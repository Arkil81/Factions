using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Factions.Core;
using NLog;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Game.Entity;

namespace Factions
{
    /// <summary>
    /// Per-entity update cost, attributed to the grid each entity belongs to and to its type.
    ///
    /// <para><b>Why here and not on the grid.</b> <c>MyCubeGrid.UpdateBeforeSimulation</c> only runs
    /// the grid's own queued callbacks; a hook there saw ~1% of the frame (FAC-372). Functional blocks — turrets, drills,
    /// doors, the logic PCU prices highest — set <c>NeedsUpdate</c> on their <b>own</b> entity and are
    /// called one by one by <c>MyParallelEntityUpdateOrchestrator</c>, the orchestrator
    /// <c>SpaceEngineersGame</c> installs. Every per-entity update passes through it, so this is where
    /// a per-grid logic cost can actually be read.</para>
    ///
    /// <para><b>Two paths, two hooks.</b></para>
    /// <list type="bullet">
    /// <item><b>Serial, on the game thread.</b> Eight orchestrator methods loop over entity sets and
    /// call <c>item.UpdateBeforeSimulation()</c>, <c>...10()</c>, <c>...100()</c>, the three
    /// <c>UpdateAfterSimulation</c> forms, <c>UpdateOnceBeforeFrame()</c> and <c>Simulate()</c>. A
    /// transpiler brackets each of those call sites — and nothing else — with a timestamp. The
    /// loops, their flag tests and their ordering are untouched.</item>
    /// <item><b>Parallel, on worker threads.</b> <c>ParallelUpdateHandlerBeforeSimulation</c> and
    /// <c>...AfterSimulation</c> run once per entity inside <c>Parallel.ForEach</c>. Prefix/postfix,
    /// with each worker thread writing to its own buffer so the measurement adds no contention to the
    /// loop it measures. Worker time is CPU time summed over threads, not game-thread time; the wall
    /// time of each whole parallel pass is recorded separately, because that is what the game thread
    /// actually waits for.</item>
    /// </list>
    ///
    /// <para><b>Default off</b>: its cost scales with the population it measures (at most ~0.15 ms
    /// a frame measured live, FAC-372). Patched at boot only when
    /// <c>EntityCostEnabled</c> is set; after that, <c>factions debug entitycost</c> switches it
    /// live and each bracket costs one boolean test when off.</para>
    /// </summary>
    internal static class EntityCostPatches
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        internal static volatile bool Enabled;
        internal static bool Bound { get; private set; }

        /// <summary>Call sites timed per orchestrator method, for the one line that says which loops
        /// are covered. A method with zero sites is a loop this build does not see.</summary>
        internal static string Coverage { get; private set; }

        // ---- game thread -------------------------------------------------------------------------
        private static readonly CostTally<long> SerialByGrid = new CostTally<long>();
        private static readonly CostTally<Type> SerialByType = new CostTally<Type>();
        private static long _parallelWallTicks;
        private static long _parallelPasses;

        // ---- worker threads ----------------------------------------------------------------------
        private sealed class WorkerBuffer
        {
            public readonly CostTally<long> ByGrid = new CostTally<long>();
            public readonly CostTally<Type> ByType = new CostTally<Type>();
        }

        [ThreadStatic] private static WorkerBuffer _worker;
        private static readonly List<WorkerBuffer> Workers = new List<WorkerBuffer>();
        private static readonly object WorkersLock = new object();

        /// <summary>Grid key for an entity that is not part of any grid (characters, floating
        /// objects, voxels, missiles).</summary>
        internal const long NotOnAGrid = 0;

        private static readonly string[] SerialLoops =
        {
            "UpdateBeforeSimulation", "UpdateBeforeSimulation10", "UpdateBeforeSimulation100",
            "UpdateAfterSimulation", "UpdateAfterSimulation10", "UpdateAfterSimulation100",
            "DispatchOnceBeforeFrame", "DispatchSimulate",
        };

        private static readonly HashSet<string> TimedCalls = new HashSet<string>
        {
            "UpdateBeforeSimulation", "UpdateBeforeSimulation10", "UpdateBeforeSimulation100",
            "UpdateAfterSimulation", "UpdateAfterSimulation10", "UpdateAfterSimulation100",
            "UpdateOnceBeforeFrame", "Simulate",
        };

        /// <summary>Call sites timed per orchestrator method, written by the transpiler each time
        /// Torch commits it. Read after the server has started, never at registration: nothing has
        /// been transpiled yet then.</summary>
        private static readonly Dictionary<string, int> SitesByLoop = new Dictionary<string, int>();
        private static int _parallelHandlers;
        private static bool _wallBound;
        private static bool _coverageLogged;

        /// <summary>Registers every hook through Torch's <see cref="PatchManager"/>; Torch commits
        /// them when the server starts.
        ///
        /// <para><b>Why not Harmony.</b> The Profiler plugin rewrites the same orchestrator loops
        /// through Torch's PatchManager, which rebuilds a method from its <i>original</i> IL plus the
        /// patches registered with Torch and writes its own jump over the method entry — so a
        /// Harmony detour on those methods is silently replaced at commit. Armed that way on
        /// 2026-09-23 the serial figure read 0 with 13 call sites swapped. Registered with Torch, both
        /// plugins' rewrites are applied in one method.</para>
        ///
        /// <para><b>Insert, don't replace.</b> The transpiler leaves each <c>callvirt</c> in place
        /// and brackets it, so a later transpiler matching on the called method's name — Profiler's
        /// does — still finds it. Ours runs as a Transpiler and Profiler's as a PostTranspiler, so
        /// ours sits outside Profiler's token and its (small) cost is counted in ours.</para></summary>
        internal static void TryPatch(PatchManager patches)
        {
            if (patches == null) return;
            try
            {
                Type orch = typeof(MyParallelEntityUpdateOrchestrator);
                PatchContext ctx = patches.AcquireContext();
                var missing = new List<string>();

                MethodInfo transpiler = W("BracketEntityCalls");
                foreach (string name in SerialLoops)
                {
                    MethodInfo loop = orch.GetMethod(name, AllInstance);
                    if (loop == null) { missing.Add(name); continue; }
                    ctx.GetPattern(loop).Transpilers.Add(transpiler);
                }

                foreach (string name in new[] { "ParallelUpdateHandlerBeforeSimulation", "ParallelUpdateHandlerAfterSimulation" })
                {
                    MethodInfo m = orch.GetMethod(name, AllInstance);
                    if (m == null) { missing.Add(name); continue; }
                    MethodRewritePattern pattern = ctx.GetPattern(m);
                    pattern.Prefixes.Add(W("ParallelPausePrefix"));
                    pattern.Prefixes.Add(W("ParallelPrefix"));
                    pattern.Suffixes.Add(W("ParallelSuffix"));
                    _parallelHandlers++;
                }

                // The parallel pass is timed at its two call sites, never by patching PerformParallelUpdate
                // itself: see BracketParallelPass.
                MethodInfo wall = W("BracketParallelPass");
                int dispatchers = 0;
                foreach (string name in new[] { "DispatchBeforeSimulation", "DispatchAfterSimulation" })
                {
                    MethodInfo m = orch.GetMethod(name, AllInstance);
                    if (m == null) { missing.Add(name); continue; }
                    ctx.GetPattern(m).Transpilers.Add(wall);
                    dispatchers++;
                }
                _wallBound = dispatchers == 2;

                Bound = true;
                Coverage = "not yet committed";
                Log.Info("Server perf: per-entity update cost registered with Torch's patch manager"
                         + (missing.Count > 0 ? " (missing: " + string.Join(", ", missing.ToArray()) + ")" : "")
                         + "; call-site coverage is logged on the first window.");
            }
            catch (Exception ex)
            {
                Bound = false;
                Log.Warn("Server perf: per-entity update cost not bound (" + ex.GetType().Name + ": "
                         + ex.Message + "); nothing else is affected.");
            }
        }

        private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static MethodInfo W(string name)
        {
            return typeof(EntityCostPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        /// <summary>Brackets each <c>callvirt MyEntity::UpdateXxx()</c> with a timestamp, and skips
        /// the call for a block the player toolbox has paused (<see cref="BlockThrottle"/>):
        /// <code>
        ///   dup; stloc entity; call CostStart; stloc start
        ///   ldloc entity; call IsPaused; brtrue SKIP
        ///   callvirt UpdateXxx            // unchanged
        ///   br DONE
        /// SKIP: pop                       // the instance the call would have consumed
        /// DONE: ldloc entity; ldloc start; call CostEnd
        /// </code>
        /// Net stack effect of each bracket is zero on both paths. Labels on the call move to the
        /// first inserted instruction, so a branch to the call still runs the bracket. The call itself
        /// stays in place, so a later transpiler matching on its name still finds it.</summary>
        private static IEnumerable<MsilInstruction> BracketEntityCalls(
            IEnumerable<MsilInstruction> insns, Func<Type, MsilLocal> __localCreator, MethodBase __methodBase)
        {
            MethodInfo start = W("CostStart");
            MethodInfo end = W("CostEnd");
            MethodInfo paused = W("IsPaused");
            var output = new List<MsilInstruction>();
            int sites = 0;
            foreach (MsilInstruction insn in insns)
            {
                var operand = insn.Operand as MsilOperandInline<MethodBase>;
                MethodBase m = operand != null ? operand.Value : null;
                if ((insn.OpCode == OpCodes.Callvirt || insn.OpCode == OpCodes.Call)
                    && m != null && !m.IsStatic && m.GetParameters().Length == 0
                    && m is MethodInfo && ((MethodInfo)m).ReturnType == typeof(void)
                    && typeof(MyEntity).IsAssignableFrom(m.DeclaringType)
                    && TimedCalls.Contains(m.Name))
                {
                    MsilLocal entity = __localCreator(typeof(MyEntity));
                    MsilLocal t0 = __localCreator(typeof(long));
                    var first = new MsilInstruction(OpCodes.Dup);
                    foreach (MsilLabel label in insn.Labels) first.Labels.Add(label);
                    insn.Labels.Clear();
                    output.Add(first);
                    output.Add(entity.AsValueStore());
                    output.Add(new MsilInstruction(OpCodes.Call).InlineValue<MethodInfo>(start));
                    output.Add(t0.AsValueStore());
                    var skip = new MsilLabel();
                    var done = new MsilLabel();
                    output.Add(entity.AsValueLoad());
                    output.Add(new MsilInstruction(OpCodes.Call).InlineValue<MethodInfo>(paused));
                    output.Add(new MsilInstruction(OpCodes.Brtrue).InlineTarget(skip));
                    output.Add(insn);
                    output.Add(new MsilInstruction(OpCodes.Br).InlineTarget(done));
                    output.Add(new MsilInstruction(OpCodes.Pop).LabelWith(skip));
                    output.Add(entity.AsValueLoad().LabelWith(done));
                    output.Add(t0.AsValueLoad());
                    output.Add(new MsilInstruction(OpCodes.Call).InlineValue<MethodInfo>(end));
                    sites++;
                    continue;
                }
                output.Add(insn);
            }
            lock (SitesByLoop) SitesByLoop[__methodBase != null ? __methodBase.Name : "?"] = sites;
            return output;
        }

        // ---- serial (game thread) -----------------------------------------------------------------

        private static long CostStart()
        {
            if (!Enabled) return 0L;
            AllocProbe.EntityStart();
            return Stopwatch.GetTimestamp();
        }

        private static bool IsPaused(MyEntity e)
        {
            return BlockThrottle.IsThrottled(e);
        }

        private static void CostEnd(MyEntity e, long start)
        {
            if (start == 0L) return;
            long dt = Stopwatch.GetTimestamp() - start;
            if (dt < 0) dt = 0;
            try
            {
                AllocProbe.EntityEnd(e != null ? e.GetType() : null, true);
                long grid = GridKey(e);
                SerialByGrid.Add(grid, dt);
                SerialByType.Add(e != null ? e.GetType() : typeof(MyEntity), dt);
                HitchProbe.AddEntity(e, grid, dt);
                BlockThrottle.AddCost(e, grid, dt);
            }
            catch { /* a measurement must not be able to fail the update it measures */ }
        }

        /// <summary>Which loops the committed methods actually time. Built on first use after the
        /// server has started, when Torch has run the transpiler.</summary>
        private static void LogCoverageOnce()
        {
            if (_coverageLogged) return;
            _coverageLogged = true;
            var parts = new List<string>();
            int total = 0;
            lock (SitesByLoop)
            {
                foreach (string name in SerialLoops)
                {
                    int n;
                    bool seen = SitesByLoop.TryGetValue(name, out n);
                    parts.Add(name + "=" + (seen ? n.ToString() : "not transpiled"));
                    total += n;
                }
            }
            parts.Add("parallelHandlers=" + _parallelHandlers);
            parts.Add("parallelWall=" + _wallBound);
            Coverage = string.Join(" ", parts.ToArray());
            Log.Info("Server perf: per-entity update cost times " + total + " serial call sites (" + Coverage + ").");
        }

        // ---- parallel (worker threads) ------------------------------------------------------------

        // Torch shares a local between a prefix and a suffix by parameter name; the name is ours
        // alone so it cannot collide with another plugin's local on the same method.
        /// <summary>False skips the parallel update of a paused block. Torch then skips the timing
        /// prefix too when it comes later, and the suffix returns on the zero start.</summary>
        private static bool ParallelPausePrefix(IMyParallelUpdateable entity)
        {
            return !BlockThrottle.IsThrottled(entity as MyEntity);
        }

        private static void ParallelPrefix(ref long __localFactionsEntityCost)
        {
            if (Enabled) AllocProbe.EntityStart();
            __localFactionsEntityCost = Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        private static void ParallelSuffix(IMyParallelUpdateable entity, long __localFactionsEntityCost)
        {
            if (__localFactionsEntityCost == 0L || entity == null) return;
            long dt = Stopwatch.GetTimestamp() - __localFactionsEntityCost;
            if (dt < 0) dt = 0;
            WorkerBuffer w = _worker;
            if (w == null)
            {
                w = new WorkerBuffer();
                lock (WorkersLock) Workers.Add(w);
                _worker = w;
            }
            try
            {
                var e = entity as MyEntity;
                AllocProbe.EntityEnd(entity.GetType(), AllocProbe.IsMainThread);
                long grid = e != null ? GridKey(e) : NotOnAGrid;
                w.ByGrid.Add(grid, dt);
                w.ByType.Add(entity.GetType(), dt);
                if (e != null) BlockThrottle.AddCost(e, grid, dt);
            }
            catch { /* a measurement must not be able to fail the update it measures */ }
        }

        /// <summary>Brackets the one <c>call PerformParallelUpdate</c> in <c>DispatchBeforeSimulation</c> and
        /// <c>DispatchAfterSimulation</c> with the wall timer:
        /// <code>
        ///   call WallStart; stloc start     // under the call's two arguments, which stay on the stack
        ///   call PerformParallelUpdate      // unchanged
        ///   ldloc start; call WallEnd
        /// </code>
        /// <para><b>Why not a prefix and suffix on <c>PerformParallelUpdate</c>.</b> That is what v0.307.0
        /// did, and any Torch rewrite of that method runs every parallel entity update twice a frame. Its
        /// parallel branch ends <c>leave IL_0084</c> (the final <c>ret</c>) just before an inner
        /// <c>finally</c>; Torch's <c>MethodTranspiler.EmitMethod</c> drops a <c>leave</c> in that position and
        /// relies on <c>ILGenerator.BeginFinallyBlock</c>'s implicit one, which goes to the end of the inner
        /// block instead: the serial <c>foreach</c> over the same entities. Measured live 2026-10-12 on a
        /// probed grid: 2.00 thrust forces queued per frame, the second from that serial loop every frame, so
        /// every grid's thrust (and every other parallel update) ran twice; dampeners held a grid in gravity
        /// at a g/4 climb. Both dispatchers are straight-line code with no exception blocks, so Torch's
        /// emitter has nothing to drop there.</para></summary>
        private static IEnumerable<MsilInstruction> BracketParallelPass(
            IEnumerable<MsilInstruction> insns, Func<Type, MsilLocal> __localCreator)
        {
            MethodInfo start = W("WallStart");
            MethodInfo end = W("WallEnd");
            foreach (MsilInstruction insn in insns)
            {
                var operand = insn.Operand as MsilOperandInline<MethodBase>;
                MethodBase m = operand != null ? operand.Value : null;
                if ((insn.OpCode == OpCodes.Call || insn.OpCode == OpCodes.Callvirt)
                    && m != null && m.Name == "PerformParallelUpdate"
                    && m.DeclaringType == typeof(MyParallelEntityUpdateOrchestrator))
                {
                    MsilLocal t0 = __localCreator(typeof(long));
                    var first = new MsilInstruction(OpCodes.Call).InlineValue<MethodInfo>(start);
                    foreach (MsilLabel label in insn.Labels) first.Labels.Add(label);
                    insn.Labels.Clear();
                    yield return first;
                    yield return t0.AsValueStore();
                    yield return insn;
                    yield return t0.AsValueLoad();
                    yield return new MsilInstruction(OpCodes.Call).InlineValue<MethodInfo>(end);
                    continue;
                }
                yield return insn;
            }
        }

        private static long WallStart()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        private static void WallEnd(long start)
        {
            if (start == 0L) return;
            long dt = Stopwatch.GetTimestamp() - start;
            if (dt > 0) { _parallelWallTicks += dt; HitchProbe.AddParallelWall(dt); }
            _parallelPasses++;
        }

        /// <summary>The grid an entity's cost belongs to: a block's grid, a grid itself, or — for a
        /// subpart such as a door leaf or a turret barrel — the grid of the block it hangs off.
        /// <para><b>Never throws.</b> It runs inside SE's update loop, and <c>MyCubeBlock.CubeGrid</c>
        /// dereferences <c>SlimBlock</c>, which a block being torn down can lack; an exception here
        /// would surface from this plugin in the middle of the game's own update.</para></summary>
        internal static long GridKey(MyEntity e)
        {
            try
            {
                for (int depth = 0; e != null && depth < 4; depth++)
                {
                    var block = e as MyCubeBlock;
                    if (block != null)
                    {
                        MyCubeGrid g = block.SlimBlock != null ? block.CubeGrid : null;
                        return g != null ? g.EntityId : NotOnAGrid;
                    }
                    var grid = e as MyCubeGrid;
                    if (grid != null) return grid.EntityId;
                    e = e.Parent;
                }
            }
            catch { }
            return NotOnAGrid;
        }

        // ---- window drain (game thread, between frames) ------------------------------------------

        internal sealed class GridRow
        {
            public long GridId;
            public long SerialTicks;
            public long ParallelTicks;
            public long Calls;
            public long TotalTicks { get { return SerialTicks + ParallelTicks; } }
        }

        internal sealed class TypeRow
        {
            public string Type;
            public long SerialTicks;
            public long ParallelTicks;
            public long Calls;
        }

        internal struct GridTicks
        {
            public long Serial;
            public long Parallel;
        }

        internal sealed class Window
        {
            public long SerialTicks;
            public long ParallelTicks;
            public long ParallelWallTicks;
            public long ParallelPasses;
            public long Calls;
            /// <summary>Serial plus parallel ticks over <b>every</b> grid, not only the top list,
            /// split by <see cref="GridClass"/>. Null when no classifier was supplied.</summary>
            public long[] TicksByClass;
            /// <summary>Serial and parallel ticks of every grid, keyed by grid id (0 = on no grid).
            /// Filled only when a classifier was supplied, i.e. when a census exists to use it.</summary>
            public Dictionary<long, GridTicks> AllGrids;
            public List<GridRow> TopGrids = new List<GridRow>();
            public List<TypeRow> TopTypes = new List<TypeRow>();
        }

        /// <summary>Merges the worker buffers, returns the window and clears everything.
        ///
        /// <para>Runs on the game thread from the telemetry flush, which is outside the orchestrator's
        /// dispatch — <c>PerformParallelUpdate</c> is blocking, so no worker is writing to a buffer
        /// while this reads it.</para></summary>
        /// <summary>How <see cref="Take"/> splits grid-attributed cost for the load governor's open
        /// question: does block logic stop running when SE stops simulating a grid?</summary>
        internal static class GridClass
        {
            internal const int NotOnAGrid = 0;
            internal const int Simulated = 1;
            internal const int Unsimulated = 2;
            /// <summary>A grid the last census did not see: spawned or closed since.</summary>
            internal const int Unknown = 3;
            internal const int Count = 4;
        }

        internal static Window Take(int topN)
        {
            return Take(topN, null);
        }

        /// <param name="classOf">Maps a grid id to a <see cref="GridClass"/> value; called for grid
        /// ids other than <see cref="NotOnAGrid"/>. Null skips the split.</param>
        internal static Window Take(int topN, Func<long, int> classOf)
        {
            LogCoverageOnce();
            var grids = new CostTally<long>();
            var parGrids = new CostTally<long>();
            var types = new CostTally<Type>();
            var parTypes = new CostTally<Type>();
            SerialByGrid.MergeInto(grids);
            SerialByType.MergeInto(types);
            lock (WorkersLock)
            {
                foreach (WorkerBuffer w in Workers)
                {
                    w.ByGrid.MergeInto(parGrids);
                    w.ByType.MergeInto(parTypes);
                    w.ByGrid.Clear();
                    w.ByType.Clear();
                }
            }
            SerialByGrid.Clear();
            SerialByType.Clear();

            var win = new Window
            {
                SerialTicks = grids.TotalTicks,
                ParallelTicks = parGrids.TotalTicks,
                ParallelWallTicks = _parallelWallTicks,
                ParallelPasses = _parallelPasses,
                Calls = grids.TotalCalls + parGrids.TotalCalls,
            };
            _parallelWallTicks = 0;
            _parallelPasses = 0;

            if (classOf != null)
            {
                Func<long, int> cls = id => id == NotOnAGrid ? GridClass.NotOnAGrid : classOf(id);
                win.TicksByClass = new long[GridClass.Count];
                grids.SumBy(cls, win.TicksByClass);
                parGrids.SumBy(cls, win.TicksByClass);

                win.AllGrids = new Dictionary<long, GridTicks>();
                foreach (KeyValuePair<long, CostTally<long>.Entry> kv in grids.Entries)
                    win.AllGrids[kv.Key] = new GridTicks { Serial = kv.Value.Ticks };
                foreach (KeyValuePair<long, CostTally<long>.Entry> kv in parGrids.Entries)
                {
                    GridTicks t;
                    win.AllGrids.TryGetValue(kv.Key, out t);
                    t.Parallel += kv.Value.Ticks;
                    win.AllGrids[kv.Key] = t;
                }
            }

            var byGrid = new Dictionary<long, GridRow>();
            foreach (KeyValuePair<long, CostTally<long>.Entry> kv in grids.Entries)
                byGrid[kv.Key] = new GridRow { GridId = kv.Key, SerialTicks = kv.Value.Ticks, Calls = kv.Value.Calls };
            foreach (KeyValuePair<long, CostTally<long>.Entry> kv in parGrids.Entries)
            {
                GridRow r;
                if (!byGrid.TryGetValue(kv.Key, out r)) { r = new GridRow { GridId = kv.Key }; byGrid[kv.Key] = r; }
                r.ParallelTicks += kv.Value.Ticks;
                r.Calls += kv.Value.Calls;
            }
            win.TopGrids.AddRange(byGrid.Values);
            win.TopGrids.Sort((a, b) => b.TotalTicks.CompareTo(a.TotalTicks));
            if (win.TopGrids.Count > topN) win.TopGrids.RemoveRange(topN, win.TopGrids.Count - topN);

            var byType = new Dictionary<Type, TypeRow>();
            foreach (KeyValuePair<Type, CostTally<Type>.Entry> kv in types.Entries)
                byType[kv.Key] = new TypeRow { Type = kv.Key.Name, SerialTicks = kv.Value.Ticks, Calls = kv.Value.Calls };
            foreach (KeyValuePair<Type, CostTally<Type>.Entry> kv in parTypes.Entries)
            {
                TypeRow r;
                if (!byType.TryGetValue(kv.Key, out r)) { r = new TypeRow { Type = kv.Key.Name }; byType[kv.Key] = r; }
                r.ParallelTicks += kv.Value.Ticks;
                r.Calls += kv.Value.Calls;
            }
            win.TopTypes.AddRange(byType.Values);
            win.TopTypes.Sort((a, b) => (b.SerialTicks + b.ParallelTicks).CompareTo(a.SerialTicks + a.ParallelTicks));
            if (win.TopTypes.Count > topN) win.TopTypes.RemoveRange(topN, win.TopTypes.Count - topN);
            return win;
        }
    }
}
