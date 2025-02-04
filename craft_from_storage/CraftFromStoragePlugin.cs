﻿using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using Wish;
using System.Collections.Generic;
using UnityEngine;
using System.Reflection;
using TMPro;
using System;
using UnityEngine.Events;
using System.Threading;
using UnityEngine.UI;


[BepInPlugin("devopsdinosaur.sunhaven.craft_from_storage", "Craft From Storage", "0.0.16")]
public class CraftFromStoragePlugin : BaseUnityPlugin {

	private Harmony m_harmony = new Harmony("devopsdinosaur.sunhaven.craft_from_storage");
	public static ManualLogSource logger;
	
	private static ConfigEntry<bool> m_enabled;
	private static ConfigEntry<bool> m_use_inventory_first;
	private static ConfigEntry<bool> m_transfer_from_action_bar;

	private void Awake() {
		logger = this.Logger;
		try {
			m_enabled = this.Config.Bind<bool>("General", "Enabled", true, "Set to false to disable this mod.");
			m_use_inventory_first = this.Config.Bind<bool>("General", "Use Inventory First", true, "If true then crafting stations will pull from inventory before storage chests.");
			m_transfer_from_action_bar = this.Config.Bind<bool>("General", "Transfer From Action Bar", false, "If true then the transfer similar/same buttons will also pull from the action bar.");
			if (m_enabled.Value) {
				this.m_harmony.PatchAll();
			}
			logger.LogInfo("devopsdinosaur.sunhaven.craft_from_storage v0.0.16" + (m_enabled.Value ? "" : " [inactive; disabled in config]") + " loaded.");
		} catch (Exception e) {
			logger.LogError("** Awake FATAL - " + e);
		}
	}

	public class OmniChest {

		private static OmniChest m_instance = null;
		private List<int> m_added_hashes = null;
		private Dictionary<int, List<SlotItemData>> m_items = null;
		private List<Inventory> m_inventories = null;
		private List<Chest> m_chests = null;
		private Chest m_current_chest = null;
		private const float CHECK_FREQUENCY = 1.0f;
		private float m_elapsed = CHECK_FREQUENCY;
		private GameObject m_transfer_similar_button = null;
		private GameObject[] m_chest_navigate_buttons = new GameObject[2];
		private Mutex m_thread_lock = new Mutex();
		private const int TEMPLATE_LEFT_ARROW_BUTTON = 0;
		private const int TEMPLATE_RIGHT_ARROW_BUTTON = 1;
		private static Dictionary<int, GameObject> m_object_templates = new Dictionary<int, GameObject>();

		public static OmniChest Instance {
			get {
				return (m_instance != null ? m_instance : m_instance = new OmniChest());
			}
		}

		private OmniChest() {
		}

		private void get_thread_lock() {
			this.m_thread_lock.WaitOne();
		}

		private void release_thread_lock() {
			this.m_thread_lock.ReleaseMutex();
		}

		public static bool list_descendants(Transform parent, Func<Transform, bool> callback, int indent) {
			Transform child;
			string indent_string = "";
			for (int counter = 0; counter < indent; counter++) {
				indent_string += " => ";
			}
			for (int index = 0; index < parent.childCount; index++) {
				child = parent.GetChild(index);
				logger.LogInfo(indent_string + child.gameObject.name);
				if (callback != null) {
					if (callback(child) == false) {
						return false;
					}
				}
				list_descendants(child, callback, indent + 1);
			}
			return true;
		}

		public static bool enum_descendants(Transform parent, Func<Transform, bool> callback) {
			Transform child;
			for (int index = 0; index < parent.childCount; index++) {
				child = parent.GetChild(index);
				if (callback != null) {
					if (callback(child) == false) {
						return false;
					}
				}
				enum_descendants(child, callback);
			}
			return true;
		}

		[HarmonyPatch(typeof(PlayerSettings), "SetupUI")]
		class HarmonyPatch_PlayerSettings_SetupUI {

			private static void Postfix(Slider ___daySpeedSlider) {

				GameObject templatize(GameObject original) {
					GameObject obj = GameObject.Instantiate(original, null);
					obj.SetActive(false);
					GameObject.DontDestroyOnLoad(obj);
					return obj;
				}

				bool find_buttons_callback(Transform transform) {
					if (transform.name == "SliderLeft") {
						m_object_templates[TEMPLATE_LEFT_ARROW_BUTTON] = templatize(transform.gameObject);
					} else if (transform.name == "SliderRight") {
						m_object_templates[TEMPLATE_RIGHT_ARROW_BUTTON] = templatize(transform.gameObject);
					}
					return !(m_object_templates.ContainsKey(TEMPLATE_LEFT_ARROW_BUTTON) && m_object_templates.ContainsKey(TEMPLATE_RIGHT_ARROW_BUTTON));
				}

				if (m_object_templates.ContainsKey(TEMPLATE_LEFT_ARROW_BUTTON) && m_object_templates.ContainsKey(TEMPLATE_RIGHT_ARROW_BUTTON)) {
					return;
				}
				enum_descendants(___daySpeedSlider.transform, find_buttons_callback);
			}
		}

		[HarmonyPatch(typeof(Chest), "Interact")]
		class HarmonyPatch_Chest_Interact {

			private static void Postfix(Chest __instance) {
				OmniChest.Instance.get_thread_lock();
				OmniChest.Instance.m_current_chest = __instance;
				OmniChest.Instance.release_thread_lock();
			}
		}

		private void create_send_similar_button(Transform chest_transform, Inventory player_inventory) {
			GameObject chest_transfer_similar_button = null;
			GameObject sort_button = null;
			
			bool __enum_descendants_callback_find_same_button__(Transform transform) {
				if (transform.name == "TransferSimilarToChestButton") {
					chest_transfer_similar_button = transform.gameObject;
					return false;
				} 
				return true;
			}

			bool __enum_descendants_callback_find_sort_button__(Transform transform) {
				if (sort_button == null && transform.name == "SortButton") {
					sort_button = transform.gameObject; 
					return false;
				}
				return true;
			}

			enum_descendants(chest_transform, __enum_descendants_callback_find_same_button__);
			enum_descendants(player_inventory.transform, __enum_descendants_callback_find_sort_button__);
			this.m_transfer_similar_button = GameObject.Instantiate<GameObject>(chest_transfer_similar_button, sort_button.transform.parent);
			this.m_transfer_similar_button.GetComponent<RectTransform>().position =
				sort_button.GetComponent<RectTransform>().position +
				Vector3.right * sort_button.GetComponent<RectTransform>().rect.width * 3;
			this.m_transfer_similar_button.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(delegate {
				this.transfer_similar_items();
			});
		}

		public void transfer_similar_items() {
			this.refresh();
			this.get_thread_lock();
			try {
				Inventory player_inventory = Player.Instance.Inventory;
				Inventory inventory;
				for (int index = (m_use_inventory_first.Value ? 1 : 0); index < (!m_use_inventory_first.Value ? this.m_inventories.Count - 1 : this.m_inventories.Count); index++) {
					try {
						inventory = this.m_inventories[index];
						player_inventory.TransferPlayerSimilarToOtherInventory(inventory);
						inventory.UpdateInventory();
						player_inventory.UpdateInventory();
					} catch {
					}
				}
			} catch (Exception e) {
				logger.LogError("** transfer_similar_items ERROR - " + e);
			}
			this.release_thread_lock();
			this.refresh();
		}

		public void refresh() {
			this.get_thread_lock();
			try {
				if (!m_enabled.Value || (m_elapsed += Time.fixedDeltaTime) < CHECK_FREQUENCY) {
					return;
				}
				Inventory player_inventory = Player.Instance.Inventory;
				m_elapsed = 0f;
				int hash = 0;
				this.m_added_hashes = new List<int>();
				this.m_items = new Dictionary<int, List<SlotItemData>>();
				this.m_inventories = new List<Inventory>();
				this.m_chests = new List<Chest>();
				if (m_use_inventory_first.Value) {
					this.add_inventory(player_inventory);
				}
				foreach (KeyValuePair<Vector3Int, Decoration> kvp in GameManager.Instance.objects) {
					if (!(kvp.Value is Chest) || this.m_added_hashes.Contains(hash = kvp.Value.GetHashCode())) {
						continue;
					}
					string type_name = kvp.Value.GetType().Name;
					if (!(type_name == "Chest" || type_name == "AutoCollector" || type_name == "Hopper")) {
						continue;
					}
					if (this.m_transfer_similar_button == null) {
						this.create_send_similar_button(kvp.Value.transform, player_inventory);
					}
					this.m_added_hashes.Add(hash);
					this.add_inventory(((Chest) kvp.Value).sellingInventory);
					this.add_chest((Chest) kvp.Value);
				}
				if (!m_use_inventory_first.Value) {
					this.add_inventory(player_inventory);
				}
				Popup popup = this.m_transfer_similar_button.GetComponent<Popup>();
				popup.text = "Zone Send Similar";
				popup.description = "Send similar items to nearby chests within the current zone (note: house and outside are different zones).\nNearby chests: " + Math.Max(this.m_inventories.Count - 1, 0);
			} catch {
			}
			this.release_thread_lock();
		}

		private void add_chest(Chest chest) {
			GameObject chest_title = null;

			bool __enum_descendants_callback_find_chest_title__(Transform transform) {
				if (transform.name == "ExternalInventoryTitle") {
					chest_title = transform.GetChild(0).gameObject;
					return false;
				}
				return true;
			}

			void create_navigation_button(int template_id, string name, Vector3 direction) {
				for (int index = 0; index < chest_title.transform.parent.childCount; index++) {
					if (chest_title.transform.parent.GetChild(index).name == name) {
						return;
					}
				}
				GameObject obj = GameObject.Instantiate<GameObject>(m_object_templates[template_id], chest_title.transform.parent);
				obj.name = name;
				obj.SetActive(true);
				RectTransform chest_title_rect = chest_title.GetComponent<RectTransform>();
				RectTransform obj_rect = obj.GetComponent<RectTransform>();
				obj_rect.localScale = new Vector3(1.5f, 1.5f, 1f);
				obj_rect.localPosition = chest_title_rect.localPosition + direction * ((chest_title_rect.rect.width / 2) + (obj_rect.rect.width / 2) + 5f);
				obj.GetComponent<UnityEngine.UI.Button>().onClick.AddListener((UnityAction) delegate {
					OmniChest.Instance.navigate_from_chest(direction);
				});
			}

			this.m_chests.Add(chest);
			FieldInfo field_info = chest.GetType().GetField("ui", BindingFlags.Instance | BindingFlags.NonPublic);
			GameObject ui = null;
			if (field_info == null || (ui = (GameObject) field_info.GetValue(chest)) == null) {
				logger.LogWarning("** add_chest WARN - chest '" + chest.name + "' has no 'ui' field; unable to create navigation buttons.");
				return;
			}
			enum_descendants(ui.transform, __enum_descendants_callback_find_chest_title__);
			if (chest_title == null) {
				logger.LogWarning("** add_chest WARN - unable to locate chest title object for '" + chest.name + "'; cannot create navigation buttons.");
				return;
			}
			create_navigation_button(TEMPLATE_LEFT_ARROW_BUTTON, "CraftFromStorage_NavigateButtonLeft", Vector3.left);
			create_navigation_button(TEMPLATE_RIGHT_ARROW_BUTTON, "CraftFromStorage_NavigateButtonRight", Vector3.right);
		}

		private void navigate_from_chest(Vector3 direction) {
			this.get_thread_lock();
			try {
				for (int index = 0; index < this.m_chests.Count; index++) {
					if (this.m_chests[index].GetHashCode() != this.m_current_chest.GetHashCode()) {
						continue;
					}
					this.m_current_chest.EndInteract((int) InteractionType.Both);
					this.m_chests[
						(direction == Vector3.left ?
							(index == 0 ?
								this.m_chests.Count - 1 : 
								index - 1
							) :
							(index == this.m_chests.Count - 1 ?
								0 :
								index + 1
							)
						)
					].Interact((int) InteractionType.Both);
					break;
				}
			} catch (Exception e) {
				logger.LogError("** navigate_from_chest ERROR - " + e);
			}
			this.release_thread_lock();
		}

		private void add_inventory(Inventory inventory) {
			try {
				this.m_inventories.Add(inventory);
				foreach (SlotItemData item in inventory.Items) {
					if (!this.m_items.ContainsKey(item.id)) {
						this.m_items[item.id] = new List<SlotItemData>();
					}
					this.m_items[item.id].Add(item);
				}
			} catch (Exception) {
			}
		}

		public int get_fish_rarity_amount(ItemRarity rarity) {
            int amount = 0;
			foreach (int id in this.m_items.Keys) {
				foreach (SlotItemData item in this.m_items[id]) {
					if (item.item is FishItem fish_item && ItemDatabase.GetItemData(fish_item.ID()).rarity.Equals(rarity)) {
						amount += item.amount;
					}
				}
			}
			return amount;
        }

		public int get_item_amount(int id) {
			int amount = 0;
			switch (id) {
			case 60000:	return GameSave.Coins;
			case 60001:	return GameSave.Orbs;
			case 60002:	return GameSave.Tickets;
			case 60200:	return this.get_fish_rarity_amount(ItemRarity.Common);
			case 60201:	return this.get_fish_rarity_amount(ItemRarity.Uncommon);
			case 60202:	return this.get_fish_rarity_amount(ItemRarity.Rare);
			case 60203:	return this.get_fish_rarity_amount(ItemRarity.Epic);
			case 60204:	return this.get_fish_rarity_amount(ItemRarity.Legendary);
			}
			if (this.m_items.ContainsKey(id)) {
				foreach (SlotItemData item in this.m_items[id]) {
					amount += item.amount;
				}
			}	
			return amount;
		}

		public bool can_craft(Recipe recipe, int amount) {
			foreach (ItemInfo item in recipe.Input) {
				if (item.item.id == ItemID.Mana) {
					if (Player.Instance.Mana <= (float) recipe.ModifiedAmount(item.amount,item.item,recipe.output.item) * amount) {
						return false;
					}
				} else if (item.item.id == ItemID.Health) {
					if (Player.Instance.Health <= (float) recipe.ModifiedAmount(item.amount,item.item,recipe.output.item) * amount) {
						return false;
					}
				} else if (this.get_item_amount(item.item.id) < amount) {
					return false;
				}
			}
			return true;
		}

		public List<ItemAmount> remove_item(int id, int amount = 1, int slot = 0) {
			List<ItemAmount> items = new List<ItemAmount>();

			// note: this function assumes can_craft() was first called, so it
			// does not do any checks for item existence.
			foreach (SlotItemData data in this.m_items[id]) {
				foreach (ItemAmount item_amount in data.slot.inventory.RemoveItem(id, amount)) {
					amount -= item_amount.amount;
					items.Add(item_amount);
				}
				if (amount <= 0) {
					break;
				}
			}
			return items;
		}

		public List<ItemAmount> remove_fish(ItemRarity rarity, int amount = 1) {
            List<ItemAmount> items = new List<ItemAmount>();

			foreach (int id in this.m_items.Keys) {
                foreach (SlotItemData item in this.m_items[id]) {
                    if (item.item is FishItem fish_item && ItemDatabase.GetItemData(fish_item.ID()).rarity.Equals(rarity)) {
                        foreach (ItemAmount item_amount in item.slot.inventory.RemoveItem(id, amount)) {
                            amount -= item_amount.amount;
                            items.Add(item_amount);
                        }
                        if (amount <= 0) {
                            return items;
                        }
                    }
                }
            }
			return items;
        }
	}

	[HarmonyPatch(typeof(CraftingTable), "CanCraft")]
	class HarmonyPatch_CraftingTable_CanCraft {

		private static bool Prefix(Recipe recipe, int amount, Inventory inventory, ref bool __result) {
			__result = OmniChest.Instance.can_craft(recipe, amount);
			return false;
		}
	}

	[HarmonyPatch(typeof(Player), "FixedUpdate")]
	class HarmonyPatch_Player_FixedUpdate {

		private static bool Prefix(Player __instance) {
			if (__instance.IsOwner) {
				OmniChest.Instance.refresh();
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(ScenePortalSpot), "OnTriggerEnter2D")]
	class HarmonyPatch_ScenePortalSpot_OnTriggerEnter2D {

		private static void Postfix() {
			OmniChest.Instance.refresh();
		}
	}

	[HarmonyPatch(typeof(CraftingPanel), "InitializeInput")]
	class HarmonyPatch_CraftingPanel_InitializeInput {

		private static bool Prefix(Recipe recipe, ItemImage itemImage, int index) {
			if (!m_enabled.Value) {
				return true;
			}
			itemImage.Initialize(ItemDatabase.GetItemData(recipe.Input[index].item.id).GetItem());
			int has_amount = 0;
			switch (recipe.Input[index].item.id) {
			case ItemID.Mana:	{ has_amount = (int) Player.Instance.Mana; break; }
			case ItemID.Health:	{ has_amount = (int) Player.Instance.Health; break; }
			default:			{ has_amount = OmniChest.Instance.get_item_amount(recipe.Input[index].item.id); break; }
			}
			int required_amount = recipe.ModifiedAmount(recipe.Input[index].amount, recipe.Input[index].item, recipe.output.item);
			itemImage.SetDisabled(has_amount < required_amount);
			itemImage.SetAmount(has_amount.FormatWithCommas() + "/" + required_amount.FormatWithCommas(), required_amount);
			return false;
		}
	}

	[HarmonyPatch(typeof(CraftingPanel), "UpdateCraftingButtons")]
	class HarmonyPatch_CraftingPanel_UpdateCraftingButtons {

		private static bool Prefix(
			ref Recipe ___recipe,
			ref UnityEngine.UI.Button ___buyButton,
			ref TextMeshProUGUI ___buyButtonTMP,
			ref UnityEngine.UI.Button ___buy5Button,
			ref TextMeshProUGUI ___buy5ButtonTMP,
			ref UnityEngine.UI.Button ___buy20Button,
			ref TextMeshProUGUI ___buy20ButtonTMP
		) {
			if (!m_enabled.Value) {
				return true;
			}
			int num = 999;
			foreach (ItemInfo item in ___recipe.Input) {
				int num2;
				if (item.item.id == ItemID.Mana) {
					num2 = (int) Player.Instance.Mana / ___recipe.ModifiedAmount(item.amount, item.item, ___recipe.output.item);
				} else if (item.item.id == ItemID.Health) {
					num2 = (int) Player.Instance.Health / ___recipe.ModifiedAmount(item.amount, item.item, ___recipe.output.item);
				} else {
					num2 = OmniChest.Instance.get_item_amount(item.item.id) / ___recipe.ModifiedAmount(item.amount, item.item, ___recipe.output.item);
				}
				if (num2 < num) {
					num = num2;
				}
			}
			Color color = new Color(1f, 1f, 1f, 28f / 51f);
			if (num < 1) {
				___buyButton.interactable = false;
				___buyButtonTMP.color = color;
				___buyButtonTMP.text = "x 1";
			} else {
				___buyButton.interactable = true;
				___buyButtonTMP.color = Color.white;
				___buyButtonTMP.text = "x <color=#FEE463>1";
			}
			if (num < 5) {
				___buy5Button.interactable = false;
				___buy5ButtonTMP.color = color;
				___buy5ButtonTMP.text = "x 5";
			} else {
				___buy5Button.interactable = true;
				___buy5ButtonTMP.color = Color.white;
				___buy5ButtonTMP.text = "x <color=#FEE463>5";
			}
			if (num < 20) {
				___buy20Button.interactable = false;
				___buy20ButtonTMP.color = color;
				___buy20ButtonTMP.text = "x 20";
			} else {
				___buy20Button.interactable = true;
				___buy20ButtonTMP.color = Color.white;
				___buy20ButtonTMP.text = "x <color=#FEE463>20";
			}
			return false;
		}
	}

	[HarmonyPatch(typeof(CraftingTable), "Craft")]
	class HarmonyPatch_CraftingTable_Craft {

		protected static float TimeFromDate(DateTime date) {
			return (float) (date.Minute + date.Hour * 60 + date.Day * 24 * 60) + (float) date.Second / 60f + (float) (date.Year * 28 * 24 * 60);
		}

		private static bool Prefix(
			ref CraftingTable __instance,
			Recipe recipe,
			int amount,
			CraftingTableData ___craftingData,
			ref float ___queueTime,
			float ___craftSpeedMultiplier
		) {
			if (!m_enabled.Value) {
				return true;
			}
			if (!OmniChest.Instance.can_craft(recipe, amount)) {
				return false;
			}
			if (___craftingData.items.Count <= 0) {
				___queueTime = 0f;
			}
			if (___queueTime <= 0f) {
				___craftingData.timeStart = TimeFromDate(SingletonBehaviour<DayCycle>.Instance.Time);
			}
			for (int i = 0; i < amount; i++) {
				List<ItemAmount> list = new List<ItemAmount>();
				foreach (ItemInfo item2 in recipe.Input) {
					if (item2.item.id == ItemID.Mana) {
						Player.Instance.UseMana(recipe.ModifiedAmount(item2.amount, item2.item, recipe.output.item));
					} else if (item2.item.id == ItemID.Health) {
                        Player.Instance.Health -= recipe.ModifiedAmount(item2.amount, item2.item, recipe.output.item);
                    } else {
                        List<ItemAmount> collection;
						switch (item2.item.id) {
						case 60200: { collection = OmniChest.Instance.remove_fish(ItemRarity.Common, item2.amount); break; }
						case 60201: { collection = OmniChest.Instance.remove_fish(ItemRarity.Uncommon, item2.amount); break; }
						case 60202: { collection = OmniChest.Instance.remove_fish(ItemRarity.Rare, item2.amount); break; }
						case 60203: { collection = OmniChest.Instance.remove_fish(ItemRarity.Epic, item2.amount); break; }
						case 60204: { collection = OmniChest.Instance.remove_fish(ItemRarity.Legendary, item2.amount); break; }
						default:	{ collection = OmniChest.Instance.remove_item(item2.item.id, recipe.ModifiedAmount(item2.amount, item2.item, recipe.output.item)); break; }
						}
						list.AddRange(collection);
					}
				}
				Item item = recipe.output.item.GenerateItem(list);
				float multiplier = ___craftSpeedMultiplier * ((GameSave.CurrentCharacter.race == (int) Race.Human) ? 1.2f : 1f);
				float craftTime = recipe.GetHoursToCraft(multiplier) * Settings.DaySpeedMultiplier;
				ItemCraftInfo itemCraftInfo = new ItemCraftInfo {
					item = item,
					craftTime = craftTime,
					amount = recipe.output.amount,
					input = list
				};
				___craftingData.items.Add(itemCraftInfo);
				___queueTime += itemCraftInfo.craftTime;
			}
			recipe.Craft();
			__instance.GetType().GetTypeInfo().GetMethod("SetupCraftingQueue", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(__instance, new object[] {});
			if (recipe is SkillTomeRecipe) {
				__instance.Initialize();
			} else {
				__instance.Refresh();
			}
			__instance.SaveMeta();
			__instance.SendNewMeta(__instance.meta);
			return false;
		}
	}

	[HarmonyPatch(typeof(Inventory), "TransferPlayerSimilarToOtherInventory")]
	class HarmonyPatch_Inventory_TransferPlayerSimilarToOtherInventory {

		private static bool Prefix(ref Inventory __instance, Inventory otherInventory) {
			if (m_enabled.Value && m_transfer_from_action_bar.Value) {
				__instance.TransferSimilarToOtherInventory(otherInventory);
				return false;
			}
			return true;
		}
	}
}