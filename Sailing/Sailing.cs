using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using SkillManager;
using UnityEngine;

namespace Sailing;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class Sailing : BaseUnityPlugin
{
	private const string ModName = "Sailing";
	private const string ModVersion = "1.1.1";
	private const string ModGUID = "org.bepinex.plugins.sailing";

	private static readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<float> explorationRadiusIncrease = null!;
	private static ConfigEntry<float> shipHealthIncrease = null!;
	private static ConfigEntry<float> experienceGainedFactor = null!;

	private static readonly Dictionary<string, ConfigEntry<float>> shipSpeedIncrease = new();
	private static readonly Dictionary<string, ConfigEntry<int>> shipPaddleRequirement = new();
	private static readonly Dictionary<string, ConfigEntry<int>> shipHalfRequirement = new();
	private static readonly Dictionary<string, ConfigEntry<int>> shipFullRequirement = new();

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0
	}

	private class ConfigurationManagerAttributes
	{
		[UsedImplicitly] public bool? ShowRangeAsPercent;
		[UsedImplicitly] public int? Order;
	}

	private static Skill sailing = null!;

	private static Sailing mod = null!;

	public void Awake()
	{
		sailing = new Skill("Sailing", "sailing.png");
		sailing.Description.English("Increases the health of ships built by you, sailing speed of ships commanded by you and your exploration radius while on a ship.");
		sailing.Name.German("Segeln");
		sailing.Description.German("Erhöht die Lebenspunkte von dir gebauter Schiffe, erhöht die Geschwindigkeiten von Schiffen, die du steuerst und erhöht deinen Erkundungsradius, wenn du dich auf einem Schiff befindest.");
		sailing.Configurable = false;

		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		explorationRadiusIncrease = config("3 - Other", "Exploration Radius Factor", 5f, new ConfigDescription("Exploration radius factor while on ships at skill level 100.", new AcceptableValueRange<float>(1f, 10f)));
		shipHealthIncrease = config("3 - Other", "Ship Health Factor", 2f, new ConfigDescription("Health factor for ships at skill level 100.", new AcceptableValueRange<float>(1f, 10f)));
		experienceGainedFactor = config("3 - Other", "Skill Experience Gain Factor", 1f, new ConfigDescription("Factor for experience gained for the sailing skill.", new AcceptableValueRange<float>(0.01f, 5f)));
		experienceGainedFactor.SettingChanged += (_, _) => sailing.SkillGainFactor = experienceGainedFactor.Value;
		sailing.SkillGainFactor = experienceGainedFactor.Value;

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);

		mod = this;
	}

	[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.OnPlaced))]
	private class AddZDO
	{
		[UsedImplicitly]
		private static void Postfix(WearNTear __instance)
		{
			if (__instance.GetComponent<Ship>())
			{
				__instance.GetComponent<ZNetView>().GetZDO().Set("Sailing Skill Level", Player.m_localPlayer.GetSkillFactor("Sailing"));
				__instance.m_health *= 1 + __instance.GetComponent<ZNetView>().GetZDO().GetFloat("Sailing Skill Level") * shipHealthIncrease.Value;
				Player.m_localPlayer.RaiseSkill("Sailing", 35f);
			}
		}
	}

	[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
	private class FetchShipPiecesObjectDB
	{
		[HarmonyPriority(Priority.Last)]
		private static void Postfix(ObjectDB __instance)
		{
			if (__instance.GetItemPrefab("Hammer")?.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces is { } pieces)
			{
				FetchShipPieces(pieces.m_pieces);
			}
		}
	}

	[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
	private class FetchShipPiecesZNetScene
	{
		[HarmonyPriority(Priority.Last)]
		private static void Postfix(ZNetScene __instance)
		{
			FetchShipPieces(__instance.m_prefabs);
		}
	}

	private static void FetchShipPieces(IEnumerable<GameObject> prefabs)
	{
		Localization english = new();
		english.SetupLanguage("English");

		Regex regex = new("['[\"\\]]");

		List<string> shipOrder = new() { "$ship_longship", "$ship_karve", "$ship_raft" };
		foreach (Ship ship in prefabs.Select(p => p.GetComponent<Ship>()).Where(s => s != null).OrderByDescending(s => shipOrder.IndexOf(s.GetComponent<Piece>().m_name)))
		{
			int order = -shipSpeedIncrease.Count * 4;

			string pieceName = ship.GetComponent<Piece>().m_name;

			if (shipSpeedIncrease.ContainsKey(pieceName))
			{
				return;
			}

			shipSpeedIncrease[pieceName] = mod.config("2 - Ship Speed", $"{regex.Replace(english.Localize(pieceName), "")} Speed Factor", 1.5f, new ConfigDescription($"Speed factor for {english.Localize(pieceName)} at skill level 100.", new AcceptableValueRange<float>(1f, 3f), new ConfigurationManagerAttributes { Order = --order }));
			shipPaddleRequirement[pieceName] = mod.config("2 - Ship Speed", $"{regex.Replace(english.Localize(pieceName), "")} Paddle Requirement", 0, new ConfigDescription($"Required sailing skill level to be able to paddle a {english.Localize(pieceName)}.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = --order, ShowRangeAsPercent = false }));
			shipHalfRequirement[pieceName] = mod.config("2 - Ship Speed", $"{regex.Replace(english.Localize(pieceName), "")} Half Requirement", 0, new ConfigDescription($"Required sailing skill level to be able to sail a {english.Localize(pieceName)} with reduced sail.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = --order, ShowRangeAsPercent = false }));
			shipFullRequirement[pieceName] = mod.config("2 - Ship Speed", $"{regex.Replace(english.Localize(pieceName), "")} Full Requirement", 0, new ConfigDescription($"Required sailing skill level to be able to sail a {english.Localize(pieceName)} with full sail.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = --order, ShowRangeAsPercent = false }));
		}
	}

	[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Awake))]
	private class IncreaseHealth
	{
		[UsedImplicitly]
		private static void Prefix(WearNTear __instance)
		{
			__instance.m_health *= 1 + (__instance.GetComponent<ZNetView>().GetZDO()?.GetFloat("Sailing Skill Level") ?? (__instance.GetComponent<Ship>() ? Player.m_localPlayer.GetSkillFactor("Sailing") : 1)) * shipHealthIncrease.Value;
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Explore), typeof(Vector3), typeof(float))]
	private class IncreaseExplorationRadius
	{
		[UsedImplicitly]
		private static void Prefix(Minimap __instance, ref float radius)
		{
			if (Player.m_localPlayer is { m_attached: true, m_attachedToShip: true } player)
			{
				radius *= 1 + player.GetSkillFactor("Sailing") * explorationRadiusIncrease.Value;
			}
		}
	}

	[HarmonyPatch(typeof(Ship), nameof(Ship.GetSailForce))]
	private class ChangeShipSpeed
	{
		[UsedImplicitly]
		private class Timer
		{
			public float UpdateDelta = 0;
			public float lastUpdate = Time.fixedTime;
		}

		private static readonly ConditionalWeakTable<Ship, Timer> timers = new();

		private static void Postfix(Ship __instance, ref Vector3 __result)
		{
			Timer timer = timers.GetOrCreateValue(__instance);
			if (Player.m_players.FirstOrDefault(p => p.GetZDOID() == __instance.m_shipControlls.GetUser()) is { } sailor)
			{
				if (shipSpeedIncrease.TryGetValue(__instance.GetComponent<Piece>().m_name, out ConfigEntry<float> speedIncrease))
				{
					__result *= 1 + sailor.m_nview.GetZDO().GetFloat("Sailing Skill") * speedIncrease.Value;
				}

				if (__instance.m_speed is not Ship.Speed.Stop)
				{
					timer.UpdateDelta += Time.fixedTime - timer.lastUpdate;
					if (timer.UpdateDelta > 1)
					{
						sailor.m_nview.InvokeRPC("Sailing Skill Increase", 1);
						timer.UpdateDelta -= 1;
					}
				}
			}
			timer.lastUpdate = Time.fixedTime;
		}
	}

	[HarmonyPatch(typeof(Ship), nameof(Ship.Forward))]
	private class PreventShipSpeeds
	{
		private static bool Prefix(Ship __instance)
		{
			if (__instance.m_speed is Ship.Speed.Slow && shipHalfRequirement.TryGetValue(__instance.GetComponent<Piece>().m_name, out ConfigEntry<int> requiredHalfLevel) && requiredHalfLevel.Value > Mathf.RoundToInt(Player.m_localPlayer.GetSkillFactor("Sailing") * 100))
			{
				Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Your sailing skill is too low to sail this ship with reduced sail.");

				return false;
			}

			if (__instance.m_speed is Ship.Speed.Half && shipFullRequirement.TryGetValue(__instance.GetComponent<Piece>().m_name, out ConfigEntry<int> requiredFullLevel) && requiredFullLevel.Value > Mathf.RoundToInt(Player.m_localPlayer.GetSkillFactor("Sailing") * 100))
			{
				Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Your sailing skill is too low to sail this ship with full sail.");

				return false;
			}

			return true;
		}
	}

	[HarmonyPatch(typeof(ShipControlls), nameof(ShipControlls.Interact))]
	private class BlockSailing
	{
		private static bool Prefix(ShipControlls __instance)
		{
			if (!shipPaddleRequirement.TryGetValue(__instance.m_ship.GetComponent<Piece>().m_name, out ConfigEntry<int> requiredPaddleLevel) || requiredPaddleLevel.Value <= Mathf.RoundToInt(Player.m_localPlayer.GetSkillFactor("Sailing") * 100))
			{
				return true;
			}

			Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Your sailing skill is too low to command this ship.");
			return false;
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Awake))]
	private class ExposeSailingSkill
	{
		private static void Postfix(Player __instance)
		{
			__instance.m_nview.Register("Sailing Skill Increase", (long _, int amount) => __instance.RaiseSkill("Sailing", amount));
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Update))]
	public class PlayerUpdate
	{
		private static void Postfix(Player __instance)
		{
			if (__instance == Player.m_localPlayer)
			{
				__instance.m_nview.GetZDO().Set("Sailing Skill", __instance.GetSkillFactor("Sailing"));
			}
		}
	}
}
