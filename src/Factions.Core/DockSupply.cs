using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One place a docked ship can take goods: a gas tank, a weapon's ammo inventory, or a
    /// cargo container's share of a reserve. <see cref="Slot"/> is the host's own index back to the
    /// block; the planner never reads it.</summary>
    public sealed class DockSupplySink
    {
        public int Slot;
        public string InvKey;
        /// <summary>Inventory units this sink can take — litres for gas, items for everything else.</summary>
        public long Capacity;
    }

    /// <summary>What the station will sell of one key: the same quantity and price its store row
    /// carries, with <see cref="Available"/> already in inventory units.</summary>
    public sealed class DockSupplyOffer
    {
        public long Available;
        /// <summary>Per priced unit — per kilolitre for gas, per item otherwise (see
        /// <see cref="ItemCredits"/>).</summary>
        public long UnitPrice;
    }

    /// <summary>The first thing that stopped a plan short of filling every sink.</summary>
    public enum DockSupplyLimit : byte
    {
        /// <summary>Every sink filled.</summary>
        None = 0,
        /// <summary>Nothing to fill: every sink was already full, or there were none.</summary>
        NoSpace = 1,
        /// <summary>The station sells less than the sinks would take.</summary>
        Stock = 2,
        /// <summary>The budget — the buyer's wallet, or the total they confirmed — ran out first.</summary>
        Funds = 3,
    }

    /// <summary>The result of <see cref="DockSupply.Plan"/>. <see cref="Units"/> is parallel to the
    /// sink list the plan was made from.</summary>
    public sealed class DockSupplyPlan
    {
        public long[] Units = new long[0];
        public readonly Dictionary<string, long> UnitsByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> CreditsByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        public long Credits;
        public DockSupplyLimit Limit;

        public long TotalUnits
        {
            get
            {
                long total = 0;
                foreach (long u in UnitsByKey.Values) total += u;
                return total;
            }
        }
    }

    /// <summary>One cargo-reserve line: this many units of this key into the ship's cargo.</summary>
    public struct DockReserveRequest
    {
        public string InvKey;
        public long Count;
        /// <summary>Volume of one unit, in litres.</summary>
        public double UnitLitres;
    }

    /// <summary>
    /// The arithmetic behind the Services Terminal's Refuel and Rearm: how much of what the station
    /// sells lands in which of a docked ship's tanks, guns and containers, for how much.
    ///
    /// <para><b>Priced per key, not per sink.</b> Credits are <see cref="ItemCredits.For(string,long,long)"/>
    /// of each key's total, so twelve tanks of hydrogen cost exactly what one tank of the same
    /// litres would. Pricing sink by sink would round each tank down on its own and undercharge by up
    /// to a credit per tank.</para>
    ///
    /// <para><b>Never over budget.</b> The budget is the buyer's balance or the total they confirmed,
    /// whichever is smaller, and the sum of <see cref="DockSupplyPlan.CreditsByKey"/> never exceeds
    /// it. That is what lets the host charge the whole plan up front with a wallet debit SE refuses
    /// rather than clamps.</para>
    ///
    /// <para>Sinks are served in the order given. The host puts guns before cargo so a thin
    /// station's stock loads the weapons before it fills a reserve.</para>
    /// </summary>
    public static class DockSupply
    {
        public static DockSupplyPlan Plan(IList<DockSupplySink> sinks,
            IDictionary<string, DockSupplyOffer> offers, long budget)
        {
            var plan = new DockSupplyPlan();
            if (sinks == null || sinks.Count == 0)
            {
                plan.Limit = DockSupplyLimit.NoSpace;
                return plan;
            }
            plan.Units = new long[sinks.Count];
            if (budget < 0) budget = 0;

            long wanted = 0;
            DockSupplyLimit limit = DockSupplyLimit.None;

            for (int i = 0; i < sinks.Count; i++)
            {
                DockSupplySink sink = sinks[i];
                if (sink == null || sink.Capacity <= 0 || string.IsNullOrEmpty(sink.InvKey)) continue;
                wanted += sink.Capacity;

                DockSupplyOffer offer;
                if (offers == null || !offers.TryGetValue(sink.InvKey, out offer) || offer == null
                    || offer.Available <= 0 || offer.UnitPrice <= 0)
                {
                    if (limit == DockSupplyLimit.None) limit = DockSupplyLimit.Stock;
                    continue;
                }

                long taken = Get(plan.UnitsByKey, sink.InvKey);
                long spentOnKey = Get(plan.CreditsByKey, sink.InvKey);
                long stockLeft = offer.Available - taken;

                // What the rest of the budget reaches to on this key, counted over the key's whole
                // total so the per-key rounding in ItemCredits.For is the one the buyer is charged.
                long budgetLeft = budget - plan.Credits;
                long reachable = MaxUnitsFor(sink.InvKey, budgetLeft + spentOnKey, offer.UnitPrice) - taken;

                long units = Math.Min(sink.Capacity, Math.Min(stockLeft, reachable));
                if (units < 0) units = 0;
                if (units < sink.Capacity && limit == DockSupplyLimit.None)
                    limit = stockLeft <= reachable ? DockSupplyLimit.Stock : DockSupplyLimit.Funds;
                if (units == 0) continue;

                plan.Units[i] = units;
                long total = taken + units;
                plan.UnitsByKey[sink.InvKey] = total;
                long credits = CreditsFor(sink.InvKey, total, offer.UnitPrice);
                plan.CreditsByKey[sink.InvKey] = credits;
                plan.Credits += credits - spentOnKey;
            }

            plan.Limit = wanted == 0 ? DockSupplyLimit.NoSpace : limit;
            return plan;
        }

        /// <summary>What <paramref name="units"/> of one key cost: <see cref="ItemCredits.For(string,long,long)"/>,
        /// but never nothing for something. A 100 L top-up at a few credits a kilolitre rounds to
        /// zero, and a service that delivers goods for free can be repeated for free.
        ///
        /// <para>Within budget whenever <see cref="MaxUnitsFor"/> allowed the units: the floor only
        /// applies when some units were reachable, which takes a budget of at least one credit.</para></summary>
        public static long CreditsFor(string invKey, long units, long unitPrice)
        {
            if (units <= 0 || unitPrice <= 0) return 0;
            return Math.Max(1L, ItemCredits.For(invKey, units, unitPrice));
        }

        /// <summary>The most inventory units of <paramref name="invKey"/> whose
        /// <see cref="ItemCredits.For(string,long,long)"/> is within <paramref name="credits"/>.
        ///
        /// <para>Finer than <see cref="ItemCredits.Affordable"/> for gas, which answers in whole
        /// kilolitres: a buyer with 4,999 credits and hydrogen at 5,000 a kilolitre can still take
        /// 999 litres, and a tank top-up is exactly that shape.</para></summary>
        public static long MaxUnitsFor(string invKey, long credits, long unitPrice)
        {
            if (credits <= 0 || unitPrice <= 0) return 0;
            long per = ItemCredits.UnitsPerPricedUnit(invKey);
            if (per == 1) return credits / unitPrice;
            if (credits > long.MaxValue / per) return ItemCredits.Affordable(invKey, credits, unitPrice);
            return credits * per / unitPrice;
        }

        /// <summary>A store row's quantity in inventory units: its <c>Quantity</c> is in priced units,
        /// which for gas is kilolitres.</summary>
        public static long OfferUnits(string invKey, long listedQuantity)
        {
            if (listedQuantity <= 0) return 0;
            long per = ItemCredits.UnitsPerPricedUnit(invKey);
            return listedQuantity > long.MaxValue / per ? long.MaxValue : listedQuantity * per;
        }

        /// <summary>A unit price with SE's friendly-reputation discount applied, never below 1. The
        /// store block applies the same multiplier to the rows it sells, so a friend pays the same
        /// here as at the counter.</summary>
        public static long ApplyMultiplier(long unitPrice, double multiplier)
        {
            if (unitPrice <= 0) return 0;
            if (double.IsNaN(multiplier) || multiplier <= 0 || multiplier >= 1) return unitPrice;
            return Math.Max(1L, (long)Math.Round(unitPrice * multiplier));
        }

        /// <summary>
        /// Splits cargo-reserve requests across containers by free volume, in container order, each
        /// request filling the first container before moving to the next. Returns one sink per
        /// (container, key) that takes anything; <see cref="DockSupplySink.Slot"/> is the index into
        /// <paramref name="freeLitres"/>.
        ///
        /// <para>Volume only. A cargo container's mass limit is not a constraint SE ships with, and
        /// the host re-reads what actually fits at delivery and refunds what did not land.</para>
        /// </summary>
        public static List<DockSupplySink> PlanCargoReserve(IList<double> freeLitres,
            IList<DockReserveRequest> requests)
        {
            var sinks = new List<DockSupplySink>();
            if (freeLitres == null || requests == null) return sinks;

            var free = new double[freeLitres.Count];
            for (int c = 0; c < free.Length; c++) free[c] = Math.Max(0.0, freeLitres[c]);

            foreach (DockReserveRequest request in requests)
            {
                if (string.IsNullOrEmpty(request.InvKey) || request.Count <= 0 || request.UnitLitres <= 0) continue;
                long remaining = request.Count;
                for (int c = 0; c < free.Length && remaining > 0; c++)
                {
                    // A hair of tolerance so 100 magazines of 0.2 L each fit in exactly 20 L: the
                    // division is floating point, SE's volume check is fixed point.
                    long fits = (long)Math.Floor(free[c] / request.UnitLitres + 1e-9);
                    long units = Math.Min(remaining, fits);
                    if (units <= 0) continue;
                    free[c] -= units * request.UnitLitres;
                    remaining -= units;
                    sinks.Add(new DockSupplySink { Slot = c, InvKey = request.InvKey, Capacity = units });
                }
            }
            return sinks;
        }

        private static long Get(Dictionary<string, long> map, string key)
        {
            long v;
            return map.TryGetValue(key, out v) ? v : 0;
        }
    }
}
