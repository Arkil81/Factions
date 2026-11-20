using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// How big a bank's input-demand row is allowed to be.
    ///
    /// <para>Machine geometry sizes an unchained row at a full bank's throughput across the demand
    /// TTL, which counts installed machines rather than what they consume. Live on v0.276.0, IMPR's
    /// N035 posted <c>Ore:Stone need=12,252,000</c> where its own measured consumption justified
    /// 443,234 — and the mine basket extracts in proportion to those quantities, so the rock a
    /// drill spends its time on is decided by that number.</para>
    ///
    /// <para>The cap is the node's flow-ranked target stock, the number every store price and buy
    /// order already reads, and only when a MEASURED term set it. Three rows must NOT move: a
    /// chain-bound one — a commitment is a burst the flow term has not observed — one the oracle has
    /// no opinion about, which is how a cold node bootstraps, and one whose target rests on the
    /// per-kind floor, which is a gate-opening value rather than a shelf size.</para>
    /// </summary>
    public class DemandRowTargetCapTests
    {
        // The live shape: a refinery holding no stone, running the joint-product stone recipe under
        // a gravel want too large for the output cap to bind. 1,000 ore per batch is what makes a
        // bank's geometry enormous next to anything the node could consume.
        //
        // RecipeInput is the class the live stone rows carry and is excluded from the chain roots,
        // so by default the ore row reaches the unchained branch -- the one with no bound of its own.
        private static (WorldModel world, ProductionManager mgr, List<FactionState> states) BlockedRefinery(
            IPriceOracle oracle, long ingotWant = 1000000,
            DemandClass wantClass = DemandClass.RecipeInput)
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 42;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ingot:Stone", NodeId = "N1",
                Quantity = ingotWant, Class = wantClass,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var stone = new ConversionRecipe
            {
                BlueprintId = "StoneOreToIngot",
                TimeSecs = 10f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Stone", Amount = 1000 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Stone", Amount = 14f } },
            };
            var capRecipes = new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe> { stone },
            };

            var mgr = new ProductionManager(capRecipes, null, oracle);
            return (world, mgr, new List<FactionState> { new FactionState { Tag = "F" } });
        }

        private static DemandEntry OreRow(WorldModel world)
        {
            return Assert.Single(world.Demand.SnapshotItemDemands("F"), d => d.Kind == "Item:Ore:Stone");
        }

        [Fact]
        public void UnchainedInputRow_IsCappedAtTheNodeTargetStock()
        {
            var (world, mgr, states) = BlockedRefinery(new TargetOracle("Ore:Stone", 4000));

            mgr.Tick(states, world, world.Tuning, new List<string>());

            Assert.Equal(4000, OreRow(world).Quantity);
        }

        [Fact]
        public void WithoutTheCap_TheSameRowIsSizedByInstalledMachines()
        {
            // The negative control for the test above: same world, an oracle with no opinion. This
            // is the number the cap replaces, and it has to be far larger or the assertion above
            // passes for a reason that is not the cap.
            var (world, mgr, states) = BlockedRefinery(new TargetOracle("Ore:Stone", 0));

            mgr.Tick(states, world, world.Tuning, new List<string>());

            Assert.True(OreRow(world).Quantity > 40000,
                "geometry should size this row far above the 4,000 target; saw " + OreRow(world).Quantity);
        }

        [Fact]
        public void NoOpinionFromTheOracle_LeavesTheRowAlone()
        {
            // A node that has never consumed the key has no flow to read, and a cap there would
            // stop it ever acquiring the first of it. Same world as the control above, asserted for
            // its own reason: this is the bootstrap guard, not a baseline.
            var (world, mgr, states) = BlockedRefinery(new TargetOracle("Ore:Stone", 0));

            mgr.Tick(states, world, world.Tuning, new List<string>());

            Assert.True(OreRow(world).Quantity > 0, "no opinion must not zero the row");
        }

        [Fact]
        public void AnOracleAnsweringZero_DoesNotZeroTheRow()
        {
            // The live oracle reports "no opinion" by returning false, so this guards the other
            // reading of the same state -- a target answered AS zero. Nothing should be able to
            // reduce a row to nothing: a node cannot consume what it never asks for, and its flow
            // would then never rise again.
            var (world, mgr, states) = BlockedRefinery(new TargetOracle("Ore:Stone", 0, answersZero: true));

            mgr.Tick(states, world, world.Tuning, new List<string>());

            Assert.True(OreRow(world).Quantity > 0, "a zero target must not zero the row");
        }

        [Fact]
        public void AFloorOnlyTarget_DoesNotCapTheRow()
        {
            // The per-kind floor is sized to open the scarcity gate for a recipe that has never run,
            // not to say how much the node should hold. Live on v0.277.0, reading it as an order size
            // pinned 462 of 956 capped rows at exactly ReferenceStockIngot (60) -- Ingot:Iron among
            // them, on a node whose refineries consume it by the thousand.
            var (world, mgr, states) = BlockedRefinery(new TargetOracle("Ore:Stone", 60, floorOnly: true));

            mgr.Tick(states, world, world.Tuning, new List<string>());

            Assert.True(OreRow(world).Quantity > 40000,
                "a floor-only target must not size the row; saw " + OreRow(world).Quantity);
        }

        [Fact]
        public void ATargetAboveTheAppetite_DoesNotRaiseTheRow()
        {
            // The cap is a ceiling, never a floor: it can only ever make a row smaller.
            var (world, mgr, states) = BlockedRefinery(new TargetOracle("Ore:Stone", 0));
            mgr.Tick(states, world, world.Tuning, new List<string>());
            long uncapped = OreRow(world).Quantity;

            var (world2, mgr2, states2) = BlockedRefinery(new TargetOracle("Ore:Stone", 50000000));
            mgr2.Tick(states2, world2, world2.Tuning, new List<string>());

            Assert.Equal(uncapped, OreRow(world2).Quantity);
        }

        [Fact]
        public void AChainBoundRow_KeepsTheCommitmentsSize_NotTheTarget()
        {
            // A hull's ore sits one level below the BOM the target's `owed` term counts in ingots,
            // so the flow term cannot have observed the burst. The chain already bounds this row by
            // the real requirement, and the cap must not cut it further.
            //
            // Unclassified is a chain ROOT (RecipeInput and ShipBuild are excluded), so the same
            // want that was unchained above descends here and binds the ore.
            var (world, mgr, states) = BlockedRefinery(new TargetOracle("Ore:Stone", 10),
                ingotWant: 7000, wantClass: DemandClass.Unclassified);

            mgr.Tick(states, world, world.Tuning, new List<string>());

            DemandEntry ore = OreRow(world);
            Assert.True(ore.ChainSized, "precondition: this row must reach the chain-bound branch");
            Assert.True(ore.Quantity > 10,
                "a commitment must not be cut to the flow-ranked target; saw " + ore.Quantity);
        }

        [Fact]
        public void TheCappedRow_NamesWhatCappedIt()
        {
            // The pre-cap appetite beside the posted size, and which of the two caps bound it --
            // otherwise the line cannot show the cap working or failing.
            var (world, mgr, states) = BlockedRefinery(new TargetOracle("Ore:Stone", 4000));
            var log = new List<string>();

            mgr.Tick(states, world, world.Tuning, log);

            string line = Assert.Single(log, l => l.Contains("PROD_INPUT_DEMAND Ore:Stone"));
            Assert.Contains("need=4000", line);
            Assert.Contains("appetite=", line);
            Assert.Contains("target=capped", line);
            Assert.DoesNotContain("chain=capped", line);
        }

        /// <summary>Answers one key with a fixed target and has no opinion about anything else — a
        /// target of 0 is returned as "no opinion", which is the state the live oracle reports as
        /// <c>won=none</c>.
        ///
        /// <para><paramref name="floorOnly"/> models the live oracle's other refusal: a target that
        /// exists but rests on the per-kind floor. <c>TryGetReferenceStock</c> still answers it —
        /// scarcity is priced off it — while <c>TryGetMeasuredTargetStock</c> does not, and the cap
        /// reads the second.</para>
        ///
        /// <para>Scarcity is 2.0 for every key, matching what <c>ScarcityPriority</c> substitutes
        /// when no oracle is supplied at all: the pull gate admits a recipe only above 1.0, so a
        /// neutral stub would refuse the bank and the row under test would never be posted.</para>
        /// </summary>
        private sealed class TargetOracle : IPriceOracle
        {
            private readonly string _key;
            private readonly long _target;
            private readonly bool _answersZero;
            private readonly bool _floorOnly;
            public TargetOracle(string key, long target, bool answersZero = false, bool floorOnly = false)
            { _key = key; _target = target; _answersZero = answersZero; _floorOnly = floorOnly; }

            public double ScarcityMultiplier(string nodeId, string inventoryKey) { return 2.0; }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                if (inventoryKey == _key && (_target > 0 || _answersZero))
                { reference = _target; return true; }
                reference = 0;
                return false;
            }

            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target)
            {
                if (_floorOnly) { target = 0; return false; }
                return TryGetReferenceStock(nodeId, inventoryKey, out target);
            }
        }
    }
}
