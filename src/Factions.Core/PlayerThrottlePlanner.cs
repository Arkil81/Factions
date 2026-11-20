using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Block classes the player toolbox's step 3 can pause. Every one has a Factions
    /// counterpart, so a paused player loses time, not capability (docs/load-governor.md).</summary>
    public enum ThrottleClass
    {
        Refinery = 0,
        Assembler = 1,
        /// <summary>Ship welders, Build and Repair included: it is a <c>MyShipWelder</c> subtype.</summary>
        Welder = 2,
        /// <summary>Programmable blocks. Never paused: when this class is picked, the player's
        /// scripts run under the limiter's tighter pressure cap instead (<see cref="PbLimiter"/>).</summary>
        Programmable = 3,
    }

    /// <summary>
    /// Which block classes each player has paused: the load governor's player toolbox, step 3
    /// (docs/load-governor.md).
    ///
    /// <para><b>All of the player's grids, one class at a time, costliest first</b> (operator,
    /// 2026-09-24). When a player reaches step 3 while over share, the class that measurably costs
    /// them most is paused on every grid they own. Each further interval they are still over share
    /// adds the next costliest. A class with no measured cost is never paused: it would free
    /// nothing.</para>
    ///
    /// <para><b>Release is whole.</b> When the player's step falls below 3, or they leave, every class
    /// the governor paused for them is released at once. The governor itself already releases one
    /// step per interval, so the lag is there without a second one.</para>
    ///
    /// <para><b>Forced classes</b> are an admin override (<c>factions throttle</c>), held until
    /// cleared and independent of the governor.</para>
    /// </summary>
    public sealed class PlayerThrottlePlanner
    {
        public const int FirstStep = 3;

        public static readonly ThrottleClass[] AllClasses =
            { ThrottleClass.Refinery, ThrottleClass.Assembler, ThrottleClass.Welder, ThrottleClass.Programmable };

        public sealed class Change
        {
            public string Party;
            public ThrottleClass Class;
            public bool On;
            /// <summary>What caused it: "governor" or "admin".</summary>
            public string Cause;
        }

        /// <summary>One player's state for this window, as the governor decided it.</summary>
        public struct PlayerState
        {
            public string Party;
            public int Step;
            public bool Over;
        }

        private readonly double _intervalSeconds;
        private readonly Dictionary<string, List<ThrottleClass>> _governed = new Dictionary<string, List<ThrottleClass>>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _lastAdd = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<ThrottleClass>> _forced = new Dictionary<string, HashSet<ThrottleClass>>(StringComparer.Ordinal);

        public PlayerThrottlePlanner(double intervalSeconds)
        {
            _intervalSeconds = intervalSeconds;
        }

        /// <summary>Applies one window's governor decision.</summary>
        /// <param name="costMs">A player's measured ms per frame for one class this window.</param>
        public List<Change> Update(IEnumerable<PlayerState> players, Func<string, ThrottleClass, double> costMs, DateTime now)
        {
            var changes = new List<Change>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (players != null)
            {
                foreach (PlayerState p in players)
                {
                    if (string.IsNullOrEmpty(p.Party)) continue;
                    seen.Add(p.Party);
                    if (p.Step < FirstStep) { ReleaseGoverned(p.Party, changes); continue; }
                    if (!p.Over) continue;

                    List<ThrottleClass> held;
                    _governed.TryGetValue(p.Party, out held);
                    DateTime last;
                    bool due = held == null || held.Count == 0
                            || !_lastAdd.TryGetValue(p.Party, out last)
                            || (now - last).TotalSeconds >= _intervalSeconds;
                    if (!due) continue;

                    ThrottleClass next;
                    if (!CostliestUnpaused(p.Party, costMs, out next)) continue;
                    if (held == null) { held = new List<ThrottleClass>(); _governed[p.Party] = held; }
                    held.Add(next);
                    _lastAdd[p.Party] = now;
                    if (!IsForced(p.Party, next))
                        changes.Add(new Change { Party = p.Party, Class = next, On = true, Cause = "governor" });
                }
            }

            var gone = new List<string>();
            foreach (string party in _governed.Keys) if (!seen.Contains(party)) gone.Add(party);
            foreach (string party in gone) ReleaseGoverned(party, changes);
            return changes;
        }

        /// <summary>Releases everything the governor holds, e.g. when actuation is switched off.
        /// Forced classes stay.</summary>
        public List<Change> ReleaseAllGoverned()
        {
            var changes = new List<Change>();
            foreach (string party in new List<string>(_governed.Keys)) ReleaseGoverned(party, changes);
            return changes;
        }

        /// <summary>The admin override. Returns the change, or null when the class was already in
        /// that state for this player.</summary>
        public Change Force(string party, ThrottleClass c, bool on)
        {
            if (string.IsNullOrEmpty(party)) return null;
            bool before = IsThrottled(party, c);
            HashSet<ThrottleClass> set;
            if (!_forced.TryGetValue(party, out set))
            {
                if (!on) return null;
                set = new HashSet<ThrottleClass>();
                _forced[party] = set;
            }
            if (on) set.Add(c); else set.Remove(c);
            if (set.Count == 0) _forced.Remove(party);
            bool after = IsThrottled(party, c);
            return before == after ? null : new Change { Party = party, Class = c, On = after, Cause = "admin" };
        }

        public bool IsThrottled(string party, ThrottleClass c)
        {
            return IsGoverned(party, c) || IsForced(party, c);
        }

        public bool IsForced(string party, ThrottleClass c)
        {
            HashSet<ThrottleClass> set;
            return party != null && _forced.TryGetValue(party, out set) && set.Contains(c);
        }

        public bool IsGoverned(string party, ThrottleClass c)
        {
            List<ThrottleClass> held;
            return party != null && _governed.TryGetValue(party, out held) && held.Contains(c);
        }

        /// <summary>Every party with at least one class paused, governed or forced.</summary>
        public List<string> Parties()
        {
            var parties = new HashSet<string>(_governed.Keys, StringComparer.Ordinal);
            foreach (string p in _forced.Keys) parties.Add(p);
            var list = new List<string>(parties);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>The classes paused for a party, in <see cref="AllClasses"/> order.</summary>
        public List<ThrottleClass> ClassesFor(string party)
        {
            var list = new List<ThrottleClass>();
            foreach (ThrottleClass c in AllClasses) if (IsThrottled(party, c)) list.Add(c);
            return list;
        }

        public bool Any { get { return _governed.Count > 0 || _forced.Count > 0; } }

        private bool CostliestUnpaused(string party, Func<string, ThrottleClass, double> costMs, out ThrottleClass best)
        {
            best = ThrottleClass.Refinery;
            double bestCost = 0;
            bool found = false;
            foreach (ThrottleClass c in AllClasses)
            {
                if (IsThrottled(party, c)) continue;
                double cost = costMs != null ? costMs(party, c) : 0.0;
                if (cost > bestCost) { bestCost = cost; best = c; found = true; }
            }
            return found;
        }

        private void ReleaseGoverned(string party, List<Change> changes)
        {
            List<ThrottleClass> held;
            if (!_governed.TryGetValue(party, out held)) return;
            _governed.Remove(party);
            _lastAdd.Remove(party);
            foreach (ThrottleClass c in held)
                if (!IsForced(party, c))
                    changes.Add(new Change { Party = party, Class = c, On = false, Cause = "governor" });
        }

        /// <summary>Lower-case name used in commands, telemetry and notices.</summary>
        public static string Name(ThrottleClass c)
        {
            switch (c)
            {
                case ThrottleClass.Refinery: return "refinery";
                case ThrottleClass.Assembler: return "assembler";
                case ThrottleClass.Programmable: return "programmable";
                default: return "welder";
            }
        }

        public static bool TryParse(string s, out ThrottleClass c)
        {
            c = ThrottleClass.Refinery;
            if (string.IsNullOrEmpty(s)) return false;
            string k = s.Trim().ToLowerInvariant();
            if (k.EndsWith("s")) k = k.Substring(0, k.Length - 1);
            switch (k)
            {
                case "refinery": case "refinerie": c = ThrottleClass.Refinery; return true;
                case "assembler": c = ThrottleClass.Assembler; return true;
                case "welder": case "bar": c = ThrottleClass.Welder; return true;
                case "programmable": case "pb": case "script": c = ThrottleClass.Programmable; return true;
                default: return false;
            }
        }
    }
}
