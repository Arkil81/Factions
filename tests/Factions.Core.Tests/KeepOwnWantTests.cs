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
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long> { { "Gas:Hydrogen", 1_000_000 } }; }
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

        /// <summary>Gas is never held back: on 2026-10-10 the reserve raised the share of fuel departures flew
        /// without from 8% to 11%. Two nodes short of hydrogen still feed each other.</summary>
        [Fact]
        public void TwoNodesShortOfAGas_StillFeedEachOther()
        {
            const string gas = "Gas:Hydrogen";
            var world = MakeWorld();
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
