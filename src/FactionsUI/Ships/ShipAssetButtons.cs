using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using EmptyKeys.UserInterface;
using EmptyKeys.UserInterface.Controls;
using EmptyKeys.UserInterface.Controls.Primitives;
using EmptyKeys.UserInterface.Input;
using Factions.Wire;
using Sandbox.Game.Screens.ViewModels;
using Sandbox.Game.Screens.ViewModels.ServicesTerminal;
using SpaceEngineers.Game.GUI;

namespace FactionsUI.Ships;

/// <summary>
/// The buttons inserted beside vanilla's own function button in the Services Terminal:
/// <b>Send to Node</b> and <b>Scan</b>, which act on the hull selected on the Active Grids tab,
/// <b>Scan station</b>, which acts on the terminal's own station and therefore needs no selection at
/// all, and <b>Refuel H2</b>, <b>Refuel O2</b> and <b>Rearm</b>, which sell the station's stock
/// into the selected hull (<see cref="DockSupplyComponent"/>). Restore lives on the Repair tab
/// (<see cref="RepairPanel"/>).
///
/// <para>The first three appear only at a node the player's own faction owns; the dock-supply three
/// appear at any station, because selling fuel is not administering the station. Whichever are
/// visible are packed from the left edge, so a customer at an NPC station does not see a gap where
/// the owner's buttons would be.</para>
///
/// <para><b>Injected controls, driven imperatively.</b> The screen is EmptyKeys MVVM and its view
/// is generated code (<c>ServicesTerminalView</c>, 3300 lines of <c>InitializeComponent</c>), so a
/// new control cannot be given a generated binding path: those are registered per view-model
/// property at type-init time and a path the generator never saw resolves to nothing. Everything
/// these buttons need instead comes off <see cref="INotifyPropertyChanged"/> on the view models
/// that already exist — tab selection from <see cref="MyGridStorageViewModel"/>, row selection from
/// its <see cref="MyActiveGridsViewModel"/> — and is written straight onto
/// <see cref="UIElement.Visibility"/> and <see cref="UIElement.IsEnabled"/>.</para>
///
/// <para><b>Located structurally, not by generated name.</b> The anchor is found as the plain
/// <see cref="Button"/> sitting in the same <see cref="Grid"/> as the two tab
/// <see cref="ToggleButton"/>s whose command parameters are "1" and "2" — that pair identifies the
/// storage panel across a rename of any <c>e_NN</c> field. The buttons are then placed in the
/// anchor's own grid cell, so vanilla's layout decides where the row is.</para>
///
/// <para>Fails closed: anything thrown while walking or extending the tree leaves the view exactly
/// as SE generated it, minus the buttons. A Services Terminal that cannot be used is a worse outcome
/// than one without ours.</para>
/// </summary>
internal sealed class ShipAssetButtons
{
    /// <summary>Depth cap on the tree walk. The anchor sits six levels down; anything past this is
    /// a cycle rather than a deeper view.</summary>
    private const int MaxDepth = 40;

    internal static ShipAssetButtons Current { get; private set; }

    private readonly MyGridStorageViewModel _storage;
    private readonly Button _send;
    private readonly Button _scan;
    private readonly Button _scanStation;
    private readonly Button _refuelH2;
    private readonly Button _refuelO2;
    private readonly Button _rearm;

    /// <summary>Gap between packed buttons, in the view's own units.</summary>
    private const float ButtonGap = 8f;

    /// <summary>Each button's width, in row order. Sized to the label with a margin: the row has to
    /// share one line with vanilla's 200-unit function button (FAC-387), and 740 units with gaps
    /// leaves it about 140 clear.
    ///
    /// <para><b>Fixed, not measured.</b> v0.325.0 scaled the row to the host grid's
    /// <c>ActualWidth</c>. That grid sizes to its content, so every refresh read back the row it had
    /// just shrunk and shrank it again, and labels were cut to "Sca" and "Refu" (operator
    /// screenshot, 2026-09-25). The view is authored in fixed units that scale uniformly with
    /// resolution. The grid is about 1,100 units wide, measured off two screenshots.</para></summary>
    private static readonly float[] NaturalWidths = { 160f, 90f, 140f, 110f, 110f, 90f };

    private ShipAssetButtons(MyGridStorageViewModel storage, Button send, Button scan,
        Button scanStation, Button refuelH2, Button refuelO2, Button rearm)
    {
        _storage = storage;
        _send = send;
        _scan = scan;
        _scanStation = scanStation;
        _refuelH2 = refuelH2;
        _refuelO2 = refuelO2;
        _rearm = rearm;
    }

    /// <summary>Called from a postfix on <c>MyGuiScreenServicesTerminal.CreateView</c>, which runs
    /// before the view is laid out and before its data context is assigned — the one moment the tree
    /// can be extended without fighting a layout pass.</summary>
    internal static void Install(MyGuiScreenServicesTerminal screen, UIRoot root)
    {
        Detach();
        if (screen == null || root == null) return;

        // No link, or a server offering neither terminal feature: leave the terminal vanilla. Either
        // bit is enough to install, because each button carries its own — a server may grant the
        // station refit and withhold the hulls, or the other way round. The snapshot's mayUse
        // decides the rest, and until it arrives the buttons stay collapsed.
        if (!TerminalShipAssetsComponent.AnyTerminalSurface && !DockSupplyComponent.Available) return;

        var viewModel = screen.ViewModel as MyServicesTerminalViewModel;
        if (viewModel == null) return;
        MyGridStorageViewModel storage = viewModel.GridStorageViewModel;
        if (storage == null) return;

        Grid host;
        Button anchor;
        if (!TryFindFunctionButton(root, out host, out anchor))
        {
            Plugin.Log("services terminal: function button not found; Send/Scan not added");
            return;
        }

        int row = Grid.GetRow(anchor);
        int column = Grid.GetColumn(anchor);

        Button send = NewButton("Send to Node", NaturalWidths[0]);
        Button scan = NewButton("Scan", NaturalWidths[1]);
        Button scanStation = NewButton("Scan station", NaturalWidths[2]);
        Button refuelH2 = NewButton("Refuel H2", NaturalWidths[3]);
        Button refuelO2 = NewButton("Refuel O2", NaturalWidths[4]);
        Button rearm = NewButton("Rearm", NaturalWidths[5]);

        var buttons = new ShipAssetButtons(storage, send, scan, scanStation, refuelH2, refuelO2, rearm);
        send.Command = new RelayCommand(buttons.OnSendClicked);
        scan.Command = new RelayCommand(buttons.OnScanClicked);
        scanStation.Command = new RelayCommand(buttons.OnScanStationClicked);
        refuelH2.Command = new RelayCommand(buttons.OnRefuelH2Clicked);
        refuelO2.Command = new RelayCommand(buttons.OnRefuelO2Clicked);
        rearm.Command = new RelayCommand(buttons.OnRearmClicked);

        // One horizontal panel in the cell, not six buttons in it. Buttons sharing a grid cell and
        // offset by their left margins each had a layout slot starting at the cell's left edge, so
        // every slot overlapped the buttons before it and hovering one lit all of those as well
        // (operator, 2026-09-25). In a panel each button owns its own slot, and a collapsed one
        // takes no space, which is also what packs the row.
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            // Mirrors vanilla's own function button in the same row, which sits 25 units off the
            // bottom of the 60-unit row.
            Margin = new Thickness(0f, 5f, 0f, 25f),
        };
        foreach (Button button in new[] { send, scan, scanStation, refuelH2, refuelO2, rearm })
            panel.Children.Add(button);

        host.Children.Add(panel);
        Grid.SetRow(panel, row);
        // From the grid's first column, not the anchor's. The anchor's row is empty under the two
        // tab toggles (130 + 130 units), and vanilla's own button is right-aligned in the last
        // column, so starting at the left edge is what leaves room for both.
        Grid.SetColumn(panel, 0);
        Grid.SetColumnSpan(panel, column + 1);

        Current = buttons;
        buttons.Subscribe();
        buttons.Apply();
    }

    /// <summary>Drops the event subscriptions. The controls themselves die with the view.</summary>
    internal static void Detach()
    {
        ShipAssetButtons current = Current;
        Current = null;
        if (current == null) return;
        try { current.Unsubscribe(); }
        catch (Exception ex) { Plugin.Log("services terminal button teardown failed: " + ex); }
    }

    // ---- Tree surgery -------------------------------------------------------------------------

    private static Button NewButton(string text, float width)
    {
        return new Button
        {
            Content = text,
            Width = width,
            Margin = new Thickness(0f, 0f, ButtonGap, 0f),
            // Out of the gamepad tab order on purpose: the generated view navigates by TabIndex
            // arithmetic (SearchNavRightIndex is 10 or 12 depending on the panel), and a new stop
            // in that row would shift what those numbers reach.
            IsTabStop = false,
            Visibility = Visibility.Collapsed,
            IsEnabled = false,
        };
    }

    /// <summary>Finds the storage panel by the pair of tab toggles it contains, then the one plain
    /// button in it. <see cref="ToggleButton"/> derives from <see cref="Button"/>, so the anchor
    /// test has to be on the exact type.</summary>
    private static bool TryFindFunctionButton(UIElement root, out Grid host, out Button anchor)
    {
        host = null;
        anchor = null;

        var queue = new Queue<KeyValuePair<UIElement, int>>();
        queue.Enqueue(new KeyValuePair<UIElement, int>(root, 0));
        var children = new List<UIElement>();

        while (queue.Count > 0)
        {
            KeyValuePair<UIElement, int> entry = queue.Dequeue();
            if (entry.Value > MaxDepth) continue;

            children.Clear();
            AppendChildren(entry.Key, children);

            var grid = entry.Key as Grid;
            if (grid != null)
            {
                bool storedTab = false;
                bool activeTab = false;
                Button plain = null;
                foreach (UIElement child in children)
                {
                    var toggle = child as ToggleButton;
                    if (toggle != null)
                    {
                        if ("1".Equals(toggle.CommandParameter)) storedTab = true;
                        else if ("2".Equals(toggle.CommandParameter)) activeTab = true;
                        continue;
                    }
                    if (plain == null && child != null && child.GetType() == typeof(Button)) plain = (Button)child;
                }
                if (storedTab && activeTab && plain != null)
                {
                    host = grid;
                    anchor = plain;
                    return true;
                }
            }

            foreach (UIElement child in children)
            {
                if (child != null) queue.Enqueue(new KeyValuePair<UIElement, int>(child, entry.Value + 1));
            }
        }
        return false;
    }

    /// <summary>Every way this view holds a child. The tab pages are the reason
    /// <see cref="ItemsControl.ItemsSource"/> is here: the generated view builds its
    /// <see cref="TabControl"/>'s items as a plain collection, so a panel-only walk never reaches
    /// the storage panel at all.</summary>
    private static void AppendChildren(UIElement element, List<UIElement> into)
    {
        var panel = element as Panel;
        if (panel != null && panel.Children != null)
        {
            foreach (UIElement child in panel.Children) into.Add(child);
        }

        var items = element as ItemsControl;
        if (items != null)
        {
            if (items.ItemsSource != null)
            {
                foreach (object item in items.ItemsSource)
                {
                    var child = item as UIElement;
                    if (child != null) into.Add(child);
                }
            }
            if (items.Items != null)
            {
                foreach (object item in items.Items)
                {
                    var child = item as UIElement;
                    if (child != null) into.Add(child);
                }
            }
        }

        var content = element as ContentControl;
        if (content != null)
        {
            var child = content.Content as UIElement;
            if (child != null) into.Add(child);
        }

        var border = element as Border;
        if (border != null && border.Child != null) into.Add(border.Child);
    }

    // ---- State --------------------------------------------------------------------------------

    private void Subscribe()
    {
        _storage.PropertyChanged += OnStoragePropertyChanged;
        if (_storage.ActiveGridsViewModel != null)
            _storage.ActiveGridsViewModel.PropertyChanged += OnActivePropertyChanged;

        TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
        if (component != null) component.SnapshotChanged += Apply;
    }

    private void Unsubscribe()
    {
        _storage.PropertyChanged -= OnStoragePropertyChanged;
        if (_storage.ActiveGridsViewModel != null)
            _storage.ActiveGridsViewModel.PropertyChanged -= OnActivePropertyChanged;

        TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
        if (component != null) component.SnapshotChanged -= Apply;
    }

    private void OnStoragePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e == null || e.PropertyName != "IsActiveGridsTabSelected") return;
        Apply();
    }

    private void OnActivePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e == null || e.PropertyName != "SelectedItem") return;
        Apply();
    }

    /// <summary>Visible only on the Active tab and only once the server has said this terminal may
    /// be used; the two hull buttons enabled only with a row selected.
    ///
    /// <para><b>Each button gates on its own feature.</b> mayUse is the one thing all three share —
    /// the caller is at a Services Terminal on a node their faction owns — and the two bits are
    /// independent: a server may grant the hulls and withhold the surface that rebuilds a live
    /// station, or the reverse, and either way the buttons it did grant still appear.</para>
    ///
    /// <para>Scan station is enabled whenever it is visible: its target is the terminal's own grid,
    /// so a selection in the Active Grids list has nothing to do with it.</para></summary>
    private void Apply()
    {
        try
        {
            TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
            bool usable = component != null
                          && component.MayUse
                          && _storage.IsActiveGridsTabSelected;

            bool ships = usable && TerminalShipAssetsComponent.Available;
            bool enabled = ships && SelectedGrid() != null;

            Visibility visibility = ships ? Visibility.Visible : Visibility.Collapsed;
            _send.Visibility = visibility;
            _scan.Visibility = visibility;
            _send.IsEnabled = enabled;
            _scan.IsEnabled = enabled;

            bool station = usable && Nodes.StationRefitComponent.Available;
            _scanStation.Visibility = station ? Visibility.Visible : Visibility.Collapsed;
            _scanStation.IsEnabled = station;

            // Not gated on mayUse: that answers "is this your faction's node", and a customer at
            // anyone's station may buy fuel. The server applies the customer's own gate.
            bool dock = DockSupplyComponent.Available && _storage.IsActiveGridsTabSelected;
            bool dockEnabled = dock && SelectedGrid() != null;
            foreach (Button button in new[] { _refuelH2, _refuelO2, _rearm })
            {
                button.Visibility = dock ? Visibility.Visible : Visibility.Collapsed;
                button.IsEnabled = dockEnabled;
            }

        }
        catch (Exception ex)
        {
            Plugin.Log("services terminal button refresh failed: " + ex);
        }
    }

    private MyGridStorageItemModel SelectedGrid()
    {
        MyActiveGridsViewModel active = _storage.ActiveGridsViewModel;
        return active == null ? null : active.SelectedItem;
    }

    // ---- Actions ------------------------------------------------------------------------------

    private void OnSendClicked(object parameter)
    {
        try
        {
            TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
            if (component == null || !component.MayUse) return;
            MyGridStorageItemModel item = SelectedGrid();
            if (item == null || item.EntityId == 0) return;

            long entityId = item.EntityId;
            var body = new StringBuilder()
                .AppendLine()
                .Append("Send ").Append(string.IsNullOrEmpty(item.Name) ? "this ship" : item.Name)
                .Append(" to the node?").AppendLine()
                .Append("Blocks: ").Append(item.Blocks.ToString(CultureInfo.InvariantCulture))
                .Append("   PCU: ").Append(item.PCU.ToString(CultureInfo.InvariantCulture))
                .AppendLine().AppendLine()
                .Append("The hull becomes one ship asset at this station and its cargo is credited ")
                .Append("to the node. This cannot be undone from here — withdraw it to get it back.");

            TerminalShipAssetsComponent.ConfirmYesNo(
                "SEND TO NODE", body.ToString(), () => component.RequestSend(entityId));
        }
        catch (Exception ex)
        {
            Plugin.Log("send to node failed: " + ex);
        }
    }

    private void OnScanClicked(object parameter)
    {
        try
        {
            TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
            if (component == null || !component.MayUse) return;
            MyGridStorageItemModel item = SelectedGrid();
            if (item == null || item.EntityId == 0) return;
            ShipScanScreen.Open(item.EntityId, item.Name);
        }
        catch (Exception ex)
        {
            Plugin.Log("scan dialog open failed: " + ex);
        }
    }

    /// <summary>The station this terminal stands on, named by the terminal's own entity id. The
    /// dialog is the ship scan's, and the server resolves the grid group, the acceptance rules and
    /// the node itself.</summary>
    private void OnScanStationClicked(object parameter)
    {
        try
        {
            TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
            if (component == null || !component.MayUse || component.TerminalEntityId == 0) return;
            ShipScanScreen.OpenForStation(component.TerminalEntityId,
                string.IsNullOrEmpty(component.NodeName) ? "" : component.NodeName);
        }
        catch (Exception ex)
        {
            Plugin.Log("station scan dialog open failed: " + ex);
        }
    }

    private void OnRefuelH2Clicked(object parameter)
    {
        Refuel(WireDockGas.Hydrogen);
    }

    private void OnRefuelO2Clicked(object parameter)
    {
        Refuel(WireDockGas.Oxygen);
    }

    /// <summary>Asks for a quote; the confirmation dialog opens when it arrives, carrying the
    /// server's own sentence and total.</summary>
    private void Refuel(WireDockGas gas)
    {
        try
        {
            DockSupplyComponent component = DockSupplyComponent.Instance;
            if (component == null) return;
            MyGridStorageItemModel item = SelectedGrid();
            if (item == null || item.EntityId == 0) return;
            component.RequestRefuelQuote(item.EntityId, gas);
        }
        catch (Exception ex)
        {
            Plugin.Log("refuel request failed: " + ex);
        }
    }

    /// <summary>Asks what the selected ship's guns take and what the station sells of it; the
    /// rearm dialog opens when the survey arrives.</summary>
    private void OnRearmClicked(object parameter)
    {
        try
        {
            DockSupplyComponent component = DockSupplyComponent.Instance;
            if (component == null) return;
            MyGridStorageItemModel item = SelectedGrid();
            if (item == null || item.EntityId == 0) return;
            RearmScreen.PendingShipName = item.Name;
            component.RequestRearmSurvey(item.EntityId);
        }
        catch (Exception ex)
        {
            Plugin.Log("rearm survey request failed: " + ex);
        }
    }
}
