using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-395 slice 3 (docs/mining-plan.md, "Capacity growth"): a hull-bound extraction order asks
    /// the shipyards for drill hulls sized to what it lacks; a deposit-bound one raises the signal for
    /// new ground and builds nothing.
    /// </summary>
    [Collection(DebugTracesCollection.Name)]
    public sealed class ExtractionCapacityTests : IDisposable
    {
        public void Dispose() { DebugTraces.ResetToDefaults(); }

        private const string Ice = "Ore:Ice";
        private const string Hydrogen = "Gas:Hydrogen";

        // ---- fixtures -------------------------------------------------------------------------------

        /// <summary>Spadi is cheapest; Rig is twice Spadi's drill rate per unit of BOM, so the Mine
        /// build picks Rig (slice 1) and the hull count is sized at Rig's rate.</summary>
        private sealed class Catalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) => prefabId == "Spadi" || prefabId == "Rig" ? 5000 : 0;
            public double DrillYieldScore(string prefabId) => prefabId == "Spadi" ? 1 : prefabId == "Rig" ? 200 : 0;
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

        private sealed class Recipes : IRecipeRegistry
        {
            public IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId)
            {
                long cost = outputSubTypeId == "Spadi" ? 10 : outputSubTypeId == "Rig" ? 1000
                          : outputSubTypeId == "WHI" ? 500 : 0;
                if (outputTypeId != "Prefab" || cost == 0) yield break;
                yield return new ConversionRecipe
                {
                    BlueprintId = outputSubTypeId,
                    Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Steel", Amount = cost } },
                    Outputs = new RecipeItem[0],
                };
            }
            public IEnumerable<ConversionRecipe> AllRecipes { get { yield break; } }
            public int Count => 2;
        }

        private sealed class Volumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string key) => 1.0;
        }

        private static WorldModel World()
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            w.Tuning.ExtractionMaxHullOrdersPerPass = 5;
            w.PrefabCatalogue = new Catalogue();
            w.Recipes = new Recipes();
            return w;
        }

        private static SpatialNode Station(WorldModel w, string id, double x, bool yard, string tag = "N")
        {
            SpatialNode n = w.Graph.Add(new Vec3(x, 0, 0), 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = 1.0;
            n.StableStationId = 42;
            if (yard) n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return n;
        }

        /// <summary>Hub (a yard itself) at 0, yards at 50 and 300, and a faction whose one ice order
        /// lacks <paramref name="rigHulls"/> Rig hulls' worth of rate across three free seams.</summary>
        private static WorldModel HullBoundEstate(double rigHulls, out FactionState st, out ShipyardManager mgr,
            bool player = false)
        {
            WorldModel w = World();
            Station(w, "Hub", 0, yard: true);
            Station(w, "Y2", 50, yard: true);
            Station(w, "Y3", 300, yard: true);
            st = new FactionState { Tag = "N", IsPlayerFaction = player };
            st.ExtractionOrders.Add(Order(ExtractionBound.HullBound, RigRate(w.Tuning) * rigHulls));
            mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Spadi", "Rig" } };
            return w;
        }

        /// <summary>One Rig hull's sustained litres per hour on a richness-1 seam, as the planner
        /// counts a drill-deposit pair.</summary>
        private static double RigRate(FactionTuning t)
        {
            double rest = (1 + Math.Max(0, t.MineOverburdenFraction)) * (1 + Math.Max(0, t.MineCooldownMultiplier));
            return 200 * t.MineDrillLitersPerScorePoint * t.MineDutyCycle / rest * 3600.0;
        }

        private static StandingExtractionOrder Order(ExtractionBound bound, double targetPerHour, int seams = 3)
        {
            return new StandingExtractionOrder
            {
                HubNodeId = "Hub",
                OreKey = Ice,
                ConsumePerHour = targetPerHour,
                TargetPerHour = targetPerHour,
                GapPerHour = targetPerHour,
                Bound = bound,
                Reason = ExtractionPlanner.ReasonGap,
                UnpairedDepositFactors = bound == ExtractionBound.HullBound
                    ? Enumerable.Repeat(1.0, seams).ToList()
                    : null,
            };
        }

        private static List<string> Tick(ShipyardManager mgr, WorldModel w, FactionState st)
        {
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, w, w.Tuning, log);
            return log;
        }

        private static List<string> MineYards(WorldModel w)
            => w.Graph.Nodes.Values.Where(n => n.BuildQueue.Any(e => e.Role == "Mine")).Select(n => n.Id)
                .OrderBy(id => id, StringComparer.Ordinal).ToList();

        private static List<DemandEntry> MineDemands(WorldModel w, string tag = "N")
            => w.Demand.SnapshotShipDemands(tag).Where(d => d.Kind == "Ship:Mine").ToList();

        // ---- the missing rate and the hull count ------------------------------------------------------

        [Fact]
        public void MissingRate_IsTargetLessDeliverableLessTheLargerOfMinedAndInFlight()
        {
            var o = new StandingExtractionOrder { TargetPerHour = 1000, DeliverablePerHour = 200, MinedPerHour = 300, InFlightPerHour = 100 };
            Assert.Equal(500, ExtractionCapacity.MissingPerHour(o), 6);
            o.InFlightPerHour = 450;
            Assert.Equal(350, ExtractionCapacity.MissingPerHour(o), 6);
            o.DeliverablePerHour = 900;
            Assert.Equal(0, ExtractionCapacity.MissingPerHour(o), 6);
        }

        [Fact]
        public void MissingRate_OfAPausedOrder_IsZero()
        {
            var o = new StandingExtractionOrder { TargetPerHour = 1000, Paused = true };
            Assert.Equal(0, ExtractionCapacity.MissingPerHour(o), 6);
        }

        [Fact]
        public void HullCount_RoundsUpOverTheRichestUnpairedSeams_AndStopsAtTheLastSeam()
        {
            FactionTuning t = TestSupport.Tuning();
            t.MineOverburdenFraction = 0;
            t.MineCooldownMultiplier = 1.0;
            // 100 L/s drill: 180,000 L/h on a richness-1 seam once the rest halves it, 90,000 at 0.5.
            var o = new StandingExtractionOrder
            {
                TargetPerHour = 200000, LitersPerUnit = 1,
                UnpairedDepositFactors = new List<double> { 1.0, 1.0, 0.5 },
            };
            Assert.Equal(2, ExtractionCapacity.HullsToCover(o, 100, t));
            o.TargetPerHour = 100;
            Assert.Equal(1, ExtractionCapacity.HullsToCover(o, 100, t));
            o.TargetPerHour = 360001;          // the third, poorer seam is what crosses it
            Assert.Equal(3, ExtractionCapacity.HullsToCover(o, 100, t));
            o.TargetPerHour = 1e12;            // never more hulls than free seams
            Assert.Equal(3, ExtractionCapacity.HullsToCover(o, 100, t));
        }

        [Fact]
        public void HullCount_WithNoSeamOrNothingMissing_IsZero_AndWithAnUnknownRate_IsOne()
        {
            FactionTuning t = TestSupport.Tuning();
            var o = new StandingExtractionOrder { TargetPerHour = 1000, UnpairedDepositFactors = new List<double> { 1.0 } };
            Assert.Equal(1, ExtractionCapacity.HullsToCover(o, 0, t));
            o.UnpairedDepositFactors = null;
            Assert.Equal(0, ExtractionCapacity.HullsToCover(o, 100, t));
            o.UnpairedDepositFactors = new List<double> { 1.0 };
            o.DeliverablePerHour = 1000;
            Assert.Equal(0, ExtractionCapacity.HullsToCover(o, 100, t));
        }

        // ---- hull-bound: proactive Ship:Mine ----------------------------------------------------------

        [Fact]
        public void HullBound_PostsOneShipMinePerMissingHull_AtTheYardsNearestTheHub()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);

            List<string> log = Tick(mgr, w, st);

            Assert.Equal(new[] { "Hub", "Y2" }, MineYards(w));
            Assert.All(w.Graph.Nodes.Values.SelectMany(n => n.BuildQueue).Where(e => e.Role == "Mine"),
                e => Assert.Equal("Rig", e.PrefabId));
            string line = Assert.Single(log, l => l.StartsWith("EXTRACTION_HULL_ORDER ", StringComparison.Ordinal));
            Assert.StartsWith("EXTRACTION_HULL_ORDER N hub=Hub ore=Ore:Ice missing=", line);
            Assert.Contains(" hulls=2 need=2 coming=0 yard=Hub,Y2 prefab=Rig", line);
        }

        [Fact]
        public void HullBound_IsCappedPerPass()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(2.5, out st, out mgr);
            w.Tuning.ExtractionMaxHullOrdersPerPass = 1;

            Tick(mgr, w, st);

            Assert.Equal(new[] { "Hub" }, MineYards(w));
        }

        [Fact]
        public void HullBound_CapZero_PostsNothing()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);
            w.Tuning.ExtractionMaxHullOrdersPerPass = 0;

            List<string> log = Tick(mgr, w, st);

            Assert.Empty(MineYards(w));
            Assert.DoesNotContain(log, l => l.StartsWith("EXTRACTION_HULL_ORDER", StringComparison.Ordinal));
        }

        [Fact]
        public void HullBound_WhileItsBuildsArePending_PostsNothingMore()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);
            Tick(mgr, w, st);

            List<string> second = Tick(mgr, w, st);

            Assert.Equal(new[] { "Hub", "Y2" }, MineYards(w));
            Assert.Equal(2, w.Graph.Nodes.Values.SelectMany(n => n.BuildQueue).Count(e => e.Role == "Mine"));
            Assert.DoesNotContain(second, l => l.StartsWith("EXTRACTION_HULL_ORDER", StringComparison.Ordinal));
        }

        [Fact]
        public void HullBound_AQueuedMineBuild_CountsTowardTheHulls_AndItsYardIsSkipped()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);
            var queued = new BuildQueueEntry { Id = "Mine:001", Role = "Mine", PrefabId = "Rig" };
            queued.Bom["Component:Steel"] = 1000;   // nothing collected, so it cannot complete this tick
            w.Graph.Nodes["Hub"].BuildQueue.Add(queued);

            List<string> log = Tick(mgr, w, st);

            Assert.Equal(new[] { "Hub", "Y2" }, MineYards(w));
            Assert.Equal(1, w.Graph.Nodes["Y2"].BuildQueue.Count(e => e.Role == "Mine"));
            Assert.Equal(1, w.Graph.Nodes["Hub"].BuildQueue.Count(e => e.Role == "Mine"));
            Assert.Contains(log, l => l.Contains(" hulls=1 need=2 coming=1 yard=Y2 "));
        }

        [Fact]
        public void HullBound_ALiveShipMineDemand_CountsAsPending()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);
            // A paused yard never turns its demand into an entry, so the demand alone is what pends.
            w.Graph.Nodes["Y3"].ShipyardPaused = true;
            w.Demand.Post(DemandEntry.ForShip("N", "Mine", "Y3", w.UtcNow.AddHours(1), destNodeId: "Hub", minCargoLiters: 1));

            List<string> log = Tick(mgr, w, st);

            Assert.Equal(new[] { "Hub" }, MineYards(w));
            Assert.Contains(log, l => l.Contains(" hulls=1 need=2 coming=1 yard=Hub "));
        }

        [Fact]
        public void HullBound_AnIdleDrillParkedAnywhere_CountsTowardTheHulls()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);
            w.Graph.Nodes["Y3"].ShipStock["Spadi"] = 1;   // built since the plan, not yet flown

            List<string> log = Tick(mgr, w, st);

            // Asserted on the demand, not the entry: the yard may answer it by relocating that drill.
            Assert.Equal(new[] { "Hub" }, MineDemands(w).Select(d => d.NodeId).ToArray());
            Assert.Contains(log, l => l.Contains(" hulls=1 need=2 coming=1 yard=Hub "));
        }

        [Fact]
        public void HullBound_TripsInFlight_ShrinkTheMissingRate()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);
            st.ExtractionOrders[0].InFlightPerHour = RigRate(w.Tuning);   // one hull's worth already out

            List<string> log = Tick(mgr, w, st);

            Assert.Equal(new[] { "Hub" }, MineYards(w));
            Assert.Contains(log, l => l.Contains(" hulls=1 need=1 coming=0 "));
        }

        [Fact]
        public void HullBound_NeverAsksForMoreHullsThanFreeSeams()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(10, out st, out mgr);
            st.ExtractionOrders[0].UnpairedDepositFactors = new List<double> { 1.0 };

            Tick(mgr, w, st);

            Assert.Equal(new[] { "Hub" }, MineYards(w));
        }

        [Fact]
        public void DepositBound_BuildsNothing()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);
            st.ExtractionOrders.Clear();
            StandingExtractionOrder order = Order(ExtractionBound.DepositBound, RigRate(w.Tuning) * 1.5);
            order.UnpairedDepositFactors = new List<double> { 1.0, 1.0 };   // the bound decides, not the seams
            st.ExtractionOrders.Add(order);

            List<string> log = Tick(mgr, w, st);

            Assert.Empty(MineYards(w));
            Assert.Empty(MineDemands(w));
            Assert.DoesNotContain(log, l => l.StartsWith("EXTRACTION_HULL_ORDER", StringComparison.Ordinal));
        }

        [Fact]
        public void BoundNone_BuildsNothing()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);
            st.ExtractionOrders.Clear();
            st.ExtractionOrders.Add(Order(ExtractionBound.None, RigRate(w.Tuning) * 1.5));

            Tick(mgr, w, st);

            Assert.Empty(MineYards(w));
            Assert.Empty(MineDemands(w));
        }

        [Fact]
        public void PlayerFaction_IsNeverBuiltFor_EvenWithAHullBoundOrder()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr, player: true);

            List<string> log = Tick(mgr, w, st);

            Assert.Empty(MineYards(w));
            Assert.Empty(MineDemands(w));
            Assert.DoesNotContain(log, l => l.StartsWith("EXTRACTION_HULL_ORDER", StringComparison.Ordinal));
        }

        // ---- the deposit-bound signal -----------------------------------------------------------------

        [Fact]
        public void GroundSignals_NameEachDepositBoundHub_AndKeepTheirFirstSince()
        {
            DateTime t0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            var deposit = Order(ExtractionBound.DepositBound, 1000);
            deposit.LitersPerUnit = 2.0;
            var orders = new List<StandingExtractionOrder>
            {
                deposit,
                Order(ExtractionBound.HullBound, 1000),
                Order(ExtractionBound.None, 1000),
            };
            orders[1].HubNodeId = "Other";
            orders[2].HubNodeId = "Third";

            List<ExtractionGroundSignal> first = ExtractionCapacity.DeriveGroundSignals(orders, null, t0);
            ExtractionGroundSignal s = Assert.Single(first);
            Assert.Equal("Hub", s.HubNodeId);
            Assert.Equal(Ice, s.OreKey);
            Assert.Equal(2000, s.MissingLitersPerHour, 6);
            Assert.Equal(t0, s.SinceUtc);

            List<ExtractionGroundSignal> later = ExtractionCapacity.DeriveGroundSignals(orders, first, t0.AddMinutes(3));
            Assert.Equal(t0, Assert.Single(later).SinceUtc);

            deposit.Paused = true;
            Assert.Empty(ExtractionCapacity.DeriveGroundSignals(orders, later, t0.AddMinutes(6)));
            deposit.Paused = false;
            Assert.Equal(t0.AddMinutes(9),
                Assert.Single(ExtractionCapacity.DeriveGroundSignals(orders, new List<ExtractionGroundSignal>(), t0.AddMinutes(9))).SinceUtc);
        }

        [Fact]
        public void GroundTrace_Format()
        {
            var s = new ExtractionGroundSignal
            {
                HubNodeId = "Depot", OreKey = Ice, MissingLitersPerHour = 2_500_000,
                SinceUtc = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc),
            };
            Assert.Equal("EXTRACTION_GROUND N hub=Depot ore=Ore:Ice missing=2.5M since=2026-09-27T12:00:00Z",
                ExtractionCapacity.FormatGroundTrace("N", s));
        }

        // ---- the planner's inputs -------------------------------------------------------------------

        private static WorldModel IronWorld()
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            FactionTuning t = w.Tuning;
            t.OpsIntervalSeconds = 180.0;
            t.EconomyIntervalSeconds = 10.0;
            t.MineSearchRadiusKm = 500.0;
            t.MineOverburdenFraction = 0.0;
            t.MineCooldownMultiplier = 1.0;
            SpatialNode hub = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "Hub");
            hub.OwnerFactionTag = "NPC";
            hub.HoldStrength = 1.0;
            hub.DemandHistory["Ore:Iron"] = 1e9 / 20.0;
            for (int i = 1; i <= 3; i++)
            {
                SpatialNode d = w.Graph.Add(new Vec3(10 * i, 0, 0), 0.5, "D" + i);
                d.OwnerFactionTag = "NPC";
                d.HoldStrength = 1.0;
                d.Radius = 40;
                TestSupport.WithOre(d, "Iron");
            }
            return w;
        }

        private static StandingExtractionOrder PlanIron(WorldModel w, ExtractionFleetIndex fleet)
        {
            List<StandingExtractionOrder> orders = ExtractionPlanner.Plan(new FactionState { Tag = "NPC" }, w, w.Tuning,
                w.UtcNow, new ExtractionPlanInputs { Volumes = new Volumes(), Fleet = fleet }, null);
            return Assert.Single(orders, o => o.OreKey == "Ore:Iron");
        }

        [Fact]
        public void Planner_AHullBoundOrder_CarriesItsUnpairedSeams_RichestFirst()
        {
            WorldModel w = IronWorld();
            var fleet = new ExtractionFleetIndex();
            fleet.IdleDrillLitersPerSecond.Add(0.001);

            StandingExtractionOrder o = PlanIron(w, fleet);

            Assert.Equal(ExtractionBound.HullBound, o.Bound);
            Assert.Equal(2, o.UnpairedDepositFactors.Count);
            Assert.True(o.UnpairedDepositFactors[0] >= o.UnpairedDepositFactors[1]);
            Assert.True(ExtractionCapacity.MissingPerHour(o) > 0);
        }

        [Fact]
        public void Planner_ADepositBoundOrder_CarriesNoSeams()
        {
            WorldModel w = IronWorld();
            var fleet = new ExtractionFleetIndex();
            fleet.IdleDrillLitersPerSecond.AddRange(new[] { 1.0, 1.0, 1.0, 1.0 });

            StandingExtractionOrder o = PlanIron(w, fleet);

            Assert.Equal(ExtractionBound.DepositBound, o.Bound);
            Assert.Null(o.UnpairedDepositFactors);
        }

        [Fact]
        public void Planner_InFlightRate_IsTheDwellRateOverTheCooldownRest()
        {
            WorldModel w = IronWorld();
            var st = new FactionState { Tag = "NPC" };
            // One trip: 7,200 L of iron over a one-hour dwell, delivering at the hub.
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine,
                CurrentStopKind = FleetStopKind.Mine,
                TargetNodeId = "D1",
                MineWeights = new Dictionary<string, double> { { "Ore:Iron", 1.0 } },
                MineTargetLiters = 7200,
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddHours(1),
                RemainingStops = new List<FleetStop> { new FleetStop { Kind = FleetStopKind.Service, NodeId = "Hub" } },
            });
            ExtractionFleetIndex fleet = ExtractionFleetIndex.Build(st, new List<SpatialNode>(), w.PrefabCatalogue,
                w.Tuning, new Volumes());
            Assert.Equal(7200, fleet.InFlightRateTo("Hub", "Ore:Iron"), 6);

            StandingExtractionOrder o = PlanIron(w, fleet);

            Assert.Equal(3600, o.InFlightPerHour, 6);   // cooldown multiplier 1.0 halves it
        }

        // ---- the Operations pass publishes both ---------------------------------------------------------

        private sealed class OpsCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) => prefabId == "WHI" ? 375_000 : 0;
            public double DrillYieldScore(string prefabId) => prefabId == "WHI" ? 128.6 : 0;
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

        /// <summary>A starved FuelDepot hub whose hulls burn far more hydrogen than any ice could
        /// replace, with <paramref name="iceDeposits"/> free ice seams in reach, no drill anywhere,
        /// and a yard.</summary>
        private static WorldModel StarvedDepot(int iceDeposits, out FactionState st, bool player = false)
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            w.Tuning = TestSupport.Tuning();
            w.Tuning.ExtractionMaxHullOrdersPerPass = 2;
            w.PrefabCatalogue = new OpsCatalogue();
            w.Recipes = new Recipes();
            st = new FactionState { Tag = "N", IsPlayerFaction = player };

            SpatialNode depot = Station(w, "Depot", 0, yard: false);
            depot.Capabilities[NodeCapabilityKind.FuelDepot] = 1;
            SpatialNode port = Station(w, "Port", 5, yard: false);
            FuelDraw.CreditBurn(port, w.UtcNow, w.Tuning, Hydrogen, 1e11);
            for (int i = 1; i <= iceDeposits; i++)
            {
                SpatialNode d = Station(w, "Ice" + i, 20 * i, yard: false);
                d.Richness = 1.0;
                d.Deposit = new NodeDeposit();
                d.Deposit.Entries.Add(new DepositEntry("Ice", 1.0, 100.0));
            }
            Station(w, "Yard", 80, yard: true);
            Station(w, "Yard2", 100, yard: true);
            return w;
        }

        private static OperationsManager Ops()
        {
            return new OperationsManager(itemVolumes: new Volumes())
            {
                IcePerGasUnitSource = t => new Dictionary<string, double> { { Hydrogen, 0.05 } },
            };
        }

        private static List<string> OpsTick(OperationsManager ops, WorldModel w, FactionState st)
        {
            var log = new List<string>();
            ops.Tick(new List<FactionState> { st }, w, w.Tuning, log);
            return log;
        }

        [Fact]
        public void OpsPass_ADepositBoundHub_PublishesItsGroundSignal_AndTracesItWhenArmed()
        {
            FactionState st;
            WorldModel w = StarvedDepot(0, out st);
            OperationsManager ops = Ops();
            DebugTraces.Extraction = true;

            List<string> log = OpsTick(ops, w, st);

            StandingExtractionOrder order = Assert.Single(st.ExtractionOrders);
            Assert.Equal(ExtractionBound.DepositBound, order.Bound);
            ExtractionGroundSignal s = Assert.Single(st.ExtractionGroundSignals);
            Assert.Equal("Depot", s.HubNodeId);
            Assert.Equal(Ice, s.OreKey);
            Assert.True(s.MissingLitersPerHour > 0);
            Assert.Equal(w.UtcNow, s.SinceUtc);
            Assert.Contains(log, l => l.StartsWith("EXTRACTION_GROUND N hub=Depot ore=Ore:Ice missing=", StringComparison.Ordinal));

            DateTime first = w.UtcNow;
            w.UtcNow = w.UtcNow.AddMinutes(3);
            OpsTick(ops, w, st);
            Assert.Equal(first, Assert.Single(st.ExtractionGroundSignals).SinceUtc);
        }

        [Fact]
        public void OpsPass_GroundTrace_IsOffByDefault()
        {
            FactionState st;
            WorldModel w = StarvedDepot(0, out st);

            List<string> log = OpsTick(Ops(), w, st);

            Assert.Single(st.ExtractionGroundSignals);
            Assert.DoesNotContain(log, l => l.StartsWith("EXTRACTION_GROUND", StringComparison.Ordinal));
        }

        [Fact]
        public void OpsPass_APlayerFaction_PublishesNeitherOrdersNorSignals()
        {
            FactionState st;
            WorldModel w = StarvedDepot(0, out st, player: true);

            OpsTick(Ops(), w, st);

            Assert.Empty(st.ExtractionOrders);
            Assert.Empty(st.ExtractionGroundSignals);
        }

        [Fact]
        public void EndToEnd_AHullBoundHub_GetsDrillHullsQueued_AndNoGroundSignal()
        {
            FactionState st;
            WorldModel w = StarvedDepot(3, out st);
            OperationsManager ops = Ops();
            OpsTick(ops, w, st);

            StandingExtractionOrder order = Assert.Single(st.ExtractionOrders);
            Assert.Equal(ExtractionBound.HullBound, order.Bound);
            Assert.Empty(st.ExtractionGroundSignals);

            // The walk that found no drill staged the dispatch fallback at the yard nearest the
            // seams; capacity growth counts it and asks the other yard for the next hull.
            Assert.Equal(new[] { "Yard" }, MineDemands(w).Select(d => d.NodeId).Distinct().ToArray());

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "WHI" } };
            List<string> log = Tick(mgr, w, st);

            Assert.Equal(new[] { "Yard", "Yard2" }, MineYards(w));
            Assert.Equal("WHI", Assert.Single(w.Graph.Nodes["Yard2"].BuildQueue, e => e.Role == "Mine").PrefabId);
            Assert.Contains(log, l => l.StartsWith("EXTRACTION_HULL_ORDER N hub=Depot ore=Ore:Ice ", StringComparison.Ordinal)
                                      && l.Contains(" coming=1 yard=Yard2 prefab=WHI"));
        }

        // ---- slice 3b: the drill-quality upgrade ------------------------------------------------------

        /// <summary>Litres per second of <paramref name="prefab"/> under the fixture catalogue.</summary>
        private static double DrillRate(FactionTuning t, string prefab)
            => new Catalogue().DrillYieldScore(prefab) * t.MineDrillLitersPerScorePoint * t.MineDutyCycle;

        private static ExtractionHull Hull(string prefab, double litersPerSecond, int count = 1)
            => new ExtractionHull { PrefabId = prefab, LitersPerSecond = litersPerSecond, Count = count };

        /// <summary>The <see cref="HullBoundEstate"/> yards, with one deposit-bound ice order worked by
        /// <paramref name="working"/>. The yard nearest the hub (the hub itself) picks Rig.</summary>
        private static WorldModel DepositBoundEstate(out FactionState st, out ShipyardManager mgr, bool player,
            params ExtractionHull[] working)
        {
            WorldModel w = HullBoundEstate(1.5, out st, out mgr, player);
            st.ExtractionOrders.Clear();
            StandingExtractionOrder order = Order(ExtractionBound.DepositBound, RigRate(w.Tuning) * 1.5);
            order.WorkingHulls = working.ToList();
            st.ExtractionOrders.Add(order);
            return w;
        }

        private static WorldModel SpadiWorkedEstate(out FactionState st, out ShipyardManager mgr, bool player = false)
        {
            FactionTuning t = TestSupport.Tuning();
            return DepositBoundEstate(out st, out mgr, player, Hull("Spadi", DrillRate(t, "Spadi"), 2));
        }

        private static List<string> Upgrades(List<string> log)
            => log.Where(l => l.StartsWith("EXTRACTION_HULL_UPGRADE", StringComparison.Ordinal)).ToList();

        [Fact]
        public void Upgrade_ADepositBoundHubWorkedBySpadis_QueuesOneRigAtTheNearestYard()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);

            List<string> log = Tick(mgr, w, st);

            Assert.Equal(new[] { "Hub" }, MineYards(w));
            Assert.Equal("Rig", Assert.Single(w.Graph.Nodes["Hub"].BuildQueue, e => e.Role == "Mine").PrefabId);
            string spadi = DrillRate(w.Tuning, "Spadi").ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            string rig = DrillRate(w.Tuning, "Rig").ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal("EXTRACTION_HULL_UPGRADE N hub=Hub ore=Ore:Ice working=Spadi@" + spadi + " hulls=2 pick=Rig@" + rig + " yard=Hub",
                Assert.Single(Upgrades(log)));
            Assert.DoesNotContain(log, l => l.StartsWith("EXTRACTION_HULL_ORDER", StringComparison.Ordinal));
        }

        [Fact]
        public void Upgrade_AtTheRatio_Fires_AndJustBelowIt_DoesNot()
        {
            FactionTuning t = TestSupport.Tuning();
            double rig = DrillRate(t, "Rig");
            FactionState st;
            ShipyardManager mgr;

            WorldModel at = DepositBoundEstate(out st, out mgr, false, Hull("Mid", rig / 2.0));
            Assert.Single(Upgrades(Tick(mgr, at, st)));

            WorldModel below = DepositBoundEstate(out st, out mgr, false, Hull("Mid", rig / 1.99));
            Assert.Empty(Upgrades(Tick(mgr, below, st)));
            Assert.Empty(MineYards(below));
            Assert.Empty(MineDemands(below));
        }

        [Fact]
        public void Upgrade_AHubAlreadyWorkedByThePick_AsksForNothing()
        {
            FactionTuning t = TestSupport.Tuning();
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = DepositBoundEstate(out st, out mgr, false, Hull("Rig", DrillRate(t, "Rig"), 2));

            Assert.Empty(Upgrades(Tick(mgr, w, st)));
            Assert.Empty(MineYards(w));
        }

        [Fact]
        public void Upgrade_WhileItIsPending_DoesNotFireAgain()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            Assert.Single(Upgrades(Tick(mgr, w, st)));

            List<string> second = Tick(mgr, w, st);

            Assert.Empty(Upgrades(second));
            Assert.Equal(1, w.Graph.Nodes.Values.SelectMany(n => n.BuildQueue).Count(e => e.Role == "Mine"));
            Assert.DoesNotContain(MineDemands(w), d => d.NodeId != "Hub");
        }

        [Fact]
        public void Upgrade_AFastHullBuiltAndNotYetFlown_CountsAsPending_ASlowOneDoesNot()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            w.Graph.Nodes["Y3"].ShipStock["Spadi"] = 3;   // parked slow hulls are no upgrade

            Assert.Single(Upgrades(Tick(mgr, w, st)));

            WorldModel w2 = SpadiWorkedEstate(out st, out mgr);
            w2.Graph.Nodes["Y3"].ShipStock["Rig"] = 1;    // an upgrade waiting for a seam to free

            Assert.Empty(Upgrades(Tick(mgr, w2, st)));
            Assert.Empty(MineDemands(w2));
        }

        [Fact]
        public void Upgrade_ASlowDrillParkedAtTheYard_DoesNotMeetIt_TheRigIsQueued()
        {
            // Live 09-27: METC's yards are its hubs, where its Spadis park. The upgrade demand was
            // retired as met by a Spadi the same tick and re-posted every pass, with no build.
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            w.Graph.Nodes["Hub"].ShipStock["Spadi"] = 2;

            List<string> log = Tick(mgr, w, st);

            Assert.Single(Upgrades(log));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_MET"));
            Assert.Equal("Rig", Assert.Single(w.Graph.Nodes["Hub"].BuildQueue, e => e.Role == "Mine").PrefabId);
            Assert.Equal(2, w.Graph.Nodes["Hub"].ShipStock["Spadi"]);

            List<string> second = Tick(mgr, w, st);
            Assert.Empty(Upgrades(second));
            Assert.DoesNotContain(second, l => l.StartsWith("EXTRACTION_HULL_UPGRADE_LAPSED", StringComparison.Ordinal));
        }

        [Fact]
        public void Upgrade_ASlowDrillElsewhere_IsNotRelocatedToAnswerIt()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            w.Graph.Nodes["Y3"].ShipStock["Spadi"] = 3;

            List<string> log = Tick(mgr, w, st);

            Assert.DoesNotContain(log, l => l.Contains("SHIP_RELOCATE_DISPATCHED"));
            Assert.Empty(st.PendingFleets);
            Assert.Equal("Rig", Assert.Single(w.Graph.Nodes["Hub"].BuildQueue, e => e.Role == "Mine").PrefabId);
        }

        [Fact]
        public void Upgrade_ADemandThatVanishesWithoutABuild_BacksOffAndSaysSo()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            DateTime t0 = w.UtcNow;
            Assert.Contains(" yard=Hub", Assert.Single(Upgrades(Tick(mgr, w, st))));

            // Gone by the next pass, demand and entry alike, with no hull: answered or dropped
            // some other way.
            w.Demand.ConsumeAll("N", "Ship:Mine", "Hub", false);
            w.Graph.Nodes["Hub"].BuildQueue.RemoveAll(e => e.Role == "Mine");
            w.UtcNow = t0.AddMinutes(3);
            List<string> second = Tick(mgr, w, st);
            Assert.Equal(new[] { "EXTRACTION_HULL_UPGRADE_LAPSED N hub=Hub ore=Ore:Ice yard=Hub posted=180s retry_in=1620s" },
                Upgrades(second));

            w.UtcNow = t0.AddMinutes(6);
            Assert.Empty(Upgrades(Tick(mgr, w, st)));      // still waiting, and said once

            w.UtcNow = t0.AddSeconds(w.Tuning.ExtractionUpgradeRetrySeconds);
            List<string> retry = Tick(mgr, w, st);
            Assert.Single(retry, l => l.StartsWith("EXTRACTION_HULL_UPGRADE N ", StringComparison.Ordinal));
        }

        [Fact]
        public void Upgrade_OneThatWasSeenComing_PostsAgainAtOnce_WhenTheHubIsStillSlow()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            Assert.Single(Upgrades(Tick(mgr, w, st)));
            w.UtcNow = w.UtcNow.AddMinutes(3);
            Assert.Empty(Upgrades(Tick(mgr, w, st)));      // covered by its build entry

            // The build is done and its hull gone to other work; the hub still flies Spadis.
            w.Graph.Nodes["Hub"].BuildQueue.RemoveAll(e => e.Role == "Mine");
            w.UtcNow = w.UtcNow.AddMinutes(3);
            List<string> third = Tick(mgr, w, st);

            Assert.Single(third, l => l.StartsWith("EXTRACTION_HULL_UPGRADE N ", StringComparison.Ordinal));
            Assert.DoesNotContain(third, l => l.StartsWith("EXTRACTION_HULL_UPGRADE_LAPSED", StringComparison.Ordinal));
        }

        [Fact]
        public void ShipDemand_ARefreshKeepsTheLargerDrillFloor()
        {
            var prior = DemandEntry.ForShip("N", "Mine", "Y", DateTime.MaxValue, minDrillYield: 50);
            var incoming = DemandEntry.ForShip("N", "Mine", "Y", DateTime.MaxValue, minDrillYield: 0);
            Assert.Equal(50, DemandEntry.PreserveOnRefresh(prior, incoming).MinDrillYield);
        }

        [Fact]
        public void Upgrade_ALiveShipMineDemand_CountsAsPending()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            w.Graph.Nodes["Y3"].ShipyardPaused = true;
            w.Demand.Post(DemandEntry.ForShip("N", "Mine", "Y3", w.UtcNow.AddHours(1), destNodeId: "Hub", minCargoLiters: 1));

            Assert.Empty(Upgrades(Tick(mgr, w, st)));
            Assert.Empty(MineYards(w));
        }

        [Fact]
        public void Upgrade_OnePendingUpgrade_CoversOneHubOnly()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            StandingExtractionOrder second = Order(ExtractionBound.DepositBound, RigRate(w.Tuning));
            second.HubNodeId = "Y3";
            second.WorkingHulls = new List<ExtractionHull> { Hull("Spadi", DrillRate(w.Tuning, "Spadi")) };
            st.ExtractionOrders.Add(second);
            w.Graph.Nodes["Y2"].ShipStock["Rig"] = 1;     // covers the neediest order, not both

            List<string> log = Tick(mgr, w, st);

            string line = Assert.Single(Upgrades(log));
            Assert.Contains(" hub=Y3 ", line);
            Assert.EndsWith(" yard=Y3", line);
        }

        private static StandingExtractionOrder SpadiOrder(WorldModel w, string hub, string ore, double rigHulls)
        {
            StandingExtractionOrder o = Order(ExtractionBound.DepositBound, RigRate(w.Tuning) * rigHulls);
            o.HubNodeId = hub;
            o.OreKey = ore;
            o.WorkingHulls = new List<ExtractionHull> { Hull("Spadi", DrillRate(w.Tuning, "Spadi")) };
            return o;
        }

        [Fact]
        public void Upgrade_IsCappedPerPass()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            w.Tuning.ExtractionMaxHullOrdersPerPass = 1;
            st.ExtractionOrders.Add(SpadiOrder(w, "Y2", Ice, 1.2));

            List<string> log = Tick(mgr, w, st);

            Assert.Contains(" hub=Hub ", Assert.Single(Upgrades(log)));
        }

        [Fact]
        public void Upgrade_SharesThePerPassCapWithHullBoundOrders()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            w.Tuning.ExtractionMaxHullOrdersPerPass = 2;
            st.ExtractionOrders.Add(SpadiOrder(w, "Y2", Ice, 1.2));
            st.ExtractionOrders.Add(SpadiOrder(w, "Y3", "Ore:Stone", 1.1));
            StandingExtractionOrder hull = Order(ExtractionBound.HullBound, RigRate(w.Tuning) * 0.5);
            hull.HubNodeId = "Y3";
            st.ExtractionOrders.Add(hull);

            List<string> log = Tick(mgr, w, st);

            // The hull-bound order takes one of the two; its pending build covers the neediest
            // upgrade, and the one demand left goes to the next.
            Assert.Single(log, l => l.StartsWith("EXTRACTION_HULL_ORDER", StringComparison.Ordinal));
            Assert.Contains(" hub=Y2 ", Assert.Single(Upgrades(log)));
        }

        // ---- one upgrade decision per hub -------------------------------------------------------------

        /// <summary>A deposit-bound order at <paramref name="hub"/> for <paramref name="ore"/>, worked by
        /// <paramref name="hulls"/> — pass the same instances to several orders for the hulls a hub's
        /// catchments share, as the planner does.</summary>
        private static StandingExtractionOrder HubOrder(WorldModel w, string hub, string ore, double rigHulls,
            params ExtractionHull[] hulls)
        {
            StandingExtractionOrder o = Order(ExtractionBound.DepositBound, RigRate(w.Tuning) * rigHulls);
            o.HubNodeId = hub;
            o.OreKey = ore;
            o.WorkingHulls = hulls.ToList();
            return o;
        }

        private static WorldModel MultiOreHub(out FactionState st, out ShipyardManager mgr, out ExtractionHull spadis,
            params string[] ores)
        {
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);
            st.ExtractionOrders.Clear();
            spadis = Hull("Spadi", DrillRate(w.Tuning, "Spadi"), 3);
            double gap = 1.5;
            foreach (string ore in ores)
            {
                st.ExtractionOrders.Add(HubOrder(w, "Hub", ore, gap, spadis));
                gap -= 0.1;
            }
            return w;
        }

        private static List<string> Lapses(List<string> log)
            => log.Where(l => l.StartsWith("EXTRACTION_HULL_UPGRADE_LAPSED", StringComparison.Ordinal)).ToList();

        [Fact]
        public void Upgrade_AThreeOreHub_PostsOneUpgrade_AndNoneLapseWhileItIsPending()
        {
            FactionState st;
            ShipyardManager mgr;
            ExtractionHull spadis;
            WorldModel w = MultiOreHub(out st, out mgr, out spadis, Ice, "Ore:Iron", "Ore:Stone");

            List<string> first = Tick(mgr, w, st);

            Assert.Equal("EXTRACTION_HULL_UPGRADE N hub=Hub ore=Ore:Ice,Ore:Iron,Ore:Stone working=Spadi@"
                         + Rate(DrillRate(w.Tuning, "Spadi")) + " hulls=3 pick=Rig@" + Rate(DrillRate(w.Tuning, "Rig"))
                         + " yard=Hub", Assert.Single(Upgrades(first)));
            Assert.Equal(new[] { "Hub" }, MineYards(w));

            for (int pass = 1; pass <= 5; pass++)
            {
                w.UtcNow = w.UtcNow.AddMinutes(3);
                Assert.Empty(Upgrades(Tick(mgr, w, st)));
            }
            Assert.Equal(1, w.Graph.Nodes.Values.SelectMany(n => n.BuildQueue).Count(e => e.Role == "Mine"));
        }

        [Fact]
        public void Upgrade_TwoHubs_StillPostTwo()
        {
            FactionState st;
            ShipyardManager mgr;
            ExtractionHull spadis;
            WorldModel w = MultiOreHub(out st, out mgr, out spadis, Ice, "Ore:Iron");
            ExtractionHull other = Hull("Spadi", DrillRate(w.Tuning, "Spadi"), 2);
            st.ExtractionOrders.Add(HubOrder(w, "Y3", Ice, 1.0, other));
            st.ExtractionOrders.Add(HubOrder(w, "Y3", "Ore:Iron", 0.9, other));

            List<string> log = Upgrades(Tick(mgr, w, st));

            Assert.Equal(2, log.Count);
            Assert.Contains(" hub=Hub ore=Ore:Ice,Ore:Iron ", log[0]);
            Assert.Contains(" hub=Y3 ore=Ore:Ice,Ore:Iron ", log[1]);
            Assert.Equal(new[] { "Hub", "Y3" }, MineYards(w));
        }

        [Fact]
        public void Upgrade_TheBackoffIsPerHub_WhateverOresItOrdersNext()
        {
            FactionState st;
            ShipyardManager mgr;
            ExtractionHull spadis;
            WorldModel w = MultiOreHub(out st, out mgr, out spadis, Ice, "Ore:Iron");
            DateTime t0 = w.UtcNow;
            Assert.Single(Upgrades(Tick(mgr, w, st)));

            // The upgrade vanishes without a build; the hub now orders a different set of ores.
            w.Demand.ConsumeAll("N", "Ship:Mine", "Hub", false);
            w.Graph.Nodes["Hub"].BuildQueue.RemoveAll(e => e.Role == "Mine");
            st.ExtractionOrders.RemoveAll(o => o.OreKey == "Ore:Iron");
            st.ExtractionOrders.Add(HubOrder(w, "Hub", "Ore:Stone", 1.2, spadis));
            st.ExtractionOrders.Add(HubOrder(w, "Y3", Ice, 1.0, Hull("Spadi", DrillRate(w.Tuning, "Spadi"))));
            w.UtcNow = t0.AddMinutes(3);

            List<string> second = Tick(mgr, w, st);

            Assert.Equal(new[] { "EXTRACTION_HULL_UPGRADE_LAPSED N hub=Hub ore=Ore:Ice,Ore:Iron yard=Hub posted=180s retry_in=1620s" },
                Lapses(second));
            string posted = Assert.Single(second, l => l.StartsWith("EXTRACTION_HULL_UPGRADE N ", StringComparison.Ordinal));
            Assert.Contains(" hub=Y3 ", posted);

            w.UtcNow = t0.AddMinutes(6);
            List<string> third = Tick(mgr, w, st);
            Assert.DoesNotContain(third, l => l.Contains(" hub=Hub "));
        }

        [Fact]
        public void Upgrade_LivePattern_N095_OneUpgradeForTheHub_NoLapses()
        {
            // METC N095, 09-28 17:59-19:29: Gold, Magnesium, Nickel, Iron and Silicon orders, a
            // different subset deposit-bound each pass. Per (hub, ore) that was 13 posts and 7 lapses.
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = HullBoundEstate(1.5, out st, out mgr);
            ExtractionHull spadis = Hull("Spadi-Atmo", DrillRate(w.Tuning, "Spadi"), 3);
            ExtractionHull buddies = Hull("Buddy", DrillRate(w.Tuning, "Spadi") * 2, 2);
            string[][] passes =
            {
                new[] { "Ore:Nickel" },
                new[] { "Ore:Gold" },
                new[] { "Ore:Magnesium", "Ore:Nickel" },
                new[] { "Ore:Magnesium", "Ore:Nickel", "Ore:Gold" },
                new[] { "Ore:Iron" },
                new[] { "Ore:Gold", "Ore:Magnesium" },
                new[] { "Ore:Iron", "Ore:Silicon" },
                new[] { "Ore:Silicon" },
                new[] { "Ore:Nickel", "Ore:Magnesium" },
                new[] { "Ore:Magnesium" },
            };
            var all = new List<string>();
            foreach (string[] ores in passes)
            {
                st.ExtractionOrders.Clear();
                double gap = 1.5;
                foreach (string ore in ores) { st.ExtractionOrders.Add(HubOrder(w, "Hub", ore, gap, spadis, buddies)); gap -= 0.1; }
                all.AddRange(Tick(mgr, w, st));
                w.UtcNow = w.UtcNow.AddMinutes(3);
            }

            Assert.Single(all, l => l.StartsWith("EXTRACTION_HULL_UPGRADE N ", StringComparison.Ordinal));
            Assert.Empty(Lapses(all));
        }

        [Fact]
        public void Upgrade_AHubIsShortWhenAnyOfItsOresIs()
        {
            FactionState st;
            ShipyardManager mgr;
            ExtractionHull spadis;
            WorldModel w = MultiOreHub(out st, out mgr, out spadis, Ice, "Ore:Iron");
            StandingExtractionOrder ice = st.ExtractionOrders[0];
            ice.InFlightPerHour = ice.TargetPerHour;                 // ice is covered; iron is not
            Assert.Equal(0, ExtractionCapacity.MissingPerHour(ice), 6);

            Assert.Contains(" hub=Hub ore=Ore:Ice,Ore:Iron ", Assert.Single(Upgrades(Tick(mgr, w, st))));

            WorldModel w2 = MultiOreHub(out st, out mgr, out spadis, Ice, "Ore:Iron");
            foreach (StandingExtractionOrder o in st.ExtractionOrders) o.InFlightPerHour = o.TargetPerHour;
            Assert.Empty(Upgrades(Tick(mgr, w2, st)));
        }

        [Fact]
        public void PooledWorkingHulls_CountsAHullSharedByTwoOresOnce_AndSkipsOrdersThatAreNotDepositBound()
        {
            ExtractionHull shared = Hull("S", 1, 2);
            ExtractionHull own = Hull("F", 10);
            var a = new StandingExtractionOrder { Bound = ExtractionBound.DepositBound, WorkingHulls = new List<ExtractionHull> { shared } };
            var b = new StandingExtractionOrder { Bound = ExtractionBound.DepositBound, WorkingHulls = new List<ExtractionHull> { shared, own } };
            var paused = new StandingExtractionOrder { Bound = ExtractionBound.DepositBound, Paused = true,
                WorkingHulls = new List<ExtractionHull> { Hull("X", 100, 9) } };

            List<ExtractionHull> pooled = ExtractionCapacity.PooledWorkingHulls(new[] { a, b, paused });

            Assert.Equal(new[] { "S", "F" }, pooled.Select(h => h.PrefabId).ToArray());
            Assert.Equal("S", ExtractionCapacity.UpgradeBasis(pooled).PrefabId);   // S,S,F
        }

        private static string Rate(double litersPerSecond)
            => litersPerSecond.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        [Fact]
        public void Upgrade_ARatioAtOrBelowOne_IsOff()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            w.Tuning.ExtractionUpgradeRateRatio = 1.0;

            Assert.Empty(Upgrades(Tick(mgr, w, st)));
            Assert.Empty(MineYards(w));
        }

        [Fact]
        public void Upgrade_AnOrderTheTripsInFlightAlreadyCover_AsksForNothing()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            StandingExtractionOrder o = st.ExtractionOrders[0];
            o.InFlightPerHour = o.TargetPerHour;

            Assert.Empty(Upgrades(Tick(mgr, w, st)));
        }

        [Fact]
        public void Upgrade_APausedOrHullBoundOrder_IsNoCandidate()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr);
            st.ExtractionOrders[0].Paused = true;
            Assert.Empty(Upgrades(Tick(mgr, w, st)));

            WorldModel w2 = SpadiWorkedEstate(out st, out mgr);
            st.ExtractionOrders[0].Bound = ExtractionBound.None;
            Assert.Empty(Upgrades(Tick(mgr, w2, st)));
        }

        [Fact]
        public void Upgrade_PlayerFaction_IsNeverBuiltFor()
        {
            FactionState st;
            ShipyardManager mgr;
            WorldModel w = SpadiWorkedEstate(out st, out mgr, player: true);

            List<string> log = Tick(mgr, w, st);

            Assert.Empty(Upgrades(log));
            Assert.Empty(MineYards(w));
            Assert.Empty(MineDemands(w));
        }

        [Fact]
        public void UpgradeBasis_IsTheUpperMedian_CountingEachHull()
        {
            Assert.Null(ExtractionCapacity.UpgradeBasis(null));
            Assert.Null(ExtractionCapacity.UpgradeBasis(new List<ExtractionHull>()));
            Assert.Equal("S", ExtractionCapacity.UpgradeBasis(new[] { Hull("S", 1) }).PrefabId);
            // One slow, one fast: the fast ones are as many as the slow, so nothing more is wanted.
            Assert.Equal("F", ExtractionCapacity.UpgradeBasis(new[] { Hull("S", 1), Hull("F", 10) }).PrefabId);
            Assert.Equal("S", ExtractionCapacity.UpgradeBasis(new[] { Hull("F", 10), Hull("S", 1, 2) }).PrefabId);
            Assert.Equal("S", ExtractionCapacity.UpgradeBasis(new[] { Hull("S", 1, 3), Hull("F", 10, 2) }).PrefabId);
            Assert.Equal("F", ExtractionCapacity.UpgradeBasis(new[] { Hull("S", 1, 3), Hull("F", 10, 3) }).PrefabId);
        }

        [Fact]
        public void Planner_ADepositBoundOrder_CarriesTheHullsWorkingItsCatchment()
        {
            WorldModel w = IronWorld();
            w.PrefabCatalogue = new Catalogue();
            var st = new FactionState { Tag = "NPC" };
            w.Graph.Nodes["D1"].ShipStock["Spadi"] = 2;            // parked on a catchment deposit
            w.Graph.Nodes["Hub"].ShipStock["Spadi"] = 1;           // parked at the hub
            SpatialNode far = w.Graph.Add(new Vec3(10000, 0, 0), 0.5, "Far");
            far.OwnerFactionTag = "NPC";
            far.ShipStock["Rig"] = 1;                              // outside the catchment
            w.Graph.Nodes["D2"].MineCooldownUntilUtc = w.UtcNow.AddHours(1);
            w.Graph.Nodes["D3"].MineCooldownUntilUtc = w.UtcNow.AddHours(1);
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine,
                ShipPrefabId = "Rig",
                CurrentStopKind = FleetStopKind.Service,
                TargetNodeId = "Hub",
                Cargo = new Dictionary<string, long> { { "Ore:Iron", 5000 } },
            });
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine,
                ShipPrefabId = "Spadi",
                CurrentStopKind = FleetStopKind.Service,
                TargetNodeId = "Elsewhere",                         // bound for another hub
                Cargo = new Dictionary<string, long> { { "Ore:Iron", 5000 } },
            });
            var owned = w.Graph.Nodes.Values.Where(n => n.OwnerFactionTag == "NPC").ToList();
            ExtractionFleetIndex fleet = ExtractionFleetIndex.Build(st, owned, w.PrefabCatalogue, w.Tuning, new Volumes());

            StandingExtractionOrder o = PlanIron(w, fleet);

            Assert.Equal(ExtractionBound.DepositBound, o.Bound);
            Assert.Equal(3, o.WorkingHulls.Where(h => h.PrefabId == "Spadi").Sum(h => h.Count));
            Assert.Equal(1, o.WorkingHulls.Where(h => h.PrefabId == "Rig").Sum(h => h.Count));
            Assert.Equal("Spadi", ExtractionCapacity.UpgradeBasis(o.WorkingHulls).PrefabId);
        }

        [Fact]
        public void Planner_AHullBoundOrder_CarriesNoWorkingHulls()
        {
            WorldModel w = IronWorld();
            var fleet = new ExtractionFleetIndex();
            fleet.IdleDrillLitersPerSecond.Add(0.001);

            Assert.Null(PlanIron(w, fleet).WorkingHulls);
        }

        // ---- slice 3b: the ground-signal grace window -------------------------------------------------

        private const double Grace = 1800;

        private static List<StandingExtractionOrder> GroundOrders()
            => new List<StandingExtractionOrder> { Order(ExtractionBound.DepositBound, 1000) };

        /// <summary>Signals at t0, runs <paramref name="gap"/> for passes t0+3m .. t0+3m×<paramref name="offPasses"/>,
        /// then signals again one pass later, and returns that signal's since.</summary>
        private static DateTime SinceAfterGap(Action<List<StandingExtractionOrder>> gap, int offPasses, DateTime t0,
            double grace = Grace, int minutesPerPass = 3)
        {
            var memory = new Dictionary<string, ExtractionGroundMemo>(StringComparer.Ordinal);
            List<ExtractionGroundSignal> prev = ExtractionCapacity.DeriveGroundSignals(GroundOrders(), null, memory, t0, grace);
            int pass = 1;
            for (; pass <= offPasses; pass++)
            {
                List<StandingExtractionOrder> off = GroundOrders();
                gap(off);
                prev = ExtractionCapacity.DeriveGroundSignals(off, prev, memory, t0.AddMinutes(minutesPerPass * pass), grace);
                Assert.Empty(prev);
            }
            return Assert.Single(ExtractionCapacity.DeriveGroundSignals(GroundOrders(), prev, memory,
                t0.AddMinutes(minutesPerPass * pass), grace)).SinceUtc;
        }

        public static IEnumerable<object[]> GapCauses()
        {
            yield return new object[] { "in-flight covers the target" };
            yield return new object[] { "bound flipped to none" };
            yield return new object[] { "order absent" };
            yield return new object[] { "order paused" };
        }

        private static Action<List<StandingExtractionOrder>> Cause(string cause)
        {
            switch (cause)
            {
                case "in-flight covers the target": return os => os[0].InFlightPerHour = os[0].TargetPerHour;
                case "bound flipped to none": return os => os[0].Bound = ExtractionBound.None;
                case "order absent": return os => os.Clear();
                case "order paused": return os => os[0].Paused = true;
                default: throw new ArgumentException(cause);
            }
        }

        [Theory]
        [MemberData(nameof(GapCauses))]
        public void Grace_KeepsSinceAcrossAOneOrTwoPassGap_WhateverDroppedTheSignal(string cause)
        {
            DateTime t0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(t0, SinceAfterGap(Cause(cause), 1, t0));
            Assert.Equal(t0, SinceAfterGap(Cause(cause), 2, t0));
        }

        [Fact]
        public void Grace_BridgesTheLongestMeasuredGap()
        {
            DateTime t0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            // ~27 min between two signalled passes: eight passes off at 3 min, signalled at +27 min.
            Assert.Equal(t0, SinceAfterGap(Cause("order absent"), 8, t0));
        }

        /// <summary>Replays one live key's signalled passes (box-local times, 09-27/28, v0.374.0, one
        /// process) with every Operations pass between them unsignalled.</summary>
        private static List<DateTime> ReplaySince(params string[] signalledAt)
        {
            var memory = new Dictionary<string, ExtractionGroundMemo>(StringComparer.Ordinal);
            DateTime day = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
            var sinces = new List<DateTime>();
            List<ExtractionGroundSignal> prev = null;
            DateTime last = default(DateTime);
            foreach (string at in signalledAt)
            {
                DateTime t = day.Add(TimeSpan.Parse(at.StartsWith("24:") ? "1." + "00" + at.Substring(2) : at,
                    System.Globalization.CultureInfo.InvariantCulture));
                // The passes in between, ~180 s apart, with nothing signalled.
                if (last != default(DateTime))
                    for (DateTime p = last.AddSeconds(180); p < t.AddSeconds(-90); p = p.AddSeconds(180))
                        prev = ExtractionCapacity.DeriveGroundSignals(new List<StandingExtractionOrder>(), prev, memory, p, Grace);
                prev = ExtractionCapacity.DeriveGroundSignals(GroundOrders(), prev, memory, t, Grace);
                sinces.Add(Assert.Single(prev).SinceUtc);
                last = t;
            }
            return sinces;
        }

        [Fact]
        public void Grace_LivePattern_KeepsSinceAcrossEveryGapInsideTheWindow_AndResetsOnlyPastIt()
        {
            // MEMN N020 Ore:Stone: gaps of 542, 1,796 and 540 s between signals, one since throughout.
            List<DateTime> memn = ReplaySince("23:13:17", "23:22:19", "23:35:54", "24:05:50", "24:14:50", "24:23:50");
            Assert.All(memn, s => Assert.Equal(memn[0], s));

            // UNGD N034 Ore:Cobalt: 1,801 s off is one second past the window, and starts a new clock.
            List<DateTime> ungd = ReplaySince("24:02:59", "24:05:54", "24:35:55");
            Assert.Equal(ungd[0], ungd[1]);
            Assert.NotEqual(ungd[0], ungd[2]);
        }

        [Fact]
        public void Grace_ResetsSinceAfterTheWindow_AndForgetsTheHub()
        {
            DateTime t0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            var memory = new Dictionary<string, ExtractionGroundMemo>(StringComparer.Ordinal);
            ExtractionCapacity.DeriveGroundSignals(GroundOrders(), null, memory, t0, Grace);
            Assert.Single(memory);

            ExtractionCapacity.DeriveGroundSignals(new List<StandingExtractionOrder>(), null, memory, t0.AddSeconds(Grace), Grace);
            Assert.Single(memory);                        // the window's edge is still inside it
            ExtractionCapacity.DeriveGroundSignals(new List<StandingExtractionOrder>(), null, memory, t0.AddSeconds(Grace + 1), Grace);
            Assert.Empty(memory);

            DateTime back = t0.AddSeconds(Grace + 180);
            Assert.Equal(back, Assert.Single(ExtractionCapacity.DeriveGroundSignals(GroundOrders(), null, memory, back, Grace)).SinceUtc);
        }

        [Fact]
        public void Grace_AGapLongerThanTheWindow_ResetsSince_EvenWithNoPassBetween()
        {
            DateTime t0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            // One pass off, but the passes are 20 min apart: 40 min between signals is past the window.
            Assert.Equal(t0.AddMinutes(40), SinceAfterGap(Cause("order paused"), 1, t0, Grace, 20));
        }

        [Fact]
        public void Grace_Zero_ResetsAfterOnePassOff_AsSlice3Shipped()
        {
            DateTime t0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(t0.AddMinutes(6), SinceAfterGap(Cause("order absent"), 1, t0, 0));
            // Consecutive passes still carry it.
            var memory = new Dictionary<string, ExtractionGroundMemo>(StringComparer.Ordinal);
            List<ExtractionGroundSignal> first = ExtractionCapacity.DeriveGroundSignals(GroundOrders(), null, memory, t0, 0);
            Assert.Equal(t0, Assert.Single(ExtractionCapacity.DeriveGroundSignals(GroundOrders(), first, memory,
                t0.AddMinutes(3), 0)).SinceUtc);
        }

        [Fact]
        public void OpsPass_ADepositBoundHubPausedForAPass_KeepsItsSince()
        {
            FactionState st;
            WorldModel w = StarvedDepot(0, out st);
            OperationsManager ops = Ops();
            DateTime t0 = w.UtcNow;
            OpsTick(ops, w, st);
            Assert.Equal(t0, Assert.Single(st.ExtractionGroundSignals).SinceUtc);

            // Stock at the ceiling pauses the order: no signal this pass.
            StandingExtractionOrder open = Assert.Single(st.ExtractionOrders);
            w.UtcNow = t0.AddMinutes(3);
            w.Graph.Nodes["Depot"].Inventory[Ice] = (long)(open.ConsumePerHour * w.Tuning.ExtractionStockHours * 2);
            OpsTick(ops, w, st);
            Assert.True(Assert.Single(st.ExtractionOrders).Paused);
            Assert.Empty(st.ExtractionGroundSignals);

            // Drawn down again: the order resumes deposit-bound, and the signal keeps its first since.
            w.UtcNow = t0.AddMinutes(6);
            w.Graph.Nodes["Depot"].Inventory.Remove(Ice);
            OpsTick(ops, w, st);
            Assert.Equal(ExtractionBound.DepositBound, Assert.Single(st.ExtractionOrders).Bound);
            Assert.Equal(t0, Assert.Single(st.ExtractionGroundSignals).SinceUtc);
        }
    }
}
