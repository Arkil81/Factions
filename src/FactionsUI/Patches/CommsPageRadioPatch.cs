using System;
using System.Reflection;
using FactionsUI.Radio;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;

namespace FactionsUI.Patches;

/// <summary>
/// After vanilla builds the Comms page, add <see cref="CommsPageRadio"/>'s <b>Radio...</b> button.
///
/// <para><c>CreateChatPageControls</c> is private, so it is resolved by name, and <c>Prepare</c>
/// returns false on a miss: the patch is skipped with its own log line instead of throwing, which
/// would fail its patch group (<c>HarmonyPatcher</c>).</para>
///
/// <para><b>Fails closed.</b> It runs inside the terminal's construction, where an exception takes the
/// whole K menu down. Anything thrown removes the button and gives the Factions list its rows back.</para>
/// </summary>
[HarmonyPatch]
internal static class CommsPageRadioPatch
{
    private static bool _resolved;
    private static MethodBase _pageBuild;
    private static bool _failureLogged;

    private static MethodBase Resolve()
    {
        if (_resolved) return _pageBuild;
        _resolved = true;
        try
        {
            _pageBuild = AccessTools.Method(typeof(MyGuiScreenTerminal), "CreateChatPageControls",
                new[] { typeof(MyGuiControlTabPage) });
        }
        catch (Exception ex)
        {
            Plugin.Log("radio button: resolving the Comms page build threw: " + ex);
        }
        if (_pageBuild == null) Plugin.Log("radio button: Comms page build did not resolve; the Comms page stays vanilla.");
        return _pageBuild;
    }

    [HarmonyPrepare]
    private static bool Prepare() => Resolve() != null;

    [HarmonyTargetMethod]
    private static MethodBase Target() => Resolve();

    [HarmonyPostfix]
    // ReSharper disable once UnusedMember.Local
    private static void Postfix(MyGuiControlTabPage chatPage)
    {
        if (chatPage == null) return;
        int rows = 0;
        try
        {
            rows = CommsPageRadio.Rows(chatPage);
            CommsPageRadio.Build(chatPage);
        }
        catch (Exception ex)
        {
            try { CommsPageRadio.Remove(chatPage, rows); }
            catch { }
            if (_failureLogged) return;
            _failureLogged = true;
            Plugin.Log("radio button: Comms page build failed, page left vanilla: " + ex);
        }
    }
}
