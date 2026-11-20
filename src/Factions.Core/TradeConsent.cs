using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Whether a seller's node will part with an item to a trade partner. One rule for the
    /// matcher that forms a trade vector (<c>TradeManager.FindConsentingSource</c>) and the dock that
    /// settles it, so nothing flies out to be refused at the pickup for a reason known at dispatch.
    ///
    /// <para>Where one of the node's installed capabilities uses the item, the node has a reference
    /// stock for it, and it sells only when genuinely oversupplied: scarcity at or below
    /// <see cref="FactionTuning.ProcurementSellerScarcityCeiling"/>. Where nothing installed there uses
    /// it, the oracle has no opinion (a flat 1.0), which read as "needs it" and kept stock like
    /// 513 M ice on a refinery node from ever being sold. That stock now sells
    /// (<see cref="FactionTuning.TradeSellUnusedStock"/>) unless the node itself has an open
    /// <c>Item:</c> row for it, or its faction is short of it anywhere. A mining node's ore is meant
    /// for its own refinery, and the owner's haul moves it there. With no oracle at all there is no
    /// evidence of anything, and nothing sells.</para>
    ///
    /// <para><b>A fuel key</b> (<see cref="FuelMarket.IsFuelKey"/>) is not asked the scarcity
    /// question. Where the node has a reference stock it consents while it holds anything above its keep (<see cref="FuelMarket.SellableAboveKeep"/>) and reads scarce
    /// otherwise; its margin is what it holds above that keep. A burning node's hydrogen reference
    /// includes its own fuel need, so the scarcity test refused at every node that burns. Where no
    /// reference exists the gates are the unused-stock ones above, unchanged, and the margin is still
    /// net of the keep.</para></summary>
    public static class TradeConsent
    {
        /// <summary>Why a node did or did not consent, for the <c>factions debug trade</c> trace.</summary>
        public enum Reason { Consents, NoOracle, Scarce, OwnRow, FactionShort, SwitchOff }

        /// <param name="shortMemo">Optional per-pass memo of <see cref="FactionShortOf"/>, keyed
        /// "tag|item": that answer walks the faction's demand rows, and a matcher asks it per node.</param>
        /// <param name="heldForBuyer">At the dock: the arriving convoy's own procurement hold
        /// (<see cref="FuelMarket.OwnHold"/>), which a fuel key's sellable excess counts back in.</param>
        public static bool SellerConsents(IPriceOracle oracle, WorldModel world, string sellerTag,
                                          SpatialNode node, string itemKey, FactionTuning tuning,
                                          IDictionary<string, bool> shortMemo = null, long heldForBuyer = 0)
        {
            long reference;
            return Evaluate(oracle, world, sellerTag, node, itemKey, tuning, shortMemo, out reference, heldForBuyer)
                == Reason.Consents;
        }

        /// <summary><see cref="SellerConsents"/> at the dock, naming the gate that decided it.</summary>
        internal static Reason EvaluateAtDock(IPriceOracle oracle, WorldModel world, string sellerTag,
                                              SpatialNode node, string itemKey, FactionTuning tuning,
                                              long heldForBuyer)
        {
            long reference;
            return Evaluate(oracle, world, sellerTag, node, itemKey, tuning, null, out reference, heldForBuyer);
        }

        /// <summary><see cref="SellerConsents"/>, naming the gate that decided it.</summary>
        public static Reason Evaluate(IPriceOracle oracle, WorldModel world, string sellerTag,
                                      SpatialNode node, string itemKey, FactionTuning tuning,
                                      IDictionary<string, bool> shortMemo = null)
        {
            long reference;
            return Evaluate(oracle, world, sellerTag, node, itemKey, tuning, shortMemo, out reference);
        }

        /// <summary>How much of <paramref name="surplus"/> the node can part with and still consent
        /// afterwards: the stock above the level where it would stop consenting, net of its own build
        /// queue's reserve. Zero whenever <see cref="Evaluate"/> refuses, with the gate in
        /// <paramref name="reason"/>.
        ///
        /// <para><b>Why a margin and not the consent bit.</b> Consent is a yes/no about the stock as it
        /// stands. A matcher that sized every vector to the whole surplus promised the same stock to
        /// every row: the first convoy to dock drained the seller down to where it stops consenting,
        /// and the dock refused every later one. On 2026-09-29 every H2 procurement from SPRT came back
        /// empty, 194 of 194.</para>
        ///
        /// <para>Where the node has a reference stock, scarcity is <c>reference / stock</c> (the
        /// oracle's own definition), so it consents while <c>stock &gt;= reference / ceiling</c> —
        /// <see cref="ConsentFloor"/>. The build-queue reserve leaves the node too, so it comes off
        /// the same side: <c>margin = surplus - floor</c>, where <paramref name="surplus"/> is
        /// <c>OperationsManager.SurplusOf</c>. Where nothing installed uses the item, consent does not
        /// depend on this node's stock level at all, and the whole surplus is the margin.</para>
        ///
        /// <para>That surplus is already net of the fuel the node keeps for its own departures
        /// (<see cref="FuelReserve.Keep"/>). The consent floor is not a substitute: it is read off the
        /// price reference, and a node holding its reserve target posts no reserve row for that
        /// reference to include.</para>
        ///
        /// <para>For a fuel key there is no consent floor:
        /// the margin is <paramref name="surplus"/> less <see cref="FuelMarket.IceKeep"/>, which is
        /// <see cref="FuelMarket.SellableAboveKeep"/> when <paramref name="surplus"/> is the node's
        /// <c>SurplusOf</c>.</para></summary>
        public static long ConsentingMargin(IPriceOracle oracle, WorldModel world, string sellerTag,
                                            SpatialNode node, string itemKey, long surplus, FactionTuning tuning,
                                            IDictionary<string, bool> shortMemo, out Reason reason)
        {
            long reference;
            reason = Evaluate(oracle, world, sellerTag, node, itemKey, tuning, shortMemo, out reference);
            if (reason != Reason.Consents || surplus <= 0) return 0;
            if (FuelMarket.IsFuelKey(world, sellerTag, itemKey, tuning))
            {
                long above = surplus - FuelMarket.IceKeep(node, itemKey, world, tuning);
                return above > 0 ? above : 0;
            }
            if (reference <= 0) return surplus;
            long floor = ConsentFloor(reference, tuning.ProcurementSellerScarcityCeiling);
            return surplus > floor ? surplus - floor : 0;
        }

        /// <summary>The least stock at which <c>reference / stock &lt;= ceiling</c>: below it the node
        /// reads scarce and refuses. A non-positive ceiling can never be met.</summary>
        internal static long ConsentFloor(long reference, double ceiling)
        {
            if (reference <= 0) return 0;
            if (ceiling <= 0) return long.MaxValue;
            double floor = Math.Ceiling(reference / ceiling);
            return floor >= long.MaxValue ? long.MaxValue : (long)floor;
        }

        private static Reason Evaluate(IPriceOracle oracle, WorldModel world, string sellerTag,
                                       SpatialNode node, string itemKey, FactionTuning tuning,
                                       IDictionary<string, bool> shortMemo, out long reference,
                                       long heldForBuyer = 0)
        {
            reference = 0;
            if (oracle == null || tuning == null || node == null || string.IsNullOrEmpty(itemKey)) return Reason.NoOracle;
            if (oracle is NullPriceOracle) return Reason.NoOracle;

            if (oracle.TryGetReferenceStock(node.Id, itemKey, out reference) && reference > 0)
            {
                if (FuelMarket.IsFuelKey(world, sellerTag, itemKey, tuning))
                    return FuelMarket.SellableAboveKeep(node, itemKey, world, tuning, heldForBuyer) > 0
                        ? Reason.Consents : Reason.Scarce;
                return oracle.ScarcityMultiplier(node.Id, itemKey) <= tuning.ProcurementSellerScarcityCeiling
                    ? Reason.Consents : Reason.Scarce;
            }
            reference = 0;

            if (!tuning.TradeSellUnusedStock) return Reason.SwitchOff;
            DemandEntry own;
            if (world != null && world.Demand != null
                && world.Demand.TryGet(sellerTag, DemandRegister.ItemKind(itemKey), node.Id, null, out own)
                && own.Quantity > 0)
                return Reason.OwnRow;
            return FactionShortOf(world, sellerTag, itemKey, shortMemo) ? Reason.FactionShort : Reason.Consents;
        }

        /// <summary>Whether any of <paramref name="tag"/>'s <c>Item:</c> rows for the key wants more
        /// than its node holds.</summary>
        public static bool FactionShortOf(WorldModel world, string tag, string itemKey, IDictionary<string, bool> memo)
        {
            if (world == null || world.Demand == null) return false;
            string key = tag + "|" + itemKey;
            bool shortOf;
            if (memo != null && memo.TryGetValue(key, out shortOf)) return shortOf;
            shortOf = false;
            foreach (DemandEntry d in world.Demand.GetFor(tag, DemandRegister.ItemKind(itemKey)))
            {
                if (string.IsNullOrEmpty(d.NodeId) || d.Quantity <= 0) continue;
                if (d.Quantity > world.GetNodeInventory(d.NodeId, itemKey)) { shortOf = true; break; }
            }
            if (memo != null) memo[key] = shortOf;
            return shortOf;
        }
    }
}
