using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="StandingOrderKind.Stock"/>: "keep N of this item at this node". The order posts the
    /// gap as demand and holds the target back from the node's routable surplus. The case it exists
    /// for: a magazine only the player's own ship design fires, which no other rule ever asks for.
    /// </summary>
    public class StockOrderTests
    {
        private const string Mag = "NATO_25x184mm";
        private static readonly DateTime T0 = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

        private static WorldModel World()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = T0;
            return world;
        }

        private static SpatialNode Node(WorldModel world, string id, Vec3 pos, string owner = "P")
        {
            SpatialNode node = world.Graph.Add(pos, 1.0, id);
            node.OwnerFactionTag = owner;
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            return node;
        }

        private static StandingOrder AddStockOrder(FactionState st, string nodeId, string itemKey, long target,
                                                   bool paused = false)
        {
            var order = new StandingOrder
            {
                Id = st.MintStandingOrderId(),
                Kind = StandingOrderKind.Stock,
                NodeId = nodeId,
                ItemKey = itemKey,
                TargetQuantity = target,
                Paused = paused,
            };
            st.AddStandingOrder(order);
            return order;
        }

        private static StandingOrder AddAllStationsOrder(FactionState st, string itemKey, long target,
                                                         bool paused = false)
        {
            var order = new StandingOrder
            {
                Id = st.MintStandingOrderId(),
                Kind = StandingOrderKind.Stock,
                AllStations = true,
                ItemKey = itemKey,
                TargetQuantity = target,
                Paused = paused,
            };
            st.AddStandingOrder(order);
            return order;
        }

        private static long SurplusOf(SpatialNode node, string key, WorldModel world)
        {
            MethodInfo m = typeof(OperationsManager).GetMethod("SurplusOf", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(SpatialNode), typeof(string), typeof(WorldModel), typeof(FactionTuning) }, null);
            return (long)m.Invoke(null, new object[] { node, key, world, world.Tuning });
        }

        private static void RunProduction(WorldModel world, FactionState st)
        {
            new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>())
                .Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());
        }

        // ---- asking ------------------------------------------------------------------------

        [Fact]
        public void AnOrder_PostsTheGapAsNeed_AndTheTargetAsItem()
        {
            WorldModel world = World();
            SpatialNode node = Node(world, "Home", new Vec3(0, 0, 0));
            node.Inventory[Mag] = 30;
            var st = new FactionState { Tag = "P" };
            AddStockOrder(st, "Home", Mag, 100);

            RunProduction(world, st);

            DemandEntry need, item;
            Assert.True(world.Demand.TryGet("P", DemandRegister.NeedKind(Mag), "Home", null, out need));
            Assert.Equal(70L, need.Quantity);
            Assert.True(world.Demand.TryGet("P", DemandRegister.ItemKind(Mag), "Home", null, out item));
            Assert.Equal(100L, item.Quantity);
        }

        [Fact]
        public void ANodeHoldingItsTarget_AsksForNothing()
        {
            WorldModel world = World();
            SpatialNode node = Node(world, "Home", new Vec3(0, 0, 0));
            node.Inventory[Mag] = 100;
            var st = new FactionState { Tag = "P" };
            AddStockOrder(st, "Home", Mag, 100);

            RunProduction(world, st);

            DemandEntry row;
            Assert.False(world.Demand.TryGet("P", DemandRegister.NeedKind(Mag), "Home", null, out row));
            Assert.False(world.Demand.TryGet("P", DemandRegister.ItemKind(Mag), "Home", null, out row));
        }

        [Fact]
        public void APausedOrder_OrOneAtANodeNotHeld_NeitherAsksNorHolds()
        {
            WorldModel world = World();
            SpatialNode home = Node(world, "Home", new Vec3(0, 0, 0));
            SpatialNode lost = Node(world, "Lost", new Vec3(10, 0, 0), owner: "Q");
            home.Inventory[Mag] = 50;
            lost.Inventory[Mag] = 50;
            var st = new FactionState { Tag = "P" };
            AddStockOrder(st, "Home", Mag, 100, paused: true);
            AddStockOrder(st, "Lost", Mag, 100);

            RunProduction(world, st);

            DemandEntry row;
            Assert.False(world.Demand.TryGet("P", DemandRegister.NeedKind(Mag), "Home", null, out row));
            Assert.False(world.Demand.TryGet("P", DemandRegister.NeedKind(Mag), "Lost", null, out row));
            Assert.Equal(50L, SurplusOf(home, Mag, world));
            Assert.Equal(50L, SurplusOf(lost, Mag, world));
        }

        [Fact]
        public void TwoOrdersForOneNodeAndItem_AddUp()
        {
            WorldModel world = World();
            Node(world, "Home", new Vec3(0, 0, 0));
            var st = new FactionState { Tag = "P" };
            AddStockOrder(st, "Home", Mag, 100);
            AddStockOrder(st, "Home", Mag, 50);

            Dictionary<(string NodeId, string ItemKey), long> targets = StockOrders.Targets(st, world.Graph);

            Assert.Equal(150L, targets[("Home", Mag)]);
        }

        // ---- every station -----------------------------------------------------------------

        [Fact]
        public void AnAllStationsOrder_TargetsEveryFoundedStationTheFactionOwns()
        {
            WorldModel world = World();
            Node(world, "A", new Vec3(0, 0, 0));
            Node(world, "B", new Vec3(10, 0, 0));
            Node(world, "Claim", new Vec3(20, 0, 0)).StableStationId = 0;
            Node(world, "Theirs", new Vec3(30, 0, 0), owner: "Q");
            var st = new FactionState { Tag = "P" };
            AddAllStationsOrder(st, Mag, 200);

            Dictionary<(string NodeId, string ItemKey), long> targets = StockOrders.Targets(st, world.Graph);

            Assert.Equal(2, targets.Count);
            Assert.Equal(200L, targets[("A", Mag)]);
            Assert.Equal(200L, targets[("B", Mag)]);
        }

        /// <summary>Most specific wins: a station order sets that station's target, higher or lower,
        /// and the all-stations orders are not added to it.</summary>
        [Fact]
        public void AStationOrder_ReplacesTheAllStationsTargetThere_HigherOrLower()
        {
            WorldModel world = World();
            Node(world, "Home", new Vec3(0, 0, 0));
            Node(world, "Outpost", new Vec3(10, 0, 0));
            Node(world, "Other", new Vec3(20, 0, 0));
            var st = new FactionState { Tag = "P" };
            AddAllStationsOrder(st, Mag, 200);
            AddAllStationsOrder(st, Mag, 50);
            AddStockOrder(st, "Home", Mag, 2000);
            AddStockOrder(st, "Outpost", Mag, 10);
            AddStockOrder(st, "Other", "Missile200mm", 30);

            Dictionary<(string NodeId, string ItemKey), long> targets = StockOrders.Targets(st, world.Graph);

            Assert.Equal(2000L, targets[("Home", Mag)]);
            Assert.Equal(10L, targets[("Outpost", Mag)]);
            Assert.Equal(250L, targets[("Other", Mag)]);           // another item does not override
            Assert.Equal(30L, targets[("Other", "Missile200mm")]);
        }

        /// <summary>A paused station order overrides nothing: it neither asks nor holds, so the
        /// station falls back to the faction default.</summary>
        [Fact]
        public void APausedStationOrder_LeavesTheAllStationsTarget()
        {
            WorldModel world = World();
            Node(world, "Home", new Vec3(0, 0, 0));
            var st = new FactionState { Tag = "P" };
            AddAllStationsOrder(st, Mag, 200);
            AddStockOrder(st, "Home", Mag, 2000, paused: true);

            Assert.Equal(200L, StockOrders.Targets(st, world.Graph)[("Home", Mag)]);
        }

        [Fact]
        public void AStationFoundedLater_IsCoveredWithNoEdit()
        {
            WorldModel world = World();
            Node(world, "Home", new Vec3(0, 0, 0));
            SpatialNode later = Node(world, "Later", new Vec3(10, 0, 0), owner: "Q");
            var st = new FactionState { Tag = "P" };
            AddAllStationsOrder(st, Mag, 200);
            Assert.False(StockOrders.Targets(st, world.Graph).ContainsKey(("Later", Mag)));

            later.OwnerFactionTag = "P";

            Assert.Equal(200L, StockOrders.Targets(st, world.Graph)[("Later", Mag)]);
        }

        [Fact]
        public void AnAllStationsOrder_EachStationAsksForItsOwnGap_AndHoldsItsTarget()
        {
            WorldModel world = World();
            SpatialNode a = Node(world, "A", new Vec3(0, 0, 0));
            SpatialNode b = Node(world, "B", new Vec3(10, 0, 0));
            a.Inventory[Mag] = 50;
            b.Inventory[Mag] = 300;
            var st = new FactionState { Tag = "P" };
            AddAllStationsOrder(st, Mag, 200);

            RunProduction(world, st);

            DemandEntry need;
            Assert.True(world.Demand.TryGet("P", DemandRegister.NeedKind(Mag), "A", null, out need));
            Assert.Equal(150L, need.Quantity);
            Assert.False(world.Demand.TryGet("P", DemandRegister.NeedKind(Mag), "B", null, out need));
            Assert.Equal(0L, SurplusOf(a, Mag, world));
            Assert.Equal(100L, SurplusOf(b, Mag, world));
        }

        [Fact]
        public void AppliedStations_SkipsOverriddenStations_AndSumsTheItemsAllStationsOrders()
        {
            WorldModel world = World();
            Node(world, "Home", new Vec3(0, 0, 0));
            Node(world, "A", new Vec3(10, 0, 0));
            Node(world, "B", new Vec3(20, 0, 0));
            var st = new FactionState { Tag = "P" };
            StandingOrder paused = AddAllStationsOrder(st, Mag, 200, paused: true);
            AddAllStationsOrder(st, Mag, 50);
            AddAllStationsOrder(st, "Missile200mm", 9);
            AddStockOrder(st, "Home", Mag, 2000);

            long target;
            List<SpatialNode> applied = StockOrders.AppliedStations(st, world.Graph, paused, out target);

            List<string> ids = applied.ConvertAll(n => n.Id);
            ids.Sort(StringComparer.Ordinal);
            Assert.Equal(new[] { "A", "B" }, ids);
            Assert.Equal(250L, target);   // the paused order counts for itself
        }

        // ---- holding -----------------------------------------------------------------------

        /// <summary>Without the hold, a filled node is a haul source for every other node's demand for
        /// the same item and a sell source to partners, and the stock leaves as fast as it arrives.</summary>
        [Fact]
        public void TheTarget_IsKeptBackFromTheRoutableSurplus()
        {
            WorldModel world = World();
            SpatialNode node = Node(world, "Home", new Vec3(0, 0, 0));
            node.Inventory[Mag] = 130;
            node.Inventory["Ingot:Iron"] = 40;
            var st = new FactionState { Tag = "P" };
            AddStockOrder(st, "Home", Mag, 100);
            Assert.Equal(130L, SurplusOf(node, Mag, world));   // not yet published

            StockOrders.Publish(st, world);

            Assert.Equal(100L, StockOrders.Keep(node, Mag, world));
            Assert.Equal(30L, SurplusOf(node, Mag, world));
            Assert.Equal(40L, SurplusOf(node, "Ingot:Iron", world));   // other keys untouched
        }

        [Fact]
        public void RemovingTheLastOrder_ReleasesTheHold()
        {
            WorldModel world = World();
            SpatialNode node = Node(world, "Home", new Vec3(0, 0, 0));
            node.Inventory[Mag] = 130;
            var st = new FactionState { Tag = "P" };
            StandingOrder order = AddStockOrder(st, "Home", Mag, 100);
            StockOrders.Publish(st, world);

            st.RemoveStandingOrder(order);
            StockOrders.Publish(st, world);

            Assert.Equal(0L, StockOrders.Keep(node, Mag, world));
            Assert.Equal(130L, SurplusOf(node, Mag, world));
        }

        /// <summary>The fuel keep and a Stock order on the same key are one hold, not two: both say
        /// "this node needs this much here".</summary>
        [Fact]
        public void AFuelKeepAndAStockOrderOnOneKey_HoldTheLarger()
        {
            FactionTuning tuning = FuelBufferTests.Tuning();
            tuning.FuelReserveFloor = 1_000;
            WorldModel world = World();
            world.Tuning = tuning;
            world.SetFuelReserveKeys("P", new List<string> { "Gas:Hydrogen" });
            SpatialNode node = Node(world, "Home", new Vec3(0, 0, 0));
            node.Inventory["Gas:Hydrogen"] = 10_000;
            var st = new FactionState { Tag = "P" };
            AddStockOrder(st, "Home", "Gas:Hydrogen", 3_000);
            StockOrders.Publish(st, world);

            Assert.Equal(1_000L, FuelReserve.Keep(node, "Gas:Hydrogen", T0, world, tuning));
            Assert.Equal(7_000L, SurplusOf(node, "Gas:Hydrogen", world));
        }

        /// <summary>The production pass republishes the hold, so an order reaches the surplus rule
        /// after a restart without the player touching it.</summary>
        [Fact]
        public void TheProductionPass_PublishesTheHold()
        {
            WorldModel world = World();
            SpatialNode node = Node(world, "Home", new Vec3(0, 0, 0));
            node.Inventory[Mag] = 130;
            var st = new FactionState { Tag = "P" };
            AddStockOrder(st, "Home", Mag, 100);

            RunProduction(world, st);

            Assert.Equal(30L, SurplusOf(node, Mag, world));
        }

        // ---- the item picker's sources --------------------------------------------------------

        [Fact]
        public void Sources_NameOwnProductionOwnStockAndOtherHolders()
        {
            WorldModel world = World();
            SpatialNode home = Node(world, "Home", new Vec3(0, 0, 0));
            home.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            home.Inventory[Mag] = 40;
            Node(world, "A", new Vec3(10, 0, 0), owner: "IGSL").Inventory[Mag] = 500;
            Node(world, "B", new Vec3(20, 0, 0), owner: "METC").Inventory[Mag] = 900;
            var recipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.Manufactory, new List<ConversionRecipe> { new ConversionRecipe
                    {
                        Inputs = new RecipeItem[0],
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_AmmoMagazine", SubTypeId = Mag } },
                    } } },
            };

            Dictionary<string, StockItemSource> sources =
                StockItemSources.Build(new FactionState { Tag = "P" }, world.Graph, recipes);

            StockItemSource mag = sources[Mag];
            Assert.True(mag.OwnProduction);
            Assert.Equal(40L, mag.OwnStock);
            Assert.Equal("you make it; you hold 40; METC, IGSL hold it", StockItemSources.Describe(mag));
        }

        [Fact]
        public void Sources_AnItemNothingTouches_HasNoKnownSource()
        {
            WorldModel world = World();
            Node(world, "Home", new Vec3(0, 0, 0));

            Dictionary<string, StockItemSource> sources =
                StockItemSources.Build(new FactionState { Tag = "P" }, world.Graph, null);

            StockItemSource none;
            Assert.False(sources.TryGetValue("ExoticRound", out none));
            Assert.Equal("no known source", StockItemSources.Describe(none));
        }

        /// <summary>A capability at a node the faction does not own does not count as its own
        /// production.</summary>
        [Fact]
        public void Sources_AnotherFactionsCapability_IsNotOwnProduction()
        {
            WorldModel world = World();
            Node(world, "Home", new Vec3(0, 0, 0));
            Node(world, "A", new Vec3(10, 0, 0), owner: "IGSL").Capabilities[NodeCapabilityKind.Manufactory] = 1;
            var recipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.Manufactory, new List<ConversionRecipe> { new ConversionRecipe
                    {
                        Inputs = new RecipeItem[0],
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_AmmoMagazine", SubTypeId = Mag } },
                    } } },
            };

            Dictionary<string, StockItemSource> sources =
                StockItemSources.Build(new FactionState { Tag = "P" }, world.Graph, recipes);

            Assert.False(sources.ContainsKey(Mag));
        }

        // ---- persistence -------------------------------------------------------------------

        [Fact]
        public void AStockOrder_RoundTrips()
        {
            var st = new FactionState { Tag = "P" };
            AddStockOrder(st, "Home", Mag, 250, paused: true);

            BrainDoc doc = FactionStore.ToBrainDoc(st, new SpatialGraph().OwnedBy("P"), FactionStore.DefaultLogCap);
            StandingOrder loaded = Assert.Single(FactionStore.ApplyBrainDoc(doc, new SpatialGraph()).StandingOrders);

            Assert.Equal(StandingOrderKind.Stock, loaded.Kind);
            Assert.Equal("Home", loaded.NodeId);
            Assert.Equal(Mag, loaded.ItemKey);
            Assert.Equal(250L, loaded.TargetQuantity);
            Assert.True(loaded.Paused);
        }

        [Fact]
        public void AnAllStationsOrder_RoundTrips_AndAStationOrderWritesNoAllStations()
        {
            var st = new FactionState { Tag = "P" };
            AddAllStationsOrder(st, Mag, 200);
            AddStockOrder(st, "Home", Mag, 250);

            BrainDoc doc = FactionStore.ToBrainDoc(st, new SpatialGraph().OwnedBy("P"), FactionStore.DefaultLogCap);
            List<StandingOrder> loaded = FactionStore.ApplyBrainDoc(doc, new SpatialGraph()).StandingOrders;

            StandingOrder all = loaded.Find(o => o.AllStations);
            Assert.NotNull(all);
            Assert.Null(all.NodeId);
            Assert.Equal(200L, all.TargetQuantity);
            Assert.DoesNotContain("\"AllStations\"",
                JsonConvert.SerializeObject(doc.StandingOrders.Find(o => o.NodeId == "Home")));
        }

        /// <summary>A build that predates the field loads an all-stations order as a Stock order
        /// with no node, which asks for nothing and holds nothing.</summary>
        [Fact]
        public void AnAllStationsOrderReadWithoutTheField_AsksForNothing()
        {
            WorldModel world = World();
            Node(world, "Home", new Vec3(0, 0, 0));
            var st = new FactionState { Tag = "P" };
            st.AddStandingOrder(new StandingOrder
            {
                Id = st.MintStandingOrderId(), Kind = StandingOrderKind.Stock, ItemKey = Mag, TargetQuantity = 200,
            });

            Assert.Null(StockOrders.Targets(st, world.Graph));
        }

        [Fact]
        public void AMineOrder_WritesNoStockFields()
        {
            var st = new FactionState { Tag = "P" };
            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, DateTime.MinValue);

            BrainDoc doc = FactionStore.ToBrainDoc(st, new SpatialGraph().OwnedBy("P"), FactionStore.DefaultLogCap);
            string json = JsonConvert.SerializeObject(Assert.Single(doc.StandingOrders));

            Assert.DoesNotContain("\"NodeId\"", json);
            Assert.DoesNotContain("\"ItemKey\"", json);
            Assert.DoesNotContain("\"TargetQuantity\"", json);
        }
    }
}
