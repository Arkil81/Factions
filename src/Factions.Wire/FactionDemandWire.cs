using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Factions.Wire
{
    /// <summary>A node id and the name to show for it.</summary>
    public struct WireNamedNode
    {
        public string Id;
        public string Name;
    }

    /// <summary>The viewing player's own faction's demand: the faction-level variant of the node
    /// detail snapshot's demand block.</summary>
    public struct WireFactionDemand
    {
        /// <summary>Empty when the viewer is in no faction Factions has registered.</summary>
        public string FactionTag;
        /// <summary>Names for every node a row is pinned to or bound for.</summary>
        public WireNamedNode[] Nodes;
        /// <summary>Live demand, most urgent first, each row carrying
        /// <see cref="WireNodeDemand.AtNodeId"/>.</summary>
        public WireNodeDemand[] Demand;
        /// <summary>Demand that ended inside <see cref="WindowSeconds"/>, most recently ended first.</summary>
        public WireNodeEndedDemand[] Ended;
        /// <summary>The server's retention, in seconds.</summary>
        public int WindowSeconds;
        /// <summary>Live rows before the writer's cap, so the window can say what it is not showing.</summary>
        public int DemandTotal;
        /// <summary>Ended rows before the writer's cap.</summary>
        public int EndedTotal;
    }

    public static partial class FactionsWire
    {
        /// <summary>Bound on one faction demand snapshot's live rows. Most urgent first on the wire.</summary>
        public const int MaxFactionDemandRows = 512;
        /// <summary>Bound on one faction demand snapshot's ended rows. Most recently ended first.</summary>
        public const int MaxFactionEndedDemandRows = 512;
        private const int MaxFactionDemandNodes = 2048;

        // ---- Faction demand --------------------------------------------------------------------

        public static byte[] WriteFactionDemandRequest(bool stream)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.FactionDemandRequest);
                w.Write(stream);
                return ms.ToArray();
            }
        }

        public static bool TryReadFactionDemandRequest(byte[] data, out bool stream)
        {
            stream = false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.FactionDemandRequest) return false;
                    stream = r.ReadBoolean();
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteFactionDemandSnapshot(WireFactionDemand snapshot)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.FactionDemandSnapshot);
                w.Write(snapshot.FactionTag ?? "");

                int nodeCount = snapshot.Nodes == null ? 0 : snapshot.Nodes.Length;
                if (nodeCount > MaxFactionDemandNodes) nodeCount = MaxFactionDemandNodes;
                w.Write(nodeCount);
                for (int i = 0; i < nodeCount; i++)
                {
                    w.Write(snapshot.Nodes[i].Id ?? "");
                    w.Write(snapshot.Nodes[i].Name ?? "");
                }

                int demandCount = snapshot.Demand == null ? 0 : snapshot.Demand.Length;
                if (demandCount > MaxFactionDemandRows) demandCount = MaxFactionDemandRows;
                w.Write(demandCount);
                for (int i = 0; i < demandCount; i++)
                {
                    WireNodeDemand d = snapshot.Demand[i];
                    w.Write(d.Kind ?? "");
                    w.Write(d.AtNodeId ?? "");
                    w.Write(d.DestNodeId ?? "");
                    w.Write(d.Quantity);
                    w.Write(d.HasHave);
                    w.Write(d.Have);
                    w.Write(d.UsedPerHour);
                    w.Write(d.Class);
                    w.Write(d.DestinedForSale);
                    w.Write(d.FirstPostedUtcTicks);
                }

                int endedCount = snapshot.Ended == null ? 0 : snapshot.Ended.Length;
                if (endedCount > MaxFactionEndedDemandRows) endedCount = MaxFactionEndedDemandRows;
                w.Write(endedCount);
                for (int i = 0; i < endedCount; i++)
                {
                    WireNodeEndedDemand e = snapshot.Ended[i];
                    w.Write(e.Kind ?? "");
                    w.Write(e.AtNodeId ?? "");
                    w.Write(e.DestNodeId ?? "");
                    w.Write(e.PeakQuantity);
                    w.Write(e.Class);
                    w.Write(e.DestinedForSale);
                    w.Write(e.Consumed);
                    w.Write(e.FirstPostedUtcTicks);
                    w.Write(e.EndedUtcTicks);
                }

                w.Write(snapshot.WindowSeconds);
                w.Write(snapshot.DemandTotal);
                w.Write(snapshot.EndedTotal);
                return ms.ToArray();
            }
        }

        public static bool TryReadFactionDemandSnapshot(byte[] data, out WireFactionDemand snapshot)
        {
            snapshot = new WireFactionDemand();
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.FactionDemandSnapshot) return false;
                    var read = new WireFactionDemand { FactionTag = r.ReadString() };

                    int nodeCount = r.ReadInt32();
                    if (nodeCount < 0 || nodeCount > MaxFactionDemandNodes) return false;
                    var nodes = new WireNamedNode[nodeCount];
                    for (int i = 0; i < nodeCount; i++)
                    {
                        nodes[i].Id = r.ReadString();
                        nodes[i].Name = r.ReadString();
                    }
                    read.Nodes = nodes;

                    int demandCount = r.ReadInt32();
                    if (demandCount < 0 || demandCount > MaxFactionDemandRows) return false;
                    var demand = new WireNodeDemand[demandCount];
                    for (int i = 0; i < demandCount; i++)
                    {
                        demand[i].Kind = r.ReadString();
                        demand[i].AtNodeId = r.ReadString();
                        demand[i].DestNodeId = r.ReadString();
                        demand[i].Quantity = r.ReadInt64();
                        demand[i].HasHave = r.ReadBoolean();
                        demand[i].Have = r.ReadInt64();
                        demand[i].UsedPerHour = r.ReadDouble();
                        demand[i].Class = r.ReadByte();
                        demand[i].DestinedForSale = r.ReadBoolean();
                        demand[i].FirstPostedUtcTicks = r.ReadInt64();
                    }
                    read.Demand = demand;

                    int endedCount = r.ReadInt32();
                    if (endedCount < 0 || endedCount > MaxFactionEndedDemandRows) return false;
                    var ended = new WireNodeEndedDemand[endedCount];
                    for (int i = 0; i < endedCount; i++)
                    {
                        ended[i].Kind = r.ReadString();
                        ended[i].AtNodeId = r.ReadString();
                        ended[i].DestNodeId = r.ReadString();
                        ended[i].PeakQuantity = r.ReadInt64();
                        ended[i].Class = r.ReadByte();
                        ended[i].DestinedForSale = r.ReadBoolean();
                        ended[i].Consumed = r.ReadBoolean();
                        ended[i].FirstPostedUtcTicks = r.ReadInt64();
                        ended[i].EndedUtcTicks = r.ReadInt64();
                    }
                    read.Ended = ended;

                    read.WindowSeconds = r.ReadInt32();
                    read.DemandTotal = r.ReadInt32();
                    read.EndedTotal = r.ReadInt32();
                    snapshot = read;
                    return true;
                }
            }
            catch { snapshot = new WireFactionDemand(); return false; }
        }
    }
}
