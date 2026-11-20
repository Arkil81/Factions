using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers the Magnate-tier player-faction autonomy gate (see docs/economy-territory.md ->
    /// "Player-faction claiming"): FactionState.IsPlayerFaction should disable autonomous manager
    /// behavior via FactionLifecycle.IsAutonomyDisabled, WITHOUT being treated as IsDormant (which
    /// would also incorrectly exclude the faction from rival/relations/influence math).
    /// </summary>
    public class FactionAutonomyTests
    {
        private sealed class FakePrefabCatalogue : IPrefabCatalogue
        {
            public readonly HashSet<string> ArmedPrefabs = new HashSet<string>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
                => ArmedPrefabs.Contains(prefabId) ? new[] { new WeaponBand { Dps = 500, RangeM = 800 } } : new WeaponBand[0];
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
                => new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        [Fact]
        public void IsAutonomyDisabled_TrueOnlyForPlayerFactions()
        {
            var npc = new FactionState { Tag = "NPC" };
            var player = new FactionState { Tag = "EXP", IsPlayerFaction = true };

            Assert.False(FactionLifecycle.IsAutonomyDisabled(npc));
            Assert.True(FactionLifecycle.IsAutonomyDisabled(player));
        }

        [Fact]
        public void PlayerFaction_IsNotDormant_SoRivalRelationsInfluenceMathStillSeesIt()
        {
            var w = TestSupport.World();
            var player = new FactionState { Tag = "EXP", IsPlayerFaction = true };

            Assert.False(FactionLifecycle.IsDormant(player, w.UtcNow));
        }

        [Fact]
        public void FleetManager_SkipsPlayerFaction_EvenWhenPatrolConditionsAreOtherwiseMet()
        {
            var w = TestSupport.World();
            w.SetTreasury("EXP", 10000);
            var t = TestSupport.Tuning();
            t.PatrolCost = 1000;
            t.FleetSpeedKmPerMin = 1200.0;
            t.CampaignCooldown = System.TimeSpan.FromHours(1);
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;

            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 0.7, "N001");
            home.OwnerFactionTag = "EXP"; home.HoldStrength = 0.9; home.Radius = 40; home.Threat = 0.8;
            SpatialNode target = w.Graph.Add(new Vec3(10, 0, 0), 0.6, "N002");
            target.OwnerFactionTag = "EXP"; target.HoldStrength = 0.5; target.Radius = 40;
            w.Graph.Connect("N001", "N002");

            const string prefabId = "PatrolCargoship";
            home.ShipStock[prefabId] = 1;
            var cat = new FakePrefabCatalogue();
            cat.ArmedPrefabs.Add(prefabId);
            w.PrefabCatalogue = cat;

            var st = new FactionState { Tag = "EXP", RngSeed = 1, IsPlayerFaction = true };
            st.CampaignFocusRegion = "N001";
            st.CampaignFocusPriority = 0.7;
            st.CampaignSetAtUtc = w.UtcNow.AddMinutes(-10);

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.DoesNotContain(log, m => m.Contains("PATROL_DISPATCH"));
        }

        // ---- self-sufficient managers now run for player factions (2026-08-16) -------------
        // Production/Shipyard/Store/Operations are driven by DemandRegister/InvestmentRegister/
        // scarcity signals a manager posts to itself, never anything FactionBrain.Think computes,
        // so they no longer skip IsAutonomyDisabled factions -- unlike FleetManager/TradeManager
        // above, which stay gated off (their ReinforcePriority/TradePriority inputs are brain-only
        // and would leave them enabled but inert).

        [Fact]
        public void ProductionManager_DoesNotSkipPlayerFaction_RecipeActuallyRuns()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "EXP";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Ingot:Iron"] = 1000;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "EXP",
                Kind = "Item:Comp:SteelPlate",
                NodeId = "N1",
                Quantity = 300,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate",
                        TimeSecs = 1,
                        Inputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 },
                        },
                        Outputs = new[]
                        {
                            new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 },
                        },
                    },
                },
            };

            var manager = new ProductionManager(capRecipes);
            var states = new List<FactionState> { new FactionState { Tag = "EXP", IsPlayerFaction = true } };
            var log = new List<string>();
            for (int second = 1; second <= 10; second++)
            {
                world.UtcNow = world.UtcNow.AddSeconds(1);
                manager.Tick(states, world, world.Tuning, log);
            }

            long ironAfter;
            node.Inventory.TryGetValue("Ingot:Iron", out ironAfter);
            Assert.True(ironAfter < 1000, "a player faction's Manufactory should have consumed Ingot:Iron, not sat idle");
        }

        [Fact]
        public void ShipyardManager_DoesNotSkipPlayerFaction_PostsProactiveDemand()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "EXP";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            var state = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            List<DemandEntry> demands = world.Demand.SnapshotShipDemands("EXP");
            Assert.Contains(demands, d => d.Kind == "Ship:Trade" && d.NodeId == "N1");
        }

        [Fact]
        public void StoreStockManager_DoesNotSkipPlayerFaction_PostsRestockDemand()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Registry = new NullItemRegistry();
            world.Catalogue = new TuningItemCatalogue(world.Tuning);

            SpatialNode yard = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Yard");
            yard.OwnerFactionTag = "EXP";
            yard.HoldStrength = 1.0;
            yard.StableStationId = 42;
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var tuning = new FactionTuning
            {
                StoreShipSlotsSmallGrid = 5,
                StoreShipSlotsLargeGrid = 2,
                StoreStockIntervalSeconds = 60,
            };
            var state = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            var log = new List<string>();
            new StoreStockManager().Tick(new List<FactionState> { state }, world, tuning, log);

            List<DemandEntry> demands = world.Demand.SnapshotShipDemands("EXP");
            // The role is chosen by StoreShelfPlan now, so the point of this test -- a player
            // faction's store is not skipped -- is asserted on the purpose and the slot size.
            Assert.Contains(demands, d => d.DestinedForSale && d.NodeId == "Yard"
                                          && d.RequiredGridSize == "Small");
        }

        [Fact]
        public void OperationsManager_DoesNotSkipPlayerFaction_DispatchesHaul()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = new FakeAllCargoPrefabCatalogue();

            SpatialNode source = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Source");
            source.OwnerFactionTag = "EXP";
            source.HoldStrength = 1.0;
            source.Inventory["Explosives"] = 1000;
            source.ShipStock["Hauler"] = 1;

            SpatialNode need = world.Graph.Add(new Vec3(1, 0, 0), 1.0, "Need");
            need.OwnerFactionTag = "EXP";
            need.HoldStrength = 1.0;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "EXP",
                Kind = "Item:Explosives",
                NodeId = "Need",
                Quantity = 500,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var state = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { state }, world, TestSupport.Tuning(), log);

            Assert.Contains(state.PendingFleets, f => f.Kind == ActionKind.Haul && f.TargetNodeId == "Need");
        }

        // ---- reservation opt-in gate (2026-08-16) -------------------------------------------
        // ProductionManager still runs for player factions (above), but must never post an
        // InvestmentRequest -- and therefore never reserve real components -- for a capability the
        // player hasn't explicitly opted into via FactionState.ReservationOptIn (the !factions
        // station develop <capability> reserve toggle). NPC factions are unaffected either way.

        [Fact]
        public void CanPostInvestmentFor_AlwaysTrueForNpcFaction()
        {
            var npc = new FactionState { Tag = "NPC" };
            Assert.True(FactionLifecycle.CanPostInvestmentFor(npc, NodeCapabilityKind.Shipyard));
        }

        [Fact]
        public void CanPostInvestmentFor_FalseForPlayerFaction_UntilOptedIn()
        {
            var player = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            Assert.False(FactionLifecycle.CanPostInvestmentFor(player, NodeCapabilityKind.Shipyard));

            player.ReservationOptIn.Add(NodeCapabilityKind.Shipyard);
            Assert.True(FactionLifecycle.CanPostInvestmentFor(player, NodeCapabilityKind.Shipyard));
            Assert.False(FactionLifecycle.CanPostInvestmentFor(player, NodeCapabilityKind.RefineryII),
                "opting into one capability must not opt into another");
        }

        [Fact]
        public void ProductionManager_DoesNotPostInvestmentRequest_ForPlayerFaction_WithoutOptIn()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "EXP";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            // No capabilities installed -- ProductionManager's throughput-bootstrap pass would
            // normally request Refinery for any faction in this state.

            var manager = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            var states = new List<FactionState> { new FactionState { Tag = "EXP", IsPlayerFaction = true } };
            var log = new List<string>();
            manager.Tick(states, world, world.Tuning, log);

            Assert.DoesNotContain(world.Investments.SnapshotFor("EXP"), r => r.Cap == NodeCapabilityKind.Refinery);
            Assert.DoesNotContain(log, l => l.Contains("THROUGHPUT_INVEST") && l.Contains("Refinery"));
        }

        [Fact]
        public void ProductionManager_PostsInvestmentRequest_ForPlayerFaction_OnceOptedIn()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "EXP";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;

            var manager = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            var playerState = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            playerState.ReservationOptIn.Add(NodeCapabilityKind.Refinery);
            var states = new List<FactionState> { playerState };
            var log = new List<string>();
            manager.Tick(states, world, world.Tuning, log);

            Assert.Contains(world.Investments.SnapshotFor("EXP"), r => r.Cap == NodeCapabilityKind.Refinery);
            Assert.Contains(log, l => l.Contains("THROUGHPUT_INVEST") && l.Contains("Refinery"));
        }

        // ---- reservation opt-in keep-alive (2026-08-18) ---------------------------------------
        // ReservationOptIn only ever permitted the *reactive* passes above to post an
        // InvestmentRequest -- toggling it on did nothing by itself if no unmet demand or
        // throughput imbalance ever happened to trigger one, forever, with zero feedback. A
        // player-driven opt-in now keeps a standing low-urgency request alive on its own.

        [Fact]
        public void ProductionManager_PostsKeepAliveInvestmentRequest_ForOptedInCapability_WithNoReactiveSignal()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "EXP";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            // No capabilities installed, no Item demand posted -- ManufactoryII has no reactive
            // trigger here: PostCapabilityInvestmentRequests needs unmet demand (none posted), and
            // PostThroughputInvestmentRequests' Manufactory-chain bootstrap only fires once ingot
            // income is already flowing, which requires an installed, active Refinery (also absent).

            var manager = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            var playerState = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            playerState.ReservationOptIn.Add(NodeCapabilityKind.ManufactoryII);
            var states = new List<FactionState> { playerState };
            var log = new List<string>();
            manager.Tick(states, world, world.Tuning, log);

            Assert.Contains(world.Investments.SnapshotFor("EXP"), r => r.Cap == NodeCapabilityKind.ManufactoryII);
            Assert.Contains(log, l => l.Contains("RESERVE_KEEPALIVE") && l.Contains("ManufactoryII"));
        }

        [Fact]
        public void ProductionManager_KeepAlive_DoesNotOverwriteHigherUrgencyReactiveRequest()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "EXP";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            // No Refinery installed -- PostThroughputInvestmentRequests' bootstrap branch fires
            // unconditionally at urgency=1.0 (RefinementInvestmentUrgency); the keep-alive pass
            // (urgency=0.2) must not run afterward and downgrade it.

            var manager = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            var playerState = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            playerState.ReservationOptIn.Add(NodeCapabilityKind.Refinery);
            var states = new List<FactionState> { playerState };
            var log = new List<string>();
            manager.Tick(states, world, world.Tuning, log);

            InvestmentRequest req = world.Investments.SnapshotFor("EXP").Single(r => r.Cap == NodeCapabilityKind.Refinery);
            Assert.Equal(1.0, req.Urgency);
            Assert.DoesNotContain(log, l => l.Contains("RESERVE_KEEPALIVE") && l.Contains("Refinery"));
        }

        // Live regression (EXP, 2026-08-19): the keep-alive treated its own still-active entry as
        // "already covered" and skipped the repost, so the request aged out at InvestmentTtl every
        // time. The next tick's ReserveInstallStock then saw an empty register and dumped the whole
        // staged Shipyard BOM back into routable inventory -- ~5000 components ping-ponging every
        // ~2.5 min, one full release/reclaim cycle per TTL lapse.
        [Fact]
        public void ProductionManager_KeepAlive_RefreshesItsOwnRequest_SoStagedStockSurvivesTheTtl()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning.InstallBoms[NodeCapabilityKind.Shipyard] = new Dictionary<string, long>
            {
                ["Comp:SteelPlate"] = 100,
            };

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "EXP";
            node.HoldStrength    = 1.0;
            node.StableStationId = 42;
            node.Inventory["Comp:SteelPlate"] = 150;

            var manager = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            var playerState = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            playerState.ReservationOptIn.Add(NodeCapabilityKind.Shipyard);
            var states = new List<FactionState> { playerState };
            var log = new List<string>();

            // Tick cadence shorter than the 120s InvestmentTtl, with the register pruned each tick
            // the way FactionRuntime/ShipyardManager do live. ReserveInstallStock runs at the top of
            // the tick, so t=0 only posts the request and t=90 is the tick that stages the stock.
            DateTime start = world.UtcNow;
            manager.Tick(states, world, world.Tuning, log);

            world.UtcNow = start.AddSeconds(90);
            world.Investments.Prune(world.UtcNow);
            manager.Tick(states, world, world.Tuning, log);
            Assert.Equal(100, node.ReservedInventory["Comp:SteelPlate"]);

            // t=150 is past the TTL of the entry posted at t=0: it only survives if the t=90 tick
            // reposted and extended it.
            world.UtcNow = start.AddSeconds(150);
            world.Investments.Prune(world.UtcNow);
            manager.Tick(states, world, world.Tuning, log);

            Assert.Contains(world.Investments.SnapshotFor("EXP"), r => r.Cap == NodeCapabilityKind.Shipyard);
            Assert.Equal(100, node.ReservedInventory["Comp:SteelPlate"]);
            Assert.Equal(50, node.Inventory["Comp:SteelPlate"]);
            Assert.DoesNotContain(log, l => l.Contains("INVEST_RELEASE"));
            // A refresh is silent -- only the first, genuinely new entry is worth a line.
            Assert.Single(log.Where(l => l.Contains("RESERVE_KEEPALIVE")));
        }

        [Fact]
        public void ProductionManager_KeepAlive_NoOpForNpcFaction()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "NPC";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            var manager = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            var npcState = new FactionState { Tag = "NPC" }; // IsPlayerFaction defaults false
            // Defensive: manually populate ReservationOptIn (never happens via any real NPC path)
            // to prove the explicit IsPlayerFaction guard is load-bearing, not just the empty-set case.
            npcState.ReservationOptIn.Add(NodeCapabilityKind.ManufactoryII);
            var states = new List<FactionState> { npcState };
            var log = new List<string>();
            manager.Tick(states, world, world.Tuning, log);

            Assert.DoesNotContain(log, l => l.Contains("RESERVE_KEEPALIVE"));
        }

        private sealed class FakeAllCargoPrefabCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 1_000_000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
                => new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }
    }
}
