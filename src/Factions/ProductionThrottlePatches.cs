using System;
using System.Diagnostics;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities.Cube;
using Torch.Managers.PatchManager;

namespace Factions
{
    /// <summary>
    /// Refinery and assembler production, timed and pausable (load governor, toolbox step 3).
    ///
    /// <para><b>Why a second hook.</b> Production does not run in the block's per-frame update, so
    /// <see cref="EntityCostPatches"/> neither sees nor gates it. It runs in
    /// <c>DoUpdateTimerTick</c> (<c>MyRefinery.cs:147</c>, <c>MyAssembler.cs:625</c>), fired by the
    /// block's <c>MyTimerComponent</c>, and processes the frames elapsed since the last firing.</para>
    ///
    /// <para><b>A skipped tick drops its work, it does not save it up.</b>
    /// <c>MyTimerComponent.Update</c> zeroes <c>FramesFromLastTrigger</c> after every firing, whatever
    /// the handler did, so lifting a pause brings no backlog burst.</para>
    ///
    /// <para>Registered with Torch's patch manager, like the other cost hooks. The two overrides are
    /// patched separately because each calls <c>base.DoUpdateTimerTick()</c> itself; a survival kit
    /// inherits the assembler's and is left running by <see cref="BlockThrottle.ClassOf"/>.</para>
    /// </summary>
    internal static class ProductionThrottlePatches
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        internal static bool Bound { get; private set; }

        internal static void TryPatch(PatchManager patches)
        {
            if (patches == null) return;
            try
            {
                PatchContext ctx = patches.AcquireContext();
                int bound = 0;
                foreach (Type t in new[] { typeof(MyRefinery), typeof(MyAssembler) })
                {
                    MethodInfo tick = t.GetMethod("DoUpdateTimerTick",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (tick == null) continue;
                    MethodRewritePattern pattern = ctx.GetPattern(tick);
                    pattern.Prefixes.Add(W("TimePrefix"));
                    pattern.Prefixes.Add(W("PausePrefix"));
                    pattern.Suffixes.Add(W("TimeSuffix"));
                    bound++;
                }
                Bound = bound == 2;
                Log.Info("Server perf: production timer hooks registered with Torch's patch manager (" + bound + " of 2).");
            }
            catch (Exception ex)
            {
                Bound = false;
                Log.Warn("Server perf: production timer hooks not bound (" + ex.GetType().Name + ": "
                         + ex.Message + "); nothing else is affected.");
            }
        }

        private static MethodInfo W(string name)
        {
            return typeof(ProductionThrottlePatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        private static void TimePrefix(ref long __localFactionsProdCost)
        {
            __localFactionsProdCost = EntityCostPatches.Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        /// <summary>False skips the tick. Torch still runs the suffix, which then records either a
        /// near-zero time or, when the timing prefix was skipped too, nothing.</summary>
        private static bool PausePrefix(MyFunctionalBlock __instance)
        {
            return !BlockThrottle.IsThrottled(__instance);
        }

        private static void TimeSuffix(MyFunctionalBlock __instance, long __localFactionsProdCost)
        {
            if (__localFactionsProdCost == 0L || __instance == null) return;
            long dt = Stopwatch.GetTimestamp() - __localFactionsProdCost;
            if (dt < 0) dt = 0;
            try { BlockThrottle.AddCost(__instance, EntityCostPatches.GridKey(__instance), dt); }
            catch { /* a measurement must not be able to fail the tick it measures */ }
        }
    }
}
