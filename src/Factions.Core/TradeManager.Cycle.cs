using System.Collections.Generic;

namespace Factions.Core
{
    public sealed partial class TradeManager
    {
        // ---- why the sweep chunks where it does ---------------------------------------------------
        //
        // Chunk ONLY at the (faction, direction) boundary, with no staged-execution-context/
        // commit-guard/journal machinery -- unlike OperationsManager.Cycle.cs, which resumes
        // mid-faction because Operations' own per-faction pass fans out into many bounded per-vector
        // dispatch decisions before one commit, so a deadline can legitimately expire mid-faction and
        // later resume against state another manager has since mutated underneath the half-finished
        // plan. TradeManager's per-faction body (eligibility -> budget gate -> claim index -> form
        // vectors -> dispatch) reads and commits synchronously and atomically within one call, with
        // no yield point in between. Chunking at this boundary changes WHEN cross-manager interleaving
        // can occur BETWEEN two passes -- it does not change WHETHER a single pass stays atomic (it
        // already is, and remains so; nothing else can observe a partially-applied _tradeBudget/
        // PendingFleets/ShipStock/Demand/Investments mutation, because none of those calls yield).
        // This is the same precedent BUG-015's DrainGlobalPass established: no staging is needed to
        // chunk at a boundary that was already atomic in the synchronous version.
        //
        // _tradeBudget's check-then-commit (CanAffordTrade, then the per-vector Record) needs no
        // staging for the same reason: both happen inside one uninterrupted pass.
        //
        // TWO passes over the roster, buy first. The ordering rule is per row, not per faction: a
        // row's owner buys for itself whenever it owns a suitable hull, and only then may a seller
        // fly to it. One interleaved pass could not honour that -- whether a row was offered to a
        // seller before its owner had looked at it would depend on roster order. The claim each
        // dispatch takes in the shared in-flight index is what stops the two passes (or two sellers)
        // serving one row twice. See docs/trade-haul-shaped.md, "Ordering, so one row is not served
        // twice".

        /// <summary>Which direction the sweep is currently walking the roster in.</summary>
        private enum TradeSweepDirection { Buy, Sell }

        /// <summary>Cross-frame sweep over a faction roster -- the Trade counterpart to
        /// ProductionManager/OperationsManager's own sweep cursors, but simpler: since one faction's
        /// pass in one direction is an atomic bounded unit (see this file's header), there is no
        /// intra-faction resumption state to carry. Faction identity is tracked by tag, not by
        /// FactionState reference or list index, so the sweep survives the host's roster changing
        /// underneath it between TryAdvance calls.</summary>
        private sealed class TradeSweepCursor
        {
            internal List<string> FactionTagOrder;
            internal int NextIndex;
            internal TradeSweepDirection Direction;
        }

        private TradeSweepCursor _sweep;

        /// <summary>Runs a full Trade sweep over <paramref name="states"/> to completion in one call
        /// -- today's synchronous contract, preserved as a thin wrapper over <see cref="TryAdvance"/>
        /// with a deadline that never expires. Mirrors <see cref="ProductionManager.Tick"/>'s exact
        /// shape.</summary>
        public void Tick(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            var deadline = FrameDeadline.Unbounded();
            while (!TryAdvance(states, world, tuning, deadline, log)) { }
        }

        /// <summary>Advances the current Trade sweep over <paramref name="states"/> by as many
        /// whole-faction units as fit before <paramref name="deadline"/> expires, resuming a prior
        /// sweep if one is in progress or starting a fresh one otherwise. Returns true once every
        /// faction has been visited in both directions -- the caller may then reschedule its next
        /// sweep on its normal interval. Returns false if the deadline was hit before the sweep
        /// finished; the caller must call this again rather than starting a new sweep, so an
        /// in-flight roster position is never lost.</summary>
        public bool TryAdvance(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            FrameDeadline deadline, List<string> log)
        {
            if (_sweep == null)
            {
                _sweep = NewCursor(states);
                _lastTelemetry = new TradeTickTelemetry();
            }

            while (!deadline.IsExpired())
            {
                if (StepSweepOnce(states, world, tuning, log))
                    return true;
            }
            _lastTelemetry.Yields++;
            return false;
        }

        /// <summary>Runs exactly one bounded unit: either the direction/sweep-complete check, or one
        /// whole faction's pass in the current direction. Always advances <c>NextIndex</c> and
        /// returns false after processing a faction -- mirrors <see cref="ProductionManager.Cycle"/>'s
        /// <c>StepSweepOnce</c> shape (needs one extra call to notice roster exhaustion).</summary>
        private bool StepSweepOnce(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            if (_sweep.NextIndex >= _sweep.FactionTagOrder.Count)
            {
                if (_sweep.Direction == TradeSweepDirection.Buy)
                {
                    _sweep.Direction = TradeSweepDirection.Sell;
                    _sweep.NextIndex = 0;
                    return false;
                }
                _sweep = null;
                _lastTelemetry.SweepsCompleted++;
                return true;
            }

            // No IsAutonomyDisabled check here -- TickOneFaction's own eligibility filter already
            // checks it, along with dormancy and the fleet cap.
            FactionState st = ResolveFaction(states, _sweep.FactionTagOrder[_sweep.NextIndex]);
            bool buying = _sweep.Direction == TradeSweepDirection.Buy;
            _sweep.NextIndex++;
            if (st != null) TickOneFaction(st, buying, states, world, tuning, log);
            return false;
        }

        private static TradeSweepCursor NewCursor(IList<FactionState> states)
        {
            var cursor = new TradeSweepCursor
            {
                FactionTagOrder = new List<string>(),
                NextIndex = 0,
                Direction = TradeSweepDirection.Buy,
            };
            foreach (FactionState st in states) cursor.FactionTagOrder.Add(st.Tag);
            return cursor;
        }

        private static FactionState ResolveFaction(IList<FactionState> states, string tag)
        {
            foreach (FactionState st in states)
                if (st.Tag == tag) return st;
            return null;
        }

        // ---- test-only seam, mirrors ProductionManager.Cycle.cs / OperationsManager.Cycle.cs -------

        internal void BeginSweepForTesting(IList<FactionState> states)
        {
            _sweep = NewCursor(states);
            _lastTelemetry = new TradeTickTelemetry();
        }

        internal bool StepSweepOnceForTesting(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            return StepSweepOnce(states, world, tuning, log);
        }

        internal bool IsSweepInProgressForTesting()
        {
            return _sweep != null;
        }
    }
}
