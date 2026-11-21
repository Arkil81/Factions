using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>An agent names the NPC stations it stands that cannot call; the authority silences
    /// their nodes as it does its own.</summary>
    public class NexusMutedStationsTests
    {
        [Fact]
        public void TheMutedNodesSurviveTheReport()
        {
            var report = new NexusAgentReport { Session = "s", Muted = new List<string> { "N1", "N2" } };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            Assert.Equal(new[] { "N1", "N2" }, back.Muted);
        }

        [Fact]
        public void NoMutedNodesCostNothingOnTheWire()
        {
            var report = new NexusAgentReport { Session = "s" };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            Assert.Null(back.Muted);
        }

        [Fact]
        public void OnlyAnNpcStationWithNoAntennaIsSilenced()
        {
            Assert.True(StationCallRules.Silenced(true, false, false));
            Assert.False(StationCallRules.Silenced(true, true, false));
            Assert.True(StationCallRules.HostCalls(true, false));
            Assert.False(StationCallRules.HostCalls(true, true));
        }
    }
}
