using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Outcome of <see cref="LaunchClearance.Resolve"/>.</summary>
    public struct LaunchClearanceResult
    {
        /// <summary>Unit bearing to depart along.</summary>
        public Vec3 DirWorld;

        /// <summary>A bearing other than the one to the target was chosen.</summary>
        public bool Adjusted;

        /// <summary>Nothing probed clear; <see cref="DirWorld"/> is the bearing to the target,
        /// i.e. the caller's behaviour without this pass — never worse.</summary>
        public bool Blocked;

        /// <summary>Angle between the chosen bearing and the bearing to the target, radians.</summary>
        public double DeflectionRad;
    }

    /// <summary>Chooses which way a grid leaves its origin when the way to its destination is not
    /// the way out of the dock. Pure geometry behind an injected probe, kept SE-free in Core so the
    /// choice is unit-testable; the host owns the physics query — the same split
    /// <see cref="ApproachClearance"/> uses at the other end of the leg.
    ///
    /// <para>The launch profile thrusts at the destination from the moment it engages, and a grid
    /// parked against a station is routinely on the wrong side of it. That is a departure straight
    /// through the hull, and the grid is under native control at the time, so SE's own avoidance is
    /// switched off and nothing catches it.</para>
    ///
    /// <para>Candidates form a <b>deterministic cone</b> about the bearing to the target: that
    /// bearing first, then rings at increasing deflection, each ring probed at evenly spaced
    /// azimuths, ordered by deflection. No randomness — a re-probe against unchanged geometry
    /// returns the identical bearing, so a launch does not wander between ticks.</para></summary>
    public static class LaunchClearance
    {
        /// <summary>Azimuths per deflection ring.</summary>
        private const int RingPositions = 8;

        /// <summary>Deflection rings, radians. Stops at a right angle deliberately: a departure
        /// that has to go backwards is better served by the caller giving up and letting the probe
        /// report <see cref="LaunchClearanceResult.Blocked"/> than by flying away from the leg.</summary>
        private static readonly double[] RingsRad =
        {
            15.0 * Math.PI / 180.0,
            30.0 * Math.PI / 180.0,
            45.0 * Math.PI / 180.0,
            60.0 * Math.PI / 180.0,
            90.0 * Math.PI / 180.0,
        };

        /// <summary>Hard cap on probe calls per resolution, the target bearing included. Each probe
        /// is a host physics sweep on the game thread.</summary>
        private const int MaxProbes = 41;   // 1 + 5 rings x 8 azimuths

        /// <summary>Resolves the departure bearing for a grid at <paramref name="originM"/> leaving
        /// toward <paramref name="targetM"/>. <paramref name="isClear"/> answers whether a corridor
        /// of the given radius, running <paramref name="clearanceM"/> along the given unit bearing
        /// from the origin, is free of obstacles; it is called at most <see cref="MaxProbes"/>
        /// times.</summary>
        public static LaunchClearanceResult Resolve(Vec3 originM, Vec3 targetM, double clearanceM,
            double probeRadiusM, Func<Vec3, double, double, bool> isClear)
        {
            Vec3 toTarget = Sub(targetM, originM);
            Vec3 bearing = Direction(toTarget);

            var result = new LaunchClearanceResult { DirWorld = bearing };
            if (clearanceM <= 0) return result;
            if (isClear(bearing, clearanceM, probeRadiusM)) return result;

            Vec3 u, v;
            PerpBasis(bearing, out u, out v);

            var candidates = new List<Candidate>();
            for (int r = 0; r < RingsRad.Length; r++)
            {
                double theta = RingsRad[r];
                double cosT = Math.Cos(theta), sinT = Math.Sin(theta);
                for (int j = 0; j < RingPositions; j++)
                {
                    double a = j * (Math.PI * 2.0 / RingPositions);
                    // Cone about `bearing`: the axial component shrinks as deflection grows, and the
                    // lateral component is the ring's own radius on the perpendicular basis.
                    Vec3 dir = Add(Scale(bearing, cosT),
                                   Scale(Add(Scale(u, Math.Cos(a)), Scale(v, Math.Sin(a))), sinT));
                    candidates.Add(new Candidate(Direction(dir), theta, candidates.Count));
                }
            }

            int probesLeft = MaxProbes - 1;   // the target bearing spent one
            for (int i = 0; i < candidates.Count && probesLeft > 0; i++, probesLeft--)
            {
                if (!isClear(candidates[i].Dir, clearanceM, probeRadiusM)) continue;
                result.DirWorld = candidates[i].Dir;
                result.Adjusted = true;
                result.DeflectionRad = candidates[i].DeflectionRad;
                return result;
            }

            result.Blocked = true;   // status quo: depart along the bearing to the target, as before
            return result;
        }

        private struct Candidate
        {
            public readonly Vec3 Dir;
            public readonly double DeflectionRad;
            public readonly int Order;
            public Candidate(Vec3 dir, double deflectionRad, int order)
            {
                Dir = dir; DeflectionRad = deflectionRad; Order = order;
            }
        }

        // ---- local vector helpers (Vec3 carries no operators) --------------------------------

        private static Vec3 Sub(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }

        private static Vec3 Add(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }

        private static Vec3 Scale(Vec3 v, double k) { return new Vec3(v.X * k, v.Y * k, v.Z * k); }

        private static double Length(Vec3 v) { return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z); }

        private static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        /// <summary>Unit vector along <paramref name="v"/>; +X when degenerate (a launch whose
        /// target coincides with its origin still needs some axis to leave along).</summary>
        private static Vec3 Direction(Vec3 v)
        {
            double len = Length(v);
            return len < 1e-6 ? new Vec3(1, 0, 0) : Scale(v, 1.0 / len);
        }

        /// <summary>Orthonormal pair perpendicular to <paramref name="d"/> (unit). The reference
        /// axis flips only as |d.X| crosses 0.9, so ring orientation is stable for any fixed
        /// bearing.</summary>
        private static void PerpBasis(Vec3 d, out Vec3 u, out Vec3 v)
        {
            Vec3 pick = Math.Abs(d.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            u = Direction(Cross(d, pick));
            v = Cross(d, u);
        }
    }
}
