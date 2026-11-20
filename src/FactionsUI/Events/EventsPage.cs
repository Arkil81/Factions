using System;
using System.Text;
using FactionsUI.Nodes;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Events;

/// <summary>
/// The "Events" page of SE's own terminal (K menu): what has happened to the player's faction,
/// newest first. Where the Assets tab answers "what is mine", this answers "what is being done to
/// it" — which is the question a player has to answer before they can decide where to put a turret.
///
/// One instance per terminal screen — the screen rebuilds its tabs every time it opens, and on a
/// resolution change. <see cref="Active"/> is the one the player is currently looking at.
/// </summary>
internal sealed class EventsPage
{
    /// <summary>Tab page key. SE's own pages are the <c>MyTerminalPageEnum</c> ordinals 0-6, and
    /// the Assets tab takes 7; pages are created lazily by key, so 8 appends after them without
    /// disturbing any of them.</summary>
    internal const int PageKey = 8;

    /// <summary>Seconds between refreshes of the "when" column. Nothing else on this page moves on
    /// its own, but an age that only updated with the snapshot would read as wrong rather than
    /// stale.</summary>
    private const double AgeRefreshSeconds = 1.0;

    private const int FilterAll = -1;
    private const int CategoryAttack = 0;
    private const int CategoryTrade = 1;
    private const int CategoryIndustry = 2;
    private const int CategoryLogistics = 3;

    internal static EventsPage Active;

    private readonly MyGuiControlTabControl _tabs;
    private readonly MyGuiControlTable _table;
    private readonly MyGuiControlMultilineText _detail;
    private readonly MyGuiControlLabel _heading;
    private readonly MyGuiControlLabel _status;
    /// <summary>Opens the node an event happened at. The whole point of the tab is to end at a
    /// place worth defending, so the row that names one has to lead there.</summary>
    private readonly MyGuiControlButton _openNode;

    /// <summary>Last history revision drawn, so the table is only rebuilt when the feed actually
    /// changed rather than on every frame the tab is up.</summary>
    private int _drawnRevision = -1;

    private int _filter = FilterAll;

    /// <summary>Identity of the selected row, so a rebuild triggered by a snapshot does not throw
    /// away what the player was reading.</summary>
    private string _selectedKey;

    private double _nextAgeRefreshSeconds;

    private EventsPage(
        MyGuiControlTabControl tabs, MyGuiControlTable table, MyGuiControlMultilineText detail,
        MyGuiControlLabel heading, MyGuiControlLabel status, MyGuiControlButton openNode)
    {
        _tabs = tabs;
        _table = table;
        _detail = detail;
        _heading = heading;
        _status = status;
        _openNode = openNode;
    }

    /// <summary>True while the terminal actually has this page on screen. Nothing is streamed while
    /// the player is looking at Inventory or Control Panel.</summary>
    private bool IsSelected
    {
        get { return _tabs != null && _tabs.SelectedPage == PageKey; }
    }

    // ---- Construction ---------------------------------------------------------------------

    /// <summary>
    /// Adds the page to a terminal's tab control and makes it <see cref="Active"/>. Coordinates
    /// mirror the Assets tab's, which mirror SE's own Factions page, so the three line up when the
    /// player flicks between them.
    /// </summary>
    internal static void Build(MyGuiControlTabControl tabs)
    {
        // Captured before the page exists: the button row has to be re-scaled for the new count,
        // and both terms of that are gone once GetTabSubControl has run.
        int pagesBefore = tabs.PagesCount;
        float scaleBefore = tabs.TabButtonScale;

        MyGuiControlTabPage page = tabs.GetTabSubControl(PageKey);
        page.Name = "PageFactionsEvents";
        page.Text = new StringBuilder("Events");
        page.TextScale = 0.7005405f;   // vanilla tab caption scale

        var separators = new MyGuiControlSeparatorList();
        separators.AddVertical(new Vector2(0.152f, -0.343f), 0.646f, 0.002f);
        page.Controls.Add(separators);

        var heading = new MyGuiControlLabel(
            new Vector2(-0.452f, -0.336f), null, "", null, 0.8f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            Name = "labelEventsHeading",
        };
        page.Controls.Add(heading);

        var filter = new MyGuiControlCombobox
        {
            Position = new Vector2(-0.106f, -0.343f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = new Vector2(0.244f, 0.04f),
            Name = "EventsFilter",
        };
        // Keys are the category constants, plus -1 for "everything", so the selected key IS the
        // filter and no lookup table is needed to turn one into the other.
        filter.AddItem(FilterAll, "All events", null, null, false);
        filter.AddItem(CategoryAttack, "Attacks & losses", null, null, false);
        filter.AddItem(CategoryTrade, "Trade & convoys", null, null, false);
        filter.AddItem(CategoryIndustry, "Industry", null, null, false);
        filter.AddItem(CategoryLogistics, "Logistics", null, null, false);
        filter.SelectItemByKey(FilterAll, false);
        page.Controls.Add(filter);

        var table = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(-0.452f, -0.297f),
            Size = new Vector2(0.59f, 0.15f),
            Name = "EventsTable",
            ColumnsCount = 4,
            VisibleRowsCount = 13,   // matches the Assets tab: the page has room for no more
        };
        table.SetCustomColumnWidths(new float[4] { 0.14f, 0.42f, 0.16f, 0.28f });
        table.SetColumnName(0, new StringBuilder("When"));
        table.SetColumnName(1, new StringBuilder("Event"));
        table.SetColumnName(2, new StringBuilder("With"));
        table.SetColumnName(3, new StringBuilder("Where"));
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
            Name = "EventsDetail",
            CanHaveFocus = true,
        };
        page.Controls.Add(detail);

        var openNode = new MyGuiControlButton(
            position: new Vector2(0.166f, 0.266f),
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(0.285f, 0.032f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: "Open the station this happened at: stock, build queue and shipyard controls.",
            text: new StringBuilder("Open location"),
            textScale: 0.7f)
        {
            Name = "EventsOpenNode",
            Enabled = false,
        };
        page.Controls.Add(openNode);

        var status = new MyGuiControlLabel(
            new Vector2(-0.452f, 0.302f), null, "", null, 0.7f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            Name = "labelEventsStatus",
        };
        page.Controls.Add(status);

        var instance = new EventsPage(tabs, table, detail, heading, status, openNode);
        openNode.ButtonClicked += _ => instance.OpenSelectedNode();
        table.ItemSelected += instance.OnRowSelected;
        table.ItemDoubleClicked += instance.OnRowDoubleClicked;
        filter.ItemSelected += () => instance.OnFilterChanged(filter);

        // A ninth page has to make every button narrower, not only carve room for its own — the same
        // arithmetic the Assets tab does for the eighth, and for the same reason: the row steps by
        // min(Size.X / PagesCount, textureWidth * scale) while MyGuiCompositeTexture.Draw clamps what
        // it paints back UP to textureWidth * scale, so past the crossover every button, vanilla's
        // included, is painted wider than its own step.
        //
        // Expressed as a ratio it composes: whichever of the two tab patches runs first, 7 -> 8 -> 9
        // leaves scale * 7/9. Deliberately NOT re-derived from vanilla's 0.75, which would undo
        // whatever the other patch did.
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
    /// subscription: entering the tab starts the stream, leaving it or closing the terminal stops it.
    /// </summary>
    internal static void Tick(bool terminalOpen)
    {
        EventsPage page = Active;
        EventsComponent events = EventsComponent.Instance;
        if (events == null) return;

        if (page == null || !terminalOpen)
        {
            Active = null;
            events.SetWant(EventsComponent.WantTab, false);
            return;
        }

        if (!page.IsSelected)
        {
            events.SetWant(EventsComponent.WantTab, false);
            return;
        }

        // Called every frame on purpose: a tab entered before the uplink was granted starts
        // streaming the moment it is, rather than staying empty until the player leaves and returns.
        events.SetWant(EventsComponent.WantTab, true);
        page.Draw(events);
    }

    private void Draw(EventsComponent events)
    {
        if (_drawnRevision != events.World.Revision)
        {
            Rebuild();
            return;   // Rebuild already wrote fresh ages and status
        }

        double now = NowSeconds();
        if (now < _nextAgeRefreshSeconds) return;
        _nextAgeRefreshSeconds = now + AgeRefreshSeconds;
        RefreshAges();
        RefreshStatus(events);
    }

    // ---- Table ---------------------------------------------------------------------------

    private void Rebuild()
    {
        EventsComponent events = EventsComponent.Instance;
        if (events == null || _table == null) return;

        EventsWorld world = events.World;
        _drawnRevision = world.Revision;

        _table.Clear();
        foreach (EventsWorld.FactionEventRow row in world.Events)
        {
            if (_filter != FilterAll && Category(row.Kind) != _filter) continue;
            AddRow(row);
        }

        RestoreSelection();
        RefreshStatus(events);
        _nextAgeRefreshSeconds = NowSeconds() + AgeRefreshSeconds;
    }

    private void AddRow(EventsWorld.FactionEventRow e)
    {
        var row = new MyGuiControlTable.Row(e);
        row.AddCell(new MyGuiControlTable.Cell(EventLabels.Age(e.Utc), null, e.Utc.ToString("u")));
        row.AddCell(new MyGuiControlTable.Cell(EventLabels.Headline(e), null, Sentence(e)));
        row.AddCell(new MyGuiControlTable.Cell(e.CounterpartyTag));
        row.AddCell(new MyGuiControlTable.Cell(e.NodeName, null,
                                               string.IsNullOrEmpty(e.NodeId) ? null : e.NodeId));
        _table.Add(row);
    }

    /// <summary>Rewrites the one column that moves on its own. Cheaper than a rebuild, and it keeps
    /// the player's selection and scroll position while the ages tick over.</summary>
    private void RefreshAges()
    {
        if (_table == null) return;
        for (int i = 0; i < _table.RowsCount; i++)
        {
            MyGuiControlTable.Row row = _table.GetRow(i);
            var e = row.UserData as EventsWorld.FactionEventRow;
            if (e == null) continue;

            MyGuiControlTable.Cell when = row.GetCell(0);
            if (when != null && when.Text != null) when.Text.Clear().Append(EventLabels.Age(e.Utc));
        }
    }

    private void RestoreSelection()
    {
        if (string.IsNullOrEmpty(_selectedKey)) { WriteDetail(null); RefreshOpenButton(null); return; }
        for (int i = 0; i < _table.RowsCount; i++)
        {
            var e = _table.GetRow(i).UserData as EventsWorld.FactionEventRow;
            if (e == null || e.Key != _selectedKey) continue;
            _table.SelectedRowIndex = i;
            WriteDetail(e);
            RefreshOpenButton(e);
            return;
        }
        // The event has aged out of the ring, or the filter no longer admits it. Drop the selection
        // rather than leaving detail describing a row that is no longer on screen.
        _selectedKey = null;
        WriteDetail(null);
        RefreshOpenButton(null);
    }

    /// <summary>Only an event with a place can be opened, and only while the server offers the node
    /// window at all.</summary>
    private void RefreshOpenButton(EventsWorld.FactionEventRow e)
    {
        if (_openNode == null) return;
        _openNode.Enabled = e != null && !string.IsNullOrEmpty(e.NodeId) && NodeDetailComponent.Available;
    }

    private void OnFilterChanged(MyGuiControlCombobox filter)
    {
        _filter = (int)filter.GetSelectedKey();
        Rebuild();
    }

    private void OnRowSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        MyGuiControlTable.Row row = table.SelectedRow;
        var e = row != null ? row.UserData as EventsWorld.FactionEventRow : null;
        _selectedKey = e != null ? e.Key : null;
        WriteDetail(e);
        RefreshOpenButton(e);
    }

    private void OnRowDoubleClicked(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        OpenSelectedNode();
    }

    /// <summary>Opens the selected event's node. Silently does nothing for an event with no place —
    /// those reach here through the double-click, which cannot be armed per row.</summary>
    private void OpenSelectedNode()
    {
        MyGuiControlTable.Row row = _table != null ? _table.SelectedRow : null;
        var e = row != null ? row.UserData as EventsWorld.FactionEventRow : null;
        if (e != null && !string.IsNullOrEmpty(e.NodeId)) NodeDetailScreen.Open(e.NodeId);
    }

    // ---- Detail panel ---------------------------------------------------------------------

    private void WriteDetail(EventsWorld.FactionEventRow e)
    {
        if (_detail == null) return;
        _detail.Clear();
        if (e == null)
        {
            _detail.AppendText("Select an event to see its detail.");
            return;
        }

        _detail.AppendText(EventLabels.Headline(e));
        _detail.AppendLine();
        _detail.AppendLine();
        _detail.AppendText(Sentence(e));
        _detail.AppendLine();
        _detail.AppendLine();

        _detail.AppendText("When: " + e.Utc.ToLocalTime().ToString("g") + " (" + EventLabels.Age(e.Utc) + " ago)");
        _detail.AppendLine();

        if (!string.IsNullOrEmpty(e.NodeId))
        {
            _detail.AppendText("Where: " + e.NodeName);
            _detail.AppendLine();
            if (!string.Equals(e.NodeName, e.NodeId, StringComparison.Ordinal))
            {
                _detail.AppendText("       " + e.NodeId);
                _detail.AppendLine();
            }
        }

        if (!string.IsNullOrEmpty(e.CounterpartyTag))
        {
            _detail.AppendText((e.ViewerIsActor ? "Against: " : "By: ") + e.CounterpartyTag);
            _detail.AppendLine();
        }

        string amount = AmountLabel(e);
        if (amount != null)
        {
            _detail.AppendText(amount);
            _detail.AppendLine();
        }

        if (e.Ratio > 0)
        {
            _detail.AppendText("Strength: " + Percent(e.Ratio));
            _detail.AppendLine();
        }
    }

    // ---- Heading and status ----------------------------------------------------------------

    private void RefreshHeading(EventsWorld world)
    {
        if (_heading == null) return;
        _heading.Text = string.IsNullOrEmpty(world.FactionTag)
            ? "Events"
            : "[" + world.FactionTag + "] events";
    }

    private void RefreshStatus(EventsComponent events)
    {
        RefreshHeading(events.World);
        if (_status == null) return;

        if (!EventsComponent.Available)
        {
            _status.Text = "Not linked to a Factions server.";
            return;
        }
        if (!events.World.HasSnapshot)
        {
            _status.Text = "Waiting for the server...";
            return;
        }

        EventsWorld world = events.World;
        if (string.IsNullOrEmpty(world.FactionTag))
        {
            _status.Text = "You are in no faction, so nothing is recorded for you yet.";
            return;
        }
        if (world.Count == 0)
        {
            _status.Text = "Nothing has happened to " + world.FactionTag + " yet.";
            return;
        }

        int age = (int)Math.Max(0, (DateTime.UtcNow - world.LastSnapshotUtc).TotalSeconds);
        _status.Text = world.Count + " event(s)   -   updated " + age + "s ago";
    }

    // ---- Formatting ------------------------------------------------------------------------

    /// <summary>Which filter bucket a kind belongs to. Anything unrecognised falls in with attacks:
    /// a newer server's event is more likely to matter than not, and burying it is worse than
    /// showing it in the wrong list.</summary>
    private static int Category(EventsWorld.EventKind kind)
    {
        switch (kind)
        {
            case EventsWorld.EventKind.TradeResolved:
            case EventsWorld.EventKind.GiftResolved:
            case EventsWorld.EventKind.ConvoyEscaped:
            case EventsWorld.EventKind.MineResolved:
                return CategoryTrade;
            case EventsWorld.EventKind.ShipBuilt:
            case EventsWorld.EventKind.BuildQueued:
            case EventsWorld.EventKind.BuildExpired:
            case EventsWorld.EventKind.BuildCancelled:
            case EventsWorld.EventKind.BuildOverridden:
                return CategoryIndustry;
            case EventsWorld.EventKind.HaulResolved:
            case EventsWorld.EventKind.ConvoyDispatched:
                return CategoryLogistics;
            default:
                return CategoryAttack;
        }
    }

    /// <summary>The long form, for the tooltip and the detail panel.</summary>
    private static string Sentence(EventsWorld.FactionEventRow e)
    {
        string other = string.IsNullOrEmpty(e.CounterpartyTag) ? "someone" : e.CounterpartyTag;
        string place = string.IsNullOrEmpty(e.NodeName) ? "in open space" : "at " + e.NodeName;

        switch (e.Kind)
        {
            case EventsWorld.EventKind.RaidEngaged:
            {
                // Amount is the dwell length in seconds -- the only event that reports a window
                // still open, so the sentence says how long there is rather than what happened.
                string window = e.Amount >= 120
                    ? (e.Amount / 60) + " minutes"
                    : e.Amount + " seconds";
                return e.ViewerIsActor
                    ? "Our raid has engaged " + other + " " + place + "."
                    : other + " is attacking " + place.Replace("at ", "") + " right now — about "
                      + window + " to answer it.";
            }
            case EventsWorld.EventKind.RaidResolved:
                return e.ViewerIsActor
                    ? "Our raid struck " + other + " " + place + "."
                    : other + " raided us " + place + ".";
            case EventsWorld.EventKind.RaidRepelled:
            {
                if (EventLabels.RaidDestroyed(e))
                    return e.ViewerIsActor
                        ? "Our raid was destroyed by " + other + " " + place + "."
                        : "We destroyed a raid by " + other + " " + place + ".";
                string hulls = e.Amount == 1 ? "1 hull" : e.Amount + " hulls";
                return e.ViewerIsActor
                    ? "Our raid was repelled by " + other + " " + place
                      + (e.Amount > 0 ? "; we lost " + hulls + "." : ".")
                    : "We repelled a raid by " + other + " " + place
                      + (e.Amount > 0 ? ", destroying " + hulls + "." : ".");
            }
            case EventsWorld.EventKind.NodeConquered:
                return e.ViewerIsActor
                    ? "We took " + place.Replace("at ", "") + " from " + other + "."
                    : other + " took " + place.Replace("at ", "") + " from us.";
            case EventsWorld.EventKind.NodeLost:
                return "Hold decayed to nothing " + place + ". Nobody attacked it; it was simply "
                       + "not maintained.";
            case EventsWorld.EventKind.ConvoyLooted:
                return e.ViewerIsActor
                    ? "We intercepted a convoy of " + other + " " + place + "."
                    : other + " intercepted one of our convoys " + place + ".";
            case EventsWorld.EventKind.ConvoyEscaped:
                return e.ViewerIsActor
                    ? "A convoy of " + other + " evaded our raider " + place + "."
                    : "One of our convoys evaded " + other + " " + place + ".";
            case EventsWorld.EventKind.TruceBreach:
                return e.ViewerIsActor
                    ? "We attacked " + other + " while under truce."
                    : other + " attacked us while under truce.";
            case EventsWorld.EventKind.ShipLost:
                return "A convoy was destroyed in transit " + place + ".";
            case EventsWorld.EventKind.TradeResolved:
                return e.ViewerIsActor
                    ? "Our trade convoy settled with " + other + " " + place + "."
                    : other + " delivered a trade to us " + place + ".";
            case EventsWorld.EventKind.GiftResolved:
                return e.ViewerIsActor
                    ? "We sent a gift to " + other + "."
                    : other + " sent us a gift.";
            case EventsWorld.EventKind.ShipBuilt:
                return "A shipyard finished a hull " + place + ".";
            case EventsWorld.EventKind.MineResolved:
                return "A mining run delivered its load " + place + ".";
            case EventsWorld.EventKind.FleetDispatched:
                return e.ViewerIsActor
                    ? "We sent an attack towards " + other + " " + place + "."
                    : other + " has sent an attack towards " + place.Replace("at ", "") + ".";
            case EventsWorld.EventKind.HaulResolved:
                return "A haul unloaded " + place + ".";
            case EventsWorld.EventKind.ConvoyDispatched:
                return "A convoy left for " + place.Replace("at ", "")
                       + (string.IsNullOrEmpty(e.CounterpartyTag) ? "." : ", bound for " + other + ".");
            case EventsWorld.EventKind.BuildQueued:
                return "A hull was put on the shipyard queue " + place + ".";
            case EventsWorld.EventKind.BuildExpired:
                return "A queued build was abandoned " + place + " without finishing.";
            case EventsWorld.EventKind.BuildCancelled:
                return "A queued build was cancelled " + place + " and its components refunded.";
            case EventsWorld.EventKind.BuildOverridden:
                return "A queued build " + place + " was replaced by another for the same role; "
                       + "the old one was refunded.";
            default:
                return "This build does not recognise this event. The server is newer than the "
                       + "client.";
        }
    }

    /// <summary>Null when the kind carries no quantity worth a line of its own.</summary>
    private static string AmountLabel(EventsWorld.FactionEventRow e)
    {
        if (e.Amount == 0) return null;
        switch (e.Kind)
        {
            case EventsWorld.EventKind.RaidResolved:
            case EventsWorld.EventKind.ConvoyLooted:
                return "Cargo taken: " + e.Amount;
            case EventsWorld.EventKind.TradeResolved:
                return "Settled: " + e.Amount;
            case EventsWorld.EventKind.GiftResolved:
                return "Value: " + e.Amount;
            case EventsWorld.EventKind.ShipLost:
                return "Ships: " + e.Amount;
            case EventsWorld.EventKind.MineResolved:
            case EventsWorld.EventKind.HaulResolved:
                return "Delivered: " + e.Amount;
            default:
                return null;
        }
    }

    private static string Percent(double fraction)
    {
        return (fraction * 100.0).ToString("0") + "%";
    }

    private static double NowSeconds()
    {
        return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
    }
}
