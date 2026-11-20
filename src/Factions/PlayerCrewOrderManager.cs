using System;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace Factions
{
    /// <summary>
    /// Server side of the crew orders on seats (docs/ground-operations.md). Two doors into
    /// <see cref="CrewOrderControl.Issue"/>, as for the wing actions:
    ///
    /// <list type="bullet">
    ///   <item><b>A client's toolbar press</b> arrives as <see cref="WireMessageKind.CrewOrderRequest"/>;
    ///   the presser must command the seat's hull.</item>
    ///   <item><b>A block the server runs</b> — timer, button panel, event controller — activates the
    ///   action here, through <see cref="CrewSeatActions"/>; the seat's owner is the commander.</item>
    /// </list>
    ///
    /// <para>Withheld from a relayed client (<see cref="UplinkManager.RemoteWithheldFeatures"/>): the
    /// seat and its crew are entities in the caller's own sector.</para>
    /// </summary>
    internal sealed class PlayerCrewOrderManager
    {
        private readonly UplinkManager _uplink;

        public PlayerCrewOrderManager(UplinkManager uplink)
        {
            _uplink = uplink;
            if (_uplink != null) _uplink.RegisterHandler(WireMessageKind.CrewOrderRequest, OnRequest);
            if (CrewSeatActions.Register(OnBlockPress))
                FactionsPlugin.Log.Info("Crew order actions registered on seats (" + CrewActions.All.Length + ").");
            else
                FactionsPlugin.Log.Warn("Crew order actions NOT registered on seats: " + CrewSeatActions.FailureReason);
        }

        public void Close()
        {
            CrewSeatActions.Unregister();
        }

        private void OnRequest(ulong steamId, byte[] data)
        {
            long seatId;
            WireCrewOrder order;
            if (!FactionsWire.TryReadCrewOrderRequest(data, out seatId, out order))
            {
                FactionsPlugin.Log.Warn("Crew order from " + steamId + " was unreadable.");
                return;
            }
            if ((_uplink.GrantedFeatures(steamId) & WireFeatures.CrewOrders) == 0) return;
            bool ok = false;
            string message;
            MyPlayer player = WingWorld.PlayerOfSteam(steamId);
            var seat = MyEntities.GetEntityById(seatId) as MyCockpit;
            if (player == null) message = "Crew order refused: the server could not identify you.";
            else if (seat == null) message = "Crew order refused: that seat is gone.";
            else
            {
                MyCubeGrid hull = CrewPresence.OrderHull(player.Character, seat.CubeGrid);
                message = CrewOrderControl.Issue(hull, player.Identity.IdentityId, false, Map(order),
                    hull != null ? CrewPresence.CrewCount(hull.EntityId) : 0, out ok);
            }
            _uplink.Send(steamId, FactionsWire.WriteCrewOrderResult(ok, order, message));
        }

        /// <summary>A block the server runs pressed a crew order on a seat.</summary>
        private static void OnBlockPress(IMyTerminalBlock block, WireCrewOrder order)
        {
            try
            {
                if (block == null || !MyAPIGateway.Multiplayer.IsServer) return;
                var seat = block as MyCockpit;
                if (seat == null) return;
                MyCubeGrid hull = CrewPresence.OrderHull(null, seat.CubeGrid);
                bool ok;
                CrewOrderControl.Issue(hull, block.OwnerId, false, Map(order),
                    hull != null ? CrewPresence.CrewCount(hull.EntityId) : 0, out ok);
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Crew order from block " + (block != null ? block.EntityId : 0) + " failed.");
            }
        }

        internal static CrewOrder Map(WireCrewOrder order)
        {
            switch (order)
            {
                case WireCrewOrder.Mount:    return CrewOrder.Mount;
                case WireCrewOrder.Dismount: return CrewOrder.Dismount;
                case WireCrewOrder.Ease:     return CrewOrder.Ease;
                default:                     return CrewOrder.Auto;
            }
        }
    }
}
