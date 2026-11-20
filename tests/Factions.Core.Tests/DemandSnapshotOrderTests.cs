using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Snapshot ordering. The register cannot be sorted in place — its list indexes are the
    /// values of its own id→index map — so the ranking belongs at the boundary a reader sees.</summary>
    public sealed class DemandSnapshotOrderTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private static WorldModel World()
        {
            var w = TestSupport.World();
            w.UtcNow = Now;
            return w;
        }

        private static SpatialNode Node(WorldModel w, string id)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            return n;
        }

        private static DemandEntry Item(string key, string nodeId, long qty, DemandClass cls,
            DateTime firstPosted = default(DateTime))
        {
            return new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + key, NodeId = nodeId, Quantity = qty,
                ExpiresUtc = Now.AddMinutes(5), Class = cls,
                FirstPostedUtc = firstPosted == default(DateTime) ? Now : firstPosted,
            };
        }

        [Fact]
        public void BurningRowLeads_EvenWhenItWasPostedLast()
        {
            WorldModel w = World();
            SpatialNode stocked = Node(w, "Stocked");
            Node(w, "Starving");
            stocked.Inventory["Comp:Construction"] = 990;

            var demands = new List<DemandEntry>
            {
                // Posted first, nearly met, and merely a catalogue preference.
                Item("Comp:Construction", "Stocked", 1_000, DemandClass.Catalog),
                // Posted last, nothing on hand, and a ship build is waiting on it.
                Item("Comp:Thrust", "Starving", 31, DemandClass.ShipBuild, Now.AddHours(-9)),
            };

            List<DemandSnapshotRow> ranked = DemandSnapshotOrder.RankForSnapshot(demands, w, TestSupport.Tuning());

            Assert.Equal("Item:Comp:Thrust", ranked[0].Demand.Kind);
        }

        [Fact]
        public void OrderInverts_WhenTheStarvingRowIsTheComfortableOne()
        {
            WorldModel w = World();
            Node(w, "Stocked");
            SpatialNode starving = Node(w, "Starving");
            starving.Inventory["Comp:Thrust"] = 31;   // met

            var demands = new List<DemandEntry>
            {
                Item("Comp:Construction", "Stocked", 1_000, DemandClass.Catalog),
                Item("Comp:Thrust", "Starving", 31, DemandClass.ShipBuild, Now.AddHours(-9)),
            };

            List<DemandSnapshotRow> ranked = DemandSnapshotOrder.RankForSnapshot(demands, w, TestSupport.Tuning());

            Assert.Equal("Item:Comp:Construction", ranked[0].Demand.Kind);
        }

        [Fact]
        public void RowsWithNoUrgencyKeepTheirOrder_SoShipDemandsAreNotShuffled()
        {
            WorldModel w = World();
            Node(w, "N1");

            var demands = new List<DemandEntry>
            {
                DemandEntry.ForShip("F", "Trade", "N1", Now.AddMinutes(5), postedUtc: Now),
                DemandEntry.ForShip("F", "Raid",  "N1", Now.AddMinutes(5), postedUtc: Now),
                DemandEntry.ForShip("F", "Mine",  "N1", Now.AddMinutes(5), postedUtc: Now),
            };

            List<DemandSnapshotRow> ranked = DemandSnapshotOrder.RankForSnapshot(demands, w, TestSupport.Tuning());

            Assert.Equal(new[] { "Ship:Trade", "Ship:Raid", "Ship:Mine" }, ranked.Select(r => r.Demand.Kind).ToArray());
        }

        [Fact]
        public void TheCapNowKeepsTheUrgentRows_NotMerelyTheOldest()
        {
            WorldModel w = World();
            SpatialNode stocked = Node(w, "Stocked");
            Node(w, "Starving");

            // Far more comfortable rows than the snapshot will carry, all posted before the one row
            // that actually matters. Before ranking, the cap kept these and dropped the burning one.
            var demands = new List<DemandEntry>();
            for (int i = 0; i < DemandsSnapshotPayload.MaxRowsPerKind * 2; i++)
            {
                string key = "Comp:Filler" + i;
                stocked.Inventory[key] = 999;
                demands.Add(Item(key, "Stocked", 1_000, DemandClass.Catalog));
            }
            demands.Add(Item("Comp:Thrust", "Starving", 31, DemandClass.ShipBuild, Now.AddHours(-9)));

            List<DemandSnapshotRow> ranked = DemandSnapshotOrder.RankForSnapshot(demands, w, TestSupport.Tuning());

            Dictionary<string, object> payload =
                DemandsSnapshotPayload.Build("F", Now, ranked, new List<InvestmentSignal>());
            var rows = (List<object>)payload["demands"];
            var kinds = rows.Cast<Dictionary<string, object>>().Select(r => (string)r["kind"]).ToList();

            Assert.Equal(DemandsSnapshotPayload.MaxRowsPerKind, kinds.Count);
            Assert.Contains("Item:Comp:Thrust", kinds);
        }

        [Fact]
        public void UnknownNode_ScoresZeroRatherThanThrowing()
        {
            WorldModel w = World();
            var demands = new List<DemandEntry>
            {
                Item("Comp:Thrust", "NoSuchNode", 31, DemandClass.ShipBuild),
                Item("Comp:Bolt", "AlsoMissing", 5, DemandClass.Catalog),
            };

            List<DemandSnapshotRow> ranked = DemandSnapshotOrder.RankForSnapshot(demands, w, TestSupport.Tuning());

            Assert.Equal(2, ranked.Count);
            // A row whose node is gone is not merely unranked -- it is unmeasured, and the payload
            // must say so rather than shipping a have=0 that reads as a total shortfall.
            Assert.All(ranked, r => Assert.False(r.Scored));
        }


        [Fact]
        public void AScoredRowCarriesTheStockItWasRankedAgainst()
        {
            WorldModel w = World();
            SpatialNode partial = Node(w, "Partial");
            partial.Inventory["Comp:Thrust"] = 7;

            var demands = new List<DemandEntry> { Item("Comp:Thrust", "Partial", 31, DemandClass.ShipBuild) };

            DemandSnapshotRow row = Assert.Single(
                DemandSnapshotOrder.RankForSnapshot(demands, w, TestSupport.Tuning()));

            Assert.True(row.Scored);
            Assert.Equal(7, row.HaveAtNode);
            // The same number the dispatcher orders by, not a recomputation the panel could get wrong.
            Assert.Equal(DemandUrgency.Score(demands[0], 7, 0, Now, TestSupport.Tuning()), row.Urgency);
        }

        [Fact]
        public void AShipRowIsUnscored_SoItsHaveIsNotReadAsZeroStock()
        {
            WorldModel w = World();
            Node(w, "N1");

            var demands = new List<DemandEntry>
            {
                DemandEntry.ForShip("F", "Trade", "N1", Now.AddMinutes(5), postedUtc: Now),
            };

            DemandSnapshotRow row = Assert.Single(
                DemandSnapshotOrder.RankForSnapshot(demands, w, TestSupport.Tuning()));

            Assert.False(row.Scored);
            Assert.Equal(0, row.HaveAtNode);
        }

        [Fact]
        public void RankingDoesNotMutateTheCallersList()
        {
            WorldModel w = World();
            SpatialNode stocked = Node(w, "Stocked");
            Node(w, "Starving");
            stocked.Inventory["Comp:Construction"] = 990;

            var demands = new List<DemandEntry>
            {
                Item("Comp:Construction", "Stocked", 1_000, DemandClass.Catalog),
                Item("Comp:Thrust", "Starving", 31, DemandClass.ShipBuild, Now.AddHours(-9)),
            };

            List<DemandSnapshotRow> ranked = DemandSnapshotOrder.RankForSnapshot(demands, w, TestSupport.Tuning());

            // The ranked rows are the snapshot; the register's own snapshot list is left as it came.
            Assert.Equal("Item:Comp:Thrust", ranked[0].Demand.Kind);
            Assert.Equal("Item:Comp:Construction", demands[0].Kind);
        }

        [Fact]
        public void EmptyAndSingleAreNoOps()
        {
            WorldModel w = World();
            var empty = new List<DemandEntry>();
            Assert.Empty(DemandSnapshotOrder.RankForSnapshot(empty, w, TestSupport.Tuning()));

            Node(w, "N1");
            var one = new List<DemandEntry> { Item("Comp:Thrust", "N1", 31, DemandClass.ShipBuild) };
            List<DemandSnapshotRow> ranked = DemandSnapshotOrder.RankForSnapshot(one, w, TestSupport.Tuning());
            Assert.Equal("Item:Comp:Thrust", Assert.Single(ranked).Demand.Kind);
        }
    }
}
