using System;
using System.Reflection;
using FactionsUI.Diplomacy;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game.ModAPI;

namespace FactionsUI.Patches;

/// <summary>
/// The two hooks behind <see cref="FactionsPageReadout"/>: the Factions page build, which adds the
/// stance lines and the Diplomacy... button, and the controller's <c>RefreshFactionProperties</c>,
/// which reports which faction is selected.
///
/// <para><b>Both targets are resolved by name.</b> <c>MyTerminalFactionController</c> is internal,
/// and <c>CreateFactionsPageControls</c> is private. Each patch's <c>Prepare</c> returns false when
/// anything it needs does not resolve — the method, or the controller's two faction fields — so a
/// miss skips that patch instead of throwing, which would fail the page's patch group. A miss is
/// logged once, at load.</para>
///
/// <para><b>They fail closed together.</b> The page build adds nothing unless the selection hook is
/// also bound: a readout that never learns the selection would sit on the page showing the first
/// faction's stance under every other one. And an exception while building removes whatever was
/// added, leaving the page as vanilla built it — the page is how a player leaves or joins a faction,
/// so it must survive anything thrown here.</para>
/// </summary>
internal static class FactionsPageDiplomacyPatch
{
    private const string ControllerTypeName = "Sandbox.Game.Gui.MyTerminalFactionController";

    private static bool _resolved;
    private static MethodBase _pageBuild;
    private static MethodBase _refresh;
    private static FieldInfo _selectedFaction;
    private static FieldInfo _userFaction;
    private static bool _failureLogged;

    /// <summary>Resolves every member both patches need, once. Never throws.</summary>
    private static bool Resolve()
    {
        if (_resolved) return _pageBuild != null && _refresh != null && _selectedFaction != null && _userFaction != null;
        _resolved = true;
        try
        {
            _pageBuild = AccessTools.Method(typeof(MyGuiScreenTerminal), "CreateFactionsPageControls",
                new[] { typeof(MyGuiControlTabPage) });
            Type controller = AccessTools.TypeByName(ControllerTypeName);
            if (controller != null)
            {
                _refresh = AccessTools.Method(controller, "RefreshFactionProperties", Type.EmptyTypes);
                _selectedFaction = AccessTools.Field(controller, "m_selectedFaction");
                _userFaction = AccessTools.Field(controller, "m_userFaction");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log("diplomacy readout: resolving the Factions page hooks threw: " + ex);
        }

        bool ok = _pageBuild != null && _refresh != null
                  && _selectedFaction != null && typeof(IMyFaction).IsAssignableFrom(_selectedFaction.FieldType)
                  && _userFaction != null && typeof(IMyFaction).IsAssignableFrom(_userFaction.FieldType);
        Plugin.Log(ok
            ? "diplomacy readout: Factions page hooks bound"
            : "diplomacy readout: Factions page hooks did not resolve (page build " + (_pageBuild != null)
              + ", refresh " + (_refresh != null) + ", selected field " + (_selectedFaction != null)
              + ", user field " + (_userFaction != null) + ") -- the Factions page stays vanilla; "
              + "!factions diplomacy still works in chat.");
        if (!ok) { _pageBuild = null; _refresh = null; }
        return ok;
    }

    /// <summary>After vanilla builds the Factions page: add the readout's controls.</summary>
    [HarmonyPatch]
    internal static class PageBuild
    {
        [HarmonyPrepare]
        private static bool Prepare() => Resolve();

        [HarmonyTargetMethod]
        private static MethodBase Target() => Resolve() ? _pageBuild : null;

        [HarmonyPostfix]
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(MyGuiControlTabPage page)
        {
            FactionsPageReadout.Detach();
            FactionsPageOverview.Detach();
            if (page == null) return;
            if (DiplomacyComponent.Available)
            {
                try
                {
                    FactionsPageReadout.Build(page);
                }
                catch (Exception ex)
                {
                    try { FactionsPageReadout.Remove(page); }
                    catch { FactionsPageReadout.Detach(); }
                    LogOnce("diplomacy readout: Factions page build failed, page left vanilla: " + ex);
                }
            }
            if (FactionIntelComponent.Available)
            {
                try
                {
                    FactionsPageOverview.Build(page);
                }
                catch (Exception ex)
                {
                    try { FactionsPageOverview.Remove(page); }
                    catch { FactionsPageOverview.Detach(); }
                    LogOnce("factions overview: Factions page build failed, member table left as vanilla: " + ex);
                }
            }
        }
    }

    /// <summary>After every vanilla refresh of the selected faction's panel: report the selection.</summary>
    [HarmonyPatch]
    internal static class SelectionRefresh
    {
        [HarmonyPrepare]
        private static bool Prepare() => Resolve();

        [HarmonyTargetMethod]
        private static MethodBase Target() => Resolve() ? _refresh : null;

        [HarmonyPostfix]
        // ReSharper disable once UnusedMember.Local
        private static void Postfix(object __instance)
        {
            if ((!FactionsPageReadout.Attached && !FactionsPageOverview.Attached) || __instance == null) return;
            try
            {
                var selected = _selectedFaction.GetValue(__instance) as IMyFaction;
                var user = _userFaction.GetValue(__instance) as IMyFaction;
                FactionsPageReadout.OnSelection(selected, user);
                FactionsPageOverview.OnSelection(selected);
            }
            catch (Exception ex)
            {
                // Hide rather than show a stance or an estimate for a faction that may not be the
                // selected one.
                try { FactionsPageReadout.OnSelection(null, null); }
                catch { }
                try { FactionsPageOverview.OnSelection(null); }
                catch { }
                LogOnce("diplomacy readout: selection hook failed: " + ex);
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
