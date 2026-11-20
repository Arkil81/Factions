using System.Collections.Generic;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    public class NodeSpawnWireTests
    {
        [Fact]
        public void List_RoundTripsEveryField()
        {
            var rows = new List<WireNodeSpawnRow>
            {
                new WireNodeSpawnRow { NodeId = "N1", DisplayName = "Alpha Post", StationId = 867634283999268577,
                                       Site = 2, Block = 0, CooldownSeconds = 0 },
                new WireNodeSpawnRow { NodeId = "N2", DisplayName = "Zeta Yard", StationId = 12,
                                       Site = 1, Block = 4, CooldownSeconds = 299 },
            };

            List<WireNodeSpawnRow> read;
            Assert.True(FactionsWire.TryReadNodeSpawnList(FactionsWire.WriteNodeSpawnList(rows), out read));

            Assert.Equal(2, read.Count);
            Assert.Equal(rows[0].NodeId, read[0].NodeId);
            Assert.Equal(rows[0].DisplayName, read[0].DisplayName);
            Assert.Equal(rows[0].StationId, read[0].StationId);
            Assert.Equal(rows[0].Site, read[0].Site);
            Assert.Equal(rows[1].Block, read[1].Block);
            Assert.Equal(rows[1].CooldownSeconds, read[1].CooldownSeconds);
        }

        [Fact]
        public void List_EmptyIsAValidAnswer()
        {
            List<WireNodeSpawnRow> read;
            Assert.True(FactionsWire.TryReadNodeSpawnList(FactionsWire.WriteNodeSpawnList(null), out read));
            Assert.Empty(read);
        }

        [Fact]
        public void Request_IsNotReadAsAList_OrTheOtherWayRound()
        {
            List<WireNodeSpawnRow> read;
            Assert.True(FactionsWire.TryReadNodeSpawnListRequest(FactionsWire.WriteNodeSpawnListRequest()));
            Assert.False(FactionsWire.TryReadNodeSpawnList(FactionsWire.WriteNodeSpawnListRequest(), out read));
            Assert.False(FactionsWire.TryReadNodeSpawnListRequest(FactionsWire.WriteNodeSpawnList(null)));
        }

        [Fact]
        public void Sentinel_RoundTrips_AndNeverDecodesARealEntityId()
        {
            long stationId;
            Assert.True(NodeSpawnSentinel.TryDecode(NodeSpawnSentinel.Encode(867634283999268577), out stationId));
            Assert.Equal(867634283999268577, stationId);

            // A medical room's id, the planet-pod path's planet id, and "no entity" all stay vanilla.
            Assert.False(NodeSpawnSentinel.TryDecode(144115188075855873, out stationId));
            Assert.False(NodeSpawnSentinel.TryDecode(0, out stationId));
            Assert.False(NodeSpawnSentinel.TryDecode(long.MinValue, out stationId));
            Assert.Equal(0, NodeSpawnSentinel.Encode(0));
        }
    }
}
