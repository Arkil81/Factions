using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Graphics.GUI;

namespace FactionsUI.Patches;

/// <summary>
/// Keeps the assembler terminal's blueprint list where the player scrolled it while the queue runs.
///
/// <para>Vanilla <c>MyTerminalProductionController.RefreshQueue</c> runs on every
/// <c>QueueChanged</c>, which a working assembler raises once per finished unit. With a queue item
/// selected, its <c>Clear</c> nulls the selection and restoring it raises <c>ItemSelected</c>, whose
/// handler <c>queueGrid_FocusChanged</c> calls <c>RefreshBlueprints</c>. That clears the blueprint
/// grid to zero rows, the scrollable panel hides its scrollbar and sets it to 0, and the list jumps
/// to the top on every unit. The list's content does not depend on the queue, so the offset it had
/// before the handler is put back after it. A blueprint-class switch or a search still rebuilds
/// from the top: they go through other handlers.</para>
///
/// <para>The controller is internal, so the handler and the panel field are resolved by name;
/// <c>Prepare</c> skips the patch with one log line on a miss.</para>
/// </summary>
[HarmonyPatch]
internal static class ProductionBlueprintScrollPatch
{
    private const string ControllerTypeName = "Sandbox.Game.Gui.MyTerminalProductionController";

    private static bool _resolved;
    private static MethodBase _handler;
    private static FieldInfo _blueprintsArea;

    private static bool Resolve()
    {
        if (_resolved) return _handler != null;
        _resolved = true;
        try
        {
            Type controller = AccessTools.TypeByName(ControllerTypeName);
            if (controller != null)
            {
                _handler = AccessTools.Method(controller, "queueGrid_FocusChanged",
                    new[] { typeof(MyGuiControlGrid), typeof(MyGuiControlGrid.EventArgs) });
                _blueprintsArea = AccessTools.Field(controller, "m_blueprintsArea");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log("blueprint scroll: resolving the production controller threw: " + ex);
        }
        if (_blueprintsArea == null || !typeof(MyGuiControlScrollablePanel).IsAssignableFrom(_blueprintsArea.FieldType))
            _handler = null;
        if (_handler == null)
            Plugin.Log("blueprint scroll: production controller hooks did not resolve; the blueprint list resets on queue changes as in vanilla.");
        return _handler != null;
    }

    [HarmonyPrepare]
    private static bool Prepare() => Resolve();

    [HarmonyTargetMethod]
    private static MethodBase Target() => Resolve() ? _handler : null;

    [HarmonyPrefix]
    // ReSharper disable once UnusedMember.Local
    private static void Prefix(object __instance, out float __state)
    {
        __state = Area(__instance)?.ScrollbarVPosition ?? 0f;
    }

    [HarmonyPostfix]
    // ReSharper disable once UnusedMember.Local
    private static void Postfix(object __instance, float __state)
    {
        MyGuiControlScrollablePanel area = Area(__instance);
        if (area != null && __state > 0f && area.ScrollbarVPosition != __state)
            area.ScrollbarVPosition = __state;
    }

    private static MyGuiControlScrollablePanel Area(object controller)
    {
        if (controller == null) return null;
        try { return _blueprintsArea.GetValue(controller) as MyGuiControlScrollablePanel; }
        catch { return null; }
    }
}
