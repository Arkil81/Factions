using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Factions.Core;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI.Ingame;
using Torch.Managers.PatchManager;

namespace Factions
{
    /// <summary>
    /// Per-block programmable-block script cost: the measurement the load governor's own limiter is
    /// to be sized from (docs/load-governor.md, toolbox step 3). Measures only; nothing is throttled.
    ///
    /// <para><b>Why <c>ExecuteCode</c>.</b> Every script run passes through the private
    /// <c>MyProgrammableBlock.ExecuteCode(argument, updateSource, out response)</c>: the runs the script
    /// schedules itself (<c>MyIngameScriptComponent</c> calling <c>Run</c> from the entity update) and
    /// the ones started from outside it — the terminal, timer and sensor actions, another script's
    /// <c>TryRun</c>, a mod. <c>updateSource</c> says which, so the split between cost a slowdown can
    /// reach and cost it cannot is read off the same call.</para>
    ///
    /// <para><b>Registered with Torch's patch manager</b>, as <see cref="EntityCostPatches"/> is and
    /// for the same reason: the Profiler plugin rewrites methods through it, and a Harmony detour on
    /// a method Torch rewrites is lost at commit. Void prefix and suffix, so the method always
    /// runs.</para>
    ///
    /// <para><b>Rides the per-entity cost switch.</b> Bound at boot when <c>EntityCostEnabled</c> is
    /// set, and <c>factions debug entitycost</c> switches both live.</para>
    /// </summary>
    internal static class PbCostPatches
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        internal static bool Bound { get; private set; }

        private const UpdateType External =
            UpdateType.Terminal | UpdateType.Trigger | UpdateType.Mod | UpdateType.Script | UpdateType.IGC;

        private sealed class Acc
        {
            public long GridId;
            public long ScheduledTicks;
            public long ExternalTicks;
            public long ScheduledRuns;
            public long ExternalRuns;
            public long MaxRunTicks;
        }

        // Scripts run on the game thread, but a mod may call TryRun from anywhere; one lock around a
        // dictionary update is small against the script run it follows.
        private static readonly object Sync = new object();
        private static Dictionary<long, Acc> _blocks = new Dictionary<long, Acc>();

        internal static void TryPatch(PatchManager patches)
        {
            if (patches == null) return;
            try
            {
                MethodInfo execute = typeof(MyProgrammableBlock).GetMethod("ExecuteCode",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (execute == null)
                {
                    Log.Warn("Server perf: programmable-block cost not bound (MyProgrammableBlock.ExecuteCode not found).");
                    return;
                }
                MethodRewritePattern pattern = patches.AcquireContext().GetPattern(execute);
                pattern.Prefixes.Add(W("Prefix"));
                pattern.Suffixes.Add(W("Suffix"));
                Bound = true;
                Log.Info("Server perf: programmable-block cost registered with Torch's patch manager.");
            }
            catch (Exception ex)
            {
                Bound = false;
                Log.Warn("Server perf: programmable-block cost not bound (" + ex.GetType().Name + ": "
                         + ex.Message + "); nothing else is affected.");
            }
        }

        private static MethodInfo W(string name)
        {
            return typeof(PbCostPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        private static void Prefix(ref long __localFactionsPbCost)
        {
            __localFactionsPbCost = EntityCostPatches.Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        /// <param name="__prefixSkipped">True when a prefix skipped the run: the limiter's stop gate
        /// (<see cref="PbLimiterPatches"/>). A run that did not happen is not a run to count.</param>
        private static void Suffix(MyProgrammableBlock __instance, UpdateType updateSource, long __localFactionsPbCost,
                                   bool __prefixSkipped)
        {
            if (__prefixSkipped || __localFactionsPbCost == 0L || __instance == null) return;
            long dt = Stopwatch.GetTimestamp() - __localFactionsPbCost;
            if (dt < 0) dt = 0;
            try
            {
                long id = __instance.EntityId;
                long grid = EntityCostPatches.GridKey(__instance);
                bool external = (updateSource & External) != 0;
                lock (Sync)
                {
                    Acc a;
                    if (!_blocks.TryGetValue(id, out a))
                    {
                        a = new Acc();
                        _blocks[id] = a;
                    }
                    a.GridId = grid;
                    if (external) { a.ExternalTicks += dt; a.ExternalRuns++; }
                    else          { a.ScheduledTicks += dt; a.ScheduledRuns++; }
                    if (dt > a.MaxRunTicks) a.MaxRunTicks = dt;
                }
                PbLimiterPatches.RecordRun(id, grid, TicksToMs(dt), updateSource);
                HitchProbe.AddScript(id, dt);
            }
            catch { /* a measurement must not be able to fail the script run it measures */ }
        }

        /// <summary>Returns the window's per-block totals and starts the next window.</summary>
        internal static List<PbCostSummary.Block> Take()
        {
            Dictionary<long, Acc> taken;
            lock (Sync)
            {
                taken = _blocks;
                _blocks = new Dictionary<long, Acc>(taken.Count);
            }
            var list = new List<PbCostSummary.Block>(taken.Count);
            foreach (KeyValuePair<long, Acc> kv in taken)
            {
                Acc a = kv.Value;
                list.Add(new PbCostSummary.Block
                {
                    BlockId = kv.Key,
                    GridId = a.GridId,
                    ScheduledMs = TicksToMs(a.ScheduledTicks),
                    ExternalMs = TicksToMs(a.ExternalTicks),
                    ScheduledRuns = a.ScheduledRuns,
                    ExternalRuns = a.ExternalRuns,
                    MaxRunMs = TicksToMs(a.MaxRunTicks),
                });
            }
            return list;
        }

        private static double TicksToMs(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }
    }
}
