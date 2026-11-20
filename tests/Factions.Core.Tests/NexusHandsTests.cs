using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The authority-to-agent node snapshot, the agent-to-authority report, and the asteroid field
    /// layout that lets two servers place the same rock without telling each other where it went.
    /// </summary>
    public class NexusHandsTests
    {
        private static AsteroidFieldPlan Plan(int count)
        {
            return new AsteroidFieldPlan { Count = count, SizeMin = 100, SizeMax = 400, RadiusKm = 5, BaseMaterialOreName = "Stone" };
        }

        [Fact]
        public void Layout_IsAPureFunctionOfTheNodeId()
        {
            List<AsteroidPlacement> a = AsteroidFieldLayout.Layout("N042", Plan(6), 0.7, false, 500);
            List<AsteroidPlacement> b = AsteroidFieldLayout.Layout("N042", Plan(6), 0.7, false, 500);
            Assert.Equal(6, a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].OffsetM.X, b[i].OffsetM.X);
                Assert.Equal(a[i].OffsetM.Z, b[i].OffsetM.Z);
                Assert.Equal(a[i].Size, b[i].Size);
                Assert.Equal(a[i].ShapeSeed, b[i].ShapeSeed);
                Assert.Equal(a[i].MaterialRoll, b[i].MaterialRoll);
            }
            List<AsteroidPlacement> other = AsteroidFieldLayout.Layout("N043", Plan(6), 0.7, false, 500);
            Assert.NotEqual(a[0].OffsetM.X, other[0].OffsetM.X);
        }

        [Fact]
        public void Layout_AsteroidIDoesNotDependOnHowManyCameBefore()
        {
            List<AsteroidPlacement> three = AsteroidFieldLayout.Layout("N007", Plan(3), 0.5, false, 500);
            List<AsteroidPlacement> six = AsteroidFieldLayout.Layout("N007", Plan(6), 0.5, false, 500);
            for (int i = 0; i < 3; i++) Assert.Equal(three[i].OffsetM.Y, six[i].OffsetM.Y);
        }

        [Fact]
        public void Layout_StaysInsideTheRadiusAndClearOfAFoundedStation()
        {
            List<AsteroidPlacement> field = AsteroidFieldLayout.Layout("N100", Plan(40), 1.0, true, 500);
            foreach (AsteroidPlacement p in field)
            {
                double r = p.OffsetM.DistanceTo(new Vec3(0, 0, 0));
                Assert.True(r <= 5000.0 + 1e-6, "outside the field radius: " + r);
                Assert.True(r >= 500 + p.Size + 100.0 - 1e-6, "inside the station clearance: " + r);
                Assert.True(p.Size >= 400 * 0.85 - 1e-9 && p.Size <= 400 * 1.15 + 1e-9);
            }
        }

        [Fact]
        public void FieldSeed_IsNonzeroAndStable()
        {
            Assert.NotEqual(0, AsteroidFieldLayout.FieldSeed("N001"));
            Assert.NotEqual(0, AsteroidFieldLayout.FieldSeed(""));
            Assert.Equal(AsteroidFieldLayout.FieldSeed("N001"), AsteroidFieldLayout.FieldSeed("N001"));
            // Pinned: a seed that drifts between releases re-reads every placed field as unplaced
            // on an agent built from a different commit.
            Assert.Equal(WorldRecipe.StableHash("N001") | 1, AsteroidFieldLayout.FieldSeed("N001"));
        }

        private static SpatialGraph SampleGraph()
        {
            var g = new SpatialGraph();
            SpatialNode a = g.Add(new Vec3(-1234.5, 6.25, 789), 0.42, "N001");
            a.Zone = NodeZone.DeepSpace;
            a.OwnerFactionTag = "SPRT";
            a.StableStationId = 9001;
            a.StationPrefabId = "Outpost_A";
            a.PendingStationPrefabId = "Outpost_B";
            a.ZoneBreachedUtc = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
            a.SafeZoneDownUntilUtc = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
            a.SiegeStartedUtc = new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc);
            a.ZoneIntegrityAtUtc = new DateTime(2026, 11, 20, 7, 30, 0, DateTimeKind.Utc);
            a.AsteroidFieldSeed = 77;
            a.Deposit = new NodeDeposit();
            a.Deposit.Entries.Add(new DepositEntry("Ice", 5.0, 0));
            a.Deposit.Entries.Add(new DepositEntry("Iron", 2.0, 0));
            SpatialNode b = g.Add(new Vec3(1, 2, 3), 0, "N002");
            b.Zone = NodeZone.Surface;
            b.Site = StationSite.Surface;
            b.DisplayName = "Tellus Landing";
            b.PlanetId = "Tellus";
            b.Deposit = new NodeDeposit();   // barren
            g.Add(new Vec3(4, 5, 6), 0.1, "N003");   // never surveyed: no deposit
            return g;
        }

        [Fact]
        public void Snapshot_RoundTripsWhatMaterialisationReads()
        {
            SpatialGraph src = SampleGraph();
            var factions = new List<FactionState>
            {
                new FactionState { Tag = "SPRT", IsPlayerFaction = false, PersonalityId = "pirate" },
                new FactionState { Tag = "EXP", IsPlayerFaction = true },
            };
            byte[] bytes = NexusNodeSnapshot.Build(src.Nodes.Values, factions, 12).Encode();

            NexusNodeSnapshot read;
            Assert.True(NexusNodeSnapshot.TryDecode(bytes, out read));
            Assert.Equal(12, read.Sequence);
            SpatialGraph g = read.ToGraph();
            Assert.Equal(3, g.Nodes.Count);

            SpatialNode a = g.Nodes["N001"];
            Assert.Equal(-1234.5, a.Position.X);
            Assert.Equal(0.42, a.ResourceValue);
            Assert.Equal("SPRT", a.OwnerFactionTag);
            Assert.Equal(9001, a.StableStationId);
            Assert.Equal("Outpost_A", a.StationPrefabId);
            Assert.Equal("Outpost_B", a.PendingStationPrefabId);
            Assert.Equal(src.Nodes["N001"].ZoneBreachedUtc, a.ZoneBreachedUtc);
            Assert.Equal(src.Nodes["N001"].SafeZoneDownUntilUtc, a.SafeZoneDownUntilUtc);
            Assert.Equal(src.Nodes["N001"].SiegeStartedUtc, a.SiegeStartedUtc.ToUniversalTime());
            Assert.Equal(src.Nodes["N001"].ZoneIntegrityAtUtc, a.ZoneIntegrityAtUtc.ToUniversalTime());
            Assert.Equal(77, a.AsteroidFieldSeed);
            Assert.True(NodeContent.IsMineable(a));
            Assert.Equal("Ice", DepositModel.DominantOre(a.Deposit));

            SpatialNode b = g.Nodes["N002"];
            Assert.Equal(NodeZone.Surface, b.Zone);
            // Respawn placement on an agent tiers by site and names the waypoint after the node.
            Assert.Equal(StationSite.Surface, b.Site);
            Assert.Equal("Tellus Landing", b.DisplayName);
            Assert.Equal(StationSite.Space, a.Site);
            Assert.Null(a.DisplayName);
            Assert.Equal("Tellus", b.PlanetId);
            Assert.NotNull(b.Deposit);
            Assert.True(b.Deposit.IsBarren);
            Assert.Null(g.Nodes["N003"].Deposit);

            List<FactionState> fs = read.ToFactions();
            Assert.Equal(2, fs.Count);
            Assert.Equal("pirate", fs[0].PersonalityId);
            Assert.True(fs[1].IsPlayerFaction);
        }

        /// <summary>The authority's player index names a player on an agent from these; a report
        /// without them reads as none rather than as an empty roster.</summary>
        [Fact]
        public void Report_CarriesPlayerNamesAndAReportWithoutThemReadsNull()
        {
            var report = new NexusAgentReport
            {
                Session = "s",
                Names = new List<NexusPlayerName> { new NexusPlayerName { Steam = 76561198000000001UL, Name = "Ann" } },
            };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            Assert.Single(back.Names);
            Assert.Equal(76561198000000001UL, back.Names[0].Steam);
            Assert.Equal("Ann", back.Names[0].Name);

            Assert.True(NexusAgentReport.TryDecode(new NexusAgentReport { Session = "s" }.Encode(), out back));
            Assert.Null(back.Names);
        }

        [Fact]
        public void Snapshot_GarbageAndAnotherVersionAreRejected()
        {
            NexusNodeSnapshot read;
            Assert.False(NexusNodeSnapshot.TryDecode(new byte[] { 1, 2, 3, 4 }, out read));
            Assert.False(NexusNodeSnapshot.TryDecode(null, out read));
            var future = new NexusNodeSnapshot { Version = NexusNodeSnapshot.CurrentVersion + 1 };
            Assert.False(NexusNodeSnapshot.TryDecode(future.Encode(), out read));
        }

        [Fact]
        public void Report_StampsOnlyUnplacedFieldsAndIsIdempotent()
        {
            SpatialGraph g = SampleGraph();
            var report = new NexusAgentReport { AsteroidFields = new List<string> { "N001", "N003", "N999" } };
            NexusAgentReport read;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out read));

            Assert.Equal(1, read.ApplyTo(g));
            Assert.Equal(77, g.Nodes["N001"].AsteroidFieldSeed);   // already placed: untouched
            Assert.Equal(AsteroidFieldLayout.FieldSeed("N003"), g.Nodes["N003"].AsteroidFieldSeed);
            Assert.Equal(0, read.ApplyTo(g));
        }
    }
}
