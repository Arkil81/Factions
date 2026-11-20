using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers <see cref="WorldModel.ComputeCampaignRegionMembers"/> — a Campaign focus
    /// region's BFS: center node plus owned nodes within 2x the per-zone node-separation radius,
    /// bounded by traverse depth.</summary>
    public class WorldModelCampaignRegionTests
    {
        /// <summary>Gravity 1.0 (Surface) within 1km of a fixed marker position; 0 (DeepSpace)
        /// everywhere else. Lets a test pin exactly one candidate node to the Surface zone while every
        /// other node stays DeepSpace (the default for <see cref="TestSupport.World"/>, which leaves
        /// <see cref="WorldModel.Planets"/> unset). Backs a real PlanetRecord centered at the marker so
        /// WorldModel.LocalSurfaceG (which samples a synthetic Center+SurfaceRadius point, not through
        /// GravityAt/NearestSurfacePoint directly) sees the same 1.0 peak this fake's own GravityAt
        /// override already returns there.</summary>
        private sealed class OneSurfaceSpotCatalogue : IPlanetCatalogue
        {
            public readonly Vec3 SurfaceMarker;
            private readonly PlanetRecord _planet;
            public OneSurfaceSpotCatalogue(Vec3 surfaceMarker)
            {
                SurfaceMarker = surfaceMarker;
                _planet = new PlanetRecord { Id = "P", Center = surfaceMarker, SurfaceRadius = 0.5, GravityRadius = 1.0 };
            }
            public IEnumerable<PlanetRecord> Planets { get { yield return _planet; } }
            public double GravityAt(Vec3 posKm) { return posKm.DistanceTo(SurfaceMarker) < 1.0 ? 1.0 : 0.0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static SpatialNode AddOwned(WorldModel w, string id, Vec3 pos, string tag = "F")
        {
            SpatialNode n = w.Graph.Add(pos, 0.5, id);
            n.OwnerFactionTag = tag;
            return n;
        }

        [Fact]
        public void Center_AlwaysIncluded_EvenWithZeroTraverseDepth()
        {
            var w = TestSupport.World();
            SpatialNode center = AddOwned(w, "N001", new Vec3(0, 0, 0));

            HashSet<string> members = w.ComputeCampaignRegionMembers("F", center.Id, 0);

            Assert.Single(members);
            Assert.Contains(center.Id, members);
        }

        [Fact]
        public void DeepSpaceNeighbor_WithinDoubleSeparationRadius_Included()
        {
            var w = TestSupport.World(); // Planets unset -> every node classifies DeepSpace (500km sep -> 1000km radius)
            SpatialNode center = AddOwned(w, "N001", new Vec3(0, 0, 0));
            SpatialNode near = AddOwned(w, "N002", new Vec3(1000, 0, 0)); // exactly at the 2x boundary

            HashSet<string> members = w.ComputeCampaignRegionMembers("F", center.Id, 2);

            Assert.Contains(near.Id, members);
        }

        [Fact]
        public void DeepSpaceNeighbor_JustPastDoubleSeparationRadius_Excluded()
        {
            var w = TestSupport.World();
            SpatialNode center = AddOwned(w, "N001", new Vec3(0, 0, 0));
            SpatialNode far = AddOwned(w, "N002", new Vec3(1000.5, 0, 0)); // just past the 2x boundary

            HashSet<string> members = w.ComputeCampaignRegionMembers("F", center.Id, 2);

            Assert.DoesNotContain(far.Id, members);
        }

        [Fact]
        public void TraverseDepth_BoundsHopCount_NotJustCumulativeRadius()
        {
            // Chain: center -1000km-> A -1000km-> B. B is only reachable from center via a second hop
            // through A (2000km from center directly, well past a single hop's radius either way).
            var w = TestSupport.World();
            SpatialNode center = AddOwned(w, "N001", new Vec3(0, 0, 0));
            SpatialNode a = AddOwned(w, "N002", new Vec3(1000, 0, 0));
            SpatialNode b = AddOwned(w, "N003", new Vec3(2000, 0, 0));

            HashSet<string> depthOne = w.ComputeCampaignRegionMembers("F", center.Id, 1);
            Assert.Contains(a.Id, depthOne);
            Assert.DoesNotContain(b.Id, depthOne); // second hop not reached — depth=1 stops after A

            HashSet<string> depthTwo = w.ComputeCampaignRegionMembers("F", center.Id, 2);
            Assert.Contains(a.Id, depthTwo);
            Assert.Contains(b.Id, depthTwo); // now reachable via A on the second hop
        }

        [Fact]
        public void NonOwnedNode_WithinRadius_Excluded()
        {
            var w = TestSupport.World();
            SpatialNode center = AddOwned(w, "N001", new Vec3(0, 0, 0));
            SpatialNode rival = AddOwned(w, "N002", new Vec3(500, 0, 0), tag: "ENEMY");

            HashSet<string> members = w.ComputeCampaignRegionMembers("F", center.Id, 2);

            Assert.DoesNotContain(rival.Id, members);
        }

        [Fact]
        public void CandidateZone_NotCenterZone_DeterminesItsOwnAdmissionRadius()
        {
            // A Surface candidate uses the much smaller Surface radius (2x15km=30km) even though the
            // center itself is DeepSpace (2x500km=1000km) — admission radius is the CANDIDATE's own
            // zone, not the center's, per the "changes according to distance from planet" requirement.
            var w = TestSupport.World();
            SpatialNode center = AddOwned(w, "N001", new Vec3(0, 0, 0));
            var surfaceMarkerPos = new Vec3(900, 0, 0);
            SpatialNode surfaceCandidate = AddOwned(w, "N002", surfaceMarkerPos); // 900km from center
            w.Planets = new OneSurfaceSpotCatalogue(surfaceMarkerPos);

            // 900km is well within DeepSpace's 1000km radius, but far outside Surface's 30km radius.
            HashSet<string> members = w.ComputeCampaignRegionMembers("F", center.Id, 2);

            Assert.DoesNotContain(surfaceCandidate.Id, members);
        }
    }
}
