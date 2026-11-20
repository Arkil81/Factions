using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using FactionsUI.Trade;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Screens.Models;
using Sandbox.Game.Screens.ViewModels;
using VRage;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Definitions;

namespace FactionsUI.Patches;

/// <summary>
/// Adds the "Ship inventory" option to the store block's inventory dropdown and makes the Sell tab,
/// the Buy tab and both All buttons mean the whole docked ship when it is chosen. See
/// <see cref="ShipInventory"/> for why the fan-out has to happen on this side of the wire.
///
/// Every patch here fails open: anything thrown leaves the store screen behaving exactly as SE
/// built it, because a store block that cannot be traded with is a worse outcome than a missing
/// convenience.
/// </summary>
internal static class StoreShipInventoryPatch
{
    private static readonly MethodInfo UpdateVolumeTextMethod =
        AccessTools.Method(typeof(MyInventoryTargetViewModel), "UpdateVolumeText");
    private static readonly MethodInfo UpdateLocalPlayerCurrencyMethod =
        AccessTools.Method(typeof(MyInventoryTargetViewModel), "UpdateLocalPlayerCurrency");

    /// <summary>Appends the entry once vanilla has rebuilt the dropdown, from the same connected-grid
    /// entity list vanilla was handed — the authoritative answer to "what is docked", already
    /// filtered by the server for access and ownership.</summary>
    [HarmonyPatch(typeof(MyStoreBlockViewModel), "OnGetInventories")]
    internal static class OnGetInventories
    {
        private static void Postfix(MyStoreBlockViewModel __instance, List<long> inventoryEntities)
        {
            try
            {
                ShipInventory.Install(__instance.InventoryTargetViewModel, inventoryEntities);
            }
            catch (Exception ex)
            {
                ShipInventory.Reset();
                Plugin.Log("ship-inventory dropdown entry not added: " + ex);
            }
        }
    }

    /// <summary>Lists the whole ship's contents when the entry is selected. This is also what makes
    /// the Sell tab's All button work: vanilla reads the amount it offers straight off this list.</summary>
    [HarmonyPatch(typeof(MyInventoryTargetViewModel), "UpdateInventoryContent")]
    internal static class UpdateInventoryContent
    {
        private static bool Prefix(MyInventoryTargetViewModel __instance)
        {
            if (!ShipInventory.IsSelected(__instance)) return true;

            try
            {
                var items = new List<MyInventoryItemModel>();
                ShipInventory.CollectItems(items);
                __instance.InventoryItems = new ObservableCollection<MyInventoryItemModel>(items);

                // The volume readout is written by the selection setter BEFORE it reaches here, so
                // it has to be re-run after the totals are recomputed or it shows the ship one
                // selection behind.
                ShipInventory.RefreshVolume();
                UpdateVolumeTextMethod.Invoke(__instance, null);
                UpdateLocalPlayerCurrencyMethod.Invoke(__instance, null);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log("ship-inventory content listing failed, falling back to vanilla: " + ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Keeps the item list live while the entry is selected.
    ///
    /// <para>This is the patch the feature does not work without, and the reason is one line of
    /// vanilla: both of SE's live-update handlers open with
    /// <c>Inventories[SelectedInventoryIndex].Inventory != inventory</c>, and a synthetic entry's
    /// <c>Inventory</c> is <b>null</b>. Every change event is therefore discarded while it is
    /// selected. The sale itself worked and the list simply never moved — and because the
    /// removals replicate back *after* the store's own result callback, refreshing on that callback
    /// alone reads the hold as still full.</para>
    ///
    /// <para>The rest of SE's body is already correct for an aggregated list: it matches models by
    /// <c>model.Inventory == inventory</c>, which <see cref="ShipInventory.CollectItems"/> sets per
    /// item. So only the guard is replaced — the body below mirrors the original.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyInventoryTargetViewModel), "OnInventoryContentChanged")]
    internal static class OnInventoryContentChanged
    {
        private static bool Prefix(MyInventoryTargetViewModel __instance, MyInventoryBase inventory,
            MyPhysicalInventoryItem item, MyFixedPoint amount)
        {
            if (!ShipInventory.IsSelected(__instance)) return true;
            if (!ShipInventory.Owns(inventory)) return false; // not this ship's — vanilla would drop it too

            try
            {
                ObservableCollection<MyInventoryItemModel> items = __instance.InventoryItems;
                if (items == null) return false;

                MyInventoryItemModel emptied = null;
                bool unseen = true;
                foreach (MyInventoryItemModel model in items)
                {
                    if (model.Inventory != inventory || item.ItemId != model.InventoryItem.ItemId) continue;
                    if (item.Amount == 0) emptied = model;
                    else model.Amount += (float)amount;
                    unseen = false;
                    break;
                }
                if (unseen) items.Add(new MyInventoryItemModel(item, inventory));
                if (emptied != null) items.Remove(emptied);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log("ship-inventory live item update failed: " + ex);
                return false;
            }
        }
    }

    /// <summary>The volume readout, blocked by the same null-inventory guard. Vanilla writes the one
    /// selected inventory's volume; this entry's is the sum over the ship, so it is recomputed
    /// rather than assigned.</summary>
    [HarmonyPatch(typeof(MyInventoryTargetViewModel), "Inventory_ContentsChanged")]
    internal static class InventoryContentsChanged
    {
        private static bool Prefix(MyInventoryTargetViewModel __instance, MyInventoryBase inventory)
        {
            if (!ShipInventory.IsSelected(__instance)) return true;
            if (!ShipInventory.Owns(inventory)) return false;

            try
            {
                ShipInventory.RefreshVolume();
                UpdateVolumeTextMethod.Invoke(__instance, null);
            }
            catch (Exception ex)
            {
                Plugin.Log("ship-inventory live volume update failed: " + ex);
            }
            return false;
        }
    }

    /// <summary>Sell tab, All: everything of this kind the ship holds, capped by what the station
    /// is still buying. Counted the way the sale will actually be split — whole units per
    /// inventory — so All never offers a number the plan cannot then cover.</summary>
    [HarmonyPatch(typeof(MyStoreBlockViewModel), "OnSetAllAmountOrder")]
    internal static class OnSetAllAmountOrder
    {
        private static bool Prefix(MyStoreBlockViewModel __instance)
        {
            try
            {
                if (!ShipInventory.IsSelected(__instance.InventoryTargetViewModel)) return true;

                MyStoreItemModel item = __instance.SelectedOrderItem;
                if (item == null || item.ItemType != ItemTypes.PhysicalItem) return true;

                int onShip = ShipInventory.SellableTotal(item.ItemDefinitionId);
                if (onShip > 0) __instance.AmountToSell = Math.Min(item.Amount, onShip);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log("ship-inventory sell-all failed, falling back to vanilla: " + ex);
                return true;
            }
        }
    }

    /// <summary>Buy tab, All: the smaller of what the station has left and what the ship's cargo
    /// containers have room for. Vanilla only ever knew the first of those.</summary>
    [HarmonyPatch(typeof(MyStoreBlockViewModel), "OnSetAllAmountOffer")]
    internal static class OnSetAllAmountOffer
    {
        private static bool Prefix(MyStoreBlockViewModel __instance)
        {
            try
            {
                if (!ShipInventory.IsSelected(__instance.InventoryTargetViewModel)) return true;

                MyStoreItemModel item = __instance.SelectedOfferItem;
                if (item == null || item.ItemType != ItemTypes.PhysicalItem) return true;

                int room = ShipInventory.SpaceTotal(item.ItemDefinitionId);
                __instance.AmountToBuy = Math.Min(__instance.AmountToBuyMaximum, room);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log("ship-inventory buy-all failed, falling back to vanilla: " + ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(MyStoreBlockViewModel), "OnSell")]
    internal static class OnSell
    {
        private static bool Prefix(MyStoreBlockViewModel __instance, MyStoreBlock ___m_storeBlock,
            double ___m_lastEconomyTick)
        {
            try
            {
                if (!ShipInventory.IsSelected(__instance.InventoryTargetViewModel)) return true;
                if (ShipInventory.Batch != null) return false; // one transaction at a time

                MyStoreItemModel item = __instance.SelectedOrderItem;
                if (item == null) return true;

                // Gas and ships have no hold to spread across; the entry carries the character's
                // entity id, so vanilla handles those exactly as if Character were selected.
                if (item.ItemType != ItemTypes.PhysicalItem) return true;

                if (!__instance.IsSellEnabled) return false;

                int amount = (int)__instance.AmountToSell;
                if (amount <= 0 || amount > item.Amount) return false;

                List<ShipInventory.Chunk> plan;
                if (!ShipInventory.PlanSell(item.ItemDefinitionId, amount, out plan))
                {
                    ShipTradeBatch.Report(__instance, selling: true, storeItemId: item.Id, amount: 0,
                        sellResult: MyStoreSellItemResults.NotEnoughAmount,
                        buyResult: MyStoreBuyItemResults.NotEnoughAmount);
                    return false;
                }

                if (ShipTradeBatch.Start(__instance, ___m_storeBlock, selling: true,
                        storeItemId: item.Id, economyTick: ___m_lastEconomyTick, chunks: plan))
                    return false;

                // Nothing was sent. Vanilla's single-target request against the character is a
                // worse trade than the one asked for, but it is a working button.
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log("ship-inventory sell failed, falling back to vanilla: " + ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(MyStoreBlockViewModel), "OnBuy")]
    internal static class OnBuy
    {
        private static bool Prefix(MyStoreBlockViewModel __instance, MyStoreBlock ___m_storeBlock,
            double ___m_lastEconomyTick)
        {
            try
            {
                if (!ShipInventory.IsSelected(__instance.InventoryTargetViewModel)) return true;
                if (ShipInventory.Batch != null) return false;

                MyStoreItemModel item = __instance.SelectedOfferItem;
                if (item == null) return true;
                if (item.ItemType != ItemTypes.PhysicalItem) return true;

                // Vanilla's own affordability gate; the server re-checks per chunk anyway.
                if (!__instance.IsBuyEnabled) return false;

                int amount = (int)__instance.AmountToBuy;
                if (amount <= 0 || amount > item.Amount) return false;

                List<ShipInventory.Chunk> plan;
                if (!ShipInventory.PlanBuy(item.ItemDefinitionId, amount, out plan))
                {
                    ShipTradeBatch.Report(__instance, selling: false, storeItemId: item.Id, amount: 0,
                        sellResult: MyStoreSellItemResults.NotEnoughInventorySpace,
                        buyResult: MyStoreBuyItemResults.NotEnoughInventorySpace);
                    return false;
                }

                if (ShipTradeBatch.Start(__instance, ___m_storeBlock, selling: false,
                        storeItemId: item.Id, economyTick: ___m_lastEconomyTick, chunks: plan))
                    return false;

                // Nothing was sent. Vanilla's single-target request against the character is a
                // worse trade than the one asked for, but it is a working button.
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log("ship-inventory buy failed, falling back to vanilla: " + ex);
                return true;
            }
        }
    }

    /// <summary>The batch's only heartbeat: a request the server refuses is dropped without a reply,
    /// and nothing else would ever re-enable the button.</summary>
    [HarmonyPatch(typeof(MyStoreBlockViewModel), "Update")]
    internal static class Update
    {
        private static void Postfix()
        {
            ShipTradeBatch batch = ShipInventory.Batch;
            if (batch == null) return;

            try
            {
                batch.Tick();
            }
            catch (Exception ex)
            {
                ShipInventory.Batch = null;
                Plugin.Log("ship-inventory batch watchdog failed: " + ex);
            }
        }
    }

    /// <summary>The dropdown entry and any batch belong to one screen; the next store block builds
    /// its own.</summary>
    [HarmonyPatch(typeof(MyStoreBlockViewModel), "OnScreenClosing")]
    internal static class OnScreenClosing
    {
        private static void Postfix()
        {
            ShipInventory.Reset();
        }
    }
}
