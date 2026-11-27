using System;
using System.Collections.Generic;
using System.Text;
using Factions.Wire;
using FactionsUI.Demand;
using FactionsUI.Map;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Nodes;

/// <summary>
/// The per-node window. Opened by node id, from the Assets tab's table and from the map's selection
/// panel, and owned by neither of them.
///
/// It draws over whatever opened it rather than replacing it — <see cref="MyGuiScreenBase.CanHideOthers"/>
/// stays false, so the terminal or the map is still there behind it when it closes. Its data comes
/// from <see cref="NodeDetailComponent"/>, which has its own server subscription: both openers'
/// caches stop refreshing the moment their own screen is no longer in front, so reading either one
/// from here would show whatever happened to be true when the player last looked at it.
///
/// <para><b>Laid out as tables, in the Services terminal's style</b> (<c>Ships.RepairPanel</c>),
/// behind four tabs: Overview (key/value rows beside the installed capabilities and the ground under
/// the node), Stock, Ships and History, each table at the window's full width; every action in one bar along the bottom. Tabs because
/// the stock table, sharing the width with a left column, cut its item names and its numbers. The shipyard's queue is
/// not here — it is <see cref="StationBuildsScreen"/>'s, behind <c>Shipyard</c>, which already
/// carried every build, its components and every queue action, so a summary table here only
/// duplicated it at the cost of half the window.</para>
///
/// <para><b>Stock says what is for sale.</b> A station holding plenty of hydrogen that its refuel
/// then refuses read as a bug: the stock list said "12,000 kL" and nothing said the station meant to
/// keep all of it. Each row now carries the store's own offer — the one refuel and rearm draw on —
/// and the reason the rest is kept.</para>
///
/// <para><b>History is what the station actually refined and built</b>, the dashboard node panel's
/// production history for this one node: each recipe's consumption and output over the server's
/// retention window, as real coalesced spans rather than the theoretical rate a slot assignment
/// implies. It answers "is this refinery running, and on what" from inside the game.</para>
///
/// <para><b>Demand is what the station is asking for</b>: the owner's <c>Item:</c> and <c>Ship:</c>
/// demands pinned to this node, most urgent first, each beside what the station holds and how fast
/// it uses the item; and under them, the demands that ended here recently, and whether they were
/// met or simply lapsed. Faction-wide demands are not shown here.</para>
/// </summary>
internal sealed class NodeDetailScreen : MyGuiScreenBase
{
    // The last of each is an empty spacer (EdgeGap): the column before it is right-aligned with
    // no margin, and SE glyphs draw a few pixels past their advance, so without it the scrollbar,
    // drawn after the rows, covers the last letter of the header and the last digit of every amount.
    private const int StockColumns = 7;
    private const int ShipColumns = 4;
    private const int HistoryColumns = 6;
    private const int DemandColumns = 7;
    private const int EndedColumns = 6;
    // The demand tab stacks two tables in the height the others give one: open demand on top,
    // what ended under it, each with its own header row.
    private const int DemandRowsVisible = 5;
    private const int EndedRowsVisible = 4;
    private const float EdgeGap = 0.02f;
    private const int OverviewRows = 8;
    /// <summary>Lines the installed list is given above the Ground panel. It is one wrapped,
    /// comma-joined paragraph, so three lines hold every capability there is.</summary>
    private const int InstalledRows = 3;
    /// <summary>Ores the Ground panel lists; the note counts any past this.</summary>
    private const int OreRows = 8;

    private const float Left = -0.43f;
    private const float Right = 0.43f;
    private const float Top = -0.31f;
    private const float TabTop = -0.258f;
    private const float BodyTop = -0.212f;
    private const float OverviewWidth = 0.40f;
    private const float InstalledLeft = 0.03f;
    private const int TableRowsVisible = 11;
    private const float OverviewValueOffset = 0.105f;
    private const float OverviewRowStep = 0.027f;
    private const float SectionRuleOffset = 0.03f;
    private const float SectionBodyOffset = 0.035f;
    private const float ActionBarTop = 0.25f;
    /// <summary>Right edges of the Ground panel's three number columns.</summary>
    private const float OreShareRight = InstalledLeft + 0.22f;
    private const float OrePriceRight = InstalledLeft + 0.31f;
    private const float OreWorthRight = Right - 0.005f;

    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.8f);
    private static readonly Vector2 ButtonSize = new Vector2(0.2f, 0.038f);
    private static readonly Vector2 TabSize = new Vector2(0.15f, 0.034f);

    private enum Tab { Overview, Stock, Demand, Ships, History }

    /// <summary>The tab the player last picked, kept across windows for the session: a player who
    /// reads stock reads it at every station they open.</summary>
    private static Tab _lastTab = Tab.Stock;

    /// <summary>The Services terminal's palette: white labels, a dimmed white for keys, a slate rule
    /// under each section, and its warning red for a row with nothing on offer.</summary>
    private static readonly Vector4 KeyColor = new Vector4(1f, 1f, 1f, 0.6f);
    private static readonly Vector4 RuleColor = new Color(77, 99, 113).ToVector4();
    private static readonly Color Warn = new Color(255, 120, 100);
    private static readonly Color Muted = new Color(150, 160, 170);
    /// <summary>The dashboard's produced/consumed pair, so the two views of one history read alike.</summary>
    private static readonly Color Produced = new Color(46, 204, 113);
    private static readonly Color Consumed = new Color(231, 76, 60);

    internal static NodeDetailScreen Instance { get; private set; }

    private readonly string _nodeId;

    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _subtitle;
    private readonly MyGuiControlLabel[] _keys = new MyGuiControlLabel[OverviewRows];
    private readonly MyGuiControlLabel[] _values = new MyGuiControlLabel[OverviewRows];
    private MyGuiControlMultilineText _installed;
    private readonly MyGuiControlLabel[] _oreNames = new MyGuiControlLabel[OreRows];
    private readonly MyGuiControlLabel[] _oreShares = new MyGuiControlLabel[OreRows];
    private readonly MyGuiControlLabel[] _orePrices = new MyGuiControlLabel[OreRows];
    private readonly MyGuiControlLabel[] _oreWorths = new MyGuiControlLabel[OreRows];
    private MyGuiControlLabel _groundNote;
    private MyGuiControlTable _ships;
    private MyGuiControlTable _stock;
    private MyGuiControlLabel _stockNote;
    private MyGuiControlTable _history;
    private MyGuiControlLabel _historyNote;
    private MyGuiControlTable _demand;
    private MyGuiControlLabel _endedCaption;
    private MyGuiControlTable _ended;
    private MyGuiControlLabel _demandNote;
    private MyGuiControlLabel _status;

    private MyGuiControlButton _overviewTab;
    private MyGuiControlButton _stockTab;
    private MyGuiControlButton _shipsTab;
    private MyGuiControlButton _historyTab;
    private MyGuiControlButton _demandTab;
    private readonly List<MyGuiControlBase> _overviewControls = new List<MyGuiControlBase>();
    private readonly List<MyGuiControlBase> _stockControls = new List<MyGuiControlBase>();
    private readonly List<MyGuiControlBase> _shipsControls = new List<MyGuiControlBase>();
    private readonly List<MyGuiControlBase> _historyControls = new List<MyGuiControlBase>();
    private readonly List<MyGuiControlBase> _demandControls = new List<MyGuiControlBase>();

    private MyGuiControlButton _shipyard;
    private MyGuiControlButton _mine;
    private MyGuiControlButton _develop;
    private MyGuiControlButton _refit;
    private MyGuiControlButton _trade;
    private MyGuiControlButton _transfer;
    private MyGuiControlButton _hold;
    private MyGuiControlButton _recruit;

    private int _drawnRevision = -1;
    private string _drawnShips;
    private string _drawnStock;
    private string _drawnHistory;
    private string _drawnDemand;
    private string _drawnEnded;

    private NodeDetailScreen(string nodeId)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _nodeId = nodeId;
        m_closeOnEsc = true;
        CanBeHidden = true;
        // Left false on purpose: the map behind this window keeps drawing, and so does the terminal.
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    /// <summary>
    /// Opens the window on <paramref name="nodeId"/>, or retargets the one already open. Both
    /// openers call this and neither has to know whether the other left a window up.
    /// </summary>
    internal static void Open(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        try
        {
            NodeDetailScreen existing = Instance;
            if (existing != null)
            {
                if (string.Equals(existing._nodeId, nodeId, StringComparison.Ordinal)) return;
                // A window on a different node is closed rather than reused: the id it was built
                // with names it.
                existing.CloseScreen();
            }
            MyGuiSandbox.AddScreen(new NodeDetailScreen(nodeId));
        }
        catch (Exception ex)
        {
            Plugin.Log("node detail window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.NodeDetailScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component != null) component.Watch(_nodeId);
    }

    public override void OnRemoved()
    {
        // The one hook every close path goes through, so the server's watch is always released.
        //
        // Guarded on still being the current window: SE defers both the removal and the add, so a
        // window replaced by one on another node can be removed AFTER its successor has loaded and
        // taken the subscription. An unguarded unwatch here would cancel the new window's watch and
        // leave it waiting on a server that was told to stop.
        if (Instance != this) { base.OnRemoved(); return; }

        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component != null) component.Watch(null);
        Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Station");

        _heading = new MyGuiControlLabel(
            new Vector2(Left, Top), null, "", null, 0.9f, "White",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);
        _subtitle = new MyGuiControlLabel(
            new Vector2(Right, Top + 0.006f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP);
        Controls.Add(_subtitle);

        var rules = new MyGuiControlSeparatorList();
        rules.AddHorizontal(new Vector2(Left, Top + 0.04f), Right - Left, 0f, RuleColor);
        rules.AddHorizontal(new Vector2(Left, ActionBarTop - 0.012f), Right - Left, 0f, RuleColor);

        _overviewControls.Clear();
        _stockControls.Clear();
        _shipsControls.Clear();
        _historyControls.Clear();
        _demandControls.Clear();
        _overviewTab = AddTabButton(0, "Overview", "Owner, hold, safe zone, what is installed here and what the ground holds.", Tab.Overview);
        _stockTab = AddTabButton(1, "Stock", "What this station holds, what its store sells, and why the rest is kept.", Tab.Stock);
        _demandTab = AddTabButton(2, "Demand",
            "What this station is asking for now, and what it asked for recently.", Tab.Demand);
        _shipsTab = AddTabButton(3, "Ships", "Hulls standing at this station.", Tab.Ships);
        _historyTab = AddTabButton(4, "History",
            "What this station actually refined and built recently, and what it used up doing it.", Tab.History);

        // ---- Overview: key/value rows beside the installed capabilities ----
        var overviewRules = new MyGuiControlSeparatorList();
        float y = BodyTop;
        AddSection(overviewRules, _overviewControls, "OVERVIEW", Left, y, OverviewWidth);
        AddSection(overviewRules, _overviewControls, "INSTALLED", InstalledLeft, y, Right - InstalledLeft);
        y += SectionBodyOffset;
        for (int i = 0; i < OverviewRows; i++)
        {
            _keys[i] = Add(_overviewControls, new MyGuiControlLabel(
                new Vector2(Left, y), null, "", KeyColor, 0.7f, "White",
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
            _values[i] = Add(_overviewControls, new MyGuiControlLabel(
                new Vector2(Left + OverviewValueOffset, y), null, "", null, 0.7f, "Blue",
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                isAutoEllipsisEnabled: true, maxWidth: OverviewWidth - OverviewValueOffset));
            y += OverviewRowStep;
        }

        _installed = Add(_overviewControls, new MyGuiControlMultilineText(
            position: new Vector2(InstalledLeft - 0.004f, BodyTop + SectionBodyOffset - 0.004f),
            size: new Vector2(Right - InstalledLeft + 0.004f, InstalledRows * OverviewRowStep),
            backgroundColor: null,
            font: "Blue",
            textScale: 0.7f,
            textAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            contents: null,
            drawScrollbarV: false,
            drawScrollbarH: false,
            textBoxAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Name = "NodeDetailInstalled",
        });

        // ---- Ground: the deposit ore by ore, under the installed list ----
        float groundTop = BodyTop + SectionBodyOffset + InstalledRows * OverviewRowStep + 0.012f;
        AddSection(overviewRules, _overviewControls, "GROUND", InstalledLeft, groundTop, Right - InstalledLeft);
        float oreY = groundTop + SectionBodyOffset;
        AddOreHeader("Ore", InstalledLeft, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, oreY, null);
        AddOreHeader("Share", OreShareRight, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP, oreY,
            "This ore's part of the deposit. A thick seam of anything is what raises Ore density.");
        AddOreHeader("Price", OrePriceRight, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP, oreY,
            "What this ore sells for, in multiples of the world's ordinary ore.");
        AddOreHeader("Worth", OreWorthRight, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP, oreY,
            "How much of this ground's Worth comes from this ore: its share times its price. A thin seam "
            + "of a dear ore can carry most of it.");
        for (int i = 0; i < OreRows; i++)
        {
            oreY += OverviewRowStep;
            _oreNames[i] = AddOreCell(InstalledLeft, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, oreY);
            _oreShares[i] = AddOreCell(OreShareRight, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP, oreY);
            _orePrices[i] = AddOreCell(OrePriceRight, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP, oreY);
            _oreWorths[i] = AddOreCell(OreWorthRight, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP, oreY);
        }
        _groundNote = Add(_overviewControls, new MyGuiControlLabel(
            new Vector2(InstalledLeft, oreY + OverviewRowStep + 0.004f), null, "", Muted.ToVector4(), 0.62f, "White",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            isAutoEllipsisEnabled: true, maxWidth: Right - InstalledLeft));
        Add(_overviewControls, overviewRules);

        // ---- Ships: full width ----
        // VisibleRowsCount, not Size.Y, decides how tall a table ends up: RefreshInternals
        // overwrites Size.Y with RowHeight * (VisibleRowsCount + 1) plus its texture minimum.
        _ships = Add(_shipsControls, new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(Left, BodyTop),
            Size = new Vector2(Right - Left, 0f),
            Name = "NodeDetailShips",
            ColumnsCount = ShipColumns,
            VisibleRowsCount = TableRowsVisible,
        });
        _ships.SetCustomColumnWidths(new float[ShipColumns] { 0.6f, 0.18f, 0.2f, EdgeGap });
        _ships.SetColumnName(0, new StringBuilder("Hull"));
        _ships.SetColumnName(1, new StringBuilder("Ready"));
        _ships.SetColumnName(2, new StringBuilder("For sale"));
        RightAlign(_ships, 1);
        RightAlign(_ships, 2);

        // ---- Stock: full width ----
        _stock = Add(_stockControls, new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(Left, BodyTop),
            Size = new Vector2(Right - Left, 0f),
            Name = "NodeDetailStock",
            ColumnsCount = StockColumns,
            VisibleRowsCount = TableRowsVisible,
        });
        _stock.SetCustomColumnWidths(new float[StockColumns] { 0.34f, 0.11f, 0.11f, 0.11f, 0.18f, 0.13f, EdgeGap });
        _stock.SetColumnName(0, new StringBuilder("Item"));
        _stock.SetColumnName(1, new StringBuilder("Held"));
        _stock.SetColumnName(2, new StringBuilder("For sale"));
        _stock.SetColumnName(3, new StringBuilder("Kept"));
        _stock.SetColumnName(4, new StringBuilder("Kept for"));
        _stock.SetColumnName(5, new StringBuilder("Install"));
        RightAlign(_stock, 1);
        RightAlign(_stock, 2);
        RightAlign(_stock, 3);
        RightAlign(_stock, 5);

        _stockNote = Add(_stockControls, new MyGuiControlLabel(
            new Vector2(Left, _stock.Position.Y + _stock.Size.Y + 0.006f), null,
            "", Muted.ToVector4(), 0.62f, "White", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));

        // ---- History: full width, the dashboard's production history columns ----
        _history = Add(_historyControls, new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(Left, BodyTop),
            Size = new Vector2(Right - Left, 0f),
            Name = "NodeDetailHistory",
            ColumnsCount = HistoryColumns,
            VisibleRowsCount = TableRowsVisible,
        });
        _history.SetCustomColumnWidths(new float[HistoryColumns] { 0.04f, 0.36f, 0.28f, 0.14f, 0.16f, EdgeGap });
        _history.SetColumnName(1, new StringBuilder("Item"));
        _history.SetColumnName(2, new StringBuilder("Recipe"));
        _history.SetColumnName(3, new StringBuilder("Qty"));
        _history.SetColumnName(4, new StringBuilder("Last seen"));
        RightAlign(_history, 3);
        RightAlign(_history, 4);

        _historyNote = Add(_historyControls, new MyGuiControlLabel(
            new Vector2(Left, _history.Position.Y + _history.Size.Y + 0.006f), null,
            "", Muted.ToVector4(), 0.62f, "White", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));

        // ---- Demand: open demand, then what ended, both full width ----
        _demand = Add(_demandControls, new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(Left, BodyTop),
            Size = new Vector2(Right - Left, 0f),
            Name = "NodeDetailDemand",
            ColumnsCount = DemandColumns,
            VisibleRowsCount = DemandRowsVisible,
        });
        _demand.SetCustomColumnWidths(new float[DemandColumns] { 0.30f, 0.11f, 0.11f, 0.11f, 0.18f, 0.17f, EdgeGap });
        _demand.SetColumnName(0, new StringBuilder("Wanted here"));
        _demand.SetColumnName(1, new StringBuilder("Qty"));
        _demand.SetColumnName(2, new StringBuilder("Have"));
        _demand.SetColumnName(3, new StringBuilder("Used /h"));
        _demand.SetColumnName(4, new StringBuilder("For"));
        _demand.SetColumnName(5, new StringBuilder("Open for"));
        RightAlign(_demand, 1);
        RightAlign(_demand, 2);
        RightAlign(_demand, 3);
        RightAlign(_demand, 5);

        // Placed from the first table's real size: RefreshInternals has already set it.
        _endedCaption = Add(_demandControls, new MyGuiControlLabel(
            new Vector2(Left, _demand.Position.Y + _demand.Size.Y + 0.006f), null,
            "", Muted.ToVector4(), 0.62f, "White", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));

        _ended = Add(_demandControls, new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(Left, _endedCaption.Position.Y + 0.028f),
            Size = new Vector2(Right - Left, 0f),
            Name = "NodeDetailEndedDemand",
            ColumnsCount = EndedColumns,
            VisibleRowsCount = EndedRowsVisible,
        });
        _ended.SetCustomColumnWidths(new float[EndedColumns] { 0.30f, 0.11f, 0.18f, 0.15f, 0.24f, EdgeGap });
        _ended.SetColumnName(0, new StringBuilder("Wanted then"));
        _ended.SetColumnName(1, new StringBuilder("Peak"));
        _ended.SetColumnName(2, new StringBuilder("For"));
        _ended.SetColumnName(3, new StringBuilder("Was open"));
        _ended.SetColumnName(4, new StringBuilder("Ended"));
        RightAlign(_ended, 1);
        RightAlign(_ended, 3);
        RightAlign(_ended, 4);

        _demandNote = Add(_demandControls, new MyGuiControlLabel(
            new Vector2(Left, _ended.Position.Y + _ended.Size.Y + 0.006f), null,
            "", Muted.ToVector4(), 0.62f, "White", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));

        // ---- bottom: every action, one bar ----
        float gap = (Right - Left - ButtonSize.X * 4f) / 3f;
        float row2 = ActionBarTop + ButtonSize.Y + 0.008f;
        // The shipyard window carries the whole queue: every build and repair, the component bill
        // behind each, and pause, cancel, priority and new build. Owned rather than "has a build
        // queued": the pause switch and the reason a yard is empty are both worth reaching when
        // nothing is queued.
        _shipyard = AddActionButton(Slot(0, gap), ActionBarTop, "Shipyard...",
            "This station's shipyard: every build and repair on its queue, what each is short of, and "
            + "the controls to pause it, queue a hull, cancel or reorder.",
            b => StationBuildsScreen.Open(_nodeId));
        // Mine orders are anchored at a hub and worked from it, so the station IS the subject of the
        // list (docs/fleet-doctrine-plan.md, decision 4).
        _mine = AddActionButton(Slot(1, gap), ActionBarTop, "Orders...",
            "Standing orders at this station: what to mine around here, and what to keep in stock.",
            OnMineClicked);
        // On a claim that has no station yet the same button offers the step before it -- founding
        // one -- rather than sitting greyed out beside the only thing the player can actually do.
        _develop = AddActionButton(Slot(2, gap), ActionBarTop, "Develop...",
            "Install a capability at this station, and decide whether Factions works toward it on "
            + "its own.",
            OnDevelopClicked);
        _refit = AddActionButton(Slot(3, gap), ActionBarTop, "Refit...",
            "Rebuild this station as another design — a vanilla one, or a station you scanned. The "
            + "components are a bill this station collects, like a ship build.",
            b => StationRefitScreen.Open(_nodeId));
        // Faction-wide despite sitting on a node: this is where the player is standing when they
        // notice a station is short of something.
        _trade = AddActionButton(Slot(0, gap), row2, "Trade...",
            "Let Factions buy what your stations are short of and sell what they have spare, and "
            + "set what that may spend.",
            b => StationTradeScreen.Open(_nodeId));
        // The only way in at a claim with no station: there is no store block to hang a tab off.
        _transfer = AddActionButton(Slot(1, gap), row2, "Stock...",
            "Take goods out of this node's own stock, or put goods into it. You have to be at the "
            + "node.",
            b => StationTransferScreen.OpenForNode(_nodeId));
        _hold = AddActionButton(Slot(2, gap), row2, "Hold...",
            "Keep this node's hold up while you are away, and set what upkeep may spend.",
            b => StationHoldScreen.Open(_nodeId));
        _recruit = AddActionButton(Slot(3, gap), row2, "Recruit...",
            "Recruit workers into this station's Habitat. Workers are recruited on Tellus.",
            b => StationRecruitScreen.Open(_nodeId));

        Controls.Add(rules);

        _status = new MyGuiControlLabel(
            new Vector2(Left, WindowSize.Y * 0.5f - 0.05f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_status);

        ShowTab(_lastTab, false);
        _drawnRevision = -1;
    }

    private static float Slot(int column, float gap)
    {
        return Left + column * (ButtonSize.X + gap);
    }

    /// <summary>Adds <paramref name="control"/> to the screen and to the tab it belongs to.</summary>
    private T Add<T>(List<MyGuiControlBase> group, T control) where T : MyGuiControlBase
    {
        Controls.Add(control);
        group.Add(control);
        return control;
    }

    /// <summary>A tab is a button, as the mine/stock pair in <see cref="StationMineScreen"/>: the
    /// shown one is disabled, which is also what marks it.</summary>
    private MyGuiControlButton AddTabButton(int slot, string text, string tooltip, Tab tab)
    {
        var button = new MyGuiControlButton(
            position: new Vector2(Left + slot * (TabSize.X + 0.008f), TabTop),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: TabSize,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: tooltip,
            text: new StringBuilder(text),
            textScale: 0.7f,
            onButtonClick: b => { _lastTab = tab; ShowTab(tab, true); });
        Controls.Add(button);
        return button;
    }

    /// <summary>Shows one tab's controls and hides the rest. Stock, Demand, Ships and History are only offered
    /// on the player's own stations — the server sends none of them for anyone else's — so a window with
    /// nothing to put in them stays on Overview without forgetting the player's choice.</summary>
    private void ShowTab(Tab tab, bool owned)
    {
        if (!owned) tab = Tab.Overview;
        SetVisible(_overviewControls, tab == Tab.Overview);
        SetVisible(_stockControls, tab == Tab.Stock);
        SetVisible(_shipsControls, tab == Tab.Ships);
        SetVisible(_historyControls, tab == Tab.History);
        SetVisible(_demandControls, tab == Tab.Demand);
        if (_overviewTab != null) _overviewTab.Enabled = tab != Tab.Overview;
        if (_stockTab != null) _stockTab.Enabled = owned && tab != Tab.Stock;
        if (_shipsTab != null) _shipsTab.Enabled = owned && tab != Tab.Ships;
        if (_historyTab != null) _historyTab.Enabled = owned && tab != Tab.History;
        if (_demandTab != null) _demandTab.Enabled = owned && tab != Tab.Demand;
    }

    private static void SetVisible(List<MyGuiControlBase> group, bool visible)
    {
        foreach (MyGuiControlBase control in group) control.Visible = visible;
    }

    /// <summary>A Ground column header: the overview's key colour, with what the column means as
    /// its tooltip.</summary>
    private void AddOreHeader(string text, float x, MyGuiDrawAlignEnum align, float y, string tooltip)
    {
        var label = Add(_overviewControls, new MyGuiControlLabel(
            new Vector2(x, y), null, text, KeyColor, 0.7f, "White", align));
        if (tooltip != null) label.SetToolTip(tooltip);
    }

    private MyGuiControlLabel AddOreCell(float x, MyGuiDrawAlignEnum align, float y)
    {
        return Add(_overviewControls, new MyGuiControlLabel(
            new Vector2(x, y), null, "", null, 0.7f, "Blue", align));
    }

    /// <summary>A section caption with the rule under it, the repair panel's table header.</summary>
    private MyGuiControlLabel AddSection(MyGuiControlSeparatorList rules, List<MyGuiControlBase> group,
                                         string caption, float x, float y, float width)
    {
        var label = Add(group, new MyGuiControlLabel(
            new Vector2(x, y), null, caption, null, 0.75f, "White",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
        rules.AddHorizontal(new Vector2(x, y + SectionRuleOffset), width, 0f, RuleColor);
        return label;
    }

    /// <summary>Right-aligns a column, header and rows, with no column margin. SE applies the margin
    /// with opposite signs to the two: a right-aligned row is inset from the cell's right edge by
    /// <c>Margin.Left</c>, the header is pushed past it by the same amount, so at the default 0.01
    /// every header sat 0.02 right of its numbers. At zero both end on the cell edge.</summary>
    private static void RightAlign(MyGuiControlTable table, int column)
    {
        table.SetColumnAlign(column, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
        table.SetHeaderColumnAlign(column, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
        table.SetHeaderColumnMargin(column, new Thickness(0f));
    }

    private MyGuiControlButton AddActionButton(
        float x, float y, string text, string tooltip, Action<MyGuiControlButton> onClick)
    {
        var button = new MyGuiControlButton(
            position: new Vector2(x, y),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: ButtonSize,
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
            // Retried every frame rather than only on open, matching the map: a window opened
            // before the uplink was granted would otherwise stay blank until it was reopened.
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
        if (!component.HasSnapshot)
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

        _heading.Text = HeadingFor(detail);
        _subtitle.Text = NodeLabels.Describe(detail.DominantOre, detail.Surveyed,
            CapabilityNames(detail), detail.HasStation) + " - " + ZoneLabel(detail.Zone);
        WriteOverview(detail);
        WriteInstalled(detail);
        WriteGround(detail);
        WriteShips(detail);
        WriteStock(detail);
        WriteHistory(detail);
        WriteDemand(detail);
        WriteTabLabels(detail);
        ShowTab(_lastTab, detail.Owned);
        RefreshButtons(detail);
        RefreshStatus(component, detail);
    }

    /// <summary>Every state where there is nothing to show: no link, no snapshot yet, or a node the
    /// server no longer has. One path, so no half-populated table can survive any of them.</summary>
    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = _nodeId;
        if (_subtitle != null) _subtitle.Text = "";
        for (int i = 0; i < OverviewRows; i++) SetRow(i, "", "", null);
        if (_installed != null) _installed.Clear();
        ClearOreRows(0);
        if (_groundNote != null) _groundNote.Text = "";
        if (_ships != null) _ships.Clear();
        if (_stock != null) _stock.Clear();
        if (_history != null) _history.Clear();
        if (_demand != null) _demand.Clear();
        if (_ended != null) _ended.Clear();
        _drawnShips = null;
        _drawnStock = null;
        _drawnHistory = null;
        _drawnDemand = null;
        _drawnEnded = null;
        if (_stockNote != null) _stockNote.Text = "";
        if (_endedCaption != null) _endedCaption.Text = "";
        if (_demandNote != null) _demandNote.Text = "";
        if (_historyNote != null) _historyNote.Text = "";
        if (_stockTab != null) _stockTab.Text = "Stock";
        if (_shipsTab != null) _shipsTab.Text = "Ships";
        if (_historyTab != null) _historyTab.Text = "History";
        if (_demandTab != null) _demandTab.Text = "Demand";
        ShowTab(_lastTab, false);
        foreach (MyGuiControlButton button in Buttons())
            if (button != null) button.Enabled = false;
        if (_status != null) _status.Text = message;
    }

    private MyGuiControlButton[] Buttons()
    {
        return new[] { _shipyard, _mine, _develop, _refit, _trade, _transfer, _hold, _recruit };
    }

    private static string HeadingFor(WireNodeDetail detail)
    {
        // The server sends no name below Identity and nothing about the site below Class, so the
        // heading says what is known rather than reading an absent station as a bare claim.
        if (detail.Tier < (byte)WireIntelTier.Class) return "Unidentified contact  " + detail.Id;
        if (detail.Tier < (byte)WireIntelTier.Identity)
            return (detail.HasStation ? "Unidentified station  " : "Unidentified site  ") + detail.Id;
        string name = string.IsNullOrEmpty(detail.DisplayName) ? detail.Id : detail.DisplayName;
        string id = string.IsNullOrEmpty(detail.DisplayName) || detail.DisplayName == detail.Id ? "" : "  " + detail.Id;
        return detail.HasStation ? name + id : name + id + "  (claimed, not yet founded)";
    }

    /// <summary>Capability names for the shared label helper. The node window already carries the
    /// named list, so it never needs the map's bitmask.</summary>
    private static string[] CapabilityNames(WireNodeDetail detail)
    {
        WireAssetCapability[] caps = detail.Capabilities;
        if (caps == null || caps.Length == 0) return null;
        var names = new string[caps.Length];
        for (int i = 0; i < caps.Length; i++) names[i] = caps[i].Cap;
        return names;
    }

    // ---- Overview -----------------------------------------------------------------------------

    private void SetRow(int row, string key, string value, string tooltip)
    {
        if (_keys[row] == null) return;
        _keys[row].Text = key;
        _values[row].Text = value;
        _values[row].SetToolTip(tooltip);
    }

    private void WriteOverview(WireNodeDetail detail)
    {
        bool identified = detail.Tier >= (byte)WireIntelTier.Identity;
        SetRow(0, "Owner", !identified ? "not identified"
                           : (string.IsNullOrEmpty(detail.OwnerTag) ? "unclaimed" : detail.OwnerTag)
                             + " (" + RelationLabel(detail.Relation) + ")", null);
        SetRow(1, "Hold", identified ? Percent(detail.HoldStrength) : "unknown", null);
        string zoneTip;
        SetRow(2, "Safe zone", DescribeZone(detail, out zoneTip), zoneTip);

        if (!detail.Owned)
        {
            // A scan's reading, with its age: what a raid on this node would be sized against as of
            // then. Read only at close range (Armament), so most foreign nodes say so.
            SetRow(3, "Defence", NodeLabels.Defence(detail.Defence, detail.DefenceAgeSeconds) ?? "not scanned",
                   "Read by a close scan and remembered. A raid meets what is there now.");
            for (int i = 4; i < OverviewRows; i++) SetRow(i, "", "", null);
            return;
        }

        SetRow(3, "Survey", DescribeSurvey(detail),
               "How much of the ground has been surveyed. Worth, Ore density and the ore mix firm up as it rises.");
        bool surveying = HasOre(detail) && detail.SurveyConfidence < 1.0;
        SetRow(4, "Worth", Percent(detail.ResourceValue) + Heading(detail.ResourceValue, detail.ResourceValueTarget, surveying),
               "Ore density times ore price. NPC factions claim and raid valuable ground first.");
        SetRow(5, "Ore density", Percent(detail.Richness) + Heading(detail.Richness, detail.RichnessTarget, surveying),
               "How much ore is under this ground, whatever its price. Mining here scales with it.");
        if (!detail.HasStation)
            SetRow(6, "Founding", Credits(detail.FoundCost) + " cr (have " + Credits(detail.FoundHave) + ")",
                   "What founding a station on this claim costs, against the faction treasury.");
        else
            SetRow(6, "Shipyard", ShipyardSummary(detail), null);
        if (detail.WorkerBeds > 0)
            SetRow(7, "Workers", detail.Workers + " of " + detail.WorkerBeds + " beds, " + DescribeWorkerSupply(detail),
                   "People living in this station's Habitat. They eat from its stock, and breathe from it unless "
                   + "the planet's air is breathable here; the ones it cannot keep leave.");
        else
            SetRow(7, "", "", null);
    }

    private static bool HasOre(WireNodeDetail detail)
    {
        return detail.Ores != null && detail.Ores.Length > 0;
    }

    private static string DescribeSurvey(WireNodeDetail detail)
    {
        if (!detail.Surveyed) return "not sampled yet";
        if (!HasOre(detail)) return "done, no ore here";
        if (detail.SurveyConfidence >= 1.0) return "complete";
        return Percent(detail.SurveyConfidence);
    }

    /// <summary>Where a reading is going while the survey runs: ", rising to 63%". Empty once the
    /// survey is done or the two already agree, so a settled number reads as settled.</summary>
    private static string Heading(double now, double target, bool surveying)
    {
        if (!surveying || Math.Abs(target - now) < 0.005) return "";
        return (target > now ? ", rising to " : ", falling to ") + Percent(target);
    }

    // ---- Ground -------------------------------------------------------------------------------

    /// <summary>The deposit ore by ore. Owner-only, like the two readings it explains: what a survey
    /// found belongs to the faction that ran it.</summary>
    private void WriteGround(WireNodeDetail detail)
    {
        if (_groundNote == null) return;
        if (!detail.Owned)
        {
            ClearOreRows(0);
            _groundNote.Text = "Only the owner knows what its survey found.";
            return;
        }

        WireNodeOre[] ores = detail.Ores ?? new WireNodeOre[0];
        bool priced = false;
        foreach (WireNodeOre ore in ores) if (ore.PriceRatio > 0) { priced = true; break; }

        int shown = Math.Min(ores.Length, OreRows);
        bool unresolved = false;
        for (int i = 0; i < shown; i++)
        {
            _oreNames[i].Text = ores[i].SubTypeId;
            _oreShares[i].Text = (ores[i].Resolved ? "" : "~") + Percent(ores[i].Share);
            _orePrices[i].Text = priced && ores[i].PriceRatio > 0
                ? ores[i].PriceRatio.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x"
                : "-";
            _oreWorths[i].Text = priced ? Percent(ores[i].WorthShare) : "-";
            if (!ores[i].Resolved) unresolved = true;
        }
        ClearOreRows(shown);

        string more = ores.Length > shown ? "+" + (ores.Length - shown) + " more. " : "";
        if (!detail.Surveyed)
            _groundNote.Text = "Nobody has sampled this ground yet.";
        else if (ores.Length == 0)
            _groundNote.Text = "The survey found no ore here.";
        else if (unresolved)
            _groundNote.Text = more + "~ not yet certain: so far the survey can resolve shares above "
                               + FinePercent(detail.ResolvedShareFloor) + ".";
        else if (detail.SurveyConfidence < 1.0)
            _groundNote.Text = more + "Every share is firm; Worth and Ore density are still settling.";
        else
            _groundNote.Text = more + "Survey complete.";
    }

    private void ClearOreRows(int from)
    {
        for (int i = from; i < OreRows; i++)
        {
            if (_oreNames[i] == null) continue;
            _oreNames[i].Text = "";
            _oreShares[i].Text = "";
            _orePrices[i].Text = "";
            _oreWorths[i].Text = "";
        }
    }

    /// <summary>A percentage that keeps a decimal below 10%, where the survey's noise floor lives
    /// for most of its run.</summary>
    private static string FinePercent(double fraction)
    {
        double pct = fraction * 100.0;
        return pct.ToString(pct < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>How long the node's food and oxygen keep its workers, for the overview row and the
    /// recruit window: whichever runs out names itself, and air the workers breathe off the planet
    /// is never the shortage. FAC-493.</summary>
    internal static string DescribeWorkerSupply(WireNodeDetail detail)
    {
        if (detail.Workers <= 0) return "nobody to feed";
        bool needFood = detail.WorkerFoodHours >= 0, needAir = detail.WorkerAirHours >= 0;
        bool noFood = needFood && detail.WorkerFoodHours <= 0, noAir = needAir && detail.WorkerAirHours <= 0;
        if (noFood && noAir) return "no food or air";
        if (noFood) return "no food";
        if (noAir) return "no air";
        if (!needFood && !needAir) return "nothing owed";
        double hours = !needAir ? detail.WorkerFoodHours
            : !needFood ? detail.WorkerAirHours
            : Math.Min(detail.WorkerFoodHours, detail.WorkerAirHours);
        string what = !needAir ? "food" : !needFood ? "air"
            : detail.WorkerFoodHours <= detail.WorkerAirHours ? "food" : "air";
        return what + " for " + DescribeHours(hours);
    }

    private static string DescribeHours(double hours)
    {
        if (hours >= 48) return (int)(hours / 24) + " days";
        if (hours >= 1) return hours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " h";
        return (int)Math.Max(1, hours * 60) + " min";
    }

    /// <summary>
    /// What this station's protection is doing, short enough for the overview's value column, with
    /// the full sentence as its tooltip.
    ///
    /// <para>Shown for any viewer, not only the owner: the bubble is visible from outside it, so
    /// this states what someone standing there can already see, and it is what a defender reading
    /// their own window mid-raid most needs.</para>
    /// </summary>
    private static string DescribeZone(WireNodeDetail detail, out string tooltip)
    {
        tooltip = null;
        DateTime until = detail.ZoneUntilUtcTicks > 0
            ? new DateTime(detail.ZoneUntilUtcTicks, DateTimeKind.Utc)
            : DateTime.MinValue;
        TimeSpan remaining = until - DateTime.UtcNow;

        switch ((WireZoneState)detail.ZoneState)
        {
            case WireZoneState.Besieged:
                // The dwell is only half the breach condition -- the shield must also be empty at
                // that moment -- so this is the earliest it can drop, not a promise that it will.
                if (remaining.TotalSeconds > 0)
                    tooltip = "The shield can break in " + Duration(remaining) + " at the earliest.";
                return "UNDER SIEGE, shield " + Percent(detail.ZoneIntegrity);
            case WireZoneState.Breached:
                return "BREACHED, raid " + Percent(detail.ZoneRaidProgress);
            case WireZoneState.Raided:
                return "BREACHED, raided";
            case WireZoneState.Down:
                return remaining.TotalSeconds > 0 ? "DOWN, back in " + Duration(remaining) : "DOWN";
            default:
                return detail.HasStation ? "intact" : "none";
        }
    }

    private static string ShipyardSummary(WireNodeDetail detail)
    {
        if (!HasShipyard(detail)) return "none";
        int queued = (detail.Builds == null ? 0 : detail.Builds.Length)
                     + (detail.Repairs == null ? 0 : detail.Repairs.Length);
        if (detail.ShipyardPaused) return "paused, " + queued + " queued";
        return queued == 0 ? "idle" : queued + " queued";
    }

    /// <summary>Whether this node carries a Shipyard, read off the named capability list. Level 0
    /// does not count: an entry at zero is a capability the node does not have.</summary>
    private static bool HasShipyard(WireNodeDetail detail)
    {
        WireAssetCapability[] caps = detail.Capabilities;
        if (caps == null) return false;
        for (int i = 0; i < caps.Length; i++)
        {
            if (caps[i].Level >= 1
                && string.Equals(caps[i].Cap, "Shipyard", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void WriteInstalled(WireNodeDetail detail)
    {
        _installed.Clear();
        if (!detail.Owned) return;
        WireAssetCapability[] caps = detail.Capabilities ?? new WireAssetCapability[0];
        if (caps.Length == 0)
        {
            _installed.AppendText(detail.HasStation ? "Nothing installed." : "Found a station to install anything.");
            return;
        }
        var text = new StringBuilder();
        for (int i = 0; i < caps.Length; i++)
        {
            if (i > 0) text.Append(", ");
            text.Append(caps[i].Cap);
            if (caps[i].Level > 1) text.Append(" x").Append(caps[i].Level);
        }
        _installed.AppendText(text.ToString());
    }

    /// <summary>Every snapshot bumps the revision, so the rows are rebuilt only when what they show
    /// changed, and then without moving the scroll (<see cref="TableRows"/>).</summary>
    private void WriteShips(WireNodeDetail detail)
    {
        WireNodeShip[] ships = detail.Ships ?? new WireNodeShip[0];
        var signature = new StringBuilder();
        for (int i = 0; i < ships.Length; i++)
            signature.Append(ships[i].PrefabId).Append('\u001f').Append(ships[i].Ready).Append('\u001f')
                     .Append(ships[i].ListedForSale).Append('\u001e');
        if (!TableRows.Changed(ref _drawnShips, signature.ToString())) return;

        TableRows.PreservingScroll(_ships, () =>
        {
            _ships.Clear();
            for (int i = 0; i < ships.Length; i++)
            {
                var row = new MyGuiControlTable.Row(ships[i]);
                row.AddCell(new MyGuiControlTable.Cell(ships[i].PrefabId, null, ships[i].PrefabId));
                row.AddCell(new MyGuiControlTable.Cell(ships[i].Ready.ToString()));
                row.AddCell(new MyGuiControlTable.Cell(ships[i].ListedForSale > 0 ? ships[i].ListedForSale.ToString() : ""));
                row.AddCell(new MyGuiControlTable.Cell(""));
                _ships.Add(row);
            }
        });
    }

    /// <summary>Row counts on the tabs, so a player on Overview can see whether the others hold
    /// anything without opening them.</summary>
    private void WriteTabLabels(WireNodeDetail detail)
    {
        int stock = detail.Owned && detail.Stock != null ? detail.Stock.Length : 0;
        int ships = detail.Owned && detail.Ships != null ? detail.Ships.Length : 0;
        int history = detail.Owned && detail.Production != null ? detail.Production.Length : 0;
        int demand = detail.Owned && detail.Demand != null ? detail.Demand.Length : 0;
        _stockTab.Text = detail.Owned ? "Stock (" + stock + ")" : "Stock";
        _shipsTab.Text = detail.Owned ? "Ships (" + ships + ")" : "Ships";
        _historyTab.Text = detail.Owned ? "History (" + history + ")" : "History";
        _demandTab.Text = detail.Owned ? "Demand (" + demand + ")" : "Demand";
    }

    // ---- Demand -------------------------------------------------------------------------------

    /// <summary>Both tables rebuilt only when a shown value changed, keeping each one's scroll. The
    /// ages are part of the signatures, as on History, so the rows keep ageing between changes.</summary>
    private void WriteDemand(WireNodeDetail detail)
    {
        if (!detail.Owned)
        {
            _demand.Clear();
            _ended.Clear();
            _drawnDemand = null;
            _drawnEnded = null;
            _endedCaption.Text = "";
            _demandNote.Text = "Demand is only shown for your own faction's stations.";
            return;
        }

        WireNodeDemand[] open = detail.Demand ?? new WireNodeDemand[0];
        WireNodeEndedDemand[] ended = detail.EndedDemand ?? new WireNodeEndedDemand[0];
        string window = WindowLabel(detail.DemandWindowSeconds);
        _endedCaption.Text = ended.Length == 0
            ? "Nothing asked for here ended in the " + window + "."
            : "Ended in the " + window + ", most recent first:";
        _demandNote.Text = open.Length == 0
            ? "Nothing is wanted here now."
            : "Most urgent first. Used /h is what this station has consumed of it per hour, smoothed.";

        var openAges = new string[open.Length];
        var signature = new StringBuilder();
        for (int i = 0; i < open.Length; i++)
        {
            openAges[i] = Since(open[i].FirstPostedUtcTicks);
            signature.Append(open[i].Kind).Append('\u001f').Append(open[i].DestNodeId).Append('\u001f')
                     .Append(open[i].Quantity).Append('\u001f').Append(open[i].Have).Append('\u001f')
                     .Append(DemandText.Rate(open[i])).Append('\u001f').Append(open[i].Class).Append('\u001f')
                     .Append(open[i].DestinedForSale ? 'S' : '-').Append('\u001f').Append(openAges[i]).Append('\u001e');
        }
        if (TableRows.Changed(ref _drawnDemand, signature.ToString()))
            TableRows.PreservingScroll(_demand, () => AddDemandRows(open, openAges));

        var endedAges = new string[ended.Length];
        signature.Clear();
        for (int i = 0; i < ended.Length; i++)
        {
            endedAges[i] = Ago(ended[i].EndedUtcTicks);
            signature.Append(ended[i].Kind).Append('\u001f').Append(ended[i].DestNodeId).Append('\u001f')
                     .Append(ended[i].PeakQuantity).Append('\u001f').Append(ended[i].Class).Append('\u001f')
                     .Append(ended[i].Consumed ? 'M' : 'L').Append('\u001f').Append(ended[i].FirstPostedUtcTicks)
                     .Append('\u001f').Append(endedAges[i]).Append('\u001e');
        }
        if (TableRows.Changed(ref _drawnEnded, signature.ToString()))
            TableRows.PreservingScroll(_ended, () => AddEndedRows(ended, endedAges));
    }

    private void AddDemandRows(WireNodeDemand[] open, string[] ages)
    {
        _demand.Clear();
        for (int i = 0; i < open.Length; i++)
        {
            WireNodeDemand d = open[i];
            string key = DemandText.ItemKey(d.Kind);
            var why = (WireDemandClass)d.Class;
            var row = new MyGuiControlTable.Row(d);
            row.AddCell(new MyGuiControlTable.Cell(DemandText.Label(d.Kind, d.DestNodeId), null,
                DemandText.Tooltip(d.Kind, d.DestinedForSale)));
            row.AddCell(new MyGuiControlTable.Cell(d.Quantity > 0 && key != null ? Amount(key, d.Quantity) : "-", null,
                d.Quantity > 0 && key != null ? "Wanted: " + Exact(key, d.Quantity) : null));
            row.AddCell(new MyGuiControlTable.Cell(d.HasHave ? Amount(key, d.Have) : "", null, null,
                d.HasHave && d.Quantity > 0 && d.Have < d.Quantity ? Warn : (Color?)null));
            row.AddCell(new MyGuiControlTable.Cell(DemandText.Rate(d)));
            row.AddCell(new MyGuiControlTable.Cell(DemandText.ClassLabel(why, d.Kind), null, DemandText.ClassTooltip(why, d.Kind)));
            row.AddCell(new MyGuiControlTable.Cell(ages[i], null,
                d.FirstPostedUtcTicks > 0 ? "First asked for " + ages[i] + " ago." : null));
            row.AddCell(new MyGuiControlTable.Cell(""));
            _demand.Add(row);
        }
    }

    private void AddEndedRows(WireNodeEndedDemand[] ended, string[] ages)
    {
        _ended.Clear();
        for (int i = 0; i < ended.Length; i++)
        {
            WireNodeEndedDemand e = ended[i];
            string key = DemandText.ItemKey(e.Kind);
            var why = (WireDemandClass)e.Class;
            string open = e.FirstPostedUtcTicks > 0 && e.EndedUtcTicks > e.FirstPostedUtcTicks
                ? Duration(new TimeSpan(e.EndedUtcTicks - e.FirstPostedUtcTicks)) : "";
            var row = new MyGuiControlTable.Row(e);
            row.AddCell(new MyGuiControlTable.Cell(DemandText.Label(e.Kind, e.DestNodeId), null,
                DemandText.Tooltip(e.Kind, e.DestinedForSale)));
            row.AddCell(new MyGuiControlTable.Cell(e.PeakQuantity > 0 && key != null ? Amount(key, e.PeakQuantity) : "-", null,
                e.PeakQuantity > 0 && key != null ? "Largest amount asked for: " + Exact(key, e.PeakQuantity) : null));
            row.AddCell(new MyGuiControlTable.Cell(DemandText.ClassLabel(why, e.Kind), null, DemandText.ClassTooltip(why, e.Kind)));
            row.AddCell(new MyGuiControlTable.Cell(open));
            row.AddCell(new MyGuiControlTable.Cell((e.Consumed ? "met " : "lapsed ") + ages[i], null,
                DemandText.EndedTooltip(e.Consumed),
                e.Consumed ? Produced : (Color?)null));
            row.AddCell(new MyGuiControlTable.Cell(""));
            _ended.Add(row);
        }
    }

    private static string Since(long utcTicks)
    {
        if (utcTicks <= 0) return "";
        TimeSpan age = DateTime.UtcNow - new DateTime(utcTicks, DateTimeKind.Utc);
        return age.TotalSeconds < 1 ? "just now" : Duration(age);
    }

    // ---- History ------------------------------------------------------------------------------

    /// <summary>Rebuilt only when a shown value changed, keeping the scroll, as
    /// <see cref="WriteShips"/>. The age column is part of the signature, so an idle station's rows
    /// still age on each snapshot instead of freezing at the last batch.</summary>
    private void WriteHistory(WireNodeDetail detail)
    {
        if (!detail.Owned)
        {
            _history.Clear();
            _drawnHistory = null;
            _historyNote.Text = "Production history is only shown for your own faction's stations.";
            return;
        }

        WireNodeProductionSpan[] spans = detail.Production ?? new WireNodeProductionSpan[0];
        string window = WindowLabel(detail.ProductionWindowSeconds);
        _historyNote.Text = spans.Length == 0
            ? "Nothing refined or built here in the " + window + "."
            : "Refined and built here in the " + window + ", most recent first: + produced, - used up.";

        var ages = new string[spans.Length];
        var signature = new StringBuilder();
        for (int i = 0; i < spans.Length; i++)
        {
            ages[i] = Ago(spans[i].EndUtcTicks);
            signature.Append(spans[i].RecipeId).Append('\u001f').Append(spans[i].Key).Append('\u001f')
                     .Append(spans[i].IsOutput ? '+' : '-').Append('\u001f').Append(spans[i].Quantity)
                     .Append('\u001f').Append(ages[i]).Append('\u001e');
        }
        if (!TableRows.Changed(ref _drawnHistory, signature.ToString())) return;

        TableRows.PreservingScroll(_history, () =>
        {
            _history.Clear();
            for (int i = 0; i < spans.Length; i++)
            {
                WireNodeProductionSpan span = spans[i];
                Color tone = span.IsOutput ? Produced : Consumed;
                string sign = span.IsOutput ? "+" : "-";
                var row = new MyGuiControlTable.Row(span);
                row.AddCell(new MyGuiControlTable.Cell(sign, null, span.IsOutput ? "Produced" : "Used up", tone));
                row.AddCell(new MyGuiControlTable.Cell(ItemNames.Describe(span.Key), null,
                    span.Key + "\n" + (span.IsOutput ? "Produced: " : "Used up: ") + Exact(span.Key, span.Quantity)));
                row.AddCell(new MyGuiControlTable.Cell(span.RecipeId, null, span.RecipeId));
                row.AddCell(new MyGuiControlTable.Cell(sign + Amount(span.Key, span.Quantity), null, null, tone));
                row.AddCell(new MyGuiControlTable.Cell(ages[i], null, SpanTooltip(span)));
                row.AddCell(new MyGuiControlTable.Cell(""));
                _history.Add(row);
            }
        });
    }

    /// <summary>"last 5 minutes", from the retention the server sent.</summary>
    private static string WindowLabel(int seconds)
    {
        if (seconds <= 0) return "recent past";
        if (seconds % 60 == 0) return "last " + (seconds / 60) + (seconds == 60 ? " minute" : " minutes");
        return "last " + seconds + " seconds";
    }

    private static string SpanTooltip(WireNodeProductionSpan span)
    {
        if (span.StartUtcTicks <= 0 || span.EndUtcTicks <= span.StartUtcTicks)
            return "One batch, " + Ago(span.EndUtcTicks) + ".";
        return "Running for " + Duration(new TimeSpan(span.EndUtcTicks - span.StartUtcTicks))
               + ", last batch " + Ago(span.EndUtcTicks) + ".";
    }

    private static string Ago(long utcTicks)
    {
        if (utcTicks <= 0) return "";
        TimeSpan age = DateTime.UtcNow - new DateTime(utcTicks, DateTimeKind.Utc);
        return age.TotalSeconds < 1 ? "just now" : Duration(age) + " ago";
    }

    // ---- Stock --------------------------------------------------------------------------------

    /// <summary>Rebuilt only when a shown value changed, keeping the scroll, as
    /// <see cref="WriteShips"/>.</summary>
    private void WriteStock(WireNodeDetail detail)
    {
        if (!detail.Owned)
        {
            _stock.Clear();
            _drawnStock = null;
            _stockNote.Text = "Stock and ships are only shown for your own faction's stations.";
            return;
        }

        WireNodeStockItem[] stock = detail.Stock ?? new WireNodeStockItem[0];
        _stockNote.Text = stock.Length == 0
            ? "Nothing held here."
            : detail.HasStore
                ? "For sale is what this station's store, refuel and rearm will sell you now."
                : "This station has no store, so nothing here is for sale.";

        var signature = new StringBuilder(detail.HasStore ? "S" : "-");
        for (int i = 0; i < stock.Length; i++)
            signature.Append(stock[i].Key).Append('\u001f').Append(stock[i].Quantity).Append('\u001f')
                     .Append(stock[i].ForSale).Append('\u001f').Append(stock[i].KeptFor).Append('\u001f')
                     .Append(stock[i].Target).Append('\u001f').Append(stock[i].Reserved).Append('\u001e');
        if (!TableRows.Changed(ref _drawnStock, signature.ToString())) return;

        TableRows.PreservingScroll(_stock, () => AddStockRows(detail, stock));
    }

    private void AddStockRows(WireNodeDetail detail, WireNodeStockItem[] stock)
    {
        _stock.Clear();
        for (int i = 0; i < stock.Length; i++)
        {
            WireNodeStockItem item = stock[i];
            long kept = Math.Max(0, item.Quantity - item.ForSale);
            bool noneOffered = detail.HasStore && item.Quantity > 0 && item.ForSale == 0;
            var why = (WireStockKeptFor)item.KeptFor;

            var row = new MyGuiControlTable.Row(item);
            row.AddCell(new MyGuiControlTable.Cell(ItemNames.Describe(item.Key), null,
                item.Key + "\nHeld: " + Exact(item.Key, item.Quantity)
                + (item.Target > 0 ? "\nKept back for this station's own needs: up to " + Exact(item.Key, item.Target) + "." : "")));
            row.AddCell(new MyGuiControlTable.Cell(Amount(item.Key, item.Quantity)));
            row.AddCell(new MyGuiControlTable.Cell(
                detail.HasStore ? Amount(item.Key, item.ForSale) : "-", null, null,
                noneOffered ? Warn : (Color?)null));
            row.AddCell(new MyGuiControlTable.Cell(kept > 0 && why != WireStockKeptFor.None ? Amount(item.Key, kept) : ""));
            row.AddCell(new MyGuiControlTable.Cell(KeptForLabel(why), null, KeptForTooltip(why, item)));
            row.AddCell(new MyGuiControlTable.Cell(item.Reserved > 0 ? Amount(item.Key, item.Reserved) : "", null,
                item.Reserved > 0 ? "Moved aside for a capability install. Not part of Held, and never sold." : null));
            row.AddCell(new MyGuiControlTable.Cell(""));
            _stock.Add(row);
        }
    }

    private static string KeptForLabel(WireStockKeptFor why)
    {
        switch (why)
        {
            case WireStockKeptFor.Owed: return "owed bills";
            case WireStockKeptFor.Use: return "own use";
            case WireStockKeptFor.Sales: return "steady sales";
            case WireStockKeptFor.GasFloor: return "gas reserve";
            case WireStockKeptFor.Floor: return "minimum";
            case WireStockKeptFor.ListingCap: return "row cap";
            case WireStockKeptFor.NotTraded: return "not traded";
            case WireStockKeptFor.NoStore: return "no store";
            default: return "";
        }
    }

    private static string KeptForTooltip(WireStockKeptFor why, WireNodeStockItem item)
    {
        string target = Amount(item.Key, item.Target);
        switch (why)
        {
            case WireStockKeptFor.Owed:
                return "Kept for the ship and install bills owed at this station: up to " + target + ".";
            case WireStockKeptFor.Use:
                return "Kept for what this station uses itself — its production, and fuel for the hulls "
                       + "that depart from it: up to " + target + ".";
            case WireStockKeptFor.Sales:
                return "Kept so players buying here keep finding it on the shelf: it aims to hold " + target + ".";
            case WireStockKeptFor.GasFloor:
                return "Every station holding gas keeps at least " + target + " in reserve.";
            case WireStockKeptFor.Floor:
                return "Something here wants this, so the station keeps at least " + target + ".";
            case WireStockKeptFor.ListingCap:
                return "More than one store row may carry. The rest is offered as the row sells down.";
            case WireStockKeptFor.NotTraded:
                return "This station's store does not trade this item.";
            case WireStockKeptFor.NoStore:
                return "This station has no store, so nothing here is for sale.";
            default:
                return null;
        }
    }

    /// <summary>See <see cref="Quantities.Item"/>.</summary>
    private static string Amount(string key, long units)
    {
        return Quantities.Item(key, units);
    }

    private static string Exact(string key, long units)
    {
        return Quantities.ItemExact(key, units);
    }

    private static string Credits(long value)
    {
        return value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- Buttons and status -------------------------------------------------------------------

    private void RefreshButtons(WireNodeDetail detail)
    {
        if (_shipyard != null)
        {
            _shipyard.Enabled = detail.Owned;
            int queued = (detail.Builds == null ? 0 : detail.Builds.Length)
                         + (detail.Repairs == null ? 0 : detail.Repairs.Length);
            _shipyard.Text = detail.ShipyardPaused ? "Shipyard (paused)"
                : queued > 0 ? "Shipyard (" + queued + " queued)" : "Shipyard...";
        }
        // One button, two jobs, decided by whether there is a station to install into. A bare claim
        // cannot take a capability, so the develop menu behind it would be nine rows of that one
        // refusal; what it can take is a station, and that is the purchase offered instead.
        if (_develop != null)
        {
            bool founded = detail.HasStation;
            _develop.Enabled = detail.Owned;
            _develop.Text = founded ? "Develop..." : "Found...";
            _develop.SetToolTip(founded
                ? "Install a capability at this station, and decide whether Factions works toward it "
                  + "on its own."
                : "Plant a real station on this claim, paying its cost from the faction treasury. "
                  + "Capabilities, a store and contracts all need one.");
        }
        if (_hold != null) _hold.Enabled = detail.Owned;
        // Offered only where the server quotes a price: an owned Habitat on a body with a pool.
        if (_recruit != null) _recruit.Enabled = detail.Owned && detail.RecruitPrice > 0;
        if (_trade != null) _trade.Enabled = detail.Owned;
        // A founded station as well as ownership: there is nothing to rebuild on a bare claim.
        // Feature-gated too, because a server may withhold the surface that rebuilds a live grid
        // while still granting the window it opens from.
        if (_refit != null)
            _refit.Enabled = detail.Owned && detail.HasStation && StationRefitComponent.Available;
        // Ownership and the feature bit, and deliberately NOT whether anything is mineable within
        // reach: that is the server's own eligibility flag, it arrives with the order pick-list, and
        // a button disabled on a list that has not arrived yet is indistinguishable from a dead one.
        if (_mine != null)
            _mine.Enabled = detail.Owned && StationMineComponent.Available;
        // Range as well as ownership: this is the one action on this window that is about where the
        // player is standing rather than about what their faction owns, and the server refuses it
        // from anywhere else.
        if (_transfer != null)
        {
            _transfer.Enabled = detail.Owned && NodeStockComponent.Available
                                && NodeStockComponent.InRangeOf(detail.XKm, detail.YKm, detail.ZKm);
        }
    }

    private void RefreshStatus(NodeDetailComponent component, WireNodeDetail detail)
    {
        // A command's reply outranks the age line for as long as it is fresh: it is the only place
        // a refusal is ever shown.
        if (!string.IsNullOrEmpty(component.LastCommandMessage)
            && (DateTime.UtcNow - component.LastCommandUtc).TotalSeconds
               < NodeDetailComponent.CommandMessageSeconds)
        {
            _status.Text = OneLine(component.LastCommandMessage);
            return;
        }

        int age = (int)Math.Max(0, (DateTime.UtcNow - component.LastSnapshotUtc).TotalSeconds);
        string summary;
        if (!detail.Owned) summary = "Not your faction's territory";
        else
        {
            WireNodeStockItem[] stock = detail.Stock ?? new WireNodeStockItem[0];
            int held = 0, offered = 0;
            for (int i = 0; i < stock.Length; i++)
            {
                if (stock[i].Quantity <= 0) continue;
                held++;
                if (stock[i].ForSale > 0) offered++;
            }
            summary = detail.HasStore ? offered + " of " + held + " item(s) for sale" : held + " item(s), no store";
        }
        _status.Text = summary + "   -   updated " + age + "s ago";
    }

    // ---- Actions ------------------------------------------------------------------------------

    /// <summary>Reads the live snapshot rather than the button's own label: the two windows behind
    /// this verb are different purchases, and a snapshot that arrived between the draw and the click
    /// must not open the wrong one.</summary>
    private void OnDevelopClicked(MyGuiControlButton button)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        if (component == null || !component.HasSnapshot) return;
        if (component.Detail.HasStation) StationDevelopScreen.Open(_nodeId);
        else StationFoundScreen.Open(_nodeId);
    }

    /// <summary>The display name goes with the id so the Mine window can head itself without a
    /// second lookup: it has no node snapshot of its own, only the faction-wide order list.</summary>
    private void OnMineClicked(MyGuiControlButton button)
    {
        NodeDetailComponent component = NodeDetailComponent.Instance;
        string name = component != null && component.Detail.Exists ? component.Detail.DisplayName : null;
        StationMineScreen.Open(_nodeId, name);
    }

    // ---- Formatting ---------------------------------------------------------------------------

    private static string Duration(TimeSpan span)
    {
        if (span.TotalHours >= 1.0) return ((int)span.TotalHours) + "h " + span.Minutes + "m";
        if (span.TotalMinutes >= 1.0) return ((int)span.TotalMinutes) + "m " + span.Seconds + "s";
        return ((int)span.TotalSeconds) + "s";
    }

    private static string Percent(double fraction)
    {
        return (fraction * 100.0).ToString("0") + "%";
    }

    /// <summary>The runtime's replies are written for chat and can carry newlines; the status line
    /// is a single <see cref="MyGuiControlLabel"/>.</summary>
    private static string OneLine(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string RelationLabel(byte relation)
    {
        switch (relation)
        {
            case 1: return "yours";
            case 2: return "neutral";
            case 3: return "hostile";
            case 4: return "friendly";
            default: return "unclaimed";
        }
    }

    private static string ZoneLabel(byte zone)
    {
        switch (zone)
        {
            case 1: return "orbit";
            case 2: return "surface";
            default: return "deep space";
        }
    }
}
