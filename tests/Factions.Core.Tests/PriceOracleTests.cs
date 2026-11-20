using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Target-stock behaviour: what a node should be holding of one key, and how scarcity
    /// reads against it.
    ///
    /// <para>The target is ranked from observed flow — <see cref="SpatialNode.DemandHistory"/>, the
    /// decaying record of what this node actually consumes — plus committed BOMs and a floor. A
    /// node's capability set no longer sizes anything, so these tests drive flow directly through
    /// <c>Flow</c> rather than installing hardware and deriving a figure from a table.</para>
    ///
    /// <para>No recipe registry is wired here, so every key's downstream reach is 0 and the weight
    /// is exactly 1.0 — a target is <c>units x TargetStockCoveragePeriods</c> and nothing else.
    /// <see cref="TargetStockRankingTests"/> carries the registry and the reach term.</para></summary>
    public class PriceOracleTests
    {
        private static (WorldModel world, PriceOracle oracle, FactionTuning tuning) Setup()
        {
            var tuning = TestSupport.Tuning();
            var world  = TestSupport.World();
            world.Tuning = tuning;
            world.Registry = NullItemRegistry.Instance;
            world.Catalogue = new TuningItemCatalogue(tuning);
            world.PriceCatalogue = null;
            var oracle = new PriceOracle(world, tuning);
            return (world, oracle, tuning);
        }

        private static SpatialNode AddNode(WorldModel world, PriceOracle oracle, string id)
        {
            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
            node.OwnerFactionTag = "F";
            oracle.InvalidateNode(node.Id);
            return node;
        }

        /// <summary>Give a node an observed consumption rate for one key. With no volume catalogue
        /// on the world (true of this fixture, and of any host that has not wired one) the credit
        /// ProductionManager writes is in raw units, so this is the same number the oracle reads
        /// back.</summary>
        private static void Flow(SpatialNode node, string invKey, double unitsPerPass)
        {
            node.DemandHistory[invKey] = unitsPerPass;
        }

        /// <summary>The target a given flow earns where downstream reach is 0.</summary>
        private static long ExpectedFlowTarget(FactionTuning tuning, double unitsPerPass)
        {
            return (long)(unitsPerPass * tuning.TargetStockCoveragePeriods);
        }

        [Fact]
        public void ProcessedGoods_HaveDistinctConcreteReferenceAndScarcity()
        {
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            Flow(node, "Comp:SteelPlate", 400);
            Flow(node, "Comp:Thrust", 400);

            long steelRef;
            long thrustRef;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:SteelPlate", out steelRef));
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:Thrust", out thrustRef));

            // Stocked either side of the node's own reference rather than at fixed counts: the
            // assertion is that stock drives scarcity per subtype, and a fixture pinned to magic
            // numbers stops exercising the unclamped range the moment the reference is recalibrated.
            node.Inventory["Comp:SteelPlate"] = steelRef * 2;
            node.Inventory["Comp:Thrust"] = thrustRef / 2;

            Assert.True(steelRef > 0);
            Assert.True(thrustRef > 0);

            double steelScarcity = oracle.ScarcityMultiplier(node.Id, "Comp:SteelPlate");
            double thrustScarcity = oracle.ScarcityMultiplier(node.Id, "Comp:Thrust");
            Assert.True(steelScarcity < thrustScarcity);
        }

        [Fact]
        public void ProcessedGoodGlut_DoesNotMaskOtherSubtypeScarcity()
        {
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            Flow(node, "Comp:SteelPlate", 400);
            Flow(node, "Comp:BulletproofGlass", 400);

            long reference;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:SteelPlate", out reference));
            node.Inventory["Comp:SteelPlate"] = reference * 4;   // deep glut, below the buy floor
            node.Inventory["Comp:BulletproofGlass"] = reference / 4;

            double steelScarcity = oracle.ScarcityMultiplier(node.Id, "Comp:SteelPlate");
            double glassScarcity = oracle.ScarcityMultiplier(node.Id, "Comp:BulletproofGlass");

            Assert.True(steelScarcity <= tuning.CentralBankBuyScarcityFloor);
            Assert.True(glassScarcity > steelScarcity);
        }

        // ---- concrete reference: value-based per subtype, not a pool split (regression) --------

        [Fact]
        public void ConcreteReference_IsIndependentOfOtherSubtypesStock()
        {
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            Flow(node, "Comp:SteelPlate", 400);

            long steelRefBefore;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:SteelPlate", out steelRefBefore));

            // Pile up a huge amount of a completely unrelated PG subtype at the same node. Under the
            // old pool-split this shrank SteelPlate's share; under the flow ranking it must not
            // register at all, because SteelPlate's own consumption is the only input.
            node.Inventory["Comp:Thrust"] = 500_000;

            long steelRefAfter;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:SteelPlate", out steelRefAfter));

            Assert.Equal(steelRefBefore, steelRefAfter);
        }

        private sealed class FakePriceCatalogue : IItemPriceCatalogue
        {
            private readonly System.Collections.Generic.Dictionary<string, long> _prices
                = new System.Collections.Generic.Dictionary<string, long>();

            public FakePriceCatalogue Set(string subTypeId, long price) { _prices[subTypeId] = price; return this; }

            public long BasePrice(string subTypeId)
            {
                long value;
                return !string.IsNullOrEmpty(subTypeId) && _prices.TryGetValue(subTypeId, out value) ? value : 0;
            }
        }

        // ---- demand-aware reference ------------------------------------------------------------

        private static readonly System.DateTime Soon =
            new System.DateTime(2999, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);

        [Fact]
        public void Reference_RisesToAPendingInstallNeed()
        {
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");

            long before;
            Assert.False(oracle.TryGetReferenceStock(node.Id, "Comp:SteelPlate", out before),
                "nothing consumes, wants or owes steel here, so the node should target none of it");

            // What a RefineryII's LargeRefinery BOM actually asks for, live.
            world.Demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Soon, 1200));

            long after;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:SteelPlate", out after));
            Assert.Equal(1200, after);
            Assert.True(after > before, "a pending capital commitment must outrank the throughput buffer");
        }

        [Fact]
        public void Reference_KeepsTheFlowBufferWhenItIsLargerThanTheNeed()
        {
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            Flow(node, "Comp:SteelPlate", 400);

            long flowTarget;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:SteelPlate", out flowTarget));
            Assert.Equal(ExpectedFlowTarget(tuning, 400), flowTarget);

            world.Demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Soon, 1));

            long withTinyNeed;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:SteelPlate", out withTinyNeed));
            Assert.Equal(flowTarget, withTinyNeed);
        }

        [Fact]
        public void NodePinnedNeed_AppliesAtThatNodeOnly()
        {
            var (world, oracle, tuning) = Setup();
            var shipyard = AddNode(world, oracle, "F1");
            var sibling  = AddNode(world, oracle, "F2");

            world.Demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", shipyard.Id, Soon, 900));

            long atShipyard;
            long atSibling;
            Assert.True(oracle.TryGetReferenceStock(shipyard.Id, "Comp:SteelPlate", out atShipyard));
            Assert.Equal(900, atShipyard);

            // The sibling owes nothing, consumes nothing and holds nothing, so it targets nothing --
            // sharper than the old flat floor, which gave every node in the faction an opinion about
            // every component in the game.
            Assert.False(oracle.TryGetReferenceStock(sibling.Id, "Comp:SteelPlate", out atSibling));
            Assert.Equal(0, atSibling);
        }

        [Fact]
        public void FactionWideNeed_IsSharedAcrossCapabilityBearingNodesOnly()
        {
            var (world, oracle, tuning) = Setup();
            var industrialA = AddNode(world, oracle, "F1");
            var industrialB = AddNode(world, oracle, "F2");
            var outpost     = AddNode(world, oracle, "F3");   // no capabilities: not a gathering place
            industrialA.Capabilities[NodeCapabilityKind.Refinery] = 1;
            industrialB.Capabilities[NodeCapabilityKind.Refinery] = 1;
            oracle.InvalidateNode(industrialA.Id);
            oracle.InvalidateNode(industrialB.Id);
            oracle.InvalidateNode(outpost.Id);

            world.Demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Soon, 1200));

            long a;
            long b;
            Assert.True(oracle.TryGetReferenceStock(industrialA.Id, "Comp:SteelPlate", out a));
            Assert.True(oracle.TryGetReferenceStock(industrialB.Id, "Comp:SteelPlate", out b));

            // Two holders, so each targets half -- the faction as a whole aims at the need, not at a
            // multiple of it. Replicating 1200 at every node is what makes 49-node FCTM target 58,800.
            Assert.Equal(600, a);
            Assert.Equal(600, b);
        }

        [Fact]
        public void FactionWideNeed_IsNotDividedAwayWhenTheFactionHasNoIndustry()
        {
            var (world, oracle, tuning) = Setup();
            var outpost = AddNode(world, oracle, "F1");   // owned, but nothing installed
            oracle.InvalidateNode(outpost.Id);

            world.Demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Soon, 1200));

            long reference;
            Assert.True(oracle.TryGetReferenceStock(outpost.Id, "Comp:SteelPlate", out reference));
            Assert.Equal(1200, reference);
        }

        [Fact]
        public void AnotherFactionsNeed_DoesNotMoveThisNodesReference()
        {
            var (world, oracle, tuning) = Setup();
            var mine = AddNode(world, oracle, "F1");

            world.Demand.Post(DemandEntry.ForNeed("G", "Comp:SteelPlate", null, Soon, 5000));

            long reference;
            Assert.False(oracle.TryGetReferenceStock(mine.Id, "Comp:SteelPlate", out reference));
            Assert.Equal(0, reference);
        }

        [Fact]
        public void APendingInstallNeedStopsTheNodeReadingAsAGlut()
        {
            // The live symptom, end to end: FCTM held 245 Comp:SteelPlate and wanted 1055 for a
            // RefineryII, and the bank bought 100 of them at half book because scarcity read 0.5.
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            node.Inventory["Comp:SteelPlate"] = 245;

            Assert.Equal(tuning.ScarcityMin, oracle.ScarcityMultiplier(node.Id, "Comp:SteelPlate"), 6);

            world.Demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Soon, 1055));

            double scarce = oracle.ScarcityMultiplier(node.Id, "Comp:SteelPlate");
            Assert.True(scarce > tuning.CentralBankBuyScarcityFloor,
                "245 held against a 1055 commitment is not a surplus the bank should be buying");
        }


        // ---- a producing node's own output ------------------------------------------------------

        // ---- an unwanted key targets nothing ----------------------------------------------------

        private static DemandEntry ItemWant(string tag, string invKey, string nodeId, System.DateTime expires)
        {
            return new DemandEntry { FactionTag = tag, Kind = "Item:" + invKey, NodeId = nodeId, Quantity = 10, ExpiresUtc = expires };
        }

        [Fact]
        public void AnUnwantedItemLosesItsReference()
        {
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            oracle.InvalidateNode(node.Id);

            long reference;
            Assert.False(oracle.TryGetReferenceStock(node.Id, "Ore:Stone", out reference));
            Assert.Equal(0, reference);
        }

        [Fact]
        public void ZeroReferenceReadsNEUTRAL_NotScarceAndNotWorthless()
        {
            // The load-bearing distinction. A zeroed reference makes TryGetReferenceStock false, and
            // ScarcityMultiplier answers 1.0 -- not ScarcityMax (which is what an unwanted item used
            // to read, because scarcity measures absence) and not ScarcityMin (which would tell every
            // consumer to dump it). 1.0 also fails ProductionManager's `scarcity > 1.0` pull gate.
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            oracle.InvalidateNode(node.Id);

            Assert.Equal(1.0, oracle.ScarcityMultiplier(node.Id, "Ore:Stone"), 6);
            Assert.NotEqual(tuning.ScarcityMax, oracle.ScarcityMultiplier(node.Id, "Ore:Stone"));
        }

        [Fact]
        public void AnItemWantAtTheNodeKeepsItsReference()
        {
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            oracle.InvalidateNode(node.Id);
            world.Demand.Post(ItemWant("F", "Ore:Iron", node.Id, Soon));

            long reference;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Ore:Iron", out reference));
            Assert.True(reference > 0);
        }

        [Fact]
        public void AFactionWideItemWantAlsoKeepsIt()
        {
            // Which of a faction's stations a delivery lands at is logistics, so a faction-scalar
            // want is a reason for any of its nodes to hold value -- the same rule StationBuyDemand
            // applies on the buy side.
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            oracle.InvalidateNode(node.Id);
            world.Demand.Post(ItemWant("F", "Ore:Iron", null, Soon));

            long reference;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Ore:Iron", out reference));
            Assert.True(reference > 0);
        }

        [Fact]
        public void ACommittedBomKeepsItEvenWithNoItemWant()
        {
            // A "Need:" commitment is a different register scope from "Item:" and must not be gated
            // away -- it is the strongest possible statement that this node owes the material.
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            oracle.InvalidateNode(node.Id);
            world.Demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", node.Id, Soon, 900));

            long reference;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Comp:SteelPlate", out reference));
            Assert.Equal(900, reference);
        }

        // ---- the scarcity curve and where a row sits on it (FAC-161) ------------------------------

        [Fact]
        public void Curve_ShortSideIsLinear_ReachingTheCeilingOnlyAtZero()
        {
            Assert.Equal(3.0, ScarcityCurve.At(1000, 0, 0.5, 3.0), 9);
            Assert.Equal(2.0, ScarcityCurve.At(1000, 500, 0.5, 3.0), 9);
            // The hyperbola it replaces read 3.0 here, a third of the reference.
            Assert.Equal(1.0 + 2.0 * 2.0 / 3.0, ScarcityCurve.At(999, 333, 0.5, 3.0), 9);
            Assert.Equal(1.0, ScarcityCurve.At(1000, 1000, 0.5, 3.0), 9);
        }

        [Fact]
        public void Curve_GlutSideIsUnchanged()
        {
            Assert.Equal(0.8, ScarcityCurve.At(1000, 1250, 0.5, 3.0), 9);
            Assert.Equal(0.5, ScarcityCurve.At(1000, 2000, 0.5, 3.0), 9);
            Assert.Equal(0.5, ScarcityCurve.At(1000, 9000, 0.5, 3.0), 9);
            Assert.Equal(1.0, ScarcityCurve.At(0, 0, 0.5, 3.0), 9);
        }

        [Fact]
        public void Curve_NeverRisesWithStock()
        {
            double previous = double.PositiveInfinity;
            for (long stock = 0; stock <= 3000; stock += 7)
            {
                double v = ScarcityCurve.At(1000, stock, 0.5, 3.0);
                Assert.True(v <= previous, "stock " + stock + ": " + v + " above " + previous);
                previous = v;
            }
        }

        [Fact]
        public void TheMultiplierHasNoTimeTerm()
        {
            // A price that moves with a clock lets a player drain a node, wait, and sell back for more.
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            node.DemandHistory["Ore:Iron"] = 5.0;          // a reference to be short of

            double now = oracle.ScarcityMultiplier(node.Id, "Ore:Iron");
            world.UtcNow = world.UtcNow.AddHours(6);
            Assert.Equal(now, oracle.ScarcityMultiplier(node.Id, "Ore:Iron"), 9);
            Assert.Equal(tuning.ScarcityMax, now, 6);       // zero stock is the top of the curve
        }

        [Fact]
        public void AnOfferIsPricedAtItsFarEnd_AnOrderAtItsMidpoint()
        {
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            node.DemandHistory["Ore:Iron"] = 5.0;
            long reference;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Ore:Iron", out reference));

            long stock = reference;
            long units = reference / 2;
            Assert.Equal(ScarcityCurve.At(reference, stock - units, tuning.ScarcityMin, tuning.ScarcityMax),
                RowScarcity.ForOffer(oracle, node.Id, "Ore:Iron", stock, units), 9);
            Assert.Equal(ScarcityCurve.At(reference, units / 2, tuning.ScarcityMin, tuning.ScarcityMax),
                RowScarcity.ForOrder(oracle, node.Id, "Ore:Iron", 0, units), 9);
        }

        [Fact]
        public void ARoundTripAtOneNodeNeverProfits()
        {
            // The characteristic failure: buy an offer row out, then sell the same units back into
            // the order row the buy-out opened. The order must never pay more per unit than the offer.
            var (world, oracle, tuning) = Setup();
            var node = AddNode(world, oracle, "F1");
            node.DemandHistory["Ore:Iron"] = 5.0;
            long reference;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Ore:Iron", out reference));

            for (long start = 0; start <= 3 * reference; start += System.Math.Max(1, reference / 7))
            for (long bought = 1; bought <= start; bought += System.Math.Max(1, start / 5))
            for (long soldBack = 1; soldBack <= 3 * reference; soldBack += System.Math.Max(1, reference / 3))
            {
                double paid = RowScarcity.ForOffer(oracle, node.Id, "Ore:Iron", start, bought);
                double received = RowScarcity.ForOrder(oracle, node.Id, "Ore:Iron", start - bought, soldBack);
                Assert.True(received <= paid,
                    "start " + start + ", bought " + bought + ", sold back " + soldBack
                    + ": received " + received + " > paid " + paid);
            }
        }

        [Fact]
        public void AnOracleWithNoAtStockAnswerPricesAtTheCurrentStock()
        {
            var flat = NullPriceOracle.Instance;
            Assert.Equal(1.0, RowScarcity.ForOffer(flat, "N", "Ore:Iron", 100, 50), 9);
            Assert.Equal(1.0, RowScarcity.ForOrder(flat, "N", "Ore:Iron", 0, 50), 9);
        }

        // ---- where a shared need lands ----------------------------------------------------------

        [Fact]
        public void SharedNeed_GoesWhollyToTheNodeHoldingTheStock()
        {
            // An even split is wrong when the need spreads and the stock does not: only some of a
            // faction's industrial nodes make a given component, so splitting halves the protection
            // at the one node holding all of it.
            var (world, oracle, tuning) = Setup();
            var holder = AddNode(world, oracle, "F1");
            var empty  = AddNode(world, oracle, "F2");
            holder.Capabilities[NodeCapabilityKind.Refinery] = 1;
            empty.Capabilities[NodeCapabilityKind.Refinery] = 1;
            holder.Inventory["Comp:SteelPlate"] = 1000;
            oracle.InvalidateNode(holder.Id);
            oracle.InvalidateNode(empty.Id);

            world.Demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Soon, 1334));

            long atHolder;
            long atEmpty;
            Assert.True(oracle.TryGetReferenceStock(holder.Id, "Comp:SteelPlate", out atHolder));
            Assert.True(oracle.TryGetReferenceStock(empty.Id, "Comp:SteelPlate", out atEmpty));

            Assert.Equal(1334, atHolder);   // holds all of it, so it owes all of it
            Assert.Equal(667, atEmpty);     // holds none, but keeps a reason to accumulate
        }

        [Fact]
        public void SharedNeed_SplitsEvenlyWhenNobodyHoldsAny()
        {
            var (world, oracle, tuning) = Setup();
            var a = AddNode(world, oracle, "F1");
            var b = AddNode(world, oracle, "F2");
            a.Capabilities[NodeCapabilityKind.Refinery] = 1;
            b.Capabilities[NodeCapabilityKind.Refinery] = 1;
            oracle.InvalidateNode(a.Id);
            oracle.InvalidateNode(b.Id);

            world.Demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Soon, 1000));

            long refA;
            long refB;
            Assert.True(oracle.TryGetReferenceStock(a.Id, "Comp:SteelPlate", out refA));
            Assert.True(oracle.TryGetReferenceStock(b.Id, "Comp:SteelPlate", out refB));
            Assert.Equal(500, refA);
            Assert.Equal(500, refB);
        }

        [Fact]
        public void SharedNeed_TracksStockMovingBetweenNodes()
        {
            var (world, oracle, tuning) = Setup();
            var a = AddNode(world, oracle, "F1");
            var b = AddNode(world, oracle, "F2");
            a.Capabilities[NodeCapabilityKind.Refinery] = 1;
            b.Capabilities[NodeCapabilityKind.Refinery] = 1;
            a.Inventory["Comp:SteelPlate"] = 800;
            b.Inventory["Comp:SteelPlate"] = 200;
            oracle.InvalidateNode(a.Id);
            oracle.InvalidateNode(b.Id);
            world.Demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Soon, 1000));

            long refA;
            Assert.True(oracle.TryGetReferenceStock(a.Id, "Comp:SteelPlate", out refA));
            Assert.Equal(800, refA);

            // The stock moves; the claim must move with it rather than staying where it was cached.
            a.Inventory["Comp:SteelPlate"] = 200;
            b.Inventory["Comp:SteelPlate"] = 800;
            world.UtcNow = world.UtcNow.AddSeconds(1);

            Assert.True(oracle.TryGetReferenceStock(a.Id, "Comp:SteelPlate", out refA));
            Assert.Equal(500, refA);   // now holds a fifth, so the even split is the larger claim
        }

    }
}
