using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Nodes;

/// <summary>
/// Withdraw from and deposit into a node's own stock, with the player standing at it.
///
/// <para><b>Why a window rather than controls in the store block's tab.</b> The store screen is an
/// MVVM screen whose tabs are code-generated from XAML, and a binding onto anything of ours has no
/// generated counterpart to bind through. A conventional <see cref="MyGuiScreenBase"/> opens over it
/// — <c>MyGuiScreenMvvmBase</c> sets <c>CanBeHidden</c> — so both the store tabs and the node window
/// launch the same controls instead of two copies in two frameworks whose refresh paths would have
/// to be solved twice.</para>
///
/// <para><b>The columns are two different questions.</b> "At station" and "Reserved" are the
/// server's, with every reserve already deducted from the first, so a number offered here is one the
/// server will honour; "To hand" is counted locally from whatever
/// <see cref="TransferTarget"/> resolved, and is only ever used to fill in an amount. The stock is
/// re-read from the server after every transfer rather than adjusted here.</para>
///
/// <para>Its interior offsets are <see cref="StationHoldScreen"/>'s, for the reason that screen
/// records: these are the ones confirmed in-game.</para>
/// </summary>
internal sealed class StationTransferScreen : MyGuiScreenBase
{
    private const int Columns = 4;

    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static StationTransferScreen Instance { get; private set; }

    /// <summary>The question this window asks. One of the two is meaningful, matching the wire: a
    /// station for the store block, a node id for the node window.</summary>
    private readonly long _stationId;
    private readonly string _nodeId;

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _target;
    private MyGuiControlLabel _status;
    private MyGuiControlTable _rows;
    private MyGuiControlTextbox _amount;
    private MyGuiControlButton _withdraw;
    private MyGuiControlButton _withdrawMax;
    private MyGuiControlButton _deposit;
    private MyGuiControlButton _depositMax;

    private int _drawnRevision = -1;

    /// <summary>What the table currently shows, so an unchanged snapshot leaves it alone.</summary>
    private string _drawnRows;

    /// <summary>Inventory key of the selected row, so a snapshot arriving while the player reads the
    /// list does not move the selection out from under the buttons.</summary>
    private string _selectedKey;

    /// <summary>The target as it was when the list was last built, so a click acts on the same
    /// containers the numbers were counted from.</summary>
    private TransferTarget _resolved;

    private StationTransferScreen(long stationId, string nodeId)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _stationId = stationId;
        _nodeId = nodeId ?? "";
        m_closeOnEsc = true;
        CanBeHidden = true;
        // The store screen or the node window behind this keeps drawing.
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    /// <summary>Opens on a station the store block belongs to. The client does not know which node
    /// that is; the server answers with it.</summary>
    internal static void OpenForStation(long stationId)
    {
        Open(stationId, "");
    }

    /// <summary>Opens on a node the node window is already showing, founded or not.</summary>
    internal static void OpenForNode(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        Open(0, nodeId);
    }

    private static void Open(long stationId, string nodeId)
    {
        try
        {
            StationTransferScreen existing = Instance;
            if (existing != null)
            {
                if (existing._stationId == stationId
                    && string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                // A window on another node is closed rather than retargeted: its selection belongs
                // to that node's stock.
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new StationTransferScreen(stationId, nodeId));
        }
        catch (Exception ex)
        {
            Plugin.Log("station transfer window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.StationTransferScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        NodeStockComponent component = NodeStockComponent.Instance;
        if (component != null) component.Query(_stationId, _nodeId, force: true);
    }

    public override void OnRemoved()
    {
        // Guarded on still being the current window, for the reason NodeDetailScreen records: SE
        // defers both the removal and the add, so a window replaced by one on another node can be
        // removed after its successor has already loaded.
        if (Instance == this) Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Station stock");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        // VisibleRowsCount, not Size.Y, decides how tall this ends up -- MyGuiControlTable's
        // RefreshInternals overwrites Size.Y. Everything below is placed off the table's real size
        // afterwards.
        _rows = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, top + 0.045f),
            Size = new Vector2(0.86f, 0f),
            Name = "StationTransferRows",
            ColumnsCount = Columns,
            VisibleRowsCount = 10,
        };
        _rows.SetCustomColumnWidths(new float[Columns] { 0.40f, 0.20f, 0.20f, 0.20f });
        _rows.SetColumnName(0, new StringBuilder("Item"));
        _rows.SetColumnName(1, new StringBuilder("At station"));
        _rows.SetColumnName(2, new StringBuilder("Reserved"));
        _rows.SetColumnName(3, new StringBuilder("To hand"));
        _rows.ItemSelected += OnRowSelected;
        Controls.Add(_rows);

        float row = top + 0.045f + _rows.Size.Y + 0.015f;

        _target = new MyGuiControlLabel(
            new Vector2(left, row), null, "", null, 0.75f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_target);

        row += 0.035f;
        var amountLabel = new MyGuiControlLabel(
            new Vector2(left, row + 0.008f), null, "Amount", null, 0.75f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(amountLabel);

        _amount = new MyGuiControlTextbox(defaultText: "0")
        {
            Position = new Vector2(left + 0.10f, row),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = new Vector2(0.16f, 0.04f),
            Name = "StationTransferAmount",
        };
        Controls.Add(_amount);

        var buttonSize = new Vector2(0.16f, 0.04f);
        const float gap = 0.17f;

        row += 0.05f;
        _withdraw = AddActionButton(new Vector2(left, row), buttonSize, "Withdraw",
            "Move the amount from this station's stock into the target inventory. The server moves "
            + "what actually fits and says how much that was.", OnWithdrawClicked);
        _withdrawMax = AddActionButton(new Vector2(left + gap, row), buttonSize, "Max",
            "Fill the amount with everything the station will release for this item. Stock a build "
            + "or an install has claimed is not part of it.", OnWithdrawMaxClicked);

        row += 0.05f;
        _deposit = AddActionButton(new Vector2(left, row), buttonSize, "Deposit",
            "Move the amount out of the target inventory and into this station's stock. No money "
            + "changes hands -- it is already your faction's.", OnDepositClicked);
        _depositMax = AddActionButton(new Vector2(left + gap, row), buttonSize, "Max",
            "Fill the amount with everything of this item the target inventory holds.",
            OnDepositMaxClicked);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawnRevision = -1;
        _drawnRows = null;
    }

    /// <summary>Named arguments, copied from <see cref="StationHoldScreen"/>'s: this constructor's
    /// positional order is not the one it reads like.</summary>
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
        NodeStockComponent component = NodeStockComponent.Instance;
        if (component != null)
        {
            // Re-asked every frame and throttled inside the component: a window opened before the
            // uplink was granted starts filling as soon as it is, and the listing does not go stale
            // behind a build that is draining the same stock.
            component.Query(_stationId, _nodeId, force: false);
            if (component.Revision != _drawnRevision) Rebuild(component);
        }
        return base.Draw();
    }

    private void Rebuild(NodeStockComponent component)
    {
        _drawnRevision = component.Revision;

        if (!NodeStockComponent.Available)
        {
            WriteUnavailable("This server does not offer station transfers.");
            return;
        }
        if (!component.HasSnapshot)
        {
            WriteUnavailable("Waiting for the server...");
            return;
        }
        if (!component.MayTransfer)
        {
            WriteUnavailable(string.IsNullOrEmpty(component.Message)
                ? "Nothing here is yours to move."
                : component.Message);
            return;
        }

        _heading.Text = string.IsNullOrEmpty(component.NodeName) ? component.NodeId : component.NodeName;

        _resolved = TransferTarget.Resolve();
        _target.Text = "Target: " + (string.IsNullOrEmpty(_resolved.Label)
            ? "nothing reachable -- fly a ship here or select an inventory"
            : _resolved.Label);

        RebuildRows(component, _resolved.Held());
        RefreshButtons(component);
        RefreshStatus(component);
    }

    /// <summary>Every state with nothing to act on, through one path, so no half-populated list can
    /// survive any of them.</summary>
    private void WriteUnavailable(string message)
    {
        if (_heading != null)
            _heading.Text = _nodeId.Length > 0 ? _nodeId : "Station stock";
        if (_rows != null) _rows.Clear();
        _drawnRows = null;
        _selectedKey = null;
        _resolved = null;
        if (_target != null) _target.Text = "";
        SetButtonsEnabled(false, false);
        if (_status != null) _status.Text = message;
    }

    /// <summary>
    /// The union of what the node holds and what the target has to hand.
    ///
    /// <para>The second half is what makes Deposit usable at all: a station short of a component is
    /// holding none of it, so a list of only the node's own rows would offer nothing to put in.</para>
    ///
    /// <para>The component re-asks every couple of seconds and every answer bumps its revision, so
    /// the rows are rebuilt only when what they show has changed, and then without moving the
    /// scroll or the selection (<see cref="TableRows"/>).</para>
    /// </summary>
    private void RebuildRows(NodeStockComponent component, Dictionary<string, long> held)
    {
        var entries = new List<(string Key, string Label, long Available, long Reserved, long ToHand)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        WireNodeTransferRow[] rows = component.Rows ?? new WireNodeTransferRow[0];
        for (int i = 0; i < rows.Length; i++)
        {
            WireNodeTransferRow item = rows[i];
            if (string.IsNullOrEmpty(item.Key)) continue;
            seen.Add(item.Key);
            long toHand;
            held.TryGetValue(item.Key, out toHand);
            entries.Add((item.Key, item.Label, item.Available, item.Reserved, toHand));
        }

        foreach (KeyValuePair<string, long> entry in held)
        {
            if (seen.Contains(entry.Key)) continue;
            entries.Add((entry.Key, null, 0, 0, entry.Value));
        }

        var signature = new StringBuilder();
        foreach (var e in entries)
            signature.Append(e.Key).Append('\u001f').Append(e.Label).Append('\u001f')
                     .Append(e.Available).Append('\u001f').Append(e.Reserved).Append('\u001f')
                     .Append(e.ToHand).Append('\u001e');
        if (!TableRows.Changed(ref _drawnRows, signature.ToString())) return;

        TableRows.PreservingScroll(_rows, () =>
        {
            _rows.Clear();
            foreach (var e in entries) AddRow(e.Key, e.Label, e.Available, e.Reserved, e.ToHand);
            RestoreSelection();
        });
    }

    private void AddRow(string key, string label, long available, long reserved, long toHand)
    {
        var data = new Row(key, available, reserved, toHand);
        var row = new MyGuiControlTable.Row(data);
        row.AddCell(new MyGuiControlTable.Cell(
            string.IsNullOrEmpty(label) ? key : label, null, key));
        row.AddCell(new MyGuiControlTable.Cell(available.ToString(CultureInfo.InvariantCulture)));
        row.AddCell(new MyGuiControlTable.Cell(
            reserved > 0 ? reserved.ToString(CultureInfo.InvariantCulture) : "-"));
        row.AddCell(new MyGuiControlTable.Cell(
            toHand > 0 ? toHand.ToString(CultureInfo.InvariantCulture) : "-"));
        _rows.Add(row);
    }

    private void RestoreSelection()
    {
        if (string.IsNullOrEmpty(_selectedKey)) return;
        for (int i = 0; i < _rows.RowsCount; i++)
        {
            var data = _rows.GetRow(i).UserData as Row;
            if (data == null || !string.Equals(data.Key, _selectedKey, StringComparison.Ordinal)) continue;
            _rows.SelectedRowIndex = i;
            return;
        }
        // The station ran out of it and the ship no longer carries any either.
        _selectedKey = null;
    }

    private Row Selected
    {
        get
        {
            MyGuiControlTable.Row row = _rows != null ? _rows.SelectedRow : null;
            return row != null ? row.UserData as Row : null;
        }
    }

    private void RefreshButtons(NodeStockComponent component)
    {
        Row selected = Selected;
        bool idle = !component.IsBusy;
        bool canWithdraw = idle && selected != null && selected.Available > 0
                           && _resolved != null && _resolved.CanWithdraw;
        bool canDeposit = idle && selected != null && selected.ToHand > 0
                          && _resolved != null && _resolved.CanDeposit;
        SetButtonsEnabled(canWithdraw, canDeposit);
    }

    private void SetButtonsEnabled(bool withdraw, bool deposit)
    {
        if (_withdraw != null) _withdraw.Enabled = withdraw;
        if (_withdrawMax != null) _withdrawMax.Enabled = withdraw;
        if (_deposit != null) _deposit.Enabled = deposit;
        if (_depositMax != null) _depositMax.Enabled = deposit;
    }

    private void RefreshStatus(NodeStockComponent component)
    {
        // A transfer's reply outranks the age line for as long as it is fresh: it is the only place
        // a partial move or a refusal is ever shown.
        if (!string.IsNullOrEmpty(component.LastResultMessage)
            && (DateTime.UtcNow - component.LastResultUtc).TotalSeconds
               < NodeStockComponent.ResultMessageSeconds)
        {
            _status.Text = OneLine(component.LastResultMessage);
            return;
        }

        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        _status.Text = _rows.RowsCount + " item(s)   -   updated " + age + "s ago";
    }

    // ---- Actions ------------------------------------------------------------------------------

    private void OnRowSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        var data = row != null ? row.UserData as Row : null;
        _selectedKey = data != null ? data.Key : null;
        NodeStockComponent component = NodeStockComponent.Instance;
        if (component != null) RefreshButtons(component);
    }

    private void OnWithdrawMaxClicked(MyGuiControlButton button)
    {
        Row selected = Selected;
        if (selected == null) return;
        _amount.Text = selected.Available.ToString(CultureInfo.InvariantCulture);
    }

    private void OnDepositMaxClicked(MyGuiControlButton button)
    {
        Row selected = Selected;
        if (selected == null) return;
        _amount.Text = selected.ToHand.ToString(CultureInfo.InvariantCulture);
    }

    private void OnWithdrawClicked(MyGuiControlButton button)
    {
        Send(deposit: false);
    }

    private void OnDepositClicked(MyGuiControlButton button)
    {
        Send(deposit: true);
    }

    /// <summary>Sends one transfer and reports what could not even be attempted. The amount is
    /// clamped to what this side can see, but only as a courtesy: the server clamps it again against
    /// the reserves and the target's real capacity, and the result says what actually moved.</summary>
    private void Send(bool deposit)
    {
        NodeStockComponent component = NodeStockComponent.Instance;
        Row selected = Selected;
        if (component == null || selected == null || _resolved == null) return;

        long amount;
        if (!long.TryParse(_amount.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount)
            || amount <= 0)
        {
            _status.Text = "Type how many to move first.";
            return;
        }

        long ceiling = deposit ? selected.ToHand : selected.Available;
        if (amount > ceiling) amount = ceiling;
        if (amount <= 0)
        {
            _status.Text = deposit
                ? "Nothing of that to hand."
                : "The station will not release any of that right now.";
            return;
        }

        List<long> targets = deposit ? _resolved.DepositFrom : _resolved.WithdrawInto;
        if (!component.BeginTransfer(deposit, selected.Key, amount, new List<long>(targets)))
        {
            _status.Text = "Could not send that.";
            return;
        }

        SetButtonsEnabled(false, false);
        _status.Text = (deposit ? "Depositing " : "Withdrawing ") + amount + " " + selected.Key + "...";
    }

    /// <summary>The server's replies can carry newlines; the status line is a single label.</summary>
    private static string OneLine(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>One listed key, carried on the table row so a click reads the numbers the row was
    /// drawn from rather than re-parsing its cells.</summary>
    private sealed class Row
    {
        internal readonly string Key;
        internal readonly long Available;
        internal readonly long Reserved;
        internal readonly long ToHand;

        internal Row(string key, long available, long reserved, long toHand)
        {
            Key = key;
            Available = available;
            Reserved = reserved;
            ToHand = toHand;
        }
    }
}
