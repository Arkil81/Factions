using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-483 slice 6 — a carrier's route through an enemy's space is priced at the catch it would
    /// meet there, and the carrier accepts it, detours, takes an escort or re-picks its source,
    /// whichever costs least. What the enemy's space costs a faction's lanes weighs its conquests.
    /// </summary>
    public sealed class TransitRiskTests
    {
        private const string Hydrogen = "Gas:Hydrogen";
        private const string Uranium = "Ingot:Uranium";

        /// <summary>Hauler: unarmed carrier. Escort: 1000 DPS. Picket: the defender's 500 DPS hull.</summary>
        private sealed class Cat : IPrefabCatalogue, IFuelPrefabCatalogue
        {
            /// <summary>No scanned mass: nothing can be burned, so no fuel can be valued.</summary>
            public bool Unscanned;
            public long CargoLiters(string prefabId) { return prefabId == "Hauler" ? 1000000 : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return prefabId == "Hauler" ? 50000 : 80000; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                if (prefabId == "Escort") return new[] { new WeaponBand { Dps = 1000f, RangeM = 800 } };
                if (prefabId == "Picket") return new[] { new WeaponBand { Dps = 500f, RangeM = 800 } };
                return new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public double DrillYieldScore(string prefabId) { return 0; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                var bands = new List<ThrustBand>
                {
                    new ThrustBand
                    {
                        ThrusterType = "Hydrogen", ForceMagnitudeN = 7.2e6f,
                        EffectivenessAtMinInfluence = 1, EffectivenessAtMaxInfluence = 1,
                        MaxPowerMw = 7.5f, FuelId = Hydrogen, FuelEfficiency = 1f,
                        FuelEnergyDensityMwhPerL = 0.001556f,
                    }
                };
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    { ThrustDirection.Forward, bands }, { ThrustDirection.Backward, bands }, { ThrustDirection.Down, bands },
                };
            }
            public double DryMassKg(string prefabId) { return Unscanned ? 0 : prefabId == "Hauler" ? 1.0e6 : 5.0e5; }
            public FuelManifest FuelManifest(string prefabId)
            {
                return new FuelManifest { HasFuelData = true, ReactorFuelKey = Uranium, ReactorFuelCapacityKg = 100 };
            }
        }

        private sealed class FlatPrices : IItemPriceCatalogue
        {
            public long BasePrice(string subTypeId) { return 3; }
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        /// <summary>ENM holds S (0,0,0) and D (300,0,0); DEF holds X (150,0,0) between them, with a
        /// 500 DPS picket parked. The two stand at <paramref name="relation"/>. Fleets fly 10 km/min, so
        /// the 150 km of DEF space between S and D takes about 15 minutes.</summary>
        private static WorldModel World(out FactionState def, out FactionState enm, double relation = -0.6,
                                        double kmPerMin = 10)
        {
            WorldModel w = TestSupport.World();
            w.FleetTrespassOn = true;
            w.Tuning.FleetSpeedKmPerMin = kmPerMin;
            w.Tuning.FleetSpeedMinKmPerMin = kmPerMin;
            w.Planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            w.PrefabCatalogue = new Cat();
            w.PriceCatalogue = new FlatPrices();
            Node(w, "S", "ENM", new Vec3(0, 0, 0));
            Node(w, "D", "ENM", new Vec3(300, 0, 0));
            Node(w, "X", "DEF", new Vec3(150, 0, 0)).ShipStock["Picket"] = 1;
            def = new FactionState { Tag = "DEF", RngSeed = 1 };
            enm = new FactionState { Tag = "ENM", RngSeed = 1 };
            def.Relations["ENM"] = relation;
            enm.Relations["DEF"] = relation;
            w.TransitStates = new List<FactionState> { def, enm };
            return w;
        }

        private static SpatialNode Node(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.9;
            n.Radius = 20;
            return n;
        }

        private static PendingFleetAction Convoy(WorldModel w, string from = "S", string to = "D")
        {
            SpatialNode a = w.Graph.Nodes[from], b = w.Graph.Nodes[to];
            return new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "ENM", TargetTag = "OTH", FleetId = "T1",
                OriginNodeId = a.Id, TargetNodeId = b.Id, OriginPosKm = a.Position, TargetPosKm = b.Position,
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(30),
                ShipPrefabId = "Hauler", ConvoySize = 1, LastResolvedSegment = -1,
                Cargo = new Dictionary<string, long> { { "Ingot:Iron", 1000 } },
                CargoValue = 100000,
            };
        }

        private static List<Vec3> Line(WorldModel w, string from, string to)
        {
            return new List<Vec3> { w.Graph.Nodes[from].Position, w.Graph.Nodes[to].Position };
        }

        private static double Minutes(WorldModel w, List<Vec3> line)
        {
            return w.Tuning.TransitTimeMins(w.ZonesFor(line));
        }

        // ---- the risk ----------------------------------------------------------------------------

        [Fact]
        public void ALongStayInEnemySpace_IsCaughtAtTheEnemyNode_AndPricedAtTheConvoyVerdict()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            PendingFleetAction convoy = Convoy(w);
            List<Vec3> line = Line(w, "S", "D");

            TransitRisk.Crossing c = Assert.Single(TransitRisk.Assess(w, w.Tuning, convoy, line, Minutes(w, line)));
            Assert.Equal("DEF", c.OwnerTag);
            Assert.Equal("X", c.NodeId);
            Assert.True(c.Caught);
            Assert.True(c.Seconds > c.ShadowSeconds);
            Assert.Equal(500.0, c.CatcherDps, 3);

            // Unarmed against a picket: weaker, so half the cargo and the hull at 0.15, at even odds.
            double expected = w.Tuning.TransitCatchOdds
                              * (w.Tuning.RaidLootFraction * 100000 + w.Tuning.ConvoyWeakHullLossChance * 50000);
            Assert.Equal(expected, TransitRisk.RiskCredits(new[] { c }, 100000, 50000, 0, w.Tuning), 3);
        }

        [Fact]
        public void AFastCrossing_ANeutralOwner_AndTheSwitchOff_CostNothing()
        {
            FactionState def, enm;
            WorldModel fast = World(out def, out enm, kmPerMin: 500);
            List<Vec3> line = Line(fast, "S", "D");
            TransitRisk.Crossing passed = Assert.Single(TransitRisk.Assess(fast, fast.Tuning, Convoy(fast), line, Minutes(fast, line)));
            Assert.False(passed.Caught);
            Assert.Equal(0.0, TransitRisk.RiskCredits(new[] { passed }, 100000, 50000, 0, fast.Tuning));

            WorldModel neutral = World(out def, out enm, relation: 0.0);
            Assert.Empty(TransitRisk.Assess(neutral, neutral.Tuning, Convoy(neutral), line, Minutes(neutral, line)));

            WorldModel off = World(out def, out enm);
            off.FleetTrespassOn = false;
            Assert.Empty(TransitRisk.Assess(off, off.Tuning, Convoy(off), line, Minutes(off, line)));
        }

        [Fact]
        public void ArmedFleets_AreNotPricedAsCarriers()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            PendingFleetAction patrol = Convoy(w);
            patrol.Kind = ActionKind.Patrol;
            List<Vec3> line = Line(w, "S", "D");
            Assert.Empty(TransitRisk.Assess(w, w.Tuning, patrol, line, Minutes(w, line)));
        }

        [Fact]
        public void TheVerdictSetsTheLoss_StrongerLosesNothing_AndNoDefenderMeansNoCatch()
        {
            FactionTuning t = TestSupport.World().Tuning;
            var caught = new TransitRisk.Crossing { Caught = true, CatcherDps = 500 };
            double weaker = TransitRisk.LossCredits(caught, 1000, 2000, 0, t);
            double equal = TransitRisk.LossCredits(caught, 1000, 2000, 500, t);
            double stronger = TransitRisk.LossCredits(caught, 1000, 2000, 1000, t);
            Assert.Equal(t.TransitCatchOdds * (t.RaidLootFraction * 1000 + t.ConvoyWeakHullLossChance * 2000), weaker, 6);
            Assert.Equal(t.TransitCatchOdds * 0.5 * (t.RaidLootFraction * 1000 + t.ConvoyDuelHullLossChance * 2000), equal, 6);
            Assert.Equal(0.0, stronger);

            caught.CatcherDps = 0;
            Assert.Equal(0.0, TransitRisk.LossCredits(caught, 1000, 2000, 0, t));
            var free = new TransitRisk.Crossing { Caught = false, CatcherDps = 500 };
            Assert.Equal(0.0, TransitRisk.LossCredits(free, 1000, 2000, 0, t));
        }

        [Fact]
        public void TheRouteSampler_ReadsTheSameOwnerAsTheWholeField()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            var field = (InfluenceField)w.Influence;
            List<Vec3> line = Line(w, "S", "D");
            List<SpatialNode> reaching = field.NodesReaching(line);
            var scratch = new Dictionary<string, double>();
            for (double x = 0; x <= 300; x += 7.5)
            {
                var p = new Vec3(x, 0, 0);
                string a, b; double av, asv, bv, bsv;
                field.DominantAt(p, out a, out av, out asv);
                field.DominantAt(p, reaching, scratch, out b, out bv, out bsv);
                Assert.Equal(a, b);
                Assert.Equal(av, bv, 9);
                Assert.Equal(asv, bsv, 9);
            }
        }

        // ---- detour --------------------------------------------------------------------------

        private static TransitPlanner.LegPlan Plan(WorldModel w, PendingFleetAction convoy)
        {
            PlanetRoutePlan direct = PlanetRoutePlanner.BuildPlan(convoy.OriginPosKm, convoy.TargetPosKm, w.Planets,
                RouteHullProfile.Unknown, PlanetRouteOptions.FromTuning(w.Tuning), "k");
            return TransitPlanner.PlanLeg(w, w.Tuning, convoy, RouteHullProfile.Unknown, convoy.OriginPosKm,
                                          convoy.TargetPosKm, direct, "k");
        }

        [Fact]
        public void ADetourOutOfTheSpace_IsTaken_WhenItsFuelCostsLessThanTheCatch()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            TransitPlanner.LegPlan plan = Plan(w, Convoy(w));

            Assert.True(plan.FuelCredits > 0, "the fixture must price fuel or nothing can be weighed");
            Assert.Equal(TransitPlanner.Pick.Detour, plan.Pick);
            Assert.True(plan.Cost < plan.RiskCredits);
            Vec3 via = Assert.Single(plan.Route.Waypoints);
            Assert.True(Math.Abs(via.Y) >= 79.0, "the 2r swing still sits in DEF space; only 4r clears it");
            Assert.True(plan.Route.DistanceKm <= w.Tuning.TransitDetourMaxStretch * 300);
        }

        [Fact]
        public void WithNoDetourInsideTheStretchBound_OrNoFuelToValue_TheRouteIsAccepted()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            w.Tuning.TransitDetourMaxStretch = 1.0;
            TransitPlanner.LegPlan bounded = Plan(w, Convoy(w));
            Assert.Equal(TransitPlanner.Pick.Accept, bounded.Pick);
            Assert.Equal(bounded.RiskCredits, bounded.Cost);

            WorldModel unpriced = World(out def, out enm);
            ((Cat)unpriced.PrefabCatalogue).Unscanned = true;
            TransitPlanner.LegPlan blind = Plan(unpriced, Convoy(unpriced));
            Assert.Equal(TransitPlanner.Pick.Accept, blind.Pick);
            Assert.Equal(-1.0, blind.DetourCredits);
        }

        [Fact]
        public void ALegPlannedThroughEnemySpace_FliesTheDetour_LogsIt_AndChargesTheLane()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            var log = new List<string>();
            PendingFleetAction planned = PendingFleetResolver.PlanFleetLeg(Convoy(w), w, w.Tuning, false, log);

            Assert.Equal(FleetStopKind.Navigation, planned.CurrentStopKind);
            Assert.True(Math.Abs(planned.TargetPosKm.Y) >= 79.0);
            string line = Assert.Single(log, l => l.StartsWith("TRANSIT_LEG "));
            Assert.StartsWith("TRANSIT_LEG ENM fleet=[T1] kind=Trade to=D enemy=DEF node=X region=node:X", line);
            Assert.Contains(" pick=detour:4r", line);
            Assert.True(enm.TransitDenial.Weight("DEF", "node:X", w.UtcNow) > 0);

            WorldModel off = World(out def, out enm);
            off.FleetTrespassOn = false;
            var offLog = new List<string>();
            PendingFleetAction straight = PendingFleetResolver.PlanFleetLeg(Convoy(off), off, off.Tuning, false, offLog);
            Assert.Equal("D", straight.TargetNodeId);
            Assert.DoesNotContain(offLog, l => l.StartsWith("TRANSIT_"));
            Assert.Equal(0, enm.TransitDenial.Count);
        }

        // ---- escort ----------------------------------------------------------------------------

        [Fact]
        public void AnEscortThatOutgunsTheDefence_FliesWhenItsFuelCostsLessThanTheRisk()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            w.Tuning.TransitDetourMaxStretch = 1.0;
            w.Graph.Nodes["S"].ShipStock["Escort"] = 2;
            var log = new List<string>();
            var ctx = new LiveOperationsExecutionContext(w, enm, log);
            PendingFleetAction convoy = Convoy(w);

            Assert.Equal(TransitPlanner.Pick.Escort, TransitPlanner.ConsiderEscort(ref convoy, ctx, w, w.Tuning, "S", "D"));
            Assert.Equal(new List<string> { "Hauler", "Escort" }, convoy.Squadron);
            Assert.Equal(1, w.Graph.Nodes["S"].ShipStock["Escort"]);
            string line = Assert.Single(log, l => l.StartsWith("TRANSIT_DISPATCH "));
            Assert.Contains(" pick=escort escort=Escort", line);
            // Escorted, the convoy outguns the picket, so the leg carries no expected loss.
            Assert.Equal(TransitPlanner.Pick.None, Plan(w, convoy).Pick);
            Assert.True(w.TransitLedger.PenaltyKm("ENM", "S", "D", w.UtcNow) > 0);
        }

        [Fact]
        public void AnEscortedHaul_LandsBothHulls_AsThemselves()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            w.FleetTrespassOn = false;
            PendingFleetAction haul = Convoy(w);
            haul.Kind = ActionKind.Haul;
            haul.TargetTag = "ENM";
            haul.Squadron = new List<string> { "Hauler", "Escort" };
            haul.ConvoySize = 2;
            haul.DepartedUtc = w.UtcNow.AddMinutes(-40);
            haul.EstimatedArrivalUtc = w.UtcNow.AddMinutes(-1);
            enm.PendingFleets.Add(haul);

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, enm }, w, w.Tuning, new FixedRandom(0.5), log, null);

            Assert.Equal(1, w.Graph.Nodes["D"].ShipStock["Hauler"]);
            Assert.Equal(1, w.Graph.Nodes["D"].ShipStock["Escort"]);
        }

        [Fact]
        public void TheLastArmedHull_NeverLeavesAsAnEscort()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            w.Tuning.TransitDetourMaxStretch = 1.0;
            w.Graph.Nodes["S"].ShipStock["Escort"] = 1;
            var log = new List<string>();
            PendingFleetAction convoy = Convoy(w);

            Assert.Equal(TransitPlanner.Pick.Accept,
                TransitPlanner.ConsiderEscort(ref convoy, new LiveOperationsExecutionContext(w, enm, log), w, w.Tuning, "S", "D"));
            Assert.Null(convoy.Squadron);
            Assert.Equal(1, w.Graph.Nodes["S"].ShipStock["Escort"]);
            Assert.Contains(log, l => l.StartsWith("TRANSIT_DISPATCH ENM") && l.Contains(" pick=route "));
        }

        // ---- re-pick ---------------------------------------------------------------------------

        [Fact]
        public void ALaneThatPaidForEnemySpace_ReadsItsCostAsDistance_UntilACleanDispatchOrItsMemoryRunsOut()
        {
            var ledger = new TransitLedger();
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            ledger.Record("ENM", "S", "D", 5000, 10, now);
            Assert.True(ledger.HasAny("ENM"));
            Assert.Equal(500.0, ledger.PenaltyKm("ENM", "S", "D", now), 6);
            Assert.Equal(0.0, ledger.PenaltyKm("ENM", "S", "D", now + TransitLedger.Memory + TimeSpan.FromSeconds(1)));
            Assert.Equal(0.0, ledger.PenaltyKm("OTH", "S", "D", now));

            ledger.Record("ENM", "S", "D", 5000, 0, now);
            Assert.Equal(0.0, ledger.PenaltyKm("ENM", "S", "D", now));
            ledger.Record("ENM", "S", "D", 0, 10, now);
            Assert.False(ledger.HasAny("ENM"));
        }

        [Fact]
        public void TheHaulOrder_PutsACleanSourceFirst_OnlyWhenTheExtraDistanceCostsLessThanTheLane()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            SpatialNode clean = Node(w, "C", "ENM", new Vec3(300, 350, 0));   // 350 km from D; S is 300
            var nearestFirst = new List<SpatialNode> { w.Graph.Nodes["S"], clean };
            Vec3 dest = w.Graph.Nodes["D"].Position;

            Assert.Same(nearestFirst, TransitPlanner.ReorderByLane(nearestFirst, "ENM", "D", dest, w));
            w.TransitLedger.Record("ENM", "S", "D", 1000, 10, w.UtcNow);            // +100 km: S at 400
            Assert.Equal(new[] { "C", "S" }, TransitPlanner.ReorderByLane(nearestFirst, "ENM", "D", dest, w).Select(n => n.Id));
            w.TransitLedger.Record("ENM", "S", "D", 1000, 100, w.UtcNow);           // +10 km: S at 310
            Assert.Equal(new[] { "S", "C" }, TransitPlanner.ReorderByLane(nearestFirst, "ENM", "D", dest, w).Select(n => n.Id));
        }

        [Fact]
        public void ABuyer_FliesToTheFartherSeller_WhenTheNearerLaneCostMoreThanTheExtraDistance()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TradeTest.World();
            world.Tuning = tuning;
            world.SetTreasury("A", 1_000_000);
            world.SetTreasury("B", tuning.InsolvencyFloor);
            TradeTest.Node(world, "A1", "A").ShipStock["Trade"] = 2;
            TradeTest.Node(world, "B1", "B", x: 100).Inventory["Iron"] = 5000;
            TradeTest.Node(world, "B2", "B", x: -150).Inventory["Iron"] = 5000;
            var oracle = new TradeTest.NodeScarcityOracle()
                .Set("A1", ItemKind.Ore, 2.0).Set("B1", ItemKind.Ore, 0.1).Set("B2", ItemKind.Ore, 0.1);

            Func<WorldModel, List<string>, PendingFleetAction> buyOnce = (w, log) =>
            {
                FactionState a = TradeTest.State("A"), b = TradeTest.State("B");
                a.CentralBankRecentIncome = 1_000_000;
                b.CentralBankRecentIncome = 1_000_000;
                TradeTest.Whitelist(a, "B");
                TradeTest.Unserved(a, "A1", "Iron", 100);
                TradeTest.Manager(oracle).Tick(new List<FactionState> { a, b }, w, tuning, log);
                return Assert.Single(a.PendingFleets);
            };

            Assert.Equal("B1", buyOnce(world, new List<string>()).TargetNodeId);

            // The last flight to B1 paid 1000 credits for hostile space at 10 credits a km: 100 km,
            // so B1 reads 200 km against B2's 150.
            world.TransitLedger.Record("A", "B1", "A1", 1000, 10, world.UtcNow);
            foreach (SpatialNode n in world.Graph.Nodes.Values) n.ShipStock.Remove("Trade");
            world.Graph.Nodes["A1"].ShipStock["Trade"] = 2;
            var log2 = new List<string>();
            Assert.Equal("B2", buyOnce(world, log2).TargetNodeId);
            string line = Assert.Single(log2, l => l.StartsWith("TRANSIT_REPICK "));
            Assert.Equal("TRANSIT_REPICK A item=Iron to=A1 chose=B2 over=B1 penaltyKm=100 overKm=100 choseKm=150", line);
        }

        private static void PostIron(WorldModel w, string tag, string nodeId, long qty)
        {
            w.Demand.Post(new DemandEntry
            {
                FactionTag = tag, Kind = "Item:Ingot:Iron", NodeId = nodeId, Quantity = qty,
                ExpiresUtc = w.UtcNow.AddMinutes(5), Class = DemandClass.ShipBuild, FirstPostedUtc = w.UtcNow.AddHours(-9),
            });
        }

        [Fact]
        public void AHaulThroughEnemySpace_IsPricedAtDispatch_AndItsFirstLegFliesTheDetour()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            w.Graph.Nodes["S"].Inventory["Ingot:Iron"] = 5000;
            w.Graph.Nodes["S"].ShipStock["Hauler"] = 1;
            PostIron(w, "ENM", "D", 1000);

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { enm }, w, w.Tuning, log);

            PendingFleetAction haul = Assert.Single(enm.PendingFleets, f => f.Kind == ActionKind.Haul);
            Assert.Equal(FleetStopKind.Navigation, haul.CurrentStopKind);
            Assert.Contains(log, l => l.StartsWith("TRANSIT_DISPATCH ENM") && l.Contains(" from=S to=D enemy=DEF node=X"));
            Assert.Contains(log, l => l.StartsWith("TRANSIT_LEG ENM") && l.Contains(" pick=detour:"));
            Assert.True(w.TransitLedger.PenaltyKm("ENM", "S", "D", w.UtcNow) > 0);
        }

        [Fact]
        public void TheHaulMatcher_DrawsFromTheFartherSource_WhenTheNearerLaneCostMoreThanTheExtraDistance()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            w.FleetTrespassOn = false;
            Node(w, "N", "ENM", new Vec3(300, 40, 0)).Inventory["Ingot:Iron"] = 5000;   // 40 km from D
            Node(w, "F", "ENM", new Vec3(300, -90, 0)).Inventory["Ingot:Iron"] = 5000;  // 90 km from D
            w.Graph.Nodes["N"].ShipStock["Hauler"] = 1;
            w.Graph.Nodes["F"].ShipStock["Hauler"] = 1;
            PostIron(w, "ENM", "D", 1000);
            w.TransitLedger.Record("ENM", "N", "D", 1000, 10, w.UtcNow);                   // +100 km: N reads 140

            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { enm }, w, w.Tuning, log);

            PendingFleetAction haul = Assert.Single(enm.PendingFleets, f => f.Kind == ActionKind.Haul);
            Assert.Equal("F", haul.OriginNodeId);
            Assert.Contains(log, l => l == "TRANSIT_REPICK ENM item=Ingot:Iron to=D chose=F over=N penaltyKm=100 overKm=40 choseKm=90");
        }

        // ---- the lane's weight in war planning -------------------------------------------------

        [Fact]
        public void TheLaneWeight_Accumulates_HalvesEveryHalfLife_AndStaysBounded()
        {
            var book = new TransitDenialBook();
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(1000.0, book.Add("DEF", "node:X", 1000, now), 6);
            Assert.Equal(1500.0, book.Add("DEF", "node:X", 500, now), 6);
            Assert.Equal(750.0, book.Weight("DEF", "node:X", now + TransitDenialBook.HalfLife), 6);
            Assert.Equal(0.0, book.Weight("OTH", "node:X", now));

            for (int i = 0; i < TransitDenialBook.MaxEntries + 10; i++) book.Add("E" + i, "r", 10 + i, now);
            Assert.Equal(TransitDenialBook.MaxEntries, book.Count);
            Assert.Equal(1500.0, book.Weight("DEF", "node:X", now), 6);
        }

        [Fact]
        public void TheConquestBoost_IsHalfTheMaximum_WhenTheLanesHaveCostAConquest()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            w.Tuning.ConquerCost = 40000;
            SpatialNode x = w.Graph.Nodes["X"];
            NodeRegion region = NodeRegion.Of(x);
            double credits;
            Assert.Equal(0.0, FactionBrain.LaneConquestBoost(enm, x, region, w.Tuning, w.UtcNow, out credits));

            enm.TransitDenial.Add("DEF", FactionBrain.CampaignKeyFor(region, x), 40000, w.UtcNow);
            Assert.Equal(w.Tuning.LaneConquestQualityBoost / 2, FactionBrain.LaneConquestBoost(enm, x, region, w.Tuning, w.UtcNow, out credits), 6);
            Assert.Equal(40000.0, credits, 6);
        }

        [Fact]
        public void AGuardRobbingACarrier_ChargesTheVictimsLaneWithTheLoot()
        {
            FactionState def, enm;
            WorldModel w = World(out def, out enm);
            w.Tuning.InterceptWindowSeconds = 0;
            PendingFleetAction convoy = Convoy(w);
            convoy.OriginPosKm = convoy.TargetPosKm = w.Graph.Nodes["X"].Position;
            convoy.TargetNodeId = null;
            convoy.EstimatedArrivalUtc = w.UtcNow.AddHours(2);
            enm.PendingFleets.Add(convoy);
            def.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "DEF", TargetTag = "DEF", TargetNodeId = "X",
                OriginNodeId = "X", FleetId = "G1", AgainstIntruder = true,
                OriginPosKm = w.Graph.Nodes["X"].Position, TargetPosKm = w.Graph.Nodes["X"].Position,
                DepartedUtc = w.UtcNow.AddMinutes(-2), EstimatedArrivalUtc = w.UtcNow.AddMinutes(-1), Resolved = true,
                ShipPrefabId = "Picket", ConvoySize = 1,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 500f, RangeM = 800 } },
            });
            var states = new List<FactionState> { def, enm };
            var log = new List<string>();
            for (int i = 0; i < 60 && !log.Exists(l => l.StartsWith("RAID_LOOTS")); i++)
            {
                PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);
                w.UtcNow = w.UtcNow.AddSeconds(10);
            }

            Assert.Contains(log, l => l.StartsWith("TRESPASS_CATCH "));
            string loot = Assert.Single(log, l => l.StartsWith("RAID_LOOTS"));
            long taken = long.Parse(loot.Split(new[] { " loot=" }, StringSplitOptions.None)[1].Split(' ')[0]);
            Assert.True(taken > 0);
            Assert.Equal((double)taken, enm.TransitDenial.Weight("DEF", "node:X", w.UtcNow), 0);
        }
    }
}
