using System;

namespace Factions.Core
{
    /// <summary>
    /// The box in which a moving body may touch rock soon: its position now and where its velocity carries it within
    /// a lookahead, inflated on every side by a base reach. Pristine feature rock gets its Havok collision grid only
    /// inside such a box (FAC-445 lever A): a grid costs ~640 KB per voxel map whatever the rock's size, and ~1,850
    /// rocks stand in view of one observer while a handful are ever touched.
    /// </summary>
    public static class RockContact
    {
        /// <summary>Axis-aligned box swept from <paramref name="posM"/> along <paramref name="velMs"/> for
        /// <paramref name="lookaheadS"/> seconds, inflated by <paramref name="reachM"/>.</summary>
        public static void SweptBox(Vec3 posM, Vec3 velMs, double reachM, double lookaheadS, out Vec3 min, out Vec3 max)
        {
            double reach = Math.Max(0, reachM), ahead = Math.Max(0, lookaheadS);
            var end = new Vec3(posM.X + velMs.X * ahead, posM.Y + velMs.Y * ahead, posM.Z + velMs.Z * ahead);
            min = new Vec3(Math.Min(posM.X, end.X) - reach, Math.Min(posM.Y, end.Y) - reach, Math.Min(posM.Z, end.Z) - reach);
            max = new Vec3(Math.Max(posM.X, end.X) + reach, Math.Max(posM.Y, end.Y) + reach, Math.Max(posM.Z, end.Z) + reach);
        }
    }
}
