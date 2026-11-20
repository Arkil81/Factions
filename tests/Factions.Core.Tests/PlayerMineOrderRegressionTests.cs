using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-395 slice 2b moved NPC mining onto the standing-order loop a player's orders
    /// already ran on, and split that loop's catchment walk out to share it. A player's orders must
    /// behave exactly as before. This is a golden transcript of three passes over one estate --
    /// overlapping catchments, a worked deposit, a cooling one, doctrine and non-doctrine hulls,
    /// weighted and unweighted orders, dwells resolving between passes -- captured from the code
    /// before the split (integrate/npc-extraction @ 7489a379) and asserted line for line, under
    /// both the synchronous tick and the staged cycle.</summary>
    public sealed class PlayerMineOrderRegressionTests
    {
        private sealed class Catalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, double> Drill = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, long> Cargo = new Dictionary<string, long>(StringComparer.Ordinal);
            public long CargoLiters(string prefabId)
            {
                long v;
                return prefabId != null && Cargo.TryGetValue(prefabId, out v) ? v : 0;
            }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) => new Dictionary<string, long>();
            public long Cost(string prefabId) => 0;
            public string FactionHint(string prefabId) => "All";
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) => new WeaponBand[0];
            public string GridSize(string prefabId) => "Small";
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) => new string[0];
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
                => new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            public double DryMassKg(string prefabId) => 0;
            public double DrillYieldScore(string prefabId)
            {
                double v;
                return prefabId != null && Drill.TryGetValue(prefabId, out v) ? v : 0;
            }
        }

        private sealed class Volumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string subTypeId) => subTypeId == "Ore:Ice" ? 0.37 : 1.0;
        }

        private static SpatialNode Node(WorldModel world, string id, double x)
        {
            SpatialNode n = world.Graph.Add(new Vec3(x, 0, 0), 1.0, id);
            n.OwnerFactionTag = "P";
            n.HoldStrength = 1.0;
            return n;
        }

        private static SpatialNode Deposit(WorldModel world, string id, double x, double richness, params string[] ores)
        {
            SpatialNode n = Node(world, id, x);
            n.Richness = richness;
            n.Deposit = new NodeDeposit();
            foreach (string ore in ores)
                n.Deposit.Entries.Add(new DepositEntry(ore, 1.0 / ores.Length, 100.0 / ores.Length));
            return n;
        }

        private static WorldModel Estate(out FactionState st)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            var cat = new Catalogue();
            cat.Drill["Slow"] = 1;   cat.Cargo["Slow"] = 20_000;
            cat.Drill["Fast"] = 100; cat.Cargo["Fast"] = 50_000;
            cat.Drill["Doc"] = 10;   cat.Cargo["Doc"] = 30_000;
            world.PrefabCatalogue = cat;

            st = new FactionState { Tag = "P", IsPlayerFaction = true };
            StandingOrderFixtures.AddDoctrineLine(st, "Doc", targetCount: 2);

            Node(world, "HubA", 0);
            Node(world, "HubB", 300).ShipStock["Doc"] = 1;
            Node(world, "Yard", 50).ShipStock["Fast"] = 2;
            Deposit(world, "D1", 10, 1.0, "Iron").ShipStock["Slow"] = 2;
            Deposit(world, "D2", 20, 0.8, "Iron", "Nickel");
            Deposit(world, "D3", 290, 1.0, "Ice");
            Deposit(world, "D4", 150, 0.6, "Iron");
            Deposit(world, "D5", 900, 1.0, "Iron");                       // outside both catchments
            world.Graph.Nodes["D2"].MineCooldownUntilUtc = world.UtcNow.AddMinutes(4);

            StandingOrderFixtures.AddMineOrder(st, "HubA", 60, world.UtcNow, priority: 2);
            StandingOrderFixtures.AddMineOrder(st, "HubB", 60, world.UtcNow,
                new Dictionary<string, double> { { "Ore:Iron", 100 } }, priority: 1);
            return world;
        }

        /// <summary>Runs the scenario and returns its transcript: every Mine log line, then every
        /// Mine fleet's shape, parked stock and ship demands, after each of three passes.</summary>
        private static List<string> Run(bool staged)
        {
            FactionState st;
            WorldModel world = Estate(out st);
            var states = new List<FactionState> { st };
            var mgr = new OperationsManager(itemVolumes: new Volumes());
            var transcript = new List<string>();

            MineDispatchOutcome outcome;
            var oneShotLog = new List<string>();
            PlayerMineResult oneShot = mgr.TriggerPlayerMine(world, world.Tuning, st, "D4", "HubA", null, oneShotLog, out outcome);
            transcript.Add("oneshot " + oneShot + " " + outcome);
            transcript.AddRange(oneShotLog.Where(l => l.Contains("MINE")).Select(StripLogId));

            for (int pass = 1; pass <= 3; pass++)
            {
                var log = new List<string>();
                if (staged) RunStagedSweep(mgr, states, world, log);
                else mgr.Tick(states, world, world.Tuning, log);

                transcript.Add("-- pass " + pass + " @" + world.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                transcript.AddRange(log.Where(l => l.Contains("MINE")).Select(StripLogId));
                // A fleet id carries a random component, so fleets are listed by their own shape.
                transcript.AddRange(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine).Select(Describe)
                    .OrderBy(d => d, StringComparer.Ordinal));
                foreach (SpatialNode n in world.Graph.Nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal))
                {
                    string stock = string.Join(",", n.ShipStock.Where(kv => kv.Value > 0)
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "x" + kv.Value));
                    string inv = string.Join(",", n.Inventory.Where(kv => kv.Value > 0)
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "x" + kv.Value));
                    transcript.Add("node " + n.Id + " ships=[" + stock + "] inv=[" + inv + "] cool="
                        + n.MineCooldownUntilUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                }
                foreach (DemandEntry d in world.Demand.SnapshotShipDemands("P")
                             .OrderBy(d => d.NodeId, StringComparer.Ordinal).ThenBy(d => d.Kind, StringComparer.Ordinal))
                    transcript.Add("demand " + d.Kind + " at=" + d.NodeId + " qty=" + d.Quantity);

                world.UtcNow = world.UtcNow.AddMinutes(3);
                PendingFleetResolver.ResolveArrivals(states, world, world.Tuning, new SeededRandom(1),
                    new List<string>(), itemVolumes: new Volumes());
            }
            return transcript;
        }

        /// <summary>Drops the leading <c>[fleet id]</c> a dispatch line carries; the id is random.</summary>
        private static string StripLogId(string line)
        {
            return System.Text.RegularExpressions.Regex.Replace(line, @"^\[[0-9A-F]+\] ", "");
        }

        private static string Describe(PendingFleetAction f)
        {
            var inv = CultureInfo.InvariantCulture;
            string stops = f.RemainingStops == null ? "" : string.Join(" ", f.RemainingStops.Select(s =>
                s.Kind + "@" + s.NodeId
                + (s.Kind == FleetStopKind.Mine
                    ? "/" + s.MineTargetLiters.ToString("0", inv) + "L/" + s.DurationSeconds.ToString("0.###", inv) + "s/"
                      + string.Join("+", s.MineWeights.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                          .Select(kv => kv.Key + "=" + kv.Value.ToString("0.###", inv)))
                    : "")));
            string weights = f.MineWeights == null ? "-" : string.Join("+", f.MineWeights
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value.ToString("0.###", inv)));
            string cargo = f.Cargo == null ? "-" : string.Join(",", f.Cargo
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "x" + kv.Value));
            return "fleet " + f.ShipPrefabId + " " + f.OriginNodeId + "->" + f.TargetNodeId
                + " " + f.CurrentStopKind + " target=" + f.MineTargetLiters.ToString("0", inv) + "L w=" + weights
                + " eta=" + f.EstimatedArrivalUtc.ToString("HH:mm:ss.fff", inv)
                + " cargo=" + cargo + " resolved=" + f.Resolved + " [" + stops + "]";
        }

        [Fact]
        public void PlayerStandingOrders_AreUnchanged_UnderTheSynchronousTick()
        {
            AssertGolden(RunInvariant(staged: false));
        }

        [Fact]
        public void PlayerStandingOrders_AreUnchanged_UnderTheStagedCycle()
        {
            AssertGolden(RunInvariant(staged: true));
        }

        /// <summary>The Mine log lines format their clock times with the current culture's time
        /// separator (a Finnish host writes <c>12.04.00</c>), so the transcript is captured under the
        /// invariant culture the golden was recorded in.</summary>
        private static List<string> RunInvariant(bool staged)
        {
            CultureInfo prior = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try { return Run(staged); }
            finally { CultureInfo.CurrentCulture = prior; }
        }

        private static void AssertGolden(List<string> actual)
        {
            Assert.Equal(Golden, actual);
        }

        private static readonly string[] Golden =
        {
            "oneshot Ok Dispatched",
            "P MINE_DISPATCHED D4 -> HubA via_pickup=HubB target=30000L dwell=80s eta_pickup=12:00:24 prefab=Doc drillRate=125.7",
            "-- pass 1 @12:00:00",
            "P MINE_DISPATCHED D1 -> HubA via_pickup=Yard target=50000L dwell=8s eta_pickup=12:00:11 prefab=Fast drillRate=1256.6",
            "P MINE_DISPATCHED D3 -> HubA via_pickup=Yard target=50000L dwell=8s eta_pickup=12:00:35 prefab=Fast drillRate=1256.6",
            "P MINE_AUTO_SCAN order=SO-001 hub=HubA rank=2 catchment=4 inflight=1 open=3 dispatched=2 [D1:dispatched D3:dispatched D2:cooldown_until=12:04:00 D4:working]",
            "P MINE_AUTO_SCAN order=SO-002 hub=HubB rank=1 catchment=4 inflight=0 open=4 dispatched=0 [D1:claimed_by_SO-001 D2:cooldown_until=12:04:00 D4:working D3:claimed_by_SO-001]",
            "fleet Doc HubB->D4 Navigation target=0L w=- eta=12:00:24.380 cargo=- resolved=False [Mine@D4/30000L/79.578s/Ore:Iron=0.5+Ore:Stone=0.5 Service@HubA]",
            "fleet Fast Yard->D1 Navigation target=0L w=- eta=12:00:11.050 cargo=- resolved=False [Mine@D1/50000L/7.958s/Ore:Iron=0.5+Ore:Stone=0.5 Service@HubA]",
            "fleet Fast Yard->D3 Navigation target=0L w=- eta=12:00:35.180 cargo=- resolved=False [Mine@D3/50000L/7.958s/Ore:Ice=0.5+Ore:Stone=0.5 Service@HubA]",
            "node D1 ships=[Slowx2] inv=[] cool=00:00:00",
            "node D2 ships=[] inv=[] cool=12:04:00",
            "node D3 ships=[] inv=[] cool=00:00:00",
            "node D4 ships=[] inv=[] cool=00:00:00",
            "node D5 ships=[] inv=[] cool=00:00:00",
            "node HubA ships=[] inv=[] cool=00:00:00",
            "node HubB ships=[] inv=[] cool=00:00:00",
            "node Yard ships=[] inv=[] cool=00:00:00",
            "-- pass 2 @12:03:00",
            "P MINE_AUTO_SCAN order=SO-001 hub=HubA rank=2 catchment=4 inflight=3 open=1 dispatched=0 [D1:working D3:working D2:cooldown_until=12:04:00 D4:working]",
            "P MINE_AUTO_SCAN order=SO-002 hub=HubB rank=1 catchment=4 inflight=0 open=4 dispatched=0 [D1:working D2:cooldown_until=12:04:00 D4:working D3:working]",
            "fleet Doc D4->D4 Mine target=30000L w=Ore:Iron=0.5+Ore:Stone=0.5 eta=12:04:19.577 cargo=- resolved=False [Service@HubA]",
            "fleet Fast D1->D1 Mine target=50000L w=Ore:Iron=0.5+Ore:Stone=0.5 eta=12:03:07.957 cargo=- resolved=False [Service@HubA]",
            "fleet Fast D3->D3 Mine target=50000L w=Ore:Ice=0.5+Ore:Stone=0.5 eta=12:03:07.957 cargo=- resolved=False [Service@HubA]",
            "node D1 ships=[Slowx2] inv=[] cool=00:00:00",
            "node D2 ships=[] inv=[] cool=12:04:00",
            "node D3 ships=[] inv=[] cool=00:00:00",
            "node D4 ships=[] inv=[] cool=00:00:00",
            "node D5 ships=[] inv=[] cool=00:00:00",
            "node HubA ships=[] inv=[] cool=00:00:00",
            "node HubB ships=[] inv=[] cool=00:00:00",
            "node Yard ships=[] inv=[] cool=00:00:00",
            "-- pass 3 @12:06:00",
            "P MINE_DISPATCHED D2 -> HubA via_pickup=D1 target=20000L dwell=442s eta_pickup=12:06:05 prefab=Slow drillRate=12.6",
            "P MINE_AUTO_SCAN order=SO-001 hub=HubA rank=2 catchment=4 inflight=3 open=1 dispatched=1 [D1:cooldown_until=12:06:07 D3:cooldown_until=12:06:07 D2:dispatched remaining=no_open_slots]",
            "P MINE_AUTO_SCAN order=SO-002 hub=HubB rank=1 catchment=4 inflight=0 open=4 dispatched=0 [D1:cooldown_until=12:06:07 D2:claimed_by_SO-001 D4:cooldown_until=12:07:19 D3:invalid_weights]",
            "fleet Doc D4->HubA Service target=0L w=- eta=12:06:24.724 cargo=Ore:Ironx15000,Ore:Stonex15000 resolved=False []",
            "fleet Fast D1->HubA Service target=0L w=- eta=12:06:09.656 cargo=Ore:Ironx25000,Ore:Stonex25000 resolved=False []",
            "fleet Fast D3->HubA Service target=0L w=- eta=12:06:41.356 cargo=Ore:Icex67567,Ore:Stonex25000 resolved=False []",
            "fleet Slow D1->D2 Navigation target=0L w=- eta=12:06:05.461 cargo=- resolved=False [Mine@D2/20000L/442.098s/Ore:Iron=0.25+Ore:Nickel=0.25+Ore:Stone=0.5 Service@HubA]",
            "node D1 ships=[Slowx1] inv=[] cool=12:06:07",
            "node D2 ships=[] inv=[] cool=12:04:00",
            "node D3 ships=[] inv=[] cool=12:06:07",
            "node D4 ships=[] inv=[] cool=12:07:19",
            "node D5 ships=[] inv=[] cool=00:00:00",
            "node HubA ships=[] inv=[] cool=00:00:00",
            "node HubB ships=[] inv=[] cool=00:00:00",
            "node Yard ships=[] inv=[] cool=00:00:00",
        };

        private static void RunStagedSweep(OperationsManager manager, IList<FactionState> states, WorldModel world,
            List<string> log)
        {
            Invoke(manager, "BeginSweepForTesting", states);
            int steps = 0;
            while (!(bool)Invoke(manager, "StepSweepOnceForTesting", states, world, world.Tuning, log))
                Assert.True(++steps < 1000, "Operations sweep did not complete");
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.Invoke(target, arguments);
        }
    }
}
