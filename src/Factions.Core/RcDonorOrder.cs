using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>The order in which armor cubes are offered as the cell for an injected remote
    /// control on a small grid.
    ///
    /// <para><b>Buried first.</b> A cube whose six face-adjacent cells are all occupied is inside the
    /// hull: taking it leaves no visible hole, and its neighbours have other links to fall back on,
    /// so the swap is the least likely to cut anything off. Then by how many faces are covered, then
    /// nearest the reference point (the hull's flight seat, or its centre), then by index, so the
    /// same hull always yields the same order.</para></summary>
    public static class RcDonorOrder
    {
        /// <summary>Candidate indices in offer order, at most <paramref name="max"/> of them.
        /// <paramref name="coveredFaces"/> is 0-6 per candidate; <paramref name="distanceSq"/> is the
        /// squared distance from the reference point. The two lists are parallel.</summary>
        public static List<int> Order(IList<int> coveredFaces, IList<double> distanceSq, int max)
        {
            var result = new List<int>();
            if (coveredFaces == null || distanceSq == null || max <= 0) return result;
            int n = Math.Min(coveredFaces.Count, distanceSq.Count);
            for (int i = 0; i < n; i++) result.Add(i);

            result.Sort(delegate (int a, int b)
            {
                int c = coveredFaces[b].CompareTo(coveredFaces[a]);
                if (c != 0) return c;
                c = distanceSq[a].CompareTo(distanceSq[b]);
                if (c != 0) return c;
                return a.CompareTo(b);
            });

            if (result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }
    }
}
