using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The assembler input-tray rules (FAC-504). The host's pushes and pulls are
    /// live-verified with <c>factions asmtest</c>.</summary>
    public class AssemblerInputPlannerTests
    {
        // SE's per-unit volumes, m³ (PhysicalItems.sbc / Components.sbc, litres ÷ 1000).
        private static readonly Dictionary<string, double> Vol = new Dictionary<string, double>
        {
            { "Ingot/Iron", 0.000127 }, { "Ingot/Nickel", 0.000112 }, { "Ingot/Silicon", 0.000429 },
            { "Ingot/Cobalt", 0.000112 }, { "Ingot/Silver", 0.000095 }, { "Ingot/Stone", 0.00037 },
            { "Tool/AngleGrinder", 0.02 }, { "Big/Thing", 5.0 },
        };

        private static double V(string k) { double v; return Vol.TryGetValue(k, out v) ? v : 0.001; }

        private static AssemblerQueueEntry Q(string recipe, double amount, params object[] perUnit)
        {
            var e = new AssemblerQueueEntry { Recipe = recipe, Amount = amount };
            for (int i = 0; i < perUnit.Length; i += 2)
                e.PerUnit.Add(new KeyValuePair<string, double>((string)perUnit[i], (double)perUnit[i + 1]));
            return e;
        }

        private static Dictionary<string, double> Inv(params object[] kv)
        {
            var d = new Dictionary<string, double>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = (double)kv[i + 1];
            return d;
        }

        private static AssemblerQueueEntry SteelPlate(double n) { return Q("SteelPlate", n, "Ingot/Iron", 21.0); }

        [Fact]
        public void NoQueueEmptiesTheTray()
        {
            AssemblerInputPlan p = AssemblerInputPlanner.Plan(new List<AssemblerQueueEntry>(),
                Inv("Ingot/Iron", 50.0, "Ingot/Cobalt", 3.0), Inv(), V, 4.0);
            Assert.Equal(AssemblerInputMode.Idle, p.Mode);
            Assert.Equal(50.0, p.PushOut["Ingot/Iron"]);
            Assert.Equal(3.0, p.PushOut["Ingot/Cobalt"]);
            Assert.Empty(p.PullIn);
        }

        [Fact]
        public void AQueueNothingInReachCanBuildEmptiesTheTray()
        {
            // Iron for one plate in the tray but the queue wants silicon it has none of: the iron is
            // a partial set vanilla would have left there for good.
            var queue = new List<AssemblerQueueEntry> { Q("Computer", 5, "Ingot/Iron", 0.5, "Ingot/Silicon", 0.2) };
            AssemblerInputPlan p = AssemblerInputPlanner.Plan(queue, Inv("Ingot/Iron", 30.0), Inv("Ingot/Iron", 100.0), V, 4.0);
            Assert.Equal(AssemblerInputMode.Blocked, p.Mode);
            Assert.Equal(30.0, p.PushOut["Ingot/Iron"]);
            Assert.Empty(p.PullIn);
        }

        [Fact]
        public void TheFirstBuildableItemIsStockedNotQueueIndexZero()
        {
            var queue = new List<AssemblerQueueEntry>
            {
                Q("AngleGrinder2", 1, "Tool/AngleGrinder", 1.0, "Ingot/Cobalt", 1.0),
                Q("AngleGrinder", 1, "Ingot/Iron", 3.0, "Ingot/Nickel", 1.0),
            };
            AssemblerInputPlan p = AssemblerInputPlanner.Plan(queue, Inv(), Inv("Ingot/Iron", 100.0, "Ingot/Nickel", 100.0, "Ingot/Cobalt", 10.0), V, 4.0);
            Assert.Equal(AssemblerInputMode.Stocking, p.Mode);
            Assert.Equal(1, p.QueueIndex);
            Assert.Equal(3.0, p.PullIn["Ingot/Iron"]);
            Assert.Equal(1.0, p.PullIn["Ingot/Nickel"]);
            Assert.False(p.PullIn.ContainsKey("Ingot/Cobalt"));
        }

        [Fact]
        public void ForeignItemsAreClearedAndOnlyTheDeficitIsPulled()
        {
            var queue = new List<AssemblerQueueEntry> { SteelPlate(10) };
            AssemblerInputPlan p = AssemblerInputPlanner.Plan(queue,
                Inv("Ingot/Iron", 50.0, "Ingot/Cobalt", 7.0), Inv("Ingot/Iron", 1000.0), V, 4.0);
            Assert.Equal(AssemblerInputMode.Stocking, p.Mode);
            Assert.Equal(10, p.Units);
            Assert.Equal(7.0, p.PushOut["Ingot/Cobalt"]);
            Assert.False(p.PushOut.ContainsKey("Ingot/Iron"));
            Assert.Equal(210.0 - 50.0, p.PullIn["Ingot/Iron"], 6);
        }

        [Fact]
        public void UnitsAreWholeAndBoundedByWhatIsInReach()
        {
            // 100 iron in reach makes 4 plates (84), not 4.76.
            var queue = new List<AssemblerQueueEntry> { SteelPlate(50) };
            AssemblerInputPlan p = AssemblerInputPlanner.Plan(queue, Inv(), Inv("Ingot/Iron", 100.0), V, 4.0);
            Assert.Equal(4, p.Units);
            Assert.Equal(84.0, p.PullIn["Ingot/Iron"], 6);
        }

        [Fact]
        public void UnitsAreBoundedByTheTray()
        {
            // 0.95 × 4 m³ ÷ (21 × 0.000127) = 1424.8 → 1424 plates.
            var queue = new List<AssemblerQueueEntry> { SteelPlate(100000) };
            AssemblerInputPlan p = AssemblerInputPlanner.Plan(queue, Inv(), Inv("Ingot/Iron", 1e9), V, 4.0);
            Assert.Equal(1424, p.Units);
        }

        [Fact]
        public void AnItemOneUnitOfWhichCannotFitIsSkipped()
        {
            var queue = new List<AssemblerQueueEntry> { Q("Huge", 1, "Big/Thing", 1.0), SteelPlate(1) };
            AssemblerInputPlan p = AssemblerInputPlanner.Plan(queue, Inv(), Inv("Big/Thing", 5.0, "Ingot/Iron", 100.0), V, 4.0);
            Assert.Equal(1, p.QueueIndex);
        }

        [Fact]
        public void OverStockOfABelongingItemStaysUntilItsRoomIsNeeded()
        {
            var computer = Q("Computer", 10, "Ingot/Iron", 0.5, "Ingot/Silicon", 0.2);
            // Room to spare: the extra iron stays.
            AssemblerInputPlan roomy = AssemblerInputPlanner.Plan(new List<AssemblerQueueEntry> { computer },
                Inv("Ingot/Iron", 100.0), Inv("Ingot/Silicon", 10.0), V, 4.0);
            Assert.False(roomy.PushOut.ContainsKey("Ingot/Iron"));
            Assert.Equal(2.0, roomy.PullIn["Ingot/Silicon"], 6);

            // A tray full of iron: only the excess over the 10 units' 5 kg goes, so the silicon fits.
            double fullIron = 4.0 / 0.000127;
            AssemblerInputPlan full = AssemblerInputPlanner.Plan(new List<AssemblerQueueEntry> { computer },
                Inv("Ingot/Iron", fullIron), Inv("Ingot/Silicon", 10.0), V, 4.0);
            Assert.Equal(fullIron - 5.0, full.PushOut["Ingot/Iron"], 3);
            Assert.Equal(2.0, full.PullIn["Ingot/Silicon"], 6);
        }

        [Fact]
        public void TheMainJamIsCleared()
        {
            // Measured on Main 2026-10-28: the tray at 4.0000 of 4 m³ under a tool-tier queue whose
            // feeding tier-1 grinder sits last.
            var tray = Inv("Ingot/Cobalt", 9614.718845, "Ingot/Silver", 220.405429, "Ingot/Stone", 7843.821743);
            var queue = new List<AssemblerQueueEntry>
            {
                Q("AngleGrinder2", 1, "Tool/AngleGrinder", 1.0, "Ingot/Cobalt", 1.0),
                Q("AngleGrinder3", 1, "Tool/AngleGrinder2", 1.0, "Ingot/Silver", 1.0),
                Q("AngleGrinder", 1, "Ingot/Iron", 3.0, "Ingot/Nickel", 1.0),
            };
            AssemblerInputPlan p = AssemblerInputPlanner.Plan(queue, tray, Inv("Ingot/Iron", 500.0, "Ingot/Nickel", 50.0), V, 4.0);
            Assert.Equal(AssemblerInputMode.Stocking, p.Mode);
            Assert.Equal(2, p.QueueIndex);
            Assert.Equal(3, p.PushOut.Count);
            Assert.Equal(7843.821743, p.PushOut["Ingot/Stone"], 6);
            Assert.Equal(3.0, p.PullIn["Ingot/Iron"]);
        }

        [Fact]
        public void AStockedTrayIsANoOp()
        {
            var queue = new List<AssemblerQueueEntry> { SteelPlate(2) };
            AssemblerInputPlan p = AssemblerInputPlanner.Plan(queue, Inv("Ingot/Iron", 42.0), Inv("Ingot/Iron", 10.0), V, 4.0);
            Assert.Equal(AssemblerInputMode.Stocking, p.Mode);
            Assert.True(p.IsNoOp);
        }

        [Fact]
        public void AnEmptyTrayWithNothingQueuedIsANoOp()
        {
            Assert.True(AssemblerInputPlanner.Plan(null, Inv(), Inv(), V, 4.0).IsNoOp);
        }
    }
}
