using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class PlayerScannerTests
    {
        [Fact]
        public void Resolve_LevelZeroReturnsNoContacts()
        {
            var node = Node("N1", NodeZone.DeepSpace, 10, false, "TEST");
            Assert.Empty(PlayerScanner.Resolve(0, new Vec3(0, 0, 0), 100, new[] { node }, null));
        }

        [Fact]
        public void Resolve_IncludesBoundaryAndExcludesBeyondRange()
        {
            var atBoundary = Node("N1", NodeZone.DeepSpace, 100, false, "TEST", "Iron");
            var beyond = Node("N2", NodeZone.DeepSpace, 100.001, false, "TEST", "Iron");

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100, new[] { atBoundary, beyond }, null, TestSupport.Tuning());

            ScannerContact contact = Assert.Single(contacts);
            Assert.Equal("node:N1", contact.Key);
            Assert.Equal(ScannerContactKind.OreField, contact.Kind);
            Assert.Equal("OreField", contact.Label);
        }

        [Fact]
        public void Resolve_UsesAbstractLabelsAndHidesEmptyNodes()
        {
            var surfaceStation = Node("E1", NodeZone.Surface, 1, true, "TEST");
            var spaceStation = Node("E2", NodeZone.DeepSpace, 2, true, "TEST");
            var oreField = Node("N3", NodeZone.DeepSpace, 3, false, "TEST", "Iron");
            var empty = Node("N4", NodeZone.DeepSpace, 4, false, "TEST");

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100,
                new[] { surfaceStation, spaceStation, oreField, empty }, null,
                TestSupport.Tuning());

            // The empty node is hidden; the ore-bearing one is named by its ground. There is no
            // "generic" contact any more -- that was the label, and the label is gone.
            Assert.Collection(contacts,
                c => { Assert.Equal("Outpost", c.Label); Assert.Equal(ScannerContactKind.Outpost, c.Kind); },
                c => { Assert.Equal("Station", c.Label); Assert.Equal(ScannerContactKind.Station, c.Kind); },
                c => { Assert.Equal("OreField", c.Label); Assert.Equal(ScannerContactKind.OreField, c.Kind); });
        }

        [Fact]
        public void Resolve_MarksUnclaimedNodesFreeAndOwnedNodesNot()
        {
            var neverOwned = Node("N1", NodeZone.DeepSpace, 1, false, null, "Iron");
            var derelict = Node("N2", NodeZone.DeepSpace, 2, false, NeutralFaction.Tag, "Iron");
            var founded = Node("E1", NodeZone.DeepSpace, 3, true, NeutralFaction.Tag);
            var owned = Node("N3", NodeZone.DeepSpace, 4, false, "ACME", "Iron");

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100,
                new[] { neverOwned, derelict, founded, owned }, null, TestSupport.Tuning());

            Assert.Collection(contacts,
                c => { Assert.Equal("OreField(free)", c.Label); Assert.True(c.Unclaimed); },
                c => { Assert.Equal("OreField(free)", c.Label); Assert.True(c.Unclaimed); },
                c => { Assert.Equal("Station(free)", c.Label); Assert.True(c.Unclaimed); },
                c => { Assert.Equal("OreField", c.Label); Assert.False(c.Unclaimed); });
        }

        [Fact]
        public void Resolve_AllFleetKindsExposeOnlyFleetLabel()
        {
            var fleets = new[]
            {
                new ScannerFleetCandidate { Key = "raid-secret", PositionKm = new Vec3(5, 0, 0), EntityId = 42 },
                new ScannerFleetCandidate { Key = "trade-secret", PositionKm = new Vec3(6, 0, 0), EntityId = 0 }
            };

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100, null, fleets);

            Assert.Collection(contacts,
                c => { Assert.Equal("fleet:raid-secret", c.Key); Assert.Equal("Fleet", c.Label); Assert.Equal(42, c.EntityId); },
                c => { Assert.Equal("fleet:trade-secret", c.Key); Assert.Equal("Fleet", c.Label); Assert.Equal(0, c.EntityId); });
        }

        // ---- fleet detection scales with signature (FAC-125) -------------------------------
        // The scanner's fleet branch and the map's fog now answer "can I see that convoy" with the
        // same rule. These pin the three cases that separate the new rule from the flat sphere it
        // replaced; the node branch is deliberately untouched and stays flat.

        [Fact]
        public void Resolve_LoudFleet_IsSeenBeyondTheNominalRange()
        {
            // signature 2.0 against a 100 km scanner reaches 200 km, so 150 km is a contact.
            var fleets = new[]
            {
                new ScannerFleetCandidate { Key = "loud", PositionKm = new Vec3(150, 0, 0), Signature = 2.0 }
            };

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100, null, fleets);

            Assert.Single(contacts);
            Assert.Equal("fleet:loud", contacts[0].Key);
        }

        [Fact]
        public void Resolve_QuietFleet_IsMissedInsideTheNominalRange()
        {
            // signature 0.25 against a 100 km scanner reaches 25 km, so 40 km is silence -- the
            // whole point of the change, and the case the flat sphere got wrong.
            var fleets = new[]
            {
                new ScannerFleetCandidate { Key = "quiet", PositionKm = new Vec3(40, 0, 0), Signature = 0.25 }
            };

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100, null, fleets);

            Assert.Empty(contacts);
        }

        [Fact]
        public void Resolve_UnsuppliedSignature_ReadsAsReferenceNotAsSilence()
        {
            // Signature left at its default. "Unknown, do not gate" -- it must behave as 1.0 and
            // give exactly the flat range this branch had before, not clamp to the 0.25 floor and
            // make every unscanned convoy invisible.
            var atReference = new[]
            {
                new ScannerFleetCandidate { Key = "inside",  PositionKm = new Vec3(99, 0, 0) },
                new ScannerFleetCandidate { Key = "outside", PositionKm = new Vec3(101, 0, 0) }
            };

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100, null, atReference);

            Assert.Single(contacts);
            Assert.Equal("fleet:inside", contacts[0].Key);
        }

        [Fact]
        public void Resolve_FleetContact_CarriesTheMotionSampleTheClientDeadReckonsFrom()
        {
            var fleets = new[]
            {
                new ScannerFleetCandidate
                {
                    Key = "convoy",
                    PositionKm = new Vec3(10, 0, 0),
                    VelocityKmPerMin = new Vec3(0, 6, 0),
                    TargetPosKm = new Vec3(10, 120, 0)
                }
            };

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100, null, fleets);

            ScannerContact contact = Assert.Single(contacts);
            Assert.True(contact.Moving);
            Assert.Equal(6.0, contact.VelocityKmPerMin.Y);
            Assert.Equal(120.0, contact.TargetPosKm.Y);
        }

        [Fact]
        public void Resolve_NodeContact_IsNotMoving()
        {
            // A node is anchored, so it carries no motion at all: dead reckoning must never be
            // applied to one, and the wire must not pay for a vector that is always zero.
            var node = Node("N1", NodeZone.DeepSpace, 10, false, "TEST", "Iron");

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100, new[] { node }, null, TestSupport.Tuning());

            ScannerContact contact = Assert.Single(contacts);
            Assert.False(contact.Moving);
            Assert.Equal(0.0, contact.VelocityKmPerMin.X);
            Assert.Equal(0.0, contact.VelocityKmPerMin.Y);
            Assert.Equal(0.0, contact.VelocityKmPerMin.Z);
        }

        [Fact]
        public void Resolve_NodeBranch_StaysFlatRegardlessOfWhatANodeIs()
        {
            // A station scores 2.0 through Signature.ForNode. If the node branch had been converted
            // with the fleet branch, this one would be detected at 200 km; it must not be.
            SpatialNode station = Node("E9", NodeZone.DeepSpace, 150, true, "OWNER");

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100, new[] { station }, null, TestSupport.Tuning());

            Assert.Empty(contacts);
        }

        [Fact]
        public void Resolve_ScannerAndMapAgree_ForTheSameConvoyAtTheSameDistance()
        {
            // FAC-125's actual complaint, as an assertion: the two features must not disagree.
            var sensors = new List<SensorSource>
            {
                new SensorSource { PositionKm = new Vec3(0, 0, 0), StrengthKm = 100 }
            };

            foreach (double signature in new[] { 0.25, 0.5, 1.0, 2.0, 4.0 })
            {
                foreach (double distanceKm in new[] { 20.0, 60.0, 110.0, 250.0, 410.0 })
                {
                    var target = new Vec3(distanceKm, 0, 0);
                    bool mapSees = FleetSensing.IsDetectedBy(sensors, target, signature);
                    bool scannerSees = PlayerScanner.Resolve(
                        1, new Vec3(0, 0, 0), 100, null,
                        new[] { new ScannerFleetCandidate { Key = "k", PositionKm = target, Signature = signature } }
                    ).Count == 1;

                    Assert.True(mapSees == scannerSees,
                        "scanner and map disagree at signature " + signature + ", " + distanceKm + " km");
                }
            }
        }

        [Fact]
        public void FleetContactKey_DistinguishesSameTickLegacyHauls()
        {
            DateTime departed = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
            var first = new PendingFleetAction
            {
                Kind = ActionKind.Haul,
                ActorTag = "HAUL",
                DepartedUtc = departed,
                OriginNodeId = "SOURCE-A",
                TargetNodeId = "DEST",
                Cargo = new Dictionary<string, long> { ["SteelPlate"] = 10 }
            };
            var second = first;
            second.OriginNodeId = "SOURCE-B";
            second.Cargo = new Dictionary<string, long> { ["Motor"] = 10 };

            Assert.NotEqual(PlayerScanner.FleetContactKey(first), PlayerScanner.FleetContactKey(second));
        }

        [Fact]
        public void OpaqueTokenRegistry_UsesStableRandomTokensWithoutEmbeddingKeys()
        {
            var registry = new ScannerOpaqueTokenRegistry();

            string first = registry.GetOrCreate("node:N123");
            string repeat = registry.GetOrCreate("node:N123");
            string second = registry.GetOrCreate("fleet:F456");

            Assert.Equal(first, repeat);
            Assert.NotEqual(first, second);
            Assert.StartsWith("FSCAN:", first);
            Assert.Equal(38, first.Length);
            Assert.DoesNotContain("N123", first);
            Assert.DoesNotContain("node", first, StringComparison.OrdinalIgnoreCase);
            Assert.True(registry.Remove("node:N123"));
            Assert.NotEqual(first, registry.GetOrCreate("node:N123"));
        }

        [Fact]
        public void OpaqueTokenRegistry_ClearDropsAllMappings()
        {
            var registry = new ScannerOpaqueTokenRegistry();
            string before = registry.GetOrCreate("fleet:F1");

            registry.Clear();

            Assert.Equal(0, registry.Count);
            Assert.NotEqual(before, registry.GetOrCreate("fleet:F1"));
        }

        [Fact]
        public void FleetGps_IsExplicitlyManagedWhileStaticContactsRemainTemporary()
        {
            Assert.False(PlayerScanner.IsTemporaryGps(ScannerContactKind.Fleet));
            Assert.True(PlayerScanner.IsTemporaryGps(ScannerContactKind.Station));
            Assert.True(PlayerScanner.IsTemporaryGps(ScannerContactKind.OreField));
        }

        [Fact]
        public void GpsUpdateMode_ModifiesExistingContactWhenMovementChangesHash()
        {
            Assert.Equal(ScannerGpsUpdateMode.Add,
                PlayerScanner.SelectGpsUpdateMode(false, 0, 10));
            Assert.Equal(ScannerGpsUpdateMode.Refresh,
                PlayerScanner.SelectGpsUpdateMode(true, 10, 10));
            Assert.Equal(ScannerGpsUpdateMode.Modify,
                PlayerScanner.SelectGpsUpdateMode(true, 10, 11));
        }

        [Fact]
        public void ContactTracker_BeepsOnlyForTrulyNewSphereEntries()
        {
            var tracker = new ScannerContactTracker();
            var ore = Contact("node:N1", 10);

            ScannerContactDelta first = tracker.Reconcile(new[] { ore });
            ScannerContactDelta refresh = tracker.Reconcile(new[] { Contact("node:N1", 11) });
            ScannerContactDelta gone = tracker.Reconcile(new ScannerContact[0]);
            ScannerContactDelta reentered = tracker.Reconcile(new[] { ore });

            Assert.Single(first.NewContacts);
            Assert.Empty(refresh.NewContacts);
            Assert.Single(refresh.UpdatedContacts);
            Assert.Single(gone.RemovedKeys);
            Assert.Single(reentered.NewContacts);
        }

        [Fact]
        public void ContactTracker_ClearEndsTheCurrentScannerSphere()
        {
            var tracker = new ScannerContactTracker();
            tracker.Reconcile(new[] { Contact("node:N1", 10) });
            tracker.Clear();

            Assert.Single(tracker.Reconcile(new[] { Contact("node:N1", 10) }).NewContacts);
        }

        [Fact]
        public void ProfileStore_RoundTripsBySteamIdAndRepairsInvalidLevels()
        {
            string dir = Path.Combine(Path.GetTempPath(), "factions-scanner-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new ScannerProfileStore(dir, 1);
                Assert.Equal(1, store.GetLevel("76561198000000001"));
                store.SetLevel("76561198000000001", 3);
                store.SetLevel("76561198000000002", -5);
                store.SaveIfDirty();

                var loaded = new ScannerProfileStore(dir, 1);
                Assert.Equal(3, loaded.GetLevel("76561198000000001"));
                Assert.Equal(0, loaded.GetLevel("76561198000000002"));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Resolve_CarriesTheNodeNameApartFromTheClassLabel()
        {
            // FAC-143: the map names a station and the scanner did not. The name travels in its
            // own field so the class label -- what the unlinked-player GPS fallback shows -- is
            // unchanged, and a fleet has none.
            var named = Node("E1", NodeZone.DeepSpace, 1, true, "TEST");
            named.DisplayName = "Halstead Emporium";
            var idOnly = Node("E2", NodeZone.DeepSpace, 2, true, "TEST");
            idOnly.DisplayName = "E2";
            var nameless = Node("E3", NodeZone.DeepSpace, 3, true, "TEST");
            var free = Node("E4", NodeZone.DeepSpace, 4, true, NeutralFaction.Tag);
            free.DisplayName = "Broadmoor";
            var fleet = new ScannerFleetCandidate { Key = "f1", PositionKm = new Vec3(5, 0, 0), Signature = 1.0 };

            IList<ScannerContact> contacts = PlayerScanner.Resolve(
                1, new Vec3(0, 0, 0), 100,
                new[] { named, idOnly, nameless, free }, new[] { fleet },
                TestSupport.Tuning());

            Assert.Collection(contacts,
                c => { Assert.Equal("Station", c.Label); Assert.Equal("Halstead Emporium", c.DisplayName); },
                // A name that is only the id is no name: the map labels neither of these.
                c => { Assert.Equal("Station", c.Label); Assert.Null(c.DisplayName); },
                c => { Assert.Equal("Station", c.Label); Assert.Null(c.DisplayName); },
                c => { Assert.Equal("Station(free)", c.Label); Assert.Equal("Broadmoor", c.DisplayName); },
                c => { Assert.Equal("Fleet", c.Label); Assert.Null(c.DisplayName); });
        }

        private static SpatialNode Node(
            string id, NodeZone zone, double x, bool station, string ownerFactionTag,
            params string[] ores)
        {
            var n = new SpatialNode
            {
                Id = id,
                Zone = zone,
                Position = new Vec3(x, 0, 0),
                StableStationId = station ? 123 : 0,
                StationPrefabId = station ? "StationPrefab" : null,
                OwnerFactionTag = ownerFactionTag
            };
            // What a contact reads as comes from the ground since FAC-102, so a fixture that wants to
            // be seen as ore-bearing has to say what is under it.
            if (ores != null && ores.Length > 0)
            {
                n.Deposit = new NodeDeposit();
                foreach (string ore in ores)
                    n.Deposit.Entries.Add(new DepositEntry(ore, 1.0 / ores.Length, 100.0 / ores.Length));
            }
            return n;
        }

        private static ScannerContact Contact(string key, double x)
        {
            return new ScannerContact
            {
                Key = key,
                Kind = ScannerContactKind.OreField,
                Label = "OreField",
                PositionKm = new Vec3(x, 0, 0)
            };
        }
    }
}
