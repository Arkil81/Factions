using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class FactionNeedShareTests
    {
        private static SpatialNode Node(string id, params NodeCapabilityKind[] caps)
        {
            var node = new SpatialNode { Id = id };
            foreach (NodeCapabilityKind cap in caps) node.Capabilities[cap] = 1;
            return node;
        }

        [Fact]
        public void ANeedNobodyHolds_SplitsEvenlyAcrossHolders()
        {
            Assert.Equal(25, FactionNeedShare.For(shared: 100, hereStock: 0, factionStock: 0, holders: 4));
        }

        [Fact]
        public void ASingleHolder_OwesTheWholeNeed()
        {
            Assert.Equal(100, FactionNeedShare.For(shared: 100, hereStock: 0, factionStock: 0, holders: 1));
        }

        [Fact]
        public void TheNodeHoldingAllOfIt_OwesAllOfIt()
        {
            // The reason an even split alone is wrong: the need spreads while the stock concentrates,
            // so a flat quarter would leave three quarters of what this node holds reading as surplus.
            Assert.Equal(100, FactionNeedShare.For(shared: 100, hereStock: 800, factionStock: 800, holders: 4));
        }

        [Fact]
        public void ANodeHoldingLessThanItsEvenShare_StillKeepsTheEvenShare()
        {
            // The max, not the proportion: a node holding a tenth of the stock across four holders
            // reads 10 by proportion and 25 by split. Taking the smaller would remove its reason to
            // accumulate the rest.
            Assert.Equal(25, FactionNeedShare.For(shared: 100, hereStock: 80, factionStock: 800, holders: 4));
        }

        [Fact]
        public void ProportionBeatsTheEvenSplit_OnceThisNodeHoldsMostOfTheStock()
        {
            Assert.Equal(75, FactionNeedShare.For(shared: 100, hereStock: 600, factionStock: 800, holders: 4));
        }

        [Fact]
        public void NothingIsShared_IsNotAClaim()
        {
            Assert.Equal(0, FactionNeedShare.For(shared: 0, hereStock: 500, factionStock: 500, holders: 2));
            Assert.Equal(0, FactionNeedShare.For(shared: -5, hereStock: 500, factionStock: 500, holders: 2));
        }

        [Fact]
        public void HolderCount_CountsOnlyNodesWithAnInstalledCapability()
        {
            var nodes = new List<SpatialNode>
            {
                Node("A", NodeCapabilityKind.Refinery),
                Node("B", NodeCapabilityKind.Manufactory, NodeCapabilityKind.Refinery),
                Node("C"),
            };
            Assert.Equal(2, FactionNeedShare.HolderCount(nodes));
        }

        [Fact]
        public void HolderCount_OfAFactionWithNoIndustry_IsOneRatherThanZero()
        {
            // A faction with a live need and no industry still has to gather it somewhere; a zero
            // here would divide the need away entirely.
            Assert.Equal(1, FactionNeedShare.HolderCount(new List<SpatialNode> { Node("A") }));
            Assert.Equal(1, FactionNeedShare.HolderCount(new List<SpatialNode>()));
            Assert.Equal(1, FactionNeedShare.HolderCount(null));
        }

        [Fact]
        public void ACapabilityAtLevelZero_IsNotAnInstalledCapability()
        {
            var node = new SpatialNode { Id = "A" };
            node.Capabilities[NodeCapabilityKind.Refinery] = 0;
            Assert.False(FactionNeedShare.HasAnyCapability(node));
            Assert.Equal(1, FactionNeedShare.HolderCount(new List<SpatialNode> { node }));
        }
    }
}
