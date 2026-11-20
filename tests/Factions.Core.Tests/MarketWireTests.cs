using System.Collections.Generic;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class MarketWireTests
    {
        private static List<string> Items()
        {
            return new List<string> { "Comp:SteelPlate", "Ore:Iron" };
        }

        private static List<WireMarketStation> Stations()
        {
            return new List<WireMarketStation>
            {
                new WireMarketStation
                {
                    NodeId = "N001", DisplayName = "Alder Station", OwnerTag = "IGSL",
                    Relation = (byte)WireRelation.Neutral,
                    XKm = 1.5, YKm = -2.5, ZKm = 3.5, Live = true, AsOfUtcTicks = 637000000000000000L,
                },
                new WireMarketStation
                {
                    NodeId = "N002", DisplayName = "Dunhill", OwnerTag = "MTTL",
                    Relation = (byte)WireRelation.Own,
                    XKm = 10, YKm = 20, ZKm = 30, Live = false, AsOfUtcTicks = 636000000000000000L,
                },
            };
        }

        private static List<WireMarketRow> Rows()
        {
            return new List<WireMarketRow>
            {
                new WireMarketRow { StationIndex = 0, ItemIndex = 0, Side = 0, UnitPrice = 9009, Quantity = 250 },
                new WireMarketRow { StationIndex = 1, ItemIndex = 1, Side = 1, UnitPrice = 143, Quantity = -1 },
            };
        }

        [Fact]
        public void MarketSnapshot_RoundTrips()
        {
            byte[] data = FactionsWire.WriteMarketSnapshot(Items(), Stations(), Rows());

            List<string> items;
            List<WireMarketStation> stations;
            List<WireMarketRow> rows;
            Assert.True(FactionsWire.TryReadMarketSnapshot(data, out items, out stations, out rows));

            Assert.Equal(new[] { "Comp:SteelPlate", "Ore:Iron" }, items);

            Assert.Equal(2, stations.Count);
            Assert.Equal("N001", stations[0].NodeId);
            Assert.Equal("Alder Station", stations[0].DisplayName);
            Assert.Equal((byte)WireRelation.Neutral, stations[0].Relation);
            Assert.True(stations[0].Live);
            Assert.Equal(3.5, stations[0].ZKm);
            Assert.False(stations[1].Live);
            Assert.Equal(636000000000000000L, stations[1].AsOfUtcTicks);

            Assert.Equal(2, rows.Count);
            Assert.Equal(9009, rows[0].UnitPrice);
            Assert.Equal(250, rows[0].Quantity);
            Assert.Equal((byte)1, rows[1].Side);
        }

        [Fact]
        public void MarketSnapshot_UnknownQuantitySurvivesAsNegativeOne()
        {
            // -1 means "a frozen station remembered the want, not the amount". If this ever came
            // back as 0 the window would render "they want none of it", which is the opposite claim.
            byte[] data = FactionsWire.WriteMarketSnapshot(Items(), Stations(), Rows());
            List<string> items;
            List<WireMarketStation> stations;
            List<WireMarketRow> rows;
            Assert.True(FactionsWire.TryReadMarketSnapshot(data, out items, out stations, out rows));
            Assert.Equal(-1, rows[1].Quantity);
        }

        [Fact]
        public void MarketSnapshot_RejectsARowIndexingPastItsTable()
        {
            var rows = new List<WireMarketRow>
            {
                new WireMarketRow { StationIndex = 7, ItemIndex = 0, Side = 0, UnitPrice = 1, Quantity = 1 },
            };
            byte[] data = FactionsWire.WriteMarketSnapshot(Items(), Stations(), rows);

            List<string> items;
            List<WireMarketStation> stations;
            List<WireMarketRow> decoded;
            Assert.False(FactionsWire.TryReadMarketSnapshot(data, out items, out stations, out decoded));
            Assert.Null(decoded);
        }

        [Fact]
        public void MarketSnapshot_EmptyIsValid()
        {
            // A viewer who has discovered no station gets an empty table, not a refused message.
            byte[] data = FactionsWire.WriteMarketSnapshot(
                new List<string>(), new List<WireMarketStation>(), new List<WireMarketRow>());

            List<string> items;
            List<WireMarketStation> stations;
            List<WireMarketRow> rows;
            Assert.True(FactionsWire.TryReadMarketSnapshot(data, out items, out stations, out rows));
            Assert.Empty(items);
            Assert.Empty(stations);
            Assert.Empty(rows);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MarketRequest_RoundTrips(bool stream)
        {
            byte[] data = FactionsWire.WriteMarketRequest(stream);
            bool decoded;
            Assert.True(FactionsWire.TryReadMarketRequest(data, out decoded));
            Assert.Equal(stream, decoded);
        }

        [Fact]
        public void MarketReadersRejectAnotherKind()
        {
            byte[] other = FactionsWire.WriteMapRequest(true);
            bool stream;
            Assert.False(FactionsWire.TryReadMarketRequest(other, out stream));
        }

        [Fact]
        public void MarketFeatureBitIsDistinct()
        {
            // 1L << 9, immediately after Signature. A collision would silently grant another feature.
            Assert.Equal(512L, (long)WireFeatures.Market);
            Assert.NotEqual(WireFeatures.Market, WireFeatures.Signature);
        }
    }
}
