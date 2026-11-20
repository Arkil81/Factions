using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="TacticalRead"/> — which bracket a lead fights at and the range it holds there —
    /// and <see cref="TacticResolver"/>'s ladder.
    /// </summary>
    public class TacticalReadTests
    {
        private static readonly double[] Brackets = { 2000, 800, 0 };

        private static List<WeaponBand> Bands(params (float dps, float rangeM)[] bands)
        {
            var list = new List<WeaponBand>();
            foreach (var b in bands) list.Add(new WeaponBand { Dps = b.dps, RangeM = b.rangeM });
            return list;
        }

        private static TacticOrder Order(TacticArm arm) => new TacticOrder { Arm = arm, BreakOff = BreakOffLevel.Mid };

        // ---- the bracket pick -------------------------------------------------------------

        [Fact]
        public void Outranging_HoldsInsideOwnBandAndOutsideTheirs()
        {
            BracketPick p = TacticalRead.PickStandoff(Bands((100, 2000)), Bands((300, 800)), Brackets);
            Assert.Equal(2000, p.BracketM);
            Assert.Equal(1700, p.HoldRangeM, 6);
            Assert.Equal(800, p.AvoidInsideM, 6);
            Assert.Equal(100, p.OwnDps, 6);
            Assert.Equal(0, p.TheirDps, 6);
        }

        [Fact]
        public void Outranged_TakesTheLongestBracketItCanReach()
        {
            // Both close brackets score 100/300; the tie goes to the longer one.
            BracketPick p = TacticalRead.PickStandoff(Bands((100, 800)), Bands((300, 2000)), Brackets);
            Assert.Equal(800, p.BracketM);
            Assert.Equal(680, p.HoldRangeM, 6);
        }

        [Fact]
        public void MixedLoadout_PrefersTheBracketTheyCannotAnswer()
        {
            BracketPick p = TacticalRead.PickStandoff(Bands((50, 2000), (200, 800)), Bands((100, 800)), Brackets);
            Assert.Equal(2000, p.BracketM);
            Assert.Equal(50, p.OwnDps, 6);
        }

        [Fact]
        public void AmongFreeBrackets_TheOneLandingMoreWins()
        {
            // They reach 500 m only: 2000 and 800 are both free, and 800 carries every gun.
            BracketPick p = TacticalRead.PickStandoff(Bands((50, 2000), (200, 800)), Bands((100, 500)), Brackets);
            Assert.Equal(800, p.BracketM);
            Assert.Equal(250, p.OwnDps, 6);
            Assert.True(p.HoldRangeM > 500 && p.HoldRangeM <= 800);
        }

        [Fact]
        public void AnEnemyBandJustShortOfTheBracket_PushesTheHoldOut()
        {
            BracketPick p = TacticalRead.PickStandoff(Bands((100, 2000)), Bands((300, 1900)), Brackets);
            Assert.Equal(2000, p.BracketM);
            Assert.Equal(1950, p.HoldRangeM, 6);
            Assert.True(p.HoldRangeM - TacticalRead.Tolerance(p.HoldRangeM, p.AvoidInsideM) > 1900);
        }

        [Fact]
        public void UnknownEnemy_RanksByOwnOutput()
        {
            BracketPick p = TacticalRead.PickStandoff(Bands((50, 2000), (200, 800)), null, Brackets);
            Assert.Equal(800, p.BracketM);   // 250 at 800 and at 0; the longer wins
            Assert.False(p.TheirsKnown);
            Assert.Equal(0, p.AvoidInsideM);
        }

        [Fact]
        public void Unarmed_HoldsAtTheLongestBracket()
        {
            BracketPick p = TacticalRead.PickStandoff(Bands(), Bands((100, 800)), Brackets);
            Assert.Equal(2000, p.BracketM);
            Assert.Equal(2000, p.HoldRangeM, 6);
            Assert.Equal(0, p.OwnDps);
        }

        [Fact]
        public void AShortBand_NeverHoldsCloserThanTheFloor()
        {
            BracketPick p = TacticalRead.PickStandoff(Bands((100, 60)), null, Brackets);
            Assert.Equal(0, p.BracketM);
            Assert.Equal(TacticalRead.MinHoldRangeM, p.HoldRangeM, 6);
        }

        [Fact]
        public void NoBrackets_FightsPointBlank()
        {
            BracketPick p = TacticalRead.PickStandoff(Bands((100, 1000)), null, null);
            Assert.Equal(0, p.BracketM);
            Assert.Equal(850, p.HoldRangeM, 6);
        }

        // ---- the plan ---------------------------------------------------------------------

        [Fact]
        public void Plan_EachArmFliesItsManoeuvre()
        {
            var own = Bands((100, 2000), (200, 600));
            var theirs = Bands((300, 800));
            Assert.Equal(ManoeuvreKind.HoldAtRange, TacticalRead.Plan(Order(TacticArm.Standoff), own, theirs, Brackets).Kind);
            Assert.Equal(ManoeuvreKind.Kite, TacticalRead.Plan(Order(TacticArm.Kite), own, theirs, Brackets).Kind);

            CombatPlan brawl = TacticalRead.Plan(Order(TacticArm.Brawl), own, theirs, Brackets);
            Assert.Equal(ManoeuvreKind.Orbit, brawl.Kind);
            Assert.Equal(510, brawl.HoldRangeM, 6);
            Assert.True(brawl.OrbitSpeedMs > 0);

            CombatPlan run = TacticalRead.Plan(Order(TacticArm.AttackRun), own, theirs, Brackets);
            Assert.Equal(ManoeuvreKind.AttackRun, run.Kind);
            Assert.Equal(1700, run.HoldRangeM, 6);
            Assert.Equal(510, run.InnerRangeM, 6);
        }

        [Fact]
        public void Plan_CarriesTheOrdersArmAndBreakOff()
        {
            CombatPlan plan = TacticalRead.Plan(new TacticOrder { Arm = TacticArm.Kite, BreakOff = BreakOffLevel.High },
                                                Bands((100, 2000)), null, Brackets);
            Assert.Equal(TacticArm.Kite, plan.Arm);
            Assert.Equal(BreakOffLevel.High, plan.BreakOff);
            Assert.True(plan.RangeToleranceM >= TacticalRead.MinToleranceM);
        }

        [Theory]
        [InlineData(1700, 800, 85)]
        [InlineData(1950, 1900, 25)]
        [InlineData(150, 0, 25)]
        [InlineData(80, 0, 20)]
        public void Tolerance_StaysClearOfTheAvoidedBand(double hold, double avoid, double expected)
        {
            Assert.Equal(expected, TacticalRead.Tolerance(hold, avoid), 6);
        }

        // ---- who fights at a hold ---------------------------------------------------------

        [Theory]
        [InlineData(ActionKind.Raid, true)]
        [InlineData(ActionKind.Retaliate, true)]
        [InlineData(ActionKind.Conquer, true)]
        [InlineData(ActionKind.Reinforce, false)]
        [InlineData(ActionKind.Trade, false)]
        public void FightsAtHold_IsTheAttackKindsOnTheirDwell(ActionKind kind, bool fights)
        {
            var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            var fleet = new PendingFleetAction
            {
                Kind = kind,
                CurrentStopKind = FleetStopKind.Engage,
                EstimatedArrivalUtc = now.AddSeconds(120),
            };
            Assert.Equal(fights, TacticalRead.FightsAtHold(fleet, now, new FactionTuning()));
        }

        [Fact]
        public void FightsAtHold_AGuardOnlyInsideItsWindow()
        {
            var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            var tuning = new FactionTuning { GuardDuration = TimeSpan.FromMinutes(10) };
            var guard = new PendingFleetAction { Kind = ActionKind.Guard, EstimatedArrivalUtc = now.AddSeconds(-5) };
            Assert.True(TacticalRead.FightsAtHold(guard, now, tuning));
            Assert.False(TacticalRead.FightsAtHold(guard, now.AddMinutes(11), tuning));

            var raidInTransit = new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                CurrentStopKind = FleetStopKind.Service,
                EstimatedArrivalUtc = now.AddSeconds(60),
            };
            Assert.False(TacticalRead.FightsAtHold(raidInTransit, now, tuning));
        }

        // ---- the resolver ladder ----------------------------------------------------------

        [Fact]
        public void Resolver_WithNoOrders_ReturnsTheLearnedDefault()
        {
            TacticOrder o = TacticResolver.Resolve(new TacticRequest { TargetEntityId = 7, AnchorEntityId = 3 });
            Assert.Equal(TacticArm.Standoff, o.Arm);
            Assert.Equal(BreakOffLevel.Mid, o.BreakOff);
            Assert.Equal(7, o.TargetEntityId);
            Assert.Equal(3, o.AnchorEntityId);
            Assert.Equal(TacticSource.Learned, o.Source);
        }

        [Fact]
        public void Resolver_PlayerOverrideBeatsTheLearnedPick()
        {
            TacticOrder o = TacticResolver.Resolve(new TacticRequest
            {
                TargetEntityId = 7,
                PlayerOverride = new TacticOrder { Arm = TacticArm.Kite, BreakOff = BreakOffLevel.High },
            });
            Assert.Equal(TacticArm.Kite, o.Arm);
            Assert.Equal(TacticSource.PlayerOverride, o.Source);
            Assert.Equal(7, o.TargetEntityId);
        }

        [Fact]
        public void Resolver_CommanderBeatsEverythingAndNamesItsOwnTarget()
        {
            TacticOrder o = TacticResolver.Resolve(new TacticRequest
            {
                TargetEntityId = 7,
                AnchorEntityId = 3,
                PlayerOverride = new TacticOrder { Arm = TacticArm.Kite },
                Commander = new TacticOrder { Arm = TacticArm.Brawl, TargetEntityId = 99 },
            });
            Assert.Equal(TacticArm.Brawl, o.Arm);
            Assert.Equal(TacticSource.Commander, o.Source);
            Assert.Equal(99, o.TargetEntityId);
            Assert.Equal(3, o.AnchorEntityId);
        }
    }
}
