using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>One weapon's observed firing record: what it fired, what arrived.
    ///
    /// <para><see cref="DamageEvents"/> is <b>not</b> "shots that hit". SE raises one damage event per
    /// damaged block, so a single explosive round that lands among five blocks raises five, and a flak
    /// shell that bursts into 20-35 shrapnel children raises far more. Treat it as a coarse contact
    /// signal; <see cref="DamagePerShot"/> is the metric that means what it says.</para></summary>
    public class WeaponAccuracySample
    {
        /// <summary>Rounds actually fired — one per ammo consumption, not per trigger pull.</summary>
        public long Shots;

        /// <summary>Damage events attributed to this weapon. See the type remarks: not a hit count.</summary>
        public long DamageEvents;

        /// <summary>Total damage delivered to blocks by rounds from this weapon.</summary>
        public double DamageDealt;

        /// <summary>What the prefab scan believes one round of this ammo delivers, carried alongside the
        /// observation so a later reader can compare without re-deriving it from a definition set that
        /// may have moved.</summary>
        public double ModelledDamagePerShot;

        public string FirstSeenUtc;
        public string LastSeenUtc;

        /// <summary>Observed damage delivered per round fired.</summary>
        public double DamagePerShot { get { return Shots > 0 ? DamageDealt / Shots : 0.0; } }

        /// <summary>Observed over modelled. 1.0 means the scan's damage figure is being realised in
        /// full; below 1.0 is the shortfall an accuracy term would represent. Zero when there is
        /// nothing to compare, which is not the same as "perfectly inaccurate".</summary>
        public double RealisedFraction
        {
            get
            {
                if (Shots <= 0 || ModelledDamagePerShot <= 0) return 0.0;
                return DamagePerShot / ModelledDamagePerShot;
            }
        }

        /// <summary>True once there is enough to say anything. A single shot's worth of damage is an
        /// anecdote — a reader tuning on this should know which rows are which.</summary>
        public bool HasSample(long minShots) { return Shots >= (minShots > 0 ? minShots : 1); }
    }

    /// <summary>
    /// Accumulates observed weapon performance, keyed by weapon and ammo, for comparison against the
    /// modelled damage the prefab scan computes.
    ///
    /// <para><b>Bounded by construction.</b> There is one row per (weapon, ammo) pair actually fired,
    /// mutated in place — never one row per shot or per engagement. A world with 180 weapon
    /// definitions cannot produce more rows than it has weapon/ammo combinations, however long it
    /// runs, so the file this serialises to does not grow with time.</para>
    ///
    /// <para>SE-free and in Core so the merge and the arithmetic are testable without a host.</para>
    /// </summary>
    public class WeaponAccuracyLedger
    {
        private readonly Dictionary<string, WeaponAccuracySample> _rows =
            new Dictionary<string, WeaponAccuracySample>(StringComparer.Ordinal);

        /// <summary>Row key. Weapon and ammo both matter: one block can fire several ammo types with
        /// very different behaviour, and merging them would average away the thing being measured.</summary>
        public static string KeyFor(string weaponSubtype, string ammoSubtype)
        {
            return (string.IsNullOrEmpty(weaponSubtype) ? "?" : weaponSubtype)
                   + "|" + (string.IsNullOrEmpty(ammoSubtype) ? "?" : ammoSubtype);
        }

        public int RowCount { get { return _rows.Count; } }

        public IEnumerable<KeyValuePair<string, WeaponAccuracySample>> Rows { get { return _rows; } }

        public WeaponAccuracySample Get(string key)
        {
            WeaponAccuracySample row;
            return _rows.TryGetValue(key, out row) ? row : null;
        }

        /// <summary>Records one round leaving the barrel.</summary>
        public void RecordShot(string key, double modelledDamagePerShot, DateTime utcNow)
        {
            WeaponAccuracySample row = Ensure(key, utcNow);
            row.Shots++;
            // The modelled figure can move under us when a definition set changes or a framework
            // publishes late; keep the most recent non-zero rather than the first.
            if (modelledDamagePerShot > 0) row.ModelledDamagePerShot = modelledDamagePerShot;
            row.LastSeenUtc = Stamp(utcNow);
        }

        /// <summary>Records damage that arrived from a round this weapon fired.</summary>
        public void RecordDamage(string key, double amount, DateTime utcNow)
        {
            if (amount <= 0) return;
            WeaponAccuracySample row = Ensure(key, utcNow);
            row.DamageEvents++;
            row.DamageDealt += amount;
            row.LastSeenUtc = Stamp(utcNow);
        }

        /// <summary>Folds a previously persisted set in, so a restart continues a sample rather than
        /// starting one. Counts add; the modelled figure and the last-seen stamp take the newer.</summary>
        public void Merge(IEnumerable<KeyValuePair<string, WeaponAccuracySample>> persisted)
        {
            if (persisted == null) return;
            foreach (KeyValuePair<string, WeaponAccuracySample> kv in persisted)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;

                WeaponAccuracySample row;
                if (!_rows.TryGetValue(kv.Key, out row))
                {
                    _rows[kv.Key] = kv.Value;
                    continue;
                }

                row.Shots += kv.Value.Shots;
                row.DamageEvents += kv.Value.DamageEvents;
                row.DamageDealt += kv.Value.DamageDealt;
                if (row.ModelledDamagePerShot <= 0) row.ModelledDamagePerShot = kv.Value.ModelledDamagePerShot;
                // The window a row describes spans both sources: earliest start, latest end.
                if (IsBefore(kv.Value.FirstSeenUtc, row.FirstSeenUtc)) row.FirstSeenUtc = kv.Value.FirstSeenUtc;
                if (IsBefore(row.LastSeenUtc, kv.Value.LastSeenUtc)) row.LastSeenUtc = kv.Value.LastSeenUtc;
            }
        }

        /// <summary>Drops every row. The operator's reset between controlled tests: a sample taken
        /// across two different weapon configurations is not one sample.</summary>
        public void Clear() { _rows.Clear(); }

        private WeaponAccuracySample Ensure(string key, DateTime utcNow)
        {
            WeaponAccuracySample row;
            if (_rows.TryGetValue(key, out row)) return row;

            row = new WeaponAccuracySample { FirstSeenUtc = Stamp(utcNow), LastSeenUtc = Stamp(utcNow) };
            _rows[key] = row;
            return row;
        }

        /// <summary>Fixed-width ISO-8601 UTC. Invariant because ':' in a format string is the
        /// culture's <i>time separator specifier</i>, not a literal — on a host whose locale
        /// separates time with '.', the same call writes <c>2026-09-01T12.00.00Z</c> into the
        /// persisted ledger. <see cref="IsBefore"/>'s ordinal compare depends on this shape.</summary>
        private static string Stamp(DateTime utc)
        {
            return utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        /// <summary>True when <paramref name="a"/> is the earlier stamp. Ordinal compare is deliberate:
        /// the stamps are fixed-width UTC, so it orders them correctly without parsing and an
        /// unparseable value cannot throw. An absent <paramref name="a"/> is never earlier; an absent
        /// <paramref name="b"/> is always later, so a missing stamp is filled rather than kept.</summary>
        private static bool IsBefore(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return false;
            if (string.IsNullOrEmpty(b)) return true;
            return string.CompareOrdinal(a, b) < 0;
        }
    }
}
