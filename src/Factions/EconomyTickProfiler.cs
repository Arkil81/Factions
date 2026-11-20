using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using NLog;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Per-tick timing breakdown of vanilla's <c>MySessionComponentEconomy.UpdateStations()</c> —
    /// the synchronous game-thread call live-measured (Keen log "Balance change" per-faction stripes,
    /// 2026-08-09) at ~15 s per economy tick on this world's ~1,800 faction stations, i.e. the
    /// periodic client-facing freeze. Every per-station call in the loop reads as microseconds in
    /// decompiled source, yet three factions' whole blocks cost ~0 ms while others cost seconds —
    /// the expense is concentrated in something only some factions trigger. This profiler times each
    /// sub-call inside the loop and logs one aggregate line per tick so the next tick names the
    /// culprit directly instead of by elimination.
    ///
    /// <para>Measurement only — no behavior change. Each target gets a prefix stamping
    /// <c>Stopwatch.GetTimestamp()</c> into <c>__state</c> and a postfix accumulating
    /// count/total/max into a static slot; the <c>UpdateStations</c> postfix emits the summary and
    /// resets. All on the game thread (the whole call chain is synchronous), so plain statics are
    /// safe. The <c>UpdateStationsStoreItems(MyFaction, bool)</c> target is the base-class body,
    /// which IS the executing body for the only registered strategy (MyGenericFactionTypeStrategy
    /// overrides only the 3-arg per-station hook — same virtual-dispatch trap fixed in 3d4de36,
    /// checked before choosing this target).</para>
    ///
    /// <para>The summary line is behind <c>factions debug econtick</c> and off by default. The
    /// timing is collected either way — the Harmony patches are installed at boot and cannot be
    /// armed per-toggle — so the toggle decides only whether the tick writes its line, and the
    /// first line after arming it describes the tick it was armed for, not the backlog.</para>
    /// </summary>
    internal static class EconomyTickProfiler
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        internal struct Slot
        {
            public int Count;
            public long TotalTicks;
            public long MaxTicks;

            public void Record(long elapsed)
            {
                if (SimulationGate.Deferred) return;
                Count++;
                TotalTicks += elapsed;
                if (elapsed > MaxTicks) MaxTicks = elapsed;
            }

            public void Reset() { Count = 0; TotalTicks = 0; MaxTicks = 0; }

            public string Fmt(double freq)
            {
                if (Count == 0) return "0x";
                double total = TotalTicks / freq * 1000.0;
                double max   = MaxTicks   / freq * 1000.0;
                return Count + "x " + total.ToString("0") + "ms max=" + max.ToString("0.0") + "ms";
            }
        }

        internal static Slot StationUpdate;      // MyFactionStation.Update (resources refill; spawned grids only do real work)
        internal static Slot CreateContracts;    // MySessionComponentContractSystem.CreateContractsForStation
        internal static Slot StoreItemsFaction;  // MyFactionTypeBaseStrategy.UpdateStationsStoreItems(MyFaction, bool) — per faction
        internal static Slot GenerateNewItems;   // MyFactionTypeBaseStrategy.GenerateNewStoreItems — spawned stations only (patch-gated)
        internal static Slot CleanOld;           // MySessionComponentContractSystem.CleanOldContracts
        internal static Slot GetCounts;          // MySessionComponentContractSystem.GetAvailableContractCountsByStation
        internal static Slot CanBeGen;           // FactionContractStrategy.CanBeGenerated (recorded from our own code)
        internal static Slot OurGenerate;        // FactionContractStrategy.GenerateContract (recorded from our own code)
        internal static Slot ContractChance;     // MySessionComponentContractSystem.GetContractChance
        internal static long UpdateStationsStartTicks;
        internal static int Gc0Start, Gc1Start, Gc2Start;
        internal static long GcMemStart;
        internal static int SlowCreateCalls;     // createContracts calls that individually exceeded the slow threshold
        internal static int SlowCreateGcs;       // GC collections (any gen) observed across those slow calls specifically

        /// <summary>Writes the tick's breakdown when <see cref="DebugTraces.EconomyProfiler"/> is on,
        /// and resets the slots either way. The reset is outside the gate: the slots accumulate on
        /// every patched call regardless of the toggle, so skipping it would carry one tick's totals
        /// into the next and make the first line after the toggle is armed a sum of everything since
        /// the last one.</summary>
        internal static void LogSummaryAndReset()
        {
            if (DebugTraces.EconomyProfiler)
            {
                double freq = Stopwatch.Frequency;
                double totalMs = (Stopwatch.GetTimestamp() - UpdateStationsStartTicks) / freq * 1000.0;
                int gc0 = GC.CollectionCount(0) - Gc0Start;
                int gc1 = GC.CollectionCount(1) - Gc1Start;
                int gc2 = GC.CollectionCount(2) - Gc2Start;
                double allocMb = (GC.GetTotalMemory(false) - GcMemStart) / (1024.0 * 1024.0);
                Log.Info("EconomyTickProfiler: UpdateStations=" + totalMs.ToString("0") + "ms"
                    + " gc0=+" + gc0 + " gc1=+" + gc1 + " gc2=+" + gc2
                    + " heapDelta=" + allocMb.ToString("0") + "MB"
                    + " slowCreateCalls=" + SlowCreateCalls + " gcsDuringSlowCalls=" + SlowCreateGcs
                    + " | cleanOld " + CleanOld.Fmt(freq)
                    + " | getCounts " + GetCounts.Fmt(freq)
                    + " | stationUpdate " + StationUpdate.Fmt(freq)
                    + " | createContracts " + CreateContracts.Fmt(freq)
                    + " | storeItemsPerFaction " + StoreItemsFaction.Fmt(freq)
                    + " | generateNewItems " + GenerateNewItems.Fmt(freq)
                    + " | canBeGenerated " + CanBeGen.Fmt(freq)
                    + " | ourGenerateContract " + OurGenerate.Fmt(freq)
                    + " | getContractChance " + ContractChance.Fmt(freq));
            }

            StationUpdate.Reset(); CreateContracts.Reset(); StoreItemsFaction.Reset();
            GenerateNewItems.Reset(); CleanOld.Reset(); GetCounts.Reset();
            CanBeGen.Reset(); OurGenerate.Reset(); ContractChance.Reset();
            SlowCreateCalls = 0; SlowCreateGcs = 0;
        }
    }

    [HarmonyPatch(typeof(MySessionComponentEconomy), "UpdateStations")]
    [PatchGroup("EconomyTickProfiler")]
    internal static class EconomyTickProfiler_UpdateStations
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            EconomyTickProfiler.UpdateStationsStartTicks = Stopwatch.GetTimestamp();
            EconomyTickProfiler.Gc0Start = GC.CollectionCount(0);
            EconomyTickProfiler.Gc1Start = GC.CollectionCount(1);
            EconomyTickProfiler.Gc2Start = GC.CollectionCount(2);
            EconomyTickProfiler.GcMemStart = GC.GetTotalMemory(false);
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            // A sliced pass logs it at its end (EconomyTickSlicer.Finish).
            if (EconomyTickSlicer.Active) return;
            EconomyTickProfiler.LogSummaryAndReset();
        }
    }

    [HarmonyPatch(typeof(MyFactionStation), "Update")]
    [PatchGroup("EconomyTickProfiler")]
    internal static class EconomyTickProfiler_StationUpdate
    {
        [HarmonyPrefix]
        private static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

        [HarmonyPostfix]
        private static void Postfix(long __state)
        {
            EconomyTickProfiler.StationUpdate.Record(Stopwatch.GetTimestamp() - __state);
        }
    }

    [HarmonyPatch(typeof(MySessionComponentContractSystem), "CreateContractsForStation")]
    [PatchGroup("EconomyTickProfiler")]
    internal static class EconomyTickProfiler_CreateContracts
    {
        internal struct CallState
        {
            public long T0;
            public int GcAll;
        }

        [HarmonyPrefix]
        private static void Prefix(out CallState __state)
        {
            __state.T0 = Stopwatch.GetTimestamp();
            __state.GcAll = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        }

        [HarmonyPostfix]
        private static void Postfix(CallState __state)
        {
            long elapsed = Stopwatch.GetTimestamp() - __state.T0;
            EconomyTickProfiler.CreateContracts.Record(elapsed);
            double ms = elapsed / (double)Stopwatch.Frequency * 1000.0;
            if (ms >= 50.0)
            {
                EconomyTickProfiler.SlowCreateCalls++;
                EconomyTickProfiler.SlowCreateGcs +=
                    GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2) - __state.GcAll;
            }
        }
    }

    [HarmonyPatch(typeof(MySessionComponentContractSystem), "CleanOldContracts")]
    [PatchGroup("EconomyTickProfiler")]
    internal static class EconomyTickProfiler_CleanOld
    {
        [HarmonyPrefix]
        private static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

        [HarmonyPostfix]
        private static void Postfix(long __state)
        {
            EconomyTickProfiler.CleanOld.Record(Stopwatch.GetTimestamp() - __state);
        }
    }

    [HarmonyPatch(typeof(MySessionComponentContractSystem), "GetAvailableContractCountsByStation")]
    [PatchGroup("EconomyTickProfiler")]
    internal static class EconomyTickProfiler_GetCounts
    {
        [HarmonyPrefix]
        private static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

        [HarmonyPostfix]
        private static void Postfix(long __state)
        {
            EconomyTickProfiler.GetCounts.Record(Stopwatch.GetTimestamp() - __state);
        }
    }

    /// <summary>Targets resolved by name at runtime — the strategy types are internal to
    /// Sandbox.Game (same idiom as <see cref="StoreItemsGeneratorPatches"/>).</summary>
    [HarmonyPatch]
    [PatchGroup("EconomyTickProfiler")]
    internal static class EconomyTickProfiler_StoreStrategy
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type baseStrategyType = AccessTools.TypeByName("Sandbox.Game.World.Generator.MyFactionTypeBaseStrategy");
            if (baseStrategyType == null) yield break;

            MethodBase perFaction = AccessTools.Method(baseStrategyType, "UpdateStationsStoreItems",
                new[] { typeof(MyFaction), typeof(bool) });
            if (perFaction != null)
                yield return perFaction;
        }

        [HarmonyPrefix]
        private static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

        [HarmonyPostfix]
        private static void Postfix(long __state)
        {
            EconomyTickProfiler.StoreItemsFaction.Record(Stopwatch.GetTimestamp() - __state);
        }
    }

    [HarmonyPatch(typeof(MySessionComponentContractSystem), "GetContractChance")]
    [PatchGroup("EconomyTickProfiler")]
    internal static class EconomyTickProfiler_ContractChance
    {
        [HarmonyPrefix]
        private static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

        [HarmonyPostfix]
        private static void Postfix(long __state)
        {
            EconomyTickProfiler.ContractChance.Record(Stopwatch.GetTimestamp() - __state);
        }
    }

    [HarmonyPatch]
    [PatchGroup("EconomyTickProfiler")]
    internal static class EconomyTickProfiler_GenerateNewItems
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type baseStrategyType = AccessTools.TypeByName("Sandbox.Game.World.Generator.MyFactionTypeBaseStrategy");
            if (baseStrategyType == null) yield break;

            MethodBase generate = AccessTools.Method(baseStrategyType, "GenerateNewStoreItems",
                new[] { typeof(MyFactionStation), typeof(int), typeof(int), typeof(long), typeof(bool), typeof(bool), typeof(bool) });
            if (generate != null)
                yield return generate;
        }

        [HarmonyPrefix]
        private static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

        [HarmonyPostfix]
        private static void Postfix(long __state)
        {
            EconomyTickProfiler.GenerateNewItems.Record(Stopwatch.GetTimestamp() - __state);
        }
    }
}
