using System;
using System.Collections.Generic;
using System.Text;

namespace Factions.Core
{
    /// <summary>
    /// Last value per key, so a trace writes a line only when the value moves. The relation trace
    /// uses one keyed by (viewer, owner) for RELATION_RESOLVE and one keyed by player faction for
    /// RELATION_ROWS.
    /// </summary>
    public sealed class ChangeBook
    {
        private readonly Dictionary<string, string> _last = new Dictionary<string, string>(StringComparer.Ordinal);

        public int Count { get { return _last.Count; } }

        /// <summary>Records <paramref name="value"/> under <paramref name="key"/>. True when it
        /// differs from what was recorded before, or nothing was; <paramref name="previous"/> is the
        /// old value, null for a key seen for the first time.</summary>
        public bool Changed(string key, string value, out string previous)
        {
            if (_last.TryGetValue(key, out previous) && string.Equals(previous, value, StringComparison.Ordinal))
                return false;
            _last[key] = value;
            return true;
        }

        public void Clear() { _last.Clear(); }
    }

    /// <summary>
    /// One scanner send's relation shifts, grouped by owner and transition, for SCANNER_RELATION.
    /// A contact the viewer was never sent counts as <see cref="First"/>, not as a shift, so a
    /// fresh sphere does not read as every contact changing colour.
    /// </summary>
    public sealed class RelationShiftTally
    {
        private readonly Dictionary<string, int> _shifts = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Sent { get; private set; }
        public int First { get; private set; }
        public int Changed { get; private set; }

        /// <param name="previous">What this viewer was last sent for the contact, or null if never.</param>
        public void Add(string ownerTag, string previous, string now)
        {
            Sent++;
            if (previous == null) { First++; return; }
            if (string.Equals(previous, now, StringComparison.Ordinal)) return;
            Changed++;
            string key = (string.IsNullOrEmpty(ownerTag) ? "-" : ownerTag) + ":" + previous + ">" + now;
            int n;
            _shifts.TryGetValue(key, out n);
            _shifts[key] = n + 1;
        }

        /// <summary><c>OWNER:From>To*count</c>, comma-separated in ordinal order, or <c>-</c>.</summary>
        public string FormatShifts()
        {
            if (_shifts.Count == 0) return "-";
            var keys = new List<string>(_shifts.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(keys[i]).Append('*').Append(_shifts[keys[i]]);
            }
            return sb.ToString();
        }
    }
}
