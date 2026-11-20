using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;
using static Factions.Core.Tests.ArmedYard;

namespace Factions.Core.Tests
{
    /// <summary>FAC-353: a raid that cannot survive what is already standing at its target is not
    /// sent. The mirror of the gate `MarshalDefence` has had since FAC-256 — *"a defence that cannot
    /// hold is not sent at all"* — which existed at four points on the defensive path and nowhere on
    /// the offensive one.
    ///
    /// <para>Live 2026-09-18: the target's defence was computed to SIZE the squadron and then
    /// discarded. 97 combat dispatches, 82 intercepts, 0 raids landed; then 31 and 22 more after
    /// cheaper hulls raised supply into the same ungated pipe.</para></summary>
    public class RaidCommitGateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc);

        /// <summary>A hopeless raid is refused, its hulls go back to the pool it drew them from, and
        /// the line names both numbers — a faction that has gone quiet must not be
        /// indistinguishable from one with no targets.</summary>
        [Fact]
        public void ASquadronThatCannotSurvive_IsNotSent()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 60000);
            origin.ShipStock["Fighter"] = 1;                 // 1000 Dps against 60,000

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Empty(st.PendingFleets);
            Assert.Contains(log, l => l.Contains("RAID_REFUSED") && l.Contains("defenceDps="));
            Assert.Equal(1, origin.ShipStock["Fighter"]);    // handed back, not stranded
        }

        /// <summary>A refused fleet is never announced. The line is written inside
        /// <c>OffensiveDispatch.Dispatch</c>, so a gate that ran after it left a <c>FLEET_DISPATCH</c>
        /// for a fleet that never flew — 793 of 926 lines in one 7.3h live window, which makes every
        /// sortie count read off the log wrong.</summary>
        [Fact]
        public void ARefusedFleet_WritesNoDispatchLine()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 60000);
            origin.ShipStock["Fighter"] = 1;

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("RAID_REFUSED"));
            Assert.DoesNotContain(log, l => l.Contains("FLEET_DISPATCH"));
        }

        /// <summary>A refused fleet is not filed as a dispatched event either — the dashboard's
        /// fleet feed is fed from the same point as the log line.</summary>
        [Fact]
        public void ARefusedFleet_RecordsNoDispatchEvent()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 60000);
            origin.ShipStock["Fighter"] = 1;

            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            Assert.DoesNotContain(world.PendingEvents,
                e => e.Kind == FactionEventKind.FleetDispatched && e.ActorTag == "A");
        }

        /// <summary>...and a squadron that CAN survive still goes. Without this arm the test above
        /// passes on a gate that refuses everything, which is the failure mode of a commit rule.</summary>
        [Fact]
        public void ASquadronThatCanSurvive_IsStillSent()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 1000);
            origin.ShipStock["Fighter"] = 30;                // 30,000 Dps against 1,000

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.NotEmpty(st.PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("RAID_REFUSED"));
        }

        /// <summary>The knob turns it off, so the previous behaviour is one tuning edit away without
        /// a deploy — which is what a rule that can make a faction look passive needs.</summary>
        [Fact]
        public void AtZeroRatio_TheGateIsOff()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 60000);
            world.Tuning.RaidCommitRatio = 0;
            origin.ShipStock["Fighter"] = 1;

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.NotEmpty(st.PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("RAID_REFUSED"));
        }

        /// <summary>One faction with a standing conquest campaign against one defended node.</summary>
        // ---- FAC-360: the attacker's half of the same defect -------------------------------------

        /// <summary>A defence whose guns reach only the last turn cannot deal enough over a duel to
        /// destroy the raid, however the DPS totals read. 2,000 of station at 100 m against 1,000 of
        /// raid refuses on totals (1000 >= 2000 x 0.6 is false) and commits on brackets: silent for
        /// the 2000 m and 800 m turns, it deals 2,000 x 0.35 = 700 against the 800 that would destroy
        /// the attacker.</summary>
        [Fact]
        public void ARaidTheDuelCannotDestroy_IsSentDespiteTheTotals()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 2000, defenceRangeM: 100);
            origin.ShipStock["Fighter"] = 1;

            Assert.False(CombatStrength.Holds(1000, 2000, world.Tuning.RaidCommitRatio));
            Assert.Equal(700, CombatStrength.DuelDamage(new List<WeaponBand>(), target, world.Tuning), 3);

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Single(st.PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("RAID_REFUSED"));
        }

        /// <summary>The negative control, and the reason the widening is not "always attack": a
        /// defence with the REACH to fire in every turn, and enough of it to destroy the raid before
        /// it can break off (2,400 x 0.35 = 840 in the first turn against the 800 that destroys it),
        /// still refuses.</summary>
        [Fact]
        public void TheSameDefenceWithReach_StillRefusesTheRaid()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 2400, defenceRangeM: 3000);
            origin.ShipStock["Fighter"] = 1;

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("RAID_REFUSED") && l.Contains("duelDmg="));
            Assert.Empty(st.PendingFleets);
        }

        // ---- FAC-375: the census, and which way the margin points --------------------------------

        /// <summary>The two margins scale the same bar in opposite directions, and the attacker needs
        /// the reciprocal one. A 1,700 defence reaching two of three turns deals 1,190 on the mean
        /// roll against a raid whose effective HP is 1,000 — it kills it. Read with the margin that
        /// sizes a squadron to destroy <em>even rolling low</em>, the same numbers say the raid
        /// lives.</summary>
        [Fact]
        public void TheSurvivalMarginIsTheReciprocalOfTheOverwhelmMargin()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 1700);
            var none = new List<WeaponBand>();

            Assert.Equal(1190, CombatStrength.DuelDamage(none, target, world.Tuning), 3);
            Assert.Equal(0.8, world.Tuning.RaidSurvivalMargin, 3);

            Assert.False(CombatStrength.Survives(none, target, world.Tuning, 1000, world.Tuning.RaidSurvivalMargin));
            Assert.True(CombatStrength.Survives(none, target, world.Tuning, 1000, world.Tuning.RaidOverwhelmMargin));
        }

        /// <summary>A raid the defence destroys before it can leave is refused. 2,400 of station at
        /// 800 m reaches the raid wherever its own 800 m guns can fire, and deals 2,400 x 0.35 = 840
        /// in the first turn against the 800 that destroys the squadron — it dies before it can
        /// break off, so it does not fly.</summary>
        [Fact]
        public void ARaidTheDefenceDestroysBeforeItCanLeave_IsRefused()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 2400);
            origin.ShipStock["Fighter"] = 1;

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Empty(st.PendingFleets);
            Assert.Contains(log, l => l.Contains("RAID_REFUSED") && l.Contains("surviveDmg=800"));
        }

        /// <summary>A Guard standing at the target is counted by the attacker's census. Dispatch takes
        /// its hulls out of <see cref="SpatialNode.ShipStock"/>, so a garrison that has sailed is
        /// absent from the parked census while being exactly what the raid meets — the station alone
        /// deals 700 a turn at 800 m, short of the 800 that destroys this squadron before it can
        /// leave, and the Guard's 1,000 carries it to 1,050.</summary>
        [Fact]
        public void AGuardStandingAtTheTarget_IsCountedByTheAttackersCensus()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 2000, defenceRangeM: 800);
            origin.ShipStock["Fighter"] = 1;
            FactionState defender = GarrisonedBy("B", target, dps: 1000, rangeM: 800, intercepted: false);

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { st, defender }, world, world.Tuning, log);

            Assert.Empty(st.PendingFleets);
            Assert.Contains(log, l => l.Contains("RAID_REFUSED") && l.Contains("defenceDps=3000"));
        }

        /// <summary>The control the test above needs: an intercepted Guard is a wreck, not a garrison.
        /// Same world, same hulls, the one flag flipped — and the raid flies. Without this arm the
        /// test above passes on a census that counts any pending fleet at all.</summary>
        [Fact]
        public void AnInterceptedGuard_IsNotCounted()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 2000, defenceRangeM: 800);
            origin.ShipStock["Fighter"] = 1;
            FactionState defender = GarrisonedBy("B", target, dps: 1000, rangeM: 800, intercepted: true);

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { st, defender }, world, world.Tuning, log);

            Assert.Single(st.PendingFleets);
            Assert.DoesNotContain(log, l => l.Contains("RAID_REFUSED"));
        }

        /// <summary>The Guard's reach is carried into the census, not just its DPS total. The same
        /// 1,000 of Guard that refuses the raid at 100 m is silent for two of the three turns when it
        /// sits at 100 m and the bar is read at 2000 m — so a census that summed DPS and dropped
        /// RangeM would refuse this one too.</summary>
        [Fact]
        public void TheGuardsReachSurvivesTheCensus()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 200, defenceRangeM: 100);
            origin.ShipStock["Fighter"] = 1;
            FactionState defender = GarrisonedBy("B", target, dps: 1000, rangeM: 100, intercepted: false);

            // 1,200 of defence against 1,000 of raid: the totals are damning, the brackets are not.
            // Only the point-blank turn fires, for 1200 x 0.35 = 420 against the 800 that would kill it.
            new FleetManager().Tick(new List<FactionState> { st, defender }, world, world.Tuning, new List<string>());
            Assert.Single(st.PendingFleets);
        }

        /// <summary>One faction holding <paramref name="node"/> with a Guard committed to it — what
        /// <c>FleetManager</c>'s cross-faction guarded-strength index reads.</summary>
        private static FactionState GarrisonedBy(string tag, SpatialNode node, double dps, double rangeM, bool intercepted)
        {
            var defender = new FactionState { Tag = tag, RngSeed = 2 };
            defender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard,
                ActorTag = tag,
                TargetNodeId = node.Id,
                Intercepted = intercepted,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = (float)dps, RangeM = (float)rangeM } },
            });
            return defender;
        }

        private static WorldModel Campaign(out SpatialNode origin, out SpatialNode target,
                                           out FactionState st, double defenceDps, double defenceRangeM = 800)
        {
            var cat = new ArmedCatalogue().Add("Fighter", 1000, "Small");
            WorldModel world = TestSupport.World();
            world.UtcNow = T0;
            world.PrefabCatalogue = cat;
            world.SetTreasury("A", 10_000_000);

            origin = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "HOME");
            origin.OwnerFactionTag = "A"; origin.HoldStrength = 1.0; origin.StableStationId = 1; origin.Radius = 40;
            target = world.Graph.Add(new Vec3(4000, 0, 0), 0.6, "MARK");
            target.OwnerFactionTag = "B"; target.HoldStrength = 1.0; target.Radius = 40;
            target.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = (float)defenceDps, RangeM = (float)defenceRangeM } };

            st = new FactionState { Tag = "A", RngSeed = 1 };
            var focus = new CampaignFocus
            {
                Id = "raid:MARK", CenterNodeId = "HOME",
                SetAtUtc = T0, ExpiresAtUtc = T0.AddHours(2),
            };
            focus.RegionMembers.Add("HOME");
            var objective = new CampaignObjective
            {
                Kind = CampaignObjectiveKind.Terrorise,
                Cause = CampaignCause.Grudge,
                TargetNodeId = "MARK",
            };
            objective.Candidates.Add("MARK");
            focus.Objectives.Add(objective);
            st.CampaignFoci.Add(focus);
            return world;
        }
    }
}
