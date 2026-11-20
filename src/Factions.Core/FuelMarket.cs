using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>How fuel trades across the ownership boundary. A faction whose depots have no ice
    /// buys hydrogen or ice from a partner instead of starving; see docs/fuel-plan.md and
    /// docs/trade-haul-shaped.md.
    ///
    /// <para><b>A fuel key</b> (<see cref="IsFuelKey"/>) is ice (<see cref="FactionTuning.IceOreSubTypeId"/>)
    /// and every key the seller's own reserve pass keeps back (<see cref="FuelReserve.Keeps"/>). For
    /// any other key nothing here applies.</para>
    ///
    /// <para><b>A seller node sells what it holds above its keep</b> (<see cref="SellableAboveKeep"/>).
    /// A burning node's hydrogen reference includes its own fuel need, so the scarcity test read every
    /// node that burns as scarce and refused at stock under twice that reference: live 2026-09-29, IGSL
    /// <c>stocked_nodes=155 scarce=153</c> while it held about 1 GL. The keep is the rule the routable
    /// surplus already applies (<see cref="FuelReserve.KeepAt"/>), plus for ice what the node's own
    /// FuelDepot draws before a refill can land (<see cref="IceKeep"/>).</para>
    ///
    /// <para><b>A fuel-key procurement holds its quantity at the seller until pickup</b>
    /// (<see cref="RefreshHolds"/>). The margin was checked at dispatch but not held: other hauls took
    /// the stock while the buyer flew out, and 19 of 35 MEMN hydrogen procurements (TFCS: 9 of 12) came
    /// back <c>PROCURE_BOUGHT … items=0</c>. The hold comes off the seller's routable surplus
    /// (<c>OperationsManager.SurplusOf</c>), so its own hauls, its trade consent and every other buyer's
    /// margin leave it alone, and the dock adds back the arriving convoy's own share.</para>
    ///
    /// <para><b>An in-flight Haul's pickup claims its quantity too, ranked below a hold</b>
    /// (<see cref="WorldModel.HaulPickupClaimed"/>). A haul dispatched to collect at a node flies out
    /// before it loads, and the stock it will take still sat in that node's surplus: live 2026-09-29,
    /// 127 of 171 empty hydrogen procurements met a haul of the seller's own already bound for the
    /// node when they were dispatched. Stock of a fuel key at a node is taken in this order, so no unit
    /// is counted twice or promised to both:</para>
    /// <list type="number">
    /// <item>the build queue's claim and the fuel keep;</item>
    /// <item>the procurement holds (<see cref="WorldModel.ProcurementHeld"/>);</item>
    /// <item>the in-flight Haul claims (<see cref="WorldModel.HaulPickupClaimed"/>);</item>
    /// <item>the rest, which is the routable surplus every new dispatch, consent and procurement
    /// margin reads (<see cref="AfterHaulClaims"/>).</item>
    /// </list>
    /// <para>So a procurement dispatched after a haul is offered only what the haul leaves; a haul
    /// arriving at a node where a procurement holds the key loads only what stands above the hold
    /// (<see cref="HaulLoadable"/>); and a docking procurement's own hold is not cut by a haul claim.
    /// Holds count Trade procurements only, claims Haul convoys only, and the trade ledger's in-flight
    /// term the Trade pickups without a hold, so the three never overlap.</para></summary>
    public static class FuelMarket
    {
        /// <summary>The inventory key of ice.</summary>
        public static string IceKey(FactionTuning tuning)
        {
            return InventoryKeyMapper.OrePrefix + (tuning != null ? tuning.IceOreSubTypeId : "");
        }

        public static bool IsIceKey(string key, FactionTuning tuning)
        {
            return tuning != null && !string.IsNullOrEmpty(key)
                && string.Equals(key, IceKey(tuning), StringComparison.Ordinal);
        }

        /// <summary>Whether <paramref name="key"/> is a fuel key for a node of
        /// <paramref name="sellerTag"/>: ice, or a key that faction's reserve pass keeps back.</summary>
        public static bool IsFuelKey(WorldModel world, string sellerTag, string key, FactionTuning tuning)
        {
            return IsIceKey(key, tuning) || FuelReserve.Keeps(world, sellerTag, key, tuning);
        }

        /// <summary>Units of ice a node keeps back from a sale for its own FuelDepot: the depot's
        /// measured ice draw (<see cref="ExtractionPlanner.DrawPerHour"/>, the planner's <c>draw</c>
        /// consumption) over <see cref="FactionTuning.FuelBufferLeadHours"/>. 0 for any other key and
        /// at a node without a FuelDepot, where nothing converts ice.
        ///
        /// <para><b>Why that lead.</b> It is the lead the hydrogen keep bridges: the stock a node needs
        /// to keep working until a refill can land. Ice sold out of a depot below it stops the depot
        /// converting, and the hydrogen it would have made is exactly the buffer the node's own
        /// departures then fly short of. The draw is the depot's measured rate, not its nameplate: a
        /// seller with ice to sell is not starved, so its draw is what it really converts.</para></summary>
        public static long IceKeep(SpatialNode node, string key, WorldModel world, FactionTuning tuning)
        {
            if (node == null || tuning == null || !IsIceKey(key, tuning)) return 0;
            int level;
            if (node.Capabilities == null
                || !node.Capabilities.TryGetValue(NodeCapabilityKind.FuelDepot, out level) || level <= 0)
                return 0;
            if (tuning.FuelBufferLeadHours <= 0) return 0;
            double perHour = ExtractionPlanner.DrawPerHour(node, key, world != null ? world.ItemVolumes : null, tuning);
            double keep = perHour * tuning.FuelBufferLeadHours;
            return keep > 0 ? (long)Math.Ceiling(keep) : 0;
        }

        /// <summary>Units of a fuel key <paramref name="node"/> can sell: its routable surplus
        /// (<see cref="OperationsManager.SurplusOf(SpatialNode, string, WorldModel, FactionTuning)"/>:
        /// stock less the build queue's claim, the fuel keep, the procurement holds and the in-flight
        /// Haul claims) less
        /// <see cref="IceKeep"/>. <paramref name="heldForBuyer"/> is the arriving convoy's own hold,
        /// which is its to collect and so does not come off. Never negative.</summary>
        public static long SellableAboveKeep(SpatialNode node, string key, WorldModel world, FactionTuning tuning,
            long heldForBuyer = 0)
        {
            if (node == null || world == null) return 0;
            long above = OperationsManager.SurplusOf(node, key, world, tuning, heldForBuyer)
                       - IceKeep(node, key, world, tuning);
            return above > 0 ? above : 0;
        }

        /// <summary>Which layer of a fuel key's stock at <paramref name="node"/> left nothing for the
        /// convoy holding <paramref name="ownHold"/> there to buy: <c>stock</c> when nothing stands above
        /// the build queue's claim and the keeps, <c>held</c> when other procurements hold the rest,
        /// <c>claimed</c> when in-flight Haul claims take what is left. For the empty-buy reason on
        /// <c>PROCURE_BOUGHT</c>.</summary>
        internal static string NothingSellable(SpatialNode node, string key, WorldModel world, FactionTuning tuning,
            long ownHold)
        {
            DateTime nowUtc = world.UtcNow;
            long stock;
            node.Inventory.TryGetValue(key, out stock);
            long above = stock - node.BuildQueueReserved(key, nowUtc) - OperationsManager.KeepOf(node, key, nowUtc, world, tuning)
                       - IceKeep(node, key, world, tuning);
            if (above <= 0) return "stock";
            long others = world.ProcurementHeld(node.Id, key) - ownHold;
            if (others > 0 && above - others <= 0) return "held";
            return "claimed";
        }

        // ---- the procurement hold ----------------------------------------------------------------

        /// <summary>Whether <paramref name="fleet"/>'s pickup of <paramref name="itemKey"/> at
        /// <paramref name="nodeId"/> is held there: the fleet is a live procurement
        /// (a Trade convoy the buyer flies), and the key is a fuel key for the node's owner
        /// (<see cref="SellerAt"/>). One predicate for the hold index and for the trade ledger, which
        /// counts every other in-flight pickup itself and must not count a held one twice.
        ///
        /// <para><b>Keyed on the pickup's node, not the leg's target.</b> A leg routed around a planet
        /// (<see cref="PendingFleetResolver.PlanFleetLeg"/>) flies clearance waypoints with
        /// <see cref="PendingFleetAction.TargetTag"/> null and the Pickup requeued behind them. The
        /// seller is read off the Pickup stop's node, so the hold stands for the whole detour.</para></summary>
        public static bool HoldsPickup(PendingFleetAction fleet, string nodeId, string itemKey, WorldModel world,
            FactionTuning tuning)
        {
            return world != null
                && fleet.Kind == ActionKind.Trade && fleet.IsProcurement
                && !fleet.Resolved && !fleet.Intercepted
                && IsFuelKey(world, SellerAt(nodeId, fleet, world), itemKey, tuning);
        }

        /// <summary>The faction whose stock a pickup at <paramref name="nodeId"/> draws on: the node's
        /// owner, or <paramref name="fleet"/>'s <see cref="PendingFleetAction.TargetTag"/> when the node
        /// is not in the graph.</summary>
        public static string SellerAt(string nodeId, PendingFleetAction fleet, WorldModel world)
        {
            SpatialNode node;
            if (nodeId != null && world != null && world.Graph.Nodes.TryGetValue(nodeId, out node))
                return node.OwnerFactionTag;
            return fleet.TargetTag;
        }

        /// <summary>Whether <paramref name="fleet"/>'s pickup of <paramref name="itemKey"/> at
        /// <paramref name="nodeId"/> claims that stock: the fleet is a live Haul, and
        /// the key is a fuel key for the node's owner (<see cref="SellerAt"/>).</summary>
        public static bool ClaimsPickup(PendingFleetAction fleet, string nodeId, string itemKey, WorldModel world,
            FactionTuning tuning)
        {
            return world != null
                && fleet.Kind == ActionKind.Haul
                && !fleet.Resolved && !fleet.Intercepted
                && IsFuelKey(world, SellerAt(nodeId, fleet, world), itemKey, tuning);
        }

        /// <summary>Rebuilds <see cref="WorldModel.ProcurementHeld"/> and
        /// <see cref="WorldModel.HaulPickupClaimed"/> from the roster: for every procurement
        /// <see cref="HoldsPickup"/> admits, and every Haul <see cref="ClaimsPickup"/> admits, the
        /// quantity of its current Pickup leg and of every Pickup stop still queued, at that stop's
        /// node.
        ///
        /// <para><b>Derived, not persisted.</b> Both are a function of the in-flight fleets,
        /// which are persisted with their pickup item and quantity, so a restart rebuilds them at the
        /// first call. Storing them as well would be a second record of the same fact that every exit
        /// path would have to keep in step.</para>
        ///
        /// <para><b>Every exit releases by construction.</b> A hold or claim exists only while its
        /// fleet is unresolved, not intercepted, still in the roster and still bound for a Pickup at
        /// that node: pickup (the leg advances past the Pickup), cancel (resolved or removed), loss
        /// (intercepted or removed), reroute (the Pickup leaves the itinerary) and expiry (resolved and
        /// removed by the arrivals cleanup) each end that. Called at the start and end of every arrivals
        /// pass, at the start and end of every Trade faction pass, and at the start of every Operations
        /// pass, so an entry is at most one frame stale, and stale only on the conservative side.</para>
        ///
        /// <para>Cost: one walk of the roster's fleets and their queued stops.</para></summary>
        public static void RefreshHolds(IList<FactionState> roster, WorldModel world, FactionTuning tuning)
        {
            if (world == null) return;
            world.ClearProcurementHolds();
            world.ClearHaulPickupClaims();
            if (roster == null) return;
            for (int si = 0; si < roster.Count; si++)
            {
                FactionState st = roster[si];
                if (st == null) continue;
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.Resolved || f.Intercepted) continue;
                    bool procurement = f.Kind == ActionKind.Trade && f.IsProcurement;
                    if (!procurement && f.Kind != ActionKind.Haul) continue;
                    if (f.CurrentStopKind == FleetStopKind.Pickup)
                        Index(f, procurement, f.TargetNodeId, f.PickupItemKey, f.PickupQuantity, world, tuning);
                    if (f.RemainingStops == null) continue;
                    for (int k = 0; k < f.RemainingStops.Count; k++)
                    {
                        FleetStop stop = f.RemainingStops[k];
                        if (stop.Kind == FleetStopKind.Pickup)
                            Index(f, procurement, stop.NodeId, stop.PickupItemKey, stop.PickupQuantity, world, tuning);
                    }
                }
            }
        }

        private static void Index(PendingFleetAction f, bool procurement, string nodeId, string itemKey, long quantity,
            WorldModel world, FactionTuning tuning)
        {
            if (procurement)
            {
                if (HoldsPickup(f, nodeId, itemKey, world, tuning))
                    world.AddProcurementHold(nodeId, itemKey, quantity);
            }
            else if (ClaimsPickup(f, nodeId, itemKey, world, tuning))
                world.AddHaulPickupClaim(nodeId, itemKey, quantity);
        }

        /// <summary>What is left of <paramref name="available"/> (a node's stock of a key net of the
        /// build queue's claim, the fuel keep and every other convoy's hold) once
        /// <paramref name="claimed"/> units of in-flight Haul claims come off it, as the convoy holding
        /// <paramref name="ownHold"/> there sees it. A hold ranks above a claim: the convoy's own share
        /// is its to collect whatever the hauls claim, and only the stock above both is free.
        /// <c>ownHold</c> 0 is every other reader, for whom it is <c>available − claimed</c>.</summary>
        internal static long AfterHaulClaims(long available, long ownHold, long claimed)
        {
            if (claimed <= 0 || available <= 0) return available;
            long own = ownHold > 0 ? Math.Min(ownHold, available) : 0;
            long free = available - own - claimed;
            return own + (free > 0 ? free : 0);
        }

        /// <summary>How much of <paramref name="requested"/> a convoy drawing on its own faction's stock
        /// (a Haul or a seller-flies Trade at its Pickup) may load at <paramref name="node"/>: all of it
        /// up to the stock where no procurement holds the key, otherwise only the stock standing above
        /// the hold, which itself stands above the build queue's claim and the fuel keep — the stock the
        /// docking procurement will be sold from. Holds exist only for fuel keys, so every other key
        /// loads up to the request.</summary>
        public static long HaulLoadable(SpatialNode node, string key, long requested, WorldModel world,
            FactionTuning tuning)
        {
            if (node == null || world == null || requested <= 0) return requested;
            long held = world.ProcurementHeld(node.Id, key);
            if (held <= 0) return requested;
            DateTime nowUtc = world.UtcNow;
            long stock;
            node.Inventory.TryGetValue(key, out stock);
            long above = stock - node.BuildQueueReserved(key, nowUtc)
                       - OperationsManager.KeepOf(node, key, nowUtc, world, tuning) - held;
            if (above <= 0) return 0;
            return above < requested ? above : requested;
        }

        /// <summary>The share of <paramref name="node"/>'s Haul claims that belongs to
        /// <paramref name="fleet"/>, standing at its Pickup there: its pickup quantity, capped at what
        /// the index claims. 0 when the fleet's pickup does not claim.</summary>
        public static long OwnClaim(PendingFleetAction fleet, SpatialNode node, WorldModel world, FactionTuning tuning)
        {
            if (node == null || !ClaimsPickup(fleet, node.Id, fleet.PickupItemKey, world, tuning)) return 0;
            long claimed = world.HaulPickupClaimed(node.Id, fleet.PickupItemKey);
            return fleet.PickupQuantity < claimed ? fleet.PickupQuantity : claimed;
        }

        /// <summary>The share of <paramref name="node"/>'s hold that belongs to
        /// <paramref name="fleet"/>, standing at its Pickup there: its pickup quantity, capped at what
        /// the index holds (an earlier convoy in the same pass may already have released its own).
        /// 0 when the fleet's pickup is not held.</summary>
        public static long OwnHold(PendingFleetAction fleet, SpatialNode node, WorldModel world, FactionTuning tuning)
        {
            if (node == null || !HoldsPickup(fleet, node.Id, fleet.PickupItemKey, world, tuning)) return 0;
            long held = world.ProcurementHeld(node.Id, fleet.PickupItemKey);
            return fleet.PickupQuantity < held ? fleet.PickupQuantity : held;
        }
    }
}
