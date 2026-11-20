using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Taking a node that carries no station. There is no safe zone to empty and no blocks to
    /// damage, so a player's presence is the whole attack: the raid opens in phase 2 already
    /// successful, and the only clock left to run is the conquer offer's. Leaving before it is due
    /// gives the node back.
    ///
    /// <para>The two raid shapes are disjoint by construction — a founded node can only be reached
    /// through its zone, an unfounded one only by standing on it — so several of these tests assert
    /// against the founded case as much as for the unfounded one.</para>
    /// </summary>
    public class PresenceRaidTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                RaidZoneIntegrityPool = 1000.0,
                RaidZoneIntegrityRegenSeconds = 30.0,
                RaidZoneRaidLapseSeconds = 30.0,
                RaidZoneSiegeDwellSeconds = 300.0,
                PlayerRaidBlockDamageForSuccess = 20000.0,
                PlayerConquerOfferMinutes = 10.0,
                PresenceRaidRadiusKm = 3.0,
                PresenceRaidHysteresisKm = 0.5,
            };
        }

        /// <summary>A node with no station on it — StableStationId 0 is what "unfounded" is.</summary>
        private static SpatialNode Unfounded()
        {
            return new SpatialNode { Id = "N1", OwnerFactionTag = "DEF", ZoneIntegrity = -1 };
        }

        private static SpatialNode Founded()
        {
            SpatialNode n = Unfounded();
            n.StableStationId = 77;
            return n;
        }

        // ── the mechanic ────────────────────────────────────────────────────────

        [Fact]
        public void Presence_OpensInPhaseTwo_AlreadySucceeded()
        {
            SpatialNode n = Unfounded();
            FactionTuning t = Tuning();

            Assert.True(PlayerSiege.BeginPresenceRaid(n, t, T0));

            Assert.NotEqual(default(DateTime), n.ZoneBreachedUtc);          // phase 2, not phase 1
            Assert.Equal(T0, n.PlayerRaidSucceededUtc);                     // and already won
            Assert.Equal(0.0, n.PlayerRaidBlockDamage);                     // on no damage at all
            Assert.True(PlayerSiege.IsPresenceRaid(n));
        }

        [Fact]
        public void TheOnlyClockLeft_IsTheConquerOffer()
        {
            SpatialNode n = Unfounded();
            FactionTuning t = Tuning();
            PlayerSiege.BeginPresenceRaid(n, t, T0);

            Assert.False(PlayerSiege.ConquerOfferDue(n, t, T0.AddMinutes(9.9)));
            Assert.True(PlayerSiege.ConquerOfferDue(n, t, T0.AddMinutes(10)));
        }

        /// <summary>The defender's window. `FleetManager.PlayerAttackThreat` sizes its Engage dwell
        /// on this, so a presence raid answering with anything other than "ten minutes from now"
        /// would leave a Guard with no time to be dispatched into.</summary>
        [Fact]
        public void TheLossDeadline_IsTenMinutesFromTheMomentPresenceBegan()
        {
            SpatialNode n = Unfounded();
            FactionTuning t = Tuning();
            PlayerSiege.BeginPresenceRaid(n, t, T0);

            Assert.Equal(T0.AddMinutes(10), PlayerSiege.LossDeadlineUtc(n, t));
        }

        // ── holding it ──────────────────────────────────────────────────────────

        [Fact]
        public void StayingHoldsIt_AndTheOfferComesDue()
        {
            SpatialNode n = Unfounded();
            FactionTuning t = Tuning();
            PlayerSiege.BeginPresenceRaid(n, t, T0);

            Assert.False(PlayerSiege.Maintain(n, t, T0.AddMinutes(5), true));
            Assert.True(PlayerSiege.ConquerOfferDue(n, t, T0.AddMinutes(10)));
        }

        [Fact]
        public void LeavingBeforeTheOfferIsDue_EndsTheRaidOutright()
        {
            SpatialNode n = Unfounded();
            FactionTuning t = Tuning();
            PlayerSiege.BeginPresenceRaid(n, t, T0);

            Assert.True(PlayerSiege.Maintain(n, t, T0.AddMinutes(5), false));

            Assert.Equal(default(DateTime), n.PlayerRaidSucceededUtc);
            Assert.Equal(default(DateTime), n.ZoneBreachedUtc);
            Assert.False(PlayerSiege.IsPresenceRaid(n));
            // Coming back later does not resume it -- the ten minutes start again.
            Assert.False(PlayerSiege.ConquerOfferDue(n, t, T0.AddMinutes(11)));
        }

        /// <summary>Returning re-arms the mechanic rather than leaving the node permanently spent.
        /// The clock restarts from the second arrival, which is what makes "hold it" mean anything.
        /// </summary>
        [Fact]
        public void ComingBack_StartsTheTenMinutesAgain()
        {
            SpatialNode n = Unfounded();
            FactionTuning t = Tuning();
            PlayerSiege.BeginPresenceRaid(n, t, T0);
            PlayerSiege.Maintain(n, t, T0.AddMinutes(5), false);

            Assert.True(PlayerSiege.BeginPresenceRaid(n, t, T0.AddMinutes(11)));

            Assert.False(PlayerSiege.ConquerOfferDue(n, t, T0.AddMinutes(20)));
            Assert.True(PlayerSiege.ConquerOfferDue(n, t, T0.AddMinutes(21)));
        }

        // ── what it refuses ─────────────────────────────────────────────────────

        [Fact]
        public void AFoundedNode_IsRefused_BecauseBreakingItsZoneIsWhatPhaseOneIsFor()
        {
            SpatialNode n = Founded();
            FactionTuning t = Tuning();

            Assert.False(PlayerSiege.BeginPresenceRaid(n, t, T0));

            Assert.Equal(default(DateTime), n.PlayerRaidSucceededUtc);
            Assert.Equal(default(DateTime), n.ZoneBreachedUtc);
            Assert.False(PlayerSiege.IsPresenceRaid(n));
        }

        [Fact]
        public void ASecondBeginAgainstARunningRaid_DoesNotRestartTheClock()
        {
            SpatialNode n = Unfounded();
            FactionTuning t = Tuning();
            PlayerSiege.BeginPresenceRaid(n, t, T0);

            Assert.False(PlayerSiege.BeginPresenceRaid(n, t, T0.AddMinutes(9)));

            Assert.Equal(T0, n.PlayerRaidSucceededUtc);
            Assert.True(PlayerSiege.ConquerOfferDue(n, t, T0.AddMinutes(10)));
        }

        [Fact]
        public void AZeroRadius_SwitchesTheMechanicOff()
        {
            SpatialNode n = Unfounded();
            FactionTuning t = Tuning();
            t.PresenceRaidRadiusKm = 0;

            Assert.False(PlayerSiege.BeginPresenceRaid(n, t, T0));
        }

        // ── through the world, where the defender is told ───────────────────────

        private static SpatialNode AddOwned(WorldModel w, string id, string owner)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 1.0;
            return n;
        }

        [Fact]
        public void TheWorldStartsIt_AndOpensTheDefendersWindowWithNoDamageToCarryIt()
        {
            WorldModel w = TestSupport.World();
            w.Tuning.PresenceRaidRadiusKm = 3.0;
            SpatialNode n = AddOwned(w, "N001", "SPRT");
            Assert.False(w.IsUnderPlayerAttack(n));

            Assert.True(w.BeginPresenceRaid("N001"));

            Assert.True(w.IsUnderPlayerAttack(n));      // FleetManager's Defend branch can see it
            Assert.True(w.AnyPlayerAttackActive());     // and the sweep that gates it opens
            Assert.True(n.Threat > 0);                  // one bump, from the raid resolution
            Assert.Equal("SPRT", n.OwnerFactionTag);    // presence starts a raid, it does not transfer
        }

        /// <summary>The stamp has to keep being renewed or the defender's window closes under an
        /// attacker who never left: `PlayerAggressionResponseWindowSeconds` is 600 s and the hold is
        /// ten minutes, so a single stamp at the start expires exactly as the offer comes due. It
        /// renews on every tick, and the ticks inside one `RaidZoneSiegeDwellSeconds` interval raise
        /// no threat — only the interval boundary charges a fresh strip.</summary>
        [Fact]
        public void HoldingIt_RenewsTheWindow_WithoutRatchetingThreatWithinAnInterval()
        {
            WorldModel w = TestSupport.World();
            SpatialNode held = AddOwned(w, "N001", "SPRT");
            SpatialNode left = AddOwned(w, "N002", "SPRT");   // same start stamp, never renewed
            w.BeginPresenceRaid("N001");
            w.BeginPresenceRaid("N002");
            double threatAtStart = held.Threat;

            w.UtcNow = w.UtcNow.AddMinutes(1);
            Assert.True(w.MarkPlayerPresenceAttack("N001"));
            w.UtcNow = w.UtcNow.AddMinutes(1);
            Assert.True(w.MarkPlayerPresenceAttack("N001"));
            Assert.Equal(threatAtStart, held.Threat);     // both ticks sit inside the first interval

            w.UtcNow = w.UtcNow.AddMinutes(9);            // 11 min on from both start stamps
            Assert.True(w.IsUnderPlayerAttack(held));     // renewed at +2 min, so 9 min of 10 used
            Assert.False(w.IsUnderPlayerAttack(left));    // unrenewed, and the 600 s window has closed
        }

        [Fact]
        public void AnUnownedOrDerelictNode_IsNotRaidable_BecauseClaimingIsWhatThoseWant()
        {
            WorldModel w = TestSupport.World();
            w.Graph.Add(new Vec3(0, 0, 0), 0.5, "N001");                          // unowned
            SpatialNode derelict = AddOwned(w, "N002", NeutralFaction.Tag);       // UNKN

            Assert.False(w.BeginPresenceRaid("N001"));
            Assert.False(w.BeginPresenceRaid("N002"));
            Assert.False(PlayerSiege.IsPresenceRaid(derelict));
        }

        [Fact]
        public void AFoundedNode_IsRefusedThroughTheWorldToo()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = AddOwned(w, "N001", "SPRT");
            n.StableStationId = 77;

            Assert.False(w.BeginPresenceRaid("N001"));
            Assert.False(w.IsUnderPlayerAttack(n));
        }
    }
}
