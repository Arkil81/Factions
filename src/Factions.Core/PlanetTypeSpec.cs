using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The physical constants of one SE planet generator definition, as an SE-free record.
    ///
    /// <para>The host resolves these from <c>MyPlanetGeneratorDefinition</c> and injects them; the
    /// sim and the tests supply a static table. Everything the planner needs to place a body — its
    /// storage footprint and how far its gravity reaches — is derivable from these numbers alone,
    /// which is what lets planning run with no planet in existence.</para>
    /// </summary>
    public sealed class PlanetTypeSpec
    {
        /// <summary>Generator definition subtype: <c>EarthLike</c>, <c>Mars</c>, <c>RealGasGiant</c>…</summary>
        public string DefinitionName;

        /// <summary><c>HillParams.Max</c> — hill height as a fraction of the base radius.</summary>
        public double HillParamsMax;

        /// <summary><c>SurfaceGravity</c>, in g.</summary>
        public double SurfaceGravity = 1.0;

        /// <summary><c>GravityFalloffPower</c>. SE's own default is 7.</summary>
        public double GravityFalloffPower = 7.0;

        /// <summary>Whether vanilla will place surface economy stations here. Also the answer to
        /// "can anything land on this body" — a gas giant wrapped around a 9.5 km core says no.</summary>
        public bool AllowSurfaceEconomyStations;

        /// <summary>Whether vanilla will place orbital economy stations here.</summary>
        public bool AllowOrbitalEconomyStations;

        /// <summary>SE's gravity cutoff: the limit is where gravity falls to 0.05 g
        /// (<c>MySphericalNaturalGravityComponent.GRAVITY_LIMIT_STRENGTH</c>).</summary>
        public const double GravityLimitStrength = 0.05;

        /// <summary>The outermost terrain radius, in km, for a body of <paramref name="diameterKm"/>.
        /// Mirrors <c>MyPlanetStorageProvider.Init</c>: <c>r + r × HillParams.Max</c>.</summary>
        public double MaxRadiusKm(double diameterKm)
        {
            double r = diameterKm * 0.5;
            return r + r * HillParamsMax;
        }

        /// <summary>
        /// How far this body's natural gravity reaches, in km. Mirrors
        /// <c>MySphericalNaturalGravityComponent.CalculateGravityLimit</c>:
        /// <c>MaxRadius × (SurfaceGravity / 0.05) ^ (1 / GravityFalloffPower)</c>.
        /// </summary>
        public double GravityLimitKm(double diameterKm)
        {
            double maxRadius = MaxRadiusKm(diameterKm);
            if (GravityFalloffPower <= 0) return maxRadius;
            double intensity = SurfaceGravity <= 0 ? 0 : SurfaceGravity;
            return maxRadius * Math.Pow(intensity / GravityLimitStrength, 1.0 / GravityFalloffPower);
        }

        /// <summary>
        /// The voxel storage cube's edge length in km, which is <b>not</b> the planet's diameter.
        /// Mirrors <c>MyVoxelCoordSystems.FindBestOctreeSize(2 × MaxRadius)</c>: the smallest power
        /// of two metres that is at least that, with a floor of 32.
        ///
        /// <para>This is the number behind the min-corner offset. Getting it wrong displaces the
        /// body — and then silently displaces every feature anchored to it.</para>
        /// </summary>
        public double StorageSizeKm(double diameterKm)
        {
            double neededM = 2.0 * MaxRadiusKm(diameterKm) * 1000.0;
            double sizeM = 32.0;
            while (sizeM < neededM) sizeM *= 2.0;
            return sizeM / 1000.0;
        }

        /// <summary>Centre → min corner. <c>MyWorldGenerator.AddPlanet</c> takes the min corner, and
        /// the resulting centre is <c>minCorner + StorageSize/2</c> on every axis.</summary>
        public Vec3 MinCornerFromCenterKm(Vec3 centerKm, double diameterKm)
        {
            double half = StorageSizeKm(diameterKm) * 0.5;
            return new Vec3(centerKm.X - half, centerKm.Y - half, centerKm.Z - half);
        }

        /// <summary>Min corner → centre. The inverse of <see cref="MinCornerFromCenterKm"/>, used to
        /// verify a realised planet landed where the plan said it would.</summary>
        public Vec3 CenterFromMinCornerKm(Vec3 minCornerKm, double diameterKm)
        {
            double half = StorageSizeKm(diameterKm) * 0.5;
            return new Vec3(minCornerKm.X + half, minCornerKm.Y + half, minCornerKm.Z + half);
        }
    }

    /// <summary>Resolves a generator definition name to its physical constants. The host implements
    /// this over <c>MyDefinitionManager</c>; tests and the sim use <see cref="StaticPlanetTypeCatalogue"/>.</summary>
    public interface IPlanetTypeCatalogue
    {
        /// <summary>False when the definition does not exist — which is a plan-time error, because
        /// at apply time SE answers an unknown definition with a bare null.</summary>
        bool TryGet(string definitionName, out PlanetTypeSpec spec);
    }

    /// <summary>In-memory catalogue for tests, the sim, and a plan produced without a live session.</summary>
    public sealed class StaticPlanetTypeCatalogue : IPlanetTypeCatalogue
    {
        private readonly Dictionary<string, PlanetTypeSpec> _byName =
            new Dictionary<string, PlanetTypeSpec>(StringComparer.OrdinalIgnoreCase);

        public StaticPlanetTypeCatalogue() { }

        public StaticPlanetTypeCatalogue(IEnumerable<PlanetTypeSpec> specs)
        {
            if (specs == null) return;
            foreach (PlanetTypeSpec s in specs) Add(s);
        }

        public StaticPlanetTypeCatalogue Add(PlanetTypeSpec spec)
        {
            if (spec != null && !string.IsNullOrEmpty(spec.DefinitionName))
                _byName[spec.DefinitionName] = spec;
            return this;
        }

        public bool TryGet(string definitionName, out PlanetTypeSpec spec)
        {
            spec = null;
            return !string.IsNullOrEmpty(definitionName) && _byName.TryGetValue(definitionName, out spec);
        }
    }
}
