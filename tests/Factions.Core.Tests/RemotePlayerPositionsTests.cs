using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The authority's store of players on other cluster members. Each member's report replaces its
    /// last one; a silent member ages out whole.
    /// </summary>
    public class RemotePlayerPositionsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

        private static RemotePlayer P(ulong steam, double x)
        {
            return new RemotePlayer { SteamId = steam, IdentityId = (long)steam, PositionM = new Vec3(x, 0, 0) };
        }

        [Fact]
        public void AReportReplacesThatMembersPreviousReport()
        {
            var s = new RemotePlayerPositions();
            Assert.True(s.Update(2, new[] { P(1, 10), P(2, 20) }, T0));
            Assert.False(s.Update(2, new[] { P(2, 25) }, T0.AddSeconds(1)));

            List<RemotePlayer> snap = s.Snapshot(T0.AddSeconds(1), MaxAge, null);
            Assert.Single(snap);
            Assert.Equal(2UL, snap[0].SteamId);
            Assert.Equal(25, snap[0].PositionM.X);
            Assert.Equal(2, snap[0].HomeServerId);
        }

        [Fact]
        public void ReportsFromTwoMembersAreBothHeld()
        {
            var s = new RemotePlayerPositions();
            s.Update(2, new[] { P(1, 10) }, T0);
            s.Update(4, new[] { P(3, 30) }, T0);
            Assert.Equal(2, s.Snapshot(T0, MaxAge, null).Count);
            Assert.Equal(2, s.HomeCount);
        }

        [Fact]
        public void ASilentMemberDropsOutOfTheSnapshotAndThenExpires()
        {
            var s = new RemotePlayerPositions();
            s.Update(2, new[] { P(1, 10) }, T0);
            s.Update(4, new[] { P(3, 30) }, T0.AddSeconds(4));

            DateTime later = T0.AddSeconds(6);
            List<RemotePlayer> snap = s.Snapshot(later, MaxAge, null);
            Assert.Single(snap);
            Assert.Equal(3UL, snap[0].SteamId);

            List<byte> expired = s.Expire(later, MaxAge);
            Assert.Equal(new List<byte> { 2 }, expired);
            Assert.Equal(1, s.HomeCount);
            Assert.True(s.Update(2, new[] { P(1, 11) }, later), "a member that expired resumes as new");
        }

        [Fact]
        public void APlayerInTwoReportsIsHeldOnceAtTheNewerPosition()
        {
            var s = new RemotePlayerPositions();
            s.Update(2, new[] { P(7, 100) }, T0);
            s.Update(4, new[] { P(7, 200) }, T0.AddSeconds(1));
            List<RemotePlayer> snap = s.Snapshot(T0.AddSeconds(1), MaxAge, null);
            Assert.Single(snap);
            Assert.Equal(200, snap[0].PositionM.X);
            Assert.Equal(4, snap[0].HomeServerId);
        }

        [Fact]
        public void APlayerConnectedLocallyIsExcluded()
        {
            var s = new RemotePlayerPositions();
            s.Update(2, new[] { P(7, 100), P(8, 50) }, T0);
            List<RemotePlayer> snap = s.Snapshot(T0, MaxAge, new HashSet<ulong> { 7UL });
            Assert.Single(snap);
            Assert.Equal(8UL, snap[0].SteamId);
        }

        [Fact]
        public void AnEmptyReportClearsThatMembersPlayers()
        {
            var s = new RemotePlayerPositions();
            s.Update(2, new[] { P(1, 10) }, T0);
            s.Update(2, new RemotePlayer[0], T0.AddSeconds(1));
            Assert.Empty(s.Snapshot(T0.AddSeconds(1), MaxAge, null));
            Assert.Equal("server2=0p/0s", s.Describe(T0.AddSeconds(1)));
        }

        [Fact]
        public void DescribeNamesEachMemberInIdOrder()
        {
            var s = new RemotePlayerPositions();
            Assert.Equal("none", s.Describe(T0));
            s.Update(4, new[] { P(3, 30) }, T0);
            s.Update(2, new[] { P(1, 10), P(2, 20) }, T0.AddSeconds(2));
            Assert.Equal("server2=2p/1s server4=1p/3s", s.Describe(T0.AddSeconds(3)));
        }
    }
}
