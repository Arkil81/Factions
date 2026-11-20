using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The contact box that decides which pristine rock gets a collision grid.</summary>
    public class RockContactTests
    {
        [Fact]
        public void AtRest_TheBoxIsTheReachOnEverySide()
        {
            Vec3 min, max;
            RockContact.SweptBox(new Vec3(100, 200, 300), new Vec3(0, 0, 0), 1500, 10, out min, out max);
            Assert.Equal(-1400, min.X); Assert.Equal(1600, max.X);
            Assert.Equal(-1300, min.Y); Assert.Equal(1700, max.Y);
            Assert.Equal(-1200, min.Z); Assert.Equal(1800, max.Z);
        }

        [Fact]
        public void Moving_TheBoxReachesAheadAlongTheVelocityOnly()
        {
            Vec3 min, max;
            RockContact.SweptBox(new Vec3(0, 0, 0), new Vec3(-500, 0, 0), 1500, 10, out min, out max);
            Assert.Equal(-6500, min.X); Assert.Equal(1500, max.X);
            Assert.Equal(-1500, min.Y); Assert.Equal(1500, max.Y);
        }

        [Fact]
        public void NegativeInputs_AreTreatedAsZero()
        {
            Vec3 min, max;
            RockContact.SweptBox(new Vec3(0, 0, 0), new Vec3(10, 0, 0), -5, -1, out min, out max);
            Assert.Equal(0, min.X); Assert.Equal(0, max.X);
        }
    }
}
