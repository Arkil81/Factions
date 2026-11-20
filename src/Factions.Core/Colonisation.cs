using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One region that yields a wanted material, with the numbers a colonisation decision
    /// needs. Supplied by <see cref="IRegionMaterialIndex"/>; this layer never derives geometry or
    /// occupancy itself.</summary>
    public sealed class ColonisationCandidate
    {
        /// <summary>The resolved region — a body's surface or orbit, or an authored feature.</summary>
        public PlacementRegion Region;

        public PlacementRegionGeometry Geometry;

        /// <summary>Separation in force here, already resolved per body or per feature.</summary>
        public double SeparationKm;

        /// <summary>How much of what this region yields is the wanted material, 0..1. A region where
        /// platinum is one band of nine is a worse answer than one made of it.</summary>
        public double Share;

        /// <summary>Nodes already sited here, **any owner**. Capacity is a property of the region, not
        /// of a faction — a body full of someone else's nodes is just as full.</summary>
        public int OccupiedNodes;

        /// <summary>When set, occupancy is read from this shared entry rather than this one's own
        /// count.
        /// <para>A region that yields several materials produces one candidate per material, because
        /// <see cref="Share"/> belongs to the (region, material) pair. Those siblings describe **one**
        /// region and must not each count the same nodes — so they point at a single entry that owns
        /// the count.</para></summary>
        public ColonisationCandidate Occupancy;

        /// <summary>Nodes sited in this region, following <see cref="Occupancy"/> when it is set.</summary>
        public int EffectiveOccupied()
        {
            return Occupancy != null ? Occupancy.OccupiedNodes : OccupiedNodes;
        }

        /// <summary>Km from the deciding faction's territory. Real cost: transit, and interception
        /// exposure on every leg thereafter.</summary>
        public double DistanceKm;

        /// <summary>Room left, in nodes. Fractional because capacity is an area estimate, not a count
        /// — see <see cref="PlacementCapacity"/>.</summary>
        public double Remaining()
        {
            double max = PlacementCapacity.MaxNodes(Region == null ? PlacementRegionKind.DeepSpace : Region.Kind,
                                                    Geometry, SeparationKm);
            double left = max - EffectiveOccupied();
            return left > 0 ? left : 0;
        }
    }

    /// <summary>
    /// Which regions yield what. Core cannot answer this itself: planet ore comes from SE's generator
    /// definitions and feature ore from the authored world files, so the host supplies the index and
    /// this layer only decides against it.
    ///
    /// <para>Deliberately region-keyed rather than position-keyed. <see cref="IOreGeography"/> samples
    /// a point and answers "what is under here", which is the right question for a node that already
    /// exists and the wrong one for choosing where to go.</para>
    /// </summary>
    public interface IRegionMaterialIndex
    {
        /// <summary>Regions that yield <paramref name="subTypeId"/>, in no particular order.</summary>
        IEnumerable<ColonisationCandidate> RegionsYielding(string subTypeId);

        /// <summary>Regions worth diverting to when <paramref name="full"/> has no room, nearest first.
        /// <para>The same body's orbit is the canonical answer for a full surface: it is as close as a
        /// region can be to another, and it is the one that puts a faction's influence within reach of
        /// the nodes it could not join.</para></summary>
        IEnumerable<ColonisationCandidate> RegionsNear(PlacementRegion full);
    }

    /// <summary>Thresholds for the make-versus-buy comparison. Passed in rather than read from
    /// <see cref="FactionTuning"/> so this slice adds no tuning field and therefore needs no
    /// <c>TuneVersion</c> migration — a new default can never beat a persisted tuning.json.</summary>
    public sealed class ColonisationRules
    {
        /// <summary>Capital cost of establishing the first node in a new region.</summary>
        public double ColoniseCost = 100000;

        /// <summary>How many economy passes of sustained acquisition cost to weigh against the capital
        /// cost. Buying is only "expensive" relative to a horizon; without one, every shortage looks
        /// like a reason to move.</summary>
        public double HorizonPasses = 30.0;

        /// <summary>Km at which distance has halved a candidate's appeal. Distance is a weighting, not
        /// a cutoff — the same shape Mine's richness-versus-distance rule uses.</summary>
        public double DistanceHalfValueKm = 3000.0;

        /// <summary>A region must have at least this much room to be a primary target. Below it the
        /// planner diverts, because placement does not refuse an over-subscribed region — it retries
        /// and then sites the node anyway (FAC-105).</summary>
        public double MinRoomNodes = 1.0;
    }

    /// <summary>What a faction has decided to do, and why. **Carries no position**: turning a region
    /// into a coordinate is placement, and placement is being rebuilt (FAC-102).</summary>
    public sealed class ColonisationIntent
    {
        /// <summary>The material that drove this. The whole decision is downstream of one scarce thing.</summary>
        public string SubTypeId;

        /// <summary>Where to go.</summary>
        public ColonisationCandidate Target;

        /// <summary>True when <see cref="Target"/> is not the best source but a foothold near it,
        /// because the best source had no room.</summary>
        public bool IsBeachhead;

        /// <summary>The region this was diverted from, when <see cref="IsBeachhead"/>. **The objective,
        /// not a footnote** — a beachhead exists to become reach onto the region it names, through
        /// Raid and then Conquer, so whatever consumes this must carry it forward rather than treat
        /// the outpost as the goal.</summary>
        public PlacementRegion DivertedFrom;

        /// <summary>Projected cost of continuing to buy over <see cref="ColonisationRules.HorizonPasses"/>.</summary>
        public double SustainedCost;

        /// <summary>Sustained cost less the capital cost. Positive is what makes going worth it.</summary>
        public double Advantage;

        /// <summary>One line naming the comparison, for the decision log. An intent that cannot say
        /// why it fired is indistinguishable from a threshold that drifted.</summary>
        public string Why;
    }

    /// <summary>
    /// Decides whether a faction should establish itself somewhere new, and where — the trigger being
    /// **strategic, driven by economics**: *"I need platinum but it's expensive"* (operator,
    /// 2026-08-26, FAC-106).
    ///
    /// <para>A make-versus-buy comparison, not a territorial urge. The sustained cost of acquiring a
    /// material is weighed against the capital cost of going to where it comes from, over a horizon;
    /// a faction colonises because buying became the expensive option and stops wanting to once it
    /// has.</para>
    ///
    /// <para><b>The material geography does the gating, not a threshold.</b> A faction short of iron
    /// never colonises — iron is on every body, so it is cheap, so buying always wins. Platinum is
    /// expensive precisely because it is one planet plus one ring band. Nothing here needs tuning to
    /// produce that asymmetry; the world produces it.</para>
    ///
    /// <para><b>This class decides, and does not act.</b> It returns a region and a reason. Resolving
    /// a region to a position is placement, which is being rebuilt under FAC-102, and a decision layer
    /// that reached into it would have to be rewritten with it.</para>
    /// </summary>
    public static class ColonisationPlanner
    {
        /// <summary>
        /// The strongest colonisation case this faction has, or null when buying still wins everywhere.
        ///
        /// <para><paramref name="sustainedCostByItem"/> is the smoothed per-pass cost of acquiring each
        /// item the faction has a standing need for — smoothed deliberately, because a one-tick
        /// shortage is a haulage problem and only a standing expensive need is a geography one.</para>
        /// </summary>
        public static ColonisationIntent Evaluate(
            IDictionary<string, double> sustainedCostByItem,
            IRegionMaterialIndex index,
            ColonisationRules rules)
        {
            if (sustainedCostByItem == null || index == null) return null;
            if (rules == null) rules = new ColonisationRules();

            ColonisationIntent best = null;

            foreach (KeyValuePair<string, double> kv in sustainedCostByItem)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;

                double sustained = kv.Value * (rules.HorizonPasses > 0 ? rules.HorizonPasses : 1.0);
                double advantage = sustained - rules.ColoniseCost;
                if (advantage <= 0) continue;   // buying is still cheaper over the horizon

                ColonisationIntent intent = BestRegionFor(kv.Key, index, rules);
                if (intent == null) continue;

                intent.SustainedCost = sustained;
                intent.Advantage = advantage;
                intent.Why = kv.Key + ": buying costs " + Round(sustained) + " over "
                           + Round(rules.HorizonPasses) + " passes against a "
                           + Round(rules.ColoniseCost) + " move"
                           + (intent.IsBeachhead && intent.DivertedFrom != null
                                ? "; " + intent.DivertedFrom + " is full, staging at " + intent.Target.Region
                                : "; " + intent.Target.Region);

                if (best == null || intent.Advantage > best.Advantage) best = intent;
            }

            return best;
        }

        /// <summary>The best region yielding one material — richest first, discounted by distance, and
        /// diverted to a foothold when the richest has no room.</summary>
        private static ColonisationIntent BestRegionFor(string subTypeId, IRegionMaterialIndex index,
                                                        ColonisationRules rules)
        {
            ColonisationCandidate bestOpen = null, bestFull = null;
            double bestOpenScore = 0, bestFullScore = 0;

            foreach (ColonisationCandidate c in index.RegionsYielding(subTypeId))
            {
                if (c == null || c.Region == null || c.Share <= 0) continue;
                double score = Appeal(c, rules);
                if (score <= 0) continue;

                if (c.Remaining() >= rules.MinRoomNodes)
                {
                    if (bestOpen == null || score > bestOpenScore) { bestOpen = c; bestOpenScore = score; }
                }
                else if (bestFull == null || score > bestFullScore) { bestFull = c; bestFullScore = score; }
            }

            // A region with room always beats staging next to one without, even a richer one: the
            // beachhead still has to fight its way in, and that cost is not modelled here.
            if (bestOpen != null)
                return new ColonisationIntent { SubTypeId = subTypeId, Target = bestOpen };

            if (bestFull == null) return null;

            ColonisationCandidate foothold = NearestWithRoom(index, bestFull.Region, rules);
            if (foothold == null) return null;

            return new ColonisationIntent
            {
                SubTypeId = subTypeId,
                Target = foothold,
                IsBeachhead = true,
                DivertedFrom = bestFull.Region
            };
        }

        /// <summary>Richness discounted by distance — a weighting, never a cutoff, so a rich distant
        /// source can still beat a poor near one.</summary>
        private static double Appeal(ColonisationCandidate c, ColonisationRules rules)
        {
            double half = rules.DistanceHalfValueKm > 0 ? rules.DistanceHalfValueKm : 1.0;
            double d = c.DistanceKm > 0 ? c.DistanceKm : 0;
            return c.Share * (half / (half + d));
        }

        private static ColonisationCandidate NearestWithRoom(IRegionMaterialIndex index, PlacementRegion full,
                                                             ColonisationRules rules)
        {
            foreach (ColonisationCandidate c in index.RegionsNear(full))
                if (c != null && c.Region != null && c.Remaining() >= rules.MinRoomNodes)
                    return c;   // the index yields nearest first
            return null;
        }

        private static string Round(double v)
        {
            return v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
