using System;

namespace Factions.Core
{
    /// <summary>
    /// How much a viewer has learned about a contact. Cumulative: each tier includes everything the
    /// tiers below it reveal. A byte, because it is meant for the wire (docs/fog-of-war.md →
    /// Intelligence grades).
    /// </summary>
    public enum IntelTier : byte
    {
        /// <summary>Not detected.</summary>
        None = 0,
        /// <summary>It exists: position, velocity, a size bucket.</summary>
        Contact = 1,
        /// <summary>Hull count and grid class for a fleet; station or field, zone and economic stage
        /// for a node.</summary>
        Class = 2,
        /// <summary>Owner and relation colour; a node's name and capabilities.</summary>
        Identity = 3,
        /// <summary>Armament and strength band; a node's defence band.</summary>
        Armament = 4,
        /// <summary>The viewer's own faction: everything, at any range.</summary>
        Full = 5,
    }

    /// <summary>
    /// One detection test's answer, with the signal-to-noise margin beside the verdict.
    ///
    /// <para><b>The margin is the sensor's, never the broadcast floor's.</b> A grid announcing itself
    /// is detected out to its broadcast radius (FAC-140), but that is the target talking, not the
    /// sensor measuring it, and it is already credited by the transponder rule. So a contact found
    /// only through its broadcast, or seen past a planet only because it broadcasts, reads
    /// <see cref="SensorSees"/> false and a margin of <see cref="double.NegativeInfinity"/>: detected,
    /// at Contact, with nothing measured.</para>
    /// </summary>
    public struct SensorReading
    {
        public bool Detected;
        /// <summary>Whether the sensor half passed on its own: in range on signature, and not
        /// blocked by a body.</summary>
        public bool SensorSees;
        /// <summary><c>20·log10(detectionKm / distanceKm)</c>: 0 dB at the edge of range, +6 dB per
        /// halving of distance, capped at <see cref="IntelGrade.MaxMarginDb"/>. Negative infinity
        /// unless <see cref="SensorSees"/>.</summary>
        public double MarginDb;

        public static readonly SensorReading NotDetected = new SensorReading
        {
            Detected = false, SensorSees = false, MarginDb = double.NegativeInfinity
        };
    }

    /// <summary>
    /// The one rule that turns a detection into a tier. The HUD scanner and the map both read this,
    /// so a contact cannot be graded differently on the two surfaces (the FAC-125 failure, one level
    /// up). Design: docs/fog-of-war.md → Intelligence grades; slice 1 is FAC-526.
    /// </summary>
    public static class IntelGrade
    {
        /// <summary>Where the margin stops growing. A contact on top of the sensor would otherwise
        /// read infinite, and every tier is long open well before this.</summary>
        public const double MaxMarginDb = 60.0;

        /// <summary>The margin for a target at <paramref name="distanceKm"/> inside a detection range
        /// of <paramref name="detectionKm"/>. Negative outside range; negative infinity for no range.</summary>
        public static double MarginDb(double detectionKm, double distanceKm)
        {
            if (detectionKm <= 0) return double.NegativeInfinity;
            if (distanceKm <= 0) return MaxMarginDb;
            double db = 20.0 * Math.Log10(detectionKm / distanceKm);
            return db > MaxMarginDb ? MaxMarginDb : db;
        }

        /// <summary>
        /// The tier the sensor earns on its own, with hysteresis against <paramref name="previous"/>.
        ///
        /// <para>A tier opens at its threshold and, once held, closes only below threshold minus
        /// <c>IntelHysteresisDb</c> — the ATC squelch's rule — so a contact sitting on a boundary
        /// does not flicker between two descriptions. Contact needs no margin: detection is enough,
        /// which is what lets a broadcast-only contact exist at all.</para>
        /// </summary>
        public static IntelTier ObservedTier(SensorReading reading, IntelTier previous, FactionTuning tuning)
        {
            if (!reading.Detected) return IntelTier.None;
            double classDb, identityDb, armamentDb, hysteresisDb;
            Thresholds(tuning, out classDb, out identityDb, out armamentDb, out hysteresisDb);

            double m = reading.MarginDb;
            if (!Opens(m, classDb, previous >= IntelTier.Class, hysteresisDb)) return IntelTier.Contact;
            if (!Opens(m, identityDb, previous >= IntelTier.Identity, hysteresisDb)) return IntelTier.Class;
            if (!Opens(m, armamentDb, previous >= IntelTier.Armament, hysteresisDb)) return IntelTier.Identity;
            return IntelTier.Armament;
        }

        /// <summary>
        /// The viewer's tier for one contact.
        ///
        /// <list type="bullet">
        /// <item><b>Own faction</b> is <see cref="IntelTier.Full"/> whatever the sensor says.</item>
        /// <item><b>Not detected</b> is <see cref="IntelTier.None"/>.</item>
        /// <item><b>A transponder</b> — a civilian mission, a station, a broadcasting grid, or a fleet
        /// striking the viewer's own node — lifts a detected contact to at least
        /// <see cref="IntelTier.Identity"/>. It never creates a detection.</item>
        /// </list>
        /// </summary>
        public static IntelTier Of(SensorReading reading, IntelTier previous, bool ownFaction,
                                   bool transponder, FactionTuning tuning)
        {
            if (ownFaction) return IntelTier.Full;
            if (!reading.Detected) return IntelTier.None;
            IntelTier tier = ObservedTier(reading, previous, tuning);
            if (transponder && tier < IntelTier.Identity) tier = IntelTier.Identity;
            return tier;
        }

        /// <summary>Hull count and mission — the silhouette — are a Class-tier observation: gross
        /// size and shape, which is what the sensor measured to see the contact at all.</summary>
        public static bool ShowsClass(IntelTier tier) { return tier >= IntelTier.Class; }

        /// <summary>Owner and relation colour. Below this a contact is grey and unnamed.</summary>
        public static bool ShowsIdentity(IntelTier tier) { return tier >= IntelTier.Identity; }

        /// <summary><see cref="FactionTuning.IntelStaleAfterHours"/> as a span; zero (never stale)
        /// for a non-positive value.</summary>
        public static TimeSpan StaleAfter(FactionTuning tuning)
        {
            double hours = tuning != null ? tuning.IntelStaleAfterHours : 72.0;
            return hours > 0 ? TimeSpan.FromHours(hours) : TimeSpan.Zero;
        }

        /// <summary>Whether a fleet on this mission broadcasts its identity. Civilian missions want to
        /// be found; military ones run silent and are identified only by margin. FAC-373's
        /// disclosure rule.</summary>
        public static bool IsCivilianMission(ActionKind kind)
        {
            switch (kind)
            {
                case ActionKind.Trade:
                case ActionKind.Haul:
                case ActionKind.Mine:
                case ActionKind.Ferry:
                case ActionKind.Gift:
                    return true;
                default:
                    return false;
            }
        }

        private static bool Opens(double marginDb, double thresholdDb, bool held, double hysteresisDb)
        {
            if (marginDb >= thresholdDb) return true;
            return held && marginDb >= thresholdDb - hysteresisDb;
        }

        private static void Thresholds(FactionTuning t, out double classDb, out double identityDb,
                                       out double armamentDb, out double hysteresisDb)
        {
            if (t == null) t = Defaults;
            classDb = t.IntelClassDb;
            identityDb = t.IntelIdentityDb;
            armamentDb = t.IntelArmamentDb;
            hysteresisDb = t.IntelHysteresisDb > 0 ? t.IntelHysteresisDb : 0;
        }

        private static readonly FactionTuning Defaults = new FactionTuning();
    }
}
