using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace Factions
{
    // Pre/post transaction snapshot shared by both patches below -- Harmony's __state handoff only
    // carries one value, so both the pre-amount (did the transaction actually complete?) and the
    // pre-balance (how much money genuinely moved?) travel together.
    internal struct StoreTxState
    {
        public int Amount;
        public long Balance;
    }

    static class StoreTransactionHelpers
    {
        /// <summary>Set on a Nexus agent whose stores the authority prices: its transactions are
        /// heard and sent to the authority to book.</summary>
        internal static volatile bool AgentStores;

        /// <summary>Whether this server books store transactions at all.</summary>
        internal static bool Active { get { return !SimulationGate.Deferred || AgentStores; } }

        // MyFactionStation.GetStoreItemById is internal to Sandbox.Game; StoreItems itself is
        // public, so a linear search here reproduces the same lookup from our assembly.
        internal static MyStoreItem FindStoreItem(IMyFactionStation station, long id)
        {
            var mfs = station as MyFactionStation;
            if (mfs == null) return null;
            foreach (MyStoreItem item in mfs.StoreItems)
                if (item.Id == id) return item;
            return null;
        }

        // The SE wallet is the authoritative record of what actually moved -- reading the real
        // balance delta (rather than recomputing price ourselves) transparently handles friendly-
        // relation price bonuses, transaction fees, and any other vanilla pricing quirk we don't
        // separately model.
        internal static long GetBalance(long factionId)
        {
            if (factionId == 0 || MyBankingSystem.Static == null) return 0;
            MyAccountInfo info;
            return MyBankingSystem.Static.TryGetAccountInfo(factionId, out info) ? info.Balance : 0;
        }

        // Who actually made the trade. Every patched store method carries the buyer as a `MyPlayer
        // player` parameter Harmony binds by name -- inferring it from position or from the store
        // item's owner would be wrong in exactly the crowded-station case that matters. 0 means
        // unattributed, which the downstream boundary treats as "record the goods, credit nobody".
        internal static long IdentityOf(MyPlayer player)
        {
            return player != null && player.Identity != null ? player.Identity.IdentityId : 0;
        }
    }

    /// <summary>
    /// Vanilla's <see cref="MyStoreBlock"/> never invokes <c>MyStoreItem.OnTransaction</c> for a
    /// purchase at an NPC/faction-owned station -- that delegate (the one <c>SetActions</c> wires up
    /// in <see cref="StationStoreManager"/>) is only fired by the separate, unrelated
    /// <c>PlayerItems</c> peer-trading path (<c>BuyPhysicalItemFromPlayer</c>). Confirmed by reading
    /// decompiled <c>MyStoreBlock.BuyPhysicalItemInternal</c>: it mutates <c>storeItem.Amount</c> and
    /// the SE wallet directly with no callback of any kind. Without this patch, every real player
    /// purchase at a Factions station is invisible to <c>SpatialNode.Inventory</c> and the sim's own
    /// treasury -- the item is delivered and money moves in the SE-native wallet, but the brain never
    /// sees either side of the trade.
    ///
    /// <para>Uses the standard Harmony prefix/postfix <c>__state</c> handoff to confirm the purchase
    /// actually completed (captured pre-amount vs. post-amount) rather than firing on vanilla's
    /// internal early-return failure paths (insufficient inventory space, etc.), and to capture the
    /// real SE wallet delta for lazily syncing into <c>WorldModel</c>'s own treasury (the sim ledger
    /// is authoritative; the SE wallet is a synced, not driving, copy -- see
    /// <see cref="StationStoreManager.HandleStationTransaction"/>).</para>
    /// </summary>
    [HarmonyPatch(typeof(MyStoreBlock), "BuyPhysicalItemInternal")]
    [PatchGroup("StoreTransactions")]
    static class BuyPhysicalItemInternalPatch
    {
        [HarmonyPrefix]
        static void Prefix(MyStoreItem storeItem, MyFaction faction, out StoreTxState __state)
        {
            if (!StoreTransactionHelpers.Active) { __state = new StoreTxState { Amount = -1, Balance = 0 }; return; }
            __state = new StoreTxState
            {
                Amount  = storeItem != null ? storeItem.Amount : -1,
                Balance = StoreTransactionHelpers.GetBalance(faction != null ? faction.FactionId : 0),
            };
        }

        [HarmonyPostfix]
        static void Postfix(long stationId, int amount, MyStoreItem storeItem, MyFaction faction, MyPlayer player, StoreTxState __state)
        {
            if (!StoreTransactionHelpers.Active) return;
            if (stationId == 0 || __state.Amount < 0 || storeItem == null || storeItem.Item == null) return;
            if (__state.Amount - storeItem.Amount != amount) return; // early-returned before completing

            long balanceAfter = StoreTransactionHelpers.GetBalance(faction != null ? faction.FactionId : 0);
            long creditsDelta = balanceAfter - __state.Balance;
            FactionsPlugin.Instance?.OnStationItemTransacted(stationId, storeItem.Item.Value, amount, isBuy: true,
                creditsDelta: creditsDelta, buyerIdentityId: StoreTransactionHelpers.IdentityOf(player));
        }
    }

    /// <summary>Same gap as <see cref="BuyPhysicalItemInternalPatch"/>, on the sell side. Confirmed
    /// by reading decompiled <c>MyStoreBlock.SellToStation</c>'s PhysicalItem branch.</summary>
    [HarmonyPatch(typeof(MyStoreBlock), "SellToStation")]
    [PatchGroup("StoreTransactions")]
    static class SellToStationPatch
    {
        [HarmonyPrefix]
        static void Prefix(long id, IMyFactionStation station, bool ignoreStation, out StoreTxState __state)
        {
            __state = new StoreTxState { Amount = -1, Balance = 0 };
            if (!StoreTransactionHelpers.Active) return;
            if (ignoreStation || station == null) return;
            MyStoreItem item = StoreTransactionHelpers.FindStoreItem(station, id);
            if (item == null) return;
            __state = new StoreTxState
            {
                Amount  = item.Amount,
                Balance = StoreTransactionHelpers.GetBalance(station.FactionId),
            };
        }

        [HarmonyPostfix]
        static void Postfix(long id, int amount, IMyFactionStation station, bool ignoreStation, MyPlayer player, StoreTxState __state)
        {
            if (!StoreTransactionHelpers.Active) return;
            if (ignoreStation || station == null || __state.Amount < 0) return;
            MyStoreItem item = StoreTransactionHelpers.FindStoreItem(station, id);
            if (item == null || item.Item == null) return;
            if (__state.Amount - item.Amount != amount) return; // early-returned before completing

            long balanceAfter = StoreTransactionHelpers.GetBalance(station.FactionId);
            long creditsDelta = balanceAfter - __state.Balance;
            FactionsPlugin.Instance?.OnStationItemTransacted(station.Id, item.Item.Value, amount, isBuy: false,
                creditsDelta: creditsDelta, buyerIdentityId: StoreTransactionHelpers.IdentityOf(player));
        }
    }
}
