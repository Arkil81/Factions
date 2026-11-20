using System;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Store;

/// <summary>
/// The Store window: what this faction's shop sells, and what is actually on sale right now.
///
/// <para>The lower table is the reason this window exists rather than being a second copy of the
/// Fleet one. A faction can hold shelf stock nothing in its list asked for — everything the managers
/// stocked before the player wrote a line, and anything left over after one is removed — and the
/// Fleet window cannot show it: its census counts unlisted stock, which subtracts exactly this out.
/// Before this window, no surface in the game showed a hull that was for sale.</para>
///
/// <para>Opened from a button on the Assets tab rather than taking a K-menu page of its own, for the
/// reason the retired standing-order window recorded: each added terminal page rescales the whole
/// vanilla tab-button row. See docs/client-plugin.md.</para>
///
/// <para>Draws over its opener rather than replacing it: <see cref="MyGuiScreenBase.CanHideOthers"/>
/// stays false. Data comes from <see cref="ShelfComponent"/> and its own subscription, never from
/// the Assets register — that cache stops refreshing the moment the player leaves the tab this
/// window was opened from.</para>
/// </summary>
internal sealed class ShelfScreen : MyGuiScreenBase
{
    private const int LineColumns = 7;
    private const int CensusColumns = 3;

    /// <summary>The Fleet window's, which took it from the node window: these interior offsets are
    /// the ones confirmed in-game.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static ShelfScreen Instance { get; private set; }

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlTable _lines;
    private MyGuiControlTable _census;
    private MyGuiControlButton _newLine;
    private MyGuiControlButton _remove;
    private MyGuiControlButton _pause;
    private MyGuiControlButton _targetUp;
    private MyGuiControlButton _targetDown;
    private MyGuiControlButton _stocking;
    private MyGuiControlButton _bankSales;

    private int _drawnRevision = -1;

    /// <summary>Id of the selected row, so a snapshot arriving while the player reads one does not
    /// move the selection out from under the action buttons. This window re-snapshots on a cadence
    /// rather than only on an edit, so that happens every few seconds rather than rarely.</summary>
    private string _selectedLineId;

    private ShelfScreen()
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        m_closeOnEsc = true;
        CanBeHidden = true;
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    /// <summary>Opens the window, or does nothing if it is already up. There is no key to retarget
    /// on — a faction has exactly one shelf.</summary>
    internal static void Open()
    {
        try
        {
            if (Instance != null) return;
            MyGuiSandbox.AddScreen(new ShelfScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("store window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.ShelfScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        ShelfComponent component = ShelfComponent.Instance;
        if (component != null) component.SetStreaming(true);
    }

    public override void OnRemoved()
    {
        // The one hook every close path goes through, so the server's subscription is always
        // released. Guarded on still being the current window: SE defers both the removal and the
        // add, so a replaced window can be removed after its successor has already subscribed.
        if (Instance != this) { base.OnRemoved(); return; }

        ShelfComponent component = ShelfComponent.Instance;
        if (component != null) component.SetStreaming(false);
        Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Store");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        // VisibleRowsCount, not Size.Y, decides how tall a table ends up: MyGuiControlTable's
        // RefreshInternals OVERWRITES Size.Y. Everything below a table is placed from its REAL size
        // afterwards, never from a matching constant.
        float linesTop = top + 0.04f;
        _lines = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, linesTop),
            Size = new Vector2(0.86f, 0f),
            Name = "ShelfLines",
            ColumnsCount = LineColumns,
            VisibleRowsCount = 7,
        };
        _lines.SetCustomColumnWidths(new float[LineColumns]
            { 0.08f, 0.30f, 0.10f, 0.10f, 0.11f, 0.09f, 0.22f });
        _lines.SetColumnName(0, new StringBuilder("Id"));
        _lines.SetColumnName(1, new StringBuilder("Hull"));
        _lines.SetColumnName(2, new StringBuilder("Sells as"));
        _lines.SetColumnName(3, new StringBuilder("On sale"));
        _lines.SetColumnName(4, new StringBuilder("Building"));
        _lines.SetColumnName(5, new StringBuilder("Target"));
        _lines.SetColumnName(6, new StringBuilder("State"));
        _lines.ItemSelected += OnLineSelected;
        Controls.Add(_lines);

        // Two rows of three rather than one row of six: a single row of button widths plus gaps has
        // to fit the window's INTERIOR, which is narrower than WindowSize.X.
        var buttonSize = new Vector2(0.16f, 0.04f);
        const float buttonGap = 0.17f;
        float buttonTop = linesTop + _lines.Size.Y + 0.015f;
        _newLine = AddActionButton(new Vector2(left, buttonTop), buttonSize, "New line...",
            "Sell a hull: pick one and say how many to keep on sale.",
            OnNewClicked);
        _remove = AddActionButton(new Vector2(left + buttonGap, buttonTop), buttonSize, "Remove",
            "Stop selling this hull. Copies already on sale are handed back to your fleet over the "
            + "next few store passes rather than destroyed.",
            OnRemoveClicked);
        _pause = AddActionButton(new Vector2(left + buttonGap * 2f, buttonTop), buttonSize, "Pause",
            "Stop restocking this line. It keeps what it already holds on sale — pausing is not the "
            + "same as removing.",
            OnPauseClicked);
        _targetUp = AddActionButton(new Vector2(left, buttonTop + 0.05f), buttonSize, "Target +1",
            "Keep one more of this hull on sale.",
            OnTargetUpClicked);
        _targetDown = AddActionButton(new Vector2(left + buttonGap, buttonTop + 0.05f), buttonSize,
            "Target -1",
            "Keep one fewer. The excess is handed back over the next few store passes.",
            OnTargetDownClicked);
        _stocking = AddActionButton(new Vector2(left + buttonGap * 2f, buttonTop + 0.05f), buttonSize,
            "Stop restocking",
            "Stop your yards building anything for the shop. What is already on sale stays on sale.",
            OnStockingClicked);
        // A fourth column on the second row only: the row has room for it, and it belongs beside
        // restocking -- both decide what leaves the faction through a sale. Opens its own window
        // because the choice is per item, over a list far longer than this one.
        _bankSales = AddActionButton(new Vector2(left + buttonGap * 3f, buttonTop + 0.05f), buttonSize,
            "Bank sales...",
            "Choose which components, weapons, ammo and consumables the central bank may buy from "
            + "you. Keep an item to stockpile it; the rest sells as before.",
            OnBankSalesClicked);

        float censusTop = buttonTop + 0.105f;
        AddSideLabel(new Vector2(left, censusTop), "On sale now");

        _census = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, censusTop + 0.028f),
            Size = new Vector2(0.86f, 0f),
            Name = "ShelfCensus",
            ColumnsCount = CensusColumns,
            VisibleRowsCount = 5,
        };
        _census.SetCustomColumnWidths(new float[CensusColumns] { 0.45f, 0.15f, 0.40f });
        _census.SetColumnName(0, new StringBuilder("Hull"));
        _census.SetColumnName(1, new StringBuilder("On sale"));
        _census.SetColumnName(2, new StringBuilder(""));
        Controls.Add(_census);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawnRevision = -1;
    }

    private void AddSideLabel(Vector2 position, string text)
    {
        Controls.Add(new MyGuiControlLabel(
            position, null, text, null, 0.75f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
    }

    private MyGuiControlButton AddActionButton(
        Vector2 position, Vector2 size, string text, string tooltip, Action<MyGuiControlButton> onClick)
    {
        var button = new MyGuiControlButton(
            position: position,
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: size,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: tooltip,
            text: new StringBuilder(text),
            textScale: 0.7f,
            onButtonClick: onClick)
        {
            Enabled = false,
        };
        Controls.Add(button);
        return button;
    }

    // ---- Frame --------------------------------------------------------------------------------

    public override bool Draw()
    {
        ShelfComponent component = ShelfComponent.Instance;
        if (component != null)
        {
            // Retried every frame rather than only on open: a window opened before the uplink was
            // granted would otherwise stay blank until it was reopened.
            component.SetStreaming(true);
            if (_drawnRevision != component.Revision) Rebuild(component);
            else RefreshStatus(component);
        }
        return base.Draw();
    }

    private void Rebuild(ShelfComponent component)
    {
        _drawnRevision = component.Revision;

        if (!ShelfComponent.Available)
        {
            WriteUnavailable("This server does not offer the store shelf to the client.");
            return;
        }
        if (!component.HasSnapshot)
        {
            WriteUnavailable("Waiting for the server...");
            return;
        }
        if (string.IsNullOrEmpty(component.FactionTag))
        {
            WriteUnavailable("You are not in a faction Factions has registered.");
            return;
        }

        _heading.Text = "[" + component.FactionTag + "]  " + component.Lines.Count
                        + "/" + component.Cap + " shelf lines"
                        + (component.StockingOff ? "   -   RESTOCKING OFF" : "");
        RebuildLines(component);
        RebuildCensus(component);
        RefreshButtons();
        RefreshStatus(component);
    }

    /// <summary>Every state where there is nothing to show. One path, so no half-populated table can
    /// survive any of them.</summary>
    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = "Store";
        if (_lines != null) _lines.Clear();
        if (_census != null) _census.Clear();
        _selectedLineId = null;
        SetButtonsEnabled(false);
        if (_status != null) _status.Text = message;
    }

    private void RebuildLines(ShelfComponent component)
    {
        _lines.Clear();
        for (int i = 0; i < component.Lines.Count; i++)
        {
            WireShelfLine l = component.Lines[i];
            var row = new MyGuiControlTable.Row(l);
            row.AddCell(new MyGuiControlTable.Cell(l.Id ?? ""));
            row.AddCell(new MyGuiControlTable.Cell(l.PrefabId ?? "", null, l.Id + "  " + l.PrefabId));
            row.AddCell(new MyGuiControlTable.Cell(string.IsNullOrEmpty(l.Role) ? "?" : l.Role));
            row.AddCell(new MyGuiControlTable.Cell(l.OnSale.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(l.Building.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(l.TargetCount.ToString()));
            string state = StateColumn(l);
            row.AddCell(new MyGuiControlTable.Cell(state, null, state));
            _lines.Add(row);
        }
        RestoreSelection();
    }

    private void RebuildCensus(ShelfComponent component)
    {
        _census.Clear();
        for (int i = 0; i < component.Census.Count; i++)
        {
            WireShelfCensusRow c = component.Census[i];
            var row = new MyGuiControlTable.Row(c);
            row.AddCell(new MyGuiControlTable.Cell(c.PrefabId ?? "", null, c.PrefabId ?? ""));
            row.AddCell(new MyGuiControlTable.Cell(c.OnSale.ToString()));
            // The marker is the point of this table: stock nothing named is what the shop is holding
            // that the player never asked for, and it is invisible everywhere else.
            row.AddCell(new MyGuiControlTable.Cell(
                c.Unlined ? "no line - Remove hands it back" : ""));
            _census.Add(row);
        }
    }

    /// <summary>Derived here from the counts the snapshot carries rather than sent as a rendered
    /// string: the server already sends every number it is made of, and a sentence on the wire would
    /// be a second place the rule lives.</summary>
    private static string StateColumn(WireShelfLine l)
    {
        if (l.Paused) return "paused";
        if (!string.IsNullOrEmpty(l.BlockedReason)) return "blocked: " + BlockedText(l.BlockedReason);
        if (l.Building > 0) return "building " + l.Building;
        if (l.OnSale >= l.TargetCount) return "at target";
        return "short " + (l.TargetCount - l.OnSale);
    }

    /// <summary>The manager's reason tokens, in words. Each wants a different fix, which is why they
    /// are not collapsed into one "blocked".</summary>
    private static string BlockedText(string reason)
    {
        switch (reason)
        {
            case "unclassed":  return "no cargo, guns or drills - nothing can class it";
            case "route":      return "no yard it can fly out of";
            case "unsellable": return "no station of yours could offer it";
            default:           return reason;
        }
    }

    private void RefreshButtons()
    {
        bool selected = _selectedLineId != null;
        SetButtonsEnabled(selected);

        if (_pause != null)
        {
            WireShelfLine? line = SelectedLine();
            _pause.Text = line.HasValue && line.Value.Paused ? "Resume" : "Pause";
        }
        ShelfComponent component = ShelfComponent.Instance;
        if (_stocking != null && component != null)
            _stocking.Text = component.StockingOff ? "Resume restocking" : "Stop restocking";
    }

    /// <summary><paramref name="enabled"/> gates the four actions that act on the selected row.
    /// "New line...", the stocking switch and "Bank sales..." are deliberately outside it — none needs a selection
    /// — and are gated on the feature being granted.</summary>
    private void SetButtonsEnabled(bool enabled)
    {
        if (_pause != null) _pause.Enabled = enabled;
        if (_remove != null) _remove.Enabled = enabled;
        if (_targetUp != null) _targetUp.Enabled = enabled;
        if (_targetDown != null) _targetDown.Enabled = enabled;
        if (_newLine != null) _newLine.Enabled = ShelfComponent.Available;
        if (_stocking != null) _stocking.Enabled = ShelfComponent.Available;
        if (_bankSales != null) _bankSales.Enabled = ShelfComponent.Available;
    }

    private void RefreshStatus(ShelfComponent component)
    {
        if (_status == null) return;

        // A command's reply outranks the age line while it is fresh: it is the only place a refusal
        // is ever shown.
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds
               < ShelfComponent.CommandMessageSeconds)
        {
            _status.Text = OneLine(component.LastCommandMessage);
            return;
        }

        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        if (component.StockingOff)
        {
            _status.Text = "Restocking is off: nothing is built for the shop. What is listed stays "
                           + "listed   -   updated " + age + "s ago";
            return;
        }
        _status.Text = component.Lines.Count == 0
            ? "No shelf lines: Factions picks what your shop stocks. Add one and it becomes the only "
              + "thing that does   -   updated " + age + "s ago"
            : "Your shelf is the only thing that stocks the shop. A hull your fleet owns is never on "
              + "sale   -   updated " + age + "s ago";
    }

    // ---- Actions ------------------------------------------------------------------------------

    private WireShelfLine? SelectedLine()
    {
        MyGuiControlTable.Row row = _lines != null ? _lines.SelectedRow : null;
        if (row == null || !(row.UserData is WireShelfLine)) return null;
        return (WireShelfLine)row.UserData;
    }

    private void RestoreSelection()
    {
        if (string.IsNullOrEmpty(_selectedLineId)) return;
        for (int i = 0; i < _lines.RowsCount; i++)
        {
            if (!(_lines.GetRow(i).UserData is WireShelfLine)) continue;
            var line = (WireShelfLine)_lines.GetRow(i).UserData;
            if (!string.Equals(line.Id, _selectedLineId, StringComparison.Ordinal)) continue;
            _lines.SelectedRowIndex = i;
            return;
        }
        // Removed while it was selected.
        _selectedLineId = null;
    }

    private void OnLineSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        _selectedLineId = row != null && row.UserData is WireShelfLine
            ? ((WireShelfLine)row.UserData).Id
            : null;
        RefreshButtons();
    }

    private void OnNewClicked(MyGuiControlButton button)
    {
        // The hull pick-list is the Fleet window's, which arrives once per doctrine subscribe. The
        // dialog says so itself when it is not there yet; this button does not gate on it, because
        // gating would make a missing list indistinguishable from a dead button.
        NewShelfLineScreen.Open();
    }

    private void OnPauseClicked(MyGuiControlButton button)
    {
        WireShelfLine? line = SelectedLine();
        if (!line.HasValue) return;
        Send(line.Value.Paused ? WireShelfCommand.Resume : WireShelfCommand.Pause, 0);
    }

    private void OnRemoveClicked(MyGuiControlButton button)
    {
        Send(WireShelfCommand.Remove, 0);
    }

    /// <summary>Target is set absolutely server-side, so a relative button reads the current target
    /// off the row it is acting on. A negative target is refused there.</summary>
    private void OnTargetUpClicked(MyGuiControlButton button)
    {
        SendTargetDelta(1);
    }

    private void OnTargetDownClicked(MyGuiControlButton button)
    {
        SendTargetDelta(-1);
    }

    private void SendTargetDelta(int delta)
    {
        WireShelfLine? line = SelectedLine();
        if (!line.HasValue) return;
        Send(WireShelfCommand.SetTarget, line.Value.TargetCount + delta);
    }

    /// <summary>Shop-wide, so it carries no line id and works with nothing selected.</summary>
    private void OnStockingClicked(MyGuiControlButton button)
    {
        ShelfComponent component = ShelfComponent.Instance;
        if (component == null) return;
        component.SendCommand(
            component.StockingOff ? WireShelfCommand.StockingOn : WireShelfCommand.StockingOff, "", 0);
    }

    private void OnBankSalesClicked(MyGuiControlButton button)
    {
        BankSalesScreen.Open();
    }

    private void Send(WireShelfCommand command, int value)
    {
        ShelfComponent component = ShelfComponent.Instance;
        if (component == null || string.IsNullOrEmpty(_selectedLineId)) return;
        component.SendCommand(command, _selectedLineId, value);
    }

    /// <summary>The runtime's replies are written for chat and can carry newlines; the status line is
    /// a single <see cref="MyGuiControlLabel"/>.</summary>
    private static string OneLine(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
