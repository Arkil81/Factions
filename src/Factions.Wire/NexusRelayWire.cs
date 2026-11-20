using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Factions.Wire
{
    /// <summary>What a relayed frame carries. Server-to-server only; a client never sees one.</summary>
    public enum RelayKind : byte
    {
        /// <summary>A client-originated Factions wire message, forwarded by the client's home
        /// server to the authority unchanged.</summary>
        ClientMessage = 1,

        /// <summary>A server-originated Factions wire message for one client, sent by the authority
        /// to that client's home server for delivery.</summary>
        ServerMessage = 2,

        /// <summary>The home server dropped this client's link (offline); the authority forgets it.</summary>
        Unlink = 3,

        /// <summary>Where the sending server's players are, sent by an agent to the authority on a
        /// cadence. The header's steam id is 0; the payload is
        /// <see cref="NexusRelayWire.WritePlayerPositions"/>. An agent replaces its whole set with
        /// each frame, so a player missing from it is no longer on that server.</summary>
        PlayerPositions = 4,

        /// <summary>The authority's node graph for an agent's materialisers, sent on a cadence. Steam
        /// id 0; the payload is a gzipped Core <c>NexusNodeSnapshot</c>, opaque here.</summary>
        NodeSnapshot = 5,

        /// <summary>What an agent's materialisers decided that the authority owns. Steam id 0; the
        /// payload is a gzipped Core <c>NexusAgentReport</c>, opaque here.</summary>
        AgentReport = 6,
        /// <summary>The authority's answer to the stations in an agent's report: grants the agent has
        /// not loaded, and close acknowledgements. Steam id 0; gzipped Core <c>NexusStationReply</c>.</summary>
        StationReply = 7,
    }

    /// <summary>One player in a <see cref="RelayKind.PlayerPositions"/> frame. Position in world
    /// metres, velocity in m/s.</summary>
    public struct RelayPlayerPosition
    {
        public ulong SteamId;
        public long IdentityId;
        public double X, Y, Z;
        public float Vx, Vy, Vz;
        /// <summary>The grid the player sits at a controller of; 0 when they are not seated. The
        /// authority's scanner runs from it for a player it does not host.</summary>
        public long SeatGridId;
        /// <summary>That grid's position in metres.</summary>
        public double GridX, GridY, GridZ;
        /// <summary>The grid's sensor strength in km, from its antennas as the agent reads them.</summary>
        public float SensorKm;
        public int AntennaCount;
    }

    /// <summary>
    /// The envelope a Factions wire message travels in between two Nexus servers. The client talks
    /// only to the server it is connected to; when that server is not the simulation authority it
    /// wraps each feature message in one of these and hands it to Nexus, and the authority answers
    /// the same way. The wire message inside is byte-for-byte what the client sent or will receive,
    /// so the feature codecs on both sides never learn a relay exists.
    ///
    /// Framing is deliberately the same BinaryWriter shape as <see cref="FactionsWire"/> and the
    /// version is checked first, so a frame from a differently-built server is rejected whole.
    /// </summary>
    public static class NexusRelayWire
    {
        /// <summary>Nexus mod-message channel the relay frames travel on. Any value distinct from
        /// every other plugin's; Nexus routes by it and does not otherwise interpret it.</summary>
        public const long ModChannelId = 4207935489L;   // 0xFAC70001

        /// <summary>Bumped on any change to the frame layout below.</summary>
        public const int RelayVersion = 1;

        /// <summary>Upper bound on the inner payload, so a corrupt length cannot pre-allocate an
        /// enormous buffer before the read fails.</summary>
        private const int MaxPayloadBytes = 16 * 1024 * 1024;

        public static byte[] Write(RelayKind kind, byte homeServerId, ulong steamId, byte[] payload)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(RelayVersion);
                w.Write((byte)kind);
                w.Write(homeServerId);
                w.Write(steamId);
                int len = payload == null ? 0 : payload.Length;
                w.Write(len);
                if (len > 0) w.Write(payload, 0, len);
                return ms.ToArray();
            }
        }

        public static bool TryRead(byte[] data, out RelayKind kind, out byte homeServerId,
                                   out ulong steamId, out byte[] payload)
        {
            kind = 0; homeServerId = 0; steamId = 0; payload = null;
            if (data == null) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    if (r.ReadInt32() != RelayVersion) return false;
                    kind = (RelayKind)r.ReadByte();
                    if (kind < RelayKind.ClientMessage || kind > RelayKind.StationReply) return false;
                    homeServerId = r.ReadByte();
                    steamId = r.ReadUInt64();
                    int len = r.ReadInt32();
                    if (len < 0 || len > MaxPayloadBytes) return false;
                    payload = len == 0 ? new byte[0] : r.ReadBytes(len);
                    return payload.Length == len;
                }
            }
            catch { return false; }
        }

        /// <summary>Upper bound on players in one positions frame, so a corrupt count cannot
        /// pre-allocate before the read fails. Far above any one server's player cap.</summary>
        public const int MaxPositionsPerFrame = 1024;

        /// <summary>Bytes per player in the seat block: grid id, position, sensor km, antennas.</summary>
        private const int SeatBytes = 8 + 3 * 8 + 4 + 4;

        /// <summary>The payload of a <see cref="RelayKind.PlayerPositions"/> frame.</summary>
        public static byte[] WritePlayerPositions(IList<RelayPlayerPosition> players)
        {
            int n = players == null ? 0 : Math.Min(players.Count, MaxPositionsPerFrame);
            using (var ms = new MemoryStream(4 + n * (52 + SeatBytes)))
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(n);
                for (int i = 0; i < n; i++)
                {
                    RelayPlayerPosition p = players[i];
                    w.Write(p.SteamId);
                    w.Write(p.IdentityId);
                    w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
                    w.Write(p.Vx); w.Write(p.Vy); w.Write(p.Vz);
                }
                // The seat block follows the whole position block, so a reader that predates it
                // still finds every position where it was.
                for (int i = 0; i < n; i++)
                {
                    RelayPlayerPosition p = players[i];
                    w.Write(p.SeatGridId);
                    w.Write(p.GridX); w.Write(p.GridY); w.Write(p.GridZ);
                    w.Write(p.SensorKm);
                    w.Write(p.AntennaCount);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadPlayerPositions(byte[] payload, out List<RelayPlayerPosition> players)
        {
            players = null;
            if (payload == null) return false;
            try
            {
                using (var ms = new MemoryStream(payload))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    int n = r.ReadInt32();
                    if (n < 0 || n > MaxPositionsPerFrame) return false;
                    var list = new List<RelayPlayerPosition>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var p = new RelayPlayerPosition();
                        p.SteamId = r.ReadUInt64();
                        p.IdentityId = r.ReadInt64();
                        p.X = r.ReadDouble(); p.Y = r.ReadDouble(); p.Z = r.ReadDouble();
                        p.Vx = r.ReadSingle(); p.Vy = r.ReadSingle(); p.Vz = r.ReadSingle();
                        list.Add(p);
                    }
                    // A report with no seat block is a whole report from an older agent.
                    if (ms.Position != ms.Length)
                    {
                        if (ms.Length - ms.Position != (long)n * SeatBytes) return false;
                        for (int i = 0; i < n; i++)
                        {
                            RelayPlayerPosition p = list[i];
                            p.SeatGridId = r.ReadInt64();
                            p.GridX = r.ReadDouble(); p.GridY = r.ReadDouble(); p.GridZ = r.ReadDouble();
                            p.SensorKm = r.ReadSingle();
                            p.AntennaCount = r.ReadInt32();
                            list[i] = p;
                        }
                    }
                    players = list;
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
