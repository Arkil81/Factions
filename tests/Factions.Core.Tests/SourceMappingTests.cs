using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class SourceMappingTests
    {
        [Fact]
        public void FromGrid_ConvertsMetresToKm_AndSetsDefaults()
        {
            SourceDoc s = SourceMapping.FromGrid(123L, 750, 40000, -8000, 16000);

            Assert.Equal("G123", s.Id);
            Assert.Equal(40.0, s.X, 6);   // 40000 m -> 40 km
            Assert.Equal(-8.0, s.Y, 6);
            Assert.Equal(16.0, s.Z, 6);
            // Radius is deliberately NOT set: it is derived from what the node is (InfluenceRadius),
            // and this mapper used to be the one place that disagreed with the rest of the world about
            // it -- 40 km against everyone else's 24. FAC-250.
            Assert.Equal(0.0, s.Radius, 6);
            Assert.Equal(SourceMapping.DefaultResourceValue, s.ResourceValue, 6);
            Assert.Equal(1.0, s.Strength, 6);   // real station: fully established on discovery
            Assert.Equal(123L, s.StableStationId); // grid entity id doubles as station marker for Develop gate
        }

        [Fact]
        public void FromGrid_AlwaysFullHold_WithStationId()
        {
            // All grid sizes start at 1.0 hold — the station is real and established regardless of block count.
            Assert.Equal(1.0, SourceMapping.FromGrid(1L, 0, 0, 0, 0).Strength, 6);
            Assert.Equal(1.0, SourceMapping.FromGrid(2L, SourceMapping.FullStrengthBlocks, 0, 0, 0).Strength, 6);
            Assert.Equal(1L, SourceMapping.FromGrid(1L, 0, 0, 0, 0).StableStationId);
        }

        [Fact]
        public void GridSourceId_RoundTrips_AndIsDistinctFromEngineIds()
        {
            string id = SourceMapping.GridSourceId(98765L);

            Assert.True(SourceMapping.IsGridSourceId(id));
            Assert.False(SourceMapping.IsGridSourceId("N001")); // engine-created source id
            Assert.False(SourceMapping.IsGridSourceId(null));

            long eid;
            Assert.True(SourceMapping.TryGetEntityId(id, out eid));
            Assert.Equal(98765L, eid);
            Assert.False(SourceMapping.TryGetEntityId("N001", out eid));
        }

        [Fact]
        public void FromEconomyStation_ConvertsMetresToKm_AndUsesDistinctId()
        {
            SourceDoc s = SourceMapping.FromEconomyStation(4242L, 120000, 0, -36000);

            Assert.Equal("E4242", s.Id);
            Assert.True(SourceMapping.IsEconomyStationSourceId(s.Id));
            Assert.False(SourceMapping.IsGridSourceId(s.Id));          // economy id is not a grid id
            Assert.False(SourceMapping.IsEconomyStationSourceId("G5")); // ...and vice versa
            Assert.False(SourceMapping.IsEconomyStationSourceId("N001"));
            Assert.Equal(120.0, s.X, 6);    // 120000 m -> 120 km
            Assert.Equal(-36.0, s.Z, 6);
            Assert.Equal(SourceMapping.EconomyStationStrength, s.Strength, 6);
            Assert.Equal(SourceMapping.EconomyStationResourceValue, s.ResourceValue, 6);
            Assert.Equal(0.0, s.Radius, 6);   // derived, not assigned here -- see above
        }

        [Fact]
        public void IsScanBacked_TrueForRealAssets_FalseForEngineAndNull()
        {
            Assert.True(SourceMapping.IsScanBacked(SourceMapping.GridSourceId(1)));            // G### world grid
            Assert.True(SourceMapping.IsScanBacked(SourceMapping.EconomyStationSourceId(1)));  // E### economy station
            Assert.False(SourceMapping.IsScanBacked("N001"));                                  // engine-invented
            Assert.False(SourceMapping.IsScanBacked(null));
            Assert.False(SourceMapping.IsScanBacked(""));
        }
    }
}
