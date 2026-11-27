using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using VRage.Game.ModAPI;

namespace FactionsUI.Patches;

/// <summary>
/// Who may read a faction's balance on SE's Factions page (K menu): its own members only. An admin
/// who wants every faction's treasury turns on the admin view (<c>!factions adminintel on</c>), which
/// shows it on the Overview tab; this label stays the player's own, so an admin is not shown a
/// foreign balance they did not ask for.
///
/// <para><b>Vanilla gets this half right.</b> <c>UpdateAccountControls</c> writes the balance only
/// for the viewer's own faction and "N/A" for every other, but <c>OnAccountBalanceChanged</c> has no
/// such test: it starts the label's count-up animation for whichever faction is selected, and the
/// controller's <c>Update</c> then writes the new balance into that label. So the first balance
/// change after selecting a foreign faction displays its balance — for an NPC faction, whose
/// treasury Factions mirrors into the SE account, that is within seconds (FAC-525).</para>
///
/// <para><b>One hook:</b> the balance change starts the animation only for the viewer's own faction;
/// anything else is suppressed before vanilla sees it.</para>
///
/// <para><b>Display only.</b> SE broadcasts every account's balance changes to every client
/// (<c>MyBankingSystem.ChangeBalanceBroadcastToClients</c>), so the figure is still in this client's
/// memory. Withholding it from the wire is a server change.</para>
///
/// <para><b>Fails closed.</b> A target or field that does not resolve skips the patch group at load
/// (logged once), which leaves the vanilla leak in place but the page intact. An exception inside the
/// balance-change hook suppresses the animation — a stale own balance is the safe wrong answer, a
/// foreign balance on screen is not.</para>
/// </summary>
internal static class FactionsPageBalancePatch
{
    private const string ControllerTypeName = "Sandbox.Game.Gui.MyTerminalFactionController";

    private static bool _resolved;
    private static MethodBase _balanceChanged;
    private static FieldInfo _userFaction;
    private static bool _failureLogged;

    private static bool Resolve()
    {
        if (_resolved) return _balanceChanged != null;
        _resolved = true;
        try
        {
            Type controller = AccessTools.TypeByName(ControllerTypeName);
            if (controller != null)
            {
                _balanceChanged = AccessTools.Method(controller, "OnAccountBalanceChanged",
                    new[] { typeof(MyAccountInfo), typeof(MyAccountInfo) });
                _userFaction = AccessTools.Field(controller, "m_userFaction");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log("faction balance: resolving the Factions page hooks threw: " + ex);
        }

        bool ok = _balanceChanged != null
                  && _userFaction != null && typeof(IMyFaction).IsAssignableFrom(_userFaction.FieldType);
        Plugin.Log(ok
            ? "faction balance: Factions page hooks bound"
            : "faction balance: Factions page hooks did not resolve (balance change " + (_balanceChanged != null)
              + ", user field " + (_userFaction != null)
              + ") -- foreign faction balances stay visible on balance change");
        if (!ok) _balanceChanged = null;
        return ok;
    }

    /// <summary>Before vanilla starts the balance animation: let it through only for the viewer's
    /// own faction.</summary>
    [HarmonyPatch]
    internal static class BalanceChanged
    {
        [HarmonyPrepare]
        private static bool Prepare() => Resolve();

        [HarmonyTargetMethod]
        private static MethodBase Target() => Resolve() ? _balanceChanged : null;

        [HarmonyPrefix]
        // ReSharper disable once UnusedMember.Local
        private static bool Prefix(object __instance, MyAccountInfo accInfo)
        {
            try
            {
                var user = _userFaction.GetValue(__instance) as IMyFaction;
                return user != null && user.FactionId == accInfo.OwnerIdentifier;
            }
            catch (Exception ex)
            {
                LogOnce("faction balance: balance-change hook failed, animation suppressed: " + ex);
                return false;
            }
        }
    }

    private static void LogOnce(string message)
    {
        if (_failureLogged) return;
        _failureLogged = true;
        Plugin.Log(message);
    }
}
