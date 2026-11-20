using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>
    /// A prefab's identity as a block multiset, so a live hull can be matched back to the catalogue
    /// entry it was built from without trusting its name.
    ///
    /// <para>The host maps two very different things onto the same census — a prefab's saved
    /// <c>MyObjectBuilder_CubeGrid</c>s and a live grid group's <c>MySlimBlock</c>s — and the whole
    /// point is that both produce the same string for the same ship. That is why the folding, the
    /// ordering and the formatting all live here rather than at either call site: two host-side
    /// implementations of "sort and concatenate" would drift, and the failure would be a silent
    /// no-match rather than an error.</para>
    ///
    /// <para>What is in it: the count of every distinct block definition id in the whole mechanical
    /// group, plus the group's grid count and its grid size class. What is deliberately not in it:
    /// block positions, orientation, ownership, names, paint, damage and inventory. A ship rebuilt
    /// block-for-block in a different order is the same prefab; a ship one block heavier is not.</para>
    ///
    /// <para>Provenance: FAC-283.</para>
    /// </summary>
    public static class PrefabFingerprint
    {
        /// <summary>The value a caller with nothing to fingerprint gets back — never a match for
        /// anything, including itself, because <see cref="Matches"/> refuses empties outright.</summary>
        public const string None = "";

        /// <summary>
        /// Folds a block census into the canonical fingerprint.
        ///
        /// <para><paramref name="census"/> is (block definition id, count) pairs over every grid in
        /// the group, in any order and with repeats — the host enumerates blocks, not a tidy
        /// dictionary, and one definition id legitimately arrives many times. Pairs are summed, so
        /// the result cannot depend on enumeration order.</para>
        ///
        /// <para>The output shape is <c>&lt;grids&gt;:&lt;size&gt;:&lt;blocks&gt;:&lt;defs&gt;:&lt;hex&gt;</c>.
        /// The three counts are carried in the clear beside the hash rather than folded into it, so
        /// a hash collision alone cannot produce a match: two hulls would additionally have to agree
        /// on grid count, block count and distinct-definition count. It also makes a stored
        /// fingerprint readable in <c>prefab_catalogue.json</c>, which matters because the only way
        /// to see why a hull did not match is to compare two of these by eye.</para>
        /// </summary>
        /// <param name="census">Block definition id -> count, unsummed and unordered.</param>
        /// <param name="gridSizeClass">The group's size class as the catalogue states it
        /// ("Large"/"Small"/"Station"). Part of the identity: the same block list at small scale is
        /// a different ship.</param>
        /// <param name="gridCount">Grids in the mechanical group. A hull and the same hull with its
        /// subgrid welded on carry different counts and must not match.</param>
        public static string Compute(
            IEnumerable<KeyValuePair<string, long>> census, string gridSizeClass, int gridCount)
        {
            if (census == null) return None;

            var folded = new Dictionary<string, long>(StringComparer.Ordinal);
            long totalBlocks = 0;
            foreach (KeyValuePair<string, long> entry in census)
            {
                if (string.IsNullOrEmpty(entry.Key) || entry.Value <= 0) continue;
                long cur;
                folded.TryGetValue(entry.Key, out cur);
                folded[entry.Key] = cur + entry.Value;
                totalBlocks += entry.Value;
            }
            if (folded.Count == 0) return None;

            var keys = new List<string>(folded.Keys);
            keys.Sort(StringComparer.Ordinal);

            string size = string.IsNullOrEmpty(gridSizeClass) ? "?" : gridSizeClass.Trim();
            var canonical = new StringBuilder();
            canonical.Append(gridCount.ToString(CultureInfo.InvariantCulture)).Append('|').Append(size).Append('|');
            for (int i = 0; i < keys.Count; i++)
            {
                canonical.Append(keys[i]).Append('=')
                         .Append(folded[keys[i]].ToString(CultureInfo.InvariantCulture)).Append(';');
            }

            var sb = new StringBuilder();
            sb.Append(gridCount.ToString(CultureInfo.InvariantCulture)).Append(':')
              .Append(size).Append(':')
              .Append(totalBlocks.ToString(CultureInfo.InvariantCulture)).Append(':')
              .Append(keys.Count.ToString(CultureInfo.InvariantCulture)).Append(':')
              .Append(Hash64(canonical.ToString()).ToString("x16", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>Whether two fingerprints name the same ship. An empty fingerprint on either side
        /// is never a match: it means "not computed", and a catalogue entry that predates the field
        /// must refuse a hull rather than accept every hull.</summary>
        public static bool Matches(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        /// <summary>FNV-1a over UTF-16 code units. Chosen because it is fixed for all time: a
        /// fingerprint is persisted in <c>prefab_catalogue.json</c> and compared against one
        /// computed by a later build, so the BCL's <c>string.GetHashCode</c> — randomised per
        /// process since .NET Core — cannot be used here.</summary>
        private static ulong Hash64(string text)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offsetBasis;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                hash ^= (byte)(c & 0xFF);
                hash *= prime;
                hash ^= (byte)((c >> 8) & 0xFF);
                hash *= prime;
            }
            return hash;
        }
    }
}
