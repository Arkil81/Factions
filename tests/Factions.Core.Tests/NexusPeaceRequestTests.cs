using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A truce request judged on a Nexus agent reaches the authority on the store stream:
    /// both directions of it survive the report's codec with what the authority books it by.</summary>
    public class NexusPeaceRequestTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void APeaceRequestSurvivesTheReport(bool offer)
        {
            var report = new NexusAgentReport
            {
                Session = "s",
                StoreEvents = new List<NexusStoreEvent>
                {
                    new NexusStoreEvent
                    {
                        Seq = 3, Kind = NexusStoreEventKind.Diplomacy, Seller = "EXP", Key = "IMPR", Buy = offer,
                        Buyer = 144115188075855900L, Steam = 76561198000000001UL,
                    },
                },
            };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            NexusStoreEvent e = Assert.Single(back.StoreEvents);
            Assert.Equal(NexusStoreEventKind.Diplomacy, e.Kind);
            Assert.Equal("EXP", e.Seller);
            Assert.Equal("IMPR", e.Key);
            Assert.Equal(offer, e.Buy);
            Assert.Null(e.Node);
            Assert.Equal(144115188075855900L, e.Buyer);
        }
    }
}
