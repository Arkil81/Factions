using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A Nexus agent's stores: shelves the authority prices, events the authority books once.</summary>
    public class NexusStoreTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);

        [Fact]
        public void ANewlyStandingStationIsShelvedAtOnceAndThenOnlyOnTheCadence()
        {
            var s = new NexusShelfSchedule();
            Assert.Equal(new[] { "N1" }, s.Due(2, "a", new[] { "N1" }, T0, Every).ToArray());
            Assert.Empty(s.Due(2, "a", new[] { "N1" }, T0.AddSeconds(1), Every));
            Assert.Equal(new[] { "N2" }, s.Due(2, "a", new[] { "N1", "N2" }, T0.AddSeconds(2), Every).ToArray());
            Assert.Equal(new[] { "N1", "N2" }, s.Due(2, "a", new[] { "N1", "N2" }, T0.AddSeconds(61), Every).ToArray());
        }

        [Fact]
        public void ABookedTradeReShelvesItsStation()
        {
            var s = new NexusShelfSchedule();
            s.Due(2, "a", new[] { "N1", "N2" }, T0, Every);
            s.MarkDirty("N2");
            Assert.Equal(new[] { "N2" }, s.Due(2, "a", new[] { "N1", "N2" }, T0.AddSeconds(1), Every).ToArray());
            Assert.Empty(s.Due(2, "a", new[] { "N1", "N2" }, T0.AddSeconds(2), Every));
        }

        /// <summary>The failure this exists for: a station that closed and stood again is a new grid
        /// with an empty store, and must be shelved again rather than wait out the cadence.</summary>
        [Fact]
        public void AStationThatStandsAgainOrANewSessionIsShelvedAgain()
        {
            var s = new NexusShelfSchedule();
            s.Due(2, "a", new[] { "N1" }, T0, Every);
            Assert.Empty(s.Due(2, "a", new string[0], T0.AddSeconds(1), Every));
            Assert.Equal(new[] { "N1" }, s.Due(2, "a", new[] { "N1" }, T0.AddSeconds(2), Every).ToArray());
            Assert.Equal(new[] { "N1" }, s.Due(2, "b", new[] { "N1" }, T0.AddSeconds(3), Every).ToArray());
            Assert.Equal(new[] { "N1" }, s.Due(4, "b", new[] { "N1" }, T0.AddSeconds(3), Every).ToArray());
        }

        [Fact]
        public void AReSentStoreEventIsBookedOnce()
        {
            var outbox = new NexusOutbox<NexusStoreEvent>();
            var inbox = new NexusStoreInbox();
            outbox.Add(new NexusStoreEvent { Kind = NexusStoreEventKind.Trade, Node = "N1", Key = "Ingot:Iron", Amount = 100, Buy = true, Credits = 500 });
            long through;
            Assert.Single(inbox.Accept(2, "s", outbox.Pending(), out through));
            Assert.Empty(inbox.Accept(2, "s", outbox.Pending(), out through));
            outbox.Ack(through);
            Assert.Null(outbox.Pending());
        }

        [Fact]
        public void TheReportAndTheReplyCarryEventsAndShelves()
        {
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(new NexusAgentReport
            {
                Session = "s",
                StoreEvents = new List<NexusStoreEvent>
                {
                    new NexusStoreEvent { Seq = 3, Kind = NexusStoreEventKind.Salvage, Node = "N1", Credits = 9000, Steam = 76561198000000001UL,
                                          Parts = new Dictionary<string, long> { { "Comp:SteelPlate", 40 } } },
                    new NexusStoreEvent { Seq = 4, Kind = NexusStoreEventKind.Ship, Seller = "AAA", Prefab = "Hauler" },
                    new NexusStoreEvent { Seq = 5, Kind = NexusStoreEventKind.Debris, Node = "N2", Grids = 3, Blocks = 17,
                                          Cargo = new Dictionary<string, long> { { "Ore:Iron", 900 } } },
                },
            }.Encode(), out back));
            Assert.Equal(3, back.StoreEvents.Count);
            Assert.Equal(NexusStoreEventKind.Debris, back.StoreEvents[2].Kind);
            Assert.Equal(3, back.StoreEvents[2].Grids);
            Assert.Equal(17, back.StoreEvents[2].Blocks);
            Assert.Equal(900, back.StoreEvents[2].Cargo["Ore:Iron"]);
            Assert.Null(back.StoreEvents[2].Parts);
            Assert.Equal(NexusStoreEventKind.Salvage, back.StoreEvents[0].Kind);
            Assert.Equal(76561198000000001UL, back.StoreEvents[0].Steam);
            Assert.Equal(40, back.StoreEvents[0].Parts["Comp:SteelPlate"]);
            Assert.Equal("Hauler", back.StoreEvents[1].Prefab);

            var sent = new NexusStationReply
            {
                StoreThrough = 4,
                Shelves = new List<NexusShelf>
                {
                    new NexusShelf
                    {
                        Node = "N1", Seller = "AAA",
                        Rows = new List<StationListing>
                        {
                            new StationListing { InvKey = "Ingot:Iron", Side = StationListingSide.Offer, Quantity = 1000, UnitPrice = 12 },
                            new StationListing { InvKey = "Ore:Ice", Side = StationListingSide.Order, Quantity = 5000, UnitPrice = 3 },
                        },
                        Ships = new List<NexusShelfShip> { new NexusShelfShip { Prefab = "Hauler", Count = 2, Price = 1500000, Pcu = 3200 } },
                    },
                    new NexusShelf { Node = "N2" },
                },
            };
            Assert.False(sent.IsEmpty);
            NexusStationReply reply;
            Assert.True(NexusStationReply.TryDecode(sent.Encode(), out reply));
            Assert.Equal(4, reply.StoreThrough);
            NexusShelf n1 = reply.Shelves[0];
            Assert.Equal(StationListingSide.Offer, n1.Rows[0].Side);
            Assert.Equal(StationListingSide.Order, n1.Rows[1].Side);
            Assert.Equal(5000, n1.Rows[1].Quantity);
            Assert.Equal(1500000, n1.Ships[0].Price);
            Assert.Null(reply.Shelves[1].Rows);
            Assert.Equal("N2", reply.Shelves[1].Node);
        }
    }
}
