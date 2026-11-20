using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using NLog;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Factions.Core;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Closes a grid together with everything that belongs to it.
    ///
    /// <para><b>Why one <c>Close()</c> is not enough.</b> SE closes exactly the
    /// <see cref="MyCubeGrid"/> it is handed. A rotor- or piston-mounted part is a separate grid, so
    /// closing the hull merely detaches its turrets and leaves them in the world carrying the hull's
    /// velocity. SE's own cleanup walks the group first for that reason
    /// (<c>MySessionComponentTrash.RemoveGrid</c>). This walks the <b>Mechanical</b> group rather
    /// than the Physical one: Physical includes a player ship docked by connector, which is not ours
    /// to close.</para>
    ///
    /// <para><b>The mark trumps the owner.</b> Every hull spawns as its faction's founder identity,
    /// so a player faction's convoy is player-owned by construction and an ownership test would
    /// refuse to close it. A grid <see cref="SimGridMarks"/> has marked is closed whatever
    /// <c>BigOwners</c> says. The guard runs the other way round: a group member carrying neither
    /// the record nor the <c>factions:npc:</c> entity name, and a non-NPC identity, is a player's
    /// own construction attached to ours, and is left where it is with a line naming it.</para>
    ///
    /// <para>Game thread only, like everything that touches <c>MyEntities</c>.</para>
    /// </summary>
    internal static class GridAssembly
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>Closes <paramref name="entityId"/>, every grid in its mechanical group, and —
        /// when <paramref name="withFleetMates"/> — every grid marked under the same fleet key.
        /// Returns how many grids were actually closed.
        ///
        /// <para>The fleet-mate pass runs even when the named entity is already gone: a hull that
        /// closed on its own leaves its turrets and its debris behind, and the fleet key is the only
        /// thing still pointing at them.</para></summary>
        public static int Close(long entityId, bool withFleetMates)
        {
            return Close(entityId, withFleetMates, true);
        }

        private static int Close(long entityId, bool withFleetMates, bool paced)
        {
            var victims = new List<MyCubeGrid>();
            Collect(entityId, withFleetMates, victims);
            return CloseCollected(victims, paced);
        }

        /// <summary>Closes grids an earlier <see cref="Collect"/> gathered — the second half of
        /// <see cref="Close(long, bool)"/>, for a caller that reads the grids before they go.</summary>
        public static int CloseCollected(List<MyCubeGrid> victims)
        {
            return CloseCollected(victims, true);
        }

        /// <summary><paramref name="paced"/> queues the grids for <see cref="Tick"/>; false closes them
        /// now, for a caller that puts something in their place this frame.</summary>
        public static int CloseCollected(List<MyCubeGrid> victims, bool paced)
        {
            // FAC-440: boarding civilian crew must never let a fleet close beneath its visitor.
            // Keep the assembly whole, including mechanically attached grids.
            foreach (MyCubeGrid grid in victims)
                if (grid != null && !grid.Closed && CrewPresence.HasPlayerAboard(grid)) return 0;
            var unit = new List<MyCubeGrid>(victims.Count);
            for (int i = 0; i < victims.Count; i++)
            {
                MyCubeGrid g = victims[i];
                if (g == null || g.MarkedForClose || unit.Contains(g)) continue;
                unit.Add(g);
            }
            if (unit.Count == 0) return 0;
            if (!paced) { CloseNow(unit); return unit.Count; }
            Queue.Add(unit);
            _queuedGrids += unit.Count;
            return unit.Count;
        }

        // ---- paced closing ------------------------------------------------------------------

        /// <summary>Blocks closed whole per frame before the next fleet waits; one fleet is always
        /// closed. SE does a close's work on the frame after it is asked for, in <c>MySector</c>, so the
        /// budget is the size of what is handed over. Fleets leave together when a player moves off: 11
        /// despawned in one second at 15:38:02 box-local 2026-10-04 and the next frame took 366 ms; paced
        /// at 800 blocks per frame, 7 fleets still made a 235 ms and a 165 ms frame at 20:22:07 (FAC-445).</summary>
        internal const int BlocksPerFrame = 300;

        /// <summary>A grid larger than this is taken apart over frames instead of closed whole.</summary>
        internal const int DisassembleAboveBlocks = 300;

        private const double FrameBaseMs = 16.7, TargetMs = 20.0, PauseAfterFrameMs = 60.0;
        private const int MinChunk = 16, MaxChunk = 400;

        private static readonly List<List<MyCubeGrid>> Queue = new List<List<MyCubeGrid>>();
        private static readonly List<MyCubeGrid> ToDisassemble = new List<MyCubeGrid>();
        private static readonly List<MyCubeGrid> ToClose = new List<MyCubeGrid>();
        private static int _queuedGrids, _batchUnits, _batchGrids, _batchBlocks, _batchFrames, _batchRazed;
        private static long _lastTick;

        private sealed class Disassembly
        {
            public MyCubeGrid Grid;
            public List<MySlimBlock> Order;
            public int Next, Chunk = MinChunk, RemovedLastTick, Frames, Pauses;
            public double MaxStepMs, MaxFrameMs;
            public long Started;
        }

        private static Disassembly _current;

        private static readonly Action<MyCubeGrid, MySlimBlock, bool, bool> RemoveBlock = BindRemoveBlock();

        private static Action<MyCubeGrid, MySlimBlock, bool, bool> BindRemoveBlock()
        {
            try
            {
                MethodInfo m = typeof(MyCubeGrid).GetMethod("RemoveBlockInternal", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(MySlimBlock), typeof(bool), typeof(bool) }, null);
                return m == null ? null
                    : (Action<MyCubeGrid, MySlimBlock, bool, bool>)Delegate.CreateDelegate(typeof(Action<MyCubeGrid, MySlimBlock, bool, bool>), m);
            }
            catch (Exception ex)
            {
                Log.Warn("GRID_DISASSEMBLY unavailable, large grids close whole: " + ex.Message);
                return null;
            }
        }

        /// <summary>Once per frame, on the game thread: one step of the grid being taken apart, or, when
        /// none is, whole closes up to <see cref="BlocksPerFrame"/>.</summary>
        public static void Tick()
        {
            long now = Stopwatch.GetTimestamp();
            double frameMs = _lastTick == 0 ? 0 : (now - _lastTick) * 1000.0 / Stopwatch.Frequency;
            _lastTick = now;
            if (_current == null && ToDisassemble.Count == 0 && ToClose.Count == 0 && Queue.Count == 0) return;
            _batchFrames++;

            // A fleet's small grids first, a grid at a time within the budget: the 16 subgrids of a
            // 17-grid hull (776 blocks) closed in one frame made a 184 ms frame at 21:12:14 box-local
            // 2026-10-04 (v0.454.1).
            if (ToClose.Count > 0)
            {
                int closedBlocks = 0, n = 0;
                while (ToClose.Count > 0 && (n == 0 || closedBlocks + Size(ToClose[0]) <= Budget))
                {
                    MyCubeGrid g = ToClose[0];
                    ToClose.RemoveAt(0);
                    n++;
                    closedBlocks += CloseNow(new List<MyCubeGrid> { g });
                }
                _batchBlocks += closedBlocks;
                FinishBatchIfDrained();
                return;
            }

            if (_current == null && ToDisassemble.Count > 0)
            {
                MyCubeGrid next = ToDisassemble[0];
                ToDisassemble.RemoveAt(0);
                _current = StartDisassembly(next);
            }
            if (_current != null)
            {
                StepDisassembly(_current, frameMs);
                return;
            }

            int blocks = 0, units = 0;
            while (Queue.Count > 0 && (units == 0 || blocks < Budget))
            {
                List<MyCubeGrid> unit = Queue[0];
                Queue.RemoveAt(0);
                units++;
                bool aboard = false;
                foreach (MyCubeGrid g in unit)
                    if (g != null && !g.Closed && CrewPresence.HasPlayerAboard(g)) { aboard = true; break; }
                if (aboard)
                {
                    Log.Info("GRID_CLOSE_KEPT lead=" + (unit[0] != null ? unit[0].EntityId : 0) + " reason=player aboard");
                    continue;
                }
                // The large grids of a fleet are taken apart; the rest close whole, within the budget.
                bool large = false;
                foreach (MyCubeGrid g in unit)
                    if (g != null && !g.MarkedForClose && !g.Closed && RemoveBlock != null && g.BlocksCount > DisassembleAboveBlocks)
                        large = true;
                _batchGrids += unit.Count;
                if (!large && blocks + UnitSize(unit) <= Budget || units == 1 && !large && unit.Count == 1)
                {
                    blocks += CloseNow(unit);
                    continue;
                }
                foreach (MyCubeGrid g in unit)
                {
                    if (g == null || g.MarkedForClose || g.Closed) continue;
                    if (RemoveBlock != null && g.BlocksCount > DisassembleAboveBlocks) ToDisassemble.Add(g);
                    else ToClose.Add(g);
                }
                break;
            }
            _batchUnits += units;
            _batchBlocks += blocks;
            FinishBatchIfDrained();
        }

        private static Disassembly StartDisassembly(MyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose || grid.Closed) return null;
            var order = new List<MySlimBlock>(grid.GetBlocks());
            Vector3D centre = Vector3D.Zero;
            foreach (MySlimBlock b in order) centre += new Vector3D(b.Position.X, b.Position.Y, b.Position.Z);
            if (order.Count > 0) centre /= order.Count;
            // Outermost first, so a hull is seen to come apart from its edges inward.
            var keyed = new List<KeyValuePair<double, MySlimBlock>>(order.Count);
            foreach (MySlimBlock b in order)
                keyed.Add(new KeyValuePair<double, MySlimBlock>(
                    Vector3D.DistanceSquared(centre, new Vector3D(b.Position.X, b.Position.Y, b.Position.Z)), b));
            keyed.Sort((a, c) => c.Key.CompareTo(a.Key));
            order.Clear();
            foreach (KeyValuePair<double, MySlimBlock> kv in keyed) order.Add(kv.Value);
            return new Disassembly { Grid = grid, Order = order, Started = Stopwatch.GetTimestamp() };
        }

        /// <summary>Removes the next chunk of blocks as SE's own raze does after its checks
        /// (<c>MyCubeGrid.RazeBlocksSuccess</c> without its physics dirty area, then the
        /// <c>RazeBlocksClient</c> broadcast), skipping
        /// the safe-zone grinding check every station's zone would refuse, and with split detection off
        /// (<c>markDirtyDisconnects: false</c>; splits are server-side only), so a half-removed hull
        /// never falls into loose grids. The chunk is sized from the whole frame the last one landed in.</summary>
        private static void StepDisassembly(Disassembly d, double frameMs)
        {
            MyCubeGrid grid = d.Grid;
            if (grid == null || grid.MarkedForClose || grid.Closed) { _current = null; return; }
            if (d.RemovedLastTick > 0 && frameMs > 0)
            {
                if (frameMs > d.MaxFrameMs) d.MaxFrameMs = frameMs;
                d.Chunk = StationAssemblyPlan.NextChunk(d.RemovedLastTick, Math.Max(0.5, frameMs - FrameBaseMs),
                                                        TargetMs * StallBackoffScale.Value, MinChunk, MaxChunk);
                d.RemovedLastTick = 0;
                if (frameMs > PauseAfterFrameMs) { d.Pauses++; return; }
            }
            if (CrewPresence.HasPlayerAboard(grid)) { d.Pauses++; return; }

            long t0 = Stopwatch.GetTimestamp();
            int remaining = d.Order.Count - d.Next;
            if (remaining <= MinChunk)
            {
                _batchBlocks += grid.BlocksCount;
                grid.Close();
                _batchRazed++;
                if (DebugTraces.SpawnCost)
                    Log.Info("GRID_DISASSEMBLED eid=" + grid.EntityId + " blocks=" + d.Order.Count + " frames=" + (d.Frames + 1)
                             + " pauses=" + d.Pauses + " maxStepMs=" + d.MaxStepMs.ToString("F1")
                             + " maxFrameMs=" + d.MaxFrameMs.ToString("F0")
                             + " wallMs=" + ((Stopwatch.GetTimestamp() - d.Started) * 1000.0 / Stopwatch.Frequency).ToString("F0"));
                _current = null;
                FinishBatchIfDrained();
                return;
            }

            int take = Math.Min(d.Chunk, remaining - MinChunk);
            var positions = new List<Vector3I>(take);
            for (int i = 0; i < take; i++)
            {
                MySlimBlock b = d.Order[d.Next++];
                if (b == null || b.CubeGrid != grid) continue;
                positions.Add(b.Position);
                RemoveBlock(grid, b, true, false);
            }
            // No physics dirty area, unlike SE's raze: marking one makes SE rebuild the whole hull's
            // collision shape on the next frame, so every chunk cost about what closing the grid does
            // (2026-10-04 20:58:27-28 box-local, v0.454.0: 123-222 ms MySector frames while this step took
            // 7-18 ms). The shape keeps the removed blocks until the close, on a grid that is leaving.
            if (positions.Count > 0) MyMultiplayer.RaiseEvent(grid, x => x.RazeBlocksClient, positions);
            d.RemovedLastTick = positions.Count;
            d.Frames++;
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > d.MaxStepMs) d.MaxStepMs = ms;
        }

        private static void FinishBatchIfDrained()
        {
            if (Queue.Count > 0 || ToDisassemble.Count > 0 || ToClose.Count > 0 || _current != null) return;
            if ((_batchUnits > 1 || _batchRazed > 0) && DebugTraces.SpawnCost)
                Log.Info("GRID_CLOSE_PACED fleets=" + _batchUnits + " grids=" + _batchGrids + " blocks=" + _batchBlocks
                         + " disassembled=" + _batchRazed + " frames=" + _batchFrames);
            _batchUnits = _batchGrids = _batchBlocks = _batchFrames = _batchRazed = 0;
            _queuedGrids = 0;
        }

        /// <summary>Grids handed over and not closed yet.</summary>
        internal static int Pending { get { return _queuedGrids; } }

        /// <summary><see cref="BlocksPerFrame"/> under the stall backoff.</summary>
        private static int Budget { get { return (int)(BlocksPerFrame * StallBackoffScale.Value); } }

        private static int Size(MyCubeGrid g)
        {
            return g == null || g.Closed ? 0 : g.BlocksCount;
        }

        private static int UnitSize(List<MyCubeGrid> unit)
        {
            int n = 0;
            foreach (MyCubeGrid g in unit) n += Size(g);
            return n;
        }

        private static int CloseNow(List<MyCubeGrid> unit)
        {
            int blocks = 0;
            foreach (MyCubeGrid g in unit)
            {
                if (g == null || g.MarkedForClose || g.Closed) continue;
                blocks += g.BlocksCount;
                g.Close();
            }
            return blocks;
        }

        /// <summary>The grids <see cref="Close(long, bool)"/> would close, without closing them.
        /// Writes the skip line for any grid it leaves out.</summary>
        public static void Collect(long entityId, bool withFleetMates, List<MyCubeGrid> victims)
        {
            int skipped = 0;

            MyCubeGrid lead;
            if (MyEntities.TryGetEntityById(entityId, out lead, false) && lead != null)
            {
                victims.Add(lead);
                AddMechanicalGroup(lead, entityId, victims, ref skipped);
            }

            SimGridRecord rec;
            if (withFleetMates && SimGridMarks.TryGet(entityId, out rec) && !string.IsNullOrEmpty(rec.FleetKey))
            {
                var mates = new List<long>();
                SimGridMarks.CopyEidsForFleet(rec.FleetKey, mates);
                for (int i = 0; i < mates.Count; i++)
                {
                    if (mates[i] == entityId) continue;
                    MyCubeGrid mate;
                    if (!MyEntities.TryGetEntityById(mates[i], out mate, false) || mate == null) continue;
                    if (victims.Contains(mate)) continue;
                    victims.Add(mate);
                    AddMechanicalGroup(mate, entityId, victims, ref skipped);
                }
            }

            if (skipped > 0) Log.Info("GRID_CLOSE_SKIP eid=" + entityId + " kept=" + skipped);
        }

        /// <summary>Closes <paramref name="grid"/> and its mechanical group. For grids the fleet
        /// registry knows nothing about — a station and whatever subgrids its design carries.</summary>
        public static int Close(MyCubeGrid grid)
        {
            // Now: the one caller replaces the station in the same place straight after.
            return grid == null ? 0 : Close(grid.EntityId, false, false);
        }

        // ---- internals ---------------------------------------------------------------------

        private static void AddMechanicalGroup(MyCubeGrid grid, long leadEid, List<MyCubeGrid> victims, ref int skipped)
        {
            var group = new List<MyCubeGrid>();
            if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Mechanical != null)
            {
                try { MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, group); }
                catch (Exception ex)
                {
                    Log.Warn(ex, "GridAssembly: mechanical group lookup failed for grid " + grid.EntityId + ".");
                    return;
                }
            }
            for (int i = 0; i < group.Count; i++)
            {
                MyCubeGrid member = group[i];
                if (member == null || member == grid || victims.Contains(member)) continue;
                if (!IsOursToClose(member))
                {
                    skipped++;
                    Log.Info("GRID_CLOSE_SKIP eid=" + member.EntityId + " lead=" + leadEid
                             + " blocks=" + member.BlocksCount + " owner=" + FirstPlayerOwner(member));
                    continue;
                }
                victims.Add(member);
            }
        }

        private static bool IsOursToClose(MyCubeGrid grid)
        {
            if (SimGridMarks.IsMarked(grid.EntityId)) return true;
            // The entity name is the same statement as the record, made by a process that is gone:
            // a grid that came back from the save still says whose it is, and the reaper may not
            // have reached it yet to rebuild the record.
            if (NpcGridNaming.IsSimBornName(grid.Name, grid.EntityId)) return true;
            return FirstPlayerOwner(grid) == 0L;
        }

        /// <summary>The first non-NPC identity owning any block on <paramref name="grid"/>, or 0 when
        /// every owner is an NPC identity or the grid is unowned. Small owners count: a player who
        /// welded one block onto a turret owns that block and nothing else.</summary>
        private static long FirstPlayerOwner(MyCubeGrid grid)
        {
            long id = FirstPlayerIdentity(grid.BigOwners);
            return id != 0L ? id : FirstPlayerIdentity(grid.SmallOwners);
        }

        private static long FirstPlayerIdentity(List<long> owners)
        {
            if (owners == null || MySession.Static == null || MySession.Static.Players == null) return 0L;
            for (int i = 0; i < owners.Count; i++)
            {
                long id = owners[i];
                if (id == 0L) continue;
                if (!MySession.Static.Players.IdentityIsNpc(id)) return id;
            }
            return 0L;
        }
    }
}
