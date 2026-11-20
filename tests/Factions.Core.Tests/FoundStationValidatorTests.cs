using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class FoundStationValidatorTests
    {
        private static SpatialNode AddClaim(WorldModel world, string id, string tag)
        {
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            node.OwnerFactionTag = tag;
            node.HoldStrength = 1.0;
            return node;   // StableStationId left 0: claimed, not yet founded
        }

        [Fact]
        public void Validate_ReturnsOk_AndQuotesFoundCost_WhenTheClaimIsOwnedAndAffordable()
        {
            WorldModel world = TestSupport.World();
            world.Tuning.FoundCost = 100_000;
            AddClaim(world, "N1", "F");
            world.SetTreasury("F", 137_500);

            long cost, have;
            FoundStationResult result = FoundStationValidator.Validate(world, world.Tuning, "F", "N1", out cost, out have);

            Assert.Equal(FoundStationResult.Ok, result);
            Assert.Equal(100_000, cost);
            Assert.Equal(137_500, have);
        }

        [Fact]
        public void Validate_ReturnsInsufficientTreasury_OneCreditShort()
        {
            // One credit, not an order of magnitude: the boundary is what a price gate is, and a
            // fixture far below it would pass against an off-by-one comparison too.
            WorldModel world = TestSupport.World();
            world.Tuning.FoundCost = 100_000;
            AddClaim(world, "N1", "F");
            world.SetTreasury("F", 99_999);

            long cost, have;
            FoundStationResult result = FoundStationValidator.Validate(world, world.Tuning, "F", "N1", out cost, out have);

            Assert.Equal(FoundStationResult.InsufficientTreasury, result);
            Assert.Equal(100_000, cost);
            Assert.Equal(99_999, have);
        }

        [Fact]
        public void Validate_ReturnsOk_ExactlyAtThePrice()
        {
            WorldModel world = TestSupport.World();
            world.Tuning.FoundCost = 100_000;
            AddClaim(world, "N1", "F");
            world.SetTreasury("F", 100_000);

            long cost, have;
            Assert.Equal(FoundStationResult.Ok,
                FoundStationValidator.Validate(world, world.Tuning, "F", "N1", out cost, out have));
        }

        [Fact]
        public void Validate_IgnoresOreEntirely()
        {
            // The gate this replaced read the node's ore. A claim with nothing under it founds on
            // money alone, and a claim buried in ore still cannot found without it.
            WorldModel world = TestSupport.World();
            world.Tuning.FoundCost = 100_000;

            SpatialNode barren = AddClaim(world, "N1", "F");
            barren.Inventory.Clear();
            world.SetTreasury("F", 100_000);

            long cost, have;
            Assert.Equal(FoundStationResult.Ok,
                FoundStationValidator.Validate(world, world.Tuning, "F", "N1", out cost, out have));

            SpatialNode rich = AddClaim(world, "N2", "G");
            rich.Inventory["Ore:Iron"] = 9_000_000;
            world.SetTreasury("G", 0);

            Assert.Equal(FoundStationResult.InsufficientTreasury,
                FoundStationValidator.Validate(world, world.Tuning, "G", "N2", out cost, out have));
        }

        [Fact]
        public void Validate_ReturnsAlreadyFounded_WhenTheNodeCarriesAStation()
        {
            WorldModel world = TestSupport.World();
            world.Tuning.FoundCost = 100_000;
            SpatialNode node = AddClaim(world, "N1", "F");
            node.StableStationId = 42;
            world.SetTreasury("F", 5_000_000);

            long cost, have;
            Assert.Equal(FoundStationResult.AlreadyFounded,
                FoundStationValidator.Validate(world, world.Tuning, "F", "N1", out cost, out have));
        }

        [Fact]
        public void Validate_ReturnsNotOwned_ForAnotherFactionsClaim()
        {
            WorldModel world = TestSupport.World();
            world.Tuning.FoundCost = 100_000;
            AddClaim(world, "N1", "OTHER");
            world.SetTreasury("F", 5_000_000);

            long cost, have;
            Assert.Equal(FoundStationResult.NotOwned,
                FoundStationValidator.Validate(world, world.Tuning, "F", "N1", out cost, out have));
        }

        [Fact]
        public void Validate_ReturnsNodeNotFound_WhenTheNodeIdIsUnknown()
        {
            WorldModel world = TestSupport.World();

            long cost, have;
            Assert.Equal(FoundStationResult.NodeNotFound,
                FoundStationValidator.Validate(world, world.Tuning, "F", "N404", out cost, out have));
        }
    }
}
