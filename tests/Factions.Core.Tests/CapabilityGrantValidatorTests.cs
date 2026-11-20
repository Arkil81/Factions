using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The admin form of `develop` deliberately keeps only the structural checks
    /// <see cref="DevelopStationValidator"/> shares with it (held by a real faction, founded) and
    /// drops the prerequisite cascade, the treasury cost and the component BOM — these pin that
    /// divergence so a later tightening of one validator can't quietly be applied to the other.</summary>
    public class CapabilityGrantValidatorTests
    {
        private static SpatialNode AddOwnedFoundedNode(WorldModel world, string id, string tag)
        {
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            node.OwnerFactionTag = tag;
            node.HoldStrength = 1.0;
            node.StableStationId = 1; // any non-zero value marks the node as founded
            return node;
        }

        private static CapabilityGrantResult Validate(WorldModel world, string nodeId,
            NodeCapabilityKind cap, int level, out string owner, out int current, out int granted, out int max)
        {
            return CapabilityGrantValidator.Validate(world, world.Tuning, nodeId, cap, level,
                out owner, out current, out granted, out max);
        }

        [Fact]
        public void Validate_GrantsRequestedLevel_OnOwnedFoundedNode()
        {
            WorldModel world = TestSupport.World();
            AddOwnedFoundedNode(world, "N1", "F");

            string owner; int current, granted, max;
            CapabilityGrantResult result = Validate(world, "N1", NodeCapabilityKind.Refinery, 1,
                out owner, out current, out granted, out max);

            Assert.Equal(CapabilityGrantResult.Ok, result);
            Assert.Equal(0, current);
            Assert.Equal(1, granted);
        }

        [Fact]
        public void Validate_ChargesNothing_WithEmptyTreasuryAndNoStock()
        {
            WorldModel world = TestSupport.World();
            AddOwnedFoundedNode(world, "N1", "F");
            world.SetTreasury("F", 0);

            string owner; int current, granted, max;
            Assert.Equal(CapabilityGrantResult.Ok,
                Validate(world, "N1", NodeCapabilityKind.Refinery, 1, out owner, out current, out granted, out max));
        }

        [Fact]
        public void Validate_IgnoresPrerequisiteCascade()
        {
            WorldModel world = TestSupport.World();
            AddOwnedFoundedNode(world, "N1", "F");

            // Shipyard requires Manufactory installed somewhere in the network; nothing is installed.
            string owner; int current, granted, max;
            Assert.Equal(CapabilityGrantResult.Ok,
                Validate(world, "N1", NodeCapabilityKind.Shipyard, 1, out owner, out current, out granted, out max));
            Assert.Equal(1, granted);
        }

        [Fact]
        public void Validate_ClampsAboveTheCap()
        {
            WorldModel world = TestSupport.World();
            AddOwnedFoundedNode(world, "N1", "F");
            int cap = world.Tuning.MaxLevelFor(NodeCapabilityKind.Refinery);

            string owner; int current, granted, max;
            Assert.Equal(CapabilityGrantResult.Ok,
                Validate(world, "N1", NodeCapabilityKind.Refinery, cap + 5, out owner, out current, out granted, out max));
            Assert.Equal(cap, granted);
            Assert.Equal(cap, max);
        }

        [Fact]
        public void Validate_ClampsBelowZero()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;

            string owner; int current, granted, max;
            Assert.Equal(CapabilityGrantResult.Ok,
                Validate(world, "N1", NodeCapabilityKind.Refinery, -3, out owner, out current, out granted, out max));
            Assert.Equal(1, current);
            Assert.Equal(0, granted);
        }

        [Fact]
        public void Validate_ReturnsNodeNotFound_WhenNodeIdUnknown()
        {
            WorldModel world = TestSupport.World();

            string owner; int current, granted, max;
            Assert.Equal(CapabilityGrantResult.NodeNotFound,
                Validate(world, "N404", NodeCapabilityKind.Refinery, 1, out owner, out current, out granted, out max));
        }

        [Fact]
        public void Validate_ReportsTheNodesOwner_NotACallerFaction()
        {
            WorldModel world = TestSupport.World();
            AddOwnedFoundedNode(world, "N1", "RIVAL");

            string owner; int current, granted, max;
            Assert.Equal(CapabilityGrantResult.Ok,
                Validate(world, "N1", NodeCapabilityKind.Refinery, 1, out owner, out current, out granted, out max));
            Assert.Equal("RIVAL", owner);
        }

        [Fact]
        public void Validate_ReturnsUnowned_ForADerelictNode()
        {
            WorldModel world = TestSupport.World();
            AddOwnedFoundedNode(world, "N1", NeutralFaction.Tag);

            string owner; int current, granted, max;
            Assert.Equal(CapabilityGrantResult.Unowned,
                Validate(world, "N1", NodeCapabilityKind.Refinery, 1, out owner, out current, out granted, out max));
        }

        [Fact]
        public void Validate_ReturnsUnowned_WhenNoFactionHoldsTheNode()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.OwnerFactionTag = null;

            string owner; int current, granted, max;
            Assert.Equal(CapabilityGrantResult.Unowned,
                Validate(world, "N1", NodeCapabilityKind.Refinery, 1, out owner, out current, out granted, out max));
        }

        [Fact]
        public void Validate_ReturnsNotFounded_WhenNodeHasNoStation()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedFoundedNode(world, "N1", "F");
            node.StableStationId = 0;

            string owner; int current, granted, max;
            Assert.Equal(CapabilityGrantResult.NotFounded,
                Validate(world, "N1", NodeCapabilityKind.Refinery, 1, out owner, out current, out granted, out max));
        }
    }

    /// <summary>The grant form parses against the enum, not
    /// <see cref="FactionTuning.AllCapabilities"/> — that array is the brain's candidate set and is
    /// one entry short of the enum, so parsing through it would put SensorArray out of reach of the
    /// operator's own lever.</summary>
    public class CapabilityTokensTests
    {
        [Theory]
        [InlineData("Refinery", NodeCapabilityKind.Refinery)]
        [InlineData("refinery", NodeCapabilityKind.Refinery)]
        [InlineData("REFINERYII", NodeCapabilityKind.RefineryII)]
        [InlineData("SensorArray", NodeCapabilityKind.SensorArray)]
        [InlineData("PrototechForge", NodeCapabilityKind.PrototechForge)]
        [InlineData("prototechrefinery", NodeCapabilityKind.PrototechRefinery)]
        public void TryParse_AcceptsEveryDefinedName_CaseInsensitively(string token, NodeCapabilityKind expected)
        {
            NodeCapabilityKind cap;
            Assert.True(CapabilityTokens.TryParse(token, out cap));
            Assert.Equal(expected, cap);
        }

        [Theory]
        [InlineData("99")]
        [InlineData("-1")]
        // One past the highest ordinal, so this stays a boundary case as the enum grows.
        [InlineData("12")]
        public void TryParse_RejectsANumberOutsideTheEnum(string token)
        {
            // Enum.TryParse alone accepts any numeric string, which would write a capability key
            // nothing can name into the node's persisted map.
            NodeCapabilityKind cap;
            Assert.False(CapabilityTokens.TryParse(token, out cap));
        }

        [Theory]
        [InlineData("Foundry")]
        [InlineData("")]
        [InlineData(null)]
        public void TryParse_RejectsANonCapabilityToken(string token)
        {
            NodeCapabilityKind cap;
            Assert.False(CapabilityTokens.TryParse(token, out cap));
        }

        [Fact]
        public void Grantable_CoversEveryEnumValue_AndNeverLessThanTheBrainsOwnSet()
        {
            // Stated as a superset rather than as "AllCapabilities is short by one": that array
            // growing to match the enum is a fix, not a regression, and must not fail this.
            Assert.Equal(Enum.GetValues(typeof(NodeCapabilityKind)).Length, CapabilityTokens.Grantable.Length);
            foreach (NodeCapabilityKind cap in FactionTuning.AllCapabilities)
                Assert.Contains(cap, CapabilityTokens.Grantable);
        }

        [Fact]
        public void Names_ListsEveryGrantableCapability()
        {
            string names = CapabilityTokens.Names();
            foreach (NodeCapabilityKind cap in CapabilityTokens.Grantable)
                Assert.Contains(cap.ToString(), names);
        }
    }

    public class WorldModelSetCapabilityLevelTests
    {
        private static SpatialNode AddOwnedNode(WorldModel world, string id, string tag)
        {
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            node.OwnerFactionTag = tag;
            node.StableStationId = 1;
            return node;
        }

        [Fact]
        public void SetCapabilityLevel_WritesTheLevelOutright()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedNode(world, "N1", "F");

            world.SetCapabilityLevel("F", "N1", NodeCapabilityKind.Refinery, 3);

            Assert.Equal(3, node.Capabilities[NodeCapabilityKind.Refinery]);
        }

        [Fact]
        public void SetCapabilityLevel_LowersAnExistingLevel()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedNode(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Refinery] = 3;

            world.SetCapabilityLevel("F", "N1", NodeCapabilityKind.Refinery, 1);

            Assert.Equal(1, node.Capabilities[NodeCapabilityKind.Refinery]);
        }

        [Fact]
        public void SetCapabilityLevel_RemovesTheEntryAtZero()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedNode(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Refinery] = 2;

            world.SetCapabilityLevel("F", "N1", NodeCapabilityKind.Refinery, 0);

            Assert.False(node.Capabilities.ContainsKey(NodeCapabilityKind.Refinery));
        }

        [Fact]
        public void SetCapabilityLevel_IsANoOp_ForANodeAnotherFactionOwns()
        {
            WorldModel world = TestSupport.World();
            SpatialNode node = AddOwnedNode(world, "N1", "RIVAL");

            world.SetCapabilityLevel("F", "N1", NodeCapabilityKind.Refinery, 2);

            Assert.Empty(node.Capabilities);
        }
    }
}
