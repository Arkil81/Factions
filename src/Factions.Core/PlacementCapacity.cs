using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Where a faction's estate may be seeded. A planet is named by its <b>generator
    /// subtype</b> and a feature by its <b>name</b> — see <see cref="PlacementRegion.Key"/>.
    ///
    /// <para><b>Not to be confused with <see cref="EstateRegion"/>,</b> which is the *authored*
    /// half: what an operator writes in <c>faction_recipe.json</c>, carrying separate
    /// <c>Body</c>/<c>Feature</c> fields and the node count. This is the *resolved* half — one
    /// discriminated key the capacity and feasibility maths look up. The seeder maps the first onto
    /// the second.</para></summary>
    public enum PlacementRegionKind
    {
        /// <summary>On a body's terrain. Refused outright when the body forbids surface nodes.</summary>
        PlanetSurface = 0,
        /// <summary>Inside a body's gravity well but off its terrain.</summary>
        PlanetOrbit = 1,
        /// <summary>Inside an authored world feature — a ring, a belt.</summary>
        Feature = 2,
        /// <summary>Open space, outside every body and feature.</summary>
        DeepSpace = 3,
    }

    /// <summary>One place an estate may occupy.</summary>
    public sealed class PlacementRegion
    {
        public PlacementRegionKind Kind;

        /// <summary>
        /// Planet regions key by <b>generator subtype</b> (<c>RealGasGiant</c>, <c>Tellus</c>, …), not
        /// by name: <see cref="PlanetRecord.Name"/> is SE's display name and reads <c>"Planet"</c> for
        /// every body on the live world, so a name key can never match. Feature regions key by name
        /// (<c>"Cauldron Ring"</c>), which does survive. Null for <see cref="PlacementRegionKind.DeepSpace"/>.
        /// </summary>
        public string Key;

        public override string ToString()
        {
            return Kind + (string.IsNullOrEmpty(Key) ? "" : "[" + Key + "]");
        }
    }

    /// <summary>The geometry a region occupies, resolved from the live world before planning.
    /// Kept SE-free so the planner and its tests share one shape.</summary>
    public sealed class PlacementRegionGeometry
    {
        /// <summary>Sphere radius (km) for a planet surface — nodes sit ON this sphere, so capacity
        /// is a spherical packing rather than a volumetric one.</summary>
        public double SurfaceRadiusKm;

        /// <summary>Outer radius (km) of a shell or feature volume.</summary>
        public double OuterRadiusKm;

        /// <summary>Inner radius (km) of a shell or annulus. Zero for a solid volume.</summary>
        public double InnerRadiusKm;

        /// <summary>Half-thickness (km) of a disc-shaped feature. Zero means the volume is a shell
        /// or ball rather than a disc.</summary>
        public double HalfThicknessKm;

        /// <summary>Where the region sits in world space, in km. The sampler needs it; capacity
        /// does not, since how many fit is a function of size rather than of place.</summary>
        public Vec3 CentreKm;

        /// <summary>False when the body's own definition — or the recipe — forbids nodes on its
        /// terrain. A surface assignment against this has zero capacity by rule, not by geometry.</summary>
        public bool AllowsSurface = true;
    }

    /// <summary>
    /// How many nodes a region can hold at a given separation floor.
    ///
    /// <para><b>Why this exists.</b> <c>WorldModel.ExpandFrom</c> retries placement a bounded number
    /// of times and then commits the node anyway, so an over-subscribed region does not fail — it
    /// produces nodes closer than the floor and records a separation violation after the fact. On a
    /// small body that is the difference between a world that looks seeded and one that is. Measured
    /// on the live system: Kor's 9.5 km surface holds <b>four</b> nodes at a 15 km floor, against a
    /// brief that assigns eleven factions to it.</para>
    ///
    /// <para>Estimates are deliberately conservative — a planner should refuse slightly early rather
    /// than emit a plan that silently degrades.</para>
    /// </summary>
    public static class PlacementCapacity
    {
        /// <summary>Fraction of a sphere's area usable once exclusion caps are packed. Circle packing
        /// on a sphere tops out near 0.90 for large counts and falls well below it for small ones;
        /// 0.83 is the conservative middle used here.</summary>
        public const double SphericalPackingEfficiency = 0.83;

        /// <summary>Hexagonal circle packing in a plane tops out at 0.9069; a single-layer disc is
        /// packed at essentially that, since the annulus is far wider than one exclusion circle.</summary>
        public const double PlanarPackingEfficiency = 0.90;

        /// <summary>Random-close packing of equal spheres. Below the 0.74 crystalline optimum on
        /// purpose: the planner samples positions, it does not lay out a lattice.</summary>
        public const double VolumePackingEfficiency = 0.60;

        /// <summary>
        /// Maximum nodes that fit in <paramref name="region"/> at <paramref name="separationKm"/>.
        /// Returns 0 for a surface region the body forbids, and 0 for a non-positive separation —
        /// an unbounded region is not the planner's decision to make silently.
        /// </summary>
        public static double MaxNodes(PlacementRegionKind kind, PlacementRegionGeometry geometry, double separationKm)
        {
            if (geometry == null || separationKm <= 0) return 0;

            switch (kind)
            {
                case PlacementRegionKind.PlanetSurface:
                    return geometry.AllowsSurface ? OnSphere(geometry.SurfaceRadiusKm, separationKm) : 0;

                case PlacementRegionKind.PlanetOrbit:
                case PlacementRegionKind.Feature:
                    return geometry.HalfThicknessKm > 0
                        ? InDisc(geometry.InnerRadiusKm, geometry.OuterRadiusKm, geometry.HalfThicknessKm, separationKm)
                        : InShell(geometry.InnerRadiusKm, geometry.OuterRadiusKm, separationKm);

                default:
                    // Open space is bounded by the authored volume, not by this region. A caller that
                    // wants a deep-space cap must supply one; reporting "unbounded" as a number would
                    // be a lie the planner then acts on.
                    return double.PositiveInfinity;
            }
        }

        /// <summary>Largest N this class will ever report for a sphere. Well past any real estate;
        /// exists so a pathological separation cannot spin the search.</summary>
        private const int MaxSphereSearch = 4096;

        /// <summary>
        /// Points ON a sphere with pairwise distance at least <paramref name="separationKm"/> —
        /// <b>as many as the seeder can actually place</b>, not as many as could theoretically fit.
        ///
        /// <para><b>This deliberately reports less than the Tammes optimum, because the optimum is not
        /// what gets built.</b> Positions come from <see cref="RegionSampler.OnSphere"/>'s golden-angle
        /// lattice, whose <i>minimum</i> spacing runs 20–25% below optimal even though its average
        /// spacing is near it. An earlier version of this method returned an exclusion-cap area
        /// estimate — 11.1 nodes for Kor at a 10 km floor, against a true optimum of 10 and a lattice
        /// that delivers <b>8</b>. A planner approving 11 and a sampler able to build 8 is the exact
        /// failure this class exists to prevent, one level further in: the plan passes and placement
        /// silently breaks the floor anyway.</para>
        ///
        /// <para>So capacity is defined by the sampler and measured from it. The two cannot drift,
        /// because there is only one answer and it is computed, not estimated.</para>
        /// </summary>
        public static double OnSphere(double radiusKm, double separationKm)
        {
            if (radiusKm <= 0 || separationKm <= 0) return 0;
            if (separationKm > 2.0 * radiusKm) return 1;   // not even an antipodal pair clears it

            int best = 1;
            for (int n = 2; n <= MaxSphereSearch; n++)
            {
                if (MinSpacingOnSphere(radiusKm, n) < separationKm) break;
                best = n;
            }
            return best;
        }

        /// <summary>Minimum pairwise distance the lattice achieves at this count. O(n²), bounded by
        /// <see cref="MaxSphereSearch"/> and only ever called on real estate sizes.</summary>
        private static double MinSpacingOnSphere(double radiusKm, int count)
        {
            List<Vec3> pts = RegionSampler.OnSphere(new Vec3(0, 0, 0), radiusKm, count, seed: 0);
            double min = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double d = pts[i].DistanceSquaredTo(pts[j]);
                    if (d < min) min = d;
                }
            return pts.Count < 2 ? double.MaxValue : Math.Sqrt(min);
        }

        /// <summary>Points inside a spherical shell (or ball, when <paramref name="innerRadiusKm"/>
        /// is zero) at the given separation.</summary>
        public static double InShell(double innerRadiusKm, double outerRadiusKm, double separationKm)
        {
            if (outerRadiusKm <= 0 || separationKm <= 0) return 0;
            if (innerRadiusKm < 0) innerRadiusKm = 0;
            if (innerRadiusKm >= outerRadiusKm) return 0;

            double volume = (4.0 / 3.0) * Math.PI * (Cube(outerRadiusKm) - Cube(innerRadiusKm));
            return volume / ExclusionVolume(separationKm) * VolumePackingEfficiency;
        }

        /// <summary>
        /// Points inside a disc-shaped annulus — a ring.
        ///
        /// <para><b>A ring thinner than one exclusion sphere is a plane, not a volume.</b> The
        /// Cauldron Ring spans 270 km of radius and is 3.6 km thick, so at any separation above that
        /// it holds exactly one layer and capacity is an <i>area</i> problem: treating it as a volume
        /// and padding the thickness up to the separation inflates it by the ratio between them —
        /// 139× at the 500 km deep-space floor, which is the difference between "structurally
        /// uninhabitable" and "roomy".</para>
        /// </summary>
        public static double InDisc(double innerRadiusKm, double outerRadiusKm, double halfThicknessKm,
            double separationKm)
        {
            if (outerRadiusKm <= 0 || separationKm <= 0) return 0;
            if (innerRadiusKm < 0) innerRadiusKm = 0;
            if (innerRadiusKm >= outerRadiusKm) return 0;

            double annulusArea = Math.PI * (outerRadiusKm * outerRadiusKm - innerRadiusKm * innerRadiusKm);
            double fullThickness = halfThicknessKm * 2.0;

            if (fullThickness <= separationKm)
            {
                double exclusionCircle = Math.PI * (separationKm / 2.0) * (separationKm / 2.0);
                return annulusArea / exclusionCircle * PlanarPackingEfficiency;
            }

            return annulusArea * fullThickness / ExclusionVolume(separationKm) * VolumePackingEfficiency;
        }

        private static double ExclusionVolume(double separationKm)
        {
            double r = separationKm / 2.0;
            return (4.0 / 3.0) * Math.PI * r * r * r;
        }

        private static double Cube(double v) { return v * v * v; }
    }
}
