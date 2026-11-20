using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Accumulated time and call count per key, for attributing a measured cost to the things that
    /// incurred it — grids, entity types.
    ///
    /// <para><b>Not thread-safe, on purpose.</b> The host keeps one tally per thread that writes and
    /// merges them on the game thread between frames. A shared concurrent map would put contention on
    /// the very update loop being measured.</para>
    ///
    /// <para>Ticks are whatever unit the caller uses (Stopwatch ticks in the host); this class never
    /// converts them.</para>
    /// </summary>
    public sealed class CostTally<TKey>
    {
        public sealed class Entry
        {
            public long Ticks;
            public long Calls;
        }

        private readonly Dictionary<TKey, Entry> _map;

        public CostTally() : this(null) { }

        public CostTally(IEqualityComparer<TKey> comparer)
        {
            _map = comparer == null ? new Dictionary<TKey, Entry>() : new Dictionary<TKey, Entry>(comparer);
        }

        public int Count { get { return _map.Count; } }

        public void Add(TKey key, long ticks)
        {
            Entry e;
            if (!_map.TryGetValue(key, out e))
            {
                e = new Entry();
                _map[key] = e;
            }
            e.Ticks += ticks;
            e.Calls++;
        }

        /// <summary>Adds every entry of this tally into <paramref name="target"/>. Leaves this one
        /// unchanged; the caller clears it when it is done.</summary>
        public void MergeInto(CostTally<TKey> target)
        {
            if (target == null || ReferenceEquals(target, this)) return;
            foreach (KeyValuePair<TKey, Entry> kv in _map)
            {
                Entry e;
                if (!target._map.TryGetValue(kv.Key, out e))
                {
                    e = new Entry();
                    target._map[kv.Key] = e;
                }
                e.Ticks += kv.Value.Ticks;
                e.Calls += kv.Value.Calls;
            }
        }

        public bool TryGet(TKey key, out Entry entry)
        {
            return _map.TryGetValue(key, out entry);
        }

        public long TotalTicks
        {
            get
            {
                long t = 0;
                foreach (Entry e in _map.Values) t += e.Ticks;
                return t;
            }
        }

        public long TotalCalls
        {
            get
            {
                long c = 0;
                foreach (Entry e in _map.Values) c += e.Calls;
                return c;
            }
        }

        public IEnumerable<KeyValuePair<TKey, Entry>> Entries { get { return _map; } }

        /// <summary>The <paramref name="n"/> costliest keys, most expensive first. Ranked by total
        /// ticks, not by average: something cheap that runs every frame for every block is what eats
        /// a window, and an average cannot see call count.</summary>
        public List<KeyValuePair<TKey, Entry>> Top(int n)
        {
            var all = new List<KeyValuePair<TKey, Entry>>(_map);
            all.Sort((a, b) => b.Value.Ticks.CompareTo(a.Value.Ticks));
            if (n >= 0 && all.Count > n) all.RemoveRange(n, all.Count - n);
            return all;
        }

        /// <summary>Sums every key's ticks into the bucket <paramref name="bucketOf"/> assigns it,
        /// over the whole tally rather than a top-N. <paramref name="into"/> is indexed by bucket;
        /// a bucket outside its bounds is skipped rather than thrown on.</summary>
        public void SumBy(System.Func<TKey, int> bucketOf, long[] into)
        {
            if (bucketOf == null || into == null) return;
            foreach (KeyValuePair<TKey, Entry> kv in _map)
            {
                int b = bucketOf(kv.Key);
                if (b >= 0 && b < into.Length) into[b] += kv.Value.Ticks;
            }
        }

        public void Clear()
        {
            _map.Clear();
        }
    }
}
