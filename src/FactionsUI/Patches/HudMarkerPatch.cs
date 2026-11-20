using HarmonyLib;
using Sandbox.Game.Gui;

namespace FactionsUI.Patches;

/// <summary>
/// Draws Factions' own HUD layers inside the HUD screen's own draw pass — the scanner markers, the
/// signature readout, the placement readout, and the status panel.
///
/// A MySessionComponentBase.Draw override queues its sprites outside that pass, in a different
/// batch and scissor state, which made the markers flicker whenever another UI element composited
/// over them. A postfix here puts them in exactly the state vanilla's own HUD markers are drawn in.
/// </summary>
[HarmonyPatch(typeof(MyGuiScreenHudSpace), nameof(MyGuiScreenHudSpace.Draw))]
internal static class HudMarkerPatch
{
    [HarmonyPostfix]
    // ReSharper disable once UnusedMember.Local
    private static void DrawFactionsHud(bool __result)
    {
        // Draw() returns false without drawing when the HUD is hidden or still fading in. Honour
        // that: scanner markers are part of the HUD and should vanish with it (Alt-F10, cutscenes).
        if (!__result) return;
        ScannerComponent scanner = ScannerComponent.Instance;
        if (scanner != null) scanner.DrawMarkers();

        SignatureComponent signature = SignatureComponent.Instance;
        if (signature != null) signature.Draw();

        ExpandComponent expand = ExpandComponent.Instance;
        if (expand != null) expand.Draw();

        StatusPanelComponent panel = StatusPanelComponent.Instance;
        if (panel != null) panel.Draw();
    }
}
