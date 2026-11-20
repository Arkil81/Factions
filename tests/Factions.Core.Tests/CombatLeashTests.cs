using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="CombatLeash"/>: a fighting hull is held to its anchor — unchanged inside the leash,
    /// drawn back in its edge band, flown home past it — target selection prefers hostiles nearer the
    /// anchor, and a break-off is measured from the enemy rather than the node.
    /// </summary>
    public class CombatLeashTests
    {
        private const double Decel = 2.0;
        private const double Cruise = 100.0;
        private const double Leash = 2500.0;   // inner edge 1875 m

        /// <summary>Target at the origin, hull at <paramref name="pos"/>, anchor at
        /// <paramref name="anchor"/>, leash <see cref="Leash"/>.</summary>
        private static FlightSample Sample(Vec3 pos, Vec3 anchor, double holdM = 1000, double leashM = Leash)
            => new FlightSample
            {
                PosM            = pos,
                TargetM         = new Vec3(0, 0, 0),
                DecelMs2        = Decel,
                CruiseMs        = Cruise,
                ArriveSpeedMs   = 2.0,
                HoldRangeM      = holdM,
                InnerRangeM     = 300,
                RangeToleranceM = 25.0,
                AnchorM         = anchor,
                LeashM          = leashM,
            };

        private static double Len(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static bool IsZero(Vec3 v) => v.X == 0 && v.Y == 0 && v.Z == 0;

        // ---- radius and zones -------------------------------------------------------------

        [Fact]
        public void Radius_IsTheLongestWeaponPlusTheMargin()
        {
            var bands = new List<WeaponBand>
            {
                new WeaponBand { Dps = 10, RangeM = 800 },
                new WeaponBand { Dps = 5, RangeM = 2000 },
            };
            Assert.Equal(2000 + CombatLeash.MarginM, CombatLeash.RadiusM(bands, 900), 6);
        }

        [Fact]
        public void Radius_IsNeverTighterThanTheRangeHeld()
        {
            // An unarmed hull holds at the longest bracket; its leash must admit that.
            Assert.Equal(1500 + CombatLeash.MarginM, CombatLeash.RadiusM(null, 1500), 6);
            Assert.Equal(1500 + CombatLeash.MarginM, CombatLeash.RadiusM(new List<WeaponBand>(), 1500), 6);
        }

        [Theory]
        [InlineData(0, LeashZone.Inside)]
        [InlineData(1875, LeashZone.Inside)]
        [InlineData(1876, LeashZone.Edge)]
        [InlineData(2500, LeashZone.Edge)]
        [InlineData(2501, LeashZone.Beyond)]
        public void Zone_SplitsAtTheInnerEdgeAndTheLeash(double dist, LeashZone want)
        {
            Assert.Equal(want, CombatLeash.Zone(dist, Leash));
        }

        [Fact]
        public void Zone_WithNoLeash_IsNone()
        {
            Assert.Equal(LeashZone.None, CombatLeash.Zone(100000, 0));
        }

        // ---- inside: unchanged ------------------------------------------------------------

        [Theory]
        [InlineData(ManoeuvreKind.HoldAtRange)]
        [InlineData(ManoeuvreKind.Orbit)]
        [InlineData(ManoeuvreKind.Kite)]
        [InlineData(ManoeuvreKind.AttackRun)]
        public void InsideTheLeash_TheManoeuvreIsUnchanged(ManoeuvreKind kind)
        {
            // Hull 3 km out from a target sitting on its anchor; leash wide enough for it.
            FlightSample leashed = Sample(new Vec3(3000, 0, 0), new Vec3(0, 0, 0), leashM: 5000);
            leashed.VelMs = new Vec3(-10, 5, 0);
            leashed.RunInbound = true;
            FlightSample free = leashed;
            free.LeashM = 0;

            FlightCommand a = FlightManoeuvre.Tick(kind, leashed);
            FlightCommand b = FlightManoeuvre.Tick(kind, free);
            Assert.Equal(b.MoveDirWorld.X, a.MoveDirWorld.X, 12);
            Assert.Equal(b.MoveDirWorld.Y, a.MoveDirWorld.Y, 12);
            Assert.Equal(b.MoveDirWorld.Z, a.MoveDirWorld.Z, 12);
            Assert.Equal(b.SpeedCapMs, a.SpeedCapMs, 12);
        }

        // ---- the edge: drawn back ---------------------------------------------------------

        [Fact]
        public void AtTheEdge_AHullOnStationIsDrawnTowardTheAnchor()
        {
            // On station at 1000 m from the target, 2300 m from an anchor off along +Z: unleashed it
            // brakes and holds; leashed it moves toward the anchor.
            FlightSample s = Sample(new Vec3(1000, 0, 0), new Vec3(1000, 0, 2300));
            FlightSample free = s;
            free.LeashM = 0;
            Assert.True(IsZero(FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, free).MoveDirWorld));

            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
            Assert.Equal(LeashZone.Edge, FlightManoeuvre.Leash(s));
            Assert.True(cmd.MoveDirWorld.Z > 0.99, "should move toward the anchor, got " + cmd.MoveDirWorld.Z);
            Assert.Equal(0.0, Len(cmd.FaceTargetWorld.Value), 6);   // nose stays on the target
        }

        [Fact]
        public void AtTheEdge_ThePullGrowsWithDepth()
        {
            FlightCommand shallow = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Sample(new Vec3(1000, 0, 0), new Vec3(1000, 0, 1950)));
            FlightCommand deep = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Sample(new Vec3(1000, 0, 0), new Vec3(1000, 0, 2450)));
            Assert.True(deep.SpeedCapMs > shallow.SpeedCapMs, shallow.SpeedCapMs + " vs " + deep.SpeedCapMs);
            Assert.True(shallow.SpeedCapMs > 0);
        }

        [Fact]
        public void AtTheEdge_TheWholeIsHeldUnderCruise()
        {
            FlightSample s = Sample(new Vec3(1000, 0, 0), new Vec3(1000, 0, 2499));
            s.CruiseMs = 20;
            Assert.True(FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s).SpeedCapMs <= 20 + 1e-9);
        }

        // ---- beyond: back to the anchor ---------------------------------------------------

        [Theory]
        [InlineData(ManoeuvreKind.HoldAtRange)]
        [InlineData(ManoeuvreKind.Orbit)]
        [InlineData(ManoeuvreKind.Kite)]
        [InlineData(ManoeuvreKind.AttackRun)]
        public void BeyondTheLeash_StopsChasingAndReturnsFacingTheTarget(ManoeuvreKind kind)
        {
            // The F3 drift: target 3 km out, hull 5 km from its anchor, target running further away.
            // Unleashed the hull follows (+X); leashed it flies home (-X) with its nose on the target.
            FlightSample s = Sample(new Vec3(-2000, 0, 0), new Vec3(-7000, 0, 0), holdM: 1000);
            s.TargetVelMs = new Vec3(40, 0, 0);
            s.RunInbound = true;
            FlightSample free = s;
            free.LeashM = 0;
            Assert.True(FlightManoeuvre.Tick(kind, free).MoveDirWorld.X > 0, "unleashed should follow the target");

            FlightCommand cmd = FlightManoeuvre.Tick(kind, s);
            Assert.Equal(LeashZone.Beyond, FlightManoeuvre.Leash(s));
            Assert.True(cmd.MoveDirWorld.X < -0.99, kind + " should fly home, got " + cmd.MoveDirWorld.X);
            Assert.Equal(0.0, Len(cmd.FaceTargetWorld.Value), 6);
            Assert.False(cmd.Arrived);
        }

        [Fact]
        public void BeyondTheLeash_TheReturnSpeedCanStopByTheInnerEdge()
        {
            FlightSample s = Sample(new Vec3(-2000, 0, 0), new Vec3(-4600, 0, 0));
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
            double gap = 2600 - CombatLeash.InnerM(Leash);
            Assert.True(FlightManoeuvre.StoppingDistanceM(cmd.SpeedCapMs, Decel) <= gap + 1e-6);
            Assert.True(cmd.SpeedCapMs > 0);
        }

        [Fact]
        public void BeyondTheLeash_AFighterKeepsItsFloor()
        {
            // Weak brake: the capped return speed is ~3.5 m/s; the combat floor keeps it at 20.
            FlightSample s = Sample(new Vec3(-2000, 0, 0), new Vec3(-4600, 0, 0));
            s.DecelMs2 = 0.01;
            s.MinCombatSpeedMs = 20;
            Assert.Equal(20.0, FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s).SpeedCapMs, 9);
        }

        [Fact]
        public void BeyondTheLeash_InGravity_ClimbsWhileOpeningFromItsTarget()
        {
            // Level with a surface target, anchor on the far side of the hull from it: the way home
            // opens the range, so it climbs.
            FlightSample s = Sample(new Vec3(1000, 0, 0), new Vec3(4000, 0, 0));
            s.UpWorld = new Vec3(0, 1, 0);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
            Assert.True(cmd.MoveDirWorld.Y >= FlightManoeuvre.MinOpeningClimb - 1e-9);
            Assert.True(cmd.MoveDirWorld.X > 0);
        }

        [Fact]
        public void BeyondTheLeash_InGravity_TheClimbNeverTurnsItAwayFromHome()
        {
            // Anchor straight below, target above: the climb rule would send it straight up, away
            // from the anchor for ever; the way home is kept.
            FlightSample s = Sample(new Vec3(0, -500, 0), new Vec3(0, -4000, 0));
            s.UpWorld = new Vec3(0, 1, 0);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, s);
            Assert.True(cmd.MoveDirWorld.Y < -0.99, "should descend toward the anchor, got " + cmd.MoveDirWorld.Y);
        }

        [Fact]
        public void TheLeash_LeavesFormUpAndDisengageAlone()
        {
            FlightSample s = Sample(new Vec3(500, 0, 0), new Vec3(-9000, 0, 0), holdM: 3000);
            FlightSample free = s;
            free.LeashM = 0;
            FlightCommand a = FlightManoeuvre.Tick(ManoeuvreKind.Disengage, s);
            FlightCommand b = FlightManoeuvre.Tick(ManoeuvreKind.Disengage, free);
            Assert.Equal(b.MoveDirWorld.X, a.MoveDirWorld.X, 12);
            Assert.True(a.MoveDirWorld.X > 0.99);
        }

        // ---- break-off measured from the enemy -------------------------------------------

        [Fact]
        public void Disengage_FromTheEnemy_IsNotDoneFarFromTheNodeWhileAHostileIsNear()
        {
            // F3: the fight drifted 4 km off the node. Under the node rule the break-off was over
            // before it began; measured from the enemy, a hostile 800 m away keeps it running.
            FlightSample s = Sample(new Vec3(4042, 0, 0), new Vec3(0, 0, 0), holdM: 3500, leashM: 0);
            Assert.True(FlightManoeuvre.Tick(ManoeuvreKind.Disengage, s).PhaseDone, "node rule");

            s.ClearOfHostilesM = CombatLeash.DisengageClearM;
            s.HostileSepM = 800;
            Assert.False(FlightManoeuvre.Tick(ManoeuvreKind.Disengage, s).PhaseDone);
        }

        [Fact]
        public void Disengage_FromTheEnemy_IsDoneOnceEveryHostileIsClear_WhereverTheNodeIs()
        {
            FlightSample s = Sample(new Vec3(200, 0, 0), new Vec3(0, 0, 0), holdM: 3500, leashM: 0);
            s.ClearOfHostilesM = CombatLeash.DisengageClearM;
            s.HostileSepM = CombatLeash.DisengageClearM - 1;
            Assert.False(FlightManoeuvre.Tick(ManoeuvreKind.Disengage, s).PhaseDone);
            s.HostileSepM = CombatLeash.DisengageClearM;
            Assert.True(FlightManoeuvre.Tick(ManoeuvreKind.Disengage, s).PhaseDone);
            s.HostileSepM = double.PositiveInfinity;
            Assert.True(FlightManoeuvre.Tick(ManoeuvreKind.Disengage, s).PhaseDone);
        }

        // ---- target selection -------------------------------------------------------------

        private static TargetCandidate C(long id, double m, double anchorM, bool opponent = false)
        {
            return new TargetCandidate { EntityId = id, DistanceM = m, AnchorDistanceM = anchorM, Opponent = opponent };
        }

        [Fact]
        public void Pick_PrefersTheHostileNearerTheAnchor()
        {
            // 100 is nearer the hull but 3 km from the anchor; 200 is a little further and at the node.
            var c = new[] { C(100, 800, 3000), C(200, 1000, 300) };
            Assert.Equal(100, TargetSelection.Pick(TargetScope.Any, 0, 0, false, default(FireRead), c, 2));
            Assert.Equal(200, TargetSelection.Pick(TargetScope.Any, 0, 0, false, default(FireRead), c, 2, Leash));
        }

        [Fact]
        public void Pick_InsideTheInnerEdge_IsStillNearestFirst()
        {
            var c = new[] { C(100, 800, 1800), C(200, 1000, 300) };
            Assert.Equal(100, TargetSelection.Pick(TargetScope.Any, 0, 0, false, default(FireRead), c, 2, Leash));
        }

        [Fact]
        public void Pick_AnAttackerIsStillTakenFirst_WhereverItIs()
        {
            var fire = new FireRead { AttackerId = 100, AttackerDamage = 50 };
            var c = new[] { C(100, 800, 4000), C(200, 1000, 300) };
            Assert.Equal(100, TargetSelection.Pick(TargetScope.Any, 0, 0, false, fire, c, 2, Leash));
        }

        [Fact]
        public void Pick_TheOpponentStillComesFirst()
        {
            var c = new[] { C(100, 800, 4000, opponent: true), C(200, 500, 300) };
            Assert.Equal(100, TargetSelection.Pick(TargetScope.PreferOpponents, 0, 0, false, default(FireRead), c, 2, Leash));
        }

        [Fact]
        public void Pick_TheSwitchMarginStillHolds_OnChargedDistances()
        {
            // Held 100: 1000 m off, 1900 m from the anchor (charged 50 m, cost 1050). 200 at 800 m at
            // the node costs 800, not under 0.7 x 1050: kept.
            var c = new[] { C(100, 1000, 1900), C(200, 800, 0) };
            Assert.Equal(100, TargetSelection.Pick(TargetScope.Any, 100, 1000, false, default(FireRead), c, 2, Leash, 1900));
            // Held 100 dragged 2.5 km off the anchor (charged 1250, cost 2250): 200 now takes it.
            c[0] = C(100, 1000, 2500);
            Assert.Equal(200, TargetSelection.Pick(TargetScope.Any, 100, 1000, false, default(FireRead), c, 2, Leash, 2500));
        }
    }
}
