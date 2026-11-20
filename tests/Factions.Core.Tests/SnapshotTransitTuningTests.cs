using System;
using System.Collections.Generic;
using System.IO;
using Factions.Api;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The dashboard draws every fleet row from a <see cref="FactionTuning"/> the snapshot loads
    /// itself, off disk. The three transit fields carry no initialiser and
    /// <see cref="FactionTuning.TransitProgressRatio"/> guards on them, so which directory those
    /// files are looked for in decides whether a fleet is drawn moving or drawn complete.
    /// </summary>
    public class SnapshotTransitTuningTests : IDisposable
    {
        private const double LiveSpeed = 500.0;
        private const double LiveSpeedMin = 30.0;
        private const double LiveAccel = 4608.0;

        private readonly string _root;
        private readonly string _dataDir;
        private readonly string _instanceDir;

        public SnapshotTransitTuningTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "fac242-" + Guid.NewGuid().ToString("N"));
            _dataDir = Path.Combine(_root, "awl-shared", "Factions");
            _instanceDir = Path.Combine(_root, "Instance", "Factions");
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(_instanceDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        /// <summary>One unresolved fleet, 5,000 km of leg, two minutes into a twenty-minute flight:
        /// far enough from either end that a correct profile cannot round to 0 or 1.</summary>
        private void WriteWorldWithOneFleetInTransit()
        {
            var store = new FactionStore(_dataDir);
            store.SaveIndex(new List<IndexEntry>
            {
                new IndexEntry { Tag = "TEST", PersonalityId = "default" },
            });
            store.SaveBrain(new BrainDoc
            {
                Tag = "TEST",
                PendingFleets = new List<PendingFleetDoc>
                {
                    new PendingFleetDoc
                    {
                        FleetId = "abc123",
                        Kind = "Trade",
                        ActorTag = "TEST",
                        OriginNodeId = "N001",
                        TargetNodeId = "N002",
                        TargetX = 5000.0,
                        DepartedUtc = DateTime.UtcNow.AddMinutes(-2),
                        EstimatedArrivalUtc = DateTime.UtcNow.AddMinutes(18),
                        Resolved = false,
                    },
                },
            });
        }

        private static void WriteTuning(string dir, string fileName, string body)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), body);
        }

        private static string LiveTransitJson()
        {
            return "{\"FleetSpeedKmPerMin\":500.0,\"FleetSpeedMinKmPerMin\":30.0,"
                 + "\"FleetAccelKmPerMin2\":4608.0}";
        }

        private static FleetDto OnlyFleet(WorldSnapshotDto snap)
        {
            Assert.Empty(snap.BrainLoadFailures);
            return Assert.Single(snap.Fleets);
        }

        [Fact]
        public void TransitTuning_LivesInTheInstanceDirOnly_FleetKeepsTheLiveSpeedAndPartialProgress()
        {
            WriteWorldWithOneFleetInTransit();
            WriteTuning(_instanceDir, "tuning.json", LiveTransitJson());

            using (var svc = new SnapshotService(_dataDir, _instanceDir))
            {
                WorldSnapshotDto snap = svc.GetSnapshot();
                FleetDto fleet = OnlyFleet(snap);

                Assert.Equal(LiveSpeed, fleet.FleetSpeedKmPerMin);
                Assert.Equal(LiveSpeedMin, fleet.FleetSpeedMinKmPerMin);
                Assert.Equal(LiveAccel, fleet.FleetAccelKmPerMin2);
                // The characteristic failure is progress pinned at 1 on every row.
                Assert.InRange(fleet.TransitProgressRatio, 0.001, 0.999);
                Assert.Empty(snap.InputLoadFailures);
            }
        }

        [Fact]
        public void TransitTuning_DefaultsAndOverrideBothInTheInstanceDir_LayerAsTheyDoOnTheServer()
        {
            WriteWorldWithOneFleetInTransit();
            WriteTuning(_instanceDir, "tuning_defaults.json",
                "{\"FleetSpeedKmPerMin\":100.0,\"FleetSpeedMinKmPerMin\":30.0,"
                + "\"FleetAccelKmPerMin2\":4608.0}");
            WriteTuning(_instanceDir, "tuning.json", "{\"FleetSpeedKmPerMin\":500.0}");

            using (var svc = new SnapshotService(_dataDir, _instanceDir))
            {
                FleetDto fleet = OnlyFleet(svc.GetSnapshot());

                // Defaults supply the accel the override omits; the override wins on speed.
                Assert.Equal(LiveSpeed, fleet.FleetSpeedKmPerMin);
                Assert.Equal(LiveAccel, fleet.FleetAccelKmPerMin2);
            }
        }

        [Fact]
        public void TransitTuning_SingleRootDeployment_StillReadsFromTheDataDirectory()
        {
            WriteWorldWithOneFleetInTransit();
            WriteTuning(_dataDir, "tuning.json", LiveTransitJson());

            using (var svc = new SnapshotService(_dataDir))
            {
                FleetDto fleet = OnlyFleet(svc.GetSnapshot());

                Assert.Equal(LiveSpeed, fleet.FleetSpeedKmPerMin);
                Assert.InRange(fleet.TransitProgressRatio, 0.001, 0.999);
            }
        }

        [Fact]
        public void TransitTuning_AbsentFromBothDirectories_IsReportedRatherThanDrawnAsArrived()
        {
            WriteWorldWithOneFleetInTransit();

            using (var svc = new SnapshotService(_dataDir, _instanceDir))
            {
                WorldSnapshotDto snap = svc.GetSnapshot();
                FleetDto fleet = OnlyFleet(snap);

                // The degenerate render is unchanged -- there is no speed to draw. What changes is
                // that /api/status can now say why, instead of the row reading as an arrival.
                Assert.Equal(0.0, fleet.FleetSpeedKmPerMin);
                Assert.Equal(1.0, fleet.TransitProgressRatio);

                InputLoadFailureDto failure = Assert.Single(snap.InputLoadFailures);
                Assert.Contains("tuning.json", failure.Input);
                Assert.Contains("FleetSpeedKmPerMin=0", failure.Error);
                Assert.Contains(_dataDir, failure.Error);
                Assert.Contains(_instanceDir, failure.Error);
            }
        }
    }
}
