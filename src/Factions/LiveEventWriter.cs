using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Factions.Core;
using Newtonsoft.Json;

namespace Factions
{
    /// <summary>Appends JSON-lines events to <c>events.jsonl</c> in the plugin storage directory.
    /// The Factions.Api tails this file and pushes each line to dashboard WebSocket clients.
    /// Rolling: once the file passes 4 MB it is renamed to <c>events.prev.jsonl</c> and a fresh one
    /// started.</summary>
    public sealed class LiveEventWriter : ILiveEventWriter
    {
        private const long RotateTriggerBytes = 4 * 1024 * 1024;

        private static readonly TimeSpan RotateCheckInterval = TimeSpan.FromSeconds(30);

        // File.AppendAllText writes the encoding preamble when it creates the file, and a BOM in
        // front of the first line is not valid JSON to a reader that splits the file on newlines
        // itself rather than going through a StreamReader.
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string _path;
        private readonly string _prevPath;
        private readonly object _lock = new object();
        private DateTime _lastRotateCheckUtc = DateTime.MinValue;

        public LiveEventWriter(string storageDir)
        {
            _path = Path.Combine(storageDir, "events.jsonl");
            _prevPath = Path.Combine(storageDir, "events.prev.jsonl");
        }

        public void WriteFleetDispatch(string factionTag, string fleetId, string kind,
                                       string originNodeId, string targetNodeId, string targetTag)
        {
            Append(new Dictionary<string, object>
            {
                { "type",        "fleet_dispatch" },
                { "at",          Iso(DateTime.UtcNow) },
                { "factionTag",  factionTag },
                { "fleetId",     fleetId ?? "" },
                { "kind",        kind },
                { "originNodeId", originNodeId ?? "" },
                { "targetNodeId", targetNodeId ?? "" },
                { "targetTag",   (object)targetTag },
            });
        }

        public void WriteDemandsSnapshot(string factionTag,
                                         List<DemandSnapshotRow> demands,
                                         List<InvestmentSignal> investments)
        {
            Append(DemandsSnapshotPayload.Build(factionTag, DateTime.UtcNow, demands, investments));
        }

        public void WriteTerritoryLoss(string factionTag, string nodeId, string cause)
        {
            Append(new Dictionary<string, object>
            {
                { "type",       "territory_loss" },
                { "at",         Iso(DateTime.UtcNow) },
                { "factionTag", factionTag },
                { "nodeId",     nodeId },
                { "cause",      cause },
            });
        }

        public void WriteHeartbeat(long simTick)
        {
            Append(new Dictionary<string, object>
            {
                { "type",    "heartbeat" },
                { "at",      Iso(DateTime.UtcNow) },
                { "simTick", simTick },
            });
        }

        // -----------------------------------------------------------------------

        private static string Iso(DateTime dt)
            => DemandsSnapshotPayload.Iso(dt);

        private void Append(object obj)
        {
            lock (_lock)
            {
                try
                {
                    string line = JsonConvert.SerializeObject(obj);
                    File.AppendAllText(_path, line + "\n", Utf8NoBom);
                    MaybeRotate();
                }
                catch { /* best-effort: events are advisory */ }
            }
        }

        // A single Flush() calls Append() once per faction plus a heartbeat, and WriteFleetDispatch
        // can burst independently of Flush() -- throttle how often the size check even runs.
        private void MaybeRotate()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastRotateCheckUtc < RotateCheckInterval) return;
            _lastRotateCheckUtc = now;
            Rotate();
        }

        // Rotate rather than rewrite in place. The previous shape read the file, dropped its oldest
        // lines and wrote the survivors back over the live path -- a truncate-and-replace under
        // Factions.Api's tailer, which follows this file by byte offset, so whatever the tailer had
        // not yet read was lost on every rewrite. A rename leaves the bytes the tailer is positioned
        // in untouched, and the fresh file starts at zero where the tailer's rotation check picks it
        // up.
        private void Rotate()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (!fi.Exists || fi.Length <= RotateTriggerBytes) return;

                try { if (File.Exists(_prevPath)) File.Delete(_prevPath); }
                catch { return; } // an unremovable predecessor would fail the Move below anyway

                File.Move(_path, _prevPath); // the next Append recreates events.jsonl
            }
            catch { }
        }
    }
}
