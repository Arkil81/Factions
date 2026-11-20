using System;

namespace Factions.Core
{
    /// <summary>
    /// The SE-free half of assembling a station across frames (FAC-443 option 3): which blocks go
    /// first, and how many go in the next frame.
    ///
    /// <para><b>Why.</b> A station landed in one frame: 1,552 ms at N600 on 2026-10-03, after a restart,
    /// with the player present. SE adds a grid (physics included) in one main-thread step, so the only
    /// way to split one grid is to add its blocks over several frames.</para>
    /// </summary>
    public static class StationAssemblyPlan
    {
        /// <summary>Block indices nearest the blocks' centroid first, so the station grows outward from
        /// its core rather than in save order. Ties keep save order.</summary>
        public static int[] CoreOutward(int[] x, int[] y, int[] z)
        {
            int n = x == null ? 0 : x.Length;
            var order = new int[n];
            if (n == 0) return order;
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < n; i++) { cx += x[i]; cy += y[i]; cz += z[i]; order[i] = i; }
            cx /= n; cy /= n; cz /= n;
            var d = new double[n];
            for (int i = 0; i < n; i++)
            {
                double dx = x[i] - cx, dy = y[i] - cy, dz = z[i] - cz;
                d[i] = dx * dx + dy * dy + dz * dz;
            }
            Array.Sort(order, (a, b) =>
            {
                int c = d[a].CompareTo(d[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            return order;
        }

        /// <summary>The next chunk's block count: scaled so the next paste takes about
        /// <paramref name="targetMs"/>, from what the last one of <paramref name="lastCount"/> blocks
        /// took, within [<paramref name="min"/>, <paramref name="max"/>]. At most doubles per step, so
        /// one cheap chunk of trivial blocks cannot buy a huge one.</summary>
        public static int NextChunk(int lastCount, double lastMs, double targetMs, int min, int max)
        {
            if (lastCount <= 0) return min;
            double scaled = lastMs <= 0.5 ? lastCount * 2.0 : lastCount * targetMs / lastMs;
            if (scaled > lastCount * 2.0) scaled = lastCount * 2.0;
            int next = (int)Math.Round(scaled);
            if (next < min) next = min;
            if (next > max) next = max;
            return next;
        }
    }
}
