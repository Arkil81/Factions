using System;

namespace Factions.Core
{
    /// <summary>
    /// Which family of station prefabs a faction builds from.
    ///
    /// <para>Style and environment are two independent axes — a style says what the station looks
    /// like, the site says whether it is an outpost or a space station — and this is only the first.
    /// It replaced a single <c>isRaider</c> boolean, which could express two families and had no
    /// room for a third.</para>
    ///
    /// <para>Distinct from <see cref="PrefabFactionHint"/> even though both are personality-derived.
    /// That one answers "which livery of <b>hull</b>", against SE's own faction types and the
    /// catalogue's <c>FactionHint</c>. This answers "which family of <b>station</b>", against prefab
    /// families SE never typed at all. Collapsing them would tie a faction's fleet to its
    /// architecture, which is not a relationship either side asked for.</para>
    /// </summary>
    public static class StationStyle
    {
        /// <summary>The vanilla economy stations and outposts — a store, a contract board, a
        /// medbay. What every faction builds unless it is something else.</summary>
        public const string Civilian = "Civilian";

        /// <summary>The <c>_Pirate</c> variants of the same economy prefabs.</summary>
        public const string Pirate = "Pirate";

        /// <summary>The Factorum's own structures. <b>These are not trading posts</b>: they carry no
        /// store block, no contract block and no medbay, which is the intent — operator, 2026-09-01,
        /// "this is not a faction you can be friends with". A style whose family cannot trade is a
        /// real statement about the faction, so nothing should quietly substitute a civilian prefab
        /// to fill a gap.</summary>
        public const string Factorum = "Factorum";

        /// <summary>The style a personality builds in. Anything unrecognised — including a null or
        /// absent personality — is <see cref="Civilian"/>, so a faction can only ever gain a
        /// distinctive style by being given a personality that names one.</summary>
        public static string ForPersonality(string personalityId)
        {
            if (string.Equals(personalityId, "raider", StringComparison.OrdinalIgnoreCase))
                return Pirate;
            if (string.Equals(personalityId, "factorum", StringComparison.OrdinalIgnoreCase))
                return Factorum;
            return Civilian;
        }

        /// <summary>Resolve a style against an optional caller override, preserving the
        /// <c>bool?</c> contract the player-facing station commands are written to: true asks for
        /// the pirate look, false asks for the civilian one, null defers to the personality.
        ///
        /// <para>A player picking a look is choosing between the two families that have a
        /// player-facing meaning. The Factorum's is deliberately not among them.</para></summary>
        public static string Resolve(bool? pirateStyleOverride, string personalityId)
        {
            if (pirateStyleOverride.HasValue)
                return pirateStyleOverride.Value ? Pirate : Civilian;
            return ForPersonality(personalityId);
        }
    }
}
