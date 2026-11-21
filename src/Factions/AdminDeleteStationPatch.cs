using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Game.SessionComponents;

namespace Factions
{
    /// <summary>
    /// Allows admins to delete/cut economy stations without vanilla's protection dialogs.
    ///
    /// Vanilla SE has three interlocking blocks that prevent admins from removing registered
    /// faction stations.  All three are bypassed here because our plugin respawns stations on
    /// its own schedule — the vanilla protection serves no purpose and prevents manual cleanup.
    /// That holds on a Nexus agent whose hands stand the authority's stations, so it applies there
    /// too; an agent without hands keeps vanilla's protection.
    ///
    /// Block 1 — admin-menu Remove button (entity-selection path):
    ///   AskIsValidForEdit_Server checks GetStationByGridId → sends canEdit=false to client →
    ///   AskIsValidForEdit_Reponse sets m_attachIsNpcStation=true → disables Remove/Depower/Stop.
    ///   Fix: prefix forces canEdit=true before m_attachIsNpcStation is written.
    ///
    /// Block 2 — admin-menu Remove button (cycle path):
    ///   Cycle_Request checks IsGridStation on the server → sends isNpcStation=true to client →
    ///   Cycle_Implementation sets m_attachIsNpcStation=true → disables buttons.
    ///   Fix: IsGridStation prefix returns false, so isNpcStation is never set.
    ///
    /// Block 3 — creative-mode Cut (Ctrl+X) and clipboard Delete:
    ///   MyCubeGrid.OnGridClosedRequest checks IsGridStation on the server → fires
    ///   StationClosingDenied back to client → shows "Removal not allowed" dialog; grid stays.
    ///   Fix: same IsGridStation prefix; the early-return is never reached.
    /// </summary>
    [HarmonyPatch(typeof(MyGuiScreenAdminMenu), "AskIsValidForEdit_Reponse")]
    static class AdminDeleteStationPatch_SelectionPath
    {
        [HarmonyPrefix]
        static void Prefix(long entityId, ref bool canEdit)
        {
            if (SimulationGate.Deferred && !SimulationGate.Hands) return;
            canEdit = true;
        }
    }

    [HarmonyPatch(typeof(MySessionComponentEconomy), "IsGridStation")]
    static class AdminDeleteStationPatch_IsGridStation
    {
        [HarmonyPrefix]
        static bool Prefix(ref bool __result)
        {
            if (SimulationGate.Deferred && !SimulationGate.Hands) return true;
            __result = false;
            return false; // skip original
        }
    }
}
