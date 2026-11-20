using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using EmptyKeys.UserInterface;
using EmptyKeys.UserInterface.Controls;
using EmptyKeys.UserInterface.Data;
using EmptyKeys.UserInterface.Input;
using EmptyKeys.UserInterface.Media;
using Factions.Wire;
using Sandbox.Game.Screens.Models.ServicesTerminal;
using Sandbox.Game.Screens.ViewModels.ServicesTerminal;
using SpaceEngineers.Game.GUI;

namespace FactionsUI.Ships;

/// <summary>
/// The Services Terminal's <b>Repair</b> tab at a station Factions runs: Restore's surface. The
/// grid list on the left stays vanilla's; the quote column on the right is replaced by our panel —
/// the design, what the restore does, a scrollable bill with one row per component, the total and
/// the time — and the tab's own accept button starts the restore, stops a running one, or registers
/// a whole ship's design. No dialogs: matching, refusals and progress all show in the panel.
///
/// <para><b>Found by binding path, driven imperatively.</b> The view is generated code
/// (<c>ServicesTerminalView</c>), and the Repair and Salvage tabs are built from the same shape. The
/// anchor is the button whose <c>Command</c> is bound to <c>GenerateRepairQuote</c> — a path only the
/// Repair tab has, and one no localisation or <c>e_NN</c> rename moves. Its grid is the quote column;
/// in it the accept and reject buttons are found by their <c>AcceptOffer</c>/<c>RejectOffer</c>
/// bindings and vanilla's quote panel by its <c>IsActiveQuoteVisible</c> binding. Those bindings are
/// cleared and the elements written directly; our own controls carry no binding at all, so no
/// generated binding path is needed (client-plugin.md Part 12).</para>
///
/// <para><b>Only at a Factions station.</b> Nothing is touched until the server's answer to the
/// terminal's probe says this station's Repair tab is Restore's; elsewhere the tab is vanilla.
/// Anything thrown while altering the view leaves it as far as it got and logs; the server refuses
/// vanilla's repair at a Factions station either way.</para>
/// </summary>
internal sealed class RepairPanel
{
    private const int MaxDepth = 40;

    private static readonly ColorW Label = new ColorW(255, 255, 255, 255);
    private static readonly ColorW Value = new ColorW(198, 220, 228, 255);
    private static readonly ColorW Warn = new ColorW(255, 120, 100, 255);
    private static readonly ColorW Rule = new ColorW(77, 99, 113, 255);

    /// <summary>Bill table columns, in the view's own units: component, need, cargo, station, bank,
    /// credits. Fixed, and the one definition the header and the rows share. The rows sit in a
    /// <see cref="ScrollViewer"/>, which measures its content unbounded, so star widths there
    /// collapse to each cell's own width and stop lining up with a header sized by the panel.
    /// Credits holds "9,999,999"; larger totals are written compactly (<see cref="Credits"/>).</summary>
    private static readonly float[] ColumnWidths = { 150f, 70f, 70f, 70f, 70f, 110f };

    internal static RepairPanel Current { get; private set; }

    private readonly MyRepairServiceViewModel _vm;
    private readonly UIRoot _root;
    private bool _active;

    private Button _accept;
    private TextBlock _design;
    private TextBlock _counts;
    private Grid _table;
    private TextBlock _total;
    private TextBlock _status;

    private long _gridId;
    private WireRestoreQuote _quote;

    private RepairPanel(MyRepairServiceViewModel vm, UIRoot root)
    {
        _vm = vm;
        _root = root;
    }

    /// <summary>From the <c>CreateView</c> postfix: keeps the tree and the view model; changes
    /// nothing until <see cref="TryActivate"/>. The server's probe answer can arrive before the
    /// view is created — the probe is sent from the view model's constructor — so a station already
    /// known to be managed tries here, and <see cref="RestorationComponent"/> retries.</summary>
    internal static void Install(MyGuiScreenServicesTerminal screen, UIRoot root)
    {
        Detach();
        var viewModel = screen == null ? null : screen.ViewModel as MyServicesTerminalViewModel;
        if (viewModel == null || viewModel.RepairServiceViewModel == null || root == null) return;
        Current = new RepairPanel(viewModel.RepairServiceViewModel, root);
        RestorationComponent restoration = RestorationComponent.Instance;
        if (restoration != null && restoration.Managed) Current.TryActivate(false);
    }

    internal static void Detach()
    {
        RepairPanel current = Current;
        Current = null;
        if (current == null || !current._active) return;
        try { current._vm.PropertyChanged -= current.OnVmPropertyChanged; }
        catch (Exception ex) { Plugin.Log("repair panel teardown failed: " + ex); }
    }

    /// <summary>Whether the open terminal's Repair tab is ours and the panel is up.</summary>
    internal static bool Active => Current != null && Current._active;

    /// <summary>The server said this station's Repair tab is Restore's: replace the quote column.
    /// False, with nothing changed, while vanilla's controls cannot be found yet — the answer can
    /// land before the tab's content is ready, so the caller retries.</summary>
    internal bool TryActivate(bool logFailure)
    {
        if (_active) return true;
        Grid column, host;
        Button generate;
        if (!TryFindAnchor(_root, out generate, out column, out host))
        {
            if (logFailure) Plugin.Log("repair panel: the Repair tab's quote column was not found; the tab stays vanilla");
            return false;
        }

        Button accept = null;
        foreach (UIElement child in column.Children)
            if (child is Button button && button != generate && HasPath(button, "AcceptOffer")) accept = button;
        if (accept == null)
        {
            if (logFailure) Plugin.Log("repair panel: the Repair tab's accept button was not found; the tab stays vanilla");
            return false;
        }

        foreach (UIElement child in column.Children)
        {
            if (child is Button button && button != generate && button != accept)
            {
                if (HasPath(button, "RejectOffer")) Collapse(button);
            }
            else if (child is Grid grid && HasPath(grid, "IsActiveQuoteVisible"))
            {
                Collapse(grid);
            }
            else if (child is Border rule && Grid.GetRow(rule) < 4)
            {
                // Vanilla's separator under its Generate button: it would run through our header.
                Collapse(rule);
            }
        }
        Collapse(generate);

        // The bill needs the width more than the grid list does: swap vanilla's 1.5 : 1.
        if (host != null && host.ColumnDefinitions != null && host.ColumnDefinitions.Count >= 2)
        {
            host.ColumnDefinitions[0].Width = new GridLength(1f, GridUnitType.Star);
            host.ColumnDefinitions[1].Width = new GridLength(1.1f, GridUnitType.Star);
        }

        BindingOperations.ClearBinding(accept, Button.CommandProperty);
        BindingOperations.ClearBinding(accept, UIElement.IsEnabledProperty);
        BindingOperations.ClearBinding(accept, UIElement.ToolTipProperty);
        accept.Command = new RelayCommand(OnAcceptClicked);
        _accept = accept;

        Grid panel = BuildPanel();
        column.Children.Add(panel);
        Grid.SetRow(panel, 0);
        Grid.SetRowSpan(panel, 4);
        Grid.SetColumn(panel, 0);
        Grid.SetColumnSpan(panel, 2);

        _active = true;
        _vm.PropertyChanged += OnVmPropertyChanged;
        Show(null, "Select a ship to see what restoring it takes.");
        OnSelectionChanged();
        return true;
    }

    // ---- finding vanilla's controls -------------------------------------------------------------

    private static bool TryFindAnchor(UIElement root, out Button generate, out Grid column, out Grid host)
    {
        generate = null;
        column = null;
        host = null;
        var queue = new Queue<(UIElement Element, Grid Parent, Grid GrandParent, int Depth)>();
        queue.Enqueue((root, null, null, 0));
        var children = new List<UIElement>();
        while (queue.Count > 0)
        {
            var entry = queue.Dequeue();
            if (entry.Depth > MaxDepth) continue;
            if (entry.Element is Button button && entry.Parent != null && HasPath(button, "GenerateRepairQuote"))
            {
                generate = button;
                column = entry.Parent;
                host = entry.GrandParent;
                return true;
            }
            children.Clear();
            AppendChildren(entry.Element, children);
            Grid asGrid = entry.Element as Grid;
            foreach (UIElement child in children)
                if (child != null)
                    queue.Enqueue((child, asGrid ?? entry.Parent, asGrid != null ? entry.Parent : entry.GrandParent, entry.Depth + 1));
        }
        return false;
    }

    /// <summary>Whether any binding on <paramref name="element"/> reads <paramref name="path"/>.</summary>
    private static bool HasPath(UIElement element, string path)
    {
        List<BindingExpression> bindings = BindingOperations.GetAllBindings(element);
        if (bindings == null) return false;
        foreach (BindingExpression b in bindings)
        {
            Binding parent = b?.ParentBinding;
            if (parent?.Path != null && string.Equals(parent.Path.Path, path, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void Collapse(UIElement element)
    {
        BindingOperations.ClearBinding(element, UIElement.VisibilityProperty);
        element.Visibility = Visibility.Collapsed;
    }

    /// <summary>Every way the generated view holds a child (see ShipAssetButtons).</summary>
    private static void AppendChildren(UIElement element, List<UIElement> into)
    {
        if (element is Panel panel && panel.Children != null)
            foreach (UIElement child in panel.Children) into.Add(child);
        if (element is ItemsControl items)
        {
            if (items.ItemsSource != null)
                foreach (object item in items.ItemsSource)
                    if (item is UIElement child) into.Add(child);
            if (items.Items != null)
                foreach (object item in items.Items)
                    if (item is UIElement child) into.Add(child);
        }
        if (element is ContentControl content && content.Content is UIElement inner) into.Add(inner);
        if (element is Border border && border.Child != null) into.Add(border.Child);
    }

    // ---- building ------------------------------------------------------------------------------

    private Grid BuildPanel()
    {
        // Clear of the grid list on the left, whose header runs to its column's edge.
        var panel = new Grid { Margin = new Thickness(15f, 5f, 0f, 5f) };
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1f, GridUnitType.Auto) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1f, GridUnitType.Auto) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1f, GridUnitType.Auto) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1f, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1f, GridUnitType.Auto) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1f, GridUnitType.Auto) });

        _design = Text("", Label);
        Add(panel, _design, 0);
        _counts = Text("", Value);
        Add(panel, _counts, 1);

        Grid header = NewTable();
        AddRow(header, 0, new[] { "Component", "Need", "Cargo", "Station", "Bank", "Credits" }, Label, null);
        var rule = new Border { Height = 2f, Background = new SolidColorBrush(Rule), VerticalAlignment = VerticalAlignment.Bottom };
        header.Children.Add(rule);
        Grid.SetColumnSpan(rule, ColumnWidths.Length);
        Add(panel, header, 2);

        _table = NewTable();
        var scroll = new ScrollViewer
        {
            Content = _table,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Add(panel, scroll, 3);

        _total = Text("", Label);
        Add(panel, _total, 4);
        _status = Text("", Value);
        Add(panel, _status, 5);
        return panel;
    }

    private static Grid NewTable()
    {
        var table = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (float width in ColumnWidths)
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Pixel) });
        return table;
    }

    private static TextBlock Text(string text, ColorW color)
    {
        return new TextBlock
        {
            Text = text,
            Margin = new Thickness(2f, 3f, 2f, 3f),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(color),
        };
    }

    private static void Add(Grid grid, UIElement element, int row)
    {
        grid.Children.Add(element);
        Grid.SetRow(element, row);
    }

    private static void AddRow(Grid table, int row, string[] cells, ColorW color, ColorW? lastColor)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1f, GridUnitType.Auto) });
        for (int c = 0; c < cells.Length; c++)
        {
            TextBlock cell = Text(cells[c], c == cells.Length - 1 && lastColor.HasValue ? lastColor.Value : color);
            cell.TextWrapping = TextWrapping.NoWrap;
            if (c > 0) cell.HorizontalAlignment = HorizontalAlignment.Right;
            table.Children.Add(cell);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, c);
        }
    }

    // ---- state ---------------------------------------------------------------------------------

    private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e == null || e.PropertyName != "SelectedGrid") return;
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        try
        {
            MyServiceGridModel selected = _vm.SelectedGrid;
            _gridId = selected == null ? 0 : selected.GridEntityId;
            _quote = null;
            if (_gridId == 0)
            {
                Show(null, "Select a ship to see what restoring it takes.");
                return;
            }
            Show(null, "Reading " + (string.IsNullOrEmpty(selected.Name) ? "the ship" : selected.Name) + "...");
            RestorationComponent.Instance?.RequestQuote(_gridId);
        }
        catch (Exception ex)
        {
            Plugin.Log("repair panel selection failed: " + ex);
        }
    }

    /// <summary>The selected ship's id, or 0.</summary>
    internal long SelectedGridId => _gridId;

    /// <summary>The panel's current offer, for the update loop's progress refresh.</summary>
    internal WireRestoreOffer Offer => _quote == null ? WireRestoreOffer.None : _quote.Offer;

    internal void OnQuote(WireRestoreQuote q)
    {
        if (!_active || q == null || q.GridEntityId != _gridId) return;
        _quote = q;
        Show(q, q.Message);
    }

    /// <summary>A request's answer: shown on the status line; the panel then asks again.</summary>
    internal void OnResult(long gridEntityId, bool ok, string message)
    {
        if (!_active || gridEntityId != _gridId) return;
        _status.Text = message ?? "";
        _status.Foreground = new SolidColorBrush(ok ? Value : Warn);
        RestorationComponent.Instance?.RequestQuote(_gridId);
    }

    private void Show(WireRestoreQuote q, string status)
    {
        try
        {
            _table.Children.Clear();
            _table.RowDefinitions.Clear();
            _design.Text = q == null || string.IsNullOrEmpty(q.Source)
                ? (q != null && q.Offer == WireRestoreOffer.Register ? "Design: none registered" : "")
                : "Design: " + q.Source + (q.Score >= 0 ? " (match " + q.Score.ToString("0.00", CultureInfo.InvariantCulture) + ")" : "");
            _counts.Text = Counts(q);
            _total.Text = Total(q);
            _status.Text = status ?? "";
            _status.Foreground = new SolidColorBrush(q != null && q.Offer == WireRestoreOffer.None && q.Lines != null && q.Lines.Length > 0 ? Warn : Value);

            if (q?.Lines != null)
                for (int i = 0; i < q.Lines.Length; i++)
                {
                    WireRestoreLine l = q.Lines[i];
                    long credits = l.NodeCredits + l.BankCredits;
                    AddRow(_table, i, new[]
                    {
                        l.Name ?? "?", Num(l.Need), Num(l.FromShip), Num(l.FromNode), Num(l.FromBank),
                        l.Short > 0 ? "short " + Num(l.Short) : Credits(credits),
                    }, Value, l.Short > 0 ? Warn : (ColorW?)null);
                }

            SetButton(q);
        }
        catch (Exception ex)
        {
            Plugin.Log("repair panel update failed: " + ex);
        }
    }

    private static string Counts(WireRestoreQuote q)
    {
        if (q == null) return "";
        if (q.Offer == WireRestoreOffer.Running)
            return "Welded " + q.Welded + " and placed " + q.Placed + " of " + q.Total
                 + (q.Skipped > 0 ? ", " + q.Skipped + " refused" : "") + "; paid " + Num(q.Paid) + " cr";
        if (q.Welds + q.Places + q.Blocked + q.Extra + q.Unreachable == 0) return "";
        return "Weld " + q.Welds + ", place " + q.Places
             + (q.Blocked > 0 ? ", blocked " + q.Blocked : "")
             + (q.Unreachable > 0 ? ", unreachable " + q.Unreachable : "")
             + (q.Extra > 0 ? ", your additions " + q.Extra + " (kept)" : "");
    }

    private static string Total(WireRestoreQuote q)
    {
        if (q == null) return "";
        switch (q.Offer)
        {
            case WireRestoreOffer.Restore:
            case WireRestoreOffer.None when q.Lines != null && q.Lines.Length > 0:
                long station = 0, bank = 0;
                foreach (WireRestoreLine l in q.Lines) { station += l.NodeCredits; bank += l.BankCredits; }
                return "Total " + Num(q.Credits) + " cr (station " + Num(station) + ", bank " + Num(bank)
                     + " at a premium), about " + Duration(q.Seconds);
            case WireRestoreOffer.Register:
                return "Registration fee " + Num(q.Credits) + " cr";
            case WireRestoreOffer.Running:
                return "About " + Duration(q.Seconds) + " left";
            default:
                return "";
        }
    }

    private void SetButton(WireRestoreQuote q)
    {
        if (_accept == null) return;
        WireRestoreOffer offer = q == null ? WireRestoreOffer.None : q.Offer;
        switch (offer)
        {
            case WireRestoreOffer.Restore:
                _accept.Content = "Restore (" + Num(q.Credits) + " cr)";
                _accept.IsEnabled = true;
                break;
            case WireRestoreOffer.Register:
                _accept.Content = "Register design (" + Num(q.Credits) + " cr)";
                _accept.IsEnabled = true;
                break;
            case WireRestoreOffer.Running:
                _accept.Content = "Stop";
                _accept.IsEnabled = true;
                break;
            default:
                _accept.Content = "Restore";
                _accept.IsEnabled = false;
                break;
        }
    }

    /// <summary>The tab's own button, and vanilla's gamepad binding for it. No confirmation: the
    /// bill is on the panel, and the total it shows is the most the server may charge.</summary>
    internal void OnAcceptClicked(object parameter)
    {
        try
        {
            WireRestoreQuote q = _quote;
            RestorationComponent component = RestorationComponent.Instance;
            if (q == null || component == null || q.GridEntityId != _gridId) return;
            switch (q.Offer)
            {
                case WireRestoreOffer.Restore: component.Request(q.GridEntityId, WireRestoreAction.Start, q.Credits); break;
                case WireRestoreOffer.Register: component.Request(q.GridEntityId, WireRestoreAction.Register, q.Credits); break;
                case WireRestoreOffer.Running: component.Request(q.GridEntityId, WireRestoreAction.Stop, 0); break;
                default: return;
            }
            _accept.IsEnabled = false;
            _status.Text = "Asking the station...";
        }
        catch (Exception ex)
        {
            Plugin.Log("repair panel action failed: " + ex);
        }
    }

    /// <summary>Vanilla's generate-quote gesture: asks again for the selected ship.</summary>
    internal void Refresh()
    {
        if (_gridId != 0) RestorationComponent.Instance?.RequestQuote(_gridId);
    }

    private static string Duration(int seconds)
    {
        if (seconds < 90) return seconds + " s";
        return ((seconds + 59) / 60) + " min";
    }

    private static string Num(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A row's credits: in full up to 9,999,999, then in millions ("12.3M"), so the
    /// column never runs into its neighbour.</summary>
    internal static string Credits(long value)
    {
        if (value < 10000000) return Num(value);
        return (value / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }
}
