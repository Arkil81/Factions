using System;

namespace Factions.Core
{
    /// <summary>
    /// The statistics behind a node survey: how many points are needed to pin a share down, and what
    /// share a given number of points has actually pinned down.
    ///
    /// <para>One relation, exposed both ways round, deliberately. The stopping rule ("may this survey
    /// stop?") and the confidence report ("how well surveyed is this node?") are the same question,
    /// and keeping them as one function is what stops them drifting into disagreeing about a node.
    /// See FAC-101.</para>
    /// </summary>
    public static class SurveyMath
    {
        /// <summary>z for a 90% two-sided normal interval. Fixed rather than tunable — the tunable is
        /// the share the survey must resolve, which is a design choice; the confidence level at which
        /// "resolve" means anything is not.</summary>
        private const double Z90 = 1.645;

        /// <summary>Relative precision the target is expressed at: ±10% of the share itself. A share
        /// of 5% is resolved when it is known to within ±0.5 points, not ±10.</summary>
        private const double RelativePrecision = 0.10;

        /// <summary>
        /// Points needed to know a share of <paramref name="share"/> to within
        /// <see cref="RelativePrecision"/> at 90% confidence.
        ///
        /// <para><c>n = (z/e)² · (1−p)/p</c> — the normal approximation to a binomial proportion.
        /// Cost rises steeply as the share falls: 5% wants ~5,100 points and 1% wants ~26,800, which
        /// is exactly why the survey is paced over time rather than taken in one tick.</para>
        /// </summary>
        public static long PointsToResolve(double share)
        {
            if (share <= 0) return long.MaxValue;
            if (share >= 1) return 1;
            double n = (Z90 / RelativePrecision) * (Z90 / RelativePrecision) * (1.0 - share) / share;
            return n < 1 ? 1 : (long)Math.Ceiling(n);
        }

        /// <summary>
        /// The inverse: the smallest share <paramref name="points"/> examinations can resolve.
        /// Returns 1.0 for a survey that has not started — nothing below "the whole deposit" is
        /// established yet, which is the honest answer rather than a flattering one.
        /// </summary>
        public static double ShareFloorFor(long points)
        {
            if (points <= 0) return 1.0;
            // n = k(1-p)/p  =>  p = k/(k+n), with k = (z/e)².
            double k = (Z90 / RelativePrecision) * (Z90 / RelativePrecision);
            double p = k / (k + points);
            return p > 1.0 ? 1.0 : p;
        }

        /// <summary>True once <paramref name="points"/> is enough to resolve
        /// <paramref name="targetShare"/>. The survey's stopping rule, and the reason a node leaves
        /// the survey queue.</summary>
        /// <summary>How far a survey has got, 0 (nothing known) to 1 (target reached), as a
        /// fraction of the points <see cref="PointsToResolve"/> asks for. This is what "unsurveyed"
        /// means as a number, so a node's worth can follow the evidence rather than a die.</summary>
        public static double Confidence(long pointsExamined, double targetShare)
        {
            if (pointsExamined <= 0) return 0.0;
            long needed = PointsToResolve(targetShare);
            if (needed <= 0) return 1.0;
            double c = (double)pointsExamined / needed;
            return c >= 1.0 ? 1.0 : c;
        }

        public static bool IsSurveyed(long points, double targetShare)
        {
            return points >= PointsToResolve(targetShare);
        }

        /// <summary>
        /// Probes one node must be given per visit for a survey to finish in
        /// <paramref name="durationHours"/>, given how many nodes share the rotation.
        ///
        /// <para><b>Duration is the knob because a rate is not one.</b> The survey advances one node
        /// per frame, so a node's own progress is the per-visit budget divided by however many nodes
        /// are ahead of it — a fixed probes-per-tick therefore means a completely different duration
        /// on a 110-node estate than on a 760-node one, and the number an operator sets stops
        /// describing what they asked for as soon as the world grows. Deriving the budget from the
        /// live rotation size keeps "two days" true at any estate size.</para>
        ///
        /// <para>One full pass costs <paramref name="candidateNodes"/> frames, so a node is visited
        /// <c>fps / candidateNodes</c> times a second and needs
        /// <c>PointsToResolve / (visits x seconds)</c> probes each visit.</para>
        ///
        /// <para>Clamped to at least 1 — a rotation can always creep — and to
        /// <paramref name="maxPerTick"/>, which bounds what one frame may be asked to read however
        /// large the estate or however short the requested duration.</para>
        /// </summary>
        public static int ProbesPerVisit(double targetShare, int candidateNodes, double durationHours,
                                         double framesPerSecond, int maxPerTick)
        {
            if (candidateNodes <= 0 || durationHours <= 0 || framesPerSecond <= 0) return 0;
            if (maxPerTick <= 0) return 0;

            long needed = PointsToResolve(targetShare);
            if (needed <= 0) return 0;

            double budget = (double)needed * candidateNodes / (framesPerSecond * durationHours * 3600.0);
            if (budget <= 1.0) return 1;
            return budget >= maxPerTick ? maxPerTick : (int)Math.Ceiling(budget);
        }
    }
}
