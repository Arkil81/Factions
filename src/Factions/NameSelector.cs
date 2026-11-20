using System;
using System.Collections.Generic;

namespace Factions
{
    /// <summary>
    /// Selects a station name that no other station in the world currently carries.
    ///
    /// The set of names in use is <b>derived</b> from the live world rather than accumulated:
    /// <see cref="RefreshNamesInUse"/> replaces it wholesale from <see cref="InUseProvider"/>, so a
    /// name belonging to a station that has since been conquered, abandoned or destroyed returns to
    /// the pool. Uniqueness is world-wide, not per faction -- a name is either on a station
    /// somewhere or it is available.
    ///
    /// Selection is deterministic in the faction tag and node id so a node keeps its name across
    /// restarts while the pool is unchanged. When a personality/type pool holds no unused name,
    /// selection falls back to an ordinal-suffixed variant ("Ironwall Command II") rather than
    /// failing, so exhaustion cannot produce an unnamed station.
    /// </summary>
    public sealed class NameSelector
    {
        /// <summary>Upper bound on the ordinal search. Far above any plausible reuse depth; exists
        /// only so a pathological catalogue cannot spin.</summary>
        private const int MaxOrdinal = 500;

        private readonly string _cataloguePath;
        private readonly Action<string> _warn;
        private Dictionary<string, List<StationNameCatalogue.CatalogueName>> _namesByPersonalityType;
        private readonly HashSet<string> _inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);
        private bool _refreshedOnExhaustion;
        private bool _initialized;

        /// <summary>
        /// Supplies every station name currently carried in the world, tag-free. Set by the host
        /// once the world is loaded; until then the in-use set stays empty and selection behaves as
        /// if the world were unnamed.
        /// </summary>
        public Func<IEnumerable<string>> InUseProvider { get; set; }

        /// <summary>Number of names currently held as in use. Diagnostic only.</summary>
        public int NamesInUseCount { get { return _inUse.Count; } }

        public NameSelector(string cataloguePath, Action<string> warn)
        {
            _cataloguePath = cataloguePath;
            _warn = warn;
        }

        /// <summary>
        /// Loads the master catalogue from disk. Safe to call multiple times (idempotent after
        /// first success).
        /// </summary>
        public bool Initialize()
        {
            if (_initialized) return true;

            try
            {
                if (!System.IO.File.Exists(_cataloguePath))
                {
                    Warn("NameSelector: catalogue not found at " + _cataloguePath);
                    return false;
                }

                string catalogueJson = System.IO.File.ReadAllText(_cataloguePath);
                var catalogue = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<StationNameCatalogue.MasterCatalogue>(catalogueJson);
                if (catalogue == null || catalogue.Entries == null || catalogue.Entries.Count == 0)
                {
                    Warn("NameSelector: catalogue is empty.");
                    return false;
                }

                // Build lookup: "personality|stationType" -> list of names.
                _namesByPersonalityType = new Dictionary<string, List<StationNameCatalogue.CatalogueName>>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (StationNameCatalogue.CatalogueEntry entry in catalogue.Entries)
                {
                    string key = entry.PersonalityId + "|" + entry.StationType;
                    _namesByPersonalityType[key] = entry.Names ?? new List<StationNameCatalogue.CatalogueName>();
                }

                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Warn("NameSelector.Initialize failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Replaces the in-use set from <see cref="InUseProvider"/>. This is the only way a name
        /// leaves the set, and the reason names are not leaked when a station changes hands or dies.
        /// </summary>
        public void RefreshNamesInUse()
        {
            if (InUseProvider == null) return;
            IEnumerable<string> names;
            try
            {
                names = InUseProvider();
            }
            catch (Exception ex)
            {
                Warn("NameSelector: in-use provider failed: " + ex.Message);
                return;
            }

            _inUse.Clear();
            if (names != null)
            {
                foreach (string name in names)
                    if (!string.IsNullOrEmpty(name)) _inUse.Add(name);
            }
            _refreshedOnExhaustion = false;
        }

        /// <summary>
        /// Selects a name for a station, deterministic in <paramref name="factionTag"/> and
        /// <paramref name="nodeId"/>. The caller must commit the result to the node's DisplayName:
        /// the returned name is held in the in-use set only until the next
        /// <see cref="RefreshNamesInUse"/>, which re-derives it from the world.
        ///
        /// Returns null only when the catalogue is missing or holds no usable name at all for the
        /// personality/type -- never because the pool ran out.
        /// </summary>
        public string SelectName(string factionTag, string personalityId, string stationType, string nodeId)
        {
            if (!_initialized || string.IsNullOrEmpty(factionTag) || string.IsNullOrEmpty(nodeId))
                return null;

            if (string.IsNullOrEmpty(personalityId))
                personalityId = "default";

            string key = personalityId + "|" + stationType;
            List<StationNameCatalogue.CatalogueName> candidates;
            if (!_namesByPersonalityType.TryGetValue(key, out candidates) || candidates.Count == 0)
            {
                // Any personality with no catalogue entry falls back to the default (Military) pool.
                personalityId = "default";
                key = personalityId + "|" + stationType;
                if (!_namesByPersonalityType.TryGetValue(key, out candidates) || candidates.Count == 0)
                {
                    WarnOnce(key, "NameSelector: no catalogue entry for personality=" + personalityId
                        + ", type=" + stationType);
                    return null;
                }
            }

            // Stable hash rather than string.GetHashCode(), whose result is not a persistence
            // contract across .NET runtimes.
            var rng = new Random(StableHash(factionTag + "|" + nodeId));

            var usable = new List<string>();
            foreach (StationNameCatalogue.CatalogueName cat in candidates)
                if (cat != null && IsCompleteStationName(cat.Name)) usable.Add(cat.Name);

            if (usable.Count == 0)
            {
                WarnOnce(key, "NameSelector: catalogue holds no usable name for personality="
                    + personalityId + ", type=" + stationType);
                return null;
            }

            List<string> available = Available(usable);
            if (available.Count == 0 && TryRefreshOnExhaustion())
                available = Available(usable);

            if (available.Count > 0)
            {
                string selected = available[rng.Next(available.Count)];
                _inUse.Add(selected);
                _refreshedOnExhaustion = false;
                return selected;
            }

            // Pool genuinely exhausted against the live world: disambiguate a catalogue name with
            // the lowest ordinal nobody is using, so the caller always gets a distinct name.
            string stem = usable[rng.Next(usable.Count)];
            for (int n = 2; n <= MaxOrdinal; n++)
            {
                string ordinal = stem + " " + Roman(n);
                if (_inUse.Contains(ordinal)) continue;
                _inUse.Add(ordinal);
                return ordinal;
            }

            WarnOnce(key, "NameSelector: exhausted ordinals for personality=" + personalityId
                + ", type=" + stationType);
            return null;
        }

        private List<string> Available(List<string> usable)
        {
            var available = new List<string>();
            foreach (string name in usable)
                if (!_inUse.Contains(name)) available.Add(name);
            return available;
        }

        /// <summary>
        /// Re-derives the in-use set when the pool looks empty, at most once between successful
        /// selections. An apparently exhausted pool is the one moment a stale set is worth the walk:
        /// the names it is holding may belong to stations that no longer exist.
        /// </summary>
        private bool TryRefreshOnExhaustion()
        {
            if (_refreshedOnExhaustion || InUseProvider == null) return false;
            RefreshNamesInUse();
            _refreshedOnExhaustion = true;
            return true;
        }

        private void Warn(string message)
        {
            if (_warn != null) _warn(message);
        }

        /// <summary>Logs once per key per process. A pool problem repeats for every node that hits
        /// it, which is how one broken bucket produced thousands of identical warnings a day.</summary>
        private void WarnOnce(string key, string message)
        {
            if (!_warned.Add(key)) return;
            Warn(message);
        }

        private static bool IsCompleteStationName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.Trim().IndexOf(' ') > 0;
        }

        private static readonly int[] RomanValues = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        private static readonly string[] RomanNumerals =
            { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };

        internal static string Roman(int value)
        {
            if (value <= 0) return string.Empty;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < RomanValues.Length; i++)
            {
                while (value >= RomanValues[i])
                {
                    sb.Append(RomanNumerals[i]);
                    value -= RomanValues[i];
                }
            }
            return sb.ToString();
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 23;
                for (int i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];
                return hash;
            }
        }
    }
}
