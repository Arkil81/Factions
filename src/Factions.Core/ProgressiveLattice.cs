using System;

namespace Factions.Core
{
    /// <summary>
    /// A low-discrepancy point sequence over the unit disc where point <c>i</c> is **fixed** — it
    /// does not move when more points are added — and every prefix is well spread.
    ///
    /// <para><b>Why this exists, and what it replaces.</b> A survey accumulates observations across
    /// many ticks and across restarts, so its whole resumable state is "how many points have I done".
    /// That only works if point <c>i</c> means the same place forever. The sunflower lattice this
    /// replaces computed <c>rho = sqrt((i + 0.5) / N)</c> — dependent on <b>N, the total</b> — so
    /// growing the sample moved every point already probed. Accumulating across a growing N is then
    /// meaningless, and nothing in the output would say so: the survey would run, the counts would
    /// climb, and the answer would be wrong. See FAC-101.</para>
    ///
    /// <para>Uses the R2 sequence (Roberts): successive multiples of the plastic constant's
    /// reciprocals, taken mod 1. Cheap, stateless, and uniform at any prefix length, which is exactly
    /// the property a resumable survey needs and a Fibonacci spiral cannot offer.</para>
    /// </summary>
    public static class ProgressiveLattice
    {
        // Plastic constant: the real root of x^3 = x + 1. Its reciprocal powers give the R2
        // sequence's two irrational increments.
        private const double A1 = 0.7548776662466927;   // 1 / plastic
        private const double A2 = 0.5698402909980532;   // 1 / plastic^2

        /// <summary>
        /// Point <paramref name="index"/> of the sequence, mapped to a disc of
        /// <paramref name="radius"/> centred on the origin.
        ///
        /// <para>The radial map is <c>sqrt</c> of the first coordinate, which is what makes the
        /// points equal-area over the disc rather than crowding the centre — the same correction the
        /// old lattice made, now against a per-point value instead of a per-total one.</para>
        /// </summary>
        public static void PointOnDisc(long index, double radius, out double x, out double y)
        {
            // Offset by a half-step so index 0 is not the exact centre, matching the old lattice's
            // (i + 0.5) convention and keeping a one-point survey off the singularity.
            double u = Frac(A1 * (index + 1));
            double v = Frac(A2 * (index + 1));

            double r = radius * Math.Sqrt(u);
            double theta = 2.0 * Math.PI * v;
            x = r * Math.Cos(theta);
            y = r * Math.Sin(theta);
        }

        /// <summary>Point <paramref name="index"/> mapped to the unit sphere, for whole-body
        /// reconnaissance. <c>z</c> is spread linearly and the azimuth by the second coordinate,
        /// which is the equal-area map for a sphere.</summary>
        public static void PointOnSphere(long index, out double x, out double y, out double z)
        {
            double u = Frac(A1 * (index + 1));
            double v = Frac(A2 * (index + 1));

            z = 1.0 - 2.0 * u;
            double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
            double theta = 2.0 * Math.PI * v;
            x = r * Math.Cos(theta);
            y = r * Math.Sin(theta);
        }

        private static double Frac(double value)
        {
            double f = value - Math.Floor(value);
            return f < 0 ? f + 1.0 : f;
        }
    }
}
