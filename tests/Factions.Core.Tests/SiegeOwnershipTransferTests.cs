using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A siege is against an owner. When the node changes hands it ends, and the new owner's fire on
    /// their own station never counts as a raid, breached zone or not. Live case: EXP took STCG's
    /// N096 by presence claim mid-siege and its continued fire stripped its own hold (FAC-473).
    /// </summary>
    public class SiegeOwnershipTransferTests
    {
        private static WorldModel World()
        {
            WorldModel w = TestSupport.World();
            FactionTuning t = w.Tuning;
            t.RaidZoneIntegrityPool = 1000.0;
            t.RaidZoneIntegrityRegenSeconds = 30.0;
            t.RaidZoneSiegeDwellSeconds = 300.0;
            t.PlayerRaidBlockDamageForSuccess = 20000.0;
            t.PlayerConquerOfferMinutes = 10.0;
            t.ConquerHoldCeiling = 0.15;
            return w;
        }

        /// <summary>A founded station whose zone a player siege has breached and whose raid has
        /// already succeeded once.</summary>
        private static SpatialNode BreachedStation(WorldModel w, string owner)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N096");
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.1;
            n.StableStationId = 96;
            n.ZoneIntegrity = 0;
            n.ZoneIntegrityAtUtc = w.UtcNow;
            n.SiegeStartedUtc = w.UtcNow.AddMinutes(-6);
            n.ZoneBreachedUtc = w.UtcNow.AddMinutes(-1);
            n.PlayerRaidBlockDamage = 1500;
            n.PlayerRaidSucceededUtc = w.UtcNow.AddSeconds(-30);
            return n;
        }

        private static void AssertNoSiege(SpatialNode n)
        {
            Assert.Equal(default(DateTime), n.SiegeStartedUtc);
            Assert.Equal(default(DateTime), n.ZoneBreachedUtc);
            Assert.Equal(default(DateTime), n.PlayerRaidSucceededUtc);
            Assert.Equal(0.0, n.PlayerRaidBlockDamage);
            Assert.False(PlayerSiege.HasStationSiege(n));
        }

        [Fact]
        public void ClaimingABreachedDerelict_EndsItsSiege()
        {
            WorldModel w = World();
            SpatialNode n = BreachedStation(w, NeutralFaction.Tag);

            Assert.True(w.TryClaim("EXP", n.Id, true));

            Assert.Equal("EXP", n.OwnerFactionTag);
            AssertNoSiege(n);
        }

        [Fact]
        public void CapturingABreachedStation_EndsItsSiege()
        {
            WorldModel w = World();
            SpatialNode n = BreachedStation(w, "STCG");

            Assert.Equal("STCG", w.CaptureNode("EXP", n.Id));

            Assert.Equal("EXP", n.OwnerFactionTag);
            AssertNoSiege(n);
        }

        [Fact]
        public void TheOwnersFire_IsNeverRaidDamage_EvenThroughABreach()
        {
            WorldModel w = World();
            SpatialNode n = BreachedStation(w, "EXP");

            Assert.True(PlayerSiege.IsOwnersFire(n, "EXP"));
            Assert.False(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow, "EXP"));
        }

        /// <summary>The negative control: the guard is about the owner, not about breached zones.</summary>
        [Fact]
        public void AnotherFactionsFire_StillCountsThroughABreach()
        {
            WorldModel w = World();
            SpatialNode n = BreachedStation(w, "STCG");

            Assert.False(PlayerSiege.IsOwnersFire(n, "EXP"));
            Assert.True(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow, "EXP"));
        }

        /// <summary>A factionless player owns nothing, so a breach stays open to their fire.</summary>
        [Fact]
        public void AFactionlessAttackersFire_StillCountsThroughABreach()
        {
            WorldModel w = World();
            SpatialNode n = BreachedStation(w, "STCG");

            Assert.False(PlayerSiege.IsOwnersFire(n, null));
            Assert.True(PlayerSiege.MayAddRaidDamage(n, w.Tuning, w.UtcNow, null));
        }
    }
}
