using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The server-to-server relay envelope. Both ends are Torch hosts built from the same source,
    /// but on a cluster they are separate deployments, so a layout drift shows up only as a client
    /// that links on its home server and never receives a snapshot.
    /// </summary>
    public class NexusRelayWireTests
    {
        [Fact]
        public void Envelope_RoundTrips_WithPayload()
        {
            byte[] inner = FactionsWire.WriteUplinkRequest("0.1.0.0");
            byte[] frame = NexusRelayWire.Write(RelayKind.ClientMessage, 2, 76561198000000001UL, inner);

            RelayKind kind; byte home; ulong steam; byte[] payload;
            Assert.True(NexusRelayWire.TryRead(frame, out kind, out home, out steam, out payload));
            Assert.Equal(RelayKind.ClientMessage, kind);
            Assert.Equal(2, home);
            Assert.Equal(76561198000000001UL, steam);
            Assert.Equal(inner, payload);

            // The inner message is untouched: the ordinary client codec still reads it.
            string clientVersion;
            Assert.True(FactionsWire.TryReadUplinkRequest(payload, out clientVersion));
            Assert.Equal("0.1.0.0", clientVersion);
        }

        [Fact]
        public void Unlink_CarriesNoPayload()
        {
            byte[] frame = NexusRelayWire.Write(RelayKind.Unlink, 4, 42UL, null);
            RelayKind kind; byte home; ulong steam; byte[] payload;
            Assert.True(NexusRelayWire.TryRead(frame, out kind, out home, out steam, out payload));
            Assert.Equal(RelayKind.Unlink, kind);
            Assert.Equal(4, home);
            Assert.Equal(42UL, steam);
            Assert.Empty(payload);
        }

        [Fact]
        public void WrongVersion_IsRejectedWhole()
        {
            byte[] frame = NexusRelayWire.Write(RelayKind.ServerMessage, 3, 7UL, new byte[] { 1, 2, 3 });
            frame[0] ^= 0xFF;   // little-endian int32 version, low byte first
            RelayKind kind; byte home; ulong steam; byte[] payload;
            Assert.False(NexusRelayWire.TryRead(frame, out kind, out home, out steam, out payload));
            Assert.Null(payload);
        }

        [Fact]
        public void Truncated_IsRejected()
        {
            byte[] frame = NexusRelayWire.Write(RelayKind.ServerMessage, 3, 7UL, new byte[64]);
            byte[] cut = new byte[frame.Length - 10];
            System.Array.Copy(frame, cut, cut.Length);
            RelayKind kind; byte home; ulong steam; byte[] payload;
            Assert.False(NexusRelayWire.TryRead(cut, out kind, out home, out steam, out payload));
        }

        [Fact]
        public void UnknownKind_IsRejected()
        {
            byte[] frame = NexusRelayWire.Write(RelayKind.ClientMessage, 2, 1UL, new byte[0]);
            frame[4] = 200;   // the kind byte follows the 4-byte version
            RelayKind kind; byte home; ulong steam; byte[] payload;
            Assert.False(NexusRelayWire.TryRead(frame, out kind, out home, out steam, out payload));
        }

        [Fact]
        public void PlayerPositions_RoundTripInsideTheEnvelope()
        {
            var players = new System.Collections.Generic.List<RelayPlayerPosition>
            {
                new RelayPlayerPosition { SteamId = 76561198000000001UL, IdentityId = 144115188075855895L,
                                          X = -1234567.25, Y = 89.5, Z = 4000000.125, Vx = 450f, Vy = -1.5f, Vz = 0f },
                new RelayPlayerPosition { SteamId = 76561198000000002UL, IdentityId = 7L,
                                          X = 1, Y = 2, Z = 3, Vx = 0f, Vy = 0f, Vz = 999.25f },
            };
            byte[] frame = NexusRelayWire.Write(RelayKind.PlayerPositions, 2, 0UL,
                                                NexusRelayWire.WritePlayerPositions(players));

            RelayKind kind; byte home; ulong steam; byte[] payload;
            Assert.True(NexusRelayWire.TryRead(frame, out kind, out home, out steam, out payload));
            Assert.Equal(RelayKind.PlayerPositions, kind);
            Assert.Equal(2, home);
            Assert.Equal(0UL, steam);

            System.Collections.Generic.List<RelayPlayerPosition> read;
            Assert.True(NexusRelayWire.TryReadPlayerPositions(payload, out read));
            Assert.Equal(2, read.Count);
            Assert.Equal(players[0].SteamId, read[0].SteamId);
            Assert.Equal(players[0].IdentityId, read[0].IdentityId);
            Assert.Equal(players[0].X, read[0].X);
            Assert.Equal(players[0].Z, read[0].Z);
            Assert.Equal(players[0].Vx, read[0].Vx);
            Assert.Equal(players[1].Vz, read[1].Vz);
        }

        [Fact]
        public void PlayerPositions_SeatRoundTrips()
        {
            var players = new System.Collections.Generic.List<RelayPlayerPosition>
            {
                new RelayPlayerPosition { SteamId = 7UL, IdentityId = 9, X = 1, SeatGridId = 42,
                                          GridX = -1000.5, GridY = 2, GridZ = 3e6, SensorKm = 12.5f, AntennaCount = 3 },
                new RelayPlayerPosition { SteamId = 8UL, IdentityId = 10 },
            };
            System.Collections.Generic.List<RelayPlayerPosition> read;
            Assert.True(NexusRelayWire.TryReadPlayerPositions(NexusRelayWire.WritePlayerPositions(players), out read));
            Assert.Equal(42, read[0].SeatGridId);
            Assert.Equal(-1000.5, read[0].GridX);
            Assert.Equal(3e6, read[0].GridZ);
            Assert.Equal(12.5f, read[0].SensorKm);
            Assert.Equal(3, read[0].AntennaCount);
            Assert.Equal(0, read[1].SeatGridId);
            Assert.Equal(8UL, read[1].SteamId);
        }

        /// <summary>An agent built before the seat block sends positions only; the authority must
        /// still read every position and see each player as unseated.</summary>
        [Fact]
        public void PlayerPositions_AReportWithoutTheSeatBlockStillReads()
        {
            byte[] old;
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(1);
                w.Write(5UL); w.Write(6L);
                w.Write(1.0); w.Write(2.0); w.Write(3.0);
                w.Write(0f); w.Write(0f); w.Write(0f);
                w.Flush();
                old = ms.ToArray();
            }
            System.Collections.Generic.List<RelayPlayerPosition> read;
            Assert.True(NexusRelayWire.TryReadPlayerPositions(old, out read));
            Assert.Single(read);
            Assert.Equal(5UL, read[0].SteamId);
            Assert.Equal(3.0, read[0].Z);
            Assert.Equal(0, read[0].SeatGridId);
        }

        [Fact]
        public void PlayerPositions_EmptySetIsAValidReport()
        {
            System.Collections.Generic.List<RelayPlayerPosition> read;
            Assert.True(NexusRelayWire.TryReadPlayerPositions(NexusRelayWire.WritePlayerPositions(null), out read));
            Assert.Empty(read);
        }

        [Fact]
        public void PlayerPositions_TruncatedOrPaddedIsRejected()
        {
            var one = new System.Collections.Generic.List<RelayPlayerPosition> { new RelayPlayerPosition { SteamId = 1UL } };
            byte[] payload = NexusRelayWire.WritePlayerPositions(one);
            System.Collections.Generic.List<RelayPlayerPosition> read;

            byte[] cut = new byte[payload.Length - 1];
            System.Array.Copy(payload, cut, cut.Length);
            Assert.False(NexusRelayWire.TryReadPlayerPositions(cut, out read));

            byte[] padded = new byte[payload.Length + 1];
            System.Array.Copy(payload, padded, payload.Length);
            Assert.False(NexusRelayWire.TryReadPlayerPositions(padded, out read));
        }

        [Fact]
        public void PlayerPositions_ACorruptCountIsRejectedBeforeAllocating()
        {
            byte[] payload = System.BitConverter.GetBytes(int.MaxValue);
            System.Collections.Generic.List<RelayPlayerPosition> read;
            Assert.False(NexusRelayWire.TryReadPlayerPositions(payload, out read));
            Assert.Null(read);
        }

        [Fact]
        public void ChatCommand_SurvivesTheWire()
        {
            var c = new RelayChatCommand { Id = 41, Text = "!factions station status \"Kestrel Yard\"", IdentityId = 144115188075855900L, Promote = 4,
                                           HasPosition = true, X = -1.5e6, Y = 2.25, Z = 3e5 };
            byte[] frame = NexusRelayWire.Write(RelayKind.ChatCommand, 2, 76561198000000001UL, NexusRelayWire.WriteChatCommand(c));
            RelayKind kind; byte home; ulong steam; byte[] payload;
            Assert.True(NexusRelayWire.TryRead(frame, out kind, out home, out steam, out payload));
            Assert.Equal(RelayKind.ChatCommand, kind);
            RelayChatCommand back;
            Assert.True(NexusRelayWire.TryReadChatCommand(payload, out back));
            Assert.Equal(41, back.Id);
            Assert.Equal(c.Text, back.Text);
            Assert.Equal(144115188075855900L, back.IdentityId);
            Assert.Equal(4, back.Promote);
            Assert.True(back.HasPosition);
            Assert.Equal(-1.5e6, back.X);
            Assert.Equal(3e5, back.Z);
        }

        [Fact]
        public void ChatReply_KeepsEveryLineInOrder()
        {
            var lines = new System.Collections.Generic.List<string> { "first", "", "third\nwith a break" };
            byte[] frame = NexusRelayWire.Write(RelayKind.ChatReply, 3, 7UL, NexusRelayWire.WriteChatReply(9, lines));
            RelayKind kind; byte home; ulong steam; byte[] payload;
            Assert.True(NexusRelayWire.TryRead(frame, out kind, out home, out steam, out payload));
            Assert.Equal(RelayKind.ChatReply, kind);
            long id; System.Collections.Generic.List<string> back;
            Assert.True(NexusRelayWire.TryReadChatReply(payload, out id, out back));
            Assert.Equal(9, id);
            Assert.Equal(lines, back);
        }

        [Fact]
        public void ChatReply_ACorruptCountOrTrailingBytesAreRejected()
        {
            long id; System.Collections.Generic.List<string> back;
            byte[] corrupt = new byte[12];
            System.BitConverter.GetBytes(int.MaxValue).CopyTo(corrupt, 8);
            Assert.False(NexusRelayWire.TryReadChatReply(corrupt, out id, out back));

            byte[] ok = NexusRelayWire.WriteChatReply(1, new[] { "x" });
            byte[] padded = new byte[ok.Length + 1];
            System.Array.Copy(ok, padded, ok.Length);
            Assert.False(NexusRelayWire.TryReadChatReply(padded, out id, out back));
        }

        [Fact]
        public void AnUnknownKindIsRejected()
        {
            byte[] frame = NexusRelayWire.Write((RelayKind)((byte)NexusRelayWire.LastKind + 1), 2, 1UL, null);
            RelayKind kind; byte home; ulong steam; byte[] payload;
            Assert.False(NexusRelayWire.TryRead(frame, out kind, out home, out steam, out payload));
        }

        /// <summary>A kind declared but above the read bound is sent and then dropped on arrival,
        /// silently: the captures and wing calls were, until the bound followed the enum.</summary>
        [Fact]
        public void EveryDeclaredKindIsReadBack()
        {
            foreach (RelayKind declared in System.Enum.GetValues(typeof(RelayKind)))
            {
                byte[] frame = NexusRelayWire.Write(declared, 3, 9UL, new byte[] { 7 });
                RelayKind kind; byte home; ulong steam; byte[] payload;
                Assert.True(NexusRelayWire.TryRead(frame, out kind, out home, out steam, out payload), declared.ToString());
                Assert.Equal(declared, kind);
                Assert.True(declared <= NexusRelayWire.LastKind, declared.ToString());
            }
        }
    }
}
