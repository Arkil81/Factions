using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The founded-station claim held against vanilla's cell generator: which ids qualify, the
    /// DROPPED/ADDED lines a resync writes when the set moves, and how a station vanilla is about to
    /// spawn is described against the claim's history.
    /// </summary>
    public class StationClaimLedgerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

        private static SpatialGraph Graph(params SpatialNode[] nodes)
        {
            var g = new SpatialGraph();
            foreach (SpatialNode n in nodes) g.Nodes[n.Id] = n;
            return g;
        }

        private static SpatialNode Founded(string id, long stationId, string owner = "IGSL")
        {
            return new SpatialNode { Id = id, StableStationId = stationId, OwnerFactionTag = owner };
        }

        [Fact]
        public void Claim_CoversOwnedFoundedNodesOnly()
        {
            var ledger = new StationClaimLedger();
            ledger.Rebuild(Graph(
                Founded("N1", 101),
                Founded("N2", 102, owner: null),
                new SpatialNode { Id = "N3", OwnerFactionTag = "IGSL" }), T0, null);

            Assert.Equal(1, ledger.Count);
            Assert.True(ledger.Contains(101));
            Assert.False(ledger.Contains(102));
        }

        [Fact]
        public void FirstBuild_IsAChange()
        {
            var ledger = new StationClaimLedger();
            bool changed = ledger.Rebuild(Graph(Founded("N1", 101)), T0, null);
            Assert.True(changed);
        }

        [Fact]
        public void SteadyResync_WritesNoLines()
        {
            var ledger = new StationClaimLedger();
            SpatialGraph g = Graph(Founded("N1", 101), Founded("N2", 102));
            ledger.Rebuild(g, T0, null);

            var lines = new List<string>();
            bool changed = ledger.Rebuild(g, T0.AddSeconds(30), lines);

            Assert.False(changed);
            Assert.Empty(lines);
        }

        [Fact]
        public void TransientlyUnownedNode_IsDroppedThenReAdded_WithTheGap()
        {
            var ledger = new StationClaimLedger();
            SpatialNode n405 = Founded("N405", 905304442631608186);
            SpatialGraph g = Graph(n405);
            ledger.Rebuild(g, T0, null);

            n405.OwnerFactionTag = null;
            var lines = new List<string>();
            Assert.True(ledger.Rebuild(g, T0.AddSeconds(30), lines));
            Assert.Equal(new[] { "STATION_CLAIM_DROPPED station=905304442631608186 node=N405 reason=unowned" }, lines);
            Assert.Equal("claim=dropped node=N405 ago=12s",
                ledger.DescribeUnclaimed(905304442631608186, T0.AddSeconds(42)));

            n405.OwnerFactionTag = "IGSL";
            lines.Clear();
            Assert.True(ledger.Rebuild(g, T0.AddSeconds(60), lines));
            Assert.Equal(new[] { "STATION_CLAIM_ADDED station=905304442631608186 node=N405 after=30s" }, lines);
        }

        [Fact]
        public void DropReason_NamesWhatChangedOnTheNode()
        {
            var ledger = new StationClaimLedger();
            SpatialNode a = Founded("A", 1), b = Founded("B", 2), c = Founded("C", 3);
            SpatialGraph g = Graph(a, b, c);
            ledger.Rebuild(g, T0, null);

            b.StableStationId = 0;
            c.StableStationId = 33;
            g.Nodes.Remove("A");
            var lines = new List<string>();
            ledger.Rebuild(g, T0.AddSeconds(30), lines);

            Assert.Contains("STATION_CLAIM_DROPPED station=1 node=A reason=node_gone", lines);
            Assert.Contains("STATION_CLAIM_DROPPED station=2 node=B reason=unfounded", lines);
            Assert.Contains("STATION_CLAIM_DROPPED station=3 node=C reason=restationed now=33", lines);
            Assert.Contains("STATION_CLAIM_ADDED station=33 node=C", lines);
            Assert.Equal(4, lines.Count);
        }

        [Fact]
        public void DescribeUnclaimed_DistinguishesNeverHeldAndReset()
        {
            var ledger = new StationClaimLedger();
            SpatialNode n = Founded("N1", 101);
            SpatialGraph g = Graph(n);
            ledger.Rebuild(g, T0, null);

            Assert.Equal("claim=never", ledger.DescribeUnclaimed(999, T0));
            Assert.Equal("claim=held node=N1", ledger.DescribeUnclaimed(101, T0));

            n.OwnerFactionTag = null;
            ledger.Rebuild(g, T0.AddSeconds(30), new List<string>());
            Assert.StartsWith("claim=dropped", ledger.DescribeUnclaimed(101, T0.AddSeconds(31)));

            ledger.Reset();
            Assert.Equal("claim=never", ledger.DescribeUnclaimed(101, T0.AddSeconds(31)));
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void FlappingClaim_WritesOnePairPerWindow_AndCountsTheRest()
        {
            var ledger = new StationClaimLedger();
            SpatialNode n = Founded("N1", 101);
            SpatialGraph g = Graph(n);
            ledger.Rebuild(g, T0, null);

            var lines = new List<string>();
            DateTime t = T0;
            for (int i = 0; i < 6; i++)   // three drop/add flaps, 30 s apart
            {
                t = t.AddSeconds(30);
                n.OwnerFactionTag = (i % 2 == 0) ? null : "IGSL";
                ledger.Rebuild(g, t, lines);
            }
            Assert.Equal(2, lines.Count);
            Assert.StartsWith("STATION_CLAIM_DROPPED station=101", lines[0]);
            Assert.StartsWith("STATION_CLAIM_ADDED station=101", lines[1]);

            lines.Clear();
            n.OwnerFactionTag = null;
            ledger.Rebuild(g, T0 + StationClaimLedger.RepeatWindow + TimeSpan.FromMinutes(1), lines);
            Assert.Equal(new[] { "STATION_CLAIM_DROPPED station=101 node=N1 reason=unowned repeats=2" }, lines);
        }

        [Fact]
        public void WholeGraphDrop_IsOneSummaryLine()
        {
            var ledger = new StationClaimLedger();
            var nodes = new List<SpatialNode>();
            for (int i = 0; i < StationClaimLedger.BulkThreshold + 5; i++) nodes.Add(Founded("N" + i, 1000 + i));
            SpatialGraph g = Graph(nodes.ToArray());
            ledger.Rebuild(g, T0, null);

            g.Nodes.Clear();
            var lines = new List<string>();
            ledger.Rebuild(g, T0.AddSeconds(30), lines);

            Assert.Equal(new[] { "STATION_CLAIM_DROPPED_BULK count=25 reasons=node_gone:25" }, lines);
            Assert.StartsWith("claim=dropped node=N0", ledger.DescribeUnclaimed(1000, T0.AddSeconds(31)));
        }

        [Fact]
        public void VanillaSpawn_IsAdmittedOncePerWindowPerStation()
        {
            var ledger = new StationClaimLedger();
            int held;
            Assert.True(ledger.AdmitVanillaSpawn(7, T0, out held));
            Assert.Equal(0, held);
            Assert.False(ledger.AdmitVanillaSpawn(7, T0.AddMinutes(1), out held));
            Assert.True(ledger.AdmitVanillaSpawn(8, T0.AddMinutes(1), out held));
            Assert.True(ledger.AdmitVanillaSpawn(7, T0 + StationClaimLedger.RepeatWindow, out held));
            Assert.Equal(1, held);
        }

        [Fact]
        public void Covers_AClaimedIdWhateverItsFaction()
        {
            Assert.True(StationClaimLedger.Covers(101, 9, new HashSet<long> { 101 }, new HashSet<long>()));
        }

        [Fact]
        public void Covers_AnUnclaimedStationOfARegistryFaction()
        {
            // A founding not yet resynced, or a node dropped for one resync: the id is not claimed,
            // and vanilla must still stay out.
            Assert.True(StationClaimLedger.Covers(102, 5, new HashSet<long> { 101 }, new HashSet<long> { 5 }));
        }

        [Fact]
        public void LeavesVanilla_AnUnclaimedStationOfAFactionOutsideTheRegistry()
        {
            Assert.False(StationClaimLedger.Covers(102, 9, new HashSet<long> { 101 }, new HashSet<long> { 5 }));
        }

        [Fact]
        public void VanillaBlock_HasItsOwnWindow_SeparateFromVanillaSpawn()
        {
            var ledger = new StationClaimLedger();
            int held;
            Assert.True(ledger.AdmitVanillaSpawn(7, T0, out held));
            Assert.True(ledger.AdmitVanillaBlock(7, T0, out held));
            Assert.False(ledger.AdmitVanillaBlock(7, T0.AddMinutes(1), out held));
            Assert.False(ledger.AdmitVanillaBlock(7, T0.AddMinutes(2), out held));
            Assert.True(ledger.AdmitVanillaBlock(7, T0 + StationClaimLedger.RepeatWindow, out held));
            Assert.Equal(2, held);
        }

        [Fact]
        public void Reset_ReopensTheVanillaBlockWindow()
        {
            var ledger = new StationClaimLedger();
            int held;
            Assert.True(ledger.AdmitVanillaBlock(7, T0, out held));
            ledger.Reset();
            Assert.True(ledger.AdmitVanillaBlock(7, T0.AddMinutes(1), out held));
        }
    }
}
