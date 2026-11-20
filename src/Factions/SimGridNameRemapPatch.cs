using Factions.Core;
using HarmonyLib;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// Keeps the durable <c>factions:npc:</c> entity name on a grid whose builder is remapped without
    /// its entity id changing.
    ///
    /// <para><b>Why.</b> SE's <c>MyObjectBuilder_EntityBase.Remap</c> sets <c>Name</c> to
    /// <c>RemapEntityName(EntityId)</c>, the id as a string, whatever the name was. Nexus's sector load
    /// (<c>NGPlugin.SyncComponents.EntityIDSync.LoadSector_Postfix</c>) runs it on every grid and character
    /// of every shard at every boot, keeping each id that already carries the server's prefix. So on the
    /// cluster every name <see cref="SimGridMarks"/> writes was gone after a restart: production Main
    /// 2026-11-20 held 241 in the save before one and none after. Readoption (<see cref="SimGridMarks.TryReadopt"/>),
    /// the trash exemption (<see cref="NpcTrashExemptionPatch"/>) and the reaper's named carrier read
    /// that name, and a player faction's hull carries no other once SE's claim timer clears its NPC
    /// bit.</para>
    ///
    /// <para><b>Only when the id is unchanged.</b> A remap that mints a new id is a copy: a pasted
    /// blueprint, a spawned prefab, or a Nexus id collision. A copy keeping our name for another id
    /// already reads as foreign (<see cref="NpcGridNaming.ShouldBackfillName"/>); restoring it there
    /// would only make it look like ours to the next check. Same id means the same grid.</para>
    ///
    /// <para><b>The grid's override, not the base.</b> Only grids carry the name, and the base method
    /// is two lines called non-virtually from the override, which the JIT may inline past a patch.
    /// The override is reached through a virtual call.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyObjectBuilder_CubeGrid), nameof(MyObjectBuilder_CubeGrid.Remap))]
    static class SimGridNameRemapPatch
    {
        [HarmonyPrefix]
        static void Prefix(MyObjectBuilder_CubeGrid __instance, out string __state)
        {
            __state = null;
            long namedEid;
            string fleetKey;
            if (__instance != null && NpcGridNaming.TryParseName(__instance.Name, out namedEid, out fleetKey)
                && namedEid == __instance.EntityId)
                __state = __instance.Name;
        }

        [HarmonyPostfix]
        static void Postfix(MyObjectBuilder_CubeGrid __instance, string __state)
        {
            if (__state == null || __instance == null) return;
            long namedEid;
            string fleetKey;
            if (NpcGridNaming.TryParseName(__state, out namedEid, out fleetKey) && namedEid == __instance.EntityId)
                __instance.Name = __state;
        }
    }
}
