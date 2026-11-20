using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The bodies a sightline has to get past. A sensor sees a target only when no planet stands
    /// between the two.
    ///
    /// <para>Built once and reused: planets do not move, so the body list is a snapshot rather than a
    /// live query. <see cref="None"/> is an empty field where every line is clear, which is the
    /// pre-occlusion behaviour exactly — a caller with no planet catalogue passes it and detection
    /// answers as it always did.</para>
    ///
    /// <para><b>The occluding radius is the body's terrain, not its atmosphere or its gravity
    /// well</b> — <see cref="PlanetRecord.SurfaceRadius"/>, unless the recipe authored
    /// <see cref="PlanetRecord.SensorOcclusionRadiusKm"/> for that body. The authored value is what a
    /// virtual body needs: Real Gas Giants reports a 9.5 km voxel core for a giant players fly around
    /// at 180 km, so SE's own radius would leave the largest object in the system transparent.</para>
    /// </summary>
    public sealed class SensorOcclusion
    {
        /// <summary>No bodies: every line is clear.</summary>
        public static readonly SensorOcclusion None = new SensorOcclusion(null, 0);

        private struct Body
        {
            public Vec3 Center;
            public double RadiusKm;
        }

        private readonly Body[] _bodies;
        private readonly double _liftKm;

        /// <summary>Slack on the radius test, km². Absorbs the floating-point error of an endpoint
        /// that sits exactly on the shell by construction.</summary>
        private const double RadiusEpsilonKmSq = 1e-6;

        public SensorOcclusion(IEnumerable<PlanetRecord> bodies, double liftKm)
        {
            _liftKm = liftKm > 0 ? liftKm : 0;
            var kept = new List<Body>();
            if (bodies != null)
            {
                foreach (PlanetRecord body in bodies)
                {
                    if (body == null) continue;
                    double radius = RadiusKmOf(body);
                    if (radius <= 0) continue;
                    kept.Add(new Body { Center = body.Center, RadiusKm = radius });
                }
            }
            _bodies = kept.ToArray();
        }

        /// <summary>The field this catalogue and tuning describe. A null catalogue or one reporting no
        /// bodies yields <see cref="None"/>, so a host without planet awareness is not a special case
        /// at any call site.</summary>
        public static SensorOcclusion From(IPlanetCatalogue planets, FactionTuning tuning)
        {
            if (planets == null) return None;
            var built = new SensorOcclusion(planets.Planets, tuning != null ? tuning.SensorHorizonLiftKm : 0);
            return built.Count == 0 ? None : built;
        }

        /// <summary>What this body occludes at: its authored radius, else its terrain.</summary>
        public static double RadiusKmOf(PlanetRecord body)
        {
            if (body == null) return 0;
            if (body.SensorOcclusionRadiusKm.HasValue) return body.SensorOcclusionRadiusKm.Value;
            return body.SurfaceRadius;
        }

        /// <summary>How many bodies can block a line.</summary>
        public int Count { get { return _bodies.Length; } }

        /// <summary>Lines this field has blocked, cumulative over the life of the object and across
        /// every channel that asks it. The only reader of it is the <c>sensing</c> trace, which
        /// reports the delta over a sweep pass: a pass that blocks nothing while
        /// <see cref="Count"/> is non-zero is the shape of a mechanism that is not running.</summary>
        public long BlockedLines { get { return _blockedLines; } }

        private long _blockedLines;

        /// <summary>How far each endpoint is raised off the body it stands on before the line is
        /// tested — <see cref="FactionTuning.SensorHorizonLiftKm"/>.</summary>
        public double LiftKm { get { return _liftKm; } }

        /// <summary>The deepest knife-edge diffraction parameter ν any body puts on the line between two
        /// points, for a radio at <paramref name="wavelengthM"/> (FAC-419,
        /// <see cref="RadioPath.SphereNu"/>). Negative infinity when nothing stands between them. Uses
        /// each body's own radius, not the lifted one <see cref="IsClear"/> allows sensors: a radio
        /// wave grazing the ground is diffracted whether or not a sensor would count it seen.</summary>
        public double WorstDiffractionNu(Vec3 fromKm, Vec3 toKm, double wavelengthM)
        {
            double worst = double.NegativeInfinity;
            for (int i = 0; i < _bodies.Length; i++)
            {
                double nu = RadioPath.SphereNu(fromKm, toKm, _bodies[i].Center, _bodies[i].RadiusKm, wavelengthM);
                if (nu > worst) worst = nu;
            }
            return worst;
        }

        /// <summary>Whether nothing stands between these two points.
        ///
        /// <para><b>Both endpoints are lifted, not just the sensor.</b> Two points exactly on a sphere
        /// have no sightline at all, so a raw test makes every surface node blind to everything else
        /// on its own body. Lifting both is the radio-horizon pair: two masts of height <c>h</c> on a
        /// body of radius <c>R</c> see each other out to <c>2·sqrt(2Rh)</c>.</para>
        ///
        /// <para>An endpoint that is <i>inside</i> a body after the lift relaxes that body's
        /// requirement to its own radius, so a sensor under a shell is not blinded by the shell it
        /// sits within — the same rule <see cref="PlanetRoutePlanner.SegmentClearOfShell"/> applies to
        /// a launch leg that would otherwise flag itself.</para></summary>
        public bool IsClear(Vec3 fromKm, Vec3 toKm)
        {
            for (int i = 0; i < _bodies.Length; i++)
            {
                Body body = _bodies[i];
                double radius = body.RadiusKm;

                // Cheap reject, and the common case: the raw segment already clears the body. Lifting
                // an endpoint radially outward never moves the segment closer to the centre, so a raw
                // line that clears the radius cannot be blocked once lifted.
                double t;
                double rawClosestSq = SegmentDistanceSq(fromKm, toKm, body.Center, out t);
                if (rawClosestSq >= radius * radius) continue;

                double distFrom = body.Center.DistanceTo(fromKm);
                double distTo = body.Center.DistanceTo(toKm);
                Vec3 liftedFrom = Lift(fromKm, body.Center, distFrom);
                Vec3 liftedTo = Lift(toKm, body.Center, distTo);

                double allowed = radius;
                double liftedFromRadius = distFrom + _liftKm;
                double liftedToRadius = distTo + _liftKm;
                if (liftedFromRadius < allowed) allowed = liftedFromRadius;
                if (liftedToRadius < allowed) allowed = liftedToRadius;
                if (allowed <= 0) continue;

                double closestSq = SegmentDistanceSq(liftedFrom, liftedTo, body.Center, out t);
                if (closestSq < allowed * allowed - RadiusEpsilonKmSq)
                {
                    _blockedLines++;
                    return false;
                }
            }
            return true;
        }

        /// <summary>Whether a sensor sweeping <paramref name="fromKm"/> to <paramref name="toKm"/> has
        /// a clear line to the target from the point on that leg where it comes closest.
        ///
        /// <para>The closest point is where the detection happens — it is the one the range test
        /// itself answered on — so testing that single line keeps the swept sweep and the point sweep
        /// answering the same question. A leg that passes behind a body and out the far side is
        /// judged on its nearest approach, not on its whole length.</para></summary>
        public bool IsClearFromSegment(Vec3 fromKm, Vec3 toKm, Vec3 targetKm)
        {
            double t;
            SegmentDistanceSq(fromKm, toKm, targetKm, out t);
            return IsClear(Lerp(fromKm, toKm, t), targetKm);
        }

        private Vec3 Lift(Vec3 point, Vec3 center, double distance)
        {
            if (_liftKm <= 0 || distance <= 1e-9) return point;
            double scale = (distance + _liftKm) / distance;
            return new Vec3(
                center.X + (point.X - center.X) * scale,
                center.Y + (point.Y - center.Y) * scale,
                center.Z + (point.Z - center.Z) * scale);
        }

        private static Vec3 Lerp(Vec3 a, Vec3 b, double t)
        {
            return new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        }

        private static double SegmentDistanceSq(Vec3 a, Vec3 b, Vec3 p, out double t)
        {
            double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
            double lengthSq = abx * abx + aby * aby + abz * abz;
            t = 0;
            if (lengthSq > 1e-9)
            {
                t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby + (p.Z - a.Z) * abz) / lengthSq;
                if (t < 0) t = 0; else if (t > 1) t = 1;
            }
            double dx = p.X - (a.X + abx * t);
            double dy = p.Y - (a.Y + aby * t);
            double dz = p.Z - (a.Z + abz * t);
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
