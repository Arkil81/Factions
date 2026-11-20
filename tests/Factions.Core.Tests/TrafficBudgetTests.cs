using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class TrafficBudgetTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        private static TrafficBudget.WindowInput Window(double startSeconds, double busyMs,
            double measured = 0, double civ = 0, double combat = 0)
        {
            return new TrafficBudget.WindowInput
            {
                WindowStartUtc = T0.AddSeconds(startSeconds),
                BusyMs = busyMs,
                FleetMeasuredMs = measured,
                CivilianBaseMs = civ,
                CombatBaseMs = combat,
            };
        }

        [Fact]
        public void BeforeTheFirstWindowEverythingIsAdmitted()
        {
            var b = new TrafficBudget();
            TrafficBudget.Decision d = b.Consider(TrafficClass.Civilian, 100.0, T0);
            Assert.True(d.WouldAdmit);
            Assert.True(double.IsNaN(d.HeadroomMs));
        }

        [Fact]
        public void HeadroomIsTheTargetLessMeasuredBusy()
        {
            var b = new TrafficBudget { TargetBusyPct = 80 };
            b.ObserveWindow(Window(0, 10.0), T0.AddSeconds(30));
            Assert.Equal(0.8 * TrafficBudget.FrameBudgetMs - 10.0, b.HeadroomMs(T0.AddSeconds(30)), 6);
        }

        [Fact]
        public void TheFactionsBudgetIsTheTargetLessEverythingThatIsNotAFleet()
        {
            var b = new TrafficBudget { TargetBusyPct = 80 };
            b.ObserveWindow(new TrafficBudget.WindowInput
            {
                WindowStartUtc = T0, BusyMs = 12.0, FleetMeasuredMs = 5.0, UnmatchedFleetMs = 3.0,
            }, T0.AddSeconds(30));
            // Others = 12 - 5 measured - 3 spawned since the census = 4.
            Assert.Equal(0.8 * TrafficBudget.FrameBudgetMs - 4.0, b.FactionsBudgetMs, 6);
        }

        /// <summary>The 18:53Z shape: a dozen spawns in one window. Each stalls a frame, and until the
        /// window closes that stall is not in the measured busy time, so the budget carries it.</summary>
        [Fact]
        public void SpawnStallsInTheOpenWindowComeOffTheBudgetUntilItCloses()
        {
            var b = new TrafficBudget { TargetBusyPct = 80, WindowSeconds = 30 };
            b.ObserveWindow(new TrafficBudget.WindowInput { WindowStartUtc = T0, BusyMs = 6.0 }, T0.AddSeconds(30));
            double before = b.FactionsBudgetMs;
            for (int i = 0; i < 12; i++)
                b.NoteSpawnStall(TrafficBudget.SpawnStallMs(1000), T0.AddSeconds(31 + i));
            // 12 x (40 + 65) ms over 30 s of frames = 1260 / 1800 = 0.7 ms a frame.
            Assert.Equal(0.7, b.SpawnStallLoadMs, 6);
            Assert.Equal(before - 0.7, b.FactionsBudgetMs, 6);

            b.ObserveWindow(new TrafficBudget.WindowInput { WindowStartUtc = T0.AddSeconds(30), BusyMs = 6.7 }, T0.AddSeconds(60));
            Assert.Equal(0.0, b.SpawnStallLoadMs, 6);
        }

        [Fact]
        public void ABiggerHullStallsLonger()
        {
            Assert.True(TrafficBudget.SpawnStallMs(2026) > TrafficBudget.SpawnStallMs(600));
            Assert.InRange(TrafficBudget.SpawnStallMs(2026), 158.0, 175.0);   // the measured Naginata
        }

        [Fact]
        public void AFleetOverTheHeadroomWouldBeRefused()
        {
            var b = new TrafficBudget { TargetBusyPct = 80 };             // target 13.33 ms
            b.ObserveWindow(Window(0, 12.0), T0.AddSeconds(30));
            DateTime now = T0.AddSeconds(31);
            Assert.True(b.Consider(TrafficClass.Civilian, 1.0, now).WouldAdmit);
            Assert.False(b.Consider(TrafficClass.Civilian, 2.0, now).WouldAdmit);

            int admit, refuse; double admittedMs, refusedMs;
            b.TakeWindow(out admit, out refuse, out admittedMs, out refusedMs);
            Assert.Equal(1, admit);
            Assert.Equal(1, refuse);
            Assert.Equal(2.0, refusedMs, 6);
            b.TakeWindow(out admit, out refuse, out admittedMs, out refusedMs);
            Assert.Equal(0, admit + refuse);
        }

        /// <summary>The burst the design exists for: twenty spawns in one window, each small enough
        /// to fit alone, must not all fit, because each one's reservation shrinks the next's room.</summary>
        [Fact]
        public void ReservationsStopABurstThatFitsOneAtATime()
        {
            var b = new TrafficBudget { TargetBusyPct = 80 };
            b.ObserveWindow(Window(0, 8.0), T0.AddSeconds(30));          // 5.33 ms of room
            int admitted = 0;
            for (int i = 0; i < 20; i++)
            {
                DateTime now = T0.AddSeconds(31 + i);
                TrafficBudget.Decision d = b.Consider(TrafficClass.Civilian, 0.5, now);
                if (d.WouldAdmit) admitted++;
                b.Reserve("f" + i, d.PredictedMs, now);
            }
            Assert.Equal(10, admitted);
        }

        [Fact]
        public void AReservationIsHeldUntilAWholeWindowHasSeenIt()
        {
            var b = new TrafficBudget { TargetBusyPct = 80, SettleSeconds = 10 };
            b.ObserveWindow(Window(0, 5.0), T0.AddSeconds(30));
            b.Reserve("a", 2.0, T0.AddSeconds(35));

            // The window 30-60 opened before the spawn settled at 45: it saw only part of it.
            b.ObserveWindow(Window(30, 6.0), T0.AddSeconds(60));
            Assert.Equal(2.0, b.ReservedMs(T0.AddSeconds(60)), 6);

            // The window 60-90 opened after it settled, so it measured all of it.
            b.ObserveWindow(Window(60, 7.0), T0.AddSeconds(90));
            Assert.Equal(0.0, b.ReservedMs(T0.AddSeconds(90)), 6);
        }

        [Fact]
        public void AReservationNoWindowReleasesExpires()
        {
            var b = new TrafficBudget { ReservationMaxSeconds = 300 };
            b.Reserve("a", 2.0, T0);
            Assert.Equal(2.0, b.ReservedMs(T0.AddSeconds(299)), 6);
            Assert.Equal(0.0, b.ReservedMs(T0.AddSeconds(300)), 6);
        }

        [Fact]
        public void ReservingAKeyAgainReplacesIt()
        {
            var b = new TrafficBudget();
            b.Reserve("a", 2.0, T0);
            b.Reserve("a", 3.0, T0.AddSeconds(1));
            Assert.Equal(1, b.ReservationCount);
            Assert.Equal(3.0, b.ReservedMs(T0.AddSeconds(1)), 6);
        }

        [Fact]
        public void ACivilianWindowTeachesTheCivilianMultiplierOnly()
        {
            var b = new TrafficBudget { CalibrationAlpha = 1.0 };
            b.ObserveWindow(Window(0, 10, measured: 6.0, civ: 3.0), T0.AddSeconds(30));
            Assert.Equal(2.0, b.CivilianMultiplier, 6);
            Assert.Equal(1.0, b.CombatMultiplier, 6);
            Assert.Equal(1, b.CivilianSamples);
            Assert.Equal(0, b.CombatSamples);
        }

        /// <summary>FAC-425's fight: the combat share is learnt after taking the civilian share out
        /// at its own multiplier, so the civilian traffic in the same window does not inflate it.</summary>
        [Fact]
        public void ACombatWindowRemovesTheCivilianShareFirst()
        {
            var b = new TrafficBudget { CalibrationAlpha = 1.0, CivilianMultiplier = 2.0 };
            // civ 0.5 x 2 = 1 ms of the 17 measured; combat 2.0 unscaled -> 8x.
            b.ObserveWindow(Window(0, 20, measured: 17.0, civ: 0.5, combat: 2.0), T0.AddSeconds(30));
            Assert.Equal(8.0, b.CombatMultiplier, 6);
            Assert.Equal(2.0, b.CivilianMultiplier, 6);
        }

        [Fact]
        public void AMixedWindowTeachesNothing()
        {
            var b = new TrafficBudget { CalibrationAlpha = 1.0 };
            b.ObserveWindow(Window(0, 10, measured: 9.0, civ: 1.5, combat: 1.5), T0.AddSeconds(30));
            Assert.Equal(1.0, b.CivilianMultiplier, 6);
            Assert.Equal(1.0, b.CombatMultiplier, 6);
            Assert.Equal(0, b.CivilianSamples + b.CombatSamples);
        }

        [Fact]
        public void AnAlmostEmptyWorldTeachesNothing()
        {
            var b = new TrafficBudget { CalibrationAlpha = 1.0, MinCalibrationMs = 0.5 };
            b.ObserveWindow(Window(0, 5, measured: 3.0, civ: 0.1), T0.AddSeconds(30));
            Assert.Equal(1.0, b.CivilianMultiplier, 6);
        }

        [Fact]
        public void AMultiplierSampleIsClampedAndBlended()
        {
            var b = new TrafficBudget { CalibrationAlpha = 0.5, MultiplierMax = 10.0 };
            b.ObserveWindow(Window(0, 10, measured: 100.0, civ: 1.0), T0.AddSeconds(30));
            Assert.Equal(1.0 + 0.5 * (10.0 - 1.0), b.CivilianMultiplier, 6);
        }

        [Fact]
        public void ThePredictionIsScaledByItsClassMultiplier()
        {
            var b = new TrafficBudget { CivilianMultiplier = 2.0, CombatMultiplier = 8.0 };
            Assert.Equal(1.0, b.Consider(TrafficClass.Civilian, 0.5, T0).PredictedMs, 6);
            Assert.Equal(4.0, b.Consider(TrafficClass.Combat, 0.5, T0).PredictedMs, 6);
        }
    }

    public class FleetLoadCostTests
    {
        private static readonly PrefabCeiling Law = new PrefabCeiling { CostPerBlockExpMs = 1.0, Exponent = 1.0 };

        private static int Blocks(string prefab)
        {
            switch (prefab)
            {
                case "Big": return 300;
                case "Small": return 100;
                default: return 0;
            }
        }

        [Fact]
        public void AConvoyCostsOneHullWhateverItsSize()
        {
            var f = new PendingFleetAction { Kind = ActionKind.Haul, ShipPrefabId = "Big", ConvoySize = 4 };
            int unknown;
            Assert.Equal(300.0, FleetLoadCost.BaseMs(f, Blocks, Law, 7.0, out unknown), 6);
            Assert.Equal(0, unknown);
        }

        [Fact]
        public void ASquadronCostsEverySurvivingHull()
        {
            var f = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ShipPrefabId = "Big",
                Squadron = new List<string> { "Big", "Small", "Small" },
            };
            int unknown;
            Assert.Equal(500.0, FleetLoadCost.BaseMs(f, Blocks, Law, 7.0, out unknown), 6);

            f.MarkHullLost("Small");
            Assert.Equal(400.0, FleetLoadCost.BaseMs(f, Blocks, Law, 7.0, out unknown), 6);
        }

        [Fact]
        public void AnUnknownPrefabIsChargedTheFallback()
        {
            var f = new PendingFleetAction { Kind = ActionKind.Trade, ShipPrefabId = "" };
            int unknown;
            Assert.Equal(7.0, FleetLoadCost.BaseMs(f, Blocks, Law, 7.0, out unknown), 6);
            Assert.Equal(1, unknown);
        }

        [Fact]
        public void BlocksCountEveryHullTheFleetSpawns()
        {
            var convoy = new PendingFleetAction { Kind = ActionKind.Haul, ShipPrefabId = "Big", ConvoySize = 3 };
            Assert.Equal(300, FleetLoadCost.Blocks(convoy, Blocks));
            var squadron = new PendingFleetAction
            {
                Kind = ActionKind.Raid, Squadron = new List<string> { "Big", "Small", "Unknown" },
            };
            Assert.Equal(400, FleetLoadCost.Blocks(squadron, Blocks));
        }

        [Fact]
        public void CombatKindsAreTheCombatClass()
        {
            Assert.Equal(TrafficClass.Combat, FleetLoadCost.ClassOf(ActionKind.Raid));
            Assert.Equal(TrafficClass.Combat, FleetLoadCost.ClassOf(ActionKind.Guard));
            Assert.Equal(TrafficClass.Civilian, FleetLoadCost.ClassOf(ActionKind.Haul));
            Assert.Equal(TrafficClass.Civilian, FleetLoadCost.ClassOf(ActionKind.Trade));
        }
    }
}
