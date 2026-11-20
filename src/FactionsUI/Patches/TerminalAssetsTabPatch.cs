using System;
using FactionsUI.Assets;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;

namespace FactionsUI.Patches;

/// <summary>
/// Adds the Assets tab to SE's own terminal (K menu).
///
/// The terminal creates its tab pages lazily by integer key, so an eighth page appends after the
/// vanilla seven without reordering, re-keying or replacing any of them: everything SE does with a
/// page it does not know — page-changed, default focus, the per-page controller lookup — is already
/// a <c>TryGetValue</c> or a <c>switch</c> with no default, so an unknown key is ignored rather
/// than faulted.
///
/// This runs inside vanilla GUI construction, where an exception would take the whole K menu down
/// for a player who may not even have the feature. It therefore fails closed: anything thrown here
/// leaves the terminal exactly as SE built it, minus the tab.
/// </summary>
[HarmonyPatch(typeof(MyGuiScreenTerminal), "CreateTabs")]
internal static class TerminalAssetsTabPatch
{
    private static void Postfix(MyGuiControlTabControl ___m_terminalTabs)
    {
        // The previous screen's page is gone the moment its tabs are rebuilt, and a stale one
        // would keep the server streaming to a window that no longer exists.
        AssetsPage.Active = null;

        if (___m_terminalTabs == null) return;
        // No link, or a server that does not offer the feature: leave the terminal vanilla rather
        // than showing a tab that could only ever be empty.
        if (!AssetsComponent.Available) return;

        try
        {
            AssetsPage.Build(___m_terminalTabs);
        }
        catch (Exception ex)
        {
            AssetsPage.Active = null;
            // A page half-built is still a drawn tab button leading nowhere; hide it.
            try { ___m_terminalTabs.GetTabSubControl(AssetsPage.PageKey).IsTabVisible = false; }
            catch { }
            Plugin.Log("assets tab construction failed: " + ex);
        }
    }
}
