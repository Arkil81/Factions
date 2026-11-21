using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Where a surface station stands when its node is on a cliff: SE's own installation
    /// slope limit, measured on a ring at the footprint's reach.</summary>
    public sealed class SurfaceSiteSearchTests
    {
        private const double Reach = 40.0;

        private static double Flat(double east, double north) { return 0.0; }

        /// <summary>A 60 m wall running north-south through the node: flat ground to the west,
        /// a plateau to the east.</summary>
        private static double Wall(double east, double north) { return east >= 0.0 ? 60.0 : 0.0; }

        [Fact]
        public void LevelGround_StaysOnTheNode_InNineSamples()
        {
            SurfaceSiteChoice c = SurfaceSiteSearch.Choose(Flat, Reach);

            Assert.False(c.Moved);
            Assert.True(c.Level);
            Assert.Equal(0.0, c.DistanceM);
            Assert.Equal(SurfaceSiteSearch.RingSamples + 1, c.Probes);
        }

        [Fact]
        public void AGentleSlopeWithinSesLimit_StaysOnTheNode()
        {
            // 0.2 rise over run everywhere: inside SE's 0.2527.
            SurfaceSiteChoice c = SurfaceSiteSearch.Choose((e, n) => 0.2 * e, Reach);

            Assert.False(c.Moved);
            Assert.True(c.Level);
            Assert.Equal(0.2, c.OriginalGrade, 6);
        }

        [Fact]
        public void ACliffThroughTheNode_MovesTheStationOffIt()
        {
            SurfaceSiteChoice c = SurfaceSiteSearch.Choose(Wall, Reach);

            Assert.Equal(1.5, c.OriginalGrade, 6);   // the 60 m wall over a 40 m reach
            Assert.True(c.Moved);
            Assert.True(c.Level);
            Assert.Equal(0.0, c.Grade, 6);
            // Every sample of the chosen footprint is on one side of the wall.
            Assert.True(c.EastM + Reach < 0.0 || c.EastM - Reach >= 0.0,
                "chosen site at east=" + c.EastM + " still straddles the wall");
            Assert.True(c.DistanceM <= SurfaceSiteSearch.MaxMoveM(Reach));
        }

        [Fact]
        public void TheNearestLevelRingWins_NotTheFurthest()
        {
            SurfaceSiteChoice c = SurfaceSiteSearch.Choose(Wall, Reach);

            // Ring 1 (40 m) cannot clear a 40 m reach off a wall through the node; ring 2 (80 m) can.
            Assert.Equal(2 * Reach, c.DistanceM, 6);
        }

        [Fact]
        public void TheChoiceIsDeterministic()
        {
            SurfaceSiteChoice a = SurfaceSiteSearch.Choose(Wall, Reach);
            SurfaceSiteChoice b = SurfaceSiteSearch.Choose(Wall, Reach);

            Assert.Equal(a.EastM, b.EastM);
            Assert.Equal(a.NorthM, b.NorthM);
        }

        [Fact]
        public void SteepEverywhere_StaysOnTheNode()
        {
            // A uniform 0.6 grade: no candidate is level, none halves the grade, so nothing moves.
            SurfaceSiteChoice c = SurfaceSiteSearch.Choose((e, n) => 0.6 * e, Reach);

            Assert.False(c.Moved);
            Assert.False(c.Level);
            Assert.Equal(c.OriginalGrade, c.Grade);
        }

        [Fact]
        public void NothingLevel_ButMuchGentlerNearby_TakesTheBestEffortSite()
        {
            // Steep (1.0) inside 100 m of the node, 0.4 beyond: never level, but 0.4 is under half.
            Func<double, double, double> terrain = (e, n) =>
            {
                double r = Math.Sqrt(e * e + n * n);
                return r < 100.0 ? r : 100.0 + 0.4 * (r - 100.0);
            };
            SurfaceSiteChoice c = SurfaceSiteSearch.Choose(terrain, Reach);

            Assert.True(c.Moved);
            Assert.False(c.Level);
            Assert.True(c.Grade <= c.OriginalGrade * SurfaceSiteSearch.BestEffortFraction);
        }

        [Fact]
        public void ASmallFootprint_StillStepsPastTheMinimumRingSpacing()
        {
            SurfaceSiteChoice c = SurfaceSiteSearch.Choose(Wall, 5.0);

            Assert.True(c.Moved);
            Assert.True(c.DistanceM >= SurfaceSiteSearch.MinStepM);
        }
    }
}
