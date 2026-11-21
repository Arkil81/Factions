using System;
using System.Collections.Generic;
using Factions.Core;
using NLog;
using Sandbox.Game.World;
using Torch.Managers.ChatManager;

namespace Factions
{
    /// <summary>
    /// The one reader of a typed yes or no for claim and conquer offers. Torch hands every chat line to
    /// every subscriber, so while each manager read chat itself a single "yes" answered a claim offer
    /// and a conquer offer at once. Here a reply answers exactly one offer: the one it names
    /// (<c>yes N123</c>), the only one the player may answer, or, when several are open and none is
    /// named, none, and the player is asked which (FAC-511). Replies relayed from a Nexus agent are
    /// routed the same way.
    /// </summary>
    internal sealed class OfferReplyRouter
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private readonly ClaimOfferManager _claims;
        private readonly ConquerOfferManager _conquers;
        private ChatManagerServer _chat;

        public OfferReplyRouter(ClaimOfferManager claims, ConquerOfferManager conquers)
        {
            _claims = claims;
            _conquers = conquers;
        }

        public void AttachChat(ChatManagerServer chat)
        {
            _chat = chat;
            if (_chat != null) _chat.MessageProcessing += OnChatMessageProcessing;
        }

        public void DetachChat()
        {
            if (_chat != null) _chat.MessageProcessing -= OnChatMessageProcessing;
            _chat = null;
        }

        private void OnChatMessageProcessing(Torch.API.Managers.TorchChatMessage msg, ref bool consumed)
        {
            try
            {
                if (consumed || !msg.AuthorSteamId.HasValue || MySession.Static == null) return;
                bool isYes;
                string nodeId;
                if (!OfferReply.TryParse(msg.Message, out isYes, out nodeId)) return;
                long identityId = MySession.Static.Players.TryGetIdentityId(msg.AuthorSteamId.Value);
                if (identityId == 0L) return;
                if (Route(identityId, identityId, isYes, nodeId)) consumed = true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Offer reply handling threw.");
            }
        }

        /// <summary>Answers the one offer a reply means. The two identities differ only for a reply
        /// relayed from an agent: the conquer offer is keyed by the identity the agent reported, the
        /// claim offer by this server's identity for the same player. False when no offer is open to
        /// them, so the line is left as chat.</summary>
        public bool Route(long conquerIdentity, long claimIdentity, bool isYes, string namedNode)
        {
            var open = new List<OpenOffer>();
            if (_claims != null) _claims.CollectOpen(claimIdentity, open);
            if (_conquers != null) _conquers.CollectOpen(conquerIdentity, open);

            int index;
            OfferReplyChoice choice = OfferReply.Choose(open, namedNode, out index);
            if (choice == OfferReplyChoice.None) return false;
            if (choice == OfferReplyChoice.Ambiguous || choice == OfferReplyChoice.NoSuchNode)
            {
                var ids = new List<string>();
                for (int i = 0; i < open.Count; i++) ids.Add(open[i].NodeId);
                Log.Info("OFFER_REPLY_ASKED identity=" + claimIdentity + " open=" + string.Join(",", ids.ToArray())
                         + (namedNode != null ? " named=" + namedNode : ""));
                string text = choice == OfferReplyChoice.Ambiguous
                    ? OfferReply.WhichText(open)
                    : OfferReply.NoSuchNodeText(namedNode, open);
                // As an offer, so an agent the player is on captures their next yes or no.
                if (_claims != null) _claims.Tell(claimIdentity, text, true);
                return true;
            }

            OpenOffer chosen = open[index];
            return chosen.Conquer
                ? _conquers.Answer(conquerIdentity, isYes, chosen.NodeId)
                : _claims.Answer(claimIdentity, isYes, chosen.NodeId);
        }
    }
}
