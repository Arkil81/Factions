using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>
    /// The running total of trade a player has done with one faction, and the remainder carried
    /// between reputation steps. Keyed by SE identity id and faction tag.
    ///
    /// <para><b>Why an accumulator and not a per-transaction rule.</b> A flat "any trade over N moves
    /// reputation" is farmable by splitting one large trade into several, and a flat "N per trade" is
    /// farmable by making many tiny ones. A running total that grants one step per
    /// <c>TradeReputationThreshold</c> crossed and <i>carries the remainder</i> is invariant to how
    /// the same value is split up. It is also the idiom SE itself uses on the loss side, where
    /// <c>MyFaction.DamageInflicted</c> accumulates against <c>ReputationLossDamage</c>.</para>
    ///
    /// <para>Separate from <see cref="PlayerKnowledgeStore"/> and <see cref="MarketMemory"/> on
    /// purpose, and self-persisting for the same reason they are: this is a record of individual
    /// <i>conduct</i>, it must survive a restart, and it belongs to no single faction's brain file.
    /// The reputation it grants lives in SE, not here — this file holds only the progress toward the
    /// next step, so losing it costs a player at most one unearned step, never their standing.</para>
    /// </summary>
    public sealed class TradeReputationLedger
    {
        private const int SchemaVersion = 1;

        private sealed class Entry
        {
            public double Remainder;
            public double Lifetime;
            public DateTime LastUtc;
        }

        private readonly string _path;
        // "<identityId>|<factionTag>" -> entry. One flat dictionary rather than nested: the only
        // access pattern is the exact pair, and a flat key is what the file round-trips as.
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private bool _dirty;

        public TradeReputationLedger(string storageDir)
        {
            _path = Path.Combine(storageDir, "trade_reputation.json");
            Load();
        }

        public int EntryCount { get { return _entries.Count; } }

        /// <summary>Credit <paramref name="value"/> of trade toward this pair and report how many
        /// whole reputation steps that crossed.</summary>
        /// <param name="identityId">SE identity of the trader. 0 (unattributed) accrues nothing.</param>
        /// <param name="factionTag">The faction whose station was traded with.</param>
        /// <param name="value">Already-weighted trade value — provision and consumption are weighted
        /// differently by the caller, because only the caller knows the direction.</param>
        /// <param name="threshold">Trade value per reputation step. Non-positive grants nothing
        /// rather than dividing by zero, so an unconfigured world is inert instead of explosive.</param>
        /// <param name="nowUtc">Clock, for the retention stamp.</param>
        /// <returns>Whole steps earned by this credit, usually 0.</returns>
        public int Accrue(long identityId, string factionTag, double value, double threshold, DateTime nowUtc)
        {
            if (identityId == 0 || string.IsNullOrEmpty(factionTag)) return 0;
            if (value <= 0.0 || threshold <= 0.0 || double.IsNaN(value) || double.IsInfinity(value)) return 0;

            string key = KeyOf(identityId, factionTag);
            Entry e;
            if (!_entries.TryGetValue(key, out e)) { e = new Entry(); _entries[key] = e; }

            e.Remainder += value;
            e.Lifetime += value;
            e.LastUtc = nowUtc;
            _dirty = true;

            int steps = (int)(e.Remainder / threshold);
            if (steps > 0) e.Remainder -= steps * threshold;
            return steps;
        }

        /// <summary>Trade value credited toward the next step for this pair. Diagnostic.</summary>
        public double RemainderFor(long identityId, string factionTag)
        {
            Entry e;
            return _entries.TryGetValue(KeyOf(identityId, factionTag), out e) ? e.Remainder : 0.0;
        }

        /// <summary>Everything this pair has ever traded, weighted. Diagnostic.</summary>
        public double LifetimeFor(long identityId, string factionTag)
        {
            Entry e;
            return _entries.TryGetValue(KeyOf(identityId, factionTag), out e) ? e.Lifetime : 0.0;
        }

        /// <summary>Drops pairs untouched for longer than <paramref name="horizon"/>. The retention
        /// bound on this file — a player who has not traded with a faction in months is carrying at
        /// most a fraction of one step, which is not worth a line.</summary>
        public int Compact(DateTime nowUtc, TimeSpan horizon)
        {
            if (horizon <= TimeSpan.Zero) return 0;
            var stale = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in _entries)
                if (nowUtc - pair.Value.LastUtc > horizon) stale.Add(pair.Key);
            foreach (string k in stale) _entries.Remove(k);
            if (stale.Count > 0) _dirty = true;
            return stale.Count;
        }

        public void SaveIfDirty()
        {
            if (!_dirty) return;
            var doc = new LedgerFile
            {
                SchemaVersion = SchemaVersion,
                Pairs = new Dictionary<string, PairDoc>(StringComparer.Ordinal),
            };
            foreach (KeyValuePair<string, Entry> pair in _entries)
                doc.Pairs[pair.Key] = new PairDoc
                {
                    Remainder = pair.Value.Remainder,
                    Lifetime = pair.Value.Lifetime,
                    LastUtc = pair.Value.LastUtc,
                };

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
                LedgerFile doc = JsonConvert.DeserializeObject<LedgerFile>(File.ReadAllText(_path));
                if (doc == null || doc.Pairs == null) return;
                foreach (KeyValuePair<string, PairDoc> pair in doc.Pairs)
                {
                    if (pair.Value == null) continue;
                    _entries[pair.Key] = new Entry
                    {
                        Remainder = pair.Value.Remainder,
                        Lifetime = pair.Value.Lifetime,
                        LastUtc = pair.Value.LastUtc,
                    };
                }
            }
            catch
            {
                // A corrupt file costs players their progress toward the next step and nothing else --
                // the reputation already granted lives in SE's own record. Starting empty beats
                // refusing to boot.
                _entries.Clear();
            }
        }

        private static string KeyOf(long identityId, string factionTag)
        {
            return identityId.ToString(CultureInfo.InvariantCulture) + "|" + factionTag;
        }

        private sealed class LedgerFile
        {
            public int SchemaVersion { get; set; }
            public Dictionary<string, PairDoc> Pairs { get; set; }
        }

        private sealed class PairDoc
        {
            public double Remainder { get; set; }
            public double Lifetime { get; set; }
            public DateTime LastUtc { get; set; }
        }
    }
}
