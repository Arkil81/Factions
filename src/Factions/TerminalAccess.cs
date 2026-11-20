using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>Everything one request needs after it has been authorised. Built once per
    /// request; no field on it is taken from the client.</summary>
    public sealed class TerminalContext
    {
        public IMyPlayer Player;
        public long IdentityId;
        public MyIdentity Identity;
        public FactionState Faction;
        public MyCubeBlock Block;
        public SpatialNode Node;
    }

    /// <summary>
    /// The authorisation gates behind every Services Terminal surface: the owner's gate
    /// (<see cref="TryAuthorise"/>) for ship assets (<see cref="PlayerShipAssetsManager"/>) and
    /// station refit (<see cref="PlayerStationRefitManager"/>), and the customer's gate
    /// (<see cref="TryAuthoriseCustomer"/>) for refuel and rearm (<see cref="DockSupplyManager"/>).
    ///
    /// <para>The client sends an entity id and nothing else that matters. This resolves the sender's
    /// SteamID to an identity, that identity to an SE faction, that faction to a
    /// <see cref="FactionState"/> that must be a player faction, the entity id to a Services Terminal
    /// block, its grid to an SE station, and that station to a node this faction owns — and refuses
    /// with a reason at any step. A client-side hide is not a gate; this is.</para>
    ///
    /// <para>Shared rather than duplicated because the two surfaces must never disagree about who may
    /// act at a station: one of them scans a design, the other rebuilds the grid the first one
    /// scanned, and both are standing at the same block when they do it.</para>
    /// </summary>
    public static class TerminalAccess
    {
        /// <summary>
        /// Resolves and checks everything a request needs, from the sender's SteamID down. The
        /// terminal entity id is the only thing the client contributes and it is verified, not
        /// believed: the block has to exist, be a Services Terminal, stand on an SE station that is
        /// one of this brain's nodes, that node has to be owned by the caller's own player faction,
        /// and the caller has to be standing near it.
        /// </summary>
        public static bool TryAuthorise(FactionRuntime runtime, ulong steamId, long terminalEntityId,
            out TerminalContext ctx, out string refusal)
        {
            ctx = null;

            if (runtime == null || runtime.World == null || runtime.World.Graph == null
                || MySession.Static == null || MySession.Static.Factions == null)
            {
                refusal = "Factions is not ready on this server.";
                return false;
            }

            IMyPlayer player = OnlinePlayer(steamId);
            if (player == null || player.Identity == null)
            {
                refusal = "Could not identify you on this server.";
                return false;
            }
            long identityId = player.IdentityId;

            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            FactionState faction = seFaction != null ? runtime.FindFaction(seFaction.Tag) : null;
            if (faction == null || !faction.IsPlayerFaction)
            {
                refusal = "Your faction is not registered with Factions yet.";
                return false;
            }

            MyCubeBlock block;
            if (!MyEntities.TryGetEntityById<MyCubeBlock>(terminalEntityId, out block, false)
                || block == null || block.MarkedForClose || !IsServicesTerminal(block))
            {
                refusal = "That is not a Services Terminal.";
                return false;
            }

            double reachM = Commands.PlayerCommandHelpers.NearbyRadiusKm * 1000.0;
            if (Vector3D.Distance(player.GetPosition(), block.PositionComp.GetPosition()) > reachM)
            {
                refusal = "You are too far from that terminal.";
                return false;
            }

            long stationId = block.CubeGrid != null
                ? StationIdOfGrid(block.CubeGrid.EntityId)
                : 0;
            SpatialNode node = stationId != 0 ? FindNodeByStation(runtime, stationId) : null;
            if (node == null)
            {
                refusal = "This station is not one of your faction's.";
                return false;
            }
            if (!string.Equals(node.OwnerFactionTag, faction.Tag, StringComparison.OrdinalIgnoreCase))
            {
                refusal = "This station belongs to " + OwnerLabel(node) + ", not to [" + faction.Tag + "].";
                return false;
            }

            ctx = new TerminalContext
            {
                Player = player,
                IdentityId = identityId,
                Identity = MySession.Static.Players.TryGetIdentity(identityId),
                Faction = faction,
                Block = block,
                Node = node,
            };
            refusal = null;
            return true;
        }

        /// <summary>
        /// The customer's gate, for the surfaces that sell rather than administer: refuel and
        /// rearm. Resolves the same player, block, distance and node as <see cref="TryAuthorise"/>,
        /// but the node may belong to any faction and the caller need not be in one — a station
        /// selling fuel sells it to whoever may use its terminal.
        ///
        /// <para>"May use" is vanilla's own rule, re-checked here because the client-side one is not
        /// a gate: <c>MyUseObjectServicesTerminal</c> opens the screen for a player whose relation
        /// to the block's owner is friendly, or when the block carries an <c>AnyoneCanUse</c>
        /// component that says so. A request from anyone else is refused.</para>
        ///
        /// <para><see cref="TerminalContext.Faction"/> is the caller's own registered faction when
        /// there is one and null otherwise; it is not the station's owner.</para>
        ///
        /// <para>A customer the owner will not trade with is refused here, with the reason, after the
        /// node is resolved: <see cref="StoreRefusal.Judge"/>, docs/diplomacy-plan.md rule 2. The
        /// refusal string is what the terminal shows, so this path sends no chat message.</para>
        /// </summary>
        public static bool TryAuthoriseCustomer(FactionRuntime runtime, ulong steamId, long terminalEntityId,
            out TerminalContext ctx, out string refusal)
        {
            ctx = null;

            if (runtime == null || runtime.World == null || runtime.World.Graph == null
                || MySession.Static == null || MySession.Static.Factions == null)
            {
                refusal = "Factions is not ready on this server.";
                return false;
            }

            IMyPlayer player = OnlinePlayer(steamId);
            if (player == null || player.Identity == null)
            {
                refusal = "Could not identify you on this server.";
                return false;
            }
            long identityId = player.IdentityId;

            MyCubeBlock block;
            if (!MyEntities.TryGetEntityById<MyCubeBlock>(terminalEntityId, out block, false)
                || block == null || block.MarkedForClose || !IsServicesTerminal(block))
            {
                refusal = "That is not a Services Terminal.";
                return false;
            }

            double reachM = Commands.PlayerCommandHelpers.NearbyRadiusKm * 1000.0;
            if (Vector3D.Distance(player.GetPosition(), block.PositionComp.GetPosition()) > reachM)
            {
                refusal = "You are too far from that terminal.";
                return false;
            }

            if (!MayUseBlock(block, identityId))
            {
                refusal = "This terminal's owner does not serve you.";
                return false;
            }

            long stationId = block.CubeGrid != null
                ? StationIdOfGrid(block.CubeGrid.EntityId)
                : 0;
            SpatialNode node = stationId != 0 ? FindNodeByStation(runtime, stationId) : null;
            if (node == null || string.IsNullOrEmpty(node.OwnerFactionTag)
                || NeutralFaction.IsClaimable(node.OwnerFactionTag))
            {
                refusal = "This station has no stock to sell.";
                return false;
            }

            StoreAccessDecision bar = StoreRefusal.Judge(runtime, identityId, node.OwnerFactionTag);
            if (bar.Refused)
            {
                StoreRefusal.Trace("dock", node, identityId, bar);
                refusal = bar.Message + ".";
                return false;
            }

            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            ctx = new TerminalContext
            {
                Player = player,
                IdentityId = identityId,
                Identity = MySession.Static.Players.TryGetIdentity(identityId),
                Faction = seFaction != null ? runtime.FindFaction(seFaction.Tag) : null,
                Block = block,
                Node = node,
            };
            refusal = null;
            return true;
        }

        /// <summary>Whether <paramref name="terminalEntityId"/> is a Services Terminal on a station
        /// Factions runs: an SE station that is a node with an owner other than the claimable
        /// neutral faction — the node <see cref="TryAuthoriseCustomer"/> sells from. At such a
        /// station the Repair tab is Restore's (<see cref="RestorationService"/>).</summary>
        public static bool IsFactionsStationTerminal(FactionRuntime runtime, long terminalEntityId)
        {
            if (runtime == null || runtime.World == null || runtime.World.Graph == null
                || MySession.Static == null || MySession.Static.Factions == null) return false;
            MyCubeBlock block;
            if (!MyEntities.TryGetEntityById<MyCubeBlock>(terminalEntityId, out block, false)
                || block == null || block.MarkedForClose || !IsServicesTerminal(block) || block.CubeGrid == null) return false;
            return IsFactionsStationGrid(runtime, block.CubeGrid.EntityId);
        }

        /// <summary>As <see cref="IsFactionsStationTerminal"/>, for the station grid itself.</summary>
        public static bool IsFactionsStationGrid(FactionRuntime runtime, long stationGridEntityId)
        {
            if (runtime == null || runtime.World == null || runtime.World.Graph == null
                || MySession.Static == null || MySession.Static.Factions == null) return false;
            long stationId = StationIdOfGrid(stationGridEntityId);
            SpatialNode node = stationId != 0 ? FindNodeByStation(runtime, stationId) : null;
            return node != null && !string.IsNullOrEmpty(node.OwnerFactionTag) && !NeutralFaction.IsClaimable(node.OwnerFactionTag);
        }

        /// <summary>Vanilla's use test for a Services Terminal, minus the admin override (which is a
        /// client-side session flag the server does not hold per player).</summary>
        private static bool MayUseBlock(MyCubeBlock block, long identityId)
        {
            var functional = block as MyFunctionalBlock;
            if (functional == null) return false;
            if (VRage.Game.MyRelationsBetweenPlayerAndBlockExtensions.IsFriendly(
                    functional.GetUserRelationToOwner(identityId))) return true;
            Sandbox.ModAPI.IMyAccessAnyoneCanUse anyone;
            return functional.Components.TryGet<Sandbox.ModAPI.IMyAccessAnyoneCanUse>(out anyone)
                   && anyone != null && anyone.AnyoneCanUse;
        }

        internal static string OwnerLabel(SpatialNode node)
        {
            return string.IsNullOrEmpty(node.OwnerFactionTag) || NeutralFaction.IsClaimable(node.OwnerFactionTag)
                ? "nobody" : "[" + node.OwnerFactionTag + "]";
        }

        /// <summary>The vanilla Services Terminal block, by its own subtype. Deliberately a name
        /// test rather than a component test: the two service components a modded terminal might
        /// carry are not what makes this feature legitimate — standing on a station this faction
        /// owns is — and a name is the one thing a block of that definition always has.</summary>
        private static bool IsServicesTerminal(MyCubeBlock block)
        {
            MyCubeBlockDefinition def = block.BlockDefinition;
            return def != null
                && string.Equals(def.Id.SubtypeName, "ServicesTerminal", StringComparison.OrdinalIgnoreCase);
        }

        private static long StationIdOfGrid(long gridEntityId)
        {
            IMyFactionStation station = MySession.Static.Factions.GetStationByGridId(gridEntityId);
            return station != null ? station.Id : 0;
        }

        internal static SpatialNode FindNodeByStation(FactionRuntime runtime, long stationId)
        {
            foreach (SpatialNode n in runtime.World.Graph.Nodes.Values)
                if (n.StableStationId == stationId) return n;
            return null;
        }

        private static IMyPlayer OnlinePlayer(ulong steamId)
        {
            if (MySession.Static == null || MySession.Static.Players == null) return null;
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Identity == null) continue;
                if (player.Id.SteamId == steamId) return player;
            }
            return null;
        }
    }
}
