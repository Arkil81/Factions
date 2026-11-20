using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the K-menu Events tab: the viewing player's faction history, as recorded by
    /// whatever resolved each event (see <see cref="FactionEventLog"/>). A linked client subscribes
    /// on entering the tab and unsubscribes on leaving it, so a player who never opens it costs
    /// nothing.
    ///
    /// Cheaper than the Assets register — the ring is already assembled and capped, and there is no
    /// world sweep — so this sends on the ring's own revision rather than a fixed cadence: a raid
    /// reaches an open tab on the next scanner tick, and a quiet faction sends nothing at all.
    /// </summary>
    internal sealed class PlayerEventsManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs with the Events tab open.</summary>
        private readonly HashSet<ulong> _streaming = new HashSet<ulong>();

        /// <summary>Last ring revision sent to each subscriber. A subscriber with no entry here is
        /// owed a snapshot immediately, which is also how a fresh subscribe is served without
        /// waiting for the faction's next event.</summary>
        private readonly Dictionary<ulong, int> _sentRevision = new Dictionary<ulong, int>();

        public PlayerEventsManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink != null) _uplink.RegisterHandler(WireMessageKind.EventsRequest, OnEventsRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnEventsRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadEventsRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Events request from " + steamId + " was unreadable.");
                return;
            }

            if (stream)
            {
                _streaming.Add(steamId);
                _sentRevision.Remove(steamId);   // owed a snapshot on the next tick
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("EVENTS_SUBSCRIBE steam=" + steamId);
            }
            else
            {
                _streaming.Remove(steamId);
                _sentRevision.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("EVENTS_UNSUBSCRIBE steam=" + steamId);
            }
        }

        /// <summary>Runs on the scanner cadence, and does nothing at all while no one has the tab
        /// open — the normal case.</summary>
        public void Tick()
        {
            if (_streaming.Count == 0) return;
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;

            foreach (LinkedViewer player in LinkedViewers.Collect())
            {
                ulong steamId = player.SteamId;
                if (!_streaming.Contains(steamId)) continue;

                MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(player.IdentityId);
                string tag = seFaction != null ? seFaction.Tag : null;
                FactionState owner = tag != null ? _runtime.FindFaction(tag) : null;

                // A player in no faction, or in one Factions has never registered, has no history.
                // Revision 0 is a real answer here, and sending it once tells the tab to say so
                // instead of waiting forever on a first snapshot.
                int revision = owner != null ? owner.EventsRevision : 0;

                int sent;
                if (_sentRevision.TryGetValue(steamId, out sent) && sent == revision) continue;
                _sentRevision[steamId] = revision;

                SendSnapshot(steamId, tag, owner);
            }
        }

        private void SendSnapshot(ulong steamId, string tag, FactionState owner)
        {
            var wire = new List<WireFactionEvent>();
            if (owner != null)
            {
                // Newest first: the tab is read from the top, and the cap means the oldest entries
                // are the ones a truncated message can afford to lose.
                for (int i = owner.RecentEvents.Count - 1; i >= 0; i--)
                    wire.Add(Describe(owner.RecentEvents[i], owner.Tag));
            }

            _uplink.Send(steamId, FactionsWire.WriteEventsSnapshot(tag ?? "", wire));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("EVENTS_SNAPSHOT steam=" + steamId + " events=" + wire.Count);
        }

        private WireFactionEvent Describe(FactionEvent e, string viewerTag)
        {
            bool viewerIsActor = string.Equals(e.ActorTag, viewerTag, StringComparison.OrdinalIgnoreCase);
            return new WireFactionEvent
            {
                UtcTicks = e.Utc.Ticks,
                Kind = (byte)e.Kind,
                ActorTag = e.ActorTag ?? "",
                OtherTag = e.OtherTag ?? "",
                NodeId = e.NodeId ?? "",
                NodeName = NodeDisplayName(e.NodeId),
                Amount = e.Amount,
                Ratio = e.Ratio,
                ViewerIsActor = viewerIsActor,
            };
        }

        /// <summary>Empty for a node that no longer exists or was never named — the id still goes
        /// out, so a lost node reads as an id rather than as nowhere.</summary>
        private string NodeDisplayName(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || _runtime.World == null) return "";
            SpatialNode node;
            if (!_runtime.World.Graph.Nodes.TryGetValue(nodeId, out node) || node == null) return "";
            return node.DisplayName ?? "";
        }

        public void PruneOffline(ICollection<ulong> onlineSteamIds)
        {
            if (_streaming.Count == 0) return;
            List<ulong> stale = null;
            foreach (ulong steamId in _streaming)
            {
                if (onlineSteamIds.Contains(steamId)) continue;
                if (stale == null) stale = new List<ulong>();
                stale.Add(steamId);
            }
            if (stale == null) return;
            foreach (ulong steamId in stale)
            {
                _streaming.Remove(steamId);
                _sentRevision.Remove(steamId);
            }
        }

        public void Close()
        {
            _streaming.Clear();
            _sentRevision.Clear();
        }
    }
}
