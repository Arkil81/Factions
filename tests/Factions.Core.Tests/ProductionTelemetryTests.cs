using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ProductionTelemetryTests
    {
        [Fact]
        public void Tick_ReportsBlockedInputDemandWorkAsDistinctProductionPhase()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind = "Item:Comp:SteelPlate",
                NodeId = "N1",
                Quantity = 100,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var manager = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate",
                        TimeSecs = 1,
                        Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1 } },
                    },
                },
            });

            manager.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, world, world.Tuning, new List<string>());

            ProductionTickTelemetry telemetry = manager.LastTelemetry;
            Assert.Equal(1, telemetry.Factions);
            Assert.Equal(1, telemetry.OwnedNodes);
            Assert.Equal(1, telemetry.CapabilityBanks);
            Assert.Equal(1, telemetry.RecipesExamined);
            Assert.Equal(1, telemetry.BlockedRecipes);
            Assert.Equal(1, telemetry.BlockedInputPosts);
            Assert.Equal(1, telemetry.DemandPosts);
            Assert.Equal(0, telemetry.DemandRefreshes);
            Assert.True(telemetry.BlockedDemandTicks >= 0);
        }
    }
}
