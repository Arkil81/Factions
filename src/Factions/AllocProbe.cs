using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Factions.Core;
using NLog;

namespace Factions
{
    /// <summary>
    /// Attributes managed allocation (<c>factions debug alloc</c>). Reads <c>GC.GetAllocatedBytesForCurrentThread</c>
    /// beside the timings <see cref="HitchProbe"/> (session components, plugins) and <see cref="EntityCostPatches"/>
    /// (entity updates, game thread and workers) already take, plus brackets round Factions' own sections, and every
    /// 30 s writes <c>ALLOC_SPLIT</c>: process total, game thread, the rest (workers, network, SE's own threads), and
    /// the top contributors in MB/s. Measured 2026-10-06: 60-100 MB/s allocated, gen0/gen1 pauses of 60-200 ms with
    /// 0 ms of game-thread CPU inside them.
    /// </summary>
    internal static class AllocProbe
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const double ReportSeconds = 30.0;
        private const int TopN = 12;

        private static readonly Dictionary<string, long> Components = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> Plugins = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> Sections = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> EntitiesMain = new Dictionary<string, long>();
        private static readonly object WorkerLock = new object();
        private static readonly Dictionary<string, long> EntitiesWorker = new Dictionary<string, long>();

        [ThreadStatic] private static long _entityStart;
        private static long _callStart, _sectionStart;
        private static long _windowStartTicks, _totalAtStart, _mainAtStart;
        private static int _mainThreadId;

        internal static bool On { get { return DebugTraces.Alloc; } }

        internal static long Now() { return GC.GetAllocatedBytesForCurrentThread(); }

        // ---- session components and plugins: sequential calls on the game thread ----------------------
        internal static void CallStart() { if (On) _callStart = Now(); }
        internal static void ComponentEnd(Type t) { if (On && t != null) Add(Components, t.Name, Now() - _callStart); }
        internal static void PluginEnd(Type t) { if (On && t != null) Add(Plugins, t.Name, Now() - _callStart); }

        // ---- entity updates: per thread ----------------------------------------------------------------
        internal static void EntityStart() { if (On) _entityStart = Now(); }
        internal static void EntityEnd(Type t, bool mainThread)
        {
            if (!On || t == null || _entityStart == 0) return;
            long d = Now() - _entityStart;
            _entityStart = 0;
            if (d <= 0) return;
            if (mainThread) Add(EntitiesMain, t.Name, d);
            else lock (WorkerLock) Add(EntitiesWorker, t.Name, d);
        }

        // ---- Factions sections: sequential on the game thread ---------------------------------------
        internal static void SectionStart() { if (On) _sectionStart = Now(); }
        internal static void SectionEnd(string name) { if (On) Add(Sections, name, Now() - _sectionStart); }
        internal static long Begin() { return On ? Now() : 0; }
        internal static void AddKeyed(string name, long bytes) { if (On) Add(Sections, name, bytes); }
        internal static void End(string name, long start) { if (On && start != 0) Add(Sections, name, Now() - start); }

        private static void Add(Dictionary<string, long> d, string key, long bytes)
        {
            if (bytes <= 0) return;
            long v;
            d.TryGetValue(key, out v);
            d[key] = v + bytes;
        }

        /// <summary>Once per frame on the game thread.</summary>
        internal static void Tick()
        {
            if (!On)
            {
                if (_windowStartTicks != 0) { _windowStartTicks = 0; Clear(); }
                return;
            }
            long now = Stopwatch.GetTimestamp();
            if (_windowStartTicks == 0)
            {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                Begin(now);
                return;
            }
            double secs = (now - _windowStartTicks) / (double)Stopwatch.Frequency;
            if (secs < ReportSeconds) return;
            try
            {
                if (!AppDomain.MonitoringIsEnabled) AppDomain.MonitoringIsEnabled = true;
                long total = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - _totalAtStart;
                long main = Now() - _mainAtStart;
                var sb = new StringBuilder("ALLOC_SPLIT secs=").Append(secs.ToString("0", CultureInfo.InvariantCulture))
                    .Append(" totalMBps=").Append(Rate(total, secs))
                    .Append(" gameThreadMBps=").Append(Rate(main, secs))
                    .Append(" otherThreadsMBps=").Append(Rate(total - main, secs))
                    .Append(" gc=").Append(GC.CollectionCount(0)).Append('/').Append(GC.CollectionCount(1)).Append('/').Append(GC.CollectionCount(2));
                Top(sb, "components", Components, secs);
                Top(sb, "plugins", Plugins, secs);
                Top(sb, "factions", Sections, secs);
                Top(sb, "entitiesMain", EntitiesMain, secs);
                lock (WorkerLock) Top(sb, "entitiesWorker", EntitiesWorker, secs);
                Log.Info(sb.ToString());
            }
            catch (Exception ex) { Log.Warn("ALLOC_SPLIT failed: " + ex.Message); }
            Begin(now);
        }

        private static void Begin(long now)
        {
            Clear();
            if (!AppDomain.MonitoringIsEnabled) AppDomain.MonitoringIsEnabled = true;
            _totalAtStart = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            _mainAtStart = Now();
            _windowStartTicks = now;
        }

        private static void Clear()
        {
            Components.Clear(); Plugins.Clear(); Sections.Clear(); EntitiesMain.Clear();
            lock (WorkerLock) EntitiesWorker.Clear();
        }

        private static void Top(StringBuilder sb, string label, Dictionary<string, long> d, double secs)
        {
            if (d.Count == 0) return;
            long sum = 0;
            foreach (long v in d.Values) sum += v;
            sb.Append(" | ").Append(label).Append('=').Append(Rate(sum, secs)).Append(':');
            foreach (KeyValuePair<string, long> kv in d.OrderByDescending(k => k.Value).Take(TopN))
                sb.Append(' ').Append(kv.Key).Append('=').Append(Rate(kv.Value, secs));
        }

        private static string Rate(long bytes, double secs)
        {
            return (bytes / 1048576.0 / Math.Max(0.001, secs)).ToString("0.00", CultureInfo.InvariantCulture);
        }

        internal static bool IsMainThread { get { return Thread.CurrentThread.ManagedThreadId == _mainThreadId; } }
    }
}
