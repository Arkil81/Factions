using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using NLog;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// SE's economy tick (<c>MySessionComponentEconomy.UpdateStations</c>, every
    /// <c>EconomyTickInSeconds</c>) spread over frames, and lazy for Factions' own stations.
    ///
    /// <para>Vanilla walks every station of every faction in one frame: station update, contract board
    /// refill, store-item generation. On 2026-10-04 that frame cost 330-560 ms every 10 minutes at
    /// 15:27:17, 15:37:27, 15:47:28 and 15:57:29 box-local (`hitch` top component
    /// <c>MySessionComponentEconomy</c>); the 16:07:29 tick profiled at 343 ms, 518 contract-board
    /// refills and 89 ms of store-item generation across 11 factions.</para>
    ///
    /// <para><b>Sliced.</b> The same calls in the same order, a few milliseconds of them per frame
    /// (<see cref="BudgetMs"/>), one station at a time.</para>
    ///
    /// <para><b>Lazy.</b> Vanilla assumes every station exists as a grid. Factions materialises a
    /// station only near a player, and nothing in Factions reads an unseen station's SE store or
    /// contracts: the store is read at a player's transaction, and contracts at a terminal. So for a
    /// faction Factions manages, a station with no live grid is skipped and left stale; its board is
    /// refilled when it materialises (<c>RefreshBoardOnSpawn</c>) and its store by
    /// <see cref="StationStoreManager"/>'s refresh. Store-item generation is skipped for those
    /// factions entirely: <see cref="FactionsPlugin.OnEconomyTickCompleted"/> clears and repopulates
    /// their stores straight after it. Factions Factions does not manage run as vanilla, sliced.</para>
    /// </summary>
    internal static class EconomyTickSlicer
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Slicer work admitted per frame; at least one station is processed whatever it costs.</summary>
        internal const double BudgetMs = 4.0;

        private delegate void CountsFn(MySessionComponentContractSystem sys,
            ref Dictionary<long, int> counts, ref Dictionary<long, List<MyContract>> lists);
        private delegate void CreateFn(MySessionComponentContractSystem sys, MyContractGenerator gen,
            MyFaction faction, IMyFactionStation station, int count, ref List<MyContract> existing);

        private static readonly Action<MySessionComponentContractSystem> CleanOld;
        private static readonly CountsFn Counts;
        private static readonly CreateFn Create;
        private static readonly Action<MyFactionStation, MyFaction> StationUpdate;
        private static readonly Action<MySessionComponentEconomy, MyFaction> Balance;
        private static readonly FieldInfo GeneratorF;
        private static readonly FieldInfo FirstGenerationF;

        internal static readonly bool Available;

        static EconomyTickSlicer()
        {
            try
            {
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                Type sys = typeof(MySessionComponentContractSystem);
                CleanOld = (Action<MySessionComponentContractSystem>)Delegate.CreateDelegate(
                    typeof(Action<MySessionComponentContractSystem>), sys.GetMethod("CleanOldContracts", inst, null, Type.EmptyTypes, null));
                Counts = (CountsFn)Delegate.CreateDelegate(typeof(CountsFn), sys.GetMethod("GetAvailableContractCountsByStation", inst));
                Create = (CreateFn)Delegate.CreateDelegate(typeof(CreateFn), sys.GetMethod("CreateContractsForStation", inst));
                StationUpdate = (Action<MyFactionStation, MyFaction>)Delegate.CreateDelegate(
                    typeof(Action<MyFactionStation, MyFaction>), typeof(MyFactionStation).GetMethod("Update", inst, null, new[] { typeof(MyFaction) }, null));
                Balance = (Action<MySessionComponentEconomy, MyFaction>)Delegate.CreateDelegate(
                    typeof(Action<MySessionComponentEconomy, MyFaction>), typeof(MySessionComponentEconomy).GetMethod("UpdateUpdateFactionBalance", inst));
                GeneratorF = typeof(MySessionComponentEconomy).GetField("m_storeItemsGenerator", inst);
                FirstGenerationF = typeof(MySessionComponentEconomy).GetField("m_stationStoreItemsFirstGeneration", inst);
                Available = GeneratorF != null && FirstGenerationF != null;
            }
            catch (Exception ex)
            {
                Log.Warn("ECONOMY_TICK slicer unavailable, vanilla runs in one frame: " + ex.Message);
                Available = false;
            }
        }

        private sealed class Pass
        {
            public MySessionComponentEconomy Economy;
            public MySessionComponentContractSystem Contracts;
            public MyContractGenerator Generator;
            public Dictionary<long, int> CountsByStation;
            public Dictionary<long, List<MyContract>> Lists;
            public List<MyFaction> Factions;
            public int Faction = -1;
            public bool Managed;
            public List<IMyFactionStation> Stations;
            public int Station;
            public long StartedTicks;
            public double WorkMs, MaxFrameMs;
            public int Frames, Live, Stale, Vanilla, StoreFactions;
        }

        private static Pass _pass;

        /// <summary>A Nexus agent ticks the slicer from its own frame. Without this a deferred server
        /// runs vanilla's one-frame pass: one that no frame ticks would never finish.</summary>
        internal static volatile bool TickedOnAgent;

        /// <summary>On a Nexus agent: whether a faction's stores are the authority's to stock, so
        /// vanilla does not generate them. Null elsewhere.</summary>
        internal static Func<string, bool> AgentManages;

        /// <summary>A pass is in progress: the postfixes on <c>UpdateStations</c> wait for its end.</summary>
        internal static bool Active { get { return _pass != null; } }

        /// <summary>Whether a station's grid exists now: what a player could be looking at.
        /// <c>StationEntityId</c> alone is not proof; it is the id the grid had.</summary>
        internal static bool IsLive(IMyFactionStation station)
        {
            if (station == null || station.StationEntityId == 0) return false;
            MyEntity e;
            if (!MyEntities.TryGetEntityById(station.StationEntityId, out e, false)) return false;
            var grid = e as MyCubeGrid;
            return grid != null && !grid.MarkedForClose && !grid.Closed;
        }

        /// <summary>The economy tick is due: begin a pass instead of vanilla's one-frame loop. False
        /// lets vanilla run.</summary>
        internal static bool Begin(MySessionComponentEconomy economy)
        {
            if (!Available || economy == null || MySession.Static == null) return false;
            if (_pass != null)
            {
                Log.Warn("ECONOMY_TICK skipped: the previous pass is still running at faction "
                         + _pass.Faction + "/" + _pass.Factions.Count);
                return true;
            }
            var contracts = MySession.Static.GetComponent<MySessionComponentContractSystem>();
            if (contracts == null || economy.EconomyDefinition == null) return false;

            long t0 = Stopwatch.GetTimestamp();
            var pass = new Pass
            {
                Economy = economy, Contracts = contracts, StartedTicks = t0,
                Generator = new MyContractGenerator(economy.EconomyDefinition),
                CountsByStation = new Dictionary<long, int>(), Lists = new Dictionary<long, List<MyContract>>(),
                Factions = new List<MyFaction>(),
            };
            CleanOld(contracts);
            Counts(contracts, ref pass.CountsByStation, ref pass.Lists);
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
                if (kv.Value != null) pass.Factions.Add(kv.Value);
            pass.WorkMs = Ms(t0);
            _pass = pass;
            return true;
        }

        /// <summary>Once per frame, on the game thread.</summary>
        internal static void Tick()
        {
            Pass pass = _pass;
            if (pass == null) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                bool any = false;
                while (!any || Ms(t0) < BudgetMs * StallBackoffScale.Value)
                {
                    any = true;
                    if (!Step(pass)) { Finish(pass, t0); return; }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ECONOMY_TICK pass abandoned at faction " + pass.Faction + "/" + pass.Factions.Count);
                _pass = null;
                return;
            }
            double ms = Ms(t0);
            pass.WorkMs += ms;
            pass.Frames++;
            if (ms > pass.MaxFrameMs) pass.MaxFrameMs = ms;
        }

        /// <summary>One unit: the next station, or the current faction's close and the next one's
        /// start. False when the pass is complete.</summary>
        private static bool Step(Pass pass)
        {
            if (pass.Stations != null && pass.Station < pass.Stations.Count)
            {
                IMyFactionStation station = pass.Stations[pass.Station++];
                RunStation(pass, pass.Factions[pass.Faction], station);
                return true;
            }
            if (pass.Stations != null) CloseFaction(pass);

            while (++pass.Faction < pass.Factions.Count)
            {
                MyFaction faction = pass.Factions[pass.Faction];
                if (MySession.Static.Factions.TryGetFactionById(faction.FactionId) != faction) continue;
                Func<string, bool> agentManages = AgentManages;
                pass.Managed = FactionContractStrategy.IsRegistered(faction.Tag)
                            || (agentManages != null && agentManages(faction.Tag));
                Balance(pass.Economy, faction);
                pass.Stations = new List<IMyFactionStation>(faction.Stations);
                pass.Station = 0;
                return true;
            }
            pass.Stations = null;
            return false;
        }

        private static void RunStation(Pass pass, MyFaction faction, IMyFactionStation station)
        {
            var concrete = station as MyFactionStation;
            if (concrete == null) return;
            if (pass.Managed)
            {
                if (!IsLive(station)) { pass.Stale++; return; }
                pass.Live++;
            }
            else pass.Vanilla++;

            StationUpdate(concrete, faction);
            List<MyContract> existing;
            int count;
            if (!pass.CountsByStation.TryGetValue(station.Id, out count))
            {
                existing = new List<MyContract>();
                Create(pass.Contracts, pass.Generator, faction, station, 0, ref existing);
                pass.Lists[station.Id] = existing;
            }
            else
            {
                if (!pass.Lists.TryGetValue(station.Id, out existing)) existing = new List<MyContract>();
                Create(pass.Contracts, pass.Generator, faction, station, count, ref existing);
            }
        }

        /// <summary>Vanilla's store-item generation for the faction just walked, for a faction Factions
        /// does not manage; as vanilla, the first-generation flag is cleared after the first faction.</summary>
        private static void CloseFaction(Pass pass)
        {
            MyFaction faction = pass.Factions[pass.Faction];
            bool first = (bool)FirstGenerationF.GetValue(pass.Economy);
            if (!pass.Managed)
            {
                var generator = GeneratorF.GetValue(pass.Economy) as MyStoreItemsGenerator;
                if (generator != null) { generator.Update(faction, first); pass.StoreFactions++; }
            }
            FirstGenerationF.SetValue(pass.Economy, false);
        }

        private static void Finish(Pass pass, long frameStart)
        {
            _pass = null;
            long r0 = Stopwatch.GetTimestamp();
            FactionsPlugin.Instance?.OnEconomyTickCompleted(true);
            double refreshMs = Ms(r0);
            double frameMs = Ms(frameStart);
            pass.WorkMs += frameMs;
            pass.Frames++;
            if (frameMs > pass.MaxFrameMs) pass.MaxFrameMs = frameMs;
            EconomyTickProfiler.LogSummaryAndReset();
            if (DebugTraces.EconomyProfiler)
                Log.Info("ECONOMY_TICK_SLICED factions=" + pass.Factions.Count + " live=" + pass.Live
                         + " stale=" + pass.Stale + " vanilla=" + pass.Vanilla + " storeFactions=" + pass.StoreFactions
                         + " frames=" + pass.Frames + " workMs=" + pass.WorkMs.ToString("F0")
                         + " maxFrameMs=" + pass.MaxFrameMs.ToString("F1") + " storeRefreshMs=" + refreshMs.ToString("F1")
                         + " wallMs=" + Ms(pass.StartedTicks).ToString("F0"));
        }

        private static double Ms(long since)
        {
            return (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>Replaces vanilla's one-frame <c>UpdateStations</c> with <see cref="EconomyTickSlicer"/>.</summary>
    [HarmonyPatch(typeof(MySessionComponentEconomy), "UpdateStations")]
    internal static class EconomyTickSlicerPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(MySessionComponentEconomy __instance)
        {
            if (SimulationGate.Deferred && !EconomyTickSlicer.TickedOnAgent) return true;
            return !EconomyTickSlicer.Begin(__instance);
        }
    }
}
