using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;
using Sandbox.Definitions;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Per-SubTypeId base price catalogue backed by <c>item_prices.json</c>.
    ///
    /// The load path now does three things:
    /// 1. Read the legacy subtype cache from disk.
    /// 2. Seed exact item prices from SE definitions and direct economy prices.
    /// 3. Resolve missing exact prices recursively from blueprints, then collapse back to the
    ///    legacy subtype file for compatibility.
    ///
    /// This keeps the old JSON format intact while letting component and ingot pricing fill in
    /// instead of silently falling back to the tiny policy defaults.
    /// </summary>
    public sealed class SeItemPriceCatalogue : IItemPriceCatalogue
    {
        private readonly Dictionary<string, long> _prices;
        private readonly Dictionary<string, long> _exactPrices;

        public int Count { get { return _prices.Count; } }
        public int ExactCount { get { return _exactPrices.Count; } }
        public int DirectSeedCount { get; private set; }
        public int RecipeResolvedCount { get; private set; }
        public int LegacyFallbackCount { get; private set; }
        public int UnresolvedCount { get; private set; }

        /// <summary>Unpriced items a single-output blueprint DOES produce -- the derivation exists but
        /// some input upstream has no price, so the chain never closed.</summary>
        public int UnresolvedBlockedChain { get; private set; }

        /// <summary>Unpriced items produced only by multi-result blueprints, which
        /// <see cref="ItemPriceResolver"/> cannot apportion. An unimplemented derivation, not missing
        /// data.</summary>
        public int UnresolvedMultiOutput { get; private set; }

        /// <summary>Unpriced items no blueprint produces. Nothing to derive from -- these need price
        /// data or they stay at their ItemKind placeholder wherever they are summed.</summary>
        public int UnresolvedNoProducer { get; private set; }

        /// <summary>A few names per reason, for the boot line.</summary>
        public string UnresolvedSample { get; private set; }

        private static string BuildUnresolvedSample(List<string> blockedChain,
            List<string> multiOutputOnly, List<string> noProducer)
        {
            var parts = new List<string>();
            AppendSample(parts, "blocked-chain", blockedChain);
            AppendSample(parts, "multi-output", multiOutputOnly);
            AppendSample(parts, "no-producer", noProducer);
            return string.Join("; ", parts.ToArray());
        }

        private static void AppendSample(List<string> parts, string label, List<string> names)
        {
            if (names == null || names.Count == 0) return;
            names.Sort(StringComparer.OrdinalIgnoreCase);
            int take = names.Count < 8 ? names.Count : 8;
            string joined = string.Join(", ", names.GetRange(0, take).ToArray());
            parts.Add(label + " (" + names.Count + "): " + joined + (names.Count > take ? " ..." : ""));
        }

        private SeItemPriceCatalogue(Dictionary<string, long> prices, Dictionary<string, long> exactPrices)
        {
            _prices = prices;
            _exactPrices = exactPrices;
        }

        public long BasePrice(string subTypeId)
        {
            if (string.IsNullOrEmpty(subTypeId)) return 0;

            long value;
            string exactKey = ItemPriceResolver.ExactKeyFromInventoryKey(subTypeId);
            if (!string.IsNullOrEmpty(exactKey) && _exactPrices.TryGetValue(exactKey, out value))
                return value;

            string normalized = ItemPriceResolver.StripInventoryPrefix(subTypeId);
            return _prices.TryGetValue(normalized, out value) ? value : 0;
        }

        /// <summary>
        /// Build the catalogue from disk + SE definitions. Call at <c>TorchSessionState.Loaded</c>.
        /// The returned instance is safe to use for the rest of the session.
        /// </summary>
        /// <param name="extraRecipes">Conversions the blueprint scan below cannot see, resolved
        /// alongside it. Gas is the case this exists for: an O2/H2 generator converts ice from its own
        /// <c>MyOxygenGeneratorDefinition</c> fields rather than a blueprint, and its product has no
        /// <c>MyPhysicalItemDefinition</c> either, so both the direct seed and the recipe scan miss it
        /// and <c>BasePrice("Gas:Hydrogen")</c> answers 0. These recipes are single-input
        /// single-output, which is exactly the shape the resolver already handles, so the price comes
        /// out of SE's own IceToGasRatio instead of being chosen. Their outputs are in litres and are
        /// converted to kilolitres first — see <see cref="PricedInStoreUnits"/>.</param>
        public static SeItemPriceCatalogue Load(string storageDir, FactionTuning fallback,
            IEnumerable<ConversionRecipe> extraRecipes)
        {
            string path = Path.Combine(storageDir, "item_prices.json");

            var legacySubtypePrices = LoadLegacySubtypePrices(path);
            var directExactSeeds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var physicalDefs = new List<MyPhysicalItemDefinition>();
            var recipeOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var multiOutputProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            MySessionComponentEconomy econ = MySession.Static != null ? MySession.Static.GetComponent<MySessionComponentEconomy>() : null;

            if (MyDefinitionManager.Static != null)
            {
                foreach (MyPhysicalItemDefinition def in MyDefinitionManager.Static.GetPhysicalItemDefinitions())
                {
                    if (def == null) continue;
                    physicalDefs.Add(def);

                    string subtype = def.Id.SubtypeName;
                    string exactKey = ItemPriceResolver.ExactKey(def.Id.TypeId.ToString(), subtype);
                    long price = ResolveDirectItemPrice(def, econ, fallback);
                    if (price > 0 && !string.IsNullOrEmpty(exactKey))
                        directExactSeeds[exactKey] = price;
                }

                foreach (MyBlueprintDefinitionBase bp in MyDefinitionManager.Static.GetBlueprintDefinitions())
                {
                    if (bp == null) continue;
                    // A blueprint with several results is not derivable by ItemPriceResolver: it
                    // prices an output by dividing the input cost, which needs one output to divide
                    // by. Recorded so an unpriced item can say that is why, rather than looking like
                    // an item nothing produces.
                    if (bp.Results != null && bp.Results.Length > 1 && bp.Prerequisites != null
                        && bp.Prerequisites.Length > 0)
                    {
                        foreach (MyBlueprintDefinitionBase.Item multi in bp.Results)
                        {
                            string k = ItemPriceResolver.ExactKey(multi.Id.TypeId.ToString(), multi.Id.SubtypeName);
                            if (!string.IsNullOrEmpty(k)) multiOutputProducts.Add(k);
                        }
                    }
                    if (bp.Results == null || bp.Results.Length != 1) continue;
                    if (bp.Prerequisites == null || bp.Prerequisites.Length == 0) continue;

                    MyBlueprintDefinitionBase.Item result = bp.Results[0];

                    MyPhysicalItemDefinition outputDef;
                    if (!MyDefinitionManager.Static.TryGetPhysicalItemDefinition(result.Id, out outputDef) || outputDef == null)
                        continue;

                    string outputKey = ItemPriceResolver.ExactKey(result.Id.TypeId.ToString(), result.Id.SubtypeName);
                    if (!string.IsNullOrEmpty(outputKey))
                        recipeOutputs.Add(outputKey);
                }
            }

            // Seed unresolved physical items from the legacy subtype file only when they are not
            // blueprint-produced items. This keeps ambiguous subtype entries from clobbering the
            // exact prices we can now derive from recipes.
            foreach (MyPhysicalItemDefinition def in physicalDefs)
            {
                if (def == null) continue;
                string subtype = def.Id.SubtypeName;
                if (string.IsNullOrEmpty(subtype)) continue;

                string exactKey = ItemPriceResolver.ExactKey(def.Id.TypeId.ToString(), subtype);
                if (string.IsNullOrEmpty(exactKey) || directExactSeeds.ContainsKey(exactKey))
                    continue;

                long legacyValue;
                if (recipeOutputs.Contains(exactKey))
                    continue;
                if (legacySubtypePrices.TryGetValue(subtype, out legacyValue) && legacyValue > 0)
                    directExactSeeds[exactKey] = legacyValue;
            }

            List<ConversionRecipe> recipes = ScanPhysicalItemRecipes();
            if (extraRecipes != null) recipes.AddRange(PricedInStoreUnits(extraRecipes));
            var exactPrices = ItemPriceResolver.ResolveExactPrices(recipes, directExactSeeds);
            ApplySeGasStorePrices(exactPrices);

            int recipeResolved = 0;
            foreach (KeyValuePair<string, long> kv in exactPrices)
            {
                long direct;
                if (!directExactSeeds.TryGetValue(kv.Key, out direct) || direct != kv.Value)
                    recipeResolved++;
            }

            int legacyFallbackCount = 0;
            foreach (MyPhysicalItemDefinition def in physicalDefs)
            {
                if (def == null) continue;
                string subtype = def.Id.SubtypeName;
                if (string.IsNullOrEmpty(subtype)) continue;

                string exactKey = ItemPriceResolver.ExactKey(def.Id.TypeId.ToString(), subtype);
                long value;
                if (exactPrices.TryGetValue(exactKey, out value) && value > 0)
                    continue;

                long legacyValue;
                if (legacySubtypePrices.TryGetValue(subtype, out legacyValue) && legacyValue > 0)
                {
                    exactPrices[exactKey] = legacyValue;
                    legacyFallbackCount++;
                }
            }

            var collapsed = ItemPriceResolver.CollapseToSubtypePrices(exactPrices);
            var finalPrices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, long> kv in collapsed)
                if (kv.Value > 0)
                    finalPrices[kv.Key] = kv.Value;

            // An item with no price is one of three distinct situations, and they have different
            // answers: a blocked chain is a resolver problem, a multi-output blueprint is an
            // unimplemented derivation, and no producing blueprint at all needs price data because
            // there is nothing to derive from. Counting them together hides which.
            int unresolvedCount = 0;
            var blockedChain    = new List<string>();
            var multiOutputOnly = new List<string>();
            var noProducer      = new List<string>();
            foreach (MyPhysicalItemDefinition def in physicalDefs)
            {
                if (def == null) continue;
                string subtype = def.Id.SubtypeName;
                if (string.IsNullOrEmpty(subtype)) continue;
                string exactKey = ItemPriceResolver.ExactKey(def.Id.TypeId.ToString(), subtype);
                long value;
                if (exactPrices.TryGetValue(exactKey, out value) && value > 0) continue;

                unresolvedCount++;
                if (recipeOutputs.Contains(exactKey))            blockedChain.Add(subtype);
                else if (multiOutputProducts.Contains(exactKey)) multiOutputOnly.Add(subtype);
                else                                            noProducer.Add(subtype);
            }
            string unresolvedSample = BuildUnresolvedSample(blockedChain, multiOutputOnly, noProducer);

            bool dirty = false;
            if (!File.Exists(path))
                dirty = true;
            else
            {
                var onDisk = LoadLegacySubtypePrices(path);
                if (onDisk.Count != finalPrices.Count)
                    dirty = true;
                else
                    foreach (KeyValuePair<string, long> kv in finalPrices)
                    {
                        long existing;
                        if (!onDisk.TryGetValue(kv.Key, out existing) || existing != kv.Value)
                        {
                            dirty = true;
                            break;
                        }
                    }
            }

            if (dirty)
            {
                try
                {
                    Directory.CreateDirectory(storageDir);
                    File.WriteAllText(path, JsonConvert.SerializeObject(finalPrices, Formatting.Indented));
                }
                catch
                {
                    /* best-effort */
                }
            }

            return new SeItemPriceCatalogue(finalPrices, exactPrices)
            {
                DirectSeedCount = directExactSeeds.Count,
                RecipeResolvedCount = recipeResolved,
                LegacyFallbackCount = legacyFallbackCount,
                UnresolvedCount = unresolvedCount,
                UnresolvedBlockedChain = blockedChain.Count,
                UnresolvedMultiOutput = multiOutputOnly.Count,
                UnresolvedNoProducer = noProducer.Count,
                UnresolvedSample = unresolvedSample,
            };
        }

        /// <summary>The same recipes with every gas output restated in <b>kilolitres</b>, which is
        /// the unit a gas is priced in — <c>MyFactionTypeDefinition.MinimumHydrogenPrice</c> is
        /// quoted per kilolitre and a store row sells one. The resolver divides an input cost by an
        /// output amount, so handing it litres would derive a credits-per-litre price, and a gas is
        /// cheap enough per litre that the integer it resolves to is nearly all rounding.</summary>
        private static IEnumerable<ConversionRecipe> PricedInStoreUnits(IEnumerable<ConversionRecipe> recipes)
        {
            foreach (ConversionRecipe recipe in recipes)
            {
                if (recipe.Outputs == null) { yield return recipe; continue; }

                var outputs = new RecipeItem[recipe.Outputs.Length];
                for (int i = 0; i < recipe.Outputs.Length; i++)
                {
                    RecipeItem item = recipe.Outputs[i];
                    if (item.TypeId == GasPropertiesTypeId)
                        item.Amount = item.Amount / GasStoreUnits.LitresPerStoreUnit;
                    outputs[i] = item;
                }

                // A new instance: the same recipe objects drive production, where the output is
                // litres and has to stay litres.
                yield return new ConversionRecipe
                {
                    BlueprintId = recipe.BlueprintId,
                    Inputs      = recipe.Inputs,
                    Outputs     = outputs,
                    TimeSecs    = recipe.TimeSecs,
                    IgnoresEfficiencyMultiplier = recipe.IgnoresEfficiencyMultiplier,
                };
            }
        }

        /// <summary>Overwrites the price of every gas SE sells itself with SE's own number.
        ///
        /// <para>The ice-cost derivation above answers what a kilolitre of hydrogen <b>costs</b> to
        /// make — ice at its <c>MinimalPricePerUnit</c> through <c>IceToGasRatio</c>. That is not
        /// what it sells for: SE prices gas from a flat <c>MinimumHydrogenPrice</c> on the faction
        /// type, an order of magnitude under its own ice, and that is the number on the shelf of
        /// every vanilla economy station in the same world. A station of ours quoting the cost
        /// instead would be the only expensive seller in the market.</para>
        ///
        /// <para>Lowest across the faction types that can sell it, so the price matches the cheapest
        /// vanilla station a player could walk to instead. A gas no faction type sells — water — has
        /// no SE price and keeps the derived one.</para></summary>
        private static void ApplySeGasStorePrices(IDictionary<string, long> exactPrices)
        {
            if (MyDefinitionManager.Static == null) return;

            long hydrogen = 0, oxygen = 0;
            foreach (MyFactionTypeDefinition def in MyDefinitionManager.Static.GetAllDefinitions<MyFactionTypeDefinition>())
            {
                if (def == null) continue;
                if (def.CanSellHydrogen && def.MinimumHydrogenPrice > 0
                    && (hydrogen == 0 || def.MinimumHydrogenPrice < hydrogen))
                    hydrogen = def.MinimumHydrogenPrice;
                if (def.CanSellOxygen && def.MinimumOxygenPrice > 0
                    && (oxygen == 0 || def.MinimumOxygenPrice < oxygen))
                    oxygen = def.MinimumOxygenPrice;
            }

            if (hydrogen > 0) exactPrices[ItemPriceResolver.ExactKey(GasPropertiesTypeId, "Hydrogen")] = hydrogen;
            if (oxygen > 0) exactPrices[ItemPriceResolver.ExactKey(GasPropertiesTypeId, "Oxygen")] = oxygen;
        }

        /// <summary>TypeId every gas carries. Gas has no <c>MyPhysicalItemDefinition</c>, so this is
        /// the only thing that identifies one inside a recipe.</summary>
        private const string GasPropertiesTypeId = "MyObjectBuilder_GasProperties";

        private static Dictionary<string, long> LoadLegacySubtypePrices(string path)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return result;

            try
            {
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, long>>(File.ReadAllText(path));
                if (loaded == null) return result;

                foreach (KeyValuePair<string, long> kv in loaded)
                {
                    if (kv.Value <= 0) continue;
                    string normalized = ItemPriceResolver.StripInventoryPrefix(kv.Key);
                    result[normalized] = kv.Value;
                }
            }
            catch
            {
                /* corrupt JSON â€” start fresh, will be rebuilt below */
            }

            return result;
        }

        private static long ResolveDirectItemPrice(MyPhysicalItemDefinition def, MySessionComponentEconomy econ,
            FactionTuning fallback)
        {
            if (def == null) return 0;

            int sePrice = def.MinimalPricePerUnit;
            if (sePrice <= 0 && econ != null)
                sePrice = ResolveMinimumItemPrice(econ, def.Id);

            if (sePrice <= 0) return 0;
            return (long)Math.Round(sePrice * fallback.PriceScaleMultiplier);
        }

        private static List<ConversionRecipe> ScanPhysicalItemRecipes()
        {
            var result = new List<ConversionRecipe>();
            if (MyDefinitionManager.Static == null) return result;

            try
            {
                foreach (MyBlueprintDefinitionBase bp in MyDefinitionManager.Static.GetBlueprintDefinitions())
                {
                    if (bp == null) continue;
                    if (bp.Prerequisites == null || bp.Prerequisites.Length == 0) continue;
                    if (bp.Results == null || bp.Results.Length != 1) continue;

                    MyBlueprintDefinitionBase.Item resultItem = bp.Results[0];

                    MyPhysicalItemDefinition outputDef;
                    if (!MyDefinitionManager.Static.TryGetPhysicalItemDefinition(resultItem.Id, out outputDef) || outputDef == null)
                        continue;

                    var inputs = new RecipeItem[bp.Prerequisites.Length];
                    for (int i = 0; i < bp.Prerequisites.Length; i++)
                    {
                        inputs[i] = new RecipeItem
                        {
                            TypeId = bp.Prerequisites[i].Id.TypeId.ToString(),
                            SubTypeId = bp.Prerequisites[i].Id.SubtypeName,
                            Amount = (double)(float)bp.Prerequisites[i].Amount,
                        };
                    }

                    result.Add(new ConversionRecipe
                    {
                        BlueprintId = bp.Id.SubtypeName,
                        Inputs = inputs,
                        Outputs = new[]
                        {
                            new RecipeItem
                            {
                                TypeId = resultItem.Id.TypeId.ToString(),
                                SubTypeId = resultItem.Id.SubtypeName,
                                Amount = (double)(float)resultItem.Amount,
                            }
                        },
                        TimeSecs = bp.BaseProductionTimeInSeconds,
                    });
                }
            }
            catch
            {
                /* definition manager not ready */
            }

            return result;
        }

        private static int ResolveMinimumItemPrice(MySessionComponentEconomy econ, MyDefinitionId id)
        {
            try
            {
                var method = econ.GetType().GetMethod("GetMinimumItemPrice",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(MyDefinitionId) }, null);
                if (method == null) return 0;
                object value = method.Invoke(econ, new object[] { id });
                if (value == null) return 0;
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }
    }
}
