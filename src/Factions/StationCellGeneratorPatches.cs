using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Game.World.Generator;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// Prevents vanilla <see cref="MyStationCellGenerator"/> from interfering with stations
    /// that <see cref="StationMaterializer"/> is managing.
    ///
    /// <list type="bullet">
    ///   <item><b>GenerateObjects prefix</b> — strips our seeds from the work list before the
    ///     vanilla loop runs, so it never tries to spawn a grid we already own.</item>
    ///   <item><b>CloseObjectSeed prefix</b> — returns false (skip vanilla) for our stations,
    ///     so the cell generator never closes a grid or safe zone we're responsible for.</item>
    /// </list>
    /// "Ours" is <see cref="StationMaterializer.IsOurs"/>: a claimed id, or any station of a
    /// registry faction. The faction half covers the claim's own lag (a founding not yet resynced,
    /// a node mid-handover), which is where vanilla's saved pins came from (FAC-025).
    /// Both patches use only the public <see cref="IMyFactionStation"/> interface to identify
    /// stations; no concrete-type access or reflection needed.
    /// </summary>
    [HarmonyPatch(typeof(MyStationCellGenerator))]
    static class StationCellGeneratorPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MyStationCellGenerator.GenerateObjects))]
        static void GenerateObjects_Prefix(List<MyObjectSeed> list)
        {
            if (SimulationGate.Deferred && !SimulationGate.Hands) return;
            list.RemoveAll(seed =>
            {
                var station = seed.UserData as IMyFactionStation;
                if (!StationMaterializer.IsOurs(station)) return false;
                if (!StationMaterializer.IsManaged(station.Id)) StationMaterializer.NoteVanillaBlock(station);
                return true;
            });
            // What survives is vanilla's to spawn: a station of a faction outside the registry, or one
            // the registry lost between resyncs. Rare enough to record each.
            if (StationMaterializer.ClaimLog == null) return;
            for (int i = 0; i < list.Count; i++)
                StationMaterializer.NoteVanillaSpawn(list[i].UserData as IMyFactionStation);
        }

        [HarmonyPrefix]
        [HarmonyPatch("CloseObjectSeed")]  // protected — cannot use nameof from outside the class
        static bool CloseObjectSeed_Prefix(MyObjectSeed objectSeed)
        {
            if (SimulationGate.Deferred && !SimulationGate.Hands) return true;
            // return false = skip vanilla; return true = let vanilla run
            return !StationMaterializer.IsOurs(objectSeed.UserData as IMyFactionStation);
        }
    }
}
