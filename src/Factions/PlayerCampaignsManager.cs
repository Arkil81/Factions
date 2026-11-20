using System;
using System.Collections.Generic;
using System.Text;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the campaign surfaces: the viewing player's own faction's campaign objectives
    /// (the directives they placed and, for an NPC-registered faction, the ones its brain nominated),
    /// and the two commands a player may send — place a raid or conquest directive on a node, and
    /// cancel one of their own. A linked client subscribes while the map or the list window is
    /// open and unsubscribes when both close, so a player who never opens either costs nothing.
    ///
    /// <para>Sends on change rather than on a cadence: a snapshot goes out when the list's
    /// signature — focus ids, expiries, last-dispatch stamps and candidate counts — differs from
    /// the one last sent to that client. The offensive block stamps <c>LastDispatchUtc</c> on every
    /// dispatch, so a subscriber sees each strike land without the window polling.</para>
    ///
    /// <para>Only the player's OWN faction is ever described, the rule
    /// <see cref="PlayerStandingOrdersManager"/> sets. A campaign belongs to exactly one faction and
    /// nobody else may see or touch it.</para>
    /// </summary>
    internal sealed class PlayerCampaignsManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;
        private readonly PlayerScannerManager _scanner;

        /// <summary>SteamIDs with a campaign surface open.</summary>
        private readonly HashSet<ulong> _streaming = new HashSet<ulong>();

        /// <summary>Signature of the list last sent to each subscriber. A subscriber with no entry
        /// is owed a snapshot immediately.</summary>
        private readonly Dictionary<ulong, string> _sentSignature = new Dictionary<ulong, string>();

        private readonly List<FactionKnowledge> _viewerSources = new List<FactionKnowledge>(2);

        public PlayerCampaignsManager(FactionRuntime runtime, UplinkManager uplink, PlayerScannerManager scanner)
        {
            _runtime = runtime;
            _uplink = uplink;
            _scanner = scanner;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.CampaignsRequest, OnCampaignsRequest);
            _uplink.RegisterHandler(WireMessageKind.CampaignCommandRequest, OnCommandRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnCampaignsRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadCampaignsRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Campaigns request from " + steamId + " was unreadable.");
                return;
            }
            if (stream)
            {
                _streaming.Add(steamId);
                _sentSignature.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("CAMPAIGNS_SUBSCRIBE steam=" + steamId);
            }
            else
            {
                _streaming.Remove(steamId);
                _sentSignature.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("CAMPAIGNS_UNSUBSCRIBE steam=" + steamId);
            }
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

                FactionState owner = ViewerFaction(player.IdentityId);
                List<WireCampaign> wire = Describe(owner);
                string signature = Signature(owner, wire);

                string sent;
                if (_sentSignature.TryGetValue(steamId, out sent) && sent == signature) continue;
                _sentSignature[steamId] = signature;
                SendSnapshot(steamId, owner, wire);
            }
        }

        private void SendSnapshot(ulong steamId, FactionState owner, List<WireCampaign> wire)
        {
            FactionTuning tuning = _runtime.Tuning;
            int cap = tuning != null ? tuning.PlayerCampaignCap : 0;
            long raidCost = tuning != null ? tuning.RaidCost : 0;
            long conquerCost = tuning != null ? tuning.ConquerCost : 0;
            _uplink.Send(steamId, FactionsWire.WriteCampaignsSnapshot(
                owner != null ? owner.Tag : "", cap, raidCost, conquerCost, wire));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("CAMPAIGNS_SNAPSHOT steam=" + steamId + " campaigns=" + wire.Count);
        }

        /// <summary>Every offensive objective the faction carries, player-issued or not. An NPC
        /// nomination on a player faction is a leftover the offensive block ignores, but the
        /// window shows it so the player can see why a marker is there.</summary>
        private List<WireCampaign> Describe(FactionState owner)
        {
            var wire = new List<WireCampaign>();
            if (owner == null) return wire;
            foreach (OffensiveCampaigns.Entry entry in OffensiveCampaigns.Active(owner))
            {
                CampaignFocus focus = entry.Focus;
                CampaignObjective objective = entry.Objective;
                NodeRegion region = NodeRegion.FromObjective(objective);
                wire.Add(new WireCampaign
                {
                    FocusId = focus.Id ?? "",
                    Kind = (byte)(objective.Kind == CampaignObjectiveKind.SeizeRegion ? 1 : 0),
                    Cause = (byte)objective.Cause,
                    TargetNodeId = objective.TargetNodeId ?? "",
                    TargetNodeName = NodeDisplayName(objective.TargetNodeId),
                    RegionKey = region.IsNone ? "" : region.ToKey(),
                    StagingNodeId = focus.CenterNodeId ?? "",
                    ExpiresUtcTicks = focus.ExpiresAtUtc.Ticks,
                    LastDispatchUtcTicks = objective.LastDispatchUtc == DateTime.MinValue ? 0L : objective.LastDispatchUtc.Ticks,
                    CandidateCount = objective.Candidates.Count,
                    PlayerIssued = focus.PlayerIssued,
                    LastOutcome = (byte)objective.LastOutcome,
                    LastOutcomeUtcTicks = objective.LastOutcomeUtc == DateTime.MinValue ? 0L : objective.LastOutcomeUtc.Ticks,
                    LastOutcomeRangeM = (int)objective.LastOutcomeRangeM,
                    LastOutcomeOwnDps = (int)objective.LastOutcomeOwnDps,
                    LastOutcomeOpposedDps = (int)objective.LastOutcomeOpposedDps,
                    LastOutcomeHullsLost = objective.LastOutcomeHullsLost,
                    LastOutcomeHullsTotal = objective.LastOutcomeHullsTotal,
                });
            }
            // The player's own defensive foci (FAC-309 slice 5), so the map can mark the centre and
            // offer Cancel, and the list window shows the order beside the offensive ones. The
            // target is the centre; "candidates" is the circuit's membership.
            foreach (CampaignFocus focus in owner.CampaignFoci)
            {
                if (focus == null || !focus.PlayerIssued || !CampaignFocusLifecycle.HasDefendRegionObjective(focus)) continue;
                CampaignObjective objective = null;
                foreach (CampaignObjective o in focus.Objectives)
                    if (o != null && o.Kind == CampaignObjectiveKind.DefendRegion) { objective = o; break; }
                wire.Add(new WireCampaign
                {
                    FocusId = focus.Id ?? "",
                    Kind = 2,
                    Cause = (byte)CampaignCause.Directive,
                    TargetNodeId = focus.CenterNodeId ?? "",
                    TargetNodeName = NodeDisplayName(focus.CenterNodeId),
                    RegionKey = "",
                    StagingNodeId = focus.CenterNodeId ?? "",
                    ExpiresUtcTicks = focus.ExpiresAtUtc.Ticks,
                    LastDispatchUtcTicks = objective == null || objective.LastDispatchUtc == DateTime.MinValue ? 0L : objective.LastDispatchUtc.Ticks,
                    CandidateCount = focus.RegionMembers.Count,
                    PlayerIssued = true,
                });
            }
            return wire;
        }

        private static string Signature(FactionState owner, List<WireCampaign> wire)
        {
            var sb = new StringBuilder();
            sb.Append(owner != null ? owner.Tag : "-");
            for (int i = 0; i < wire.Count; i++)
            {
                WireCampaign c = wire[i];
                sb.Append('|').Append(c.FocusId).Append(':').Append(c.ExpiresUtcTicks)
                  .Append(':').Append(c.LastDispatchUtcTicks).Append(':').Append(c.CandidateCount)
                  .Append(':').Append(c.LastOutcome).Append(':').Append(c.LastOutcomeUtcTicks);
            }
            return sb.ToString();
        }

        private string NodeDisplayName(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || _runtime.World == null) return "";
            SpatialNode node;
            if (!_runtime.World.Graph.Nodes.TryGetValue(nodeId, out node) || node == null) return "";
            return node.DisplayName ?? "";
        }

        // ---- Commands -------------------------------------------------------------------------

        private void OnCommandRequest(ulong steamId, byte[] data)
        {
            WireCampaignCommand command;
            string nodeId, focusId;
            byte kind;
            bool regionScope;
            if (!FactionsWire.TryReadCampaignCommandRequest(data, out command, out nodeId, out kind,
                    out regionScope, out focusId))
            {
                FactionsPlugin.Log.Warn("Campaign command from " + steamId + " was unreadable.");
                return;
            }

            string resultFocusId;
            bool ok;
            string message = Run(steamId, command, nodeId, kind, regionScope, focusId, out ok, out resultFocusId);
            _uplink.Send(steamId, FactionsWire.WriteCampaignCommandResult(ok, message, resultFocusId));
            // The mutation moved the signature; the next Tick sends the new list.
            _sentSignature.Remove(steamId);
            Tick();
            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("CAMPAIGN_COMMAND steam=" + steamId + " command=" + command
                                     + " node=" + nodeId + " kind=" + kind + " region=" + regionScope
                                     + " focus=" + focusId + " ok=" + ok);
            }
        }

        /// <summary>Runs one command against the caller's own faction. The faction is re-resolved
        /// from the SteamID and the node id is handed to a <see cref="FactionRuntime"/> entry point
        /// that applies every refusal rule itself; nothing in the request is trusted.</summary>
        private string Run(ulong steamId, WireCampaignCommand command, string nodeId, byte kind,
            bool regionScope, string focusId, out bool ok, out string resultFocusId)
        {
            ok = false;
            resultFocusId = "";
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";
            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";
            FactionState owner = ViewerFaction(identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) return refusal;

            switch (command)
            {
                case WireCampaignCommand.Place:
                {
                    CampaignObjectiveKind objectiveKind = kind == 1 ? CampaignObjectiveKind.SeizeRegion
                        : kind == 2 ? CampaignObjectiveKind.DefendRegion : CampaignObjectiveKind.Terrorise;
                    BuildViewerSources(identityId, owner);
                    CampaignFocus placed;
                    string message = _runtime.PlacePlayerCampaign(owner, nodeId, objectiveKind, regionScope,
                        _viewerSources, out placed);
                    ok = placed != null;
                    resultFocusId = placed != null ? placed.Id : "";
                    return message;
                }
                case WireCampaignCommand.Cancel:
                {
                    CampaignFocus removed;
                    string message = _runtime.CancelPlayerCampaign(owner, focusId, out removed);
                    ok = removed != null;
                    resultFocusId = removed != null ? removed.Id : "";
                    return message;
                }
                default:
                    return "No such command.";
            }
        }

        /// <summary>The viewer's knowledge, as the map builds it: their own scanner store plus their
        /// faction's. A node the map would not draw cannot be named.</summary>
        private void BuildViewerSources(long identityId, FactionState owner)
        {
            _viewerSources.Clear();
            if (_scanner != null) _viewerSources.Add(_scanner.KnowledgeFor(identityId));
            if (owner != null && owner.Knowledge != null) _viewerSources.Add(owner.Knowledge);
        }

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
                _sentSignature.Remove(steamId);
            }
        }

        public void Close()
        {
            _streaming.Clear();
            _sentSignature.Clear();
        }
    }
}
