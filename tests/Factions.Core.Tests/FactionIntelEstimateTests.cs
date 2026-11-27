using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-532: the Factions page Overview's estimates (docs/fog-of-war.md → The Factions
    /// page: Overview and Members).</summary>
    public class FactionIntelEstimateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Window = TimeSpan.FromHours(72);
        private static readonly TimeSpan Horizon = TimeSpan.FromDays(30);
        private static readonly TimeSpan Stale = TimeSpan.FromHours(72);

        private static SpatialNode Node(string id, string owner)
        {
            return new SpatialNode { Id = id, OwnerFactionTag = owner, StableStationId = 1, StationPrefabId = "P" };
        }

        [Fact]
        public void Sightings_KeepTheStrongestInTheWindow_ThenTheNextAfterItLapses()
        {
            var s = new FleetSightings();
            Assert.True(s.Record("AAA", 20000, T0, Window));
            Assert.False(s.Record("AAA", 5000, T0.AddHours(1), Window));      // weaker, inside the window
            Assert.False(s.Record("AAA", 90000, T0.AddHours(-1), Window));    // older than the held one
            FleetSighting got;
            Assert.True(s.TryGet("aaa", T0.AddHours(2), Window, out got));
            Assert.Equal(20000f, got.Dps);
            Assert.Equal(T0, got.Utc);

            Assert.False(s.TryGet("AAA", T0.AddHours(72), Window, out got));  // lapsed at read
            Assert.True(s.Record("AAA", 5000, T0.AddHours(73), Window));      // a lapsed one is replaced
            Assert.True(s.TryGet("AAA", T0.AddHours(74), Window, out got));
            Assert.Equal(5000f, got.Dps);
            Assert.False(s.Record("AAA", 0, T0.AddHours(75), Window));        // an unarmed fleet says nothing
            Assert.Equal(1, s.Compact(T0.AddHours(73 + 72), Window));
            Assert.Equal(0, s.Count);
        }

        [Fact]
        public void Bands_AreTheCalibratedEdges_AndUnknownWithoutAReading()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(WealthBand.Unknown, FactionIntelEstimate.Wealth(0, 0, t));
            Assert.Equal(WealthBand.Poor, FactionIntelEstimate.Wealth(14, 7, t));
            Assert.Equal(WealthBand.Modest, FactionIntelEstimate.Wealth(15, 8, t));
            Assert.Equal(WealthBand.Comfortable, FactionIntelEstimate.Wealth(40, 20, t));
            Assert.Equal(WealthBand.Prosperous, FactionIntelEstimate.Wealth(100, 50, t));
            Assert.Equal(WealthBand.Rich, FactionIntelEstimate.Wealth(200, 100, t));

            Assert.Equal(StrengthBand.Unknown, FactionIntelEstimate.Strength(1e6, false, t));
            Assert.Equal(StrengthBand.Negligible, FactionIntelEstimate.Strength(0, true, t));
            Assert.Equal(StrengthBand.Light, FactionIntelEstimate.Strength(150000, true, t));
            Assert.Equal(StrengthBand.Moderate, FactionIntelEstimate.Strength(450000, true, t));
            Assert.Equal(StrengthBand.Strong, FactionIntelEstimate.Strength(650000, true, t));
            Assert.Equal(StrengthBand.Overwhelming, FactionIntelEstimate.Strength(900000, true, t));
        }

        [Fact]
        public void Estimate_CountsOnlyIdentifiedNodesOfTheBelievedOwner_AndSumsWhatWasScanned()
        {
            FactionTuning t = TestSupport.Tuning();
            var g = new SpatialGraph();
            SpatialNode hub = Node("H", "AAA");
            SpatialNode settlement = Node("S", "AAA");
            SpatialNode contactOnly = Node("C", "AAA");
            SpatialNode taken = Node("X", "BBB");     // AAA's when seen, BBB's now: the viewer still believes AAA
            SpatialNode other = Node("O", "BBB");
            foreach (SpatialNode n in new[] { hub, settlement, contactOnly, taken, other }) g.Nodes[n.Id] = n;

            var k = new FactionKnowledge();
            k.Touch(hub, T0, IntelTier.Armament, NodeStage.Hub, Stale,
                    new DefenceReading { Band = DefenceBand.Fortress, Dps = 300000f });
            k.Touch(settlement, T0.AddHours(1), IntelTier.Identity, NodeStage.Settlement, Stale);
            k.Touch(contactOnly, T0, IntelTier.Contact, NodeStage.Capital, Stale);
            taken.OwnerFactionTag = "AAA";
            k.Touch(taken, T0, IntelTier.Identity, NodeStage.Outpost, Stale);
            taken.OwnerFactionTag = "BBB";
            k.Touch(other, T0, IntelTier.Armament, NodeStage.Capital, Stale,
                    new DefenceReading { Band = DefenceBand.Fortress, Dps = 999999f });

            var sightings = new FleetSightings();
            sightings.Record("AAA", 200000, T0.AddHours(2), Window);
            DateTime now = T0.AddHours(3);

            FactionEstimate e = FactionIntelEstimate.Estimate("AAA", g, new List<FactionKnowledge> { k }, sightings,
                                                               now, Horizon, t);
            Assert.Equal(3, e.KnownNodes);                         // H, S, X; not the contact-only C
            Assert.Equal(4.0 + 2.0 + 1.0, e.WealthScore);
            Assert.Equal(WealthBand.Poor, e.Wealth);
            Assert.Equal(1, e.DefenceReadings);
            Assert.True(e.HasSighting);
            Assert.Equal(500000.0, e.StrengthDps, 0);
            Assert.Equal(StrengthBand.Moderate, e.Strength);
            Assert.Equal(T0.AddHours(2), e.StrengthUtc);

            FactionEstimate stranger = FactionIntelEstimate.Estimate("ZZZ", g, new List<FactionKnowledge> { k },
                                                                      sightings, now, Horizon, t);
            Assert.Equal(0, stranger.KnownNodes);
            Assert.Equal(WealthBand.Unknown, stranger.Wealth);
            Assert.Equal(StrengthBand.Unknown, stranger.Strength);

            // Identified nodes with no close scan and no sighting: wealth is known, strength is not.
            FactionEstimate unscanned = FactionIntelEstimate.Estimate("AAA", g, new List<FactionKnowledge> { k },
                                                                       null, now, Horizon, t);
            Assert.Equal(300000.0, unscanned.StrengthDps, 0);
            var bare = new FactionKnowledge();
            bare.Touch(settlement, T0, IntelTier.Identity, NodeStage.Settlement, Stale);
            FactionEstimate noReading = FactionIntelEstimate.Estimate("AAA", g, new List<FactionKnowledge> { bare },
                                                                       null, now, Horizon, t);
            Assert.Equal(WealthBand.Poor, noReading.Wealth);
            Assert.Equal(StrengthBand.Unknown, noReading.Strength);
        }

        [Fact]
        public void Persistence_DefenceDpsRidesWithTheBand()
        {
            var doc = KnowledgeEntryDoc.From(new KnownNode
            {
                LastSeenUtc = T0, HasDetail = true, OwnerTag = "AAA", Tier = IntelTier.Armament, GradedUtc = T0,
                Defence = DefenceBand.Contested, DefenceUtc = T0, DefenceDps = 42000f,
            });
            string json = JsonConvert.SerializeObject(doc);
            Assert.Contains("\"DV\":42000", json, StringComparison.Ordinal);
            Assert.Equal(42000f, JsonConvert.DeserializeObject<KnowledgeEntryDoc>(json).ToKnownNode().DefenceDps);

            string unread = JsonConvert.SerializeObject(KnowledgeEntryDoc.From(new KnownNode
            {
                LastSeenUtc = T0, HasDetail = true, Tier = IntelTier.Identity, GradedUtc = T0,
            }));
            Assert.DoesNotContain("\"DV\"", unread, StringComparison.Ordinal);
        }

        [Fact]
        public void PlayerStore_RoundTripsSightings_AndOmitsThemWhenNone()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fi-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var store = new PlayerKnowledgeStore(dir);
                store.Touch(7, "B", T0);
                store.SaveIfDirty();
                string file = System.IO.Directory.GetFiles(dir)[0];
                Assert.DoesNotContain("Sightings", System.IO.File.ReadAllText(file), StringComparison.Ordinal);

                store.RecordSighting(7, "AAA", 12345, T0, Window);
                store.SaveIfDirty();
                var back = new PlayerKnowledgeStore(dir);
                FleetSighting s;
                Assert.True(back.SightingsFor(7).TryGet("AAA", T0.AddHours(1), Window, out s));
                Assert.Equal(12345f, s.Dps);
                Assert.Equal(T0, s.Utc);
                Assert.Equal(DateTimeKind.Utc, s.Utc.Kind);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Wire_ExactFiguresTravelOnlyWhenExact()
        {
            WireFactionIntel f;
            string tag;
            Assert.True(FactionsWire.TryReadFactionIntelRequest(FactionsWire.WriteFactionIntelRequest("AAA"), out tag));
            Assert.Equal("AAA", tag);

            var foreign = new WireFactionIntel
            {
                Tag = "AAA", Known = true, KnownNodes = 5, Wealth = (byte)WireWealthBand.Modest,
                Strength = (byte)WireStrengthBand.Light, StrengthAgeSeconds = 3600,
                Ledger = 999, StrengthDps = 888, Treasury = 777, HasTreasury = true,
            };
            byte[] bytes = FactionsWire.WriteFactionIntelSnapshot(foreign);
            Assert.True(FactionsWire.TryReadFactionIntelSnapshot(bytes, out f));
            Assert.Equal(5, f.KnownNodes);
            Assert.Equal((byte)WireWealthBand.Modest, f.Wealth);
            Assert.Equal((byte)WireStrengthBand.Light, f.Strength);
            Assert.Equal(3600, f.StrengthAgeSeconds);
            Assert.False(f.Exact);
            Assert.Equal(0L, f.Ledger);
            Assert.Equal(0L, f.Treasury);

            foreign.Exact = true;
            Assert.True(FactionsWire.TryReadFactionIntelSnapshot(FactionsWire.WriteFactionIntelSnapshot(foreign), out f));
            Assert.Equal(999L, f.Ledger);
            Assert.Equal(888L, f.StrengthDps);
            Assert.True(f.HasTreasury);
            Assert.Equal(777L, f.Treasury);

            foreign.HasTreasury = false;
            Assert.True(FactionsWire.TryReadFactionIntelSnapshot(FactionsWire.WriteFactionIntelSnapshot(foreign), out f));
            Assert.False(f.HasTreasury);
            Assert.Equal(0L, f.Treasury);

            Assert.False(FactionsWire.TryReadFactionIntelSnapshot(FactionsWire.WriteFactionIntelRequest("AAA"), out f));
        }

        [Fact]
        public void Bands_MirrorTheWireByOrdinal()
        {
            Assert.Equal((int)WireWealthBand.Rich, (int)WealthBand.Rich);
            Assert.Equal((int)WireStrengthBand.Overwhelming, (int)StrengthBand.Overwhelming);
            Assert.Equal(Enum.GetValues(typeof(WireWealthBand)).Length, Enum.GetValues(typeof(WealthBand)).Length);
            Assert.Equal(Enum.GetValues(typeof(WireStrengthBand)).Length, Enum.GetValues(typeof(StrengthBand)).Length);
        }
    }
}
