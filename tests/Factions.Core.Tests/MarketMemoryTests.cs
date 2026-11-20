using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class MarketMemoryTests
    {
        private static readonly DateTime T0 = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Horizon = TimeSpan.FromDays(7);

        private static Dictionary<string, List<FrozenMarketRow>> Station(
            string nodeId, string invKey, int price, byte side = 0)
        {
            return new Dictionary<string, List<FrozenMarketRow>>
            {
                { nodeId, new List<FrozenMarketRow>
                    { new FrozenMarketRow { InvKey = invKey, Side = side, UnitPrice = price } } },
            };
        }

        [Fact]
        public void RemembersAndReadsBackTheFreshestOfSeveralObservers()
        {
            var m = new MarketMemory(null);
            m.Remember(1, "IGSL", T0, Station("N1", "Ore:Iron", 300));
            m.Remember(2, "IGSL", T0.AddHours(1), Station("N1", "Ore:Iron", 350));

            FrozenFactionMarket got = m.Freshest(new List<long> { 1, 2 }, "IGSL", T0.AddHours(2), Horizon);
            Assert.NotNull(got);
            Assert.Equal(T0.AddHours(1), got.AsOfUtc);
            Assert.Equal(350, got.Stations["N1"][0].UnitPrice);
        }

        [Fact]
        public void AFactionNobodyHasSeenReadsAsNull()
        {
            var m = new MarketMemory(null);
            m.Remember(1, "IGSL", T0, Station("N1", "Ore:Iron", 300));
            Assert.Null(m.Freshest(new List<long> { 1 }, "MTTL", T0, Horizon));
        }

        [Fact]
        public void ALapsedMemoryIsForgottenAtRead_NotShownStale()
        {
            var m = new MarketMemory(null);
            m.Remember(1, "IGSL", T0, Station("N1", "Ore:Iron", 300));
            Assert.Null(m.Freshest(new List<long> { 1 }, "IGSL", T0.Add(Horizon), Horizon));
            // Compact is a file-size measure only: it cannot change what a read answers.
            Assert.NotNull(m.Freshest(new List<long> { 1 }, "IGSL", T0.AddHours(1), Horizon));
        }

        [Fact]
        public void ANonPositiveHorizonMeansNoExpiry_NotExpireEverything()
        {
            // Mirrors FactionKnowledge.HasLive: a tuning file that never carried the key must not
            // silently blind every observer.
            var m = new MarketMemory(null);
            m.Remember(1, "IGSL", T0, Station("N1", "Ore:Iron", 300));
            Assert.NotNull(m.Freshest(new List<long> { 1 }, "IGSL", T0.AddYears(5), TimeSpan.Zero));
        }

        [Fact]
        public void RememberReplacesAFactionsTable_RatherThanMergingIntoIt()
        {
            // A station that stopped listing an item must stop showing it; a merge would keep the
            // row alive forever.
            var m = new MarketMemory(null);
            m.Remember(1, "IGSL", T0, Station("N1", "Ore:Iron", 300));
            m.Remember(1, "IGSL", T0.AddHours(1), Station("N2", "Ore:Ice", 25));

            FrozenFactionMarket got = m.Freshest(new List<long> { 1 }, "IGSL", T0.AddHours(2), Horizon);
            Assert.False(got.Stations.ContainsKey("N1"));
            Assert.True(got.Stations.ContainsKey("N2"));
        }

        [Fact]
        public void CompactDropsLapsedEntriesAndEmptiesTheObserver()
        {
            var m = new MarketMemory(null);
            m.Remember(1, "IGSL", T0, Station("N1", "Ore:Iron", 300));
            Assert.Equal(1, m.ObserverCount);

            Assert.Equal(1, m.Compact(T0.Add(Horizon), Horizon));
            Assert.Equal(0, m.ObserverCount);
        }

        [Fact]
        public void RoundTripsThroughDisk()
        {
            string dir = Path.Combine(Path.GetTempPath(), "fac-mm-" + Guid.NewGuid().ToString("N"));
            try
            {
                var a = new MarketMemory(dir);
                a.Remember(4242, "IGSL", T0, Station("N1", "Comp:SteelPlate", 9009, side: 1));
                a.SaveIfDirty();

                var b = new MarketMemory(dir);
                FrozenFactionMarket got = b.Freshest(new List<long> { 4242 }, "IGSL", T0.AddHours(1), Horizon);
                Assert.NotNull(got);
                FrozenMarketRow row = Assert.Single(got.Stations["N1"]);
                Assert.Equal("Comp:SteelPlate", row.InvKey);
                Assert.Equal(9009, row.UnitPrice);
                Assert.Equal((byte)1, row.Side);
                Assert.Equal(T0, got.AsOfUtc);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void ACorruptFileCostsMemoryAndNotTheSession()
        {
            string dir = Path.Combine(Path.GetTempPath(), "fac-mm-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "market_memory.json"), "{ not json at all ][");
                var m = new MarketMemory(dir);              // must not throw
                Assert.Equal(0, m.ObserverCount);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void AnInMemoryStoreNeverTouchesDisk()
        {
            // The offline sim and most tests pass a null storage dir.
            var m = new MarketMemory(null);
            m.Remember(1, "IGSL", T0, Station("N1", "Ore:Iron", 300));
            m.SaveIfDirty();                                 // must be a no-op, not a crash
            Assert.NotNull(m.Freshest(new List<long> { 1 }, "IGSL", T0, Horizon));
        }
    }
}
