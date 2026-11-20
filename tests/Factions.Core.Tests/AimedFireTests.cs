using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers <see cref="AimedFire"/>: the lead-predicted intercept a fixed-gun hull faces,
    /// the aim cone and the fire gate — and <see cref="FlightAttitude.ResolveAimed"/>, the pitch an
    /// aiming hull may take in gravity.</summary>
    public class AimedFireTests
    {
        private static readonly Vec3 Zero = new Vec3(0, 0, 0);
        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static double Len(Vec3 v) => Math.Sqrt(Dot(v, v));
        private static Vec3 Add(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        private static Vec3 Scale(Vec3 v, double k) => new Vec3(v.X * k, v.Y * k, v.Z * k);
        private static double Deg(double rad) => rad * 180.0 / Math.PI;

        private static void Near(Vec3 expected, Vec3 actual, int digits = 6)
        {
            Assert.Equal(expected.X, actual.X, digits);
            Assert.Equal(expected.Y, actual.Y, digits);
            Assert.Equal(expected.Z, actual.Z, digits);
        }

        /// <summary>Where the round actually is at the solution's time: shooter plus its own velocity
        /// (and the shooter's, when inherited) times t. A correct solution puts it on the target.</summary>
        private static Vec3 RoundAt(Vec3 shooter, Vec3 shooterVel, AimSolution s, double speed, bool inherits)
        {
            Vec3 v = Scale(s.AimDir, speed);
            if (inherits) v = Add(v, shooterVel);
            return Add(shooter, Scale(v, s.TimeSec));
        }

        [Fact]
        public void StationaryTarget_AimsStraightAtIt()
        {
            AimSolution s = AimedFire.Intercept(Zero, Zero, new Vec3(0, 0, -1000), Zero, 500, true);
            Assert.True(s.Valid);
            Near(new Vec3(0, 0, -1), s.AimDir);
            Near(new Vec3(0, 0, -1000), s.InterceptM);
            Assert.Equal(2.0, s.TimeSec, 9);
        }

        /// <summary>A target crossing the line of fire is led: the aim swings ahead of it, the round
        /// and the target are at the same point at the solution's time, and the lead angle is the
        /// textbook asin(v/s) for a target crossing at right angles.</summary>
        [Fact]
        public void CrossingTarget_IsLed_AndTheRoundMeetsIt()
        {
            var shooter = Zero;
            var target = new Vec3(0, 0, -1000);
            var targetVel = new Vec3(100, 0, 0);
            AimSolution s = AimedFire.Intercept(shooter, Zero, target, targetVel, 500, true);
            Assert.True(s.Valid);
            Assert.True(s.AimDir.X > 0, "aim leads towards the target's motion");
            Near(Add(target, Scale(targetVel, s.TimeSec)), RoundAt(shooter, Zero, s, 500, true), 6);
            Assert.Equal(Math.Asin(100.0 / 500.0), AimedFire.AngleRad(s.AimDir, new Vec3(0, 0, -1)), 9);
        }

        /// <summary>The inheritance patch decides the aim: a round that carries the shooter's
        /// sideways drift must be pointed against it, one that does not is pointed straight.</summary>
        [Fact]
        public void MovingShooter_InheritanceOn_AimsAgainstTheDrift_OffAimsStraight()
        {
            var shooterVel = new Vec3(100, 0, 0);
            var target = new Vec3(0, 0, -1000);

            AimSolution inherits = AimedFire.Intercept(Zero, shooterVel, target, Zero, 500, true);
            Assert.True(inherits.Valid);
            Assert.True(inherits.AimDir.X < 0, "an inheriting round is aimed against the shooter's drift");
            Near(target, RoundAt(Zero, shooterVel, inherits, 500, true), 6);

            AimSolution plain = AimedFire.Intercept(Zero, shooterVel, target, Zero, 500, false);
            Assert.True(plain.Valid);
            Near(new Vec3(0, 0, -1), plain.AimDir);
            Near(target, RoundAt(Zero, shooterVel, plain, 500, false), 6);
        }

        /// <summary>Shooter and target flying in formation: with inheritance the relative geometry is
        /// static, so the aim is straight at the target whatever the common velocity.</summary>
        [Fact]
        public void CommonVelocity_WithInheritance_IsInvisibleToTheAim()
        {
            var vel = new Vec3(300, 50, 0);
            AimSolution s = AimedFire.Intercept(Zero, vel, new Vec3(0, 0, -1500), vel, 500, true);
            Assert.True(s.Valid);
            Near(new Vec3(0, 0, -1), s.AimDir);
            Assert.Equal(3.0, s.TimeSec, 9);
        }

        [Fact]
        public void TargetOutrunningTheRound_HasNoSolution()
        {
            AimSolution s = AimedFire.Intercept(Zero, Zero, new Vec3(0, 0, -1000), new Vec3(0, 0, -600), 500, true);
            Assert.False(s.Valid);
            Assert.False(AimedFire.Intercept(Zero, Zero, new Vec3(0, 0, -1000), Zero, 0, true).Valid);
        }

        /// <summary>A target closing faster than the round flies still has the earlier root.</summary>
        [Fact]
        public void TargetClosingFast_TakesTheEarliestMeeting()
        {
            AimSolution s = AimedFire.Intercept(Zero, Zero, new Vec3(0, 0, -1000), new Vec3(0, 0, 600), 500, true);
            Assert.True(s.Valid);
            Assert.Equal(1000.0 / 1100.0, s.TimeSec, 9);
        }

        [Fact]
        public void EffectiveSpeed_ProjectileIsItsLaunchSpeed_MissileAveragesItsSpinUp()
        {
            Assert.Equal(400.0, AimedFire.EffectiveSpeedMs(400, 400, 0, 800), 9);
            // SE caps the muzzle at DesiredSpeed.
            Assert.Equal(200.0, AimedFire.EffectiveSpeedMs(500, 200, 0, 800), 9);

            // Rocket: 100 → 200 at 600 m/s²: reaches 200 after 1/6 s and 25 m, then 975 m at 200.
            double t = 1.0 / 6.0 + 975.0 / 200.0;
            Assert.Equal(1000.0 / t, AimedFire.EffectiveSpeedMs(100, 200, 600, 1000), 9);
            double v = AimedFire.EffectiveSpeedMs(100, 200, 600, 1000);
            Assert.True(v > 100 && v < 200);
        }

        [Fact]
        public void Cone_IsTheTargetsAngularSize_FlooredAndCapped()
        {
            Assert.Equal(Math.Atan2(20 * AimedFire.TargetRadiusShare, 1000), AimedFire.ConeRad(1000, 20), 12);
            Assert.Equal(AimedFire.MinConeRad, AimedFire.ConeRad(3000, 5), 12);
            Assert.Equal(AimedFire.MaxConeRad, AimedFire.ConeRad(50, 100), 12);
        }

        [Fact]
        public void Gate_FiresOnlyOnAHostileInRangeInsideTheCone()
        {
            AimSolution ok = AimedFire.Intercept(Zero, Zero, new Vec3(0, 0, -1000), Zero, 500, true);
            double cone = AimedFire.ConeRad(1000, 20);

            Assert.Equal(FireGate.Fire, AimedFire.Gate(true, false, ok, cone * 0.5, cone, 1000, 1400));
            Assert.Equal(FireGate.NotHostile, AimedFire.Gate(false, false, ok, 0, cone, 1000, 1400));
            Assert.Equal(FireGate.OutOfRange, AimedFire.Gate(true, false, ok, 0, cone, 1000, 800));
            Assert.Equal(FireGate.OutOfRange, AimedFire.Gate(true, false, ok, 0, cone, 1000, 0));
            Assert.Equal(FireGate.OffAxis, AimedFire.Gate(true, false, ok, cone * 1.2, cone, 1000, 1400));
            Assert.Equal(FireGate.NoSolution, AimedFire.Gate(true, false, default(AimSolution), 0, cone, 1000, 1400));
        }

        /// <summary>A gun already firing holds the trigger out to the release cone, and one that is
        /// not needs the tight cone: an error on the edge does not chatter.</summary>
        [Fact]
        public void Gate_HasHysteresis()
        {
            AimSolution ok = AimedFire.Intercept(Zero, Zero, new Vec3(0, 0, -1000), Zero, 500, true);
            double cone = AimedFire.ConeRad(1000, 20);
            double err = cone * 1.3;
            Assert.Equal(FireGate.OffAxis, AimedFire.Gate(true, false, ok, err, cone, 1000, 1400));
            Assert.Equal(FireGate.Fire, AimedFire.Gate(true, true, ok, err, cone, 1000, 1400));
            Assert.Equal(FireGate.OffAxis, AimedFire.Gate(true, true, ok, cone * AimedFire.ConeReleaseFactor * 1.01, cone, 1000, 1400));
        }

        [Fact]
        public void AimsNose_OnlyWhenAlignedFixedGunsCarryHalfTheDps()
        {
            Assert.True(AimedFire.AimsNose(500, 1000));
            Assert.True(AimedFire.AimsNose(900, 1000));
            Assert.False(AimedFire.AimsNose(499, 1000));
            Assert.False(AimedFire.AimsNose(0, 0));
            Assert.False(AimedFire.AimsNose(0, 1000));
        }

        /// <summary>A gun offset from the nose: turning the nose by NoseFor brings the gun, not the
        /// nose, onto the aim. A gun on the nose asks for the aim itself.</summary>
        [Fact]
        public void NoseFor_BringsAnOffsetGunOntoTheAim()
        {
            var aim = new Vec3(1, 0, 0);
            Near(aim, AimedFire.NoseFor(aim, new Vec3(0, 0, -1), new Vec3(0, 0, -1)));

            double off = 5.0 * Math.PI / 180.0;
            var nose = new Vec3(0, 0, -1);
            var gun = new Vec3(Math.Sin(off), 0, -Math.Cos(off));   // 5° to the right of the nose
            Vec3 wanted = AimedFire.NoseFor(aim, gun, nose);
            // The body turns by the rotation that takes the gun onto the aim; the nose keeps its
            // 5° offset from the gun on the other side.
            Assert.Equal(off, AimedFire.AngleRad(wanted, aim), 9);
            Assert.True(wanted.Z < 0, "the nose trails the gun by 5°, the gun sits on the aim");
        }

        [Fact]
        public void ResolveAimed_PitchesTowardsTheTarget_UpToTheCap()
        {
            var down = new Vec3(0, -9.81, 0);
            // 20° up: taken in full.
            double el = 20.0 * Math.PI / 180.0;
            AttitudeTarget t = FlightAttitude.ResolveAimed(new Vec3(Math.Cos(el), Math.Sin(el), 0), down,
                                                           new Vec3(0, 0, -1), AimedFire.MaxAimPitchRad);
            Assert.True(t.HasUp);
            Near(new Vec3(Math.Cos(el), Math.Sin(el), 0), t.Forward);
            Assert.Equal(0.0, Dot(t.Forward, t.Up), 9);
            Assert.Equal(0.0, t.Up.Z, 9);   // no roll

            // Straight below: capped at the pitch limit.
            AttitudeTarget d = FlightAttitude.ResolveAimed(new Vec3(0.01, -1, 0), down, new Vec3(1, 0, 0),
                                                           AimedFire.MaxAimPitchRad);
            Assert.Equal(-AimedFire.MaxAimPitchRad, Math.Asin(d.Forward.Y), 6);
            Assert.Equal(1.0, Len(d.Forward), 9);

            // Zero-g: the plain resolver.
            AttitudeTarget z = FlightAttitude.ResolveAimed(new Vec3(0, 5, 5), Zero, new Vec3(1, 0, 0), AimedFire.MaxAimPitchRad);
            Assert.False(z.HasUp);
            Near(new Vec3(0, Math.Sqrt(0.5), Math.Sqrt(0.5)), z.Forward);
        }
    }
}
