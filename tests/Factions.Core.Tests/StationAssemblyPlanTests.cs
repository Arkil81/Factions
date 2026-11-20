using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class StationAssemblyPlanTests
    {
        [Fact]
        public void BlocksGoCoreFirstThenOutward()
        {
            // a line of five blocks, saved out of order: the middle one goes first
            int[] x = { 4, 0, 2, 1, 3 };
            int[] o = StationAssemblyPlan.CoreOutward(x, new int[5], new int[5]);
            Assert.Equal(2, x[o[0]]);
            Assert.Equal(new[] { 0, 4 }, new[] { x[o[3]], x[o[4]] }.OrderBy(v => v).ToArray());
            Assert.Equal(5, o.Distinct().Count());
        }

        [Fact]
        public void TiesKeepSaveOrder()
        {
            int[] o = StationAssemblyPlan.CoreOutward(new[] { 1, -1, 1, -1 }, new int[4], new int[4]);
            Assert.Equal(new[] { 0, 1, 2, 3 }, o);
        }

        [Fact]
        public void TheNextChunkAimsAtTheTarget()
        {
            Assert.Equal(100, StationAssemblyPlan.NextChunk(200, 50, 25, 20, 500));
            Assert.Equal(20, StationAssemblyPlan.NextChunk(30, 400, 25, 20, 500));
        }

        [Fact]
        public void ACheapChunkAtMostDoubles()
        {
            Assert.Equal(200, StationAssemblyPlan.NextChunk(100, 1, 25, 20, 500));
            Assert.Equal(200, StationAssemblyPlan.NextChunk(100, 0, 25, 20, 500));
            Assert.Equal(500, StationAssemblyPlan.NextChunk(400, 1, 25, 20, 500));
        }
    }
}
