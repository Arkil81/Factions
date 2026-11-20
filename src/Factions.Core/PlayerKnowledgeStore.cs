using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>
    /// What each player has personally detected, keyed by SE identity id.
    ///
    /// <para>Separate from a faction's store on purpose. A viewer's knowledge is the <b>union</b> of
    /// their own store and their faction's, computed at read by
    /// <see cref="KnowledgeView.IsDiscoveredBy"/> -- never merged into either. That is what lets
    /// joining a faction grant its map and leaving take it back while the player keeps what they saw
    /// themselves, and it is why join-learn-leave is not an exploit here: there is no write to walk
    /// away with.</para>
    ///
    /// <para>A solo player belongs to no faction, so their own store is their whole map. Without
    /// this, the map would be blank for them.</para>
    /// </summary>
    public sealed class PlayerKnowledgeStore
    {
        private const int SchemaVersion = 2;
        private readonly string _path;
        private readonly Dictionary<long, FactionKnowledge> _byIdentity =
            new Dictionary<long, FactionKnowledge>();
        private bool _dirty;

        public PlayerKnowledgeStore(string storageDir)
        {
            _path = Path.Combine(storageDir, "knowledge.json");
            Load();
        }

        public int PlayerCount { get { return _byIdentity.Count; } }

        /// <summary>The player's own store, created on first use. Never null, so callers can hand it
        /// straight to the union without a null check.</summary>
        public FactionKnowledge For(long identityId)
        {
            FactionKnowledge knowledge;
            if (!_byIdentity.TryGetValue(identityId, out knowledge))
            {
                knowledge = new FactionKnowledge();
                _byIdentity[identityId] = knowledge;
            }
            return knowledge;
        }

        /// <summary>Records a detection and marks the store for saving. Deliberately the only write
        /// path: <see cref="For"/> hands out a live object, but a caller that mutates it without
        /// going through here would not get persisted, so everything that writes uses this.</summary>
        public void Touch(long identityId, string nodeId, DateTime utc)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            For(identityId).Touch(nodeId, utc);
            _dirty = true;
        }

        /// <summary>Records a detection that carries what was seen, the same snapshot a faction's
        /// sweep writes.
        ///
        /// <para>A player's map is the consumer: without the snapshot a remembered node has no
        /// remembered owner, and the only thing left to draw is today's owner -- which is what a
        /// two-tone map exists not to say. The scanner already resolves an owner tag for the HUD at
        /// the moment of detection, so nothing is learned here that the player was not shown.</para></summary>
        public void Touch(long identityId, SpatialNode node, DateTime utc)
        {
            if (node == null || string.IsNullOrEmpty(node.Id)) return;
            For(identityId).Touch(node, utc);
            _dirty = true;
        }

        /// <summary>Drops lapsed entries across every player. The retention bound on this file, and
        /// purely a size measure -- expiry itself is evaluated at read.</summary>
        public int Compact(DateTime nowUtc, TimeSpan horizon)
        {
            int dropped = 0;
            foreach (KeyValuePair<long, FactionKnowledge> pair in _byIdentity)
                dropped += pair.Value.Compact(nowUtc, horizon);
            if (dropped > 0) _dirty = true;
            return dropped;
        }

        public void SaveIfDirty()
        {
            if (!_dirty) return;
            var doc = new KnowledgeFile
            {
                SchemaVersion = SchemaVersion,
                Players = new Dictionary<string, Dictionary<string, KnowledgeEntryDoc>>(StringComparer.Ordinal),
            };
            foreach (KeyValuePair<long, FactionKnowledge> pair in _byIdentity)
            {
                if (pair.Value.Count == 0) continue;   // an empty player is not worth a line
                var entries = new Dictionary<string, KnowledgeEntryDoc>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, KnownNode> e in pair.Value.Snapshots)
                    entries[e.Key] = new KnowledgeEntryDoc
                    {
                        T = e.Value.LastSeenUtc,
                        HasDetail = e.Value.HasDetail,
                        O = e.Value.OwnerTag,
                        H = e.Value.HoldStrength,
                        V = e.Value.ResourceValue,
                    };
                doc.Players[pair.Key.ToString()] = entries;
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
            try
            {
                if (!File.Exists(_path)) return;
                KnowledgeFile doc = JsonConvert.DeserializeObject<KnowledgeFile>(File.ReadAllText(_path));
                if (doc == null || doc.Players == null) return;
                foreach (KeyValuePair<string, Dictionary<string, KnowledgeEntryDoc>> pair in doc.Players)
                {
                    long identityId;
                    if (!long.TryParse(pair.Key, out identityId) || pair.Value == null) continue;
                    var loaded = new Dictionary<string, KnownNode>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, KnowledgeEntryDoc> e in pair.Value)
                    {
                        if (e.Value == null) continue;
                        loaded[e.Key] = new KnownNode
                        {
                            LastSeenUtc = e.Value.T,
                            HasDetail = e.Value.HasDetail,
                            OwnerTag = e.Value.O,
                            HoldStrength = e.Value.H,
                            ResourceValue = e.Value.V,
                        };
                    }
                    var knowledge = new FactionKnowledge();
                    knowledge.LoadFrom(loaded);
                    _byIdentity[identityId] = knowledge;
                }
            }
            catch
            {
                // A corrupt file costs players their personal map and nothing else -- every other
                // knowledge source is derived or held elsewhere -- so starting empty beats refusing
                // to boot.
                _byIdentity.Clear();
            }
        }

        /// <summary>Schema 2 carries the detail a detection saw, written by
        /// <see cref="KnowledgeEntryDocConverter"/> as the brain files already write theirs: a bare
        /// timestamp when there is none, an object when there is. A schema 1 file loads unchanged --
        /// every entry in it is a timestamp, which is exactly a detail-free entry.</summary>
        private sealed class KnowledgeFile
        {
            public int SchemaVersion { get; set; }
            public Dictionary<string, Dictionary<string, KnowledgeEntryDoc>> Players { get; set; }
        }
    }
}
