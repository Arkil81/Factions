using System;
using System.Reflection;
using Factions.Wire;
using FactionsUI.Respawn;
using HarmonyLib;
using Sandbox.Game.Multiplayer;
using Sandbox.Graphics.GUI;
using SpaceEngineers.Game.GUI;
using SpaceEngineers.Game.World;
using VRageMath;

namespace FactionsUI.Patches;

/// <summary>
/// Adds the player's founded nodes to SE's respawn screen (FAC-481), after its medical rooms.
///
/// <para><b>Why rows in SE's own table.</b> The screen already owns the respawn flow end to end —
/// the permanent-death confirmation, the request, the black-out, the close once the character
/// exists, and the reopen when the server refuses. A node row reuses all of it: picking one calls the
/// screen's own <c>CheckPermaDeathAndRespawn</c> and <c>RespawnAtMedicalRoom</c> with
/// <see cref="NodeSpawnSentinel"/> in place of a medical room id, exactly what a medbay row does.
/// The row's user data is <see cref="NodeSpawnRowData"/>, a type every vanilla branch on row data
/// passes by: no preview to stream, no detail panel, no ship cooldown.</para>
///
/// <para><b>Fails closed together.</b> Every member is resolved by name once; if any is missing no
/// patch here binds, so the screen stays vanilla rather than showing a row that cannot be picked.
/// Every hook catches what it throws: this screen is the only way back into the game.</para>
/// </summary>
internal static class RespawnScreenNodeRowsPatch
{
    private const string RowTooltip = "Arrive in a suit beside your faction's station.";

    private static bool _resolved;
    private static bool _ok;
    private static FieldInfo _table;
    private static FieldInfo _button;
    private static FieldInfo _selectedRowData;
    private static FieldInfo _noRespawnText;
    private static FieldInfo _outsideWorldText;
    private static MethodInfo _checkPermaDeath;
    private static MethodInfo _respawnAtMedicalRoom;
    private static MethodInfo _requestConfirmation;
    private static MethodInfo _onTableItemSelected;
    private static bool _subscribed;

    private static bool Resolve()
    {
        if (_resolved) return _ok;
        _resolved = true;
        try
        {
            Type t = typeof(MyGuiScreenMedicals);
            _table = AccessTools.Field(t, "m_respawnsTable");
            _button = AccessTools.Field(t, "m_respawnButton");
            _selectedRowData = AccessTools.Field(t, "m_selectedRowData");
            _noRespawnText = AccessTools.Field(t, "m_noRespawnText");
            _outsideWorldText = AccessTools.Field(t, "m_respawnOutsideWorldText");
            _checkPermaDeath = AccessTools.Method(t, "CheckPermaDeathAndRespawn", new[] { typeof(Action) });
            _respawnAtMedicalRoom = AccessTools.Method(t, "RespawnAtMedicalRoom", new[] { typeof(long) });
            _requestConfirmation = AccessTools.Method(t, "RequestConfirmation", Type.EmptyTypes);
            _onTableItemSelected = AccessTools.Method(t, "OnTableItemSelected",
                new[] { typeof(MyGuiControlTable), typeof(MyGuiControlTable.EventArgs) });
            _ok = _table != null && _button != null && _selectedRowData != null
                  && _noRespawnText != null && _outsideWorldText != null
                  && _checkPermaDeath != null && _respawnAtMedicalRoom != null
                  && _requestConfirmation != null && _onTableItemSelected != null
                  && AccessTools.Method(t, "RefreshMedicalRooms") != null
                  && AccessTools.Method(t, "RefreshRespawnPoints") != null
                  && AccessTools.Method(t, "onRespawnClick") != null
                  && AccessTools.Method(t, "UpdateSpawnTimes") != null;
        }
        catch (Exception ex)
        {
            Plugin.Log("respawn node rows: resolving the respawn screen threw: " + ex);
            _ok = false;
        }
        Plugin.Log(_ok
            ? "respawn node rows: respawn screen hooks bound"
            : "respawn node rows: respawn screen hooks did not resolve -- the respawn screen stays vanilla");
        return _ok;
    }

    /// <summary>Vanilla asks the server for its respawn points: ask for the node list alongside.</summary>
    [HarmonyPatch(typeof(MyGuiScreenMedicals), "RefreshRespawnPoints")]
    internal static class Refresh
    {
        [HarmonyPrepare]
        private static bool Prepare() => Resolve();

        private static void Postfix()
        {
            try
            {
                NodeSpawnComponent component = NodeSpawnComponent.Instance;
                if (component == null) return;
                if (!_subscribed)
                {
                    component.RowsChanged += OnRowsChanged;
                    _subscribed = true;
                }
                component.RequestList();
            }
            catch (Exception ex)
            {
                Plugin.Log("respawn node rows: list request failed: " + ex);
            }
        }
    }

    /// <summary>Vanilla has just rebuilt the table from the server's answer: put the node rows back.</summary>
    [HarmonyPatch(typeof(MyGuiScreenMedicals), "RefreshMedicalRooms")]
    internal static class Rebuild
    {
        [HarmonyPrepare]
        private static bool Prepare() => Resolve();

        private static void Postfix(MyGuiScreenMedicals __instance) => Apply(__instance);
    }

    /// <summary>Picking a node row: the screen's own medbay path, naming the sentinel.</summary>
    [HarmonyPatch(typeof(MyGuiScreenMedicals), "onRespawnClick")]
    internal static class Click
    {
        [HarmonyPrepare]
        private static bool Prepare() => Resolve();

        private static bool Prefix(MyGuiScreenMedicals __instance)
        {
            try
            {
                var table = _table.GetValue(__instance) as MyGuiControlTable;
                if (!(table?.SelectedRow?.UserData is NodeSpawnRowData row)) return true;
                if (row.Blocked(DateTime.UtcNow)) return false;
                long sentinel = NodeSpawnSentinel.Encode(row.Wire.StationId);
                if (sentinel == 0) return false;

                Action respawn = () =>
                {
                    _respawnAtMedicalRoom.Invoke(__instance, new object[] { sentinel });
                    // As the suit row does: the arrival streams in fresh, with no preview before it.
                    if (!Sync.IsServer) _requestConfirmation.Invoke(__instance, null);
                    NodeSpawnComponent.Instance?.MarkSent(row);
                };
                _checkPermaDeath.Invoke(__instance, new object[] { respawn });
            }
            catch (Exception ex)
            {
                Plugin.Log("respawn node rows: respawn click failed: " + ex);
            }
            return false;
        }
    }

    /// <summary>A blocked row cannot be picked; one whose cooldown has run out can.</summary>
    [HarmonyPatch(typeof(MyGuiScreenMedicals), "HandleInputAfterSimulation")]
    internal static class ButtonState
    {
        [HarmonyPrepare]
        private static bool Prepare() => Resolve();

        private static void Postfix(MyGuiScreenMedicals __instance)
        {
            try
            {
                var table = _table.GetValue(__instance) as MyGuiControlTable;
                if (!(table?.SelectedRow?.UserData is NodeSpawnRowData row)) return;
                if (_button.GetValue(__instance) is MyGuiControlButton button)
                    button.Enabled = !row.Blocked(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Plugin.Log("respawn node rows: button state failed: " + ex);
            }
        }
    }

    /// <summary>Vanilla's per-frame cooldown labels: count the node rows down too.</summary>
    [HarmonyPatch(typeof(MyGuiScreenMedicals), "UpdateSpawnTimes")]
    internal static class Countdown
    {
        [HarmonyPrepare]
        private static bool Prepare() => Resolve();

        private static void Postfix(MyGuiScreenMedicals __instance)
        {
            try
            {
                var table = _table.GetValue(__instance) as MyGuiControlTable;
                if (table == null) return;
                DateTime now = DateTime.UtcNow;
                for (int i = 0; i < table.RowsCount; i++)
                {
                    MyGuiControlTable.Row r = table.GetRow(i);
                    if (!(r.UserData is NodeSpawnRowData row) || row.Wire.Block == 0) continue;
                    Color? colour = row.Blocked(now) ? Color.Gray : (Color?)null;
                    MyGuiControlTable.Cell name = r.GetCell(0);
                    MyGuiControlTable.Cell status = r.GetCell(1);
                    status.Text.Clear().Append(row.Status(now));
                    name.TextColor = colour;
                    status.TextColor = colour;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log("respawn node rows: countdown failed: " + ex);
            }
        }
    }

    private static void OnRowsChanged()
    {
        MyGuiScreenMedicals screen = MyGuiScreenMedicals.Static;
        if (screen != null) Apply(screen);
    }

    /// <summary>Replaces the node rows in the screen's table with the last list, after the medical
    /// rooms, and keeps the player's selection where it was.</summary>
    private static void Apply(MyGuiScreenMedicals screen)
    {
        try
        {
            var table = _table.GetValue(screen) as MyGuiControlTable;
            if (table == null) return;   // the faction-picker variant of the screen has no respawn table

            MyGuiControlTable.Row selected = table.SelectedRow;
            object selectedData = _selectedRowData.GetValue(screen);
            // Not table.Remove(predicate): SE's removes only the first match, so a list arriving
            // without a vanilla rebuild in between left every other node row in place.
            for (int i = table.RowsCount - 1; i >= 0; i--)
            {
                MyGuiControlTable.Row r = table.GetRow(i);
                if (r.UserData is NodeSpawnRowData) table.Remove(r);
            }

            NodeSpawnComponent component = NodeSpawnComponent.Instance;
            if (component != null && NodeSpawnComponent.Available)
            {
                int at = 0;
                while (at < table.RowsCount
                       && table.GetRow(at).UserData is MySpaceRespawnComponent.MyRespawnPointInfo)
                    at++;
                DateTime now = DateTime.UtcNow;
                foreach (NodeSpawnRowData row in component.Rows)
                    table.Insert(at++, BuildRow(row, now));
            }

            int index = -1;
            if (selectedData is NodeSpawnRowData was)
                index = table.FindIndex(r => r.UserData is NodeSpawnRowData d && d.Wire.NodeId == was.Wire.NodeId);
            if (index < 0 && selected != null)
                index = table.FindIndex(r => ReferenceEquals(r, selected));

            if (index >= 0)
            {
                table.SelectedRowIndex = index;
            }
            else if (table.RowsCount > 0 && table.SelectedRow == null)
            {
                // Vanilla found nothing to offer and selected nothing; the node rows are the offer.
                table.SelectedRowIndex = 0;
                _onTableItemSelected.Invoke(screen, new object[] { table, default(MyGuiControlTable.EventArgs) });
            }

            if (table.RowsCount > 0)
            {
                if (_noRespawnText.GetValue(screen) is MyGuiControlMultilineText none) none.Visible = false;
                if (_outsideWorldText.GetValue(screen) is MyGuiControlMultilineText outside) outside.Visible = false;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log("respawn node rows: applying the node rows failed: " + ex);
        }
    }

    private static MyGuiControlTable.Row BuildRow(NodeSpawnRowData row, DateTime now)
    {
        Color? colour = row.Blocked(now) ? Color.Gray : (Color?)null;
        var r = new MyGuiControlTable.Row(row, RowTooltip);
        r.AddCell(new MyGuiControlTable.Cell(row.Wire.DisplayName + " (" + SiteText(row.Wire.Site) + ")",
            null, RowTooltip, colour));
        r.AddCell(new MyGuiControlTable.Cell(row.Status(now), null, RowTooltip, colour));
        return r;
    }

    private static string SiteText(byte site)
    {
        switch (site)
        {
            case 2:  return "surface";
            case 1:  return "orbit";
            default: return "space";
        }
    }
}
