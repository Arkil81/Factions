using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>An agent's player grids reach the authority's sensors through its report.</summary>
    public class NexusPlayerContactTests
    {
        private static NexusAgentReport RoundTrip(NexusAgentReport report)
        {
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            return back;
        }

        [Fact]
        public void AContactArrivesAsTheSameDetectionTarget()
        {
            var sampled = new PlayerContact
            {
                IdentityId = 144115188075855900L, GridEntityId = 87654321012345L,
                PositionKm = new Vec3(1234.5678901, -98.7654321, 4321.0004), Signature = 2.345678, BroadcastRadiusKm = 20,
            };
            NexusAgentReport back = RoundTrip(new NexusAgentReport
            {
                Session = "s", Contacts = new List<NexusPlayerContact> { NexusPlayerContact.From(sampled) },
            });

            PlayerContact c = Assert.Single(back.Contacts).ToContact();
            Assert.Equal(sampled.IdentityId, c.IdentityId);
            Assert.Equal(sampled.GridEntityId, c.GridEntityId);
            Assert.Equal(1234.568, c.PositionKm.X, 6);
            Assert.Equal(-98.765, c.PositionKm.Y, 6);
            Assert.Equal(4321.0, c.PositionKm.Z, 6);
            Assert.Equal(2.3457, c.Signature, 6);
            Assert.Equal(20.0, c.BroadcastRadiusKm, 6);
        }

        /// <summary>Null keeps the authority's last list; empty clears it. The lean serializer must
        /// not fold one into the other.</summary>
        [Fact]
        public void AnEmptyListIsNotAnAbsentOne()
        {
            Assert.Null(RoundTrip(new NexusAgentReport { Session = "s" }).Contacts);
            NexusAgentReport empty = RoundTrip(new NexusAgentReport { Session = "s", Contacts = new List<NexusPlayerContact>() });
            Assert.NotNull(empty.Contacts);
            Assert.Empty(empty.Contacts);
        }

        /// <summary>An unsampled signature still reads as the reference, not as silence: a zero
        /// crosses the wire as zero.</summary>
        [Fact]
        public void AZeroSignatureStaysZero()
        {
            var sampled = new PlayerContact { IdentityId = 7, GridEntityId = 8, PositionKm = new Vec3(1, 2, 3) };
            NexusAgentReport back = RoundTrip(new NexusAgentReport
            {
                Session = "s", Contacts = new List<NexusPlayerContact> { NexusPlayerContact.From(sampled) },
            });
            Assert.Equal(0.0, Assert.Single(back.Contacts).ToContact().Signature);
        }
    }
}
