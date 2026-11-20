using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// What a node holds and what it is worth, read from the world rather than assigned to it.
    ///
    /// <para><b>The rule.</b> A node's economic content is a function of where it is — the deposit
    /// under it and the body it hangs off — never of a label it was given or a die that was rolled
    /// for it. This class is the one place that reading happens, so the mint, Mine targeting and the
    /// display label cannot disagree about what a node is.</para>
    ///
    /// <para><b>What this replaces.</b> <c>FactionTuning.NodeKindExpandBias</c> rolled a new node's
    /// kind from its parent's, and that roll decided whether the world was ever asked: <see
    /// cref="WorldModel.ResolveDeposit(SpatialNode)"/> returned early on a kind the roll had not made
    /// mineable, so a node rolled <c>Core</c> sat on a platinum seam it could never see. Sampling is
    /// now unconditional and the deposit answers the question the roll used to.</para>
    ///
    /// <para><b>There is no label.</b> A node has no kind field and no kind ordinal: every question
    /// the old <c>NodeKind</c> answered is asked of the deposit or the site directly, here. What a
    /// player sees is derived at send time by <see cref="NodeDisplay"/>. See docs/issues/FAC-102.</para>
    /// </summary>
    public static class NodeContent
    {
        /// <summary>Is there ore under this node? Asked of the deposit, which is the sample, rather
        /// than of the kind, which was the roll.
        /// <para>A node whose sample the seam <see cref="DepositResolution.Declined"/> has no deposit
        /// and reads as not mineable — correct, because nothing is known to be there. It stays a
        /// backfill candidate until the seam answers.</para></summary>
        public static bool IsMineable(SpatialNode n)
        {
            return n != null && n.Deposit != null && !n.Deposit.IsBarren;
        }

        /// <summary>Has this node's ground never been looked at? The seam declined to answer for it
        /// (<see cref="DepositResolution.Declined"/>), so it holds no deposit at all.
        ///
        /// <para><b>Not the same as barren, and the difference is the whole point.</b> A barren node
        /// comes away holding an <i>empty</i> deposit — the seam answered, and the answer was
        /// nothing. Both read as not <see cref="IsMineable"/>, so a caller testing only that cannot
        /// tell proven-empty ground from ground nobody has sampled, and will hand the second one
        /// whatever it hands the first. <see cref="ResourceValueFor"/> is where that mattered.</para>
        ///
        /// <para>The state survives a restart: a barren deposit writes no entries but does write
        /// <see cref="SourceDoc.DepositPoints"/>, and the load path rebuilds the empty deposit from
        /// it, so a declined node is still the only one that comes back holding null.</para></summary>
        public static bool IsUnsampled(SpatialNode n)
        {
            return n != null && n.Deposit == null;
        }

        /// <summary>Nothing here worth noticing: no ore under it, nothing built on it, and no body to
        /// stand on. What a sensor sweep skips so knowledge does not fill up with empty claims.
        ///
        /// <para><b>A station is never empty space</b>, however barren its ground. The retired
        /// <c>Empty</c> label did not know that — it was derived from the deposit and the site alone —
        /// so a deep-space station with no ore under it read as empty and was skipped by both the
        /// knowledge sweep and the player scanner. Included here rather than left to each caller,
        /// because both were getting it wrong the same way.</para>
        ///
        /// <para>On a body is <see cref="SpatialNode.PlanetId"/> being set, matching
        /// <see cref="CapabilityAffinityFor"/>'s own <c>OnBody</c> reading.</para></summary>
        public static bool IsEmptySpace(SpatialNode n)
        {
            if (n == null) return false;
            if (IsMineable(n)) return false;
            if (n.PlanetId != null) return false;
            if (n.StableStationId > 0 || !string.IsNullOrEmpty(n.StationPrefabId)) return false;
            return CapabilityMaskIsEmpty(n);
        }

        private static bool CapabilityMaskIsEmpty(SpatialNode n)
        {
            if (n.Capabilities == null) return true;
            foreach (var entry in n.Capabilities)
                if (entry.Value > 0) return false;
            return true;
        }

        /// <summary>True when this node's deposit is dominated by ice.</summary>
        public static bool IsIce(SpatialNode n, FactionTuning tuning)
        {
            return IsIceDeposit(n, tuning);
        }

        /// <summary>True when this node's deposit is worth enough per unit, against the world's
        /// ordinary ore, to count as exceptional. See <see cref="DepositModel.IsRare"/>.</summary>
        public static bool IsRare(SpatialNode n, FactionTuning tuning,
            IItemPriceCatalogue prices, IItemRegistry registry)
        {
            return IsMineable(n) && DepositModel.IsRare(n.Deposit, tuning, prices, registry);
        }

        /// <summary>The ceiling this node's <see cref="SpatialNode.ResourceValue"/> may reach, read
        /// from the world in place of the retired per-kind table.
        ///
        /// <para>The three values reproduce what <c>NodeKindClassifier.DefaultResourceValue</c>
        /// returned for the kinds that could still occur — 1.0 for a mineable node, 0.8 for the
        /// industrial kinds, 0.0 for <c>Empty</c> — so this slice changes <i>who decides</i> a node's
        /// ceiling without changing what the ceiling is.</para>
        ///
        /// <para><b>Deliberately not derived from measured richness yet.</b> <see
        /// cref="DepositEntry.Richness"/> is the honest input for a mineable node's value and it is in
        /// arbitrary units, so the constant that saturates it can only be calibrated against a real
        /// survey distribution — which FAC-101's paced survey is only now starting to produce. Picking
        /// that constant blind would make every node either worthless or maximal. See
        /// docs/issues/FAC-102.</para></summary>
        /// <summary>The free-mint multiplier for one channel at one node — <see cref="ItemKind"/>
        /// magnitude read from the world instead of from a label.
        ///
        /// <para><b>Ore and RareOre are gated on the deposit.</b> A node mints ore only if there is ore
        /// under it. That gate is the whole of what the retired per-kind scale column expressed: its
        /// eight rows were 2.0 for the three mineable kinds and a trickle for the rest, which is a
        /// clumsy way of writing "has a deposit". How <i>much</i> is already the node's own
        /// <see cref="SpatialNode.ResourceValue"/>, and <i>which ores</i> is already the deposit.</para>
        ///
        /// <para>Every other channel passes the global scale through. Only <see cref="ItemKind.Ingot"/>
        /// reaches this today — the <c>Ingot:Stone</c> gravel trickle every node with rock produces.
        /// ProcessedGood, Hydrogen and Oxygen carried a per-kind scale and had <b>no entry in the yield
        /// table at all</b>, so nothing ever minted them: ProcessedGood is <see cref="ProductionManager"/>'s
        /// to make from inputs through a Manufactory, and Hydrogen/Oxygen have no producer anywhere
        /// (FAC-038). Those three scales were retired rather than re-keyed.</para></summary>
        public static double FreeMintScaleFor(SpatialNode n, ItemKind kind, FactionTuning tuning)
        {
            if (n == null || tuning == null) return 0;
            if ((kind == ItemKind.Ore || kind == ItemKind.RareOre) && !IsMineable(n)) return 0;
            return tuning.FreeMintScaleFor(kind);
        }

        /// <summary>How well this node suits <paramref name="cap"/>, 0..1 — asked of the ground and the
        /// site rather than of a label.
        ///
        /// <para>Conditions are tried in order of how specific they are and the first one the node
        /// satisfies wins: <c>IceDeposit</c>, <c>Mineable</c>, <c>HasIndustry</c>, <c>OnBody</c>,
        /// <c>Default</c>. A refinery wants ore beneath it, a fuel depot wants ice, a shipyard wants to
        /// sit where industry already is, a manufactory wants somewhere to stand; each capability's row
        /// names only the conditions it actually cares about.</para>
        ///
        /// <para><c>HasIndustry</c> is the condition that carries the retired table's real intent for
        /// a shipyard. That table scored the <c>Core</c> label 0.9 and every raw-ground label near
        /// zero, which was reaching for "somewhere already built up" — a property of what has been
        /// installed on a node, not of where the node is. Read from the capability set directly.</para>
        ///
        /// <para><b>On a body</b> is <see cref="SpatialNode.PlanetId"/> being set, which is exactly
        /// "inside some body's gravity well" and is re-derived every boot alongside
        /// <see cref="SpatialNode.Zone"/>. Read directly rather than through the zone label, which
        /// FAC-096 is retiring down to a separation key.</para></summary>
        public static double CapabilityAffinityFor(SpatialNode n, NodeCapabilityKind cap, FactionTuning tuning)
        {
            if (n == null || tuning == null) return DefaultAffinity;

            Dictionary<string, double> row;
            if (tuning.CapabilityAffinity == null
                || !tuning.CapabilityAffinity.TryGetValue(cap.ToString(), out row)
                || row == null)
                return DefaultAffinity;

            double v;
            if (IsIceDeposit(n, tuning) && row.TryGetValue("IceDeposit",  out v)) return v;
            if (IsMineable(n)           && row.TryGetValue("Mineable",    out v)) return v;
            if (HasIndustry(n)          && row.TryGetValue("HasIndustry", out v)) return v;
            if (n.PlanetId != null      && row.TryGetValue("OnBody",      out v)) return v;
            return row.TryGetValue("Default", out v) ? v : DefaultAffinity;
        }

        /// <summary>The affinity a node gets when nothing is configured for the capability. Neutral:
        /// an unconfigured capability must not read as either forbidden or ideal.</summary>
        public const double DefaultAffinity = 0.5;

        /// <summary>True once anything that refines or assembles stands on this node.</summary>
        private static bool HasIndustry(SpatialNode n)
        {
            if (n.Capabilities == null) return false;
            return Level(n, NodeCapabilityKind.Refinery)    > 0
                || Level(n, NodeCapabilityKind.RefineryII)  > 0
                || Level(n, NodeCapabilityKind.Manufactory) > 0
                || Level(n, NodeCapabilityKind.ManufactoryII) > 0
                || Level(n, NodeCapabilityKind.PrototechForge) > 0
                || Level(n, NodeCapabilityKind.PrototechRefinery) > 0;
        }

        private static int Level(SpatialNode n, NodeCapabilityKind cap)
        {
            int level;
            return n.Capabilities.TryGetValue(cap, out level) ? level : 0;
        }

        private static bool IsIceDeposit(SpatialNode n, FactionTuning tuning)
        {
            if (!IsMineable(n)) return false;
            string dominant = DepositModel.DominantOre(n.Deposit);
            return dominant != null
                && string.Equals(dominant, tuning.IceOreSubTypeId, StringComparison.Ordinal);
        }

        /// <summary>What a node is worth, 0..1 — the value the mint, Claim ordering, raid scoring and
        /// the Found gate all read.
        ///
        /// <para><b>Ore is proven, sites are inspected, and unlooked-at ground is neither.</b> A
        /// mineable node starts at <see cref="FactionTuning.UnsurveyedResourceValue"/> and rises
        /// toward what its survey measures, in proportion to how much of that survey is done —
        /// unsurveyed means unproven, so a node earns its worth rather than being dealt it. A node
        /// <i>sampled</i> and found to hold no ore has nothing left to survey: its worth is its
        /// ceiling, known by inspection and not a lottery. A node the seam never answered for has
        /// been neither proven nor inspected, and gets the unsurveyed floor — the same answer the
        /// mineable path already gives to "we have not looked".</para>
        ///
        /// <para>That third case is the one this got wrong. <see cref="IsMineable"/> is false for
        /// both an empty deposit and an absent one, so a declined node was handed the ceiling meant
        /// for proven-empty ground. Live on 2026-08-30 that was 85 nodes reading 0.800 — every one an
        /// unsampled point in the outer well of a body nothing can land on, no other node on the
        /// estate above 0.348, and <b>all 71 of that day's foundings</b> on one of them. See FAC-139.</para>
        ///
        /// <para><b>Worth is value per unit of ground, not per unit of ore.</b> A deposit's density and
        /// what its ore is worth both count, because a mining operation gets the product of the two.
        /// Judging on value alone could not separate anything: every SE ore costs several times iron,
        /// so measured live every node on the estate read maximally valuable. How that product is
        /// read back down to 0..1 is <see cref="ProvenShare"/>, which is compressive for a reason
        /// recorded there.</para>
        ///
        /// <para>With no price catalogue there is nothing to value a deposit against, so a surveyed
        /// mineable node converges on its ceiling instead. Knowing nothing about worth is not the same
        /// as knowing a deposit is poor, and the offline sim must not read every node as barren.</para>
        /// </summary>
        public static double ResourceValueFor(SpatialNode n, IPlanetCatalogue planets,
            FactionTuning tuning, IItemPriceCatalogue prices, IItemRegistry registry)
        {
            if (n == null) return 0.0;

            double target = ResourceValueTarget(n, planets, tuning, prices, registry);
            if (!IsMineable(n)) return target;

            double floor = ResourceValueFloor(n, planets, tuning);
            return MathUtil.Clamp01(floor + (target - floor) * SurveyConfidence(n, tuning));
        }

        /// <summary>What <see cref="ResourceValueFor"/> converges on once the survey is complete —
        /// the worth the ground has measured so far, before the confidence term drags it toward the
        /// floor. For a node with nothing to survey it is the value itself.
        ///
        /// <para>The same three cases as <see cref="ResourceValueFor"/>: an unsampled node reads the
        /// unsurveyed floor, a sampled node with no ore its cap, and a mineable node its cap times
        /// <see cref="ProvenShare"/> of what its ground is worth.</para></summary>
        public static double ResourceValueTarget(SpatialNode n, IPlanetCatalogue planets,
            FactionTuning tuning, IItemPriceCatalogue prices, IItemRegistry registry)
        {
            if (n == null) return 0.0;

            double cap = ResourceValueCap(n, planets, tuning);

            // Nobody has looked here. Not knowing is not the same as knowing there is nothing, and a
            // node in that state gets the same answer the mineable path gives it -- the unsurveyed
            // floor -- rather than the ceiling. Clamped by the cap, so a declined node in open space
            // still reads 0.
            if (IsUnsampled(n)) return ResourceValueFloor(n, planets, tuning);

            // Sampled, and the ground holds nothing to survey: what the site is worth is what it is
            // worth, and no amount of further looking changes it.
            if (!IsMineable(n)) return cap;

            double valuePerGround;
            if (!DepositModel.TryValuePerGround(n.Deposit, prices, registry, out valuePerGround)) return cap;

            double saturation = tuning != null ? tuning.ResourceValueSaturationPerProbe : 0;
            return cap * ProvenShare(valuePerGround, saturation);
        }

        private static double ResourceValueFloor(SpatialNode n, IPlanetCatalogue planets, FactionTuning tuning)
        {
            double cap = ResourceValueCap(n, planets, tuning);
            double floor = tuning != null ? tuning.UnsurveyedResourceValue : 0.2;
            return floor > cap ? cap : floor;
        }

        /// <summary>How far this node's survey has got, 0..1 — the one term that moves both
        /// <see cref="ResourceValueFor"/> and <see cref="RichnessFor"/> from their floors to what the
        /// ground measures. Zero for a node with no ore under it, which has nothing to survey; one
        /// with no tuning, matching what both readings assume then.</summary>
        public static double SurveyConfidence(SpatialNode n, FactionTuning tuning)
        {
            if (!IsMineable(n)) return 0.0;
            if (tuning == null) return 1.0;
            return MathUtil.Clamp01(SurveyMath.Confidence(n.Deposit.PointsExamined, tuning.SurveyTargetShare));
        }

        /// <summary>How much ore is under this node, 0..1 — what every yield and drill-rate path
        /// scales by. Price-blind, unlike <see cref="ResourceValueFor"/>.
        ///
        /// <para><b>Ore has to be there before it can be mined, so the deposit is the whole gate.</b>
        /// Ground with no deposit reads 0 and mints nothing: barren ground because the seam answered
        /// and the answer was nothing, unsampled ground because there is no measurement to stand a
        /// floor on. That is stricter than <see cref="ResourceValueFor"/>, which hands an unsampled
        /// node its floor — worth can be guessed at from the site, quantity cannot be guessed at at
        /// all. It also has to be stricter: <see cref="Economy.MintsNothing"/> reads this, and a
        /// floor granted to every deposit-less node in the graph would open a mint at each one.</para>
        ///
        /// <para><b>The survey drags it upward.</b> A node with a deposit but no measurement behind it
        /// starts at <see cref="FactionTuning.RichnessFloor"/> and converges on what its ground
        /// actually measures as probes accumulate — the same
        /// <see cref="SurveyMath.Confidence"/> term <see cref="ResourceValueFor"/> uses, so both
        /// readings advance off one survey rather than two.</para>
        ///
        /// <para><b>Linear against a saturation constant, because the input is one and a half decades
        /// wide.</b> <see cref="FactionTuning.RichnessSaturationMetresPerProbe"/> carries the measured
        /// distribution and the reason a log map — right for value-per-ground — is wrong here.</para>
        /// </summary>
        public static double RichnessFor(SpatialNode n, FactionTuning tuning)
        {
            if (n == null) return 0.0;

            // No ore under it, or nothing known to be: nothing to mine and nothing to scale a mint
            // by. Both cases read zero, deliberately -- see the summary.
            if (!IsMineable(n)) return 0.0;

            double cap = tuning != null ? tuning.RichnessCap : 1.0;
            double floor = tuning != null ? tuning.RichnessFloor : 0.2;
            if (floor > cap) floor = cap;

            double target = RichnessTarget(n, tuning);
            return MathUtil.Clamp01(floor + (target - floor) * SurveyConfidence(n, tuning));
        }

        /// <summary>What <see cref="RichnessFor"/> converges on once the survey is complete: the
        /// density the ground has measured so far against
        /// <see cref="FactionTuning.RichnessSaturationMetresPerProbe"/>, capped. Zero for a node with
        /// no ore under it.</summary>
        public static double RichnessTarget(SpatialNode n, FactionTuning tuning)
        {
            if (n == null || !IsMineable(n)) return 0.0;

            double cap = tuning != null ? tuning.RichnessCap : 1.0;
            double saturation = tuning != null ? tuning.RichnessSaturationMetresPerProbe : 0;
            if (saturation <= 0) return cap;

            double perProbe = n.Deposit.RichnessPerPoint;
            return perProbe <= 0 ? 0.0 : Math.Min(cap, perProbe / saturation);
        }

        /// <summary>How much of its ceiling a deposit's measured worth has earned, 0..1.
        ///
        /// <para><b>Compressive, because the input spans more than two orders of magnitude.</b>
        /// <see cref="DepositModel.TryValuePerGround"/> is density times ore-price ratio. Measured over
        /// the 789 sampled nodes of the live estate 2026-10-09 it runs 0.72 to 138.5, p50 16.2. Read
        /// linearly against the top of that range, the thin end lands near 0.005 and ordinary ground
        /// becomes unmineable, since <see cref="MineTargeting"/>, Claim ordering and raid scoring all
        /// read this number too. On a log scale it lands at p25 0.39 / p50 0.58 / p75 0.82, a
        /// distribution a threshold can cut. See FAC-117, FAC-139 and FAC-191.</para>
        ///
        /// <para><paramref name="saturation"/> is the value-per-ground at which a node reaches its
        /// cap. Zero or less disables the reading entirely and returns the full share: an unpriced
        /// world knows nothing about worth, which is not the same as knowing a deposit is poor.</para>
        /// </summary>
        private static double ProvenShare(double valuePerGround, double saturation)
        {
            if (saturation <= 0) return 1.0;
            if (valuePerGround <= 0) return 0.0;
            double span = Math.Log10(1.0 + saturation);
            if (span <= 0) return 1.0;
            return MathUtil.Clamp01(Math.Log10(1.0 + valuePerGround) / span);
        }

        public static double ResourceValueCap(SpatialNode n, IPlanetCatalogue planets, FactionTuning tuning)
        {
            if (n == null) return 0.0;
            if (IsMineable(n)) return tuning != null ? tuning.MineableResourceValueCap : 1.0;

            // A site with no ore is worth what it is as a place to stand, and that needs somewhere to
            // stand over. Terrain qualifies; so does orbit above terrain, which has a world below to
            // supply. The outer well of a body nothing can land on is neither -- it reads gravity and
            // is otherwise open space, so it is valued as open space.
            StationSite site = StationSiting.Classify(planets, n.Position);
            if (site == StationSite.Surface
                || (site == StationSite.Orbital && StationSiting.InLandableWell(planets, n.Position)))
                return tuning != null ? tuning.InhabitableResourceValueCap : 0.8;

            return 0.0;
        }
    }
}
