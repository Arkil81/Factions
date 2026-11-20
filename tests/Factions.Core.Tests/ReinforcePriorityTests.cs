using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class ReinforcePriorityTests
    {
        private static Personality Persona()
        {
            var p = new Personality { Temperature = 0.0 };
            p.Weights[Drive.Security] = 1;
            p.Weights[Drive.Growth] = 1;
            p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Rivalry] = 1;
            p.Weights[Drive.Caution] = 1;
            return p;
        }

        private static SpatialNode AddOwnedNode(FakeWorld w, string id, double hold, double value)
        {
            var node = w.Graph.Add(new Vec3(value * 1000, 0, 0), value, id);
            node.OwnerFactionTag = "F";
            node.HoldStrength = hold;
            return node;
        }

        [Fact]
        public void Think_ComputesDescendingReinforcePriorityFromNodeValue()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 500_000, Ledger = 500_000 };

            var scanBacked = AddOwnedNode(w, "E001", 0.6, 0.9);
            scanBacked.Capabilities[NodeCapabilityKind.Refinery] = 1;
            var campaignNode = AddOwnedNode(w, "N002", 0.5, 0.7);
            campaignNode.Capabilities[NodeCapabilityKind.Shipyard] = 2;
            var lowValue = AddOwnedNode(w, "N003", 0.3, 0.2);

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.CampaignFocusRegion = campaignNode.Id;

            brain.Think(s, Persona(), w, new SeededRandom(1), t);

            Assert.Equal(3, s.ReinforcePriority.Count);
            Assert.Equal(campaignNode.Id, s.ReinforcePriority[0].NodeId);
            Assert.Equal(scanBacked.Id, s.ReinforcePriority[1].NodeId);
            Assert.Equal(lowValue.Id, s.ReinforcePriority[2].NodeId);
            Assert.True(s.ReinforcePriority[0].Score >= s.ReinforcePriority[1].Score);
            Assert.True(s.ReinforcePriority[1].Score >= s.ReinforcePriority[2].Score);
        }

        [Fact]
        public void Think_IncludesScanBackedAndEngineNodesInSamePriorityList()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 500_000, Ledger = 500_000 };

            var scanBacked = AddOwnedNode(w, "E100", 0.7, 0.95);
            scanBacked.Capabilities[NodeCapabilityKind.RefineryII] = 1;
            var engineNode = AddOwnedNode(w, "N100", 0.2, 0.1);

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            brain.Think(s, Persona(), w, new SeededRandom(1), t);

            Assert.Contains(s.ReinforcePriority, x => x.NodeId == scanBacked.Id);
            Assert.Contains(s.ReinforcePriority, x => x.NodeId == engineNode.Id);
            Assert.Equal(scanBacked.Id, s.ReinforcePriority[0].NodeId);
        }

        [Fact]
        public void FleetManager_ConsumesBrainPriorityWithoutResortingOrNearDeadOverride()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            t.ReinforceCost = 500;
            t.ReinforceStep = 0.1;

            var w = TestSupport.World();
            w.SetTreasury("F", t.ReinforceCost);
            var lowValueNearDead = w.Graph.Add(new Vec3(0, 0, 0), 0.1, "N001");
            lowValueNearDead.OwnerFactionTag = "F";
            lowValueNearDead.HoldStrength = 0.1;
            // A reinforce flies a warship now, and never from the node it is reinforcing: the
            // low-value node is the only other ground this faction holds, so it is the depot.
            ReinforceMissionSupport.Stock(w, lowValueNearDead, 2);
            var highValue = w.Graph.Add(new Vec3(100, 0, 0), 0.95, "N002");
            highValue.OwnerFactionTag = "F";
            highValue.HoldStrength = 0.75;
            highValue.Capabilities[NodeCapabilityKind.Shipyard] = 6;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            brain.Think(s, Persona(), w, new SeededRandom(1), t);

            var log = new List<string>();
            var states = new List<FactionState> { s };
            new FleetManager().Tick(states, w, t, log);

            var reinforce = Assert.Single(log.Where(x => x.Contains("REINFORCE_DISPATCH cost=")));
            Assert.Contains(highValue.Id, reinforce);
            ReinforceMissionSupport.FlyFleets(w, t, states, log);
            Assert.Equal(0.85, highValue.HoldStrength, 6);
            Assert.Equal(0.1, lowValueNearDead.HoldStrength, 6);
        }

        // 2026-08-02 region-model change: the campaign bonus must apply to any node in
        // CampaignFocusRegionMembers, not just the exact CampaignFocusRegion center node.
        [Fact]
        public void CampaignBonus_AppliesToRegionMember_NotJustTheExactCenterNode()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 500_000, Ledger = 500_000 };

            // Otherwise-identical nodes; only "member" sits inside the flagged region.
            var member = AddOwnedNode(w, "N002", 0.6, 0.5);
            var nonMember = AddOwnedNode(w, "N003", 0.6, 0.5);

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            s.CampaignFocusRegion = "N001"; // the center — neither test node
            s.CampaignFocusRegionMembers.Add("N001");
            s.CampaignFocusRegionMembers.Add(member.Id); // member is a region neighbor, nonMember is not

            brain.Think(s, Persona(), w, new SeededRandom(1), t);

            var memberEntry = s.ReinforcePriority.Single(x => x.NodeId == member.Id);
            var nonMemberEntry = s.ReinforcePriority.Single(x => x.NodeId == nonMember.Id);
            Assert.True(memberEntry.Score > nonMemberEntry.Score,
                "a region member that isn't the exact CampaignFocusRegion center should still get the campaign bonus");
        }
    }
}
