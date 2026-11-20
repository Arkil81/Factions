using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Factions.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Factions.Core.Tests
{
    public class PersistenceTests
    {
        private sealed class TuningDiskState
        {
            public FactionTuning Loaded;
            public FactionTuning Saved;
            public bool TransitContractMigrated;
            public string DirectoryPath;
        }

        private sealed class TestTimeSpanSecondsConverter : JsonConverter
        {
            public override bool CanConvert(Type t) { return t == typeof(TimeSpan); }
            public override void WriteJson(JsonWriter w, object v, JsonSerializer s)
                { w.WriteValue(((TimeSpan)v).TotalSeconds); }
            public override object ReadJson(JsonReader r, Type t, object existing, JsonSerializer s)
            {
                if (r.TokenType == JsonToken.Float || r.TokenType == JsonToken.Integer)
                    return TimeSpan.FromSeconds(Convert.ToDouble(r.Value));
                return existing ?? TimeSpan.Zero;
            }
        }

        [Fact]
        public void Brain_RoundTrips_AndLogIsCapped()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(10, 0, 20), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;
            var outpost = graph.Add(new Vec3(40, 0, 25), 0.4, "H2");
            outpost.OwnerFactionTag = "ACME"; outpost.HoldStrength = 0.5; outpost.Radius = 24;
            home.Inventory["Comp:SteelPlate"] = 125;
            home.ReservedInventory["Comp:SteelPlate"] = 75;

            var st = new FactionState { Tag = "ACME", FactionId = 42, PersonalityId = "settler", RngSeed = 99 };
            st.Drives[Drive.Growth] = 0.7;
            st.Drives[Drive.Security] = 0.2;
            st.Cooldowns[ActionKind.Expand] = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc);
            st.ActionRepetition[ActionKind.Claim] = 2.5;
            st.Relations["PLAYER_X"] = 0.4;
            StandingOrderFixtures.AddMineOrder(st, "Hub", 45, new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
            for (int i = 0; i < 25; i++) st.Log.Add("entry " + i);

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));
                store.SaveIndex(new[] { FactionStore.ToIndexEntry(st, DateTime.UtcNow) });

                var index = store.LoadIndex();
                Assert.Single(index);

                var graph2 = new SpatialGraph();
                FactionState st2 = FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), graph2);

                Assert.Equal("ACME", st2.Tag);
                Assert.Equal(42, st2.FactionId);
                Assert.Equal(2, graph2.OwnedBy("ACME").Count());
                Assert.Equal(0.7, st2.Drives[Drive.Growth], 6);
                Assert.True(st2.Cooldowns.ContainsKey(ActionKind.Expand));
                Assert.Equal(2.5, st2.ActionRepetition[ActionKind.Claim], 6);
                Assert.Equal(0.4, st2.RelationTo("PLAYER_X"), 6);
                StandingOrder order2 = Assert.Single(st2.StandingOrders);
                Assert.Equal(StandingOrderKind.Mine, order2.Kind);
                Assert.Equal("Hub", order2.HubNodeId);
                Assert.Equal(45, order2.PeriodSeconds);
                Assert.Equal(new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc), order2.NextScanUtc);
                Assert.Equal("SO-001", order2.Id);
                Assert.Equal(1, st2.StandingOrderSeq);
                Assert.Equal(FactionStore.DefaultLogCap, st2.Log.Count);
                var home2 = graph2.Nodes["H1"];
                Assert.Equal(125, home2.Inventory["Comp:SteelPlate"]);
                Assert.Equal(75, home2.ReservedInventory["Comp:SteelPlate"]);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ShipyardPause_BuildPriority_AndInstallPriority_RoundTrip()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8;
            home.ShipyardPaused = true;

            var entry = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "SomeHull", Priority = 4,
                CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            };
            entry.Bom["Comp:MetalGrid"] = 100;
            home.BuildQueue.Add(entry);

            var st = new FactionState { Tag = "ACME" };
            st.InstallPriority[NodeCapabilityKind.Shipyard] = 2;

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));

                var graph2 = new SpatialGraph();
                FactionState st2 = FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), graph2);

                Assert.True(graph2.Nodes["H1"].ShipyardPaused);
                Assert.Equal(4, Assert.Single(graph2.Nodes["H1"].BuildQueue).Priority);
                Assert.Equal(2, st2.InstallRankFor(NodeCapabilityKind.Shipyard));
                Assert.Equal(0, st2.InstallRankFor(NodeCapabilityKind.Refinery));   // unset reads as 0
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // FAC-264. The whole point of the opt-in is that it outlives a restart — this server takes
        // several a day — so a round-trip is not a nicety here, it is the feature. And the empty
        // case must stay OUT of the JSON: an NPC never fills this, and a key per faction per flush
        // is exactly the bloat CLAUDE.md's lean-JSON rule is about.
        // FAC-298. The trade twin of the test below, and it exists because ReservationOptIn -- the
        // other player opt-in of this shape -- shipped with NO persistence test at all. A switch
        // that silently forgets itself across a restart is worse than one that was never added:
        // the player sets it, watches convoys fly, and finds it off after the next Torch restart.
        //
        // The empty case must stay OUT of the JSON. Both flags are written only when true, so every
        // brain doc predating this feature loads with trade off, which is the behaviour it was
        // saved under.
        [Fact]
        public void TradeOptIn_RoundTrips_AndIsOmittedWhenOff()
        {
            var graph = new SpatialGraph();
            SpatialNode home = graph.Add(new Vec3(0, 0, 0), 0.6, "N130");
            home.OwnerFactionTag = "EXP";

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);

                // Both switches, and the two of the three budget knobs a player touched.
                var st = new FactionState { Tag = "EXP", IsPlayerFaction = true };
                st.TradeBuyOptIn = true;
                st.TradeSellOptIn = true;
                st.TradeIncomeFractionOverride = 0.42;
                st.TradeBudgetFloorOverride = 1234;
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("EXP"), FactionStore.DefaultLogCap));

                FactionState st2 = FactionStore.ApplyBrainDoc(store.LoadBrain("EXP"), new SpatialGraph());
                Assert.True(st2.TradeBuyOptIn);
                Assert.True(st2.TradeSellOptIn);
                Assert.Equal(0.42, st2.TradeIncomeFractionOverride);
                Assert.Equal(1234, st2.TradeBudgetFloorOverride);
                Assert.Null(st2.TradeWealthFractionOverride);    // untouched stays unset

                // The switches are independent through the file too, not one field twice.
                var sellOnly = new FactionState { Tag = "SEL", IsPlayerFaction = true };
                sellOnly.TradeSellOptIn = true;
                store.SaveBrain(FactionStore.ToBrainDoc(sellOnly, graph.OwnedBy("SEL"), FactionStore.DefaultLogCap));
                FactionState sellOnly2 = FactionStore.ApplyBrainDoc(store.LoadBrain("SEL"), new SpatialGraph());
                Assert.True(sellOnly2.TradeSellOptIn);
                Assert.False(sellOnly2.TradeBuyOptIn);

                // Lean JSON: an NPC, and a player faction that opted into nothing, write no key.
                var npc = new FactionState { Tag = "NPC" };
                BrainDoc npcDoc = FactionStore.ToBrainDoc(npc, graph.OwnedBy("NPC"), FactionStore.DefaultLogCap);
                Assert.Null(npcDoc.TradeBuyOptIn);
                Assert.Null(npcDoc.TradeSellOptIn);
                Assert.Null(npcDoc.TradeIncomeFractionOverride);

                // And a doc written before the feature existed loads as OFF rather than throwing.
                var legacy = new BrainDoc { Tag = "OLD" };
                FactionState legacy2 = FactionStore.ApplyBrainDoc(legacy, new SpatialGraph());
                Assert.False(legacy2.TradeBuyOptIn);
                Assert.False(legacy2.TradeSellOptIn);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ReinforceOptIn_RoundTrips_AndIsOmittedWhenEmpty()
        {
            var graph = new SpatialGraph();
            SpatialNode home = graph.Add(new Vec3(0, 0, 0), 0.6, "N130");
            home.OwnerFactionTag = "EXP"; home.HoldStrength = 0.4;

            var st = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            st.ReinforceOptIn["N130"] = 7;

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("EXP"), FactionStore.DefaultLogCap));

                var graph2 = new SpatialGraph();
                FactionState st2 = FactionStore.ApplyBrainDoc(store.LoadBrain("EXP"), graph2);
                Assert.Equal(7, st2.ReinforceOptIn["N130"]);

                // ... and the derived list rebuilds from it, which is what FleetManager reads.
                var world = TestSupport.World();
                SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 0.6, "N130");
                n.OwnerFactionTag = "EXP"; n.HoldStrength = 0.4;
                FactionLifecycle.ProjectPlayerReinforcePriority(st2, world);
                Assert.Equal("N130", Assert.Single(st2.ReinforcePriority).NodeId);

                var empty = new FactionState { Tag = "NPC" };
                Assert.Null(FactionStore.ToBrainDoc(empty, graph.OwnedBy("NPC"), FactionStore.DefaultLogCap).ReinforceOptIn);

                // The budget overrides round-trip as NULLABLES: absent has to load as "use the
                // world's tuning", not as a zero, because zero income + zero floor means uncapped.
                var budgeted = new FactionState { Tag = "EXP", IsPlayerFaction = true };
                budgeted.ReinforceIncomeFractionOverride = 0.75;
                budgeted.ReinforceBudgetFloorOverride = 4321;
                store.SaveBrain(FactionStore.ToBrainDoc(budgeted, graph.OwnedBy("EXP"), FactionStore.DefaultLogCap));
                FactionState budgeted2 = FactionStore.ApplyBrainDoc(store.LoadBrain("EXP"), new SpatialGraph());
                Assert.Equal(0.75, budgeted2.ReinforceIncomeFractionOverride);
                Assert.Equal(4321, budgeted2.ReinforceBudgetFloorOverride);
                Assert.Null(budgeted2.ReinforceWealthFractionOverride);   // untouched stays unset
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void OlderDocWithoutPauseOrPriority_LoadsAtTheOriginalDefaults()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8;
            home.BuildQueue.Add(new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "SomeHull",
                ExpiresUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            });

            var st = new FactionState { Tag = "ACME" };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
                Assert.Null(doc.InstallPriority);   // omitted entirely when nothing is ranked

                store.SaveBrain(doc);
                var graph2 = new SpatialGraph();
                FactionState st2 = FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), graph2);

                Assert.False(graph2.Nodes["H1"].ShipyardPaused);
                Assert.Equal(0, Assert.Single(graph2.Nodes["H1"].BuildQueue).Priority);
                Assert.Empty(st2.InstallPriority);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void BuildQueueEntry_ReservedInputsAndRecipeProgress_RoundTrip()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8;

            var entry = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "SomeHull",
                CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ExpiresUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            };
            entry.Bom["Comp:SteelPlate"] = 200;
            entry.Collected["Comp:SteelPlate"] = 40;
            entry.ReservedInputs["Ingot:Iron"] = 150;
            entry.RecipeProgress["SteelPlate"] = new BuildRecipeSlot
            {
                Accum = 0.42,
                LastTick = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc),
            };
            home.BuildQueue.Add(entry);

            var st = new FactionState { Tag = "ACME" };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));

                var graph2 = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), graph2);

                BuildQueueEntry entry2 = Assert.Single(graph2.Nodes["H1"].BuildQueue);
                Assert.Equal("Trade:001", entry2.Id);
                Assert.Equal(200, entry2.Bom["Comp:SteelPlate"]);
                Assert.Equal(40, entry2.Collected["Comp:SteelPlate"]);
                Assert.Equal(150, entry2.ReservedInputs["Ingot:Iron"]);
                BuildRecipeSlot slot2 = entry2.RecipeProgress["SteelPlate"];
                Assert.Equal(0.42, slot2.Accum, 6);
                Assert.Equal(entry.RecipeProgress["SteelPlate"].LastTick, slot2.LastTick);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void GraphTopology_RoundTripsAcrossOwners_AndRestoresUnknFrontier()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME";
            var derelict = graph.Add(new Vec3(40, 0, 0), 0.8, "N001");
            derelict.OwnerFactionTag = NeutralFaction.Tag;
            graph.Connect(home.Id, derelict.Id);

            var acme = new FactionState { Tag = "ACME" };
            var unkn = new FactionState { Tag = NeutralFaction.Tag };
            var reloadedGraph = new SpatialGraph();

            var acmeDoc = FactionStore.ToBrainDoc(acme, graph.OwnedBy(acme.Tag), FactionStore.DefaultLogCap);
            var unknDoc = FactionStore.ToBrainDoc(unkn, graph.OwnedBy(unkn.Tag), FactionStore.DefaultLogCap);
            FactionStore.ApplyBrainDoc(acmeDoc, reloadedGraph);
            FactionStore.ApplyBrainDoc(unknDoc, reloadedGraph);
            FactionStore.RestoreTopology(new[] { acmeDoc, unknDoc }, reloadedGraph);

            Assert.Contains("N001", reloadedGraph.Nodes["H1"].Neighbors);
            Assert.Contains("H1", reloadedGraph.Nodes["N001"].Neighbors);
            Assert.Equal("N001", Assert.Single(reloadedGraph.Frontier("ACME")).Id);
        }

        [Fact]
        public void GraphTopology_RestoresEdges_ForLegacyTopologyVersionZeroDoc()
        {
            // Regression test for the bug this fixes: every real faction's on-disk brain was stuck at
            // TopologyVersion=0 forever (nothing but the UNKN-only reconcileterritory transaction ever
            // bumped it), so RestoreTopology's old `doc.TopologyVersion < 1` gate discarded every
            // faction's Neighbors data on every restart. Must pass now that the gate is gone.
            var graph = new SpatialGraph();
            graph.Add(new Vec3(0, 0, 0), 0.5, "A");
            graph.Add(new Vec3(1, 0, 0), 0.5, "B");

            var doc = new BrainDoc
            {
                Tag = "ALFA",
                TopologyVersion = 0,
                Sources = new List<SourceDoc> { new SourceDoc { Id = "A", Neighbors = new List<string> { "B" } } }
            };

            int restored = FactionStore.RestoreTopology(new[] { doc }, graph);

            Assert.Equal(1, restored);
            Assert.Contains("B", graph.Nodes["A"].Neighbors);
            Assert.Contains("A", graph.Nodes["B"].Neighbors);
        }

        [Fact]
        public void ApplyBrainDoc_RatchetsTopologyVersionToOne_ExceptForUnkn()
        {
            var alfaState = FactionStore.ApplyBrainDoc(new BrainDoc { Tag = "ALFA", TopologyVersion = 0 }, new SpatialGraph());
            Assert.Equal(1, alfaState.TopologyVersion);

            var unknState = FactionStore.ApplyBrainDoc(new BrainDoc { Tag = NeutralFaction.Tag, TopologyVersion = 0 }, new SpatialGraph());
            Assert.Equal(0, unknState.TopologyVersion);
        }

        [Fact]
        public void Brain_RoundTrip_PersistsRatchetedTopologyVersion()
        {
            var graph = new SpatialGraph();
            var a = graph.Add(new Vec3(0, 0, 0), 0.5, "A");
            a.OwnerFactionTag = "ALFA";
            var b = graph.Add(new Vec3(1, 0, 0), 0.5, "B");
            b.OwnerFactionTag = "ALFA";
            graph.Connect("A", "B");

            var st = new FactionState { Tag = "ALFA", TopologyVersion = 0 }; // simulate a pre-fix legacy brain
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ALFA"), FactionStore.DefaultLogCap));

                BrainDoc reloadedDoc = store.LoadBrain("ALFA");
                var reloadedGraph = new SpatialGraph();
                FactionState reloadedState = FactionStore.ApplyBrainDoc(reloadedDoc, reloadedGraph);
                FactionStore.RestoreTopology(new[] { reloadedDoc }, reloadedGraph);

                Assert.Equal(1, reloadedState.TopologyVersion);
                Assert.Contains("B", reloadedGraph.Nodes["A"].Neighbors);

                // Flushing again now writes the ratcheted version to disk.
                store.SaveBrain(FactionStore.ToBrainDoc(reloadedState, reloadedGraph.OwnedBy("ALFA"), FactionStore.DefaultLogCap));
                Assert.Equal(1, store.LoadBrain("ALFA").TopologyVersion);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void SpatialNode_AbandonedUtc_RoundTripsThroughSaveAndLoad()
        {
            var graph = new SpatialGraph();
            var derelict = graph.Add(new Vec3(0, 0, 0), 0.5, "N001");
            derelict.OwnerFactionTag = NeutralFaction.Tag;
            derelict.AbandonedUtc = new DateTime(2026, 1, 1, 3, 0, 0, DateTimeKind.Utc);

            var unkn = new FactionState { Tag = NeutralFaction.Tag };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(unkn, graph.OwnedBy(NeutralFaction.Tag), FactionStore.DefaultLogCap));

                var reloadedGraph = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain(NeutralFaction.Tag), reloadedGraph);

                Assert.Equal(derelict.AbandonedUtc, reloadedGraph.Nodes["N001"].AbandonedUtc);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        /// <summary>Richness survives the brain file, and it is stored rather than only derived: a
        /// node the ore seam declined for reaches no evidence-driven refresh site ever again, so a
        /// value that did not round-trip would be lost on every restart.</summary>
        [Fact]
        public void SpatialNode_Richness_RoundTripsThroughSaveAndLoad()
        {
            var graph = new SpatialGraph();
            var node = graph.Add(new Vec3(0, 0, 0), 0.181, "N001");
            node.OwnerFactionTag = "F";
            // Deliberately different from ResourceValue above -- equal values would round-trip even
            // if the writer persisted one field twice. These are ice's live numbers (FAC-219).
            node.Richness = 0.545;

            var st = new FactionState { Tag = "F" };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("F"), FactionStore.DefaultLogCap));

                var reloadedGraph = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("F"), reloadedGraph);

                Assert.Equal(0.545, reloadedGraph.Nodes["N001"].Richness, 9);
                Assert.Equal(0.181, reloadedGraph.Nodes["N001"].ResourceValue, 9);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        /// <summary>A brain written before FAC-219 has no Richness key. It must load at zero rather
        /// than throwing or defaulting to something that mints -- the boot audit pass in
        /// <c>WorldModel.ReclassifyNodes</c> is what re-derives it, and it is the only thing that
        /// reaches a node the seam declined for.</summary>
        [Fact]
        public void OlderDocWithoutRichness_LoadsAtZero_ForTheBootPassToDerive()
        {
            const string legacyBrain = "{\"Tag\":\"F\",\"Sources\":[{\"Id\":\"N001\","
                                     + "\"X\":0,\"Y\":0,\"Z\":0,\"Strength\":1.0,\"Radius\":40,"
                                     + "\"ResourceValue\":0.7}]}";
            var doc = JsonConvert.DeserializeObject<BrainDoc>(legacyBrain);
            var graph = new SpatialGraph();
            FactionStore.ApplyBrainDoc(doc, graph);

            Assert.Equal(0.7, graph.Nodes["N001"].ResourceValue, 9);
            Assert.Equal(0.0, graph.Nodes["N001"].Richness);
        }

        [Fact]
        public void SandboxStationReader_ReadsConsecutiveFactionStationsWithoutSkipping()
        {
            const string checkpoint = "<Root><MyObjectBuilder_Faction><Tag>UNKN</Tag><Stations>"
                + "<MyObjectBuilder_Station><Id>1</Id></MyObjectBuilder_Station>"
                + "<MyObjectBuilder_Station><Id>2</Id></MyObjectBuilder_Station>"
                + "</Stations></MyObjectBuilder_Faction><MyObjectBuilder_Faction><Tag>ALFA</Tag><Stations>"
                + "<MyObjectBuilder_Station><Id>3</Id></MyObjectBuilder_Station>"
                + "</Stations></MyObjectBuilder_Faction></Root>";

            using (var xml = new StringReader(checkpoint))
                Assert.Equal(new[] { "1", "2" }, SandboxStationReader.ReadFactionStations(xml, NeutralFaction.Tag)
                    .Select(station => station.SelectSingleNode("./Id").InnerText));
        }

        [Fact]
        public void LegacyUnknReconciliation_SelectsEngineNodesForTrimAndEconomyStationsForRediscovery()
        {
            var unkn = new BrainDoc
            {
                Tag = NeutralFaction.Tag,
                TopologyVersion = 0,
                Sources = new List<SourceDoc>
                {
                    new SourceDoc { Id = "N001" },
                    new SourceDoc { Id = "E42" },
                    new SourceDoc { Id = "G99" },
                }
            };
            var current = new BrainDoc
            {
                Tag = "ALFA",
                TopologyVersion = 1,
                Sources = new List<SourceDoc> { new SourceDoc { Id = "N002" } }
            };

            TerritoryReconciliationPlan plan = TerritoryReconciliationPlan.Create(new[] { unkn, current });

            Assert.Equal(new[] { "N001" }, plan.LegacyUnknEngineNodeIds);
            Assert.Equal(new[] { "E42" }, plan.LegacyUnknEconomyStationIds);
            Assert.Contains("G99", plan.SerializedSourceIds);
            Assert.Contains("N002", plan.SerializedSourceIds);
        }

        [Fact]
        public void LegacyUnknReconciliation_UsesOnlyExistingUnknEconomyIdsAsRecoveryEligibility()
        {
            var unkn = new BrainDoc
            {
                Tag = NeutralFaction.Tag,
                TopologyVersion = 1,
                Sources = new List<SourceDoc>
                {
                    new SourceDoc { Id = "N001" },
                    new SourceDoc { Id = "E42" },
                    new SourceDoc { Id = "G99" },
                    new SourceDoc { Id = "E200" },
                }
            };
            var other = new BrainDoc
            {
                Tag = "ALFA",
                Sources = new List<SourceDoc> { new SourceDoc { Id = "E43" } }
            };

            Assert.Equal(new[] { "E200", "E42" }, TerritoryReconciliationPlan.UnknEconomyRecoveryIds(unkn));
            Assert.Empty(TerritoryReconciliationPlan.UnknEconomyRecoveryIds(other));
        }

        [Fact]
        public void LegacyUnknReconciliation_DetectsPersistedNonUnknCandidateCollisions()
        {
            var unkn = new BrainDoc { Tag = NeutralFaction.Tag, Sources = new List<SourceDoc> { new SourceDoc { Id = "E42" } } };
            var alfa = new BrainDoc { Tag = "ALFA", Sources = new List<SourceDoc> { new SourceDoc { Id = "E42" } } };
            var beta = new BrainDoc { Tag = "BETA", Sources = new List<SourceDoc> { new SourceDoc { Id = "E200" } } };

            Assert.Equal(new[] { "E42:ALFA" }, TerritoryReconciliationPlan.NonUnknSourceCollisions(
                new[] { unkn, alfa, beta }, new[] { new SourceDoc { Id = "E42" }, new SourceDoc { Id = "E999" } }));
        }

        [Fact]
        public void LegacyUnknReconciliation_ReplacesAllUnknSourcesWithValidatedEconomyCandidatesOnly()
        {
            var unkn = new BrainDoc
            {
                Tag = NeutralFaction.Tag,
                TopologyVersion = 0,
                Sources = new List<SourceDoc>
                {
                    new SourceDoc { Id = "N001" },
                    new SourceDoc { Id = "E42" },
                    new SourceDoc { Id = "G99" },
                }
            };
            var other = new BrainDoc
            {
                Tag = "ALFA",
                TopologyVersion = 0,
                Sources = new List<SourceDoc> { new SourceDoc { Id = "N002" }, new SourceDoc { Id = "E43" } }
            };
            var candidates = new List<SourceDoc>
            {
                new SourceDoc { Id = "E100", StableStationId = 100 },
                new SourceDoc { Id = "E200", StableStationId = 200 },
            };

            TerritoryReconciliationPlan plan = TerritoryReconciliationPlan.Create(new[] { unkn, other });

            Assert.Equal(3, plan.ReplaceAllUnknSources(unkn, candidates));
            Assert.Equal(1, unkn.TopologyVersion);
            Assert.Equal(new[] { "E100", "E200" }, unkn.Sources.Select(s => s.Id).OrderBy(id => id));
            Assert.Equal(0, other.TopologyVersion);
            Assert.Equal(new[] { "N002", "E43" }, other.Sources.Select(s => s.Id));
        }

        [Fact]
        public void TrimUnserializedNodes_RemovesGraphOnlyNodeAndRetainedBacklinks()
        {
            var graph = new SpatialGraph();
            graph.Add(new Vec3(0, 0, 0), 0.5, "KEEP");
            graph.Add(new Vec3(1, 0, 0), 0.5, "DROP");
            graph.Connect("KEEP", "DROP");

            int removed = graph.TrimToSourceIds(new HashSet<string> { "KEEP" });

            Assert.Equal(1, removed);
            Assert.True(graph.Nodes.ContainsKey("KEEP"));
            Assert.False(graph.Nodes.ContainsKey("DROP"));
            Assert.Empty(graph.Nodes["KEEP"].Neighbors);
        }

        [Fact]
        public void DemandHistoryAndNeglectClocks_RoundTrip()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(10, 0, 20), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;
            home.DemandHistory["Ingot:Iron"] = 42.5;
            home.LastHaulDepartedUtc = new DateTime(2026, 1, 1, 3, 0, 0, DateTimeKind.Utc);
            home.LastHaulArrivedUtc = new DateTime(2026, 1, 1, 4, 30, 0, DateTimeKind.Utc);

            var st = new FactionState { Tag = "ACME" };

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));

                var graph2 = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), graph2);

                var home2 = graph2.Nodes["H1"];
                Assert.Equal(42.5, home2.DemandHistory["Ingot:Iron"], 6);
                Assert.Equal(home.LastHaulDepartedUtc, home2.LastHaulDepartedUtc);
                Assert.Equal(home.LastHaulArrivedUtc, home2.LastHaulArrivedUtc);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Brain_RoundTripsALegsTierTable_AndOmitsAnOpenSpaceOne()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME";
            var st = new FactionState { Tag = "ACME" };

            var zones = new LegZoneProfile { HasGeometry = true };
            zones.Add(TransitTier.Gravity, 40.0);
            zones.Add(TransitTier.DeepSpace, 600.0);
            zones.Add(TransitTier.Orbit, 90.0);

            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "ACME", OriginNodeId = "H1", TargetNodeId = "E1",
                CurrentStopKind = FleetStopKind.Service,
                LegZones = zones,
            });

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Equal(new double[] { 40.0, 600.0, 90.0 }, doc.PendingFleets[0].ZoneRunKm);
            Assert.Equal(new int[] { 0, 2, 1 }, doc.PendingFleets[0].ZoneRunTiers);

            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            LegZoneProfile loaded = back.PendingFleets[0].LegZones;
            Assert.NotNull(loaded);
            Assert.Equal(730.0, loaded.TotalKm, 6);
            Assert.Equal(40.0, loaded.KmIn(TransitTier.Gravity), 6);
            Assert.Equal(90.0, loaded.KmIn(TransitTier.Orbit), 6);

            // A leg that is entirely open space says nothing its distance does not, so it is left out
            // of the JSON entirely -- and a fleet that loads without a table is scheduled on its
            // distance, exactly as one saved before the tiers existed.
            PendingFleetAction plain = st.PendingFleets[0];
            plain.LegZones = LegZoneProfile.PlainDeepSpace(500.0);
            st.PendingFleets[0] = plain;
            BrainDoc plainDoc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Null(plainDoc.PendingFleets[0].ZoneRunKm);
            Assert.Null(plainDoc.PendingFleets[0].ZoneRunTiers);
            Assert.Null(FactionStore.ApplyBrainDoc(plainDoc, new SpatialGraph()).PendingFleets[0].LegZones);
        }

        [Fact]
        public void Brain_RoundTripsFleetItinerary_AndLeavesLegacyFleetUnmigrated()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;
            var st = new FactionState { Tag = "ACME" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "ACME", OriginNodeId = "H1", TargetNodeId = "E1",
                CurrentStopKind = FleetStopKind.Service,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = "H1", Kind = FleetStopKind.Return }
                }
            });

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Equal("Service", doc.PendingFleets[0].CurrentStopKind);
            Assert.Equal("Return", doc.PendingFleets[0].RemainingStops[0].Kind);

            FactionState roundTripped = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal(FleetStopKind.Service, roundTripped.PendingFleets[0].CurrentStopKind);
            Assert.Single(roundTripped.PendingFleets[0].RemainingStops);
            Assert.Equal(FleetStopKind.Return, roundTripped.PendingFleets[0].RemainingStops[0].Kind);

            st.PendingFleets[0] = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "ACME", OriginNodeId = "H1", TargetNodeId = "E1",
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { Kind = FleetStopKind.Navigation, HasPosition = true, PositionKm = new Vec3(12, 34, 56) },
                    new FleetStop { NodeId = "H1", Kind = FleetStopKind.Return }
                }
            };
            BrainDoc navigationDoc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            FactionState navigationRoundTrip = FactionStore.ApplyBrainDoc(navigationDoc, new SpatialGraph());
            Assert.Equal(FleetStopKind.Navigation, navigationRoundTrip.PendingFleets[0].RemainingStops[0].Kind);
            Assert.True(navigationRoundTrip.PendingFleets[0].RemainingStops[0].HasPosition);
            Assert.Equal(12, navigationRoundTrip.PendingFleets[0].RemainingStops[0].PositionKm.X);
            Assert.Equal(34, navigationRoundTrip.PendingFleets[0].RemainingStops[0].PositionKm.Y);
            Assert.Equal(56, navigationRoundTrip.PendingFleets[0].RemainingStops[0].PositionKm.Z);

            var legacyDoc = new BrainDoc
            {
                Tag = "LEGACY",
                PendingFleets = new List<PendingFleetDoc>
                {
                    new PendingFleetDoc { Kind = "Raid", ActorTag = "LEGACY", TargetNodeId = "E1" }
                }
            };
            FactionState legacy = FactionStore.ApplyBrainDoc(legacyDoc, new SpatialGraph());
            Assert.Equal(FleetStopKind.Service, legacy.PendingFleets[0].CurrentStopKind);
            Assert.Null(legacy.PendingFleets[0].RemainingStops);
        }

        /// <summary>FAC-277: the node a hull comes home to survives a reboot, and a fleet that has
        /// none writes nothing -- the field is absent from the file, which is exactly what every
        /// fleet persisted before it existed looks like.</summary>
        [Fact]
        public void Brain_RoundTripsFleetReturnNode_AndOmitsItWhenAbsent()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;
            var st = new FactionState { Tag = "ACME" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "ACME", OriginNodeId = "H1", ReturnNodeId = "ORE1",
                TargetNodeId = "D1", CurrentStopKind = FleetStopKind.Service,
            });

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Equal("ORE1", doc.PendingFleets[0].ReturnNodeId);
            FactionState roundTripped = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal("ORE1", roundTripped.PendingFleets[0].ReturnNodeId);

            st.PendingFleets[0] = new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "ACME", OriginNodeId = "H1", TargetNodeId = "D1",
            };
            BrainDoc plainDoc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Null(plainDoc.PendingFleets[0].ReturnNodeId);
            Assert.Null(FactionStore.ApplyBrainDoc(plainDoc, new SpatialGraph()).PendingFleets[0].ReturnNodeId);
        }

        /// <summary>A squadron's escort grid IDs and its casualty list survive a reboot -- the only
        /// reference the host keeps to the escort grids, without which a restart mid-observation
        /// leaves them orphaned -- and a fleet with neither writes neither field.</summary>
        [Fact]
        public void Brain_RoundTripsSquadronEscortsAndSurvivors_AndOmitsBothWhenAbsent()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;
            var st = new FactionState { Tag = "ACME" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "ACME", OriginNodeId = "H1", TargetNodeId = "E1",
                MaterialisedEntityId = 7001L,
                Squadron = new List<string> { "BigGunship", "Fighter", "Fighter" },
                // 0 is a stale slot the host has already dropped its escort from; it must not persist.
                EscortEntityIds = new List<long> { 7002L, 0L, 7003L },
                SurvivingHulls = new List<string> { "Fighter", "Fighter" },
            });

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Equal(new List<long> { 7002L, 7003L }, doc.PendingFleets[0].EscortEntityIds);
            Assert.Equal(new List<string> { "Fighter", "Fighter" }, doc.PendingFleets[0].SurvivingHulls);

            FactionState roundTripped = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal(new List<long> { 7002L, 7003L }, roundTripped.PendingFleets[0].EscortEntityIds);
            Assert.Equal(new List<string> { "Fighter", "Fighter" }, roundTripped.PendingFleets[0].SurvivingHulls);
            Assert.Equal(new List<string> { "Fighter", "Fighter" }, roundTripped.PendingFleets[0].HullsToReturn);

            // Serialised with default settings, so the only thing that can omit these two fields is
            // their own NullValueHandling.Ignore -- not the store's global one.
            string populatedJson = JsonConvert.SerializeObject(doc);
            Assert.Contains("EscortEntityIds", populatedJson, StringComparison.Ordinal);
            Assert.Contains("SurvivingHulls", populatedJson, StringComparison.Ordinal);

            st.PendingFleets[0] = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "ACME", OriginNodeId = "H1", TargetNodeId = "E1",
                Squadron = new List<string> { "BigGunship", "Fighter", "Fighter" },
            };
            BrainDoc plainDoc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Null(plainDoc.PendingFleets[0].EscortEntityIds);
            Assert.Null(plainDoc.PendingFleets[0].SurvivingHulls);

            string plainJson = JsonConvert.SerializeObject(plainDoc);
            Assert.DoesNotContain("EscortEntityIds", plainJson, StringComparison.Ordinal);
            Assert.DoesNotContain("SurvivingHulls", plainJson, StringComparison.Ordinal);

            FactionState plainRoundTrip = FactionStore.ApplyBrainDoc(plainDoc, new SpatialGraph());
            Assert.Null(plainRoundTrip.PendingFleets[0].EscortEntityIds);
            Assert.Null(plainRoundTrip.PendingFleets[0].SurvivingHulls);
        }

        /// <summary>An empty survivor list means "every member was lost", which is not the null
        /// "none lost yet" -- it has to survive the round trip as itself.</summary>
        [Fact]
        public void Brain_RoundTripsEmptySurvivingHulls_AsWipedOutRatherThanIntact()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;
            var st = new FactionState { Tag = "ACME" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "ACME", OriginNodeId = "H1", TargetNodeId = "E1",
                Squadron = new List<string> { "BigGunship" },
                SurvivingHulls = new List<string>(),
            });

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            // Through the JSON boundary the store actually writes: NullValueHandling.Ignore drops a
            // null list, and an empty one has to come back as an empty list rather than as nothing.
            string json = JsonConvert.SerializeObject(doc, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            Assert.Contains("SurvivingHulls", json, StringComparison.Ordinal);
            FactionState roundTripped = FactionStore.ApplyBrainDoc(
                JsonConvert.DeserializeObject<BrainDoc>(json), new SpatialGraph());

            Assert.NotNull(roundTripped.PendingFleets[0].SurvivingHulls);
            Assert.Empty(roundTripped.PendingFleets[0].SurvivingHulls);
            Assert.Empty(roundTripped.PendingFleets[0].HullsToReturn);
        }

        [Fact]
        public void Brain_RoundTripsPickupStop_ItemKeyAndQuantity()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;
            var st = new FactionState { Tag = "ACME" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "ACME", OriginNodeId = "E1", TargetNodeId = "H1",
                CurrentStopKind = FleetStopKind.Pickup,
                PickupItemKey = "Ingot:Iron",
                PickupQuantity = 250,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = "D1", Kind = FleetStopKind.Service }
                }
            });

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Equal("Pickup", doc.PendingFleets[0].CurrentStopKind);
            Assert.Equal("Ingot:Iron", doc.PendingFleets[0].PickupItemKey);
            Assert.Equal(250, doc.PendingFleets[0].PickupQuantity);

            FactionState roundTripped = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal(FleetStopKind.Pickup, roundTripped.PendingFleets[0].CurrentStopKind);
            Assert.Equal("Ingot:Iron", roundTripped.PendingFleets[0].PickupItemKey);
            Assert.Equal(250, roundTripped.PendingFleets[0].PickupQuantity);

            // A fleet with no Pickup leg round-trips these fields at their zero-value default.
            st.PendingFleets[0] = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "ACME", OriginNodeId = "H1", TargetNodeId = "E1",
            };
            BrainDoc plainDoc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            FactionState plainRoundTrip = FactionStore.ApplyBrainDoc(plainDoc, new SpatialGraph());
            Assert.Null(plainRoundTrip.PendingFleets[0].PickupItemKey);
            Assert.Equal(0, plainRoundTrip.PendingFleets[0].PickupQuantity);
        }

        [Fact]
        public void Brain_RoundTripsMineDwellStop_WeightsAndTargetLiters_AndNodeCooldownDeadline()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "Ore1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;
            home.MineCooldownUntilUtc = new DateTime(2026, 1, 2, 3, 0, 0, DateTimeKind.Utc);
            var st = new FactionState { Tag = "ACME" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "ACME", OriginNodeId = "Ore1", TargetNodeId = "Ore1",
                CurrentStopKind = FleetStopKind.Mine,
                MineWeights = new Dictionary<string, double> { ["Ore:Iron"] = 0.6, ["Ore:Nickel"] = 0.4 },
                MineTargetLiters = 5000,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = "Dest1", Kind = FleetStopKind.Service }
                }
            });

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Equal("Mine", doc.PendingFleets[0].CurrentStopKind);
            Assert.Equal(0.6, doc.PendingFleets[0].MineWeights["Ore:Iron"]);
            Assert.Equal(0.4, doc.PendingFleets[0].MineWeights["Ore:Nickel"]);
            Assert.Equal(5000, doc.PendingFleets[0].MineTargetLiters);
            Assert.Equal(home.MineCooldownUntilUtc, doc.Sources[0].MineCooldownUntilUtc);

            var graph2 = new SpatialGraph();
            FactionState roundTripped = FactionStore.ApplyBrainDoc(doc, graph2);
            Assert.Equal(FleetStopKind.Mine, roundTripped.PendingFleets[0].CurrentStopKind);
            Assert.Equal(0.6, roundTripped.PendingFleets[0].MineWeights["Ore:Iron"]);
            Assert.Equal(0.4, roundTripped.PendingFleets[0].MineWeights["Ore:Nickel"]);
            Assert.Equal(5000, roundTripped.PendingFleets[0].MineTargetLiters);
            Assert.Equal(home.MineCooldownUntilUtc, graph2.Nodes["Ore1"].MineCooldownUntilUtc);
        }

        [Fact]
        public void Brain_RoundTripsMineWanderUp_AndOmitsItInDeepSpace()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "Ore1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;
            var st = new FactionState { Tag = "ACME" };
            var inGravity = new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "ACME", OriginNodeId = "Ore1", TargetNodeId = "Ore1",
                CurrentStopKind = FleetStopKind.Mine, MineWanderUp = new Vec3(0.6, 0, 0.8),
            };
            st.PendingFleets.Add(inGravity);
            var inSpace = inGravity;
            inSpace.MineWanderUp = null;
            st.PendingFleets.Add(inSpace);

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            Assert.Null(doc.PendingFleets[1].MineWanderUp);
            Assert.DoesNotContain("MineWanderUp", Newtonsoft.Json.JsonConvert.SerializeObject(doc.PendingFleets[1]),
                                  StringComparison.Ordinal);

            FactionState roundTripped = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.True(roundTripped.PendingFleets[0].MineWanderUp.HasValue);
            Assert.Equal(0.6, roundTripped.PendingFleets[0].MineWanderUp.Value.X);
            Assert.Equal(0.8, roundTripped.PendingFleets[0].MineWanderUp.Value.Z);
            Assert.False(roundTripped.PendingFleets[1].MineWanderUp.HasValue);
        }

        [Fact]
        public void Brain_RoundTrips_CampaignAndMusterFocusState()
        {
            // Regression coverage for a gap found live 2026-07-31: CampaignFocusRegion/Priority/
            // SetAtUtc and MilitaryPosture/MusterFocusTag/Priority/SetAtUtc were never mapped into
            // BrainDoc, so they silently reset to default on every restart despite being intended
            // to persist (MilitaryPosture in particular decays over hours, not restarts).
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;

            DateTime campaignSet = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime musterSet   = new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Utc);

            var st = new FactionState { Tag = "ACME", FactionId = 42, PersonalityId = "settler", RngSeed = 99 };
            st.CampaignFocusRegion   = "N001";
            st.CampaignFocusPriority = 0.65;
            st.CampaignSetAtUtc      = campaignSet;
            st.MilitaryPosture       = 0.42;
            st.MusterFocusTag        = "RIVAL";
            st.MusterFocusPriority   = 0.55;
            st.MusterSetAtUtc        = musterSet;

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));

                var graph2 = new SpatialGraph();
                FactionState st2 = FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), graph2);

                Assert.Equal("N001", st2.CampaignFocusRegion);
                Assert.Equal(0.65, st2.CampaignFocusPriority, 6);
                Assert.Equal(campaignSet, st2.CampaignSetAtUtc);
                Assert.Equal(0.42, st2.MilitaryPosture, 6);
                Assert.Equal("RIVAL", st2.MusterFocusTag);
                Assert.Equal(0.55, st2.MusterFocusPriority, 6);
                Assert.Equal(musterSet, st2.MusterSetAtUtc);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Brain_RoundTrips_DefaultFocusState_OmitsFieldsAndStaysAtZero()
        {
            // The omit-when-default half of the same contract: a faction that never fired Campaign/
            // Muster must round-trip to the same defaults, not leak a stray 0.0/null-tag artifact.
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;

            var st = new FactionState { Tag = "ACME", FactionId = 42, PersonalityId = "settler", RngSeed = 99 };

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
                Assert.Null(doc.CampaignFocusRegion);
                Assert.Null(doc.MilitaryPosture);
                Assert.Null(doc.MusterFocusTag);
                store.SaveBrain(doc);

                var graph2 = new SpatialGraph();
                FactionState st2 = FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), graph2);

                Assert.Null(st2.CampaignFocusRegion);
                Assert.Equal(0.0, st2.CampaignFocusPriority);
                Assert.Equal(0.0, st2.MilitaryPosture);
                Assert.Null(st2.MusterFocusTag);
                Assert.Equal(0.0, st2.MusterFocusPriority);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void LoadTuning_MigratesTuneVersionSixReferenceContract_AndPersistsIt()
        {
            TuningDiskState state = LoadTuningFromDisk(
                6,
                5,
                new Dictionary<string, object>
                {
                    { "ReferenceStockOre", 200 },
                    { "ReferenceStockProcessedGood", 100 },
                    { "ReferenceStockRareOre", 40 },
                    { "ReferenceStockIngot", 60 }
                },
                new Dictionary<string, object>
                {
                    { "ReferenceStockOre", 1000 },
                    { "ReferenceStockProcessedGood", 500 },
                    { "ReferenceStockRareOre", 200 },
                    { "ReferenceStockIngot", 300 }
                });

            try
            {
                Assert.Equal(6, state.Loaded.TuneVersion);
                Assert.Equal(200, state.Loaded.ReferenceStockOre);
                Assert.Equal(100, state.Loaded.ReferenceStockProcessedGood);
                Assert.Equal(40, state.Loaded.ReferenceStockRareOre);
                Assert.Equal(60, state.Loaded.ReferenceStockIngot);

                Assert.Equal(6, state.Saved.TuneVersion);
                Assert.Equal(200, state.Saved.ReferenceStockOre);
                Assert.Equal(100, state.Saved.ReferenceStockProcessedGood);
                Assert.Equal(40, state.Saved.ReferenceStockRareOre);
                Assert.Equal(60, state.Saved.ReferenceStockIngot);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        /// <summary>A CHANGED default never reaches a running server on its C# initialiser: LoadTuning
        /// merges defaults, then tuning.json overwrites them, and the host rewrites tuning.json every
        /// load -- so a value an older build persisted wins forever. The grant is the sharpest case in
        /// the file, because <c>FactionTuning.CentralBankGrantAmount</c>'s initialiser has read
        /// 1,000,000 while every live server ran 250,000. Reading the C# default and concluding the
        /// number was live is the mistake this test exists to make impossible.</summary>
        [Fact]
        public void ACentralBankGrantPersistedByAnOlderBuild_IsMigratedRatherThanKept()
        {
            FactionTuning t = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 41, \"CentralBankGrantAmount\": 250000 }");

            Assert.Equal(58, t.TuneVersion);
            Assert.Equal(1000000L, t.CentralBankGrantAmount);
        }

        /// <summary>TuneVersion 55: every live server persisted measure mode and a 1.5 import premium,
        /// so the armed draw with the fallback off reaches an existing tuning.json only through the
        /// gate; an operator override written at the current version is kept.</summary>
        [Fact]
        public void AMeasureModeFuelDrawPersistedByAnOlderBuild_IsMigratedToDrawWithNoImport_AndACurrentOverrideIsKept()
        {
            FactionTuning migrated = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 54, \"FuelDrawMode\": 1, \"FuelImportPremium\": 1.5 }");
            Assert.Equal(58, migrated.TuneVersion);
            Assert.Equal(FuelDrawModes.Draw, migrated.FuelDrawMode);
            Assert.Equal(0.0, migrated.FuelImportPremium, 9);

            FactionTuning kept = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 58, \"FuelDrawMode\": 1, \"FuelImportPremium\": 1.5 }");
            Assert.Equal(FuelDrawModes.Measure, kept.FuelDrawMode);
            Assert.Equal(1.5, kept.FuelImportPremium, 9);
        }

        /// <summary>TuneVersion 56: the right-sized hull search widens 4 → 8 on every existing server,
        /// which persisted 4 when the key arrived; an override at the current version is kept.</summary>
        [Fact]
        public void AHaulSmallHullSearchWidthPersistedAt4_IsMigratedTo8_AndACurrentOverrideIsKept()
        {
            FactionTuning migrated = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 55, \"HaulSmallHullSearchNodes\": 4 }");
            Assert.Equal(58, migrated.TuneVersion);
            Assert.Equal(8, migrated.HaulSmallHullSearchNodes);

            FactionTuning kept = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 58, \"HaulSmallHullSearchNodes\": 4 }");
            Assert.Equal(4, kept.HaulSmallHullSearchNodes);
        }

        /// <summary>The negative control for the test above: at an EQUAL TuneVersion nothing migrates,
        /// so a live operator override survives. Without this, the assertion above would also pass on
        /// an implementation that ignored the version gate and stamped the default unconditionally.</summary>
        [Fact]
        public void AnOperatorGrantOverrideSurvivesWhenTheVersionHasNotMoved()
        {
            FactionTuning t = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 58, \"CentralBankGrantAmount\": 250000 }");

            Assert.Equal(250000L, t.CentralBankGrantAmount);
        }

        /// <summary>TuneVersion 46: the Guard strength gate's margin moves 1.2 → 0.9 on every existing
        /// server (the first build with the key persisted 1.2, and the running host rewrites
        /// tuning.json on every load), while an operator override at the current version is kept.</summary>
        [Fact]
        public void AGuardCommitRatioPersistedByTheFirstBuild_IsMigratedTo09_AndAnOverrideAtTheCurrentVersionIsKept()
        {
            FactionTuning migrated = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 45, \"GuardCommitRatio\": 1.2 }");
            Assert.Equal(58, migrated.TuneVersion);
            Assert.Equal(0.9, migrated.GuardCommitRatio, 9);

            FactionTuning kept = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 58, \"GuardCommitRatio\": 1.0 }");
            Assert.Equal(1.0, kept.GuardCommitRatio, 9);
        }

        /// <summary>TuneVersion 47: Guard's budget gains its treasury term. Every host persisted 0 —
        /// an income-only budget — and a defence is now marshalled in several convoys, so a faction
        /// whose income is small beside its treasury could field the hulls and not afford to send
        /// them. The negative control is the same one the test above uses: at the current version an
        /// operator's own 0 survives.</summary>
        [Fact]
        public void AGuardWealthFractionOfZero_IsMigratedToTheTreasuryTerm_AndAnOverrideAtTheCurrentVersionIsKept()
        {
            FactionTuning migrated = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 46, \"GuardWealthFractionPerWindow\": 0.0 }");
            Assert.Equal(58, migrated.TuneVersion);
            Assert.Equal(0.001, migrated.GuardWealthFractionPerWindow, 9);

            FactionTuning kept = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 58, \"GuardWealthFractionPerWindow\": 0.0 }");
            Assert.Equal(0.0, kept.GuardWealthFractionPerWindow, 9);
        }

        /// <summary>TuneVersion 48: the ship-activity cap moves 3.0 -> 10.0 on every existing server.
        /// The build that introduced the key persisted 3.0, and at one credit per fleet stop that is
        /// three stops -- which a world running ~1,500 ships clears nearly everywhere, so the traffic
        /// channel carried two thirds of the estate rather than the trafficked tail it is for.
        ///
        /// <para>Without the gate entry this change is INERT on a live server: LoadTuning merges the
        /// defaults and then lets tuning.json override them, and the host rewrites tuning.json on
        /// every load, so the persisted 3.0 would win over the new C# default forever. This test is
        /// the one that fails if the gate entry is dropped.</para></summary>
        [Fact]
        public void AShipActivityCapPersistedByTheFirstBuild_IsMigratedTo10_AndAnOverrideAtTheCurrentVersionIsKept()
        {
            FactionTuning migrated = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 47, \"ShipActivityCreditCap\": 3.0 }");
            Assert.Equal(58, migrated.TuneVersion);
            Assert.Equal(10.0, migrated.ShipActivityCreditCap, 9);

            FactionTuning kept = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 58, \"ShipActivityCreditCap\": 3.0 }");
            Assert.Equal(3.0, kept.ShipActivityCreditCap, 9);
        }

        /// <summary>TuneVersion 49: the ship-activity half-life moves 3600 -> 900 on every existing
        /// server. Credit settles at ~1.44x a node's stops per hour scaled by this span, so at 3600
        /// the mean node's settled level sat at a cap of 10 and 55% of the estate saturated —
        /// measured live. The half-life, not the cap, is what makes the channel measure a rate.
        ///
        /// <para>Same inert-deploy hazard as the cap above: without this gate entry the persisted
        /// 3600 wins over the new C# default forever.</para></summary>
        [Fact]
        public void AShipActivityHalfLifePersistedByAnEarlierBuild_IsMigratedTo900_AndAnOverrideAtTheCurrentVersionIsKept()
        {
            FactionTuning migrated = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 48, \"ShipActivityHalfLifeSeconds\": 3600.0 }");
            Assert.Equal(58, migrated.TuneVersion);
            Assert.Equal(900.0, migrated.ShipActivityHalfLifeSeconds, 9);

            FactionTuning kept = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 58, \"ShipActivityHalfLifeSeconds\": 3600.0 }");
            Assert.Equal(3600.0, kept.ShipActivityHalfLifeSeconds, 9);
        }

        /// <summary>TuneVersion 50: the ship-activity gain moves 0.30 -> 1.20 on every existing
        /// server. Break-even is <c>HoldDecayPerHour / ShipActivityHoldPerHour</c> as a share of the
        /// cap, so 0.30 put it at 0.8 — a node had to hold 8 of 10 credit merely to stand still.
        /// Once TuneVersion 49 made credit rate-limited that left the mean node at -0.16 hold/hour
        /// and the estate shrinking, which was never a goal. At 1.20 break-even is 0.2.
        ///
        /// <para>Same inert-deploy hazard as the two keys above: without this gate entry the
        /// persisted 0.30 wins over the new C# default forever.</para></summary>
        [Fact]
        public void AShipActivityGainPersistedByAnEarlierBuild_IsMigratedTo12_AndAnOverrideAtTheCurrentVersionIsKept()
        {
            FactionTuning migrated = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 49, \"ShipActivityHoldPerHour\": 0.30 }");
            Assert.Equal(58, migrated.TuneVersion);
            Assert.Equal(1.20, migrated.ShipActivityHoldPerHour, 9);

            FactionTuning kept = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 58, \"ShipActivityHoldPerHour\": 0.30 }");
            Assert.Equal(0.30, kept.ShipActivityHoldPerHour, 9);
        }

        /// <summary>The break-even the shipped pair is chosen for: a node is in net credit exactly
        /// when its credit share reaches <c>HoldDecayPerHour / ShipActivityHoldPerHour</c>. Pins the
        /// RELATIONSHIP rather than either number, so a future move of decay or gain that puts
        /// break-even back above the estate's reach fails here rather than live.</summary>
        [Fact]
        public void ShippedTuning_PutsShipActivityBreakEvenWellBelowTheCap()
        {
            FactionTuning t = TestSupport.Tuning();

            double breakEvenShare = t.HoldDecayPerHour / t.ShipActivityHoldPerHour;
            Assert.Equal(0.20, breakEvenShare, 9);
            Assert.True(breakEvenShare <= 0.35,
                "break-even at " + breakEvenShare.ToString("0.00") + " of the cap leaves the typical "
                + "node in deficit once credit is rate-limited");
        }

        [Fact]
        public void ShippedTuningDefaults_ExposeReferenceDefaults()
        {
            // TuneVersion itself just needs to match the shipped file's current counter (it's bumped
            // on every unrelated default-value change, most recently to restate the two gas fallback
            // values per kilolitre rather than per litre); this test's real
            // purpose is pinning the Reference*/ItemKind values below. Failing here on a deliberate
            // bump is the intended tripwire: a bump re-applies every field in
            // ApplyVersionedContractDefaults over whatever the live tuning.json holds.
            FactionTuning tuning = TestSupport.Tuning();

            Assert.Equal(58, tuning.TuneVersion);
            Assert.Equal(15, tuning.MaxRefineryUpgrades);
            // The seven brain-action cooldowns the test server ran at. Pinned here because they are
            // the whole content of the 36 -> 37 bump: a value drifting back to hours is the failure
            // this test exists to catch.
            Assert.Equal(TimeSpan.FromSeconds(60), tuning.ExpandCooldown);
            Assert.Equal(TimeSpan.FromSeconds(60), tuning.ClaimCooldown);
            Assert.Equal(TimeSpan.FromSeconds(60), tuning.DevelopCooldown);
            Assert.Equal(TimeSpan.FromSeconds(60), tuning.WithdrawCooldown);
            Assert.Equal(TimeSpan.FromSeconds(60), tuning.NegotiateCooldown);
            Assert.Equal(TimeSpan.FromSeconds(60), tuning.FoundCooldown);
            Assert.Equal(TimeSpan.FromSeconds(60), tuning.ActionCooldown);
            // The modelled machine fitment: four fully connected speed modules (8 ports x 0.5).
            Assert.Equal(4.0, tuning.ProductionProductivityBonus, 9);
            Assert.Equal(10.0, tuning.ProductionSlotInputHorizonSeconds, 9);
            Assert.Equal(0.24, tuning.HoldDecayPerHour, 9);
            Assert.Equal(TimeSpan.FromHours(8), tuning.UnknGraceDuration);
            Assert.Equal(3.0, tuning.TargetStockCoveragePeriods, 9);
            Assert.Equal(3.0, tuning.TargetStockDownstreamWeight, 9);
            Assert.Equal(1.0, tuning.TargetStockFlowFloorUnits, 9);
            Assert.Equal(200, tuning.ReferenceStockFor(ItemKind.Ore));
            Assert.Equal(100, tuning.ReferenceStockFor(ItemKind.ProcessedGood));
            Assert.Equal(40, tuning.ReferenceStockFor(ItemKind.RareOre));
            Assert.Equal(60, tuning.ReferenceStockFor(ItemKind.Ingot));
            // For gas the floor IS the shelf, and the oracle applies it unconditionally: 10,000,000 L
            // of hydrogen and 2,000,000 L of oxygen, the operator's 2026-08-29 throttle on how much
            // of the world's ice the chain may consume. These were reached through the capability
            // credit budget until TuneVersion 53 and are now stated here. A value drifting back
            // toward the ore scale silently throttles the whole gas chain.
            Assert.Equal(10_000_000, tuning.ReferenceStockFor(ItemKind.Hydrogen));
            Assert.Equal(2_000_000, tuning.ReferenceStockFor(ItemKind.Oxygen));
            Assert.Equal(4608.0, tuning.FleetAccelKmPerMin2);
            Assert.Equal(60.0, tuning.FlushEveryRealSeconds);
        }

        [Fact]
        public void LoadTuning_MigratesLegacyPerPassHoldDecayToHourlyRate()
        {
            TuningDiskState state = LoadTuningFromDisk(
                20,
                19,
                new Dictionary<string, object> { { "HoldDecayPerHour", 0.24 } },
                new Dictionary<string, object> { { "HoldDecayRate", 0.02 } },
                includeLiveHoldDecayPerHour: false);
            try
            {
                Assert.False(state.TransitContractMigrated);
                Assert.Equal(20, state.Loaded.TuneVersion);
                Assert.Equal(0.24, state.Loaded.HoldDecayPerHour, 9);
                Assert.Equal(20, state.Saved.TuneVersion);
                Assert.Equal(0.24, state.Saved.HoldDecayPerHour, 9);

                JObject savedJson = JObject.Parse(File.ReadAllText(Path.Combine(state.DirectoryPath, "tuning.json")));
                Assert.Null(savedJson["HoldDecayRate"]);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_ReschedulesPendingFleets_WhenALaterTransitContractIsCrossed()
        {
            // 32 = legs billed for the line actually flown. A server already past the version-15
            // contract still has to be carried across this one.
            TuningDiskState state = LoadTuningFromDisk(
                32,
                31,
                new Dictionary<string, object>(),
                new Dictionary<string, object>());
            try
            {
                Assert.True(state.TransitContractMigrated);
                Assert.Equal(32, state.Saved.TuneVersion);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        /// <summary>The 36 -> 37 bump. Every live server's tuning.json holds the slow cooldowns a
        /// previous build persisted, and LoadTuning lets tuning.json override the shipped defaults --
        /// so without an explicit line in ApplyVersionedContractDefaults the migrated values would
        /// never reach a running server, and the only place the change would be visible is the repo.
        /// Asserts the saved file too: an in-memory-only migration is rewritten back to the slow
        /// value on the next load.</summary>
        [Fact]
        public void LoadTuning_VersionBump_MigratesBrainActionCooldownsOverPersistedSlowValues()
        {
            TuningDiskState state = LoadTuningFromDisk(
                37,
                36,
                new Dictionary<string, object>
                {
                    { "ExpandCooldown", 60.0 },
                    { "ClaimCooldown", 60.0 },
                    { "DevelopCooldown", 60.0 },
                    { "WithdrawCooldown", 60.0 },
                    { "NegotiateCooldown", 60.0 },
                    { "FoundCooldown", 60.0 },
                    { "ActionCooldown", 60.0 },
                },
                new Dictionary<string, object>
                {
                    { "ExpandCooldown", 28800.0 },
                    { "ClaimCooldown", 14400.0 },
                    { "DevelopCooldown", 7200.0 },
                    { "WithdrawCooldown", 300.0 },
                    { "NegotiateCooldown", 600.0 },
                    { "FoundCooldown", 86400.0 },
                    { "ActionCooldown", 600.0 },
                });
            try
            {
                TimeSpan expected = TimeSpan.FromSeconds(60);
                Assert.Equal(expected, state.Loaded.ExpandCooldown);
                Assert.Equal(expected, state.Loaded.ClaimCooldown);
                Assert.Equal(expected, state.Loaded.DevelopCooldown);
                Assert.Equal(expected, state.Loaded.WithdrawCooldown);
                Assert.Equal(expected, state.Loaded.NegotiateCooldown);
                Assert.Equal(expected, state.Loaded.FoundCooldown);
                Assert.Equal(expected, state.Loaded.ActionCooldown);

                Assert.Equal(expected, state.Saved.ExpandCooldown);
                Assert.Equal(expected, state.Saved.ClaimCooldown);
                Assert.Equal(expected, state.Saved.DevelopCooldown);
                Assert.Equal(expected, state.Saved.WithdrawCooldown);
                Assert.Equal(expected, state.Saved.NegotiateCooldown);
                Assert.Equal(expected, state.Saved.FoundCooldown);
                Assert.Equal(expected, state.Saved.ActionCooldown);
                Assert.Equal(37, state.Saved.TuneVersion);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        /// <summary>The negative half: below the gate an operator's own override still wins. A
        /// migration that fires on every load is not a migration, it is a hardcoded value.</summary>
        [Fact]
        public void LoadTuning_NoVersionBump_LeavesAnOperatorsCooldownOverrideAlone()
        {
            TuningDiskState state = LoadTuningFromDisk(
                37,
                37,
                new Dictionary<string, object> { { "DevelopCooldown", 60.0 } },
                new Dictionary<string, object> { { "DevelopCooldown", 900.0 } });
            try
            {
                Assert.Equal(TimeSpan.FromSeconds(900), state.Loaded.DevelopCooldown);
                Assert.Equal(TimeSpan.FromSeconds(900), state.Saved.DevelopCooldown);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_VersionBump_MigratesFeatureDensityCeilingOverAPersistedValue()
        {
            // The live server's tuning.json is rewritten on every load, so a ceiling persisted by an
            // earlier build outlives any new shipped default unless it comes through the version gate.
            TuningDiskState state = LoadTuningFromDisk(
                35,
                34,
                new Dictionary<string, object> { { "FeatureMaxDensityPer1000Km3", 1350.0 } },
                new Dictionary<string, object> { { "FeatureMaxDensityPer1000Km3", 40.0 } });
            try
            {
                Assert.Equal(1350.0, state.Loaded.FeatureMaxDensityPer1000Km3, 9);
                Assert.Equal(1350.0, state.Saved.FeatureMaxDensityPer1000Km3, 9);
                Assert.Equal(35, state.Saved.TuneVersion);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_MissingReclaimBudget_KeepsTheInlineDefault()
        {
            // Every deployed tuning.json predates this key, and the field it feeds is a budget where
            // 0 does not mean "slower" -- it means the reload-reclaim sweep never runs. A zeroed key
            // would restore the exact defect the sweep exists to fix (rock reloaded from the save
            // that no record owns), and it would be invisible from the log, because the sweep that
            // did not run emits nothing. Neither file carries the key here and no version is bumped.
            TuningDiskState state = LoadTuningFromDisk(
                35,
                35,
                new Dictionary<string, object>(),
                new Dictionary<string, object>());
            try
            {
                Assert.Equal(32, state.Loaded.FeatureRockReclaimPerTick);
                Assert.Equal(32, state.Saved.FeatureRockReclaimPerTick);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_NoVersionBump_LeavesAPersistedFeatureDensityCeilingAlone()
        {
            TuningDiskState state = LoadTuningFromDisk(
                35,
                35,
                new Dictionary<string, object> { { "FeatureMaxDensityPer1000Km3", 1350.0 } },
                new Dictionary<string, object> { { "FeatureMaxDensityPer1000Km3", 40.0 } });
            try
            {
                Assert.Equal(40.0, state.Loaded.FeatureMaxDensityPer1000Km3, 9);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_DoesNotRescheduleForAVersionBumpThatCrossesNoTransitContract()
        {
            TuningDiskState state = LoadTuningFromDisk(
                33,
                32,
                new Dictionary<string, object>(),
                new Dictionary<string, object>());
            try
            {
                Assert.False(state.TransitContractMigrated);
                Assert.Equal(33, state.Saved.TuneVersion);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_MigratesFleetAccelerationToFourTimesThePriorDefault()
        {
            TuningDiskState state = LoadTuningFromDisk(
                15,
                14,
                new Dictionary<string, object> { { "FleetAccelKmPerMin2", 288.0 } },
                new Dictionary<string, object> { { "FleetAccelKmPerMin2", 72.0 } });
            try
            {
                Assert.True(state.TransitContractMigrated);
                Assert.Equal(288.0, state.Loaded.FleetAccelKmPerMin2);
                Assert.Equal(15, state.Saved.TuneVersion);
                Assert.Equal(288.0, state.Saved.FleetAccelKmPerMin2);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_MigratesSnapshotFlushIntervalToThirtySeconds()
        {
            TuningDiskState state = LoadTuningFromDisk(
                16,
                15,
                new Dictionary<string, object> { { "FlushEveryRealSeconds", 30.0 } },
                new Dictionary<string, object> { { "FlushEveryRealSeconds", 300.0 } });
            try
            {
                Assert.Equal(30.0, state.Loaded.FlushEveryRealSeconds);
                Assert.Equal(16, state.Saved.TuneVersion);
                Assert.Equal(30.0, state.Saved.FlushEveryRealSeconds);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_MigratesProductionProductivityBonus()
        {
            // A live tuning.json written before machine profiles existed carries an explicit
            // "ProductionProductivityBonus": 0 (SaveTuning persists the full merged object), which
            // would pin every machine to an unupgraded fit forever without a migration entry.
            TuningDiskState state = LoadTuningFromDisk(
                26,
                25,
                new Dictionary<string, object> { { "ProductionProductivityBonus", 4.0 } },
                new Dictionary<string, object> { { "ProductionProductivityBonus", 0.0 } });
            try
            {
                Assert.Equal(4.0, state.Loaded.ProductionProductivityBonus, 9);
                Assert.Equal(26, state.Saved.TuneVersion);
                Assert.Equal(4.0, state.Saved.ProductionProductivityBonus, 9);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_AlreadyCurrentVersion_RespectsCustomProductivityBonusOverride()
        {
            // An operator who deliberately dialled the fitment down on a current-version file keeps it.
            TuningDiskState state = LoadTuningFromDisk(
                26,
                26,
                new Dictionary<string, object> { { "ProductionProductivityBonus", 4.0 } },
                new Dictionary<string, object> { { "ProductionProductivityBonus", 1.0 } });
            try
            {
                Assert.Equal(1.0, state.Loaded.ProductionProductivityBonus, 9);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_MigratesMaxRefineryUpgradesToFifteen()
        {
            // A live tuning.json saved before this change already has an explicit
            // "MaxRefineryUpgrades": 5 baked in (SaveTuning always persists the full merged
            // object) -- without an explicit migration entry, the live-override populate step
            // would silently keep the stale 5 forever, even after the shipped default moves to 15.
            TuningDiskState state = LoadTuningFromDisk(
                23,
                22,
                new Dictionary<string, object> { { "MaxRefineryUpgrades", 15 } },
                new Dictionary<string, object> { { "MaxRefineryUpgrades", 5 } });
            try
            {
                Assert.Equal(15, state.Loaded.MaxRefineryUpgrades);
                Assert.Equal(23, state.Saved.TuneVersion);
                Assert.Equal(15, state.Saved.MaxRefineryUpgrades);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_MigratesOrbitGravityMinToFivePercent()
        {
            // A live tuning.json saved before this change already has an explicit
            // "OrbitGravityMin": 0.01 baked in (SaveTuning always persists the full merged object) --
            // without an explicit migration entry, the live-override populate step would silently keep
            // the stale 0.01 (and its old absolute-classification-floor meaning) forever, even after
            // the shipped default moves to 0.05 with the new relative snap-band-ceiling meaning.
            TuningDiskState state = LoadTuningFromDisk(
                24,
                23,
                new Dictionary<string, object> { { "OrbitGravityMin", 0.05 } },
                new Dictionary<string, object> { { "OrbitGravityMin", 0.01 } });
            try
            {
                Assert.Equal(0.05, state.Loaded.OrbitGravityMin, 9);
                Assert.Equal(24, state.Saved.TuneVersion);
                Assert.Equal(0.05, state.Saved.OrbitGravityMin, 9);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        [Fact]
        public void LoadTuning_AlreadyCurrentVersion_RespectsCustomOrbitGravityMinOverride()
        {
            // Once tuning.json is already at the current TuneVersion, ApplyVersionedContractDefaults
            // must NOT re-stamp the shipped default over an operator's deliberate live override.
            TuningDiskState state = LoadTuningFromDisk(
                24,
                24,
                new Dictionary<string, object> { { "OrbitGravityMin", 0.05 } },
                new Dictionary<string, object> { { "OrbitGravityMin", 0.03 } });
            try
            {
                Assert.Equal(0.03, state.Loaded.OrbitGravityMin, 9);
                Assert.Equal(0.03, state.Saved.OrbitGravityMin, 9);
            }
            finally
            {
                Cleanup(state.DirectoryPath);
            }
        }

        private static TuningDiskState LoadTuningFromDisk(
            int defaultsTuneVersion,
            int liveTuneVersion,
            IDictionary<string, object> defaultsExtra,
            IDictionary<string, object> liveExtra,
            bool includeLiveHoldDecayPerHour = true)
        {
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-tuning-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var defaults = new Dictionary<string, object>
            {
                { "TuneVersion", defaultsTuneVersion },
                { "HoldMaintenanceThreshold", 0.8 },
                { "HoldDecayPerHour", 0.24 },
                { "ReinforceCost", 50000 },
                { "GuardCost", 100000 }
            };
            foreach (KeyValuePair<string, object> kv in defaultsExtra)
                defaults[kv.Key] = kv.Value;

            var live = new Dictionary<string, object>
            {
                { "TuneVersion", liveTuneVersion },
                { "HoldMaintenanceThreshold", 0.55 },
                { "ReinforceCost", 12345 },
                { "GuardCost", 54321 }
            };
            if (includeLiveHoldDecayPerHour)
                live["HoldDecayPerHour"] = 0.48;
            foreach (KeyValuePair<string, object> kv in liveExtra)
                live[kv.Key] = kv.Value;

            File.WriteAllText(Path.Combine(dir, "tuning_defaults.json"), JsonConvert.SerializeObject(defaults, Formatting.Indented));
            File.WriteAllText(Path.Combine(dir, "tuning.json"), JsonConvert.SerializeObject(live, Formatting.Indented));

            var store = new FactionStore(dir);
            var loaded = new FactionTuning();
            bool transitContractMigrated = store.LoadTuning(loaded);
            var savedSettings = new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new TestTimeSpanSecondsConverter() },
            };
            var saved = JsonConvert.DeserializeObject<FactionTuning>(
                File.ReadAllText(Path.Combine(dir, "tuning.json")), savedSettings);
            return new TuningDiskState
            {
                Loaded = loaded,
                Saved = saved,
                TransitContractMigrated = transitContractMigrated,
                DirectoryPath = dir
            };
        }

        // ---- yield-table migration ------------------------------------------------------------

        private static NodeYieldEntry FindYieldEntry(FactionTuning tuning, string subTypeId)
        {
            NodeYieldEntry[] table = tuning.NodeYieldTable;
            if (table == null) return null;
            foreach (NodeYieldEntry e in table)
                if (e.SubTypeId == subTypeId) return e;
            return null;
        }

        private static object YieldEntry(string subTypeId, double weight, bool mineOnly = false)
        {
            return new Dictionary<string, object>
            {
                { "SubTypeId", subTypeId },
                { "ItemKind", "Ore" },
                { "Weight", weight },
                { "MineOnly", mineOnly }
            };
        }

        // The per-kind yield table became a single global row at FAC-102, so the additive
        // row-by-row merge these three tests covered has no shape to merge. What replaces it: a
        // persisted tuning.json written before the change carries no NodeYieldTable key at all, and
        // must fall to the shipped default rather than load as null and mint nothing.
        [Fact]
        public void LoadTuning_OlderFileWithoutTheNewTable_FallsToTheShippedDefault()
        {
            TuningDiskState state = LoadTuningFromDisk(
                31,
                30,
                new Dictionary<string, object>
                {
                    { "NodeYieldTable", new object[] { YieldEntry("Ore:Stone", 1.0, true) } }
                },
                new Dictionary<string, object>());   // a pre-FAC-102 file: no such key
            try
            {
                Assert.NotNull(state.Loaded.NodeYieldTable);
                NodeYieldEntry stone = FindYieldEntry(state.Loaded, "Ore:Stone");
                Assert.NotNull(stone);
                Assert.True(stone.MineOnly);
            }
            finally { Cleanup(state.DirectoryPath); }
        }

        // An operator-edited table still wins: the fallback fills an absent key, it does not overwrite
        // a present one.
        [Fact]
        public void LoadTuning_FileWithItsOwnTable_KeepsIt()
        {
            TuningDiskState state = LoadTuningFromDisk(
                31,
                30,
                new Dictionary<string, object>
                {
                    { "NodeYieldTable", new object[] { YieldEntry("Ore:Stone", 1.0, true) } }
                },
                new Dictionary<string, object>
                {
                    { "NodeYieldTable", new object[] { YieldEntry("Ore:Iron", 0.9) } }
                });
            try
            {
                Assert.Equal(0.9, FindYieldEntry(state.Loaded, "Ore:Iron").Weight);
                Assert.Null(FindYieldEntry(state.Loaded, "Ore:Stone"));
            }
            finally { Cleanup(state.DirectoryPath); }
        }

        // ---- FAC-233: the Ingot free-mint channel is stripped from a persisted tuning -----------

        // The trap this migration exists for. Every live tuning.json already carries the Ingot:Stone
        // row and the Ingot scale, and the two fallbacks beside this migration only fill an ABSENT
        // key -- so editing tuning_defaults.json alone changes nothing on a running server. The
        // removal is the opposite direction from FAC-050's additive merge, which cannot express it.
        [Fact]
        public void LoadTuning_StripsAPersistedIngotRow_WhenTheVersionMoves()
        {
            FactionTuning t = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": 44,"
                + " \"FreeMintScale\": { \"Ore\": 2.0, \"RareOre\": 0.1, \"Ingot\": 0.15 },"
                + " \"NodeYieldTable\": ["
                + "   { \"SubTypeId\": \"Ingot:Stone\", \"ItemKind\": \"Ingot\", \"Weight\": 1.0 },"
                + "   { \"SubTypeId\": \"Ore:Stone\", \"ItemKind\": \"Ore\", \"Weight\": 1.0, \"MineOnly\": true } ] }");

            Assert.Null(FindYieldEntry(t, "Ingot:Stone"));
            Assert.Equal(0.0, t.FreeMintScaleFor(ItemKind.Ingot), 6);

            // The supply path the removal relies on is untouched, and so are the operator's own ore
            // weights -- the migration is surgical, not a wholesale copy of the shipped table.
            NodeYieldEntry stone = FindYieldEntry(t, "Ore:Stone");
            Assert.NotNull(stone);
            Assert.True(stone.MineOnly);
            Assert.Equal(2.0, t.FreeMintScaleFor(ItemKind.Ore), 6);
        }

        // The negative control, and the escape hatch: the strip is a one-time migration behind the
        // version gate, not an unconditional wipe. At an EQUAL TuneVersion an operator who has
        // deliberately re-declared BOTH halves of the channel keeps them -- and both halves is the
        // only way to declare it, since a row without a scale mints nothing and a scale without a row
        // is credited income with no supply. Without this the test above would also pass on an
        // implementation that ignored the gate.
        [Fact]
        public void AnOperatorReDeclaredIngotChannelSurvivesWhenTheVersionHasNotMoved()
        {
            FactionTuning t = TestSupport.TuningWithOverride(
                "{ \"TuneVersion\": " + TestSupport.Tuning().TuneVersion + ","
                + " \"FreeMintScale\": { \"Ore\": 2.0, \"Ingot\": 0.15 },"
                + " \"NodeYieldTable\": ["
                + "   { \"SubTypeId\": \"Ingot:Stone\", \"ItemKind\": \"Ingot\", \"Weight\": 1.0 } ] }");

            Assert.NotNull(FindYieldEntry(t, "Ingot:Stone"));
            Assert.Equal(0.15, t.FreeMintScaleFor(ItemKind.Ingot), 6);
        }

        private static void Cleanup(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        [Fact]
        public void RelocationRole_RoundTrips_AndIsOmittedWhenNull()
        {
            var graph = new SpatialGraph();
            var home = graph.Add(new Vec3(0, 0, 0), 0.5, "H1");
            home.OwnerFactionTag = "ACME";

            var st = new FactionState { Tag = "ACME" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, RelocationRole = "Mine", ActorTag = "ACME",
                OriginNodeId = "H1", TargetNodeId = "H1", FleetId = "R1", LastResolvedSegment = -1,
            });
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "ACME", // ordinary Haul -- no RelocationRole
                OriginNodeId = "H1", TargetNodeId = "H1", FleetId = "R2", LastResolvedSegment = -1,
            });

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));

                // Rule 4 (lean JSON): the null case must be omitted entirely, so exactly one
                // occurrence -- the relocation fleet's.
                string json = File.ReadAllText(Directory.GetFiles(dir, "ACME.json", SearchOption.AllDirectories)[0]);
                Assert.Single(Regex.Matches(json, "RelocationRole").Cast<Match>());

                FactionState st2 = FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), new SpatialGraph());
                Assert.Equal("Mine", st2.PendingFleets.Single(f => f.FleetId == "R1").RelocationRole);
                Assert.Null(st2.PendingFleets.Single(f => f.FleetId == "R2").RelocationRole);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
