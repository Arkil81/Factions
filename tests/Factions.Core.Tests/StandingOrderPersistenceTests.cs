using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Round-trip and migration rules for <see cref="FactionState.StandingOrders"/> —
    /// the list that replaced the pre-FAC-008 one-order-per-hub <c>MineAutoOrders</c> dictionary.</summary>
    public class StandingOrderPersistenceTests
    {
        private static FactionState RoundTrip(FactionState st)
        {
            var graph = new SpatialGraph();
            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy(st.Tag), FactionStore.DefaultLogCap);
            return FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
        }

        [Fact]
        public void MineOrder_RoundTripsOreWeightsAndScanCursor()
        {
            var st = new FactionState { Tag = "ACME" };
            var weights = new Dictionary<string, double> { { "Iron", 50 }, { "Nickel", 50 } };
            DateTime nextScan = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);
            StandingOrderFixtures.AddMineOrder(st, "Hub", 45, nextScan, weights);

            StandingOrder loaded = Assert.Single(RoundTrip(st).StandingOrders);

            Assert.Equal(StandingOrderKind.Mine, loaded.Kind);
            Assert.Equal("Hub", loaded.HubNodeId);
            Assert.Equal(45, loaded.PeriodSeconds);
            // The cursor is persisted so a restart doesn't re-scan every order in the load tick.
            Assert.Equal(nextScan, loaded.NextScanUtc);
            Assert.Equal(50, loaded.OreWeights["Nickel"]);
        }

        [Fact]
        public void SeveralOrdersOnOneHub_SurviveTheRoundTrip()
        {
            var st = new FactionState { Tag = "ACME" };
            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, DateTime.MinValue,
                new Dictionary<string, double> { { "Iron", 100 } }, priority: 9);
            StandingOrderFixtures.AddMineOrder(st, "Hub", 120, DateTime.MinValue,
                new Dictionary<string, double> { { "Cobalt", 100 } }, priority: 1);

            List<StandingOrder> loaded = RoundTrip(st).StandingOrders;

            // The exact case the old hubNodeId-keyed dictionary could not represent at all.
            Assert.Equal(2, loaded.Count);
            Assert.Equal(2, loaded.FindAll(o => o.HubNodeId == "Hub").Count);
        }

        [Fact]
        public void IdCounter_SurvivesRestart_SoIdsAreNeverReused()
        {
            var st = new FactionState { Tag = "ACME" };
            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, DateTime.MinValue);
            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, DateTime.MinValue);

            FactionState loaded = RoundTrip(st);
            loaded.StandingOrders.RemoveAt(0);

            Assert.Equal(2, loaded.StandingOrderSeq);
            Assert.Equal("SO-003", loaded.MintStandingOrderId());
        }

        [Fact]
        public void UnrecognisedKind_IsDropped_NotLoadedAsWhateverIsZero()
        {
            var doc = new BrainDoc
            {
                Tag = "ACME",
                StandingOrders = new List<StandingOrderDoc>
                {
                    new StandingOrderDoc { Id = "SO-001", Kind = "Conquer", Priority = 5 },
                    new StandingOrderDoc { Id = "SO-002", Kind = "Mine", HubNodeId = "Hub", PeriodSeconds = 60 },
                },
                StandingOrderSeq = 2,
            };

            FactionState st = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            // An order is an instruction to spend the faction's resources: running the wrong one is
            // worse than losing it, which is the opposite of the Events ring's own unknown-kind rule.
            StandingOrder only = Assert.Single(st.StandingOrders);
            Assert.Equal("SO-002", only.Id);
            Assert.Equal(StandingOrderKind.Mine, only.Kind);
        }

        [Fact]
        public void LegacyMineAutoOrders_MigrateIntoTheList()
        {
            var doc = new BrainDoc
            {
                Tag = "ACME",
                MineAutoOrders = new Dictionary<string, MineAutoOrderDoc>
                {
                    { "Hub", new MineAutoOrderDoc
                        {
                            PeriodSeconds = 90,
                            NextScanUtc = new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc),
                            OreWeights = new Dictionary<string, double> { { "Iron", 100 } },
                        }
                    },
                },
            };

            FactionState st = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            StandingOrder migrated = Assert.Single(st.StandingOrders);
            Assert.Equal(StandingOrderKind.Mine, migrated.Kind);
            Assert.Equal("Hub", migrated.HubNodeId);
            Assert.Equal(90, migrated.PeriodSeconds);
            Assert.Equal(new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc), migrated.NextScanUtc);
            Assert.Equal(100, migrated.OreWeights["Iron"]);
            // Minted through the same counter, so a migrated order is indistinguishable from a fresh
            // one and the next id cannot collide with it.
            Assert.Equal("SO-001", migrated.Id);
            Assert.Equal(1, st.StandingOrderSeq);
        }

        [Fact]
        public void LegacyMineAutoOrders_AreIgnoredOnceTheNewListExists()
        {
            var doc = new BrainDoc
            {
                Tag = "ACME",
                StandingOrders = new List<StandingOrderDoc>
                {
                    new StandingOrderDoc { Id = "SO-001", Kind = "Mine", HubNodeId = "Hub", PeriodSeconds = 60 },
                },
                StandingOrderSeq = 1,
                // A file written by this build never carries both; if one somehow does, the migration
                // must not double-load the same standing order.
                MineAutoOrders = new Dictionary<string, MineAutoOrderDoc>
                {
                    { "Hub", new MineAutoOrderDoc { PeriodSeconds = 60 } },
                },
            };

            FactionState st = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());

            Assert.Single(st.StandingOrders);
        }

        [Fact]
        public void StandingOrdersByRank_IsATotalOrder_HighestRankThenOldestThenId()
        {
            var st = new FactionState { Tag = "ACME" };
            StandingOrder a = StandingOrderFixtures.AddMineOrder(st, "H", 60, DateTime.MinValue, priority: 1);
            StandingOrder b = StandingOrderFixtures.AddMineOrder(st, "H", 60, DateTime.MinValue, priority: 5);
            StandingOrder c = StandingOrderFixtures.AddMineOrder(st, "H", 60, DateTime.MinValue, priority: 5);
            b.CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            c.CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc); // younger

            List<StandingOrder> ranked = st.StandingOrdersByRank();

            Assert.Equal(new[] { b.Id, c.Id, a.Id }, new[] { ranked[0].Id, ranked[1].Id, ranked[2].Id });
        }

        [Fact]
        public void StandingOrders_HoldMineOnly_TheDoctrineIsASeparateList()
        {
            var st = new FactionState { Tag = "ACME" };
            StandingOrder mine = StandingOrderFixtures.AddMineOrder(st, "H", 60, DateTime.MinValue);
            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 1);

            // Two lists, two id spaces, and nothing that leaks between them: a fleet goal is not a
            // standing order any more, and a Mine order is not part of the fleet.
            Assert.Equal(mine.Id, Assert.Single(st.StandingOrdersByRank(StandingOrderKind.Mine)).Id);
            Assert.Equal(mine.Id, Assert.Single(st.StandingOrders).Id);
            Assert.Equal(line.Id, Assert.Single(st.FleetDoctrine).Id);
            Assert.StartsWith("SO-", mine.Id);
            Assert.StartsWith("FD-", line.Id);
        }

        [Fact]
        public void EmptyList_IsOmittedFromTheBrainDoc()
        {
            var st = new FactionState { Tag = "ACME" };
            var graph = new SpatialGraph();

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);

            // "Keep JSON lean": the per-faction brain files are loaded one at a time and bloat compounds.
            Assert.Null(doc.StandingOrders);
        }
    }
}
