using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One body, fully resolved: where it goes, what it is called on disk, and every
    /// derived number the host needs to create it and then check that it landed correctly.</summary>
    public sealed class PlannedPlanet
    {
        public string Name;
        public string Definition;
        public string StorageName;
        public int Seed;
        public double DiameterKm;

        /// <summary>Where the body's centre is meant to end up.</summary>
        public Vec3 CenterKm;

        /// <summary>What <c>MyWorldGenerator.AddPlanet</c> is actually given. Derived, never authored.</summary>
        public Vec3 MinCornerKm;

        /// <summary>Storage cube edge in km — the min-corner offset is half this, and it is a power
        /// of two metres rather than the planet's diameter.</summary>
        public double StorageSizeKm;

        /// <summary>How far this body's natural gravity reaches.</summary>
        public double GravityLimitKm;

        /// <summary>The outermost terrain radius — <c>r + r × HillParams.Max</c>. What clearance from
        /// pre-existing objects is measured against, since burying something in voxel is the failure
        /// that matters, not merely enclosing it in a gravity well.</summary>
        public double TerrainRadiusKm;

        /// <summary>The radius kept clear of other bodies: the gravity limit, or the reach of the
        /// widest feature anchored here, whichever is greater.</summary>
        public double ExclusionRadiusKm;

        /// <summary>False when no position satisfied the constraints; <see cref="Rejection"/> says why.</summary>
        public bool Placed;

        /// <summary>Which constraint kept refusing, when <see cref="Placed"/> is false.</summary>
        public string Rejection;

        /// <summary>How many candidate positions were tried. Zero for an authored centre.</summary>
        public int Attempts;

        /// <summary>True when the centre came from the recipe rather than the placement search.</summary>
        public bool CenterWasAuthored;

        public bool AllowSurfaceEconomyStations;
        public bool AllowOrbitalEconomyStations;

        /// <summary>Give this body a GPS marker on creation.</summary>
        public bool AddGps;

        /// <summary>Real Gas Giants parameters, when this body is one. Carried through so the host
        /// can write the mod's config from the same numbers the planner sized the body with.</summary>
        public GasGiantSpec GasGiant;

        /// <summary>The band this body was pinned to, if any.</summary>
        public string Orbit;

        /// <summary>The band it ended up on, and how far from the system centre it landed.</summary>
        public string OrbitBandName;
        public double OrbitRadiusKm;

        /// <summary>The SE <c>EntityId</c> of the body once it has actually been created. Zero until
        /// then. This is what a feature's <c>AnchorPlanetId</c> is resolved to — the id does not
        /// exist at plan time, which is why features anchor by name until it does.</summary>
        public long RealisedEntityId;
    }

    /// <summary>A feature with its anchor resolved to a planet in the plan. The SE EntityId is not
    /// known until apply time; <see cref="AnchorPlanetName"/> is what the emitter resolves against.</summary>
    public sealed class PlannedFeature
    {
        public string Name;
        public WorldFeatureShape Shape;

        /// <summary>Name of the planet in this plan, or null for a free-floating feature.</summary>
        public string AnchorPlanetName;

        /// <summary>The centre to write into <c>world_features.json</c>. For an anchored feature this
        /// is the planet's planned centre, so the loader's no-anchor fallback lands on the body
        /// rather than at the origin.</summary>
        public Vec3 CenterKm;

        public Vec3 Normal;
        public double InnerRadiusKm;
        public double OuterRadiusKm;
        public double HalfThicknessKm;

        /// <summary>Packing, 0..1. See <see cref="WorldFeature.Density"/>.</summary>
        public double Density;

        /// <summary>Minimum km between two nodes in this region. See
        /// <see cref="WorldFeature.NodeSeparationKm"/>.</summary>
        public double? NodeSeparationKm;

        /// <summary>The <see cref="NodeZone"/> nodes inside this region belong to. Null defers to
        /// gravity classification. Zone's remaining job is separation, so this authors how densely
        /// the region may be settled — see <see cref="WorldFeature.Zone"/>.</summary>
        public NodeZone? Zone;

        /// <summary>True when a respawn ship arriving in space may be placed in this region. See
        /// <see cref="WorldFeature.SpaceRespawn"/>.</summary>
        public bool SpaceRespawn;

        public List<FeatureBand> Bands = new List<FeatureBand>();

        /// <summary>Composition. Null is no ore opinion — the region governs placement only and the
        /// reader beneath it answers for ore; an empty list authors a barren region that shadows it.
        /// See <see cref="WorldFeature.Materials"/>.</summary>
        public List<FeatureMaterial> Materials;

        /// <summary>True when this feature was derived from a gas giant's ring rather than authored.
        /// Its geometry comes from the same radius and scales the mod's own config gets, so the two
        /// cannot drift apart.</summary>
        public bool DerivedFromGasGiant;
    }

    /// <summary>
    /// The output of planning: an exact, inspectable description of what <c>apply</c> would create.
    /// Serialisable on purpose — a plan can be committed and built against before any of it exists.
    /// </summary>
    public sealed class WorldPlan
    {
        public int WorldSeed;
        public List<PlannedPlanet> Planets = new List<PlannedPlanet>();
        public List<PlannedFeature> Features = new List<PlannedFeature>();

        /// <summary>How many distinct generator definitions this plan uses. SE's <c>MaxPlanets</c>
        /// caps distinct <b>types</b>, not instances, and rejects with a bare null at apply time —
        /// so this is the number to check before anything is created.</summary>
        public int DistinctDefinitionCount;

        /// <summary>Anything that refused to plan. A non-empty list means <c>apply</c> must not run.</summary>
        public List<string> Errors = new List<string>();

        /// <summary>Things worth saying out loud that are not failures — retries, spilled margins.</summary>
        public List<string> Notes = new List<string>();

        /// <summary>How many pre-existing objects placement was checked against. Zero means nothing
        /// was supplied, which is not the same as nothing being there — a plan that checked nothing
        /// should not read like a plan that found nothing.</summary>
        public int ObstaclesConsidered;

        public bool IsApplicable
        {
            get
            {
                if (Errors.Count > 0) return false;
                for (int i = 0; i < Planets.Count; i++)
                    if (!Planets[i].Placed) return false;
                return true;
            }
        }
    }
}
