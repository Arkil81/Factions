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
/// The create dialog behind the Orders window's "New order..." in Stock mode: pick an item, say how
/// many to keep at this station.
///
/// <para>Every item is listed, whether or not anything supplies it. The Source column says where it
/// could come from today (your own production, your own stock, another faction's stock) and
/// "no known source" otherwise. Such an item stays orderable, because an order is what makes the
/// want visible to whoever could fill it.</para>
///
/// <para>The station is the one the window was opened on, and the server re-validates the node, the
/// item and the list cap through the same entry point as the chat verb.</para>
/// </summary>
internal sealed class NewStockOrderScreen : MyGuiScreenBase
{
    private const int ItemColumns = 3;

    private static readonly Vector2 WindowSize = new Vector2(0.8f, 0.8f);

    internal static NewStockOrderScreen Instance { get; private set; }

    private readonly string _nodeId;
    private readonly string _nodeLabel;

    private MyGuiControlTextbox _filter;
    private MyGuiControlTable _items;
    private MyGuiControlTextbox _quantity;
    private MyGuiControlLabel _selected;
    private MyGuiControlLabel _status;

    private string _selectedKey;
    /// <summary>The order covers every founded station rather than this one. A field, not read off
    /// the checkbox, so a RecreateControls keeps the choice.</summary>
    private bool _allStations;
    private string _drawnFilter;
    private List<WireStockItem> _drawnItems;

    private NewStockOrderScreen(string nodeId, string nodeLabel)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _nodeId = nodeId;
        _nodeLabel = nodeLabel;
        m_closeOnEsc = true;
        CanBeHidden = true;
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    internal static void Open(string nodeId, string nodeLabel)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        try
        {
            NewStockOrderScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new NewStockOrderScreen(nodeId, nodeLabel));
        }
        catch (Exception ex)
        {
            Plugin.Log("new stock order dialog open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.NewStockOrderScreen";
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
        AddCaption("New stock order");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.03f;
        float fieldLeft = left + 0.13f;
        float row = topLeft.Y + 0.09f;

        AddLabel(new Vector2(left, row + 0.008f), "Station", 0.75f);
        AddLabel(new Vector2(fieldLeft, row + 0.008f), _nodeLabel ?? _nodeId, 0.75f);

        row += 0.05f;
        AddLabel(new Vector2(left, row + 0.008f), "Filter", 0.75f);
        _filter = AddTextbox(new Vector2(fieldLeft, row), new Vector2(0.34f, 0.04f), "NewStockFilter", "");

        row += 0.055f;
        _items = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, row),
            Size = new Vector2(0.74f, 0f),
            Name = "NewStockItems",
            ColumnsCount = ItemColumns,
            VisibleRowsCount = 9,
        };
        _items.SetCustomColumnWidths(new float[ItemColumns] { 0.36f, 0.14f, 0.50f });
        _items.SetColumnName(0, new StringBuilder("Item"));
        _items.SetColumnName(1, new StringBuilder("Type"));
        _items.SetColumnName(2, new StringBuilder("Source"));
        _items.ItemSelected += OnItemSelected;
        Controls.Add(_items);

        // Placed from the table's real height: RefreshInternals overwrites Size.Y.
        row += _items.Size.Y + 0.015f;
        _selected = AddLabel(new Vector2(left, row + 0.008f), "Pick an item above.", 0.75f);

        row += 0.05f;
        AddLabel(new Vector2(left, row + 0.008f), "Keep", 0.75f);
        _quantity = AddTextbox(new Vector2(fieldLeft, row), new Vector2(0.2f, 0.04f), "NewStockQuantity", "100");

        var allStations = new MyGuiControlCheckbox(
            position: new Vector2(fieldLeft + 0.23f, row),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Keep this many at every station you own, including ones you found later. "
                     + "A station order for the same item replaces it at that station.",
            isChecked: _allStations);
        allStations.IsCheckedChanged = b => { if (b != null) _allStations = b.IsChecked; };
        Controls.Add(allStations);
        AddLabel(new Vector2(fieldLeft + 0.27f, row + 0.008f), "at every station", 0.75f);

        var buttonSize = new Vector2(0.16f, 0.04f);
        float buttonTop = -topLeft.Y - 0.10f;
        var create = new MyGuiControlButton(
            position: new Vector2(left, buttonTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: buttonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Create the order. The server validates every field.",
            text: new StringBuilder("Create"),
            textScale: 0.7f,
            onButtonClick: OnCreateClicked);
        Controls.Add(create);

        var cancel = new MyGuiControlButton(
            position: new Vector2(left + 0.18f, buttonTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: buttonSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: null,
            text: new StringBuilder("Cancel"),
            textScale: 0.7f,
            onButtonClick: _ => CloseScreen());
        Controls.Add(cancel);

        _status = AddLabel(new Vector2(left, -topLeft.Y - 0.05f), "", 0.7f);
        _drawnItems = null;
        _drawnFilter = null;
    }

    private MyGuiControlLabel AddLabel(Vector2 position, string text, float scale)
    {
        var label = new MyGuiControlLabel(
            position, null, text, null, scale, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(label);
        return label;
    }

    private MyGuiControlTextbox AddTextbox(Vector2 position, Vector2 size, string name, string defaultText)
    {
        var box = new MyGuiControlTextbox(defaultText: defaultText)
        {
            Position = position,
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = size,
            Name = name,
        };
        Controls.Add(box);
        return box;
    }

    // ---- Frame --------------------------------------------------------------------------------

    /// <summary>Rebuilds the list when a new item list arrives or the filter text changes, and at no
    /// other time: the component's revision also moves on every order snapshot, which says nothing
    /// about the items. Polled rather than hooked to the textbox so an item list arriving while the
    /// dialog is open is picked up the same way. A new item list keeps the scroll position; a new
    /// filter starts from the top of what it matches.</summary>
    public override bool Draw()
    {
        StationMineComponent component = StationMineComponent.Instance;
        string filter = _filter != null ? (_filter.Text ?? "").Trim() : "";
        if (component != null)
        {
            bool newItems = !ReferenceEquals(_drawnItems, component.StockItems);
            bool newFilter = !string.Equals(filter, _drawnFilter, StringComparison.Ordinal);
            if (newItems || newFilter)
            {
                _drawnItems = component.StockItems;
                _drawnFilter = filter;
                if (newFilter) RebuildItems(component.StockItems, filter);
                else TableRows.PreservingScroll(_items, () => RebuildItems(component.StockItems, filter));
            }
        }
        return base.Draw();
    }

    private void RebuildItems(List<WireStockItem> items, string filter)
    {
        _items.Clear();
        int restore = -1;
        for (int i = 0; i < items.Count; i++)
        {
            WireStockItem item = items[i];
            if (!Matches(item, filter)) continue;
            var row = new MyGuiControlTable.Row(item);
            row.AddCell(new MyGuiControlTable.Cell(item.Name, null, item.Key));
            row.AddCell(new MyGuiControlTable.Cell(item.Category));
            row.AddCell(new MyGuiControlTable.Cell(item.Source, null, item.Source));
            _items.Add(row);
            if (string.Equals(item.Key, _selectedKey, StringComparison.Ordinal)) restore = _items.RowsCount - 1;
        }
        if (restore >= 0) _items.SelectedRowIndex = restore;
        if (_status != null)
            _status.Text = items.Count == 0 ? "Waiting for the server's item list..." : _items.RowsCount + " of " + items.Count + " items";
    }

    /// <summary>Every whitespace-separated word must appear in the name, key or category.</summary>
    private static bool Matches(WireStockItem item, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        foreach (string word in filter.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Contains(item.Name, word) || Contains(item.Key, word) || Contains(item.Category, word)) continue;
            return false;
        }
        return true;
    }

    private static bool Contains(string haystack, string needle)
    {
        return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void OnItemSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        if (row == null || !(row.UserData is WireStockItem))
        {
            _selectedKey = null;
            _selected.Text = "Pick an item above.";
            return;
        }
        var item = (WireStockItem)row.UserData;
        _selectedKey = item.Key;
        _selected.Text = item.Name + "  (" + item.Key + ")  -  " + item.Source;
    }

    // ---- Create -------------------------------------------------------------------------------

    private void OnCreateClicked(MyGuiControlButton button)
    {
        StationMineComponent component = StationMineComponent.Instance;
        if (component == null) return;

        if (string.IsNullOrEmpty(_selectedKey))
        {
            _status.Text = "Pick an item first.";
            return;
        }
        long quantity;
        if (!long.TryParse((_quantity.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity)
            || quantity <= 0)
        {
            _status.Text = "Keep must be a positive whole number.";
            return;
        }

        // "all" is the node argument the server reads as every station, the same as the chat verb's.
        component.SendCreateStock(_allStations ? "all" : _nodeId, _selectedKey, quantity);

        // The Orders window behind this one shows the result, including a refusal, in its status line.
        CloseScreen();
    }
}
