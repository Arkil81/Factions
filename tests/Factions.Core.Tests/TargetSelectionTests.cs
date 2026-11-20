using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary><see cref="TargetSelection"/>: whom a fighting hull shoots — the bench-only scope,
    /// the opponent preference, the re-pick margin — and whom a fleet's opponent is.</summary>
    public class TargetSelectionTests
    {
        private static TargetCandidate C(long id, double m, bool opponent = false)
        {
            return new TargetCandidate { EntityId = id, DistanceM = m, Opponent = opponent };
        }

        private static long Pick(TargetScope scope, long current, double currentM, bool currentOpp, params TargetCandidate[] c)
        {
            return TargetSelection.Pick(scope, current, currentM, currentOpp, default(FireRead), c, c.Length);
        }

        private static long PickShotBy(long attacker, TargetScope scope, long current, double currentM, bool currentOpp,
                                       params TargetCandidate[] c)
        {
            var fire = new FireRead { AttackerId = attacker, AttackerDamage = 100, CurrentDamage = attacker == current ? 100 : 0 };
            return TargetSelection.Pick(scope, current, currentM, currentOpp, fire, c, c.Length);
        }

        private static long PickUnderFire(FireRead fire, TargetScope scope, long current, double currentM, bool currentOpp,
                                          params TargetCandidate[] c)
        {
            return TargetSelection.Pick(scope, current, currentM, currentOpp, fire, c, c.Length);
        }

        // ---- the live F2 thrash: four attackers trading hits -----------------------------------

        [Fact]
        public void HeldTargetShootingBack_IsKept_AgainstAComparableTopAttacker()
        {
            // Held 200 has dealt 100; 300 is on top at 150 — under 2x, so no switch.
            var fire = new FireRead { AttackerId = 300, AttackerDamage = 150, CurrentDamage = 100 };
            Assert.Equal(200, PickUnderFire(fire, TargetScope.Any, 200, 800, false, C(200, 800), C(300, 300)));
        }

        [Fact]
        public void HeldTargetShootingBack_GivesWay_ToAClearlyHeavierAttacker()
        {
            var fire = new FireRead { AttackerId = 300, AttackerDamage = 201, CurrentDamage = 100 };
            Assert.Equal(300, PickUnderFire(fire, TargetScope.Any, 200, 800, false, C(200, 800), C(300, 300)));
        }

        [Fact]
        public void HeldTargetShootingBack_IsKept_OverANearerHostileByTheDistanceMargin()
        {
            // No attacker other than the held one: its damage keeps it against a much nearer grid.
            var fire = new FireRead { AttackerId = 200, AttackerDamage = 100, CurrentDamage = 100 };
            Assert.Equal(200, PickUnderFire(fire, TargetScope.Any, 200, 1000, false, C(200, 1000), C(300, 100)));
        }

        [Fact]
        public void HeldTargetNotShooting_GivesWayToTheAttacker()
        {
            var fire = new FireRead { AttackerId = 300, AttackerDamage = 10, CurrentDamage = 0 };
            Assert.Equal(300, PickUnderFire(fire, TargetScope.PreferOpponents, 200, 500, true, C(200, 500, true), C(300, 2500)));
        }

        [Fact]
        public void HeavierAttacker_OutsideTheScope_DoesNotDisplaceAHeldTarget()
        {
            var fire = new FireRead { AttackerId = 300, AttackerDamage = 1000, CurrentDamage = 100 };
            Assert.Equal(200, PickUnderFire(fire, TargetScope.OpponentsOnly, 200, 800, true, C(200, 800, true), C(300, 300)));
        }

        [Fact]
        public void Thrash_FourAttackersTakingTurns_SettleOnOneTarget()
        {
            // Four attackers hitting one hull in turn, one hit a second, equal damage: the pick made
            // on the first read holds for the whole minute.
            var fire = new IncomingFire();
            long[] hull = { 11 };
            long[] attackers = { 135, 963, 108, 777 };
            var cands = new[] { C(135, 700), C(963, 600), C(108, 650), C(777, 620) };
            long target = 0;
            int changes = 0;
            for (int t = 0; t < 60; t++)
            {
                System.DateTime now = T0.AddSeconds(t);
                fire.Record(11, attackers[t % 4], 50 + (t % 3) * 10, now);
                double top;
                long a = fire.TopAttacker(hull, 1, now, out top);
                var read = new FireRead { AttackerId = a, AttackerDamage = top, CurrentDamage = fire.DamageBy(hull, 1, target, now) };
                double m = 0;
                foreach (TargetCandidate c in cands) if (c.EntityId == target) m = c.DistanceM;
                long next = TargetSelection.Pick(TargetScope.Any, target, m, false, read, cands, cands.Length);
                if (target != 0 && next != target) changes++;
                target = next;
            }
            Assert.Equal(0, changes);
            Assert.NotEqual(0, target);
        }

        // ---- fired on: the attacker first -----------------------------------------------------

        [Fact]
        public void Attacker_IsTakenFirst_OverANearerHeldOpponent()
        {
            // A real fleet holding its opponent at 500 m is shot from 2500 m by a player.
            Assert.Equal(300, PickShotBy(300, TargetScope.PreferOpponents, 200, 500, true,
                C(200, 500, true), C(300, 2500)));
        }

        [Fact]
        public void Attacker_IsKept_WhenAlreadyTheTarget()
        {
            Assert.Equal(300, PickShotBy(300, TargetScope.Any, 300, 2500, false, C(100, 100), C(300, 2500)));
        }

        [Fact]
        public void Attacker_OutsideTheScope_IsIgnoredByABenchFleet()
        {
            // The player shooting a bench fleet is not its scenario opponent: it keeps its opponent.
            Assert.Equal(200, PickShotBy(300, TargetScope.OpponentsOnly, 200, 800, true,
                C(200, 800, true), C(300, 400)));
        }

        [Fact]
        public void Attacker_WithinTheScope_ComesFirstForABenchFleet()
        {
            Assert.Equal(210, PickShotBy(210, TargetScope.OpponentsOnly, 200, 400, true,
                C(200, 400, true), C(210, 1500, true)));
        }

        [Fact]
        public void Attacker_OutOfReach_LeavesTheRuleAsItWas()
        {
            Assert.Equal(200, PickShotBy(999, TargetScope.PreferOpponents, 200, 800, true, C(200, 800, true), C(100, 300)));
        }

        // ---- the live F1 case: a player in reach first, the scenario's other side second ----

        [Fact]
        public void BenchFleet_NeverTakesAPlayer_EvenAlone()
        {
            Assert.Equal(0, Pick(TargetScope.OpponentsOnly, 0, 0, false, C(100, 1819)));
        }

        [Fact]
        public void BenchFleet_TakesItsOpponent_OverANearerPlayer()
        {
            Assert.Equal(200, Pick(TargetScope.OpponentsOnly, 0, 0, false, C(100, 400), C(200, 900, true)));
        }

        [Fact]
        public void BenchFleet_DropsAHeldNonOpponent()
        {
            // A target held from before the scope applied is not eligible and is not kept.
            Assert.Equal(200, Pick(TargetScope.OpponentsOnly, 100, 1819, false, C(100, 1819), C(200, 2000, true)));
            Assert.Equal(0, Pick(TargetScope.OpponentsOnly, 100, 1819, false, C(100, 1819)));
        }

        [Fact]
        public void HostileAny_TakesThePlayerWhenNearest()
        {
            Assert.Equal(100, Pick(TargetScope.Any, 0, 0, false, C(100, 400), C(200, 900, true)));
        }

        // ---- real fleets: opponent first, then the re-pick margin ----------------------------

        [Fact]
        public void RealFleet_SwitchesToItsOpponent_WhateverTheDistance()
        {
            Assert.Equal(200, Pick(TargetScope.PreferOpponents, 100, 500, false, C(100, 500), C(200, 2900, true)));
        }

        [Fact]
        public void RealFleet_KeepsItsOpponent_OverANearerIncidentalHostile()
        {
            Assert.Equal(200, Pick(TargetScope.PreferOpponents, 200, 2000, true, C(100, 100), C(200, 2000, true)));
        }

        [Fact]
        public void RealFleet_FallsBackToAnyHostile_WithNoOpponentInReach()
        {
            Assert.Equal(100, Pick(TargetScope.PreferOpponents, 0, 0, false, C(300, 900), C(100, 400)));
        }

        [Fact]
        public void KeepsTarget_WhenTheBestIsNotNearerByTheMargin()
        {
            // 1000 m held; 750 m is nearer but not below 0.7 x 1000.
            Assert.Equal(100, Pick(TargetScope.Any, 100, 1000, false, C(100, 1000), C(200, 750)));
            Assert.Equal(100, Pick(TargetScope.PreferOpponents, 100, 1000, true, C(100, 1000, true), C(200, 750, true)));
        }

        [Fact]
        public void Switches_WhenTheBestIsNearerByTheMargin()
        {
            Assert.Equal(200, Pick(TargetScope.Any, 100, 1000, false, C(100, 1000), C(200, 690)));
            Assert.Equal(200, Pick(TargetScope.OpponentsOnly, 100, 1819, true, C(100, 1819, true), C(200, 470, true)));
        }

        [Fact]
        public void KeepsATargetOutsideTheCandidateList()
        {
            // Between the acquire and release radii a held target is not a candidate; it is kept
            // against one not nearer by the margin.
            Assert.Equal(100, Pick(TargetScope.Any, 100, 3500, false, C(200, 2900)));
        }

        [Fact]
        public void NoCandidatesAndNoTarget_IsNone()
        {
            Assert.Equal(0, Pick(TargetScope.PreferOpponents, 0, 0, false));
        }

        [Fact]
        public void ReadsOnlyCount()
        {
            var c = new[] { C(100, 900), C(200, 10) };
            Assert.Equal(100, TargetSelection.Pick(TargetScope.Any, 0, 0, false, default(FireRead), c, 1));
        }

        // ---- IncomingFire -----------------------------------------------------------------------

        private static readonly System.DateTime T0 = new System.DateTime(2026, 9, 27, 21, 24, 0, System.DateTimeKind.Utc);

        [Fact]
        public void IncomingFire_RemembersTheLastAttacker_ForTheWindow()
        {
            var fire = new IncomingFire();
            fire.Record(11, 500, 10, T0);
            fire.Record(11, 600, 10, T0.AddSeconds(2));
            long a;
            System.DateTime t;
            Assert.True(fire.TryRecent(11, T0.AddSeconds(10), out a, out t));
            Assert.Equal(600, a);
            Assert.True(fire.HitRecently(11, T0.AddSeconds(2) + IncomingFire.RecentWindow));
            Assert.False(fire.HitRecently(11, T0.AddSeconds(3) + IncomingFire.RecentWindow));
            Assert.False(fire.HitRecently(12, T0));
        }

        [Fact]
        public void IncomingFire_SquadronAttacker_IsTheMostRecentHit()
        {
            var fire = new IncomingFire();
            fire.Record(11, 500, 10, T0);
            fire.Record(12, 700, 10, T0.AddSeconds(5));
            fire.Record(13, 800, 10, T0.AddSeconds(-30));   // stale
            Assert.Equal(700, fire.LatestAttacker(new long[] { 11, 12, 13 }, 3, T0.AddSeconds(6)));
            Assert.Equal(500, fire.LatestAttacker(new long[] { 11, 12, 13 }, 1, T0.AddSeconds(6)));
            Assert.Equal(0, fire.LatestAttacker(new long[] { 13 }, 1, T0.AddSeconds(6)));
        }

        [Fact]
        public void IncomingFire_TopAttacker_IsTheMostDamage_SummedOverTheSquadron()
        {
            var fire = new IncomingFire();
            fire.Record(11, 500, 100, T0);
            fire.Record(11, 600, 150, T0);
            fire.Record(12, 500, 100, T0);               // 500 hit two hulls: 200 in all
            fire.Record(13, 700, 5000, T0.AddSeconds(-30)); // outside the window
            double d;
            Assert.Equal(500, fire.TopAttacker(new long[] { 11, 12, 13 }, 3, T0, out d));
            Assert.Equal(200, d, 6);
            Assert.Equal(600, fire.TopAttacker(new long[] { 11 }, 1, T0, out d));
            Assert.Equal(200, fire.DamageBy(new long[] { 11, 12 }, 2, 500, T0), 6);
            Assert.Equal(0, fire.DamageBy(new long[] { 13 }, 1, 700, T0));
        }

        [Fact]
        public void IncomingFire_DamageDecays()
        {
            var fire = new IncomingFire();
            fire.Record(11, 500, 100, T0);
            double later = fire.DamageBy(new long[] { 11 }, 1, 500, T0.AddSeconds(IncomingFire.DecaySeconds));
            Assert.Equal(100 * System.Math.Exp(-1), later, 6);
            fire.Record(11, 500, 100, T0.AddSeconds(IncomingFire.DecaySeconds));
            Assert.Equal(100 + 100 * System.Math.Exp(-1), fire.DamageBy(new long[] { 11 }, 1, 500, T0.AddSeconds(IncomingFire.DecaySeconds)), 6);
        }

        [Fact]
        public void IncomingFire_AFifthAttacker_TakesTheWeakestSlot()
        {
            var fire = new IncomingFire();
            long[] hull = { 11 };
            fire.Record(11, 1, 400, T0);
            fire.Record(11, 2, 10, T0);
            fire.Record(11, 3, 300, T0);
            fire.Record(11, 4, 200, T0);
            fire.Record(11, 5, 50, T0);
            Assert.Equal(0, fire.DamageBy(hull, 1, 2, T0));
            Assert.Equal(50, fire.DamageBy(hull, 1, 5, T0), 6);
            Assert.Equal(400, fire.DamageBy(hull, 1, 1, T0), 6);
        }

        [Fact]
        public void IncomingFire_IgnoresSelfAndUnattributedHits_AndForgets()
        {
            var fire = new IncomingFire();
            fire.Record(11, 0, 10, T0);
            fire.Record(11, 11, 10, T0);
            Assert.Equal(0, fire.Count);
            fire.Record(11, 500, 10, T0);
            fire.Forget(11);
            Assert.False(fire.HitRecently(11, T0));
        }

        // ---- scope --------------------------------------------------------------------------

        [Fact]
        public void Scope_BenchIsOpponentsOnly_UnlessHostileAny_RealPrefersOpponents()
        {
            var real = new PendingFleetAction { Kind = ActionKind.Raid };
            var bench = new PendingFleetAction { Kind = ActionKind.Raid, BenchLabel = "F1" };
            var any = new PendingFleetAction { Kind = ActionKind.Raid, BenchLabel = "F1", BenchFlags = BenchFlags.HostileAny | BenchFlags.Ledger };
            Assert.Equal(TargetScope.PreferOpponents, TargetSelection.ScopeFor(real));
            Assert.Equal(TargetScope.OpponentsOnly, TargetSelection.ScopeFor(bench));
            Assert.Equal(TargetScope.Any, TargetSelection.ScopeFor(any));
        }

        // ---- the opponent -------------------------------------------------------------------

        private static PendingFleetAction Fleet(ActionKind kind, string actor, string node, string targetTag,
                                                long lead, params long[] escorts)
        {
            return new PendingFleetAction
            {
                Kind = kind, ActorTag = actor, TargetNodeId = node, TargetTag = targetTag,
                FleetId = actor + "." + kind + "." + lead,
                MaterialisedEntityId = lead,
                EscortEntityIds = escorts.Length > 0 ? new List<long>(escorts) : null,
            };
        }

        private static List<FactionState> States(params PendingFleetAction[] fleets)
        {
            var byTag = new Dictionary<string, FactionState>();
            var states = new List<FactionState>();
            foreach (PendingFleetAction f in fleets)
            {
                FactionState st;
                if (!byTag.TryGetValue(f.ActorTag, out st))
                {
                    st = new FactionState { Tag = f.ActorTag, RngSeed = 1 };
                    byTag[f.ActorTag] = st;
                    states.Add(st);
                }
                st.PendingFleets.Add(f);
            }
            return states;
        }

        /// <summary>CollectOpponents for the fleet with <paramref name="fleet"/>'s id.</summary>
        private static string Opp(List<FactionState> states, PendingFleetAction fleet, HashSet<long> grids)
        {
            for (int si = 0; si < states.Count; si++)
                for (int fi = 0; fi < states[si].PendingFleets.Count; fi++)
                    if (states[si].PendingFleets[fi].FleetId == fleet.FleetId)
                        return TargetSelection.CollectOpponents(states, si, fi, grids);
            throw new System.InvalidOperationException("no fleet " + fleet.FleetId);
        }

        [Fact]
        public void BenchFight_OpponentIsTheOtherSidesHulls_AndNoFaction()
        {
            string node = BenchScenario.FightNodeId("F1");
            PendingFleetAction atk = Fleet(ActionKind.Raid, "SPRT", node, "IGSL", 11, 12, 13);
            PendingFleetAction def = Fleet(ActionKind.Guard, "IGSL", node, null, 21, 22, 23);
            PendingFleetAction elsewhere = Fleet(ActionKind.Guard, "IGSL", "n.other", null, 31);
            List<FactionState> states = States(atk, def, elsewhere);

            var grids = new HashSet<long>();
            Assert.Null(Opp(states, atk, grids));
            Assert.Equal(new HashSet<long> { 21, 22, 23 }, grids);

            grids.Clear();
            Assert.Null(Opp(states, def, grids));
            Assert.Equal(new HashSet<long> { 11, 12, 13 }, grids);
        }

        [Fact]
        public void BenchFight_OtherSideStillSpawning_HasNoOpponentGrids()
        {
            string node = BenchScenario.FightNodeId("F1");
            PendingFleetAction atk = Fleet(ActionKind.Raid, "SPRT", node, "IGSL", 11);
            PendingFleetAction def = Fleet(ActionKind.Guard, "IGSL", node, null, PendingFleetAction.SpawningId);
            var grids = new HashSet<long>();
            Opp(States(atk, def), atk, grids);
            Assert.Empty(grids);
        }

        [Fact]
        public void RealRaid_OpponentIsTheNodesDefendersAndItsOwner()
        {
            PendingFleetAction raid = Fleet(ActionKind.Raid, "SPRT", "n.1", "IGSL", 11);
            PendingFleetAction guard = Fleet(ActionKind.Guard, "IGSL", "n.1", null, 21);
            PendingFleetAction reinforce = Fleet(ActionKind.Reinforce, "IGSL", "n.1", null, 22);
            PendingFleetAction otherRaid = Fleet(ActionKind.Raid, "UNKN", "n.1", "IGSL", 31);
            PendingFleetAction trade = Fleet(ActionKind.Trade, "IGSL", "n.1", null, 41);
            List<FactionState> states = States(raid, guard, reinforce, otherRaid, trade);

            var grids = new HashSet<long>();
            Assert.Equal("IGSL", Opp(states, raid, grids));
            Assert.Equal(new HashSet<long> { 21, 22 }, grids);

            grids.Clear();
            Assert.Null(Opp(states, guard, grids));
            Assert.Equal(new HashSet<long> { 11, 31 }, grids);
        }

        [Fact]
        public void AFleetThatNeitherAttacksNorDefends_HasNoOpponent()
        {
            PendingFleetAction trade = Fleet(ActionKind.Trade, "IGSL", "n.1", "SPRT", 41);
            PendingFleetAction raid = Fleet(ActionKind.Raid, "SPRT", "n.1", "IGSL", 11);
            var grids = new HashSet<long>();
            Assert.Null(Opp(States(trade, raid), trade, grids));
            Assert.Empty(grids);
        }
    }
}
