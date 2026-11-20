using System.IO;
using System.Text;

namespace Factions.Wire
{
    /// <summary>The crew orders a seat carries. The byte is the wire value and part of the terminal
    /// action id (<see cref="CrewActions.Id"/>), so a value is never renumbered: a toolbar slot saved
    /// with it names it.</summary>
    public enum WireCrewOrder : byte
    {
        Mount = 1,
        Dismount = 2,
        Ease = 3,
        Auto = 4,
    }

    /// <summary>The terminal action ids and names of the crew orders, one table for the server and
    /// the client (see <see cref="WingActions"/> for why the two must agree on every string).</summary>
    public static class CrewActions
    {
        public const string IdPrefix = "FactionsCrew";

        /// <summary>The orders in toolbar order.</summary>
        public static readonly WireCrewOrder[] All =
        {
            WireCrewOrder.Mount, WireCrewOrder.Dismount, WireCrewOrder.Ease, WireCrewOrder.Auto,
        };

        public static string Id(WireCrewOrder order)
        {
            return IdPrefix + order;
        }

        public static string Name(WireCrewOrder order)
        {
            switch (order)
            {
                case WireCrewOrder.Mount:    return "Crew: mount up";
                case WireCrewOrder.Dismount: return "Crew: dismount";
                case WireCrewOrder.Ease:     return "Crew: at ease";
                case WireCrewOrder.Auto:     return "Crew: automatic";
                default:                     return "Crew: " + order;
            }
        }

        public static bool IsKnown(byte value)
        {
            return value >= (byte)WireCrewOrder.Mount && value <= (byte)WireCrewOrder.Auto;
        }
    }

    public static partial class FactionsWire
    {
        // ---- Crew order -------------------------------------------------------------------------

        /// <summary>A crew order pressed on <paramref name="seatEntityId"/>'s toolbar.</summary>
        public static byte[] WriteCrewOrderRequest(long seatEntityId, WireCrewOrder order)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.CrewOrderRequest);
                w.Write(seatEntityId);
                w.Write((byte)order);
                return ms.ToArray();
            }
        }

        /// <summary>An order byte this build does not know is refused here.</summary>
        public static bool TryReadCrewOrderRequest(byte[] data, out long seatEntityId, out WireCrewOrder order)
        {
            seatEntityId = 0;
            order = WireCrewOrder.Auto;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.CrewOrderRequest) return false;
                    long seat = r.ReadInt64();
                    byte o = r.ReadByte();
                    if (!CrewActions.IsKnown(o)) return false;
                    seatEntityId = seat;
                    order = (WireCrewOrder)o;
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteCrewOrderResult(bool ok, WireCrewOrder order, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.CrewOrderResult);
                w.Write(ok);
                w.Write((byte)order);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadCrewOrderResult(byte[] data, out bool ok, out WireCrewOrder order, out string message)
        {
            ok = false;
            order = WireCrewOrder.Auto;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.CrewOrderResult) return false;
                    bool done = r.ReadBoolean();
                    byte o = r.ReadByte();
                    string text = r.ReadString();
                    if (!CrewActions.IsKnown(o)) return false;
                    ok = done;
                    order = (WireCrewOrder)o;
                    message = text;
                    return true;
                }
            }
            catch { ok = false; message = null; return false; }
        }
    }
}
