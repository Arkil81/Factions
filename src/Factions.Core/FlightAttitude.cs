using System;

namespace Factions.Core
{
    /// <summary>The orientation a manoeuvre asks the hull's reference block to hold, in world terms.
    /// <see cref="HasUp"/> false means "keep the current roll".</summary>
    public struct AttitudeTarget
    {
        public Vec3 Forward;
        public Vec3 Up;
        public bool HasUp;
    }

    /// <summary>
    /// Resolves the attitude a native manoeuvre holds, using the convention SE's own autopilot flies.
    ///
    /// <para>In gravity the hull's up points away from the planet and its nose lies along the
    /// horizontal part of where it is going: <c>MyAutopilotComponentDefinition.AlignToGravity</c>
    /// defaults true, so a remote control climbs level and cruises nose-first. Holding the same
    /// attitude keeps the native controller, the cruise autopilot and the lift gate on one lift
    /// axis. In zero gravity there is no up to keep, so the nose goes straight at the bearing and the
    /// roll is left alone.</para>
    /// </summary>
    public static class FlightAttitude
    {
        private const double MinGravityMs2 = 1e-3;
        private const double MinHorizontal = 1e-3;

        /// <param name="faceDir">Where the manoeuvre wants the nose, world direction; need not be unit.</param>
        /// <param name="gravity">Local gravity vector, m/s². Zero for none.</param>
        /// <param name="currentNose">The reference block's current forward, world direction. Keeps the
        /// heading when the bearing is straight up or down.</param>
        public static AttitudeTarget Resolve(Vec3 faceDir, Vec3 gravity, Vec3 currentNose)
        {
            double g = Length(gravity);
            if (g < MinGravityMs2)
                return new AttitudeTarget { Forward = Unit(faceDir, currentNose), HasUp = false };

            Vec3 down = Scale(gravity, 1.0 / g);
            Vec3 up = Scale(down, -1.0);

            Vec3 horizontal = Reject(faceDir, down);
            if (Length(horizontal) < MinHorizontal * Math.Max(1.0, Length(faceDir)))
                horizontal = Reject(currentNose, down);
            if (Length(horizontal) < MinHorizontal)
                horizontal = Reject(Math.Abs(down.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 0, 1), down);

            return new AttitudeTarget { Forward = Unit(horizontal, horizontal), Up = up, HasUp = true };
        }

        /// <summary>As <see cref="Resolve"/>, for a hull pointing fixed guns: in gravity the nose may
        /// also pitch towards <paramref name="faceDir"/>, by at most <paramref name="maxPitchRad"/>
        /// above or below the horizon, with up kept square to it in the vertical plane — no roll. In
        /// zero gravity it is <see cref="Resolve"/>.</summary>
        public static AttitudeTarget ResolveAimed(Vec3 faceDir, Vec3 gravity, Vec3 currentNose, double maxPitchRad)
        {
            AttitudeTarget level = Resolve(faceDir, gravity, currentNose);
            if (!level.HasUp) return level;

            double len = Length(faceDir);
            if (len < 1e-9) return level;
            double sinEl = (faceDir.X * level.Up.X + faceDir.Y * level.Up.Y + faceDir.Z * level.Up.Z) / len;
            if (sinEl > 1) sinEl = 1;
            if (sinEl < -1) sinEl = -1;
            double el = Math.Asin(sinEl);
            double cap = Math.Max(0, maxPitchRad);
            if (el > cap) el = cap;
            if (el < -cap) el = -cap;

            double c = Math.Cos(el), s = Math.Sin(el);
            Vec3 h = level.Forward, u = level.Up;
            return new AttitudeTarget
            {
                Forward = new Vec3(h.X * c + u.X * s, h.Y * c + u.Y * s, h.Z * c + u.Z * s),
                Up = new Vec3(u.X * c - h.X * s, u.Y * c - h.Y * s, u.Z * c - h.Z * s),
                HasUp = true,
            };
        }

        private static Vec3 Reject(Vec3 v, Vec3 unitAxis)
        {
            double d = v.X * unitAxis.X + v.Y * unitAxis.Y + v.Z * unitAxis.Z;
            return new Vec3(v.X - d * unitAxis.X, v.Y - d * unitAxis.Y, v.Z - d * unitAxis.Z);
        }

        private static Vec3 Unit(Vec3 v, Vec3 fallback)
        {
            double len = Length(v);
            if (len > 1e-9) return Scale(v, 1.0 / len);
            double fl = Length(fallback);
            return fl > 1e-9 ? Scale(fallback, 1.0 / fl) : new Vec3(0, 0, -1);
        }

        private static Vec3 Scale(Vec3 v, double k) { return new Vec3(v.X * k, v.Y * k, v.Z * k); }

        private static double Length(Vec3 v) { return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z); }
    }
}
