using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Wire;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace FactionsUI;

/// <summary>
/// The hull chooser shared by the Build dialog and the New doctrine line dialog: a text filter, a
/// table of every hull the server offered with the numbers the choice is made on. The player picks a
/// hull and nothing else: a request names the hull, and what it answers is read off the hull
/// server-side, so the Roles column says what it fits rather than asking which one.
///
/// <para>A table rather than a role combo and a hull combo, because the two-combo shape made the
/// player commit to a hull before seeing whether it met their needs, one hull at a time. Here every
/// candidate is on screen at once and any column sorts on a header click, so "the biggest hold that
/// can leave this yard" is one click and a glance.</para>
///
/// <para>Not a control: it adds its controls to the owning screen's collection, and the screen
/// places what comes after it from <see cref="Bottom"/>.</para>
/// </summary>
internal sealed class HullPicker
{
    private const int Columns = 9;

    private readonly MyGuiControlTextbox _filter;
    private readonly MyGuiControlTable _table;

    /// <summary>What the last rebuild drew: the hulls, the roles offered and the filter. A server
    /// snapshot that changes none of them leaves the table — and the player's scroll — alone.</summary>
    private string _drawn;

    private readonly List<WireBuildPrefab> _hulls = new List<WireBuildPrefab>();
    private readonly List<string> _offeredRoles = new List<string>();

    /// <summary>Held across rebuilds and sorts, both of which drop the table's own selection.</summary>
    private string _selectedId;

    /// <summary>Raised when the selected hull changes.</summary>
    internal event Action SelectionChanged;

    /// <summary>The Y just below the table, for whatever the owning screen places next.</summary>
    internal float Bottom { get; private set; }

    internal HullPicker(MyGuiControls controls, Vector2 topLeft, float width, int visibleRows, string name)
    {
        AddLabel(controls, new Vector2(topLeft.X, topLeft.Y + 0.008f), "Filter");
        _filter = new MyGuiControlTextbox(defaultText: "", maxLength: 40)
        {
            Position = new Vector2(topLeft.X + 0.08f, topLeft.Y),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Size = new Vector2(0.3f, 0.04f),
            Name = name + "Filter",
        };
        _filter.SetToolTip("Show only hulls whose name, role, size or thrust contains this text.");
        _filter.TextChanged += _ => Rebuild();
        controls.Add(_filter);

        // VisibleRowsCount, not Size.Y, decides the table's height: RefreshInternals overwrites
        // Size.Y, so the row below is placed from the size read back after construction.
        float tableTop = topLeft.Y + 0.055f;
        _table = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(topLeft.X, tableTop),
            Size = new Vector2(width, 0f),
            Name = name + "Hulls",
            ColumnsCount = Columns,
            VisibleRowsCount = visibleRows,
        };
        _table.SetCustomColumnWidths(new float[Columns]
            { 0.25f, 0.16f, 0.06f, 0.13f, 0.10f, 0.07f, 0.07f, 0.09f, 0.07f });
        string[] titles = { "Hull", "Roles", "Size", "Thrust", "Cargo L", "Dps", "Drill", "BOM", "Flies" };
        for (int i = 0; i < Columns; i++)
        {
            _table.SetColumnName(i, new StringBuilder(titles[i]));
            // Every cell carries its raw value as UserData, so a numeric column sorts as a number
            // rather than as its formatted text.
            _table.SetColumnComparison(i, CompareCells);
        }
        _table.ItemSelected += OnRowSelected;
        // A header click has already sorted, and sorting drops the selection.
        _table.ColumnClicked += (t, c) => Reselect();
        controls.Add(_table);

        Bottom = tableTop + _table.Size.Y + 0.015f;
    }

    private static void AddLabel(MyGuiControls controls, Vector2 position, string text)
    {
        controls.Add(new MyGuiControlLabel(
            position, null, text, null, 0.75f, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP));
    }

    // ---- Content ------------------------------------------------------------------------------

    /// <summary>Replaces the hulls on offer. Only hulls with at least one role in
    /// <paramref name="roles"/> are shown: a hull that cannot be built as anything offered is a
    /// pick the server would refuse.</summary>
    internal void SetHulls(IList<WireBuildPrefab> prefabs, IList<string> roles)
    {
        _hulls.Clear();
        _offeredRoles.Clear();
        if (roles != null)
            foreach (string r in roles)
                if (!string.IsNullOrEmpty(r) && !_offeredRoles.Contains(r)) _offeredRoles.Add(r);
        if (prefabs != null)
            foreach (WireBuildPrefab p in prefabs)
                if (OfferedRoles(p).Count > 0) _hulls.Add(p);
        Rebuild();
    }

    /// <summary>Empties the picker for a state with nothing to pick.</summary>
    internal void Clear()
    {
        _hulls.Clear();
        _offeredRoles.Clear();
        Rebuild();
    }

    internal int HullCount { get { return _hulls.Count; } }

    internal bool TryGetSelected(out WireBuildPrefab prefab)
    {
        MyGuiControlTable.Row row = _table.SelectedRow;
        if (row != null && row.UserData is WireBuildPrefab)
        {
            prefab = (WireBuildPrefab)row.UserData;
            return true;
        }
        prefab = new WireBuildPrefab();
        return false;
    }

    internal bool HasSelection
    {
        get
        {
            WireBuildPrefab unused;
            return TryGetSelected(out unused);
        }
    }

    private void Rebuild()
    {
        string filter = (_filter.Text ?? "").Trim();
        if (!TableRows.Changed(ref _drawn, Signature(filter))) return;

        TableRows.PreservingScroll(_table, () =>
        {
            _table.Clear();
            foreach (WireBuildPrefab p in _hulls)
            {
                List<string> roles = OfferedRoles(p);
                string name = string.IsNullOrEmpty(p.Label) ? p.PrefabId : p.Label;
                string thrust = string.IsNullOrEmpty(p.Propulsion) ? "none" : p.Propulsion;
                string size = (p.GridSize ?? "").Length > 0 ? p.GridSize.Substring(0, 1).ToUpperInvariant() : "?";
                if (!Matches(filter, name, p.PrefabId, string.Join(" ", roles), thrust, p.GridSize)) continue;

                var row = new MyGuiControlTable.Row(p);
                row.AddCell(TextCell(name, name + "  (" + p.PrefabId + ")"));
                row.AddCell(TextCell(string.Join(", ", roles), string.Join(", ", roles)));
                row.AddCell(TextCell(size, p.GridSize ?? ""));
                row.AddCell(TextCell(thrust, thrust));
                row.AddCell(CountCell(p.CargoLiters, " L"));
                row.AddCell(CountCell((long)Math.Round(p.Dps), " dps"));
                row.AddCell(NumberCell(p.DrillYieldScore,
                    p.DrillYieldScore > 0 ? p.DrillYieldScore.ToString("N1", CultureInfo.InvariantCulture) : "-",
                    null));
                row.AddCell(CountCell(p.BomUnits, " units"));
                row.AddCell(new MyGuiControlTable.Cell(
                    p.CanFlyFromHere ? "yes" : "no", p.CanFlyFromHere ? 1.0 : 0.0, null,
                    p.CanFlyFromHere ? (Color?)null : new Color(230, 90, 60)));
                _table.Add(row);
            }
            // The order the player last clicked survives a rebuild. A no-op until a header is
            // clicked, so the server's own order is the default.
            _table.Sort(switchSort: false);
        });
        Reselect();
    }

    /// <summary>Puts the selection back on <see cref="_selectedId"/>. A hull the filter now hides
    /// is not selected — the dialog acts only on what the player can see.</summary>
    private void Reselect()
    {
        MyGuiControlTable.Row match = null;
        for (int i = 0; i < _table.RowsCount; i++)
        {
            MyGuiControlTable.Row row = _table.GetRow(i);
            if (row.UserData is WireBuildPrefab
                && string.Equals(((WireBuildPrefab)row.UserData).PrefabId, _selectedId, StringComparison.Ordinal))
            {
                match = row;
                break;
            }
        }
        if (match != null) _table.SelectedRow = match;
        else _table.SelectedRowIndex = null;
        SelectionChanged?.Invoke();
    }

    private string Signature(string filter)
    {
        var sb = new StringBuilder(filter).Append('|');
        foreach (string r in _offeredRoles) sb.Append(r).Append(',');
        foreach (WireBuildPrefab p in _hulls)
            sb.Append('|').Append(p.PrefabId).Append(';').Append(p.Label)
              .Append(';').Append(p.CargoLiters).Append(';').Append(p.Dps.ToString(CultureInfo.InvariantCulture))
              .Append(';').Append(p.BomUnits).Append(';').Append(p.CanFlyFromHere ? '1' : '0')
              .Append(';').Append(p.Roles == null ? "" : string.Join(",", p.Roles));
        return sb.ToString();
    }

    private List<string> OfferedRoles(WireBuildPrefab prefab)
    {
        var result = new List<string>();
        if (prefab.Roles == null) return result;
        foreach (string r in prefab.Roles)
            if (_offeredRoles.Contains(r) && !result.Contains(r)) result.Add(r);
        return result;
    }

    // ---- Selection ----------------------------------------------------------------------------

    private void OnRowSelected(MyGuiControlTable table, MyGuiControlTable.EventArgs args)
    {
        WireBuildPrefab prefab;
        _selectedId = TryGetSelected(out prefab) ? prefab.PrefabId : null;
        SelectionChanged?.Invoke();
    }

    // ---- Cells --------------------------------------------------------------------------------

    /// <summary>Text plus the same text as its tooltip: a hull name or a thrust list can be wider
    /// than its column, and the table clips rather than wraps.</summary>
    private static MyGuiControlTable.Cell TextCell(string text, string tooltip)
    {
        return new MyGuiControlTable.Cell(text, text, tooltip);
    }

    private static MyGuiControlTable.Cell NumberCell(double value, string text, string tooltip)
    {
        return new MyGuiControlTable.Cell(text, value, tooltip);
    }

    /// <summary>k/M/G past four digits, because the columns hold about six characters; the exact
    /// figure is the tooltip, and the raw value is what the column sorts on.</summary>
    private static MyGuiControlTable.Cell CountCell(long value, string unit)
    {
        return NumberCell(value, Quantities.Count(value), Quantities.Exact(value) + unit);
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
}
