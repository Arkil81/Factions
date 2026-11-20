using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Uniform spatial hash over node positions, so a range query costs the nodes actually near the
    /// query point instead of the whole graph.
    ///
    /// <para>This is the cost bound the sensing model needs. Without it a per-faction sweep is
    /// owned x all-nodes -- on the live graph roughly 1865 x 1865 distance tests -- which is fine
    /// once and ruinous per tick. Nodes do not move, so the index is rebuilt only when the graph's
    /// node set changes, the same cheap staleness signal <see cref="InfluenceField"/> uses.</para>
    /// </summary>
    public sealed class NodeProximityIndex
    {
        private struct Cell : IEquatable<Cell>
        {
            public readonly int X, Y, Z;
            public Cell(int x, int y, int z) { X = x; Y = y; Z = z; }
            public bool Equals(Cell other) { return X == other.X && Y == other.Y && Z == other.Z; }
            public override bool Equals(object obj) { return obj is Cell && Equals((Cell)obj); }
            public override int GetHashCode()
            {
                unchecked { return ((X * 397) ^ Y) * 397 ^ Z; }
            }
        }

        private readonly double _cellKm;
        private readonly int _builtNodeCount;
        private readonly Dictionary<Cell, List<string>> _cells = new Dictionary<Cell, List<string>>();

        private NodeProximityIndex(double cellKm, int builtNodeCount)
        {
            _cellKm = cellKm;
            _builtNodeCount = builtNodeCount;
        }

        public double CellKm { get { return _cellKm; } }
        public int CellCount { get { return _cells.Count; } }

        /// <summary>Cell size should be at least the largest range anyone will query, so a query only
        /// has to visit the 3x3x3 block around the point. A smaller cell is still correct, just
        /// slower.</summary>
        public static NodeProximityIndex Build(SpatialGraph graph, double cellKm)
        {
            if (cellKm <= 0) cellKm = 1.0;
            var index = new NodeProximityIndex(cellKm, graph == null ? 0 : graph.Nodes.Count);
            if (graph == null) return index;
            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node == null || string.IsNullOrEmpty(node.Id)) continue;
                Cell cell = index.CellOf(node.Position);
                List<string> bucket;
                if (!index._cells.TryGetValue(cell, out bucket))
                {
                    bucket = new List<string>();
                    index._cells[cell] = bucket;
                }
                bucket.Add(node.Id);
            }
            return index;
        }

        /// <summary>Cheap structural staleness check. Ownership and capability drift do not invalidate
        /// the index -- only positions do, and those never change for a node that still exists.</summary>
        public bool IsStaleFor(SpatialGraph graph)
        {
            return graph == null || graph.Nodes.Count != _builtNodeCount;
        }

        /// <summary>Appends every node within <paramref name="radiusKm"/> of the point. The radius is
        /// applied exactly; the index only bounds which cells are examined.</summary>
        public void QueryWithin(
            SpatialGraph graph, Vec3 posKm, double radiusKm, List<SpatialNode> into)
        {
            if (graph == null || into == null || radiusKm <= 0) return;
            int span = (int)Math.Ceiling(radiusKm / _cellKm);
            Cell centre = CellOf(posKm);
            double radiusSq = radiusKm * radiusKm;
            for (int dx = -span; dx <= span; dx++)
            for (int dy = -span; dy <= span; dy++)
            for (int dz = -span; dz <= span; dz++)
            {
                List<string> bucket;
                if (!_cells.TryGetValue(new Cell(centre.X + dx, centre.Y + dy, centre.Z + dz), out bucket))
                    continue;
                for (int i = 0; i < bucket.Count; i++)
                {
                    SpatialNode node;
                    if (!graph.Nodes.TryGetValue(bucket[i], out node) || node == null) continue;
                    if (Sensing.DistanceSquared(posKm, node.Position) > radiusSq) continue;
                    into.Add(node);
                }
            }
        }

        /// <summary>Appends every node within <paramref name="radiusKm"/> of ANY of
        /// <paramref name="pointsKm"/>, each node at most once.
        ///
        /// <para>This is the shape a per-frame proximity scan wants. Asking "is any observer near
        /// this node" costs one pass over the whole graph however few observers there are; asking
        /// "which nodes are near this observer" costs the cell neighbourhood around each observer, so
        /// an empty observer list costs nothing at all and one observer costs a 3x3x3 block. A node
        /// near two observers is returned once, which is what a spawn or presence decision wants.</para>
        ///
        /// <para>Appends, and — like <see cref="QueryWithinSegment"/> — dedupes only within this
        /// call: entries already in <paramref name="into"/> are neither inspected nor matched
        /// against. Clear it first if that matters.</para>
        ///
        /// <para><paramref name="scratchSeen"/> is the caller's dedup set, reusable across calls to
        /// keep a per-frame path allocation-free; it is cleared on entry. Pass null and one is
        /// allocated per call — but a single point needs no set at all and allocates none.</para></summary>
        public void QueryWithinAny(
            SpatialGraph graph, IList<Vec3> pointsKm, double radiusKm,
            List<SpatialNode> into, HashSet<string> scratchSeen = null)
        {
            if (graph == null || into == null || pointsKm == null || pointsKm.Count == 0) return;
            if (radiusKm <= 0) return;

            // A node lives in exactly one cell and QueryWithin visits each cell once, so one point
            // cannot return a duplicate and needs no set.
            if (pointsKm.Count == 1)
            {
                QueryWithin(graph, pointsKm[0], radiusKm, into);
                return;
            }

            HashSet<string> seen = scratchSeen ?? new HashSet<string>(StringComparer.Ordinal);
            if (scratchSeen != null) seen.Clear();
            for (int i = 0; i < pointsKm.Count; i++)
            {
                int start = into.Count;
                QueryWithin(graph, pointsKm[i], radiusKm, into);
                int write = start;
                for (int j = start; j < into.Count; j++)
                    if (seen.Add(into[j].Id)) into[write++] = into[j];
                if (write < into.Count) into.RemoveRange(write, into.Count - write);
            }
        }

        /// <summary>Appends every node within <paramref name="radiusKm"/> of the SEGMENT from
        /// <paramref name="fromKm"/> to <paramref name="toKm"/>.
        ///
        /// <para>This is what makes a moving sensor's coverage independent of how often it is
        /// sampled. Point-sampling a fleet that travels 4500 km between sweeps against a 60 km-wide
        /// sensor surveys 1.33% of its route; sweeping the segment surveys all of it at any cadence.</para>
        ///
        /// <para>The broad phase marches the segment a cell at a time rather than taking its bounding
        /// box: a long diagonal segment has an enormous box and is thin, so the box would visit
        /// overwhelmingly empty space. Marching costs one cell-neighbourhood lookup per cell of
        /// length -- ~45 for a 4500 km leg at a 100 km cell.</para></summary>
        public void QueryWithinSegment(
            SpatialGraph graph, Vec3 fromKm, Vec3 toKm, double radiusKm,
            List<SpatialNode> into, HashSet<string> scratchSeen = null)
        {
            if (graph == null || into == null || radiusKm <= 0) return;
            double dx = toKm.X - fromKm.X, dy = toKm.Y - fromKm.Y, dz = toKm.Z - fromKm.Z;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            HashSet<string> seen = scratchSeen ?? new HashSet<string>(StringComparer.Ordinal);
            if (scratchSeen != null) seen.Clear();

            int steps = (int)Math.Ceiling(length / _cellKm);
            if (steps < 1) steps = 1;
            int span = (int)Math.Ceiling(radiusKm / _cellKm);
            double radiusSq = radiusKm * radiusKm;

            // Consecutive marched blocks overlap by two thirds along the marched axis, so without
            // this the same bucket is fetched and walked three times over. A 4500 km leg marches ~46
            // steps at 27 cells each; deduping cells cuts that by roughly 3x.
            var visited = new HashSet<Cell>();
            for (int step = 0; step <= steps; step++)
            {
                double t = steps == 0 ? 0 : (double)step / steps;
                var probe = new Vec3(fromKm.X + dx * t, fromKm.Y + dy * t, fromKm.Z + dz * t);
                Cell centre = CellOf(probe);
                for (int cx = -span; cx <= span; cx++)
                for (int cy = -span; cy <= span; cy++)
                for (int cz = -span; cz <= span; cz++)
                {
                    var cell = new Cell(centre.X + cx, centre.Y + cy, centre.Z + cz);
                    if (!visited.Add(cell)) continue;
                    List<string> bucket;
                    if (!_cells.TryGetValue(cell, out bucket))
                        continue;
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        if (!seen.Add(bucket[i])) continue;
                        SpatialNode node;
                        if (!graph.Nodes.TryGetValue(bucket[i], out node) || node == null) continue;
                        if (Sensing.DistanceToSegmentSquared(node.Position, fromKm, toKm) > radiusSq) continue;
                        into.Add(node);
                    }
                }
            }
        }

        private Cell CellOf(Vec3 p)
        {
            return new Cell(
                (int)Math.Floor(p.X / _cellKm),
                (int)Math.Floor(p.Y / _cellKm),
                (int)Math.Floor(p.Z / _cellKm));
        }
    }
}
