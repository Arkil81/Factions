using System;

namespace Factions.Core
{
    /// <summary>
    /// Which prefab livery a faction draws hulls from — the <c>factionHint</c> argument to
    /// <see cref="IPrefabCatalogue.GetNamesForRole"/>.
    ///
    /// <para><b>This is not the same question as "what type is this faction".</b> SE's own answer
    /// (<see cref="IFactionTypeCatalogue.TryGetFactionType"/>) is authoritative wherever it has one,
    /// and every economy faction does — Miner, Trader, Builder, Pirate, Military. But SE has no
    /// Factorum <c>MyFactionTypeDefinition</c>: the live catalogue reports <c>FCTM=None</c>, so a
    /// hint taken straight from the type never matches the seven <c>FactionHint=Factorum</c> prefabs
    /// and every Factorum query falls through to the universal pool.</para>
    ///
    /// <para><b>Why "None" is not simply corrected at the source.</b> Because <c>None</c> is a real
    /// answer that another consumer depends on: <see cref="FactionStorePolicy"/> reads it to decide
    /// FCTM keeps no player store, and deliberately treats an <i>unresolved</i> tag as "keeps its
    /// store" so a world with no catalogue behaves. Making FCTM's type read "Factorum" there hands
    /// it a store back. The livery is a separate, lighter question and gets a separate answer.</para>
    ///
    /// <para>The fallback is keyed on <b>personality</b> rather than on the tag, because personality
    /// is already what carries this: <c>VanillaFleetSpawner</c>'s hull table is personality-keyed,
    /// and FCTM's own <c>factorum</c> personality already exists and is already assigned. A
    /// tag-keyed override would be a second statement of the same fact, free to disagree with it —
    /// and would leave a hand-made Factorum faction flying generic hulls.</para>
    /// </summary>
    public static class PrefabFactionHint
    {
        /// <summary>The hint meaning "no preference"; <see cref="IPrefabCatalogue.GetNamesForRole"/>
        /// reads it as the universal pool.</summary>
        public const string Universal = "All";

        /// <summary>SE's name for a faction it has no economic role for. Not an error and not an
        /// absence — see the class remarks.</summary>
        public const string NoSeType = "None";

        /// <summary>
        /// The livery to query the catalogue with.
        /// <paramref name="seFactionType"/> is what <see cref="IFactionTypeCatalogue"/> answered
        /// (null when it could not answer at all); <paramref name="personalityId"/> is the faction's
        /// assigned personality.
        ///
        /// <para>SE's type wins whenever it names one, so this can never quietly re-livery a faction
        /// the engine has an opinion about. Only a type of <see cref="NoSeType"/> — or none at all —
        /// consults the personality.</para>
        /// </summary>
        public static string Resolve(string seFactionType, string personalityId)
        {
            bool seHasAnswer = !string.IsNullOrEmpty(seFactionType)
                            && !string.Equals(seFactionType, NoSeType, StringComparison.OrdinalIgnoreCase);
            if (seHasAnswer) return seFactionType;

            string fromPersonality = ForPersonality(personalityId);
            return fromPersonality ?? Universal;
        }

        /// <summary>Whether a faction with this personality builds only its own livery for every
        /// role its livery has a hull for, and a generic hull only for a role it has none for.
        /// The Factorum has no drill hull, so it mines in a generic one and flies its own
        /// everywhere else.
        ///
        /// <para>Every other personality, and an absent one, keeps the union
        /// <see cref="PrefabRolePool.Combine"/> builds and ranks its own hulls against the
        /// universal ones on cost. A livery that is only a tie-break never wins against hulls
        /// one-thousandth its price, and the Factorum's hulls are that much dearer.</para>
        ///
        /// <para>Keyed on personality for the reason <see cref="Resolve"/> consults it. Provenance:
        /// FAC-479.</para></summary>
        public static bool LiveryIsMandatory(string personalityId)
        {
            return string.Equals(personalityId, "factorum", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The livery a personality implies, or null when it implies none.
        ///
        /// <para>Only personalities whose hulls are a distinct family in the catalogue appear here.
        /// <c>raider</c> is deliberately absent: SE already types every raider faction
        /// <c>Pirate</c>, so a row for it would only ever restate an answer SE has already given —
        /// and would fire on the one case where SE disagreed, which is the case worth respecting.
        /// </para></summary>
        private static string ForPersonality(string personalityId)
        {
            if (string.Equals(personalityId, "factorum", StringComparison.OrdinalIgnoreCase))
                return "Factorum";
            return null;
        }
    }
}
