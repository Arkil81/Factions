using System;
using Factions.Wire;
using NLog;

namespace Factions
{
    /// <summary>
    /// A Nexus agent's half of the Services Terminal surfaces. The terminal block, its station grid
    /// and the player standing at it are this server's, so the gates run here
    /// (<see cref="TerminalAccess"/>, against <see cref="FactionsPlugin.AgentTerminalWorld"/>) and
    /// only what changes the authority's records goes on to it.
    ///
    /// <para><b>Terminal assets</b> are answered by an agent <see cref="PlayerShipAssetsManager"/>
    /// over the snapshot's hull stock.</para>
    ///
    /// <para><b>Station refit.</b> The node actions -- queue, force, apply -- name a node, not a
    /// block, and go to the authority unchanged, where the caller is resolved through
    /// <see cref="LinkedViewers"/>. The scan reads the station grid, which stands here, and goes
    /// to the authority as a capture (<see cref="AgentCaptures"/>), which answers it.</para>
    /// </summary>
    internal sealed class AgentTerminalSurfaces
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);
        internal const string ScanNotCarried = "Scanning a station is not available on this server yet.";

        private readonly FactionsPlugin _plugin;
        private readonly UplinkManager _uplink;

        public AgentTerminalSurfaces(FactionsPlugin plugin, UplinkManager uplink)
        {
            _plugin = plugin;
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.StationRefitCommandRequest, OnRefitCommand);
        }

        private void OnRefitCommand(ulong steamId, byte[] data)
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
            if (command != WireStationRefitCommand.Scan)
            {
                _uplink.ForwardClientMessage(steamId, data, WireMessageKind.StationRefitCommandRequest.ToString());
                return;
            }
            AgentCaptures captures = _plugin.AgentCaptures;
            string message = ScanNotCarried;
            if (captures != null && captures.StationScan(steamId, terminalEntityId, scope, name, out message)) return;
            _uplink.Send(steamId, FactionsWire.WriteStationRefitCommandResult(false, message ?? ""));
        }
    }
}
