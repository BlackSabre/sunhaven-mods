﻿
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using Wish;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using System.Reflection;
using System;
using TMPro;
using System.IO;
using UnityEngine.Events;
using DG.Tweening;
using Mirror;
using UnityEngine.UI;
using QFSW.QC;


[BepInPlugin("devopsdinosaur.sunhaven.debugging", "DEBUGGING", "0.0.1")]
public class DebuggingPlugin : BaseUnityPlugin {

	private Harmony m_harmony = new Harmony("devopsdinosaur.sunhaven.debugging");
	public static ManualLogSource logger;

	private static ConfigEntry<bool> m_enable_cheats;

	private void Awake() {
		logger.LogInfo((object) "devopsdinosaur.sunhaven.debugging v0.0.1 loaded.");
		m_enable_cheats = this.Config.Bind<bool>("General", "Enable Cheats", true, "Determines whether console cheats are enabled (without that weird key combination thingy)");
		this.m_harmony.PatchAll();
		
		foreach (string key in BepInEx.Bootstrap.Chainloader.PluginInfos.Keys) {
			PluginInfo plugin_info = BepInEx.Bootstrap.Chainloader.PluginInfos[key];
			logger.LogInfo(key + " - " + plugin_info.ToString());
		}

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

	public static void list_component_types(Transform obj) {
		foreach (Component component in obj.GetComponents<Component>()) {
			logger.LogInfo(component.GetType().ToString());
		}
	}

    [HarmonyPatch(typeof(PlayerSettings), "Initialize")]
    class HarmonyPatch_PlayerSettings_Initialize {

        private static void Postfix(PlayerSettings __instance) {
            __instance.SetCheatsEnabled(true);
        }
    }

    [HarmonyPatch(typeof(Player), "RequestSleep")]
	class HarmonyPatch_Player_RequestSleep {

		private static bool Prefix(Player __instance, Bed bed, ref bool ____paused, ref UnityAction ___OnUnpausePlayer) {
			DialogueController.Instance.SetDefaultBox();
			DialogueController.Instance.PushDialogue(new DialogueNode {
				dialogueText = new List<string> { "Would you like to sleep?" },
				responses = new Dictionary<int, Response> {{
					0,
					new Response
					{
						responseText = () => "Yes",
						action = delegate {
							__instance.GetType().GetTypeInfo().GetMethod("StartSleep", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(__instance, new object[] {bed});
						}
					}
				}, {
					1,
					new Response
					{
						responseText = () => "No",
						action = delegate {
							DialogueController.Instance.CancelDialogue(animate: true, null, showActionBar: true);
						}
					}
				}
			}});
			____paused = true;
			___OnUnpausePlayer = (UnityAction) Delegate.Combine(___OnUnpausePlayer, (UnityAction) delegate {
				DialogueController.Instance.CancelDialogue();
			});
			return false;
		}
	}

	[HarmonyPatch(typeof(LiamWheat), "ReceiveDamage")]
	class HarmonyPatch_LiamWheat_ReceiveDamage {

		private static bool Prefix(ref LiamWheat __instance, ref DamageHit __result) {
			AudioManager.Instance.PlayOneShot(SingletonBehaviour<Prefabs>.Instance.cropHit, __instance.transform.position);
			UnityEngine.Object.Destroy(__instance.gameObject);
			__result = new DamageHit {
				hit = true,
				damageTaken = 1f
			};
			Pickup.Spawn(
				__instance.transform.position.x + 0.5f, 
				__instance.transform.position.y + 0.707106769f, 
				__instance.transform.position.z, 
				ItemID.Wheat
			);
			return false;
		}
	}

	[HarmonyPatch(typeof(Pickaxe), "Action")]
	class HarmonyPatch_Pickaxe_Action {

		private const float POWER_MULTIPLIER = 5f;
		private const float AOE_RANGE_MULTIPLIER = 2f;

		private static bool Prefix(
			ref Pickaxe __instance,
			ref Decoration ____currentDecoration,
			ref Player ___player,
			ref float ____power,
			ref PickaxeType ___pickaxeType,
			ref int ____breakingPower
		) {
			if (!((bool) ____currentDecoration && ____currentDecoration is Rock rock)) {
				return true;
			}
			bool is_crit = Utilities.Chance(___player.GetStat(StatType.MiningCrit));
			float damage = (is_crit ? (____power * 2f) : ____power);
			damage *= UnityEngine.Random.Range(0.75f, 1.25f);
			damage *= 1f + ___player.GetStat(StatType.MiningDamage);
			if (SceneSettingsManager.Instance.GetCurrentSceneSettings != null && SceneSettingsManager.Instance.GetCurrentSceneSettings.townType == TownType.Nelvari && ___pickaxeType == PickaxeType.Nelvari) {
				damage *= 1.3f;
			}
			if (SceneSettingsManager.Instance.GetCurrentSceneSettings != null && SceneSettingsManager.Instance.GetCurrentSceneSettings.townType == TownType.Withergate && ___pickaxeType == PickaxeType.Withergate) {
				damage *= 1.3f;
			}
			foreach (Rock rock2 in Utilities.CircleCast<Rock>(rock.Center, 3f * AOE_RANGE_MULTIPLIER)) {
				if (rock2 != null) {
					hit_rock(rock2, damage, ____breakingPower, is_crit);
				}
			}
			return true;
		}

		private static void hit_rock(Rock rock, float damage, float _breakingPower, bool is_crit) {
			Vector3 position = rock.Position + new Vector3(0.5f, -0.15f, -1f);
			ParticleManager.Instance.InstantiateParticle(((ParticleSystem) rock.GetType().GetTypeInfo().GetField("_breakParticle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rock)), position);
			((Transform) rock.GetType().GetTypeInfo().GetField("graphics", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rock)).DOScale(new Vector3(1.1f, 0.9f, 1f), 0.35f).From().SetEase(Ease.OutBounce);
			if (_breakingPower < (float) rock.GetType().GetTypeInfo().GetField("requiredPower", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rock)) {
				return;
			}
			FieldInfo current_health_info = rock.GetType().GetTypeInfo().GetField("_currentHealth", BindingFlags.Instance | BindingFlags.NonPublic);
			return;
			float current_health = (float) current_health_info.GetValue(rock) - damage;
			current_health_info.SetValue(rock, current_health);
			FieldInfo heal_tween_info = rock.GetType().GetTypeInfo().GetField("healTween", BindingFlags.Instance | BindingFlags.NonPublic);
			Tween heal_tween = (Tween) heal_tween_info.GetValue(rock);
			heal_tween.Kill();
			FloatingTextManager.Instance.SpawnFloatingDamageText((int) damage, position, DamageType.Player, is_crit);
			Slider _healthSlider = (Slider) rock.GetType().GetTypeInfo().GetField("_healthSlider", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rock);
			if ((bool) _healthSlider) {
				_healthSlider.gameObject.SetActive(value: true);
				_healthSlider.DOValue(Mathf.Clamp(current_health / (float) rock.GetType().GetTypeInfo().GetField("_health", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rock), 0f, 1f), 0.125f);
				DOVirtual.DelayedCall(20f, delegate {
					if ((bool) _healthSlider) {
						_healthSlider.gameObject.SetActive(value: false);
					}
				});
			}
			/*
				healTween = DOVirtual.DelayedCall(20f, delegate
				{
					if ((bool) _healthSlider) {
						_healthSlider.gameObject.SetActive(value: false);
					}
				});
			} else if (isHeavystone) {
				if (!SingletonBehaviour<GameSave>.Instance.GetProgressBoolCharacter("Heavystone")) {
					SingletonBehaviour<HelpTooltips>.Instance.SendNotification("Heavystone", "<color=#39CCFF>Heavystone</color> Deposits are a lot tougher than normal stone! You'll need <color=#39CCFF>a stronger tool</color> to break them!", new List<(Transform, Vector3, Direction)>(), 35, delegate
					{
						SingletonBehaviour<HelpTooltips>.Instance.CompleteNotification(35);
						SingletonBehaviour<GameSave>.Instance.SetProgressBoolCharacter("Heavystone", value: true);
					});
				}
				SingletonBehaviour<NotificationStack>.Instance.SendNotification("You'll need <color=#39CCFF>a stronger tool</color> to break <color=#39CCFF>Heavystone</color> Deposits!");
			}
			if (_currentHealth <= 0f) {
				Die(hitFromLocalPlayer, homeIn, rustyKeyDropMultiplier, brokeUsingPickaxe);
			} else if ((bool) _rockHitSound) {
				AudioManager.Instance.PlayOneShot(_rockHitSound, base.transform.position);
			}
			*/
		}
	}

	[HarmonyPatch(typeof(Rock), "Die")]
	class HarmonyPatch_Rock_Die {

		private static bool Prefix(
			ref bool hitFromLocalPlayer,
			ref bool homeIn, 
			ref float rustyKeyDropMultiplier, 
			ref bool brokeUsingPickaxe,
			ref Rock __instance,
			ref AudioClip ____rockBreakSound
		) {
			logger.LogInfo("Rock.Die - pos: " + __instance.Position);
			if ((bool) ____rockBreakSound) {
				AudioManager.Instance.PlayOneShot(____rockBreakSound, __instance.transform.position);
			}
			if (hitFromLocalPlayer) {
				__instance.GetType().GetTypeInfo().GetMethod("HandleRockDrop", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(__instance, new object[] {homeIn, rustyKeyDropMultiplier, brokeUsingPickaxe});
			}
			return false;
		}
	}

	[HarmonyPatch(typeof(ScenePortalManager), "Awake")]
	class HarmonyPatch_ScenePortalManager_Awake {

		private static bool Prefix() {
			ItemID item_id = new ItemID();
			ItemData data = null;
			foreach (FieldInfo info in typeof(ItemID).GetFields(BindingFlags.Public | BindingFlags.Static)) {
				if (!info.IsLiteral || info.IsInitOnly) {
					continue;
				}
				data = ItemDatabase.GetItemData((int) info.GetValue(item_id));
				if (data.category == ItemCategory.Craftable) {
					//logger.LogInfo(info.Name);
				}
			}
			return true;
		}
	}
}