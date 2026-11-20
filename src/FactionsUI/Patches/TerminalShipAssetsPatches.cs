using System;
using System.Collections.Generic;
using EmptyKeys.UserInterface.Controls;
using Factions.Wire;
using FactionsUI.Ships;
using HarmonyLib;
using Sandbox.Game.EntityComponents.Interfaces;
using Sandbox.Game.Screens.ViewModels.ServicesTerminal;
using Sandbox.ModAPI;
using SpaceEngineers.Game.GUI;

namespace FactionsUI.Patches;

/// <summary>
/// Everything this plugin adds to SE's Services Terminal, as six patches on five vanilla seams.
///
/// <para><b>Every one of them fails closed</b>, in the shape <see cref="TerminalAssetsTabPatch"/>
/// established: the terminal is how a player retrieves and stores their own ships, so an exception
/// thrown from here must leave that working, minus whatever of ours it was doing. Where a prefix
/// would otherwise hand a synthetic row to vanilla — which would then ask the server to retrieve a
/// guid it never stored — the failure path suppresses the vanilla call instead of running it, and
/// says so in the log.</para>
///
/// <para>The seams, and why each is the one:</para>
/// <list type="bullet">
/// <item><c>MyServicesTerminalViewModel..ctor</c> — the only place holding both the block (whose
/// entity id is the request key) and the <see cref="IMyGridStorageProxy"/> the withdrawal later
/// hands the guid to.</item>
/// <item><c>MyServicesTerminalViewModel.OnScreenClosed</c> — where that state stops being true.</item>
/// <item><c>MyGridStorageModelBase.RefreshViewItems</c> — the one method every rebuild of either
/// list funnels through, and the point after vanilla has cleared <c>m_dataItems</c>.</item>
/// <item><c>MyStoredGridsViewModel.OnPull</c> / <c>OnDelete</c> — the two things a stored row can
/// have done to it.</item>
/// <item><c>MyGuiScreenServicesTerminal.CreateView</c> — the freshly generated view, before layout.</item>
/// </list>
/// </summary>
internal static class TerminalShipAssetsPatches
{
    /// <summary>The screen is opening. Captures the terminal and its proxy, and asks the server what
    /// this faction holds; the answer decides whether anything is shown at all.</summary>
    [HarmonyPatch(typeof(MyServicesTerminalViewModel), MethodType.Constructor,
        new[] { typeof(IMyFunctionalBlock), typeof(IMyGridStorageProxy) })]
    internal static class ViewModelConstructor
    {
        private static void Postfix(MyServicesTerminalViewModel __instance, IMyFunctionalBlock block,
            IMyGridStorageProxy gridStorageEntityProxy)
        {
            try
            {
                if (block == null) return;
                // Before the ship-asset snapshot, which returns early without that feature: the
                // Repair tab's probe is its own.
                RestorationComponent restoration = RestorationComponent.Instance;
                if (restoration != null) restoration.OnTerminalOpened(block.EntityId);
                TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
                if (component == null) return;
                component.OnTerminalOpened(__instance, block.EntityId, gridStorageEntityProxy);
            }
            catch (Exception ex)
            {
                Plugin.Log("services terminal open hook failed: " + ex);
            }
        }
    }

    /// <summary>The screen closed. The proxy, the snapshot and the injected buttons' subscriptions
    /// all belong to it; the next terminal builds its own.</summary>
    [HarmonyPatch(typeof(MyServicesTerminalViewModel), "OnScreenClosed")]
    internal static class ViewModelClosed
    {
        private static void Postfix()
        {
            try { ShipAssetButtons.Detach(); }
            catch (Exception ex) { Plugin.Log("services terminal button detach failed: " + ex); }

            try
            {
                RepairPanel.Detach();
                RestorationComponent.Instance?.OnTerminalClosed();
            }
            catch (Exception ex) { Plugin.Log("repair panel detach failed: " + ex); }

            try
            {
                TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
                if (component != null) component.OnTerminalClosed();
            }
            catch (Exception ex)
            {
                Plugin.Log("services terminal close hook failed: " + ex);
            }
        }
    }

    /// <summary>
    /// Appends the faction's ship assets to the Stored Grids list.
    ///
    /// <para>A prefix, not a postfix, because the rows have to be in <c>m_dataItems</c> before
    /// vanilla builds the filtered, sorted, paged view out of it. It re-adds on every refresh by
    /// design — vanilla clears the list in its own server callback and calls straight through to
    /// here — and dedupes by id so a refresh that did not clear (paging, sorting, a search
    /// keystroke) cannot double them.</para>
    ///
    /// <para>Only the stored list. The active list is live grids near the terminal and has nothing
    /// of ours in it.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyGridStorageModelBase), "RefreshViewItems")]
    internal static class RefreshViewItems
    {
        private static void Prefix(MyGridStorageModelBase __instance,
            List<MyGridStorageItemModel> ___m_dataItems)
        {
            try
            {
                if (!(__instance is MyStoredGridsViewModel)) return;
                TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
                if (component == null || !TerminalShipAssetsComponent.Available) return;
                component.AppendRows(___m_dataItems);
            }
            catch (Exception ex)
            {
                // Vanilla's own rows are untouched by a throw here; the list simply shows fewer.
                Plugin.Log("ship-asset rows not added: " + ex);
            }
        }
    }

    /// <summary>
    /// Pull, on one of our rows: a withdrawal request instead of vanilla's retrieval.
    ///
    /// <para>Vanilla would ask the server to validate a guid it has never stored, so this must not
    /// fall through on failure — a row recognised as ours suppresses the vanilla path whether or not
    /// the request went out. The conversion the server performs is what puts a real stored grid
    /// behind that guid, and only then does vanilla's queue take over.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyStoredGridsViewModel), "OnPull")]
    internal static class OnPull
    {
        private static bool Prefix(MyStoredGridsViewModel __instance)
        {
            TerminalShipAssetsComponent component;
            WireShipAssetRow row;
            try
            {
                component = TerminalShipAssetsComponent.Instance;
                if (component == null) return true;
                MyGridStorageItemModel item = __instance.SelectedItem;
                if (item == null) return true;
                if (!component.TryGetRow(item.Id, out row)) return true;   // a real stored grid
            }
            catch (Exception ex)
            {
                Plugin.Log("ship-asset pull recognition failed, falling back to vanilla: " + ex);
                return true;
            }

            try
            {
                component.RequestWithdraw(row);
                __instance.BigButtonEnabled = false;
            }
            catch (Exception ex)
            {
                Plugin.Log("ship-asset withdrawal request failed: " + ex);
            }
            return false;
        }
    }

    /// <summary>Delete, on one of our rows: nothing. A synthetic row is a count at a node, and
    /// vanilla's delete would either fail against an unknown guid or — worse, if one ever collided —
    /// destroy a real stored grid.</summary>
    [HarmonyPatch(typeof(MyStoredGridsViewModel), "OnDelete")]
    internal static class OnDelete
    {
        private static bool Prefix(MyStoredGridsViewModel __instance, object model)
        {
            try
            {
                TerminalShipAssetsComponent component = TerminalShipAssetsComponent.Instance;
                if (component == null) return true;
                MyGridStorageItemModel item = model as MyGridStorageItemModel ?? __instance.SelectedItem;
                if (item == null) return true;
                if (!component.IsOurs(item.Id)) return true;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log("ship-asset delete guard failed, falling back to vanilla: " + ex);
                return true;
            }
        }
    }

    /// <summary>The Active Grids buttons, inserted into the freshly generated view. See
    /// <see cref="ShipAssetButtons"/> for why they are driven imperatively rather than bound.</summary>
    [HarmonyPatch(typeof(MyGuiScreenServicesTerminal), "CreateView")]
    internal static class CreateView
    {
        private static void Postfix(MyGuiScreenServicesTerminal __instance, UIRoot __result)
        {
            try
            {
                ShipAssetButtons.Install(__instance, __result);
            }
            catch (Exception ex)
            {
                // The view SE generated is untouched apart from whatever was added before the throw,
                // and a stray collapsed button is invisible. Never fault the screen itself.
                ShipAssetButtons.Detach();
                Plugin.Log("services terminal buttons not added: " + ex);
            }

            try
            {
                RepairPanel.Install(__instance, __result);
            }
            catch (Exception ex)
            {
                RepairPanel.Detach();
                Plugin.Log("repair panel not installed: " + ex);
            }
        }
    }

    /// <summary>Vanilla's Repair tab commands at a station whose Repair tab is Restore's. The tab's
    /// buttons are rebound by <see cref="RepairPanel"/>; these catch the gamepad bindings, which
    /// still call the view model: generate asks for a fresh bill, accept is the panel's own button,
    /// reject does nothing. Anywhere else vanilla runs.</summary>
    [HarmonyPatch(typeof(MyRepairServiceViewModel), "OnGenerateRepairQuote")]
    internal static class RepairGenerate
    {
        private static bool Prefix()
        {
            try
            {
                if (!RepairPanel.Active) return true;
                RepairPanel.Current.Refresh();
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log("repair generate hook failed: " + ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(MyRepairServiceViewModel), "OnAcceptOffer")]
    internal static class RepairAccept
    {
        private static bool Prefix()
        {
            try
            {
                if (!RepairPanel.Active) return true;
                RepairPanel.Current.OnAcceptClicked(null);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log("repair accept hook failed: " + ex);
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(MyRepairServiceViewModel), "OnRejectOffer")]
    internal static class RepairReject
    {
        private static bool Prefix()
        {
            return !RepairPanel.Active;
        }
    }
}
