using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.GameSystems;
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// SE-backed implementation of <see cref="IPlanetCatalogue"/>.
    /// Wraps <c>MyGamePruningStructure</c>, <c>MyGravityProviderSystem</c>, and
    /// <c>MyPlanet</c> APIs to provide planet-aware zone classification and surface
    /// snapping to <see cref="WorldModel.ExpandFrom"/>.
    ///
    /// Must only be called from the SE game thread.
    /// All positions exchanged with Core are in km; SE world coords are in metres.
    /// </summary>
    internal sealed class SePlanetCatalogue : IPlanetCatalogue
    {
        // Authored per-body surface rule. Never null -- an empty policy defers every body to SE's
        // own flag, which is the shipped behaviour when no recipe is present.
        private readonly SurfaceNodePolicy _surfacePolicy;

        internal SePlanetCatalogue(SurfaceNodePolicy surfacePolicy)
        {
            if (surfacePolicy == null)
            {
                string ignored;
                SurfaceNodePolicy.TryBuild(null, out surfacePolicy, out ignored);
            }
            _surfacePolicy = surfacePolicy;
        }

        // Built once, on first read, and never rebuilt. Planets do not move and do not change, so
        // there is nothing to keep in sync -- and this property is read twice per
        // ShipPool.RouteEnvironment.From, which is the hottest call in the plugin (FAC-265).
        private List<PlanetRecord> _cache;

        /// <summary>Live bodies, mapped to Core's SE-free record. The list is built on the first
        /// read and reused for the life of the session.
        /// <para>Never enumerate <c>MyEntities.GetEntities()</c> to find planets: that walks every
        /// entity in the world -- thousands of asteroid voxel maps -- under the entity set's spin
        /// lock, to reject all but four. <see cref="MyPlanets"/> keeps the list SE itself maintains.
        /// </para></summary>
        public IEnumerable<PlanetRecord> Planets
        {
            get { return _cache ?? Build(); }
        }

        /// <summary>Drops the built list so the next read rebuilds it. The only caller is the
        /// <c>factions world apply</c> path, which is the one thing in this process that can create
        /// a planet after boot (<c>WorldBuilder.Apply</c> -> <c>MyWorldGenerator.AddPlanet</c>).
        /// Nothing else invalidates, because nothing else can.</summary>
        internal void Invalidate()
        {
            _cache = null;
        }

        private List<PlanetRecord> Build()
        {
            var records = new List<PlanetRecord>();
            List<MyPlanet> live = MyPlanets.GetPlanets();
            if (live == null || live.Count == 0)
                // Not cached. An empty answer here is far more likely to mean MyPlanets has not
                // registered its bodies yet than to mean the world has none, and caching that would
                // classify every node as DeepSpace for the rest of the session with nothing to heal
                // it. Retrying costs a Count check, not a scan.
                return records;

            for (int i = 0; i < live.Count; i++)
            {
                MyPlanet planet = live[i];
                if (planet == null) continue;
                Vector3D c = planet.PositionComp.GetPosition();
                float gLimit = 0f;
                var gravComp = planet.Components.Get<MyGravityProviderComponent>();
                if (gravComp != null) gLimit = gravComp.GravityLimit;
                var record = new PlanetRecord
                {
                    Id            = planet.EntityId.ToString(),
                    Name          = planet.DisplayName ?? "Planet",
                    Generator     = planet.Generator == null ? "Unknown" : planet.Generator.Id.SubtypeName,
                    Center        = ToKm(c),
                    SurfaceRadius = planet.AverageRadius / 1000.0,
                    GravityRadius = gLimit / 1000.0,
                    AtmosphereRadius = (planet.AverageRadius + planet.AtmosphereAltitude) / 1000.0,
                    HasAtmosphere = planet.Generator != null && planet.Generator.HasAtmosphere,
                    BreathableOxygen = planet.Generator != null && planet.Generator.HasAtmosphere
                        && planet.Generator.Atmosphere.Breathable
                        ? planet.Generator.Atmosphere.Density * planet.Generator.Atmosphere.OxygenDensity : 0.0,
                    AllowsOrbitalStations = planet.Generator == null || planet.Generator.AllowOrbitalEconomyStations,
                };

                // Every authored per-body rule is stamped in one Core-side place, so a Core test
                // can cover the hop from policy to PlanetRecord -- this project cannot be reached
                // from the test suite. Vanilla's own station generator filters its planet list on
                // AllowSurfaceEconomyStations before placing outposts vs orbitals, so a body that
                // refuses surface stations there must refuse our nodes too; a planet with no
                // generator definition keeps PlanetRecord's permissive defaults rather than being
                // silently treated as forbidden.
                _surfacePolicy.ApplyTo(record,
                    planet.Generator == null ? null : planet.Generator.Id.SubtypeName,
                    planet.Generator == null || planet.Generator.AllowSurfaceEconomyStations);
                records.Add(record);
            }

            _cache = records;
            return records;
        }

        public double GravityAt(Vec3 posKm)
        {
            float gMult;
            MyGravityProviderSystem.CalculateNaturalGravityInPoint(ToM(posKm), out gMult);
            return gMult;
        }

        public Vec3 NearestSurfacePoint(Vec3 posKm)
        {
            Vector3D worldPos = ToM(posKm);
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(worldPos);
            if (planet == null) return posKm;
            return ToKm(planet.GetClosestSurfacePointGlobal(worldPos));
        }

        public Vec3 SurfaceNormal(Vec3 posKm)
        {
            Vector3D worldPos = ToM(posKm);
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(worldPos);
            if (planet == null) return new Vec3(0, 1, 0);
            Vector3D normal = Vector3D.Normalize(worldPos - planet.PositionComp.GetPosition());
            return new Vec3(normal.X, normal.Y, normal.Z);
        }

        private static Vector3D ToM(Vec3 km)
        {
            return new Vector3D(km.X * 1000.0, km.Y * 1000.0, km.Z * 1000.0);
        }

        private static Vec3 ToKm(Vector3D m)
        {
            return new Vec3(m.X / 1000.0, m.Y / 1000.0, m.Z / 1000.0);
        }
    }
}
