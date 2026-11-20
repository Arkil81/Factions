using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    public static partial class PendingFleetResolver
    {
        /// <summary>The <see cref="PendingFleetAction.ForcedRatioCause"/> values the host writes when a
        /// fleet's lead grid is taken out by hostile action in the world: destroyed, captured, or
        /// disabled (<see cref="DisabledHull"/>). A grid decided each of these; the stall and the
        /// bookkeeping causes are not among them.</summary>
        public static bool IsCombatLoss(string cause)
        {
            return cause == "grid_destroyed" || cause == "grid_captured" || cause == DisabledHull.Cause;
        }

        /// <summary>Whether <paramref name="fleet"/> is an attack (Raid, Retaliate, Conquer) whose lead
        /// grid was lost in combat (<see cref="IsCombatLoss"/>) before its mission resolved -- on its
        /// way in, on its engagement dwell, or in an interception hold. Such an attack has lost: it
        /// lands nothing (<see cref="SettleLeadLost"/>). An attack already on its way home is not
        /// one: its mission resolved before the lead was lost.</summary>
        public static bool LeadLostInCombat(PendingFleetAction fleet)
        {
            return fleet.ForcedRatio.HasValue && IsCombatLoss(fleet.ForcedRatioCause) && MissionHangsOnLead(fleet);
        }

        /// <summary>Whether losing <paramref name="fleet"/>'s lead in combat now loses its mission: an
        /// attack (Raid, Retaliate, Conquer) whose mission has not resolved. The host asks this before
        /// writing the loss, to keep the surviving escorts in the world to fly out.</summary>
        public static bool MissionHangsOnLead(PendingFleetAction fleet)
        {
            if (!EngagesOnArrival(fleet.Kind) || fleet.Intercepted || fleet.Resolved) return false;
            return fleet.RetreatReason == null && fleet.CurrentStopKind != FleetStopKind.Return;
        }

        /// <summary>Settles every attack whose lead was lost in combat (<see cref="LeadLostInCombat"/>)
        /// before anything else in the pass reads it.</summary>
        private static void SettleLostLeads(IList<FactionState> states, WorldModel world, FactionTuning tuning,
                                            IList<string> log)
        {
            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                if (st == null) continue;
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                    if (LeadLostInCombat(st.PendingFleets[fi]))
                        SettleLeadLost((si, fi), states, world, tuning, log);
            }
        }

        /// <summary>An attack whose lead grid was lost in combat has lost its meeting, whatever part
        /// of the dwell had run: no effect, no loot, no hold stripped, and every siege cycle still
        /// queued dropped with the fleet. It is filed as a broken-off attack is -- the event, both
        /// ledgers, the repelled-attack memory, the campaign's intercepted outcome and a
        /// <see cref="CombatOutcome"/> -- decided by the grids (<see cref="SourcePhysical"/>). A squadron's
        /// members still standing (<see cref="PendingFleetAction.HullsToReturn"/>: the host has already
        /// taken the lead and any escort lost in the world out of it) are credited home at the
        /// integrity the grids measured and fly out of the fight (<see cref="PendingFleetAction.HullsLeaving"/>);
        /// a fleet that is not a squadron was that one grid, and is written off whole.</summary>
        private static void SettleLeadLost((int si, int fi) raidRef, IList<FactionState> states, WorldModel world,
                                           FactionTuning tuning, IList<string> log)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            string cause = raid.ForcedRatioCause;
            string node = raid.TargetNodeId;
            SpatialNode targetNode = NodeOf(world, node);
            bool dwelling = raid.CurrentStopKind == FleetStopKind.Engage && node != null;
            List<(int si, int fi)> force = dwelling
                ? DwellDefenders(states, raidRef, world.UtcNow, tuning)
                : new List<(int si, int fi)>();
            string defenderTag = dwelling ? DefenderTagOf(states, force, targetNode) : (raid.TargetTag ?? DefenderTagOf(states, force, targetNode));
            string defenderFleet = force.Count > 0 ? states[force[0].si].PendingFleets[force[0].fi].LogId : null;
            List<WeaponBand> defenderBands = dwelling ? DefenceBands(states, force, targetNode) : null;
            long defenderFlat = dwelling ? CombatStrength.StationFlat(targetNode, tuning) : 0;

            bool isSquadron = raid.Squadron != null && raid.Squadron.Count > 0;
            int standing = isSquadron ? raid.HullsToReturn.Count : Math.Max(1, raid.ConvoySize);
            int brought = raid.Fight != null && raid.Fight.AttackerHulls > 0
                ? raid.Fight.AttackerHulls
                : (isSquadron ? raid.Squadron.Count : standing);
            var lostHere = new List<string>();
            if (!isSquadron)
                for (int i = 0; i < standing; i++) lostHere.Add(raid.ShipPrefabId ?? "");

            // The ratio the loss forced is no longer this attack's to land at.
            raid.ForcedRatio = 0.0;
            SettleBrokenAttack(ref raid, raidRef, defenderTag, node, (long)(TotalDps(defenderBands) + defenderFlat),
                               standing, lostHere, "lead_lost:", isSquadron, 1.0, states, world, log);
            int leaving = states[raidRef.si].PendingFleets[raidRef.fi].HullsLeaving;
            string how = leaving > 0 ? "broke_off" : "beaten";
            MarkStopped(states, raidRef, how, 0.0, SourcePhysical);
            int lost = Math.Max(0, brought - (isSquadron ? standing : 0));
            RecordAttackStopped(raidRef, raid, defenderTag, node, defenderBands, defenderFlat, states, world, tuning,
                                lost, brought);
            log.Add("RAID_BROKE_OFF " + raid.LogId + " " + raid.ActorTag + " vs "
                    + (defenderFleet != null ? defenderFleet + " " + (defenderTag ?? "?") : "station")
                    + " @" + (node ?? "?") + " ratio=0.00 hulls_lost=" + lost.ToString(CultureInfo.InvariantCulture)
                    + "/" + brought.ToString(CultureInfo.InvariantCulture)
                    + " src=" + SourcePhysical + " (lead_lost=" + cause + " stop=" + raid.CurrentStopKind + ")");

            CombatAttackResult attack = leaving > 0 ? CombatAttackResult.BrokeOff : CombatAttackResult.Destroyed;
            int defBrought = 0;
            for (int i = 0; i < force.Count; i++) defBrought += HullsStanding(states[force[i].si].PendingFleets[force[i].fi]);
            RecordCombatOutcome(states, world, log, raidRef, force.Count > 0 ? force : null, targetNode, node,
                force.Count > 0 ? CombatMeeting.Guard : CombatMeeting.Station, attack, isSquadron ? 0 : lostHere.Count,
                false, false, SourcePhysical, brought, defBrought, defenderTag, defenderFleet);
        }
    }
}
