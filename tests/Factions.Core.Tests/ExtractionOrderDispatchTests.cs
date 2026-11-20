using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-395 slice 2b (docs/mining-plan.md): an NPC's extraction orders come from
    /// <see cref="ExtractionPlanner"/> once per Operations pass and are walked through the same
    /// catchment loop as a player's standing orders -- full-hold trips, one per free deposit, until
    /// the order's target rate is met. The per-cycle demand-row trigger and the Mine-vs-Haul contest
    /// are gone. Player orders are pinned by <see cref="PlayerMineOrderRegressionTests"/>.
    /// </summary>
    [Collection(DebugTracesCollection.Name)]
    public sealed class ExtractionOrderDispatchTests : IDisposable
    {
        public void Dispose() { DebugTraces.ResetToDefaults(); }

        private const string Ice = "Ore:Ice";
        private const string Iron = "Ore:Iron";
        private const string Hydrogen = "Gas:Hydrogen";
        private const long WhiHold = 375_000;

        private sealed class Catalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId)
            {
                switch (prefabId)
                {
                    case "WHI": return WhiHold;
                    case "Spadi": return 3_375;
                    case "Hauler": return 50_000;
                    default: return 0;
                }
            }
            public double DrillYieldScore(string prefabId)
            {
                switch (prefabId)
                {
                    case "WHI": return 128.6;     // ~1,616 L/s at the shipped constants
                    case "Spadi": return 0.64;    // ~8 L/s
                    default: return 0;
                }
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
        }

        private sealed class Volumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string key) => key.StartsWith("Ore:", StringComparison.Ordinal) ? 0.37 : 1.0;
        }

        private static readonly Func<FactionTuning, Dictionary<string, double>> IcePerGas =
            t => new Dictionary<string, double> { { Hydrogen, 0.05 } };

        private static OperationsManager Manager()
        {
            return new OperationsManager(itemVolumes: new Volumes()) { IcePerGasUnitSource = IcePerGas };
        }

        private static SpatialNode Node(WorldModel world, string id, double x, string tag = "N")
        {
            SpatialNode n = world.Graph.Add(new Vec3(x, 0, 0), 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = 1.0;
            return n;
        }

        private static SpatialNode Deposit(WorldModel world, string id, double x, string ore, string tag = "N")
        {
            SpatialNode n = Node(world, id, x, tag);
            n.Richness = 1.0;
            n.Deposit = new NodeDeposit();
            n.Deposit.Entries.Add(new DepositEntry(ore, 1.0, 100.0));
            return n;
        }

        /// <summary>An NPC whose one FuelDepot is starved: its hulls burn hydrogen far faster than
        /// anything could supply ice for it, three ice deposits lie in reach, a WHI drill idles at
        /// the yard and a slow Spadi on the nearest seam.</summary>
        private static WorldModel StarvedIceHub(out FactionState st, string tag = "N", bool player = false)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            world.PrefabCatalogue = new Catalogue();
            st = new FactionState { Tag = tag, IsPlayerFaction = player };

            SpatialNode depot = Node(world, "Depot", 0, tag);
            depot.Capabilities[NodeCapabilityKind.FuelDepot] = 1;
            SpatialNode port = Node(world, "Port", 5, tag);
            FuelDraw.CreditBurn(port, world.UtcNow, world.Tuning, Hydrogen, 1e11);
            Deposit(world, "Ice1", 20, "Ice", tag).ShipStock["Spadi"] = 1;
            Deposit(world, "Ice2", 40, "Ice", tag);
            Deposit(world, "Ice3", 60, "Ice", tag);
            Node(world, "Yard", 80, tag).ShipStock["WHI"] = 1;
            return world;
        }

        private static List<string> Tick(OperationsManager mgr, WorldModel world, FactionState st)
        {
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);
            return log;
        }

        private static List<PendingFleetAction> MineFleets(FactionState st)
            => st.PendingFleets.Where(f => f.Kind == ActionKind.Mine).ToList();

        private static string DwellSite(PendingFleetAction f)
            => f.CurrentStopKind == FleetStopKind.Mine
                ? f.TargetNodeId
                : f.RemainingStops.First(s => s.Kind == FleetStopKind.Mine).NodeId;

        private static double TripLiters(PendingFleetAction f)
            => f.MineTargetLiters + (f.RemainingStops ?? new List<FleetStop>())
                .Where(s => s.Kind == FleetStopKind.Mine).Sum(s => s.MineTargetLiters);

        private static Dictionary<string, double> TripWeights(PendingFleetAction f)
            => f.CurrentStopKind == FleetStopKind.Mine
                ? f.MineWeights
                : f.RemainingStops.First(s => s.Kind == FleetStopKind.Mine).MineWeights;

        // ---- the order flies full holds -----------------------------------------------------------

        [Fact]
        public void StarvedIceHub_WithAnIdleWhiDrill_FliesAFullHoldTripFromItsOrder()
        {
            FactionState st;
            WorldModel world = StarvedIceHub(out st);
            OperationsManager mgr = Manager();

            List<string> log = Tick(mgr, world, st);

            StandingExtractionOrder order = Assert.Single(mgr.ExtractionOrdersFor("N"));
            Assert.Equal("Depot", order.HubNodeId);
            Assert.Equal(Ice, order.OreKey);
            Assert.Equal(ExtractionPlanner.SourceBurn, order.ConsumeSource);
            Assert.False(order.Paused);

            PendingFleetAction whi = Assert.Single(MineFleets(st), f => f.ShipPrefabId == "WHI");
            Assert.Equal("Ice1", DwellSite(whi));                        // nearest of equal seams
            Assert.Equal(WhiHold, TripLiters(whi), 6);                   // the hold, not a shortfall
            Assert.Equal(new[] { Ice }, TripWeights(whi).Keys.ToArray()); // the order's ore alone
            Assert.Equal(1.0, TripWeights(whi)[Ice], 9);

            // One trip per free seam; the slow hull takes the next one.
            Assert.Equal(new[] { "Ice1", "Ice2" }, MineFleets(st).Select(DwellSite).OrderBy(s => s).ToArray());
            List<string> dispatched = log.Where(l => l.Contains("MINE_DISPATCHED")).ToList();
            Assert.Equal(2, dispatched.Count);
            Assert.All(dispatched, l => Assert.EndsWith(" order=Depot:Ore:Ice", l));
        }

        /// <summary>FAC-384: a fight closes its node to Mine. A closed seam is passed over for the next
        /// one, and a closed hub dispatches nothing at all — each with the combat reason, no demand.</summary>
        [Fact]
        public void AClosedSeam_IsPassedOver_WithTheCombatReason()
        {
            FactionState st;
            WorldModel world = StarvedIceHub(out st);
            world.ClosedForCombat.Add("Ice1");

            List<string> log = Tick(Manager(), world, st);

            Assert.DoesNotContain(MineFleets(st), f => DwellSite(f) == "Ice1");
            Assert.NotEmpty(MineFleets(st));
            Assert.Contains(log, l => l.Contains("MINE_DISPATCH_BLOCKED src=Ice1") && l.EndsWith("reason=combat"));
        }

        [Fact]
        public void AClosedHub_DispatchesNoMineTrip_AndStagesNoDemand()
        {
            FactionState st;
            WorldModel world = StarvedIceHub(out st);
            world.ClosedForCombat.Add("Depot");

            List<string> log = Tick(Manager(), world, st);

            Assert.Empty(MineFleets(st));
            Assert.Contains(log, l => l.Contains("MINE_DISPATCH_BLOCKED") && l.Contains("dest=Depot") && l.EndsWith("reason=combat"));
            Assert.DoesNotContain(log, l => l.Contains("MINE_DISPATCH_BLOCKED") && !l.EndsWith("reason=combat"));
        }

        [Fact]
        public void TheStockCeiling_PausesTheOrder_AndDispatchResumesOnlyBelowTheHysteresisBand()
        {
            FactionState st;
            WorldModel world = StarvedIceHub(out st);
            OperationsManager mgr = Manager();
            SpatialNode depot = world.Graph.Nodes["Depot"];

            // Read the consumption the planner measures, then fill the hub to its ceiling.
            double ceiling = WorldCeiling(world);
            depot.Inventory[Ice] = (long)Math.Ceiling(ceiling);

            Tick(mgr, world, st);
            StandingExtractionOrder paused = Assert.Single(mgr.ExtractionOrdersFor("N"));
            Assert.True(paused.Paused);
            Assert.Equal(ExtractionPlanner.ReasonCeiling, paused.Reason);
            Assert.Empty(MineFleets(st));

            // Inside the band: still paused, on the previous plan the manager kept.
            depot.Inventory[Ice] = (long)(ceiling * 0.9);
            Tick(mgr, world, st);
            Assert.Equal(ExtractionPlanner.ReasonHysteresis, Assert.Single(mgr.ExtractionOrdersFor("N")).Reason);
            Assert.Empty(MineFleets(st));

            // Below the resume line: open again, and trips fly.
            depot.Inventory[Ice] = (long)(ceiling * 0.5);
            List<string> log = Tick(mgr, world, st);
            StandingExtractionOrder resumed = Assert.Single(mgr.ExtractionOrdersFor("N"));
            Assert.False(resumed.Paused);
            Assert.Equal(ExtractionPlanner.ReasonResume, resumed.Reason);
            Assert.NotEmpty(MineFleets(st));
            Assert.Contains(log, l => l.Contains("MINE_DISPATCHED") && l.EndsWith(" order=Depot:Ore:Ice"));
        }

        /// <summary><c>ExtractionStockHours × consume</c> for the depot, in units, as the planner
        /// computes it from this world.</summary>
        private static double WorldCeiling(WorldModel world)
        {
            FactionState st = new FactionState { Tag = "N" };
            List<StandingExtractionOrder> orders = ExtractionPlanner.Plan(st, world, world.Tuning, world.UtcNow,
                new ExtractionPlanInputs { Volumes = new Volumes(), IcePerGasUnit = IcePerGas(world.Tuning) }, null);
            StandingExtractionOrder o = Assert.Single(orders);
            return world.Tuning.ExtractionStockHours * o.ConsumePerHour;
        }

        // ---- the retired trigger ------------------------------------------------------------------

        /// <summary>An ore want the Haul pass can partly serve, a free deposit and an idle drill. The
        /// retired path ranked a Mine option for this want against the Haul vector and flew a drill
        /// (it did on integrate/npc-extraction @ 7489a379). Now the want gets its Haul and nothing
        /// else: no measured draw, so no extraction order, so no trip.</summary>
        [Fact]
        public void AnOreDemandRow_NoLongerDispatchesAMineTrip_AndTheHaulStillFlies()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            world.Tuning.HaulMinLoadFraction = 0;
            world.PrefabCatalogue = new Catalogue();
            var st = new FactionState { Tag = "N" };

            Deposit(world, "Ore1", 0, "Iron").ShipStock["WHI"] = 1;
            SpatialNode stock = Node(world, "Stock", 10);
            stock.Inventory[Iron] = 1_000;
            stock.ShipStock["Hauler"] = 1;
            Node(world, "Need", 20);
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "N", Kind = "Item:" + Iron, NodeId = "Need", Quantity = 50_000,
                ExpiresUtc = world.UtcNow.AddMinutes(30), Class = DemandClass.ShipBuild,
                FirstPostedUtc = world.UtcNow,
            });
            OperationsManager mgr = Manager();

            List<string> log = Tick(mgr, world, st);

            Assert.Contains(st.PendingFleets, f => f.Kind == ActionKind.Haul);
            Assert.Empty(MineFleets(st));
            Assert.DoesNotContain(log, l => l.Contains("MINE_"));
            Assert.Empty(mgr.ExtractionOrdersFor("N"));
        }

        // ---- no consumption, no order -------------------------------------------------------------

        [Fact]
        public void AHubWithZeroConsumption_NeverGetsAnOrder_WhateverElseItHas()
        {
            FactionState st;
            WorldModel world = StarvedIceHub(out st);
            world.Graph.Nodes["Port"].FuelBurnEma.Clear();        // nothing burns, so no ice is implied
            world.Graph.Nodes["Depot"].Inventory[Ice] = 10;        // a hub with stock ...
            world.Demand.Post(new DemandEntry                    // ... and a want no bank posted
            {
                FactionTag = "N", Kind = "Item:" + Ice, NodeId = "Depot", Quantity = 1_000_000,
                ExpiresUtc = world.UtcNow.AddMinutes(30), Class = DemandClass.Catalog,
                FirstPostedUtc = world.UtcNow,
            });
            OperationsManager mgr = Manager();

            Tick(mgr, world, st);

            Assert.Empty(mgr.ExtractionOrdersFor("N"));
            Assert.Empty(MineFleets(st));

            // The discriminator: the same estate with a measured draw at the depot orders and flies.
            world.Graph.Nodes["Depot"].DemandHistory[Ice] = 1e9;
            Tick(mgr, world, st);
            Assert.Single(mgr.ExtractionOrdersFor("N"));
            Assert.NotEmpty(MineFleets(st));
        }

        [Fact]
        public void APlayerFaction_GetsNoExtractionOrders()
        {
            FactionState st;
            WorldModel world = StarvedIceHub(out st, tag: "P", player: true);
            world.Graph.Nodes["Depot"].DemandHistory[Ice] = 1e9;
            OperationsManager mgr = Manager();

            List<string> log = Tick(mgr, world, st);

            Assert.Empty(mgr.ExtractionOrdersFor("P"));
            Assert.Empty(MineFleets(st));
            Assert.DoesNotContain(log, l => l.Contains("MINE_"));
        }

        // ---- the bootstrap mint and the hull fallback ---------------------------------------------

        [Fact]
        public void ADrillLessFaction_MintsTheBootstrap_OncePerOrderPerPass()
        {
            FactionState st;
            WorldModel world = StarvedIceHub(out st);
            world.Graph.Nodes["Ice1"].ShipStock.Clear();
            world.Graph.Nodes["Yard"].ShipStock.Clear();          // no drill anywhere
            OperationsManager mgr = Manager();

            List<string> log = Tick(mgr, world, st);

            string mint = Assert.Single(log, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Contains("src=Ice1 dest=Depot", mint);
            double cap = world.Tuning.FreeMinerCargoVolumeLiters * world.Tuning.MineBootstrapYieldFraction;
            Assert.True(cap > 0, "tuning must fund a mint for this test to mean anything");
            Assert.Equal((long)(cap / 0.37), world.GetNodeInventory("Depot", Ice));   // a full bootstrap hold
            Assert.True(world.Graph.Nodes["Ice1"].MineCooldownUntilUtc > world.UtcNow);
            Assert.Equal(default(DateTime), world.Graph.Nodes["Ice2"].MineCooldownUntilUtc);   // the walk stopped
            Assert.Empty(MineFleets(st));
            Assert.Contains(log, l => l.Contains("MINE_DISPATCH_BLOCKED") && l.Contains("no_ship_anywhere"));
            Assert.Single(world.Demand.SnapshotShipDemands("N"), d => d.Kind == "Ship:Mine");
        }

        [Fact]
        public void AnOpenOrderWithFreeDepositsButNoIdleDrill_StagesTheShipMineFallback_AndMintsNothing()
        {
            FactionState st;
            WorldModel world = StarvedIceHub(out st);
            world.Graph.Nodes["Ice1"].ShipStock.Clear();
            world.Graph.Nodes["Yard"].ShipStock.Clear();
            // The faction's one drill is out working Ice1: owned, so no bootstrap; busy, so no trip.
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "N", ShipPrefabId = "WHI", ConvoySize = 1,
                OriginNodeId = "Ice1", TargetNodeId = "Ice1", CurrentStopKind = FleetStopKind.Mine,
                DepartedUtc = world.UtcNow, EstimatedArrivalUtc = world.UtcNow.AddMinutes(10),
                MineWeights = new Dictionary<string, double> { { Ice, 1.0 } }, MineTargetLiters = 1_000,
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "Depot", Kind = FleetStopKind.Service } },
            });
            OperationsManager mgr = Manager();

            List<string> log = Tick(mgr, world, st);

            Assert.Equal(ExtractionBound.HullBound, Assert.Single(mgr.ExtractionOrdersFor("N")).Bound);
            Assert.DoesNotContain(log, l => l.Contains("MINE_BOOTSTRAP"));
            Assert.Single(MineFleets(st));                                    // only the one already out
            Assert.Single(world.Demand.SnapshotShipDemands("N"), d => d.Kind == "Ship:Mine");
            // One attempt, then the walk stops: Ice3 is never tried after Ice2 found no hull.
            string blocked = Assert.Single(log, l => l.Contains("MINE_DISPATCH_BLOCKED"));
            Assert.Contains("src=Ice2 dest=Depot reason=no_ship_anywhere", blocked);
        }

        // ---- the trace ----------------------------------------------------------------------------

        [Fact]
        public void TheExtractionTrace_NamesEachDepositsState_AndIsSilentByDefault()
        {
            FactionState st;
            WorldModel world = StarvedIceHub(out st);
            world.Graph.Nodes["Ice2"].MineCooldownUntilUtc = world.UtcNow.AddHours(1);
            OperationsManager mgr = Manager();

            List<string> quiet = Tick(mgr, world, st);
            Assert.DoesNotContain(quiet, l => l.Contains("EXTRACTION_PLAN") || l.Contains("MINE_ORDER_SCAN"));
            // The WHI takes Ice1, Ice2 is cooling, the Spadi takes Ice3.
            Assert.Equal(new[] { "Ice1", "Ice3" }, MineFleets(st).Select(DwellSite).OrderBy(s => s).ToArray());

            Deposit(world, "Ice4", 70, "Ice");
            Deposit(world, "Rock", 30, "Iron");                  // in reach, holds no ice: not in the walk
            world.Graph.Nodes["Yard"].ShipStock["WHI"] = 1;
            DebugTraces.Extraction = true;
            world.UtcNow = world.UtcNow.AddMinutes(3);
            List<string> log = Tick(mgr, world, st);

            Assert.Single(log, l => l.StartsWith("EXTRACTION_PLAN N hub=Depot ore=Ore:Ice ", StringComparison.Ordinal));
            string scan = Assert.Single(log, l => l.StartsWith("N MINE_ORDER_SCAN order=Depot:Ore:Ice ", StringComparison.Ordinal));
            Assert.Contains("catchment=4 dispatched=1 [", scan);
            Assert.Contains("Ice1:working", scan);
            Assert.Contains("Ice2:cooldown_until=" + world.Graph.Nodes["Ice2"].MineCooldownUntilUtc.ToString("HH:mm:ss"), scan);
            Assert.Contains("Ice3:working", scan);
            Assert.Contains("Ice4:dispatched", scan);
            Assert.DoesNotContain("Rock", scan);
        }

        // ---- staged/sync equivalence --------------------------------------------------------------

        /// <summary>Two orders on two hubs (ice on the burn, iron on a refinery's draw), three passes
        /// with dwells resolving in between: the synchronous tick and the staged cycle dispatch the
        /// same trips, write the same log and keep the same plan.</summary>
        [Fact]
        public void ExtractionOrders_DispatchTheSame_UnderTheStagedCycleAsUnderTheTick()
        {
            List<string> sync = RunTwoHubs(staged: false);
            List<string> staged = RunTwoHubs(staged: true);

            Assert.Contains(sync, l => l.Contains("MINE_DISPATCHED") && l.EndsWith(" order=Depot:Ore:Ice"));
            Assert.Contains(sync, l => l.Contains("MINE_DISPATCHED") && l.EndsWith(" order=Refinery:Ore:Iron"));
            Assert.Equal(sync, staged);
        }

        private static List<string> RunTwoHubs(bool staged)
        {
            FactionState st;
            WorldModel world = StarvedIceHub(out st);
            Node(world, "Refinery", 300).DemandHistory[Iron] = 1e8;
            Deposit(world, "Iron1", 310, "Iron").ShipStock["WHI"] = 1;
            Deposit(world, "Iron2", 320, "Iron");
            OperationsManager mgr = Manager();
            var states = new List<FactionState> { st };
            var transcript = new List<string>();
            var inv = CultureInfo.InvariantCulture;

            for (int pass = 1; pass <= 3; pass++)
            {
                var log = new List<string>();
                if (staged) RunStagedSweep(mgr, states, world, log);
                else mgr.Tick(states, world, world.Tuning, log);

                transcript.Add("-- pass " + pass);
                transcript.AddRange(log.Where(l => l.Contains("MINE")).Select(l =>
                    System.Text.RegularExpressions.Regex.Replace(l, @"^\[[0-9A-F]+\] ", "")));
                transcript.AddRange(MineFleets(st).Select(f => f.ShipPrefabId + " " + f.OriginNodeId + "->"
                        + f.TargetNodeId + " " + f.CurrentStopKind + " " + TripLiters(f).ToString("0", inv)
                        + " cargo=" + (f.Cargo == null ? "-" : string.Join(",", f.Cargo.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .Select(kv => kv.Key + "x" + kv.Value)))
                        + " eta=" + f.EstimatedArrivalUtc.ToString("HH:mm:ss.fff", inv))
                    .OrderBy(s => s, StringComparer.Ordinal));
                transcript.AddRange(mgr.ExtractionOrdersFor("N").Select(o => o.Key + " paused=" + o.Paused
                    + " reason=" + o.Reason + " bound=" + o.Bound
                    + " target=" + o.TargetRateLitersPerHour.ToString("0", inv)));
                transcript.Add("Depot ice=" + world.GetNodeInventory("Depot", Ice)
                    + " Refinery iron=" + world.GetNodeInventory("Refinery", Iron));

                world.UtcNow = world.UtcNow.AddMinutes(10);
                PendingFleetResolver.ResolveArrivals(states, world, world.Tuning, new SeededRandom(1),
                    new List<string>(), itemVolumes: new Volumes());
            }
            return transcript;
        }

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
