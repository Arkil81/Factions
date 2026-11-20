using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class StallBackoffTests
    {
        private static readonly LoadGovernor.Pressure Short = LoadGovernor.Pressure.Short;
        private static readonly LoadGovernor.Pressure Hold = LoadGovernor.Pressure.Hold;
        private static readonly LoadGovernor.Pressure Clear = LoadGovernor.Pressure.Clear;

        [Fact]
        public void ShortHalvesDownToTheFloor()
        {
            var b = new StallBackoff();
            Assert.True(b.Update(Short)); Assert.Equal(0.5, b.Scale, 6);
            Assert.True(b.Update(Short)); Assert.Equal(0.25, b.Scale, 6);
            Assert.False(b.Update(Short)); Assert.Equal(StallBackoff.Floor, b.Scale, 6);
        }

        [Fact]
        public void HoldKeepsAndClearDoublesBackToOne()
        {
            var b = new StallBackoff();
            b.Update(Short); b.Update(Short);
            Assert.False(b.Update(Hold)); Assert.Equal(0.25, b.Scale, 6);
            Assert.True(b.Update(Clear)); Assert.Equal(0.5, b.Scale, 6);
            Assert.True(b.Update(Clear)); Assert.Equal(1.0, b.Scale, 6);
            Assert.False(b.Update(Clear)); Assert.Equal(1.0, b.Scale, 6);
        }

        [Fact]
        public void ResetReturnsToOneAndSaysWhetherItMoved()
        {
            var b = new StallBackoff();
            Assert.False(b.Reset());
            b.Update(Short);
            Assert.True(b.Reset());
            Assert.Equal(1.0, b.Scale, 6);
        }
    }
}
