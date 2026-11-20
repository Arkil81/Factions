using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a lane cost its last dispatch, so the next choice of source can weigh it.
    ///
    /// <para>A dispatch through hostile space records the cost of the option it took
    /// (<see cref="TransitPlanner.ConsiderEscort"/>) and what a kilometre of that route cost its hull in
    /// fuel. A later match reads the cost back as extra distance (<see cref="PenaltyKm"/>), so a
    /// cleaner source wins once the extra kilometres to it cost less than the risk did. A clean
    /// dispatch on the same lane clears the entry. An entry older than <see cref="Memory"/> is ignored:
    /// standings, Guards and borders move. Not persisted.</para></summary>
    public sealed class TransitLedger
    {
        public static readonly TimeSpan Memory = TimeSpan.FromHours(1);

        private struct Entry
        {
            public double CostCredits;
            public double CreditsPerKm;
            public DateTime StampUtc;
        }

        private readonly Dictionary<string, Dictionary<string, Entry>> _byActor =
            new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);

        public void Record(string actorTag, string sourceNodeId, string deliverNodeId, double costCredits,
                           double creditsPerKm, DateTime nowUtc)
        {
            if (actorTag == null || sourceNodeId == null || deliverNodeId == null) return;
            Dictionary<string, Entry> lanes;
            string key = sourceNodeId + ">" + deliverNodeId;
            if (costCredits <= 0)
            {
                if (_byActor.TryGetValue(actorTag, out lanes)) lanes.Remove(key);
                return;
            }
            if (!_byActor.TryGetValue(actorTag, out lanes))
            {
                lanes = new Dictionary<string, Entry>(StringComparer.Ordinal);
                _byActor[actorTag] = lanes;
            }
            lanes[key] = new Entry { CostCredits = costCredits, CreditsPerKm = creditsPerKm, StampUtc = nowUtc };
            if (lanes.Count > 256) Prune(lanes, nowUtc);
        }

        /// <summary>Whether <paramref name="actorTag"/> has any lane on record: the cheap test a matcher
        /// runs before it re-orders anything.</summary>
        public bool HasAny(string actorTag)
        {
            Dictionary<string, Entry> lanes;
            return actorTag != null && _byActor.TryGetValue(actorTag, out lanes) && lanes.Count > 0;
        }

        /// <summary>The lane's recorded cost as kilometres of the hull that flew it. 0 when nothing
        /// fresh is recorded, and 0 when the fuel was unpriced: a cost that cannot be set against
        /// distance does not move the choice.</summary>
        public double PenaltyKm(string actorTag, string sourceNodeId, string deliverNodeId, DateTime nowUtc)
        {
            Dictionary<string, Entry> lanes;
            if (actorTag == null || !_byActor.TryGetValue(actorTag, out lanes)) return 0;
            Entry e;
            if (!lanes.TryGetValue(sourceNodeId + ">" + deliverNodeId, out e)) return 0;
            if (nowUtc - e.StampUtc > Memory || e.CreditsPerKm <= 0) return 0;
            return e.CostCredits / e.CreditsPerKm;
        }

        private static void Prune(Dictionary<string, Entry> lanes, DateTime nowUtc)
        {
            var stale = new List<string>();
            foreach (var kv in lanes) if (nowUtc - kv.Value.StampUtc > Memory) stale.Add(kv.Key);
            for (int i = 0; i < stale.Count; i++) lanes.Remove(stale[i]);
        }
    }

    /// <summary>What an enemy's space has cost a faction's lanes, per region of that enemy: the
    /// weight war planning reads when it ranks conquests (<c>FactionBrain.AddConquers</c>).
    ///
    /// <para>Fed by every priced leg (the credits of the option taken: the risk accepted, a detour's
    /// fuel, an escort's) and by every robbery a trespass catch makes. It halves every
    /// <see cref="HalfLife"/>, so a lane that stops costing stops counting. Bounded at
    /// <see cref="MaxEntries"/>, dropping the lightest. Not persisted: lanes refill it within
    /// hours.</para></summary>
    public sealed class TransitDenialBook
    {
        public static readonly TimeSpan HalfLife = TimeSpan.FromHours(12);
        public const int MaxEntries = 64;

        private struct Entry
        {
            public double Credits;
            public DateTime StampUtc;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public int Count { get { return _entries.Count; } }

        /// <summary>Adds <paramref name="credits"/> to the enemy region's weight and returns the
        /// weight after.</summary>
        public double Add(string enemyTag, string regionKey, double credits, DateTime nowUtc)
        {
            if (enemyTag == null || regionKey == null || credits <= 0) return Weight(enemyTag, regionKey, nowUtc);
            string key = enemyTag + "|" + regionKey;
            double weight = credits + Weight(enemyTag, regionKey, nowUtc);
            _entries[key] = new Entry { Credits = weight, StampUtc = nowUtc };
            if (_entries.Count > MaxEntries) DropLightest(nowUtc);
            return weight;
        }

        /// <summary>The decayed weight of the enemy's region, in credits.</summary>
        public double Weight(string enemyTag, string regionKey, DateTime nowUtc)
        {
            if (enemyTag == null || regionKey == null) return 0;
            Entry e;
            if (!_entries.TryGetValue(enemyTag + "|" + regionKey, out e)) return 0;
            return Decayed(e, nowUtc);
        }

        private static double Decayed(Entry e, DateTime nowUtc)
        {
            double hours = (nowUtc - e.StampUtc).TotalHours;
            if (hours <= 0) return e.Credits;
            return e.Credits * Math.Pow(0.5, hours / HalfLife.TotalHours);
        }

        private void DropLightest(DateTime nowUtc)
        {
            string lightest = null;
            double least = double.MaxValue;
            foreach (var kv in _entries)
            {
                double w = Decayed(kv.Value, nowUtc);
                if (w < least || (w == least && string.CompareOrdinal(kv.Key, lightest) < 0)) { least = w; lightest = kv.Key; }
            }
            if (lightest != null) _entries.Remove(lightest);
        }
    }
}
