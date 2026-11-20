using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Server side of the K-menu Assets tab. A linked client subscribes when the player enters the
    /// tab and unsubscribes when they leave it, so a player who never opens it costs nothing —
    /// which matters more here than for the map, because answering "what grids do I own" means
    /// sweeping every entity in the world.
    ///
    /// One snapshot carries all three kinds of holding: territory from the faction graph, convoys
    /// from the fleet register, and grids from the live world. The first two exist only for a
    /// faction registered with Factions; the third exists for any player at all, so a player in no
    /// faction still gets a useful list.
    /// </summary>
    internal sealed class PlayerAssetsManager
    {
        /// <summary>Snapshots are sent every this-many scanner ticks. At the scanner's ~5 s cadence
        /// that is ~30 s, which is far faster than territory or a build queue changes and slow
        /// enough that the entity sweep below stays off the profile.</summary>
        private const int TicksPerSnapshot = 6;

        /// <summary>Grids smaller than this are debris, seats and dropped tools as far as an asset
        /// register is concerned. Listing them would bury the ships in noise.</summary>
        private const int MinBlocksToList = 8;

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs with the Assets tab open.</summary>
        private readonly HashSet<ulong> _streaming = new HashSet<ulong>();

        /// <summary>Subscribers still owed a snapshot on the next tick, because they only just
        /// subscribed and should not wait out the cadence to see anything.</summary>
        private readonly HashSet<ulong> _owesSnapshot = new HashSet<ulong>();

        private int _tick;

        public PlayerAssetsManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink != null) _uplink.RegisterHandler(WireMessageKind.AssetsRequest, OnAssetsRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        /// <summary>Authority on a Nexus cluster: the player-owned grids each agent last reported.
        /// A player's ships stand on whichever shard they were left in, and this server's own
        /// sweep sees only its own; null off a cluster.</summary>
        internal Func<List<NexusOwnedGrid>> RemoteGrids;

        private void OnAssetsRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadAssetsRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Assets request from " + steamId + " was unreadable.");
                return;
            }

            if (stream)
            {
                _streaming.Add(steamId);
                _owesSnapshot.Add(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("ASSETS_SUBSCRIBE steam=" + steamId);
            }
            else
            {
                _streaming.Remove(steamId);
                _owesSnapshot.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("ASSETS_UNSUBSCRIBE steam=" + steamId);
            }
        }

        /// <summary>Runs on the scanner cadence. Does nothing at all while no one has the tab open,
        /// which is the normal case.</summary>
        public void Tick()
        {
            if (_streaming.Count == 0) return;
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;

            bool due = (_tick++ % TicksPerSnapshot) == 0;

            // Collect the recipients first: the grid sweep is paid once for the whole pass, not
            // once per subscriber, so it must know whether anyone is actually owed a send.
            List<LinkedViewer> recipients = null;
            foreach (LinkedViewer player in LinkedViewers.Collect())
            {
                ulong steamId = player.SteamId;
                if (!_streaming.Contains(steamId)) continue;
                if (!due && !_owesSnapshot.Contains(steamId)) continue;
                if (recipients == null) recipients = new List<LinkedViewer>();
                recipients.Add(player);
            }
            if (recipients == null) return;

            List<OwnedGrid> grids = SweepGrids();
            AppendRemoteGrids(grids);
            List<MapFleetInfo> fleets = _runtime.GetMapFleets();

            foreach (LinkedViewer player in recipients)
            {
                _owesSnapshot.Remove(player.SteamId);
                SendSnapshot(player, grids, fleets);
            }
        }

        private void SendSnapshot(LinkedViewer player, List<OwnedGrid> grids, List<MapFleetInfo> fleets)
        {
            long identityId = player.IdentityId;
            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            string tag = seFaction != null ? seFaction.Tag : null;
            long factionId = seFaction != null ? seFaction.FactionId : 0L;

            var wireNodes = new List<WireAssetNode>();
            var wireFleets = new List<WireAssetFleet>();

            // Territory and convoys exist only once the faction is registered with Factions —
            // a brand-new player faction that has claimed nothing yet has neither, and that is a
            // state the tab shows rather than an error.
            FactionState owner = tag != null ? _runtime.FindFaction(tag) : null;
            if (owner != null)
            {
                foreach (SpatialNode node in _runtime.World.Graph.OwnedBy(owner.Tag))
                {
                    if (node == null || string.IsNullOrEmpty(node.Id)) continue;
                    wireNodes.Add(DescribeNode(owner, node));
                }

                foreach (MapFleetInfo fleet in fleets)
                {
                    if (!string.Equals(fleet.OwnerTag, owner.Tag, StringComparison.Ordinal)) continue;
                    wireFleets.Add(DescribeFleet(fleet));
                }
            }

            var wireGrids = new List<WireAssetGrid>();
            foreach (OwnedGrid grid in grids)
            {
                bool mine = grid.OwnerIdentityId == identityId;
                bool factionMate = !mine && factionId != 0L && grid.OwnerFactionId == factionId;
                if (!mine && !factionMate) continue;

                wireGrids.Add(new WireAssetGrid
                {
                    EntityId = grid.EntityId,
                    Name = grid.Name,
                    IsStatic = grid.IsStatic,
                    GridSize = grid.GridSize,
                    BlockCount = grid.BlockCount,
                    XKm = grid.PositionM.X / 1000.0,
                    YKm = grid.PositionM.Y / 1000.0,
                    ZKm = grid.PositionM.Z / 1000.0,
                    OwnedPersonally = mine,
                    OwnerName = mine ? "" : grid.OwnerName,
                });
            }

            ulong steamId = player.SteamId;
            _uplink.Send(steamId, FactionsWire.WriteAssetsSnapshot(
                tag ?? "", seFaction != null ? seFaction.Name : "", wireNodes, wireFleets, wireGrids));
            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("ASSETS_SNAPSHOT steam=" + steamId
                                     + " nodes=" + wireNodes.Count
                                     + " fleets=" + wireFleets.Count
                                     + " grids=" + wireGrids.Count);
            }
        }

        private WireAssetNode DescribeNode(FactionState owner, SpatialNode node)
        {
            bool founded = node.StableStationId != 0;

            WireAssetCapability[] caps = null;
            if (node.Capabilities.Count > 0)
            {
                caps = new WireAssetCapability[node.Capabilities.Count];
                int i = 0;
                foreach (KeyValuePair<NodeCapabilityKind, int> pair in node.Capabilities)
                {
                    caps[i].Cap = pair.Key.ToString();
                    caps[i].Level = pair.Value;
                    i++;
                }
            }

            int shipsReady = 0;
            foreach (KeyValuePair<string, int> pair in node.ShipStock) shipsReady += pair.Value;

            return new WireAssetNode
            {
                Id = node.Id,
                DisplayName = node.DisplayName,
                DominantOre = NodeDisplay.DominantOre(node),
                Surveyed = NodeDisplay.Surveyed(node),
                Zone = (byte)node.Zone,
                HasStation = founded,
                XKm = node.Position.X,
                YKm = node.Position.Y,
                ZKm = node.Position.Z,
                HoldStrength = node.HoldStrength,
                ResourceValue = node.ResourceValue,
                Richness = node.Richness,
                // Zero once founded: there is nothing left to buy, and a price beside a built
                // station reads as an outstanding bill.
                FoundCost = founded ? 0L : _runtime.World.Tuning.FoundCost,
                ShipsReady = shipsReady,
                BuildsQueued = node.BuildQueue.Count,
                ShipsInRepair = node.DamagedHulls.Count,
                ShipyardPaused = node.ShipyardPaused,
                Capabilities = caps,
            };
        }

        private WireAssetFleet DescribeFleet(MapFleetInfo fleet)
        {
            return new WireAssetFleet
            {
                Key = fleet.Key,
                Kind = (byte)fleet.Kind,
                XKm = fleet.PositionKm.X,
                YKm = fleet.PositionKm.Y,
                ZKm = fleet.PositionKm.Z,
                OriginName = NodeDisplayName(fleet.OriginNodeId),
                DestinationName = NodeDisplayName(fleet.TargetNodeId),
                TargetXKm = fleet.TargetPosKm.X,
                TargetYKm = fleet.TargetPosKm.Y,
                TargetZKm = fleet.TargetPosKm.Z,
                EtaUtcTicks = fleet.EstimatedArrivalUtc.Ticks,
                ConvoySize = fleet.ConvoySize,
                EntityId = fleet.MaterialisedEntityId,
            };
        }

        /// <summary>Name for a node id, falling back to the id itself — an unnamed or unfounded
        /// node still has to be nameable in a route line.</summary>
        private string NodeDisplayName(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return "";
            SpatialNode node;
            if (!_runtime.World.Graph.Nodes.TryGetValue(nodeId, out node) || node == null) return nodeId;
            return string.IsNullOrEmpty(node.DisplayName) ? nodeId : node.DisplayName;
        }

        /// <summary>One grid as the sweep found it, with ownership already resolved. Kept per-pass
        /// and shared across subscribers so the entity sweep is paid once.</summary>
        private struct OwnedGrid
        {
            public long EntityId;
            public string Name;
            public bool IsStatic;
            public byte GridSize;
            public int BlockCount;
            public Vector3D PositionM;
            public long OwnerIdentityId;
            public long OwnerFactionId;
            public string OwnerName;
        }

        /// <summary>
        /// Every grid in the world that has an owner, with that owner's identity and faction.
        /// Filtered per subscriber afterwards rather than per subscriber here: two players in the
        /// same faction would otherwise sweep the entity list twice for the same answer.
        /// </summary>
        private List<OwnedGrid> SweepGrids()
        {
            var found = new List<OwnedGrid>();
            var identityNames = new Dictionary<long, string>();
            var identityFactions = new Dictionary<long, long>();

            MyAPIGateway.Entities.GetEntities(null, e =>
            {
                var grid = e as MyCubeGrid;
                if (grid == null || grid.MarkedForClose) return false;
                if (grid.BlocksCount < MinBlocksToList) return false;

                long ownerId = PrimaryOwner(grid);
                if (ownerId == 0L) return false;   // unowned / derelict — nobody's asset

                string ownerName;
                if (!identityNames.TryGetValue(ownerId, out ownerName))
                {
                    MyIdentity identity = MySession.Static.Players.TryGetIdentity(ownerId);
                    ownerName = identity != null ? identity.DisplayName : "";
                    identityNames[ownerId] = ownerName;

                    MyFaction ownerFaction = MySession.Static.Factions.GetPlayerFaction(ownerId);
                    identityFactions[ownerId] = ownerFaction != null ? ownerFaction.FactionId : 0L;
                }

                found.Add(new OwnedGrid
                {
                    EntityId = grid.EntityId,
                    Name = grid.DisplayName,
                    IsStatic = grid.IsStatic,
                    GridSize = (byte)grid.GridSizeEnum,
                    BlockCount = grid.BlocksCount,
                    PositionM = grid.PositionComp != null ? grid.PositionComp.GetPosition() : Vector3D.Zero,
                    OwnerIdentityId = ownerId,
                    OwnerFactionId = identityFactions[ownerId],
                    OwnerName = ownerName,
                });
                return false;   // the predicate is the visitor; nothing is collected into the list
            });

            return found;
        }

        /// <summary>Adds the agents' grids to this server's sweep, with owner and faction resolved
        /// here as the sweep resolves its own. A grid this server also holds — one caught mid
        /// transfer — is listed once, from the local sweep.</summary>
        private void AppendRemoteGrids(List<OwnedGrid> grids)
        {
            List<NexusOwnedGrid> remote = RemoteGrids != null ? RemoteGrids() : null;
            if (remote == null || remote.Count == 0) return;

            var seen = new HashSet<long>();
            foreach (OwnedGrid grid in grids) seen.Add(grid.EntityId);
            var identityFactions = new Dictionary<long, long>();

            foreach (NexusOwnedGrid grid in remote)
            {
                if (grid.Blocks < MinBlocksToList || !seen.Add(grid.Id)) continue;

                long factionId;
                if (!identityFactions.TryGetValue(grid.Owner, out factionId))
                {
                    MyFaction ownerFaction = MySession.Static.Factions.GetPlayerFaction(grid.Owner);
                    factionId = ownerFaction != null ? ownerFaction.FactionId : 0L;
                    identityFactions[grid.Owner] = factionId;
                }
                MyIdentity identity = MySession.Static.Players.TryGetIdentity(grid.Owner);

                grids.Add(new OwnedGrid
                {
                    EntityId = grid.Id,
                    Name = grid.Name ?? "",
                    IsStatic = grid.Static,
                    GridSize = grid.Size,
                    BlockCount = grid.Blocks,
                    PositionM = new Vector3D(grid.X, grid.Y, grid.Z),
                    OwnerIdentityId = grid.Owner,
                    OwnerFactionId = factionId,
                    OwnerName = identity != null ? identity.DisplayName : (grid.OwnerName ?? ""),
                });
            }
        }

        /// <summary>Mirrors the BigOwners first-non-zero idiom used by
        /// <see cref="ShipCaptureScanner"/> and <c>WorldScanner.GetOwningNpcFaction</c>, so all
        /// three agree on who a grid belongs to.</summary>
        private static long PrimaryOwner(MyCubeGrid grid)
        {
            List<long> owners = grid.BigOwners;
            if (owners == null || owners.Count == 0) return 0L;
            if (owners[0] != 0L) return owners[0];
            return owners.Count > 1 ? owners[1] : 0L;
        }

        /// <summary>Drops subscribers that are no longer online, on the same cadence and for the
        /// same reason <see cref="UplinkManager.PruneOffline"/> drops links.</summary>
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
                _owesSnapshot.Remove(steamId);
            }
        }

        public void Close()
        {
            _streaming.Clear();
            _owesSnapshot.Clear();
        }
    }
}
