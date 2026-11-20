using System;

namespace Factions.Core
{
    /// <summary>Something a controlled grid must not fly into, as the host sees it this tick: a
    /// bounding sphere and the velocity it is moving at. World metres and m/s, like
    /// <see cref="FlightSample"/>.</summary>
    public struct FlightObstacle
    {
        public Vec3 CenterM;
        public Vec3 VelMs;
        public double RadiusM;
    }

    /// <summary>The steer-around term every manoeuvre's command passes through. Pure geometry over
    /// the obstacle spheres the host put in <see cref="FlightSample.Obstacles"/>; it holds no
    /// state, so the same inputs always produce the same command.
    ///
    /// <para><b>The threat test is closest approach, not proximity.</b> For each obstacle the
    /// relative motion is extrapolated to the moment the two would be closest — clamped to the
    /// look-ahead and, on an approach, to the moment the grid reaches its goal — and the obstacle is
    /// a threat when the two spheres, plus <see cref="FlightSample.AvoidMarginM"/>, would overlap
    /// then. A grid already overlapping evaluates at time zero, which is the same test.</para>
    ///
    /// <para><b>Urgency is continuous</b> in both the miss distance and the time to it, falling to
    /// zero at the edge of the separation sphere and at the end of the look-ahead. The command is a
    /// blend weighted by it, so a threat that is just appearing bends the course a little rather
    /// than switching it — the property that keeps a grid from chattering at the boundary.</para>
    ///
    /// <para><b>The steer direction is where the grid would be, relative to the obstacle, at the
    /// evaluated moment.</b> At the true closest approach that is perpendicular to the relative
    /// velocity; earlier than it, it also points back along it, so the command brakes the closure
    /// as well as sidestepping. A dead-on course has no such offset, and falls back to a fixed
    /// perpendicular of the relative velocity: two grids meeting head-on see opposite relative
    /// velocities, so they sidestep to opposite sides instead of into each other.</para></summary>
    public static class FlightAvoidance
    {
        /// <summary>Below this share of the separation radius a lateral offset is treated as dead-on
        /// and the fixed perpendicular is used instead — the offset's own direction is noise there.</summary>
        private const double DeadOnFraction = 0.02;

        /// <summary>A braking command is left alone below this urgency. Braking already slows the
        /// closure, and the thrust command is direction-only: any non-zero steer is full lateral
        /// thrust, so a faint threat must not be allowed to turn a brake into one.</summary>
        public const double BrakeOverrideUrgency = 0.25;

        /// <summary>The worst threat in a sample, and the direction that answers every threat
        /// together.</summary>
        public struct Assessment
        {
            /// <summary>0 means no threat; 1 means overlapping now, or about to on a dead-on course.</summary>
            public double Urgency;

            /// <summary>Index into <see cref="FlightSample.Obstacles"/> of the most urgent threat, or
            /// -1 when there is none.</summary>
            public int WorstIndex;

            /// <summary>Unit steer direction, world axes; zero when there is no threat.</summary>
            public Vec3 SteerDir;

            /// <summary>The most urgent threat's evaluated time and miss distance, and the separation
            /// it was tested against — for the host's log line.</summary>
            public double TimeSec;
            public double MissM;
            public double SeparationM;
        }

        /// <summary>Whether the sample carries what the avoidance term needs to run at all.</summary>
        public static bool Active(FlightSample s)
        {
            return s.Obstacles != null && s.ObstacleCount > 0
                && s.SelfRadiusM > 0 && s.AvoidLookaheadSec > 0;
        }

        /// <summary>Reads every obstacle in the sample and returns the combined answer.
        /// <paramref name="goal"/> is where the manoeuvre means to come to rest, or null when it
        /// does not stop (a launch): the grid is not steered off a course that ends before it would
        /// reach the obstacle, and an obstacle whose separation sphere contains the goal is shrunk
        /// to leave the goal on its surface, so the grid can still arrive.</summary>
        public static Assessment Assess(FlightSample s, Vec3? goal)
        {
            var a = new Assessment { WorstIndex = -1 };
            if (!Active(s)) return a;

            Vec3 self = s.SelfCenterM;
            double speed = Length(s.VelMs);

            // Time until the grid reaches its goal at its current speed. Past it, the grid has
            // stopped, so what its extrapolated straight line would hit is not a threat.
            double tLimit = s.AvoidLookaheadSec;
            if (goal.HasValue && speed > 1e-6)
            {
                double tGoal = Length(Sub(goal.Value, self)) / speed;
                if (tGoal < tLimit) tLimit = tGoal;
            }

            int count = s.ObstacleCount < s.Obstacles.Length ? s.ObstacleCount : s.Obstacles.Length;
            Vec3 sum = new Vec3(0, 0, 0);

            for (int i = 0; i < count; i++)
            {
                FlightObstacle o = s.Obstacles[i];
                if (o.RadiusM < 0) continue;

                double sep = s.SelfRadiusM + o.RadiusM + (s.AvoidMarginM > 0 ? s.AvoidMarginM : 0);
                if (goal.HasValue)
                {
                    double goalToObstacle = Length(Sub(goal.Value, o.CenterM));
                    if (goalToObstacle < sep) sep = goalToObstacle;
                }
                if (sep <= 1e-6) continue;

                Vec3 r = Sub(o.CenterM, self);      // to the obstacle
                Vec3 v = Sub(s.VelMs, o.VelMs);     // closing velocity, self relative to it
                double vv = Dot(v, v);

                double t = vv > 1e-9 ? Dot(r, v) / vv : 0;
                if (t < 0) t = 0;
                if (t > tLimit) t = tLimit;

                // Where the grid sits relative to the obstacle at t.
                Vec3 m = Sub(Scale(v, t), r);
                double miss = Length(m);
                if (miss >= sep) continue;

                double u = (1.0 - miss / sep) * (s.AvoidLookaheadSec > 0 ? 1.0 - t / s.AvoidLookaheadSec : 1.0);
                if (u <= 0) continue;

                Vec3 dir = SteerDirection(m, v, sep);
                if (Length(dir) < 1e-9) continue;

                sum = Add(sum, Scale(dir, u));
                if (u > a.Urgency)
                {
                    a.Urgency = u;
                    a.WorstIndex = i;
                    a.TimeSec = t;
                    a.MissM = miss;
                    a.SeparationM = sep;
                    a.SteerDir = dir;
                }
            }

            if (a.WorstIndex < 0) return a;

            // Threats on opposite sides can cancel; then the worst one decides.
            double sumLen = Length(sum);
            if (sumLen > 1e-6) a.SteerDir = Scale(sum, 1.0 / sumLen);
            return a;
        }

        /// <summary>Blends the steer into a manoeuvre's command. A thrusting command is turned
        /// toward the steer in proportion to urgency; a braking one (zero) is replaced by the steer
        /// only past <see cref="BrakeOverrideUrgency"/>. Replacing a brake gives up nothing: SE
        /// dampens every velocity component not along the commanded direction, so a sideways
        /// command still sheds the speed that was carrying the grid at the obstacle.</summary>
        public static Vec3 Compose(Vec3 moveDir, Assessment a)
        {
            if (a.WorstIndex < 0 || a.Urgency <= 0) return moveDir;

            double u = a.Urgency > 1 ? 1 : a.Urgency;
            if (Length(moveDir) < 1e-9)
                return u >= BrakeOverrideUrgency ? a.SteerDir : moveDir;

            Vec3 blended = Add(Scale(moveDir, 1.0 - u), Scale(a.SteerDir, u));
            double len = Length(blended);
            return len > 1e-9 ? Scale(blended, 1.0 / len) : a.SteerDir;
        }

        /// <summary>Unit direction away from the obstacle given the grid's offset from it at the
        /// evaluated moment. Dead-on offsets take a fixed perpendicular of the closing velocity.</summary>
        private static Vec3 SteerDirection(Vec3 offset, Vec3 closing, double sep)
        {
            double len = Length(offset);
            double cl = Length(closing);

            if (cl > 1e-6)
            {
                // Lateral part of the offset: its component across the closing velocity.
                Vec3 along = Scale(closing, Dot(offset, closing) / (cl * cl));
                Vec3 lateral = Sub(offset, along);
                if (Length(lateral) < DeadOnFraction * sep)
                {
                    Vec3 perp = Perpendicular(closing);
                    // Keep whatever brakes the closure; replace only the missing sideways part.
                    Vec3 withSide = Add(Scale(along, 1.0 / sep), perp);
                    double wl = Length(withSide);
                    return wl > 1e-9 ? Scale(withSide, 1.0 / wl) : perp;
                }
            }

            return len > 1e-9 ? Scale(offset, 1.0 / len) : new Vec3(0, 0, 0);
        }

        /// <summary>A fixed unit perpendicular of <paramref name="v"/>. Antisymmetric — the
        /// perpendicular of -v is the negation of the perpendicular of v — which is what sends two
        /// grids on a dead-on course to opposite sides.</summary>
        private static Vec3 Perpendicular(Vec3 v)
        {
            Vec3 p = Cross(v, new Vec3(0, 0, 1));
            if (Length(p) < 1e-6 * Length(v)) p = Cross(v, new Vec3(1, 0, 0));
            double len = Length(p);
            return len > 1e-12 ? Scale(p, 1.0 / len) : new Vec3(0, 0, 0);
        }

        private static Vec3 Sub(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }

        private static Vec3 Add(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }

        private static Vec3 Scale(Vec3 v, double k) { return new Vec3(v.X * k, v.Y * k, v.Z * k); }

        private static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        private static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        private static double Length(Vec3 v) { return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z); }
    }
}
