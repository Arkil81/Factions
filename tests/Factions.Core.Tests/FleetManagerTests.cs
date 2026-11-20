using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Verifies FleetManager Campaign dispatch logic:
    ///   - Patrol dispatched when patrol stock > 0 and threat > 0.5.
    ///   - Reinforce dispatched when hold < 0.6 and no patrol dispatched.
    ///   - Campaign priority ordering: Patrol first, then Reinforce.
    ///   - Campaign focus is created by brain and read by FleetManager.
    /// </summary>
    public class FleetManagerTests
    {
        private static WorldModel MakeWorld(int seed = 1)
            => TestSupport.World(seed);

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
                                           double hold = 0.8, double value = 0.6)
        {
            SpatialNode n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static FactionTuning FastFleetTuning()
        {
            var t = TestSupport.Tuning();
            t.PatrolCost = 1000;
            t.ReinforceCost = 500;
            t.ReinforceStep = 0.1;
            t.FleetSpeedKmPerMin = 1200.0; // instant
            t.CampaignCooldown = TimeSpan.FromHours(1);
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0; // disable spend-budget cap for these tests
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            return t;
        }

        /// <summary>Ship stock is unified and tag-based now (see ShipPool) — dispatch requires a
        /// prefab the catalogue actually classifies as combat-suitable, not just a nonzero count
        /// under the right role key. Tests that need Patrol stock to be dispatchable must wire a
        /// catalogue marking the stocked prefab armed.</summary>
        private sealed class FakePrefabCatalogue : IPrefabCatalogue
        {
            public readonly HashSet<string> ArmedPrefabs = new HashSet<string>();
            public readonly HashSet<string> CargoPrefabs = new HashSet<string>();
            public readonly Dictionary<string, double> DryMassKgByPrefab = new Dictionary<string, double>();
            public readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> ThrustBandsByPrefab
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>();
            public long CargoLiters(string prefabId) => CargoPrefabs.Contains(prefabId) ? 1000 : 0;
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
                => ArmedPrefabs.Contains(prefabId) ? new[] { new WeaponBand { Dps = 500, RangeM = 800 } } : new WeaponBand[0];
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }

            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands;
                return ThrustBandsByPrefab.TryGetValue(prefabId, out bands)
                    ? bands : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }

            public double DryMassKg(string prefabId)
            {
                double v;
                return DryMassKgByPrefab.TryGetValue(prefabId, out v) ? v : 0;
            }

            public double DrillYieldScore(string prefabId) { return 0; }

            /// <summary>Down-facing thrusters only, matching whichever vanilla-shaped curve
            /// <paramref name="atmosphericOnly"/> selects — Atmospheric (dead in deep space) or Ion
            /// (fine in deep space, weak at a planet surface).</summary>
            public void SetDownThrust(string prefabId, double dryMassKg, float forceN, bool atmosphericOnly)
            {
                DryMassKgByPrefab[prefabId] = dryMassKg;
                ThrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { atmosphericOnly
                        ? new ThrustBand { ThrusterType = "Atmospheric", ForceMagnitudeN = forceN,
                            MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
                            EffectivenessAtMinInfluence = 0f, EffectivenessAtMaxInfluence = 1f,
                            NeedsAtmosphereForInfluence = true }
                        : new ThrustBand { ThrusterType = "Ion", ForceMagnitudeN = forceN,
                            MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
                            EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 0.2f,
                            NeedsAtmosphereForInfluence = false },
                    },
                };
            }
        }

        private static void StockPatrolShip(WorldModel w, SpatialNode node, string prefabId = "PatrolCargoship")
        {
            node.ShipStock[prefabId] = 1;
            var cat = w.PrefabCatalogue as FakePrefabCatalogue;
            if (cat == null) w.PrefabCatalogue = cat = new FakePrefabCatalogue();
            cat.ArmedPrefabs.Add(prefabId);
        }

        [Fact]
        public void TuningDefaults_DefineNonZeroCampaignCooldown()
        {
            var t = TestSupport.Tuning();

            Assert.True(t.CampaignCooldown > TimeSpan.Zero,
                "CampaignCooldown must be present in shipped tuning_defaults.json; zero makes any stamped campaign expire immediately.");
        }

        [Fact]
        public void TuningDefaults_DefineSpendBudgetFractionsAndWindow()
        {
            var t = TestSupport.Tuning();

            Assert.True(t.ReinforceMaxIncomeFractionPerWindow > 0);
            Assert.True(t.ReinforceBudgetFloor > 0);
            Assert.True(t.PatrolMaxIncomeFractionPerWindow > 0);
            Assert.True(t.PatrolBudgetFloor > 0);
            Assert.True(t.TradeMaxIncomeFractionPerWindow > 0);
            Assert.True(t.TradeBudgetFloor > 0);
            Assert.True(t.SpendBudgetWindowSeconds > 0);
        }

        [Fact]
        public void TuningLoad_RepairsZeroCampaignCooldownOverride()
        {
            var t = TestSupport.TuningWithOverride("{ \"CampaignCooldown\": 0.0 }");

            Assert.True(t.CampaignCooldown > TimeSpan.Zero,
                "Existing live tuning.json files with CampaignCooldown=0 must be repaired during LoadTuning.");
        }

        [Fact]
        public void Campaign_PatrolDispatches_WhenPatrolStockAvailable_ThreatAboveThreshold()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var target = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.5, 0.6);
            target.Threat = 0.8;
            w.Graph.Connect("N001", "N002");

            // The hull sits at home and the circuit runs to the OTHER node: a patrol is a transit.
            StockPatrolShip(w, home);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N002");

            var log = new List<string>();
            var fm = new FleetManager();
            fm.Tick(new[] { st }, w, t, log);

            Assert.True(log.Any(m => m.Contains("PATROL_DISPATCH")));
            Assert.True(log.Any(m => m.Contains("PATROL_DISPATCH") && m.Contains("N001 -> N002")),
                "the source must not be the node being patrolled");
        }

        [Fact]
        public void Campaign_PatrolDispatch_RoutesAroundConfiguredPlanet()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10000);
            var t = FastFleetTuning();
            w.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 50 },
            });

            var home = AddNode(w, "N001", "F", new Vec3(-200, 0, 0), 0.9, 0.7);
            var target = AddNode(w, "N002", "F", new Vec3(200, 0, 0), 0.5, 0.6);
            target.Threat = 0.8;
            StockPatrolShip(w, home);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N002");

            new FleetManager().Tick(new[] { st }, w, t, new List<string>());

            Assert.Single(st.PendingFleets);
            Assert.Equal(FleetStopKind.Navigation, st.PendingFleets[0].CurrentStopKind);
            Assert.Null(st.PendingFleets[0].TargetNodeId);
        }

        [Fact]
        public void Campaign_PatrolDispatch_PrefersFartherRouteFeasibleStock_OverNearerRouteInfeasibleStock()
        {
            // Deep-space scenario (no planet wired -> NullPlanetCatalogue, gravity 0 everywhere):
            // the NEAR node only has an Atmospheric-only hull (dead thrust with no atmosphere, so
            // its dry TWR is 0 in deep space); the FAR node has an Ion-capable hull (full thrust in
            // deep space, TWR = +Infinity there). FindShipSource must skip the incapable near stock
            // and dispatch from the farther but actually-capable node.
            var w = MakeWorld();
            w.SetTreasury("F", 10000);
            var t = FastFleetTuning();

            var near = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var far  = AddNode(w, "N003", "F", new Vec3(1000, 0, 0), 0.9, 0.7);
            var target = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.5, 0.6);
            target.Threat = 0.8;
            w.Graph.Connect("N001", "N002");
            w.Graph.Connect("N003", "N002");

            var cat = new FakePrefabCatalogue();
            cat.ArmedPrefabs.Add("AtmoOnly");   // Patrol requires combat suitability (nonzero Dps)
            cat.ArmedPrefabs.Add("IonCapable");
            cat.SetDownThrust("AtmoOnly", dryMassKg: 1000, forceN: 20000, atmosphericOnly: true);
            cat.SetDownThrust("IonCapable", dryMassKg: 1000, forceN: 20000, atmosphericOnly: false);
            w.PrefabCatalogue = cat;
            near.ShipStock["AtmoOnly"] = 1;
            far.ShipStock["IonCapable"] = 1;

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.CampaignFocusRegion = "N001";
            st.CampaignFocusPriority = 0.7;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            string dispatchLine = log.FirstOrDefault(m => m.Contains("PATROL_DISPATCH"));
            Assert.NotNull(dispatchLine);
            Assert.Contains("N003 -> ", dispatchLine); // dispatched from the farther, route-capable node
            Assert.DoesNotContain("N001 -> ", dispatchLine);
        }

        [Fact]
        public void Campaign_PatrolDispatch_SamePlanetRouteInfeasible_PostsPlanetScopedInvestment()
        {
            // Only Ion stock exists network-wide; both origin and target sample real g=1 (inside a
            // real PlanetRecord's SurfaceRadius) -- Ion's ~20%-effectiveness-at-full-influence curve
            // fails the real MinDryTwr there, so ConsumeForRole must return 0 despite
            // HasSuitableForRole's route-blind check having already passed (FindShipSource falls
            // back to this same node since it's the only role-suitable candidate in the network).
            var w = MakeWorld();
            w.SetTreasury("F", 10000);
            var t = FastFleetTuning();
            w.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "Terra", Center = new Vec3(0, 0, 0), SurfaceRadius = 50, GravityRadius = 200 },
            });

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            home.Zone = NodeZone.Surface;
            home.PlanetId = "Terra";
            var target = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.5, 0.6);
            target.Threat = 0.8;
            target.Zone = NodeZone.Surface;
            target.PlanetId = "Terra";
            w.Graph.Connect("N001", "N002");
            // A Shipyard is only asked for where one could be installed: a founded station in the
            // region, and the prerequisite capability somewhere in the network (FAC-238).
            home.StableStationId = 6001;
            home.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            var cat = new FakePrefabCatalogue();
            cat.ArmedPrefabs.Add("IonOnly");
            cat.SetDownThrust("IonOnly", dryMassKg: 1000, forceN: 5000, atmosphericOnly: false); // TWR ~0.10 at g=1 -- fails
            w.PrefabCatalogue = cat;
            home.ShipStock["IonOnly"] = 1;

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N002");

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.Single(w.Investments.SnapshotFor("F"));
            InvestmentRequest req = w.Investments.SnapshotFor("F")[0];
            Assert.Equal(NodeCapabilityKind.Shipyard, req.Cap);
            Assert.Equal("Terra", req.TargetPlanetId);
            Assert.Contains(log, l => l.Contains("INVEST_REQUEST Shipyard planet=Terra"));

            // Live regression (2026-08-10): a route-infeasible-but-tag-suitable stock must abort the
            // whole dispatch, not just skip the investment-request branch. Before this fix, falling
            // through here spent PatrolCost, queued a phantom zero-ship/unknown-prefab PendingFleet
            // that later credited an unidentified "" ship into ShipStock on "return" (manufacturing
            // stock from nothing), and repeated every tick since the route infeasibility never clears.
            Assert.Equal(10000, w.GetTreasury("F"));
            Assert.Empty(st.PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("PATROL_DISPATCH"));

            // The capability investment alone gives a Shipyard no route to build for -- a
            // Ship:Patrol demand carrying the real origin/destination must accompany it so a
            // freshly-built hull is actually biased toward surviving this specific patrol leg.
            DemandEntry shipDemand = Assert.Single(w.Demand.SnapshotShipDemands("F"));
            Assert.Equal("Ship:Patrol", shipDemand.Kind);
            Assert.Equal("N001", shipDemand.NodeId);
            Assert.Equal("N002", shipDemand.DestNodeId);
        }

        [Fact]
        public void Campaign_PatrolCost_FeedsNetFlowOnNextAccrueIncome()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10000);
            var t = FastFleetTuning();
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0; // isolate: no CB grant income in net flow

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.9, 0.7);
            var target = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.5, 0.6);
            target.Threat = 0.8;
            w.Graph.Connect("N001", "N002");

            StockPatrolShip(w, home);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N002");

            new FleetManager().Tick(new[] { st }, w, t, new List<string>());
            w.AccrueIncome(new[] { st });

            // AddNode has no StableStationId, so no upkeep applies here — the patrol cost is the
            // only component.
            Assert.Equal(-t.PatrolCost, w.GetNetFlow("F"));
        }

        /// <summary>Hold maintenance runs on its own clock, independent of the campaign machinery
        /// beside it: a campaign focus is set here and the reinforce still dispatches and lands.
        /// <para>This fixture used to state the opposite premise -- that a reinforce fires where a
        /// patrol cannot for want of a hull. Since FAC-327 a reinforce IS a hull, so that case is
        /// now "no suitable hull anywhere", which
        /// <c>FleetManagerReinforceMissionTests.NoHullAnywhere_PostsAPatrolDemand_AndSpendsNothing</c>
        /// states directly.</para></summary>
        [Fact]
        public void Campaign_ReinforceDispatches_AlongsideACampaignFocus()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.3, 0.7);
            var threatNode = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.5, 0.6);
            w.Graph.Connect("N001", "N002");
            ReinforceMissionSupport.Stock(w, threatNode, 4);

            threatNode.Threat = 0.8;

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.CampaignFocusRegion = threatNode.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = home.Id, Score = 1.0 });

            var log = new List<string>();
            var fm = new FleetManager();
            var states = new List<FactionState> { st };
            fm.Tick(states, w, t, log);
            Assert.True(log.Any(m => m.Contains("REINFORCE_DISPATCH")));
            Assert.Equal(0.3, home.HoldStrength, 6);        // sent, not yet delivered

            ReinforceMissionSupport.FlyFleets(w, t, states, log);
            Assert.True(home.HoldStrength > 0.3);
        }

        [Fact]
        public void Campaign_PriorityOrdering_PatrolBeforeReinforce()
        {
            var w = MakeWorld();
            w.SetTreasury("F", 10000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.8, 0.7);
            var threatNode = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.8, 0.6);
            w.Graph.Connect("N001", "N002");

            threatNode.Threat = 0.8;
            StockPatrolShip(w, home);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.CampaignFocusRegion = threatNode.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var log = new List<string>();
            var fm = new FleetManager();
            fm.Tick(new[] { st }, w, t, log);

            Assert.True(log.Any(m => m.Contains("PATROL_DISPATCH")));
            Assert.False(log.Any(m => m.Contains("REINFORCE")));
        }

        [Fact]
        public void Campaign_Expires_AndPatrolStopsWithIt_RawThreatAloneIsNotATrigger()
        {
            // The inverse of what this asserted before FAC-309 slice 3, and the point of the slice:
            // the defensive focus IS the trigger, so a region whose focus has expired is no longer
            // patrolled however hot its nodes are. Raw threat orders a circuit, it does not summon
            // one -- Guard is what answers a node that is already under attack.
            var w = MakeWorld();
            w.SetTreasury("F", 10000);
            var t = FastFleetTuning();

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.3, 0.7);
            var threatNode = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.5, 0.6);
            w.Graph.Connect("N001", "N002");
            threatNode.Threat = 0.8;
            StockPatrolShip(w, home);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.CampaignFocusRegion = threatNode.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-70);

            var log = new List<string>();
            var fm = new FleetManager();
            fm.Tick(new[] { st }, w, t, log);

            Assert.False(log.Any(m => m.Contains("PATROL_DISPATCH")),
                "an expired focus leaves nothing to patrol under, however high the node's threat");
            Assert.False(log.Any(m => m.Contains("REINFORCE_DISPATCH")));
            Assert.True(log.Any(m => m.Contains("CAMPAIGN_EXPIRED")));
            Assert.Null(st.CampaignFocusRegion);
        }

        [Fact]
        public void Campaign_NoDispatch_WhenTreasuryBelowCost()
        {
            var w = MakeWorld();
            var t = FastFleetTuning();
            w.SetTreasury("F", 100);

            var home = AddNode(w, "N001", "F", new Vec3(0, 0, 0), 0.3, 0.7);
            var threatNode = AddNode(w, "N002", "F", new Vec3(10, 0, 0), 0.5, 0.6);
            w.Graph.Connect("N001", "N002");
            threatNode.Threat = 0.8;

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.CampaignFocusRegion = threatNode.Id;
            st.CampaignFocusPriority = 0.8;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var log = new List<string>();
            var fm = new FleetManager();
            fm.Tick(new[] { st }, w, t, log);

            Assert.False(log.Any(m => m.Contains("PATROL_DISPATCH")));
            Assert.False(log.Any(m => m.Contains("REINFORCE_DISPATCH")));
        }
    }
}
