using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Factions
{
    /// <summary>
    /// What Windows says the game thread is doing while a probed assembly step runs long (FAC-445). Steps
    /// of 0.2-1 s ran on 0-47 ms of game-thread CPU with no file opens, no collection and no concurrent
    /// model load (2026-10-05 10:07-10:34 box-local); this tells preemption (Ready: runnable, not
    /// scheduled) from a wait on another thread (Wait / UserRequest, Executive, EventPairLow...) and from
    /// paging (Wait / PageIn).
    ///
    /// <para>A background thread polls; past <see cref="StartAfterMs"/> into a step it reads the game
    /// thread's <c>ProcessThread.ThreadState</c> and <c>WaitReason</c> every <see cref="EveryMs"/>. Read
    /// only: nothing is suspended. Under <c>factions debug spawncost</c>.</para>
    /// </summary>
    internal static class ThreadStateSampler
    {
        private const int EveryMs = 10;
        private const double StartAfterMs = 30;

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, int> Tally = new Dictionary<string, int>(StringComparer.Ordinal);
        private static volatile int _nativeId;
        private static long _since;
        private static volatile bool _active;
        private static Thread _thread;

        internal static void Begin()
        {
            lock (Gate) Tally.Clear();
            _nativeId = GetCurrentThreadId();
            Interlocked.Exchange(ref _since, Stopwatch.GetTimestamp());
            _active = true;
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Factions thread-state sampler" };
                _thread.Start();
            }
        }

        /// <summary>Stops sampling; the tally as <c> threads=State/Reason:n ...</c>, or empty.</summary>
        internal static string End()
        {
            _active = false;
            lock (Gate)
            {
                if (Tally.Count == 0) return " threads=none";
                var rows = new List<KeyValuePair<string, int>>(Tally);
                rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                var sb = new StringBuilder(" threads=");
                for (int i = 0; i < rows.Count; i++)
                    sb.Append(i == 0 ? "" : ",").Append(rows[i].Key).Append(':').Append(rows[i].Value);
                Tally.Clear();
                return sb.ToString();
            }
        }

        private static void Run()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(EveryMs);
                    if (!_active) continue;
                    double ms = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _since)) * 1000.0 / Stopwatch.Frequency;
                    if (ms < StartAfterMs) continue;
                    string state = Read(_nativeId);
                    if (state == null || !_active) continue;
                    lock (Gate)
                    {
                        int n;
                        Tally.TryGetValue(state, out n);
                        Tally[state] = n + 1;
                    }
                }
                catch { }
            }
        }

        private static string Read(int nativeId)
        {
            using (Process p = Process.GetCurrentProcess())
            {
                foreach (ProcessThread t in p.Threads)
                {
                    if (t.Id != nativeId) continue;
                    System.Diagnostics.ThreadState s = t.ThreadState;
                    return s == System.Diagnostics.ThreadState.Wait ? "Wait/" + t.WaitReason : s.ToString();
                }
            }
            return null;
        }
    }
}
