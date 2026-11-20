using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A Habitat asks for its workers' food and keeps it from haulers (<see cref="WorkerFoodDemand"/>).
    /// The failure it prevents, read live 2026-10-18 at EXP's N099 on Tellus: the node's Farm grew
    /// vegetables, haulers loaded them for the FoodProcessor at N097 (`HAUL_PICKUP … loaded=10
    /// Vegetables`), and the workers left one by one on `fed=0` until the last went at 20:49.
    /// </summary>
    public sealed class WorkerFoodDemandTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

        private sealed class Menu : INutritionCatalogue
        {
            public double NutritionPerUnit(string invKey)
            {
                switch (invKey)
                {
                    case "Fruit": return 0.06;
                    case "MealPack_VeggieBurger": return 0.46;
                    case "MealPack_SteakDinner": return 1.0;
                    default: return 0;
                }
            }
        }

        /// <summary>Four workers eat 2.23 an hour each; six hours of that is 53.52 nutrition.</summary>
        private static WorldModel World()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = T0;
            world.Nutrition = new Menu();
            world.Tuning.HabitatBedsPerLevel = 20;
            world.Tuning.WorkerNutritionPerHour = 2.23;
            world.Tuning.WorkerFoodReserveHours = 6.0;
            return world;
        }

        private static SpatialNode Node(WorldModel world, string id, Vec3 pos, double workers)
        {
            SpatialNode n = world.Graph.Add(pos, 0.5, id);
            n.OwnerFactionTag = "EXP";
            n.Capabilities[NodeCapabilityKind.Habitat] = 1;
            n.Workers = workers;
            n.WorkersAtUtc = workers > 0 ? T0 : default(DateTime);
            return n;
        }

        private static long SurplusOf(SpatialNode node, string key, WorldModel world)
        {
            MethodInfo m = typeof(OperationsManager).GetMethod("SurplusOf", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(SpatialNode), typeof(string), typeof(WorldModel), typeof(FactionTuning) }, null);
            return (long)m.Invoke(null, new object[] { node, key, world, world.Tuning });
        }

        private static string Post(WorldModel world, SpatialNode n, IEnumerable<string> producible, out long units)
        {
            var ctx = new LiveProductionExecutionContext(world, new List<string>());
            double have, target; bool isNew;
            return WorkerFoodDemand.PostForNode("EXP", n, ctx, world.Tuning, world.Nutrition, producible,
                T0.AddHours(1), out units, out have, out target, out isNew);
        }

        // ---- the keep ---------------------------------------------------------------------------

        [Fact]
        public void TheKeep_IsSixHoursOfEating_AndTheRestOfTheHarvestStillMoves()
        {
            WorldModel world = World();
            SpatialNode farm = Node(world, "N099", new Vec3(0, 0, 0), 4);
            farm.Inventory["Fruit"] = 2000;

            Assert.Equal(892L, WorkerFoodDemand.KeepUnits(farm, "Fruit", world.Tuning, world.Nutrition));   // 53.52 / 0.06
            Assert.Equal(2000L - 892L, SurplusOf(farm, "Fruit", world));
        }

        [Fact]
        public void TheKeep_FollowsTheEatingOrder_SoMealsPastTheTargetAreFree()
        {
            WorldModel world = World();
            SpatialNode n = Node(world, "N099", new Vec3(0, 0, 0), 4);
            n.Inventory["Fruit"] = 500;                    // 30 nutrition, eaten first
            n.Inventory["MealPack_SteakDinner"] = 100;     // 100 nutrition

            Assert.Equal(500L, WorkerFoodDemand.KeepUnits(n, "Fruit", world.Tuning, world.Nutrition));
            Assert.Equal(24L, WorkerFoodDemand.KeepUnits(n, "MealPack_SteakDinner", world.Tuning, world.Nutrition));
            Assert.Equal(76L, SurplusOf(n, "MealPack_SteakDinner", world));
        }

        [Fact]
        public void NobodyLivingThere_OrAKeyNobodyEats_KeepsNothing()
        {
            WorldModel world = World();
            SpatialNode empty = Node(world, "N100", new Vec3(0, 0, 0), 0);
            empty.Inventory["Fruit"] = 2000;
            SpatialNode lived = Node(world, "N099", new Vec3(10, 0, 0), 4);
            lived.Inventory["Grain"] = 50;

            Assert.Equal(0L, WorkerFoodDemand.KeepUnits(empty, "Fruit", world.Tuning, world.Nutrition));
            Assert.Equal(2000L, SurplusOf(empty, "Fruit", world));
            Assert.Equal(0L, WorkerFoodDemand.KeepUnits(lived, "Grain", world.Tuning, world.Nutrition));
        }

        // ---- the demand -------------------------------------------------------------------------

        [Fact]
        public void AnEmptyHabitat_AsksForTheBestFoodHeldElsewhere_AtUpkeep_NeedAndGrossItemRows()
        {
            WorldModel world = World();
            SpatialNode home = Node(world, "N099", new Vec3(0, 0, 0), 4);
            SpatialNode kitchen = Node(world, "N097", new Vec3(10, 0, 0), 0);
            kitchen.Inventory["Fruit"] = 300;
            kitchen.Inventory["MealPack_VeggieBurger"] = 40;

            long units;
            string key = Post(world, home, null, out units);

            Assert.Equal("MealPack_VeggieBurger", key);
            Assert.Equal(117L, units);                     // ceil(53.52 / 0.46)
            DemandEntry item, need;
            Assert.True(world.Demand.TryGet("EXP", DemandRegister.ItemKind("MealPack_VeggieBurger"), "N099", null, out item));
            Assert.True(world.Demand.TryGet("EXP", DemandRegister.NeedKind("MealPack_VeggieBurger"), "N099", null, out need));
            Assert.Equal(117L, item.Quantity);
            Assert.Equal(117L, need.Quantity);
            Assert.Equal(DemandClass.Upkeep, item.Class);
            Assert.Equal(DemandClass.Upkeep, need.Class);
        }

        [Fact]
        public void TheItemRowIsGross_StockAlreadyHeldCountsTowardTheTarget()
        {
            WorldModel world = World();
            SpatialNode home = Node(world, "N099", new Vec3(0, 0, 0), 4);
            home.Inventory["MealPack_SteakDinner"] = 20;   // 20 of 53.52
            SpatialNode kitchen = Node(world, "N097", new Vec3(10, 0, 0), 0);
            kitchen.Inventory["MealPack_SteakDinner"] = 100;

            long units;
            Assert.Equal("MealPack_SteakDinner", Post(world, home, null, out units));
            Assert.Equal(34L, units);
            DemandEntry item;
            Assert.True(world.Demand.TryGet("EXP", DemandRegister.ItemKind("MealPack_SteakDinner"), "N099", null, out item));
            Assert.Equal(54L, item.Quantity);              // what the node should hold: 20 + 34
        }

        [Fact]
        public void WithNothingHeldAnywhere_ItNamesTheBestFoodItCanProduce()
        {
            WorldModel world = World();
            SpatialNode home = Node(world, "N099", new Vec3(0, 0, 0), 4);

            long units;
            Assert.Equal("MealPack_SteakDinner",
                Post(world, home, new[] { "Fruit", "Ingot:Iron", "MealPack_SteakDinner" }, out units));
            Assert.Equal(54L, units);
        }

        [Fact]
        public void AStockedHabitat_AsksForNothing()
        {
            WorldModel world = World();
            SpatialNode stocked = Node(world, "N099", new Vec3(0, 0, 0), 4);
            stocked.Inventory["MealPack_SteakDinner"] = 60;
            SpatialNode kitchen = Node(world, "N097", new Vec3(10, 0, 0), 0);
            kitchen.Inventory["MealPack_SteakDinner"] = 100;

            long units;
            Assert.Null(Post(world, stocked, null, out units));
            Assert.Empty(world.Demand.SnapshotItemDemands("EXP"));
        }

        [Fact]
        public void AHabitatWithNothingToName_AsksForNothing()
        {
            WorldModel world = World();
            SpatialNode bare = Node(world, "N101", new Vec3(20, 0, 0), 4);

            long units;
            Assert.Null(Post(world, bare, new[] { "Ingot:Iron" }, out units));
            Assert.Empty(world.Demand.SnapshotItemDemands("EXP"));
        }
    }
}
