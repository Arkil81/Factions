using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class LoadLedgerTests
    {
        private static LoadLedger.SimulatedGrid G(long id, string party, int blocks, bool awake = false, bool isStatic = false)
        {
            return new LoadLedger.SimulatedGrid { GridId = id, Party = party, Name = "g" + id, Blocks = blocks, Awake = awake, IsStatic = isStatic };
        }

        private static readonly LoadLedger.Coefficients C = new LoadLedger.Coefficients();

        [Fact]
        public void PhysicsIsNormalisedToTheMeasuredTotal()
        {
            var r = LoadLedger.Compute(
                new[] { G(1, "a", 100, awake: true), G(2, "b", 300), G(3, "b", 50, awake: true) },
                null, null, 4.0, 0, 0, C);

            double sum = 0;
            foreach (var g in r.Grids) sum += g.PhysicsMs;
            Assert.Equal(4.0, sum, 9);
            Assert.Equal(4.0, r.Parties["a"].PhysicsMs + r.Parties["b"].PhysicsMs, 9);
            Assert.Equal(0.0, r.UnattributedPhysicsMs);
        }

        [Fact]
        public void AStaticGridIsChargedNoPhysics()
        {
            var r = LoadLedger.Compute(
                new[] { G(1, "station", 3000, isStatic: true), G(2, "ship", 287, awake: true) },
                null, null, 2.0, 0, 0, C);

            Assert.Equal(0.0, r.Parties["station"].PhysicsMs);
            Assert.Equal(2.0, r.Parties["ship"].PhysicsMs, 9);
            Assert.Equal(1, r.Parties["station"].SimulatedGrids);
        }

        [Fact]
        public void AnAwakeGridWeighsTheAwakeMultiplierMoreThanTheSameGridAsleep()
        {
            double asleep = LoadLedger.PhysicsWeight(500, false, false, C);
            double awake = LoadLedger.PhysicsWeight(500, true, false, C);
            Assert.Equal(15.0, awake / asleep, 9);
        }

        [Fact]
        public void TheDefaultExponentReproducesTheBenchRatio()
        {
            // 2026-09-23 bench: 3,387 vs 1,208 blocks, both large grid, both drifting, measured
            // 2.07 vs 0.52 ms/frame -- about 4x.
            double small = LoadLedger.PhysicsWeight(1208, true, false, C);
            double large = LoadLedger.PhysicsWeight(3387, true, false, C);
            Assert.InRange(large / small, 3.5, 4.5);
        }

        [Fact]
        public void TheExponentIsTheCoefficientsNotAConstant()
        {
            var steep = new LoadLedger.Coefficients { SizeExponent = 2.2 };
            double ratio = LoadLedger.PhysicsWeight(3387, false, false, steep)
                         / LoadLedger.PhysicsWeight(1208, false, false, steep);
            Assert.InRange(ratio, 9.0, 10.0);
        }

        [Fact]
        public void WithNoDynamicGridThePhysicsStaysUnattributed()
        {
            var r = LoadLedger.Compute(new[] { G(1, "station", 1000, isStatic: true) }, null, null, 1.5, 0, 0, C);
            Assert.Equal(1.5, r.UnattributedPhysicsMs, 9);
            Assert.Equal(0.0, r.Parties["station"].PhysicsMs);
        }

        [Fact]
        public void UnmeasuredPhysicsIsNotAttributed()
        {
            var r = LoadLedger.Compute(new[] { G(1, "a", 100, awake: true) }, null, null, double.NaN, 0, 0, C);
            Assert.Equal(0.0, r.Parties["a"].PhysicsMs);
            Assert.Equal(0.0, r.UnattributedPhysicsMs);
        }

        [Fact]
        public void LogicGoesToTheGridsPartyEvenWhenTheGridIsNotSimulated()
        {
            var logic = new Dictionary<long, LoadLedger.GridLogic>
            {
                { 1, new LoadLedger.GridLogic { SerialMs = 0.5 } },
                { 7, new LoadLedger.GridLogic { SerialMs = 0.2 } },   // not simulated, census knows it
                { 8, new LoadLedger.GridLogic { SerialMs = 0.1 } },   // census never saw it
                { 0, new LoadLedger.GridLogic { SerialMs = 0.3 } },   // on no grid
            };
            var r = LoadLedger.Compute(new[] { G(1, "a", 100) }, logic,
                id => id == 7 ? "b" : null, 0.0, 0, 0, C);

            Assert.Equal(0.5, r.Parties["a"].LogicMs, 9);
            Assert.Equal(0.2, r.Parties["b"].LogicMs, 9);
            Assert.Equal(0.1, r.Parties[LoadLedger.Unknown].LogicMs, 9);
            Assert.Equal(0.3, r.Parties[LoadLedger.World].LogicMs, 9);
            Assert.Equal(1.1, r.LogicMs, 9);
            Assert.Equal(0.5, r.Grids[0].LogicMs, 9);
        }

        [Fact]
        public void ParallelLogicIsScaledByTheWallToCpuRatio()
        {
            var logic = new Dictionary<long, LoadLedger.GridLogic>
            {
                { 1, new LoadLedger.GridLogic { SerialMs = 0.4, ParallelCpuMs = 1.0 } },
            };
            // the pass cost 2.0 ms of CPU and the main thread waited 0.5 ms for it
            var r = LoadLedger.Compute(new[] { G(1, "a", 10) }, logic, null, 0.0, 0.5, 2.0, C);
            Assert.Equal(0.25, r.ParallelScale, 9);
            Assert.Equal(0.4 + 0.25, r.Parties["a"].LogicMs, 9);
        }

        [Fact]
        public void ParallelScaleNeverExceedsOne()
        {
            var r = LoadLedger.Compute(null, null, null, 0.0, 3.0, 1.0, C);
            Assert.Equal(1.0, r.ParallelScale, 9);
        }

        [Fact]
        public void GridsAreRankedByTotalCost()
        {
            var logic = new Dictionary<long, LoadLedger.GridLogic>
            {
                { 2, new LoadLedger.GridLogic { SerialMs = 5.0 } },
            };
            var r = LoadLedger.Compute(new[] { G(1, "a", 1000, awake: true), G(2, "a", 10) }, logic, null, 1.0, 0, 0, C);
            Assert.Equal(2, r.Grids[0].GridId);
        }
    }
}
