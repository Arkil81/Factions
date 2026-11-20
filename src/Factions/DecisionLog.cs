using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Factions
{
    /// <summary>
    /// Day-rolling decision/event log. One file per local calendar day
    /// (<c>Factions-YYYY-MM-DD.log</c>), timestamps in local time to match Torch's own logs.
    /// Append-only; every operation is guarded so logging can never crash the simulation tick.
    ///
    /// <para><b>Written off the game thread.</b> <see cref="Write"/> stamps the line and queues it; a
    /// background thread writes and flushes each batch. Writing on the caller's thread with
    /// <c>AutoFlush</c> made every line a disk write on the game thread: at 20:16:49 box-local
    /// 2026-10-04 a sim tick wrote 318 lines while a game download kept the disk busy, and that frame
    /// took 10.5 s, all of it in this plugin (FAC-445). The queue is bounded; a line past the bound is
    /// dropped and the drop counted into the file.</para>
    /// </summary>
    public sealed class DecisionLog : IDisposable
    {
        /// <summary>Lines held for the writer before new ones are dropped: well above a tick's burst
        /// (318 at the worst measured), small enough that a stalled disk cannot grow the heap freely.</summary>
        private const int MaxQueued = 100000;

        private readonly string _dir;
        private readonly object _gate = new object();
        private List<KeyValuePair<DateTime, string>> _queue = new List<KeyValuePair<DateTime, string>>();
        private List<KeyValuePair<DateTime, string>> _spare = new List<KeyValuePair<DateTime, string>>();
        private int _dropped;
        private bool _stopping;
        private readonly Thread _thread;

        /// <summary>Set for a log that keeps no file: every line goes here, stamped UTC.</summary>
        private readonly Action<DateTime, string> _sink;

        // Writer-thread state only.
        private string _date;
        private StreamWriter _writer;

        /// <summary>A log with no file of its own: a Nexus agent's, whose lines the authority files.</summary>
        public DecisionLog(Action<DateTime, string> sink)
        {
            _sink = sink;
        }

        public DecisionLog(string dir)
        {
            _dir = dir;
            try { Directory.CreateDirectory(_dir); } catch { }
            _thread = new Thread(Run) { IsBackground = true, Name = "Factions DecisionLog" };
            _thread.Start();
        }

        public void Write(string line)
        {
            if (_sink != null)
            {
                try { _sink(DateTime.UtcNow, line); }
                catch { /* never let logging break the tick */ }
                return;
            }
            WriteAt(DateTime.Now, line);
        }

        /// <summary>Files a line under <paramref name="at"/> (local time) rather than now: a line
        /// another server wrote earlier and sent here.</summary>
        public void WriteAt(DateTime at, string line)
        {
            if (_sink != null) { Write(line); return; }
            try
            {
                DateTime now = at;
                lock (_gate)
                {
                    if (_stopping) return;
                    if (_queue.Count >= MaxQueued) { _dropped++; return; }
                    _queue.Add(new KeyValuePair<DateTime, string>(now, line));
                    if (_queue.Count == 1) Monitor.Pulse(_gate);
                }
            }
            catch { /* never let logging break the tick */ }
        }

        private void Run()
        {
            while (true)
            {
                List<KeyValuePair<DateTime, string>> batch;
                int dropped;
                bool stopping;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_stopping) Monitor.Wait(_gate);
                    batch = _queue;
                    _queue = _spare;
                    _spare = batch;
                    dropped = _dropped;
                    _dropped = 0;
                    stopping = _stopping;
                }
                WriteBatch(batch, dropped);
                batch.Clear();
                if (stopping)
                {
                    lock (_gate) { if (_queue.Count > 0) continue; }
                    CloseWriter();
                    return;
                }
            }
        }

        private void WriteBatch(List<KeyValuePair<DateTime, string>> batch, int dropped)
        {
            try
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    DateTime at = batch[i].Key;
                    string day = at.ToString("yyyy-MM-dd");
                    if (_writer == null || day != _date) Roll(day);
                    _writer.WriteLine(at.ToString("HH:mm:ss") + "  " + batch[i].Value);
                }
                if (dropped > 0 && _writer != null)
                    _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  DECISION_LOG_DROPPED lines=" + dropped);
                if (_writer != null) _writer.Flush();
            }
            catch { /* a failed write loses its batch, never the writer thread */ }
        }

        private void Roll(string day)
        {
            CloseWriter();
            _date = day;
            string path = Path.Combine(_dir, "Factions-" + day + ".log");
            _writer = new StreamWriter(path, true);
        }

        private void CloseWriter()
        {
            try { if (_writer != null) { _writer.Flush(); _writer.Dispose(); } } catch { }
            _writer = null;
        }

        /// <summary>Writes what is queued and closes the file; waits up to two seconds for the writer.</summary>
        public void Dispose()
        {
            if (_thread == null) return;
            lock (_gate)
            {
                _stopping = true;
                Monitor.Pulse(_gate);
            }
            try { _thread.Join(2000); } catch { }
        }
    }
}
