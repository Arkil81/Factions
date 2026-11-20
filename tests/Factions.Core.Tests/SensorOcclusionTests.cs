using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Planets block sightlines. The geometry, what it changes in the detection test, and the two
    /// channels that are deliberately exempt from it.
    /// </summary>
    public class SensorOcclusionTests
    {
        private const double Lift = 2.0;   // FactionTuning.SensorHorizonLiftKm's shipped value

        private static PlanetRecord Body(double radiusKm, Vec3 centre, double? authoredKm = null)
        {
            return new PlanetRecord
            {
                Id = "P", Name = "Planet", Generator = "Tellus",
                Center = centre,
                SurfaceRadius = radiusKm,
                AtmosphereRadius = radiusKm * 1.2,
                GravityRadius = radiusKm * 4,
                SensorOcclusionRadiusKm = authoredKm,
            };
        }

        private static SensorOcclusion Field(double radiusKm, double liftKm = Lift)
        {
            return new SensorOcclusion(new[] { Body(radiusKm, new Vec3(0, 0, 0)) }, liftKm);
        }

        // ---- the geometry ------------------------------------------------------------------------

        [Fact]
        public void NoBodies_LeaveEveryLineClear()
        {
            Assert.Equal(0, SensorOcclusion.None.Count);
            Assert.True(SensorOcclusion.None.IsClear(new Vec3(0, 0, 0), new Vec3(9999, 0, 0)));
        }

        [Fact]
        public void ABodyBetweenTwoPointsBlocksThem_AndTheSamePairClearsWhenItIsNotBetween()
        {
            SensorOcclusion field = Field(30);

            // Opposite sides of the body, 100 km apart.
            Assert.False(field.IsClear(new Vec3(-50, 0, 0), new Vec3(50, 0, 0)));

            // The same separation, both on the near side: nothing in the way.
            Assert.True(field.IsClear(new Vec3(-50, 200, 0), new Vec3(50, 200, 0)));
        }

        /// <summary>The lift is what makes a surface estate able to see itself at all: two points
        /// exactly on a sphere have no sightline through it, at any separation. At 2 km the pair
        /// horizon on a 30 km body is 2·sqrt(2·30·2 + 2²) = 22.3 km.</summary>
        [Fact]
        public void TheLiftSetsThePairHorizon_AndZeroLiftWouldBlindASurfaceEstateEntirely()
        {
            const double r = 30.0;
            SensorOcclusion lifted = Field(r);
            double horizon = 2 * Math.Sqrt(2 * r * Lift + Lift * Lift);
            Assert.InRange(horizon, 22.0, 22.5);

            Assert.True(lifted.IsClear(OnSurface(r, -horizon * 0.45), OnSurface(r, horizon * 0.45)));
            Assert.False(lifted.IsClear(OnSurface(r, -horizon * 0.6), OnSurface(r, horizon * 0.6)));

            // Without it, two stations a kilometre apart on the same body cannot see each other.
            Assert.False(Field(r, liftKm: 0).IsClear(OnSurface(r, -0.5), OnSurface(r, 0.5)));
        }

        /// <summary>A point on the surface, <paramref name="alongKm"/> of arc from the pole, as a
        /// chord-accurate position rather than a point floating above the terrain.</summary>
        private static Vec3 OnSurface(double radiusKm, double alongKm)
        {
            double angle = alongKm / radiusKm;
            return new Vec3(radiusKm * Math.Sin(angle), radiusKm * Math.Cos(angle), 0);
        }

        [Fact]
        public void ASensorInsideAShellIsNotBlindedByIt()
        {
            // A 180 km virtual body with something sitting 100 km from its centre: it still sees out.
            SensorOcclusion field = new SensorOcclusion(
                new[] { Body(9.5, new Vec3(0, 0, 0), authoredKm: 180.0) }, Lift);
            Assert.True(field.IsClear(new Vec3(100, 0, 0), new Vec3(400, 0, 0)));
        }

        [Fact]
        public void AnAuthoredRadiusOccludesWhereTheTerrainWouldNot()
        {
            // A line that misses the voxel core by 50 km and crosses the giant well inside it.
            Vec3 a = new Vec3(-250, 50, 0), b = new Vec3(250, 50, 0);

            // Real Gas Giants' 9.5 km voxel core leaves a line across the system clear...
            Assert.True(new SensorOcclusion(new[] { Body(9.5, new Vec3(0, 0, 0)) }, Lift).IsClear(a, b));

            // ...while the body players actually fly around does not.
            Assert.False(new SensorOcclusion(
                new[] { Body(9.5, new Vec3(0, 0, 0), authoredKm: 180.0) }, Lift).IsClear(a, b));
        }

        [Fact]
        public void ASweptLegIsJudgedOnItsClosestApproach()
        {
            SensorOcclusion field = Field(30);
            var target = new Vec3(0, -200, 0);

            // The leg's nearest point to the target is on the far side of the body.
            Assert.False(field.IsClearFromSegment(new Vec3(-300, 200, 0), new Vec3(300, 200, 0), target));

            // The same leg on the target's own side of the body.
            Assert.True(field.IsClearFromSegment(new Vec3(-300, -200, 0), new Vec3(300, -200, 0), target));
        }

        // ---- what it changes in the detection test -----------------------------------------------

        [Fact]
        public void AnOccludedTargetInRangeIsNotDetected()
        {
            SensorOcclusion field = Field(30);
            var sensor = new Vec3(-50, 0, 0);
            var target = new Vec3(50, 0, 0);

            Assert.True(Sensing.Detects(sensor, 200, target, 1.0, 0.0, null));
            Assert.False(Sensing.Detects(sensor, 200, target, 1.0, 0.0, field));
        }

        /// <summary>SE draws a broadcasting grid on every receiver in radius with no line-of-sight
        /// test of its own, and we cannot suppress that channel. Occluding it would contradict what is
        /// already on the player's HUD rather than apply fog (FAC-140).</summary>
        [Fact]
        public void TheBroadcastFloorPenetratesABody_ButOnlyOutToItsOwnRadius()
        {
            SensorOcclusion field = Field(30);
            var sensor = new Vec3(-50, 0, 0);
            var near = new Vec3(50, 0, 0);     // 100 km out, inside a 150 km broadcast
            var far = new Vec3(200, 0, 0);     // 250 km out, beyond it, inside the sensor's 400 km

            Assert.True(Sensing.Detects(sensor, 400, near, 1.0, 150.0, field));
            Assert.False(Sensing.Detects(sensor, 400, far, 1.0, 150.0, field));
        }

        [Fact]
        public void ANullFieldLeavesTheOldAnswerUntouched()
        {
            var sensor = new Vec3(0, 0, 0);
            var target = new Vec3(90, 0, 0);
            Assert.Equal(Sensing.Detects(sensor, 100, target, 1.0),
                         Sensing.Detects(sensor, 100, target, 1.0, 0.0, null));
            Assert.Equal(Sensing.Detects(sensor, 50, target, 1.0),
                         Sensing.Detects(sensor, 50, target, 1.0, 0.0, null));
        }

        // ---- the sweep ---------------------------------------------------------------------------

        private static SpatialNode Station(string id, Vec3 pos, string owner)
        {
            return new SpatialNode { Id = id, Position = pos, OwnerFactionTag = owner, StableStationId = 1 };
        }

        [Fact]
        public void TheSweepDoesNotDiscoverWhatIsBehindABody()
        {
            FactionTuning t = TestSupport.Tuning();
            var now = DateTime.UtcNow;
            // Station-to-station reach is SensorNodePassiveRangeKm x a station's 2.0 signature = 50 km.
            SpatialNode home = Station("A", new Vec3(-20, 0, 0), "MERC");
            SpatialNode rival = Station("B", new Vec3(20, 0, 0), "RVN");
            var g = new SpatialGraph();
            g.Nodes[home.Id] = home;
            g.Nodes[rival.Id] = rival;
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);

            var seeing = new FactionState { Tag = "MERC" };
            KnowledgeSweep.SweepOwnedNodes(g, index, seeing, t, now, null);
            Assert.True(seeing.Knowledge.HasLive("B", now, t.DiscoveryHorizon));

            var blind = new FactionState { Tag = "MERC" };
            KnowledgeSweep.SweepOwnedNodes(g, index, blind, t, now, Field(15));
            Assert.False(blind.Knowledge.HasLive("B", now, t.DiscoveryHorizon));
        }

        [Fact]
        public void AFleetSweepDoesNotSurveyThroughABody()
        {
            FactionTuning t = TestSupport.Tuning();
            var now = DateTime.UtcNow;
            // A fleet reaches SensorFleetRangeKm (15 km) x a station's 2.0 signature = 30 km.
            SpatialNode rival = Station("B", new Vec3(0, -12, 0), "RVN");
            var g = new SpatialGraph();
            g.Nodes[rival.Id] = rival;
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);
            var from = new Vec3(-40, 15, 0);
            var to = new Vec3(40, 15, 0);

            var seeing = new FactionState { Tag = "MERC" };
            KnowledgeSweep.SweepFleetSegment(g, index, seeing, from, to, t, now);
            Assert.True(seeing.Knowledge.HasLive("B", now, t.DiscoveryHorizon));

            var blind = new FactionState { Tag = "MERC" };
            KnowledgeSweep.SweepFleetSegment(g, index, blind, from, to, t, now, null, null, Field(8));
            Assert.False(blind.Knowledge.HasLive("B", now, t.DiscoveryHorizon));
        }

        /// <summary>Ambient knowledge of your own neighbourhood is not a sensor reading, so no body
        /// stands in the way of it: a faction does not forget the node on the far side of its own
        /// planet. Stated as a test because the birthright path takes no occlusion field at all, and
        /// that is a decision rather than an omission.</summary>
        [Fact]
        public void TheBirthrightIsNotOccluded()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode home = Station("A", new Vec3(-20, 0, 0), "MERC");
            SpatialNode neighbour = Station("B", new Vec3(20, 0, 0), "RVN");
            home.Neighbors.Add(neighbour.Id);
            neighbour.Neighbors.Add(home.Id);
            var g = new SpatialGraph();
            g.Nodes[home.Id] = home;
            g.Nodes[neighbour.Id] = neighbour;

            Assert.True(KnowledgeView.IsDiscoveredBy(
                g, "MERC", new List<FactionKnowledge>(), "B", DateTime.UtcNow,
                t.DiscoveryHorizon, t.BirthrightRangeKm));
        }

        // ---- the authored radius, from the shipped recipe to the consumer -------------------------

        /// <summary>The shipped recipe's Cauldron entry reaches <see cref="SensorOcclusion.RadiusKmOf"/>
        /// through the same <see cref="SurfaceNodePolicy.ApplyTo"/> call <c>SePlanetCatalogue</c> makes.
        /// A per-body rule that binds by a name nothing reads ships inert and every unit test on both
        /// sides still passes — see <see cref="SeparationBindingTests"/>, which exists for that.</summary>
        [Fact]
        public void TheShippedGasGiantOccludesAtItsAuthoredRadius()
        {
            string error;
            WorldRecipe recipe = WorldRecipe.Parse(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")),
                out error);
            Assert.Null(error);

            SurfaceNodePolicy policy;
            string why;
            Assert.True(SurfaceNodePolicy.TryBuild(recipe, out policy, out why), why);

            // What SE reports for this body: the voxel core, not the giant.
            var record = new PlanetRecord
            {
                Id = "1", Name = "Planet", Generator = "RealGasGiant",
                Center = new Vec3(0, 0, 0), SurfaceRadius = 9.5, AtmosphereRadius = 191.3,
            };
            policy.ApplyTo(record, "RealGasGiant", seFlag: true);

            Assert.Equal(180.0, SensorOcclusion.RadiusKmOf(record));
        }

        /// <summary>The authored radius is reportable as well as consumable. The boot line
        /// <c>FactionsPlugin.LoadSurfaceNodePolicy</c> writes is built from these two members, and an
        /// authored value that no line reports is one a server can be missing with no symptom.</summary>
        [Fact]
        public void TheAuthoredRadiusIsReportable()
        {
            string error;
            WorldRecipe recipe = WorldRecipe.Parse(
                File.ReadAllText(TestSupport.RepoFile("instance/worlds/awl-factions-genesis/world_recipe.json")),
                out error);
            Assert.Null(error);

            SurfaceNodePolicy policy;
            string why;
            Assert.True(SurfaceNodePolicy.TryBuild(recipe, out policy, out why), why);

            Assert.Equal(1, policy.OcclusionCount);
            var reported = new List<string>();
            foreach (KeyValuePair<string, double> e in policy.OcclusionEntries)
                reported.Add(e.Key + "=" + e.Value.ToString("0.###") + " km");
            Assert.Equal(new[] { "RealGasGiant=180 km" }, reported);
        }

        [Fact]
        public void AnUnauthoredBodyOccludesAtItsTerrain()
        {
            Assert.Equal(30.0, SensorOcclusion.RadiusKmOf(Body(30, new Vec3(0, 0, 0))));
        }

        [Fact]
        public void TwoEntriesSharingAGeneratorMustAgreeOnTheRadius()
        {
            var recipe = new WorldRecipe
            {
                Planets = new List<RecipePlanet>
                {
                    new RecipePlanet { Name = "One", Definition = "RealGasGiant", SensorOcclusionRadiusKm = 180 },
                    new RecipePlanet { Name = "Two", Definition = "RealGasGiant", SensorOcclusionRadiusKm = 90 },
                }
            };
            SurfaceNodePolicy policy;
            string why;
            Assert.False(SurfaceNodePolicy.TryBuild(recipe, out policy, out why));
            Assert.Contains("SensorOcclusionRadiusKm", why);
        }

        [Fact]
        public void AnOcclusionRadiusNeedsAGeneratorAndAPositiveValue()
        {
            SurfaceNodePolicy policy;
            string why;

            Assert.False(SurfaceNodePolicy.TryBuild(new WorldRecipe
            {
                Planets = new List<RecipePlanet> { new RecipePlanet { Name = "One", SensorOcclusionRadiusKm = 180 } }
            }, out policy, out why));
            Assert.Contains("Definition", why);

            Assert.False(SurfaceNodePolicy.TryBuild(new WorldRecipe
            {
                Planets = new List<RecipePlanet>
                {
                    new RecipePlanet { Name = "One", Definition = "Tellus", SensorOcclusionRadiusKm = 0 }
                }
            }, out policy, out why));
            Assert.Contains("greater than 0", why);
        }
    }
}
