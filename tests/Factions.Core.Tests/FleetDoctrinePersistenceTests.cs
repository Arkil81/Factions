using System;
using System.Collections.Generic;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Round-trip, lean-JSON and migration rules for <see cref="FactionState.FleetDoctrine"/>,
    /// plus the one predicate every doctrine gate reads
    /// (<see cref="FactionLifecycle.DoctrineActive"/>).</summary>
    public class FleetDoctrinePersistenceTests
    {
        private static FactionState RoundTrip(FactionState st, List<string> log = null)
        {
            var graph = new SpatialGraph();
            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy(st.Tag), FactionStore.DefaultLogCap);
            return FactionStore.ApplyBrainDoc(doc, new SpatialGraph(), null, log);
        }

        [Fact]
        public void DoctrineLine_RoundTripsEveryPlayerSuppliedField()
        {
            var st = new FactionState { Tag = "ACME", IsPlayerFaction = true };
            DoctrineLine original = StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 10, scopeNodeId: "N1", maxConcurrent: 3, priority: 7, paused: true);
            original.CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            FactionState back = RoundTrip(st);
            DoctrineLine loaded = Assert.Single(back.FleetDoctrine);

            Assert.Equal("FD-001", loaded.Id);
            Assert.Equal("Ridill", loaded.PrefabId);
            Assert.Equal(10, loaded.TargetCount);
            Assert.Equal(3, loaded.MaxConcurrent);
            Assert.Equal("N1", loaded.ScopeNodeId);
            Assert.Equal(7, loaded.Priority);
            Assert.True(loaded.Paused);
            Assert.Equal(original.CreatedUtc, loaded.CreatedUtc);
            // The counter travels with the list, so the next line minted after a restart cannot
            // collide with an id the player already has written down.
            Assert.Equal(1, back.DoctrineSeq);
            Assert.Equal("FD-002", back.MintDoctrineLineId());
        }

        [Fact]
        public void EmptyDoctrine_IsOmittedFromTheSerializedBrain()
        {
            var st = new FactionState { Tag = "ACME" };
            var graph = new SpatialGraph();

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);

            Assert.Null(doc.FleetDoctrine);
            // "Keep JSON lean": every NPC brain would otherwise carry the key, and the per-faction
            // files are loaded one at a time. Asserted on the SERIALIZED text, not just the DTO --
            // a null field with the wrong NullValueHandling still writes "FleetDoctrine": null.
            string json = JsonConvert.SerializeObject(doc);
            Assert.DoesNotContain("FleetDoctrine", json, StringComparison.Ordinal);
            Assert.DoesNotContain("DoctrineSeq", json, StringComparison.Ordinal);
        }

        /// <summary>The pre-doctrine brain file: a Mine order and a retired <c>Kind: "Build"</c>
        /// goal in one list.</summary>
        private static BrainDoc LegacyDocWithABuildOrder()
        {
            return new BrainDoc
            {
                Tag = "ACME",
                IsPlayerFaction = true,
                StandingOrderSeq = 3,
                StandingOrders = new List<StandingOrderDoc>
                {
                    new StandingOrderDoc
                    {
                        Id = "SO-002", Kind = "Mine", HubNodeId = "Hub", PeriodSeconds = 60,
                    },
                    new StandingOrderDoc
                    {
                        Id = "SO-003", Kind = "Build", PrefabId = "Ridill", Role = "Mine",
                        TargetCount = 4, MaxConcurrent = 2, ScopeNodeId = "N1", Priority = 5,
                        ExcludeListedForSale = true,
                        CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    },
                },
            };
        }

        [Fact]
        public void LegacyBuildOrder_LoadsAsOneDoctrineLine_AndLeavesOnlyMineOnTheOrderList()
        {
            var log = new List<string>();

            FactionState st = FactionStore.ApplyBrainDoc(LegacyDocWithABuildOrder(), new SpatialGraph(), null, log);

            DoctrineLine line = Assert.Single(st.FleetDoctrine);
            Assert.Equal("Ridill", line.PrefabId);
            Assert.Equal(4, line.TargetCount);
            Assert.Equal(2, line.MaxConcurrent);
            Assert.Equal("N1", line.ScopeNodeId);
            Assert.Equal(5, line.Priority);
            // Re-minted from the doctrine's own counter, not carried: the two lists have separate id
            // spaces and a migrated line must be indistinguishable from a fresh one.
            Assert.Equal("FD-001", line.Id);

            StandingOrder kept = Assert.Single(st.StandingOrders);
            Assert.Equal(StandingOrderKind.Mine, kept.Kind);
            Assert.Equal("SO-002", kept.Id);

            // The migration is logged once, not silently: a rewrite of the player's fleet goal must
            // be findable afterwards.
            Assert.Contains(log, l => l.Contains("DOCTRINE_MIGRATED") && l.Contains("from=SO-003")
                                      && l.Contains("to=FD-001") && l.Contains("prefab=Ridill"));
        }

        [Fact]
        public void MigratedBuildOrder_IsNeverWrittenBackAsAStandingOrder()
        {
            FactionState st = FactionStore.ApplyBrainDoc(LegacyDocWithABuildOrder(), new SpatialGraph());
            var graph = new SpatialGraph();

            BrainDoc flushed = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);

            StandingOrderDoc written = Assert.Single(flushed.StandingOrders);
            Assert.Equal("Mine", written.Kind);
            Assert.Single(flushed.FleetDoctrine);
            // Idempotent: loading what we just wrote must not migrate a second time and double the
            // doctrine.
            Assert.Single(FactionStore.ApplyBrainDoc(flushed, new SpatialGraph()).FleetDoctrine);
        }

        [Fact]
        public void AbsentDoctrineField_LoadsAsAnEmptyDoctrine()
        {
            // A brain file written before this feature existed carries neither key. It must load as
            // "no doctrine", which is the behaviour it had.
            FactionState st = FactionStore.ApplyBrainDoc(new BrainDoc { Tag = "ACME" }, new SpatialGraph());

            Assert.Empty(st.FleetDoctrine);
            Assert.Equal(0, st.DoctrineSeq);
            Assert.False(FactionLifecycle.DoctrineActive(st));
        }

        [Fact]
        public void DoctrineActive_IsPlayerFactionAndANonEmptyList()
        {
            var npcWithLines = new FactionState { Tag = "NPC" };
            StandingOrderFixtures.AddDoctrineLine(npcWithLines, "Ridill", targetCount: 1);
            var playerNoLines = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            var playerWithLines = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            StandingOrderFixtures.AddDoctrineLine(playerWithLines, "Ridill", targetCount: 1);

            Assert.True(FactionLifecycle.DoctrineActive(playerWithLines));
            // Empty means unchanged: a player who removes their last line gets the old behaviour
            // back, not a fleet that quietly stops growing.
            Assert.False(FactionLifecycle.DoctrineActive(playerNoLines));
            // The V1 scope limit. The EVALUATOR is species-blind on purpose (ServiceDoctrine reads
            // the list alone); it is the two autonomy gates that are not, and this is the predicate
            // they read.
            Assert.False(FactionLifecycle.DoctrineActive(npcWithLines));
        }

        [Fact]
        public void DoctrineByRank_RanksByPriorityThenId()
        {
            var st = new FactionState { Tag = "ACME" };
            DoctrineLine a = StandingOrderFixtures.AddDoctrineLine(st, "A", targetCount: 1, priority: 0);
            DoctrineLine b = StandingOrderFixtures.AddDoctrineLine(st, "B", targetCount: 1, priority: 9);
            DoctrineLine c = StandingOrderFixtures.AddDoctrineLine(st, "C", targetCount: 1, priority: 9);

            List<DoctrineLine> ranked = st.DoctrineByRank();

            // Highest rank first; the id breaks the tie, so two lines added in one batch (which can
            // share a creation timestamp to the tick) still evaluate in a fixed order.
            Assert.Equal(new[] { b.Id, c.Id, a.Id }, new[] { ranked[0].Id, ranked[1].Id, ranked[2].Id });
        }
    }
}
