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
/// The standing orders anchored at one station, in two modes switched from the heading row: Mine
/// orders (what this station keeps digging around it, how often, in what mix) and Stock orders (how
/// much of an item this station keeps, FAC-431). One table serves both; the mode decides its
/// columns and which kind of row it shows.
///
/// <para>Opened from <c>Orders...</c> on the station window. A mine order names a hub and works the
/// deposits within reach of it, so the station IS its subject — which is why this replaced the
/// faction-wide standing-order window rather than being a filter on it
/// (docs/fleet-doctrine-plan.md, decision 4). The list behind it is still faction-wide and this
/// window shows the slice anchored here; the count in the heading says both.</para>
///
/// <para>Draws over the station window rather than replacing it:
/// <see cref="MyGuiScreenBase.CanHideOthers"/> stays false, so that window keeps drawing — and keeps
/// re-asserting its own node watch — behind this one.</para>
/// </summary>
internal sealed class StationMineScreen : MyGuiScreenBase
{
    private const int OrderColumns = 6;

    /// <summary>Deliberately <c>NodeDetailScreen</c>'s: these interior offsets are the ones
    /// confirmed in-game.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static StationMineScreen Instance { get; private set; }

    private readonly string _nodeId;
    private readonly string _nodeName;

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlTable _orders;
    private MyGuiControlMultilineText _detail;
    private MyGuiControlButton _newOrder;
    private MyGuiControlButton _pause;
    private MyGuiControlButton _remove;
    private MyGuiControlButton _priorityUp;
    private MyGuiControlButton _priorityDown;
    private MyGuiControlButton _mineMode;
    private MyGuiControlButton _stockMode;

    /// <summary>False shows Mine orders, true shows Stock orders.</summary>
    private bool _showStock;

    private int _drawnRevision = -1;

    /// <summary>Id of the selected row, so a snapshot arriving while the player reads one does not
    /// move the selection out from under the action buttons.</summary>
    private string _selectedOrderId;

    private StationMineScreen(string nodeId, string nodeName)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _nodeId = nodeId;
        _nodeName = nodeName;
        m_closeOnEsc = true;
        CanBeHidden = true;
        // Left false on purpose: the station window behind this one keeps drawing.
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    /// <summary>Opens the window on <paramref name="nodeId"/>, or retargets the one already open by
    /// closing it and opening the new one — the orders shown are a property of the station, so a
    /// window on another one is a different window.</summary>
    internal static void Open(string nodeId, string nodeName)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        try
        {
            StationMineScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new StationMineScreen(nodeId, nodeName));
        }
        catch (Exception ex)
        {
            Plugin.Log("station mine window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.StationMineScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        StationMineComponent component = StationMineComponent.Instance;
        if (component != null) component.SetStreaming(true);
    }

    public override void OnRemoved()
    {
        // The one hook every close path goes through, so the server's subscription is always
        // released. Guarded on still being the current window for the reason the node window
        // records: SE defers both the removal and the add, so a replaced window can be removed after
        // its successor has already subscribed.
        if (Instance != this) { base.OnRemoved(); return; }

        StationMineComponent component = StationMineComponent.Instance;
        if (component != null) component.SetStreaming(false);
        Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Orders");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        // VisibleRowsCount, not Size.Y, decides how tall this ends up: MyGuiControlTable's
        // RefreshInternals OVERWRITES Size.Y with RowHeight * (VisibleRowsCount + 1) plus its
        // texture minimum. Everything below the table is placed from its REAL size afterwards.
        _orders = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, top + 0.04f),
            Size = new Vector2(0.52f, 0f),
            Name = "StationMineOrders",
            ColumnsCount = OrderColumns,
            VisibleRowsCount = 11,
        };
        SetColumns();
        _orders.ItemSelected += OnOrderSelected;
        Controls.Add(_orders);

        // Measured off the table's post-construction size rather than a matching constant, so the
        // two columns cannot drift apart when VisibleRowsCount changes. The scrollbar lives INSIDE
        // Size.X, so a plain gap beyond the table's right edge clears it.
        float tableHeight = _orders.Size.Y;
        float detailLeft = left + _orders.Size.X + 0.02f;

        _detail = new MyGuiControlMultilineText(
            position: new Vector2(detailLeft, top + 0.04f),
            size: new Vector2(0.31f, tableHeight),
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
            Name = "StationMineDetail",
            CanHaveFocus = true,
        };
        Controls.Add(_detail);

        // The mode pair sits on the heading row over the detail pane, which is the one free strip
        // above the table: nothing below the heading moves.
        var modeSize = new Vector2(0.15f, 0.035f);
        _mineMode = AddActionButton(new Vector2(detailLeft, top - 0.005f), modeSize, "Mine orders",
            "Recurring mining around this station.", _ => SetMode(false));
        _stockMode = AddActionButton(new Vector2(detailLeft + 0.16f, top - 0.005f), modeSize, "Stock orders",
            "Items this station keeps in stock: it asks for what is missing and holds the target.",
            _ => SetMode(true));

        // Two rows of three under the table rather than one row of five: a single row of button
        // widths plus gaps has to fit the window's INTERIOR, which is narrower than WindowSize.X.
        var buttonSize = new Vector2(0.16f, 0.04f);
        const float buttonGap = 0.17f;
        float buttonTop = top + 0.04f + tableHeight + 0.015f;
        _newOrder = AddActionButton(new Vector2(left, buttonTop), buttonSize, "New order...",
            "Add a recurring mine order anchored at this station.",
            OnNewClicked);
        _priorityUp = AddActionButton(new Vector2(left + buttonGap, buttonTop), buttonSize, "Priority +1",
            "Raise this order. A Mine order ranked higher wins a deposit two orders both want; a Stock order "
            + "ranked 1 or more takes its item's materials ahead of your ship builds.",
            OnPriorityUpClicked);
        _priorityDown = AddActionButton(new Vector2(left + buttonGap * 2f, buttonTop), buttonSize, "Priority -1",
            "Lower this order against the others.",
            OnPriorityDownClicked);
        _pause = AddActionButton(new Vector2(left, buttonTop + 0.05f), buttonSize, "Pause",
            "Stop evaluating this order. It keeps its id and rank.",
            OnPauseClicked);
        _remove = AddActionButton(new Vector2(left + buttonGap, buttonTop + 0.05f), buttonSize, "Remove",
            "Delete this order. Ids are never reused, so the others keep theirs.",
            OnRemoveClicked);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawnRevision = -1;
    }

    private void SetColumns()
    {
        if (_showStock)
        {
            _orders.SetCustomColumnWidths(new float[OrderColumns]
                { 0.13f, 0.33f, 0.14f, 0.14f, 0.12f, 0.14f });
            _orders.SetColumnName(0, new StringBuilder("Id"));
            _orders.SetColumnName(1, new StringBuilder("Item"));
            _orders.SetColumnName(2, new StringBuilder("Target"));
            _orders.SetColumnName(3, new StringBuilder("Have"));
            _orders.SetColumnName(4, new StringBuilder("Short"));
            _orders.SetColumnName(5, new StringBuilder("State"));
        }
        else
        {
            _orders.SetCustomColumnWidths(new float[OrderColumns]
                { 0.13f, 0.09f, 0.13f, 0.29f, 0.18f, 0.18f });
            _orders.SetColumnName(0, new StringBuilder("Id"));
            _orders.SetColumnName(1, new StringBuilder("Pri"));
            _orders.SetColumnName(2, new StringBuilder("Period"));
            _orders.SetColumnName(3, new StringBuilder("Ore mix"));
            _orders.SetColumnName(4, new StringBuilder("Next scan"));
            _orders.SetColumnName(5, new StringBuilder("State"));
        }
    }

    private void SetMode(bool stock)
    {
        if (_showStock == stock) return;
        _showStock = stock;
        _selectedOrderId = null;
        if (_orders != null) SetColumns();
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
        StationMineComponent component = StationMineComponent.Instance;
        if (component != null)
        {
            // Retried every frame rather than only on open: a window opened before the uplink was
            // granted would otherwise stay blank until it was reopened.
            component.SetStreaming(true);
            // The countdown is derived from a timestamp, so it has to be redrawn on a clock rather
            // than only when the server sends something.
            if (_drawnRevision != component.Revision) Rebuild(component);
            else RefreshDetail(component);
        }
        return base.Draw();
    }

    private void Rebuild(StationMineComponent component)
    {
        _drawnRevision = component.Revision;

        if (!StationMineComponent.Available)
        {
            WriteUnavailable("This server does not offer standing orders to the client.");
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

        _heading.Text = StationLabel() + "   -   " + CountHere(component) + " here, ["
                        + component.FactionTag + "] holds " + component.Orders.Count + "/" + component.Cap
                        + " orders";
        // Stock orders re-send whenever a station's stock moves, so keep the player's place.
        TableRows.PreservingScroll(_orders, () => RebuildRows(component));
        RefreshButtons();
        RefreshDetail(component);
    }

    /// <summary>Every state where there is nothing to show. One path, so no half-populated table can
    /// survive any of them.</summary>
    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = StationLabel();
        if (_orders != null) _orders.Clear();
        if (_detail != null) { _detail.Clear(); _detail.AppendText(message); }
        _selectedOrderId = null;
        SetButtonsEnabled(false);
        if (_status != null) _status.Text = message;
    }

    private string StationLabel()
    {
        return string.IsNullOrEmpty(_nodeName) ? _nodeId : _nodeName + "  (" + _nodeId + ")";
    }

    /// <summary>Anchored at this station, or a Stock order for every station, and of the kind the
    /// window is showing.</summary>
    private bool AnchoredHere(WireStandingOrder o)
    {
        if (o.Kind != (_showStock ? (byte)1 : (byte)0)) return false;
        return (o.Kind == 1 && o.AllStations) || string.Equals(o.HubNodeId, _nodeId, StringComparison.Ordinal);
    }

    /// <summary>An all-stations order's target and stock at this station; false where it does not
    /// apply here (a per-node order for the item overrides it, or the station is not founded).</summary>
    private bool AppliesHere(WireStandingOrder o, out long target, out long have)
    {
        target = o.StationTarget;
        have = 0;
        if (o.StationIds == null || o.StationHaves == null) return false;
        for (int i = 0; i < o.StationIds.Length && i < o.StationHaves.Length; i++)
        {
            if (!string.Equals(o.StationIds[i], _nodeId, StringComparison.Ordinal)) continue;
            have = o.StationHaves[i];
            return true;
        }
        return false;
    }

    private int CountHere(StationMineComponent component)
    {
        int count = 0;
        for (int i = 0; i < component.Orders.Count; i++)
            if (AnchoredHere(component.Orders[i])) count++;
        return count;
    }

    private void RebuildRows(StationMineComponent component)
    {
        _orders.Clear();
        for (int i = 0; i < component.Orders.Count; i++)
        {
            WireStandingOrder o = component.Orders[i];
            if (!AnchoredHere(o)) continue;
            var row = new MyGuiControlTable.Row(o);
            row.AddCell(new MyGuiControlTable.Cell(o.Id));
            if (_showStock)
            {
                string item = string.IsNullOrEmpty(o.ItemName) ? o.ItemKey : o.ItemName;
                if (o.AllStations)
                {
                    long target, have;
                    bool here = AppliesHere(o, out target, out have);
                    row.AddCell(new MyGuiControlTable.Cell(item + "  (all stations)", null, o.ItemKey));
                    row.AddCell(new MyGuiControlTable.Cell(o.TargetQuantity.ToString()));
                    row.AddCell(new MyGuiControlTable.Cell(here ? have.ToString() : "-"));
                    row.AddCell(new MyGuiControlTable.Cell(here ? Math.Max(0, target - have).ToString() : "-"));
                    row.AddCell(new MyGuiControlTable.Cell(
                        o.Paused ? "paused" : here ? "running" : "not here",
                        null, here ? null : "A station order for this item overrides it here, or this station is not founded."));
                    _orders.Add(row);
                    continue;
                }
                long shortBy = Math.Max(0, o.TargetQuantity - o.Have);
                row.AddCell(new MyGuiControlTable.Cell(item, null, o.ItemKey));
                row.AddCell(new MyGuiControlTable.Cell(o.TargetQuantity.ToString()));
                row.AddCell(new MyGuiControlTable.Cell(o.Have.ToString()));
                row.AddCell(new MyGuiControlTable.Cell(shortBy.ToString()));
                row.AddCell(new MyGuiControlTable.Cell(o.Paused ? "paused" : "running"));
                _orders.Add(row);
                continue;
            }
            row.AddCell(new MyGuiControlTable.Cell(o.Priority.ToString()));
            row.AddCell(new MyGuiControlTable.Cell(((int)o.PeriodSeconds) + "s"));
            string ores = string.IsNullOrEmpty(o.OreWeights) ? "natural mix" : o.OreWeights;
            row.AddCell(new MyGuiControlTable.Cell(ores, null, ores));
            row.AddCell(new MyGuiControlTable.Cell(CountdownFor(o)));
            row.AddCell(new MyGuiControlTable.Cell(o.Paused ? "paused" : "running"));
            _orders.Add(row);
        }
        RestoreSelection();
    }

    private void RestoreSelection()
    {
        if (string.IsNullOrEmpty(_selectedOrderId)) return;
        for (int i = 0; i < _orders.RowsCount; i++)
        {
            if (!(_orders.GetRow(i).UserData is WireStandingOrder)) continue;
            var order = (WireStandingOrder)_orders.GetRow(i).UserData;
            if (!string.Equals(order.Id, _selectedOrderId, StringComparison.Ordinal)) continue;
            _orders.SelectedRowIndex = i;
            return;
        }
        // Removed while it was selected.
        _selectedOrderId = null;
    }

    /// <summary>Derived locally from the timestamp the server sent, so a scan cursor rewritten every
    /// pass does not re-send the whole list to every open window.</summary>
    private static string CountdownFor(WireStandingOrder o)
    {
        if (o.NextScanUtcTicks <= 0) return "due";
        double seconds = (new DateTime(o.NextScanUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds;
        return seconds > 0 ? "in " + ((int)seconds) + "s" : "due";
    }

    private void RefreshButtons()
    {
        bool selected = _selectedOrderId != null;
        SetButtonsEnabled(selected);
        if (_pause == null) return;
        WireStandingOrder? order = SelectedOrder();
        _pause.Text = order.HasValue && order.Value.Paused ? "Resume" : "Pause";
    }

    /// <summary><paramref name="enabled"/> gates the four actions that act on the selected row.
    /// "New order..." is deliberately outside it — creating one needs no selection — and is gated on
    /// the feature being granted, NOT on the pick-list having arrived: gating it on the list would
    /// make a missing options message indistinguishable from a dead button.</summary>
    private void SetButtonsEnabled(bool enabled)
    {
        if (_pause != null) _pause.Enabled = enabled;
        if (_remove != null) _remove.Enabled = enabled;
        // A Mine order's rank decides a contested deposit; a Stock order's decides its item's
        // materials against ship builds and capability installs.
        if (_priorityUp != null) { _priorityUp.Visible = true; _priorityUp.Enabled = enabled; }
        if (_priorityDown != null) { _priorityDown.Visible = true; _priorityDown.Enabled = enabled; }
        if (_newOrder != null) _newOrder.Enabled = StationMineComponent.Available;
        bool modes = StationMineComponent.Available;
        if (_mineMode != null) _mineMode.Enabled = modes && _showStock;
        if (_stockMode != null) _stockMode.Enabled = modes && !_showStock;
    }

    private void RefreshDetail(StationMineComponent component)
    {
        if (_detail == null || _status == null) return;

        WireStandingOrder? selected = SelectedOrder();
        _detail.Clear();
        if (!selected.HasValue)
        {
            if (CountHere(component) > 0)
                _detail.AppendText("Select an order to act on it.");
            else if (_showStock)
                _detail.AppendText("No stock orders at this station.\n\nA stock order keeps a quantity of "
                  + "one item here. The station asks for whatever is missing, which your hauls, your "
                  + "own production or a trade partner can fill, and it will not ship out or sell "
                  + "the quantity it keeps.\n\nNew order... adds one.");
            else
                _detail.AppendText("No mine orders at this station.\n\nA mine order scans everything "
                  + "mineable within reach of this station and keeps one fleet per deposit heading "
                  + "there.\n\nNew order... adds one.");
        }
        else
        {
            WriteDetail(selected.Value);
        }

        // A command's reply outranks the age line while it is fresh: it is the only place a refusal
        // is ever shown.
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds
               < StationMineComponent.CommandMessageSeconds)
        {
            _status.Text = OneLine(component.LastCommandMessage);
            return;
        }

        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        _status.Text = (_showStock ? "Stock is kept here and not shipped out" : "Higher rank is evaluated first")
                       + "   -   updated " + age + "s ago";
    }

    /// <summary>What a Stock order's rank does (FAC-501), shown under both kinds of Stock order.</summary>
    private const string RankText =
        "Rank 1 or more puts this item first: the materials to make it, and the item itself here, are "
        + "kept from your ship builds and capability upgrades ranked below it. At 0 they come first.";

    private void WriteDetail(WireStandingOrder o)
    {
        if (o.Kind == 1 && o.AllStations)
        {
            long target, have;
            bool here = AppliesHere(o, out target, out have);
            int applied = o.StationIds == null ? 0 : o.StationIds.Length;
            int shortAt = 0;
            for (int i = 0; o.StationHaves != null && i < o.StationHaves.Length; i++)
                if (o.StationHaves[i] < target) shortAt++;
            AppendLine(o.Id + (o.Paused ? "   PAUSED" : ""));
            AppendLine("Rank " + o.Priority);
            AppendLine("");
            AppendLine("Stations: all your founded stations (" + applied + ")");
            AppendLine("Item: " + (string.IsNullOrEmpty(o.ItemName) ? o.ItemKey : o.ItemName + "  (" + o.ItemKey + ")"));
            AppendLine("Keep at each: " + o.TargetQuantity + (target != o.TargetQuantity ? "  (" + target + " with your other all-stations orders)" : ""));
            AppendLine("Short at: " + shortAt + " of " + applied + " stations");
            AppendLine(here
                ? "Here: have " + have + ", short " + Math.Max(0, target - have)
                : "Here: not applied. A station order for this item sets this station's target, or the station is not founded.");
            AppendLine("");
            AppendLine("Every station you own or found later keeps this many. A station order for the same "
                       + "item replaces it at that station, higher or lower. Each station asks for its own "
                       + "missing quantity and holds what it keeps.");
            AppendLine("");
            AppendLine(RankText);
            return;
        }
        if (o.Kind == 1)
        {
            AppendLine(o.Id + (o.Paused ? "   PAUSED" : ""));
            AppendLine("Rank " + o.Priority);
            AppendLine("");
            AppendLine("Station: " + StationLabel());
            AppendLine("Item: " + (string.IsNullOrEmpty(o.ItemName) ? o.ItemKey : o.ItemName + "  (" + o.ItemKey + ")"));
            AppendLine("Keep: " + o.TargetQuantity);
            AppendLine("Have: " + o.Have);
            AppendLine("Short: " + Math.Max(0, o.TargetQuantity - o.Have));
            AppendLine("");
            AppendLine("While short, the station asks for the missing quantity. Your other stations' "
                       + "surplus, your own production and trade partners fill it the same way they fill "
                       + "any other demand. The kept quantity is not shipped to other stations or sold; "
                       + "what you take out yourself is ordered again.");
            AppendLine("");
            AppendLine(RankText);
            return;
        }

        AppendLine(o.Id + (o.Paused ? "   PAUSED" : ""));
        AppendLine("Rank " + o.Priority);
        AppendLine("");
        AppendLine("Hub: " + StationLabel());
        AppendLine("Period ceiling: " + ((int)o.PeriodSeconds) + "s");
        AppendLine("Ores: " + (string.IsNullOrEmpty(o.OreWeights) ? "each deposit's natural mix" : o.OreWeights));
        AppendLine("Next scan: " + CountdownFor(o));
        AppendLine("");
        AppendLine("Every scan finds what is mineable within reach of the hub and keeps one fleet "
                   + "per deposit heading there. The period is a ceiling on the fallback poll rate, "
                   + "not a floor - a deposit coming off cooldown scans sooner.");
    }

    private void AppendLine(string text)
    {
        _detail.AppendText(text);
        _detail.AppendLine();
    }

    // ---- Actions ------------------------------------------------------------------------------

    private WireStandingOrder? SelectedOrder()
    {
        MyGuiControlTable.Row row = _orders != null ? _orders.SelectedRow : null;
        if (row == null || !(row.UserData is WireStandingOrder)) return null;
        return (WireStandingOrder)row.UserData;
    }

    private void OnOrderSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        _selectedOrderId = row != null && row.UserData is WireStandingOrder
            ? ((WireStandingOrder)row.UserData).Id
            : null;
        RefreshButtons();
    }

    private void OnNewClicked(MyGuiControlButton button)
    {
        StationMineComponent component = StationMineComponent.Instance;
        if (component == null) return;
        if (_showStock)
        {
            if (!component.HasStockItems)
            {
                if (_status != null)
                    _status.Text = "Waiting for the server's item list - try again in a moment.";
                return;
            }
            NewStockOrderScreen.Open(_nodeId, StationLabel());
            return;
        }
        if (!component.HasOptions)
        {
            // The pick-list arrives once per subscribe. Without it this dialog cannot say whether
            // the station is even a legal hub, so say so where the player is already reading.
            if (_status != null)
                _status.Text = "Waiting for the server's node list - try again in a moment.";
            Plugin.Log("new mine order: options not received yet (hasOptions=false)");
            return;
        }
        if (!component.IsMineHub(_nodeId))
        {
            // The server's own eligibility flag, not a guess: nothing mineable is within this
            // station's reach, so an order anchored here would scan and find nothing.
            if (_status != null)
                _status.Text = "Nothing mineable is within reach of this station, so it cannot anchor "
                               + "a mine order.";
            return;
        }
        NewMineOrderScreen.Open(_nodeId, StationLabel());
    }

    private void OnPauseClicked(MyGuiControlButton button)
    {
        WireStandingOrder? order = SelectedOrder();
        if (!order.HasValue) return;
        Send(order.Value.Paused ? WireStandingOrderCommand.Resume : WireStandingOrderCommand.Pause, 0);
    }

    private void OnRemoveClicked(MyGuiControlButton button)
    {
        Send(WireStandingOrderCommand.Remove, 0);
    }

    private void OnPriorityUpClicked(MyGuiControlButton button)
    {
        SendPriorityDelta(1);
    }

    private void OnPriorityDownClicked(MyGuiControlButton button)
    {
        SendPriorityDelta(-1);
    }

    /// <summary>Rank is set absolutely server-side, so a relative button reads the current rank off
    /// the row it is acting on rather than sending a delta.</summary>
    private void SendPriorityDelta(int delta)
    {
        WireStandingOrder? order = SelectedOrder();
        if (!order.HasValue) return;
        Send(WireStandingOrderCommand.Priority, order.Value.Priority + delta);
    }

    private void Send(WireStandingOrderCommand command, int value)
    {
        StationMineComponent component = StationMineComponent.Instance;
        if (component == null || string.IsNullOrEmpty(_selectedOrderId)) return;
        component.SendCommand(command, _selectedOrderId, value);
    }

    /// <summary>The runtime's replies are written for chat and can carry newlines; the status line is
    /// a single <see cref="MyGuiControlLabel"/>.</summary>
    private static string OneLine(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
