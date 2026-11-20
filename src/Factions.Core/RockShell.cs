using System;

namespace Factions.Core
{
    /// <summary>The proportions of a <see cref="RockShell"/>, read from tuning.</summary>
    public struct RockShellShape
    {
        /// <summary>Radius at rest, and the lateral semi-axis when moving.</summary>
        public double SideKm;
        /// <summary>Semi-axis behind a body moving at or above <see cref="MinSpeedMs"/>.</summary>
        public double BackKm;
        /// <summary>Seconds of travel added to <see cref="SideKm"/> ahead of a body moving at or above
        /// <see cref="MinSpeedMs"/>.</summary>
        public double LeadSeconds;
        /// <summary>Below this speed the shell is a sphere of <see cref="SideKm"/>.</summary>
        public double MinSpeedMs;

        public static RockShellShape From(FactionTuning t)
        {
            return new RockShellShape
            {
                SideKm = Math.Max(0, t.FeatureRockShellSideKm),
                BackKm = Math.Max(0, t.FeatureRockShellBackKm),
                LeadSeconds = Math.Max(0, t.FeatureRockShellLeadSeconds),
                MinSpeedMs = Math.Max(0, t.FeatureRockShellMinSpeedMs),
            };
        }
    }

    /// <summary>A body the server keeps rock around: a position and a velocity.</summary>
    public struct RockShellAnchor
    {
        public Vec3 PosKm;
        public Vec3 VelMs;

        public RockShellAnchor(Vec3 posKm, Vec3 velMs) { PosKm = posKm; VelMs = velMs; }
    }

    /// <summary>
    /// The region around a body inside which the server keeps pristine feature rock spawned: a sphere at rest, and above
    /// a speed an egg on the velocity -- two half-ellipsoids sharing the lateral semi-axis, the forward one stretched by
    /// the distance covered in a lead time. Everything that queries voxels on the server reaches within ~3 km of the
    /// body that asks (autopilot avoidance, turret and AI targeting, the solar-shadow ray); linked clients draw the rest
    /// of their view from the plan, so rock outside every shell need not exist server-side.
    /// </summary>
    public static class RockShell
    {
        /// <summary>True when a sphere of <paramref name="radiusKm"/> at <paramref name="pointKm"/> may touch the shell
        /// of <paramref name="anchor"/> with every semi-axis multiplied by <paramref name="scale"/>. Conservative: the
        /// semi-axes are inflated by the radius, which contains the shell's Minkowski sum with the sphere.</summary>
        public static bool Touches(RockShellAnchor anchor, RockShellShape shape, Vec3 pointKm, double radiusKm, double scale)
        {
            double dx = pointKm.X - anchor.PosKm.X, dy = pointKm.Y - anchor.PosKm.Y, dz = pointKm.Z - anchor.PosKm.Z;
            double d2 = dx * dx + dy * dy + dz * dz;
            double r = Math.Max(0, radiusKm);
            double side = shape.SideKm * scale;
            double speed = Speed(anchor.VelMs);
            if (speed <= 0 || speed < shape.MinSpeedMs)
            {
                double reach = side + r;
                return d2 <= reach * reach;
            }

            double ux = anchor.VelMs.X / speed, uy = anchor.VelMs.Y / speed, uz = anchor.VelMs.Z / speed;
            double x = dx * ux + dy * uy + dz * uz;
            double perp2 = Math.Max(0, d2 - x * x);
            double axis = (x >= 0 ? shape.SideKm + shape.LeadSeconds * speed / 1000.0 : shape.BackKm) * scale + r;
            double lateral = side + r;
            if (axis <= 0 || lateral <= 0) return false;
            return x * x / (axis * axis) + perp2 / (lateral * lateral) <= 1.0;
        }

        /// <summary>Radius of a sphere centred on the anchor that contains the whole shell at <paramref name="scale"/>.</summary>
        public static double BoundingRadiusKm(RockShellAnchor anchor, RockShellShape shape, double scale)
        {
            double speed = Speed(anchor.VelMs);
            if (speed <= 0 || speed < shape.MinSpeedMs) return shape.SideKm * scale;
            double ahead = shape.SideKm + shape.LeadSeconds * speed / 1000.0;
            return Math.Max(ahead, Math.Max(shape.SideKm, shape.BackKm)) * scale;
        }

        internal static double Speed(Vec3 v)
        {
            return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        }
    }

    /// <summary>A <see cref="RockShell"/> of one anchor, worked out once so that testing many points against it costs a
    /// few multiplies each. Same answers as <see cref="RockShell.Touches"/>.</summary>
    public struct RockShellProbe
    {
        private readonly Vec3 _pos;
        private readonly double _ux, _uy, _uz, _ahead, _back, _side;
        private readonly bool _sphere;

        public RockShellProbe(RockShellAnchor anchor, RockShellShape shape)
        {
            _pos = anchor.PosKm;
            _side = shape.SideKm;
            double speed = RockShell.Speed(anchor.VelMs);
            _sphere = speed <= 0 || speed < shape.MinSpeedMs;
            if (_sphere)
            {
                _ux = _uy = _uz = 0;
                _ahead = _back = _side;
            }
            else
            {
                _ux = anchor.VelMs.X / speed; _uy = anchor.VelMs.Y / speed; _uz = anchor.VelMs.Z / speed;
                _ahead = shape.SideKm + shape.LeadSeconds * speed / 1000.0;
                _back = shape.BackKm;
            }
        }

        public Vec3 PosKm { get { return _pos; } }

        public bool Touches(Vec3 pointKm, double radiusKm, double scale)
        {
            double dx = pointKm.X - _pos.X, dy = pointKm.Y - _pos.Y, dz = pointKm.Z - _pos.Z;
            double d2 = dx * dx + dy * dy + dz * dz;
            double r = radiusKm > 0 ? radiusKm : 0;
            double lateral = _side * scale + r;
            if (_sphere) return d2 <= lateral * lateral;
            double x = dx * _ux + dy * _uy + dz * _uz;
            double perp2 = d2 - x * x;
            if (perp2 < 0) perp2 = 0;
            double axis = (x >= 0 ? _ahead : _back) * scale + r;
            if (axis <= 0 || lateral <= 0) return false;
            return x * x / (axis * axis) + perp2 / (lateral * lateral) <= 1.0;
        }
    }
}
