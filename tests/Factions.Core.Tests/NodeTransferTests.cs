using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The arithmetic behind the Withdraw/Deposit hand on a node a player's own faction holds: what
    /// may leave, what is held back, and that the two mutations move exactly what they say.
    ///
    /// <para>Everything here runs against an explicit clock. <see cref="SpatialNode.BuildQueueReserved"/>
    /// takes <c>nowUtc</c> and compares it against each entry's expiry, so a test reading the wall
    /// clock would change verdict as its fixtures aged past it.</para>
    /// </summary>
    public class NodeTransferTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

        private const string Key = "Comp:SteelPlate";

        private static SpatialNode Node(long held)
        {
            var node = new SpatialNode { Id = "N1", OwnerFactionTag = "PLR" };
            if (held > 0) node.Inventory[Key] = held;
            return node;
        }

        /// <summary>One queue entry wanting <paramref name="bom"/> of the key and already holding
        /// <paramref name="collected"/> of it, alive for another day.</summary>
        private static BuildQueueEntry Build(long bom, long collected = 0, int hoursToExpiry = 24)
        {
            var entry = new BuildQueueEntry
            {
                Id = "B" + bom + "-" + collected,
                Role = "Raid",
                ExpiresUtc = T0.AddHours(hoursToExpiry),
            };
            entry.Bom[Key] = bom;
            if (collected > 0) entry.Collected[Key] = collected;
            return entry;
        }

        // ---- Withdrawable ---------------------------------------------------------------------

        [Fact]
        public void Withdrawable_IsTheWholeStock_WhenNothingCompetesForIt()
        {
            Assert.Equal(200, NodeTransfer.Withdrawable(Node(200), Key, T0));
        }

        [Fact]
        public void Withdrawable_ExcludesAnActiveBuildsOutstandingShortfall()
        {
            SpatialNode node = Node(200);
            node.BuildQueue.Add(Build(bom: 150, collected: 50));   // 100 still to absorb
            Assert.Equal(100, NodeTransfer.Withdrawable(node, Key, T0));
        }

        [Fact]
        public void Withdrawable_FloorsAtZero_WhenTheQueueWantsMoreThanTheNodeHolds()
        {
            SpatialNode node = Node(40);
            node.BuildQueue.Add(Build(bom: 500));
            Assert.Equal(0, NodeTransfer.Withdrawable(node, Key, T0));
        }

        [Fact]
        public void Withdrawable_IgnoresABuildWaitingForASlot()
        {
            SpatialNode node = Node(200);
            node.BuildQueue.Add(Build(bom: 60));    // occupies the single build slot
            node.BuildQueue.Add(Build(bom: 90));    // waiting: absorbs nothing, reserves nothing
            Assert.Equal(140, NodeTransfer.Withdrawable(node, Key, T0));
        }

        [Fact]
        public void Withdrawable_IgnoresAnExpiredBuild()
        {
            SpatialNode node = Node(200);
            node.BuildQueue.Add(Build(bom: 150, collected: 0, hoursToExpiry: -1));
            Assert.Equal(200, NodeTransfer.Withdrawable(node, Key, T0));
        }

        [Fact]
        public void Withdrawable_IgnoresEveryBuild_AtAPausedYard()
        {
            SpatialNode node = Node(200);
            node.BuildQueue.Add(Build(bom: 150));
            node.ShipyardPaused = true;
            Assert.Equal(200, NodeTransfer.Withdrawable(node, Key, T0));
        }

        /// <summary>Install-reserved stock is already OUT of <c>Inventory</c> — the two dictionaries
        /// are disjoint. Subtracting it here as well would withhold a second copy of it that does not
        /// exist, and hide routable stock nothing has claimed.</summary>
        [Fact]
        public void Withdrawable_DoesNotSubtractInstallReservedStock_WhichIsAlreadyOutOfInventory()
        {
            SpatialNode node = Node(200);
            node.ReservedInventory[Key] = 500;
            Assert.Equal(200, NodeTransfer.Withdrawable(node, Key, T0));
        }

        [Fact]
        public void Withdrawable_IsZero_ForAKeyTheNodeDoesNotHold()
        {
            Assert.Equal(0, NodeTransfer.Withdrawable(Node(200), "Ore:Iron", T0));
        }

        // ---- Reserved -------------------------------------------------------------------------

        [Fact]
        public void Reserved_SumsTheQueueClaimAndTheStagedInstallStock()
        {
            SpatialNode node = Node(200);
            node.BuildQueue.Add(Build(bom: 150, collected: 50));   // 100 outstanding
            node.ReservedInventory[Key] = 25;
            Assert.Equal(125, NodeTransfer.Reserved(node, Key, T0));
        }

        /// <summary>A build short of stock the node does not have is short, not holding. Reporting
        /// the whole shortfall would put units on the listing that are not standing anywhere.</summary>
        [Fact]
        public void Reserved_ClampsTheQueueClaimToWhatTheNodeActuallyHolds()
        {
            SpatialNode node = Node(40);
            node.BuildQueue.Add(Build(bom: 500));
            Assert.Equal(40, NodeTransfer.Reserved(node, Key, T0));
        }

        /// <summary>Available and Reserved together account for everything the node holds under this
        /// key, routable and staged. A client showing both must not be able to make stock appear or
        /// vanish by adding them up.</summary>
        [Fact]
        public void AvailablePlusReserved_AccountsForEveryUnitTheNodeHolds()
        {
            SpatialNode node = Node(200);
            node.BuildQueue.Add(Build(bom: 150, collected: 50));
            node.ReservedInventory[Key] = 25;
            Assert.Equal(200 + 25,
                NodeTransfer.Withdrawable(node, Key, T0) + NodeTransfer.Reserved(node, Key, T0));
        }

        // ---- IsDepositable --------------------------------------------------------------------

        [Theory]
        [InlineData("Ore:Iron")]
        [InlineData("Ingot:Cobalt")]
        [InlineData("Comp:SteelPlate")]
        public void IsDepositable_AcceptsTheThreeFamiliesTheEconomyConsumes(string key)
        {
            Assert.True(NodeTransfer.IsDepositable(key));
        }

        [Theory]
        [InlineData("Gas:Hydrogen")]
        [InlineData("NATO_25x184mm")]
        [InlineData("Datapad")]
        [InlineData("")]
        [InlineData(null)]
        public void IsDepositable_RefusesAnythingThatWouldEnterStockAsADeadChannel(string key)
        {
            Assert.False(NodeTransfer.IsDepositable(key));
        }

        // ---- TakeFromNode ---------------------------------------------------------------------

        [Fact]
        public void TakeFromNode_MovesWhatWasAskedFor_AndLeavesTheRemainder()
        {
            SpatialNode node = Node(200);
            Assert.Equal(75, NodeTransfer.TakeFromNode(node, Key, 75, T0));
            Assert.Equal(125, node.Inventory[Key]);
        }

        /// <summary>Acceptance check 1's paired number: the node falls by exactly what moved, and the
        /// build's own collected stock is untouched by a withdrawal beside it.</summary>
        [Fact]
        public void TakeFromNode_ClampsToTheReserve_AndDoesNotTouchWhatABuildHasCollected()
        {
            SpatialNode node = Node(200);
            BuildQueueEntry entry = Build(bom: 150, collected: 50);
            node.BuildQueue.Add(entry);

            Assert.Equal(100, NodeTransfer.TakeFromNode(node, Key, 999, T0));
            Assert.Equal(100, node.Inventory[Key]);
            Assert.Equal(50, entry.Collected[Key]);
        }

        [Fact]
        public void TakeFromNode_DropsTheKeyOnceTheStockIsGone()
        {
            SpatialNode node = Node(60);
            Assert.Equal(60, NodeTransfer.TakeFromNode(node, Key, 60, T0));
            Assert.False(node.Inventory.ContainsKey(Key));
        }

        [Fact]
        public void TakeFromNode_MovesNothing_WhenTheWholeStockIsReserved()
        {
            SpatialNode node = Node(100);
            node.BuildQueue.Add(Build(bom: 100));
            Assert.Equal(0, NodeTransfer.TakeFromNode(node, Key, 100, T0));
            Assert.Equal(100, node.Inventory[Key]);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(-5L)]
        public void TakeFromNode_MovesNothing_ForANonPositiveAmount(long amount)
        {
            SpatialNode node = Node(100);
            Assert.Equal(0, NodeTransfer.TakeFromNode(node, Key, amount, T0));
            Assert.Equal(100, node.Inventory[Key]);
        }

        // ---- CreditNode -----------------------------------------------------------------------

        [Fact]
        public void CreditNode_AddsToStockThatIsAlreadyThere()
        {
            SpatialNode node = Node(40);
            NodeTransfer.CreditNode(node, Key, 60);
            Assert.Equal(100, node.Inventory[Key]);
        }

        [Fact]
        public void CreditNode_OpensAKeyTheNodeDidNotHold()
        {
            SpatialNode node = Node(0);
            NodeTransfer.CreditNode(node, "Ore:Iron", 30);
            Assert.Equal(30, node.Inventory["Ore:Iron"]);
        }

        [Fact]
        public void CreditNode_IsWithdrawableAgainImmediately_WhenNothingCompetesForIt()
        {
            SpatialNode node = Node(0);
            NodeTransfer.CreditNode(node, Key, 30);
            Assert.Equal(30, NodeTransfer.Withdrawable(node, Key, T0));
        }
    }
}
