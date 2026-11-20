using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One row of a <see cref="LootTable"/>: an item, the amount one pick of it places, and its
    /// weight. Mirrors SE's <c>MyContainerTypeDefinition.ContainerTypeItem</c>, keyed by inventory key.</summary>
    public sealed class LootTableItem
    {
        public string Key;
        public double AmountMin;
        public double AmountMax;
        public float Frequency;
        /// <summary>The item comes in whole units (a component, a tool); a fractional roll rounds up,
        /// as SE's does. Everything else rounds down, because node stock holds whole units.</summary>
        public bool Integral;
        /// <summary>When set, a pick of this row places every row of the named set instead.</summary>
        public string Set;
    }

    /// <summary>A block's loot table: SE's <c>MyContainerTypeDefinition</c> with its items keyed by
    /// inventory key, so the roll can run without SE.</summary>
    public sealed class LootTable
    {
        public int CountMin;
        public int CountMax;
        public List<LootTableItem> Items = new List<LootTableItem>();
        public Dictionary<string, List<LootTableItem>> Sets =
            new Dictionary<string, List<LootTableItem>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Loot in a materialised station's tagged containers, drawn from its node's stock instead of
    /// created: rolled from the block's own vanilla table when the station stands, capped by what
    /// the node can spare, and credited back when it despawns. What players took is gone from stock.
    ///
    /// <para>The shape is <see cref="StationMunitions"/>'s. <see cref="SpatialNode.StationLoot"/> is
    /// the record of what was drawn and is still aboard; a credit never exceeds it, and a read only
    /// ever lowers it. A station grid is never saved, so a record found at boot, or found when the
    /// station stands again, was aboard a grid that no longer exists and is handed back in full.</para>
    ///
    /// <para>What a draw may take is <see cref="OperationsManager.SurplusOf(SpatialNode, string, WorldModel, FactionTuning)"/>:
    /// the stock left after an active build's claim, the node's fuel and Stock keeps, procurement holds
    /// and Haul claims. Loot is the one consumer that hands everything it does not lose straight back,
    /// so it takes only what nothing else is waiting for.</para>
    /// </summary>
    public static class StationLoot
    {
        /// <summary>How long a station's loot blocks stay empty after players take from them: a
        /// despawn that finds anything gone starts it, and every spawn inside it stands with nothing
        /// drawn, so one station cannot be emptied again by leaving and coming back.</summary>
        public static readonly TimeSpan RestockCooldown = TimeSpan.FromHours(1);

        /// <summary>Whether <paramref name="node"/>'s station is still restocking at
        /// <paramref name="nowUtc"/>; <paramref name="left"/> is the time remaining.</summary>
        public static bool Restocking(SpatialNode node, DateTime nowUtc, out TimeSpan left)
        {
            left = TimeSpan.Zero;
            if (node == null || node.LootRestockUtc <= nowUtc) return false;
            left = node.LootRestockUtc - nowUtc;
            return true;
        }

        /// <summary>What players near a station are told as it stands: when its stores restock while
        /// <see cref="Restocking"/>, that it holds supplies when <paramref name="placed"/> is non-zero,
        /// and nothing otherwise.</summary>
        public static string SpawnNotice(string stationName, SpatialNode node, DateTime nowUtc, long placed)
        {
            string name = string.IsNullOrEmpty(stationName) ? (node != null ? node.Id : "The station") : stationName;
            TimeSpan left;
            if (Restocking(node, nowUtc, out left))
            {
                int minutes = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
                return name + ": stores were raided. They restock in " + minutes + (minutes == 1 ? " minute." : " minutes.");
            }
            return placed > 0 ? name + ": stores are stocked." : null;
        }

        /// <summary>What one block's table places, by key: SE's own roll
        /// (<c>MyInventory.GenerateContent</c>). A pick count from the table's range, then that many
        /// rows drawn by weight without replacement, each placing a uniform amount from its range. Keys
        /// picked twice (a set and a row naming the same item) add.
        ///
        /// <para>The pick count's upper bound is exclusive unless the range is a single value, because
        /// SE draws it with <c>Random.Next(min, max)</c>.</para></summary>
        public static Dictionary<string, long> Roll(LootTable table, IRandom rng)
        {
            var placed = new Dictionary<string, long>(StringComparer.Ordinal);
            if (table == null || table.Items == null || table.Items.Count == 0 || rng == null) return placed;

            int lo = Math.Min(table.CountMin, table.CountMax);
            int hi = Math.Max(table.CountMin, table.CountMax);
            int count = lo == hi ? lo : lo + (int)Math.Floor(rng.NextDouble() * (hi - lo));

            var taken = new bool[table.Items.Count];
            for (int pick = 0; pick < count; pick++)
            {
                int row = PickRow(table.Items, taken, rng);
                if (row < 0) break;
                taken[row] = true;
                LootTableItem item = table.Items[row];
                List<LootTableItem> set;
                if (!string.IsNullOrEmpty(item.Set) && table.Sets != null && table.Sets.TryGetValue(item.Set, out set))
                {
                    for (int s = 0; s < set.Count; s++) Place(placed, set[s], rng);
                }
                else Place(placed, item, rng);
            }
            return placed;
        }

        /// <summary>Draws, per key, what <paramref name="want"/> asks for as far as the node's surplus
        /// goes, and adds it to the record. Returns what was drawn, for the host to place; whatever it
        /// cannot place goes back through <see cref="ReturnUnplaced"/>.</summary>
        public static Dictionary<string, long> Draw(WorldModel world, SpatialNode node,
                                                    IDictionary<string, long> want, FactionTuning tuning)
        {
            var drawn = new Dictionary<string, long>(StringComparer.Ordinal);
            if (world == null || node == null || want == null) return drawn;
            foreach (KeyValuePair<string, long> w in want)
            {
                if (w.Value <= 0 || string.IsNullOrEmpty(w.Key)) continue;
                long spare = OperationsManager.SurplusOf(node, w.Key, world, tuning ?? world.Tuning);
                long take = Math.Min(w.Value, spare);
                if (take <= 0) continue;
                world.AddNodeInventory(node.Id, w.Key, -take);
                AddToRecord(node, w.Key, take);
                drawn[w.Key] = take;
            }
            return drawn;
        }

        /// <summary>Returns to stock what a <see cref="Draw"/> took and the block had no room for,
        /// never more than the record holds. Returns the units returned.</summary>
        public static long ReturnUnplaced(WorldModel world, SpatialNode node, IDictionary<string, long> unplaced)
        {
            if (world == null || node == null || unplaced == null || node.StationLoot == null) return 0;
            long returned = 0;
            foreach (KeyValuePair<string, long> left in unplaced)
            {
                long held;
                if (left.Value <= 0 || !node.StationLoot.TryGetValue(left.Key, out held)) continue;
                long back = Math.Min(left.Value, held);
                if (back <= 0) continue;
                world.AddNodeInventory(node.Id, left.Key, back);
                AddToRecord(node, left.Key, -back);
                returned += back;
            }
            TrimRecord(node);
            return returned;
        }

        /// <summary>A station despawning: lowers the record to what its loot blocks were read to hold
        /// (never raising it, so nothing a player dropped in is credited), credits the rest to stock
        /// and clears it. Returns the units credited; <paramref name="taken"/> is what the read found
        /// gone, which players carried off and which is not returned. Anything taken starts
        /// <see cref="RestockCooldown"/>.</summary>
        public static long Credit(WorldModel world, SpatialNode node, IDictionary<string, long> observed, out long taken)
        {
            taken = 0;
            if (node == null || node.StationLoot == null) return 0;
            var keys = new List<string>(node.StationLoot.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                long had = node.StationLoot[keys[i]];
                long seen = 0;
                if (observed != null) observed.TryGetValue(keys[i], out seen);
                long now = seen < 0 ? 0 : Math.Min(had, seen);
                if (now == had) continue;
                node.StationLoot[keys[i]] = now;
                taken += had - now;
            }
            if (taken > 0 && world != null) node.LootRestockUtc = world.UtcNow + RestockCooldown;
            return CreditRecord(world, node);
        }

        /// <summary>Hands back a record left by a grid that closed without a read — any record still
        /// standing when its station stands again. Returns the units credited.</summary>
        public static long CreditStale(WorldModel world, SpatialNode node)
        {
            return node == null ? 0 : CreditRecord(world, node);
        }

        /// <summary>Hands back every record in the world in full: after a restart no station grid
        /// stands. Returns the units credited; <paramref name="nodes"/> is how many nodes held any.</summary>
        public static long CreditAfterRestart(WorldModel world, out int nodes)
        {
            nodes = 0;
            if (world == null) return 0;
            long credited = 0;
            foreach (SpatialNode node in world.Graph.Nodes.Values)
            {
                if (node.StationLoot == null) continue;
                long back = CreditRecord(world, node);
                if (back > 0) nodes++;
                credited += back;
            }
            return credited;
        }

        /// <summary>Units on the record, summed over keys.</summary>
        public static long Aboard(SpatialNode node)
        {
            long total = 0;
            if (node != null && node.StationLoot != null)
                foreach (KeyValuePair<string, long> held in node.StationLoot)
                    if (held.Value > 0) total += held.Value;
            return total;
        }

        private static int PickRow(List<LootTableItem> items, bool[] taken, IRandom rng)
        {
            double total = 0;
            for (int i = 0; i < items.Count; i++)
                if (!taken[i] && items[i] != null && items[i].Frequency > 0) total += items[i].Frequency;
            if (total <= 0) return -1;
            double roll = rng.NextDouble() * total;
            int last = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (taken[i] || items[i] == null || items[i].Frequency <= 0) continue;
                last = i;
                roll -= items[i].Frequency;
                if (roll < 0) return i;
            }
            return last;
        }

        private static void Place(Dictionary<string, long> placed, LootTableItem item, IRandom rng)
        {
            if (item == null || string.IsNullOrEmpty(item.Key)) return;
            double lo = Math.Min(item.AmountMin, item.AmountMax);
            double hi = Math.Max(item.AmountMin, item.AmountMax);
            double amount = lo + rng.NextDouble() * (hi - lo);
            long units = item.Integral ? (long)Math.Ceiling(amount) : (long)Math.Floor(amount);
            if (units <= 0) return;
            long have;
            placed.TryGetValue(item.Key, out have);
            placed[item.Key] = have + units;
        }

        private static long CreditRecord(WorldModel world, SpatialNode node)
        {
            if (world == null || node.StationLoot == null) return 0;
            long credited = 0;
            foreach (KeyValuePair<string, long> held in node.StationLoot)
            {
                if (held.Value <= 0) continue;
                world.AddNodeInventory(node.Id, held.Key, held.Value);
                credited += held.Value;
            }
            node.StationLoot = null;
            return credited;
        }

        private static void AddToRecord(SpatialNode node, string key, long delta)
        {
            if (node.StationLoot == null)
                node.StationLoot = new Dictionary<string, long>(StringComparer.Ordinal);
            long held;
            node.StationLoot.TryGetValue(key, out held);
            long next = held + delta;
            node.StationLoot[key] = next < 0 ? 0 : next;
        }

        private static void TrimRecord(SpatialNode node)
        {
            if (node.StationLoot == null) return;
            foreach (KeyValuePair<string, long> held in node.StationLoot)
                if (held.Value > 0) return;
            node.StationLoot = null;
        }
    }
}
