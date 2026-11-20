using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ShipSensorTests
    {
        // Block volumes, m³: Size.X*Y*Z cubes x edge³ (2.5 m large, 0.5 m small).
        private const double SmallAntenna = 0.25;          // SmallBlockRadioAntenna, 1x1x2
        private const double LargeCompact = 46.875;        // LargeBlockCompactRadioAntenna, 1x3x1
        private const double LargeStandard = 187.5;        // LargeBlockRadioAntenna, 1x6x2
        private const double Dish = 1171.875;              // LargeBlockRadioAntennaDish, 5x3x5

        private static AntennaReading Antenna(double volumeM3, double radiusM, bool broadcasting = true)
            => new AntennaReading { VolumeM3 = volumeM3, RadiusM = radiusM, Broadcasting = broadcasting };

        private static double Km(params AntennaReading[] antennas)
            => ShipSensor.StrengthKm(antennas, TestSupport.Tuning());

        [Fact]
        public void NoAntenna_IsTheFloor_WhateverTheGrid()
        {
            Assert.Equal(25.0, Km(), 9);
            Assert.Equal(25.0, ShipSensor.StrengthKm(null, TestSupport.Tuning()), 9);
        }

        [Fact]
        public void TheAgreedDesignTable()
        {
            Assert.InRange(Km(Antenna(SmallAntenna, 50000)), 39.0, 39.5);
            Assert.InRange(Km(Antenna(LargeCompact, 50000)), 78.0, 78.5);
            Assert.Equal(100.0, Km(Antenna(LargeStandard, 50000)), 9);
            Assert.InRange(Km(Antenna(Dish, 50000, broadcasting: false)), 64.0, 65.0);
            Assert.InRange(Km(Antenna(Dish, 50000)), 143.5, 144.0);
        }

        [Fact]
        public void ACompactAntennaAtFiveKilometres_ListensAndBarelyIlluminates()
        {
            // 25 + 0.707 x (25 + 50 x 0.1)
            Assert.InRange(Km(Antenna(LargeCompact, 5000)), 46.0, 46.5);
        }

        [Fact]
        public void BroadcastingOff_LeavesOnlyThePassiveHalf_HoweverFarTheSliderIsSet()
        {
            Assert.Equal(50.0, Km(Antenna(LargeStandard, 50000, broadcasting: false)), 9);
            Assert.Equal(100.0, Km(Antenna(LargeStandard, 50000, broadcasting: true)), 9);
        }

        [Fact]
        public void RadiusBeyondTheReference_DoesNotExtendRange()
        {
            Assert.Equal(Km(Antenna(LargeStandard, 50000)), Km(Antenna(LargeStandard, 500000)), 9);
        }

        [Fact]
        public void MoreAntennas_DiminishingReturns_SqrtOfCount()
        {
            AntennaReading a = Antenna(LargeStandard, 50000);
            double one = Km(a) - 25.0;
            double four = Km(a, a, a, a) - 25.0;
            Assert.Equal(2.0 * one, four, 9);
        }

        [Fact]
        public void OneDish_OutreachesTwoStandardAntennas()
        {
            AntennaReading std = Antenna(LargeStandard, 50000);
            Assert.True(Km(Antenna(Dish, 50000)) > Km(std, std));
        }

        [Fact]
        public void ReferenceKm_IsOneStandardAntennaAtFullRadius()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(ShipSensor.StrengthKm(new List<AntennaReading> { Antenna(LargeStandard, 50000) }, t),
                         ShipSensor.ReferenceKm(t), 9);
        }
    }
}
