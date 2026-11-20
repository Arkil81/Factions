using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Decides what a faction stocks its player store with, now that no prefab is tagged as store
    /// merchandise (FAC-060). The shelf is drawn from the same capability-derived pool the faction
    /// builds its own fleet from, so something has to choose — and the operator's rule is that the
    /// SE faction type selects the capability class while the personality modulates within it:
    /// type answers "what kind of hull", personality answers "which of them".
    ///
    /// <para><b>Prescriptive, not learned.</b> Both inputs are authored: a type is world-recipe
    /// data and a personality is assigned at registration. Nothing here reads purchase history,
    /// which is deliberate — history exists only for hulls that were already listed, so a rule
    /// reading it can only ever recommend more of what is already on the shelf and can never
    /// surface a hull nobody has been offered. Deferred by the operator on separate grounds
    /// (2026-09-03: it would redefine the small/large slot knob); recorded here because this is
    /// the class that would otherwise grow it.</para>
    ///
    /// <para><b>The weights are a first pass.</b> Same standing as <see
    /// cref="FactionTuning.MinCargoLitersForRoleClassificationSmall"/> and its siblings — placed to
    /// be tuned against a real shelf, not derived from anything. They are deliberately shallow: a
    /// type's own class always outweighs any single trait, so personality can shade a shelf but
    /// never invert it.</para>
    /// </summary>
    public static class StoreShelfPlan
    {
        /// <summary>The three capability classes a shelf can want, named by the fleet role that
        /// already expresses each one. These are real roles from the shared pool — the whole point
        /// of the retirement is that a shelf hull is a hull, so it is demanded by the same role a
        /// fleet build would use.</summary>
        public const string Armed = "Raid";
        public const string Cargo = "Trade";
        public const string Drill = "Mine";

        private static readonly string[] Classes = { Cargo, Armed, Drill };

        /// <summary>Class weights for an SE faction type. An unresolved or unknown type falls to the
        /// balanced default rather than to nothing: a faction whose type the catalogue cannot name
        /// still keeps a shelf (see <see cref="FactionStorePolicy.HasPlayerStore"/>, which reads an
        /// unresolved tag the same forgiving way).</summary>
        private static void BaseWeights(string factionType, out double cargo, out double armed, out double drill)
        {
            switch (factionType)
            {
                case "Military": cargo = 0.30; armed = 0.60; drill = 0.10; return;
                case "Pirate":   cargo = 0.35; armed = 0.55; drill = 0.10; return;
                case "Miner":    cargo = 0.35; armed = 0.10; drill = 0.55; return;
                case "Builder":  cargo = 0.60; armed = 0.20; drill = 0.20; return;
                case "Trader":   cargo = 0.70; armed = 0.15; drill = 0.15; return;
                default:         cargo = 0.50; armed = 0.25; drill = 0.25; return;
            }
        }

        /// <summary>Target share per class for one faction, type-led and personality-shaded.
        /// Returns a map whose values sum to 1.</summary>
        public static Dictionary<string, double> Shares(string factionType, Personality personality)
        {
            double cargo, armed, drill;
            BaseWeights(factionType, out cargo, out armed, out drill);

            // Personality shades the base rather than competing with it. TraitInfluence caps how far
            // a trait can move a share, so the type's own class stays dominant at every trait value
            // -- a mercantile Military faction still shelves mostly warships.
            if (personality != null && personality.Traits != null)
            {
                Traits t = personality.Traits;
                cargo += TraitInfluence * Clamp01(t.Mercantilism);
                armed += TraitInfluence * Clamp01(Math.Max(t.Aggression, t.Xenophobia));
                drill += TraitInfluence * Clamp01(t.Expansionism);
            }

            double total = cargo + armed + drill;
            if (total <= 0) { cargo = 1.0; armed = 0.0; drill = 0.0; total = 1.0; }

            return new Dictionary<string, double>(StringComparer.Ordinal)
            {
                { Cargo, cargo / total },
                { Armed, armed / total },
                { Drill, drill / total },
            };
        }

        /// <summary>How far a single trait at 1.0 may move a class's raw weight before
        /// normalisation. 0.20 against a type's 0.55-0.70 lead keeps the type dominant: at the
        /// extreme a trait closes part of the gap and never crosses it.</summary>
        private const double TraitInfluence = 0.20;

        /// <summary>The class the next shelf hull should belong to: whichever is furthest below its
        /// target share of a shelf of <paramref name="targetTotal"/> hulls, given what is already
        /// listed. Deterministic — no RNG, no clock — so a test can state the whole sequence.
        ///
        /// <para>Filling by largest shortfall rather than by drawing on the weights is what makes a
        /// small shelf honest: at 5 slots a 0.55/0.35/0.10 split cannot be sampled meaningfully, but
        /// it can be *filled* in order, and the resulting shelf reads as the intended mix rather
        /// than as a run of luck.</para></summary>
        /// <param name="listedByClass">How many hulls of each class are already on the shelf.
        /// Missing keys count as zero.</param>
        public static string NextClass(string factionType, Personality personality,
            IReadOnlyDictionary<string, double> sharesOverride,
            IDictionary<string, int> listedByClass, int targetTotal)
        {
            IReadOnlyDictionary<string, double> shares = sharesOverride ?? Shares(factionType, personality);
            if (targetTotal < 1) targetTotal = 1;

            string best = null;
            double worstShortfall = double.NegativeInfinity;
            foreach (string cls in Classes)
            {
                double share;
                if (!shares.TryGetValue(cls, out share)) share = 0;
                int have = 0;
                if (listedByClass != null) listedByClass.TryGetValue(cls, out have);
                double shortfall = (share * targetTotal) - have;
                // Ties resolve by the fixed Classes order (cargo, armed, drill) rather than by
                // dictionary iteration, so the sequence is reproducible.
                if (shortfall > worstShortfall)
                {
                    worstShortfall = shortfall;
                    best = cls;
                }
            }
            return best ?? Cargo;
        }

        /// <summary>Which class a prefab belongs to for shelf-composition purposes, using the same
        /// predicates dispatch uses. A hull that satisfies several is counted under the narrowest
        /// it satisfies (drill, then armed, then cargo) so a multi-role hull does not inflate the
        /// class a faction is least short of.</summary>
        public static string ClassOf(IPrefabCatalogue catalogue, string prefabId)
        {
            if (catalogue == null || string.IsNullOrEmpty(prefabId)) return null;
            if (ShipPool.IsMineSuitable(catalogue, prefabId)) return Drill;
            if (ShipPool.IsCombatSuitable(catalogue, prefabId)) return Armed;
            if (ShipPool.IsCargoSuitable(catalogue, prefabId)) return Cargo;
            return null;
        }

        /// <summary>Whether <paramref name="prefabId"/> may be offered at a station standing at
        /// <paramref name="stationPosKm"/> — the operator's region-and-gravity gate on the shelf.
        ///
        /// <para>It is the same degenerate same-point <see cref="ShipPool.RouteEnvironment"/> the
        /// builder uses (<c>ShipyardManager.FilterZoneFeasible</c>), which is the point: a shelf hull
        /// is gated by exactly the physics a dispatched hull is. One call answers both halves,
        /// because the environment carries gravity <i>and</i> atmosphere at the point — so an
        /// atmospheric-only hull fails in vacuum and an ion hull fails in a well, each for its own
        /// reason rather than by a rule about zones.</para>
        ///
        /// <para><b>Where</b> is a property of the selling station, not of the owning network. A
        /// faction's two stations in different regimes therefore show different shelves off one
        /// shared pool — which is the whole reason the gate is applied here rather than at the yard:
        /// gating only at build time would still let a hull reserved for a surface node appear at a
        /// deep-space one.</para>
        ///
        /// <para>An unscanned hull is never excluded (<see cref="ShipPool.IsRouteFeasible"/> returns
        /// true at <c>DryMassKg == 0</c>), so missing scan data cannot empty a shelf.</para></summary>
        public static bool IsShelfableAt(IPrefabCatalogue catalogue, string prefabId,
            IPlanetCatalogue planets, Vec3 stationPosKm, double minDryTwr)
        {
            if (catalogue == null || string.IsNullOrEmpty(prefabId)) return false;
            if (planets == null) return true;   // no planet data wired: gate nothing, same as the builder
            ShipPool.RouteEnvironment here = ShipPool.RouteEnvironment.From(
                planets, stationPosKm, stationPosKm, minDryTwr);
            return ShipPool.IsRouteFeasible(catalogue, prefabId, here);
        }

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
    }
}
