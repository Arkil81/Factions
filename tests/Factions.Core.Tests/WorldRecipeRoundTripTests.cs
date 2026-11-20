using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The property the recipe exists to have: planning it reproduces the world it describes.
    ///
    /// <para>Checked against the two committed artifacts rather than against numbers repeated here,
    /// because the failure this guards is precisely the two drifting apart. The live world ran for a
    /// day on a ring the recipe would have rebuilt 15 km inward, at twice the thickness, in the
    /// wrong zone, at density zero and made only of Ice — and every test still passed, because they
    /// all asserted the derivation against itself. FAC-099.</para>
    /// </summary>
    public class WorldRecipeRoundTripTests
    {
        private static IPlanetTypeCatalogue Catalogue()
        {
            return new StaticPlanetTypeCatalogue(new List<PlanetTypeSpec>
            {
                Spec("RealGasGiant", false), Spec("Tellus", true), Spec("Agni", true), Spec("Kor", true)
            });
        }

        private static PlanetTypeSpec Spec(string name, bool surface)
        {
            return new PlanetTypeSpec
            {
                DefinitionName = name,
                HillParamsMax = name == "RealGasGiant" ? 0.0 : 0.03,
                SurfaceGravity = 1.0,
                GravityFalloffPower = 7.0,
                AllowSurfaceEconomyStations = surface,
                AllowOrbitalEconomyStations = true
            };
        }

        private static PlannedFeature PlannedRing()
        {
            string error;
            WorldRecipe recipe = WorldRecipe.Parse(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")), out error);
            Assert.Null(error);
            Assert.NotNull(recipe);

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.Empty(plan.Errors);
            return plan.Features.Find(f => f.Name == "Cauldron Ring");
        }

        private static WorldFeature AuthoredRing()
        {
            string status;
            List<WorldFeature> features = WorldFeatureFile.Parse(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_features.json")), out status);
            return features.Find(f => f.Name == "Cauldron Ring");
        }

        private static void AssertSameMaterials(List<FeatureMaterial> expected, List<FeatureMaterial> actual, string where)
        {
            if (expected == null) { Assert.True(actual == null, where + ": expected inheritance, got materials"); return; }
            Assert.True(actual != null, where + ": expected materials, got inheritance");
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                FeatureMaterial e = expected[i], a = actual[i];
                Assert.Equal(e.SubTypeId, a.SubTypeId);
                Assert.Equal(e.Share, a.Share, 6);
                Assert.Equal(e.BandDepthM, a.BandDepthM, 6);
                Assert.Equal(e.OverburdenM, a.OverburdenM, 6);
                Assert.Equal(e.ResolvedSizeRange().MinM, a.ResolvedSizeRange().MinM, 6);
                Assert.Equal(e.ResolvedSizeRange().MaxM, a.ResolvedSizeRange().MaxM, 6);
                Assert.Equal(e.DepositCountCoefficient, a.DepositCountCoefficient);
                Assert.Equal(e.DepositSizeDenominator, a.DepositSizeDenominator);
            }
        }

        [Fact]
        public void OnlyTheRingAdmitsASpaceRespawn_NoNestDoes()
        {
            string error;
            WorldRecipe recipe = WorldRecipe.Parse(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")), out error);
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());

            List<string> admitting = plan.Features.FindAll(f => f.SpaceRespawn).ConvertAll(f => f.Name);
            Assert.Equal(new List<string> { "Cauldron Ring" }, admitting);
            Assert.Contains(plan.Features, f => f.Name.Contains("Nest"));
        }

        [Fact]
        public void PlanningTheRecipeReproducesTheAuthoredRing()
        {
            PlannedFeature planned = PlannedRing();
            WorldFeature authored = AuthoredRing();

            Assert.NotNull(planned);
            Assert.NotNull(authored);
            Assert.Equal(WorldFeatureShape.Ring, planned.Shape);
            Assert.Equal(authored.InnerRadiusKm, planned.InnerRadiusKm, 6);
            Assert.Equal(authored.OuterRadiusKm, planned.OuterRadiusKm, 6);
            Assert.Equal(authored.HalfThicknessKm, planned.HalfThicknessKm, 6);
            Assert.Equal(authored.Density, planned.Density, 6);
            Assert.Equal(authored.Zone, planned.Zone);
            Assert.Equal(authored.NodeSeparationKm, planned.NodeSeparationKm);
            Assert.True(planned.SpaceRespawn, "the ring is where a space respawn lands");
            Assert.Equal(authored.SpaceRespawn, planned.SpaceRespawn);
            AssertSameMaterials(authored.Materials, planned.Materials, "ring");
        }

        [Fact]
        public void EveryAuthoredBandIsReproducedInPlaceAndInSubstance()
        {
            PlannedFeature planned = PlannedRing();
            WorldFeature authored = AuthoredRing();

            Assert.Equal(authored.Bands.Count, planned.Bands.Count);
            for (int i = 0; i < authored.Bands.Count; i++)
            {
                FeatureBand e = authored.Bands[i], a = planned.Bands[i];
                string where = "band " + (i + 1);
                Assert.Equal(e.InnerRadiusKm, a.InnerRadiusKm, 6);
                Assert.Equal(e.OuterRadiusKm, a.OuterRadiusKm, 6);
                Assert.Equal(e.Density, a.Density);
                AssertSameMaterials(e.Materials, a.Materials, where);
            }
        }

        [Fact]
        public void TheAuthoredRingClearsTheAtmosphereItWasMovedFor()
        {
            // The 15 km that a rebuild used to lose. Cauldron's atmosphere reaches 191 km; a ring
            // starting at 180 sits inside it.
            Assert.True(PlannedRing().InnerRadiusKm > 191.0);
        }

        [Fact]
        public void TheRingIsSettleableAcrossAllNineBands()
        {
            // FAC-105. Unauthored, the ring straddles Cauldron's 304.89 km gravity limit and takes two
            // different floors across one feature, with band 4 changing rule partway across its own
            // width -- 494 sites inside and 0.6 outside. An authored separation removes the cliff
            // rather than moving it, so the value has to be present, not merely plausible.
            PlannedFeature ring = PlannedRing();
            Assert.True(ring.NodeSeparationKm.HasValue, "the ring must author its own separation");
            Assert.Equal(30.0, ring.NodeSeparationKm.Value, 6);
            Assert.Equal(NodeZone.Orbit, ring.Zone);
        }

        [Fact]
        public void EveryLandableBodyAuthorsItsOwnSeparation()
        {
            // Kor holds FOUR nodes at the old 15 km floor against a brief that puts eleven claimants
            // on it, and over-subscription is silent: placement retries a bounded number of times and
            // then sites the node anyway. A missing value here is a separation violation later.
            string error;
            WorldRecipe recipe = WorldRecipe.Parse(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")),
                out error);
            Assert.Null(error);

            // Built through SurfaceNodePolicy -- the policy the LIVE path actually consults, via
            // SePlanetCatalogue -> PlanetRecord.NodeSeparationKm -> WorldModel.SeparationAtKm.
            //
            // This assertion previously ran against NodeSeparationPolicy, which read the authored
            // field and had zero call sites: it passed while every authored separation was inert,
            // because the recipe authored NodeSeparationKm and the live path read a differently
            // named field. Asserting against a policy nothing consults proves only that the file
            // parses. Point end-to-end checks at the consumed path, not at a parallel one.
            SurfaceNodePolicy policy;
            string why;
            Assert.True(SurfaceNodePolicy.TryBuild(recipe, out policy, out why), why);

            Assert.Equal(10.0, policy.SeparationKmFor("Kor").Value, 6);
            Assert.Equal(10.0, policy.SeparationKmFor("Agni").Value, 6);
            Assert.Equal(14.0, policy.SeparationKmFor("Tellus").Value, 6);

            // A body the recipe says nothing about defers to the zone floor, so this stays additive.
            Assert.False(policy.SeparationKmFor("EarthLike").HasValue);
        }

        [Fact]
        public void TheRingIsNotEmpty()
        {
            // Density absent deserialises to 0, and ObjectCount clamps that to nothing at all: the
            // quietest way for a rebuild to produce a ring with no rock in it.
            Assert.True(PlannedRing().Density > 0.0);
        }
    }
}
