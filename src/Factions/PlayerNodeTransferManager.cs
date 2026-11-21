using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Server side of the hand on a node's own stock: what a player standing at a station their
    /// faction holds may take out of <see cref="SpatialNode.Inventory"/>, and what they may put back
    /// in. Two request/response pairs, both behind <see cref="WireFeatures.NodeInventory"/>.
    ///
    /// <para><b>Nothing here is a sale.</b> No wallet is read, no treasury is touched, and no ledger
    /// entry is written. The stock is already the faction's; carrying it between the abstract node
    /// and a physical inventory changes how it is represented, not who owns it. That is the whole
    /// distinction from <see cref="StationStoreManager"/>, whose transaction seam settles a real
    /// purchase at market price.</para>
    ///
    /// <para><b>Every claim the client makes is re-derived here</b>, the way
    /// <see cref="PlayerNodeDetailManager"/> re-derives the faction behind a node command: the sender's
    /// SteamID resolves to an identity, the identity to an SE faction, and only that tag is compared
    /// against the node's owner. The client is likewise not believed about where it is standing or
    /// what it owns — the player's own character position and the target grid's owners are read from
    /// the session.</para>
    ///
    /// <para>Runs entirely on the game thread, inside the uplink message handler: one request moves
    /// goods between exactly one node and exactly one physical inventory, start to finish, so there
    /// is no window in which items exist on neither side.</para>
    ///
    /// <para><b>One request is one leg, not one transfer.</b> A hold spread over several containers
    /// is filled by a run of requests against the same node in quick succession, each naming the next
    /// target and whatever is still outstanding. Two consequences shape the code below: the routable
    /// amount is re-read from the node on every request rather than carried across the run, since the
    /// leg before it has already changed it; and a target that simply cannot take anything — a full
    /// container, a hold with none of the asked-for item — answers <c>ok</c> with a
    /// <c>moved</c> of zero rather than refusing, because a refusal is the run's signal to stop and a
    /// full first container must not strand goods the second one would take.</para>
    /// </summary>
    internal sealed class PlayerNodeTransferManager
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>Resolves an inventory key to the SE definition the item is created from. A
        /// delegate rather than a second definition scan: <see cref="StationStoreManager"/> already
        /// builds exactly this map for its listings, and two scans could disagree about which key
        /// names which subtype.</summary>
        internal delegate bool DefinitionIdResolver(string invKey, out MyDefinitionId defId);

        /// <summary>How far from the node a player, and the grid they are filling, may stand. The
        /// same 5 km the other player-faction managers use for "at the node".</summary>
        private const double SearchRadiusKm = 5.0;

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;
        private readonly DefinitionIdResolver _resolveDefinition;

        /// <summary>Authority on a cluster: queues a stock move for the agent a player is connected
        /// to. The block and the goods are there; the node's stock is here.</summary>
        internal Action<byte, NexusStockMove> QueueRemoteMove { get; set; }

        public PlayerNodeTransferManager(FactionRuntime runtime, UplinkManager uplink,
                                         DefinitionIdResolver resolveDefinition)
        {
            _runtime = runtime;
            _uplink = uplink;
            _resolveDefinition = resolveDefinition;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.NodeStockRequest, OnStockRequest);
            _uplink.RegisterHandler(WireMessageKind.NodeTransferRequest, OnTransferRequest);
        }

        // ---- Listing ---------------------------------------------------------------------------

        private void OnStockRequest(ulong steamId, byte[] data)
        {
            long stationId;
            string nodeId;
            if (!FactionsWire.TryReadNodeStockRequest(data, out stationId, out nodeId))
            {
                Log.Warn("Node stock request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            SpatialNode node;
            string refusal;
            bool allowed = TryAuthorise(steamId, stationId, nodeId, out node, out refusal);

            // A node resolved but refused still names itself: the surface asking is a store block that
            // knows only its station id, and a refusal with no node behind it cannot say which
            // station it is talking about.
            string id = node != null ? node.Id : (nodeId ?? "");
            string label = node != null ? node.DisplayName : "";
            _uplink.Send(steamId, FactionsWire.WriteNodeStockSnapshot(
                id, label, allowed, allowed ? "" : refusal, allowed ? Rows(node) : null));

            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("NODE_STOCK steam=" + steamId + " station=" + stationId
                                     + " node=" + id + " may=" + allowed);
            }
        }

        /// <summary>Every key the node holds under this hand, reserves already deducted from
        /// <see cref="WireNodeTransferRow.Available"/> so a client can never offer an amount the
        /// transfer would then refuse.
        ///
        /// <para>Rows whose stock is entirely reserved are kept. Stock a build has claimed is exactly
        /// what a player wondering where their ore went is looking for, and dropping the row would
        /// read as the station never having had it. A row with nothing on either side is dropped —
        /// that is a key the node does not hold.</para></summary>
        private WireNodeTransferRow[] Rows(SpatialNode node)
        {
            DateTime now = _runtime.World != null ? _runtime.World.UtcNow : DateTime.UtcNow;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> kv in node.Inventory)
                if (kv.Value > 0) keys.Add(kv.Key);
            foreach (KeyValuePair<string, long> kv in node.ReservedInventory)
                if (kv.Value > 0) keys.Add(kv.Key);
            if (keys.Count == 0) return null;

            var rows = new List<WireNodeTransferRow>(keys.Count);
            foreach (string key in keys)
            {
                // Gas is listed by the node window and deliberately not by this one: it has no
                // MyDefinitionId to materialise from, so an offered row could only ever be refused.
                if (InventoryKeyMapper.IsGasKey(key)) continue;

                long available = NodeTransfer.Withdrawable(node, key, now);
                long reserved = NodeTransfer.Reserved(node, key, now);
                if (available <= 0 && reserved <= 0) continue;

                rows.Add(new WireNodeTransferRow
                {
                    Key = key,
                    Label = DisplayName(key),
                    Available = available,
                    Reserved = reserved,
                });
            }
            if (rows.Count == 0) return null;

            rows.Sort((a, b) =>
            {
                int byStock = (b.Available + b.Reserved).CompareTo(a.Available + a.Reserved);
                return byStock != 0 ? byStock : string.CompareOrdinal(a.Key, b.Key);
            });
            return rows.ToArray();
        }

        /// <summary>The item's own name, or "" when the key names nothing this server can create —
        /// the wire's marker for "show the key itself".</summary>
        private string DisplayName(string invKey)
        {
            MyDefinitionId defId;
            if (_resolveDefinition == null || !_resolveDefinition(invKey, out defId)) return "";
            MyPhysicalItemDefinition def;
            if (MyDefinitionManager.Static == null
             || !MyDefinitionManager.Static.TryGetPhysicalItemDefinition(defId, out def)
             || def == null) return "";
            return def.DisplayNameText ?? "";
        }

        // ---- Transfer --------------------------------------------------------------------------

        private void OnTransferRequest(ulong steamId, byte[] data)
        {
            string nodeId, invKey;
            bool deposit;
            long amount, targetEntityId;
            if (!FactionsWire.TryReadNodeTransferRequest(
                    data, out nodeId, out deposit, out invKey, out amount, out targetEntityId))
            {
                Log.Warn("Node transfer request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            SpatialNode node;
            string refusal;
            if (!TryAuthorise(steamId, 0L, nodeId, out node, out refusal))
            {
                Refuse(steamId, node, deposit, invKey, amount, refusal);
                return;
            }

            if (amount <= 0) { Refuse(steamId, node, deposit, invKey, amount, "Choose an amount to move."); return; }
            if (string.IsNullOrEmpty(invKey)) { Refuse(steamId, node, deposit, invKey, amount, "Choose an item to move."); return; }

            // Gas has no item and no blueprint — a hydrogen bottle is an assembler product, not the
            // gas — so there is nothing to hand a player in either direction. Refused rather than
            // dropped: a row that silently does nothing is worse than one that says why.
            if (InventoryKeyMapper.IsGasKey(invKey))
            {
                Refuse(steamId, node, deposit, invKey, amount,
                       "Gas cannot be carried as cargo; only what a station holds as items can be moved.");
                return;
            }
            if (deposit && !NodeTransfer.IsDepositable(invKey))
            {
                Refuse(steamId, node, deposit, invKey, amount,
                       "Only ore, ingots and components can be added to a station's stock.");
                return;
            }

            MyDefinitionId defId;
            if (_resolveDefinition == null || !_resolveDefinition(invKey, out defId))
            {
                Refuse(steamId, node, deposit, invKey, amount, "This server has no item definition for " + invKey + ".");
                return;
            }

            if (_uplink.IsRemote(steamId))
            {
                QueueRemote(steamId, node, deposit, invKey, amount, targetEntityId);
                return;
            }

            MyEntity target;
            if (!TryResolveTarget(targetEntityId, node, out target, out refusal))
            {
                Refuse(steamId, node, deposit, invKey, amount, refusal);
                return;
            }

            long moved;
            string message;
            bool ok = deposit
                ? Deposit(node, target, invKey, defId, amount, out moved, out message)
                : Withdraw(node, target, invKey, defId, amount, out moved, out message);

            _uplink.Send(steamId, FactionsWire.WriteNodeTransferResult(ok, moved, message));
            Trace(node, deposit, invKey, amount, moved, ok);
        }

        /// <summary>Node -> physical inventory. The item is created here and the node is decremented
        /// by <b>what actually landed</b>, never by what was asked for: a hold that fills up part way
        /// leaves the remainder standing in the station rather than losing it.
        ///
        /// <para>An ask larger than the routable remainder is refused outright rather than clamped.
        /// The listing already carries the routable number, so an ask past it means the client is
        /// acting on a stale snapshot, and quietly moving less than a player asked for reads as the
        /// station having been emptied by someone else. The remainder is re-read here on every call,
        /// so a run of legs against one node always prices itself against what the leg before it
        /// left.</para></summary>
        private bool Withdraw(SpatialNode node, MyEntity target, string invKey, MyDefinitionId defId,
                              long amount, out long moved, out string message)
        {
            moved = 0;
            DateTime now = _runtime.World != null ? _runtime.World.UtcNow : DateTime.UtcNow;
            long routable = NodeTransfer.Withdrawable(node, invKey, now);
            message = WithdrawRefusal(node, invKey, amount, routable, now);
            if (message != null) return false;

            MyInventory inv = target.GetInventory(0);
            if (inv == null) { message = "That target has no inventory."; return false; }

            // Whole units only. Node stock is an integer count, and SE will happily place a
            // fractional amount of ore — anything past the whole unit would be created here and
            // never accounted for on the node side.
            MyFixedPoint fits = inv.ComputeAmountThatFits(defId);
            long room = (long)fits;
            long toMove = amount < room ? amount : room;
            // A leg's own target being full is not the run's failure: the next one may have room, and
            // the stock is still standing in the node either way.
            if (toMove <= 0) { message = "That inventory is full."; return true; }

            var builder = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(defId);
            if (!inv.AddItems((MyFixedPoint)(double)toMove, builder))
            {
                message = "That inventory would not take the items.";
                return true;
            }

            // Same synchronous block as the AddItems above, so the reserve cannot have moved between
            // the two; TakeFromNode re-applies the clamp regardless.
            moved = NodeTransfer.TakeFromNode(node, invKey, toMove, now);
            message = moved < amount
                ? "Moved " + moved + " of " + amount + "; the inventory filled up."
                : "Moved " + moved + " " + invKey + ".";
            return true;
        }

        /// <summary>Physical inventory -> node. Removal happens first and the node is credited with
        /// <b>what was actually removed</b>, so a deposit can never mint node stock out of items that
        /// are still sitting in the hold.
        ///
        /// <para>Drawn across every inventory the target block has, not index 0 alone — ore sits in a
        /// drill as readily as in a container, and this is the direction where that matters. Whole
        /// units only, for the same reason the withdraw side moves whole units.</para></summary>
        private bool Deposit(SpatialNode node, MyEntity target, string invKey, MyDefinitionId defId,
                             long amount, out long moved, out string message)
        {
            moved = RemoveFrom(target, defId, amount);

            // As on the withdraw side: a target holding none of it is this leg's answer, not the
            // run's, and the next container may be the one carrying the goods.
            if (moved <= 0)
            {
                message = "There is no " + invKey + " in that inventory.";
                return true;
            }

            NodeTransfer.CreditNode(node, invKey, moved);
            message = moved < amount
                ? "Stored " + moved + " of " + amount + "; that was all there was."
                : "Stored " + moved + " " + invKey + " at " + node.DisplayName + ".";
            return true;
        }

        // ---- A player on a Nexus agent ---------------------------------------------------------

        /// <summary>Authority: the request of a player connected to an agent, whose block is on that
        /// agent. A withdrawal is taken off the node here and now, so two requests in flight can
        /// never take the same units; the agent places what fits and reports the rest back as a
        /// <see cref="NexusStoreEventKind.Stock"/> credit. A deposit is credited only when the agent
        /// reports what it removed. The agent answers the client itself, once it has moved the goods.</summary>
        private void QueueRemote(ulong steamId, SpatialNode node, bool deposit, string invKey, long amount, long targetEntityId)
        {
            Vector3D standing;
            byte home;
            Action<byte, NexusStockMove> queue = QueueRemoteMove;
            if (queue == null || !LinkedViewers.TryRemotePosition(steamId, out standing, out home) || home == 0)
            {
                Refuse(steamId, node, deposit, invKey, amount, "Could not reach the server you are connected to.");
                return;
            }

            long taken = 0;
            if (!deposit)
            {
                DateTime now = _runtime.World != null ? _runtime.World.UtcNow : DateTime.UtcNow;
                long routable = NodeTransfer.Withdrawable(node, invKey, now);
                string refusal = WithdrawRefusal(node, invKey, amount, routable, now);
                if (refusal != null) { Refuse(steamId, node, deposit, invKey, amount, refusal); return; }
                taken = NodeTransfer.TakeFromNode(node, invKey, amount, now);
                if (taken <= 0) { Refuse(steamId, node, deposit, invKey, amount, node.DisplayName + " holds no " + invKey + "."); return; }
            }

            queue(home, new NexusStockMove
            {
                Steam = steamId,
                Node = node.Id,
                NodeName = node.DisplayName,
                Owner = node.OwnerFactionTag,
                Key = invKey,
                Amount = deposit ? amount : taken,
                Asked = amount,
                Deposit = deposit,
                Target = targetEntityId,
            });
            Trace(node, deposit, invKey, amount, taken, true);
        }

        /// <summary>Why a withdrawal of <paramref name="amount"/> cannot be made; null when it can.</summary>
        private static string WithdrawRefusal(SpatialNode node, string invKey, long amount, long routable, DateTime now)
        {
            if (routable <= 0)
            {
                long reserved = NodeTransfer.Reserved(node, invKey, now);
                return reserved > 0
                    ? "All " + reserved + " of " + invKey + " here is reserved for an active build or a capability install."
                    : node.DisplayName + " holds no " + invKey + ".";
            }
            if (amount > routable)
                return "Only " + routable + " of " + invKey + " is free to move; the rest is reserved"
                       + " for an active build or a capability install.";
            return null;
        }

        /// <summary>Agent: carries one stock move between the node and the block it names.
        /// <paramref name="credit"/> is what goes back to the node's stock: a deposit's removed units,
        /// or the withdrawn units that did not land. <paramref name="node"/> is the snapshot's, for
        /// its position and owner; nothing here touches its stock.</summary>
        internal static bool ApplyMove(NexusStockMove move, SpatialNode node, DefinitionIdResolver resolve,
                                       out long moved, out long credit, out string message)
        {
            moved = 0;
            credit = move.Deposit ? 0 : move.Amount;
            MyDefinitionId defId;
            if (resolve == null || !resolve(move.Key, out defId))
            {
                message = "This server has no item definition for " + move.Key + ".";
                return false;
            }
            MyEntity target;
            string refusal;
            if (!TryResolveTarget(move.Target, node, out target, out refusal))
            {
                message = refusal;
                return false;
            }

            if (!move.Deposit)
            {
                MyInventory inv = target.GetInventory(0);
                if (inv == null) { message = "That target has no inventory."; return false; }
                long room = (long)inv.ComputeAmountThatFits(defId);
                long toMove = move.Amount < room ? move.Amount : room;
                if (toMove <= 0) { message = "That inventory is full."; return true; }
                var builder = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(defId);
                if (!inv.AddItems((MyFixedPoint)(double)toMove, builder))
                {
                    message = "That inventory would not take the items.";
                    return true;
                }
                moved = toMove;
                credit = move.Amount - toMove;
                message = moved < move.Amount
                    ? "Moved " + moved + " of " + move.Amount + "; the inventory filled up."
                    : "Moved " + moved + " " + move.Key + ".";
                return true;
            }

            moved = RemoveFrom(target, defId, move.Amount);
            credit = moved;
            if (moved <= 0) { message = "There is no " + move.Key + " in that inventory."; return true; }
            message = moved < move.Asked
                ? "Stored " + moved + " of " + move.Asked + "; that was all there was."
                : "Stored " + moved + " " + move.Key + " at " + (move.NodeName ?? move.Node) + ".";
            return true;
        }

        /// <summary>Whole units of <paramref name="defId"/> removed from every inventory the block
        /// has, up to <paramref name="amount"/>.</summary>
        private static long RemoveFrom(MyEntity target, MyDefinitionId defId, long amount)
        {
            long moved = 0;
            long remaining = amount;
            for (int i = 0; i < target.InventoryCount && remaining > 0; i++)
            {
                MyInventory inv = target.GetInventory(i);
                if (inv == null) continue;
                long whole = (long)inv.GetItemAmount(defId);
                long take = remaining < whole ? remaining : whole;
                if (take <= 0) continue;
                long removed = (long)inv.RemoveItemsOfType((MyFixedPoint)(double)take, defId);
                if (removed <= 0) continue;
                moved += removed;
                remaining -= removed;
            }
            return moved;
        }

        // ---- Authorisation ---------------------------------------------------------------------

        /// <summary>Whether this client may act on this node at all, and the node it named.
        ///
        /// <para>Four separate facts, none of them taken from the request: the sender is a live player
        /// with an identity; that identity is in an SE faction Factions knows as a player faction;
        /// that faction's tag is the node's owner; and the player's own character is standing within
        /// <see cref="SearchRadiusKm"/> of the node. A client-side hide of the surface is a
        /// convenience — this is the gate.</para></summary>
        private bool TryAuthorise(ulong steamId, long stationId, string nodeId,
                                  out SpatialNode node, out string refusal)
        {
            node = null;
            refusal = null;
            if (_runtime == null || _runtime.World == null || MySession.Static == null)
            {
                refusal = "Factions is not ready yet.";
                return false;
            }

            // A player connected here stands where their character is; one connected to a Nexus
            // agent stands where that agent last reported them, and resolves to this server's
            // identity for their Steam id.
            MyPlayer player = PlayerFor(steamId);
            Vector3D standing = Vector3D.Zero;
            bool standingKnown;
            long identityId;
            if (player != null && player.Identity != null)
            {
                identityId = player.Identity.IdentityId;
                IMyCharacter character = player.Character;
                standingKnown = character != null;
                if (standingKnown) standing = character.PositionComp.GetPosition();
            }
            else
            {
                byte home;
                identityId = LinkedViewers.IdentityFor(steamId);
                if (identityId == 0L || !LinkedViewers.TryRemotePosition(steamId, out standing, out home))
                {
                    refusal = "Could not identify you on this server.";
                    return false;
                }
                standingKnown = true;
            }

            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            FactionState owner = seFaction != null ? _runtime.FindFaction(seFaction.Tag) : null;
            if (owner == null || !owner.IsPlayerFaction)
            {
                refusal = "Your faction is not registered with Factions yet.";
                return false;
            }

            node = ResolveNode(stationId, nodeId);
            if (node == null)
            {
                refusal = "Factions does not know this station.";
                return false;
            }
            if (!string.Equals(node.OwnerFactionTag, owner.Tag, StringComparison.OrdinalIgnoreCase))
            {
                refusal = "This station does not belong to " + owner.Tag + ".";
                return false;
            }

            if (!standingKnown || !WithinReach(standing, node))
            {
                refusal = "Move within " + (int)SearchRadiusKm + " km of the station to move its stock.";
                return false;
            }
            return true;
        }

        /// <summary>The node a request names. A store block knows only the station it belongs to, and
        /// the node window knows only the node id — and a claim the player has not founded has no
        /// station at all — so both are accepted and the node id wins when both are present.
        ///
        /// <para><b>A station is known by two ids and either may arrive here.</b>
        /// <see cref="SpatialNode.StableStationId"/> holds an SE station id for a node this plugin
        /// founded and the station grid's own entity id for a scan-adopted one
        /// (<c>StationFounder</c> names that case), and a store block asking on its own behalf sends
        /// whichever of the two its grid resolved to. Matching only the value as sent therefore leaves
        /// the two crossed cases — a scan-adopted node asked about by station id, a founded one asked
        /// about by grid id — resolving to nothing at all, which reaches the player as the station
        /// simply not offering the surface and leaves no trace anywhere to say why.</para></summary>
        private SpatialNode ResolveNode(long stationId, string nodeId)
        {
            SpatialNode node;
            if (!string.IsNullOrEmpty(nodeId)
             && _runtime.World.Graph.Nodes.TryGetValue(nodeId, out node)) return node;
            if (stationId == 0L) return null;

            long alternate = AlternateStationId(stationId);
            foreach (SpatialNode candidate in _runtime.World.Graph.Nodes.Values)
            {
                if (candidate.StableStationId == 0L) continue;
                if (candidate.StableStationId == stationId) return candidate;
                if (alternate != 0L && candidate.StableStationId == alternate) return candidate;
            }
            return null;
        }

        /// <summary>The other id SE knows the same station by: the station grid's entity id given a
        /// station id, or the station id given the grid's entity id. Zero when SE has no station
        /// registered under either reading — the ordinary answer for a claim nobody has founded, and
        /// for anything that is not a station at all.</summary>
        private static long AlternateStationId(long stationId)
        {
            if (MySession.Static == null || MySession.Static.Factions == null) return 0L;
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                foreach (IMyFactionStation station in kv.Value.Stations)
                {
                    if (station == null) continue;
                    if (station.Id == stationId) return station.StationEntityId;
                    if (station.StationEntityId == stationId) return station.Id;
                }
            }
            return 0L;
        }

        /// <summary>The block whose inventory the transfer moves through, once it has been confirmed
        /// to sit on a grid the node's own faction owns and to be standing at the node.
        ///
        /// <para>A block rather than a grid, because that is the unit the store screen's own dropdown
        /// hands out and the unit SE's store protocol carries. The grid behind it is what carries the
        /// ownership.</para></summary>
        private static bool TryResolveTarget(long entityId, SpatialNode node, out MyEntity target, out string refusal)
        {
            target = null;
            refusal = null;
            MyEntity entity;
            if (entityId == 0L || !MyEntities.TryGetEntityById(entityId, out entity) || entity == null)
            {
                refusal = "Choose where the goods should go.";
                return false;
            }

            var block = entity as MyCubeBlock;
            MyCubeGrid grid = block != null ? block.CubeGrid : null;
            if (grid == null)
            {
                refusal = "That is not a block this station can load.";
                return false;
            }
            if (!IsOwnedByFaction(grid, node.OwnerFactionTag))
            {
                refusal = "That grid does not belong to " + node.OwnerFactionTag + ".";
                return false;
            }
            if (!WithinReach(grid.PositionComp.GetPosition(), node))
            {
                refusal = "That grid is too far from the station.";
                return false;
            }
            if (entity.InventoryCount <= 0)
            {
                refusal = "That block has no inventory.";
                return false;
            }
            target = entity;
            return true;
        }

        /// <summary>Node positions are kept in kilometres; SE's are in metres. Compared squared so
        /// the check costs no square root.</summary>
        private static bool WithinReach(Vector3D worldPosition, SpatialNode node)
        {
            var centre = new Vector3D(node.Position.X * 1000.0, node.Position.Y * 1000.0, node.Position.Z * 1000.0);
            double reach = SearchRadiusKm * 1000.0;
            return Vector3D.DistanceSquared(worldPosition, centre) <= reach * reach;
        }

        /// <summary>The grid's owning faction, read the way the rest of this plugin reads it: the
        /// first non-zero big owner, resolved through SE's own faction membership.
        ///
        /// <para>The resolution step is the point of it. A big owner is an <b>identity</b>, never a
        /// faction id — a ship a player built and is flying is owned by that player personally — so
        /// comparing the owner against a faction would fail on exactly the grid the node window's
        /// transfer targets, the one the caller is piloting. Membership is what makes it the
        /// faction's grid.</para></summary>
        private static bool IsOwnedByFaction(MyCubeGrid grid, string factionTag)
        {
            if (grid == null || grid.Projector != null) return false;
            List<long> owners = grid.BigOwners;
            long ownerId = 0L;
            if (owners != null && owners.Count > 0 && owners[0] != 0L) ownerId = owners[0];
            else if (owners != null && owners.Count > 1) ownerId = owners[1];
            if (ownerId == 0L) return false;

            MyFaction faction = MySession.Static.Factions.GetPlayerFaction(ownerId);
            return faction != null && string.Equals(faction.Tag, factionTag, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether this client was granted the hand at link time. Checked on every request
        /// rather than trusted from the client's own dispatch table: the feature has its own bit
        /// precisely so a server can withhold it, and a withheld feature that still answers is not
        /// withheld.</summary>
        private bool Granted(ulong steamId)
        {
            return _uplink != null && (_uplink.GrantedFeatures(steamId) & WireFeatures.NodeInventory) != 0;
        }

        private static MyPlayer PlayerFor(ulong steamId)
        {
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Identity == null) continue;
                if (player.Id.SteamId == steamId) return player;
            }
            return null;
        }

        private void Refuse(ulong steamId, SpatialNode node, bool deposit, string invKey, long amount, string reason)
        {
            _uplink.Send(steamId, FactionsWire.WriteNodeTransferResult(false, 0, reason));
            Trace(node, deposit, invKey, amount, 0, false);
        }

        /// <summary>One line per attempted transfer, behind the same switch that gates the store's
        /// own listing census. Off by default and built only when it is on: the Torch log is where
        /// the engine's stall diagnostics land, and a goods movement is diagnostic, not a record.</summary>
        private void Trace(SpatialNode node, bool deposit, string invKey, long asked, long moved, bool ok)
        {
            if (!StationStoreManager.DebugEnabled) return;
            DateTime now = _runtime != null && _runtime.World != null ? _runtime.World.UtcNow : DateTime.UtcNow;
            Log.Info("NODE_XFER node=" + (node != null ? node.Id : "-")
                     + " faction=" + (node != null ? node.OwnerFactionTag : "-")
                     + " dir=" + (deposit ? "deposit" : "withdraw")
                     + " key=" + (invKey ?? "-")
                     + " asked=" + asked
                     + " moved=" + moved
                     + " routable=" + (node != null ? NodeTransfer.Withdrawable(node, invKey, now) : 0)
                     + " ok=" + ok);
        }
    }
}
