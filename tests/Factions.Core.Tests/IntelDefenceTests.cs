using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-531: node defence as a remembered Armament reading
    /// (docs/fog-of-war.md → Intelligence grades, slice 4).</summary>
    public class IntelDefenceTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Stale = TimeSpan.FromHours(72);

        private static SpatialNode Station(string id, string owner, float turretDps)
        {
            var n = new SpatialNode { Id = id, OwnerFactionTag = owner, StableStationId = 1, StationPrefabId = "P" };
            if (turretDps > 0) n.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = turretDps, RangeM = 800 } };
            return n;
        }

        [Fact]
        public void Band_FloorIsUndefended_ThenTheCalibratedEdges()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(20000.0, t.IntelDefenceContestedDps);
            Assert.Equal(100000.0, t.IntelDefenceFortressDps);
            SpatialNode n = Station("A", "AAA", 0);
            double floor = CombatStrength.StationFlat(n, t);
            Assert.Equal(DefenceBand.Undefended, NodeDefence.Band(floor, n, t));
            Assert.Equal(DefenceBand.Light, NodeDefence.Band(floor + 1, n, t));
            Assert.Equal(DefenceBand.Contested, NodeDefence.Band(20000, n, t));
            Assert.Equal(DefenceBand.Fortress, NodeDefence.Band(100000, n, t));
            Assert.Equal(0, NodeDefence.Pips(DefenceBand.Unknown));
            Assert.Equal(0, NodeDefence.Pips(DefenceBand.Undefended));
            Assert.Equal(3, NodeDefence.Pips(DefenceBand.Fortress));
        }

        [Fact]
        public void Reader_UsesTheGatesOwnFigure()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode bare = Station("A", "AAA", 0);
            SpatialNode armed = Station("B", "AAA", 50000);
            Func<SpatialNode, DefenceBand> read = NodeDefence.Reader(null, t, new List<FactionState>());
            Assert.Equal(DefenceBand.Undefended, read(bare));
            Assert.Equal(NodeDefence.Band(CombatStrength.OfNodeDefence(armed, null, t), armed, t), read(armed));
            Assert.Equal(DefenceBand.Contested, read(armed));
        }

        [Fact]
        public void Touch_RecordsDefenceOnlyAtArmament_AndKeepsItThroughWorseReadings()
        {
            var k = new FactionKnowledge();
            SpatialNode n = Station("A", "AAA", 0);
            k.Touch(n, T0, IntelTier.Identity, NodeStage.Settlement, Stale, DefenceBand.Fortress);
            KnownNode e;
            Assert.True(k.TryGetGrade("A", T0, TimeSpan.Zero, out e));
            Assert.Equal(DefenceBand.Unknown, e.Defence);

            k.Touch(n, T0.AddMinutes(1), IntelTier.Armament, NodeStage.Settlement, Stale, DefenceBand.Light);
            // Stale grade replaced by a long-range pass: the band survives with its own time.
            k.Touch(n, T0.AddHours(80), IntelTier.Contact, NodeStage.None, Stale, DefenceBand.Unknown);
            Assert.True(k.TryGetGrade("A", T0.AddHours(80), TimeSpan.Zero, out e));
            Assert.Equal(IntelTier.Contact, e.Tier);
            Assert.Equal(DefenceBand.Light, e.Defence);
            Assert.Equal(T0.AddMinutes(1), e.DefenceUtc);

            k.Touch(n, T0.AddHours(81), IntelTier.Full, NodeStage.Settlement, Stale, DefenceBand.Contested);
            Assert.True(k.TryGetGrade("A", T0.AddHours(81), TimeSpan.Zero, out e));
            Assert.Equal(DefenceBand.Contested, e.Defence);
            k.Touch("A", T0.AddHours(82));
            Assert.True(k.TryGetGrade("A", T0.AddHours(82), TimeSpan.Zero, out e));
            Assert.Equal(DefenceBand.Contested, e.Defence);
        }

        [Fact]
        public void Touch_ASecondSensorInTheSameSweepCanStillReadDefence()
        {
            var k = new FactionKnowledge();
            SpatialNode n = Station("A", "AAA", 0);
            k.Touch(n, T0, IntelTier.Armament, NodeStage.Settlement, Stale, DefenceBand.Unknown);
            k.Touch(n, T0, IntelTier.Armament, NodeStage.Settlement, Stale, DefenceBand.Light);
            KnownNode e;
            Assert.True(k.TryGetGrade("A", T0, TimeSpan.Zero, out e));
            Assert.Equal(DefenceBand.Light, e.Defence);
        }

        [Fact]
        public void GradeOf_TakesTheFreshestDefenceAcrossStores_WhicheverHoldsTheGrade()
        {
            SpatialNode n = Station("A", "AAA", 0);
            var better = new FactionKnowledge();
            better.Touch(n, T0.AddHours(2), IntelTier.Armament, NodeStage.Settlement, Stale, DefenceBand.Light);
            var fresher = new FactionKnowledge();
            fresher.Touch(n, T0.AddHours(1), IntelTier.Armament, NodeStage.Settlement, Stale, DefenceBand.Fortress);
            fresher.Touch(n, T0.AddHours(3), IntelTier.Armament, NodeStage.Settlement, Stale, DefenceBand.Contested);
            KnownNode g = KnowledgeView.GradeOf("ZZZ", new List<FactionKnowledge> { better, fresher }, n,
                                                T0.AddHours(3), TimeSpan.FromDays(30));
            Assert.Equal(DefenceBand.Contested, g.Defence);
            Assert.Equal(T0.AddHours(3), g.DefenceUtc);

            var legacy = new FactionKnowledge();
            legacy.Touch(n, T0);
            Assert.Equal(DefenceBand.Unknown,
                KnowledgeView.GradeOf("ZZZ", new List<FactionKnowledge> { legacy }, n, T0, TimeSpan.Zero).Defence);
        }

        [Fact]
        public void Sweep_AClosePassReadsDefence_AFarOneDoesNot()
        {
            FactionTuning t = TestSupport.Tuning();
            // Node passive range 25 km against a station's x2 signature: 50 km. 16 dB is ~8 km.
            SpatialNode home = Station("H", "MERC", 0);
            SpatialNode near = Station("N", "RVN", 50000);
            near.Position = new Vec3(3, 0, 0);
            SpatialNode far = Station("F", "RVN", 50000);
            far.Position = new Vec3(40, 0, 0);
            var g = new SpatialGraph();
            foreach (SpatialNode x in new[] { home, near, far }) g.Nodes[x.Id] = x;
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);
            var state = new FactionState { Tag = "MERC" };

            KnowledgeSweep.SweepOwnedNodes(g, index, state, t, T0, null, null,
                                           NodeDefence.Readings(null, t, new List<FactionState>()));

            KnownNode e;
            Assert.True(state.Knowledge.TryGetGrade("N", T0, TimeSpan.Zero, out e));
            Assert.Equal(DefenceBand.Contested, e.Defence);
            Assert.True(state.Knowledge.TryGetGrade("F", T0, TimeSpan.Zero, out e));
            Assert.Equal(DefenceBand.Unknown, e.Defence);
        }

        [Fact]
        public void Persistence_DefenceRoundTrips_AndIsOmittedUntilRead()
        {
            var doc = KnowledgeEntryDoc.From(new KnownNode
            {
                LastSeenUtc = T0, HasDetail = true, OwnerTag = "AAA",
                Tier = IntelTier.Contact, GradedUtc = T0,
                Defence = DefenceBand.Fortress, DefenceUtc = T0.AddHours(-5),
            });
            string json = JsonConvert.SerializeObject(doc);
            Assert.Contains("\"D\":4", json);
            KnownNode back = JsonConvert.DeserializeObject<KnowledgeEntryDoc>(json).ToKnownNode();
            Assert.Equal(DefenceBand.Fortress, back.Defence);
            Assert.Equal(T0.AddHours(-5), back.DefenceUtc);

            string plain = JsonConvert.SerializeObject(KnowledgeEntryDoc.From(new KnownNode
            {
                LastSeenUtc = T0, HasDetail = true, Tier = IntelTier.Identity, GradedUtc = T0,
            }));
            Assert.DoesNotContain("\"D\"", plain, StringComparison.Ordinal);
            Assert.DoesNotContain("\"DT\"", plain, StringComparison.Ordinal);
        }

        [Fact]
        public void Wire_MapNodeAndNodeDetailCarryTheBand()
        {
            var node = new WireMapNode { Id = "N1", Tier = (byte)WireIntelTier.Armament, Defence = (byte)WireDefenceBand.Light };
            var next = new WireMapNode { Id = "N2", DisplayName = "Broadmoor" };
            List<WireMapNode> nodes;
            List<WireMapFaction> factions;
            Assert.True(FactionsWire.TryReadMapWorld(
                FactionsWire.WriteMapWorld(new List<WireMapNode> { node, next }, null), out nodes, out factions));
            Assert.Equal((byte)WireDefenceBand.Light, nodes[0].Defence);
            Assert.Equal("Broadmoor", nodes[1].DisplayName);

            WireNodeDetail detail;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(
                new WireNodeDetail { Exists = true, Id = "N1", Defence = (byte)WireDefenceBand.Fortress,
                                     DefenceAgeSeconds = 259200, DisplayName = "Broadmoor" }), out detail));
            Assert.Equal((byte)WireDefenceBand.Fortress, detail.Defence);
            Assert.Equal(259200, detail.DefenceAgeSeconds);
            Assert.Equal("Broadmoor", detail.DisplayName);
        }

        [Fact]
        public void WireDefenceBand_MirrorsDefenceBandByOrdinal()
        {
            foreach (DefenceBand b in Enum.GetValues(typeof(DefenceBand)))
                Assert.Equal(b.ToString(), ((WireDefenceBand)(byte)b).ToString());
            Assert.Equal(Enum.GetValues(typeof(DefenceBand)).Length, Enum.GetValues(typeof(WireDefenceBand)).Length);
        }
    }
}
