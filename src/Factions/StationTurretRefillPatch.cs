using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;    // MyCryoChamber
using Sandbox.Game.Weapons;            // MyLargeTurretBase
using Sandbox.Game.World;              // MyFactionStation

namespace Factions
{
    /// <summary>
    /// Keeps vanilla's free turret refill off a Factions station whose ammunition comes from node
    /// stock (FAC-441).
    ///
    /// <para><c>MyStationResourcesGenerator.UpdateStation</c>, run by <c>MyFactionStation.Update</c> at
    /// every spawn and on every economy tick, tops up each block in a cache built once per grid by
    /// <c>InitBlocksCache</c>: gas generators get ice, reactors fuel, batteries charge, tanks gas, and
    /// every <c>MyLargeTurretBase</c> a full inventory of ammunition. This postfix removes the turrets
    /// from that cache for a grid in <see cref="StationMaterializer.StockFedGrids"/>, and the cargo
    /// containers and cryo chambers vanilla fills with loot for a grid in
    /// <see cref="StationMaterializer.LootFedGrids"/> (<see cref="Factions.Core.StationLoot"/>); everything else stays
    /// vanilla's.</para>
    ///
    /// <para>The cache is per station, not per grid, and vanilla clears it only when handed a closed
    /// grid. <see cref="ClearResourceCache"/> is the despawn half: without it, every later spawn of the
    /// station in the same process iterates the first grid's closed blocks and refills nothing.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class StationTurretRefillPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        private static readonly PropertyInfo ResourcesGeneratorProp =
            AccessTools.Property(typeof(MyFactionStation), "ResourcesGenerator");
        private static MethodInfo _clearBlocksCache;

        internal static bool Bound;
        private static bool _outcomeLogged;

        /// <summary>A miss disables this class only and must never throw: a throw fails its whole
        /// patch group (<c>HarmonyPatcher</c>).</summary>
        [HarmonyPrepare]
        private static bool Prepare()
        {
            Bound = Target() != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (Bound)
                    Log.Info("Station munitions: turret refill patch bound.");
                else
                    Log.Error("Station munitions: MyStationResourcesGenerator.InitBlocksCache(MyCubeGrid) did not "
                            + "resolve, so vanilla refills a stock-fed station's turrets for free on top of its draw.");
            }
            return Bound;
        }

        [HarmonyTargetMethod]
        private static MethodBase Target()
        {
            System.Type generator = AccessTools.TypeByName("Sandbox.Game.World.Generator.MyStationResourcesGenerator");
            return generator == null ? null : AccessTools.Method(generator, "InitBlocksCache", new[] { typeof(MyCubeGrid) });
        }

        [HarmonyPostfix]
        private static void Postfix(MyCubeGrid grid, List<MyCubeBlock> ___m_blocksCache)
        {
            if (grid == null || ___m_blocksCache == null) return;
            if (StationMaterializer.StockFedGrids.Contains(grid.EntityId))
                ___m_blocksCache.RemoveAll(b => b is MyLargeTurretBase);
            // The generator fills every empty cargo container and cryo chamber with the station's
            // container type, at spawn and again on every economy tick: credits and tools from nowhere.
            if (StationMaterializer.LootFedGrids.Contains(grid.EntityId))
                ___m_blocksCache.RemoveAll(b => b is MyCargoContainer || b is MyCryoChamber);
        }

        /// <summary>Drops the station's cached block list so its next spawn builds a fresh one from
        /// the new grid. Returns false when the reflection did not resolve.</summary>
        internal static bool ClearResourceCache(MyFactionStation station)
        {
            if (station == null || ResourcesGeneratorProp == null) return false;
            object generator = ResourcesGeneratorProp.GetValue(station);
            if (generator == null) return false;
            if (_clearBlocksCache == null) _clearBlocksCache = AccessTools.Method(generator.GetType(), "ClearBlocksCache");
            if (_clearBlocksCache == null) return false;
            _clearBlocksCache.Invoke(generator, null);
            return true;
        }
    }
}
