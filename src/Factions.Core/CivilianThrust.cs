using System;

namespace Factions.Core
{
    /// <summary>Continuous civilian velocity control, independent of facing. The host supplies
    /// grid-local velocity, gravity and override-independent force capacity. No SE dependencies.</summary>
    public static class CivilianThrust
    {
        public const double ResponseSeconds = 2.0;
        public const double ApproachResponseSeconds = 1.0;
        public const double UpdateSeconds = 0.1;
        public const double VelocityDeadbandMs = 0.02;
        public const double OverrideDeadband = 0.005;

        /// <summary>Approach ceiling allowing for the velocity loop's response distance. Solves
        /// v²/(2a) + v·response = gap, and also slows the final position loop to avoid overshooting. Neither
        /// a speed floor nor the direction the nose points may force a burn through the rest point.</summary>
        public static double ApproachSpeedMs(double gapM, double decelMs2, double ceilingMs)
        {
            if (gapM <= 0 || decelMs2 <= 0 || ceilingMs <= 0) return 0;
            double ar = decelMs2 * ResponseSeconds;
            double brake = 2.0 * decelMs2 * gapM / (Math.Sqrt(ar * ar + 2.0 * decelMs2 * gapM) + ar);
            return Math.Min(ceilingMs, Math.Min(brake, gapM / (4.0 * ResponseSeconds)));
        }

        /// <summary>Signed axis fractions in [-1,1]. A velocity match in space yields zero thrust
        /// and coasts; in gravity it retains weight support. A slower or reversed velocity target
        /// freely commands reverse thrust. The host turns each sign into its own thruster bucket.</summary>
        public static Vec3 Fractions(Vec3 wantedMs, Vec3 velocityMs, Vec3 gravityMs2,
                                     double massKg, AxisThrust available, double responseSeconds = ResponseSeconds)
        {
            if (massKg <= 0 || responseSeconds <= 0) return new Vec3(0, 0, 0);
            return new Vec3(
                Axis(wantedMs.X - velocityMs.X, gravityMs2.X, massKg, available.RightN, available.LeftN, responseSeconds),
                Axis(wantedMs.Y - velocityMs.Y, gravityMs2.Y, massKg, available.UpN, available.DownN, responseSeconds),
                Axis(wantedMs.Z - velocityMs.Z, gravityMs2.Z, massKg, available.BackwardN, available.ForwardN, responseSeconds));
        }

        private static double Axis(double errorMs, double gravityMs2, double massKg,
                                   double positiveN, double negativeN, double responseSeconds)
        {
            if (Math.Abs(errorMs) <= VelocityDeadbandMs) errorMs = 0;
            double forceN = massKg * (errorMs / responseSeconds - gravityMs2);
            double capacityN = forceN >= 0 ? positiveN : negativeN;
            if (capacityN <= 0) return 0;
            return Math.Max(-1, Math.Min(1, forceN / capacityN));
        }

        /// <summary>Bound writes while retaining exact zero and full authority transitions.
        /// In particular, a small old burn is cleared when the new request is zero.</summary>
        public static bool ShouldWrite(double previous, double wanted)
        {
            return (wanted == 0 || wanted == 1) ? previous != wanted
                : Math.Abs(wanted - previous) >= OverrideDeadband;
        }
    }
}
