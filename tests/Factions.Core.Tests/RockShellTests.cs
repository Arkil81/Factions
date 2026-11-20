using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The rock shell: a sphere at rest, an egg on the velocity above a speed.</summary>
    public class RockShellTests
    {
        private static readonly RockShellShape Shape = new RockShellShape
        {
            SideKm = 3.0, BackKm = 1.5, LeadSeconds = 10.0, MinSpeedMs = 100.0,
        };

        private static RockShellAnchor At(double vx) { return new RockShellAnchor(new Vec3(0, 0, 0), new Vec3(vx, 0, 0)); }

        [Fact]
        public void AtRest_IsASphereOfSide()
        {
            Assert.True(RockShell.Touches(At(0), Shape, new Vec3(-2.99, 0, 0), 0, 1.0));
            Assert.False(RockShell.Touches(At(0), Shape, new Vec3(-3.01, 0, 0), 0, 1.0));
            Assert.True(RockShell.Touches(At(0), Shape, new Vec3(0, 2.99, 0), 0, 1.0));
        }

        [Fact]
        public void BelowMinSpeed_StaysASphere_BackIsNotApplied()
        {
            // 99 m/s: behind at 2 km is inside a 3 km sphere though BackKm is 1.5.
            Assert.True(RockShell.Touches(At(99), Shape, new Vec3(-2.0, 0, 0), 0, 1.0));
            Assert.False(RockShell.Touches(At(99), Shape, new Vec3(3.5, 0, 0), 0, 1.0));
        }

        [Fact]
        public void AtSpeed_StretchesAheadByLeadTimesSpeed_AndTrimsBehind()
        {
            RockShellAnchor a = At(1000);   // 3 + 10 s x 1 km/s = 13 km ahead
            Assert.True(RockShell.Touches(a, Shape, new Vec3(12.9, 0, 0), 0, 1.0));
            Assert.False(RockShell.Touches(a, Shape, new Vec3(13.1, 0, 0), 0, 1.0));
            Assert.True(RockShell.Touches(a, Shape, new Vec3(-1.4, 0, 0), 0, 1.0));
            Assert.False(RockShell.Touches(a, Shape, new Vec3(-1.6, 0, 0), 0, 1.0));
            Assert.True(RockShell.Touches(a, Shape, new Vec3(0, 2.99, 0), 0, 1.0));
            Assert.False(RockShell.Touches(a, Shape, new Vec3(0, 3.01, 0), 0, 1.0));
        }

        [Fact]
        public void ObjectRadius_Inflates_SoABigRockJustOutsideStillTouches()
        {
            Assert.False(RockShell.Touches(At(0), Shape, new Vec3(3.3, 0, 0), 0, 1.0));
            Assert.True(RockShell.Touches(At(0), Shape, new Vec3(3.3, 0, 0), 0.5, 1.0));
        }

        [Fact]
        public void Scale_ShrinksTheShell_ForTheSpawnSideOfTheBand()
        {
            var p = new Vec3(2.9, 0, 0);
            Assert.True(RockShell.Touches(At(0), Shape, p, 0, 1.0));
            Assert.False(RockShell.Touches(At(0), Shape, p, 0, 1.0 / 1.15));
        }

        [Fact]
        public void BoundingRadius_ContainsEveryPointTheShellTouches()
        {
            var rng = new Random(7);
            foreach (double speed in new[] { 0.0, 50.0, 150.0, 800.0 })
            {
                var a = new RockShellAnchor(new Vec3(10, -4, 2), new Vec3(speed * 0.6, speed * 0.8, 0));
                double bound = RockShell.BoundingRadiusKm(a, Shape, 1.0);
                for (int i = 0; i < 4000; i++)
                {
                    var p = new Vec3(a.PosKm.X + (rng.NextDouble() - 0.5) * 40,
                                     a.PosKm.Y + (rng.NextDouble() - 0.5) * 40,
                                     a.PosKm.Z + (rng.NextDouble() - 0.5) * 40);
                    if (RockShell.Touches(a, Shape, p, 0, 1.0))
                        Assert.True(p.DistanceTo(a.PosKm) <= bound + 1e-9, "speed " + speed);
                }
            }
        }

        /// <summary>Inflating the semi-axes by the radius must contain the shell's Minkowski sum with the sphere: a
        /// point within the radius of any point inside the shell must test as touching.</summary>
        [Fact]
        public void RadiusInflation_IsConservative()
        {
            var rng = new Random(11);
            RockShellAnchor a = At(600);
            const double r = 0.4;
            int checkedPoints = 0;
            for (int i = 0; i < 20000; i++)
            {
                var inside = new Vec3((rng.NextDouble() - 0.4) * 20, (rng.NextDouble() - 0.5) * 7, (rng.NextDouble() - 0.5) * 7);
                if (!RockShell.Touches(a, Shape, inside, 0, 1.0)) continue;
                double ux = rng.NextDouble() - 0.5, uy = rng.NextDouble() - 0.5, uz = rng.NextDouble() - 0.5;
                double n = Math.Sqrt(ux * ux + uy * uy + uz * uz);
                double d = rng.NextDouble() * r;
                var near = new Vec3(inside.X + ux / n * d, inside.Y + uy / n * d, inside.Z + uz / n * d);
                Assert.True(RockShell.Touches(a, Shape, near, r, 1.0));
                checkedPoints++;
            }
            Assert.True(checkedPoints > 500);
        }

        [Fact]
        public void Probe_AnswersExactlyAsTouches()
        {
            var rng = new Random(23);
            foreach (double speed in new[] { 0.0, 99.0, 100.0, 450.0, 1200.0 })
            {
                var a = new RockShellAnchor(new Vec3(5, 1, -7), new Vec3(speed * 0.36, -speed * 0.48, speed * 0.8));
                var probe = new RockShellProbe(a, Shape);
                for (int i = 0; i < 5000; i++)
                {
                    var p = new Vec3(5 + (rng.NextDouble() - 0.5) * 36, 1 + (rng.NextDouble() - 0.5) * 36, -7 + (rng.NextDouble() - 0.5) * 36);
                    double r = rng.NextDouble() * 0.6;
                    double scale = rng.NextDouble() < 0.5 ? 1.0 : 1.0 / 1.15;
                    Assert.Equal(RockShell.Touches(a, Shape, p, r, scale), probe.Touches(p, r, scale));
                }
            }
        }

        [Fact]
        public void Shape_ReadsEveryKeyFromTuning()
        {
            var t = new FactionTuning
            {
                FeatureRockShellSideKm = 2, FeatureRockShellBackKm = 1, FeatureRockShellLeadSeconds = 4,
                FeatureRockShellMinSpeedMs = 50,
            };
            RockShellShape s = RockShellShape.From(t);
            Assert.Equal(2, s.SideKm);
            Assert.Equal(1, s.BackKm);
            Assert.Equal(4, s.LeadSeconds);
            Assert.Equal(50, s.MinSpeedMs);
        }

        [Fact]
        public void TuningDefaults_AreTheMeasuredShell()
        {
            var t = new FactionTuning();
            Assert.Equal(3.0, t.FeatureRockShellSideKm);
            Assert.Equal(3.0, t.FeatureRockShellBackKm);
            Assert.Equal(10.0, t.FeatureRockShellLeadSeconds);
            Assert.Equal(100.0, t.FeatureRockShellMinSpeedMs);
            Assert.Equal(1.15, t.FeatureRockShellHysteresis);
            Assert.Equal(1.0, t.FeatureRockSpawnBudgetMs);
        }

        [Fact]
        public void StationsDespawnAtOnce_AndGoQuietPastFiveKm()
        {
            var t = new FactionTuning();
            Assert.Equal(0.0, t.StationDespawnGraceSeconds);
            Assert.Equal(5.0, t.StationQuietKm);
            Assert.Equal(3.0, t.StationQuietWakeCombatKm);
            Assert.Equal(0.5, t.StationQuietHysteresisKm);
        }
    }
}
