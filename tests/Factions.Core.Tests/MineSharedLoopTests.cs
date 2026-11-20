using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-395 slices 1 and 2b: the Mine loop an NPC's extraction orders and a player's
    /// standing orders share -- in-flight netting, one trip per deposit at a time, and the fastest
    /// idle drill at dispatch. The build-side rank (drill rate per cost) is in
    /// <see cref="ShipyardManagerTests"/>; the planner wiring and the walk's trace tokens in
    /// <see cref="ExtractionOrderDispatchTests"/>.</summary>
    public sealed class MineSharedLoopTests
    {
        /// <summary>Per-prefab drill score and hold. Any prefab with a drill score is Mine-suitable;
        /// every hull carries 1,000,000 L so no trip is hold-clamped unless a test says so.</summary>
        private sealed class Catalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, double> Drill = new Dictionary<string, double>(StringComparer.Ordinal);
            public long CargoLiters(string prefabId) => 1_000_000;
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
            public double VolumeLitersPerUnit(string subTypeId) => 1.0;
        }

        private static WorldModel MakeWorld(out Catalogue cat)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            cat = new Catalogue();
            cat.Drill["Slow"] = 1;
            cat.Drill["Fast"] = 100;
            cat.Drill["Doc"] = 10;
            world.PrefabCatalogue = cat;
            return world;
        }

        private static SpatialNode Node(WorldModel world, string id, Vec3 pos)
        {
            SpatialNode n = world.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            return n;
        }

        private static SpatialNode Deposit(WorldModel world, string id, Vec3 pos)
        {
            SpatialNode n = Node(world, id, pos);
            n.Richness = 1.0;
            n.Deposit = new NodeDeposit();
            n.Deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
            return n;
        }

        /// <summary>An NPC hub drawing iron at <paramref name="litersPerPass"/> per Operations pass
        /// (180 s, so x20 an hour): the extraction planner opens an order there whose target rate is
        /// that draw less the catchment's passive mint. The default is far beyond any passive mint
        /// and any one trip; 100,000 (2 ML/h) clears two deposits' passive mint and is met by one
        /// Fast trip (~22 ML/h on iron).</summary>
        private static void Want(WorldModel world, string nodeId, double litersPerPass = 1e9)
        {
            world.Graph.Nodes[nodeId].DemandHistory["Ore:Iron"] = litersPerPass;
        }

        private static List<string> Tick(OperationsManager mgr, WorldModel world, FactionState st)
        {
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);
            return log;
        }

        private static OperationsManager Manager() => new OperationsManager(itemVolumes: new Volumes());

        private static List<PendingFleetAction> MineFleets(FactionState st)
            => st.PendingFleets.Where(f => f.Kind == ActionKind.Mine).ToList();

        private static string DwellSite(PendingFleetAction f)
            => f.CurrentStopKind == FleetStopKind.Mine
                ? f.TargetNodeId
                : f.RemainingStops.First(s => s.Kind == FleetStopKind.Mine).NodeId;

        // ---- in-flight netting ----------------------------------------------------------------

        /// <summary>A trip already flying for an order counts against its target rate: the next
        /// cycle sees the same order, a free deposit and an idle drill, and must not send a second
        /// trip while the one in flight covers the rate.</summary>
        [Fact]
        public void TripInFlight_CoversTheOrdersRate_NoSecondTrip()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F" };
            Deposit(world, "Ore1", new Vec3(0, 0, 0)).ShipStock["Fast"] = 1;
            Deposit(world, "Ore2", new Vec3(1, 0, 0)).ShipStock["Fast"] = 1;   // a free seam and a free drill
            Node(world, "Need", new Vec3(2, 0, 0));
            Want(world, "Need", 100_000);
            OperationsManager mgr = Manager();

            Tick(mgr, world, st);
            Assert.Single(MineFleets(st));

            world.UtcNow = world.UtcNow.AddMinutes(3);
            Want(world, "Need", 100_000);
            Tick(mgr, world, st);

            Assert.Single(MineFleets(st));
        }

        /// <summary>The discriminator for the test above: netting compares rates, it does not block
        /// the hub. An order whose target outgrows the trip in flight gets a second, full-hold trip on
        /// the free seam.</summary>
        [Fact]
        public void TargetThatOutgrowsTheTripInFlight_GetsASecondFullHoldTrip()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F" };
            Deposit(world, "Ore1", new Vec3(0, 0, 0)).ShipStock["Fast"] = 1;
            Deposit(world, "Ore2", new Vec3(1, 0, 0)).ShipStock["Fast"] = 1;
            Node(world, "Need", new Vec3(2, 0, 0));
            Want(world, "Need", 100_000);
            OperationsManager mgr = Manager();
            Tick(mgr, world, st);

            world.UtcNow = world.UtcNow.AddMinutes(3);
            Want(world, "Need");                                   // far beyond one trip's rate
            Tick(mgr, world, st);

            List<PendingFleetAction> mines = MineFleets(st);
            Assert.Equal(2, mines.Count);
            Assert.All(mines, f => Assert.Equal(1_000_000, f.MineTargetLiters + f.RemainingStops
                .Where(s => s.Kind == FleetStopKind.Mine).Sum(s => s.MineTargetLiters), 6));
            Assert.Equal(new[] { "Ore1", "Ore2" }, mines.Select(DwellSite).OrderBy(x => x).ToArray());
        }

        /// <summary>The staged cycle -- the only path the live host runs -- seeds the same in-flight
        /// rate index. Without it this passes through <see cref="OperationsManager.Tick"/> and fails
        /// live.</summary>
        [Fact]
        public void TripInFlight_CoversTheOrdersRate_UnderTheStagedCycleToo()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F" };
            Deposit(world, "Ore1", new Vec3(0, 0, 0)).ShipStock["Fast"] = 1;
            Deposit(world, "Ore2", new Vec3(1, 0, 0)).ShipStock["Fast"] = 1;
            Node(world, "Need", new Vec3(2, 0, 0));
            Want(world, "Need", 100_000);
            OperationsManager mgr = Manager();
            var states = new List<FactionState> { st };

            RunStagedSweep(mgr, states, world);
            Assert.Single(MineFleets(st));

            world.UtcNow = world.UtcNow.AddMinutes(3);
            Want(world, "Need", 100_000);
            RunStagedSweep(mgr, states, world);

            Assert.Single(MineFleets(st));
        }

        // ---- one trip per deposit -------------------------------------------------------------

        /// <summary>A deposit's cooldown is written when its dwell resolves, so mid-dwell it reads
        /// as free. A second hub whose only seam is being worked must wait, not stack a second drill
        /// on the same seam. The walk's <c>working</c> token is asserted in
        /// <see cref="ExtractionOrderDispatchTests"/>.</summary>
        [Fact]
        public void DepositMidDwell_TakesNoSecondTrip()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F" };
            Deposit(world, "Ore1", new Vec3(0, 0, 0)).ShipStock["Fast"] = 2;   // a spare drill on the seam
            Node(world, "NeedA", new Vec3(1, 0, 0));
            Node(world, "NeedB", new Vec3(2, 0, 0));
            Want(world, "NeedA");
            OperationsManager mgr = Manager();
            Tick(mgr, world, st);
            Assert.Single(MineFleets(st));

            world.UtcNow = world.UtcNow.AddMinutes(3);
            Want(world, "NeedA");
            Want(world, "NeedB");
            Tick(mgr, world, st);

            Assert.Single(MineFleets(st));
        }

        /// <summary>The same within one pass: the first order's dispatch marks the seam, and a
        /// second order walked later in the same pass finds it worked.</summary>
        [Fact]
        public void DepositDispatchedThisPass_TakesNoSecondTripInTheSamePass()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F" };
            Deposit(world, "Ore1", new Vec3(0, 0, 0)).ShipStock["Fast"] = 2;
            Node(world, "NeedA", new Vec3(1, 0, 0));
            Node(world, "NeedB", new Vec3(2, 0, 0));
            Want(world, "NeedA");
            Want(world, "NeedB");

            List<string> log = Tick(Manager(), world, st);

            Assert.Single(MineFleets(st));
            Assert.Single(log, l => l.Contains("MINE_DISPATCHED"));
        }

        // ---- standing orders: the working skip spends no slot ---------------------------------

        /// <summary>A hub whose catchment has one deposit already being worked toward it: the scan
        /// fills its open slots on the OTHER deposits -- the same number of trips it sent before,
        /// one per deposit, instead of stacking one on the worked seam.</summary>
        [Fact]
        public void StandingOrder_FillsItsOpenSlotsOnFreeDeposits_AndStacksNothingOnAWorkedOne()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F" };
            Node(world, "Hub", new Vec3(0, 0, 0));
            Deposit(world, "Ore1", new Vec3(0, 0, 0)).ShipStock["Fast"] = 2;
            Deposit(world, "Ore2", new Vec3(0, 0, 0)).ShipStock["Fast"] = 1;
            Deposit(world, "Ore3", new Vec3(0, 0, 0)).ShipStock["Fast"] = 1;
            OperationsManager mgr = Manager();

            MineDispatchOutcome outcome;
            Assert.Equal(PlayerMineResult.Ok, mgr.TriggerPlayerMine(world, world.Tuning, st, "Ore1", "Hub", null,
                new List<string>(), out outcome));
            Assert.Equal(MineDispatchOutcome.Dispatched, outcome);

            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, world.UtcNow);
            List<string> log = Tick(mgr, world, st);

            List<PendingFleetAction> mines = MineFleets(st);
            Assert.Equal(3, mines.Count);                                     // catchment size, as before
            Assert.Equal(new[] { "Ore1", "Ore2", "Ore3" }, mines.Select(DwellSite).OrderBy(s => s).ToArray());
            string scan = Assert.Single(log, l => l.Contains("MINE_AUTO_SCAN"));
            Assert.Contains("inflight=1 open=2 dispatched=2", scan);
            Assert.Contains("Ore1:working", scan);
        }

        // ---- the fastest idle drill ------------------------------------------------------------

        /// <summary>A slow drill standing on the deposit used to fly the trip because it was
        /// nearest. The fast one a node away is fetched instead.</summary>
        [Fact]
        public void FasterIdleDrill_IsFetched_OverASlowOneAlreadyAtTheDeposit()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F" };
            Deposit(world, "Ore1", new Vec3(0, 0, 0)).ShipStock["Slow"] = 1;
            Node(world, "Yard", new Vec3(50, 0, 0)).ShipStock["Fast"] = 1;
            Node(world, "Need", new Vec3(1, 0, 0));
            Want(world, "Need");

            List<string> log = Tick(Manager(), world, st);

            PendingFleetAction mine = Assert.Single(MineFleets(st));
            Assert.Equal("Fast", mine.ShipPrefabId);
            Assert.Equal("Ore1", DwellSite(mine));
            Assert.Equal(1, world.Graph.Nodes["Ore1"].ShipStock["Slow"]);        // left parked
            string expected = (100 * world.Tuning.MineDrillLitersPerScorePoint * world.Tuning.MineDutyCycle)
                .ToString("0.0", CultureInfo.InvariantCulture);
            string line = Assert.Single(log, l => l.Contains("MINE_DISPATCHED"));
            Assert.Contains("via_pickup=Yard", line);
            Assert.EndsWith(" prefab=Fast drillRate=" + expected + " order=Need:Ore:Iron", line);
        }

        /// <summary>Among other nodes, too, rate outranks distance: the near slow drill loses to the
        /// far fast one.</summary>
        [Fact]
        public void FasterIdleDrill_IsFetched_OverANearerSlowAlternate()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F" };
            Deposit(world, "Ore1", new Vec3(0, 0, 0));
            Node(world, "Near", new Vec3(5, 0, 0)).ShipStock["Slow"] = 1;
            Node(world, "Far", new Vec3(50, 0, 0)).ShipStock["Fast"] = 1;
            Node(world, "Need", new Vec3(1, 0, 0));
            Want(world, "Need");

            Tick(Manager(), world, st);

            PendingFleetAction mine = Assert.Single(MineFleets(st));
            Assert.Equal("Fast", mine.ShipPrefabId);
            Assert.Equal("Far", mine.OriginNodeId);
        }

        /// <summary>The tie rule: an equally fast hull elsewhere does not pull the trip away from the
        /// one already at the deposit -- that is the nearest, and it needs no pickup leg.</summary>
        [Fact]
        public void EquallyFastDrill_AtTheDeposit_KeepsTheTrip()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F" };
            Deposit(world, "Ore1", new Vec3(0, 0, 0)).ShipStock["Fast"] = 1;
            Node(world, "A_Yard", new Vec3(0, 0, 0)).ShipStock["Fast"] = 1;   // co-located, sorts first by id
            Node(world, "Need", new Vec3(1, 0, 0));
            Want(world, "Need");

            List<string> log = Tick(Manager(), world, st);

            PendingFleetAction mine = Assert.Single(MineFleets(st));
            Assert.Equal(FleetStopKind.Mine, mine.CurrentStopKind);            // dwelling at once, no pickup
            Assert.Equal("Ore1", mine.OriginNodeId);
            Assert.DoesNotContain(log, l => l.Contains("via_pickup="));
        }

        /// <summary>A player whose doctrine names the Mine hull flies that hull, even with a faster
        /// one idle -- the doctrine outranks the rate.</summary>
        [Fact]
        public void PlayerDoctrineHull_IsFlown_OverAFasterOneAtTheDeposit()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F", IsPlayerFaction = true };
            StandingOrderFixtures.AddDoctrineLine(st, "Doc", targetCount: 4);
            Deposit(world, "Ore1", new Vec3(0, 0, 0)).ShipStock["Fast"] = 1;
            Node(world, "Yard", new Vec3(50, 0, 0)).ShipStock["Doc"] = 1;
            Node(world, "Hub", new Vec3(1, 0, 0));
            OperationsManager mgr = Manager();

            MineDispatchOutcome outcome;
            Assert.Equal(PlayerMineResult.Ok, mgr.TriggerPlayerMine(world, world.Tuning, st, "Ore1", "Hub", null,
                new List<string>(), out outcome));

            PendingFleetAction mine = Assert.Single(MineFleets(st));
            Assert.Equal("Doc", mine.ShipPrefabId);
        }

        /// <summary>The discriminator: the same estate without the doctrine flies the faster hull
        /// that is already at the deposit.</summary>
        [Fact]
        public void WithoutDoctrine_TheSameEstateFliesTheFasterHull()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            var st = new FactionState { Tag = "F", IsPlayerFaction = true };
            Deposit(world, "Ore1", new Vec3(0, 0, 0)).ShipStock["Fast"] = 1;
            Node(world, "Yard", new Vec3(50, 0, 0)).ShipStock["Doc"] = 1;
            Node(world, "Hub", new Vec3(1, 0, 0));
            OperationsManager mgr = Manager();

            MineDispatchOutcome outcome;
            Assert.Equal(PlayerMineResult.Ok, mgr.TriggerPlayerMine(world, world.Tuning, st, "Ore1", "Hub", null,
                new List<string>(), out outcome));

            PendingFleetAction mine = Assert.Single(MineFleets(st));
            Assert.Equal("Fast", mine.ShipPrefabId);
        }

        // ---- staged-cycle driver (same reflection hooks as OperationsCycleEquivalenceTests) ------

        private static void RunStagedSweep(OperationsManager manager, IList<FactionState> states, WorldModel world)
        {
            var log = new List<string>();
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
