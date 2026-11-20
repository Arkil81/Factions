using System;
using System.Collections.Generic;
using FactionsUI.Patches;
using FactionsUI.Trade;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Screens.Models;
using Sandbox.Game.Screens.ViewModels;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;

namespace FactionsUI.Nodes;

/// <summary>
/// Where a node's goods go, and where a deposit comes from. Resolved, never picked: at a founded
/// station the answer is already on screen — the store block's own inventory dropdown, including the
/// "Ship inventory" entry <see cref="ShipInventory"/> adds — and offering a second list beside it
/// would let the two disagree about what "my ship" means.
///
/// <para>Away from a store block there is no dropdown to read, so the target is the thing the player
/// is currently flying, and their suit when they are not flying anything. Either way the resolved
/// target is named on screen rather than chosen there.</para>
///
/// <para><b>The two directions read different blocks</b>, the same asymmetry
/// <see cref="ShipInventory"/> records for buying and selling: a withdrawal fills cargo containers
/// only, because dropping ore into a refinery's ore slot or a reactor's fuel tray is not what "put it
/// on my ship" means, while a deposit draws from every inventory a block has, because ore sits in
/// drills as readily as in containers.</para>
///
/// <para><b>No amount is decided here.</b> The lists are entity ids and nothing else: the server is
/// what knows an item's volume, and each transfer leg asks for the whole outstanding amount and is
/// told what actually fit.</para>
/// </summary>
internal sealed class TransferTarget
{
    /// <summary>What the window says the goods are moving to and from.</summary>
    internal string Label { get; private set; }

    /// <summary>Entity ids a withdrawal may fill, in the order the legs will try them.</summary>
    internal readonly List<long> WithdrawInto = new List<long>();

    /// <summary>Entity ids a deposit may draw from.</summary>
    internal readonly List<long> DepositFrom = new List<long>();

    /// <summary>Every inventory behind <see cref="DepositFrom"/>, so the window can say how much of
    /// each key is actually to hand without asking the server a second question.</summary>
    private readonly List<MyInventory> _sources = new List<MyInventory>();

    internal bool CanWithdraw { get { return WithdrawInto.Count > 0; } }
    internal bool CanDeposit { get { return DepositFrom.Count > 0; } }

    /// <summary>
    /// The current target. Rebuilt on every refresh rather than cached: a ship undocks, a container
    /// is destroyed, and the store dropdown's selection changes under the window while it is open.
    /// </summary>
    internal static TransferTarget Resolve()
    {
        var target = new TransferTarget();
        try
        {
            if (target.ResolveFromStoreScreen()) return target;
            target.ResolveFromPlayer();
        }
        catch (Exception ex)
        {
            Plugin.Log("transfer target resolution failed: " + ex);
        }
        return target;
    }

    /// <summary>The store block's own dropdown, when one is open. False when there is no store
    /// screen or its selection cannot be read, which is the node-window case.</summary>
    private bool ResolveFromStoreScreen()
    {
        MyStoreBlockViewModel store = StoreNodeTransferPatch.ViewModel;
        if (store == null) return false;

        MyInventoryTargetViewModel inventoryTarget = store.InventoryTargetViewModel;
        if (inventoryTarget == null || inventoryTarget.Inventories == null) return false;

        if (ShipInventory.IsSelected(inventoryTarget))
        {
            Label = ShipInventory.Label;
            foreach (ShipInventory.Target source in ShipInventory.BuyTargets())
                WithdrawInto.Add(source.EntityId);
            foreach (ShipInventory.Target source in ShipInventory.SellSources())
            {
                DepositFrom.Add(source.EntityId);
                _sources.AddRange(source.Inventories);
            }
            return CanWithdraw || CanDeposit;
        }

        int index = inventoryTarget.SelectedInventoryIndex;
        if (index < 0 || index >= inventoryTarget.Inventories.Count) return false;
        MyInventoryTargetModel selected = inventoryTarget.Inventories[index];
        if (selected == null || selected.EntityId == 0) return false;

        Label = string.IsNullOrEmpty(selected.Name) ? "Selected inventory" : selected.Name;
        WithdrawInto.Add(selected.EntityId);
        DepositFrom.Add(selected.EntityId);
        CollectInventories(selected.EntityId);
        return true;
    }

    /// <summary>The ship the player is flying, or their suit. The top-most controlled entity is a
    /// grid exactly when they are at a control seat, which is the case worth spreading a transfer
    /// across; standing inside one, they are their own container.</summary>
    private void ResolveFromPlayer()
    {
        if (MySession.Static == null) return;

        var grid = MySession.Static.TopMostControlledEntity as MyCubeGrid;
        if (grid != null && ResolveFromGrid(grid)) return;

        MyCharacter character = MySession.Static.LocalCharacter;
        if (character == null) return;

        Label = "Your suit";
        WithdrawInto.Add(character.EntityId);
        DepositFrom.Add(character.EntityId);
        MyInventory suit = character.GetInventory();
        if (suit != null) _sources.Add(suit);
    }

    /// <summary>Every block of the controlled grid the local player is a big owner of — the same
    /// ownership rule the store's own transfers are accepted under, so the window never offers a
    /// container the server would refuse.</summary>
    private bool ResolveFromGrid(MyCubeGrid grid)
    {
        long identity = MySession.Static.LocalPlayerId;
        if (grid.BigOwners == null || !grid.BigOwners.Contains(identity)) return false;

        foreach (MyCubeBlock block in grid.GetFatBlocks())
        {
            var inventories = new List<MyInventory>();
            for (int i = 0; i < block.InventoryCount; i++)
            {
                MyInventory inventory = block.GetInventory(i);
                if (inventory != null) inventories.Add(inventory);
            }
            if (inventories.Count == 0) continue;

            DepositFrom.Add(block.EntityId);
            _sources.AddRange(inventories);
            if (block is MyCargoContainer) WithdrawInto.Add(block.EntityId);
        }

        if (!CanWithdraw && !CanDeposit) return false;
        Label = string.IsNullOrEmpty(grid.DisplayName) ? "This ship" : grid.DisplayName;
        return true;
    }

    private void CollectInventories(long entityId)
    {
        MyEntity entity;
        if (!MyEntities.TryGetEntityById(entityId, out entity, allowClosed: false) || entity == null) return;
        for (int i = 0; i < entity.InventoryCount; i++)
        {
            MyInventory inventory = entity.GetInventory(i);
            if (inventory != null) _sources.Add(inventory);
        }
    }

    // ---- What is to hand ----------------------------------------------------------------------

    /// <summary>
    /// Whole units of each transferable key the target currently holds, keyed the way the node keys
    /// its own stock.
    ///
    /// <para>This is what makes a Deposit tab useful: a station about to build something holds none
    /// of the component it is short of, so a list of what the NODE has would offer nothing to put
    /// in. Anything outside ore, ingots and components is left out rather than listed and refused —
    /// a tool or an ammo box entering node stock would be a channel nothing consumes.</para>
    /// </summary>
    internal Dictionary<string, long> Held()
    {
        var held = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (MyInventory inventory in _sources)
        {
            if (inventory == null) continue;
            foreach (MyPhysicalInventoryItem item in inventory.GetItems())
            {
                if (item.Content == null) continue;
                string key = ItemNames.Key(item.Content.GetId());
                if (key == null) continue;
                long units = Units(item.Amount);
                if (units <= 0) continue;
                long running;
                held[key] = held.TryGetValue(key, out running) ? running + units : units;
            }
        }
        return held;
    }

    /// <summary>Whole units of a fixed-point amount, saturating rather than casting — the same guard
    /// <see cref="ShipInventory"/> records, where an unconstrained inventory's
    /// <see cref="MyFixedPoint.MaxValue"/> overflows a direct conversion into a negative count.</summary>
    private static long Units(MyFixedPoint amount)
    {
        double units = (double)amount;
        if (units <= 0.0) return 0L;
        if (units >= long.MaxValue) return long.MaxValue;
        return (long)Math.Floor(units);
    }
}
