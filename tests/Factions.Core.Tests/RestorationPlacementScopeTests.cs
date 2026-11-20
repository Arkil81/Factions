using System;
using System.Threading;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The safe-zone exception for a restore's own placement: only the grid in scope, only
    /// while the scope is open, only on the thread that opened it. The characteristic failure is a
    /// leak — a player's build inside a station zone answered yes because a scope was left open.</summary>
    public class RestorationPlacementScopeTests
    {
        [Fact]
        public void Outside_the_scope_nothing_is_allowed()
        {
            Assert.False(RestorationPlacementScope.AllowsBuildingOn(42));
            Assert.False(RestorationPlacementScope.AllowsBuildingInArea());
            Assert.False(RestorationPlacementScope.IsOpen);
        }

        [Fact]
        public void Inside_the_scope_only_the_restored_grid_may_be_built_on()
        {
            using (RestorationPlacementScope.Enter(42))
            {
                Assert.True(RestorationPlacementScope.AllowsBuildingOn(42));
                Assert.False(RestorationPlacementScope.AllowsBuildingOn(43));
                Assert.True(RestorationPlacementScope.AllowsBuildingInArea());
            }
            Assert.False(RestorationPlacementScope.AllowsBuildingOn(42));
            Assert.False(RestorationPlacementScope.AllowsBuildingInArea());
        }

        [Fact]
        public void A_throw_inside_the_scope_still_closes_it()
        {
            Action build = () =>
            {
                using (RestorationPlacementScope.Enter(7)) throw new InvalidOperationException("build failed");
            };
            Assert.Throws<InvalidOperationException>(build);
            Assert.False(RestorationPlacementScope.AllowsBuildingOn(7));
        }

        [Fact]
        public void The_scope_is_this_threads_only_and_never_nests()
        {
            using (RestorationPlacementScope.Enter(9))
            {
                bool other = true;
                var t = new Thread(() => other = RestorationPlacementScope.AllowsBuildingOn(9) || RestorationPlacementScope.AllowsBuildingInArea());
                t.Start();
                t.Join();
                Assert.False(other);
                Assert.Throws<InvalidOperationException>(() => RestorationPlacementScope.Enter(10));
                Assert.True(RestorationPlacementScope.AllowsBuildingOn(9));
            }
        }

        [Fact]
        public void A_group_scope_covers_every_grid_of_the_ship_and_no_other()
        {
            using (RestorationPlacementScope.Enter(new long[] { 42, 43, 44 }))
            {
                Assert.True(RestorationPlacementScope.IsOpen);
                Assert.True(RestorationPlacementScope.AllowsBuildingOn(42));
                Assert.True(RestorationPlacementScope.AllowsBuildingOn(43));
                Assert.True(RestorationPlacementScope.AllowsBuildingOn(44));
                Assert.False(RestorationPlacementScope.AllowsBuildingOn(45));
                Assert.False(RestorationPlacementScope.AllowsBuildingOn(0));
            }
            Assert.False(RestorationPlacementScope.IsOpen);
            Assert.False(RestorationPlacementScope.AllowsBuildingOn(43));
        }

        [Fact]
        public void A_group_scope_refuses_an_empty_or_zero_grid_set()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RestorationPlacementScope.Enter(new long[0]));
            Assert.Throws<ArgumentOutOfRangeException>(() => RestorationPlacementScope.Enter(new long[] { 5, 0 }));
            Assert.False(RestorationPlacementScope.IsOpen);
        }
    }
}
