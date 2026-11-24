using System.Globalization;
using System.Threading;

namespace Factions.Core
{
    /// <summary>
    /// Names the part of the Factions update a stalled game thread is in, while it is stalled.
    ///
    /// <para><b>Why.</b> Production Main froze for 26.7 s at 18:56:13Z on 2026-11-22. The hitch probe
    /// charged the frame to the Factions plugin, but on a Nexus agent nothing splits that plugin's
    /// update into parts, and the per-frame record only lands after the frame ends. A thread that is
    /// not the game thread can read which section the game thread last entered, and report it during
    /// the stall, including one spent waiting on a lock, a file or the network.</para>
    ///
    /// <para><b>Two threads.</b> The game thread calls <see cref="Enter"/> at each section boundary:
    /// three plain writes and one volatile one, no allocation, no lock. A watchdog thread calls
    /// <see cref="Poll"/> a few times a second and gets at most one <see cref="StallEvent"/> back.
    /// <see cref="Arm"/> and <see cref="Disarm"/> may be called from either.</para>
    ///
    /// <para><b>Reading the result.</b> A stall the watchdog only notices once it is long past the
    /// threshold, with a poll gap about as long, means the watchdog was stopped too: the whole
    /// process was suspended (a blocking collection, paging), not the game thread busy in a
    /// section. <c>gc2</c> carries the gen-2 collection count across the stall for the same
    /// question.</para>
    /// </summary>
    public sealed class StallWatch
    {
        /// <summary>The game thread is not inside the Factions update: SE, Torch or another plugin.</summary>
        public const string Outside = "outside";

        private readonly long _thresholdTicks;
        private readonly long _repeatTicks;
        private readonly double _msPerTick;

        // Written by the game thread only.
        private string _section = Outside;
        private long _sectionStart;
        private long _seq;
        private long _endSeq = -1;
        private long _endTicks;

        // Written by the watchdog, read by the game thread.
        private long _flaggedSeq = -1;

        // Written by Arm/Disarm. Polls report nothing until the game thread has entered a section
        // after the arm, so a stale boundary from before it cannot read as a stall.
        private long _armSeq = long.MaxValue;

        // The watchdog's own.
        private long _stuckSeq = -1;
        private long _stuckStart;
        private string _stuckSection;
        private long _lastReport;
        private int _gc2AtStuck;
        private long _lastPoll;
        private int _lastGc2;

        public StallWatch(double thresholdMs, double repeatMs, long ticksPerSecond)
        {
            _msPerTick = 1000.0 / ticksPerSecond;
            _thresholdTicks = (long)(thresholdMs / _msPerTick);
            _repeatTicks = (long)(repeatMs / _msPerTick);
        }

        /// <summary>Game thread: <paramref name="section"/> starts now. Pass string literals, so
        /// nothing is allocated.</summary>
        public void Enter(string section, long now)
        {
            long s = _seq;
            if (Volatile.Read(ref _flaggedSeq) == s)
            {
                _endTicks = now;
                Volatile.Write(ref _endSeq, s);
            }
            Volatile.Write(ref _sectionStart, now);
            Volatile.Write(ref _section, section);
            Volatile.Write(ref _seq, s + 1);
        }

        public void Arm()
        {
            Volatile.Write(ref _armSeq, Volatile.Read(ref _seq));
        }

        public void Disarm()
        {
            Volatile.Write(ref _armSeq, long.MaxValue);
        }

        /// <summary>Watchdog: what changed since the last poll. <paramref name="gc2"/> is the
        /// process's gen-2 collection count now.</summary>
        public StallEvent Poll(long now, int gc2)
        {
            bool first = _lastPoll == 0;
            long gap = first ? 0 : now - _lastPoll;
            int gc2Before = first ? gc2 : _lastGc2;
            _lastPoll = now;
            _lastGc2 = gc2;

            long s1 = Volatile.Read(ref _seq);
            string section = Volatile.Read(ref _section);
            long start = Volatile.Read(ref _sectionStart);
            long s2 = Volatile.Read(ref _seq);
            long armSeq = Volatile.Read(ref _armSeq);

            if (armSeq == long.MaxValue) { _stuckSeq = -1; return StallEvent.None; }

            if (_stuckSeq >= 0)
            {
                if (s1 == _stuckSeq && s2 == _stuckSeq)
                {
                    if (now - _lastReport < _repeatTicks) return StallEvent.None;
                    _lastReport = now;
                    return new StallEvent(StallEventKind.Ongoing, _stuckSection, Ms(now - _stuckStart), Ms(gap),
                                          gc2 - _gc2AtStuck, false);
                }
                bool exact = Volatile.Read(ref _endSeq) == _stuckSeq;
                long end = exact ? Volatile.Read(ref _endTicks) : now;
                var ended = new StallEvent(StallEventKind.End, _stuckSection, Ms(end - _stuckStart), Ms(gap),
                                           gc2 - _gc2AtStuck, exact);
                _stuckSeq = -1;
                return ended;
            }

            if (s1 != s2 || s1 <= armSeq) return StallEvent.None;
            if (now - start < _thresholdTicks) return StallEvent.None;

            _stuckSeq = s1;
            _stuckStart = start;
            _stuckSection = section;
            _lastReport = now;
            _gc2AtStuck = gc2Before;
            // The game thread may already have left this section; then the end is the poll that sees it.
            Volatile.Write(ref _flaggedSeq, s1);
            return new StallEvent(StallEventKind.Stall, section, Ms(now - start), Ms(gap), gc2 - gc2Before, false);
        }

        private double Ms(long ticks)
        {
            return ticks * _msPerTick;
        }
    }

    public enum StallEventKind { None, Stall, Ongoing, End }

    public struct StallEvent
    {
        public static readonly StallEvent None = new StallEvent(StallEventKind.None, null, 0, 0, 0, false);

        public readonly StallEventKind Kind;
        public readonly string Section;
        /// <summary>Stall and Ongoing: how long the section has run. End: how long it ran.</summary>
        public readonly double Ms;
        /// <summary>Time since the watchdog's previous poll.</summary>
        public readonly double PollGapMs;
        /// <summary>Gen-2 collections since the last poll before the stall was seen.</summary>
        public readonly int Gc2;
        /// <summary>End only: the game thread stamped the moment it left the section. When false the
        /// end is the poll that saw it gone, up to one poll interval late.</summary>
        public readonly bool ExactEnd;

        public StallEvent(StallEventKind kind, string section, double ms, double pollGapMs, int gc2, bool exactEnd)
        {
            Kind = kind;
            Section = section;
            Ms = ms;
            PollGapMs = pollGapMs;
            Gc2 = gc2;
            ExactEnd = exactEnd;
        }

        /// <summary>The Torch log line; empty for <see cref="StallEventKind.None"/>.</summary>
        public string Format()
        {
            switch (Kind)
            {
                case StallEventKind.Stall:
                    return "FACTIONS_STALL section=" + Section + " elapsedMs=" + Ms.ToString("F0", CultureInfo.InvariantCulture)
                           + " pollGapMs=" + PollGapMs.ToString("F0", CultureInfo.InvariantCulture) + " gc2=+" + Gc2;
                case StallEventKind.Ongoing:
                    return "FACTIONS_STALL_ONGOING section=" + Section + " elapsedMs=" + Ms.ToString("F0", CultureInfo.InvariantCulture)
                           + " pollGapMs=" + PollGapMs.ToString("F0", CultureInfo.InvariantCulture) + " gc2=+" + Gc2;
                case StallEventKind.End:
                    return "FACTIONS_STALL_END section=" + Section + " ms=" + Ms.ToString("F0", CultureInfo.InvariantCulture)
                           + " end=" + (ExactEnd ? "exact" : "polled") + " pollGapMs=" + PollGapMs.ToString("F0", CultureInfo.InvariantCulture)
                           + " gc2=+" + Gc2;
                default:
                    return "";
            }
        }
    }
}
