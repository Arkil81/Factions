using System;
using System.Collections.Generic;
using Sandbox.Game.World;
using Torch.Commands;
using VRage.Game.ModAPI;
using VRageMath;
using Factions.Core;

namespace Factions.Commands
{
    /// <summary>
    /// Shared caller-resolution helpers for every player-permission `!factions ...` command —
    /// promoted out of <see cref="PlayerStationCommands"/> (the first command class to need them)
    /// once <see cref="PlayerShipCommands"/> became a second caller.
    /// </summary>
    internal static class PlayerCommandHelpers
    {
        // "At a station" threshold, matching PlayerPresenceManager.PresenceRadiusKm.
        internal const double NearbyRadiusKm = 5.0;

        /// <summary>Who ran the command: the player connected here, or one connected to a Nexus agent
        /// whose command was forwarded (<see cref="RemoteCommandContext"/>). Null for the console and
        /// the file bridge.</summary>
        internal static CommandCaller Caller(CommandContext context)
        {
            var remote = context as RemoteCommandContext;
            if (remote != null) return remote.Caller;
            IMyPlayer player = context.Player;
            if (player == null) return null;
            var caller = new CommandCaller
            {
                SteamId = player.SteamUserId,
                IdentityId = player.IdentityId,
                Promote = MySession.Static != null ? MySession.Static.GetUserPromoteLevel(player.SteamUserId) : MyPromoteLevel.None,
            };
            IMyCharacter ch = player.Character;
            if (ch != null)
            {
                caller.HasPosition = true;
                caller.PositionM = ch.PositionComp.GetPosition();
            }
            return caller;
        }

        /// <summary>True when the caller is a server admin. <c>[Permission]</c> gates a whole
        /// command, but `develop` is one command carrying both a player form and an admin form, so
        /// the admin form has to check the promote level itself. False without a player — a console
        /// or file-bridge caller reaches the grant through the `factions develop` bridge verb
        /// instead, which never goes through Torch's permission check at all.</summary>
        internal static bool IsAdmin(CommandContext context)
        {
            CommandCaller caller = Caller(context);
            return caller != null && caller.Promote >= MyPromoteLevel.Admin;
        }

        /// <summary>The caller's identity on this server; 0 for the console and the file bridge.</summary>
        internal static long CallerIdentity(CommandContext context)
        {
            CommandCaller caller = Caller(context);
            return caller != null ? caller.IdentityId : 0L;
        }

        /// <summary>True, having told the caller why, when they may not spend <paramref name="owner"/>'s
        /// treasury or components, or change what it is set to spend, switching off included: only its
        /// leader, founder or a server admin may
        /// (<see cref="PlayerAuthority"/>). Call it on the write branch only — reading stays open to
        /// any member.</summary>
        internal static bool RefuseSpend(CommandContext context, FactionState owner)
        {
            string refusal = PlayerAuthority.SpendRefusal(CallerIdentity(context), owner.Tag);
            if (refusal == null) return false;
            context.Respond(refusal);
            return true;
        }

        internal static bool TryResolveCallerFaction(CommandContext context, out FactionState owner, out string error)
        {
            owner = null;
            error = null;

            FactionsPlugin plugin = FactionsPlugin.Instance;
            FactionRuntime runtime = plugin != null ? plugin.Runtime : null;
            if (runtime == null) { error = "Factions plugin not ready."; return false; }

            CommandCaller caller = Caller(context);
            if (caller == null) { error = "Run this in-game, not from the server console."; return false; }

            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(caller.IdentityId);
            if (seFaction == null) { error = "You are not in a faction."; return false; }

            owner = runtime.FindFaction(seFaction.Tag);
            if (owner == null || !owner.IsPlayerFaction)
            {
                error = "[" + seFaction.Tag + "] is not registered with Factions yet — claim a node first.";
                return false;
            }
            return true;
        }

        /// <summary>Resolves the caller's SE faction WITHOUT requiring it to be on the Factions
        /// roster — <paramref name="owner"/> comes back null for a faction that has never claimed
        /// anything, which is a normal state, not an error.
        ///
        /// <para>Every other verb here acts on a node the faction already owns, so registration is a
        /// precondition for them. Expand is the one verb whose whole point is that a faction owning
        /// nothing can still act (docs/economy-territory.md → Expanding), and registration happens
        /// inside its transaction, after the money moves.</para></summary>
        internal static bool TryResolveCallerSeFaction(CommandContext context, out MyFaction seFaction,
                                                       out FactionState owner, out string error)
        {
            seFaction = null;
            owner = null;
            error = null;

            FactionsPlugin plugin = FactionsPlugin.Instance;
            FactionRuntime runtime = plugin != null ? plugin.Runtime : null;
            if (runtime == null) { error = "Factions plugin not ready."; return false; }

            CommandCaller caller = Caller(context);
            if (caller == null) { error = "Run this in-game, not from the server console."; return false; }

            seFaction = MySession.Static.Factions.GetPlayerFaction(caller.IdentityId);
            if (seFaction == null) { error = "You are not in a faction."; return false; }

            owner = runtime.FindFaction(seFaction.Tag);
            if (owner != null && !owner.IsPlayerFaction)
            {
                // An NPC tag the brain is already driving. Its territory is the brain's to expand.
                error = "[" + seFaction.Tag + "] is run by the simulation, not by its members.";
                seFaction = null;
                owner = null;
                return false;
            }
            return true;
        }

        /// <summary>The caller's own position in the km-scale coordinates the world model uses.</summary>
        internal static bool TryResolvePositionKm(CommandContext context, out Vec3 posKm, out string error)
        {
            posKm = new Vec3(0, 0, 0);
            error = null;

            CommandCaller caller = Caller(context);
            if (caller == null || !caller.HasPosition) { error = "Can't find your in-world position."; return false; }

            Vector3D worldPos = caller.PositionM;
            posKm = new Vec3(worldPos.X / 1000.0, worldPos.Y / 1000.0, worldPos.Z / 1000.0);
            return true;
        }

        // Mirrors PlayerPresenceManager.Refresh's own proximity math (world metres -> km,
        // straight-line distance against SpatialNode.Position) and its PresenceRadiusKm threshold,
        // just against IMyCharacter (CommandContext.Player is IMyPlayer, not the concrete MyPlayer
        // the presence scan iterates) instead of re-scanning every online player.
        internal static bool TryResolveNearestOwnedNode(CommandContext context, IEnumerable<SpatialNode> candidates,
            out SpatialNode nearest, out string error)
        {
            nearest = null;
            error = null;

            CommandCaller caller = Caller(context);
            if (caller == null || !caller.HasPosition)
            {
                error = "Can't find your in-world position — specify a nodeId explicitly.";
                return false;
            }

            Vector3D worldPos = caller.PositionM;
            double px = worldPos.X / 1000.0, py = worldPos.Y / 1000.0, pz = worldPos.Z / 1000.0;

            double bestDist = double.MaxValue;
            foreach (SpatialNode n in candidates)
            {
                double dx = px - n.Position.X, dy = py - n.Position.Y, dz = pz - n.Position.Z;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dist <= NearbyRadiusKm && dist < bestDist) { bestDist = dist; nearest = n; }
            }

            if (nearest == null)
                error = "No node found within " + NearbyRadiusKm.ToString("0") + " km — stand closer, or specify a nodeId explicitly.";
            return nearest != null;
        }
    }
}
