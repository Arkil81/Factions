using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the standing-order list: the viewing player's own faction's recurring intent
    /// (<see cref="FactionState.StandingOrders"/>, Mine and Stock orders), and the four actions that
    /// operate on a row it is already showing. A linked client subscribes when the window opens and unsubscribes
    /// when it closes, so a player who never opens it costs nothing.
    ///
    /// <para>The client surface is the station window's <c>Orders...</c> button, which shows the Mine
    /// and Stock orders anchored at the node it was opened from. The list itself is still faction-wide and this manager still sends all of it:
    /// filtering by hub is a property of the window, not of the intent, and a second subscription
    /// per station would cost a snapshot each.</para>
    ///
    /// <para>Sends on the list's own revision rather than a fixed cadence, the way
    /// <see cref="PlayerEventsManager"/> sends on the event ring's: a standing-order list changes
    /// only when a player changes it. The scan cursor is deliberately excluded from that revision —
    /// the countdown beside a Mine order is derived client-side from
    /// <see cref="WireStandingOrder.NextScanUtcTicks"/>, so a cursor rewritten every scan does not
    /// re-send the whole list to every open window.</para>
    ///
    /// <para>Only the player's OWN faction is ever described. There is no viewer/owner seam to get
    /// wrong here the way <see cref="PlayerNodeDetailManager"/> has one: a standing order belongs to
    /// exactly one faction and nobody else may see or touch it.</para>
    /// </summary>
    internal sealed class PlayerStandingOrdersManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs with the window open.</summary>
        private readonly HashSet<ulong> _streaming = new HashSet<ulong>();

        /// <summary>Last list revision sent to each subscriber. A subscriber with no entry here is
        /// owed a snapshot immediately, which is how a fresh subscribe is served without waiting for
        /// the player's next edit.</summary>
        private readonly Dictionary<ulong, int> _sentRevision = new Dictionary<ulong, int>();

        public PlayerStandingOrdersManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.StandingOrdersRequest, OnOrdersRequest);
            _uplink.RegisterHandler(WireMessageKind.StandingOrderCommandRequest, OnCommandRequest);
            _uplink.RegisterHandler(WireMessageKind.StandingOrderCreateRequest, OnCreateRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnOrdersRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadStandingOrdersRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Standing-orders request from " + steamId + " was unreadable.");
                return;
            }

            if (stream)
            {
                _streaming.Add(steamId);
                _sentRevision.Remove(steamId);   // owed a snapshot on the next tick
                // Once per subscribe, not per snapshot: the pick-lists are the largest payload of
                // this feature and the one part that does not move when a player edits their list.
                SendOptions(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("ORDERS_SUBSCRIBE steam=" + steamId);
            }
            else
            {
                _streaming.Remove(steamId);
                _sentRevision.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("ORDERS_UNSUBSCRIBE steam=" + steamId);
            }
        }

        /// <summary>Runs on the scanner cadence, and does nothing at all while no one has the window
        /// open — the normal case.</summary>
        public void Tick()
        {
            if (_streaming.Count == 0) return;
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;

            foreach (LinkedViewer player in LinkedViewers.Collect())
            {
                ulong steamId = player.SteamId;
                if (!_streaming.Contains(steamId)) continue;

                FactionState owner = ViewerFaction(player.IdentityId);

                // A player in no faction, or in one Factions has not registered as a player faction,
                // has no list. Revision 0 is a real answer: sending it once tells the window to say
                // so rather than waiting forever on a first snapshot.
                int revision = owner != null ? unchecked(owner.StandingOrdersRevision * 397 + StockHaveSignature(owner)) : 0;

                int sent;
                if (_sentRevision.TryGetValue(steamId, out sent) && sent == revision) continue;
                _sentRevision[steamId] = revision;

                SendSnapshot(steamId, owner);
            }
        }

        private void SendSnapshot(ulong steamId, FactionState owner)
        {
            var wire = new List<WireStandingOrder>();
            if (owner != null)
            {
                // Rank order, so the window lists them in the order the managers evaluate them.
                foreach (StandingOrder order in owner.StandingOrdersByRank())
                    wire.Add(Describe(owner, order));
            }

            int cap = _runtime.Tuning != null ? _runtime.Tuning.StandingOrderCap : 0;
            _uplink.Send(steamId, FactionsWire.WriteStandingOrdersSnapshot(
                owner != null ? owner.Tag : "", cap, wire));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("ORDERS_SNAPSHOT steam=" + steamId + " orders=" + wire.Count);
        }

        private WireStandingOrder Describe(FactionState owner, StandingOrder order)
        {
            var w = new WireStandingOrder
            {
                Id = order.Id ?? "",
                Kind = (byte)order.Kind,
                Priority = order.Priority,
                Paused = order.Paused,
            };

            if (order.Kind == StandingOrderKind.Stock && order.AllStations)
            {
                w.HubNodeId = "";
                w.HubNodeName = "";
                w.ItemKey = order.ItemKey ?? "";
                w.ItemName = ItemName(order.ItemKey);
                w.TargetQuantity = order.TargetQuantity;
                w.AllStations = true;
                long target;
                List<SpatialNode> applied = AppliedStations(owner, order, out target);
                w.StationTarget = target;
                w.StationIds = new string[applied.Count];
                w.StationHaves = new long[applied.Count];
                for (int i = 0; i < applied.Count; i++)
                {
                    long have;
                    applied[i].Inventory.TryGetValue(order.ItemKey, out have);
                    w.StationIds[i] = applied[i].Id;
                    w.StationHaves[i] = have;
                }
                return w;
            }

            if (order.Kind == StandingOrderKind.Stock)
            {
                // Anchored at a node like a Mine order, so the window filters both kinds the same way.
                w.HubNodeId = order.NodeId ?? "";
                w.HubNodeName = NodeDisplayName(order.NodeId);
                w.ItemKey = order.ItemKey ?? "";
                w.ItemName = ItemName(order.ItemKey);
                w.TargetQuantity = order.TargetQuantity;
                w.Have = StockHave(order);
                return w;
            }

            w.HubNodeId = order.HubNodeId ?? "";
            w.HubNodeName = NodeDisplayName(order.HubNodeId);
            w.PeriodSeconds = order.PeriodSeconds;
            w.OreWeights = DescribeOreWeights(order.OreWeights);
            w.NextScanUtcTicks = order.NextScanUtc.Ticks;
            return w;
        }

        /// <summary>"Iron:50, Nickel:50", or empty for each catchment node's own natural mix. A
        /// display string: the window shows it and never computes with it.</summary>
        private static string DescribeOreWeights(Dictionary<string, double> weights)
        {
            if (weights == null || weights.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var kv in weights)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append(':').Append(kv.Value.ToString("0", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>What a Stock order's node holds of its item now.</summary>
        private long StockHave(StandingOrder order)
        {
            if (_runtime.World == null || string.IsNullOrEmpty(order.NodeId) || string.IsNullOrEmpty(order.ItemKey)) return 0;
            SpatialNode node;
            if (!_runtime.World.Graph.Nodes.TryGetValue(order.NodeId, out node) || node == null) return 0;
            long have;
            node.Inventory.TryGetValue(order.ItemKey, out have);
            return have;
        }

        /// <summary>Where an all-stations order sets the target, and that target; empty before the
        /// world is up.</summary>
        private List<SpatialNode> AppliedStations(FactionState owner, StandingOrder order, out long target)
        {
            if (_runtime.World == null) { target = 0; return new List<SpatialNode>(); }
            return StockOrders.AppliedStations(owner, _runtime.World.Graph, order, out target);
        }

        /// <summary>Folded into the send revision so a Stock order's <c>Have</c> reaches an open
        /// window when stock moves, not only when the list is edited. An all-stations order folds in
        /// each station it applies at, and the station set itself, which ownership changes.</summary>
        private int StockHaveSignature(FactionState owner)
        {
            int signature = 0;
            foreach (StandingOrder order in owner.StandingOrders)
            {
                if (order.Kind != StandingOrderKind.Stock) continue;
                if (!order.AllStations)
                {
                    unchecked { signature = signature * 31 + StockHave(order).GetHashCode(); }
                    continue;
                }
                long target;
                foreach (SpatialNode station in AppliedStations(owner, order, out target))
                {
                    long have;
                    station.Inventory.TryGetValue(order.ItemKey, out have);
                    unchecked
                    {
                        signature = signature * 31 + StringComparer.Ordinal.GetHashCode(station.Id);
                        signature = signature * 31 + have.GetHashCode();
                    }
                }
                unchecked { signature = signature * 31 + target.GetHashCode(); }
            }
            return signature;
        }

        /// <summary>The item's display name from the orderable-item list, cached per list build;
        /// the key itself when it is not on the list.</summary>
        private string ItemName(string itemKey)
        {
            if (string.IsNullOrEmpty(itemKey)) return "";
            if (_itemNames == null && _runtime.ListOrderableItems != null)
            {
                _itemNames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (FactionRuntime.OrderableItem item in _runtime.ListOrderableItems())
                    if (!string.IsNullOrEmpty(item.Key)) _itemNames[item.Key] = item.Name ?? "";
            }
            string name;
            return _itemNames != null && _itemNames.TryGetValue(itemKey, out name) && !string.IsNullOrEmpty(name)
                ? name : itemKey;
        }

        private Dictionary<string, string> _itemNames;

        /// <summary>Empty for a node that no longer exists or was never named — the id still goes
        /// out, so a lost hub reads as an id rather than as nowhere.</summary>
        private string NodeDisplayName(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || _runtime.World == null) return "";
            SpatialNode node;
            if (!_runtime.World.Graph.Nodes.TryGetValue(nodeId, out node) || node == null) return "";
            return node.DisplayName ?? "";
        }

        /// <summary>Sends the create dialog's pick-lists: which owned nodes are eligible for what,
        /// and which prefabs each build role accepts. Eligibility is resolved here rather than shipped
        /// as raw data for the client to filter — the client has no graph, no catalogue and no
        /// recipes, and a dialog that offered an option the server would refuse is worse than one
        /// that offers fewer.</summary>
        private void SendOptions(ulong steamId)
        {
            if (_runtime == null || _runtime.World == null) return;

            long identityId = IdentityFor(steamId);
            FactionState owner = identityId != 0L ? ViewerFaction(identityId) : null;

            var nodes = new List<WireOrderOption>();
            if (owner != null)
            {
                foreach (var entry in _runtime.StandingOrderNodeOptions(owner))
                {
                    nodes.Add(new WireOrderOption
                    {
                        Id = entry.Key.Id,
                        Label = entry.Key.DisplayName ?? "",
                        Flags = entry.Value,
                    });
                }
            }

            // Nodes only: the hull pick-list this message carried went with the Build goal, and the
            // Fleet window sends its own (with the numbers a hull is chosen on) rather than a flat
            // list of ids.
            _uplink.Send(steamId, FactionsWire.WriteStandingOrderOptions(nodes));

            List<WireStockItem> items = StockItems(owner);
            _uplink.Send(steamId, FactionsWire.WriteStockItemOptions(items));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("ORDERS_OPTIONS steam=" + steamId + " nodes=" + nodes.Count + " items=" + items.Count);
        }

        /// <summary>Every orderable item with where it could come from for <paramref name="owner"/>.
        /// Every item is offered, sourced or not: an order for an item nobody supplies is how the
        /// want becomes visible to anyone who could.</summary>
        private List<WireStockItem> StockItems(FactionState owner)
        {
            var items = new List<WireStockItem>();
            if (owner == null || _runtime.ListOrderableItems == null) return items;

            Dictionary<string, StockItemSource> sources = _runtime.StockItemSourcesFor(owner);
            foreach (FactionRuntime.OrderableItem item in _runtime.ListOrderableItems())
            {
                StockItemSource source;
                sources.TryGetValue(item.Key, out source);
                int flags = 0;
                if (source != null)
                {
                    if (source.OwnProduction) flags |= (int)WireStockItemFlags.OwnProduction;
                    if (source.OwnStock > 0) flags |= (int)WireStockItemFlags.OwnStock;
                    if (source.OtherStock.Count > 0) flags |= (int)WireStockItemFlags.OtherStock;
                }
                items.Add(new WireStockItem
                {
                    Key = item.Key,
                    Name = item.Name ?? item.Key,
                    Category = item.Category ?? "Other",
                    Source = StockItemSources.Describe(source),
                    Flags = flags,
                });
            }
            items.Sort((a, b) =>
            {
                int cmp = string.CompareOrdinal(a.Category, b.Category);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return items;
        }

        // ---- Commands -------------------------------------------------------------------------

        private void OnCommandRequest(ulong steamId, byte[] data)
        {
            string orderId;
            WireStandingOrderCommand command;
            int value;
            if (!FactionsWire.TryReadStandingOrderCommandRequest(data, out orderId, out command, out value))
            {
                FactionsPlugin.Log.Warn("Standing-order command from " + steamId + " was unreadable.");
                return;
            }

            string message = Run(steamId, orderId, command, value);
            // The runtime's player-facing entry points answer in prose whether they succeeded or
            // refused, so "ok" here means the command was dispatched at all, not that it changed
            // anything. The snapshot that follows is the authoritative answer either way.
            _uplink.Send(steamId, FactionsWire.WriteStandingOrderCommandResult(
                message != null, message ?? "No such command."));

            // Every mutator bumps StandingOrdersRevision, so the next Tick sends the new list
            // without any owed-snapshot bookkeeping of its own.
            _sentRevision.Remove(steamId);
            Tick();

            // Behind the same 'factions debug uplink' gate as every other line here. The mutation is
            // already recorded in the Factions day log by the code that performs it
            // (STANDING_ORDER_*_BY_PLAYER from FactionRuntime), so what this adds is the SteamID
            // behind the click — diagnostic, not the record. The chat verb has the same gap.
            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("ORDER_COMMAND steam=" + steamId + " order=" + orderId
                                     + " command=" + command + " value=" + value);
            }
        }

        /// <summary>
        /// Runs one command against the caller's own faction. Every path re-resolves the faction
        /// from the SteamID and hands the order id to a <see cref="FactionRuntime"/> entry point
        /// that resolves it within that faction, so a client naming an order belonging to someone
        /// else is refused by the same code that refuses the chat verb.
        /// </summary>
        private string Run(ulong steamId, string orderId, WireStandingOrderCommand command, int value)
        {
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";
            if (string.IsNullOrEmpty(orderId)) return "No standing order selected.";

            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";

            FactionState owner = ViewerFaction(identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) return refusal;

            switch (command)
            {
                case WireStandingOrderCommand.Remove:
                    return _runtime.RemoveStandingOrderForPlayer(owner, orderId);
                case WireStandingOrderCommand.Priority:
                    return _runtime.SetStandingOrderPriorityForPlayer(owner, orderId, value);
                case WireStandingOrderCommand.Pause:
                    return _runtime.SetStandingOrderPausedForPlayer(owner, orderId, true);
                case WireStandingOrderCommand.Resume:
                    return _runtime.SetStandingOrderPausedForPlayer(owner, orderId, false);
                default:
                    return null;
            }
        }

        private void OnCreateRequest(ulong steamId, byte[] data)
        {
            byte kind;
            string nodeId, oreWeights, itemKey;
            double periodSeconds;
            long quantity;
            if (!FactionsWire.TryReadStandingOrderCreateRequest(data, out kind, out nodeId, out periodSeconds,
                    out oreWeights, out itemKey, out quantity))
            {
                FactionsPlugin.Log.Warn("Standing-order create from " + steamId + " was unreadable.");
                return;
            }

            string message = RunCreate(steamId, kind, nodeId, periodSeconds, oreWeights, itemKey, quantity);
            _uplink.Send(steamId, FactionsWire.WriteStandingOrderCommandResult(
                message != null, message ?? "No such command."));

            _sentRevision.Remove(steamId);
            Tick();

            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("ORDER_CREATE steam=" + steamId + " kind=" + kind
                                     + " node=" + nodeId + " period=" + ((int)periodSeconds)
                                     + " item=" + itemKey + " qty=" + quantity);
            }
        }

        /// <summary>Creates one order through the same <see cref="FactionRuntime"/> entry point the
        /// chat verb calls, so every validation the verb applies — ownership, hub reachability, the
        /// list cap — applies identically here. Nothing the client sent is trusted; the pick-list is
        /// a convenience, not an authorisation.</summary>
        private string RunCreate(ulong steamId, byte kind, string nodeId, double periodSeconds,
            string oreWeights, string itemKey, long quantity)
        {
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";

            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";

            FactionState owner = ViewerFaction(identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) return refusal;

            if (kind == (byte)StandingOrderKind.Mine)
                return _runtime.AddStandingMineOrderForPlayer(owner, nodeId, periodSeconds, oreWeights);
            if (kind == (byte)StandingOrderKind.Stock)
                return _runtime.AddStandingStockOrderForPlayer(owner, nodeId, itemKey, quantity);

            // A kind this protocol version does not have, which the handshake should already have refused.
            return "Unknown standing-order kind " + kind + ".";
        }

        /// <summary>The Factions faction behind a player identity, or null when they are in no SE
        /// faction or in one that is not a registered player faction. Mirrors
        /// <c>PlayerCommandHelpers.TryResolveCallerFaction</c>, including its player-faction
        /// requirement — an NPC tag must not be reachable from a client.</summary>
        private FactionState ViewerFaction(long identityId)
        {
            if (identityId == 0L || MySession.Static == null || MySession.Static.Factions == null) return null;
            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (seFaction == null) return null;
            FactionState owner = _runtime.FindFaction(seFaction.Tag);
            return owner != null && owner.IsPlayerFaction ? owner : null;
        }

        private static long IdentityFor(ulong steamId)
        {
            if (MySession.Static == null) return 0L;
            return LinkedViewers.IdentityFor(steamId);
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
