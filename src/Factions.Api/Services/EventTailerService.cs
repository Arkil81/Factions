using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Factions.Api.Services
{
    /// <summary>Background service that tails <c>events.jsonl</c> and broadcasts each new line
    /// to all connected WebSocket clients. Polls every second; safe to have zero clients.</summary>
    public sealed class EventTailerService : BackgroundService
    {
        private readonly string _eventsPath;
        private readonly List<WebSocket> _clients = new List<WebSocket>();
        private readonly SemaphoreSlim _clientsLock = new SemaphoreSlim(1, 1);

        public EventTailerService(SnapshotService svc)
        {
            _eventsPath = Path.Combine(svc.DataDir, "events.jsonl");
        }

        /// <summary>Connected WebSocket clients, read without the lock: a diagnostic, not a gate.</summary>
        public int ClientCount => _clients.Count;

        /// <summary>Registers the WebSocket and blocks until the client disconnects.</summary>
        public async Task ServeClientAsync(WebSocket ws, CancellationToken ct)
        {
            await _clientsLock.WaitAsync(ct);
            try { _clients.Add(ws); }
            finally { _clientsLock.Release(); }

            // Block until the client closes or the server shuts down
            var buf = new byte[256];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }

            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

            await _clientsLock.WaitAsync(CancellationToken.None);
            try { _clients.Remove(ws); }
            finally { _clientsLock.Release(); }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Start from the end of any existing file so we don't replay history on startup.
            var reader = new JsonlTailReader(_eventsPath, startAtEnd: true);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Read the whole batch before broadcasting: the file handle is released first, so
                    // a slow client cannot hold it open across the writer's rotating rename.
                    foreach (string line in reader.ReadNewLines())
                        await BroadcastAsync(line, stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch { /* best-effort; retry next poll */ }

                try { await Task.Delay(1000, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task BroadcastAsync(string json, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var seg = new ArraySegment<byte>(bytes);

            List<WebSocket> snapshot;
            await _clientsLock.WaitAsync(ct);
            try { snapshot = new List<WebSocket>(_clients); }
            finally { _clientsLock.Release(); }

            if (snapshot.Count == 0) return;

            var dead = new List<WebSocket>();
            foreach (var ws in snapshot)
            {
                if (ws.State != WebSocketState.Open) { dead.Add(ws); continue; }
                try { await ws.SendAsync(seg, WebSocketMessageType.Text, true, ct); }
                catch { dead.Add(ws); }
            }

            if (dead.Count > 0)
            {
                await _clientsLock.WaitAsync(CancellationToken.None);
                try { foreach (var ws in dead) _clients.Remove(ws); }
                finally { _clientsLock.Release(); }
            }
        }
    }
}
