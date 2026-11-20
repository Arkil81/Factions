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
/// The develop menu for one station: every capability, where it stands at this node, what the next
/// install costs, what is stopping it, and the two faction-wide settings that decide whether
/// Factions works toward it on its own.
///
/// <para>A window of its own rather than a panel inside <see cref="NodeDetailScreen"/>. That window
/// is already two full columns — a build queue with four action buttons beside a detail block — and
/// a nine-row table with its own buttons does not fit under either without shrinking the queue to
/// three visible rows. The precedent is <c>NewDoctrineLineScreen</c>: a second screen opened from a
/// button on the first, drawn over it.</para>
///
/// <para>It reads <see cref="NodeDetailComponent"/>'s snapshot rather than holding a subscription of
/// its own — the station window it was opened from is still watching the same node behind it. It
/// does re-assert that watch each frame, so the menu keeps updating even if the window behind it is
/// closed first, but it never releases one: the station window owns that lifecycle.</para>
/// </summary>
internal sealed class StationDevelopScreen : MyGuiScreenBase
{
    private const int CapabilityColumns = 6;

    /// <summary>Deliberately <see cref="NodeDetailScreen"/>'s, for the reason
    /// <c>FleetDoctrineScreen</c> records: these interior offsets are the ones confirmed in-game.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static StationDevelopScreen Instance { get; private set; }

    private readonly string _nodeId;

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlTable _capabilities;
    private MyGuiControlMultilineText _detail;
    private MyGuiControlButton _install;
    private MyGuiControlButton _reserve;
    private MyGuiControlButton _priorityUp;
    private MyGuiControlButton _priorityDown;

    private int _drawnRevision = -1;

    /// <summary>Capability name of the selected row, so a snapshot arriving while the player reads
    /// one does not move the selection out from under the action buttons.</summary>
    private string _selectedCap;

    private StationDevelopScreen(string nodeId)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _nodeId = nodeId;
        m_closeOnEsc = true;
        CanBeHidden = true;
        // Left false on purpose: the station window that opened this keeps drawing behind it, and
        // its own Draw is what holds the server-side watch this menu reads.
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
            StationDevelopScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new StationDevelopScreen(nodeId));
        }
        catch (Exception ex)
        {
            Plugin.Log("develop window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.StationDevelopScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
    }

    public override void OnRemoved()
    {
        // Guarded on still being the current window for the reason NodeDetailScreen records: SE
        // defers both the removal and the add, so a replaced window can be removed after its
        // successor has already loaded. No unwatch here — this screen never took the subscription.
        if (Instance == this) Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Develop station");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        // VisibleRowsCount, not Size.Y, decides how tall this ends up — MyGuiControlTable's
        // RefreshInternals overwrites Size.Y — so everything below is placed off its real size
        // afterwards. Nine rows is the whole capability set, so the table never scrolls.
        _capabilities = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, top + 0.04f),
            Size = new Vector2(0.56f, 0f),
            Name = "StationCapabilities",
            ColumnsCount = CapabilityColumns,
            VisibleRowsCount = 9,
        };
        _capabilities.SetCustomColumnWidths(
            new float[CapabilityColumns] { 0.29f, 0.10f, 0.14f, 0.09f, 0.09f, 0.29f });
        _capabilities.SetColumnName(0, new StringBuilder("Capability"));
        _capabilities.SetColumnName(1, new StringBuilder("Level"));
        _capabilities.SetColumnName(2, new StringBuilder("Cost"));
        _capabilities.SetColumnName(3, new StringBuilder("Auto"));
        _capabilities.SetColumnName(4, new StringBuilder("Prio"));
        _capabilities.SetColumnName(5, new StringBuilder("Status"));
        _capabilities.ItemSelected += OnCapabilitySelected;
        Controls.Add(_capabilities);

        // Measured off the table's post-construction size rather than a matching constant, so the
        // two columns cannot drift apart when VisibleRowsCount changes. The scrollbar lives inside
        // Size.X, so a plain gap beyond the right edge clears it.
        float tableHeight = _capabilities.Size.Y;
        float detailLeft = left + _capabilities.Size.X + 0.02f;

        _detail = new MyGuiControlMultilineText(
            position: new Vector2(detailLeft, top + 0.04f),
            size: new Vector2(0.26f, tableHeight),
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
            Name = "StationDevelopDetail",
            CanHaveFocus = true,
        };
        Controls.Add(_detail);

        var buttonSize = new Vector2(0.19f, 0.04f);
        const float buttonGap = 0.21f;
        float buttonTop = top + 0.04f + tableHeight + 0.015f;
        _install = AddActionButton(new Vector2(left, buttonTop), buttonSize, "Install",
            "Install the next level of the selected capability here, paying its credit cost and "
            + "consuming nothing else. Refused if anything in the Status column is in the way.",
            OnInstallClicked);
        _reserve = AddActionButton(new Vector2(left + buttonGap, buttonTop), buttonSize, "Auto-invest",
            "Let Factions stage materials toward this capability on its own. Off by default for a "
            + "player faction, so components are never locked toward something you did not choose. "
            + "Applies to your whole faction, not just this station.",
            OnReserveClicked);
        _priorityUp = AddActionButton(new Vector2(left, buttonTop + 0.05f), buttonSize, "Priority +1",
            "Raise this install's claim on components that ship builds also want. Higher wins. "
            + "Applies to your whole faction.",
            OnPriorityUpClicked);
        _priorityDown = AddActionButton(new Vector2(left + buttonGap, buttonTop + 0.05f), buttonSize,
            "Priority -1",
            "Lower this install's claim on contested components. At 0, ship builds win.",
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
            // one watch between them. Closing is the only correct answer: re-asserting this node's
            // watch every frame would leave the two screens taking the subscription off each other,
            // and a menu whose Install button names a node nobody is watching is worse than gone.
            // A null watch is the other case -- the window behind was closed, not moved -- and there
            // the re-assert below is what keeps this menu live.
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
            WriteUnavailable("Only your own faction's stations can be developed.");
            return;
        }
        if (!detail.HasStation)
        {
            WriteUnavailable("Nothing can be installed until this claim has a founded station.");
            return;
        }

        _heading.Text = (string.IsNullOrEmpty(detail.DisplayName) ? detail.Id : detail.DisplayName)
                        + "  -  " + detail.Id;
        RebuildRows(detail);
        RefreshDetail(detail);
        RefreshButtons();
        RefreshStatus(component, detail);
    }

    /// <summary>Every state where there is nothing to act on. One path, so no half-populated table
    /// can survive any of them.</summary>
    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = _nodeId;
        if (_capabilities != null) _capabilities.Clear();
        if (_detail != null) { _detail.Clear(); _detail.AppendText(message); }
        _selectedCap = null;
        SetButtonsEnabled(false);
        if (_status != null) _status.Text = message;
    }

    private void RebuildRows(WireNodeDetail detail)
    {
        _capabilities.Clear();
        WireNodeCapabilityOption[] options = detail.Develop ?? new WireNodeCapabilityOption[0];
        for (int i = 0; i < options.Length; i++)
        {
            WireNodeCapabilityOption o = options[i];
            var row = new MyGuiControlTable.Row(o);
            row.AddCell(new MyGuiControlTable.Cell(o.Cap));
            row.AddCell(new MyGuiControlTable.Cell(o.Level + "/" + o.MaxLevel));
            row.AddCell(new MyGuiControlTable.Cell(CostText(o)));
            row.AddCell(new MyGuiControlTable.Cell(o.Reserved ? "on" : "off"));
            row.AddCell(new MyGuiControlTable.Cell(o.Priority == 0 ? "-" : o.Priority.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(ShortStatus(o), null, LongStatus(o)));
            _capabilities.Add(row);
        }
        RestoreSelection();
    }

    private void RestoreSelection()
    {
        if (string.IsNullOrEmpty(_selectedCap)) return;
        for (int i = 0; i < _capabilities.RowsCount; i++)
        {
            if (!(_capabilities.GetRow(i).UserData is WireNodeCapabilityOption)) continue;
            var option = (WireNodeCapabilityOption)_capabilities.GetRow(i).UserData;
            if (!string.Equals(option.Cap, _selectedCap, StringComparison.Ordinal)) continue;
            _capabilities.SelectedRowIndex = i;
            return;
        }
        _selectedCap = null;
    }

    private void RefreshDetail(WireNodeDetail detail)
    {
        _detail.Clear();
        WireNodeCapabilityOption selected;
        if (!TryGetSelected(out selected))
        {
            AppendLine("Pick a capability to see what installing it here would cost and what is "
                       + "in its way.");
            AppendLine("");
            AppendLine("Auto-invest and priority are faction-wide: they apply to that capability "
                       + "everywhere you build it, not only at this station.");
            return;
        }

        AppendLine(selected.Cap);
        AppendLine("Installed here: " + selected.Level + " of " + selected.MaxLevel);
        AppendLine("");
        AppendLine(LongStatus(selected));
        if (selected.Cost > 0) AppendLine("Cost: " + selected.Cost + " credits");
        AppendLine("");
        AppendLine("Auto-invest: " + (selected.Reserved ? "on" : "off"));
        AppendLine(selected.Reserved
            ? "Factions may stage materials toward this on its own."
            : "Your components are never reserved toward this while it is off.");
        AppendLine("");
        AppendLine("Install priority: " + selected.Priority);
        AppendLine(selected.Priority > 0
            ? "Outranks any ship build below " + selected.Priority + " for contested components."
            : "Ship builds claim contested components first (the default).");
    }

    private void AppendLine(string text)
    {
        if (!string.IsNullOrEmpty(text)) _detail.AppendText(text);
        _detail.AppendLine();
    }

    /// <summary>Buttons turn on with a selection and nothing else. A blocked install stays
    /// clickable on purpose: the client holds no copy of the gate to disable it against, and the
    /// server's refusal is a sentence where a dead button is silence. The Status column and the
    /// detail panel already say what is in the way before the click.</summary>
    private void RefreshButtons()
    {
        WireNodeCapabilityOption selected;
        bool haveSelection = TryGetSelected(out selected);
        SetButtonsEnabled(haveSelection);

        // Says what the click will do, matching the station window's Pause/Resume button, rather
        // than restating the state the Auto column already shows.
        if (_reserve != null)
            _reserve.Text = haveSelection && selected.Reserved ? "Stop auto-invest" : "Start auto-invest";
    }

    private void SetButtonsEnabled(bool enabled)
    {
        if (_install != null) _install.Enabled = enabled;
        if (_reserve != null) _reserve.Enabled = enabled;
        if (_priorityUp != null) _priorityUp.Enabled = enabled;
        if (_priorityDown != null) _priorityDown.Enabled = enabled;
    }

    private void RefreshStatus(NodeDetailComponent component, WireNodeDetail detail)
    {
        // A command's reply outranks the age line for as long as it is fresh: it is the only place
        // a refusal ("costs 5000 credits - [ABC] has 900") is ever shown in full.
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds
               < NodeDetailComponent.CommandMessageSeconds)
        {
            _status.Text = OneLine(component.LastCommandMessage);
            return;
        }

        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        int ready = 0;
        WireNodeCapabilityOption[] options = detail.Develop ?? new WireNodeCapabilityOption[0];
        for (int i = 0; i < options.Length; i++)
            if (options[i].Block == (byte)WireDevelopBlock.None) ready++;
        _status.Text = ready + " of " + options.Length + " installable now   -   updated "
                       + age + "s ago";
    }

    // ---- Actions ------------------------------------------------------------------------------

    private void OnCapabilitySelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        _selectedCap = row != null && row.UserData is WireNodeCapabilityOption
            ? ((WireNodeCapabilityOption)row.UserData).Cap
            : null;
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component != null && component.HasSnapshot) RefreshDetail(component.Detail);
        RefreshButtons();
    }

    private void OnInstallClicked(MyGuiControlButton button)
    {
        Send(WireNodeCommand.Develop, 0);
    }

    private void OnReserveClicked(MyGuiControlButton button)
    {
        // The server verb is a flip, not a set, so the button sends no state — which is also what
        // keeps a stale snapshot from turning a click into a no-op against the value it displays.
        Send(WireNodeCommand.ReserveToggle, 0);
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
    /// row it is acting on rather than sending a delta. Clamped at 0: below it the setting is
    /// removed anyway, and a negative rank means nothing to the claim scale.</summary>
    private void SendPriorityDelta(int delta)
    {
        WireNodeCapabilityOption selected;
        if (!TryGetSelected(out selected)) return;
        int rank = selected.Priority + delta;
        Send(WireNodeCommand.InstallPriority, rank < 0 ? 0 : rank);
    }

    private void Send(WireNodeCommand command, int value)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component == null || string.IsNullOrEmpty(_selectedCap)) return;
        component.SendCommand(command, _selectedCap, value);
    }

    private bool TryGetSelected(out WireNodeCapabilityOption option)
    {
        option = default(WireNodeCapabilityOption);
        MyGuiControlTable.Row row = _capabilities != null ? _capabilities.SelectedRow : null;
        if (row == null || !(row.UserData is WireNodeCapabilityOption)) return false;
        option = (WireNodeCapabilityOption)row.UserData;
        return true;
    }

    // ---- Formatting ---------------------------------------------------------------------------

    private static string CostText(WireNodeCapabilityOption option)
    {
        // Zero is not free: the gate stops before it resolves a price, so there is no cost to show.
        return option.Cost > 0 ? option.Cost.ToString() : "-";
    }

    private static string ShortStatus(WireNodeCapabilityOption option)
    {
        switch ((WireDevelopBlock)option.Block)
        {
            case WireDevelopBlock.None:                return "Ready";
            case WireDevelopBlock.AtMaxLevel:          return "At max";
            case WireDevelopBlock.PrerequisiteMissing: return "Needs " + option.BlockKey;
            case WireDevelopBlock.InsufficientTreasury:
                return "Short " + (option.BlockNeed - option.BlockHave) + " cr";
            case WireDevelopBlock.InsufficientStock:   return "Short " + option.BlockKey;
            case WireDevelopBlock.NotFounded:          return "No station";
            default:                                   return "";
        }
    }

    private static string LongStatus(WireNodeCapabilityOption option)
    {
        switch ((WireDevelopBlock)option.Block)
        {
            case WireDevelopBlock.None:
                return "Ready to install.";
            case WireDevelopBlock.AtMaxLevel:
                return "Already at this capability's ceiling here.";
            case WireDevelopBlock.PrerequisiteMissing:
                return "Needs " + option.BlockKey + " installed somewhere in your faction's network "
                       + "first - not necessarily at this station.";
            case WireDevelopBlock.InsufficientTreasury:
                return "Treasury is short " + (option.BlockNeed - option.BlockHave) + " credits: "
                       + "the install costs " + option.BlockNeed + " and your faction holds "
                       + option.BlockHave + ".";
            case WireDevelopBlock.InsufficientStock:
                return "Needs " + (option.BlockNeed - option.BlockHave) + " more " + option.BlockKey
                       + ": the install wants " + option.BlockNeed + " and your faction holds "
                       + option.BlockHave + " across every station.";
            case WireDevelopBlock.NotFounded:
                return "This claim has no founded station to install into.";
            default:
                return "";
        }
    }

    /// <summary>The runtime's replies are written for chat and can carry newlines; the status line
    /// is a single <see cref="MyGuiControlLabel"/>.</summary>
    private static string OneLine(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
