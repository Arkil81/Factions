using System;
using System.Collections.Generic;

namespace Factions.Core
{
    public static partial class PendingFleetResolver
    {
        /// <summary>The <see cref="PendingFleetAction.RetreatReason"/> of a defence sent home because
        /// the node it stood at is no longer its side's.</summary>
        public const string NodeLostReason = "node_lost";

        /// <summary>Who owns an attack's target now, when that ownership leaves nobody there to
        /// attack; null while it is still worth fighting. Any attack is moot on a node its own faction
        /// now holds. A Raid or Retaliate is also moot on a node nobody holds
        /// (<see cref="NeutralFaction"/>): there is no hold to strip. A Conquer is not — a derelict is
        /// still its to take.
        /// <para>Only the mission leg is asked — the arrival and the engagement dwell — not the trip
        /// home or a waypoint on the way.</para></summary>
        public static string TargetChangedHands(PendingFleetAction fleet, WorldModel world)
        {
            if (!EngagesOnArrival(fleet.Kind) || !IsMissionArrival(fleet)) return null;
            if (fleet.TargetNodeId == null) return null;
            SpatialNode node;
            if (!world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out node)) return null;
            string owner = node.OwnerFactionTag ?? NeutralFaction.Tag;
            if (owner == fleet.ActorTag) return owner;
            if (fleet.Kind != ActionKind.Conquer && owner == NeutralFaction.Tag) return owner;
            return null;
        }

        /// <summary>Whether a defence's node has left the faction it was sent to defend
        /// (<see cref="PendingFleetAction.TargetTag"/>) for one the defence has no reason to stand
        /// for: neither its own faction nor a pact partner of it.</summary>
        private static bool DefendedNodeLost(IList<FactionState> states, PendingFleetAction defence, SpatialNode node)
        {
            string owner = node.OwnerFactionTag;
            if (defence.TargetTag == null || owner == defence.TargetTag || owner == defence.ActorTag) return false;
            for (int i = 0; i < states.Count; i++)
                if (states[i] != null && owner != null && states[i].Tag == owner)
                    return !Diplomacy.InPactWith(states[i], defence.ActorTag);
            return true;
        }

        /// <summary>Settles fleets standing at a node that changed hands under them, before any fight
        /// at it is observed or met.
        /// <list type="bullet">
        ///   <item>An attack on its engagement dwell whose target is moot (<see cref="TargetChangedHands"/>)
        ///   ends the dwell now; its resolution reads the new owner and leaves without fighting.</item>
        ///   <item>A Guard standing its window, or a Reinforce holding its dwell, at a node that has
        ///   left the faction it came to defend (<see cref="DefendedNodeLost"/>) is sent home
        ///   (<see cref="BeginRetreat"/>, <see cref="NodeLostReason"/>). It is no longer a defender, so
        ///   no meeting pairs it with an attack on the node's new owner.</item>
        /// </list></summary>
        private static void SettleLostGround(IList<FactionState> states, WorldModel world, FactionTuning tuning,
                                             IList<string> log)
        {
            DateTime now = world.UtcNow;
            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                if (st == null) continue;
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.IsBench || f.Intercepted || f.TargetNodeId == null) continue;

                    if (EngagesOnArrival(f.Kind) && f.CurrentStopKind == FleetStopKind.Engage
                        && !f.Resolved && f.RetreatReason == null
                        && f.EstimatedArrivalUtc > now && TargetChangedHands(f, world) != null)
                    {
                        f.EstimatedArrivalUtc = now;
                        st.PendingFleets[fi] = f;
                        continue;
                    }

                    if (!f.StandsAsDefender(now, tuning)) continue;
                    SpatialNode node;
                    if (!world.Graph.Nodes.TryGetValue(f.TargetNodeId, out node)) continue;
                    if (!DefendedNodeLost(states, f, node)) continue;
                    if (!BeginRetreat(ref f, now, tuning, NodeLostReason)) continue;
                    st.PendingFleets[fi] = f;
                    log.Add("DEFENCE_WITHDRAWN " + f.LogId + " " + f.ActorTag + " kind=" + f.Kind
                            + " @ " + f.TargetNodeId + " owner=" + (node.OwnerFactionTag ?? NeutralFaction.Tag)
                            + " reason=" + NodeLostReason);
                }
            }
        }
        /// <summary>Resolves an attack whose target changed hands while it flew or dwelt
        /// (<see cref="TargetChangedHands"/>): no fight, no hold stripped, no loot, every siege dwell
        /// still queued dropped, and the fleet goes home. A Raid files as landed on the faction it was
        /// sent against, whose loss of the node its earlier cycles are part of; a Conquer files
        /// nothing, because the claim that took the node already did. Returns false, changing nothing,
        /// while the target is still the victim's.</summary>
        private static bool ResolveChangedHands(IList<FactionState> states, (int si, int fi) fleetRef,
            ref PendingFleetAction fleet, WorldModel world, double ratio, IList<string> log)
        {
            string owner = TargetChangedHands(fleet, world);
            if (owner == null) return false;
            int dropped = 0;
            if (fleet.RemainingStops != null)
                for (int i = fleet.RemainingStops.Count - 1; i >= 0; i--)
                    if (fleet.RemainingStops[i].Kind == FleetStopKind.Engage)
                    {
                        fleet.RemainingStops.RemoveAt(i);
                        dropped++;
                    }
            if (fleet.Kind != ActionKind.Conquer)
                world.RecordEvent(FactionEventKind.RaidResolved, fleet.ActorTag, fleet.TargetTag,
                                  fleet.TargetNodeId, 0, ratio);
            log.Add("TARGET_CHANGED_HANDS " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                    + " @ " + fleet.TargetNodeId + " from=" + (fleet.TargetTag ?? "?") + " to=" + owner
                    + (dropped > 0 ? " siege_dropped=" + dropped : ""));
            RecordCampaignOutcome(states, fleetRef, CampaignOutcome.Landed, new CombatStrength.DuelBracket(), world);
            return true;
        }
    }
}
