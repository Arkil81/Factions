using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What one ship sold from a store's shelf took off the faction's books.</summary>
    public struct ShipSaleDecrement
    {
        /// <summary>False when no owned node had the prefab listed; nothing was changed.</summary>
        public bool Sold;
        public string NodeId;
        public int ListedBefore, ListedAfter;
        public int StockBefore, StockAfter;
    }

    /// <summary>
    /// A ship sold from a faction's store shelf. The shelf shows <see cref="SpatialNode.ListedForSale"/>
    /// summed over the owner's network, and the hull sold is also a hull of
    /// <see cref="SpatialNode.ShipStock"/>, so one sale takes one off both at the first owned node
    /// that has the prefab listed. Without it the next store refresh lists the sold hull again.
    /// </summary>
    public static class StoreShipSale
    {
        public static ShipSaleDecrement SellListed(IEnumerable<SpatialNode> ownedNodes, string prefabId)
        {
            var result = new ShipSaleDecrement();
            if (ownedNodes == null || string.IsNullOrEmpty(prefabId)) return result;
            foreach (SpatialNode src in ownedNodes)
            {
                if (src == null) continue;
                int listed;
                if (!src.ListedForSale.TryGetValue(prefabId, out listed) || listed <= 0) continue;

                int stock;
                src.ShipStock.TryGetValue(prefabId, out stock);
                int listedAfter = listed - 1;
                int stockAfter = Math.Max(0, stock - 1);
                if (listedAfter > 0) src.ListedForSale[prefabId] = listedAfter;
                else src.ListedForSale.Remove(prefabId);
                if (stockAfter > 0) src.ShipStock[prefabId] = stockAfter;
                else src.ShipStock.Remove(prefabId);

                result.Sold = true;
                result.NodeId = src.Id;
                result.ListedBefore = listed;
                result.ListedAfter = listedAfter;
                result.StockBefore = stock;
                result.StockAfter = stockAfter;
                return result;
            }
            return result;
        }
    }

    /// <summary>A grid purchase seen before SE decided it — who bought which prefab, from whom.</summary>
    public struct PendingShipSale
    {
        public string PrefabId;
        public long BuyerIdentityId;
        /// <summary>The selling faction's tag when the item bought was a Factions shelf listing;
        /// null for any other store.</summary>
        public string ShelfFactionTag;
        public DateTime AtUtc;
    }

    /// <summary>
    /// Purchases noted before SE's checks, closed only by the spawn that proves one succeeded. SE's
    /// grid purchase can refuse (PCU, limits, no free place) or fail its spawn after it was asked,
    /// and neither raises anything a plugin can hear, so a noted sale that no spawn closes within
    /// <see cref="Window"/> is dropped unaccounted. A spawn closes the oldest open sale of the same
    /// prefab, once.
    /// </summary>
    public sealed class StoreShipSaleLedger
    {
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

        private readonly List<PendingShipSale> _pending = new List<PendingShipSale>();

        public int PendingCount { get { return _pending.Count; } }

        public void Note(PendingShipSale sale)
        {
            if (string.IsNullOrEmpty(sale.PrefabId)) return;
            Expire(sale.AtUtc);
            _pending.Add(sale);
        }

        /// <summary>The sale a spawn of <paramref name="prefabId"/> at <paramref name="nowUtc"/>
        /// completes, removed from the ledger. False for a spawn no open sale accounts for.</summary>
        public bool TryConfirm(string prefabId, DateTime nowUtc, out PendingShipSale sale)
        {
            sale = default(PendingShipSale);
            if (string.IsNullOrEmpty(prefabId)) return false;
            Expire(nowUtc);
            for (int i = 0; i < _pending.Count; i++)
            {
                if (!string.Equals(_pending[i].PrefabId, prefabId, StringComparison.Ordinal)) continue;
                sale = _pending[i];
                _pending.RemoveAt(i);
                return true;
            }
            return false;
        }

        private void Expire(DateTime nowUtc)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (nowUtc - _pending[i].AtUtc > Window) _pending.RemoveAt(i);
        }
    }
}
