using System;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Nodes;

/// <summary>
/// One station's shipyard: every build, what it is short of component by component, where in the
/// estate that shortfall could come from, and every queue action — pause, new build, cancel and
/// priority. The station window carries no queue of its own and opens this from its Shipyard button.
///
/// <para>Why it needs a window: "how far along is this build" is a percentage, but the question that
/// always follows — <b>why has it stopped</b> — needs a row per BOM component, nineteen of them for a
/// hull, against a node's own stock, the rest of the faction's, and whatever is already flying here.
/// The server computes all of it (<c>Factions.Core.BuildSupply</c>); this is where it is read.</para>
///
/// <para>The node's repair queue is listed under the builds, in the order the yard serves it --
/// smallest outstanding bill first -- and a repair row reads through the same component panel: a
/// hull in the yard is a hull the player does not have, and without these rows a fleet waiting for
/// parts reads as a fleet destroyed.</para>
///
/// <para>A window of its own for the reason <see cref="StationDevelopScreen"/> records: the station
/// window is already two full columns. It reads <see cref="NodeDetailComponent"/>'s snapshot rather
/// than holding a subscription of its own, re-asserts that watch each frame so it keeps updating if
/// the window behind it closes first, and never releases one.</para>
/// </summary>
internal sealed class StationBuildsScreen : MyGuiScreenBase
{
    private const int BuildColumns = 7;
    private const int ComponentColumns = 7;

    /// <summary>Deliberately <see cref="NodeDetailScreen"/>'s, for the reason
    /// <c>FleetDoctrineScreen</c> records: these interior offsets are the ones confirmed in-game.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static StationBuildsScreen Instance { get; private set; }

    private readonly string _nodeId;

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlTable _builds;
    private MyGuiControlTable _components;
    private MyGuiControlMultilineText _detail;
    private MyGuiControlButton _pause;
    private MyGuiControlButton _cancel;
    private MyGuiControlButton _priorityUp;
    private MyGuiControlButton _priorityDown;
    private MyGuiControlButton _newBuild;

    private int _drawnRevision = -1;

    /// <summary>Entry id of the selected build, or <see cref="RepairKey"/> of the selected repair
    /// row, so a snapshot arriving while the player reads its components does not move the
    /// selection out from under the action buttons.</summary>
    private string _selectedEntryId;

    private StationBuildsScreen(string nodeId)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _nodeId = nodeId;
        m_closeOnEsc = true;
        CanBeHidden = true;
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    internal static void Open(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        try
        {
            StationBuildsScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new StationBuildsScreen(nodeId));
        }
        catch (Exception ex)
        {
            Plugin.Log("builds window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.StationBuildsScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
    }

    public override void OnRemoved()
    {
        // Guarded on still being the current window for the reason NodeDetailScreen records: SE
        // defers both the removal and the add. No unwatch here — this screen never took the watch.
        if (Instance == this) Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Shipyard");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        // On the heading row, right-aligned with the tables below it, rather than beside the four
        // action buttons: that row is four 0.19-wide slots at a 0.21 gap and ends 0.04 short of the
        // window's inner edge, so there is no fifth slot in it. Adding one is what would have
        // re-flowed offsets confirmed in-game.
        _newBuild = AddActionButton(new Vector2(left + 0.84f - 0.19f, top - 0.005f),
            new Vector2(0.19f, 0.04f), "New build...",
            "Queue one hull at this shipyard. It is built once; the fleet doctrine decides what is "
            + "built on its own.",
            OnNewBuildClicked);

        // VisibleRowsCount, not Size.Y, decides how tall a MyGuiControlTable ends up — its
        // RefreshInternals overwrites Size.Y — so every control below is placed off the real size
        // afterwards rather than off a constant that would drift from it.
        _builds = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, top + 0.04f),
            Size = new Vector2(0.84f, 0f),
            Name = "StationBuildsQueue",
            ColumnsCount = BuildColumns,
            VisibleRowsCount = 5,
        };
        _builds.SetCustomColumnWidths(
            new float[BuildColumns] { 0.11f, 0.26f, 0.15f, 0.08f, 0.11f, 0.17f, 0.12f });
        _builds.SetColumnName(0, new StringBuilder("Build"));
        _builds.SetColumnName(1, new StringBuilder("Prefab"));
        _builds.SetColumnName(2, new StringBuilder("Collected"));
        _builds.SetColumnName(3, new StringBuilder("Done"));
        _builds.SetColumnName(4, new StringBuilder("Short"));
        _builds.SetColumnName(5, new StringBuilder("Slot"));
        _builds.SetColumnName(6, new StringBuilder("Prio"));
        _builds.ItemSelected += OnBuildSelected;
        Controls.Add(_builds);

        float componentsTop = top + 0.04f + _builds.Size.Y + 0.015f;
        var componentCaption = new MyGuiControlLabel(
            new Vector2(left, componentsTop), null, "Components", null, 0.8f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(componentCaption);

        _components = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, componentsTop + 0.03f),
            Size = new Vector2(0.56f, 0f),
            Name = "StationBuildsComponents",
            ColumnsCount = ComponentColumns,
            VisibleRowsCount = 7,
        };
        _components.SetCustomColumnWidths(
            new float[ComponentColumns] { 0.32f, 0.11f, 0.11f, 0.11f, 0.10f, 0.13f, 0.12f });
        _components.SetColumnName(0, new StringBuilder("Component"));
        _components.SetColumnName(1, new StringBuilder("Need"));
        _components.SetColumnName(2, new StringBuilder("Have"));
        _components.SetColumnName(3, new StringBuilder("Short"));
        _components.SetColumnName(4, new StringBuilder("Here"));
        _components.SetColumnName(5, new StringBuilder("Estate"));
        _components.SetColumnName(6, new StringBuilder("Inbound"));
        Controls.Add(_components);

        float detailLeft = left + _components.Size.X + 0.02f;
        _detail = new MyGuiControlMultilineText(
            position: new Vector2(detailLeft, componentsTop + 0.03f),
            size: new Vector2(0.26f, _components.Size.Y),
            backgroundColor: null,
            font: "Blue",
            textScale: 0.75f,
            textAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            contents: null,
            drawScrollbarV: true,
            drawScrollbarH: false,
            textBoxAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            textPadding: new MyGuiBorderThickness(0.006f, 0.004f))
        {
            VisualStyle = MyGuiControlMultilineStyleEnum.BackgroundBordered,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Name = "StationBuildsDetail",
            CanHaveFocus = true,
        };
        Controls.Add(_detail);

        var buttonSize = new Vector2(0.19f, 0.04f);
        const float buttonGap = 0.21f;
        float buttonTop = componentsTop + 0.03f + _components.Size.Y + 0.015f;
        _pause = AddActionButton(new Vector2(left, buttonTop), buttonSize, "Pause shipyard",
            "Stop this shipyard claiming components. What its builds already hold stays with them.",
            OnPauseClicked);
        _cancel = AddActionButton(new Vector2(left + buttonGap, buttonTop), buttonSize, "Cancel build",
            "Cancel the selected build. Its components are returned to this station's inventory.",
            OnCancelClicked);
        _priorityUp = AddActionButton(new Vector2(left + buttonGap * 2f, buttonTop), buttonSize,
            "Priority +1",
            "Raise the selected build's claim on a contested component. Higher wins, and the claim "
            + "order decides which build holds the yard's working slot.",
            OnPriorityUpClicked);
        _priorityDown = AddActionButton(new Vector2(left + buttonGap * 3f, buttonTop), buttonSize,
            "Priority -1",
            "Lower the selected build's claim on a contested component.",
            OnPriorityDownClicked);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawnRevision = -1;
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
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component != null)
        {
            // The station window behind this one can be retargeted onto another node, and there is
            // one watch between them — closing is the only correct answer, exactly as the develop
            // menu does. A null watch is the other case (the window behind was closed, not moved),
            // and there the re-assert below is what keeps this screen live.
            if (!string.IsNullOrEmpty(component.WatchedNodeId)
                && !string.Equals(component.WatchedNodeId, _nodeId, StringComparison.Ordinal))
            {
                CloseScreen();
                return base.Draw();
            }
            component.Watch(_nodeId);
            if (_drawnRevision != component.Revision) Rebuild(component);
        }
        return base.Draw();
    }

    private void Rebuild(NodeDetailComponent component)
    {
        _drawnRevision = component.Revision;

        if (!NodeDetailComponent.Available)
        {
            WriteUnavailable("Not linked to a Factions server.");
            return;
        }
        if (!component.HasSnapshot
            || !string.Equals(component.Detail.Id, _nodeId, StringComparison.Ordinal))
        {
            WriteUnavailable("Waiting for the server...");
            return;
        }

        WireNodeDetail detail = component.Detail;
        if (!detail.Exists)
        {
            WriteUnavailable("This station is no longer on the server's map.");
            return;
        }
        if (!detail.Owned)
        {
            WriteUnavailable("A shipyard queue is only shown for your own faction's stations.");
            return;
        }

        _heading.Text = (string.IsNullOrEmpty(detail.DisplayName) ? detail.Id : detail.DisplayName)
                        + "  -  " + detail.Id
                        + (detail.ShipyardPaused ? "  (shipyard paused)" : "");
        RebuildBuilds(detail);
        RefreshComponents();
        RefreshDetail(detail);
        RefreshButtons(detail);
        RefreshStatus(component, detail);
    }

    /// <summary>Every state where there is nothing to act on. One path, so no half-populated table
    /// can survive any of them.</summary>
    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = _nodeId;
        if (_builds != null) _builds.Clear();
        if (_components != null) _components.Clear();
        if (_detail != null) { _detail.Clear(); _detail.AppendText(message); }
        _selectedEntryId = null;
        SetButtonsEnabled(false, false);
        if (_status != null) _status.Text = message;
    }

    private void RebuildBuilds(WireNodeDetail detail)
    {
        _builds.Clear();
        WireNodeBuild[] builds = detail.Builds ?? new WireNodeBuild[0];
        for (int i = 0; i < builds.Length; i++)
        {
            WireNodeBuild build = builds[i];
            var row = new MyGuiControlTable.Row(build);
            row.AddCell(new MyGuiControlTable.Cell(build.Id, null, BuildLabel(build)));
            row.AddCell(new MyGuiControlTable.Cell(
                string.IsNullOrEmpty(build.PrefabId) ? "(free build)" : build.PrefabId,
                null, build.PrefabId));
            row.AddCell(new MyGuiControlTable.Cell(build.Collected + " / " + build.BomTotal));
            row.AddCell(new MyGuiControlTable.Cell(PercentOf(build)));
            row.AddCell(new MyGuiControlTable.Cell(ShortCountText(build), null, ShortTooltip(build)));
            row.AddCell(new MyGuiControlTable.Cell(SlotText(build, detail), null, SlotTooltip(build, detail)));
            row.AddCell(new MyGuiControlTable.Cell(build.Priority.ToString()));
            _builds.Add(row);
        }

        WireNodeRepair[] repairs = detail.Repairs ?? new WireNodeRepair[0];
        for (int i = 0; i < repairs.Length; i++)
        {
            WireNodeRepair repair = repairs[i];
            var row = new MyGuiControlTable.Row(repair);
            row.AddCell(new MyGuiControlTable.Cell(
                repair.Count > 1 ? "Repair x" + repair.Count : "Repair", null,
                "Damaged hull" + (repair.Count > 1 ? "s" : "") + " in repair, served "
                + ServedText(repair) + ". Smallest outstanding bill first."));
            row.AddCell(new MyGuiControlTable.Cell(
                (repair.PrefabId ?? "?") + "  " + IntegrityText(repair.Integrity), null, repair.PrefabId));
            row.AddCell(new MyGuiControlTable.Cell(Compact(repair.Collected) + " / " + Compact(repair.BillTotal)));
            row.AddCell(new MyGuiControlTable.Cell(
                repair.BillTotal <= 0 ? "-" : (repair.Collected * 100.0 / repair.BillTotal).ToString("0") + "%"));
            row.AddCell(new MyGuiControlTable.Cell(ShortCountText(repair.Bill), null, ShortTooltip(repair.Bill)));
            row.AddCell(new MyGuiControlTable.Cell(RepairStateText(repair), null, RepairStateTooltip(repair)));
            row.AddCell(new MyGuiControlTable.Cell("#" + repair.Position, null,
                "Served " + ServedText(repair) + " of " + detail.RepairHulls + " hull(s) in repair here."));
            _builds.Add(row);
        }
        RestoreSelection();
    }

    /// <summary>Selection key of a repair row: stable while the row's hulls stay in the queue, which
    /// its served position is not.</summary>
    private static string RepairKey(WireNodeRepair repair)
    {
        return "repair:" + repair.PrefabId + ":" + repair.Integrity.ToString("R") + ":" + repair.QueuedUtcTicks
               + ":" + repair.PartsCompleteUtcTicks + ":" + repair.Collected;
    }

    private static string KeyOf(MyGuiControlTable.Row row)
    {
        if (row == null) return null;
        if (row.UserData is WireNodeBuild) return ((WireNodeBuild)row.UserData).Id;
        if (row.UserData is WireNodeRepair) return RepairKey((WireNodeRepair)row.UserData);
        return null;
    }

    /// <summary>Keeps the selection on the entry the player chose across snapshots, and puts it on
    /// the first row when there is none — an empty component table beside a queue with builds in it
    /// reads as a window that failed to load.</summary>
    private void RestoreSelection()
    {
        if (!string.IsNullOrEmpty(_selectedEntryId))
        {
            for (int i = 0; i < _builds.RowsCount; i++)
            {
                if (!string.Equals(KeyOf(_builds.GetRow(i)), _selectedEntryId, StringComparison.Ordinal)) continue;
                _builds.SelectedRowIndex = i;
                return;
            }
            // The build finished, expired or was cancelled, or the repair row moved on, while it
            // was selected.
            _selectedEntryId = null;
        }

        if (_builds.RowsCount == 0) return;
        _builds.SelectedRowIndex = 0;
        _selectedEntryId = KeyOf(_builds.GetRow(0));
    }

    private void RefreshComponents()
    {
        _components.Clear();
        WireBuildComponent[] bom;
        WireNodeBuild selected;
        WireNodeRepair repair;
        if (TryGetSelected(out selected)) bom = selected.Bom ?? new WireBuildComponent[0];
        else if (TryGetSelectedRepair(out repair)) bom = repair.Bill ?? new WireBuildComponent[0];
        else return;

        for (int i = 0; i < bom.Length; i++)
        {
            WireBuildComponent c = bom[i];
            long outstanding = c.Need > c.Have ? c.Need - c.Have : 0L;
            var row = new MyGuiControlTable.Row(c);
            row.AddCell(new MyGuiControlTable.Cell(ItemNames.Describe(c.Key), null, ComponentTooltip(c)));
            row.AddCell(new MyGuiControlTable.Cell(Compact(c.Need)));
            row.AddCell(new MyGuiControlTable.Cell(Compact(c.Have)));
            row.AddCell(new MyGuiControlTable.Cell(outstanding == 0 ? "-" : Compact(outstanding)));
            row.AddCell(new MyGuiControlTable.Cell(outstanding == 0 ? "" : Compact(c.AtNode)));
            row.AddCell(new MyGuiControlTable.Cell(
                outstanding == 0 ? "" : (c.Unserved ? "none" : Compact(c.Elsewhere))));
            row.AddCell(new MyGuiControlTable.Cell(outstanding == 0 ? "" : Compact(c.EnRoute)));
            _components.Add(row);
        }
    }

    private void RefreshDetail(WireNodeDetail detail)
    {
        _detail.Clear();
        WireNodeRepair repair;
        if (TryGetSelectedRepair(out repair))
        {
            RefreshRepairDetail(repair, detail);
            return;
        }
        WireNodeBuild selected;
        if (!TryGetSelected(out selected))
        {
            bool empty = (detail.Builds == null || detail.Builds.Length == 0)
                         && (detail.Repairs == null || detail.Repairs.Length == 0);
            AppendLine(empty
                ? "Nothing is queued at this shipyard."
                : "Pick a build to see what it is short of.");
            AppendLine("");
            AppendLine("A yard works one build at a time. The rest of the queue waits its turn and "
                       + "absorbs nothing while it does, however short it looks.");
            return;
        }

        AppendLine(selected.Id + "  (" + BuildLabel(selected) + ")");
        AppendLine(string.IsNullOrEmpty(selected.PrefabId) ? "Free build, no BOM" : selected.PrefabId);
        AppendLine("");
        AppendLine(SlotTooltip(selected, detail));
        AppendLine("");
        AppendLine("Queued " + Ago(selected.CreatedUtcTicks) + " ago.");
        AppendLine(selected.StalledSeconds > 0
            ? "Nothing has landed in it for " + Duration(selected.StalledSeconds) + "."
            : "It is absorbing components now.");

        WireBuildComponent blocker;
        if (TryGetBlocker(selected, out blocker))
        {
            AppendLine("");
            AppendLine("Biggest shortfall: " + ItemNames.Describe(blocker.Key) + ", "
                       + (blocker.Need - blocker.Have) + " short.");
            AppendLine(SupplyVerdict(blocker));
        }

        WireBuildReservedInput[] reserved = selected.ReservedInputs ?? new WireBuildReservedInput[0];
        if (reserved.Length > 0)
        {
            AppendLine("");
            AppendLine("Staged toward making what is missing:");
            for (int i = 0; i < reserved.Length; i++)
                AppendLine("  " + ItemNames.Describe(reserved[i].Key) + "  " + reserved[i].Quantity);
        }
    }

    private void RefreshRepairDetail(WireNodeRepair repair, WireNodeDetail detail)
    {
        AppendLine((repair.Count > 1 ? repair.Count + " x " : "") + (repair.PrefabId ?? "?")
                   + " in repair, " + IntegrityText(repair.Integrity) + " intact");
        AppendLine("Served " + ServedText(repair) + " of " + detail.RepairHulls + ".");
        AppendLine("");
        AppendLine(RepairStateTooltip(repair));
        AppendLine("");
        AppendLine("Bill " + repair.BillTotal + " units per hull, " + repair.Collected + " collected.");
        AppendLine("Came home " + Ago(repair.QueuedUtcTicks) + " ago.");

        WireBuildComponent blocker;
        if (TryGetBlocker(repair.Bill, out blocker))
        {
            AppendLine("");
            AppendLine("Biggest shortfall: " + ItemNames.Describe(blocker.Key) + ", "
                       + (blocker.Need - blocker.Have) + " short.");
            AppendLine(SupplyVerdict(blocker));
        }
        AppendLine("");
        AppendLine("The yard serves the smallest outstanding bill first, and takes parts a hull "
                   + "further down the queue is holding when one ahead of it needs them.");
    }

    private void AppendLine(string text)
    {
        if (!string.IsNullOrEmpty(text)) _detail.AppendText(text);
        _detail.AppendLine();
    }

    private void RefreshButtons(WireNodeDetail detail)
    {
        _pause.Text = detail.ShipyardPaused ? "Resume shipyard" : "Pause shipyard";
        WireNodeBuild selected;
        SetButtonsEnabled(detail.Owned, detail.Owned && TryGetSelected(out selected));
    }

    private void SetButtonsEnabled(bool shipyardActions, bool entryActions)
    {
        if (_pause != null) _pause.Enabled = shipyardActions;
        if (_cancel != null) _cancel.Enabled = entryActions;
        if (_priorityUp != null) _priorityUp.Enabled = entryActions;
        if (_priorityDown != null) _priorityDown.Enabled = entryActions;
        // Not an entry action: a queue with nothing in it is exactly when a player wants to add
        // one. Feature-gated, and disabled rather than hidden when the server withholds the bit.
        if (_newBuild != null) _newBuild.Enabled = shipyardActions && StationBuildComponent.Available;
    }

    private void RefreshStatus(NodeDetailComponent component, WireNodeDetail detail)
    {
        // A command's reply outranks the age line for as long as it is fresh: it is the only place
        // a refusal ("that build no longer exists") is ever shown.
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds
               < NodeDetailComponent.CommandMessageSeconds)
        {
            _status.Text = OneLine(component.LastCommandMessage);
            return;
        }

        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        int builds = detail.Builds == null ? 0 : detail.Builds.Length;
        _status.Text = builds + " build(s) queued"
                       + (detail.RepairHulls > 0 ? ", " + detail.RepairHulls + " hull(s) in repair" : "")
                       + "   -   updated " + age + "s ago";
    }

    // ---- Actions ------------------------------------------------------------------------------

    private void OnBuildSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        _selectedEntryId = KeyOf(table.SelectedRow);

        RefreshComponents();
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component != null && component.HasSnapshot)
        {
            RefreshDetail(component.Detail);
            RefreshButtons(component.Detail);
        }
    }

    private void OnNewBuildClicked(MyGuiControlButton button)
    {
        StationBuildScreen.Open(_nodeId);
    }

    private void OnPauseClicked(MyGuiControlButton button)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component == null || !component.HasSnapshot) return;
        component.SendCommand(
            component.Detail.ShipyardPaused ? WireNodeCommand.QueueResume : WireNodeCommand.QueuePause,
            null, 0);
    }

    private void OnCancelClicked(MyGuiControlButton button)
    {
        Send(WireNodeCommand.QueueCancel, 0);
    }

    private void OnPriorityUpClicked(MyGuiControlButton button)
    {
        SendPriorityDelta(1);
    }

    private void OnPriorityDownClicked(MyGuiControlButton button)
    {
        SendPriorityDelta(-1);
    }

    /// <summary>Priority is set absolutely server-side, so a relative button reads the rank off the
    /// row it is acting on rather than sending a delta.</summary>
    private void SendPriorityDelta(int delta)
    {
        WireNodeBuild selected;
        if (!TryGetSelected(out selected)) return;
        Send(WireNodeCommand.QueuePriority, selected.Priority + delta);
    }

    private void Send(WireNodeCommand command, int value)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        WireNodeBuild selected;
        if (component == null || !TryGetSelected(out selected)) return;
        component.SendCommand(command, selected.Id, value);
    }

    private bool TryGetSelected(out WireNodeBuild build)
    {
        build = default(WireNodeBuild);
        MyGuiControlTable.Row row = _builds != null ? _builds.SelectedRow : null;
        if (row == null || !(row.UserData is WireNodeBuild)) return false;
        build = (WireNodeBuild)row.UserData;
        return true;
    }

    private bool TryGetSelectedRepair(out WireNodeRepair repair)
    {
        repair = default(WireNodeRepair);
        MyGuiControlTable.Row row = _builds != null ? _builds.SelectedRow : null;
        if (row == null || !(row.UserData is WireNodeRepair)) return false;
        repair = (WireNodeRepair)row.UserData;
        return true;
    }

    // ---- Formatting ---------------------------------------------------------------------------

    /// <summary>The outstanding row with the largest shortfall. The server sends the BOM already in
    /// that order, so this is the first row that is still short of anything.</summary>
    private static bool TryGetBlocker(WireNodeBuild build, out WireBuildComponent blocker)
    {
        return TryGetBlocker(build.Bom, out blocker);
    }

    private static bool TryGetBlocker(WireBuildComponent[] rows, out WireBuildComponent blocker)
    {
        blocker = default(WireBuildComponent);
        WireBuildComponent[] bom = rows ?? new WireBuildComponent[0];
        for (int i = 0; i < bom.Length; i++)
        {
            if (bom[i].Need <= bom[i].Have) continue;
            blocker = bom[i];
            return true;
        }
        return false;
    }

    /// <summary>Where the shortfall could come from, in the order a player would act on it: already
    /// on its way, standing somewhere else, or nowhere at all.</summary>
    private static string SupplyVerdict(WireBuildComponent c)
    {
        if (c.EnRoute > 0) return c.EnRoute + " of it is already on its way here.";
        if (c.AtNode > 0)
            return c.AtNode + " is sitting in this station's own stock - a paused shipyard or a "
                   + "waiting build is what leaves it there.";
        if (c.Elsewhere > 0)
            return c.Elsewhere + " is held at your other stations, and nothing is moving it yet.";
        if (c.Unserved)
            return "Nothing in your faction has any, and the last supply pass found no source for "
                   + "it. It has to be made, bought or hauled in.";
        return "Your faction holds none of it anywhere.";
    }

    private static string ComponentTooltip(WireBuildComponent c)
    {
        long outstanding = c.Need > c.Have ? c.Need - c.Have : 0L;
        if (outstanding == 0)
            return c.Key + ": complete, " + c.Have + " of " + c.Need + " absorbed.";
        return c.Key + ": " + c.Have + " of " + c.Need + " absorbed, " + outstanding + " short."
               + "  Here " + c.AtNode + ", elsewhere in your estate " + c.Elsewhere
               + ", inbound " + c.EnRoute + "."
               + (c.Unserved ? "  The last supply pass found no source for it." : "");
    }

    private static string ShortCountText(WireNodeBuild build)
    {
        return ShortCountText(build.Bom);
    }

    private static string ShortCountText(WireBuildComponent[] rows)
    {
        WireBuildComponent[] bom = rows ?? new WireBuildComponent[0];
        if (bom.Length == 0) return "-";
        int outstanding = 0;
        for (int i = 0; i < bom.Length; i++) if (bom[i].Need > bom[i].Have) outstanding++;
        return outstanding == 0 ? "none" : outstanding + " of " + bom.Length;
    }

    private static string ShortTooltip(WireNodeBuild build)
    {
        return ShortTooltip(build.Bom);
    }

    private static string ShortTooltip(WireBuildComponent[] rows)
    {
        WireBuildComponent blocker;
        if (!TryGetBlocker(rows, out blocker)) return "Every component of this bill is collected.";
        return "Largest shortfall: " + blocker.Key + ", " + (blocker.Need - blocker.Have) + " short.";
    }

    /// <summary>A yard works <c>SpatialNode.BuildSlotCount()</c> of its queue at a time. A waiting entry
    /// absorbs nothing and reserves nothing — which is why it is not stalling, however long it has
    /// sat, and why it is worth saying rather than leaving the player to read a flat 2%.</summary>
    private static string SlotText(WireNodeBuild build, WireNodeDetail detail)
    {
        if (detail.ShipyardPaused) return "paused";
        if (!build.Working) return "waiting (" + build.WaitingBehind + ")";
        return build.StalledSeconds > 0 ? "stalled" : "working";
    }

    private static string SlotTooltip(WireNodeBuild build, WireNodeDetail detail)
    {
        if (detail.ShipyardPaused)
            return "The shipyard is paused: no build here is claiming components at all.";
        if (!build.Working)
            return "Waiting behind " + build.WaitingBehind + " build(s). It absorbs nothing and "
                   + "holds nothing back while it waits - the claim order decides, and Priority "
                   + "moves it.";
        return build.StalledSeconds > 0
            ? "Holds the yard's working slot, but nothing has landed in it for "
              + Duration(build.StalledSeconds) + "."
            : "Holds the yard's working slot and is absorbing components.";
    }

    private static string ServedText(WireNodeRepair repair)
    {
        return repair.Count > 1
            ? "#" + repair.Position + "-" + (repair.Position + repair.Count - 1)
            : "#" + repair.Position;
    }

    private static string IntegrityText(double integrity)
    {
        return (integrity * 100.0).ToString("0") + "%";
    }

    private static string RepairStateText(WireNodeRepair repair)
    {
        switch ((WireRepairState)repair.State)
        {
            case WireRepairState.Repairing:
                return "repairing " + Until(repair.DoneUtcTicks);
            case WireRepairState.NoShipyard: return "no shipyard";
            case WireRepairState.YardPaused: return "paused";
            default: return "parts";
        }
    }

    private static string RepairStateTooltip(WireNodeRepair repair)
    {
        switch ((WireRepairState)repair.State)
        {
            case WireRepairState.Repairing:
                return "The parts are in and the yard is working the hull. Back in stock in "
                       + Until(repair.DoneUtcTicks) + ".";
            case WireRepairState.NoShipyard:
                return "This station has no Shipyard, so nothing here is repaired.";
            case WireRepairState.YardPaused:
                return "The shipyard is paused: no repair here collects parts or progresses.";
            default:
                return "Waiting for parts. The yard starts work once the whole bill is collected.";
        }
    }

    private static string Until(long utcTicks)
    {
        if (utcTicks <= 0) return "?";
        double seconds = (new DateTime(utcTicks, DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds;
        return seconds <= 0 ? "now" : Duration(seconds);
    }

    private static string PercentOf(WireNodeBuild build)
    {
        if (build.BomTotal <= 0) return "-";
        return (build.Collected * 100.0 / build.BomTotal).ToString("0") + "%";
    }

    /// <summary>Column-width arithmetic, not readability: a BOM runs to six figures and the table's
    /// numeric columns are sized for five. Every exact number is in the row's tooltip.</summary>
    private static string Compact(long value)
    {
        if (value < 100000) return value.ToString();
        if (value < 10000000) return (value / 1000L) + "k";
        return (value / 1000000L) + "M";
    }

    /// <summary>"Trade build" for an automatic build; "ordered build" for one a player or a doctrine
    /// line named by hull, which carries no role.</summary>
    private static string BuildLabel(WireNodeBuild build)
    {
        return string.IsNullOrEmpty(build.Role) ? "ordered build" : build.Role + " build";
    }

    private static string Ago(long utcTicks)
    {
        if (utcTicks <= 0) return "some time";
        double seconds = (DateTime.UtcNow - new DateTime(utcTicks, DateTimeKind.Utc)).TotalSeconds;
        return seconds <= 0 ? "just now" : Duration(seconds);
    }

    private static string Duration(double seconds)
    {
        if (seconds < 60) return (int)seconds + "s";
        if (seconds < 3600) return (int)(seconds / 60) + "m";
        var span = TimeSpan.FromSeconds(seconds);
        return (int)span.TotalHours + "h " + span.Minutes + "m";
    }

    /// <summary>The runtime's replies are written for chat and can carry newlines; the status line
    /// is a single <see cref="MyGuiControlLabel"/>.</summary>
    private static string OneLine(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
