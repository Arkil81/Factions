using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Factions.Wire
{
    /// <summary>One of the caller's founded nodes on the respawn screen. Mirrors Core's
    /// <c>NodeSpawnOption</c>.</summary>
    public struct WireNodeSpawnRow
    {
        public string NodeId;
        public string DisplayName;
        /// <summary>The station's <c>StableStationId</c>; what <see cref="NodeSpawnSentinel"/> encodes.</summary>
        public long StationId;
        /// <summary>Core's <c>StationSite</c>: 0 space, 1 orbital, 2 surface.</summary>
        public byte Site;
        /// <summary>Core's <c>NodeSpawnBlock</c>; 0 means the node can take a spawn now.</summary>
        public byte Block;
        /// <summary>Seconds until a cooldown block lifts, counted from when the list was sent.</summary>
        public int CooldownSeconds;
    }

    /// <summary>
    /// How a node spawn rides SE's own respawn request. <c>RespawnMsg</c> carries one entity id and
    /// nothing a plugin can add to, so the station is named by the negation of its stable id: SE's
    /// entity ids are positive, so a negative one can never name a real medical room. A server that
    /// does not intercept it looks the id up, finds nothing, and refuses the respawn, which reopens
    /// the client's respawn screen — the failure is SE's own, never a spawn somewhere else.
    /// </summary>
    public static class NodeSpawnSentinel
    {
        public static long Encode(long stationId)
        {
            return stationId > 0 ? -stationId : 0L;
        }

        public static bool TryDecode(long respawnEntityId, out long stationId)
        {
            stationId = respawnEntityId < 0 && respawnEntityId != long.MinValue ? -respawnEntityId : 0L;
            return stationId != 0;
        }
    }

    public static partial class FactionsWire
    {
        public const int MaxNodeSpawnRows = 512;

        // ---- Node spawn -------------------------------------------------------------------------

        public static byte[] WriteNodeSpawnListRequest()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NodeSpawnListRequest);
                return ms.ToArray();
            }
        }

        public static bool TryReadNodeSpawnListRequest(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    return ReadHeader(r, out kind) && kind == WireMessageKind.NodeSpawnListRequest;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteNodeSpawnList(IList<WireNodeSpawnRow> rows)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.NodeSpawnList);
                int count = rows != null ? rows.Count : 0;
                if (count > MaxNodeSpawnRows) count = MaxNodeSpawnRows;
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    WireNodeSpawnRow row = rows[i];
                    w.Write(row.NodeId ?? "");
                    w.Write(row.DisplayName ?? "");
                    w.Write(row.StationId);
                    w.Write(row.Site);
                    w.Write(row.Block);
                    w.Write(row.CooldownSeconds);
                }
                return ms.ToArray();
            }
        }

        public static bool TryReadNodeSpawnList(byte[] data, out List<WireNodeSpawnRow> rows)
        {
            rows = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.NodeSpawnList) return false;
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxNodeSpawnRows) return false;
                    var read = new List<WireNodeSpawnRow>(count);
                    for (int i = 0; i < count; i++)
                    {
                        WireNodeSpawnRow row;
                        row.NodeId = r.ReadString();
                        row.DisplayName = r.ReadString();
                        row.StationId = r.ReadInt64();
                        row.Site = r.ReadByte();
                        row.Block = r.ReadByte();
                        row.CooldownSeconds = r.ReadInt32();
                        read.Add(row);
                    }
                    rows = read;
                    return true;
                }
            }
            catch { rows = null; return false; }
        }

        /// <summary>What the respawn screen says about a blocked row. One table for both ends, so the
        /// row's status and the refusal the server sends at spawn time read the same.</summary>
        public static string NodeSpawnBlockText(byte block)
        {
            switch (block)
            {
                case 0:  return "Ready";
                case 1:  return "No station";
                case 2:  return "Not your faction's";
                case 3:  return "Safe zone down";
                case 4:  return "Cooldown";
                default: return "Unavailable";
            }
        }
    }
}
