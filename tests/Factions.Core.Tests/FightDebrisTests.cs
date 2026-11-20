using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-425 slice 1: what a fight sheds is closed while the fight is still on.</summary>
    public class FightDebrisTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A marked piece of <paramref name="blocks"/> blocks that broke off
        /// <paramref name="ageSeconds"/> ago, <paramref name="km"/> from the fight.</summary>
        private static FightDebrisFacts Piece(int blocks, double ageSeconds, double km = 1.0)
        {
            return new FightDebrisFacts
            {
                Group = new ReapCandidate { Marked = true, Blocks = blocks, AgeSeconds = ageSeconds, PlayerNear = true },
                DistanceKm = km,
            };
        }

        [Fact]
        public void ASmallPieceIsClosedOnceItHasSettled_DespiteAPlayerWatching()
        {
            Assert.Equal(FightDebrisVerdict.Settling, FightDebris.Judge(Piece(12, 3), false));
            Assert.Equal(FightDebrisVerdict.Close, FightDebris.Judge(Piece(12, FightDebris.SettleSeconds), false));
            // The world reaper would have kept it for its 600 s grace with a player near.
            Assert.Equal(ReapVerdict.TooYoung, GridReaping.Judge(Piece(12, 30).Group, 600));
        }

        [Fact]
        public void AWreckKeepsItsSalvageWindow_UnlessTheSimFloorTrips()
        {
            FightDebrisFacts wreck = Piece(FightDebris.WreckMinBlocks, 60);
            Assert.Equal(FightDebrisVerdict.Salvage, FightDebris.Judge(wreck, false));
            Assert.Equal(FightDebrisVerdict.Close, FightDebris.Judge(wreck, true));
            Assert.Equal(FightDebrisVerdict.Close, FightDebris.Judge(Piece(FightDebris.WreckMinBlocks, FightDebris.WreckSalvageSeconds), false));
            // The floor does not skip the settle: an explosion still mid-flight is left alone.
            Assert.Equal(FightDebrisVerdict.Settling, FightDebris.Judge(Piece(FightDebris.WreckMinBlocks, 2), true));
        }

        [Fact]
        public void AGridFromTheSave_IsOldEnoughForEveryWindow()
        {
            Assert.Equal(FightDebrisVerdict.Close, FightDebris.Judge(Piece(500, double.MaxValue), false));
        }

        [Fact]
        public void EveryRefusalOfTheReaperStillHolds_AndCaptureIsOneMore()
        {
            FightDebrisFacts f = Piece(12, 60);
            f.Group.Held = true;
            Assert.Equal(FightDebrisVerdict.Held, FightDebris.Judge(f, true));

            f = Piece(12, 60); f.Group.Station = true;
            Assert.Equal(FightDebrisVerdict.Station, FightDebris.Judge(f, true));

            f = Piece(12, 60); f.Group.Respawn = true;
            Assert.Equal(FightDebrisVerdict.Respawn, FightDebris.Judge(f, true));

            f = Piece(12, 60); f.Group.UnmarkedPlayerOwned = true;
            Assert.Equal(FightDebrisVerdict.PlayerOwned, FightDebris.Judge(f, true));

            f = Piece(12, 60); f.Captured = true;
            Assert.Equal(FightDebrisVerdict.Captured, FightDebris.Judge(f, true));

            f = Piece(12, 60); f.Group.Marked = false;
            Assert.Equal(FightDebrisVerdict.NotSimBorn, FightDebris.Judge(f, true));
        }

        [Fact]
        public void APieceOutsideTheRadius_OrWithNoSite_IsNotTheFightsToClose()
        {
            Assert.Equal(FightDebrisVerdict.Close, FightDebris.Judge(Piece(12, 60, FightDebris.RadiusKm), false));
            Assert.Equal(FightDebrisVerdict.OutOfRange, FightDebris.Judge(Piece(12, 60, FightDebris.RadiusKm + 0.01), false));
            Assert.Equal(FightDebrisVerdict.OutOfRange, FightDebris.Judge(Piece(12, 60, double.PositiveInfinity), false));
        }

        [Fact]
        public void ASiteMergesNearbyHulls_AndOutlivesThemByTheGrace()
        {
            var sites = new FightSiteMemory();
            sites.Update(new List<Vec3> { new Vec3(0, 0, 0), new Vec3(0.5, 0, 0), new Vec3(10, 0, 0) }, T0);
            Assert.Equal(2, sites.Count);
            Assert.Equal(3.0, sites.NearestKm(new Vec3(3, 0, 0)), 6);

            // The hulls are gone; the wreckage stays a fight's for the grace, then is the reaper's.
            sites.Update(new List<Vec3>(), T0.AddSeconds(FightDebris.SiteGraceSeconds - 1));
            Assert.Equal(2, sites.Count);
            sites.Update(new List<Vec3>(), T0.AddSeconds(FightDebris.SiteGraceSeconds));
            Assert.Equal(0, sites.Count);
            Assert.True(double.IsPositiveInfinity(sites.NearestKm(new Vec3(0, 0, 0))));
        }

        [Fact]
        public void AHullStillThere_KeepsItsSiteAlive()
        {
            var sites = new FightSiteMemory();
            var hull = new List<Vec3> { new Vec3(1, 1, 1) };
            sites.Update(hull, T0);
            sites.Update(hull, T0.AddSeconds(FightDebris.SiteGraceSeconds - 10));
            sites.Update(new List<Vec3>(), T0.AddSeconds(FightDebris.SiteGraceSeconds + 10));
            Assert.Equal(1, sites.Count);
        }
    }
}
