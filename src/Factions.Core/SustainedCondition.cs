using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Confirms a per-key condition only once it has held continuously for a duration. Each
    /// observation reports whether the condition is true right now; a false observation forgets the
    /// key, so a transient state never accumulates toward confirmation. A key is tracked only while
    /// its condition is being observed true, and is dropped on confirmation, on a false observation
    /// and on <see cref="Forget"/>.
    /// </summary>
    public sealed class SustainedCondition<TKey>
    {
        private readonly Dictionary<TKey, DateTime> _since;
        private readonly TimeSpan _duration;

        public SustainedCondition(TimeSpan duration) : this(duration, null) { }

        public SustainedCondition(TimeSpan duration, IEqualityComparer<TKey> comparer)
        {
            _duration = duration;
            _since = comparer == null ? new Dictionary<TKey, DateTime>() : new Dictionary<TKey, DateTime>(comparer);
        }

        /// <summary>Keys whose condition is currently being timed.</summary>
        public int Count { get { return _since.Count; } }

        /// <summary>Records one observation of <paramref name="key"/>. Returns true exactly once per
        /// unbroken run, on the first observation at or past the duration; the key is then dropped,
        /// so a condition that is still true afterwards starts a new run.</summary>
        public bool Observe(TKey key, bool holds, DateTime now)
        {
            if (!holds)
            {
                _since.Remove(key);
                return false;
            }

            DateTime since;
            if (!_since.TryGetValue(key, out since))
            {
                since = now;
                _since[key] = now;
            }
            if (now - since < _duration) return false;

            _since.Remove(key);
            return true;
        }

        public void Forget(TKey key)
        {
            _since.Remove(key);
        }
    }
}
