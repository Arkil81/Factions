using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    [Collection(DebugTracesCollection.Name)]
    public class CentralBankTests : IDisposable
    {
        /// <summary>These traces are per-process statics, so a test that flips one and throws would
        /// leak it into whatever xUnit runs next. Dispose runs after every test in this class.</summary>
        public void Dispose() { DebugTraces.ResetToDefaults(); }

        [Fact]
        public void CurrentMoneySupply_SumsFactionTreasuries()
        {
            var world = TestSupport.World();
            world.SetTreasury("A", 125000);
            world.SetTreasury("B", -5000);

            Assert.Equal(120000, world.GetCurrentMoneySupply());
        }

        [Fact]
        public void SnapshotCentralBank_ComputesCollateralCountsAndTarget()
        {
            var world = TestSupport.World();
            world.Tuning.CentralBankReserveRatio = 0.5;
            world.Tuning.CentralBankTerritoryNodeValue = 10;
            world.Tuning.CentralBankFoundedNodeValue = 100;
            world.Tuning.CentralBankCapabilityLevelValue = 7;
            world.Tuning.CentralBankShipValue = 13;

            var a = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            a.OwnerFactionTag = "A";

            var b = world.Graph.Add(new Vec3(10, 0, 0), 0.5, "A2");
            b.OwnerFactionTag = "A";
            b.StableStationId = 42;
            b.Capabilities[NodeCapabilityKind.Refinery] = 1;
            b.Capabilities[NodeCapabilityKind.Manufactory] = 2;
            b.ShipStock["Patrol"] = 3;

            var c = world.Graph.Add(new Vec3(20, 0, 0), 0.5, "B1");
            c.OwnerFactionTag = "B";

            world.SetTreasury("A", 50);
            world.SetTreasury("B", -20);

            CentralBankSnapshot snap = world.SnapshotCentralBank();

            Assert.Equal(30, snap.CurrentMoneySupply);
            Assert.Equal(190, snap.WorldCollateralValue);
            Assert.Equal(95, snap.TargetMoneySupply);
            Assert.Equal(65, snap.MoneySupplyGap);
            Assert.Equal(0.5, snap.ReserveRatio, 6);
            Assert.Equal(3, snap.TerritoryNodeCount);
            Assert.Equal(1, snap.FoundedNodeCount);
            Assert.Equal(3, snap.CapabilityLevelCount);
            Assert.Equal(3, snap.ShipCount);
            Assert.Equal(3, snap.IdleShipCount);
            Assert.Equal(0, snap.InFlightShipCount);
        }

        [Fact]
        public void SnapshotCentralBank_ShipCount_CountsInFlightHullsAndSurvivesDispatch()
        {
            var world = TestSupport.World();
            var home = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            home.OwnerFactionTag = "A";
            home.ShipStock["Fighter"] = 4;

            var state = new FactionState { Tag = "A" };
            var states = new List<FactionState> { state };

            CentralBankSnapshot idle = world.SnapshotCentralBank(states);
            Assert.Equal(4, idle.ShipCount);
            Assert.Equal(4, idle.IdleShipCount);
            Assert.Equal(0, idle.InFlightShipCount);

            // A solo raid and a three-ship squadron leave port: ShipPool takes their hulls out of
            // node stock for the whole journey.
            home.ShipStock["Fighter"] = 0;
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Fighter", ConvoySize = 1,
            });
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Fighter", ConvoySize = 3,
                Squadron = new List<string> { "Fighter", "Fighter", "Fighter" },
            });

            CentralBankSnapshot flying = world.SnapshotCentralBank(states);
            Assert.Equal(0, flying.IdleShipCount);
            Assert.Equal(4, flying.InFlightShipCount);
            Assert.Equal(4, flying.ShipCount);
        }

        [Fact]
        public void TallyShipsByFaction_SplitsByOwnerAndSumsToTheWorldSnapshot()
        {
            var world = TestSupport.World();
            var a1 = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            a1.OwnerFactionTag = "A";
            a1.ShipStock["Fighter"] = 2;
            var b1 = world.Graph.Add(new Vec3(1000, 0, 0), 0.5, "B1");
            b1.OwnerFactionTag = "B";
            b1.ShipStock["Hauler"] = 5;
            var free = world.Graph.Add(new Vec3(2000, 0, 0), 0.5, "X1");
            free.ShipStock["Hauler"] = 7;

            var a = new FactionState { Tag = "A" };
            var b = new FactionState { Tag = "B" };
            var c = new FactionState { Tag = "C" };
            a.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Fighter", ConvoySize = 3,
                Squadron = new List<string> { "Fighter", "Fighter", "Fighter" },
            });
            // Back in stock already, or destroyed: neither is still flying.
            a.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Fighter", ConvoySize = 4, Resolved = true,
            });
            b.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "B", ShipPrefabId = "Hauler", ConvoySize = 2, Intercepted = true,
            });
            var states = new List<FactionState> { a, b, c };

            Dictionary<string, ShipTally> tally = world.TallyShipsByFaction(states);

            Assert.Equal(2, tally["A"].Idle);
            Assert.Equal(3, tally["A"].InFlight);
            Assert.Equal(5, tally["A"].Total);
            Assert.Equal(5, tally["B"].Idle);
            Assert.Equal(0, tally["B"].InFlight);
            Assert.False(tally.ContainsKey("C"));
            Assert.Equal(2, tally.Count);

            CentralBankSnapshot snap = world.SnapshotCentralBank(states);
            int idle = 0, flying = 0;
            foreach (ShipTally t in tally.Values) { idle += t.Idle; flying += t.InFlight; }
            Assert.Equal(snap.IdleShipCount, idle);
            Assert.Equal(snap.InFlightShipCount, flying);
            Assert.Equal(10, snap.ShipCount);
        }

        [Fact]
        public void Collateral_CountsInFlightHulls_SoDispatchDoesNotMoveTheTarget()
        {
            var world = TestSupport.World();
            world.Tuning.CentralBankReserveRatio = 0.5;
            world.Tuning.CentralBankTerritoryNodeValue = 10;
            world.Tuning.CentralBankFoundedNodeValue = 0;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 1000;

            var home = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            home.OwnerFactionTag = "A";
            home.ShipStock["Fighter"] = 4;

            var state = new FactionState { Tag = "A" };
            var states = new List<FactionState> { state };

            // 10 (territory) + 4 x 1000 (hulls)
            Assert.Equal(4010, world.GetWorldCollateralValue(states));
            Assert.Equal(4010, world.GetFactionCollateralValue("A", state));
            Assert.Equal(2005, world.GetTargetMoneySupply(states));

            // Three of the four leave port. The hulls move out of node stock and onto a fleet.
            home.ShipStock["Fighter"] = 1;
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Fighter", ConvoySize = 3,
            });

            Assert.Equal(4010, world.GetWorldCollateralValue(states));
            Assert.Equal(4010, world.GetFactionCollateralValue("A", state));
            Assert.Equal(2005, world.GetTargetMoneySupply(states));

            // Without the roster the old node-only figure is reproduced, three hulls short.
            Assert.Equal(1010, world.GetWorldCollateralValue());
            Assert.Equal(1010, world.GetFactionCollateralValue("A"));
        }

        [Fact]
        public void Collateral_InFlightHulls_SkipResolvedAndInterceptedFleets()
        {
            var world = TestSupport.World();
            world.Tuning.CentralBankTerritoryNodeValue = 10;
            world.Tuning.CentralBankFoundedNodeValue = 0;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 1000;

            var home = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            home.OwnerFactionTag = "A";
            home.ShipStock["Fighter"] = 1;   // the resolved fleet's hull, already credited back

            var state = new FactionState { Tag = "A" };
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Fighter", ConvoySize = 1,
                Resolved = true,
            });
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Fighter", ConvoySize = 2,
                Intercepted = true,
            });

            Assert.Equal(1010, world.GetWorldCollateralValue(new List<FactionState> { state }));
            Assert.Equal(1010, world.GetFactionCollateralValue("A", state));
        }

        [Fact]
        public void Collateral_LandlessFactionWithFleets_IsNeverVisitedByTheIncomePass()
        {
            // In-flight hulls give a landless faction non-zero collateral for the first time, so the
            // "collateral <= 0" denial that used to keep it out of rescue lending no longer holds.
            // What keeps it out is the income pass enumerating node OWNERS: it never visits this
            // faction at all. This test pins that reason. The explicit OwnsAnyNode gate in
            // ComputeCentralBankSupportLoan is unreachable while that stays true, and states the rule
            // for the day it does not -- this test does NOT exercise it.
            var world = TestSupport.World();
            world.Tuning.CentralBankShipValue = 1_000_000;

            var state = new FactionState { Tag = "A" };
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Fighter", ConvoySize = 5,
            });
            world.SetTreasury("A", -50_000);

            Assert.False(world.OwnsAnyNode("A"));
            Assert.Equal(5_000_000, world.GetFactionCollateralValue("A", state));

            long before = world.GetTreasury("A");
            world.AccrueIncome(new List<FactionState> { state });

            Assert.Equal(before, world.GetTreasury("A"));
            Assert.Equal(0, world.GetDebt("A"));
            Assert.Equal(0, state.CentralBankSupportDebt);
            Assert.DoesNotContain(world.SnapshotCentralBankEvents(), e => e.FactionTag == "A");
            Assert.DoesNotContain(world.SnapshotDebtEvents(), e => e.FactionTag == "A");
        }

        [Fact]
        public void SnapshotCentralBank_ShipCount_SkipsResolvedAndInterceptedFleets()
        {
            var world = TestSupport.World();
            var home = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            home.OwnerFactionTag = "A";
            home.ShipStock["Fighter"] = 1;   // the resolved fleet's hull, already credited back

            var state = new FactionState { Tag = "A" };
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Fighter", ConvoySize = 1,
                Resolved = true,
            });
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Fighter", ConvoySize = 2,
                Intercepted = true,
            });

            CentralBankSnapshot snap = world.SnapshotCentralBank(new List<FactionState> { state });
            Assert.Equal(1, snap.IdleShipCount);
            Assert.Equal(0, snap.InFlightShipCount);
            Assert.Equal(1, snap.ShipCount);
        }

        [Fact]
        public void SnapshotCentralBank_StationBookValue_IsCountedBeforeDevelopment()
        {
            var world = TestSupport.World();
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 10;
            world.Tuning.CentralBankFoundedNodeValue = 100;
            world.Tuning.CentralBankCapabilityLevelValue = 7;
            world.Tuning.CentralBankShipValue = 0;

            var legacy = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "E1");
            legacy.OwnerFactionTag = "A";
            legacy.StableStationId = 42;
            legacy.StationBookValue = 5000;

            CentralBankSnapshot before = world.SnapshotCentralBank();
            Assert.Equal(5010, before.WorldCollateralValue);
            Assert.Equal(0, before.FoundedNodeCount);

            legacy.Capabilities[NodeCapabilityKind.Refinery] = 1;

            CentralBankSnapshot after = world.SnapshotCentralBank();
            Assert.Equal(5017, after.WorldCollateralValue);
            Assert.Equal(1, after.FoundedNodeCount);
        }

        [Fact]
        public void FactionTuning_DefaultCentralBankPolicy_IsNonZero()
        {
            var tuning = new FactionTuning();

            Assert.Equal(0.4, tuning.CentralBankReserveRatio);
            Assert.Equal(0.01, tuning.CentralBankMaxAdjustmentFractionPerTick);
            Assert.Equal(250000, tuning.CentralBankTerritoryNodeValue);
            Assert.Equal(500000, tuning.CentralBankFoundedNodeValue);
            Assert.Equal(100000, tuning.CentralBankCapabilityLevelValue);
            Assert.Equal(125000, tuning.CentralBankShipValue);
            Assert.Equal(0.25, tuning.CentralBankSupportDebtFraction);
            Assert.Equal(250000, tuning.CentralBankSupportLoanAmount);
            Assert.Equal(50000, tuning.CentralBankSupportTreasuryBuffer);
            Assert.Equal(12.0, tuning.CentralBankSupportRunwayTicks);
            Assert.Equal(System.TimeSpan.FromMinutes(2), tuning.CentralBankSupportCooldown);
            Assert.Equal(1.0, tuning.CentralBankAssetPurchasePriceMultiplier);
            Assert.Equal(0.5, tuning.CentralBankBuyScarcityFloor);
            Assert.Equal(100, tuning.CentralBankBuyMinLot);
            Assert.Equal(1000000, tuning.CentralBankGrantAmount);

            Assert.Equal(12.0, tuning.CentralBankGrantRunwayTicks);
            Assert.Equal(0.02, tuning.CentralBankGrantCollateralFraction, 6);
            Assert.Equal(System.TimeSpan.FromMinutes(1), tuning.CentralBankGrantCooldown);
        }

        [Fact]
        public void AccrueIncome_IssuesCentralBankSupportLoan_BeforeForceShed()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankSupportDebtFraction = 0.5;
            world.Tuning.CentralBankSupportLoanAmount = 40000;
            world.Tuning.CentralBankSupportTreasuryBuffer = 30000;
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;

            world.SetTreasury("F", -60000);

            world.AccrueIncome(new[] { state });

            Assert.Equal(-20000, world.GetTreasury("F"));
            Assert.Equal(40000, world.GetDebt("F"));
            Assert.Equal("F", node.OwnerFactionTag);
            Assert.True(state.CentralBankSupportCooldownUntilUtc > world.UtcNow);
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "support-loan" && e.FactionTag == "F" && e.Amount == 40000);
            Assert.Contains(world.SnapshotDebtEvents(), e => e.Type == "support-loan" && e.FactionTag == "F" && e.Amount == 40000);
        }

        [Fact]
        public void AccrueIncome_IssuesCentralBankSupportLoan_BeforeCrossingFloor()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankSupportDebtFraction = 0.5;
            world.Tuning.CentralBankSupportLoanAmount = 40000;
            world.Tuning.CentralBankSupportTreasuryBuffer = 50000;
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;

            world.SetTreasury("F", 25000);

            world.AccrueIncome(new[] { state });

            Assert.Equal(50000, world.GetTreasury("F"));
            Assert.Equal(25000, world.GetDebt("F"));
            Assert.Equal("F", node.OwnerFactionTag);
            Assert.True(state.CentralBankSupportCooldownUntilUtc > world.UtcNow);
        }

        [Fact]
        public void AccrueIncome_CentralBankSupportLoan_TargetsRunwayWhenBleeding()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankSupportDebtFraction = 0.5;
            world.Tuning.CentralBankSupportLoanAmount = 100000;
            world.Tuning.CentralBankSupportTreasuryBuffer = 5000;
            world.Tuning.CentralBankSupportRunwayTicks = 6;
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.UpkeepPerSource = 1000;

            var a = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            a.OwnerFactionTag = "F";
            a.HoldStrength = 0.5;
            a.StableStationId = 1;

            var b = world.Graph.Add(new Vec3(10, 0, 0), 0.05, "N2");
            b.OwnerFactionTag = "F";
            b.HoldStrength = 0.5;
            b.StableStationId = 2;

            world.SetTreasury("F", 0);

            world.AccrueIncome(new[] { state });

            Assert.Equal(12000, world.GetTreasury("F"));
            Assert.Equal(14000, world.GetDebt("F"));
            Assert.Equal(-2000, world.GetNetFlow("F"));
        }

        [Fact]
        public void AccrueIncome_SupportLoanCooldown_PreservesWorkingTreasuryTargetAcrossTicks()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankSupportDebtFraction = 0.5;
            world.Tuning.CentralBankSupportLoanAmount = 40000;
            world.Tuning.CentralBankSupportTreasuryBuffer = 30000;
            world.Tuning.CentralBankSupportCooldown = System.TimeSpan.FromHours(1);
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;

            world.SetTreasury("F", -60000);

            world.AccrueIncome(new[] { state });
            Assert.Equal(-20000, world.GetTreasury("F"));
            Assert.Equal(40000, world.GetDebt("F"));

            world.SetTreasury("F", 60000);
            world.AccrueIncome(new[] { state });

            Assert.Equal(30000, world.GetTreasury("F"));
            Assert.Equal(10000, world.GetDebt("F"));
        }

        [Fact]
        public void AccrueIncome_AutoRepay_PreservesWorkingTreasuryTarget_WithoutRecentSupport()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankReserveRatio = 0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0;
            world.Tuning.CentralBankSupportTreasuryBuffer = 30000;
            world.Tuning.CentralBankSupportRunwayTicks = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;

            world.SetTreasury("F", 60000);
            world.SetDebt("F", 40000);

            world.AccrueIncome(new[] { new FactionState { Tag = "F" } });

            Assert.Equal(30000, world.GetTreasury("F"));
            Assert.Equal(10000, world.GetDebt("F"));
        }

        [Fact]
        public void AccrueIncome_CentralBankSupportLoan_IgnoresLegacyDebtButTracksSupportDebtSeparately()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.DebtRatio = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            DebugTraces.CentralBankDenials = true;   // per-process static; Dispose restores it
            world.Tuning.CentralBankSupportDebtFraction = 0.25;
            world.Tuning.CentralBankSupportLoanAmount = 40000;
            world.Tuning.CentralBankSupportTreasuryBuffer = 50000;
            world.Tuning.CentralBankSupportCooldown = System.TimeSpan.Zero;
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;

            world.SetTreasury("F", -60000);
            world.SetDebt("F", 500000);

            world.AccrueIncome(new[] { state });

            Assert.Equal(-30000, world.GetTreasury("F"));
            Assert.Equal(530000, world.GetDebt("F"));
            Assert.Equal(30000, state.CentralBankSupportDebt);
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "support-loan" && e.FactionTag == "F" && e.Amount == 30000);
        }

        [Fact]
        public void AccrueIncome_CentralBankSupportLoan_LogsDenialWhenSupportDebtCapReached()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F", CentralBankSupportDebt = 130000 };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.DebtRatio = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            DebugTraces.CentralBankDenials = true;   // per-process static; Dispose restores it
            world.Tuning.CentralBankSupportDebtFraction = 0.25;
            world.Tuning.CentralBankSupportLoanAmount = 40000;
            world.Tuning.CentralBankSupportTreasuryBuffer = 50000;
            world.Tuning.CentralBankSupportCooldown = System.TimeSpan.Zero;
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;

            world.SetTreasury("F", -60000);
            world.SetDebt("F", 500000);

            world.AccrueIncome(new[] { state });

            Assert.Equal(130000, state.CentralBankSupportDebt);
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "support-denied" && e.FactionTag == "F" && e.Message.Contains("Support debt cap"));
        }

        [Fact]
        public void AccrueIncome_CentralBankGrant_InjectsFreeMoneyWithoutDebt()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.DebtRatio = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankGrantAmount = 40000;
            world.Tuning.CentralBankGrantTreasuryBuffer = 50000;
            world.Tuning.CentralBankGrantCooldown = System.TimeSpan.Zero;
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;
            node.StationBookValue = 2000000;

            world.SetTreasury("F", -10000);

            world.AccrueIncome(new[] { state });

            Assert.Equal(30000, world.GetTreasury("F"));
            Assert.Equal(0, world.GetDebt("F"));
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "grant" && e.FactionTag == "F" && e.Amount == 40000);
        }

        [Fact]
        public void AccrueIncome_CentralBankGrant_AnchorsToCollateralInsteadOfFlatBuffer()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.DebtRatio = 0;
            world.Tuning.CentralBankGrantAmount = 1000000;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankGrantTreasuryBuffer = 75000;
            world.Tuning.CentralBankGrantCollateralFraction = 0.02;
            world.Tuning.CentralBankGrantCooldown = System.TimeSpan.Zero;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 0;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.StationBookValue = 5000000;

            world.SetTreasury("F", 0);

            world.AccrueIncome(new[] { state });

            Assert.Equal(100000, world.GetTreasury("F"));
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "grant" && e.FactionTag == "F" && e.Amount == 100000);
        }

        [Fact]
        public void AccrueIncome_CentralBankDenials_AreSuppressedByDefault()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 1000000;
            world.Tuning.CentralBankGrantCooldown = System.TimeSpan.FromHours(1);
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 0;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.StationBookValue = 2000000;

            world.SetTreasury("F", -10000);
            world.AccrueIncome(new[] { state });

            int afterGrantEvents = world.SnapshotCentralBankEvents().Count;
            world.SetTreasury("F", -10000);
            world.AccrueIncome(new[] { state });

            Assert.Equal(afterGrantEvents, world.SnapshotCentralBankEvents().Count);
            Assert.DoesNotContain(world.SnapshotCentralBankEvents(), e => e.Type == "grant-denied" || e.Type == "support-denied");
        }

        [Fact]
        public void AccrueIncome_CentralBankGrant_RespectsHopeThresholdAndCooldown()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.DebtRatio = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankGrantAmount = 40000;
            world.Tuning.CentralBankGrantTreasuryBuffer = 50000;
            world.Tuning.CentralBankGrantCooldown = System.TimeSpan.FromHours(1);
            DebugTraces.CentralBankDenials = true;   // per-process static; Dispose restores it
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;
            node.StationBookValue = 2000000;

            world.SetTreasury("F", -10000);

            world.AccrueIncome(new[] { state });

            Assert.Equal(30000, world.GetTreasury("F"));
            Assert.True(state.CentralBankGrantCooldownUntilUtc > world.UtcNow);
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "grant" && e.FactionTag == "F");

            world.SetTreasury("F", world.Tuning.InsolvencyFloor - 10000);
            world.AccrueIncome(new[] { state });

            Assert.Equal(world.Tuning.InsolvencyFloor, world.GetTreasury("F"));
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "grant-denied" && e.FactionTag == "F" && e.Message.Contains("cooldown"));
        }

        [Fact]
        public void AccrueIncome_CentralBankSupportLoan_RespectsCooldownAndCollateralCap()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankSupportDebtFraction = 0.25;
            world.Tuning.CentralBankSupportLoanAmount = 100000;
            world.Tuning.CentralBankSupportTreasuryBuffer = 50000;
            world.Tuning.CentralBankSupportCooldown = System.TimeSpan.FromHours(1);
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.2, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.7;
            node.StableStationId = 1;

            world.SetTreasury("F", -60000);

            world.AccrueIncome(new[] { state });

            Assert.Equal(-30000, world.GetTreasury("F"));
            Assert.Equal(30000, world.GetDebt("F"));

            world.SetTreasury("F", -60000);
            world.AccrueIncome(new[] { state });

            Assert.Equal(-50000, world.GetTreasury("F"));
            Assert.Equal(30000, world.GetDebt("F"));

            world.UtcNow = world.UtcNow.AddHours(2);
            world.SetTreasury("F", -60000);
            world.AccrueIncome(new[] { state });

            Assert.Equal(-50000, world.GetTreasury("F"));
            Assert.Equal(30000, world.GetDebt("F"));
        }

        [Fact]
        public void CentralBankSupportCooldown_RoundTripsThroughPersistence()
        {
            var until = new System.DateTime(2026, 2, 3, 4, 5, 6, System.DateTimeKind.Utc);
            var state = new FactionState { Tag = "F", CentralBankSupportCooldownUntilUtc = until };

            BrainDoc doc = FactionStore.ToBrainDoc(state, System.Array.Empty<SpatialNode>(), 10);
            Assert.Equal(until, doc.CentralBankSupportCooldownUntilUtc);

            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal(until, back.CentralBankSupportCooldownUntilUtc);
        }

        [Fact]
        public void CentralBankGrantCooldown_RoundTripsThroughPersistence()
        {
            var until = new System.DateTime(2026, 2, 3, 4, 5, 6, System.DateTimeKind.Utc);
            var state = new FactionState { Tag = "F", CentralBankGrantCooldownUntilUtc = until };

            BrainDoc doc = FactionStore.ToBrainDoc(state, System.Array.Empty<SpatialNode>(), 10);
            Assert.Equal(until, doc.CentralBankGrantCooldownUntilUtc);

            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal(until, back.CentralBankGrantCooldownUntilUtc);
        }

        [Fact]
        public void CentralBankSupportDebt_RoundTripsThroughPersistence()
        {
            var state = new FactionState { Tag = "F", CentralBankSupportDebt = 12345 };

            BrainDoc doc = FactionStore.ToBrainDoc(state, System.Array.Empty<SpatialNode>(), 10);
            Assert.Equal(12345, doc.CentralBankSupportDebt);

            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal(12345, back.CentralBankSupportDebt);
        }

        [Fact]
        public void CentralBankRecentIncome_RoundTripsThroughPersistence()
        {
            var state = new FactionState { Tag = "F", CentralBankRecentIncome = 12345.5 };

            BrainDoc doc = FactionStore.ToBrainDoc(state, System.Array.Empty<SpatialNode>(), 10);
            Assert.Equal(12345.5, doc.CentralBankRecentIncome);

            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal(12345.5, back.CentralBankRecentIncome);
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_AddsTreasuryWithoutDebt()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 1000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankAssetPurchasePriceMultiplier = 1.0;
            world.Tuning.CentralBankBuyScarcityFloor = 0.5;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Comp:SteelPlate"] = 200;

            world.SetTreasury("F", 0);

            world.AccrueIncome(new[] { new FactionState { Tag = "F" } });

            Assert.Equal(10, world.GetTreasury("F")); // 2 units × 10 credits × scarcity floor 0.5
            Assert.Equal(0, world.GetDebt("F"));
            Assert.Equal(198, world.GetInventory("F", ItemKind.ProcessedGood));
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "asset-purchase" && e.FactionTag == "F" && e.Amount == 10 && e.Message.Contains("Comp:SteelPlate"));
        }

        private static WorldModel AssetPurchaseWorld()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 1000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankAssetPurchasePriceMultiplier = 1.0;
            world.Tuning.CentralBankBuyScarcityFloor = 0.5;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;
            return world;
        }

        private static void AddGlutNode(WorldModel world, string id, string owner, double x)
        {
            var node = world.Graph.Add(new Vec3(x, 0, 0), 0.5, id);
            node.OwnerFactionTag = owner;
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Comp:SteelPlate"] = 200;
            world.SetTreasury(owner, 0);
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_SkipsADontSellItemAndBuysTheRest()
        {
            var world = AssetPurchaseWorld();
            AddGlutNode(world, "N1", "F", 0);
            world.Graph.Nodes["N1"].Inventory["Comp:Construction"] = 200;
            var f = new FactionState { Tag = "F", IsPlayerFaction = true };
            f.SetCentralBankRule("Comp:SteelPlate", BankSaleMode.DontSell, 0);

            world.AccrueIncome(new[] { f });

            Assert.Equal(200, world.Graph.Nodes["N1"].Inventory["Comp:SteelPlate"]);
            Assert.Equal(198, world.Graph.Nodes["N1"].Inventory["Comp:Construction"]);
            Assert.DoesNotContain(world.SnapshotCentralBankEvents(),
                e => e.Type == "asset-purchase" && e.Message.Contains("Comp:SteelPlate"));
            Assert.Contains(world.SnapshotCentralBankEvents(),
                e => e.Type == "asset-purchase" && e.FactionTag == "F" && e.Message.Contains("Comp:Construction"));
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_AFactionNotSellingItsOnlyGlutIsNotASeller()
        {
            var world = AssetPurchaseWorld();
            AddGlutNode(world, "N1", "F", 0);
            AddGlutNode(world, "N2", "G", 100000);
            var f = new FactionState { Tag = "F", IsPlayerFaction = true };
            f.SetCentralBankRule("Comp:SteelPlate", BankSaleMode.DontSell, 0);
            var g = new FactionState { Tag = "G" };

            world.AccrueIncome(new[] { f, g });

            Assert.Equal(0, world.GetTreasury("F"));
            Assert.Equal(200, world.GetInventory("F", ItemKind.ProcessedGood));
            Assert.Equal(198, world.GetInventory("G", ItemKind.ProcessedGood));
            Assert.DoesNotContain(world.SnapshotCentralBankEvents(), e => e.Type == "asset-purchase" && e.FactionTag == "F");
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_NeverBuysBelowTheFactionWideTarget()
        {
            // 400 held across two glut nodes; each would sell 2 (1% of 200). A target of 399 leaves
            // room for one unit in total, across both nodes.
            var world = AssetPurchaseWorld();
            AddGlutNode(world, "N1", "F", 0);
            AddGlutNode(world, "N2", "F", 100000);
            var f = new FactionState { Tag = "F", IsPlayerFaction = true };
            f.SetCentralBankRule("Comp:SteelPlate", BankSaleMode.AboveTarget, 399);

            world.AccrueIncome(new[] { f });

            Assert.Equal(399, world.GetInventory("F", ItemKind.ProcessedGood));
            Assert.Contains(world.SnapshotCentralBankEvents(),
                e => e.Type == "asset-purchase" && e.FactionTag == "F" && e.Message.Contains("Comp:SteelPlate x1"));
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_ATargetAtOrAboveHoldingsSellsNothing()
        {
            var world = AssetPurchaseWorld();
            AddGlutNode(world, "N1", "F", 0);
            var f = new FactionState { Tag = "F", IsPlayerFaction = true };
            f.SetCentralBankRule("Comp:SteelPlate", BankSaleMode.AboveTarget, 200);

            world.AccrueIncome(new[] { f });

            Assert.Equal(200, world.GetInventory("F", ItemKind.ProcessedGood));
            Assert.Equal(0, world.GetTreasury("F"));
            Assert.DoesNotContain(world.SnapshotCentralBankEvents(), e => e.Type == "asset-purchase");
        }

        [Fact]
        public void SetCentralBankRule_KeepsAKeyInOneRule_AndAZeroTargetIsTheDefault()
        {
            var st = new FactionState { Tag = "F" };
            long target;

            Assert.True(st.SetCentralBankRule("Comp:Motor", BankSaleMode.DontSell, 0));
            Assert.True(st.SetCentralBankRule("Comp:Motor", BankSaleMode.AboveTarget, 50));
            Assert.Equal(BankSaleMode.AboveTarget, st.CentralBankRuleFor("Comp:Motor", out target));
            Assert.Equal(50, target);
            Assert.DoesNotContain("Comp:Motor", st.CentralBankDontSell);
            Assert.False(st.SetCentralBankRule("Comp:Motor", BankSaleMode.AboveTarget, 50));

            Assert.True(st.SetCentralBankRule("Comp:Motor", BankSaleMode.AboveTarget, 0));
            Assert.Equal(BankSaleMode.SellOversupply, st.CentralBankRuleFor("Comp:Motor", out target));
            Assert.Equal(0, st.CentralBankRuleCount);
        }

        [Fact]
        public void CentralBankRules_RoundTripAndAreOmittedWhenEmpty()
        {
            var state = new FactionState { Tag = "F", IsPlayerFaction = true };
            state.SetCentralBankRule("Comp:SteelPlate", BankSaleMode.DontSell, 0);
            state.SetCentralBankRule("Comp:Display", BankSaleMode.DontSell, 0);
            state.SetCentralBankRule("Comp:Motor", BankSaleMode.AboveTarget, 750);
            BrainDoc doc = FactionStore.ToBrainDoc(state, System.Array.Empty<SpatialNode>(), 10);
            Assert.Equal(new[] { "Comp:Display", "Comp:SteelPlate" }, doc.CentralBankDontSell);
            Assert.Equal(750, doc.CentralBankTargets["Comp:Motor"]);

            FactionState back = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            long target;
            Assert.Equal(BankSaleMode.DontSell, back.CentralBankRuleFor("Comp:SteelPlate", out target));
            Assert.Equal(BankSaleMode.AboveTarget, back.CentralBankRuleFor("Comp:Motor", out target));
            Assert.Equal(750, target);
            Assert.False(back.LegacyCentralBankSellOptOut);

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(FactionStore.ToBrainDoc(
                new FactionState { Tag = "G", IsPlayerFaction = true }, System.Array.Empty<SpatialNode>(), 10));
            Assert.DoesNotContain("CentralBankDontSell", json);
            Assert.DoesNotContain("CentralBankTargets", json);
            Assert.DoesNotContain("CentralBankSellOptOut", json);
        }

        [Fact]
        public void LegacyBankSellOptOut_ExpandsToEveryBankItem_AndSurvivesUntilItCan()
        {
            // A brain written while the switch was faction-wide.
            var doc = FactionStore.ToBrainDoc(new FactionState { Tag = "F", IsPlayerFaction = true },
                System.Array.Empty<SpatialNode>(), 10);
            doc.CentralBankSellOptOut = true;
            FactionState st = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.True(st.LegacyCentralBankSellOptOut);

            // No catalogue yet: the flag is neither honoured nor dropped, and is written back.
            Assert.Equal(0, FactionLifecycle.ExpandLegacyBankSellOptOut(st, new string[0]));
            Assert.True(st.LegacyCentralBankSellOptOut);
            Assert.True(FactionStore.ToBrainDoc(st, System.Array.Empty<SpatialNode>(), 10).CentralBankSellOptOut);

            Assert.Equal(2, FactionLifecycle.ExpandLegacyBankSellOptOut(st, new[] { "Comp:SteelPlate", "Comp:Motor" }));
            Assert.False(st.LegacyCentralBankSellOptOut);
            Assert.True(st.CentralBankDontSell.SetEquals(new[] { "Comp:SteelPlate", "Comp:Motor" }));
            Assert.False(FactionStore.ToBrainDoc(st, System.Array.Empty<SpatialNode>(), 10).CentralBankSellOptOut);
        }

        [Fact]
        public void BankSalesReport_ListsCatalogueHeldAndKept_AndSumsHeldAcrossNodes()
        {
            var world = AssetPurchaseWorld();
            world.Registry = NullItemRegistry.Instance;
            AddGlutNode(world, "N1", "F", 0);
            AddGlutNode(world, "N2", "F", 100000);
            world.Graph.Nodes["N2"].Inventory["Ingot:Iron"] = 500;   // the bank never buys an ingot
            var f = new FactionState { Tag = "F", IsPlayerFaction = true };
            f.SetCentralBankRule("Comp:Motor", BankSaleMode.DontSell, 0);

            System.Collections.Generic.List<BankSalesRow> rows =
                BankSalesReport.Build(world, f, new[] { "Comp:Construction" });

            Assert.Equal(new[] { "Comp:Construction", "Comp:Motor", "Comp:SteelPlate" },
                rows.ConvertAll(r => r.Key));
            Assert.Equal(400, rows.Find(r => r.Key == "Comp:SteelPlate").Held);
            Assert.Equal(0, rows.Find(r => r.Key == "Comp:Construction").Held);
            Assert.Equal(BankSaleMode.DontSell, rows.Find(r => r.Key == "Comp:Motor").Mode);
            Assert.Equal(BankSaleMode.SellOversupply, rows.Find(r => r.Key == "Comp:SteelPlate").Mode);
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_DoesNotBuyBuildQueueReservedStock()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 1000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankAssetPurchasePriceMultiplier = 1.0;
            world.Tuning.CentralBankBuyScarcityFloor = 0.5;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Comp:SteelPlate"] = 200;

            // An in-flight ship build needs every unit currently in stock (deep glut by scarcity,
            // but none of it is actually surplus — see SpatialNode.BuildQueueReserved).
            // ExpiresUtc matters: an expired entry no longer withholds anything, since the next
            // ShipyardManager tick prunes and refunds it anyway (SpatialNode.BuildQueueReserved).
            var entry = new BuildQueueEntry { Id = "Raid:001", Role = "Raid", ExpiresUtc = world.UtcNow.AddHours(23) };
            entry.Bom["Comp:SteelPlate"] = 200;
            node.BuildQueue.Add(entry);

            world.SetTreasury("F", 0);

            world.AccrueIncome(new[] { new FactionState { Tag = "F" } });

            Assert.Equal(0, world.GetTreasury("F"));
            Assert.Equal(200, node.Inventory["Comp:SteelPlate"]);
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_BuysOnlyThePortionNotBuildQueueReserved()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 1000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankAssetPurchasePriceMultiplier = 1.0;
            world.Tuning.CentralBankBuyScarcityFloor = 0.5;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Comp:SteelPlate"] = 200;

            // Build only needs 150 of the 200 on hand — the remaining 50 is genuine surplus.
            // ExpiresUtc matters: an expired entry no longer withholds anything, since the next
            // ShipyardManager tick prunes and refunds it anyway (SpatialNode.BuildQueueReserved).
            var entry = new BuildQueueEntry { Id = "Raid:001", Role = "Raid", ExpiresUtc = world.UtcNow.AddHours(23) };
            entry.Bom["Comp:SteelPlate"] = 150;
            node.BuildQueue.Add(entry);

            world.SetTreasury("F", 0);

            world.AccrueIncome(new[] { new FactionState { Tag = "F" } });

            // 1 % of the 200 on hand = 2 units, well within the 50-unit unreserved surplus.
            Assert.Equal(10, world.GetTreasury("F"));
            Assert.Equal(198, node.Inventory["Comp:SteelPlate"]);
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_UsesRelativeScarcityForSmallFaction()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 1000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankAssetPurchasePriceMultiplier = 1.0;
            world.Tuning.CentralBankBuyScarcityFloor = 0.5;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Comp:SteelPlate"] = 200;

            world.AccrueIncome(new[] { new FactionState { Tag = "F" } });

            Assert.True(world.GetTreasury("F") > 0);
            Assert.Equal(198, node.Inventory["Comp:SteelPlate"]);
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_PricesDeepGlutBelowMildSurplus()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 2000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankAssetPurchasePriceMultiplier = 1.0;
            world.Tuning.CentralBankBuyScarcityFloor = 1.0;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;

            var deep = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            deep.OwnerFactionTag = "A";
            deep.HoldStrength = 0.8;
            deep.StableStationId = 1;
            deep.Capabilities[NodeCapabilityKind.Refinery] = 1;
            deep.Inventory["Comp:SteelPlate"] = 400;

            var mild = world.Graph.Add(new Vec3(10, 0, 0), 0.5, "B1");
            mild.OwnerFactionTag = "B";
            mild.HoldStrength = 0.8;
            mild.StableStationId = 2;
            mild.Capabilities[NodeCapabilityKind.Refinery] = 1;
            mild.Inventory["Comp:SteelPlate"] = 120;

            long deepBefore = deep.Inventory["Comp:SteelPlate"];
            long mildBefore = mild.Inventory["Comp:SteelPlate"];

            world.AccrueIncome(new[] { new FactionState { Tag = "A" }, new FactionState { Tag = "B" } });

            long deepUnits = deepBefore - deep.Inventory["Comp:SteelPlate"];
            long mildUnits = mildBefore - mild.Inventory["Comp:SteelPlate"];
            Assert.True(deepUnits > 0);
            Assert.True(mildUnits > 0);
            long deepUnitPrice = world.GetTreasury("A") / deepUnits;
            long mildUnitPrice = world.GetTreasury("B") / mildUnits;
            Assert.True(deepUnitPrice < mildUnitPrice);
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_SelfCorrectsAsScarcityRises()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 1000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankAssetPurchasePriceMultiplier = 1.0;
            world.Tuning.CentralBankBuyScarcityFloor = 0.5;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Comp:SteelPlate"] = 200;

            var state = new FactionState { Tag = "F" };
            for (int i = 0; i < 35; i++)
                world.AccrueIncome(new[] { state });

            Assert.True(world.GetTreasury("F") > 0);
            Assert.True(node.Inventory["Comp:SteelPlate"] < 200);
            Assert.True(world.PriceOracle.ScarcityMultiplier(node.Id, "Comp:SteelPlate") > 0.5);

            long before = world.GetTreasury("F");
            world.AccrueIncome(new[] { state });
            Assert.Equal(before, world.GetTreasury("F"));
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_StillIgnoresDemandSignals()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 1000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankAssetPurchasePriceMultiplier = 1.0;
            world.Tuning.DemandPressureFloorProcessedGood = 100;
            world.Tuning.CentralBankBuyScarcityFloor = 0.5;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Comp:SteelPlate"] = 220;
            node.Inventory["Comp:Motor"] = 90;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F",
                Kind = "Item:Comp:SteelPlate",
                NodeId = node.Id,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
                Quantity = 300,
            });

            world.SetTreasury("F", 0);

            world.AccrueIncome(new[] { new FactionState { Tag = "F" } });

            Assert.Equal(15, world.GetTreasury("F"));
            Assert.Equal(217, node.Inventory["Comp:SteelPlate"]);
            Assert.Equal(90, node.Inventory["Comp:Motor"]);
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "asset-purchase" && e.FactionTag == "F" && e.Message.Contains("Comp:SteelPlate x3") && e.Message.Contains("unitPrice=5"));
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_DoesNotBuyOre()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 1000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankBuyScarcityFloor = 0.5;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.OreYield = 0;
            world.Tuning.RareOreYield = 0;   // every ore is ItemKind.Ore now, and rare ore always
                                             // stored under the "Ore:" prefix -- so leaving this
                                             // non-zero mints into the total this test asserts
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.FreeMinerCargoVolumeLiters = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["Ore:Iron"] = 5000;

            world.SetTreasury("F", 0);

            world.AccrueIncome(new[] { new FactionState { Tag = "F" } });

            Assert.Equal(0, world.GetTreasury("F"));
            Assert.Equal(5000, world.GetInventory("F", ItemKind.Ore));
            Assert.Equal(0, world.GetDebt("F"));
        }

        [Fact]
        public void AccrueIncome_CentralBankAssetPurchase_SplitsBudgetAcrossEligibleFactions()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 10;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.DemandPressureFloorProcessedGood = 0;
            world.Tuning.CentralBankBuyScarcityFloor = 1.0;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.BaseYield = 0;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;

            var a = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            a.OwnerFactionTag = "A";
            a.HoldStrength = 0.8;
            a.StableStationId = 1;
            a.Capabilities[NodeCapabilityKind.Refinery] = 1;
            a.Inventory["SteelPlate"] = 500;

            var b = world.Graph.Add(new Vec3(10, 0, 0), 0.5, "B1");
            b.OwnerFactionTag = "B";
            b.HoldStrength = 0.8;
            b.StableStationId = 2;
            b.Capabilities[NodeCapabilityKind.Refinery] = 1;
            b.Inventory["SteelPlate"] = 150;

            world.SetTreasury("A", 0);
            world.SetTreasury("B", 0);

            world.AccrueIncome(new[] { new FactionState { Tag = "A" }, new FactionState { Tag = "B" } });

            Assert.True(world.GetTreasury("A") > 0);
            Assert.True(world.GetTreasury("B") > 0);
        }

        [Fact]
        public void AccrueIncome_CentralBankExpansion_IsBoundedByAdjustmentFraction()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0.1;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 1000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.DemandPressureFloorProcessedGood = 0;
            world.Tuning.CentralBankBuyScarcityFloor = 1.0;
            world.Tuning.CentralBankBuyMinLot = 1;
            world.Tuning.BaseYield = 0;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            node.Inventory["SteelPlate"] = 1000;

            world.AccrueIncome(new[] { new FactionState { Tag = "F" } });

            Assert.True(world.GetTreasury("F") > 0);
            Assert.True(world.GetTreasury("F") <= 100);
        }

        [Fact]
        public void AccrueIncome_CentralBankContraction_IsBoundedAndMonotonic()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0.1;
            world.Tuning.CentralBankTerritoryNodeValue = 0;
            world.Tuning.CentralBankFoundedNodeValue = 10;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.OreYield = 0;
            world.Tuning.ProcessedGoodYield = 0;
            world.Tuning.UpkeepOre = 0;
            world.Tuning.UpkeepProcessedGood = 0;
            // The levy only reaches treasury above each faction's own reserve, whose floor is the grant
            // treasury target (75,000 by default) -- above these 1,000-credit treasuries. Zero it, and
            // the band, so this keeps measuring the controller's pace rather than the reserve.
            world.Tuning.CentralBankGrantTreasuryBuffer = 0;
            world.Tuning.CentralBankGrantRunwayTicks = 0;
            world.Tuning.CentralBankGrantCollateralFraction = 0;
            world.Tuning.CentralBankContractionBand = 0;

            var a = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1");
            a.OwnerFactionTag = "A";
            a.HoldStrength = 0.8;
            a.StableStationId = 1;

            var b = world.Graph.Add(new Vec3(10, 0, 0), 0.5, "B1");
            b.OwnerFactionTag = "B";
            b.HoldStrength = 0.8;
            b.StableStationId = 2;

            world.SetTreasury("A", 1000);
            world.SetTreasury("B", 1000);

            long previousSupply = world.GetCurrentMoneySupply();
            for (int i = 0; i < 5; i++)
            {
                world.AccrueIncome(new[] { new FactionState { Tag = "A" }, new FactionState { Tag = "B" } });
                long currentSupply = world.GetCurrentMoneySupply();
                Assert.True(currentSupply < previousSupply);
                Assert.True(currentSupply >= world.GetTargetMoneySupply());
                previousSupply = currentSupply;
            }

            Assert.Equal(1187, world.GetCurrentMoneySupply());
            Assert.True(world.GetTreasury("A") > 0);
            Assert.True(world.GetTreasury("B") > 0);
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "contraction" && e.Amount < 0);
        }

        [Fact]
        public void AccrueIncome_DebtEvents_RecordInterestAndRepayment()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0.1;
            world.Tuning.InsolvencyFloor = 0;
            world.Tuning.CentralBankReserveRatio = 0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0;
            world.Tuning.CentralBankGrantAmount = 0;
            world.Tuning.CentralBankSupportTreasuryBuffer = 0;
            world.Tuning.CentralBankSupportRunwayTicks = 0;
            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            world.SetTreasury("F", 1000);
            world.SetDebt("F", 100);

            world.AccrueIncome(new[] { new FactionState { Tag = "F" } });

            Assert.Equal(880, world.GetTreasury("F"));
            Assert.Equal(0, world.GetDebt("F"));
            Assert.Contains(world.SnapshotDebtEvents(), e => e.Type == "interest" && e.FactionTag == "F" && e.Amount == -10);
            Assert.Contains(world.SnapshotDebtEvents(), e => e.Type == "repay" && e.FactionTag == "F" && e.Amount == -110);
        }

        [Fact]
        public void AccrueIncome_SupportLoan_DeniedWithNoIncomeHistory()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0.1;
            world.Tuning.LoanAmount = 0;
            world.Tuning.InsolvencyFloor = -1000000; // keep force-shed out of scope; this test is only about the loan gate
            world.Tuning.CentralBankGrantAmount = 0; // no grants: RecentIncome stays at its starting value of 0
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankSupportDebtFraction = 0.5;
            world.Tuning.CentralBankSupportLoanAmount = 40000;
            world.Tuning.CentralBankSupportTreasuryBuffer = 30000;
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankDebtServiceIncomeCapFraction = 0.5;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;

            world.SetTreasury("F", -60000);

            world.AccrueIncome(new[] { state });

            // Mirrors AccrueIncome_IssuesCentralBankSupportLoan_BeforeForceShed's setup exactly (which
            // issues a 40000 loan there), but with a nonzero interest rate and zero income history: any
            // nonzero projected interest exceeds a zero income cap, so the loan that would otherwise fire
            // is denied instead — a bank shouldn't lend to a faction with no earnings track record.
            Assert.Equal(0, world.GetDebt("F"));
            Assert.Equal(0.0, state.CentralBankRecentIncome);
        }

        [Fact]
        public void AccrueIncome_SupportLoan_UsesGrantSeededIncome_ButLoanPrincipalItselfIsNotIncome()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0; // tick 1: pure grant, no interest complications
            world.Tuning.LoanAmount = 0;
            world.Tuning.InsolvencyFloor = -1000000;
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankGrantAmount = 40000;
            world.Tuning.CentralBankGrantTreasuryBuffer = 50000;
            world.Tuning.CentralBankGrantCooldown = System.TimeSpan.Zero;
            world.Tuning.CentralBankTerritoryNodeValue = 20000;
            world.Tuning.CentralBankFoundedNodeValue = 100000;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0; // tick 1: grants only
            world.Tuning.CentralBankIncomeEmaPeriods = 1;  // alpha = 1.0: RecentIncome exactly equals the latest pass's income sample

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;
            node.StableStationId = 1;
            node.StationBookValue = 2000000;

            world.SetTreasury("F", -10000);
            world.AccrueIncome(new[] { state });

            Assert.Equal(0, world.GetDebt("F"));
            Assert.Equal(40000.0, state.CentralBankRecentIncome, 6);

            // Tick 2: enable support loans and a nonzero interest rate, matching
            // AccrueIncome_IssuesCentralBankSupportLoan_BeforeForceShed's numbers exactly (known to
            // produce a 40000 loan there). The grant-seeded income from tick 1 clears the income cap:
            // projected interest 40000 * 0.1 = 4000 <= cap 40000 * 0.5 = 20000.
            world.Tuning.DebtInterestRate = 0.1;
            world.Tuning.CentralBankGrantAmount = 0; // tick 2: no new grant — isolate the loan
            world.Tuning.CentralBankSupportDebtFraction = 0.5;
            world.Tuning.CentralBankSupportLoanAmount = 40000;
            world.Tuning.CentralBankSupportTreasuryBuffer = 30000;
            world.SetTreasury("F", -60000);

            world.AccrueIncome(new[] { state });

            Assert.Equal(40000, world.GetDebt("F"));
            // The loan itself must not count as income: tick 2 had no grant, so RecentIncome should have
            // decayed back to 0 (periods=1 fully forgets the prior tick), not been inflated by the
            // 40000 loan that was just issued.
            Assert.Equal(0.0, state.CentralBankRecentIncome, 6);
        }

        [Fact]
        public void AccrueIncome_IncomeEma_SmoothsAcrossConfiguredPeriods()
        {
            var world = TestSupport.World();
            var state = new FactionState { Tag = "F" };
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.LoanAmount = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0; // isolate: grants only, no support loans this test
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankGrantTreasuryBuffer = 1000000;   // keep "needed" far above GrantAmount
            world.Tuning.CentralBankGrantCollateralFraction = 1.0;   // keep the collateral-based cap far above GrantAmount too
            world.Tuning.CentralBankGrantCooldown = System.TimeSpan.Zero;
            world.Tuning.CentralBankTerritoryNodeValue = 2000000;    // ample collateral so GrantAmount is the binding cap
            world.Tuning.CentralBankFoundedNodeValue = 0;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankIncomeEmaPeriods = 3; // alpha = 2/(3+1) = 0.5, for easy hand-verified arithmetic

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.05, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;

            world.SetTreasury("F", -1000000);
            world.Tuning.CentralBankGrantAmount = 1000;
            world.AccrueIncome(new[] { state });
            Assert.Equal(500.0, state.CentralBankRecentIncome, 6); // 0.5*1000 + 0.5*0

            world.SetTreasury("F", -1000000);
            world.Tuning.CentralBankGrantAmount = 2000;
            world.AccrueIncome(new[] { state });
            Assert.Equal(1250.0, state.CentralBankRecentIncome, 6); // 0.5*2000 + 0.5*500
        }

        [Fact]
        public void AccrueIncome_TradeSettlement_FeedsRecentIncomeOnNextPass()
        {
            var world = TestSupport.World();
            world.Tuning.CentralBankGrantAmount = 0; // isolate: trade income only
            world.Tuning.CentralBankSupportLoanAmount = 0;
            world.Tuning.CentralBankIncomeEmaPeriods = 1; // alpha = 1.0, exact match for simple arithmetic

            var supplier = new FactionState { Tag = "A" };
            var recipient = new FactionState { Tag = "B" };
            world.Graph.Add(new Vec3(0, 0, 0), 0.8, "A1").OwnerFactionTag = "A";
            world.Graph.Add(new Vec3(10, 0, 0), 0.8, "B1").OwnerFactionTag = "B";
            world.SetTreasury("A", 10000);
            world.SetTreasury("B", 10000);

            DateTime now = world.UtcNow;
            supplier.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now, CargoValue = 4000
            });

            var states = new List<FactionState> { supplier, recipient };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, world, world.Tuning, new SeededRandom(1), log, null);

            Assert.Equal(0.0, supplier.CentralBankRecentIncome); // not folded in yet — pending until the next AccrueIncome

            world.AccrueIncome(states);

            Assert.Equal(4000.0, supplier.CentralBankRecentIncome, 6); // alpha=1.0 -> exact match to the trade proceeds
        }

        [Fact]
        public void AccrueIncome_RaidCreditLoot_FeedsRecentIncomeOnNextPass()
        {
            var w = TestSupport.World();
            var t = w.Tuning;
            t.InterceptWindowSeconds = 0;   // the on-contact path; the window has its own tests (FAC-307)
            t.CentralBankGrantAmount = 0; // isolate: raid loot only
            t.CentralBankSupportLoanAmount = 0;
            t.CentralBankIncomeEmaPeriods = 1; // alpha = 1.0

            SpatialNode raidHome = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "RH");
            raidHome.OwnerFactionTag = "RAID"; raidHome.HoldStrength = 0.9;

            SpatialNode tradeOrigin = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "TS");
            tradeOrigin.OwnerFactionTag = "TRADE"; tradeOrigin.HoldStrength = 0.9;
            // No inventory -> cargo falls back to CargoValue=TradeCost, forcing the credit-loot path.

            SpatialNode tradeDest = w.Graph.Add(new Vec3(200, 0, 0), 0.8, "TD");
            tradeDest.OwnerFactionTag = "RECV"; tradeDest.HoldStrength = 0.9;

            var stRaid = new FactionState { Tag = "RAID" };
            var stTrade = new FactionState { Tag = "TRADE" };
            var stRecv = new FactionState { Tag = "RECV" };
            var states = new List<FactionState> { stRaid, stTrade, stRecv };

            var tIntent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "RECV", Cost = t.TradeCost };
            PendingFleetAction tradeFleet = PendingFleetResolver.CreatePending(ActionKind.Trade, "TRADE", tIntent, w, t, immediate: true);
            stTrade.PendingFleets.Add(tradeFleet);

            var rIntent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "TD", Cost = t.RaidCost };
            PendingFleetAction raidFleet = PendingFleetResolver.CreatePending(ActionKind.Raid, "RAID", rIntent, w, t, immediate: true);
            stRaid.PendingFleets.Add(raidFleet);

            var log = new List<string>();
            // Seed guarantees interception (roll < RaidInterceptsTradeChance), same as
            // RaidInterceptsTrade_LootsItems_NotCredits in EconomyPhase2Tests.cs.
            PendingFleetResolver.ResolveArrivals(states, w, t, new SeededRandom(0), log);

            long treasuryAfterResolve = w.GetTreasury("RAID");
            Assert.True(treasuryAfterResolve > 0, "credit-loot fallback should have credited the raider's treasury");
            Assert.Equal(0.0, stRaid.CentralBankRecentIncome); // not folded in yet

            w.AccrueIncome(states);

            Assert.Equal((double)treasuryAfterResolve, stRaid.CentralBankRecentIncome, 6); // alpha=1.0, nothing else credited RAID this pass
        }

        [Fact]
        public void AccrueIncome_PendingIncomeAndGrant_BothAccumulateInSamePass()
        {
            var world = TestSupport.World();
            world.Tuning.UpkeepPerSource = 0;
            world.Tuning.DebtInterestRate = 0;
            world.Tuning.CentralBankIncomeEmaPeriods = 1; // alpha = 1.0
            world.Tuning.CentralBankReserveRatio = 1.0;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            world.Tuning.CentralBankGrantAmount = 40000;
            world.Tuning.CentralBankGrantTreasuryBuffer = 50000;
            world.Tuning.CentralBankGrantCooldown = System.TimeSpan.Zero;
            world.Tuning.CentralBankGrantCollateralFraction = 1.0;
            world.Tuning.CentralBankTerritoryNodeValue = 2000000;
            world.Tuning.CentralBankFoundedNodeValue = 0;
            world.Tuning.CentralBankCapabilityLevelValue = 0;
            world.Tuning.CentralBankShipValue = 0;
            world.Tuning.CentralBankSupportLoanAmount = 0;

            var node = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.5;

            var state = new FactionState { Tag = "F" };
            world.SetTreasury("F", -10000);
            world.AddPendingIncome("F", 4000); // simulate trade/raid income accrued before this pass

            world.AccrueIncome(new[] { state });

            // If the grant line still overwrote instead of accumulating, this would equal exactly
            // 40000 (the grant alone), silently dropping the pending 4000.
            Assert.Equal(44000.0, state.CentralBankRecentIncome, 6);
        }
    }
}
