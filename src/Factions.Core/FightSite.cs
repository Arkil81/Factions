using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Why a hull left a fight's field.</summary>
    public enum FightExitCause
    {
        /// <summary>Lost: the grid is gone with damage on record.</summary>
        Destroyed,
        /// <summary>Lost: thrust gone, a wreck.</summary>
        Disabled,
        /// <summary>Below <see cref="FightSite.RetreatIntegrity"/>: it leaves the fight, creditable.</summary>
        Retreated,
        /// <summary>On the field <see cref="FightSite.RotationSeconds"/> while its side had a queue:
        /// back to the end of the queue.</summary>
        Rotated,
        /// <summary>The field budget shrank under it: back to the front of the queue.</summary>
        Shed,
    }

    /// <summary>One hull in a fight, queued or on the field. <see cref="Member"/> indexes the fleet's
    /// <see cref="PendingFleetAction.HullsToReturn"/>.</summary>
    public sealed class FightHull
    {
        public string FleetId;
        public int Member;
        public string Prefab;
        public string Side;
        /// <summary>Predicted frame cost on the field, already scaled by the combat calibration.</summary>
        public double CostMs;
        public double Dps;
        public DateTime QueuedUtc;
        public DateTime OnFieldSinceUtc;

        public string Key { get { return FleetId + "#" + Member; } }
    }

    /// <summary>A hull the site wants off the field, and why. The host takes the grid away, then
    /// reports it with <see cref="FightSite.Exit"/>.</summary>
    public struct FightExit
    {
        public FightHull Hull;
        public FightExitCause Cause;
    }

    /// <summary>
    /// A materialised fight as controlled airspace (docs/fight-control.md, FAC-425). Each side queues
    /// its hulls; a bounded number are on the field at once; a hull leaves the field before the next is
    /// cleared in. Queued hulls wait abstractly and do not fight (operator, 2026-10-07: physical only).
    ///
    /// <para><b>The site holds the meeting, not a grid.</b> FAC-383 rejected waves because a lead never
    /// leaves until the fight resolves. Here any hull, the lead included, can rotate out; a fleet is at
    /// the fight while it has a hull on the field or in the queue.</para>
    ///
    /// <para>Pure bookkeeping. The host spawns what <see cref="Admit"/> clears, takes away what
    /// <see cref="Review"/> asks for, and reports every departure through <see cref="Exit"/>.</para>
    /// </summary>
    public sealed class FightSite
    {
        /// <summary>Below this hull integrity a hull on the field retreats.</summary>
        public const double RetreatIntegrity = 0.35;
        /// <summary>A hull on the field this long rotates out when its side has a queue.</summary>
        public const double RotationSeconds = 180;

        private sealed class SideState
        {
            public readonly List<FightHull> Queue = new List<FightHull>();
            public readonly List<FightHull> Field = new List<FightHull>();
            public int Lost;
            public int Retreated;
        }

        private readonly Dictionary<string, SideState> _sides =
            new Dictionary<string, SideState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new List<string>();

        public string Id;
        public Vec3 PosKm;
        public DateTime OpenedUtc;

        public FightSite(string id, Vec3 posKm, DateTime nowUtc)
        {
            Id = id;
            PosKm = posKm;
            OpenedUtc = nowUtc;
        }

        public IReadOnlyList<string> Sides { get { return _order; } }

        public double UsedMs
        {
            get
            {
                double used = 0;
                foreach (SideState s in _sides.Values)
                    for (int i = 0; i < s.Field.Count; i++) used += s.Field[i].CostMs;
                return used;
            }
        }

        public int OnField(string side) { SideState s; return _sides.TryGetValue(side, out s) ? s.Field.Count : 0; }
        public int Queued(string side)  { SideState s; return _sides.TryGetValue(side, out s) ? s.Queue.Count : 0; }
        public int Lost(string side)    { SideState s; return _sides.TryGetValue(side, out s) ? s.Lost : 0; }
        public int RetreatedFrom(string side) { SideState s; return _sides.TryGetValue(side, out s) ? s.Retreated : 0; }

        public IReadOnlyList<FightHull> FieldOf(string side)
        {
            SideState s;
            return _sides.TryGetValue(side, out s) ? (IReadOnlyList<FightHull>)s.Field : new FightHull[0];
        }

        /// <summary>Whether the hull is queued or on the field here.</summary>
        public bool Contains(string fleetId, int member)
        {
            return Find(fleetId, member) != null;
        }

        /// <summary>Joins <paramref name="hull"/> to the back of its side's queue. Idempotent: a hull
        /// already queued or on the field is left where it is.</summary>
        public void Enqueue(FightHull hull, DateTime nowUtc)
        {
            if (hull == null || string.IsNullOrEmpty(hull.Side)) return;
            if (Find(hull.FleetId, hull.Member) != null) return;
            hull.QueuedUtc = nowUtc;
            Side(hull.Side).Queue.Add(hull);
        }

        /// <summary>Puts <paramref name="hull"/> straight on the field: a grid already in the world
        /// when the site opened, adopted rather than spawned. <see cref="Review"/> sheds it if the
        /// field is over budget. Idempotent like <see cref="Enqueue"/>.</summary>
        public void PlaceOnField(FightHull hull, DateTime nowUtc)
        {
            if (hull == null || string.IsNullOrEmpty(hull.Side)) return;
            if (Find(hull.FleetId, hull.Member) != null) return;
            hull.QueuedUtc = nowUtc;
            hull.OnFieldSinceUtc = nowUtc;
            Side(hull.Side).Field.Add(hull);
        }

        /// <summary>The fleet lost hull slot <paramref name="slot"/> (its slots shift down past it, as
        /// <see cref="PendingFleetAction.MarkHullLostAt"/> does): every hull of the fleet here above it
        /// is renumbered. A hull still holding <paramref name="slot"/> itself is dropped.</summary>
        public void OnSlotRemoved(string fleetId, int slot)
        {
            foreach (SideState s in _sides.Values)
            {
                s.Queue.RemoveAll(h => h.Member == slot && string.Equals(h.FleetId, fleetId, StringComparison.Ordinal));
                s.Field.RemoveAll(h => h.Member == slot && string.Equals(h.FleetId, fleetId, StringComparison.Ordinal));
                for (int i = 0; i < s.Queue.Count; i++) Shift(s.Queue[i], fleetId, slot);
                for (int i = 0; i < s.Field.Count; i++) Shift(s.Field[i], fleetId, slot);
            }
        }

        private static void Shift(FightHull h, string fleetId, int removed)
        {
            if (h.Member > removed && string.Equals(h.FleetId, fleetId, StringComparison.Ordinal)) h.Member--;
        }

        /// <summary>Every hull of <paramref name="fleetId"/> queued here, front to back.</summary>
        public List<FightHull> QueuedOf(string fleetId)
        {
            var list = new List<FightHull>();
            foreach (SideState s in _sides.Values)
                for (int i = 0; i < s.Queue.Count; i++)
                    if (string.Equals(s.Queue[i].FleetId, fleetId, StringComparison.Ordinal)) list.Add(s.Queue[i]);
            return list;
        }

        /// <summary>Every hull of <paramref name="fleetId"/> on the field here.</summary>
        public List<FightHull> FieldOfFleet(string fleetId)
        {
            var list = new List<FightHull>();
            foreach (SideState s in _sides.Values)
                for (int i = 0; i < s.Field.Count; i++)
                    if (string.Equals(s.Field[i].FleetId, fleetId, StringComparison.Ordinal)) list.Add(s.Field[i]);
            return list;
        }

        /// <summary>Sides with any hull left, field or queue.</summary>
        public int SidesStanding
        {
            get
            {
                int n = 0;
                foreach (SideState s in _sides.Values) if (s.Field.Count + s.Queue.Count > 0) n++;
                return n;
            }
        }

        /// <summary>Clears queue heads onto the field within <paramref name="fieldMs"/>, and returns
        /// them for the host to spawn.
        ///
        /// <para>Each side is entitled to the share of the budget its strength (DPS over field and
        /// queue) is of the total, so what is visible is a fair sample of what is fighting. The side
        /// furthest under its share goes next; its head is cleared if it fits. A side with nothing on
        /// the field gets its head cleared whatever the budget — a fight is never invisible on one
        /// side. FIFO per side: a head that does not fit stops that side.</para></summary>
        public List<FightHull> Admit(double fieldMs, DateTime nowUtc)
        {
            return Admit(fieldMs, nowUtc, null);
        }

        /// <summary><see cref="Admit(double, DateTime)"/>, first retiring every queued hull below
        /// <see cref="RetreatIntegrity"/> by <paramref name="integrityOf"/>: it would retreat the moment it
        /// arrived, so it leaves the fight from the queue instead of being spawned to do it.</summary>
        public List<FightHull> Admit(double fieldMs, DateTime nowUtc, Func<FightHull, double> integrityOf)
        {
            if (integrityOf != null)
                foreach (SideState s in _sides.Values)
                    s.Retreated += s.Queue.RemoveAll(h => integrityOf(h) < RetreatIntegrity);
            var cleared = new List<FightHull>();
            double used = UsedMs;
            double total = 0;
            var strength = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in _order)
            {
                double dps = StrengthOf(_sides[name]);
                strength[name] = dps;
                total += dps;
            }
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                string pick = null;
                double pickRatio = double.MaxValue;
                foreach (string name in _order)
                {
                    SideState s = _sides[name];
                    if (s.Queue.Count == 0 || blocked.Contains(name)) continue;
                    double share = total > 0 ? fieldMs * strength[name] / total : fieldMs / Math.Max(1, _order.Count);
                    double ratio = s.Field.Count == 0 ? -1 : (share > 0 ? FieldMs(s) / share : double.MaxValue);
                    if (ratio < pickRatio) { pickRatio = ratio; pick = name; }
                }
                if (pick == null) break;

                SideState side = _sides[pick];
                FightHull head = side.Queue[0];
                bool first = side.Field.Count == 0;
                if (!first && used + head.CostMs > fieldMs) { blocked.Add(pick); continue; }

                side.Queue.RemoveAt(0);
                head.OnFieldSinceUtc = nowUtc;
                side.Field.Add(head);
                used += head.CostMs;
                cleared.Add(head);
            }
            return cleared;
        }

        /// <summary>What should leave the field now: hulls below <see cref="RetreatIntegrity"/>, at most
        /// one long-serving hull per side that has a queue, and — when <paramref name="fieldMs"/> has
        /// shrunk below what is on the field — the oldest hull of the side most over its share, until it
        /// fits, never a side's last. Nothing is changed; the host acts and reports with
        /// <see cref="Exit"/>.</summary>
        public List<FightExit> Review(DateTime nowUtc, Func<FightHull, double> integrityOf, double fieldMs)
        {
            var exits = new List<FightExit>();
            var leaving = new HashSet<FightHull>();

            foreach (string name in _order)
            {
                SideState s = _sides[name];
                FightHull oldest = null;
                for (int i = 0; i < s.Field.Count; i++)
                {
                    FightHull h = s.Field[i];
                    double integrity = integrityOf != null ? integrityOf(h) : 1.0;
                    if (integrity < RetreatIntegrity)
                    {
                        exits.Add(new FightExit { Hull = h, Cause = FightExitCause.Retreated });
                        leaving.Add(h);
                        continue;
                    }
                    if ((nowUtc - h.OnFieldSinceUtc).TotalSeconds >= RotationSeconds
                        && (oldest == null || h.OnFieldSinceUtc < oldest.OnFieldSinceUtc))
                        oldest = h;
                }
                if (oldest != null && s.Queue.Count > 0)
                {
                    exits.Add(new FightExit { Hull = oldest, Cause = FightExitCause.Rotated });
                    leaving.Add(oldest);
                }
            }

            double used = 0;
            foreach (SideState s in _sides.Values)
                for (int i = 0; i < s.Field.Count; i++)
                    if (!leaving.Contains(s.Field[i])) used += s.Field[i].CostMs;

            while (used > fieldMs)
            {
                FightHull shed = null;
                double worst = double.MinValue;
                foreach (string name in _order)
                {
                    SideState s = _sides[name];
                    int staying = 0;
                    double sideMs = 0;
                    FightHull oldest = null;
                    for (int i = 0; i < s.Field.Count; i++)
                    {
                        FightHull h = s.Field[i];
                        if (leaving.Contains(h)) continue;
                        staying++;
                        sideMs += h.CostMs;
                        if (oldest == null || h.OnFieldSinceUtc < oldest.OnFieldSinceUtc) oldest = h;
                    }
                    if (staying <= 1) continue;
                    if (sideMs > worst) { worst = sideMs; shed = oldest; }
                }
                if (shed == null) break;
                exits.Add(new FightExit { Hull = shed, Cause = FightExitCause.Shed });
                leaving.Add(shed);
                used -= shed.CostMs;
            }
            return exits;
        }

        /// <summary>The hull has left the field for <paramref name="cause"/>. Rotated goes to the back of
        /// its queue and Shed to the front; Retreated, Destroyed and Disabled leave the fight. False when
        /// the hull was not on the field.</summary>
        public bool Exit(string fleetId, int member, FightExitCause cause, DateTime nowUtc)
        {
            foreach (SideState s in _sides.Values)
            {
                for (int i = 0; i < s.Field.Count; i++)
                {
                    FightHull h = s.Field[i];
                    if (h.Member != member || !string.Equals(h.FleetId, fleetId, StringComparison.Ordinal)) continue;
                    s.Field.RemoveAt(i);
                    switch (cause)
                    {
                        case FightExitCause.Rotated:
                            h.QueuedUtc = nowUtc;
                            s.Queue.Add(h);
                            break;
                        case FightExitCause.Shed:
                            h.QueuedUtc = nowUtc;
                            s.Queue.Insert(0, h);
                            break;
                        case FightExitCause.Retreated:
                            s.Retreated++;
                            break;
                        default:
                            s.Lost++;
                            break;
                    }
                    return true;
                }
            }
            return false;
        }

        /// <summary>Takes every hull of <paramref name="fleetId"/> out of the site, queued or on the
        /// field — the fleet left the fight as a whole (resolved, handed to the duel, retargeted).</summary>
        public int Withdraw(string fleetId)
        {
            int n = 0;
            foreach (SideState s in _sides.Values)
            {
                n += s.Queue.RemoveAll(h => string.Equals(h.FleetId, fleetId, StringComparison.Ordinal));
                n += s.Field.RemoveAll(h => string.Equals(h.FleetId, fleetId, StringComparison.Ordinal));
            }
            return n;
        }

        /// <summary>Physical only: the fight is decided when exactly one side has hulls left (field or
        /// queue). <paramref name="winner"/> is that side; null with every side empty, a draw.</summary>
        public bool IsDecided(out string winner)
        {
            winner = null;
            int standing = 0;
            foreach (string name in _order)
            {
                SideState s = _sides[name];
                if (s.Field.Count + s.Queue.Count == 0) continue;
                standing++;
                winner = name;
            }
            if (standing == 1) return _order.Count > 1;
            winner = null;
            return standing == 0 && _order.Count > 0;
        }

        private SideState Side(string name)
        {
            SideState s;
            if (!_sides.TryGetValue(name, out s))
            {
                s = new SideState();
                _sides[name] = s;
                _order.Add(name);
            }
            return s;
        }

        private FightHull Find(string fleetId, int member)
        {
            foreach (SideState s in _sides.Values)
            {
                for (int i = 0; i < s.Field.Count; i++)
                    if (s.Field[i].Member == member && string.Equals(s.Field[i].FleetId, fleetId, StringComparison.Ordinal))
                        return s.Field[i];
                for (int i = 0; i < s.Queue.Count; i++)
                    if (s.Queue[i].Member == member && string.Equals(s.Queue[i].FleetId, fleetId, StringComparison.Ordinal))
                        return s.Queue[i];
            }
            return null;
        }

        private static double StrengthOf(SideState s)
        {
            double dps = 0;
            for (int i = 0; i < s.Field.Count; i++) dps += Math.Max(0, s.Field[i].Dps);
            for (int i = 0; i < s.Queue.Count; i++) dps += Math.Max(0, s.Queue[i].Dps);
            return dps;
        }

        private static double FieldMs(SideState s)
        {
            double ms = 0;
            for (int i = 0; i < s.Field.Count; i++) ms += s.Field[i].CostMs;
            return ms;
        }
    }
}
