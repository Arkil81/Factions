using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the Store window: what the viewing player's own faction sells
    /// (<see cref="FactionState.Shelf"/>), what is on sale right now, and the actions that edit a
    /// line the window is showing. A linked client subscribes when the window opens and unsubscribes
    /// when it closes, so a player who never opens it costs nothing.
    ///
    /// <para><b>Sent on the scanner cadence, not on a revision</b>, for the reason
    /// <see cref="PlayerFleetDoctrineManager"/> is: a shelf line's numbers move without the list
    /// moving at all. <c>OnSale</c> falls the moment a customer buys, <c>Building</c> changes when a
    /// yard takes the build, and the census gains a row whenever the release pass hands one back. A
    /// revision-gated send would leave the window frozen on the counts as they stood at the player's
    /// last edit, which is exactly the state that looks like a working window.</para>
    ///
    /// <para><b>No options message.</b> Unlike the doctrine, a shelf line names no role and no scope
    /// node, so the only pick-list it needs is the hull list <see cref="WireMessageKind.FleetDoctrineOptions"/>
    /// already carries — the largest payload in the protocol, and one the Store window reads rather
    /// than asking for a second copy of.</para>
    ///
    /// <para>Only the player's OWN faction is ever described: a shelf belongs to one faction and
    /// nobody else may see or touch it.</para>
    /// </summary>
    internal sealed class PlayerShelfManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs with the window open.</summary>
        private readonly HashSet<ulong> _streaming = new HashSet<ulong>();

        public PlayerShelfManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.PlayerShelfRequest, OnShelfRequest);
            _uplink.RegisterHandler(WireMessageKind.PlayerShelfCommandRequest, OnCommandRequest);
            _uplink.RegisterHandler(WireMessageKind.PlayerShelfCreateRequest, OnCreateRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnShelfRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadPlayerShelfRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Player shelf request from " + steamId + " was unreadable.");
                return;
            }

            if (stream)
            {
                _streaming.Add(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("SHELF_SUBSCRIBE steam=" + steamId);
                Tick();
            }
            else
            {
                _streaming.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("SHELF_UNSUBSCRIBE steam=" + steamId);
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

        /// <summary>One snapshot: every line with its live counts, and every hull on sale. Assembled
        /// in Core by <see cref="PlayerShelfReport"/> and mapped here — this method adds nothing, so
        /// the window and the store manager cannot disagree about a count.</summary>
        private void SendSnapshot(ulong steamId, FactionState owner)
        {
            var lines = new List<WireShelfLine>();
            var census = new List<WireShelfCensusRow>();
            bool stockingOff = false;

            if (owner != null)
            {
                PlayerShelfReport report = PlayerShelfReport.Build(_runtime.World, owner);
                stockingOff = report.StockingOff;

                foreach (ShelfLineReport row in report.Lines)
                {
                    ShelfLine line = row.Line;
                    lines.Add(new WireShelfLine
                    {
                        Id = line.Id ?? "",
                        Paused = line.Paused,
                        PrefabId = line.PrefabId ?? "",
                        TargetCount = line.TargetCount,
                        OnSale = row.OnSale,
                        Building = row.Building,
                        Role = row.Role ?? "",
                        BlockedReason = row.BlockedReason ?? "",
                    });
                }

                foreach (ShelfCensusRow row in report.Census)
                {
                    census.Add(new WireShelfCensusRow
                    {
                        PrefabId = row.PrefabId ?? "",
                        OnSale = row.OnSale,
                        Unlined = row.Unlined,
                    });
                }
            }

            int cap = _runtime.Tuning != null ? _runtime.Tuning.PlayerShelfCap : 0;
            _uplink.Send(steamId, FactionsWire.WritePlayerShelfSnapshot(
                owner != null ? owner.Tag : "", cap, stockingOff, lines, census));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("SHELF_SNAPSHOT steam=" + steamId + " lines=" + lines.Count
                                     + " census=" + census.Count + " off=" + stockingOff);
        }

        // ---- Commands -------------------------------------------------------------------------

        private void OnCommandRequest(ulong steamId, byte[] data)
        {
            string lineId;
            WireShelfCommand command;
            int value;
            if (!FactionsWire.TryReadPlayerShelfCommandRequest(data, out lineId, out command, out value))
            {
                FactionsPlugin.Log.Warn("Player shelf command from " + steamId + " was unreadable.");
                return;
            }

            string message = Run(steamId, lineId, command, value);
            // The runtime's player-facing entry points answer in prose whether they succeeded or
            // refused, so "ok" here means the command was dispatched at all, not that it changed
            // anything. The snapshot that follows is the authoritative answer either way.
            _uplink.Send(steamId, FactionsWire.WritePlayerShelfCommandResult(
                message != null, message ?? "No such command."));

            // Without waiting out the cadence: the player clicked and is looking at the row.
            Tick();

            // Behind the same 'factions debug uplink' gate as every other line here. The mutation is
            // already recorded in the Factions day log by the code that performs it
            // (SHELF_LINE_*_BY_PLAYER from FactionRuntime), so what this adds is the SteamID behind
            // the click — diagnostic, not the record.
            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("SHELF_COMMAND steam=" + steamId + " line=" + lineId
                                     + " command=" + command + " value=" + value);
            }
        }

        /// <summary>
        /// Runs one command against the caller's own faction. Every path re-resolves the faction from
        /// the SteamID and hands the line id to a <see cref="FactionRuntime"/> entry point that
        /// resolves it within that faction, so a client naming a line belonging to someone else is
        /// refused by the same code that refuses the chat verb.
        ///
        /// <para>The two stocking commands carry no line id and ignore whichever one was sent: they
        /// are shop-wide, and a player switches restocking off with no row selected.</para>
        /// </summary>
        private string Run(ulong steamId, string lineId, WireShelfCommand command, int value)
        {
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";

            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";

            FactionState owner = ViewerFaction(identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) return refusal;

            if (command == WireShelfCommand.StockingOff) return _runtime.SetStoreShelfOptOutForPlayer(owner, true);
            if (command == WireShelfCommand.StockingOn)  return _runtime.SetStoreShelfOptOutForPlayer(owner, false);

            if (string.IsNullOrEmpty(lineId)) return "No shelf line selected.";

            switch (command)
            {
                case WireShelfCommand.Remove:
                    return _runtime.RemoveShelfLineForPlayer(owner, lineId);
                case WireShelfCommand.Pause:
                    return _runtime.SetShelfLinePausedForPlayer(owner, lineId, true);
                case WireShelfCommand.Resume:
                    return _runtime.SetShelfLinePausedForPlayer(owner, lineId, false);
                case WireShelfCommand.SetTarget:
                    return _runtime.SetShelfLineTargetForPlayer(owner, lineId, value);
                default:
                    return null;
            }
        }

        private void OnCreateRequest(ulong steamId, byte[] data)
        {
            string prefabId;
            int targetCount;
            if (!FactionsWire.TryReadPlayerShelfCreateRequest(data, out prefabId, out targetCount))
            {
                FactionsPlugin.Log.Warn("Player shelf create from " + steamId + " was unreadable.");
                return;
            }

            string message = RunCreate(steamId, prefabId, targetCount);
            _uplink.Send(steamId, FactionsWire.WritePlayerShelfCommandResult(
                message != null, message ?? "Could not add that line."));
            Tick();

            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("SHELF_CREATE steam=" + steamId + " prefab=" + prefabId
                                     + " target=" + targetCount);
            }
        }

        private string RunCreate(ulong steamId, string prefabId, int targetCount)
        {
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";

            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";

            FactionState owner = ViewerFaction(identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) return refusal;

            return _runtime.AddShelfLineForPlayer(owner, prefabId, targetCount);
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
