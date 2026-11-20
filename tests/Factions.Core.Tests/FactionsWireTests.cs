using System;
using System.Collections.Generic;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The client/server wire contract. These are the only automated checks on it — the Torch host
    /// and FactionsUI compile the same source, so a codec change that breaks round-tripping still
    /// builds cleanly on both sides and only shows up as a client that silently never uplinks.
    /// </summary>
    public class FactionsWireTests
    {
        [Fact]
        public void UplinkRequest_RoundTrips()
        {
            byte[] data = FactionsWire.WriteUplinkRequest("0.1.0.0");

            string clientVersion;
            Assert.True(FactionsWire.TryReadUplinkRequest(data, out clientVersion));
            Assert.Equal("0.1.0.0", clientVersion);
        }

        [Fact]
        public void UplinkGranted_RoundTrips()
        {
            byte[] data = FactionsWire.WriteUplinkGranted("1.2.3.4", WireFeatures.Scanner);

            string serverVersion;
            WireFeatures features;
            Assert.True(FactionsWire.TryReadUplinkGranted(data, out serverVersion, out features));
            Assert.Equal("1.2.3.4", serverVersion);
            Assert.True((features & WireFeatures.Scanner) != 0);
        }

        [Fact]
        public void UplinkGranted_WithNoFeatures_GrantsNothing()
        {
            byte[] data = FactionsWire.WriteUplinkGranted("1.2.3.4", WireFeatures.None);

            string serverVersion;
            WireFeatures features;
            Assert.True(FactionsWire.TryReadUplinkGranted(data, out serverVersion, out features));
            Assert.Equal(WireFeatures.None, features);
            Assert.False((features & WireFeatures.Scanner) != 0);
        }

        [Fact]
        public void ScannerConfig_RoundTrips()
        {
            byte[] data = FactionsWire.WriteScannerConfig(
                2, 25.0, 100.0);

            int level;
            double smallKm, largeKm;
            Assert.True(FactionsWire.TryReadScannerConfig(data, out level, out smallKm, out largeKm));
            Assert.Equal(2, level);
            Assert.Equal(25.0, smallKm);
            Assert.Equal(100.0, largeKm);
        }

        [Fact]
        public void ScannerContacts_RoundTrips_PreservingOrderAndFields()
        {
            var present = new List<WireContact>
            {
                new WireContact { Key = "node:a", Kind = 3, Label = "RareOreField(free)",
                                  XKm = 1.5, YKm = -2.25, ZKm = 3.75, EntityId = 0, Unclaimed = true,
                                  Relation = (byte)WireRelation.Unknown },
                new WireContact { Key = "fleet:f1", Kind = 7, Label = "Fleet",
                                  XKm = 10, YKm = 20, ZKm = 30, EntityId = 123456789L, Unclaimed = false,
                                  Relation = (byte)WireRelation.Enemy },
            };
            var removed = new List<string> { "node:gone", "fleet:old" };

            byte[] data = FactionsWire.WriteScannerContacts(present, removed, false);

            List<WireContact> readPresent;
            List<string> readRemoved;
            bool readReset;
            Assert.True(FactionsWire.TryReadScannerContacts(data, out readPresent, out readRemoved, out readReset));
            Assert.False(readReset);
            Assert.Equal(2, readPresent.Count);
            Assert.Equal("node:a", readPresent[0].Key);
            Assert.Equal(3, readPresent[0].Kind);
            Assert.True(readPresent[0].Unclaimed);
            Assert.Equal(-2.25, readPresent[0].YKm);
            Assert.Equal((byte)WireRelation.Unknown, readPresent[0].Relation);
            Assert.Equal("fleet:f1", readPresent[1].Key);
            Assert.Equal(123456789L, readPresent[1].EntityId);
            Assert.False(readPresent[1].Unclaimed);
            Assert.Equal((byte)WireRelation.Enemy, readPresent[1].Relation);
            Assert.Equal(new[] { "node:gone", "fleet:old" }, readRemoved);
        }

        [Fact]
        public void ScannerContacts_MotionBlock_RoundTripsAndDoesNotDesyncTheContactAfterIt()
        {
            // The motion block is written only for a moving contact, so a reader that got the
            // condition wrong would decode this list's SECOND contact out of the first one's
            // vectors. The static contact therefore comes after the moving one on purpose.
            var present = new List<WireContact>
            {
                new WireContact { Key = "fleet:f1", Kind = 7, Label = "Fleet",
                                  XKm = 10, YKm = 20, ZKm = 30, EntityId = 0, Unclaimed = false,
                                  Relation = (byte)WireRelation.Enemy,
                                  Moving = true,
                                  VelXKmPerMin = 1.5, VelYKmPerMin = -2.5, VelZKmPerMin = 0.25,
                                  TargetXKm = 100, TargetYKm = 200, TargetZKm = 300 },
                new WireContact { Key = "node:a", Kind = 0, Label = "Station",
                                  XKm = -1, YKm = -2, ZKm = -3, EntityId = 42L, Unclaimed = true,
                                  Relation = (byte)WireRelation.Own },
            };

            byte[] data = FactionsWire.WriteScannerContacts(present, new List<string>(), false);

            List<WireContact> readPresent;
            List<string> readRemoved;
            bool readReset;
            Assert.True(FactionsWire.TryReadScannerContacts(data, out readPresent, out readRemoved, out readReset));
            Assert.Equal(2, readPresent.Count);

            Assert.True(readPresent[0].Moving);
            Assert.Equal(1.5, readPresent[0].VelXKmPerMin);
            Assert.Equal(-2.5, readPresent[0].VelYKmPerMin);
            Assert.Equal(0.25, readPresent[0].VelZKmPerMin);
            Assert.Equal(100.0, readPresent[0].TargetXKm);
            Assert.Equal(200.0, readPresent[0].TargetYKm);
            Assert.Equal(300.0, readPresent[0].TargetZKm);

            // The contact behind the variable-length block is the one that proves the framing.
            Assert.Equal("node:a", readPresent[1].Key);
            Assert.False(readPresent[1].Moving);
            Assert.Equal(-1.0, readPresent[1].XKm);
            Assert.Equal(42L, readPresent[1].EntityId);
            Assert.True(readPresent[1].Unclaimed);
            Assert.Equal((byte)WireRelation.Own, readPresent[1].Relation);
            Assert.Equal(0.0, readPresent[1].VelXKmPerMin);
        }

        [Fact]
        public void ScannerContacts_FleetBlock_RoundTripsAndDoesNotDesyncTheContactAfterIt()
        {
            // Same hazard as the motion block, and the same shape of test: the mission and hull
            // count are written only for a fleet kind, so a reader that disagreed about which
            // kinds carry them would decode the NEXT contact out of these two bytes. The node
            // comes last deliberately -- it is the assertion that proves the framing, rather
            // than merely the values.
            var present = new List<WireContact>
            {
                // Fleet in transit: carries the motion block AND the fleet block.
                new WireContact { Key = "fleet:f1", Kind = FactionsWire.ContactKindFleet, Label = "Fleet",
                                  XKm = 10, YKm = 20, ZKm = 30,
                                  Relation = (byte)WireRelation.Enemy,
                                  Moving = true,
                                  VelXKmPerMin = 1.5, VelYKmPerMin = -2.5, VelZKmPerMin = 0.25,
                                  TargetXKm = 100, TargetYKm = 200, TargetZKm = 300,
                                  FleetKind = 8, ConvoySize = 12 },
                // RaidEngagement: parked, so its fleet block follows NO motion block.
                new WireContact { Key = "fleet:f2", Kind = FactionsWire.ContactKindRaidEngagement,
                                  Label = "Raid: Broadmoor",
                                  XKm = 11, YKm = 21, ZKm = 31,
                                  Relation = (byte)WireRelation.Enemy,
                                  Moving = false,
                                  FleetKind = 19, ConvoySize = 1 },
                new WireContact { Key = "node:a", Kind = 0, Label = "Station",
                                  XKm = -1, YKm = -2, ZKm = -3, EntityId = 42L, Unclaimed = true,
                                  Relation = (byte)WireRelation.Own },
            };

            byte[] data = FactionsWire.WriteScannerContacts(present, new List<string>(), false);

            List<WireContact> readPresent;
            List<string> readRemoved;
            bool readReset;
            Assert.True(FactionsWire.TryReadScannerContacts(data, out readPresent, out readRemoved, out readReset));
            Assert.Equal(3, readPresent.Count);

            Assert.Equal(8, readPresent[0].FleetKind);
            Assert.Equal(12, readPresent[0].ConvoySize);
            Assert.Equal(1.5, readPresent[0].VelXKmPerMin);

            Assert.Equal(19, readPresent[1].FleetKind);
            Assert.Equal(1, readPresent[1].ConvoySize);
            Assert.False(readPresent[1].Moving);

            // The contact behind two variable-length blocks is what proves the framing held.
            Assert.Equal("node:a", readPresent[2].Key);
            Assert.Equal(42L, readPresent[2].EntityId);
            Assert.True(readPresent[2].Unclaimed);
            Assert.Equal(-3.0, readPresent[2].ZKm);
        }

        [Fact]
        public void ScannerContacts_NodeName_RoundTripsAndFleetsCarryNone()
        {
            // The name is written only for a non-fleet kind, in the slot the fleet block takes for
            // a fleet. The node after the fleet is what proves the two branches agree on framing.
            var present = new List<WireContact>
            {
                new WireContact { Key = "node:a", Kind = 0, Label = "Station", DisplayName = "Halstead Emporium",
                                  Relation = (byte)WireRelation.Neutral },
                new WireContact { Key = "fleet:f1", Kind = FactionsWire.ContactKindFleet, Label = "Fleet",
                                  DisplayName = "must not travel", FleetKind = 8, ConvoySize = 3 },
                new WireContact { Key = "node:b", Kind = 2, Label = "OreField", EntityId = 7L },
            };

            byte[] data = FactionsWire.WriteScannerContacts(present, new List<string>(), false);

            List<WireContact> readPresent;
            List<string> readRemoved;
            bool readReset;
            Assert.True(FactionsWire.TryReadScannerContacts(data, out readPresent, out readRemoved, out readReset));
            Assert.Equal(3, readPresent.Count);
            Assert.Equal("Halstead Emporium", readPresent[0].DisplayName);
            Assert.Null(readPresent[1].DisplayName);
            Assert.Equal(3, readPresent[1].ConvoySize);
            Assert.Equal("node:b", readPresent[2].Key);
            Assert.Equal("", readPresent[2].DisplayName);
            Assert.Equal(7L, readPresent[2].EntityId);
        }

        [Theory]
        [InlineData("Halstead Emporium", "Halstead Emporium", true)]
        [InlineData("Halstead Emporium (SPRT)", "Halstead Emporium", true)]   // the map's disambiguated GPS
        [InlineData("Halstead Emporium 2", "Halstead Emporium", false)]
        [InlineData("Halstead Emporium (", "Halstead Emporium", false)]
        [InlineData("halstead emporium", "Halstead Emporium", false)]
        [InlineData("Halstead", "Halstead Emporium", false)]
        [InlineData("", "Halstead Emporium", false)]
        [InlineData("Halstead Emporium", "", false)]
        public void ScannerNames_GpsShowsName(string hudText, string nodeName, bool expected)
        {
            Assert.Equal(expected, ScannerNames.GpsShowsName(hudText, nodeName));
        }

        [Fact]
        public void ScannerNames_HudText_IsTheDisplayNameElseTheName()
        {
            // A pre-rename welcome GPS: a node-id name, and the station's name on the HUD.
            Assert.Equal("Halstead Emporium",
                ScannerNames.HudText(WelcomeGps.LegacyNamePrefix + "E1", "Halstead Emporium"));
            Assert.Equal("My marker", ScannerNames.HudText("My marker", null));
            Assert.Equal("My marker", ScannerNames.HudText("My marker", ""));
        }

        [Fact]
        public void ScannerContacts_NonFleetKinds_CarryNoFleetFields()
        {
            // The negative half: a node must not pay two bytes for a field that cannot apply to
            // it, and must read back as zero rather than as whatever followed it in the stream.
            // The values below are set on purpose -- the writer is required to drop them.
            var present = new List<WireContact>
            {
                new WireContact { Key = "node:a", Kind = 2, Label = "OreField",
                                  Relation = (byte)WireRelation.Unknown,
                                  FleetKind = 8, ConvoySize = 12 },
            };

            byte[] data = FactionsWire.WriteScannerContacts(present, new List<string>(), false);

            List<WireContact> readPresent;
            List<string> readRemoved;
            bool readReset;
            Assert.True(FactionsWire.TryReadScannerContacts(data, out readPresent, out readRemoved, out readReset));
            Assert.Single(readPresent);
            Assert.Equal(0, readPresent[0].FleetKind);
            Assert.Equal(0, readPresent[0].ConvoySize);
        }

        [Fact]
        public void ContactCarriesFleetFields_IsTrueForExactlyTheFleetKinds()
        {
            // Writer and reader share this predicate, so it is the single point at which a
            // desync could be introduced. Pinned across every byte value, not just the ones the
            // scanner emits today.
            Assert.True(FactionsWire.ContactCarriesFleetFields(FactionsWire.ContactKindFleet));
            Assert.True(FactionsWire.ContactCarriesFleetFields(FactionsWire.ContactKindRaidEngagement));
            for (int kind = 0; kind <= 255; kind++)
            {
                if (kind == FactionsWire.ContactKindFleet
                    || kind == FactionsWire.ContactKindRaidEngagement) continue;
                Assert.False(FactionsWire.ContactCarriesFleetFields((byte)kind));
            }
        }

        [Fact]
        public void ScannerContacts_EmptyDelta_RoundTrips()
        {
            byte[] data = FactionsWire.WriteScannerContacts(new List<WireContact>(), new List<string>(), false);

            List<WireContact> present;
            List<string> removed;
            bool reset;
            Assert.True(FactionsWire.TryReadScannerContacts(data, out present, out removed, out reset));
            Assert.Empty(present);
            Assert.Empty(removed);
            Assert.False(reset);
        }

        [Fact]
        public void ScannerContacts_ResetFlag_SurvivesAnEmptyDelta()
        {
            // The case that matters: the server has forgotten what the client holds and must say so
            // while having nothing else to report.
            byte[] data = FactionsWire.WriteScannerContacts(new List<WireContact>(), new List<string>(), true);

            List<WireContact> present;
            List<string> removed;
            bool reset;
            Assert.True(FactionsWire.TryReadScannerContacts(data, out present, out removed, out reset));
            Assert.True(reset);
            Assert.Empty(present);
            Assert.Empty(removed);
        }

        [Fact]
        public void UplinkRefused_RoundTrips()
        {
            byte[] data = FactionsWire.WriteUplinkRefused("protocol 99 != 1");

            string reason;
            Assert.True(FactionsWire.TryReadUplinkRefused(data, out reason));
            Assert.Equal("protocol 99 != 1", reason);
        }

        [Fact]
        public void Header_ExposesKindAndVersion_WithoutDecodingPayload()
        {
            byte[] data = FactionsWire.WriteUplinkRequest("0.1.0.0");

            WireMessageKind kind;
            int protocolVersion;
            Assert.True(FactionsWire.TryReadHeader(data, out kind, out protocolVersion));
            Assert.Equal(WireMessageKind.UplinkRequest, kind);
            Assert.Equal(FactionsWire.ProtocolVersion, protocolVersion);
        }

        [Fact]
        public void ReadingAsWrongKind_Fails()
        {
            byte[] granted = FactionsWire.WriteUplinkGranted("1.0", WireFeatures.Scanner);

            string clientVersion;
            Assert.False(FactionsWire.TryReadUplinkRequest(granted, out clientVersion));
        }

        [Fact]
        public void ForeignProtocolVersion_IsRejected_NotMisread()
        {
            // A payload stamped with a different protocol version: byte 0 is the kind, bytes 1-4
            // the version. The reader must refuse it rather than decode the trailing fields.
            byte[] data = FactionsWire.WriteUplinkRequest("0.1.0.0");
            data[2] = (byte)(FactionsWire.ProtocolVersion + 1);   // the version follows the two-byte kind

            string clientVersion;
            Assert.False(FactionsWire.TryReadUplinkRequest(data, out clientVersion));

            // The header still parses, so a receiver can report the mismatch instead of going quiet.
            WireMessageKind kind;
            int protocolVersion;
            Assert.True(FactionsWire.TryReadHeader(data, out kind, out protocolVersion));
            Assert.Equal(WireMessageKind.UplinkRequest, kind);
            Assert.NotEqual(FactionsWire.ProtocolVersion, protocolVersion);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 1, 0, 0 })]
        public void TruncatedOrMissingData_IsRejected(byte[] data)
        {
            WireMessageKind kind;
            int protocolVersion;
            Assert.False(FactionsWire.TryReadHeader(data, out kind, out protocolVersion));

            string clientVersion;
            Assert.False(FactionsWire.TryReadUplinkRequest(data ?? new byte[0], out clientVersion));
        }

        // ---- Map ----------------------------------------------------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MapRequest_RoundTrips(bool subscribing)
        {
            byte[] data = FactionsWire.WriteMapRequest(subscribing);

            bool stream;
            Assert.True(FactionsWire.TryReadMapRequest(data, out stream));
            Assert.Equal(subscribing, stream);
        }

        [Fact]
        public void MapWorld_RoundTripsEveryField()
        {
            var node = new WireMapNode
            {
                Id = "E042",
                DisplayName = "Kharak Refinery",
                DominantOre = "Platinum",
                Surveyed = true,
                Zone = 1,
                OwnerTag = "MERC",
                Relation = (byte)WireRelation.Enemy,
                HasStation = true,
                HasInfrastructure = true,
                XKm = -1234.5,
                YKm = 6789.25,
                ZKm = -0.125,
                RadiusKm = 40,
                HoldStrength = 0.625,
                // Bits 9 and 10 (Farm, FoodProcessor) ride above the old byte, so a reader that
                // still took one byte would lose them and misread every field after.
                CapabilityMask = 0x062A,
                Knowledge = (byte)WireKnowledge.Detected,
            };
            var faction = new WireMapFaction { Tag = "MERC", Relation = (byte)WireRelation.Enemy, NodeCount = 7 };

            byte[] data = FactionsWire.WriteMapWorld(
                new List<WireMapNode> { node }, new List<WireMapFaction> { faction });

            List<WireMapNode> nodes;
            List<WireMapFaction> factions;
            Assert.True(FactionsWire.TryReadMapWorld(data, out nodes, out factions));

            WireMapNode got = Assert.Single(nodes);
            Assert.Equal(node.Id, got.Id);
            Assert.Equal(node.DisplayName, got.DisplayName);
            Assert.Equal(node.DominantOre, got.DominantOre);
            Assert.Equal(node.Surveyed, got.Surveyed);
            Assert.Equal(node.Zone, got.Zone);
            Assert.Equal(node.OwnerTag, got.OwnerTag);
            Assert.Equal(node.Relation, got.Relation);
            Assert.True(got.HasStation);
            Assert.True(got.HasInfrastructure);
            Assert.Equal(node.XKm, got.XKm);
            Assert.Equal(node.YKm, got.YKm);
            Assert.Equal(node.ZKm, got.ZKm);
            Assert.Equal(node.RadiusKm, got.RadiusKm);
            Assert.Equal(node.HoldStrength, got.HoldStrength);
            Assert.Equal(node.CapabilityMask, got.CapabilityMask);
            Assert.Equal(node.Knowledge, got.Knowledge);

            WireMapFaction gotFaction = Assert.Single(factions);
            Assert.Equal("MERC", gotFaction.Tag);
            Assert.Equal((byte)WireRelation.Enemy, gotFaction.Relation);
            Assert.Equal(7, gotFaction.NodeCount);
        }

        [Fact]
        public void MapWorld_WithNothingToSend_RoundTripsEmpty()
        {
            // An empty world is a legitimate message, not an error: it is what a client is told
            // before the graph is populated, and it must clear whatever the client still holds.
            byte[] data = FactionsWire.WriteMapWorld(null, null);

            List<WireMapNode> nodes;
            List<WireMapFaction> factions;
            Assert.True(FactionsWire.TryReadMapWorld(data, out nodes, out factions));
            Assert.Empty(nodes);
            Assert.Empty(factions);
        }

        [Fact]
        public void MapFleets_RoundTripsEveryField_IncludingStops()
        {
            var fleet = new WireMapFleet
            {
                Key = "fleet:abc123",
                Kind = 21,
                OwnerTag = "AWL",
                Relation = (byte)WireRelation.Own,
                XKm = 100.5, YKm = -200.25, ZKm = 300,
                VelXKmPerMin = 1.5, VelYKmPerMin = -2.25, VelZKmPerMin = 0,
                TargetXKm = 900, TargetYKm = -800, TargetZKm = 700,
                EtaUtcTicks = 638000000000000000L,
                ConvoySize = 4,
                EntityId = 123456789L,
                RoutedStops = new[]
                {
                    new WireMapStop { XKm = 500, YKm = 400, ZKm = 300, Kind = 1 },
                    new WireMapStop { XKm = -1, YKm = -2, ZKm = -3, Kind = 2 },
                },
            };

            byte[] data = FactionsWire.WriteMapFleets(new List<WireMapFleet> { fleet }, null);

            List<WireMapFleet> fleets;
            List<WireMapAlarm> alarms;
            Assert.True(FactionsWire.TryReadMapFleets(data, out fleets, out alarms));

            WireMapFleet got = Assert.Single(fleets);
            Assert.Equal(fleet.Key, got.Key);
            Assert.Equal(fleet.Kind, got.Kind);
            Assert.Equal(fleet.OwnerTag, got.OwnerTag);
            Assert.Equal(fleet.Relation, got.Relation);
            Assert.Equal(fleet.XKm, got.XKm);
            Assert.Equal(fleet.YKm, got.YKm);
            Assert.Equal(fleet.ZKm, got.ZKm);
            Assert.Equal(fleet.VelXKmPerMin, got.VelXKmPerMin);
            Assert.Equal(fleet.VelYKmPerMin, got.VelYKmPerMin);
            Assert.Equal(fleet.VelZKmPerMin, got.VelZKmPerMin);
            Assert.Equal(fleet.TargetXKm, got.TargetXKm);
            Assert.Equal(fleet.TargetYKm, got.TargetYKm);
            Assert.Equal(fleet.TargetZKm, got.TargetZKm);
            Assert.Equal(fleet.EtaUtcTicks, got.EtaUtcTicks);
            Assert.Equal(fleet.ConvoySize, got.ConvoySize);
            Assert.Equal(fleet.EntityId, got.EntityId);
            Assert.Equal(2, got.RoutedStops.Length);
            Assert.Equal(500, got.RoutedStops[0].XKm);
            Assert.Equal((byte)1, got.RoutedStops[0].Kind);
            Assert.Equal(-3, got.RoutedStops[1].ZKm);
            Assert.Equal((byte)2, got.RoutedStops[1].Kind);
        }

        [Fact]
        public void MapFleets_WithNoStops_RoundTripsEmptyItinerary()
        {
            // Guard and Ferry are one-way and carry no remaining stops; a null array must survive
            // as an empty one rather than tripping the reader.
            var fleet = new WireMapFleet { Key = "fleet:oneway", RoutedStops = null };

            byte[] data = FactionsWire.WriteMapFleets(new List<WireMapFleet> { fleet }, null);

            List<WireMapFleet> fleets;
            List<WireMapAlarm> alarms;
            Assert.True(FactionsWire.TryReadMapFleets(data, out fleets, out alarms));
            Assert.Empty(Assert.Single(fleets).RoutedStops);
        }

        [Fact]
        public void MapFleets_WithNothingInTransit_RoundTripsEmpty()
        {
            byte[] data = FactionsWire.WriteMapFleets(null, null);

            List<WireMapFleet> fleets;
            List<WireMapAlarm> alarms;
            Assert.True(FactionsWire.TryReadMapFleets(data, out fleets, out alarms));
            Assert.Empty(fleets);
        }

        [Fact]
        public void MapFleets_CarriesTheAlarmOnANodeWithNoFleetOnIt()
        {
            // The case the whole row exists for: a player besieging a station produces no fleet, so
            // an alarm that only rode WireMapFleet could never describe it.
            var alarm = new WireMapAlarm
            {
                NodeId = "N130",
                State = (byte)WireZoneState.Besieged,
                ZoneIntegrity = 0.42,
                RaidProgress = 0.0,
                UntilUtcTicks = 638000000000000000L,
            };

            byte[] data = FactionsWire.WriteMapFleets(null, new List<WireMapAlarm> { alarm });

            List<WireMapFleet> fleets;
            List<WireMapAlarm> alarms;
            Assert.True(FactionsWire.TryReadMapFleets(data, out fleets, out alarms));
            Assert.Empty(fleets);

            WireMapAlarm got = Assert.Single(alarms);
            Assert.Equal("N130", got.NodeId);
            Assert.Equal((byte)WireZoneState.Besieged, got.State);
            Assert.Equal(0.42, got.ZoneIntegrity);
            Assert.Equal(638000000000000000L, got.UntilUtcTicks);
        }

        [Fact]
        public void MapFleets_WithNothingUnderAttack_RoundTripsNoAlarms()
        {
            byte[] data = FactionsWire.WriteMapFleets(
                new List<WireMapFleet> { new WireMapFleet { Key = "fleet:1" } }, null);

            List<WireMapFleet> fleets;
            List<WireMapAlarm> alarms;
            Assert.True(FactionsWire.TryReadMapFleets(data, out fleets, out alarms));
            Assert.Single(fleets);
            Assert.Empty(alarms);
        }

        [Fact]
        public void MapFleets_WithAbsurdAlarmCount_IsRejectedBeforeAllocating()
        {
            byte[] data = FactionsWire.WriteMapFleets(null, null);
            // The alarm count is the last four bytes of a fleetless message.
            data[data.Length - 4] = 0xFF; data[data.Length - 3] = 0xFF;
            data[data.Length - 2] = 0xFF; data[data.Length - 1] = 0x7F;

            List<WireMapFleet> fleets;
            List<WireMapAlarm> alarms;
            Assert.False(FactionsWire.TryReadMapFleets(data, out fleets, out alarms));
        }

        [Fact]
        public void NodeDetail_CarriesZoneStateForANodeTheViewerDoesNotOwn()
        {
            // Zone state sits ABOVE the ownership seam on purpose: a safe zone is visible from
            // outside it. An unowned snapshot stops after Owned, so this is the one field group
            // past HoldStrength that has to survive that early return.
            var detail = new WireNodeDetail
            {
                Exists = true,
                Id = "N130",
                Owned = false,
                ZoneState = (byte)WireZoneState.Breached,
                ZoneIntegrity = 0.0,
                ZoneRaidProgress = 0.63,
                ZoneUntilUtcTicks = 0,
            };

            byte[] data = FactionsWire.WriteNodeDetailSnapshot(detail);

            WireNodeDetail got;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(data, out got));
            Assert.False(got.Owned);
            Assert.Equal((byte)WireZoneState.Breached, got.ZoneState);
            Assert.Equal(0.63, got.ZoneRaidProgress);
        }

        [Fact]
        public void MapWorld_WithAbsurdNodeCount_IsRejectedBeforeAllocating()
        {
            // Corrupt the node count to int.MaxValue. The reader must refuse on the bound rather
            // than try to pre-allocate the list.
            byte[] data = FactionsWire.WriteMapWorld(new List<WireMapNode>(), new List<WireMapFaction>());
            data[6] = 0xFF; data[7] = 0xFF; data[8] = 0xFF; data[9] = 0x7F;

            List<WireMapNode> nodes;
            List<WireMapFaction> factions;
            Assert.False(FactionsWire.TryReadMapWorld(data, out nodes, out factions));
        }

        [Fact]
        public void MapFleets_WithAbsurdFleetCount_IsRejectedBeforeAllocating()
        {
            byte[] data = FactionsWire.WriteMapFleets(new List<WireMapFleet>(), null);
            data[6] = 0xFF; data[7] = 0xFF; data[8] = 0xFF; data[9] = 0x7F;

            List<WireMapFleet> fleets;
            List<WireMapAlarm> alarms;
            Assert.False(FactionsWire.TryReadMapFleets(data, out fleets, out alarms));
        }

        [Fact]
        public void MapMessages_FromAForeignProtocol_AreRejected()
        {
            byte[] data = FactionsWire.WriteMapWorld(new List<WireMapNode>(), new List<WireMapFaction>());
            data[2] = (byte)(FactionsWire.ProtocolVersion + 1);   // the version follows the two-byte kind

            List<WireMapNode> nodes;
            List<WireMapFaction> factions;
            Assert.False(FactionsWire.TryReadMapWorld(data, out nodes, out factions));
        }

        [Fact]
        public void MapMessages_ReadAsTheWrongKind_Fail()
        {
            byte[] world = FactionsWire.WriteMapWorld(new List<WireMapNode>(), new List<WireMapFaction>());

            List<WireMapFleet> fleets;
            List<WireMapAlarm> alarms;
            Assert.False(FactionsWire.TryReadMapFleets(world, out fleets, out alarms));

            bool stream;
            Assert.False(FactionsWire.TryReadMapRequest(world, out stream));
        }

        [Fact]
        public void AssetsRequest_RoundTrips()
        {
            byte[] subscribe = FactionsWire.WriteAssetsRequest(true);
            byte[] unsubscribe = FactionsWire.WriteAssetsRequest(false);

            bool stream;
            Assert.True(FactionsWire.TryReadAssetsRequest(subscribe, out stream));
            Assert.True(stream);
            Assert.True(FactionsWire.TryReadAssetsRequest(unsubscribe, out stream));
            Assert.False(stream);
        }

        [Fact]
        public void AssetsSnapshot_RoundTrips()
        {
            var nodes = new List<WireAssetNode>
            {
                new WireAssetNode
                {
                    Id = "node-1",
                    DisplayName = "Ceres Yard",
                    DominantOre = "Platinum",
                    Surveyed = true,
                    Zone = 1,
                    HasStation = true,
                    XKm = 1.5, YKm = -2.5, ZKm = 3.5,
                    HoldStrength = 0.75,
                    ResourceValue = 0.4,
                    Richness = 0.62,
                    FoundCost = 100000,
                    ShipsReady = 3,
                    BuildsQueued = 2,
                    ShipyardPaused = true,
                    Capabilities = new[]
                    {
                        new WireAssetCapability { Cap = "Shipyard", Level = 1 },
                        new WireAssetCapability { Cap = "Refinery", Level = 2 },
                    },
                },
            };
            var fleets = new List<WireAssetFleet>
            {
                new WireAssetFleet
                {
                    Key = "fleet-1",
                    Kind = 21,
                    XKm = 10, YKm = 20, ZKm = 30,
                    OriginName = "Ceres Yard",
                    DestinationName = "Vesta Depot",
                    TargetXKm = 40, TargetYKm = 50, TargetZKm = 60,
                    EtaUtcTicks = 638000000000000000L,
                    ConvoySize = 4,
                    EntityId = 12345L,
                },
            };
            var grids = new List<WireAssetGrid>
            {
                new WireAssetGrid
                {
                    EntityId = 999L,
                    Name = "Hauler Mk II",
                    IsStatic = false,
                    GridSize = 1,
                    BlockCount = 420,
                    XKm = -1, YKm = -2, ZKm = -3,
                    OwnedPersonally = false,
                    OwnerName = "Someone Else",
                },
            };

            byte[] data = FactionsWire.WriteAssetsSnapshot("PLYR", "Player Faction", nodes, fleets, grids);

            string tag, name;
            List<WireAssetNode> readNodes;
            List<WireAssetFleet> readFleets;
            List<WireAssetGrid> readGrids;
            Assert.True(FactionsWire.TryReadAssetsSnapshot(
                data, out tag, out name, out readNodes, out readFleets, out readGrids));

            Assert.Equal("PLYR", tag);
            Assert.Equal("Player Faction", name);

            Assert.Single(readNodes);
            WireAssetNode node = readNodes[0];
            Assert.Equal("node-1", node.Id);
            Assert.Equal("Ceres Yard", node.DisplayName);
            Assert.Equal("Platinum", node.DominantOre);
            Assert.Equal(1, node.Zone);
            Assert.True(node.HasStation);
            Assert.Equal(3.5, node.ZKm);
            Assert.Equal(0.75, node.HoldStrength);
            Assert.Equal(0.4, node.ResourceValue);
            // Distinct from ResourceValue on purpose: equal fixture values would round-trip even if
            // the writer sent one field twice. See docs/issues/FAC-219.
            Assert.Equal(0.62, node.Richness);
            Assert.Equal(100000L, node.FoundCost);
            Assert.Equal(3, node.ShipsReady);
            Assert.Equal(2, node.BuildsQueued);
            Assert.True(node.ShipyardPaused);
            Assert.Equal(2, node.Capabilities.Length);
            Assert.Equal("Shipyard", node.Capabilities[0].Cap);
            Assert.Equal(2, node.Capabilities[1].Level);

            Assert.Single(readFleets);
            WireAssetFleet fleet = readFleets[0];
            Assert.Equal("fleet-1", fleet.Key);
            Assert.Equal(21, fleet.Kind);
            Assert.Equal("Ceres Yard", fleet.OriginName);
            Assert.Equal("Vesta Depot", fleet.DestinationName);
            Assert.Equal(60, fleet.TargetZKm);
            Assert.Equal(638000000000000000L, fleet.EtaUtcTicks);
            Assert.Equal(4, fleet.ConvoySize);
            Assert.Equal(12345L, fleet.EntityId);

            Assert.Single(readGrids);
            WireAssetGrid grid = readGrids[0];
            Assert.Equal(999L, grid.EntityId);
            Assert.Equal("Hauler Mk II", grid.Name);
            Assert.False(grid.IsStatic);
            Assert.Equal(1, grid.GridSize);
            Assert.Equal(420, grid.BlockCount);
            Assert.False(grid.OwnedPersonally);
            Assert.Equal("Someone Else", grid.OwnerName);
        }

        [Fact]
        public void AssetsSnapshot_WithNoFactionAndNoHoldings_RoundTrips()
        {
            // A player in no faction is a real state: they own no territory and no convoys, but
            // may still own grids, and the tab has to be able to say so.
            byte[] data = FactionsWire.WriteAssetsSnapshot(
                "", "", new List<WireAssetNode>(), new List<WireAssetFleet>(), new List<WireAssetGrid>());

            string tag, name;
            List<WireAssetNode> nodes;
            List<WireAssetFleet> fleets;
            List<WireAssetGrid> grids;
            Assert.True(FactionsWire.TryReadAssetsSnapshot(
                data, out tag, out name, out nodes, out fleets, out grids));
            Assert.Equal("", tag);
            Assert.Empty(nodes);
            Assert.Empty(fleets);
            Assert.Empty(grids);
        }

        [Fact]
        public void AssetsSnapshot_WithNoCapabilities_ReadsAsEmptyNotNull()
        {
            // The writer sends a zero count for a null array; the reader must hand back something
            // the page can iterate without a null check at every use.
            var nodes = new List<WireAssetNode>
            {
                new WireAssetNode { Id = "bare", DisplayName = "Bare Claim", Capabilities = null },
            };

            byte[] data = FactionsWire.WriteAssetsSnapshot("T", "T", nodes, null, null);

            string tag, name;
            List<WireAssetNode> readNodes;
            List<WireAssetFleet> readFleets;
            List<WireAssetGrid> readGrids;
            Assert.True(FactionsWire.TryReadAssetsSnapshot(
                data, out tag, out name, out readNodes, out readFleets, out readGrids));
            Assert.Single(readNodes);
            Assert.NotNull(readNodes[0].Capabilities);
            Assert.Empty(readNodes[0].Capabilities);
        }

        [Fact]
        public void AssetsMessages_FromAForeignProtocol_AreRejected()
        {
            byte[] data = FactionsWire.WriteAssetsSnapshot("T", "T", null, null, null);
            data[2] = (byte)(FactionsWire.ProtocolVersion + 1);   // the version follows the two-byte kind

            string tag, name;
            List<WireAssetNode> nodes;
            List<WireAssetFleet> fleets;
            List<WireAssetGrid> grids;
            Assert.False(FactionsWire.TryReadAssetsSnapshot(
                data, out tag, out name, out nodes, out fleets, out grids));
        }

        [Fact]
        public void AssetsMessages_ReadAsTheWrongKind_Fail()
        {
            byte[] snapshot = FactionsWire.WriteAssetsSnapshot("T", "T", null, null, null);

            bool stream;
            Assert.False(FactionsWire.TryReadAssetsRequest(snapshot, out stream));

            List<WireMapNode> nodes;
            List<WireMapFaction> factions;
            Assert.False(FactionsWire.TryReadMapWorld(snapshot, out nodes, out factions));
        }

        [Fact]
        public void AssetsFeature_IsADistinctBitFromScannerAndMap()
        {
            // Each feature is granted on its own bit, so a server can offer the map without the
            // asset register and the client gates each independently.
            byte[] data = FactionsWire.WriteUplinkGranted(
                "1.0", WireFeatures.Scanner | WireFeatures.Map | WireFeatures.Assets);

            string serverVersion;
            WireFeatures features;
            Assert.True(FactionsWire.TryReadUplinkGranted(data, out serverVersion, out features));
            Assert.True((features & WireFeatures.Assets) != 0);
            Assert.NotEqual(WireFeatures.Map, WireFeatures.Assets);
            Assert.NotEqual(WireFeatures.Scanner, WireFeatures.Assets);

            byte[] withoutAssets = FactionsWire.WriteUplinkGranted("1.0", WireFeatures.Scanner | WireFeatures.Map);
            Assert.True(FactionsWire.TryReadUplinkGranted(withoutAssets, out serverVersion, out features));
            Assert.False((features & WireFeatures.Assets) != 0);
        }

        [Fact]
        public void MapFeature_IsADistinctBitFromScanner()
        {
            // The two features must be independently grantable -- a server can offer the scanner
            // without the map, and the client gates each on its own bit.
            byte[] data = FactionsWire.WriteUplinkGranted("1.0", WireFeatures.Scanner | WireFeatures.Map);

            string serverVersion;
            WireFeatures features;
            Assert.True(FactionsWire.TryReadUplinkGranted(data, out serverVersion, out features));
            Assert.True((features & WireFeatures.Scanner) != 0);
            Assert.True((features & WireFeatures.Map) != 0);
            Assert.NotEqual(WireFeatures.Scanner, WireFeatures.Map);
        }

        // ---- Node detail ------------------------------------------------------------------

        private static WireNodeDetail OwnedDetail()
        {
            return new WireNodeDetail
            {
                Exists = true,
                Id = "N8466",
                DisplayName = "Kepler Yard",
                DominantOre = "Platinum",
                Surveyed = true,
                Zone = 1,
                OwnerTag = "EXP",
                Relation = (byte)WireRelation.Own,
                HasStation = true,
                XKm = -1234.5,
                YKm = 6789.0,
                ZKm = 0.25,
                HoldStrength = 0.82,
                Owned = true,
                ResourceValue = 0.41,
                Richness = 0.73,
                FoundCost = 100000,
                FoundHave = 137500,
                FoundBlock = (byte)WireFoundBlock.None,
                ShipyardPaused = true,
                Capabilities = new[]
                {
                    new WireAssetCapability { Cap = "Shipyard", Level = 2 },
                    new WireAssetCapability { Cap = "Refinery", Level = 1 },
                },
                Stock = new[]
                {
                    new WireNodeStockItem { Key = "SteelPlate", Quantity = 12000, Reserved = 0, ForSale = 9000, Target = 3000 },
                    new WireNodeStockItem
                    {
                        Key = "Superconductor", Quantity = 413, Reserved = 90, ForSale = 57, Target = 520,
                        KeptFor = (byte)WireStockKeptFor.Owed,
                    },
                },
                Builds = new[]
                {
                    new WireNodeBuild
                    {
                        Id = "Raid:001", Role = "Raid", PrefabId = "AWL_Raider",
                        BomTotal = 8960, Collected = 2131, Priority = 3,
                        ExpiresUtcTicks = 638_000_000_000_000_000L, StalledSeconds = 21_720.5,
                        CreatedUtcTicks = 637_900_000_000_000_000L, Working = true, WaitingBehind = 0,
                        Bom = new[]
                        {
                            // Every number distinct from every other, so a writer sending one field
                            // twice cannot round-trip.
                            new WireBuildComponent
                            {
                                Key = "Comp:Superconductor", Need = 3100, Have = 413,
                                AtNode = 7, Elsewhere = 21, EnRoute = 55, Unserved = true,
                            },
                            new WireBuildComponent
                            {
                                Key = "Comp:SteelPlate", Need = 5860, Have = 1718,
                                AtNode = 12000, Elsewhere = 900, EnRoute = 0, Unserved = false,
                            },
                        },
                        ReservedInputs = new[]
                        {
                            new WireBuildReservedInput { Key = "Ingot:Iron", Quantity = 4400 },
                        },
                    },
                    new WireNodeBuild
                    {
                        Id = "Trade:002", Role = "Trade", PrefabId = "AWL_Hauler",
                        BomTotal = 4550, Collected = 0, Priority = 0,
                        ExpiresUtcTicks = 638_100_000_000_000_000L, StalledSeconds = 0,
                        CreatedUtcTicks = 637_950_000_000_000_000L, Working = false, WaitingBehind = 1,
                    },
                },
                Ships = new[]
                {
                    new WireNodeShip { PrefabId = "AWL_Hauler", Ready = 4, ListedForSale = 1 },
                },
                Develop = new[]
                {
                    new WireNodeCapabilityOption
                    {
                        Cap = "Refinery", Level = 1, MaxLevel = 15, Cost = 5000,
                        Block = (byte)WireDevelopBlock.None, BlockKey = "",
                        Reserved = true, Priority = 4,
                    },
                    new WireNodeCapabilityOption
                    {
                        Cap = "PrototechForge", Level = 0, MaxLevel = 8, Cost = 0,
                        Block = (byte)WireDevelopBlock.PrerequisiteMissing, BlockKey = "ManufactoryII",
                        Reserved = false, Priority = 0,
                    },
                    new WireNodeCapabilityOption
                    {
                        Cap = "Shipyard", Level = 0, MaxLevel = 1, Cost = 5000,
                        Block = (byte)WireDevelopBlock.InsufficientStock, BlockKey = "Comp:SteelPlate",
                        BlockHave = 120, BlockNeed = 400, Reserved = false, Priority = 0,
                    },
                },
                HoldOptIn = true,
                HoldRank = 7,
                HoldCost = 50000,
                HoldBudgetIncomeFraction = 0.3,
                HoldBudgetTreasuryFraction = 0.001,
                HoldBudgetFloor = 50000,
                HoldBudgetOverrideMask = 5,     // income + floor set, treasury on the world default
                TradeBuyOptIn = true,
                TradeSellOptIn = false,         // deliberately unequal: one field cannot serve both
                TradePartners = 6,
                TradeHasHull = true,
                TradeUnserved = 90,
                TradeInFlight = 2,
                TradeMaxFleets = 30,
                TradeBudgetIncomeFraction = 0.3,
                TradeBudgetTreasuryFraction = 0.25,
                TradeBudgetFloor = 60,
                TradeBudgetOverrideMask = 2,    // treasury set, the other two on the world default
            };
        }

        // The node-detail codec is hand-rolled BinaryWriter/BinaryReader, so a field written and not
        // read (or read in the wrong order) silently corrupts every field after it rather than
        // failing. Distinct values per field, so a transposition cannot pass.
        [Fact]
        public void NodeDetail_HoldBlock_RoundTrips()
        {
            byte[] data = FactionsWire.WriteNodeDetailSnapshot(OwnedDetail());
            WireNodeDetail read;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(data, out read));

            Assert.True(read.HoldOptIn);
            Assert.Equal(7, read.HoldRank);
            Assert.Equal(50000, read.HoldCost);
            Assert.Equal(0.3, read.HoldBudgetIncomeFraction);
            Assert.Equal(0.001, read.HoldBudgetTreasuryFraction);
            Assert.Equal(50000, read.HoldBudgetFloor);
            Assert.Equal(5, read.HoldBudgetOverrideMask);

            // The block is appended, so the fields before it must be untouched — this is what
            // catches a writer and reader that disagree about length somewhere earlier.
            Assert.Equal("N8466", read.Id);
            Assert.Equal(0.82, read.HoldStrength);
            Assert.Equal(3, read.Develop.Length);
            Assert.Equal("Shipyard", read.Develop[2].Cap);
        }

        // The ground block sits between the worker fields and the capability list, so this asserts
        // both that its fields survive and that the capabilities after it still read.
        [Fact]
        public void NodeDetail_GroundBlock_RoundTrips()
        {
            WireNodeDetail sent = OwnedDetail();
            sent.SurveyConfidence = 0.34;
            sent.ResourceValueTarget = 0.63;
            sent.RichnessTarget = 0.72;
            sent.ResolvedShareFloor = 0.041;
            sent.Ores = new[]
            {
                new WireNodeOre { SubTypeId = "Iron", Share = 0.61, PriceRatio = 1.0, WorthShare = 0.28, Resolved = true },
                new WireNodeOre { SubTypeId = "Platinum", Share = 0.03, PriceRatio = 3.5, WorthShare = 0.38, Resolved = false },
            };

            WireNodeDetail read;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out read));

            Assert.Equal(0.34, read.SurveyConfidence);
            Assert.Equal(0.63, read.ResourceValueTarget);
            Assert.Equal(0.72, read.RichnessTarget);
            Assert.Equal(0.041, read.ResolvedShareFloor);
            Assert.Equal(2, read.Ores.Length);
            Assert.Equal("Platinum", read.Ores[1].SubTypeId);
            Assert.Equal(0.03, read.Ores[1].Share);
            Assert.Equal(3.5, read.Ores[1].PriceRatio);
            Assert.Equal(0.38, read.Ores[1].WorthShare);
            Assert.True(read.Ores[0].Resolved);
            Assert.False(read.Ores[1].Resolved);

            Assert.Equal(2, read.Capabilities.Length);
            Assert.Equal("Refinery", read.Capabilities[1].Cap);
        }

        /// <summary>The ore mix is below the ownership seam: an unowned snapshot never carries it, and
        /// reads back as an empty list rather than null.</summary>
        [Fact]
        public void NodeDetail_GroundBlock_IsNotSentForANodeTheViewerDoesNotOwn()
        {
            WireNodeDetail sent = OwnedDetail();
            sent.Owned = false;
            sent.Ores = new[] { new WireNodeOre { SubTypeId = "Iron", Share = 1.0 } };

            WireNodeDetail read;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out read));

            Assert.NotNull(read.Ores);
            Assert.Empty(read.Ores);
            Assert.Equal(0.0, read.SurveyConfidence);
        }

        // FAC-298. The trade block is appended past the hold block, so this asserts the same two
        // things that one does: the new fields survive, and nothing before them moved.
        [Fact]
        public void NodeDetail_TradeBlock_RoundTrips()
        {
            byte[] data = FactionsWire.WriteNodeDetailSnapshot(OwnedDetail());
            WireNodeDetail read;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(data, out read));

            // Unequal on purpose: two switches written from one field would pass an all-true check.
            Assert.True(read.TradeBuyOptIn);
            Assert.False(read.TradeSellOptIn);
            Assert.Equal(6, read.TradePartners);
            Assert.True(read.TradeHasHull);
            Assert.Equal(90, read.TradeUnserved);
            Assert.Equal(2, read.TradeInFlight);
            Assert.Equal(30, read.TradeMaxFleets);
            Assert.Equal(0.3, read.TradeBudgetIncomeFraction);
            Assert.Equal(0.25, read.TradeBudgetTreasuryFraction);
            Assert.Equal(60, read.TradeBudgetFloor);
            Assert.Equal(2, read.TradeBudgetOverrideMask);

            // The hold block sits immediately before it and must be undisturbed.
            Assert.True(read.HoldOptIn);
            Assert.Equal(7, read.HoldRank);
            Assert.Equal(5, read.HoldBudgetOverrideMask);
            Assert.Equal("N8466", read.Id);
            Assert.Equal(3, read.Develop.Length);
        }

        // The production block is appended past the repair block: the spans survive in order, the
        // window survives, and the repair count written immediately before it is undisturbed.
        [Fact]
        public void NodeDetail_ProductionBlock_RoundTrips()
        {
            WireNodeDetail sent = OwnedDetail();
            sent.RepairHulls = 4;
            sent.ProductionWindowSeconds = 300;
            sent.Production = new[]
            {
                new WireNodeProductionSpan
                {
                    RecipeId = "SteelPlate", Key = "Comp:SteelPlate", IsOutput = true, Quantity = 120,
                    StartUtcTicks = 637_900_000_000_000_000L, EndUtcTicks = 637_900_000_100_000_000L,
                },
                new WireNodeProductionSpan
                {
                    RecipeId = "SteelPlate", Key = "Ingot:Iron", IsOutput = false, Quantity = 2520,
                    StartUtcTicks = 637_900_000_000_000_001L, EndUtcTicks = 637_900_000_100_000_002L,
                },
            };

            WireNodeDetail read;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out read));

            Assert.Equal(300, read.ProductionWindowSeconds);
            Assert.Equal(2, read.Production.Length);
            Assert.Equal("SteelPlate", read.Production[0].RecipeId);
            Assert.Equal("Comp:SteelPlate", read.Production[0].Key);
            Assert.True(read.Production[0].IsOutput);
            Assert.Equal(120L, read.Production[0].Quantity);
            Assert.Equal(637_900_000_000_000_000L, read.Production[0].StartUtcTicks);
            Assert.Equal(637_900_000_100_000_000L, read.Production[0].EndUtcTicks);
            // The second row too, with every field distinct from the first: a reader one field short
            // reads it at the wrong offset.
            Assert.Equal("Ingot:Iron", read.Production[1].Key);
            Assert.False(read.Production[1].IsOutput);
            Assert.Equal(2520L, read.Production[1].Quantity);
            Assert.Equal(637_900_000_000_000_001L, read.Production[1].StartUtcTicks);
            Assert.Equal(637_900_000_100_000_002L, read.Production[1].EndUtcTicks);

            Assert.Equal(4, read.RepairHulls);
            Assert.True(read.TradeBuyOptIn);
            Assert.Equal("N8466", read.Id);
        }

        // The demand block is appended past the production block. Two rows of each table with every
        // field distinct, so a reader one field short reads the second row at the wrong offset.
        [Fact]
        public void NodeDetail_DemandBlock_RoundTrips()
        {
            WireNodeDetail sent = OwnedDetail();
            sent.ProductionWindowSeconds = 300;
            sent.Production = new[] { new WireNodeProductionSpan { RecipeId = "R", Key = "Ingot:Iron", Quantity = 9 } };
            sent.DemandWindowSeconds = 1800;
            sent.Demand = new[]
            {
                new WireNodeDemand
                {
                    Kind = "Item:Ingot:Iron", DestNodeId = "", Quantity = 500, HasHave = true, Have = 120,
                    UsedPerHour = 42.5, Class = (byte)WireDemandClass.RecipeInput, DestinedForSale = false,
                    FirstPostedUtcTicks = 637_900_000_000_000_000L,
                },
                new WireNodeDemand
                {
                    Kind = "Ship:Trade", DestNodeId = "N12", Quantity = 0, HasHave = false, Have = 0,
                    UsedPerHour = 0, Class = (byte)WireDemandClass.Unclassified, DestinedForSale = true,
                    FirstPostedUtcTicks = 637_900_000_000_000_007L,
                },
            };
            sent.EndedDemand = new[]
            {
                new WireNodeEndedDemand
                {
                    Kind = "Item:Comp:SteelPlate", DestNodeId = "", PeakQuantity = 800,
                    Class = (byte)WireDemandClass.ShipBuild, DestinedForSale = false, Consumed = false,
                    FirstPostedUtcTicks = 637_900_000_000_000_001L, EndedUtcTicks = 637_900_000_100_000_001L,
                },
                new WireNodeEndedDemand
                {
                    Kind = "Ship:Haul", DestNodeId = "N3", PeakQuantity = 0,
                    Class = (byte)WireDemandClass.Unclassified, DestinedForSale = true, Consumed = true,
                    FirstPostedUtcTicks = 637_900_000_000_000_002L, EndedUtcTicks = 637_900_000_100_000_002L,
                },
            };

            WireNodeDetail read;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out read));

            Assert.Equal(1800, read.DemandWindowSeconds);
            Assert.Equal(2, read.Demand.Length);
            Assert.Equal("Item:Ingot:Iron", read.Demand[0].Kind);
            Assert.Equal(500L, read.Demand[0].Quantity);
            Assert.True(read.Demand[0].HasHave);
            Assert.Equal(120L, read.Demand[0].Have);
            Assert.Equal(42.5, read.Demand[0].UsedPerHour);
            Assert.Equal((byte)WireDemandClass.RecipeInput, read.Demand[0].Class);
            Assert.Equal("Ship:Trade", read.Demand[1].Kind);
            Assert.Equal("N12", read.Demand[1].DestNodeId);
            Assert.False(read.Demand[1].HasHave);
            Assert.True(read.Demand[1].DestinedForSale);
            Assert.Equal(637_900_000_000_000_007L, read.Demand[1].FirstPostedUtcTicks);

            Assert.Equal(2, read.EndedDemand.Length);
            Assert.Equal("Item:Comp:SteelPlate", read.EndedDemand[0].Kind);
            Assert.Equal(800L, read.EndedDemand[0].PeakQuantity);
            Assert.Equal((byte)WireDemandClass.ShipBuild, read.EndedDemand[0].Class);
            Assert.False(read.EndedDemand[0].Consumed);
            Assert.Equal(637_900_000_100_000_001L, read.EndedDemand[0].EndedUtcTicks);
            Assert.Equal("Ship:Haul", read.EndedDemand[1].Kind);
            Assert.Equal("N3", read.EndedDemand[1].DestNodeId);
            Assert.True(read.EndedDemand[1].DestinedForSale);
            Assert.True(read.EndedDemand[1].Consumed);
            Assert.Equal(637_900_000_000_000_002L, read.EndedDemand[1].FirstPostedUtcTicks);
            Assert.Equal(637_900_000_100_000_002L, read.EndedDemand[1].EndedUtcTicks);

            // The block before it is undisturbed.
            Assert.Equal(300, read.ProductionWindowSeconds);
            Assert.Equal(9L, Assert.Single(read.Production).Quantity);
        }

        [Fact]
        public void NodeDetail_DemandBlock_IsCappedByTheWriter_AndWithheldFromANonOwner()
        {
            WireNodeDetail sent = OwnedDetail();
            sent.Demand = new WireNodeDemand[FactionsWire.MaxDemandRowsPerNode + 3];
            for (int i = 0; i < sent.Demand.Length; i++) sent.Demand[i] = new WireNodeDemand { Kind = "Item:K" + i };
            sent.EndedDemand = new WireNodeEndedDemand[FactionsWire.MaxEndedDemandRowsPerNode + 3];
            for (int i = 0; i < sent.EndedDemand.Length; i++) sent.EndedDemand[i] = new WireNodeEndedDemand { Kind = "Item:E" + i };
            sent.DemandWindowSeconds = 1800;

            WireNodeDetail read;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out read));
            Assert.Equal(FactionsWire.MaxDemandRowsPerNode, read.Demand.Length);
            Assert.Equal("Item:K0", read.Demand[0].Kind);
            Assert.Equal(FactionsWire.MaxEndedDemandRowsPerNode, read.EndedDemand.Length);
            Assert.Equal("Item:E0", read.EndedDemand[0].Kind);
            Assert.Equal(1800, read.DemandWindowSeconds);

            sent.Owned = false;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out read));
            Assert.Empty(read.Demand);
            Assert.Empty(read.EndedDemand);
            Assert.Equal(0, read.DemandWindowSeconds);
        }

        /// <summary>The client casts the class byte straight to <see cref="WireDemandClass"/>, so the
        /// two enums must agree ordinal for ordinal.</summary>
        [Fact]
        public void WireDemandClass_MirrorsCoreDemandClass()
        {
            foreach (DemandClass c in Enum.GetValues(typeof(DemandClass)))
                Assert.Equal(c.ToString(), ((WireDemandClass)(byte)c).ToString());
            Assert.Equal(Enum.GetValues(typeof(DemandClass)).Length, Enum.GetValues(typeof(WireDemandClass)).Length);
        }

        [Fact]
        public void FactionDemand_RoundTrips_WithEachRowsNode()
        {
            var sent = new WireFactionDemand
            {
                FactionTag = "EXP",
                Nodes = new[] { new WireNamedNode { Id = "N096", Name = "Sentinel Post" }, new WireNamedNode { Id = "N3", Name = "" } },
                Demand = new[]
                {
                    new WireNodeDemand
                    {
                        Kind = "Item:Ingot:Iron", AtNodeId = "N096", DestNodeId = "", Quantity = 500, HasHave = true,
                        Have = 120, UsedPerHour = 42.5, Class = (byte)WireDemandClass.Install,
                        FirstPostedUtcTicks = 637_900_000_000_000_000L,
                    },
                    new WireNodeDemand
                    {
                        Kind = "Item:Comp:SteelPlate", AtNodeId = "", Quantity = 2532, HasHave = true, Have = 7,
                        Class = (byte)WireDemandClass.Install, DestinedForSale = true,
                    },
                },
                Ended = new[]
                {
                    new WireNodeEndedDemand
                    {
                        Kind = "Ship:Haul", AtNodeId = "", DestNodeId = "N3", PeakQuantity = 0, Consumed = true,
                        FirstPostedUtcTicks = 637_900_000_000_000_002L, EndedUtcTicks = 637_900_000_100_000_002L,
                    },
                },
                WindowSeconds = 1800,
                DemandTotal = 2,
                EndedTotal = 9,
            };

            WireFactionDemand read;
            Assert.True(FactionsWire.TryReadFactionDemandSnapshot(FactionsWire.WriteFactionDemandSnapshot(sent), out read));

            Assert.Equal("EXP", read.FactionTag);
            Assert.Equal(2, read.Nodes.Length);
            Assert.Equal("Sentinel Post", read.Nodes[0].Name);
            Assert.Equal("N3", read.Nodes[1].Id);
            Assert.Equal(2, read.Demand.Length);
            Assert.Equal("N096", read.Demand[0].AtNodeId);
            Assert.Equal(42.5, read.Demand[0].UsedPerHour);
            Assert.Equal(637_900_000_000_000_000L, read.Demand[0].FirstPostedUtcTicks);
            Assert.Equal("", read.Demand[1].AtNodeId);
            Assert.Equal(7L, read.Demand[1].Have);
            Assert.True(read.Demand[1].DestinedForSale);
            WireNodeEndedDemand e = Assert.Single(read.Ended);
            Assert.Equal("N3", e.DestNodeId);
            Assert.True(e.Consumed);
            Assert.Equal(637_900_000_100_000_002L, e.EndedUtcTicks);
            Assert.Equal(1800, read.WindowSeconds);
            Assert.Equal(2, read.DemandTotal);
            Assert.Equal(9, read.EndedTotal);
        }

        [Fact]
        public void FactionDemand_IsCappedByTheWriter_AndTheRequestRoundTrips()
        {
            var sent = new WireFactionDemand
            {
                FactionTag = "F",
                Demand = new WireNodeDemand[FactionsWire.MaxFactionDemandRows + 3],
                Ended = new WireNodeEndedDemand[FactionsWire.MaxFactionEndedDemandRows + 3],
            };
            for (int i = 0; i < sent.Demand.Length; i++) sent.Demand[i] = new WireNodeDemand { Kind = "Item:K" + i };
            for (int i = 0; i < sent.Ended.Length; i++) sent.Ended[i] = new WireNodeEndedDemand { Kind = "Item:K" + i };

            WireFactionDemand read;
            Assert.True(FactionsWire.TryReadFactionDemandSnapshot(FactionsWire.WriteFactionDemandSnapshot(sent), out read));
            Assert.Equal(FactionsWire.MaxFactionDemandRows, read.Demand.Length);
            Assert.Equal(FactionsWire.MaxFactionEndedDemandRows, read.Ended.Length);
            Assert.Empty(read.Nodes);

            bool stream;
            Assert.True(FactionsWire.TryReadFactionDemandRequest(FactionsWire.WriteFactionDemandRequest(true), out stream));
            Assert.True(stream);
            Assert.False(FactionsWire.TryReadFactionDemandRequest(FactionsWire.WriteMarketRequest(true), out stream));
        }

        [Fact]
        public void NodeDetail_ProductionBlock_IsCappedByTheWriter_AndWithheldFromANonOwner()
        {
            WireNodeDetail sent = OwnedDetail();
            sent.Production = new WireNodeProductionSpan[FactionsWire.MaxProductionRowsPerNode + 5];
            for (int i = 0; i < sent.Production.Length; i++)
                sent.Production[i] = new WireNodeProductionSpan { RecipeId = "R" + i, Key = "Ore:Iron", Quantity = i };

            WireNodeDetail read;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out read));
            // Most recent first on the wire, so the cut keeps the head and drops the tail.
            Assert.Equal(FactionsWire.MaxProductionRowsPerNode, read.Production.Length);
            Assert.Equal("R0", read.Production[0].RecipeId);

            sent.Owned = false;
            sent.ProductionWindowSeconds = 300;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out read));
            Assert.NotNull(read.Production);
            Assert.Empty(read.Production);
            Assert.Equal(0, read.ProductionWindowSeconds);
        }

        [Fact]
        public void NodeCommand_TradeVerbs_RoundTrip()
        {
            foreach (WireNodeCommand verb in new[] { WireNodeCommand.TradeToggle, WireNodeCommand.TradeBudget })
            {
                byte[] data = FactionsWire.WriteNodeCommandRequest("N8466", verb, "buy", -1);
                string nodeId; WireNodeCommand read; string target; int value;
                Assert.True(FactionsWire.TryReadNodeCommandRequest(data, out nodeId, out read, out target, out value));
                Assert.Equal(verb, read);
                Assert.Equal("N8466", nodeId);
                Assert.Equal("buy", target);
                Assert.Equal(-1, value);        // the clear sentinel must survive as a negative
            }
        }

        [Fact]
        public void NodeCommand_HoldVerbs_RoundTrip()
        {
            foreach (WireNodeCommand verb in new[] { WireNodeCommand.HoldToggle, WireNodeCommand.HoldRank,
                                                     WireNodeCommand.HoldBudget })
            {
                byte[] data = FactionsWire.WriteNodeCommandRequest("N8466", verb, "", 9);
                string nodeId; WireNodeCommand read; string target; int value;
                Assert.True(FactionsWire.TryReadNodeCommandRequest(data, out nodeId, out read, out target, out value));
                Assert.Equal(verb, read);
                Assert.Equal("N8466", nodeId);
                Assert.Equal(9, value);
            }
        }

        [Fact]
        public void NodeDetailRequest_RoundTrips()
        {
            byte[] data = FactionsWire.WriteNodeDetailRequest("N8466");

            string nodeId;
            Assert.True(FactionsWire.TryReadNodeDetailRequest(data, out nodeId));
            Assert.Equal("N8466", nodeId);
        }

        [Fact]
        public void NodeDetailRequest_WithNoNode_IsTheUnsubscribe()
        {
            // An empty id is how the window says "stop watching"; there is no separate verb, so a
            // null must survive the round trip as the same empty string the server tests for.
            byte[] data = FactionsWire.WriteNodeDetailRequest(null);

            string nodeId;
            Assert.True(FactionsWire.TryReadNodeDetailRequest(data, out nodeId));
            Assert.Equal("", nodeId);
        }

        [Fact]
        public void NodeDetailSnapshot_RoundTripsEveryField()
        {
            WireNodeDetail sent = OwnedDetail();
            byte[] data = FactionsWire.WriteNodeDetailSnapshot(sent);

            WireNodeDetail got;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(data, out got));

            Assert.True(got.Exists);
            Assert.Equal(sent.Id, got.Id);
            Assert.Equal(sent.DisplayName, got.DisplayName);
            Assert.Equal(sent.DominantOre, got.DominantOre);
            Assert.Equal(sent.Surveyed, got.Surveyed);
            Assert.Equal(sent.Zone, got.Zone);
            Assert.Equal(sent.OwnerTag, got.OwnerTag);
            Assert.Equal(sent.Relation, got.Relation);
            Assert.True(got.HasStation);
            Assert.Equal(sent.XKm, got.XKm);
            Assert.Equal(sent.YKm, got.YKm);
            Assert.Equal(sent.ZKm, got.ZKm);
            Assert.Equal(sent.HoldStrength, got.HoldStrength);

            Assert.True(got.Owned);
            Assert.Equal(sent.ResourceValue, got.ResourceValue);
            Assert.Equal(sent.Richness, got.Richness);
            // Distinct from each other on purpose: equal fixture values would round-trip even if
            // the writer sent one field twice.
            Assert.Equal(sent.FoundCost, got.FoundCost);
            Assert.Equal(sent.FoundHave, got.FoundHave);
            Assert.Equal(sent.FoundBlock, got.FoundBlock);
            Assert.True(got.ShipyardPaused);
            Assert.False(got.HasStore);

            Assert.Equal(2, got.Capabilities.Length);
            Assert.Equal("Shipyard", got.Capabilities[0].Cap);
            Assert.Equal(2, got.Capabilities[0].Level);

            Assert.Equal(2, got.Stock.Length);
            Assert.Equal("Superconductor", got.Stock[1].Key);
            Assert.Equal(413L, got.Stock[1].Quantity);
            Assert.Equal(90L, got.Stock[1].Reserved);
            Assert.Equal(57L, got.Stock[1].ForSale);
            Assert.Equal(520L, got.Stock[1].Target);
            Assert.Equal((byte)WireStockKeptFor.Owed, got.Stock[1].KeptFor);
            // The first row too: a reader one field short reads the second row at the wrong offset.
            Assert.Equal(9000L, got.Stock[0].ForSale);
            Assert.Equal((byte)WireStockKeptFor.None, got.Stock[0].KeptFor);

            Assert.Equal(2, got.Builds.Length);
            Assert.Equal("Raid:001", got.Builds[0].Id);
            Assert.Equal("Raid", got.Builds[0].Role);
            Assert.Equal("AWL_Raider", got.Builds[0].PrefabId);
            Assert.Equal(8960L, got.Builds[0].BomTotal);
            Assert.Equal(2131L, got.Builds[0].Collected);
            Assert.Equal(3, got.Builds[0].Priority);
            Assert.Equal(638_000_000_000_000_000L, got.Builds[0].ExpiresUtcTicks);
            Assert.Equal(21_720.5, got.Builds[0].StalledSeconds);
            Assert.Equal(637_900_000_000_000_000L, got.Builds[0].CreatedUtcTicks);

            // The slot, which is what separates a build collecting nothing from one merely next in
            // line. Both halves asserted: a writer that hardcoded either would pass on one row.
            Assert.True(got.Builds[0].Working);
            Assert.Equal(0, got.Builds[0].WaitingBehind);
            Assert.False(got.Builds[1].Working);
            Assert.Equal(1, got.Builds[1].WaitingBehind);

            Assert.Equal(2, got.Builds[0].Bom.Length);
            Assert.Equal("Comp:Superconductor", got.Builds[0].Bom[0].Key);
            Assert.Equal(3100L, got.Builds[0].Bom[0].Need);
            Assert.Equal(413L, got.Builds[0].Bom[0].Have);
            Assert.Equal(7L, got.Builds[0].Bom[0].AtNode);
            Assert.Equal(21L, got.Builds[0].Bom[0].Elsewhere);
            Assert.Equal(55L, got.Builds[0].Bom[0].EnRoute);
            Assert.True(got.Builds[0].Bom[0].Unserved);
            Assert.False(got.Builds[0].Bom[1].Unserved);
            Assert.Equal(12000L, got.Builds[0].Bom[1].AtNode);

            Assert.Single(got.Builds[0].ReservedInputs);
            Assert.Equal("Ingot:Iron", got.Builds[0].ReservedInputs[0].Key);
            Assert.Equal(4400L, got.Builds[0].ReservedInputs[0].Quantity);

            // A build with no breakdown reads as empty, never null: the window enumerates it.
            Assert.Empty(got.Builds[1].Bom);
            Assert.Empty(got.Builds[1].ReservedInputs);

            Assert.Single(got.Ships);
            Assert.Equal("AWL_Hauler", got.Ships[0].PrefabId);
            Assert.Equal(4, got.Ships[0].Ready);
            Assert.Equal(1, got.Ships[0].ListedForSale);

            Assert.Equal(3, got.Develop.Length);
            Assert.Equal("Refinery", got.Develop[0].Cap);
            Assert.Equal(1, got.Develop[0].Level);
            Assert.Equal(15, got.Develop[0].MaxLevel);
            Assert.Equal(5000L, got.Develop[0].Cost);
            Assert.Equal((byte)WireDevelopBlock.None, got.Develop[0].Block);
            Assert.True(got.Develop[0].Reserved);
            Assert.Equal(4, got.Develop[0].Priority);
            // The two fields that make a block legible: the name of what is missing, and the pair
            // the window subtracts to say how far short it is.
            Assert.Equal("ManufactoryII", got.Develop[1].BlockKey);
            Assert.Equal((byte)WireDevelopBlock.PrerequisiteMissing, got.Develop[1].Block);
            Assert.Equal("Comp:SteelPlate", got.Develop[2].BlockKey);
            Assert.Equal(120L, got.Develop[2].BlockHave);
            Assert.Equal(400L, got.Develop[2].BlockNeed);
        }

        /// <summary>HasStore sits beside ShipyardPaused; the fixture has the yard paused and no store,
        /// so this flips both — a writer that sent one flag twice passes exactly one of the two.</summary>
        [Fact]
        public void NodeDetailSnapshot_HasStoreIsItsOwnFlag()
        {
            WireNodeDetail sent = OwnedDetail();
            sent.ShipyardPaused = false;
            sent.HasStore = true;

            WireNodeDetail got;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out got));

            Assert.False(got.ShipyardPaused);
            Assert.True(got.HasStore);
        }

        [Fact]
        public void NodeDetailSnapshot_ForANodeTheViewerDoesNotOwn_CarriesNoDetailAtAll()
        {
            // The privacy rule is enforced by the writer, not by the reader or the window: an
            // unowned node's stock, queue and ships must not be on the wire in the first place.
            WireNodeDetail sent = OwnedDetail();
            sent.Owned = false;
            sent.OwnerTag = "RVL";
            sent.Relation = (byte)WireRelation.Enemy;

            byte[] data = FactionsWire.WriteNodeDetailSnapshot(sent);

            WireNodeDetail got;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(data, out got));
            Assert.True(got.Exists);
            Assert.False(got.Owned);
            Assert.Equal("RVL", got.OwnerTag);
            Assert.Equal(sent.HoldStrength, got.HoldStrength);

            Assert.Empty(got.Stock);
            Assert.Empty(got.Builds);
            Assert.Empty(got.Ships);
            Assert.Empty(got.Capabilities);
            Assert.Empty(got.Develop);
            Assert.Equal(0.0, got.ResourceValue);
            Assert.Equal(0.0, got.Richness);
            Assert.Equal(0L, got.FoundCost);
            Assert.Equal(0L, got.FoundHave);
            Assert.False(got.ShipyardPaused);

            // And the payload really is shorter, not merely blanked on read.
            Assert.True(data.Length < FactionsWire.WriteNodeDetailSnapshot(OwnedDetail()).Length);
        }

        [Fact]
        public void NodeDetailSnapshot_ForAnUnknownNode_SaysSoRatherThanReadingBlank()
        {
            byte[] data = FactionsWire.WriteNodeDetailSnapshot(
                new WireNodeDetail { Exists = false, Id = "N9999" });

            WireNodeDetail got;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(data, out got));
            Assert.False(got.Exists);
            Assert.Equal("N9999", got.Id);
            Assert.False(got.Owned);
        }

        [Fact]
        public void NodeDetailSnapshot_WithNothingHeld_ReadsAsEmptyNotNull()
        {
            // Null collections are what the server sends for an empty node; the reader must hand
            // the window arrays it can index without a null check.
            var sent = new WireNodeDetail { Exists = true, Id = "N1", Owned = true };
            byte[] data = FactionsWire.WriteNodeDetailSnapshot(sent);

            WireNodeDetail got;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(data, out got));
            Assert.NotNull(got.Capabilities);
            Assert.NotNull(got.Stock);
            Assert.NotNull(got.Builds);
            Assert.NotNull(got.Ships);
            Assert.NotNull(got.Develop);
            Assert.Empty(got.Stock);
            Assert.Empty(got.Develop);
        }

        [Fact]
        public void NodeCommandRequest_RoundTrips()
        {
            byte[] data = FactionsWire.WriteNodeCommandRequest(
                "N8466", WireNodeCommand.QueuePriority, "Raid:001", -2);

            string nodeId, entryId;
            WireNodeCommand command;
            int value;
            Assert.True(FactionsWire.TryReadNodeCommandRequest(
                data, out nodeId, out command, out entryId, out value));
            Assert.Equal("N8466", nodeId);
            Assert.Equal(WireNodeCommand.QueuePriority, command);
            Assert.Equal("Raid:001", entryId);
            Assert.Equal(-2, value);
        }

        [Fact]
        public void NodeCommandRequest_CarriesACapabilityWhereTheQueueVerbsCarryAnEntry()
        {
            // One target field for both families. The server switches on the verb before it reads
            // the token, so a capability name has to survive the same round trip a build id does.
            byte[] data = FactionsWire.WriteNodeCommandRequest(
                "N8466", WireNodeCommand.InstallPriority, "PrototechRefinery", 7);

            string nodeId, target;
            WireNodeCommand command;
            int value;
            Assert.True(FactionsWire.TryReadNodeCommandRequest(
                data, out nodeId, out command, out target, out value));
            Assert.Equal(WireNodeCommand.InstallPriority, command);
            Assert.Equal("PrototechRefinery", target);
            Assert.Equal(7, value);
        }

        [Fact]
        public void NodeCommandRequest_WithAnAbsurdToken_IsRejected()
        {
            // The only client message that changes server state; a length no real id can reach is
            // a malformed or hostile payload, and must not reach the runtime at all.
            byte[] data = FactionsWire.WriteNodeCommandRequest(
                new string('x', 4096), WireNodeCommand.QueueCancel, "Raid:001", 0);

            string nodeId, entryId;
            WireNodeCommand command;
            int value;
            Assert.False(FactionsWire.TryReadNodeCommandRequest(
                data, out nodeId, out command, out entryId, out value));
        }

        [Fact]
        public void NodeCommandResult_RoundTrips()
        {
            byte[] data = FactionsWire.WriteNodeCommandResult(false, "No build 'Raid:009' at N8466.");

            bool ok;
            string message;
            Assert.True(FactionsWire.TryReadNodeCommandResult(data, out ok, out message));
            Assert.False(ok);
            Assert.Equal("No build 'Raid:009' at N8466.", message);
        }

        [Fact]
        public void NodeDetailMessages_ReadAsTheWrongKind_Fail()
        {
            byte[] snapshot = FactionsWire.WriteNodeDetailSnapshot(OwnedDetail());
            byte[] request = FactionsWire.WriteNodeDetailRequest("N8466");

            string nodeId;
            Assert.False(FactionsWire.TryReadNodeDetailRequest(snapshot, out nodeId));

            WireNodeDetail detail;
            Assert.False(FactionsWire.TryReadNodeDetailSnapshot(request, out detail));

            bool ok;
            string message;
            Assert.False(FactionsWire.TryReadNodeCommandResult(snapshot, out ok, out message));
        }

        [Fact]
        public void NodeDetailFeature_IsADistinctBitFromTheOtherThree()
        {
            // Its own bit rather than a corner of Assets: it is the one feature carrying a
            // client-originated mutation, so a server must be able to withhold it while still
            // handing out the read-only register.
            byte[] data = FactionsWire.WriteUplinkGranted(
                "1.0", WireFeatures.Scanner | WireFeatures.Map | WireFeatures.Assets);

            string serverVersion;
            WireFeatures features;
            Assert.True(FactionsWire.TryReadUplinkGranted(data, out serverVersion, out features));
            Assert.False((features & WireFeatures.NodeDetail) != 0);

            byte[] withDetail = FactionsWire.WriteUplinkGranted(
                "1.0", WireFeatures.Assets | WireFeatures.NodeDetail);
            Assert.True(FactionsWire.TryReadUplinkGranted(withDetail, out serverVersion, out features));
            Assert.True((features & WireFeatures.NodeDetail) != 0);
            Assert.NotEqual(WireFeatures.Assets, WireFeatures.NodeDetail);
        }

        [Fact]
        public void NodeDetailKinds_DoNotCollideWithAnyOtherFeature()
        {
            // Additive kinds in their own block: an older client granted nothing new simply drops
            // them, which is what lets this ship without a ProtocolVersion bump.
            Assert.Equal(64, (byte)WireMessageKind.NodeDetailRequest);
            Assert.Equal(65, (byte)WireMessageKind.NodeDetailSnapshot);
            Assert.Equal(66, (byte)WireMessageKind.NodeCommandRequest);
            Assert.Equal(67, (byte)WireMessageKind.NodeCommandResult);
            Assert.True((byte)WireMessageKind.NodeDetailRequest > (byte)WireMessageKind.AssetsSnapshot);
        }
        [Fact]
        public void EventsRequest_RoundTrips()
        {
            byte[] data = FactionsWire.WriteEventsRequest(true);

            bool stream;
            Assert.True(FactionsWire.TryReadEventsRequest(data, out stream));
            Assert.True(stream);
        }

        [Fact]
        public void EventsSnapshot_RoundTrips()
        {
            var events = new List<WireFactionEvent>
            {
                new WireFactionEvent
                {
                    UtcTicks = 638_000_000_000_000_000L,
                    Kind = 1,
                    ActorTag = "RVN",
                    OtherTag = "EXP",
                    NodeId = "N042",
                    NodeName = "Blackreach Depot",
                    Amount = 1200,
                    Ratio = 0.75,
                    ViewerIsActor = false,
                },
            };

            byte[] data = FactionsWire.WriteEventsSnapshot("EXP", events);

            string factionTag;
            List<WireFactionEvent> read;
            Assert.True(FactionsWire.TryReadEventsSnapshot(data, out factionTag, out read));
            Assert.Equal("EXP", factionTag);
            WireFactionEvent e = Assert.Single(read);
            Assert.Equal(638_000_000_000_000_000L, e.UtcTicks);
            Assert.Equal(1, e.Kind);
            Assert.Equal("RVN", e.ActorTag);
            Assert.Equal("EXP", e.OtherTag);
            Assert.Equal("N042", e.NodeId);
            Assert.Equal("Blackreach Depot", e.NodeName);
            Assert.Equal(1200, e.Amount);
            Assert.Equal(0.75, e.Ratio, 3);
            Assert.False(e.ViewerIsActor);
        }

        [Fact]
        public void EventsSnapshot_WithNoFaction_IsAReadableEmptyFeed()
        {
            byte[] data = FactionsWire.WriteEventsSnapshot("", new List<WireFactionEvent>());

            string factionTag;
            List<WireFactionEvent> read;
            Assert.True(FactionsWire.TryReadEventsSnapshot(data, out factionTag, out read));
            Assert.Equal("", factionTag);
            Assert.Empty(read);
        }

        /// <summary>Null strings are a real server-side state (a one-sided event has no
        /// counterparty); they must come back as empty rather than faulting the whole snapshot.</summary>
        [Fact]
        public void EventsSnapshot_NullStrings_SurviveAsEmpty()
        {
            var events = new List<WireFactionEvent>
            {
                new WireFactionEvent { UtcTicks = 1, Kind = 11, ActorTag = "EXP", ViewerIsActor = true },
            };

            byte[] data = FactionsWire.WriteEventsSnapshot("EXP", events);

            string factionTag;
            List<WireFactionEvent> read;
            Assert.True(FactionsWire.TryReadEventsSnapshot(data, out factionTag, out read));
            Assert.Equal("", read[0].OtherTag);
            Assert.Equal("", read[0].NodeId);
            Assert.Equal("", read[0].NodeName);
        }

        // ---- Feature rock -----------------------------------------------------------------

        [Fact]
        public void FeatureRockObjects_RoundTrips_PreservingOrderAndFields()
        {
            var present = new List<WireRockObject>
            {
                new WireRockObject { StorageName = "fr1a2b3c4d_10_-3_7_0", EntityId = 432345564227567616L,
                                     XKm = 1.5, YKm = -2.25, ZKm = 3.75, SizeM = 128.5,
                                     MaterialSubTypeId = "Ice", ShapeSeed = 987654321 },
                new WireRockObject { StorageName = "fr1a2b3c4d_10_-3_7_1", EntityId = -1L,
                                     XKm = -0.5, YKm = 0, ZKm = 0, SizeM = 64,
                                     MaterialSubTypeId = "Iron", ShapeSeed = int.MinValue },
            };
            var removed = new List<string> { "fr99999999_0_0_0_0", "fr99999999_0_0_0_1" };

            byte[] data = FactionsWire.WriteFeatureRockObjects(present, removed, false, 4);

            List<WireRockObject> readPresent;
            List<string> readRemoved;
            bool readReset;
            int readGeneratorVersion;
            Assert.True(FactionsWire.TryReadFeatureRockObjects(data, out readPresent, out readRemoved, out readReset, out readGeneratorVersion));
            Assert.False(readReset);
            Assert.Equal(2, readPresent.Count);
            Assert.Equal("fr1a2b3c4d_10_-3_7_0", readPresent[0].StorageName);
            Assert.Equal(432345564227567616L, readPresent[0].EntityId);
            Assert.Equal(-2.25, readPresent[0].YKm);
            Assert.Equal(128.5, readPresent[0].SizeM);
            Assert.Equal("Ice", readPresent[0].MaterialSubTypeId);
            Assert.Equal(987654321, readPresent[0].ShapeSeed);
            Assert.Equal(int.MinValue, readPresent[1].ShapeSeed);
            Assert.Equal(-1L, readPresent[1].EntityId);
            Assert.Equal(new[] { "fr99999999_0_0_0_0", "fr99999999_0_0_0_1" }, readRemoved);
        }

        /// <summary>The entity id is the one field both ends must agree on exactly — it is what lets
        /// SE swap an edited storage into the rock the client already built instead of stacking a
        /// second one on it. Feature-rock ids sit in SE's voxel range (the 0x600... top bits), which
        /// is well past what survives a narrower integer, so it is worth pinning rather than
        /// assuming.</summary>
        [Fact]
        public void FeatureRockObjects_EntityId_SurvivesTheFullVoxelIdRange()
        {
            long voxelRangeId = unchecked((long)0x06FFFFFFFFFFFFFFUL);
            var present = new List<WireRockObject>
            {
                new WireRockObject { StorageName = "fr0_0_0_0_0", EntityId = voxelRangeId },
            };

            byte[] data = FactionsWire.WriteFeatureRockObjects(present, null, false, 4);

            List<WireRockObject> readPresent;
            List<string> readRemoved;
            bool readReset;
            int readGeneratorVersion;
            Assert.True(FactionsWire.TryReadFeatureRockObjects(data, out readPresent, out readRemoved, out readReset, out readGeneratorVersion));
            Assert.Equal(voxelRangeId, readPresent[0].EntityId);
        }

        /// <summary>A reset with nothing present is the message that tells a client to drop
        /// everything it holds. If the flag were lost the client would keep rendering rock the
        /// server is actively withholding, so the empty case is the one that must survive.</summary>
        [Fact]
        public void FeatureRockObjects_ResetFlag_SurvivesAnEmptyDelta()
        {
            byte[] data = FactionsWire.WriteFeatureRockObjects(new List<WireRockObject>(), new List<string>(), true, 4);

            List<WireRockObject> present;
            List<string> removed;
            bool reset;
            int generatorVersion;
            Assert.True(FactionsWire.TryReadFeatureRockObjects(data, out present, out removed, out reset, out generatorVersion));
            Assert.True(reset);
            Assert.Empty(present);
            Assert.Empty(removed);
        }

        [Fact]
        public void FeatureRockObjects_NullLists_RoundTripAsEmpty()
        {
            byte[] data = FactionsWire.WriteFeatureRockObjects(null, null, false, 4);

            List<WireRockObject> present;
            List<string> removed;
            bool reset;
            int generatorVersion;
            Assert.True(FactionsWire.TryReadFeatureRockObjects(data, out present, out removed, out reset, out generatorVersion));
            Assert.Empty(present);
            Assert.Empty(removed);
        }

        [Fact]
        public void FeatureRockObjects_NullStrings_SurviveAsEmpty()
        {
            var present = new List<WireRockObject> { new WireRockObject { EntityId = 7 } };

            byte[] data = FactionsWire.WriteFeatureRockObjects(present, null, false, 4);

            List<WireRockObject> read;
            List<string> removed;
            bool reset;
            int generatorVersion;
            Assert.True(FactionsWire.TryReadFeatureRockObjects(data, out read, out removed, out reset, out generatorVersion));
            Assert.Equal("", read[0].StorageName);
            Assert.Equal("", read[0].MaterialSubTypeId);
        }

        /// <summary>The feature bit must be independently withholdable: it changes what the SERVER
        /// stops sending, so a server that grants everything else and not this one has to be
        /// expressible, or a client would derive rock a server is still replicating.</summary>
        [Fact]
        public void FeatureRock_IsGrantedIndependentlyOfEveryOtherFeature()
        {
            WireFeatures everythingElse = WireFeatures.Scanner | WireFeatures.Map | WireFeatures.Assets
                                        | WireFeatures.NodeDetail | WireFeatures.Events | WireFeatures.StandingOrders;

            byte[] withoutRock = FactionsWire.WriteUplinkGranted("1.0", everythingElse);
            byte[] onlyRock = FactionsWire.WriteUplinkGranted("1.0", WireFeatures.FeatureRock);

            string version;
            WireFeatures read;
            Assert.True(FactionsWire.TryReadUplinkGranted(withoutRock, out version, out read));
            Assert.Equal(WireFeatures.None, read & WireFeatures.FeatureRock);

            Assert.True(FactionsWire.TryReadUplinkGranted(onlyRock, out version, out read));
            Assert.Equal(WireFeatures.FeatureRock, read & WireFeatures.FeatureRock);
            Assert.Equal(WireFeatures.None, read & everythingElse);
        }

        /// <summary>The handshake the server enrols on. Both flags must survive independently: the
        /// server refuses enrolment when its own material control disagrees with the client's, so a
        /// flag that decoded as the other would either withhold rock from a client that cannot build
        /// it, or send authored-ore rock to one that will render vanilla's mix.</summary>
        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void FeatureRockRequest_RoundTripsBothFlagsIndependently(bool canDerive, bool materialControl)
        {
            byte[] data = FactionsWire.WriteFeatureRockRequest(canDerive, materialControl);

            bool readCanDerive, readMaterialControl;
            Assert.True(FactionsWire.TryReadFeatureRockRequest(data, out readCanDerive, out readMaterialControl));
            Assert.Equal(canDerive, readCanDerive);
            Assert.Equal(materialControl, readMaterialControl);
        }

        /// <summary>The two feature-rock kinds travel in opposite directions on one channel. Reading
        /// either as the other must fail rather than decode into plausible nonsense — a request
        /// misread as a delta would have a client build rock from a boolean.</summary>
        [Fact]
        public void FeatureRockKinds_AreNotInterchangeable()
        {
            byte[] request = FactionsWire.WriteFeatureRockRequest(true, true);
            byte[] objects = FactionsWire.WriteFeatureRockObjects(new List<WireRockObject>(), null, true, 4);

            List<WireRockObject> present;
            List<string> removed;
            bool reset;
            int generatorVersion;
            Assert.False(FactionsWire.TryReadFeatureRockObjects(request, out present, out removed, out reset, out generatorVersion));

            bool canDerive, materialControl;
            Assert.False(FactionsWire.TryReadFeatureRockRequest(objects, out canDerive, out materialControl));
        }

        /// <summary>The generator version selects WHICH of SE's asteroid generators builds the shape,
        /// so a client that read a different value from the one sent would build the same seed into
        /// an entirely different rock. It is operator-tunable server-side, so the client has no safe
        /// default to fall back on and this field is the only thing standing between the two ends.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(int.MaxValue)]
        public void FeatureRockObjects_CarriesTheServersGeneratorVersion(int generatorVersion)
        {
            var present = new List<WireRockObject>
            {
                new WireRockObject { StorageName = "fr0_0_0_0_0", EntityId = 7, ShapeSeed = 42, SizeM = 128 },
            };

            byte[] data = FactionsWire.WriteFeatureRockObjects(present, null, false, generatorVersion);

            List<WireRockObject> read;
            List<string> removed;
            bool reset;
            int readGeneratorVersion;
            Assert.True(FactionsWire.TryReadFeatureRockObjects(data, out read, out removed, out reset, out readGeneratorVersion));
            Assert.Equal(generatorVersion, readGeneratorVersion);
            // The per-rock fields must survive alongside it -- this field was inserted into an
            // existing layout, and a misplaced read would corrupt everything after it.
            Assert.Equal(42, read[0].ShapeSeed);
            Assert.Equal(128, read[0].SizeM);
            Assert.Equal(7L, read[0].EntityId);
        }

        /// <summary>The ore-volume overrides are the difference between a rock the client draws with
        /// the server's amount of ore in it and one it draws with the world default. They are
        /// nullable server-side, so absent must survive as absent: zero is a different instruction
        /// (a zero count is a rock with no ore at all) and must never be confused with "inherit".</summary>
        [Fact]
        public void FeatureRockObjects_DepositOverrides_RoundTripPresentAndAbsentIndependently()
        {
            var present = new List<WireRockObject>
            {
                new WireRockObject { StorageName = "both", ShapeSeed = 1,
                                     HasDepositCount = true, DepositCountCoefficient = 3.5,
                                     HasDepositSize = true,  DepositSizeDenominator = 20.0 },
                new WireRockObject { StorageName = "neither", ShapeSeed = 2 },
                new WireRockObject { StorageName = "count only", ShapeSeed = 3,
                                     HasDepositCount = true, DepositCountCoefficient = 2.0 },
                new WireRockObject { StorageName = "size only", ShapeSeed = 4,
                                     HasDepositSize = true, DepositSizeDenominator = 120.0 },
            };

            byte[] data = FactionsWire.WriteFeatureRockObjects(present, null, false, 4);

            List<WireRockObject> read;
            List<string> removed;
            bool reset;
            int generatorVersion;
            Assert.True(FactionsWire.TryReadFeatureRockObjects(data, out read, out removed, out reset, out generatorVersion));

            Assert.True(read[0].HasDepositCount);  Assert.Equal(3.5, read[0].DepositCountCoefficient);
            Assert.True(read[0].HasDepositSize);   Assert.Equal(20.0, read[0].DepositSizeDenominator);

            // Absent stays absent. If this ever reads back as "present, value 0" the client would
            // build a rock with no ore in it at all rather than one with the world's usual amount.
            Assert.False(read[1].HasDepositCount);
            Assert.False(read[1].HasDepositSize);

            Assert.True(read[2].HasDepositCount);  Assert.False(read[2].HasDepositSize);
            Assert.False(read[3].HasDepositCount); Assert.True(read[3].HasDepositSize);
            Assert.Equal(120.0, read[3].DepositSizeDenominator);

            // And the fields that were already there still decode around the inserted ones.
            Assert.Equal("size only", read[3].StorageName);
            Assert.Equal(4, read[3].ShapeSeed);
        }


        // ---- SignatureStatus (protocol 10) --------------------------------------------------

        [Fact]
        public void SignatureStatus_RoundTrips()
        {
            byte[] payload = FactionsWire.WriteSignatureStatus(2.375);

            double signature;
            Assert.True(FactionsWire.TryReadSignatureStatus(payload, out signature));
            Assert.Equal(2.375, signature, 9);
        }

        [Fact]
        public void SignatureStatus_RejectsAnotherKindsPayload()
        {
            // The reader must refuse a well-formed message of a different kind rather than
            // deserialising whatever happens to sit at that offset.
            byte[] other = FactionsWire.WriteGridSpeedConfig(100, 100);

            double signature;
            Assert.False(FactionsWire.TryReadSignatureStatus(other, out signature));
            Assert.Equal(0, signature);
        }

        [Fact]
        public void NetTraceStart_RoundTrips()
        {
            byte[] payload = FactionsWire.WriteNetTraceStart(-73021996184912345L, 600, "20261014-170512");

            long eid;
            int frames;
            string stamp;
            Assert.True(FactionsWire.TryReadNetTraceStart(payload, out eid, out frames, out stamp));
            Assert.Equal(-73021996184912345L, eid);
            Assert.Equal(600, frames);
            Assert.Equal("20261014-170512", stamp);
        }

        [Fact]
        public void NetTraceStart_RejectsAnotherKindsPayload()
        {
            byte[] other = FactionsWire.WriteGridSpeedConfig(100, 100);

            long eid;
            int frames;
            string stamp;
            Assert.False(FactionsWire.TryReadNetTraceStart(other, out eid, out frames, out stamp));
            Assert.Equal(0, eid);
        }

        [Fact]
        public void SignatureStatus_CarriesAPlayerScaleValueWithoutLoss()
        {
            // A 50,000 t armed capital under jump reaches ~38. The field must carry the top of the
            // range as faithfully as the bottom -- there is no ship ceiling any more.
            foreach (double value in new[] { 0.10, 0.725, 1.305, 12.78, 38.33 })
            {
                double read;
                Assert.True(FactionsWire.TryReadSignatureStatus(
                    FactionsWire.WriteSignatureStatus(value), out read));
                Assert.Equal(value, read, 9);
            }
        }

        [Fact]
        public void ProtocolVersion_MovedForTheNewMessage()
        {
            // A protocol change is anything that moves the wire, in either direction: 12 took a
            // per-node separation away once the map stopped needing one, 13 adds Richness beside
            // ResourceValue on both node messages, 14 adds the scanner contact's motion sample, 15
            // appends the develop menu to the node snapshot. A client on any older number misreads
            // every contact past the field -- and 14's block is variable-length, so it misreads
            // every contact after a moving one too. 15 is appended past every existing field, but a
            // NEWER client against an OLDER server would still read off the end of a snapshot that
            // has no menu, which is what this number refuses on the handshake instead.
            // 16 appends the node snapshot's hold-upkeep block (opt-in, rank, cost, and the
            // faction's three resolved reinforce budget knobs) -- appended past the develop menu,
            // so the same newer-client-against-older-server reasoning applies.
            // 17 replaces founding's two ore-tally fields with its credit price on BOTH node
            // messages -- a substitution mid-payload, not an append, so an older client reads every
            // field after it at the wrong offset in either direction.
            // 18 adds the node-inventory message block (FAC-282). Purely new kinds, appended past
            // every existing field, so nothing already on the wire moves -- but a newer client
            // sending kind 192 to an older server would be answered by silence rather than a
            // refusal, and the handshake is where that is caught.
            // 19 adds the campaign message block (FAC-293 slice C) and the Campaigns feature bit.
            // Purely new kinds again, so the same silence-versus-refusal reasoning as 18 applies.
            // 20 appends the node snapshot's trade block (FAC-298) -- the two opt-in switches, the
            // ranked partner count, the trade-hull flag, in-flight against the cap, and the
            // faction's three resolved trade budget knobs -- plus two node-command verbs. Appended
            // past the hold block, so 16's newer-client-against-older-server reasoning applies
            // unchanged: a client that expects the trade fields reads off the end of a snapshot
            // written without them.
            // 21 grows each build row in the node snapshot: its queue timestamp, whether it holds
            // one of the yard's working slots, and its per-component BOM with the estate's answer
            // for every shortfall. Two variable-length lists INSIDE an existing repeated block, so
            // an older client does not merely read off the end -- it reads the second build's id
            // out of the first build's component rows, and every field after the queue at the
            // wrong offset.
            // 22 adds the Build button's four kinds and the Build feature bit. Purely new kinds
            // again, so on their own 18's silence-versus-refusal reasoning would apply -- but this
            // one also RETIRES three buttons from the node window, and a client that still draws
            // Cancel build / Priority +-1 there is a client whose Builds... window is from before
            // the queue rows carried their BOM. Refusing it at the handshake is what keeps the two
            // halves of this slice from being mixed.
            // 23 adds the fleet-doctrine block AND takes the Build half off WireStandingOrder --
            // prefab, role, target, concurrency, scope and the live Have, all of which sat AFTER the
            // Mine fields in a repeated block. A client on 22 reading a 23 snapshot reads the next
            // order's id out of the gap where its own Build fields should be, so this is a
            // substitution mid-payload in the sense 17 was, not an append.
            // 24 adds the store-shelf block and the PlayerShelf feature bit. Purely new kinds, so
            // 18's silence-versus-refusal reasoning applies on its own -- but the sharper reason is
            // that the shelf kinds carry the SAME codec shape as the doctrine ones, snapshot for
            // snapshot and command for command. A client that guessed at a kind it did not know
            // would decode a shelf snapshot's line rows as doctrine rows and show a player their
            // shop as their fleet. The handshake refusing an unknown version is what keeps that
            // from ever being attempted; AShelfMessage_IsNotReadableAsADoctrineMessage is the
            // per-message half of the same guarantee.
            // 25 appends the map fleet message's alarm rows and inserts the node snapshot's
            // safe-zone block. The alarm list is an append past the fleets, so a client on 24
            // stops reading where the list begins and never sees a siege -- survivable. The zone
            // block is not: it sits ABOVE the ownership seam, between HoldStrength and Owned, so an
            // older client reads the first byte of a double as its Owned flag and then decodes the
            // whole unowned-node early return at the wrong offset. A substitution mid-payload in
            // the sense 17 and 23 were.
            // 26 appends the mission and hull count to a scanner contact, for fleet kinds only.
            // Two bytes INSIDE each fleet's row, so a client on 25 does not merely stop early: it
            // finishes the fleet, leaves the two bytes unread, and then takes the mission byte as
            // the length prefix of the next contact's Key. Every contact behind the first fleet in
            // the message decodes at the wrong offset. A substitution mid-payload in the sense 17,
            // 23 and 25 were, and the handshake refusal is again the whole defence.
            //   The alternative considered and rejected was extending ScannerContactKind's VALUE
            // space with per-mission fleet kinds, which needs no version move at all -- and that is
            // exactly what made it worse. An un-restarted client would hit its `default:` arm and
            // draw every convoy in the world as a grey GPS diamond, silently, with nothing to say
            // why. A refusal that names the version is the better failure (FAC-373 for the
            // disclosure question this deliberately does not answer).
            // 27 appends the last attempt's outcome to a campaign row: what came back, when, the
            // duel range that decided it with both sides' output there, and the hull cost. Seven
            // fields at the END of each row, which is the same substitution as 25 and 26 whenever
            // the message carries more than one campaign -- a client on 26 finishes the first row,
            // leaves seven fields unread, and takes the outcome byte as the length prefix of the
            // next row's FocusId. The handshake refusal is again the whole defence.
            //   The alternative considered and rejected was reusing LastDispatchUtcTicks with a
            // sign or sentinel to mean "refused", which needs no version move -- and would have
            // made an un-restarted client render a refusal as a strike, silently, which is the
            // defect being fixed.
            //   28 appends OverCeiling to every doctrine line, after RouteBlocked. The line is a
            // fixed-order record in a list, so a client on 27 would read the new byte as the next
            // line's Id length; the handshake refusal is the defence again (FAC-385).
            //   29 adds the Tactics window's two kinds and the Tactics feature bit. Purely new kinds,
            // so 18's reasoning applies on its own: a newer client subscribing to an older server
            // would be answered by silence, and the handshake is where that is caught.
            //   30 adds the wing actions' two kinds and the WingCommand feature bit (FAC-404). New
            // kinds only, as 29: a newer client pressing a wing action on an older server would get
            // no answer.
            //   31 adds a seventh wing command byte, protect me (FAC-408): an older server reads it
            // as unknown and drops the press, so the two ends must be built from the same table.
            //   32 adds the combat outcome notice and the CombatOutcome feature bit (FAC-416). A new
            // server-to-client kind only: an older client would drop it unread, and the handshake
            // says why instead.
            //   33 adds restoration at the Services Terminal and the Restoration feature bit
            // (FAC-412): two request and two answer kinds. A newer client pressing Restore on an
            // older server would get no answer.
            //   34 reshapes the restore quote into the Repair tab's panel (WireRestoreQuote: the
            // design, the counts, the bill with a short column, a running restore's progress) and
            // adds the managed flag the terminal's probe reads.
            //   35 offers the TrafficVoice feature and its TrafficCallout kind (FAC-419). A link
            // request carries no feature list, so an older client would be granted a voice it cannot
            // play and lose its fleet chat whispers; refusing it at the handshake is the fix.
            //   36 adds the pilot call (server to client) and the voice preference (client to server),
            // FAC-419 slice 5. A 35 client granted TrafficVoice would ignore the pilot calls and could not
            // switch the voice off on the server.
            //   37 appends the call's source and the listener's reach to both traffic messages, and the
            // pilot's SNR stops including reflector gain (FAC-419, aiming by ear). A 36 client reads the
            // leading fields and ignores the rest, so it would play every off-beam call the server now
            // sends as if it were in reach.
            //   38 appends a Stock order's item, target and stock to every standing-order row, and the
            // item key and quantity to the create request, and adds the Stock-order item list (FAC-431).
            // A 37 client reads each row short and every row after the first at the wrong offset.
            //   39 appends the repair queue to the node snapshot, a repair count to every asset node
            // (mid-row, before ShipyardPaused), and repair counts to every doctrine line and census
            // row (FAC-432). A 38 client reads every asset node and doctrine row after the first at
            // the wrong offset.
            //   40 appends the fleet id and the queue fields to every traffic callout and adds five
            // queue events a 39 client refuses (FAC-436).
            //   41 widens the message kind to two bytes: every header moves, and a 40 client's
            // five-byte header is refused as malformed.
            //   42 appends what is for sale, the station's target and why the rest is kept to every
            // node stock row, and a store flag to the node snapshot. A 41 client reads every stock row
            // after the first at the wrong offset.
            //   43 drops the role from the build request and the doctrine-line create request, and
            // carries a doctrine line's fitting roles where its role was (FAC-448). A 42 client's
            // build request reads its role as the prefab id.
            //   44 adds a ban flag to every doctrine line (after OverCeiling) and a blocked reason to
            // every refit row (FAC-451). A 43 client reads every doctrine row after the first at the
            // wrong offset.
            //   45 appended the central-bank sell switch to the node snapshot; 46 moved it to the shelf
            // snapshot, after StockingOff. 47 takes it off the shelf snapshot again -- bank sales are
            // per item, on their own four kinds (255-258). A 46 client reads the shelf line count one
            // byte early.
            //   48 writes a node contact's name where a fleet contact's mission and hull count go
            // (FAC-143). A 47 client reads the name's length prefix as the next contact's key.
            //   49 appends the node's production history to the node snapshot, past the repair block.
            // A 49 client against a 48 server reads off the end of every owned snapshot.
            //   50 widens the map node's capability mask to 16 bits, for Farm (9) and FoodProcessor
            // (10). A 49 client reads the mask's high byte as the node's zone.
            //   51 appends the node's demand and its ended demands to the node snapshot, past the
            // production block. A 51 client against a 50 server reads off the end of every owned snapshot.
            //   52 grants the HUD status panel, and a granted client is sent no siege notification.
            // A 51 client granted it would be told nothing at all while sieging.
            //   53 appends the node's workers, beds, recruit price and supplied hours past HasStore,
            // and adds the Recruit node command (FAC-493). A 52 client reads the worker count as the
            // capability count.
            //   54 inserts the survey confidence, both readings' targets, the Found bar, the
            // resolved-share floor and the ore mix between the worker fields and the capability list.
            // A 53 client reads the survey confidence's bytes as the capability count.
            //   55 drops the Found bar from the ground block. A 54 client reads the resolved-share
            // floor's bytes as the Found bar and everything after it one double late.
            // FAC-219 -- the branch name is not the check, this number is.
            //   56 appends the speaker to every traffic callout and adds the four trespass hail
            // events: an older client would read a hail event as unknown and miss the field.
            //   58 splits the workers' supplied hours into food hours and air hours. A 57 client
            // reads the air hours as the survey confidence and everything after it one double late.
            //   59 appends the all-stations fields to every standing-order row (FAC-499). A 58 client
            // reads the next row's id from the AllStations byte.
            Assert.Equal(59, FactionsWire.ProtocolVersion);
        }

        /// <summary>FAC-376: a campaign row carries what came back from its last attempt. Two rows,
        /// because the seven new fields sit at the end of each one — a reader that mis-sizes a row
        /// decodes the second at the wrong offset and that is the only way to see it.</summary>
        [Fact]
        public void CampaignsSnapshot_RoundTripsTheOutcomeOfEachRow()
        {
            var sent = new List<WireCampaign>
            {
                new WireCampaign
                {
                    FocusId = "conquest:N873", Kind = 1, Cause = 4,
                    TargetNodeId = "N873", TargetNodeName = "Vulcan Forward", RegionKey = "",
                    StagingNodeId = "N483", ExpiresUtcTicks = 637_000_000_000_000_000L,
                    LastDispatchUtcTicks = 636_000_000_000_000_000L, CandidateCount = 3, PlayerIssued = true,
                    LastOutcome = 1, LastOutcomeUtcTicks = 636_000_000_000_000_000L,
                    LastOutcomeRangeM = 2000, LastOutcomeOwnDps = 11695, LastOutcomeOpposedDps = 136321,
                    LastOutcomeHullsLost = 0, LastOutcomeHullsTotal = 0,
                },
                new WireCampaign
                {
                    FocusId = "raid:N512", Kind = 0, Cause = 1,
                    TargetNodeId = "N512", TargetNodeName = "Ravenna Dock", RegionKey = "",
                    StagingNodeId = "N483", ExpiresUtcTicks = 637_000_000_000_000_001L,
                    LastDispatchUtcTicks = 636_000_000_000_000_001L, CandidateCount = 1, PlayerIssued = false,
                    LastOutcome = 2, LastOutcomeUtcTicks = 636_000_000_000_000_500L,
                    LastOutcomeRangeM = 800, LastOutcomeOwnDps = 116427, LastOutcomeOpposedDps = 109772,
                    LastOutcomeHullsLost = 2, LastOutcomeHullsTotal = 24,
                },
            };

            string tag; int cap; long raidCost, conquerCost; List<WireCampaign> got;
            Assert.True(FactionsWire.TryReadCampaignsSnapshot(
                FactionsWire.WriteCampaignsSnapshot("EXP", 4, 5000, 20000, sent),
                out tag, out cap, out raidCost, out conquerCost, out got));

            Assert.Equal("EXP", tag);
            Assert.Equal(2, got.Count);
            for (int i = 0; i < sent.Count; i++)
            {
                Assert.Equal(sent[i].FocusId, got[i].FocusId);
                Assert.Equal(sent[i].TargetNodeName, got[i].TargetNodeName);
                Assert.Equal(sent[i].LastDispatchUtcTicks, got[i].LastDispatchUtcTicks);
                Assert.Equal(sent[i].PlayerIssued, got[i].PlayerIssued);
                Assert.Equal(sent[i].LastOutcome, got[i].LastOutcome);
                Assert.Equal(sent[i].LastOutcomeUtcTicks, got[i].LastOutcomeUtcTicks);
                Assert.Equal(sent[i].LastOutcomeRangeM, got[i].LastOutcomeRangeM);
                Assert.Equal(sent[i].LastOutcomeOwnDps, got[i].LastOutcomeOwnDps);
                Assert.Equal(sent[i].LastOutcomeOpposedDps, got[i].LastOutcomeOpposedDps);
                Assert.Equal(sent[i].LastOutcomeHullsLost, got[i].LastOutcomeHullsLost);
                Assert.Equal(sent[i].LastOutcomeHullsTotal, got[i].LastOutcomeHullsTotal);
            }
            // The outcome is a SEPARATE clock: an intercepted raid was dispatched before it was lost.
            Assert.True(got[1].LastOutcomeUtcTicks > got[1].LastDispatchUtcTicks);
        }

        [Fact]
        public void ExpandRequest_RoundTripsTheActionAndTheQuoteItActsOn()
        {
            foreach (WireExpandAction action in new[]
                     { WireExpandAction.Unsubscribe, WireExpandAction.Subscribe, WireExpandAction.Commit })
            {
                WireExpandAction got;
                int quoteId;
                Assert.True(FactionsWire.TryReadExpandRequest(
                    FactionsWire.WriteExpandRequest(action, 4242), out got, out quoteId));
                Assert.Equal(action, got);
                Assert.Equal(4242, quoteId);
            }
        }

        [Fact]
        public void ExpandRequest_WithAnActionThisBuildDoesNotKnow_IsRefusedNotCoerced()
        {
            // Every value this enum could grow into is a state change, so an unknown one must not
            // fall through to Unsubscribe -- or, far worse, to Commit.
            byte[] data = FactionsWire.WriteExpandRequest(WireExpandAction.Commit, 1);
            data[data.Length - 5] = 200;   // the action byte sits just before the 4-byte quote id

            WireExpandAction action;
            int quoteId;
            Assert.False(FactionsWire.TryReadExpandRequest(data, out action, out quoteId));
        }

        [Fact]
        public void ExpandStatus_RoundTripsEveryField()
        {
            byte[] data = FactionsWire.WriteExpandStatus(
                quoteId: 77, clear: true, zone: 2, separationKm: 15.0,
                placedXKm: -1234.5, placedYKm: 6789.25, placedZKm: -0.125,
                snapDistanceKm: 61.5, costCredits: 1000000L,
                chargedToPlayer: true, committed: true,
                verdict: WireExpandVerdict.Ok, nodeId: "N4711");

            WireExpandStatus got;
            Assert.True(FactionsWire.TryReadExpandStatus(data, out got));

            Assert.Equal(77, got.QuoteId);
            Assert.True(got.Clear);
            Assert.Equal(2, got.Zone);
            Assert.Equal(15.0, got.SeparationKm);
            Assert.Equal(-1234.5, got.PlacedXKm);
            Assert.Equal(6789.25, got.PlacedYKm);
            Assert.Equal(-0.125, got.PlacedZKm);
            Assert.Equal(61.5, got.SnapDistanceKm);
            Assert.Equal(1000000L, got.CostCredits);
            Assert.True(got.ChargedToPlayer);
            Assert.True(got.Committed);
            Assert.Equal(WireExpandVerdict.Ok, got.Verdict);
            Assert.Equal("N4711", got.NodeId);
        }

        [Fact]
        public void ExpandStatus_CarriesNothingAboutWhatBlocksAnOccupiedSite()
        {
            // The blocking node's id and its distance are both things the server knows and this
            // payload must never carry -- see the design's "Occupied says only 'occupied'". The check
            // is structural: a refusal with no node has no field left that could name one.
            byte[] data = FactionsWire.WriteExpandStatus(
                quoteId: 9, clear: false, zone: 0, separationKm: 500.0,
                placedXKm: 1, placedYKm: 2, placedZKm: 3, snapDistanceKm: 0,
                costCredits: 1000000L, chargedToPlayer: false, committed: true,
                verdict: WireExpandVerdict.Occupied, nodeId: null);

            WireExpandStatus got;
            Assert.True(FactionsWire.TryReadExpandStatus(data, out got));

            Assert.False(got.Clear);
            Assert.Equal(WireExpandVerdict.Occupied, got.Verdict);
            Assert.Equal("", got.NodeId);
        }

        // ---- Node inventory (FAC-282) ---------------------------------------------------------

        [Fact]
        public void NodeStockRequest_CarriesEitherIdentifier()
        {
            // The store block has a station and no node id; the node window has the reverse. Both
            // shapes must survive the round trip, because the server picks which one it resolves.
            byte[] fromStore = FactionsWire.WriteNodeStockRequest(123456789L, null);
            long stationId;
            string nodeId;
            Assert.True(FactionsWire.TryReadNodeStockRequest(fromStore, out stationId, out nodeId));
            Assert.Equal(123456789L, stationId);
            Assert.Equal("", nodeId);

            byte[] fromWindow = FactionsWire.WriteNodeStockRequest(0, "N007");
            Assert.True(FactionsWire.TryReadNodeStockRequest(fromWindow, out stationId, out nodeId));
            Assert.Equal(0L, stationId);
            Assert.Equal("N007", nodeId);
        }

        [Fact]
        public void NodeStockSnapshot_RoundTrips()
        {
            var rows = new[]
            {
                new WireNodeTransferRow { Key = "Ore:Iron", Label = "Iron Ore", Available = 5000, Reserved = 0 },
                new WireNodeTransferRow { Key = "Comp:SteelPlate", Label = "Steel Plate", Available = 12, Reserved = 300 },
            };

            byte[] data = FactionsWire.WriteNodeStockSnapshot("N007", "Ashfall Yard", true, "", rows);

            string nodeId, nodeName, message;
            bool mayTransfer;
            WireNodeTransferRow[] got;
            Assert.True(FactionsWire.TryReadNodeStockSnapshot(
                data, out nodeId, out nodeName, out mayTransfer, out message, out got));

            Assert.Equal("N007", nodeId);
            Assert.Equal("Ashfall Yard", nodeName);
            Assert.True(mayTransfer);
            Assert.Equal(2, got.Length);
            Assert.Equal("Comp:SteelPlate", got[1].Key);
            Assert.Equal(12L, got[1].Available);
            Assert.Equal(300L, got[1].Reserved);
        }

        [Fact]
        public void NodeStockSnapshot_RefusalCarriesTheReasonAndNoRows()
        {
            // A refusal must not read as an empty station: the reason travels, the rows do not.
            byte[] data = FactionsWire.WriteNodeStockSnapshot(
                "N036", "Someone Else's Yard", false, "Not your faction's station.", null);

            string nodeId, nodeName, message;
            bool mayTransfer;
            WireNodeTransferRow[] got;
            Assert.True(FactionsWire.TryReadNodeStockSnapshot(
                data, out nodeId, out nodeName, out mayTransfer, out message, out got));

            Assert.False(mayTransfer);
            Assert.Equal("Not your faction's station.", message);
            Assert.Empty(got);
        }

        [Fact]
        public void NodeTransferRequest_RoundTripsBothDirections()
        {
            byte[] withdraw = FactionsWire.WriteNodeTransferRequest("N007", false, "Ore:Iron", 2500L, 777L);

            string nodeId, invKey;
            bool deposit;
            long amount, target;
            Assert.True(FactionsWire.TryReadNodeTransferRequest(
                withdraw, out nodeId, out deposit, out invKey, out amount, out target));
            Assert.Equal("N007", nodeId);
            Assert.False(deposit);
            Assert.Equal("Ore:Iron", invKey);
            Assert.Equal(2500L, amount);
            Assert.Equal(777L, target);

            byte[] deposited = FactionsWire.WriteNodeTransferRequest("N007", true, "Ingot:Iron", 40L, 777L);
            Assert.True(FactionsWire.TryReadNodeTransferRequest(
                deposited, out nodeId, out deposit, out invKey, out amount, out target));
            Assert.True(deposit);
            Assert.Equal("Ingot:Iron", invKey);
        }

        [Fact]
        public void NodeTransferRequest_RejectsAnOverlongToken()
        {
            // The token bound is the only thing standing between a hostile payload and a node-id
            // lookup, so it has to refuse rather than truncate.
            byte[] data = FactionsWire.WriteNodeTransferRequest(
                new string('N', 200), false, "Ore:Iron", 1L, 1L);

            string nodeId, invKey;
            bool deposit;
            long amount, target;
            Assert.False(FactionsWire.TryReadNodeTransferRequest(
                data, out nodeId, out deposit, out invKey, out amount, out target));
        }

        [Fact]
        public void NodeTransferResult_CarriesAPartialMove()
        {
            // A hold that filled up is a success with a smaller number, not a failure -- the amount
            // travels separately from the verdict for exactly this case.
            byte[] data = FactionsWire.WriteNodeTransferResult(true, 1800L, "Hold full at 1,800.");

            bool ok;
            long moved;
            string message;
            Assert.True(FactionsWire.TryReadNodeTransferResult(data, out ok, out moved, out message));
            Assert.True(ok);
            Assert.Equal(1800L, moved);
            Assert.Equal("Hold full at 1,800.", message);
        }

        // ---- Ship assets (FAC-283) --------------------------------------------------------------

        [Fact]
        public void ShipAssetsKinds_DoNotCollideWithAnyOtherFeature()
        {
            // Additive kinds in their own block: an older client granted nothing new simply drops
            // them, which is what lets this ship without a ProtocolVersion bump. 196 is the first
            // free number past the node-inventory block, which holds 192-195.
            Assert.Equal(196, (byte)WireMessageKind.TerminalAssetsRequest);
            Assert.Equal(197, (byte)WireMessageKind.TerminalAssetsSnapshot);
            Assert.Equal(198, (byte)WireMessageKind.ShipWithdrawRequest);
            Assert.Equal(199, (byte)WireMessageKind.ShipWithdrawResult);
            Assert.Equal(200, (byte)WireMessageKind.ShipSendRequest);
            Assert.Equal(201, (byte)WireMessageKind.ShipSendResult);
            Assert.Equal(202, (byte)WireMessageKind.ShipScanRequest);
            Assert.Equal(203, (byte)WireMessageKind.ShipScanResult);
            Assert.True((byte)WireMessageKind.TerminalAssetsRequest > (byte)WireMessageKind.ExpandStatus);
        }

        [Fact]
        public void ShipAssets_IsItsOwnGrant_NotAFoldOfNodeDetail()
        {
            // The bit exists so a server can hand out the read-only node window while withholding
            // the surface that spawns and despawns hulls. A grant of one must therefore never read
            // as a grant of the other.
            byte[] detailOnly = FactionsWire.WriteUplinkGranted("1.0", WireFeatures.NodeDetail);

            string serverVersion;
            WireFeatures features;
            Assert.True(FactionsWire.TryReadUplinkGranted(detailOnly, out serverVersion, out features));
            Assert.False((features & WireFeatures.ShipAssets) != 0);

            byte[] both = FactionsWire.WriteUplinkGranted(
                "1.0", WireFeatures.NodeDetail | WireFeatures.ShipAssets);
            Assert.True(FactionsWire.TryReadUplinkGranted(both, out serverVersion, out features));
            Assert.True((features & WireFeatures.ShipAssets) != 0);
            Assert.True((features & WireFeatures.NodeDetail) != 0);
            Assert.Equal((long)WireFeatures.ShipAssets, 1L << 12);
        }

        [Fact]
        public void TerminalAssetsRequest_RoundTrips()
        {
            byte[] data = FactionsWire.WriteTerminalAssetsRequest(123456789012345L);

            long terminalEntityId;
            Assert.True(FactionsWire.TryReadTerminalAssetsRequest(data, out terminalEntityId));
            Assert.Equal(123456789012345L, terminalEntityId);
        }

        [Fact]
        public void TerminalAssetsRequest_RejectsATruncatedBuffer()
        {
            byte[] data = FactionsWire.WriteTerminalAssetsRequest(7L);
            var cut = new byte[data.Length - 3];
            System.Array.Copy(data, cut, cut.Length);

            long terminalEntityId;
            Assert.False(FactionsWire.TryReadTerminalAssetsRequest(cut, out terminalEntityId));
        }

        [Fact]
        public void TerminalAssetsSnapshot_RoundTripsEveryField()
        {
            var rows = new[]
            {
                new WireShipAssetRow
                {
                    NodeId = "N007", NodeName = "Ashfall Yard", PrefabId = "AWL_Hauler_Mk2",
                    Count = 3, Blocks = 412, Pcu = 9100, GridSize = "Large", DistanceKm = 0.0,
                },
                new WireShipAssetRow
                {
                    NodeId = "N019", NodeName = "Redcrown Depot", PrefabId = "AWL_Scout",
                    Count = 1, Blocks = 88, Pcu = 1240, GridSize = "Small", DistanceKm = 1842.75,
                },
            };

            byte[] data = FactionsWire.WriteTerminalAssetsSnapshot(
                42L, "N007", "Ashfall Yard", true, "", rows);

            long terminalEntityId;
            string nodeId, nodeName, message;
            bool mayUse;
            WireShipAssetRow[] got;
            Assert.True(FactionsWire.TryReadTerminalAssetsSnapshot(
                data, out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out got));

            Assert.Equal(42L, terminalEntityId);
            Assert.Equal("N007", nodeId);
            Assert.Equal("Ashfall Yard", nodeName);
            Assert.True(mayUse);
            Assert.Equal("", message);
            Assert.Equal(2, got.Length);
            Assert.Equal("N019", got[1].NodeId);
            Assert.Equal("Redcrown Depot", got[1].NodeName);
            Assert.Equal("AWL_Scout", got[1].PrefabId);
            Assert.Equal(1, got[1].Count);
            Assert.Equal(88, got[1].Blocks);
            Assert.Equal(1240, got[1].Pcu);
            Assert.Equal("Small", got[1].GridSize);
            Assert.Equal(1842.75, got[1].DistanceKm, 9);
        }

        [Fact]
        public void TerminalAssetsSnapshot_RefusalCarriesTheReasonAndNoRows()
        {
            // At an NPC station the surface is absent, and the refusal has to say so: an empty list
            // on its own reads as a faction that owns no ships.
            byte[] data = FactionsWire.WriteTerminalAssetsSnapshot(
                42L, "", "", false, "This station is not yours.", null);

            long terminalEntityId;
            string nodeId, nodeName, message;
            bool mayUse;
            WireShipAssetRow[] got;
            Assert.True(FactionsWire.TryReadTerminalAssetsSnapshot(
                data, out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out got));

            Assert.False(mayUse);
            Assert.Equal("This station is not yours.", message);
            Assert.Empty(got);
        }

        [Fact]
        public void TerminalAssetsSnapshot_NullStringsSurviveAsEmpty()
        {
            byte[] data = FactionsWire.WriteTerminalAssetsSnapshot(
                1L, null, null, true, null,
                new[] { new WireShipAssetRow { NodeId = null, NodeName = null, PrefabId = null, GridSize = null } });

            long terminalEntityId;
            string nodeId, nodeName, message;
            bool mayUse;
            WireShipAssetRow[] got;
            Assert.True(FactionsWire.TryReadTerminalAssetsSnapshot(
                data, out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out got));

            Assert.Equal("", nodeId);
            Assert.Equal("", nodeName);
            Assert.Equal("", message);
            Assert.Single(got);
            Assert.Equal("", got[0].NodeId);
            Assert.Equal("", got[0].PrefabId);
            Assert.Equal("", got[0].GridSize);
        }

        [Fact]
        public void TerminalAssetsSnapshot_AtTheRowCap_RoundTripsAndAbove_ItTruncates()
        {
            // The cap is what stops a corrupt length preallocating, so the writer must clamp to it
            // rather than emit a count the reader will refuse.
            var atCap = new WireShipAssetRow[512];
            for (int i = 0; i < atCap.Length; i++)
            {
                atCap[i].NodeId = "N" + i;
                atCap[i].PrefabId = "P" + i;
                atCap[i].Count = 1;
            }

            long terminalEntityId;
            string nodeId, nodeName, message;
            bool mayUse;
            WireShipAssetRow[] got;
            Assert.True(FactionsWire.TryReadTerminalAssetsSnapshot(
                FactionsWire.WriteTerminalAssetsSnapshot(1L, "N0", "", true, "", atCap),
                out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out got));
            Assert.Equal(512, got.Length);
            Assert.Equal("N511", got[511].NodeId);

            var overCap = new WireShipAssetRow[600];
            for (int i = 0; i < overCap.Length; i++) overCap[i].NodeId = "N" + i;
            Assert.True(FactionsWire.TryReadTerminalAssetsSnapshot(
                FactionsWire.WriteTerminalAssetsSnapshot(1L, "N0", "", true, "", overCap),
                out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out got));
            Assert.Equal(512, got.Length);
        }

        [Fact]
        public void TerminalAssetsSnapshot_RejectsAnOutOfRangeCount()
        {
            long terminalEntityId;
            string nodeId, nodeName, message;
            bool mayUse;
            WireShipAssetRow[] got;

            // One row past the cap, with every one of those rows written out in full: without the
            // bound this payload decodes cleanly, so only the bound can refuse it. That is what
            // makes this check able to fail -- a count of int.MaxValue is refused by the allocation
            // throwing whether the bound is there or not.
            Assert.False(FactionsWire.TryReadTerminalAssetsSnapshot(
                HandBuiltAssetsSnapshot(513, 513),
                out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out got));

            // And the same payload one row under the cap is accepted, so the refusal above is the
            // bound and not the hand-built shape.
            Assert.True(FactionsWire.TryReadTerminalAssetsSnapshot(
                HandBuiltAssetsSnapshot(512, 512),
                out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out got));
            Assert.Equal(512, got.Length);

            // A corrupt length must not reach the allocation at all.
            Assert.False(FactionsWire.TryReadTerminalAssetsSnapshot(
                HandBuiltAssetsSnapshot(int.MaxValue, 0),
                out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out got));
            Assert.False(FactionsWire.TryReadTerminalAssetsSnapshot(
                HandBuiltAssetsSnapshot(-1, 0),
                out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out got));
        }

        [Fact]
        public void TerminalAssetsSnapshot_RejectsATruncatedBuffer()
        {
            byte[] data = FactionsWire.WriteTerminalAssetsSnapshot(
                1L, "N007", "Ashfall Yard", true, "",
                new[] { new WireShipAssetRow { NodeId = "N007", PrefabId = "P", Count = 1, DistanceKm = 3.0 } });
            var cut = new byte[data.Length / 2];
            System.Array.Copy(data, cut, cut.Length);

            long terminalEntityId;
            string nodeId, nodeName, message;
            bool mayUse;
            WireShipAssetRow[] got;
            Assert.False(FactionsWire.TryReadTerminalAssetsSnapshot(
                cut, out terminalEntityId, out nodeId, out nodeName, out mayUse, out message, out got));
        }

        /// <summary>An assets snapshot with a declared row count that need not match the number of
        /// rows actually written. Mirrors the codec's own field order, so the count lands where the
        /// reader looks for it and the rows after it decode the way the writer's would.</summary>
        private static byte[] HandBuiltAssetsSnapshot(int declaredCount, int rowsWritten)
        {
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                w.Write((ushort)WireMessageKind.TerminalAssetsSnapshot);
                w.Write(FactionsWire.ProtocolVersion);
                w.Write(1L);
                w.Write("N007");
                w.Write("Ashfall Yard");
                w.Write(true);
                w.Write("");
                w.Write(declaredCount);
                for (int i = 0; i < rowsWritten; i++)
                {
                    w.Write("N" + i);
                    w.Write("");
                    w.Write("P" + i);
                    w.Write(1);
                    w.Write(0);
                    w.Write(0);
                    w.Write("Large");
                    w.Write(0.0);
                }
                return ms.ToArray();
            }
        }

        [Fact]
        public void ShipWithdrawRequest_RoundTrips()
        {
            byte[] data = FactionsWire.WriteShipWithdrawRequest(42L, "N019", "AWL_Scout");

            long terminalEntityId;
            string nodeId, prefabId;
            Assert.True(FactionsWire.TryReadShipWithdrawRequest(
                data, out terminalEntityId, out nodeId, out prefabId));
            Assert.Equal(42L, terminalEntityId);
            Assert.Equal("N019", nodeId);
            Assert.Equal("AWL_Scout", prefabId);
        }

        [Fact]
        public void ShipWithdrawRequest_RejectsAnOverlongToken()
        {
            // The token bound is what stands between a hostile payload and a node or prefab lookup,
            // so it refuses rather than truncates.
            long terminalEntityId;
            string nodeId, prefabId;
            Assert.False(FactionsWire.TryReadShipWithdrawRequest(
                FactionsWire.WriteShipWithdrawRequest(1L, new string('N', 200), "P"),
                out terminalEntityId, out nodeId, out prefabId));
            Assert.False(FactionsWire.TryReadShipWithdrawRequest(
                FactionsWire.WriteShipWithdrawRequest(1L, "N007", new string('P', 200)),
                out terminalEntityId, out nodeId, out prefabId));
        }

        [Fact]
        public void ShipWithdrawRequest_ToleratesNullTokensAndRejectsATruncatedBuffer()
        {
            long terminalEntityId;
            string nodeId, prefabId;
            Assert.True(FactionsWire.TryReadShipWithdrawRequest(
                FactionsWire.WriteShipWithdrawRequest(1L, null, null),
                out terminalEntityId, out nodeId, out prefabId));
            Assert.Equal("", nodeId);
            Assert.Equal("", prefabId);

            byte[] data = FactionsWire.WriteShipWithdrawRequest(1L, "N007", "AWL_Scout");
            var cut = new byte[data.Length - 4];
            System.Array.Copy(data, cut, cut.Length);
            Assert.False(FactionsWire.TryReadShipWithdrawRequest(
                cut, out terminalEntityId, out nodeId, out prefabId));
        }

        [Fact]
        public void ShipWithdrawResult_CarriesTheGuidByteForByte()
        {
            // The guid is SE's own stored-grid key and the whole point of a success -- the client
            // hands it straight to vanilla's retrieval proxy, so it must cross unaltered.
            var guid = new System.Guid("2f1c8f0a-7b3d-4e56-9a01-c4d5e6f70819");
            byte[] data = FactionsWire.WriteShipWithdrawResult(true, guid, "Retrieval queued.");

            bool ok;
            System.Guid got;
            string message;
            Assert.True(FactionsWire.TryReadShipWithdrawResult(data, out ok, out got, out message));
            Assert.True(ok);
            Assert.Equal(guid, got);
            Assert.Equal("Retrieval queued.", message);
        }

        [Fact]
        public void ShipWithdrawResult_RefusalCarriesAnEmptyGuidAndNullMessageSurvives()
        {
            byte[] data = FactionsWire.WriteShipWithdrawResult(false, System.Guid.Empty, null);

            bool ok;
            System.Guid guid;
            string message;
            Assert.True(FactionsWire.TryReadShipWithdrawResult(data, out ok, out guid, out message));
            Assert.False(ok);
            Assert.Equal(System.Guid.Empty, guid);
            Assert.Equal("", message);
        }

        [Fact]
        public void ShipWithdrawResult_RejectsATruncatedGuid()
        {
            // ReadBytes returns short instead of throwing, so a half-read guid would otherwise be
            // accepted as a different -- valid-looking -- key.
            byte[] data = FactionsWire.WriteShipWithdrawResult(
                true, new System.Guid("2f1c8f0a-7b3d-4e56-9a01-c4d5e6f70819"), "x");
            var cut = new byte[6 + 1 + 8];
            System.Array.Copy(data, cut, cut.Length);

            bool ok;
            System.Guid guid;
            string message;
            Assert.False(FactionsWire.TryReadShipWithdrawResult(cut, out ok, out guid, out message));
        }

        [Fact]
        public void ShipSendRequest_RoundTripsAndRejectsATruncatedBuffer()
        {
            byte[] data = FactionsWire.WriteShipSendRequest(42L, 987654321L);

            long terminalEntityId, gridEntityId;
            Assert.True(FactionsWire.TryReadShipSendRequest(data, out terminalEntityId, out gridEntityId));
            Assert.Equal(42L, terminalEntityId);
            Assert.Equal(987654321L, gridEntityId);

            var cut = new byte[data.Length - 5];
            System.Array.Copy(data, cut, cut.Length);
            Assert.False(FactionsWire.TryReadShipSendRequest(cut, out terminalEntityId, out gridEntityId));
        }

        [Fact]
        public void ShipSendResult_NamesThePrefabOnSuccessAndNothingOnRefusal()
        {
            bool ok;
            string prefabId, message;
            Assert.True(FactionsWire.TryReadShipSendResult(
                FactionsWire.WriteShipSendResult(true, "AWL_Hauler_Mk2", "Sent to Ashfall Yard."),
                out ok, out prefabId, out message));
            Assert.True(ok);
            Assert.Equal("AWL_Hauler_Mk2", prefabId);
            Assert.Equal("Sent to Ashfall Yard.", message);

            // The no-match refusal has no prefab to name, and a null one must not break the codec.
            Assert.True(FactionsWire.TryReadShipSendResult(
                FactionsWire.WriteShipSendResult(false, null, "No prefab match - Scan it first."),
                out ok, out prefabId, out message));
            Assert.False(ok);
            Assert.Equal("", prefabId);
            Assert.Equal("No prefab match - Scan it first.", message);
        }

        [Fact]
        public void ShipSendResult_RejectsATruncatedBuffer()
        {
            byte[] data = FactionsWire.WriteShipSendResult(true, "AWL_Hauler_Mk2", "Sent.");
            var cut = new byte[data.Length - 6];
            System.Array.Copy(data, cut, cut.Length);

            bool ok;
            string prefabId, message;
            Assert.False(FactionsWire.TryReadShipSendResult(cut, out ok, out prefabId, out message));
        }

        [Fact]
        public void ShipScanRequest_RoundTripsScopeAndName()
        {
            byte[] data = FactionsWire.WriteShipScanRequest(42L, 987654321L, "faction", "Dawnrunner");

            long terminalEntityId, gridEntityId;
            string scope, name;
            Assert.True(FactionsWire.TryReadShipScanRequest(
                data, out terminalEntityId, out gridEntityId, out scope, out name));
            Assert.Equal(42L, terminalEntityId);
            Assert.Equal(987654321L, gridEntityId);
            Assert.Equal("faction", scope);
            Assert.Equal("Dawnrunner", name);
        }

        [Fact]
        public void ShipScanRequest_TolerantOfNulls_AndRefusesAnOverlongName()
        {
            long terminalEntityId, gridEntityId;
            string scope, name;
            Assert.True(FactionsWire.TryReadShipScanRequest(
                FactionsWire.WriteShipScanRequest(1L, 2L, null, null),
                out terminalEntityId, out gridEntityId, out scope, out name));
            Assert.Equal("", scope);
            Assert.Equal("", name);

            Assert.False(FactionsWire.TryReadShipScanRequest(
                FactionsWire.WriteShipScanRequest(1L, 2L, "faction", new string('x', 200)),
                out terminalEntityId, out gridEntityId, out scope, out name));
        }

        [Fact]
        public void ShipScanRequest_RejectsATruncatedBuffer()
        {
            byte[] data = FactionsWire.WriteShipScanRequest(1L, 2L, "all", "Dawnrunner");
            var cut = new byte[data.Length - 7];
            System.Array.Copy(data, cut, cut.Length);

            long terminalEntityId, gridEntityId;
            string scope, name;
            Assert.False(FactionsWire.TryReadShipScanRequest(
                cut, out terminalEntityId, out gridEntityId, out scope, out name));
        }

        [Fact]
        public void ShipScanResult_RoundTripsAndToleratesANullMessage()
        {
            bool ok;
            string message;
            Assert.True(FactionsWire.TryReadShipScanResult(
                FactionsWire.WriteShipScanResult(true, "Scanned Dawnrunner (412 blocks)."),
                out ok, out message));
            Assert.True(ok);
            Assert.Equal("Scanned Dawnrunner (412 blocks).", message);

            Assert.True(FactionsWire.TryReadShipScanResult(
                FactionsWire.WriteShipScanResult(false, null), out ok, out message));
            Assert.False(ok);
            Assert.Equal("", message);
        }

        [Fact]
        public void StationRefitRequest_RoundTrips()
        {
            string nodeId;
            Assert.True(FactionsWire.TryReadStationRefitRequest(
                FactionsWire.WriteStationRefitRequest("N8466"), out nodeId));
            Assert.Equal("N8466", nodeId);
        }

        [Fact]
        public void StationRefitRequest_WithAnAbsurdNodeId_IsRejected()
        {
            string nodeId;
            Assert.False(FactionsWire.TryReadStationRefitRequest(
                FactionsWire.WriteStationRefitRequest(new string('x', 4096)), out nodeId));
        }

        [Fact]
        public void StationRefitSnapshot_RoundTripsEveryField_IncludingThePerComponentBill()
        {
            var sent = new WireStationRefit
            {
                NodeId = "N8466",
                MayRefit = true,
                Message = "",
                CurrentPrefabId = "Economy_SpaceStation_12",
                PendingPrefabId = "Bastion",
                Site = "Space",
                SiegeBlocked = true,
                Observed = true,
                CollectingPrefabId = "Redoubt",
                CollectedUnits = 412L,
                CollectingBomUnits = 1900L,
                Candidates = new[]
                {
                    new WireStationRefitCandidate
                    {
                        Id = "Bastion",
                        Kind = "scanned",
                        Site = "Space",
                        BlockCount = 1284,
                        Dps = 4821.5f,
                        Cost = 9_400_000L,
                        NetBomTotal = 1900L,
                        Current = false,
                        NetBom = new[]
                        {
                            new WireBuildComponent { Key = "Comp:SteelPlate", Need = 1500L, Have = 300L },
                            new WireBuildComponent { Key = "Comp:Construction", Need = 400L, Have = 0L },
                        },
                    },
                    new WireStationRefitCandidate
                    {
                        Id = "Economy_SpaceStation_12",
                        Kind = "vanilla",
                        Site = "Space",
                        BlockCount = 1102,
                        Dps = 900f,
                        Cost = 4_000_000L,
                        // -1 is not zero: a design nothing can price, versus one the node already
                        // stands as everything of.
                        NetBomTotal = -1L,
                        Current = true,
                        NetBom = null,
                    },
                },
            };

            WireStationRefit read;
            Assert.True(FactionsWire.TryReadStationRefitSnapshot(
                FactionsWire.WriteStationRefitSnapshot(sent), out read));

            Assert.Equal("N8466", read.NodeId);
            Assert.True(read.MayRefit);
            Assert.Equal("Economy_SpaceStation_12", read.CurrentPrefabId);
            Assert.Equal("Bastion", read.PendingPrefabId);
            Assert.Equal("Space", read.Site);
            Assert.True(read.SiegeBlocked);
            Assert.True(read.Observed);
            Assert.Equal("Redoubt", read.CollectingPrefabId);
            Assert.Equal(412L, read.CollectedUnits);
            Assert.Equal(1900L, read.CollectingBomUnits);

            Assert.Equal(2, read.Candidates.Length);
            WireStationRefitCandidate first = read.Candidates[0];
            Assert.Equal("Bastion", first.Id);
            Assert.Equal("scanned", first.Kind);
            Assert.Equal(1284, first.BlockCount);
            Assert.Equal(4821.5f, first.Dps);
            Assert.Equal(9_400_000L, first.Cost);
            Assert.Equal(1900L, first.NetBomTotal);
            Assert.False(first.Current);
            Assert.Equal(2, first.NetBom.Length);
            Assert.Equal("Comp:SteelPlate", first.NetBom[0].Key);
            Assert.Equal(1500L, first.NetBom[0].Need);
            Assert.Equal(300L, first.NetBom[0].Have);
            Assert.Equal("Comp:Construction", first.NetBom[1].Key);

            WireStationRefitCandidate second = read.Candidates[1];
            Assert.Equal(-1L, second.NetBomTotal);
            Assert.True(second.Current);
            Assert.Empty(second.NetBom);
        }

        [Fact]
        public void StationRefitSnapshot_ForANodeTheViewerCannotRefit_CarriesTheReasonAndNoDesigns()
        {
            var sent = new WireStationRefit
            {
                NodeId = "N8466",
                MayRefit = false,
                Message = "N8466 isn't owned by your faction.",
            };

            WireStationRefit read;
            Assert.True(FactionsWire.TryReadStationRefitSnapshot(
                FactionsWire.WriteStationRefitSnapshot(sent), out read));
            Assert.False(read.MayRefit);
            Assert.Equal("N8466 isn't owned by your faction.", read.Message);
            Assert.Empty(read.Candidates);
        }

        [Fact]
        public void StationRefitSnapshot_RejectsATruncatedBuffer()
        {
            byte[] data = FactionsWire.WriteStationRefitSnapshot(new WireStationRefit
            {
                NodeId = "N8466",
                MayRefit = true,
                Candidates = new[] { new WireStationRefitCandidate { Id = "Bastion", Kind = "scanned" } },
            });
            var cut = new byte[8];
            System.Array.Copy(data, cut, cut.Length);

            WireStationRefit read;
            Assert.False(FactionsWire.TryReadStationRefitSnapshot(cut, out read));
        }

        /// <summary>The cut above lands inside the node id, before the candidate loop is ever
        /// entered. This one lands three bytes from the end — inside the last BOM row of the last
        /// candidate — so it is the nested loop that has to fail rather than the header.</summary>
        [Fact]
        public void StationRefitSnapshot_TruncatedInsideACandidatesBillIsRejected()
        {
            byte[] data = FactionsWire.WriteStationRefitSnapshot(new WireStationRefit
            {
                NodeId = "N8466",
                MayRefit = true,
                Candidates = new[]
                {
                    new WireStationRefitCandidate
                    {
                        Id = "Bastion",
                        Kind = "scanned",
                        Site = "Space",
                        NetBom = new[]
                        {
                            new WireBuildComponent { Key = "SteelPlate", Need = 900, Have = 100 },
                            new WireBuildComponent { Key = "Construction", Need = 400, Have = 400 },
                        },
                    },
                },
            });
            WireStationRefit read;
            // The whole buffer first, so the rejection below is the cut and not a payload this
            // codec could never read.
            Assert.True(FactionsWire.TryReadStationRefitSnapshot(data, out read));
            Assert.Equal(2, read.Candidates[0].NetBom.Length);

            var cut = new byte[data.Length - 3];
            System.Array.Copy(data, cut, cut.Length);
            Assert.False(FactionsWire.TryReadStationRefitSnapshot(cut, out read));
        }

        /// <summary>A candidate count one past the cap, with every one of those candidates actually
        /// written. The rows have to be there: a buffer that merely declares more than it carries
        /// would be refused by the read running off the end, and would pass with the cap deleted.</summary>
        [Fact]
        public void StationRefitSnapshot_WithMoreCandidatesThanTheCapIsRejected()
        {
            WireStationRefit read;
            Assert.True(FactionsWire.TryReadStationRefitSnapshot(
                HandBuiltStationRefitSnapshot(StationRefitCandidateCap, 0), out read));
            Assert.Equal(StationRefitCandidateCap, read.Candidates.Length);

            Assert.False(FactionsWire.TryReadStationRefitSnapshot(
                HandBuiltStationRefitSnapshot(StationRefitCandidateCap + 1, 0), out read));
        }

        /// <summary>The same one step down: a single candidate whose own bill runs one row past the
        /// per-build cap, every row written.</summary>
        [Fact]
        public void StationRefitSnapshot_WithACandidateBillPastTheCapIsRejected()
        {
            WireStationRefit read;
            Assert.True(FactionsWire.TryReadStationRefitSnapshot(
                HandBuiltStationRefitSnapshot(1, BomRowCap), out read));
            Assert.Equal(BomRowCap, read.Candidates[0].NetBom.Length);

            Assert.False(FactionsWire.TryReadStationRefitSnapshot(
                HandBuiltStationRefitSnapshot(1, BomRowCap + 1), out read));
        }

        /// <summary>The codec's own bound on one refit listing. Mirrored rather than exposed: the
        /// cap is an internal detail of the reader, and a test that read it from there could not
        /// tell a moved cap from a deleted one.</summary>
        private const int StationRefitCandidateCap = 256;

        /// <summary>The codec's own bound on one build's component bill.</summary>
        private const int BomRowCap = 256;

        /// <summary>A refit snapshot with an arbitrary number of candidates, each carrying an
        /// arbitrary number of BOM rows. Mirrors the codec's own field order, so everything past the
        /// counts decodes the way the writer's output would and only the counts are under test.</summary>
        private static byte[] HandBuiltStationRefitSnapshot(int candidates, int bomRows)
        {
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8))
            {
                w.Write((ushort)WireMessageKind.StationRefitSnapshot);
                w.Write(FactionsWire.ProtocolVersion);
                w.Write("N8466");
                w.Write(true);
                w.Write("");
                w.Write("Outpost");
                w.Write("");
                w.Write("Space");
                w.Write(false);
                w.Write(true);
                w.Write("");
                w.Write(0L);
                w.Write(0L);

                w.Write(candidates);
                for (int i = 0; i < candidates; i++)
                {
                    w.Write("P" + i);
                    w.Write("vanilla");
                    w.Write("Space");
                    w.Write(120);
                    w.Write(0f);
                    w.Write(0L);
                    w.Write(0L);
                    w.Write(false);

                    w.Write(bomRows);
                    for (int b = 0; b < bomRows; b++)
                    {
                        w.Write("C" + b);
                        w.Write(1L);
                        w.Write(0L);
                        w.Write(0L);
                        w.Write(0L);
                        w.Write(0L);
                        w.Write(false);
                    }
                }
                return ms.ToArray();
            }
        }

        [Fact]
        public void StationRefitCommandRequest_RoundTripsTheNodeVerbsAndTheScan()
        {
            string nodeId, prefabId, scope, name;
            WireStationRefitCommand command;
            long terminalEntityId;

            Assert.True(FactionsWire.TryReadStationRefitCommandRequest(
                FactionsWire.WriteStationRefitCommandRequest(
                    "N8466", WireStationRefitCommand.Force, "Bastion", 0L, "", ""),
                out nodeId, out command, out prefabId, out terminalEntityId, out scope, out name));
            Assert.Equal("N8466", nodeId);
            Assert.Equal(WireStationRefitCommand.Force, command);
            Assert.Equal("Bastion", prefabId);
            Assert.Equal(0L, terminalEntityId);

            // The scan names a terminal instead of a node: the server resolves which node that
            // terminal stands on rather than believing a client about it.
            Assert.True(FactionsWire.TryReadStationRefitCommandRequest(
                FactionsWire.WriteStationRefitCommandRequest(
                    "", WireStationRefitCommand.Scan, "", 107_384_512_338L, "all", "Bastion"),
                out nodeId, out command, out prefabId, out terminalEntityId, out scope, out name));
            Assert.Equal("", nodeId);
            Assert.Equal(WireStationRefitCommand.Scan, command);
            Assert.Equal(107_384_512_338L, terminalEntityId);
            Assert.Equal("all", scope);
            Assert.Equal("Bastion", name);
        }

        [Fact]
        public void StationRefitCommandRequest_WithAnAbsurdToken_IsRejected()
        {
            // A client message that changes server state; a length no real id can reach is a
            // malformed or hostile payload, and must not reach the runtime at all.
            string nodeId, prefabId, scope, name;
            WireStationRefitCommand command;
            long terminalEntityId;
            Assert.False(FactionsWire.TryReadStationRefitCommandRequest(
                FactionsWire.WriteStationRefitCommandRequest(
                    "N8466", WireStationRefitCommand.Queue, new string('x', 4096), 0L, "", ""),
                out nodeId, out command, out prefabId, out terminalEntityId, out scope, out name));

            Assert.False(FactionsWire.TryReadStationRefitCommandRequest(
                FactionsWire.WriteStationRefitCommandRequest(
                    "", WireStationRefitCommand.Scan, "", 1L, "faction", new string('n', 4096)),
                out nodeId, out command, out prefabId, out terminalEntityId, out scope, out name));
        }

        [Fact]
        public void StationRefitCommandResult_RoundTripsAndToleratesANullMessage()
        {
            bool ok;
            string message;
            Assert.True(FactionsWire.TryReadStationRefitCommandResult(
                FactionsWire.WriteStationRefitCommandResult(
                    false, "'Bastion' is a surface design; N412 is a space node."),
                out ok, out message));
            Assert.False(ok);
            Assert.Equal("'Bastion' is a surface design; N412 is a space node.", message);

            Assert.True(FactionsWire.TryReadStationRefitCommandResult(
                FactionsWire.WriteStationRefitCommandResult(true, null), out ok, out message));
            Assert.True(ok);
            Assert.Equal("", message);
        }

        [Fact]
        public void StationRefitMessages_ReadAsTheWrongKind_Fail()
        {
            byte[] snapshot = FactionsWire.WriteStationRefitSnapshot(
                new WireStationRefit { NodeId = "N8466" });
            byte[] request = FactionsWire.WriteStationRefitRequest("N8466");

            string nodeId;
            Assert.False(FactionsWire.TryReadStationRefitRequest(snapshot, out nodeId));

            WireStationRefit read;
            Assert.False(FactionsWire.TryReadStationRefitSnapshot(request, out read));

            bool ok;
            string message;
            Assert.False(FactionsWire.TryReadStationRefitCommandResult(snapshot, out ok, out message));

            // The node window's command result has the same two trailing fields; neither reader may
            // decode the other's payload just because they line up.
            Assert.False(FactionsWire.TryReadStationRefitCommandResult(
                FactionsWire.WriteNodeCommandResult(true, "queued"), out ok, out message));
        }

        [Fact]
        public void StationRefitKinds_DoNotCollideWithAnyOtherFeature()
        {
            // Additive kinds in their own block: an older client granted nothing new simply drops
            // them, which is what lets this ship without a ProtocolVersion bump.
            Assert.Equal(208, (byte)WireMessageKind.StationRefitRequest);
            Assert.Equal(209, (byte)WireMessageKind.StationRefitSnapshot);
            Assert.Equal(210, (byte)WireMessageKind.StationRefitCommandRequest);
            Assert.Equal(211, (byte)WireMessageKind.StationRefitCommandResult);
            Assert.True((byte)WireMessageKind.StationRefitRequest
                        > (byte)WireMessageKind.CampaignCommandResult);
        }

        [Fact]
        public void StationRefitFeature_IsADistinctBitFromShipAssetsAndNodeDetail()
        {
            // Its own bit: this surface rebuilds the live station grid a player may be standing in,
            // so a server must be able to withhold it while still granting the window it opens from.
            byte[] without = FactionsWire.WriteUplinkGranted(
                "1.0", WireFeatures.NodeDetail | WireFeatures.ShipAssets);

            string serverVersion;
            WireFeatures features;
            Assert.True(FactionsWire.TryReadUplinkGranted(without, out serverVersion, out features));
            Assert.False((features & WireFeatures.StationRefit) != 0);

            byte[] with = FactionsWire.WriteUplinkGranted("1.0", WireFeatures.StationRefit);
            Assert.True(FactionsWire.TryReadUplinkGranted(with, out serverVersion, out features));
            Assert.True((features & WireFeatures.StationRefit) != 0);
            Assert.NotEqual(WireFeatures.ShipAssets, WireFeatures.StationRefit);
            Assert.NotEqual(WireFeatures.Campaigns, WireFeatures.StationRefit);
        }

        [Fact]
        public void ShipScanResult_RejectsATruncatedBufferAndAForeignKind()
        {
            byte[] data = FactionsWire.WriteShipScanResult(true, "Scanned.");
            var cut = new byte[6];
            System.Array.Copy(data, cut, cut.Length);

            bool ok;
            string message;
            Assert.False(FactionsWire.TryReadShipScanResult(cut, out ok, out message));

            // A different message's payload must not be decoded by this reader just because its
            // trailing fields happen to line up.
            Assert.False(FactionsWire.TryReadShipScanResult(
                FactionsWire.WriteShipSendResult(true, "P", "m"), out ok, out message));
        }

        // ---- Build button ---------------------------------------------------------------------

        private static WireBuildPrefab SampleBuildPrefab()
        {
            return new WireBuildPrefab
            {
                PrefabId = "C21_Trade_ConsignmentDrone",
                Label = "Consignment Drone",
                Roles = new[] { "Trade", "Mine" },
                CargoLiters = 42000,
                Dps = 137.5,
                GridSize = "Large",
                Propulsion = "atmo+hydro",
                DrillYieldScore = 2.25,
                BomUnits = 1240,
                CanFlyFromHere = true,
            };
        }

        [Fact]
        public void BuildOptionsRequest_RoundTrips()
        {
            byte[] data = FactionsWire.WriteBuildOptionsRequest("N8466");

            string nodeId;
            Assert.True(FactionsWire.TryReadBuildOptionsRequest(data, out nodeId));
            Assert.Equal("N8466", nodeId);
        }

        [Fact]
        public void BuildOptions_RoundTripsEveryFieldOfEveryHull()
        {
            WireBuildPrefab sent = SampleBuildPrefab();
            var second = new WireBuildPrefab
            {
                PrefabId = "Nagamaki",
                Label = "",
                Roles = new[] { "Raid" },
                CargoLiters = 0,
                Dps = 0,
                GridSize = "Small",
                Propulsion = "none",
                DrillYieldScore = 0,
                BomUnits = 0,
                CanFlyFromHere = false,
            };
            byte[] data = FactionsWire.WriteBuildOptions(
                "N8466", new[] { "Trade", "Raid", "Patrol", "Mine" },
                new List<WireBuildPrefab> { sent, second });

            string nodeId;
            List<string> roles;
            List<WireBuildPrefab> prefabs;
            Assert.True(FactionsWire.TryReadBuildOptions(data, out nodeId, out roles, out prefabs));
            Assert.Equal("N8466", nodeId);
            Assert.Equal(new[] { "Trade", "Raid", "Patrol", "Mine" }, roles.ToArray());
            Assert.Equal(2, prefabs.Count);

            WireBuildPrefab read = prefabs[0];
            Assert.Equal(sent.PrefabId, read.PrefabId);
            Assert.Equal(sent.Label, read.Label);
            Assert.Equal(sent.Roles, read.Roles);
            Assert.Equal(sent.CargoLiters, read.CargoLiters);
            Assert.Equal(sent.Dps, read.Dps);
            Assert.Equal(sent.GridSize, read.GridSize);
            Assert.Equal(sent.Propulsion, read.Propulsion);
            Assert.Equal(sent.DrillYieldScore, read.DrillYieldScore);
            Assert.Equal(sent.BomUnits, read.BomUnits);
            Assert.True(read.CanFlyFromHere);

            // The per-hull role list is a variable-length block inside a repeated one, which is
            // exactly the shape that reads the next hull's id out of the previous hull's roles when
            // the two ends disagree. The second row proves the cursor landed where it should.
            Assert.Equal("Nagamaki", prefabs[1].PrefabId);
            Assert.Equal(new[] { "Raid" }, prefabs[1].Roles);
            Assert.False(prefabs[1].CanFlyFromHere);
        }

        [Fact]
        public void BuildOptions_WithNoHulls_IsAReadableAnswer()
        {
            byte[] data = FactionsWire.WriteBuildOptions("N1", new string[0], new List<WireBuildPrefab>());

            string nodeId;
            List<string> roles;
            List<WireBuildPrefab> prefabs;
            Assert.True(FactionsWire.TryReadBuildOptions(data, out nodeId, out roles, out prefabs));
            Assert.Equal("N1", nodeId);
            Assert.Empty(roles);
            // "This yard can build nothing you may queue" must arrive as an empty list, not as an
            // unreadable payload the dialog would render as "waiting for the server" forever.
            Assert.Empty(prefabs);
        }

        [Fact]
        public void BuildOptions_TrimsToTheCap_RatherThanSendingAnUnboundedList()
        {
            var many = new List<WireBuildPrefab>();
            for (int i = 0; i < 600; i++)
            {
                WireBuildPrefab p = SampleBuildPrefab();
                p.PrefabId = "Hull" + i;
                many.Add(p);
            }
            byte[] data = FactionsWire.WriteBuildOptions("N1", new[] { "Trade" }, many);

            string nodeId;
            List<string> roles;
            List<WireBuildPrefab> prefabs;
            Assert.True(FactionsWire.TryReadBuildOptions(data, out nodeId, out roles, out prefabs));
            Assert.Equal(512, prefabs.Count);
        }

        [Fact]
        public void BuildRequest_RoundTrips()
        {
            byte[] data = FactionsWire.WriteBuildRequest("N8466", "C21_Trade_ConsignmentDrone");

            string nodeId, prefabId;
            Assert.True(FactionsWire.TryReadBuildRequest(data, out nodeId, out prefabId));
            Assert.Equal("N8466", nodeId);
            Assert.Equal("C21_Trade_ConsignmentDrone", prefabId);
        }

        [Fact]
        public void BuildResult_RoundTrips()
        {
            byte[] data = FactionsWire.WriteBuildResult(true, "Queued C21_Trade_ConsignmentDrone (Trade) at N8466.");

            bool ok;
            string message;
            Assert.True(FactionsWire.TryReadBuildResult(data, out ok, out message));
            Assert.True(ok);
            Assert.Equal("Queued C21_Trade_ConsignmentDrone (Trade) at N8466.", message);
        }

        [Fact]
        public void BuildMessages_ReadAsTheWrongKind_Fail()
        {
            byte[] request = FactionsWire.WriteBuildOptionsRequest("N1");
            byte[] options = FactionsWire.WriteBuildOptions(
                "N1", new[] { "Trade" }, new List<WireBuildPrefab> { SampleBuildPrefab() });
            byte[] build = FactionsWire.WriteBuildRequest("N1", "Hull");
            byte[] result = FactionsWire.WriteBuildResult(true, "queued");

            string nodeId, prefabId;
            List<string> roles;
            List<WireBuildPrefab> prefabs;
            bool ok;
            string message;

            Assert.False(FactionsWire.TryReadBuildOptionsRequest(options, out nodeId));
            Assert.False(FactionsWire.TryReadBuildOptions(request, out nodeId, out roles, out prefabs));
            Assert.False(FactionsWire.TryReadBuildRequest(options, out nodeId, out prefabId));
            Assert.False(FactionsWire.TryReadBuildResult(build, out ok, out message));

            // Neighbouring kinds with the same trailing fields must not decode each other. The node
            // command result and the refit command result are both (bool, string), and the refit
            // request and the build options request are both (string).
            Assert.False(FactionsWire.TryReadBuildResult(
                FactionsWire.WriteNodeCommandResult(true, "queued"), out ok, out message));
            Assert.False(FactionsWire.TryReadStationRefitCommandResult(result, out ok, out message));
            Assert.False(FactionsWire.TryReadBuildOptionsRequest(
                FactionsWire.WriteStationRefitRequest("N1"), out nodeId));
        }

        [Fact]
        public void BuildKinds_DoNotCollideWithAnyOtherFeature()
        {
            Assert.Equal(212, (byte)WireMessageKind.BuildOptionsRequest);
            Assert.Equal(213, (byte)WireMessageKind.BuildOptions);
            Assert.Equal(214, (byte)WireMessageKind.BuildRequest);
            Assert.Equal(215, (byte)WireMessageKind.BuildResult);
            Assert.True((byte)WireMessageKind.BuildOptionsRequest
                        > (byte)WireMessageKind.StationRefitCommandResult);
        }

        [Fact]
        public void BuildFeature_IsADistinctBitFromNodeDetailAndStationRefit()
        {
            // Its own bit: this surface spends a station's components, so a server must be able to
            // withhold it while still granting the window the button sits on.
            byte[] without = FactionsWire.WriteUplinkGranted(
                "1.0", WireFeatures.NodeDetail | WireFeatures.StationRefit);

            string serverVersion;
            WireFeatures features;
            Assert.True(FactionsWire.TryReadUplinkGranted(without, out serverVersion, out features));
            Assert.False((features & WireFeatures.Build) != 0);

            byte[] with = FactionsWire.WriteUplinkGranted("1.0", WireFeatures.Build);
            Assert.True(FactionsWire.TryReadUplinkGranted(with, out serverVersion, out features));
            Assert.True((features & WireFeatures.Build) != 0);
            Assert.NotEqual(WireFeatures.NodeDetail, WireFeatures.Build);
            Assert.NotEqual(WireFeatures.StationRefit, WireFeatures.Build);
        }

        // ---- Fleet doctrine --------------------------------------------------------------------

        private static WireDoctrineLine SampleDoctrineLine()
        {
            return new WireDoctrineLine
            {
                Id = "FD-001",
                Priority = 10,
                Paused = true,
                Fits = "Trade",
                PrefabId = "C21_Trade_ConsignmentDrone",
                TargetCount = 6,
                MaxConcurrent = 2,
                ScopeNodeId = "N600",
                ScopeNodeName = "Kirkwood Yard",
                Have = 4,
                Flying = 2,
                Building = 1,
                Pressure = 3,
                RouteBlocked = true,
                OverCeiling = true,
                Banned = true,
            };
        }

        [Fact]
        public void FleetDoctrineRequest_RoundTrips()
        {
            bool stream;
            Assert.True(FactionsWire.TryReadFleetDoctrineRequest(
                FactionsWire.WriteFleetDoctrineRequest(true), out stream));
            Assert.True(stream);

            Assert.True(FactionsWire.TryReadFleetDoctrineRequest(
                FactionsWire.WriteFleetDoctrineRequest(false), out stream));
            Assert.False(stream);
        }

        // ---- The store shelf --------------------------------------------------------------------

        [Fact]
        public void PlayerShelfRequest_RoundTrips()
        {
            bool stream;
            Assert.True(FactionsWire.TryReadPlayerShelfRequest(
                FactionsWire.WritePlayerShelfRequest(true), out stream));
            Assert.True(stream);
            Assert.True(FactionsWire.TryReadPlayerShelfRequest(
                FactionsWire.WritePlayerShelfRequest(false), out stream));
            Assert.False(stream);
        }

        [Fact]
        public void PlayerShelfSnapshot_RoundTripsEveryFieldOfEveryList()
        {
            var lines = new List<WireShelfLine>
            {
                new WireShelfLine
                {
                    Id = "SH-001", Paused = true, PrefabId = "Clipper", TargetCount = 4,
                    OnSale = 2, Building = 1, Role = "Trade", BlockedReason = "route",
                },
                // The all-default row beside the all-set one: a writer that skipped a field would
                // pass with one of these and fail with the pair.
                new WireShelfLine
                {
                    Id = "SH-002", Paused = false, PrefabId = "Tanto", TargetCount = 1,
                    OnSale = 0, Building = 0, Role = "", BlockedReason = "",
                },
            };
            var census = new List<WireShelfCensusRow>
            {
                new WireShelfCensusRow { PrefabId = "Smidr-Space", OnSale = 3, Unlined = true },
                new WireShelfCensusRow { PrefabId = "Clipper", OnSale = 2, Unlined = false },
            };

            byte[] data = FactionsWire.WritePlayerShelfSnapshot("EXP", 32, true, lines, census);

            string tag;
            int cap;
            bool off;
            List<WireShelfLine> readLines;
            List<WireShelfCensusRow> readCensus;
            Assert.True(FactionsWire.TryReadPlayerShelfSnapshot(
                data, out tag, out cap, out off, out readLines, out readCensus));

            Assert.Equal("EXP", tag);
            Assert.Equal(32, cap);
            Assert.True(off);

            Assert.Equal(2, readLines.Count);
            Assert.Equal("SH-001", readLines[0].Id);
            Assert.True(readLines[0].Paused);
            Assert.Equal("Clipper", readLines[0].PrefabId);
            Assert.Equal(4, readLines[0].TargetCount);
            Assert.Equal(2, readLines[0].OnSale);
            Assert.Equal(1, readLines[0].Building);
            Assert.Equal("Trade", readLines[0].Role);
            Assert.Equal("route", readLines[0].BlockedReason);
            Assert.Equal("SH-002", readLines[1].Id);
            Assert.False(readLines[1].Paused);
            Assert.Equal(0, readLines[1].OnSale);
            Assert.Equal("", readLines[1].BlockedReason);

            Assert.Equal(2, readCensus.Count);
            Assert.Equal("Smidr-Space", readCensus[0].PrefabId);
            Assert.Equal(3, readCensus[0].OnSale);
            // The flag the whole window exists for: a hull on sale that no line named.
            Assert.True(readCensus[0].Unlined);
            Assert.Equal("Clipper", readCensus[1].PrefabId);
            Assert.False(readCensus[1].Unlined);
        }

        [Fact]
        public void PlayerShelfCommandRequest_RoundTripsEveryCommand()
        {
            foreach (WireShelfCommand command in Enum.GetValues(typeof(WireShelfCommand)))
            {
                string lineId;
                WireShelfCommand read;
                int value;
                Assert.True(FactionsWire.TryReadPlayerShelfCommandRequest(
                    FactionsWire.WritePlayerShelfCommandRequest("SH-007", command, 5),
                    out lineId, out read, out value));
                Assert.Equal("SH-007", lineId);
                Assert.Equal(command, read);
                Assert.Equal(5, value);
            }
        }

        [Fact]
        public void PlayerShelfCommandResult_RoundTrips()
        {
            bool ok;
            string message;
            Assert.True(FactionsWire.TryReadPlayerShelfCommandResult(
                FactionsWire.WritePlayerShelfCommandResult(true, "Shelf line SH-001 added."),
                out ok, out message));
            Assert.True(ok);
            Assert.Equal("Shelf line SH-001 added.", message);
        }

        [Fact]
        public void PlayerShelfCreateRequest_RoundTrips()
        {
            string prefabId;
            int targetCount;
            Assert.True(FactionsWire.TryReadPlayerShelfCreateRequest(
                FactionsWire.WritePlayerShelfCreateRequest("Clipper", 6), out prefabId, out targetCount));
            Assert.Equal("Clipper", prefabId);
            Assert.Equal(6, targetCount);
        }

        [Fact]
        public void BankSalesSnapshot_RoundTrips()
        {
            var rows = new List<WireBankSalesRow>
            {
                new WireBankSalesRow { Key = "Comp:SteelPlate", Name = "Steel Plate", Category = 0,
                                       Held = 12345678901L, UnitValue = 21, Mode = 1, Target = 9876543210L },
                // The all-default row beside the all-set one: a writer that skipped a field would
                // pass with one of these and fail with the pair.
                new WireBankSalesRow { Key = "Comp:NATO_25x184mm", Name = "", Category = 2,
                                       Held = 0, UnitValue = 0, Mode = 0, Target = 0 },
            };
            string tag;
            List<WireBankSalesRow> read;
            Assert.True(FactionsWire.TryReadBankSalesSnapshot(
                FactionsWire.WriteBankSalesSnapshot("EXP", rows), out tag, out read));

            Assert.Equal("EXP", tag);
            Assert.Equal(2, read.Count);
            Assert.Equal("Comp:SteelPlate", read[0].Key);
            Assert.Equal("Steel Plate", read[0].Name);
            Assert.Equal(0, read[0].Category);
            Assert.Equal(12345678901L, read[0].Held);
            Assert.Equal(21, read[0].UnitValue);
            Assert.Equal(1, read[0].Mode);
            Assert.Equal(9876543210L, read[0].Target);
            Assert.Equal("Comp:NATO_25x184mm", read[1].Key);
            Assert.Equal("", read[1].Name);
            Assert.Equal(2, read[1].Category);
            Assert.Equal(0, read[1].Held);
            Assert.Equal(0, read[1].Mode);
            Assert.Equal(0, read[1].Target);
        }

        [Fact]
        public void BankSalesCommand_RoundTrips_AndAnOversizedOneIsRefused()
        {
            byte mode;
            long target;
            List<string> keys;
            Assert.True(FactionsWire.TryReadBankSalesCommandRequest(
                FactionsWire.WriteBankSalesCommandRequest(1, 4000000000L, new[] { "Comp:Motor", "Comp:SteelPlate" }),
                out mode, out target, out keys));
            Assert.Equal(1, mode);
            Assert.Equal(4000000000L, target);
            Assert.Equal(new[] { "Comp:Motor", "Comp:SteelPlate" }, keys);

            var tooMany = new List<string>();
            for (int i = 0; i <= FactionsWire.MaxBankSalesRows; i++) tooMany.Add("Comp:X" + i);
            Assert.False(FactionsWire.TryReadBankSalesCommandRequest(
                FactionsWire.WriteBankSalesCommandRequest(2, 0, tooMany), out mode, out target, out keys));

            bool stream;
            Assert.True(FactionsWire.TryReadBankSalesRequest(FactionsWire.WriteBankSalesRequest(true), out stream));
            Assert.True(stream);

            bool ok;
            string message;
            Assert.True(FactionsWire.TryReadBankSalesCommandResult(
                FactionsWire.WriteBankSalesCommandResult(true, "Kept."), out ok, out message));
            Assert.True(ok);
            Assert.Equal("Kept.", message);
        }

        [Fact]
        public void AShelfMessage_IsNotReadableAsADoctrineMessage()
        {
            // The kinds share a codec shape, and the header is the only thing separating them. A
            // reader that ignored it would decode a shelf snapshot as a doctrine one and show a
            // player their shop as their fleet.
            byte[] shelf = FactionsWire.WritePlayerShelfSnapshot("EXP", 32, false,
                new List<WireShelfLine>(), new List<WireShelfCensusRow>());

            string tag;
            int cap, revision;
            List<WireDoctrineLine> lines;
            List<WireDoctrinePressureRow> unlined;
            List<WireFleetCensusRow> census;
            Assert.False(FactionsWire.TryReadFleetDoctrineSnapshot(
                shelf, out tag, out cap, out revision, out lines, out unlined, out census));
        }

        [Fact]
        public void Header_CarriesATwoByteKind_AndRefusesTheOneByteForm()
        {
            byte[] request = FactionsWire.WriteUplinkRequest("test");
            WireMessageKind kind;
            int version;
            Assert.True(FactionsWire.TryReadHeader(request, out kind, out version));
            Assert.Equal(WireMessageKind.UplinkRequest, kind);
            Assert.Equal(FactionsWire.ProtocolVersion, version);

            // A kind past the old byte range survives the header.
            byte[] wide = new byte[6];
            BitConverter.GetBytes((ushort)300).CopyTo(wide, 0);
            BitConverter.GetBytes(FactionsWire.ProtocolVersion).CopyTo(wide, 2);
            Assert.True(FactionsWire.TryReadHeader(wide, out kind, out version));
            Assert.Equal(300, (int)kind);
            Assert.Equal(FactionsWire.ProtocolVersion, version);

            // A client from before the change sends a one-byte kind: five bytes of header, refused.
            Assert.False(FactionsWire.TryReadHeader(new byte[] { 1, 40, 0, 0, 0 }, out kind, out version));
        }

        [Fact]
        public void FleetDoctrineSnapshot_CarriesRefitRows_AndAnOlderServersHasNone()
        {
            var refits = new List<WireHullRefitRow>
            {
                new WireHullRefitRow { Family = "Naginata", Latest = "Naginata@2", Cap = 2, InRefit = 1,
                                       OutdatedParked = 5, OutdatedAtYards = 3 },
                new WireHullRefitRow { Family = "Test", Latest = "Test@2", Blocked = "banned" },
            };
            var census = new List<WireFleetCensusRow> { new WireFleetCensusRow { PrefabId = "Tanto", Parked = 4 } };
            byte[] data = FactionsWire.WriteFleetDoctrineSnapshot("EXP", 32, 3, new List<WireDoctrineLine>(),
                new List<WireDoctrinePressureRow>(), census, refits);

            string tag;
            int cap, revision;
            List<WireDoctrineLine> lines;
            List<WireDoctrinePressureRow> unlined;
            List<WireFleetCensusRow> readCensus;
            List<WireHullRefitRow> readRefits;
            Assert.True(FactionsWire.TryReadFleetDoctrineSnapshot(data, out tag, out cap, out revision,
                out lines, out unlined, out readCensus, out readRefits));
            Assert.Equal(2, readRefits.Count);
            Assert.Equal("Naginata", readRefits[0].Family);
            Assert.Equal("Naginata@2", readRefits[0].Latest);
            Assert.Equal(2, readRefits[0].Cap);
            Assert.Equal(1, readRefits[0].InRefit);
            Assert.Equal(5, readRefits[0].OutdatedParked);
            Assert.Equal(3, readRefits[0].OutdatedAtYards);
            Assert.Equal("Test@2", readRefits[1].Latest);
            Assert.Equal("", readRefits[0].Blocked);
            Assert.Equal("banned", readRefits[1].Blocked);
            Assert.Equal("Tanto", Assert.Single(readCensus).PrefabId);

            // A server that predates the rows ends the message after the census: the four-byte count
            // is all the tail there is with no rows, so cutting it off reproduces that server.
            byte[] older = new byte[FactionsWire.WriteFleetDoctrineSnapshot("EXP", 32, 3,
                new List<WireDoctrineLine>(), new List<WireDoctrinePressureRow>(), census).Length - 4];
            Array.Copy(FactionsWire.WriteFleetDoctrineSnapshot("EXP", 32, 3, new List<WireDoctrineLine>(),
                new List<WireDoctrinePressureRow>(), census), older, older.Length);
            Assert.True(FactionsWire.TryReadFleetDoctrineSnapshot(older, out tag, out cap, out revision,
                out lines, out unlined, out readCensus, out readRefits));
            Assert.Empty(readRefits);
            Assert.Equal("Tanto", Assert.Single(readCensus).PrefabId);
        }

        [Fact]
        public void DesignVersionQueryAndInfo_RoundTrip()
        {
            long terminal, grid;
            bool station;
            Assert.True(FactionsWire.TryReadDesignVersionQuery(
                FactionsWire.WriteDesignVersionQuery(11L, 22L, false), out terminal, out grid, out station));
            Assert.Equal(11L, terminal);
            Assert.Equal(22L, grid);
            Assert.False(station);

            var sent = new WireDesignVersionInfo
            {
                EntityId = 22L, Station = false, Offered = true, Family = "Naginata", CurrentId = "Naginata@2",
                NextId = "Naginata@3", ScopeAll = false, ScopeLabel = "[EXP] only", Reason = "",
            };
            WireDesignVersionInfo read;
            Assert.True(FactionsWire.TryReadDesignVersionInfo(FactionsWire.WriteDesignVersionInfo(sent), out read));
            Assert.Equal(sent.EntityId, read.EntityId);
            Assert.True(read.Offered);
            Assert.Equal("Naginata", read.Family);
            Assert.Equal("Naginata@2", read.CurrentId);
            Assert.Equal("Naginata@3", read.NextId);
            Assert.False(read.ScopeAll);
            Assert.Equal("[EXP] only", read.ScopeLabel);
            Assert.False(FactionsWire.TryReadDesignVersionQuery(FactionsWire.WriteDesignVersionInfo(sent),
                out terminal, out grid, out station));
        }

        [Fact]
        public void FleetDoctrineSnapshot_RoundTripsEveryFieldOfEveryList()
        {
            WireDoctrineLine sent = SampleDoctrineLine();
            var second = new WireDoctrineLine
            {
                Id = "FD-002",
                Priority = 0,
                Paused = false,
                Fits = "Mine",
                PrefabId = "C11_Mining_Puller",
                TargetCount = 4,
                MaxConcurrent = 1,
                ScopeNodeId = "",
                ScopeNodeName = "",
                Have = 0,
                Flying = 0,
                Building = 0,
                Pressure = 0,
                RouteBlocked = false,
            };
            var unlined = new List<WireDoctrinePressureRow>
            {
                new WireDoctrinePressureRow { Role = "Raid", NodeId = "N612", NodeName = "Far Post", Demands = 3 },
                new WireDoctrinePressureRow { Role = "Trade", NodeId = "N246", NodeName = "", Demands = 1 },
            };
            var census = new List<WireFleetCensusRow>
            {
                new WireFleetCensusRow { PrefabId = "DS_Miner_DrifterDrone", Parked = 4, Flying = 0 },
                new WireFleetCensusRow { PrefabId = "Nagamaki", Parked = 1, Flying = 9 },
            };

            byte[] data = FactionsWire.WriteFleetDoctrineSnapshot(
                "EXP", 32, 17, new List<WireDoctrineLine> { sent, second }, unlined, census);

            string tag;
            int cap, revision;
            List<WireDoctrineLine> lines;
            List<WireDoctrinePressureRow> readUnlined;
            List<WireFleetCensusRow> readCensus;
            Assert.True(FactionsWire.TryReadFleetDoctrineSnapshot(
                data, out tag, out cap, out revision, out lines, out readUnlined, out readCensus));

            Assert.Equal("EXP", tag);
            Assert.Equal(32, cap);
            Assert.Equal(17, revision);
            Assert.Equal(2, lines.Count);

            WireDoctrineLine read = lines[0];
            Assert.Equal(sent.Id, read.Id);
            Assert.Equal(sent.Priority, read.Priority);
            Assert.True(read.Paused);
            Assert.Equal(sent.Fits, read.Fits);
            Assert.Equal(sent.Banned, read.Banned);
            Assert.Equal(sent.PrefabId, read.PrefabId);
            Assert.Equal(sent.TargetCount, read.TargetCount);
            Assert.Equal(sent.MaxConcurrent, read.MaxConcurrent);
            Assert.Equal(sent.ScopeNodeId, read.ScopeNodeId);
            Assert.Equal(sent.ScopeNodeName, read.ScopeNodeName);
            Assert.Equal(sent.Have, read.Have);
            Assert.Equal(sent.Flying, read.Flying);
            Assert.Equal(sent.Building, read.Building);
            Assert.Equal(sent.Pressure, read.Pressure);
            Assert.True(read.RouteBlocked);
            Assert.True(read.OverCeiling);
            Assert.True(read.Banned);

            // Three variable-length lists in one message: the second row of each is what proves the
            // cursor landed where it should rather than inside its predecessor.
            Assert.Equal("FD-002", lines[1].Id);
            Assert.False(lines[1].RouteBlocked);
            Assert.False(lines[1].OverCeiling);
            Assert.False(lines[1].Banned);

            Assert.Equal(2, readUnlined.Count);
            Assert.Equal("Raid", readUnlined[0].Role);
            Assert.Equal("N612", readUnlined[0].NodeId);
            Assert.Equal("Far Post", readUnlined[0].NodeName);
            Assert.Equal(3, readUnlined[0].Demands);
            Assert.Equal("N246", readUnlined[1].NodeId);
            Assert.Equal("", readUnlined[1].NodeName);

            Assert.Equal(2, readCensus.Count);
            Assert.Equal("DS_Miner_DrifterDrone", readCensus[0].PrefabId);
            Assert.Equal(4, readCensus[0].Parked);
            Assert.Equal(0, readCensus[0].Flying);
            Assert.Equal("Nagamaki", readCensus[1].PrefabId);
            Assert.Equal(9, readCensus[1].Flying);
        }

        [Fact]
        public void FleetDoctrineSnapshot_WithNothingAtAll_IsAReadableAnswer()
        {
            byte[] data = FactionsWire.WriteFleetDoctrineSnapshot("", 32, 0, null, null, null);

            string tag;
            int cap, revision;
            List<WireDoctrineLine> lines;
            List<WireDoctrinePressureRow> unlined;
            List<WireFleetCensusRow> census;
            Assert.True(FactionsWire.TryReadFleetDoctrineSnapshot(
                data, out tag, out cap, out revision, out lines, out unlined, out census));
            // "You have no doctrine and own nothing" must arrive as three empty lists, not as an
            // unreadable payload the window would render as "waiting for the server" forever.
            Assert.Equal("", tag);
            Assert.Empty(lines);
            Assert.Empty(unlined);
            Assert.Empty(census);
        }

        [Fact]
        public void FleetDoctrineSnapshot_TrimsEachListToItsOwnCap()
        {
            var lines = new List<WireDoctrineLine>();
            for (int i = 0; i < 400; i++)
            {
                WireDoctrineLine l = SampleDoctrineLine();
                l.Id = "FD-" + i;
                lines.Add(l);
            }
            var unlined = new List<WireDoctrinePressureRow>();
            for (int i = 0; i < 700; i++)
                unlined.Add(new WireDoctrinePressureRow { Role = "Trade", NodeId = "N" + i, Demands = 1 });
            var census = new List<WireFleetCensusRow>();
            for (int i = 0; i < 700; i++)
                census.Add(new WireFleetCensusRow { PrefabId = "Hull" + i, Parked = 1, Flying = 0 });

            byte[] data = FactionsWire.WriteFleetDoctrineSnapshot("EXP", 32, 1, lines, unlined, census);

            string tag;
            int cap, revision;
            List<WireDoctrineLine> readLines;
            List<WireDoctrinePressureRow> readUnlined;
            List<WireFleetCensusRow> readCensus;
            Assert.True(FactionsWire.TryReadFleetDoctrineSnapshot(
                data, out tag, out cap, out revision, out readLines, out readUnlined, out readCensus));
            Assert.Equal(256, readLines.Count);
            Assert.Equal(512, readUnlined.Count);
            Assert.Equal(512, readCensus.Count);
        }

        [Fact]
        public void FleetDoctrineCommandRequest_RoundTripsEveryCommand()
        {
            foreach (WireDoctrineCommand command in Enum.GetValues(typeof(WireDoctrineCommand)))
            {
                byte[] data = FactionsWire.WriteFleetDoctrineCommandRequest("FD-003", command, 7);

                string lineId;
                WireDoctrineCommand read;
                int value;
                Assert.True(FactionsWire.TryReadFleetDoctrineCommandRequest(data, out lineId, out read, out value));
                Assert.Equal("FD-003", lineId);
                Assert.Equal(command, read);
                Assert.Equal(7, value);
            }
        }

        [Fact]
        public void FleetDoctrineCommandResult_RoundTrips()
        {
            byte[] data = FactionsWire.WriteFleetDoctrineCommandResult(false, "No doctrine line FD-009.");

            bool ok;
            string message;
            Assert.True(FactionsWire.TryReadFleetDoctrineCommandResult(data, out ok, out message));
            Assert.False(ok);
            Assert.Equal("No doctrine line FD-009.", message);
        }

        [Fact]
        public void FleetDoctrineOptions_RoundTripsRolesScopesAndHulls()
        {
            var roles = new List<string> { "Trade", "Raid", "Patrol", "Mine" };
            var nodes = new List<WireOrderOption>
            {
                new WireOrderOption { Id = "N600", Label = "Kirkwood Yard", Flags = 2 },
                new WireOrderOption { Id = "N601", Label = "", Flags = 2 },
            };
            var prefabs = new List<WireBuildPrefab> { SampleBuildPrefab() };

            byte[] data = FactionsWire.WriteFleetDoctrineOptions(roles, nodes, prefabs);

            List<string> readRoles;
            List<WireOrderOption> readNodes;
            List<WireBuildPrefab> readPrefabs;
            Assert.True(FactionsWire.TryReadFleetDoctrineOptions(data, out readRoles, out readNodes, out readPrefabs));
            Assert.Equal(roles.ToArray(), readRoles.ToArray());
            Assert.Equal(2, readNodes.Count);
            Assert.Equal("N600", readNodes[0].Id);
            Assert.Equal("Kirkwood Yard", readNodes[0].Label);
            Assert.Equal(2, readNodes[0].Flags);
            Assert.Equal("", readNodes[1].Label);

            // The same struct and the same reader the Build dialog's list uses, so a hull cannot
            // read differently in the two dialogs.
            WireBuildPrefab hull = Assert.Single(readPrefabs);
            Assert.Equal("C21_Trade_ConsignmentDrone", hull.PrefabId);
            Assert.Equal(new[] { "Trade", "Mine" }, hull.Roles);
            Assert.Equal(42000, hull.CargoLiters);
            Assert.Equal(137.5, hull.Dps);
            Assert.Equal("atmo+hydro", hull.Propulsion);
            Assert.Equal(2.25, hull.DrillYieldScore);
            Assert.Equal(1240, hull.BomUnits);
            Assert.True(hull.CanFlyFromHere);
        }

        [Fact]
        public void FleetDoctrineOptions_TrimsTheHullListToTheCap()
        {
            var many = new List<WireBuildPrefab>();
            for (int i = 0; i < 600; i++)
            {
                WireBuildPrefab p = SampleBuildPrefab();
                p.PrefabId = "Hull" + i;
                many.Add(p);
            }
            byte[] data = FactionsWire.WriteFleetDoctrineOptions(
                new List<string> { "Trade" }, new List<WireOrderOption>(), many);

            List<string> roles;
            List<WireOrderOption> nodes;
            List<WireBuildPrefab> prefabs;
            Assert.True(FactionsWire.TryReadFleetDoctrineOptions(data, out roles, out nodes, out prefabs));
            Assert.Equal(512, prefabs.Count);
        }

        [Fact]
        public void FleetDoctrineCreateRequest_RoundTrips()
        {
            byte[] scoped = FactionsWire.WriteFleetDoctrineCreateRequest(
                "C21_Trade_ConsignmentDrone", 6, 2, "N600");

            string prefabId, scopeNodeId;
            int targetCount, maxConcurrent;
            Assert.True(FactionsWire.TryReadFleetDoctrineCreateRequest(
                scoped, out prefabId, out targetCount, out maxConcurrent, out scopeNodeId));
            Assert.Equal("C21_Trade_ConsignmentDrone", prefabId);
            Assert.Equal(6, targetCount);
            Assert.Equal(2, maxConcurrent);
            Assert.Equal("N600", scopeNodeId);

            // "Any yard" travels as an empty scope, which the server reads as unscoped.
            byte[] unscoped = FactionsWire.WriteFleetDoctrineCreateRequest("Puller", 4, 1, null);
            Assert.True(FactionsWire.TryReadFleetDoctrineCreateRequest(
                unscoped, out prefabId, out targetCount, out maxConcurrent, out scopeNodeId));
            Assert.Equal("", scopeNodeId);
        }

        [Fact]
        public void FleetDoctrineMessages_ReadAsTheWrongKind_Fail()
        {
            byte[] request = FactionsWire.WriteFleetDoctrineRequest(true);
            byte[] snapshot = FactionsWire.WriteFleetDoctrineSnapshot(
                "EXP", 32, 1, new List<WireDoctrineLine> { SampleDoctrineLine() }, null, null);
            byte[] command = FactionsWire.WriteFleetDoctrineCommandRequest("FD-001", WireDoctrineCommand.Remove, 0);
            byte[] result = FactionsWire.WriteFleetDoctrineCommandResult(true, "ok");
            byte[] options = FactionsWire.WriteFleetDoctrineOptions(
                new List<string> { "Trade" }, new List<WireOrderOption>(), new List<WireBuildPrefab>());
            byte[] create = FactionsWire.WriteFleetDoctrineCreateRequest("Hull", 1, 1, null);

            bool stream, ok;
            string tag, message, lineId, prefabId, scopeNodeId;
            int cap, revision, value, targetCount, maxConcurrent;
            List<WireDoctrineLine> lines;
            List<WireDoctrinePressureRow> unlined;
            List<WireFleetCensusRow> census;
            List<string> roles;
            List<WireOrderOption> nodes;
            List<WireBuildPrefab> prefabs;
            WireDoctrineCommand readCommand;

            Assert.False(FactionsWire.TryReadFleetDoctrineRequest(snapshot, out stream));
            Assert.False(FactionsWire.TryReadFleetDoctrineSnapshot(
                request, out tag, out cap, out revision, out lines, out unlined, out census));
            Assert.False(FactionsWire.TryReadFleetDoctrineCommandRequest(result, out lineId, out readCommand, out value));
            Assert.False(FactionsWire.TryReadFleetDoctrineCommandResult(command, out ok, out message));
            Assert.False(FactionsWire.TryReadFleetDoctrineOptions(create, out roles, out nodes, out prefabs));
            Assert.False(FactionsWire.TryReadFleetDoctrineCreateRequest(
                options, out prefabId, out targetCount, out maxConcurrent, out scopeNodeId));

            // The Build dialog's options share this message's hull-list block. Sharing a block must
            // not make one message readable as the other.
            Assert.False(FactionsWire.TryReadBuildOptions(options, out tag, out roles, out prefabs));
            Assert.False(FactionsWire.TryReadFleetDoctrineOptions(
                FactionsWire.WriteBuildOptions("N1", new[] { "Trade" }, new List<WireBuildPrefab>()),
                out roles, out nodes, out prefabs));
        }

        [Fact]
        public void FleetDoctrineFeature_IsADistinctBitFromStandingOrders()
        {
            // Its own bit: the doctrine decides everything a faction builds, so a server must be
            // able to withhold it while still granting the Mine list beside it.
            byte[] without = FactionsWire.WriteUplinkGranted(
                "1.0", WireFeatures.StandingOrders | WireFeatures.Build);

            string serverVersion;
            WireFeatures features;
            Assert.True(FactionsWire.TryReadUplinkGranted(without, out serverVersion, out features));
            Assert.False((features & WireFeatures.FleetDoctrine) != 0);

            byte[] with = FactionsWire.WriteUplinkGranted("1.0", WireFeatures.FleetDoctrine);
            Assert.True(FactionsWire.TryReadUplinkGranted(with, out serverVersion, out features));
            Assert.True((features & WireFeatures.FleetDoctrine) != 0);
            Assert.NotEqual(WireFeatures.StandingOrders, WireFeatures.FleetDoctrine);
            Assert.NotEqual(WireFeatures.Build, WireFeatures.FleetDoctrine);
        }

        // ---- standing orders: Mine and Stock --------------------------------------------------

        [Fact]
        public void StandingOrdersSnapshot_RoundTripsEveryRemainingField()
        {
            var sent = new WireStandingOrder
            {
                Id = "SO-002",
                Kind = 0,
                Priority = 5,
                Paused = true,
                HubNodeId = "N246",
                HubNodeName = "Rockhaven",
                PeriodSeconds = 180.5,
                OreWeights = "Iron:50, Nickel:50",
                NextScanUtcTicks = 638000000000000000L,
            };
            var second = new WireStandingOrder
            {
                Id = "SO-003",
                Kind = 0,
                Priority = 0,
                Paused = false,
                HubNodeId = "N247",
                HubNodeName = "",
                PeriodSeconds = 0,
                OreWeights = "",
                NextScanUtcTicks = 0,
            };

            byte[] data = FactionsWire.WriteStandingOrdersSnapshot(
                "EXP", 32, new List<WireStandingOrder> { sent, second });

            string tag;
            int cap;
            List<WireStandingOrder> orders;
            Assert.True(FactionsWire.TryReadStandingOrdersSnapshot(data, out tag, out cap, out orders));
            Assert.Equal("EXP", tag);
            Assert.Equal(32, cap);
            Assert.Equal(2, orders.Count);

            WireStandingOrder read = orders[0];
            Assert.Equal(sent.Id, read.Id);
            Assert.Equal(0, read.Kind);
            Assert.Equal(sent.Priority, read.Priority);
            Assert.True(read.Paused);
            Assert.Equal(sent.HubNodeId, read.HubNodeId);
            Assert.Equal(sent.HubNodeName, read.HubNodeName);
            Assert.Equal(sent.PeriodSeconds, read.PeriodSeconds);
            Assert.Equal(sent.OreWeights, read.OreWeights);
            Assert.Equal(sent.NextScanUtcTicks, read.NextScanUtcTicks);

            // The second row is what proves the reader stops where the writer did rather than
            // reading into it.
            Assert.Equal("SO-003", orders[1].Id);
            Assert.Equal("N247", orders[1].HubNodeId);
        }

        [Fact]
        public void StandingOrdersSnapshot_RoundTripsAStockRow_BetweenMineRows()
        {
            var stock = new WireStandingOrder
            {
                Id = "SO-011",
                Kind = 1,
                Paused = true,
                HubNodeId = "N600",
                HubNodeName = "Hammerfall's Keep",
                ItemKey = "NATO_25x184mm",
                ItemName = "Gatling Ammo Box",
                TargetQuantity = 600,
                Have = 432,
            };
            var mine = new WireStandingOrder { Id = "SO-012", Kind = 0, HubNodeId = "N601", PeriodSeconds = 60 };

            byte[] data = FactionsWire.WriteStandingOrdersSnapshot(
                "EXP", 32, new List<WireStandingOrder> { stock, mine });

            string tag;
            int cap;
            List<WireStandingOrder> orders;
            Assert.True(FactionsWire.TryReadStandingOrdersSnapshot(data, out tag, out cap, out orders));
            WireStandingOrder read = orders[0];
            Assert.Equal(1, read.Kind);
            Assert.True(read.Paused);
            Assert.Equal("N600", read.HubNodeId);
            Assert.Equal("NATO_25x184mm", read.ItemKey);
            Assert.Equal("Gatling Ammo Box", read.ItemName);
            Assert.Equal(600L, read.TargetQuantity);
            Assert.Equal(432L, read.Have);
            Assert.Equal("SO-012", orders[1].Id);
            Assert.Equal(60.0, orders[1].PeriodSeconds);
            Assert.Equal("", orders[1].ItemKey);
        }

        [Fact]
        public void StandingOrdersSnapshot_RoundTripsAnAllStationsRow_BeforeAStationRow()
        {
            var all = new WireStandingOrder
            {
                Id = "SO-020",
                Kind = 1,
                ItemKey = "NATO_25x184mm",
                TargetQuantity = 200,
                AllStations = true,
                StationTarget = 250,
                StationIds = new[] { "N600", "N765" },
                StationHaves = new[] { 40L, 300L },
            };
            var station = new WireStandingOrder { Id = "SO-021", Kind = 1, HubNodeId = "N601", ItemKey = "Missile200mm", Have = 7 };

            byte[] data = FactionsWire.WriteStandingOrdersSnapshot(
                "EXP", 32, new List<WireStandingOrder> { all, station });

            string tag;
            int cap;
            List<WireStandingOrder> orders;
            Assert.True(FactionsWire.TryReadStandingOrdersSnapshot(data, out tag, out cap, out orders));
            WireStandingOrder read = orders[0];
            Assert.True(read.AllStations);
            Assert.Equal(250L, read.StationTarget);
            Assert.Equal(new[] { "N600", "N765" }, read.StationIds);
            Assert.Equal(new[] { 40L, 300L }, read.StationHaves);
            Assert.False(orders[1].AllStations);
            Assert.Null(orders[1].StationIds);
            Assert.Equal("N601", orders[1].HubNodeId);
            Assert.Equal(7L, orders[1].Have);
        }

        [Fact]
        public void StandingOrdersSnapshot_TrimsToTheCap()
        {
            var many = new List<WireStandingOrder>();
            for (int i = 0; i < 400; i++)
                many.Add(new WireStandingOrder { Id = "SO-" + i, HubNodeId = "N" + i });

            byte[] data = FactionsWire.WriteStandingOrdersSnapshot("EXP", 32, many);

            string tag;
            int cap;
            List<WireStandingOrder> orders;
            Assert.True(FactionsWire.TryReadStandingOrdersSnapshot(data, out tag, out cap, out orders));
            Assert.Equal(256, orders.Count);
        }

        [Fact]
        public void StandingOrderOptions_CarriesHubsOnly()
        {
            var nodes = new List<WireOrderOption>
            {
                new WireOrderOption { Id = "N246", Label = "Rockhaven", Flags = 1 },
                new WireOrderOption { Id = "N600", Label = "Kirkwood Yard", Flags = 3 },
            };

            byte[] data = FactionsWire.WriteStandingOrderOptions(nodes);

            List<WireOrderOption> readNodes;
            Assert.True(FactionsWire.TryReadStandingOrderOptions(data, out readNodes));
            Assert.Equal(2, readNodes.Count);
            Assert.Equal("N246", readNodes[0].Id);
            Assert.Equal("Rockhaven", readNodes[0].Label);
            Assert.Equal(1, readNodes[0].Flags);
            Assert.Equal(3, readNodes[1].Flags);
        }

        [Fact]
        public void StandingOrderCreateRequest_CarriesAMineOrder()
        {
            byte[] data = FactionsWire.WriteStandingOrderCreateRequest(0, "N246", 180.0, "Iron:50, Nickel:50");

            byte kind;
            string nodeId, oreWeights, itemKey;
            double periodSeconds;
            long quantity;
            Assert.True(FactionsWire.TryReadStandingOrderCreateRequest(
                data, out kind, out nodeId, out periodSeconds, out oreWeights, out itemKey, out quantity));
            Assert.Equal(0, kind);
            Assert.Equal("N246", nodeId);
            Assert.Equal(180.0, periodSeconds);
            Assert.Equal("Iron:50, Nickel:50", oreWeights);
            Assert.Equal("", itemKey);
            Assert.Equal(0L, quantity);
        }

        [Fact]
        public void StandingOrderCreateRequest_CarriesAStockOrder()
        {
            byte[] data = FactionsWire.WriteStandingOrderCreateRequest(1, "N600", 0, "", "NATO_25x184mm", 600);

            byte kind;
            string nodeId, oreWeights, itemKey;
            double periodSeconds;
            long quantity;
            Assert.True(FactionsWire.TryReadStandingOrderCreateRequest(
                data, out kind, out nodeId, out periodSeconds, out oreWeights, out itemKey, out quantity));
            Assert.Equal(1, kind);
            Assert.Equal("N600", nodeId);
            Assert.Equal("NATO_25x184mm", itemKey);
            Assert.Equal(600L, quantity);
        }

        [Fact]
        public void StockItemOptions_RoundTrip()
        {
            var sent = new List<WireStockItem>
            {
                new WireStockItem { Key = "NATO_25x184mm", Name = "Gatling Ammo Box", Category = "Ammo",
                                    Source = "you make it; IGSL holds it",
                                    Flags = (int)(WireStockItemFlags.OwnProduction | WireStockItemFlags.OtherStock) },
                new WireStockItem { Key = "Ingot:Iron", Name = "Iron Ingot", Category = "Ingot",
                                    Source = "no known source", Flags = 0 },
            };

            byte[] data = FactionsWire.WriteStockItemOptions(sent);

            List<WireStockItem> read;
            Assert.True(FactionsWire.TryReadStockItemOptions(data, out read));
            Assert.Equal(2, read.Count);
            Assert.Equal("NATO_25x184mm", read[0].Key);
            Assert.Equal("Gatling Ammo Box", read[0].Name);
            Assert.Equal("Ammo", read[0].Category);
            Assert.Equal("you make it; IGSL holds it", read[0].Source);
            Assert.Equal(5, read[0].Flags);
            Assert.Equal("Ingot:Iron", read[1].Key);
            Assert.False(FactionsWire.TryReadStandingOrderOptions(data, out _));
        }

        [Fact]
        public void StandingOrderMessages_ReadAsTheWrongKind_Fail()
        {
            byte[] request = FactionsWire.WriteStandingOrdersRequest(true);
            byte[] snapshot = FactionsWire.WriteStandingOrdersSnapshot(
                "EXP", 32, new List<WireStandingOrder> { new WireStandingOrder { Id = "SO-001" } });
            byte[] options = FactionsWire.WriteStandingOrderOptions(new List<WireOrderOption>());
            byte[] create = FactionsWire.WriteStandingOrderCreateRequest(0, "N1", 60, "");

            bool stream;
            string tag, nodeId, oreWeights, itemKey;
            int cap;
            byte kind;
            double periodSeconds;
            long quantity;
            List<WireStandingOrder> orders;
            List<WireOrderOption> nodes;

            Assert.False(FactionsWire.TryReadStandingOrdersRequest(snapshot, out stream));
            Assert.False(FactionsWire.TryReadStandingOrdersSnapshot(request, out tag, out cap, out orders));
            Assert.False(FactionsWire.TryReadStandingOrderOptions(create, out nodes));
            Assert.False(FactionsWire.TryReadStandingOrderCreateRequest(
                options, out kind, out nodeId, out periodSeconds, out oreWeights, out itemKey, out quantity));
        }

        [Fact]
        public void FleetDoctrineKinds_SitAfterTheBuildBlock()
        {
            // The block after 215, as the file's own convention asks. A kind reused from a retired
            // feature would be decoded by whichever handler happened to be registered.
            Assert.Equal(216, (byte)WireMessageKind.FleetDoctrineRequest);
            Assert.Equal(221, (byte)WireMessageKind.FleetDoctrineCreateRequest);
            Assert.True((byte)WireMessageKind.FleetDoctrineRequest > (byte)WireMessageKind.BuildResult);
        }
    }
}
