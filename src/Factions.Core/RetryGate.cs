using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Per-entity hold-off: once armed, an entity is refused until the interval has
    /// passed. Used where a refusal or an abort would otherwise be retried on the next frame, so
    /// a condition that keeps failing costs one attempt per interval instead of one per frame.
    /// Not thread-safe; the flight controller uses it from the game thread only.</summary>
    public sealed class RetryGate
    {
        private readonly Dictionary<long, DateTime> _until = new Dictionary<long, DateTime>();
        private readonly List<long> _expired = new List<long>();

        public RetryGate(TimeSpan interval)
        {
            Interval = interval;
        }

        public TimeSpan Interval { get; }

        public int Count { get { return _until.Count; } }

        /// <summary>Refuses <paramref name="id"/> until <paramref name="nowUtc"/> plus the
        /// interval. Re-arming restarts the interval.</summary>
        public void Arm(long id, DateTime nowUtc)
        {
            _until[id] = nowUtc + Interval;
        }

        /// <summary>Whether <paramref name="id"/> is still held off. An expired entry is dropped
        /// here, so an entity that is asked about again needs no sweep.</summary>
        public bool Refuses(long id, DateTime nowUtc)
        {
            DateTime until;
            if (!_until.TryGetValue(id, out until)) return false;
            if (nowUtc < until) return true;
            _until.Remove(id);
            return false;
        }

        /// <summary>Drops expired entries once more than <paramref name="threshold"/> are held,
        /// for entities that are never asked about again.</summary>
        public void PruneExpired(DateTime nowUtc, int threshold)
        {
            if (_until.Count < threshold) return;
            _expired.Clear();
            foreach (KeyValuePair<long, DateTime> kv in _until)
                if (nowUtc >= kv.Value) _expired.Add(kv.Key);
            for (int i = 0; i < _expired.Count; i++) _until.Remove(_expired[i]);
            _expired.Clear();
        }
    }
}
