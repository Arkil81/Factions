using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.Entities;
using VRage.Game;
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// SE-backed <see cref="IOreGeography"/> for planets: answers what ore is actually in the ground
    /// by sampling the body's own ore cubemap.
    ///
    /// <para>SE places planet ore deterministically. Each entry in
    /// <c>MyPlanetGeneratorDefinition.OreMappings</c> binds a byte value to a voxel material and a
    /// depth band; the planet's ore cubemap holds that byte per texel. So "what ore is here" is a
    /// texture read, not voxel data — it cannot reach <c>MyStorageBase.Load</c> and therefore cannot
    /// trigger the synchronous <c>Loading voxel '&lt;path&gt;.vx2'</c> stall. The maps are decoded once at
    /// planet construction and pinned by <c>MyHeightMapLoadingSystem</c>, so sampling allocates
    /// nothing and touches no disk.</para>
    ///
    /// <para>Deep space is not handled here — there is no cubemap to read, and it is answered by
    /// authored world features instead. See docs/ore-geography.md.</para>
    ///
    /// <para>All positions exchanged with Core are in km; SE world coords are in metres.</para>
    /// </summary>
    internal sealed class SeOreGeography : IOreGeography
    {
        /// <summary>Index of the ore map in <c>MyPlanetMaterialProvider.Maps</c>. SE's own
        /// <c>m_oreMap = maps[2]</c>; the array is material/biome/ore/occlusion.</summary>
        private const int OreMapIndex = 2;

        /// <summary>One ore band as the sample loop needs it, decoded once per planet.</summary>
        private struct OreBand
        {
            public string SubTypeId;    // bare SE ore subtype, e.g. "Iron"
            public double BandDepthM;
            public double OverburdenM;
            public double YieldRatio;   // the band material's MinedOreRatio
        }

        private readonly double _oreSpacingM;
        private readonly double _surfaceSpacingM;
        private readonly int _oreMaxPoints;
        private readonly int _surfaceMaxPoints;
        private readonly double _surfaceLayerDepthM;
        private readonly HashSet<string> _ubiquitous;
        private readonly SePlanetScatter _scatter;

        /// <summary>Per-planet ore-map byte -&gt; band. Built on first use and kept: a planet's
        /// generator cannot change within a session.</summary>
        private readonly Dictionary<long, Dictionary<byte, OreBand>> _bandsByPlanet =
            new Dictionary<long, Dictionary<byte, OreBand>>();

        /// <param name="oreSpacingM">Metres between ore-map samples. Counts are derived from this and
        /// the area actually covered, which is the whole point: a fixed count against a variable area
        /// samples the biggest thing most thinly.</param>
        /// <param name="surfaceSpacingM">Metres between surface-material samples. Two orders of
        /// magnitude coarser because the features differ in scale by that much.</param>
        /// <param name="oreMaxPoints">Ceiling on derived ore-probe counts; area grows as the square of
        /// the radius, so without one a large catchment is unbounded work.</param>
        /// <param name="surfaceMaxPoints">Ceiling on derived surface-probe counts. Lower than the ore
        /// ceiling because a surface probe costs microseconds where an ore-map read costs nanoseconds.</param>
        /// <param name="surfaceLayerDepthM">Nominal thickness credited to a surface layer. A layer's
        /// real depth lives in the planet's material rules and is not exposed here, so this is an
        /// explicit tunable rather than a number pretending to be read from the world.</param>
        /// <param name="ubiquitousOres">Ores that are everywhere and must not be credited from the
        /// surface — stone, in practice. They already have an effort-gated <c>MineOnly</c> row in the
        /// node-kind table (FAC-050); counting them here would flood every deposit with the one ore
        /// whose whole design is that it is not a deposit.</param>
        public SeOreGeography(double oreSpacingM = 92.0, double surfaceSpacingM = 2000.0,
            int oreMaxPoints = 65536, int surfaceMaxPoints = 4096,
            double surfaceLayerDepthM = 8.0, IEnumerable<string> ubiquitousOres = null)
        {
            _oreSpacingM = Math.Max(0.001, oreSpacingM);
            _surfaceSpacingM = Math.Max(0.001, surfaceSpacingM);
            _oreMaxPoints = Math.Max(1, oreMaxPoints);
            _surfaceMaxPoints = Math.Max(0, surfaceMaxPoints);
            _surfaceLayerDepthM = surfaceLayerDepthM;
            _ubiquitous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ubiquitousOres != null)
                foreach (string o in ubiquitousOres) if (!string.IsNullOrEmpty(o)) _ubiquitous.Add(o);
            _scatter = new SePlanetScatter(_ubiquitous);
        }

        /// <summary>Points needed to cover <paramref name="areaM2"/> at <paramref name="spacingM"/>
        /// between samples, floored at 8 so a tiny body still gets a usable read and capped so a huge
        /// one cannot run away. <b>This is the whole fix for FAC-098</b>: the count follows the area,
        /// so raising a catchment radius covers more ground instead of covering the same ground more
        /// thinly.</summary>
        private static int PointsFor(double areaM2, double spacingM, int maxPoints)
        {
            if (maxPoints <= 0 || areaM2 <= 0) return 0;
            double raw = areaM2 / (spacingM * spacingM);
            if (raw <= 8) return Math.Min(8, maxPoints);
            return raw >= maxPoints ? maxPoints : (int)Math.Round(raw);
        }

        /// <summary>The coverage half of <see cref="TryResample"/> with the probing removed: the same
        /// closest-planet, gravity-well and ore-map tests, stopping before a single texel is read.
        /// <para>Kept beside them deliberately. If the containment rule here and the one there ever
        /// diverge, a placement loop would rank a position the sampler then declines — so they are one
        /// sequence of tests written twice rather than two ideas, and the tests pin that.</para></summary>
        public OreProspect CouldBearOre(Vec3 posKm)
        {
            Vector3D worldPos = ToM(posKm);
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(worldPos);
            if (planet == null) return OreProspect.Unknown;

            Vector3D centre = planet.PositionComp.GetPosition();
            double distM = (worldPos - centre).Length();
            if (!IsWithinGravityWell(planet, distM)) return OreProspect.Unknown;
            if (distM <= 1.0) return OreProspect.Unknown;

            MyPlanetStorageProvider provider = planet.Provider;
            if (provider == null || provider.Closed || provider.Material == null) return OreProspect.Unknown;

            MyCubemap[] maps = provider.Material.Maps;
            if (maps == null || maps.Length <= OreMapIndex || maps[OreMapIndex] == null) return OreProspect.None;
            return BandsFor(planet).Count > 0 ? OreProspect.Possible : OreProspect.None;
        }

        public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
        {
            double catchmentM0 = radiusKm * 1000.0;
            int firstPass = PointsFor(Math.PI * catchmentM0 * catchmentM0, _oreSpacingM, _oreMaxPoints);
            return TryResample(posKm, radiusKm, 0, firstPass, out sample);
        }

        /// <summary>Continues the survey from <paramref name="startIndex"/> for
        /// <paramref name="count"/> more points. Both entry points run this, so the first sample and
        /// every later refinement walk one sequence — a node's point 0 is point 0 forever.</summary>
        public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
        {
            sample = null;
            if (radiusKm <= 0) return false;

            Vector3D worldPos = ToM(posKm);
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(worldPos);
            if (planet == null) return false;

            // GetClosestPlanet returns the nearest body however far away, so an unguarded call would
            // let a deep-space node sample a planet thousands of km distant.
            Vector3D centre = planet.PositionComp.GetPosition();
            Vector3D fromCentre = worldPos - centre;
            double distM = fromCentre.Length();
            if (!IsWithinGravityWell(planet, distM)) return false;
            if (distM <= 1.0) return false; // at the core, no surface direction exists

            MyPlanetStorageProvider provider = planet.Provider;
            if (provider == null || provider.Closed || provider.Material == null) return false;

            MyCubemap[] maps = provider.Material.Maps;
            Dictionary<byte, OreBand> bands = BandsFor(planet);

            var result = new OreSample();

            // A body with no ore map, or none of whose mappings resolve to a mineable material, is
            // genuinely barren rather than unsamplable -- the gas-giant case. That is a real answer
            // and the caller must be able to tell it apart from "no body here".
            if (maps == null || maps.Length <= OreMapIndex || maps[OreMapIndex] == null || bands.Count == 0)
            {
                result.PointsExamined = 0;
                sample = result;
                return true;
            }

            MyCubemap oreMap = maps[OreMapIndex];

            Vector3D up = fromCentre / distM;
            Vector3D tangentA, tangentB;
            BuildTangentBasis(up, out tangentA, out tangentB);

            // Small-angle: a catchment of r on a body of radius R subtends about r/R.
            double surfaceRadiusM = Math.Max(1.0, planet.AverageRadius);
            double angularRadius = Math.Min(Math.PI * 0.5, (radiusKm * 1000.0) / surfaceRadiusM);

            var hits = new Dictionary<byte, int>();
            int examined = 0;

            int orePoints = Math.Max(0, count);

            for (int i = 0; i < orePoints; i++)
            {
                // Progressive lattice: point (startIndex + i) is a fixed place regardless of how many
                // points are ever taken, which is what lets a survey resume and accumulate. The old
                // sunflower divided by the total and so moved every point whenever the total grew.
                double dx, dy;
                ProgressiveLattice.PointOnDisc(startIndex + i, angularRadius, out dx, out dy);

                Vector3D dir = up + (tangentA * dx) + (tangentB * dy);
                dir.Normalize();

                var local = new Vector3((float)dir.X, (float)dir.Y, (float)dir.Z);
                int face;
                Vector2 texcoord;
                MyCubemapHelpers.CalculateSampleTexcoord(ref local, out face, out texcoord);
                if (face < 0 || face >= oreMap.Faces.Length) continue;

                examined++;
                byte oreValue = oreMap.Faces[face].GetValue(texcoord.X, texcoord.Y);
                if (oreValue == 0) continue;          // 0 is "no vein", the overwhelming majority
                if (!bands.ContainsKey(oreValue)) continue;

                int seen;
                hits[oreValue] = hits.TryGetValue(oreValue, out seen) ? seen + 1 : 1;
            }

            result.PointsExamined = examined;
            foreach (var kv in hits)
            {
                OreBand band = bands[kv.Key];
                result.Ores.Add(new OreObservation(band.SubTypeId, kv.Value, band.BandDepthM, band.OverburdenM,
                    band.YieldRatio, OreSource.Band));
            }

            AddSurfaceMaterials(planet, result, up, tangentA, tangentB, angularRadius, examined, startIndex);

            sample = result;
            return true;
        }

        public IEnumerable<string> MintableOres
        {
            get
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                // WorldBuilder.LivePlanets, not MyEntities.GetEntities -- see FAC-265.
                foreach (MyPlanet planet in WorldBuilder.LivePlanets())
                {
                    foreach (var kv in BandsFor(planet))
                        if (seen.Add(kv.Value.SubTypeId)) yield return kv.Value.SubTypeId;
                    // Layers and scatter are ore a player drills on this body as surely as a band;
                    // a recipe whose input only comes from them is still reachable.
                    foreach (string ore in SurfaceOresFor(planet))
                        if (seen.Add(ore)) yield return ore;
                    foreach (string ore in _scatter.OresOn(planet))
                        if (seen.Add(ore)) yield return ore;
                }
            }
        }

        /// <summary>
        /// Credits mineable <b>surface</b> materials and the ore the environment scatters on them —
        /// two SE systems beside the ore map, both invisible to the ore cubemap.
        ///
        /// <para>A body can carry a material a player drills at the surface while its
        /// <c>&lt;OreMappings&gt;</c> never mention it — Kor's 25 m ice sea is the case that found
        /// this. Reading only the ore map reproduces FAC-068's own defect by another route: the
        /// simulated map says no ice, the playable map has an ocean of it. See FAC-084.</para>
        ///
        /// <para><b>Every layer of the rule, at its own thickness.</b> SE resolves a surface point to
        /// a material rule whose layers stack downward, each <see
        /// cref="MyPlanetMaterialProvider.VoxelMaterial.Depth"/> the depth of its own floor. Each
        /// layer that mines to something other than a ubiquitous ore is credited as the difference to
        /// the floor above it, with that floor as its overburden — Tellus's lake ice is 20 m from the
        /// surface, a buried layer 30 m under sand is 30 m of overburden. Only a point the rule cannot
        /// answer for falls back to <c>surfaceLayerDepthM</c>.</para>
        ///
        /// <para><b>Scatter</b> — pillars and other environment voxel maps — is credited per point as
        /// the expected volume per square metre their spawn rule places there
        /// (<see cref="SePlanetScatter"/>), at zero overburden.</para>
        ///
        /// <para>Every observation carries its material's <c>MinedOreRatio</c>, so a 5x ice sheet
        /// and a 1x snowfield of the same ore are not the same ground.</para>
        ///
        /// <para>Sampled far coarser than the ore map and normalised back onto its budget, so every
        /// channel expresses the same quantity — fraction of the catchment covered — despite very
        /// different sample densities.</para>
        ///
        /// <para>Game thread only. <c>ComputeCombinedMaterialAndSurfaceExtended</c> reaches
        /// <c>GetMaterialForPositionDebug</c>, which mutates a private static blend cache on its
        /// material branch.</para>
        /// </summary>
        private void AddSurfaceMaterials(MyPlanet planet, OreSample result,
            Vector3D up, Vector3D tangentA, Vector3D tangentB, double angularRadius, int oreSamplesExamined,
            long surfaceStart)
        {
            if (_surfaceMaxPoints <= 0 || oreSamplesExamined <= 0) return;

            MyPlanetStorageProvider provider = planet.Provider;
            if (provider == null || provider.Closed) return;

            Vector3D centre = planet.PositionComp.GetPosition();
            var layerHits = new Dictionary<LayerKey, int>();
            var scatter = new Dictionary<KeyValuePair<string, double>, double>();
            var scatterHits = new Dictionary<KeyValuePair<string, double>, int>();
            var perPoint = new Dictionary<KeyValuePair<string, double>, double>();
            int surfaceExamined = 0;

            // Same catchment, its own spacing: a surface layer is a lake, not a texel.
            double catchM = Math.Sin(angularRadius) * Math.Max(1.0, planet.AverageRadius);
            int surfacePoints = PointsFor(Math.PI * catchM * catchM, _surfaceSpacingM, _surfaceMaxPoints);
            if (surfacePoints <= 0) return;

            for (int i = 0; i < surfacePoints; i++)
            {
                double dx, dy;
                ProgressiveLattice.PointOnDisc(surfaceStart + i, angularRadius, out dx, out dy);

                Vector3D dir = up + (tangentA * dx) + (tangentB * dy);
                dir.Normalize();

                // Storage-corner-relative, which is the space this API takes -- distinct from the
                // centre-relative space the cubemap helpers use.
                Vector3D world = SurfacePointAlong(planet, centre, dir);
                var local = (Vector3)(world - planet.PositionLeftBottomCorner);

                MyPlanetStorageProvider.SurfacePropertiesExtended props;
                provider.ComputeCombinedMaterialAndSurfaceExtended(local, out props);
                surfaceExamined++;

                // An ore-origin answer is already counted by the cubemap pass; crediting it again
                // would double-count exactly the positions both systems agree about.
                if (props.Origin == MyPlanetStorageProvider.SurfacePropertiesExtended.MaterialOrigin.Ore) continue;

                MyPlanetMaterialProvider.PlanetMaterial rule = props.EffectiveRule;
                if (rule != null) CreditLayers(rule, layerHits);
                else if (props.Material != null) Credit(props.Material, _surfaceLayerDepthM, 0.0, layerHits);

                // The spawn scan keys on the rule's top material and the biome map, and filters on the
                // same height, latitude, longitude and slope this probe just resolved.
                MyVoxelMaterialDefinition top = rule != null ? rule.FirstOrDefault : props.Material;
                if (top == null) continue;
                perPoint.Clear();
                _scatter.AddExpected(planet, props.BiomeValue, top.Index, props.HeightRatio,
                    props.Latitude, props.Longitude, props.Slope, dir, perPoint);
                foreach (var kv in perPoint)
                {
                    double cur;
                    scatter[kv.Key] = scatter.TryGetValue(kv.Key, out cur) ? cur + kv.Value : kv.Value;
                    int n;
                    scatterHits[kv.Key] = scatterHits.TryGetValue(kv.Key, out n) ? n + 1 : 1;
                }
            }

            if (surfaceExamined <= 0) return;

            double toOreBudget = (double)oreSamplesExamined / surfaceExamined;
            foreach (var kv in layerHits)
            {
                int normalised = (int)Math.Round(kv.Value * toOreBudget);
                if (normalised <= 0) continue;
                result.Ores.Add(new OreObservation(kv.Key.SubTypeId, normalised, kv.Key.ThicknessM,
                    kv.Key.OverburdenM, kv.Key.YieldRatio, OreSource.Layer));
            }
            foreach (var kv in scatter)
            {
                // Hits and thickness are chosen so their product is the scattered volume on the ore
                // budget exactly: a pillar's volume per square metre is far too small to survive the
                // rounding a hit count takes.
                double scaledHits = scatterHits[kv.Key] * toOreBudget;
                int hits = Math.Max(1, (int)Math.Round(scaledHits));
                double thickness = kv.Value * toOreBudget / hits;
                if (thickness <= 0) continue;
                result.Ores.Add(new OreObservation(kv.Key.Key, hits, thickness, 0.0, kv.Key.Value, OreSource.Scatter));
            }
        }

        /// <summary>One distinct layer a surface pass found: an ore, the material's yield, and where
        /// in the stack it sits.</summary>
        private struct LayerKey : IEquatable<LayerKey>
        {
            public string SubTypeId;
            public double ThicknessM;
            public double OverburdenM;
            public double YieldRatio;

            public bool Equals(LayerKey o)
            {
                return string.Equals(SubTypeId, o.SubTypeId, StringComparison.Ordinal)
                    && ThicknessM == o.ThicknessM && OverburdenM == o.OverburdenM && YieldRatio == o.YieldRatio;
            }

            public override bool Equals(object obj) { return obj is LayerKey && Equals((LayerKey)obj); }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = SubTypeId == null ? 0 : StringComparer.Ordinal.GetHashCode(SubTypeId);
                    h = h * 31 + ThicknessM.GetHashCode();
                    h = h * 31 + OverburdenM.GetHashCode();
                    return h * 31 + YieldRatio.GetHashCode();
                }
            }
        }

        /// <summary>Credits every ore-bearing layer of <paramref name="rule"/>. A layer's
        /// <c>Depth</c> is its floor below the surface, so its thickness is the step from the floor
        /// above; a rule without layers is one material down to its own <c>Depth</c>.</summary>
        private void CreditLayers(MyPlanetMaterialProvider.PlanetMaterial rule, Dictionary<LayerKey, int> hits)
        {
            if (!rule.HasLayers)
            {
                if (rule.Material != null) Credit(rule.Material, rule.Depth, 0.0, hits);
                return;
            }
            double above = 0;
            MyPlanetMaterialProvider.VoxelMaterial[] layers = rule.Layers;
            for (int i = 0; i < layers.Length; i++)
            {
                double floor = layers[i].Depth;
                if (floor > above && layers[i].Material != null)
                    Credit(layers[i].Material, floor - above, above, hits);
                if (floor > above) above = floor;
            }
        }

        private void Credit(MyVoxelMaterialDefinition material, double thicknessM, double overburdenM,
            Dictionary<LayerKey, int> hits)
        {
            string minedOre = material.MinedOre;
            if (string.IsNullOrEmpty(minedOre) || _ubiquitous.Contains(minedOre)) return;
            if (material.MinedOreRatio <= 0 || thicknessM <= 0) return;
            var key = new LayerKey
            {
                SubTypeId = minedOre, ThicknessM = thicknessM, OverburdenM = overburdenM,
                YieldRatio = material.MinedOreRatio,
            };
            int n;
            hits[key] = hits.TryGetValue(key, out n) ? n + 1 : 1;
        }

        /// <summary>One line per planet naming the ore that body actually carries, for the boot log,
        /// followed by one line per scattered voxel map that holds ore.
        /// <para>This is the "ore-per-planet coverage" instrument docs/mod-compatibility.md asks for:
        /// on a world whose bodies differ, it is the difference between an operator seeing the ore
        /// geography and discovering it from a faction that cannot find platinum. A body reporting
        /// no ore is the interesting case, not an error.</para></summary>
        public IEnumerable<string> PlanetOreSummary()
        {
            // WorldBuilder.LivePlanets, not MyEntities.GetEntities -- see FAC-265.
            foreach (MyPlanet planet in WorldBuilder.LivePlanets())
            {
                var ores = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in BandsFor(planet))
                    if (seen.Add(kv.Value.SubTypeId)) ores.Add(kv.Value.SubTypeId);
                ores.Sort(StringComparer.Ordinal);

                string generator = planet.Generator == null ? "Unknown" : planet.Generator.Id.SubtypeName;
                string name = (planet.DisplayName ?? "Planet") + " [" + generator + "]";

                // Surface materials and scatter are separate SE systems from ore mappings, reported
                // separately so the three are never conflated.
                var surface = new List<string>(SurfaceOresFor(planet));
                surface.Sort(StringComparer.Ordinal);
                var scattered = new List<string>(_scatter.OresOn(planet));
                scattered.Sort(StringComparer.Ordinal);

                yield return name + " "
                             + (ores.Count == 0 ? "carries NO ore mapping" : string.Join(", ", ores.ToArray()))
                             + (surface.Count == 0 ? "" : "  |  surface: " + string.Join(", ", surface.ToArray()))
                             + (scattered.Count == 0 ? "" : "  |  scatter: " + string.Join(", ", scattered.ToArray()));

                string status;
                var items = new List<SePlanetScatter.ItemReport>(_scatter.ReportFor(planet, out status));
                yield return name + " " + status;
                foreach (SePlanetScatter.ItemReport r in items)
                    yield return string.Format("{0} scatter {1}: {2} {3:N1} m3 per spawn, {4:N0} m2 footprint",
                        name, r.Item, r.SubTypeId, r.CubicMetres, r.FootprintM2);
            }
        }

        /// <summary>Per planet: every ore a material-rule layer on it mines to, from the whole-body
        /// sweep. Cached, because <see cref="MintableOres"/> asks for it and the sweep is thousands of
        /// surface probes.</summary>
        private readonly Dictionary<long, List<string>> _surfaceOresByPlanet = new Dictionary<long, List<string>>();

        /// <summary>Mineable layer materials on this body, any depth, sampled over the whole sphere on
        /// a golden-angle spread. The per-node answer comes from <see cref="AddSurfaceMaterials"/>
        /// against that node's own catchment.</summary>
        private List<string> SurfaceOresFor(MyPlanet planet)
        {
            List<string> cached;
            if (_surfaceOresByPlanet.TryGetValue(planet.EntityId, out cached)) return cached;

            var seen = new List<string>();
            MyPlanetStorageProvider provider = planet.Provider;
            if (provider == null || provider.Closed || _surfaceMaxPoints <= 0) return seen;

            Vector3D centre = planet.PositionComp.GetPosition();

            // Whole-body reconnaissance at the same spacing as a catchment sample, so the biggest
            // body is no longer the worst-read one. Measured before this: 256 fixed probes gave Kor
            // 4.4 km^2 each and Tellus 44.2 -- the instrument was least sensitive where there was
            // most surface to miss.
            double bodyM = Math.Max(1.0, planet.AverageRadius);
            int probes = PointsFor(4.0 * Math.PI * bodyM * bodyM, _surfaceSpacingM, _surfaceMaxPoints);
            var hits = new Dictionary<LayerKey, int>();
            for (int i = 0; i < probes; i++)
            {
                double sx, sy, sz;
                ProgressiveLattice.PointOnSphere(i, out sx, out sy, out sz);
                var dir = new Vector3D(sx, sy, sz);

                Vector3D world = SurfacePointAlong(planet, centre, dir);
                var local = (Vector3)(world - planet.PositionLeftBottomCorner);

                MyPlanetStorageProvider.SurfacePropertiesExtended props;
                provider.ComputeCombinedMaterialAndSurfaceExtended(local, out props);

                if (props.Origin == MyPlanetStorageProvider.SurfacePropertiesExtended.MaterialOrigin.Ore) continue;
                if (props.EffectiveRule != null) CreditLayers(props.EffectiveRule, hits);
                else if (props.Material != null) Credit(props.Material, _surfaceLayerDepthM, 0.0, hits);
            }
            foreach (var kv in hits)
                if (!seen.Contains(kv.Key.SubTypeId)) seen.Add(kv.Key.SubTypeId);
            _surfaceOresByPlanet[planet.EntityId] = seen;
            return seen;
        }

        /// <summary>Decode table for one planet: ore-map byte -&gt; the ore it means and its band.
        /// Cached per planet entity — a generator does not change within a session.</summary>
        private Dictionary<byte, OreBand> BandsFor(MyPlanet planet)
        {
            Dictionary<byte, OreBand> bands;
            if (_bandsByPlanet.TryGetValue(planet.EntityId, out bands)) return bands;

            bands = new Dictionary<byte, OreBand>();
            MyPlanetGeneratorDefinition generator = planet.Generator;
            MyPlanetOreMapping[] mappings = generator == null ? null : generator.OreMappings;

            if (mappings != null)
            {
                foreach (MyPlanetOreMapping mapping in mappings)
                {
                    if (mapping == null || string.IsNullOrEmpty(mapping.Type)) continue;

                    MyVoxelMaterialDefinition material =
                        MyDefinitionManager.Static.GetVoxelMaterialDefinition(mapping.Type);
                    if (material == null || string.IsNullOrEmpty(material.MinedOre)) continue;

                    bands[mapping.Value] = new OreBand
                    {
                        SubTypeId   = material.MinedOre,
                        BandDepthM  = mapping.Depth,
                        OverburdenM = mapping.Start,
                        YieldRatio  = material.MinedOreRatio,
                    };
                }
            }

            _bandsByPlanet[planet.EntityId] = bands;
            return bands;
        }

        /// <summary>Bare ore subtype to the mean <c>MinedOreRatio</c> of the asteroid-spawnable voxel
        /// materials that mine to it — what a feature rock authored as that ore gives up per cubic
        /// metre, since SE veins it with those materials (see FeatureRockMaterialControl). Read from
        /// the resolved definitions, so BetterStone's 1.4-8.8 minerals arrive as themselves.</summary>
        public static Dictionary<string, double> AsteroidYieldRatios()
        {
            var sums = new Dictionary<string, double>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MyVoxelMaterialDefinition m in MyDefinitionManager.Static.GetVoxelMaterialDefinitions())
            {
                if (m == null || !m.SpawnsInAsteroids || string.IsNullOrEmpty(m.MinedOre) || m.MinedOreRatio <= 0) continue;
                double s;
                sums[m.MinedOre] = sums.TryGetValue(m.MinedOre, out s) ? s + m.MinedOreRatio : m.MinedOreRatio;
                int c;
                counts[m.MinedOre] = counts.TryGetValue(m.MinedOre, out c) ? c + 1 : 1;
            }
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kv in sums) result[kv.Key] = kv.Value / counts[kv.Key];
            return result;
        }

        /// <summary>True when <paramref name="distM"/> is inside this planet's gravity well — the
        /// same test Core's zone classifier uses for "which body is this position's business".
        /// Falls back to a multiple of the surface radius on a body with no gravity component.</summary>
        private static bool IsWithinGravityWell(MyPlanet planet, double distM)
        {
            var gravComp = planet.Components.Get<MyGravityProviderComponent>();
            if (gravComp != null && gravComp.GravityLimit > 0) return distM <= gravComp.GravityLimit;
            return distM <= planet.AverageRadius * 2.0;
        }

        /// <summary>
        /// The point where <paramref name="dir"/> meets the planet's <b>actual terrain</b>, not its
        /// mean radius.
        ///
        /// <para>Probing at <c>AverageRadius</c> reads a point as far from the ground as the terrain's
        /// relief, and SE resolves a material by walking the selected rule's layers by depth — so an
        /// off-datum probe drops through the thin top band into whatever coarse layer sits beneath,
        /// or past every band into the planet's default material. Measured live: the operator's
        /// Tellus lake is 360 m above mean radius and resolved <c>Grass</c> there against
        /// <c>Ice_03</c> at the terrain.</para>
        ///
        /// <para><b>The bias is systematic, not noise.</b> Deep layers are disproportionately stone,
        /// and the bands worth finding are thin — a Kor snow/ice band is 3 m against a debris band of
        /// 500 m — so reading off-datum destroys exactly the materials this seam exists to find while
        /// preserving the ones it excludes anyway. See FAC-097.</para>
        /// </summary>
        private static Vector3D SurfacePointAlong(MyPlanet planet, Vector3D centre, Vector3D dir)
        {
            var centreLocal = (Vector3)(dir * planet.AverageRadius);
            Vector3 surfaceLocal = planet.GetClosestSurfacePointLocal(ref centreLocal);
            return centre + surfaceLocal;
        }

        /// <summary>Any two unit vectors perpendicular to <paramref name="up"/> and to each other.
        /// The choice of basis only rotates the lattice within the disc, which does not change the
        /// set of texels covered.</summary>
        private static void BuildTangentBasis(Vector3D up, out Vector3D a, out Vector3D b)
        {
            Vector3D seed = Math.Abs(up.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
            a = Vector3D.Normalize(Vector3D.Cross(up, seed));
            b = Vector3D.Cross(up, a);
        }

        private static Vector3D ToM(Vec3 km)
        {
            return new Vector3D(km.X * 1000.0, km.Y * 1000.0, km.Z * 1000.0);
        }
    }
}
