using System;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Fleet;

/// <summary>
/// The Refits window off the Fleet window: every design the faction has hulls of on an older
/// version, or a refit set for, and how many of its parked hulls the yards may refit to the latest
/// at once. The rows ride the Fleet window's own subscription (<see cref="FleetDoctrineComponent.Refits"/>);
/// a cap change is the same command the chat verb <c>!factions fleet refit</c> runs.
/// </summary>
internal sealed class HullRefitScreen : MyGuiScreenBase
{
    private const int Columns = 6;

    private static readonly Vector2 WindowSize = new Vector2(0.72f, 0.56f);

    internal static HullRefitScreen Instance { get; private set; }

    private MyGuiControlLabel _status;
    private MyGuiControlTable _rows;
    private MyGuiControlButton _capUp;
    private MyGuiControlButton _capDown;
    private MyGuiControlButton _stop;

    private int _drawnRevision = -1;
    private string _selectedFamily;

    private HullRefitScreen()
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        m_closeOnEsc = true;
        CanBeHidden = true;
        // The Fleet window behind this one keeps drawing and owns the subscription.
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    internal static void Open()
    {
        try
        {
            if (Instance != null) return;
            MyGuiSandbox.AddScreen(new HullRefitScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("refits window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.HullRefitScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
    }

    public override void OnRemoved()
    {
        if (Instance == this) Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Refits");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        Controls.Add(new MyGuiControlMultilineText(
            new Vector2(left, top), new Vector2(WindowSize.X - 0.04f, 0.06f), null, "Blue", 0.7f,
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Text = new StringBuilder("A refit takes a parked hull at a shipyard, collects only what the newer "
                + "version adds, and credits back what it replaces. Cap is how many are in refit at once."),
        });

        _rows = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, top + 0.07f),
            Size = new Vector2(WindowSize.X - 0.04f, 0f),
            Name = "HullRefits",
            ColumnsCount = Columns,
            VisibleRowsCount = 7,
        };
        _rows.SetCustomColumnWidths(new float[Columns] { 0.24f, 0.24f, 0.10f, 0.12f, 0.15f, 0.15f });
        _rows.SetColumnName(0, new StringBuilder("Design"));
        _rows.SetColumnName(1, new StringBuilder("Refit to"));
        _rows.SetColumnName(2, new StringBuilder("Cap"));
        _rows.SetColumnName(3, new StringBuilder("In refit"));
        _rows.SetColumnName(4, new StringBuilder("Old parked"));
        _rows.SetColumnName(5, new StringBuilder("At yards"));
        _rows.ItemSelected += OnRowSelected;
        Controls.Add(_rows);

        var buttonSize = new Vector2(0.16f, 0.04f);
        const float buttonGap = 0.17f;
        float buttonTop = top + 0.07f + _rows.Size.Y + 0.015f;
        _capUp = AddButton(new Vector2(left, buttonTop), buttonSize, "Cap +1",
            "Refit one more of this design's hulls at a time.", OnCapUpClicked);
        _capDown = AddButton(new Vector2(left + buttonGap, buttonTop), buttonSize, "Cap -1",
            "One fewer at a time. Hulls already in refit finish.", OnCapDownClicked);
        _stop = AddButton(new Vector2(left + buttonGap * 2f, buttonTop), buttonSize, "Stop",
            "Take no more of this design into refit. Hulls already in refit finish.", OnStopClicked);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);
        SetButtonsEnabled(false);
    }

    private MyGuiControlButton AddButton(Vector2 position, Vector2 size, string text, string tooltip,
                                         Action<MyGuiControlButton> onClick)
    {
        var button = new MyGuiControlButton(
            position: position,
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: size,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: tooltip,
            text: new StringBuilder(text),
            textScale: 0.7f,
            onButtonClick: onClick);
        Controls.Add(button);
        return button;
    }

    // ---- Frame --------------------------------------------------------------------------------

    public override bool Draw()
    {
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        if (component != null)
        {
            component.SetStreaming(true);
            if (_drawnRevision != component.Revision) Rebuild(component);
        }
        return base.Draw();
    }

    private static string BlockedText(string blocked)
    {
        if (string.IsNullOrEmpty(blocked)) return null;
        if (blocked == "banned") return "banned on this server";
        if (blocked == "over_ceiling") return "over this server's performance limit";
        return blocked;
    }

    private void Rebuild(FleetDoctrineComponent component)
    {
        _drawnRevision = component.Revision;
        _rows.Clear();
        MyGuiControlTable.Row reselect = null;
        foreach (WireHullRefitRow r in component.Refits)
        {
            var row = new MyGuiControlTable.Row(r);
            row.AddCell(new MyGuiControlTable.Cell(r.Family ?? "", null, r.Family));
            // A family whose latest version this server will not build is never refit; say so here
            // rather than leave a cap that silently takes nothing.
            string blocked = BlockedText(r.Blocked);
            row.AddCell(new MyGuiControlTable.Cell(
                blocked == null ? (r.Latest ?? "") : (r.Latest ?? "") + "  (" + blocked + ")", null,
                blocked == null ? r.Latest : r.Latest + ": " + blocked + ", so no hull is refit to it."));
            row.AddCell(new MyGuiControlTable.Cell(r.Cap > 0 ? r.Cap.ToString() : "-"));
            row.AddCell(new MyGuiControlTable.Cell(r.InRefit.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(r.OutdatedParked.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(r.OutdatedAtYards.ToString()));
            _rows.Add(row);
            if (string.Equals(r.Family, _selectedFamily, StringComparison.OrdinalIgnoreCase)) reselect = row;
        }
        if (reselect != null) _rows.SelectedRow = reselect;
        else _selectedFamily = null;
        SetButtonsEnabled(_selectedFamily != null && FleetDoctrineComponent.Available);

        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds < FleetDoctrineComponent.CommandMessageSeconds)
            _status.Text = component.LastCommandMessage.Replace("\r", " ").Replace("\n", " ").Trim();
        else
            _status.Text = component.Refits.Count == 0
                ? "No hull is on an older version of its design."
                : "Select a design to set how many of its hulls are refit at once.";
    }

    private void SetButtonsEnabled(bool enabled)
    {
        if (_capUp != null) _capUp.Enabled = enabled;
        if (_capDown != null) _capDown.Enabled = enabled;
        if (_stop != null) _stop.Enabled = enabled;
    }

    // ---- Actions ------------------------------------------------------------------------------

    private void OnRowSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        _selectedFamily = row != null && row.UserData is WireHullRefitRow
            ? ((WireHullRefitRow)row.UserData).Family
            : null;
        SetButtonsEnabled(_selectedFamily != null && FleetDoctrineComponent.Available);
    }

    private WireHullRefitRow? Selected()
    {
        MyGuiControlTable.Row row = _rows != null ? _rows.SelectedRow : null;
        if (row == null || !(row.UserData is WireHullRefitRow)) return null;
        return (WireHullRefitRow)row.UserData;
    }

    private void OnCapUpClicked(MyGuiControlButton button) { SendCap(+1); }

    private void OnCapDownClicked(MyGuiControlButton button) { SendCap(-1); }

    private void OnStopClicked(MyGuiControlButton button)
    {
        WireHullRefitRow? row = Selected();
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        if (!row.HasValue || component == null) return;
        component.SendCommand(WireDoctrineCommand.SetRefitCap, row.Value.Family, 0);
    }

    /// <summary>Sends the new cap rather than a delta, computed from the row on screen; the
    /// snapshot that follows shows what the server made of it.</summary>
    private void SendCap(int delta)
    {
        WireHullRefitRow? row = Selected();
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        if (!row.HasValue || component == null) return;
        int cap = Math.Max(0, row.Value.Cap + delta);
        component.SendCommand(WireDoctrineCommand.SetRefitCap, row.Value.Family, cap);
    }
}
