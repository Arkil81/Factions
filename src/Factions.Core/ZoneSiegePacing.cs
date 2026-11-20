using System;

namespace Factions.Core
{
    /// <summary>What a raider firing at a protected station's zone should do this second.</summary>
    public enum ZoneFireCall
    {
        /// <summary>Hold fire: the siege is alive and the push is not due.</summary>
        Hold,
        /// <summary>No siege yet: fire until a hit lands and starts its clock.</summary>
        Start,
        /// <summary>The zone has gone too long without a hit: fire until one lands.</summary>
        KeepAlive,
        /// <summary>Empty the pool: the breach comes due before this attacker could drain a full one.</summary>
        Push,
    }

    /// <summary>
    /// Paces a raid's fire on a protected station's zone so it plays the siege
    /// (<see cref="PlayerSiege"/>) instead of emptying its magazines into it (FAC-490). The zone drops
    /// when its pool is empty once <see cref="FactionTuning.RaidZoneSiegeDwellSeconds"/> have passed
    /// since the siege began; the pool refills from empty in
    /// <see cref="FactionTuning.RaidZoneIntegrityRegenSeconds"/> and the siege lapses once it has stood
    /// full for <see cref="FactionTuning.RaidZoneRaidLapseSeconds"/>. Any hit clears the lapse, and fire
    /// on an empty pool is wasted. So: fire to start the siege, keep it alive with a hit every
    /// <see cref="KeepAliveSeconds"/>, and fire continuously only from the moment a full pool would
    /// just be emptied as the breach comes due.
    /// </summary>
    public static class ZoneSiegePacing
    {
        /// <summary>Lead added to the push on top of the drain time: rounds in flight, flush latency.</summary>
        public const double PushMarginSeconds = 5.0;

        /// <summary>A keep-alive is due this long after the zone's last hit: two thirds of the refill
        /// time, so the pool never refills and the lapse clock never starts.</summary>
        public static double KeepAliveSeconds(FactionTuning t)
        {
            double regen = t != null ? t.RaidZoneIntegrityRegenSeconds : 0;
            return regen > 0 ? regen * 2.0 / 3.0 : 20.0;
        }

        /// <summary>The call for an attacker whose fire on the zone around <paramref name="n"/> totals
        /// <paramref name="attackerDps"/>, on the world clock. <paramref name="secondsSinceOwnBurst"/>
        /// paces the keep-alive when the node carries no siege state to read (the pool switched off).</summary>
        public static ZoneFireCall Decide(SpatialNode n, FactionTuning t, DateTime now, double attackerDps,
                                          double secondsSinceOwnBurst)
        {
            if (n == null || t == null) return ZoneFireCall.Push;
            double max = PlayerSiege.MaxIntegrity(n, t);
            double keepAlive = KeepAliveSeconds(t);
            if (max <= 0)
                return secondsSinceOwnBurst >= keepAlive ? ZoneFireCall.KeepAlive : ZoneFireCall.Hold;
            if (n.ZoneBreachedUtc != default(DateTime)) return ZoneFireCall.Push;
            if (n.SiegeStartedUtc == default(DateTime)) return ZoneFireCall.Start;

            double regenRate = t.RaidZoneIntegrityRegenSeconds > 0 ? max / t.RaidZoneIntegrityRegenSeconds : 0;
            if (attackerDps > regenRate && regenRate > 0)
            {
                DateTime due = n.SiegeStartedUtc.AddSeconds(t.RaidZoneSiegeDwellSeconds);
                double drain = max / (attackerDps - regenRate);
                if (now >= due.AddSeconds(-(drain + PushMarginSeconds))) return ZoneFireCall.Push;
            }
            return (now - n.ZoneIntegrityAtUtc).TotalSeconds >= keepAlive ? ZoneFireCall.KeepAlive : ZoneFireCall.Hold;
        }

        /// <summary>Whether the call fires.</summary>
        public static bool Fires(ZoneFireCall call) { return call != ZoneFireCall.Hold; }
    }
}
