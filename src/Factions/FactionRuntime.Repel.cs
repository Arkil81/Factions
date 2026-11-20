using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// The grids of an attack the grids left undecided while the defence stood (a repelled raid,
    /// <c>RAID_REPELLED</c>): Core takes the fleet off its dwell at once, and while its grids are in
    /// the world they are seen to break off — they fly <see cref="ManoeuvreKind.Disengage"/> away from
    /// the node before anything takes them home. A bench attacker takes the same leg away from its
    /// fight (<see cref="BenchScenario.BeginAftermath"/>). See docs/fleet-events.md, "Physical
    /// authority and the handoff".
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>How far a repelled grid gets from every hostile grid before it turns for home
        /// (<see cref="CombatLeash.DisengageClearM"/>): measured from the enemy, not the node, so a
        /// fight that drifted off its node does not count as clear the moment it ends.</summary>
        private const double RepelLeaveSeparationM = CombatLeash.DisengageClearM;

        /// <summary>How long a repelled grid is given to get clear before it is handed on anyway.</summary>
        private static readonly TimeSpan RepelLeaveTimeout = TimeSpan.FromSeconds(90);

        /// <summary>Leads of repelled fleets on their return leg, still breaking away from the node,
        /// and when to stop waiting for them.</summary>
        private readonly Dictionary<long, DateTime> _leavingHome = new Dictionary<long, DateTime>();

        private static bool WasRepelled(PendingFleetAction fleet)
        {
            return string.Equals(fleet.RetreatReason, "repelled", StringComparison.Ordinal);
        }

        /// <summary>At the end of a repelled fleet's dwell, with its lead in the world: the lead breaks
        /// away from the hostiles round the node it attacked, flying away from the node
        /// (<see cref="PendingFleetAction.OriginPosKm"/>, where its return leg starts) while none is
        /// near; escorts under escort control follow it in formation, the others are wired home at
        /// once. False when the controller does not hold the lead, and the caller wires
        /// it home as any other fleet.</summary>
        private bool StartRepelledLeave(PendingFleetAction fleet, DateTime now)
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            long eid = fleet.MaterialisedEntityId;
            if (vanilla == null || eid <= 0 || !vanilla.LeaveFrom(eid, fleet.OriginPosKm, RepelLeaveSeparationM))
                return false;
            _leavingHome[eid] = now + RepelLeaveTimeout;
            if (_leavingHome.Count > 32) PruneLeaving(_leavingHome);
            _log.Write("FLEET_REPELLED_LEAVING " + fleet.LogId + " " + fleet.ActorTag + " eid=" + eid
                       + " clear=" + RepelLeaveSeparationM.ToString("0", CultureInfo.InvariantCulture) + "m");
            return true;
        }

        /// <summary>Every frame for a repelled fleet's lead that is still breaking away: once it is
        /// clear, or its time is up, it and its escorts are wired home on the cruise autopilot.
        /// Answers whether the lead was still leaving this frame (the caller skips the rest of it).</summary>
        private bool TickRepelledLeave(PendingFleetAction fleet, DateTime now)
        {
            long eid = fleet.MaterialisedEntityId;
            DateTime giveUp;
            if (!_leavingHome.TryGetValue(eid, out giveUp)) return false;
            var vanilla = _spawner as VanillaFleetSpawner;
            bool clear = vanilla == null || vanilla.IsLeaveDone(eid);
            if (!clear && now < giveUp) return true;
            _leavingHome.Remove(eid);
            bool wired = _spawner.Retarget(eid, fleet.TargetPosKm);
            int escortsRewired = RetargetEscorts(fleet.EscortEntityIds, fleet.TargetPosKm);
            _log.Write("FLEET_ENGAGEMENT_OVER " + fleet.LogId + " " + fleet.ActorTag + " eid=" + eid
                       + " stop=" + fleet.CurrentStopKind + " -> " + (fleet.TargetNodeId ?? "[navigation]")
                       + " rewired=" + wired + (clear ? " left=clear" : " left=timeout")
                       + EscortTally(escortsRewired, EscortCount(fleet)));
            return true;
        }

        private void PruneLeaving(Dictionary<long, DateTime> map)
        {
            var gone = new List<long>();
            foreach (long eid in map.Keys) if (!_spawner.GridExists(eid)) gone.Add(eid);
            foreach (long eid in gone) map.Remove(eid);
        }
    }
}
