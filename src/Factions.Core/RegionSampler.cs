using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Deterministic, well-spread points inside a region — the position half of estate seeding.
    ///
    /// <para><b>Lattices, not random draws.</b> Rejection-sampling random points and retrying on a
    /// separation failure is what <see cref="WorldModel.ExpandFrom"/> does, and it gives up after a
    /// bounded number of attempts and places the node anyway. Seeding a whole estate that way would
    /// stack those near-misses: on Kor, eleven random points on a 9.5 km sphere will not find the
    /// tetrahedral arrangement that clears 10 km, so the capacity the planner approved would not
    /// actually be reachable. A golden-angle lattice reaches it by construction.</para>
    ///
    /// <para><b>Determinism is a repo rule and a debugging one.</b> Same seed, same world — so a
    /// placement bug is reproducible rather than a story about one boot. The seed varies the lattice's
    /// orientation, never its spacing: rerolling moves an estate, it never crowds one.</para>
    /// </summary>
    public static class RegionSampler
    {
        /// <summary>(1 + √5) / 2. The golden angle it induces is what makes successive lattice points
        /// land as far from their predecessors as a deterministic rule can manage.</summary>
        private static readonly double Phi = (1.0 + Math.Sqrt(5.0)) / 2.0;

        /// <summary>
        /// <paramref name="count"/> points spread over a sphere of <paramref name="radiusKm"/> about
        /// <paramref name="centreKm"/> — a Fibonacci lattice, whose minimum spacing is within a few
        /// percent of the optimal spherical code at every count that matters here.
        /// </summary>
        public static List<Vec3> OnSphere(Vec3 centreKm, double radiusKm, int count, int seed)
        {
            var points = new List<Vec3>();
            if (count <= 0 || radiusKm <= 0) return points;

            // A seeded rotation about the polar axis, plus a half-step z offset. Both move the whole
            // lattice rigidly, so spacing is identical for every seed.
            double spin = SeededUnit(seed) * 2.0 * Math.PI;

            for (int i = 0; i < count; i++)
            {
                // z walks the interval evenly; equal-area bands make the spacing uniform rather than
                // bunching at the poles the way an even latitude/longitude grid does.
                double z = count == 1 ? 0.0 : 1.0 - (2.0 * i + 1.0) / count;
                double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
                double theta = spin + 2.0 * Math.PI * i / Phi;

                points.Add(new Vec3(
                    centreKm.X + radiusKm * r * Math.Cos(theta),
                    centreKm.Y + radiusKm * r * Math.Sin(theta),
                    centreKm.Z + radiusKm * z));
            }
            return points;
        }

        /// <summary>
        /// <paramref name="count"/> points inside a flat annulus about <paramref name="centreKm"/> —
        /// a ring. Radius is drawn by equal-area quantile so points do not bunch at the inner edge,
        /// and the angle advances by the golden angle so successive points land opposite each other.
        /// <paramref name="halfThicknessKm"/> spreads them through the disc's depth.
        /// </summary>
        public static List<Vec3> InAnnulus(Vec3 centreKm, double innerRadiusKm, double outerRadiusKm,
            double halfThicknessKm, int count, int seed)
        {
            return InAnnulus(centreKm, innerRadiusKm, outerRadiusKm, halfThicknessKm, count, seed, null);
        }

        /// <summary>
        /// As above, keeping only points <paramref name="claims"/> accepts.
        ///
        /// <para>An annulus is not always solid. The Cauldron Ring's bands do not tile it — the
        /// outermost ends at 453.93 km inside a 465 km outer radius, and there are gaps between
        /// every band — and the feature deliberately disowns a point that falls in a gap. Spreading
        /// points by equal area across the <i>whole</i> annulus therefore puts some of them outside
        /// the region that asked for them, where they inherit the deep-space floor and read as
        /// wildly under-separated. Found live: one of nineteen ring nodes, at r=459.94.</para>
        ///
        /// <para>Oversamples and filters rather than solving the claimed geometry, because the claim
        /// is the feature's own predicate and this class must not re-derive it — the same reason
        /// separation has one authority. Returns fewer than <paramref name="count"/> when the region
        /// genuinely cannot supply them; the caller reports that as SHORT.</para>
        /// </summary>
        public static List<Vec3> InAnnulus(Vec3 centreKm, double innerRadiusKm, double outerRadiusKm,
            double halfThicknessKm, int count, int seed, Func<Vec3, bool> claims)
        {
            if (claims == null)
                return InAnnulusRaw(centreKm, innerRadiusKm, outerRadiusKm, halfThicknessKm, count, seed);

            // Escalate the draw until enough survive the filter, or the region is proven too sparse.
            for (int factor = 2; factor <= MaxOversample; factor *= 2)
            {
                List<Vec3> drawn = InAnnulusRaw(centreKm, innerRadiusKm, outerRadiusKm,
                                                halfThicknessKm, count * factor, seed);
                var kept = new List<Vec3>();
                foreach (Vec3 v in drawn)
                {
                    if (!claims(v)) continue;
                    kept.Add(v);
                    if (kept.Count == count) return kept;
                }
                if (factor * 2 > MaxOversample) return kept;
            }
            return new List<Vec3>();
        }

        /// <summary>How far the draw escalates before a region is called too sparse. 64x a request of
        /// 19 is 1216 candidates — far past any real band gap, and bounded so an unsatisfiable claim
        /// predicate terminates instead of hanging a boot.</summary>
        private const int MaxOversample = 64;

        private static List<Vec3> InAnnulusRaw(Vec3 centreKm, double innerRadiusKm, double outerRadiusKm,
            double halfThicknessKm, int count, int seed)
        {
            var points = new List<Vec3>();
            if (count <= 0 || outerRadiusKm <= 0) return points;
            if (innerRadiusKm < 0) innerRadiusKm = 0;
            if (innerRadiusKm >= outerRadiusKm) return points;

            double spin = SeededUnit(seed) * 2.0 * Math.PI;
            double inner2 = innerRadiusKm * innerRadiusKm;
            double outer2 = outerRadiusKm * outerRadiusKm;

            for (int i = 0; i < count; i++)
            {
                // Equal-area quantile: r = sqrt(inner² + q(outer² − inner²)). Interpolating the radius
                // linearly instead would put the same number of points on every ring of radius, which
                // crowds the inside and thins the outside.
                double q = count == 1 ? 0.5 : (i + 0.5) / count;
                double r = Math.Sqrt(inner2 + q * (outer2 - inner2));
                double theta = spin + 2.0 * Math.PI * i / Phi;

                // Deterministic sweep through the thickness rather than a constant plane, so a ring
                // deeper than its separation can hold more than a single layer.
                double zFraction = count == 1 ? 0.0 : ((2.0 * i + 1.0) / count) - 1.0;

                points.Add(new Vec3(
                    centreKm.X + r * Math.Cos(theta),
                    centreKm.Y + halfThicknessKm * zFraction,
                    centreKm.Z + r * Math.Sin(theta)));
            }
            return points;
        }

        /// <summary>
        /// The <paramref name="count"/> points of <paramref name="candidates"/> closest to
        /// <paramref name="homeKm"/> — how one faction takes a contiguous neighbourhood out of a
        /// lattice shared by every claimant on that body.
        ///
        /// <para>The lattice is generated once for the region's whole demand, so separation holds
        /// <i>across</i> factions and not merely within one. Carving each faction's share by proximity
        /// to its own home is what stops ten factions interleaving into one another's ground.</para>
        /// </summary>
        public static List<Vec3> NearestTo(IList<Vec3> candidates, Vec3 homeKm, int count)
        {
            var taken = new List<Vec3>();
            if (candidates == null || count <= 0) return taken;

            var order = new List<int>();
            for (int i = 0; i < candidates.Count; i++) order.Add(i);
            // Distance, then index: ties must not depend on sort stability across runtimes.
            order.Sort((a, b) =>
            {
                double da = candidates[a].DistanceSquaredTo(homeKm);
                double db = candidates[b].DistanceSquaredTo(homeKm);
                int c = da.CompareTo(db);
                return c != 0 ? c : a.CompareTo(b);
            });

            for (int i = 0; i < count && i < order.Count; i++) taken.Add(candidates[order[i]]);
            return taken;
        }

        /// <summary>A stable [0,1) from a seed. Not a general RNG — one value, one rotation, and the
        /// same answer on every runtime, which <c>Random</c> does not promise across versions.</summary>
        public static double SeededUnit(int seed)
        {
            // SplitMix64's finalizer: strong avalanche, trivially portable, no allocation.
            ulong x = unchecked((ulong)seed + 0x9E3779B97F4A7C15UL);
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return (x >> 11) * (1.0 / 9007199254740992.0);   // 53 bits -> [0,1)
        }

        /// <summary>A per-faction seed that depends on the world seed and the tag and nothing else, so
        /// one faction's estate cannot shift because another's count changed.</summary>
        public static int SeedFor(int worldSeed, string factionTag, string regionKey)
        {
            unchecked
            {
                int h = worldSeed;
                h = h * 31 + StableHash(factionTag);
                h = h * 31 + StableHash(regionKey);
                return h;
            }
        }

        /// <summary><c>string.GetHashCode</c> is randomised per process on modern .NET, so a world
        /// seeded with it would differ between boots. This one does not.</summary>
        private static int StableHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            unchecked
            {
                int h = 17;
                for (int i = 0; i < s.Length; i++) h = h * 31 + s[i];
                return h;
            }
        }
    }
}
