using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="ThrustPhysics"/> — the pure port of SE's real thrust-effectiveness curve
    /// (<c>MyThrusterBlockThrustComponent.CalculateForceMultiplier</c>) plus the dry-TWR and
    /// in-atmosphere helpers built on top of it.
    /// </summary>
    public class ThrustPhysicsTests
    {
        // ---- fake planet catalogue (mirrors FleetRulesTests) --------------------------------

        private sealed class FakePlanetCatalogue : IPlanetCatalogue
        {
            private readonly PlanetRecord[] _planets;
            public FakePlanetCatalogue(params PlanetRecord[] planets) { _planets = planets; }
            public IEnumerable<PlanetRecord> Planets { get { return _planets; } }
            public double GravityAt(Vec3 posKm) { return 0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static PlanetRecord Planet(double cx, bool hasAtmosphere, double atmosphereRadius)
            => new PlanetRecord
            {
                Center = new Vec3(cx, 0, 0),
                SurfaceRadius = 60,
                GravityRadius = 100,
                AtmosphereRadius = atmosphereRadius,
                HasAtmosphere = hasAtmosphere,
            };

        // ---- vanilla worked examples (docs/todo.md's Atmospheric/Hydrogen/Ion table) --------
        // Atmo 100/0, Hydrogen 100/100, Ion 20/100 — "atmo" = inAtmosphere at influence 1 (surface),
        // "space" = deep space (influence 0, not in atmosphere).

        private static readonly ThrustBand Atmospheric = new ThrustBand
        {
            ThrusterType = "Atmospheric",
            ForceMagnitudeN = 100,
            MinPlanetaryInfluence = 0,
            MaxPlanetaryInfluence = 1,
            EffectivenessAtMinInfluence = 0,
            EffectivenessAtMaxInfluence = 1,
            NeedsAtmosphereForInfluence = true,
        };

        private static readonly ThrustBand Hydrogen = new ThrustBand
        {
            ThrusterType = "Hydrogen",
            ForceMagnitudeN = 100,
            MinPlanetaryInfluence = 0,
            MaxPlanetaryInfluence = 0,       // Min == Max: curve short-circuits to 1 everywhere
            EffectivenessAtMinInfluence = 1,
            EffectivenessAtMaxInfluence = 1,
            NeedsAtmosphereForInfluence = false,
        };

        private static readonly ThrustBand Ion = new ThrustBand
        {
            ThrusterType = "Ion",
            ForceMagnitudeN = 100,
            MinPlanetaryInfluence = 0,
            MaxPlanetaryInfluence = 1,
            EffectivenessAtMinInfluence = 1, // full in deep space
            EffectivenessAtMaxInfluence = 0.2f, // ~20% at planet surface
            NeedsAtmosphereForInfluence = false,
        };

        [Fact]
        public void ForceMultiplier_Atmospheric_FullAtSurface_ZeroInDeepSpace()
        {
            Assert.Equal(1f, ThrustPhysics.ForceMultiplier(Atmospheric, 1.0, true));
            Assert.Equal(0f, ThrustPhysics.ForceMultiplier(Atmospheric, 0.0, false));
        }

        [Fact]
        public void ForceMultiplier_Hydrogen_FullEverywhere()
        {
            Assert.Equal(1f, ThrustPhysics.ForceMultiplier(Hydrogen, 1.0, true));
            Assert.Equal(1f, ThrustPhysics.ForceMultiplier(Hydrogen, 0.0, false));
        }

        [Fact]
        public void ForceMultiplier_Ion_FullInSpace_PartialAtSurface()
        {
            Assert.Equal(1f, ThrustPhysics.ForceMultiplier(Ion, 0.0, false));
            Assert.Equal(0.2f, ThrustPhysics.ForceMultiplier(Ion, 1.0, true), 3);
        }

        [Fact]
        public void ForceMultiplier_NeedsAtmosphere_OutOfAtmosphereAtNonZeroInfluence_UsesMinEffectiveness()
        {
            // In orbit/low-influence but no atmosphere: the NeedsAtmosphereForInfluence branch fires
            // regardless of the influence value, not just at influence == 0.
            float m = ThrustPhysics.ForceMultiplier(Atmospheric, 0.5, false);
            Assert.Equal(Atmospheric.EffectivenessAtMinInfluence, m);
        }

        [Fact]
        public void ForceN_SumsMultipleBandsInOneDirection()
        {
            var bands = new List<ThrustBand> { Hydrogen, Ion };
            // Deep space: Hydrogen full (100) + Ion full (100) = 200.
            Assert.Equal(200.0, ThrustPhysics.ForceN(bands, 0.0, false), 3);
        }

        [Fact]
        public void ForceN_NullOrEmpty_ReturnsZero()
        {
            Assert.Equal(0.0, ThrustPhysics.ForceN(null, 1.0, true));
            Assert.Equal(0.0, ThrustPhysics.ForceN(new List<ThrustBand>(), 1.0, true));
        }

        [Fact]
        public void DryTwr_ReadsOnlyDownBucket_IgnoresBackward()
        {
            var byDirection = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                [ThrustDirection.Down] = new List<ThrustBand> { Hydrogen },     // 100N lift
                [ThrustDirection.Backward] = new List<ThrustBand> { Hydrogen, Hydrogen }, // must be ignored
            };
            // weight = 5kg * 1.0g * 9.81 = 49.05N; force = 100N -> TWR ~2.038, unaffected by Backward.
            double twr = ThrustPhysics.DryTwr(byDirection, dryMassKg: 5, gravityG: 1.0, airDensity: 1.0, inAtmosphere: true);
            Assert.Equal(100.0 / (5 * 9.81), twr, 3);
        }

        [Fact]
        public void DryTwr_MissingDownKey_TreatedAsZeroForce()
        {
            var byDirection = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                [ThrustDirection.Backward] = new List<ThrustBand> { Hydrogen },
            };
            double twr = ThrustPhysics.DryTwr(byDirection, dryMassKg: 5, gravityG: 1.0, airDensity: 1.0, inAtmosphere: true);
            Assert.Equal(0.0, twr);
        }

        [Fact]
        public void DryTwr_ZeroGravity_WithForce_IsPositiveInfinity()
        {
            var byDirection = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                [ThrustDirection.Down] = new List<ThrustBand> { Hydrogen },
            };
            double twr = ThrustPhysics.DryTwr(byDirection, dryMassKg: 5, gravityG: 0.0, airDensity: 0.0, inAtmosphere: false);
            Assert.Equal(double.PositiveInfinity, twr);
        }

        [Fact]
        public void DryTwr_ZeroGravity_NoForce_IsZero()
        {
            var byDirection = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            double twr = ThrustPhysics.DryTwr(byDirection, dryMassKg: 5, gravityG: 0.0, airDensity: 0.0, inAtmosphere: false);
            Assert.Equal(0.0, twr);
        }

        [Fact]
        public void DryTwr_NullDictionary_TreatedAsEmpty()
        {
            double twr = ThrustPhysics.DryTwr(null, dryMassKg: 5, gravityG: 1.0, airDensity: 1.0, inAtmosphere: true);
            Assert.Equal(0.0, twr);
        }

        // ---- InAtmosphere ---------------------------------------------------------------------

        [Fact]
        public void InAtmosphere_InsideRadius_HasAtmosphere_ReturnsTrue()
        {
            var cats = new FakePlanetCatalogue(Planet(0, hasAtmosphere: true, atmosphereRadius: 70));
            Assert.True(ThrustPhysics.InAtmosphere(cats, new Vec3(50, 0, 0)));
        }

        [Fact]
        public void InAtmosphere_OutsideRadius_ReturnsFalse()
        {
            var cats = new FakePlanetCatalogue(Planet(0, hasAtmosphere: true, atmosphereRadius: 70));
            Assert.False(ThrustPhysics.InAtmosphere(cats, new Vec3(200, 0, 0)));
        }

        [Fact]
        public void InAtmosphere_NearestPlanetHasNoAtmosphere_ReturnsFalse()
        {
            var cats = new FakePlanetCatalogue(Planet(0, hasAtmosphere: false, atmosphereRadius: 70));
            Assert.False(ThrustPhysics.InAtmosphere(cats, new Vec3(50, 0, 0)));
        }

        [Fact]
        public void InAtmosphere_NoPlanets_ReturnsFalse()
        {
            var cats = new FakePlanetCatalogue();
            Assert.False(ThrustPhysics.InAtmosphere(cats, new Vec3(0, 0, 0)));
        }

        [Fact]
        public void InAtmosphere_PicksNearestPlanet_NotJustAny()
        {
            var cats = new FakePlanetCatalogue(
                Planet(0, hasAtmosphere: false, atmosphereRadius: 70),   // nearest to the sample point, no atmosphere
                Planet(1000, hasAtmosphere: true, atmosphereRadius: 70)); // far away, has atmosphere
            Assert.False(ThrustPhysics.InAtmosphere(cats, new Vec3(50, 0, 0)));
        }
    }
}
