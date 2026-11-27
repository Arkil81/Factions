using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The intelligence grade (FAC-526): the margin beside the detection verdict, the tier it earns,
    /// and the scanner contact that carries both. docs/fog-of-war.md → Intelligence grades.
    /// </summary>
    public class IntelGradeTests
    {
        private static readonly FactionTuning Tuning = new FactionTuning();
        private static readonly Vec3 Origin = new Vec3(0, 0, 0);

        private static SensorReading Seen(double marginDb)
        {
            return new SensorReading { Detected = true, SensorSees = true, MarginDb = marginDb };
        }

        // ---- margin -------------------------------------------------------------------------

        [Fact]
        public void Margin_IsZeroAtTheEdgeAndSixDbPerHalving()
        {
            Assert.Equal(0.0, IntelGrade.MarginDb(100, 100), 6);
            Assert.Equal(6.02, IntelGrade.MarginDb(100, 50), 2);
            Assert.Equal(12.04, IntelGrade.MarginDb(100, 25), 2);
            Assert.True(IntelGrade.MarginDb(100, 150) < 0);
        }

        [Fact]
        public void Margin_IsCappedOnTopOfTheSensorAndAbsentWithNoRange()
        {
            Assert.Equal(IntelGrade.MaxMarginDb, IntelGrade.MarginDb(100, 0));
            Assert.Equal(IntelGrade.MaxMarginDb, IntelGrade.MarginDb(100, 1e-9));
            Assert.True(double.IsNegativeInfinity(IntelGrade.MarginDb(0, 10)));
        }

        // ---- Measure agrees with Detects ----------------------------------------------------

        [Fact]
        public void Measure_ReturnsTheVerdictDetectsReturns_AcrossRangeBroadcastAndOcclusion()
        {
            var occlusion = new SensorOcclusion(new[]
            {
                new PlanetRecord
                {
                    Id = "P", Name = "Planet", Generator = "Tellus", Center = new Vec3(100, 0, 0),
                    SurfaceRadius = 30, AtmosphereRadius = 36, GravityRadius = 120
                }
            }, 2.0);
            double[] strengths = { 0, 25, 50, 100 };
            double[] signatures = { 0.5, 1.0, 2.0 };
            double[] broadcasts = { 0, 40, 250 };
            double[] xs = { 10, 49, 60, 99, 150, 199, 210, 260 };
            int checkedPairs = 0, detected = 0;
            foreach (SensorOcclusion occ in new[] { null, occlusion })
            foreach (double s in strengths)
            foreach (double sig in signatures)
            foreach (double b in broadcasts)
            foreach (double x in xs)
            {
                var target = new Vec3(x, 0, 0);
                bool expected = Sensing.Detects(Origin, s, target, sig, b, occ);
                SensorReading reading = Sensing.Measure(Origin, s, target, sig, b, occ);
                Assert.Equal(expected, reading.Detected);
                if (expected) detected++;
                checkedPairs++;
            }
            // Both outcomes occur, so agreement is not two functions that always say no.
            Assert.True(detected > 0 && detected < checkedPairs);
        }

        [Fact]
        public void Measure_CarriesTheSensorMarginOnSignatureRange()
        {
            // Signature 2.0 doubles a 50 km sensor to 100 km; at 25 km that is two halvings.
            SensorReading r = Sensing.Measure(Origin, 50, new Vec3(25, 0, 0), 2.0, 0, null);
            Assert.True(r.Detected);
            Assert.True(r.SensorSees);
            Assert.Equal(12.04, r.MarginDb, 2);
        }

        [Fact]
        public void Measure_BroadcastOnlyDetectionHasNoMargin()
        {
            // Beyond the 50 km sensor, inside a 200 km broadcast: the target is talking, nothing is measured.
            SensorReading r = Sensing.Measure(Origin, 50, new Vec3(150, 0, 0), 1.0, 200, null);
            Assert.True(r.Detected);
            Assert.False(r.SensorSees);
            Assert.True(double.IsNegativeInfinity(r.MarginDb));
            Assert.Equal(IntelTier.Contact, IntelGrade.Of(r, IntelTier.None, false, false, Tuning));
        }

        [Fact]
        public void Measure_BroadcastSeenPastAPlanetHasNoMargin()
        {
            var occlusion = new SensorOcclusion(new[]
            {
                new PlanetRecord
                {
                    Id = "P", Name = "Planet", Generator = "Tellus", Center = new Vec3(20, 0, 0),
                    SurfaceRadius = 8, AtmosphereRadius = 10, GravityRadius = 32
                }
            }, 2.0);
            var target = new Vec3(40, 0, 0);
            SensorReading blocked = Sensing.Measure(Origin, 100, target, 1.0, 0, occlusion);
            Assert.False(blocked.Detected);

            SensorReading heard = Sensing.Measure(Origin, 100, target, 1.0, 60, occlusion);
            Assert.True(heard.Detected);
            Assert.False(heard.SensorSees);
            Assert.True(double.IsNegativeInfinity(heard.MarginDb));
        }

        [Fact]
        public void Measure_WithoutMarginSkipsTheLogarithm()
        {
            SensorReading r = Sensing.Measure(Origin, 100, new Vec3(10, 0, 0), 1.0, 0, null, false);
            Assert.True(r.Detected);
            Assert.True(double.IsNegativeInfinity(r.MarginDb));
        }

        // ---- tiers --------------------------------------------------------------------------

        [Theory]
        [InlineData(0.0, IntelTier.Contact)]
        [InlineData(5.9, IntelTier.Contact)]
        [InlineData(6.0, IntelTier.Class)]
        [InlineData(9.9, IntelTier.Class)]
        [InlineData(10.0, IntelTier.Identity)]
        [InlineData(15.9, IntelTier.Identity)]
        [InlineData(16.0, IntelTier.Armament)]
        [InlineData(60.0, IntelTier.Armament)]
        public void ObservedTier_OpensEachTierAtItsThreshold(double marginDb, IntelTier expected)
        {
            Assert.Equal(expected, IntelGrade.ObservedTier(Seen(marginDb), IntelTier.None, Tuning));
        }

        [Fact]
        public void ObservedTier_NotDetectedIsNone()
        {
            Assert.Equal(IntelTier.None, IntelGrade.ObservedTier(SensorReading.NotDetected, IntelTier.Armament, Tuning));
        }

        [Fact]
        public void ObservedTier_AHeldTierSurvivesTwoDbBelowItsThreshold()
        {
            // 8.5 dB: Class from cold, but Identity (opens at 10) is held down to 8.
            Assert.Equal(IntelTier.Class, IntelGrade.ObservedTier(Seen(8.5), IntelTier.Class, Tuning));
            Assert.Equal(IntelTier.Identity, IntelGrade.ObservedTier(Seen(8.5), IntelTier.Identity, Tuning));
            Assert.Equal(IntelTier.Class, IntelGrade.ObservedTier(Seen(7.9), IntelTier.Identity, Tuning));
            // Holding a higher tier holds every tier under it.
            Assert.Equal(IntelTier.Armament, IntelGrade.ObservedTier(Seen(14.5), IntelTier.Armament, Tuning));
            Assert.Equal(IntelTier.Class, IntelGrade.ObservedTier(Seen(4.5), IntelTier.Armament, Tuning));
            Assert.Equal(IntelTier.Contact, IntelGrade.ObservedTier(Seen(3.9), IntelTier.Armament, Tuning));
        }

        [Fact]
        public void ObservedTier_AContactOnABoundaryDoesNotFlicker()
        {
            IntelTier tier = IntelTier.None;
            var seen = new List<IntelTier>();
            foreach (double m in new[] { 10.2, 9.6, 10.1, 9.2, 10.4, 8.7 })
            {
                tier = IntelGrade.ObservedTier(Seen(m), tier, Tuning);
                seen.Add(tier);
            }
            Assert.All(seen, t => Assert.Equal(IntelTier.Identity, t));
        }

        [Fact]
        public void ObservedTier_ReadsTheTunedThresholds()
        {
            var t = new FactionTuning { IntelClassDb = 3, IntelIdentityDb = 5, IntelArmamentDb = 7, IntelHysteresisDb = 0 };
            Assert.Equal(IntelTier.Armament, IntelGrade.ObservedTier(Seen(7), IntelTier.None, t));
            Assert.Equal(IntelTier.Identity, IntelGrade.ObservedTier(Seen(6.9), IntelTier.Armament, t));
        }

        [Fact]
        public void Of_OwnFactionIsFullAtAnyRangeAndUndetected()
        {
            Assert.Equal(IntelTier.Full, IntelGrade.Of(Seen(-20), IntelTier.None, true, false, Tuning));
            Assert.Equal(IntelTier.Full, IntelGrade.Of(SensorReading.NotDetected, IntelTier.None, true, false, Tuning));
        }

        [Fact]
        public void Of_ATransponderLiftsToIdentityButNeverCreatesADetection()
        {
            Assert.Equal(IntelTier.Identity, IntelGrade.Of(Seen(0.5), IntelTier.None, false, true, Tuning));
            Assert.Equal(IntelTier.Armament, IntelGrade.Of(Seen(20), IntelTier.None, false, true, Tuning));
            Assert.Equal(IntelTier.None, IntelGrade.Of(SensorReading.NotDetected, IntelTier.None, false, true, Tuning));
        }

        [Fact]
        public void IsCivilianMission_MilitaryMissionsRunSilent()
        {
            Assert.True(IntelGrade.IsCivilianMission(ActionKind.Trade));
            Assert.True(IntelGrade.IsCivilianMission(ActionKind.Haul));
            Assert.True(IntelGrade.IsCivilianMission(ActionKind.Mine));
            Assert.False(IntelGrade.IsCivilianMission(ActionKind.Raid));
            Assert.False(IntelGrade.IsCivilianMission(ActionKind.Patrol));
            Assert.False(IntelGrade.IsCivilianMission(ActionKind.Guard));
            Assert.False(IntelGrade.IsCivilianMission(ActionKind.Campaign));
        }

        // ---- the scanner contact ------------------------------------------------------------

        [Fact]
        public void Grade_ScannerContactsCarryMarginTransponderAndTier()
        {
            var station = new SpatialNode
            {
                Id = "S", Zone = NodeZone.DeepSpace, Position = new Vec3(90, 0, 0),
                StableStationId = 7, OwnerFactionTag = "FOE"
            };
            var field = new SpatialNode { Id = "F", Zone = NodeZone.DeepSpace, Position = new Vec3(20, 0, 0), OwnerFactionTag = "FOE" };
            var home = new SpatialNode { Id = "H", Zone = NodeZone.DeepSpace, Position = new Vec3(95, 0, 0), OwnerFactionTag = "ME" };
            foreach (SpatialNode n in new[] { field, home })
            {
                n.Deposit = new NodeDeposit();
                n.Deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
            }
            var fleets = new List<ScannerFleetCandidate>
            {
                // 60 km of 100: 4.4 dB, Contact on margin, a trader squawks to Identity.
                new ScannerFleetCandidate { Key = "trader", PositionKm = new Vec3(60, 0, 0), OwnerTag = "FOE", MissionKind = (byte)ActionKind.Trade },
                // Same range, silent: Contact.
                new ScannerFleetCandidate { Key = "raider", PositionKm = new Vec3(0, 60, 0), OwnerTag = "FOE", MissionKind = (byte)ActionKind.Raid },
                // Striking my node at 90 km: identified to me regardless.
                new ScannerFleetCandidate { Key = "strike", PositionKm = new Vec3(0, 0, 90), OwnerTag = "FOE", MissionKind = (byte)ActionKind.Raid, EngagementTargetNodeId = "H" },
            };
            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, Origin, 100, new[] { station, field, home }, fleets, Tuning);
            PlayerScanner.Grade(contacts, new ScannerContactTracker(), tag => tag == "ME", Tuning);

            var byKey = new Dictionary<string, ScannerContact>();
            foreach (ScannerContact c in contacts) byKey[c.Key] = c;

            Assert.True(byKey["node:S"].Transponder);
            Assert.Equal(IntelTier.Identity, byKey["node:S"].Tier);      // 0.9 dB, squawking
            Assert.False(byKey["node:F"].Transponder);
            Assert.Equal(IntelTier.Identity, byKey["node:F"].Tier);      // 14 dB, by margin
            Assert.Equal(IntelTier.Full, byKey["node:H"].Tier);
            Assert.Equal(4.44, byKey["fleet:trader"].MarginDb, 2);
            Assert.Equal(IntelTier.Identity, byKey["fleet:trader"].Tier);
            Assert.False(byKey["fleet:raider"].Transponder);
            Assert.Equal(IntelTier.Contact, byKey["fleet:raider"].Tier);
            Assert.Equal("ME", byKey["fleet:strike"].IdentifiedToTag);
            Assert.Equal(IntelTier.Identity, byKey["fleet:strike"].Tier);
        }

        [Fact]
        public void Grade_ReadsHysteresisFromTheTrackersLastPass()
        {
            var tracker = new ScannerContactTracker();
            var fleet = new ScannerFleetCandidate { Key = "f", OwnerTag = "FOE", MissionKind = (byte)ActionKind.Raid };

            // 30 km of 100 km: 10.5 dB, Identity.
            fleet.PositionKm = new Vec3(30, 0, 0);
            IList<ScannerContact> pass1 = PlayerScanner.Resolve(1, Origin, 100, null, new[] { fleet }, Tuning);
            PlayerScanner.Grade(pass1, tracker, null, Tuning);
            Assert.Equal(IntelTier.Identity, pass1[0].Tier);
            tracker.Reconcile(pass1);

            // 35 km: 9.1 dB. Held, so still Identity; a cold tracker would read Class.
            fleet.PositionKm = new Vec3(35, 0, 0);
            IList<ScannerContact> pass2 = PlayerScanner.Resolve(1, Origin, 100, null, new[] { fleet }, Tuning);
            PlayerScanner.Grade(pass2, tracker, null, Tuning);
            Assert.Equal(IntelTier.Identity, pass2[0].Tier);

            IList<ScannerContact> cold = PlayerScanner.Resolve(1, Origin, 100, null, new[] { fleet }, Tuning);
            PlayerScanner.Grade(cold, new ScannerContactTracker(), null, Tuning);
            Assert.Equal(IntelTier.Class, cold[0].Tier);
        }

        // ---- slice 2: what a fleet contact carries at each tier (FAC-528) ---------------------

        private static ScannerContact FleetContact(IntelTier tier, ScannerContactKind kind = ScannerContactKind.Fleet)
        {
            return new ScannerContact
            {
                Key = "fleet:f", Kind = kind, Label = kind == ScannerContactKind.RaidEngagement ? "Raid: Broadmoor" : "Fleet",
                OwnerTag = "FOE", FleetKind = (byte)ActionKind.Raid, ConvoySize = 6, Moving = true,
                TargetPosKm = new Vec3(500, 0, 0), HasFlightPlan = kind != ScannerContactKind.RaidEngagement,
                IdentifiedToTag = "ME", Tier = tier
            };
        }

        private static ScannerContact Withheld(ScannerContact c, Func<string, bool> offers = null)
        {
            var list = new List<ScannerContact> { c };
            PlayerScanner.Withhold(list, offers);
            return list[0];
        }

        [Fact]
        public void Withhold_AtContactLeavesOnlyPositionAndMotion()
        {
            ScannerContact c = Withheld(FleetContact(IntelTier.Contact));
            Assert.Equal(0, c.FleetKind);
            Assert.Equal(0, c.ConvoySize);
            Assert.Null(c.OwnerTag);
            Assert.Null(c.IdentifiedToTag);
            Assert.False(c.HasFlightPlan);
            Assert.Equal(0.0, c.TargetPosKm.X);
            Assert.True(c.Moving);
        }

        [Fact]
        public void Withhold_ClassShowsTheSilhouetteButNotTheOwner()
        {
            ScannerContact c = Withheld(FleetContact(IntelTier.Class));
            Assert.Equal((byte)ActionKind.Raid, c.FleetKind);
            Assert.Equal(6, c.ConvoySize);
            Assert.Null(c.OwnerTag);
        }

        [Fact]
        public void Withhold_IdentityShowsTheOwnerButNeverTheDestination()
        {
            ScannerContact c = Withheld(FleetContact(IntelTier.Armament), tag => false);
            Assert.Equal("FOE", c.OwnerTag);
            Assert.False(c.HasFlightPlan);
            Assert.Equal(0.0, c.TargetPosKm.X);
        }

        [Fact]
        public void Withhold_TheDestinationIsOfferedAtAnyTier()
        {
            // A friend tells you where it is going even before your sensor can name it; the rule
            // reads the true owner, which is decided before the owner is withheld.
            ScannerContact c = Withheld(FleetContact(IntelTier.Contact), tag => tag == "FOE");
            Assert.True(c.HasFlightPlan);
            Assert.Equal(500.0, c.TargetPosKm.X);
            Assert.Null(c.OwnerTag);
            Assert.True(Withheld(FleetContact(IntelTier.Full)).HasFlightPlan);
        }

        [Fact]
        public void Withhold_AFightBelowClassReadsAsAPlainFleet()
        {
            ScannerContact c = Withheld(FleetContact(IntelTier.Contact, ScannerContactKind.RaidEngagement));
            Assert.Equal(ScannerContactKind.Fleet, c.Kind);
            Assert.Equal("Fleet", c.Label);
            ScannerContact seen = Withheld(FleetContact(IntelTier.Class, ScannerContactKind.RaidEngagement));
            Assert.Equal(ScannerContactKind.RaidEngagement, seen.Kind);
            Assert.Equal("Raid: Broadmoor", seen.Label);
        }

        [Fact]
        public void Withhold_LeavesAnIdentifiedNodeAlone()
        {
            // Below Identity a node loses its name and owner (IntelNodeTests); at Identity it keeps both.
            var node = new ScannerContact { Key = "node:n", Kind = ScannerContactKind.Station, Label = "Station",
                                            DisplayName = "Broadmoor", OwnerTag = "FOE", Tier = IntelTier.Identity };
            ScannerContact c = Withheld(node);
            Assert.Equal("FOE", c.OwnerTag);
            Assert.Equal("Broadmoor", c.DisplayName);
        }

        [Fact]
        public void FleetSensing_Measure_TakesTheBestMarginAndAgreesWithIsDetectedBy()
        {
            var sensors = new List<SensorSource>
            {
                new SensorSource { PositionKm = new Vec3(0, 0, 0), StrengthKm = 100 },     // 80 km: 1.9 dB
                new SensorSource { PositionKm = new Vec3(70, 0, 0), StrengthKm = 50 },     // 10 km: 14 dB
                new SensorSource { PositionKm = new Vec3(5000, 0, 0), StrengthKm = 100 },  // misses
            };
            var target = new Vec3(80, 0, 0);
            SensorReading r = FleetSensing.Measure(sensors, target, 1.0, 0, null);
            Assert.True(r.Detected);
            Assert.Equal(13.98, r.MarginDb, 2);
            Assert.Equal(FleetSensing.IsDetectedBy(sensors, target, 1.0, 0, null), r.Detected);

            var far = new Vec3(3000, 0, 0);
            Assert.False(FleetSensing.Measure(sensors, far, 1.0, 0, null).Detected);
            Assert.False(FleetSensing.IsDetectedBy(sensors, far, 1.0, 0, null));
            Assert.False(FleetSensing.Measure(null, target, 1.0, 0, null).Detected);
        }
    }
}
