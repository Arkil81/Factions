using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class EconomyCadenceTests
    {
        [Fact]
        public void SameActiveTime_ProducesSameEconomyAcrossDecisionSpeeds()
        {
            EconomyResult slow = RunForActiveSeconds(60.0, 0.1);
            EconomyResult normal = RunForActiveSeconds(60.0, 1.0);
            EconomyResult fast = RunForActiveSeconds(60.0, 5.0);

            Assert.Equal(6, slow.EconomyTicks);
            Assert.Equal(slow.EconomyTicks, normal.EconomyTicks);
            Assert.Equal(slow.EconomyTicks, fast.EconomyTicks);
            Assert.Equal(slow.Treasury, normal.Treasury);
            Assert.Equal(slow.Treasury, fast.Treasury);
            Assert.Equal(slow.InventoryTotal, normal.InventoryTotal);
            Assert.Equal(slow.InventoryTotal, fast.InventoryTotal);
            Assert.Equal(slow.Debt, normal.Debt);
            Assert.Equal(slow.Debt, fast.Debt);
            Assert.Equal(slow.Hold, normal.Hold, 6);
            Assert.Equal(slow.Hold, fast.Hold, 6);
            Assert.Equal(slow.Relation, normal.Relation, 6);
            Assert.Equal(slow.Relation, fast.Relation, 6);
            Assert.Equal(slow.Fatigue, normal.Fatigue, 6);
            Assert.Equal(slow.Fatigue, fast.Fatigue, 6);
            Assert.True(slow.Debt > 0);
            Assert.True(slow.Hold < 1.0);
            Assert.True(slow.Relation < -0.5);
            Assert.True(slow.Fatigue > 0);
        }

        [Fact]
        public void FramePartitioning_DoesNotChangeQuantumCount()
        {
            var coarse = new FixedIntervalAccumulator();
            var fine = new FixedIntervalAccumulator();

            int coarseTicks = coarse.Consume(60.0, 10.0, 100);
            int fineTicks = 0;
            for (int i = 0; i < 240; i++)
                fineTicks += fine.Consume(0.25, 10.0, 100);

            Assert.Equal(6, coarseTicks);
            Assert.Equal(coarseTicks, fineTicks);
            Assert.Equal(coarse.AccumulatedSeconds, fine.AccumulatedSeconds, 6);
        }

        [Fact]
        public void ManagerCadence_IsIndependentOfDecisionSpeed()
        {
            Assert.Equal(60, CountManagerTicks(60.0, 0.1));
            Assert.Equal(60, CountManagerTicks(60.0, 1.0));
            Assert.Equal(60, CountManagerTicks(60.0, 5.0));
        }

        [Fact]
        public void SecondsUntilNext_TracksPartialInterval()
        {
            var clock = new FixedIntervalAccumulator();

            Assert.Equal(1.0, clock.SecondsUntilNext(1.0), 6);
            Assert.Equal(0, clock.Consume(0.25, 1.0, 1));
            Assert.Equal(0.75, clock.SecondsUntilNext(1.0), 6);
            Assert.Equal(1, clock.Consume(0.75, 1.0, 1));
            Assert.Equal(1.0, clock.SecondsUntilNext(1.0), 6);
        }

        [Fact]
        public void PausedElapsedTime_DoesNotCatchUpOnResume()
        {
            var clock = new FixedIntervalAccumulator();

            int beforePause = clock.Consume(9.0, 10.0, 100);
            // Paused time is intentionally not submitted to the active-time accumulator.
            int afterResume = clock.Consume(1.0, 10.0, 100);

            Assert.Equal(0, beforePause);
            Assert.Equal(1, afterResume);
        }

        [Fact]
        public void CappedConsumption_PreservesBacklog()
        {
            var clock = new FixedIntervalAccumulator();

            int first = clock.Consume(35.0, 10.0, 2);
            int second = clock.Consume(0.1, 10.0, 2);

            Assert.Equal(2, first);
            Assert.Equal(1, second);
            Assert.Equal(5.1, clock.AccumulatedSeconds, 6);
        }

        private static EconomyResult RunForActiveSeconds(double activeSeconds, double decisionSpeed)
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Radius = 30;
            node.LastReinforcedUtc = System.DateTime.MinValue;

            SpatialNode rival = world.Graph.Add(new Vec3(12, 0, 0), 1.0, "R1");
            rival.OwnerFactionTag = "R";
            rival.HoldStrength = 1.0;
            rival.StableStationId = 43;
            rival.Radius = 30;
            rival.LastReinforcedUtc = System.DateTime.MinValue;

            world.SetTreasury("F", 1_000_000);
            world.SetDebt("F", 2_000_000);
            world.Tuning.LoanAmount = 0;
            world.Tuning.UpkeepPerSource = 0;

            var state = new FactionState { Tag = "F" };
            // "These two are enemies", stated in both halves of the standing model: the current
            // standing AND the conduct ledger behind it. Seeding the standing alone stopped meaning
            // that once Drift began reading the ledger (FAC-249) -- border pressure by itself is
            // bounded by MaxBorderPressureStandingBias, so an enmity with nothing on the record
            // relaxes toward that bound instead of holding. The ledger is what a run of raids leaves.
            state.Relations["R"] = -0.5;
            state.EarnedRelations["R"] = -0.5;
            // ...and a blow actually exchanged, which now has to be one that COST something: fatigue is
            // derived from the combat power a faction spent on a rival and did not get back, so a
            // stamp alone leaves it at zero and the fatigue equality assertions would compare zeros.
            FightLedger.Append(state, new FightRecord
            {
                Utc = world.UtcNow,                            // world clock -- this suite is deterministic
                Kind = ActionKind.Raid,
                Attacking = true,
                OpponentTag = "R",
                AttackerDps = 1000,
                AttackerRatio = 0,
                AttackerHullsLost = 1,
            }, 64);
            Diplomacy.HitFatigue(state, "R", 0.0, world.UtcNow);
            var personality = new Personality
            {
                Traits = new Traits { Xenophobia = 0.8, Aggression = 0.7, Mercantilism = 0.1 },
            };

            var economyClock = new FixedIntervalAccumulator();
            double decisionAccumulator = 0;
            int economyTicks = 0;
            const double frameSeconds = 0.25;
            int frames = (int)(activeSeconds / frameSeconds);
            double interval = world.Tuning.EconomyIntervalSeconds;

            for (int i = 0; i < frames; i++)
            {
                decisionAccumulator += frameSeconds * decisionSpeed;
                decisionAccumulator -= (int)decisionAccumulator;

                int due = economyClock.Consume(frameSeconds, interval, 100);
                for (int q = 0; q < due; q++)
                {
                    world.AccrueIncome();
                    Diplomacy.Drift(state, personality, world, world.Tuning);
                    Diplomacy.AccrueFatigue(state, world, world.Tuning);
                    world.DecayInfluence(0.02);
                    economyTicks++;
                }
            }

            long inventoryTotal = 0;
            foreach (long amount in node.Inventory.Values)
                inventoryTotal += amount;

            return new EconomyResult
            {
                EconomyTicks = economyTicks,
                Treasury = world.GetTreasury("F"),
                InventoryTotal = inventoryTotal,
                Debt = world.GetDebt("F"),
                Hold = node.HoldStrength,
                Relation = state.RelationTo("R"),
                Fatigue = state.FatigueTo("R"),
            };
        }

        private static int CountManagerTicks(double activeSeconds, double decisionSpeed)
        {
            var managerClock = new FixedIntervalAccumulator();
            double decisionAccumulator = 0;
            int managerTicks = 0;
            const double frameSeconds = 0.25;
            int frames = (int)(activeSeconds / frameSeconds);

            for (int i = 0; i < frames; i++)
            {
                decisionAccumulator += frameSeconds * decisionSpeed;
                decisionAccumulator -= (int)decisionAccumulator;
                managerTicks += managerClock.Consume(frameSeconds, 1.0, 100);
            }

            return managerTicks;
        }

        private struct EconomyResult
        {
            public int EconomyTicks;
            public long Treasury;
            public long InventoryTotal;
            public long Debt;
            public double Hold;
            public double Relation;
            public double Fatigue;
        }
    }
}
