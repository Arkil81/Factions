using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a yes or no typed in chat does to a standing claim or conquer offer.</summary>
    public enum OfferReplyVerdict
    {
        /// <summary>No offer stands for the author's faction; the line is ordinary chat.</summary>
        NotForThem,
        /// <summary>An offer stands for the author's faction, but the author may not answer it.</summary>
        Refused,
        /// <summary>The author may answer it: accept on yes, decline on no.</summary>
        Answer,
    }

    /// <summary>
    /// Who may answer a claim or conquer offer, who it is whispered to, and the sentences involved.
    /// Answering commits the faction, so only someone who may command it can: a leader, a founder or a
    /// server admin (the host's <c>PlayerAuthority.MayCommand</c>). Any other member is told so. The
    /// offer belongs to the faction rather than to the member it was whispered to, so any leader can
    /// answer it, including one who was not there when it was made (FAC-510).
    /// </summary>
    public static class OfferAuthority
    {
        /// <summary>The verdict on a reply from a member of <paramref name="authorFactionTag"/> to an
        /// offer standing for <paramref name="offeredFactionTag"/>.</summary>
        public static OfferReplyVerdict Judge(string authorFactionTag, string offeredFactionTag, bool authorMayCommand)
        {
            if (string.IsNullOrEmpty(authorFactionTag) || string.IsNullOrEmpty(offeredFactionTag)) return OfferReplyVerdict.NotForThem;
            if (!string.Equals(authorFactionTag, offeredFactionTag, StringComparison.Ordinal)) return OfferReplyVerdict.NotForThem;
            return authorMayCommand ? OfferReplyVerdict.Answer : OfferReplyVerdict.Refused;
        }

        /// <summary>The index of the member an offer is whispered to: the first who may command the
        /// faction, else the first member. -1 for an empty list.</summary>
        public static int PickRecipient(IList<bool> mayCommand)
        {
            if (mayCommand == null || mayCommand.Count == 0) return -1;
            for (int i = 0; i < mayCommand.Count; i++)
                if (mayCommand[i]) return i;
            return 0;
        }

        /// <summary>The reply a member who may not answer is given.</summary>
        public static string RefusalText(string factionTag)
        {
            return "Only a leader of [" + factionTag + "] can accept or decline this offer.";
        }

        /// <summary>The claim offer as whispered. A recipient who may not answer it is told who can.</summary>
        public static string ClaimOfferText(string siteDescription, string nodeId, string factionTag, bool recipientMayCommand)
        {
            string head = "Our scouts report an unclaimed " + siteDescription + " (" + nodeId + "). ";
            if (recipientMayCommand) return head + "Claim it for " + factionTag + "? Reply yes or no.";
            return head + "A leader of [" + factionTag + "] can claim it for the faction by replying yes, or turn it down with no.";
        }

        /// <summary>The conquer offer as whispered to the attacker. An attacker who may not answer it is
        /// told who can.</summary>
        public static string ConquerOfferText(string nodeId, string factionTag, bool recipientMayCommand)
        {
            string head = "You hold " + nodeId + ". Its owner cannot dislodge you. ";
            if (recipientMayCommand || string.IsNullOrEmpty(factionTag))
                return head + "Take it? Reply \"yes\" to claim it, \"no\" to leave it standing.";
            return head + "A leader of [" + factionTag + "] can take it for the faction by replying \"yes\", or leave it standing with \"no\".";
        }

        /// <summary>The conquer offer as whispered to a leader who was not the attacker.</summary>
        public static string ConquerOfferForLeaderText(string nodeId, string factionTag, string attackerName)
        {
            return (string.IsNullOrEmpty(attackerName) ? "A member of [" + factionTag + "]" : attackerName)
                 + " holds " + nodeId + " and its owner cannot dislodge them. Take it for [" + factionTag
                 + "]? Reply \"yes\" to claim it, \"no\" to leave it standing.";
        }
    }
}
