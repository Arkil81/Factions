using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Player grids on a Nexus agent's shard reach the authority's scanner, and the bridge
    /// verbs that list and probe the grid candidates.</summary>
    public class ScannerGridsAcrossShardsTests
    {
        private static ScannerGridCandidate Grid(long id, double xKm, long owner, string tag, string name)
        {
            return new ScannerGridCandidate
            {
                EntityId = id, PositionKm = new Vec3(xKm, 2.25, -3), VelocityKmPerMin = new Vec3(0.5, 0, 0),
                Signature = 1.25, BroadcastRadiusKm = 4, OwnerIdentityId = owner, OwnerTag = tag, OwnerName = name,
                Large = true, Blocks = 340, Name = "Hauler Mk2", WeaponDps = 5000,
            };
        }

        [Fact]
        public void AgentReport_CarriesScannerGrids_ThroughEncodeAndDecode()
        {
            var report = new NexusAgentReport
            {
                ScannerGrids = new List<NexusScannerGrid> { NexusScannerGrid.From(Grid(5, 1000.1234, 501, "ABC", "Alice")) },
            };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            ScannerGridCandidate got = Assert.Single(back.ScannerGrids).ToCandidate();
            Assert.Equal(5, got.EntityId);
            Assert.Equal(501, got.OwnerIdentityId);
            Assert.Equal("ABC", got.OwnerTag);
            Assert.Equal("Alice", got.OwnerName);
            Assert.Equal(1000.123, got.PositionKm.X, 6);
            Assert.Equal(0.5, got.VelocityKmPerMin.X, 6);
            Assert.True(got.Large);
            Assert.Equal(340, got.Blocks);
            Assert.Equal("Hauler Mk2", got.Name);
            Assert.Equal(5000, got.WeaponDps, 6);
            Assert.Equal(4, got.BroadcastRadiusKm, 6);
            Assert.False(got.OwnerOffline);
            Assert.True(got.Remote);
        }

        [Fact]
        public void AgentReport_WithoutScannerGrids_DecodesWithNone()
        {
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(new NexusAgentReport().Encode(), out back));
            Assert.Null(back.ScannerGrids);
        }

        [Fact]
        public void Book_AppendsFreshAgentGrids_SkipsLocalAndStaleOnes()
        {
            DateTime now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
            var book = new NexusScannerGridBook();
            book.Update(1, new List<NexusScannerGrid>
            {
                NexusScannerGrid.From(Grid(5, 10, 501, "ABC", "Alice")),
                NexusScannerGrid.From(Grid(6, 20, 502, null, "Bob")),
                NexusScannerGrid.From(Grid(7, 30, 0, null, null)),
            }, now.AddSeconds(-10));
            book.Update(2, new List<NexusScannerGrid> { NexusScannerGrid.From(Grid(8, 40, 503, "XYZ", "Cy")) },
                        now.AddSeconds(-90));
            book.Update(3, null, now);   // a report that did not sample keeps nothing new

            var into = new List<ScannerGridCandidate> { Grid(6, 21, 502, null, "Bob") };
            var skip = new HashSet<long> { 6 };
            book.AppendFresh(into, now, TimeSpan.FromSeconds(60), skip);

            Assert.Equal(2, into.Count);
            Assert.Equal(6, into[0].EntityId);
            Assert.Equal(21, into[0].PositionKm.X, 6);
            Assert.Equal(5, into[1].EntityId);
            Assert.False(into[0].Remote);
            Assert.True(into[1].Remote);
            Assert.Contains("remote=1", ScannerGrids.DescribeCandidates(into, false, 40));
            Assert.Contains("Alice [ABC] remote large", ScannerGrids.DescribeCandidate(into[1]));
        }

        [Fact]
        public void Book_AnAgentGridIsScannedByAPlayerOnAnotherShard()
        {
            DateTime now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
            var book = new NexusScannerGridBook();
            book.Update(1, new List<NexusScannerGrid> { NexusScannerGrid.From(Grid(5, 3, 501, "ABC", "Alice")) }, now);
            var grids = new List<ScannerGridCandidate>();
            book.AppendFresh(grids, now, TimeSpan.FromSeconds(60), new HashSet<long>());

            IList<ScannerContact> contacts = PlayerScanner.Resolve(1, new Vec3(0, 2, -3), 10, null, null,
                TestSupport.Tuning(), null, null, null, grids, 0);
            ScannerContact c = Assert.Single(contacts);
            Assert.Equal(ScannerContactKind.Grid, c.Kind);
            Assert.Equal(5, c.EntityId);
        }

        [Fact]
        public void DescribeCandidates_CountsByOwnerClass_AndListsPlayersFirst()
        {
            var grids = new List<ScannerGridCandidate>
            {
                Grid(7, 1, 0, null, null),
                Grid(6, 2, 900, "SPRT", null),
                Grid(5, 3, 501, "ABC", "Alice"),
            };
            string all = ScannerGrids.DescribeCandidates(grids, false, 40);
            string[] lines = all.Split('\n');
            Assert.Equal("scanner grids: candidates=3 player=1 npc=1 unowned=1 remote=0", lines[0]);
            Assert.Equal(4, lines.Length);
            Assert.StartsWith("  5 player Alice [ABC] large blocks=340 pos=3.000 2.250 -3.000", lines[1]);
            Assert.Contains("name=\"Hauler Mk2\"", lines[1]);
            Assert.StartsWith("  7 unowned Unowned", lines[2]);
            Assert.StartsWith("  6 npc [SPRT]", lines[3]);

            string players = ScannerGrids.DescribeCandidates(grids, true, 40);
            Assert.Equal(2, players.Split('\n').Length);

            string capped = ScannerGrids.DescribeCandidates(grids, false, 1);
            Assert.EndsWith("\n  ... 2 more", capped);
        }

        [Fact]
        public void DescribeCandidate_MarksAnOfflineOwner()
        {
            ScannerGridCandidate g = Grid(5, 3, 501, "ABC", "Alice");
            g.OwnerOffline = true;
            Assert.Contains("Alice [ABC] (offline) large", ScannerGrids.DescribeCandidate(g));
        }

        [Fact]
        public void DescribeContact_ShowsTheWithheldLabelAndDistance()
        {
            ScannerContact c = ScannerGrids.ToContact(Grid(5, 3, 501, "ABC", "Alice"),
                                                      new SensorReading { Detected = true, SensorSees = true });
            c.Tier = IntelTier.Class;
            ScannerGrids.Withhold(ref c, TestSupport.Tuning());
            Assert.Equal("grid 5 tier=Class dKm=3.00 label=\"Large grid, 340 blocks\"",
                         ScannerGrids.DescribeContact(c, new Vec3(0, 2.25, -3)));

            c = ScannerGrids.ToContact(Grid(5, 3, 501, "ABC", "Alice"),
                                       new SensorReading { Detected = true, SensorSees = true });
            c.Tier = IntelTier.Identity;
            ScannerGrids.Withhold(ref c, TestSupport.Tuning());
            Assert.Equal("grid 5 tier=Identity dKm=3.00 label=\"Alice [ABC] · Large grid, 340 blocks\" name=\"Hauler Mk2\"",
                         ScannerGrids.DescribeContact(c, new Vec3(0, 2.25, -3)));
        }
    }
}
