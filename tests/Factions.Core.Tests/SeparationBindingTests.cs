using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The authored separations in the shipped world files reach <see cref="WorldModel.SeparationAtKm"/>.
    ///
    /// <para><b>Why this file exists.</b> Per-body and per-feature separation shipped inert: the world
    /// files authored <c>NodeSeparationKm</c> while the read path looked for a different name, and
    /// every test on both sides passed because each set its own field directly. A test whose input the
    /// test itself supplies cannot fail on a wiring gap. Every case here starts from the **shipped
    /// json** and ends at the value placement actually uses, so a name that stops binding fails here
    /// rather than on a live server.</para>
    /// </summary>
    public class SeparationBindingTests
    {
        private const string WorldDir = "instance/worlds/awl-factions-genesis/";

        private static WorldRecipe ShippedRecipe()
        {
            string error;
            WorldRecipe recipe = WorldRecipe.Parse(
                File.ReadAllText(TestSupport.RepoFile(WorldDir + "world_recipe.json")), out error);
            Assert.Null(error);
            Assert.NotNull(recipe);
            return recipe;
        }

        private static List<WorldFeature> ShippedFeatures()
        {
            string status;
            List<WorldFeature> features = WorldFeatureFile.Parse(
                File.ReadAllText(TestSupport.RepoFile(WorldDir + "world_features.json")), out status);
            Assert.NotEmpty(features);
            return features;
        }

        private static SurfaceNodePolicy ShippedPolicy()
        {
            SurfaceNodePolicy policy;
            string why;
            Assert.True(SurfaceNodePolicy.TryBuild(ShippedRecipe(), out policy, out why), why);
            return policy;
        }

        /// <summary>A body as the host would produce it: geometry from the live planet, every authored
        /// per-body rule stamped by <see cref="SurfaceNodePolicy.ApplyTo"/> — the same call
        /// <c>SePlanetCatalogue</c> makes, so this covers the hop rather than re-implementing it.</summary>
        private static PlanetRecord Body(SurfaceNodePolicy policy, string generator,
                                         double surfaceRadiusKm, double gravityRadiusKm)
        {
            var record = new PlanetRecord
            {
                Id = generator, Name = "Planet", Generator = generator,
                Center = new Vec3(0, 0, 0),
                SurfaceRadius = surfaceRadiusKm, GravityRadius = gravityRadiusKm,
                AtmosphereRadius = surfaceRadiusKm, HasAtmosphere = true,
            };
            policy.ApplyTo(record, generator, seFlag: true);
            return record;
        }

        private sealed class OneBodyCatalogue : IPlanetCatalogue
        {
            private readonly PlanetRecord _p;
            public OneBodyCatalogue(PlanetRecord p) { _p = p; }
            public IEnumerable<PlanetRecord> Planets { get { yield return _p; } }

            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_p.Center);
                if (d >= _p.GravityRadius) return 0;
                if (d <= _p.SurfaceRadius) return 1.0;
                return (_p.GravityRadius - d) / (_p.GravityRadius - _p.SurfaceRadius);
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_p.Center);
                if (d < 1e-6) return new Vec3(_p.SurfaceRadius, 0, 0);
                double k = _p.SurfaceRadius / d;
                return new Vec3(posKm.X * k, posKm.Y * k, posKm.Z * k);
            }

            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static FactionTuning Tuning()
        {
            return new FactionTuning
            {
                SurfaceGravityThreshold = 0.5,
                OrbitGravityMin = 0.05,
                NodeSeparationSurfaceKm = 15.0,
                NodeSeparationOrbitKm = 30.0,
                NodeSeparationDeepSpaceKm = 500.0,
            };
        }

        // ---- body: shipped recipe -> policy -> PlanetRecord -> SeparationAtKm -------------------

        [Theory]
        [InlineData("Kor", 10.0)]
        [InlineData("Agni", 10.0)]
        [InlineData("Tellus", 14.0)]
        public void AnAuthoredBodySeparation_ReachesSeparationAtKm(string generator, double expectedKm)
        {
            PlanetRecord body = Body(ShippedPolicy(), generator, surfaceRadiusKm: 30, gravityRadiusKm: 90);
            var w = new WorldModel(new SeededRandom(1))
            {
                Tuning = Tuning(), Planets = new OneBodyCatalogue(body),
            };

            Assert.Equal(expectedKm, w.SeparationAtKm(new Vec3(30, 0, 0)), 6);
        }

        [Fact]
        public void KorIsDenserThanTheGlobalFloor_WhichIsTheWholePointOfAuthoringIt()
        {
            // The brief puts eleven claimants on Kor; at the 15 km global floor it holds about four.
            // If this ever equals NodeSeparationSurfaceKm again, the authored value has stopped binding.
            PlanetRecord kor = Body(ShippedPolicy(), "Kor", surfaceRadiusKm: 30, gravityRadiusKm: 90);
            var w = new WorldModel(new SeededRandom(1))
            {
                Tuning = Tuning(), Planets = new OneBodyCatalogue(kor),
            };

            double korSurface = w.SeparationAtKm(new Vec3(30, 0, 0));
            Assert.True(korSurface < w.Tuning.NodeSeparationSurfaceKm,
                "Kor's authored separation is not binding: got " + korSurface
                + " km, which is the global floor rather than the recipe's value.");
        }

        [Fact]
        public void ApplyTo_LeavesAnUnauthoredBodyDeferringToTheGlobalFloor()
        {
            PlanetRecord other = Body(ShippedPolicy(), "NotInTheRecipe", surfaceRadiusKm: 30, gravityRadiusKm: 90);
            var w = new WorldModel(new SeededRandom(1))
            {
                Tuning = Tuning(), Planets = new OneBodyCatalogue(other),
            };

            Assert.Null(other.NodeSeparationKm);
            Assert.Equal(w.Tuning.NodeSeparationSurfaceKm, w.SeparationAtKm(new Vec3(30, 0, 0)), 6);
        }

        // ---- feature: shipped world_features.json -> WorldFeaturePlacement -> SeparationAtKm -----

        [Fact]
        public void TheRingsAuthoredSeparation_ReachesSeparationAtKm()
        {
            List<WorldFeature> features = ShippedFeatures();
            WorldFeature ring = features.Find(f => f.NodeSeparationKm.HasValue);
            Assert.True(ring != null, "no shipped feature authors NodeSeparationKm");

            var w = new WorldModel(new SeededRandom(1))
            {
                Tuning = Tuning(),
                Features = new WorldFeaturePlacement(features, null),
            };

            // Mid-ring, on the disc, relative to the feature's own resolved centre.
            double midRadius = (ring.InnerRadiusKm + ring.OuterRadiusKm) * 0.5;
            var inRing = new Vec3(ring.Center.X + midRadius, ring.Center.Y, ring.Center.Z);

            Assert.Equal(ring.NodeSeparationKm.Value, w.SeparationAtKm(inRing), 6);
        }

        [Fact]
        public void OutsideEveryAuthoredRegion_TheGlobalDeepSpaceFloorStillApplies()
        {
            // What makes a feature a region: open space is not quietly re-authored with it.
            var w = new WorldModel(new SeededRandom(1))
            {
                Tuning = Tuning(),
                Features = new WorldFeaturePlacement(ShippedFeatures(), null),
            };

            Assert.Equal(w.Tuning.NodeSeparationDeepSpaceKm,
                         w.SeparationAtKm(new Vec3(9_000_000, 0, 0)), 6);
        }
    }
}
