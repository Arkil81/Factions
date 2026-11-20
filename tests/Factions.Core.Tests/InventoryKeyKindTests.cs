using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Inventory keys carry an "Ore:"/"Comp:" prefix the item registry does not key on, so
    /// every consumer that classifies a held item goes through
    /// <see cref="WorldModel.TryGetInventoryItemKind"/>.</summary>
    public class InventoryKeyKindTests
    {
        [Fact]
        public void ATradeBasket_LoadsPrefixedStock_AtItsKindsFallbackPrice()
        {
            WorldModel world = TestSupport.World();
            FactionTuning tuning = world.Tuning;
            tuning.TradeCargoOre = 1000;
            tuning.TradeCargoProcessedGood = 1000;
            tuning.FallbackOreValue = 7;
            tuning.FallbackProcessedGoodValue = 11;
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N");
            node.OwnerFactionTag = "F";
            node.Inventory["Ore:Ice"] = 500;
            node.Inventory["Comp:SteelPlate"] = 50;
            var fleet = new PendingFleetAction { Kind = ActionKind.Trade, ActorTag = "F" };

            PendingFleetResolver.LoadTradeCargo(node, ref fleet, null, node.Position, world, tuning,
                itemVolumes: null, itemMasses: null, oracle: null, prices: null, preferredItemKind: null);

            Assert.NotNull(fleet.Cargo);
            Assert.Equal(500L, fleet.Cargo["Ore:Ice"]);
            Assert.Equal(50L, fleet.Cargo["Comp:SteelPlate"]);
            long expected = ItemCredits.For("Ore:Ice", 500, tuning.FallbackFor(ItemKind.Ore))
                          + ItemCredits.For("Comp:SteelPlate", 50, tuning.FallbackFor(ItemKind.ProcessedGood));
            Assert.Equal(expected, fleet.CargoValue);
        }
        [Fact]
        public void RaidLoot_ValuesUnpricedPrefixedStock_AtItsKindsFallbackPrice()
        {
            WorldModel world = TestSupport.World();
            FactionTuning tuning = world.Tuning;
            tuning.FallbackOreValue = 7; // not 1, which is also the value of an item of unknown kind
            SpatialNode home = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Home");
            home.OwnerFactionTag = "R";
            SpatialNode target = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "Target");
            target.OwnerFactionTag = "V";
            target.Inventory["Ore:Ice"] = 400;

            long value = world.RaidLootNodeInventory("Home", "Target", 0.5, 1000, prices: null, itemVolumes: null);

            long taken;
            home.Inventory.TryGetValue("Ore:Ice", out taken);
            Assert.True(taken > 0, "the raid took nothing, so the value below is not tested");
            Assert.Equal(ItemCredits.For("Ore:Ice", taken, tuning.FallbackFor(ItemKind.Ore)), value);
        }
    }
}
