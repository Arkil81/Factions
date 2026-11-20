using System;
using System.Text;
using Factions.Wire;
using FactionsUI.Map;
using FactionsUI.Nodes;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Assets;

/// <summary>
/// The "Assets" page of SE's own terminal (K menu): everything the viewing player and their
/// faction own, in one list. Built with vanilla controls at vanilla coordinates so it reads as
/// another tab of the terminal rather than a plugin window wearing its skin.
///
/// One instance per terminal screen — the screen rebuilds its tabs every time it opens, and on a
/// resolution change. <see cref="Active"/> is the one the player is currently looking at; it is
/// how <see cref="Plugin.Update"/> reaches the page without the page having to survive the screen.
/// </summary>
internal sealed class AssetsPage
{
    /// <summary>Tab page key. SE's own pages are the <c>MyTerminalPageEnum</c> ordinals 0-6
    /// (Inventory..Gps); pages are created lazily by key, so 7 appends after them without
    /// disturbing a single vanilla page.</summary>
    internal const int PageKey = 7;

    /// <summary>Seconds between refreshes of the columns that change without a server message.
    /// The register itself only changes on a snapshot, but the player keeps moving and a convoy
    /// keeps closing on its target, and numbers that only updated with the snapshot would read as
    /// wrong rather than stale.</summary>
    private const double CountdownRefreshSeconds = 0.5;

    private const int FilterAll = -1;
    private const int CategoryStation = 0;
    private const int CategoryFleet = 1;
    private const int CategoryGrid = 2;

    internal static AssetsPage Active;

    private readonly MyGuiControlTabControl _tabs;
    private readonly MyGuiControlTable _table;
    private readonly MyGuiControlMultilineText _detail;
    private readonly MyGuiControlLabel _heading;
    private readonly MyGuiControlLabel _status;
    /// <summary>Opens the selected station in <see cref="NodeDetailScreen"/>. Only ever enabled for
    /// a station row: a convoy and a player-built grid are not nodes and have no window.</summary>
    private readonly MyGuiControlButton _openNode;
    /// <summary>Opens the faction's Fleet window. Unlike <see cref="_openNode"/> it acts on no row
    /// — a faction has one doctrine — so it is enabled purely on the server granting the feature.</summary>
    private readonly MyGuiControlButton _openFleet;
    private readonly MyGuiControlButton _openStore;
    private readonly MyGuiControlButton _openMarket;
    private readonly MyGuiControlButton _openCampaigns;
    /// <summary>Opens the faction Demand window: everything the faction is asking for, at every
    /// station and faction-wide. Acts on no row, like <see cref="_openFleet"/>.</summary>
    private readonly MyGuiControlButton _openDemand;

    /// <summary>Last register revision drawn, so the table is only rebuilt when the data actually
    /// changed rather than on every frame the tab is up.</summary>
    private int _drawnRevision = -1;

    /// <summary>Filter the table down to one category, or <see cref="FilterAll"/>.</summary>
    private int _filter = FilterAll;

    /// <summary>Identity of the selected row, so a rebuild triggered by a snapshot does not throw
    /// away what the player was reading.</summary>
    private string _selectedKey;

    private double _nextCountdownRefreshSeconds;

    private AssetsPage(
        MyGuiControlTabControl tabs, MyGuiControlTable table, MyGuiControlMultilineText detail,
        MyGuiControlLabel heading, MyGuiControlLabel status, MyGuiControlButton openNode,
        MyGuiControlButton openFleet,
        MyGuiControlButton openStore,
        MyGuiControlButton openMarket,
        MyGuiControlButton openCampaigns,
        MyGuiControlButton openDemand)
    {
        _tabs = tabs;
        _table = table;
        _detail = detail;
        _heading = heading;
        _status = status;
        _openNode = openNode;
        _openFleet = openFleet;
        _openStore = openStore;
        _openMarket = openMarket;
        _openCampaigns = openCampaigns;
        _openDemand = openDemand;
    }

    /// <summary>True while the terminal actually has this page on screen. Nothing is streamed
    /// while the player is looking at Inventory or Control Panel.</summary>
    private bool IsSelected
    {
        get { return _tabs != null && _tabs.SelectedPage == PageKey; }
    }

    // ---- Construction ---------------------------------------------------------------------

    /// <summary>
    /// Adds the page to a terminal's tab control and makes it <see cref="Active"/>. Coordinates
    /// mirror SE's own Factions page: a list on the left, detail on the right, both inside the
    /// same margins, so the two tabs line up when the player flicks between them.
    /// </summary>
    internal static void Build(MyGuiControlTabControl tabs)
    {
        // Captured before the page exists: the button row has to be re-scaled for the new count,
        // and both terms of that are gone once GetTabSubControl has run.
        int pagesBefore = tabs.PagesCount;
        float scaleBefore = tabs.TabButtonScale;

        MyGuiControlTabPage page = tabs.GetTabSubControl(PageKey);
        page.Name = "PageFactionsAssets";
        page.Text = new StringBuilder("Assets");
        page.TextScale = 0.7005405f;   // vanilla tab caption scale

        var separators = new MyGuiControlSeparatorList();
        separators.AddVertical(new Vector2(0.152f, -0.343f), 0.646f, 0.002f);
        page.Controls.Add(separators);

        var heading = new MyGuiControlLabel(
            new Vector2(-0.452f, -0.336f), null, "", null, 0.8f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            Name = "labelAssetsHeading",
        };
        page.Controls.Add(heading);

        var filter = new MyGuiControlCombobox
        {
            Position = new Vector2(-0.106f, -0.343f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = new Vector2(0.244f, 0.04f),
            Name = "AssetsFilter",
        };
        // Keys are the category constants, plus -1 for "everything", so the selected key IS the
        // filter and no lookup table is needed to turn one into the other.
        filter.AddItem(FilterAll, "All assets", null, null, false);
        filter.AddItem(CategoryStation, "Stations", null, null, false);
        filter.AddItem(CategoryFleet, "Convoys", null, null, false);
        filter.AddItem(CategoryGrid, "Ships & grids", null, null, false);
        filter.SelectItemByKey(FilterAll, false);
        page.Controls.Add(filter);

        var table = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(-0.452f, -0.297f),
            Size = new Vector2(0.59f, 0.15f),
            Name = "AssetsTable",
            ColumnsCount = 4,
            VisibleRowsCount = 13,   // matches SE's own Factions table: the page has room for no more
        };
        table.SetCustomColumnWidths(new float[4] { 0.44f, 0.17f, 0.23f, 0.16f });
        table.SetColumnName(0, new StringBuilder("Asset"));
        table.SetColumnName(1, new StringBuilder("Type"));
        table.SetColumnName(2, new StringBuilder("Status"));
        table.SetColumnName(3, new StringBuilder("Distance"));
        table.SetHeaderColumnMargin(0, new Thickness(0.01f, 0f, 0f, 0f));
        table.SetHeaderColumnMargin(1, new Thickness(0.005f, 0f, 0.005f, 0f));
        table.SetHeaderColumnMargin(2, new Thickness(0.005f, 0f, 0.005f, 0f));
        table.SetHeaderColumnMargin(3, new Thickness(0f));
        page.Controls.Add(table);

        var detail = new MyGuiControlMultilineText(
            position: new Vector2(0.166f, -0.297f),
            size: new Vector2(0.285f, 0.56f),
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
            Name = "AssetsDetail",
            CanHaveFocus = true,
        };
        page.Controls.Add(detail);

        // Sits in the gap between the detail panel's lower edge (-0.297 + 0.56) and the status
        // line, so it appears under what it acts on without moving a single existing control.
        // Half-width since the Demand button took the right half of this row: there is no fourth
        // row to put it on (see Fleet below).
        var openNode = new MyGuiControlButton(
            position: new Vector2(0.166f, 0.266f),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.14f, 0.032f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Open this station's own window: stock, build queue and shipyard controls.",
            text: new StringBuilder("Open station"),
            textScale: 0.7f)
        {
            Name = "AssetsOpenNode",
            Enabled = false,
        };
        page.Controls.Add(openNode);

        // Faction-wide and takes no selection, like Fleet. Beside Open station because the station
        // window's Demand tab is the one-station view of the same rows.
        var openDemand = new MyGuiControlButton(
            position: new Vector2(0.311f, 0.266f),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.14f, 0.032f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Everything your faction is asking for, at every station and faction-wide, "
                     + "and what it stopped asking for lately.",
            text: new StringBuilder("Demand"),
            textScale: 0.7f)
        {
            Name = "AssetsOpenDemand",
            Enabled = false,
        };
        page.Controls.Add(openDemand);

        // Beside the station button rather than on a tab of its own: the doctrine is faction-wide,
        // and a tenth terminal page would rescale the whole vanilla tab-button row again (see
        // FleetDoctrineScreen). Acts on no row, so it never takes a selection.
        //
        // This slot was "Standing orders" until v0.219.0. Mine orders are anchored at a station and
        // moved to that station's own window; what is left that is genuinely faction-wide is the
        // fleet doctrine, so the button says so.
        // Half-width, sharing its row with Store, for the reason the Market/Campaigns row below is
        // split: a third full-width row at 0.378 lands below the page's clickable area and draws as
        // a button nothing can press. The two belong side by side anyway — one says what the fleet
        // is made of, the other what the shop sells, and they count the same hulls differently.
        var openFleet = new MyGuiControlButton(
            position: new Vector2(0.166f, 0.302f),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.14f, 0.032f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "What your fleet is made of: the hulls your faction builds and keeps, what is "
                     + "waiting on hulls, and what you already own.",
            text: new StringBuilder("Fleet"),
            textScale: 0.7f)
        {
            Name = "AssetsOpenFleet",
            Enabled = false,
        };
        page.Controls.Add(openFleet);

        var openStore = new MyGuiControlButton(
            position: new Vector2(0.311f, 0.302f),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.14f, 0.032f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "What your shop sells: the hulls you keep on sale, and what is on the shelf "
                     + "right now — including stock no line of yours asked for.",
            text: new StringBuilder("Store"),
            textScale: 0.7f)
        {
            Name = "AssetsOpenStore",
            Enabled = false,
        };
        page.Controls.Add(openStore);

        // Third opener alongside the keybind: the market is faction-wide like the order list, takes
        // no selection, and a player who has not bound a key would otherwise never find it.
        // Half-width, sharing its row with Campaigns: a third full-width row at 0.378 lands below
        // the page's clickable area and draws as a button nothing can press.
        var openMarket = new MyGuiControlButton(
            position: new Vector2(0.166f, 0.340f),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.14f, 0.032f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Prices at every station your faction knows, and the trades they imply.",
            text: new StringBuilder("Market"),
            textScale: 0.7f)
        {
            Name = "AssetsOpenMarket",
            Enabled = false,
        };
        page.Controls.Add(openMarket);

        // Fourth opener: the campaigns the faction is fighting, and Cancel for the player's own.
        // Placing one happens on the map, where the target is. Right half of the Market row.
        var openCampaigns = new MyGuiControlButton(
            position: new Vector2(0.311f, 0.340f),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.14f, 0.032f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Your faction's raid and conquest campaigns. Place one from the map; cancel yours here.",
            text: new StringBuilder("Campaigns"),
            textScale: 0.7f)
        {
            Name = "AssetsOpenCampaigns",
            Enabled = false,
        };
        page.Controls.Add(openCampaigns);

        var status = new MyGuiControlLabel(
            new Vector2(-0.452f, 0.302f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            Name = "labelAssetsStatus",
        };
        page.Controls.Add(status);

        var instance = new AssetsPage(tabs, table, detail, heading, status, openNode, openFleet, openStore,
                                      openMarket, openCampaigns, openDemand);
        openNode.ButtonClicked += _ => instance.OpenSelectedNode();
        openFleet.ButtonClicked += _ => Fleet.FleetDoctrineScreen.Open();
        openStore.ButtonClicked += _ => Store.ShelfScreen.Open();
        openMarket.ButtonClicked += _ => Market.MarketScreen.Open();
        openCampaigns.ButtonClicked += _ => Campaigns.CampaignsScreen.Open();
        openDemand.ButtonClicked += _ => Demand.FactionDemandScreen.Open();
        table.ItemSelected += instance.OnRowSelected;
        table.ItemDoubleClicked += instance.OnRowDoubleClicked;
        filter.ItemSelected += () => instance.OnFilterChanged(filter);

        // An eighth page has to make every button narrower, not only carve room for its own.
        //
        // The control steps along the row by TabButtonSize.X, which is
        // min(Size.X / PagesCount, textureWidth * scale) -- but MyGuiCompositeTexture.Draw clamps
        // what it actually paints back UP to textureWidth * scale. Once Size.X / PagesCount falls
        // below that, every button is painted wider than its own step and the row overlaps itself.
        // Seven pages to eight crosses exactly that line at SE's own 0.75.
        //
        // Shrinking the texture is the lever vanilla itself pulls, dropping 0.875 to 0.75 when GPS
        // takes the row from six pages to seven. Scaling by the page-count ratio is provably enough:
        // the row fitted at pagesBefore, so
        //   textureWidth * scale * pagesBefore / pagesAfter
        //     <= (Size.X / pagesBefore) * pagesBefore / pagesAfter
        //      = Size.X / pagesAfter.
        // Assigning also runs the setter's RefreshInternals, which re-lays out the row.
        int pagesAfter = tabs.PagesCount;
        if (pagesAfter > pagesBefore && pagesBefore > 0)
        {
            tabs.TabButtonScale = scaleBefore * pagesBefore / pagesAfter;
        }

        instance.Rebuild();
        Active = instance;
    }

    // ---- Per-frame -----------------------------------------------------------------------

    /// <summary>
    /// Driven from <see cref="Plugin.Update"/>, which Pulsar calls every frame. Owns the
    /// subscription: entering the tab starts the stream, leaving it or closing the terminal stops
    /// it. Deliberately not a Harmony patch on the screen's close — the terminal being shut is
    /// observable without one, and every patch on that screen is a way to break the K menu.
    /// </summary>
    internal static void Tick(bool terminalOpen)
    {
        AssetsPage page = Active;
        AssetsComponent assets = AssetsComponent.Instance;
        if (assets == null) return;

        if (page == null || !terminalOpen)
        {
            Active = null;
            assets.SetStreaming(false);
            return;
        }

        if (!page.IsSelected)
        {
            assets.SetStreaming(false);
            return;
        }

        // Called every frame on purpose: a tab entered before the uplink was granted starts
        // streaming the moment it is, rather than staying empty until the player leaves and
        // returns.
        assets.SetStreaming(true);
        page.Draw(assets);
    }

    private void Draw(AssetsComponent assets)
    {
        if (_drawnRevision != assets.World.Revision)
        {
            Rebuild();
            return;   // Rebuild already wrote fresh numbers and status
        }

        double now = NowSeconds();
        if (now < _nextCountdownRefreshSeconds) return;
        _nextCountdownRefreshSeconds = now + CountdownRefreshSeconds;
        RefreshCountdowns();
        RefreshStatus(assets);
    }

    // ---- Table ---------------------------------------------------------------------------

    private void Rebuild()
    {
        AssetsComponent assets = AssetsComponent.Instance;
        if (assets == null || _table == null) return;

        AssetsWorld world = assets.World;
        _drawnRevision = world.Revision;

        _table.Clear();

        Vector3D here = AssetsComponent.LocalPlayerPosition();

        if (_filter == FilterAll || _filter == CategoryStation)
        {
            foreach (AssetsWorld.AssetNode node in world.Nodes) AddNodeRow(node, here);
        }
        if (_filter == FilterAll || _filter == CategoryFleet)
        {
            foreach (AssetsWorld.AssetFleet fleet in world.Fleets) AddFleetRow(fleet, here);
        }
        if (_filter == FilterAll || _filter == CategoryGrid)
        {
            foreach (AssetsWorld.AssetGrid grid in world.Grids) AddGridRow(grid, here);
        }

        RestoreSelection();
        RefreshStatus(assets);
        _nextCountdownRefreshSeconds = NowSeconds() + CountdownRefreshSeconds;
    }

    private void AddNodeRow(AssetsWorld.AssetNode node, Vector3D here)
    {
        var row = new MyGuiControlTable.Row(new RowRef(CategoryStation, node, "n:" + node.Id));
        row.AddCell(new MyGuiControlTable.Cell(node.DisplayName, null, node.Id));
        row.AddCell(new MyGuiControlTable.Cell(NodeLabels.Describe(
            node.DominantOre, node.Surveyed, node.CapabilityNames, node.HasStation)));
        row.AddCell(new MyGuiControlTable.Cell(
            (node.HasStation ? "Hold " + Percent(node.HoldStrength) : "Claimed, not founded")
            + (node.ShipsInRepair > 0 ? ", " + node.ShipsInRepair + " in repair" : ""),
            null,
            node.ShipsInRepair > 0
                ? node.ShipsInRepair + " damaged hull(s) waiting here for repair. Open station, then Builds..."
                : null));
        row.AddCell(new MyGuiControlTable.Cell(Distance(here, node.PositionM)));
        _table.Add(row);
    }

    private void AddFleetRow(AssetsWorld.AssetFleet fleet, Vector3D here)
    {
        string name = string.IsNullOrEmpty(fleet.DestinationName)
            ? FleetKindLabel(fleet.Kind) + " convoy"
            : FleetKindLabel(fleet.Kind) + " -> " + fleet.DestinationName;

        var row = new MyGuiControlTable.Row(new RowRef(CategoryFleet, fleet, "f:" + fleet.Key));
        row.AddCell(new MyGuiControlTable.Cell(name));
        row.AddCell(new MyGuiControlTable.Cell("Convoy"));
        row.AddCell(new MyGuiControlTable.Cell(EtaLabel(fleet.EtaUtc)));
        row.AddCell(new MyGuiControlTable.Cell(Distance(here, fleet.PositionM)));
        _table.Add(row);
    }

    private void AddGridRow(AssetsWorld.AssetGrid grid, Vector3D here)
    {
        var row = new MyGuiControlTable.Row(new RowRef(CategoryGrid, grid, "g:" + grid.EntityId));
        row.AddCell(new MyGuiControlTable.Cell(grid.Name));
        row.AddCell(new MyGuiControlTable.Cell(
            (grid.GridSize == 1 ? "Small " : "Large ") + (grid.IsStatic ? "station" : "ship")));
        row.AddCell(new MyGuiControlTable.Cell(
            grid.OwnedPersonally ? grid.BlockCount + " blocks" : grid.OwnerName));
        row.AddCell(new MyGuiControlTable.Cell(Distance(here, grid.PositionM)));
        _table.Add(row);
    }

    /// <summary>
    /// Rewrites the two columns that move on their own — distance, because the player is flying,
    /// and a convoy's ETA, because it is counting down — without rebuilding the table. Cheaper
    /// than a rebuild, and it keeps the player's selection and scroll position while the numbers
    /// change under them.
    /// </summary>
    private void RefreshCountdowns()
    {
        if (_table == null) return;
        Vector3D here = AssetsComponent.LocalPlayerPosition();
        for (int i = 0; i < _table.RowsCount; i++)
        {
            MyGuiControlTable.Row row = _table.GetRow(i);
            var reference = row.UserData as RowRef;
            if (reference == null) continue;

            MyGuiControlTable.Cell distance = row.GetCell(3);
            if (distance != null && distance.Text != null)
                distance.Text.Clear().Append(Distance(here, reference.Position));

            if (reference.Category != CategoryFleet) continue;
            MyGuiControlTable.Cell eta = row.GetCell(2);
            if (eta != null && eta.Text != null)
                eta.Text.Clear().Append(EtaLabel(((AssetsWorld.AssetFleet)reference.Item).EtaUtc));
        }
    }

    private void RestoreSelection()
    {
        if (string.IsNullOrEmpty(_selectedKey)) { WriteDetail(null); RefreshOpenButton(null); return; }
        for (int i = 0; i < _table.RowsCount; i++)
        {
            var reference = _table.GetRow(i).UserData as RowRef;
            if (reference == null || reference.Key != _selectedKey) continue;
            _table.SelectedRowIndex = i;
            WriteDetail(reference);
            RefreshOpenButton(reference);
            return;
        }
        // The asset is gone — a convoy that arrived, a grid that was ground down. Drop the
        // selection rather than leaving detail describing something that no longer exists.
        _selectedKey = null;
        WriteDetail(null);
        RefreshOpenButton(null);
    }

    /// <summary>Only a station has a window, and only while the server offers one.</summary>
    private void RefreshOpenButton(RowRef reference)
    {
        if (_openNode != null)
        {
            _openNode.Enabled = reference != null
                                && reference.Category == CategoryStation
                                && NodeDetailComponent.Available;
        }
        // Deliberately not gated on a selection: the doctrine belongs to the faction, not to
        // whichever row happens to be highlighted.
        if (_openFleet != null)
            _openFleet.Enabled = Fleet.FleetDoctrineComponent.Available;
        // Same reasoning: the shelf belongs to the faction, not to the highlighted row.
        if (_openStore != null)
            _openStore.Enabled = Store.ShelfComponent.Available;
        // Same reasoning: the market is faction-wide, so it never takes the highlighted row.
        if (_openMarket != null)
            _openMarket.Enabled = Market.MarketComponent.Available;
        if (_openCampaigns != null)
            _openCampaigns.Enabled = Campaigns.CampaignsComponent.Available;
        if (_openDemand != null)
            _openDemand.Enabled = Demand.FactionDemandComponent.Available;
    }

    private void OnFilterChanged(MyGuiControlCombobox filter)
    {
        _filter = (int)filter.GetSelectedKey();
        Rebuild();
    }

    private void OnRowSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        var reference = row != null ? row.UserData as RowRef : null;
        _selectedKey = reference != null ? reference.Key : null;
        WriteDetail(reference);
        RefreshOpenButton(reference);
    }

    /// <summary>Double-click is the second way in, alongside the button — a table row that opens
    /// something is expected to answer a double-click, and the button is what says so.</summary>
    private void OnRowDoubleClicked(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        OpenSelectedNode();
    }

    /// <summary>Opens the selected station's window. Silently does nothing for a convoy or a grid:
    /// both reach here through the double-click, which cannot be armed per row.</summary>
    private void OpenSelectedNode()
    {
        AssetsWorld.AssetNode node = SelectedNode();
        if (node != null) NodeDetailScreen.Open(node.Id);
    }

    private AssetsWorld.AssetNode SelectedNode()
    {
        MyGuiControlTable.Row row = _table != null ? _table.SelectedRow : null;
        var reference = row != null ? row.UserData as RowRef : null;
        return reference != null && reference.Category == CategoryStation
            ? (AssetsWorld.AssetNode)reference.Item
            : null;
    }

    // ---- Detail panel ---------------------------------------------------------------------

    private void WriteDetail(RowRef reference)
    {
        if (_detail == null) return;
        _detail.Clear();
        if (reference == null)
        {
            _detail.AppendText("Select an asset to see its detail.");
            return;
        }

        switch (reference.Category)
        {
            case CategoryStation: WriteNodeDetail((AssetsWorld.AssetNode)reference.Item); break;
            case CategoryFleet: WriteFleetDetail((AssetsWorld.AssetFleet)reference.Item); break;
            case CategoryGrid: WriteGridDetail((AssetsWorld.AssetGrid)reference.Item); break;
        }
    }

    private void WriteNodeDetail(AssetsWorld.AssetNode node)
    {
        _detail.AppendText(node.DisplayName);
        _detail.AppendLine();
        _detail.AppendText(node.Id);
        _detail.AppendLine();
        _detail.AppendLine();
        _detail.AppendText(NodeLabels.Describe(
            node.DominantOre, node.Surveyed, node.CapabilityNames, node.HasStation)
            + " - " + ZoneLabel(node.Zone));
        _detail.AppendLine();

        if (!node.HasStation)
        {
            _detail.AppendText("Claimed, not yet founded.");
            _detail.AppendLine();
            if (node.FoundCost > 0)
            {
                _detail.AppendText("Founding: " + node.FoundCost + " credits");
                _detail.AppendLine();
            }
        }

        _detail.AppendText("Hold: " + Percent(node.HoldStrength));
        _detail.AppendLine();
        _detail.AppendText("Worth: " + Percent(node.ResourceValue));
        _detail.AppendLine();
        _detail.AppendText("Ore density: " + Percent(node.Richness));
        _detail.AppendLine();

        if (node.Capabilities.Length > 0)
        {
            _detail.AppendLine();
            _detail.AppendText("Installed:");
            _detail.AppendLine();
            foreach (WireAssetCapability cap in node.Capabilities)
            {
                _detail.AppendText("  " + cap.Cap + (cap.Level > 1 ? " x" + cap.Level : ""));
                _detail.AppendLine();
            }
        }

        if (node.ShipsReady > 0 || node.BuildsQueued > 0 || node.ShipsInRepair > 0 || node.ShipyardPaused)
        {
            _detail.AppendLine();
            _detail.AppendText("Shipyard:");
            _detail.AppendLine();
            _detail.AppendText("  " + node.ShipsReady + " ready, " + node.BuildsQueued + " building");
            _detail.AppendLine();
            if (node.ShipsInRepair > 0)
            {
                _detail.AppendText("  " + node.ShipsInRepair + " in repair");
                _detail.AppendLine();
            }
            if (node.ShipyardPaused)
            {
                _detail.AppendText("  paused");
                _detail.AppendLine();
            }
        }
    }

    private void WriteFleetDetail(AssetsWorld.AssetFleet fleet)
    {
        _detail.AppendText(FleetKindLabel(fleet.Kind) + " convoy");
        _detail.AppendLine();
        _detail.AppendLine();
        // The mark itself, not a name for it: this pane is where a player decodes what they saw
        // over the marker on the map, so it has to show the same thing the map drew.
        string echelon = FleetEchelon.Text(fleet.ConvoySize);
        _detail.AppendText("Ships: " + Math.Max(1, fleet.ConvoySize)
                           + (echelon.Length > 0 ? "   " + echelon : ""));
        _detail.AppendLine();
        if (!string.IsNullOrEmpty(fleet.OriginName))
        {
            _detail.AppendText("From: " + fleet.OriginName);
            _detail.AppendLine();
        }
        if (!string.IsNullOrEmpty(fleet.DestinationName))
        {
            _detail.AppendText("To: " + fleet.DestinationName);
            _detail.AppendLine();
        }
        _detail.AppendText("Arrives: " + EtaLabel(fleet.EtaUtc));
        _detail.AppendLine();
        _detail.AppendText("Remaining: " + Distance(fleet.PositionM, fleet.TargetM));
        _detail.AppendLine();
        if (fleet.EntityId == 0)
        {
            _detail.AppendLine();
            _detail.AppendText("Not yet in the world - it materialises when a player is close "
                               + "enough to see it.");
            _detail.AppendLine();
        }
    }

    private void WriteGridDetail(AssetsWorld.AssetGrid grid)
    {
        _detail.AppendText(grid.Name);
        _detail.AppendLine();
        _detail.AppendLine();
        _detail.AppendText((grid.GridSize == 1 ? "Small grid " : "Large grid ")
                           + (grid.IsStatic ? "station" : "ship"));
        _detail.AppendLine();
        _detail.AppendText("Blocks: " + grid.BlockCount);
        _detail.AppendLine();
        _detail.AppendText("Owner: " + (grid.OwnedPersonally ? "you" : grid.OwnerName));
        _detail.AppendLine();
    }

    // ---- Heading and status ----------------------------------------------------------------

    /// <summary>The faction is only known once a snapshot has arrived, so the heading is written
    /// alongside the status line rather than at construction.</summary>
    private void RefreshHeading(AssetsWorld world)
    {
        if (_heading == null) return;
        _heading.Text = string.IsNullOrEmpty(world.FactionTag)
            ? "Your assets"
            : "[" + world.FactionTag + "] " + world.FactionName;
    }

    private void RefreshStatus(AssetsComponent assets)
    {
        RefreshHeading(assets.World);
        if (_status == null) return;

        if (!AssetsComponent.Available)
        {
            _status.Text = "Not linked to a Factions server.";
            return;
        }
        if (!assets.World.HasSnapshot)
        {
            _status.Text = "Waiting for the server...";
            return;
        }

        AssetsWorld world = assets.World;
        int age = (int)Math.Max(0, (DateTime.UtcNow - world.LastSnapshotUtc).TotalSeconds);
        int inRepair = 0;
        foreach (AssetsWorld.AssetNode node in world.Nodes) inRepair += node.ShipsInRepair;
        _status.Text = world.Nodes.Count + " station(s), "
                       + world.Fleets.Count + " convoy(s), "
                       + world.Grids.Count + " grid(s)"
                       + (inRepair > 0 ? ", " + inRepair + " hull(s) in repair" : "")
                       + "   -   updated " + age + "s ago";
    }

    // ---- Formatting ------------------------------------------------------------------------

    private sealed class RowRef
    {
        public readonly int Category;
        public readonly object Item;
        /// <summary>Stable across rebuilds, so a snapshot arriving while the player reads a row
        /// does not move the selection.</summary>
        public readonly string Key;

        public RowRef(int category, object item, string key)
        {
            Category = category;
            Item = item;
            Key = key;
        }

        public Vector3D Position
        {
            get
            {
                switch (Category)
                {
                    case CategoryStation: return ((AssetsWorld.AssetNode)Item).PositionM;
                    case CategoryFleet: return ((AssetsWorld.AssetFleet)Item).PositionM;
                    default: return ((AssetsWorld.AssetGrid)Item).PositionM;
                }
            }
        }
    }

    private static string Percent(double fraction)
    {
        return (fraction * 100.0).ToString("0") + "%";
    }

    private static string Distance(Vector3D from, Vector3D to)
    {
        double metres = Vector3D.Distance(from, to);
        if (metres < 1000.0) return metres.ToString("0") + " m";
        double km = metres / 1000.0;
        if (km < 1000.0) return km.ToString("0.0") + " km";
        return (km / 1000.0).ToString("0.0") + " Mm";
    }

    private static string EtaLabel(DateTime etaUtc)
    {
        TimeSpan remaining = etaUtc - DateTime.UtcNow;
        if (remaining.TotalSeconds <= 0) return "arriving";
        if (remaining.TotalMinutes < 1) return (int)remaining.TotalSeconds + "s";
        if (remaining.TotalHours < 1) return (int)remaining.TotalMinutes + "m";
        return (int)remaining.TotalHours + "h " + remaining.Minutes + "m";
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

    /// <summary>Deferred to <see cref="FleetIcons"/>, which the map reads for the same convoy.
    /// This held a private copy of the same ordinal switch, two kinds short of it: a table naming
    /// a Reinforce "Fleet" while the marker beside it drew a guard is exactly the disagreement
    /// <see cref="NodeLabels"/> exists to prevent, in the other half of the same screen.</summary>
    private static string FleetKindLabel(byte kind)
    {
        return FleetIcons.Label(kind);
    }

    /// <summary>Wall clock, matching the scanner and map: presentation timing must not stretch or
    /// freeze with sim speed or a paused session.</summary>
    private static double NowSeconds()
    {
        return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
    }
}
