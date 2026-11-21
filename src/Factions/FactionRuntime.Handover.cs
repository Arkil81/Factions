using System;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// A fleet grid Nexus carried across a sector boundary, on the authority (see
    /// <see cref="SectorHandover"/>). The grid keeps its EntityId, so the fleet's
    /// <see cref="PendingFleetAction.MaterialisedEntityId"/> or escort entry stays valid through the
    /// move; only which server flies it changes, and the spawner routes it there. What the runtime
    /// owes it is its leg, sent again to the server now flying it.
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>The fleet <paramref name="eid"/> flies for, as its kind and faction: false for a
        /// grid no pending fleet holds (a departing survivor, a wreck), which is then not handed off.</summary>
        internal bool FleetOfGridForHandover(long eid, out ActionKind kind, out string factionTag, out long factionId)
        {
            kind = default(ActionKind);
            factionTag = null;
            factionId = 0;
            PendingFleetAction fleet;
            if (!TryFleetOfGrid(eid, out fleet)) return false;
            kind = fleet.Kind;
            factionTag = fleet.ActorTag;
            return true;
        }

        /// <summary>A grid now flown by <paramref name="server"/> after a sector crossing: a lead is
        /// put back on its fleet's leg; an escort takes its formation from the per-frame ask.</summary>
        internal void ResumeHandedOver(long eid, int server)
        {
            PendingFleetAction fleet;
            if (!TryFleetOfGrid(eid, out fleet)) return;
            _stalledSince.Remove(eid);
            bool lead = fleet.MaterialisedEntityId == eid;
            bool wired = lead && _spawner.Retarget(eid, fleet.TargetPosKm);
            _log.Write("FLEET_HANDED_OVER " + fleet.LogId + " " + fleet.ActorTag + " eid=" + eid + " server=" + server
                       + (lead ? " rewired=" + wired : " escort"));
        }
    }
}
