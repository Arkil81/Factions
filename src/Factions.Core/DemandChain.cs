using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// How much of each material a faction's outstanding commitments actually require, netted
    /// against what it already holds at every level (docs/demand-chains.md).
    ///
    /// <para><b>What this replaces.</b> A production bank sizes its input demand as machine count x
    /// recipe throughput x planning horizon — what it could consume if fed continuously.
    /// <see cref="FactionEventKind"/>-era FAC-054 named that plainly: <c>PROD_INPUT_DEMAND</c> is not
    /// a scarcity signal, it is an appetite. An appetite is structurally unretireable, so
    /// <see cref="DemandUrgency"/>'s starvation term pins at 1.0 and its age term climbs forever: a
    /// demand that can never be met becomes indistinguishable from one that is merely unmet.</para>
    ///
    /// <para><b>Netting is per level and against the aggregate.</b> Stock is consumed by the first
    /// level that can use it and is not offered again lower down, so two chains never both net the
    /// same 900 ingots. That is also what makes the mechanism serve the <em>frontier</em> rather
    /// than the leaf: ingots on hand leave nothing to push down to ore, so no ore is asked for, and
    /// as each level fills the frontier rises on its own. Nothing has to notice that mining is
    /// finished and switch it off.</para>
    ///
    /// <para><b>It never invents demand.</b> Same constraint <c>DemandHistory</c> carries: the
    /// output is bounded by the roots handed in. A faction with no builds and no installs gets an
    /// empty map and every consumer is unchanged for it.</para>
    /// </summary>
    public static class DemandChain
    {
        /// <summary>What one key's level of a chain still needs, and on whose behalf.</summary>
        public struct Requirement
        {
            /// <summary>Gross units this level's commitments want — not the shortfall. See the
            /// contract note on <see cref="Requirements"/>.</summary>
            public long Quantity;

            /// <summary>The class of the strongest commitment this level serves, inherited whole
            /// from the root rather than stamped from what the level locally is.
            ///
            /// <para><b>Not attenuated by depth</b>, deliberately. Re-burying the root under the
            /// symptom it causes is the defect this exists to remove: an <c>Ore:Iron</c> that a
            /// blocked hull is waiting on ranked at half the weight of the hull, and level with
            /// every routine background input in the network. Depth is not evidence of
            /// unimportance. (The candidate discriminator, if the class term turns out to saturate,
            /// is how <em>binding</em> the chain is — docs/demand-chains.md open question 3 — and it
            /// is unmeasured, so it is not guessed at here.)</para></summary>
            public DemandClass Class;
        }

        /// <summary>What the commitments rooted in <paramref name="rootOutstanding"/> still require,
        /// by inventory key, after netting <paramref name="stockOf"/> at every level.
        ///
        /// <para>Quantities are <b>gross</b>: what the commitments want at that level, not what is
        /// missing. Netting happens once, downstream, where the contract puts it. What netting does
        /// here is decide what <em>descends</em> — a level the node already covers passes nothing
        /// down, so no ore is asked for to make ingots that are on the shelf.</para>
        ///
        /// <para>A key is absent when no commitment leads to it at all. Callers must not read that
        /// as zero: capping an absent key would silence standing production that no chain was ever
        /// entitled to speak for.</para></summary>
        /// <param name="rootOutstanding">Outstanding requirement per key, summed over every
        /// commitment: a <see cref="BuildQueueEntry"/>'s <c>Bom</c> less its <c>Collected</c>, an
        /// install BOM. Gross, in the same sense <c>MaterialVector.Quantity</c> is gross — this
        /// method does the netting.</param>
        /// <param name="expansion">The table for the capabilities this faction actually holds. A
        /// key it cannot produce is a leaf, and the chain stops there whether or not some better
        /// equipped faction could go further.</param>
        /// <param name="stockOf">On-hand quantity of a key, over whatever scope the caller means by
        /// "already held" — network-wide for a network-wide answer.</param>
        public static Dictionary<string, Requirement> Requirements(
            IEnumerable<KeyValuePair<string, Requirement>> rootOutstanding,
            RecipeExpansion expansion,
            Func<string, long> stockOf)
        {
            var required = new Dictionary<string, Requirement>(StringComparer.Ordinal);
            if (rootOutstanding == null || expansion == null) return required;

            // Gross want per key, and the level it has to be settled at. A key can be wanted both as
            // a root commitment and as an input to a taller chain, so the two accumulate.
            var want    = new Dictionary<string, double>(StringComparer.Ordinal);
            var cls     = new Dictionary<string, DemandClass>(StringComparer.Ordinal);
            var byLevel = new Dictionary<int, List<string>>();

            foreach (var root in rootOutstanding)
            {
                if (string.IsNullOrEmpty(root.Key) || root.Value.Quantity <= 0) continue;
                Add(want, cls, byLevel, expansion, root.Key, root.Value.Quantity, root.Value.Class);
            }
            if (want.Count == 0) return required;

            // Descending height, so every consumer of a key is settled before the key itself and a
            // level is visited exactly once. Acyclic by construction -- RecipeExpansion pruned the
            // cycles -- so an input always sits strictly lower than what consumes it.
            for (int level = expansion.MaxHeight; level >= 0; level--)
            {
                List<string> keys;
                if (!byLevel.TryGetValue(level, out keys)) continue;

                foreach (string key in keys)
                {
                    DemandClass keyClass;
                    cls.TryGetValue(key, out keyClass);

                    double gross;
                    if (!want.TryGetValue(key, out gross) || gross <= 0) continue;

                    long held = stockOf != null ? stockOf(key) : 0;
                    if (held < 0) held = 0;

                    // The posted quantity is GROSS, never the shortfall. DemandEntry.Quantity is
                    // gross by contract (docs/material-vectors.md) because MatchVectors nets the
                    // destination's stock and DemandUrgency.Starvation takes haveAtNode separately.
                    // A poster that nets too is not merely conservative: Starvation returns 0 the
                    // moment haveAtNode >= quantity, so a partly-covered row would score zero and be
                    // read as "no claim" by both the dispatch ordering and the Haul-vs-Mine choice,
                    // dropping out of ranking entirely.
                    required[key] = new Requirement
                    {
                        Quantity = (long)Math.Ceiling(gross),
                        Class    = keyClass,
                    };

                    // Netting governs the DESCENT only, and that is where it belongs: it is what
                    // stops a chain demanding ore for ingots the node is already holding, and what
                    // makes the frontier rise on its own as each level fills.
                    double shortfall = gross - held;
                    if (shortfall <= 0) continue;

                    // Only the shortfall descends. Stock consumed here is not offered to the level
                    // below, which is what stops a chain demanding ore for ingots it already holds.
                    // The class descends whole with it.
                    IReadOnlyDictionary<string, double> inputs;
                    if (!expansion.TryGetDirectInputs(key, out inputs)) continue;
                    foreach (var input in inputs)
                        Add(want, cls, byLevel, expansion, input.Key, input.Value * shortfall, keyClass);
                }
            }
            return required;
        }

        /// <summary>Accumulates a want at its level, and raises the key's class to
        /// <paramref name="quantity"/>'s own if that commitment is the stronger one.
        /// <para>The strongest wins because the class asks what a shortfall <em>costs</em>: a key
        /// serving both a stalled hull and a catalogue top-up is as expensive to leave unmet as the
        /// hull. Summing or averaging would let routine wants dilute the commitment that actually
        /// matters, which is the same inversion read from the other side.</para></summary>
        private static void Add(Dictionary<string, double> want, Dictionary<string, DemandClass> cls,
            Dictionary<int, List<string>> byLevel,
            RecipeExpansion expansion, string key, double quantity, DemandClass demandClass)
        {
            if (string.IsNullOrEmpty(key) || quantity <= 0) return;

            DemandClass known;
            if (!cls.TryGetValue(key, out known) || demandClass > known) cls[key] = demandClass;

            double have;
            if (want.TryGetValue(key, out have))
            {
                want[key] = have + quantity;
                return;
            }
            want[key] = quantity;

            int level = expansion.Height(key);
            List<string> keys;
            if (!byLevel.TryGetValue(level, out keys))
            {
                keys = new List<string>();
                byLevel[level] = keys;
            }
            keys.Add(key);
        }
    }
}
