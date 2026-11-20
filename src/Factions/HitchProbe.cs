using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Factions.Core;
using NLog;
using Torch.Managers.PatchManager;
using Torch.Managers.PatchManager.MSIL;
using VRage.Game.Entity;

namespace Factions
{
    /// <summary>
    /// Records what one slow frame was spent on (the hitch census, 2026-09-25).
    ///
    /// <para><b>Why.</b> Over 24 h the worst frame of a 30 s window reached 353 ms at p90 and 2.75 s
    /// at worst, and in 80% of the windows with a frame over 250 ms the main thread was busy the whole
    /// frame in nothing already instrumented: not this plugin, not the physics step or transforms, not
    /// a script run. Window maxima cannot say what one frame did, so this measures per frame.</para>
    ///
    /// <para><b>The frame.</b> <c>Game.RunSingleFrame</c> brackets <c>UpdateInternal</c> and writes
    /// <c>FrameTimeTicks</c>, the figure <c>busyMsPerFrame</c> is built from. A prefix there resets
    /// the per-frame counters; the suffix reads that same <c>FrameTimeTicks</c> and, when it is over
    /// the threshold, keeps a record of where the frame went. Nothing is kept for an ordinary
    /// frame.</para>
    ///
    /// <para><b>What a frame is split into.</b> Named sections, timed by prefix/suffix on the method
    /// itself: the game update, the session update, the session-component loop, the four replication
    /// calls, the multiplayer tick, <c>Parallel.RunCallbacks</c>, the invoke queue and Torch's plugin
    /// loop. The sections nest (the component loop is inside the session update), so they are reported
    /// raw, not summed. Inside two of them, each call is bracketed to name the culprit: every session
    /// component's update, by component type, and every Torch plugin's update, by plugin type. The
    /// entity, parallel-pass and script hooks add their share of the frame here too. GC collections
    /// by generation are counted across the frame.</para>
    ///
    /// <para>Game thread only, like everything it brackets, so the counters are plain fields.</para>
    /// </summary>
    internal static class HitchProbe
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        internal static volatile bool Enabled;
        internal static bool Bound { get; private set; }
        internal static long ThresholdTicks = (long)(0.100 * Stopwatch.Frequency);
        internal const int MaxPendingPerWindow = 500;

        internal static readonly string[] SectionNames =
        {
            "gameUpdate", "sessionUpdate", "components", "replClientStates", "replBefore", "replAfter",
            "replSimulate", "mpTick", "parallelCallbacks", "invokeQueue", "plugins",
        };

        private static readonly Dictionary<MethodBase, int> SectionOf = new Dictionary<MethodBase, int>();

        // ---- per frame -----------------------------------------------------------------------------
        private static bool _inFrame;
        private static readonly long[] Sections = new long[SectionNames.Length];
        private static long _componentTicks, _topComponentTicks;
        private static Type _topComponent;
        private static long _pluginTicks, _topPluginTicks;
        private static Type _topPlugin;
        private static long _entityTicks, _topEntityTicks, _topEntityGrid;
        private static Type _topEntity;
        private static long _parallelWallTicks;
        private static long _scriptTicks, _topScriptTicks, _topScriptBlock;
        private static int _gc0, _gc1, _gc2;

        // ---- per window ----------------------------------------------------------------------------
        private static readonly CostTally<Type> ComponentsByType = new CostTally<Type>();
        private static readonly CostTally<Type> PluginsByType = new CostTally<Type>();
        private static List<Hitch> _pending = new List<Hitch>();
        private static int _droppedThisWindow;

        internal sealed class Hitch
        {
            public DateTime Utc;
            public double FrameMs;
            public double[] SectionMs;
            public double ComponentsMs, TopComponentMs;
            public string TopComponent;
            public double PluginsMs, TopPluginMs;
            public string TopPlugin;
            public double EntityMs, TopEntityMs;
            public string TopEntity;
            public long TopEntityGrid;
            public double ParallelWallMs;
            public double ScriptMs, TopScriptMs;
            public long TopScriptBlock;
            public int Gc0, Gc1, Gc2;
        }

        internal static void TryPatch(PatchManager patches)
        {
            if (patches == null) return;
            var missing = new List<string>();
            try
            {
                PatchContext ctx = patches.AcquireContext();
                const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                MethodInfo frame = typeof(Sandbox.Engine.Platform.Game).GetMethod("RunSingleFrame", Inst);
                if (frame == null) { Log.Warn("Server perf: hitch probe not bound (Game.RunSingleFrame not found)."); return; }
                MethodRewritePattern fp = ctx.GetPattern(frame);
                fp.Prefixes.Add(W("FramePrefix"));
                fp.Suffixes.Add(W("FrameSuffix"));

                Type session = typeof(Sandbox.Game.World.MySession);
                Type repl = typeof(VRage.Network.MyReplicationServer);
                var sections = new[]
                {
                    Find(typeof(Sandbox.MySandboxGame), "Update", Inst),
                    Find(session, "Update", Inst),
                    Find(session, "UpdateComponents", Inst),
                    Find(repl, "UpdateClientStateGroups", Inst),
                    Find(repl, "UpdateBefore", Inst),
                    Find(repl, "UpdateAfter", Inst),
                    Find(repl, "Simulate", Inst),
                    Find(typeof(Sandbox.Engine.Multiplayer.MyDedicatedServerBase), "Tick", Inst),
                    Find(typeof(ParallelTasks.Parallel), "RunCallbacks", Stat),
                    Find(typeof(Sandbox.MySandboxGame), "ProcessInvoke", Inst),
                    Find(typeof(Torch.Managers.PluginManager), "UpdatePlugins", Inst),
                };
                for (int i = 0; i < sections.Length; i++)
                {
                    if (sections[i] == null) { missing.Add(SectionNames[i]); continue; }
                    SectionOf[sections[i]] = i;
                    MethodRewritePattern p = ctx.GetPattern(sections[i]);
                    p.Prefixes.Add(W("SectionPrefix"));
                    p.Suffixes.Add(W("SectionSuffix"));
                }
                if (sections[2] != null) ctx.GetPattern(sections[2]).Transpilers.Add(W("BracketComponents"));
                if (sections[10] != null) ctx.GetPattern(sections[10]).Transpilers.Add(W("BracketPlugins"));

                Bound = true;
                Log.Info("Server perf: hitch probe registered with Torch's patch manager"
                         + (missing.Count > 0 ? " (missing: " + string.Join(", ", missing.ToArray()) + ")" : "") + ".");
            }
            catch (Exception ex)
            {
                Bound = false;
                Log.Warn("Server perf: hitch probe not bound (" + ex.GetType().Name + ": " + ex.Message
                         + "); nothing else is affected.");
            }
        }

        /// <summary>The one method of that name, or null when there is none or more than one.</summary>
        private static MethodInfo Find(Type t, string name, BindingFlags flags)
        {
            MethodInfo found = null;
            foreach (MethodInfo m in t.GetMethods(flags))
            {
                if (m.Name != name || m.IsAbstract) continue;
                if (found != null) return null;
                found = m;
            }
            return found;
        }

        private static MethodInfo W(string name)
        {
            return typeof(HitchProbe).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        // ---- frame ---------------------------------------------------------------------------------

        private static void FramePrefix()
        {
            if (!Enabled) { _inFrame = false; return; }
            _inFrame = true;
            Array.Clear(Sections, 0, Sections.Length);
            _componentTicks = _topComponentTicks = 0; _topComponent = null;
            _pluginTicks = _topPluginTicks = 0; _topPlugin = null;
            _entityTicks = _topEntityTicks = _topEntityGrid = 0; _topEntity = null;
            _parallelWallTicks = 0;
            _scriptTicks = _topScriptTicks = _topScriptBlock = 0;
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
        }

        private static void FrameSuffix(Sandbox.Engine.Platform.Game __instance)
        {
            if (!_inFrame) return;
            _inFrame = false;
            try
            {
                long frame = __instance != null ? __instance.FrameTimeTicks : 0;
                if (frame < ThresholdTicks) return;
                if (_pending.Count >= MaxPendingPerWindow) { _droppedThisWindow++; return; }
                var h = new Hitch
                {
                    Utc = DateTime.UtcNow,
                    FrameMs = Ms(frame),
                    SectionMs = new double[Sections.Length],
                    ComponentsMs = Ms(_componentTicks), TopComponentMs = Ms(_topComponentTicks),
                    TopComponent = _topComponent != null ? _topComponent.Name : "",
                    PluginsMs = Ms(_pluginTicks), TopPluginMs = Ms(_topPluginTicks),
                    TopPlugin = _topPlugin != null ? _topPlugin.Name : "",
                    EntityMs = Ms(_entityTicks), TopEntityMs = Ms(_topEntityTicks),
                    TopEntity = _topEntity != null ? _topEntity.Name : "", TopEntityGrid = _topEntityGrid,
                    ParallelWallMs = Ms(_parallelWallTicks),
                    ScriptMs = Ms(_scriptTicks), TopScriptMs = Ms(_topScriptTicks), TopScriptBlock = _topScriptBlock,
                    Gc0 = GC.CollectionCount(0) - _gc0, Gc1 = GC.CollectionCount(1) - _gc1, Gc2 = GC.CollectionCount(2) - _gc2,
                };
                for (int i = 0; i < Sections.Length; i++) h.SectionMs[i] = Ms(Sections[i]);
                _pending.Add(h);
            }
            catch { /* a measurement must not be able to fail the frame it measures */ }
        }

        // ---- named sections ------------------------------------------------------------------------

        private static void SectionPrefix(ref long __localFactionsHitch)
        {
            __localFactionsHitch = _inFrame ? Stopwatch.GetTimestamp() : 0L;
        }

        private static void SectionSuffix(MethodBase __original, long __localFactionsHitch)
        {
            if (__localFactionsHitch == 0L || !_inFrame) return;
            int idx;
            if (__original != null && SectionOf.TryGetValue(__original, out idx))
                Sections[idx] += Stopwatch.GetTimestamp() - __localFactionsHitch;
        }

        // ---- per-call brackets inside two sections --------------------------------------------------

        private static readonly HashSet<string> ComponentCalls = new HashSet<string> { "UpdateBeforeSimulation", "Simulate", "UpdateAfterSimulation" };

        private static IEnumerable<MsilInstruction> BracketComponents(IEnumerable<MsilInstruction> insns, Func<Type, MsilLocal> __localCreator)
        {
            return Bracket(insns, __localCreator,
                m => ComponentCalls.Contains(m.Name) && typeof(VRage.Game.Components.MySessionComponentBase).IsAssignableFrom(m.DeclaringType),
                W("ComponentEnd"));
        }

        private static IEnumerable<MsilInstruction> BracketPlugins(IEnumerable<MsilInstruction> insns, Func<Type, MsilLocal> __localCreator)
        {
            return Bracket(insns, __localCreator,
                m => m.Name == "Update" && typeof(Torch.API.Plugins.ITorchPlugin).IsAssignableFrom(m.DeclaringType),
                W("PluginEnd"));
        }

        /// <summary>Brackets each matching instance, no-argument, void call:
        /// <code>dup; stloc target; call Start; stloc t0; callvirt X; ldloc target; ldloc t0; call end</code>
        /// Net stack effect zero; the call stays in place so another transpiler can still find it.</summary>
        private static IEnumerable<MsilInstruction> Bracket(IEnumerable<MsilInstruction> insns, Func<Type, MsilLocal> localCreator,
                                                            Func<MethodInfo, bool> match, MethodInfo end)
        {
            MethodInfo start = W("Start");
            var output = new List<MsilInstruction>();
            foreach (MsilInstruction insn in insns)
            {
                var operand = insn.Operand as MsilOperandInline<MethodBase>;
                var m = (operand != null ? operand.Value : null) as MethodInfo;
                if ((insn.OpCode == OpCodes.Callvirt || insn.OpCode == OpCodes.Call)
                    && m != null && !m.IsStatic && m.GetParameters().Length == 0 && m.ReturnType == typeof(void) && match(m))
                {
                    MsilLocal target = localCreator(typeof(object));
                    MsilLocal t0 = localCreator(typeof(long));
                    var first = new MsilInstruction(OpCodes.Dup);
                    foreach (MsilLabel label in insn.Labels) first.Labels.Add(label);
                    insn.Labels.Clear();
                    output.Add(first);
                    output.Add(target.AsValueStore());
                    output.Add(new MsilInstruction(OpCodes.Call).InlineValue<MethodInfo>(start));
                    output.Add(t0.AsValueStore());
                    output.Add(insn);
                    output.Add(target.AsValueLoad());
                    output.Add(t0.AsValueLoad());
                    output.Add(new MsilInstruction(OpCodes.Call).InlineValue<MethodInfo>(end));
                    continue;
                }
                output.Add(insn);
            }
            return output;
        }

        private static long Start()
        {
            AllocProbe.CallStart();
            return _inFrame || EntityCostPatches.Enabled || AllocProbe.On ? Stopwatch.GetTimestamp() : 0L;
        }

        private static void ComponentEnd(object component, long t0)
        {
            if (t0 == 0L || component == null) return;
            long dt = Stopwatch.GetTimestamp() - t0;
            try
            {
                Type t = component.GetType();
                AllocProbe.ComponentEnd(t);
                ComponentsByType.Add(t, dt);
                if (!_inFrame) return;
                _componentTicks += dt;
                if (dt > _topComponentTicks) { _topComponentTicks = dt; _topComponent = t; }
            }
            catch { }
        }

        private static void PluginEnd(object plugin, long t0)
        {
            if (t0 == 0L || plugin == null) return;
            long dt = Stopwatch.GetTimestamp() - t0;
            try
            {
                Type t = plugin.GetType();
                AllocProbe.PluginEnd(t);
                PluginsByType.Add(t, dt);
                if (!_inFrame) return;
                _pluginTicks += dt;
                if (dt > _topPluginTicks) { _topPluginTicks = dt; _topPlugin = t; }
            }
            catch { }
        }

        // ---- fed by the other cost hooks ------------------------------------------------------------

        internal static void AddEntity(MyEntity e, long gridId, long dt)
        {
            if (!_inFrame) return;
            _entityTicks += dt;
            if (dt > _topEntityTicks) { _topEntityTicks = dt; _topEntity = e != null ? e.GetType() : null; _topEntityGrid = gridId; }
        }

        internal static void AddParallelWall(long dt)
        {
            if (_inFrame) _parallelWallTicks += dt;
        }

        internal static void AddScript(long blockId, long dt)
        {
            if (!_inFrame) return;
            _scriptTicks += dt;
            if (dt > _topScriptTicks) { _topScriptTicks = dt; _topScriptBlock = blockId; }
        }

        // ---- window --------------------------------------------------------------------------------

        internal sealed class TypeCost
        {
            public string Type;
            public double Ms;
            public long Calls;
        }

        internal static List<Hitch> TakeHitches(out int dropped)
        {
            List<Hitch> taken = _pending;
            _pending = new List<Hitch>();
            dropped = _droppedThisWindow;
            _droppedThisWindow = 0;
            return taken;
        }

        internal static List<TypeCost> TakeComponents(int topN) { return Take(ComponentsByType, topN); }
        internal static List<TypeCost> TakePlugins(int topN) { return Take(PluginsByType, topN); }

        private static List<TypeCost> Take(CostTally<Type> tally, int topN)
        {
            var all = new CostTally<Type>();
            tally.MergeInto(all);
            tally.Clear();
            var list = new List<TypeCost>();
            foreach (KeyValuePair<Type, CostTally<Type>.Entry> kv in all.Entries)
                list.Add(new TypeCost { Type = kv.Key.Name, Ms = Ms(kv.Value.Ticks), Calls = kv.Value.Calls });
            list.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            if (list.Count > topN) list.RemoveRange(topN, list.Count - topN);
            return list;
        }

        private static double Ms(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }
    }
}
