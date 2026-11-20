using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A Stock order's rank on the claim scale ship builds and capability installs share
    /// (<see cref="StockClaimFloor"/>). The case it exists for: EXP's rocket order at N099 held 0 while
    /// Mine, Patrol and Raid builds reserved every cobalt ingot the rocket recipe needs.
    /// </summary>
    public class StockOrderRankTests
    {
        private const string Rocket = "Ace_100mmRocket";
        private const string Iron = "Ingot:Iron";
        private const string Cobalt = "Ingot:Cobalt";
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);

        private static RecipeItem Ingot(string sub, double amount)
        {
            return new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = sub, Amount = amount };
        }

        /// <summary>One ManufactoryII bank: a MetalGrid recipe the build needs and a rocket recipe the
        /// order needs, both eating iron and cobalt.</summary>
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> Recipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.ManufactoryII] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "MetalGrid", TimeSecs = 1f,
                        Inputs  = new[] { Ingot("Iron", 2), Ingot("Cobalt", 1) },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "MetalGrid", Amount = 1 } },
                    },
                    new ConversionRecipe
                    {
                        BlueprintId = "Rocket", TimeSecs = 1f,
                        Inputs  = new[] { Ingot("Iron", 2), Ingot("Cobalt", 1) },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_AmmoMagazine", SubTypeId = Rocket, Amount = 1 } },
                    },
                },
            };
        }

        private static WorldModel World(long iron, long cobalt, int buildPriority, out SpatialNode node, out BuildQueueEntry build)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = T0;
            node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            if (iron > 0) node.Inventory[Iron] = iron;
            if (cobalt > 0) node.Inventory[Cobalt] = cobalt;

            build = new BuildQueueEntry { Id = "Raid:001", Role = "Raid", Priority = buildPriority, ExpiresUtc = T0.AddHours(1) };
            build.Bom["Comp:MetalGrid"] = 100;
            node.BuildQueue.Add(build);
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + Rocket, NodeId = "N1", Quantity = 1000, ExpiresUtc = T0.AddHours(1),
            });
            return world;
        }

        private static FactionState State(string itemKey, long target, int rank)
        {
            var st = new FactionState { Tag = "F" };
            st.AddStandingOrder(new StandingOrder
            {
                Id = st.MintStandingOrderId(),
                Kind = StandingOrderKind.Stock,
                NodeId = "N1",
                ItemKey = itemKey,
                TargetQuantity = target,
                Priority = rank,
            });
            return st;
        }

        private static ProductionManager Manager()
        {
            return new ProductionManager(Recipes(), null, new FixedScarcity(new Dictionary<string, double> { { Rocket, 3.0 } }));
        }

        private static List<string> Run(WorldModel world, FactionState st, ProductionManager manager, int ticks)
        {
            var log = new List<string>();
            for (int i = 0; i < ticks; i++)
            {
                manager.Tick(new List<FactionState> { st }, world, world.Tuning, log);
                world.UtcNow = world.UtcNow.AddSeconds(5);
            }
            return log;
        }

        private static long Reserved(List<string> log, string key)
        {
            long sum = 0;
            foreach (string l in log)
            {
                int at = l.IndexOf(" SHIPBUILD_RESERVE " + key + " +", StringComparison.Ordinal);
                if (at < 0) continue;
                string rest = l.Substring(at + (" SHIPBUILD_RESERVE " + key + " +").Length);
                sum += long.Parse(rest.Substring(0, rest.IndexOf(' ')));
            }
            return sum;
        }

        private static long Have(SpatialNode node, string key)
        {
            long have;
            node.Inventory.TryGetValue(key, out have);
            return have;
        }

        // ---- the contest -------------------------------------------------------------------------

        [Fact]
        public void AtRankZero_TheBuildTakesTheContestedInput_AsBefore()
        {
            SpatialNode node; BuildQueueEntry build;
            WorldModel world = World(1000, 10, ShipyardManager.RolePriority("Raid", TestSupport.World().Tuning), out node, out build);
            FactionState st = State(Rocket, 10, 0);

            List<string> log = Run(world, st, Manager(), 6);

            Assert.Equal(10, Reserved(log, Cobalt));
            Assert.Equal(0, Have(node, Rocket));
        }

        [Fact]
        public void ARankedOrder_KeepsItsItemsInputsFromALowerRankedBuild()
        {
            SpatialNode node; BuildQueueEntry build;
            WorldModel world = World(1000, 10, ShipyardManager.RolePriority("Raid", TestSupport.World().Tuning), out node, out build);
            FactionState st = State(Rocket, 10, 1);

            List<string> log = Run(world, st, Manager(), 6);

            Assert.Equal(0, Reserved(log, Cobalt));
            Assert.True(Have(node, Rocket) > 0, "the rocket recipe ran on the cobalt the order claimed");
            Assert.True(Reserved(log, Iron) > 0, "iron beyond the order's batches still went to the build");
        }

        [Fact]
        public void ATie_GoesToTheBuild()
        {
            WorldModel probe = TestSupport.World();
            SpatialNode node; BuildQueueEntry build;
            WorldModel world = World(1000, 10, StockClaimFloor.ClaimRank(1, probe.Tuning), out node, out build);
            FactionState st = State(Rocket, 10, 1);

            List<string> log = Run(world, st, Manager(), 6);

            Assert.Equal(10, Reserved(log, Cobalt));
            Assert.Equal(0, Have(node, Rocket));
        }

        [Fact]
        public void AnOrderBlockedOnOneInput_HoldsNoOtherInputBack()
        {
            int raid = ShipyardManager.RolePriority("Raid", TestSupport.World().Tuning);
            SpatialNode n0, n1; BuildQueueEntry b0, b1;
            WorldModel unranked = World(1000, 0, raid, out n0, out b0);
            WorldModel ranked = World(1000, 0, raid, out n1, out b1);

            long ironUnranked = Reserved(Run(unranked, State(Rocket, 10, 0), Manager(), 1), Iron);
            long ironRanked = Reserved(Run(ranked, State(Rocket, 10, 1), Manager(), 1), Iron);

            Assert.True(ironUnranked > 0);
            Assert.Equal(ironUnranked, ironRanked);
        }

        [Fact]
        public void TheInputClaim_IsBoundedByTheShortfall()
        {
            SpatialNode node; BuildQueueEntry build;
            WorldModel world = World(1000, 10, ShipyardManager.RolePriority("Raid", TestSupport.World().Tuning), out node, out build);
            node.Inventory[Rocket] = 6;
            FactionState st = State(Rocket, 10, 1);

            long cobalt = Reserved(Run(world, st, Manager(), 1), Cobalt);

            Assert.True(cobalt > 0 && cobalt < 10, "4 rockets short claims 4 rockets of cobalt, the build takes the rest: " + cobalt);
        }

        [Fact]
        public void ARankedOrderForTheIngotItself_HoldsItsTargetFromTheBuild()
        {
            SpatialNode node; BuildQueueEntry build;
            WorldModel world = World(1000, 10, ShipyardManager.RolePriority("Raid", TestSupport.World().Tuning), out node, out build);
            FactionState st = State(Cobalt, 6, 1);

            Assert.Equal(4, Reserved(Run(world, st, Manager(), 1), Cobalt));
        }

        [Fact]
        public void AnUnrankedRecipe_LeavesARankedOrdersInputs()
        {
            // No build at all: MetalGrid, scarcer and so ranked first at the one-machine bank, would
            // otherwise eat the cobalt the rocket order claims.
            SpatialNode node; BuildQueueEntry build;
            WorldModel world = World(1000, 10, 0, out node, out build);
            node.BuildQueue.Clear();
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:MetalGrid", NodeId = "N1", Quantity = 1000, ExpiresUtc = T0.AddHours(1),
            });
            var manager = new ProductionManager(Recipes(), null,
                new FixedScarcity(new Dictionary<string, double> { { Rocket, 3.0 }, { "Comp:MetalGrid", 5.0 } }));

            Run(world, State(Rocket, 10, 1), manager, 6);

            Assert.True(Have(node, Rocket) > 0, "the rocket recipe ran");
            Assert.Equal(0, Have(node, "Comp:MetalGrid"));
        }

        [Fact]
        public void AnInstallRankedBelowTheOrder_LeavesItsTarget()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = T0;
            world.Tuning.InstallBoms[NodeCapabilityKind.ManufactoryII] =
                new Dictionary<string, long> { ["Comp:MetalGrid"] = 80 };
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            node.Inventory["Comp:MetalGrid"] = 80;
            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.ManufactoryII, Urgency = 1.0, ExpiresUtc = T0.AddMinutes(5),
            });
            FactionState st = State("Comp:MetalGrid", 50, 1);

            new ProductionManager(Recipes()).Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            Assert.Equal(30, node.ReservedInventory["Comp:MetalGrid"]);
        }

        // ---- rank ----------------------------------------------------------------------------------

        [Fact]
        public void ClaimRank_LiftsASetRankAboveEveryBuildRole_AndLeavesZeroAlone()
        {
            FactionTuning tuning = TestSupport.World().Tuning;
            Assert.Equal(1 + tuning.HighestBuildRolePriority(), StockClaimFloor.ClaimRank(1, tuning));
            Assert.Equal(0, StockClaimFloor.ClaimRank(0, tuning));
            Assert.True(StockClaimFloor.ClaimRank(1, tuning) > ShipyardManager.RolePriority("Mine", tuning));
        }

        [Fact]
        public void TargetRanks_TakeTheStationOrdersWhereTheyApply_AndTheHighestOfSeveral()
        {
            WorldModel world = TestSupport.World();
            foreach (string id in new[] { "A", "B" })
            {
                SpatialNode n = world.Graph.Add(new Vec3(id == "A" ? 0 : 10, 0, 0), 1.0, id);
                n.OwnerFactionTag = "F";
                n.HoldStrength = 1.0;
                n.StableStationId = 1;
            }
            var st = new FactionState { Tag = "F" };
            st.AddStandingOrder(new StandingOrder { Id = st.MintStandingOrderId(), Kind = StandingOrderKind.Stock, AllStations = true, ItemKey = Rocket, TargetQuantity = 5, Priority = 2 });
            st.AddStandingOrder(new StandingOrder { Id = st.MintStandingOrderId(), Kind = StandingOrderKind.Stock, AllStations = true, ItemKey = Rocket, TargetQuantity = 5, Priority = 4 });
            st.AddStandingOrder(new StandingOrder { Id = st.MintStandingOrderId(), Kind = StandingOrderKind.Stock, NodeId = "A", ItemKey = Rocket, TargetQuantity = 50, Priority = 0 });

            Dictionary<(string NodeId, string ItemKey), int> ranks;
            Dictionary<(string NodeId, string ItemKey), long> targets = StockOrders.TargetsWithRanks(st, world.Graph, out ranks);

            Assert.Equal(50, targets[("A", Rocket)]);
            Assert.Equal(0, ranks[("A", Rocket)]);
            Assert.Equal(10, targets[("B", Rocket)]);
            Assert.Equal(4, ranks[("B", Rocket)]);
        }

        private sealed class FixedScarcity : IPriceOracle
        {
            private readonly Dictionary<string, double> _scarcity;
            public FixedScarcity(Dictionary<string, double> scarcity) { _scarcity = scarcity; }
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference) { reference = 0; return false; }
            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                double value;
                return _scarcity.TryGetValue(inventoryKey, out value) ? value : 1.0;
            }
        }
    }
}
