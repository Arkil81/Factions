using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One wing block of Digi's Aerodynamic Wings mod, in grid-local axes.</summary>
    public struct Wing
    {
        /// <summary>The block's Up: the only direction the wing pushes along.</summary>
        public Vec3 Normal;
        /// <summary>The airflow direction the wing works in; it is inert while <c>Chord·v ≤ 0</c>.</summary>
        public Vec3 Chord;
        public double ForceMul;
    }

    /// <summary>The force Digi's Aerodynamic Wings mod (workshop 473571246) applies to a grid, and
    /// what the analogue thrust law must add to fly through it.
    ///
    /// <para>The mod applies, per working wing and per frame, <c>-n (n·v) · mul · min(2|v|², 10000) ·
    /// atmosphere</c> at the physical group's centre of mass: a damper on the velocity across the
    /// wing that grows with airspeed, and no torque. Left alone it leaves a proportional velocity loop
    /// with a large standing error, so a hull at cruise can barely climb or descend.</para></summary>
    public static class WingLift
    {
        public const long ModId = 473571246;
        public const double MinAtmosphere = 0.4;
        public const double MaxAtmosphere = 0.7;
        /// <summary>The mod's <c>2|v|²</c> floor (5 m/s) and ceiling (about 71 m/s).</summary>
        public const double MinSpeedTerm = 50;
        public const double MaxSpeedTerm = 10000;

        /// <summary>Describes a wing block from its subtype and its grid-local Up, Left and Forward.
        /// False for every block that is not one of the mod's wings.</summary>
        public static bool TryDescribe(string subtype, Vec3 up, Vec3 left, Vec3 forward, out Wing wing)
        {
            wing = new Wing();
            double mul, skew;
            switch (subtype)
            {
                case "WingStreight": mul = 0.75; skew = 0; break;
                case "WingAngled1":  mul = 1.0;  skew = 0.15; break;
                case "WingAngled2":  mul = 1.25; skew = 0.35; break;
                default: return false;
            }
            wing.Normal = up;
            wing.Chord = Normalize(new Vec3(left.X + forward.X * skew, left.Y + forward.Y * skew,
                                            left.Z + forward.Z * skew));
            wing.ForceMul = mul;
            return true;
        }

        /// <summary>The mod's atmosphere factor from air density: 0 at 0.4 or thinner, 1 at 0.7 or denser.</summary>
        public static double Atmosphere(double airDensity)
        {
            double a = (airDensity - MinAtmosphere) / (MaxAtmosphere - MinAtmosphere);
            return a < 0 ? 0 : a > 1 ? 1 : a;
        }

        private static double SpeedTerm(Vec3 v)
        {
            double sp = 2.0 * Dot(v, v);
            return sp > MaxSpeedTerm ? MaxSpeedTerm : sp;
        }

        /// <summary>The total wing force at velocity <paramref name="velocityMs"/>, newtons.</summary>
        public static Vec3 ForceN(IList<Wing> wings, Vec3 velocityMs, double atmosphere)
        {
            var f = new Vec3(0, 0, 0);
            if (wings == null || atmosphere <= 0) return f;
            double sp = SpeedTerm(velocityMs);
            if (sp < MinSpeedTerm) return f;
            for (int i = 0; i < wings.Count; i++)
            {
                Wing w = wings[i];
                if (Dot(w.Chord, velocityMs) <= 0) continue;
                f = Add(f, Scale(w.Normal, -Dot(w.Normal, velocityMs) * w.ForceMul * sp * atmosphere));
            }
            return f;
        }

        /// <summary>The force the thrusters add so the wings stop resisting the commanded velocity:
        /// each wing's force with the cross-wing speed taken from the target and the airspeed and
        /// active set from the current velocity.
        ///
        /// <para>Evaluated wholly at the target it overshoots while the hull is still slow (it
        /// cancels a force the wings are not making yet); evaluated wholly at the current velocity
        /// it slows convergence. This mix turns the wings into extra damping toward the target and is
        /// exact once the hull gets there.</para></summary>
        public static Vec3 CounterN(IList<Wing> wings, Vec3 wantedMs, Vec3 velocityMs, double atmosphere)
        {
            var f = new Vec3(0, 0, 0);
            if (wings == null || atmosphere <= 0) return f;
            double sp = SpeedTerm(velocityMs);
            if (sp < MinSpeedTerm) return f;
            for (int i = 0; i < wings.Count; i++)
            {
                Wing w = wings[i];
                if (Dot(w.Chord, velocityMs) <= 0) continue;
                f = Add(f, Scale(w.Normal, Dot(w.Normal, wantedMs) * w.ForceMul * sp * atmosphere));
            }
            return f;
        }

        /// <summary>The largest share of <paramref name="wantedMs"/>, at least
        /// <paramref name="floorMs"/> of speed, whose steady flight (weight plus wing force) the
        /// thrusters can hold on every axis. 1 when the full velocity fits, and also when the hull
        /// cannot hold itself up at rest, since slowing down would not help that.</summary>
        public static double FittingScale(IList<Wing> wings, Vec3 wantedMs, Vec3 gravityMs2, double massKg,
                                          AxisThrust available, double atmosphere, double floorMs)
        {
            if (wings == null || wings.Count == 0 || atmosphere <= 0 || massKg <= 0) return 1;
            double speed = Math.Sqrt(Dot(wantedMs, wantedMs));
            if (speed <= 1e-9) return 1;
            if (Fits(wings, wantedMs, 1, gravityMs2, massKg, available, atmosphere)) return 1;
            if (!Fits(wings, wantedMs, 0, gravityMs2, massKg, available, atmosphere)) return 1;
            double lo = 0, hi = 1;
            for (int i = 0; i < 20; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Fits(wings, wantedMs, mid, gravityMs2, massKg, available, atmosphere)) lo = mid;
                else hi = mid;
            }
            double floor = floorMs > 0 ? Math.Min(1, floorMs / speed) : 0;
            return Math.Max(lo, floor);
        }

        private static bool Fits(IList<Wing> wings, Vec3 wantedMs, double s, Vec3 g, double m,
                                 AxisThrust a, double atmosphere)
        {
            Vec3 v = Scale(wantedMs, s);
            Vec3 wing = ForceN(wings, v, atmosphere);
            // Steady flight: thrust = -(weight + wing force).
            return FitsAxis(-(m * g.X + wing.X), a.RightN, a.LeftN)
                && FitsAxis(-(m * g.Y + wing.Y), a.UpN, a.DownN)
                && FitsAxis(-(m * g.Z + wing.Z), a.BackwardN, a.ForwardN);
        }

        private static bool FitsAxis(double needN, double positiveN, double negativeN)
        {
            return needN >= 0 ? needN <= positiveN + 1e-6 : -needN <= negativeN + 1e-6;
        }

        private static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        private static Vec3 Add(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        private static Vec3 Scale(Vec3 a, double k) { return new Vec3(a.X * k, a.Y * k, a.Z * k); }
        private static Vec3 Normalize(Vec3 a)
        {
            double l = Math.Sqrt(Dot(a, a));
            return l > 1e-12 ? Scale(a, 1.0 / l) : a;
        }
    }
}
