using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class NexusAuthorityTests
    {
        [Fact]
        public void NoNexus_Simulates_RegardlessOfConfiguredAuthority()
        {
            string reason;
            // The standalone test server has no cluster and no configured authority; it must still run.
            Assert.True(NexusAuthority.ShouldSimulate(NexusPresence.Absent, 0, 0, out reason));
            Assert.Contains("standalone", reason);

            // A leftover authority ID from a cluster deployment must not silence a standalone server.
            Assert.True(NexusAuthority.ShouldSimulate(NexusPresence.Absent, 0, 7, out reason));
        }

        [Fact]
        public void UnresolvableId_FailsClosed()
        {
            string reason;
            // Nexus is present but unreadable: simulating would risk a second writer on shared
            // balances, so this defers even though an authority is configured and might be us.
            Assert.False(NexusAuthority.ShouldSimulate(NexusPresence.Unresolvable, 0, 2, out reason));
            Assert.Contains("could not be read", reason);
        }

        [Fact]
        public void UnconfiguredAuthority_OnACluster_FailsClosed()
        {
            string reason;
            Assert.False(NexusAuthority.ShouldSimulate(NexusPresence.Resolved, 3, 0, out reason));
            Assert.Contains("NexusAuthorityServerId", reason);
            // The reason names this server, so the operator can copy the value straight into tuning.json.
            Assert.Contains("3", reason);
        }

        [Fact]
        public void NegativeAuthorityId_IsTreatedAsUnset()
        {
            string reason;
            Assert.False(NexusAuthority.ShouldSimulate(NexusPresence.Resolved, 3, -1, out reason));
            Assert.Contains("unset", reason);
        }

        [Fact]
        public void MatchingServer_Simulates()
        {
            string reason;
            Assert.True(NexusAuthority.ShouldSimulate(NexusPresence.Resolved, 2, 2, out reason));
            Assert.Contains("authority", reason);
        }

        [Fact]
        public void NonMatchingServer_Defers()
        {
            string reason;
            Assert.False(NexusAuthority.ShouldSimulate(NexusPresence.Resolved, 4, 2, out reason));
            Assert.Contains("not the configured authority", reason);
        }

        [Fact]
        public void ExactlyOneServerOfAClusterSimulates()
        {
            // The property that makes the gate correct: for any configured authority, exactly one
            // member of the cluster runs. This is what protects the shared banking state.
            const int authority = 3;
            int simulating = 0;
            string reason;
            for (int serverId = 1; serverId <= 6; serverId++)
                if (NexusAuthority.ShouldSimulate(NexusPresence.Resolved, serverId, authority, out reason))
                    simulating++;
            Assert.Equal(1, simulating);
        }

        [Fact]
        public void ReasonIsAlwaysPopulated()
        {
            string reason;
            foreach (NexusPresence presence in new[]
                     { NexusPresence.Absent, NexusPresence.Resolved, NexusPresence.Unresolvable })
                for (int authority = 0; authority <= 2; authority++)
                {
                    NexusAuthority.ShouldSimulate(presence, 1, authority, out reason);
                    Assert.False(string.IsNullOrWhiteSpace(reason));
                }
        }

        [Fact]
        public void Role_FollowsPresenceAndDecision()
        {
            Assert.Equal(NexusRole.Standalone, NexusAuthority.RoleFor(NexusPresence.Absent, true));
            Assert.Equal(NexusRole.Authority, NexusAuthority.RoleFor(NexusPresence.Resolved, true));
            Assert.Equal(NexusRole.Agent, NexusAuthority.RoleFor(NexusPresence.Resolved, false));
            // No reply address to give the authority, so this server stays dark rather than relaying.
            Assert.Equal(NexusRole.Dark, NexusAuthority.RoleFor(NexusPresence.Unresolvable, false));
        }
    }
}
