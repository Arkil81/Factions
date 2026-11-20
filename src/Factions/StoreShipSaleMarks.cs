using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Game;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Hears a ship bought from a store. SE's grid purchase (<c>MyStoreBlock.BuyPrefabInternal</c>)
    /// never raises the store item's <c>OnTransaction</c> and never names the grid it spawns — the
    /// spawn is asynchronous and its completion delegate is a closure — so two hooks share the work:
    /// a prefix notes the purchase (prefab, buyer, and the selling faction when the item is a
    /// Factions shelf hull), and SE's own <c>MyVisualScriptLogicProvider.PrefabSpawnedDetailed</c>,
    /// raised with the first spawned grid's entity id and the prefab name, confirms it
    /// (<see cref="StoreShipSaleLedger"/>). Only a confirmed purchase counts; SE can still refuse
    /// after the prefix ran (PCU, limits, no free place) or fail the spawn, and neither is heard.
    ///
    /// <para>A confirmed purchase is recorded twice over: which prefab the grid came from, for
    /// restoration (<see cref="CustomPrefabStore.RecordSoldShip"/>), and — for a shelf hull — one
    /// hull off the seller's <c>ListedForSale</c> and <c>ShipStock</c>
    /// (<see cref="StationStoreManager.OnPlayerBoughtShip"/>), without which the next store refresh
    /// lists the sold hull again.</para>
    ///
    /// <para>Applied by hand, like <see cref="SafeZoneMissilePatches"/>: an attribute naming a
    /// private method a later SE build renamed would take plugin init down.</para>
    /// </summary>
    internal static class StoreShipSaleMarks
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private static readonly StoreShipSaleLedger Ledger = new StoreShipSaleLedger();
        private static bool _subscribed;

        /// <summary>Where a confirmed sale's prefab is written. Null records nothing.</summary>
        internal static CustomPrefabStore Store;

        /// <summary>Takes a confirmed shelf sale off the seller's books (seller tag, prefab). Null
        /// changes nothing.</summary>
        internal static Func<string, string, ShipSaleDecrement> ShelfSold;

        /// <summary>On a Nexus agent: the seller's books are the authority's, so the sale goes there
        /// (seller tag, prefab, buyer identity) instead of <see cref="ShelfSold"/>.</summary>
        internal static Action<string, string, long> ShelfSoldRemote;

        internal static bool Bound { get; private set; }

        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo target = AccessTools.Method(typeof(MyStoreBlock), "BuyPrefabInternal");
                if (target == null)
                {
                    Log.Info("StoreShipSaleMarks: MyStoreBlock.BuyPrefabInternal not found; bought ships carry no design record.");
                    return;
                }
                var prefix = new HarmonyMethod(typeof(StoreShipSaleMarks)
                    .GetMethod("OnBuyPrefab", BindingFlags.NonPublic | BindingFlags.Static));
                harmony.Patch(target, prefix: prefix);
                if (!_subscribed)
                {
                    MyVisualScriptLogicProvider.PrefabSpawnedDetailed += OnPrefabSpawned;
                    _subscribed = true;
                }
                Bound = true;
                Log.Info("StoreShipSaleMarks: bound; a ship bought from a store records its prefab.");
            }
            catch (Exception ex)
            {
                Log.Info("StoreShipSaleMarks: could not patch the grid purchase: " + ex.Message);
            }
        }

        /// <summary>Notes the purchase. Runs before SE's own checks, so a refused purchase also lands
        /// here — it is simply never confirmed, because no spawn follows it.</summary>
        private static void OnBuyPrefab(MyStoreItem storeItem, MyPlayer player)
        {
            try
            {
                if (storeItem == null || string.IsNullOrEmpty(storeItem.PrefabName)) return;
                string shelfTag;
                StationStoreManager.TryGetShipListing(storeItem, out shelfTag);
                Ledger.Note(new PendingShipSale
                {
                    PrefabId = storeItem.PrefabName,
                    BuyerIdentityId = StoreTransactionHelpers.IdentityOf(player),
                    ShelfFactionTag = shelfTag,
                    AtUtc = DateTime.UtcNow,
                });
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "StoreShipSaleMarks: could not note a grid purchase.");
            }
        }

        private static void OnPrefabSpawned(long entityId, string prefabName)
        {
            try
            {
                if (Ledger.PendingCount == 0 || entityId == 0) return;
                DateTime now = DateTime.UtcNow;
                PendingShipSale sale;
                if (!Ledger.TryConfirm(prefabName, now, out sale)) return;
                if (Store != null) Store.RecordSoldShip(entityId, prefabName, sale.BuyerIdentityId, now);

                string books = "";
                if (sale.ShelfFactionTag != null && ShelfSoldRemote != null)
                {
                    ShelfSoldRemote(sale.ShelfFactionTag, prefabName, sale.BuyerIdentityId);
                    books = " seller=" + sale.ShelfFactionTag + " books=authority";
                }
                else if (sale.ShelfFactionTag != null)
                {
                    ShipSaleDecrement d = ShelfSold != null ? ShelfSold(sale.ShelfFactionTag, prefabName) : new ShipSaleDecrement();
                    books = " seller=" + sale.ShelfFactionTag
                          + (d.Sold
                              ? " node=" + d.NodeId + " listed=" + d.ListedBefore + "->" + d.ListedAfter
                                + " stock=" + d.StockBefore + "->" + d.StockAfter
                              : " listed=none");
                }
                Log.Info("SHIP_SOLD prefab=" + prefabName + " grid=" + entityId + " buyer=" + sale.BuyerIdentityId + books);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "StoreShipSaleMarks: could not record a bought ship.");
            }
        }
    }
}
