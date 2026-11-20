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
    }
}
