using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-529: nodes graded and remembered, and the economic stage
    /// (docs/fog-of-war.md → Intelligence grades, slice 3).</summary>
    public class IntelNodeTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

        private static SpatialNode Node(string id, double x, string owner = null, bool station = true, int levels = 0)
        {
            var n = new SpatialNode
            {
                Id = id,
                Position = new Vec3(x, 0, 0),
                OwnerFactionTag = owner,
                StableStationId = station ? 1 : 0,
            };
            if (levels > 0) n.Capabilities[NodeCapabilityKind.Refinery] = levels;
            return n;
        }

        private static SpatialGraph GraphOf(params SpatialNode[] nodes)
        {
            var g = new SpatialGraph();
            foreach (SpatialNode n in nodes) g.Nodes[n.Id] = n;
            return g;
        }

        // ---- stage ----------------------------------------------------------------------------

        [Fact]
        public void Stage_LaddersFromFieldToCapital()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode field = Node("F", 0, "AAA", station: false);
            SpatialNode outpost = Node("O", 0, "AAA", station: false, levels: 2);
            SpatialNode settlement = Node("S", 0, "AAA", levels: 5);
            SpatialNode hub = Node("H", 0, "AAA", levels: 12);
            SpatialNode capital = Node("C", 0, "AAA", levels: 20);
            NodeStages stages = NodeStages.Build(GraphOf(field, outpost, settlement, hub, capital), t);

            Assert.Equal(NodeStage.Field, stages.Of(field));
            Assert.Equal(NodeStage.Outpost, stages.Of(outpost));
            Assert.Equal(NodeStage.Settlement, stages.Of(settlement));
            Assert.Equal(NodeStage.Hub, stages.Of(hub));
            Assert.Equal(NodeStage.Capital, stages.Of(capital));
            Assert.Equal(1, stages.CapitalCount);
        }

        [Fact]
        public void Stage_HubOpensAtTheCalibratedScore()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal(10.0, t.NodeHubScore);
            SpatialNode nine = Node("A", 0, "AAA", levels: 9);
            SpatialNode ten = Node("B", 0, "BBB", levels: 10);
            NodeStages stages = NodeStages.Build(GraphOf(nine, ten), t);
            Assert.Equal(NodeStage.Settlement, stages.Of(nine));
            Assert.Equal(NodeStage.Capital, stages.Of(ten));   // BBB's only Hub
        }

        [Fact]
        public void Stage_OneCapitalPerFaction_TiesToTheLowerId_AndNoneForAFactionWithoutAHub()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode a2 = Node("N002", 0, "AAA", levels: 15);
            SpatialNode a1 = Node("N001", 0, "AAA", levels: 15);
            SpatialNode b = Node("N003", 0, "BBB", levels: 5);
            SpatialNode unkn = Node("N004", 0, NeutralFaction.Tag, levels: 25);
            NodeStages stages = NodeStages.Build(GraphOf(a2, a1, b, unkn), t);

            Assert.Equal(NodeStage.Capital, stages.Of(a1));
            Assert.Equal(NodeStage.Hub, stages.Of(a2));
            Assert.Equal(NodeStage.Settlement, stages.Of(b));
            Assert.Equal(NodeStage.Hub, stages.Of(unkn));      // abandoned: no faction to be capital of
            Assert.Equal(1, stages.CapitalCount);
        }

        [Fact]
        public void Score_IsCapabilityLevelsPlusATenthOfHeadcount_AndIgnoresTraffic()
        {
            SpatialNode n = Node("A", 0, "AAA", levels: 4);
            n.Capabilities[NodeCapabilityKind.Habitat] = 1;
            n.Workers = 30;
            n.ShipActivityCredits = 10;
            n.ShipActivityAtUtc = T0;
            Assert.Equal(4 + 1 + 3.0, NodeStages.Score(n), 9);
        }

        // ---- the remembered grade -------------------------------------------------------------

        [Fact]
        public void Grade_ACloseScanSurvivesALaterLongRangePass_UntilItGoesStale()
        {
            var k = new FactionKnowledge();
            SpatialNode n = Node("A", 0, "AAA", station: false);
            TimeSpan stale = TimeSpan.FromHours(72);

            k.Touch(n, T0, IntelTier.Identity, NodeStage.Field, stale);
            k.Touch(n, T0.AddHours(1), IntelTier.Contact, NodeStage.None, stale);
            KnownNode e;
            Assert.True(k.TryGetGrade("A", T0.AddHours(1), TimeSpan.Zero, out e));
            Assert.Equal(IntelTier.Identity, e.Tier);
            Assert.Equal(NodeStage.Field, e.Stage);
            Assert.Equal(T0, e.GradedUtc);
            Assert.Equal(T0.AddHours(1), e.LastSeenUtc);   // the sighting still renews the entry

            k.Touch(n, T0.AddHours(73), IntelTier.Contact, NodeStage.None, stale);
            Assert.True(k.TryGetGrade("A", T0.AddHours(73), TimeSpan.Zero, out e));
            Assert.Equal(IntelTier.Contact, e.Tier);
            Assert.Equal(NodeStage.None, e.Stage);
        }

        [Fact]
        public void Grade_AnEqualOrBetterReadingReplacesIt()
        {
            var k = new FactionKnowledge();
            SpatialNode n = Node("A", 0, "AAA");
            k.Touch(n, T0, IntelTier.Class, NodeStage.Settlement, TimeSpan.FromHours(72));
            n.Capabilities[NodeCapabilityKind.Refinery] = 12;
            k.Touch(n, T0.AddMinutes(3), IntelTier.Class, NodeStage.Hub, TimeSpan.FromHours(72));
            KnownNode e;
            Assert.True(k.TryGetGrade("A", T0.AddMinutes(3), TimeSpan.Zero, out e));
            Assert.Equal(NodeStage.Hub, e.Stage);
            Assert.Equal(T0.AddMinutes(3), e.GradedUtc);
        }

        [Fact]
        public void Grade_TwoSensorsInOneSweepKeepTheBetterReading()
        {
            var k = new FactionKnowledge();
            SpatialNode n = Node("A", 0, "AAA", station: false);
            k.Touch(n, T0, IntelTier.Contact, NodeStage.None, TimeSpan.FromHours(72));
            k.Touch(n, T0, IntelTier.Identity, NodeStage.Field, TimeSpan.FromHours(72));
            k.Touch(n, T0, IntelTier.Class, NodeStage.Field, TimeSpan.FromHours(72));
            KnownNode e;
            Assert.True(k.TryGetGrade("A", T0, TimeSpan.Zero, out e));
            Assert.Equal(IntelTier.Identity, e.Tier);
        }

        [Fact]
        public void Grade_FullIsRememberedAsArmament_AndAnUngradedTouchLeavesTheGradeAlone()
        {
            var k = new FactionKnowledge();
            SpatialNode n = Node("A", 0, "AAA");
            k.Touch(n, T0, IntelTier.Full, NodeStage.Settlement, TimeSpan.FromHours(72));
            k.Touch(n, T0.AddMinutes(1));
            k.Touch("A", T0.AddMinutes(2));
            KnownNode e;
            Assert.True(k.TryGetGrade("A", T0.AddMinutes(2), TimeSpan.Zero, out e));
            Assert.Equal(IntelTier.Armament, e.Tier);
            Assert.Equal(NodeStage.Settlement, e.Stage);
            Assert.Equal(T0.AddMinutes(2), e.LastSeenUtc);
        }

        [Fact]
        public void GradeOf_OwnIsFull_BestTierWinsAcrossStores_AndUngradedKnowledgeReadsAsIdentity()
        {
            SpatialNode n = Node("A", 0, "AAA", station: false);
            var mine = new FactionKnowledge();
            var faction = new FactionKnowledge();
            mine.Touch(n, T0, IntelTier.Contact, NodeStage.None, TimeSpan.Zero);
            faction.Touch(n, T0.AddMinutes(-5), IntelTier.Class, NodeStage.Field, TimeSpan.Zero);
            var sources = new List<FactionKnowledge> { mine, faction };
            TimeSpan horizon = TimeSpan.FromDays(30);

            Assert.Equal(IntelTier.Full, KnowledgeView.GradeOf("AAA", sources, n, T0, horizon).Tier);
            KnownNode g = KnowledgeView.GradeOf("ZZZ", sources, n, T0, horizon);
            Assert.Equal(IntelTier.Class, g.Tier);
            Assert.Equal(NodeStage.Field, g.Stage);

            var legacy = new FactionKnowledge();
            legacy.Touch(n, T0);
            Assert.Equal(IntelTier.Identity,
                KnowledgeView.GradeOf("ZZZ", new List<FactionKnowledge> { legacy }, n, T0, horizon).Tier);
            Assert.Equal(IntelTier.Identity, KnowledgeView.GradeOf("ZZZ", null, n, T0, horizon).Tier);
        }

        [Fact]
        public void GradeOf_ALapsedEntryDoesNotLendItsGrade()
        {
            SpatialNode n = Node("A", 0, "AAA", station: false);
            var old = new FactionKnowledge();
            old.Touch(n, T0, IntelTier.Armament, NodeStage.Field, TimeSpan.Zero);
            var recent = new FactionKnowledge();
            recent.Touch(n, T0.AddDays(20), IntelTier.Contact, NodeStage.None, TimeSpan.Zero);
            KnownNode g = KnowledgeView.GradeOf("ZZZ", new List<FactionKnowledge> { old, recent }, n,
                                                T0.AddDays(20), TimeSpan.FromDays(7));
            Assert.Equal(IntelTier.Contact, g.Tier);
        }

        // ---- the sweep writes grades ----------------------------------------------------------

        [Fact]
        public void Sweep_AStationSquawks_ABareFieldIsGradedByMargin()
        {
            FactionTuning t = TestSupport.Tuning();
            // Node passive range 25 km. A bare field's signature 0.75 gives 18.75 km of detection.
            SpatialNode home = Node("H", 0, "MERC");
            SpatialNode farStation = Node("S", 45, "RVN");                       // 50 km range, 0.9 dB
            SpatialNode farField = Node("F", -17, null, station: false);         // 0.8 dB: Contact
            SpatialNode nearField = Node("G", 3000, null, station: false);
            SpatialNode outpost = Node("P", 3005, "MERC");
            // A bare field with nothing on it is empty space and never swept; a planet site is not.
            farField.PlanetId = "Agni";
            nearField.PlanetId = "Agni";
            SpatialGraph g = GraphOf(home, farStation, farField, nearField, outpost);
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);
            var state = new FactionState { Tag = "MERC" };
            NodeStages stages = NodeStages.Build(g, t);

            KnowledgeSweep.SweepOwnedNodes(g, index, state, t, T0, null, stages);

            KnownNode e;
            Assert.True(state.Knowledge.TryGetGrade("S", T0, TimeSpan.Zero, out e));
            Assert.Equal(IntelTier.Identity, e.Tier);
            Assert.Equal(NodeStage.Settlement, e.Stage);
            Assert.True(state.Knowledge.TryGetGrade("F", T0, TimeSpan.Zero, out e));
            Assert.Equal(IntelTier.Contact, e.Tier);
            Assert.Equal(NodeStage.None, e.Stage);
            Assert.True(state.Knowledge.TryGetGrade("G", T0, TimeSpan.Zero, out e));   // 5 km: 11.5 dB
            Assert.Equal(IntelTier.Identity, e.Tier);
            Assert.Equal(NodeStage.Field, e.Stage);
        }

        [Fact]
        public void Sweep_AFleetGradesAtItsClosestApproach()
        {
            FactionTuning t = TestSupport.Tuning();
            // Fleet range 15 km x 0.75 = 11.25 km against a bare field; passing 2 km off is 15 dB.
            SpatialNode field = Node("F", 5000, null, station: false);
            field.Position = new Vec3(5000, 2, 0);
            field.PlanetId = "Agni";
            SpatialGraph g = GraphOf(Node("H", 0, "MERC"), field);
            NodeProximityIndex index = NodeProximityIndex.Build(g, 200);
            var state = new FactionState { Tag = "MERC" };

            KnowledgeSweep.SweepFleetSegment(g, index, state, new Vec3(4900, 0, 0), new Vec3(5100, 0, 0),
                                             t, T0, null, null, null, NodeStages.Build(g, t));

            KnownNode e;
            Assert.True(state.Knowledge.TryGetGrade("F", T0, TimeSpan.Zero, out e));
            Assert.Equal(IntelTier.Identity, e.Tier);
        }

        // ---- persistence ----------------------------------------------------------------------

        [Fact]
        public void Persistence_AGradeRoundTrips_AndAnUngradedEntryWritesNoGradeKeys()
        {
            var graded = KnowledgeEntryDoc.From(new KnownNode
            {
                LastSeenUtc = T0, HasDetail = true, OwnerTag = "AAA", HoldStrength = 0.5,
                Tier = IntelTier.Class, Stage = NodeStage.Hub, GradedUtc = T0.AddMinutes(-1),
            });
            string json = JsonConvert.SerializeObject(graded);
            Assert.Contains("\"G\":2", json);
            KnownNode back = JsonConvert.DeserializeObject<KnowledgeEntryDoc>(json).ToKnownNode();
            Assert.Equal(IntelTier.Class, back.Tier);
            Assert.Equal(NodeStage.Hub, back.Stage);
            Assert.Equal(T0.AddMinutes(-1), back.GradedUtc);
            Assert.Equal("AAA", back.OwnerTag);

            string plain = JsonConvert.SerializeObject(KnowledgeEntryDoc.From(new KnownNode
            {
                LastSeenUtc = T0, HasDetail = true, OwnerTag = "AAA",
            }));
            Assert.DoesNotContain("\"G\"", plain, StringComparison.Ordinal);
            Assert.DoesNotContain("\"GT\"", plain, StringComparison.Ordinal);
            Assert.Equal(IntelTier.None, JsonConvert.DeserializeObject<KnowledgeEntryDoc>(plain).ToKnownNode().Tier);
        }

        // ---- the HUD --------------------------------------------------------------------------

        [Fact]
        public void Withhold_ANodeBelowIdentityIsGreyAndUnnamed_AndKeepsItsKind()
        {
            var contacts = new List<ScannerContact>
            {
                new ScannerContact { Key = "node:F", Kind = ScannerContactKind.IceField, Label = "Ice field",
                                     DisplayName = "Frostmere", OwnerTag = "AAA", Tier = IntelTier.Class },
                new ScannerContact { Key = "node:S", Kind = ScannerContactKind.Station, Label = "Station",
                                     DisplayName = "Broadmoor", OwnerTag = "AAA", Tier = IntelTier.Identity },
            };
            PlayerScanner.Withhold(contacts, null);
            Assert.Null(contacts[0].DisplayName);
            Assert.Null(contacts[0].OwnerTag);
            Assert.Equal(ScannerContactKind.IceField, contacts[0].Kind);
            Assert.Equal("Broadmoor", contacts[1].DisplayName);
            Assert.Equal("AAA", contacts[1].OwnerTag);
        }

        // ---- the wire -------------------------------------------------------------------------

        [Fact]
        public void Wire_MapNodeAndNodeDetailCarryTheTier()
        {
            var node = new WireMapNode
            {
                Id = "N1", Tier = (byte)WireIntelTier.Class, Stage = (byte)WireNodeStage.Capital, XKm = 4,
            };
            var other = new WireMapNode { Id = "N2", DisplayName = "Broadmoor", Tier = (byte)WireIntelTier.Full };
            List<WireMapNode> nodes;
            List<WireMapFaction> factions;
            Assert.True(FactionsWire.TryReadMapWorld(
                FactionsWire.WriteMapWorld(new List<WireMapNode> { node, other }, null), out nodes, out factions));
            Assert.Equal((byte)WireIntelTier.Class, nodes[0].Tier);
            Assert.Equal((byte)WireNodeStage.Capital, nodes[0].Stage);
            Assert.Equal("Broadmoor", nodes[1].DisplayName);
            Assert.Equal((byte)WireIntelTier.Full, nodes[1].Tier);

            WireNodeDetail detail;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(
                new WireNodeDetail { Exists = true, Id = "N1", Tier = (byte)WireIntelTier.Contact, HoldStrength = 0.25 }),
                out detail));
            Assert.Equal((byte)WireIntelTier.Contact, detail.Tier);
            Assert.Equal(0.25, detail.HoldStrength);
        }

        [Fact]
        public void WireNodeStage_MirrorsNodeStageByOrdinal()
        {
            foreach (NodeStage s in Enum.GetValues(typeof(NodeStage)))
                Assert.Equal(s.ToString(), ((WireNodeStage)(byte)s).ToString());
            Assert.Equal(Enum.GetValues(typeof(NodeStage)).Length, Enum.GetValues(typeof(WireNodeStage)).Length);
        }
    }
}
