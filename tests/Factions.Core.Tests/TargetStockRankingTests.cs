using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The target-stock ranking: what a node should hold of one key, scored from what flows past it
    /// and what stands on the key above.
    ///
    /// <para>The two cases the shape has to sort are the operator's own, and neither is encoded —
    /// both fall out of the recipe graph. Concrete is terminal, so nothing is made from it and its
    /// reach is 0. Iron ingot feeds many things in quantity, so its reach is high. A ranking that
    /// needs a special case for either is the wrong shape.</para>
    ///
    /// <para><see cref="PriceOracleTests"/> runs without a recipe registry, so every reach there is
    /// 0 and the weight is exactly 1. This fixture carries the graph.</para>
    /// </summary>
    public class TargetStockRankingTests
    {
        // A three-level catalogue of the live shape: ore -> ingot -> components -> a ship part, plus
        // the stone leg, which is one step deep and ends in something nothing consumes.
        private sealed class FakeRecipes : IRecipeRegistry
        {
            private readonly List<ConversionRecipe> _all = new List<ConversionRecipe>();

            public FakeRecipes Add(string id, RecipeItem[] inputs, RecipeItem[] outputs)
            {
                _all.Add(new ConversionRecipe { BlueprintId = id, Inputs = inputs, Outputs = outputs, TimeSecs = 1f });
                return this;
            }

            public IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId)
            {
                foreach (ConversionRecipe r in _all)
                    foreach (RecipeItem o in r.Outputs)
                        if (o.TypeId == outputTypeId && o.SubTypeId == outputSubTypeId) { yield return r; break; }
            }

            public IEnumerable<ConversionRecipe> AllRecipes { get { return _all; } }
            public int Count { get { return _all.Count; } }
        }

        private static RecipeItem Ore(string sub, double amount)
        {
            return new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = sub, Amount = amount };
        }

        private static RecipeItem Ingot(string sub, double amount)
        {
            return new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = sub, Amount = amount };
        }

        private static RecipeItem Comp(string sub, double amount)
        {
            return new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = sub, Amount = amount };
        }

        private static FakeRecipes Catalogue()
        {
            return new FakeRecipes()
                // The iron leg: deep and broad.
                .Add("IronOreToIngot",  new[] { Ore("Iron", 1) },        new[] { Ingot("Iron", 0.7) })
                .Add("SteelPlate",      new[] { Ingot("Iron", 21) },     new[] { Comp("SteelPlate", 1) })
                .Add("MetalGrid",       new[] { Ingot("Iron", 12) },     new[] { Comp("MetalGrid", 1) })
                .Add("InteriorPlate",   new[] { Ingot("Iron", 3) },      new[] { Comp("InteriorPlate", 1) })
                .Add("Girder",          new[] { Ingot("Iron", 6) },      new[] { Comp("Girder", 1) })
                .Add("Thrust",          new[] { Comp("SteelPlate", 30), Comp("MetalGrid", 4) },
                                        new[] { Comp("Thrust", 1) })
                // The stone leg: one step, ending in something nothing is made from.
                .Add("StoneOreToIngot", new[] { Ore("Stone", 1000) },    new[] { Ingot("Stone", 14), Ingot("Iron", 30) })
                .Add("Concrete",        new[] { Ingot("Stone", 10) },    new[] { Comp("Concrete", 1) });
        }

        private static (WorldModel world, PriceOracle oracle, FactionTuning tuning) Setup()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TestSupport.World();
            world.Tuning = tuning;
            world.Registry = NullItemRegistry.Instance;
            world.Catalogue = new TuningItemCatalogue(tuning);
            world.PriceCatalogue = null;
            world.Recipes = Catalogue();
            return (world, new PriceOracle(world, tuning), tuning);
        }

        private static SpatialNode Node(WorldModel world, string id)
        {
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
            n.OwnerFactionTag = "F";
            return n;
        }

        // ---- the index itself --------------------------------------------------------------------

        [Fact]
        public void ATerminalProductHasNoReach()
        {
            ItemDownstreamIndex index = ItemDownstreamIndex.Build(Catalogue());

            Assert.Equal(0, index.ConsumerCount("Comp:Concrete"));
            Assert.Equal(0.0, index.Reach("Comp:Concrete"));
        }

        [Fact]
        public void IronIngotOutranksStoneIngot_WithNothingEncodedAboutEither()
        {
            // The operator's two worked cases, decided by the graph alone. Iron ingot feeds four
            // components; stone ingot feeds one thing, and nothing is made from that.
            ItemDownstreamIndex index = ItemDownstreamIndex.Build(Catalogue());

            Assert.True(index.ConsumerCount("Ingot:Iron") > index.ConsumerCount("Ingot:Stone"));
            Assert.True(index.StepsAbove("Ingot:Iron") > index.StepsAbove("Ingot:Stone"),
                "iron ingot -> component -> thruster is two steps; stone ingot -> concrete is one");
            Assert.True(index.Reach("Ingot:Iron") > index.Reach("Ingot:Stone"),
                "reach " + index.Reach("Ingot:Iron") + " vs " + index.Reach("Ingot:Stone"));
        }

        [Fact]
        public void QuantityCounts_NotJustBreadth()
        {
            // "and in what quantity" -- two keys with the same consumer count are separated by how
            // much of them each batch takes.
            var sparing = new FakeRecipes()
                .Add("A", new[] { Ingot("Cobalt", 1) },  new[] { Comp("A", 1) })
                .Add("B", new[] { Ingot("Nickel", 50) }, new[] { Comp("B", 1) });
            ItemDownstreamIndex index = ItemDownstreamIndex.Build(sparing);

            Assert.Equal(index.ConsumerCount("Ingot:Cobalt"), index.ConsumerCount("Ingot:Nickel"));
            Assert.True(index.Reach("Ingot:Nickel") > index.Reach("Ingot:Cobalt"));
        }

        [Fact]
        public void ACycleInTheGraphTerminates()
        {
            // The catalogue is not guaranteed acyclic and a modded one can close a loop.
            var looped = new FakeRecipes()
                .Add("AtoB", new[] { Ingot("A", 1) }, new[] { Ingot("B", 1) })
                .Add("BtoA", new[] { Ingot("B", 1) }, new[] { Ingot("A", 1) });
            ItemDownstreamIndex index = ItemDownstreamIndex.Build(looped);

            Assert.True(index.StepsAbove("Ingot:A") >= 1);
            Assert.True(index.Reach("Ingot:A") > 0);
        }

        [Fact]
        public void AnEmptyRegistryLeavesEveryReachNeutral()
        {
            ItemDownstreamIndex index = ItemDownstreamIndex.Build(NullRecipeRegistry.Instance);

            Assert.Equal(0.0, index.Reach("Ingot:Iron"));
            Assert.Equal(0, index.ConsumerCount("Ingot:Iron"));
        }

        // ---- the target ----------------------------------------------------------------------------

        [Fact]
        public void TargetIsObservedConsumptionOverTheCoverageHorizon()
        {
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            node.DemandHistory["Ore:Magnesium"] = 500;   // no reach: nothing is made from it here

            long target;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Ore:Magnesium", out target));
            Assert.Equal((long)(500 * tuning.TargetStockCoveragePeriods), target);
        }

        [Fact]
        public void TheSameFlowEarnsABiggerTargetForAWidelyUsedKey()
        {
            // The whole point of the downstream term: equal flow, unequal usefulness.
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            node.DemandHistory["Ingot:Iron"]  = 1000;
            node.DemandHistory["Ingot:Stone"] = 1000;

            long iron, stone;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Ingot:Iron", out iron));
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Ingot:Stone", out stone));

            Assert.True(iron > stone, "iron " + iron + " must outrank stone " + stone + " at equal flow");
        }

        [Fact]
        public void AKeyNothingConsumes_TargetsNothing_AndReadsNeutral()
        {
            // The defect this replaces: an unwanted key at zero stock used to read maximum scarcity,
            // because scarcity measured absence. It must now read NEUTRAL -- not scarce, which would
            // order more of it, and not the floor, which would tell every consumer to dump it.
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");

            long target;
            Assert.False(oracle.TryGetReferenceStock(node.Id, "Ore:Stone", out target));
            Assert.Equal(0, target);
            Assert.Equal(1.0, oracle.ScarcityMultiplier(node.Id, "Ore:Stone"), 6);
            Assert.NotEqual(tuning.ScarcityMax, oracle.ScarcityMultiplier(node.Id, "Ore:Stone"));
        }

        [Fact]
        public void AStoneGlutFallsBelowThePullGate_SoTheRecipeStopsGettingSlots()
        {
            // The loop this exists to unwind, at the point it unwinds from. Gravel is made far
            // faster than it is used, so its flow term is small against its stock; the pull gate
            // admits a recipe only above 1.0, so the stone recipe stops earning slots -- and stone
            // ore stops being consumed one hop later.
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            node.DemandHistory["Ingot:Stone"] = 100;      // a trickle of real consumption
            node.Inventory["Ingot:Stone"] = 1_600_000;    // what a live SPRT node was holding

            Assert.True(oracle.ScarcityMultiplier(node.Id, "Ingot:Stone") <= 1.0,
                "a node sitting on 1.6M gravel it barely uses must not still be asking for more");
        }

        [Fact]
        public void AnIronShortfallStaysAboveThePullGate()
        {
            // The discriminator for the test above: the same rule, on the key that should keep its
            // slots. Without this, "everything stopped" would read as a pass.
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            node.DemandHistory["Ingot:Iron"] = 100;
            node.Inventory["Ingot:Iron"] = 0;

            Assert.True(oracle.ScarcityMultiplier(node.Id, "Ingot:Iron") > 1.0,
                "iron is consumed here and the node holds none; it must still want it");
        }

        [Fact]
        public void AStrayBatchDoesNotMintAStandingTarget()
        {
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            node.DemandHistory["Ingot:Iron"] = tuning.TargetStockFlowFloorUnits / 2.0;

            long target;
            Assert.False(oracle.TryGetReferenceStock(node.Id, "Ingot:Iron", out target));
        }

        [Fact]
        public void FlowIsReadBackInUnits_NotInTheLitresItWasCreditedIn()
        {
            // ProductionManager weights the DemandHistory credit by litres per unit where a volume is
            // known. Read back undivided, a target would be denominated in litres and compared
            // against a unit stock -- which for a 3 L component is a 3x inflation.
            var (world, oracle, tuning) = Setup();
            world.ItemVolumes = new FakeVolumes().Set("Ingot:Iron", 3.0);
            SpatialNode node = Node(world, "N1");
            node.DemandHistory["Ingot:Iron"] = 3000;   // 1000 units at 3 L each

            long target;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Ingot:Iron", out target));

            world.ItemVolumes = null;
            var bare = new PriceOracle(world, tuning);
            long unweighted;
            Assert.True(bare.TryGetReferenceStock(node.Id, "Ingot:Iron", out unweighted));

            Assert.Equal(unweighted / 3, target);
        }

        [Fact]
        public void ABankBuyableGlutStillReadsAsAGlut_SoTheMoneyFaucetStaysOpen()
        {
            // The sale term's stand-in. The central bank buys only below CentralBankBuyScarcityFloor,
            // and its buyout is the money supply's only non-debt injection. A terminal product ranked
            // to a flat zero reads NEUTRAL, which is above that floor and closes the faucet.
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            node.Inventory["Comp:Thrust"] = 50_000;   // nothing is made from it, nothing wants it

            Assert.True(oracle.ScarcityMultiplier(node.Id, "Comp:Thrust")
                        <= tuning.CentralBankBuyScarcityFloor,
                "the bank has to be able to see this pile to buy it");
        }

        [Fact]
        public void TheSaleFloorCannotPushAnUnwantedKeyIntoSCARCITY()
        {
            // The same floor, at the other end. It exists to let a glut be seen; if it applied at any
            // stock level it would be the old inflation back under a new name.
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            node.Inventory["Comp:Thrust"] = 1;   // far below the floor, and still unwanted

            Assert.Equal(1.0, oracle.ScarcityMultiplier(node.Id, "Comp:Thrust"), 6);
        }

        // ---- the sale term ---------------------------------------------------------------------

        /// <summary>What <see cref="SpatialNode.SalesHistory"/> has to hold for the oracle to read
        /// back <paramref name="unitsPerPass"/>: it is a decaying accumulator, so a steady rate
        /// settles at <c>unitsPerPass / alpha</c> and the oracle multiplies by alpha on read.</summary>
        private static void Sold(SpatialNode node, FactionTuning tuning, string invKey, double unitsPerPass)
        {
            double alpha = 2.0 / (tuning.DemandHistoryEmaPeriods + 1.0);
            node.SalesHistory[invKey] = unitsPerPass / alpha;
        }

        [Fact]
        public void AKeyNothingConsumes_StillTargetsWhatItSells()
        {
            // The operator's concrete case: a terminal product nothing is made from, held only
            // because a paying buyer keeps taking it. It must not target zero, or the node stops
            // making the one thing its customers want.
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            Sold(node, tuning, "Comp:Thrust", 500);

            long target;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:Thrust", out target));
            Assert.Equal((long)(500 * tuning.TargetStockSaleCoveragePeriods), target);
        }

        /// <summary>Sales raise the target so the node restocks; they do not make the store hold the
        /// stock back from the customers they came from.</summary>
        [Fact]
        public void ATargetSetBySalesAlone_KeepsNothingBackFromTheStore()
        {
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            Sold(node, tuning, "Comp:Thrust", 500);

            long keepBack;
            Assert.False(oracle.TryGetKeepBackStock(node.Id, "Comp:Thrust", out keepBack));
            Assert.Equal(0L, keepBack);
            PriceOracle.TargetBreakdown b = oracle.ExplainTarget(node.Id, "Comp:Thrust");
            Assert.Equal("sold", b.Winner);
            Assert.Equal("none", b.KeepBackTerm);
        }

        [Fact]
        public void SalesAboveConsumption_KeepBackOnlyWhatIsConsumed()
        {
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            node.DemandHistory["Comp:Thrust"] = 50;
            Sold(node, tuning, "Comp:Thrust", 5000);

            PriceOracle.TargetBreakdown b = oracle.ExplainTarget(node.Id, "Comp:Thrust");
            Assert.Equal("sold", b.Winner);
            Assert.Equal(b.Sold, b.Target);
            Assert.Equal("flow", b.KeepBackTerm);
            Assert.Equal(b.Flow, b.KeepBack);
            Assert.True(b.KeepBack > 0 && b.KeepBack < b.Target);

            long keepBack;
            Assert.True(oracle.TryGetKeepBackStock(node.Id, "Comp:Thrust", out keepBack));
            Assert.Equal(b.Flow, keepBack);
        }

        [Fact]
        public void SellingRanksLowerThanBeingConsumedAtTheSameRate()
        {
            // "it should rank low and trend towards the floor" -- a shelf stocked for customers is a
            // smaller claim than one feeding a machine, at equal throughput.
            var (world, oracle, tuning) = Setup();
            SpatialNode seller = Node(world, "N1");
            SpatialNode user   = Node(world, "N2");
            Sold(seller, tuning, "Comp:Thrust", 500);
            user.DemandHistory["Comp:Thrust"] = 500;

            long sold, consumed;
            Assert.True(oracle.TryGetReferenceStock(seller.Id, "Comp:Thrust", out sold));
            Assert.True(oracle.TryGetReferenceStock(user.Id, "Comp:Thrust", out consumed));
            Assert.True(sold < consumed, "sold " + sold + " must rank below consumed " + consumed);
        }

        [Fact]
        public void SalesDecayWhenTheBuyingStops()
        {
            // The decay term, on the sale side, driven through OperationsManager.Tick rather than by
            // reflecting into the decay itself: removing the CALL reds nothing when the test invokes
            // the method directly, and the call is the thing that can go missing.
            WorldModel world = TestSupport.World();
            FactionTuning tuning = world.Tuning;
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.StableStationId = 1;
            Sold(node, tuning, "Comp:Thrust", 500);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            var states = new List<FactionState> { st };
            var log = new List<string>();

            double before = node.SalesHistory["Comp:Thrust"];
            Assert.True(before > 0);

            var ops = new OperationsManager();
            for (int pass = 0; pass < 400; pass++)
            {
                world.UtcNow = world.UtcNow.AddMinutes(1);
                ops.Tick(states, world, tuning, log);
            }

            Assert.False(node.SalesHistory.ContainsKey("Comp:Thrust"),
                "sales that stopped must decay out, not hold a target open forever");
        }

        [Fact]
        public void TheSaleTermIsNotFedByStock()
        {
            // The reason the central bank is excluded. Nothing about a node's own stock may reach
            // the sale term, or the curve closes on itself: more stock -> more bought -> higher
            // target -> more production -> more stock.
            var (world, oracle, tuning) = Setup();
            SpatialNode node = Node(world, "N1");
            Sold(node, tuning, "Comp:Thrust", 500);

            long before;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:Thrust", out before));

            node.Inventory["Comp:Thrust"] = 5_000_000;

            long after;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:Thrust", out after));
            Assert.Equal(before, after);
        }

        [Fact]
        public void TheCentralBankBuyout_CreditsNoSales()
        {
            // The operator's constraint, tested against the pass that actually buys rather than
            // against a hand-set stock: "attaching to the bank would cause an unstable curve
            // because the bank has no bounds for buying" (2026-09-20). The bank takes
            // CentralBankBuyFraction x stock from any glut, so if this ever starts crediting, sales
            // become a function of stock and the rank feeds itself.
            WorldModel world = TestSupport.World();
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.CentralBankBuyScarcityFloor = 1.0;

            SpatialNode glut = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            glut.OwnerFactionTag = "A";
            glut.StableStationId = 1;
            glut.Capabilities[NodeCapabilityKind.Refinery] = 1;
            glut.Inventory["SteelPlate"] = 500;
            world.SetTreasury("A", 250000);

            var states = new[] { new FactionState { Tag = "A", RngSeed = 1 } };
            long stockBefore = glut.Inventory["SteelPlate"];

            world.AccrueIncome(states);

            // The discriminator: the pass has to have actually bought, or "no sales credited" is a
            // vacuous pass on a buyout that never ran.
            Assert.Contains(world.SnapshotMonetaryEvents(), e => e.Type == "asset-purchase");
            Assert.True(glut.Inventory["SteelPlate"] < stockBefore,
                "the bank must have taken stock, or this test proves nothing");

            Assert.Empty(glut.SalesHistory);
        }

        [Fact]
        public void ASaleCreditsTheSellersNode_NotTheBuyers()
        {
            // "how much is being bought FROM us" -- it is the seller's shelf whose target rises.
            var (world, oracle, tuning) = Setup();
            SpatialNode seller = Node(world, "N1");
            SpatialNode buyer  = Node(world, "N2");

            world.RecordUnitsSold(seller.Id, "Comp:Thrust", 4000);

            Assert.True(seller.SalesHistory.ContainsKey("Comp:Thrust"));
            Assert.False(buyer.SalesHistory.ContainsKey("Comp:Thrust"));
        }

        [Fact]
        public void AFactionScalarSaleHasNoNodeToCredit_AndDoesNotThrow()
        {
            // A trade settling against the faction pool rather than a node carries a null origin.
            // Found by FleetConvoyTests, which resolves exactly that shape.
            var (world, oracle, tuning) = Setup();
            Node(world, "N1");

            world.RecordUnitsSold(null, "Comp:Thrust", 4000);
            world.RecordUnitsSold("N1", null, 4000);
            world.RecordUnitsSold("NoSuchNode", "Comp:Thrust", 4000);
        }

        private sealed class FakeVolumes : IItemVolumeCatalogue
        {
            private readonly Dictionary<string, double> _litres = new Dictionary<string, double>();
            public FakeVolumes Set(string key, double litres) { _litres[key] = litres; return this; }
            public double VolumeLitersPerUnit(string invKey)
            {
                double v;
                return !string.IsNullOrEmpty(invKey) && _litres.TryGetValue(invKey, out v) ? v : 0;
            }
        }
    }
}
