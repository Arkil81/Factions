using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Owns inter-faction trade for every faction. Runs at manager cadence (10× brain rate).
    ///
    /// <para>Trade is Haul across the ownership boundary, with payment and consent. A
    /// <see cref="MaterialVector"/> is formed from a demand row its owner's own Operations pass
    /// could not source inside its own estate (<see cref="FactionState.UnservedDemands"/>), matched
    /// to a consenting source node on the other side of the border, sized to the row's remaining
    /// quantity capped at what that node can still part with (<see cref="TradeSellerLedger"/>), and
    /// dispatched through the same tiers, convoy assessment, consolidation and
    /// in-flight claim that intra-faction Haul uses — <c>OperationsManager.DispatchVector</c>, not a
    /// copy of it — with <see cref="ActionKind.Trade"/>, so the existing Service-stop settlement
    /// (sell) or Pickup-stop purchase (buy) moves money for exactly what boards.</para>
    ///
    /// <para>Two directions, one rule applied from each side. <b>Sell</b>: the seller flies its own
    /// hull to a partner's unserved row and settles at a Service stop, then comes home under a
    /// terminal Return. <b>Buy</b>: the buyer flies an empty hull out to a partner's consenting
    /// stock, pays at the Pickup stop for exactly what boards, and carries it home. The buy
    /// direction is a whole roster pass ahead of the sell direction, so every faction that owns a
    /// hull has had its chance at a row before any seller is offered it; the sell direction is what
    /// serves the hull-less.</para>
    ///
    /// <para><see cref="FactionState.TradePriority"/> is the whitelist and nothing else: the actor's
    /// own relation, embargo and reach are already applied by the brain, and its <c>Score</c> only
    /// orders partners when several could serve one row. The partner's say is read here, per pair:
    /// <see cref="Diplomacy.TradeBarred"/> (either side hostile or embargoing) and a player partner's
    /// switch for the direction it would trade in. See docs/trade-haul-shaped.md and
    /// docs/diplomacy-plan.md.</para>
    /// </summary>
    public sealed partial class TradeManager : IFactionManager
    {
        private readonly IPriceOracle _oracle;
        private readonly SpendBudget _tradeBudget = new SpendBudget();
        // Recipient-side windowed budget, queried at Trade *resolution* time (not dispatch) by
        // PendingFleetResolver — caps how much a recipient can be charged by inbound trade per window,
        // aggregated across every sender trading with them, so a trade flood can't monopolize their
        // spending capacity. Kept here alongside _tradeBudget since this is where trade-related
        // spend-budget state already lives.
        private readonly SpendBudget _tradeInboundBudget = new SpendBudget();
        public SpendBudget InboundBudget { get { return _tradeInboundBudget; } }

        /// <summary>The shared vector-dispatch core, owned rather than injected: it is used purely as
        /// a dispatcher here (the three tiers, <c>AssessConvoy</c>, consolidation, the
        /// <c>Ship:Trade</c> fallback), and keeping Trade's calls on an instance nobody reads as
        /// Operations telemetry is what stops trade work landing in the numbers <c>OPS_TICK_PERF</c>
        /// reports. One instance for the manager's lifetime: its per-pass caches live on the
        /// <c>FactionTickContext</c> this manager creates per faction, not on it.</summary>
        private readonly OperationsManager _dispatch;

        private TradeTickTelemetry _lastTelemetry = new TradeTickTelemetry();

        // "tag|item" -> whether that faction is short of the item anywhere, for seller consent.
        // Cleared per faction pass: stock and rows move between passes.
        private readonly Dictionary<string, bool> _consentShortMemo = new Dictionary<string, bool>(StringComparer.Ordinal);
        public TradeTickTelemetry LastTelemetry { get { return _lastTelemetry; } }

        /// <summary>The prefab and price catalogues this manager once carried are gone: hull
        /// selection now happens inside the shared core, which reads
        /// <see cref="WorldModel.PrefabCatalogue"/>, and prices are settled at the stop by
        /// <see cref="PendingFleetResolver"/> from the catalogue the host hands it. Only the oracle
        /// is read here, and only to decide seller consent.</summary>
        public TradeManager(
            IPriceOracle oracle = null,
            IItemVolumeCatalogue volumes = null,
            IItemMassCatalogue masses = null)
        {
            _oracle  = oracle ?? NullPriceOracle.Instance;
            _dispatch = new OperationsManager(volumes, masses);
        }

        // ---- one faction's pass ------------------------------------------------------------------

        /// <summary>One faction's whole Trade pass in one direction: eligibility, the budget gate,
        /// the roster-wide claim index, vector formation and dispatch.</summary>
        private void TickOneFaction(FactionState st, bool buying, IList<FactionState> states,
            WorldModel world, FactionTuning tuning, List<string> log)
        {
            long factionStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _consentShortMemo.Clear();

            long eligStart = System.Diagnostics.Stopwatch.GetTimestamp();
            // A player faction that opted in gets the partner list its brain never computed. Done
            // before the eligibility read below, because that read is what consumes it, and on the
            // buy pass alone so one projection serves both directions of the same sweep.
            if (buying) FactionLifecycle.ProjectPlayerTradePriority(st, world, tuning);

            // Trade is not strategy. A player faction keeps the brain switched off -- who to raid,
            // what to found, where to expand all stay theirs -- but moving goods its own production
            // already asked for is logistics, and it may opt in to that per direction. The strategic
            // half of trade, whom to deal with at all, it still holds: relation and embargo are what
            // rank the partner list, and both are the player's.
            bool directionEnabled = buying ? FactionLifecycle.TradeBuyEnabled(st) : FactionLifecycle.TradeSellEnabled(st);
            bool blocked = FactionLifecycle.IsDormant(st, world.UtcNow) || !directionEnabled;
            bool noPriority = !blocked && st.TradePriority.Count == 0;
            int inFlight = blocked || noPriority ? 0 : InFlightTradeCount(st);
            bool atFleetCap = !blocked && !noPriority && inFlight >= tuning.MaxTradeFleets;
            _lastTelemetry.EligibilityFilterTicks += System.Diagnostics.Stopwatch.GetTimestamp() - eligStart;

            // Counted on the buy pass alone, so "factions" stays a roster count rather than twice one.
            if (buying) _lastTelemetry.Factions++;
            // An opted-in player faction that ranked nobody is the characteristic failure of this
            // whole feature and TRADE_TICK_PERF cannot show it: that line is emitted only when the
            // pass is slow or actually dispatched something, so "eligible but inert" and "not
            // deployed" look identical in the log. This line is the difference, and it is the one
            // an acceptance check reads.
            if (buying && st.IsPlayerFaction && FactionLifecycle.TradeEnabled(st) && log != null)
                log.Add("TRADE_OPTIN " + st.Tag
                        + " buy=" + (st.TradeBuyOptIn ? "on" : "off")
                        + " sell=" + (st.TradeSellOptIn ? "on" : "off")
                        + " partners=" + st.TradePriority.Count
                        + " unserved=" + st.UnservedDemands.Count
                        + " inFlight=" + InFlightTradeCount(st)
                        + (FactionLifecycle.IsDormant(st, world.UtcNow) ? " dormant" : ""));
            if (blocked || noPriority || atFleetCap) return;
            if (buying) _lastTelemetry.EligibleFactions++;

            if (!CanAffordTrade(st, world, tuning))
            {
                _lastTelemetry.BudgetBlocked++;
                return;
            }

            TradePerfBaseline before = TradePerfBaseline.Capture(_lastTelemetry);
            var ctx = new LiveOperationsExecutionContext(world, st, log);
            var context = new OperationsManager.FactionTickContext();

            // Roster-wide, not this faction's own: two factions can now serve one row, so the claim
            // that stops them has to see every faction's in-flight convoys and every player-contract
            // claim. Rebuilt per faction pass, which is also what makes a convoy dispatched earlier
            // in this same sweep visible here.
            var claims = new HashSet<(string NodeId, string ItemKey)>();
            OperationsManager.IndexInFlightHaulPairs(states, claims);
            // A partly served row always has its owner's own haul in flight -- that is what served
            // the part. It is blocked only by another trade convoy or a player contract on it.
            var tradeClaims = new HashSet<(string NodeId, string ItemKey)>();
            OperationsManager.IndexInFlightTradePairs(states, tradeClaims);
            // The seller-side counterpart of the row claims above: what each seller node can still
            // part with, net of every convoy already bound to load there and of this pass's own
            // vectors. Scoped to this pass for the same reason the claim sets are.
            // Rebuilt from the same roster the ledger's in-flight index is, so a held procurement
            // comes off the seller's surplus exactly once.
            FuelMarket.RefreshHolds(states, world, tuning);
            var ledger = new TradeSellerLedger(states, world, tuning);

            long dispatchStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (buying) RunBuyPass(st, states, ctx, context, claims, tradeClaims, ledger, ref inFlight, world, tuning, log);
            else RunSellPass(st, states, ctx, context, claims, tradeClaims, ledger, ref inFlight, world, tuning, log);
            _lastTelemetry.DispatchTicks += System.Diagnostics.Stopwatch.GetTimestamp() - dispatchStart;

            _dispatch.PublishFallbacks(tuning, context, ctx);
            // This pass's procurements hold their quantity from dispatch, not from the next rebuild.
            FuelMarket.RefreshHolds(states, world, tuning);

            MaybeLogTradeTickPerf(log, st.Tag, factionStartTicks, before);
        }

        /// <summary>Buyer-flies. Only for a faction that <em>owns</em> a cargo hull somewhere —
        /// ownership, not availability, so a one-hull faction is not read as fleetless for the whole
        /// duration of every errand that hull flies (FAC-199's rule). A faction that owns none is
        /// served by the sell direction instead; nothing teleports.</summary>
        private void RunBuyPass(FactionState st, IList<FactionState> states, IOperationsExecutionContext ctx,
            OperationsManager.FactionTickContext context, HashSet<(string NodeId, string ItemKey)> claims,
            HashSet<(string NodeId, string ItemKey)> tradeClaims, TradeSellerLedger ledger,
            ref int inFlight, WorldModel world, FactionTuning tuning, List<string> log)
        {
            var ownedNodes = new List<SpatialNode>();
            foreach (SpatialNode n in world.Graph.OwnedBy(st.Tag)) ownedNodes.Add(n);
            if (!OperationsManager.NetworkOwnsSuitable(ctx, st, ownedNodes, ActionKind.Trade, world.PrefabCatalogue))
                return;

            List<UnservedDemand> rows = DedupUnserved(st.UnservedDemands);
            if (rows.Count == 0) return;
            _lastTelemetry.UnservedSeen += rows.Count;

            // The partners this pass may buy from, decided once rather than per row: the bar and the
            // seller's own switch are properties of the pair, not of the row.
            List<string> sellers = OpenPartners(st, states, false, tuning, log);
            if (sellers.Count == 0) return;

            foreach (UnservedDemand row in rows)
            {
                if (inFlight >= tuning.MaxTradeFleets) break;
                if ((row.Partial ? tradeClaims : claims).Contains((row.NodeId, row.ItemKey))) { _lastTelemetry.Claimed++; continue; }
                SpatialNode needNode;
                if (!world.Graph.Nodes.TryGetValue(row.NodeId, out needNode)) continue;

                // Every open partner's best node competes; the first partner with any consenting
                // node no longer wins by rank alone. See SourceCandidate.IsBetterThan.
                SourceCandidate best = default(SourceCandidate);
                SourceCandidate nearestOverall = default(SourceCandidate);
                for (int rank = 0; rank < sellers.Count; rank++)
                {
                    _lastTelemetry.Partners++;
                    SourceCandidate c, cNearest;
                    if (!FindConsentingSource(sellers[rank], row.ItemKey, needNode.Position, row.Remaining,
                            ledger, world, tuning, out c, out cNearest, st.Tag, row.NodeId, st.Tag, log))
                        continue;
                    c.PartnerRank = rank;
                    cNearest.PartnerRank = rank;
                    if (best.NodeId == null || c.IsBetterThan(best)) best = c;
                    if (nearestOverall.NodeId == null || cNearest.IsBetterThan(nearestOverall)) nearestOverall = cNearest;
                }
                if (best.NodeId == null) { _lastTelemetry.RefusedConsent++; continue; }
                LogRepick(st.Tag, row.ItemKey, row.NodeId, best, nearestOverall, world, log);
                string sellerTag = best.OwnerTag;
                string sourceNodeId = best.NodeId;
                long quantity = best.Quantity;
                ledger.Book(sourceNodeId, row.ItemKey, quantity);

                var vector = new MaterialVector
                {
                    FactionTag    = st.Tag,
                    ItemKey       = row.ItemKey,
                    Quantity      = quantity,
                    SourceNodeId  = sourceNodeId,
                    NeedNodeId    = row.NodeId,
                    DeliverNodeId = row.NodeId,
                    Urgency       = row.Urgency,
                    Shortfall     = row.Remaining,
                };
                _lastTelemetry.Vectors++;

                var profile = new OperationsManager.VectorDispatchProfile
                {
                    Kind             = ActionKind.Trade,
                    SourceOwnerTag   = sellerTag,
                    DeliverOwnerTag  = st.Tag,
                    CounterpartyTag  = sellerTag,
                    IsProcurement    = true,
                    ReturnsToSource  = false,
                    InvestmentReason = "procure",
                };

                // One vector per pool: buyer-flies loads at a foreign node, and consolidation groups
                // on the DELIVERY node, so folding two sellers' rows onto one hull would have it
                // buying at a node the second seller's consent was never asked about.
                var single = new List<MaterialVector> { vector };
                var classByRow = new Dictionary<string, DemandClass>(StringComparer.Ordinal);
                classByRow[RowKey(row.NodeId, row.ItemKey)] = row.Class;
                var marginByRow = new Dictionary<string, long>(StringComparer.Ordinal);
                marginByRow[RowKey(row.NodeId, row.ItemKey)] = best.Available;
                HashSet<string> partialRows = row.Partial
                    ? new HashSet<string>(StringComparer.Ordinal) { RowKey(row.NodeId, row.ItemKey) }
                    : null;
                DispatchPool(st, single, profile, classByRow, marginByRow, ctx, context, claims, tradeClaims,
                    partialRows, ledger, ref inFlight, world, tuning, log);
            }
        }

        /// <summary>Seller-flies: for each whitelisted partner, the rows that partner's own
        /// Operations pass could not fill, served from this faction's consenting stock. Vectors are
        /// pooled per (actor, partner) pair, so consolidation collects several sources for one
        /// partner's node without ever mixing two partners' cargo aboard one hull.</summary>
        private void RunSellPass(FactionState st, IList<FactionState> states, IOperationsExecutionContext ctx,
            OperationsManager.FactionTickContext context, HashSet<(string NodeId, string ItemKey)> claims,
            HashSet<(string NodeId, string ItemKey)> tradeClaims, TradeSellerLedger ledger,
            ref int inFlight, WorldModel world, FactionTuning tuning, List<string> log)
        {
            foreach (TradePriorityEntry entry in st.TradePriority)
            {
                if (inFlight >= tuning.MaxTradeFleets) break;
                FactionState partner = ResolveFaction(states, entry.PartnerTag);
                if (partner == null) continue;
                if (!PartnerOpen(st, partner, true, tuning, log)) continue;
                _lastTelemetry.Partners++;

                List<UnservedDemand> rows = DedupUnserved(partner.UnservedDemands);
                if (rows.Count == 0) continue;
                _lastTelemetry.UnservedSeen += rows.Count;

                var vectors = new List<MaterialVector>();
                var classByRow = new Dictionary<string, DemandClass>(StringComparer.Ordinal);
                var marginByRow = new Dictionary<string, long>(StringComparer.Ordinal);
                var partialRows = new HashSet<string>(StringComparer.Ordinal);
                foreach (UnservedDemand row in rows)
                {
                    if ((row.Partial ? tradeClaims : claims).Contains((row.NodeId, row.ItemKey))) { _lastTelemetry.Claimed++; continue; }
                    SpatialNode needNode;
                    if (!world.Graph.Nodes.TryGetValue(row.NodeId, out needNode)) continue;

                    SourceCandidate source, sourceNearest;
                    if (!FindConsentingSource(st.Tag, row.ItemKey, needNode.Position, row.Remaining,
                            ledger, world, tuning, out source, out sourceNearest, st.Tag, row.NodeId,
                            entry.PartnerTag, log))
                    {
                        _lastTelemetry.RefusedConsent++;
                        continue;
                    }
                    LogRepick(st.Tag, row.ItemKey, row.NodeId, source, sourceNearest, world, log);
                    string sourceNodeId = source.NodeId;
                    long quantity = source.Quantity;
                    // Booked as the vector forms, not as it flies: every row of this pool is formed
                    // before any of it dispatches, so a later row has to see an earlier one's claim.
                    ledger.Book(sourceNodeId, row.ItemKey, quantity);

                    vectors.Add(new MaterialVector
                    {
                        FactionTag    = st.Tag,
                        ItemKey       = row.ItemKey,
                        Quantity      = quantity,
                        SourceNodeId  = sourceNodeId,
                        NeedNodeId    = row.NodeId,
                        DeliverNodeId = row.NodeId,
                        Urgency       = row.Urgency,
                        Shortfall     = row.Remaining,
                    });
                    classByRow[RowKey(row.NodeId, row.ItemKey)] = row.Class;
                    marginByRow[RowKey(row.NodeId, row.ItemKey)] = source.Available;
                    if (row.Partial) partialRows.Add(RowKey(row.NodeId, row.ItemKey));
                }
                if (vectors.Count == 0) continue;
                _lastTelemetry.Vectors += vectors.Count;
                OperationsManager.SortByUrgency(vectors);

                var profile = new OperationsManager.VectorDispatchProfile
                {
                    Kind             = ActionKind.Trade,
                    SourceOwnerTag   = st.Tag,
                    DeliverOwnerTag  = entry.PartnerTag,
                    CounterpartyTag  = entry.PartnerTag,
                    IsProcurement    = false,
                    ReturnsToSource  = true,
                    InvestmentReason = "trade",
                };
                DispatchPool(st, vectors, profile, classByRow, marginByRow, ctx, context, claims, tradeClaims,
                    partialRows, ledger, ref inFlight, world, tuning, log);
            }
        }

        // ---- the other side's say ----------------------------------------------------------------

        /// <summary><paramref name="st"/>'s whitelisted partners, in rank order, that this pass may
        /// deal with. <see cref="FactionState.TradePriority"/> is only <paramref name="st"/>'s own view;
        /// this is where the partner's is read. A partner with no state in the roster is kept, as it
        /// always was: <see cref="Diplomacy.TradeBarred"/> treats a missing state as not barred.</summary>
        private List<string> OpenPartners(FactionState st, IList<FactionState> states, bool selling,
            FactionTuning tuning, List<string> log)
        {
            var open = new List<string>(st.TradePriority.Count);
            foreach (TradePriorityEntry entry in st.TradePriority)
                if (PartnerOpen(st, ResolveFaction(states, entry.PartnerTag), selling, tuning, log))
                    open.Add(entry.PartnerTag);
            return open;
        }

        /// <summary>Whether <paramref name="st"/> may trade with <paramref name="partner"/> in this
        /// direction. Two keys the partner holds: the symmetric bar (either side hostile, or either
        /// side's embargo), and -- for a player faction -- the partner's own switch for the direction
        /// it would be trading in. When <paramref name="st"/> buys, the partner sells, so its
        /// <see cref="FactionState.TradeSellOptIn"/> is what is asked; when <paramref name="st"/>
        /// sells, the partner buys. An NPC's switches always read on.</summary>
        private bool PartnerOpen(FactionState st, FactionState partner, bool selling, FactionTuning tuning,
            List<string> log)
        {
            if (partner == null) return true;
            string reason = null;
            if (Diplomacy.TradeBarred(st, partner, tuning)) { _lastTelemetry.Barred++; reason = BarReason(st, partner); }
            else if (!(selling ? FactionLifecycle.TradeBuyEnabled(partner) : FactionLifecycle.TradeSellEnabled(partner)))
            {
                _lastTelemetry.OptedOut++;
                reason = "opted_out";
            }
            if (DebugTraces.Trade && log != null) LogPartnerClosed(log, st.Tag, partner.Tag, selling, reason);
            return reason == null;
        }

        /// <summary>Which half of <see cref="Diplomacy.TradeBarred"/> held, for the trace only. The
        /// embargo is named first: it is the deliberate act, where hostility may be drift.</summary>
        private static string BarReason(FactionState a, FactionState b)
        {
            if (a.Embargoes.Contains(b.Tag ?? "")) return "embargo:" + a.Tag;
            if (b.Embargoes.Contains(a.Tag ?? "")) return "embargo:" + b.Tag;
            return "hostile";
        }

        // "actor|partner|dir" -> the reason last traced, so the trace logs on change and again once
        // the pair reopens and closes.
        private readonly Dictionary<string, string> _lastPartnerClosed = new Dictionary<string, string>(StringComparer.Ordinal);

        private void LogPartnerClosed(List<string> log, string actorTag, string partnerTag, bool selling, string reason)
        {
            string key = actorTag + "|" + partnerTag + "|" + (selling ? "sell" : "buy");
            string prior;
            bool had = _lastPartnerClosed.TryGetValue(key, out prior);
            if (reason == null) { if (had) _lastPartnerClosed.Remove(key); return; }
            if (had && prior == reason) return;
            _lastPartnerClosed[key] = reason;
            log.Add("TRADE_PARTNER_CLOSED actor=" + actorTag + " partner=" + partnerTag
                    + " dir=" + (selling ? "sell" : "buy") + " reason=" + reason);
        }

        // ---- dispatch ----------------------------------------------------------------------------

        /// <summary>Runs one pool of vectors through the shared core in urgency order. The pool is
        /// what makes consolidation safe: a vector is consumed the moment this loop takes it, so a
        /// later vector looking backwards for company can never fold one that already has a convoy.
        /// Identical discipline to <c>OperationsManager</c>'s own dispatch loop.
        /// <para>Every vector arrives booked in <paramref name="ledger"/>. One that does not fly —
        /// skipped on a claim, refused for want of a hull or route, or never reached — hands its
        /// booking back, so a later row in the same pass can use that stock. A vector folded into
        /// another's convoy flies with it and keeps its booking.</para></summary>
        private void DispatchPool(FactionState st, List<MaterialVector> vectors,
            OperationsManager.VectorDispatchProfile profile, Dictionary<string, DemandClass> classByRow,
            Dictionary<string, long> marginByRow,
            IOperationsExecutionContext ctx, OperationsManager.FactionTickContext context,
            HashSet<(string NodeId, string ItemKey)> claims, HashSet<(string NodeId, string ItemKey)> tradeClaims,
            HashSet<string> partialRows, TradeSellerLedger ledger, ref int inFlight,
            WorldModel world, FactionTuning tuning, List<string> log)
        {
            var pool = new OperationsManager.HaulVectorPool(vectors, tuning);
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool.IsConsumed(i)) continue;
                if (inFlight >= tuning.MaxTradeFleets) break;

                MaterialVector v = pool[i];
                var claim = (v.DeliverNodeId, v.ItemKey);
                bool partial = partialRows != null && partialRows.Contains(RowKey(v.DeliverNodeId, v.ItemKey));
                if ((partial ? tradeClaims : claims).Contains(claim))
                {
                    _lastTelemetry.Claimed++;
                    pool.Consume(i);
                    ledger.Release(v.SourceNodeId, v.ItemKey, v.Quantity);
                    continue;
                }
                if (!CanAffordTrade(st, world, tuning)) { _lastTelemetry.BudgetBlocked++; break; }

                pool.Consume(i);
                OperationsManager.VectorDispatchInfo info;
                OperationsManager.HaulOutcome outcome = _dispatch.DispatchVector(
                    st, i, pool, ctx, world, tuning, context, profile, out info);

                switch (outcome)
                {
                    case OperationsManager.HaulOutcome.Dispatched:
                        _lastTelemetry.Tier1++;
                        break;
                    case OperationsManager.HaulOutcome.DispatchedViaPickup:
                        _lastTelemetry.Tier2++;
                        break;
                    case OperationsManager.HaulOutcome.NoShipAnywhere:
                    case OperationsManager.HaulOutcome.RouteInfeasible:
                        _lastTelemetry.NoShip++;
                        _lastTelemetry.Tier3++;
                        ledger.Release(v.SourceNodeId, v.ItemKey, v.Quantity);
                        continue;
                    case OperationsManager.HaulOutcome.LoadTooLight:
                        _lastTelemetry.AssessRefused++;
                        pool.Release(i);   // nothing flew; a later convoy to the same buyer may carry it
                        continue;          // still booked: released below if nothing folds it in
                    default:
                        ledger.Release(v.SourceNodeId, v.ItemKey, v.Quantity);
                        continue;   // load failed, an endpoint changed hands, or a node vanished
                }

                // The one dispatch-time charge. Levied per vector, outside any staged context, and
                // only for a convoy that actually flew -- a staged Ship: demand costs nothing.
                world.Spend(st.Tag, tuning.TradeCost);
                _tradeBudget.Record(st.Tag, world.UtcNow, tuning.TradeCost, tuning.SpendBudgetWindowSeconds);
                claims.Add(claim);
                tradeClaims.Add(claim);
                inFlight++;

                DemandClass rowClass;
                classByRow.TryGetValue(RowKey(v.DeliverNodeId, v.ItemKey), out rowClass);
                long margin;
                marginByRow.TryGetValue(RowKey(v.DeliverNodeId, v.ItemKey), out margin);
                if (profile.IsProcurement) _lastTelemetry.Procured++;
                else _lastTelemetry.Dispatched++;
                LogDispatched(log, st.Tag, profile, v, rowClass, margin, info);
            }

            // Whatever the loop never took -- the fleet cap or the budget ended it, or a light load
            // nothing folded in -- did not fly and claims nothing.
            for (int i = 0; i < pool.Count; i++)
                if (!pool.IsConsumed(i)) ledger.Release(pool[i].SourceNodeId, pool[i].ItemKey, pool[i].Quantity);
        }

        /// <summary>One line per convoy, carrying the vector's own fields — the item, the quantity
        /// and the class of want that raised it — so a settled trade can be traced back to the row
        /// it was formed from. <c>qty</c> is what actually boarded when the hull loaded at dispatch
        /// (tier 1) and the planned quantity when it is still flying out to load (tier 2). <c>margin</c>
        /// is what the source node could still part with when it was chosen, after every claim on it
        /// and before this vector's own — the ceiling <c>qty</c> was held under.</summary>
        private static void LogDispatched(List<string> log, string actorTag,
            OperationsManager.VectorDispatchProfile profile, MaterialVector v, DemandClass demandClass,
            long margin, OperationsManager.VectorDispatchInfo info)
        {
            string prefab = string.IsNullOrEmpty(info.PrefabId) ? "" : " prefab=" + info.PrefabId;
            long qty = info.LoadedQuantity > 0 ? info.LoadedQuantity : v.Quantity;
            if (profile.IsProcurement)
                log.Add("PROCURE_DISPATCHED " + info.LogId + " " + actorTag + " <- " + profile.CounterpartyTag
                        + " item=" + v.ItemKey + " qty=" + qty + " class=" + demandClass
                        + " buy_at=" + v.SourceNodeId + " deliver=" + v.DeliverNodeId
                        + " tier=" + info.Tier + prefab + " margin=" + margin);
            else
                log.Add("TRADE_DISPATCHED " + info.LogId + " " + actorTag + " -> " + profile.CounterpartyTag
                        + " item=" + v.ItemKey + " qty=" + qty + " class=" + demandClass
                        + " from=" + v.SourceNodeId + " to=" + v.DeliverNodeId
                        + " tier=" + info.Tier + prefab + " margin=" + margin);
        }

        // ---- vector formation --------------------------------------------------------------------

        /// <summary>One consenting seller node, as a candidate for one row.</summary>
        private struct SourceCandidate
        {
            public string NodeId;
            public string OwnerTag;
            /// <summary>What the node can still part with, after every claim on it.</summary>
            public long Available;
            /// <summary>What it would carry for this row: <c>min(Available, row remaining)</c>.</summary>
            public long Quantity;
            public double Distance;
            /// <summary>The owner's position in the buyer's open-partner list; 0 in the sell
            /// direction, where the owner is the actor itself.</summary>
            public int PartnerRank;

            /// <summary>The ranking, in order:
            /// <list type="number">
            /// <item><b>How much of the row it covers</b> (<see cref="Quantity"/>, larger first). A
            /// marginal node that can cover a sliver loses to a depot that can cover the row — the
            /// FAC-109 finding, where an 18 ML node beat 100 ML depots on distance alone. Capped at
            /// the row, so any node that covers it all is as good as any other on this key and a
            /// far-off giant does not beat a near node that is already enough. Not margin per
            /// distance: that ratio mixes units, and would still send a hull after a sliver that
            /// happens to sit next door.</item>
            /// <item><b>Distance</b>, nearer first — among nodes that cover the same amount, the
            /// cheaper trip.</item>
            /// <item><b>Partner rank</b> (<see cref="FactionState.TradePriority"/> order). The
            /// score is a partner-wide aggregate of reach, resource value and relation, not a
            /// statement about this item at this node, so it only breaks what the two keys above
            /// leave tied — the job the old first-partner-wins rule gave it outright.</item>
            /// <item>Node id, ordinal, for determinism.</item>
            /// </list></summary>
            public bool IsBetterThan(SourceCandidate other)
            {
                if (Quantity != other.Quantity) return Quantity > other.Quantity;
                if (Distance != other.Distance) return Distance < other.Distance;
                if (PartnerRank != other.PartnerRank) return PartnerRank < other.PartnerRank;
                return string.CompareOrdinal(NodeId, other.NodeId) < 0;
            }
        }

        /// <summary>The best node of <paramref name="ownerTag"/>'s estate to source
        /// <paramref name="itemKey"/> from, by <see cref="SourceCandidate.IsBetterThan"/>: one that
        /// consents (<see cref="TradeConsent"/> — the same rule the dock re-applies) and still has
        /// consenting margin left after every claim on it (<see cref="TradeSellerLedger"/>). The
        /// vector is sized to that margin, never to the node's whole surplus: the whole surplus was
        /// promised to every row that asked, and the dock refused everyone after the first.
        /// <para>An item whose kind the registry does not know is never traded: consent cannot be
        /// evaluated for it. With <see cref="NullPriceOracle"/> wired every scarcity reads a constant
        /// 1.0, which is above the ceiling — so no oracle means no evidence anyone is oversupplied in
        /// anything, and no vector forms. That is the safe degeneracy.</para></summary>
        private bool FindConsentingSource(string ownerTag, string itemKey, Vec3 needPos, long remaining,
            TradeSellerLedger ledger, WorldModel world, FactionTuning tuning, out SourceCandidate best,
            out SourceCandidate nearest, string flyerTag, string needNodeId,
            string buyerTag = null, List<string> log = null)
        {
            best = default(SourceCandidate);
            nearest = default(SourceCandidate);
            bool priced = flyerTag != null && needNodeId != null && world.TransitLedger.HasAny(flyerTag);
            if (remaining <= 0) return false;
            ItemKind ignoredKind;
            // Recognition only: an item the registry cannot place is not tradable at all. The kind
            // it resolves to is deliberately unused — consent below is decided per inventory key.
            // Asked through the world, which strips the "Ore:"/"Comp:" prefix the registry does not
            // key on; asking the registry with the raw key refused every ore and component row.
            if (!world.TryGetInventoryItemKind(itemKey, out ignoredKind)) return false;

            int[] refused = DebugTraces.Trade && log != null ? new int[7] : null;
            foreach (SpatialNode n in world.Graph.OwnedBy(ownerTag))
            {
                bool stocked;
                TradeConsent.Reason reason;
                long available = ledger.Available(n, ownerTag, itemKey, _oracle, world, tuning, _consentShortMemo,
                    out stocked, out reason);
                if (!stocked) continue;
                if (reason != TradeConsent.Reason.Consents)
                {
                    if (refused != null) refused[(int)reason]++;
                    continue;
                }
                if (available <= 0)
                {
                    if (refused != null) refused[NoMarginSlot]++;
                    continue;
                }

                var c = new SourceCandidate
                {
                    NodeId    = n.Id,
                    OwnerTag  = ownerTag,
                    Available = available,
                    Quantity  = remaining < available ? remaining : available,
                    Distance  = n.Position.DistanceTo(needPos),
                };
                if (nearest.NodeId == null || c.IsBetterThan(nearest)) nearest = c;
                // A lane whose last dispatch paid for hostile space reads that cost as distance, so
                // an equally covering source a cheaper distance away wins (TransitLedger).
                if (priced) c.Distance += world.TransitLedger.PenaltyKm(flyerTag, n.Id, needNodeId, world.UtcNow);
                if (best.NodeId == null || c.IsBetterThan(best)) best = c;
            }
            if (best.NodeId == null)
            {
                if (refused != null) LogConsentRefused(log, ownerTag, buyerTag, itemKey, refused);
                return false;
            }
            return true;
        }

        // "flyer|need|item" -> the last re-pick line, so it logs on change.
        private readonly Dictionary<string, string> _lastRepick = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>One <c>TRANSIT_REPICK</c> line when a lane's recorded transit cost moved the choice
        /// off the source the plain ranking names, on change only.</summary>
        private void LogRepick(string flyerTag, string itemKey, string needNodeId, SourceCandidate chosen,
            SourceCandidate plain, WorldModel world, List<string> log)
        {
            if (log == null || chosen.NodeId == null || plain.NodeId == null) return;
            string key = flyerTag + "|" + needNodeId + "|" + itemKey;
            if (string.Equals(chosen.NodeId, plain.NodeId, StringComparison.Ordinal))
            {
                _lastRepick.Remove(key);
                return;
            }
            string signature = chosen.NodeId + ">" + plain.NodeId;
            string prior;
            if (_lastRepick.TryGetValue(key, out prior) && prior == signature) return;
            _lastRepick[key] = signature;
            log.Add(TransitPlanner.FormatRepick(flyerTag, itemKey, needNodeId, chosen.NodeId, plain.NodeId,
                world.TransitLedger.PenaltyKm(flyerTag, plain.NodeId, needNodeId, world.UtcNow), plain.Distance,
                chosen.Distance));
        }

        // The refusal trace's slot for a node that consents but has nothing left above its floor
        // once claims are counted -- past the last TradeConsent.Reason.
        private const int NoMarginSlot = 6;

        // "seller|buyer|item" -> the last refusal line's counts, so the trace logs on change.
        private readonly Dictionary<string, string> _lastConsentRefusal = new Dictionary<string, string>(StringComparer.Ordinal);

        private void LogConsentRefused(List<string> log, string sellerTag, string buyerTag, string itemKey, int[] refused)
        {
            string counts = "stocked_nodes=" + (refused[1] + refused[2] + refused[3] + refused[4] + refused[5] + refused[NoMarginSlot])
                + " scarce=" + refused[(int)TradeConsent.Reason.Scarce]
                + " own_row=" + refused[(int)TradeConsent.Reason.OwnRow]
                + " faction_short=" + refused[(int)TradeConsent.Reason.FactionShort]
                + " switch_off=" + refused[(int)TradeConsent.Reason.SwitchOff]
                + " no_oracle=" + refused[(int)TradeConsent.Reason.NoOracle]
                + " no_margin=" + refused[NoMarginSlot];
            string key = sellerTag + "|" + (buyerTag ?? "?") + "|" + itemKey;
            string prior;
            if (_lastConsentRefusal.TryGetValue(key, out prior) && prior == counts) return;
            _lastConsentRefusal[key] = counts;
            log.Add("TRADE_CONSENT_REFUSED seller=" + sellerTag + " buyer=" + (buyerTag ?? "?") + " item=" + itemKey + " " + counts);
        }

        /// <summary>One row per (node, item), most urgent first. The published set is keyed on the
        /// demand register's own composite key, which also carries the destination, the sale flag and
        /// the grid size — so two rows can name the same node and item. They are folded on the
        /// MAXIMUM remaining quantity, not the sum: each row's remaining was already netted against
        /// that node's stock, so adding two of them would order the same shortfall twice.</summary>
        private static List<UnservedDemand> DedupUnserved(List<UnservedDemand> rows)
        {
            var folded = new List<UnservedDemand>();
            if (rows == null || rows.Count == 0) return folded;

            var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (UnservedDemand row in rows)
            {
                if (row.NodeId == null || row.ItemKey == null || row.Remaining <= 0) continue;
                string key = RowKey(row.NodeId, row.ItemKey);
                int at;
                if (!indexByKey.TryGetValue(key, out at))
                {
                    indexByKey[key] = folded.Count;
                    folded.Add(row);
                    continue;
                }
                UnservedDemand existing = folded[at];
                if (row.Remaining > existing.Remaining) existing.Remaining = row.Remaining;
                existing.Partial = existing.Partial && row.Partial;   // wholly unserved wins
                if (row.Urgency > existing.Urgency) { existing.Urgency = row.Urgency; existing.Class = row.Class; }
                folded[at] = existing;
            }

            // Stable on the published order, so two rows of equal urgency are served in the order
            // their owner's matcher emitted them -- the same determinism SortByUrgency gives the Haul
            // pass, which List.Sort's own instability would not.
            var order = new List<int>(folded.Count);
            for (int i = 0; i < folded.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int cmp = folded[b].Urgency.CompareTo(folded[a].Urgency);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });
            var sorted = new List<UnservedDemand>(folded.Count);
            for (int i = 0; i < order.Count; i++) sorted.Add(folded[order[i]]);
            return sorted;
        }

        private static string RowKey(string nodeId, string itemKey)
        {
            return nodeId + "|" + itemKey;
        }

        // ---- budget / fleet-cap helpers ------------------------------------------------------------

        private bool CanAffordTrade(FactionState st, WorldModel world, FactionTuning tuning)
        {
            long treasury = world.GetTreasury(st.Tag);
            if (treasury < tuning.TradeCost) return false;
            // Resolved per faction: a player can set how much of their own money trade may take, and
            // an unset knob reads the world's tuning. An NPC has no override and reads tuning too,
            // so this is the same three numbers it always was for one.
            double incomeFraction = st.TradeIncomeFraction(tuning);
            long budgetFloor = st.TradeBudgetFloorFor(tuning);
            double wealthFraction = st.TradeWealthFraction(tuning);
            long tradeSurplus = wealthFraction > 0
                ? Economy.SpendableTreasurySurplus(treasury, world.GetNetFlow(st.Tag), tuning)
                : 0;
            return _tradeBudget.CanSpend(st.Tag, world.UtcNow, st.CentralBankRecentIncome, tuning.TradeCost,
                incomeFraction, budgetFloor,
                tuning.SpendBudgetWindowSeconds,
                tradeSurplus, wealthFraction);
        }

        /// <summary>Unresolved, un-intercepted Trade convoys this faction has out. Public because a
        /// player-facing readback reports it against <see cref="FactionTuning.MaxTradeFleets"/>,
        /// which is one of the two caps that silently ends a pass.</summary>
        public static int InFlightTradeCount(FactionState s)
        {
            int n = 0;
            foreach (PendingFleetAction pf in s.PendingFleets)
                if (pf.Kind == ActionKind.Trade && !pf.Resolved && !pf.Intercepted) n++;
            return n;
        }

        // ---- diagnostics ---------------------------------------------------------------------------

        /// <summary>Value snapshot of every sweep-cumulative <see cref="TradeTickTelemetry"/> field
        /// <see cref="MaybeLogTradeTickPerf"/> reports as a per-faction delta -- captured once per
        /// faction so the reported deltas stay local to that one faction rather than the whole sweep.
        /// Mirrors OperationsManager's OpsPerfBaseline.</summary>
        private struct TradePerfBaseline
        {
            public int Partners, UnservedSeen, Vectors, Dispatched, Procured, NoShip;
            public int RefusedConsent, Claimed, AssessRefused, Tier1, Tier2, Tier3;
            public int Barred, OptedOut;
            public long Dispatch;

            public static TradePerfBaseline Capture(TradeTickTelemetry t)
            {
                return new TradePerfBaseline
                {
                    Partners = t.Partners, UnservedSeen = t.UnservedSeen, Vectors = t.Vectors,
                    Dispatched = t.Dispatched, Procured = t.Procured, NoShip = t.NoShip,
                    RefusedConsent = t.RefusedConsent, Claimed = t.Claimed, AssessRefused = t.AssessRefused,
                    Tier1 = t.Tier1, Tier2 = t.Tier2, Tier3 = t.Tier3,
                    Barred = t.Barred, OptedOut = t.OptedOut,
                    Dispatch = t.DispatchTicks,
                };
            }
        }

        /// <summary>Aggregated per-faction diagnostic line. Emitted above the cost threshold OR
        /// whenever anything actually dispatched: a gate that selects against the cheap path cannot
        /// measure it, and the dispatch counts are the numbers this pass exists to report.</summary>
        private void MaybeLogTradeTickPerf(List<string> log, string factionTag, long factionStartTicks,
            TradePerfBaseline before)
        {
            TradeTickTelemetry t = _lastTelemetry;
            int dispatched = t.Dispatched - before.Dispatched;
            int procured   = t.Procured   - before.Procured;
            double totalMs = TicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - factionStartTicks);
            if (totalMs <= PerfLogThresholdMs && dispatched == 0 && procured == 0) return;

            log.Add("TRADE_TICK_PERF " + factionTag
                    + " partners=" + (t.Partners - before.Partners)
                    + " unservedSeen=" + (t.UnservedSeen - before.UnservedSeen)
                    + " vectors=" + (t.Vectors - before.Vectors)
                    + " dispatched=" + dispatched
                    + " procured=" + procured
                    + " noShip=" + (t.NoShip - before.NoShip)
                    + " refusedConsent=" + (t.RefusedConsent - before.RefusedConsent)
                    + " barred=" + (t.Barred - before.Barred)
                    + " optedOut=" + (t.OptedOut - before.OptedOut)
                    + " claimed=" + (t.Claimed - before.Claimed)
                    + " assessRefused=" + (t.AssessRefused - before.AssessRefused)
                    + " tier1=" + (t.Tier1 - before.Tier1)
                    + " tier2=" + (t.Tier2 - before.Tier2)
                    + " tier3=" + (t.Tier3 - before.Tier3)
                    + " totalMs=" + totalMs.ToString("0.00")
                    + " dispatchMs=" + TicksToMs(t.DispatchTicks - before.Dispatch).ToString("0.00"));
        }

        /// <summary>Diagnostic threshold only, not gameplay behaviour: avoids a permanent
        /// per-faction-per-tick log line for the common, cheap case. Settable for tests, as
        /// <see cref="OperationsManager.PerfLogThresholdMs"/> is; the host never sets it.</summary>
        public double PerfLogThresholdMs { get; set; } = 1.0;

        private static double TicksToMs(long stopwatchTicks)
        {
            return stopwatchTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }
}
