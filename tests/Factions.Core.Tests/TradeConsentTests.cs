using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers <see cref="TradeConsent"/>, the one seller-consent rule the trade matcher and
    /// the dock share. FAC-109.</summary>
    public class TradeConsentTests
    {
        private static WorldModel World(out SpatialNode node)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "A1");
            node.OwnerFactionTag = "A";
            node.Inventory["Ice"] = 1000;
            return world;
        }

        [Fact]
        public void WithAReference_ItSellsOnlyWhenOversupplied()
        {
            SpatialNode node;
            WorldModel world = World(out node);
            FactionTuning tuning = TestSupport.Tuning();

            Assert.True(TradeConsent.SellerConsents(new TradeTest.NodeScarcityOracle().Set("A1", "Ice", 0.5),
                world, "A", node, "Ice", tuning));
            Assert.False(TradeConsent.SellerConsents(new TradeTest.NodeScarcityOracle().Set("A1", "Ice", 0.8),
                world, "A", node, "Ice", tuning));
        }

        [Fact]
        public void WithoutAReference_ItSellsWhatItHasNoUseFor()
        {
            SpatialNode node;
            WorldModel world = World(out node);
            Assert.True(TradeConsent.SellerConsents(new TradeTest.NodeScarcityOracle(), world, "A", node, "Ice",
                TestSupport.Tuning()));
        }

        [Fact]
        public void WithoutAReference_ItKeepsWhatItAskedForItself()
        {
            SpatialNode node;
            WorldModel world = World(out node);
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "A", Kind = "Item:Ice", NodeId = "A1", Quantity = 10, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            Assert.False(TradeConsent.SellerConsents(new TradeTest.NodeScarcityOracle(), world, "A", node, "Ice",
                TestSupport.Tuning()));
        }

        /// <summary>Unused here is not unused by the faction: a node's ore is its own refinery's
        /// input elsewhere, and the owner is short of it there, so it stays home.</summary>
        [Fact]
        public void WithoutAReference_ItKeepsWhatItsFactionIsShortOfElsewhere()
        {
            SpatialNode node;
            WorldModel world = World(out node);
            SpatialNode other = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "A2");
            other.OwnerFactionTag = "A";
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "A", Kind = "Item:Ice", NodeId = "A2", Quantity = 500, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            Assert.False(TradeConsent.SellerConsents(new TradeTest.NodeScarcityOracle(), world, "A", node, "Ice",
                TestSupport.Tuning()));

            other.Inventory["Ice"] = 500;   // the want is met: nothing is short any more
            Assert.True(TradeConsent.SellerConsents(new TradeTest.NodeScarcityOracle(), world, "A", node, "Ice",
                TestSupport.Tuning()));
        }

        [Fact]
        public void TheSwitchOff_AndNoOracle_BothRefuse()
        {
            SpatialNode node;
            WorldModel world = World(out node);
            FactionTuning off = TestSupport.Tuning();
            off.TradeSellUnusedStock = false;
            Assert.False(TradeConsent.SellerConsents(new TradeTest.NodeScarcityOracle(), world, "A", node, "Ice", off));
            Assert.False(TradeConsent.SellerConsents(NullPriceOracle.Instance, world, "A", node, "Ice",
                TestSupport.Tuning()));
        }
    }
}
