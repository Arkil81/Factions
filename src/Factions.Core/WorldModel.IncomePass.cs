using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// The resumable form of <see cref="WorldModel.AccrueIncome"/>: the same pass, the same total
    /// order, the same arithmetic -- only frame boundaries are new. `AccrueIncome` itself stays the
    /// synchronous run-to-completion entry point every offline caller depends on (Factions.Sim,
    /// FactionRuntime.Step, the Core test suite), exactly as FactionRuntime.Flush() wraps its own
    /// phase stepper.
    ///
    /// Three properties make this safe to checkpoint, and each is load-bearing:
    ///
    /// 1. **The money-supply barrier is its own phase.** MoneySupplyBarrier is unreachable until the
    ///    ChargingFactions cursor is exhausted, so GetCurrentMoneySupply() still sums a fully-charged
    ///    roster -- the property a naive "one faction of the whole pass per step" cursor would have
    ///    destroyed.
    /// 2. **Cursors are monotone and tag-keyed.** No phase ever rewinds, and there is deliberately no
    ///    staleness check and no abandon-and-restart path (unlike InfluenceField's snapshot rebuild,
    ///    which can restart precisely because it only mutates a private buffer). An income pass
    ///    mutates real ledgers as it goes, so a restart would re-charge already-charged factions.
    ///    Tags rather than indices into `states` mean a roster change between frames cannot misindex.
    /// 3. **The three published caches are double-buffered.** _cachedNetFlow/_cachedLedger/
    ///    _cachedFleetPower are filled into build buffers and published as three reference
    ///    assignments in the final step, so a brain tick or manager running between frames always
    ///    reads the last fully-completed pass, never a half-filled cache. Reads *inside* the pass go
    ///    through GetNetFlowForIncomePass, which reproduces the monolithic body's
    ///    "cleared at pass start" semantics exactly.
    ///
    /// What is observable mid-pass, and accepted: treasury, debt and territory changes land per
    /// faction as the cursor advances, and external actors (brain Enact, FleetManager/TradeManager
    /// spends, SE store transactions, contract payouts) move treasuries while the pass is in flight.
    /// Each faction's own charge is applied inside one uninterrupted step, so nothing observes a torn
    /// faction; what is exposed is skew between factions, bounded by one pass. The MoneyGap and
    /// ExpansionBudget computed at the barrier are consumed some frames later, so the bank may inject
    /// marginally more or less than the instantaneous gap warrants -- ComputeCentralBankAdjustmentBudget
    /// is a fractional proportional controller that self-corrects on the next pass. See
    /// docs/economy-territory.md.
    public sealed partial class WorldModel
    {
        private enum IncomePassPhase
        {
            MintingNodes,           // one node per step
            DrainingPendings,       // one atomic step
            CachingFleetPower,      // one faction per step
            ChargingFactions,       // one faction per step
            MoneySupplyBarrier,     // one atomic step -- the whole-roster barrier
            AssessingLevy,          // one faction per step; entered only when the gap is not positive
            Contracting,            // one faction per step; entered only when the assessment found a levy to take
            IssuingCredit,          // one faction per step
            SelectingAssetSellers,  // one faction per step
            BuyingAssets,           // one eligible seller per step
            FoldingIncomeEma,       // one faction per step
            Publishing,             // one atomic step: swap the build buffers into the live caches
        }

        /// <summary>Everything that is a local variable in the monolithic body, promoted to pass
        /// state so it survives a frame boundary.</summary>
        private sealed class IncomePassState
        {
            internal IncomePassPhase Phase;
            internal IList<FactionState> States;

            // Phase 1 cursor. The node order is frozen at Begin, mirroring InfluenceField's
            // _buildOrder: a node added mid-pass is minted next pass, a removed one is skipped.
            internal List<string> NodeOrder;
            internal int NodeIndex;
            internal readonly HashSet<string> Owners = new HashSet<string>();

            // Owned-node index, accumulated for free during the mint walk. Every later per-faction
            // sweep iterates its own faction's nodes through it instead of the whole graph, which is
            // what turns ~6 O(factions x nodes) walks per pass into O(nodes). Membership is still
            // re-checked live per node (see OwnedNodesForPass), so the index can only ever miss a
            // node acquired after minting -- never count one the faction no longer owns.
            internal readonly Dictionary<string, List<string>> NodeIdsByTag = new Dictionary<string, List<string>>();
            internal bool OwnedNodeIndexReady;

            // Roster cursor, reset on entry to each per-faction phase.
            internal List<string> Tags;
            internal int TagIndex;

            internal Dictionary<string, long> ExpenseThisPassByTag;
            internal Dictionary<string, long> IncomeThisPassByTag;
            internal Dictionary<string, long> PendingIncomeByTag;
            internal readonly Dictionary<string, FactionState> StateByTag = new Dictionary<string, FactionState>();
            internal readonly Dictionary<string, long> TreasuryAtLedgerByTag = new Dictionary<string, long>();

            internal long MoneyGap;
            internal long ExpansionBudget;
            internal string PolicyMode = "";

            // Non-positive gap only. ContractionBudget is what the excess beyond the band asks for;
            // RescueDemand is what the factions below their own reserve would draw in grants and
            // support loans. Both are paid from one levy on treasury held above each faction's own
            // reserve (LevyBaseByTag), so a rescue during contraction moves money rather than mints it.
            internal long ContractionBudget;
            internal long RescueDemand;
            internal readonly Dictionary<string, long> LevyBaseByTag = new Dictionary<string, long>();
            internal long LevyBase;
            internal long LevyBudget;
            internal long LevyRemaining;
            internal long Levied;
            internal long Rescued;

            internal List<string> EligibleSellers;
            internal int SellerIndex;

            // Build buffers. BuildLedger doubles as the monolithic body's `ledgerByTag` local, which
            // always held the same values as _cachedLedger.
            internal readonly Dictionary<string, long> BuildNetFlow = new Dictionary<string, long>();
            internal readonly Dictionary<string, long> BuildLedger = new Dictionary<string, long>();
            internal readonly Dictionary<string, long> BuildFleetPower = new Dictionary<string, long>();

            // World collateral memo. GetTargetMoneySupply() is an O(nodes) walk and AppendMonetaryEvent
            // stamps it on every emitted event; memoising it per pass replaces dozens of walks with one.
            // Invalidated whenever the pass itself changes territory (force-shed). Fleets dispatched
            // mid-pass are deliberately NOT an invalidation: every event in one pass should be stamped
            // with the same target, and the in-flight term is a fraction of a percent of the total.
            internal bool WorldCollateralKnown;
            internal long WorldCollateral;
        }

        private IncomePassState _incomePass;

        /// <summary>True while a resumable income pass is in flight.</summary>
        public bool IsIncomePassInProgress() { return _incomePass != null; }

        // Diagnostics only, mirroring DiagnosticSnapshotBuildIndex/Total.
        public string DiagnosticIncomePassPhase
        {
            get { return _incomePass == null ? "idle" : _incomePass.Phase.ToString(); }
        }
        public int DiagnosticIncomePassIndex
        {
            get
            {
                IncomePassState pass = _incomePass;
                if (pass == null) return 0;
                if (pass.Phase == IncomePassPhase.MintingNodes) return pass.NodeIndex;
                if (pass.Phase == IncomePassPhase.BuyingAssets) return pass.SellerIndex;
                return pass.TagIndex;
            }
        }
        public int DiagnosticIncomePassTotal
        {
            get
            {
                IncomePassState pass = _incomePass;
                if (pass == null) return 0;
                if (pass.Phase == IncomePassPhase.MintingNodes) return pass.NodeOrder == null ? 0 : pass.NodeOrder.Count;
                if (pass.Phase == IncomePassPhase.BuyingAssets) return pass.EligibleSellers == null ? 0 : pass.EligibleSellers.Count;
                return pass.Tags == null ? 0 : pass.Tags.Count;
            }
        }

        /// <summary>Starts a resumable income pass. Drive it with <see cref="AdvanceIncomePass"/>
        /// until that returns true; there is no restart path (see this file's header).</summary>
        public void BeginIncomePass(IList<FactionState> states = null)
        {
            IncomePassState pass = new IncomePassState();
            pass.Phase = IncomePassPhase.MintingNodes;
            pass.States = states;
            pass.NodeOrder = new List<string>(Graph.Nodes.Keys);
            _incomePass = pass;
        }

        /// <summary>Runs exactly one bounded unit. Returns true once the pass has completed and
        /// published -- and immediately if no pass is in flight, so a host whose phase machine
        /// outlived its WorldModel simply skips the phase instead of throwing. Never checks a
        /// deadline of its own: the caller admits as many units as its frame budget allows.</summary>
        public bool AdvanceIncomePass()
        {
            IncomePassState pass = _incomePass;
            if (pass == null) return true;

            switch (pass.Phase)
            {
                case IncomePassPhase.MintingNodes:          StepMintOneNode(pass);              return false;
                case IncomePassPhase.DrainingPendings:      StepDrainPendings(pass);            return false;
                case IncomePassPhase.CachingFleetPower:     StepCacheOneFleetPower(pass);       return false;
                case IncomePassPhase.ChargingFactions:      StepChargeOneFaction(pass);         return false;
                case IncomePassPhase.MoneySupplyBarrier:    StepMoneySupplyBarrier(pass);       return false;
                case IncomePassPhase.AssessingLevy:         StepAssessOneLevy(pass);            return false;
                case IncomePassPhase.Contracting:           StepContractOneFaction(pass);       return false;
                case IncomePassPhase.IssuingCredit:         StepIssueCreditToOneFaction(pass);  return false;
                case IncomePassPhase.SelectingAssetSellers: StepSelectOneAssetSeller(pass);     return false;
                case IncomePassPhase.BuyingAssets:          StepBuyFromOneSeller(pass);         return false;
                case IncomePassPhase.FoldingIncomeEma:      StepFoldIncomeEmaForOneFaction(pass); return false;
                default:                                    StepPublish(pass);                  return true;
            }
        }

        /// <summary>Drops the remainder of an in-flight pass without publishing. Charges already
        /// applied stay applied -- they are real ledger mutations, not buffered work -- and the three
        /// caches keep their last published values. Teardown only, never a restart.</summary>
        public void AbandonIncomePass() { _incomePass = null; }

        // ---- Phase 1: item minting, and the owned-node index ------------------------------------

        private void StepMintOneNode(IncomePassState pass)
        {
            if (pass.NodeIndex >= pass.NodeOrder.Count)
            {
                pass.OwnedNodeIndexReady = true;
                pass.Phase = IncomePassPhase.DrainingPendings;
                return;
            }

            string nodeId = pass.NodeOrder[pass.NodeIndex++];
            SpatialNode n;
            if (!Graph.Nodes.TryGetValue(nodeId, out n)) return;   // removed mid-pass

            string owner = n.OwnerFactionTag;
            if (owner == null || owner == NeutralFaction.Tag) return;   // UNKN is dormant: no production, no upkeep
            pass.Owners.Add(owner);

            List<string> owned;
            if (!pass.NodeIdsByTag.TryGetValue(owner, out owned))
            {
                owned = new List<string>();
                pass.NodeIdsByTag[owner] = owned;
            }
            owned.Add(n.Id);

            double control = _field.ControlAt(n.Id, owner);

            // Production: mint into specific SubTypeIds per the free-mint table
            NodeYieldEntry[] yieldTable = NodeYieldResolver.EntriesFor(n, Tuning, Registry);
            if (yieldTable.Length == 0) return;
            foreach (NodeYieldEntry ye in yieldTable)
            {
                if (ye.MineOnly) continue;   // effort-gated: only a Mine run yields it, never this pass
                ItemKind kind;
                if (!Enum.TryParse(ye.ItemKind, out kind)) continue;
                long amount = Economy.SourceItemYield(n, control, Tuning, kind);
                if (amount <= 0) continue;
                long toMint = (long)(amount * ye.Weight);
                if (toMint <= 0) continue;
                long cur;
                n.Inventory.TryGetValue(ye.SubTypeId, out cur);
                n.Inventory[ye.SubTypeId] = cur + toMint;
            }
        }

        // ---- Phase 2 setup ----------------------------------------------------------------------

        /// <summary>One atomic step, so the pendings drain is provably exactly-once per pass: anything
        /// Spend()/Earn() posts after this instant lands in the next pass, exactly as before.</summary>
        private void StepDrainPendings(IncomePassState pass)
        {
            pass.ExpenseThisPassByTag = new Dictionary<string, long>(_pendingBurnByTag);
            _pendingBurnByTag.Clear();
            // Real, non-debt income accrued since the last pass (trade settlement, raid loot via
            // AddPendingIncome). Snapshotted here so the charging phase's net-flow cache includes it;
            // IssuingCredit seeds its own bookkeeping from this same dictionary and adds grants/
            // asset-purchase proceeds on top.
            pass.IncomeThisPassByTag = new Dictionary<string, long>(_pendingRealIncomeByTag);
            _pendingRealIncomeByTag.Clear();

            pass.Tags = new List<string>(pass.Owners);
            pass.TagIndex = 0;
            pass.Phase = IncomePassPhase.CachingFleetPower;
        }

        /// <summary>Fleet combat power: unrelated to the credit/debt accounting below, refreshed here
        /// purely to reuse this pass's already-collected tags and cadence rather than adding a separate
        /// per-tick sweep. See ComputeFleetCombatPower/GetFleetCombatPower.</summary>
        private void StepCacheOneFleetPower(IncomePassState pass)
        {
            if (pass.TagIndex >= pass.Tags.Count)
            {
                pass.TagIndex = 0;
                pass.Phase = IncomePassPhase.ChargingFactions;
                return;
            }
            string tag = pass.Tags[pass.TagIndex++];
            pass.BuildFleetPower[tag] = ComputeFleetCombatPower(tag);
        }

        // ---- Phase 2 loop 1: credit upkeep drain, interest, repay, ledger, net flow ---------------

        private void StepChargeOneFaction(IncomePassState pass)
        {
            if (pass.TagIndex >= pass.Tags.Count)
            {
                pass.Phase = IncomePassPhase.MoneySupplyBarrier;
                return;
            }
            string tag = pass.Tags[pass.TagIndex++];

            FactionState state = null;
            if (pass.States != null)
                for (int si = 0; si < pass.States.Count; si++)
                    if (pass.States[si] != null && pass.States[si].Tag == tag) { state = pass.States[si]; break; }
            pass.StateByTag[tag] = state;

            int stationCount = 0;
            foreach (SpatialNode n in OwnedNodesForPass(tag))
                if (Economy.HasDevelopedInfrastructure(n)) stationCount++;

            // Credit upkeep drain
            long upkeepCost = Tuning.UpkeepPerSource * stationCount;
            AddTreasury(tag, -upkeepCost);

            // Interest accrual on outstanding debt
            long debt = GetDebt(tag);
            long interestThisPass = 0;
            if (debt > 0 && Tuning.DebtInterestRate > 0)
            {
                interestThisPass = Math.Max(1L, (long)(debt * Tuning.DebtInterestRate));
                debt += interestThisPass;
                AddTreasury(tag, -interestThisPass);
                _debt[tag] = debt;
                AppendDebtEvent(tag, "interest", -interestThisPass, GetTreasury(tag), debt,
                    "Interest accrued on outstanding debt.");
            }

            // Auto-repay from surplus above InsolvencyFloor
            if (debt > 0)
            {
                long repaymentFloor = Tuning.InsolvencyFloor;
                long supportTarget = ComputeCentralBankSupportTreasuryTarget(tag);
                if (supportTarget > repaymentFloor)
                    repaymentFloor = supportTarget;

                long surplus = GetTreasury(tag) - repaymentFloor;
                if (surplus > 0)
                {
                    long repay = Math.Min(debt, surplus);
                    long supportDebt = state != null ? state.CentralBankSupportDebt : 0;
                    long supportRepay = Math.Min(supportDebt, repay);
                    if (supportRepay > 0 && state != null)
                        state.CentralBankSupportDebt = supportDebt - supportRepay;
                    AddTreasury(tag, -repay);
                    debt -= repay;
                    if (debt > 0) _debt[tag] = debt; else _debt.Remove(tag);
                    AppendDebtEvent(tag, "repay", -repay, GetTreasury(tag), debt,
                        "Faction repaid debt from treasury surplus.");
                }
            }

            // Cache ledger = treasury + node inventory at market price
            long treasuryAfterRepay = GetTreasury(tag);
            long ledger = ComputeLedger(tag);
            pass.BuildLedger[tag] = ledger;
            pass.TreasuryAtLedgerByTag[tag] = treasuryAfterRepay;

            long expenseThisPass;
            pass.ExpenseThisPassByTag.TryGetValue(tag, out expenseThisPass);
            long pendingIncomeThisPass;
            pass.IncomeThisPassByTag.TryGetValue(tag, out pendingIncomeThisPass);
            long netFlow = pendingIncomeThisPass - upkeepCost - interestThisPass - expenseThisPass;
            pass.BuildNetFlow[tag] = netFlow;
        }

        // ---- Phase 2b: the whole-roster money-supply barrier --------------------------------------

        /// <summary>Unreachable until every faction has been charged, so the gap is still measured
        /// against a fully-charged roster exactly as the monolithic body did.</summary>
        private void StepMoneySupplyBarrier(IncomePassState pass)
        {
            pass.WorldCollateralKnown = false;   // measure the gap against fresh collateral
            long target = TargetMoneySupplyForPass();
            CommitCollateralAnchor(pass.WorldCollateral);
            long money = GetCurrentMoneySupply();
            pass.MoneyGap = target - money;

            if (pass.MoneyGap > 0)
            {
                // Expansionary controller path. Grants and rescue lending get first claim on the
                // positive money gap; surplus buyouts use whatever remains.
                pass.PolicyMode = "expand";
                pass.ExpansionBudget = ComputeCentralBankAdjustmentBudget(pass.MoneyGap);
                EnterIssuingCredit(pass);
                return;
            }

            // At or above target. Contract only on the excess beyond the band edge, and pay any
            // rescue the roster still needs out of the same levy (see StepAssessOneLevy).
            double band = Tuning.CentralBankContractionBand > 0 ? Tuning.CentralBankContractionBand : 0;
            long bandEdge = target + (long)(target * band);
            long excess = money - bandEdge;
            pass.ContractionBudget = excess > 0 ? ComputeCentralBankAdjustmentBudget(excess) : 0;
            pass.PolicyMode = pass.ContractionBudget > 0 ? "contract" : "hold";
            pass.TagIndex = 0;
            pass.Phase = IncomePassPhase.AssessingLevy;
        }

        /// <summary>Sorts one faction into payer or rescue case. A faction's own reserve is the larger
        /// of its reserve-ratio share of its own collateral and the treasury target the bank would
        /// grant it back up to; only treasury above that is levy base. So a faction whose cash is in
        /// line with its own assets pays nothing when someone else's war shrinks the world, and no levy
        /// ever takes a faction below the point where the bank would owe it a grant. A faction below
        /// its reserve is never levied and instead adds the grant (or, failing that, support loan) it
        /// would draw to <see cref="IncomePassState.RescueDemand"/> -- an estimate computed by the same
        /// pure eligibility functions IssuingCredit will call, unconstrained by budget.</summary>
        private void StepAssessOneLevy(IncomePassState pass)
        {
            if (pass.TagIndex >= pass.Tags.Count)
            {
                FinishLevyAssessment(pass);
                return;
            }
            string tag = pass.Tags[pass.TagIndex++];

            FactionState state;
            pass.StateByTag.TryGetValue(tag, out state);
            long collateral = GetFactionCollateralValue(tag, state);
            long treasury = GetTreasury(tag);
            long reserve = Math.Max(TargetFromCollateral(collateral), ComputeCentralBankGrantTreasuryTarget(tag, collateral));
            if (treasury > reserve)
            {
                long levyBase = treasury - reserve;
                pass.LevyBaseByTag[tag] = levyBase;
                pass.LevyBase += levyBase;
                return;
            }

            long ledger;
            if (!pass.BuildLedger.TryGetValue(tag, out ledger))
                ledger = ComputeLedger(tag);
            long debt = GetDebt(tag);
            string ignored;
            long rescue = ComputeCentralBankGrant(tag, state, ledger, debt, collateral, long.MaxValue, out ignored);
            if (rescue <= 0)
                rescue = ComputeCentralBankSupportLoan(tag, state, ledger, debt, long.MaxValue, out ignored);
            if (rescue > 0) pass.RescueDemand += rescue;
        }

        private void FinishLevyAssessment(IncomePassState pass)
        {
            long wanted = pass.ContractionBudget + pass.RescueDemand;
            long levy = Math.Min(wanted, pass.LevyBase);
            if (levy <= 0)
            {
                EnterIssuingCredit(pass);
                return;
            }
            pass.LevyBudget = levy;
            pass.LevyRemaining = levy;
            pass.TagIndex = 0;
            pass.Phase = IncomePassPhase.Contracting;
        }

        private void StepContractOneFaction(IncomePassState pass)
        {
            if (pass.TagIndex >= pass.Tags.Count || pass.LevyRemaining <= 0)
            {
                // Rescues take first claim on what was collected: the levy exists to move money to
                // factions below their reserve before it removes any from circulation.
                pass.Levied = pass.LevyBudget - pass.LevyRemaining;
                pass.ExpansionBudget = Math.Min(pass.RescueDemand, pass.Levied);
                EnterIssuingCredit(pass);
                return;
            }

            int i = pass.TagIndex++;
            string tag = pass.Tags[i];
            long levyBase;
            if (!pass.LevyBaseByTag.TryGetValue(tag, out levyBase) || levyBase <= 0) return;

            int factionsRemaining = pass.Tags.Count - i;
            // In double: budget x base overflows a long once both reach the billions.
            long levy = (long)((double)pass.LevyBudget * levyBase / pass.LevyBase);
            if (levy <= 0 && pass.LevyRemaining > 0) levy = 1;
            if (factionsRemaining == 1 || levy > pass.LevyRemaining) levy = pass.LevyRemaining;
            if (levy > levyBase) levy = levyBase;
            if (levy <= 0) return;

            AddTreasury(tag, -levy);
            pass.LevyRemaining -= levy;
            AppendCentralBankEvent("contraction", tag, -levy,
                "Levy withdrew treasury held above the faction's own reserve.");
        }

        // ---- Phase 2c: grants, support loans, and last-resort force-shed -------------------------

        /// <summary>IncomeThisPassByTag (seeded from pending real income when the pendings were
        /// drained) gains grants here and asset-purchase proceeds below -- folded into
        /// FactionState.CentralBankRecentIncome's EMA at the end of the pass, so the support-loan
        /// income-cap gate always reads last pass's smoothed value, never this pass's in-progress one.
        /// The pre-grant snapshot lets the CB-injected delta be folded into the net-flow cache
        /// too.</summary>
        private void EnterIssuingCredit(IncomePassState pass)
        {
            pass.PendingIncomeByTag = new Dictionary<string, long>(pass.IncomeThisPassByTag);
            pass.TagIndex = 0;
            pass.Phase = IncomePassPhase.IssuingCredit;
        }

        private void StepIssueCreditToOneFaction(IncomePassState pass)
        {
            if (pass.TagIndex >= pass.Tags.Count)
            {
                if (pass.MoneyGap > 0 && pass.ExpansionBudget > 0)
                {
                    pass.EligibleSellers = new List<string>();
                    pass.TagIndex = 0;
                    pass.Phase = IncomePassPhase.SelectingAssetSellers;
                }
                else
                {
                    pass.TagIndex = 0;
                    pass.Phase = IncomePassPhase.FoldingIncomeEma;
                }
                return;
            }
            string tag = pass.Tags[pass.TagIndex++];

            FactionState state;
            pass.StateByTag.TryGetValue(tag, out state);
            long ledger;
            if (!pass.BuildLedger.TryGetValue(tag, out ledger))
                ledger = ComputeLedger(tag);

            long debt = GetDebt(tag);
            long budgetBefore = pass.ExpansionBudget;
            // Central-bank grants add free liquidity to factions that still have clear hope.
            long granted = MaybeIssueCentralBankGrant(tag, state, ledger, debt, ref pass.ExpansionBudget);
            if (granted > 0)
            {
                long priorIncome;
                pass.IncomeThisPassByTag.TryGetValue(tag, out priorIncome);
                pass.IncomeThisPassByTag[tag] = priorIncome + granted;
            }
            debt = GetDebt(tag);

            // Collateral-aware central-bank support for asset-solvent but cash-poor factions.
            // Loan principal is debt, never income -- deliberately not added to IncomeThisPassByTag.
            MaybeIssueCentralBankSupportLoan(tag, state, ledger, debt, ref pass.ExpansionBudget);
            debt = GetDebt(tag);
            pass.Rescued += budgetBefore - pass.ExpansionBudget;

            // The ledger can stay healthy while treasury is temporarily under water; only
            // unrecoverable balance-sheet insolvency should trigger territory amputation.
            long bal = GetTreasury(tag);
            bool shed = false;
            if (bal < Tuning.InsolvencyFloor)
            {
                SetTreasury(tag, Tuning.InsolvencyFloor);
                long treasuryAtLedger;
                if (!pass.TreasuryAtLedgerByTag.TryGetValue(tag, out treasuryAtLedger))
                    treasuryAtLedger = bal;

                long effectiveLedger = ledger + (bal - treasuryAtLedger);
                if (IsTrueInsolvencyAfterLiquiditySupport(effectiveLedger))
                {
                    shed = ForceShedWorstDrain(tag, state) != null;
                    // A shed hands the node to UNKN, which changes world collateral: drop the memo so
                    // the next reader recomputes it rather than stamping a stale figure.
                    if (shed) pass.WorldCollateralKnown = false;
                }
            }

            long finalTreasury = GetTreasury(tag);
            long finalLedger = shed ? ComputeLedger(tag) : ledger + (finalTreasury - pass.TreasuryAtLedgerByTag[tag]);
            pass.BuildLedger[tag] = finalLedger;
        }

        // ---- Phase 2d: non-debt asset purchases --------------------------------------------------

        private void StepSelectOneAssetSeller(IncomePassState pass)
        {
            if (pass.TagIndex >= pass.Tags.Count)
            {
                pass.SellerIndex = 0;
                pass.TagIndex = 0;
                pass.Phase = pass.EligibleSellers.Count == 0
                    ? IncomePassPhase.FoldingIncomeEma
                    : IncomePassPhase.BuyingAssets;
                return;
            }
            string tag = pass.Tags[pass.TagIndex++];
            // A faction whose only glut is in items it keeps is not a seller, so its equal share of
            // the budget goes to the factions that are.
            FactionState state;
            pass.StateByTag.TryGetValue(tag, out state);
            if (HasCentralBankBuyableProcessedGoodSurplus(tag, state))
                pass.EligibleSellers.Add(tag);
        }

        private void StepBuyFromOneSeller(IncomePassState pass)
        {
            if (pass.SellerIndex >= pass.EligibleSellers.Count || pass.ExpansionBudget <= 0)
            {
                pass.TagIndex = 0;
                pass.Phase = IncomePassPhase.FoldingIncomeEma;
                return;
            }

            int i = pass.SellerIndex++;
            string tag = pass.EligibleSellers[i];
            int sellersRemaining = pass.EligibleSellers.Count - i;
            long equalShareBudget = sellersRemaining > 0 ? pass.ExpansionBudget / sellersRemaining : pass.ExpansionBudget;
            long budget = equalShareBudget > 0 ? equalShareBudget : pass.ExpansionBudget;
            FactionState state;
            pass.StateByTag.TryGetValue(tag, out state);
            long spent = BuyCentralBankProcessedGoodSurplus(tag, state, budget, ref pass.ExpansionBudget);
            if (spent <= 0) return;
            long prior;
            pass.IncomeThisPassByTag.TryGetValue(tag, out prior);
            pass.IncomeThisPassByTag[tag] = prior + spent;
        }

        // ---- Trailing EMA fold, then publication --------------------------------------------------

        /// <summary>Folds this pass's real income (grants + asset-purchase proceeds + pending trade
        /// settlement/raid loot; never loan principal) into the faction's EMA. Periods are expressed in
        /// income passes, so the memory length scales with whatever cadence interest accrues at.</summary>
        private void StepFoldIncomeEmaForOneFaction(IncomePassState pass)
        {
            if (pass.TagIndex >= pass.Tags.Count)
            {
                pass.Phase = IncomePassPhase.Publishing;
                return;
            }
            string tag = pass.Tags[pass.TagIndex++];

            long incomeThisPass;
            pass.IncomeThisPassByTag.TryGetValue(tag, out incomeThisPass);

            // Grants/asset-purchase proceeds landed after the charging phase cached net flow -- fold
            // the delta in so GetNetFlow reports the pass's full income - expenses.
            long pendingIncome;
            pass.PendingIncomeByTag.TryGetValue(tag, out pendingIncome);
            long cbInjected = incomeThisPass - pendingIncome;
            if (cbInjected != 0)
            {
                long cachedNetFlow;
                pass.BuildNetFlow.TryGetValue(tag, out cachedNetFlow);
                pass.BuildNetFlow[tag] = cachedNetFlow + cbInjected;
            }

            FactionState state;
            if (!pass.StateByTag.TryGetValue(tag, out state) || state == null) return;
            double emaPeriods = Tuning.CentralBankIncomeEmaPeriods > 0 ? Tuning.CentralBankIncomeEmaPeriods : 1.0;
            double alpha = 2.0 / (emaPeriods + 1.0);
            state.CentralBankRecentIncome = alpha * incomeThisPass + (1.0 - alpha) * state.CentralBankRecentIncome;
        }

        /// <summary>Publishes the pass: three reference assignments, so every between-frames reader saw
        /// the previous pass in full and now sees this one in full. Replacing the dictionaries wholesale
        /// also reproduces the monolithic body's Clear() semantics for a faction that has dropped out of
        /// the roster.</summary>
        private void StepPublish(IncomePassState pass)
        {
            _cachedNetFlow = pass.BuildNetFlow;
            _cachedLedger = pass.BuildLedger;
            _cachedFleetPower = pass.BuildFleetPower;
            _lastPolicyMode = pass.PolicyMode;
            _lastPolicyLevied = pass.Levied;
            _lastPolicyRescued = pass.Rescued;
            _incomePass = null;
        }

        // ---- Pass-scoped helpers used by the shared bodies in WorldModel.cs ----------------------

        /// <summary>Every owned node of <paramref name="factionTag"/>, served from the pass's owned-node
        /// index when one is available and by a full graph walk otherwise. Ownership is re-checked live,
        /// so the index can only ever miss a node acquired after this pass started minting -- which is
        /// picked up next pass, the same way a node added mid-pass is not minted this pass.</summary>
        private IEnumerable<SpatialNode> OwnedNodesForPass(string factionTag)
        {
            IncomePassState pass = _incomePass;
            if (pass != null && pass.OwnedNodeIndexReady)
            {
                List<string> ids;
                if (!pass.NodeIdsByTag.TryGetValue(factionTag, out ids)) yield break;
                for (int i = 0; i < ids.Count; i++)
                {
                    SpatialNode n;
                    if (!Graph.Nodes.TryGetValue(ids[i], out n)) continue;
                    if (n.OwnerFactionTag != factionTag) continue;
                    yield return n;
                }
                yield break;
            }

            foreach (SpatialNode n in Graph.Nodes.Values)
                if (n.OwnerFactionTag == factionTag)
                    yield return n;
        }

        /// <summary>Net-flow read used *inside* an income pass. While a pass is in flight this reads the
        /// pass's own build buffer: empty during the charging phase (matching the monolithic body's
        /// _cachedNetFlow.Clear() at the top of Phase 2, so a repayment floor sees 0 for the faction
        /// being charged) and populated from IssuingCredit onward (matching the refilled cache). Reading
        /// the published GetNetFlow here instead would silently substitute last pass's value.</summary>
        private long GetNetFlowForIncomePass(string factionTag)
        {
            IncomePassState pass = _incomePass;
            if (pass == null) return GetNetFlow(factionTag);
            long v;
            return pass.BuildNetFlow.TryGetValue(factionTag, out v) ? v : 0;
        }

        /// <summary>Target money supply, memoised for the duration of an income pass. The underlying
        /// world-collateral walk is O(nodes) and AppendMonetaryEvent stamps it on every emitted event;
        /// without the memo a pass that issues a few dozen events pays for a few dozen full graph walks.
        /// Invalidated whenever the pass changes territory. Outside a pass there is no roster to hand
        /// <see cref="GetTargetMoneySupply"/>, so that path returns the node-only figure.</summary>
        private long TargetMoneySupplyForPass()
        {
            IncomePassState pass = _incomePass;
            if (pass == null) return GetTargetMoneySupply();

            if (!pass.WorldCollateralKnown)
            {
                pass.WorldCollateral = GetWorldCollateralValue(pass.States);
                pass.WorldCollateralKnown = true;
            }
            return TargetFromCollateral(AnchoredCollateral(pass.WorldCollateral));
        }
    }
}
