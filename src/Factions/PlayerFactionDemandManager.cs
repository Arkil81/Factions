using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the Assets tab's Demand window: everything the viewing player's own faction is
    /// asking for — every live <c>Item:</c> and <c>Ship:</c> row, pinned to a station or faction-wide —
    /// and what ended inside the register's retention. The faction-level variant of the station
    /// window's Demand tab (<see cref="PlayerNodeDetailManager"/>), which shows one node's rows only.
    ///
    /// <para>Sends on change: the encoded snapshot is compared with the one last sent to that client,
    /// so a window left open on a quiet faction costs one build per pass and no traffic. Only the
    /// player's OWN faction is ever described, the rule <see cref="PlayerCampaignsManager"/>
    /// follows.</para>
    /// </summary>
    internal sealed class PlayerFactionDemandManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs with the window open.</summary>
        private readonly HashSet<ulong> _streaming = new HashSet<ulong>();

        /// <summary>The bytes last sent to each subscriber. A subscriber with no entry is owed a
        /// snapshot immediately.</summary>
        private readonly Dictionary<ulong, byte[]> _sent = new Dictionary<ulong, byte[]>();

        public PlayerFactionDemandManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.FactionDemandRequest, OnRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadFactionDemandRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Faction demand request from " + steamId + " was unreadable.");
                return;
            }
            _sent.Remove(steamId);
            if (stream) _streaming.Add(steamId);
            else _streaming.Remove(steamId);
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic((stream ? "FACTION_DEMAND_SUBSCRIBE" : "FACTION_DEMAND_UNSUBSCRIBE")
                                     + " steam=" + steamId);
        }

        /// <summary>Runs on the scanner cadence, and does nothing while nobody subscribes.</summary>
        public void Tick()
        {
            if (_streaming.Count == 0) return;
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;

            foreach (LinkedViewer player in LinkedViewers.Collect())
            {
                ulong steamId = player.SteamId;
                if (!_streaming.Contains(steamId)) continue;

                byte[] bytes = FactionsWire.WriteFactionDemandSnapshot(Describe(ViewerFaction(player.IdentityId)));
                byte[] sent;
                if (_sent.TryGetValue(steamId, out sent) && SameBytes(sent, bytes)) continue;
                _sent[steamId] = bytes;
                _uplink.Send(steamId, bytes);
                if (UplinkManager.DebugTraffic)
                    _uplink.WriteTraffic("FACTION_DEMAND_SNAPSHOT steam=" + steamId + " bytes=" + bytes.Length);
            }
        }

        /// <summary>The faction's demand as the window shows it. A station-pinned row reads that
        /// station's stock and smoothed use; a faction-wide one reads the sum over every station the
        /// faction owns, which is the stock it could be met from.</summary>
        private WireFactionDemand Describe(FactionState owner)
        {
            var snapshot = new WireFactionDemand
            {
                FactionTag = owner != null ? owner.Tag : "",
                WindowSeconds = (int)DemandRegister.EndedRetentionSeconds,
            };
            if (owner == null) return snapshot;

            WorldModel world = _runtime.World;
            var owned = new List<SpatialNode>();
            var ownedIds = new List<string>();
            foreach (SpatialNode node in world.Graph.OwnedBy(owner.Tag))
            {
                if (node == null) continue;
                owned.Add(node);
                ownedIds.Add(node.Id);
            }
            var names = new Dictionary<string, string>(StringComparer.Ordinal);

            List<DemandSnapshotRow> ranked = DemandSnapshotOrder.RankForSnapshot(
                world.Demand.SnapshotFactionWindowDemands(owner.Tag), world, world.Tuning);
            snapshot.DemandTotal = ranked.Count;
            int count = Math.Min(ranked.Count, FactionsWire.MaxFactionDemandRows);
            snapshot.Demand = new WireNodeDemand[count];
            for (int i = 0; i < count; i++)
            {
                DemandEntry d = ranked[i].Demand;
                string key = d.Kind.StartsWith(DemandRegister.ItemPrefix, StringComparison.Ordinal)
                    ? d.Kind.Substring(DemandRegister.ItemPrefix.Length) : null;
                long have = 0;
                double used = 0;
                if (key != null)
                {
                    SpatialNode at = Node(d.NodeId);
                    if (at != null)
                    {
                        at.Inventory.TryGetValue(key, out have);
                        used = ExtractionPlanner.DrawPerHour(at, key, world.ItemVolumes, world.Tuning);
                    }
                    else if (string.IsNullOrEmpty(d.NodeId))
                    {
                        foreach (SpatialNode node in owned)
                        {
                            long held;
                            if (node.Inventory.TryGetValue(key, out held)) have += held;
                            used += ExtractionPlanner.DrawPerHour(node, key, world.ItemVolumes, world.Tuning);
                        }
                    }
                }
                Name(names, d.NodeId);
                Name(names, d.DestNodeId);
                snapshot.Demand[i] = new WireNodeDemand
                {
                    Kind = d.Kind,
                    AtNodeId = d.NodeId ?? "",
                    DestNodeId = d.DestNodeId ?? "",
                    Quantity = d.Quantity,
                    HasHave = key != null,
                    Have = have,
                    UsedPerHour = used,
                    Class = (byte)d.Class,
                    DestinedForSale = d.DestinedForSale,
                    FirstPostedUtcTicks = d.FirstPostedUtc.Ticks,
                };
            }

            List<DemandEpisode> ended = world.Demand.SnapshotEndedForFaction(owner.Tag, ownedIds, world.UtcNow);
            snapshot.EndedTotal = ended.Count;
            count = Math.Min(ended.Count, FactionsWire.MaxFactionEndedDemandRows);
            snapshot.Ended = new WireNodeEndedDemand[count];
            for (int i = 0; i < count; i++)
            {
                DemandEpisode e = ended[i];
                Name(names, e.NodeId);
                Name(names, e.DestNodeId);
                snapshot.Ended[i] = new WireNodeEndedDemand
                {
                    Kind = e.Kind,
                    AtNodeId = e.NodeId ?? "",
                    DestNodeId = e.DestNodeId ?? "",
                    PeakQuantity = e.PeakQuantity,
                    Class = (byte)e.Class,
                    DestinedForSale = e.DestinedForSale,
                    Consumed = e.Consumed,
                    FirstPostedUtcTicks = e.FirstPostedUtc.Ticks,
                    EndedUtcTicks = e.EndedUtc.Ticks,
                };
            }

            // Every owned station is named even with no rows, so the window's filter lists them all.
            foreach (SpatialNode node in owned) Name(names, node.Id);
            snapshot.Nodes = new WireNamedNode[names.Count];
            int n = 0;
            foreach (KeyValuePair<string, string> pair in names)
                snapshot.Nodes[n++] = new WireNamedNode { Id = pair.Key, Name = pair.Value };
            return snapshot;
        }

        private SpatialNode Node(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;
            SpatialNode node;
            return _runtime.World.Graph.Nodes.TryGetValue(nodeId, out node) ? node : null;
        }

        private void Name(Dictionary<string, string> names, string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || names.ContainsKey(nodeId)) return;
            SpatialNode node = Node(nodeId);
            names[nodeId] = node != null ? node.DisplayName ?? "" : "";
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private FactionState ViewerFaction(long identityId)
        {
            if (identityId == 0L || MySession.Static == null || MySession.Static.Factions == null) return null;
            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (seFaction == null) return null;
            FactionState owner = _runtime.FindFaction(seFaction.Tag);
            return owner != null && owner.IsPlayerFaction ? owner : null;
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
                _sent.Remove(steamId);
            }
        }

        public void Close()
        {
            _streaming.Clear();
            _sent.Clear();
        }
    }
}
