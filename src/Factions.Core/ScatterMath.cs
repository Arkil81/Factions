using System;

namespace Factions.Core
{
    /// <summary>
    /// How much ore a planet's scattered environment voxel maps — rock pillars and the like — put
    /// on a square metre of ground, from SE's own spawn rule rather than from spawning any.
    ///
    /// <para><b>Why it has to be modelled rather than read.</b> These maps exist only near a player
    /// (SE builds them with a sector's physics) and are not in the save until someone digs one, so
    /// there is nothing on the server to count. What is fixed is the rule that places them. SE
    /// scatters <c>ItemDensity</c> spawn points per square metre of each cube-face plane, gives the
    /// level-0 slice of them (<see cref="LodZeroShare"/>) to items that can be voxel maps, picks one
    /// of the environment mappings whose rule matches the point, and samples an item from it.
    /// Decompiled SE, <c>MyProceduralLogicalSector.GetItemForPosition</c> and
    /// <c>ProgressiveScanHelper</c>.</para>
    ///
    /// <para>The host supplies each mapping's expected ore volume per spawn; this turns that into a
    /// volume per square metre of sphere, which is the unit a band's thickness is in, thinned by the
    /// share of attempts SE refuses for overlapping a map already placed
    /// (<see cref="PlacedShare"/>).</para>
    /// </summary>
    public static class ScatterMath
    {
        /// <summary>Share of a sector's spawn points that SE's progressive scan hands to level of
        /// detail 0 — the only level whose sampler offers voxel maps. SE splits points across levels
        /// by <c>F(x) = -(ln10/ln4)^-x / ln4</c>, so level 0 gets <c>F(1) - F(0) = 1/ln4 - 1/ln10</c>,
        /// 0.287.</summary>
        public static readonly double LodZeroShare = 1.0 / Math.Log(4.0) - 1.0 / Math.Log(10.0);

        /// <summary>Square metres of cube-face plane per square metre of sphere along a surface
        /// direction whose largest absolute component is <paramref name="dominantComponent"/>.
        ///
        /// <para>SE lays spawn points on a plane at <c>R/sqrt2</c> from the centre and projects them
        /// radially, so a plane metre covers more sphere at a face's centre than at its corner.
        /// Projecting at distance h onto radius R, a sphere element is <c>R^2 h / L^3</c> times its
        /// plane element, L being the ray length <c>h / c</c>; inverted, with h = R/sqrt2, that is
        /// <c>1 / (2 c^3)</c> — 0.5 at a face centre, 2.6 at a corner, 0.955 averaged over the
        /// body.</para></summary>
        public static double PlanePerSphereArea(double dominantComponent)
        {
            double c = Math.Abs(dominantComponent);
            if (c < 1.0 / Math.Sqrt(3.0)) c = 1.0 / Math.Sqrt(3.0);   // a unit vector's floor
            if (c > 1.0) c = 1.0;
            return 1.0 / (2.0 * c * c * c);
        }

        /// <summary>Largest share of ground non-overlapping boxes cover when they are dropped one at a
        /// time at random places and turns, each refused if it would overlap one already standing —
        /// random sequential adsorption's jamming limit, 0.5 for squares of random turn. Fitted to a
        /// simulation of exactly that rule.</summary>
        public const double JammingCoverage = 0.5;

        /// <summary>Mean area two randomly turned squares of area A exclude each other from, over A:
        /// <c>2 + P^2 / (2 pi A)</c> with perimeter <c>P = 4 sqrt(A)</c>, which is <c>2 + 8/pi</c>.
        /// Sets how fast refusals start as placements thicken.</summary>
        public static readonly double ExcludedAreaRatio = 2.0 + 8.0 / Math.PI;

        /// <summary>Share of voxel-map spawn attempts SE actually places.
        /// <para>SE refuses a voxel map whose box would overlap a static entity already standing
        /// (<c>MyVoxelMapEnvironmentProxy.AddVoxelMap</c>). A mapping authored to fill its ground —
        /// Kor's cities give their pipes a density of 1 — therefore packs its maps side by side rather
        /// than stacking one per spawn point, and counting every attempt overstates its ore a
        /// hundredfold.</para>
        /// <para><paramref name="attemptsPerM2"/> is voxel-map attempts per square metre of sphere and
        /// <paramref name="footprintM2"/> their mean box footprint. Covered share follows
        /// <c>d(theta)/dx = (1 - theta/J)^k</c> with <c>k = ExcludedAreaRatio x J</c>, which matches
        /// the exact low-density slope and the jamming limit, and a simulation of the rule within 6%
        /// at every density between; this returns covered share over attempted cover.</para></summary>
        public static double PlacedShare(double attemptsPerM2, double footprintM2)
        {
            double x = attemptsPerM2 * footprintM2;
            if (!(x > 1e-12)) return 1.0;
            double k = ExcludedAreaRatio * JammingCoverage;
            double covered = JammingCoverage * (1.0 - Math.Pow(1.0 + (k - 1.0) * x / JammingCoverage, -1.0 / (k - 1.0)));
            return Math.Min(1.0, covered / x);
        }

        /// <summary>Expected cubic metres of one material per square metre of sphere at a point.
        /// <para><paramref name="volumePerSpawnSum"/> is, over every mapping whose rule matches the
        /// point, the expected volume one spawn from that mapping places (item probability times the
        /// item's expected volume, summed over its items); <paramref name="candidates"/> is how many
        /// mappings matched, including ones that hold no voxel map at all, because SE picks one of
        /// them uniformly before sampling an item.</para></summary>
        public static double VolumePerSquareMetre(double itemDensityPerPlaneM2, int candidates,
            double volumePerSpawnSum, double planePerSphereArea)
        {
            if (itemDensityPerPlaneM2 <= 0 || candidates <= 0 || volumePerSpawnSum <= 0) return 0.0;
            return itemDensityPerPlaneM2 * LodZeroShare * volumePerSpawnSum / candidates * planePerSphereArea;
        }
    }
}
