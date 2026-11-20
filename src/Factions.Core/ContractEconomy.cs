using System;

namespace Factions.Core
{
    /// <summary>
    /// Pure reward math for player-completable contracts that substitute for an automated brain/
    /// FleetManager action (e.g. Reinforce-via-Repair). The reward mirrors the action's own tuning
    /// cost rather than vanilla's own contract-type reward formula, scaled up by how urgently the
    /// faction needs the action performed.
    /// </summary>
    public static class ContractEconomy
    {
        /// <summary>Reward = <paramref name="actionCost"/> scaled up to <paramref name="premiumMax"/>
        /// fraction extra at <paramref name="urgency01"/> == 1, rounded to the nearest 1000 to match
        /// existing contract reward rounding conventions.</summary>
        public static long ComputePremiumReward(long actionCost, double urgency01, double premiumMax)
        {
            double u = Math.Max(0.0, Math.Min(1.0, urgency01));
            long raw = (long)Math.Round(actionCost * (1.0 + u * premiumMax));
            return raw / 1000 * 1000;
        }

        /// <summary>
        /// The largest quantity of an item a faction can afford to offer a contract for, given
        /// <paramref name="unitReward"/> per unit and <paramref name="budgetRemaining"/> credits of
        /// window capacity. Returns 0 when it cannot fund even one unit — the caller's signal not to
        /// generate the contract at all.
        ///
        /// <para><b>Trims the quantity, never the unit price.</b> Halving what a contract pays per
        /// unit produces an offer no player would take and quietly misprices the item everywhere the
        /// same number is read; asking for less of it stays honest about both.</para>
        ///
        /// <para>Item-value contracts had no affordability term at all: reward is
        /// <c>round(basePrice x scarcity) x amount</c> and the only treasury check in the file gates
        /// the Reinforce type. Live 2026-08-29 ISBT offered 200 Explosives at
        /// <c>57,214 x 3 = 34,328,000</c> credits against a **150,000** treasury — 229x everything the
        /// faction owned — and the day's running total was 8.65bn across 212 contracts (FAC-156).</para>
        ///
        /// <para>Divides rather than multiplies to test affordability, for the same reason
        /// <c>StationListings.TrimToBudget</c> does: a five-figure unit reward against a large
        /// requested quantity overflows a long the other way round.</para>
        /// </summary>
        public static int AffordableContractQuantity(int requestedAmount, long unitReward, long budgetRemaining)
        {
            if (requestedAmount <= 0) return 0;
            if (budgetRemaining == long.MaxValue) return requestedAmount;   // uncapped
            if (unitReward <= 0) return requestedAmount;                    // free to reward; nothing to bound
            if (budgetRemaining <= 0) return 0;

            long affordable = budgetRemaining / unitReward;
            if (affordable <= 0) return 0;
            return affordable < requestedAmount ? (int)affordable : requestedAmount;
        }
    }
}
