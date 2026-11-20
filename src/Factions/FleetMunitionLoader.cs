using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Definitions;
using Sandbox.Game;                    // MyInventory
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;            // MyGunBase
using VRage;                           // MyFixedPoint
using VRage.Game;
using VRage.Game.ModAPI;               // IMyGunObject
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// Puts a fleet's or a station's drawn magazines onto its materialised grids and reads back what
    /// is left (FAC-386, FAC-441).
    ///
    /// <para><b>The prefab's own magazines are removed first.</b> A prefab spawns with ammunition
    /// aboard, and the model credits none of it: a grid that kept it would fight a player with
    /// rounds the faction never drew, and a read at despawn could not tell those rounds from the
    /// drawn ones. Only magazines a weapon on the grids can load are removed, from every inventory
    /// — a cargo hold feeds the guns over the conveyors — so unrelated loot stays where it was.</para>
    ///
    /// <para>Weapons are filled first. A load sized by estimated use (<c>FleetMunitions.SortieLoad</c>)
    /// is usually more than the guns hold, and the rest goes to the assembly's cargo containers,
    /// which feed the guns over the conveyors. What neither takes stays in the pool the caller
    /// passed in, for the caller to carry.</para>
    /// </summary>
    internal static class FleetMunitionLoader
    {
        /// <summary>Strips every gun-feeding magazine from <paramref name="grids"/>, then fills their
        /// weapons from <paramref name="pool"/>, taking what is loaded out of it.</summary>
        public static void StripAndLoad(IList<MyCubeGrid> grids, Dictionary<string, long> pool,
                                        out long stripped, out long loaded)
        {
            stripped = Strip(grids);
            loaded = Load(grids, pool);
        }

        /// <summary>Removes every gun-feeding magazine from every inventory on <paramref name="grids"/>.
        /// Returns how many it removed.</summary>
        public static long Strip(IList<MyCubeGrid> grids)
        {
            long stripped = 0;
            if (grids == null || grids.Count == 0) return 0;

            Dictionary<string, MyDefinitionId> accepted = AcceptedMagazines(grids);
            if (accepted.Count == 0) return 0;

            foreach (MyCubeGrid grid in grids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    if (block == null || !block.HasInventory) continue;
                    for (int i = 0; i < block.InventoryCount; i++)
                    {
                        MyInventory inv = block.GetInventory(i);
                        if (inv == null) continue;
                        foreach (KeyValuePair<string, MyDefinitionId> mag in accepted)
                        {
                            long have = (long)inv.GetItemAmount(mag.Value);
                            if (have <= 0) continue;
                            stripped += (long)inv.RemoveItemsOfType((MyFixedPoint)(double)have, mag.Value);
                        }
                    }
                }
            }
            return stripped;
        }

        /// <summary>Fills the weapons on <paramref name="grids"/> from <paramref name="pool"/>, then
        /// cargo, taking what is placed out of the pool. Returns how many it placed.</summary>
        public static long Load(IList<MyCubeGrid> grids, Dictionary<string, long> pool)
        {
            long loaded = 0;
            if (grids == null || grids.Count == 0 || pool == null || pool.Count == 0) return 0;
            Dictionary<string, MyDefinitionId> accepted = AcceptedMagazines(grids);
            if (accepted.Count == 0) return 0;

            foreach (MyCubeGrid grid in grids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    List<MyDefinitionId> magazines = MagazinesOf(block);
                    if (magazines == null || !block.HasInventory) continue;
                    MyInventory inv = block.GetInventory(0);
                    if (inv == null) continue;
                    for (int m = 0; m < magazines.Count; m++)
                    {
                        string key = KeyOf(magazines[m]);
                        long have;
                        if (key == null || !pool.TryGetValue(key, out have) || have <= 0) continue;
                        // AddItems and ComputeAmountThatFits skip the inventory's constraint.
                        if (!inv.CheckConstraint(magazines[m])) continue;
                        long room = (long)inv.ComputeAmountThatFits(magazines[m]);
                        long put = Math.Min(room, have);
                        if (put <= 0) continue;
                        var builder = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(magazines[m]);
                        if (!inv.AddItems((MyFixedPoint)(double)put, builder)) continue;
                        pool[key] = have - put;
                        loaded += put;
                    }
                }
            }

            // What the guns could not hold goes to cargo, where the conveyors feed it to them.
            foreach (MyCubeGrid grid in grids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    if (!(block is MyCargoContainer) || !block.HasInventory) continue;
                    MyInventory inv = block.GetInventory(0);
                    if (inv == null) continue;
                    foreach (KeyValuePair<string, MyDefinitionId> mag in accepted)
                    {
                        long have;
                        if (!pool.TryGetValue(mag.Key, out have) || have <= 0) continue;
                        if (!inv.CheckConstraint(mag.Value)) continue;
                        long room = (long)inv.ComputeAmountThatFits(mag.Value);
                        long put = Math.Min(room, have);
                        if (put <= 0) continue;
                        var builder = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(mag.Value);
                        if (!inv.AddItems((MyFixedPoint)(double)put, builder)) continue;
                        pool[mag.Key] = have - put;
                        loaded += put;
                    }
                }
            }
            return loaded;
        }

        /// <summary>What the weapons on <paramref name="grids"/> hold when full, by magazine key:
        /// each weapon's inventory filled with the first magazine it lists. The load for a station
        /// whose design the catalogue carries no munition data for.</summary>
        public static Dictionary<string, long> WeaponCapacity(IList<MyCubeGrid> grids)
        {
            var capacity = new Dictionary<string, long>(StringComparer.Ordinal);
            if (grids == null) return capacity;
            foreach (MyCubeGrid grid in grids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    List<MyDefinitionId> magazines = MagazinesOf(block);
                    if (magazines == null || !block.HasInventory) continue;
                    MyInventory inv = block.GetInventory(0);
                    MyAmmoMagazineDefinition def = MyDefinitionManager.Static.GetAmmoMagazineDefinition(magazines[0]);
                    string key = KeyOf(magazines[0]);
                    if (inv == null || def == null || def.Volume <= 0 || key == null) continue;
                    long fits = (long)Math.Floor((double)inv.MaxVolume / def.Volume);
                    if (fits <= 0) continue;
                    long held;
                    capacity.TryGetValue(key, out held);
                    capacity[key] = held + fits;
                }
            }
            return capacity;
        }

        /// <summary>Adds the gun-feeding magazines <paramref name="grids"/> hold, in every inventory,
        /// to <paramref name="into"/>. Returns how many it found.</summary>
        public static long Read(IList<MyCubeGrid> grids, Dictionary<string, long> into)
        {
            if (grids == null || grids.Count == 0 || into == null) return 0;
            Dictionary<string, MyDefinitionId> accepted = AcceptedMagazines(grids);
            long found = 0;
            foreach (MyCubeGrid grid in grids)
            {
                if (grid == null) continue;
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    if (block == null || !block.HasInventory) continue;
                    for (int i = 0; i < block.InventoryCount; i++)
                    {
                        MyInventory inv = block.GetInventory(i);
                        if (inv == null) continue;
                        foreach (KeyValuePair<string, MyDefinitionId> mag in accepted)
                        {
                            long have = (long)inv.GetItemAmount(mag.Value);
                            if (have <= 0) continue;
                            long prior;
                            into.TryGetValue(mag.Key, out prior);
                            into[mag.Key] = prior + have;
                            found += have;
                        }
                    }
                }
            }
            return found;
        }

        /// <summary>True when <paramref name="grids"/> carry at least one weapon this model recognises
        /// and none of them can fire: no gun-feeding magazine in any inventory, and no round left in
        /// any gun's loaded magazine. False for grids with no recognised weapon, which must not read
        /// as out of ammunition merely because nothing here can see their ammo.</summary>
        public static bool IsOutOfAmmunition(IList<MyCubeGrid> grids)
        {
            if (grids == null || grids.Count == 0) return false;
            bool anyWeapon = false;
            foreach (MyCubeGrid grid in grids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    if (MagazinesOf(block) == null) continue;
                    anyWeapon = true;
                    var gun = block as IMyGunObject<MyGunBase>;
                    if (gun != null && gun.GunBase != null && gun.GunBase.CurrentAmmo > 0) return false;
                }
            }
            if (!anyWeapon) return false;
            return Read(grids, new Dictionary<string, long>(StringComparer.Ordinal)) == 0;
        }

        /// <summary>Every magazine any weapon on <paramref name="grids"/> can load, by inventory key.</summary>
        internal static Dictionary<string, MyDefinitionId> AcceptedMagazines(IList<MyCubeGrid> grids)
        {
            var accepted = new Dictionary<string, MyDefinitionId>(StringComparer.Ordinal);
            foreach (MyCubeGrid grid in grids)
            {
                if (grid == null) continue;
                foreach (MyCubeBlock block in grid.GetFatBlocks())
                {
                    List<MyDefinitionId> magazines = MagazinesOf(block);
                    if (magazines == null) continue;
                    for (int m = 0; m < magazines.Count; m++)
                    {
                        string key = KeyOf(magazines[m]);
                        if (key != null && !accepted.ContainsKey(key)) accepted[key] = magazines[m];
                    }
                }
            }
            return accepted;
        }

        /// <summary>The magazines a weapon block can load, in definition order — the same list, and
        /// the same exclusion of interior turrets, the prefab scan prices. Null for anything else.</summary>
        private static List<MyDefinitionId> MagazinesOf(MyCubeBlock block)
        {
            if (block == null || !WeaponStatsHelper.IsScoreableWeapon(block.BlockDefinition)) return null;
            var weaponBlock = (MyWeaponBlockDefinition)block.BlockDefinition;
            MyWeaponDefinition weaponDef = MyDefinitionManager.Static.GetWeaponDefinition(weaponBlock.WeaponDefinitionId);
            if (weaponDef == null || weaponDef.AmmoMagazinesId == null || weaponDef.AmmoMagazinesId.Length == 0) return null;
            return new List<MyDefinitionId>(weaponDef.AmmoMagazinesId);
        }

        private static string KeyOf(MyDefinitionId magazine)
        {
            return InventoryKeyMapper.FromTypeIdAndSubtype(magazine.TypeId.ToString(), magazine.SubtypeName);
        }
    }
}
