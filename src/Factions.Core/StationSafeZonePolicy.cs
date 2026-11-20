using System;

namespace Factions.Core
{
    /// <summary>
    /// Whether a faction's stations materialise inside a protective safe zone.
    ///
    /// <para>A station's safe zone is what makes it unshootable. Ours are created with
    /// <c>MySafeZoneAccess.Blacklist</c> and empty lists — anyone may fly in, but no combat,
    /// grinding or building happens inside — so a faction whose stations are meant to be a target
    /// must not have one. Operator, 2026-09-01: the Factorum's stations should be directly
    /// attackable.</para>
    ///
    /// <para>Keyed on personality for the same reason <see cref="PrefabFactionHint"/> is: it is the
    /// existing carrier for "what kind of faction is this", already assigned and already persisted.
    /// A tag test would be a second statement of the same fact.</para>
    ///
    /// <para><b>One consequence worth stating, because it is invisible otherwise.</b> SE's
    /// <c>MyBotAnchorComponent</c> registers a station bot anchor only for a station whose
    /// <c>SafeZoneEntityId</c> is non-zero, so an unprotected station grows no walking NPC crew.
    /// For the Factorum that reads correctly — it is a machine intelligence, not a trading post —
    /// but it is a real effect of this policy and not only a combat one.</para>
    /// </summary>
    public static class StationSafeZonePolicy
    {
        /// <summary>False when this faction's stations should stand unprotected and attackable.
        /// Every other personality — and an absent one — keeps the safe zone, so this can only ever
        /// remove protection where it is asked for.</summary>
        public static bool SpawnsSafeZone(string personalityId)
        {
            return !string.Equals(personalityId, "factorum", StringComparison.OrdinalIgnoreCase);
        }
    }
}
