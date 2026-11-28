using System;

namespace Factions.Core
{
    /// <summary>
    /// Slows an abstract fleet as it nears a player, so the player can reach it and make it spawn.
    ///
    /// <para><b>Why.</b> A fleet cruising at <see cref="FactionTuning.FleetSpeedKmPerMin"/> (500 km/min
    /// on production) crosses the <see cref="FactionTuning.ObservationRadiusKm"/> sphere in about three
    /// seconds, which is less than a spawn waits in the pacer. Near a player it therefore brakes to
    /// <see cref="FactionTuning.ChaseBrakeFloorKmPerMin"/>, 25 km/min: below the world's 30 km/min
    /// ship limit, so a ship at full speed can still close on it.</para>
    ///
    /// <para><b>Gradual, at a constant deceleration.</b> The cap rises from that floor at the observation
    /// radius to cruise speed at <see cref="FactionTuning.ChaseBrakeStartKm"/>, the way a ship braking to
    /// a point slows down. A player watching the map sees the fleet slow over many seconds rather than
    /// stop dead, so an intercept aimed at it does not overshoot. A fleet moving away from the player
    /// speeds up again along the same curve.</para>
    ///
    /// <para>The host applies the cap by pushing the fleet's schedule back each frame
    /// (<see cref="FactionsLoadActuator.Freeze"/>), so the fleet's arrival moves later by exactly
    /// the time the brake cost it.</para>
    /// </summary>
    public static class ChaseBrake
    {
        /// <summary>The step the nominal speed is measured over.</summary>
        public static readonly TimeSpan Probe = TimeSpan.FromSeconds(1);

        /// <summary>The fastest a fleet may move, in km/min, at <paramref name="nearestPlayerKm"/> from
        /// the nearest player. Positive infinity means no cap: the player is past
        /// <see cref="FactionTuning.ChaseBrakeStartKm"/>, no distance is known, or the speeds leave no
        /// room to brake in.</summary>
        public static double SpeedCapKmPerMin(double nearestPlayerKm, FactionTuning tuning)
        {
            if (tuning == null || double.IsNaN(nearestPlayerKm) || nearestPlayerKm < 0) return double.PositiveInfinity;
            double vMin = tuning.ChaseBrakeFloorKmPerMin > 0 ? tuning.ChaseBrakeFloorKmPerMin : tuning.FleetSpeedMinKmPerMin;
            double vMax = tuning.FleetSpeedKmPerMin;
            if (!(vMin > 0) || !(vMax > vMin)) return double.PositiveInfinity;

            double inner = tuning.ObservationRadiusKm, outer = tuning.ChaseBrakeStartKm;
            if (!(outer > 0)) return double.PositiveInfinity;
            if (nearestPlayerKm <= inner) return vMin;
            if (outer <= inner || nearestPlayerKm >= outer) return double.PositiveInfinity;

            double decel = (vMax * vMax - vMin * vMin) / (2.0 * (outer - inner));
            return Math.Sqrt(vMin * vMin + 2.0 * decel * (nearestPlayerKm - inner));
        }

        /// <summary>How far to push a fleet's schedule back after a frame of <paramref name="frame"/>, so
        /// it covered the frame at <paramref name="capKmPerMin"/> instead of
        /// <paramref name="nominalKmPerMin"/>. Zero when it is already no faster than the cap.</summary>
        public static TimeSpan FreezeFor(double nominalKmPerMin, double capKmPerMin, TimeSpan frame)
        {
            if (frame <= TimeSpan.Zero || double.IsInfinity(capKmPerMin) || double.IsNaN(capKmPerMin)) return TimeSpan.Zero;
            if (!(nominalKmPerMin > capKmPerMin)) return TimeSpan.Zero;
            double kept = capKmPerMin <= 0 ? 0.0 : capKmPerMin / nominalKmPerMin;
            return TimeSpan.FromTicks((long)(frame.Ticks * (1.0 - kept)));
        }

        /// <summary>The speed the fleet's own schedule moves it at, at <paramref name="nowUtc"/>, in km/min.
        /// Measured over the last <see cref="Probe"/>, so it is the leg's tier speed in cruise and its
        /// acceleration ramp near either end.</summary>
        public static double NominalSpeedKmPerMin(PendingFleetAction fleet, DateTime nowUtc, FactionTuning tuning)
        {
            Vec3 before = fleet.CurrentPosKm(nowUtc - Probe, tuning);
            Vec3 now = fleet.CurrentPosKm(nowUtc, tuning);
            return before.DistanceTo(now) / Probe.TotalMinutes;
        }
    }
}
