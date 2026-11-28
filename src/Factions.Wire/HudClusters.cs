using System;

namespace Factions.Wire
{
    /// <summary>
    /// Which scanner markers the HUD bundles into one, by SE's own rule (<c>MyHudMarkerRender</c>):
    /// two points are near when half their separation is no more than <c>sin(angle / 2)</c> of the
    /// camera's distance to their midpoint, with <c>ClusterAngle</c> 10 degrees -- seen from the
    /// camera, they are about 10 degrees apart or closer. Like SE, a pass seeds a group with the
    /// first unbundled marker and adds every later one near the seed; nearness is to the seed, not
    /// chained through the group.
    /// </summary>
    public static class HudClusters
    {
        /// <summary>SE's <c>MyHudMarkerRender.PointOfInterest.ClusterAngle</c>.</summary>
        public const double ClusterAngleDeg = 10.0;

        /// <summary>SE's <c>IsPOINearby</c>, in metres or any one unit.</summary>
        public static bool Near(double ax, double ay, double az, double bx, double by, double bz,
                                double cx, double cy, double cz, double angleDeg)
        {
            double hx = 0.5 * (ax - bx), hy = 0.5 * (ay - by), hz = 0.5 * (az - bz);
            double half2 = hx * hx + hy * hy + hz * hz;
            double mx = cx - (bx + hx), my = cy - (by + hy), mz = cz - (bz + hz);
            double reach = Math.Sin(angleDeg * (Math.PI / 360.0)) * Math.Sqrt(mx * mx + my * my + mz * mz);
            return half2 <= reach * reach;
        }

        /// <summary>
        /// The seed of each point's group: <c>seedOf[i] == i</c> for a point that leads a group or
        /// stands alone, otherwise the index of the point it was bundled under. Points with
        /// <paramref name="clusters"/>[i] false are never bundled and bundle nothing. Positions are
        /// <c>xyz[3*i .. 3*i+2]</c>.
        /// </summary>
        public static int[] Seeds(double[] xyz, bool[] clusters, double cx, double cy, double cz, double angleDeg)
        {
            int n = xyz.Length / 3;
            var seedOf = new int[n];
            for (int i = 0; i < n; i++) seedOf[i] = -1;
            for (int i = 0; i < n; i++)
            {
                if (seedOf[i] >= 0) continue;
                seedOf[i] = i;
                if (!clusters[i]) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (seedOf[j] >= 0 || !clusters[j]) continue;
                    if (Near(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2],
                             xyz[3 * j], xyz[3 * j + 1], xyz[3 * j + 2], cx, cy, cz, angleDeg))
                        seedOf[j] = i;
                }
            }
            return seedOf;
        }
    }
}
