using System;
using System.Collections.Generic;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components;
using VRage.Network;

namespace Factions
{
    /// <summary>What a Services Terminal prefix hands its postfix: who is trading, what their wallet
    /// held before vanilla touched it, which SE station the terminal belongs to, and the census of
    /// the grid taken while it still existed. <c>Identity == 0</c> means the prefix declined and the
    /// postfix must do nothing.</summary>
    internal sealed class ServiceTxState
    {
        public long Identity;
        public long Balance;
        public long StationId;
        public int Blocks;
        public Dictionary<string, long> Components;
        public Dictionary<string, long> Cargo;

        public static readonly ServiceTxState Declined = new ServiceTxState();
    }

    /// <summary>Shared plumbing for the three terminal patches: resolving the calling player and
    /// the owning station from inside a server event, and taking grid censuses in inventory keys.</summary>
    static class ServicesTerminalHelpers
    {
        /// <summary>The player whose client raised the event vanilla is currently servicing. Valid
        /// only inside a <c>[Server]</c> event body, which is exactly where the patches run.</summary>
        internal static long CallerIdentity()
        {
            EndpointId sender = MyEventContext.Current.Sender;
            if (!sender.IsValid || MySession.Static == null || MySession.Static.Players == null) return 0;
            return MySession.Static.Players.TryGetIdentityId(sender.Value);
        }

        internal static long BalanceOf(long identityId)
        {
            if (identityId == 0 || MyBankingSystem.Static == null) return 0;
            MyAccountInfo info;
            return MyBankingSystem.Static.TryGetAccountInfo(identityId, out info) ? info.Balance : 0;
        }

        /// <summary>The SE station record the terminal block sits on, by the same lookup vanilla's own
        /// component uses; 0 when the block is not on a registered station grid.</summary>
        internal static long StationIdOf(MyEntityComponentBase component)
        {
            var block = component != null ? component.Entity as MyFunctionalBlock : null;
            if (block == null || block.CubeGrid == null || MySession.Static == null || MySession.Static.Factions == null) return 0;
            IMyFactionStation station = MySession.Static.Factions.GetStationByGridId(block.CubeGrid.EntityId);
            return station != null ? station.Id : 0;
        }

        /// <summary>Whether a patch should do any work for this terminal: live session, settlement
        /// on, and the terminal standing on a station the brain owns. Cheap, so it runs before the
        /// census rather than after.</summary>
        internal static bool TryBeginFor(MyEntityComponentBase component, out long stationId)
        {
            stationId = 0;
            if (!StoreTransactionHelpers.Active) return false;
            FactionsPlugin plugin = FactionsPlugin.Instance;
            if (plugin == null || !plugin.ServicesSettlementReady) return false;
            stationId = StationIdOf(component);
            return stationId != 0 && plugin.IsFactionsStation(stationId);
        }

        /// <summary>The blocks vanilla prices: for every block in the mechanical group, the definition's
        /// component list minus what <c>GetMissingComponents</c> reports absent, as
        /// <c>Comp:&lt;Subtype&gt;</c> units. Cargo aboard goes into <paramref name="cargo"/> under the
        /// inventory key the store uses for the same item, gas skipped because a gas has no item.</summary>
        internal static int SalvageCensus(MyCubeGrid grid, Dictionary<string, long> components, Dictionary<string, long> cargo)
        {
            var group = new List<MyCubeGrid>();
            MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, group);
            return CensusGrids(group, components, cargo);
        }

        /// <summary>The same census over an explicitly supplied set of grids. The terminal hands it a
        /// mechanical group; <see cref="DebrisAbsorbManager"/> hands it a physical one, because that
        /// is the set SE's trash criteria judge and its remover closes.</summary>
        internal static int CensusGrids(IEnumerable<MyCubeGrid> grids, Dictionary<string, long> components, Dictionary<string, long> cargo)
        {
            int blocks = 0;
            var missing = new Dictionary<string, int>();
            foreach (MyCubeGrid g in grids)
            {
                if (g == null) continue;
                foreach (MySlimBlock block in g.CubeBlocks)
                {
                    if (block == null || block.BlockDefinition == null) continue;
                    blocks++;
                    missing.Clear();
                    block.GetMissingComponents(missing);
                    AddPresentComponents(block.BlockDefinition, missing, components);
                    if (block.FatBlock != null && block.FatBlock.HasInventory)
                        AddCargo(block.FatBlock, cargo);
                }
            }
            return blocks;
        }

        /// <summary>The components vanilla's repair conjures: the missing list of every damaged,
        /// repairable block in the group. Returns false when the group carries a block hostile to the
        /// caller, in which case vanilla refuses the job and there is nothing to census.</summary>
        internal static bool RepairCensus(MyCubeGrid grid, Dictionary<string, long> components)
        {
            var group = new List<MyCubeGrid>();
            MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, group);
            var friendlyIdentities = new HashSet<long>();
            var friendlyFactions = new HashSet<long>();
            MyServiceHostileBlocksHelper.BuildFriendlySets(grid, friendlyIdentities, friendlyFactions);
            var missing = new Dictionary<string, int>();
            foreach (MyCubeGrid g in group)
            {
                if (g == null) continue;
                foreach (MySlimBlock block in g.CubeBlocks)
                {
                    if (block == null || block.BlockDefinition == null) continue;
                    if (MyServiceHostileBlocksHelper.IsBlockHostile(block, friendlyIdentities, friendlyFactions)) return false;
                    bool damaged = !block.IsFullIntegrity || block.HasDeformation;
                    if (!damaged || !block.BlockDefinition.Repairable) continue;
                    missing.Clear();
                    block.GetMissingComponents(missing);
                    foreach (KeyValuePair<string, int> kv in missing)
                    {
                        if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                        Add(components, "Comp:" + kv.Key, kv.Value);
                    }
                }
            }
            return true;
        }

        private static void AddPresentComponents(MyCubeBlockDefinition def, Dictionary<string, int> missing, Dictionary<string, long> into)
        {
            if (def.Components == null) return;
            // A definition can list one subtype twice (the terminal itself lists SteelPlate at both
            // ends of its stack), so total per subtype first, then subtract what is missing once.
            var present = new Dictionary<string, long>();
            foreach (MyCubeBlockDefinition.Component c in def.Components)
            {
                if (c == null || c.Definition == null || c.Count <= 0) continue;
                string subtype = c.Definition.Id.SubtypeName;
                if (string.IsNullOrEmpty(subtype)) continue;
                Add(present, subtype, c.Count);
            }
            foreach (KeyValuePair<string, long> kv in present)
            {
                int gone;
                missing.TryGetValue(kv.Key, out gone);
                long units = kv.Value - gone;
                if (units > 0) Add(into, "Comp:" + kv.Key, units);
            }
        }

        /// <summary>Everything in one block's inventories under the key the store uses for the same
        /// item, gas skipped because a gas has no item. Internal rather than private since FAC-283:
        /// the terminal's "Send to Node" takes the same census off a live hull, and a second
        /// implementation of it would be a second set of keys.</summary>
        internal static void AddCargo(MyCubeBlock fat, Dictionary<string, long> into)
        {
            for (int i = 0; i < fat.InventoryCount; i++)
            {
                MyInventory inv = fat.GetInventory(i);
                if (inv == null) continue;
                foreach (MyPhysicalInventoryItem item in inv.GetItems())
                {
                    if (item.Content == null) continue;
                    MyDefinitionId id = item.Content.GetId();
                    string key = Factions.Core.InventoryKeyMapper.FromTypeIdAndSubtype(id.TypeId.ToString(), id.SubtypeName);
                    if (string.IsNullOrEmpty(key) || Factions.Core.InventoryKeyMapper.IsGasKey(key)) continue;
                    int amount = item.Amount.ToIntSafe();
                    if (amount > 0) Add(into, key, amount);
                }
            }
        }

        private static void Add(Dictionary<string, long> into, string key, long units)
        {
            long cur;
            into.TryGetValue(key, out cur);
            into[key] = cur + units;
        }
    }

    /// <summary>
    /// Vanilla's <c>MySalvageServiceComponent.AcceptOffer</c> pays the selling player out of nothing
    /// (<c>MyBankingSystem.ChangeBalance(player, +payout)</c> with no counterparty) and then closes
    /// every grid in the wreck's mechanical group, so the parts it "bought" cease to exist. It raises
    /// no callback. Read from the decompiled 1.210 body; provenance FAC-280.
    ///
    /// <para>The prefix takes the census while the grid still exists; the postfix confirms the sale by
    /// the player's real wallet gain -- every vanilla refusal (stale quote, hostile block, missing
    /// account, closing grid) leaves the balance untouched -- and hands the settlement to the sim,
    /// where the faction pays and the parts land in the node.</para>
    /// </summary>
    [HarmonyPatch(typeof(MySalvageServiceComponent), "AcceptOffer")]
    static class SalvageAcceptOfferPatch
    {
        [HarmonyPrefix]
        static void Prefix(MySalvageServiceComponent __instance, MyObjectBuilder_SalvageServiceQuote quote, out ServiceTxState __state)
        {
            __state = ServiceTxState.Declined;
            try
            {
                long stationId;
                if (quote == null || !ServicesTerminalHelpers.TryBeginFor(__instance, out stationId)) return;
                long identity = ServicesTerminalHelpers.CallerIdentity();
                if (identity == 0) return;
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById<MyCubeGrid>(quote.GridEntityId, out grid, false) || grid == null) return;

                var state = new ServiceTxState
                {
                    Identity   = identity,
                    Balance    = ServicesTerminalHelpers.BalanceOf(identity),
                    StationId  = stationId,
                    Components = new Dictionary<string, long>(),
                    Cargo      = new Dictionary<string, long>(),
                };
                state.Blocks = ServicesTerminalHelpers.SalvageCensus(grid, state.Components, state.Cargo);
                __state = state;
            }
            catch (Exception ex)
            {
                __state = ServiceTxState.Declined;
                FactionsPlugin.Log.Warn(ex, "Salvage census failed; the sale settles in vanilla only.");
            }
        }

        [HarmonyPostfix]
        static void Postfix(ServiceTxState __state)
        {
            if (__state == null || __state.Identity == 0) return;
            long gained = ServicesTerminalHelpers.BalanceOf(__state.Identity) - __state.Balance;
            if (gained <= 0) return; // refused, or paid nothing
            FactionsPlugin.Instance?.OnSalvageSettled(__state.StationId, __state.Identity, gained,
                __state.Blocks, __state.Components, __state.Cargo);
        }
    }

    /// <summary>Same gap on the repair side: <c>MyRepairServiceComponent.Repair</c> charges the player
    /// and nobody receives, and the components it restores come from nowhere. The census is the
    /// missing-component list of every damaged, repairable block, taken in the prefix because after
    /// the repair nothing is missing.
    ///
    /// <para><b>At a station Factions runs, vanilla's repair does not run at all</b>: the Repair tab
    /// there is Restore's (<see cref="RestorationService"/>, docs/ship-restoration.md), one function
    /// at one price. A client without the plugin that still presses vanilla's accept is answered
    /// "grid state changed" and nothing is charged. The settlement below remains for a station whose
    /// node is unowned or the claimable neutral's — the stations that keep vanilla's tab.</para></summary>
    [HarmonyPatch(typeof(MyRepairServiceComponent), "Repair")]
    static class RepairServicePatch
    {
        private static readonly System.Reflection.MethodInfo SendRepairResult =
            AccessTools.Method(typeof(MyRepairServiceComponent), "SendRepairResult");

        [HarmonyPrefix]
        static bool Prefix(MyRepairServiceComponent __instance, MyObjectBuilder_ServiceQuoteData quote, out ServiceTxState __state)
        {
            __state = ServiceTxState.Declined;
            try
            {
                if (IsRestoreStation(__instance))
                {
                    FactionsPlugin.Log.Info("VANILLA_REPAIR_REFUSED grid=" + (quote != null ? quote.GridEntityId : 0)
                                          + " player=" + ServicesTerminalHelpers.CallerIdentity() + " reason=restore-station");
                    if (SendRepairResult != null) SendRepairResult.Invoke(__instance, new object[] { Sandbox.ModAPI.RepairResult.GridStateChanged });
                    return false;
                }
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Repair takeover check failed; this repair runs in vanilla.");
            }
            try
            {
                long stationId;
                if (quote == null || !ServicesTerminalHelpers.TryBeginFor(__instance, out stationId)) return true;
                long identity = ServicesTerminalHelpers.CallerIdentity();
                if (identity == 0) return true;
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById<MyCubeGrid>(quote.GridEntityId, out grid, false) || grid == null) return true;

                var components = new Dictionary<string, long>();
                if (!ServicesTerminalHelpers.RepairCensus(grid, components)) return true; // hostile: vanilla refuses

                __state = new ServiceTxState
                {
                    Identity   = identity,
                    Balance    = ServicesTerminalHelpers.BalanceOf(identity),
                    StationId  = stationId,
                    Components = components,
                };
            }
            catch (Exception ex)
            {
                __state = ServiceTxState.Declined;
                FactionsPlugin.Log.Warn(ex, "Repair census failed; the repair settles in vanilla only.");
            }
            return true;
        }

        /// <summary>The terminal stands on a station Factions runs, and Restore is up to serve it.</summary>
        private static bool IsRestoreStation(MyRepairServiceComponent component)
        {
            FactionsPlugin plugin = FactionsPlugin.Instance;
            if (plugin == null || plugin.Restoration == null || SimulationGate.Deferred) return false;
            var block = component != null ? component.Entity as MyCubeBlock : null;
            return block != null && block.CubeGrid != null
                && TerminalAccess.IsFactionsStationGrid(plugin.Runtime, block.CubeGrid.EntityId);
        }

        [HarmonyPostfix]
        static void Postfix(ServiceTxState __state)
        {
            if (__state == null || __state.Identity == 0) return;
            long paid = __state.Balance - ServicesTerminalHelpers.BalanceOf(__state.Identity);
            if (paid <= 0) return; // refused, or a free repair
            FactionsPlugin.Instance?.OnRepairSettled(__state.StationId, __state.Identity, paid, __state.Components);
        }
    }

    /// <summary>The grid-storage expedite fee (<c>GridStorageExpediteCostPerSecond</c> times the
    /// remaining countdown) is the only money that moves in grid storage, and it too goes into the
    /// void. A pure service fee: no census, the wallet delta is the whole settlement.</summary>
    [HarmonyPatch(typeof(MyGridsStorageEntityComponent), "ExpediteCurrentClientOrderEndpoint")]
    static class GridStorageExpeditePatch
    {
        [HarmonyPrefix]
        static void Prefix(MyGridsStorageEntityComponent __instance, out ServiceTxState __state)
        {
            __state = ServiceTxState.Declined;
            try
            {
                long stationId;
                if (!ServicesTerminalHelpers.TryBeginFor(__instance, out stationId)) return;
                long identity = ServicesTerminalHelpers.CallerIdentity();
                if (identity == 0) return;
                __state = new ServiceTxState
                {
                    Identity  = identity,
                    Balance   = ServicesTerminalHelpers.BalanceOf(identity),
                    StationId = stationId,
                };
            }
            catch (Exception ex)
            {
                __state = ServiceTxState.Declined;
                FactionsPlugin.Log.Warn(ex, "Expedite prefix failed; the fee settles in vanilla only.");
            }
        }

        [HarmonyPostfix]
        static void Postfix(ServiceTxState __state)
        {
            if (__state == null || __state.Identity == 0) return;
            long paid = __state.Balance - ServicesTerminalHelpers.BalanceOf(__state.Identity);
            if (paid <= 0) return;
            FactionsPlugin.Instance?.OnExpediteSettled(__state.StationId, __state.Identity, paid);
        }
    }
}
