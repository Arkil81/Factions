using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Inputs for one stop-point clearance resolution. <b>World metres</b>, matching
    /// <see cref="FlightSample"/>'s convention — flight control works in metres and the host
    /// converts once at the seam.</summary>
    public struct ClearanceParams
    {
        /// <summary>Preferred rest distance short of the target — the stop point the approach
        /// uses whenever it is clear.</summary>
        public double StopDistanceM;

        /// <summary>Furthest the chosen point may deviate beyond <see cref="StopDistanceM"/>.
        /// The chosen point's distance from the target never exceeds
        /// <c>StopDistanceM + MaxStandoffM</c> — callers size this against the arrival radius
        /// that resolves the leg, so a clearance offset cannot park the grid somewhere the
        /// fleet lifecycle will never count as arrived.</summary>
        public double MaxStandoffM;

        /// <summary>Radius of the clearance sphere probed at each candidate. Also the lattice
        /// step, so consecutive candidates are spaced roughly one probe apart.</summary>
        public double ProbeRadiusM;
    }

    /// <summary>Outcome of <see cref="ApproachClearance.Resolve"/>.</summary>
    public struct ClearanceResult
    {
        public Vec3 StopPointM;

        /// <summary>A clear point other than the preferred one was chosen.</summary>
        public bool Adjusted;

        /// <summary>Nothing probed clear within the envelope; <see cref="StopPointM"/> is the
        /// preferred point, i.e. the caller's behaviour without clearance — never worse.</summary>
        public bool Blocked;
    }

    /// <summary>Chooses where an approach comes to rest when the preferred stop point may be
    /// occupied — parked grids at a busy node being the motivating case. Pure geometry behind an
    /// injected probe, kept SE-free in Core so the choice is unit-testable; the host owns the
    /// physics query (the same split <see cref="FlightManoeuvre"/> and <c>IPlanetCatalogue</c> use).
    ///
    /// <para>Candidates form a <b>deterministic lattice</b> — axial standoff away from the target
    /// combined with rings perpendicular to the approach axis, probed in order of increasing
    /// deviation from the preferred point. No randomness: with unchanged occupancy a re-probe
    /// returns the identical point, which is what keeps a grid from chasing a wandering parking
    /// spot between probe cycles.</para></summary>
    public static class ApproachClearance
    {
        /// <summary>Lateral candidates per ring, evenly spaced around the approach axis.</summary>
        private const int RingPositions = 8;

        /// <summary>Hard cap on probe calls per resolution (the preferred point included). Each
        /// probe is a host physics query on the game thread; the lattice is probed best-first, so
        /// the cap trims only the least attractive candidates.</summary>
        private const int MaxProbes = 32;

        /// <summary>Resolves the stop point for an approach from <paramref name="fromM"/> toward
        /// <paramref name="targetM"/>. <paramref name="isClear"/> returns whether a sphere of the
        /// given radius centred on the point is free of obstacles; it is called at most
        /// <see cref="MaxProbes"/> times.</summary>
        public static ClearanceResult Resolve(Vec3 targetM, Vec3 fromM, ClearanceParams p,
            Func<Vec3, double, bool> isClear)
        {
            double stopDist = p.StopDistanceM > 0 ? p.StopDistanceM : 0;
            double step = p.ProbeRadiusM > 1.0 ? p.ProbeRadiusM : 1.0;
            double maxStandoff = p.MaxStandoffM > 0 ? p.MaxStandoffM : 0;
            double maxFromTarget = stopDist + maxStandoff;

            Vec3 dir = Direction(Sub(targetM, fromM));
            Vec3 preferred = Sub(targetM, Scale(dir, stopDist));

            var result = new ClearanceResult { StopPointM = preferred };
            if (isClear(preferred, p.ProbeRadiusM)) return result;

            Vec3 u, v;
            PerpBasis(dir, out u, out v);

            // Lattice: k steps of axial standoff (away from the target), m steps of lateral ring
            // radius. Envelope and deviation are computed arithmetically from (k, m) rather than
            // from the summed points, so the filter and the ordering carry no float drift.
            int maxSteps = (int)(maxStandoff / step);
            double stepSq = step * step;
            double maxFromTargetSq = maxFromTarget * maxFromTarget;

            var candidates = new List<Candidate>();
            for (int k = 0; k <= maxSteps; k++)
            {
                double axialFromTarget = stopDist + k * step;
                Vec3 axial = Sub(preferred, Scale(dir, k * step));
                for (int m = 0; m <= maxSteps; m++)
                {
                    if (k == 0 && m == 0) continue;   // the preferred point, probed above

                    double lateral = m * step;
                    if (axialFromTarget * axialFromTarget + lateral * lateral > maxFromTargetSq)
                        break;   // lateral only grows within this k; nothing further fits

                    double devSq = (k * (double)k + m * (double)m) * stepSq;
                    if (m == 0)
                    {
                        candidates.Add(new Candidate(axial, devSq, candidates.Count));
                        continue;
                    }
                    for (int j = 0; j < RingPositions; j++)
                    {
                        double a = j * (Math.PI * 2.0 / RingPositions);
                        Vec3 point = Add(axial, Add(Scale(u, lateral * Math.Cos(a)),
                                                    Scale(v, lateral * Math.Sin(a))));
                        candidates.Add(new Candidate(point, devSq, candidates.Count));
                    }
                }
            }

            // Best-first, ties broken by insertion order — List.Sort is unstable on its own.
            candidates.Sort(CompareCandidates);

            int probesLeft = MaxProbes - 1;   // the preferred point spent one
            for (int i = 0; i < candidates.Count && probesLeft > 0; i++, probesLeft--)
            {
                if (!isClear(candidates[i].Point, p.ProbeRadiusM)) continue;
                result.StopPointM = candidates[i].Point;
                result.Adjusted = true;
                return result;
            }

            result.Blocked = true;   // status quo: fly to the preferred point, as before
            return result;
        }

        private struct Candidate
        {
            public readonly Vec3 Point;
            public readonly double DevSq;
            public readonly int Order;
            public Candidate(Vec3 point, double devSq, int order)
            {
                Point = point; DevSq = devSq; Order = order;
            }
        }

        private static int CompareCandidates(Candidate a, Candidate b)
        {
            return a.DevSq != b.DevSq ? a.DevSq.CompareTo(b.DevSq) : a.Order.CompareTo(b.Order);
        }

        /// <summary>Unit vector along <paramref name="v"/>; +X when degenerate (approach from the
        /// target's own position still needs some axis to stand off along).</summary>
        private static Vec3 Direction(Vec3 v)
        {
            double len = Length(v);
            return len < 1e-6 ? new Vec3(1, 0, 0) : Scale(v, 1.0 / len);
        }

        /// <summary>Orthonormal pair perpendicular to <paramref name="d"/> (unit). The reference
        /// axis flips only as |d.X| crosses 0.9, so the ring orientation is stable for any fixed
        /// approach direction.</summary>
        private static void PerpBasis(Vec3 d, out Vec3 u, out Vec3 v)
        {
            Vec3 pick = Math.Abs(d.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            u = Direction(Cross(d, pick));
            v = Cross(d, u);
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
    }
}
