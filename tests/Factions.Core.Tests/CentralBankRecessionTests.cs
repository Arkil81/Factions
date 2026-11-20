using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The measures that keep a war's collateral swing from becoming a server-wide recession:
    /// hulls priced at catalogue cost, a target anchored against falls, a contraction band, a levy on
    /// each faction's excess over its own reserve, and rescues paid out of that levy.</summary>
    [Collection(DebugTracesCollection.Name)]
    public class CentralBankRecessionTests : IDisposable
    {
        public void Dispose() { DebugTraces.ResetToDefaults(); }

        private sealed class CostCatalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, long> _costs;
            public CostCatalogue(Dictionary<string, long> costs) { _costs = costs; }
            public long Cost(string prefabId) { long c; return _costs.TryGetValue(prefabId, out c) ? c : 0; }
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new List<WeaponBand>(); }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new List<string>(); }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        /// <summary>A world whose income pass moves money only through the central bank: no yield,
        /// upkeep, interest, grants, support loans or reserve floor unless a test turns one on.</summary>
        private static WorldModel Quiet()
        {
            WorldModel world = TestSupport.World();
            FactionTuning t = world.Tuning;
            t.UpkeepPerSource = 0;
            t.DebtInterestRate = 0;
            t.LoanAmount = 0;
            t.BaseYield = 0;
            t.OreYield = 0;
            t.ProcessedGoodYield = 0;
            t.UpkeepOre = 0;
            t.UpkeepProcessedGood = 0;
            t.CentralBankGrantAmount = 0;
            t.CentralBankSupportLoanAmount = 0;
            t.CentralBankGrantTreasuryBuffer = 0;
            t.CentralBankGrantRunwayTicks = 0;
            t.CentralBankGrantCollateralFraction = 0;
            t.CentralBankSupportTreasuryBuffer = 0;
            t.CentralBankSupportRunwayTicks = 0;
            t.CentralBankReserveRatio = 1.0;
            t.CentralBankMaxAdjustmentFractionPerTick = 0.1;
            t.CentralBankTerritoryNodeValue = 0;
            t.CentralBankFoundedNodeValue = 0;
            t.CentralBankCapabilityLevelValue = 0;
            t.CentralBankShipValue = 1000;
            t.CentralBankContractionBand = 0;
            return world;
        }

        private static SpatialNode Own(WorldModel world, string id, string owner, double x)
        {
            SpatialNode n = world.Graph.Add(new Vec3(x, 0, 0), 0.5, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.8;
            return n;
        }

        private static long Levied(WorldModel world, string tag)
        {
            return -world.SnapshotCentralBankEvents().Where(e => e.Type == "contraction" && e.FactionTag == tag).Sum(e => e.Amount);
        }

        // ---- Hulls priced at catalogue cost ------------------------------------------------------

        [Fact]
        public void HullCollateral_UsesCatalogueCost_AndTheFlatValueOnlyForAnUnpricedPrefab()
        {
            WorldModel world = Quiet();
            world.Tuning.CentralBankShipValue = 125;
            SpatialNode n = Own(world, "A1", "A", 0);
            n.ShipStock["Frigate"] = 2;
            n.ShipStock["Homebrew"] = 3;

            // No catalogue (the offline sim): every hull at the flat value.
            Assert.Equal(5 * 125, world.GetWorldCollateralValue());

            world.PrefabCatalogue = new CostCatalogue(new Dictionary<string, long> { { "Frigate", 1000000 } });
            Assert.Equal(2 * 1000000 + 3 * 125, world.GetWorldCollateralValue());
        }

        [Fact]
        public void HullCollateral_InFlight_PricesASquadronByMemberAndAConvoyByItsPrefab()
        {
            WorldModel world = Quiet();
            world.PrefabCatalogue = new CostCatalogue(new Dictionary<string, long> { { "Frigate", 1000000 }, { "Drone", 10000 } });
            Own(world, "A1", "A", 0);
            var state = new FactionState { Tag = "A" };
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = "Frigate", ConvoySize = 2,
                Squadron = new List<string> { "Frigate", "Drone" },
            });
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "A", ShipPrefabId = "Drone", ConvoySize = 3,
            });
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "A", ShipPrefabId = "Frigate", ConvoySize = 1, Resolved = true,
            });

            long expected = 1000000 + 10000 + 3 * 10000;
            Assert.Equal(expected, world.GetWorldCollateralValue(new[] { state }));
            Assert.Equal(expected, world.GetFactionCollateralValue("A", state));
        }

        // ---- The anchor ----------------------------------------------------------------------------

        [Fact]
        public void Anchor_AFallReachesTheTargetByHalfLives_ARiseAtOnce()
        {
            WorldModel world = Quiet();
            SpatialNode n = Own(world, "A1", "A", 0);
            n.ShipStock["Hull"] = 10;
            world.AccrueIncome(new[] { new FactionState { Tag = "A" } });   // commits the anchor at 10,000
            Assert.Equal(10000, world.CollateralAnchor);

            n.ShipStock["Hull"] = 4;   // six hulls lost
            CentralBankSnapshot snap = world.SnapshotCentralBank();
            Assert.Equal(4000, snap.WorldCollateralValue);
            Assert.Equal(10000, snap.AnchorCollateralValue);
            Assert.Equal(10000, snap.TargetMoneySupply);

            world.UtcNow += world.Tuning.CentralBankAnchorHalfLife;
            Assert.Equal(7000, world.GetTargetMoneySupply());
            world.UtcNow += world.Tuning.CentralBankAnchorHalfLife;
            Assert.Equal(5500, world.GetTargetMoneySupply());

            n.ShipStock["Hull"] = 12;   // rebuilt past the old level
            Assert.Equal(12000, world.GetTargetMoneySupply());
        }

        /// <summary>The recession itself, and its control. A war faction loses most of its hulls; a
        /// bystander holds cash and no hulls. With the anchor off the target drops with the hulls and
        /// the bystander is levied. With it on, rebuilding inside the half-life means nobody is.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Anchor_AWarRebuiltWithinTheHalfLife_LeviesNobody(bool anchored)
        {
            WorldModel world = Quiet();
            if (!anchored) world.Tuning.CentralBankAnchorHalfLife = TimeSpan.Zero;
            SpatialNode war = Own(world, "W1", "W", 0);
            war.ShipStock["Hull"] = 10;
            Own(world, "B1", "B", 100);
            world.SetTreasury("W", 1000);
            world.SetTreasury("B", 8000);
            var states = new[] { new FactionState { Tag = "W" }, new FactionState { Tag = "B" } };

            world.AccrueIncome(states);   // money 9,000 under a 10,000 target: nothing to levy
            war.ShipStock["Hull"] = 4;
            for (int pass = 0; pass < 6; pass++)
            {
                world.UtcNow += TimeSpan.FromMinutes(10);
                world.AccrueIncome(states);
                war.ShipStock["Hull"] += 1;
            }

            if (anchored)
            {
                Assert.Equal(0, Levied(world, "B"));
                Assert.Equal(8000, world.GetTreasury("B"));
            }
            else
            {
                Assert.True(Levied(world, "B") > 0);
                Assert.True(world.GetTreasury("B") < 8000);
            }
        }

        [Fact]
        public void Anchor_RoundTripsThroughTheStore()
        {
            WorldModel world = Quiet();
            SpatialNode n = Own(world, "A1", "A", 0);
            n.ShipStock["Hull"] = 10;
            world.AccrueIncome(new[] { new FactionState { Tag = "A" } });

            string dir = Path.Combine(Path.GetTempPath(), "cb-anchor-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveCentralBankState(world);

                WorldModel restored = Quiet();
                store.LoadCentralBankState(restored);
                Assert.Equal(world.CollateralAnchor, restored.CollateralAnchor);
                Assert.Equal(world.CollateralAnchorUtc, restored.CollateralAnchorUtc);

                // A restart in the dip keeps the war's collateral in the target.
                SpatialNode m = Own(restored, "A1", "A", 0);
                m.ShipStock["Hull"] = 4;
                restored.UtcNow = world.UtcNow;
                Assert.Equal(10000, restored.GetTargetMoneySupply());
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // ---- The band ------------------------------------------------------------------------------

        [Fact]
        public void Band_MoneyInsideTheBandIsHeld_AboveItOnlyTheExcessBeyondTheEdgeIsLevied()
        {
            WorldModel world = Quiet();
            world.Tuning.CentralBankTerritoryNodeValue = 1000;
            world.Tuning.CentralBankContractionBand = 0.05;   // edge at 1,050
            Own(world, "A1", "A", 0);
            var states = new[] { new FactionState { Tag = "A" } };

            world.SetTreasury("A", 1040);
            world.AccrueIncome(states);
            Assert.Equal(1040, world.GetTreasury("A"));
            Assert.Equal("hold", world.SnapshotCentralBank().LastPolicyMode);

            world.SetTreasury("A", 1200);   // 150 beyond the edge; 10% of that per pass
            world.AccrueIncome(states);
            Assert.Equal(1185, world.GetTreasury("A"));
            CentralBankSnapshot snap = world.SnapshotCentralBank();
            Assert.Equal("contract", snap.LastPolicyMode);
            Assert.Equal(15, snap.LastPolicyLevied);
        }

        // ---- The levy ------------------------------------------------------------------------------

        /// <summary>B's cash equals its reserve-ratio share of its own assets; X holds four times its
        /// share. Under the old rule (pro rata on all treasury) B would have paid a sixth of the levy.</summary>
        [Fact]
        public void Levy_SparesAFactionAtItsOwnReserve_AndFallsOnTheExcessHolder()
        {
            WorldModel world = Quiet();
            world.Tuning.CentralBankReserveRatio = 0.5;
            world.Tuning.CentralBankTerritoryNodeValue = 2000;
            Own(world, "B1", "B", 0);
            Own(world, "X1", "X", 100);
            world.SetTreasury("B", 1000);
            world.SetTreasury("X", 5000);

            // Target 0.5 x 4,000 = 2,000 against 6,000 held: 10% of the 4,000 excess is 400.
            world.AccrueIncome(new[] { new FactionState { Tag = "B" }, new FactionState { Tag = "X" } });

            Assert.Equal(1000, world.GetTreasury("B"));
            Assert.Equal(4600, world.GetTreasury("X"));
            Assert.Equal(0, Levied(world, "B"));
            Assert.Equal(400, Levied(world, "X"));
        }

        [Fact]
        public void Levy_NeverTakesAFactionBelowItsGrantTarget()
        {
            WorldModel world = Quiet();
            world.Tuning.CentralBankGrantTreasuryBuffer = 4900;
            Own(world, "X1", "X", 0);
            world.SetTreasury("X", 5000);

            // Collateral 0, so the target is 0 and the whole 5,000 is excess -- but only 100 sits
            // above the treasury the bank would grant X back up to.
            world.AccrueIncome(new[] { new FactionState { Tag = "X" } });

            Assert.Equal(4900, world.GetTreasury("X"));
        }

        // ---- Rescues during contraction ------------------------------------------------------------

        /// <summary>Before: a negative gap zeroed the expansion budget, so P was denied its grant for
        /// as long as the world sat above target. Now the grant is paid from the same pass's levy on
        /// X, and the money supply still falls by the contraction share.</summary>
        [Fact]
        public void Rescue_DuringContraction_IsPaidFromTheLevy()
        {
            WorldModel world = Quiet();
            world.Tuning.CentralBankTerritoryNodeValue = 100;
            world.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0.01;
            world.Tuning.CentralBankGrantAmount = 50;
            world.Tuning.CentralBankGrantTreasuryBuffer = 100;
            world.Tuning.CentralBankGrantCollateralFraction = 1.0;
            Own(world, "P1", "P", 0);
            Own(world, "X1", "X", 100);
            world.SetTreasury("P", 20);
            world.SetTreasury("X", 10000);
            long before = world.GetCurrentMoneySupply();

            // Target 200 against 10,020: contraction asks ceil(1% x 9,820) = 99; P's grant asks 50.
            world.AccrueIncome(new[] { new FactionState { Tag = "P" }, new FactionState { Tag = "X" } });

            Assert.Equal(70, world.GetTreasury("P"));
            Assert.Equal(10000 - 149, world.GetTreasury("X"));
            Assert.Equal(before - 99, world.GetCurrentMoneySupply());
            Assert.Contains(world.SnapshotCentralBankEvents(), e => e.Type == "grant" && e.FactionTag == "P" && e.Amount == 50);
            CentralBankSnapshot snap = world.SnapshotCentralBank();
            Assert.Equal("contract", snap.LastPolicyMode);
            Assert.Equal(149, snap.LastPolicyLevied);
            Assert.Equal(50, snap.LastPolicyRescued);
        }

        [Fact]
        public void Rescue_InsideTheBand_IsStillPaid_AndLeavesMoneySupplyUnchanged()
        {
            WorldModel world = Quiet();
            world.Tuning.CentralBankTerritoryNodeValue = 5000;
            world.Tuning.CentralBankContractionBand = 0.5;
            world.Tuning.CentralBankGrantAmount = 50;
            world.Tuning.CentralBankGrantTreasuryBuffer = 100;
            world.Tuning.CentralBankGrantCollateralFraction = 0.01;   // caps a grant at 50, under the 100 buffer
            Own(world, "P1", "P", 0);
            Own(world, "X1", "X", 100);
            world.SetTreasury("P", 20);
            world.SetTreasury("X", 10500);
            long before = world.GetCurrentMoneySupply();

            // Target 10,000, edge 15,000, money 10,520: no contraction, but P still gets its grant.
            world.AccrueIncome(new[] { new FactionState { Tag = "P" }, new FactionState { Tag = "X" } });

            Assert.Equal("hold", world.SnapshotCentralBank().LastPolicyMode);
            Assert.Equal(70, world.GetTreasury("P"));
            Assert.Equal(10450, world.GetTreasury("X"));
            Assert.Equal(before, world.GetCurrentMoneySupply());
        }
    }
}
