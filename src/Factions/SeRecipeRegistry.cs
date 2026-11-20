using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;
using Sandbox.Definitions;
using SpaceEngineers.Game.Definitions;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// SE-backed <see cref="IRecipeRegistry"/> with a file cache for resilience against
    /// definition-manager timing issues at session load.
    ///
    /// On construction:
    ///   1. Load recipes from <c>item_recipe_cache.json</c> (if present).
    ///   2. Scan <see cref="MyDefinitionManager.Static.GetBlueprintDefinitions()"/>.
    ///   3. If the live scan returned anything: compute added/removed vs cache, save merged result.
    ///   4. If the live scan returned nothing: use the cache as-is.
    ///
    /// Skips <see cref="MyCompositeBlueprintDefinition"/> (aggregates with no direct recipe data).
    /// All other blueprint types (regular, block, repair) are included.
    ///
    /// <para>A fourth pass reads <see cref="MyOxygenGeneratorDefinition"/>, which is where gas
    /// production lives in SE and which has no blueprint at all. See <see cref="AddGasRecipes"/>.</para>
    ///
    /// <para>A fifth reads farm plots — a block's <see cref="MyFarmPlotLogicDefinition"/> over each
    /// <see cref="MySeedItemDefinition"/> it accepts — which is how SE grows crops, again with no
    /// blueprint. See <see cref="AddFarmRecipes"/>.</para>
    ///
    /// Recipes are indexed in memory by output "TypeId/SubTypeId" key for O(1) lookup.
    /// The cache file is a flat JSON array of recipe objects for human auditability.
    /// </summary>
    public sealed class SeRecipeRegistry : IRecipeRegistry
    {
        private const string CacheFileName = "item_recipe_cache.json";

        private readonly List<ConversionRecipe> _all = new List<ConversionRecipe>();
        // "TypeId/SubTypeId" → recipes that produce that output
        private readonly Dictionary<string, List<ConversionRecipe>> _byOutput
            = new Dictionary<string, List<ConversionRecipe>>();
        // Prefab ids whose recipe came from a player scan rather than the content walk. Only used to
        // tell a capture replacing its own re-registration (a boot replay, harmless) from one
        // replacing a vanilla prefab's bill (a name collision worth a line).
        private readonly HashSet<string> _capturedPrefabRecipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // BlueprintId → recipe (first wins on duplicate)
        private readonly Dictionary<string, ConversionRecipe> _byBlueprintId
            = new Dictionary<string, ConversionRecipe>();
        // Gas-generator block key → that block's gas recipes. Rebuilt from _all at index time, so a
        // cache-only load (definition manager not ready) still resolves them.
        private readonly Dictionary<string, List<ConversionRecipe>> _gasByBlock
            = new Dictionary<string, List<ConversionRecipe>>(StringComparer.OrdinalIgnoreCase);
        // Farm-plot block key -> that block's crop recipes, rebuilt from _all the same way.
        private readonly Dictionary<string, List<ConversionRecipe>> _farmByBlock
            = new Dictionary<string, List<ConversionRecipe>>(StringComparer.OrdinalIgnoreCase);
        private readonly string _cachePath;

        public int FromFile  { get; private set; }
        public int Added     { get; private set; }
        public int Removed   { get; private set; }
        public int Count     { get { return _all.Count; } }

        public IEnumerable<ConversionRecipe> AllRecipes { get { return _all; } }

        /// <param name="storageDir">Plugin storage directory. Pass null to disable file caching.</param>
        public SeRecipeRegistry(string storageDir = null)
        {
            _cachePath = storageDir != null ? Path.Combine(storageDir, CacheFileName) : null;
            Build();
        }

        /// <summary>Recipe whose <see cref="ConversionRecipe.BlueprintId"/> matches exactly,
        /// or null if not found. Used to look up recipes by SE blueprint class membership.</summary>
        public ConversionRecipe GetRecipeByBlueprintId(string blueprintId)
        {
            if (blueprintId == null) return null;
            ConversionRecipe r;
            return _byBlueprintId.TryGetValue(blueprintId, out r) ? r : null;
        }

        /// <summary>The gas recipes registered for one gas-generator block, by the block key
        /// <see cref="GasBlockKey"/> derives. Empty for an unknown block. This is the lookup
        /// <c>capability_recipes.json</c>'s <c>GasBlocks</c> map resolves through, because a gas
        /// recipe belongs to no <c>MyBlueprintClassDefinition</c> and so cannot be reached the way
        /// every other capability's recipes are.</summary>
        public IEnumerable<ConversionRecipe> GetGasRecipesForBlock(string blockKey)
        {
            if (string.IsNullOrEmpty(blockKey)) return new ConversionRecipe[0];
            List<ConversionRecipe> list;
            return _gasByBlock.TryGetValue(blockKey, out list) ? list : (IEnumerable<ConversionRecipe>)new ConversionRecipe[0];
        }

        /// <summary>The crop recipes registered for one farm-plot block, by its SubtypeId. Empty for
        /// an unknown block. <c>capability_recipes.json</c>'s <c>FarmBlocks</c> map resolves through
        /// this, for the reason <see cref="GetGasRecipesForBlock"/> exists.</summary>
        public IEnumerable<ConversionRecipe> GetFarmRecipesForBlock(string blockKey)
        {
            if (string.IsNullOrEmpty(blockKey)) return new ConversionRecipe[0];
            List<ConversionRecipe> list;
            return _farmByBlock.TryGetValue(blockKey, out list) ? list : (IEnumerable<ConversionRecipe>)new ConversionRecipe[0];
        }

        /// <summary>Every gas recipe, across all generator blocks. Gas has no
        /// <c>MyPhysicalItemDefinition</c> and no blueprint, so it is invisible to both halves of
        /// <c>SeItemPriceCatalogue</c>'s scan; handing it this set is what gives a litre of gas a
        /// price derived from SE's own IceToGasRatio rather than no price at all.</summary>
        public IEnumerable<ConversionRecipe> GasRecipes
        {
            get
            {
                foreach (List<ConversionRecipe> list in _gasByBlock.Values)
                    foreach (ConversionRecipe recipe in list)
                        yield return recipe;
            }
        }

        public IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId)
        {
            string key = outputTypeId + "/" + outputSubTypeId;
            List<ConversionRecipe> list;
            return _byOutput.TryGetValue(key, out list) ? list : (IEnumerable<ConversionRecipe>)new ConversionRecipe[0];
        }

        /// <summary>True when a <c>Prefab/&lt;prefabId&gt;</c> recipe is already registered — the
        /// question <see cref="Factions.Core.ProductionManager.GetPrefabBom"/> answers, asked without
        /// building the bill. Callers that top up a set of prefabs at boot use it to leave alone
        /// whatever <see cref="ScanSeDefinitions"/>'s prefab pass already covered.</summary>
        public bool HasPrefabRecipe(string prefabId)
        {
            return !string.IsNullOrEmpty(prefabId) && _byBlueprintId.ContainsKey("Prefab/" + prefabId);
        }

        /// <summary>Registers a "Prefab/&lt;prefabId&gt;" recipe from a set of grids, aggregating
        /// component totals across <paramref name="grids"/> exactly like
        /// <see cref="ScanSeDefinitions"/>'s Pass 3 does when it walks the definition manager. Two
        /// callers share this body: a player capture (`!factions ship capture`,
        /// `!factions station scan`), whose grids come off a live hull; and the boot pass that tops
        /// up any station prefab the definition walk did not reach, whose grids come off a
        /// <c>MyPrefabDefinition</c>. Either way the BOM is resolvable immediately, with no session
        /// restart.
        ///
        /// <para>Not written to the file cache: a capture's recipe is re-derived fresh from its saved
        /// grids on every reload (see <c>CustomPrefabStore</c>), so caching it here would only risk
        /// drift. Re-registering an id replaces the recipe rather than stacking a second one behind
        /// the same output key.</para>
        ///
        /// <para><paramref name="fromCapture"/> marks the player-scan callers. A capture replacing a
        /// bill the content walk produced means the two share an id, and a refit to that id will be
        /// billed for the scanned hull rather than the vanilla design — so it is logged. The name
        /// gate in front of a scan is what stops a new one being created.</para></summary>
        public void RegisterPrefabRecipe(string prefabId, MyObjectBuilder_CubeGrid[] grids,
            bool fromCapture = false)
        {
            if (string.IsNullOrEmpty(prefabId) || grids == null || MyDefinitionManager.Static == null) return;

            var totals = new Dictionary<string, double>();
            foreach (MyObjectBuilder_CubeGrid grid in grids)
            {
                if (grid == null || grid.CubeBlocks == null) continue;
                foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
                {
                    if (block == null) continue;
                    MyCubeBlockDefinition blockDef;
                    if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out blockDef)) continue;
                    if (blockDef.Components == null) continue;
                    foreach (MyCubeBlockDefinition.Component comp in blockDef.Components)
                    {
                        if (comp.Definition == null) continue;
                        string sub = comp.Definition.Id.SubtypeName;
                        if (string.IsNullOrEmpty(sub)) continue;
                        double existing;
                        totals.TryGetValue(sub, out existing);
                        totals[sub] = existing + comp.Count;
                    }
                }
            }

            var inputs = new List<RecipeItem>(totals.Count);
            foreach (var kv in totals)
                inputs.Add(new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = kv.Key, Amount = kv.Value });

            var recipe = new ConversionRecipe
            {
                BlueprintId = "Prefab/" + prefabId,
                Inputs      = inputs.ToArray(),
                Outputs     = new RecipeItem[] { new RecipeItem { TypeId = "Prefab", SubTypeId = prefabId, Amount = 1.0 } },
                TimeSecs    = 0,
            };

            // Replace, never stack: the id is the identity here, and a second entry behind the same
            // output key would leave GetRecipesFor handing back a stale bill alongside the new one.
            ConversionRecipe superseded;
            if (_byBlueprintId.TryGetValue(recipe.BlueprintId, out superseded))
            {
                _all.Remove(superseded);
                if (fromCapture && !_capturedPrefabRecipes.Contains(prefabId))
                    FactionsPlugin.Log.Warn("SeRecipeRegistry: scanned design '" + prefabId
                        + "' replaces the build bill of a prefab of the same name found in game "
                        + "content; a refit to that id now costs the scanned hull.");
            }
            if (fromCapture) _capturedPrefabRecipes.Add(prefabId);

            _all.Add(recipe);
            _byBlueprintId[recipe.BlueprintId] = recipe;
            string outKey = "Prefab/" + prefabId;
            List<ConversionRecipe> list;
            if (!_byOutput.TryGetValue(outKey, out list))
                _byOutput[outKey] = list = new List<ConversionRecipe>();
            else if (superseded != null)
                list.Remove(superseded);
            list.Add(recipe);
        }

        /// <summary>Drops a scanned design's <c>Prefab/&lt;id&gt;</c> recipe. Refuses a content
        /// prefab's: only a capture is ever deleted. False when there was nothing to drop.</summary>
        public bool RemovePrefabRecipe(string prefabId)
        {
            if (string.IsNullOrEmpty(prefabId) || !_capturedPrefabRecipes.Contains(prefabId)) return false;
            ConversionRecipe recipe;
            if (!_byBlueprintId.TryGetValue("Prefab/" + prefabId, out recipe)) return false;
            _byBlueprintId.Remove("Prefab/" + prefabId);
            _all.Remove(recipe);
            _byOutput.Remove("Prefab/" + prefabId);
            _capturedPrefabRecipes.Remove(prefabId);
            return true;
        }

        private void Build()
        {
            List<ConversionRecipe> fileCache = LoadFileCache();
            FromFile = fileCache.Count;

            List<ConversionRecipe> live = ScanSeDefinitions();

            if (live.Count > 0)
            {
                // Delta: compare by BlueprintId
                var fileIds = new HashSet<string>();
                foreach (ConversionRecipe r in fileCache) fileIds.Add(r.BlueprintId);
                var liveIds = new HashSet<string>();
                foreach (ConversionRecipe r in live) liveIds.Add(r.BlueprintId);

                foreach (string id in liveIds) if (!fileIds.Contains(id)) Added++;
                foreach (string id in fileIds) if (!liveIds.Contains(id)) Removed++;

                _all.AddRange(live);
                SaveFileCache(live);
            }
            else
            {
                _all.AddRange(fileCache);
            }

            BuildIndex();
        }

        private void BuildIndex()
        {
            foreach (ConversionRecipe recipe in _all)
            {
                if (!string.IsNullOrEmpty(recipe.BlueprintId) && !_byBlueprintId.ContainsKey(recipe.BlueprintId))
                    _byBlueprintId[recipe.BlueprintId] = recipe;

                string gasBlock = GasBlockOf(recipe.BlueprintId);
                if (gasBlock != null)
                {
                    List<ConversionRecipe> gasList;
                    if (!_gasByBlock.TryGetValue(gasBlock, out gasList))
                        _gasByBlock[gasBlock] = gasList = new List<ConversionRecipe>();
                    gasList.Add(recipe);
                }

                string farmBlock = FarmBlockOf(recipe.BlueprintId);
                if (farmBlock != null)
                {
                    List<ConversionRecipe> farmList;
                    if (!_farmByBlock.TryGetValue(farmBlock, out farmList))
                        _farmByBlock[farmBlock] = farmList = new List<ConversionRecipe>();
                    farmList.Add(recipe);
                }

                if (recipe.Outputs == null) continue;
                foreach (RecipeItem output in recipe.Outputs)
                {
                    string key = output.TypeId + "/" + output.SubTypeId;
                    List<ConversionRecipe> list;
                    if (!_byOutput.TryGetValue(key, out list))
                    {
                        list = new List<ConversionRecipe>();
                        _byOutput[key] = list;
                    }
                    list.Add(recipe);
                }
            }
        }

        private static List<ConversionRecipe> ScanSeDefinitions()
        {
            var result = new List<ConversionRecipe>();
            if (MyDefinitionManager.Static == null) return result;

            // Pass 1: blueprint definitions (ore→ingot, ingot→component, weapons, consumables, etc.)
            try
            {
                foreach (MyBlueprintDefinitionBase bp in MyDefinitionManager.Static.GetBlueprintDefinitions())
                {
                    if (bp == null) continue;
                    // Skip composite blueprints — they aggregate sub-recipes without direct data
                    if (bp is MyCompositeBlueprintDefinition) continue;
                    if (bp.Prerequisites == null || bp.Prerequisites.Length == 0) continue;
                    if (bp.Results == null || bp.Results.Length == 0) continue;

                    string id = bp.Id.SubtypeName;
                    if (string.IsNullOrEmpty(id)) continue;

                    result.Add(new ConversionRecipe
                    {
                        BlueprintId = id,
                        Inputs      = ExtractItems(bp.Prerequisites),
                        Outputs     = ExtractItems(bp.Results),
                        TimeSecs    = bp.BaseProductionTimeInSeconds,
                        // SE charges this blueprint's prerequisites in full, ignoring the world's
                        // assembler efficiency multiplier (MyAssembler.GetEfficiencyMultiplierForBlueprint).
                        // Set on the vanilla hand tools and weapons.
                        IgnoresEfficiencyMultiplier = bp.IgnoreEfficiencyMultiplier,
                    });
                }
            }
            catch { /* definition manager not ready */ }

            // Pass 2: cube block definitions (component→CubeBlock recipes).
            // These are NOT in GetBlueprintDefinitions() — component requirements live on the block
            // definition itself. Accessed via GetDefinitionPairs() → MyCubeBlockDefinitionGroup.
            try
            {
                foreach (var pair in MyDefinitionManager.Static.GetDefinitionPairs())
                {
                    MyCubeBlockDefinitionGroup group = pair.Value;
                    if (group == null) continue;
                    AddBlockRecipe(result, group.Large, "Large");
                    AddBlockRecipe(result, group.Small, "Small");
                }
            }
            catch { /* definition manager not ready */ }

            // Pass 3: prefab definitions (ship/station blueprints → aggregated component BOM).
            // CubeGrids is lazy-loaded from file the first time it is accessed per prefab.
            // This is a one-time cost — results are written to the file cache after scanning.
            try
            {
                foreach (var pair in MyDefinitionManager.Static.GetPrefabDefinitions())
                {
                    MyPrefabDefinition prefab = pair.Value;
                    if (prefab == null) continue;
                    string prefabId = prefab.Id.SubtypeName;
                    if (string.IsNullOrEmpty(prefabId)) continue;

                    MyObjectBuilder_CubeGrid[] grids = prefab.CubeGrids; // triggers lazy file load
                    if (grids == null || grids.Length == 0) continue;

                    // Aggregate component totals across all grids and all blocks in the prefab.
                    var totals = new Dictionary<string, double>();
                    foreach (MyObjectBuilder_CubeGrid grid in grids)
                    {
                        if (grid == null || grid.CubeBlocks == null) continue;
                        foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
                        {
                            if (block == null) continue;
                            MyCubeBlockDefinition blockDef;
                            if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out blockDef)) continue;
                            if (blockDef.Components == null) continue;
                            foreach (MyCubeBlockDefinition.Component comp in blockDef.Components)
                            {
                                if (comp.Definition == null) continue;
                                string sub = comp.Definition.Id.SubtypeName;
                                if (string.IsNullOrEmpty(sub)) continue;
                                double existing;
                                totals.TryGetValue(sub, out existing);
                                totals[sub] = existing + comp.Count;
                            }
                        }
                    }

                    if (totals.Count == 0) continue;

                    var inputs = new List<RecipeItem>(totals.Count);
                    foreach (var kv in totals)
                        inputs.Add(new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = kv.Key, Amount = kv.Value });

                    result.Add(new ConversionRecipe
                    {
                        BlueprintId = "Prefab/" + prefabId,
                        Inputs      = inputs.ToArray(),
                        Outputs     = new RecipeItem[]
                        {
                            new RecipeItem { TypeId = "Prefab", SubTypeId = prefabId, Amount = 1.0 }
                        },
                        TimeSecs = 0,
                    });
                }
            }
            catch { /* definition manager or file system not ready */ }

            // Pass 4: gas generators. These are NOT blueprints -- an O2/H2 generator converts ice to
            // gas from its own definition fields, so nothing in GetBlueprintDefinitions() describes it.
            try
            {
                foreach (MyDefinitionBase defBase in MyDefinitionManager.Static.GetAllDefinitions())
                    AddGasRecipes(result, defBase as MyOxygenGeneratorDefinition);
            }
            catch { /* definition manager not ready */ }

            // Pass 5: farm plots. Not blueprints either -- see AddFarmRecipes.
            try
            {
                foreach (MyDefinitionBase defBase in MyDefinitionManager.Static.GetAllDefinitions())
                    AddFarmRecipes(result, defBase as MyCubeBlockDefinition);
            }
            catch { /* definition manager not ready */ }

            return result;
        }

        /// <summary>
        /// One recipe per seed a farm-plot block accepts: <c>Ore/Ice -> &lt;crop&gt;</c>, sized as one
        /// growth cycle of one plot. Read from the block's own definitions, none invented:
        ///
        /// <list type="bullet">
        /// <item><b>Time</b> is <c>PlantGrowthMinutes</c> × the seed's <c>GrowthTimeMultiplier</c>.</item>
        /// <item><b>Ice</b> is the water drawn over that time — <c>PlantWaterConsumptionPerMinute</c>
        /// × <c>SeedsRequired</c> × the seed's <c>WaterConsumptionMultiplier</c> — divided by the
        /// water an irrigation system makes per ice (<see cref="WaterPerIce"/>; 1 in vanilla).
        /// Unscaled: <c>FactionTuning.FarmIceScale</c> is applied when the capability resolves, so
        /// this cache holds SE's numbers.</item>
        /// <item><b>Output</b> is the expected harvest less the replant seed the farm keeps
        /// (docs/food-and-population.md). SE rolls <c>MyRandom.Next(MinimumOutput, MaximumOutput)</c>,
        /// whose upper bound is exclusive, so vanilla's 2/4 harvests 2 or 3 — 2.5 on average, 1.5
        /// kept.</item>
        /// </list>
        ///
        /// <para>The id is <c>Farm/&lt;block&gt;/&lt;seed&gt;</c>; <see cref="FarmCrops"/> reads the
        /// seed back out of it to decide which crops grow at a node.</para>
        /// </summary>
        private static void AddFarmRecipes(List<ConversionRecipe> result, MyCubeBlockDefinition block)
        {
            if (block == null || string.IsNullOrEmpty(block.Id.SubtypeName)) return;
            MyFarmPlotLogicDefinition farm = FarmPlotLogicOf(block);
            if (farm == null || farm.PlantGrowthMinutes <= 0f) return;

            double waterPerIce = WaterPerIce(farm.ConsumedResourceId.SubtypeName);
            if (waterPerIce <= 0) return;
            int seedsRequired = farm.SeedsRequired > 0 ? farm.SeedsRequired : 1;

            foreach (MySeedItemDefinition seed in SeedsAccepted(farm))
            {
                if (seed == null || string.IsNullOrEmpty(seed.Id.SubtypeName)) continue;
                double minutes = farm.PlantGrowthMinutes * (seed.GrowthTimeMultiplier > 0f ? seed.GrowthTimeMultiplier : 1f);
                double water = farm.PlantWaterConsumptionPerMinute * seedsRequired * seed.WaterConsumptionMultiplier * minutes;
                double harvest = seed.MaximumOutput > seed.MinimumOutput
                    ? (seed.MinimumOutput + seed.MaximumOutput - 1) / 2.0
                    : seed.MinimumOutput;
                double kept = harvest * seedsRequired - seedsRequired;
                MyDefinitionId crop = seed.OutputItemDefinitionId;
                if (minutes <= 0 || water <= 0 || kept <= 0 || string.IsNullOrEmpty(crop.SubtypeName)) continue;

                result.Add(new ConversionRecipe
                {
                    BlueprintId = FarmCrops.RecipePrefix + block.Id.SubtypeName + "/" + seed.Id.SubtypeName,
                    Inputs      = new RecipeItem[]
                    {
                        new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Ice", Amount = water / waterPerIce },
                    },
                    Outputs     = new RecipeItem[]
                    {
                        new RecipeItem { TypeId = crop.TypeId.ToString(), SubTypeId = crop.SubtypeName, Amount = kept },
                    },
                    TimeSecs    = (float)(minutes * 60.0),
                });
            }
        }

        /// <summary>The farm-plot logic a block's component container carries, or null for every
        /// block that is not a farm plot.</summary>
        private static MyFarmPlotLogicDefinition FarmPlotLogicOf(MyCubeBlockDefinition block)
        {
            MyContainerDefinition container;
            if (!MyDefinitionManager.Static.TryGetContainerDefinition(block.Id, out container)
                || container == null || container.DefaultComponents == null)
                return null;
            foreach (MyContainerDefinition.DefaultComponent component in container.DefaultComponents)
            {
                if (component == null || !component.SubtypeId.HasValue) continue;
                if (component.BuilderType.ToString() != "MyObjectBuilder_FarmPlotLogic") continue;
                MyComponentDefinitionBase def;
                if (MyDefinitionManager.Static.TryGetEntityComponentDefinition(
                        new MyDefinitionId(component.BuilderType, component.SubtypeId.Value), out def))
                    return def as MyFarmPlotLogicDefinition;
            }
            return null;
        }

        /// <summary>The seeds a plot's whitelist names, or every seed SE defines when it names none —
        /// which is what <c>MyFarmPlotLogic</c> accepts in that case.</summary>
        private static IEnumerable<MySeedItemDefinition> SeedsAccepted(MyFarmPlotLogicDefinition farm)
        {
            if (farm.WhitelistedSeeds != null && farm.WhitelistedSeeds.Length > 0)
            {
                foreach (SerializableDefinitionId id in farm.WhitelistedSeeds)
                {
                    MySeedItemDefinition seed;
                    if (MyDefinitionManager.Static.TryGetDefinition<MySeedItemDefinition>((MyDefinitionId)id, out seed))
                        yield return seed;
                }
                yield break;
            }
            foreach (MyPhysicalItemDefinition item in MyDefinitionManager.Static.GetPhysicalItemDefinitions())
            {
                var seed = item as MySeedItemDefinition;
                if (seed != null) yield return seed;
            }
        }

        /// <summary>Litres of <paramref name="resourceSubtype"/> (Water) an irrigation system makes
        /// per unit of ice: the <c>IceToGasRatio</c> of the generator that produces it, 1 in vanilla.
        /// 0 when nothing in this world makes that resource from ice, which leaves a farm plot with no
        /// recipe rather than one priced on a guess.</summary>
        private static double WaterPerIce(string resourceSubtype)
        {
            if (string.IsNullOrEmpty(resourceSubtype)) return 0;
            foreach (MyDefinitionBase defBase in MyDefinitionManager.Static.GetAllDefinitions())
            {
                var gen = defBase as MyOxygenGeneratorDefinition;
                if (gen == null || gen.ProducedGases == null) continue;
                foreach (MyOxygenGeneratorDefinition.MyGasGeneratorResourceInfo gas in gen.ProducedGases)
                    if (gas.Id.SubtypeName == resourceSubtype && gas.IceToGasRatio > 0f)
                        return gas.IceToGasRatio;
            }
            return 0;
        }

        /// <summary>The block key inside a farm recipe id, or null when the id is not one.</summary>
        private static string FarmBlockOf(string blueprintId)
        {
            if (string.IsNullOrEmpty(blueprintId) || !blueprintId.StartsWith(FarmCrops.RecipePrefix)) return null;
            string rest = blueprintId.Substring(FarmCrops.RecipePrefix.Length);
            int slash = rest.LastIndexOf('/');
            return slash > 0 ? rest.Substring(0, slash) : null;
        }

        /// <summary>
        /// One recipe per gas a generator block produces: <c>Ore/Ice -> GasProperties/&lt;gas&gt;</c>,
        /// sized as exactly one second of that block running flat out.
        ///
        /// <para><b>One recipe per gas, not one recipe emitting both.</b> SE's ratios differ per gas
        /// (Oxygen 10, Hydrogen 20 on the vanilla generator) because the block converts the ice it
        /// consumes into whichever gas is being drawn, at that gas's efficiency — they are
        /// alternative outputs over one input stream, not co-products in a fixed proportion. A single
        /// recipe emitting both would invent a ratio SE does not have.</para>
        ///
        /// <para>Every number is read, none invented: <c>IceConsumptionPerSecond</c> is the input
        /// amount, <c>IceToGasRatio</c> multiplies it into the output, and <c>TimeSecs</c> is 1.0
        /// because that is the second those two fields are quoted per. That also leaves the machine
        /// multiplier a plain 1.00 (see <c>MachinePhysics</c>), since the block has no speed tag of
        /// the kind a refinery or assembler carries.</para>
        ///
        /// <para>Water is emitted too, from the <c>IrrigationSystem</c> generator (ratio 1). Nothing
        /// consumes it yet — see FAC-103 — but it comes off the same definition and inventing a
        /// separate water source later would be the worse outcome.</para>
        /// </summary>
        private static void AddGasRecipes(List<ConversionRecipe> result, MyOxygenGeneratorDefinition def)
        {
            if (def == null || def.ProducedGases == null) return;
            if (def.IceConsumptionPerSecond <= 0f) return;

            string blockKey = GasBlockKey(def);
            if (string.IsNullOrEmpty(blockKey)) return;

            foreach (MyOxygenGeneratorDefinition.MyGasGeneratorResourceInfo gas in def.ProducedGases)
            {
                string gasSubtype = gas.Id.SubtypeName;
                if (string.IsNullOrEmpty(gasSubtype) || gas.IceToGasRatio <= 0f) continue;

                result.Add(new ConversionRecipe
                {
                    BlueprintId = GasRecipePrefix + blockKey + "/" + gasSubtype,
                    Inputs      = new RecipeItem[]
                    {
                        new RecipeItem
                        {
                            TypeId    = "MyObjectBuilder_Ore",
                            SubTypeId = "Ice",
                            Amount    = def.IceConsumptionPerSecond,
                        }
                    },
                    Outputs     = new RecipeItem[]
                    {
                        new RecipeItem
                        {
                            TypeId    = gas.Id.TypeId.ToString(),
                            SubTypeId = gasSubtype,
                            Amount    = (double)def.IceConsumptionPerSecond * gas.IceToGasRatio,
                        }
                    },
                    TimeSecs    = 1.0f,
                });
            }
        }

        /// <summary>Prefix marking a recipe as coming from the gas pass. Chosen to match the
        /// <c>Block/</c> and <c>Prefab/</c> prefixes the other synthetic passes use.</summary>
        private const string GasRecipePrefix = "Gas/";

        /// <summary>Stable identity for a gas-generator block: its SubtypeName, or its
        /// <c>BlockPairName</c> when the subtype is empty — which SE's own large O2/H2 Generator has
        /// (<c>&lt;SubtypeId&gt;&lt;/SubtypeId&gt;</c> in <c>CubeBlocks_Production.sbc</c>), so a
        /// subtype-only convention could not name the one block this feature exists for. The vanilla
        /// set this yields is unique per block: <c>OxygenGenerator</c>, <c>OxygenGeneratorSmall</c>,
        /// <c>LargeBlockOxygenGeneratorLab</c>, <c>SmallBlockOxygenGeneratorLab</c>,
        /// <c>IrrigationSystem</c>.</summary>
        private static string GasBlockKey(MyOxygenGeneratorDefinition def)
        {
            if (def == null) return null;
            string subtype = def.Id.SubtypeName;
            return !string.IsNullOrEmpty(subtype) ? subtype : def.BlockPairName;
        }

        /// <summary>The block key inside a gas recipe id, or null when the id is not one. Parsing it
        /// back out is what lets the block index survive a cache-only load, where no definition is
        /// available to re-derive it from.</summary>
        private static string GasBlockOf(string blueprintId)
        {
            if (string.IsNullOrEmpty(blueprintId) || !blueprintId.StartsWith(GasRecipePrefix)) return null;
            string rest = blueprintId.Substring(GasRecipePrefix.Length);
            int slash = rest.LastIndexOf('/');
            return slash > 0 ? rest.Substring(0, slash) : null;
        }

        private static void AddBlockRecipe(List<ConversionRecipe> result,
            MyCubeBlockDefinition def, string sizeSuffix)
        {
            if (def == null) return;
            if (def.Components == null || def.Components.Length == 0) return;

            string subtype = def.Id.SubtypeName;
            if (string.IsNullOrEmpty(subtype)) return;

            // BlueprintId uses "Block/" prefix to avoid collisions with blueprint-type recipe ids.
            string blueprintId = "Block/" + subtype + "_" + sizeSuffix;

            var inputs = new RecipeItem[def.Components.Length];
            for (int i = 0; i < def.Components.Length; i++)
            {
                MyCubeBlockDefinition.Component c = def.Components[i];
                inputs[i] = new RecipeItem
                {
                    TypeId    = c.Definition != null ? c.Definition.Id.TypeId.ToString() : "MyObjectBuilder_Component",
                    SubTypeId = c.Definition != null ? c.Definition.Id.SubtypeName : string.Empty,
                    Amount    = c.Count,
                };
            }

            result.Add(new ConversionRecipe
            {
                BlueprintId = blueprintId,
                Inputs      = inputs,
                Outputs     = new RecipeItem[]
                {
                    new RecipeItem
                    {
                        TypeId    = "MyObjectBuilder_CubeBlock",
                        SubTypeId = subtype,
                        Amount    = 1.0,
                    }
                },
                TimeSecs = 0, // block build time depends on welder speed, not a fixed recipe value
            });
        }

        private static RecipeItem[] ExtractItems(MyBlueprintDefinitionBase.Item[] items)
        {
            var result = new RecipeItem[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                result[i] = new RecipeItem
                {
                    TypeId    = items[i].Id.TypeId.ToString(),
                    SubTypeId = items[i].Id.SubtypeName,
                    Amount    = (double)(float)items[i].Amount,
                };
            }
            return result;
        }

        // ---- persistence -----------------------------------------------------------------------

        // Serialization proxy — matches the JSON structure exactly so we can use JsonConvert.
        private sealed class RecipeItemDto  { public string TypeId; public string SubTypeId; public double Amount; }
        private sealed class RecipeDto      { public string BlueprintId; public float TimeSecs;
                                              public RecipeItemDto[] Inputs; public RecipeItemDto[] Outputs;
                                              public bool IgnoresEfficiencyMultiplier; }

        private List<ConversionRecipe> LoadFileCache()
        {
            if (_cachePath == null || !File.Exists(_cachePath))
                return new List<ConversionRecipe>();
            try
            {
                string json = File.ReadAllText(_cachePath);
                List<RecipeDto> dtos = JsonConvert.DeserializeObject<List<RecipeDto>>(json);
                if (dtos == null) return new List<ConversionRecipe>();

                var result = new List<ConversionRecipe>(dtos.Count);
                foreach (RecipeDto dto in dtos)
                {
                    if (dto == null || string.IsNullOrEmpty(dto.BlueprintId)) continue;
                    result.Add(new ConversionRecipe
                    {
                        BlueprintId = dto.BlueprintId,
                        TimeSecs    = dto.TimeSecs,
                        Inputs      = DtoToItems(dto.Inputs),
                        Outputs     = DtoToItems(dto.Outputs),
                        IgnoresEfficiencyMultiplier = dto.IgnoresEfficiencyMultiplier,
                    });
                }
                return result;
            }
            catch
            {
                return new List<ConversionRecipe>();
            }
        }

        private void SaveFileCache(List<ConversionRecipe> recipes)
        {
            if (_cachePath == null) return;
            try
            {
                var dtos = new List<RecipeDto>(recipes.Count);
                foreach (ConversionRecipe r in recipes)
                {
                    dtos.Add(new RecipeDto
                    {
                        BlueprintId = r.BlueprintId,
                        TimeSecs    = r.TimeSecs,
                        Inputs      = ItemsToDtos(r.Inputs),
                        Outputs     = ItemsToDtos(r.Outputs),
                        IgnoresEfficiencyMultiplier = r.IgnoresEfficiencyMultiplier,
                    });
                }
                File.WriteAllText(_cachePath, JsonConvert.SerializeObject(dtos, Formatting.Indented));
            }
            catch { /* non-fatal */ }
        }

        private static RecipeItem[] DtoToItems(RecipeItemDto[] dtos)
        {
            if (dtos == null) return new RecipeItem[0];
            var result = new RecipeItem[dtos.Length];
            for (int i = 0; i < dtos.Length; i++)
                result[i] = new RecipeItem { TypeId = dtos[i].TypeId, SubTypeId = dtos[i].SubTypeId, Amount = dtos[i].Amount };
            return result;
        }

        private static RecipeItemDto[] ItemsToDtos(RecipeItem[] items)
        {
            if (items == null) return new RecipeItemDto[0];
            var result = new RecipeItemDto[items.Length];
            for (int i = 0; i < items.Length; i++)
                result[i] = new RecipeItemDto { TypeId = items[i].TypeId, SubTypeId = items[i].SubTypeId, Amount = items[i].Amount };
            return result;
        }
    }
}
