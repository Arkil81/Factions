using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What each seller node can still part with, per item, for the length of one faction's
    /// Trade pass. <c>TradeManager</c> sizes every vector against <see cref="Available"/> and books
    /// it here, so a later row — in the same pool, for another partner, or in the other direction —
    /// sees what is left rather than the whole stock again.
    ///
    /// <para><b>Three terms.</b> The node's <see cref="TradeConsent.ConsentingMargin"/>, read once
    /// per (node, item) on first ask and then frozen for the pass; minus what in-flight Trade convoys
    /// are still bound to collect there (a pending Pickup leg, any faction, either direction); minus
    /// what this pass has booked. Freezing the margin is what keeps a tier-1 dispatch honest: that
    /// convoy loads at dispatch and the node's live stock falls, while its booking is still counted
    /// here — reading the live stock again would count the same units twice.</para>
    ///
    /// <para><b>Why the scope is one faction's pass, not the sweep.</b> A faction's pass is atomic
    /// (see <c>TradeManager.Cycle.cs</c>), but the sweep chunks across frames, and between two
    /// passes convoys can dock, load and resolve. A sweep-long ledger would keep counting a claim the
    /// dock has since drained. Instead every pass rebuilds the in-flight index from the roster's
    /// <see cref="PendingFleetAction"/>s — the same source the row-claim index is rebuilt from per
    /// pass — and every convoy an earlier pass dispatched, buy or sell, is in it. That is how the
    /// second faction's pass, and the sell walk after the buy walk, see the first's claims. A booking
    /// that never flew has claimed nothing and is not carried over.</para>
    ///
    /// <para>The dock (<c>FleetResolution.BuyTradeCargo</c>) is unchanged and remains the final
    /// word: this only stops the matcher promising the same stock twice.</para>
    ///
    /// <para><b>A held pickup is counted once.</b> A fuel-key procurement's pickup is held at the
    /// seller (<see cref="FuelMarket.RefreshHolds"/>) and already comes off the node's surplus, so the in-flight term leaves it out
    /// (<see cref="FuelMarket.HoldsPickup"/>). An in-flight Haul's claim on a fuel key comes off that
    /// surplus as well (<see cref="WorldModel.HaulPickupClaimed"/>); the in-flight term here counts
    /// Trade convoys only, so a haul's claim is never counted twice either.</para></summary>
    internal sealed class TradeSellerLedger
    {
        private struct Reading
        {
            public long Margin;
            public bool Stocked;
            public TradeConsent.Reason Reason;
        }

        private readonly IList<FactionState> _roster;
        private readonly WorldModel _world;
        private readonly FactionTuning _tuning;
        private Dictionary<(string NodeId, string ItemKey), long> _inFlight;   // lazy: a pass with no rows never pays for it
        private readonly Dictionary<(string NodeId, string ItemKey), Reading> _readings =
            new Dictionary<(string NodeId, string ItemKey), Reading>();
        private readonly Dictionary<(string NodeId, string ItemKey), long> _booked =
            new Dictionary<(string NodeId, string ItemKey), long>();

        internal TradeSellerLedger(IList<FactionState> roster)
            : this(roster, null, null)
        {
        }

        /// <param name="world">Read only for <see cref="FuelMarket.HoldsPickup"/>; null counts every
        /// in-flight pickup.</param>
        internal TradeSellerLedger(IList<FactionState> roster, WorldModel world, FactionTuning tuning)
        {
            _roster = roster;
            _world = world;
            _tuning = tuning;
        }

        /// <summary>What <paramref name="node"/> can still sell of <paramref name="itemKey"/> this
        /// pass. <paramref name="stocked"/> is false when it has no routable surplus at all (not a
        /// refusal, so the consent trace does not count it); otherwise <paramref name="reason"/> is the
        /// consent gate's answer, and a consenting node can still come back 0 once its margin is
        /// claimed.</summary>
        internal long Available(SpatialNode node, string ownerTag, string itemKey, IPriceOracle oracle,
            WorldModel world, FactionTuning tuning, IDictionary<string, bool> shortMemo,
            out bool stocked, out TradeConsent.Reason reason)
        {
            var key = (node.Id, itemKey);
            Reading r;
            if (!_readings.TryGetValue(key, out r))
            {
                long surplus = OperationsManager.SurplusOf(node, itemKey, world, tuning);
                r.Stocked = surplus > 0;
                r.Reason = TradeConsent.Reason.Consents;
                r.Margin = r.Stocked
                    ? TradeConsent.ConsentingMargin(oracle, world, ownerTag, node, itemKey, surplus, tuning, shortMemo, out r.Reason)
                    : 0;
                _readings[key] = r;
            }
            stocked = r.Stocked;
            reason = r.Reason;
            if (r.Margin <= 0) return 0;

            long left = r.Margin - InFlight(node.Id, itemKey) - Booked(node.Id, itemKey);
            return left > 0 ? left : 0;
        }

        internal void Book(string nodeId, string itemKey, long quantity)
        {
            if (quantity <= 0) return;
            var key = (nodeId, itemKey);
            long prior;
            _booked.TryGetValue(key, out prior);
            _booked[key] = prior + quantity;
        }

        /// <summary>Returns a booking whose vector never flew.</summary>
        internal void Release(string nodeId, string itemKey, long quantity)
        {
            if (quantity <= 0) return;
            var key = (nodeId, itemKey);
            long prior;
            if (!_booked.TryGetValue(key, out prior)) return;
            long left = prior - quantity;
            if (left > 0) _booked[key] = left;
            else _booked.Remove(key);
        }

        internal long Booked(string nodeId, string itemKey)
        {
            long v;
            return _booked.TryGetValue((nodeId, itemKey), out v) ? v : 0;
        }

        internal long InFlight(string nodeId, string itemKey)
        {
            if (_inFlight == null)
            {
                _inFlight = new Dictionary<(string NodeId, string ItemKey), long>();
                IndexPendingTradePickups(_roster, _inFlight, _world, _tuning);
            }
            long v;
            return _inFlight.TryGetValue((nodeId, itemKey), out v) ? v : 0;
        }

        /// <summary>Units every unresolved Trade convoy on the roster is still bound to collect, per
        /// (pickup node, item): the current leg when it is a Pickup, and every Pickup stop still
        /// queued. Cargo already aboard has left the node's stock and is not a claim on it. A pickup
        /// <see cref="FuelMarket.HoldsPickup"/> admits is left out: its hold is already off the node's
        /// surplus. One walk of the roster's fleets per pass, never per row.</summary>
        internal static void IndexPendingTradePickups(IList<FactionState> roster,
            Dictionary<(string NodeId, string ItemKey), long> into, WorldModel world = null, FactionTuning tuning = null)
        {
            if (roster == null) return;
            foreach (FactionState st in roster)
            {
                if (st == null) continue;
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    if (f.Kind != ActionKind.Trade || f.Resolved || f.Intercepted) continue;
                    if (f.CurrentStopKind == FleetStopKind.Pickup
                        && !FuelMarket.HoldsPickup(f, f.TargetNodeId, f.PickupItemKey, world, tuning))
                        Add(into, f.TargetNodeId, f.PickupItemKey, f.PickupQuantity);
                    if (f.RemainingStops == null) continue;
                    foreach (FleetStop stop in f.RemainingStops)
                        if (stop.Kind == FleetStopKind.Pickup
                            && !FuelMarket.HoldsPickup(f, stop.NodeId, stop.PickupItemKey, world, tuning))
                            Add(into, stop.NodeId, stop.PickupItemKey, stop.PickupQuantity);
                }
            }
        }

        private static void Add(Dictionary<(string NodeId, string ItemKey), long> into, string nodeId,
            string itemKey, long quantity)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(itemKey) || quantity <= 0) return;
            var key = (nodeId, itemKey);
            long prior;
            into.TryGetValue(key, out prior);
            into[key] = quantity > long.MaxValue - prior ? long.MaxValue : prior + quantity;
        }
    }
}
