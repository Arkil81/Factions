using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Definitions;             // MyContainerTypeDefinition, MyDefinitionManager, MyPhysicalItemDefinition
using Sandbox.Game;                    // MyInventory
using Sandbox.Game.Entities;           // MyCubeGrid, MyCargoContainer
using Sandbox.Game.Entities.Cube;      // MyTerminalBlock
using SpaceEngineers.Game.EntityComponents.Blocks;   // MyRandomCargoEntityComponent
using VRage;                           // MyFixedPoint
using VRage.Game;                      // MyDefinitionId, MyObjectBuilder_PhysicalObject
using VRage.ObjectBuilders;            // MyObjectBuilderSerializer

namespace Factions
{
    /// <summary>
    /// The SE half of <see cref="StationLoot"/>: finds the blocks a station design tags with a vanilla
    /// loot table, rolls each table, places what the node's stock covers, and reads back what is left.
    ///
    /// <para><b>A loot block is one that carries a <c>ContainerType</c></b>, the way vanilla's own
    /// encounter spawn reads it: a cargo container's own field, or the
    /// <see cref="MyRandomCargoEntityComponent"/> on a barrel, crate, bed or locker. Vanilla rolls these
    /// only for a grid spawned with <c>SpawningOptions.SpawnRandomCargo</c>, which a Factions station is
    /// not, so before this every tag on a station was inert. Untagged blocks are not loot.</para>
    ///
    /// <para><b>Whatever a loot block holds when the station stands is cleared first.</b> A prefab's
    /// inventories are copied verbatim, so anything in them was created by the spawn and owned by
    /// nobody; leaving it would put free goods beside drawn ones and let a read-back credit them.</para>
    ///
    /// <para>Magazines a gun on the station can load are not placed: they are the station's
    /// ammunition, drawn and read by <see cref="FleetMunitionLoader"/> across every inventory, and a
    /// second record of the same rounds would credit them twice.</para>
    /// </summary>
    internal static class StationLootLoader
    {
        private sealed class Table
        {
            public LootTable Loot;
            public Dictionary<string, MyDefinitionId> Defs;
        }

        // Per ContainerType subtype; null when SE has no such definition (vanilla ships at least one,
        // FactorumStories, that no .sbc defines).
        private static readonly Dictionary<string, Table> Tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        // Every key a table has ever named, for the read-back.
        private static readonly Dictionary<string, MyDefinitionId> KeyDefs = new Dictionary<string, MyDefinitionId>(StringComparer.Ordinal);

        /// <summary>Every block on <paramref name="grids"/> with a loot table, and the table's name.</summary>
        public static void LootBlocks(IList<MyCubeGrid> grids, List<KeyValuePair<MyCubeBlock, string>> into)
        {
            into.Clear();
            if (grids == null) return;
            for (int g = 0; g < grids.Count; g++)
            {
                MyCubeGrid grid = grids[g];
                if (grid == null || grid.MarkedForClose) continue;
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    if (block == null || !block.HasInventory || block.MarkedForClose) continue;
                    string tag = ContainerTypeOf(block);
                    if (!string.IsNullOrEmpty(tag)) into.Add(new KeyValuePair<MyCubeBlock, string>(block, tag));
                }
            }
        }

        /// <summary>Clears each loot block, rolls its table, draws what the node can spare and places it.
        /// Returns the units placed; <paramref name="returned"/> is what was drawn and found no room, and
        /// <paramref name="cleared"/> the item stacks the spawn left in the blocks. With
        /// <paramref name="draw"/> false the blocks are only cleared: a station still restocking
        /// stands empty.</summary>
        public static long Stand(WorldModel world, SpatialNode node, IList<MyCubeGrid> grids,
                                 List<KeyValuePair<MyCubeBlock, string>> blocks, bool skipGunMagazines,
                                 bool draw, IRandom rng, out long returned, out long cleared)
        {
            returned = 0;
            cleared = 0;
            if (world == null || node == null || blocks == null || blocks.Count == 0) return 0;
            List<Dictionary<string, long>> wants = Prepare(grids, blocks, skipGunMagazines, draw ? rng : null, out cleared);
            if (!draw) return 0;
            long placed = 0;
            var unplaced = new Dictionary<string, long>(StringComparer.Ordinal);
            // Block by block: what one block could not hold is back in stock before the next draws.
            for (int i = 0; i < wants.Count; i++)
            {
                if (wants[i].Count == 0) continue;
                Dictionary<string, long> drawn = StationLoot.Draw(world, node, wants[i], world.Tuning);
                if (drawn.Count == 0) continue;
                unplaced.Clear();
                placed += PlaceOne(blocks[i], drawn, unplaced);
                if (unplaced.Count > 0) returned += StationLoot.ReturnUnplaced(world, node, unplaced);
            }
            return placed;
        }

        /// <summary>The block half of a stand, for a station whose stock is on another server: clears
        /// every loot block and, with an <paramref name="rng"/>, rolls each one's table. Returns one want
        /// per block, in <paramref name="blocks"/> order (empty for a block with no table, or with no
        /// rng); gas and the guns' own magazines are never wanted.</summary>
        public static List<Dictionary<string, long>> Prepare(IList<MyCubeGrid> grids,
                                                             List<KeyValuePair<MyCubeBlock, string>> blocks,
                                                             bool skipGunMagazines, IRandom rng, out long cleared)
        {
            cleared = 0;
            var wants = new List<Dictionary<string, long>>(blocks == null ? 0 : blocks.Count);
            if (blocks == null) return wants;
            Dictionary<string, MyDefinitionId> guns = skipGunMagazines && rng != null
                ? FleetMunitionLoader.AcceptedMagazines(grids)
                : null;
            for (int i = 0; i < blocks.Count; i++)
            {
                var want = new Dictionary<string, long>(StringComparer.Ordinal);
                wants.Add(want);
                MyCubeBlock block = blocks[i].Key;
                MyInventory inv = block.GetInventory(0);
                if (inv == null) continue;
                cleared += inv.ItemCount;
                inv.Clear();
                var terminal = block as MyTerminalBlock;
                if (terminal != null) terminal.ShowInInventory = true;   // vanilla's station cache hides cargo
                if (rng == null) continue;
                Table table = TableFor(blocks[i].Value);
                if (table == null) continue;
                foreach (KeyValuePair<string, long> kv in StationLoot.Roll(table.Loot, rng))
                    if (!InventoryKeyMapper.IsGasKey(kv.Key) && (guns == null || !guns.ContainsKey(kv.Key)))
                        want[kv.Key] = kv.Value;
            }
            return wants;
        }

        /// <summary>Places <paramref name="pools"/> (one per block, in <paramref name="blocks"/> order)
        /// and returns the units placed; <paramref name="unplaced"/> sums what found no room.</summary>
        public static long Place(List<KeyValuePair<MyCubeBlock, string>> blocks, IList<Dictionary<string, long>> pools,
                                 out Dictionary<string, long> unplaced)
        {
            unplaced = new Dictionary<string, long>(StringComparer.Ordinal);
            long placed = 0;
            if (blocks == null || pools == null) return 0;
            for (int i = 0; i < blocks.Count && i < pools.Count; i++)
                if (pools[i] != null && pools[i].Count > 0) placed += PlaceOne(blocks[i], pools[i], unplaced);
            return placed;
        }

        /// <summary>Places one block's pool; adds what found no room to <paramref name="unplaced"/>.</summary>
        private static long PlaceOne(KeyValuePair<MyCubeBlock, string> entry, Dictionary<string, long> pool,
                                     Dictionary<string, long> unplaced)
        {
            long placed = 0;
            MyCubeBlock block = entry.Key;
            MyInventory inv = block == null || block.MarkedForClose ? null : block.GetInventory(0);
            Table table = inv == null ? null : TableFor(entry.Value);
            foreach (KeyValuePair<string, long> d in pool)
            {
                long put = table == null ? 0 : Place(inv, table.Defs, d.Key, d.Value);
                placed += put;
                if (put < d.Value)
                {
                    long sum;
                    unplaced.TryGetValue(d.Key, out sum);
                    unplaced[d.Key] = sum + d.Value - put;
                }
            }
            return placed;
        }

        /// <summary>What the loot blocks on <paramref name="grids"/> hold of every key a table has
        /// named, into <paramref name="observed"/>.</summary>
        public static void Read(IList<MyCubeGrid> grids, Dictionary<string, long> observed)
        {
            observed.Clear();
            var blocks = new List<KeyValuePair<MyCubeBlock, string>>();
            LootBlocks(grids, blocks);
            for (int i = 0; i < blocks.Count; i++)
            {
                MyInventory inv = blocks[i].Key.GetInventory(0);
                if (inv == null) continue;
                foreach (KeyValuePair<string, MyDefinitionId> kd in KeyDefs)
                {
                    long have = (long)Math.Floor((double)inv.GetItemAmount(kd.Value));
                    if (have <= 0) continue;
                    long sum;
                    observed.TryGetValue(kd.Key, out sum);
                    observed[kd.Key] = sum + have;
                }
            }
        }

        private static string ContainerTypeOf(MyCubeBlock block)
        {
            var cargo = block as MyCargoContainer;
            if (cargo != null && !string.IsNullOrEmpty(cargo.ContainerType)) return cargo.ContainerType;
            MyRandomCargoEntityComponent random;
            if (block.Components != null && block.Components.TryGet(out random) && random != null)
                return random.ContainerType;
            return null;
        }

        private static long Place(MyInventory inv, Dictionary<string, MyDefinitionId> defs, string key, long amount)
        {
            MyDefinitionId defId;
            if (amount <= 0 || !defs.TryGetValue(key, out defId)) return 0;
            if (!inv.CheckConstraint(defId)) return 0;
            long room = (long)Math.Floor((double)inv.ComputeAmountThatFits(defId));
            long put = Math.Min(room, amount);
            if (put <= 0) return 0;
            var builder = MyObjectBuilderSerializer.CreateNewObject(defId) as MyObjectBuilder_PhysicalObject;
            if (builder == null || !inv.AddItems((MyFixedPoint)(double)put, builder)) return 0;
            return put;
        }

        private static Table TableFor(string containerType)
        {
            Table table;
            if (Tables.TryGetValue(containerType, out table)) return table;
            table = Build(MyDefinitionManager.Static.GetContainerTypeDefinition(containerType));
            Tables[containerType] = table;
            return table;
        }

        private static Table Build(MyContainerTypeDefinition def)
        {
            if (def == null || def.Items == null || def.Items.Length == 0) return null;
            var table = new Table
            {
                Loot = new LootTable { CountMin = def.CountMin, CountMax = def.CountMax },
                Defs = new Dictionary<string, MyDefinitionId>(StringComparer.Ordinal)
            };
            for (int i = 0; i < def.Items.Length; i++)
                table.Loot.Items.Add(Row(def.Items[i], table.Defs));
            if (def.Sets != null)
                foreach (KeyValuePair<string, List<MyContainerTypeDefinition.ContainerTypeItem>> set in def.Sets)
                {
                    var rows = new List<LootTableItem>(set.Value.Count);
                    for (int i = 0; i < set.Value.Count; i++) rows.Add(Row(set.Value[i], table.Defs));
                    table.Loot.Sets[set.Key] = rows;
                }
            return table;
        }

        private static LootTableItem Row(MyContainerTypeDefinition.ContainerTypeItem item, Dictionary<string, MyDefinitionId> defs)
        {
            string key = InventoryKeyMapper.FromTypeIdAndSubtype(item.DefinitionId.TypeId.ToString(), item.DefinitionId.SubtypeName);
            MyPhysicalItemDefinition physical;
            bool integral = MyDefinitionManager.Static.TryGetPhysicalItemDefinition(item.DefinitionId, out physical)
                            && physical != null && physical.HasIntegralAmounts;
            if (key != null)
            {
                defs[key] = item.DefinitionId;
                KeyDefs[key] = item.DefinitionId;
            }
            return new LootTableItem
            {
                Key = key,
                AmountMin = (double)item.AmountMin,
                AmountMax = (double)item.AmountMax,
                Frequency = item.Frequency,
                Integral = integral,
                Set = item.Set
            };
        }
    }
}
