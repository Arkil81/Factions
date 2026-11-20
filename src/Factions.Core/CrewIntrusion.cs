using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>FAC-440 intruder rules. A boarder becomes an intruder by acting against the hull,
    /// never by standings: a factionless player reads as an enemy of everyone, so relations alone
    /// would make every visitor hostile.</summary>
    public static class CrewIntrusion
    {
        /// <summary>Damage a player deals on purpose. Collisions, falls, the environment and suit
        /// failure are not acts; neither is welding.</summary>
        private static readonly HashSet<string> Acts = new HashSet<string>
        {
            "Grind", "Drill", "Bullet", "Rocket", "Explosion", "Weapon", "Bolt", "Mine", "Fire",
        };

        public static bool IsHostileAct(string damageType)
        {
            return damageType != null && Acts.Contains(damageType);
        }

        /// <summary>Whether an actor stands outside the hull's side: its faction when it has one,
        /// otherwise its solo owner. Everyone is outside an unowned hull. Identity 0 is nobody.</summary>
        public static bool IsOutsider(long actor, long actorFaction, long hullFaction, long hullOwner)
        {
            if (actor == 0) return false;
            if (hullFaction != 0) return actorFaction != hullFaction;
            if (hullOwner != 0) return actor != hullOwner;
            return true;
        }
    }

    /// <summary>Who has acted against each hull. An intruder's faction-mates are intruders on that
    /// hull too. Held for as long as the hull exists in the world; nothing is persisted.</summary>
    public sealed class IntruderLedger
    {
        private readonly Dictionary<long, HashSet<long>> _identities = new Dictionary<long, HashSet<long>>();
        private readonly Dictionary<long, HashSet<long>> _factions = new Dictionary<long, HashSet<long>>();

        /// <summary>Records an act. True when the identity was not already an intruder there.</summary>
        public bool Mark(long hull, long identity, long faction)
        {
            if (identity == 0) return false;
            if (faction != 0) Set(_factions, hull).Add(faction);
            return Set(_identities, hull).Add(identity);
        }

        public bool IsIntruder(long hull, long identity, long faction)
        {
            if (identity == 0) return false;
            HashSet<long> set;
            if (_identities.TryGetValue(hull, out set) && set.Contains(identity)) return true;
            return faction != 0 && _factions.TryGetValue(hull, out set) && set.Contains(faction);
        }

        public bool Knows(long hull) { return _identities.ContainsKey(hull); }

        public int Count(long hull)
        {
            HashSet<long> set;
            return _identities.TryGetValue(hull, out set) ? set.Count : 0;
        }

        public IEnumerable<long> Hulls { get { return _identities.Keys; } }

        public void Forget(long hull)
        {
            _identities.Remove(hull);
            _factions.Remove(hull);
        }

        public void Clear()
        {
            _identities.Clear();
            _factions.Clear();
        }

        private static HashSet<long> Set(Dictionary<long, HashSet<long>> map, long hull)
        {
            HashSet<long> set;
            if (!map.TryGetValue(hull, out set)) { set = new HashSet<long>(); map[hull] = set; }
            return set;
        }
    }

    /// <summary>A hull's crew are armed while an intruder is aboard and stand down after a quiet
    /// minute, so an intruder stepping out through an airlock does not disarm them.</summary>
    public sealed class CrewAlarm
    {
        public const double StandDownSeconds = 60;
        public bool Active { get; private set; }
        public double QuietSeconds { get; private set; }

        public bool Update(bool intruderAboard, double elapsedSeconds)
        {
            if (intruderAboard) { Active = true; QuietSeconds = 0; }
            else if (Active && elapsedSeconds > 0)
            {
                QuietSeconds += elapsedSeconds;
                if (QuietSeconds >= StandDownSeconds) { Active = false; QuietSeconds = 0; }
            }
            return Active;
        }
    }
}
