using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A refuel or rearm on a Nexus agent reaches the authority as one Supply event: the
    /// units per key, what was paid, and the service, for the authority to book as it books its own.</summary>
    public class NexusSupplyEventTests
    {
        [Fact]
        public void ASupplyEventSurvivesTheReport()
        {
            var report = new NexusAgentReport
            {
                Session = "s",
                StoreEvents = new List<NexusStoreEvent>
                {
                    new NexusStoreEvent
                    {
                        Kind = NexusStoreEventKind.Supply, Node = "N4", Seller = "rearm", Credits = 1850,
                        Buyer = 77, Steam = 76561198000000002UL,
                        Parts = new Dictionary<string, long> { { "AmmoMagazine:NATO_25x184mm", 40 }, { "AmmoMagazine:Missile200mm", 6 } },
                    },
                },
            };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            NexusStoreEvent e = Assert.Single(back.StoreEvents);
            Assert.Equal(NexusStoreEventKind.Supply, e.Kind);
            Assert.Equal("rearm", e.Seller);
            Assert.Equal(1850, e.Credits);
            Assert.Equal(40, e.Parts["AmmoMagazine:NATO_25x184mm"]);
            Assert.Equal(6, e.Parts["AmmoMagazine:Missile200mm"]);
            Assert.Equal(76561198000000002UL, e.Steam);
        }
    }
}
