using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels;
using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.Game.WorldEnvironment.Definitions;
using VRage.Game;
using VRage.Utils;
using VRage.Voxels;
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// The ore a planet's environment scatters over its surface as small voxel maps — Tellus's
    /// silver pillars, the char-tree rocks, Kor's pipes — as an expected volume per square metre at a
    /// surface point. A third SE system beside the ore map and the material rules, and invisible to
    /// both: the ground under a pillar reads its rule's material, and the pillar is a separate voxel
    /// map SE builds near players and never saves untouched.
    ///
    /// <para>Read from the resolved definitions, never from a mod's files: the planet's
    /// <c>MyProceduralEnvironmentDefinition</c> (built from <c>&lt;EnvironmentItems&gt;</c> by SE's
    /// legacy converter, which halves a voxel map's density), each item's voxel-map collection and
    /// material modifier, and each storage's own voxels, loaded once and counted. The arithmetic that
    /// turns that into a volume per square metre is <see cref="ScatterMath"/>.</para>
    ///
    /// <para>Probabilities are read by sampling SE's own samplers at evenly spaced points rather than
    /// recomputed from densities, so the null filler a mapping carries when its densities sum below
    /// one, and any normalisation SE applies, come along without being re-derived here.</para>
    ///
    /// <para>Game thread only, like the rest of <see cref="SeOreGeography"/>.</para>
    /// </summary>
    internal sealed class SePlanetScatter
    {
        /// <summary>Evenly spaced sample positions used to read a sampler's distribution. 4096 gives
        /// any item of at least 0.03% probability a reading.</summary>
        private const int DistributionSamples = 4096;

        /// <summary>One ore one spawn from a mapping places, expected.</summary>
        internal struct OreVolume
        {
            public string SubTypeId;
            public double YieldRatio;
            public double CubicMetres;
        }

        private sealed class Mapping
        {
            public MyEnvironmentRule Rule;
            public List<OreVolume> PerSpawn;   // empty for a mapping that scatters no ore
            /// <summary>Chance one spawn from this mapping is a voxel map, ore or not: every one
            /// takes ground another would have used.</summary>
            public double VoxelShare;
            /// <summary>Sum over its voxel maps of chance times box footprint, square metres.</summary>
            public double FootprintShare;
        }

        /// <summary>What one spawn of a voxel-map collection places, expected over its storages and
        /// material options.</summary>
        private sealed class CollectionInfo
        {
            public readonly List<OreVolume> Ores = new List<OreVolume>();
            /// <summary>Mean box footprint, square metres; 0 when no storage could be read.</summary>
            public double FootprintM2;
        }

        private sealed class StorageInfo
        {
            public Dictionary<byte, double> Volumes;
            public double FootprintM2;
        }

        /// <summary>One voxel-map item's contribution to <see cref="PlanetOreSummary"/>.</summary>
        internal struct ItemReport
        {
            public string Item;
            public string SubTypeId;
            public double CubicMetres;
            public double FootprintM2;
        }

        private sealed class PlanetModel
        {
            public double ItemDensity;
            public readonly Dictionary<ushort, List<Mapping>> ByBiomeMaterial = new Dictionary<ushort, List<Mapping>>();
            public readonly List<ItemReport> Items = new List<ItemReport>();
            public string Status;
        }

        private readonly HashSet<string> _ubiquitous;
        private readonly Dictionary<long, PlanetModel> _planets = new Dictionary<long, PlanetModel>();
        private readonly Dictionary<string, StorageInfo> _storageVolumes =
            new Dictionary<string, StorageInfo>(StringComparer.Ordinal);

        public SePlanetScatter(HashSet<string> ubiquitousOres)
        {
            _ubiquitous = ubiquitousOres ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Adds the ore scattered around one surface point to <paramref name="into"/>, keyed by
        /// ore and yield, as cubic metres per square metre of sphere.
        /// <para><paramref name="biome"/>, <paramref name="materialIndex"/>, height, latitude,
        /// longitude and slope are the values SE's spawn scan keys and filters on
        /// (<c>MySurfaceParams</c>); <paramref name="direction"/> is the unit surface direction, whose
        /// largest component sets how much cube-face plane the point stands for.</para></summary>
        public void AddExpected(MyPlanet planet, byte biome, byte materialIndex, float heightRatio,
            float latitude, float longitude, float slope, Vector3D direction,
            Dictionary<KeyValuePair<string, double>, double> into)
        {
            PlanetModel model = ModelFor(planet);
            if (model.ItemDensity <= 0 || model.ByBiomeMaterial.Count == 0) return;

            List<Mapping> mappings;
            if (!model.ByBiomeMaterial.TryGetValue(Key(biome, materialIndex), out mappings)) return;

            int candidates = 0;
            double voxelShare = 0, footprintShare = 0;
            Dictionary<KeyValuePair<string, double>, double> sum = null;
            for (int i = 0; i < mappings.Count; i++)
            {
                Mapping m = mappings[i];
                if (m.Rule != null && !m.Rule.Check(heightRatio, latitude, longitude, slope)) continue;
                candidates++;
                voxelShare += m.VoxelShare;
                footprintShare += m.FootprintShare;
                for (int j = 0; j < m.PerSpawn.Count; j++)
                {
                    if (sum == null) sum = new Dictionary<KeyValuePair<string, double>, double>();
                    OreVolume v = m.PerSpawn[j];
                    var k = new KeyValuePair<string, double>(v.SubTypeId, v.YieldRatio);
                    double cur;
                    sum[k] = sum.TryGetValue(k, out cur) ? cur + v.CubicMetres : v.CubicMetres;
                }
            }
            if (sum == null || candidates == 0) return;

            double dominant = Math.Max(Math.Abs(direction.X), Math.Max(Math.Abs(direction.Y), Math.Abs(direction.Z)));
            double area = ScatterMath.PlanePerSphereArea(dominant);
            // Every voxel map at this point competes for the same ground, so one placed share
            // thins them all.
            double attempts = model.ItemDensity * ScatterMath.LodZeroShare * area * voxelShare / candidates;
            double placed = voxelShare > 0 ? ScatterMath.PlacedShare(attempts, footprintShare / voxelShare) : 1.0;
            foreach (var kv in sum)
            {
                double perM2 = ScatterMath.VolumePerSquareMetre(model.ItemDensity, candidates, kv.Value, area) * placed;
                if (perM2 <= 0) continue;
                double cur;
                into[kv.Key] = into.TryGetValue(kv.Key, out cur) ? cur + perM2 : perM2;
            }
        }

        /// <summary>Every ore this planet scatters, for <see cref="SeOreGeography.MintableOres"/>.</summary>
        public IEnumerable<string> OresOn(MyPlanet planet)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ItemReport r in ModelFor(planet).Items)
                if (seen.Add(r.SubTypeId)) yield return r.SubTypeId;
        }

        /// <summary>The boot-log half: one entry per voxel-map item that carries ore, with the ore and
        /// the expected cubic metres one spawn of it holds; plus what was read or why nothing was.</summary>
        public IEnumerable<ItemReport> ReportFor(MyPlanet planet, out string status)
        {
            PlanetModel model = ModelFor(planet);
            status = model.Status;
            return model.Items;
        }

        private static ushort Key(byte biome, byte material)
        {
            return (ushort)((biome << 8) | material);
        }

        private PlanetModel ModelFor(MyPlanet planet)
        {
            PlanetModel model;
            if (_planets.TryGetValue(planet.EntityId, out model)) return model;
            model = new PlanetModel();
            _planets[planet.EntityId] = model;
            try { Build(planet, model); }
            catch (Exception ex)
            {
                model.ByBiomeMaterial.Clear();
                model.Items.Clear();
                model.Status = "scatter unreadable: " + ex.GetType().Name + ": " + ex.Message;
            }
            return model;
        }

        private void Build(MyPlanet planet, PlanetModel model)
        {
            MyPlanetGeneratorDefinition generator = planet.Generator;
            var env = generator == null ? null : generator.EnvironmentDefinition as MyProceduralEnvironmentDefinition;
            if (env == null) { model.Status = "no procedural environment"; return; }
            if (env.MaterialEnvironmentMappings == null) { model.Status = "environment mappings not prepared"; return; }

            model.ItemDensity = env.ItemDensity;
            var perItem = new Dictionary<MyStringHash, CollectionInfo>();
            int mappingsWithOre = 0;

            foreach (var kv in env.MaterialEnvironmentMappings)
            {
                var list = new List<Mapping>();
                foreach (MyEnvironmentItemMapping mapping in kv.Value)
                {
                    if (mapping == null) continue;
                    var sampler = mapping.Sampler(0);
                    if (sampler == null) continue;   // SE would not offer this mapping at level 0 either

                    var m = new Mapping { Rule = mapping.Rule, PerSpawn = new List<OreVolume>() };
                    foreach (var item in Distribution(sampler))
                    {
                        MyRuntimeEnvironmentItemInfo info = item.Key;
                        if (info == null || info.Type == null || info.Type.Name != "VoxelMap") continue;

                        CollectionInfo collection;
                        if (!perItem.TryGetValue(info.Subtype, out collection))
                        {
                            collection = OresOfCollection(info.Subtype);
                            perItem[info.Subtype] = collection;
                            foreach (OreVolume o in collection.Ores)
                                model.Items.Add(new ItemReport { Item = info.Subtype.String, SubTypeId = o.SubTypeId, CubicMetres = o.CubicMetres, FootprintM2 = collection.FootprintM2 });
                        }
                        if (collection.FootprintM2 > 0)
                        {
                            m.VoxelShare += item.Value;
                            m.FootprintShare += item.Value * collection.FootprintM2;
                        }
                        foreach (OreVolume o in collection.Ores)
                            m.PerSpawn.Add(new OreVolume { SubTypeId = o.SubTypeId, YieldRatio = o.YieldRatio, CubicMetres = o.CubicMetres * item.Value });
                    }
                    if (m.PerSpawn.Count > 0) mappingsWithOre++;
                    list.Add(m);
                }
                // Mappings that scatter nothing are kept: SE picks among every matching mapping before
                // it samples one, so they dilute the ones that do.
                if (list.Count > 0) model.ByBiomeMaterial[Key(kv.Key.Biome, kv.Key.Material)] = list;
            }

            if (mappingsWithOre == 0) model.ByBiomeMaterial.Clear();
            model.Status = string.Format("scatter: {0} item(s) carrying ore, item density {1:0.#####}/m2",
                perItem.Count == 0 ? 0 : CountWithOre(perItem), env.ItemDensity);
        }

        private static int CountWithOre(Dictionary<MyStringHash, CollectionInfo> perItem)
        {
            int n = 0;
            foreach (var kv in perItem) if (kv.Value.Ores.Count > 0) n++;
            return n;
        }

        /// <summary>Expected ore one spawn of a voxel-map collection holds: over its storages by their
        /// probability, over its modifier's options by theirs, each storage's material volumes remapped
        /// by that option; and the mean footprint of the box SE tests for overlap.</summary>
        private CollectionInfo OresOfCollection(MyStringHash collectionId)
        {
            var result = new CollectionInfo();
            var collection = MyDefinitionManager.Static.GetDefinition<MyVoxelMapCollectionDefinition>(collectionId);
            if (collection == null || collection.StorageFiles == null) return result;

            // Option index -> (probability, change map). No modifier is a single identity option.
            var options = new List<KeyValuePair<double, Dictionary<byte, byte>>>();
            MyVoxelMaterialModifierDefinition modifier = collection.Modifier == MyStringHash.NullOrEmpty
                ? null
                : MyDefinitionManager.Static.GetDefinition<MyVoxelMaterialModifierDefinition>(collection.Modifier);
            if (modifier != null && modifier.Options != null)
            {
                var values = new List<VoxelMapChange>(modifier.Options);
                var counts = new int[values.Count];
                for (int s = 0; s < DistributionSamples; s++)
                {
                    int idx = IndexOf(values, modifier.Options.Sample((s + 0.5f) / DistributionSamples));
                    if (idx >= 0) counts[idx]++;
                }
                for (int i = 0; i < values.Count; i++)
                    if (counts[i] > 0)
                        options.Add(new KeyValuePair<double, Dictionary<byte, byte>>(
                            counts[i] / (double)DistributionSamples, values[i].Changes));
            }
            if (options.Count == 0) options.Add(new KeyValuePair<double, Dictionary<byte, byte>>(1.0, null));

            var byMaterial = new Dictionary<byte, double>();
            double footprint = 0, readable = 0;
            foreach (var storage in DistributionOfIds(collection.StorageFiles))
            {
                StorageInfo info = StorageVolumes(storage.Key.SubtypeName);
                if (info == null) continue;
                Dictionary<byte, double> volumes = info.Volumes;
                footprint += storage.Value * info.FootprintM2;
                readable += storage.Value;
                foreach (var option in options)
                    foreach (var mv in volumes)
                    {
                        byte to;
                        byte mat = option.Value != null && option.Value.TryGetValue(mv.Key, out to) ? to : mv.Key;
                        double cur;
                        byMaterial.TryGetValue(mat, out cur);
                        byMaterial[mat] = cur + mv.Value * storage.Value * option.Key;
                    }
            }

            var byOre = new Dictionary<KeyValuePair<string, double>, double>();
            foreach (var mv in byMaterial)
            {
                MyVoxelMaterialDefinition def = MyDefinitionManager.Static.GetVoxelMaterialDefinition(mv.Key);
                if (def == null || string.IsNullOrEmpty(def.MinedOre) || def.MinedOreRatio <= 0) continue;
                if (_ubiquitous.Contains(def.MinedOre)) continue;
                var k = new KeyValuePair<string, double>(def.MinedOre, def.MinedOreRatio);
                double cur;
                byOre[k] = byOre.TryGetValue(k, out cur) ? cur + mv.Value : mv.Value;
            }
            if (readable > 0) result.FootprintM2 = footprint / readable;
            foreach (var kv in byOre)
                result.Ores.Add(new OreVolume { SubTypeId = kv.Key.Key, YieldRatio = kv.Key.Value, CubicMetres = kv.Value });
            return result;
        }

        private static int IndexOf(List<VoxelMapChange> values, VoxelMapChange v)
        {
            for (int i = 0; i < values.Count; i++)
                if (ReferenceEquals(values[i].Changes, v.Changes)) return i;
            return -1;
        }

        /// <summary>Cubic metres of each material in one voxel-map storage, counted once from its own
        /// voxels: content is 0..255 per one-metre voxel; and the footprint of the box SE tests for
        /// overlap, the storage's size across its two horizontal axes (SE stands a map with its Y up).
        /// Null when the file cannot be read.</summary>
        private StorageInfo StorageVolumes(string storageSubtype)
        {
            StorageInfo volumes;
            if (_storageVolumes.TryGetValue(storageSubtype, out volumes)) return volumes;
            volumes = null;
            MyStorageBase storage = null;
            try
            {
                string path = MyWorldGenerator.GetVoxelPrefabPath(storageSubtype);
                storage = MyStorageBase.LoadFromFile(path, null, false, false);
                // Environment voxel maps are small; one this large is not a pillar, and reading it
                // whole would allocate tens of megabytes on the game thread.
                if (storage != null && (long)storage.Size.X * storage.Size.Y * storage.Size.Z > MaxStorageVoxels)
                    storage = CloseAndNull(storage);
                if (storage != null)
                {
                    var data = new MyStorageData();
                    data.Resize(storage.Size);
                    storage.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, 0, Vector3I.Zero, storage.Size - Vector3I.One);
                    var sums = new Dictionary<byte, long>();
                    for (int i = 0; i < data.SizeLinear; i++)
                    {
                        byte content = data.Content(i);
                        if (content == 0) continue;
                        byte mat = data.Material(i);
                        long cur;
                        sums[mat] = sums.TryGetValue(mat, out cur) ? cur + content : content;
                    }
                    volumes = new StorageInfo
                    {
                        Volumes = new Dictionary<byte, double>(),
                        FootprintM2 = (double)storage.Size.X * storage.Size.Z,
                    };
                    foreach (var kv in sums) volumes.Volumes[kv.Key] = kv.Value / 255.0;
                }
            }
            catch (Exception)
            {
                volumes = null;
            }
            finally
            {
                if (storage != null) storage.Close();
            }
            _storageVolumes[storageSubtype] = volumes;
            return volumes;
        }

        /// <summary>Largest storage counted, in voxels: 256 cubed.</summary>
        private const long MaxStorageVoxels = 256L * 256L * 256L;

        private static MyStorageBase CloseAndNull(MyStorageBase storage)
        {
            storage.Close();
            return null;
        }

        /// <summary>A sampler's distribution, read by sampling it at evenly spaced positions. Values
        /// the sampler returns as null (SE's filler for densities summing below one) are dropped, so
        /// the probabilities returned sum to the chance of getting a real item.</summary>
        private static List<KeyValuePair<T, double>> Distribution<T>(MyDiscreteSampler<T> sampler) where T : class
        {
            var counts = new Dictionary<T, int>();
            for (int s = 0; s < DistributionSamples; s++)
            {
                T v = sampler.Sample((s + 0.5f) / DistributionSamples);
                if (v == null) continue;
                int cur;
                counts[v] = counts.TryGetValue(v, out cur) ? cur + 1 : 1;
            }
            var result = new List<KeyValuePair<T, double>>(counts.Count);
            foreach (var kv in counts) result.Add(new KeyValuePair<T, double>(kv.Key, kv.Value / (double)DistributionSamples));
            return result;
        }

        private static List<KeyValuePair<MyDefinitionId, double>> DistributionOfIds(MyDiscreteSampler<MyDefinitionId> sampler)
        {
            var counts = new Dictionary<MyDefinitionId, int>();
            for (int s = 0; s < DistributionSamples; s++)
            {
                MyDefinitionId v = sampler.Sample((s + 0.5f) / DistributionSamples);
                int cur;
                counts[v] = counts.TryGetValue(v, out cur) ? cur + 1 : 1;
            }
            var result = new List<KeyValuePair<MyDefinitionId, double>>(counts.Count);
            foreach (var kv in counts) result.Add(new KeyValuePair<MyDefinitionId, double>(kv.Key, kv.Value / (double)DistributionSamples));
            return result;
        }
    }
}
