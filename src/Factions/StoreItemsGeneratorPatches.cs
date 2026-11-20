using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Skips vanilla's per-station store-item generation work for any station with no live grid —
    /// mirrors <see cref="FactionContractStrategy"/>'s <c>CanBeGenerated</c> spawn-state gate for the
    /// same reason (docs/todo.md's "Contract generation burst" entry): <c>faction.Stations</c> holds
    /// every station Factions has ever founded regardless of spawn state, and vanilla's own
    /// per-<c>EconomyTick</c> <c>MySessionComponentEconomy.UpdateStations</c> loop calls
    /// <c>MyStoreItemsGenerator.Update</c> for every one of them unconditionally, on the same blocking
    /// single-threaded call as the contract-generation burst already fixed this session.
    ///
    /// <para><b>Correction (2026-08-09):</b> the original version of this patch targeted
    /// <c>MyFactionTypeBaseStrategy.UpdateStationsStoreItems(MyFactionStation, int, int)</c> — a
    /// <c>protected virtual</c> hook whose base-class body is an empty stub. <c>MyStoreItemsGenerator</c>'s
    /// constructor always instantiates the *concrete* type <c>MyGenericFactionTypeStrategy</c> (the only
    /// strategy class vanilla ever registers), whose override calls <c>base.UpdateStationsStoreItems</c>
    /// (the empty stub — patching it skips nothing) and then its own <c>GenerateGridOffers</c>, a
    /// completely separate method the old patch never touched. Worse, the actual expensive work —
    /// <c>MyFactionTypeBaseStrategy.GenerateNewStoreItems</c> (wraps <c>GenerateOffers</c>/
    /// <c>GenerateOrders</c>: item-candidate scans, <c>MyDefinitionManager</c> lookups, <c>MyRandom</c>,
    /// list allocations) — is called directly from <c>UpdateStationsStoreItems(MyFaction, bool)</c>,
    /// never through the patched virtual hook at all. Net effect: the shipped patch loaded cleanly and
    /// changed nothing about the real per-station cost, which is why the felt freeze persisted unchanged.
    /// Retargeted at the two actual per-station methods below, both <c>private</c> (no virtual dispatch
    /// ambiguity) and both taking a leading <c>MyFactionStation station</c> parameter that Harmony binds
    /// a shared prefix to by name.</para>
    ///
    /// <para><see cref="TargetMethods"/> resolves both types by name at runtime (same
    /// <c>AccessTools.TypeByName</c> idiom <see cref="StationFounder"/>/<see cref="StationMaterializer"/>
    /// already use for other internal SE types) since neither is nameable with <c>typeof()</c> from this
    /// assembly — auto-discovered by the attribute pass (<c>HarmonyPatcher.PatchAll</c>) in
    /// <see cref="FactionsPlugin"/>, no new bootstrap wiring needed.</para>
    ///
    /// <para>No faction-registration check needed: vanilla's own default NPC Trade stations are
    /// spawned eagerly at world generation, not lazily materialized — Cornerstone II
    /// (<see cref="StationMaterializer"/>) is exclusively a Factions-side mechanism, so a genuinely
    /// vanilla-only station's <c>StationEntityId</c> is already always nonzero and this gate is a
    /// no-op for it; it only actually changes behavior for Factions' own lazily-spawned stations.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class StoreItemsGeneratorPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type baseStrategyType = AccessTools.TypeByName("Sandbox.Game.World.Generator.MyFactionTypeBaseStrategy");
            Type genericStrategyType = AccessTools.TypeByName("Sandbox.Game.World.Generator.MyGenericFactionTypeStrategy");

            MethodBase generateNewStoreItems = baseStrategyType == null
                ? null
                : AccessTools.Method(baseStrategyType, "GenerateNewStoreItems",
                    new[] { typeof(MyFactionStation), typeof(int), typeof(int), typeof(long), typeof(bool), typeof(bool), typeof(bool) });
            if (generateNewStoreItems != null)
                yield return generateNewStoreItems;

            MethodBase generateGridOffers = genericStrategyType == null
                ? null
                : AccessTools.Method(genericStrategyType, "GenerateGridOffers",
                    new[] { typeof(MyFactionStation), typeof(int) });
            if (generateGridOffers != null)
                yield return generateGridOffers;
        }

        [HarmonyPrefix]
        private static bool Prefix(MyFactionStation station)
        {
            if (SimulationGate.Deferred) return true;
            // return false = skip vanilla; return true = let vanilla run
            return station == null || station.StationEntityId != 0;
        }
    }
}
