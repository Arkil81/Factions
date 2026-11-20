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
/// The Fleet window: what this faction's fleet is made of, what it is waiting on, and what it
/// actually owns.
///
/// <para>While the doctrine is non-empty it is the only thing that builds FLEET hulls for a player
/// faction, and a role with no line is not built at all (docs/fleet-doctrine-plan.md). That makes
/// the two tables under the doctrine load-bearing rather than decorative: <b>Waiting on hulls</b> is
/// the work no line answers — the silence the ceiling produces, made visible — and <b>Owned hulls</b>
/// is every hull the faction holds, so stock nothing names is visible as stock rather than only as
/// an absence from the list above.</para>
///
/// <para>Opened from a button on the Assets tab rather than taking a K-menu page of its own, for the
/// reason the retired standing-order window recorded: each added terminal page rescales the whole
/// vanilla tab-button row. See docs/client-plugin.md.</para>
///
/// <para>Draws over its opener rather than replacing it: <see cref="MyGuiScreenBase.CanHideOthers"/>
/// stays false, so the terminal is still behind it when it closes. Data comes from
/// <see cref="FleetDoctrineComponent"/> and its own subscription, never from the Assets register —
/// that cache stops refreshing the moment the player leaves the tab this window was opened
/// from.</para>
/// </summary>
internal sealed class FleetDoctrineScreen : MyGuiScreenBase
{
    private const int LineColumns = 10;
    private const int PressureColumns = 3;
    private const int CensusColumns = 4;

    /// <summary>Deliberately the retired standing-order window's, which took it from
    /// <see cref="Nodes.NodeDetailScreen"/>: these interior offsets are the ones confirmed in-game,
    /// and the one window that invented its own put a button outside its own frame.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static FleetDoctrineScreen Instance { get; private set; }

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlTable _lines;
    private MyGuiControlTable _pressure;
    private MyGuiControlTable _census;
    private MyGuiControlLabel _censusCaption;
    private MyGuiControlButton _newLine;
    private MyGuiControlButton _remove;
    private MyGuiControlButton _priorityUp;
    private MyGuiControlButton _priorityDown;
    private MyGuiControlButton _pause;
    private MyGuiControlButton _targetUp;
    private MyGuiControlButton _targetDown;
    private MyGuiControlButton _tactics;
    private MyGuiControlButton _refits;
    private MyGuiControlButton _decommission;
    /// <summary>The census row's prefab, held across rebuilds as the line selection is.</summary>
    private string _selectedCensusId;

    private int _drawnRevision = -1;

    /// <summary>Id of the selected row, so a snapshot arriving while the player reads one does not
    /// move the selection out from under the action buttons. This window re-snapshots on a cadence
    /// rather than only on an edit, so that happens every few seconds rather than rarely.</summary>
    private string _selectedLineId;

    private FleetDoctrineScreen()
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        m_closeOnEsc = true;
        CanBeHidden = true;
        // Left false on purpose: the terminal behind this window keeps drawing.
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    /// <summary>Opens the window, or does nothing if it is already up. There is no key to retarget
    /// on — a faction has exactly one doctrine.</summary>
    internal static void Open()
    {
        try
        {
            if (Instance != null) return;
            MyGuiSandbox.AddScreen(new FleetDoctrineScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("fleet window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.FleetDoctrineScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        if (component != null) component.SetStreaming(true);
    }

    public override void OnRemoved()
    {
        // The one hook every close path goes through, so the server's subscription is always
        // released. Guarded on still being the current window for the reason the node window
        // records: SE defers both the removal and the add, so a replaced window can be removed after
        // its successor has already subscribed.
        if (Instance != this) { base.OnRemoved(); return; }

        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        if (component != null) component.SetStreaming(false);
        Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Fleet");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        // VisibleRowsCount, not Size.Y, decides how tall a table ends up: MyGuiControlTable's
        // RefreshInternals OVERWRITES Size.Y with RowHeight * (VisibleRowsCount + 1) plus its
        // texture minimum. Everything below a table is therefore placed from its REAL size
        // afterwards, never from a matching constant.
        float linesTop = top + 0.04f;
        _lines = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, linesTop),
            Size = new Vector2(0.86f, 0f),
            Name = "FleetDoctrineLines",
            ColumnsCount = LineColumns,
            VisibleRowsCount = 6,
        };
        _lines.SetCustomColumnWidths(new float[LineColumns]
            { 0.07f, 0.10f, 0.20f, 0.07f, 0.08f, 0.09f, 0.08f, 0.08f, 0.09f, 0.14f });
        _lines.SetColumnName(0, new StringBuilder("Pri"));
        _lines.SetColumnName(1, new StringBuilder("Fits"));
        _lines.SetColumnName(2, new StringBuilder("Hull"));
        _lines.SetColumnName(3, new StringBuilder("Have"));
        _lines.SetColumnName(4, new StringBuilder("Flying"));
        _lines.SetColumnName(5, new StringBuilder("Building"));
        _lines.SetColumnName(6, new StringBuilder("Repair"));
        _lines.SetColumnName(7, new StringBuilder("Target"));
        _lines.SetColumnName(8, new StringBuilder("Scope"));
        _lines.SetColumnName(9, new StringBuilder("State"));
        _lines.ItemSelected += OnLineSelected;
        Controls.Add(_lines);

        // Two rows of four rather than one row of seven: a single row of button widths plus gaps has
        // to fit the window's INTERIOR, which is narrower than WindowSize.X.
        var buttonSize = new Vector2(0.16f, 0.04f);
        const float buttonGap = 0.17f;
        float buttonTop = linesTop + _lines.Size.Y + 0.015f;
        _newLine = AddActionButton(new Vector2(left, buttonTop), buttonSize, "New line...",
            "Add a line: one hull, and how many of it your faction should own.",
            OnNewClicked);
        _remove = AddActionButton(new Vector2(left + buttonGap, buttonTop), buttonSize, "Remove",
            "Delete this line. Ids are never reused, so the others keep theirs. Removing the last "
            + "line hands hull choice back to Factions.",
            OnRemoveClicked);
        _priorityUp = AddActionButton(new Vector2(left + buttonGap * 2f, buttonTop), buttonSize, "Priority +1",
            "Raise this line. Higher rank is evaluated first and wins a shipyard two lines both want.",
            OnPriorityUpClicked);
        _priorityDown = AddActionButton(new Vector2(left + buttonGap * 3f, buttonTop), buttonSize, "Priority -1",
            "Lower this line against the others.",
            OnPriorityDownClicked);
        _pause = AddActionButton(new Vector2(left, buttonTop + 0.05f), buttonSize, "Pause",
            "Stop evaluating this line. It keeps its id and rank, and stops answering demand for the "
            + "roles its hull fits — the work then shows under Waiting on hulls.",
            OnPauseClicked);
        _targetUp = AddActionButton(new Vector2(left + buttonGap, buttonTop + 0.05f), buttonSize, "Target +1",
            "Own one more of this hull. The target is a ceiling as well as a floor: nothing builds "
            + "past it however much work is waiting.",
            OnTargetUpClicked);
        _targetDown = AddActionButton(new Vector2(left + buttonGap * 2f, buttonTop + 0.05f), buttonSize,
            "Target -1",
            "Own one fewer. Lowering it scraps nothing — it stops replacing losses.",
            OnTargetDownClicked);
        _tactics = AddActionButton(new Vector2(left + buttonGap * 3f, buttonTop + 0.05f), buttonSize,
            "Tactics...",
            "What your hulls have learned about fighting each opponent: the tactic they lean to, how "
            + "sure they are, and the fights they learned it from.",
            OnTacticsClicked);
        _refits = AddActionButton(new Vector2(left + buttonGap * 4f, buttonTop), buttonSize, "Refits...",
            "Hulls on an older version of their design, and refitting them to the latest a few at a "
            + "time on the repair queue.",
            OnRefitsClicked);
        _decommission = AddActionButton(new Vector2(left + buttonGap * 4f, buttonTop + 0.05f), buttonSize,
            "Decommission...",
            "Dismantle every hull of the design selected under Owned hulls, every version: parked, on the "
            + "shelf and in repair or refit now, and the ones flying when they come home. Their components "
            + "go to the node that holds each, and any doctrine or shelf line naming it is removed. "
            + "Cannot be undone.",
            OnDecommissionClicked);

        float sideTop = buttonTop + 0.105f;
        const float sideWidth = 0.42f;
        float rightLeft = left + sideWidth + 0.02f;

        AddSideLabel(new Vector2(left, sideTop), "Waiting on hulls (no line)");
        _censusCaption = AddSideLabel(new Vector2(rightLeft, sideTop), "Owned hulls");

        float sideTableTop = sideTop + 0.028f;
        _pressure = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, sideTableTop),
            Size = new Vector2(sideWidth, 0f),
            Name = "FleetDoctrinePressure",
            ColumnsCount = PressureColumns,
            VisibleRowsCount = 4,
        };
        _pressure.SetCustomColumnWidths(new float[PressureColumns] { 0.25f, 0.45f, 0.30f });
        _pressure.SetColumnName(0, new StringBuilder("Role"));
        _pressure.SetColumnName(1, new StringBuilder("Node"));
        _pressure.SetColumnName(2, new StringBuilder("Demands"));
        Controls.Add(_pressure);

        _census = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(rightLeft, sideTableTop),
            Size = new Vector2(sideWidth, 0f),
            Name = "FleetDoctrineCensus",
            ColumnsCount = CensusColumns,
            VisibleRowsCount = 4,
        };
        _census.SetCustomColumnWidths(new float[CensusColumns] { 0.43f, 0.19f, 0.19f, 0.19f });
        _census.SetColumnName(0, new StringBuilder("Hull"));
        _census.SetColumnName(1, new StringBuilder("Parked"));
        _census.SetColumnName(2, new StringBuilder("Flying"));
        _census.SetColumnName(3, new StringBuilder("Repair"));
        _census.ItemSelected += OnCensusSelected;
        Controls.Add(_census);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawnRevision = -1;
    }

    private MyGuiControlLabel AddSideLabel(Vector2 position, string text)
    {
        var label = new MyGuiControlLabel(
            position, null, text, null, 0.75f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(label);
        return label;
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
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
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

    private void Rebuild(FleetDoctrineComponent component)
    {
        _drawnRevision = component.Revision;

        if (!FleetDoctrineComponent.Available)
        {
            WriteUnavailable("This server does not offer the fleet doctrine to the client.");
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
                        + "/" + component.Cap + " doctrine lines";
        RebuildLines(component);
        RebuildPressure(component);
        RebuildCensus(component);
        RefreshButtons();
        RefreshStatus(component);
    }

    /// <summary>Every state where there is nothing to show. One path, so no half-populated table can
    /// survive any of them.</summary>
    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = "Fleet";
        if (_lines != null) _lines.Clear();
        if (_pressure != null) _pressure.Clear();
        if (_census != null) _census.Clear();
        _selectedLineId = null;
        SetButtonsEnabled(false);
        if (_status != null) _status.Text = message;
    }

    private void RebuildLines(FleetDoctrineComponent component)
    {
        _lines.Clear();
        for (int i = 0; i < component.Lines.Count; i++)
        {
            WireDoctrineLine l = component.Lines[i];
            var row = new MyGuiControlTable.Row(l);
            row.AddCell(new MyGuiControlTable.Cell(l.Priority.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(l.Fits ?? "", null,
                string.IsNullOrEmpty(l.Fits) ? "Its hull fits no build role." : "Answers demand for " + l.Fits + "."));
            row.AddCell(new MyGuiControlTable.Cell(l.PrefabId ?? "", null, l.Id + "  " + l.PrefabId));
            row.AddCell(new MyGuiControlTable.Cell(l.Have.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(l.Flying.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(l.Building.ToString()));
            row.AddCell(RepairCell(l.RepairWaiting, l.Repairing));
            row.AddCell(new MyGuiControlTable.Cell(l.TargetCount.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(ScopeColumn(l)));
            string state = StateColumn(l);
            row.AddCell(new MyGuiControlTable.Cell(state, null, state));
            _lines.Add(row);
        }
        RestoreSelection();
    }

    private void RebuildPressure(FleetDoctrineComponent component)
    {
        _pressure.Clear();
        for (int i = 0; i < component.Unlined.Count; i++)
        {
            WireDoctrinePressureRow p = component.Unlined[i];
            var row = new MyGuiControlTable.Row(p);
            row.AddCell(new MyGuiControlTable.Cell(p.Role ?? ""));
            row.AddCell(new MyGuiControlTable.Cell(NodeLabel(p.NodeId, p.NodeName)));
            row.AddCell(new MyGuiControlTable.Cell(p.Demands.ToString()));
            _pressure.Add(row);
        }
    }

    private void RebuildCensus(FleetDoctrineComponent component)
    {
        _census.Clear();
        int waiting = 0, repairing = 0;
        for (int i = 0; i < component.Census.Count; i++)
        {
            WireFleetCensusRow c = component.Census[i];
            bool lined = AnyLineNames(component, c.PrefabId);
            var row = new MyGuiControlTable.Row(c);
            // The marker is the point of this table: a hull nothing names is stock the doctrine did
            // not ask for, and is invisible in the list above by definition.
            string hull = lined ? (c.PrefabId ?? "") : (c.PrefabId ?? "") + "  <- no line";
            row.AddCell(new MyGuiControlTable.Cell(hull, null, hull));
            row.AddCell(new MyGuiControlTable.Cell(c.Parked.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(c.Flying.ToString()));
            row.AddCell(RepairCell(c.RepairWaiting, c.Repairing));
            _census.Add(row);
            if (_selectedCensusId != null
                && string.Equals(c.PrefabId, _selectedCensusId, StringComparison.OrdinalIgnoreCase))
                _census.SelectedRowIndex = _census.RowsCount - 1;
            waiting += c.RepairWaiting;
            repairing += c.Repairing;
        }
        if (_selectedCensusId != null && !SelectedCensus().HasValue) _selectedCensusId = null;
        RefreshButtons();
        if (_censusCaption != null)
            _censusCaption.Text = waiting + repairing == 0
                ? "Owned hulls"
                : "Owned hulls  -  " + (waiting + repairing) + " in repair, " + waiting + " waiting for parts";
    }

    /// <summary>Hulls in the yard: counted in a line's Have, not flying, and not destroyed. The split
    /// is in the tooltip because the column is narrow.</summary>
    private static MyGuiControlTable.Cell RepairCell(int waiting, int repairing)
    {
        int total = waiting + repairing;
        return new MyGuiControlTable.Cell(total == 0 ? "-" : total.ToString(), null,
            total == 0
                ? "No hull of this prefab is in repair."
                : waiting + " waiting for parts, " + repairing + " with parts in and being repaired. "
                  + "Hulls in repair count toward a line's Have, so it does not build their replacements.");
    }

    private static bool AnyLineNames(FleetDoctrineComponent component, string prefabId)
    {
        if (string.IsNullOrEmpty(prefabId)) return false;
        for (int i = 0; i < component.Lines.Count; i++)
            if (string.Equals(component.Lines[i].PrefabId, prefabId, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string NodeLabel(string nodeId, string nodeName)
    {
        return string.IsNullOrEmpty(nodeName) ? (nodeId ?? "") : nodeName;
    }

    private static string ScopeColumn(WireDoctrineLine l)
    {
        if (string.IsNullOrEmpty(l.ScopeNodeId)) return "any";
        return string.IsNullOrEmpty(l.ScopeNodeName) ? l.ScopeNodeId : l.ScopeNodeName;
    }

    /// <summary>Derived here from the counts the snapshot carries rather than sent as a rendered
    /// string: the server already sends every number it is made of, and a sentence on the wire would
    /// be a second place the rule lives.</summary>
    private static string StateColumn(WireDoctrineLine l)
    {
        string state;
        if (l.Paused) state = "paused";
        else if (l.Banned) state = "blocked: banned on this server";
        else if (l.OverCeiling) state = "blocked: over this server's performance limit";
        else if (l.RouteBlocked)
            state = "blocked: cannot fly from "
                    + (string.IsNullOrEmpty(l.ScopeNodeId) ? "any yard" : ScopeColumn(l));
        else if (l.Building > 0) state = "building " + l.Building;
        else if (l.Have >= l.TargetCount) state = "at target";
        else state = "short " + (l.TargetCount - l.Have);

        // Pressure rides on every state, including "at target": a line at its target with work
        // waiting is exactly the ceiling this design chose, and the only place a player sees it.
        if (l.Pressure > 0) state += "  ·  " + l.Pressure + " waiting";
        return state;
    }

    private void RefreshButtons()
    {
        bool selected = _selectedLineId != null;
        SetButtonsEnabled(selected);
        if (_pause == null) return;
        WireDoctrineLine? line = SelectedLine();
        _pause.Text = line.HasValue && line.Value.Paused ? "Resume" : "Pause";
    }

    /// <summary><paramref name="enabled"/> gates the six actions that act on the selected row.
    /// "New line..." is deliberately outside it — creating one needs no selection — and is gated on
    /// the feature being granted, NOT on the pick-lists having arrived: gating it on the lists would
    /// make a missing options message indistinguishable from a dead button.</summary>
    private void SetButtonsEnabled(bool enabled)
    {
        if (_pause != null) _pause.Enabled = enabled;
        if (_remove != null) _remove.Enabled = enabled;
        if (_priorityUp != null) _priorityUp.Enabled = enabled;
        if (_priorityDown != null) _priorityDown.Enabled = enabled;
        if (_targetUp != null) _targetUp.Enabled = enabled;
        if (_targetDown != null) _targetDown.Enabled = enabled;
        if (_newLine != null) _newLine.Enabled = FleetDoctrineComponent.Available;
        // Needs no selection and no doctrine: tactics are learned whatever the fleet is made of.
        if (_tactics != null) _tactics.Enabled = TacticsComponent.Available;
        if (_refits != null) _refits.Enabled = FleetDoctrineComponent.Available;
        if (_decommission != null)
            _decommission.Enabled = FleetDoctrineComponent.Available && SelectedCensus().HasValue;
    }

    private void RefreshStatus(FleetDoctrineComponent component)
    {
        if (_status == null) return;

        // A command's reply outranks the age line while it is fresh: it is the only place a refusal
        // is ever shown.
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds
               < FleetDoctrineComponent.CommandMessageSeconds)
        {
            _status.Text = OneLine(component.LastCommandMessage);
            return;
        }

        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        _status.Text = component.Lines.Count == 0
            ? "No doctrine: Factions picks your fleet's hulls. Add a line and it becomes the only "
              + "thing that builds them   -   updated " + age + "s ago"
            : "Your doctrine is the only thing that builds FLEET hulls; a role with no line is not "
              + "built. Shop stock is separate   -   updated " + age + "s ago";
    }

    // ---- Actions ------------------------------------------------------------------------------

    private WireDoctrineLine? SelectedLine()
    {
        MyGuiControlTable.Row row = _lines != null ? _lines.SelectedRow : null;
        if (row == null || !(row.UserData is WireDoctrineLine)) return null;
        return (WireDoctrineLine)row.UserData;
    }

    private void RestoreSelection()
    {
        if (string.IsNullOrEmpty(_selectedLineId)) return;
        for (int i = 0; i < _lines.RowsCount; i++)
        {
            if (!(_lines.GetRow(i).UserData is WireDoctrineLine)) continue;
            var line = (WireDoctrineLine)_lines.GetRow(i).UserData;
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
        _selectedLineId = row != null && row.UserData is WireDoctrineLine
            ? ((WireDoctrineLine)row.UserData).Id
            : null;
        RefreshButtons();
    }

    private void OnNewClicked(MyGuiControlButton button)
    {
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
        if (component == null || !component.HasOptions)
        {
            // The pick-lists arrive once per subscribe. Without them the dialog would open onto
            // empty pickers, so say so in the one place the player is already reading.
            if (_status != null)
                _status.Text = "Waiting for the server's role and hull lists - try again in a moment.";
            Plugin.Log("new doctrine line: options not received yet (hasOptions=false)");
            return;
        }
        NewDoctrineLineScreen.Open();
    }

    private WireFleetCensusRow? SelectedCensus()
    {
        MyGuiControlTable.Row row = _census != null ? _census.SelectedRow : null;
        if (row == null || !(row.UserData is WireFleetCensusRow)) return null;
        return (WireFleetCensusRow)row.UserData;
    }

    private void OnCensusSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        WireFleetCensusRow? c = SelectedCensus();
        _selectedCensusId = c.HasValue ? c.Value.PrefabId : null;
        RefreshButtons();
    }

    private void OnDecommissionClicked(MyGuiControlButton button)
    {
        WireFleetCensusRow? selected = SelectedCensus();
        if (!selected.HasValue || string.IsNullOrEmpty(selected.Value.PrefabId)) return;
        WireFleetCensusRow c = selected.Value;
        string body = "Dismantle every parked hull of '" + c.PrefabId + "' (all versions of the design, shelf "
                      + "included) for its components? " + c.Parked + " parked here in the census."
                      + (c.RepairWaiting + c.Repairing > 0
                          ? " Hulls in repair are dismantled too, for what they hold."
                          : "")
                      + (c.Flying > 0
                          ? " The " + c.Flying + " flying are dismantled when they come home."
                          : "")
                      + (FleetDoctrineComponent.Instance != null && AnyLineNames(FleetDoctrineComponent.Instance, c.PrefabId)
                          ? " Its doctrine line is removed too, so it is not rebuilt."
                          : "")
                      + " This cannot be undone.";
        string prefabId = c.PrefabId;
        FactionsUI.Ships.TerminalShipAssetsComponent.ConfirmYesNo("Decommission", body, delegate
        {
            FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
            if (component != null) component.SendCommand(WireDoctrineCommand.Decommission, prefabId, 0);
        }, "Decommission");
    }

    private void OnTacticsClicked(MyGuiControlButton button)
    {
        TacticsScreen.Open();
    }

    private void OnRefitsClicked(MyGuiControlButton button)
    {
        HullRefitScreen.Open();
    }

    private void OnPauseClicked(MyGuiControlButton button)
    {
        WireDoctrineLine? line = SelectedLine();
        if (!line.HasValue) return;
        Send(line.Value.Paused ? WireDoctrineCommand.Resume : WireDoctrineCommand.Pause, 0);
    }

    private void OnRemoveClicked(MyGuiControlButton button)
    {
        Send(WireDoctrineCommand.Remove, 0);
    }

    /// <summary>Relative on purpose: the server steps the line's own rank, so a click against a row
    /// the cadence has since re-sent cannot write back a rank the player never saw.</summary>
    private void OnPriorityUpClicked(MyGuiControlButton button)
    {
        Send(WireDoctrineCommand.PriorityUp, 0);
    }

    private void OnPriorityDownClicked(MyGuiControlButton button)
    {
        Send(WireDoctrineCommand.PriorityDown, 0);
    }

    /// <summary>Target is set absolutely server-side, so a relative button reads the current target
    /// off the row it is acting on. A target below 1 is refused there — removing the line is how a
    /// player stops wanting a hull.</summary>
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
        WireDoctrineLine? line = SelectedLine();
        if (!line.HasValue) return;
        Send(WireDoctrineCommand.SetTarget, line.Value.TargetCount + delta);
    }

    private void Send(WireDoctrineCommand command, int value)
    {
        FleetDoctrineComponent component = FleetDoctrineComponent.Instance;
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
