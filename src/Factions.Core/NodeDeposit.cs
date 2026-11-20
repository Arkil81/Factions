using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a <see cref="WorldModel.ResolveDeposit(SpatialNode, out DepositResolution)"/>
    /// call actually did. The backfill pass keys on this rather than on "does the node have a
    /// deposit", because a declined sample leaves the node looking exactly like one never tried.</summary>
    public enum DepositResolution
    {
        /// <summary>Not a mineable node, or nothing to do.</summary>
        NotApplicable = 0,
        /// <summary>The seam had no answer for this position — deep space outside every authored
        /// feature, or a host with no ore geography wired. Kind and deposit are untouched.</summary>
        Declined = 1,
        /// <summary>Sampled, and the ground holds something.</summary>
        Sampled = 2,
        /// <summary>Sampled, and the ground holds nothing. The node comes away holding an empty
        /// deposit rather than none, which is what stops it being a candidate again.</summary>
        Barren = 3,
    }

    /// <summary>One ore in a node's deposit, stored as what was <b>observed</b> rather than as a
    /// derived fraction.
    /// <para><see cref="Share"/> is computed on read from <see cref="Weight"/> against the deposit's
    /// total. That is what makes a deposit accumulable: a normalised fraction cannot absorb a new
    /// observation without the counts that produced it, so a surveyed deposit has to keep the counts.
    /// See FAC-101.</para></summary>
    public struct DepositEntry
    {
        /// <summary>Bare SE ore subtype, unprefixed ("Iron", "Ice").</summary>
        public string SubTypeId;

        /// <summary>Accumulated observation weight for this ore — the same yield-weighted quantity as
        /// <see cref="Richness"/>, summed over every survey step so far. Not normalised, so a later
        /// step is a plain addition, and carries no tuning factor, so the unit is stable across a
        /// retune. Shares, and so the mint split and the ore mix, are taken from it.</summary>
        public double Weight;

        /// <summary>Ore under the ground, in reference band-metres: hits times band thickness times
        /// the material's yield over <see cref="DepositModel.ReferenceYieldRatio"/>. Comparable between
        /// deposits, not an absolute rate. Accumulates alongside <see cref="Weight"/>.</summary>
        public double Richness;

        /// <summary>Rock the ore sits in, in plain band-metres: hits times band thickness, with no
        /// yield in it. What a drill has to cut, as against what it gets out; the ratio of the two is
        /// the material's yield (<see cref="NodeDeposit.TryYieldRatioOf"/>). Zero on an entry
        /// written before it was measured.</summary>
        public double Volume;

        public DepositEntry(string subTypeId, double weight, double richness)
            : this(subTypeId, weight, richness, 0.0)
        {
        }

        public DepositEntry(string subTypeId, double weight, double richness, double volume)
        {
            SubTypeId = subTypeId;
            Weight = weight;
            Richness = richness;
            Volume = volume;
        }
    }

    /// <summary>One ore of a deposit as <see cref="DepositModel.Breakdown"/> reports it.</summary>
    public struct OreBreakdown
    {
        /// <summary>Bare SE ore subtype, unprefixed ("Iron", "Ice").</summary>
        public string SubTypeId;
        /// <summary>This ore's share of the deposit, 0..1, by observation weight.</summary>
        public double Share;
        /// <summary>What a unit of this ore is worth in multiples of the world's ordinary ore; 0 when
        /// it or the baseline is unpriced.</summary>
        public double PriceRatio;
        /// <summary>This ore's share of the ground's value-per-ground, 0..1: share times price, over
        /// the deposit's total. 0 throughout on an unpriced world.</summary>
        public double WorthShare;
        /// <summary>True once <see cref="Share"/> clears the survey's noise floor.</summary>
        public bool Resolved;
    }

    /// <summary>What a node can actually mint, derived once from the world rather than looked up
    /// per node kind. Empty means barren — the node yields no ore at all, which is the normal
    /// outcome for most positions and is not an error. See docs/ore-geography.md.</summary>
    public sealed class NodeDeposit
    {
        public readonly List<DepositEntry> Entries = new List<DepositEntry>();

        /// <summary>Survey points examined so far, hits and misses alike, across every step. The
        /// whole resumable state of a survey: the next point to probe is derived from it, so a
        /// restart continues rather than restarting. See FAC-101.</summary>
        public long PointsExamined;

        /// <summary>Which definition of an entry's quantities this deposit was measured in.
        /// <para>A deposit is summed across restarts, so a change to what one observation means —
        /// the yield weighting and real layer thickness both were — cannot be applied to stored
        /// state: adding new observations onto old ones would mix units with nothing to say so.
        /// A deposit below <see cref="CurrentUnits"/> is re-surveyed instead
        /// (<see cref="WorldModel.TryResurveyStep"/>) and keeps serving its old readings until the
        /// new survey replaces it.</para></summary>
        public int Units = CurrentUnits;

        /// <summary>Units a deposit is measured in today. 0 is everything written before yield
        /// weighting: ratio-blind band-metres, surface layers at a nominal 8 m, scatter unseen.
        /// 1 counted every scatter spawn attempt as placed, where SE refuses one that overlaps a
        /// map already standing (<see cref="ScatterMath.PlacedShare"/>).</summary>
        public const int CurrentUnits = 2;

        /// <summary>True when this deposit was measured in older units and is due a re-survey.</summary>
        public bool IsStale { get { return Units < CurrentUnits; } }

        /// <summary>Smallest share this deposit can distinguish from noise at the survey's confidence
        /// target, given how many points have been examined. 1.0 before anything is surveyed.
        /// <para>Derived from the same relation the survey's stopping rule uses, so "how well is this
        /// node surveyed" and "may the survey stop" are one number rather than two that can
        /// disagree.</para></summary>
        public double ResolvedShareFloor
        {
            get { return SurveyMath.ShareFloorFor(PointsExamined); }
        }

        public bool IsBarren { get { return Entries.Count == 0; } }

        /// <summary>Sum of every entry's observation weight — the denominator <see cref="ShareOf"/>
        /// normalises against.</summary>
        public double TotalWeight
        {
            get
            {
                double total = 0;
                for (int i = 0; i < Entries.Count; i++) total += Entries[i].Weight;
                return total;
            }
        }

        /// <summary>Folds one survey step in: adds its observations to the running totals and
        /// advances <see cref="PointsExamined"/>. Ores not seen before are appended; a repeat ore
        /// accumulates. <b>Addition, not replacement</b> — which is the whole reason the entry stores
        /// weight rather than share.</summary>
        public void Accumulate(IEnumerable<DepositEntry> observations, long pointsExamined)
        {
            PointsExamined += Math.Max(0, pointsExamined);
            if (observations == null) return;

            foreach (DepositEntry obs in observations)
            {
                if (string.IsNullOrEmpty(obs.SubTypeId) || obs.Weight <= 0) continue;

                int found = -1;
                for (int i = 0; i < Entries.Count; i++)
                    if (string.Equals(Entries[i].SubTypeId, obs.SubTypeId, StringComparison.Ordinal))
                    { found = i; break; }

                if (found < 0) { Entries.Add(obs); continue; }

                DepositEntry e = Entries[found];
                e.Weight += obs.Weight;
                e.Richness += obs.Richness;
                e.Volume += obs.Volume;
                Entries[found] = e;
            }

            Entries.Sort(CompareByWeightDescending);
        }

        private static int CompareByWeightDescending(DepositEntry a, DepositEntry b)
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            // Ordinal tie-break so an equal-weight deposit names the same dominant ore every time --
            // a node's derived kind must not depend on accumulation order.
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.SubTypeId, b.SubTypeId);
        }

        /// <summary>Sum of every entry's richness — the deposit's overall quality, for comparing one
        /// deposit against another.</summary>
        public double TotalRichness
        {
            get
            {
                double total = 0;
                for (int i = 0; i < Entries.Count; i++) total += Entries[i].Richness;
                return total;
            }
        }

        /// <summary>Discard all but <paramref name="retainedFraction"/> of what has been learned here,
        /// as if that much of the survey had never been done.
        ///
        /// <para><b>Weights and points scale together, and that is the whole of it.</b> Shares are
        /// weights over their total and density is richness over points, so scaling every term by the
        /// same factor leaves both untouched and moves only <see cref="PointsExamined"/> — which is
        /// exactly what forgetting is: the same picture of the ground, held with less certainty.
        /// Cutting points alone would leave the richness numerator intact and make an abandoned node
        /// read as <i>denser</i> than it was, which is worse than the penalty not existing.</para>
        ///
        /// <para>The deposit itself is not touched in kind: what is under a node does not change
        /// because nobody is standing on it. An entry is never dropped, so a node keeps knowing which
        /// ores are present even at the floor.</para></summary>
        public void Forget(double retainedFraction)
        {
            if (retainedFraction >= 1.0) return;
            double keep = retainedFraction > 0.0 ? retainedFraction : 0.0;

            PointsExamined = (long)(PointsExamined * keep);
            for (int i = 0; i < Entries.Count; i++)
            {
                DepositEntry e = Entries[i];
                e.Weight *= keep;
                e.Richness *= keep;
                e.Volume *= keep;
                Entries[i] = e;
            }
        }

        /// <summary>Ore-band metres found per probe examined — the deposit's <b>density</b>, and the
        /// only signal it carries about <i>how much</i> ore is under a node rather than <i>what kind</i>.
        /// <para>A rate, not a total. <see cref="TotalRichness"/> accumulates with survey effort, so
        /// comparing two nodes on it measures how long each was looked at; dividing by
        /// <see cref="PointsExamined"/> is what turns it into a property of the ground. It is also
        /// invariant to probe spacing and catchment size, both of which cancel in the ratio, so two
        /// bodies sampled at different separations stay comparable.</para></summary>
        public double RichnessPerPoint
        {
            get { return PointsExamined > 0 ? TotalRichness / PointsExamined : 0.0; }
        }

        /// <summary>Rock band-metres per probe, with no yield in it — how much of the ground a drill
        /// cuts through is ore-bearing at all. An entry with no measured
        /// <see cref="DepositEntry.Volume"/> (written before it existed) counts its richness, which
        /// is what that entry's richness was.</summary>
        public double VolumePerPoint
        {
            get
            {
                if (PointsExamined <= 0) return 0.0;
                double total = 0;
                for (int i = 0; i < Entries.Count; i++)
                    total += Entries[i].Volume > 0 ? Entries[i].Volume : Entries[i].Richness;
                return total / PointsExamined;
            }
        }

        /// <summary>Rock band-metres behind each band-metre of richness: how much has to be cut for
        /// the ore <see cref="SpatialNode.Richness"/> counts. <see cref="DepositModel.ReferenceYieldRatio"/>
        /// over the deposit's mean yield, so 1 for ground at the reference yield and for anything
        /// measured before volume was, 3.9 for 1x silver, 0.44 for 8.8x dolomite.</summary>
        public double RockPerRichness
        {
            get
            {
                double rock = 0, richness = 0;
                for (int i = 0; i < Entries.Count; i++)
                {
                    rock += Entries[i].Volume > 0 ? Entries[i].Volume : Entries[i].Richness;
                    richness += Entries[i].Richness;
                }
                return richness > 0 ? rock / richness : 1.0;
            }
        }

        /// <summary>Ore a cubic metre of <paramref name="subTypeId"/>'s rock gives up here, as
        /// measured: the yield of the materials it was actually found in, averaged by volume. False
        /// when the ore is absent or its volume was never measured, so the caller falls back to a
        /// table instead of reading 1.</summary>
        public bool TryYieldRatioOf(string subTypeId, out double ratio)
        {
            ratio = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                DepositEntry e = Entries[i];
                if (!string.Equals(e.SubTypeId, subTypeId, StringComparison.Ordinal)) continue;
                if (e.Volume <= 0) return false;
                ratio = e.Richness / e.Volume * DepositModel.ReferenceYieldRatio;
                return true;
            }
            return false;
        }

        /// <summary>Share of this deposit that is <paramref name="subTypeId"/>; 0 when absent.
        /// Derived from the accumulated weights, never stored.</summary>
        public double ShareOf(string subTypeId)
        {
            double total = TotalWeight;
            if (total <= 0) return 0;
            for (int i = 0; i < Entries.Count; i++)
                if (string.Equals(Entries[i].SubTypeId, subTypeId, StringComparison.Ordinal))
                    return Entries[i].Weight / total;
            return 0;
        }
    }

    /// <summary>
    /// Turns a raw <see cref="OreSample"/> into a <see cref="NodeDeposit"/>.
    /// <para>The split is deliberate: <see cref="IOreGeography"/> measures what is in the ground and
    /// this decides what that means. Every policy choice — how a node kind leans, how richness is
    /// computed, what counts as too thin to bother with — lives here, in Core, SE-free and
    /// unit-testable. See docs/ore-geography.md.</para>
    /// </summary>
    public static class DepositModel
    {
        /// <summary>Ore bands thinner than this are dropped. SE does not generate continuous voxels
        /// for a band this thin, so it appears in the ore map and cannot be mined — including it
        /// would advertise ore no player can reach, which is the defect this whole model exists to
        /// close. Matches the threshold Ore Detector Reforged applies for the same reason.</summary>
        public const double MinBandDepthM = 1.5;

        /// <summary>
        /// Derive a node's deposit from what was sampled around it.
        /// <para>This is a <b>measurement</b> and carries no policy. What a place is worth, how well
        /// it yields, and what it is called are all decided downstream from this result — so a
        /// preference can be retuned without invalidating deposits already accumulated on disk. A
        /// bias applied here would be baked into stored state: a survey accumulates across restarts,
        /// so a later retune would sum new observations onto old ones in different units, with
        /// nothing to report that it had.</para>
        /// <para>A sample that found nothing still returns a deposit, carrying the points it
        /// examined. <b>Barren and unsampled are different answers</b> — <see cref="IOreGeography"/>
        /// is explicit that an empty <c>Ores</c> with a nonzero <c>PointsExamined</c> is a real
        /// measurement — and the count is the only thing that distinguishes them once the entries
        /// are empty either way. A null sample is the one case with nothing to record.</para>
        /// <para>Every call returns a deposit of its own, never a shared one: a deposit is mutated
        /// in place by <see cref="Accumulate"/> and <see cref="Forget"/>, so one instance behind
        /// several nodes would let either reach all of them. Never returns null.</para>
        /// </summary>
        public static NodeDeposit Derive(OreSample sample)
        {
            if (sample == null) return new NodeDeposit();
            if (sample.Ores.Count == 0) return new NodeDeposit { PointsExamined = sample.PointsExamined };

            var deposit = new NodeDeposit();
            double totalWeight = 0;

            for (int i = 0; i < sample.Ores.Count; i++)
            {
                OreObservation obs = sample.Ores[i];
                if (string.IsNullOrEmpty(obs.SubTypeId)) continue;
                if (obs.Hits <= 0) continue;
                if (obs.Source != OreSource.Scatter && obs.BandDepthM < MinBandDepthM) continue;

                // Volume is area covered times band thickness: how much ore-bearing rock per unit of
                // ground, which is what makes a wide thick seam genuinely better than a narrow one.
                double volume = obs.Hits * obs.BandDepthM;
                if (volume <= 0) continue;
                // Richness is what that rock yields, on the reference scale. A reader that cannot
                // name the material's yield reports 0, which reads as the reference yield and leaves
                // the volume unmeasured, so the drill rate takes its ratio from the table instead.
                bool yieldKnown = obs.YieldRatio > 0;
                double richness = yieldKnown ? volume * obs.YieldRatio / ReferenceYieldRatio : volume;
                double measuredVolume = yieldKnown ? volume : 0.0;

                // Weight and richness are the same observation here; they diverge only once a
                // survey accumulates several steps. Stored raw -- shares are derived on read, which
                // is what lets a later step simply add to this. One entry per ore: several bands or
                // layers of one ore are one ore to everything that reads the deposit.
                AddTo(deposit.Entries, new DepositEntry(obs.SubTypeId, richness, richness, measuredVolume));
                totalWeight += richness;
            }

            deposit.PointsExamined = sample.PointsExamined;

            // Every band too thin to count is the same answer as no band at all -- looked, found
            // nothing -- so it keeps its point count rather than reading as unsampled.
            if (deposit.Entries.Count == 0 || totalWeight <= 0)
            {
                deposit.Entries.Clear();
                return deposit;
            }

            deposit.Entries.Sort(CompareByRichnessDescending);
            return deposit;
        }

        /// <summary>The <c>MinedOreRatio</c> a stored richness of 1 band-metre stands for — the unit
        /// <see cref="DepositEntry.Richness"/> is measured in.
        ///
        /// <para><b>A unit, not a tunable.</b> Dividing by it keeps richness on the band-metre scale
        /// <see cref="FactionTuning.RichnessSaturationMetresPerProbe"/> and
        /// <see cref="FactionTuning.ResourceValueSaturationPerProbe"/> were calibrated on, so
        /// weighting by yield redistributes ore between nodes instead of multiplying it. 3.9 is the volume-weighted mean ratio of the live estate's ore, measured
        /// 2026-10-17 over 332 surveyed nodes. Because it is baked into stored deposits, changing it
        /// is a change of units and needs a <see cref="NodeDeposit.CurrentUnits"/> bump, not a
        /// retune.</para></summary>
        public const double ReferenceYieldRatio = 3.9;

        private static void AddTo(List<DepositEntry> entries, DepositEntry add)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (!string.Equals(entries[i].SubTypeId, add.SubTypeId, StringComparison.Ordinal)) continue;
                DepositEntry e = entries[i];
                e.Weight += add.Weight;
                e.Richness += add.Richness;
                e.Volume += add.Volume;
                entries[i] = e;
                return;
            }
            entries.Add(add);
        }

        /// <summary>The ore that dominates this deposit by share, or null when barren. This is what
        /// a node's kind label is derived from — the deposit names the node, rather than the node
        /// kind dictating the deposit.</summary>
        public static string DominantOre(NodeDeposit deposit)
        {
            if (deposit == null || deposit.IsBarren) return null;
            return deposit.Entries[0].SubTypeId; // Derive sorts richest first
        }

        /// <summary>
        /// Whether this deposit is <b>rare</b> — worth enough per unit, relative to the world's
        /// ordinary ore, to count as exceptional.
        ///
        /// <para>Rarity is <b>continuous and world-derived</b>: a deposit is rare when it is worth
        /// enough per unit relative to the baseline ore, not when its ore appears on a curated list.
        /// A body carrying enormous platinum reserves prices platinum lower, so its deposits stop
        /// reading as exceptional — which is exactly what a global boolean could not express.</para>
        ///
        /// <para>False when there is no price data: an unpriced world should read every deposit as
        /// ordinary rather than as rare.</para>
        /// </summary>
        public static bool IsRare(NodeDeposit deposit, FactionTuning tuning,
            IItemPriceCatalogue prices, IItemRegistry registry)
        {
            if (tuning == null) return false;

            double ratio;
            if (!TryValueRatio(deposit, prices, registry, out ratio)) return false;

            return ratio >= tuning.RareDepositValueRatio;
        }

        /// <summary>Value of the ore under a unit of this node's ground: its density times what that
        /// ore is worth, in multiples of the world's ordinary ore.
        ///
        /// <para><b>The one axis a node's worth is judged on.</b> Value ratio alone cannot separate
        /// deposits here — SE's ores span only 1x to 3.5x iron, and measured live (on the ingot
        /// reading, which inflated that span) every node read maximally valuable. Density alone cannot
        /// either: a thick seam of cheap ore is not a prize. Their product is what a mining operation
        /// actually gets per unit of ground worked, so a broad common deposit and a thin rare one
        /// compare on the same scale instead of on two.</para>
        ///
        /// <para>False when there is nothing to value it against, or nothing has been examined yet —
        /// which is different from a deposit being poor.</para></summary>
        public static bool TryValuePerGround(NodeDeposit deposit, IItemPriceCatalogue prices,
            IItemRegistry registry, out double valuePerGround)
        {
            valuePerGround = 0;
            if (deposit == null || deposit.IsBarren || prices == null) return false;
            if (deposit.PointsExamined <= 0) return false;

            long baseline;
            if (!TryBaselinePrice(prices, registry, out baseline)) return false;

            double total = 0;
            for (int i = 0; i < deposit.Entries.Count; i++)
                total += ValuePerGroundOf(deposit, deposit.Entries[i], prices, baseline);

            valuePerGround = total;
            return true;
        }

        /// <summary>One ore's term in <see cref="TryValuePerGround"/>: its density times its price
        /// ratio. Zero for an unpriced ore, which the sum skips the same way.</summary>
        private static double ValuePerGroundOf(NodeDeposit deposit, DepositEntry e,
            IItemPriceCatalogue prices, long baseline)
        {
            long price = OrePrice(prices, e.SubTypeId);
            if (price <= 0 || deposit.PointsExamined <= 0) return 0.0;
            return (e.Richness / deposit.PointsExamined) * (price / (double)baseline);
        }

        /// <summary>
        /// The deposit ore by ore, as a player is shown it: each ore's share, what it is worth
        /// against the world's ordinary ore, and how much of the ground's worth comes from it.
        ///
        /// <para><b>The worth column is <see cref="TryValuePerGround"/>'s own sum, split.</b> Each row
        /// carries its term over the total, so the rows read back to the one number a node's worth is
        /// derived from rather than to a second estimate of it. Zero throughout on an unpriced world,
        /// which knows nothing about worth.</para>
        ///
        /// <para>A share is <see cref="OreBreakdown.Resolved"/> once it is at least
        /// <see cref="NodeDeposit.ResolvedShareFloor"/>, the survey's own noise floor: below it the
        /// ore is known to be present, and its share is not yet known.</para>
        ///
        /// <para>Heaviest first, the order <see cref="NodeDeposit.Accumulate"/> keeps. Empty for a
        /// null or barren deposit.</para>
        /// </summary>
        public static List<OreBreakdown> Breakdown(NodeDeposit deposit, IItemPriceCatalogue prices,
            IItemRegistry registry)
        {
            var rows = new List<OreBreakdown>();
            if (deposit == null || deposit.IsBarren) return rows;

            long baseline = 0;
            bool priced = prices != null && TryBaselinePrice(prices, registry, out baseline);
            double floor = deposit.ResolvedShareFloor;

            double totalWorth = 0;
            var worth = new double[deposit.Entries.Count];
            for (int i = 0; i < deposit.Entries.Count; i++)
            {
                worth[i] = priced ? ValuePerGroundOf(deposit, deposit.Entries[i], prices, baseline) : 0.0;
                totalWorth += worth[i];
            }

            for (int i = 0; i < deposit.Entries.Count; i++)
            {
                DepositEntry e = deposit.Entries[i];
                double share = deposit.ShareOf(e.SubTypeId);
                long price = priced ? OrePrice(prices, e.SubTypeId) : 0;
                rows.Add(new OreBreakdown
                {
                    SubTypeId = e.SubTypeId,
                    Share = share,
                    PriceRatio = price > 0 ? price / (double)baseline : 0.0,
                    WorthShare = totalWorth > 0 ? worth[i] / totalWorth : 0.0,
                    Resolved = share >= floor,
                });
            }
            return rows;
        }

        /// <summary>What the world's ordinary ore costs, as the baseline every price ratio here is
        /// taken against. False when the registry names none or it is unpriced.</summary>
        private static bool TryBaselinePrice(IItemPriceCatalogue prices, IItemRegistry registry, out long baseline)
        {
            baseline = 0;
            string baselineSubType = registry != null ? registry.RepresentativeSubType(ItemKind.Ore) : null;
            if (string.IsNullOrEmpty(baselineSubType)) return false;

            baseline = OrePrice(prices, baselineSubType);
            return baseline > 0;
        }

        /// <summary>How much a unit of this deposit is worth relative to the world's ordinary ore:
        /// the share-weighted price of its mix over the baseline ore's price.
        /// <para>The one place a deposit is valued, so the rarity label and a node's worth cannot
        /// drift into disagreeing about the same ground. Baseline is whatever the world says its
        /// ordinary ore costs, not a named ore: on a modded world the representative may not be iron,
        /// and hardcoding one would put the curated list back in a different costume.</para>
        /// <para>False when there is nothing to value it against — an unpriced world knows nothing
        /// about worth, which is different from knowing a deposit is poor.</para></summary>
        public static bool TryValueRatio(NodeDeposit deposit, IItemPriceCatalogue prices,
            IItemRegistry registry, out double ratio)
        {
            ratio = 0;
            if (deposit == null || deposit.IsBarren || prices == null) return false;

            long baseline;
            if (!TryBaselinePrice(prices, registry, out baseline)) return false;

            double valuePerUnit = 0;
            for (int i = 0; i < deposit.Entries.Count; i++)
            {
                DepositEntry e = deposit.Entries[i];
                long price = OrePrice(prices, e.SubTypeId);
                if (price <= 0) continue;
                valuePerUnit += deposit.ShareOf(e.SubTypeId) * price;
            }

            ratio = valuePerUnit / baseline;
            return true;
        }

        /// <summary>What a unit of <paramref name="subTypeId"/> is worth as it comes out of the ground.
        /// <para><b>The ore, not the subtype.</b> A deposit's subtypes are bare (<c>Platinum</c>), and a
        /// bare key misses the exact price table and reads the collapsed one, which keeps the highest
        /// price any item of that name carries -- the ingot. SE refines platinum at 0.005 ingot per
        /// ore, so that read valued a platinum deposit at 490x iron where its ore is worth 3.5x, while
        /// the mint, cargo and every store priced the same ore as ore. Measured live 2026-10-09: all
        /// 789 sampled nodes' persisted worth matched the ingot reading exactly. See
        /// docs/issues/FAC-191.</para></summary>
        private static long OrePrice(IItemPriceCatalogue prices, string subTypeId)
        {
            return prices.BasePrice(WorldModel.ApplyInvKeyPrefix(ItemKind.Ore, subTypeId));
        }

        private static int CompareByRichnessDescending(DepositEntry a, DepositEntry b)
        {
            int byRichness = b.Richness.CompareTo(a.Richness);
            // Ordinal tie-break so an equal-richness deposit derives the same label every time --
            // DominantOre reads entry 0, and a node's kind must not depend on sample iteration order.
            return byRichness != 0 ? byRichness : string.CompareOrdinal(a.SubTypeId, b.SubTypeId);
        }
    }
}
