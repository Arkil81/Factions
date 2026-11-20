using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="StoreShipSale"/> and <see cref="StoreShipSaleLedger"/>: a ship bought from a
    /// Factions shelf comes off <c>ListedForSale</c> and <c>ShipStock</c> once, and only when SE
    /// actually spawned it — a refused or failed purchase takes nothing off.
    /// </summary>
    public class StoreShipSaleTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc);

        private static SpatialNode Node(string id, int listed, int stock, string prefab = "Laevateinn")
        {
            var n = new SpatialNode { Id = id };
            if (listed > 0) n.ListedForSale[prefab] = listed;
            if (stock > 0) n.ShipStock[prefab] = stock;
            return n;
        }

        private static PendingShipSale Sale(string prefab, DateTime at, string shelf = "STCG")
        {
            return new PendingShipSale { PrefabId = prefab, BuyerIdentityId = 42, ShelfFactionTag = shelf, AtUtc = at };
        }

        /// <summary>The host's path in one place: a noted purchase, a spawn that may confirm it, and
        /// the decrement only for a confirmed shelf sale.</summary>
        private static ShipSaleDecrement Run(StoreShipSaleLedger ledger, List<SpatialNode> owned, string spawnedPrefab, DateTime spawnAt)
        {
            PendingShipSale sale;
            if (!ledger.TryConfirm(spawnedPrefab, spawnAt, out sale) || sale.ShelfFactionTag == null)
                return new ShipSaleDecrement();
            return StoreShipSale.SellListed(owned, sale.PrefabId);
        }

        [Fact]
        public void A_confirmed_sale_takes_one_off_listed_and_stock()
        {
            var owned = new List<SpatialNode> { Node("A", 0, 4), Node("B", 2, 3) };
            var ledger = new StoreShipSaleLedger();
            ledger.Note(Sale("Laevateinn", T0));

            ShipSaleDecrement d = Run(ledger, owned, "Laevateinn", T0.AddSeconds(2));

            Assert.True(d.Sold);
            Assert.Equal("B", d.NodeId);
            Assert.Equal(2, d.ListedBefore);
            Assert.Equal(1, d.ListedAfter);
            Assert.Equal(3, d.StockBefore);
            Assert.Equal(2, d.StockAfter);
            Assert.Equal(1, owned[1].ListedForSale["Laevateinn"]);
            Assert.Equal(2, owned[1].ShipStock["Laevateinn"]);
            Assert.Equal(4, owned[0].ShipStock["Laevateinn"]);
        }

        [Fact]
        public void The_last_listed_hull_sold_removes_both_keys()
        {
            var owned = new List<SpatialNode> { Node("A", 1, 1) };
            ShipSaleDecrement d = StoreShipSale.SellListed(owned, "Laevateinn");
            Assert.True(d.Sold);
            Assert.False(owned[0].ListedForSale.ContainsKey("Laevateinn"));
            Assert.False(owned[0].ShipStock.ContainsKey("Laevateinn"));
        }

        [Fact]
        public void A_refused_purchase_never_spawns_and_takes_nothing_off()
        {
            var owned = new List<SpatialNode> { Node("A", 2, 2) };
            var ledger = new StoreShipSaleLedger();
            ledger.Note(Sale("Laevateinn", T0));

            // No spawn follows. A later, unrelated spawn of the same prefab outside the window is not
            // this purchase.
            ShipSaleDecrement d = Run(ledger, owned, "Laevateinn", T0 + StoreShipSaleLedger.Window + TimeSpan.FromSeconds(1));

            Assert.False(d.Sold);
            Assert.Equal(2, owned[0].ListedForSale["Laevateinn"]);
            Assert.Equal(2, owned[0].ShipStock["Laevateinn"]);
            Assert.Equal(0, ledger.PendingCount);
        }

        [Fact]
        public void A_spawn_of_another_prefab_does_not_close_the_sale()
        {
            var owned = new List<SpatialNode> { Node("A", 2, 2) };
            var ledger = new StoreShipSaleLedger();
            ledger.Note(Sale("Laevateinn", T0));

            Assert.False(Run(ledger, owned, "Mago", T0.AddSeconds(1)).Sold);
            Assert.Equal(2, owned[0].ListedForSale["Laevateinn"]);
            Assert.Equal(1, ledger.PendingCount);
        }

        [Fact]
        public void One_purchase_is_counted_once_however_many_spawns_follow()
        {
            var owned = new List<SpatialNode> { Node("A", 3, 3) };
            var ledger = new StoreShipSaleLedger();
            ledger.Note(Sale("Laevateinn", T0));

            Assert.True(Run(ledger, owned, "Laevateinn", T0.AddSeconds(1)).Sold);
            Assert.False(Run(ledger, owned, "Laevateinn", T0.AddSeconds(2)).Sold);
            Assert.Equal(2, owned[0].ListedForSale["Laevateinn"]);
        }

        [Fact]
        public void A_purchase_from_a_store_that_is_not_a_factions_shelf_takes_nothing_off()
        {
            var owned = new List<SpatialNode> { Node("A", 2, 2) };
            var ledger = new StoreShipSaleLedger();
            ledger.Note(Sale("Laevateinn", T0, shelf: null));

            Assert.False(Run(ledger, owned, "Laevateinn", T0.AddSeconds(1)).Sold);
            Assert.Equal(2, owned[0].ListedForSale["Laevateinn"]);
        }

        [Fact]
        public void A_prefab_no_node_lists_changes_nothing()
        {
            var owned = new List<SpatialNode> { Node("A", 0, 5) };
            ShipSaleDecrement d = StoreShipSale.SellListed(owned, "Laevateinn");
            Assert.False(d.Sold);
            Assert.Equal(5, owned[0].ShipStock["Laevateinn"]);
        }
    }
}
