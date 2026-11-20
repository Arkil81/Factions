using System;

namespace Factions.Core
{
    /// <summary>SE's faction diplomacy acts, named as <c>MyFactionStateChange</c> names them. Core
    /// cannot see the SE enum; the host maps between the two, one to one.</summary>
    public enum VanillaFactionAct
    {
        None = 0,
        SendPeaceRequest,
        CancelPeaceRequest,
        AcceptPeace,
        AcceptFriendRequest,
        DeclareWar,
        /// <summary>Any other state change (membership, friend requests, faction removal).</summary>
        Other,
    }

    /// <summary>What the host does with one player-sent faction state change, after SE's own
    /// <c>CheckFactionStateChange</c> has judged it.</summary>
    public enum VanillaRequestVerdict
    {
        /// <summary>Not ours to touch: vanilla's answer stands.</summary>
        PassThrough = 0,
        /// <summary>Refused: the sender does not lead the faction they are acting for.</summary>
        RefuseNotLeader,
        /// <summary>Post a truce offer and let vanilla record the request, so the white flag shows
        /// the offer as pending on every client.</summary>
        OfferPeaceAndKeep,
        /// <summary>Post a truce offer and refuse the vanilla change. Accepting peace would otherwise
        /// broadcast a Neutral relation to every client while the war stands on the server.</summary>
        OfferPeaceAndSuppress,
        /// <summary>Withdraw the faction's truce offer and let vanilla clear the flag.</summary>
        WithdrawAndKeep,
    }

    /// <summary>
    /// The rules behind the vanilla Factions screen's diplomacy buttons, for one player faction and
    /// one Factions-managed NPC (diplomacy-plan.md rule 5, layer 1).
    ///
    /// <para><b>Factions is authoritative; SE's request set is a view of it.</b> A player's "Send
    /// peace" posts a truce offer into <see cref="Diplomacy"/>. That offer concludes through
    /// <see cref="Diplomacy.ProcessProposals"/> or lapses on its TTL. Either way the host then
    /// raises the vanilla broadcast that makes every client, modded or not, agree: the relation
    /// projection raises AcceptPeace when the pair's band moves, and
    /// <see cref="ReconcileRequest"/> clears a flag whose offer is gone.</para>
    ///
    /// <para>SE-free so the decisions are tested here; the host keeps only the reflection and the
    /// broadcast. Provenance: FAC-394 slice 5.</para>
    /// </summary>
    public static class VanillaDiplomacy
    {
        /// <summary>The acts the Factions screen's diplomacy buttons send.</summary>
        public static bool IsDiplomaticAct(VanillaFactionAct act)
        {
            switch (act)
            {
                case VanillaFactionAct.SendPeaceRequest:
                case VanillaFactionAct.CancelPeaceRequest:
                case VanillaFactionAct.AcceptPeace:
                case VanillaFactionAct.AcceptFriendRequest:
                case VanillaFactionAct.DeclareWar:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Decides one player-sent diplomatic state change between a PlayerMade faction and a
        /// Factions-managed NPC.
        ///
        /// <para><b>Only a leader acts for the faction</b> (rule 3). SE's server check already
        /// refuses a non-leader, non-admin sender; the verdict repeats it so the refusal is ours
        /// to report and does not depend on one SE version's check. Admins keep vanilla's
        /// exemption: SE lets an admin act for any faction and this does not narrow that.</para>
        ///
        /// <para>A server-originated change (<paramref name="playerSent"/> false) is not a player's
        /// act and passes through. So does anything vanilla refused on its own state grounds, and
        /// anything from a faction that is not yet registered, which has no state to route into and
        /// keeps the relation-write fallback in <c>FactionDiplomacyPatch</c>.</para>
        /// </summary>
        public static VanillaRequestVerdict JudgeRequest(VanillaFactionAct act, bool playerSent, bool inScope,
                                                         bool senderIsLeader, bool senderIsAdmin,
                                                         bool vanillaAccepted, bool playerRegistered)
        {
            if (!inScope || !playerSent || !IsDiplomaticAct(act)) return VanillaRequestVerdict.PassThrough;
            if (!senderIsLeader && !senderIsAdmin) return VanillaRequestVerdict.RefuseNotLeader;
            if (!vanillaAccepted || !playerRegistered) return VanillaRequestVerdict.PassThrough;

            switch (act)
            {
                case VanillaFactionAct.SendPeaceRequest:    return VanillaRequestVerdict.OfferPeaceAndKeep;
                case VanillaFactionAct.AcceptPeace:
                case VanillaFactionAct.AcceptFriendRequest: return VanillaRequestVerdict.OfferPeaceAndSuppress;
                case VanillaFactionAct.CancelPeaceRequest:  return VanillaRequestVerdict.WithdrawAndKeep;
                default:                                    return VanillaRequestVerdict.PassThrough;   // war: routed on apply
            }
        }

        /// <summary>True while <paramref name="from"/> has a truce or non-aggression offer to
        /// <paramref name="toTag"/> that <see cref="Diplomacy.ProcessProposals"/> would still act on.
        /// An alliance proposal is not a peace offer and does not count. The age test is
        /// <see cref="Diplomacy.OfferOpen"/>, the same rule ProcessProposals prunes by, so a flag is
        /// cancelled exactly when the offer behind it lapses: <see cref="FactionTuning.PlayerOfferTtl"/>
        /// when either side is a player faction, which every pair reconciled here is.</summary>
        public static bool TruceOfferOpen(FactionState from, FactionState to, DateTime nowUtc, FactionTuning t)
        {
            if (from == null || to == null || string.IsNullOrEmpty(to.Tag) || t == null) return false;
            DiplomacyMessage msg;
            if (!from.Proposals.TryGetValue(to.Tag, out msg) || msg == null || msg.Terms == null) return false;
            if (!Diplomacy.OfferOpen(from, to, msg, nowUtc, t)) return false;
            return IsPeaceOffer(msg);
        }

        /// <summary>Withdraws <paramref name="self"/>'s truce offer to <paramref name="toTag"/>, if it
        /// has one. An alliance proposal is left standing. Returns whether an offer was removed.</summary>
        public static bool WithdrawPeace(FactionState self, string toTag)
        {
            if (self == null || string.IsNullOrEmpty(toTag)) return false;
            DiplomacyMessage msg;
            if (!self.Proposals.TryGetValue(toTag, out msg) || msg == null || !IsPeaceOffer(msg)) return false;
            return self.Proposals.Remove(toTag);
        }

        /// <summary>
        /// The vanilla request a directed pair should be showing, given the offer Factions holds.
        ///
        /// <para>Only a pair SE has at war is judged. A peace request needs a war (SE's own check
        /// refuses one otherwise), and the same request set also holds friend requests, which
        /// exist only between Neutral factions and are not ours to clear.</para>
        ///
        /// <list type="bullet">
        /// <item><description>offer open, no flag → <see cref="VanillaFactionAct.SendPeaceRequest"/>
        /// (an offer posted by chat or by the brain, or one that survived a restart the flag did not);</description></item>
        /// <item><description>flag, no open offer → <see cref="VanillaFactionAct.CancelPeaceRequest"/>
        /// (the offer lapsed or was dropped).</description></item>
        /// </list>
        ///
        /// <para>A concluded truce does not reach here as a flag: the relation projection moves
        /// the pair out of Enemies in the same pass and raises AcceptPeace, which clears the
        /// request on every machine.</para>
        /// </summary>
        public static VanillaFactionAct ReconcileRequest(bool seAtWar, bool requestShown, bool offerOpen)
        {
            if (!seAtWar) return VanillaFactionAct.None;
            if (offerOpen && !requestShown) return VanillaFactionAct.SendPeaceRequest;
            if (!offerOpen && requestShown) return VanillaFactionAct.CancelPeaceRequest;
            return VanillaFactionAct.None;
        }

        /// <summary>The vanilla state change that sets a client's relation to
        /// <paramref name="band"/> (a <see cref="ReputationScale.BandOf"/> name). Each one runs SE's
        /// <c>ChangeFactionRelation</c> on the receiving machine and clears any request between the
        /// pair.</summary>
        public static VanillaFactionAct ActForBand(string band)
        {
            switch (band)
            {
                case "Enemies": return VanillaFactionAct.DeclareWar;
                case "Neutral": return VanillaFactionAct.AcceptPeace;
                case "Friends": return VanillaFactionAct.AcceptFriendRequest;
                default:        return VanillaFactionAct.None;
            }
        }

        /// <summary>Whether applying <paramref name="act"/> on a machine runs SE's
        /// <c>ChangeFactionRelation</c>, whose member loop overwrites every member's personal
        /// reputation toward the other faction on that machine. True for exactly the three
        /// <see cref="ActForBand"/> produces; a peace request or its cancellation only moves the
        /// request set (MyFactionCollection.ApplyFactionStateChange).</summary>
        public static bool RunsMemberLoop(VanillaFactionAct act)
        {
            return act == VanillaFactionAct.DeclareWar
                || act == VanillaFactionAct.AcceptPeace
                || act == VanillaFactionAct.AcceptFriendRequest;
        }

        private static bool IsPeaceOffer(DiplomacyMessage msg)
        {
            if (msg.Terms == null) return false;
            bool peace = false;
            foreach (DiplomacyTerm term in msg.Terms)
            {
                if (term.Verb == DiplomacyVerb.Alliance) return false;
                if (term.Verb == DiplomacyVerb.Truce || term.Verb == DiplomacyVerb.NonAggression) peace = true;
            }
            return peace;
        }
    }
}
