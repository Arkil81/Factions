using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Torch.Managers.PatchManager;

namespace Factions
{
    /// <summary>
    /// The programmable-block limiter's two gates (<see cref="PbLimiter"/>; docs/load-governor.md).
    ///
    /// <para><b>Slow: at the script component, carrying the flags.</b> <c>MyIngameScriptComponent</c>
    /// resets its pending <c>m_updateType</c> at the start of every frame
    /// (<c>UpdateBeforeSimulation</c>, which also consumes <c>UpdateFrequency.Once</c>) and runs the
    /// script with it after the simulation step. Skipping the run anywhere later would lose those flags:
    /// a script on Update10 or Update100 would miss its runs, and one chaining itself on <c>Once</c>
    /// would stop for good. So a skipped opportunity keeps its flags here and the next frame's reset
    /// gets them ORed back in. The script sees every flag it missed, and its own
    /// <c>Runtime.TimeSinceLastRun</c> grows.</para>
    ///
    /// <para><b>Stop: at <c>ExecuteCode</c></b>, which every run passes through, scheduled or started
    /// from outside. The skipped call reports <c>NoScript</c> with the block's current detail text as
    /// its response, so <c>SetDetailedInfo</c> sees no change and another script's <c>TryRun</c> gets
    /// false.</para>
    ///
    /// <para>Registered with Torch's patch manager, like the cost hooks.</para>
    /// </summary>
    internal static class PbLimiterPatches
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>The limiter, or null when <c>PbLimitEnabled</c> is off. Read on every script
        /// update, so null must mean "do nothing" everywhere.</summary>
        internal static volatile PbLimiter Limiter;

        internal static bool Bound { get; private set; }

        /// <summary>Recompiles since the last window, drained by the monitor so a stop a recompile
        /// cleared is also told to the player.</summary>
        private static readonly List<PbLimiter.Change> Resets = new List<PbLimiter.Change>();

        /// <summary>Flags of skipped opportunities, per component, until its next frame's reset.
        /// Game thread only: both component hooks run in the entity update.</summary>
        private static readonly Dictionary<MyIngameScriptComponent, UpdateType> Carried =
            new Dictionary<MyIngameScriptComponent, UpdateType>();

        private const UpdateType External =
            UpdateType.Terminal | UpdateType.Trigger | UpdateType.Mod | UpdateType.Script | UpdateType.IGC;

        internal static void TryPatch(PatchManager patches)
        {
            if (patches == null) return;
            try
            {
                PatchContext ctx = patches.AcquireContext();
                const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                MethodInfo after = typeof(MyIngameScriptComponent).GetMethod("UpdateAfterSimulation", Inst);
                MethodInfo before = typeof(MyIngameScriptComponent).GetMethod("UpdateBeforeSimulation", Inst);
                MethodInfo execute = typeof(MyProgrammableBlock).GetMethod("ExecuteCode", Inst);
                MethodInfo recompile = typeof(MyProgrammableBlock).GetMethod("Recompile", Inst);
                if (after == null || before == null || execute == null)
                {
                    Log.Warn("Server perf: programmable-block limiter not bound (a script method was not found).");
                    return;
                }
                ctx.GetPattern(after).Prefixes.Add(W("SlowPrefix"));
                ctx.GetPattern(before).Suffixes.Add(W("CarrySuffix"));
                ctx.GetPattern(execute).Prefixes.Add(W("StopPrefix"));
                if (recompile != null) ctx.GetPattern(recompile).Prefixes.Add(W("RecompilePrefix"));
                Bound = true;
                Log.Info("Server perf: programmable-block limiter registered with Torch's patch manager"
                         + (recompile == null ? " (no recompile hook: a stop is cleared only by its timer)" : "") + ".");
            }
            catch (Exception ex)
            {
                Bound = false;
                Log.Warn("Server perf: programmable-block limiter not bound (" + ex.GetType().Name + ": "
                         + ex.Message + "); nothing else is affected.");
            }
        }

        private static MethodInfo W(string name)
        {
            return typeof(PbLimiterPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        /// <summary>False skips this frame's scheduled run and keeps its flags for the next.</summary>
        private static bool SlowPrefix(MyIngameScriptComponent __instance,
                                       MyProgrammableBlock __field_m_block, UpdateType __field_m_updateType)
        {
            PbLimiter limiter = Limiter;
            if (limiter == null || __field_m_block == null || __field_m_updateType == UpdateType.None) return true;
            try
            {
                if (limiter.AllowScheduled(__field_m_block.EntityId)) return true;
                UpdateType held;
                Carried.TryGetValue(__instance, out held);
                Carried[__instance] = held | __field_m_updateType;
                return false;
            }
            catch { return true; /* a limiter fault must never stop a script */ }
        }

        /// <summary>After the frame's reset, puts back what skipped frames were owed.</summary>
        private static void CarrySuffix(MyIngameScriptComponent __instance, ref UpdateType __field_m_updateType)
        {
            if (Carried.Count == 0) return;
            UpdateType held;
            if (!Carried.TryGetValue(__instance, out held)) return;
            Carried.Remove(__instance);
            __field_m_updateType |= held;
        }

        /// <summary>False skips a stopped block's run, whatever started it.</summary>
        private static bool StopPrefix(MyProgrammableBlock __instance, ref string response,
                                       ref MyProgrammableBlock.ScriptTerminationReason __result)
        {
            PbLimiter limiter = Limiter;
            if (limiter == null || __instance == null) return true;
            try
            {
                if (!limiter.IsStopped(__instance.EntityId)) return true;
                response = __instance.DetailedInfo != null ? __instance.DetailedInfo.ToString() : "";
                __result = MyProgrammableBlock.ScriptTerminationReason.NoScript;
                return false;
            }
            catch { return true; }
        }

        private static void RecompilePrefix(MyProgrammableBlock __instance)
        {
            PbLimiter limiter = Limiter;
            if (limiter == null || __instance == null) return;
            try
            {
                PbLimiter.Change c = limiter.Reset(__instance.EntityId);
                if (c != null) lock (Resets) Resets.Add(c);
            }
            catch { }
        }

        /// <summary>Called by <see cref="PbCostPatches"/> after every run it timed.</summary>
        internal static void RecordRun(long blockId, long gridId, double ms, UpdateType source)
        {
            PbLimiter limiter = Limiter;
            if (limiter == null) return;
            limiter.RecordRun(blockId, gridId, ms, (source & External) != 0);
        }

        internal static List<PbLimiter.Change> TakeResets()
        {
            lock (Resets)
            {
                var list = new List<PbLimiter.Change>(Resets);
                Resets.Clear();
                return list;
            }
        }

        /// <summary>Drops carried flags for components that no longer exist, so the map cannot grow
        /// for the life of the process. Called once per window.</summary>
        internal static void PruneCarried()
        {
            if (Carried.Count == 0) return;
            var dead = new List<MyIngameScriptComponent>();
            foreach (MyIngameScriptComponent c in Carried.Keys)
                if (c.Entity == null || c.Entity.MarkedForClose || c.Entity.Closed) dead.Add(c);
            foreach (MyIngameScriptComponent c in dead) Carried.Remove(c);
        }
    }
}
