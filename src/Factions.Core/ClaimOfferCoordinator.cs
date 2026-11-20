using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Live (non-persisted) claim-offer negotiation state for one claimable node. Lost on
    /// restart by design — worst case a faction is re-asked, which is harmless.</summary>
    public sealed class ClaimOfferState
    {
        /// <summary>Faction tag currently holding the outstanding offer, or null if none.</summary>
        public string OfferedFactionTag;

        /// <summary>Factions that said "no" this presence "session" — cleared once the node has no
        /// eligible presence at all, so a later re-entry gets a fresh slate (design: "ask again upon
        /// re-entering the node").</summary>
        public readonly HashSet<string> DeclinedFactionTags = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Pure race/decline/re-offer rules for the Magnate-tier claim-offer flow (see
    /// docs/economy-territory.md → "Player-faction claiming"). Single-sourced here so the host's
    /// chat/presence adapter has no policy logic of its own to get wrong or diverge from the sim.</summary>
    public static class ClaimOfferCoordinator
    {
        /// <summary>Resolves which faction (if any) should hold the offer this tick, given every
        /// eligible faction currently present at the node, ordered earliest-arrival-first. Mutates
        /// <paramref name="state"/> to track lapses (left without answering) and node-empty resets.</summary>
        public static string ResolveOfferee(ClaimOfferState state, IList<string> presentFactionTagsByArrival)
        {
            if (state.OfferedFactionTag != null)
            {
                if (Contains(presentFactionTagsByArrival, state.OfferedFactionTag))
                    return state.OfferedFactionTag; // still outstanding, waiting on a reply
                state.OfferedFactionTag = null; // left without answering — lapses, not a decline
            }

            if (presentFactionTagsByArrival == null || presentFactionTagsByArrival.Count == 0)
            {
                // Node is empty of eligible presence — next arrival (even a prior decliner) starts fresh.
                state.DeclinedFactionTags.Clear();
                return null;
            }

            for (int i = 0; i < presentFactionTagsByArrival.Count; i++)
            {
                string tag = presentFactionTagsByArrival[i];
                if (state.DeclinedFactionTags.Contains(tag)) continue;
                state.OfferedFactionTag = tag;
                return tag;
            }
            return null; // everyone currently present has already declined this session
        }

        /// <summary>Records a "no" reply. Idempotent to call more than once for the same tag.</summary>
        public static void Decline(ClaimOfferState state, string factionTag)
        {
            if (state.OfferedFactionTag == factionTag) state.OfferedFactionTag = null;
            state.DeclinedFactionTags.Add(factionTag);
        }

        private static bool Contains(IList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
