using System;

namespace Factions.Core
{
    /// <summary>
    /// The SE-native tag for ownerless territory. A node abandoned via <see cref="WorldModel.Withdraw"/>
    /// or decay expiry (<see cref="WorldModel.DecayInfluence"/>) is transferred to this faction instead
    /// of being deleted or left unowned — a persistent, claimable "graveyard" rather than a graph hole.
    /// <see cref="FactionLifecycle.IsDormant"/> treats this tag as permanently dormant: it never thinks,
    /// fights, trades, or produces. See docs/economy-territory.md.
    /// </summary>
    public static class NeutralFaction
    {
        public const string Tag = "UNKN";

        /// <summary>True when a node with this owner tag is claimable — unowned (null, a state
        /// that never actually occurs post-Expand today, but kept for safety) or held by the
        /// dormant UNKN "graveyard" faction. Single-sourced so <see cref="WorldModel.Claim"/> and
        /// anything that needs to *display* claimability (e.g. the player scanner) agree exactly.</summary>
        public static bool IsClaimable(string ownerFactionTag)
        {
            return ownerFactionTag == null || ownerFactionTag == Tag;
        }

        /// <summary>True when <paramref name="claimantTag"/> may claim <paramref name="node"/> right
        /// now: it is claimable at all, and no other faction's live siege is holding it.
        ///
        /// <para>A node whose hold is ground to zero is abandoned to <see cref="Tag"/> by the decay
        /// pass, which puts the besieger's prize on the open market at the exact moment the siege
        /// succeeds. Claim costs one action and no hulls, so a bystander's brain takes it before the
        /// attacker's conquest fleet has finished its transit. For the length of the contest window
        /// the derelict is the besieger's to take.</para>
        ///
        /// <para>A null <see cref="SpatialNode.ContestedByTag"/> is a player's siege, and matches no
        /// faction tag: every faction is held off, which is the intent.</para></summary>
        public static bool IsClaimableBy(SpatialNode node, string claimantTag, DateTime utcNow)
        {
            return node != null && IsClaimable(node.OwnerFactionTag)
                   && !IsReservedAgainst(node, claimantTag, utcNow);
        }

        /// <summary>True when a live siege by someone other than <paramref name="claimantTag"/> is
        /// holding <paramref name="node"/>. Says nothing about whether the node is claimable —
        /// <see cref="IsClaimableBy"/> is the question a caller wants.</summary>
        public static bool IsReservedAgainst(SpatialNode node, string claimantTag, DateTime utcNow)
        {
            if (node == null || utcNow >= node.ContestedUntilUtc) return false;
            return !string.Equals(node.ContestedByTag, claimantTag, StringComparison.Ordinal);
        }
    }
}
