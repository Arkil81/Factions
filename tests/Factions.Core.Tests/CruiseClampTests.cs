using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers the dynamic cruise clamp and the closed loop it is planned against —
    /// <see cref="FlightManoeuvre.BrakeBudgetM"/>, <see cref="FlightManoeuvre.CruiseCapMs"/>,
    /// <see cref="FlightManoeuvre.AchievedDecelMs2"/> and
    /// <see cref="FlightManoeuvre.PlanningDecelMs2"/> — plus the terminal bubble the clamp budgets
    /// for.
    ///
    /// <para>Live figures the cases are sized against (5 days on the Genesis world, 363 approaches):
    /// braking-estimate p25 1.53, p50 5.80, p75 8.24 m/s²; observation radius 15 km; horizon margin
    /// 1.5. At the 100 m/s that shipped before this, 23% of those approaches needed more braking
    /// distance than the 15 km horizon could give them.</para>
    /// </summary>
    public class CruiseClampTests
    {
        private const double ObsRadiusM  = 15000.0;
        private const double Margin      = 1.5;
        private const double BubbleM     = 300.0;
        private const double BubbleMs    = 10.0;
        private const double FloorMs     = 40.0;
        private const double CeilingMs   = 500.0;

        private static double Budget()
            => FlightManoeuvre.BrakeBudgetM(ObsRadiusM, Margin, BubbleM);

        private static double Cap(double decelMs2)
            => FlightManoeuvre.CruiseCapMs(decelMs2, Budget(), BubbleMs, FloorMs, CeilingMs);

        // ---- brake budget ----------------------------------------------------------------------

        [Fact]
        public void BrakeBudget_IsTheObservableHorizonLessTheBubble()
        {
            // 15000 / 1.5 = 10000 usable, less the 300 m bubble.
            Assert.Equal(9700.0, Budget(), 6);
        }

        [Fact]
        public void BrakeBudget_NeverGoesNegative()
        {
            Assert.Equal(0.0, FlightManoeuvre.BrakeBudgetM(400.0, 1.5, 300.0), 6);
        }

        // ---- the clamp itself ------------------------------------------------------------------

        [Fact]
        public void CruiseCap_GrantsTheSpeedTheGridCanShedInTheBudget()
        {
            // sqrt(10^2 + 2*5.8*9700) = 335.3 m/s for the median live hull.
            Assert.Equal(Math.Sqrt(100.0 + 2.0 * 5.8 * 9700.0), Cap(5.8), 6);
        }

        /// <summary>The whole point of the clamp: the weak quartile and the strong quartile must not
        /// come out at the same number, because that is what the single 100 m/s constant did.</summary>
        [Fact]
        public void CruiseCap_SeparatesTheWeakHullFromTheStrongOne()
        {
            double weak   = Cap(1.53);   // live p25
            double strong = Cap(8.24);   // live p75

            Assert.True(weak < 200, "p25 hull should stay well under 200 m/s, got " + weak);
            Assert.True(strong > 390, "p75 hull should clear 390 m/s, got " + strong);
            Assert.True(strong > weak * 2, "the two quartiles should be far apart, got "
                                           + weak + " and " + strong);
        }

        /// <summary>The granted speed must be shed-able within the budget, which is the safety
        /// property the whole design rests on. Verified by running the profile's own curve
        /// backwards: stopping from the cap must fit, leaving the grid at bubble speed.</summary>
        [Theory]
        [InlineData(0.85)]
        [InlineData(1.53)]
        [InlineData(5.8)]
        [InlineData(8.24)]
        [InlineData(16.89)]
        public void CruiseCap_IsAlwaysShedableWithinTheBudget(double decel)
        {
            double cap = Cap(decel);

            // Distance to go from cap down to bubble speed at this authority.
            double toShed = FlightManoeuvre.StoppingDistanceM(cap, decel)
                          - FlightManoeuvre.StoppingDistanceM(BubbleMs, decel);

            Assert.True(toShed <= Budget() + 1e-6,
                "cap " + cap + " needs " + toShed + " m but the budget is " + Budget());
        }

        [Fact]
        public void CruiseCap_HoldsTheFloorSoAWeakEstimateCannotStrandAGridCrawling()
        {
            // A near-zero estimate must not produce a 5 m/s cruise for a 15 km leg.
            Assert.Equal(FloorMs, Cap(0.0001), 6);
            Assert.Equal(FloorMs, Cap(0.0), 6);
            Assert.Equal(FloorMs, Cap(-1.0), 6);
        }

        [Fact]
        public void CruiseCap_HoldsTheCeiling()
        {
            Assert.Equal(CeilingMs, Cap(1000.0), 6);
        }

        [Fact]
        public void CruiseCap_WithNoBudgetFallsToTheFloor()
        {
            Assert.Equal(FloorMs, FlightManoeuvre.CruiseCapMs(5.8, 0, BubbleMs, FloorMs, CeilingMs), 6);
        }

        // ---- the closed loop -------------------------------------------------------------------

        [Fact]
        public void AchievedDecel_IsSpeedLostPerSecond()
        {
            Assert.Equal(4.0, FlightManoeuvre.AchievedDecelMs2(0.5, 100, 98), 6);
        }

        [Fact]
        public void AchievedDecel_IsNegativeWhenTheGridSpedUp()
        {
            Assert.True(FlightManoeuvre.AchievedDecelMs2(0.5, 98, 100) < 0);
        }

        // ---- which closed runs become a sample ---------------------------------------------------

        private const double ArriveMs = 2.0;   // ApproachArriveSpeedMs default

        /// <summary>A run still moving above arrive speed when it closes is a brake at speed and is
        /// kept at its measured value. This is the positive control for the rejections below.</summary>
        [Fact]
        public void BrakeSample_ABrakeStillMovingWhenTheWindowClosesIsKept()
        {
            Assert.Equal(3.5, FlightManoeuvre.BrakeSampleMs2(1.0, 30.0, 26.5, 4.08, ArriveMs), 6);
        }

        /// <summary>The live case behind this rule: a grid held 221 s at its rest point closed 109
        /// runs, and its best reading was the settle from the 2.0 m/s it was reported stopped at
        /// — 1.00 m/s² on a 4.08 estimate — not anything the approach measured. A run that ends at
        /// or under arrive speed measured the stop, not the brake.</summary>
        [Theory]
        [InlineData(2.0,  1.0)]    // the settle: reached rest partway through the window
        [InlineData(0.05, 0.01)]   // jitter while parked
        [InlineData(3.0,  0.0)]    // a real brake, under the estimate, that ran all the way down inside the window
        public void BrakeSample_ARunThatEndsAtRestMeasuredTheStopNotTheBrake(double before, double after)
        {
            Assert.Equal(0.0, FlightManoeuvre.BrakeSampleMs2(1.0, before, after, 4.08, ArriveMs), 6);
        }

        [Fact]
        public void BrakeSample_SpeedingUpIsNotABrake()
        {
            Assert.Equal(0.0, FlightManoeuvre.BrakeSampleMs2(1.0, 26.0, 30.0, 4.08, ArriveMs), 6);
        }

        /// <summary>Above the thrust estimate is impossible and is discarded outright, not clamped
        /// to it — clamped, it would become the record's maximum by another route.</summary>
        [Fact]
        public void BrakeSample_AboveTheEstimateIsNoiseAndIsDiscardedNotClamped()
        {
            Assert.Equal(0.0, FlightManoeuvre.BrakeSampleMs2(1.0, 100.0, 90.0, 4.08, ArriveMs), 6);
        }

        [Fact]
        public void BrakeSample_WithNoEstimateAnyPositiveBrakeCounts()
        {
            Assert.Equal(10.0, FlightManoeuvre.BrakeSampleMs2(1.0, 100.0, 90.0, 0.0, ArriveMs), 6);
        }

        [Fact]
        public void PlanningDecel_UsesTheEstimateUntilSomethingHasBeenObserved()
        {
            Assert.Equal(29.2, FlightManoeuvre.PlanningDecelMs2(29.2, 0, false, 1.0), 6);
        }

        /// <summary>FAC-007's measured hull: the estimate reports 29.2 m/s² and SE delivers 0.09.
        /// The clamp must plan against the 0.09, or it grants a grid that cannot brake at all the
        /// speed of one that can.</summary>
        [Fact]
        public void PlanningDecel_HoldsTheEstimateDownToWhatTheBrakeActuallyDelivered()
        {
            double planned = FlightManoeuvre.PlanningDecelMs2(29.2, 0.09, true, 1.0);
            Assert.Equal(0.09, planned, 6);

            // The estimate alone would have bought this hull the ceiling. What it actually gets is
            // slower than the 100 m/s flat cap that shipped before the clamp existed — which is the
            // safety property: the grid that cannot stop is the one that is slowed down.
            Assert.Equal(CeilingMs, Cap(29.2), 6);
            Assert.True(Cap(planned) < 100.0,
                "a hull whose brake does not bite must come out under the old flat cap, got "
                + Cap(planned));
        }

        /// <summary>The slack knob is what decides whether the clamp plans against measured
        /// authority or against more than the grid has ever shown. At its 1.0 default the granted
        /// speed is shed-able; above it, it is not — which is why the default is 1.0.</summary>
        [Fact]
        public void PlanningDecel_SlackAboveOnePlansOptimistically()
        {
            const double actual = 0.09;

            double honest = Cap(FlightManoeuvre.PlanningDecelMs2(29.2, actual, true, 1.0));
            double loose  = Cap(FlightManoeuvre.PlanningDecelMs2(29.2, actual, true, 1.5));

            // Speed still carried when the grid reaches the bubble, braking at what it really has.
            double honestEntry = Math.Sqrt(Math.Max(0, honest * honest - 2 * actual * Budget()));
            double looseEntry  = Math.Sqrt(Math.Max(0, loose  * loose  - 2 * actual * Budget()));

            Assert.True(honestEntry <= BubbleMs + 0.5,
                "at slack 1.0 the grid should reach the bubble at bubble speed, got " + honestEntry);
            Assert.True(looseEntry > BubbleMs * 2,
                "slack 1.5 should visibly overshoot the bubble speed, got " + looseEntry);
        }

        [Fact]
        public void PlanningDecel_NeverRaisesTheEstimate()
        {
            // A one-off observation above the estimate (gravity helping, a lighter grid) is not
            // evidence of more authority than the thrust tables show.
            Assert.Equal(2.0, FlightManoeuvre.PlanningDecelMs2(2.0, 50.0, true, 1.0), 6);
        }

        [Fact]
        public void PlanningDecel_AZeroObservationLeavesNoUsableAuthority()
        {
            Assert.Equal(0.0, FlightManoeuvre.PlanningDecelMs2(29.2, 0.0, true, 1.0), 6);
        }

        /// <summary>The failure that shipped in v0.211.0 and that the telemetry caught live: an
        /// observation taken per tick converges on the largest one-frame artifact, which sits ABOVE
        /// the thrust estimate — so <see cref="FlightManoeuvre.PlanningDecelMs2"/>'s min() always
        /// picked the estimate and the clamp never bound. Live figures from that window.</summary>
        [Theory]
        [InlineData(1.50,  6.92)]
        [InlineData(0.89,  7.45)]
        [InlineData(3.32, 19.47)]
        [InlineData(11.11, 71.35)]
        [InlineData(3.32, 23.10)]
        public void PlanningDecel_AnObservationAboveTheEstimateLeavesTheClampInert(double estimate, double observed)
        {
            // This is what the arithmetic does with a noise reading, and why it must never reach here.
            Assert.Equal(estimate, FlightManoeuvre.PlanningDecelMs2(estimate, observed, true, 1.0), 6);
        }

        /// <summary>A deceleration above what opposing thrust allows is not physically reachable —
        /// gravity is already projected into the estimate — so the host discards such a run instead
        /// of recording it. Stated here because the rule lives in the host and this is the shape it
        /// has to preserve: a rejected reading leaves the record alone rather than clamping into it.</summary>
        [Fact]
        public void PlanningDecel_OnlyBindsWhenTheObservationIsBelowTheEstimate()
        {
            Assert.Equal(0.12, FlightManoeuvre.PlanningDecelMs2(29.2, 0.12, true, 1.0), 6);   // binds
            Assert.Equal(29.2, FlightManoeuvre.PlanningDecelMs2(29.2, 30.0, true, 1.0), 6);   // cannot bind
        }

        /// <summary>Windowed measurement is what makes the reading survivable: averaging over a
        /// second of braking rejects the single-frame spike that a per-tick sample reports as a
        /// real figure. Both readings describe the same physical second.</summary>
        [Fact]
        public void AchievedDecel_AWindowAveragesAwayTheSpikeAPerTickSampleReports()
        {
            // One 1/60 s frame in which the reported speed jumps 1.2 m/s: 72 m/s^2 per-tick.
            Assert.Equal(72.0, FlightManoeuvre.AchievedDecelMs2(1.0 / 60.0, 100.0, 98.8), 6);

            // The same second, in which the grid genuinely shed 3 m/s: 3 m/s^2.
            Assert.Equal(3.0, FlightManoeuvre.AchievedDecelMs2(1.0, 100.0, 97.0), 6);
        }

        /// <summary>End to end on FAC-007's hull: a sustained reading below the estimate must pull
        /// the granted cruise speed under the flat cap that shipped before the clamp.</summary>
        [Fact]
        public void CruiseCap_ASustainedReadingOnAHullThatCannotBrakeKeepsItSlow()
        {
            double planned = FlightManoeuvre.PlanningDecelMs2(29.2, 0.12, true, 1.0);
            double cap = Cap(planned);

            Assert.True(cap < 100.0, "expected under the old flat cap, got " + cap);
            Assert.True(cap >= FloorMs, "and never under the floor, got " + cap);
        }

        // ---- terminal bubble -------------------------------------------------------------------

        private static FlightSample Approach(double distM, double speedMs, double decelMs2)
            => new FlightSample
            {
                PosM             = new Vec3(distM, 0, 0),
                TargetM          = new Vec3(0, 0, 0),
                VelMs            = new Vec3(-speedMs, 0, 0),
                DecelMs2         = decelMs2,
                CruiseMs         = 500.0,
                StopDistanceM    = 50.0,
                ArriveToleranceM = 5.0,
                MinApproachMs    = 5.0,
                ArriveSpeedMs    = 2.0,
                TerminalRadiusM  = BubbleM,
                TerminalSpeedMs  = BubbleMs,
            };

        [Fact]
        public void Bubble_CapsEvenAHullThatCouldBrakeFarHarder()
        {
            // 250 m from the node at 17 m/s^2 the sqrt curve alone would allow ~87 m/s.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Approach(250, 60, 16.89));
            Assert.Equal(BubbleMs, cmd.SpeedCapMs, 6);
        }

        [Fact]
        public void Bubble_DoesNotApplyOutsideItsRadius()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Approach(400, 60, 16.89));
            Assert.True(cmd.SpeedCapMs > BubbleMs,
                "outside the bubble the sqrt curve owns the cap, got " + cmd.SpeedCapMs);
        }

        [Fact]
        public void Bubble_CommandsAFullBrakeOnAGridThatEntersItTooFast()
        {
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Approach(250, 60, 16.89));
            Assert.Equal(0.0, cmd.MoveDirWorld.X, 6);
            Assert.Equal(0.0, cmd.MoveDirWorld.Y, 6);
            Assert.Equal(0.0, cmd.MoveDirWorld.Z, 6);
        }

        /// <summary>The bubble may only ever lower the cap. Raising it would hand a weak hull more
        /// speed than its own braking curve allows, inside the densest geometry on the leg.</summary>
        [Fact]
        public void Bubble_NeverRaisesTheCapOnAWeakHull()
        {
            // 60 m from the node: 10 m to the stop point at 0.85 m/s^2 is a ~4 m/s curve, floored
            // to MinApproachMs at 5. The 10 m/s bubble must not lift that.
            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, Approach(60, 3, 0.85));
            Assert.Equal(5.0, cmd.SpeedCapMs, 6);
        }

        [Fact]
        public void Bubble_IsOffWhenUntuned()
        {
            FlightSample s = Approach(250, 60, 16.89);
            s.TerminalRadiusM = 0;
            s.TerminalSpeedMs = 0;

            FlightCommand cmd = FlightManoeuvre.Tick(ManoeuvreKind.ApproachAndStop, s);
            Assert.True(cmd.SpeedCapMs > BubbleMs,
                "an untuned bubble must restore the pure sqrt profile, got " + cmd.SpeedCapMs);
        }
    }
}
