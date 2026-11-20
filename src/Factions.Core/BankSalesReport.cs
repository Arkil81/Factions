using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One item the central bank buys, as one faction sees it.</summary>
    public sealed class BankSalesRow
    {
        /// <summary>Inventory key, <c>Comp:SteelPlate</c>. What a rule and a command name.</summary>
        public string Key;
        public ProcessedGoodCategory Category;
        /// <summary>Held across every node the faction owns.</summary>
        public long Held;
        /// <summary>Book value per unit, before the bank's multiplier and the node's scarcity.</summary>
        public long UnitValue;
        public BankSaleMode Mode;
        /// <summary>The faction-wide target under <see cref="BankSaleMode.AboveTarget"/>; zero otherwise.</summary>
        public long Target;
    }

    /// <summary>
    /// The per-item bank-sale rules for one faction: every processed good the bank buys, with what the
    /// faction holds of it and the rule it set. Shared by the Store window's Bank sales list
    /// and the chat verb, so the two cannot list different items.
    ///
    /// <para>Rows are the union of the catalogue the host passes, everything the faction holds that
    /// the bank buys, and everything it has a rule for. The catalogue is what lets a player set a
    /// rule before owning any of the item -- the case of a faction holding no node at all.</para>
    /// </summary>
    public static class BankSalesReport
    {
        public static List<BankSalesRow> Build(WorldModel world, FactionState st, IEnumerable<string> catalogueKeys)
        {
            var rows = new List<BankSalesRow>();
            if (world == null || st == null) return rows;

            var held = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (SpatialNode n in world.Graph.OwnedBy(st.Tag))
                foreach (KeyValuePair<string, long> kv in n.Inventory)
                {
                    if (kv.Value <= 0 || !BankBuys(world, kv.Key)) continue;
                    long prior;
                    held.TryGetValue(kv.Key, out prior);
                    held[kv.Key] = prior + kv.Value;
                }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (catalogueKeys != null)
                foreach (string key in catalogueKeys)
                    if (!string.IsNullOrEmpty(key)) keys.Add(key);
            foreach (string key in held.Keys) keys.Add(key);
            foreach (string key in st.CentralBankDontSell) keys.Add(key);
            foreach (string key in st.CentralBankTargets.Keys) keys.Add(key);

            foreach (string key in keys)
            {
                long h;
                held.TryGetValue(key, out h);
                long target;
                BankSaleMode mode = st.CentralBankRuleFor(key, out target);
                rows.Add(new BankSalesRow
                {
                    Key = key,
                    Category = world.Registry != null ? world.Registry.CategoryFor(SubType(key)) : ProcessedGoodCategory.Component,
                    Held = h,
                    UnitValue = world.GetItemBookValue(key, ItemKind.ProcessedGood),
                    Mode = mode,
                    Target = target,
                });
            }
            rows.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return rows;
        }

        /// <summary>The bank's own kind rule: it buys processed goods and nothing else.</summary>
        private static bool BankBuys(WorldModel world, string key)
        {
            ItemKind kind;
            return world.TryGetInventoryItemKind(key, out kind) && kind == ItemKind.ProcessedGood;
        }

        /// <summary>The subtype a processed good's inventory key names.</summary>
        public static string SubType(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return key.StartsWith("Comp:", StringComparison.Ordinal) ? key.Substring(5) : key;
        }
    }
}
