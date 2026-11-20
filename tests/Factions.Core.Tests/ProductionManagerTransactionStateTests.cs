using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class ProductionManagerTransactionStateTests
    {
        [Fact]
        public void TransactionState_AbortedGuard_DoesNotPublishSlotsSuppressionLogsOrTelemetry()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            var state = new FactionState { Tag = "F" };
            var manager = new ProductionManager(Recipes());
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);

            object transaction = Invoke(manager, "BeginTransactionForTesting");
            Invoke(transaction, "StageSlot", "F:N1:1:0", "SteelPlate", 0.5, world.UtcNow);
            Invoke(transaction, "StageCatalogSuppression", "F:Comp:Pistol", true);
            Invoke(transaction, "AddLog", "F CATALOG_DEMAND Comp:Pistol");
            Invoke(transaction, "SetTelemetryFactions", 1);

            world.AddNodeInventory("N1", "Ingot:Iron", 1);
            var log = new List<string>();

            Assert.False((bool)Invoke(manager, "TryCommitTransactionForTesting", transaction, guard, state, world, log));
            Assert.Empty(manager.SnapshotThroughput("F"));
            Assert.False((bool)Invoke(manager, "IsCatalogDemandSuppressedForTesting", "F:Comp:Pistol"));
            Assert.Empty(log);
            Assert.Equal(0, manager.LastTelemetry.Factions);
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.Invoke(target, arguments);
        }

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> Recipes()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Manufactory] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "SteelPlate",
                        TimeSecs = 1,
                        Inputs = new[]
                        {
                            new RecipeItem
                            {
                                TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1,
                            },
                        },
                        Outputs = new[]
                        {
                            new RecipeItem
                            {
                                TypeId = "MyObjectBuilder_Component", SubTypeId = "SteelPlate", Amount = 1,
                            },
                        },
                    },
                },
            };
        }
    }
}
