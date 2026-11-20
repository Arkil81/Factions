using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;
using Sandbox.Definitions;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// SE-backed <see cref="IItemRegistry"/> with a file cache for resilience against
    /// definition-manager timing issues at session load.
    ///
    /// On construction:
    ///   1. Load SubTypeId→ItemKind entries from <c>item_registry_cache.json</c> (if present).
    ///   2. Scan <see cref="MyDefinitionManager.Static"/> for all physical item definitions.
    ///   3. If the live scan returned anything: compute added/removed vs the file cache, save the
    ///      merged result back to disk.
    ///   4. If the live scan returned nothing (definition manager not yet populated): use the file
    ///      cache as-is and skip the save so a partial scan never erases good data.
    ///
    /// TypeId → ItemKind mapping table:
    ///   Ore                   → Ore
    ///   Ingot                 → Ingot  (fast-path in TryGetItemKind; "Ingot:" prefix not stripped by StripInvKeyPrefix)
    ///   Component             → ProcessedGood
    ///   ConsumableItem        → ProcessedGood  (food, medical; farming arch TBD)
    ///   PhysicalGunObject     → ProcessedGood  (weapons as manufactured goods)
    ///   GasContainerObject    → ProcessedGood  (H2 EVA bottles — player-facing gear, not sim fuel)
    ///   OxygenContainerObject → ProcessedGood  (O2 EVA bottles — same rationale)
    ///   AmmoMagazine          → ProcessedGood  (ammo as manufactured goods)
    ///   SeedItem              → ProcessedGood  (keyed "Seed:&lt;subtype&gt;" — a seed shares its crop's subtype)
    ///   PhysicalObject        → ProcessedGood  (Grain and Algae: crops that are not edible raw)
    ///
    /// A second table (<see cref="s_typeIdCategory"/>) further classifies ProcessedGood into
    /// <see cref="ProcessedGoodCategory"/> for store/production cap sizing — weapons, ammo, and
    /// consumables are sized very differently from bulk components.
    ///
    /// Intentionally unmapped:
    ///   Package        — ship prefab containers; special handling TBD
    ///   PhysicalObject/SpaceCredit — money, not an economy commodity
    ///
    /// Gases are not physical items and so never appear in the scan at all. They are handled by the
    /// "Gas:" fast path in <see cref="TryGetItemKind"/>, and they are the only members
    /// <see cref="ItemKind.Hydrogen"/> and <see cref="ItemKind.Oxygen"/> have — the bottles that used
    /// to stand in for them are assembler-built EVA gear and classify as ProcessedGood above.
    /// </summary>
    public sealed class SeItemRegistry : IItemRegistry
    {
        private const string CacheFileName = "item_registry_cache.json";

        // TypeId.ToString() → ItemKind. Single-pass scan: iterate all physical item defs once,
        // look up TypeId in this table. Unmapped TypeIds are silently skipped.
        private static readonly Dictionary<string, ItemKind> s_typeIdKind = new Dictionary<string, ItemKind>
        {
            { "MyObjectBuilder_Ore",                   ItemKind.Ore   },
            { "MyObjectBuilder_Ingot",                 ItemKind.Ingot },
            { "MyObjectBuilder_Component",             ItemKind.ProcessedGood },
            { "MyObjectBuilder_ConsumableItem",        ItemKind.ProcessedGood },
            { "MyObjectBuilder_PhysicalGunObject",     ItemKind.ProcessedGood },
            { "MyObjectBuilder_GasContainerObject",    ItemKind.ProcessedGood },
            { "MyObjectBuilder_OxygenContainerObject", ItemKind.ProcessedGood },
            { "MyObjectBuilder_AmmoMagazine",          ItemKind.ProcessedGood },
            { "MyObjectBuilder_SeedItem",              ItemKind.ProcessedGood },
            { "MyObjectBuilder_PhysicalObject",        ItemKind.ProcessedGood },
        };

        /// <summary>Subtypes of a mapped TypeId that are still not commodities. SpaceCredit is the
        /// physical form of money; Grain and Algae are the only other PhysicalObjects SE ships.</summary>
        private static readonly HashSet<string> s_excludedSubtypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "SpaceCredit",
        };

        /// <summary>TypeIds whose items are classified but stay off the central bank's buy list. Its
        /// rows are <c>Comp:</c> keys, and neither a seed (<c>Seed:</c>) nor a grain (bare) is
        /// stocked under one, so listing them would offer a row no stock can ever fill.</summary>
        private static readonly HashSet<string> s_unbankedTypeIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "MyObjectBuilder_SeedItem",
            "MyObjectBuilder_PhysicalObject",
        };

        // TypeId.ToString() → ProcessedGoodCategory, for store/production cap sizing. Only
        // consulted for subtypes whose ItemKind is ProcessedGood; unlisted TypeIds default to Component.
        private static readonly Dictionary<string, ProcessedGoodCategory> s_typeIdCategory = new Dictionary<string, ProcessedGoodCategory>
        {
            { "MyObjectBuilder_PhysicalGunObject",     ProcessedGoodCategory.Weapon },
            { "MyObjectBuilder_AmmoMagazine",          ProcessedGoodCategory.Ammunition },
            { "MyObjectBuilder_ConsumableItem",        ProcessedGoodCategory.Consumable },
            { "MyObjectBuilder_GasContainerObject",    ProcessedGoodCategory.Consumable },
            { "MyObjectBuilder_OxygenContainerObject", ProcessedGoodCategory.Consumable },
            { "MyObjectBuilder_SeedItem",              ProcessedGoodCategory.Consumable },
            { "MyObjectBuilder_PhysicalObject",        ProcessedGoodCategory.Consumable },
        };

        // Hard overrides applied after the SE scan. These take priority over whatever TypeId
        // mapping SE assigns. Silver/Gold/Platinum are MyObjectBuilder_Ore in SE but we treat
        // them as RareOre to give them a separate demand-pressure bucket.
        /// <summary>No subtype overrides. Every <c>MyObjectBuilder_Ore</c> classifies as
        /// <see cref="ItemKind.Ore"/>; "rare" is a continuous, price-derived property of a deposit
        /// (<see cref="DepositModel.IsRare"/>), not a category an ore is in or out of. The
        /// three-entry list this replaced also omitted Uranium, which the offline registry included —
        /// so the sim and the live host disagreed about what uranium was.</summary>
        private static readonly Dictionary<string, ItemKind> s_subTypeOverrides = new Dictionary<string, ItemKind>();

        private readonly Dictionary<string, ItemKind> _kindMap = new Dictionary<string, ItemKind>();
        private readonly Dictionary<string, ProcessedGoodCategory> _categoryMap = new Dictionary<string, ProcessedGoodCategory>();
        /// <summary>Player-visible processed goods from the live definition scan: subtype to display
        /// name. Empty when the scan fell back to the file cache, which carries no names.</summary>
        private readonly Dictionary<string, string> _publicProcessedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly string[] _repr;
        private readonly string _cachePath;

        /// <summary>How many entries were loaded from the file cache on startup.</summary>
        public int FromFile  { get; private set; }
        /// <summary>SubTypeIds present in the live SE scan but absent from the cache.</summary>
        public int Added     { get; private set; }
        /// <summary>SubTypeIds in the cache but absent from the live SE scan.</summary>
        public int Removed   { get; private set; }
        /// <summary>Total entries in the final merged map.</summary>
        public int MappedCount { get { return _kindMap.Count; } }

        /// <param name="storageDir">Plugin storage directory for the cache file.
        /// Pass null to disable file caching (offline tests).</param>
        public SeItemRegistry(string storageDir = null)
        {
            _repr = new string[]
            {
                "Iron",            // Ore         (0)
                "SteelPlate",      // ProcessedGood (1)
                "Gas:Hydrogen",    // Hydrogen    (2) — the prefix is part of the key; see TryGetItemKind
                "Gas:Oxygen",      // Oxygen      (3) — the prefix is part of the key; see TryGetItemKind
                "Silver",          // RareOre     (4)
                "Iron",            // Ingot       (5) — ApplyInvKeyPrefix prepends "Ingot:" → "Ingot:Iron"
            };
            _cachePath = storageDir != null ? Path.Combine(storageDir, CacheFileName) : null;
            Build();
        }

        /// <summary>Inventory keys (<c>Comp:SteelPlate</c>) of every player-visible processed good:
        /// the items the central bank buys, as the Bank sales list offers them. Ordinal order.</summary>
        public List<string> BankBuyableKeys()
        {
            var keys = new List<string>(_publicProcessedNames.Count);
            foreach (string sub in _publicProcessedNames.Keys) keys.Add("Comp:" + sub);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        /// <summary>The display name of a processed good's inventory key, or its subtype when the
        /// scan never named it.</summary>
        public string DisplayNameFor(string inventoryKey)
        {
            string sub = BankSalesReport.SubType(inventoryKey);
            string name;
            return _publicProcessedNames.TryGetValue(sub, out name) ? name : sub;
        }

        public bool TryGetItemKind(string subTypeId, out ItemKind kind)
        {
            // "Ingot:" prefix is not stripped by StripInvKeyPrefix, so "Ingot:Iron" arrives here
            // as-is. Map it to ItemKind.Ingot so ingots are distinct from raw ores.
            if (subTypeId != null && subTypeId.StartsWith("Ingot:"))
            {
                kind = ItemKind.Ingot;
                return true;
            }
            // Gas is litres of a GasProperties definition, which is not a MyPhysicalItemDefinition
            // and so is never seen by ScanSeDefinitions. It is the only member ItemKind.Hydrogen /
            // ItemKind.Oxygen have: a bottle is an assembler-built container, classified
            // ProcessedGood by the TypeId table above. See SeRecipeRegistry's gas pass and FAC-038.
            if (subTypeId != null && subTypeId.StartsWith("Gas:"))
            {
                kind = subTypeId == "Gas:Oxygen" ? ItemKind.Oxygen : ItemKind.Hydrogen;
                return true;
            }
            // Retained like "Gas:" -- the bare subtype is the crop's, which the map holds already.
            if (InventoryKeyMapper.IsSeedKey(subTypeId))
            {
                kind = ItemKind.ProcessedGood;
                return true;
            }
            return _kindMap.TryGetValue(subTypeId, out kind);
        }

        public string RepresentativeSubType(ItemKind kind)
        {
            int i = (int)kind;
            return (i >= 0 && i < _repr.Length) ? _repr[i] : null;
        }

        public ProcessedGoodCategory CategoryFor(string subTypeId)
        {
            if (InventoryKeyMapper.IsSeedKey(subTypeId)) return ProcessedGoodCategory.Consumable;
            ProcessedGoodCategory category;
            return !string.IsNullOrEmpty(subTypeId) && _categoryMap.TryGetValue(subTypeId, out category)
                ? category
                : ProcessedGoodCategory.Component;
        }

        private void Build()
        {
            Dictionary<string, ItemKind> fileCache = LoadFileCache();
            FromFile = fileCache.Count;

            Dictionary<string, ProcessedGoodCategory> liveCategoryMap;
            Dictionary<string, ItemKind> liveMap = ScanSeDefinitions(out liveCategoryMap, _publicProcessedNames);

            if (liveMap.Count > 0)
            {
                foreach (var kv in liveCategoryMap)
                    _categoryMap[kv.Key] = kv.Value;

                // Apply overrides before diffing so Added/Removed counts reflect final state.
                foreach (var kv in s_subTypeOverrides) liveMap[kv.Key] = kv.Value;

                foreach (var kv in liveMap)
                    if (!fileCache.ContainsKey(kv.Key)) Added++;
                foreach (var kv in fileCache)
                    if (!liveMap.ContainsKey(kv.Key)) Removed++;

                foreach (var kv in liveMap)
                    _kindMap[kv.Key] = kv.Value;

                SaveFileCache(_kindMap);
            }
            else
            {
                // Definition manager not ready — use file cache; apply overrides on top.
                foreach (var kv in fileCache)
                    _kindMap[kv.Key] = kv.Value;
                foreach (var kv in s_subTypeOverrides)
                    _kindMap[kv.Key] = kv.Value;
            }
        }

        private static Dictionary<string, ItemKind> ScanSeDefinitions(out Dictionary<string, ProcessedGoodCategory> categoryMap,
            Dictionary<string, string> publicProcessedNames)
        {
            var map = new Dictionary<string, ItemKind>();
            categoryMap = new Dictionary<string, ProcessedGoodCategory>();
            if (MyDefinitionManager.Static == null) return map;
            try
            {
                // Two-pass scan. Ore is processed first (pass 1) and inserts bare SubTypeIds ("Iron" → Ore).
                // Ingots are handled via the TryGetItemKind fast-path (not the map), so no collision
                // with ore SubTypeIds. Pass 2 handles all other TypeIds with first-wins guard.
                var defs = MyDefinitionManager.Static.GetPhysicalItemDefinitions();

                // Pass 1: ores only
                foreach (MyPhysicalItemDefinition def in defs)
                {
                    if (def == null) continue;
                    if (def.Id.TypeId.ToString() != "MyObjectBuilder_Ore") continue;
                    string sub = def.Id.SubtypeName;
                    if (!string.IsNullOrEmpty(sub))
                        map[sub] = ItemKind.Ore;
                }

                // Pass 2: everything else in the table (first-wins guards against any remaining collisions)
                foreach (MyPhysicalItemDefinition def in defs)
                {
                    if (def == null) continue;
                    string typeIdStr = def.Id.TypeId.ToString();
                    if (typeIdStr == "MyObjectBuilder_Ore") continue; // already handled
                    ItemKind kind;
                    if (!s_typeIdKind.TryGetValue(typeIdStr, out kind)) continue;
                    string sub = def.Id.SubtypeName;
                    if (string.IsNullOrEmpty(sub)) continue;
                    if (s_excludedSubtypes.Contains(sub)) continue;
                    if (!map.ContainsKey(sub)) map[sub] = kind;

                    ProcessedGoodCategory category;
                    if (kind == ItemKind.ProcessedGood && s_typeIdCategory.TryGetValue(typeIdStr, out category)
                        && !categoryMap.ContainsKey(sub))
                        categoryMap[sub] = category;

                    if (kind == ItemKind.ProcessedGood && def.Public && map[sub] == ItemKind.ProcessedGood
                        && !s_unbankedTypeIds.Contains(typeIdStr)
                        && !publicProcessedNames.ContainsKey(sub))
                        publicProcessedNames[sub] = string.IsNullOrEmpty(def.DisplayNameText) ? sub : def.DisplayNameText;
                }
            }
            catch { /* definition manager not ready */ }
            return map;
        }

        private Dictionary<string, ItemKind> LoadFileCache()
        {
            if (_cachePath == null || !File.Exists(_cachePath))
                return new Dictionary<string, ItemKind>();
            try
            {
                string json = File.ReadAllText(_cachePath);
                Dictionary<string, string> raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (raw == null) return new Dictionary<string, ItemKind>();
                var result = new Dictionary<string, ItemKind>(raw.Count);
                foreach (var kv in raw)
                {
                    ItemKind ik;
                    if (Enum.TryParse(kv.Value, out ik))
                        result[kv.Key] = ik;
                }
                return result;
            }
            catch
            {
                return new Dictionary<string, ItemKind>();
            }
        }

        private void SaveFileCache(Dictionary<string, ItemKind> map)
        {
            if (_cachePath == null) return;
            try
            {
                var serialisable = new Dictionary<string, string>(map.Count);
                foreach (var kv in map)
                    serialisable[kv.Key] = kv.Value.ToString();
                File.WriteAllText(_cachePath, JsonConvert.SerializeObject(serialisable, Formatting.Indented));
            }
            catch { /* non-fatal — next boot re-scans */ }
        }
    }
}
