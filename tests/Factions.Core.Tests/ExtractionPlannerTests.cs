using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers docs/mining-plan.md slice 2a: <see cref="ExtractionPlanner"/> and the mined-rate EMA it
    /// reads (<see cref="ExtractionFlow"/>). The planner is pure, so every case builds a world by hand
    /// and reads the orders back.
    /// </summary>
    [Collection(DebugTracesCollection.Name)]
    public class ExtractionPlannerTests : IDisposable
    {
        private const string Iron = "Ore:Iron";
        private const string Ice = "Ore:Ice";
        private const string Hydrogen = "Gas:Hydrogen";
        private const string Tag = "NPC";

        public void Dispose() { DebugTraces.ResetToDefaults(); }

        // ---- fixtures -------------------------------------------------------------------------

        private sealed class OneLitre : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string invKey) { return 1.0; }
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private static WorldModel World()
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            FactionTuning t = w.Tuning;
            t.OpsIntervalSeconds = 180.0;       // one DemandHistory pass = 180 s -> x20 per hour
            t.EconomyIntervalSeconds = 10.0;
            t.MineSearchRadiusKm = 500.0;
            t.ExtractionStockHours = 24.0;
            t.ExtractionResumeFraction = 0.75;
            t.ExtractionMinedEmaHours = 6.0;
            t.MineOverburdenFraction = 0.0;
            t.MineCooldownMultiplier = 1.0;
            return w;
        }

        private static SpatialNode Node(WorldModel w, string id, Vec3 pos, string owner = Tag)
        {
            SpatialNode n = w.Graph.Add(pos, 0.5, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 1.0;
            n.Radius = 40;
            return n;
        }

        /// <summary>A refinery hub drawing <paramref name="unitsPerHour"/> of iron: DemandHistory is
        /// litres per 180 s pass at one litre a unit, so a twentieth of that per pass.</summary>
        private static SpatialNode IronHub(WorldModel w, double unitsPerHour, string id = "Hub")
        {
            SpatialNode hub = Node(w, id, new Vec3(0, 0, 0));
            hub.DemandHistory[Iron] = unitsPerHour / 20.0;
            return hub;
        }

        private static SpatialNode IronDeposit(WorldModel w, string id, double x)
        {
            return TestSupport.WithOre(Node(w, id, new Vec3(x, 0, 0)), "Iron");
        }

        private static FactionState Npc() { return new FactionState { Tag = Tag, RngSeed = 1 }; }

        private static ExtractionPlanInputs Inputs(ExtractionFleetIndex fleet = null,
                                                   Dictionary<string, double> icePerGas = null)
        {
            return new ExtractionPlanInputs { Volumes = new OneLitre(), Fleet = fleet, IcePerGasUnit = icePerGas };
        }

        private static List<StandingExtractionOrder> Plan(WorldModel w, ExtractionPlanInputs inputs = null,
            IList<StandingExtractionOrder> previous = null, FactionState st = null)
        {
            return ExtractionPlanner.Plan(st ?? Npc(), w, w.Tuning, w.UtcNow, inputs ?? Inputs(), previous);
        }

        private static StandingExtractionOrder Only(List<StandingExtractionOrder> orders, string ore)
        {
            StandingExtractionOrder found = null;
            foreach (StandingExtractionOrder o in orders)
                if (o.OreKey == ore) { Assert.Null(found); found = o; }
            Assert.NotNull(found);
            return found;
        }

        private static ExtractionFleetIndex Drills(params double[] litresPerSecond)
        {
            var index = new ExtractionFleetIndex();
            index.IdleDrillLitersPerSecond.AddRange(litresPerSecond);
            index.SortPool();
            return index;
        }

        // ---- the gap --------------------------------------------------------------------------

        [Fact]
        public void APositiveGap_OpensAnOrder_WithTheMeasuredTerms()
        {
            WorldModel w = World();
            IronHub(w, 2000);

            StandingExtractionOrder o = Only(Plan(w), Iron);

            Assert.Equal("Hub", o.HubNodeId);
            Assert.Equal(2000, o.ConsumePerHour, 6);
            Assert.Equal(ExtractionPlanner.SourceDraw, o.ConsumeSource);
            Assert.Equal(0, o.PassivePerHour, 6);
            Assert.Equal(0, o.MinedPerHour, 6);
            Assert.Equal(2000, o.GapPerHour, 6);
            Assert.Equal(2000, o.TargetRateLitersPerHour, 6);
            Assert.False(o.Paused);
            Assert.Equal(ExtractionPlanner.ReasonGap, o.Reason);
        }

        [Fact]
        public void MinedDeliveriesThatCoverConsumption_OpenNothing()
        {
            WorldModel w = World();
            SpatialNode hub = IronHub(w, 2000);
            // 18,000 units credited on a 6 h constant read as 3,000 an hour.
            ExtractionFlow.CreditMined(hub, w.UtcNow, w.Tuning, Iron, 18000);

            Assert.Empty(Plan(w));
        }

        [Fact]
        public void MinedDeliveriesShortOfConsumption_LeaveTheRemainderAsTheGap()
        {
            WorldModel w = World();
            SpatialNode hub = IronHub(w, 2000);
            ExtractionFlow.CreditMined(hub, w.UtcNow, w.Tuning, Iron, 6000);   // 1,000 an hour

            StandingExtractionOrder o = Only(Plan(w), Iron);
            Assert.Equal(1000, o.MinedPerHour, 6);
            Assert.Equal(1000, o.GapPerHour, 6);
            Assert.Equal(2000, o.TargetPerHour, 6);   // the order sustains the whole mined rate
        }

        [Fact]
        public void PassiveMint_IsTheCatchmentsIncomePassYield_AndCountsAgainstTheGap()
        {
            WorldModel w = World();
            IronHub(w, 1e9);
            SpatialNode dep = IronDeposit(w, "Dep", 10);

            StandingExtractionOrder o = Only(Plan(w), Iron);

            double control = w.Influence.ControlAt(dep.Id, Tag);
            double expected = Economy.SourceItemYield(dep, control, w.Tuning, ItemKind.Ore) * 1.0 * 360.0;
            Assert.True(expected > 0, "fixture: the deposit has to mint something");
            Assert.Equal(expected, o.PassivePerHour, 3);
            Assert.Equal(1e9 - expected, o.GapPerHour, 3);
        }

        [Fact]
        public void PassiveThatCoversConsumption_OpensNothing()
        {
            WorldModel w = World();
            IronHub(w, 1.0);             // one unit an hour; any deposit's mint beats it
            IronDeposit(w, "Dep", 10);

            Assert.Empty(Plan(w));
        }

        [Fact]
        public void AnOpenOrder_IsDroppedOncePassiveCoversConsumption()
        {
            WorldModel w = World();
            IronHub(w, 2000);
            List<StandingExtractionOrder> plan = Plan(w);
            Assert.False(Only(plan, Iron).Paused);

            IronDeposit(w, "Dep", 10);                   // its mint is far above 2,000 an hour
            Assert.Empty(Plan(w, previous: plan));
        }

        [Fact]
        public void ADepositOutsideTheSearchRadius_IsNotInTheCatchment()
        {
            WorldModel w = World();
            IronHub(w, 1e9);
            IronDeposit(w, "Far", 600);

            StandingExtractionOrder o = Only(Plan(w), Iron);
            Assert.Equal(0, o.PassivePerHour, 6);
        }

        [Fact]
        public void ADepositBetweenTwoHubs_CountsOnceForTheNearer()
        {
            WorldModel w = World();
            IronHub(w, 1e9, "A");
            SpatialNode b = Node(w, "B", new Vec3(100, 0, 0));
            b.DemandHistory[Iron] = 1e9 / 20.0;
            SpatialNode dep = IronDeposit(w, "Dep", 80);
            dep.Inventory[Iron] = 500;

            List<StandingExtractionOrder> orders = Plan(w);
            StandingExtractionOrder a = orders.Find(o => o.HubNodeId == "A");
            StandingExtractionOrder bo = orders.Find(o => o.HubNodeId == "B");
            Assert.Equal(0, a.PassivePerHour, 6);
            Assert.Equal(0, a.Stock, 6);
            Assert.True(bo.PassivePerHour > 0);
            Assert.Equal(500, bo.Stock, 6);
        }

        // ---- zero consumption, non-ore, player ---------------------------------------------------

        [Fact]
        public void AHubWithNoConsumption_NeverOrders_EvenWithDepositsAndNoStock()
        {
            WorldModel w = World();
            Node(w, "Hub", new Vec3(0, 0, 0));
            IronDeposit(w, "Dep", 10);

            Assert.Empty(Plan(w, Inputs(Drills(1000))));
        }

        [Fact]
        public void ConsumptionOfANonOreKey_IsNotAnExtractionNeed()
        {
            WorldModel w = World();
            SpatialNode hub = Node(w, "Hub", new Vec3(0, 0, 0));
            hub.DemandHistory["Ingot:Iron"] = 100;
            hub.DemandHistory[Hydrogen] = 100;

            Assert.Empty(Plan(w));
        }

        [Fact]
        public void APlayerFaction_GetsNoOrders()
        {
            WorldModel w = World();
            IronHub(w, 2000);
            var player = new FactionState { Tag = Tag, RngSeed = 1, IsPlayerFaction = true };

            Assert.Empty(Plan(w, st: player));
            Assert.NotEmpty(Plan(w));   // the same world does order for an NPC
        }

        [Fact]
        public void AnotherFactionsConsumption_IsNotThisFactionsNeed()
        {
            WorldModel w = World();
            SpatialNode theirs = Node(w, "Theirs", new Vec3(0, 0, 0), "OTHER");
            theirs.DemandHistory[Iron] = 100;

            Assert.Empty(Plan(w));
        }

        // ---- the stock ceiling and its hysteresis -------------------------------------------------

        [Fact]
        public void StockAtTheCeiling_PausesANewOrder()
        {
            WorldModel w = World();
            SpatialNode hub = IronHub(w, 2000);         // ceiling 48,000, resume 36,000
            hub.Inventory[Iron] = 48000;

            StandingExtractionOrder o = Only(Plan(w), Iron);
            Assert.True(o.Paused);
            Assert.Equal(ExtractionPlanner.ReasonCeiling, o.Reason);
            Assert.Equal(ExtractionBound.None, o.Bound);
        }

        [Fact]
        public void StockJustBelowTheCeiling_LeavesANewOrderOpen()
        {
            WorldModel w = World();
            SpatialNode hub = IronHub(w, 2000);
            hub.Inventory[Iron] = 47999;

            Assert.False(Only(Plan(w), Iron).Paused);
        }

        [Fact]
        public void APausedOrder_StaysPausedInsideTheBand_AndResumesBelowIt()
        {
            WorldModel w = World();
            SpatialNode hub = IronHub(w, 2000);
            hub.Inventory[Iron] = 50000;
            List<StandingExtractionOrder> plan = Plan(w);
            Assert.True(Only(plan, Iron).Paused);

            // Between resume (36,000) and ceiling (48,000): held by the hysteresis.
            hub.Inventory[Iron] = 40000;
            plan = Plan(w, previous: plan);
            StandingExtractionOrder held = Only(plan, Iron);
            Assert.True(held.Paused);
            Assert.Equal(ExtractionPlanner.ReasonHysteresis, held.Reason);

            // Exactly on the resume line is not below it.
            hub.Inventory[Iron] = 36000;
            plan = Plan(w, previous: plan);
            Assert.True(Only(plan, Iron).Paused);

            hub.Inventory[Iron] = 35999;
            plan = Plan(w, previous: plan);
            StandingExtractionOrder resumed = Only(plan, Iron);
            Assert.False(resumed.Paused);
            Assert.Equal(ExtractionPlanner.ReasonResume, resumed.Reason);

            // Open again, the same band does not pause it: only the ceiling does.
            hub.Inventory[Iron] = 40000;
            plan = Plan(w, previous: plan);
            StandingExtractionOrder sustained = Only(plan, Iron);
            Assert.False(sustained.Paused);
            Assert.Equal(ExtractionPlanner.ReasonSustain, sustained.Reason);

            hub.Inventory[Iron] = 48000;
            plan = Plan(w, previous: plan);
            Assert.True(Only(plan, Iron).Paused);
        }

        [Fact]
        public void AnOpenOrder_SurvivesItsOwnGapClosing()
        {
            // The order's trips are what closed the gap; closing it there would oscillate on the EMA.
            WorldModel w = World();
            SpatialNode hub = IronHub(w, 2000);
            List<StandingExtractionOrder> plan = Plan(w);

            ExtractionFlow.CreditMined(hub, w.UtcNow, w.Tuning, Iron, 12000);   // 2,000 an hour
            plan = Plan(w, previous: plan);
            StandingExtractionOrder o = Only(plan, Iron);
            Assert.False(o.Paused);
            Assert.Equal(ExtractionPlanner.ReasonSustain, o.Reason);
            Assert.Equal(0, o.GapPerHour, 6);
            Assert.Equal(ExtractionBound.None, o.Bound);
        }

        [Fact]
        public void CatchmentStock_CountsTowardTheCeiling()
        {
            WorldModel w = World();
            IronHub(w, 2000);
            SpatialNode dep = IronDeposit(w, "Dep", 10);
            dep.HoldStrength = 0;                        // mints nothing, so the hub still consumes net
            dep.Inventory[Iron] = 48000;

            Assert.True(Only(Plan(w), Iron).Paused);
        }

        // ---- the bound -------------------------------------------------------------------------

        [Fact]
        public void FreeDepositsAndTooFewDrills_AreHullBound()
        {
            WorldModel w = World();
            IronHub(w, 1e9);
            IronDeposit(w, "D1", 10);
            IronDeposit(w, "D2", 20);

            StandingExtractionOrder o = Only(Plan(w, Inputs(Drills(0.001))), Iron);
            Assert.Equal(ExtractionBound.HullBound, o.Bound);
            Assert.True(o.DeliverablePerHour > 0);
        }

        [Fact]
        public void FreeDepositsAndNoDrillAtAll_AreHullBound()
        {
            WorldModel w = World();
            IronHub(w, 1e9);
            IronDeposit(w, "D1", 10);

            Assert.Equal(ExtractionBound.HullBound, Only(Plan(w, Inputs(Drills())), Iron).Bound);
        }

        [Fact]
        public void EveryDepositCoolingDownOrWorked_IsDepositBound()
        {
            WorldModel w = World();
            IronHub(w, 1e9);
            SpatialNode cooling = IronDeposit(w, "D1", 10);
            IronDeposit(w, "D2", 20);
            cooling.MineCooldownUntilUtc = w.UtcNow.AddMinutes(5);
            ExtractionFleetIndex fleet = Drills(1e6, 1e6);
            fleet.WorkedDepositIds.Add("D2");

            StandingExtractionOrder o = Only(Plan(w, Inputs(fleet)), Iron);
            Assert.Equal(ExtractionBound.DepositBound, o.Bound);
            Assert.Equal(0, o.DeliverablePerHour, 6);
        }

        [Fact]
        public void NoDepositInTheCatchment_IsDepositBound()
        {
            WorldModel w = World();
            IronHub(w, 2000);

            Assert.Equal(ExtractionBound.DepositBound, Only(Plan(w, Inputs(Drills(1e6))), Iron).Bound);
        }

        [Fact]
        public void EveryFreeDepositDrilled_AndStillShort_IsDepositBound()
        {
            WorldModel w = World();
            IronHub(w, 1e12);
            IronDeposit(w, "D1", 10);
            IronDeposit(w, "D2", 20);

            StandingExtractionOrder o = Only(Plan(w, Inputs(Drills(1, 1, 1))), Iron);
            Assert.Equal(ExtractionBound.DepositBound, o.Bound);
        }

        [Fact]
        public void EnoughDrillsOnFreeDeposits_BindNothing()
        {
            WorldModel w = World();
            IronHub(w, 1e9);
            SpatialNode d1 = IronDeposit(w, "D1", 10);

            StandingExtractionOrder o = Only(Plan(w, Inputs(Drills(1e9))), Iron);
            Assert.Equal(ExtractionBound.None, o.Bound);

            // The sustained rate: drill x richness x density, halved by the rest a deposit owes.
            double factor = MineTargeting.NodeRichnessFactor(d1, w.Tuning,
                new Dictionary<string, double> { { Iron, 1.0 } }, w.Registry);
            Assert.True(factor > 0, "fixture: the deposit has to be drillable");
            double expected = 1e9 * factor / 2.0 * 3600.0;
            Assert.True(Math.Abs(o.DeliverablePerHour - expected) <= expected * 1e-12);
        }

        [Fact]
        public void TwoHubsCompetingForOneDrill_TheLargerGapTakesIt()
        {
            WorldModel w = World();
            IronHub(w, 1e9, "Big");
            SpatialNode small = Node(w, "Small", new Vec3(2000, 0, 0));
            small.DemandHistory[Iron] = 1e8 / 20.0;
            IronDeposit(w, "DBig", 10);
            IronDeposit(w, "DSmall", 2010);

            List<StandingExtractionOrder> orders = Plan(w, Inputs(Drills(1e6)));
            Assert.True(orders.Find(o => o.HubNodeId == "Big").DeliverablePerHour > 0);
            StandingExtractionOrder s = orders.Find(o => o.HubNodeId == "Small");
            Assert.Equal(0, s.DeliverablePerHour, 6);
            Assert.Equal(ExtractionBound.HullBound, s.Bound);
        }

        [Fact]
        public void TheFleetIndex_CountsParkedDrillsAndTheDepositsFleetsAreWorking()
        {
            WorldModel w = World();
            SpatialNode hub = IronHub(w, 2000);
            hub.ShipStock["Driller"] = 2;
            hub.ShipStock["Hauler"] = 5;
            var st = Npc();
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = Tag, TargetNodeId = "D1", CurrentStopKind = FleetStopKind.Mine,
            });
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = Tag, TargetNodeId = "Waypoint",
                CurrentStopKind = FleetStopKind.Navigation,
                RemainingStops = new List<FleetStop> { new FleetStop { Kind = FleetStopKind.Mine, NodeId = "D2" } },
            });
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = Tag, TargetNodeId = "D3", CurrentStopKind = FleetStopKind.Mine,
                Resolved = true,
            });

            ExtractionFleetIndex index = ExtractionFleetIndex.Build(st, new[] { hub }, new DrillCatalogue(), w.Tuning);

            Assert.Equal(2, index.IdleDrillLitersPerSecond.Count);
            Assert.Equal(DrillCatalogue.Score * w.Tuning.MineDrillLitersPerScorePoint * w.Tuning.MineDutyCycle,
                         index.IdleDrillLitersPerSecond[0], 6);
            Assert.Contains("D1", index.WorkedDepositIds);
            Assert.Contains("D2", index.WorkedDepositIds);
            Assert.DoesNotContain("D3", index.WorkedDepositIds);
            Assert.DoesNotContain("Waypoint", index.WorkedDepositIds);
        }

        private sealed class DrillCatalogue : IPrefabCatalogue
        {
            public const double Score = 6.859;   // one 1.9 m large drill
            public long CargoLiters(string prefabId) { return 50000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public double DrillYieldScore(string prefabId) { return prefabId == "Driller" ? Score : 0; }
            public double DryMassKg(string prefabId) { return 0; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
        }

        // ---- the demand term: a starved bank asks through its unmet input rows ------------------

        private const string Stone = "Ore:Stone";

        /// <summary>A stone hub whose refinery eats exactly what it receives: 1,000 an hour drawn,
        /// 1,000 an hour mined, nothing on the shelf.</summary>
        private static SpatialNode StarvedStoneHub(WorldModel w)
        {
            SpatialNode hub = Node(w, "Hub", new Vec3(0, 0, 0));
            hub.DemandHistory[Stone] = 1000 / 20.0;
            ExtractionFlow.CreditMined(hub, w.UtcNow, w.Tuning, Stone, 6000);
            return hub;
        }

        private static void PostInputRow(WorldModel w, string nodeId, string ore, long quantity,
                                         DemandClass cls = DemandClass.RecipeInput, bool chainSized = false)
        {
            w.Demand.Post(new DemandEntry
            {
                FactionTag = Tag, Kind = "Item:" + ore, NodeId = nodeId, Quantity = quantity,
                ExpiresUtc = w.UtcNow.AddHours(1), Class = cls, ChainSized = chainSized,
            });
        }

        [Fact]
        public void AStarvedHubWithAnUnmetInputRow_OpensAnOrder_RefillingWithinTheStockWindow()
        {
            WorldModel w = World();
            StarvedStoneHub(w);
            PostInputRow(w, "Hub", Stone, 24000);   // 24,000 over 24 h: +1,000 an hour

            StandingExtractionOrder o = Only(Plan(w), Stone);
            Assert.Equal(ExtractionPlanner.SourceDemand, o.ConsumeSource);
            Assert.Equal(24000, o.UnmetInputDemand, 6);
            Assert.Equal(2000, o.ConsumePerHour, 6);
            Assert.Equal(1000, o.GapPerHour, 6);
            Assert.True(o.TargetPerHour > 0);
            Assert.False(o.Paused);
        }

        [Fact]
        public void TheSameStarvedHubWithNoRow_OpensNothing()
        {
            WorldModel w = World();
            StarvedStoneHub(w);

            Assert.Empty(Plan(w));
        }

        [Fact]
        public void ARowFullyCoveredByStock_AddsNothing()
        {
            WorldModel w = World();
            SpatialNode hub = StarvedStoneHub(w);
            hub.Inventory[Stone] = 24000;
            PostInputRow(w, "Hub", Stone, 24000);

            Assert.Empty(Plan(w));
        }

        [Fact]
        public void ARowPartlyCoveredByStock_AddsOnlyTheRemainder()
        {
            WorldModel w = World();
            SpatialNode hub = StarvedStoneHub(w);
            hub.Inventory[Stone] = 12000;
            PostInputRow(w, "Hub", Stone, 24000);

            StandingExtractionOrder o = Only(Plan(w), Stone);
            Assert.Equal(12000, o.UnmetInputDemand, 6);
            Assert.Equal(1500, o.ConsumePerHour, 6);
        }

        [Fact]
        public void ARowCoveredByInboundDeliveries_AddsNothing()
        {
            WorldModel w = World();
            StarvedStoneHub(w);
            PostInputRow(w, "Hub", Stone, 24000);
            var fleet = new ExtractionFleetIndex();
            fleet.AddInbound("Hub", Stone, 24000);

            Assert.Empty(Plan(w, Inputs(fleet)));
        }

        [Fact]
        public void AStoreOrCatalogRow_IsNotAProductionInput()
        {
            WorldModel w = World();
            StarvedStoneHub(w);
            PostInputRow(w, "Hub", Stone, 24000, DemandClass.Catalog);

            Assert.Empty(Plan(w));
        }

        [Fact]
        public void AChainSizedRow_CountsWhateverClassItCarries()
        {
            WorldModel w = World();
            StarvedStoneHub(w);
            PostInputRow(w, "Hub", Stone, 24000, DemandClass.ShipBuild, chainSized: true);

            Assert.Equal(ExtractionPlanner.SourceDemand, Only(Plan(w), Stone).ConsumeSource);
        }

        [Fact]
        public void AHubThatHasNeverDrawn_ButHasAnUnmetRow_Orders()
        {
            WorldModel w = World();
            Node(w, "Hub", new Vec3(0, 0, 0));
            PostInputRow(w, "Hub", Stone, 24000);

            StandingExtractionOrder o = Only(Plan(w), Stone);
            Assert.Equal(1000, o.ConsumePerHour, 6);
            Assert.Equal(ExtractionPlanner.SourceDemand, o.ConsumeSource);
        }

        [Fact]
        public void AnIceRow_IsLeftToTheBurn()
        {
            WorldModel w = World();
            Node(w, "Hub", new Vec3(0, 0, 0));
            PostInputRow(w, "Hub", Ice, 24000);

            Assert.Empty(Plan(w));
        }

        [Fact]
        public void TheFleetIndex_CountsCargoAndUnminedDwellsInboundToTheirDeliveryStop()
        {
            var st = Npc();
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = Tag, TargetNodeId = "Hub", CurrentStopKind = FleetStopKind.Service,
                Cargo = new Dictionary<string, long> { { Stone, 500 } },
            });
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = Tag, TargetNodeId = "D1", CurrentStopKind = FleetStopKind.Mine,
                MineWeights = new Dictionary<string, double> { { Stone, 0.5 }, { Iron, 0.5 } }, MineTargetLiters = 1000,
                RemainingStops = new List<FleetStop> { new FleetStop { Kind = FleetStopKind.Service, NodeId = "Hub" } },
            });
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = Tag, TargetNodeId = "Hub", CurrentStopKind = FleetStopKind.Service,
                Cargo = new Dictionary<string, long> { { Stone, 9999 } },
            });

            ExtractionFleetIndex index = ExtractionFleetIndex.Build(st, null, null, TestSupport.Tuning(), new OneLitre());

            Assert.Equal(1000, index.InboundTo("Hub", Stone), 6);
            Assert.Equal(500, index.InboundTo("Hub", Iron), 6);
            Assert.Equal(0, index.InboundTo("D1", Stone), 6);
        }

        // ---- the ice path ------------------------------------------------------------------------

        [Fact]
        public void AnIceHub_ReadsTheIceItsDepotShareOfTheFuelBurnImplies()
        {
            WorldModel w = World();
            SpatialNode depotA = Node(w, "DepotA", new Vec3(0, 0, 0));
            depotA.Capabilities[NodeCapabilityKind.FuelDepot] = 1;
            SpatialNode depotB = Node(w, "DepotB", new Vec3(1000, 0, 0));
            depotB.Capabilities[NodeCapabilityKind.FuelDepot] = 3;
            SpatialNode port = Node(w, "Port", new Vec3(2000, 0, 0));
            // 60,000 L over the 6 h constant: 10,000 L of hydrogen an hour, burned where hulls leave.
            FuelDraw.CreditBurn(port, w.UtcNow, w.Tuning, Hydrogen, 60000);

            var ratio = new Dictionary<string, double> { { Hydrogen, 0.05 } };   // 25 ice -> 500 L
            List<StandingExtractionOrder> orders = Plan(w, Inputs(icePerGas: ratio));

            Assert.Equal(2, orders.Count);
            StandingExtractionOrder a = orders.Find(o => o.HubNodeId == "DepotA");
            StandingExtractionOrder b = orders.Find(o => o.HubNodeId == "DepotB");
            Assert.Equal(Ice, a.OreKey);
            Assert.Equal(125, a.ConsumePerHour, 6);   // 500 ice/h x 1/4 of the levels
            Assert.Equal(375, b.ConsumePerHour, 6);
            Assert.Equal(ExtractionPlanner.SourceBurn, a.ConsumeSource);
        }

        [Fact]
        public void AnIceHub_TakesMeasuredDepotDraw_WhenItExceedsTheBurnShare()
        {
            WorldModel w = World();
            SpatialNode depot = Node(w, "Depot", new Vec3(0, 0, 0));
            depot.Capabilities[NodeCapabilityKind.FuelDepot] = 1;
            depot.DemandHistory[Ice] = 50;              // 1,000 an hour drawn by the depot itself
            FuelDraw.CreditBurn(depot, w.UtcNow, w.Tuning, Hydrogen, 60000);

            var ratio = new Dictionary<string, double> { { Hydrogen, 0.05 } };   // burn implies 500
            StandingExtractionOrder o = Only(Plan(w, Inputs(icePerGas: ratio)), Ice);
            Assert.Equal(1000, o.ConsumePerHour, 6);
            Assert.Equal(ExtractionPlanner.SourceDraw, o.ConsumeSource);

            depot.DemandHistory[Ice] = 10;              // 200 an hour: now the burn is larger
            o = Only(Plan(w, Inputs(icePerGas: ratio)), Ice);
            Assert.Equal(500, o.ConsumePerHour, 6);
            Assert.Equal(ExtractionPlanner.SourceBurn, o.ConsumeSource);
        }

        [Fact]
        public void BurnWithNoDepot_ImpliesNoIceHub()
        {
            WorldModel w = World();
            SpatialNode port = Node(w, "Port", new Vec3(0, 0, 0));
            FuelDraw.CreditBurn(port, w.UtcNow, w.Tuning, Hydrogen, 60000);

            Assert.Empty(Plan(w, Inputs(icePerGas: new Dictionary<string, double> { { Hydrogen, 0.05 } })));
        }

        [Fact]
        public void TheDepotRatio_IsReadFromTheFuelDepotRecipe()
        {
            var recipe = new ConversionRecipe
            {
                BlueprintId = "Gas/OxygenGenerator/Hydrogen",
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Ice", Amount = 25.0 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_GasProperties", SubTypeId = "Hydrogen", Amount = 500.0 } },
                TimeSecs = 1.0f,
            };
            var pm = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.FuelDepot, new List<ConversionRecipe> { recipe } },
            });

            FactionTuning t = TestSupport.Tuning();
            MachineModel model = MachinePhysics.ResolveAll(null)[(int)NodeCapabilityKind.FuelDepot];
            Dictionary<string, double> ratio = pm.DepotIcePerGasUnit(t);

            Assert.Single(ratio);
            Assert.Equal(MachinePhysics.InputAmount(25.0, model, false) / (500.0 * model.OutputScale),
                         ratio[Hydrogen], 9);
        }

        // ---- the mined EMA -----------------------------------------------------------------------

        [Fact]
        public void AMineDelivery_CreditsTheHubsMinedRate()
        {
            WorldModel w = World();
            var actor = Npc();
            Node(w, "OreField1", new Vec3(0, 0, 0));
            SpatialNode dest = Node(w, "Refinery1", new Vec3(10, 0, 0));

            actor.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = Tag, TargetNodeId = "Refinery1", OriginNodeId = "OreField1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow,
                Cargo = new Dictionary<string, long> { { Iron, 600 } },
            });
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { actor }, w, w.Tuning,
                                                 new FixedRandom(0.0), new List<string>());

            Assert.Equal(600, dest.Inventory[Iron]);
            Assert.Equal(100, ExtractionFlow.MinedRateAt(dest, Iron, w.UtcNow, w.Tuning), 6);
            Assert.Equal(w.UtcNow, dest.MinedEmaAtUtc);
        }

        [Fact]
        public void TheMinedRate_DecaysByEOverItsConstant_AndAccumulates()
        {
            var node = new SpatialNode { Id = "N" };
            FactionTuning t = TestSupport.Tuning();
            t.ExtractionMinedEmaHours = 6.0;
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            ExtractionFlow.CreditMined(node, t0, t, Iron, 6000);
            Assert.Equal(1000, ExtractionFlow.MinedRateAt(node, Iron, t0, t), 6);
            Assert.Equal(1000 / Math.E, ExtractionFlow.MinedRateAt(node, Iron, t0.AddHours(6), t), 6);

            ExtractionFlow.CreditMined(node, t0.AddHours(6), t, Iron, 6000);
            Assert.Equal(1000 / Math.E + 1000, ExtractionFlow.MinedRateAt(node, Iron, t0.AddHours(6), t), 6);
        }

        [Fact]
        public void TheMinedRate_RoundTripsThroughTheBrainFile_AndIsOmittedWhenEmpty()
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(1, 2, 3), 0.5, "N1");
            n.OwnerFactionTag = "ACME";
            SpatialNode empty = graph.Add(new Vec3(4, 5, 6), 0.5, "N2");
            empty.OwnerFactionTag = "ACME";
            FactionTuning t = TestSupport.Tuning();
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            ExtractionFlow.CreditMined(n, t0, t, Iron, 6000);

            var st = new FactionState { Tag = "ACME", FactionId = 7, PersonalityId = "settler", RngSeed = 1 };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));
                string json = File.ReadAllText(Directory.GetFiles(dir, "*ACME*", SearchOption.AllDirectories)[0]);
                Assert.Equal(1, CountOf(json, "\"MinedEma\""));
                Assert.Equal(1, CountOf(json, "\"MinedEmaAtUtc\""));

                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), reloaded);
                Assert.Equal(1000, reloaded.Nodes["N1"].MinedEma[Iron], 6);
                Assert.Equal(t0, reloaded.Nodes["N1"].MinedEmaAtUtc);
                Assert.Empty(reloaded.Nodes["N2"].MinedEma);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        private static int CountOf(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }

        // ---- the trace ---------------------------------------------------------------------------

        [Fact]
        public void TheTrace_NamesEveryTerm_AndIsSilentUntilSwitchedOn()
        {
            WorldModel w = World();
            IronHub(w, 2000);
            List<StandingExtractionOrder> orders = Plan(w);

            var log = new List<string>();
            ExtractionPlanner.AppendTrace(log, Tag, orders);
            Assert.Empty(log);

            bool on; string what;
            Assert.True(DebugTraces.TryToggle("extraction", out on, out what));
            Assert.True(on);
            ExtractionPlanner.AppendTrace(log, Tag, orders);

            Assert.Equal(
                "EXTRACTION_PLAN NPC hub=Hub ore=Ore:Iron consume=2000 src=draw unmet=0 passive=0 mined=0 stock=0"
                + " gap=2000 target=2000 deliver=0 state=open bound=deposit reason=gap",
                Assert.Single(log));
        }

        [Fact]
        public void TheTrace_CompactsLargeFigures()
        {
            Assert.Equal("9999", ExtractionPlanner.Compact(9999));
            Assert.Equal("12.5k", ExtractionPlanner.Compact(12500));
            Assert.Equal("115M", ExtractionPlanner.Compact(115e6));
            Assert.Equal("-1.5G", ExtractionPlanner.Compact(-1.5e9));
        }
    }
}
