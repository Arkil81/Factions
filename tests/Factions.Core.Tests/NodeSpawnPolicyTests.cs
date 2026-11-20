using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class NodeSpawnPolicyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        private static SpatialNode Founded(WorldModel world, string id, string tag, long stationId, string name = null)
        {
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            node.OwnerFactionTag = tag;
            node.StableStationId = stationId;
            node.DisplayName = name;
            return node;
        }

        [Fact]
        public void Check_IsNone_ForAnOwnFoundedNodeWithItsZoneUp()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = Founded(world, "N1", "PLR", 11);

            Assert.Equal(NodeSpawnBlock.None, NodeSpawnPolicy.Check(node, "PLR", Now, default(DateTime)));
        }

        [Fact]
        public void Check_RefusesAClaimWithNoStation()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = Founded(world, "N1", "PLR", 0);

            Assert.Equal(NodeSpawnBlock.NoStation, NodeSpawnPolicy.Check(node, "PLR", Now, default(DateTime)));
        }

        [Fact]
        public void Check_RefusesAnotherFactionsNode_AndAFactionlessSpawner()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = Founded(world, "N1", "PLR", 11);

            Assert.Equal(NodeSpawnBlock.NotYours, NodeSpawnPolicy.Check(node, "OTH", Now, default(DateTime)));
            Assert.Equal(NodeSpawnBlock.NotYours, NodeSpawnPolicy.Check(node, null, Now, default(DateTime)));
            // Tags are ordinal everywhere the graph compares them; a case-folded match is someone else.
            Assert.Equal(NodeSpawnBlock.NotYours, NodeSpawnPolicy.Check(node, "plr", Now, default(DateTime)));
        }

        [Fact]
        public void Check_RefusesWhileARaidKnockdownHoldsTheZoneDown_AndAdmitsOnceItLapses()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = Founded(world, "N1", "PLR", 11);
            node.SafeZoneDownUntilUtc = Now.AddSeconds(1);

            Assert.Equal(NodeSpawnBlock.ZoneDown, NodeSpawnPolicy.Check(node, "PLR", Now, default(DateTime)));
            Assert.Equal(NodeSpawnBlock.None, NodeSpawnPolicy.Check(node, "PLR", Now.AddSeconds(1), default(DateTime)));
        }

        [Fact]
        public void Check_RefusesOnceASiegeHasBreachedTheZone()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = Founded(world, "N1", "PLR", 11);
            node.ZoneBreachedUtc = Now.AddMinutes(-1);

            Assert.Equal(NodeSpawnBlock.ZoneDown, NodeSpawnPolicy.Check(node, "PLR", Now, default(DateTime)));
        }

        [Fact]
        public void Check_RefusesInsideTheCooldown_ToTheSecond()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = Founded(world, "N1", "PLR", 11);
            DateTime last = Now.AddSeconds(-(NodeSpawnPolicy.CooldownSeconds - 1));

            Assert.Equal(NodeSpawnBlock.Cooldown, NodeSpawnPolicy.Check(node, "PLR", Now, last));
            Assert.Equal(1, NodeSpawnPolicy.CooldownRemainingSeconds(Now, last));
            Assert.Equal(NodeSpawnBlock.None, NodeSpawnPolicy.Check(node, "PLR", Now.AddSeconds(1), last));
        }

        [Fact]
        public void CooldownRemaining_RoundsUpAPartialSecond()
        {
            DateTime last = Now.AddSeconds(-(NodeSpawnPolicy.CooldownSeconds - 0.2));
            Assert.Equal(1, NodeSpawnPolicy.CooldownRemainingSeconds(Now, last));
        }

        [Fact]
        public void Check_ReportsZoneDownBeforeCooldown()
        {
            // A zone the player can do nothing about outranks a wait that ends on its own.
            WorldModel world = TestSupport.World();
            SpatialNode node = Founded(world, "N1", "PLR", 11);
            node.SafeZoneDownUntilUtc = Now.AddMinutes(5);

            Assert.Equal(NodeSpawnBlock.ZoneDown, NodeSpawnPolicy.Check(node, "PLR", Now, Now.AddSeconds(-10)));
        }

        [Fact]
        public void Options_ListsOnlyTheSpawnersFoundedNodes_ByName_WithEachBlock()
        {
            WorldModel world = TestSupport.World();
            Founded(world, "N1", "PLR", 11, "Zeta Yard");
            SpatialNode down = Founded(world, "N2", "PLR", 12, "Alpha Post");
            down.SafeZoneDownUntilUtc = Now.AddMinutes(1);
            Founded(world, "N3", "PLR", 0, "Unfounded Claim");
            Founded(world, "N4", "OTH", 14, "Their Station");

            List<NodeSpawnOption> options = NodeSpawnPolicy.Options(world.Graph, "PLR", Now, default(DateTime));

            Assert.Equal(new[] { "N2", "N1" }, options.ConvertAll(o => o.NodeId).ToArray());
            Assert.Equal(NodeSpawnBlock.ZoneDown, options[0].Block);
            Assert.Equal(NodeSpawnBlock.None, options[1].Block);
            Assert.Equal(11, options[1].StableStationId);
        }

        [Fact]
        public void Options_CarriesTheCooldownOnlyOnRowsItBlocks()
        {
            WorldModel world = TestSupport.World();
            Founded(world, "N1", "PLR", 11, "Open");
            SpatialNode down = Founded(world, "N2", "PLR", 12, "Down");
            down.SafeZoneDownUntilUtc = Now.AddMinutes(1);
            DateTime last = Now.AddSeconds(-100);

            List<NodeSpawnOption> options = NodeSpawnPolicy.Options(world.Graph, "PLR", Now, last);
            NodeSpawnOption open = options.Find(o => o.NodeId == "N1");
            NodeSpawnOption zoneDown = options.Find(o => o.NodeId == "N2");

            Assert.Equal(NodeSpawnBlock.Cooldown, open.Block);
            Assert.Equal(NodeSpawnPolicy.CooldownSeconds - 100, open.CooldownSeconds);
            Assert.Equal(NodeSpawnBlock.ZoneDown, zoneDown.Block);
            Assert.Equal(0, zoneDown.CooldownSeconds);
        }

        [Fact]
        public void Options_IsEmptyForAFactionlessSpawner()
        {
            WorldModel world = TestSupport.World();
            Founded(world, "N1", "PLR", 11);

            Assert.Empty(NodeSpawnPolicy.Options(world.Graph, null, Now, default(DateTime)));
        }

        [Fact]
        public void FindByStation_ResolvesTheNodeCarryingTheStation()
        {
            WorldModel world = TestSupport.World();
            Founded(world, "N1", "PLR", 11);
            Founded(world, "N2", "PLR", 12);

            Assert.Equal("N2", NodeSpawnPolicy.FindByStation(world.Graph, 12).Id);
            Assert.Null(NodeSpawnPolicy.FindByStation(world.Graph, 99));
            Assert.Null(NodeSpawnPolicy.FindByStation(world.Graph, 0));
        }
    }
}
