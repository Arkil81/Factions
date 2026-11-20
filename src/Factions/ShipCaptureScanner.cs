using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Host-only (SE-facing) helpers for `!factions ship capture`: finding the ship a player is
    /// looking at, and checking whether they have access to it. No equivalent exists in Core — a
    /// raycast/live-grid query has no meaning outside a running SE session.
    /// </summary>
    internal static class ShipCaptureScanner
    {
        /// <summary>Raycasts from the player's head/camera out to <paramref name="maxRangeM"/> and, on
        /// a hit, returns every grid in the hit grid's mechanical group (rotors/pistons/connectors —
        /// mirrors how a vanilla prefab can bundle multiple <c>CubeGrids</c>). A single unconnected
        /// grid still returns a one-element list.</summary>
        public static bool TryFindTargetedGrids(IMyPlayer player, double maxRangeM,
            out List<MyCubeGrid> grids, out string error)
        {
            grids = null;
            MyCubeGrid rootGrid;
            if (!TryFindTargetedGrid(player, maxRangeM, out rootGrid, out error)) return false;
            grids = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(rootGrid);
            return true;
        }

        /// <summary>The grid the player's raycast hits — the one grid, not its mechanical group.</summary>
        public static bool TryFindTargetedGrid(IMyPlayer player, double maxRangeM,
            out MyCubeGrid grid, out string error)
        {
            grid = null;
            error = null;

            IMyCharacter character = player != null ? player.Character : null;
            if (character == null)
            {
                error = "You need to be alive and in the world to capture a ship.";
                return false;
            }

            MatrixD head = character.GetHeadMatrix(true);
            Vector3D from = head.Translation;
            Vector3D to = from + head.Forward * maxRangeM;

            IHitInfo hit;
            if (!MyAPIGateway.Physics.CastRay(from, to, out hit) || hit == null || hit.HitEntity == null)
            {
                error = "No ship in range — look directly at the hull, within " + maxRangeM.ToString("0") + " m.";
                return false;
            }

            grid = hit.HitEntity.GetTopMostParent() as MyCubeGrid;
            if (grid == null)
            {
                error = "That's not a ship — look directly at the hull.";
                return false;
            }
            return true;
        }

        /// <summary>True only if the player has access to every grid in the group — an owner, or
        /// sharing a faction with the owner. An unowned/derelict grid always fails: there is no
        /// owner to grant access. Applied per-grid so a mechanically-attached grid the player
        /// doesn't own (e.g. someone else's turret bolted to their hull) blocks the whole capture.</summary>
        public static bool PlayerHasAccessToAll(IMyPlayer player, List<MyCubeGrid> grids, out string deniedReason)
        {
            deniedReason = null;
            MyFaction playerFaction = MySession.Static.Factions.GetPlayerFaction(player.IdentityId);

            foreach (MyCubeGrid grid in grids)
            {
                if (!HasAccess(player.IdentityId, playerFaction, grid))
                {
                    deniedReason = grids.Count > 1
                        ? "You don't have access to every part of that ship (a mechanically-connected grid isn't yours)."
                        : "You don't have access to that ship.";
                    return false;
                }
            }
            return true;
        }

        // Mirrors WorldScanner.GetOwningNpcFaction's "first non-zero BigOwners entry" idiom.
        private static bool HasAccess(long playerIdentityId, MyFaction playerFaction, MyCubeGrid grid)
        {
            List<long> owners = grid.BigOwners;
            long ownerId = 0L;
            if (owners != null && owners.Count > 0 && owners[0] != 0L) ownerId = owners[0];
            else if (owners != null && owners.Count > 1) ownerId = owners[1];
            if (ownerId == 0L) return false; // unowned/derelict -- no owner to grant access

            if (ownerId == playerIdentityId) return true;
            if (playerFaction == null) return false;

            MyFaction ownerFaction = MySession.Static.Factions.GetPlayerFaction(ownerId);
            return ownerFaction != null && ownerFaction.FactionId == playerFaction.FactionId;
        }
    }
}
