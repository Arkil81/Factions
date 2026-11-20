using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The derived answer to "can this recipe ever run?" -- what nodes mint, closed over the
    /// recipe set. Replaces hand-curated name lists for that question; see docs/mod-compatibility.md.</summary>
    public class RecipeReachabilityTests
    {
        private static RecipeItem Item(string typeId, string subTypeId, double amount = 1)
            => new RecipeItem { TypeId = typeId, SubTypeId = subTypeId, Amount = amount };

        private static ConversionRecipe Recipe(string id, RecipeItem[] inputs, RecipeItem[] outputs)
            => new ConversionRecipe { BlueprintId = id, Inputs = inputs, Outputs = outputs, TimeSecs = 1 };

        private const string Ore  = "MyObjectBuilder_Ore";
        private const string Ingt = "MyObjectBuilder_Ingot";
        private const string Comp = "MyObjectBuilder_Component";
        private const string Gas  = "MyObjectBuilder_GasProperties";

        /// <summary>What a real world's geography reports: bare ore subtypes, world-scoped.</summary>
        private static readonly string[] WorldOres = { "Iron", "Nickel", "Cobalt", "Silicon", "Ice" };

        [Fact]
        public void MintedExactKeys_ConvertsPrefixedYieldKeysToRecipeInputForm()
        {
            HashSet<string> minted = RecipeReachability.MintedExactKeys(TestSupport.Tuning(), null);

            Assert.Contains("MyObjectBuilder_Ore/Stone", minted);
        }

        // FAC-233 removed the Ingot:Stone row, so gravel is no longer SEEDED as minted -- it has to
        // be earned through the closure instead. That is the whole risk of the removal: an output
        // nobody can reach is FAC-049's dead recipe, billing full-bank input demand every pass
        // forever. ShippedTableAlone_ReachesNothingButStone below is the other half, proving the
        // closure does reach it via Ore:Stone + StoneOreToIngot.
        [Fact]
        public void MintedExactKeys_NoLongerSeedsGravelAsMinted()
        {
            Assert.DoesNotContain("MyObjectBuilder_Ingot/Stone",
                RecipeReachability.MintedExactKeys(TestSupport.Tuning(), WorldOres));
        }

        [Fact]
        public void MintedExactKeys_IncludesMineOnlyEntries()
        {
            // Ore:Stone is MineOnly -- no passive mint, but a Mine run is a real source, which is
            // exactly why the shipped exclusion list deliberately does NOT carry it.
            Assert.Contains("MyObjectBuilder_Ore/Stone",
                RecipeReachability.MintedExactKeys(TestSupport.Tuning(), null));
        }

        [Fact]
        public void MintedExactKeys_DoesNotInventScrapOre()
        {
            // The other half of the same pair: nothing mints scrap, so the curated entry that names
            // it is derivable rather than a fact someone has to remember.
            Assert.DoesNotContain("MyObjectBuilder_Ore/Scrap",
                RecipeReachability.MintedExactKeys(TestSupport.Tuning(), null));
        }

        // The seed is what the world can supply, and since FAC-102 the yield table only states stone
        // -- every other ore comes from the ground. Seeded from the table alone the closure is
        // stone-only, which excluded the entire ore-to-ingot tier. See FAC-111.
        [Fact]
        public void MintedExactKeys_IncludesEveryOreTheGeographyCanPlace()
        {
            HashSet<string> minted = RecipeReachability.MintedExactKeys(TestSupport.Tuning(), WorldOres);

            Assert.Contains("MyObjectBuilder_Ore/Iron", minted);
            Assert.Contains("MyObjectBuilder_Ore/Ice", minted);
            Assert.Contains("MyObjectBuilder_Ore/Stone", minted);   // the table's half survives
        }

        // The regression, stated as its consumer sees it: seeded from the SHIPPED table plus a real
        // geography, the ore-to-ingot tier and everything rooted in it must survive the closure.
        // Asserting on the seed alone is what let this through -- the seed shrinking looked correct.
        [Fact]
        public void ShippedTablePlusGeography_KeepsTheOreToIngotTierReachable()
        {
            var recipes = new[]
            {
                Recipe("IronOreToIngot",  new[] { Item(Ore, "Iron") },      new[] { Item(Ingt, "Iron") }),
                Recipe("StoneOreToIngot", new[] { Item(Ore, "Stone") },     new[] { Item(Ingt, "Stone") }),
                Recipe("SteelPlate",      new[] { Item(Ingt, "Iron") },     new[] { Item(Comp, "SteelPlate") }),
                Recipe("GasIceToHydrogen",new[] { Item(Ore, "Ice") },       new[] { Item(Gas,  "Hydrogen") }),
            };

            HashSet<string> reachable = RecipeReachability.Resolve(
                RecipeReachability.MintedExactKeys(TestSupport.Tuning(), WorldOres), recipes);

            Assert.Contains("MyObjectBuilder_Ingot/Iron", reachable);
            Assert.Contains("MyObjectBuilder_Component/SteelPlate", reachable);
            // Gas is a GasProperties definition, not an ingot: an O2/H2 generator's ProducedGases
            // names MyObjectBuilder_GasProperties, and that is the type SeRecipeRegistry emits.
            Assert.Contains("MyObjectBuilder_GasProperties/Hydrogen", reachable);
        }

        // The defect itself, pinned: with the shipped table and no geography the closure really is
        // stone-only. Kept so the fix cannot be quietly undone by narrowing the seed again.
        [Fact]
        public void ShippedTableAlone_ReachesNothingButStone()
        {
            var recipes = new[]
            {
                Recipe("IronOreToIngot",  new[] { Item(Ore, "Iron") },  new[] { Item(Ingt, "Iron") }),
                Recipe("StoneOreToIngot", new[] { Item(Ore, "Stone") }, new[] { Item(Ingt, "Stone") }),
            };

            HashSet<string> reachable = RecipeReachability.Resolve(
                RecipeReachability.MintedExactKeys(TestSupport.Tuning(), null), recipes);

            Assert.DoesNotContain("MyObjectBuilder_Ingot/Iron", reachable);
            Assert.Contains("MyObjectBuilder_Ingot/Stone", reachable);
        }

        [Fact]
        public void Resolve_ClosesAChainFromTheMintSet()
        {
            var recipes = new[]
            {
                Recipe("IronOreToIngot",  new[] { Item(Ore,  "Iron")  }, new[] { Item(Ingt, "Iron") }),
                Recipe("IronIngotToPlate", new[] { Item(Ingt, "Iron") }, new[] { Item(Comp, "SteelPlate") }),
            };

            HashSet<string> reachable = RecipeReachability.Resolve(
                new[] { "MyObjectBuilder_Ore/Iron" }, recipes);

            Assert.Contains("MyObjectBuilder_Ingot/Iron", reachable);
            Assert.Contains("MyObjectBuilder_Component/SteelPlate", reachable);
        }

        [Fact]
        public void Resolve_IsOrderIndependent_AChainListedBackwardsStillCloses()
        {
            // The fixed point must not depend on declaration order: the consumer is listed before
            // its producer here, so a single forward pass would stop after one step.
            var recipes = new[]
            {
                Recipe("IngotToPlate", new[] { Item(Ingt, "Iron") }, new[] { Item(Comp, "SteelPlate") }),
                Recipe("OreToIngot",   new[] { Item(Ore,  "Iron") }, new[] { Item(Ingt, "Iron") }),
            };

            HashSet<string> reachable = RecipeReachability.Resolve(
                new[] { "MyObjectBuilder_Ore/Iron" }, recipes);

            Assert.Contains("MyObjectBuilder_Component/SteelPlate", reachable);
        }

        [Fact]
        public void Resolve_LeavesAnOrphanRecipesOutputUnreachable()
        {
            // The Concrete Tool shape: a blueprint injected into a vanilla class whose input is a
            // player-only ore no node can ever supply.
            var recipes = new[]
            {
                Recipe("ConcreteOreToGravel", new[] { Item(Ore, "Concrete") }, new[] { Item(Ingt, "Stone") }),
            };

            HashSet<string> reachable = RecipeReachability.Resolve(
                new[] { "MyObjectBuilder_Ore/Iron" }, recipes);

            Assert.DoesNotContain("MyObjectBuilder_Ore/Concrete", reachable);
            Assert.DoesNotContain("MyObjectBuilder_Ingot/Stone", reachable);
        }

        [Fact]
        public void Resolve_ARecipeIsBlockedByAnyUnobtainableInput_NotOnlyTheFirst()
        {
            var recipes = new[]
            {
                Recipe("NeedsBoth",
                       new[] { Item(Ore, "Iron"), Item(Ore, "Concrete") },
                       new[] { Item(Comp, "Impossible") }),
            };

            HashSet<string> reachable = RecipeReachability.Resolve(
                new[] { "MyObjectBuilder_Ore/Iron" }, recipes);

            Assert.DoesNotContain("MyObjectBuilder_Component/Impossible", reachable);
        }

        [Fact]
        public void Resolve_ARecipeWithNoInputs_IsAlwaysAdmitted()
        {
            var recipes = new[] { Recipe("FromNothing", new RecipeItem[0], new[] { Item(Comp, "Free") }) };

            Assert.Contains("MyObjectBuilder_Component/Free",
                            RecipeReachability.Resolve(new string[0], recipes));
        }

        [Fact]
        public void Resolve_ACycleTerminatesAndStaysOutsideTheClosure()
        {
            // A and B each need the other. Neither is minted, so the fixed point must exclude both
            // rather than spin.
            var recipes = new[]
            {
                Recipe("AfromB", new[] { Item(Comp, "B") }, new[] { Item(Comp, "A") }),
                Recipe("BfromA", new[] { Item(Comp, "A") }, new[] { Item(Comp, "B") }),
            };

            HashSet<string> reachable = RecipeReachability.Resolve(
                new[] { "MyObjectBuilder_Ore/Iron" }, recipes);

            Assert.DoesNotContain("MyObjectBuilder_Component/A", reachable);
            Assert.DoesNotContain("MyObjectBuilder_Component/B", reachable);
        }

        [Fact]
        public void InputsAllReachable_TreatsAMalformedInputAsNonBlocking()
        {
            // An input missing a half cannot be matched against any rule, so it must not be the
            // reason a recipe is dropped.
            ConversionRecipe recipe = Recipe("Malformed", new[] { Item(Ore, null) }, new RecipeItem[0]);

            Assert.True(RecipeReachability.InputsAllReachable(
                recipe, new HashSet<string> { "MyObjectBuilder_Ore/Iron" }));
        }
    }
}
