using System;
using System.Collections.Generic;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Market;

/// <summary>
/// Prices across every station the viewer's faction knows, and the trades they imply.
///
/// Two views behind one table rather than a tab control: Prices is every row, Opportunities is the
/// cheapest offer paired against the dearest order per item. The pairing is done here because every
/// row it needs is already on the client — see <see cref="MarketComponent.BuildOpportunities"/> for
/// why that is presentation and not a rule.
///
/// Draws over its opener (<see cref="MyGuiScreenBase.CanHideOthers"/> stays false), and takes its
/// own subscription rather than reading the map's cache, which stops refreshing when the map closes.
/// </summary>
internal sealed class MarketScreen : MyGuiScreenBase
{
    private const int Columns = 6;
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);

    internal static MarketScreen Instance { get; private set; }

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _status;
    private MyGuiControlTable _table;
    private MyGuiControlTextbox _filter;
    private MyGuiControlButton _modeButton;

    private int _drawnRevision = -1;
    private bool _opportunities;
    private string _lastFilter = "";

    private MarketScreen()
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
            MyGuiSandbox.AddScreen(new MarketScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("market window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.MarketScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        MarketComponent component = MarketComponent.Instance;
        if (component != null) component.SetStreaming(true);
    }

    public override void OnRemoved()
    {
        // Guarded on still being the current window: SE defers both the removal and the add, so a
        // replaced window can be removed after its successor has already subscribed.
        if (Instance != this) { base.OnRemoved(); return; }

        MarketComponent component = MarketComponent.Instance;
        if (component != null) component.SetStreaming(false);
        Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Market");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        // VisibleRowsCount decides the real height: MyGuiControlTable.RefreshInternals overwrites
        // Size.Y, so everything below is placed from the table's size AFTER construction, never from
        // the value passed in.
        _table = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, top + 0.04f),
            Size = new Vector2(0.85f, 0f),
            Name = "Market",
            ColumnsCount = Columns,
            VisibleRowsCount = 13,
        };
        _table.SetCustomColumnWidths(new float[Columns] { 0.24f, 0.24f, 0.10f, 0.14f, 0.14f, 0.14f });
        Controls.Add(_table);
        ApplyColumnNames();

        float tableHeight = _table.Size.Y;
        float rowTop = top + 0.04f + tableHeight + 0.015f;

        _filter = new MyGuiControlTextbox(
            position: new Vector2(left, rowTop),
            defaultText: "",
            maxLength: 40)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = new Vector2(0.3f, 0.04f),
            Name = "MarketFilter",
        };
        _filter.SetToolTip("Show only rows whose item or station name contains this text.");
        Controls.Add(_filter);

        _modeButton = new MyGuiControlButton(
            position: new Vector2(left + 0.33f, rowTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.2f, 0.04f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Switch between every price and the trades they imply.",
            text: new StringBuilder("Show opportunities"),
            textScale: 0.7f,
            onButtonClick: OnModeClicked);
        Controls.Add(_modeButton);

        _status = new MyGuiControlLabel(
            new Vector2(left, -topLeft.Y - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        _drawnRevision = -1;
    }

    private void ApplyColumnNames()
    {
        if (_opportunities)
        {
            _table.SetColumnName(0, new StringBuilder("Item"));
            _table.SetColumnName(1, new StringBuilder("Buy at"));
            _table.SetColumnName(2, new StringBuilder("Buy"));
            _table.SetColumnName(3, new StringBuilder("Sell at"));
            _table.SetColumnName(4, new StringBuilder("Margin/unit"));
            _table.SetColumnName(5, new StringBuilder("Distance"));
        }
        else
        {
            _table.SetColumnName(0, new StringBuilder("Item"));
            _table.SetColumnName(1, new StringBuilder("Station"));
            _table.SetColumnName(2, new StringBuilder("Side"));
            _table.SetColumnName(3, new StringBuilder("Price"));
            _table.SetColumnName(4, new StringBuilder("Quantity"));
            _table.SetColumnName(5, new StringBuilder("As of"));
        }
    }

    private void OnModeClicked(MyGuiControlButton _)
    {
        _opportunities = !_opportunities;
        _modeButton.Text = _opportunities ? "Show all prices" : "Show opportunities";
        ApplyColumnNames();
        _drawnRevision = -1;
    }

    // ---- Rendering ----------------------------------------------------------------------------

    public override bool Update(bool hasFocus)
    {
        bool result = base.Update(hasFocus);

        MarketComponent component = MarketComponent.Instance;
        if (component == null) return result;

        // Keep asking while the window is up: a window opened before the uplink was granted starts
        // streaming as soon as it is.
        component.SetStreaming(true);

        string filter = _filter != null ? _filter.Text ?? "" : "";
        if (component.Revision == _drawnRevision && filter == _lastFilter) return result;
        _drawnRevision = component.Revision;
        _lastFilter = filter;
        Rebuild(component, filter);
        return result;
    }

    private void Rebuild(MarketComponent component, string filter)
    {
        _table.Clear();

        if (!MarketComponent.Available)
        {
            _heading.Text = "This server does not offer market data.";
            _status.Text = "";
            return;
        }
        if (!component.HasSnapshot)
        {
            _heading.Text = "Waiting for the server...";
            _status.Text = "";
            return;
        }

        int shown = _opportunities
            ? FillOpportunities(component, filter)
            : FillPrices(component, filter);

        int frozen = 0;
        for (int i = 0; i < component.Stations.Count; i++) if (!component.Stations[i].Live) frozen++;

        _heading.Text = component.Stations.Count == 0
            ? "No station you know is trading."
            : component.Stations.Count + " station(s), " + component.Rows.Count + " price(s)";

        _status.Text = shown + " row(s) shown"
            + (frozen > 0 ? "  —  " + frozen + " station(s) remembered, not in contact"
                            + (_opportunities ? "; * marks a trade with a remembered leg" : "") : "")
            + "  —  updated " + Ago(DateTime.UtcNow - component.LastSnapshotUtc) + " ago";
    }

    private int FillPrices(MarketComponent component, string filter)
    {
        int shown = 0;
        for (int i = 0; i < component.Rows.Count; i++)
        {
            WireMarketRow r = component.Rows[i];
            string item = component.Items[r.ItemIndex];
            string label = ItemNames.Describe(item);
            WireMarketStation st = component.Stations[r.StationIndex];
            if (!Matches(filter, label, item, st.DisplayName, st.OwnerTag)) continue;

            var row = new MyGuiControlTable.Row();
            row.AddCell(ItemCell(label, item, stale: false));
            row.AddCell(Cell(StationLabel(st)));
            // The station's side, named from the player's: an Offer is something they can buy.
            row.AddCell(Cell(r.Side == 0 ? "Buy" : "Sell"));
            row.AddCell(Cell(r.UnitPrice.ToString("N0")));
            row.AddCell(Cell(r.Quantity < 0 ? "unknown" : r.Quantity.ToString("N0")));
            row.AddCell(Cell(st.Live ? "live" : Ago(DateTime.UtcNow - new DateTime(st.AsOfUtcTicks, DateTimeKind.Utc))));
            _table.Add(row);
            shown++;
        }
        return shown;
    }

    private int FillOpportunities(MarketComponent component, string filter)
    {
        List<MarketComponent.Opportunity> ops = component.BuildOpportunities();
        int shown = 0;
        for (int i = 0; i < ops.Count; i++)
        {
            MarketComponent.Opportunity o = ops[i];
            string label = ItemNames.Describe(o.Item);
            WireMarketStation a = component.Stations[o.BuyStation];
            WireMarketStation b = component.Stations[o.SellStation];
            if (!Matches(filter, label, o.Item, a.DisplayName, b.DisplayName)) continue;

            var row = new MyGuiControlTable.Row();
            row.AddCell(ItemCell(label, o.Item, o.Stale));
            row.AddCell(Cell(StationLabel(a)));
            row.AddCell(Cell(o.BuyPrice.ToString("N0")));
            row.AddCell(Cell(StationLabel(b) + "  @" + o.SellPrice.ToString("N0")));
            row.AddCell(Cell(o.MarginPerUnit.ToString("N0")
                             + (o.Units >= 0 ? "  x" + o.Units.ToString("N0") : "")));
            row.AddCell(Cell(o.DistanceKm.ToString("N0") + " km"));
            _table.Add(row);
            shown++;
        }
        return shown;
    }

    /// <summary>Text plus the same text as its tooltip: a station name or an item can be wider than
    /// its column, and the table clips rather than wraps.</summary>
    private static MyGuiControlTable.Cell Cell(string text)
    {
        return new MyGuiControlTable.Cell(text, null, text);
    }

    private static bool Matches(string filter, params string[] fields)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        for (int i = 0; i < fields.Length; i++)
            if (!string.IsNullOrEmpty(fields[i])
                && fields[i].IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>The item column: its name on screen, its inventory key in the tooltip. The key is
    /// what the server and every other window call it, and the column fits one of the two.</summary>
    private static MyGuiControlTable.Cell ItemCell(string label, string invKey, bool stale)
    {
        string tip = label + "  (" + invKey + ")";
        if (stale) tip += "  -  one side of this trade is a remembered price, not a live one";
        return new MyGuiControlTable.Cell(stale ? label + " *" : label, null, tip);
    }

    private static string StationLabel(WireMarketStation st)
    {
        string name = string.IsNullOrEmpty(st.DisplayName) ? st.NodeId : st.DisplayName;
        return string.IsNullOrEmpty(st.OwnerTag) ? name : name + " (" + st.OwnerTag + ")";
    }

    private static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return (int)span.TotalSeconds + "s";
        if (span.TotalHours < 1) return (int)span.TotalMinutes + "m";
        if (span.TotalDays < 1) return (int)span.TotalHours + "h";
        return (int)span.TotalDays + "d";
    }
}
