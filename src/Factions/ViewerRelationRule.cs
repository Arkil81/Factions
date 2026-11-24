using Factions.Wire;

namespace Factions
{
    /// <summary>
    /// The colour a viewer is told for one SE standing, whether it came from the faction table or
    /// from the viewer's own reputation. SE-free so the mapping is testable: the host translates
    /// SE's <c>MyRelationsBetweenFactions</c> into these constants by name
    /// (<c>ViewerRelations</c>), so an SE renumbering cannot shift it.
    /// </summary>
    internal static class ViewerRelationRule
    {
        internal const int SeNeutral = 0;
        internal const int SeEnemies = 1;
        internal const int SeAllies = 2;
        internal const int SeFriends = 3;

        /// <summary>Enemies is <see cref="WireRelation.Enemy"/>; Friends, and the obsolete Allies
        /// SE still keeps in the enum, are <see cref="WireRelation.Friendly"/>; anything else is
        /// <see cref="WireRelation.Neutral"/>.</summary>
        internal static WireRelation FromStanding(int seRelation)
        {
            switch (seRelation)
            {
                case SeEnemies: return WireRelation.Enemy;
                case SeAllies:
                case SeFriends: return WireRelation.Friendly;
                default: return WireRelation.Neutral;
            }
        }

        /// <summary>Whether a faction member's answer comes from their own reputation rather than the
        /// faction table: only when the table is readable and has no row for the pair. An unreadable
        /// table (null) keeps the table answer.</summary>
        internal static bool ReadsReputation(bool? rowPresent)
        {
            return rowPresent == false;
        }
    }
}
