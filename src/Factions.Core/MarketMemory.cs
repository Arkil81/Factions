using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>One remembered price row. Quantity is deliberately absent: a belief about how much
    /// of something a station had is worthless within minutes, and the wire encodes "not known" as
    /// <c>-1</c> rather than pretending to a number.</summary>
    public struct FrozenMarketRow
    {
        public string InvKey;
        /// <summary>Matches <see cref="StationListingSide"/>.</summary>
        public byte Side;
        public int UnitPrice;
    }

    /// <summary>What one observer last saw of one faction's market, at one moment.
    ///
    /// <para>The unit is the <b>faction</b>, not the node, because that is how liveness is scoped:
    /// contact with any of a faction's nodes refreshes every station of theirs the observer has
    /// discovered, and losing contact freezes all of them together. Storing per node would let one
    /// faction's table carry a dozen different ages, which is not a state the world can produce.</para>
    /// </summary>
    public sealed class FrozenFactionMarket
    {
        public DateTime AsOfUtc;
        public readonly Dictionary<string, List<FrozenMarketRow>> Stations =
            new Dictionary<string, List<FrozenMarketRow>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// What each observer remembers of the markets they are no longer in contact with, keyed by SE
    /// identity id and then by faction tag.
    ///
    /// <para>Separate from <see cref="PlayerKnowledgeStore"/> on purpose. That file is the discovery
    /// set — which nodes exist to you — and is compacted on the discovery horizon. This is a belief
    /// about prices, it turns over far faster, and mixing the two would make the discovery file's
    /// size depend on the item catalogue.</para>
    ///
    /// <para><see cref="Freshest"/> takes a list of observer ids and returns the freshest memory
    /// among them, so a faction-shared store can be added later by passing a second id without
    /// changing any caller. <b>Today only the player's own id is passed</b> — there is no
    /// faction-level market memory, so joining a faction does not grant its price history the way
    /// it grants its map. That is a gap, not a design choice; it is recorded in FAC-149.</para>
    /// </summary>
    public sealed class MarketMemory
    {
        private const int SchemaVersion = 1;

        private readonly string _path;
        private readonly Dictionary<long, Dictionary<string, FrozenFactionMarket>> _byObserver =
            new Dictionary<long, Dictionary<string, FrozenFactionMarket>>();
        private bool _dirty;

        /// <param name="storageDir">Null or empty for an in-memory store, which is what the tests
        /// and the offline sim want.</param>
        public MarketMemory(string storageDir)
        {
            _path = string.IsNullOrEmpty(storageDir) ? null : Path.Combine(storageDir, "market_memory.json");
            Load();
        }

        public int ObserverCount { get { return _byObserver.Count; } }

        /// <summary>Records what <paramref name="observerId"/> can currently see of
        /// <paramref name="factionTag"/>. Replaces that faction's whole table rather than merging:
        /// a station that stopped listing an item must stop showing it, and a merge would keep the
        /// row alive forever.</summary>
        public void Remember(
            long observerId, string factionTag, DateTime utc,
            IEnumerable<KeyValuePair<string, List<FrozenMarketRow>>> stations)
        {
            if (observerId == 0 || string.IsNullOrEmpty(factionTag) || stations == null) return;

            Dictionary<string, FrozenFactionMarket> byTag;
            if (!_byObserver.TryGetValue(observerId, out byTag))
            {
                byTag = new Dictionary<string, FrozenFactionMarket>(StringComparer.Ordinal);
                _byObserver[observerId] = byTag;
            }

            var market = new FrozenFactionMarket { AsOfUtc = utc };
            foreach (KeyValuePair<string, List<FrozenMarketRow>> s in stations)
            {
                if (string.IsNullOrEmpty(s.Key) || s.Value == null) continue;
                market.Stations[s.Key] = new List<FrozenMarketRow>(s.Value);
            }

            byTag[factionTag] = market;
            _dirty = true;
        }

        /// <summary>The freshest memory of <paramref name="factionTag"/> across every store the
        /// viewer draws on. Null when nobody they can read has seen it, or when every memory has
        /// lapsed past <paramref name="horizon"/> — a stale belief is forgotten, not shown dimmer.</summary>
        public FrozenFactionMarket Freshest(
            IList<long> observerIds, string factionTag, DateTime nowUtc, TimeSpan horizon)
        {
            if (observerIds == null || string.IsNullOrEmpty(factionTag)) return null;

            FrozenFactionMarket best = null;
            for (int i = 0; i < observerIds.Count; i++)
            {
                Dictionary<string, FrozenFactionMarket> byTag;
                if (!_byObserver.TryGetValue(observerIds[i], out byTag)) continue;
                FrozenFactionMarket m;
                if (!byTag.TryGetValue(factionTag, out m) || m == null) continue;
                if (horizon > TimeSpan.Zero && nowUtc - m.AsOfUtc >= horizon) continue;
                if (best == null || m.AsOfUtc > best.AsOfUtc) best = m;
            }
            return best;
        }

        /// <summary>Drops lapsed faction tables. Purely a file-size measure — expiry is evaluated at
        /// read in <see cref="Freshest"/>, so calling this or not cannot change what a viewer sees.
        /// Returns how many faction entries went.</summary>
        public int Compact(DateTime nowUtc, TimeSpan horizon)
        {
            if (horizon <= TimeSpan.Zero) return 0;
            int dropped = 0;
            var emptyObservers = new List<long>();
            foreach (KeyValuePair<long, Dictionary<string, FrozenFactionMarket>> pair in _byObserver)
            {
                List<string> expired = null;
                foreach (KeyValuePair<string, FrozenFactionMarket> m in pair.Value)
                {
                    if (nowUtc - m.Value.AsOfUtc < horizon) continue;
                    if (expired == null) expired = new List<string>();
                    expired.Add(m.Key);
                }
                if (expired != null)
                    for (int i = 0; i < expired.Count; i++) { pair.Value.Remove(expired[i]); dropped++; }
                if (pair.Value.Count == 0) emptyObservers.Add(pair.Key);
            }
            for (int i = 0; i < emptyObservers.Count; i++) _byObserver.Remove(emptyObservers[i]);
            if (dropped > 0) _dirty = true;
            return dropped;
        }

        public void Forget(long observerId)
        {
            if (_byObserver.Remove(observerId)) _dirty = true;
        }

        // ---- persistence -------------------------------------------------------------------------

        private sealed class MemoryFile
        {
            public int SchemaVersion;
            public Dictionary<string, Dictionary<string, FrozenDoc>> Observers;
        }

        private sealed class FrozenDoc
        {
            public DateTime AsOfUtc;
            public Dictionary<string, List<FrozenMarketRow>> Stations;
        }

        public void SaveIfDirty()
        {
            if (!_dirty || _path == null) return;

            var doc = new MemoryFile
            {
                SchemaVersion = SchemaVersion,
                Observers = new Dictionary<string, Dictionary<string, FrozenDoc>>(StringComparer.Ordinal),
            };
            foreach (KeyValuePair<long, Dictionary<string, FrozenFactionMarket>> pair in _byObserver)
            {
                if (pair.Value.Count == 0) continue;
                var byTag = new Dictionary<string, FrozenDoc>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, FrozenFactionMarket> m in pair.Value)
                {
                    byTag[m.Key] = new FrozenDoc
                    {
                        AsOfUtc = m.Value.AsOfUtc,
                        Stations = m.Value.Stations,
                    };
                }
                doc.Observers[pair.Key.ToString()] = byTag;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(doc, Formatting.Indented));
            if (File.Exists(_path))
            {
                string backup = _path + ".bak";
                File.Replace(temp, _path, backup);
                try { File.Delete(backup); } catch { }
            }
            else File.Move(temp, _path);
            _dirty = false;
        }

        private void Load()
        {
            if (_path == null || !File.Exists(_path)) return;
            try
            {
                MemoryFile doc = JsonConvert.DeserializeObject<MemoryFile>(File.ReadAllText(_path));
                if (doc == null || doc.Observers == null) return;
                foreach (KeyValuePair<string, Dictionary<string, FrozenDoc>> pair in doc.Observers)
                {
                    long id;
                    if (!long.TryParse(pair.Key, out id) || pair.Value == null) continue;
                    var byTag = new Dictionary<string, FrozenFactionMarket>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, FrozenDoc> m in pair.Value)
                    {
                        if (m.Value == null) continue;
                        var market = new FrozenFactionMarket { AsOfUtc = m.Value.AsOfUtc };
                        if (m.Value.Stations != null)
                            foreach (KeyValuePair<string, List<FrozenMarketRow>> s in m.Value.Stations)
                                if (!string.IsNullOrEmpty(s.Key) && s.Value != null)
                                    market.Stations[s.Key] = s.Value;
                        byTag[m.Key] = market;
                    }
                    if (byTag.Count > 0) _byObserver[id] = byTag;
                }
            }
            catch
            {
                // A corrupt memory file costs remembered prices, nothing else. Losing them is
                // strictly better than refusing to load the session over it.
                _byObserver.Clear();
            }
        }
    }
}
