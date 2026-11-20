using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Screens.ViewModels;

namespace FactionsUI.Trade;

/// <summary>
/// Drives one "Ship inventory" transaction: the per-entity chunks <see cref="ShipInventory"/>
/// planned, sent to the server one at a time.
///
/// Sequential, not fanned out, for a hard reason: <c>MyStoreBlock</c> holds exactly ONE pending
/// result callback per direction, so a second request raised before the first replies overwrites
/// the delegate and the first answer lands nowhere. Each chunk therefore waits for its own reply
/// and the next goes out from that callback.
///
/// The player sees one transaction, not a burst: intermediate replies are swallowed, and only the
/// aggregate — total units moved, or the first failure — is handed to vanilla's own result handler,
/// which owns the error box, the store refresh and the balance update.
/// </summary>
internal sealed class ShipTradeBatch
{
    /// <summary>A reply that never arrives would leave the batch — and the Buy/Sell button —
    /// waiting forever. The server drops a request it refuses without replying at all, so this is
    /// the only backstop.</summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);

    private static readonly MethodInfo CreateBuyRequestMethod =
        AccessTools.Method(typeof(MyStoreBlock), "CreateBuyRequest");
    private static readonly MethodInfo OnSellResultMethod =
        AccessTools.Method(typeof(MyStoreBlockViewModel), "OnSellResult");
    private static readonly MethodInfo OnBuyResultMethod =
        AccessTools.Method(typeof(MyStoreBlockViewModel), "OnBuyResult");

    private readonly MyStoreBlockViewModel _viewModel;
    private readonly MyStoreBlock _block;
    private readonly bool _selling;
    private readonly long _storeItemId;
    private readonly double _economyTick;
    private readonly List<ShipInventory.Chunk> _chunks;

    private int _sent;
    private int _moved;
    private bool _finished;
    private DateTime _awaitingSinceUtc;

    private ShipTradeBatch(MyStoreBlockViewModel viewModel, MyStoreBlock block, bool selling,
        long storeItemId, double economyTick, List<ShipInventory.Chunk> chunks)
    {
        _viewModel = viewModel;
        _block = block;
        _selling = selling;
        _storeItemId = storeItemId;
        _economyTick = economyTick;
        _chunks = chunks;
    }

    /// <summary>Starts the transaction. False means nothing was sent and the caller should let
    /// vanilla run its single-target request instead — including when one of the non-public members
    /// this leans on has moved in an SE update, which is the one failure a player would otherwise
    /// experience as a dead button.</summary>
    public static bool Start(MyStoreBlockViewModel viewModel, MyStoreBlock block, bool selling,
        long storeItemId, double economyTick, List<ShipInventory.Chunk> chunks)
    {
        if (viewModel == null || block == null || chunks == null || chunks.Count == 0) return false;

        if (OnSellResultMethod == null || OnBuyResultMethod == null
            || (!selling && CreateBuyRequestMethod == null))
        {
            Plugin.Log("ship-inventory disabled: SE's store block no longer exposes the members it "
                       + "needs (CreateBuyRequest/OnSellResult/OnBuyResult)");
            return false;
        }

        var batch = new ShipTradeBatch(viewModel, block, selling, storeItemId, economyTick, chunks);
        ShipInventory.Batch = batch;

        // Both flags belong to the transaction, not to one request: without this the button stays
        // live between chunks and a second click would start a competing batch.
        if (selling) viewModel.IsSellEnabled = false;
        else viewModel.IsBuyEnabled = false;

        try
        {
            batch.SendNext();
        }
        catch (Exception ex)
        {
            // Nothing is in flight, so the batch must not be left holding the screen's only slot.
            batch._finished = true;
            ShipInventory.Batch = null;
            if (selling) viewModel.IsSellEnabled = true;
            else viewModel.IsBuyEnabled = true;
            Plugin.Log("ship-inventory first request failed: " + ex);
            return false;
        }
        return true;
    }

    /// <summary>Drops a batch whose screen has gone: a reply still in flight then lands on a dead
    /// view model rather than driving a refresh of a window nobody is looking at.</summary>
    public void Abandon()
    {
        _finished = true;
    }

    /// <summary>Called once per frame from the store screen's own update.</summary>
    public void Tick()
    {
        if (_finished) return;
        if (DateTime.UtcNow - _awaitingSinceUtc < ReplyTimeout) return;

        Plugin.Log("ship-inventory " + (_selling ? "sell" : "buy") + " timed out after "
                   + _moved + " unit(s); the server did not answer chunk " + _sent
                   + " of " + _chunks.Count);
        Finish(MyStoreSellItemResults.ItemsTimeout, MyStoreBuyItemResults.ItemsTimeout);
    }

    private void SendNext()
    {
        if (_sent >= _chunks.Count)
        {
            Finish(MyStoreSellItemResults.Success, MyStoreBuyItemResults.Success);
            return;
        }

        ShipInventory.Chunk chunk = _chunks[_sent++];
        _awaitingSinceUtc = DateTime.UtcNow;

        if (_selling)
        {
            _block.CreateSellItemRequest(_storeItemId, chunk.Amount, chunk.EntityId, _economyTick,
                OnSellChunk);
        }
        else
        {
            // internal on MyStoreBlock — the sell twin is public, this one is not.
            CreateBuyRequestMethod.Invoke(_block, new object[]
            {
                _storeItemId, chunk.Amount, chunk.EntityId, _economyTick,
                (Action<MyStoreBuyItemResult>)OnBuyChunk,
            });
        }
    }

    private void OnSellChunk(MyStoreSellItemResult result)
    {
        if (_finished) return;
        if (result == null || result.Result != MyStoreSellItemResults.Success)
        {
            Finish(result != null ? result.Result : MyStoreSellItemResults.ItemNotFound,
                MyStoreBuyItemResults.ItemNotFound);
            return;
        }
        _moved += result.Amount;
        SendNext();
    }

    private void OnBuyChunk(MyStoreBuyItemResult result)
    {
        if (_finished) return;
        if (result == null || result.Result != MyStoreBuyItemResults.Success)
        {
            Finish(MyStoreSellItemResults.ItemNotFound,
                result != null ? result.Result : MyStoreBuyItemResults.ItemNotFound);
            return;
        }
        _moved += result.Amount;
        SendNext();
    }

    /// <summary>Ends the batch and reports it once, through the vanilla handler for this direction
    /// so the error text, the store refresh and the balance update are all SE's own. Only the
    /// parameter matching this batch's own direction is read; the other is inert.</summary>
    private void Finish(MyStoreSellItemResults sellResult, MyStoreBuyItemResults buyResult)
    {
        if (_finished) return;
        _finished = true;
        if (ReferenceEquals(ShipInventory.Batch, this)) ShipInventory.Batch = null;

        // A backstop, not the mechanism. The live path is SE's own per-item change event, which
        // StoreShipInventoryPatch unblocks; this rebuild runs at result time, which is *before* the
        // removals replicate back, so on its own it would read the hold as still full. It is here
        // for the case where an event is missed entirely.
        try
        {
            ShipInventory.RefreshVolume();
            if (_viewModel.InventoryTargetViewModel != null)
                _viewModel.InventoryTargetViewModel.UpdateInventoryContent();
        }
        catch (Exception ex)
        {
            Plugin.Log("ship-inventory listing refresh failed: " + ex);
        }

        Report(_viewModel, _selling, _storeItemId, _moved, sellResult, buyResult);
    }

    /// <summary>Hands one outcome to the vanilla result handler for this direction, which owns the
    /// error box, the store refresh and the balance update. Also the way a plan that never got off
    /// the ground — not enough on board, no room in the hold — is reported, so a refusal reads
    /// exactly like SE's own.</summary>
    public static void Report(MyStoreBlockViewModel viewModel, bool selling, long storeItemId,
        int amount, MyStoreSellItemResults sellResult, MyStoreBuyItemResults buyResult)
    {
        try
        {
            if (selling)
            {
                OnSellResultMethod.Invoke(viewModel, new object[]
                {
                    new MyStoreSellItemResult
                    {
                        Result = sellResult, ItemId = storeItemId, Amount = amount,
                    },
                });
            }
            else
            {
                OnBuyResultMethod.Invoke(viewModel, new object[]
                {
                    new MyStoreBuyItemResult
                    {
                        Result = buyResult, ItemId = storeItemId, Amount = amount,
                    },
                });
            }
        }
        catch (Exception ex)
        {
            // Reporting failing must not also leave the button dead.
            if (selling) viewModel.IsSellEnabled = true;
            else viewModel.IsBuyEnabled = true;
            Plugin.Log("ship-inventory result reporting failed: " + ex);
        }
    }
}
