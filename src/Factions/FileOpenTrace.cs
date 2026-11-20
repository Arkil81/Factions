using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace Factions
{
    /// <summary>
    /// Which files SE opens on the game thread while an assembly step is probed (FAC-445). After the model
    /// warm-up, a WHI-24Drill's first 64 blocks still waited 389 ms on 0 ms of CPU reading 151 KB, and the
    /// same 151 KB on each of three spawns (2026-10-04 20:17:43 and 23:21:44, 2026-10-05 09:49:40 box-local):
    /// a file read at every grid creation that the warm-up does not cover. This names it.
    ///
    /// <para>Off unless <see cref="Active"/> is set by <see cref="GridAssembler"/>'s probe, and then only on
    /// the thread that set it, so the warm-up's own loads on SE's workers are not counted.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class FileOpenTrace
    {
        internal static volatile bool Active;
        private static int _thread;
        private static readonly List<KeyValuePair<string, double>> Opens = new List<KeyValuePair<string, double>>();

        internal static void Begin()
        {
            lock (Opens) Opens.Clear();
            _thread = Thread.CurrentThread.ManagedThreadId;
            Active = true;
        }

        /// <summary>Stops recording; the slowest opens, at most <paramref name="max"/>, as
        /// <c>path:ms</c>, with how many there were in all.</summary>
        internal static string End(int max)
        {
            Active = false;
            List<KeyValuePair<string, double>> opens;
            lock (Opens) { opens = new List<KeyValuePair<string, double>>(Opens); Opens.Clear(); }
            if (opens.Count == 0) return " files=0";
            opens.Sort((a, b) => b.Value.CompareTo(a.Value));
            double total = 0;
            foreach (KeyValuePair<string, double> o in opens) total += o.Value;
            var sb = new StringBuilder(" files=").Append(opens.Count).Append('/').Append(total.ToString("F1")).Append("ms");
            for (int i = 0; i < opens.Count && i < max; i++)
                sb.Append(i == 0 ? " [" : " | ").Append(Shorten(opens[i].Key)).Append(':').Append(opens[i].Value.ToString("F1"));
            return sb.Append(']').ToString();
        }

        private static string Shorten(string path)
        {
            if (string.IsNullOrEmpty(path)) return "?";
            int c = path.IndexOf("Content", StringComparison.OrdinalIgnoreCase);
            if (c >= 0) return path.Substring(c);
            int m = path.IndexOf("Mods", StringComparison.OrdinalIgnoreCase);
            return m >= 0 ? path.Substring(m) : path;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type fs = AccessTools.TypeByName("VRage.FileSystem.MyFileSystem");
            if (fs == null) yield break;
            MethodBase m;
            if ((m = AccessTools.Method(fs, "Open", new[] { typeof(string), typeof(FileMode), typeof(FileAccess), typeof(FileShare) })) != null) yield return m;
            if ((m = AccessTools.Method(fs, "OpenRead", new[] { typeof(string) })) != null) yield return m;
            if ((m = AccessTools.Method(fs, "OpenRead", new[] { typeof(string), typeof(string) })) != null) yield return m;
            if ((m = AccessTools.Method(fs, "FileExists", new[] { typeof(string) })) != null) yield return m;
        }

        private static void Prefix(out long __state)
        {
            __state = Active && Thread.CurrentThread.ManagedThreadId == _thread ? Stopwatch.GetTimestamp() : 0;
        }

        private static void Postfix(long __state, object[] __args, MethodBase __originalMethod)
        {
            if (__state == 0) return;
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                string path = __args != null && __args.Length > 0 ? __args[0] as string : null;
                if (__args != null && __args.Length > 1 && __args[1] is string) path = path + "/" + (string)__args[1];
                string tag = __originalMethod.Name == "FileExists" ? "?" : "";
                lock (Opens) if (Opens.Count < 2000) Opens.Add(new KeyValuePair<string, double>(tag + path, ms));
            }
            catch { }
        }
    }
}
