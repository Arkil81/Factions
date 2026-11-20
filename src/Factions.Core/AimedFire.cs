using System;

namespace Factions.Core
{
    /// <summary>Where a round has to be sent to meet a moving target, and whether a gun pointing a
    /// given way should be firing. Pure geometry: the host reads the grids, the ammo and the gun axes,
    /// and turns the answer into a facing and a trigger. See docs/fleet-events.md, *Aimed fire*.</summary>
    public struct AimSolution
    {
        /// <summary>False when no intercept exists: the target outruns the round along the line.</summary>
        public bool Valid;

        /// <summary>Where the target will be when the round arrives, world metres. A gun pointing
        /// from the shooter at this point (less the inherited drift, see <see cref="AimDir"/>) hits.</summary>
        public Vec3 InterceptM;

        /// <summary>The direction the round's own muzzle velocity has to point, unit, world axes. The
        /// same as the bearing to <see cref="InterceptM"/> when the round inherits nothing or the
        /// shooter is at rest.</summary>
        public Vec3 AimDir;

        /// <summary>Time of flight, seconds.</summary>
        public double TimeSec;
    }

    /// <summary>Why a gun is or is not firing this tick. Logged on a firing transition.</summary>
    public enum FireGate
    {
        Fire = 0,
        NotHostile = 1,
        NoSolution = 2,
        OutOfRange = 3,
        OffAxis = 4,
    }

    public static class AimedFire
    {
        /// <summary>A hull aims its nose and fires its fixed guns only when the fixed guns that point
        /// along its nose carry at least this share of its weapon DPS. Below it the hull is a turret
        /// boat, and pointing the nose would only spoil the manoeuvre for guns that aim themselves.</summary>
        public const double MinAimableFixedShare = 0.5;

        /// <summary>A fixed gun counts as pointing along the nose within 10 degrees of it. One that
        /// points elsewhere (a broadside mount, a rear gun) cannot be brought to bear by aiming the
        /// nose and is left alone.</summary>
        public static readonly double AlignedGunCos = Math.Cos(10.0 * Math.PI / 180.0);

        /// <summary>The aim cone is the angle the target's body subtends from the gun, at this share
        /// of its bounding radius, so a hit lands inside the hull rather than grazing the sphere
        /// round it — floored and capped below.</summary>
        public const double TargetRadiusShare = 0.7;

        public static readonly double MinConeRad = 0.5 * Math.PI / 180.0;
        public static readonly double MaxConeRad = 6.0 * Math.PI / 180.0;

        /// <summary>Once firing, a gun keeps firing until its error passes this multiple of the cone,
        /// so an error hovering on the edge does not toggle the trigger every tick.</summary>
        public const double ConeReleaseFactor = 1.5;

        /// <summary>How far above or below the horizon an aiming hull may pitch its nose in gravity.
        /// The flight model otherwise holds the nose level; past this the lift thrusters would be
        /// asked to carry the hull at an attitude the lift gate never checked.</summary>
        public static readonly double MaxAimPitchRad = 35.0 * Math.PI / 180.0;

        /// <summary>The intercept of a round leaving <paramref name="shooterPosM"/> at
        /// <paramref name="roundSpeedMs"/> (its own muzzle speed) with a target at
        /// <paramref name="targetPosM"/> moving at <paramref name="targetVelMs"/>. With
        /// <paramref name="inheritsShooterVelocity"/> the round also carries the shooter's velocity —
        /// true of every projectile round in SE, and of missile rounds while the inheritance patch is
        /// bound. The earliest non-negative time is taken.</summary>
        public static AimSolution Intercept(Vec3 shooterPosM, Vec3 shooterVelMs, Vec3 targetPosM, Vec3 targetVelMs,
                                            double roundSpeedMs, bool inheritsShooterVelocity)
        {
            var none = new AimSolution { Valid = false };
            if (!(roundSpeedMs > 0)) return none;

            Vec3 r = Sub(targetPosM, shooterPosM);
            Vec3 v = inheritsShooterVelocity ? Sub(targetVelMs, shooterVelMs) : targetVelMs;

            // |r + v·t| = s·t  →  (v·v − s²)t² + 2(r·v)t + r·r = 0
            double a = Dot(v, v) - roundSpeedMs * roundSpeedMs;
            double b = 2.0 * Dot(r, v);
            double c = Dot(r, r);
            if (c < 1e-12) return none;

            double t;
            if (Math.Abs(a) < 1e-9)
            {
                if (b >= 0) return none;
                t = -c / b;
            }
            else
            {
                double disc = b * b - 4.0 * a * c;
                if (disc < 0) return none;
                double sq = Math.Sqrt(disc);
                double t1 = (-b - sq) / (2.0 * a);
                double t2 = (-b + sq) / (2.0 * a);
                double lo = Math.Min(t1, t2), hi = Math.Max(t1, t2);
                t = lo > 0 ? lo : hi;
                if (!(t > 0)) return none;
            }

            // The round's own velocity runs along r + v·t; the world point it meets the target at is
            // where the target is at t.
            Vec3 lead = Add(r, Scale(v, t));
            double len = Length(lead);
            if (len < 1e-9) return none;
            return new AimSolution
            {
                Valid = true,
                AimDir = Scale(lead, 1.0 / len),
                InterceptM = Add(targetPosM, Scale(targetVelMs, t)),
                TimeSec = t,
            };
        }

        /// <summary>A round's mean speed over <paramref name="rangeM"/>: it leaves at
        /// <paramref name="launchMs"/> (capped at <paramref name="desiredMs"/>, as SE caps it), gains
        /// <paramref name="accelMs2"/> up to <paramref name="desiredMs"/>, and holds it. A projectile,
        /// or a missile with no acceleration, flies at its launch speed throughout.</summary>
        public static double EffectiveSpeedMs(double launchMs, double desiredMs, double accelMs2, double rangeM)
        {
            double v0 = desiredMs > 0 && launchMs > desiredMs ? desiredMs : launchMs;
            if (!(v0 > 0) && !(accelMs2 > 0)) return 0;
            if (!(accelMs2 > 0) || !(desiredMs > v0) || !(rangeM > 0)) return v0;

            double tUp = (desiredMs - v0) / accelMs2;
            double dUp = 0.5 * (v0 + desiredMs) * tUp;
            double t = rangeM <= dUp
                ? (-v0 + Math.Sqrt(v0 * v0 + 2.0 * accelMs2 * rangeM)) / accelMs2
                : tUp + (rangeM - dUp) / desiredMs;
            return t > 0 ? rangeM / t : v0;
        }

        /// <summary>The half-angle inside which a gun at <paramref name="distanceM"/> fires at a
        /// target of bounding radius <paramref name="targetRadiusM"/>.</summary>
        public static double ConeRad(double distanceM, double targetRadiusM)
        {
            if (!(distanceM > 0)) return MaxConeRad;
            double cone = Math.Atan2(Math.Max(0, targetRadiusM) * TargetRadiusShare, distanceM);
            if (cone < MinConeRad) return MinConeRad;
            if (cone > MaxConeRad) return MaxConeRad;
            return cone;
        }

        /// <summary>The angle between two directions, radians; neither need be unit. Zero-length
        /// input is π — never "on target".</summary>
        public static double AngleRad(Vec3 a, Vec3 b)
        {
            double la = Length(a), lb = Length(b);
            if (la < 1e-12 || lb < 1e-12) return Math.PI;
            double cos = Dot(a, b) / (la * lb);
            if (cos > 1) cos = 1;
            if (cos < -1) cos = -1;
            return Math.Acos(cos);
        }

        /// <summary>Whether a gun fires this tick. It needs a hostile target, an intercept, the
        /// meeting point inside the gun's reach, and its axis inside the cone — the release cone,
        /// <see cref="ConeReleaseFactor"/> wider, while it is already firing.</summary>
        /// <param name="errRad">Angle between the gun's axis and <see cref="AimSolution.AimDir"/>.</param>
        /// <param name="interceptRangeM">Distance from the gun to <see cref="AimSolution.InterceptM"/>.</param>
        public static FireGate Gate(bool hostile, bool wasFiring, AimSolution aim, double errRad, double coneRad,
                                    double interceptRangeM, double weaponRangeM)
        {
            if (!hostile) return FireGate.NotHostile;
            if (!aim.Valid) return FireGate.NoSolution;
            if (!(weaponRangeM > 0) || interceptRangeM > weaponRangeM) return FireGate.OutOfRange;
            double limit = wasFiring ? coneRad * ConeReleaseFactor : coneRad;
            return errRad <= limit ? FireGate.Fire : FireGate.OffAxis;
        }

        /// <summary>Whether a hull aims at all: its nose-aligned fixed guns carry at least
        /// <see cref="MinAimableFixedShare"/> of its weapon DPS.</summary>
        public static bool AimsNose(double aimableFixedDps, double totalDps)
        {
            return aimableFixedDps > 0 && totalDps > 0 && aimableFixedDps >= MinAimableFixedShare * totalDps;
        }

        /// <summary>The nose direction that brings a gun whose axis is <paramref name="gunAxis"/> onto
        /// <paramref name="aimDir"/> while the nose is <paramref name="nose"/>: the nose turned by the
        /// same shortest-arc rotation that takes the gun axis onto the aim. For a gun along the nose
        /// this is <paramref name="aimDir"/> itself.</summary>
        public static Vec3 NoseFor(Vec3 aimDir, Vec3 gunAxis, Vec3 nose)
        {
            Vec3 d = Unit(aimDir), g = Unit(gunAxis), f = Unit(nose);
            Vec3 axis = Cross(g, d);
            double sin = Length(axis);
            double cos = Dot(g, d);
            if (sin < 1e-9) return cos >= 0 ? f : Scale(f, -1.0);
            Vec3 k = Scale(axis, 1.0 / sin);
            // Rodrigues: f' = f cosθ + (k×f) sinθ + k (k·f)(1 − cosθ)
            return Unit(Add(Add(Scale(f, cos), Scale(Cross(k, f), sin)), Scale(k, Dot(k, f) * (1.0 - cos))));
        }

        internal static Vec3 Add(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        internal static Vec3 Sub(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        internal static Vec3 Scale(Vec3 v, double k) { return new Vec3(v.X * k, v.Y * k, v.Z * k); }
        internal static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        internal static double Length(Vec3 v) { return Math.Sqrt(Dot(v, v)); }
        internal static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
        internal static Vec3 Unit(Vec3 v)
        {
            double len = Length(v);
            return len > 1e-12 ? Scale(v, 1.0 / len) : new Vec3(0, 0, 0);
        }
    }
}
