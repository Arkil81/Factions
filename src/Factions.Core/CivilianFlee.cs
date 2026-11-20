using System;

namespace Factions.Core
{
    /// <summary>
    /// Where a civilian ship under fire runs to. The host aims a materialised Haul, Trade, Gift or
    /// Mine grid at <see cref="PointFrom"/> when another faction hits it, keeps it running while
    /// the hits keep coming, and hands it back to its leg once it has gone
    /// <see cref="HoldSeconds"/> without one. A convoy held in an interception window does not run:
    /// it is parked beside the raider to fight it out.
    /// </summary>
    public static class CivilianFlee
    {
        /// <summary>How far ahead of the ship, directly away from the shooter, the flee point sits.</summary>
        public const double DistanceKm = 8.0;

        /// <summary>The ship keeps running this long after the last hit.</summary>
        public const double HoldSeconds = 20.0;

        /// <summary>Least time between two re-aims of a running ship; a re-aim rewires its autopilot.</summary>
        public const double ReaimSeconds = 5.0;

        /// <summary>A ship this close to its flee point is re-aimed on the next hit whatever the interval.</summary>
        public const double ReachedKm = 1.0;

        /// <summary>In gravity, how much of the run is climb: the away direction gains this much of
        /// the local up axis before it is normalised, so a ship never runs into the ground.</summary>
        public const double ClimbShare = 0.5;

        /// <summary>The point <see cref="DistanceKm"/> from <paramref name="selfKm"/>, directly away
        /// from <paramref name="shooterKm"/>, tilted up by <see cref="ClimbShare"/> when
        /// <paramref name="up"/> is given. A shooter on top of the ship gives no direction; the ship
        /// then climbs, or in deep space runs along +X.</summary>
        public static Vec3 PointFrom(Vec3 selfKm, Vec3 shooterKm, Vec3? up)
        {
            double x = selfKm.X - shooterKm.X, y = selfKm.Y - shooterKm.Y, z = selfKm.Z - shooterKm.Z;
            double l = Math.Sqrt(x * x + y * y + z * z);
            if (l > 1e-9) { x /= l; y /= l; z /= l; }
            else { x = 0; y = 0; z = 0; }
            if (up.HasValue)
            {
                x += up.Value.X * ClimbShare; y += up.Value.Y * ClimbShare; z += up.Value.Z * ClimbShare;
            }
            l = Math.Sqrt(x * x + y * y + z * z);
            if (l <= 1e-9) { x = 1; y = 0; z = 0; l = 1; }
            return new Vec3(selfKm.X + x / l * DistanceKm, selfKm.Y + y / l * DistanceKm, selfKm.Z + z / l * DistanceKm);
        }

        /// <summary>Whether a ship already running from a hit at <paramref name="lastHitUtc"/> is still
        /// running at <paramref name="nowUtc"/>.</summary>
        public static bool StillRunning(DateTime lastHitUtc, DateTime nowUtc)
        {
            return (nowUtc - lastHitUtc).TotalSeconds < HoldSeconds;
        }
    }
}
