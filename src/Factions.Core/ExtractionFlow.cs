using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The <i>mined</i> term of the NPC extraction planner's flow gap (docs/mining-plan.md, "The NPC
    /// planner"): a per-node, per-ore estimate of how many units Mine fleets deliver there per hour.
    ///
    /// <para>Credited at the one place mined ore reaches its hub — the Mine case of
    /// <c>FleetResolution</c>'s service-stop resolution, the line that writes <c>MINE_RESOLVED</c> —
    /// with the units that actually arrived, after interception scaling. Crediting at the dwell
    /// instead would count ore still in a hold that might never land, and would key it to the
    /// deposit rather than the hub the planner reasons about.</para>
    ///
    /// <para>Same shape as <see cref="FuelDraw.CreditBurn"/>: a value-and-timestamp pair that decays
    /// by <c>e</c> over <see cref="FactionTuning.ExtractionMinedEmaHours"/>, each credit adding
    /// <c>units ÷ that span</c>, so a steady delivery rate settles at its real units per hour and a
    /// hub that stops receiving falls away. Derived on read; nothing ticks it.</para>
    /// </summary>
    public static class ExtractionFlow
    {
        /// <summary>Below this many units per hour an entry is dropped, so a hub that stopped
        /// receiving an ore eventually carries no key for it in the brain file.</summary>
        internal const double MinedEmaPruneBelow = 0.01;

        /// <summary>Folds <paramref name="units"/> of <paramref name="key"/> delivered to
        /// <paramref name="node"/> into its <see cref="SpatialNode.MinedEma"/>.</summary>
        public static void CreditMined(SpatialNode node, DateTime nowUtc, FactionTuning tuning,
                                       string key, double units)
        {
            if (node == null || tuning == null || string.IsNullOrEmpty(key) || units <= 0) return;
            double tauHours = tuning.ExtractionMinedEmaHours;
            if (tauHours <= 0) return;

            Decay(node, nowUtc, tauHours);
            double prior;
            node.MinedEma.TryGetValue(key, out prior);
            node.MinedEma[key] = prior + units / tauHours;
            node.MinedEmaAtUtc = nowUtc;
        }

        /// <summary>The mined-rate estimate for one ore key at <paramref name="node"/> as of
        /// <paramref name="nowUtc"/>, in units per hour.</summary>
        public static double MinedRateAt(SpatialNode node, string key, DateTime nowUtc, FactionTuning tuning)
        {
            if (node == null || tuning == null || string.IsNullOrEmpty(key)) return 0;
            double stored;
            if (!node.MinedEma.TryGetValue(key, out stored) || stored <= 0) return 0;
            double decayed = stored * DecayFactor(node.MinedEmaAtUtc, nowUtc, tuning.ExtractionMinedEmaHours);
            return decayed > 0 ? decayed : 0;
        }

        private static void Decay(SpatialNode node, DateTime nowUtc, double tauHours)
        {
            if (node.MinedEma.Count == 0) return;
            double factor = DecayFactor(node.MinedEmaAtUtc, nowUtc, tauHours);
            if (factor >= 1.0) return;

            var keys = new List<string>(node.MinedEma.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                double next = node.MinedEma[keys[i]] * factor;
                if (next < MinedEmaPruneBelow) node.MinedEma.Remove(keys[i]);
                else node.MinedEma[keys[i]] = next;
            }
        }

        private static double DecayFactor(DateTime sinceUtc, DateTime nowUtc, double tauHours)
        {
            if (tauHours <= 0 || sinceUtc == default(DateTime)) return 1.0;
            double hours = (nowUtc - sinceUtc).TotalHours;
            if (hours <= 0) return 1.0;           // also covers a clock that went backwards on reload
            return Math.Exp(-hours / tauHours);
        }
    }
}
