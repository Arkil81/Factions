using System;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace Factions
{
    /// <summary>
    /// Server side of the wing actions (docs/fleet-tactics.md, Package 2; docs/client-plugin.md,
    /// Part 18). Two doors into <see cref="FactionRuntime.IssueWing"/>:
    ///
    /// <list type="bullet">
    ///   <item><b>A client's toolbar press</b> arrives as <see cref="WireMessageKind.WingCommandRequest"/>.
    ///   The presser is the linked player; "my target" is their own lock, read here, else the grid
    ///   under their crosshair, which the client sent.</item>
    ///   <item><b>A block the server runs</b> — button panel, timer, event controller, programmable
    ///   block — activates the action here, through <see cref="WingAntennaActions"/>. There is no
    ///   presser: the commander is the antenna's owner, and "my target" is the antenna grid's seated
    ///   pilot's lock, a combat AI block's lock, or a cockpit's retained lock, in that order.</item>
    /// </list>
    ///
    /// <para><i>Protect me</i> from either door becomes a station's call
    /// (<see cref="FactionRuntime.CallStation"/>): a block's press is the station's own, a client's
    /// needs the presser to be in the station's faction.</para>
    ///
    /// <para>Registering the actions here is also what lets a toolbar slot that names one survive a
    /// save and reload with its action found: the server resolves slot ids against its own list.</para>
    ///
    /// <para>Granted on a standalone server and on the authority, and withheld from a relayed client
    /// (<see cref="UplinkManager.RemoteWithheldFeatures"/>): the antenna and the hulls are entities in
    /// the caller's own sector.</para>
    /// </summary>
    internal sealed class PlayerWingManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        public PlayerWingManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink != null) _uplink.RegisterHandler(WireMessageKind.WingCommandRequest, OnRequest);
            if (WingAntennaActions.Register(OnBlockPress))
                FactionsPlugin.Log.Info("Wing actions registered on antennas (" + WingActions.All.Length + ").");
            else
                FactionsPlugin.Log.Warn("Wing actions NOT registered on antennas: " + WingAntennaActions.FailureReason);
        }

        public void Close()
        {
            WingAntennaActions.Unregister();
        }

        private void OnRequest(ulong steamId, byte[] data)
        {
            long antennaId, crosshair;
            WireWingCommand command;
            if (!FactionsWire.TryReadWingCommandRequest(data, out antennaId, out command, out crosshair))
            {
                FactionsPlugin.Log.Warn("Wing command from " + steamId + " was unreadable.");
                return;
            }
            if ((_uplink.GrantedFeatures(steamId) & WireFeatures.WingCommand) == 0) return;

            string message;
            bool ok = false;
            MyPlayer player = WingWorld.PlayerOfSteam(steamId);
            if (_runtime == null || player == null)
                message = "Wing refused: the server could not identify you.";
            else
            {
                long identity = player.Identity.IdentityId;
                var req = new WingRequest
                {
                    Via = WingVia.Client,
                    Command = Map(command),
                    AntennaEntityId = antennaId,
                    CommanderIdentityId = identity,
                    PresserLock = WingWorld.PresserLock(identity),
                    Crosshair = WingWorld.GridOf(crosshair),
                };
                message = _runtime.IssueWing(req, out ok);
            }
            _uplink.Send(steamId, FactionsWire.WriteWingCommandResult(ok, command, message));
        }

        /// <summary>A block the server runs pressed a wing action.</summary>
        private void OnBlockPress(IMyTerminalBlock block, WireWingCommand command)
        {
            try
            {
                if (_runtime == null || block == null || !MyAPIGateway.Multiplayer.IsServer) return;
                long pilot, combat, retained;
                WingWorld.GridLocks(block.CubeGrid != null ? block.CubeGrid.EntityId : 0, out pilot, out combat, out retained);
                bool ok;
                _runtime.IssueWing(new WingRequest
                {
                    Via = WingVia.Block,
                    Command = Map(command),
                    AntennaEntityId = block.EntityId,
                    CommanderIdentityId = block.OwnerId,
                    PilotLock = pilot,
                    CombatBlockLock = combat,
                    CockpitRetained = retained,
                }, out ok);
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Wing action from block " + (block != null ? block.EntityId : 0) + " failed.");
            }
        }

        /// <summary>The wire's command onto Core's, by name.</summary>
        internal static WingCommand Map(WireWingCommand command)
        {
            switch (command)
            {
                case WireWingCommand.FormOnMe:    return WingCommand.FormOnMe;
                case WireWingCommand.FocusTarget: return WingCommand.FocusTarget;
                case WireWingCommand.Hold:        return WingCommand.Hold;
                case WireWingCommand.BreakOff:    return WingCommand.BreakOff;
                case WireWingCommand.Return:      return WingCommand.Return;
                case WireWingCommand.ProtectMe:   return WingCommand.ProtectMe;
                default:                          return WingCommand.Release;
            }
        }
    }
}
