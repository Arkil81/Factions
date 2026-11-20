using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Traffic control's station queues (docs/traffic-control.md, T3, T8): one FIFO queue per station
    /// a player is near, holding arrivals and departures together. <see cref="TrafficPresence"/>
    /// joins fleets to it, pops its heads when the station's neighbourhood has room, and reports the
    /// entries that have waited long enough to divert (an arrival) or to have the delay called (a
    /// departure, or an arrival with nowhere to divert to).
    ///
    /// <para>A cleared fleet is remembered for the station and direction it was cleared for, so it
    /// is admitted ahead of other traffic and does not queue again while it is still arriving at, or
    /// still leaving, that station. Game thread only; not persisted: after a restart nothing is held.</para>
    /// </summary>
    public sealed class StationQueues
    {
        public sealed class Entry
        {
            public string Key;
            public string Station;
            public bool Departure;
            public DateTime JoinedUtc;
            /// <summary>An arrival whose stop can be taken elsewhere: it diverts after the wait.</summary>
            public bool Divertable;
            public bool DelayReported;
            public double CostMs;
            /// <summary>Holding visibly as a spawned grid: its cost is already in use, so clearing it
            /// costs nothing more.</summary>
            public bool Materialised;
        }

        private struct ClearedFor
        {
            public string Station;
            public bool Departure;
        }

        private readonly Dictionary<string, List<Entry>> _byStation = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, ClearedFor> _cleared = new Dictionary<string, ClearedFor>(StringComparer.Ordinal);

        public int Count { get { return _byKey.Count; } }

        public Entry Find(string key)
        {
            Entry e;
            return key != null && _byKey.TryGetValue(key, out e) ? e : null;
        }

        public bool IsCleared(string key, string station, bool departure)
        {
            ClearedFor c;
            return key != null && _cleared.TryGetValue(key, out c)
                && string.Equals(c.Station, station, StringComparison.Ordinal) && c.Departure == departure;
        }

        /// <summary>Whether <paramref name="key"/> holds a clearance at any station.</summary>
        public bool HasClearance(string key)
        {
            return key != null && _cleared.ContainsKey(key);
        }

        /// <summary>Joins <paramref name="key"/> to the back of <paramref name="station"/>'s queue, or
        /// refreshes its cost when it is already there. A fleet already queued at another station, or
        /// in the other direction, moves to the back of this one.</summary>
        public Entry Join(string key, string station, bool departure, bool divertable, double costMs, DateTime now, out bool joined,
                          bool materialised = false)
        {
            joined = false;
            Entry e = Find(key);
            if (e != null && (e.Station != station || e.Departure != departure)) { Remove(key); e = null; }
            if (e == null)
            {
                e = new Entry { Key = key, Station = station, Departure = departure, JoinedUtc = now };
                _byKey[key] = e;
                List<Entry> q;
                if (!_byStation.TryGetValue(station, out q)) _byStation[station] = q = new List<Entry>();
                q.Add(e);
                joined = true;
            }
            e.Divertable = divertable && (joined || e.Divertable);
            e.CostMs = costMs;
            e.Materialised = materialised;
            return e;
        }

        /// <summary>1-based place in its station's queue; 0 when not queued.</summary>
        public int Position(string key)
        {
            Entry e = Find(key);
            if (e == null) return 0;
            return _byStation[e.Station].IndexOf(e) + 1;
        }

        public IReadOnlyList<Entry> Queue(string station)
        {
            List<Entry> q;
            return station != null && _byStation.TryGetValue(station, out q) ? (IReadOnlyList<Entry>)q : new List<Entry>();
        }

        public IEnumerable<string> Stations { get { return _byStation.Keys; } }

        public void Remove(string key)
        {
            Entry e = Find(key);
            if (e == null) return;
            _byKey.Remove(key);
            List<Entry> q = _byStation[e.Station];
            q.Remove(e);
            if (q.Count == 0) _byStation.Remove(e.Station);
        }

        /// <summary>Takes the entry out of its queue and remembers what it was cleared for.</summary>
        public void Clear(Entry e)
        {
            if (e == null) return;
            Remove(e.Key);
            _cleared[e.Key] = new ClearedFor { Station = e.Station, Departure = e.Departure };
        }

        /// <summary>An arrival with nowhere to divert to waits like a departure from here on.</summary>
        public void MarkUndivertable(string key)
        {
            Entry e = Find(key);
            if (e != null) e.Divertable = false;
        }

        /// <summary>Drops every queued entry not in <paramref name="queued"/> and every clearance not
        /// in <paramref name="cleared"/>: the fleet resolved, was lost, changed its leg, or its
        /// station has no player near it any more.</summary>
        public void Retain(ISet<string> queued, ISet<string> cleared)
        {
            var drop = new List<string>();
            foreach (string k in _byKey.Keys) if (queued == null || !queued.Contains(k)) drop.Add(k);
            foreach (string k in drop) Remove(k);
            drop.Clear();
            foreach (string k in _cleared.Keys) if (cleared == null || !cleared.Contains(k)) drop.Add(k);
            foreach (string k in drop) _cleared.Remove(k);
        }
    }
}
