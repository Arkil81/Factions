using System;

namespace Factions.Core
{
    /// <summary>
    /// What a faction says and remembers when a player occupies its ground: a presence raid on one of
    /// its unfounded nodes (<see cref="PlayerSiege.BeginPresenceRaid"/>). SE-free; the host delivers
    /// the hail, charges the reputation and files the record.
    ///
    /// <para><b>The strips are the clock.</b> The occupier is already on the node, so proximity has
    /// nothing left to say; what grows is time on station, which the raid counts in hold strips
    /// (<see cref="SpatialNode.PresenceStrips"/>, one per
    /// <see cref="FactionTuning.RaidZoneSiegeDwellSeconds"/>). Each strip is hailed and charged.</para>
    ///
    /// <para><b>The enmity precondition holds by construction.</b> A presence raid starts only against
    /// an owner SE already calls the player's enemy, so nothing here can push a neutral standing into
    /// war.</para>
    ///
    /// <para>Provenance: FAC-483 (occupation), design in docs/fleet-events.md.</para>
    /// </summary>
    public static class Occupation
    {
        /// <summary>The occupation hail for strip <paramref name="strip"/> (1 = the raid has just
        /// begun), in chat. The first warns; every later one says it is still happening and will be
        /// remembered, which <see cref="FightLedger.RecordOccupation"/> makes true. Null for a strip
        /// below 1.</summary>
        public static string HailText(int strip, string factionName)
        {
            if (strip < 1) return null;
            string name = string.IsNullOrEmpty(factionName) ? "this faction's" : factionName;
            return strip == 1
                ? "You are occupying " + name + " ground. You will be treated as an aggressor."
                : "You are still on " + name + " ground. This will not be forgotten.";
        }

        /// <summary>Whether strip <paramref name="strip"/> is the opening one, which the voice says
        /// differently from the ones that follow.</summary>
        public static bool IsFirstStrip(int strip) { return strip <= 1; }

        /// <summary>Reputation one hold strip costs the occupier with the owner, as a positive amount:
        /// what SE itself charges for the block damage a strip costs on the station path,
        /// <see cref="FactionTuning.PlayerRaidBlockDamageForSuccess"/>. SE takes
        /// <paramref name="damagingPerStep"/> reputation per full
        /// <paramref name="reputationLossDamage"/> of damage (its <c>ReputationSettings</c>
        /// <c>DamageSettings</c>), so a presence strip costs exactly what shooting the same strip off a
        /// station would. 0 when any input is unset.
        ///
        /// <para>The station path charges absorbed zone fire on top. A presence raid has no zone to
        /// absorb anything, so block damage is the whole of the comparable cost.</para></summary>
        public static int ReputationPerStrip(double blockDamageForSuccess, double reputationLossDamage, int damagingPerStep)
        {
            if (blockDamageForSuccess <= 0 || reputationLossDamage <= 0 || damagingPerStep <= 0) return 0;
            return (int)Math.Floor(blockDamageForSuccess / reputationLossDamage) * damagingPerStep;
        }
    }
}
