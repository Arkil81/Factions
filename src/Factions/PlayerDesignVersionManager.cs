using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using NLog;
using Sandbox.Game.Entities;

namespace Factions
{
    /// <summary>
    /// Server side of the scan dialogs' "Publish as" choice: which design a selected hull, or the
    /// station a Services Terminal stands on, is, and the version scanning it would publish
    /// (<see cref="FactionRuntime.PreviewVersionForPlayer"/>). Read-only — the scan itself is still
    /// the ship-assets or station-refit request, judged afresh when it arrives.
    ///
    /// <para>Every query is authorised against its terminal through the owner's gate the scans
    /// themselves use (<see cref="TerminalAccess.TryAuthorise"/>), so nothing is described for a
    /// terminal or a station the caller could not scan.</para>
    /// </summary>
    internal sealed class PlayerDesignVersionManager
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        public PlayerDesignVersionManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.DesignVersionQuery, OnQuery);
        }

        private bool Granted(ulong steamId)
        {
            return _uplink != null && (_uplink.GrantedFeatures(steamId) & WireFeatures.DesignVersions) != 0;
        }

        private void OnQuery(ulong steamId, byte[] data)
        {
            long terminalEntityId, gridEntityId;
            bool station;
            if (!FactionsWire.TryReadDesignVersionQuery(data, out terminalEntityId, out gridEntityId, out station))
            {
                FactionsPlugin.Log.Warn("Design version query from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            var info = new WireDesignVersionInfo
            {
                EntityId = station ? terminalEntityId : gridEntityId,
                Station = station,
            };
            try
            {
                Fill(steamId, terminalEntityId, gridEntityId, station, ref info);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Design version query failed for " + steamId + ".");
                info.Offered = false;
                info.Reason = "The server could not answer.";
            }
            _uplink.Send(steamId, FactionsWire.WriteDesignVersionInfo(info));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("DESIGN_VERSION_INFO steam=" + steamId + " station=" + station
                                     + " current=" + info.CurrentId + " offered=" + info.Offered + " next=" + info.NextId);
        }

        private void Fill(ulong steamId, long terminalEntityId, long gridEntityId, bool station,
                          ref WireDesignVersionInfo info)
        {
            TerminalContext ctx;
            string message;
            if (!TerminalAccess.TryAuthorise(_runtime, steamId, terminalEntityId, out ctx, out message))
            {
                info.Reason = message ?? "";
                return;
            }

            string currentId;
            if (station)
            {
                currentId = ctx.Node != null ? ctx.Node.StationPrefabId : null;
            }
            else
            {
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById<MyCubeGrid>(gridEntityId, out grid, false)
                    || grid == null || grid.MarkedForClose || grid.Closed)
                {
                    info.Reason = "That ship is no longer there.";
                    return;
                }
                currentId = _runtime.DesignOfShipGrid(grid);
            }

            FactionRuntime.DesignVersionPreview preview = _runtime.PreviewVersionForPlayer(ctx.Faction, currentId, station);
            info.Offered = preview.Offered;
            info.Family = preview.Family;
            info.CurrentId = preview.CurrentId;
            info.NextId = preview.NextId;
            info.ScopeAll = preview.ScopeAll;
            info.ScopeLabel = preview.ScopeLabel;
            info.Reason = preview.Reason;
        }
    }
}
