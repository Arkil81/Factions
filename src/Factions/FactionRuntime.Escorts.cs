using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>The observation loop's side of escort control: a materialised squadron's escorts
    /// are handed to the flight controller to hold formation on their anchor, and fight beside it
    /// while it fights. See docs/fleet-tactics.md, slice 2.</summary>
    public sealed partial class FactionRuntime
    {
        private Func<long, bool> _gridExists;

        /// <summary>Called every frame for a materialised fleet whose lead is alive and still its own.
        /// With escort control on, each escort is asked to hold its slot on its anchor: the resolved
        /// order's anchor (the lead) while it lives, else the next live hull in squadron order
        /// (<see cref="Formation.PickAnchor"/>). The lead's own loss closes its escorts in the same
        /// pass, so that fallback only ever covers the frames before it is settled. An escort with
        /// no live anchor, and every escort once control is switched off, is handed back to the
        /// cruise autopilot on the fleet's waypoint. Nothing here touches the lead, the fleet's
        /// arrival, stall or drift, or its escort list.</summary>
        private void FlyEscorts(PendingFleetAction fleet)
        {
            List<long> escorts = fleet.EscortEntityIds;
            if (escorts == null || escorts.Count == 0) return;
            var vanilla = _spawner as VanillaFleetSpawner;
            if (vanilla == null) return;

            if (_gridExists == null) _gridExists = _spawner.GridExists;
            long lead = fleet.MaterialisedEntityId;
            long preferred = TacticResolver.ResolveAnchor(new TacticRequest { AnchorEntityId = lead });

            for (int i = 0; i < escorts.Count; i++)
            {
                long eid = escorts[i];
                if (eid <= 0 || IsCommanded(eid)) continue;   // a commander's order flies it
                long anchor = Formation.PickAnchor(preferred, lead, escorts, eid, _gridExists);
                int slot = anchor > 0 ? Formation.SlotIndex(escorts, eid, anchor) : -1;
                if (slot < 0)
                {
                    vanilla.StopEscorting(eid, fleet.TargetPosKm);
                    continue;
                }
                vanilla.FlyEscort(eid, anchor, slot, fleet.TargetPosKm);
            }
        }
    }
}
