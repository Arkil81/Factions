using System;
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Screens.Models;
using Sandbox.Game.Screens.ViewModels;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace FactionsUI.Trade;

/// <summary>
/// The "Ship inventory" entry in a store block's inventory dropdown: one selection standing for the
/// whole docked ship, so selling a hold full of ore — or buying enough to fill it — is not a walk
/// down the dropdown one container at a time.
///
/// SE's store protocol is per-entity. <c>SellItem</c>/<c>BuyItem</c> each carry ONE entity id, which
/// the server resolves to that entity's inventory 0 (<c>TryGetInventory</c> is
/// <c>GetInventory(0)</c>) and then moves the full amount or refuses it; nothing on the server
/// aggregates across a grid. So the entry is synthetic and the fan-out is client-side: this
/// class plans a transaction into per-entity chunks and <see cref="ShipTradeBatch"/> sends them one
/// at a time.
///
/// Two asymmetries between the directions are deliberate. Selling draws from every ship inventory
/// (ore sits in drills as readily as in containers, and since FAC-177 the Factions server
/// consolidates a block's own trays before it reads index 0, so an assembler's output counts too),
/// while buying fills <see cref="MyCargoContainer"/>s only — `BuyFromStation` writes to index 0 with
/// no such consolidation, and a purchase routed into a refinery's ore slot or a reactor's fuel slot
/// is not what "put it on my ship" means either way.
///
/// Only one store screen is open at a time, so the state is static and cleared on close.
/// </summary>
internal static class ShipInventory
{
    /// <summary>Dropdown label. Vanilla's own entries are localized; this one is not, in common
    /// with the rest of this plugin's UI text.</summary>
    public const string Label = "Ship inventory";

    /// <summary>One planned request: an entity id the server will accept, and the units to move
    /// against it.</summary>
    internal readonly struct Chunk
    {
        public readonly long EntityId;
        public readonly int Amount;

        /// <summary>This block holds the item across more than one of its own inventories, so the
        /// sale leans on the server reading and removing across all of them
        /// (<c>SellFromAllInventoriesPatch</c>, FAC-177) rather than on index 0 alone. Nothing is
        /// moved between trays, so this costs nothing — plain containers are simply drawn from
        /// first, keeping the ordinary sale on the path that needs no patch at all.</summary>
        public readonly bool DisturbsBlockInventories;

        public Chunk(long entityId, int amount) : this(entityId, amount, false) { }

        public Chunk(long entityId, int amount, bool disturbsBlockInventories)
        {
            EntityId = entityId;
            Amount = amount;
            DisturbsBlockInventories = disturbsBlockInventories;
        }
    }

    /// <summary>One block the server will accept a request against: its entity id and **every**
    /// inventory it has, index 0 first.
    ///
    /// <para>Index 0 is what SE itself reads — <c>MyEntityExtensions.TryGetInventory</c> is
    /// <c>GetInventory(0)</c> — but the Factions server's `SellInventoryConsolidationPatch`
    /// (FAC-177) moves a sale's shortfall into index 0 first, so a *sale* reaches the whole block
    /// while a *purchase* still lands in index 0 alone. Hence both are kept and the two directions
    /// read different things off this.</para></summary>
    internal readonly struct Target
    {
        public readonly long EntityId;
        public readonly List<MyInventory> Inventories;

        public Target(long entityId, List<MyInventory> inventories)
        {
            EntityId = entityId;
            Inventories = inventories;
        }

        /// <summary>Where a purchase lands. Unchanged by FAC-177 — that patch is on the sell path.</summary>
        public MyInventory Primary { get { return Inventories[0]; } }

        public bool IsMultiInventory { get { return Inventories.Count > 1; } }

        public MyFixedPoint Held(MyDefinitionId itemId)
        {
            MyFixedPoint held = 0;
            for (int i = 0; i < Inventories.Count; i++) held += Inventories[i].GetItemAmount(itemId);
            return held;
        }
    }

    private static MyInventoryTargetModel _marker;
    private static readonly List<long> Entities = new List<long>();

    /// <summary>The ship's own inventories, snapshotted when the entry is built. Vanilla's dropdown
    /// is a snapshot of the same moment — <c>Initialize</c> subscribes to exactly these and never
    /// revisits the list — so matching it keeps the two from disagreeing about what "the ship" is,
    /// and keeps the per-item change handler off an allocating rescan.</summary>
    private static readonly HashSet<MyInventoryBase> ShipInventories = new HashSet<MyInventoryBase>();

    /// <summary>The multi-request transaction in flight, or null. One at a time per screen: the
    /// store block keeps a single result-callback field, so a second request sent before the first
    /// answers would orphan it.</summary>
    public static ShipTradeBatch Batch;

    public static void Reset()
    {
        if (Batch != null) Batch.Abandon();
        Batch = null;
        _marker = null;
        Entities.Clear();
        ShipInventories.Clear();
    }

    /// <summary>
    /// Appends the entry to a dropdown vanilla has just rebuilt, given the same connected-grid
    /// entity list vanilla was handed. No entry at all when nothing is docked, or when the docked
    /// grid is not one the server would let this player trade from — an entry that could only ever
    /// fail is worse than none.
    /// </summary>
    public static void Install(MyInventoryTargetViewModel target, List<long> inventoryEntities)
    {
        Reset();
        if (target == null || target.Inventories == null || inventoryEntities == null) return;

        Entities.AddRange(inventoryEntities);
        List<Target> sources = SellSources();
        int reachable = sources.Count;
        if (reachable == 0)
        {
            Entities.Clear();
            return;
        }
        foreach (Target source in sources)
            foreach (MyInventory inventory in source.Inventories)
                ShipInventories.Add(inventory);

        _marker = new MyInventoryTargetModel(null)
        {
            Name = Label,
            // The character's own entity id, not a placeholder: the entry stays a legal target for
            // the paths this plugin does NOT intercept — a gas or ship offer, the deposit/withdraw
            // buttons — which then behave exactly as if Character were selected instead of sending
            // the server an entity id that resolves to nothing.
            EntityId = MySession.Static != null ? MySession.Static.LocalCharacterEntityId : 0L,
            // Vanilla's own aggregate-inventory flag. Nothing here needs it — the item list is
            // built below — but it makes the entry degrade into "all inventories" rather than into
            // an empty list if the content patch ever fails to apply.
            AllInventories = true,
        };
        RefreshVolume();

        // Straight after the character entry, which Initialize put at index 0 and left selected.
        // Inserting below the selection leaves it pointing where vanilla just aimed it.
        target.Inventories.Insert(Math.Min(1, target.Inventories.Count), _marker);

        // Once per store screen, not per frame. It is the only positive signal that this build is
        // the one running: everything else this feature does is silent until it fails.
        Plugin.Log("ship-inventory entry added, " + reachable + " reachable block"
                   + (reachable == 1 ? "" : "s") + " of " + inventoryEntities.Count + " docked");
    }

    /// <summary>Whether a changed inventory is one this entry is standing for. Called from SE's own
    /// per-item change event, so it must not allocate or walk the entity tree.</summary>
    public static bool Owns(MyInventoryBase inventory)
    {
        return inventory != null && ShipInventories.Contains(inventory);
    }

    public static bool IsSelected(MyInventoryTargetViewModel target)
    {
        if (_marker == null || target == null || target.Inventories == null) return false;
        int index = target.SelectedInventoryIndex;
        return index >= 0 && index < target.Inventories.Count
               && ReferenceEquals(target.Inventories[index], _marker);
    }

    /// <summary>Every ship block a sale can draw from, each carrying all of its own inventories.
    /// Filtered by the server's own acceptance rule
    /// (<c>SellToStation</c>/<c>BuyFromStation</c> both require the local identity to be a big owner
    /// of the grid) because a rejected request is logged and dropped server-side with no reply at
    /// all — a chunk sent to one would stall the batch rather than fail it.</summary>
    public static List<Target> SellSources()
    {
        var sources = new List<Target>();
        long identity = MySession.Static != null ? MySession.Static.LocalPlayerId : 0L;

        foreach (long entityId in Entities)
        {
            MyCubeBlock block;
            if (!MyEntities.TryGetEntityById(entityId, out block, allowClosed: false)) continue;
            if (block.CubeGrid == null || !block.CubeGrid.BigOwners.Contains(identity)) continue;

            var inventories = new List<MyInventory>();
            for (int i = 0; i < block.InventoryCount; i++)
            {
                MyInventory inventory = block.GetInventory(i);
                if (inventory != null) inventories.Add(inventory);
            }
            if (inventories.Count == 0) continue;

            sources.Add(new Target(entityId, inventories));
        }
        return sources;
    }

    /// <summary>Where a purchase lands: cargo containers only.</summary>
    public static List<Target> BuyTargets()
    {
        var targets = new List<Target>();
        foreach (Target source in SellSources())
        {
            MyCubeBlock block;
            if (!MyEntities.TryGetEntityById(source.EntityId, out block, allowClosed: false)) continue;
            if (!(block is MyCargoContainer)) continue;
            targets.Add(source);
        }
        return targets;
    }

    /// <summary>Units of <paramref name="itemId"/> the ship can actually sell. Floored **per
    /// block**, which is the unit one request covers: two containers holding 10.5 ore each can sell
    /// 20, not 21, but an assembler holding 10.5 across its input and output trays sells all 21 of
    /// its own, because the server consolidates within a block before it reads index 0.</summary>
    public static int SellableTotal(MyDefinitionId itemId)
    {
        long total = 0;
        foreach (Target source in SellSources())
            total += Units(source.Held(itemId));
        return Clamp(total);
    }

    /// <summary>Units of <paramref name="itemId"/> the ship's cargo containers have room for.</summary>
    public static int SpaceTotal(MyDefinitionId itemId)
    {
        long total = 0;
        foreach (Target target in BuyTargets())
        {
            if (!target.Primary.CheckConstraint(itemId)) continue;
            total += Units(target.Primary.ComputeAmountThatFits(itemId));
        }
        return Clamp(total);
    }

    /// <summary>Splits a sale across the ship's blocks: plain single-inventory ones first, fullest
    /// first within that, so the fewest requests carry it and a multi-inventory block is only
    /// disturbed when the rest of the ship cannot cover the sale. False — and no chunks — when the
    /// ship cannot cover <paramref name="amount"/> in full: a store transaction is all-or-nothing
    /// per request, and quietly selling less than the player asked for would be worse than saying
    /// so.</summary>
    public static bool PlanSell(MyDefinitionId itemId, int amount, out List<Chunk> plan)
    {
        var candidates = new List<Chunk>();
        foreach (Target source in SellSources())
        {
            int available = Clamp(Units(source.Held(itemId)));
            if (available > 0) candidates.Add(new Chunk(source.EntityId, available, source.IsMultiInventory));
        }
        return Fill(candidates, amount, out plan);
    }

    /// <summary>Splits a purchase across the ship's cargo containers, emptiest-first so one big
    /// buy fills them one after another rather than topping every container up by a sliver.</summary>
    public static bool PlanBuy(MyDefinitionId itemId, int amount, out List<Chunk> plan)
    {
        var candidates = new List<Chunk>();
        foreach (Target target in BuyTargets())
        {
            if (!target.Primary.CheckConstraint(itemId)) continue;
            int space = Clamp(Units(target.Primary.ComputeAmountThatFits(itemId)));
            if (space > 0) candidates.Add(new Chunk(target.EntityId, space));
        }
        return Fill(candidates, amount, out plan);
    }

    /// <summary>Draws <paramref name="amount"/> from the blocks that cost the least to touch —
    /// single-inventory first — and from the largest capacity within that.</summary>
    private static bool Fill(List<Chunk> candidates, int amount, out List<Chunk> plan)
    {
        plan = new List<Chunk>();
        if (amount <= 0) return false;

        candidates.Sort((a, b) =>
        {
            if (a.DisturbsBlockInventories != b.DisturbsBlockInventories)
                return a.DisturbsBlockInventories ? 1 : -1;
            return b.Amount.CompareTo(a.Amount);
        });

        int remaining = amount;
        foreach (Chunk candidate in candidates)
        {
            if (remaining <= 0) break;
            int take = Math.Min(remaining, candidate.Amount);
            if (take <= 0) continue;
            plan.Add(new Chunk(candidate.EntityId, take));
            remaining -= take;
        }

        if (remaining > 0)
        {
            plan.Clear();
            return false;
        }
        return true;
    }

    /// <summary>Rebuilds the entry's volume readout from the ship's own inventories, in litres, the
    /// same unit and scaling vanilla uses for a single container.</summary>
    public static void RefreshVolume()
    {
        if (_marker == null) return;

        float current = 0f, max = 0f;
        foreach (Target source in SellSources())
            foreach (MyInventory inventory in source.Inventories)
            {
                current += (float)inventory.CurrentVolume * 1000f;
                max += (float)inventory.MaxVolume * 1000f;
            }
        _marker.Volume = current;
        _marker.MaxVolume = max;
    }

    /// <summary>Fills <paramref name="items"/> with the ship's contents, one model per stack, the
    /// shape vanilla's own per-inventory listing produces.</summary>
    public static void CollectItems(List<MyInventoryItemModel> items)
    {
        foreach (Target source in SellSources())
            foreach (MyInventory inventory in source.Inventories)
                foreach (MyPhysicalInventoryItem item in inventory.GetItems())
                    items.Add(new MyInventoryItemModel(item, inventory));
    }

    /// <summary>Whole units of a fixed-point amount. Saturating rather than casting: an
    /// unconstrained inventory answers <see cref="MyFixedPoint.MaxValue"/>, whose raw value
    /// overflows a direct <c>(int)</c> conversion into a negative count.</summary>
    private static long Units(MyFixedPoint amount)
    {
        double units = (double)amount;
        if (units <= 0.0) return 0L;
        if (units >= int.MaxValue) return int.MaxValue;
        return (long)Math.Floor(units);
    }

    private static int Clamp(long value)
    {
        if (value <= 0L) return 0;
        return value >= int.MaxValue ? int.MaxValue : (int)value;
    }
}
