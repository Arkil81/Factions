using System;

namespace Factions.Core
{
    /// <summary>
    /// How badly one demand wants to be served, on a single scale every consumer shares.
    ///
    /// <para>Every consumer reads the same score, which is the whole reason it is one function:
    /// <see cref="OperationsManager"/> orders its matched vector list by it (before this, the order
    /// was whichever item happened to be posted into <see cref="DemandRegister"/> first, which at
    /// one-hull scale decided outright who got the ship). The Haul-vs-Mine choice that also
    /// multiplied a Mine option's estimate by it was retired with the NPC demand-row Mine trigger
    /// (FAC-395 slice 2b).</para>
    ///
    /// <para><b>Shape.</b> <c>class x starvation^k x (1 + ageW x age) x (1 + histW x history)</c>.
    /// Starvation is the discriminator and the other three are multipliers on it, so a fully-served
    /// demand scores zero however old, however historically hungry, and whatever raised it — the
    /// score can never invent a want, matching the constraint DemandHistory already carries in
    /// docs/material-vectors.md.</para>
    /// </summary>
    public static class DemandUrgency
    {
        /// <summary>Fallback class weight when tuning carries no row for a class — the same 1.0 an
        /// ordinary catalogue want gets, never 0.</summary>
        public const double DefaultClassWeight = 1.0;

        /// <summary>Urgency of one demand, given what the demanding node actually holds.
        /// <para>0 means "not wanted": the demand is met, its quantity is unset, or starvation has
        /// been tuned out. Callers treat 0 as "no claim", never as "lowest claim".</para></summary>
        /// <param name="demand">The demand being ranked. <see cref="DemandEntry.Quantity"/> is the
        /// gross want (see docs/material-vectors.md — MatchVectors nets the destination's stock, so
        /// the poster does not).</param>
        /// <param name="haveAtNode">On-hand stock of the same key at the demanding node.</param>
        /// <param name="historyValue">The node's <see cref="SpatialNode.DemandHistory"/> EMA for this
        /// item, in weighted liters. 0 when absent.</param>
        /// <param name="nowUtc">World clock, for the age term.</param>
        public static double Score(DemandEntry demand, long haveAtNode, double historyValue,
            DateTime nowUtc, FactionTuning tuning)
        {
            return Score(demand, haveAtNode, historyValue, nowUtc, tuning, 0);
        }

        /// <summary>The same score, told how many hulls are parked at the demanding node.
        /// <para>Read only by <see cref="DemandClass.Fuel"/>: docs/fuel-plan.md's decision 6 puts a
        /// fuel row at every founded node and lets ship stock boost its urgency rather than decide
        /// its existence. Every other class ignores the count, so a caller with no node in hand may
        /// pass 0 and gets exactly what it got before.</para></summary>
        /// <param name="parkedHulls">Hulls in the demanding node's <see cref="SpatialNode.ShipStock"/>.</param>
        public static double Score(DemandEntry demand, long haveAtNode, double historyValue,
            DateTime nowUtc, FactionTuning tuning, int parkedHulls)
        {
            if (tuning == null || demand.Quantity <= 0) return 0;

            double starvation = Starvation(demand.Quantity, haveAtNode, tuning);
            if (starvation <= 0) return 0;

            return ClassWeight(demand.Class, tuning)
                 * ParkedHullScale(demand.Class, parkedHulls, tuning)
                 * starvation
                 * (1.0 + tuning.UrgencyAgeWeight * Age01(demand.FirstPostedUtc, nowUtc, tuning))
                 * (1.0 + tuning.UrgencyHistoryWeight * History01(historyValue, tuning));
        }

        /// <summary>The hull-count multiplier a <see cref="DemandClass.Fuel"/> row gets:
        /// <c>1 + FuelUrgencyPerParkedHull × hulls</c>, capped at
        /// <see cref="FactionTuning.FuelUrgencyHullCap"/>. 1.0 for every other class and for a node
        /// with nothing parked, so a fuel row never ranks below what its class weight alone gives
        /// it.</summary>
        public static double ParkedHullScale(DemandClass demandClass, int parkedHulls, FactionTuning tuning)
        {
            if (demandClass != DemandClass.Fuel || parkedHulls <= 0 || tuning == null) return 1.0;
            if (tuning.FuelUrgencyPerParkedHull <= 0) return 1.0;
            double scale = 1.0 + tuning.FuelUrgencyPerParkedHull * parkedHulls;
            double cap = tuning.FuelUrgencyHullCap;
            return cap > 1.0 && scale > cap ? cap : scale;
        }

        /// <summary>The fraction of the want that is unmet, sharpened by
        /// <see cref="FactionTuning.UrgencyStarvationExponent"/>. 1 = the node holds none of it.</summary>
        public static double Starvation(long quantity, long haveAtNode, FactionTuning tuning)
        {
            if (quantity <= 0) return 0;
            if (haveAtNode >= quantity) return 0;
            double unmet = haveAtNode <= 0 ? 1.0 : 1.0 - ((double)haveAtNode / quantity);

            double k = tuning != null ? tuning.UrgencyStarvationExponent : 1.0;
            if (k <= 0) return 0;          // starvation tuned out -- the score collapses with it
            if (k == 1.0) return unmet;    // the common case, no pow call
            return Math.Pow(unmet, k);
        }

        /// <summary>How far this demand has aged toward
        /// <see cref="FactionTuning.UrgencyAgeHorizonHours"/>, clamped to 0..1.
        /// <para>An unset <paramref name="firstPostedUtc"/> reads as 0 (brand new) rather than as
        /// fully aged: a register with no clock must not promote every demand it holds.</para></summary>
        public static double Age01(DateTime firstPostedUtc, DateTime nowUtc, FactionTuning tuning)
        {
            if (firstPostedUtc == default(DateTime)) return 0;
            double horizonHours = tuning != null ? tuning.UrgencyAgeHorizonHours : 0;
            if (horizonHours <= 0) return 0;
            double ageHours = (nowUtc - firstPostedUtc).TotalHours;
            if (ageHours <= 0) return 0;   // also covers a clock that went backwards across a reload
            return MathUtil.Clamp01(ageHours / horizonHours);
        }

        /// <summary>The saturating history term, 0..1. See
        /// <see cref="FactionTuning.UrgencyHistoryWeight"/> for why this is not linear.</summary>
        public static double History01(double historyValue, FactionTuning tuning)
        {
            if (historyValue <= 0) return 0;
            double saturation = tuning != null ? tuning.UrgencyHistorySaturation : 0;
            if (saturation <= 0) return 0;
            return historyValue / (historyValue + saturation);
        }

        /// <summary>The tuned multiplier for <paramref name="demandClass"/>, or
        /// <see cref="DefaultClassWeight"/> when tuning carries no row for it.</summary>
        public static double ClassWeight(DemandClass demandClass, FactionTuning tuning)
        {
            double[] weights = tuning != null ? tuning.UrgencyClassWeights : null;
            int index = (int)demandClass;
            if (weights == null || index < 0 || index >= weights.Length) return DefaultClassWeight;
            double w = weights[index];
            return w > 0 ? w : DefaultClassWeight;
        }
    }
}
