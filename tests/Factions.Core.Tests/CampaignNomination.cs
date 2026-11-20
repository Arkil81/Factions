using System.Collections.Generic;
using System.Linq;
using Factions.Core;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Reading the brain's offensive output the way it is shaped now: the brain nominates a
    /// <see cref="ActionKind.Campaign"/> naming a region (or a single node) with a
    /// <see cref="CampaignCause"/>, and <c>FleetManager</c> picks the node and the fleet kind.
    /// These helpers answer "did the brain nominate a raid campaign that covers node X" — the
    /// question the old <c>Kind == Raid &amp;&amp; TargetNodeId == X</c> assertion was really asking.
    /// </summary>
    internal static class CampaignNomination
    {
        /// <summary>Whether <paramref name="intent"/>'s objective would cover
        /// <paramref name="nodeId"/> — it names that node, or names the region the node stands in.</summary>
        public static bool Covers(FactionIntent intent, IWorldView world, string nodeId)
        {
            if (intent.Kind != ActionKind.Campaign || !intent.ObjectiveKind.HasValue) return false;
            if (intent.ObjectiveNodeId != null) return intent.ObjectiveNodeId == nodeId;
            NodeRegion region;
            if (intent.TargetRegion == null || !NodeRegion.TryFromKey(intent.TargetRegion, out region)) return false;
            SpatialNode node;
            if (!world.Graph.Nodes.TryGetValue(nodeId, out node)) return false;
            return region.Contains(node);
        }

        private static IEnumerable<Candidate> Matching(Decision d, IWorldView world, string nodeId,
            CampaignObjectiveKind kind, CampaignCause? cause)
        {
            return d.Scored.Where(c => c.Intent.ObjectiveKind == kind
                                    && (!cause.HasValue || c.Intent.Cause == cause.Value)
                                    && Covers(c.Intent, world, nodeId));
        }

        /// <summary>The raid-campaign candidate covering <paramref name="nodeId"/> for an ordinary
        /// motive — predation or a grudge — or null. Deliberately not the antagonist's own campaign
        /// (<see cref="CampaignCause.Terror"/>) and not a retaliation: those two are nominated by
        /// different rules and have their own helpers.</summary>
        public static Candidate Raid(Decision d, IWorldView world, string nodeId)
        {
            return d.Scored.FirstOrDefault(c => c.Intent.ObjectiveKind == CampaignObjectiveKind.Terrorise
                                             && (c.Intent.Cause == CampaignCause.Predation
                                              || c.Intent.Cause == CampaignCause.Grudge)
                                             && Covers(c.Intent, world, nodeId));
        }

        /// <summary>The retaliation raid-campaign candidate covering <paramref name="nodeId"/>, or null.</summary>
        public static Candidate Retaliation(Decision d, IWorldView world, string nodeId)
        {
            return Matching(d, world, nodeId, CampaignObjectiveKind.Terrorise, CampaignCause.Retaliation)
                .FirstOrDefault();
        }

        /// <summary>Every retaliation raid-campaign candidate, wherever it points.</summary>
        public static List<Candidate> AllRetaliations(Decision d)
        {
            return d.Scored.Where(c => c.Intent.ObjectiveKind == CampaignObjectiveKind.Terrorise
                                    && c.Intent.Cause == CampaignCause.Retaliation).ToList();
        }

        /// <summary>The conquest-campaign candidate covering <paramref name="nodeId"/>, or null.</summary>
        public static Candidate Conquest(Decision d, IWorldView world, string nodeId)
        {
            return Matching(d, world, nodeId, CampaignObjectiveKind.SeizeRegion, null).FirstOrDefault();
        }

        /// <summary>Every conquest-campaign candidate, wherever it points.</summary>
        public static List<Candidate> AllConquests(Decision d)
        {
            return d.Scored.Where(c => c.Intent.ObjectiveKind == CampaignObjectiveKind.SeizeRegion).ToList();
        }

        /// <summary>The ids of every node covered by some raid-campaign nomination — what the brain
        /// has decided is worth attacking, now that it names places rather than nodes.</summary>
        public static string[] RaidedNodeIds(Decision d, IWorldView world)
        {
            var raids = AllRaids(d);
            return world.Graph.Nodes.Keys
                .Where(id => raids.Any(c => Covers(c.Intent, world, id)))
                .OrderBy(id => id, System.StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>Every raid-campaign candidate, wherever it points.</summary>
        public static List<Candidate> AllRaids(Decision d)
        {
            return d.Scored.Where(c => c.Intent.ObjectiveKind == CampaignObjectiveKind.Terrorise).ToList();
        }
    }
}
