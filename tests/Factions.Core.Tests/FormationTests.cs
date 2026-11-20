using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Escort formation: <see cref="ManoeuvreKind.FormUp"/> holding a slot on a moving anchor with
    /// the braking rule honoured, the slot and spread geometry that keeps hulls off one point, the
    /// anchor fallback, and the bubble cap.
    /// </summary>
    public class FormationTests
    {
        private const double Decel = 2.0;
        private const double Cruise = 100.0;
        private const double Spacing = 200.0;

        private static double Len(Vec3 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static Vec3 Sub(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        private static Vec3 Add(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        private static Vec3 Scale(Vec3 v, double k) => new Vec3(v.X * k, v.Y * k, v.Z * k);
        private static bool IsZero(Vec3 v) => v.X == 0 && v.Y == 0 && v.Z == 0;

        /// <summary>An escort at <paramref name="pos"/> forming on an anchor at the origin facing -Z
        /// with up +Y, slot 0 at <see cref="Spacing"/>.</summary>
        private static FlightSample Sample(Vec3 pos, Vec3 vel, Vec3 anchorVel, int slot = 0)
            => new FlightSample
            {
                PosM            = pos,
                VelMs           = vel,
                TargetM         = new Vec3(0, 0, 0),
                TargetVelMs     = anchorVel,
                AnchorForward   = new Vec3(0, 0, -1),
                AnchorUp        = new Vec3(0, 1, 0),
                SlotM           = Formation.SlotOffset(slot, Spacing),
                DecelMs2        = Decel,
                CruiseMs        = Cruise,
                ArriveSpeedMs   = 2.0,
                RangeToleranceM = Formation.SlotToleranceM,
            };

        // ---- slot geometry ------------------------------------------------------------------

        [Fact]
        public void SlotOffset_NoTwoSlotsNorTheAnchorShareAPoint()
        {
            var points = new List<Vec3> { new Vec3(0, 0, 0) };
            for (int i = 0; i < 12; i++) points.Add(Formation.SlotOffset(i, Spacing));

            for (int a = 0; a < points.Count; a++)
                for (int b = a + 1; b < points.Count; b++)
                    Assert.True(points[a].DistanceTo(points[b]) >= Spacing * Math.Sqrt(2) - 1e-9,
                        "slots " + (a - 1) + " and " + (b - 1) + " are " + points[a].DistanceTo(points[b]) + " m apart");
        }

        [Fact]
        public void SlotOffset_IsAWedgeBehindTheAnchorAlternatingSides()
        {
            Vec3 s0 = Formation.SlotOffset(0, Spacing);
            Vec3 s1 = Formation.SlotOffset(1, Spacing);
            Vec3 s2 = Formation.SlotOffset(2, Spacing);
            Assert.True(s0.X > 0 && s1.X < 0, "first two slots straddle the anchor");
            Assert.True(s0.Z > 0 && s1.Z > 0, "slots are aft");
            Assert.True(s2.Z > s0.Z && s2.X > s0.X, "the next rank is further back and further out");
        }

        [Theory]
        [InlineData(0, 100.0)]
        [InlineData(10, 100.0)]
        [InlineData(50, 160.0)]
        [InlineData(120, 300.0)]
        public void SlotSpacing_IsTheLargestDiameterPlusTheGap(double radius, double expected)
        {
            Assert.Equal(expected, Formation.SlotSpacingM(radius), 6);
        }

        [Fact]
        public void SlotWorld_FollowsTheAnchorsOrientation()
        {
            Vec3 slot = Formation.SlotOffset(0, Spacing);   // right and aft

            // Nose -Z, up +Y: right is +X, aft is +Z.
            Vec3 a = Formation.SlotWorld(new Vec3(10, 20, 30), new Vec3(0, 0, -1), new Vec3(0, 1, 0), slot);
            Assert.Equal(10 + Spacing, a.X, 6);
            Assert.Equal(20, a.Y, 6);
            Assert.Equal(30 + Spacing, a.Z, 6);

            // Nose +X, up +Y: right is +Z, aft is -X.
            Vec3 b = Formation.SlotWorld(new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), slot);
            Assert.Equal(-Spacing, b.X, 6);
            Assert.Equal(0, b.Y, 6);
            Assert.Equal(Spacing, b.Z, 6);
        }

        [Fact]
        public void SlotWorld_ADegenerateFrameStillPlacesTheSlotAtItsDistance()
        {
            Vec3 slot = Formation.SlotOffset(0, Spacing);
            Vec3 w = Formation.SlotWorld(new Vec3(0, 0, 0), new Vec3(0, 0, 0), new Vec3(0, 0, 0), slot);
            Assert.Equal(Len(slot), Len(w), 6);
            Vec3 along = Formation.SlotWorld(new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 1, 0), slot);
            Assert.Equal(Len(slot), Len(along), 6);
        }

        // ---- FormUp -------------------------------------------------------------------------

        [Fact]
        public void FormUp_InItsSlotBehindAStillAnchor_BrakesAndHolds()
        {
            Vec3 slot = Formation.SlotWorld(new Vec3(0, 0, 0), new Vec3(0, 0, -1), new Vec3(0, 1, 0),
                                            Formation.SlotOffset(0, Spacing));
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.FormUp, Sample(slot, new Vec3(0, 0, 0), new Vec3(0, 0, 0)));
            Assert.True(IsZero(cmd.MoveDirWorld));
            Assert.False(cmd.Arrived);
            Assert.False(cmd.Complete);
        }

        [Fact]
        public void FormUp_FacesAlongTheAnchorsNose()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.FormUp,
                Sample(new Vec3(1000, 0, 0), new Vec3(0, 0, 0), new Vec3(0, 0, 0)));
            Assert.True(cmd.FaceTargetWorld.HasValue);
            Vec3 face = Sub(cmd.FaceTargetWorld.Value, new Vec3(1000, 0, 0));
            Assert.True(Dot(face, new Vec3(0, 0, -1)) / Len(face) > 0.999);
        }

        [Fact]
        public void FormUp_FarFromItsSlot_ThrustsTowardIt()
        {
            Vec3 pos = new Vec3(3000, 0, 0);
            FlightSample s = Sample(pos, new Vec3(0, 0, 0), new Vec3(0, 0, 0));
            Vec3 slot = FlightManoeuvre.SlotWorld(s);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.FormUp, s);
            Vec3 toSlot = Sub(slot, pos);
            Assert.True(Dot(cmd.MoveDirWorld, toSlot) / Len(toSlot) > 0.99);
        }

        [Theory]
        [InlineData(30)]
        [InlineData(100)]
        [InlineData(500)]
        [InlineData(2500)]
        [InlineData(20000)]
        public void FormUp_NeverAsksToCloseFasterThanItCanStopInTheSlot(double gapM)
        {
            FlightSample s = Sample(new Vec3(0, 0, 0), new Vec3(0, 0, 0), new Vec3(0, 0, 0));
            Vec3 slot = FlightManoeuvre.SlotWorld(s);
            s.PosM = Add(slot, new Vec3(gapM, 0, 0));
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.FormUp, s);
            double stop = FlightManoeuvre.StoppingDistanceM(cmd.SpeedCapMs, Decel);
            Assert.True(stop <= gapM + 1e-6, "stop " + stop + " vs gap " + gapM);
        }

        [Fact]
        public void FormUp_ClosingFasterThanItCanStop_ThrustsAgainstTheClose()
        {
            // 100 m short of the slot at 2 m/s^2 allows 20 m/s of closing; closing at 50.
            FlightSample s = Sample(new Vec3(0, 0, 0), new Vec3(0, 0, 0), new Vec3(0, 0, 0));
            Vec3 slot = FlightManoeuvre.SlotWorld(s);
            s.PosM = Add(slot, new Vec3(100, 0, 0));
            s.VelMs = new Vec3(-50, 0, 0);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.FormUp, s);
            Assert.True(cmd.MoveDirWorld.X > 0.99, "should brake away from the slot");
        }

        [Fact]
        public void FormUp_InItsSlotBehindAMovingAnchor_MatchesItsVelocity()
        {
            var anchorVel = new Vec3(0, 0, -40);
            FlightSample s = Sample(new Vec3(0, 0, 0), anchorVel, anchorVel);
            s.PosM = FlightManoeuvre.SlotWorld(s);
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.FormUp, s);
            Assert.Equal(40.0, cmd.SpeedCapMs, 6);
            Assert.False(IsZero(cmd.MoveDirWorld), "must not brake to a stop under a moving anchor");
            Assert.True(Math.Abs(cmd.MoveDirWorld.Z) > 0.99, "thrust stays on the anchor's line of travel");
        }

        /// <summary>A point mass flown by <see cref="FlightManoeuvre.Tick"/>: the commanded direction
        /// accelerates at <see cref="Decel"/> while the velocity across it is dampened at the same
        /// rate, and a zero command dampens the whole velocity — SE's AI-thrust path, to first order.</summary>
        private static void Step(ref Vec3 pos, ref Vec3 vel, Vec3 dir, double dt)
        {
            if (IsZero(dir))
            {
                double sp = Len(vel);
                double ns = Math.Max(0, sp - Decel * dt);
                vel = sp > 1e-9 ? Scale(vel, ns / sp) : vel;
            }
            else
            {
                double along = Dot(vel, dir);
                Vec3 across = Sub(vel, Scale(dir, along));
                double al = Len(across);
                double nal = Math.Max(0, al - Decel * dt);
                across = al > 1e-9 ? Scale(across, nal / al) : across;
                vel = Add(Scale(dir, along + Decel * dt), across);
            }
            pos = Add(pos, Scale(vel, dt));
        }

        [Fact]
        public void FormUp_ConvergesOnTheSlotOfAMovingAnchorAndStaysThere()
        {
            const double dt = 1.0 / 60.0;
            var anchorVel = new Vec3(0, 0, -40);
            Vec3 anchor = new Vec3(0, 0, 0);
            Vec3 pos = new Vec3(2500, 300, 1500);
            Vec3 vel = new Vec3(0, 0, 0);

            double worstLate = 0;
            for (int i = 0; i < 60 * 240; i++)
            {
                FlightSample s = Sample(pos, vel, anchorVel);
                s.TargetM = anchor;
                FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.FormUp, s);
                Assert.False(cmd.Arrived);
                Step(ref pos, ref vel, cmd.MoveDirWorld, dt);
                anchor = Add(anchor, Scale(anchorVel, dt));

                if (i >= 60 * 180)
                {
                    FlightSample now = Sample(pos, vel, anchorVel);
                    now.TargetM = anchor;
                    double err = FlightManoeuvre.SlotWorld(now).DistanceTo(pos);
                    if (err > worstLate) worstLate = err;
                }
            }
            Assert.True(worstLate <= 3 * Formation.SlotToleranceM, "slot error in the last minute reached " + worstLate + " m");
            Assert.True(Len(Sub(vel, anchorVel)) < 3.0, "relative speed " + Len(Sub(vel, anchorVel)));
        }

        // ---- combat spread ------------------------------------------------------------------

        [Fact]
        public void SpreadBearing_EveryHullHoldsItsOwnBearingAtLeastAStepApart()
        {
            var lead = new Vec3(1, 0, 0);
            var up = new Vec3(0, 1, 0);
            double step = Formation.SpreadStepRad(Spacing, 800, 4);
            var bearings = new List<Vec3> { lead };
            for (int i = 0; i < 4; i++) bearings.Add(Formation.SpreadBearing(lead, up, i, step));

            for (int a = 0; a < bearings.Count; a++)
            {
                Assert.Equal(1.0, Len(bearings[a]), 9);
                Assert.Equal(0.0, bearings[a].Y, 9);   // turned about up: stays level
                for (int b = a + 1; b < bearings.Count; b++)
                {
                    double angle = Math.Acos(Math.Max(-1, Math.Min(1, Dot(bearings[a], bearings[b]))));
                    Assert.True(angle >= step - 1e-9, "hulls " + a + "/" + b + " only " + angle + " rad apart");
                }
            }
        }

        [Fact]
        public void SpreadStep_KeepsNeighboursASlotApartAndTheOutermostShortOfTheFarSide()
        {
            Assert.Equal(Spacing / 600.0, Formation.SpreadStepRad(Spacing, 600, 2), 9);
            Assert.Equal(Formation.MinSpreadStepRad, Formation.SpreadStepRad(Spacing, 5000, 2), 9);

            // Eight escorts at 150 m would need more than a half-turn a side; the fan is clamped.
            double step = Formation.SpreadStepRad(Spacing, 150, 8);
            Assert.True(4 * step <= Formation.MaxSpreadRad + 1e-9);
        }

        [Fact]
        public void SpreadBearing_AnAxisAlongTheBearingStillGivesADistinctUnitBearing()
        {
            Vec3 b = Formation.SpreadBearing(new Vec3(0, 1, 0), new Vec3(0, 1, 0), 0, 0.5);
            Assert.Equal(1.0, Len(b), 9);
            Assert.Equal(Math.Cos(0.5), b.Y, 9);
        }

        private static FlightSample Range(Vec3 pos, double hold, Vec3 spread)
            => new FlightSample
            {
                PosM            = pos,
                TargetM         = new Vec3(0, 0, 0),
                DecelMs2        = Decel,
                CruiseMs        = Cruise,
                ArriveSpeedMs   = 2.0,
                HoldRangeM      = hold,
                InnerRangeM     = 300,
                RunInbound      = false,
                RangeToleranceM = 25.0,
                SpreadBearing   = spread,
            };

        [Theory]
        [InlineData(ManoeuvreKind.HoldAtRange)]
        [InlineData(ManoeuvreKind.Kite)]
        [InlineData(ManoeuvreKind.AttackRun)]
        public void Spread_OnTheShellOffItsBearing_MovesRoundTheTargetTowardIt(ManoeuvreKind kind)
        {
            FlightCommand cmd = FlightManoeuvre.Tick(kind, Range(new Vec3(1000, 0, 0), 1000, new Vec3(0, 1, 0)));
            Assert.True(cmd.MoveDirWorld.Y > 0.9, "should go round toward +Y, got " + cmd.MoveDirWorld);
        }

        [Fact]
        public void Spread_OnItsBearing_HoldsLikeTheLead()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Range(new Vec3(1000, 0, 0), 1000, new Vec3(1, 0, 0)));
            Assert.True(IsZero(cmd.MoveDirWorld));
        }

        [Fact]
        public void Spread_None_LeavesTheCombatCommandExactlyAsItWas()
        {
            foreach (ManoeuvreKind kind in new[] { ManoeuvreKind.HoldAtRange, ManoeuvreKind.Kite, ManoeuvreKind.AttackRun })
            {
                FlightSample s = Range(new Vec3(700, 400, 0), 1000, new Vec3(0, 0, 0));
                s.VelMs = new Vec3(3, -2, 1);
                FlightCommand a = FlightManoeuvre.Tick(kind, s);
                s.SpreadBearing = new Vec3(700, 400, 0);   // already on it: nothing to add
                FlightCommand b = FlightManoeuvre.Tick(kind, s);
                Assert.Equal(a.MoveDirWorld.X, b.MoveDirWorld.X, 9);
                Assert.Equal(a.MoveDirWorld.Y, b.MoveDirWorld.Y, 9);
                Assert.Equal(a.MoveDirWorld.Z, b.MoveDirWorld.Z, 9);
                Assert.Equal(a.SpeedCapMs, b.SpeedCapMs, 9);
            }
        }

        [Fact]
        public void Spread_StraightAcrossTheTarget_StillPicksADirection()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Range(new Vec3(1000, 0, 0), 1000, new Vec3(-1, 0, 0)));
            Assert.False(IsZero(cmd.MoveDirWorld));
            Assert.True(Math.Abs(cmd.MoveDirWorld.X) < 0.1, "goes round, not through: " + cmd.MoveDirWorld);
        }

        [Fact]
        public void Spread_NeverAsksToSwingFasterThanItCanStopOnItsBearing()
        {
            // 90 degrees off at 1000 m: about 1571 m of arc.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.HoldAtRange, Range(new Vec3(1000, 0, 0), 1000, new Vec3(0, 1, 0)));
            double arc = Math.PI / 2 * 1000;
            Assert.True(FlightManoeuvre.StoppingDistanceM(cmd.SpeedCapMs, Decel) <= arc + 1e-6);
        }

        // ---- anchor and slot assignment ------------------------------------------------------

        [Fact]
        public void PickAnchor_ThePreferredAnchorWhileItLives()
        {
            var escorts = new List<long> { 11, 12, 13 };
            var alive = new HashSet<long> { 1, 5, 11, 12, 13 };
            Assert.Equal(5, Formation.PickAnchor(5, 1, escorts, 12, alive.Contains));
        }

        [Fact]
        public void PickAnchor_FallsBackToTheLeadThenTheNextLiveHullInOrder()
        {
            var escorts = new List<long> { 11, 12, 13 };
            Assert.Equal(1, Formation.PickAnchor(5, 1, escorts, 12, new HashSet<long> { 1, 11, 12, 13 }.Contains));
            Assert.Equal(11, Formation.PickAnchor(1, 1, escorts, 12, new HashSet<long> { 11, 12, 13 }.Contains));
            Assert.Equal(13, Formation.PickAnchor(1, 1, escorts, 12, new HashSet<long> { 12, 13 }.Contains));
        }

        [Fact]
        public void PickAnchor_NeverTheEscortItself_AndNoneWhenNothingElseLives()
        {
            var escorts = new List<long> { 11, 12 };
            Assert.Equal(12, Formation.PickAnchor(11, 11, escorts, 11, new HashSet<long> { 11, 12 }.Contains));
            Assert.Equal(0, Formation.PickAnchor(1, 1, escorts, 11, new HashSet<long> { 11 }.Contains));
        }

        [Fact]
        public void SlotIndex_CountsTheEscortsAheadButNotTheAnchor()
        {
            var escorts = new List<long> { 11, 12, 13, 14 };
            Assert.Equal(0, Formation.SlotIndex(escorts, 11, 1));
            Assert.Equal(3, Formation.SlotIndex(escorts, 14, 1));
            // 11 took over as anchor: 12 moves up into slot 0.
            Assert.Equal(0, Formation.SlotIndex(escorts, 12, 11));
            Assert.Equal(2, Formation.SlotIndex(escorts, 14, 11));
            Assert.Equal(-1, Formation.SlotIndex(escorts, 99, 1));
        }

        // ---- bubble cap --------------------------------------------------------------------

        /// <summary>The host's admission, one escort at a time in squadron order: count what is
        /// controlled near it, admit while under the cap.</summary>
        private static List<int> Admit(List<Vec3> controlled, IList<Vec3> escortPositions, int cap)
        {
            var admitted = new List<int>();
            for (int i = 0; i < escortPositions.Count; i++)
            {
                Vec3[] arr = controlled.ToArray();
                int near = Formation.CountWithin(escortPositions[i], arr, arr.Length, Formation.BubbleRadiusM);
                if (!Formation.AdmitsControl(near, cap)) continue;
                controlled.Add(escortPositions[i]);
                admitted.Add(i);
            }
            return admitted;
        }

        [Fact]
        public void Cap_AdmitsEscortsInSquadronOrderUntilTheBubbleIsFull()
        {
            // Two leads already controlled in the bubble, cap 5: the first three escorts get it.
            var controlled = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(1500, 0, 0) };
            var escorts = new List<Vec3>();
            for (int i = 0; i < 6; i++) escorts.Add(new Vec3(100 * (i + 1), 0, 0));
            Assert.Equal(new List<int> { 0, 1, 2 }, Admit(controlled, escorts, 5));
        }

        [Fact]
        public void Cap_ASquadronInAnotherBubbleIsCountedApart()
        {
            var controlled = new List<Vec3>();
            for (int i = 0; i < 5; i++) controlled.Add(new Vec3(10 * i, 0, 0));
            var escorts = new List<Vec3> { new Vec3(50, 0, 0), new Vec3(20000, 0, 0), new Vec3(20100, 0, 0) };
            Assert.Equal(new List<int> { 1, 2 }, Admit(controlled, escorts, 5));
        }

        [Fact]
        public void Cap_ZeroAdmitsNothing()
        {
            Assert.False(Formation.AdmitsControl(0, 0));
            Assert.True(Formation.AdmitsControl(Formation.MaxControlledPerBubble - 1, Formation.MaxControlledPerBubble));
            Assert.False(Formation.AdmitsControl(Formation.MaxControlledPerBubble, Formation.MaxControlledPerBubble));
        }

        [Fact]
        public void CountWithin_ReadsOnlyTheLivePrefix()
        {
            var arr = new[] { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(20, 0, 0) };
            Assert.Equal(2, Formation.CountWithin(new Vec3(0, 0, 0), arr, 2, 100));
            Assert.Equal(1, Formation.CountWithin(new Vec3(0, 0, 0), arr, 3, 5));
        }

        // ---- the anchor rung of the order ladder ---------------------------------------------

        [Fact]
        public void ResolveAnchor_AgreesWithResolveOnEveryRung()
        {
            var requests = new[]
            {
                new TacticRequest { AnchorEntityId = 7 },
                new TacticRequest { AnchorEntityId = 7, PlayerOverride = new TacticOrder { AnchorEntityId = 8 } },
                new TacticRequest { AnchorEntityId = 7, PlayerOverride = new TacticOrder() },
                new TacticRequest { AnchorEntityId = 7, Commander = new TacticOrder { AnchorEntityId = 9 },
                                    PlayerOverride = new TacticOrder { AnchorEntityId = 8 } },
                new TacticRequest { AnchorEntityId = 7, Commander = new TacticOrder(),
                                    PlayerOverride = new TacticOrder { AnchorEntityId = 8 } },
            };
            foreach (TacticRequest r in requests)
                Assert.Equal(TacticResolver.Resolve(r).AnchorEntityId, TacticResolver.ResolveAnchor(r));
            Assert.Equal(9, TacticResolver.ResolveAnchor(requests[3]));
        }
    }
}
