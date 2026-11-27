using System;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Names the GPS waypoint of an item-delivery contract (Acquisition, Haul, vanilla
    /// ObtainAndDeliver) after its destination station and item, instead of vanilla's single
    /// localised name shared by every such contract. The contract screen shows only the issuing
    /// faction, so this marker is the one place a player learns where the delivery goes.
    ///
    /// <para><c>MyContract.ShareConditionWithPlayerDeliverItems</c> is private and builds the
    /// <c>MyGps</c> inline, so the prefix replaces it for a delivery condition whose station resolves,
    /// building the same marker with the new name and description. Anything unresolved, or any
    /// throw, runs vanilla.</para>
    ///
    /// <para><see cref="WouldCreateGpsHashPatch"/> keeps SE's shared-marker bookkeeping consistent:
    /// when one contract ends, SE hands its marker to another active contract that would have
    /// created an identical one (same name and coordinates) and deletes it otherwise. That check
    /// hashes vanilla's name, so it is taught the new one.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyContract), "ShareConditionWithPlayerDeliverItems")]
    static class ContractDeliveryGpsPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private static readonly MethodInfo s_resolveTarget =
            AccessTools.Method(typeof(MyContract), "ResolveConditionTarget");
        private static readonly MethodInfo s_computeHash =
            AccessTools.Method(typeof(MyContract), "ComputeGpsHash");

        [HarmonyPrefix]
        static bool Prefix(MyContract __instance, long __0, IMyContractCondition __1, bool __2, ref bool __result)
        {
            try
            {
                string name, description;
                Vector3D target;
                if (!TryDescribe(__1, out name, out description, out target)) return true;

                MyGps gps = new MyGps();
                gps.DisplayName = name;
                gps.Name = name;
                gps.Description = description;
                gps.Coords = target;
                gps.ShowOnHud = true;
                gps.DiscardAt = null;
                gps.GPSColor = Color.DarkOrange;
                gps.ContractId = __instance.Id;
                MySession.Static.Gpss.SendAddGpsRequest(__0, ref gps, 0L, __2);
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Contract delivery waypoint fell back to the vanilla name.");
                return true;
            }
        }

        /// <summary>The marker's name, description and position for a delivery condition, or false
        /// when it is not one or its station cannot be named.</summary>
        internal static bool TryDescribe(IMyContractCondition condition, out string name, out string description, out Vector3D target)
        {
            name = null; description = null; target = Vector3D.Zero;
            MyContractConditionDeliverItems items = condition as MyContractConditionDeliverItems;
            if (items == null || s_resolveTarget == null || MySession.Static == null) return false;

            MyFaction faction = MySession.Static.Factions.TryGetFactionById(items.FactionEndId) as MyFaction;
            IMyFactionStation station = faction != null ? faction.GetStationById(items.StationEndId) : null;
            if (station == null) return false;

            string place = ContractWaypoint.Place(NodeName(items.StationEndId), GridName(station), faction.Tag);
            MyPhysicalItemDefinition itemDef = MyDefinitionManager.Static.GetPhysicalItemDefinition(items.ItemType);
            string itemName = itemDef != null ? itemDef.DisplayNameText : items.ItemType.SubtypeName;

            name = ContractWaypoint.Name(place, itemName);
            if (name == null) return false;
            description = ContractWaypoint.Description(place, faction != null ? faction.Name : null, items.ItemAmount, itemName);

            object[] args = { condition, Vector3D.Zero, 0L };
            s_resolveTarget.Invoke(null, args);
            target = (Vector3D)args[1];
            return true;
        }

        /// <summary>SE's hash of a marker with this name at this position, as its shared-marker check
        /// computes it; null when the method could not be bound.</summary>
        internal static int? Hash(string name, Vector3D coords)
        {
            if (s_computeHash == null) return null;
            return (int)s_computeHash.Invoke(null, new object[] { name, coords, 0L });
        }

        private static string NodeName(long stationId)
        {
            if (SimulationGate.Deferred) return null;
            FactionRuntime runtime = FactionsPlugin.Instance != null ? FactionsPlugin.Instance.Runtime : null;
            if (runtime == null || runtime.World == null) return null;
            return PlayerScanner.NodeName(TerminalAccess.FindNodeByStation(runtime, stationId));
        }

        private static string GridName(IMyFactionStation station)
        {
            if (station.StationEntityId == 0) return null;
            MyCubeGrid grid = MyEntities.GetEntityById(station.StationEntityId) as MyCubeGrid;
            return grid != null ? grid.DisplayName : null;
        }
    }

    /// <summary>See <see cref="ContractDeliveryGpsPatch"/>: SE asks every other active contract of a
    /// player whether it would create the marker being released, and only recognises vanilla's name.
    /// A match on the per-contract name keeps the marker alive for the contract still needing it.</summary>
    [HarmonyPatch(typeof(MyContract), "WouldCreateGpsWithHash")]
    static class WouldCreateGpsHashPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        [HarmonyPostfix]
        static void Postfix(MyContract __instance, int __0, ref bool __result)
        {
            if (__result) return;
            try
            {
                string name, description;
                Vector3D target;
                if (!ContractDeliveryGpsPatch.TryDescribe(__instance.ContractCondition, out name, out description, out target)) return;
                int? hash = ContractDeliveryGpsPatch.Hash(name, target);
                if (hash.HasValue && hash.Value == __0) __result = true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Contract delivery waypoint hash check fell back to vanilla.");
            }
        }
    }
}
