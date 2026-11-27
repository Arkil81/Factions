using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Estimated wealth on the Factions page Overview. Ordinals are the wire's.</summary>
    public enum WealthBand : byte
    {
        Unknown = 0,
        Poor = 1,
        Modest = 2,
        Comfortable = 3,
        Prosperous = 4,
        Rich = 5,
    }

    /// <summary>Estimated fleet strength on the Factions page Overview. Ordinals are the wire's.</summary>
    public enum StrengthBand : byte
    {
        Unknown = 0,
        Negligible = 1,
        Light = 2,
        Moderate = 3,
        Strong = 4,
        Overwhelming = 5,
    }

    /// <summary>The strongest armed fleet of one faction a viewer has seen at Armament, and when.</summary>
    public struct FleetSighting
    {
        public float Dps;
        public DateTime Utc;
    }

    /// <summary>
    /// Per observer, per faction: the strongest armed fleet seen at Armament within the window, and
    /// when (docs/fog-of-war.md → The Factions page). A fact about the faction, not a remembered
    /// fleet: nothing here says where it was or where it went.
    /// </summary>
    public sealed class FleetSightings
    {
        private readonly Dictionary<string, FleetSighting> _byTag =
            new Dictionary<string, FleetSighting>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return _byTag.Count; } }

        public IEnumerable<KeyValuePair<string, FleetSighting>> Entries { get { return _byTag; } }

        /// <summary>Records one sighting. It replaces the held one when it is at least as strong, or
        /// when the held one has aged out of <paramref name="window"/>. True when anything changed.</summary>
        public bool Record(string tag, double dps, DateTime utc, TimeSpan window)
        {
            if (string.IsNullOrEmpty(tag) || dps <= 0) return false;
            FleetSighting held;
            if (_byTag.TryGetValue(tag, out held))
            {
                if (utc < held.Utc) return false;
                bool aged = window > TimeSpan.Zero && utc - held.Utc >= window;
                if (!aged && dps < held.Dps) return false;
                if (!aged && (float)dps == held.Dps && utc == held.Utc) return false;
            }
            _byTag[tag] = new FleetSighting { Dps = (float)dps, Utc = utc };
            return true;
        }

        /// <summary>The held sighting, when it is inside <paramref name="window"/>.</summary>
        public bool TryGet(string tag, DateTime nowUtc, TimeSpan window, out FleetSighting sighting)
        {
            sighting = default(FleetSighting);
            if (string.IsNullOrEmpty(tag) || !_byTag.TryGetValue(tag, out sighting)) return false;
            return window <= TimeSpan.Zero || nowUtc - sighting.Utc < window;
        }

        public void Load(string tag, FleetSighting sighting)
        {
            if (!string.IsNullOrEmpty(tag) && sighting.Dps > 0) _byTag[tag] = sighting;
        }

        /// <summary>Drops sightings older than the window. Returns how many.</summary>
        public int Compact(DateTime nowUtc, TimeSpan window)
        {
            if (window <= TimeSpan.Zero) return 0;
            var stale = new List<string>();
            foreach (KeyValuePair<string, FleetSighting> p in _byTag)
                if (nowUtc - p.Value.Utc >= window) stale.Add(p.Key);
            foreach (string k in stale) _byTag.Remove(k);
            return stale.Count;
        }
    }

    /// <summary>What one viewer believes about one faction: the Overview's three rows, before banding.</summary>
    public struct FactionEstimate
    {
        /// <summary>Nodes the viewer has graded at Identity or better and believes this faction holds.</summary>
        public int KnownNodes;
        /// <summary>Stage score of those nodes (<see cref="FactionIntelCensus.StageScore"/>'s weights).</summary>
        public double WealthScore;
        /// <summary>Σ remembered defence of those nodes plus the strongest sighting in the window.</summary>
        public double StrengthDps;
        /// <summary>Known nodes that carried a defence reading.</summary>
        public int DefenceReadings;
        public bool HasSighting;
        /// <summary>The newest reading behind <see cref="StrengthDps"/>; default when there is none.</summary>
        public DateTime StrengthUtc;
        public WealthBand Wealth;
        public StrengthBand Strength;
    }

    public static class FactionIntelEstimate
    {
        public static double StageWeight(NodeStage stage)
        {
            switch (stage)
            {
                case NodeStage.Outpost: return 1.0;
                case NodeStage.Settlement: return 2.0;
                case NodeStage.Hub: return 4.0;
                case NodeStage.Capital: return 8.0;
                default: return 0.0;
            }
        }

        public static WealthBand Wealth(double score, int knownNodes, FactionTuning t)
        {
            if (knownNodes <= 0 || t == null) return WealthBand.Unknown;
            if (score < t.IntelWealthModestScore) return WealthBand.Poor;
            if (score < t.IntelWealthComfortableScore) return WealthBand.Modest;
            if (score < t.IntelWealthProsperousScore) return WealthBand.Comfortable;
            if (score < t.IntelWealthRichScore) return WealthBand.Prosperous;
            return WealthBand.Rich;
        }

        public static StrengthBand Strength(double dps, bool anyReading, FactionTuning t)
        {
            if (!anyReading || t == null) return StrengthBand.Unknown;
            if (dps < t.IntelStrengthLightDps) return StrengthBand.Negligible;
            if (dps < t.IntelStrengthModerateDps) return StrengthBand.Light;
            if (dps < t.IntelStrengthStrongDps) return StrengthBand.Moderate;
            if (dps < t.IntelStrengthOverwhelmingDps) return StrengthBand.Strong;
            return StrengthBand.Overwhelming;
        }

        public static TimeSpan FleetWindow(FactionTuning t)
        {
            return t != null && t.IntelFleetWindowHours > 0 ? TimeSpan.FromHours(t.IntelFleetWindowHours) : TimeSpan.Zero;
        }

        /// <summary>
        /// The viewer's estimate of <paramref name="tag"/>, from their own stores only. A node counts
        /// when the freshest live entry across <paramref name="sources"/> names this faction its owner
        /// and the best grade is Identity or better; its stage is that grade's, and its defence the
        /// freshest reading (<see cref="KnowledgeView.GradeOf"/>). The viewer's own estate is not this
        /// path: own figures are exact and come from the world.
        /// </summary>
        public static FactionEstimate Estimate(string tag, SpatialGraph graph, IList<FactionKnowledge> sources,
                                               FleetSightings sightings, DateTime nowUtc, TimeSpan horizon,
                                               FactionTuning tuning)
        {
            var e = new FactionEstimate();
            if (string.IsNullOrEmpty(tag) || graph == null) return e;
            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node == null) continue;
                string owner;
                if (!TryBelievedOwner(node.Id, sources, nowUtc, horizon, out owner)) continue;
                if (!string.Equals(owner, tag, StringComparison.OrdinalIgnoreCase)) continue;
                KnownNode grade = KnowledgeView.GradeOf(null, sources, node, nowUtc, horizon);
                if (!IntelGrade.ShowsIdentity(grade.Tier)) continue;
                e.KnownNodes++;
                e.WealthScore += StageWeight(grade.Stage);
                if (grade.Defence != DefenceBand.Unknown)
                {
                    e.DefenceReadings++;
                    e.StrengthDps += grade.DefenceDps;
                    if (grade.DefenceUtc > e.StrengthUtc) e.StrengthUtc = grade.DefenceUtc;
                }
            }
            FleetSighting s;
            if (sightings != null && sightings.TryGet(tag, nowUtc, FleetWindow(tuning), out s))
            {
                e.HasSighting = true;
                e.StrengthDps += s.Dps;
                if (s.Utc > e.StrengthUtc) e.StrengthUtc = s.Utc;
            }
            e.Wealth = Wealth(e.WealthScore, e.KnownNodes, tuning);
            e.Strength = Strength(e.StrengthDps, e.DefenceReadings > 0 || e.HasSighting, tuning);
            return e;
        }

        /// <summary>The owner at the freshest live detection across the stores.</summary>
        private static bool TryBelievedOwner(string nodeId, IList<FactionKnowledge> sources, DateTime nowUtc,
                                             TimeSpan horizon, out string owner)
        {
            owner = null;
            if (sources == null) return false;
            DateTime best = DateTime.MinValue;
            bool found = false;
            for (int i = 0; i < sources.Count; i++)
            {
                KnownNode entry;
                if (sources[i] == null || !sources[i].TryGetGrade(nodeId, nowUtc, horizon, out entry)) continue;
                if (!entry.HasDetail || entry.LastSeenUtc < best) continue;
                best = entry.LastSeenUtc;
                owner = entry.OwnerTag;
                found = true;
            }
            return found && !string.IsNullOrEmpty(owner);
        }
    }
}
