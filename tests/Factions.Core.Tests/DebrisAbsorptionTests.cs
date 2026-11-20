using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Which node reclaims a wreck. The host half of debris absorption is a live SE query
    /// with no offline harness, so this pins the only part that is pure: a node needs both a station
    /// and an owner to be credited, reach is a hard boundary, and the nearest eligible node wins
    /// even when a closer ineligible one exists.</summary>
    public class DebrisAbsorptionTests
    {
        private static SpatialNode Founded(SpatialGraph graph, string id, double xKm, string owner = "IGSL")
        {
            SpatialNode node = graph.Add(new Vec3(xKm, 0, 0), 0.5, id);
            node.OwnerFactionTag = owner;
            node.StableStationId = 1000 + graph.Nodes.Count;
            return node;
        }

        [Fact]
        public void CanAbsorb_NeedsBothAStationAndAnOwner()
        {
            var graph = new SpatialGraph();

            SpatialNode founded = Founded(graph, "N001", 0);
            Assert.True(DebrisAbsorption.CanAbsorb(founded));

            SpatialNode unowned = graph.Add(new Vec3(1, 0, 0), 0.5, "N002");
            unowned.StableStationId = 55;
            Assert.False(DebrisAbsorption.CanAbsorb(unowned));

            SpatialNode stationless = graph.Add(new Vec3(2, 0, 0), 0.5, "N003");
            stationless.OwnerFactionTag = "IGSL";
            Assert.False(DebrisAbsorption.CanAbsorb(stationless));

            Assert.False(DebrisAbsorption.CanAbsorb(null));
        }

        [Fact]
        public void NearestAbsorbingNode_PicksTheClosestEligibleNode()
        {
            var graph = new SpatialGraph();
            Founded(graph, "N001", 0);
            Founded(graph, "N002", 3);

            SpatialNode chosen = DebrisAbsorption.NearestAbsorbingNode(graph, new Vec3(2.4, 0, 0), 5.0);
            Assert.NotNull(chosen);
            Assert.Equal("N002", chosen.Id);
        }

        [Fact]
        public void NearestAbsorbingNode_SkipsACloserIneligibleNode()
        {
            var graph = new SpatialGraph();

            // Nearest by distance, but nobody owns it -- there is no inventory to credit.
            SpatialNode derelict = graph.Add(new Vec3(0.2, 0, 0), 0.5, "N001");
            derelict.StableStationId = 77;

            Founded(graph, "N002", 4);

            SpatialNode chosen = DebrisAbsorption.NearestAbsorbingNode(graph, Vec3Zero(), 10.0);
            Assert.NotNull(chosen);
            Assert.Equal("N002", chosen.Id);
        }

        [Fact]
        public void NearestAbsorbingNode_ReachIsAHardBoundary()
        {
            var graph = new SpatialGraph();
            Founded(graph, "N001", 5);

            Assert.Null(DebrisAbsorption.NearestAbsorbingNode(graph, Vec3Zero(), 4.99));
            Assert.NotNull(DebrisAbsorption.NearestAbsorbingNode(graph, Vec3Zero(), 5.01));
        }

        [Fact]
        public void NearestAbsorbingNode_NoReachMeansNoAbsorption()
        {
            var graph = new SpatialGraph();
            Founded(graph, "N001", 1);

            Assert.Null(DebrisAbsorption.NearestAbsorbingNode(graph, Vec3Zero(), 0));
            Assert.Null(DebrisAbsorption.NearestAbsorbingNode(graph, Vec3Zero(), -1));
            Assert.Null(DebrisAbsorption.NearestAbsorbingNode(null, Vec3Zero(), 5));
        }

        [Fact]
        public void NearestAbsorbingNode_BreaksATieOnIdSoTwoSweepsAgree()
        {
            var graph = new SpatialGraph();
            SpatialNode east = graph.Add(new Vec3(2, 0, 0), 0.5, "N900");
            east.OwnerFactionTag = "IGSL";
            east.StableStationId = 1;
            SpatialNode west = graph.Add(new Vec3(-2, 0, 0), 0.5, "N100");
            west.OwnerFactionTag = "METC";
            west.StableStationId = 2;

            Assert.Equal("N100", DebrisAbsorption.NearestAbsorbingNode(graph, Vec3Zero(), 5.0).Id);
        }

        private static Vec3 Vec3Zero() { return new Vec3(0, 0, 0); }
    }
}
