using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Inventory arithmetic for the Services Terminal settlements — the salvage yard crediting a
    /// wreck's parts into a node, and the repair bay drawing components out of one.
    ///
    /// <para>Kept here, away from the host, because the host side of that feature is three Harmony
    /// patches with no offline harness: the only part that can be tested at all is the arithmetic,
    /// so the arithmetic is the part that does not live in the patch. The host maps SE block and
    /// item definitions to inventory keys and hands the census over; everything below is pure.</para>
    ///
    /// <para>Provenance: FAC-280.</para>
    /// </summary>
    public static class ServiceSettlement
    {
        /// <summary>What a salvage credit actually moved: how many distinct inventory keys were
        /// touched and the total units added across them.</summary>
        public struct SalvageCredit
        {
            public int Keys;
            public long Units;
        }

        /// <summary>What a repair debit actually moved: units taken out of the node, and the units
        /// the census asked for that were not there. A non-zero <see cref="Shortfall"/> means the
        /// repair happened anyway — vanilla conjures the components, so refusing after the fact is
        /// not available — and the node simply had less than the job consumed.</summary>
        public struct RepairDebit
        {
            public long Debited;
            public long Shortfall;
        }

        /// <summary>Units of one census entry the yard actually recovers. Rounds down, so a
        /// fraction below one unit's worth recovers nothing rather than rounding up into stock the
        /// wreck did not carry. A fraction of 1.0 returns the census unchanged without going
        /// through floating point at all.</summary>
        public static long RecoveredUnits(long presentUnits, double recoveryFraction)
        {
            if (presentUnits <= 0) return 0;
            if (recoveryFraction <= 0.0) return 0;
            if (recoveryFraction >= 1.0) return presentUnits;
            double scaled = presentUnits * recoveryFraction;
            if (scaled < 1.0) return 0;
            return (long)Math.Floor(scaled);
        }

        /// <summary>Adds a scaled census into <paramref name="inventory"/> in place. Entries that
        /// scale to zero are skipped entirely, so a no-op salvage leaves no empty keys behind.</summary>
        public static SalvageCredit CreditSalvage(
            IDictionary<string, long> inventory,
            IEnumerable<KeyValuePair<string, long>> census,
            double recoveryFraction)
        {
            var result = new SalvageCredit();
            if (inventory == null || census == null) return result;

            foreach (KeyValuePair<string, long> entry in census)
            {
                if (string.IsNullOrEmpty(entry.Key)) continue;
                long units = RecoveredUnits(entry.Value, recoveryFraction);
                if (units <= 0) continue;

                long cur;
                inventory.TryGetValue(entry.Key, out cur);
                inventory[entry.Key] = cur + units;
                result.Keys++;
                result.Units += units;
            }
            return result;
        }

        /// <summary>Takes a census out of <paramref name="inventory"/> in place, floored at zero per
        /// key, and reports the exact deficit. A key the node does not hold at all contributes its
        /// whole census amount to the shortfall.</summary>
        public static RepairDebit DebitRepair(
            IDictionary<string, long> inventory,
            IEnumerable<KeyValuePair<string, long>> census)
        {
            var result = new RepairDebit();
            if (census == null) return result;

            foreach (KeyValuePair<string, long> entry in census)
            {
                if (string.IsNullOrEmpty(entry.Key) || entry.Value <= 0) continue;

                long held = 0;
                if (inventory != null) inventory.TryGetValue(entry.Key, out held);
                if (held < 0) held = 0;

                long taken = held < entry.Value ? held : entry.Value;
                if (taken > 0 && inventory != null) inventory[entry.Key] = held - taken;

                result.Debited   += taken;
                result.Shortfall += entry.Value - taken;
            }
            return result;
        }
    }
}
