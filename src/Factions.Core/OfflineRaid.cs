using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Whether the faction that owns a damaged grid can defend it.</summary>
    public enum OfflineState : byte
    {
        /// <summary>A human member is online.</summary>
        Online = 0,
        /// <summary>Every human member is offline, but the last left less than the grace window ago.</summary>
        Grace = 1,
        /// <summary>Every human member has been offline for at least the grace window.</summary>
        Offline = 2,
    }

    /// <summary>What the host does about one hostile hit.</summary>
    public struct OfflineRaidVerdict
    {
        /// <summary>The first offline hit of a raid: log it and tell the admins.</summary>
        public bool Began;
        /// <summary>Show the attacker the warning: on <see cref="Began"/> and again every repeat window.</summary>
        public bool WarnAttacker;
        /// <summary>The hit would have been offline, but the victim faction logged off during this
        /// attacker's attack, so it is the same fight.</summary>
        public bool CombatLogged;
    }

    /// <summary>One raid, closed once the attacker has stopped for the end window.</summary>
    public struct OfflineRaidSummary
    {
        public long Attacker;
        public long Victim;
        public string VictimKey;
        public DateTime StartedUtc;
        public DateTime LastHitUtc;
        public double Damage;
        public int BlocksDestroyed;
        public int Grids;
    }

    /// <summary>A victim faction that went wholly offline while under hostile player fire.</summary>
    public struct CombatLogRecord
    {
        public string VictimKey;
        public long Attacker;
        public long GridEntityId;
        public DateTime LastHostileUtc;
    }

    /// <summary>
    /// Offline-raid and combat-log bookkeeping (FAC-534), SE-free. The host decides which hits are
    /// hostile player damage to a human-owned grid and how long the owner's faction has been offline;
    /// this decides what that hit means and keeps the per-raid totals.
    ///
    /// <para>A raid is one attacker against one victim faction (or factionless owner, keyed by
    /// identity), however many grids it touches. It ends once the attacker has not hit that victim for
    /// <see cref="FactionTuning.OfflineRaidEndAfterMinutes"/>.</para>
    /// </summary>
    public sealed class OfflineRaidLedger
    {
        private struct RaidKey : IEquatable<RaidKey>
        {
            public long Attacker;
            public string Victim;
            public bool Equals(RaidKey o) { return Attacker == o.Attacker && string.Equals(Victim, o.Victim, StringComparison.Ordinal); }
            public override bool Equals(object obj) { return obj is RaidKey && Equals((RaidKey)obj); }
            public override int GetHashCode() { return Attacker.GetHashCode() * 31 + (Victim != null ? StringComparer.Ordinal.GetHashCode(Victim) : 0); }
        }

        private sealed class Raid
        {
            public long Victim;
            public DateTime StartedUtc;
            public DateTime LastHitUtc;
            public DateTime LastWarnUtc;
            public double Damage;
            public int BlocksDestroyed;
            public readonly HashSet<long> Grids = new HashSet<long>();
        }

        private sealed class Hostile
        {
            public long Attacker;
            public long GridEntityId;
            public DateTime LastUtc;
            public bool WasOnline;
            public bool Seen;
            /// <summary>Every attacker of this victim and their last hit: all of them were in the
            /// fight a combat logger left.</summary>
            public readonly Dictionary<long, DateTime> Attackers = new Dictionary<long, DateTime>();
        }

        private readonly Dictionary<RaidKey, Raid> _raids = new Dictionary<RaidKey, Raid>();
        /// <summary>Attacker against victim pairs where the victim logged off mid-fight; the value is
        /// the last hit, and the exemption lasts while the attacker keeps hitting.</summary>
        private readonly Dictionary<RaidKey, DateTime> _combatLogged = new Dictionary<RaidKey, DateTime>();
        private readonly Dictionary<string, Hostile> _hostile = new Dictionary<string, Hostile>(StringComparer.Ordinal);
        private readonly List<RaidKey> _scratch = new List<RaidKey>();
        private readonly List<string> _keyScratch = new List<string>();

        public int OpenRaids { get { return _raids.Count; } }

        /// <summary>Null <paramref name="offlineSinceUtc"/> is online. An unknown last-seen time is
        /// the host's to pass as <see cref="DateTime.MinValue"/>, which reads as long offline.</summary>
        public static OfflineState Classify(DateTime? offlineSinceUtc, DateTime nowUtc, TimeSpan grace)
        {
            if (!offlineSinceUtc.HasValue) return OfflineState.Online;
            return nowUtc - offlineSinceUtc.Value >= grace ? OfflineState.Offline : OfflineState.Grace;
        }

        /// <summary>Hostile player damage on a human-owned grid of <paramref name="victimKey"/>: one hit, or
        /// the host's total since its last pass, with <paramref name="destroyed"/> blocks destroyed.</summary>
        public OfflineRaidVerdict Hit(long attacker, long victim, string victimKey, long gridEntityId, double amount,
                                      int destroyed, OfflineState state, DateTime nowUtc, FactionTuning t)
        {
            var verdict = new OfflineRaidVerdict();
            if (attacker == 0 || string.IsNullOrEmpty(victimKey)) return verdict;

            Hostile h;
            if (!_hostile.TryGetValue(victimKey, out h))
            {
                h = new Hostile();
                _hostile[victimKey] = h;
            }
            h.Attacker = attacker;
            h.GridEntityId = gridEntityId;
            h.LastUtc = nowUtc;
            h.Attackers[attacker] = nowUtc;
            if (!h.Seen) { h.Seen = true; h.WasOnline = state == OfflineState.Online; }

            var key = new RaidKey { Attacker = attacker, Victim = victimKey };
            DateTime loggedHit;
            if (_combatLogged.TryGetValue(key, out loggedHit))
            {
                if (nowUtc - loggedHit < EndAfter(t))
                {
                    _combatLogged[key] = nowUtc;
                    verdict.CombatLogged = state == OfflineState.Offline;
                    return verdict;
                }
                _combatLogged.Remove(key);
            }

            Raid raid;
            bool open = _raids.TryGetValue(key, out raid);
            if (state != OfflineState.Offline && !open) return verdict;
            if (!open)
            {
                raid = new Raid { Victim = victim, StartedUtc = nowUtc, LastWarnUtc = nowUtc };
                _raids[key] = raid;
                verdict.Began = true;
                verdict.WarnAttacker = true;
            }
            else if (nowUtc - raid.LastWarnUtc >= TimeSpan.FromMinutes(Math.Max(0.0, t.OfflineRaidWarnRepeatMinutes)))
            {
                raid.LastWarnUtc = nowUtc;
                verdict.WarnAttacker = true;
            }
            raid.LastHitUtc = nowUtc;
            if (amount > 0) raid.Damage += amount;
            if (destroyed > 0) raid.BlocksDestroyed += destroyed;
            if (gridEntityId != 0) raid.Grids.Add(gridEntityId);
            return verdict;
        }

        /// <summary>Raids the attacker has stopped, removed and appended to <paramref name="into"/>.</summary>
        public void TakeEnded(DateTime nowUtc, FactionTuning t, List<OfflineRaidSummary> into)
        {
            TimeSpan idle = EndAfter(t);
            _scratch.Clear();
            foreach (KeyValuePair<RaidKey, Raid> p in _raids)
                if (nowUtc - p.Value.LastHitUtc >= idle) _scratch.Add(p.Key);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Raid r = _raids[_scratch[i]];
                _raids.Remove(_scratch[i]);
                if (into != null)
                    into.Add(new OfflineRaidSummary
                    {
                        Attacker = _scratch[i].Attacker, Victim = r.Victim, VictimKey = _scratch[i].Victim,
                        StartedUtc = r.StartedUtc, LastHitUtc = r.LastHitUtc, Damage = r.Damage,
                        BlocksDestroyed = r.BlocksDestroyed, Grids = r.Grids.Count,
                    });
            }

            _scratch.Clear();
            foreach (KeyValuePair<RaidKey, DateTime> p in _combatLogged)
                if (nowUtc - p.Value >= idle) _scratch.Add(p.Key);
            for (int i = 0; i < _scratch.Count; i++) _combatLogged.Remove(_scratch[i]);

            TimeSpan window = CombatLogWindow(t);
            _keyScratch.Clear();
            foreach (KeyValuePair<string, Hostile> p in _hostile)
                if (nowUtc - p.Value.LastUtc >= window) _keyScratch.Add(p.Key);
            for (int i = 0; i < _keyScratch.Count; i++) _hostile.Remove(_keyScratch[i]);
        }

        /// <summary>Victims hit by a player within the combat-log window; the host reports whether
        /// each is online now through <see cref="ObserveOnline"/>.</summary>
        public void KeysUnderFire(List<string> into)
        {
            into.Clear();
            foreach (string k in _hostile.Keys) into.Add(k);
        }

        /// <summary>The victim's online state now. True, with the record, when it went from online to
        /// wholly offline inside the combat-log window: from then on, that attacker's hits on it are
        /// the same fight and not an offline raid.</summary>
        public bool ObserveOnline(string victimKey, bool online, DateTime nowUtc, FactionTuning t, out CombatLogRecord record)
        {
            record = default(CombatLogRecord);
            Hostile h;
            if (string.IsNullOrEmpty(victimKey) || !_hostile.TryGetValue(victimKey, out h)) return false;
            bool was = h.WasOnline;
            h.WasOnline = online;
            if (!was || online || nowUtc - h.LastUtc >= CombatLogWindow(t)) return false;
            record = new CombatLogRecord
            {
                VictimKey = victimKey, Attacker = h.Attacker, GridEntityId = h.GridEntityId, LastHostileUtc = h.LastUtc,
            };
            TimeSpan window = CombatLogWindow(t);
            foreach (KeyValuePair<long, DateTime> a in h.Attackers)
                if (nowUtc - a.Value < window) _combatLogged[new RaidKey { Attacker = a.Key, Victim = victimKey }] = nowUtc;
            return true;
        }

        private static TimeSpan EndAfter(FactionTuning t)
        {
            return TimeSpan.FromMinutes(Math.Max(0.1, t.OfflineRaidEndAfterMinutes));
        }

        private static TimeSpan CombatLogWindow(FactionTuning t)
        {
            return TimeSpan.FromMinutes(Math.Max(0.0, t.CombatLogWindowMinutes));
        }
    }
}
