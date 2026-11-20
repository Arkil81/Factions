using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The orbital layout and the Real Gas Giants coupling, checked against the live AWL Cauldron
    /// system rather than against invented numbers.
    /// </summary>
    public class WorldLayoutTests
    {
        private static PlanetTypeSpec Type(string name, double hill, double gravity, bool surface)
        {
            return new PlanetTypeSpec
            {
                DefinitionName = name,
                HillParamsMax = hill,
                SurfaceGravity = gravity,
                GravityFalloffPower = 7.0,
                AllowSurfaceEconomyStations = surface,
                AllowOrbitalEconomyStations = true
            };
        }

        private static IPlanetTypeCatalogue Catalogue()
        {
            return new StaticPlanetTypeCatalogue(new List<PlanetTypeSpec>
            {
                Type("RealGasGiant", 0.0, 1.0, false),
                Type("Tellus", 0.03, 1.0, true),
                Type("Agni", 0.03, 0.9, true),
                Type("Kor", 0.03, 0.25, true)
            });
        }

        /// <summary>Cauldron's own ring, as configured live: a 180 km virtual body, an authored
        /// 195-465 km annulus clearing the 191 km atmosphere, and GasGiantRingType_Default2's
        /// eighteen band fractions. The scales stay as the fallback the radii override.</summary>
        private static GasGiantSpec CauldronGiant()
        {
            var ring = new GasGiantRingSpec
            {
                Skin = "Default2",
                InnerScale = 1.0,
                OuterScale = 2.5,
                InnerRadiusKm = 195.0,
                OuterRadiusKm = 465.0,
                FeatureHalfThicknessKm = 1.8,
                Density = 0.75,
                Zone = NodeZone.Orbit,
                CollectResourceSubTypeId = "Ice",
                CollectResourceAmount = 100
            };
            ring.BandFractions.AddRange(new[]
            {
                0.0, 0.05, 0.07, 0.17, 0.19, 0.284, 0.317, 0.46, 0.54, 0.62,
                0.717, 0.761, 0.807, 0.859, 0.938, 0.943, 0.953, 0.959
            });
            ring.Materials = new List<FeatureMaterial> { new FeatureMaterial { SubTypeId = "Ice", Share = 1.0, BandDepthM = 12.0 } };

            var giant = new GasGiantSpec
            {
                RadiusKm = 180.0,
                GravityStrength = 2.0,
                GravityFalloff = 7.0,
                Skin = "Cauldron"
            };
            giant.Rings.Add(ring);
            return giant;
        }

        private static WorldRecipe CauldronSystem()
        {
            var recipe = new WorldRecipe
            {
                WorldSeed = 20260824,
                Volume = new WorldVolume { Shape = WorldVolumeShape.Sphere, ExtentKm = 30000 },
                Placement = new WorldPlacementRules { SeparationMarginKm = 100, MaxAttemptsPerPlanet = 500 },
                Layout = new WorldLayout
                {
                    CentralBody = "Cauldron",
                    PlaneNormal = new Vec3(0, 1, 0),
                    InclinationDeg = 6.0
                }
            };
            recipe.Layout.Bands.Add(new OrbitBand { Name = "inner", InnerRadiusKm = 600, OuterRadiusKm = 800 });
            recipe.Layout.Bands.Add(new OrbitBand { Name = "middle", InnerRadiusKm = 950, OuterRadiusKm = 1150 });
            recipe.Layout.Bands.Add(new OrbitBand { Name = "outer", InnerRadiusKm = 2900, OuterRadiusKm = 3300 });

            recipe.Planets.Add(new RecipePlanet
            {
                Name = "Cauldron", Definition = "RealGasGiant", DiameterKm = 19.0, GasGiant = CauldronGiant()
            });
            recipe.Planets.Add(new RecipePlanet { Name = "Agni", Definition = "Agni", DiameterKm = 30.0, Orbit = "inner" });
            recipe.Planets.Add(new RecipePlanet { Name = "Tellus", Definition = "Tellus", DiameterKm = 60.0, Orbit = "middle", AddGps = true });
            recipe.Planets.Add(new RecipePlanet { Name = "Kor", Definition = "Kor", DiameterKm = 19.0, Orbit = "outer" });
            return recipe;
        }

        // ---- the gas giant's own numbers ---------------------------------------------------------

        [Fact]
        public void GasGiantGravityComesFromTheModNotTheGeneratorDefinition()
        {
            // The live world reports 304.887 km. 180 x (2/0.05)^(1/7). Reading it off the generator
            // definition would size the well off a 9.5 km voxel core instead -- 25x too small.
            Assert.Equal(304.9, CauldronGiant().GravityLimitKm(), 1);

            WorldPlan plan = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            PlannedPlanet giant = plan.Planets.Find(p => p.Name == "Cauldron");
            Assert.Equal(304.9, giant.GravityLimitKm, 1);
            Assert.True(giant.GravityLimitKm > 25 * giant.TerrainRadiusKm);
        }

        [Fact]
        public void TheRingSetsTheExclusionRadius()
        {
            // The ring's authored 465 km outer edge, which reaches further than the 305 km gravity limit.
            WorldPlan plan = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            PlannedPlanet giant = plan.Planets.Find(p => p.Name == "Cauldron");
            Assert.Equal(465.0, giant.ExclusionRadiusKm, 6);
        }

        [Fact]
        public void TheOldAgniOrbitWouldNotClearTheRing()
        {
            // Live Cauldron has Agni at 488 km, inside the ring's 450 km plus Agni's own gravity and
            // the margin. This is the constraint catching a real defect in the world it reproduces.
            WorldRecipe recipe = CauldronSystem();
            recipe.Layout.Bands[0] = new OrbitBand { Name = "inner", InnerRadiusKm = 470, OuterRadiusKm = 500 };
            recipe.Placement.MaxAttemptsPerPlanet = 60;

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            PlannedPlanet agni = plan.Planets.Find(p => p.Name == "Agni");
            Assert.False(agni.Placed);
            Assert.Contains("Cauldron", agni.Rejection);
        }

        // ---- the ring feature is derived, not authored --------------------------------------------

        [Fact]
        public void EachRingBecomesAFeatureDerivedFromTheSameNumbers()
        {
            WorldPlan plan = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            PlannedFeature ring = plan.Features.Find(f => f.DerivedFromGasGiant);

            Assert.NotNull(ring);
            Assert.Equal("Cauldron", ring.AnchorPlanetName);
            Assert.Equal(WorldFeatureShape.Ring, ring.Shape);
            Assert.Equal(195.0, ring.InnerRadiusKm, 6);
            Assert.Equal(465.0, ring.OuterRadiusKm, 6);
            Assert.Equal(1.8, ring.HalfThicknessKm, 6);
            Assert.Equal(0.75, ring.Density, 6);
            Assert.Equal(NodeZone.Orbit, ring.Zone);
        }

        [Fact]
        public void RingBandsReproduceCauldronsNine()
        {
            // The skin's fractions are fractions of the ring's span: 195 + f x 270. These are the
            // exact nine bands the named world's world_features.json carries, measured off the live world.
            WorldPlan plan = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            PlannedFeature ring = plan.Features.Find(f => f.DerivedFromGasGiant);

            Assert.Equal(9, ring.Bands.Count);
            Assert.Equal(195.0, ring.Bands[0].InnerRadiusKm, 1);
            Assert.Equal(208.5, ring.Bands[0].OuterRadiusKm, 1);
            Assert.Equal(213.9, ring.Bands[1].InnerRadiusKm, 1);
            Assert.Equal(240.9, ring.Bands[1].OuterRadiusKm, 1);
            Assert.Equal(452.3, ring.Bands[8].InnerRadiusKm, 1);
            Assert.Equal(453.9, ring.Bands[8].OuterRadiusKm, 1);
        }

        [Fact]
        public void MovingTheGiantMovesItsRingWithIt()
        {
            // The coupling that makes a derived feature worth having: change the body, and the ring
            // cannot be left describing the old one.
            WorldRecipe recipe = CauldronSystem();
            // Only the scale path follows the body; authored radii are the author overruling it.
            recipe.Planets[0].GasGiant.Rings[0].InnerRadiusKm = null;
            recipe.Planets[0].GasGiant.Rings[0].OuterRadiusKm = null;
            recipe.Planets[0].GasGiant.RadiusKm = 200.0;

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            PlannedFeature ring = plan.Features.Find(f => f.DerivedFromGasGiant);
            PlannedPlanet giant = plan.Planets.Find(p => p.Name == "Cauldron");

            Assert.Equal(200.0, ring.InnerRadiusKm, 6);
            Assert.Equal(500.0, ring.OuterRadiusKm, 6);
            Assert.Equal(500.0, giant.ExclusionRadiusKm, 6);
        }

        [Fact]
        public void ASolidRingHasNoBands()
        {
            WorldRecipe recipe = CauldronSystem();
            recipe.Planets[0].GasGiant.Rings[0].BandFractions.Clear();
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.Empty(plan.Features.Find(f => f.DerivedFromGasGiant).Bands);
        }

        // ---- what Real Gas Giants is handed --------------------------------------------------------

        /// <summary>Real Gas Giants' own ring arithmetic, replayed from <c>RingManager.Init</c>:
        /// <c>radiusRingInner = radiusPlanet * RingInnerScale</c> and then
        /// <c>radiusRingOuter = radiusRingInner * RingOuterScale</c>. The outer edge is a multiple of
        /// the INNER radius, not of the body. Here so the scales this recipe emits are checked against
        /// the consumer rather than against the derivation that produced them.</summary>
        private static void RggRingEdges(GasGiantSpec giant, out double innerKm, out double outerKm)
        {
            GasGiantRingSpec ring = giant.Rings[0];
            innerKm = giant.RadiusKm * ring.RggInnerScale(giant.RadiusKm);
            outerKm = innerKm * ring.RggOuterScale(giant.RadiusKm);
        }

        [Fact]
        public void TheScalesHandedToRealGasGiantsRedrawTheAuthoredAnnulus()
        {
            double innerKm, outerKm;
            RggRingEdges(CauldronGiant(), out innerKm, out outerKm);

            Assert.Equal(195.0, innerKm, 6);
            Assert.Equal(465.0, outerKm, 6);
        }

        [Fact]
        public void AnUnmovedRingIsWhereTheTwoConventionsAgree()
        {
            // An inner scale of exactly 1 is the single case where a body-relative outer scale and
            // RGG's inner-relative one land on the same radius. A ring authored at 180-450 therefore
            // says nothing about which convention the emitted scale is in.
            GasGiantSpec giant = CauldronGiant();
            giant.Rings[0].InnerRadiusKm = 180.0;
            giant.Rings[0].OuterRadiusKm = 450.0;

            double innerKm, outerKm;
            RggRingEdges(giant, out innerKm, out outerKm);

            Assert.Equal(180.0, innerKm, 6);
            Assert.Equal(450.0, outerKm, 6);
            Assert.Equal(2.5, giant.Rings[0].RggOuterScale(giant.RadiusKm), 6);
        }

        [Fact]
        public void TheDrawnBandsSitOnTheMinedOnes()
        {
            // RGG lays the skin's fraction pairs over its own span, and the feature lays the same
            // pairs over the feature's. A drawn ring wider than the modelled one puts rock in a
            // visible gap and leaves a drawn band empty, by a margin that grows outward.
            GasGiantSpec giant = CauldronGiant();
            double innerKm, outerKm;
            RggRingEdges(giant, out innerKm, out outerKm);

            WorldPlan plan = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            PlannedFeature ring = plan.Features.Find(f => f.DerivedFromGasGiant);
            List<double> fractions = giant.Rings[0].BandFractions;

            Assert.Equal(fractions.Count / 2, ring.Bands.Count);
            for (int i = 0; i < ring.Bands.Count; i++)
            {
                double drawnInner = innerKm + fractions[i * 2] * (outerKm - innerKm);
                double drawnOuter = innerKm + fractions[i * 2 + 1] * (outerKm - innerKm);
                Assert.Equal(drawnInner, ring.Bands[i].InnerRadiusKm, 6);
                Assert.Equal(drawnOuter, ring.Bands[i].OuterRadiusKm, 6);
            }
        }

        // ---- orbital placement --------------------------------------------------------------------

        [Fact]
        public void TheCentralBodySitsAtTheSystemCentre()
        {
            WorldPlan plan = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            PlannedPlanet giant = plan.Planets.Find(p => p.Name == "Cauldron");
            Assert.True(giant.Placed);
            Assert.Equal(0.0, giant.CenterKm.X, 9);
            Assert.Equal(0.0, giant.CenterKm.Y, 9);
            Assert.Equal(0.0, giant.CenterKm.Z, 9);
        }

        [Fact]
        public void EveryBodyLandsOnTheBandItNamed()
        {
            WorldPlan plan = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            Assert.True(plan.IsApplicable);

            AssertOnBand(plan, "Agni", "inner", 600, 800);
            AssertOnBand(plan, "Tellus", "middle", 950, 1150);
            AssertOnBand(plan, "Kor", "outer", 2900, 3300);
        }

        private static void AssertOnBand(WorldPlan plan, string name, string band, double inner, double outer)
        {
            PlannedPlanet p = plan.Planets.Find(x => x.Name == name);
            Assert.True(p.Placed, name + " was not placed: " + p.Rejection);
            Assert.Equal(band, p.OrbitBandName);
            Assert.InRange(p.CenterKm.DistanceTo(new Vec3(0, 0, 0)), inner, outer);
        }

        [Fact]
        public void BodiesStayWithinTheAuthoredInclination()
        {
            WorldPlan plan = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            foreach (PlannedPlanet p in plan.Planets)
            {
                if (!p.Placed || p.Name == "Cauldron") continue;
                double r = p.CenterKm.DistanceTo(new Vec3(0, 0, 0));
                double inclination = Math.Asin(Math.Abs(p.CenterKm.Y) / r) * 180.0 / Math.PI;
                Assert.InRange(inclination, 0.0, 6.0001);
            }
        }

        [Fact]
        public void AFlatSystemPutsEverythingOnThePlane()
        {
            WorldRecipe recipe = CauldronSystem();
            recipe.Layout.InclinationDeg = 0.0;
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());

            foreach (PlannedPlanet p in plan.Planets)
                if (p.Placed) Assert.Equal(0.0, p.CenterKm.Y, 6);
        }

        [Fact]
        public void TheSystemPlaneFollowsItsNormal()
        {
            WorldRecipe recipe = CauldronSystem();
            recipe.Layout.PlaneNormal = new Vec3(1, 0, 0);
            recipe.Layout.InclinationDeg = 0.0;
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());

            // Normal +X means the orbital plane is YZ, so X is the axis that stays flat.
            foreach (PlannedPlanet p in plan.Planets)
                if (p.Placed) Assert.Equal(0.0, p.CenterKm.X, 6);
        }

        [Fact]
        public void UnpinnedBodiesTakeBandsInTurnRatherThanPilingUp()
        {
            // A random draw puts two bodies in one orbit and leaves another empty; a system does not
            // look like that.
            WorldRecipe recipe = CauldronSystem();
            foreach (RecipePlanet rp in recipe.Planets) rp.Orbit = null;

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            var used = new List<string>();
            foreach (PlannedPlanet p in plan.Planets)
                if (p.Placed && p.OrbitBandName != null) used.Add(p.OrbitBandName);

            Assert.Equal(3, used.Count);
            Assert.Equal(3, new HashSet<string>(used).Count);
        }

        [Fact]
        public void AnUnknownBandNamesItself()
        {
            WorldRecipe recipe = CauldronSystem();
            recipe.Planets[1].Orbit = "nowhere";
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());

            PlannedPlanet agni = plan.Planets.Find(p => p.Name == "Agni");
            Assert.False(agni.Placed);
            Assert.Contains("nowhere", agni.Rejection);
        }

        [Fact]
        public void AnUnknownCentralBodyIsAPlanTimeError()
        {
            WorldRecipe recipe = CauldronSystem();
            recipe.Layout.CentralBody = "Sol";
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.Contains(plan.Errors, e => e.Contains("Layout.CentralBody"));
        }

        [Fact]
        public void LayoutIsDeterministic()
        {
            WorldPlan a = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            WorldPlan b = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            for (int i = 0; i < a.Planets.Count; i++)
            {
                Assert.Equal(a.Planets[i].CenterKm.X, b.Planets[i].CenterKm.X, 9);
                Assert.Equal(a.Planets[i].CenterKm.Y, b.Planets[i].CenterKm.Y, 9);
                Assert.Equal(a.Planets[i].CenterKm.Z, b.Planets[i].CenterKm.Z, 9);
            }
        }

        [Fact]
        public void AnAuthoredCentreStillWinsOverTheLayout()
        {
            WorldRecipe recipe = CauldronSystem();
            recipe.Planets[1].CenterKm = new Vec3(700, 0, 0);
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());

            PlannedPlanet agni = plan.Planets.Find(p => p.Name == "Agni");
            Assert.Equal(700.0, agni.CenterKm.X, 6);
            Assert.True(agni.CenterWasAuthored);
        }

        [Fact]
        public void NoLayoutStillScattersInTheVolume()
        {
            WorldRecipe recipe = CauldronSystem();
            recipe.Layout = null;
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());

            Assert.True(plan.IsApplicable);
            Assert.Contains(plan.Notes, n => n.Contains("no Layout"));
        }

        [Fact]
        public void GpsIsOffUnlessAuthored()
        {
            WorldPlan plan = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            Assert.True(plan.Planets.Find(p => p.Name == "Tellus").AddGps);
            Assert.False(plan.Planets.Find(p => p.Name == "Agni").AddGps);
            Assert.False(plan.Planets.Find(p => p.Name == "Cauldron").AddGps);
        }

        [Fact]
        public void AnOversizedGasGiantBodyIsRefusedBecauseTheModWouldReplaceIt()
        {
            // RGG closes any planet on its generator whose MaximumRadius exceeds 10 km and respawns
            // its own 19 km body elsewhere. Silently deleted and moved, so this has to fail at plan
            // time rather than be discovered in the world.
            WorldRecipe recipe = CauldronSystem();
            recipe.Planets[0].DiameterKm = 120.0;

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.False(plan.IsApplicable);
            Assert.Contains(plan.Errors, e => e.Contains("Real Gas Giants closes anything larger"));
        }

        [Fact]
        public void CauldronsOwnNineteenKilometreBodyIsUnderTheLimit()
        {
            WorldPlan plan = WorldPlanner.Plan(CauldronSystem(), Catalogue());
            Assert.Empty(plan.Errors);
            Assert.True(plan.Planets.Find(p => p.Name == "Cauldron").TerrainRadiusKm <= 10.0);
        }

        [Fact]
        public void ABadGasGiantRefusesWithAReason()
        {
            WorldRecipe recipe = CauldronSystem();
            recipe.Planets[0].GasGiant.Rings[0].InnerRadiusKm = null;
            recipe.Planets[0].GasGiant.Rings[0].OuterRadiusKm = null;
            recipe.Planets[0].GasGiant.Rings[0].OuterScale = 0.5;   // inside the inner scale
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.Contains(plan.Errors, e => e.Contains("outer edge"));
        }

        [Fact]
        public void AnAuthoredRadiusRefusesWhenItInvertsTheRing()
        {
            WorldRecipe recipe = CauldronSystem();
            recipe.Planets[0].GasGiant.Rings[0].OuterRadiusKm = 100.0;   // inside the authored inner
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.Contains(plan.Errors, e => e.Contains("outer edge"));
        }

        [Fact]
        public void BandOreMustMatchTheFractionPairs()
        {
            // Positional, so a miscount has to be an error: silently shifting every band's ore by
            // one would move Platinum off the inner edge and nothing would say so.
            WorldRecipe recipe = CauldronSystem();
            recipe.Planets[0].GasGiant.Rings[0].BandOre = new List<RingBandOre> { new RingBandOre() };
            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            Assert.Contains(plan.Errors, e => e.Contains("BandOre"));
        }

        [Fact]
        public void AuthoredBandOreLandsOnTheMatchingBand()
        {
            WorldRecipe recipe = CauldronSystem();
            var ore = new List<RingBandOre>();
            for (int i = 0; i < 9; i++) ore.Add(new RingBandOre());
            ore[0].Materials = new List<FeatureMaterial> { new FeatureMaterial { SubTypeId = "Platinum", Share = 1.0 } };
            ore[8].Density = 0.25;
            recipe.Planets[0].GasGiant.Rings[0].BandOre = ore;

            WorldPlan plan = WorldPlanner.Plan(recipe, Catalogue());
            PlannedFeature ring = plan.Features.Find(f => f.DerivedFromGasGiant);

            Assert.Equal("Platinum", ring.Bands[0].Materials[0].SubTypeId);
            Assert.Null(ring.Bands[4].Materials);          // inherits the ring's Ice
            Assert.Equal(0.25, ring.Bands[8].Density.Value, 6);
        }
    }
}
