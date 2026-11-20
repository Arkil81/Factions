using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A departure node's standing buffer (<see cref="FuelReserve.BufferTarget"/>): measured burn
    /// × <see cref="FactionTuning.FuelBufferLeadHours"/>, capped, zero where nothing departs, and
    /// kept on the node's <c>Item:</c> row when the catalogue's gas top-up rewrites it. FAC-109
    /// lever (a): the draw happens at departure and the refill arrives after it, so a node holding
    /// only what the last delivery brought flies the next departure short.
    /// </summary>
    public class FuelBufferTests
    {
        internal const string HydrogenKey = "Gas:Hydrogen";
        private const string UraniumKey = "Ingot:Uranium";
        private const long Reference = 10_000_000;
        internal static readonly DateTime T0 = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

        internal static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                FuelDrawMode = FuelDrawModes.Draw,
                FuelBurnEmaHours = 2.0,
                FuelReserveHours = 6.0,
                FuelReserveFloor = 500_000,
                FuelReserveFloorReactorKg = 10,
                FuelBufferLeadHours = 0.5,
                FuelBufferMaxFloors = 100.0,
            };
        }

        /// <summary>A founded node whose burn estimate reads <paramref name="unitsPerHour"/> at
        /// <see cref="T0"/>. One credit of <c>rate × tau</c> units lands exactly on the rate.</summary>
        private static SpatialNode DepartureNode(FactionTuning tuning, double unitsPerHour, string key = HydrogenKey)
        {
            var node = new SpatialNode { Id = "N", StableStationId = 1 };
            if (unitsPerHour > 0)
                FuelDraw.CreditBurn(node, T0, tuning, key, unitsPerHour * tuning.FuelBurnEmaHours);
            return node;
        }

        // ---- the target --------------------------------------------------------------------

        [Fact]
        public void SteadyDepartures_BufferIsBurnTimesLead()
        {
            FactionTuning tuning = Tuning();
            var node = new SpatialNode { Id = "N", StableStationId = 1 };

            // A departure burning 10,000 L every six minutes, for a day: 100,000 L/h.
            DateTime t = T0;
            for (int i = 0; i < 240; i++)
            {
                FuelDraw.CreditBurn(node, t, tuning, HydrogenKey, 10_000);
                t = t.AddMinutes(6);
            }
            t = t.AddMinutes(-6);

            double burn = FuelDraw.BurnRateAt(node, HydrogenKey, t, tuning);
            Assert.InRange(burn, 95_000, 105_000);
            Assert.Equal((long)Math.Ceiling(burn * 0.5), FuelReserve.BufferTarget(node, HydrogenKey, t, tuning));
        }

        [Fact]
        public void TheBuffer_ScalesWithTheLead()
        {
            FactionTuning tuning = Tuning();
            SpatialNode node = DepartureNode(tuning, 1_000_000);
            Assert.Equal(500_000L, FuelReserve.BufferTarget(node, HydrogenKey, T0, tuning));

            tuning.FuelBufferLeadHours = 2.0;
            Assert.Equal(2_000_000L, FuelReserve.BufferTarget(node, HydrogenKey, T0, tuning));

            tuning.FuelBufferLeadHours = 0;
            Assert.Equal(0L, FuelReserve.BufferTarget(node, HydrogenKey, T0, tuning));
        }

        [Fact]
        public void ANodeWithNoDepartures_HasNoBuffer()
        {
            FactionTuning tuning = Tuning();
            var node = new SpatialNode { Id = "N", StableStationId = 1 };

            Assert.Equal(0L, FuelReserve.BufferTarget(node, HydrogenKey, T0, tuning));
            // It still asks for the reserve floor -- decision 6 -- but nothing more.
            Assert.Equal(tuning.FuelReserveFloor, FuelReserve.TargetStock(node, HydrogenKey, T0, tuning));
            // And a top-up sharing its row is written exactly as the top-up asked.
            Assert.Equal(12_345L, FuelReserve.ProtectBuffer(node, HydrogenKey, 0, 12_345, T0, tuning));
        }

        [Fact]
        public void TheBuffer_FallsWhenTheBurnFalls()
        {
            FactionTuning tuning = Tuning();
            SpatialNode node = DepartureNode(tuning, 1_000_000);
            long before = FuelReserve.BufferTarget(node, HydrogenKey, T0, tuning);

            // No departure for one time constant: the rate decays by e, and the buffer with it.
            DateTime later = T0.AddHours(tuning.FuelBurnEmaHours);
            long after = FuelReserve.BufferTarget(node, HydrogenKey, later, tuning);

            Assert.Equal(500_000L, before);
            Assert.Equal((long)Math.Ceiling(500_000 / Math.E), after);
        }

        [Fact]
        public void TheBuffer_IsCappedAtAMultipleOfTheKeysFloor()
        {
            FactionTuning tuning = Tuning();
            tuning.FuelBufferMaxFloors = 4.0;

            SpatialNode gas = DepartureNode(tuning, 100_000_000);
            Assert.Equal(4L * tuning.FuelReserveFloor, FuelReserve.BufferTarget(gas, HydrogenKey, T0, tuning));

            // Reactor fuel caps against its own floor in kilograms, not the gas floor in litres.
            SpatialNode reactor = DepartureNode(tuning, 1_000, UraniumKey);
            Assert.Equal(4L * tuning.FuelReserveFloorReactorKg, FuelReserve.BufferTarget(reactor, UraniumKey, T0, tuning));

            tuning.FuelBufferMaxFloors = 0;
            Assert.Equal(50_000_000L, FuelReserve.BufferTarget(gas, HydrogenKey, T0, tuning));
        }

        [Fact]
        public void BelowTheDrawMode_ThereIsNoBuffer()
        {
            FactionTuning tuning = Tuning();
            SpatialNode node = DepartureNode(tuning, 1_000_000);
            tuning.FuelDrawMode = FuelDrawModes.Measure;

            Assert.Equal(0L, FuelReserve.BufferTarget(node, HydrogenKey, T0, tuning));
            Assert.Equal(7L, FuelReserve.ProtectBuffer(node, HydrogenKey, 0, 7, T0, tuning));
        }

        /// <summary>The buffer is a floor under the reserve, not a second demand: at the default
        /// lead the reserve horizon is longer and the reserve's quantity is unchanged.</summary>
        [Fact]
        public void TheReserve_IsTheLargerOfItsHorizonAndTheBuffer()
        {
            FactionTuning tuning = Tuning();
            SpatialNode node = DepartureNode(tuning, 1_000_000);
            Assert.Equal(6_000_000L, FuelReserve.TargetStock(node, HydrogenKey, T0, tuning));

            tuning.FuelBufferLeadHours = 8.0;
            Assert.Equal(8_000_000L, FuelReserve.TargetStock(node, HydrogenKey, T0, tuning));
        }

        // ---- the shared Item: row ----------------------------------------------------------

        [Fact]
        public void ProtectBuffer_RaisesATopUpBelowTheBuffer_OnlyWhileTheNodeHoldsLess()
        {
            FactionTuning tuning = Tuning();
            SpatialNode node = DepartureNode(tuning, 60_000_000);   // buffer 30 ML
            Assert.Equal(30_000_000L, FuelReserve.BufferTarget(node, HydrogenKey, T0, tuning));

            // Holding 5 ML against a 20 ML shelf, the top-up would write 15 ML; the buffer is kept.
            Assert.Equal(30_000_000L, FuelReserve.ProtectBuffer(node, HydrogenKey, 5_000_000, 15_000_000, T0, tuning));
            // A top-up already larger than the buffer stands.
            Assert.Equal(40_000_000L, FuelReserve.ProtectBuffer(node, HydrogenKey, 5_000_000, 40_000_000, T0, tuning));
            // At or above its buffer the node is covered, and the top-up is written as asked.
            Assert.Equal(1_000L, FuelReserve.ProtectBuffer(node, HydrogenKey, 30_000_000, 1_000, T0, tuning));
        }

        /// <summary>The live shape, 2026-09-29: the catalogue's gas top-up runs after the reserve on
        /// the same (faction, Item:Gas:Hydrogen, node) row and writes <c>2 × reference − stock</c>
        /// — 20 ML at every node. Without the buffer rule a busy departure node's row read 15 ML,
        /// which <c>MatchVectors</c> nets again to 10 ML, whatever the node burns.</summary>
        [Fact]
        public void TheGasTopUp_DoesNotWriteADepartureNodesRowBelowItsBuffer()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = T0;
            world.Tuning = Tuning();
            world.Tuning.ProductionSurplusIntentEnabled = true;
            world.Tuning.ProductionSurplusTargetMultiple = 2.0;

            SpatialNode busy = FoundedNode(world, "Busy", new Vec3(0, 0, 0));
            FuelDraw.CreditBurn(busy, T0, world.Tuning, HydrogenKey, 60_000_000 * world.Tuning.FuelBurnEmaHours);
            busy.Inventory[HydrogenKey] = 5_000_000;

            SpatialNode quiet = FoundedNode(world, "Quiet", new Vec3(100, 0, 0));
            quiet.Inventory[HydrogenKey] = 5_000_000;

            GasManager().Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning,
                              new List<string>());

            DemandEntry busyRow, quietRow;
            Assert.True(world.Demand.TryGet("F", "Item:" + HydrogenKey, "Busy", null, out busyRow));
            Assert.True(world.Demand.TryGet("F", "Item:" + HydrogenKey, "Quiet", null, out quietRow));

            Assert.Equal(30_000_000L, busyRow.Quantity);          // the buffer, gross
            Assert.Equal(DemandClass.Fuel, busyRow.Class);
            Assert.Equal(2 * Reference - 5_000_000, quietRow.Quantity);   // no departures: unchanged
        }

        // ---- the trace ---------------------------------------------------------------------

        [Fact]
        public void TheTrace_FiresOnAMaterialChangeOnly()
        {
            Assert.True(FuelReserve.BufferChangedMaterially(0, 1));
            Assert.False(FuelReserve.BufferChangedMaterially(0, 0));
            Assert.True(FuelReserve.BufferChangedMaterially(1_000, 0));
            Assert.False(FuelReserve.BufferChangedMaterially(1_000, 1_200));
            Assert.True(FuelReserve.BufferChangedMaterially(1_000, 1_250));
            Assert.True(FuelReserve.BufferChangedMaterially(1_000, 750));
        }

        [Fact]
        public void TheTraceLine_NamesBurnLeadTargetAndStock()
        {
            Assert.Equal("FUEL_BUFFER SPRT node=N130 key=Gas:Hydrogen burn=3000000 lead=0.5 target=1500000 have=0",
                FuelReserve.FormatBufferLine("SPRT", "N130", HydrogenKey, 3_000_000.4, 0.5, 1_500_000, 0));
        }

        // ---- fixtures ----------------------------------------------------------------------

        internal static SpatialNode FoundedNode(WorldModel world, string id, Vec3 pos)
        {
            SpatialNode node = world.Graph.Add(pos, 1.0, id);
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            return node;
        }

        /// <summary>A manager that knows the depot's gas recipe, so the catalogue pass posts its gas
        /// top-up, and an oracle whose hydrogen reference is 10 ML at every node, as the live one
        /// reads for a node with no larger claim.</summary>
        private static ProductionManager GasManager()
        {
            var iceToHydrogen = new ConversionRecipe
            {
                BlueprintId = "Gas/OxygenGenerator/Hydrogen",
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Ice", Amount = 25.0 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_GasProperties", SubTypeId = "Hydrogen", Amount = 500.0 } },
                TimeSecs = 1.0f,
            };
            return new ProductionManager(
                new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
                {
                    { NodeCapabilityKind.FuelDepot, new List<ConversionRecipe> { iceToHydrogen } },
                },
                null,
                new HydrogenReferenceOracle());
        }

        private sealed class HydrogenReferenceOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }

            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference)
            {
                reference = kind == ItemKind.Hydrogen ? Reference : 0;
                return reference > 0;
            }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                reference = inventoryKey == HydrogenKey ? Reference : 0;
                return reference > 0;
            }

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                return inventoryKey == HydrogenKey ? 2.0 : 1.0;
            }
        }
    }

    /// <summary>The <c>FUEL_BUFFER</c> trace. In the debug-traces collection because it flips the
    /// process-wide <see cref="DebugTraces.Fuel"/> flag.</summary>
    [Collection(DebugTracesCollection.Name)]
    public class FuelBufferTraceTests
    {
        [Fact]
        public void TheTrace_IsWrittenBehindTheFuelDebugFlag_AndOnlyWhenTheBufferMoves()
        {
            DebugTraces.ResetToDefaults();
            try
            {
                WorldModel world = TestSupport.World();
                world.UtcNow = FuelBufferTests.T0;
                world.Tuning = FuelBufferTests.Tuning();
                SpatialNode node = FuelBufferTests.FoundedNode(world, "N1", new Vec3(0, 0, 0));
                FuelDraw.CreditBurn(node, FuelBufferTests.T0, world.Tuning, FuelBufferTests.HydrogenKey, 4_000_000 * world.Tuning.FuelBurnEmaHours);
                var manager = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
                var states = new List<FactionState> { new FactionState { Tag = "F" } };

                DebugTraces.Fuel = false;
                var off = new List<string>();
                manager.Tick(states, world, world.Tuning, off);
                Assert.DoesNotContain(off, l => l.Contains("FUEL_BUFFER"));

                DebugTraces.Fuel = true;
                var first = new List<string>();
                manager.Tick(states, world, world.Tuning, first);
                Assert.Contains(first, l => l.Contains("FUEL_BUFFER F node=N1 key=Gas:Hydrogen burn=4000000 lead=0.5 target=2000000"));

                var unchanged = new List<string>();
                world.UtcNow = world.UtcNow.AddMinutes(1);
                manager.Tick(states, world, world.Tuning, unchanged);
                Assert.DoesNotContain(unchanged, l => l.Contains("FUEL_BUFFER"));
            }
            finally
            {
                DebugTraces.ResetToDefaults();
            }
        }

    }
}
