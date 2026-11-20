using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Placement consults the geography it already has. Before this, <see cref="WorldModel.ExpandFrom"/>
    /// scored candidates by distance from the nearest existing node alone — so the roomiest answer and
    /// the one that can never hold ore were, in open space, the same answer — and only learned what was
    /// under the chosen point afterwards, in <c>CommitNode</c>'s <c>ResolveDeposit</c>.
    /// </summary>
    public class ExpandProspectTests
    {
        /// <summary>Ore on one side of the x=0 plane and nothing on the other, so a committed position
        /// says which side placement chose. Sampling agrees with the prospect by construction: a stub
        /// whose two answers could disagree would let a test pass on a contradiction.</summary>
        private sealed class HalfSpaceOre : IOreGeography
        {
            public int ProspectCalls;
            public int SampleCalls;

            private static bool Bearing(Vec3 p) { return p.X > 0; }

            public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
            {
                SampleCalls++;
                if (!Bearing(posKm)) { sample = new OreSample { PointsExamined = 1000 }; return true; }
                var s = new OreSample { PointsExamined = 1000 };
                s.Ores.Add(new OreObservation("Iron", 60, 10.0, 5.0));
                sample = s;
                return true;
            }

            public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
            {
                if (!TrySample(posKm, radiusKm, out sample)) return false;
                sample.PointsExamined = count;
                return true;
            }

            public OreProspect CouldBearOre(Vec3 posKm)
            {
                ProspectCalls++;
                return Bearing(posKm) ? OreProspect.Possible : OreProspect.None;
            }

            public IEnumerable<string> MintableOres { get { yield return "Iron"; } }
        }

        /// <summary>Nothing anywhere, and it says so decisively rather than declining — the barren-world
        /// case, distinct from a seam with no answer.</summary>
        private sealed class NothingAnywhere : IOreGeography
        {
            public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
            {
                sample = new OreSample { PointsExamined = 1000 };
                return true;
            }

            public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
            {
                return TrySample(posKm, radiusKm, out sample);
            }

            public OreProspect CouldBearOre(Vec3 posKm) { return OreProspect.None; }

            public IEnumerable<string> MintableOres { get { yield break; } }
        }

        private static WorldModel World(IOreGeography ores, bool prefer)
        {
            FactionTuning t = TestSupport.Tuning();
            t.ExpandPrefersGroundThatCouldBearOre = prefer;
            var w = new WorldModel(new SeededRandom(1234)) { Tuning = t };
            if (ores != null) w.Ores = ores;
            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "P001");
            parent.OwnerFactionTag = "F";
            return w;
        }

        [Fact]
        public void Expand_PrefersGroundThatCouldBearOre_OverTheRoomierSide()
        {
            // A fresh world per trial, and one expansion each. Expanding repeatedly from the same
            // parent would measure something else: the ore hemisphere fills with nodes, separation
            // starts rejecting its candidates, and the barren side wins on legality rather than on
            // ranking. That saturation is correct behaviour and it is not what this pins.
            int bearing = 0, barren = 0, asked = 0;
            for (int seed = 1; seed <= 40; seed++)
            {
                var ores = new HalfSpaceOre();
                FactionTuning t = TestSupport.Tuning();
                t.ExpandPrefersGroundThatCouldBearOre = true;
                var w = new WorldModel(new SeededRandom(seed)) { Tuning = t, Ores = ores };
                SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "P001");
                parent.OwnerFactionTag = "F";

                SpatialNode child = w.ExpandFrom("F", "P001");
                if (child.Position.X > 0) bearing++; else barren++;
                asked += ores.ProspectCalls;
            }

            Assert.True(asked > 0, "placement never asked the geography");
            // Not "every one": a sphere sample can land wholly on the barren side, and forcing that
            // never to happen would be a test of the RNG rather than of the ranking.
            Assert.True(bearing > barren * 3,
                "expansion should land on ore-bearing ground far more often than not: "
                + bearing + " bearing vs " + barren + " barren");
        }

        [Fact]
        public void Expand_WithTheFlagOff_IgnoresTheGeographyEntirely()
        {
            var ores = new HalfSpaceOre();
            WorldModel w = World(ores, prefer: false);

            for (int i = 0; i < 20; i++) w.ExpandFrom("F", "P001");

            Assert.Equal(0, ores.ProspectCalls);
        }

        /// <summary>The regression the ranking is shaped to avoid: when no candidate could bear ore,
        /// roominess must still decide outright. A blended score would collapse every candidate to the
        /// same value here and hands the first legal one the win — which is the "first past the
        /// threshold" placement a previous fix removed.</summary>
        [Fact]
        public void Expand_WhenNothingCouldBearOre_StillPlacesByRoominess()
        {
            WorldModel gated = World(new NothingAnywhere(), prefer: true);
            WorldModel control = World(new NothingAnywhere(), prefer: false);

            // Same seed, same geography, so the two worlds saturate on the same iteration; a refusal on
            // one side must be a refusal on the other, and every placement must be the same point.
            int placed = 0;
            for (int i = 0; i < 15; i++)
            {
                SpatialNode a = gated.ExpandFrom("F", "P001");
                SpatialNode b = control.ExpandFrom("F", "P001");
                Assert.Equal(b == null, a == null);
                if (a == null) continue;
                placed++;
                Assert.Equal(b.Position.X, a.Position.X, 6);
                Assert.Equal(b.Position.Y, a.Position.Y, 6);
                Assert.Equal(b.Position.Z, a.Position.Z, 6);
            }
            Assert.True(placed > 0, "every expansion was refused, so nothing was compared");
        }

        [Fact]
        public void CouldBearOreAt_IsHonestRegardlessOfTheFlag()
        {
            // The predicate is public because placement asks it elsewhere too (FAC-106). A flag inside
            // it would hand those callers "false everywhere" dressed as an answer.
            WorldModel off = World(new HalfSpaceOre(), prefer: false);
            Assert.True(off.CouldBearOreAt(new Vec3(10, 0, 0)));
            Assert.False(off.CouldBearOreAt(new Vec3(-10, 0, 0)));
        }

        [Fact]
        public void CompositeProspect_FeatureEmptinessShadowsTheBodyUnderIt()
        {
            // The precedence TrySample uses: an author who says "this band is empty" is not overruled
            // by the planet beneath. Anything else and the ring's own emptiness would read as the gas
            // giant's ore.
            var features = new WorldFeatureGeography(new[]
            {
                new WorldFeature
                {
                    Name = "ring",
                    Center = new Vec3(0, 0, 0),
                    InnerRadiusKm = 0,
                    OuterRadiusKm = 100,
                    Materials = new List<FeatureMaterial>(),
                }
            }, null);

            var composite = new CompositeOreGeography(features, new AlwaysBearing());

            Assert.Equal(OreProspect.None, composite.CouldBearOre(new Vec3(10, 0, 0)));
            Assert.Equal(OreProspect.Possible, composite.CouldBearOre(new Vec3(5000, 0, 0)));
        }

        private sealed class AlwaysBearing : IOreGeography
        {
            public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
            {
                var s = new OreSample { PointsExamined = 1000 };
                s.Ores.Add(new OreObservation("Iron", 60, 10.0, 5.0));
                sample = s;
                return true;
            }

            public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
            {
                return TrySample(posKm, radiusKm, out sample);
            }

            public OreProspect CouldBearOre(Vec3 posKm) { return OreProspect.Possible; }

            public IEnumerable<string> MintableOres { get { yield return "Iron"; } }
        }
    }
}
