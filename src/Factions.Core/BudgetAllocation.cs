using System;

namespace Factions.Core
{
    /// <summary>
    /// How far below its reference a node is holding an item — the shared need signal both sides of
    /// a station's shelf read.
    ///
    /// <para>This class carried a concentrating allocator until 2026-08-30, which split one bounded
    /// pot across competing buy rows by need. It was retired with the pot: store rows are now sized
    /// independently against the whole budget (operator direction — see
    /// <see cref="StationListings"/>'s <c>EmitSized</c>), so nothing divides a window between rows
    /// any more. The contract side never batched and keeps its own per-offer slice in
    /// <c>FactionContractStrategy.ContractRewardSliceFor</c>.</para>
    /// </summary>
    public static class BudgetAllocation
    {
        /// <summary>Score for a candidate that has no usable reference — participates, does not
        /// dominate. Deliberately mid-range rather than 0: an item the oracle has no opinion about is
        /// not thereby known to be unwanted.</summary>
        public const double NeutralScore = 0.5;

        /// <summary>
        /// How far below its reference a node is holding <paramref name="stock"/>, as 0..1.
        /// 1 = holding none of it, 0 = at or above target.
        ///
        /// <para>Deliberately **not** <c>ScarcityMultiplier</c>, which is the obvious choice and
        /// cannot do this job: it is clamped to <c>[ScarcityMin, ScarcityMax]</c> and saturates. Live
        /// 2026-08-29, both rows on the operator's screen priced at exactly 3.0 — `Stone` at
        /// `10 x 3` and `Construction` at `1,144 x 3` — so ranking by it would have called them equal.
        /// This ratio uses the same two quantities unclamped, so it still discriminates where the
        /// multiplier has flattened.</para>
        /// </summary>
        public static double ShortfallScore(long reference, long stock)
        {
            if (reference <= 0) return NeutralScore;
            if (stock <= 0) return 1.0;
            if (stock >= reference) return 0.0;
            return (double)(reference - stock) / reference;
        }
    }
}
