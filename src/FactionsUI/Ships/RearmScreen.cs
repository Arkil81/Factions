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

namespace FactionsUI.Ships;

/// <summary>
/// The Rearm dialog: which of the docked ship's gun groups to load and with which magazine, and how
/// many magazines of each type to put in its cargo as a reserve.
///
/// <para><b>Built from the server's survey, and nothing else.</b> The groups, the magazines each
/// takes, how many fit, what the station sells and at what price are all the server's answer for
/// this ship at this station. The dialog only collects choices; the commit is re-planned by the
/// server against the ship and the stock as they stand, and never charges more than the total shown
/// here when the player pressed Rearm.</para>
///
/// <para><b>Guns before cargo.</b> The estimate spends a thin station's stock on the selected guns
/// first and offers only the remainder as reserve, which is also the order the server fills in.
/// The reserve is bounded by the ship's free cargo volume as the survey measured it.</para>
///
/// <para>A <see cref="MyGuiScreenBase"/> window over the terminal, for the reason
/// <see cref="ShipScanScreen"/> gives: controls injected into SE's generated EmptyKeys view cannot be
/// given binding paths, and this dialog is two tables and a row of buttons.</para>
/// </summary>
internal sealed class RearmScreen : MyGuiScreenBase
{
    private static readonly Vector2 WindowSize = new Vector2(0.80f, 0.80f);
    private const int GroupColumns = 6;
    private const int ReserveColumns = 6;

    internal static RearmScreen Instance { get; private set; }

    /// <summary>Set by the Rearm button just before the survey is asked for, so the caption can
    /// name the ship the player selected. Only a label; the survey names the grid by id.</summary>
    internal static string PendingShipName;

    private sealed class GroupChoice
    {
        public WireRearmGroup Group;
        public bool Load;
        public int Option = -1;

        public bool HasOption => Group.Options != null && Option >= 0 && Option < Group.Options.Length;
        public WireRearmOption Selected => Group.Options[Option];
    }

    private sealed class ReserveChoice
    {
        public WireRearmMagazine Magazine;
        public long Count;
    }

    private readonly long _gridEntityId;
    private readonly long _balance;
    private readonly double _cargoFreeLitres;
    private readonly string _shipName;
    private readonly List<GroupChoice> _groups = new List<GroupChoice>();
    private readonly List<ReserveChoice> _reserves = new List<ReserveChoice>();
    private readonly Dictionary<string, WireRearmMagazine> _magazines =
        new Dictionary<string, WireRearmMagazine>(StringComparer.Ordinal);

    private MyGuiControlTable _groupTable;
    private MyGuiControlTable _reserveTable;
    private MyGuiControlLabel _heading;
    private MyGuiControlLabel _total;
    private MyGuiControlLabel _status;
    private MyGuiControlButton _toggle;
    private MyGuiControlButton _cycle;
    private readonly List<MyGuiControlButton> _steps = new List<MyGuiControlButton>();
    private MyGuiControlButton _rearm;

    private RearmScreen(long gridEntityId, long balance, double cargoFreeLitres,
        WireRearmMagazine[] magazines, WireRearmGroup[] groups, string shipName)
        : base(new Vector2(0.5f, 0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, WindowSize, false, null,
               MySandboxGame.Config.UIBkOpacity, MySandboxGame.Config.UIOpacity)
    {
        _gridEntityId = gridEntityId;
        _balance = balance;
        _cargoFreeLitres = cargoFreeLitres;
        _shipName = string.IsNullOrEmpty(shipName) ? "this ship" : shipName;

        foreach (WireRearmMagazine magazine in magazines ?? new WireRearmMagazine[0])
        {
            if (string.IsNullOrEmpty(magazine.Key)) continue;
            _magazines[magazine.Key] = magazine;
            _reserves.Add(new ReserveChoice { Magazine = magazine });
        }

        foreach (WireRearmGroup group in groups ?? new WireRearmGroup[0])
        {
            var choice = new GroupChoice { Group = group };
            WireRearmOption[] options = group.Options ?? new WireRearmOption[0];
            // The magazine the guns already have selected, when the station sells it; otherwise
            // the first one it does sell.
            for (int i = 0; i < options.Length; i++)
                if (string.Equals(options[i].Key, group.CurrentKey, StringComparison.Ordinal)) choice.Option = i;
            if (choice.Option < 0 && options.Length > 0) choice.Option = 0;
            choice.Load = choice.HasOption && choice.Selected.Fits > 0;
            _groups.Add(choice);
        }

        m_closeOnEsc = true;
        CanBeHidden = true;
        CanHideOthers = false;
        CloseButtonEnabled = true;
        EnabledBackgroundFade = true;
        RecreateControls(constructor: true);
    }

    internal static void Open(long gridEntityId, long balance, double cargoFreeLitres,
        WireRearmMagazine[] magazines, WireRearmGroup[] groups)
    {
        try
        {
            if (Instance != null) Instance.CloseScreen();
            MyGuiSandbox.AddScreen(new RearmScreen(gridEntityId, balance, cargoFreeLitres, magazines, groups,
                PendingShipName));
        }
        catch (Exception ex)
        {
            Plugin.Log("rearm dialog open failed: " + ex);
        }
    }

    public override string GetFriendlyName()
    {
        return "FactionsUI.RearmScreen";
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
        AddCaption("Rearm " + _shipName);
        _steps.Clear();

        Vector2 topLeft = -WindowSize * 0.5f;
        float left = topLeft.X + 0.03f;
        float width = WindowSize.X - 0.06f;
        float y = topLeft.Y + 0.09f;

        _heading = AddLabel(new Vector2(left, y), "", 0.8f);
        y += 0.04f;

        AddLabel(new Vector2(left, y), "Weapons - pick a row, then toggle it or change its magazine", 0.7f);
        y += 0.03f;

        // VisibleRowsCount, not Size.Y, sets the height -- RefreshInternals overwrites Size.Y -- so
        // everything below is placed off the table's real size afterwards.
        _groupTable = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, y),
            Size = new Vector2(width, 0f),
            Name = "RearmGroups",
            ColumnsCount = GroupColumns,
            VisibleRowsCount = 5,
        };
        _groupTable.SetCustomColumnWidths(new float[GroupColumns] { 0.08f, 0.30f, 0.08f, 0.28f, 0.12f, 0.14f });
        _groupTable.SetColumnName(0, new StringBuilder("Load"));
        _groupTable.SetColumnName(1, new StringBuilder("Weapon"));
        _groupTable.SetColumnName(2, new StringBuilder("Guns"));
        _groupTable.SetColumnName(3, new StringBuilder("Magazine"));
        _groupTable.SetColumnName(4, new StringBuilder("Mags"));
        _groupTable.SetColumnName(5, new StringBuilder("Cost"));
        _groupTable.ItemSelected += (table, args) => RefreshButtons();
        _groupTable.ItemDoubleClicked += (table, args) => OnToggle(null);
        Controls.Add(_groupTable);
        y += _groupTable.Size.Y + 0.01f;

        _toggle = AddButton(new Vector2(left, y), 0.16f, "Load on/off",
            "Load this weapon group, or leave it as it is.", OnToggle);
        _cycle = AddButton(new Vector2(left + 0.17f, y), 0.16f, "Next magazine",
            "Switch to the next magazine this weapon takes that the station sells.", OnCycle);
        y += 0.06f;

        AddLabel(new Vector2(left, y), "Cargo reserve - magazines to carry in the hold", 0.7f);
        y += 0.03f;

        _reserveTable = new MyGuiControlTable
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            Position = new Vector2(left, y),
            Size = new Vector2(width, 0f),
            Name = "RearmReserve",
            ColumnsCount = ReserveColumns,
            VisibleRowsCount = 4,
        };
        _reserveTable.SetCustomColumnWidths(new float[ReserveColumns] { 0.30f, 0.13f, 0.13f, 0.12f, 0.14f, 0.18f });
        _reserveTable.SetColumnName(0, new StringBuilder("Magazine"));
        _reserveTable.SetColumnName(1, new StringBuilder("In stock"));
        _reserveTable.SetColumnName(2, new StringBuilder("To guns"));
        _reserveTable.SetColumnName(3, new StringBuilder("Price"));
        _reserveTable.SetColumnName(4, new StringBuilder("Reserve"));
        _reserveTable.SetColumnName(5, new StringBuilder("Cost"));
        _reserveTable.ItemSelected += (table, args) => RefreshButtons();
        Controls.Add(_reserveTable);
        y += _reserveTable.Size.Y + 0.01f;

        float stepX = left;
        foreach (long step in new long[] { -100, -10, -1, 1, 10, 100 })
        {
            long captured = step;
            _steps.Add(AddButton(new Vector2(stepX, y), 0.07f,
                (step > 0 ? "+" : "") + step.ToString(CultureInfo.InvariantCulture),
                "Change the reserve of the selected magazine.", b => OnStep(captured)));
            stepX += 0.075f;
        }
        y += 0.06f;

        _total = AddLabel(new Vector2(left, y), "", 0.8f);
        y += 0.035f;
        _status = AddLabel(new Vector2(left, y), "", 0.7f);

        _rearm = AddButton(new Vector2(left, -topLeft.Y - 0.08f), 0.16f, "Rearm",
            "Buy and load the selection. You pay no more than the total shown.", OnRearm);
        AddButton(new Vector2(left + 0.17f, -topLeft.Y - 0.08f), 0.16f, "Cancel", null, b => CloseScreen());

        Rebuild();
    }

    private MyGuiControlLabel AddLabel(Vector2 position, string text, float scale)
    {
        var label = new MyGuiControlLabel(position, null, text, null, scale, "Blue",
            MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP);
        Controls.Add(label);
        return label;
    }

    private MyGuiControlButton AddButton(Vector2 position, float width, string text, string tooltip,
        Action<MyGuiControlButton> onClick)
    {
        var button = new MyGuiControlButton(
            position: position,
            visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
            size: new Vector2(width, 0.04f),
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
            toolTip: tooltip,
            text: new StringBuilder(text),
            textScale: 0.7f,
            onButtonClick: onClick);
        Controls.Add(button);
        return button;
    }

    // ---- Estimate -----------------------------------------------------------------------------
    //
    // The same order the server fills in: loaded gun groups top to bottom, each taking what fits
    // from what is left of the station's stock, then the cargo reserve from the remainder. So every
    // number in both tables is the server's plan for this survey, not a separate guess -- short only
    // of the player's balance, which the server applies last.

    /// <summary>Magazines each loaded group takes, in table order, and what the station has left of
    /// each key once the guns are served.</summary>
    private void Allocate(out Dictionary<GroupChoice, long> groupTake, out Dictionary<string, long> stockAfterGuns)
    {
        groupTake = new Dictionary<GroupChoice, long>();
        stockAfterGuns = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (WireRearmMagazine magazine in _magazines.Values)
            stockAfterGuns[magazine.Key] = Math.Max(0, magazine.Available);

        foreach (GroupChoice choice in _groups)
        {
            if (!choice.Load || !choice.HasOption) continue;
            string key = choice.Selected.Key;
            long left;
            stockAfterGuns.TryGetValue(key, out left);
            long take = Math.Min(Math.Max(0, choice.Selected.Fits), left);
            groupTake[choice] = take;
            stockAfterGuns[key] = left - take;
        }
    }

    private double ReserveLitres(ReserveChoice except)
    {
        double litres = 0;
        foreach (ReserveChoice r in _reserves)
            if (r != except) litres += r.Count * Math.Max(0, r.Magazine.UnitLitres);
        return litres;
    }

    /// <summary>The cargo-volume bound on one reserve line: what the rest of the reserve leaves of
    /// the hold.</summary>
    private long VolumeCap(ReserveChoice choice)
    {
        if (choice.Magazine.UnitLitres <= 0) return 0;
        double room = _cargoFreeLitres - ReserveLitres(choice);
        return room <= 0 ? 0 : (long)Math.Floor(room / choice.Magazine.UnitLitres + 1e-9);
    }

    /// <summary>The most this reserve line may hold: what the guns leave of the station's stock,
    /// and what the rest of the reserve leaves of the hold.</summary>
    private static long StockCap(ReserveChoice choice, Dictionary<string, long> stockAfterGuns)
    {
        long left;
        stockAfterGuns.TryGetValue(choice.Magazine.Key, out left);
        return Math.Max(0, left);
    }

    private long ReserveCap(ReserveChoice choice, Dictionary<string, long> stockAfterGuns)
    {
        return Math.Min(StockCap(choice, stockAfterGuns), VolumeCap(choice));
    }

    private void ClampReserves()
    {
        Dictionary<GroupChoice, long> take;
        Dictionary<string, long> after;
        Allocate(out take, out after);
        foreach (ReserveChoice r in _reserves)
            r.Count = Math.Max(0, Math.Min(r.Count, ReserveCap(r, after)));
    }

    private long Total()
    {
        Dictionary<GroupChoice, long> take;
        Dictionary<string, long> after;
        Allocate(out take, out after);
        long total = 0;
        foreach (KeyValuePair<GroupChoice, long> kv in take)
        {
            WireRearmMagazine magazine;
            if (_magazines.TryGetValue(kv.Key.Selected.Key, out magazine)) total += kv.Value * Math.Max(0, magazine.UnitPrice);
        }
        foreach (ReserveChoice r in _reserves) total += r.Count * Math.Max(0, r.Magazine.UnitPrice);
        return total;
    }

    private long GunsTake(Dictionary<GroupChoice, long> take, string key)
    {
        long sum = 0;
        foreach (KeyValuePair<GroupChoice, long> kv in take)
            if (string.Equals(kv.Key.Selected.Key, key, StringComparison.Ordinal)) sum += kv.Value;
        return sum;
    }

    // ---- Tables -------------------------------------------------------------------------------

    private void Rebuild()
    {
        ClampReserves();
        Dictionary<GroupChoice, long> take;
        Dictionary<string, long> after;
        Allocate(out take, out after);

        int groupSelected = _groupTable.SelectedRowIndex ?? -1;
        _groupTable.Clear();
        foreach (GroupChoice choice in _groups)
        {
            var row = new MyGuiControlTable.Row(choice);
            row.AddCell(new MyGuiControlTable.Cell(!choice.HasOption ? "-" : choice.Load ? "yes" : "no"));
            row.AddCell(new MyGuiControlTable.Cell(choice.Group.Name ?? ""));
            row.AddCell(new MyGuiControlTable.Cell(choice.Group.Blocks.ToString(CultureInfo.InvariantCulture)));
            if (!choice.HasOption)
            {
                row.AddCell(new MyGuiControlTable.Cell("not sold here", null,
                    "This station sells no magazine these guns take."));
                row.AddCell(new MyGuiControlTable.Cell(""));
                row.AddCell(new MyGuiControlTable.Cell(""));
            }
            else
            {
                WireRearmOption option = choice.Selected;
                WireRearmMagazine magazine;
                _magazines.TryGetValue(option.Key, out magazine);
                row.AddCell(new MyGuiControlTable.Cell(MagazineName(option.Key)));
                long got;
                take.TryGetValue(choice, out got);
                if (!choice.Load)
                    row.AddCell(new MyGuiControlTable.Cell(Num(option.Fits) + " fit"));
                else if (got < option.Fits)
                    row.AddCell(new MyGuiControlTable.Cell(Num(got) + " of " + Num(option.Fits), null,
                        "Room for " + Num(option.Fits) + ", but the station has only " + Num(got)
                        + " left once the rows above take theirs."));
                else
                    row.AddCell(new MyGuiControlTable.Cell(Num(got)));
                row.AddCell(new MyGuiControlTable.Cell(choice.Load ? Num(got * magazine.UnitPrice) : ""));
            }
            _groupTable.Add(row);
        }
        if (groupSelected >= 0 && groupSelected < _groupTable.RowsCount) _groupTable.SelectedRowIndex = groupSelected;

        int reserveSelected = _reserveTable.SelectedRowIndex ?? -1;
        _reserveTable.Clear();
        foreach (ReserveChoice r in _reserves)
        {
            long guns = GunsTake(take, r.Magazine.Key);
            var row = new MyGuiControlTable.Row(r);
            row.AddCell(new MyGuiControlTable.Cell(r.Magazine.Name ?? r.Magazine.Key));
            row.AddCell(new MyGuiControlTable.Cell(Num(r.Magazine.Available)));
            row.AddCell(new MyGuiControlTable.Cell(guns > 0 ? Num(guns) : ""));
            row.AddCell(new MyGuiControlTable.Cell(Num(r.Magazine.UnitPrice)));
            row.AddCell(new MyGuiControlTable.Cell(Num(r.Count) + " / " + Num(ReserveCap(r, after)), null,
                "Reserve / most this line can hold: what the guns leave of the station's stock, "
                + "and what the rest of the reserve leaves of the hold."));
            row.AddCell(new MyGuiControlTable.Cell(r.Count > 0 ? Num(r.Count * r.Magazine.UnitPrice) : ""));
            _reserveTable.Add(row);
        }
        if (reserveSelected >= 0 && reserveSelected < _reserveTable.RowsCount) _reserveTable.SelectedRowIndex = reserveSelected;

        double freeAfter = Math.Max(0, _cargoFreeLitres - ReserveLitres(null));
        _heading.Text = "Balance: " + Num(_balance) + " cr     Cargo free: " + Num((long)_cargoFreeLitres)
                      + " L (" + Num((long)freeAfter) + " L after the reserve)";

        long total = Total();
        _total.Text = "Total: " + Num(total) + " cr";
        const string overBalance = "More than your balance. The station loads what your credits cover, guns first.";
        if (total > _balance) _status.Text = overBalance;
        else if (_status.Text == overBalance) _status.Text = "";
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        GroupChoice group = SelectedGroup();
        if (_toggle != null) _toggle.Enabled = group != null && group.HasOption;
        if (_cycle != null) _cycle.Enabled = group != null && group.Group.Options != null && group.Group.Options.Length > 1;
        bool reserve = SelectedReserve() != null;
        foreach (MyGuiControlButton step in _steps) step.Enabled = reserve;
        if (_rearm != null) _rearm.Enabled = Total() > 0;
    }

    private GroupChoice SelectedGroup()
    {
        MyGuiControlTable.Row row = _groupTable != null ? _groupTable.SelectedRow : null;
        return row != null ? row.UserData as GroupChoice : null;
    }

    private ReserveChoice SelectedReserve()
    {
        MyGuiControlTable.Row row = _reserveTable != null ? _reserveTable.SelectedRow : null;
        return row != null ? row.UserData as ReserveChoice : null;
    }

    private string MagazineName(string key)
    {
        WireRearmMagazine magazine;
        return _magazines.TryGetValue(key, out magazine) && !string.IsNullOrEmpty(magazine.Name) ? magazine.Name : key;
    }

    // ---- Actions ------------------------------------------------------------------------------

    private void OnToggle(MyGuiControlButton button)
    {
        GroupChoice group = SelectedGroup();
        if (group == null || !group.HasOption) return;
        group.Load = !group.Load;
        _status.Text = "";
        Rebuild();
    }

    private void OnCycle(MyGuiControlButton button)
    {
        GroupChoice group = SelectedGroup();
        if (group == null || group.Group.Options == null || group.Group.Options.Length < 2) return;
        group.Option = (group.Option + 1) % group.Group.Options.Length;
        _status.Text = "";
        Rebuild();
    }

    /// <summary>Adjusts the selected reserve line and, when the bound stops it, says which bound —
    /// a silent clamp reads as the button not working.</summary>
    private void OnStep(long step)
    {
        ReserveChoice reserve = SelectedReserve();
        if (reserve == null) return;
        Dictionary<GroupChoice, long> take;
        Dictionary<string, long> after;
        Allocate(out take, out after);
        long stockCap = StockCap(reserve, after);
        long volumeCap = VolumeCap(reserve);
        long cap = Math.Min(stockCap, volumeCap);
        long wanted = reserve.Count + step;
        reserve.Count = Math.Max(0, Math.Min(cap, wanted));

        string name = reserve.Magazine.Name ?? reserve.Magazine.Key;
        if (wanted > cap)
        {
            long guns = GunsTake(take, reserve.Magazine.Key);
            _status.Text = stockCap <= volumeCap
                ? name + ": reserve capped at " + Num(cap) + ". The station has " + Num(reserve.Magazine.Available)
                  + (guns > 0 ? " and the selected guns take " + Num(guns) + " first." : ".")
                : name + ": reserve capped at " + Num(cap) + " by the free cargo space.";
        }
        else
        {
            _status.Text = "";
        }
        Rebuild();
    }

    private void OnRearm(MyGuiControlButton button)
    {
        try
        {
            var picks = new List<WireRearmPick>();
            foreach (GroupChoice choice in _groups)
                if (choice.Load && choice.HasOption)
                    picks.Add(new WireRearmPick { GroupId = choice.Group.GroupId, Key = choice.Selected.Key });

            var reserves = new List<WireRearmReserve>();
            foreach (ReserveChoice r in _reserves)
                if (r.Count > 0) reserves.Add(new WireRearmReserve { Key = r.Magazine.Key, Count = r.Count });

            long total = Total();
            if (total <= 0 || picks.Count + reserves.Count == 0)
            {
                _status.Text = "Nothing selected to buy.";
                return;
            }

            DockSupplyComponent component = DockSupplyComponent.Instance;
            if (component != null) component.RequestRearm(_gridEntityId, total, picks.ToArray(), reserves.ToArray());
            CloseScreen();
        }
        catch (Exception ex)
        {
            Plugin.Log("rearm request failed: " + ex);
            CloseScreen();
        }
    }

    private static string Num(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
