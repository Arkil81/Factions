using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Factions.Core;
using Newtonsoft.Json;
using Sandbox.Definitions;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// Host-side prefab book-value cache.
    ///
    /// Fleet values are persisted back into <c>fleet_prefabs.json</c> alongside the existing kind →
    /// personality → prefab lists. Station values are stored in a dedicated <c>station_prefabs.json</c>
    /// sidecar because they are only used as collateral metadata.
    /// </summary>
    internal sealed class PrefabBookValueCache
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
        };

        // All vanilla SE economy station prefabs. Seeded statically so book values are computed
        // even when SE reflection doesn't surface every name via GetStationTypeDefinition.
        internal static readonly string[] KnownStationPrefabs =
        {
            "Economy_SpaceStation_10", "Economy_SpaceStation_11", "Economy_SpaceStation_11_Pirate",
            "Economy_SpaceStation_12", "Economy_SpaceStation_13",
            "Economy_SpaceStation_14", "Economy_SpaceStation_14_Pirate",
            "Economy_SpaceStation_15", "Economy_SpaceStation_15_Pirate",
            "Economy_Outpost_10",
            "Economy_Outpost_11", "Economy_Outpost_11_Pirate",
            "Economy_Outpost_12", "Economy_Outpost_12_Pirate",
            "Economy_Outpost_13", "Economy_Outpost_13_Pirate",
            "Economy_Outpost_14",
        };


        private readonly string _fleetPath;
        private readonly string _stationPath;
        private readonly string _stationCombatPath;
        private readonly Dictionary<string, long> _stationValues = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<WeaponBand>> _stationWeaponBands = new Dictionary<string, List<WeaponBand>>(StringComparer.OrdinalIgnoreCase);

        // Station prefabs the catalogue attributes to the Factorum -- derived, never listed. An
        // authored list would have to be re-edited every time SE ships or a mod adds Factorum
        // content, and the first version of it named the three gun platforms, which the spawn table
        // actually uses as SATELLITES of a logistics facility rather than as stations in their own
        // right. Reading the catalogue asks the question that was meant: which structures are
        // theirs.
        private readonly HashSet<string> _factorumStations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public FleetPrefabConfig FleetConfig { get; private set; }

        public int FleetValueCount
        {
            get { return FleetConfig != null && FleetConfig.BookValues != null ? FleetConfig.BookValues.Count : 0; }
        }

        public int StationValueCount
        {
            get { return _stationValues.Count; }
        }

        private PrefabBookValueCache(string fleetPath, string stationPath, string stationCombatPath, FleetPrefabConfig fleetConfig)
        {
            _fleetPath = fleetPath;
            _stationPath = stationPath;
            _stationCombatPath = stationCombatPath;
            FleetConfig = fleetConfig ?? FleetPrefabConfig.Defaults();
        }

        public long FleetBookValue(string prefabId)
        {
            long value;
            return FleetConfig != null && FleetConfig.BookValues != null && !string.IsNullOrEmpty(prefabId)
                && FleetConfig.BookValues.TryGetValue(prefabId, out value) ? value : 0;
        }

        public long StationBookValue(string prefabId)
        {
            long value;
            return !string.IsNullOrEmpty(prefabId) && _stationValues.TryGetValue(prefabId, out value) ? value : 0;
        }

        private static readonly List<WeaponBand> EmptyBands = new List<WeaponBand>();

        public IReadOnlyList<WeaponBand> StationWeaponBands(string prefabId)
        {
            List<WeaponBand> bands;
            return !string.IsNullOrEmpty(prefabId) && _stationWeaponBands.TryGetValue(prefabId, out bands) && bands != null
                ? bands : EmptyBands;
        }

        /// <summary>
        /// Every cached station prefab a founding of this environment and style may draw.
        /// <paramref name="style"/> is a <see cref="StationStyle"/> constant; the rule is
        /// <see cref="StationPrefabPool.Select"/>.
        /// </summary>
        public IList<string> GetStationPool(bool isSpace, string style)
        {
            return StationPrefabPool.Select(_stationValues.Keys, isSpace, style, IsFactorumStation, SeListedSites());
        }

        // SE's per-station-type lists, read once they answer. An empty read is not kept: the
        // definitions may not be loaded yet, and caching that would site nothing for the session.
        private Dictionary<string, string> _seListedSites;

        private Dictionary<string, string> SeListedSites()
        {
            if (_seListedSites != null) return _seListedSites;
            Dictionary<string, string> sites = StationFounder.GetKnownPrefabSites();
            if (sites.Count > 0) _seListedSites = sites;
            return sites;
        }

        /// <summary>Every station prefab the valuation walk resolved — the set
        /// <see cref="GetStationPool"/> draws its pools from, exposed whole for the boot pass that
        /// gives each one a refit recipe and a catalogue entry.</summary>
        public IEnumerable<string> StationPrefabIds
        {
            get { return _stationValues.Keys; }
        }

        /// <summary>True when the valuation walk priced a station prefab of this name. The
        /// membership test behind <see cref="StationPrefabIds"/>, for callers that only need to know
        /// whether an id is spoken for.</summary>
        public bool IsKnownStationPrefab(string prefabId)
        {
            return !string.IsNullOrEmpty(prefabId) && _stationValues.ContainsKey(prefabId);
        }

        /// <summary>True for a static structure the catalogue attributes to the Factorum. Derived,
        /// never listed — see <see cref="_factorumStations"/>.</summary>
        public bool IsFactorumStation(string prefabId)
        {
            return !string.IsNullOrEmpty(prefabId) && _factorumStations.Contains(prefabId);
        }

        public long BookValue(string prefabId)
        {
            long value = StationBookValue(prefabId);
            if (value > 0) return value;
            return FleetBookValue(prefabId);
        }

        public static PrefabBookValueCache Load(string storageDir, FleetPrefabConfig fleetConfig,
            IItemCatalogue itemCatalogue, IItemPriceCatalogue priceCatalogue, IItemRegistry itemRegistry,
            SePrefabCatalogue prefabCatalogue = null)
        {
            string fleetPath = Path.Combine(storageDir, "fleet_prefabs.json");
            string stationPath = Path.Combine(storageDir, "station_prefabs.json");
            string stationCombatPath = Path.Combine(storageDir, "station_combat.json");
            var missingPrices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var placeholderPrices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var cache = new PrefabBookValueCache(fleetPath, stationPath, stationCombatPath, fleetConfig);
            if (cache.FleetConfig.BookValues == null)
                cache.FleetConfig.BookValues = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, long> stationFileValues = LoadDictionary(stationPath);
            if (stationFileValues != null)
                foreach (var kv in stationFileValues)
                    if (kv.Value > 0)
                        cache._stationValues[kv.Key] = kv.Value;

            Dictionary<string, List<WeaponBand>> stationCombatFileValues = LoadBandsDictionary(stationCombatPath);
            if (stationCombatFileValues != null)
                foreach (var kv in stationCombatFileValues)
                    if (kv.Value != null && kv.Value.Count > 0)
                        cache._stationWeaponBands[kv.Key] = kv.Value;

            bool fleetDirty = false;
            bool stationDirty = false;
            bool stationCombatDirty = false;
            HashSet<string> fleetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string prefabId in cache.FleetConfig.AllPrefabIds())
                if (!string.IsNullOrEmpty(prefabId))
                    fleetIds.Add(prefabId);

            HashSet<string> stationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in KnownStationPrefabs) stationIds.Add(s);
            // Every static structure the catalogue attributes to a livery with its own station
            // family. Book values must be computed for these like any other station prefab, so they
            // join the id set before the valuation walk below.
            if (prefabCatalogue != null)
            {
                foreach (string prefabId in prefabCatalogue.Ids)
                {
                    if (!string.Equals(prefabCatalogue.FactionHint(prefabId), StationStyle.Factorum,
                                       StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(prefabCatalogue.GridSize(prefabId), "Station",
                                       StringComparison.OrdinalIgnoreCase)) continue;
                    stationIds.Add(prefabId);
                    cache._factorumStations.Add(prefabId);
                }
            }
            foreach (string prefabId in StationFounder.GetKnownPrefabNames())
                if (!string.IsNullOrEmpty(prefabId))
                    stationIds.Add(prefabId);

            if (MySession.Static != null && MySession.Static.Factions != null)
            {
                foreach (var factionPair in MySession.Static.Factions)
                {
                    MyFaction faction = factionPair.Value;
                    if (faction == null) continue;
                    foreach (var station in faction.Stations)
                    {
                        if (station == null || string.IsNullOrEmpty(station.PrefabName)) continue;
                        stationIds.Add(station.PrefabName);
                    }
                }
            }

            if (MyDefinitionManager.Static != null)
            {
                var prefabDefs = MyDefinitionManager.Static.GetPrefabDefinitions();

                foreach (string prefabId in fleetIds)
                {
                    long value = ComputePrefabBookValue(prefabDefs, prefabId, itemCatalogue, priceCatalogue, itemRegistry, missingPrices, placeholderPrices);
                    if (value <= 0) continue;
                    long existing;
                    if (!cache.FleetConfig.BookValues.TryGetValue(prefabId, out existing) || existing != value)
                    {
                        cache.FleetConfig.BookValues[prefabId] = value;
                        fleetDirty = true;
                    }
                }

                foreach (string prefabId in stationIds)
                {
                    long value = ComputePrefabBookValue(prefabDefs, prefabId, itemCatalogue, priceCatalogue, itemRegistry, missingPrices, placeholderPrices);
                    if (value <= 0) continue;
                    long existing;
                    if (!cache._stationValues.TryGetValue(prefabId, out existing) || existing != value)
                    {
                        cache._stationValues[prefabId] = value;
                        stationDirty = true;
                    }
                }

                foreach (string prefabId in stationIds)
                {
                    List<WeaponBand> bands = ComputeStationWeaponBands(prefabDefs, prefabId);
                    if (bands == null || bands.Count == 0) continue;
                    cache._stationWeaponBands[prefabId] = bands;
                    stationCombatDirty = true;
                }
            }

            if (fleetDirty || !File.Exists(fleetPath))
            {
                try
                {
                    Directory.CreateDirectory(storageDir);
                    File.WriteAllText(fleetPath, JsonConvert.SerializeObject(cache.FleetConfig, Settings));
                }
                catch { /* best-effort */ }
            }

            if (stationDirty || !File.Exists(stationPath))
            {
                try
                {
                    Directory.CreateDirectory(storageDir);
                    File.WriteAllText(stationPath, JsonConvert.SerializeObject(cache._stationValues, Settings));
                }
                catch { /* best-effort */ }
            }

            if (stationCombatDirty || !File.Exists(stationCombatPath))
            {
                try
                {
                    Directory.CreateDirectory(storageDir);
                    File.WriteAllText(stationCombatPath, JsonConvert.SerializeObject(cache._stationWeaponBands, Settings));
                }
                catch { /* best-effort */ }
            }

            if (placeholderPrices.Count > 0)
            {
                var phSample = placeholderPrices.Take(12).ToArray();
                FactionsPlugin.Log.Warn("Prefab valuation priced " + placeholderPrices.Count
                    + " component subtype(s) at their ItemKind placeholder -- no MinimalPricePerUnit and"
                    + " no blueprint to derive one, so every prefab containing them is undervalued: "
                    + string.Join(", ", phSample)
                    + (placeholderPrices.Count > phSample.Length ? " ..." : ""));
            }
            if (missingPrices.Count > 0)
            {
                var sample = missingPrices.Take(12).ToArray();
                FactionsPlugin.Log.Warn("Prefab valuation still lacks prices for " + missingPrices.Count
                    + " component subtype(s): " + string.Join(", ", sample)
                    + (missingPrices.Count > sample.Length ? " ..." : ""));
            }

            return cache;
        }

        private static Dictionary<string, long> LoadDictionary(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, long>>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, List<WeaponBand>> LoadBandsDictionary(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, List<WeaponBand>>>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static long ComputePrefabBookValue(IEnumerable<KeyValuePair<string, MyPrefabDefinition>> prefabDefs, string prefabId,
            IItemCatalogue itemCatalogue, IItemPriceCatalogue priceCatalogue, IItemRegistry itemRegistry,
            ISet<string> missingPrices = null,
            ISet<string> placeholderPrices = null)
        {
            if (prefabDefs == null || string.IsNullOrEmpty(prefabId)) return 0;
            MyPrefabDefinition prefab = null;
            foreach (var kv in prefabDefs)
            {
                if (!string.Equals(kv.Key, prefabId, StringComparison.OrdinalIgnoreCase)) continue;
                prefab = kv.Value;
                break;
            }
            if (prefab == null || prefab.CubeGrids == null)
                return 0;

            long total = 0;
            foreach (MyObjectBuilder_CubeGrid grid in prefab.CubeGrids)
            {
                if (grid == null || grid.CubeBlocks == null) continue;
                foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
                {
                    if (block == null) continue;
                    MyCubeBlockDefinition blockDef;
                    if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out blockDef)) continue;
                    if (blockDef == null || blockDef.Components == null) continue;

                    foreach (MyCubeBlockDefinition.Component component in blockDef.Components)
                    {
                        if (component.Count <= 0 || component.Definition == null) continue;
                        string subTypeId = component.Definition.Id.SubtypeName;
                        if (string.IsNullOrEmpty(subTypeId)) continue;

                        long unitPrice = 0;
                        if (priceCatalogue != null)
                            unitPrice = priceCatalogue.BasePrice(subTypeId);
                        if (unitPrice <= 0)
                        {
                            var physicalDef = component.Definition as MyPhysicalItemDefinition;
                            if (physicalDef != null && physicalDef.MinimalPricePerUnit > 0)
                                unitPrice = physicalDef.MinimalPricePerUnit;
                        }
                        if (unitPrice <= 0 && itemRegistry != null && itemRegistry.TryGetItemKind(subTypeId, out ItemKind kind)
                            && itemCatalogue != null)
                        {
                            // Kind placeholder, not a price for THIS item: a component with no
                            // MinimalPricePerUnit and no blueprint the price resolver could close
                            // over lands here and is valued as a generic member of its kind. Recorded
                            // separately because it is silent -- the sum comes out plausible and every
                            // hull carrying the component is undervalued by the difference.
                            unitPrice = itemCatalogue.CreditValuePer(kind);
                            if (unitPrice > 0 && placeholderPrices != null)
                                placeholderPrices.Add(subTypeId);
                        }
                        if (unitPrice <= 0)
                        {
                            // Distinct from the placeholder case: the item's ItemKind could not be
                            // resolved at all, so it contributes nothing to the total.
                            if (missingPrices != null)
                                missingPrices.Add(subTypeId);
                            continue;
                        }
                        total += (long)component.Count * unitPrice;
                    }
                }
            }

            return total;
        }

        /// <summary>Station-prefab counterpart to <see cref="PrefabCatalogueScanner.ScanPrefab"/>'s
        /// weapon-band computation — most station prefabs are unarmed civilian trade posts, but any
        /// that do carry weapon/turret blocks (e.g. a pirate-flagged station) get real Dps/range
        /// bands cached the same way ship prefabs do.</summary>
        private static List<WeaponBand> ComputeStationWeaponBands(
            IEnumerable<KeyValuePair<string, MyPrefabDefinition>> prefabDefs, string prefabId)
        {
            if (prefabDefs == null || string.IsNullOrEmpty(prefabId)) return null;
            MyPrefabDefinition prefab = null;
            foreach (var kv in prefabDefs)
            {
                if (!string.Equals(kv.Key, prefabId, StringComparison.OrdinalIgnoreCase)) continue;
                prefab = kv.Value;
                break;
            }
            if (prefab == null || prefab.CubeGrids == null) return null;

            var hullWeapons = new WeaponStatsHelper.HullWeaponPricer();
            foreach (MyObjectBuilder_CubeGrid grid in prefab.CubeGrids)
            {
                if (grid == null || grid.CubeBlocks == null) continue;
                foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
                {
                    if (block == null) continue;
                    MyCubeBlockDefinition blockDef;
                    if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out blockDef) || blockDef == null)
                        continue;

                    float blockDps, blockRangeM;
                    WeaponStatsHelper.ComputeWeaponStats(blockDef, out blockDps, out blockRangeM);
                    if (WeaponStatsHelper.HullWeaponPricer.Prices(blockDef, blockDps))
                        hullWeapons.Add(blockDef, blockDps, blockRangeM);
                }
            }

            var perBlockStats = new List<(float dps, float rangeM)>();
            foreach (var priced in hullWeapons.Finish())
                if (priced.dps > 0f) perBlockStats.Add((priced.dps, priced.rangeM));
            return WeaponStatsHelper.GroupIntoBands(perBlockStats);
        }
    }
}
