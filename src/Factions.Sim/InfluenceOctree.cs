using System;
using Factions.Core;

namespace Factions.Sim
{
    /// <summary>
    /// Sparse region octree over the influence field: leaves at the smallest unit (20 km), homogeneous
    /// regions auto-merged. Used here only to MEASURE compression — count the nodes needed to represent
    /// the field. A region merges into one leaf when its dominant faction is uniform across it (so empty
    /// space AND the solid interior of a single faction both collapse); only borders subdivide.
    /// Derived from sources, never persisted (sources are the truth). (graph param reserved for a future
    /// source-aware refinement.)
    /// </summary>
    public static class InfluenceOctree
    {
        public const double CellSizeKm = 20.0;
        public const double EmptyThreshold = 0.03;

        public static int CountNodes(InfluenceField field, SpatialGraph graph, Vec3 center, double halfSize)
        {
            return Build(field, center, halfSize);
        }

        private static int Build(InfluenceField field, Vec3 c, double half)
        {
            if (half <= CellSizeKm / 2.0 || IsUniform(field, c, half))
                return 1;                                   // leaf: smallest unit, or merged uniform block

            int total = 1;                                  // this internal node
            double q = half / 2.0;
            for (int dx = -1; dx <= 1; dx += 2)
                for (int dy = -1; dy <= 1; dy += 2)
                    for (int dz = -1; dz <= 1; dz += 2)
                        total += Build(field, new Vec3(c.X + dx * q, c.Y + dy * q, c.Z + dz * q), q);
            return total;
        }

        // Uniform iff a 3x3x3 sample spanning the cube (corners, faces, edges, AND centre) all agree on
        // the dominant faction (incl. "empty"). The centre sample is what catches an interior feature.
        private static bool IsUniform(InfluenceField field, Vec3 c, double half)
        {
            string baseTag = null;
            bool first = true;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        string t = DominantTag(field, new Vec3(c.X + dx * half, c.Y + dy * half, c.Z + dz * half));
                        if (first) { baseTag = t; first = false; }
                        else if (t != baseTag) return false;
                    }
            return true;
        }

        private static string DominantTag(InfluenceField field, Vec3 p)
        {
            string tag; double top, second;
            field.DominantAt(p, out tag, out top, out second);
            return top < EmptyThreshold ? null : tag;
        }
    }
}
