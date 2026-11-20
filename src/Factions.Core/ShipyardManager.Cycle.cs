using System.Collections.Generic;

namespace Factions.Core
{
    public sealed partial class ShipyardManager
    {
        // ---- why the sweep chunks where it does ---------------------------------------------------
        //
        // Chunk ONLY at the faction boundary, with no staged-execution-context/commit-guard/journal
        // machinery -- TradeManager.Cycle.cs's Design 1, not OperationsManager.Cycle.cs's staged one.
        // Operations resumes mid-faction because its per-faction pass fans out into many bounded
        // per-vector dispatch decisions before one commit, so a deadline can legitimately expire
        // mid-faction and later resume against state another manager has since mutated underneath the
        // half-finished plan. ShipyardManager's per-faction body (post demands -> snapshot -> stall
        // sweep -> service the demand groups -> orphan pass) reads and commits synchronously within
        // one call, with no yield point in between. Chunking here changes WHEN cross-manager
        // interleaving can occur BETWEEN two factions -- it does not change WHETHER one faction's
        // pass stays atomic (it already is, and remains so).
        //
        // Everything the per-faction body carries is derived at the top of that body and dies at the
        // bottom of it: the demand snapshot, the (node|role) groups, the installClaims floor map, the
        // `serviced` set. None of it crosses the boundary, so there is no intra-faction resumption
        // state to persist and no chance of a stale floor being applied to a later faction.
        //
        // The register prune is the one thing that is NOT per-faction, and it stays at the head of the
        // sweep (PruneRegisters, called once when the cursor is created) rather than running per
        // TryAdvance call. Running it per call would charge a fresh TTL pass against demands the
        // factions still ahead of the cursor have not been serviced against yet, and would log the
        // same expiries repeatedly across the frames one sweep spans.
        //
        // ONE pass over the roster, unlike Trade's two: there is no buy/sell ordering rule here.

        /// <summary>Cross-frame sweep over a faction roster -- the Shipyard counterpart to
        /// TradeManager's cursor, and the same shape: since one faction's pass is an atomic bounded
        /// unit (see this file's header), there is no intra-faction resumption state to carry.
        /// Faction identity is tracked by tag, not by FactionState reference or list index, so the
        /// sweep survives the host's roster changing underneath it between TryAdvance calls.</summary>
        private sealed class ShipyardSweepCursor
        {
            internal List<string> FactionTagOrder;
            internal int NextIndex;
        }

        private ShipyardSweepCursor _sweep;

        /// <summary>Advances the current Shipyard sweep over <paramref name="states"/> by as many
        /// whole-faction units as fit before <paramref name="deadline"/> expires, resuming a prior
        /// sweep if one is in progress or starting a fresh one otherwise. Returns true once every
        /// faction has been visited -- the caller may then reschedule its next sweep on its normal
        /// interval. Returns false if the deadline was hit before the sweep finished; the caller must
        /// call this again rather than starting a new sweep, so an in-flight roster position is never
        /// lost.</summary>
        public bool TryAdvance(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            FrameDeadline deadline, List<string> log)
        {
            if (_sweep == null)
            {
                PruneRegisters(world, log);
                _sweep = NewCursor(states);
            }

            while (!deadline.IsExpired())
            {
                if (StepSweepOnce(states, world, tuning, log))
                    return true;
            }
            return false;
        }

        /// <summary>Runs exactly one bounded unit: either the sweep-complete check, or one whole
        /// faction's pass. Always advances <c>NextIndex</c> and returns false after processing a
        /// faction -- mirrors TradeManager.Cycle.cs's <c>StepSweepOnce</c> shape (needs one extra
        /// call to notice roster exhaustion).</summary>
        private bool StepSweepOnce(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            List<string> log)
        {
            if (_sweep.NextIndex >= _sweep.FactionTagOrder.Count)
            {
                _sweep = null;
                return true;
            }

            FactionState st = ResolveFaction(states, _sweep.FactionTagOrder[_sweep.NextIndex]);
            _sweep.NextIndex++;
            if (st != null) TickOneFaction(st, world, tuning, log);
            return false;
        }

        private static ShipyardSweepCursor NewCursor(IList<FactionState> states)
        {
            var cursor = new ShipyardSweepCursor
            {
                FactionTagOrder = new List<string>(),
                NextIndex = 0,
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

        // ---- test-only seam, mirrors TradeManager.Cycle.cs ----------------------------------------

        internal void BeginSweepForTesting(IList<FactionState> states, WorldModel world, List<string> log)
        {
            PruneRegisters(world, log);
            _sweep = NewCursor(states);
        }

        internal bool StepSweepOnceForTesting(IList<FactionState> states, WorldModel world,
            FactionTuning tuning, List<string> log)
        {
            return StepSweepOnce(states, world, tuning, log);
        }

        internal bool IsSweepInProgressForTesting()
        {
            return _sweep != null;
        }
    }
}
