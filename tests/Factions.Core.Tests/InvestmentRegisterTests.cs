using System;
using System.Linq;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class InvestmentRegisterTests
    {
        [Fact]
        public void Post_KeysOnFactionCapAndTargetPlanetId_DifferentPlanetsDoNotCollide()
        {
            var register = new InvestmentRegister();
            DateTime expiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.True(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.8,
                ExpiresUtc = expiry, TargetPlanetId = "Terra",
            }));
            Assert.True(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.5,
                ExpiresUtc = expiry, TargetPlanetId = "Mars",
            }));

            Assert.Equal(2, register.Count);
            var forF = register.SnapshotFor("F");
            Assert.Contains(forF, r => r.TargetPlanetId == "Terra" && r.Urgency == 0.8);
            Assert.Contains(forF, r => r.TargetPlanetId == "Mars" && r.Urgency == 0.5);
        }

        [Fact]
        public void Post_SamePlanetRefreshesInPlace_RegressionGuard()
        {
            var register = new InvestmentRegister();
            DateTime firstExpiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime refreshedExpiry = firstExpiry.AddMinutes(1);

            Assert.True(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.5,
                ExpiresUtc = firstExpiry, TargetPlanetId = "Terra",
            }));
            Assert.False(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.9,
                ExpiresUtc = refreshedExpiry, TargetPlanetId = "Terra",
            }));

            Assert.Equal(1, register.Count);
            InvestmentRequest only = register.SnapshotFor("F").Single();
            Assert.Equal(0.9, only.Urgency);
            Assert.Equal(refreshedExpiry, only.ExpiresUtc);
        }

        [Fact]
        public void Post_NullTargetPlanetId_BehavesExactlyAsBeforeThisFeature_RegressionGuard()
        {
            // Every non-Shipyard-local-investment capability request leaves TargetPlanetId null --
            // this must keep deduping on (FactionTag, Cap) alone, same as pre-planet-awareness.
            var register = new InvestmentRegister();
            DateTime expiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.True(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Refinery, Urgency = 0.5, ExpiresUtc = expiry,
            }));
            Assert.False(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Refinery, Urgency = 0.9, ExpiresUtc = expiry,
            }));

            Assert.Equal(1, register.Count);
            Assert.Equal(0.9, register.SnapshotFor("F").Single().Urgency);
        }

        [Fact]
        public void Prune_RemovesExpiredAndKeepsIndexConsistentForFurtherPosts()
        {
            var register = new InvestmentRegister();
            DateTime now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.5,
                ExpiresUtc = now.AddMinutes(-1), TargetPlanetId = "Terra",
            });
            register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.5,
                ExpiresUtc = now.AddMinutes(5), TargetPlanetId = "Mars",
            });

            register.Prune(now);
            Assert.Equal(1, register.Count);
            Assert.Equal("Mars", register.SnapshotFor("F").Single().TargetPlanetId);

            // Index must still be correct after the Prune-triggered rebuild -- a fresh post for the
            // now-pruned key re-adds cleanly rather than colliding with a stale index entry.
            Assert.True(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.5,
                ExpiresUtc = now.AddMinutes(5), TargetPlanetId = "Terra",
            }));
            Assert.Equal(2, register.Count);
        }
    

        [Fact]
        public void Post_KeysOnTargetFeatureToo_TwoNestsAreTwoNeeds()
        {
            // A request scoped to a feature names a region no body claims. Two features, and a
            // feature beside a planet, must all track independently -- or FCTM's six nests would
            // collapse into one request and five of them could never be asked for.
            var register = new InvestmentRegister();
            var expiry = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
            Assert.True(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.8,
                ExpiresUtc = expiry, TargetFeature = "Far Nest Umbra",
            }));
            Assert.True(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.8,
                ExpiresUtc = expiry, TargetFeature = "Far Nest Vigil",
            }));
            Assert.True(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.8,
                ExpiresUtc = expiry, TargetPlanetId = "Terra", TargetSite = StationSite.Surface,
            }));
            // ...and the same feature again is a refresh, not a fourth entry.
            Assert.False(register.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, Urgency = 0.8,
                ExpiresUtc = expiry, TargetFeature = "Far Nest Umbra",
            }));
            Assert.Equal(3, register.Count);
        }
    }
}
