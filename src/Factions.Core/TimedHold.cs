using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Keys held until a deadline. A hold is armed for a duration, answers whether it is still in
    /// force, and drops itself once it has expired — either on the query that finds it expired or
    /// on a periodic <see cref="Prune"/>. The set therefore never grows past the number of keys
    /// armed inside one hold duration.
    /// </summary>
    public sealed class TimedHold<TKey>
    {
        private struct Entry
        {
            public DateTime Since;
            public DateTime Until;
        }

        private readonly Dictionary<TKey, Entry> _held;

        public TimedHold() : this(null) { }

        public TimedHold(IEqualityComparer<TKey> comparer)
        {
            _held = comparer == null ? new Dictionary<TKey, Entry>() : new Dictionary<TKey, Entry>(comparer);
        }

        /// <summary>Keys currently tracked, expired ones included until the next query or prune.</summary>
        public int Count { get { return _held.Count; } }

        /// <summary>Arms, or re-arms, a hold on <paramref name="key"/> lasting <paramref name="duration"/> from <paramref name="now"/>.</summary>
        public void Arm(TKey key, DateTime now, TimeSpan duration)
        {
            _held[key] = new Entry { Since = now, Until = now + duration };
        }

        /// <summary>True while the hold on <paramref name="key"/> is in force. An expired hold is dropped here.</summary>
        public bool IsHeld(TKey key, DateTime now)
        {
            Entry e;
            if (!_held.TryGetValue(key, out e)) return false;
            if (now < e.Until) return true;
            _held.Remove(key);
            return false;
        }

        /// <summary>When the hold on <paramref name="key"/> was armed, if it is tracked at all.</summary>
        public bool TryGetSince(TKey key, out DateTime since)
        {
            Entry e;
            if (_held.TryGetValue(key, out e)) { since = e.Since; return true; }
            since = default(DateTime);
            return false;
        }

        public void Release(TKey key)
        {
            _held.Remove(key);
        }

        /// <summary>Drops every hold that has expired by <paramref name="now"/>. Allocates only when there is something to drop.</summary>
        public void Prune(DateTime now)
        {
            if (_held.Count == 0) return;
            List<TKey> expired = null;
            foreach (KeyValuePair<TKey, Entry> kv in _held)
                if (now >= kv.Value.Until) (expired ?? (expired = new List<TKey>())).Add(kv.Key);
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++) _held.Remove(expired[i]);
        }

        public void Clear()
        {
            _held.Clear();
        }
    }
}
