using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the station-refit surface: the panel that lists what a founded node could be
    /// rebuilt as, the three actions that rebuild it, and the Services Terminal button that scans the
    /// station the player is standing in as a new design.
    ///
    /// <para><b>Request/response, not a subscription.</b> Unlike <see cref="PlayerNodeDetailManager"/>
    /// this holds no watch: the refit panel opens over the node window, which already owns this
    /// client's single node watch, and taking that watch away would leave the window behind it
    /// frozen. The panel asks when it opens and again after every command, which is the same shape
    /// <see cref="PlayerNodeTransferManager"/>'s store surface uses for the same reason.</para>
    ///
    /// <para><b>Every request is re-authorised.</b> The three node actions re-resolve the sender's
    /// SteamID to an SE faction, that faction to a <see cref="FactionState"/> that must be a player
    /// faction, and hand the node id to the same <see cref="FactionRuntime"/> entry point the chat
    /// verb calls — which checks ownership itself. The scan goes through
    /// <see cref="TerminalAccess.TryAuthorise"/>, the same gate the Services Terminal's ship surfaces
    /// use, so a client naming a terminal it is not standing at is refused by the code that already
    /// refuses the ship scan.</para>
    /// </summary>
    internal sealed class PlayerStationRefitManager
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>The first line a successful station scan answers with. Matched rather than
        /// re-derived so the wording has one source, exactly as the ship scan matches its own.</summary>
        private const string ScannedPrefix = "Scanned station '";

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        public PlayerStationRefitManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.StationRefitRequest, OnRefitRequest);
            _uplink.RegisterHandler(WireMessageKind.StationRefitCommandRequest, OnCommandRequest);
        }

        public void Close()
        {
        }

        /// <summary>Whether this client was granted the refit surface at link time. Checked on every
        /// request rather than trusted from the client's own dispatch table: the feature has its own
        /// bit precisely so a server can withhold it, and a withheld feature that still answers is
        /// not withheld. A refused request is silent — a client that was never granted the bit does
        /// not send one, so anything reaching here is hand-crafted.</summary>
        private bool Granted(ulong steamId)
        {
            return _uplink != null && (_uplink.GrantedFeatures(steamId) & WireFeatures.StationRefit) != 0;
        }

        // ---- snapshot -----------------------------------------------------------------------------

        private void OnRefitRequest(ulong steamId, byte[] data)
        {
            string nodeId;
            if (!FactionsWire.TryReadStationRefitRequest(data, out nodeId))
            {
                FactionsPlugin.Log.Warn("Station refit request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;
            SendSnapshot(steamId, nodeId);
        }

        private void SendSnapshot(ulong steamId, string nodeId)
        {
            try
            {
                _uplink.Send(steamId, FactionsWire.WriteStationRefitSnapshot(Describe(steamId, nodeId)));
                if (UplinkManager.DebugTraffic)
                    _uplink.WriteTraffic("STATION_REFIT_SNAPSHOT steam=" + steamId + " node=" + nodeId);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Station refit snapshot failed for " + steamId + ".");
            }
        }

        /// <summary>One node as this viewer may see it. Everything past the ownership seam comes from
        /// <see cref="FactionRuntime.DescribeStationRefit"/>, which is also what the chat verb
        /// renders — so the panel and <c>!factions station prefabs</c> can never disagree about what
        /// a design costs from here.</summary>
        private WireStationRefit Describe(ulong steamId, string nodeId)
        {
            var wire = new WireStationRefit { NodeId = nodeId ?? "", Message = "" };

            FactionState owner = ViewerFaction(IdentityFor(steamId));
            if (owner == null)
            {
                wire.Message = "Your faction is not registered with Factions yet.";
                return wire;
            }

            StationRefitOverview overview = _runtime.DescribeStationRefit(owner, nodeId);
            if (overview.Refusal != null)
            {
                wire.Message = overview.Refusal;
                return wire;
            }

            wire.MayRefit = true;
            wire.CurrentPrefabId = overview.CurrentPrefabId ?? "";
            wire.PendingPrefabId = overview.PendingPrefabId ?? "";
            wire.Site = StationPrefabSite.For(overview.Site);
            wire.SiegeBlocked = overview.SiegeBlocked;
            wire.Observed = overview.Observed;
            wire.CollectingPrefabId = overview.CollectingPrefabId ?? "";
            wire.CollectedUnits = overview.CollectedUnits;
            wire.CollectingBomUnits = overview.CollectingBomUnits;

            if (overview.Candidates.Count == 0)
            {
                wire.Message = "No station designs for a "
                               + (overview.Site == StationSite.Surface ? "surface" : "space")
                               + " node yet — stand in the design you want and use Scan station at "
                               + "its Services Terminal.";
            }

            var rows = new List<WireStationRefitCandidate>(overview.Candidates.Count);
            for (int i = 0; i < overview.Candidates.Count; i++)
            {
                StationRefitCandidate c = overview.Candidates[i];
                bool collecting = !string.IsNullOrEmpty(overview.CollectingPrefabId)
                                  && string.Equals(c.PrefabId, overview.CollectingPrefabId,
                                                   StringComparison.OrdinalIgnoreCase);
                rows.Add(new WireStationRefitCandidate
                {
                    Id          = c.PrefabId,
                    Kind        = c.Captured ? "scanned" : "vanilla",
                    Site        = c.Site ?? "",
                    BlockCount  = c.BlockCount,
                    Dps         = (float)c.Dps,
                    Cost        = c.Cost,
                    // -1 rather than 0 for a design with no recipe: the panel has to tell "nothing
                    // to pay" apart from "nothing can price this".
                    NetBomTotal = c.HasRecipe ? c.NetBomUnits : -1L,
                    Current     = c.Current,
                    NetBom      = BomRows(c, collecting ? CollectedOf(nodeId) : null),
                });
            }
            wire.Candidates = rows.ToArray();
            return wire;
        }

        /// <summary>The net bill as component rows. Only <c>Need</c> is meaningful on a design that
        /// is not being collected toward; <paramref name="collected"/> fills in <c>Have</c> for the
        /// one that is, so the panel can show a rebuild's progress per component rather than only as
        /// a total.</summary>
        private static WireBuildComponent[] BomRows(StationRefitCandidate candidate,
                                                    IReadOnlyDictionary<string, long> collected)
        {
            if (!candidate.HasRecipe || candidate.NetBom.Count == 0) return null;
            var rows = new List<WireBuildComponent>(candidate.NetBom.Count);
            foreach (KeyValuePair<string, long> kv in candidate.NetBom)
            {
                long have = 0;
                if (collected != null) collected.TryGetValue(kv.Key, out have);
                rows.Add(new WireBuildComponent { Key = kv.Key, Need = kv.Value, Have = have });
            }
            rows.Sort((a, b) => (b.Need - b.Have).CompareTo(a.Need - a.Have));
            return rows.ToArray();
        }

        /// <summary>What the refit entry at this node has absorbed so far, or null when nothing is
        /// collecting here.</summary>
        private IReadOnlyDictionary<string, long> CollectedOf(string nodeId)
        {
            SpatialNode node;
            if (_runtime == null || _runtime.World == null || string.IsNullOrEmpty(nodeId)
                || !_runtime.World.Graph.Nodes.TryGetValue(nodeId, out node) || node == null) return null;
            for (int i = 0; i < node.BuildQueue.Count; i++)
            {
                BuildQueueEntry entry = node.BuildQueue[i];
                if (ShipyardManager.IsStationRefit(entry)) return entry.Collected;
            }
            return null;
        }

        // ---- commands -----------------------------------------------------------------------------

        private void OnCommandRequest(ulong steamId, byte[] data)
        {
            string nodeId, prefabId, scope, name;
            WireStationRefitCommand command;
            long terminalEntityId;
            if (!FactionsWire.TryReadStationRefitCommandRequest(data, out nodeId, out command,
                    out prefabId, out terminalEntityId, out scope, out name))
            {
                FactionsPlugin.Log.Warn("Station refit command from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            string message;
            bool ok;
            try
            {
                // nodeId is by reference because a scan names a terminal rather than a node, and
                // the answer has to be pushed for whichever node that terminal turned out to be on.
                ok = Run(steamId, ref nodeId, command, prefabId, terminalEntityId, scope, name,
                         out message);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Station refit command failed for " + steamId + ".");
                ok = false;
                message = "The command failed on the server.";
            }

            _uplink.Send(steamId, FactionsWire.WriteStationRefitCommandResult(ok, message));

            // The panel is request/response, so it only learns what changed if the answer is pushed.
            // Harmless when it is showing another node or is not open at all: the client drops a
            // snapshot whose node id is not the one it asked about.
            if (!string.IsNullOrEmpty(nodeId)) SendSnapshot(steamId, nodeId);

            // Behind the same 'factions debug uplink' gate as every other line here. The mutation
            // itself is already in the Factions day log -- STATION_SCANNED, STATION_REFIT_QUEUED and
            // STATION_REFIT_APPLIED from FactionRuntime -- so what this adds is the SteamID behind
            // the click, which is diagnostic, not the record.
            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("STATION_REFIT_COMMAND steam=" + steamId + " node=" + nodeId
                                     + " command=" + command + " prefab=" + prefabId);
            }
        }

        /// <summary>
        /// Runs one command against the caller's own faction. The three node verbs re-resolve the
        /// faction from the SteamID and hand it, with the node id, to the same
        /// <see cref="FactionRuntime"/> entry point the chat verb calls; the scan re-resolves the
        /// terminal instead, because it names a block rather than a node.
        ///
        /// <para><paramref name="nodeId"/> is by reference because it is also the node the answer is
        /// pushed for: a scan carries no node and is rewritten to the one the terminal turned out to
        /// stand on, whose panel is the one that wants to see the new design.</para>
        /// </summary>
        private bool Run(ulong steamId, ref string nodeId, WireStationRefitCommand command, string prefabId,
                         long terminalEntityId, string scope, string name, out string message)
        {
            if (_runtime == null || _runtime.World == null)
            {
                message = "Factions runtime not ready.";
                return false;
            }

            if (command == WireStationRefitCommand.Scan)
                return TryScanStation(steamId, terminalEntityId, scope, name, ref nodeId, out message);

            long identityId = IdentityFor(steamId);
            if (identityId == 0L) { message = "Could not identify you on this server."; return false; }

            FactionState owner = ViewerFaction(identityId);
            if (owner == null) { message = "Your faction is not registered with Factions yet."; return false; }
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) { message = refusal; return false; }
            if (string.IsNullOrEmpty(nodeId)) { message = "No station selected."; return false; }

            switch (command)
            {
                case WireStationRefitCommand.Queue:
                    message = _runtime.QueueRefitForPlayer(owner, nodeId, prefabId, false);
                    // The runtime answers in prose whether it queued or refused, so a queued rebuild
                    // is recognised the way the chat verb's reader recognises it: by the sentence it
                    // starts with.
                    return Queued(message);
                case WireStationRefitCommand.Force:
                    message = _runtime.QueueRefitForPlayer(owner, nodeId, prefabId, true);
                    return Queued(message);
                case WireStationRefitCommand.Apply:
                    message = _runtime.ApplyRefitNowForPlayer(owner, nodeId);
                    return message != null && message.Contains("now stands as");
                default:
                    message = "No such command.";
                    return false;
            }
        }

        private static bool Queued(string reply)
        {
            return !string.IsNullOrEmpty(reply)
                   && (reply.StartsWith("Refit of ", StringComparison.Ordinal));
        }

        /// <summary>The chat verb's station scan with a terminal entity id in front of it instead of
        /// a raycast, and the terminal's own grid group instead of the one the player is looking at.
        /// The body — the static/three-block/site rules, the registration, the recipe and the
        /// <c>STATION_SCANNED</c> line — is
        /// <see cref="FactionRuntime.CaptureStationGridsForPlayer"/>, shared verbatim with
        /// <c>!factions station scan</c>.</summary>
        private bool TryScanStation(ulong steamId, long terminalEntityId, string scope, string name,
                                    ref string nodeId, out string message)
        {
            TerminalContext ctx;
            if (!TerminalAccess.TryAuthorise(_runtime, steamId, terminalEntityId, out ctx, out message))
                return false;

            nodeId = ctx.Node.Id;

            MyCubeGrid grid = ctx.Block.CubeGrid;
            if (grid == null || grid.MarkedForClose || grid.Closed)
            {
                message = "That station is no longer there.";
                return false;
            }

            List<MyCubeGrid> group = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid);
            if (group == null || group.Count == 0)
            {
                message = "That station is no longer there.";
                return false;
            }

            message = _runtime.CaptureStationGridsForPlayer(ctx.Faction, ctx.Player, group, scope, name);
            return !string.IsNullOrEmpty(message)
                   && (message.StartsWith(ScannedPrefix, StringComparison.Ordinal)
                       || message.StartsWith(FactionRuntime.PublishedReplyPrefix, StringComparison.Ordinal));
        }

        // ---- identity -----------------------------------------------------------------------------

        /// <summary>The Factions faction behind a player identity, or null when they are in no SE
        /// faction or in one that has claimed nothing yet. Mirrors
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
    }
}
