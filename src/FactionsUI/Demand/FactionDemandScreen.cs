using System;
using System.Collections.Generic;
using System.Text;
using Factions.Wire;
using Sandbox;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI.Demand;

/// <summary>
/// The faction Demand window: everything the player's faction is asking for — every live
/// <c>Item:</c> and <c>Ship:</c> demand, pinned to a station or faction-wide — and what ended in the
/// server's retention window. The station window's Demand tab shows one station's rows; this is the
/// whole faction, with an At column and a filter to narrow it to one station or to the faction-wide
/// rows no station tab can show.
///
/// Same chrome contract as <see cref="Campaigns.CampaignsScreen"/>: opened from the Assets tab, draws
/// over its opener, data from <see cref="FactionDemandComponent"/>'s own subscription.
/// </summary>
internal sealed class FactionDemandScreen : MyGuiScreenBase
{
    private const int OpenColumns = 8;
    private const int EndedColumns = 7;
    private const float EdgeGap = 0.02f;
    private const long FilterAll = -1;
    private const long FilterFactionWide = -2;
    private static readonly Vector2 WindowSize = new Vector2(0.9f, 0.86f);
    private static readonly Color Warn = new Color(255, 120, 100);
    private static readonly Color Muted = new Color(150, 160, 170);
    private static readonly Color Produced = new Color(46, 204, 113);

    internal static FactionDemandScreen Instance { get; private set; }

    private MyGuiControlLabel _heading;
    private MyGuiControlCombobox _filter;
    private MyGuiControlTable _open;
    private MyGuiControlLabel _endedCaption;
    private MyGuiControlTable _ended;
    private MyGuiControlLabel _note;

    private int _drawnRevision = -1;
    private string _drawnOpen;
    private string _drawnEnded;
    private string _drawnStations;

    /// <summary>The station the filter names, by id, so a snapshot that adds or drops a station
    /// does not move the selection onto a different one. Null with <see cref="_filterKey"/> on
    /// All or Faction-wide.</summary>
    private string _filterNodeId;
    private long _filterKey = FilterAll;
    /// <summary>Combobox key -> station id, rebuilt with the station list.</summary>
    private readonly List<string> _stationIds = new List<string>();

    private FactionDemandScreen()
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
            MyGuiSandbox.AddScreen(new FactionDemandScreen());
        }
        catch (Exception ex)
        {
            Plugin.Log("faction demand window open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.FactionDemandScreen";
    }

    public override void LoadContent()
    {
        base.LoadContent();
        Instance = this;
        FactionDemandComponent component = FactionDemandComponent.Instance;
        if (component != null) component.SetStreaming(true);
    }

    public override void OnRemoved()
    {
        if (Instance != this) { base.OnRemoved(); return; }
        FactionDemandComponent component = FactionDemandComponent.Instance;
        if (component != null) component.SetStreaming(false);
        Instance = null;
        base.OnRemoved();
    }

    // ---- Chrome -------------------------------------------------------------------------------

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Demand");

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.02f;
        float right = -topLeft.X - 0.02f;
        float top = topLeft.Y + 0.09f;

        _heading = new MyGuiControlLabel(
            new Vector2(left, top + 0.008f), null, "", null, 0.9f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_heading);

        _filter = new MyGuiControlCombobox
        {
            Position = new Vector2(right - 0.28f, top),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = new Vector2(0.28f, 0.04f),
            Name = "FactionDemandFilter",
        };
        _filter.ItemSelected += OnFilterChanged;
        Controls.Add(_filter);
        _stationIds.Clear();
        _drawnStations = null;
        FillFilter(new List<WireNamedNode>());

        // VisibleRowsCount decides the real height; everything below a table is placed from its
        // measured size, as in the station window.
        _open = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, top + 0.055f),
            Size = new Vector2(right - left, 0f),
            Name = "FactionDemandOpen",
            ColumnsCount = OpenColumns,
            VisibleRowsCount = 8,
        };
        _open.SetCustomColumnWidths(new float[OpenColumns] { 0.24f, 0.16f, 0.09f, 0.09f, 0.09f, 0.15f, 0.16f, EdgeGap });
        _open.SetColumnName(0, new StringBuilder("Wanted"));
        _open.SetColumnName(1, new StringBuilder("At"));
        _open.SetColumnName(2, new StringBuilder("Qty"));
        _open.SetColumnName(3, new StringBuilder("Have"));
        _open.SetColumnName(4, new StringBuilder("Used /h"));
        _open.SetColumnName(5, new StringBuilder("For"));
        _open.SetColumnName(6, new StringBuilder("Open for"));
        RightAlign(_open, 2);
        RightAlign(_open, 3);
        RightAlign(_open, 4);
        RightAlign(_open, 6);
        Controls.Add(_open);

        _endedCaption = new MyGuiControlLabel(
            new Vector2(left, _open.Position.Y + _open.Size.Y + 0.008f), null,
            "", Muted.ToVector4(), 0.62f, "White", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_endedCaption);

        _ended = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, _endedCaption.Position.Y + 0.028f),
            Size = new Vector2(right - left, 0f),
            Name = "FactionDemandEnded",
            ColumnsCount = EndedColumns,
            VisibleRowsCount = 5,
        };
        _ended.SetCustomColumnWidths(new float[EndedColumns] { 0.24f, 0.16f, 0.09f, 0.15f, 0.12f, 0.22f, EdgeGap });
        _ended.SetColumnName(0, new StringBuilder("Wanted then"));
        _ended.SetColumnName(1, new StringBuilder("At"));
        _ended.SetColumnName(2, new StringBuilder("Peak"));
        _ended.SetColumnName(3, new StringBuilder("For"));
        _ended.SetColumnName(4, new StringBuilder("Was open"));
        _ended.SetColumnName(5, new StringBuilder("Ended"));
        RightAlign(_ended, 2);
        RightAlign(_ended, 4);
        RightAlign(_ended, 5);
        Controls.Add(_ended);

        _note = new MyGuiControlLabel(
            new Vector2(left, _ended.Position.Y + _ended.Size.Y + 0.008f), null,
            "", Muted.ToVector4(), 0.62f, "White", MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(_note);

        _drawnRevision = -1;
        _drawnOpen = null;
        _drawnEnded = null;
    }

    private static void RightAlign(MyGuiControlTable table, int column)
    {
        table.SetColumnAlign(column, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
        table.SetHeaderColumnAlign(column, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
        table.SetHeaderColumnMargin(column, new Thickness(0f));
    }

    // ---- Frame --------------------------------------------------------------------------------

    /// <summary>Redrawn on a new snapshot and, between snapshots, once a second, so the ages keep
    /// moving on a quiet faction whose snapshot does not change.</summary>
    private DateTime _nextAgeRefreshUtc;

    public override bool Draw()
    {
        FactionDemandComponent component = FactionDemandComponent.Instance;
        if (component != null)
        {
            component.SetStreaming(true);
            DateTime now = DateTime.UtcNow;
            if (_drawnRevision != component.Revision || now >= _nextAgeRefreshUtc)
            {
                _nextAgeRefreshUtc = now.AddSeconds(1);
                Rebuild(component);
            }
        }
        return base.Draw();
    }

    private void Rebuild(FactionDemandComponent component)
    {
        _drawnRevision = component.Revision;
        if (!FactionDemandComponent.Available)
        {
            WriteUnavailable("This server does not offer the Demand window to the client.");
            return;
        }
        if (!component.HasSnapshot)
        {
            WriteUnavailable("Waiting for the server...");
            return;
        }
        WireFactionDemand s = component.Snapshot;
        if (string.IsNullOrEmpty(s.FactionTag))
        {
            WriteUnavailable("You are not in a faction Factions has registered.");
            return;
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var stations = new List<WireNamedNode>();
        foreach (WireNamedNode n in s.Nodes ?? new WireNamedNode[0])
        {
            names[n.Id] = string.IsNullOrEmpty(n.Name) ? n.Id : n.Name;
            stations.Add(n);
        }
        FillFilter(stations);

        var open = new List<WireNodeDemand>();
        foreach (WireNodeDemand d in s.Demand ?? new WireNodeDemand[0])
            if (Passes(d.AtNodeId)) open.Add(d);
        var ended = new List<WireNodeEndedDemand>();
        foreach (WireNodeEndedDemand e in s.Ended ?? new WireNodeEndedDemand[0])
            if (Passes(e.AtNodeId)) ended.Add(e);

        int allOpen = s.Demand == null ? 0 : s.Demand.Length;
        _heading.Text = "[" + s.FactionTag + "]  " + allOpen + " open"
                        + (s.DemandTotal > allOpen ? " (of " + s.DemandTotal + ")" : "")
                        + (_filterKey == FilterAll ? "" : ",  " + open.Count + " shown");

        string window = WindowLabel(s.WindowSeconds);
        int allEnded = s.Ended == null ? 0 : s.Ended.Length;
        _endedCaption.Text = ended.Count == 0
            ? "Nothing " + (_filterKey == FilterAll ? "" : "here ") + "ended in the " + window + "."
            : "Ended in the " + window + ", most recent first"
              + (s.EndedTotal > allEnded ? " (newest " + allEnded + " of " + s.EndedTotal + ")" : "") + ":";
        _note.Text = open.Count == 0
            ? "Nothing is wanted" + (_filterKey == FilterAll ? "" : " here") + " now."
            : "Most urgent first. A faction-wide row's Have and Used /h are the sum over all your stations.";

        WriteOpen(open, names);
        WriteEnded(ended, names);
    }

    private bool Passes(string atNodeId)
    {
        if (_filterKey == FilterAll) return true;
        if (_filterKey == FilterFactionWide) return string.IsNullOrEmpty(atNodeId);
        return string.Equals(atNodeId, _filterNodeId, StringComparison.Ordinal);
    }

    private void WriteUnavailable(string message)
    {
        if (_heading != null) _heading.Text = "Demand";
        if (_open != null) _open.Clear();
        if (_ended != null) _ended.Clear();
        _drawnOpen = null;
        _drawnEnded = null;
        if (_endedCaption != null) _endedCaption.Text = "";
        if (_note != null) _note.Text = message;
    }

    // ---- Filter -------------------------------------------------------------------------------

    /// <summary>Re-fills the dropdown only when the station list changed, and keeps the selection
    /// on the same station by id; a station that is gone falls back to All.</summary>
    private void FillFilter(List<WireNamedNode> stations)
    {
        stations.Sort((a, b) => string.Compare(Display(a), Display(b), StringComparison.OrdinalIgnoreCase));
        var signature = new StringBuilder();
        foreach (WireNamedNode n in stations) signature.Append(n.Id).Append('\u001f').Append(n.Name).Append('\u001e');
        if (!TableRows.Changed(ref _drawnStations, signature.ToString())) return;

        _filter.ClearItems();
        _stationIds.Clear();
        _filter.AddItem(FilterAll, "Everything", null, null, false);
        _filter.AddItem(FilterFactionWide, "Faction-wide only", null, null, false);
        for (int i = 0; i < stations.Count; i++)
        {
            _stationIds.Add(stations[i].Id);
            _filter.AddItem(i, Display(stations[i]), null, null, false);
        }

        long key = _filterKey;
        if (key >= 0)
        {
            key = _stationIds.IndexOf(_filterNodeId);
            if (key < 0) { key = FilterAll; _filterNodeId = null; }
        }
        _filterKey = key;
        _filter.SelectItemByKey(key, false);
    }

    private static string Display(WireNamedNode n)
    {
        return string.IsNullOrEmpty(n.Name) ? n.Id : n.Name;
    }

    private void OnFilterChanged()
    {
        long key = _filter.GetSelectedKey();
        _filterKey = key;
        _filterNodeId = key >= 0 && key < _stationIds.Count ? _stationIds[(int)key] : null;
        _drawnRevision = -1;
    }

    // ---- Tables -------------------------------------------------------------------------------

    private void WriteOpen(List<WireNodeDemand> open, Dictionary<string, string> names)
    {
        var ages = new string[open.Count];
        var signature = new StringBuilder();
        for (int i = 0; i < open.Count; i++)
        {
            WireNodeDemand d = open[i];
            ages[i] = Since(d.FirstPostedUtcTicks);
            signature.Append(d.Kind).Append('\u001f').Append(d.AtNodeId).Append('\u001f').Append(d.DestNodeId)
                     .Append('\u001f').Append(d.Quantity).Append('\u001f').Append(d.Have).Append('\u001f')
                     .Append(DemandText.Rate(d)).Append('\u001f').Append(d.Class).Append('\u001f')
                     .Append(d.DestinedForSale ? 'S' : '-').Append('\u001f').Append(ages[i]).Append('\u001e');
        }
        if (!TableRows.Changed(ref _drawnOpen, signature.ToString())) return;
        TableRows.PreservingScroll(_open, () =>
        {
            _open.Clear();
            for (int i = 0; i < open.Count; i++)
            {
                WireNodeDemand d = open[i];
                string key = DemandText.ItemKey(d.Kind);
                var why = (WireDemandClass)d.Class;
                bool factionWide = string.IsNullOrEmpty(d.AtNodeId);
                var row = new MyGuiControlTable.Row(d);
                row.AddCell(new MyGuiControlTable.Cell(DemandText.Label(d.Kind, Name(names, d.DestNodeId)), null,
                    DemandText.Tooltip(d.Kind, d.DestinedForSale)));
                row.AddCell(AtCell(d.AtNodeId, names));
                row.AddCell(new MyGuiControlTable.Cell(d.Quantity > 0 && key != null ? Quantities.Item(key, d.Quantity) : "-", null,
                    d.Quantity > 0 && key != null ? "Wanted: " + Quantities.ItemExact(key, d.Quantity) : null));
                row.AddCell(new MyGuiControlTable.Cell(d.HasHave ? Quantities.Item(key, d.Have) : "", null,
                    d.HasHave ? (factionWide ? "Held across all your stations: " : "Held at the station: ")
                                + Quantities.ItemExact(key, d.Have) : null,
                    d.HasHave && d.Quantity > 0 && d.Have < d.Quantity ? Warn : (Color?)null));
                row.AddCell(new MyGuiControlTable.Cell(DemandText.Rate(d)));
                row.AddCell(new MyGuiControlTable.Cell(DemandText.ClassLabel(why, d.Kind), null,
                    DemandText.ClassTooltip(why, d.Kind)));
                row.AddCell(new MyGuiControlTable.Cell(ages[i], null,
                    d.FirstPostedUtcTicks > 0 ? "First asked for " + ages[i] + " ago." : null));
                row.AddCell(new MyGuiControlTable.Cell(""));
                _open.Add(row);
            }
        });
    }

    private void WriteEnded(List<WireNodeEndedDemand> ended, Dictionary<string, string> names)
    {
        var ages = new string[ended.Count];
        var signature = new StringBuilder();
        for (int i = 0; i < ended.Count; i++)
        {
            WireNodeEndedDemand e = ended[i];
            ages[i] = Ago(e.EndedUtcTicks);
            signature.Append(e.Kind).Append('\u001f').Append(e.AtNodeId).Append('\u001f').Append(e.DestNodeId)
                     .Append('\u001f').Append(e.PeakQuantity).Append('\u001f').Append(e.Class).Append('\u001f')
                     .Append(e.Consumed ? 'M' : 'L').Append('\u001f').Append(e.FirstPostedUtcTicks)
                     .Append('\u001f').Append(ages[i]).Append('\u001e');
        }
        if (!TableRows.Changed(ref _drawnEnded, signature.ToString())) return;
        TableRows.PreservingScroll(_ended, () =>
        {
            _ended.Clear();
            for (int i = 0; i < ended.Count; i++)
            {
                WireNodeEndedDemand e = ended[i];
                string key = DemandText.ItemKey(e.Kind);
                var why = (WireDemandClass)e.Class;
                string wasOpen = e.FirstPostedUtcTicks > 0 && e.EndedUtcTicks > e.FirstPostedUtcTicks
                    ? Duration(new TimeSpan(e.EndedUtcTicks - e.FirstPostedUtcTicks)) : "";
                var row = new MyGuiControlTable.Row(e);
                row.AddCell(new MyGuiControlTable.Cell(DemandText.Label(e.Kind, Name(names, e.DestNodeId)), null,
                    DemandText.Tooltip(e.Kind, e.DestinedForSale)));
                row.AddCell(AtCell(e.AtNodeId, names));
                row.AddCell(new MyGuiControlTable.Cell(e.PeakQuantity > 0 && key != null ? Quantities.Item(key, e.PeakQuantity) : "-", null,
                    e.PeakQuantity > 0 && key != null ? "Largest amount asked for: " + Quantities.ItemExact(key, e.PeakQuantity) : null));
                row.AddCell(new MyGuiControlTable.Cell(DemandText.ClassLabel(why, e.Kind), null,
                    DemandText.ClassTooltip(why, e.Kind)));
                row.AddCell(new MyGuiControlTable.Cell(wasOpen));
                row.AddCell(new MyGuiControlTable.Cell((e.Consumed ? "met " : "lapsed ") + ages[i], null,
                    DemandText.EndedTooltip(e.Consumed), e.Consumed ? Produced : (Color?)null));
                row.AddCell(new MyGuiControlTable.Cell(""));
                _ended.Add(row);
            }
        });
    }

    private static MyGuiControlTable.Cell AtCell(string atNodeId, Dictionary<string, string> names)
    {
        if (string.IsNullOrEmpty(atNodeId))
            return new MyGuiControlTable.Cell("faction-wide", null,
                "Owed at no station in particular: any of your stations' stock can meet it.", Muted);
        return new MyGuiControlTable.Cell(Name(names, atNodeId), null, atNodeId);
    }

    private static string Name(Dictionary<string, string> names, string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return "";
        string name;
        return names.TryGetValue(nodeId, out name) ? name : nodeId;
    }

    // ---- Formatting ---------------------------------------------------------------------------

    private static string WindowLabel(int seconds)
    {
        if (seconds <= 0) return "recent past";
        if (seconds % 60 == 0) return "last " + (seconds / 60) + (seconds == 60 ? " minute" : " minutes");
        return "last " + seconds + " seconds";
    }

    private static string Since(long utcTicks)
    {
        if (utcTicks <= 0) return "";
        TimeSpan age = DateTime.UtcNow - new DateTime(utcTicks, DateTimeKind.Utc);
        return age.TotalSeconds < 1 ? "just now" : Duration(age);
    }

    private static string Ago(long utcTicks)
    {
        if (utcTicks <= 0) return "";
        TimeSpan age = DateTime.UtcNow - new DateTime(utcTicks, DateTimeKind.Utc);
        return age.TotalSeconds < 1 ? "just now" : Duration(age) + " ago";
    }

    private static string Duration(TimeSpan span)
    {
        if (span.TotalHours >= 1.0) return ((int)span.TotalHours) + "h " + span.Minutes + "m";
        if (span.TotalMinutes >= 1.0) return ((int)span.TotalMinutes) + "m " + span.Seconds + "s";
        return ((int)span.TotalSeconds) + "s";
    }
}
