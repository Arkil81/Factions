using System;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;

namespace Factions
{
    /// <summary>
    /// Raises SE's own faction-state broadcast from the server, so every client — with or without
    /// the FactionsUI plugin — sees a relation or peace request Factions decided.
    ///
    /// <para><b>Why a broadcast is needed at all.</b> SE has no event that replicates the
    /// faction-relation table. <c>SetReputationBetweenFactions</c>, which the relation projection
    /// writes through, is local to the machine that calls it. A client learns a relation only from
    /// <c>FactionStateChangeSuccess</c> (MyFactionCollection.cs:2162, <c>[Broadcast]</c>), on which
    /// every machine runs <c>ApplyFactionStateChange</c> (:1577) locally, or from the world download
    /// on join.</para>
    ///
    /// <para><b>The success event is raised directly, not the request.</b> The public statics
    /// (<c>MyFactionCollection.AcceptPeace</c> and siblings) go through
    /// <c>FactionStateChangeRequest</c> (:2095) and <c>CheckFactionStateChange</c> (:1451), which
    /// judge the SERVER's own table: AcceptPeace is refused once the pair already reads Neutral,
    /// and the projection has written exactly that just before it raises. Raising
    /// <c>FactionStateChangeSuccess</c> with sender 0 skips the gate and is what the request would
    /// have broadcast. The event is private, so it is bound by reflection; a miss disables the
    /// broadcast only, and clients keep vanilla's rejoin-to-refresh behaviour.</para>
    ///
    /// <para><b>Re-entrancy.</b> A server-raised broadcast is also invoked locally on the server,
    /// synchronously inside the raise (<c>MyReplicationServer.DispatchEvent</c> →
    /// <c>ShouldServerInvokeLocally</c> → <c>InvokeLocally</c>), and so reaches
    /// <c>ChangeFactionRelation</c>, where <see cref="FactionDiplomacyPatch"/> would read it as a
    /// new player act. <see cref="Raising"/> is true for exactly that window, and the patch skips
    /// the vanilla body without routing anything: the server's table already holds the projected
    /// value, which vanilla's band midpoint and member-reputation loop would overwrite. Clients run
    /// the vanilla body on their own copy, member loop included; the server copy is untouched.</para>
    ///
    /// <para>Game thread only. Provenance: FAC-394 slice 5.</para>
    /// </summary>
    internal static class VanillaDiplomacyBroadcast
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        private static bool _resolved;
        private static Func<IMyEventOwner, Action<MyFactionStateChange, long, long, long, long>> _successGetter;

        /// <summary>True only while this class is raising a broadcast. Read by the
        /// <c>ChangeFactionRelation</c> prefix and the request check.</summary>
        internal static bool Raising { get; private set; }

        /// <summary>SE's action for a Core act; <c>null</c> for one this class does not raise.</summary>
        internal static MyFactionStateChange? ToSe(VanillaFactionAct act)
        {
            switch (act)
            {
                case VanillaFactionAct.SendPeaceRequest:    return MyFactionStateChange.SendPeaceRequest;
                case VanillaFactionAct.CancelPeaceRequest:  return MyFactionStateChange.CancelPeaceRequest;
                case VanillaFactionAct.AcceptPeace:         return MyFactionStateChange.AcceptPeace;
                case VanillaFactionAct.AcceptFriendRequest: return MyFactionStateChange.AcceptFriendRequest;
                case VanillaFactionAct.DeclareWar:          return MyFactionStateChange.DeclareWar;
                default:                                    return null;
            }
        }

        /// <summary>The Core act for SE's action.</summary>
        internal static VanillaFactionAct FromSe(MyFactionStateChange action)
        {
            switch (action)
            {
                case MyFactionStateChange.SendPeaceRequest:    return VanillaFactionAct.SendPeaceRequest;
                case MyFactionStateChange.CancelPeaceRequest:  return VanillaFactionAct.CancelPeaceRequest;
                case MyFactionStateChange.AcceptPeace:         return VanillaFactionAct.AcceptPeace;
                case MyFactionStateChange.AcceptFriendRequest: return VanillaFactionAct.AcceptFriendRequest;
                case MyFactionStateChange.DeclareWar:          return VanillaFactionAct.DeclareWar;
                default:                                       return VanillaFactionAct.Other;
            }
        }

        /// <summary>Binds the private success event once. Never throws.
        /// <para>The getter is a single cached instance on purpose: SE's event table caches call
        /// sites by the getter object (<c>MyEventTable.TryGet</c>), so a fresh closure per raise
        /// would add an entry per call.</para></summary>
        private static bool Resolve()
        {
            if (_resolved) return _successGetter != null;
            _resolved = true;
            try
            {
                MethodInfo success = AccessTools.Method(typeof(MyFactionCollection), "FactionStateChangeSuccess",
                    new[] { typeof(MyFactionStateChange), typeof(long), typeof(long), typeof(long), typeof(long) });
                if (success == null || !success.IsStatic)
                {
                    Log.Error("Vanilla diplomacy broadcast: MyFactionCollection.FactionStateChangeSuccess"
                            + "(MyFactionStateChange, long, long, long, long) did not resolve. Clients see a "
                            + "relation or peace-request change only after they rejoin. Nothing else is affected.");
                    return false;
                }
                var del = (Action<MyFactionStateChange, long, long, long, long>)Delegate.CreateDelegate(
                    typeof(Action<MyFactionStateChange, long, long, long, long>), success);
                _successGetter = s => del;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Vanilla diplomacy broadcast: binding FactionStateChangeSuccess failed. Clients "
                            + "see a relation or peace-request change only after they rejoin.");
                return false;
            }
        }

        /// <summary>
        /// Broadcasts <paramref name="act"/> between two SE factions to every client and applies it
        /// on the server under <see cref="Raising"/>. Returns whether it was raised. Never throws:
        /// a caller in the middle of the relation projection must not lose the rest of its pass.
        /// </summary>
        internal static bool Raise(VanillaFactionAct act, long fromFactionId, long toFactionId,
                                   string fromTag, string toTag, string reason)
        {
            MyFactionStateChange? action = ToSe(act);
            bool raised = false;
            if (action.HasValue && Sync.IsServer && MyMultiplayer.Static != null && Resolve())
            {
                try
                {
                    Raising = true;
                    MyMultiplayer.RaiseStaticEvent(_successGetter, action.Value, fromFactionId, toFactionId, 0L, 0L);
                    raised = true;
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Broadcasting " + act + " " + fromTag + " -> " + toTag + " failed; clients "
                               + "keep the previous relation until they rejoin.");
                }
                finally
                {
                    Raising = false;
                }
            }

            // The act ran ChangeFactionRelation on every client, whose member loop overwrote the
            // clients' copy of each member's projected reputation toward the NPC; the server's is
            // intact, so the projection would not otherwise re-send it.
            if (raised)
            {
                try { PlayerReputationManager.Current?.MarkMemberResend(act, fromFactionId, toFactionId); }
                catch (Exception ex) { Log.Warn(ex, "Marking members for a reputation re-send failed."); }
            }

            if (DebugTraces.Diplomacy)
                FactionsPlugin.Instance?.Runtime?.WriteDecisionLine(
                    "DIPLOMACY_VANILLA_BROADCAST act=" + act + " from=" + fromTag + " to=" + toTag
                    + " reason=" + reason + " raised=" + (raised ? "yes" : "no"));
            return raised;
        }
    }

    /// <summary>
    /// Server-side judgement of the vanilla Factions screen's diplomacy buttons for a PlayerMade
    /// faction acting toward a Factions-managed NPC (diplomacy-plan.md rule 5, layer 1).
    ///
    /// <para><b>Hook: a postfix on <c>MyFactionCollection.CheckFactionStateChange</c></b>
    /// (MyFactionCollection.cs:1451), the one gate every client request passes on the server
    /// (<c>FactionStateChangeRequest</c>, :2095-2100). A postfix sees SE's own verdict
    /// (<c>__result</c>) and the sender's identity (<c>senderId</c>, 0 for a server-originated
    /// change), and can still refuse by setting <c>__result</c> false — nothing is broadcast
    /// then.</para>
    ///
    /// <para><b>Why here and not at <c>ChangeFactionRelation</c>.</b> "Send peace" never reaches
    /// that method. The request becomes AcceptPeace only when the target has
    /// <c>AutoAcceptPeace</c> (:2143), which <c>MyFaction</c> defaults to false and every NPC on the
    /// live save holds false; otherwise it only enters SE's request set as Sent/Pending (:1607).
    /// So the offer was never posted into <see cref="Diplomacy"/>.</para>
    ///
    /// <para>Verdicts are <see cref="VanillaDiplomacy.JudgeRequest"/>'s; this class applies them.
    /// A patch miss disables this class only.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyFactionCollection), "CheckFactionStateChange")]  // private; string required
    internal static class FactionStateChangeCheckPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        private static bool _outcomeLogged;

        /// <summary>Resolved before Harmony binds anything. A miss disables this class only and must
        /// never throw: a throw fails its whole patch group (<c>HarmonyPatcher</c>).
        /// <para>Provenance: FAC-394 slice 5.</para></summary>
        [HarmonyPrepare]
        private static bool Prepare()
        {
            MethodInfo target = AccessTools.Method(
                typeof(MyFactionCollection), "CheckFactionStateChange",
                new[] { typeof(MyFactionStateChange), typeof(long), typeof(long), typeof(long), typeof(long) });
            bool resolved = target != null && target.ReturnType == typeof(bool);
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Vanilla diplomacy requests: Factions routing bound.");
                else
                    Log.Error("Vanilla diplomacy requests: MyFactionCollection.CheckFactionStateChange"
                            + "(MyFactionStateChange, long, long, long, long) did not resolve. \"Send peace\" "
                            + "to a managed NPC posts no truce offer, and a player's Accept peace sets "
                            + "clients Neutral while the war stands on the server. Nothing else is affected.");
            }
            return resolved;
        }

        [HarmonyPostfix]
        private static void Postfix(MyFactionStateChange action, long fromFactionId, long toFactionId,
                                    long senderId, ref bool __result)
        {
            try
            {
                if (VanillaDiplomacyBroadcast.Raising) return;
                VanillaFactionAct act = VanillaDiplomacyBroadcast.FromSe(action);
                if (!VanillaDiplomacy.IsDiplomaticAct(act)) return;
                if (SimulationGate.Deferred) return;

                FactionRuntime runtime = FactionsPlugin.Instance?.Runtime;
                FactionTuning tuning = runtime?.World?.Tuning;
                if (runtime == null || tuning == null) return;
                MySession session = MySession.Static;
                if (session == null || session.Factions == null || session.Players == null) return;

                MyFaction seFrom = session.Factions.TryGetFactionById(fromFactionId) as MyFaction;
                MyFaction seTo = session.Factions.TryGetFactionById(toFactionId) as MyFaction;
                if (seFrom == null || seTo == null) return;

                FactionState npc = runtime.FindFaction(seTo.Tag);
                bool inScope = seFrom.FactionType == MyFactionTypes.PlayerMade && npc != null && !npc.IsPlayerFaction;
                if (!inScope) return;

                FactionState player = runtime.FindFaction(seFrom.Tag);
                bool registered = player != null && player.IsPlayerFaction;
                bool playerSent = senderId != 0L;
                ulong senderSteam = playerSent ? session.Players.TryGetSteamId(senderId) : 0UL;
                bool leader = playerSent && seFrom.IsLeader(senderId);
                bool admin = senderSteam != 0UL && session.IsUserAdmin(senderSteam);
                bool vanillaAccepted = __result;

                VanillaRequestVerdict verdict = VanillaDiplomacy.JudgeRequest(
                    act, playerSent, inScope, leader, admin, vanillaAccepted, registered);

                if (DebugTraces.Diplomacy)
                    runtime.WriteDecisionLine("DIPLOMACY_VANILLA_REQUEST act=" + action + " from=" + seFrom.Tag
                        + " to=" + seTo.Tag + " sender=" + senderId + " leader=" + (leader ? "yes" : "no")
                        + " admin=" + (admin ? "yes" : "no") + " vanilla=" + (vanillaAccepted ? "accepted" : "refused")
                        + " verdict=" + verdict);

                switch (verdict)
                {
                    case VanillaRequestVerdict.RefuseNotLeader:
                        __result = false;
                        if (DebugTraces.Diplomacy)
                            runtime.WriteDecisionLine("DIPLOMACY_VANILLA_REFUSED act=" + action + " from=" + seFrom.Tag
                                + " to=" + seTo.Tag + " sender=" + senderId + " reason=not_leader");
                        if (senderSteam != 0UL)
                            runtime.WhisperPlayer(senderSteam, "Only a leader of " + seFrom.Tag
                                + " can declare war or make peace for it.");
                        return;

                    case VanillaRequestVerdict.OfferPeaceAndKeep:
                        Diplomacy.OfferPeace(player, npc.Tag, runtime.World.UtcNow);
                        FactionDiplomacyPatch.LogPeaceOffered(player, npc, action.ToString(), tuning);
                        if (seTo.AutoAcceptPeace)
                        {
                            // FactionStateChangeRequest reads this AFTER the check returns (:2143) and
                            // would turn the request into an instant AcceptPeace broadcast. A managed
                            // NPC's peace is Diplomacy's to grant.
                            seTo.AutoAcceptPeace = false;
                            Log.Info("Auto-accept peace turned off for managed NPC faction " + seTo.Tag + ".");
                        }
                        return;

                    case VanillaRequestVerdict.OfferPeaceAndSuppress:
                        Diplomacy.OfferPeace(player, npc.Tag, runtime.World.UtcNow);
                        FactionDiplomacyPatch.LogPeaceOffered(player, npc, action.ToString(), tuning);
                        __result = false;
                        return;

                    case VanillaRequestVerdict.WithdrawAndKeep:
                        bool withdrawn = VanillaDiplomacy.WithdrawPeace(player, npc.Tag);
                        Log.Info("DIPLOMACY_PEACE_WITHDRAWN player=" + player.Tag + " target=" + npc.Tag
                               + " offer=" + (withdrawn ? "removed" : "none"));
                        return;
                }
            }
            catch (Exception ex)
            {
                // Vanilla's own verdict stands: a throw out of the check would break the Factions
                // screen for everyone.
                Log.Error(ex, "Judging a vanilla diplomacy request failed; SE's own verdict was used.");
            }
        }
    }
}
