using System;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SpaceEngineers.Game.EntityComponents.Blocks;
using Torch.API.Managers;
using Torch.Managers.ChatManager;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components;

namespace Factions
{
    /// <summary>
    /// The host half of docs/diplomacy-plan.md rule 2: a customer whom a station's owner is barred
    /// from trading with is refused at that owner's store blocks and Services Terminal. The rule
    /// itself is <see cref="StoreAccess.Decide"/> in Core; this resolves its three inputs from SE,
    /// traces a refusal and tells the player why.
    ///
    /// <para><b>Scope:</b> stations on a node Factions tracks whose owner is a registered NPC
    /// faction. A player faction's station, an unregistered owner and a station Factions does not
    /// know are served as vanilla serves them.</para>
    ///
    /// <para>On a Nexus agent the faction states are the authority's, so the authority judges every
    /// registered pair into the snapshot and the agent reads the owner's row
    /// (<see cref="StoreAccess.DecideRelayed"/>).</para>
    /// </summary>
    internal static class StoreRefusal
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        private static readonly RefusalNoticeThrottle Throttle = new RefusalNoticeThrottle();
        private const string NoticeAuthor = "Factions";

        /// <summary>Whether <paramref name="identityId"/> may trade with the owner of
        /// <paramref name="ownerTag"/>'s station. Served whenever an input cannot be read: the bar is
        /// a diplomatic rule, and a store that shut on a lookup failure would read as a fault.</summary>
        internal static StoreAccessDecision Judge(FactionRuntime runtime, long identityId, string ownerTag)
        {
            if (runtime == null || identityId == 0 || string.IsNullOrEmpty(ownerTag)
                || MySession.Static == null || MySession.Static.Factions == null)
                return StoreAccessDecision.Served;

            FactionState owner = runtime.FindFaction(ownerTag);
            if (owner == null || owner.IsPlayerFaction) return StoreAccessDecision.Served;

            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            FactionState customer = seFaction != null ? runtime.FindFaction(seFaction.Tag) : null;

            string personalBand = null;
            if (customer == null)
            {
                // The personal record, not the faction's: GetRelationBetweenPlayerAndFaction reads
                // the player-to-faction table, whose band SE derives with
                // TranslateReputationToRelationship. The same call PlayerReputationManager reads.
                MyFaction ownerSe = MySession.Static.Factions.TryGetFactionByTag(ownerTag);
                if (ownerSe != null)
                {
                    Tuple<MyRelationsBetweenFactions, int> rel =
                        MySession.Static.Factions.GetRelationBetweenPlayerAndFaction(identityId, ownerSe.FactionId);
                    if (rel != null) personalBand = rel.Item1.ToString();
                }
            }

            return StoreAccess.Decide(owner, customer, personalBand, runtime.Tuning);
        }

        /// <summary>The store and vanilla-terminal entry: resolves the node behind an SE station id,
        /// judges, and on a refusal traces it and tells the player. True means refuse.</summary>
        internal static bool RefusesAtStation(long stationId, long identityId, ulong steamId, string surface)
        {
            if (stationId == 0 || identityId == 0) return false;
            if (SimulationGate.Deferred) return RefusesOnAgent(stationId, identityId, steamId, surface);
            FactionsPlugin plugin = FactionsPlugin.Instance;
            if (plugin == null || !plugin.IsFactionsStation(stationId)) return false;
            FactionRuntime runtime = plugin.Runtime;
            if (runtime == null || runtime.World == null || runtime.World.Graph == null) return false;

            SpatialNode node = TerminalAccess.FindNodeByStation(runtime, stationId);
            if (node == null) return false;

            StoreAccessDecision d = Judge(runtime, identityId, node.OwnerFactionTag);
            if (!d.Refused) return false;

            Trace(surface, node, identityId, d);
            Notify(plugin, runtime.Tuning, identityId, steamId, node.OwnerFactionTag, d.Message);
            return true;
        }

        /// <summary>The same judgement on a Nexus agent, from the owner's row of the authority's
        /// snapshot (<see cref="StoreAccess.DecideRelayed"/>). A customer's faction counts as
        /// registered when the snapshot has it; their personal band is read from this server's SE,
        /// which Nexus keeps in step with the authority's.</summary>
        private static bool RefusesOnAgent(long stationId, long identityId, ulong steamId, string surface)
        {
            FactionsPlugin plugin = FactionsPlugin.Instance;
            NexusSnapshotFaction owner;
            if (plugin == null || !plugin.IsFactionsStation(stationId) || !plugin.TryAgentStationOwner(stationId, out owner))
                return false;
            StoreAccessDecision d = JudgeOnAgent(plugin, owner, identityId);
            if (!d.Refused) return false;
            if (DebugTraces.StoreBar)
                Log.Info("STORE_REFUSED surface=" + surface + " station=" + stationId + " owner=" + owner.Tag
                       + " identity=" + identityId + " cause=" + d.Cause);
            Notify(plugin, plugin.Tuning, identityId, steamId, owner.Tag, d.Message);
            return true;
        }

        /// <summary>The agent's judgement of one customer at a station whose owner's snapshot row is
        /// <paramref name="owner"/>. Served when the owner is a player faction or unregistered.</summary>
        internal static StoreAccessDecision JudgeOnAgent(FactionsPlugin plugin, NexusSnapshotFaction owner, long identityId)
        {
            if (plugin == null || owner == null || owner.Player || identityId == 0
                || MySession.Static == null || MySession.Static.Factions == null)
                return StoreAccessDecision.Served;

            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            string customerTag = seFaction != null && plugin.IsAgentRegistryFaction(seFaction.Tag) ? seFaction.Tag : null;
            string personalBand = null;
            if (customerTag == null)
            {
                MyFaction ownerSe = MySession.Static.Factions.TryGetFactionByTag(owner.Tag);
                if (ownerSe != null)
                {
                    Tuple<MyRelationsBetweenFactions, int> rel =
                        MySession.Static.Factions.GetRelationBetweenPlayerAndFaction(identityId, ownerSe.FactionId);
                    if (rel != null) personalBand = rel.Item1.ToString();
                }
            }
            return StoreAccess.DecideRelayed(owner, customerTag, personalBand);
        }

        /// <summary>STORE_REFUSED, behind <c>factions debug storebar</c>.</summary>
        internal static void Trace(string surface, SpatialNode node, long identityId, StoreAccessDecision d)
        {
            if (!DebugTraces.StoreBar || node == null) return;
            Log.Info("STORE_REFUSED surface=" + surface + " node=" + node.Id + " owner=" + node.OwnerFactionTag
                   + " identity=" + identityId + " cause=" + d.Cause);
        }

        private static void Notify(FactionsPlugin plugin, FactionTuning tuning, long identityId, ulong steamId,
                                   string ownerTag, string message)
        {
            try
            {
                double cooldown = tuning != null ? tuning.StoreRefusalNoticeCooldownSeconds : 30.0;
                if (!Throttle.ShouldNotify(identityId, ownerTag, DateTime.UtcNow, cooldown)) return;
                if (steamId == 0 && MySession.Static != null && MySession.Static.Players != null)
                    steamId = MySession.Static.Players.TryGetSteamId(identityId);
                if (steamId == 0) return;
                ChatManagerServer chat = plugin.Torch != null && plugin.Torch.CurrentSession != null
                    ? plugin.Torch.CurrentSession.Managers.GetManager<ChatManagerServer>()
                    : null;
                if (chat == null) return;
                chat.SendMessageAsOther(NoticeAuthor, message + ".", VRageMath.Color.OrangeRed, steamId);
            }
            catch (Exception ex)
            {
                Log.Warn("Store refusal notice to " + identityId + " failed: " + ex.Message);
            }
        }

        /// <summary>Whether <paramref name="method"/> exists and carries every named parameter.
        /// Harmony binds prefix arguments by name, so a renamed parameter would throw at patch time
        /// and fail the patch's group; a Prepare that checks the names turns that into this one patch
        /// staying unbound, with a line saying which name was missing.</summary>
        internal static bool Binds(MethodInfo method, string patchName, params string[] parameterNames)
        {
            bool ok = method != null;
            if (ok)
            {
                ParameterInfo[] ps = method.GetParameters();
                foreach (string name in parameterNames)
                {
                    bool found = false;
                    foreach (ParameterInfo p in ps) if (p.Name == name) { found = true; break; }
                    if (!found) { ok = false; break; }
                }
            }
            if (!ok)
                Log.Error("Store refusal: " + patchName + " did not bind on this SE build, so a customer "
                        + "barred from trading is served on that path. Every other path is unaffected.");
            return ok;
        }
    }

    /// <summary>
    /// Every purchase at an SE station's store block — physical item, hydrogen, oxygen and prefab
    /// (ship) — passes through the private <c>MyStoreBlock.BuyFromStation</c>, reached from the
    /// <c>BuyItem</c> server event after <c>HasAccess</c> (which our station prefabs pass for
    /// everyone: the <c>Economy_*</c> station prefabs' store blocks carry <c>AnyoneCanUse=true</c>).
    /// A vending machine is a <c>MyStoreBlock</c> and takes the same path.
    ///
    /// <para><b>How the refusal reaches the client.</b> Not by skipping the method: a request with no
    /// reply leaves the client's buy callback pending. Instead the client's economy-tick stamp is
    /// set to −∞, so vanilla's own first check (<c>LastEconomyTick &gt; lastEconomyTick</c>) sends
    /// <c>MyStoreBuyItemResults.ItemsTimeout</c> through its own <c>SendBuyItemResult</c>, before
    /// any money or item has moved. The client shows its "Transaction Failed" box and re-reads the
    /// store and the balance, so nothing desyncs; the reason arrives by chat.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyStoreBlock), "BuyFromStation")]
    static class BuyFromStationRefusalPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            return StoreRefusal.Binds(AccessTools.Method(typeof(MyStoreBlock), "BuyFromStation"),
                "MyStoreBlock.BuyFromStation", "player", "station", "lastEconomyTick");
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        static void Prefix(MyPlayer player, IMyFactionStation station, ref double lastEconomyTick)
        {
            try
            {
                if (player == null || player.Identity == null || station == null) return;
                if (StoreRefusal.RefusesAtStation(station.Id, player.Identity.IdentityId, player.Id.SteamId, "buy"))
                    lastEconomyTick = double.NegativeInfinity;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Store refusal check failed on a purchase; it is served.");
            }
        }
    }

    /// <summary>The sell side of <see cref="BuyFromStationRefusalPatch"/>: the private
    /// <c>MyStoreBlock.SellToStation</c>, whose first check is the same stamp comparison and replies
    /// <c>MyStoreSellItemResults.ItemsTimeout</c>. <c>ignoreStation</c> marks a player store's custom
    /// item, which is not a station trade.</summary>
    [HarmonyPatch(typeof(MyStoreBlock), "SellToStation")]
    static class SellToStationRefusalPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            return StoreRefusal.Binds(AccessTools.Method(typeof(MyStoreBlock), "SellToStation"),
                "MyStoreBlock.SellToStation", "player", "station", "lastEconomyTick", "ignoreStation");
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        static void Prefix(MyPlayer player, IMyFactionStation station, bool ignoreStation, ref double lastEconomyTick)
        {
            try
            {
                if (ignoreStation || player == null || player.Identity == null || station == null) return;
                if (StoreRefusal.RefusesAtStation(station.Id, player.Identity.IdentityId, player.Id.SteamId, "sell"))
                    lastEconomyTick = double.NegativeInfinity;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Store refusal check failed on a sale; it is served.");
            }
        }
    }

    /// <summary>
    /// Repair at a Services Terminal. Vanilla's <c>Repair</c> re-quotes the grid named in the
    /// client's quote and answers <c>RepairResult.GridStateChanged</c> when it cannot, before it
    /// charges. Clearing the quote's grid id makes that happen, so the client gets vanilla's own
    /// failure reply and no money moves. Runs before <see cref="RepairServicePatch"/>, which then
    /// finds no grid and settles nothing.
    /// </summary>
    [HarmonyPatch(typeof(MyRepairServiceComponent), "Repair")]
    static class RepairRefusalPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            return StoreRefusal.Binds(AccessTools.Method(typeof(MyRepairServiceComponent), "Repair"),
                "MyRepairServiceComponent.Repair", "quote");
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        static void Prefix(MyRepairServiceComponent __instance, MyObjectBuilder_ServiceQuoteData quote)
        {
            try
            {
                if (quote == null) return;
                if (ServicesTerminalRefusal.Refuses(__instance, "repair")) quote.GridEntityId = 0;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Store refusal check failed on a repair; it is served.");
            }
        }
    }

    /// <summary>Salvage at a Services Terminal: <c>AcceptOffer</c>'s first check resolves the quote's
    /// grid and replies <c>AcceptOfferResult(false)</c> when it cannot, before paying. Same device as
    /// <see cref="RepairRefusalPatch"/>.</summary>
    [HarmonyPatch(typeof(MySalvageServiceComponent), "AcceptOffer")]
    static class SalvageRefusalPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            return StoreRefusal.Binds(AccessTools.Method(typeof(MySalvageServiceComponent), "AcceptOffer"),
                "MySalvageServiceComponent.AcceptOffer", "quote");
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        static void Prefix(MySalvageServiceComponent __instance, MyObjectBuilder_SalvageServiceQuote quote)
        {
            try
            {
                if (quote == null) return;
                if (ServicesTerminalRefusal.Refuses(__instance, "salvage")) quote.GridEntityId = 0;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Store refusal check failed on a salvage; it is served.");
            }
        }
    }

    /// <summary>The grid-storage expedite fee. Vanilla's endpoint sends the requester nothing on
    /// success (it broadcasts the order as dirty) and returns silently on its own early refusals,
    /// so skipping it is indistinguishable from one of those: no reply, no charge, the countdown
    /// unchanged.</summary>
    [HarmonyPatch(typeof(MyGridsStorageEntityComponent), "ExpediteCurrentClientOrderEndpoint")]
    static class GridStorageExpediteRefusalPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            return StoreRefusal.Binds(
                AccessTools.Method(typeof(MyGridsStorageEntityComponent), "ExpediteCurrentClientOrderEndpoint"),
                "MyGridsStorageEntityComponent.ExpediteCurrentClientOrderEndpoint");
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        static bool Prefix(MyGridsStorageEntityComponent __instance)
        {
            try
            {
                return !ServicesTerminalRefusal.Refuses(__instance, "expedite");
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Store refusal check failed on an expedite; it is served.");
                return true;
            }
        }
    }

    /// <summary>The caller and station of a vanilla Services Terminal event, fed to
    /// <see cref="StoreRefusal.RefusesAtStation"/>. Valid only inside the server event body.</summary>
    static class ServicesTerminalRefusal
    {
        internal static bool Refuses(VRage.Game.Components.MyEntityComponentBase component, string surface)
        {
            long stationId = ServicesTerminalHelpers.StationIdOf(component);
            if (stationId == 0) return false;
            VRage.Network.EndpointId sender = VRage.Network.MyEventContext.Current.Sender;
            if (!sender.IsValid) return false;
            long identity = ServicesTerminalHelpers.CallerIdentity();
            return StoreRefusal.RefusesAtStation(stationId, identity, sender.Value, surface);
        }
    }
}
