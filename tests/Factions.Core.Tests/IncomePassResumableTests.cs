using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The resumable form of <see cref="WorldModel.AccrueIncome"/> (WorldModel.IncomePass.cs):
    /// driving the pass one bounded unit at a time must land in exactly the same world state as the
    /// synchronous call, must charge every faction exactly once, and must not expose a half-filled
    /// net-flow/ledger/fleet-power cache to the readers that run between frames.</summary>
    public class IncomePassResumableTests
    {
        private sealed class ArmedPrefabCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return prefabId == "Gunship"
                    ? new[] { new WeaponBand { Dps = 100, RangeM = 800 } }
                    : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
            public double LiftThrustNewtons(string prefabId, bool atmosphericOnly) { return 0; }
        }

        // ---- fixtures ------------------------------------------------------------------------

        /// <summary>Four factions of differing size, one indebted (interest + auto-repay), one below
        /// the insolvency floor with a net-negative node (clamp + force-shed), processed-good surplus
        /// on two of them (asset purchases), ship stock (fleet power), and a positive money gap so the
        /// expansionary central-bank path runs.</summary>
        private static WorldModel Expansionary(out FactionState[] states)
        {
            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = new ArmedPrefabCatalogue();
            w.Tuning.CentralBankReserveRatio = 1.0;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 1.0;
            w.Tuning.CentralBankBuyMinLot = 1;
            w.Tuning.CentralBankBuyScarcityFloor = 1.0;
            w.Tuning.DemandPressureFloorProcessedGood = 0;

            AddNode(w, "A1", "A", new Vec3(0, 0, 0), station: 1, cap: 1);
            // A depot holding surplus plates, not a factory holding its own output: a Manufactory
            // carries a reference for the components it produces, so its own stock is a buffer rather
            // than the glut this fixture needs the bank to buy.
            SpatialNode a2 = AddNode(w, "A2", "A", new Vec3(20, 0, 0), station: 2, cap: 1,
                kind: NodeCapabilityKind.Refinery);
            a2.Inventory["SteelPlate"] = 500;
            a2.ShipStock["Gunship"] = 3;

            SpatialNode b1 = AddNode(w, "B1", "B", new Vec3(400, 0, 0), station: 3, cap: 2);
            b1.Inventory["SteelPlate"] = 150;
            b1.Inventory["Ore:Iron"] = 400;

            AddNode(w, "C1", "C", new Vec3(800, 0, 0), station: 4, cap: 1);
            AddNode(w, "D1", "D", new Vec3(1200, 0, 0), station: 5, cap: 1);

            w.SetTreasury("A", 250000);
            w.SetTreasury("B", 4000);
            w.SetTreasury("C", -50);           // pushed under the floor by upkeep -> clamp/shed path
            w.SetTreasury("D", 90000);
            w.SetDebt("B", 30000);             // interest accrual
            w.SetDebt("D", 5000);              // interest + auto-repay from surplus

            w.AddPendingIncome("A", 7000);     // trade settlement since the last pass
            w.AddPendingExpense("D", 2500);    // dispatch costs since the last pass

            states = new[]
            {
                new FactionState { Tag = "A", CentralBankRecentIncome = 1200 },
                new FactionState { Tag = "B", CentralBankRecentIncome = 0, CentralBankSupportDebt = 1000 },
                new FactionState { Tag = "C" },
                new FactionState { Tag = "D", CentralBankRecentIncome = 400 },
            };
            return w;
        }

        /// <summary>Money supply well above target, so the contraction levy runs across the roster.</summary>
        private static WorldModel Contractionary(out FactionState[] states)
        {
            WorldModel w = TestSupport.World();
            w.PrefabCatalogue = new ArmedPrefabCatalogue();
            w.Tuning.CentralBankReserveRatio = 0.01;
            w.Tuning.CentralBankMaxAdjustmentFractionPerTick = 0.5;

            AddNode(w, "A1", "A", new Vec3(0, 0, 0), station: 1, cap: 1);
            AddNode(w, "B1", "B", new Vec3(400, 0, 0), station: 2, cap: 1);
            AddNode(w, "C1", "C", new Vec3(800, 0, 0), station: 3, cap: 1);

            w.SetTreasury("A", 5000000);
            w.SetTreasury("B", 2000000);
            w.SetTreasury("C", 800000);

            states = new[]
            {
                new FactionState { Tag = "A" }, new FactionState { Tag = "B" }, new FactionState { Tag = "C" },
            };
            return w;
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos, long station, int cap,
            NodeCapabilityKind kind = NodeCapabilityKind.Manufactory)
        {
            SpatialNode n = w.Graph.Add(pos, 0.8, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.8;
            n.StableStationId = station;
            n.Capabilities[kind] = cap;
            return n;
        }

        private static int RunChunked(WorldModel w, IList<FactionState> states)
        {
            w.BeginIncomePass(states);
            int steps = 0;
            while (!w.AdvanceIncomePass())
            {
                steps++;
                Assert.True(steps < 100000, "income pass did not terminate");
            }
            return steps + 1;
        }

        private static void AssertSameWorld(WorldModel expected, FactionState[] expectedStates,
                                            WorldModel actual, FactionState[] actualStates)
        {
            foreach (string tag in new[] { "A", "B", "C", "D" })
            {
                Assert.Equal(expected.GetTreasury(tag), actual.GetTreasury(tag));
                Assert.Equal(expected.GetDebt(tag), actual.GetDebt(tag));
                Assert.Equal(expected.GetLedger(tag), actual.GetLedger(tag));
                Assert.Equal(expected.GetNetFlow(tag), actual.GetNetFlow(tag));
                Assert.Equal(expected.GetFleetCombatPower(tag), actual.GetFleetCombatPower(tag));
            }
            Assert.Equal(expected.GetCurrentMoneySupply(), actual.GetCurrentMoneySupply());
            Assert.Equal(expected.GetTargetMoneySupply(), actual.GetTargetMoneySupply());

            foreach (string id in expected.Graph.Nodes.Keys)
            {
                SpatialNode e = expected.Graph.Nodes[id];
                SpatialNode a = actual.Graph.Nodes[id];
                Assert.Equal(e.OwnerFactionTag, a.OwnerFactionTag);
                Assert.Equal(e.HoldStrength, a.HoldStrength, 9);
                Assert.Equal(e.Inventory.Count, a.Inventory.Count);
                foreach (var kv in e.Inventory)
                    Assert.Equal(kv.Value, a.Inventory[kv.Key]);
            }

            for (int i = 0; i < expectedStates.Length; i++)
            {
                Assert.Equal(expectedStates[i].CentralBankRecentIncome, actualStates[i].CentralBankRecentIncome, 9);
                Assert.Equal(expectedStates[i].CentralBankSupportDebt, actualStates[i].CentralBankSupportDebt);
            }

            // The monetary event stream encodes the whole control flow: same events, same order,
            // same amounts, or the two paths diverged somewhere.
            IList<MonetaryEvent> ee = expected.SnapshotMonetaryEvents();
            IList<MonetaryEvent> ae = actual.SnapshotMonetaryEvents();
            Assert.Equal(ee.Count, ae.Count);
            for (int i = 0; i < ee.Count; i++)
            {
                Assert.Equal(ee[i].Scope, ae[i].Scope);
                Assert.Equal(ee[i].Type, ae[i].Type);
                Assert.Equal(ee[i].FactionTag, ae[i].FactionTag);
                Assert.Equal(ee[i].Amount, ae[i].Amount);
                Assert.Equal(ee[i].TreasuryAfter, ae[i].TreasuryAfter);
                Assert.Equal(ee[i].DebtAfter, ae[i].DebtAfter);
                Assert.Equal(ee[i].MoneySupplyAfter, ae[i].MoneySupplyAfter);
                Assert.Equal(ee[i].MoneySupplyGapAfter, ae[i].MoneySupplyGapAfter);
            }
        }

        // ---- equivalence ----------------------------------------------------------------------

        [Fact]
        public void ChunkedPass_MatchesSynchronousPass_Expansionary()
        {
            FactionState[] syncStates, chunkStates;
            WorldModel sync = Expansionary(out syncStates);
            WorldModel chunked = Expansionary(out chunkStates);

            sync.AccrueIncome(syncStates);
            RunChunked(chunked, chunkStates);

            AssertSameWorld(sync, syncStates, chunked, chunkStates);

            // The fixture must actually exercise the expansionary paths it exists to cover.
            Assert.Contains(sync.SnapshotMonetaryEvents(), e => e.Type == "interest");
            Assert.Contains(sync.SnapshotMonetaryEvents(), e => e.Type == "asset-purchase");
        }

        [Fact]
        public void ChunkedPass_MatchesSynchronousPass_Contractionary()
        {
            FactionState[] syncStates, chunkStates;
            WorldModel sync = Contractionary(out syncStates);
            WorldModel chunked = Contractionary(out chunkStates);

            sync.AccrueIncome(syncStates);
            RunChunked(chunked, chunkStates);

            AssertSameWorld(sync, syncStates, chunked, chunkStates);
            Assert.Contains(sync.SnapshotMonetaryEvents(), e => e.Type == "contraction");
        }

        [Fact]
        public void ChunkedPass_MatchesSynchronousPass_AcrossRepeatedPasses()
        {
            FactionState[] syncStates, chunkStates;
            WorldModel sync = Expansionary(out syncStates);
            WorldModel chunked = Expansionary(out chunkStates);

            for (int pass = 0; pass < 5; pass++)
            {
                sync.AccrueIncome(syncStates);
                RunChunked(chunked, chunkStates);
                AssertSameWorld(sync, syncStates, chunked, chunkStates);
            }
        }

        // ---- cursor mechanics ------------------------------------------------------------------

        [Fact]
        public void AdvanceIncomePass_ReturnsTrueExactlyOnce_AndTakesManyBoundedUnits()
        {
            FactionState[] states;
            WorldModel w = Expansionary(out states);

            w.BeginIncomePass(states);
            int steps = 0;
            int trues = 0;
            while (steps < 100000)
            {
                steps++;
                if (w.AdvanceIncomePass()) { trues++; break; }
            }

            Assert.Equal(1, trues);
            Assert.False(w.IsIncomePassInProgress());
            // One unit per node plus one per faction per roster phase, not one unit for the whole pass.
            Assert.True(steps > w.Graph.Nodes.Count + states.Length, "pass did not actually chunk: " + steps + " steps");
            // Idle passes report complete rather than throwing, so a host resuming a phase whose
            // WorldModel was replaced underneath it simply skips it.
            Assert.True(w.AdvanceIncomePass());
        }

        [Fact]
        public void ChargesUpkeepAndInterestExactlyOncePerPass()
        {
            FactionState[] states;
            WorldModel w = Expansionary(out states);
            w.Tuning.CentralBankReserveRatio = 0;   // no grants/loans/purchases: isolate the charges

            long debtBefore = w.GetDebt("B");
            RunChunked(w, states);

            Assert.Equal(1, w.SnapshotDebtEvents().Count(e => e.Type == "interest" && e.FactionTag == "B"));
            long interest = Math.Max(1L, (long)(debtBefore * w.Tuning.DebtInterestRate));
            Assert.Equal(debtBefore + interest, w.GetDebt("B"));
        }

        [Fact]
        public void PartialPass_ChargesOnlyTheFactionsTheCursorHasReached()
        {
            FactionState[] states;
            WorldModel w = Expansionary(out states);
            w.Tuning.CentralBankReserveRatio = 0;

            long before = w.GetTreasury("D");
            w.BeginIncomePass(states);
            while (w.DiagnosticIncomePassPhase != "ChargingFactions") w.AdvanceIncomePass();

            // Not one faction charged yet, however far the mint phase has run.
            Assert.Equal(before, w.GetTreasury("D"));

            int charged = 0;
            while (w.DiagnosticIncomePassPhase == "ChargingFactions" && charged < 2)
            {
                w.AdvanceIncomePass();
                charged = w.DiagnosticIncomePassIndex;
            }
            Assert.True(charged >= 1);
            w.AbandonIncomePass();
        }

        [Fact]
        public void AccrueIncome_FinishesAnInFlightPass_InsteadOfRestartingIt()
        {
            FactionState[] finishStates, refStates;
            WorldModel finish = Expansionary(out finishStates);
            WorldModel reference = Expansionary(out refStates);

            finish.BeginIncomePass(finishStates);
            while (finish.DiagnosticIncomePassPhase != "ChargingFactions") finish.AdvanceIncomePass();
            finish.AdvanceIncomePass();                 // charge one faction, then hand over
            finish.AccrueIncome(finishStates);          // must resume, not start a second pass

            reference.AccrueIncome(refStates);
            AssertSameWorld(reference, refStates, finish, finishStates);
        }

        [Fact]
        public void AbandonIncomePass_KeepsAppliedChargesAndPublishesNothing()
        {
            FactionState[] states;
            WorldModel w = Expansionary(out states);

            w.AccrueIncome(states);                     // a completed pass to publish caches from
            long publishedNetFlow = w.GetNetFlow("A");
            long publishedLedger = w.GetLedger("A");

            w.BeginIncomePass(states);
            while (w.DiagnosticIncomePassPhase != "ChargingFactions") w.AdvanceIncomePass();
            w.AdvanceIncomePass();
            long treasuryMidPass = w.GetTreasury("A");
            w.AbandonIncomePass();

            Assert.False(w.IsIncomePassInProgress());
            Assert.Equal(publishedNetFlow, w.GetNetFlow("A"));   // nothing published
            Assert.Equal(publishedLedger, w.GetLedger("A"));
            Assert.Equal(treasuryMidPass, w.GetTreasury("A"));   // charges already applied stay applied
        }

        // ---- cache publication -------------------------------------------------------------------

        [Fact]
        public void Caches_KeepPreviousPassValues_UntilTheFinalUnitPublishes()
        {
            FactionState[] states;
            WorldModel w = Expansionary(out states);

            w.AccrueIncome(states);
            var netFlowBefore = new Dictionary<string, long>();
            var ledgerBefore = new Dictionary<string, long>();
            var powerBefore = new Dictionary<string, long>();
            foreach (string tag in new[] { "A", "B", "C", "D" })
            {
                netFlowBefore[tag] = w.GetNetFlow(tag);
                ledgerBefore[tag] = w.GetLedger(tag);
                powerBefore[tag] = w.GetFleetCombatPower(tag);
            }

            // Something a reader would notice if it were served mid-pass state.
            w.Graph.Nodes["A2"].ShipStock["Gunship"] = 9;

            w.BeginIncomePass(states);
            bool done = false;
            int guard = 0;
            while (!done && guard++ < 100000)
            {
                foreach (string tag in new[] { "A", "B", "C", "D" })
                {
                    Assert.Equal(netFlowBefore[tag], w.GetNetFlow(tag));
                    Assert.Equal(ledgerBefore[tag], w.GetLedger(tag));
                    Assert.Equal(powerBefore[tag], w.GetFleetCombatPower(tag));
                }
                done = w.AdvanceIncomePass();
            }

            Assert.True(done);
            Assert.Equal(9 * 100, w.GetFleetCombatPower("A"));   // published only now
        }

        [Fact]
        public void RepaymentFloor_ReadsThisPassNetFlow_NotThePublishedCache()
        {
            // ComputeCentralBankTreasuryTarget reads GetNetFlow while the pass is running. The
            // monolithic body cleared the cache at pass start, so that read saw 0 during charging;
            // a resumable pass that published its buffer early would substitute last pass's value and
            // silently change repayment/grant sizing. Two passes in a row must therefore charge and
            // repay identically to two independent single passes.
            FactionState[] twoPassStates, oneThenOneStates;
            WorldModel twoPass = Expansionary(out twoPassStates);
            WorldModel oneThenOne = Expansionary(out oneThenOneStates);

            twoPass.AccrueIncome(twoPassStates);
            twoPass.AccrueIncome(twoPassStates);

            RunChunked(oneThenOne, oneThenOneStates);
            RunChunked(oneThenOne, oneThenOneStates);

            AssertSameWorld(twoPass, twoPassStates, oneThenOne, oneThenOneStates);
        }

        // ---- mid-pass world mutation ---------------------------------------------------------------

        [Fact]
        public void NodeAddedMidPass_IsNotMintedUntilTheNextPass()
        {
            FactionState[] states;
            WorldModel w = Expansionary(out states);

            w.BeginIncomePass(states);
            w.AdvanceIncomePass();   // start minting

            // The late node needs ground under it to mint anything at all: since FAC-233 retired the
            // passive gravel channel, every remaining yield channel is gated on a deposit.
            SpatialNode late = TestSupport.WithOre(
                AddNode(w, "A9", "A", new Vec3(60, 0, 0), station: 9, cap: 1), "Iron");
            while (!w.AdvanceIncomePass()) { }

            Assert.Empty(late.Inventory);

            w.AccrueIncome(states);
            Assert.NotEmpty(late.Inventory);
        }

        [Fact]
        public void NodeRemovedMidPass_IsSkippedWithoutThrowing()
        {
            FactionState[] states;
            WorldModel w = Expansionary(out states);

            w.BeginIncomePass(states);
            w.AdvanceIncomePass();
            w.Graph.Nodes.Remove("D1");

            while (!w.AdvanceIncomePass()) { }
            Assert.False(w.IsIncomePassInProgress());
        }

        [Fact]
        public void FactionLosingAllTerritoryMidPass_StillCompletesThePass()
        {
            FactionState[] states;
            WorldModel w = Expansionary(out states);

            w.BeginIncomePass(states);
            while (w.DiagnosticIncomePassPhase != "ChargingFactions") w.AdvanceIncomePass();
            w.Withdraw("D", "D1");

            while (!w.AdvanceIncomePass()) { }
            Assert.False(w.IsIncomePassInProgress());
            Assert.Equal(NeutralFaction.Tag, w.Graph.Nodes["D1"].OwnerFactionTag);
        }

        [Fact]
        public void SpendAfterThePendingsDrain_LandsInTheNextPass()
        {
            // The pendings drain is one atomic step, so a manager's mid-pass Spend moves treasury
            // immediately but is accounted in the *next* pass's net flow -- exactly as it was when the
            // whole pass was one synchronous call and the spend could only land between passes.
            FactionState[] spendStates, controlStates;
            WorldModel spend = Expansionary(out spendStates);
            WorldModel control = Expansionary(out controlStates);
            spend.Tuning.CentralBankReserveRatio = 0;
            control.Tuning.CentralBankReserveRatio = 0;

            spend.BeginIncomePass(spendStates);
            control.BeginIncomePass(controlStates);
            while (spend.DiagnosticIncomePassPhase != "ChargingFactions") spend.AdvanceIncomePass();
            while (control.DiagnosticIncomePassPhase != "ChargingFactions") control.AdvanceIncomePass();

            long treasuryBefore = spend.GetTreasury("A");
            spend.Spend("A", 1234);
            Assert.Equal(treasuryBefore - 1234, spend.GetTreasury("A"));   // treasury moves immediately

            while (!spend.AdvanceIncomePass()) { }
            while (!control.AdvanceIncomePass()) { }
            Assert.Equal(control.GetNetFlow("A"), spend.GetNetFlow("A"));  // not this pass's expense

            RunChunked(spend, spendStates);
            RunChunked(control, controlStates);
            Assert.Equal(control.GetNetFlow("A") - 1234, spend.GetNetFlow("A"));   // next pass's, in full
        }

        // ---- the owned-node index agrees with a full graph walk --------------------------------------

        [Fact]
        public void OwnedNodeIndex_AgreesWithTheFullGraphWalk()
        {
            FactionState[] states;
            WorldModel w = Expansionary(out states);

            // Outside a pass these walk the whole graph; inside one they are served from the pass's
            // owned-node index. Both must produce identical answers.
            var collateralFull = new Dictionary<string, long>();
            foreach (string tag in new[] { "A", "B", "C", "D" })
                collateralFull[tag] = w.GetFactionCollateralValue(tag);
            string shedFull = w.ForceShedWorstDrain("C");
            Assert.Null(shedFull);   // nothing net-negative yet; the answer still has to match below

            w.BeginIncomePass(states);
            while (w.DiagnosticIncomePassPhase == "MintingNodes") w.AdvanceIncomePass();

            foreach (string tag in new[] { "A", "B", "C", "D" })
                Assert.Equal(collateralFull[tag], w.GetFactionCollateralValue(tag));
            Assert.Equal(shedFull, w.ForceShedWorstDrain("C"));
            w.AbandonIncomePass();
        }

        [Fact]
        public void FleetPower_PublishedByThePass_SumsOwnedShipStock()
        {
            FactionState[] states;
            WorldModel w = Expansionary(out states);

            RunChunked(w, states);

            Assert.Equal(3 * 100, w.GetFleetCombatPower("A"));
            Assert.Equal(0, w.GetFleetCombatPower("B"));
        }
    }
}
