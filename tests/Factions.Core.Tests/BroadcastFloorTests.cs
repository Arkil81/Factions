using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-140: a broadcasting grid is detected at least as far as it announces itself,
    /// because SE's own antenna HUD already draws it there.</summary>
    public sealed class BroadcastFloorTests
    {
        private static readonly Vec3 Origin = new Vec3(0, 0, 0);

        [Fact]
        public void TheFloorLifts_AQuietHullThatIsShouting()
        {
            // FAC-140's own first row: a 3 t scout is signature 0.20, so a 100 km observer resolves
            // it at 20 km -- while SE draws it on the HUD out to the 50 km it is broadcasting.
            Assert.Equal(20.0, Sensing.DetectionRangeKm(100, 0.20), 9);
            Assert.Equal(50.0, Sensing.DetectionRangeKm(100, 0.20, 50.0), 9);
        }

        [Fact]
        public void ItIsAFloorAndNeverACap()
        {
            // A 10,000 t hull reads 3.42 and is found at 342 km. Its 50 km antenna must not shrink
            // that -- the vanilla channel is a minimum, not the rule.
            double loud = Sensing.DetectionRangeKm(100, 3.424);
            Assert.Equal(loud, Sensing.DetectionRangeKm(100, 3.424, 50.0), 9);
            Assert.True(loud > 50.0);
        }

        [Fact]
        public void NotBroadcasting_ChangesNothing()
        {
            Assert.Equal(Sensing.DetectionRangeKm(25, 0.5),
                         Sensing.DetectionRangeKm(25, 0.5, 0.0), 9);
            Assert.Equal(Sensing.DetectionRangeKm(25, 0.5),
                         Sensing.DetectionRangeKm(25, 0.5, -5.0), 9);
        }

        [Fact]
        public void AnObserverWithNoSensorAtAllStillDetectsNothing()
        {
            // The floor answers "how far does this carry", not "is anyone listening". A faction with
            // no sensor covering the space does not acquire a contact because the target is loud.
            Assert.Equal(0.0, Sensing.DetectionRangeKm(0, 1.0, 500.0), 9);
            Assert.False(Sensing.Detects(Origin, 0, new Vec3(10, 0, 0), 1.0, 500.0));
        }

        [Fact]
        public void AFactionSeesAQuietPlayerWhoIsBroadcastingPastItsSignatureRange()
        {
            var sensors = new List<SensorSource>
            {
                new SensorSource { PositionKm = Origin, StrengthKm = 25.0 }   // a bare station
            };
            // Signature 0.20 gives 5 km. The grid sits at 30 km, broadcasting 50 km.
            var quiet = new PlayerContact
            {
                IdentityId = 7,
                PositionKm = new Vec3(30, 0, 0),
                Signature = 0.20
            };
            var contacts = new List<PlayerContact> { quiet };
            var into = new List<PlayerContact>();

            Assert.Equal(0, PlayerSensing.Detect(sensors, contacts, into));

            quiet.BroadcastRadiusKm = 50.0;
            contacts[0] = quiet;
            Assert.Equal(1, PlayerSensing.Detect(sensors, contacts, into));
            Assert.Equal(7, into[0].IdentityId);
        }

        [Fact]
        public void TheHudScannerAppliesItToAMaterialisedFleet()
        {
            var fleet = new ScannerFleetCandidate
            {
                Key = "f1",
                PositionKm = new Vec3(60, 0, 0),
                Signature = 0.20,      // 25 km sensor x 0.20 = 5 km
                EntityId = 4242
            };
            Assert.Empty(PlayerScanner.Resolve(1, Origin, 25.0, null, new[] { fleet }));

            fleet.BroadcastRadiusKm = 80.0;
            IList<ScannerContact> contacts = PlayerScanner.Resolve(1, Origin, 25.0, null, new[] { fleet });
            Assert.Single(contacts);
            Assert.Equal(ScannerContactKind.Fleet, contacts[0].Kind);
        }

        [Fact]
        public void AScannerlessPlayerGetsNothing_HoweverLoudlyItBroadcasts()
        {
            // Level 0 is the on/off gate and sits above the whole rule.
            Assert.Empty(PlayerScanner.Resolve(0, Origin, 25.0, null, new[]
            {
                new ScannerFleetCandidate
                {
                    Key = "f1", PositionKm = new Vec3(10, 0, 0), Signature = 0.2, BroadcastRadiusKm = 500.0
                }
            }));
        }
    }
}
