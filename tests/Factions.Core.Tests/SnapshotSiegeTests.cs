using System;
using System.Collections.Generic;
using System.IO;
using Factions.Api;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A player siege reaches the dashboard, or it does not exist there. Nothing else on the wire
    /// carries it: a player shooting a station produces no <see cref="FleetDto"/>, so before
    /// FAC-258 the map drew an untouched world through a twenty-minute breach-and-raid.
    ///
    /// <para>These go through <see cref="SnapshotService"/> on real files rather than calling
    /// <c>BuildSiege</c>, because the failure this guards is the whole path: brain written, brain
    /// read, tuning found, DTO populated.</para>
    /// </summary>
    public class SnapshotSiegeTests : IDisposable
    {
        private readonly string _root;
        private readonly string _dataDir;
        private readonly string _instanceDir;

        public SnapshotSiegeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "fac258-" + Guid.NewGuid().ToString("N"));
            _dataDir = Path.Combine(_root, "awl-shared", "Factions");
            _instanceDir = Path.Combine(_root, "Instance", "Factions");
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(_instanceDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        /// <summary>One faction, two nodes: the besieged one and an untouched neighbour. The
        /// neighbour is not decoration — it is what makes "the siege is on the node" a real claim
        /// rather than "every node came back carrying one".</summary>
        private void WriteWorld(SiegeDoc siege)
        {
            var store = new FactionStore(_dataDir);
            store.SaveIndex(new List<IndexEntry>
            {
                new IndexEntry { Tag = "TEST", PersonalityId = "default" },
            });
            store.SaveBrain(new BrainDoc
            {
                Tag = "TEST",
                Sources = new List<SourceDoc>
                {
                    new SourceDoc { Id = "N001", X = 1, Y = 0, Z = 0, StableStationId = 55, Siege = siege },
                    new SourceDoc { Id = "N002", X = 2, Y = 0, Z = 0, StableStationId = 56 },
                },
            });
        }

        private static void WriteTuning(string dir, string fileName, string body)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), body);
        }

        private static NodeDto NodeOf(WorldSnapshotDto snap, string id)
        {
            Assert.Empty(snap.BrainLoadFailures);
            return Assert.Single(snap.Nodes, n => n.Id == id);
        }

        [Fact]
        public void ANodeUnderSiege_CarriesItsSiege_AndAnUntouchedNeighbourCarriesNone()
        {
            DateTime now = DateTime.UtcNow;
            // Half the pool gone, stamped now so the regeneration ramp adds nothing.
            WriteWorld(new SiegeDoc
            {
                ZoneIntegrity = 500.0,
                ZoneIntegrityAtUtc = now,
                SiegeStartedUtc = now.AddSeconds(-60),
            });

            using (var svc = new SnapshotService(_dataDir, _instanceDir))
            {
                WorldSnapshotDto snap = svc.GetSnapshot();

                SiegeDto siege = Assert.IsType<SiegeDto>(NodeOf(snap, "N001").Siege);
                // A live pool regenerates as the clock runs, so the band is one-sided rather than
                // an equality: what it has to separate is a half-worn shield from the two readings
                // that would hide a siege -- 1.0, the never-besieged sentinel, and 0.
                Assert.InRange(siege.Integrity, 0.49, 0.60);
                Assert.Equal(300.0, siege.DwellSeconds);
                Assert.Null(siege.BreachedUtc);
                Assert.Null(siege.SucceededUtc);
                Assert.Equal(0.0, siege.RaidProgress);

                // The characteristic failure is a siege object on every node in the world.
                Assert.Null(NodeOf(snap, "N002").Siege);
            }
        }

        [Fact]
        public void ANodeNobodyHasTouched_SendsNoSiegeAtAll()
        {
            WriteWorld(null);

            using (var svc = new SnapshotService(_dataDir, _instanceDir))
            {
                WorldSnapshotDto snap = svc.GetSnapshot();
                Assert.Null(NodeOf(snap, "N001").Siege);
                Assert.Null(NodeOf(snap, "N002").Siege);
            }
        }

        /// <summary>The pool is frozen while the zone is down, so an empty pool stamped minutes ago
        /// must still read 0 — a breached station whose shield reads as regenerated back to full is
        /// the reading that would tell an operator the fight is over while it is running.</summary>
        [Fact]
        public void ABreachedZone_ReadsEmptyAndFrozen_WithTheRaidProgressItHasAccumulated()
        {
            DateTime now = DateTime.UtcNow;
            WriteWorld(new SiegeDoc
            {
                ZoneIntegrity = 0.0,
                ZoneIntegrityAtUtc = now.AddMinutes(-5),
                SiegeStartedUtc = now.AddMinutes(-10),
                ZoneBreachedUtc = now.AddMinutes(-5),
                PlayerRaidBlockDamage = 5000.0,
            });

            using (var svc = new SnapshotService(_dataDir, _instanceDir))
            {
                SiegeDto siege = Assert.IsType<SiegeDto>(NodeOf(svc.GetSnapshot(), "N001").Siege);

                Assert.Equal(0.0, siege.Integrity);
                Assert.NotNull(siege.BreachedUtc);
                Assert.Equal(0.25, siege.RaidProgress, 3);   // 5,000 of the 20,000 needed
                Assert.Null(siege.SucceededUtc);
            }
        }

        /// <summary>Damage overshoots the threshold on the flush that wins, and a bar past 100%
        /// reads as a broken gauge rather than as a raid already won.</summary>
        [Fact]
        public void AWonRaid_ClampsItsProgressAndCarriesTheConquerClocksStart()
        {
            DateTime now = DateTime.UtcNow;
            WriteWorld(new SiegeDoc
            {
                ZoneIntegrity = 0.0,
                ZoneIntegrityAtUtc = now.AddMinutes(-8),
                SiegeStartedUtc = now.AddMinutes(-15),
                ZoneBreachedUtc = now.AddMinutes(-8),
                PlayerRaidBlockDamage = 24000.0,
                PlayerRaidSucceededUtc = now.AddMinutes(-2),
            });

            using (var svc = new SnapshotService(_dataDir, _instanceDir))
            {
                SiegeDto siege = Assert.IsType<SiegeDto>(NodeOf(svc.GetSnapshot(), "N001").Siege);

                Assert.Equal(1.0, siege.RaidProgress);
                Assert.NotNull(siege.SucceededUtc);
            }
        }

        /// <summary>The pool and the dwell are tunable, and the fraction is only meaningful against
        /// the pool the server is actually running. Read from the wrong file and a half-empty
        /// shield reads as a full one.</summary>
        [Fact]
        public void SiegeTuning_ComesFromTheInstanceDirectory_LikeEveryOtherPerRootInput()
        {
            DateTime now = DateTime.UtcNow;
            WriteTuning(_instanceDir, "tuning.json",
                "{\"RaidZoneIntegrityPool\":200.0,\"RaidZoneSiegeDwellSeconds\":600.0,"
                + "\"PlayerRaidBlockDamageForSuccess\":1000.0}");
            WriteWorld(new SiegeDoc
            {
                ZoneIntegrity = 50.0,
                ZoneIntegrityAtUtc = now,
                SiegeStartedUtc = now.AddSeconds(-30),
                ZoneBreachedUtc = now.AddSeconds(-1),
                PlayerRaidBlockDamage = 250.0,
            });

            using (var svc = new SnapshotService(_dataDir, _instanceDir))
            {
                SiegeDto siege = Assert.IsType<SiegeDto>(NodeOf(svc.GetSnapshot(), "N001").Siege);

                // Against the shipped 1,000-point pool this would read 0.05, not 0.25.
                Assert.Equal(0.25, siege.Integrity, 3);
                Assert.Equal(600.0, siege.DwellSeconds);
                Assert.Equal(0.25, siege.RaidProgress, 3);
            }
        }

        /// <summary>Defaults layer under the override exactly as they do for the transit contract:
        /// a tuning.json naming only the pool must not blank the dwell.</summary>
        [Fact]
        public void SiegeTuning_DefaultsAndOverrideLayer_AsTheyDoOnTheServer()
        {
            DateTime now = DateTime.UtcNow;
            WriteTuning(_instanceDir, "tuning_defaults.json",
                "{\"RaidZoneIntegrityPool\":1000.0,\"RaidZoneSiegeDwellSeconds\":450.0}");
            WriteTuning(_instanceDir, "tuning.json", "{\"RaidZoneIntegrityPool\":400.0}");
            // Breached, so the pool is frozen and the fraction is exact: this test is about which
            // file the pool came from, and a regenerating pool would make it about the clock.
            WriteWorld(new SiegeDoc
            {
                ZoneIntegrity = 100.0,
                ZoneIntegrityAtUtc = now.AddSeconds(-20),
                SiegeStartedUtc = now.AddSeconds(-30),
                ZoneBreachedUtc = now.AddSeconds(-1),
            });

            using (var svc = new SnapshotService(_dataDir, _instanceDir))
            {
                SiegeDto siege = Assert.IsType<SiegeDto>(NodeOf(svc.GetSnapshot(), "N001").Siege);

                Assert.Equal(0.25, siege.Integrity, 3);     // the override's pool
                Assert.Equal(450.0, siege.DwellSeconds);    // the default the override omits
            }
        }
    }
}
