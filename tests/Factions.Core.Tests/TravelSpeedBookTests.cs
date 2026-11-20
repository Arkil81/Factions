using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The trespass pass's speed reading: displacement between passes, so a supercruising ship whose
    /// physics velocity reads 0 is not taken for one lingering after its Intercept warning.
    /// </summary>
    public sealed class TravelSpeedBookTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 19, 0, 12, 31, DateTimeKind.Utc);

        [Fact]
        public void TheLiveSupercruiseReadsAsPassingThrough()
        {
            // 2026-10-19: -738.5 -> -390.6 km on X in 30 s, with speed=0 on every line.
            var book = new TravelSpeedBook();
            Assert.Equal(0, book.Observe(1, 7, new Vec3(-738.5, -9.1, 267.3), T0));
            double ms = book.Observe(1, 7, new Vec3(-390.6, -8.4, 263.8), T0.AddSeconds(30));
            Assert.InRange(ms, 11000, 12000);
            double speed = TravelSpeedBook.Effective(0, ms);
            Assert.False(TrespassLadder.IsLingering(TrespassRung.Intercept, speed, false));
        }

        [Fact]
        public void AShipHoldingStillStillLingers()
        {
            var book = new TravelSpeedBook();
            book.Observe(1, 7, new Vec3(10, 0, 0), T0);
            double ms = book.Observe(1, 7, new Vec3(10.01, 0, 0), T0.AddSeconds(5));   // 2 m/s drift
            Assert.InRange(ms, 1.9, 2.1);
            Assert.True(TrespassLadder.IsLingering(TrespassRung.Intercept, TravelSpeedBook.Effective(0, ms), false));
        }

        [Fact]
        public void ChangingShipsIsNotTravel()
        {
            var book = new TravelSpeedBook();
            book.Observe(1, 7, new Vec3(0, 0, 0), T0);
            Assert.Equal(0, book.Observe(1, 8, new Vec3(500, 0, 0), T0.AddSeconds(5)));
        }

        [Fact]
        public void ASampleTooSoonKeepsTheLastReading()
        {
            var book = new TravelSpeedBook();
            book.Observe(1, 7, new Vec3(0, 0, 0), T0);
            double ms = book.Observe(1, 7, new Vec3(1, 0, 0), T0.AddSeconds(5));       // 200 m/s
            Assert.Equal(ms, book.Observe(1, 7, new Vec3(1.0005, 0, 0), T0.AddSeconds(5.2)));
        }

        [Fact]
        public void PhysicsSpeedStandsWhenItIsTheFaster()
        {
            Assert.Equal(90, TravelSpeedBook.Effective(90, 0));
            Assert.Equal(5000, TravelSpeedBook.Effective(3, 5000));
        }

        [Fact]
        public void PilotsAreMeasuredApart_AndForgottenOffline()
        {
            var book = new TravelSpeedBook();
            book.Observe(1, 7, new Vec3(0, 0, 0), T0);
            Assert.Equal(0, book.Observe(2, 9, new Vec3(100, 0, 0), T0.AddSeconds(5)));
            book.Prune(new HashSet<long> { 2 });
            Assert.Equal(1, book.Count);
            Assert.Equal(0, book.Observe(1, 7, new Vec3(100, 0, 0), T0.AddSeconds(10)));   // fresh after logout
        }
    }
}
