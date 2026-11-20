using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The <c>Gas:</c> inventory key — the form gas takes in the item economy, introduced with the
    /// FuelDepot ice-to-gas chain (FAC-038). Gas is not a physical item in SE: it is litres of a
    /// <c>GasProperties</c> definition, which is why it needs a key form of its own rather than
    /// riding on the assembler-built bottles that used to stand in for it.
    /// </summary>
    public class GasInventoryKeyTests
    {
        [Fact]
        public void GasKeysClassifyAsTheirOwnItemKind()
        {
            ItemKind kind;

            Assert.True(NullItemRegistry.Instance.TryGetItemKind("Gas:Hydrogen", out kind));
            Assert.Equal(ItemKind.Hydrogen, kind);

            Assert.True(NullItemRegistry.Instance.TryGetItemKind("Gas:Oxygen", out kind));
            Assert.Equal(ItemKind.Oxygen, kind);
        }

        [Fact]
        public void AGasKeySumsIntoItsKindOnANode()
        {
            // The property that matters downstream: a node holding gas reads back as that ItemKind,
            // so install costs, afford checks and trade cargo can see it at all. Before FAC-038
            // ItemKind.Hydrogen had no member the host could ever produce.
            var world = new WorldModel();
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 0.8, "NA");
            node.OwnerFactionTag = "AAA";
            node.Inventory["Gas:Hydrogen"] = 500;
            node.Inventory["Gas:Oxygen"]   = 250;

            Assert.Equal(500L, world.GetInventory("AAA", ItemKind.Hydrogen));
            Assert.Equal(250L, world.GetInventory("AAA", ItemKind.Oxygen));
        }

        [Fact]
        public void AGasKeyIsNotStrippedBeforeTheRegistrySees_It()
        {
            // "Gas:" is retained exactly like "Ingot:": the bare subtype "Hydrogen" would collide
            // with nothing today, but the prefix is what says this is litres of gas rather than an
            // item that happens to share the name.
            Assert.Equal("MyObjectBuilder_GasProperties/Hydrogen",
                ItemPriceResolver.ExactKeyFromInventoryKey("Gas:Hydrogen"));
            Assert.Equal("MyObjectBuilder_GasProperties/Oxygen",
                ItemPriceResolver.ExactKeyFromInventoryKey("Gas:Oxygen"));
            Assert.Equal("Hydrogen", ItemPriceResolver.StripInventoryPrefix("Gas:Hydrogen"));
        }

        [Fact]
        public void AFuelDepotHasARunnableMachineModel()
        {
            // Before FAC-038 this was speed 0 / count 0, which TickOneBank skips outright — so even
            // a resolved recipe list could never have run.
            MachineProfile profile = MachinePhysics.DefaultProfile(NodeCapabilityKind.FuelDepot);
            Assert.True(profile.BaseSpeed > 0, "a FuelDepot bank with zero speed never ticks");
            Assert.True(profile.MachineCount > 0, "a FuelDepot bank with zero machines never ticks");

            MachineModel model = MachinePhysics.Resolve(profile, 0.0);
            // The generator carries no speed tag of its own: its whole rate is the recipe's
            // IceConsumptionPerSecond, so the machine multiplier must stay neutral.
            Assert.Equal(1.0, model.Speed, 6);
            Assert.Equal(1.0, model.OutputScale, 6);
            Assert.Equal(1.0, model.InputDivisor, 6);
        }
    }
}
