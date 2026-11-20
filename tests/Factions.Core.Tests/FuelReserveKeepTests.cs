using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="FuelReserve.Keep"/> and the routable surplus that leaves it out. The failure it
    /// prevents, measured live 2026-09-29: a node that had just received
    /// 14,536,846 L of hydrogen into its reserve shipped exactly that quantity on to a neighbour 47 s
    /// later and logged <c>FUEL_SHORT drawn=0</c> on the same departure, because every founded node
    /// posts a reserve and was also a haul source for all of its stock.
    /// </summary>
    public class FuelReserveKeepTests
    {
        private const string H2 = "Gas:Hydrogen";
        private const string Uranium = "Ingot:Uranium";
        private static readonly DateTime T0 = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Reserve horizon 6 h, buffer lead 0.5 h, floors of 1,000 L and 10 kg: a node
        /// burning 10,000 L/h asks for 60,000 L and keeps its 5,000 L buffer.</summary>
        private static FactionTuning Tuning()
        {
            FactionTuning tuning = FuelBufferTests.Tuning();
            tuning.FuelReserveFloor = 1_000;
            tuning.TradeSellUnusedStock = true;
            return tuning;
        }

        private static WorldModel World(FactionTuning tuning)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = T0;
            world.Tuning = tuning;
            world.SetFuelReserveKeys("F", new List<string> { H2, Uranium });
            return world;
        }

        private static SpatialNode Node(WorldModel world, string id, Vec3 pos, double burnPerHour, long stock,
                                        bool founded = true)
        {
            SpatialNode node = world.Graph.Add(pos, 1.0, id);
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = founded ? 1 : 0;
            if (burnPerHour > 0)
                FuelDraw.CreditBurn(node, T0, world.Tuning, H2, burnPerHour * world.Tuning.FuelBurnEmaHours);
            if (stock > 0) node.Inventory[H2] = stock;
            return node;
        }

        private static long SurplusOf(SpatialNode node, string key, WorldModel world)
        {
            MethodInfo m = typeof(OperationsManager).GetMethod("SurplusOf", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(SpatialNode), typeof(string), typeof(WorldModel), typeof(FactionTuning) }, null);
            return (long)m.Invoke(null, new object[] { node, key, world, world.Tuning });
        }

        // ---- the keep ----------------------------------------------------------------------

        [Fact]
        public void TheKeep_IsTheDepartureBuffer_NotTheWholeReserveTarget()
        {
            WorldModel world = World(Tuning());
            SpatialNode yard = Node(world, "Yard", new Vec3(0, 0, 0), 10_000, 0);

            Assert.Equal(60_000L, FuelReserve.TargetStock(yard, H2, T0, world.Tuning));
            Assert.Equal(5_000L, FuelReserve.BufferTarget(yard, H2, T0, world.Tuning));
            Assert.Equal(5_000L, FuelReserve.Keep(yard, H2, T0, world, world.Tuning));
        }

        /// <summary>A depot's output stays at the depot node. Holding less than six hours of its own
        /// burn but more than its buffer, it still supplies the excess over the buffer.</summary>
        [Fact]
        public void ADepotUnderSixHoursOfBurnButAboveItsBuffer_SuppliesTheExcess()
        {
            WorldModel world = World(Tuning());
            SpatialNode depot = Node(world, "Depot", new Vec3(0, 0, 0), 10_000, 30_000);

            Assert.True(30_000 < FuelReserve.TargetStock(depot, H2, T0, world.Tuning));
            Assert.Equal(25_000L, SurplusOf(depot, H2, world));
        }

        [Fact]
        public void TheKeep_IsZeroWhereTheReservePostsNothing()
        {
            FactionTuning tuning = Tuning();
            WorldModel world = World(tuning);
            SpatialNode yard = Node(world, "Yard", new Vec3(0, 0, 0), 10_000, 0);
            SpatialNode outpost = Node(world, "Outpost", new Vec3(10, 0, 0), 10_000, 0, founded: false);

            Assert.Equal(0L, FuelReserve.Keep(outpost, H2, T0, world, tuning));          // not founded
            Assert.Equal(0L, FuelReserve.Keep(yard, "Ingot:Iron", T0, world, tuning));   // not a reserve key

            world.SetFuelReserveKeys("F", null);
            Assert.Equal(0L, FuelReserve.Keep(yard, H2, T0, world, tuning));             // keys cleared
            world.SetFuelReserveKeys("F", new List<string> { H2 });

            tuning.FuelDrawMode = FuelDrawModes.Draw - 1;
            Assert.Equal(0L, FuelReserve.Keep(yard, H2, T0, world, tuning));             // nothing drawn
            tuning.FuelDrawMode = FuelDrawModes.Draw;
        }

        /// <summary>Reactor fuel is a reserve key like gas: a founded node with no burn of it keeps
        /// the reactor floor, in kilograms.</summary>
        [Fact]
        public void TheReactorFuelKey_KeepsItsOwnFloor()
        {
            WorldModel world = World(Tuning());
            SpatialNode yard = Node(world, "Yard", new Vec3(0, 0, 0), 0, 0);
            yard.Inventory[Uranium] = 25;

            Assert.Equal(10L, FuelReserve.Keep(yard, Uranium, T0, world, world.Tuning));
            Assert.Equal(15L, SurplusOf(yard, Uranium, world));
        }

        /// <summary>The reserve pass records the keys it posts for, so the keep and the reserve read
        /// one list: a founded node that has never burned hydrogen still keeps its floor because a
        /// neighbour burns it.</summary>
        [Fact]
        public void TheReservePass_RecordsItsKeys_AndAQuietNodeKeepsTheFloor()
        {
            FactionTuning tuning = Tuning();
            WorldModel world = TestSupport.World();
            world.UtcNow = T0;
            world.Tuning = tuning;
            SpatialNode busy = Node(world, "Busy", new Vec3(0, 0, 0), 10_000, 0);
            SpatialNode quiet = Node(world, "Quiet", new Vec3(10, 0, 0), 0, 5_000);
            Assert.False(world.IsFuelReserveKey("F", H2));

            new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>())
                .Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, tuning, new List<string>());

            Assert.True(world.IsFuelReserveKey("F", H2));
            Assert.Equal(5_000L, FuelReserve.Keep(busy, H2, T0, world, tuning));
            Assert.Equal(1_000L, FuelReserve.Keep(quiet, H2, T0, world, tuning));
            Assert.Equal(4_000L, SurplusOf(quiet, H2, world));
        }

        // ---- SurplusOf ---------------------------------------------------------------------

        [Fact]
        public void ANodeHoldingNoMoreThanItsKeep_HasNoSurplus()
        {
            WorldModel world = World(Tuning());
            SpatialNode yard = Node(world, "Yard", new Vec3(0, 0, 0), 10_000, 5_000);
            Assert.Equal(0L, SurplusOf(yard, H2, world));

            yard.Inventory[H2] = 4_000;
            Assert.Equal(0L, SurplusOf(yard, H2, world));
        }

        [Fact]
        public void ANodeAboveItsKeep_OffersExactlyStockLessKeep()
        {
            WorldModel world = World(Tuning());
            SpatialNode yard = Node(world, "Yard", new Vec3(0, 0, 0), 10_000, 14_536_846);
            Assert.Equal(14_536_846L - 5_000, SurplusOf(yard, H2, world));
        }

        [Fact]
        public void ADepotWithLargeStockAndSmallBurn_StillSupplies()
        {
            WorldModel world = World(Tuning());
            SpatialNode depot = Node(world, "Depot", new Vec3(0, 0, 0), 100, 50_000_000);
            // A 50 L buffer at 100 L/h, under the 1,000 L floor.
            Assert.Equal(50_000_000L - 1_000, SurplusOf(depot, H2, world));
        }

        // ---- the single-target matchers ----------------------------------------------------

        [Fact]
        public void TryFindSource_SizesTheSourceByWhatItDoesNotKeep()
        {
            WorldModel world = World(Tuning());
            SpatialNode yard = Node(world, "Yard", new Vec3(0, 0, 0), 10_000, 100_000);
            Node(world, "Need", new Vec3(50, 0, 0), 0, 0);

            MaterialVector v;
            Assert.True(OperationsManager.TryFindSource("F", H2, "Need", 500_000,
                new List<SpatialNode> { yard, world.Graph.Nodes["Need"] }, world, out v));
            Assert.Equal("Yard", v.SourceNodeId);
            Assert.Equal(95_000L, v.Quantity);
        }

        [Fact]
        public void TryFindSource_ANodeAtItsKeepIsNoSource()
        {
            WorldModel world = World(Tuning());
            SpatialNode yard = Node(world, "Yard", new Vec3(0, 0, 0), 10_000, 5_000);
            SpatialNode need = Node(world, "Need", new Vec3(50, 0, 0), 0, 0);

            MaterialVector v;
            Assert.False(OperationsManager.TryFindSource("F", H2, "Need", 500_000,
                new List<SpatialNode> { yard, need }, world, out v));
        }

        [Fact]
        public void TryFindDemand_SizesTheSourceByWhatItDoesNotKeep()
        {
            WorldModel world = World(Tuning());
            Node(world, "Yard", new Vec3(0, 0, 0), 10_000, 100_000);
            Node(world, "Need", new Vec3(50, 0, 0), 0, 0);
            var demands = new List<DemandEntry>
            {
                new DemandEntry { FactionTag = "F", Kind = "Item:" + H2, NodeId = "Need", Quantity = 500_000,
                                  ExpiresUtc = T0.AddMinutes(5) },
            };

            MaterialVector v;
            Assert.True(OperationsManager.TryFindDemand("F", "Yard", demands, world, out v));
            Assert.Equal(95_000L, v.Quantity);
        }

        // ---- the trade seller --------------------------------------------------------------

        /// <summary>A trade sale leaves the faction entirely, so a seller keeps its buffer too: the
        /// consent gate reads a price reference, which is not the reserve, and a node holding its
        /// reserve target posts no row for the gate to see.</summary>
        [Fact]
        public void ATradeSeller_OffersOnlyWhatItDoesNotKeep()
        {
            WorldModel world = World(Tuning());
            SpatialNode yard = Node(world, "Yard", new Vec3(0, 0, 0), 10_000, 100_000);

            Assert.Equal(95_000L, LedgerAvailable(yard, world));
        }

        [Fact]
        public void ATradeSeller_AtItsKeep_IsNotStocked()
        {
            WorldModel world = World(Tuning());
            SpatialNode yard = Node(world, "Yard", new Vec3(0, 0, 0), 10_000, 5_000);

            bool stocked;
            Assert.Equal(0L, LedgerAvailable(yard, world, out stocked));
            Assert.False(stocked);
        }

        private static long LedgerAvailable(SpatialNode node, WorldModel world)
        {
            bool stocked;
            return LedgerAvailable(node, world, out stocked);
        }

        private static long LedgerAvailable(SpatialNode node, WorldModel world, out bool stocked)
        {
            Type ledgerType = typeof(OperationsManager).Assembly.GetType("Factions.Core.TradeSellerLedger");
            object ledger = Activator.CreateInstance(ledgerType, BindingFlags.NonPublic | BindingFlags.Instance, null,
                new object[] { new List<FactionState>() }, null);
            MethodInfo available = ledgerType.GetMethod("Available", BindingFlags.NonPublic | BindingFlags.Instance);
            var args = new object[]
            {
                node, "F", H2, new UnreferencedOracle(), world, world.Tuning, new Dictionary<string, bool>(), null, null,
            };
            long result = (long)available.Invoke(ledger, args);
            stocked = (bool)args[7];
            Assert.Equal(TradeConsent.Reason.Consents, (TradeConsent.Reason)args[8]);
            return result;
        }

        /// <summary>No reference stock anywhere: consent then rests on the node's own rows, and the
        /// consenting margin is the whole surplus.</summary>
        private sealed class UnreferencedOracle : IPriceOracle
        {
            public double ScarcityMultiplier(string nodeId, string inventoryKey) { return 1.0; }
            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference) { reference = 0; return false; }
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
        }
    }
}
