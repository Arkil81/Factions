using System;
using System.Collections.Generic;
using System.Diagnostics;
using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.Entity;

namespace Factions
{
    /// <summary>
    /// The blocks the player toolbox's step 3 has paused, and what each pausable class costs per grid
    /// (docs/load-governor.md). A paused block stays powered and enabled; its update and its
    /// production timer return early, so pausing drops that interval's work and lifting it restores
    /// the block with nothing to repair.
    ///
    /// <para><b>The set is read from the game's update loops, on worker threads too.</b> It is
    /// rebuilt whole on the game thread and swapped in as a new reference, so a reader sees one set
    /// or the other, never a half-built one. Null when nothing is paused, which keeps the check a
    /// single field read in the common case.</para>
    /// </summary>
    internal static class BlockThrottle
    {
        private static volatile HashSet<long> _ids;

        /// <summary>Blocks paused, per party and class, as of the last rebuild.</summary>
        internal static Dictionary<string, int[]> CountsByParty = new Dictionary<string, int[]>(StringComparer.Ordinal);

        /// <summary>Each party's paused blocks as the client is told them, as of the last rebuild.</summary>
        internal static Dictionary<string, List<Factions.Wire.WirePausedBlock>> BlocksByParty =
            new Dictionary<string, List<Factions.Wire.WirePausedBlock>>(StringComparer.Ordinal);

        /// <summary>Raised on the game thread after every rebuild, and when the programmable-block
        /// limiter changes a block's tier, so each player can be told what of theirs is limited now.</summary>
        internal static event Action Rebuilt;

        /// <summary>The party a grid is charged to, from the last census. Set by the monitor.</summary>
        internal static Func<long, string> PartyOfGrid = id => null;

        internal static void RaiseNoticesChanged()
        {
            RaiseRebuilt();
        }

        internal static int Count
        {
            get { HashSet<long> s = _ids; return s == null ? 0 : s.Count; }
        }

        internal static bool IsThrottled(MyEntity e)
        {
            HashSet<long> s = _ids;
            return s != null && e != null && s.Contains(e.EntityId);
        }

        /// <summary>The pausable class of a block, or null. A survival kit is an assembler to SE, but
        /// it is also a respawn point and medical station, so it is never paused.</summary>
        internal static ThrottleClass? ClassOf(MyEntity e)
        {
            if (e is MySurvivalKit) return null;
            if (e is MyRefinery) return ThrottleClass.Refinery;
            if (e is MyAssembler) return ThrottleClass.Assembler;
            if (e is MyShipWelder) return ThrottleClass.Welder;
            return null;
        }

        /// <summary>Rebuilds the paused set from the planner's classes per party. Game thread only.</summary>
        /// <param name="partyByGrid">Every grid the last census saw, with the party it is charged
        /// to.</param>
        internal static void Rebuild(PlayerThrottlePlanner planner, Dictionary<long, string> partyByGrid)
        {
            var counts = new Dictionary<string, int[]>(StringComparer.Ordinal);
            var blocks = new Dictionary<string, List<Factions.Wire.WirePausedBlock>>(StringComparer.Ordinal);
            if (planner == null || !planner.Any || partyByGrid == null)
            {
                _ids = null;
                CountsByParty = counts;
                BlocksByParty = blocks;
                RaiseRebuilt();
                return;
            }

            var ids = new HashSet<long>();
            var classesByParty = new Dictionary<string, List<ThrottleClass>>(StringComparer.Ordinal);
            foreach (string party in planner.Parties())
            {
                List<ThrottleClass> classes = planner.ClassesFor(party);
                if (classes.Count > 0) classesByParty[party] = classes;
            }

            foreach (KeyValuePair<long, string> kv in partyByGrid)
            {
                List<ThrottleClass> classes;
                if (kv.Value == null || !classesByParty.TryGetValue(kv.Value, out classes)) continue;
                MyEntity entity;
                if (!MyEntities.TryGetEntityById(kv.Key, out entity, false)) continue;
                var grid = entity as MyCubeGrid;
                if (grid == null || grid.MarkedForClose) continue;
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    ThrottleClass? c = ClassOf(block);
                    if (!c.HasValue || !classes.Contains(c.Value)) continue;
                    ids.Add(block.EntityId);
                    int[] n;
                    if (!counts.TryGetValue(kv.Value, out n)) { n = new int[PlayerThrottlePlanner.AllClasses.Length]; counts[kv.Value] = n; }
                    n[(int)c.Value]++;
                    List<Factions.Wire.WirePausedBlock> list;
                    if (!blocks.TryGetValue(kv.Value, out list)) { list = new List<Factions.Wire.WirePausedBlock>(); blocks[kv.Value] = list; }
                    list.Add(new Factions.Wire.WirePausedBlock
                    {
                        EntityId = block.EntityId,
                        Class = (byte)c.Value,
                        ByAdmin = planner.IsForced(kv.Value, c.Value),
                    });
                }
            }

            _ids = ids.Count > 0 ? ids : null;
            CountsByParty = counts;
            BlocksByParty = blocks;
            RaiseRebuilt();
        }

        internal static void Clear()
        {
            _ids = null;
            CountsByParty = new Dictionary<string, int[]>(StringComparer.Ordinal);
            BlocksByParty = new Dictionary<string, List<Factions.Wire.WirePausedBlock>>(StringComparer.Ordinal);
            RaiseRebuilt();
        }

        private static void RaiseRebuilt()
        {
            Action handler = Rebuilt;
            if (handler == null) return;
            try { handler(); } catch { /* telling players must not be able to fail the rebuild */ }
        }

        // ---- cost per grid and class --------------------------------------------------------------

        private static readonly object CostSync = new object();
        private static Dictionary<long, long[]> _ticksByGrid = new Dictionary<long, long[]>();

        /// <summary>Records a pausable block's update or production-timer time against its grid.
        /// Called from the game thread and, for the parallel pass, from workers; the lock is only
        /// taken for the three pausable classes.</summary>
        internal static void AddCost(MyEntity e, long gridId, long ticks)
        {
            ThrottleClass? c = ClassOf(e);
            if (!c.HasValue) return;
            lock (CostSync)
            {
                long[] t;
                if (!_ticksByGrid.TryGetValue(gridId, out t))
                {
                    t = new long[PlayerThrottlePlanner.AllClasses.Length];
                    _ticksByGrid[gridId] = t;
                }
                t[(int)c.Value] += ticks;
            }
        }

        /// <summary>Returns the window's ms per grid and class and starts the next window.</summary>
        internal static Dictionary<long, double[]> TakeCostMs()
        {
            Dictionary<long, long[]> taken;
            lock (CostSync)
            {
                taken = _ticksByGrid;
                _ticksByGrid = new Dictionary<long, long[]>(taken.Count);
            }
            var ms = new Dictionary<long, double[]>(taken.Count);
            foreach (KeyValuePair<long, long[]> kv in taken)
            {
                var row = new double[kv.Value.Length];
                for (int i = 0; i < row.Length; i++) row[i] = kv.Value[i] * 1000.0 / Stopwatch.Frequency;
                ms[kv.Key] = row;
            }
            return ms;
        }
    }
}
