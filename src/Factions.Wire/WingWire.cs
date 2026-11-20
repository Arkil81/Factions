using System.IO;
using System.Text;

namespace Factions.Wire
{
    /// <summary>The wing actions an antenna carries. The byte is the wire value and part of the
    /// terminal action id (<see cref="WingActions.Id"/>), so a value is never renumbered: a toolbar
    /// slot saved with it names it.</summary>
    public enum WireWingCommand : byte
    {
        FormOnMe = 1,
        FocusTarget = 2,
        Hold = 3,
        BreakOff = 4,
        Return = 5,
        Release = 6,
        /// <summary>A station's antenna calls for protection.</summary>
        ProtectMe = 7,
    }

    /// <summary>The terminal action ids and names of the wing actions, one table for the server and
    /// the client. SE stores a toolbar slot as the block's entity id and this action id, and the
    /// server resolves the id against its own registration, so the two sides must agree on every
    /// string here.</summary>
    public static class WingActions
    {
        public const string IdPrefix = "FactionsWing";

        /// <summary>The commands in toolbar order.</summary>
        public static readonly WireWingCommand[] All =
        {
            WireWingCommand.FormOnMe, WireWingCommand.FocusTarget, WireWingCommand.Hold,
            WireWingCommand.BreakOff, WireWingCommand.Return, WireWingCommand.Release,
            WireWingCommand.ProtectMe,
        };

        public static string Id(WireWingCommand command)
        {
            return IdPrefix + command;
        }

        public static string Name(WireWingCommand command)
        {
            switch (command)
            {
                case WireWingCommand.FormOnMe:    return "Wing: form on me";
                case WireWingCommand.FocusTarget: return "Wing: focus my target";
                case WireWingCommand.Hold:        return "Wing: hold";
                case WireWingCommand.BreakOff:    return "Wing: break off";
                case WireWingCommand.Return:      return "Wing: return";
                case WireWingCommand.Release:     return "Wing: release";
                case WireWingCommand.ProtectMe:   return "Wing: protect me";
                default:                          return "Wing: " + command;
            }
        }

        public static bool IsKnown(byte value)
        {
            return value >= (byte)WireWingCommand.FormOnMe && value <= (byte)WireWingCommand.ProtectMe;
        }
    }

    public static partial class FactionsWire
    {
        // ---- Wing command -----------------------------------------------------------------------

        /// <summary>A wing action pressed on <paramref name="antennaEntityId"/>. <paramref name="crosshairEntityId"/>
        /// is the grid under the presser's crosshair, zero for none; the server prefers the presser's
        /// own target lock, which it reads itself.</summary>
        public static byte[] WriteWingCommandRequest(long antennaEntityId, WireWingCommand command, long crosshairEntityId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.WingCommandRequest);
                w.Write(antennaEntityId);
                w.Write((byte)command);
                w.Write(crosshairEntityId);
                return ms.ToArray();
            }
        }

        /// <summary>A command byte this build does not know is refused here.</summary>
        public static bool TryReadWingCommandRequest(byte[] data, out long antennaEntityId, out WireWingCommand command,
                                                     out long crosshairEntityId)
        {
            antennaEntityId = 0;
            command = WireWingCommand.Release;
            crosshairEntityId = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.WingCommandRequest) return false;
                    long antenna = r.ReadInt64();
                    byte c = r.ReadByte();
                    long crosshair = r.ReadInt64();
                    if (!WingActions.IsKnown(c)) return false;
                    antennaEntityId = antenna;
                    command = (WireWingCommand)c;
                    crosshairEntityId = crosshair;
                    return true;
                }
            }
            catch { return false; }
        }

        public static byte[] WriteWingCommandResult(bool ok, WireWingCommand command, string message)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.WingCommandResult);
                w.Write(ok);
                w.Write((byte)command);
                w.Write(message ?? "");
                return ms.ToArray();
            }
        }

        public static bool TryReadWingCommandResult(byte[] data, out bool ok, out WireWingCommand command, out string message)
        {
            ok = false;
            command = WireWingCommand.Release;
            message = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.WingCommandResult) return false;
                    bool done = r.ReadBoolean();
                    byte c = r.ReadByte();
                    string text = r.ReadString();
                    if (!WingActions.IsKnown(c)) return false;
                    ok = done;
                    command = (WireWingCommand)c;
                    message = text;
                    return true;
                }
            }
            catch { ok = false; message = null; return false; }
        }
    }
}
