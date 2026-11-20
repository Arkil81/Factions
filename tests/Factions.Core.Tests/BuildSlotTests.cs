using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A yard's capacity: how many builds it works at once
    /// (<see cref="SpatialNode.BuildSlotCount"/>, its Shipyard level), and the id every one of them
    /// is addressed by (<see cref="SpatialNode.MintBuildEntryId"/>).
    ///
    /// <para>The two belong in one file because they are the same change: raising the slot count is
    /// what makes an id collision reachable often enough to matter, and the production side resolves
    /// <c>(nodeId, entryId)</c> to the first match with no error when one occurs.</para></summary>
    public class BuildSlotTests
    {
        private static SpatialNode Yard(int shipyardLevel)
        {
            var n = new SpatialNode { Id = "N1", OwnerFactionTag = "F", StableStationId = 7 };
            if (shipyardLevel > 0) n.Capabilities[NodeCapabilityKind.Shipyard] = shipyardLevel;
            return n;
        }

        private static BuildQueueEntry Queue(SpatialNode node, string role)
        {
            var e = new BuildQueueEntry { Id = node.MintBuildEntryId(role), Role = role, PrefabId = "P" };
            e.Bom["Comp:SteelPlate"] = 100;
            node.BuildQueue.Add(e);
            return e;
        }

        // ---- the slot count is the level ---------------------------------------------------------

        [Fact]
        public void BuildSlotCount_IsTheShipyardLevel()
        {
            Assert.Equal(1, Yard(1).BuildSlotCount());
            Assert.Equal(3, Yard(3).BuildSlotCount());
        }

        [Fact]
        public void BuildSlotCount_FloorsAtOneForANodeWithNoShipyard()
        {
            // A station refit is queued at any founded owned node, yard or not, and is serviced by
            // the orphan pass. A count of 0 here would leave every refit waiting forever for a slot
            // that cannot exist.
            SpatialNode plain = Yard(0);
            Assert.Equal(1, plain.BuildSlotCount());

            BuildQueueEntry refit = Queue(plain, ShipyardManager.StationRefitRole);
            Assert.True(plain.IsActiveBuild(refit));
        }

        [Fact]
        public void IsActiveBuild_WorksTheTopLevelManyEntries_AndQueuesTheRest()
        {
            SpatialNode yard = Yard(2);
            BuildQueueEntry first  = Queue(yard, "Trade");
            BuildQueueEntry second = Queue(yard, "Raid");
            BuildQueueEntry third  = Queue(yard, "Mine");

            // Two slots: the third waits. The paired negative matters more than the positives -- a
            // slot count that ignored the level would read all three as working.
            Assert.True(yard.IsActiveBuild(first));
            Assert.True(yard.IsActiveBuild(second));
            Assert.False(yard.IsActiveBuild(third));

            // And the same queue one level down works exactly one, which is the pre-change behaviour.
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            Assert.True(yard.IsActiveBuild(first));
            Assert.False(yard.IsActiveBuild(second));
            Assert.False(yard.IsActiveBuild(third));
        }

        // ---- entry ids stay unique ---------------------------------------------------------------

        [Fact]
        public void MintBuildEntryId_DoesNotReuseAnIdAfterAnEntryIsRemoved()
        {
            // The characteristic failure of minting from BuildQueue.Count: remove an entry and the
            // count walks back over an id that is still in use by a survivor.
            SpatialNode yard = Yard(1);
            BuildQueueEntry a = Queue(yard, "Trade");
            BuildQueueEntry b = Queue(yard, "Trade");
            yard.BuildQueue.Remove(a);

            BuildQueueEntry c = Queue(yard, "Trade");

            Assert.NotEqual(b.Id, c.Id);
            Assert.NotEqual(a.Id, c.Id);
        }

        [Fact]
        public void MintBuildEntryId_SkipsAnIdTheQueueAlreadyCarries()
        {
            // Entries also arrive from persistence and from fixtures with ids this counter never
            // issued, so uniqueness is a property of the mint rather than of every writer keeping
            // the counter ahead of the queue.
            SpatialNode yard = Yard(1);
            var planted = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", PrefabId = "P" };
            planted.Bom["Comp:SteelPlate"] = 100;
            yard.BuildQueue.Add(planted);          // BuildSeq is still 0

            string minted = yard.MintBuildEntryId("Trade");

            Assert.NotEqual("Trade:001", minted);
        }

        [Fact]
        public void ParseBuildEntrySeq_ReadsWhatTheMintWrote_AndZeroForAnythingElse()
        {
            SpatialNode yard = Yard(1);
            Assert.Equal(1, SpatialNode.ParseBuildEntrySeq(yard.MintBuildEntryId("Trade")));
            Assert.Equal(2, SpatialNode.ParseBuildEntrySeq(yard.MintBuildEntryId("Raid")));
            // The bare role string a doc written before ids existed loads as.
            Assert.Equal(0, SpatialNode.ParseBuildEntrySeq("Trade"));
            Assert.Equal(0, SpatialNode.ParseBuildEntrySeq(null));
            Assert.Equal(0, SpatialNode.ParseBuildEntrySeq("Trade:"));
            Assert.Equal(0, SpatialNode.ParseBuildEntrySeq("Trade:abc"));
        }

        // ---- and survive a restart ---------------------------------------------------------------

        private static SpatialNode RoundTrip(SpatialNode node)
        {
            var graph = new SpatialGraph();
            SpatialNode placed = graph.Add(node.Position, 0.6, node.Id);
            placed.OwnerFactionTag = "ACME";
            placed.HoldStrength = 0.8;
            placed.BuildSeq = node.BuildSeq;
            foreach (KeyValuePair<NodeCapabilityKind, int> kv in node.Capabilities)
                placed.Capabilities[kv.Key] = kv.Value;
            foreach (BuildQueueEntry e in node.BuildQueue) placed.BuildQueue.Add(e);

            var st = new FactionState { Tag = "ACME" };
            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);
            var graph2 = new SpatialGraph();
            FactionStore.ApplyBrainDoc(doc, graph2);
            return graph2.Nodes[node.Id];
        }

        [Fact]
        public void BuildSeq_SurvivesARestart_SoTheNextIdCannotCollide()
        {
            SpatialNode yard = Yard(1);
            BuildQueueEntry a = Queue(yard, "Trade");
            BuildQueueEntry b = Queue(yard, "Raid");
            yard.BuildQueue.Remove(a);             // seq 2 stands, only "Raid:002" is left

            SpatialNode loaded = RoundTrip(yard);

            Assert.Equal(2, loaded.BuildSeq);
            Assert.Equal(b.Id, Assert.Single(loaded.BuildQueue).Id);
            Assert.NotEqual(b.Id, loaded.MintBuildEntryId("Raid"));
        }

        [Fact]
        public void ALegacyDocWithNoBuildSeq_RecoversItFromTheQueueItCarries()
        {
            // The migration that matters: every brain on disk today was written by the count-based
            // minting and carries no counter at all. Loading one at 0 would hand a live id straight
            // back out on the next build.
            SpatialNode yard = Yard(1);
            var legacyA = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", PrefabId = "P" };
            legacyA.Bom["Comp:SteelPlate"] = 100;
            var legacyB = new BuildQueueEntry { Id = "Raid:004", Role = "Raid", PrefabId = "P" };
            legacyB.Bom["Comp:SteelPlate"] = 100;
            yard.BuildQueue.Add(legacyA);
            yard.BuildQueue.Add(legacyB);
            Assert.Equal(0, yard.BuildSeq);        // as a pre-change doc loads

            SpatialNode loaded = RoundTrip(yard);

            Assert.Equal(4, loaded.BuildSeq);      // past the HIGHEST id present, not the count
            Assert.Equal("Trade:005", loaded.MintBuildEntryId("Trade"));
        }

        [Fact]
        public void TwoEntriesSharingAnId_AreRepairedOnLoad()
        {
            // A collision the count-based minting could already write: two Trade entries at one node
            // (a shelf build and a fleet build coexist by design) with one removed in between. Left
            // alone, both resolve to the same build everywhere the production side addresses one.
            SpatialNode yard = Yard(1);
            var first = new BuildQueueEntry { Id = "Trade:003", Role = "Trade", PrefabId = "A" };
            first.Bom["Comp:SteelPlate"] = 100;
            first.Collected["Comp:SteelPlate"] = 40;
            var clash = new BuildQueueEntry { Id = "Trade:003", Role = "Trade", PrefabId = "B", DestinedForSale = true };
            clash.Bom["Comp:SteelPlate"] = 100;
            yard.BuildQueue.Add(first);
            yard.BuildQueue.Add(clash);

            SpatialNode loaded = RoundTrip(yard);

            Assert.Equal(2, loaded.BuildQueue.Count);
            Assert.NotEqual(loaded.BuildQueue[0].Id, loaded.BuildQueue[1].Id);
            // The one that was there first keeps its id and its collected components; only the
            // duplicate is re-minted.
            Assert.Equal("Trade:003", loaded.BuildQueue[0].Id);
            Assert.Equal(40, loaded.BuildQueue[0].Collected["Comp:SteelPlate"]);
            Assert.Equal("B", loaded.BuildQueue[1].PrefabId);
        }
            [Fact]
        public void CollectedAboveItsBom_IsClampedOnLoad_AndTheExcessIsDiscarded()
        {
            // The shape id-twins left on disk: one twin's Collected written into both, so a small
            // shelf build carries a warship's components -- over on some keys, keys its BOM never
            // names, and still short on the one key it actually needs.
            SpatialNode yard = Yard(1);
            yard.Inventory["Comp:MetalGrid"] = 5;
            var shelf = new BuildQueueEntry { Id = "Raid:004", Role = "Raid", PrefabId = "Small", DestinedForSale = true };
            shelf.Bom["Comp:SteelPlate"] = 259;
            shelf.Bom["Comp:MetalGrid"] = 4;
            shelf.Bom["Comp:Motor"] = 12;
            shelf.Collected["Comp:MetalGrid"] = 33554;
            shelf.Collected["Comp:Motor"] = 7;
            shelf.Collected["Comp:Reactor"] = 30;
            yard.BuildQueue.Add(shelf);

            var graph = new SpatialGraph();
            SpatialNode placed = graph.Add(yard.Position, 0.6, yard.Id);
            placed.OwnerFactionTag = "ACME";
            placed.HoldStrength = 0.8;
            placed.Inventory["Comp:MetalGrid"] = 5;
            placed.BuildQueue.Add(shelf);
            BrainDoc doc = FactionStore.ToBrainDoc(new FactionState { Tag = "ACME" }, graph.OwnedBy("ACME"),
                FactionStore.DefaultLogCap);
            var log = new List<string>();
            var graph2 = new SpatialGraph();
            FactionStore.ApplyBrainDoc(doc, graph2, null, log);
            SpatialNode loaded = graph2.Nodes[yard.Id];

            BuildQueueEntry entry = Assert.Single(loaded.BuildQueue);
            Assert.Equal(4, entry.Collected["Comp:MetalGrid"]);
            Assert.Equal(7, entry.Collected["Comp:Motor"]);            // under its BOM: untouched
            Assert.False(entry.Collected.ContainsKey("Comp:Reactor")); // not in the BOM at all
            Assert.False(entry.Collected.ContainsKey("Comp:SteelPlate"));
            Assert.Equal(5, loaded.Inventory["Comp:MetalGrid"]);        // discarded, not refunded
            Assert.Contains("ACME BUILD_COLLECTED_CLAMPED node=" + yard.Id
                            + " entry=Raid:004 prefab=Small discarded=33580 keys=2", log);
        }

        [Fact]
        public void CollectedWithinItsBom_LoadsUnchanged_AndLogsNothing()
        {
            SpatialNode yard = Yard(1);
            var build = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", PrefabId = "P" };
            build.Bom["Comp:SteelPlate"] = 100;
            build.Bom["Comp:Motor"] = 10;
            build.Collected["Comp:SteelPlate"] = 100;
            build.Collected["Comp:Motor"] = 3;
            yard.BuildQueue.Add(build);

            var graph = new SpatialGraph();
            SpatialNode placed = graph.Add(yard.Position, 0.6, yard.Id);
            placed.OwnerFactionTag = "ACME";
            placed.HoldStrength = 0.8;
            placed.BuildQueue.Add(build);
            BrainDoc doc = FactionStore.ToBrainDoc(new FactionState { Tag = "ACME" }, graph.OwnedBy("ACME"),
                FactionStore.DefaultLogCap);
            var log = new List<string>();
            FactionStore.ApplyBrainDoc(doc, new SpatialGraph(), null, log);

            Assert.DoesNotContain(log, l => l.Contains("BUILD_COLLECTED_CLAMPED"));
            int keys;
            Assert.Equal(0, FactionStore.ClampCollectedToBom(build, out keys));
            Assert.Equal(100, build.Collected["Comp:SteelPlate"]);
            Assert.Equal(3, build.Collected["Comp:Motor"]);
        }

        // ---- a build named by hull ---------------------------------------------------------------

        [Fact]
        public void ABuildNamedByHull_IsMintedAB_Id_OnTheSameCounter()
        {
            SpatialNode yard = Yard(1);
            BuildQueueEntry trade = Queue(yard, "Trade");
            BuildQueueEntry named = Queue(yard, null);

            Assert.Equal("Trade:001", trade.Id);
            Assert.Equal("B:002", named.Id);
            Assert.Equal(2, SpatialNode.ParseBuildEntrySeq(named.Id));
        }

        [Fact]
        public void ABuildNamedByHull_SurvivesARestart_WithNoRole_AndWritesNone()
        {
            SpatialNode yard = Yard(1);
            BuildQueueEntry named = Queue(yard, null);

            SpatialNode loaded = RoundTrip(yard);

            // The loader used to skip an entry with no role: a player's build would vanish on restart.
            BuildQueueEntry back = Assert.Single(loaded.BuildQueue);
            Assert.Equal(named.Id, back.Id);
            Assert.Null(back.Role);
            Assert.Equal("P", back.PrefabId);

            var graph = new SpatialGraph();
            SpatialNode placed = graph.Add(yard.Position, 0.6, yard.Id);
            placed.OwnerFactionTag = "ACME";
            placed.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            placed.BuildQueue.Add(named);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                FactionStore.ToBrainDoc(new FactionState { Tag = "ACME" }, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));
            Assert.Contains("\"B:001\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Role\"", json, StringComparison.Ordinal);
        }
    }
}
