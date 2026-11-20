using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Factions.Core;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRage;
using VRage.Game;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.Game.ObjectBuilders.Components;
using VRage.Network;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Adds a spawn's grids to the world over several frames instead of one (FAC-443). Used for every
    /// Factions spawn: scanned stations, fleet hulls. The one-frame spawn is each caller's fallback.
    ///
    /// <para><b>How.</b> The grids are taken largest first. For each, its <see cref="CoreBlocks"/> blocks
    /// nearest the centroid (<see cref="StationAssemblyPlan.CoreOutward"/>) are added as the grid; every
    /// frame after, the next chunk is pasted onto it through the server half of SE's
    /// paste-onto-a-grid path (<c>PasteBlocksServer</c>: the chunk is built as its own grid and merged in)
    /// and the same paste is replayed on every client. Each chunk is sized from the whole frame the last
    /// one landed in, measured tick to tick so SE's first update of the new blocks is counted
    /// (<see cref="StationAssemblyPlan.NextChunk"/>, <see cref="TargetMs"/> over <see cref="FrameBaseMs"/>);
    /// a frame over <see cref="PauseAfterFrameMs"/> pauses the assembly for one frame.</para>
    ///
    /// <para><b>Ids are remapped once for the whole spawn</b>, so references between blocks in different
    /// chunks and grids (toolbars, timer actions, a rotor's top) still resolve. Each pasted chunk gets a
    /// fresh grid id of its own, since it lives as a grid for a moment before it is merged.</para>
    ///
    /// <para><b>A ship is assembled static and converted at the end.</b> Every grid of a moving spawn is
    /// built with <c>IsStatic = true</c>, the path the stations proved, so a half-built ship neither sags
    /// nor collides; when the last block is in, each grid is converted with SE's own server-side
    /// <c>OnConvertToDynamic</c> event (the one its convert-to-ship request raises) and the conversion is
    /// timed. The first version held physics off instead (<c>Physics.Enabled = false</c>), and the first
    /// two-grid hull it met (a Clipper: a piston base on the hull, its top on the second grid) took the
    /// server down natively mid-assembly (2026-10-03 21:02:54Z). Small grids convert the same way: the
    /// engine's conversion takes either size, and static small grids are only a player-facing rule.</para>
    ///
    /// <para><b>A ship's thrust groups are rebuilt once it is complete.</b> SE's thrust component files
    /// each conveyor-fed thruster into a group as it registers, by what its conveyors reach at that
    /// moment, and only merges groups again on events an assembly never raises in the right order. A
    /// hull built in chunks ends with its thrusters split across groups that cannot reach a tank, so an
    /// all-hydrogen hull has no thrust at all: an H2 Bulwark measured 15 groups and 0 of 17 thrusters
    /// powered for good, against 1 group and 17 of 17 for the same prefab spawned in one frame (FAC-456,
    /// 2026-10-09). <see cref="RegroupThrust"/> asks SE to merge them. The merge reads conveyor
    /// reachability, which SE recomputes on a worker every 100 frames, so a regroup on the frame the last
    /// block lands finds half the tanks unreachable and changes nothing (v0.485.0, measured). A finished
    /// ship is therefore held static for <see cref="ConveyorSettleSeconds"/>, then converted and
    /// regrouped in one step: the merge runs on its first dynamic update, before anything flies it.</para>
    ///
    /// <para><b>Checked, not assumed.</b> After every chunk each of its blocks is looked up at its own
    /// cell; a shortfall is logged as <c>GRID_ASSEMBLY_SHORT</c> with the blocks that did not arrive,
    /// closes every grid of the spawn and ends it, and the caller falls back.</para>
    ///
    /// <para><b>Mechanical bases go last.</b> A piston, rotor, hinge or suspension base looks for its top
    /// on its first update after it appears and does not look again; SE's own spawn adds every grid in
    /// one frame, so the top is always there. Here the top's grid arrives later, so every
    /// <c>MyObjectBuilder_MechanicalConnectionBlock</c> on every grid is held back and pasted after all
    /// grids' other blocks are in (2026-10-04, the Clipper test: hull complete, piston base without its
    /// top).</para>
    ///
    /// <para><b>Seated characters are removed</b> (a cockpit's <c>Pilot</c>, and characters held as a
    /// block's hierarchy children): a character's saved battery has a null the network serialiser
    /// refuses, which killed every early station run.</para>
    /// </summary>
    internal sealed class GridAssembler
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        internal static readonly GridAssembler Instance = new GridAssembler();

        internal const int CoreBlocks = 64;
        internal const int MinChunk = 16;
        internal const int MaxChunk = 400;
        internal const double TargetMs = 25.0;

        /// <summary>A frame's own cost before any assembly: what the measured frame is reduced by to
        /// estimate what the last chunk added.</summary>
        internal const double FrameBaseMs = 16.7;

        /// <summary>A frame this long pauses the assembly for one frame, so SE's follow-up work on the
        /// last chunk does not land on the same frame as the next one.</summary>
        internal const double PauseAfterFrameMs = 60.0;

        /// <summary>How long a finished ship stands static before it is converted and its thrust
        /// regrouped: long enough for SE's conveyor recompute (every 100 frames, on a worker) to have
        /// seen the last block. An H2 Bulwark's tanks were all reachable 1 s after its last chunk.</summary>
        internal const double ConveyorSettleSeconds = 3.0;

        private long _lastTick;

        // The server half of SE's paste-onto-a-grid, without its sender filter. The public
        // PasteBlocksToGrid checks each block's DLC against the sender, and a server-invoked paste's
        // sender is the server, which owns none: every DLC block was stripped (2026-10-03, N600: Fieldwork
        // floor panels and Decorative Pack 2 catwalks, 8 of the first 16 blocks). PasteBlocksServer builds
        // the chunk and merges it; the client event is the one SE raises after it, raised here.
        private static readonly MethodInfo PasteServerM = typeof(MyCubeGrid).GetMethod("PasteBlocksServer",
            BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(List<MyObjectBuilder_CubeGrid>) }, null);
        private static readonly MethodInfo PasteClientM = typeof(MyCubeGrid).GetMethod("PasteBlocksToGridClient_Implementation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Func<MyCubeGrid, Action<MyObjectBuilder_CubeGrid, MatrixI>> ClientPaste =
            g => (Action<MyObjectBuilder_CubeGrid, MatrixI>)Delegate.CreateDelegate(
                typeof(Action<MyObjectBuilder_CubeGrid, MatrixI>), g, PasteClientM);

        /// <summary>Whether the direct paste is available in this SE build. Without it there is no
        /// assembly: the public paste would strip DLC blocks.</summary>
        internal static bool Available { get { return PasteServerM != null && PasteClientM != null; } }

        private static readonly Func<MyCubeGrid, Action> ConvertEvent = g => g.OnConvertToDynamic;

        private sealed class Part
        {
            public int BuilderIndex;
            public MyObjectBuilder_CubeGrid Shell;
            public List<MyObjectBuilder_CubeBlock> Blocks;
            public List<MyObjectBuilder_BlockGroup> Groups;
            public List<MyObjectBuilder_CubeBlock> Deferred;
            public int[] Order;
            public int Next;
            public long GridId;
            public MyCubeGrid Grid;
            public int Expected;
        }

        private sealed class Job
        {
            public string Key;
            public bool ConvertToShip;
            public List<Part> Parts;
            public int Current;
            /// <summary>-1 while grids are being built; then the part whose mechanical bases go next.</summary>
            public int DeferredPart = -1;
            public int Chunk;
            public int Total;
            public int Pilots;
            public int Projections;
            public Action<List<MyCubeGrid>> Done;
            public Action<string> Failed;
            public long StartedTicks;
            public double PasteMs;
            public double MaxPasteMs;
            public int Pastes;
            public List<MyObjectBuilder_CubeBlock> LastChunk;
            /// <summary>Blocks pasted on the previous tick; 0 when the previous tick pasted none.</summary>
            public int PastedLastTick;
            public double MaxFrameMs;
            public int Pauses;
            /// <summary>Set when the worker loading the hull's block models has finished; the first paste
            /// waits for it.</summary>
            public volatile bool Warmed;
            public volatile int WarmLoaded;
            public int WarmModels;
            public int WarmScripts;
            public volatile int ScriptsCompiled;
            public double WarmMs;
            public bool Started;
            /// <summary>Set when every block of a ship is in; the job finishes once it passes.</summary>
            public DateTime SettleUntilUtc;
        }

        private readonly List<Job> _jobs = new List<Job>();

        internal int Active { get { return _jobs.Count; } }

        /// <summary>Starts assembling <paramref name="prepared"/>: placed, owned and flagged, ids not yet
        /// remapped. <paramref name="moving"/> builds the grids static and converts them to ships once the
        /// spawn is complete.
        /// <paramref name="done"/> receives the grids in the order of <paramref name="prepared"/>. False
        /// when the first core could not be added; nothing is left in the world then.</summary>
        internal bool Begin(string key, MyObjectBuilder_CubeGrid[] prepared, bool moving,
                            Action<List<MyCubeGrid>> done, Action<string> failed)
        {
            if (prepared == null || prepared.Length == 0) return false;
            if (!Available)
            {
                Log.Warn("GRID_ASSEMBLY unavailable: MyCubeGrid.PasteBlocksServer / PasteBlocksToGridClient_Implementation not found");
                return false;
            }

            var job = new Job
            {
                Key = key, ConvertToShip = moving, Parts = new List<Part>(prepared.Length), Chunk = MinChunk,
                Done = done, Failed = failed, StartedTicks = Stopwatch.GetTimestamp(),
            };
            int projections = 0;
            foreach (MyObjectBuilder_CubeGrid grid in prepared)
            {
                if (grid == null) continue;
                if (moving) grid.IsStatic = true;
                if (grid.CubeBlocks != null)
                {
                    job.Pilots += StripCharacters(grid.CubeBlocks);
                    projections += StripProjections(grid.CubeBlocks);
                }
            }
            job.Projections = projections;

            // Once for the whole spawn, so references across chunks and grids keep pointing at each other.
            MyEntities.RemapObjectBuilderCollection(prepared);

            for (int i = 0; i < prepared.Length; i++)
            {
                MyObjectBuilder_CubeGrid grid = prepared[i];
                if (grid == null || grid.CubeBlocks == null || grid.CubeBlocks.Count == 0) continue;
                var part = new Part
                {
                    BuilderIndex = i, Groups = grid.BlockGroups, GridId = grid.EntityId,
                    Blocks = new List<MyObjectBuilder_CubeBlock>(), Deferred = new List<MyObjectBuilder_CubeBlock>(),
                };
                foreach (MyObjectBuilder_CubeBlock b in grid.CubeBlocks)
                    (b is MyObjectBuilder_MechanicalConnectionBlock ? part.Deferred : part.Blocks).Add(b);
                // A grid that is nothing but bases still needs a block to stand the grid on.
                if (part.Blocks.Count == 0) { part.Blocks.Add(part.Deferred[0]); part.Deferred.RemoveAt(0); }
                grid.CubeBlocks = new List<MyObjectBuilder_CubeBlock>();
                grid.BlockGroups = new List<MyObjectBuilder_BlockGroup>();
                grid.ConveyorLines = new List<MyObjectBuilder_ConveyorLine>();
                part.Shell = (MyObjectBuilder_CubeGrid)grid.Clone();

                int n = part.Blocks.Count;
                var x = new int[n]; var y = new int[n]; var z = new int[n];
                for (int b = 0; b < n; b++)
                {
                    SerializableVector3I m = part.Blocks[b].Min;
                    x[b] = m.X; y[b] = m.Y; z[b] = m.Z;
                }
                part.Order = StationAssemblyPlan.CoreOutward(x, y, z);
                job.Parts.Add(part);
                job.Total += n + part.Deferred.Count;
            }
            if (job.Parts.Count == 0) return false;
            job.Parts.Sort((a, b) => b.Blocks.Count.CompareTo(a.Blocks.Count));

            List<string> models = ModelsFor(job.Parts);
            List<string> scripts = ScriptsFor(job.Parts);
            job.WarmScripts = scripts.Count;
            job.WarmModels = models.Count;
            long warmStart = Stopwatch.GetTimestamp();
            try
            {
                // SE's own workers, as its block-model preload uses: a thread Havok has not been initialised
                // on loads a model without its collision shapes and caches it that way (MyModel.LoadData
                // checks HkBaseSystem.IsThreadInitialized). v0.450.2 warmed on a .NET thread-pool thread and
                // the next door built from such a model had no subpart physics: MyDoorBase
                // .CreateSubpartConstraint threw and took the server down (2026-10-04 15:16:51 box-local).
                Sandbox.ModAPI.MyAPIGateway.Parallel.Start(() =>
                {
                    try
                    {
                        job.WarmLoaded = WarmModels(models);
                        job.ScriptsCompiled = ScriptCompileCache.Precompile(scripts);
                        job.WarmMs = Ms(warmStart);
                    }
                    finally { job.Warmed = true; }
                });
            }
            catch (Exception ex)
            {
                Log.Warn("GRID_ASSEMBLY model warm-up not started: " + ex.Message);
                job.Warmed = true;
            }
            _jobs.Add(job);
            return true;
        }

        /// <summary>The distinct programs of the programmable blocks in <paramref name="parts"/>, for the
        /// warm-up to compile (<see cref="ScriptCompileCache"/>).</summary>
        private static List<string> ScriptsFor(List<Part> parts)
        {
            var programs = new HashSet<string>(StringComparer.Ordinal);
            foreach (Part part in parts)
                for (int pass = 0; pass < 2; pass++)
                    foreach (MyObjectBuilder_CubeBlock b in pass == 0 ? part.Blocks : part.Deferred)
                    {
                        var pb = b as MyObjectBuilder_MyProgrammableBlock;
                        if (pb != null && !string.IsNullOrEmpty(pb.Program)) programs.Add(pb.Program);
                    }
            return new List<string>(programs);
        }

        /// <summary>The model files of every block in <paramref name="parts"/>: each definition's model and,
        /// for armour, its cube-part models. Subpart models are found from these as they load.</summary>
        private static List<string> ModelsFor(List<Part> parts)
        {
            var seen = new HashSet<MyDefinitionId>();
            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Part part in parts)
                for (int pass = 0; pass < 2; pass++)
                    foreach (MyObjectBuilder_CubeBlock b in pass == 0 ? part.Blocks : part.Deferred)
                    {
                        MyDefinitionId id = b.GetId();
                        if (!seen.Add(id)) continue;
                        Sandbox.Definitions.MyCubeBlockDefinition def;
                        if (!Sandbox.Definitions.MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out def) || def == null) continue;
                        if (!string.IsNullOrEmpty(def.Model)) models.Add(def.Model);
                        if (def.CubeDefinition != null && def.CubeDefinition.Model != null)
                            foreach (string m in def.CubeDefinition.Model)
                                if (!string.IsNullOrEmpty(m)) models.Add(m);
                    }
            return new List<string>(models);
        }

        /// <summary>Loads <paramref name="models"/> and their subparts' models, as SE's own block-model
        /// preload does at world load (<c>MySession</c>, <c>MyModels.GetModelOnlyData</c> from worker
        /// threads); returns how many were not loaded yet. On a worker thread. The slow assembly steps
        /// were waits on disk and page faults, not work (2026-10-04: a 254 ms chunk read 4.9 MB at 62 ms
        /// of main-thread CPU); SE loads a block's model on first use, on the main thread.</summary>
        private static int WarmModels(List<string> models)
        {
            int loaded = 0;
            // Never load a model where its collision shapes cannot be built: it would be cached without them.
            if (!Havok.HkBaseSystem.IsThreadInitialized) return -1;
            try
            {
                var seen = new HashSet<string>(models, StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<string>(models);
                while (queue.Count > 0)
                {
                    string path = queue.Dequeue();
                    VRage.Game.Models.MyModel before = VRage.Game.Models.MyModels.GetModel(path);
                    bool had = before != null && before.LoadedData;
                    VRage.Game.Models.MyModel model = VRage.Game.Models.MyModels.GetModelOnlyData(path);
                    if (model == null) continue;
                    if (!had && model.LoadedData) loaded++;
                    if (model.Dummies == null) continue;
                    foreach (KeyValuePair<string, VRageRender.Import.MyModelDummy> kv in model.Dummies)
                    {
                        var sub = new VRage.Game.Entity.MyEntitySubpart.Data();
                        if (VRage.Game.Entity.MyEntitySubpart.GetSubpartFromDummy(path, kv.Key, kv.Value, ref sub)
                            && sub.File != null && seen.Add(sub.File))
                            queue.Enqueue(sub.File);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("GRID_ASSEMBLY model warm-up failed: " + ex.Message); }
            return loaded;
        }

        /// <summary>Once per frame: one step for each spawn being assembled — a core, a chunk, or the
        /// finish.</summary>
        internal void Tick()
        {
            // The frame just ended, tick to tick: the last chunk's paste and SE's work on the blocks it
            // added (their first update runs on the following frame, which our paste time cannot see:
            // 2026-10-04, Yari, 100-152 ms MySector frames after chunks that pasted in 3-7 ms).
            long tickNow = Stopwatch.GetTimestamp();
            double frameMs = _lastTick == 0 ? 0 : (tickNow - _lastTick) * 1000.0 / Stopwatch.Frequency;
            _lastTick = tickNow;

            for (int j = _jobs.Count - 1; j >= 0; j--)
            {
                Job job = _jobs[j];
                if (!job.Started)
                {
                    if (!job.Warmed) continue;
                    job.Started = true;
                    try
                    {
                        if (!AddCore(job, job.Parts[0])) { Abort(j, job, job.Key + ": the first grid could not be created"); continue; }
                    }
                    catch (Exception ex) { Abort(j, job, job.Key + ": " + ex.Message); }
                    continue;
                }
                if (job.SettleUntilUtc != default(DateTime))
                {
                    if (DateTime.UtcNow < job.SettleUntilUtc) continue;
                    bool lost = false;
                    foreach (Part p in job.Parts) lost |= p.Grid == null || p.Grid.MarkedForClose || p.Grid.Closed;
                    if (lost) { Abort(j, job, job.Key + ": a grid closed while its conveyors settled"); continue; }
                    _jobs.RemoveAt(j);
                    Finish(job);
                    continue;
                }
                if (job.PastedLastTick > 0 && frameMs > 0)
                {
                    if (frameMs > job.MaxFrameMs) job.MaxFrameMs = frameMs;
                    job.Chunk = StationAssemblyPlan.NextChunk(job.PastedLastTick, Math.Max(0.5, frameMs - FrameBaseMs),
                                                              TargetMs * StallBackoffScale.Value, MinChunk, MaxChunk);
                    job.PastedLastTick = 0;
                    if (frameMs > PauseAfterFrameMs) { job.Pauses++; continue; }
                }
                try
                {
                    Part part = job.Parts[job.DeferredPart >= 0 ? job.DeferredPart : job.Current];
                    if (part.Grid == null || part.Grid.MarkedForClose || part.Grid.Closed)
                    {
                        Abort(j, job, job.Key + ": grid closed at " + part.Next + "/" + part.Blocks.Count + " blocks");
                        continue;
                    }

                    // By cell, not by count: a chunk merged at the wrong offset adds as many blocks as it
                    // should and is still wrong.
                    int have = part.Grid.BlocksCount;
                    int absent = Absent(part.Grid, job.LastChunk);
                    if (have < part.Expected || absent > 0)
                    {
                        Log.Warn("GRID_ASSEMBLY_SHORT key=" + job.Key + " expected=" + part.Expected
                                 + " have=" + have + " absentFromLastChunk=" + absent + " after=" + job.Pastes
                                 + " chunks; missing " + Missing(part.Grid, job.LastChunk));
                        Abort(j, job, job.Key + ": " + Math.Max(part.Expected - have, absent) + " block(s) did not arrive");
                        continue;
                    }

                    if (job.DeferredPart >= 0 || part.Next >= part.Blocks.Count)
                    {
                        job.LastChunk = null;
                        if (job.DeferredPart < 0 && ++job.Current < job.Parts.Count)
                        {
                            if (!AddCore(job, job.Parts[job.Current]))
                                Abort(j, job, job.Key + ": grid " + job.Current + " could not be added");
                            continue;
                        }
                        // Every grid is in: the mechanical bases now, one grid's worth per frame.
                        int next = job.DeferredPart + 1;
                        while (next < job.Parts.Count && job.Parts[next].Deferred.Count == 0) next++;
                        if (next < job.Parts.Count)
                        {
                            job.DeferredPart = next;
                            Part host = job.Parts[next];
                            PasteChunk(job, host, host.Deferred, "bases");
                            continue;
                        }
                        if (job.ConvertToShip)
                        {
                            job.SettleUntilUtc = DateTime.UtcNow.AddSeconds(ConveyorSettleSeconds);
                            continue;
                        }
                        _jobs.RemoveAt(j);
                        Finish(job);
                        continue;
                    }

                    int count = Math.Min(job.Chunk, part.Blocks.Count - part.Next);
                    var blocks = new List<MyObjectBuilder_CubeBlock>(count);
                    for (int i = 0; i < count; i++, part.Next++) blocks.Add(part.Blocks[part.Order[part.Next]]);
                    PasteChunk(job, part, blocks, "blocks");
                }
                catch (Exception ex)
                {
                    Abort(j, job, job.Key + ": " + ex.Message);
                }
            }
        }

        /// <summary>Pastes <paramref name="blocks"/> onto the part's grid as one chunk; returns the ms.</summary>
        private double PasteChunk(Job job, Part part, List<MyObjectBuilder_CubeBlock> blocks, string what)
        {
            var chunk = (MyObjectBuilder_CubeGrid)part.Shell.Clone();
            chunk.CubeBlocks = new List<MyObjectBuilder_CubeBlock>(blocks);
            if (what == "blocks" && part.Next >= part.Blocks.Count && part.Groups != null && part.Deferred.Count == 0)
                chunk.BlockGroups = part.Groups;
            if (what == "bases" && part.Groups != null) chunk.BlockGroups = part.Groups;
            // A chunk lives as a grid for a moment before it is merged, so it needs an id of its own.
            chunk.EntityId = MyEntityIdentifier.AllocateId();
            job.LastChunk = new List<MyObjectBuilder_CubeBlock>(chunk.CubeBlocks);
            Probe probe = Probe.Start();
            var pasted = new List<MyObjectBuilder_CubeGrid> { chunk };
            var merge = (MatrixI)PasteServerM.Invoke(part.Grid, new object[] { pasted });
            double serverMs = Ms(probe.Wall);
            long t1 = Stopwatch.GetTimestamp();
            MyMultiplayer.RaiseEvent(part.Grid, ClientPaste, chunk, merge);
            double clientMs = Ms(t1);
            long t2 = Stopwatch.GetTimestamp();
            var server = MyMultiplayer.ReplicationLayer as MyReplicationServer;
            if (server != null) server.ResendMissingReplicableChildren(part.Grid);
            double resendMs = Ms(t2);
            double ms = Ms(probe.Wall);
            part.Expected += blocks.Count;
            job.PastedLastTick = blocks.Count;
            job.Pastes++;
            job.PasteMs += ms;
            if (ms > job.MaxPasteMs) job.MaxPasteMs = ms;
            if (DebugTraces.SpawnCost)
                Log.Info("GRID_ASSEMBLY_CHUNK key=" + job.Key + " grid=" + job.Parts.IndexOf(part) + " " + what + "=" + blocks.Count
                         + " at=" + part.Next + "/" + part.Blocks.Count + " ms=" + ms.ToString("F1") + " next=" + job.Chunk
                         + (ms >= SlowStepMs
                             ? " server=" + serverMs.ToString("F1") + " client=" + clientMs.ToString("F1")
                               + " resend=" + resendMs.ToString("F1") + probe.Tail()
                             : ""));
            if (FileOpenTrace.Active) { FileOpenTrace.End(0); ThreadStateSampler.End(); }
            return ms;
        }

        private bool AddCore(Job job, Part part)
        {
            int count = Math.Min(CoreBlocks, part.Blocks.Count);
            MyObjectBuilder_CubeGrid core = Take(part, count);
            core.EntityId = part.GridId;
            Probe probe = Probe.Start();
            part.Grid = MyEntities.CreateFromObjectBuilderAndAdd(core, true) as MyCubeGrid;
            double ms = Ms(probe.Wall);
            if (part.Grid == null) { if (FileOpenTrace.Active) { FileOpenTrace.End(0); ThreadStateSampler.End(); } return false; }
            part.Expected = core.CubeBlocks.Count;
            job.PastedLastTick = core.CubeBlocks.Count;
            job.LastChunk = new List<MyObjectBuilder_CubeBlock>(core.CubeBlocks);
            if (DebugTraces.SpawnCost)
                Log.Info("GRID_ASSEMBLY_CORE key=" + job.Key + " grid=" + job.Current + "/" + job.Parts.Count
                         + " blocks=" + count + "/" + part.Blocks.Count + " ms=" + ms.ToString("F0")
                         + " eid=" + part.Grid.EntityId + " pilotsRemoved=" + job.Pilots + " projectionsRemoved=" + job.Projections
                         + (ms >= SlowStepMs ? probe.Tail() : ""));
            if (FileOpenTrace.Active) { FileOpenTrace.End(0); ThreadStateSampler.End(); }
            return true;
        }

        private void Finish(Job job)
        {
            double convertMs = 0;
            int direct = 0;
            if (job.ConvertToShip)
            {
                long t0 = Stopwatch.GetTimestamp();
                foreach (Part p in job.Parts)
                {
                    if (p.Grid == null || !p.Grid.IsStatic) continue;
                    // As SE's own convert-to-ship request does on the server; read back, and called
                    // directly if the event did not run here.
                    MyMultiplayer.RaiseEvent(p.Grid, ConvertEvent);
                    if (p.Grid.IsStatic) { p.Grid.OnConvertToDynamic(); direct++; }
                }
                foreach (Part p in job.Parts) RegroupThrust(p.Grid);
                convertMs = Ms(t0);
            }
            var byBuilder = new SortedDictionary<int, MyCubeGrid>();
            foreach (Part p in job.Parts) byBuilder[p.BuilderIndex] = p.Grid;
            if (DebugTraces.SpawnCost)
                Log.Info("GRID_ASSEMBLY_DONE key=" + job.Key + " grids=" + job.Parts.Count + " blocks=" + job.Total
                         + " chunks=" + job.Pastes + " pasteMs=" + job.PasteMs.ToString("F0")
                         + " maxChunkMs=" + job.MaxPasteMs.ToString("F0")
                         + " maxFrameMs=" + job.MaxFrameMs.ToString("F0") + " pauses=" + job.Pauses
                         + (job.ConvertToShip ? " convertMs=" + convertMs.ToString("F1") + (direct > 0 ? " convertedDirectly=" + direct : "") : "")
                         + " wallMs=" + Ms(job.StartedTicks).ToString("F0")
                         + " models=" + job.WarmModels + " modelsLoaded=" + job.WarmLoaded
                         + " warmMs=" + job.WarmMs.ToString("F0")
                         + " scripts=" + job.WarmScripts + " scriptsCompiled=" + job.ScriptsCompiled);
            job.Done(new List<MyCubeGrid>(byBuilder.Values));
        }

        /// <summary>Asks SE to rebuild what decides whether gas reaches a thruster: the grid's conveyor
        /// endpoints, its thrust component's conveyor groups, and the direction tables built from them.
        /// SE acts on all three on its next update. False when the grid has no thrust component.</summary>
        internal static bool RegroupThrust(MyCubeGrid grid)
        {
            if (grid == null || grid.Closed || grid.MarkedForClose) return false;
            grid.GridSystems.ConveyorSystem.FlagForRecomputation();
            var thrust = grid.Components.Get<MyEntityThrustComponent>();
            if (thrust == null) return false;
            thrust.MergeAllGroupsDirty();
            thrust.MarkDirty();
            return true;
        }

        private void Abort(int index, Job job, string why)
        {
            _jobs.RemoveAt(index);
            foreach (Part p in job.Parts)
                try { if (p.Grid != null && !p.Grid.MarkedForClose) p.Grid.Close(); } catch (Exception) { }
            job.Failed(why);
        }

        /// <summary>Removes the blueprints projectors carry; returns how many projectors held one. A
        /// Naginata's projector holds a 47,187-block blueprint for a 2,026-block hull: on the 2026-10-04 Yari
        /// test the chunk carrying one took 1,081 ms, the blueprint being serialised for every client with
        /// it, and a projector builds its preview from it when it is added. No NPC hull welds from its
        /// projection; the old spawn path clears it after the spawn (<see cref="NpcProjections"/>).</summary>
        internal static int StripProjections(List<MyObjectBuilder_CubeBlock> blocks)
        {
            int stripped = 0;
            foreach (MyObjectBuilder_CubeBlock b in blocks)
            {
                var projector = b as MyObjectBuilder_ProjectorBase;
                if (projector == null) continue;
                bool had = projector.ProjectedGrid != null || (projector.ProjectedGrids != null && projector.ProjectedGrids.Count > 0);
                projector.ProjectedGrid = null;
                projector.ProjectedGrids = null;
                if (had) stripped++;
            }
            return stripped;
        }

        /// <summary>Removes seated characters; returns how many.</summary>
        private static int StripCharacters(List<MyObjectBuilder_CubeBlock> blocks)
        {
            int removed = 0;
            foreach (MyObjectBuilder_CubeBlock b in blocks)
            {
                var seat = b as MyObjectBuilder_Cockpit;
                if (seat != null && seat.Pilot != null) { seat.Pilot = null; removed++; }
                if (b.ComponentContainer == null || b.ComponentContainer.Components == null) continue;
                foreach (MyObjectBuilder_ComponentContainer.ComponentData data in b.ComponentContainer.Components)
                {
                    var hierarchy = data.Component as MyObjectBuilder_HierarchyComponentBase;
                    if (hierarchy == null || hierarchy.Children == null) continue;
                    removed += hierarchy.Children.RemoveAll(c => c is MyObjectBuilder_Character);
                }
            }
            return removed;
        }

        /// <summary>The part's next <paramref name="count"/> blocks in assembly order, as a grid on its own
        /// transform; the block groups ride on the chunk that completes the grid.</summary>
        private static MyObjectBuilder_CubeGrid Take(Part part, int count)
        {
            var grid = (MyObjectBuilder_CubeGrid)part.Shell.Clone();
            grid.CubeBlocks = new List<MyObjectBuilder_CubeBlock>(count);
            for (int i = 0; i < count && part.Next < part.Order.Length; i++, part.Next++)
                grid.CubeBlocks.Add(part.Blocks[part.Order[part.Next]]);
            if (part.Next >= part.Blocks.Count && part.Groups != null && part.Deferred.Count == 0) grid.BlockGroups = part.Groups;
            return grid;
        }

        /// <summary>How many of the last chunk's blocks are not at their own cell.</summary>
        private static int Absent(MyCubeGrid grid, List<MyObjectBuilder_CubeBlock> chunk)
        {
            if (chunk == null) return 0;
            int absent = 0;
            foreach (MyObjectBuilder_CubeBlock b in chunk)
            {
                SerializableVector3I m = b.Min;
                if (grid.GetCubeBlock(new Vector3I(m.X, m.Y, m.Z)) == null) absent++;
            }
            return absent;
        }

        /// <summary>The last chunk's blocks the grid does not hold, by type and cell: the first few, and
        /// a count per type.</summary>
        private static string Missing(MyCubeGrid grid, List<MyObjectBuilder_CubeBlock> chunk)
        {
            if (chunk == null) return "(none listed)";
            var byType = new Dictionary<string, int>(StringComparer.Ordinal);
            var sb = new StringBuilder();
            int listed = 0;
            foreach (MyObjectBuilder_CubeBlock b in chunk)
            {
                SerializableVector3I m = b.Min;
                if (grid.GetCubeBlock(new Vector3I(m.X, m.Y, m.Z)) != null) continue;
                string type = b.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + b.SubtypeName;
                int c;
                byType[type] = byType.TryGetValue(type, out c) ? c + 1 : 1;
                if (listed++ < 6) sb.Append(type).Append('@').Append(m.X).Append(',').Append(m.Y).Append(',').Append(m.Z).Append(' ');
            }
            sb.Append("| by type:");
            foreach (KeyValuePair<string, int> kv in byType) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            return sb.ToString();
        }

        /// <summary>A step this slow logs where its time went: the paste's server, client-event and
        /// resend parts, the thread's own CPU time, and the collections that ran inside it.</summary>
        internal const double SlowStepMs = 150.0;

        /// <summary>Wall time beside the main thread's CPU time and GC counts across one step. Steps of
        /// 0.46-1.1 s landed on block ranges that cost 5-30 ms on other spawns of the same prefab
        /// (2026-10-04 22:26-22:46Z, with no collection on those frames): CPU time far under wall time
        /// means the thread was waiting, not building (measured 2026-10-04: 1,024 ms elapsed against 31 ms
        /// CPU). The process's disk reads and page faults across the step tell a wait on I/O from a wait
        /// on another thread.</summary>
        private struct Probe
        {
            public long Wall;
            private long _cpu, _read, _faults;
            private int _gc0, _gc1, _gc2;

            public static Probe Start()
            {
                var p = new Probe { Wall = Stopwatch.GetTimestamp(), _cpu = ThreadCpu100ns(), _read = ProcessReadBytes(), _faults = ProcessPageFaults() };
                if (DebugTraces.SpawnCost) { FileOpenTrace.Begin(); ThreadStateSampler.Begin(); }
                p._gc0 = GC.CollectionCount(0); p._gc1 = GC.CollectionCount(1); p._gc2 = GC.CollectionCount(2);
                return p;
            }

            public string Tail()
            {
                long cpu = ThreadCpu100ns();
                string cpuMs = cpu < 0 || _cpu < 0 ? "?" : ((cpu - _cpu) / 10000.0).ToString("F1");
                long read = ProcessReadBytes(), faults = ProcessPageFaults();
                return " cpuMs=" + cpuMs + " gc=" + (GC.CollectionCount(0) - _gc0) + "/"
                       + (GC.CollectionCount(1) - _gc1) + "/" + (GC.CollectionCount(2) - _gc2)
                       + " readKB=" + (read < 0 || _read < 0 ? "?" : ((read - _read) / 1024).ToString())
                       + " faults=" + (faults < 0 || _faults < 0 ? "?" : (faults - _faults).ToString())
                       + (FileOpenTrace.Active ? FileOpenTrace.End(6) + ThreadStateSampler.End() : "");
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MemoryCounters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                           QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        [System.Runtime.InteropServices.DllImport("psapi.dll")]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters counters, uint size);

        /// <summary>Bytes the process has read from files and devices; a disk read inside a step that
        /// waited shows here. -1 when unavailable.</summary>
        private static long ProcessReadBytes()
        {
            try { IoCounters c; return GetProcessIoCounters(GetCurrentProcess(), out c) ? (long)c.ReadTransferCount : -1; }
            catch { return -1; }
        }

        /// <summary>The process's page faults, soft and hard together. -1 when unavailable.</summary>
        private static long ProcessPageFaults()
        {
            try
            {
                MemoryCounters c;
                return GetProcessMemoryInfo(GetCurrentProcess(), out c, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemoryCounters)))
                    ? (long)c.PageFaultCount : -1L;
            }
            catch { return -1; }
        }

        /// <summary>The calling thread's kernel + user CPU time in 100 ns units, or -1 when unreadable.</summary>
        internal static long ThreadCpuNow() { return ThreadCpu100ns(); }

        private static long ThreadCpu100ns()
        {
            try
            {
                long c, e, k, u;
                return GetThreadTimes(GetCurrentThread(), out c, out e, out k, out u) ? k + u : -1;
            }
            catch { return -1; }
        }

        private static double Ms(long since)
        {
            return (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
