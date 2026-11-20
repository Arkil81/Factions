using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A fighting hull's agility (<see cref="CombatAgility"/>), the moving orbit it holds a range with
    /// (<see cref="FlightSample.MinCombatSpeedMs"/>) and its evasive bursts under fire
    /// (<see cref="Evasion"/>).
    /// </summary>
    public class CombatAgilityTests
    {
        private const double Hold = 680.0;
        private const double Tol = 34.0;
        private const double Decel = 9.0;
        private const double Cruise = 100.0;
        private static readonly DateTime T0 = new DateTime(2026, 9, 27, 21, 24, 0, DateTimeKind.Utc);

        private static FlightSample OnStation(double floorMs, double velX = 0, double velZ = 0)
            => new FlightSample
            {
                PosM             = new Vec3(Hold, 0, 0),
                TargetM          = new Vec3(0, 0, 0),
                VelMs            = new Vec3(velX, 0, velZ),
                DecelMs2         = Decel,
                CruiseMs         = Cruise,
                ArriveSpeedMs    = 2.0,
                HoldRangeM       = Hold,
                RangeToleranceM  = Tol,
                MinCombatSpeedMs = floorMs,
                OrbitSense       = 1,
            };

        private static double Len(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static bool IsZero(Vec3 v) => v.X == 0 && v.Y == 0 && v.Z == 0;
        private static Vec3 Add(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        private static Vec3 Scale(Vec3 v, double k) => new Vec3(v.X * k, v.Y * k, v.Z * k);

        /// <summary>A point mass flown the way SE's AI-thrust path flies a grid: a zero command
        /// dampens the whole velocity, a direction accelerates along it and dampens what lies across
        /// it, all at <paramref name="accel"/>.</summary>
        private static void Step(ref Vec3 pos, ref Vec3 vel, Vec3 dir, double accel, double dt)
        {
            double dv = accel * dt;
            if (IsZero(dir))
            {
                double sp = Len(vel);
                vel = sp <= dv ? new Vec3(0, 0, 0) : Scale(vel, (sp - dv) / sp);
            }
            else
            {
                double along = Dot(vel, dir);
                Vec3 across = Add(vel, Scale(dir, -along));
                double ac = Len(across);
                across = ac <= dv ? new Vec3(0, 0, 0) : Scale(across, (ac - dv) / ac);
                vel = Add(across, Scale(dir, along + dv));
            }
            pos = Add(pos, Scale(vel, dt));
        }

        // ---- agility -----------------------------------------------------------------------

        [Fact]
        public void AccelMs2_IsTheStrongestAxisOverMass()
        {
            var t = new AxisThrust { ForwardN = 200000, BackwardN = 540000, LeftN = 120000, UpN = 300000 };
            Assert.Equal(9.0, CombatAgility.AccelMs2(t, 60000), 9);
            Assert.Equal(0.0, CombatAgility.AccelMs2(t, 0));
            Assert.Equal(0.0, CombatAgility.AccelMs2(new AxisThrust(), 60000));
        }

        [Fact]
        public void Floor_IsRealForAFighterAndZeroForAHeavyHull()
        {
            CombatMotion fighter = CombatAgility.Motion(9.0, false);   // ~60 t small grid
            CombatMotion capital = CombatAgility.Motion(1.2, false);
            Assert.True(fighter.FloorMs >= 15.0, "a fighter keeps a real floor: " + fighter.FloorMs);
            Assert.True(fighter.Agility > 0 && fighter.Agility < 1);
            Assert.Equal(0.0, capital.FloorMs);
            Assert.Equal(0.0, capital.Agility);
            Assert.Equal(CombatAgility.MaxFloorMs, CombatAgility.Motion(40.0, false).FloorMs, 9);
        }

        [Fact]
        public void Floor_RisesWithAgility()
        {
            double prev = -1;
            for (double a = 0; a <= 20; a += 0.5)
            {
                double f = CombatAgility.Motion(a, false).FloorMs;
                Assert.True(f >= prev);
                prev = f;
            }
        }

        [Fact]
        public void FormationHeld_BuysNoFloor()
        {
            CombatMotion held = CombatAgility.Motion(20.0, true);
            Assert.Equal(0.0, held.FloorMs);
            Assert.Equal(0.0, held.Agility);
        }

        [Fact]
        public void OrbitSense_IsFixedPerSeedAndTakesBothWays()
        {
            var seen = new HashSet<int>();
            for (long seed = 1; seed <= 32; seed++)
            {
                int sense = CombatAgility.OrbitSense(seed);
                Assert.True(sense == 1 || sense == -1);
                Assert.Equal(sense, CombatAgility.OrbitSense(seed));
                seen.Add(sense);
            }
            Assert.Equal(2, seen.Count);
        }

        // ---- the moving orbit --------------------------------------------------------------

        [Fact]
        public void OnStation_AgileHull_MovesAcrossTheLineOfSightFacingTheTarget()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, OnStation(30));
            Assert.False(IsZero(cmd.MoveDirWorld));
            Assert.True(Math.Abs(cmd.MoveDirWorld.X) < 1e-9, "tangential inside the band");
            Assert.Equal(30.0, cmd.SpeedCapMs, 6);
            Assert.Equal(0.0, cmd.FaceTargetWorld.Value.X, 9);
        }

        [Fact]
        public void OnStation_NoFloor_StillBrakesAndHolds()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, OnStation(0));
            Assert.True(IsZero(cmd.MoveDirWorld));
        }

        [Theory]
        [InlineData(ManoeuvreKind.HoldAtRange, 1)]
        [InlineData(ManoeuvreKind.HoldAtRange, -1)]
        [InlineData(ManoeuvreKind.Kite, 1)]
        public void MovingOrbit_KeepsTheBandAndNeverStops(ManoeuvreKind kind, int sense)
        {
            FlightSample s = OnStation(30);
            s.OrbitSense = sense;
            Vec3 pos = s.PosM, vel = new Vec3(0, 0, 0);
            const double dt = 0.1;
            double minSpeed = double.MaxValue, minDist = double.MaxValue, maxDist = 0;
            int zeros = 0;
            for (int i = 0; i < 1800; i++)   // three minutes
            {
                s.PosM = pos;
                s.VelMs = vel;
                FlightCommand cmd = FlightManoeuvre.Tick(kind, s);
                if (IsZero(cmd.MoveDirWorld)) zeros++;
                Step(ref pos, ref vel, cmd.MoveDirWorld, Decel, dt);
                if (i < 100) continue;   // ten seconds to get up to speed
                double d = Len(pos);
                minDist = Math.Min(minDist, d);
                maxDist = Math.Max(maxDist, d);
                minSpeed = Math.Min(minSpeed, Len(vel));
            }
            Assert.Equal(0, zeros);
            Assert.True(minSpeed >= 25.0, "speed floor held: " + minSpeed);
            Assert.True(minDist >= Hold - 2 * Tol && maxDist <= Hold + 2 * Tol,
                "range band kept: " + minDist + ".." + maxDist);
        }

        [Fact]
        public void MovingOrbit_SenseSetsTheWayRound()
        {
            FlightSample a = OnStation(30);
            FlightSample b = OnStation(30);
            b.OrbitSense = -1;
            Vec3 da = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, a).MoveDirWorld;
            Vec3 db = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, b).MoveDirWorld;
            Assert.True(Dot(da, db) < -0.99);
        }

        [Fact]
        public void MovingOrbit_FloorIsHeldUnderTheCentripetalCap()
        {
            FlightSample s = OnStation(40);
            s.DecelMs2 = 0.5;
            s.HoldRangeM = 200;
            s.PosM = new Vec3(200, 0, 0);
            double cap = FlightManoeuvre.OrbitSpeedCapMs(200, 0.5);
            Assert.Equal(cap, FlightManoeuvre.OrbitFloorMs(s), 9);
            Assert.True(FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s).SpeedCapMs <= cap + 1e-9);
        }

        [Fact]
        public void MovingOrbit_OpeningInGravity_StillClimbs()
        {
            FlightSample s = OnStation(30);
            s.PosM = new Vec3(300, 0, 0);   // well inside the band, level with a surface target
            s.UpWorld = new Vec3(0, 1, 0);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
            Assert.True(cmd.MoveDirWorld.Y >= FlightManoeuvre.MinOpeningClimb - 1e-9, "climb " + cmd.MoveDirWorld.Y);
        }

        [Fact]
        public void MovingOrbit_InGravity_CirclesLevel()
        {
            FlightSample s = OnStation(30);
            s.UpWorld = new Vec3(0, 1, 0);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
            Assert.False(IsZero(cmd.MoveDirWorld));
            Assert.True(Math.Abs(cmd.MoveDirWorld.Y) < 1e-9);
        }

        [Fact]
        public void Escort_SweepsEitherSideOfItsBearingWithoutStopping()
        {
            FlightSample s = OnStation(30);
            s.SpreadBearing = new Vec3(1, 0, 0);
            Vec3 pos = s.PosM, vel = new Vec3(0, 0, 0);
            const double dt = 0.1;
            double maxArc = 0;
            bool leftOfBearing = false, rightOfBearing = false;
            int zeros = 0;
            for (int i = 0; i < 1800; i++)
            {
                s.PosM = pos;
                s.VelMs = vel;
                FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
                if (IsZero(cmd.MoveDirWorld)) zeros++;
                Step(ref pos, ref vel, cmd.MoveDirWorld, Decel, dt);
                if (i < 100) continue;
                double d = Len(pos);
                double arc = Math.Acos(Math.Max(-1, Math.Min(1, pos.X / d))) * d;
                maxArc = Math.Max(maxArc, arc);
                if (pos.Z > 20) leftOfBearing = true;
                if (pos.Z < -20) rightOfBearing = true;
            }
            Assert.Equal(0, zeros);
            Assert.True(leftOfBearing && rightOfBearing, "sweeps across its bearing");
            Assert.True(maxArc < 4 * FlightManoeuvre.SweepTolerances * Tol, "stays about its bearing: " + maxArc);
        }

        // ---- evasive bursts ----------------------------------------------------------------

        private static readonly CombatMotion Agile = CombatAgility.Motion(9.0, false);

        [Fact]
        public void NoHit_NoBurst()
        {
            var st = new EvadeState();
            Assert.False(Evasion.Consider(ref st, T0, null, 42, Agile, false, false));
            Assert.Equal(EvadeDir.None, st.Dir);
        }

        [Fact]
        public void FreshHit_StartsABurstThatEnds()
        {
            var st = new EvadeState();
            Assert.True(Evasion.Consider(ref st, T0, T0.AddSeconds(-0.2), 42, Agile, false, false));
            Assert.NotEqual(EvadeDir.None, st.Dir);
            Assert.InRange(st.DurationSec, Evasion.MinBurstSec, Evasion.MaxBurstSec);
            Assert.Equal(Agile.FloorMs + Evasion.BurstExtraMs * Agile.Agility, st.SpeedMs, 9);
            Assert.True(Evasion.Active(st, T0.AddSeconds(st.DurationSec - 0.05)));
            Assert.False(Evasion.Active(st, T0.AddSeconds(st.DurationSec)));

            // The same hit is stale by the time the interval allows another burst.
            DateTime after = T0.AddSeconds(Evasion.MinIntervalSec + 1);
            Assert.False(Evasion.Consider(ref st, after, T0.AddSeconds(-0.2), 42, Agile, false, false));
            Assert.Equal(EvadeDir.None, st.Dir);
        }

        [Fact]
        public void StaleHit_StartsNothing()
        {
            var st = new EvadeState();
            Assert.False(Evasion.Consider(ref st, T0, T0.AddSeconds(-(Evasion.HitFreshSec + 0.1)), 42, Agile, false, false));
        }

        [Fact]
        public void ContinuousFire_IsRateLimited()
        {
            var st = new EvadeState();
            int starts = 0, inBurst = 0, ticks = 0;
            for (double t = 0; t < 120; t += 0.1)
            {
                DateTime now = T0.AddSeconds(t);
                if (Evasion.Consider(ref st, now, now, 7, Agile, false, false)) starts++;
                if (Evasion.Active(st, now)) inBurst++;
                ticks++;
            }
            int most = (int)(120 / Evasion.MinIntervalSec) + 1;
            Assert.InRange(starts, most - 1, most);
            Assert.True(inBurst <= ticks * (Evasion.MaxBurstSec / Evasion.MinIntervalSec) + 1,
                "not pinned in bursts: " + inBurst + "/" + ticks);
        }

        [Fact]
        public void Bursts_AreDeterministicBySeed()
        {
            List<string> a = Sequence(1234), b = Sequence(1234);
            Assert.Equal(a, b);
            bool differs = false;
            for (long seed = 1; seed < 10 && !differs; seed++)
                if (!Sequence(seed).SequenceEqual(a)) differs = true;
            Assert.True(differs);
        }

        private static List<string> Sequence(long seed)
        {
            var st = new EvadeState();
            var seq = new List<string>();
            for (double t = 0; t < 200; t += 0.5)
            {
                DateTime now = T0.AddSeconds(t);
                if (Evasion.Consider(ref st, now, now, seed, Agile, false, false))
                    seq.Add(st.Dir + "/" + st.DurationSec.ToString("F3"));
            }
            return seq;
        }

        [Fact]
        public void Bursts_TakeEveryDirection_AndNeverDownInGravity()
        {
            var space = new HashSet<EvadeDir>();
            for (long seed = 1; seed <= 200; seed++)
            {
                var st = new EvadeState();
                Assert.True(Evasion.Consider(ref st, T0, T0, seed, Agile, false, true));
                Assert.NotEqual(EvadeDir.Down, st.Dir);
                var sp = new EvadeState();
                Evasion.Consider(ref sp, T0, T0, seed, Agile, false, false);
                space.Add(sp.Dir);
            }
            Assert.Equal(5, space.Count);
        }

        [Fact]
        public void FormationHeld_NeverBreaks_AndCutsARunningBurst()
        {
            var st = new EvadeState();
            Assert.False(Evasion.Consider(ref st, T0, T0, 42, Agile, true, false));
            Assert.True(Evasion.Consider(ref st, T0, T0, 42, Agile, false, false));
            Assert.False(Evasion.Consider(ref st, T0.AddSeconds(0.1), T0.AddSeconds(0.1), 42, Agile, true, false));
            Assert.False(Evasion.Active(st, T0.AddSeconds(0.2)));
        }

        [Fact]
        public void GunPlatform_NeverBreaks()
        {
            var st = new EvadeState();
            Assert.False(Evasion.Consider(ref st, T0, T0, 42, CombatAgility.Motion(1.0, false), false, false));
        }

        [Theory]
        [InlineData(ManoeuvreKind.HoldAtRange)]
        [InlineData(ManoeuvreKind.Orbit)]
        [InlineData(ManoeuvreKind.Kite)]
        [InlineData(ManoeuvreKind.AttackRun)]
        public void Burst_ReplacesTheTangentialTermAndKeepsTheNoseOnTarget(ManoeuvreKind kind)
        {
            FlightSample s = OnStation(30);
            s.InnerRangeM = 300;
            s.RunInbound = false;
            s.Evade = EvadeDir.Up;
            s.EvadeSpeedMs = 45;
            FlightCommand cmd = FlightManoeuvre.Tick(kind, s);
            Assert.True(cmd.MoveDirWorld.Y > 0.99, kind + " breaks up: " + cmd.MoveDirWorld.Y);
            Assert.Equal(0.0, cmd.FaceTargetWorld.Value.X, 9);
        }

        [Fact]
        public void Burst_LeftAndRightAreOpposite_BoostFollowsTheOrbit()
        {
            FlightSample s = OnStation(30);
            s.EvadeSpeedMs = 45;
            s.Evade = EvadeDir.Left;
            Vec3 left = FlightManoeuvre.EvadeVelocity(s);
            s.Evade = EvadeDir.Right;
            Vec3 right = FlightManoeuvre.EvadeVelocity(s);
            s.Evade = EvadeDir.Boost;
            Vec3 boost = FlightManoeuvre.EvadeVelocity(s);
            s.Evade = EvadeDir.None;
            Vec3 orbit = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s).MoveDirWorld;
            Assert.True(Dot(left, right) < 0);
            Assert.True(Math.Abs(Dot(left, new Vec3(1, 0, 0))) < 1e-9, "across the line of sight");
            Assert.True(Dot(boost, orbit) > 0.99 * Len(boost));
        }

        [Fact]
        public void Burst_DownInGravityGoesUp()
        {
            FlightSample s = OnStation(30);
            s.UpWorld = new Vec3(0, 1, 0);
            s.Evade = EvadeDir.Down;
            s.EvadeSpeedMs = 45;
            Assert.True(FlightManoeuvre.EvadeVelocity(s).Y > 0);
        }

        [Fact]
        public void Burst_IsCappedByWhatTheHullCanShedWithinItsReach()
        {
            FlightSample s = OnStation(30);
            s.DecelMs2 = 1.0;
            s.Evade = EvadeDir.Right;
            s.EvadeSpeedMs = 200;
            double reach = FlightManoeuvre.RangeSpeedCapMs(Hold * FlightManoeuvre.BurstReachFraction, 1.0, Cruise);
            Assert.Equal(reach, Len(FlightManoeuvre.EvadeVelocity(s)), 9);
            Assert.True(reach < 200);
        }

        [Fact]
        public void Disengage_IgnoresABurst()
        {
            FlightSample s = OnStation(30);
            FlightCommand plain = FlightManoeuvre.Tick(ManoeuvreKind.Disengage, s);
            s.Evade = EvadeDir.Up;
            s.EvadeSpeedMs = 45;
            FlightCommand burst = FlightManoeuvre.Tick(ManoeuvreKind.Disengage, s);
            Assert.Equal(plain.MoveDirWorld.X, burst.MoveDirWorld.X, 12);
            Assert.Equal(plain.MoveDirWorld.Y, burst.MoveDirWorld.Y, 12);
        }

        [Fact]
        public void FormationHeld_EndToEnd_RestsOnStation()
        {
            FlightSample s = OnStation(CombatAgility.Motion(12.0, true).FloorMs);
            Assert.True(IsZero(FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s).MoveDirWorld));
        }
    }
}
