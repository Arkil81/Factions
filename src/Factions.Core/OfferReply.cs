using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a player's offer reply resolves to among the offers open to them.</summary>
    public enum OfferReplyChoice
    {
        /// <summary>No offer is open to them; the line is ordinary chat.</summary>
        None,
        /// <summary>Exactly one offer is meant; <c>index</c> names it.</summary>
        One,
        /// <summary>Several are open and the reply named none of them; ask which.</summary>
        Ambiguous,
        /// <summary>The reply named a node no open offer is for.</summary>
        NoSuchNode,
    }

    /// <summary>One offer open to a player, as the reply router sees it.</summary>
    public struct OpenOffer
    {
        public string NodeId;
        /// <summary>True for a conquer offer, false for a claim offer.</summary>
        public bool Conquer;
        /// <summary>Whether this player may answer it, rather than be refused.</summary>
        public bool MayAnswer;
    }

    /// <summary>
    /// A typed reply to a claim or conquer offer: <c>yes</c> or <c>no</c>, optionally behind a
    /// whisper-reply prefix and optionally naming the node (<c>yes N123</c>). One reply answers one
    /// offer. When a player has several open and does not name one, they are asked which (FAC-511):
    /// a single line used to answer every open offer at once, and a <c>no</c> meant for a claim also
    /// ended a siege.
    /// </summary>
    public static class OfferReply
    {
        private static readonly string[] ReplyPrefixes = { "/r ", "/reply " };

        /// <summary>Parses a chat line. <paramref name="nodeId"/> is null when no node was named.
        /// False for anything that is not a yes or no, so ordinary chat passes through.</summary>
        public static bool TryParse(string rawText, out bool isYes, out string nodeId)
        {
            isYes = false;
            nodeId = null;
            if (string.IsNullOrEmpty(rawText)) return false;
            string text = rawText.Trim();
            foreach (string prefix in ReplyPrefixes)
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring(prefix.Length).TrimStart();
                    break;
                }
            }
            text = text.TrimEnd('!', '.', '?', ' ');

            string word = text;
            int space = text.IndexOf(' ');
            if (space > 0)
            {
                word = text.Substring(0, space);
                string rest = text.Substring(space + 1).Trim().TrimEnd('!', '.', '?', ' ').Trim('(', ')');
                // "yes please" or "no thanks" stays ordinary chat; only a node id makes it a reply.
                if (!LooksLikeNodeId(rest)) return false;
                nodeId = rest;
            }

            if (string.Equals(word, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(word, "y", StringComparison.OrdinalIgnoreCase))
            {
                isYes = true;
                return true;
            }
            if (string.Equals(word, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(word, "n", StringComparison.OrdinalIgnoreCase))
                return true;
            nodeId = null;
            return false;
        }

        /// <summary>A node id as the graph mints them: letters, then digits (<c>N123</c>).</summary>
        public static bool LooksLikeNodeId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 16) return false;
            int i = 0;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            if (i == 0 || i == s.Length) return false;
            for (; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        /// <summary>Which open offer a reply means. With a node named, the offer for that node. With
        /// none, the only offer the player may answer, or, if they may answer none, the first (whose
        /// answer is then a refusal). Several they may answer, and none named: ambiguous.</summary>
        public static OfferReplyChoice Choose(IList<OpenOffer> open, string namedNode, out int index)
        {
            index = -1;
            if (open == null || open.Count == 0) return OfferReplyChoice.None;

            if (!string.IsNullOrEmpty(namedNode))
            {
                for (int i = 0; i < open.Count; i++)
                    if (string.Equals(open[i].NodeId, namedNode, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        return OfferReplyChoice.One;
                    }
                return OfferReplyChoice.NoSuchNode;
            }

            int answerable = 0, first = -1;
            for (int i = 0; i < open.Count; i++)
                if (open[i].MayAnswer)
                {
                    answerable++;
                    if (first < 0) first = i;
                }
            if (answerable == 0) { index = 0; return OfferReplyChoice.One; }
            if (answerable == 1) { index = first; return OfferReplyChoice.One; }
            return OfferReplyChoice.Ambiguous;
        }

        /// <summary>The question a player with several open offers is asked.</summary>
        public static string WhichText(IList<OpenOffer> open)
        {
            var parts = new List<string>();
            string example = null;
            for (int i = 0; i < open.Count; i++)
            {
                if (!open[i].MayAnswer) continue;
                parts.Add(open[i].NodeId + (open[i].Conquer ? " (take it from its owner)" : " (claim it)"));
                if (example == null) example = open[i].NodeId;
            }
            return "You have " + parts.Count + " offers open: " + string.Join(", ", parts.ToArray())
                 + ". Reply \"yes " + example + "\" or \"no " + example + "\" to answer one of them.";
        }

        /// <summary>The reply to a player who named a node no open offer is for.</summary>
        public static string NoSuchNodeText(string namedNode, IList<OpenOffer> open)
        {
            var ids = new List<string>();
            for (int i = 0; i < open.Count; i++) ids.Add(open[i].NodeId);
            return "No offer is open for " + namedNode + ". Your open offers are for "
                 + string.Join(", ", ids.ToArray()) + ".";
        }
    }
}
