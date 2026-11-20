using System;
using System.Collections.ObjectModel;
using System.Reflection;
using EmptyKeys.UserInterface;
using EmptyKeys.UserInterface.Controls;
using EmptyKeys.UserInterface.Input;
using FactionsUI.Nodes;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Screens.ViewModels;
using Sandbox.Game.World;
using SpaceEngineers.Game.GUI;
using VRage.Game.ModAPI;

namespace FactionsUI.Patches;

/// <summary>
/// The Withdraw and Deposit tabs on a store block standing on a station your faction owns.
///
/// <para>Each tab is a launcher, not a second copy of the transfer controls. SE's store screen is an
/// MVVM screen whose tabs are code-generated from XAML, and a binding onto anything of ours has no
/// generated counterpart to bind through — so the tab holds one imperatively-driven button, and the
/// controls behind it are a conventional <see cref="StationTransferScreen"/> that the node window
/// opens too. One piece of transfer UI, two ways in.</para>
///
/// <para><b>Whether the tabs appear at all is the server's answer, never this client's.</b> The
/// store block knows the grid it is on; the server is asked what node that station is and whether
/// this viewer may move anything there, and the tabs stay hidden until it says yes. A cached world
/// on this side would be the wrong thing to ask: it stops refreshing the moment its own window
/// closes.</para>
///
/// <para>Every patch here fails open, the rule <see cref="StoreShipInventoryPatch"/> states:
/// anything thrown leaves the store screen behaving exactly as SE built it, because a store block
/// that cannot be traded with is a worse outcome than a missing convenience.</para>
/// </summary>
internal static class StoreNodeTransferPatch
{
    /// <summary>The store screen currently up, or null. Read by <see cref="TransferTarget"/> so a
    /// transfer lands wherever the store block's own inventory dropdown is pointing.</summary>
    internal static MyStoreBlockViewModel ViewModel;

    private static TabItem _withdrawTab;
    private static TabItem _depositTab;
    private static bool _tabsShown;

    /// <summary>What the server is asked about. The station id when SE knows one for this grid, and
    /// the grid's own entity id when it does not — a scan-adopted station is recorded under the
    /// latter, so both are values the server's node model may be carrying.</summary>
    private static long _stationId;

    /// <summary>Set once on the first fault, because this runs every frame and a persistent one
    /// would fill the log faster than a player could read it.</summary>
    private static bool _disabled;

    /// <summary>Appends the two tabs to whichever store view SE just built. The keyboard and gamepad
    /// views name their tab control differently, so it is found by type rather than by field
    /// name.</summary>
    [HarmonyPatch(typeof(MyGuiScreenStoreBlock), "CreateView")]
    internal static class CreateView
    {
        private static void Postfix(UIRoot __result)
        {
            _withdrawTab = null;
            _depositTab = null;
            _tabsShown = false;
            if (__result == null || _disabled) return;

            try
            {
                TabControl tabs = FindTabControl(__result);
                if (tabs == null) return;
                var items = tabs.ItemsSource as ObservableCollection<object>;
                if (items == null) return;

                _withdrawTab = BuildTab("Withdraw",
                    "Take this station's own stock out into your ship.");
                _depositTab = BuildTab("Deposit",
                    "Put goods into this station's own stock. No money changes hands.");
                items.Add(_withdrawTab);
                items.Add(_depositTab);
            }
            catch (Exception ex)
            {
                _withdrawTab = null;
                _depositTab = null;
                _disabled = true;
                Plugin.Log("station stock tabs not added, store screen left as vanilla built it: " + ex);
            }
        }
    }

    /// <summary>
    /// Asks the server about this station and shows or hides the tabs on its answer.
    ///
    /// <para>The question is sent from here rather than from <c>InitializeData</c>, which SE runs
    /// from a parallel task's completion callback; this runs on the frame loop, where sending is
    /// unambiguously safe. The component throttles the repeat, so asking every frame costs one
    /// message every couple of seconds.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyStoreBlockViewModel), "Update")]
    internal static class Update
    {
        private static void Postfix(MyStoreBlockViewModel __instance, MyStoreBlock ___m_storeBlock)
        {
            if (_disabled) return;
            try
            {
                if (!ReferenceEquals(ViewModel, __instance))
                {
                    ViewModel = __instance;
                    _stationId = ResolveStationId(___m_storeBlock);
                }

                NodeStockComponent component = NodeStockComponent.Instance;
                if (component == null || !NodeStockComponent.Available || _stationId == 0)
                {
                    ShowTabs(false);
                    return;
                }

                // Stand aside while another surface owns the component's single question — a
                // transfer window the node window opened, on a node that is not this station. Both
                // asking every frame would clear and re-send sixty times a second, and reading the
                // other one's answer would put these tabs on a store block that is not ours.
                if (StationTransferScreen.Instance == null || component.IsQueryFor(_stationId, ""))
                    component.Query(_stationId, "", force: false);

                ShowTabs(component.HasSnapshot && component.MayTransfer
                         && component.IsQueryFor(_stationId, ""));
            }
            catch (Exception ex)
            {
                _disabled = true;
                ShowTabs(false);
                Plugin.Log("station stock tabs disabled for this session: " + ex);
            }
        }
    }

    /// <summary>The tabs and the recorded view model belong to one screen; the next store block
    /// builds its own.</summary>
    [HarmonyPatch(typeof(MyStoreBlockViewModel), "OnScreenClosing")]
    internal static class OnScreenClosing
    {
        private static void Postfix()
        {
            ViewModel = null;
            _withdrawTab = null;
            _depositTab = null;
            _tabsShown = false;
            _stationId = 0;
        }
    }

    // ---- Building ------------------------------------------------------------------------------

    private static TabItem BuildTab(string header, string explanation)
    {
        var tab = new TabItem
        {
            Name = "FactionsStationStock" + header,
            Header = header,
            // Hidden until the server says this station is yours. Vanilla's own tabs bind this
            // property to the view model; ours is written imperatively because a binding path onto
            // an object of ours has no generated property info to travel through.
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(new TextBlock
        {
            Text = explanation,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0f, 0f, 0f, 16f),
        });

        var button = new Button
        {
            Width = 320f,
            Height = 44f,
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = "Open station stock",
            Command = new RelayCommand(OnOpenClicked),
        };
        panel.Children.Add(button);

        tab.Content = panel;
        return tab;
    }

    private static void OnOpenClicked(object parameter)
    {
        try
        {
            if (_stationId != 0) StationTransferScreen.OpenForStation(_stationId);
        }
        catch (Exception ex)
        {
            Plugin.Log("station stock window open failed: " + ex);
        }
    }

    private static void ShowTabs(bool visible)
    {
        if (_tabsShown == visible) return;
        _tabsShown = visible;
        Visibility state = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_withdrawTab != null) _withdrawTab.Visibility = state;
        if (_depositTab != null) _depositTab.Visibility = state;
    }

    private static TabControl FindTabControl(UIRoot view)
    {
        foreach (FieldInfo field in AccessTools.GetDeclaredFields(view.GetType()))
        {
            if (!typeof(TabControl).IsAssignableFrom(field.FieldType)) continue;
            var tabs = field.GetValue(view) as TabControl;
            if (tabs != null) return tabs;
        }
        return null;
    }

    /// <summary>What to ask the server about. A founded station has an SE faction station whose id
    /// the server's node carries; a station the server adopted from a scan carries the grid's entity
    /// id instead, and that is also the only answer available when the client has no station record
    /// for this grid at all.</summary>
    private static long ResolveStationId(MyStoreBlock block)
    {
        MyCubeGrid grid = block != null ? block.CubeGrid : null;
        if (grid == null) return 0;
        try
        {
            if (MySession.Static != null && MySession.Static.Factions != null)
            {
                IMyFactionStation station = MySession.Static.Factions.GetStationByGridId(grid.EntityId);
                if (station != null) return station.Id;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log("station lookup for the store block's grid failed: " + ex);
        }
        return grid.EntityId;
    }
}
