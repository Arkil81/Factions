using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// The gas sibling of <see cref="BuyPhysicalItemInternalPatch"/>, and deliberately a separate
    /// patch rather than a widening of it.
    ///
    /// <para>Both existing postfixes bail on <c>storeItem.Item == null</c>, which is true of
    /// <b>every</b> gas row by construction — a gas <c>MyStoreItem</c> carries no
    /// <c>MyDefinitionId</c> at all and is identified by its <c>ItemTypes</c>. So the existing patch
    /// is correctly inert for gas and must stay that way; relaxing that null check would let a gas
    /// row through a path that then has no item to name.</para>
    ///
    /// <para>Without this, a player's gas purchase delivers gas and moves credits in SE while
    /// <c>SpatialNode.Inventory</c> and the faction treasury never see it — the same failure
    /// <see cref="StoreTransactionPatches"/> exists to prevent on the physical-item side.</para>
    ///
    /// <para>Patched on <c>BuyGasStation</c> rather than <c>BuyGasInternal</c> because only the
    /// former carries the <see cref="IMyFactionStation"/>; the mutation it delegates to still
    /// happens inside the wrapped call, so the prefix/postfix amount diff works the same.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyStoreBlock), "BuyGasStation")]
    static class BuyGasStationPatch
    {
        [HarmonyPrefix]
        static void Prefix(MyStoreItem storeItem, IMyFactionStation station, out StoreTxState __state)
        {
            __state = new StoreTxState { Amount = -1, Balance = 0 };
            if (storeItem == null || station == null) return;
            __state = new StoreTxState
            {
                Amount  = storeItem.Amount,
                Balance = StoreTransactionHelpers.GetBalance(station.FactionId),
            };
        }

        [HarmonyPostfix]
        static void Postfix(MyStoreItem storeItem, int amount, IMyFactionStation station,
                            MyDefinitionId gasId, MyPlayer player, StoreTxState __state)
        {
            if (storeItem == null || station == null || __state.Amount < 0) return;
            // BuyGasInternal has several early returns (tank full, wrong inventory, not enough gas in
            // the source tanks) that leave Amount untouched. Only a completed purchase moves it.
            if (__state.Amount - storeItem.Amount != amount) return;

            string invKey = InventoryKeyMapper.FromTypeIdAndSubtype(
                gasId.TypeId.ToString(), gasId.SubtypeName);
            if (string.IsNullOrEmpty(invKey)) return;

            long balanceAfter = StoreTransactionHelpers.GetBalance(station.FactionId);
            long creditsDelta = balanceAfter - __state.Balance;

            // The store trades kilolitres; node inventory counts litres. Debiting the store amount
            // directly would under-debit the node by a factor of a thousand.
            long litres = GasStoreUnits.ToLitres(amount);
            if (litres <= 0 || litres > int.MaxValue) return;

            FactionsPlugin.Instance?.OnStationItemTransacted(
                station.Id, invKey, (int)litres, isBuy: true, creditsDelta: creditsDelta,
                buyerIdentityId: StoreTransactionHelpers.IdentityOf(player));
        }
    }
}
