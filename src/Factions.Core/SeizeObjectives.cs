using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The follow-through on a colonisation beachhead: turning "I could not get onto Agni" into a
    /// standing reason to keep pushing, and biasing target selection toward it.
    ///
    /// <para><b>Why this reuses campaign objectives rather than adding a concept.</b>
    /// <see cref="CampaignFocus"/> already is the persisted answer to "where is this faction
    /// concentrating", and its own doc calls itself a defensive-only foundation that a later
    /// operational layer may extend. A parallel "colonisation objective" would be a third notion of
    /// the same thing alongside that and the directive list in FAC-047 — which FAC-047 explicitly
    /// warns against.</para>
    ///
    /// <para><b>Scope: body regions.</b> A node carries <see cref="SpatialNode.PlanetId"/> and
    /// <see cref="SpatialNode.Zone"/>, which resolve it to a body's surface or orbit. Nothing on a
    /// node records which authored feature contains it, so a feature-region objective cannot yet be
    /// matched and <see cref="Advances"/> says so by returning false rather than guessing. That is the
    /// case the operator described anyway — a full body, an orbital foothold, and conquest inward.</para>
    ///
    /// <para><b>No catalogue lookups here.</b> The body is resolved once into
    /// <see cref="CampaignObjective.TargetPlanetId"/> when the objective is made; matching is then a
    /// zone check and a string compare. The brain tests every node against every objective on every
    /// pass, so anything heavier belongs outside that loop.</para>
    /// </summary>
    public static class SeizeObjectives
    {
        /// <summary>The objective a beachhead implies, or null when the intent is not one. The
        /// diversion target is where the faction goes; this is what it goes there *for*.</summary>
        public static CampaignObjective FromBeachhead(ColonisationIntent intent, string targetPlanetId)
        {
            if (intent == null || !intent.IsBeachhead || intent.DivertedFrom == null) return null;
            if (string.IsNullOrEmpty(intent.DivertedFrom.Key)) return null;

            return new CampaignObjective
            {
                Kind = CampaignObjectiveKind.SeizeRegion,
                TargetRegionKind = intent.DivertedFrom.Kind,
                TargetRegionKey = intent.DivertedFrom.Key,
                TargetPlanetId = targetPlanetId,
            };
        }

        /// <summary>Whether taking <paramref name="node"/> advances <paramref name="objective"/>.
        /// <para>An objective naming a node (<see cref="CampaignObjective.TargetNodeId"/>) is
        /// <b>node-scoped</b>: that node advances it and nothing else does, whatever region either
        /// stands in. That is the beachhead's last step and the shape a directive naming one node
        /// takes. Everything else is region-scoped and matched below.</para>
        /// <para>False for anything the faction already owns — seizing is about what it does not
        /// hold — and false for a feature region, which cannot yet be matched (see the class note).</para></summary>
        public static bool Advances(CampaignObjective objective, SpatialNode node, string factionTag)
        {
            if (objective == null || node == null) return false;
            if (objective.Kind != CampaignObjectiveKind.SeizeRegion) return false;
            if (node.OwnerFactionTag == factionTag) return false;
            if (!string.IsNullOrEmpty(objective.TargetNodeId))
                return string.Equals(objective.TargetNodeId, node.Id, StringComparison.Ordinal);
            if (!objective.TargetRegionKind.HasValue) return false;
            if (string.IsNullOrEmpty(objective.TargetPlanetId)) return false;
            if (node.OwnerFactionTag == factionTag) return false;

            PlacementRegionKind want = objective.TargetRegionKind.Value;

            NodeZone needZone;
            if (want == PlacementRegionKind.PlanetSurface) needZone = NodeZone.Surface;
            else if (want == PlacementRegionKind.PlanetOrbit) needZone = NodeZone.Orbit;
            else return false;   // Feature / DeepSpace: no membership recorded on a node

            if (node.Zone != needZone) return false;

            return string.Equals(node.PlanetId, objective.TargetPlanetId, StringComparison.Ordinal);
        }

        /// <summary>Total quality boost for a Raid or Conquer against <paramref name="node"/>, summed
        /// across the faction's active seize objectives.
        ///
        /// <para>A weighting, never a filter. A faction pursuing a beachhead should lean toward its
        /// objective, not ignore a rival breaking through behind it — the same rule distance already
        /// follows in Mine's source selection.</para></summary>
        public static double QualityBoost(FactionState state, SpatialNode node, double boostPerObjective)
        {
            if (state == null || node == null || boostPerObjective <= 0) return 0;
            if (state.CampaignFoci == null) return 0;

            double total = 0;
            foreach (CampaignFocus focus in state.CampaignFoci)
            {
                if (focus == null || focus.Objectives == null) continue;
                foreach (CampaignObjective o in focus.Objectives)
                    if (Advances(o, node, state.Tag)) total += boostPerObjective;
            }
            return total;
        }

        /// <summary>Whether this focus already carries an equivalent objective — so a repeated
        /// colonisation decision reinforces one intent rather than stacking duplicates of it.</summary>
        public static bool AlreadyCarries(CampaignFocus focus, CampaignObjective objective)
        {
            if (focus == null || focus.Objectives == null || objective == null) return false;
            foreach (CampaignObjective o in focus.Objectives)
            {
                if (o == null || o.Kind != objective.Kind) continue;
                // A node-scoped objective is the same one only when it names the same node: two
                // conquests of two nodes on the same body are two objectives, not a duplicate.
                if (!string.IsNullOrEmpty(o.TargetNodeId) || !string.IsNullOrEmpty(objective.TargetNodeId))
                {
                    if (string.Equals(o.TargetNodeId, objective.TargetNodeId, StringComparison.Ordinal))
                        return true;
                    continue;
                }
                if (o.TargetRegionKind != objective.TargetRegionKind) continue;
                if (string.Equals(o.TargetPlanetId, objective.TargetPlanetId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

    }
}
