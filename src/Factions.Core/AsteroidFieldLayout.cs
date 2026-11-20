using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One asteroid of a node's field: where it sits relative to the node, how big it is,
    /// the seed of its shape, and the two rolls that pick its material.</summary>
    public struct AsteroidPlacement
    {
        /// <summary>Offset from the node's centre, in metres.</summary>
        public Vec3 OffsetM;
        public double Size;
        public int ShapeSeed;
        /// <summary>Uniform [0,1): which of the plan's ores this asteroid is made of.</summary>
        public double MaterialRoll;
        /// <summary>Uniform [0,1): which voxel material of that ore.</summary>
        public double VariantRoll;
    }

    /// <summary>
    /// Where a node's asteroid field goes, as a pure function of the node's id and its plan.
    ///
    /// <para>On a Nexus cluster the field is spawned by whichever server a player first reaches it
    /// on, and that is not the authority. A field rolled from the clock would differ per server and
    /// per restart, so the authority would have to be told where every rock went. Derived from the
    /// node id, every server places the same rock and the authority only needs to learn that it
    /// was placed. Each asteroid has its own stream, so asteroid 3 is the same whether or not
    /// asteroids 0-2 found a material.</para>
    /// </summary>
    public static class AsteroidFieldLayout
    {
        /// <summary>The value stamped on <see cref="SpatialNode.AsteroidFieldSeed"/> once a field is
        /// placed. Nonzero for every id.</summary>
        public static int FieldSeed(string nodeId)
        {
            return WorldRecipe.StableHash(nodeId ?? "") | 1;
        }

        /// <param name="stationClearanceM">Radius around the node centre kept clear when the node has
        /// a founded station (the safe zone); the asteroid's own size and a 100 m margin are added.</param>
        public static List<AsteroidPlacement> Layout(string nodeId, AsteroidFieldPlan plan, double resourceValue,
                                                     bool founded, double stationClearanceM)
        {
            var result = new List<AsteroidPlacement>();
            if (plan == null || plan.Count <= 0) return result;
            int seed = FieldSeed(nodeId);
            double radiusM = plan.RadiusKm * 1000.0;
            double rv = MathUtil.Clamp01(resourceValue);
            for (int i = 0; i < plan.Count; i++)
            {
                int stream = FeatureRockPlanner.Combine(seed, i + 1);
                var rng = new DeterministicRng(stream);
                double size = (plan.SizeMin + (plan.SizeMax - plan.SizeMin) * rv) * (0.85 + rng.NextDouble() * 0.3);

                // Generous, not tangent: vanilla's CSG shapes have lobes reaching close to the full
                // size from centre (a tighter margin once obstructed a station's spawn point).
                double clearanceM = founded ? stationClearanceM + size + 100.0 : 0.0;
                if (clearanceM >= radiusM) clearanceM = radiusM * 0.5;

                result.Add(new AsteroidPlacement
                {
                    OffsetM = PointInShell(ref rng, radiusM, clearanceM),
                    Size = size,
                    ShapeSeed = stream,
                    MaterialRoll = rng.NextDouble(),
                    VariantRoll = rng.NextDouble(),
                });
            }
            return result;
        }

        /// <summary>Uniform in volume between <paramref name="minRadius"/> and <paramref name="radius"/>.</summary>
        private static Vec3 PointInShell(ref DeterministicRng rng, double radius, double minRadius)
        {
            if (radius <= 0) return new Vec3(0, 0, 0);
            double theta = rng.NextDouble() * 2.0 * Math.PI;
            double phi = Math.Acos(2.0 * rng.NextDouble() - 1.0);
            double lo = Math.Max(0, minRadius);
            double minCubed = lo * lo * lo;
            double maxCubed = radius * radius * radius;
            double r = Math.Pow(minCubed + (maxCubed - minCubed) * rng.NextDouble(), 1.0 / 3.0);
            double sinPhi = Math.Sin(phi);
            return new Vec3(r * sinPhi * Math.Cos(theta), r * sinPhi * Math.Sin(theta), r * Math.Cos(phi));
        }
    }
}
