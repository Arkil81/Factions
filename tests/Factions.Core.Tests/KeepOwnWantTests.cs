using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-211: a haul source keeps back what its own demand row for an item still wants.
    /// Live 2026-10-09, STCG moved the same 9,121 <c>Ore:Ice</c> N027 -> N097 -> N027 -> N097 inside eight
    /// minutes while both nodes posted a row for it, because each read the other's stock as surplus.</summary>
    public sealed class KeepOwnWantTests
    {
        private const string Key = "Ore:Ice";

        private sealed class CargoCatalogue : IPrefabCatalogue
        {
            public bool HasPrefab(string prefabId) => true;
            public long CargoLiters(string prefabId) => 1_000_000;
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long> { { "Gas:Hydrogen", 1_000_000 }, { "Gas:Oxygen", 1_000_000 } }; }
            public double Dps(string prefabId) => 0;
            public IReadOnlyList<string> RolesFor(string prefabId) => new[] { "Haul", "Trade", "Mine" };
            public double DryMassKg(string prefabId) => 0;
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) => null;
            public double DrillYieldScore(string prefabId) => 1.0;
            public string GridSize(string prefabId) => "Small";
            public long Cost(string prefabId) => 0;
            public string FactionHint(string prefabId) => "All";
            public IEnumerable<string> AllPrefabIds => new[] { "Hauler" };
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) => new WeaponBand[0];
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) => new[] { "Hauler" };
        }

        private static WorldModel MakeWorld()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = new CargoCatalogue();
            return world;
        }

        private static SpatialNode AddNode(WorldModel world, string id, double x, long stock)
        {
            SpatialNode n = world.Graph.Add(new Vec3(x, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            if (stock > 0) n.Inventory[Key] = stock;
            n.ShipStock["Hauler"] = 1;
            return n;
        }

        private static void Post(WorldModel world, string nodeId, long qty, DemandClass cls)
        {
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind       = "Item:" + Key,
                NodeId     = nodeId,
                Quantity   = qty,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
                Class      = cls,
            });
        }

        private static List<PendingFleetAction> Tick(WorldModel world)
        {
            var st = new FactionState { Tag = "F" };
            var ops = new OperationsManager();
            ops.Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), new List<string>());
            return st.PendingFleets.Where(f => f.Kind == ActionKind.Haul).ToList();
        }

        /// <summary>The live shape: two nodes, each short of the same key at the same class.</summary>
        private static WorldModel TwoShortNodes(DemandClass bClass)
        {
            var world = MakeWorld();
            AddNode(world, "A", 0, 900);
            AddNode(world, "B", 1, 0);
            Post(world, "A", 5_400, DemandClass.RecipeInput);
            Post(world, "B", 2_700, bClass);
            return world;
        }

        [Fact]
        public void TwoNodesShortOfOneKey_NeitherFeedsTheOther()
        {
            Assert.Empty(Tick(TwoShortNodes(DemandClass.RecipeInput)));
        }

        /// <summary>Also the discriminator for the test above: the same world with B one class higher
        /// dispatches, so the empty result there is the reserve and not a setup that cannot dispatch.</summary>
        [Fact]
        public void AnOutrankingDestination_StillDrawsTheReserve()
        {
            PendingFleetAction haul = Assert.Single(Tick(TwoShortNodes(DemandClass.ShipBuild)));
            Assert.Equal("B", haul.TargetNodeId);
            Assert.Equal(900, haul.Cargo[Key]);
        }

        [Fact]
        public void StockAboveTheOwnRow_IsStillSpare()
        {
            var world = MakeWorld();
            AddNode(world, "A", 0, 1_000);
            AddNode(world, "B", 1, 0);
            Post(world, "A", 400, DemandClass.RecipeInput);
            Post(world, "B", 2_700, DemandClass.RecipeInput);

            PendingFleetAction haul = Assert.Single(Tick(world));
            Assert.Equal("B", haul.TargetNodeId);
            Assert.Equal(600, haul.Cargo[Key]);
        }

        [Fact]
        public void ASourceWithNoRowOfItsOwn_GivesEverything()
        {
            var world = MakeWorld();
            AddNode(world, "A", 0, 900);
            AddNode(world, "B", 1, 0);
            Post(world, "B", 2_700, DemandClass.Catalog);

            PendingFleetAction haul = Assert.Single(Tick(world));
            Assert.Equal(900, haul.Cargo[Key]);
        }

        /// <summary>Fuel gas is never held back: on 2026-10-10 the reserve raised the share of fuel departures flew
        /// without from 8% to 11%. Two nodes short of hydrogen the faction keeps as fuel still feed each other.</summary>
        [Fact]
        public void TwoNodesShortOfAFuelGas_StillFeedEachOther()
        {
            const string gas = "Gas:Hydrogen";
            var world = MakeWorld();
            world.SetFuelReserveKeys("F", new[] { gas });
            SpatialNode a = AddNode(world, "A", 0, 0);
            AddNode(world, "B", 1, 0);
            a.Inventory[gas] = 900;
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:" + gas, NodeId = "A", Quantity = 5_400,
                                                ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.Fuel });
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:" + gas, NodeId = "B", Quantity = 2_700,
                                                ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.Fuel });

            PendingFleetAction haul = Assert.Single(Tick(world));
            Assert.Equal("B", haul.TargetNodeId);
        }

        /// <summary>Between two nodes short of a fuel gas it flows from the fuller to the emptier only,
        /// and only as far as evens them out. Live 2026-11-22: 60% of ISBT's 12.65 GL of hydrogen in
        /// 14 h was matched by a haul the other way on the same pair. Here B holds 1.8 M of 2.7 M
        /// (67%) and A 0.9 M of 5.4 M (17%): B sends A 0.9 M, which leaves both at a third, and A
        /// sends B nothing -- before the rule A, the emptier, also fed B.</summary>
        [Fact]
        public void TwoNodesShortOfAFuelGas_OnlyTheFullerFeedsTheEmptier()
        {
            const string gas = "Gas:Hydrogen";
            var world = MakeWorld();
            world.SetFuelReserveKeys("F", new[] { gas });
            SpatialNode a = AddNode(world, "A", 0, 0);
            SpatialNode b = AddNode(world, "B", 1, 0);
            a.Inventory[gas] = 900_000;
            b.Inventory[gas] = 1_800_000;
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:" + gas, NodeId = "A", Quantity = 5_400_000,
                                                ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.Fuel });
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:" + gas, NodeId = "B", Quantity = 2_700_000,
                                                ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.Fuel });

            PendingFleetAction haul = Assert.Single(Tick(world));
            Assert.Equal("A", haul.TargetNodeId);
            Assert.Equal(900_000, haul.Cargo[gas]);
        }

        [Theory]
        [InlineData(1_800, 2_700, 900, 5_400, 900)]     // the test above: both end at a third
        [InlineData(900, 5_400, 1_800, 2_700, 0)]       // the emptier never feeds the fuller
        [InlineData(900, 5_400, 0, 2_700, 300)]         // both end at a ninth
        [InlineData(1_000, 2_000, 500, 1_000, 0)]       // already even
        [InlineData(3_000, 2_000, 0, 1_000, long.MaxValue)]   // a source at or above its row is not limited
        [InlineData(0, 2_000, 0, 1_000, 0)]             // nothing to send
        public void FuelGradientAllowance_EvensTheShareOfEachRow(long srcStock, long srcRow, long destStock, long destRow,
            long expected)
        {
            Assert.Equal(expected, OperationsManager.FuelGradientAllowance(srcStock, srcRow, destStock, destRow));
        }

        /// <summary>A gas the faction does not keep as fuel is held back like an item. Live 2026-11-21:
        /// every IGSL node posted a 4 M L oxygen row and read its whole stock as spare, so 73% of its
        /// 92,843 hauls were oxygen and 65% of the volume came back on the same pair.</summary>
        [Fact]
        public void TwoNodesShortOfANonFuelGas_NeitherFeedsTheOther()
        {
            Assert.Empty(Tick(TwoShortOfGas("Gas:Oxygen", fuelKeys: new[] { "Gas:Hydrogen" })));
        }

        /// <summary>The discriminator: the same world, with oxygen named a fuel key, dispatches -- so the
        /// empty result above is the reserve and not a setup that cannot haul gas.</summary>
        [Fact]
        public void TheSameGasNamedAsFuel_StillMoves()
        {
            PendingFleetAction haul = Assert.Single(Tick(TwoShortOfGas("Gas:Oxygen", fuelKeys: new[] { "Gas:Oxygen" })));
            Assert.Equal("B", haul.TargetNodeId);
        }

        private static WorldModel TwoShortOfGas(string gas, string[] fuelKeys)
        {
            var world = MakeWorld();
            world.SetFuelReserveKeys("F", fuelKeys);
            SpatialNode a = AddNode(world, "A", 0, 0);
            AddNode(world, "B", 1, 0);
            a.Inventory[gas] = 900_000;     // a full tank, so the load gate passes it
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:" + gas, NodeId = "A", Quantity = 5_400_000,
                                                ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.Catalog });
            world.Demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:" + gas, NodeId = "B", Quantity = 2_700_000,
                                                ExpiresUtc = world.UtcNow.AddMinutes(5), Class = DemandClass.Catalog });
            return world;
        }

        [Theory]
        [InlineData(900, 1_000, 400, 300)]   // spare 600 of a 900 surplus
        [InlineData(300, 1_000, 400, 0)]     // a keep already took more than the row: nothing further
        [InlineData(900, 900, 5_400, 900)]   // short of its own row: keeps all of it
        [InlineData(900, 900, 0, 0)]         // no row
        [InlineData(0, 900, 5_400, 0)]       // no surplus to keep
        public void OwnWantReserve_IsTheSurplusAboveTheStockTheRowLeavesSpare(long surplus, long stock, long row, long expected)
        {
            Assert.Equal(expected, OperationsManager.OwnWantReserve(surplus, stock, row));
        }
    }
}
