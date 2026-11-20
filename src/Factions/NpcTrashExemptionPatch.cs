using Factions.Core;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Keeps SE's trash cleaner off a Factions hull for as long as it carries the NPC-spawned bit, which is what
    /// SE 1.210 did for every NPC grid.
    ///
    /// <para><b>Why.</b> SE 1.211 (Warfare 3) narrowed <c>MySessionComponentTrash.UpdateTrash</c>'s NPC exemption to
    /// <c>IsNpcSpawnedGrid &amp;&amp; !IsClaimTimerRunning</c>, so claimed vanilla encounters get cleaned up.
    /// <c>MyNPCGridClaimSessionComponent</c> starts that timer on the first block damaged, the first shot fired
    /// from a dynamic grid, a block added, a split or merge, or a non-NPC owner — that is, on every Factions hull
    /// that has been in a fight, and on every hull a player faction flies. From then on the hull is judged like a
    /// player's derelict and can be removed under the sim, which still counts it.</para>
    ///
    /// <para><b>Scope.</b> Only grids this plugin marked (the per-process record, or the durable entity name a
    /// restart or a Nexus transfer leaves behind). The claim timer still runs and still clears the bit on expiry,
    /// so capture and <see cref="GridReaper"/> keep their 1.210 behaviour; vanilla's outside-the-world removal is
    /// left to vanilla.</para>
    /// </summary>
    [HarmonyPatch(typeof(MySessionComponentTrash), "UpdateTrash")]
    static class NpcTrashExemptionPatch
    {
        [HarmonyPrefix]
        static bool Prefix(MyCubeGrid grid, ref bool __result)
        {
            if (grid == null || !grid.IsNpcSpawnedGrid || !IsOurs(grid)) return true;
            if (!MySession.IsInsideWorld(grid.PositionComp.GetPosition())) return true;
            __result = false;
            return false;
        }

        private static bool IsOurs(MyCubeGrid grid)
        {
            if (SimGridMarks.IsMarked(grid.EntityId)) return true;
            long namedEid;
            string fleetKey;
            return NpcGridNaming.TryParseName(grid.Name, out namedEid, out fleetKey) && namedEid == grid.EntityId;
        }
    }
}
