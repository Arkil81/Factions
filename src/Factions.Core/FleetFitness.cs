using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What one faction's jobs of a given role have actually turned out to require.
    ///
    /// <para><b>This records the JOB, never the hull that served it.</b> A statistic about our own
    /// choices is self-confirming — a faction owning only small drones would observe "small was
    /// enough" forever and keep buying them — so the size evidence is one TRIP: a matched vector's
    /// <see cref="MaterialVector.Quantity"/>, which <c>MatchVectors</c> sets to the smaller of the
    /// source's surplus and the want before any hull is chosen. See docs/fleet-fitness-plan.md's
    /// design-agnostic split: a value computable for an option never taken may drive selection; one
    /// observable only for choices already made may only calibrate a model.</para>
    ///
    /// <para><b>A trip, not the want it serves.</b> The destination's whole shortfall was recorded
    /// first, and it is not a hull size: a want is bounded by demand sizing and a trip by what one
    /// source can spare. Measured 2026-10-09, nine factions held 15–59% of their weight at or above
    /// 4,194,304 L, so p90 sat in the top bucket and every Trade build was clamped to the largest
    /// flyable hull, while FCTM's real trips ran p50 224 L / p90 2,384 L (FAC-237). See
    /// <see cref="FleetFitness.TripLiters"/>.</para>
    ///
    /// <para><b>Size is a distribution, not a mean.</b> A mean job size hides the tail that sets the
    /// useful hull size — FAC-232's live window read mean fill 0.377 against median 0.050 on the same
    /// 217 convoys. <see cref="Buckets"/> is a log-scale histogram so a high percentile is readable
    /// from a fixed 12 integers, which also keeps the brain file lean (CLAUDE.md rule 4) and the
    /// collection bounded by construction rather than by the catalogue's size.</para></summary>
    public sealed class RoleRequirement
    {
        /// <summary>Number of size buckets. Bucket i covers [4^i, 4^(i+1)) litres, so the 12 of them
        /// span 1 L to ~16.8 ML — past the largest hull in the live catalogue (375,000 L) with room
        /// for a modded one, and small enough to serialise as one short list.</summary>
        public const int BucketCount = 12;

        /// <summary>Observed job sizes in litres, log4-bucketed. Doubles rather than ints because
        /// <see cref="Decay"/> scales them geometrically; a counter that can only be incremented
        /// would need a separate ageing mechanism.</summary>
        public readonly double[] Buckets = new double[BucketCount];

        /// <summary>Saturation: the share of demanded quantity still unmet, exponentially smoothed.
        /// Distinct from size and NOT derivable from it — small jobs with a flat backlog mean the
        /// small hull is right, while small jobs with a growing backlog mean the network cannot keep
        /// up. Load size alone cannot tell those apart, which is why this is a second term.</summary>
        public double UnmetEma;

        /// <summary>Observations folded in since this record existed. A caller must treat a cold
        /// record as "no opinion" rather than as "no requirement" — see <see cref="IsWarm"/>.</summary>
        public long Samples;

        /// <summary>Whether there is enough evidence to be worth reading. Below this a caller keeps
        /// its existing behaviour; a fresh faction must still be able to bootstrap.</summary>
        public bool IsWarm { get { return Samples >= WarmSampleCount; } }

        public const long WarmSampleCount = 20;

        /// <summary>What an observation measures. Persisted beside the histogram so a record written
        /// under an earlier measure is discarded on load instead of read as evidence: geometric decay
        /// would take hundreds of Operations passes to fade it, and a saturated record looks exactly
        /// like a real requirement. 0 (absent) is the original whole-shortfall measure; 1 is one trip
        /// (<see cref="FleetFitness.TripLiters"/>).</summary>
        public const int CurrentBasis = 1;

        /// <summary>Least surviving histogram weight that still counts as evidence — one whole
        /// observation. See <see cref="SizeAtPercentile"/> for why a floor is needed at all.</summary>
        public const double ResidualEvidenceFloor = 1.0;

        /// <summary>Files one observed job size, in litres.</summary>
        public void Observe(double liters)
        {
            if (liters <= 0 || double.IsNaN(liters) || double.IsInfinity(liters)) return;
            Buckets[BucketFor(liters)] += 1.0;
            Samples++;
        }

        /// <summary>Folds one saturation reading: <paramref name="unmetFraction"/> is the share of the
        /// demanded quantity still outstanding, 0..1.</summary>
        public void ObserveUnmet(double unmetFraction, double emaPeriods)
        {
            if (double.IsNaN(unmetFraction) || emaPeriods <= 0) return;
            double clamped = MathUtil.Clamp01(unmetFraction);
            double alpha = 2.0 / (emaPeriods + 1.0);
            UnmetEma = Samples <= 0 ? clamped : UnmetEma + alpha * (clamped - UnmetEma);
        }

        /// <summary>Ages the histogram geometrically so a requirement tracks the present rather than
        /// accumulating forever. Applied on the same cadence the caller folds demand history on.</summary>
        public void Decay(double retain)
        {
            if (retain < 0 || retain >= 1.0) return;
            for (int i = 0; i < BucketCount; i++) Buckets[i] *= retain;
        }

        /// <summary>Litres at <paramref name="percentile"/> of observed job size, as the LOWER edge of
        /// the bucket that crosses it.
        /// <para>Lower edge, not midpoint, on purpose: this figure becomes a hull-size floor, and a
        /// floor that overstates the requirement filters out hulls that would in fact have served —
        /// the conservative error is the recoverable one.</para>
        /// <para>Returns 0 when there is nothing observed, which callers must read as "no opinion",
        /// never as "the requirement is zero".</para></summary>
        public double SizeAtPercentile(double percentile)
        {
            double total = 0;
            for (int i = 0; i < BucketCount; i++) total += Buckets[i];
            // Geometric decay never reaches zero, so without an explicit floor a faction that stopped
            // hauling days ago would keep reporting the size its traffic used to need -- and a stale
            // requirement is worse than none, because it looks like evidence. Below one whole
            // observation's worth of surviving weight this is "no recent evidence", not "small jobs".
            if (total < ResidualEvidenceFloor) return 0;

            double target = total * MathUtil.Clamp01(percentile);
            double running = 0;
            for (int i = 0; i < BucketCount; i++)
            {
                running += Buckets[i];
                if (running >= target) return LowerEdge(i);
            }
            return LowerEdge(BucketCount - 1);
        }

        internal static int BucketFor(double liters)
        {
            int i = 0;
            double edge = 4.0;
            while (i < BucketCount - 1 && liters >= edge) { edge *= 4.0; i++; }
            return i;
        }

        internal static double LowerEdge(int bucket)
        {
            double edge = 1.0;
            for (int i = 0; i < bucket; i++) edge *= 4.0;
            return edge;
        }
    }

    /// <summary>Why a candidate hull did or did not suit a requirement, and how expensive it is once
    /// it does. Returned rather than reduced to one number so the decision can be logged and read —
    /// the point of the whole design is knowing WHY a hull was chosen, not merely choosing one.
    /// <para>Converging on the same hull cheapest-first already picks is a correct outcome for a
    /// faction whose jobs really are small; the defect being fixed is choosing without a requirement
    /// to clear, not choosing cheaply.</para></summary>
    public struct FitnessVerdict
    {
        /// <summary>Whether the candidate meets the requirement at all.</summary>
        public bool Eligible;
        /// <summary>Build cost, the ranking key AMONG eligible candidates only. Buildability never
        /// competes with the requirement — "the easiest solution THAT SOLVES THE PROBLEM" — so this
        /// is deliberately not blended into a single score with the eligibility terms.</summary>
        public long BuildCost;
        /// <summary>Candidate's cargo volume, carried for the log line.</summary>
        public long CargoLiters;
        /// <summary>Short reason the candidate was refused, or null when eligible.</summary>
        public string Refusal;
    }

    /// <summary>Scores a candidate hull against what the role's jobs have required.
    ///
    /// <para><b>Constrained choice, not a weighted blend.</b> Eligibility is a filter; cost ranks the
    /// survivors; if nothing is eligible the caller degrades rather than blocking. A multiplicative
    /// score would let a very cheap hull outrank one that actually solves the problem — this shape
    /// cannot, which is also why there is no weight to tune. It mirrors the filter-then-rank
    /// <see cref="ShipyardManager"/> already performs for route feasibility and the cargo floor.</para>
    ///
    /// <para>One function, read by both the build-time and dispatch-time choosers when Slice 1 wires
    /// it in — the same reason <see cref="DemandUrgency"/> is one function with two readers: a hull
    /// the builder considers good and the dispatcher declines to send is incoherent.</para></summary>
    public static class FleetFitness
    {
        /// <summary>Judges one candidate. <paramref name="requiredLiters"/> is the requirement read
        /// off <see cref="RoleRequirement.SizeAtPercentile"/>; 0 means "no opinion" and every
        /// candidate is eligible, so a cold faction behaves exactly as it does today.</summary>
        public static FitnessVerdict Score(IPrefabCatalogue catalogue, string prefabId, double requiredLiters)
        {
            var verdict = new FitnessVerdict { Eligible = true };
            if (catalogue == null || string.IsNullOrEmpty(prefabId)) return verdict;

            verdict.CargoLiters = catalogue.CargoLiters(prefabId);
            verdict.BuildCost   = catalogue.Cost(prefabId);

            if (requiredLiters > 0 && verdict.CargoLiters < requiredLiters)
            {
                verdict.Eligible = false;
                verdict.Refusal = "cargo_below_requirement";
            }
            return verdict;
        }

        /// <summary>Litres of hold one trip of <paramref name="vector"/> needs: its own
        /// <see cref="MaterialVector.Quantity"/> at the item's volume. 0 means "no observation" —
        /// gas, which travels in tanks a cargo floor does not size (<see cref="GasCarriage.InTanks"/>),
        /// or an item whose volume is unknown, which is not a zero-sized job.</summary>
        public static double TripLiters(MaterialVector vector, IItemVolumeCatalogue volumes)
        {
            if (volumes == null || vector.Quantity <= 0) return 0;
            if (GasCarriage.InTanks(vector.ItemKey)) return 0;
            double litersPerUnit = volumes.VolumeLitersPerUnit(vector.ItemKey);
            return litersPerUnit > 0 ? vector.Quantity * litersPerUnit : 0;
        }

        /// <summary>The cargo floor a new build is sized against: the larger of the live per-shipment
        /// floor and what this role's jobs have historically required (docs/fleet-fitness-plan.md,
        /// slice 1).
        ///
        /// <para><b>Why the historical figure is needed at all.</b> The live floor is derived from ONE
        /// shipment, at the moment a dispatch failed, and measured across 16 live builds on
        /// 2026-09-03 it ran 3.2x to 30.3x BELOW the role's p90 demand — under it in every single
        /// case. A hull bought to that floor is sized for one delivery rather than for the work.</para>
        ///
        /// <para><b>It only ever raises.</b> Taking the larger means a build can never be sized
        /// smaller than it is today, so the change has one direction and one failure mode to watch.
        /// A requirement no hull can meet is not a dead end either: <c>ShipyardManager.SelectPrefabBom</c>
        /// already retries with the floor dropped, treating it as a preference while the route check
        /// stays a hard gate.</para>
        ///
        /// <para>Cargo roles only, and only while warm. Mine and combat have their own requirements
        /// (slices 2 and 3) which are not built, and a cold faction keeps today's behaviour so a
        /// fresh one can still bootstrap.</para></summary>
        /// <param name="destinedForSale">The purpose discriminator agreed with FAC-060: a build for the
        /// shelf, not for the faction's own fleet. Such a build is **excluded** from fleet fitness
        /// rather than given a fitness of its own — sizing a shelf hull against the faction's own
        /// haulage p90 would size it by a statistic about work the faction does, not about anything a
        /// buyer wants, and the build would succeed looking entirely plausible.
        /// <para>The parameter exists ahead of its caller on purpose. FAC-060 retires the
        /// <c>StoreSmall</c>/<c>StoreLarge</c> role strings, and the moment a store build posts a
        /// fleet role with nothing carrying its purpose it inherits the floor below. That made the
        /// flag and the retirement a single-commit problem across two sessions; accepting it here,
        /// defaulted false and behaviour-preserving, reduces it to passing <c>true</c>.</para></param>
        public static double SizingFloor(FactionState st, string role, FactionTuning tuning, double liveFloor,
            bool destinedForSale = false)
        {
            if (destinedForSale) return liveFloor;
            if (st == null || tuning == null || !tuning.FleetRequirementDrivesBuild) return liveFloor;
            if (tuning.FleetRequirementPercentile <= 0) return liveFloor;
            if (!string.Equals(role, "Trade", StringComparison.Ordinal)) return liveFloor;

            RoleRequirement requirement;
            if (!st.FleetRequirements.TryGetValue(role, out requirement) || requirement == null) return liveFloor;
            if (!requirement.IsWarm) return liveFloor;

            double required = requirement.SizeAtPercentile(tuning.FleetRequirementPercentile);
            return required > liveFloor ? required : liveFloor;
        }

        /// <summary>The candidate this requirement implies: cheapest among those that meet it, or —
        /// when none do — the largest available, which is the closest thing to meeting it. Never
        /// returns null while <paramref name="candidates"/> is non-empty, so a caller can always
        /// field something.
        /// <para><paramref name="degraded"/> reports that nothing met the requirement, which is the
        /// signal that the faction cannot currently build for the work it has.</para></summary>
        /// <param name="costOf">Build cost of a candidate, on whatever scale the caller's real ranker
        /// uses. **It must be the SAME measure**: the live ranker sorts by summed BOM units while
        /// <see cref="IPrefabCatalogue.Cost"/> is a component-price sum, and comparing a choice made
        /// on one against a choice made on the other produces disagreements that say nothing about
        /// the requirement. Null falls back to the catalogue's cost, which is only right for a caller
        /// that ranks on it.</param>
        public static string Choose(IPrefabCatalogue catalogue, IList<string> candidates,
            double requiredLiters, out bool degraded, Func<string, long> costOf = null)
        {
            degraded = false;
            if (candidates == null || candidates.Count == 0) return null;

            string best = null; long bestCost = 0;
            string largest = null; long largestCargo = -1;

            for (int i = 0; i < candidates.Count; i++)
            {
                string id = candidates[i];
                FitnessVerdict v = Score(catalogue, id, requiredLiters);
                if (costOf != null) v.BuildCost = costOf(id);

                if (v.CargoLiters > largestCargo
                    || (v.CargoLiters == largestCargo && string.CompareOrdinal(id, largest) < 0))
                {
                    largestCargo = v.CargoLiters; largest = id;
                }
                if (!v.Eligible) continue;

                // Ordinal tie-break so two equal-cost candidates cannot pick differently between the
                // synchronous and staged paths for the same world.
                if (best == null || v.BuildCost < bestCost
                    || (v.BuildCost == bestCost && string.CompareOrdinal(id, best) < 0))
                {
                    best = id; bestCost = v.BuildCost;
                }
            }

            if (best != null) return best;
            degraded = true;
            return largest;
        }
    }
}
