using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the market window. A linked client subscribes when it opens the window and
    /// unsubscribes when it closes it, so an idle client costs nothing.
    ///
    /// <para>The table is built <b>once per pass for the whole world</b> and then filtered per
    /// subscriber. Prices do not depend on who is looking — only on whether they are allowed to
    /// look — and the alternative would recompute every station's listings once per viewer.</para>
    ///
    /// <para>Three gates, all applied in the writer rather than the reader, so nothing a viewer may
    /// not see reaches the wire at all. This is the same rule <c>PlayerMapManager.IsVisibleTo</c>
    /// states for fog of war and <c>WireNodeDetail.Owned</c> states for node detail:</para>
    /// <list type="number">
    ///   <item>the node has a station (a stable station id, not a spawned grid);</item>
    ///   <item>the viewer has discovered it (<see cref="KnowledgeView.IsDiscoveredBy"/>);</item>
    ///   <item>its owner is not at war with them — a hostile faction's stations are omitted
    ///         entirely rather than sent with prices withheld, because the map already says they
    ///         are there and a row you can never act on is noise.</item>
    /// </list>
    /// </summary>
    internal sealed class PlayerMarketManager
    {
        /// <summary>Market sends happen every this-many scanner ticks. At the scanner's ~5 s cadence
        /// that is roughly the one minute the feature was specified at, and prices move on the
        /// economy's cadence rather than a frame's.</summary>
        private const int TicksPerMarketSend = 12;

        /// <summary>How often the frozen memory is written out. Far slower than the send cadence:
        /// the file is a fallback for players who are away, not a live record.</summary>
        private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;
        private readonly PlayerScannerManager _scanner;
        private readonly MarketMemory _memory;
        private readonly Func<SpatialNode, List<StationListing>, int> _buildListings;
        private readonly Func<List<PlayerContact>> _contacts;
        private readonly HashSet<string> _registeredTags;

        private readonly HashSet<ulong> _streaming = new HashSet<ulong>();
        private readonly HashSet<ulong> _owesSend = new HashSet<ulong>();
        private readonly ViewerRelations _relations = new ViewerRelations("market");
        private readonly PlayerMarketWorld _world = new PlayerMarketWorld();
        private readonly List<FactionKnowledge> _viewerSources = new List<FactionKnowledge>(2);
        private readonly List<long> _viewerObservers = new List<long>(2);

        private int _tick;
        private DateTime _lastSaveUtc = DateTime.MinValue;

        public PlayerMarketManager(
            FactionRuntime runtime, UplinkManager uplink, PlayerScannerManager scanner,
            MarketMemory memory, HashSet<string> registeredTags,
            Func<SpatialNode, List<StationListing>, int> buildListings,
            Func<List<PlayerContact>> contacts)
        {
            _runtime = runtime;
            _uplink = uplink;
            _scanner = scanner;
            _memory = memory;
            _registeredTags = registeredTags;
            _buildListings = buildListings;
            _contacts = contacts;
            if (_uplink != null) _uplink.RegisterHandler(WireMessageKind.MarketRequest, OnMarketRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnMarketRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadMarketRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Market request from " + steamId + " was unreadable.");
                return;
            }

            if (stream)
            {
                _streaming.Add(steamId);
                _owesSend.Add(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("MARKET_SUBSCRIBE steam=" + steamId);
            }
            else
            {
                _streaming.Remove(steamId);
                _owesSend.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("MARKET_UNSUBSCRIBE steam=" + steamId);
            }
        }

        public void PruneOffline(HashSet<ulong> online)
        {
            if (online == null) return;
            _streaming.RemoveWhere(id => !online.Contains(id));
            _owesSend.RemoveWhere(id => !online.Contains(id));
        }

        /// <summary>Runs on the scanner cadence and does nothing at all while nobody has the window
        /// open, which is the normal case. The memory save is the one exception — it still has to
        /// land after the last subscriber leaves.</summary>
        public void Tick()
        {
            SaveMemoryIfDue();
            if (_streaming.Count == 0) return;
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;
            bool due = (_tick++ % TicksPerMarketSend) == 0;
            if (!due && _owesSend.Count == 0) return;

            WorldModel world = _runtime.World;
            _world.Rebuild(world, _buildListings, _registeredTags);

            List<PlayerContact> contacts = _contacts != null ? _contacts() : null;
            DateTime nowUtc = world.UtcNow;
            TimeSpan horizon = world.Tuning.DiscoveryHorizon;
            double birthright = world.Tuning.BirthrightRangeKm;

            foreach (LinkedViewer player in LinkedViewers.Collect())
            {
                ulong steamId = player.SteamId;
                if (!_streaming.Contains(steamId)) continue;
                // A fresh subscriber is owed a send immediately rather than waiting out the
                // minute; everyone else moves on the cadence.
                bool owed = _owesSend.Remove(steamId);
                if (!due && !owed) continue;

                long identityId = player.IdentityId;
                _relations.Clear();
                SendMarket(steamId, identityId, world, contacts, nowUtc, horizon, birthright);
            }
        }

        private void SendMarket(
            ulong steamId, long identityId, WorldModel world, List<PlayerContact> contacts,
            DateTime nowUtc, TimeSpan horizon, double birthright)
        {
            BuildViewerSources(identityId);
            string viewerTag = ResolveViewerFactionTag(identityId);

            PlayerContact self = default(PlayerContact);
            bool haveSelf = false;
            if (contacts != null)
                for (int i = 0; i < contacts.Count; i++)
                    if (contacts[i].IdentityId == identityId) { self = contacts[i]; haveSelf = true; break; }

            // Contact is asked once per faction, not once per station: it is a property of the
            // relationship, and a faction can hold dozens of nodes.
            var inContact = new Dictionary<string, bool>(StringComparer.Ordinal);
            var liveByFaction = new Dictionary<string, Dictionary<string, List<FrozenMarketRow>>>(StringComparer.Ordinal);

            var items = new List<string>();
            var itemIndex = new Dictionary<string, ushort>(StringComparer.Ordinal);
            var stations = new List<WireMarketStation>();
            var rows = new List<WireMarketRow>();

            foreach (KeyValuePair<string, List<StationListing>> entry in _world.Listings)
            {
                string nodeId = entry.Key;
                SpatialNode node;
                if (!world.Graph.Nodes.TryGetValue(nodeId, out node) || node == null) continue;

                string ownerTag = _world.Owners[nodeId];
                WireRelation relation = _relations.For(ownerTag, identityId);
                if (relation == WireRelation.Enemy) continue;

                if (!KnowledgeView.IsDiscoveredBy(
                        world.Graph, viewerTag, _viewerSources, nodeId, nowUtc, horizon, birthright))
                    continue;

                bool live;
                if (!inContact.TryGetValue(ownerTag, out live))
                {
                    live = relation == WireRelation.Own
                        || (haveSelf && PlayerMarketWorld.FactionDetects(
                                world, world.Tuning, ownerTag, self.PositionKm, self.Signature));
                    inContact[ownerTag] = live;
                }

                List<FrozenMarketRow> rowSource;
                DateTime asOf;
                if (live)
                {
                    rowSource = PlayerMarketWorld.Freeze(entry.Value);
                    asOf = nowUtc;

                    Dictionary<string, List<FrozenMarketRow>> perNode;
                    if (!liveByFaction.TryGetValue(ownerTag, out perNode))
                    {
                        perNode = new Dictionary<string, List<FrozenMarketRow>>(StringComparer.Ordinal);
                        liveByFaction[ownerTag] = perNode;
                    }
                    perNode[nodeId] = rowSource;
                }
                else
                {
                    // Per-player memory only. A faction-shared market memory would need its own
                    // key space and is not built; see FAC-149.
                    FrozenFactionMarket remembered =
                        _memory.Freshest(_viewerObservers, ownerTag, nowUtc, horizon);
                    if (remembered == null) continue;   // never seen it trade; nothing to show
                    if (!remembered.Stations.TryGetValue(nodeId, out rowSource) || rowSource == null) continue;
                    asOf = remembered.AsOfUtc;
                }

                ushort stationIdx = (ushort)stations.Count;
                stations.Add(new WireMarketStation
                {
                    NodeId = nodeId,
                    DisplayName = node.DisplayName ?? "",
                    OwnerTag = ownerTag,
                    Relation = (byte)relation,
                    XKm = node.Position.X,
                    YKm = node.Position.Y,
                    ZKm = node.Position.Z,
                    Live = live,
                    AsOfUtcTicks = asOf.Ticks,
                });

                List<StationListing> liveRows = live ? entry.Value : null;
                for (int i = 0; i < rowSource.Count; i++)
                {
                    FrozenMarketRow r = rowSource[i];
                    ushort idx;
                    if (!itemIndex.TryGetValue(r.InvKey, out idx))
                    {
                        idx = (ushort)items.Count;
                        items.Add(r.InvKey);
                        itemIndex[r.InvKey] = idx;
                    }

                    rows.Add(new WireMarketRow
                    {
                        StationIndex = stationIdx,
                        ItemIndex = idx,
                        Side = r.Side,
                        UnitPrice = r.UnitPrice,
                        // -1, not 0: a remembered station carries what a faction wanted, never how
                        // much. Zero would state the opposite of "unknown".
                        Quantity = liveRows != null ? (int)Math.Min(liveRows[i].Quantity, int.MaxValue) : -1,
                    });
                }
            }

            // Only what this viewer could actually see is remembered, so the memory can never grant
            // knowledge the fog-of-war gate refused.
            foreach (KeyValuePair<string, Dictionary<string, List<FrozenMarketRow>>> f in liveByFaction)
                _memory.Remember(identityId, f.Key, nowUtc, f.Value);

            _uplink.Send(steamId, FactionsWire.WriteMarketSnapshot(items, stations, rows));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("MARKET steam=" + steamId + " stations=" + stations.Count
                                     + " rows=" + rows.Count + " items=" + items.Count
                                     + " frozen=" + CountFrozen(stations));
        }

        private static int CountFrozen(List<WireMarketStation> stations)
        {
            int n = 0;
            for (int i = 0; i < stations.Count; i++) if (!stations[i].Live) n++;
            return n;
        }

        private void BuildViewerSources(long identityId)
        {
            ViewerKnowledge.BuildSources(_runtime, _scanner, identityId, _viewerSources);
            _viewerObservers.Clear();
            _viewerObservers.Add(identityId);
        }

        private string ResolveViewerFactionTag(long identityId)
        {
            return ViewerKnowledge.FactionTag(identityId);
        }

        private void SaveMemoryIfDue()
        {
            if (_memory == null) return;
            DateTime now = DateTime.UtcNow;
            if (now - _lastSaveUtc < SaveInterval) return;
            _lastSaveUtc = now;
            try { _memory.SaveIfDirty(); }
            catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Market memory save failed."); }
        }

        /// <summary>Flush on shutdown, so a clean stop does not lose the window's memory.</summary>
        public void Flush()
        {
            if (_memory == null) return;
            try { _memory.SaveIfDirty(); }
            catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Market memory flush failed."); }
        }
    }
}
