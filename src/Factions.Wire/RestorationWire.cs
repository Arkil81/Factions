using System.IO;
using System.Text;

namespace Factions.Wire
{
    /// <summary>What a <see cref="WireMessageKind.RestoreQuote"/> offers the player. A value is
    /// never renumbered.</summary>
    public enum WireRestoreOffer : byte
    {
        /// <summary>Nothing to start: a refusal, or nothing to do. The message says which.</summary>
        None = 0,
        /// <summary>A restore bill; the button starts it for at most the quoted credits.</summary>
        Restore = 1,
        /// <summary>The ship is whole: register its design for the quoted fee.</summary>
        Register = 2,
        /// <summary>A restore of this ship is running or paused; the button stops it.</summary>
        Running = 3,
        /// <summary>The design is being matched; the quote follows in its own message.</summary>
        Pending = 4,
    }

    /// <summary>What a <see cref="WireMessageKind.RestoreRequest"/> asks for.</summary>
    public enum WireRestoreAction : byte
    {
        Start = 1,
        Register = 2,
        Stop = 3,
    }

    /// <summary>One component of a restore bill, split by where it comes from.</summary>
    public struct WireRestoreLine
    {
        /// <summary>Component subtype.</summary>
        public string Name;
        public long Need;
        public long FromShip;
        public long FromNode;
        public long NodeCredits;
        public long FromBank;
        public long BankCredits;
        /// <summary>Units no source can supply; the restore cannot run while any line has some.</summary>
        public long Short;
    }

    /// <summary>What the Repair tab shows for one ship at a Factions station: the design it is
    /// restored to, what the restore does, the bill, and — while one runs — its progress.</summary>
    public sealed class WireRestoreQuote
    {
        public long TerminalEntityId;
        public long GridEntityId;
        /// <summary>This station's Repair tab is Restore's. False at a station Factions does not
        /// run: the tab stays vanilla.</summary>
        public bool Managed;
        public WireRestoreOffer Offer;
        /// <summary>A refusal, a note, or a running restore's state, in the player's words.</summary>
        public string Message;
        /// <summary>The design, e.g. <c>matched:Laevateinn</c>, <c>design:&lt;eid&gt;</c>.</summary>
        public string Source;
        /// <summary>The pattern match's score, 0..1; negative when the design was not matched.</summary>
        public double Score = -1;
        public int Welds;
        public int Places;
        public int Blocked;
        /// <summary>Live blocks the design does not have: the owner's additions, left alone.</summary>
        public int Extra;
        public int Unreachable;
        /// <summary>The bill's total for a restore; the fee for a registration.</summary>
        public long Credits;
        /// <summary>The restore's estimated time, uninterrupted; while running, the time left.</summary>
        public int Seconds;
        public int Placed;
        public int Welded;
        public int Skipped;
        /// <summary>Steps in the running restore.</summary>
        public int Total;
        public long Paid;
        /// <summary>"range" or "damage" while a running restore is paused; empty otherwise.</summary>
        public string PauseReason;
        public WireRestoreLine[] Lines;
    }

    public static partial class FactionsWire
    {
        public static byte[] WriteRestoreQuoteRequest(long terminalEntityId, long gridEntityId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.RestoreQuoteRequest);
                w.Write(terminalEntityId);
                w.Write(gridEntityId);
                return ms.ToArray();
            }
        }

        public static bool TryReadRestoreQuoteRequest(byte[] data, out long terminalEntityId, out long gridEntityId)
        {
            terminalEntityId = 0;
            gridEntityId = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.RestoreQuoteRequest) return false;
                    long t = r.ReadInt64();
                    long g = r.ReadInt64();
                    terminalEntityId = t;
                    gridEntityId = g;
                    return true;
                }
            }
            catch { terminalEntityId = 0; gridEntityId = 0; return false; }
        }

        /// <summary>The Repair tab's panel for one ship, or — for the probe a terminal sends when it
        /// opens (grid 0) — only whether this station's Repair tab is Restore's.</summary>
        public static byte[] WriteRestoreQuote(WireRestoreQuote q)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.RestoreQuote);
                w.Write(q.TerminalEntityId);
                w.Write(q.GridEntityId);
                w.Write(q.Managed);
                w.Write((byte)q.Offer);
                w.Write(q.Message ?? "");
                w.Write(q.Source ?? "");
                w.Write(q.Score);
                w.Write(q.Welds);
                w.Write(q.Places);
                w.Write(q.Blocked);
                w.Write(q.Extra);
                w.Write(q.Unreachable);
                w.Write(q.Credits);
                w.Write(q.Seconds);
                w.Write(q.Placed);
                w.Write(q.Welded);
                w.Write(q.Skipped);
                w.Write(q.Total);
                w.Write(q.Paid);
                w.Write(q.PauseReason ?? "");
                int n = q.Lines == null ? 0 : q.Lines.Length;
                w.Write(n);
                for (int i = 0; i < n; i++)
                {
                    WireRestoreLine l = q.Lines[i];
                    w.Write(l.Name ?? "");
                    w.Write(l.Need);
                    w.Write(l.FromShip);
                    w.Write(l.FromNode);
                    w.Write(l.NodeCredits);
                    w.Write(l.FromBank);
                    w.Write(l.BankCredits);
                    w.Write(l.Short);
                }
                return ms.ToArray();
            }
        }

        /// <summary>False for another kind, a truncated payload, an offer this build does not know,
        /// or a line count the payload cannot hold.</summary>
        public static bool TryReadRestoreQuote(byte[] data, out WireRestoreQuote quote)
        {
            quote = null;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.RestoreQuote) return false;
                    var q = new WireRestoreQuote();
                    q.TerminalEntityId = r.ReadInt64();
                    q.GridEntityId = r.ReadInt64();
                    q.Managed = r.ReadBoolean();
                    byte o = r.ReadByte();
                    if (o > (byte)WireRestoreOffer.Pending) return false;
                    q.Offer = (WireRestoreOffer)o;
                    q.Message = r.ReadString();
                    q.Source = r.ReadString();
                    q.Score = r.ReadDouble();
                    q.Welds = r.ReadInt32();
                    q.Places = r.ReadInt32();
                    q.Blocked = r.ReadInt32();
                    q.Extra = r.ReadInt32();
                    q.Unreachable = r.ReadInt32();
                    q.Credits = r.ReadInt64();
                    q.Seconds = r.ReadInt32();
                    q.Placed = r.ReadInt32();
                    q.Welded = r.ReadInt32();
                    q.Skipped = r.ReadInt32();
                    q.Total = r.ReadInt32();
                    q.Paid = r.ReadInt64();
                    q.PauseReason = r.ReadString();
                    int n = r.ReadInt32();
                    // Eight fields, the shortest line is 57 bytes: a count past that is not a quote.
                    if (n < 0 || n > (ms.Length - ms.Position) / 57) return false;
                    var lines = new WireRestoreLine[n];
                    for (int i = 0; i < n; i++)
                    {
                        lines[i].Name = r.ReadString();
                        lines[i].Need = r.ReadInt64();
                        lines[i].FromShip = r.ReadInt64();
                        lines[i].FromNode = r.ReadInt64();
                        lines[i].NodeCredits = r.ReadInt64();
                        lines[i].FromBank = r.ReadInt64();
                        lines[i].BankCredits = r.ReadInt64();
                        lines[i].Short = r.ReadInt64();
                    }
                    q.Lines = lines;
                    quote = q;
                    return true;
                }
            }
            catch
            {
                quote = null;
                return false;
            }
        }

        /// <param name="maxCredits">For <see cref="WireRestoreAction.Start"/>, the quoted total the
        /// restore may not charge past; for <see cref="WireRestoreAction.Register"/>, the fee the
        /// player agreed to. Ignored for a stop.</param>
        public static byte[] WriteRestoreRequest(long terminalEntityId, long gridEntityId, WireRestoreAction action, long maxCredits)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.RestoreRequest);
                w.Write(terminalEntityId);
                w.Write(gridEntityId);
                w.Write((byte)action);
                w.Write(maxCredits);
                return ms.ToArray();
            }
        }

        public static bool TryReadRestoreRequest(byte[] data, out long terminalEntityId, out long gridEntityId,
            out WireRestoreAction action, out long maxCredits)
        {
            terminalEntityId = 0;
            gridEntityId = 0;
            action = WireRestoreAction.Start;
            maxCredits = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.RestoreRequest) return false;
                    long t = r.ReadInt64();
                    long g = r.ReadInt64();
                    byte a = r.ReadByte();
                    if (a < (byte)WireRestoreAction.Start || a > (byte)WireRestoreAction.Stop) return false;
                    long c = r.ReadInt64();
                    terminalEntityId = t;
                    gridEntityId = g;
                    action = (WireRestoreAction)a;
                    maxCredits = c;
                    return true;
                }
            }
            catch { terminalEntityId = 0; gridEntityId = 0; return false; }
        }

        /// <param name="notice">True for a running restore's own news (paused, resumed, done), which
        /// the client shows on the HUD; false for the answer to a request, shown in a box.</param>
        public static byte[] WriteRestoreResult(long gridEntityId, bool ok, bool notice, string message, long balanceAfter)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                WriteHeader(w, WireMessageKind.RestoreResult);
                w.Write(gridEntityId);
                w.Write(ok);
                w.Write(notice);
                w.Write(message ?? "");
                w.Write(balanceAfter);
                return ms.ToArray();
            }
        }

        public static bool TryReadRestoreResult(byte[] data, out long gridEntityId, out bool ok, out bool notice,
            out string message, out long balanceAfter)
        {
            gridEntityId = 0;
            ok = false;
            notice = false;
            message = null;
            balanceAfter = 0;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8))
                {
                    WireMessageKind kind;
                    if (!ReadHeader(r, out kind) || kind != WireMessageKind.RestoreResult) return false;
                    long g = r.ReadInt64();
                    bool k = r.ReadBoolean();
                    bool n = r.ReadBoolean();
                    string m = r.ReadString();
                    long b = r.ReadInt64();
                    gridEntityId = g;
                    ok = k;
                    notice = n;
                    message = m;
                    balanceAfter = b;
                    return true;
                }
            }
            catch { ok = false; message = null; return false; }
        }
    }
}
