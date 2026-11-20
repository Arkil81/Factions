using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Which regions a space respawn may land in is authored per feature
    /// (<see cref="WorldFeature.SpaceRespawn"/>), not inferred from shape or owner.</summary>
    public class SpaceRespawnRegionTests
    {
        // A ring that admits respawns, and a nest beside it that does not. Shapes follow the genesis
        // world; the coordinates are fixture ones.
        private static WorldFeature Ring(bool spaceRespawn = true)
        {
            return new WorldFeature
            {
                Name = "Ring",
                Shape = WorldFeatureShape.Ring,
                Center = new Vec3(0, 0, 0),
                Normal = new Vec3(0, 1, 0),
                InnerRadiusKm = 195.0,
                OuterRadiusKm = 465.0,
                HalfThicknessKm = 1.8,
                SpaceRespawn = spaceRespawn
            };
        }

        private static WorldFeature Nest()
        {
            return new WorldFeature
            {
                Name = "Nest",
                Shape = WorldFeatureShape.Sphere,
                Center = new Vec3(2000, 0, 0),
                OuterRadiusKm = 750.0
            };
        }

        private static WorldFeaturePlacement Placement(params WorldFeature[] features)
        {
            return new WorldFeaturePlacement(features, null);
        }

        [Fact]
        public void AWorldWithNoFlaggedFeature_IsUnrestricted()
        {
            WorldFeaturePlacement p = Placement(Ring(spaceRespawn: false), Nest());
            Vec3 point;

            Assert.False(p.HasSpaceRespawnRegion);
            Assert.False(p.TryNearestSpaceRespawnPoint(new Vec3(5000, 0, 0), out point));
            Assert.False(WorldFeaturePlacement.Empty.HasSpaceRespawnRegion);
        }

        [Fact]
        public void ANodeInTheRingAdmitsARespawn_ANodeInTheNestDoesNot()
        {
            WorldFeaturePlacement p = Placement(Ring(), Nest());

            Assert.True(p.HasSpaceRespawnRegion);
            Assert.True(p.AllowsSpaceRespawnAt(new Vec3(300, 0, 0)));
            Assert.False(p.AllowsSpaceRespawnAt(new Vec3(2300, 0, 0)), "inside the nest");
            Assert.False(p.AllowsSpaceRespawnAt(new Vec3(0, 0, 5000)), "open space, no feature");
        }

        [Fact]
        public void Containment_RefusesOpenSpaceOffTheRingsPlane_WhichTheClaimWouldAccept()
        {
            // 50 km above the ring at a ring radius: inside the claim ball, outside the slab. A node
            // there would be placed into the ring; a pod there would be floating in nothing.
            WorldFeaturePlacement p = Placement(Ring(), Nest());
            var aboveThePlane = new Vec3(300, 50, 0);

            Assert.True(p.AllowsSpaceRespawnAt(aboveThePlane));
            Assert.False(p.AllowsSpaceRespawnContaining(aboveThePlane));
            Assert.True(p.AllowsSpaceRespawnContaining(new Vec3(300, 1.0, 0)));
        }

        [Fact]
        public void TheNearestRespawnPoint_FromInsideTheNest_IsInTheRing()
        {
            WorldFeaturePlacement p = Placement(Ring(), Nest());
            Vec3 point;

            Assert.True(p.TryNearestSpaceRespawnPoint(new Vec3(1500, 200, 0), out point));
            Assert.True(p.AllowsSpaceRespawnContaining(point));
            Assert.Equal(465.0 - WorldFeature.ContainmentInsetKm, point.X, 6);   // just inside the face
            Assert.Equal(1.8 - WorldFeature.ContainmentInsetKm, point.Y, 6);
        }

        [Fact]
        public void APointAlreadyInTheRing_IsItsOwnNearestRespawnPoint()
        {
            WorldFeaturePlacement p = Placement(Ring(), Nest());
            var inRing = new Vec3(0, 0.5, 250);
            Vec3 point;

            Assert.True(p.TryNearestSpaceRespawnPoint(inRing, out point));
            Assert.Equal(0.0, inRing.DistanceTo(point), 9);
        }

        [Fact]
        public void TheFlagSurvivesTheFeatureFile_AndIsOmittedWhenFalse()
        {
            string json = "{ \"Features\": [ "
                + "{ \"Name\": \"Ring\", \"Shape\": 1, \"InnerRadiusKm\": 195.0, \"OuterRadiusKm\": 465.0, \"HalfThicknessKm\": 1.8, \"SpaceRespawn\": true }, "
                + "{ \"Name\": \"Nest\", \"Shape\": 0, \"OuterRadiusKm\": 750.0 } ] }";
            string status;
            List<WorldFeature> features = WorldFeatureFile.Parse(json, out status);

            Assert.True(features.Find(f => f.Name == "Ring").SpaceRespawn);
            Assert.False(features.Find(f => f.Name == "Nest").SpaceRespawn);
            Assert.DoesNotContain("SpaceRespawn", Newtonsoft.Json.JsonConvert.SerializeObject(Nest()),
                System.StringComparison.Ordinal);
        }
    }
}
