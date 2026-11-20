using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using NLog;

namespace Factions
{
    /// <summary>
    /// Ships line-protocol batches to InfluxDB without the game thread ever waiting on a socket.
    ///
    /// <para><b>The whole design constraint is one sentence:</b> a metrics endpoint that stops
    /// answering must cost the simulation nothing. So the game thread only ever does
    /// <see cref="Enqueue"/> — a lock, a list add, and a pulse — and one background thread owns
    /// every DNS lookup, connect, write and timeout. A server that is down, slow, or a
    /// <c>192.168.x</c> address that no longer resolves cannot stall a frame.</para>
    ///
    /// <para><b>The queue is bounded and the loss is counted.</b> During an outage batches would
    /// otherwise accumulate at one per window forever. Past the bound the oldest is dropped, which
    /// is the right end to lose — recent samples are the ones anybody is looking at — and the drop
    /// count rides on the next batch that gets through, so a gap in the series is never silent.</para>
    /// </summary>
    internal sealed class InfluxPublisher : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly string _writeUrl;
        private readonly string _authHeader;
        private readonly int _timeoutMs;
        private readonly int _maxQueued;

        private readonly object _gate = new object();
        private readonly Queue<string> _queue = new Queue<string>();
        private Thread _thread;
        private bool _stopping;

        private long _sent, _failed, _dropped;
        private string _lastError;
        private DateTime _lastErrorUtc;
        /// <summary>Suppresses repeats of one endpoint being down — the failure recurs every window
        /// and the log is read by an operator looking for something else.</summary>
        private DateTime _nextErrorLogUtc;

        public long SentBatches   { get { lock (_gate) return _sent; } }
        public long FailedBatches { get { lock (_gate) return _failed; } }
        public long DroppedBatches { get { lock (_gate) return _dropped; } }
        public string LastError
        {
            get { lock (_gate) return _lastError; }
        }
        public DateTime LastErrorUtc { get { lock (_gate) return _lastErrorUtc; } }

        public int QueueDepth { get { lock (_gate) return _queue.Count; } }

        public InfluxPublisher(ServerPerfConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");

            // Nanosecond precision is line protocol's default, and is what the builder emits.
            _writeUrl = config.InfluxUrl + "/write?db=" + Uri.EscapeDataString(config.Database) + "&precision=ns";
            _timeoutMs = config.TimeoutSeconds * 1000;
            _maxQueued = config.MaxQueuedBatches;

            if (!string.IsNullOrEmpty(config.Username))
            {
                string pair = config.Username + ":" + (config.Password ?? "");
                _authHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair));
            }
        }

        /// <summary>Hands a batch to the sender. Returns immediately; called from the game thread.
        /// <para>A null or empty payload is ignored rather than queued — an empty POST body is a 400
        /// from Influx, and "this window produced nothing" is an ordinary state.</para></summary>
        public void Enqueue(string lineProtocol)
        {
            if (string.IsNullOrEmpty(lineProtocol)) return;

            lock (_gate)
            {
                if (_stopping) return;
                while (_queue.Count >= _maxQueued)
                {
                    _queue.Dequeue();
                    _dropped++;
                }
                _queue.Enqueue(lineProtocol);
                if (_thread == null)
                {
                    // Started on first use rather than in the constructor so a configured-but-idle
                    // publisher costs no thread at all.
                    _thread = new Thread(SendLoop);
                    _thread.IsBackground = true;   // must never hold up a Torch shutdown
                    _thread.Name = "Factions-InfluxPublisher";
                    _thread.Start();
                }
                Monitor.Pulse(_gate);
            }
        }

        private void SendLoop()
        {
            while (true)
            {
                string payload;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_stopping) Monitor.Wait(_gate);
                    if (_queue.Count == 0) return;      // stopping and drained
                    payload = _queue.Dequeue();
                }

                try
                {
                    Post(payload);
                    lock (_gate) { _sent++; }
                }
                catch (Exception ex)
                {
                    // Deliberately not requeued. A batch carries its own timestamps, so a retry
                    // would eventually write points hours late; and during an outage a retrying
                    // sender never drains, which turns a metrics problem into a memory one.
                    lock (_gate)
                    {
                        _failed++;
                        _lastError = Describe(ex);
                        _lastErrorUtc = DateTime.UtcNow;
                        if (DateTime.UtcNow >= _nextErrorLogUtc)
                        {
                            _nextErrorLogUtc = DateTime.UtcNow.AddMinutes(10);
                            Log.Warn("Server perf: InfluxDB write failed: " + _lastError
                                     + " (" + _failed + " failed, " + _dropped + " dropped so far).");
                        }
                    }
                }
            }
        }

        private void Post(string payload)
        {
            var request = (HttpWebRequest)WebRequest.Create(_writeUrl);
            request.Method = "POST";
            request.ContentType = "text/plain; charset=utf-8";
            request.Timeout = _timeoutMs;
            request.ReadWriteTimeout = _timeoutMs;
            // Torch's process-wide default is 2 connections per endpoint; this sender is serial and
            // holding one alive across windows avoids a connect per batch.
            request.KeepAlive = true;
            if (_authHeader != null) request.Headers[HttpRequestHeader.Authorization] = _authHeader;

            byte[] body = Encoding.UTF8.GetBytes(payload);
            request.ContentLength = body.Length;
            using (Stream stream = request.GetRequestStream())
                stream.Write(body, 0, body.Length);

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                // Influx 1.x answers a good write with 204. A 200 means it accepted the request but
                // is reporting something in the body, which is not a success worth counting as one.
                if (response.StatusCode != HttpStatusCode.NoContent)
                    throw new IOException("unexpected status " + (int)response.StatusCode
                                          + " " + response.StatusDescription);
            }
        }

        /// <summary>Turns an exception into the one line an operator can act on.
        /// <para>Influx puts the actual complaint — a bad field type, an unparseable line, a missing
        /// database — in the body of a 4xx, and <c>WebException.Message</c> says only "The remote
        /// server returned an error". Reading the body is the difference between a diagnosable
        /// failure and a mystery.</para></summary>
        private static string Describe(Exception ex)
        {
            var web = ex as WebException;
            if (web == null) return ex.Message;

            var response = web.Response as HttpWebResponse;
            if (response == null) return web.Status + ": " + web.Message;

            string body = "";
            try
            {
                using (Stream s = response.GetResponseStream())
                {
                    if (s != null)
                    {
                        using (var reader = new StreamReader(s))
                        {
                            body = reader.ReadToEnd();
                            if (body.Length > 400) body = body.Substring(0, 400) + "...";
                        }
                    }
                }
            }
            catch { /* the status alone is still worth reporting */ }
            finally { response.Close(); }

            return "HTTP " + (int)response.StatusCode + " " + response.StatusDescription
                   + (body.Length > 0 ? ": " + body.Trim() : "");
        }

        public void Dispose()
        {
            Thread thread;
            lock (_gate)
            {
                _stopping = true;
                thread = _thread;
                _thread = null;
                Monitor.PulseAll(_gate);
            }
            // Bounded: the queue drains or the wait expires. A telemetry sender must not be able to
            // hold a shutdown open, and it is a background thread, so abandoning it is safe.
            if (thread != null) thread.Join(TimeSpan.FromSeconds(3));
        }
    }
}
