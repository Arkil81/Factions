using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>Shape of the volume random placement draws from.</summary>
    public enum WorldVolumeShape { Box = 0, Sphere = 1 }

    /// <summary>
    /// The volume new bodies may be placed in, centred on the world origin. Authored per run —
    /// nothing here has a baked default extent, per the operator's "as given via parameter".
    /// </summary>
    public sealed class WorldVolume
    {
        public WorldVolumeShape Shape = WorldVolumeShape.Box;

        /// <summary>Half-extent in km: a box reaches ±this on every axis, a sphere this far from
        /// the origin.</summary>
        public double ExtentKm;

        /// <summary>Keep this radius around the origin clear. Leaves the world centre available for
        /// a spawn area, and keeps a body off the coordinate a mis-anchored feature falls back to.</summary>
        public double OriginKeepOutKm;
    }

    /// <summary>Knobs for the placement search itself.</summary>
    public sealed class WorldPlacementRules
    {
        /// <summary>Extra clearance beyond the two bodies' own exclusion radii, in km.</summary>
        public double SeparationMarginKm = 100.0;

        /// <summary>How many candidate positions to try before declaring a body unplaceable.</summary>
        public int MaxAttemptsPerPlanet = 200;

        /// <summary>
        /// Clearance in km between a body's <b>terrain</b> and any pre-existing thing in the sector
        /// (see <see cref="PlacementObstacle"/>). Measured off the terrain radius rather than the
        /// gravity limit on purpose: a station inside a gravity well is ordinary — a station inside
        /// the voxel is not.
        /// </summary>
        public double ObstacleMarginKm = 50.0;
    }

    /// <summary>
    /// Something already in the sector that a new body must not be created on top of — a station
    /// record, a player grid, a reserved spawn area.
    ///
    /// <para>Live world state rather than authored content, so it is passed into
    /// <see cref="WorldPlanner.Plan"/> alongside the type catalogue rather than sitting in the
    /// recipe. Supplying none is legitimate and is reported as such: a plan that checked nothing
    /// should not read like a plan that found nothing.</para>
    /// </summary>
    public sealed class PlacementObstacle
    {
        /// <summary>What this is, for the plan's own explanation of why it moved a body.</summary>
        public string Label;

        public Vec3 PositionKm;

        /// <summary>Clearance wanted around it, beyond the margin. Zero for a point.</summary>
        public double RadiusKm;

        public PlacementObstacle() { }

        public PlacementObstacle(string label, Vec3 positionKm, double radiusKm = 0)
        {
            Label = label;
            PositionKm = positionKm;
            RadiusKm = radiusKm;
        }
    }

    /// <summary>One authored body.</summary>
    public sealed class RecipePlanet
    {
        /// <summary>Display name, and the key features anchor to. Must be unique in the recipe.</summary>
        public string Name;

        /// <summary>Generator definition subtype — <c>EarthLike</c>, <c>RealGasGiant</c>, …</summary>
        public string Definition;

        /// <summary>Diameter in km. SE's own cap is 8000 km.</summary>
        public double DiameterKm;

        /// <summary>Terrain seed. Absent (0) derives one from the world seed and the name, so a
        /// recipe without explicit seeds is still reproducible.</summary>
        public int Seed;

        /// <summary>Explicit centre in km. Absent means "place me by the layout".
        /// <b>This is a centre, not a min corner</b> — the planner does that conversion.</summary>
        public Vec3? CenterKm;

        /// <summary>Name of a <see cref="OrbitBand"/> to pin this body into. Absent lets the planner
        /// distribute it across the bands.</summary>
        public string Orbit;

        /// <summary>Give this body a GPS marker on creation. Off unless authored — a world where
        /// every planet announces itself is a different game from one where they are found.</summary>
        public bool AddGps;

        /// <summary>
        /// Whether a faction node may be sited on this body's terrain. Absent defers to SE's own
        /// <c>AllowSurfaceEconomyStations</c> on the generator definition.
        ///
        /// <para>Authored because the SE flag cannot answer it here: it defaults to <c>true</c>,
        /// vanilla sets it <c>true</c> on every body that mentions it, and no mod on this server
        /// declares it at all — so it is uniformly true and discriminates nothing. A gas giant whose
        /// 9.5 km core sits under a 191 km atmosphere is exactly the case it would need to catch.</para>
        ///
        /// <para>Deliberately not derived from physics. "Can a ship reach this surface" is a property
        /// of the hull (<see cref="ThrustPhysics.DryTwr"/> against <c>MinDryTwr</c>), not of the body,
        /// so it answers a different question and would vary per prefab.</para>
        /// </summary>
        public bool? AllowSurfaceNodes;

        /// <summary>
        /// Minimum km between two nodes on this body's surface. Absent falls back to the zone floor.
        ///
        /// <para>Authored per body because capacity is sphere packing and this system's planets are
        /// far smaller than the global floors assumed. Kor's 9.5 km surface holds <b>four</b> nodes
        /// at 15 km — a regular tetrahedron, edge 15.5 km — against a brief that puts eleven
        /// claimants on it. At 10 km it holds eleven. Agni goes 12 → 29 the same way.</para>
        ///
        /// <para><b>Over-subscription does not raise an error</b>, which is why this is authored
        /// rather than discovered: <c>ExpandFrom</c> retries a bounded number of times and then
        /// places the node regardless, so a body asked for more than it holds yields silent
        /// separation violations, not a refusal (FAC-105).</para>
        ///
        /// <para><b>Not floor-capped</b> (operator, 2026-08-26: *"No cap, authored intent wins."*).
        /// A small value on a large body is a very large number of nodes, and node count is what the
        /// influence field and the per-faction brain files scale with — the constraint here is
        /// authored intent, not geometry.</para>
        /// </summary>
        public double? NodeSeparationKm;

        /// <summary>
        /// What this body blocks a sensor's sightline at, in km. Absent occludes at the body's own
        /// terrain radius, which is what a real body's radius means.
        ///
        /// <para>Authored because SE's radius is not always the body. Real Gas Giants gives Cauldron a
        /// 9.5 km voxel core and draws the giant players fly around at
        /// <see cref="GasGiantSpec.RadiusKm"/> — 180 km. Deferring to the terrain there leaves the
        /// largest object in the system transparent to every sensor in it; deferring to its
        /// atmosphere or its 305 km gravity limit blocks lines through space nothing occupies.</para>
        ///
        /// <para>Keyed by generator subtype like every other per-body rule, so two entries sharing a
        /// generator must agree — see <see cref="SurfaceNodePolicy"/>.</para>
        /// </summary>
        public double? SensorOcclusionRadiusKm;

        /// <summary>Real Gas Giants parameters. Present makes this body a gas giant: its gravity and
        /// exclusion radius come from here rather than the generator definition, the builder writes a
        /// matching entry into RGG's own config, and each ring becomes a world feature derived from
        /// these same numbers.</summary>
        public GasGiantSpec GasGiant;

        /// <summary>
        /// This body's ore geography, replacing whatever its generator definition ships.
        ///
        /// <para>Null inherits the generator's own mappings, which is the shipped default and what
        /// every body did before this existed. An <b>empty list is an authored statement</b> that
        /// the body carries no ore — the same distinction <see cref="FeatureBand.Materials"/> draws,
        /// and a real case: the gas giant's generator ships an empty <c>&lt;OreMappings/&gt;</c>.</para>
        ///
        /// <para>Authoring repoints and bands; it does not place. See
        /// <see cref="RecipeOreMapping"/>.</para>
        /// </summary>
        public List<RecipeOreMapping> OreMappings;

        /// <summary>
        /// This body's animal spawn rules, applied in place of its generator's
        /// <c>AnimalSpawnInfo</c> wherever one matches. Null leaves the generator's own lists alone,
        /// which is the shipped default. See <see cref="FaunaPolicy"/> for how a rule is chosen and
        /// what happens where none matches.
        /// </summary>
        public List<FaunaRule> Fauna;

        /// <summary>
        /// The crops that grow wild on this body: SE's forageable plants, read off the planet
        /// definition (a surface rule's <c>MyObjectBuilder_Forageable</c> group, whose plants each
        /// yield one crop) and recorded here. An NPC Farm on the body grows any of them with no seed
        /// stock; anything else needs a seed. Names are SE's seed and crop subtypes — <c>Fruit</c>,
        /// <c>Grain</c>, <c>Mushrooms</c>, <c>Vegetables</c>.
        ///
        /// <para>Null and empty both mean nothing grows wild, which is Kor's and every deep-space
        /// node's answer. See docs/food-and-population.md.</para>
        /// </summary>
        public List<string> NativeCrops;

        /// <summary>
        /// Whether this body has a civilian pool a Habitat can recruit workers from. Tellus's is
        /// infinite and eats nothing; it is the only place workers come from (operator, 2026-10-14).
        /// Null and false both mean no pool. docs/food-and-population.md, FAC-493.
        /// </summary>
        public bool? Recruits;
    }

    /// <summary>
    /// One entry of a body's ore map: which voxel material a byte value of the planet's ore cubemap
    /// means, and the depth band it fills. Mirrors SE's own <c>MyPlanetOreMapping</c> field for
    /// field, because the host assigns it straight across.
    ///
    /// <para><b>This chooses what a deposit is, never where it is.</b> Which texels carry which byte
    /// comes from the generator's ore cubemap in its texture folder and is untouched. Authoring
    /// <c>Value</c> 12 as platinum makes every existing byte-12 deposit platinum; it does not create
    /// one.</para>
    /// </summary>
    public sealed class RecipeOreMapping
    {
        /// <summary>The ore-cubemap byte this entry decodes.</summary>
        public byte Value;

        /// <summary><b>Voxel material</b> subtype — <c>Iron_01</c>, <c>Cattierite_01</c> — not an ore
        /// item name. The ore a player gets is that material's <c>MinedOre</c>, which is how one
        /// authored entry can yield a modded ore without this file knowing the mod exists.</summary>
        public string Material;

        /// <summary>Metres below the surface where the band begins. SE's own default is 5.</summary>
        public float Start = 5f;

        /// <summary>Band thickness in metres. SE's own default is 10.</summary>
        public float Depth = 10f;

        /// <summary>Terrain tint over the deposit, as <c>#rrggbb</c>. Null leaves the surface
        /// untinted, which is a visible difference from a vanilla map rather than a neutral default —
        /// vanilla sets it on every entry.</summary>
        public string TargetColor;

        /// <summary>Strength of <see cref="TargetColor"/>. Null with a colour set leaves SE's own
        /// default in place.</summary>
        public float? ColorInfluence;
    }

    /// <summary>How node counts are spread across the factions that get an estate.</summary>
    public enum EstateDistribution
    {
        /// <summary>A few large powers, several mid, a few small — the world reads as having
        /// history rather than as having been dealt evenly.</summary>
        Graduated = 0,

        /// <summary>Every faction gets a count drawn independently from the same range.</summary>
        Uniform = 1
    }

    /// <summary>How many nodes a faction's estate holds.</summary>
    public sealed class EstateNodeCount
    {
        public int Min = 3;
        public int Max = 10;
        public EstateDistribution Distribution = EstateDistribution.Graduated;
    }

    /// <summary>Pins one faction's node count, overriding the distribution.</summary>
    public sealed class EstateFactionOverride
    {
        public string Tag;
        public int Nodes;
    }

    /// <summary>
    /// The estate to seed once the bodies exist: how much territory each faction starts with, and
    /// where it may go.
    ///
    /// <para><b>SUPERSEDED by <see cref="FactionRecipe"/> (operator direction, 2026-08-26) — do not
    /// build a seeder against this.</b> It says how <i>many</i> nodes a faction gets; the estate brief
    /// needs <i>where</i>, per faction, per body, and none of that is a count or a single volume.
    /// The argument recorded here — that one authored file should describe the world — lost to the
    /// observation that factions are not the world: the same bodies should be able to take a
    /// different faction set. Nothing in production reads this block, and the live recipe authors
    /// none. Kept only so an existing file still parses; retire it with FAC-105.</para>
    /// </summary>
    public sealed class WorldEstate
    {
        /// <summary>Where estates may be seeded. Null inherits the recipe's own
        /// <see cref="WorldRecipe.Volume"/>.</summary>
        public WorldVolume Volume;

        public EstateNodeCount NodesPerFaction = new EstateNodeCount();

        /// <summary>Optional per-tag pins. Any faction not named here takes the distribution.</summary>
        public List<EstateFactionOverride> Factions = new List<EstateFactionOverride>();

        /// <summary>Structural validation. Returns false with a reason rather than throwing, matching
        /// how the rest of the recipe reports a refusal.</summary>
        public bool Validate(out string why)
        {
            EstateNodeCount counts = NodesPerFaction;
            if (counts == null) { why = "Estate.NodesPerFaction is missing."; return false; }
            if (counts.Min <= 0) { why = "Estate.NodesPerFaction.Min must be greater than zero."; return false; }
            if (counts.Max < counts.Min) { why = "Estate.NodesPerFaction.Max must be at least Min."; return false; }

            if (Factions != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (EstateFactionOverride f in Factions)
                {
                    if (f == null || string.IsNullOrEmpty(f.Tag)) { why = "an Estate.Factions entry has no Tag."; return false; }
                    if (!seen.Add(f.Tag)) { why = "duplicate Estate.Factions entry for '" + f.Tag + "'."; return false; }
                    if (f.Nodes <= 0) { why = "Estate override for '" + f.Tag + "' must ask for at least one node."; return false; }
                }
            }

            if (Volume != null && Volume.ExtentKm <= 0) { why = "Estate.Volume.ExtentKm must be greater than zero."; return false; }
            why = null;
            return true;
        }
    }

    /// <summary>
    /// A world feature as authored in the recipe. Identical to <see cref="WorldFeature"/> except
    /// that it anchors by <b>recipe planet name</b> rather than by SE EntityId — the id does not
    /// exist until the planet has been created, and it is the thing that rots across a regenerate.
    /// </summary>
    public sealed class RecipeFeature
    {
        public string Name;
        public WorldFeatureShape Shape = WorldFeatureShape.Sphere;

        /// <summary>Name of a planet in this same recipe. A name that is not in the recipe is a
        /// plan-time error, not a boot-time warning.</summary>
        public string AnchorPlanet;

        /// <summary>Literal centre, used when there is no anchor.</summary>
        public Vec3 Center;

        public Vec3 Normal = new Vec3(0, 1, 0);
        public double InnerRadiusKm;
        public double OuterRadiusKm;
        public double HalfThicknessKm;

        /// <summary>Packing, 0..1. See <see cref="WorldFeature.Density"/>.</summary>
        public double Density;

        /// <summary>The <see cref="NodeZone"/> nodes inside this region belong to. Null defers to
        /// gravity classification. Zone's remaining job is separation, so this authors how densely
        /// the region may be settled — see <see cref="WorldFeature.Zone"/>.</summary>
        public NodeZone? Zone;

        /// <summary>Minimum km between two nodes inside this region. Absent falls back to the zone
        /// floor, which for deep space is 500 km.
        ///
        /// <para>This is what makes a feature a <i>place</i> rather than a coordinate: 50 km inside a
        /// worked industrial region against 500 km in the open space around it reads as a tenfold
        /// change in density at the boundary. Author it on any region meant to be settled — and
        /// <b>do not let open deep space inherit it</b>, or the whole sector becomes as dense as a
        /// worksite.</para></summary>
        public double? NodeSeparationKm;

        /// <summary>True when a respawn ship arriving in space may be placed in this region. See
        /// <see cref="WorldFeature.SpaceRespawn"/>.</summary>
        public bool SpaceRespawn;

        public List<FeatureBand> Bands = new List<FeatureBand>();

        /// <summary>Composition. Null is no ore opinion — the region governs placement only and the
        /// reader beneath it answers for ore; an empty list authors a barren region that shadows it.
        /// See <see cref="WorldFeature.Materials"/>.</summary>
        public List<FeatureMaterial> Materials;
    }

    /// <summary>
    /// The authored description of a world: what bodies exist, where they may go, and what features
    /// hang off them. Parsed in Core because it is pure JSON over pure data, and because a
    /// misparsed recipe should be catchable by a unit test rather than by a half-built world.
    /// </summary>
    public sealed class WorldRecipe
    {
        public const string FileName = "world_recipe.json";

        /// <summary>Seeds every derived decision: placement draws and per-planet terrain seeds.
        /// The same recipe and seed produce the same world.</summary>
        public int WorldSeed;

        public WorldVolume Volume = new WorldVolume();
        public WorldPlacementRules Placement = new WorldPlacementRules();

        /// <summary>Orbital layout. Absent falls back to uniform sampling inside
        /// <see cref="Volume"/>, which makes isolated worlds rather than a system.</summary>
        public WorldLayout Layout;

        /// <summary>World-storage folder of the Real Gas Giants mod, where its config lives. Kept in
        /// the authored file rather than in code so the mod id is data — the builder writes a setup
        /// file, it does not hold a compiled dependency on one mod's identity.</summary>
        public string GasGiantConfigFolder = "3232085677.sbm_RealGasGiants";
        public List<RecipePlanet> Planets = new List<RecipePlanet>();
        public List<RecipeFeature> Features = new List<RecipeFeature>();

        /// <summary>Optional. Null means no estate is seeded — planets and features alone are a
        /// legitimate world. Read by the estate-seeding stage, not by <see cref="WorldPlanner"/>.</summary>
        public WorldEstate Estate;

        /// <summary>
        /// Parses <paramref name="json"/>. Unlike <see cref="WorldFeatureFile"/> — which degrades to
        /// blank because a quiet deep space is recoverable — a bad recipe returns <c>null</c> with a
        /// reason. Half-applying a world is not recoverable, so nothing proceeds on a guess.
        /// </summary>
        public static WorldRecipe Parse(string json, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(json)) { error = FileName + ": empty."; return null; }

            WorldRecipe recipe;
            try
            {
                recipe = JsonConvert.DeserializeObject<WorldRecipe>(json);
            }
            catch (Exception ex)
            {
                error = FileName + ": unreadable -- " + ex.Message;
                return null;
            }

            if (recipe == null) { error = FileName + ": parsed to nothing."; return null; }
            if (recipe.Volume == null) recipe.Volume = new WorldVolume();
            if (recipe.Placement == null) recipe.Placement = new WorldPlacementRules();
            if (recipe.Planets == null) recipe.Planets = new List<RecipePlanet>();
            if (recipe.Features == null) recipe.Features = new List<RecipeFeature>();
            return recipe;
        }

        /// <summary>The volume estates may be seeded in: the estate's own, or the recipe's.</summary>
        public WorldVolume EstateVolume()
        {
            if (Estate != null && Estate.Volume != null) return Estate.Volume;
            return Volume;
        }

        /// <summary>
        /// A stable string hash, so a name-derived planet seed is the same on every runtime and
        /// every release. <see cref="string.GetHashCode()"/> is explicitly not stable across
        /// processes, which would make "the same recipe builds the same world" quietly false.
        /// FNV-1a, 32-bit.
        /// </summary>
        public static int StableHash(string s)
        {
            unchecked
            {
                uint hash = 2166136261;
                if (!string.IsNullOrEmpty(s))
                    for (int i = 0; i < s.Length; i++)
                    {
                        hash ^= s[i];
                        hash *= 16777619;
                    }
                return (int)(hash & 0x7FFFFFFF);
            }
        }

        /// <summary>The terrain seed for a body: the authored one, or one derived from the world
        /// seed and the body's name.</summary>
        public static int ResolveSeed(int worldSeed, RecipePlanet planet)
        {
            if (planet == null) return worldSeed;
            if (planet.Seed != 0) return planet.Seed;
            return StableHash(worldSeed.ToString() + "/" + (planet.Name ?? string.Empty));
        }

        /// <summary>SE's own storage-name convention (<c>MyVoxelMaps.SpawnPlanet</c>):
        /// <c>&lt;name&gt;-&lt;seed&gt;d&lt;diameter-in-metres&gt;</c>. Must be unique per body.</summary>
        public static string StorageName(RecipePlanet planet, int resolvedSeed)
        {
            string name = planet == null || string.IsNullOrEmpty(planet.Name) ? "Planet" : planet.Name;
            double diameterM = planet == null ? 0 : planet.DiameterKm * 1000.0;
            return name + "-" + resolvedSeed.ToString() + "d" + ((long)Math.Round(diameterM)).ToString();
        }
    }
}
