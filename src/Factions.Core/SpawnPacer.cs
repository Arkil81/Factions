using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Keeps Factions' own spawns apart: one at a time, and the next only a spacing after the
    /// previous has landed (operator, 2026-09-25: 1 s, fleets and stations alike).
    ///
    /// <para><b>Why.</b> A spawn is one of the heaviest frames the server has. The hitch probe put a
    /// Naginata spawn at 158-175 ms even with its projection cleared. Over 2026-09-24/25, 221 of 1,403
    /// spawns were followed by another within a second, in 163 bursts of 2-6, mostly one convoy's hulls
    /// together. Two spawns in one frame cost the sum of both.</para>
    ///
    /// <para><b>First come, first served.</b> A spawn that is refused joins the back of the queue
    /// and is granted when it reaches the head. Callers poll every tick, so a key that stops asking
    /// for <see cref="QueueStaleSeconds"/> has gone (its fleet resolved, the player left) and is
    /// dropped rather than holding the queue. A spawn that never reports landing is released after
    /// <see cref="InFlightStaleSeconds"/>, so a lost callback cannot stop every later spawn.</para>
    ///
    /// <para><b>The gap scales with what the last spawn cost</b> (operator, 2026-10-03, after N600 lagged:
    /// a Naginata@2 landing is a ~460 ms frame, and the fixed 1 s let three land in three seconds). The
    /// host reports the landing frame's measured duration; the next spawn then waits long enough that
    /// spawning takes at most <see cref="MaxSpawnDuty"/> of wall time — a 460 ms landing buys ~9 s, a
    /// 40 ms one the plain spacing. Capped at <see cref="MaxGapSeconds"/> so one freak frame cannot
    /// stall every spawn.</para>
    ///
    /// <para><b>A player's own spawn goes first.</b> <see cref="Prioritise"/> moves a key ahead of
    /// every ordinary one, behind earlier prioritised keys: a fleet a player has closed on to sync
    /// distance must land while they are still beside it (FAC-539). It still waits for the spawn in
    /// flight and for the gap, which protect the frame.</para>
    ///
    /// <para>Game thread only; not synchronised.</para>
    /// </summary>
    public sealed class SpawnPacer
    {
        public double SpacingSeconds;
        /// <summary>The share of wall time spawn landings may take: the gap after a landing that took
        /// <c>t</c> is at least <c>t / MaxSpawnDuty</c>.</summary>
        public const double MaxSpawnDuty = 0.05;
        public const double MaxGapSeconds = 30.0;
        public double QueueStaleSeconds = 10.0;
        public double InFlightStaleSeconds = 30.0;

        private sealed class Waiting
        {
            public DateTime Since;
            public DateTime LastAsked;
        }

        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, Waiting> _waiting = new Dictionary<string, Waiting>(StringComparer.Ordinal);
        private readonly HashSet<string> _priority = new HashSet<string>(StringComparer.Ordinal);
        private string _inFlight;
        private DateTime _inFlightSince;
        private DateTime _lastLanded = DateTime.MinValue;
        private string _lastLandedKey;
        private double _gapSeconds;

        // window counters
        public int Granted { get; private set; }
        /// <summary>Spawns that had to wait at least one tick.</summary>
        public int Queued { get; private set; }
        public int MaxQueue { get; private set; }
        public double MaxWaitSeconds { get; private set; }
        public int StaleInFlight { get; private set; }
        public int DroppedFromQueue { get; private set; }
        /// <summary>Keys moved to the front by <see cref="Prioritise"/>.</summary>
        public int Prioritised { get; private set; }
        /// <summary>The longest gap a landing's cost imposed this window, seconds.</summary>
        public double MaxGapImposedSeconds { get; private set; }

        /// <summary><see cref="StallBackoff.Scale"/>: the gap is divided by it, so spawns are up to four
        /// times as far apart while frames stall.</summary>
        public double Backoff = 1.0;

        /// <summary>The gap the next spawn waits after the last landing, seconds.</summary>
        public double GapSeconds
        {
            get { return Math.Max(SpacingSeconds, _gapSeconds) / Math.Max(StallBackoff.Floor, Math.Min(1.0, Backoff)); }
        }

        public SpawnPacer(double spacingSeconds)
        {
            SpacingSeconds = spacingSeconds;
        }

        public bool Enabled { get { return SpacingSeconds > 0; } }
        public int QueueLength { get { return _order.Count; } }
        public string InFlight { get { return _inFlight; } }

        /// <summary>Asks to start the spawn named <paramref name="key"/> now. True: start it, and
        /// call <see cref="Landed"/> when it lands. False: try again on a later tick.</summary>
        /// <param name="waitedSeconds">When granted, how long the key waited in the queue.</param>
        public bool TryBegin(string key, DateTime now, out double waitedSeconds)
        {
            waitedSeconds = 0;
            if (!Enabled) return true;
            if (string.IsNullOrEmpty(key)) return true;

            Waiting w;
            bool isNew = !_waiting.TryGetValue(key, out w);
            if (isNew)
            {
                w = new Waiting { Since = now, LastAsked = now };
                _waiting[key] = w;
                _order.Add(key);
                if (_order.Count > MaxQueue) MaxQueue = _order.Count;
            }
            w.LastAsked = now;

            if (_inFlight != null && (now - _inFlightSince).TotalSeconds >= InFlightStaleSeconds)
            {
                _inFlight = null;
                _lastLanded = now;
                StaleInFlight++;
            }
            DropGoneFromHead(now, key);

            if (_inFlight != null || (now - _lastLanded).TotalSeconds < GapSeconds || _order[0] != key)
            {
                if (isNew) Queued++;
                return false;
            }

            _order.RemoveAt(0);
            _waiting.Remove(key);
            _priority.Remove(key);
            _inFlight = key;
            _inFlightSince = now;
            Granted++;
            waitedSeconds = (now - w.Since).TotalSeconds;
            if (waitedSeconds > MaxWaitSeconds) MaxWaitSeconds = waitedSeconds;
            return true;
        }

        /// <summary>Takes <paramref name="key"/> out of the queue: its fleet no longer wants the spawn.
        /// Without this a withdrawn key at the head holds every spawn behind it for
        /// <see cref="QueueStaleSeconds"/>. False when the spawn has already started; that one lands.</summary>
        public bool Withdraw(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            if (key == _inFlight) return false;
            if (_waiting.Remove(key)) _order.Remove(key);
            _priority.Remove(key);
            return true;
        }

        /// <summary>Puts <paramref name="key"/> ahead of every ordinary waiting key, behind those
        /// prioritised before it, queueing it if it was not waiting yet. The caller keeps polling
        /// <see cref="TryBegin"/> as usual. No-op for the spawn already in flight.</summary>
        public void Prioritise(string key, DateTime now)
        {
            if (!Enabled || string.IsNullOrEmpty(key) || key == _inFlight) return;
            Waiting w;
            bool waiting = _waiting.TryGetValue(key, out w);
            if (waiting && _priority.Contains(key)) return;
            if (waiting) _order.Remove(key);
            else _waiting[key] = new Waiting { Since = now, LastAsked = now };
            _priority.Add(key);
            int at = 0;
            while (at < _order.Count && _priority.Contains(_order[at])) at++;
            _order.Insert(at, key);
            if (_order.Count > MaxQueue) MaxQueue = _order.Count;
            Prioritised++;
        }

        /// <summary>The spawn has landed (or failed): the next may start a spacing from now.</summary>
        public void Landed(string key, DateTime now)
        {
            if (!Enabled || key == null || _inFlight != key) return;
            _inFlight = null;
            _lastLanded = now;
            _lastLandedKey = key;
            _gapSeconds = 0;
        }

        /// <summary>How long the frame the last landing happened in took, measured by the host once
        /// that frame is over. Lengthens the gap before the next spawn to keep landings within
        /// <see cref="MaxSpawnDuty"/>. Ignored for any key but the last to land.</summary>
        public void ReportLandingFrame(string key, double frameMs)
        {
            if (!Enabled || key == null || key != _lastLandedKey || frameMs <= 0) return;
            _gapSeconds = Math.Min(MaxGapSeconds, frameMs / 1000.0 / MaxSpawnDuty);
            if (_gapSeconds > MaxGapImposedSeconds) MaxGapImposedSeconds = _gapSeconds;
        }

        /// <summary>Whether a key starting with <paramref name="prefix"/> is waiting its turn. A
        /// caller that times out slow spawns uses this to tell a queued spawn from a stuck one.</summary>
        public bool IsQueued(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return false;
            for (int i = 0; i < _order.Count; i++)
                if (_order[i].StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Whether <paramref name="key"/> itself is waiting its turn. Exact and O(1), for a
        /// caller asking about every spawn it holds each frame.</summary>
        public bool IsWaiting(string key)
        {
            return !string.IsNullOrEmpty(key) && _waiting.ContainsKey(key);
        }

        /// <summary>Returns the window's counters and starts the next window.</summary>
        public void ResetWindow()
        {
            Granted = Queued = StaleInFlight = DroppedFromQueue = Prioritised = 0;
            MaxGapImposedSeconds = 0;
            MaxQueue = _order.Count;
            MaxWaitSeconds = 0;
        }

        private void DropGoneFromHead(DateTime now, string asking)
        {
            while (_order.Count > 0)
            {
                string head = _order[0];
                if (head == asking) return;
                Waiting hw;
                if (_waiting.TryGetValue(head, out hw) && (now - hw.LastAsked).TotalSeconds < QueueStaleSeconds) return;
                _order.RemoveAt(0);
                _waiting.Remove(head);
                _priority.Remove(head);
                DroppedFromQueue++;
            }
        }
    }
}
