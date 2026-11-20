using Factions.Wire;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;

namespace FactionsUI.Combat;

/// <summary>
/// Shows a resolved fight on the HUD (docs/client-plugin.md, Part 19): the server sends one
/// <see cref="WireMessageKind.CombatOutcomeNotice"/> to each linked player within observation range
/// of a fight when it resolves, with the sentence and whether the player's faction won, lost or was
/// not in it. Shown for <see cref="NoticeMs"/>: green for a win, red for a loss, white otherwise.
/// Nothing is kept.
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class CombatOutcomeComponent : MySessionComponentBase
{
    private const int NoticeMs = 6000;

    public override void LoadData()
    {
        UplinkComponent.RegisterHandler(WireMessageKind.CombatOutcomeNotice, OnNotice);
    }

    private static void OnNotice(byte[] data)
    {
        if (!FactionsWire.TryReadCombatOutcomeNotice(data, out WireCombatTone tone, out string text))
        {
            Plugin.Log("COMBAT_OUTCOME_NOTICE unreadable");
            return;
        }
        if (string.IsNullOrEmpty(text) || MyAPIGateway.Utilities == null) return;
        MyAPIGateway.Utilities.ShowNotification(text, NoticeMs, FontFor(tone));
        if (Config.Current.DebugLogging) Plugin.Log("COMBAT_OUTCOME_NOTICE tone=" + tone + " " + text);
    }

    internal static string FontFor(WireCombatTone tone)
    {
        switch (tone)
        {
            case WireCombatTone.Win: return MyFontEnum.Green;
            case WireCombatTone.Loss: return MyFontEnum.Red;
            default: return MyFontEnum.White;
        }
    }
}
