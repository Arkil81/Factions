using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using NLog;
using VRage.Scripting;

namespace Factions
{
    /// <summary>
    /// One compile per distinct in-game script (FAC-445). <c>MyProgrammableBlock.Compile</c> does
    /// <c>CompileIngameScriptAsync(...).Result</c>: a Roslyn compile on another thread with the game thread
    /// blocked on it, each time a programmable block with a program is created. Assembled hulls carrying
    /// scripts stalled 386-464 ms at those blocks (2026-10-05 11:19:48 and 11:34:04 box-local, the game
    /// thread in Wait/UserRequest), and each compile loaded an assembly that is never unloaded.
    ///
    /// <para>Keyed on the script's text, its entry type, its base type and memory tracking. A block whose
    /// script has been compiled gets that assembly back as a finished task; one being compiled shares the
    /// compile in flight. Every block still makes its own <c>Program</c> instance with its own storage, and
    /// an edited script is different text, so a real compile. Player blocks included (operator,
    /// 2026-10-05).</para>
    ///
    /// <para>An assembly shared this way is not handed to <c>ClearCachedTypesForAssembly</c> when one of its
    /// blocks drops it: that untracks its types for memory accounting, which the other blocks still use.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class ScriptCompileCache
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>Distinct scripts kept; past it the oldest is forgotten (its assembly stays loaded, as
        /// every compiled assembly does).</summary>
        private const int MaxEntries = 512;

        private sealed class Entry
        {
            public Task<Assembly> Task;
            public List<Message> Diagnostics;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly LinkedList<string> Order = new LinkedList<string>();
        private static readonly HashSet<Assembly> Shared = new HashSet<Assembly>();
        private static int _hits, _compiles;

        internal static int Hits { get { return _hits; } }
        internal static int Compiles { get { return _compiles; } }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(IVRageScriptingFunctions), "CompileIngameScriptAsync");
        }

        private static bool Prepare()
        {
            if (TargetMethod() != null) return true;
            Log.Warn("ScriptCompileCache: IVRageScriptingFunctions.CompileIngameScriptAsync not found; scripts compile per block");
            return false;
        }

        private static string Key(string program, string typeName, string baseType, bool track)
        {
            return typeName + "\u0001" + baseType + "\u0001" + (track ? "1" : "0") + "\u0001" + program;
        }

        private static bool Prefix(string program, string typeName, string baseType, bool trackMemoryUsage,
                                   ref List<Message> diagnostics, ref Task<Assembly> __result, out string __state)
        {
            __state = null;
            if (string.IsNullOrEmpty(program)) return true;
            string key = Key(program, typeName, baseType, trackMemoryUsage);
            lock (Gate)
            {
                Entry e;
                if (Cache.TryGetValue(key, out e))
                {
                    if (e.Task.IsFaulted || e.Task.IsCanceled || (e.Task.IsCompleted && e.Task.Result == null))
                    {
                        Cache.Remove(key);
                        Order.Remove(key);
                    }
                    else
                    {
                        diagnostics = e.Task.IsCompleted ? new List<Message>(e.Diagnostics) : e.Diagnostics;
                        __result = e.Task;
                        _hits++;
                        return false;
                    }
                }
            }
            __state = key;
            return true;
        }

        private static void Postfix(ref List<Message> diagnostics, Task<Assembly> __result, string __state)
        {
            if (__state == null || __result == null) return;
            lock (Gate)
            {
                _compiles++;
                if (Cache.ContainsKey(__state)) return;
                Cache[__state] = new Entry { Task = __result, Diagnostics = diagnostics ?? new List<Message>() };
                Order.AddLast(__state);
                while (Order.Count > MaxEntries)
                {
                    Cache.Remove(Order.First.Value);
                    Order.RemoveFirst();
                }
            }
            string key = __state;
            __result.ContinueWith(t =>
            {
                lock (Gate)
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result != null) Shared.Add(t.Result);
                    else { Cache.Remove(key); Order.Remove(key); }
                }
            });
        }

        /// <summary>Compiles <paramref name="programs"/> through the cache and waits for them: the warm-up of an
        /// assembled hull, on one of SE's workers, so its blocks find their scripts compiled. Returns how many
        /// were compiled here, not found.</summary>
        internal static int Precompile(List<string> programs)
        {
            if (programs == null || programs.Count == 0) return 0;
            if (Sandbox.Game.World.MySession.Static == null || !Sandbox.Game.World.MySession.Static.EnableIngameScripts) return 0;
            int compiled = 0;
            foreach (string program in programs)
            {
                try
                {
                    int before = _compiles;
                    List<Message> diag;
                    Task<Assembly> t = VRage.MyVRage.Platform.Scripting.CompileIngameScriptAsync(
                        "factions-warm", program, out diag, "Factions warm-up", "Program", "MyGridProgram",
                        Sandbox.Engine.Utils.MyFakes.ENABLE_PROGRAMMABLE_BLOCK_MEMORY_LIMIT);
                    if (_compiles != before) compiled++;
                    t.Wait();
                }
                catch (Exception ex) { Log.Warn("GRID_ASSEMBLY script warm-up failed: " + ex.Message); }
            }
            return compiled;
        }

        /// <summary>Keeps a shared assembly's type sizes for the blocks still running it.</summary>
        [HarmonyPatch]
        private static class KeepSharedTypes
        {
            private static MethodBase TargetMethod()
            {
                Type t = AccessTools.TypeByName("VRage.Scripting.MyVRageScriptingInternal");
                return t == null ? null : AccessTools.Method(t, "ClearCachedTypesForAssembly", new[] { typeof(Assembly) });
            }

            private static bool Prepare() { return TargetMethod() != null; }

            private static bool Prefix(Assembly assembly)
            {
                if (assembly == null) return true;
                lock (Gate) return !Shared.Contains(assembly);
            }
        }
    }
}
