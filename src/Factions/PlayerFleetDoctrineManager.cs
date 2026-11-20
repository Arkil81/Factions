using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the Fleet window: the viewing player's own faction doctrine
    /// (<see cref="FactionState.FleetDoctrine"/>), the work waiting on hulls it does not answer, the
    /// hulls the faction actually owns, and the actions that edit a line the window is showing. A
    /// linked client subscribes when the window opens and unsubscribes when it closes, so a player
    /// who never opens it costs nothing.
    ///
    /// <para><b>Sent on a cadence, not only on a revision</b> — the one way this differs from
    /// <see cref="PlayerStandingOrdersManager"/>, which it is otherwise a clone of. A standing-order
    /// list only changes when a player changes it, so a revision is the whole story there. A
    /// doctrine line's numbers move without the list moving at all: <c>Have</c> falls when a hull is
    /// dispatched, <c>Flying</c> rises with it, <c>Building</c> changes when the yard takes the
    /// build, and <c>Pressure</c> appears when a haul blocks. A revision-gated send would leave the
    /// window frozen on the counts as they stood at the player's last edit, which is exactly the
    /// state that looks like a working window.</para>
    ///
    /// <para>The cadence is <see cref="Tick"/>'s own — the ~5s scanner cadence
    /// <c>FactionsPlugin</c> calls every player manager on — and the send is unconditional, the same
    /// rule <see cref="PlayerNodeDetailManager"/> uses for the same reason it gives: a window a
    /// player is reading should keep up with the numbers changing in it. <see cref="FactionState.FleetDoctrineRevision"/>
    /// is still sent, so the window can tell an edit apart from a count moving, and a command still
    /// forces the next snapshot immediately rather than waiting out the cadence.</para>
    ///
    /// <para>Only the player's OWN faction is ever described, exactly as the standing-order manager
    /// does: a doctrine belongs to one faction and nobody else may see or touch it.</para>
    /// </summary>
    internal sealed class PlayerFleetDoctrineManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs with the window open.</summary>
        private readonly HashSet<ulong> _streaming = new HashSet<ulong>();

        public PlayerFleetDoctrineManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.FleetDoctrineRequest, OnDoctrineRequest);
            _uplink.RegisterHandler(WireMessageKind.FleetDoctrineCommandRequest, OnCommandRequest);
            _uplink.RegisterHandler(WireMessageKind.FleetDoctrineCreateRequest, OnCreateRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnDoctrineRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadFleetDoctrineRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Fleet doctrine request from " + steamId + " was unreadable.");
                return;
            }

            if (stream)
            {
                _streaming.Add(steamId);
                // Once per subscribe, not per snapshot: the pick-lists are the largest payload of
                // this feature and the one part that does not move when a player edits their
                // doctrine — or when the counts beside it do.
                SendOptions(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("DOCTRINE_SUBSCRIBE steam=" + steamId);
                Tick();
            }
            else
            {
                _streaming.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("DOCTRINE_UNSUBSCRIBE steam=" + steamId);
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

                SendSnapshot(steamId, ViewerFaction(player.IdentityId));
            }
        }

        /// <summary>One snapshot: every line with its live counts, the pressure no line answers, and
        /// the census. Assembled in Core by <see cref="FleetDoctrineReport"/> and mapped here — this
        /// method adds display names and nothing else, so the window and the evaluator cannot
        /// disagree about a count.</summary>
        private void SendSnapshot(ulong steamId, FactionState owner)
        {
            var lines = new List<WireDoctrineLine>();
            var unlined = new List<WireDoctrinePressureRow>();
            var census = new List<WireFleetCensusRow>();
            var refits = new List<WireHullRefitRow>();
            int revision = 0;

            if (owner != null)
            {
                revision = owner.FleetDoctrineRevision;
                FleetDoctrineReport report = FleetDoctrineReport.Build(_runtime.World, owner);

                foreach (DoctrineLineReport row in report.Lines)
                {
                    DoctrineLine line = row.Line;
                    lines.Add(new WireDoctrineLine
                    {
                        Id = line.Id ?? "",
                        Priority = line.Priority,
                        Paused = line.Paused,
                        Fits = string.Join("/", row.Fits),
                        PrefabId = line.PrefabId ?? "",
                        TargetCount = line.TargetCount,
                        MaxConcurrent = line.MaxConcurrent,
                        ScopeNodeId = line.ScopeNodeId ?? "",
                        ScopeNodeName = NodeDisplayName(line.ScopeNodeId),
                        Have = row.Have,
                        Flying = row.Flying,
                        Building = row.Building,
                        Pressure = row.Pressure,
                        RouteBlocked = row.RouteBlocked,
                        OverCeiling = row.OverCeiling,
                        Banned = row.Banned,
                        RepairWaiting = row.RepairWaiting,
                        Repairing = row.Repairing,
                    });
                }

                foreach (DoctrinePressureRow row in report.Unlined)
                {
                    unlined.Add(new WireDoctrinePressureRow
                    {
                        Role = row.Role ?? "",
                        NodeId = row.NodeId ?? "",
                        NodeName = NodeDisplayName(row.NodeId),
                        Demands = row.Demands,
                    });
                }

                foreach (FleetCensusRow row in report.Census)
                {
                    census.Add(new WireFleetCensusRow
                    {
                        PrefabId = row.PrefabId ?? "",
                        Parked = row.Parked,
                        Flying = row.Flying,
                        RepairWaiting = row.RepairWaiting,
                        Repairing = row.Repairing,
                    });
                }
            }

            if (owner != null)
                foreach (HullRefit.Row row in HullRefit.Rows(_runtime.World, owner))
                {
                    refits.Add(new WireHullRefitRow
                    {
                        Family = row.Family ?? "",
                        Latest = row.Latest ?? "",
                        Cap = row.Cap,
                        InRefit = row.InRefit,
                        OutdatedParked = row.OutdatedParked,
                        OutdatedAtYards = row.OutdatedAtYards,
                        Blocked = row.Blocked ?? "",
                    });
                }

            int cap = _runtime.Tuning != null ? _runtime.Tuning.FleetDoctrineCap : 0;
            _uplink.Send(steamId, FactionsWire.WriteFleetDoctrineSnapshot(
                owner != null ? owner.Tag : "", cap, revision, lines, unlined, census, refits));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("DOCTRINE_SNAPSHOT steam=" + steamId + " lines=" + lines.Count
                                     + " unlined=" + unlined.Count + " census=" + census.Count);
        }

        /// <summary>Empty for a node that no longer exists or was never named — the id still goes
        /// out, so a lost scope reads as an id rather than as nowhere.</summary>
        private string NodeDisplayName(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || _runtime.World == null) return "";
            SpatialNode node;
            if (!_runtime.World.Graph.Nodes.TryGetValue(nodeId, out node) || node == null) return "";
            return node.DisplayName ?? "";
        }

        /// <summary>Sends the new-line dialog's pick-lists: the build roles, the owned yards a line
        /// may be scoped to, and the hull list with the numbers a player chooses on. Every one is
        /// resolved server-side — the client has no graph, no catalogue and no recipes, and a dialog
        /// that offered a hull the server would refuse is worse than one that offers fewer.</summary>
        private void SendOptions(ulong steamId)
        {
            if (_runtime == null || _runtime.World == null) return;

            long identityId = IdentityFor(steamId);
            FactionState owner = identityId != 0L ? ViewerFaction(identityId) : null;

            var roles = new List<string>(ShipyardManager.PlayerQueueRoles);
            var nodes = new List<WireOrderOption>();
            var prefabs = new List<WireBuildPrefab>();

            if (owner != null)
            {
                foreach (SpatialNode node in _runtime.DoctrineScopeNodeOptions(owner))
                {
                    nodes.Add(new WireOrderOption
                    {
                        Id = node.Id,
                        Label = node.DisplayName ?? "",
                        Flags = (int)WireNodeOptionFlags.Shipyard,
                    });
                }
                foreach (BuildPrefabOption option in _runtime.DoctrineHullOptions(owner))
                    prefabs.Add(Describe(option));
            }

            _uplink.Send(steamId, FactionsWire.WriteFleetDoctrineOptions(roles, nodes, prefabs));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("DOCTRINE_OPTIONS steam=" + steamId + " roles=" + roles.Count
                                     + " nodes=" + nodes.Count + " prefabs=" + prefabs.Count);
        }

        /// <summary>Core's row onto the wire row. The same mapping
        /// <see cref="PlayerNodeDetailManager"/> makes for the Build dialog — one struct, one
        /// meaning, so a hull reads identically in both dialogs.</summary>
        private static WireBuildPrefab Describe(BuildPrefabOption option)
        {
            return new WireBuildPrefab
            {
                PrefabId = option.PrefabId ?? "",
                Label = option.Label ?? "",
                Roles = option.Roles.ToArray(),
                CargoLiters = option.CargoLiters,
                Dps = option.Dps,
                GridSize = option.GridSize ?? "",
                Propulsion = option.Propulsion ?? "",
                DrillYieldScore = option.DrillYieldScore,
                BomUnits = option.BomUnits,
                CanFlyFromHere = option.CanFlyFromHere,
            };
        }

        // ---- Commands -------------------------------------------------------------------------

        private void OnCommandRequest(ulong steamId, byte[] data)
        {
            string lineId;
            WireDoctrineCommand command;
            int value;
            if (!FactionsWire.TryReadFleetDoctrineCommandRequest(data, out lineId, out command, out value))
            {
                FactionsPlugin.Log.Warn("Fleet doctrine command from " + steamId + " was unreadable.");
                return;
            }

            string message = Run(steamId, lineId, command, value);
            // The runtime's player-facing entry points answer in prose whether they succeeded or
            // refused, so "ok" here means the command was dispatched at all, not that it changed
            // anything. The snapshot that follows is the authoritative answer either way.
            _uplink.Send(steamId, FactionsWire.WriteFleetDoctrineCommandResult(
                message != null, message ?? "No such command."));

            // Without waiting out the cadence: the player clicked and is looking at the row.
            Tick();

            // Behind the same 'factions debug uplink' gate as every other line here. The mutation is
            // already recorded in the Factions day log by the code that performs it
            // (DOCTRINE_LINE_*_BY_PLAYER from FactionRuntime), so what this adds is the SteamID
            // behind the click — diagnostic, not the record. The chat verb has the same gap.
            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("DOCTRINE_COMMAND steam=" + steamId + " line=" + lineId
                                     + " command=" + command + " value=" + value);
            }
        }

        /// <summary>
        /// Runs one command against the caller's own faction. Every path re-resolves the faction
        /// from the SteamID and hands the line id to a <see cref="FactionRuntime"/> entry point that
        /// resolves it within that faction, so a client naming a line belonging to someone else is
        /// refused by the same code that refuses the chat verb.
        ///
        /// <para>The two priority commands are relative and the step is taken HERE, off the line's
        /// current rank, rather than sent as an absolute rank the client computed from a row that
        /// may since have moved.</para>
        /// </summary>
        private string Run(ulong steamId, string lineId, WireDoctrineCommand command, int value)
        {
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";
            if (string.IsNullOrEmpty(lineId)) return "No doctrine line selected.";

            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";

            FactionState owner = ViewerFaction(identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) return refusal;

            switch (command)
            {
                case WireDoctrineCommand.Remove:
                    return _runtime.RemoveDoctrineLineForPlayer(owner, lineId);
                case WireDoctrineCommand.PriorityUp:
                    return SetPriorityDelta(owner, lineId, 1);
                case WireDoctrineCommand.PriorityDown:
                    return SetPriorityDelta(owner, lineId, -1);
                case WireDoctrineCommand.Pause:
                    return _runtime.SetDoctrineLinePausedForPlayer(owner, lineId, true);
                case WireDoctrineCommand.Resume:
                    return _runtime.SetDoctrineLinePausedForPlayer(owner, lineId, false);
                case WireDoctrineCommand.SetTarget:
                    return _runtime.SetDoctrineLineTargetForPlayer(owner, lineId, value);
                case WireDoctrineCommand.SetConcurrency:
                    return _runtime.SetDoctrineLineConcurrencyForPlayer(owner, lineId, value);
                case WireDoctrineCommand.SetRefitCap:
                    // The id is a design family here, not a line; the runtime resolves it within the
                    // caller's faction exactly as the chat verb does.
                    return _runtime.SetHullRefitCapForPlayer(owner, lineId, value);
                case WireDoctrineCommand.Decommission:
                    // The id is a design here too. The window confirmed before sending.
                    return _runtime.DecommissionForPlayer(owner, lineId, value, apply: true);
                default:
                    return null;
            }
        }

        /// <summary>Steps a line's rank by one. The line is resolved through the faction's own
        /// lookup, so a line id that is not theirs falls through to the runtime's refusal rather
        /// than being read here.</summary>
        private string SetPriorityDelta(FactionState owner, string lineId, int delta)
        {
            DoctrineLine line = owner.FindDoctrineLine(lineId);
            if (line == null) return _runtime.SetDoctrineLinePriorityForPlayer(owner, lineId, 0);
            return _runtime.SetDoctrineLinePriorityForPlayer(owner, lineId, line.Priority + delta);
        }

        private void OnCreateRequest(ulong steamId, byte[] data)
        {
            string prefabId, scopeNodeId;
            int targetCount, maxConcurrent;
            if (!FactionsWire.TryReadFleetDoctrineCreateRequest(data, out prefabId,
                    out targetCount, out maxConcurrent, out scopeNodeId))
            {
                FactionsPlugin.Log.Warn("Fleet doctrine create from " + steamId + " was unreadable.");
                return;
            }

            string message = RunCreate(steamId, prefabId, targetCount, maxConcurrent, scopeNodeId);
            _uplink.Send(steamId, FactionsWire.WriteFleetDoctrineCommandResult(
                message != null, message ?? "No such command."));

            Tick();

            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("DOCTRINE_CREATE steam=" + steamId
                                     + " prefab=" + prefabId + " target=" + targetCount
                                     + " concurrent=" + maxConcurrent
                                     + " scope=" + (string.IsNullOrEmpty(scopeNodeId) ? "any" : scopeNodeId));
            }
        }

        /// <summary>Creates one line through the same <see cref="FactionRuntime"/> entry point the
        /// chat verb calls, so every validation the verb applies — the hull fitting some build role,
        /// a real build recipe, the scope node's ownership and Shipyard, the list cap —
        /// applies identically here. Nothing the client sent is trusted; the pick-lists this dialog
        /// was built from are a convenience, not an authorisation.</summary>
        private string RunCreate(ulong steamId, string prefabId, int targetCount,
            int maxConcurrent, string scopeNodeId)
        {
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";

            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";

            FactionState owner = ViewerFaction(identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) return refusal;

            return _runtime.AddDoctrineLineForPlayer(owner, prefabId, targetCount, maxConcurrent,
                string.IsNullOrEmpty(scopeNodeId) ? null : scopeNodeId);
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
            foreach (ulong steamId in stale) _streaming.Remove(steamId);
        }

        public void Close()
        {
            _streaming.Clear();
        }
    }
}
