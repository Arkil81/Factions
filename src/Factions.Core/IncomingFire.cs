using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Who is shooting each materialised fleet hull: per hull, the last hit, and for up to
    /// <see cref="SlotsPerHull"/> attackers the damage each dealt, decayed with time constant
    /// <see cref="DecaySeconds"/> so it reads as "damage in the last several seconds". The host
    /// records every hit on a grid it materialised, with the attacker walked to the hull its weapon is
    /// mounted on, and forgets the hull when it despawns. The combat read asks it whom a hull, or its
    /// squadron, is being shot by (<see cref="TargetSelection.Pick"/>); evasion asks whether a hull was
    /// hit and when (<see cref="TryRecent"/>). A hull's record is allocated on its first hit and
    /// updated in place after that, so a hit allocates nothing. Game thread only.
    /// </summary>
    public sealed class IncomingFire
    {
        /// <summary>How long a hit keeps its attacker an answer to "who is shooting me".</summary>
        public static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(20);

        /// <summary>Time constant of the per-attacker damage decay.</summary>
        public const double DecaySeconds = 10.0;

        /// <summary>Attackers remembered per hull. A new attacker takes the slot with the least
        /// decayed damage.</summary>
        public const int SlotsPerHull = 4;

        private struct Slot
        {
            public long AttackerGridId;
            public double Damage;     // decayed to LastUtc
            public DateTime LastUtc;
        }

        private sealed class HullRecord
        {
            public long LastAttacker;
            public DateTime LastUtc;
            public readonly Slot[] Slots = new Slot[SlotsPerHull];
        }

        private readonly Dictionary<long, HullRecord> _hulls = new Dictionary<long, HullRecord>();

        // Squadron aggregation scratch, reused.
        private readonly long[] _aggIds = new long[64];
        private readonly double[] _aggDamage = new double[64];

        public int Count { get { return _hulls.Count; } }

        /// <summary>Grid <paramref name="attackerGridId"/> dealt <paramref name="damage"/> to hull
        /// <paramref name="hullId"/> at <paramref name="utc"/>. A zero attacker (a hit no grid fired)
        /// and a hull hitting itself are not recorded.</summary>
        public void Record(long hullId, long attackerGridId, double damage, DateTime utc)
        {
            if (hullId == 0 || attackerGridId == 0 || attackerGridId == hullId) return;
            HullRecord r;
            if (!_hulls.TryGetValue(hullId, out r))
            {
                r = new HullRecord();
                _hulls[hullId] = r;
            }
            r.LastAttacker = attackerGridId;
            r.LastUtc = utc;
            if (damage < 0) damage = 0;

            int free = -1, weakest = -1;
            double weakestDmg = double.MaxValue;
            for (int i = 0; i < SlotsPerHull; i++)
            {
                if (r.Slots[i].AttackerGridId == attackerGridId)
                {
                    r.Slots[i].Damage = Decayed(r.Slots[i], utc) + damage;
                    r.Slots[i].LastUtc = utc;
                    return;
                }
                if (r.Slots[i].AttackerGridId == 0) { if (free < 0) free = i; continue; }
                double d = Live(r.Slots[i], utc) ? Decayed(r.Slots[i], utc) : 0;
                if (d < weakestDmg) { weakestDmg = d; weakest = i; }
            }
            int k = free >= 0 ? free : weakest;
            r.Slots[k] = new Slot { AttackerGridId = attackerGridId, Damage = damage, LastUtc = utc };
        }

        /// <summary>The grid that last damaged <paramref name="hullId"/>, when that was inside
        /// <see cref="RecentWindow"/> of <paramref name="nowUtc"/>; false otherwise.</summary>
        public bool TryRecent(long hullId, DateTime nowUtc, out long attackerGridId, out DateTime hitUtc)
        {
            HullRecord r;
            if (_hulls.TryGetValue(hullId, out r) && nowUtc - r.LastUtc <= RecentWindow)
            {
                attackerGridId = r.LastAttacker;
                hitUtc = r.LastUtc;
                return true;
            }
            attackerGridId = 0;
            hitUtc = DateTime.MinValue;
            return false;
        }

        /// <summary>Whether <paramref name="hullId"/> was hit inside <see cref="RecentWindow"/>.</summary>
        public bool HitRecently(long hullId, DateTime nowUtc)
        {
            long a;
            DateTime t;
            return TryRecent(hullId, nowUtc, out a, out t);
        }

        /// <summary>The decayed damage <paramref name="attackerGridId"/> dealt the first
        /// <paramref name="count"/> of <paramref name="hullIds"/>, counting only hulls it hit inside
        /// <see cref="RecentWindow"/>; 0 when it has not.</summary>
        public double DamageBy(long[] hullIds, int count, long attackerGridId, DateTime nowUtc)
        {
            if (attackerGridId == 0) return 0;
            double sum = 0;
            for (int h = 0; h < count; h++)
            {
                HullRecord r;
                if (!_hulls.TryGetValue(hullIds[h], out r)) continue;
                for (int i = 0; i < SlotsPerHull; i++)
                    if (r.Slots[i].AttackerGridId == attackerGridId && Live(r.Slots[i], nowUtc))
                        sum += Decayed(r.Slots[i], nowUtc);
            }
            return sum;
        }

        /// <summary>Of everything that hit the first <paramref name="count"/> of
        /// <paramref name="hullIds"/> inside <see cref="RecentWindow"/>, the grid with the most decayed
        /// damage summed across them, and that damage; 0 and 0 for none. Ties go to the grid seen
        /// first.</summary>
        public long TopAttacker(long[] hullIds, int count, DateTime nowUtc, out double damage)
        {
            int n = 0;
            for (int h = 0; h < count; h++)
            {
                HullRecord r;
                if (!_hulls.TryGetValue(hullIds[h], out r)) continue;
                for (int i = 0; i < SlotsPerHull; i++)
                {
                    Slot s = r.Slots[i];
                    if (!Live(s, nowUtc)) continue;
                    double d = Decayed(s, nowUtc);
                    int k = 0;
                    while (k < n && _aggIds[k] != s.AttackerGridId) k++;
                    if (k < n) { _aggDamage[k] += d; continue; }
                    if (n == _aggIds.Length) continue;
                    _aggIds[n] = s.AttackerGridId;
                    _aggDamage[n] = d;
                    n++;
                }
            }
            long best = 0;
            damage = 0;
            for (int k = 0; k < n; k++)
                if (best == 0 || _aggDamage[k] > damage) { best = _aggIds[k]; damage = _aggDamage[k]; }
            return best;
        }

        /// <summary>Of <paramref name="hullIds"/> (the first <paramref name="count"/>), the grid that
        /// hit any of them most recently inside <see cref="RecentWindow"/>, or 0.</summary>
        public long LatestAttacker(long[] hullIds, int count, DateTime nowUtc)
        {
            long best = 0;
            DateTime bestUtc = DateTime.MinValue;
            for (int i = 0; i < count; i++)
            {
                long a;
                DateTime t;
                if (!TryRecent(hullIds[i], nowUtc, out a, out t) || t <= bestUtc) continue;
                best = a;
                bestUtc = t;
            }
            return best;
        }

        /// <summary>The attacker of <paramref name="hullId"/>'s most recent hit on record, of the
        /// <see cref="SlotsPerHull"/> attackers it remembers, skipping any
        /// <paramref name="excluded"/> answers true for (a grid of the hull's own faction). Not
        /// windowed: the caller judges how recent is recent. False when none is on record.</summary>
        public bool TryLastHit(long hullId, Func<long, bool> excluded, out long attackerGridId, out DateTime hitUtc)
        {
            attackerGridId = 0;
            hitUtc = DateTime.MinValue;
            HullRecord r;
            if (!_hulls.TryGetValue(hullId, out r)) return false;
            for (int i = 0; i < SlotsPerHull; i++)
            {
                Slot s = r.Slots[i];
                if (s.AttackerGridId == 0 || s.LastUtc <= hitUtc) continue;
                if (excluded != null && excluded(s.AttackerGridId)) continue;
                attackerGridId = s.AttackerGridId;
                hitUtc = s.LastUtc;
            }
            return attackerGridId != 0;
        }

        /// <summary>Drops a hull that despawned.</summary>
        public void Forget(long hullId) { _hulls.Remove(hullId); }

        private static bool Live(Slot s, DateTime nowUtc)
        {
            return s.AttackerGridId != 0 && nowUtc - s.LastUtc <= RecentWindow;
        }

        private static double Decayed(Slot s, DateTime nowUtc)
        {
            double dt = (nowUtc - s.LastUtc).TotalSeconds;
            return dt <= 0 ? s.Damage : s.Damage * Math.Exp(-dt / DecaySeconds);
        }
    }
}
