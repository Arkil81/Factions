using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.API.Managers;
using Torch.Managers.ChatManager;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// A Nexus agent's half of every capture: <c>!factions ship capture</c>, <c>!factions station
    /// scan</c>, <c>!factions livery capture</c>, the Services Terminal's ship and station scans, and
    /// the scan dialogs' "Publish as" query. The grid stands here, so the checks that read it run
    /// here -- access, static or not, the block cap, the root grid -- the same ones a local capture
    /// runs; the design is named and registered by the authority
    /// (<see cref="FactionRuntime.RegisterRelayedCapture"/>), which answers on
    /// <see cref="RelayKind.CaptureResult"/>. The answer goes back where the player asked.
    ///
    /// <para>This server spawns captured designs from its own read of the data directory's store.
    /// The authority's snapshot carries its capture generation; when that moves the store is read
    /// again, and the old one kept if the read fails.</para>
    /// </summary>
    internal sealed class AgentCaptures
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);
        private const string WhisperAuthor = "Factions";
        internal const string Sent = "Scan sent; the answer follows.";
        internal const string NoAnswer = "The scan was not answered. Try again shortly.";

        private sealed class Asked
        {
            public ulong Steam;
            public NexusCaptureSurface Surface;
            public long EntityId;
            public bool Station;
        }

        private readonly FactionsPlugin _plugin;
        private readonly UplinkManager _uplink;
        private readonly Func<FactionTuning> _tuning;
        private readonly Func<AgentHands> _hands;
        private readonly NexusCapturePending<Asked> _pending = new NexusCapturePending<Asked>();

        public AgentCaptures(FactionsPlugin plugin, UplinkManager uplink, Func<FactionTuning> tuning, Func<AgentHands> hands)
        {
            _plugin = plugin;
            _uplink = uplink;
            _tuning = tuning;
            _hands = hands;
            if (_uplink == null) return;
            _uplink.CaptureResultReceived = OnResult;
            _uplink.RegisterHandler(WireMessageKind.ShipScanRequest, OnShipScan);
            _uplink.RegisterHandler(WireMessageKind.DesignVersionQuery, OnDesignQuery);
        }

        public void Close()
        {
            if (_uplink != null) _uplink.CaptureResultReceived = null;
        }

        // ---- chat ---------------------------------------------------------------------------------

        /// <summary>A chat capture of the grid <paramref name="player"/> looks at. Returns what to
        /// answer now; the authority's answer is whispered when it comes.</summary>
        public string Chat(NexusCaptureKind kind, IMyPlayer player, string scope, string name, string liveryTag)
        {
            if (player == null) return "Run this in game, looking at the " + (kind == NexusCaptureKind.Station ? "station." : "ship.");
            string owner, refusal;
            if (!TryOwner(kind, player.IdentityId, liveryTag, out owner, out refusal)) return refusal;
            FactionTuning t = _tuning();
            List<MyCubeGrid> grids;
            string error;
            if (!ShipCaptureScanner.TryFindTargetedGrids(player, t.ShipCaptureMaxRangeM, out grids, out error))
                return error;
            return Upload(kind, NexusCaptureSurface.Chat, player, grids, owner, scope, name, 0, out refusal)
                ? Sent : refusal;
        }

        // ---- terminal -----------------------------------------------------------------------------

        private void OnShipScan(ulong steamId, byte[] data)
        {
            long terminalEntityId, gridEntityId;
            string scope, name;
            if (!FactionsWire.TryReadShipScanRequest(data, out terminalEntityId, out gridEntityId, out scope, out name))
            {
                FactionsPlugin.Log.Warn("Ship scan request from " + steamId + " was unreadable.");
                return;
            }
            if ((_uplink.GrantedFeatures(steamId) & WireFeatures.ShipAssets) == 0) return;
            string message;
            if (!TryShipScan(steamId, terminalEntityId, gridEntityId, scope, name, out message))
                _uplink.Send(steamId, FactionsWire.WriteShipScanResult(false, message));
        }

        private bool TryShipScan(ulong steamId, long terminalEntityId, long gridEntityId, string scope, string name,
                                 out string message)
        {
            TerminalContext ctx;
            if (!TerminalAccess.TryAuthorise(_plugin.AgentTerminalWorld(), steamId, terminalEntityId, out ctx, out message))
                return false;
            List<MyCubeGrid> group;
            if (!TryGroup(gridEntityId, out group)) { message = "That ship is no longer there."; return false; }
            if (!PlayerShipAssetsManager.WithinDesignatedArea(ctx.Block, group))
            {
                message = "Bring the ship within " + PlayerShipAssetsManager.DesignatedAreaM.ToString("0") + " m of the terminal.";
                return false;
            }
            return Upload(NexusCaptureKind.Ship, NexusCaptureSurface.TerminalShip, ctx.Player, group, ctx.Faction.Tag,
                          scope, name, 0, out message);
        }

        /// <summary>The station refit panel's Scan station, from <see cref="AgentTerminalSurfaces"/>.
        /// False with <paramref name="message"/> when it was refused here; true when it was sent.</summary>
        public bool StationScan(ulong steamId, long terminalEntityId, string scope, string name, out string message)
        {
            TerminalContext ctx;
            if (!TerminalAccess.TryAuthorise(_plugin.AgentTerminalWorld(), steamId, terminalEntityId, out ctx, out message))
                return false;
            List<MyCubeGrid> group;
            if (ctx.Block.CubeGrid == null || !TryGroup(ctx.Block.CubeGrid.EntityId, out group))
            {
                message = "That station is no longer there.";
                return false;
            }
            return Upload(NexusCaptureKind.Station, NexusCaptureSurface.TerminalStation, ctx.Player, group,
                          ctx.Faction.Tag, scope, name, 0, out message);
        }

        private void OnDesignQuery(ulong steamId, byte[] data)
        {
            long terminalEntityId, gridEntityId;
            bool station;
            if (!FactionsWire.TryReadDesignVersionQuery(data, out terminalEntityId, out gridEntityId, out station))
            {
                FactionsPlugin.Log.Warn("Design version query from " + steamId + " was unreadable.");
                return;
            }
            if ((_uplink.GrantedFeatures(steamId) & WireFeatures.DesignVersions) == 0) return;
            long entityId = station ? terminalEntityId : gridEntityId;
            TerminalContext ctx;
            string message;
            if (!TerminalAccess.TryAuthorise(_plugin.AgentTerminalWorld(), steamId, terminalEntityId, out ctx, out message))
            {
                SendInfo(steamId, entityId, station, null, message);
                return;
            }
            var up = new NexusCaptureUpload
            {
                Kind = (int)(station ? NexusCaptureKind.PreviewStation : NexusCaptureKind.PreviewShip),
                Surface = (int)NexusCaptureSurface.DesignQuery,
                Steam = steamId, Identity = ctx.IdentityId, Owner = ctx.Faction.Tag,
            };
            if (station) up.AdoptNode = ctx.Node.Id;
            else
            {
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById<MyCubeGrid>(gridEntityId, out grid, false)
                    || grid == null || grid.MarkedForClose || grid.Closed)
                {
                    SendInfo(steamId, entityId, false, null, "That ship is no longer there.");
                    return;
                }
                Func<long, string> prefabOf = CrewPresence.AgentPrefabOf;
                up.KnownId = prefabOf != null ? prefabOf(grid.EntityId) : null;
                up.DisplayName = grid.DisplayName;
            }
            Send(up, new Asked { Steam = steamId, Surface = NexusCaptureSurface.DesignQuery, EntityId = entityId, Station = station });
        }

        // ---- the shared half ----------------------------------------------------------------------

        private bool TryOwner(NexusCaptureKind kind, long identityId, string liveryTag, out string owner, out string refusal)
        {
            owner = null;
            AgentHands hands = _hands();
            if (hands == null || hands.Graph == null) { refusal = "Factions is not ready on this server."; return false; }
            if (kind == NexusCaptureKind.Livery)
            {
                FactionState target = hands.FindFaction(liveryTag);
                if (target == null) { refusal = "No registered faction [" + (liveryTag ?? "") + "]."; return false; }
                owner = target.Tag;
                refusal = null;
                return true;
            }
            MyFaction se = MySession.Static != null && MySession.Static.Factions != null
                ? MySession.Static.Factions.GetPlayerFaction(identityId) : null;
            FactionState f = se != null ? hands.FindFaction(se.Tag) : null;
            if (f == null || !f.IsPlayerFaction) { refusal = "Your faction is not registered with Factions yet."; return false; }
            owner = f.Tag;
            refusal = null;
            return true;
        }

        private static bool TryGroup(long gridEntityId, out List<MyCubeGrid> group)
        {
            group = null;
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById<MyCubeGrid>(gridEntityId, out grid, false)
                || grid == null || grid.MarkedForClose || grid.Closed) return false;
            group = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid);
            return group != null && group.Count > 0;
        }

        /// <summary>The checks a local capture runs on the grid itself, then the upload.</summary>
        private bool Upload(NexusCaptureKind kind, NexusCaptureSurface surface, IMyPlayer player, List<MyCubeGrid> grids,
                            string owner, string scope, string name, long entityId, out string refusal)
        {
            bool station = kind == NexusCaptureKind.Station;
            FactionTuning t = _tuning();
            if (grids == null || grids.Count == 0)
            {
                refusal = station ? "That's not a station — look directly at the hull." : "That's not a ship — look directly at the hull.";
                return false;
            }
            if (!ShipCaptureScanner.PlayerHasAccessToAll(player, grids, out refusal)) return false;
            foreach (MyCubeGrid g in grids)
            {
                if (g == null) continue;
                if (station && !g.IsStatic) { refusal = "That is not a station: " + g.DisplayName + " is a ship grid."; return false; }
                if (!station && g.IsStatic) { refusal = "That is a station — use !factions station scan."; return false; }
            }
            int blocks = 0;
            foreach (MyCubeGrid g in grids) if (g != null) blocks += g.BlocksCount;
            int cap = station ? t.StationCaptureMaxBlocks : t.ShipCaptureMaxBlocks;
            if (blocks > cap)
            {
                refusal = "That " + (station ? "station" : "ship") + " has " + blocks + " blocks — the cap is " + cap + ".";
                return false;
            }
            if (station) FactionRuntime.PromoteLargestGridToRoot(grids);

            var builders = new MyObjectBuilder_CubeGrid[grids.Count];
            for (int i = 0; i < grids.Count; i++) builders[i] = (MyObjectBuilder_CubeGrid)grids[i].GetObjectBuilder(true);
            string xml = CustomPrefabStore.GridsToXml(builders);
            if (xml == null) { refusal = "The grid could not be read."; return false; }

            Vector3D root = grids[0].PositionComp.GetPosition();
            var up = new NexusCaptureUpload
            {
                Kind = (int)kind, Surface = (int)surface, Steam = player.SteamUserId, Identity = player.IdentityId,
                Owner = owner, Scope = scope, Name = name, Blocks = blocks,
                Px = root.X / 1000.0, Py = root.Y / 1000.0, Pz = root.Z / 1000.0,
                GridsXml = xml,
            };
            if (station)
            {
                AgentHands hands = _hands();
                if (hands != null)
                    foreach (MyCubeGrid g in grids)
                    {
                        if (g == null) continue;
                        up.AdoptNode = hands.NodeIdForStationGrid(g.EntityId);
                        if (up.AdoptNode != null) break;
                    }
            }
            if (!Send(up, new Asked { Steam = player.SteamUserId, Surface = surface, EntityId = entityId }))
            {
                refusal = "The scan could not be sent to the server that registers designs.";
                return false;
            }
            refusal = null;
            return true;
        }

        /// <summary>Sends a design registered here to the authority, which writes it and pays its fee
        /// to the node's owner. Nobody waits on the answer: the payer has been charged and the design
        /// is already usable here (<see cref="CustomPrefabStore.KeepDesign"/>).</summary>
        public bool RegisterDesign(ulong steamId, long identityId, SpatialNode node, long fee, long entityId,
                                   string fingerprint, string gridName, MyObjectBuilder_CubeGrid[] grids)
        {
            if (_uplink == null || grids == null || grids.Length == 0) return false;
            int blocks = 0;
            foreach (MyObjectBuilder_CubeGrid g in grids) if (g != null && g.CubeBlocks != null) blocks += g.CubeBlocks.Count;
            var up = new NexusCaptureUpload
            {
                Kind = (int)NexusCaptureKind.Design, Surface = (int)NexusCaptureSurface.TerminalShip,
                Steam = steamId, Identity = identityId, Owner = node != null ? node.OwnerFactionTag : null,
                AdoptNode = node != null ? node.Id : null, EntityId = entityId, Fingerprint = fingerprint, Fee = fee,
                DisplayName = gridName, Blocks = blocks, GridsXml = CustomPrefabStore.GridsToXml(grids),
                // Below every pending scan's id, so its answer is never taken for one.
                Id = -DateTime.UtcNow.Ticks,
            };
            bool ok = _uplink.ForwardFrame(RelayKind.CaptureUpload, steamId, up.Encode(), "design");
            if (!ok) Log.Warn("NEXUS_DESIGN_NOT_SENT grid=" + entityId + " steam=" + steamId);
            return ok;
        }

        private bool Send(NexusCaptureUpload up, Asked asked)
        {
            up.Id = _pending.Add(asked, DateTime.UtcNow);
            bool ok = _uplink.ForwardFrame(RelayKind.CaptureUpload, up.Steam, up.Encode(), "capture");
            if (!ok) _pending.Take(up.Id);
            return ok;
        }

        // ---- answers ------------------------------------------------------------------------------

        private void OnResult(ulong steamId, byte[] payload)
        {
            NexusCaptureResult r;
            if (!NexusCaptureResult.TryDecode(payload, out r))
            {
                Log.Warn("NEXUS_CAPTURE_RESULT_UNREADABLE bytes=" + (payload == null ? 0 : payload.Length));
                return;
            }
            Asked asked = _pending.Take(r.Id);
            if (asked == null) return;
            Deliver(asked, r);
        }

        /// <summary>Tells the callers whose scans went unanswered. Called on the agent's tick.</summary>
        public void Tick(DateTime nowUtc)
        {
            List<Asked> lapsed = _pending.Lapse(nowUtc);
            if (lapsed == null) return;
            for (int i = 0; i < lapsed.Count; i++)
                Deliver(lapsed[i], new NexusCaptureResult { Ok = false, Message = NoAnswer, Reason = NoAnswer });
        }

        private void Deliver(Asked asked, NexusCaptureResult r)
        {
            try
            {
                switch (asked.Surface)
                {
                    case NexusCaptureSurface.Chat:
                        Whisper(asked.Steam, r.Message);
                        break;
                    case NexusCaptureSurface.TerminalShip:
                        _uplink.Send(asked.Steam, FactionsWire.WriteShipScanResult(r.Ok, r.Message ?? ""));
                        break;
                    case NexusCaptureSurface.TerminalStation:
                        _uplink.Send(asked.Steam, FactionsWire.WriteStationRefitCommandResult(r.Ok, r.Message ?? ""));
                        // The panel is request/response: ask for the node it shows on the caller's behalf,
                        // so the new design appears in it.
                        if (!string.IsNullOrEmpty(r.Node))
                            _uplink.ForwardClientMessage(asked.Steam, FactionsWire.WriteStationRefitRequest(r.Node), "refit(after scan)");
                        break;
                    case NexusCaptureSurface.DesignQuery:
                        SendInfo(asked.Steam, asked.EntityId, asked.Station, r, r.Reason);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Capture answer to " + asked.Steam + " failed.");
            }
        }

        private void SendInfo(ulong steamId, long entityId, bool station, NexusCaptureResult r, string reason)
        {
            var info = new WireDesignVersionInfo { EntityId = entityId, Station = station, Reason = reason ?? "" };
            if (r != null)
            {
                info.Offered = r.Offered;
                info.Family = r.Family ?? "";
                info.CurrentId = r.CurrentId ?? "";
                info.NextId = r.NextId ?? "";
                info.ScopeAll = r.ScopeAll;
                info.ScopeLabel = r.ScopeLabel ?? "";
            }
            _uplink.Send(steamId, FactionsWire.WriteDesignVersionInfo(info));
        }

        private void Whisper(ulong steamId, string text)
        {
            if (steamId == 0 || string.IsNullOrEmpty(text)) return;
            var chat = _plugin.Torch?.CurrentSession?.Managers?.GetManager<ChatManagerServer>();
            if (chat != null) chat.SendMessageAsOther(WhisperAuthor, text, Color.CornflowerBlue, steamId);
        }
    }
}
