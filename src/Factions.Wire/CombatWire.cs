using System.IO;
using System.Text;

namespace Factions.Wire
{
    /// <summary>What one viewer makes of a fight. The byte values are Core's <c>CombatTone</c>;
    /// a value is never renumbered.</summary>
    public enum WireCombatTone : byte
    {
        /// <summary>The viewer's faction was on neither side.</summary>
        Neutral = 0,
        Win = 1,
        Loss = 2,
    }

    public static partial class FactionsWire
    {
        public static byte[] WriteCombatOutcomeNotice(WireCombatTone tone, string text)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.CombatOutcomeNotice);
                w.Write((byte)tone);
                w.Write(text ?? "");
                return ms.ToArray();
            }
        }

        /// <summary>False for another kind, a truncated payload, or a tone this build does not know.</summary>
        public static bool TryReadCombatOutcomeNotice(byte[] data, out WireCombatTone tone, out string text)
        {
            tone = WireCombatTone.Neutral;
            text = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.CombatOutcomeNotice) return false;
                    byte t = r.ReadByte();
                    string s = r.ReadString();
                    if (t > (byte)WireCombatTone.Loss) return false;
                    tone = (WireCombatTone)t;
                    text = s;
                    return true;
                }
            }
            catch { tone = WireCombatTone.Neutral; text = null; return false; }
        }
    }
}
