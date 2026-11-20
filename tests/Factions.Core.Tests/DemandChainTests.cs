using System.Collections.Generic;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Sizing a commitment's real material requirement, netted at every level.
    ///
    /// <para>FAC-205 names the characteristic failure explicitly: a demand that <em>looks</em> better
    /// sized while still not matching any real requirement. So these assert exact quantities derived
    /// by hand from the BOM, never that a number got smaller — and the paired failure it warns about,
    /// a chain that nets only at the root and then demands ore for ingots the node already holds, has
    /// its own test below.</para>
    /// </summary>
    public sealed class DemandChainTests
    {
        private static MachineModel[] Models()
        {
            var models = new MachineModel[7];
            for (int i = 0; i < models.Length; i++)
                models[i] = new MachineModel { Speed = 1.0, OutputScale = 1.0, InputDivisor = 1.0, MachineCount = 6 };
            models[(int)NodeCapabilityKind.RefineryII]    = Model(1.00, 1.0);
            models[(int)NodeCapabilityKind.Refinery]      = Model(0.70, 1.0);
            models[(int)NodeCapabilityKind.ManufactoryII] = Model(1.00, 3.0);
            return models;
        }

        private static MachineModel Model(double outputScale, double inputDivisor)
        {
            return new MachineModel
            {
                Speed = 1.0, OutputScale = outputScale, InputDivisor = inputDivisor, MachineCount = 6,
            };
        }

        private static RecipeItem Item(string typeId, string subType, double amount)
        {
            return new RecipeItem { TypeId = typeId, SubTypeId = subType, Amount = amount };
        }

        // The live catalogue's own numbers: 30 Ingot:Iron per Thrust (÷3 at the assembler),
        // 1 Ore:Iron to 0.7 Ingot:Iron.
        private static RecipeExpansion LiveIronChain(params NodeCapabilityKind[] caps)
        {
            var thrust = new ConversionRecipe
            {
                BlueprintId = "ThrustComponent", TimeSecs = 4f,
                Inputs  = new[] { Item("MyObjectBuilder_Ingot", "Iron", 30.0) },
                Outputs = new[] { Item("MyObjectBuilder_Component", "Thrust", 1.0) },
            };
            var refine = new ConversionRecipe
            {
                BlueprintId = "IronOreToIngot", TimeSecs = 0.05f,
                Inputs  = new[] { Item("MyObjectBuilder_Ore", "Iron", 1.0) },
                Outputs = new[] { Item("MyObjectBuilder_Ingot", "Iron", 0.7) },
            };

            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.ManufactoryII, new List<ConversionRecipe> { thrust } },
                { NodeCapabilityKind.RefineryII,    new List<ConversionRecipe> { refine } },
                { NodeCapabilityKind.Refinery,      new List<ConversionRecipe> { refine } },
            };
            return RecipeExpansion.Build(capRecipes,
                caps.Length > 0 ? caps : new[] { NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.RefineryII },
                Models());
        }

        // Roots default to ShipBuild: every root ProductionManager supplies today is an outstanding
        // ship BOM, and the class tests below say so explicitly where it is the point.
        private static Dictionary<string, DemandChain.Requirement> Roots(params object[] pairs)
        {
            var roots = new Dictionary<string, DemandChain.Requirement>();
            for (int i = 0; i < pairs.Length; i += 2)
                roots[(string)pairs[i]] = Root((long)(int)pairs[i + 1], DemandClass.ShipBuild);
            return roots;
        }

        private static DemandChain.Requirement Root(long quantity, DemandClass demandClass)
        {
            return new DemandChain.Requirement { Quantity = quantity, Class = demandClass };
        }

        private static System.Func<string, long> Stock(params object[] pairs)
        {
            var held = new Dictionary<string, long>();
            for (int i = 0; i < pairs.Length; i += 2) held[(string)pairs[i]] = (long)(int)pairs[i + 1];
            return key => { long v; return held.TryGetValue(key, out v) ? v : 0; };
        }

        [Fact]
        public void BlockedHull_SizesOreAtTheChainRequirement_NotTheBanksAppetite()
        {
            // FAC-205's own case: 31 Comp:Thrust outstanding. 31 x 30 ÷ 3 = 310 Ingot:Iron,
            // 310 ÷ 0.7 = 443 Ore:Iron. The row it replaces read 464,074.
            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                Roots("Comp:Thrust", 31), LiveIronChain(), Stock());

            Assert.Equal(31, required["Comp:Thrust"].Quantity);
            Assert.Equal(310, required["Ingot:Iron"].Quantity);
            Assert.Equal(443, required["Ore:Iron"].Quantity);
        }

        [Fact]
        public void IngotsAlreadyHeld_StopTheChainThere_AndNoOreIsAskedFor()
        {
            // The frontier rising. This is the paired failure FAC-205 warns about: a chain that nets
            // only at the root demands ore to make ingots it is already holding, and reads as a
            // success on the leaf number alone.
            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                Roots("Comp:Thrust", 31), LiveIronChain(), Stock("Ingot:Iron", 310));

            // Gross, not the shortfall: the node holds all 310 and the row still says 310, because
            // MatchVectors and DemandUrgency both net haveAtNode themselves.
            Assert.Equal(310, required["Ingot:Iron"].Quantity);
            // Nothing descends past a covered level, so no ore is asked for at all.
            Assert.False(required.ContainsKey("Ore:Iron"));
        }

        [Fact]
        public void PartiallyHeldIngots_PassOnlyTheShortfall_NeverTheGrossWant()
        {
            // 310 wanted, 110 held: 200 to make, 286 ore. Netting the gross want instead would ask
            // for 443 and haul ore for ingots already on the shelf.
            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                Roots("Comp:Thrust", 31), LiveIronChain(), Stock("Ingot:Iron", 110));

            Assert.Equal(310, required["Ingot:Iron"].Quantity);   // gross
            Assert.Equal(286, required["Ore:Iron"].Quantity);     // ceil(shortfall 200 / 0.7)
        }

        [Fact]
        public void ComponentsAlreadyHeld_NetAtTheRoot_AndNothingDescends()
        {
            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                Roots("Comp:Thrust", 31), LiveIronChain(), Stock("Comp:Thrust", 31));

            Assert.Equal(31, required["Comp:Thrust"].Quantity);   // gross
            Assert.False(required.ContainsKey("Ingot:Iron"));
            Assert.False(required.ContainsKey("Ore:Iron"));
        }

        [Fact]
        public void TwoRootsWantingOneKey_NetTheStockOnce_NotOncePerChain()
        {
            // Open question 2 on the design page: the structure across roots is a DAG, and netting
            // per chain is where double-counting enters. 31 + 9 = 40 Thrust is 400 ingots; 100 held
            // must be subtracted once, leaving 300, not subtracted from each root's share.
            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                new Dictionary<string, DemandChain.Requirement> { { "Comp:Thrust", Root(40, DemandClass.ShipBuild) } }, LiveIronChain(), Stock("Ingot:Iron", 100));

            Assert.Equal(400, required["Ingot:Iron"].Quantity);   // gross
            Assert.Equal(429, required["Ore:Iron"].Quantity);     // ceil(shortfall 300 / 0.7)
        }

        [Fact]
        public void KeyWantedBothAsCommitmentAndAsInput_SumsBeforeNetting()
        {
            // A BOM that lists both the component and the ingot. The ingot level must settle once,
            // against the sum, or the second contribution nets the same stock again.
            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                new Dictionary<string, DemandChain.Requirement> { { "Comp:Thrust", Root(31, DemandClass.ShipBuild) }, { "Ingot:Iron", Root(90, DemandClass.ShipBuild) } },
                LiveIronChain(), Stock("Ingot:Iron", 100));

            Assert.Equal(310 + 90, required["Ingot:Iron"].Quantity);   // gross
            Assert.Equal(429, required["Ore:Iron"].Quantity);          // ceil(shortfall 300 / 0.7)
        }

        [Fact]
        public void FactionWithoutTheRefinery_StopsAtTheIngot_AndAsksForNoOre()
        {
            // Its frontier is Ingot:Iron and no Mine run closes it -- only a Haul or an install
            // does. Asking for ore it cannot refine would be the chain inventing work.
            RecipeExpansion noRefinery = LiveIronChain(NodeCapabilityKind.ManufactoryII);

            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                Roots("Comp:Thrust", 31), noRefinery, Stock());

            Assert.Equal(310, required["Ingot:Iron"].Quantity);
            Assert.False(required.ContainsKey("Ore:Iron"));
        }

        [Fact]
        public void TierOneRefinery_AsksForMoreOre_ForTheSameCommitment()
        {
            // MaterialEfficiency 0.70: 310 ingots off a tier-I refinery costs 633 ore, not 443. A
            // shared table would have undersized this faction by 43%.
            RecipeExpansion tierOne = LiveIronChain(
                NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.Refinery);

            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                Roots("Comp:Thrust", 31), tierOne, Stock());

            Assert.Equal(310, required["Ingot:Iron"].Quantity);
            Assert.Equal(633, required["Ore:Iron"].Quantity);   // ceil(310 / 0.49)
        }

        [Fact]
        public void NoCommitments_ProduceAnEmptyMap_SoNothingIsCapped()
        {
            // The "never invents demand" contract, from the caller's side: an empty map is how
            // ProductionManager knows to leave a faction's sizing exactly as it was.
            Assert.Empty(DemandChain.Requirements(Roots(), LiveIronChain(), Stock()));
            Assert.Empty(DemandChain.Requirements(null, LiveIronChain(), Stock()));
            Assert.Empty(DemandChain.Requirements(Roots("Comp:Thrust", 31), null, Stock()));
        }

        [Fact]
        public void ACoveredRoot_StillReportsItsGrossWant_SoNettingHappensExactlyOnce()
        {
            // The defect this replaced: reporting the shortfall made a covered row read 0, and
            // DemandUrgency.Starvation returns 0 the moment haveAtNode >= quantity — so the row
            // scored zero and both the dispatch ordering and the Haul-vs-Mine choice treated it as
            // "no claim" rather than "nearly met". Gross here, netted once downstream.
            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                Roots("Comp:Thrust", 31), LiveIronChain(), Stock("Comp:Thrust", 100));

            Assert.Equal(31, required["Comp:Thrust"].Quantity);
            Assert.False(required.ContainsKey("Ore:Cobalt"));
        }

        [Fact]
        public void ARootThatIsItselfALeaf_IsSizedAndNetted_WithNothingBelowIt()
        {
            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                Roots("Ore:Iron", 500), LiveIronChain(), Stock("Ore:Iron", 120));

            Assert.Equal(500, required["Ore:Iron"].Quantity);   // gross; the 120 held is netted downstream
            Assert.Single(required);
        }

        // ---- slice 3: the class travels with the chain -----------------------------------------

        [Fact]
        public void EveryLevelInheritsTheRootsClass_UndilutedByDepth()
        {
            // The inversion FAC-205 opens with: the ore a blocked hull is waiting on was classed
            // RecipeInput at weight 2.0, half the ShipBuild it feeds and level with every background
            // input in the network -- so OperationsManager entered the Haul/Mine pass with the
            // causality backwards and lost the only drill to a cargo errand.
            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                Roots("Comp:Thrust", 31), LiveIronChain(), Stock());

            Assert.Equal(DemandClass.ShipBuild, required["Comp:Thrust"].Class);
            Assert.Equal(DemandClass.ShipBuild, required["Ingot:Iron"].Class);
            Assert.Equal(DemandClass.ShipBuild, required["Ore:Iron"].Class);
        }

        [Fact]
        public void DepthDoesNotAttenuateTheClass_TheDeepestLevelRanksWithTheRoot()
        {
            // Stated as a "not this" on the design page. Attenuating by depth is tempting and re-
            // buries the root, which IS the defect; the leaf here is two hops down and must still
            // weigh what the commitment weighs.
            Dictionary<string, DemandChain.Requirement> required = DemandChain.Requirements(
                Roots("Comp:Thrust", 31), LiveIronChain(), Stock());

            Assert.Equal(required["Comp:Thrust"].Class, required["Ore:Iron"].Class);
            Assert.Equal(2, LiveIronChain().Height("Comp:Thrust"));
            Assert.Equal(0, LiveIronChain().Height("Ore:Iron"));
        }

        [Fact]
        public void KeySharedByTwoCommitments_TakesTheStrongerClass_NotTheLastOneToTouchIt()
        {
            // A key feeding both a stalled hull and a capability install is as expensive to leave
            // unmet as the hull.
            //
            // The shape here is deliberate and a weaker one does not test anything: the STRONG root
            // sits on the LOW key and the weak one above it. With the strong root high instead, the
            // descent re-stamps the low key with the strong class on its way down, so a plain
            // last-write-wins produces the same answer as a max and the assertion passes either way.
            // Found by mutating the max to an assignment and watching the first version of this test
            // stay green.
            var roots = new Dictionary<string, DemandChain.Requirement>
            {
                { "Ingot:Iron",  Root(100, DemandClass.ShipBuild) },
                { "Comp:Thrust", Root(31,  DemandClass.Install) },
            };

            Dictionary<string, DemandChain.Requirement> required =
                DemandChain.Requirements(roots, LiveIronChain(), Stock());

            Assert.Equal(DemandClass.ShipBuild, required["Ingot:Iron"].Class);
            Assert.Equal(DemandClass.ShipBuild, required["Ore:Iron"].Class);
            Assert.Equal(DemandClass.Install,   required["Comp:Thrust"].Class);
            Assert.Equal(310 + 100, required["Ingot:Iron"].Quantity);
        }

        [Fact]
        public void StrongerClassWinsWhicheverOrderTheRootsArriveIn()
        {
            // The same pair enumerated both ways. A max is order-independent; anything that depends
            // on which root was seen first fails one of these two.
            var strongLast = new Dictionary<string, DemandChain.Requirement>
            {
                { "Comp:Thrust", Root(31,  DemandClass.Install) },
                { "Ingot:Iron",  Root(100, DemandClass.ShipBuild) },
            };
            var strongFirst = new Dictionary<string, DemandChain.Requirement>
            {
                { "Ingot:Iron",  Root(100, DemandClass.ShipBuild) },
                { "Comp:Thrust", Root(31,  DemandClass.Install) },
            };

            Assert.Equal(DemandClass.ShipBuild,
                DemandChain.Requirements(strongLast,  LiveIronChain(), Stock())["Ingot:Iron"].Class);
            Assert.Equal(DemandClass.ShipBuild,
                DemandChain.Requirements(strongFirst, LiveIronChain(), Stock())["Ingot:Iron"].Class);
        }

        [Fact]
        public void AWeakerRootAloneKeepsItsOwnClass_TheChainDoesNotPromoteAnything()
        {
            // The chain inherits; it never invents a stronger reason than the commitment gave it.
            var roots = new Dictionary<string, DemandChain.Requirement>
            {
                { "Comp:Thrust", Root(31, DemandClass.Install) },
            };

            Dictionary<string, DemandChain.Requirement> required =
                DemandChain.Requirements(roots, LiveIronChain(), Stock());

            Assert.Equal(DemandClass.Install, required["Comp:Thrust"].Class);
            Assert.Equal(DemandClass.Install, required["Ore:Iron"].Class);
        }
    }
}
