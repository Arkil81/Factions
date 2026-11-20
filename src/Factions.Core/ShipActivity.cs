using System;

namespace Factions.Core
{
    /// <summary>
    /// Ship traffic as a passive hold trickle — the fleet-side counterpart of
    /// <see cref="PresenceReinforce"/>, which measures player bodies.
    ///
    /// <para>A node accrues credit when a fleet its owner sent resolves a stop there. The credit
    /// decays on a half-life, and <c>WorldModel.DecayInfluence</c> nets a hold gain proportional to
    /// the saturated share of it against that pass's decay. At full credit the gain exceeds the
    /// decay, so a well-trafficked node climbs back towards full hold on traffic alone; a corridor
    /// nothing flies to bleeds out at the full rate and abandons.</para>
    ///
    /// <para><b>It is a real gain, and a slow one.</b> That is the point: traffic alone can recover
    /// a node taken at <see cref="FactionTuning.ConquerHoldCeiling"/>, which no amount of offsetting
    /// could. The net at saturation is the margin between
    /// <see cref="FactionTuning.ShipActivityHoldPerHour"/> and
    /// <see cref="FactionTuning.HoldDecayPerHour"/> — +0.06/hour at the shipped defaults, hours per
    /// node against a raid's single arrival, so it cannot out-pace a raid campaign.</para>
    ///
    /// <para><b>Stored as a value and a timestamp, not ticked.</b> Decay is exponential, so the
    /// level is derived on read, the same shape <see cref="PlayerSiege.IntegrityOf"/> uses. Nothing
    /// runs per frame and a restart cannot lose progress that was never written.</para>
    ///
    /// <para>Pure policy, single-sourced and offline-testable: the sim and the host compute the same
    /// numbers because there is only one copy of them.</para>
    /// </summary>
    public static class ShipActivity
    {
        /// <summary>True when the channel is configured at all. A non-positive half-life, cap or
        /// per-visit credit switches it off rather than making credit permanent or instantaneous.</summary>
        public static bool Enabled(FactionTuning t)
        {
            return t != null
                && t.ShipActivityHalfLifeSeconds > 0
                && t.ShipActivityCreditCap > 0
                && t.ShipActivityCreditPerVisit > 0;
        }

        /// <summary>This node's credit right now: the stored level halved once per
        /// <see cref="FactionTuning.ShipActivityHalfLifeSeconds"/> since it was stored.</summary>
        public static double CreditsOf(SpatialNode n, FactionTuning t, DateTime now)
        {
            if (n == null || !Enabled(t)) return 0.0;
            double stored = n.ShipActivityCredits;
            if (stored <= 0) return 0.0;
            if (stored > t.ShipActivityCreditCap) stored = t.ShipActivityCreditCap;

            double elapsed = (now - n.ShipActivityAtUtc).TotalSeconds;
            if (elapsed <= 0) return stored;
            return stored * Math.Pow(0.5, elapsed / t.ShipActivityHalfLifeSeconds);
        }

        /// <summary>Credits one owning-faction fleet stop at this node and returns the new level.
        /// Saturates at <see cref="FactionTuning.ShipActivityCreditCap"/> on write, so a hub with
        /// heavy traffic holds steady rather than banking indefinitely.</summary>
        public static double Credit(SpatialNode n, FactionTuning t, DateTime now)
        {
            if (n == null || !Enabled(t)) return 0.0;
            double level = CreditsOf(n, t, now) + t.ShipActivityCreditPerVisit;
            if (level > t.ShipActivityCreditCap) level = t.ShipActivityCreditCap;
            n.ShipActivityCredits = level;
            n.ShipActivityAtUtc = now;
            return level;
        }

        /// <summary>Hold this node's traffic gains over <paramref name="intervalSeconds"/>, before
        /// the caller subtracts that interval's decay from it. At saturated credit this equals
        /// <see cref="FactionTuning.ShipActivityHoldPerHour"/>'s share of the interval.</summary>
        public static double HoldGainForInterval(SpatialNode n, FactionTuning t, DateTime now, double intervalSeconds)
        {
            if (intervalSeconds <= 0 || !Enabled(t) || t.ShipActivityHoldPerHour <= 0) return 0.0;
            double credits = CreditsOf(n, t, now);
            if (credits <= 0) return 0.0;
            double share = credits / t.ShipActivityCreditCap;
            if (share > 1.0) share = 1.0;
            return share * t.ShipActivityHoldPerHour * intervalSeconds / 3600.0;
        }
    }
}
