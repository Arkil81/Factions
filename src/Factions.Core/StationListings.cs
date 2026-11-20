using System;
using System.Collections.Generic;

namespace Factions.Core
{
    public enum StationListingSide : byte
    {
        /// <summary>The station sells this to a player.</summary>
        Offer = 0,
        /// <summary>The station buys this from a player.</summary>
        Order = 1,
    }

    /// <summary>One row a station's store carries: what, which way, at what unit price, how much.
    ///
    /// <para><see cref="Quantity"/> and <see cref="UnitPrice"/> are both in the unit the row trades
    /// in, which for gas is the <b>kilolitre</b> and for everything else the inventory unit. A row
    /// is a store listing, and SE settles gas in kilolitres.</para></summary>
    public struct StationListing
    {
        public string InvKey;
        public StationListingSide Side;
        public long UnitPrice;
        public long Quantity;
    }

    /// <summary>One buy candidate the budget could not seat, for the caller that logs it.
    ///
    /// <para>Exists because <c>budgettrimmed</c> is a bare count: reconstructing which twelve items
    /// a station refused, and why, took a world-save read and a price table on FAC-176, and the
    /// answer it produced rested on the wrong budget figure. A refusal names itself.</para></summary>
    public struct StationBudgetRefusal
    {
        public string InvKey;
        /// <summary>Price the row would have carried — <c>base x scarcity</c>, the number that
        /// decides affordability. The whole point of the record.</summary>
        public long UnitPrice;
        /// <summary>Units wanted before the budget was consulted.</summary>
        public long WantUnits;
    }

    /// <summary>
    /// The host-owned facts a listing needs that Core cannot know: whether an inventory key is
    /// listable at all (it resolves to a known <see cref="ItemKind"/> and a real SE definition), and
    /// the per-type quantity ceiling.
    ///
    /// <para>A port rather than loose lambdas so every consumer passes one object and cannot supply
    /// one rule without the other — an item the store block skips for want of a definition must also
    /// be absent from any remote view of that station, or the two disagree about what is for sale.</para>
    /// </summary>
    public interface IStationListingPolicy
    {
        bool CanList(string invKey);
        /// <summary>Most of <paramref name="invKey"/> one row may carry, in inventory units.
        /// <see cref="StationListings.Uncapped"/> means no ceiling.</summary>
        long ListingCap(string invKey);
    }

    /// <summary>
    /// What a station buys and sells, and at what price, from the node model alone.
    ///
    /// <para>The store block and any remote market view cannot share SE's own store list:
    /// <c>StationStoreManager</c> only populates <c>MyFactionStation.StoreItems</c> for a station
    /// whose grid is spawned, which happens when a player is near it. Anything showing prices for a
    /// station nobody is standing at has to compute them, and two implementations of one price means
    /// a player reads one number remotely and is charged another on arrival.</para>
    ///
    /// <para>Prices and thresholds resolve per inventory key, never per <see cref="ItemKind"/> — two
    /// components of one kind differ by orders of magnitude in book value (FAC-130).</para>
    /// </summary>
    public static class StationListings
    {
        /// <summary>Offer an item once stock exceeds this fraction of its reference.</summary>
        public const double OfferThreshold = 0.2;

        /// <summary>In the no-catalogue fallback, order an item while stock is under this fraction.</summary>
        public const double OrderThreshold = 0.8;

        /// <summary>Below this base price an item is not worth a row.</summary>
        public const long MinBasePrice = 1;

        /// <summary>
        /// Appends every row this node's store carries to <paramref name="into"/>.
        /// </summary>
        /// <param name="orderKeys">The owning faction type's <c>OrdersList</c>. Null or empty selects
        /// the inventory-threshold fallback, which is what a world with no faction-type catalogue
        /// gets. Either way every key the faction demands where this station can answer it joins
        /// the candidates (<see cref="StationBuyDemand.DemandedKeys"/>).</param>
        /// <param name="noDemand">How many <paramref name="orderKeys"/> candidates were rejected for
        /// want of a live faction demand. The only observable signal that the buy-side filter is
        /// running: it is structurally 0 if the filter is bypassed.</param>
        /// <param name="buyBudget">Credits the owning faction could pay this window.
        /// <see cref="Uncapped"/> disables the bound. Applied to **each row on its own**, never
        /// across the set: a row is an advertisement, not a reservation, so the sum may exceed the
        /// window and the row that outlives it fails at settlement (operator, 2026-08-30). Bounding
        /// each row is still what stops a 30,000,000-credit order against a 350,000 treasury
        /// (FAC-159) — that defect was one row, not a set.</param>
        /// <param name="budgetTrimmed">How many order rows the budget shortened or dropped. Same
        /// contract as <paramref name="noDemand"/>: structurally 0 if the bound is bypassed.</param>
        /// <param name="refusals">Optional sink for the rows the budget could not seat at all, so a
        /// caller can name them rather than print a count. Null collects nothing.</param>
        /// <param name="noHeadroom">How many candidates were dropped because the demand is already
        /// satisfied or the node is already at its listing cap — <c>min(headroom, cap - stock) &lt;= 0</c>.
        /// The third and last silent exit from the buy side: without it a row can leave a player's
        /// store with no server-side trace at all, which is what made the churn in FAC-160 take a
        /// five-minute sampling run and a cross-day comparison to explain.</param>
        /// <param name="offList">How many candidates reached a row only because the faction demands
        /// them — absent from <paramref name="orderKeys"/>, or in the fallback not held by the node.
        /// Structurally 0 if demand stops nominating candidates.</param>
        /// <param name="orePriority">Emit ore rows ahead of every other buy row. Ordering only:
        /// rows no longer compete for a shared pot, so this decides what a player reads first in the
        /// store list, not who gets funded. See <see cref="IsOrePriority"/>.</param>
        public static void Build(
            SpatialNode node,
            IReadOnlyList<string> orderKeys,
            IPriceOracle oracle,
            IItemPriceCatalogue prices,
            DemandRegister demand,
            IStationListingPolicy policy,
            DateTime utcNow,
            List<StationListing> into,
            out int noDemand,
            long buyBudget,
            bool offersNeedWeighted,
            bool orePriority,
            out int budgetTrimmed,
            out int noHeadroom,
            out int offList,
            IList<StationBudgetRefusal> refusals = null)
        {
            noDemand = 0;
            budgetTrimmed = 0;
            noHeadroom = 0;
            offList = 0;
            if (node == null || into == null || policy == null || prices == null) return;

            AppendOffers(node, oracle, prices, policy, into, offersNeedWeighted);

            // Candidates are the faction type's OrdersList plus every key the faction demands where
            // this station can answer it. The list is flavour and demand is need, so a need off the
            // list still lists; every row still needs a live demand, so a list key with none still
            // does not (FAC-486).
            List<string> demanded = StationBuyDemand.DemandedKeys(demand, node.OwnerFactionTag, node.Id, utcNow);
            var pending = new List<BuyRow>();
            if (orderKeys != null && orderKeys.Count > 0)
            {
                AddDemandSized(node, orderKeys, oracle, prices, demand, policy, utcNow, orePriority,
                    pending, ref noDemand, ref noHeadroom);
                int listed = pending.Count;
                AddDemandSized(node, Absent(demanded, orderKeys), oracle, prices, demand, policy, utcNow,
                    orePriority, pending, ref noDemand, ref noHeadroom);
                offList = pending.Count - listed;
            }
            else
            {
                AddInventoryShortfalls(node, oracle, prices, demand, policy, utcNow, orePriority,
                    pending, ref noDemand, ref noHeadroom);
                int held = pending.Count;
                AddDemandSized(node, NotHeld(demanded, node), oracle, prices, demand, policy, utcNow,
                    orePriority, pending, ref noDemand, ref noHeadroom);
                offList = pending.Count - held;
            }

            // A station may not advertise what it could not pay for. Offers are unaffected: selling
            // earns credits rather than spending them.
            EmitSized(pending, buyBudget, into, ref budgetTrimmed, refusals);
        }

        /// <summary><paramref name="keys"/> less every key in <paramref name="already"/>.</summary>
        private static List<string> Absent(List<string> keys, IReadOnlyList<string> already)
        {
            var result = new List<string>();
            if (keys.Count == 0) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < already.Count; i++)
                if (!string.IsNullOrEmpty(already[i])) seen.Add(already[i]);
            for (int i = 0; i < keys.Count; i++)
                if (!seen.Contains(keys[i])) result.Add(keys[i]);
            return result;
        }

        /// <summary><paramref name="keys"/> less every key the node holds: the fallback already
        /// judged those against their reference stock.</summary>
        private static List<string> NotHeld(List<string> keys, SpatialNode node)
        {
            var result = new List<string>();
            for (int i = 0; i < keys.Count; i++)
                if (!node.Inventory.ContainsKey(keys[i])) result.Add(keys[i]);
            return result;
        }

        /// <summary>Sentinel for "no budget bound" — matches <see cref="SpendBudget"/>'s own uncapped
        /// contract so a caller that has no opinion passes the same value both places.</summary>
        public const long Uncapped = long.MaxValue;


        private static void AppendOffers(
            SpatialNode node, IPriceOracle oracle, IItemPriceCatalogue prices,
            IStationListingPolicy policy, List<StationListing> into, bool needWeighted)
        {
            foreach (KeyValuePair<string, long> kv in node.Inventory)
            {
                if (kv.Value <= 0) continue;
                string invKey = kv.Key;
                if (!policy.CanList(invKey)) continue;

                long basePrice = prices.BasePrice(invKey);
                if (basePrice < MinBasePrice) continue;

                // A false return is "no opinion on this item here", which is the list-it side of the
                // threshold — the same thing a zero per-kind constant used to mean. Held back against
                // the station's own need, not the whole target: see IOfferKeepBack.
                long keepBack = KeepBackOf(oracle, node.Id, invKey);
                if (kv.Value <= keepBack * OfferThreshold) continue;

                long amount = Math.Min(kv.Value, policy.ListingCap(invKey));
                amount = OfferableAfterOwnNeed(amount, keepBack, kv.Value, needWeighted);
                amount = ItemCredits.PricedUnits(invKey, amount);
                if (amount <= 0) continue;

                into.Add(new StationListing
                {
                    InvKey    = invKey,
                    Side      = StationListingSide.Offer,
                    UnitPrice = PriceOf(basePrice, RowScarcity.ForOffer(oracle, node.Id, invKey, kv.Value,
                                                    amount * ItemCredits.UnitsPerPricedUnit(invKey))),
                    Quantity  = amount,
                });
            }
        }

        /// <summary>Collects a row for each of <paramref name="keys"/> the faction has a live demand
        /// for, sized by that demand.</summary>
        private static void AddDemandSized(
            SpatialNode node, IReadOnlyList<string> keys, IPriceOracle oracle,
            IItemPriceCatalogue prices, DemandRegister demand, IStationListingPolicy policy,
            DateTime utcNow, bool orePriority, List<BuyRow> pending,
            ref int noDemand, ref int noHeadroom)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                string invKey = keys[i];
                if (!policy.CanList(invKey)) continue;

                long basePrice = prices.BasePrice(invKey);
                if (basePrice < MinBasePrice) continue;

                long headroom;
                if (!StationBuyDemand.TryGetBuyQuantity(
                        demand, node.OwnerFactionTag, node.Id, invKey, utcNow, out headroom))
                {
                    noDemand++;
                    continue;
                }

                long currentStock;
                node.Inventory.TryGetValue(invKey, out currentStock);

                long amount = Math.Min(headroom, policy.ListingCap(invKey) - Math.Max(0, currentStock));
                amount = ItemCredits.PricedUnits(invKey, amount);
                if (amount <= 0) { noHeadroom++; continue; }

                pending.Add(new BuyRow
                {
                    InvKey    = invKey,
                    WantUnits = amount,
                    UnitPrice = PriceOf(basePrice, RowScarcity.ForOrder(oracle, node.Id, invKey, currentStock,
                                                    amount * ItemCredits.UnitsPerPricedUnit(invKey))),
                    OreFirst  = IsOrePriority(invKey, orePriority),
                });
            }
        }

        /// <summary>One collected buy candidate, before the budget is consulted.</summary>
        private struct BuyRow
        {
            public string InvKey;
            /// <summary>In the unit the row is priced in, so the budget bound in
            /// <see cref="EmitPass"/> divides like with like.</summary>
            public long WantUnits;
            public long UnitPrice;
            /// <summary>Emit ahead of the ordinary rows. Presentation, not funding.</summary>
            public bool OreFirst;
        }

        /// <summary>Sizes each buy row against the <b>whole</b> budget, independently, and emits it.
        ///
        /// <para><b>Rows are advertisements, not reservations</b> (operator, 2026-08-30): *"individual
        /// rows don't have to be treated as linked. Each row should be sized against the full budget.
        /// If a player exhausts the budget on one row, the other rows will simply fail."* A store row
        /// is a standing offer to buy, and a player clears at most one or two of them; making every
        /// row wait its turn in a shared pot priced the shelf for a run on the bank that does not
        /// happen.</para>
        ///
        /// <para><b>What this gives up, deliberately.</b> The sum across rows may exceed the window,
        /// so a large sale can leave the next row unfundable. That is the intended failure and it is
        /// self-correcting rather than silent: a real sale charges the same window through
        /// <c>SpendBudget.Record</c>, so the next refresh sizes every row against what is actually
        /// left and the shelf shrinks on its own.</para>
        ///
        /// <para><b>What it keeps.</b> No single row may exceed the budget — which is the whole of
        /// FAC-159's defect, a 30,000,000-credit stone row against a 350,000 treasury. Bounding each
        /// row is what fixed that; dividing the bound between rows was never the load-bearing part,
        /// and it cost FAC-176: at a 0.25 reserve and a 0.5 per-row share the cap was measured
        /// against the whole budget while the ordinary tier only ever saw a quarter of it, so the cap
        /// could not bind where it was needed and the first component row took the lot — measured at
        /// ISBT's live numbers, 77,220 credits out of a 40,559 "reserve".</para></summary>
        private static void EmitSized(
            List<BuyRow> pending, long budget, List<StationListing> into,
            ref int budgetTrimmed, IList<StationBudgetRefusal> refusals)
        {
            if (pending.Count == 0) return;

            // Two passes so ore reads first in the store list. Ordering only -- the second pass is
            // sized against the same budget as the first, never against what the first left.
            EmitPass(pending, budget, true, into, ref budgetTrimmed, refusals);
            EmitPass(pending, budget, false, into, ref budgetTrimmed, refusals);
        }

        private static void EmitPass(
            List<BuyRow> pending, long budget, bool oreFirstPass, List<StationListing> into,
            ref int budgetTrimmed, IList<StationBudgetRefusal> refusals)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                BuyRow r = pending[i];
                if (r.OreFirst != oreFirstPass) continue;

                long units = r.WantUnits;
                if (budget != Uncapped && r.UnitPrice > 0)
                {
                    // Divide, never multiply, to test affordability: a seven-figure want against a
                    // five-figure unit price overflows a long the other way round.
                    long affordable = budget / r.UnitPrice;
                    if (affordable < units) units = affordable;
                }

                if (units <= 0)
                {
                    budgetTrimmed++;
                    if (refusals != null)
                        refusals.Add(new StationBudgetRefusal
                        {
                            InvKey = r.InvKey, UnitPrice = r.UnitPrice, WantUnits = r.WantUnits,
                        });
                    continue;
                }
                if (units < r.WantUnits) budgetTrimmed++;

                into.Add(new StationListing
                {
                    InvKey    = r.InvKey,
                    Side      = StationListingSide.Order,
                    UnitPrice = r.UnitPrice,
                    Quantity  = units,
                });
            }
        }

        /// <summary>Fallback for a world with no faction-type catalogue: order what the node is short
        /// of. Carries the same demand gate — being under a threshold is not a reason to buy
        /// something the faction has no use for.</summary>
        private static void AddInventoryShortfalls(
            SpatialNode node, IPriceOracle oracle, IItemPriceCatalogue prices, DemandRegister demand,
            IStationListingPolicy policy, DateTime utcNow, bool orePriority, List<BuyRow> pending,
            ref int noDemand, ref int noHeadroom)
        {
            foreach (KeyValuePair<string, long> kv in node.Inventory)
            {
                string invKey = kv.Key;
                if (!policy.CanList(invKey)) continue;

                long basePrice = prices.BasePrice(invKey);
                if (basePrice < MinBasePrice) continue;

                long reference = ReferenceOf(oracle, node.Id, invKey);
                if (reference <= 0 || kv.Value >= reference * OrderThreshold) continue;

                long headroom;
                if (!StationBuyDemand.TryGetBuyQuantity(
                        demand, node.OwnerFactionTag, node.Id, invKey, utcNow, out headroom))
                {
                    noDemand++;
                    continue;
                }

                long amount = Math.Min(Math.Min(reference - kv.Value, headroom), policy.ListingCap(invKey));
                amount = ItemCredits.PricedUnits(invKey, amount);
                if (amount <= 0) { noHeadroom++; continue; }

                pending.Add(new BuyRow
                {
                    InvKey    = invKey,
                    WantUnits = amount,
                    UnitPrice = PriceOf(basePrice, RowScarcity.ForOrder(oracle, node.Id, invKey, kv.Value,
                                                    amount * ItemCredits.UnitsPerPricedUnit(invKey))),
                    OreFirst  = IsOrePriority(invKey, orePriority),
                });
            }
        }

        /// <summary>How much of <paramref name="amount"/> a node will part with, given how short of
        /// this item it is itself.
        ///
        /// <para>The **price** already answers "how badly is this needed" — offers are priced
        /// <c>base x ScarcityMultiplier</c>, so live 2026-08-29 ISBT N007 offered the iron ore it was
        /// short of at <c>143 x 1.88 = 269</c> while its glutted ice sat at the <c>0.5</c> floor. The
        /// **quantity** did not: <c>min(stock, cap)</c> put all 558 kg of that iron on the shelf while
        /// the node held zero <c>Ingot:Iron</c> and was posting `REFINE_DEMAND` for it (FAC-162).</para>
        ///
        /// <para>Weighted rather than blocked, per the operator: *"some sales is acceptable"*. A node
        /// at its reference parts with everything; one holding half of what it wants parts with half;
        /// one holding none parts with none. The weight is
        /// <see cref="BudgetAllocation.ShortfallScore"/> — deliberately the same need signal the buy
        /// side ranks by, so a station cannot want an item on one tab and not want it on the
        /// other.</para></summary>
        private static long OfferableAfterOwnNeed(long amount, long reference, long stock, bool needWeighted)
        {
            if (!needWeighted || amount <= 0 || reference <= 0) return amount;

            double keepBack = BudgetAllocation.ShortfallScore(reference, stock);
            if (keepBack <= 0) return amount;                  // at or above target: sell freely
            if (keepBack >= 1.0) return 0;                     // holding none of what it needs

            long offered = (long)(amount * (1.0 - keepBack));
            return offered > 0 ? offered : 0;
        }

        /// <summary>Whether one buy candidate is emitted ahead of the ordinary rows. Selling ore is
        /// what a player can do from the moment they spawn, so a station holding ore demand shows it
        /// at the top of the list.
        ///
        /// <para>This was a funding tier until 2026-08-30 and is now purely presentation: every row
        /// is sized against the whole budget, so ore no longer takes anything a component could have
        /// had and there is nothing left to reserve against it.</para></summary>
        private static bool IsOrePriority(string invKey, bool orePriority)
        {
            return orePriority && InventoryKeyMapper.IsOreKey(invKey);
        }

        /// <summary>What the store holds back of <paramref name="invKey"/>: the oracle's
        /// <see cref="IOfferKeepBack"/> answer, or its whole reference stock when it has none.</summary>
        private static long KeepBackOf(IPriceOracle oracle, string nodeId, string invKey)
        {
            var keep = oracle as IOfferKeepBack;
            if (keep == null) return ReferenceOf(oracle, nodeId, invKey);
            long keepBack;
            return keep.TryGetKeepBackStock(nodeId, invKey, out keepBack) ? keepBack : 0;
        }

        private static long ReferenceOf(IPriceOracle oracle, string nodeId, string invKey)
        {
            long reference;
            if (oracle == null || !oracle.TryGetReferenceStock(nodeId, invKey, out reference)) return 0;
            return reference;
        }

        /// <summary>`base × scarcity`, never below 1. The scarcity is the row's, from
        /// <see cref="RowScarcity"/>: an offer at its far end, an order at its midpoint.</summary>
        private static long PriceOf(long basePrice, double scarcity)
        {
            return Math.Max(1L, (long)Math.Round(basePrice * scarcity));
        }
    }
}
