using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The bank recipe index: a capability bank reaches the recipes its faction's demand admits
    /// through an output-keyed index instead of evaluating every recipe in the capability's list.
    ///
    /// <para>Two properties carry the whole change and both are pinned here. The set the index
    /// produces must be exactly the set a full scan would have admitted, <b>in list order</b> —
    /// the bank ranks equal scarcities by which recipe it saw first, so a set in demand-set order
    /// would silently re-decide contested slots. And nothing may be excluded from the index itself:
    /// a recipe nothing wants today has to become selectable the tick something wants it, or the
    /// saving is a content cut that no later demand can undo.</para>
    /// </summary>
    public sealed class ProductionBankCandidateIndexTests
    {
        private static RecipeItem Ingot(string subtype, double amount)
        {
            return new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = subtype, Amount = amount };
        }

        private static RecipeItem Comp(string subtype, double amount)
        {
            return new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = subtype, Amount = amount };
        }

        private static ConversionRecipe Recipe(string id, RecipeItem[] inputs, RecipeItem[] outputs, float timeSecs = 1f)
        {
            return new ConversionRecipe { BlueprintId = id, TimeSecs = timeSecs, Inputs = inputs, Outputs = outputs };
        }

        /// <summary>Mirrors the live ManufactoryII shape the ticket measured: a long list, a small
        /// demanded slice, and the awkward members a naive index gets wrong — a recipe with two
        /// demanded outputs, a recipe whose only demanded output carries a zero amount, and three
        /// structurally unusable recipes the bank loop drops before it looks at demand at all.</summary>
        private static List<ConversionRecipe> MixedList()
        {
            return new List<ConversionRecipe>
            {
                Recipe("Wanted_First",   new[] { Ingot("Iron", 1) },  new[] { Comp("SteelPlate", 1) }),
                Recipe("Unwanted_A",     new[] { Ingot("Iron", 1) },  new[] { Comp("AceShell", 1) }),
                Recipe("Wanted_TwoOuts", new[] { Ingot("Iron", 1) },  new[] { Comp("SteelPlate", 1), Comp("Motor", 1) }),
                Recipe("Unwanted_B",     new[] { Ingot("Iron", 1) },  new[] { Comp("Firework", 1) }),
                Recipe("ZeroAmountOut",  new[] { Ingot("Iron", 1) },  new[] { Comp("SteelPlate", 0) }),
                Recipe("Wanted_Last",    new[] { Ingot("Iron", 1) },  new[] { Comp("Motor", 1) }),
                Recipe("NoInputs",       new RecipeItem[0],           new[] { Comp("SteelPlate", 1) }),
                Recipe("NoOutputs",      new[] { Ingot("Iron", 1) },  new RecipeItem[0]),
                Recipe("ZeroTime",       new[] { Ingot("Iron", 1) },  new[] { Comp("SteelPlate", 1) }, timeSecs: 0f),
            };
        }

        private static ProductionManager ManagerFor(List<ConversionRecipe> recipes,
            NodeCapabilityKind cap = NodeCapabilityKind.ManufactoryII)
        {
            return new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { cap, recipes },
            });
        }

        private static List<string> ResolveCandidateIds(ProductionManager manager, NodeCapabilityKind cap,
            HashSet<string> demandedKeys)
        {
            MethodInfo method = typeof(ProductionManager).GetMethod("ResolveBankCandidates",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var resolved = (List<ConversionRecipe>)method.Invoke(manager, new object[] { cap, demandedKeys });
            return resolved.Select(r => r.BlueprintId).ToList();
        }

        /// <summary>ProductionManager.InvKey is internal to Core; this is the same mapping for the
        /// two item kinds these fixtures use, restated so the expectation is independent of it.</summary>
        private static string TestInvKey(RecipeItem item)
        {
            return (item.TypeId == "MyObjectBuilder_Ingot" ? "Ingot:" : "Comp:") + item.SubTypeId;
        }

        /// <summary>The predicate the bank loop applied before the index existed, written out so the
        /// expectation is the old behaviour rather than a restatement of the new code.</summary>
        private static List<string> FullScanCandidateIds(List<ConversionRecipe> recipes, HashSet<string> demandedKeys)
        {
            var ids = new List<string>();
            foreach (ConversionRecipe recipe in recipes)
            {
                if (recipe.Inputs  == null || recipe.Inputs.Length  == 0) continue;
                if (recipe.Outputs == null || recipe.Outputs.Length == 0) continue;
                if (recipe.TimeSecs <= 0f) continue;
                bool demanded = false;
                foreach (RecipeItem outp in recipe.Outputs)
                {
                    if (outp.Amount <= 0) continue;
                    if (!demandedKeys.Contains(TestInvKey(outp))) continue;
                    demanded = true;
                    break;
                }
                if (demanded) ids.Add(recipe.BlueprintId);
            }
            return ids;
        }

        [Fact]
        public void CandidateSet_IsExactlyWhatAFullScanWouldAdmit()
        {
            List<ConversionRecipe> recipes = MixedList();
            var demanded = new HashSet<string> { "Comp:SteelPlate", "Comp:Motor", "Comp:NothingMakesThis" };

            List<string> expected = FullScanCandidateIds(recipes, demanded);
            List<string> actual = ResolveCandidateIds(ManagerFor(recipes), NodeCapabilityKind.ManufactoryII, demanded);

            // Sanity: the fixture actually discriminates. An expectation equal to the whole list, or
            // to nothing, would make this assertion pass without testing the filter.
            Assert.Equal(new[] { "Wanted_First", "Wanted_TwoOuts", "Wanted_Last" }, expected);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void CandidateSet_IsInRecipeListOrder_NotDemandSetOrder()
        {
            // The bank's insertion sort keeps the first-seen recipe ahead of a later one at equal
            // scarcity, so the order the candidates arrive in decides contested slots. Demand is
            // added here in the reverse of list order: an index that returned recipes in the order
            // the demand set enumerates would hand Wanted_Last the slot Wanted_First won before.
            List<ConversionRecipe> recipes = MixedList();
            var demanded = new HashSet<string>();
            demanded.Add("Comp:Motor");        // produced by Wanted_TwoOuts (idx 2) and Wanted_Last (idx 5)
            demanded.Add("Comp:SteelPlate");   // produced by Wanted_First (idx 0) and Wanted_TwoOuts (idx 2)

            List<string> actual = ResolveCandidateIds(ManagerFor(recipes), NodeCapabilityKind.ManufactoryII, demanded);

            Assert.Equal(new[] { "Wanted_First", "Wanted_TwoOuts", "Wanted_Last" }, actual);
        }

        [Fact]
        public void ARecipeWithTwoDemandedOutputs_AppearsOnce()
        {
            List<ConversionRecipe> recipes = MixedList();
            var demanded = new HashSet<string> { "Comp:SteelPlate", "Comp:Motor" };

            List<string> actual = ResolveCandidateIds(ManagerFor(recipes), NodeCapabilityKind.ManufactoryII, demanded);

            Assert.Equal(1, actual.Count(id => id == "Wanted_TwoOuts"));
        }

        [Fact]
        public void NoDemandAtAll_AdmitsNothing()
        {
            List<ConversionRecipe> recipes = MixedList();
            Assert.Empty(ResolveCandidateIds(ManagerFor(recipes), NodeCapabilityKind.ManufactoryII, new HashSet<string>()));
        }

        // ---- Decision 1: the set stays complete -------------------------------------------------

        private static WorldModel WorldWithManufactory(long ingotsOnHand)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.ManufactoryII] = 1;
            if (ingotsOnHand > 0) node.Inventory["Ingot:Iron"] = ingotsOnHand;
            return world;
        }

        private static void PostItemDemand(WorldModel world, string key, long quantity)
        {
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + key, NodeId = "N1",
                Quantity = quantity, ExpiresUtc = world.UtcNow.AddHours(4),
            });
        }

        [Fact]
        public void ANeverDemandedRecipe_BecomesSelectable_TheTickSomethingDemandsIt()
        {
            // The completeness half. The same manager instance runs both halves, so its index was
            // built once with AceShell in it and nothing re-admits the recipe later: if the index
            // had dropped what nothing wanted at construction, no demand could bring it back.
            var recipes = new List<ConversionRecipe>
            {
                Recipe("SteelPlate", new[] { Ingot("Iron", 1) }, new[] { Comp("SteelPlate", 1) }),
                Recipe("AceShell",   new[] { Ingot("Iron", 1) }, new[] { Comp("AceShell", 1) }),
            };
            // Iron on hand asks for every output the node is short of, so the premise "nobody asked
            // for AceShell" has to be stated: the node is not short of it until something demands it.
            var oracle = new SettableScarcityOracle();
            oracle.Scarcity["Comp:SteelPlate"] = 2.0;
            ProductionManager manager = new ProductionManager(
                new Dictionary<NodeCapabilityKind, List<ConversionRecipe>> { { NodeCapabilityKind.ManufactoryII, recipes } },
                null, oracle);
            WorldModel world = WorldWithManufactory(500000);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };

            PostItemDemand(world, "Comp:SteelPlate", 100000);
            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(states, world, world.Tuning, new List<string>());
            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(states, world, world.Tuning, new List<string>());

            // Nobody asked for AceShell, so nothing was made -- and the demanded recipe DID run, so
            // this is a statement about the gate and not about an idle bank.
            Assert.Equal(0, world.GetNodeInventory("N1", "Comp:AceShell"));
            Assert.True(world.GetNodeInventory("N1", "Comp:SteelPlate") > 0);

            PostItemDemand(world, "Comp:AceShell", 100000);
            oracle.Scarcity["Comp:AceShell"] = 2.0;
            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(states, world, world.Tuning, new List<string>());
            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(states, world, world.Tuning, new List<string>());

            Assert.True(world.GetNodeInventory("N1", "Comp:AceShell") > 0);
        }

        // ---- Decision 2's trap: FAC-164 -------------------------------------------------------

        [Fact]
        public void ADemandedRecipeWithNoInputOnHand_StillPostsItsInputDemand()
        {
            // FAC-164's regression, and the specific defect a "skip what cannot run" index would
            // reintroduce: the recipe with nothing to work with is exactly the one that must ask.
            // The undemanded recipe beside it is what makes the index do real filtering here, so a
            // version that silenced the asking while skipping would fail this rather than pass it
            // vacuously.
            var recipes = new List<ConversionRecipe>
            {
                Recipe("SteelPlate", new[] { Ingot("Iron", 1) },   new[] { Comp("SteelPlate", 1) }),
                Recipe("AceShell",   new[] { Ingot("Cobalt", 1) }, new[] { Comp("AceShell", 1) }),
            };
            ProductionManager manager = ManagerFor(recipes);
            WorldModel world = WorldWithManufactory(0);
            PostItemDemand(world, "Comp:SteelPlate", 100000);

            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            List<DemandEntry> demands = world.Demand.SnapshotItemDemands("F");
            Assert.Contains(demands, d => d.Kind == "Item:Ingot:Iron" && d.Quantity > 0);
            // The undemanded recipe's own input is not asked for -- it was never admitted, exactly as
            // a full scan would also have left it.
            Assert.DoesNotContain(demands, d => d.Kind == "Item:Ingot:Cobalt");
        }

        // ---- what the saving looks like in telemetry ------------------------------------------

        [Fact]
        public void SkippedRecipes_AreCountedRatherThanEvaluated()
        {
            // RecipesExamined is now evaluations, so it FALLS when the index does its job; the
            // recipes it reached past are carried in RecipesSkippedNoDemand instead, and the two
            // together are still the usable set the banks stood in front of. The three structurally
            // unusable members of MixedList are in neither: the bank never reached them either.
            List<ConversionRecipe> recipes = MixedList();
            ProductionManager manager = ManagerFor(recipes);
            WorldModel world = WorldWithManufactory(500000);
            PostItemDemand(world, "Comp:SteelPlate", 100000);

            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            ProductionTickTelemetry telemetry = manager.LastTelemetry;
            Assert.Equal(2, telemetry.RecipesExamined);          // Wanted_First, Wanted_TwoOuts
            Assert.Equal(4, telemetry.RecipesSkippedNoDemand);   // Unwanted_A/B, ZeroAmountOut, Wanted_Last
            Assert.Equal(6, telemetry.RecipesExamined + telemetry.RecipesSkippedNoDemand);
        }

        /// <summary>A scarcity per key a test can change between ticks; 1.0 (not short) otherwise.</summary>
        private sealed class SettableScarcityOracle : IPriceOracle
        {
            public readonly Dictionary<string, double> Scarcity = new Dictionary<string, double>();

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                double value;
                return Scarcity.TryGetValue(inventoryKey, out value) ? value : 1.0;
            }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference) { reference = 0; return false; }
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
        }
    }
}
