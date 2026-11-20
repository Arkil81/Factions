using System;
using System.Collections.Generic;
using Factions.Core;
using Newtonsoft.Json;

namespace Factions
{
    /// <summary>
    /// Deserialized from <c>capability_recipes.json</c>. Maps capability kind names to the
    /// SE blueprint class names that each installed level of that capability can process.
    ///
    /// <para>Structure: <c>Capabilities[capabilityName][level] = [className, ...]</c></para>
    ///
    /// <para>Example:
    /// <code>
    /// {
    ///   "Capabilities": {
    ///     "Refinery":    { "1": ["CommonMetals"] },
    ///     "Manufactory": { "1": ["SimpleComponents"] }
    ///   },
    ///   "InstallBlocks": {
    ///     "RefineryII":    "LargeRefinery",
    ///     "ManufactoryII": "LargeAssembler"
    ///   }
    /// }
    /// </code>
    /// Blueprint class names must match SE's <c>MyDefinitionManager.GetBlueprintClass(name)</c>.
    /// </para>
    ///
    /// <para><c>InstallBlocks[capabilityName] = blockKey</c> — optional. When present, that
    /// capability's install cost is resolved from the named SE cube block's real construction BOM
    /// (<c>MyCubeBlockDefinition.Components</c>) instead of the flat <see cref="ItemKind"/> costs in
    /// <c>FactionTuning</c>. Capabilities not listed here keep the flat-cost model unchanged. The
    /// key is the block's SubtypeId, or its BlockPairName when the SubtypeId is empty — the same
    /// rule <c>GasBlocks</c> uses, because the one block <c>FuelDepot</c> installs from is exactly
    /// the one whose SubtypeId SE leaves blank.</para>
    ///
    /// <para><c>InstallPrefabs[capabilityName] = prefabName</c> — the whole-structure counterpart to
    /// <c>InstallBlocks</c>, for a capability with no single representative SE block (e.g.
    /// <c>Shipyard</c> — there is no vanilla "Shipyard" cube block, only whole prefab
    /// ships/stations named for the role). Resolves every block across the named prefab's
    /// CubeGrids and sums their components into one BOM, the same walk
    /// <c>PrefabBookValueCache.ComputePrefabBookValue</c> already does for prefab book value.
    /// Merged into the same install-BOM set as <c>InstallBlocks</c>; a capability should appear in
    /// at most one of the two.</para>
    ///
    /// <para><c>InstallExtraBlocks[capabilityName] = { blockKey: count }</c> — further blocks a level
    /// installs beside its <c>InstallBlocks</c> one, each charged <c>count</c> times its construction
    /// BOM. A Farm level is one irrigation system and the 30 plots it waters, so the plots are paid
    /// for as well as the machine. Ignored for a capability with no <c>InstallBlocks</c> entry.</para>
    ///
    /// <para><c>MachineBlocks[capabilityName] = blockSubtypeId</c> — the SE block whose definition
    /// supplies that capability's machine physics (<c>RefineSpeed</c>/<c>AssemblySpeed</c> and
    /// <c>MaterialEfficiency</c>), resolved into <c>FactionTuning.MachineProfiles</c>. Deliberately
    /// separate from <c>InstallBlocks</c>: that map also decides whether a capability is charged a
    /// real component BOM instead of its flat ItemKind install cost, so listing Refinery I there
    /// purely to obtain its speed would silently change its install economics.</para>
    ///
    /// <para><c>GasBlocks[capabilityName] = [blockKey, ...]</c> — the gas-generator blocks a
    /// capability runs. Separate from <c>Capabilities</c> because that map names SE <b>blueprint
    /// class</b> names, and gas production has no blueprint at all: an O2/H2 generator converts ice
    /// from its own <c>MyOxygenGeneratorDefinition</c> fields, so there is no class for
    /// <c>GetBlueprintClass</c> to expand. The block key is the block's SubtypeId, or its
    /// BlockPairName when the SubtypeId is empty — which SE's own large O2/H2 Generator has, so
    /// <c>OxygenGenerator</c> below names it by pair.</para>
    ///
    /// <para><c>FarmBlocks[capabilityName] = [blockKey, ...]</c> — the farm-plot blocks a capability
    /// runs, the <c>GasBlocks</c> idea for growing: SE grows a crop in a block's
    /// <c>MyFarmPlotLogicDefinition</c> over a planted <c>MySeedItemDefinition</c>, and no
    /// blueprint describes either. <c>SeRecipeRegistry</c> reads both into one recipe per seed the
    /// plot accepts, and <see cref="FactionTuning.FarmIceScale"/> scales its ice when the capability
    /// resolves. The key is the block's SubtypeId.</para>
    ///
    /// <para><c>IgnoredBlueprints</c> / <c>IgnoredInputKeys</c> — recipes the managers must never
    /// see, dropped at resolution so host and <c>Factions.Sim</c> run the identical set. See
    /// <see cref="RecipeExclusions"/> for what each matches and why there are two of them.</para>
    ///
    /// <para><c>SyntheticRecipes[capabilityName] = [recipe, ...]</c> — recipes this world runs that
    /// SE does not define. The prototech chain is why it exists: SE ships no blueprint that produces
    /// <c>Ingot/PrototechScrap</c> (its entry is a self-loop the file itself marks as <i>"only used
    /// for economy prices"</i>) and none at all that produces <c>Component/PrototechFrame</c> (a stub
    /// commented <i>"exists to prevent missing blueprint errors"</i>), because in vanilla both come
    /// off destroyed Factorum grids. Five of the six real prototech component recipes take scrap and
    /// every Factorum hull needs frames, so without these two the whole class resolves into a
    /// capability that can only ever make <c>PrototechPanel</c>. <c>Component/ZoneChip</c> is the
    /// same stub shape with the same remedy on ManufactoryII -- see
    /// <see cref="DefaultZoneChipRecipes"/>.</para>
    ///
    /// <para>They are configuration rather than code because their amounts are a balance decision:
    /// an operator retunes what a scrap unit costs in rare ingots without a rebuild. They are folded
    /// into <see cref="RecipeReachability"/>'s closure before exclusion, so a material only they can
    /// produce still counts as obtainable.</para>
    /// </summary>
    public sealed class CapabilityRecipesConfig
    {
        public Dictionary<string, Dictionary<string, List<string>>> Capabilities { get; set; }
            = new Dictionary<string, Dictionary<string, List<string>>>();

        public Dictionary<string, string> InstallBlocks { get; set; }
            = new Dictionary<string, string>();

        public Dictionary<string, string> InstallPrefabs { get; set; }
            = new Dictionary<string, string>();

        public Dictionary<string, string> MachineBlocks { get; set; }
            = new Dictionary<string, string>();

        /// <summary>Blocks installed beside a capability's <see cref="InstallBlocks"/> block, with how
        /// many of each. See the class remarks.</summary>
        public Dictionary<string, Dictionary<string, int>> InstallExtraBlocks { get; set; }
            = new Dictionary<string, Dictionary<string, int>>();

        /// <summary>Gas-generator block keys per capability. See the class remarks for why this is a
        /// separate map from <see cref="Capabilities"/>.</summary>
        public Dictionary<string, List<string>> GasBlocks { get; set; }
            = new Dictionary<string, List<string>>();

        /// <summary>Farm-plot block keys per capability. See the class remarks for why this is a
        /// separate map from <see cref="Capabilities"/>.</summary>
        public Dictionary<string, List<string>> FarmBlocks { get; set; }
            = new Dictionary<string, List<string>>();

        /// <summary>Blueprint IDs (SE blueprint SubtypeName) never handed to a capability.
        /// <c>null</c> — the key absent from an existing file — means
        /// <see cref="RecipeExclusions.DefaultIgnoredBlueprints"/>; an empty list means exclude none.</summary>
        public List<string> IgnoredBlueprints { get; set; }

        /// <summary>Input keys (<c>TypeId/SubTypeId</c>) whose recipes are never handed to a
        /// capability, matched against the recipe's FIRST input. <c>null</c> means
        /// <see cref="RecipeExclusions.DefaultIgnoredInputKeys"/>; an empty list means exclude none.</summary>
        public List<string> IgnoredInputKeys { get; set; }

        /// <summary>Recipes this world defines that SE does not, per capability name. See the class
        /// remarks for why the prototech chain needs them.</summary>
        public Dictionary<string, List<ConversionRecipe>> SyntheticRecipes { get; set; }
            = new Dictionary<string, List<ConversionRecipe>>();

        /// <summary>This capability's synthetic recipes, never null.</summary>
        public IList<ConversionRecipe> SyntheticRecipesFor(string capabilityName)
        {
            List<ConversionRecipe> recipes;
            if (SyntheticRecipes != null && capabilityName != null
                && SyntheticRecipes.TryGetValue(capabilityName, out recipes) && recipes != null)
                return recipes;
            return new List<ConversionRecipe>();
        }

        /// <summary>Every synthetic recipe across every capability, for the reachability closure —
        /// which asks what this world can produce at all, not which machine produces it.</summary>
        public IList<ConversionRecipe> AllSyntheticRecipes()
        {
            var all = new List<ConversionRecipe>();
            if (SyntheticRecipes == null) return all;
            foreach (var entry in SyntheticRecipes)
                if (entry.Value != null)
                    foreach (ConversionRecipe r in entry.Value)
                        if (r != null) all.Add(r);
            return all;
        }

        public static CapabilityRecipesConfig Defaults()
        {
            var cfg = new CapabilityRecipesConfig();
            cfg.Capabilities["Refinery"] = new Dictionary<string, List<string>>
            {
                ["1"] = new List<string> { "CommonMetals" },
            };
            cfg.Capabilities["Manufactory"] = new Dictionary<string, List<string>>
            {
                ["1"] = new List<string> { "SimpleComponents" },
            };
            cfg.Capabilities["ManufactoryII"] = new Dictionary<string, List<string>>
            {
                ["1"] = new List<string>
                {
                    "LargeBlocks",
                    "SmallBlocks",
                    "Components",
                    "EliteEquipment",
                    "EliteTools",
                    "EliteWeapons",
                    "EliteConsumables",
                    "BuildPlanner",
                },
            };
            cfg.Capabilities["RefineryII"] = new Dictionary<string, List<string>>
            {
                ["1"] = new List<string> { "Ingots" },
            };
            // The Factorum's own tier. PrototechComponents is the only class the Prototech Assembler
            // carries and the only class that carries prototech components -- a LargeAssembler cannot
            // run these, in SE or here. PrototechRefinery draws the same Ingots class as RefineryII;
            // what it buys is the block's RefineSpeed 5.5 / MaterialEfficiency 2.0 against 1.3 / 1.0.
            cfg.Capabilities["PrototechForge"] = new Dictionary<string, List<string>>
            {
                ["1"] = new List<string> { "PrototechComponents" },
            };
            cfg.Capabilities["PrototechRefinery"] = new Dictionary<string, List<string>>
            {
                ["1"] = new List<string> { "Ingots" },
            };
            // SE's food processor carries exactly these two classes: crops (and meat, algae) into
            // meals, and a crop into its seed. Recipes rooted in meat or algae fall to the derived
            // reachability rule, since nothing in this world harvests either yet.
            cfg.Capabilities["FoodProcessor"] = new Dictionary<string, List<string>>
            {
                ["1"] = new List<string> { "FoodProcessorRations", "FoodProcessorSeeds" },
            };
            cfg.InstallBlocks["RefineryII"]    = "LargeRefinery";
            cfg.InstallBlocks["ManufactoryII"] = "LargeAssembler";
            // The generator a FuelDepot is, charged as its real component BOM. Without this the
            // capability falls back to a flat 50 Hydrogen -- the only gas producer priced in the gas
            // nothing else can make.
            cfg.InstallBlocks["FuelDepot"]     = "OxygenGenerator";
            // Both prototech blocks are charged their real component BOM, less whatever only the
            // capability being installed could have made -- see InstallBomSelfReference. For the
            // refinery nothing is dropped, because a forge makes its prototech parts, and that is
            // the gate: only a faction already running one can pay for a prototech machine. For the
            // forge itself the prototech half IS dropped, which is what makes a first forge
            // buildable at all.
            cfg.InstallBlocks["PrototechForge"]     = "LargePrototechAssembler";
            cfg.InstallBlocks["PrototechRefinery"]  = "LargePrototechRefinery";
            // A Farm level is one irrigation system and the plots it waters, and both are charged:
            // the system is the machine, the plots are interior plate, construction and small tube.
            // 30 is the fruit-limited count MachineProfile models the level on (600 L/min of water
            // over a fruit plot's 20 L/min).
            cfg.InstallBlocks["Farm"]               = "IrrigationSystem";
            cfg.InstallExtraBlocks["Farm"]          = new Dictionary<string, int> { { "LargeBlockFarmPlot", 30 } };
            cfg.InstallBlocks["FoodProcessor"]      = "FoodProcessor";
            // A Habitat level is a medical room, a kitchen and the beds it houses workers in; the
            // beds are the worker cap (FactionTuning.HabitatBedsPerLevel), so 20 here is 20 workers.
            cfg.InstallBlocks["Habitat"]            = "LargeMedicalRoom";
            cfg.InstallExtraBlocks["Habitat"]       = new Dictionary<string, int>
                { { "LargeBlockBed", 20 }, { "LargeBlockKitchen", 1 } };
            cfg.InstallPrefabs["Shipyard"]     = "GE_SmallShipyard_Empty";
            cfg.MachineBlocks["Refinery"]      = "Blast Furnace";
            cfg.MachineBlocks["RefineryII"]    = "LargeRefinery";
            cfg.MachineBlocks["Manufactory"]   = "BasicAssembler";
            cfg.MachineBlocks["ManufactoryII"] = "LargeAssembler";
            cfg.MachineBlocks["PrototechForge"]    = "LargePrototechAssembler";
            cfg.MachineBlocks["PrototechRefinery"] = "LargePrototechRefinery";
            cfg.MachineBlocks["FoodProcessor"]     = "FoodProcessor";
            // The vanilla large O2/H2 Generator, named by BlockPairName because its SubtypeId is
            // empty. Deliberately not the Lab or Small variants, and not IrrigationSystem — that one
            // makes Water, which has no consumer yet (FAC-103).
            cfg.GasBlocks["FuelDepot"]         = new List<string> { "OxygenGenerator" };
            // The one farm plot SE ships; there is no small-grid plot. The algae farm is a different
            // logic (MySolarFoodGenerator) and is not read.
            cfg.FarmBlocks["Farm"]             = new List<string> { "LargeBlockFarmPlot" };
            cfg.SyntheticRecipes["PrototechForge"] = DefaultPrototechRecipes();
            cfg.SyntheticRecipes["ManufactoryII"] = DefaultZoneChipRecipes();
            cfg.IgnoredBlueprints             = RecipeExclusions.DefaultIgnoredBlueprints();
            cfg.IgnoredInputKeys              = RecipeExclusions.DefaultIgnoredInputKeys();
            return cfg;
        }

        /// <summary>The two recipes that make the prototech chain producible at all.
        ///
        /// <para>Amounts are priced off SE's own economy values rather than chosen: a
        /// <c>PrototechScrap</c> unit lists at 25,000 and a <c>PrototechFrame</c> at 100,000, hence
        /// four scrap to a frame. The scrap basket is deliberately weighted toward the metals whose
        /// ore refines worst -- platinum at 0.5%, gold and uranium at 1% -- so a Factorum hull is
        /// paid for in deep-space ore rather than in iron. One <c>GE_HighProfileTransportB</c> comes
        /// to roughly 137 scrap: about 27k platinum ore and 27k gold ore at those yields.</para></summary>
        public static List<ConversionRecipe> DefaultPrototechRecipes()
        {
            return new List<ConversionRecipe>
            {
                new ConversionRecipe
                {
                    BlueprintId = "SynthPrototechScrap",
                    TimeSecs    = 60f,
                    Inputs      = new[]
                    {
                        Item("MyObjectBuilder_Ingot", "Iron",     25),
                        Item("MyObjectBuilder_Ingot", "Silver",    5),
                        Item("MyObjectBuilder_Ingot", "Gold",      2),
                        Item("MyObjectBuilder_Ingot", "Platinum",  1),
                        Item("MyObjectBuilder_Ingot", "Uranium",   1),
                    },
                    Outputs     = new[] { Item("MyObjectBuilder_Ingot", "PrototechScrap", 1) },
                },
                new ConversionRecipe
                {
                    BlueprintId = "SynthPrototechFrame",
                    TimeSecs    = 120f,
                    Inputs      = new[]
                    {
                        Item("MyObjectBuilder_Ingot", "PrototechScrap",  4),
                        Item("MyObjectBuilder_Ingot", "Iron",          100),
                        Item("MyObjectBuilder_Ingot", "Cobalt",         20),
                    },
                    Outputs     = new[] { Item("MyObjectBuilder_Component", "PrototechFrame", 1) },
                },
            };
        }

        /// <summary>The recipe that makes a safe zone buildable.
        ///
        /// <para>SE's <c>ZoneChip</c> blueprint is a one-for-one self-loop with a 99,999-second
        /// production time and belongs to no blueprint class -- the same economy-price stub shape as
        /// <c>Ingot/PrototechScrap</c>. In vanilla a chip is bought from a station, never made, so
        /// the five a <c>SafeZoneBlock</c> costs are a component nothing in this world can produce
        /// and any structure carrying one stalls the way Factorum hulls did. Two prefabs in the live
        /// catalogue carry one: <c>GE_ListeningPostC</c> and the captured <c>GSS_Station</c>.</para>
        ///
        /// <para>The basket is a chip -- silicon substrate, silver traces, gold bonding, platinum --
        /// and comes to 1,021,500 at this world's ingot prices against the chip's own 100,000 list
        /// price, so a safe zone is paid for about ten times over in refined metal. That is the
        /// gate, and <c>IgnoresEfficiencyMultiplier</c> is what keeps it real: ManufactoryII carries
        /// the world's <c>AssemblerEfficiencyMultiplier</c> as an input divisor (3.00 live), and
        /// without the opt-out the basket would be charged at a third of what it reads. SE sets the
        /// same flag on its own special-item blueprints.</para>
        ///
        /// <para>It sits on ManufactoryII rather than the forge because the prefabs that need it are
        /// ordinary station designs any faction can be assigned, not Factorum hulls.</para></summary>
        public static List<ConversionRecipe> DefaultZoneChipRecipes()
        {
            return new List<ConversionRecipe>
            {
                new ConversionRecipe
                {
                    BlueprintId = "SynthZoneChip",
                    TimeSecs    = 600f,
                    IgnoresEfficiencyMultiplier = true,
                    Inputs      = new[]
                    {
                        Item("MyObjectBuilder_Ingot", "Silicon",  500),
                        Item("MyObjectBuilder_Ingot", "Silver",   100),
                        Item("MyObjectBuilder_Ingot", "Gold",      20),
                        Item("MyObjectBuilder_Ingot", "Platinum",   5),
                    },
                    Outputs     = new[] { Item("MyObjectBuilder_Component", "ZoneChip", 1) },
                },
            };
        }

        private static RecipeItem Item(string typeId, string subTypeId, double amount)
        {
            var item = new RecipeItem();
            item.TypeId    = typeId;
            item.SubTypeId = subTypeId;
            item.Amount    = amount;
            return item;
        }

        /// <summary>Adds every <see cref="Defaults"/> entry this config does not already name.
        ///
        /// <para>The file is written only when it is missing entirely, so a key added to
        /// <see cref="Defaults"/> after an instance was created never reaches that instance. It is
        /// not a deploy gap — <c>build.ps1</c> is not supposed to overwrite an operator's config —
        /// but the failure is silent: <c>FuelDepot</c> shipped with a <c>GasBlocks</c> default and
        /// resolved nothing for three days on a server whose file predated the key, and emitted no
        /// line saying so. <see cref="DefaultMachineBlock"/> is the same idea applied to one map at
        /// lookup time; this generalises it so the next added key cannot fail the same way.</para>
        ///
        /// <para>Merged per entry, not per map, because that is where the gap actually is: the live
        /// file had an <c>InstallBlocks</c> map, just no <c>FuelDepot</c> row in it. An operator who
        /// deliberately deleted a default entry gets it back and one log line naming it.</para>
        ///
        /// <para><see cref="IgnoredBlueprints"/> and <see cref="IgnoredInputKeys"/> are deliberately
        /// left alone. Null already means "use the built-in set" at lookup time, so writing today's
        /// list to disk would freeze it against a later change — reintroducing, for those two keys,
        /// exactly the staleness this method removes.</para></summary>
        /// <returns><c>Map.Key</c> paths added, sorted. Empty when the config was already complete,
        /// which is the common case and the one that must not rewrite the file.</returns>
        public static List<string> MergeDefaults(CapabilityRecipesConfig config)
        {
            var added = new List<string>();
            if (config == null) return added;

            CapabilityRecipesConfig defaults = Defaults();

            if (config.Capabilities == null)
                config.Capabilities = new Dictionary<string, Dictionary<string, List<string>>>();
            if (config.InstallBlocks  == null) config.InstallBlocks  = new Dictionary<string, string>();
            if (config.InstallPrefabs == null) config.InstallPrefabs = new Dictionary<string, string>();
            if (config.MachineBlocks  == null) config.MachineBlocks  = new Dictionary<string, string>();
            if (config.InstallExtraBlocks == null) config.InstallExtraBlocks = new Dictionary<string, Dictionary<string, int>>();
            if (config.GasBlocks      == null) config.GasBlocks      = new Dictionary<string, List<string>>();
            if (config.FarmBlocks     == null) config.FarmBlocks     = new Dictionary<string, List<string>>();
            if (config.SyntheticRecipes == null) config.SyntheticRecipes = new Dictionary<string, List<ConversionRecipe>>();

            foreach (var entry in defaults.Capabilities)
                if (!config.Capabilities.ContainsKey(entry.Key))
                {
                    config.Capabilities[entry.Key] = entry.Value;
                    added.Add("Capabilities." + entry.Key);
                }

            MergeMissing(config.InstallBlocks,  defaults.InstallBlocks,  "InstallBlocks",  added);
            MergeMissing(config.InstallPrefabs, defaults.InstallPrefabs, "InstallPrefabs", added);
            MergeMissing(config.MachineBlocks,  defaults.MachineBlocks,  "MachineBlocks",  added);
            MergeMissing(config.InstallExtraBlocks, defaults.InstallExtraBlocks, "InstallExtraBlocks", added);
            MergeMissing(config.GasBlocks,      defaults.GasBlocks,      "GasBlocks",      added);
            MergeMissing(config.FarmBlocks,     defaults.FarmBlocks,     "FarmBlocks",     added);
            MergeMissing(config.SyntheticRecipes, defaults.SyntheticRecipes, "SyntheticRecipes", added);

            added.Sort(StringComparer.Ordinal);
            return added;
        }

        private static void MergeMissing<T>(Dictionary<string, T> target, Dictionary<string, T> defaults,
                                            string mapName, List<string> added)
        {
            foreach (var entry in defaults)
                if (!target.ContainsKey(entry.Key))
                {
                    target[entry.Key] = entry.Value;
                    added.Add(mapName + "." + entry.Key);
                }
        }

        /// <summary>The block a capability's machine physics comes from when
        /// <see cref="MachineBlocks"/> has no entry for it — an instance whose
        /// <c>capability_recipes.json</c> predates that key, since the file is only seeded when
        /// absent. Same values <see cref="Defaults"/> ships.</summary>
        public static string DefaultMachineBlock(string capabilityName)
        {
            switch (capabilityName)
            {
                case "Refinery":      return "Blast Furnace";
                case "RefineryII":    return "LargeRefinery";
                case "Manufactory":   return "BasicAssembler";
                case "ManufactoryII": return "LargeAssembler";
                case "PrototechForge":    return "LargePrototechAssembler";
                case "PrototechRefinery": return "LargePrototechRefinery";
                case "FoodProcessor":     return "FoodProcessor";
                default:              return null;
            }
        }

        public static string DefaultsJson()
        {
            return JsonConvert.SerializeObject(Defaults(), Formatting.Indented);
        }
    }

    /// <summary>Sums block construction lists into an install BOM keyed <c>Comp:&lt;subtype&gt;</c>.
    /// SE lists a component more than once when it is laid down in stages (a farm plot names
    /// InteriorPlate and Construction twice each), so every occurrence adds.</summary>
    public static class InstallBomBuilder
    {
        /// <summary>Adds <paramref name="count"/> blocks' worth of <paramref name="components"/>
        /// (component subtype, count per block) to <paramref name="bom"/>.</summary>
        public static void Add(Dictionary<string, long> bom, IEnumerable<KeyValuePair<string, int>> components, int count)
        {
            if (bom == null || components == null || count <= 0) return;
            foreach (KeyValuePair<string, int> c in components)
            {
                if (c.Value <= 0 || string.IsNullOrEmpty(c.Key)) continue;
                string invKey = "Comp:" + c.Key;
                long cur;
                bom.TryGetValue(invKey, out cur);
                bom[invKey] = cur + (long)c.Value * count;
            }
        }
    }

    /// <summary>
    /// Keeps a capability's real-block install BOM from requiring its own exclusive output.
    ///
    /// <para>SE's prototech blocks are built out of prototech: <c>LargePrototechAssembler</c> costs
    /// 1 <c>PrototechFrame</c>, 240 <c>PrototechPanel</c>, 20 <c>PrototechMachinery</c> and 2
    /// <c>PrototechCoolingUnit</c>, and the only thing in this world that makes any of them is a
    /// <c>PrototechForge</c> — the capability that block installs. Charged verbatim, the first forge
    /// can never be built and the whole tier is unreachable, silently, with the config reading as
    /// entirely correct. That is not a prototech quirk: any capability whose block is made of what
    /// only that capability makes has it.</para>
    ///
    /// <para><b>Only self-exclusive components are dropped.</b> A component another capability can
    /// make stays on the BOM however scarce it is — which is what leaves
    /// <c>PrototechRefinery</c>'s prototech parts in place, since a forge makes them, and keeps
    /// "you cannot buy a prototech machine without a forge" as a real gate rather than an accident.
    /// A component nothing at all produces also stays: dropping it would quietly cheapen the
    /// install, and an install nobody can pay for is a different problem with its own signal.</para>
    /// </summary>
    public static class InstallBomSelfReference
    {
        /// <summary>Removes from <paramref name="bom"/> every entry that only
        /// <paramref name="cap"/> itself can produce. Returns the removed keys, sorted, for the
        /// resolve-time log line; empty when the BOM was already payable.</summary>
        public static List<string> StripSelfProduced(NodeCapabilityKind cap, Dictionary<string, long> bom,
            Dictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes)
        {
            var dropped = new List<string>();
            if (bom == null || bom.Count == 0 || capRecipes == null) return dropped;

            foreach (var entry in bom)
            {
                if (OnlyProducedBy(cap, entry.Key, capRecipes)) dropped.Add(entry.Key);
            }
            foreach (string key in dropped) bom.Remove(key);
            dropped.Sort(StringComparer.Ordinal);
            return dropped;
        }

        private static bool OnlyProducedBy(NodeCapabilityKind cap, string invKey,
            Dictionary<NodeCapabilityKind, List<ConversionRecipe>> capRecipes)
        {
            bool producedBySelf = false;
            foreach (var entry in capRecipes)
            {
                if (entry.Value == null) continue;
                foreach (ConversionRecipe recipe in entry.Value)
                {
                    if (recipe == null || recipe.Outputs == null) continue;
                    foreach (RecipeItem output in recipe.Outputs)
                    {
                        string key = InventoryKeyMapper.FromTypeIdAndSubtype(output.TypeId, output.SubTypeId);
                        if (!string.Equals(key, invKey, StringComparison.Ordinal)) continue;
                        if (entry.Key != cap) return false;   // someone else can make it
                        producedBySelf = true;
                    }
                }
            }
            return producedBySelf;
        }
    }

    /// <summary>
    /// The set of recipes <c>ResolveCapabilityRecipes</c> drops before any manager sees them.
    /// Excluding here rather than in <c>ProductionManager</c> keeps one source of truth: the same
    /// resolved map is written to <c>production_model.json</c>, so <c>Factions.Sim</c> runs the
    /// identical set without its own filter.
    ///
    /// <para>Two rules, because they answer different questions. <b>Blueprint IDs</b> name one
    /// recipe exactly — the operator's list, for a recipe whose input the economy has no way to
    /// supply. <b>Input keys</b> name a material — a structural rule that keeps holding when SE or a
    /// mod adds another blueprint consuming it. One material is routinely refined by several
    /// blueprints — vanilla stone ore alone has three (<c>StoneOreToIngot</c>,
    /// <c>Position0010_StoneOreToIngotBasic</c>, <c>StoneOreToIngot_Deconstruction</c>) — which is
    /// why the material rule is not expressed as blueprint IDs.</para>
    ///
    /// <para>Input keys are fully qualified <c>TypeId/SubTypeId</c> and are matched against the
    /// recipe's FIRST input only. Both halves matter: <c>MyObjectBuilder_Ore/Stone</c> is stone ore,
    /// while <c>MyObjectBuilder_Ingot/Stone</c> is gravel and is <c>ReactorComponent</c>'s first
    /// input — a rule matching the subtype alone would drop that recipe from every capability.</para>
    /// </summary>
    public sealed class RecipeExclusions
    {
        private readonly HashSet<string> _blueprintIds;
        private readonly HashSet<string> _inputKeys;
        private readonly HashSet<string> _reachable;

        public RecipeExclusions(IEnumerable<string> blueprintIds, IEnumerable<string> inputKeys)
            : this(blueprintIds, inputKeys, null) { }

        /// <param name="reachable">Exact <c>TypeId/SubTypeId</c> keys the economy can obtain, from
        /// <see cref="RecipeReachability.Resolve"/>. Null disables the derived rule and leaves only
        /// the two name lists -- the behaviour before it existed.</param>
        public RecipeExclusions(IEnumerable<string> blueprintIds, IEnumerable<string> inputKeys,
                                HashSet<string> reachable)
        {
            _blueprintIds = ToSet(blueprintIds);
            _inputKeys    = ToSet(inputKeys);
            _reachable    = reachable;
        }

        /// <summary>Blueprints excluded when <c>capability_recipes.json</c> has no
        /// <c>IgnoredBlueprints</c> key — the file is only seeded when absent, so an instance older
        /// than that key still gets the built-in set.
        ///
        /// <para><c>ScrapIngotToIronIngot</c> consumes <c>Ingot/Scrap</c>, which nothing in the
        /// resolved recipe set produces and no node mints. It can never run, but its output
        /// (<c>Ingot/Iron</c>) is demanded, so it clears the admission gate every pass and posts a
        /// full-bank-scale input demand for an item that cannot exist.</para></summary>
        public static List<string> DefaultIgnoredBlueprints()
        {
            return new List<string> { "ScrapIngotToIronIngot" };
        }

        /// <summary>Input keys excluded when <c>capability_recipes.json</c> has no
        /// <c>IgnoredInputKeys</c> key. Scrap ore is never mined, so recipes consuming it only produce
        /// unfillable demand.
        ///
        /// <para><c>Ore/Stone</c> is deliberately NOT here: the mineable node kinds carry a
        /// <c>MineOnly</c> <c>Ore:Stone</c> yield entry, so stone ore has a real source and the
        /// recipes refining it into gravel can actually run. Gravel is <c>ReactorComponent</c>'s
        /// first input, and the passive mint alone could not keep up with it.</para></summary>
        public static List<string> DefaultIgnoredInputKeys()
        {
            return new List<string> { "MyObjectBuilder_Ore/Scrap" };
        }

        /// <summary>Reads both lists off <paramref name="config"/>, substituting the built-in
        /// defaults for a key that is absent (<c>null</c>). An empty list is an explicit
        /// "exclude nothing" and is honoured as written.</summary>
        public static RecipeExclusions From(CapabilityRecipesConfig config)
        {
            return From(config, null);
        }

        /// <summary>As <see cref="From(CapabilityRecipesConfig)"/>, plus the derived
        /// reachability rule. <paramref name="reachable"/> null keeps the name lists alone.</summary>
        public static RecipeExclusions From(CapabilityRecipesConfig config, HashSet<string> reachable)
        {
            List<string> blueprints = config != null ? config.IgnoredBlueprints : null;
            List<string> inputKeys  = config != null ? config.IgnoredInputKeys  : null;
            return new RecipeExclusions(blueprints ?? DefaultIgnoredBlueprints(),
                                        inputKeys  ?? DefaultIgnoredInputKeys(),
                                        reachable);
        }

        /// <summary>True if <paramref name="recipe"/> must not be handed to any capability.
        /// <paramref name="reason"/> is set to the matched rule, for the resolve-time log line.</summary>
        public bool ShouldExclude(ConversionRecipe recipe, out string reason)
        {
            reason = null;
            if (recipe == null) return false;

            if (recipe.BlueprintId != null && _blueprintIds.Contains(recipe.BlueprintId))
            {
                reason = "blueprint";
                return true;
            }

            if (recipe.Inputs != null && recipe.Inputs.Length > 0)
            {
                string key = InputKey(recipe.Inputs[0]);
                if (key != null && _inputKeys.Contains(key))
                {
                    reason = "input " + key;
                    return true;
                }

                // Derived rule: the first input has no source in this world -- nothing mints it and
                // no admitted recipe produces it. This is the computation both shipped name-list
                // entries were justified by, so it subsumes them and keeps holding for a material a
                // mod adds. Matched on the FIRST input only, the same half of the recipe the operator
                // list matches, because the mint set seeding the closure has not been validated
                // against a live modded world yet -- widening to every input risks dropping a recipe
                // whose supply the seed set simply does not describe. See docs/mod-compatibility.md.
                if (key != null && _reachable != null && !_reachable.Contains(key))
                {
                    reason = "unreachable input " + key;
                    return true;
                }
            }

            return false;
        }

        /// <summary>An input's fully qualified <c>TypeId/SubTypeId</c>, or null if either half is
        /// missing (an unqualified item can never match a fully qualified rule).</summary>
        public static string InputKey(RecipeItem item)
        {
            if (string.IsNullOrEmpty(item.TypeId) || string.IsNullOrEmpty(item.SubTypeId)) return null;
            return item.TypeId + "/" + item.SubTypeId;
        }

        private static HashSet<string> ToSet(IEnumerable<string> values)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values != null)
                foreach (string v in values)
                    if (!string.IsNullOrEmpty(v)) set.Add(v.Trim());
            return set;
        }
    }
}
