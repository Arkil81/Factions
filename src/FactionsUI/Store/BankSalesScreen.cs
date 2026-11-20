using System;
using System.Collections.Generic;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Store;

/// <summary>
/// What the central bank may buy from the faction: every component, weapon, ammo and consumable the
/// server knows, what the faction holds of each, and one rule per item -- sell oversupply (the
/// default, and the bank's own rule), sell oversupply above a faction-wide target, or don't sell.
/// The bank only ever buys a node's oversupply; the rules only narrow that.
///
/// <para>Opened from the Store window, because what the faction sells is decided there, and because
/// that window needs no node -- a faction holding none can still mark an item kept before it has
/// any. The list is the whole catalogue for the same reason.</para>
///
/// <para>The table is built the way <see cref="HullPicker"/>'s is: a text filter over it, every
/// column sorting on a header click on the cell's raw value, and every column left-aligned with
/// the table's default margins. Left alignment is deliberate: SE applies a right-aligned column's
/// margin to the header and the rows with opposite signs, which is the header/number mismatch the
/// station window had to correct.</para>
/// </summary>
internal sealed class BankSalesScreen : MyGuiScreenBase
{
    private const int Columns = 5;

    /// <summary>The Store window's, so the two line up when one is opened over the other.</summary>
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    private static readonly Color DontSellColor = new Color(240, 120, 60);
    private static readonly Color TargetColor = new Color(240, 190, 60);

    internal static BankSalesScreen Instance { get; private set; }

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlTextbox _filter;
    private MyGuiControlTable _table;
    private MyGuiControlButton _sell;
    private MyGuiControlButton _dontSell;
    private MyGuiControlTextbox _target;
    private MyGuiControlButton _aboveTarget;
    private MyGuiControlButton _sellShown;
    private MyGuiControlButton _dontSellShown;
    private MyGuiControlButton _aboveTargetShown;

    private int _drawnRevision = -1;

    /// <summary>What the last rebuild drew, so a snapshot that changes nothing leaves the table and
    /// the player's scroll alone.</summary>
    private string _drawn;

    /// <summary>Held across rebuilds and sorts, both of which drop the table's own selection.</summary>
    private string _selectedKey;

    private BankSalesScreen()
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

    internal static void Open()
    {
        try
        {
            if (Instance != null) return;
            MyGuiSandbox.AddScreen(new BankSalesScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("bank sales window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.BankSalesScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        BankSalesComponent component = BankSalesComponent.Instance;
        if (component != null) component.SetStreaming(true);
    }

    public override void OnRemoved()
    {
        // Guarded on still being the current window, for the reason ShelfScreen records.
        if (Instance != this) { base.OnRemoved(); return; }

        BankSalesComponent component = BankSalesComponent.Instance;
        if (component != null) component.SetStreaming(false);
        Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Bank sales");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        float filterTop = top + 0.04f;
        Controls.Add(new MyGuiControlLabel(
            new Vector2(left, filterTop + 0.008f), null, "Filter", null, 0.75f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
        _filter = new MyGuiControlTextbox(defaultText: "", maxLength: 40)
        {
            Position = new Vector2(left + 0.08f, filterTop),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = new Vector2(0.3f, 0.04f),
            Name = "BankSalesFilter",
        };
        _filter.SetToolTip("Show only items whose name, type, id or rule contains this text -- "
                           + "\"don't\" or \"above\" lists the items with a rule of their own.");
        _filter.TextChanged += _ => Rebuild(BankSalesComponent.Instance);
        Controls.Add(_filter);

        // VisibleRowsCount, not Size.Y, decides the table's height: RefreshInternals overwrites
        // Size.Y, so the buttons are placed from the size read back after construction.
        float tableTop = filterTop + 0.055f;
        _table = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, tableTop),
            Size = new Vector2(0.86f, 0f),
            Name = "BankSalesItems",
            ColumnsCount = Columns,
            VisibleRowsCount = 11,
        };
        _table.SetCustomColumnWidths(new float[Columns] { 0.34f, 0.13f, 0.12f, 0.12f, 0.29f });
        string[] titles = { "Item", "Type", "Held", "Value", "Bank" };
        for (int i = 0; i < Columns; i++)
        {
            _table.SetColumnName(i, new StringBuilder(titles[i]));
            _table.SetColumnComparison(i, CompareCells);
        }
        _table.ItemSelected += OnRowSelected;
        _table.ItemDoubleClicked += OnRowDoubleClicked;
        // A header click has already sorted, and sorting drops the selection.
        _table.ColumnClicked += (t, c) => Reselect();
        Controls.Add(_table);

        // Two rows of four: the top row acts on the selected item, the bottom row on every item the
        // filter shows. The target box sits between the rule buttons and the one that reads it.
        var buttonSize = new Vector2(0.2f, 0.04f);
        const float buttonGap = 0.215f;
        float buttonTop = tableTop + _table.Size.Y + 0.015f;
        _sell = AddActionButton(new Vector2(left, buttonTop), buttonSize, "Sell oversupply",
            "The default: the bank buys a slice of this item wherever a station holds far more than "
            + "it needs. Double-clicking a row switches it between this and Don't sell.",
            b => SendSelected(WireSellOversupply));
        _dontSell = AddActionButton(new Vector2(left + buttonGap, buttonTop), buttonSize, "Don't sell",
            "The bank never buys this item from you, however much you hold.",
            b => SendSelected(WireDontSell));
        Controls.Add(new MyGuiControlLabel(
            new Vector2(left + buttonGap * 2f, buttonTop + 0.008f), null, "Target", null, 0.75f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
        _target = new MyGuiControlTextbox(defaultText: "", maxLength: 12)
        {
            Position = new Vector2(left + buttonGap * 2f + 0.07f, buttonTop),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = new Vector2(0.13f, 0.04f),
            Name = "BankSalesTarget",
        };
        _target.SetToolTip("How many to keep across all your stations. The bank still buys only "
                           + "oversupply, and never takes you below this.");
        Controls.Add(_target);
        _aboveTarget = AddActionButton(new Vector2(left + buttonGap * 3f, buttonTop), buttonSize,
            "Sell above target",
            "Sell oversupply, but never below the target across all your stations.",
            b => SendSelected(WireAboveTarget));

        float shownTop = buttonTop + 0.05f;
        _sellShown = AddActionButton(new Vector2(left, shownTop), buttonSize, "All shown: sell",
            "Sell oversupply of every item the filter shows.",
            b => SendShown(WireSellOversupply));
        _dontSellShown = AddActionButton(new Vector2(left + buttonGap, shownTop), buttonSize,
            "All shown: don't sell",
            "Don't sell any item the filter shows.",
            b => SendShown(WireDontSell));
        _aboveTargetShown = AddActionButton(new Vector2(left + buttonGap * 3f, shownTop), buttonSize,
            "All shown: above target",
            "Sell oversupply above the target box's amount, for every item the filter shows.",
            b => SendShown(WireAboveTarget));

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawn = null;
        _drawnRevision = -1;
    }

    /// <summary>Core's <c>BankSaleMode</c> ordinals, as the wire carries them.</summary>
    private const byte WireSellOversupply = 0;
    private const byte WireAboveTarget = 1;
    private const byte WireDontSell = 2;

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
        BankSalesComponent component = BankSalesComponent.Instance;
        if (component != null)
        {
            // Retried every frame: a window opened before the uplink was granted starts streaming
            // as soon as it is.
            component.SetStreaming(true);
            if (_drawnRevision != component.Revision) Rebuild(component);
            else RefreshStatus(component);
        }
        return base.Draw();
    }

    private void Rebuild(BankSalesComponent component)
    {
        if (component == null) return;
        _drawnRevision = component.Revision;

        if (!BankSalesComponent.Available)
        {
            WriteUnavailable("This server does not offer bank sales to the client.");
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

        int ruled = 0;
        foreach (WireBankSalesRow r in component.Rows) if (r.Mode != WireSellOversupply) ruled++;
        _heading.Text = "[" + component.FactionTag + "]  " + component.Rows.Count + " items, "
                        + ruled + " with a rule of their own";

        string filter = (_filter.Text ?? "").Trim();
        if (TableRows.Changed(ref _drawn, Signature(component, filter)))
        {
            TableRows.PreservingScroll(_table, () =>
            {
                _table.Clear();
                foreach (WireBankSalesRow r in component.Rows)
                {
                    string name = string.IsNullOrEmpty(r.Name) ? r.Key : r.Name;
                    string type = CategoryName(r.Category);
                    string bank = RuleText(r);
                    if (!Matches(filter, name, r.Key, type, bank)) continue;

                    var row = new MyGuiControlTable.Row(r);
                    row.AddCell(new MyGuiControlTable.Cell(name, name, name + "  (" + r.Key + ")"));
                    row.AddCell(new MyGuiControlTable.Cell(type, type, null));
                    row.AddCell(CountCell(r.Held, " held across your stations"));
                    row.AddCell(CountCell(r.UnitValue, " credits per unit, before the bank's scarcity adjustment"));
                    row.AddCell(new MyGuiControlTable.Cell(
                        bank, (double)RuleOrder(r), RuleTooltip(r),
                        r.Mode == WireDontSell ? DontSellColor
                        : r.Mode == WireAboveTarget ? TargetColor : (Color?)null));
                    _table.Add(row);
                }
                // The order the player last clicked survives a rebuild; until a header is clicked
                // the server's order (by item id) stands.
                _table.Sort(switchSort: false);
            });
            Reselect();
        }
        RefreshButtons();
        RefreshStatus(component);
    }

    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = "Bank sales";
        if (_table != null) _table.Clear();
        _drawn = null;
        _selectedKey = null;
        SetEnabled(false, false);
        if (_status != null) _status.Text = message;
    }

    private void RefreshButtons()
    {
        WireBankSalesRow? selected = Selected();
        bool row = selected.HasValue;
        bool any = _table != null && _table.RowsCount > 0;
        if (_sell != null) _sell.Enabled = row && selected.Value.Mode != WireSellOversupply;
        if (_dontSell != null) _dontSell.Enabled = row && selected.Value.Mode != WireDontSell;
        if (_aboveTarget != null) _aboveTarget.Enabled = row;
        if (_sellShown != null) _sellShown.Enabled = any;
        if (_dontSellShown != null) _dontSellShown.Enabled = any;
        if (_aboveTargetShown != null) _aboveTargetShown.Enabled = any;
    }

    private void SetEnabled(bool row, bool shown)
    {
        if (_sell != null) _sell.Enabled = row;
        if (_dontSell != null) _dontSell.Enabled = row;
        if (_aboveTarget != null) _aboveTarget.Enabled = row;
        if (_sellShown != null) _sellShown.Enabled = shown;
        if (_dontSellShown != null) _dontSellShown.Enabled = shown;
        if (_aboveTargetShown != null) _aboveTargetShown.Enabled = shown;
    }

    private void RefreshStatus(BankSalesComponent component)
    {
        if (_status == null || component == null || !component.HasSnapshot) return;
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds < BankSalesComponent.CommandMessageSeconds)
        {
            _status.Text = component.LastCommandMessage.Replace("\r", " ").Replace("\n", " ").Trim();
            return;
        }
        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        _status.Text = "The bank buys only what a station holds far past its needs; a rule only narrows that"
                       + "   -   updated " + age + "s ago";
    }

    private static string Signature(BankSalesComponent component, string filter)
    {
        var sb = new StringBuilder(filter);
        foreach (WireBankSalesRow r in component.Rows)
            sb.Append('|').Append(r.Key).Append(';').Append(r.Held).Append(';').Append(r.UnitValue)
              .Append(';').Append(r.Mode).Append(';').Append(r.Target);
        return sb.ToString();
    }

    // ---- Selection and actions ----------------------------------------------------------------

    private WireBankSalesRow? Selected()
    {
        MyGuiControlTable.Row row = _table != null ? _table.SelectedRow : null;
        if (row != null && row.UserData is WireBankSalesRow) return (WireBankSalesRow)row.UserData;
        return null;
    }

    /// <summary>A newly selected row puts its own target in the box, so Sell above target edits
    /// the number the row shows rather than one left over from another item.</summary>
    private void OnRowSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        WireBankSalesRow? selected = Selected();
        string key = selected.HasValue ? selected.Value.Key : null;
        if (selected.HasValue && !string.Equals(key, _selectedKey, StringComparison.Ordinal)
            && selected.Value.Mode == WireAboveTarget && _target != null)
            _target.Text = selected.Value.Target.ToString();
        _selectedKey = key;
        RefreshButtons();
    }

    private void OnRowDoubleClicked(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        WireBankSalesRow? selected = Selected();
        if (selected.HasValue)
            Send(selected.Value.Mode == WireDontSell ? WireSellOversupply : WireDontSell, 0,
                 new List<string> { selected.Value.Key });
    }

    /// <summary>Puts the selection back on <see cref="_selectedKey"/>. A row the filter now hides is
    /// not selected -- the buttons act only on what the player can see.</summary>
    private void Reselect()
    {
        MyGuiControlTable.Row match = null;
        for (int i = 0; i < _table.RowsCount; i++)
        {
            MyGuiControlTable.Row row = _table.GetRow(i);
            if (row.UserData is WireBankSalesRow
                && string.Equals(((WireBankSalesRow)row.UserData).Key, _selectedKey, StringComparison.Ordinal))
            {
                match = row;
                break;
            }
        }
        if (match != null) _table.SelectedRow = match;
        else _table.SelectedRowIndex = null;
        RefreshButtons();
    }

    private void SendSelected(byte mode)
    {
        WireBankSalesRow? selected = Selected();
        long target;
        if (!selected.HasValue || !TargetFor(mode, out target)) return;
        Send(mode, target, new List<string> { selected.Value.Key });
    }

    /// <summary>Every row the filter shows whose rule would change -- one request for all of them.</summary>
    private void SendShown(byte mode)
    {
        long target;
        if (!TargetFor(mode, out target)) return;
        var keys = new List<string>();
        for (int i = 0; i < _table.RowsCount; i++)
        {
            MyGuiControlTable.Row row = _table.GetRow(i);
            if (!(row.UserData is WireBankSalesRow)) continue;
            var r = (WireBankSalesRow)row.UserData;
            if (r.Mode != mode || (mode == WireAboveTarget && r.Target != target)) keys.Add(r.Key);
        }
        Send(mode, target, keys);
    }

    /// <summary>The target box's amount for <see cref="WireAboveTarget"/>, zero for the other rules.
    /// A box that does not hold a whole number of at least 1 sends nothing and says so.</summary>
    private bool TargetFor(byte mode, out long target)
    {
        target = 0;
        if (mode != WireAboveTarget) return true;
        string text = (_target != null ? _target.Text ?? "" : "").Replace(",", "").Replace(" ", "").Trim();
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out target) && target > 0)
            return true;
        if (_status != null) _status.Text = "Type a target of at least 1 in the Target box first.";
        return false;
    }

    private static void Send(byte mode, long target, List<string> keys)
    {
        BankSalesComponent component = BankSalesComponent.Instance;
        if (component == null || keys.Count == 0) return;
        component.SendCommand(mode, target, keys);
    }

    private static string RuleText(WireBankSalesRow r)
    {
        if (r.Mode == WireDontSell) return "don't sell";
        if (r.Mode == WireAboveTarget) return "above " + Quantities.Count(r.Target);
        return "sell oversupply";
    }

    private static string RuleTooltip(WireBankSalesRow r)
    {
        if (r.Mode == WireDontSell) return "The bank never buys this item from you.";
        if (r.Mode == WireAboveTarget)
            return "The bank buys oversupply, but never takes you below " + Quantities.Exact(r.Target)
                   + " across all your stations.";
        return "The bank buys a slice wherever a station holds far more than it needs.";
    }

    /// <summary>What the Bank column sorts on: most permissive first, targets by size.</summary>
    private static double RuleOrder(WireBankSalesRow r)
    {
        if (r.Mode == WireDontSell) return double.MaxValue;
        if (r.Mode == WireAboveTarget) return r.Target;
        return 0;
    }

    // ---- Cells --------------------------------------------------------------------------------

    /// <summary>k/M/G past four digits, as the hull table does; the exact figure is the tooltip and
    /// the raw value is what the column sorts on.</summary>
    private static MyGuiControlTable.Cell CountCell(long value, string unit)
    {
        return new MyGuiControlTable.Cell(Quantities.Count(value), (double)value, Quantities.Exact(value) + unit);
    }

    private static int CompareCells(MyGuiControlTable.Cell a, MyGuiControlTable.Cell b)
    {
        object x = a != null ? a.UserData : null;
        object y = b != null ? b.UserData : null;
        if (x is double && y is double) return ((double)x).CompareTo((double)y);
        return string.Compare(x as string ?? "", y as string ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(string filter, params string[] fields)
    {
        if (filter.Length == 0) return true;
        foreach (string f in fields)
            if (!string.IsNullOrEmpty(f) && f.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>Core's <c>ProcessedGoodCategory</c> ordinals.</summary>
    private static string CategoryName(byte category)
    {
        switch (category)
        {
            case 1: return "Weapon";
            case 2: return "Ammo";
            case 3: return "Consumable";
            default: return "Component";
        }
    }
}
