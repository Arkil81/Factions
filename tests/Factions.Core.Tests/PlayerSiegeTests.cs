using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The player siege state machine: absorbed fire against a station's safe zone, the breach, the
    /// raid that follows, and the abandonment path. Core owns every clock here, so all of it is
    /// testable without a live game — which is the point of the host holding only the SE entity.
    /// </summary>
    public class PlayerSiegeTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

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
            };
        }

        // Founded, because a siege only ever runs against a station: every entry point in
        // PlayerSiegeManager resolves its target through a materialised grid or safe zone, and a node
        // with no StableStationId is the presence-raid case instead (see PresenceRaidTests).
        private static SpatialNode Node()
        {
            return new SpatialNode
            {
                Id = "N1", OwnerFactionTag = "DEF", ZoneIntegrity = -1, StableStationId = 77,
            };
        }

        [Fact]
        public void UntouchedNode_ReadsFullIntegrity_AndItsZoneIsUp()
        {
            SpatialNode n = Node();
            Assert.Equal(1000.0, PlayerSiege.IntegrityOf(n, Tuning(), T0), 6);
            Assert.Equal(1.0, PlayerSiege.IntegrityFraction(n, Tuning(), T0), 6);
            Assert.True(PlayerSiege.ZoneShouldBeEnabled(n, T0));
        }

        /// <summary>The owner's own fire on their own shield is not an attack. Live: EXP firing on its
        /// newly taken N096 logged `SIEGE_BEGAN node=N096 owner=EXP` (2026-10-12 00:00:55).</summary>
        [Fact]
        public void TheOwnersFireOnTheirOwnShield_StartsNoSiege_AndLeavesThePoolFull()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();

            PlayerSiege.SiegeStep step = PlayerSiege.AbsorbZoneDamage(n, t, T0, 400, "DEF");

            Assert.False(step.SiegeStarted);
            Assert.False(step.Breached);
            Assert.Equal(1000.0, PlayerSiege.IntegrityOf(n, t, T0), 6);
            Assert.False(PlayerSiege.HasStationSiege(n));
        }

        /// <summary>The negative control: the same fire from another faction starts the siege.</summary>
        [Fact]
        public void AnotherFactionsFireOnTheShield_StartsTheSiege()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();

            PlayerSiege.SiegeStep step = PlayerSiege.AbsorbZoneDamage(n, t, T0, 400, "EXP");

            Assert.True(step.SiegeStarted);
            Assert.Equal(600.0, PlayerSiege.IntegrityOf(n, t, T0), 6);
        }

        [Fact]
        public void AbsorbedFire_DepletesThePool_AndRegenRefillsItLinearly()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();

            PlayerSiege.AbsorbZoneDamage(n, t, T0, 400);
            Assert.Equal(600.0, PlayerSiege.IntegrityOf(n, t, T0), 6);

            // 15 s of a 30 s empty-to-full refill returns half the pool.
            Assert.Equal(1000.0, PlayerSiege.IntegrityOf(n, t, T0.AddSeconds(15)), 6);
            Assert.Equal(750.0, PlayerSiege.IntegrityOf(n, t, T0.AddSeconds(4.5)), 6);
        }

        // The two halves of the breach condition, each shown to be load-bearing on its own.
        [Fact]
        public void EmptyPoolAlone_DoesNotBreach_BeforeTheDwellElapses()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            PlayerSiege.SiegeStep step = PlayerSiege.AbsorbZoneDamage(n, t, T0, 5000);  // over-kill in one burst

            Assert.Equal(0.0, step.Integrity, 6);
            Assert.False(step.Breached);
            Assert.True(PlayerSiege.ZoneShouldBeEnabled(n, T0));
        }

        [Fact]
        public void ElapsedDwellAlone_DoesNotBreach_WhileThePoolHoldsAnything()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            t.RaidZoneIntegrityRegenSeconds = 100000;   // effectively no regen, so the pool just sits
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 999);            // 1 point left
            PlayerSiege.SiegeStep step = PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(400), 0.5);

            Assert.True(step.Integrity > 0);
            Assert.False(step.Breached);
            Assert.True(PlayerSiege.ZoneShouldBeEnabled(n, T0.AddSeconds(400)));
        }

        [Fact]
        public void EmptyPoolAfterTheDwell_BreachesAndDropsTheZone()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            t.RaidZoneIntegrityRegenSeconds = 100000;
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 900);
            PlayerSiege.SiegeStep step = PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(301), 200);

            Assert.True(step.Breached);
            Assert.False(PlayerSiege.ZoneShouldBeEnabled(n, T0.AddSeconds(301)));
        }

        // Phase 2 is a fight with the station, not the shield: the attacker must not have to keep
        // suppressing a zone that is already down.
        [Fact]
        public void ABreachedZonesPoolIsFrozen_AndDoesNotRegenerate()
        {
            FactionTuning t = Tuning();
            SpatialNode n = SustainedSiegeToBreach(t);

            Assert.Equal(0.0, PlayerSiege.IntegrityOf(n, t, T0.AddSeconds(3000)), 6);
            Assert.False(PlayerSiege.ZoneShouldBeEnabled(n, T0.AddSeconds(3000)));
        }

        // The real shape of a siege, at the shipped regeneration rate rather than a suppressed one:
        // a 1000-point pool refilling in 30 s bleeds ~33/s, so an attacker has to out-damage the
        // regen for the whole 300 s dwell. This is the path a player actually takes, and every
        // downstream phase-2 test is built on it rather than on an artificially frozen pool.
        [Fact]
        public void SustainedFire_BreachesOnlyAfterTheFullDwell_AtTheShippedRegenRate()
        {
            FactionTuning t = Tuning();
            SpatialNode n = Node();
            double breachedAtSec = -1;

            // 200 damage every 5 s = 40/s against ~33/s of regen: it drains and stays drained.
            // The fire has to be genuinely continuous -- an 11 s gap regenerates more than one
            // burst removes, which is the whole pressure this mechanic applies.
            for (double sec = 0; sec <= 320; sec += 5)
            {
                PlayerSiege.SiegeStep s = PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(sec), 200);
                if (s.Breached && breachedAtSec < 0) breachedAtSec = sec;
            }

            Assert.True(breachedAtSec >= 300, "breached at " + breachedAtSec + "s, before the dwell elapsed");
            Assert.True(breachedAtSec <= 305, "breach was late at " + breachedAtSec + "s");
            Assert.False(PlayerSiege.ZoneShouldBeEnabled(n, T0.AddSeconds(320)));
        }

        // Letting up lets the pool climb back, which is the pressure the mechanic is for.
        [Fact]
        public void FiringTooSlowly_NeverEmptiesThePool_SoTheDwellNeverMatters()
        {
            FactionTuning t = Tuning();
            SpatialNode n = Node();

            // 100 damage every 10 s = 10/s against ~33/s of regen.
            for (double sec = 0; sec < 400; sec += 10)
            {
                PlayerSiege.SiegeStep s = PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(sec), 100);
                Assert.False(s.Breached);
            }
            Assert.True(PlayerSiege.ZoneShouldBeEnabled(n, T0.AddSeconds(400)));
        }

        [Fact]
        public void AnAbandonedSiege_LapsesAndResetsTheDwellToZero()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 500);
            DateTime started = n.SiegeStartedUtc;
            Assert.NotEqual(default(DateTime), started);

            // Pool refills at +30 s; the lapse clock starts then and expires 30 s later.
            Assert.False(PlayerSiege.Maintain(n, t, T0.AddSeconds(31), false));  // starts the clock
            Assert.True(PlayerSiege.Maintain(n, t, T0.AddSeconds(62), false));   // expires it

            Assert.Equal(default(DateTime), n.SiegeStartedUtc);   // dwell reset, not paused
            Assert.Equal(1000.0, PlayerSiege.IntegrityOf(n, t, T0.AddSeconds(62)), 6);
        }

        [Fact]
        public void FreshDamage_ClearsTheLapseClock()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            PlayerSiege.AbsorbZoneDamage(n, t, T0, 500);
            PlayerSiege.Maintain(n, t, T0.AddSeconds(31), false);          // lapse clock running
            Assert.NotEqual(default(DateTime), n.SiegeLapseSinceUtc);

            PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(40), 100);
            Assert.Equal(default(DateTime), n.SiegeLapseSinceUtc);
            Assert.False(PlayerSiege.Maintain(n, t, T0.AddSeconds(80), false));
        }

        // Presence sustains phase 2; walking away gives the station its shield back.
        [Fact]
        public void ABreach_IsHeldOpenByPresence_AndReleasedWhenTheAttackerLeaves()
        {
            FactionTuning t = Tuning();
            SpatialNode n = SustainedSiegeToBreach(t);

            PlayerSiege.Maintain(n, t, T0.AddSeconds(400), true);
            Assert.False(PlayerSiege.ZoneShouldBeEnabled(n, T0.AddSeconds(400)));

            PlayerSiege.Maintain(n, t, T0.AddSeconds(500), false);
            Assert.Equal(default(DateTime), n.ZoneBreachedUtc);
        }

        // The discriminator against a presence raid, asserted on the side that must NOT reset. Both
        // paths clear ZoneBreachedUtc when the attacker leaves, so that field alone cannot tell them
        // apart -- what separates them is whether the rest of the raid survives.
        [Fact]
        public void AFoundedBreach_ReleasedByAbsence_KeepsTheSiegeItAlreadyEarned()
        {
            FactionTuning t = Tuning();
            SpatialNode n = SustainedSiegeToBreach(t);
            PlayerSiege.RecordRaidDamage(n, t, T0.AddSeconds(400), 5000, false);

            PlayerSiege.Maintain(n, t, T0.AddSeconds(500), false);

            Assert.Equal(default(DateTime), n.ZoneBreachedUtc);       // shield back up
            Assert.NotEqual(default(DateTime), n.SiegeStartedUtc);    // but the siege is not undone
            Assert.Equal(5000.0, n.PlayerRaidBlockDamage);
        }

        [Fact]
        public void RaidSucceeds_OnCumulativeBlockDamage()
        {
            SpatialNode n = Breached(out FactionTuning t);
            Assert.False(PlayerSiege.RecordRaidDamage(n, t, T0, 19999, false));
            Assert.True(PlayerSiege.RecordRaidDamage(n, t, T0, 1, false));
            Assert.NotEqual(default(DateTime), n.PlayerRaidSucceededUtc);
        }

        // The other route, and it must not need the damage threshold at all -- killing the reactors
        // is a different way to take a station and should read as one.
        [Fact]
        public void RaidSucceeds_OnPoweredDown_WithNegligibleDamage()
        {
            SpatialNode n = Breached(out FactionTuning t);
            Assert.True(PlayerSiege.RecordRaidDamage(n, t, T0, 1, true));
        }

        [Fact]
        public void RaidDamage_IsIgnored_WhenTheZoneIsStillUp()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            Assert.False(PlayerSiege.RecordRaidDamage(n, t, T0, 999999, true));
            Assert.Equal(default(DateTime), n.PlayerRaidSucceededUtc);
        }

        [Fact]
        public void ConquerOffer_IsDueOnlyAfterTheHoldingPeriod()
        {
            SpatialNode n = Breached(out FactionTuning t);
            PlayerSiege.RecordRaidDamage(n, t, T0, 20000, false);

            Assert.False(PlayerSiege.ConquerOfferDue(n, t, T0.AddMinutes(9)));
            Assert.True(PlayerSiege.ConquerOfferDue(n, t, T0.AddMinutes(10)));
        }

        // One predicate answers "should this zone be on", folding both reasons it can be off, so
        // neither can silently re-enable the other's.
        [Fact]
        public void ZoneEnablement_FoldsBothThePlayerBreachAndTheNpcRaidKnockdown()
        {
            SpatialNode n = Node();
            n.SafeZoneDownUntilUtc = T0.AddMinutes(30);              // an NPC raid landed
            Assert.False(PlayerSiege.ZoneShouldBeEnabled(n, T0));
            Assert.True(PlayerSiege.ZoneShouldBeEnabled(n, T0.AddMinutes(31)));

            n.ZoneBreachedUtc = T0;                                   // and a player is inside it
            Assert.False(PlayerSiege.ZoneShouldBeEnabled(n, T0.AddMinutes(31)));
        }

        [Fact]
        public void PoolOfZero_DisablesTheMechanicEntirely()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            t.RaidZoneIntegrityPool = 0;

            PlayerSiege.SiegeStep step = PlayerSiege.AbsorbZoneDamage(n, t, T0, 100000);
            Assert.False(step.Breached);
            Assert.True(PlayerSiege.ZoneShouldBeEnabled(n, T0));
        }

        private static SpatialNode Breached(out FactionTuning t)
        {
            t = Tuning();
            return SustainedSiegeToBreach(t);
        }

        /// <summary>Drives a node to a breach the way a player does — sustained fire that
        /// out-damages regeneration across the whole dwell — rather than by suppressing the
        /// regeneration rate, so the phase-2 tests rest on a reachable state.</summary>
        private static SpatialNode SustainedSiegeToBreach(FactionTuning t)
        {
            SpatialNode n = Node();
            for (double sec = 0; sec <= 305; sec += 5)
                PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(sec), 200);
            Assert.NotEqual(default(DateTime), n.ZoneBreachedUtc);
            return n;
        }
        // ---- What every surface draws the station by ---------------------------------------------

        /// <summary>The states are read most urgent first, and this is the ordering that matters:
        /// the three stamps overlap, because a won raid still carries the breach that let it happen
        /// and the siege that opened it. Read in any other order, a node about to change hands
        /// reports as one waiting for its shield back.</summary>
        [Fact]
        public void StateOf_ReadsTheMostUrgentStampWhenSeveralAreSet()
        {
            SpatialNode n = Node();
            Assert.Equal(PlayerSiege.ZoneState.Protected, PlayerSiege.StateOf(n, T0));

            n.SiegeStartedUtc = T0;
            Assert.Equal(PlayerSiege.ZoneState.Besieged, PlayerSiege.StateOf(n, T0));

            n.ZoneBreachedUtc = T0.AddSeconds(300);
            Assert.Equal(PlayerSiege.ZoneState.Breached, PlayerSiege.StateOf(n, T0));

            n.PlayerRaidSucceededUtc = T0.AddSeconds(600);
            Assert.Equal(PlayerSiege.ZoneState.Raided, PlayerSiege.StateOf(n, T0));
        }

        /// <summary>The NPC half. A resolved raid knocks the zone out for a window with no siege
        /// behind it, so a reading built only from the siege stamps calls this station protected
        /// through exactly the period it can be walked into.</summary>
        [Fact]
        public void StateOf_ReportsAZoneKnockedOutByALandedRaid_WithNoSiegeRunning()
        {
            SpatialNode n = Node();
            n.SafeZoneDownUntilUtc = T0.AddMinutes(30);

            Assert.Equal(PlayerSiege.ZoneState.Down, PlayerSiege.StateOf(n, T0));
            Assert.False(PlayerSiege.ZoneShouldBeEnabled(n, T0));
            // And it comes back on its own, which is what makes the window a window.
            Assert.Equal(PlayerSiege.ZoneState.Protected, PlayerSiege.StateOf(n, T0.AddMinutes(31)));
        }

        /// <summary>A bare claim has no station, so it has no protection to report. Without this a
        /// node that has never been founded would draw an alarm ring on a map for a shield it does
        /// not have.</summary>
        [Fact]
        public void StateOf_OnAnUnfoundedClaim_IsNoStation()
        {
            SpatialNode n = Node();
            n.StableStationId = 0;
            n.SafeZoneDownUntilUtc = T0.AddMinutes(30);

            Assert.Equal(PlayerSiege.ZoneState.NoStation, PlayerSiege.StateOf(n, T0));
        }

        /// <summary>The breach countdown is the dwell, and it is only half the condition: the pool
        /// must be empty at that moment too. It stops existing once the zone is actually down,
        /// because a countdown to something that has happened is a frozen clock.</summary>
        [Fact]
        public void BreachDue_IsTheDwellFromTheSiegeStart_AndStopsAtTheBreach()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();
            Assert.Equal(DateTime.MinValue, PlayerSiege.BreachDueUtc(n, t));

            n.SiegeStartedUtc = T0;
            Assert.Equal(T0.AddSeconds(300), PlayerSiege.BreachDueUtc(n, t));

            n.ZoneBreachedUtc = T0.AddSeconds(310);
            Assert.Equal(DateTime.MinValue, PlayerSiege.BreachDueUtc(n, t));
        }

        /// <summary>The raid bar is clamped: the last flush of damage overshoots the threshold, and
        /// a bar past 100% reads as a broken gauge rather than as a raid already won.</summary>
        [Fact]
        public void RaidProgress_IsTheDamageFractionAndDoesNotPassOne()
        {
            SpatialNode n = Node();
            FactionTuning t = Tuning();

            Assert.Equal(0.0, PlayerSiege.RaidProgressFraction(n, t), 6);
            n.PlayerRaidBlockDamage = 5000.0;
            Assert.Equal(0.25, PlayerSiege.RaidProgressFraction(n, t), 6);
            n.PlayerRaidBlockDamage = 25000.0;
            Assert.Equal(1.0, PlayerSiege.RaidProgressFraction(n, t), 6);
        }

        // ── the unobserved sweep (FAC-433) ──────────────────────────────────

        [Fact]
        public void HasStationSiege_IsTrueForEveryPhase_AndFalseOnceReset()
        {
            FactionTuning t = Tuning();
            SpatialNode n = Node();
            Assert.False(PlayerSiege.HasStationSiege(n));

            PlayerSiege.AbsorbZoneDamage(n, t, T0, 100);
            Assert.True(PlayerSiege.HasStationSiege(n));                  // besieged

            PlayerSiege.AbsorbZoneDamage(n, t, T0.AddSeconds(300), 5000);
            Assert.NotEqual(default(DateTime), n.ZoneBreachedUtc);
            Assert.True(PlayerSiege.HasStationSiege(n));                  // breached

            PlayerSiege.Reset(n);
            Assert.False(PlayerSiege.HasStationSiege(n));
        }

        [Fact]
        public void HasStationSiege_LeavesAPresenceRaid_ToThePresencePath()
        {
            FactionTuning t = Tuning();
            t.PresenceRaidRadiusKm = 3.0;
            var n = new SpatialNode { Id = "N2", OwnerFactionTag = "DEF", ZoneIntegrity = -1 };
            Assert.True(PlayerSiege.BeginPresenceRaid(n, t, T0));
            Assert.True(PlayerSiege.IsPresenceRaid(n));
            Assert.False(PlayerSiege.HasStationSiege(n));
        }

        // The state N600 was persisted in on 2026-09-30, twelve hours after its raid: won, the breach
        // already lifted, the pool stamped empty at the moment the attacker left. The sweep reaches it
        // and, with nobody present, Maintain clears it in two calls one lapse window apart.
        [Fact]
        public void AWonRaidLeftUnobserved_IsSwept_AndLapsesWithNobodyPresent()
        {
            FactionTuning t = Tuning();
            DateTime raided = new DateTime(2026, 9, 29, 20, 29, 52, DateTimeKind.Utc);
            SpatialNode n = Node();
            n.ZoneIntegrity = 0.0;
            n.ZoneIntegrityAtUtc = new DateTime(2026, 9, 29, 21, 19, 45, DateTimeKind.Utc);
            n.SiegeStartedUtc = raided.AddSeconds(-10);
            n.PlayerRaidBlockDamage = 14578.87;
            n.PlayerRaidSucceededUtc = raided;

            DateTime now = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
            Assert.True(PlayerSiege.HasStationSiege(n));
            Assert.Equal(PlayerSiege.ZoneState.Raided, PlayerSiege.StateOf(n, now));

            Assert.False(PlayerSiege.Maintain(n, t, now, attackerPresent: false));   // lapse clock starts
            Assert.True(PlayerSiege.Maintain(n, t, now.AddSeconds(31), attackerPresent: false));

            Assert.False(PlayerSiege.HasStationSiege(n));
            Assert.Equal(PlayerSiege.ZoneState.Protected, PlayerSiege.StateOf(n, now.AddSeconds(31)));
        }
    }
}
