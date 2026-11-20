using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Reference stock is what lets gas be produced at all, and these tests pin the two halves of
    /// that.
    ///
    /// <para><c>PriceOracle.ScarcityMultiplier</c> answers a flat <c>1.0</c> whenever reference stock
    /// is 0, and <c>ProductionManager.TickCapabilityBank</c> skips every recipe at
    /// <c>scarcity &lt;= 1.0</c>. So with <c>ReferenceStockFor(Hydrogen)</c> hard-coded to 0 and no
    /// <c>CapabilityOutputFor</c> row for gas, an installed and actively demanded FuelDepot still
    /// produced nothing — the capability resolving its recipes was never sufficient.</para>
    ///
    /// <para>The floor is also the chain's bootstrap, and gas is the one kind whose floor the oracle
    /// applies unconditionally. Every other key has to be wanted, owed or glutted to earn one —
    /// which for gas would be circular, because the want is downstream of the capability the want
    /// would ask for.</para>
    ///
    /// <para>For gas the floor is also the shelf. The 10,000,000 L / 2,000,000 L figures used to be
    /// reached through the capability credit budget; that budget is gone and they are now stated
    /// once, on <c>ReferenceStockHydrogen</c> / <c>ReferenceStockOxygen</c>.</para>
    /// </summary>
    public class GasReferenceStockTests
    {
        private static SpatialNode Node(WorldModel world, string id)
        {
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = "AAA";
            n.HoldStrength = 1.0;
            return n;
        }

        [Fact]
        public void ANodeWithNoFuelDepot_StillHoldsAGasReference_SoTheChainCanStart()
        {
            WorldModel world = TestSupport.World();
            var oracle = new PriceOracle(world, world.Tuning);
            SpatialNode node = Node(world, "N1");   // no capabilities at all

            long reference;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Gas:Hydrogen", out reference),
                "a zero reference is what left the gas chain unable to bootstrap");
            Assert.Equal(world.Tuning.ReferenceStockHydrogen, reference);
            Assert.True(oracle.ScarcityMultiplier(node.Id, "Gas:Hydrogen") > 1.0,
                "the production pull gate admits a recipe only above 1.0");
        }

        [Fact]
        public void ZeroingTheFloor_RestoresTheDefectExactly()
        {
            // The discriminator, stated as a failing state: at floor 0 an empty node reports NO
            // reference and a NEUTRAL multiplier, which is indistinguishable from "nothing here wants
            // gas" and is why no depot was ever invested in.
            WorldModel world = TestSupport.World();
            world.Tuning.ReferenceStockHydrogen = 0;
            world.Tuning.ReferenceStockOxygen   = 0;
            var oracle = new PriceOracle(world, world.Tuning);
            SpatialNode node = Node(world, "N1");

            long reference;
            Assert.False(oracle.TryGetReferenceStock(node.Id, "Gas:Hydrogen", out reference));
            Assert.Equal(1.0, oracle.ScarcityMultiplier(node.Id, "Gas:Hydrogen"), 9);
        }

        [Fact]
        public void ANodeHullsActuallyFuelAtWantsMoreThanTheBareShelf()
        {
            // What separates two nodes is now draw, not hardware: FuelDraw credits DemandHistory
            // wherever hulls really depart, and that is the term that lifts a busy depot above the
            // shelf every node gets. A depot nothing launches from has no more claim on the world's
            // ice than an outpost.
            WorldModel world = TestSupport.World();
            var oracle = new PriceOracle(world, world.Tuning);
            SpatialNode quiet = Node(world, "N1");
            SpatialNode busy  = Node(world, "N2");
            busy.DemandHistory["Gas:Hydrogen"] = 20_000_000;   // litres drawn per pass

            long quietRef, busyRef;
            Assert.True(oracle.TryGetReferenceStock(quiet.Id, "Gas:Hydrogen", out quietRef));
            Assert.True(oracle.TryGetReferenceStock(busy.Id, "Gas:Hydrogen", out busyRef));

            Assert.Equal(world.Tuning.ReferenceStockHydrogen, quietRef);
            Assert.True(busyRef > quietRef,
                "a node hulls actually fuel at must want more hydrogen than the bare shelf; got "
                + busyRef + " against " + quietRef);
        }

        [Fact]
        public void TheGasShelfIsStillTheOperatorsNumber()
        {
            // 10,000,000 L of hydrogen and 2,000,000 L of oxygen -- operator, 2026-08-29. Pinned
            // because target stock is what STOPS production: these two numbers are the throttle on
            // the whole chain, and on how much of the world's ice it may consume. They used to be
            // computed as 2000 x 10 x 1500 / 3 and 1000 x 10 x 1000 / 5 through a credit budget that
            // no longer exists; if this test moves, the throttle moved with it.
            WorldModel world = TestSupport.World();
            var oracle = new PriceOracle(world, world.Tuning);
            SpatialNode depot = Node(world, "N1");

            long h2, o2;
            Assert.True(oracle.TryGetReferenceStock(depot.Id, "Gas:Hydrogen", out h2));
            Assert.True(oracle.TryGetReferenceStock(depot.Id, "Gas:Oxygen", out o2));

            Assert.Equal(10_000_000L, h2);
            Assert.Equal(2_000_000L, o2);
        }

        [Fact]
        public void AFuelDepotProducesBothGases_NotOnlyHydrogen()
        {
            // SE's generator converts ice into whichever gas is drawn, so both rows must be present.
            // Oxygen has no consumer yet by operator decision, but a missing row would mean it is
            // never produced either, and FAC-103 would inherit a chain that only ever made H2.
            var tuning = new FactionTuning();

            Assert.True(tuning.CapabilityOutputFor(NodeCapabilityKind.FuelDepot, ItemKind.Hydrogen) > 0);
            Assert.True(tuning.CapabilityOutputFor(NodeCapabilityKind.FuelDepot, ItemKind.Oxygen) > 0);
            // Twice as much hydrogen per unit of ice, which is SE's own 20:1 against 10:1 ratio.
            Assert.Equal(
                2 * tuning.CapabilityOutputFor(NodeCapabilityKind.FuelDepot, ItemKind.Oxygen),
                tuning.CapabilityOutputFor(NodeCapabilityKind.FuelDepot, ItemKind.Hydrogen));
        }

        [Fact]
        public void AFuelDepotWantsIceOnHand_BecauseThatIsWhatItConsumes()
        {
            // Its input row was empty while it carried a phantom 80-unit Hydrogen INPUT row -- the
            // depot is the thing that produces hydrogen, and what it actually consumes is ice.
            var tuning = new FactionTuning();

            Assert.True(tuning.CapabilityInputFor(NodeCapabilityKind.FuelDepot, ItemKind.Ore) > 0);
            Assert.Equal(0, tuning.CapabilityInputFor(NodeCapabilityKind.FuelDepot, ItemKind.Hydrogen));
        }

        [Fact]
        public void TheWantGateDoesNotApplyToGas()
        {
            // The circle this floor exists to break, stated as a test rather than as a note. Every
            // other kind earns its floor by being wanted; gas cannot, because the want is downstream
            // of the FuelDepot the want would ask for. A node with no gas demand anywhere in its
            // faction must still hold an opinion about hydrogen.
            WorldModel world = TestSupport.World();
            var oracle = new PriceOracle(world, world.Tuning);
            SpatialNode node = Node(world, "N1");

            long gas, ore;
            Assert.True(oracle.TryGetReferenceStock(node.Id, "Gas:Hydrogen", out gas));
            Assert.False(oracle.TryGetReferenceStock(node.Id, "Ore:Iron", out ore),
                "ore has no such exemption -- unwanted, unconsumed and unheld, it targets nothing");
        }
    }
}
