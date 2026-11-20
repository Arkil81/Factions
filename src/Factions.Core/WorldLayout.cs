using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One orbital band: an annulus of radii about the system centre.</summary>
    public sealed class OrbitBand
    {
        /// <summary>Optional name, so a planet can be pinned to this band by naming it.</summary>
        public string Name;

        public double InnerRadiusKm;
        public double OuterRadiusKm;
    }

    /// <summary>
    /// How unpinned bodies are positioned. A system rather than a scatter: bodies sit on radius
    /// bands about a central body, near a common plane, tilted by a small inclination.
    ///
    /// <para>Absent, placement falls back to uniform sampling inside the recipe's volume — which
    /// produces isolated worlds rather than somewhere you travel across.</para>
    /// </summary>
    public sealed class WorldLayout
    {
        /// <summary>Recipe planet name the bands are measured from. Null or unmatched puts the
        /// system centre at the world origin.</summary>
        public string CentralBody;

        /// <summary>Normal of the system plane. Bodies sit near it, within
        /// <see cref="InclinationDeg"/>.</summary>
        public Vec3 PlaneNormal = new Vec3(0, 1, 0);

        /// <summary>Maximum tilt off the system plane, in degrees. Zero is a perfectly flat system.</summary>
        public double InclinationDeg = 5.0;

        public List<OrbitBand> Bands = new List<OrbitBand>();

        public bool Validate(out string why)
        {
            if (Bands == null || Bands.Count == 0) { why = "Layout needs at least one Band."; return false; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Bands.Count; i++)
            {
                OrbitBand b = Bands[i];
                if (b == null) { why = "Layout has a null Band."; return false; }
                if (b.InnerRadiusKm < 0) { why = "a Band's InnerRadiusKm cannot be negative."; return false; }
                if (b.OuterRadiusKm <= b.InnerRadiusKm)
                {
                    why = "band " + (string.IsNullOrEmpty(b.Name) ? "#" + i : "'" + b.Name + "'")
                        + " needs OuterRadiusKm greater than InnerRadiusKm.";
                    return false;
                }
                if (!string.IsNullOrEmpty(b.Name) && !seen.Add(b.Name))
                {
                    why = "duplicate band name '" + b.Name + "' -- names are how a planet pins its orbit.";
                    return false;
                }
            }
            double n = Math.Sqrt(PlaneNormal.X * PlaneNormal.X + PlaneNormal.Y * PlaneNormal.Y + PlaneNormal.Z * PlaneNormal.Z);
            if (n <= 0) { why = "Layout.PlaneNormal cannot be zero-length."; return false; }
            if (InclinationDeg < 0 || InclinationDeg > 90) { why = "Layout.InclinationDeg must be between 0 and 90."; return false; }
            why = null;
            return true;
        }

        /// <summary>The band a planet should take: the one it names, else null for the caller to
        /// distribute.</summary>
        public OrbitBand FindBand(string name)
        {
            if (string.IsNullOrEmpty(name) || Bands == null) return null;
            foreach (OrbitBand b in Bands)
                if (b != null && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }
    }

    /// <summary>Samples positions on an orbital band. Separated from the planner so the geometry is
    /// testable on its own.</summary>
    public static class OrbitSampler
    {
        /// <summary>An orthonormal basis whose third axis is <paramref name="normal"/>. The choice of
        /// the first axis is arbitrary but deterministic, so a seeded run reproduces exactly.</summary>
        public static void Basis(Vec3 normal, out Vec3 u, out Vec3 v, out Vec3 n)
        {
            double len = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
            if (len <= 0) { n = new Vec3(0, 1, 0); len = 1; }
            else n = new Vec3(normal.X / len, normal.Y / len, normal.Z / len);

            // Cross with whichever axis the normal is least aligned to, so the cross never degenerates.
            Vec3 seed = Math.Abs(n.Y) < 0.9 ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
            u = Normalize(Cross(seed, n));
            v = Cross(n, u);
        }

        /// <summary>A point on <paramref name="band"/> about <paramref name="centre"/>: uniform radius
        /// within the band, uniform angle around the plane, and a tilt within
        /// <paramref name="inclinationDeg"/> of it.</summary>
        public static Vec3 Sample(Vec3 centre, OrbitBand band, Vec3 planeNormal, double inclinationDeg, IRandom rng)
        {
            Vec3 u, v, n;
            Basis(planeNormal, out u, out v, out n);

            double radius = band.InnerRadiusKm + rng.NextDouble() * (band.OuterRadiusKm - band.InnerRadiusKm);
            double theta = rng.NextDouble() * 2.0 * Math.PI;
            double phi = (rng.NextDouble() * 2.0 - 1.0) * inclinationDeg * Math.PI / 180.0;

            double inPlane = Math.Cos(phi);
            double x = radius * (Math.Cos(theta) * inPlane * u.X + Math.Sin(theta) * inPlane * v.X + Math.Sin(phi) * n.X);
            double y = radius * (Math.Cos(theta) * inPlane * u.Y + Math.Sin(theta) * inPlane * v.Y + Math.Sin(phi) * n.Y);
            double z = radius * (Math.Cos(theta) * inPlane * u.Z + Math.Sin(theta) * inPlane * v.Z + Math.Sin(phi) * n.Z);
            return new Vec3(centre.X + x, centre.Y + y, centre.Z + z);
        }

        private static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        private static Vec3 Normalize(Vec3 a)
        {
            double len = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
            return len <= 0 ? new Vec3(1, 0, 0) : new Vec3(a.X / len, a.Y / len, a.Z / len);
        }
    }
}
