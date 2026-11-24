using System;
using System.Diagnostics;
using System.Threading;
using Factions.Core;
using NLog;

namespace Factions
{
    /// <summary>
    /// The thread that reads <see cref="StallWatch"/> while the game thread is stuck, and the markers
    /// the plugin's update calls at each section boundary.
    ///
    /// <para>Always on: it logs only when one section has held the game thread for
    /// <see cref="ThresholdMs"/>, which is a fault a player sees, so each line is one real event
    /// (operator, 2026-11-22, after a 26.7 s freeze on production Main that nothing could split).
    /// <c>outside</c> stalls are SE, Torch or another plugin, a world save among them.</para>
    /// </summary>
    internal static class StallWatchdog
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        internal const double ThresholdMs = 2000, RepeatMs = 10000;
        private const int PollMs = 250;

        private static readonly StallWatch Watch = new StallWatch(ThresholdMs, RepeatMs, Stopwatch.Frequency);
        private static Thread _thread;
        private static volatile bool _stop;
        private static int _failures;

        /// <summary>Game thread: <paramref name="section"/> starts now. String literals only.</summary>
        internal static void Enter(string section)
        {
            Watch.Enter(section, Stopwatch.GetTimestamp());
        }

        internal static void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "Factions stall watchdog", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            Log.Info("STALL_WATCHDOG started thresholdMs=" + ThresholdMs + " repeatMs=" + RepeatMs + " pollMs=" + PollMs);
        }

        internal static void Stop()
        {
            Thread t = _thread;
            if (t == null) return;
            _stop = true;
            Watch.Disarm();
            t.Join(2 * PollMs);
            _thread = null;
        }

        /// <summary>From session load: polls report from the next section boundary on.</summary>
        internal static void Arm()
        {
            Watch.Arm();
        }

        /// <summary>From session unload: loading and unloading are not stalls.</summary>
        internal static void Disarm()
        {
            Watch.Disarm();
        }

        private static void Run()
        {
            while (!_stop)
            {
                Thread.Sleep(PollMs);
                try
                {
                    StallEvent e = Watch.Poll(Stopwatch.GetTimestamp(), GC.CollectionCount(2));
                    if (e.Kind != StallEventKind.None) Log.Warn(e.Format());
                }
                catch (Exception ex)
                {
                    if (++_failures <= 3) Log.Warn(ex, "Stall watchdog poll failed.");
                }
            }
        }
    }
}
