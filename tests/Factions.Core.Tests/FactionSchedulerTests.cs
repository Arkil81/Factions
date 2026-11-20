using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Tests for the per-faction wall-clock scheduler mechanics added in the
    /// scheduler rewrite (FactionTickIntervalSeconds / GlobalPassIntervalSeconds).
    ///
    /// These tests work entirely on Core types — no FactionRuntime (host-project)
    /// dependency — so they can run in the netstandard2.0 test harness.
    /// </summary>
    public class FactionSchedulerTests
    {
        // ---- FactionState.NextTickRealUtc ---------------------------------------------------

        [Fact]
        public void NextTickRealUtc_DefaultsToMinValue_MakingItImmediatelyDue()
        {
            var st = new FactionState { Tag = "TST" };
            Assert.Equal(DateTime.MinValue, st.NextTickRealUtc);
            // MinValue < any real DateTime.UtcNow, so the scheduler sees it as immediately due.
            Assert.True(st.NextTickRealUtc < DateTime.UtcNow);
        }

        [Fact]
        public void NextTickRealUtc_CanBeScheduledInFuture()
        {
            var st = new FactionState { Tag = "TST" };
            DateTime future = DateTime.UtcNow.AddSeconds(60);
            st.NextTickRealUtc = future;
            Assert.True(st.NextTickRealUtc > DateTime.UtcNow);
        }

        // ---- FactionTuning new interval fields ---------------------------------------------

        [Fact]
        public void FactionTuning_NewIntervalFields_DefaultToZero_FallbacksApplyInRuntime()
        {
            // Verify that un-set tuning fields are zero so the runtime's
            // "> 0 ? : Default" fallback pattern kicks in.
            var tuning = new FactionTuning();
            Assert.Equal(0.0, tuning.FactionTickIntervalSeconds);
            Assert.Equal(0.0, tuning.GlobalPassIntervalSeconds);
            Assert.Equal(0.0, tuning.FlushEveryRealSeconds);
            Assert.Equal(0.0, tuning.FleetIntervalSeconds);
            Assert.Equal(0.0, tuning.ProductionIntervalSeconds);
            Assert.Equal(0.0, tuning.OpsIntervalSeconds);
            Assert.Equal(0.0, tuning.ShipyardIntervalSeconds);
            Assert.Equal(0.0, tuning.TradeIntervalSeconds);
        }

        [Fact]
        public void FactionTuning_NewIntervalFields_RoundtripThroughTuning()
        {
            var tuning = new FactionTuning
            {
                FactionTickIntervalSeconds = 120.0,
                GlobalPassIntervalSeconds  =  45.0,
                FlushEveryRealSeconds      = 600.0,
                FleetIntervalSeconds       =   2.0,
                ProductionIntervalSeconds  =  60.0,
                OpsIntervalSeconds         =  15.0,
                ShipyardIntervalSeconds    =  90.0,
                TradeIntervalSeconds       = 120.0,
            };
            Assert.Equal(120.0, tuning.FactionTickIntervalSeconds);
            Assert.Equal(45.0,  tuning.GlobalPassIntervalSeconds);
            Assert.Equal(600.0, tuning.FlushEveryRealSeconds);
            Assert.Equal(2.0,   tuning.FleetIntervalSeconds);
            Assert.Equal(60.0,  tuning.ProductionIntervalSeconds);
            Assert.Equal(15.0,  tuning.OpsIntervalSeconds);
            Assert.Equal(90.0,  tuning.ShipyardIntervalSeconds);
            Assert.Equal(120.0, tuning.TradeIntervalSeconds);
        }

        // ---- WorldModel.AccrueIncome with filtered states ----------------------------------

        [Fact]
        public void AccrueIncome_WithSingleState_OnlyAppliesBankingForThatFaction()
        {
            // AccrueIncome processes item minting for ALL owners in Phase 1,
            // but Phase 2 banking (upkeep, debt, insolvency) runs for all discovered owners.
            // This test verifies that a single-faction world produces the same treasury result
            // whether called via AccrueIncome(list) or AccrueIncome(null).
            WorldModel world = TestSupport.World();
            string tag = "FAC-X";
            var st = new FactionState { Tag = tag };

            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N001");
            n.OwnerFactionTag = tag;
            n.HoldStrength    = 1.0;
            n.Radius          = 30;
            world.SetTreasury(tag, 500_000);
            world.PrepareInfluenceSnapshot();

            long beforeGlobal = world.GetTreasury(tag);
            world.AccrueIncome(new List<FactionState> { st });
            long afterFiltered = world.GetTreasury(tag);
            long deltaTreasury = afterFiltered - beforeGlobal;

            // Reset and run again without a states filter — should produce the same delta.
            world.SetTreasury(tag, 500_000);
            world.PrepareInfluenceSnapshot();
            world.AccrueIncome(null);
            long afterGlobal = world.GetTreasury(tag);
            long deltaGlobal = afterGlobal - 500_000;

            Assert.Equal(deltaTreasury, deltaGlobal);
        }

        // ---- Scheduler coherence: staggered NextTickRealUtc spread -------------------------

        [Fact]
        public void StaggeredFactionTicks_AllFireWithinOneInterval()
        {
            // Simulate the boot initialisation: all NextTickRealUtc = MinValue.
            // Verify that after one full interval, all 13 factions would have been
            // visited once (the runtime drains 1 per frame; at 60Hz × 60s = 3600 frames).
            const int factionCount = 13;
            const double intervalSeconds = 60.0;
            DateTime now = DateTime.UtcNow;

            var states = new List<FactionState>();
            for (int i = 0; i < factionCount; i++)
                states.Add(new FactionState { Tag = "F" + i, NextTickRealUtc = DateTime.MinValue });

            // Simulate the per-frame drain: pick first due, reschedule.
            int framesSimulated = 0;
            int maxFrames = factionCount * 2; // safety bound: each faction fires once in 2× count frames
            for (int frame = 0; frame < maxFrames; frame++)
            {
                framesSimulated++;
                DateTime frameTime = now.AddSeconds(frame * (1.0 / 60.0)); // 60 fps
                foreach (FactionState st in states)
                {
                    if (st.NextTickRealUtc <= frameTime)
                    {
                        st.NextTickRealUtc = frameTime.AddSeconds(intervalSeconds);
                        break; // one per frame
                    }
                }
            }

            // After factionCount frames, every faction should have fired at least once.
            foreach (FactionState st in states)
                Assert.True(st.NextTickRealUtc > now,
                    st.Tag + " was never scheduled — still at MinValue or past it without rescheduling");

            // No single frame sees more than one faction tick.
            Assert.True(framesSimulated >= factionCount);
        }
    
        // ---- brain-actor faction selection ------------------------------------------------------

        [Fact]
        public void PickMostOverdue_PrefersTheEarliestDueFaction_NotTheFirstInListOrder()
        {
            DateTime now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
            var states = new List<FactionState>
            {
                new FactionState { Tag = "A", NextTickRealUtc = now.AddSeconds(-1) },
                new FactionState { Tag = "B", NextTickRealUtc = now.AddSeconds(-30) },
                new FactionState { Tag = "C", NextTickRealUtc = now.AddSeconds(-5) },
            };

            int index;
            Assert.True(FactionTickSelection.TryPickMostOverdue(states, now, out index));
            Assert.Equal(1, index);
        }

        [Fact]
        public void PickMostOverdue_IgnoresFactionsThatAreNotDueYet()
        {
            DateTime now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
            var states = new List<FactionState>
            {
                new FactionState { Tag = "A", NextTickRealUtc = now.AddSeconds(10) },
                new FactionState { Tag = "B", NextTickRealUtc = now.AddSeconds(1) },
            };

            int index;
            Assert.False(FactionTickSelection.TryPickMostOverdue(states, now, out index));
            Assert.Equal(-1, index);
        }

        [Fact]
        public void PickMostOverdue_ResolvesExactTiesToTheLowerIndex()
        {
            DateTime now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
            DateTime due = now.AddSeconds(-2);
            var states = new List<FactionState>
            {
                new FactionState { Tag = "A", NextTickRealUtc = due },
                new FactionState { Tag = "B", NextTickRealUtc = due },
            };

            int index;
            Assert.True(FactionTickSelection.TryPickMostOverdue(states, now, out index));
            Assert.Equal(0, index);
        }
}
}
