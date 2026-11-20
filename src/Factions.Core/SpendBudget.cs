using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Per-faction windowed spend budget for one operational spender (e.g. reinforce vs.
    /// Patrol vs. Trade dispatch). Budget is
    /// <c>recentIncome * incomeFraction + treasurySurplus * wealthFraction + floor</c> — anchored to
    /// what the faction actually earns plus a bounded draw on treasury above its operational
    /// reserve (see <see cref="Economy.SpendableTreasurySurplus"/>), so a spender can't drain a
    /// balance below the reserve faster than it's replenished. In-memory only, never persisted — a
    /// restart just starts a fresh window. Owned as a field on the manager that spends.</summary>
    public sealed class SpendBudget
    {
        private struct Window
        {
            public DateTime StartUtc;
            public long Spent;
        }

        private readonly Dictionary<string, Window> _windows = new Dictionary<string, Window>();

        /// <summary>True if <paramref name="cost"/> fits within this window's remaining budget for
        /// <paramref name="tag"/>. Uncapped when <paramref name="windowSeconds"/> &lt;= 0, or when
        /// both <paramref name="incomeFraction"/> and <paramref name="floor"/> are &lt;= 0 (the
        /// rollback lever — zeroing only one of the two is a real policy, not a disable switch).</summary>
        public bool CanSpend(string tag, DateTime nowUtc, double recentIncome, long cost,
            double incomeFraction, long floor, double windowSeconds,
            long treasurySurplus = 0, double wealthFraction = 0)
        {
            if (windowSeconds <= 0 || (incomeFraction <= 0 && floor <= 0)) return true;

            Window w = GetOrRollWindow(tag, nowUtc, windowSeconds);
            long budget = ComputeBudget(recentIncome, incomeFraction, floor, treasurySurplus, wealthFraction);
            return w.Spent + cost <= budget;
        }

        /// <summary>Numeric remaining capacity in the current window for <paramref name="tag"/>.
        /// Same uncapped contract as <see cref="CanSpend"/> (returns <see cref="long.MaxValue"/>),
        /// but for callers that need to know *how much* still fits — e.g. partial-fill settlement —
        /// rather than just whether one fixed cost fits.</summary>
        public long RemainingBudget(string tag, DateTime nowUtc, double recentIncome,
            double incomeFraction, long floor, double windowSeconds,
            long treasurySurplus = 0, double wealthFraction = 0)
        {
            if (windowSeconds <= 0 || (incomeFraction <= 0 && floor <= 0)) return long.MaxValue;

            Window w = GetOrRollWindow(tag, nowUtc, windowSeconds);
            long budget = ComputeBudget(recentIncome, incomeFraction, floor, treasurySurplus, wealthFraction);
            long remaining = budget - w.Spent;
            return remaining > 0 ? remaining : 0;
        }

        private static long ComputeBudget(double recentIncome, double incomeFraction, long floor,
            long treasurySurplus, double wealthFraction)
        {
            double budget = recentIncome * incomeFraction + floor;
            if (treasurySurplus > 0 && wealthFraction > 0)
                budget += treasurySurplus * wealthFraction;
            return budget >= long.MaxValue ? long.MaxValue : (long)budget;
        }

        /// <summary>Records a spend of <paramref name="cost"/> against the current window for
        /// <paramref name="tag"/>. Call only after the spend actually happens — some callers check
        /// affordability, then abort for an unrelated reason (e.g. no ship stock) without spending.</summary>
        public void Record(string tag, DateTime nowUtc, long cost, double windowSeconds)
        {
            Window w = GetOrRollWindow(tag, nowUtc, windowSeconds);
            w.Spent += cost;
            _windows[tag] = w;
        }

        private Window GetOrRollWindow(string tag, DateTime nowUtc, double windowSeconds)
        {
            Window w;
            bool has = _windows.TryGetValue(tag, out w);
            if (!has || (nowUtc - w.StartUtc).TotalSeconds >= windowSeconds)
                w = new Window { StartUtc = nowUtc, Spent = 0 };
            return w;
        }
    }
}
