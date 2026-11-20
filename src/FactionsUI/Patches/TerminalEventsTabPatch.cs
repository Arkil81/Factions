using System;
using FactionsUI.Events;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;

namespace FactionsUI.Patches;

/// <summary>
/// Adds the Events tab to SE's own terminal (K menu), on the same terms as
/// <see cref="TerminalAssetsTabPatch"/>: a ninth page appended by lazy integer key, so no vanilla
/// page is reordered, re-keyed or replaced.
///
/// Independent of the Assets patch on purpose. Either can be absent — the server grants the two
/// features separately — and the tab-row rescale each performs is a ratio, so the pair composes
/// whichever order Harmony runs them in.
///
/// This runs inside vanilla GUI construction, where an exception would take the whole K menu down
/// for a player who may not even have the feature. It therefore fails closed: anything thrown here
/// leaves the terminal exactly as SE built it, minus the tab.
/// </summary>
[HarmonyPatch(typeof(MyGuiScreenTerminal), "CreateTabs")]
internal static class TerminalEventsTabPatch
{
    private static void Postfix(MyGuiControlTabControl ___m_terminalTabs)
    {
        // The previous screen's page is gone the moment its tabs are rebuilt, and a stale one would
        // keep the server streaming to a window that no longer exists.
        EventsPage.Active = null;

        if (___m_terminalTabs == null) return;
        // No link, or a server that does not offer the feature: leave the terminal vanilla rather
        // than showing a tab that could only ever be empty.
        if (!EventsComponent.Available) return;

        try
        {
            EventsPage.Build(___m_terminalTabs);
        }
        catch (Exception ex)
        {
            EventsPage.Active = null;
            // A page half-built is still a drawn tab button leading nowhere; hide it.
            try { ___m_terminalTabs.GetTabSubControl(EventsPage.PageKey).IsTabVisible = false; }
            catch { }
            Plugin.Log("events tab construction failed: " + ex);
        }
    }
}
