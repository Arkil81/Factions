using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace FactionsUI.Patches;

/// <summary>
/// Who may read a faction's balance on SE's Factions page (K menu): its own members, and admins.
///
/// <para><b>Vanilla gets this half right.</b> <c>UpdateAccountControls</c> writes the balance only
/// for the viewer's own faction and "N/A" for every other, but <c>OnAccountBalanceChanged</c> has no
/// such test: it starts the label's count-up animation for whichever faction is selected, and the
/// controller's <c>Update</c> then writes the new balance into that label. So the first balance
/// change after selecting a foreign faction displays its balance — for an NPC faction, whose
/// treasury Factions mirrors into the SE account, that is within seconds (FAC-525).</para>
///
/// <para>Two hooks, one rule:</para>
/// <list type="bullet">
/// <item><b>Balance change:</b> the animation starts only for the viewer's own faction, or for an
/// admin. Anything else is suppressed before vanilla sees it.</item>
/// <item><b>Selection:</b> an admin is shown the selected faction's balance in place of vanilla's
/// "N/A", so the one viewer allowed the figure does not have to wait for it to change.</item>
/// </list>
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
    private const string BalanceLabelName = "labelBalanceValue";
    private const string CurrencyImageName = "imageCurrency";

    private static bool _resolved;
    private static MethodBase _balanceChanged;
    private static MethodBase _updateAccount;
    private static FieldInfo _selectedFaction;
    private static FieldInfo _userFaction;
    private static FieldInfo _controlsParent;
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
                _updateAccount = AccessTools.Method(controller, "UpdateAccountControls", Type.EmptyTypes);
                _selectedFaction = AccessTools.Field(controller, "m_selectedFaction");
                _userFaction = AccessTools.Field(controller, "m_userFaction");
                _controlsParent = AccessTools.Field(controller, "m_controlsParent");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log("faction balance: resolving the Factions page hooks threw: " + ex);
        }

        bool ok = _balanceChanged != null && _updateAccount != null
                  && _selectedFaction != null && typeof(IMyFaction).IsAssignableFrom(_selectedFaction.FieldType)
                  && _userFaction != null && typeof(IMyFaction).IsAssignableFrom(_userFaction.FieldType)
                  && _controlsParent != null && typeof(IMyGuiControlsParent).IsAssignableFrom(_controlsParent.FieldType);
        Plugin.Log(ok
            ? "faction balance: Factions page hooks bound"
            : "faction balance: Factions page hooks did not resolve (balance change " + (_balanceChanged != null)
              + ", account refresh " + (_updateAccount != null) + ", selected field " + (_selectedFaction != null)
              + ", user field " + (_userFaction != null) + ", controls field " + (_controlsParent != null)
              + ") -- foreign faction balances stay visible on balance change");
        if (!ok) { _balanceChanged = null; _updateAccount = null; }
        return ok;
    }

    private static bool LocalIsAdmin()
    {
        var session = MyAPIGateway.Session;
        var mp = MyAPIGateway.Multiplayer;
        return session != null && mp != null && session.IsUserAdmin(mp.MyId);
    }

    /// <summary>Before vanilla starts the balance animation: let it through only for the viewer's
    /// own faction, or for an admin.</summary>
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
                if (user != null && user.FactionId == accInfo.OwnerIdentifier) return true;
                return LocalIsAdmin();
            }
            catch (Exception ex)
            {
                LogOnce("faction balance: balance-change hook failed, animation suppressed: " + ex);
                return false;
            }
        }
    }

    /// <summary>After vanilla fills the account controls for a selection: an admin sees a foreign
    /// faction's balance instead of "N/A".</summary>
    [HarmonyPatch]
    internal static class AccountRefresh
    {
        [HarmonyPrepare]
        private static bool Prepare() => Resolve();

        [HarmonyTargetMethod]
        private static MethodBase Target() => Resolve() ? _updateAccount : null;

        [HarmonyPostfix]
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(object __instance)
        {
            try
            {
                var selected = _selectedFaction.GetValue(__instance) as IMyFaction;
                if (selected == null) return;
                var user = _userFaction.GetValue(__instance) as IMyFaction;
                if (user != null && user.FactionId == selected.FactionId) return;   // vanilla already shows it
                if (!LocalIsAdmin()) return;

                long balance;
                if (!selected.TryGetBalanceInfo(out balance)) return;
                var parent = _controlsParent.GetValue(__instance) as IMyGuiControlsParent;
                var label = parent?.Controls.GetControlByName(BalanceLabelName) as MyGuiControlLabel;
                if (label == null) return;
                label.UserData = null;
                label.Text = MyBankingSystem.GetFormatedValue(balance);
                MyGuiControlBase currency = parent.Controls.GetControlByName(CurrencyImageName);
                if (currency != null) currency.Visible = true;
            }
            catch (Exception ex)
            {
                LogOnce("faction balance: account refresh hook failed: " + ex);
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
