using System;

namespace Factions.Core
{
    /// <summary>Which manoeuvre a controlled grid is flying. The combat kinds keep a range to a
    /// moving target (<see cref="FlightSample.TargetM"/> and <see cref="FlightSample.TargetVelMs"/>)
    /// rather than coming to rest at a point, and none of them ever reports
    /// <see cref="FlightCommand.Arrived"/>. See docs/fleet-tactics.md.</summary>
    public enum ManoeuvreKind
    {
        ApproachAndStop = 0,

        /// <summary>Get a grid moving off its origin and up to a handoff speed, then give it back to
        /// the cruise autopilot. The bookend of <see cref="ApproachAndStop"/>: one owns the grid
        /// while it accelerates away, the other while it brakes in.</summary>
        Launch = 1,

        /// <summary>Keep <see cref="FlightSample.HoldRangeM"/> from the target, closing or opening
        /// as needed, nose on the target. With <see cref="FlightSample.MinCombatSpeedMs"/> the hold
        /// is a moving orbit inside the range band, not a rest point.</summary>
        HoldAtRange = 2,

        /// <summary>Circle the target at <see cref="FlightSample.HoldRangeM"/>, nose on the target.</summary>
        Orbit = 3,

        /// <summary>Fall back to <see cref="FlightSample.HoldRangeM"/> when the target is inside it,
        /// nose on the target; never closes.</summary>
        Kite = 4,

        /// <summary>Close to <see cref="FlightSample.InnerRangeM"/>, then open to
        /// <see cref="FlightSample.HoldRangeM"/>, repeated. The leg is
        /// <see cref="FlightSample.RunInbound"/>; <see cref="FlightCommand.PhaseDone"/> says it is
        /// over.</summary>
        AttackRun = 5,

        /// <summary>Leave the target at cruise until clear — of every hostile grid by
        /// <see cref="FlightSample.ClearOfHostilesM"/> when that is set, else of the target by
        /// <see cref="FlightSample.HoldRangeM"/> — then report <see cref="FlightCommand.PhaseDone"/>.</summary>
        Disengage = 6,

        /// <summary>Hold the slot <see cref="FlightSample.SlotM"/> on an anchor grid — its position
        /// <see cref="FlightSample.TargetM"/>, velocity <see cref="FlightSample.TargetVelMs"/> and
        /// orientation <see cref="FlightSample.AnchorForward"/>/<see cref="FlightSample.AnchorUp"/> —
        /// velocity-matched, nose along the anchor's. Never reports
        /// <see cref="FlightCommand.Arrived"/>: an escort does not own its fleet's leg.</summary>
        FormUp = 7,
    }

    /// <summary>Everything a manoeuvre needs to know about a grid this tick.
    ///
    /// <para><b>Units: world metres and m/s, not km.</b> Every other position in this codebase is
    /// km (<see cref="Vec3"/> as used by <c>PendingFleetAction.TargetPosKm</c> etc.); flight control
    /// works in metres because the profile constants (stop distance, tolerance) are metres and the
    /// SE-side values the host reads are metres. The host converts once, at the seam.</para></summary>
    public struct FlightSample
    {
        public Vec3 PosM;              // grid position, world metres
        public Vec3 TargetM;           // manoeuvre target, world metres
        public Vec3 VelMs;             // grid linear velocity, world axes, m/s

        /// <summary>Usable braking acceleration. The host derives this from live grid state
        /// (max thrust opposing travel / grid mass), so damage and power loss are reflected.</summary>
        public double DecelMs2;

        /// <summary>Use continuous civilian thrust; the legacy direction command is still emitted.</summary>
        public bool AnalogueThrust;

        public double CruiseMs;        // speed ceiling for this grid
        public double StopDistanceM;   // how far short of TargetM to come to rest

        /// <summary>Resolved rest point, when a clearance pass has chosen one (see
        /// <see cref="ApproachClearance"/>). Overrides the "<see cref="StopDistanceM"/> short of
        /// <see cref="TargetM"/>" rule: the manoeuvre flies to this point with zero further
        /// standoff, while the nose stays on <see cref="TargetM"/>. Null means no clearance pass
        /// ran — the manoeuvre behaves exactly as it did before this field existed.</summary>
        public Vec3? StopPointM;
        public double ArriveToleranceM;
        public double MinApproachMs;   // floor on the commanded speed cap while still approaching
        public double ArriveSpeedMs;   // must also be this slow to count as stopped, not just close

        /// <summary>Speed at which <see cref="ManoeuvreKind.Launch"/> reports
        /// <see cref="FlightCommand.Complete"/> and the grid returns to the cruise autopilot.
        /// Must sit clear of the host's stall threshold: the stall detector force-fails a grid held
        /// below that for its whole timeout, so handing back at or under it would return the grid
        /// straight into the condition the manoeuvre exists to fly it out of.</summary>
        public double LaunchHandoffMs;

        /// <summary>Where the launch engaged, world metres. Only <see cref="ManoeuvreKind.Launch"/>
        /// reads it, and only against <see cref="LaunchClearanceM"/>.</summary>
        public Vec3 LaunchOriginM;

        /// <summary>How far from <see cref="LaunchOriginM"/> a launch must carry the grid before it
        /// may hand back, in metres. Zero disables the condition and restores speed-only handback.
        ///
        /// <para>Speed alone is not enough to leave a station. The cruise autopilot's obstacle
        /// search adopts anything overlapping the grid's own volume sphere, so a grid handed back
        /// while still parked beside station geometry is handed into that condition no matter how
        /// fast it is going; the launch has to carry it out of the neighbourhood, not merely get it
        /// moving. Distance from the origin rather than to the target, because the target may be
        /// 80 km away and the thing being escaped is behind.</para></summary>
        public double LaunchClearanceM;

        /// <summary>Radius around <see cref="TargetM"/> inside which the approach holds a single
        /// fixed speed regardless of how hard the grid could brake. Zero disables the bubble and
        /// restores the pure sqrt profile.
        ///
        /// <para>This is the volume that holds station geometry and parked traffic, so the speed
        /// there is a safety choice rather than a performance one: the clearance pass and any
        /// steer-around term only have time to act if everything crosses it slowly. It is also what
        /// makes a high cruise speed safe to grant — the grid must have shed all of it by the time
        /// it arrives here, which is exactly what <see cref="FlightManoeuvre.CruiseCapMs"/>
        /// budgets for.</para></summary>
        public double TerminalRadiusM;

        /// <summary>Speed cap applied inside <see cref="TerminalRadiusM"/>. Must sit above the
        /// host's stall threshold only while the grid is still under native control, which it is
        /// throughout the bubble — the stall detector exempts a manoeuvring grid.</summary>
        public double TerminalSpeedMs;

        /// <summary>Clearance-resolved departure bearing for <see cref="ManoeuvreKind.Launch"/>,
        /// world axes, unit length. Null means no pass ran and the launch departs straight along
        /// the bearing to the target, as it did before this field existed.
        ///
        /// <para>A launch spawns beside station geometry and the target is frequently on the far
        /// side of it, so "thrust at the destination" and "leave the station" are not the same
        /// vector. The launch flies this one until it is clear of
        /// <see cref="LaunchClearanceM"/>, then turns.</para></summary>
        public Vec3? LaunchDirWorld;

        /// <summary>Bounding sphere of the grid itself, world metres. The avoidance term measures
        /// separation from here rather than from <see cref="PosM"/>, which is the grid's origin
        /// and can sit well off the centre of a long hull. A radius of zero switches avoidance off.</summary>
        public Vec3 SelfCenterM;
        public double SelfRadiusM;

        /// <summary>What the grid must not fly into — the first <see cref="ObstacleCount"/>
        /// entries. The array is the host's and is reused across ticks; entries past the count are
        /// stale. Null or an empty count switches avoidance off. See <see cref="FlightAvoidance"/>.</summary>
        public FlightObstacle[] Obstacles;
        public int ObstacleCount;

        /// <summary>How far ahead the closest-approach test looks, in seconds. Zero switches
        /// avoidance off.</summary>
        public double AvoidLookaheadSec;

        /// <summary>Clearance kept between the two bounding spheres, metres.</summary>
        public double AvoidMarginM;

        /// <summary>The combat target's linear velocity, world axes, m/s. The combat kinds keep
        /// their range in the target's frame, so a moving target is followed rather than left
        /// behind. Zero for a station.</summary>
        public Vec3 TargetVelMs;

        /// <summary>The separation the combat kinds keep from <see cref="TargetM"/>, metres: the
        /// standoff of <see cref="ManoeuvreKind.HoldAtRange"/>, the radius of
        /// <see cref="ManoeuvreKind.Orbit"/>, the floor of <see cref="ManoeuvreKind.Kite"/>, the
        /// outer end of <see cref="ManoeuvreKind.AttackRun"/> and the break range of
        /// <see cref="ManoeuvreKind.Disengage"/>.</summary>
        public double HoldRangeM;

        /// <summary>The inner end of an <see cref="ManoeuvreKind.AttackRun"/>, metres.</summary>
        public double InnerRangeM;

        /// <summary>How far either side of the kept range counts as on station, metres. Inside it
        /// the range term commands nothing.</summary>
        public double RangeToleranceM;

        /// <summary>Tangential speed <see cref="ManoeuvreKind.Orbit"/> asks for, m/s, before the
        /// centripetal cap.</summary>
        public double OrbitSpeedMs;

        /// <summary>Which leg of an <see cref="ManoeuvreKind.AttackRun"/> is being flown: true
        /// closing to <see cref="InnerRangeM"/>, false opening to <see cref="HoldRangeM"/>. Held by
        /// the host and flipped on <see cref="FlightCommand.PhaseDone"/>.</summary>
        public bool RunInbound;

        /// <summary>World up at the grid, unit length, or zero in space. When set, a combat
        /// manoeuvre that moves the grid away from its target climbs at least
        /// <see cref="FlightManoeuvre.MinOpeningClimb"/> while doing so: the line from a surface
        /// target to a grid beside it runs along the ground, and opening the range along it flies
        /// the grid into the terrain.</summary>
        public Vec3 UpWorld;

        /// <summary>The anchor's nose and lift side, world axes, for
        /// <see cref="ManoeuvreKind.FormUp"/>. Need not be unit; see <see cref="Formation.Frame"/>.</summary>
        public Vec3 AnchorForward;
        public Vec3 AnchorUp;

        /// <summary>The slot <see cref="ManoeuvreKind.FormUp"/> holds, in the anchor frame
        /// (X right, Y up, Z aft), metres. See <see cref="Formation.SlotOffset"/>.</summary>
        public Vec3 SlotM;

        /// <summary>The bearing from the target at which <see cref="ManoeuvreKind.HoldAtRange"/>,
        /// <see cref="ManoeuvreKind.Kite"/> and <see cref="ManoeuvreKind.AttackRun"/> keep their
        /// range, world axes, or zero to keep whatever bearing the grid already has. An escort gets
        /// one from <see cref="Formation.SpreadBearing"/> so a squadron's hulls do not converge on
        /// one point of the range shell; the lead has none.</summary>
        public Vec3 SpreadBearing;

        /// <summary>The least speed, relative to the target, a hull keeps while
        /// <see cref="ManoeuvreKind.HoldAtRange"/> or <see cref="ManoeuvreKind.Kite"/> holds its
        /// range, m/s (<see cref="CombatAgility.Motion"/>). Above zero the hold is a moving orbit: it
        /// circles the target inside the range band instead of coming to rest in it; an escort with a
        /// <see cref="SpreadBearing"/> sweeps either side of its bearing instead. Also the least
        /// tangential speed of <see cref="ManoeuvreKind.Orbit"/>. Zero keeps the rest point. Held
        /// under <see cref="FlightManoeuvre.OrbitSpeedCapMs"/> at the kept range and under
        /// <see cref="CruiseMs"/>.</summary>
        public double MinCombatSpeedMs;

        /// <summary>The axis the moving orbit circles, world axes, or zero for world up
        /// (<see cref="UpWorld"/>) in gravity and world Y in space. The host passes the grid's own up
        /// in space, so it circles by strafing on its lateral thrusters with its nose on the target.</summary>
        public Vec3 OrbitAxis;

        /// <summary>Which way round the moving orbit goes about <see cref="OrbitAxis"/>: -1, or +1
        /// for anything else (<see cref="CombatAgility.OrbitSense"/>).</summary>
        public int OrbitSense;

        /// <summary>An evasive burst running this tick (<see cref="Evasion"/>), or
        /// <see cref="EvadeDir.None"/>. While one runs it replaces the tangential term of every
        /// combat kind but <see cref="ManoeuvreKind.Disengage"/>; the range term and the facing are
        /// unchanged.</summary>
        public EvadeDir Evade;

        /// <summary>The burst's speed, m/s, before <see cref="CruiseMs"/> and
        /// <see cref="FlightManoeuvre.BurstReachFraction"/> cap it.</summary>
        public double EvadeSpeedMs;

        /// <summary>What the hull fights for, world metres (<see cref="CombatLeash"/>): its node, its
        /// commander's grid or its station. Read only while <see cref="LeashM"/> is above zero.</summary>
        public Vec3 AnchorM;

        /// <summary>How far from <see cref="AnchorM"/> <see cref="ManoeuvreKind.HoldAtRange"/>,
        /// <see cref="ManoeuvreKind.Orbit"/>, <see cref="ManoeuvreKind.Kite"/> and
        /// <see cref="ManoeuvreKind.AttackRun"/> may take the hull, metres
        /// (<see cref="CombatLeash.RadiusM"/>). Zero for no leash.</summary>
        public double LeashM;

        /// <summary>For <see cref="ManoeuvreKind.Disengage"/>: the separation from every hostile grid
        /// that ends it, metres. Above zero it is done once <see cref="HostileSepM"/> reaches this,
        /// whatever the distance from <see cref="TargetM"/>; zero keeps the
        /// <see cref="HoldRangeM"/>-from-<see cref="TargetM"/> rule.</summary>
        public double ClearOfHostilesM;

        /// <summary>Distance to the nearest hostile grid, metres, or
        /// <see cref="double.PositiveInfinity"/> when none is near. Read only under
        /// <see cref="ClearOfHostilesM"/>.</summary>
        public double HostileSepM;
    }

    /// <summary>What the controller should command this tick. Translation, facing and speed are
    /// independent by design — a combat manoeuvre moves one way while pointing another.</summary>
    public struct FlightCommand
    {
        /// <summary>Unit vector to thrust along, in world axes. <b>Zero means brake and hold</b> —
        /// SE's AI-thrust path dampens the entire velocity vector when commanded a zero direction,
        /// which is the strongest braking available on the legacy path. Combat may command reverse
        /// thrust directly. Analogue civilian control reads DesiredVelocityMs instead.</summary>
        public Vec3 MoveDirWorld;

        /// <summary>Civilian velocity target for analogue thrust, world m/s. Null uses the legacy
        /// direction-only path. A zero target requests a stop; it is not a coasting command.</summary>
        public Vec3? DesiredVelocityMs;

        /// <summary>World point to aim the nose at, or null to leave facing alone.</summary>
        public Vec3? FaceTargetWorld;

        /// <summary>The speed this manoeuvre wants right now. Advisory: SE's AI-thrust path takes a
        /// direction, not a speed, so <see cref="MoveDirWorld"/> already encodes the accelerate /
        /// brake decision. Carried for logging and for the cruise-phase autopilot speed limit.</summary>
        public double SpeedCapMs;

        /// <summary>At rest at the manoeuvre's goal. Read by the host as <b>leg arrival</b>, so only
        /// a manoeuvre that genuinely ends the leg may set it — <see cref="ManoeuvreKind.Launch"/>
        /// reports <see cref="Complete"/> instead.</summary>
        public bool Arrived;

        /// <summary>This manoeuvre has done its job and the grid should be handed back. Distinct
        /// from <see cref="Arrived"/>, which additionally asserts the leg is over.</summary>
        public bool Complete;

        /// <summary>A combat manoeuvre has finished its current leg: an
        /// <see cref="ManoeuvreKind.AttackRun"/> reached the end it was flying to, or a
        /// <see cref="ManoeuvreKind.Disengage"/> is clear. Neither hands the grid back nor ends the
        /// leg — the host decides what follows.</summary>
        public bool PhaseDone;

        /// <summary>What the avoidance term found this tick. <see cref="FlightAvoidance.Assessment.WorstIndex"/>
        /// is -1 when nothing threatened, in which case <see cref="MoveDirWorld"/> is exactly what the
        /// manoeuvre asked for.</summary>
        public FlightAvoidance.Assessment Avoid;
    }

    /// <summary>Pure manoeuvre geometry and the deceleration profile, kept SE-free in Core so the
    /// sim, the unit tests and the live host run identical flight rules — the same split
    /// <see cref="ThrustPhysics"/> uses for the thrust-effectiveness curve.</summary>
    /// <summary>Why <see cref="FlightManoeuvre.LaunchGate"/> declined a grid.</summary>
    public enum LaunchRefusal { None, DegenerateBand, InsideApproachHorizon, TooFast }

    /// <summary>The largest force a grid's thrusters can apply along each grid-local direction,
    /// newtons, named by the direction the force pushes the grid.</summary>
    public struct AxisThrust
    {
        public double UpN, DownN, LeftN, RightN, ForwardN, BackwardN;
    }

    public static class FlightManoeuvre
    {
        /// <summary>Classic v²/2a. <see cref="double.PositiveInfinity"/> when there is no usable
        /// braking thrust at all — such a grid can never stop, and callers must clamp.</summary>
        public static double StoppingDistanceM(double speedMs, double decelMs2)
        {
            if (speedMs <= 0) return 0;
            if (decelMs2 <= 0) return double.PositiveInfinity;
            return speedMs * speedMs / (2.0 * decelMs2);
        }

        /// <summary>Distance from the target at which the controller must take over from the cruise
        /// autopilot — the "predictive horizon". Derived per grid rather than fixed: at 100 m/s a
        /// 0.5 m/s² hauler needs ~10 km to stop while a 2 m/s² one needs ~2.5 km, so any single
        /// constant is wrong for one of them. Sized off the faster of cruise and current speed so
        /// the horizon doesn't shrink while the grid is still accelerating up to cruise.</summary>
        public static double HorizonM(FlightSample s, double margin, double maxHorizonM)
        {
            double speed = Length(s.VelMs);
            if (s.CruiseMs > speed) speed = s.CruiseMs;

            double stopping = StoppingDistanceM(speed, s.DecelMs2);
            if (double.IsInfinity(stopping) || double.IsNaN(stopping)) return maxHorizonM;

            double horizon = stopping * margin + s.StopDistanceM;
            if (horizon > maxHorizonM) return maxHorizonM;
            return horizon;
        }

        /// <summary>How much distance a leg may actually spend braking: the deepest horizon the
        /// host can observe, discounted by the same margin the horizon carries, less the terminal
        /// bubble the grid must already be slow for.
        ///
        /// <para>The horizon cap is not a formality. A grid outside the observation radius is not
        /// being flown by the controller at all, so braking distance the profile "plans" out there
        /// is distance it never gets — which is why cruise speed has to be budgeted against this
        /// number rather than against the grid's thrust alone.</para></summary>
        public static double BrakeBudgetM(double maxHorizonM, double horizonMargin, double terminalRadiusM)
        {
            if (maxHorizonM <= 0) return 0;
            double usable = horizonMargin > 0 ? maxHorizonM / horizonMargin : maxHorizonM;
            double budget = usable - (terminalRadiusM > 0 ? terminalRadiusM : 0);
            return budget > 0 ? budget : 0;
        }

        /// <summary>The fastest a grid may be allowed to cruise on this leg: the speed it can shed
        /// down to <paramref name="terminalSpeedMs"/> within <paramref name="brakeBudgetM"/>, from
        /// the braking authority it is planned against.
        ///
        /// <para>Inverse of the profile's own <c>sqrt(2ad)</c> curve, which is what makes the two
        /// consistent: a grid granted this speed reaches the bubble at exactly the bubble speed
        /// rather than arriving with a curve it cannot complete. A weak hauler is held slow and a
        /// high-thrust hull is let run, instead of one constant being wrong for both.</para>
        ///
        /// <para><paramref name="decelMs2"/> must be the <see cref="PlanningDecelMs2"/> figure, not
        /// a raw thrust estimate: this function grants speed in proportion to it, so an estimate
        /// that over-reports buys a grid speed it cannot shed.</para></summary>
        public static double CruiseCapMs(double decelMs2, double brakeBudgetM, double terminalSpeedMs,
                                         double floorMs, double ceilingMs)
        {
            double floor   = floorMs   > 0 ? floorMs   : 0;
            double ceiling = ceilingMs > 0 ? ceilingMs : 0;
            if (ceiling <= 0) return 0;
            if (floor > ceiling) floor = ceiling;

            if (decelMs2 <= 0 || brakeBudgetM <= 0) return floor;

            double terminal = terminalSpeedMs > 0 ? terminalSpeedMs : 0;
            double v = Math.Sqrt(terminal * terminal + 2.0 * decelMs2 * brakeBudgetM);

            if (v < floor)   return floor;
            if (v > ceiling) return ceiling;
            return v;
        }

        /// <summary>Deceleration a grid actually produced between two speed samples. Negative when
        /// it sped up. Only meaningful while a full brake was commanded — under the profile's
        /// bang-bang rule that is precisely the ticks where <see cref="FlightCommand.MoveDirWorld"/>
        /// came back zero because speed was over the cap.</summary>
        public static double AchievedDecelMs2(double dtSec, double speedBeforeMs, double speedAfterMs)
        {
            if (dtSec <= 0) return 0;
            return (speedBeforeMs - speedAfterMs) / dtSec;
        }

        /// <summary>The braking sample a closed run yields, or 0 when the run measured nothing
        /// worth keeping. The host owns the run — when it opened and at what speed — and calls
        /// this once the window has elapsed; every rule about whether the run counts lives here.
        ///
        /// <para>A run that <b>ends at or under arrive speed</b> measured the stop, not the brake.
        /// Once the grid is at rest inside its arrival envelope the profile keeps commanding a zero
        /// vector, so runs keep closing for as long as the grid is held there — and each one reads
        /// either physics jitter or the last metre of the settle, in which the grid reaches rest
        /// partway through the window and the remainder reads no deceleration at all. Both are
        /// biased low; neither is authority. Measured live: 109 samples on a grid held 221 s at its
        /// rest point, and its best reading was the settle from 2 m/s, not the approach.</para>
        ///
        /// <para>A run <b>above the thrust estimate</b> is impossible — gravity is already projected
        /// into that figure — and therefore noise. It is discarded rather than clamped so it cannot
        /// become the record's maximum by another route.</para></summary>
        public static double BrakeSampleMs2(double dtSec, double speedBeforeMs, double speedAfterMs,
                                            double estimateMs2, double arriveSpeedMs)
        {
            if (speedAfterMs <= arriveSpeedMs) return 0;

            double achieved = AchievedDecelMs2(dtSec, speedBeforeMs, speedAfterMs);
            if (achieved <= 0) return 0;
            if (estimateMs2 > 0 && achieved > estimateMs2) return 0;
            return achieved;
        }

        /// <summary>Braking authority to plan against: the thrust-derived estimate, held down to
        /// what the grid has actually been observed to deliver once there is an observation.
        ///
        /// <para>The estimate alone is not safe to grant speed from. It reads a grid's opposing
        /// thrust, which is an upper bound on what SE's dampener will apply and on some hulls is a
        /// wild one. Taking the minimum means a brake that does not bite lowers the speed the grid
        /// is given, instead of the estimate silently authorising more of it.</para>
        ///
        /// <para>This deliberately never raises the estimate: <paramref name="observedMs2"/> can
        /// exceed it (gravity helping, a lighter grid after unloading) and letting that through
        /// would plan against a one-off. <paramref name="slack"/> above 1.0 plans optimistically and
        /// is not what covers a sample taken mid-attitude-swing — keeping the grid's best sample
        /// rather than its mean is.</para></summary>
        public static double PlanningDecelMs2(double estimateMs2, double observedMs2,
                                              bool hasObservation, double slack)
        {
            if (estimateMs2 < 0) estimateMs2 = 0;
            if (!hasObservation) return estimateMs2;

            double allowed = observedMs2 * (slack > 0 ? slack : 1.0);
            if (allowed <= 0) return 0;
            return estimateMs2 < allowed ? estimateMs2 : allowed;
        }

        /// <summary>Whether a grid should be taken onto <see cref="ManoeuvreKind.Launch"/> this tick.
        ///
        /// <para><b>The two speeds must not be the same number.</b> A launch ends at
        /// <paramref name="handoffSpeedMs"/> and the handback releases the grid; if it could re-engage
        /// at that same speed the two conditions chatter against each other every frame, and the grid
        /// is held at the threshold instead of accelerating to cruise.
        /// <see cref="ShouldBeginLaunch"/> and <see cref="FlightCommand.Complete"/> are therefore
        /// separated by the whole band between <paramref name="engageSpeedMs"/> and
        /// <paramref name="handoffSpeedMs"/>: a released grid has to lose all of it before this can
        /// fire again.</para>
        ///
        /// <para>Engaging is for a grid that has not got going — near rest — not merely one below
        /// handoff speed. A grid already under way is the cruise autopilot's, and one close enough for
        /// the approach profile is <see cref="ApproachAndStop"/>'s.</para></summary>
        public static bool ShouldBeginLaunch(double speedMs, bool withinApproachHorizon,
                                             double engageSpeedMs, double handoffSpeedMs)
        {
            return LaunchGate(speedMs, withinApproachHorizon, true, true,
                              engageSpeedMs, engageSpeedMs, handoffSpeedMs) == LaunchRefusal.None;
        }

        /// <summary>The launch gate with its reason, for a grid in either of the two states a launch
        /// meets.
        ///
        /// <para><b>A grid no launch has held yet</b> engages below
        /// <paramref name="firstEngageSpeedMs"/>, not <paramref name="engageSpeedMs"/>. The low engage
        /// speed exists only to stop a handback re-engaging, and a fresh grid has had no handback: at
        /// 2 m/s, a grid already creeping or settling when first asked was never launched, stayed
        /// under the 8.33 m/s stall speed, and was destroyed 15 s after spawning (FAC-337). The limit
        /// never reaches <paramref name="handoffSpeedMs"/>, so a launch still cannot engage at a speed
        /// where it would report Complete.</para>
        ///
        /// <para><b>The approach horizon only refuses when an approach will take the grid.</b> On a
        /// fly-through navigation leg the observation loop never asks for one, so a launch refused
        /// there left the grid flown by neither.</para></summary>
        public static LaunchRefusal LaunchGate(double speedMs, bool withinApproachHorizon, bool approachFollows,
                                               bool launchedBefore, double engageSpeedMs,
                                               double firstEngageSpeedMs, double handoffSpeedMs)
        {
            if (engageSpeedMs >= handoffSpeedMs) return LaunchRefusal.DegenerateBand;
            if (withinApproachHorizon && approachFollows) return LaunchRefusal.InsideApproachHorizon;
            return speedMs < EngageLimitMs(launchedBefore, engageSpeedMs, firstEngageSpeedMs, handoffSpeedMs)
                ? LaunchRefusal.None
                : LaunchRefusal.TooFast;
        }

        /// <summary>The speed below which <see cref="LaunchGate"/> may engage; see there.</summary>
        public static double EngageLimitMs(bool launchedBefore, double engageSpeedMs,
                                           double firstEngageSpeedMs, double handoffSpeedMs)
        {
            if (launchedBefore) return engageSpeedMs;
            double first = firstEngageSpeedMs > engageSpeedMs ? firstEngageSpeedMs : engageSpeedMs;
            return first < handoffSpeedMs ? first : handoffSpeedMs;
        }

        /// <summary>The braking acceleration to plan against: what the thrusters deliver along the
        /// brake axis, plus gravity's own signed contribution along it, with the safety factor applied
        /// to the <b>result</b>.
        ///
        /// <para>Derating the thrust while charging gravity in full is not conservatism, it is a
        /// different quantity: a descent at 1 g reads zero or less for any hull under
        /// <c>1/safety</c> lift TWR, and the controller then hands back a grid that brakes perfectly
        /// well. Live 2026-09-17, a 3,568 t <c>Naginata</c> (2.39) and an 856 t <c>Alsvidr</c> (2.25)
        /// aborted every descent into the operator's ground station, eight times in 0.7 s on one
        /// (FAC-338). Derating the margin keeps <see cref="FactionTuning.ApproachDecelSafetyFactor"/>
        /// doing the job it was measured for — a hull delivers less than its block maximum — without
        /// inventing a hull that cannot brake at all.</para>
        ///
        /// <para>Zero in zero gravity is unchanged: with no gravity term the two forms are the same
        /// number. A hull that genuinely cannot out-thrust gravity along the brake axis still reads
        /// 0, which is the reading that means "no usable approach".</para></summary>
        /// <param name="thrustAccelMs2">Thrust along the brake axis over mass, undersated.</param>
        /// <param name="gravityAlongBrakeMs2">Gravity projected on the brake axis: negative when it
        /// opposes braking (a descent), positive when it helps (a climb).</param>
        public static double BrakingDecelMs2(double thrustAccelMs2, double gravityAlongBrakeMs2,
                                             double safetyFactor)
        {
            if (thrustAccelMs2 <= 0) return 0;
            double net = thrustAccelMs2 + gravityAlongBrakeMs2;
            if (net <= 0) return 0;
            double safety = safetyFactor > 0 ? safetyFactor : 1.0;
            return net * safety;
        }

        /// <summary>Whether a controlled grid whose planning deceleration has gone to zero must be
        /// handed back. Only an approach plans against braking; without it the profile would hold the
        /// grid at the crawl floor. A launch never brakes, and in gravity the estimate reads zero for
        /// any hull moving downward on an axis under 2.5 TWR, because the thrust side is derated and
        /// gravity is not — so a launch aborted on it drops a hull that is still climbing out.</summary>
        public static bool AbortsOnLostBraking(ManoeuvreKind kind, double planningDecelMs2)
        {
            return kind != ManoeuvreKind.Launch && planningDecelMs2 <= 0;
        }

        /// <summary>Whether an approach must not take a grid in gravity because the grid cannot hold
        /// itself up in either attitude it may fly: the one it has now, or the one the manoeuvre
        /// levels it into. Below that line every descent reads zero braking
        /// (<see cref="BrakingDecelMs2"/>), so an engaged approach is aborted as soon as the
        /// released dampeners let the grid sink, and the grid is engaged again once they have
        /// stopped it. A launch is never refused here; it is checked per tick on
        /// <see cref="LaunchCannotHoldAltitude"/>.</summary>
        public static bool ApproachCannotHoldAltitude(ManoeuvreKind kind, double liftNowTwr,
                                                      double liftHeldTwr, double minLiftTwr)
        {
            if (kind == ManoeuvreKind.Launch) return false;
            double best = liftNowTwr > liftHeldTwr ? liftNowTwr : liftHeldTwr;
            return LaunchCannotHoldAltitude(best, minLiftTwr);
        }

        /// <summary>Thrust-to-weight the grid can apply straight up in its current attitude.
        /// <paramref name="upLocal"/> is world up in grid-local coordinates (X right, Y up, Z backward).
        /// The best a grid can push along a direction is each local axis's thrust on the side facing
        /// it, weighted by how much of the direction lies along that axis. Positive infinity with no
        /// weight to hold.</summary>
        public static double LiftTwr(Vec3 upLocal, AxisThrust thrust, double massKg, double gravityMs2)
        {
            if (massKg <= 0 || gravityMs2 <= 1e-6) return double.PositiveInfinity;
            double len = Length(upLocal);
            if (len < 1e-9) return double.PositiveInfinity;
            Vec3 u = Scale(upLocal, 1.0 / len);

            double forceN = Math.Abs(u.X) * (u.X >= 0 ? thrust.RightN : thrust.LeftN)
                          + Math.Abs(u.Y) * (u.Y >= 0 ? thrust.UpN : thrust.DownN)
                          + Math.Abs(u.Z) * (u.Z >= 0 ? thrust.BackwardN : thrust.ForwardN);
            return forceN / (massKg * gravityMs2);
        }

        /// <summary>Whether a launch should hand its grid back because the attitude it is holding
        /// cannot keep it up. Never in zero gravity, and never with the threshold switched off.</summary>
        public static bool LaunchCannotHoldAltitude(double liftTwr, double abortLiftTwr)
        {
            return abortLiftTwr > 0 && liftTwr < abortLiftTwr;
        }

        /// <summary>The manoeuvre's own command, then the avoidance term composed into its
        /// <see cref="FlightCommand.MoveDirWorld"/> — one path for every manoeuvre, so a new one
        /// inherits avoidance without doing anything. Facing, speed cap and the arrival and handback
        /// flags are the manoeuvre's and are left alone.</summary>
        public static FlightCommand Tick(ManoeuvreKind kind, FlightSample s)
        {
            FlightCommand cmd;
            Vec3? goal;
            switch (kind)
            {
                case ManoeuvreKind.Launch:
                    cmd = Launch(s);
                    goal = null;
                    break;
                case ManoeuvreKind.HoldAtRange:
                    cmd = HoldAtRange(s);
                    goal = RangePoint(s, s.HoldRangeM);
                    break;
                case ManoeuvreKind.Orbit:
                    cmd = Orbit(s);
                    goal = null;
                    break;
                case ManoeuvreKind.Kite:
                    cmd = Kite(s);
                    goal = RangePoint(s, s.HoldRangeM);
                    break;
                case ManoeuvreKind.AttackRun:
                    cmd = AttackRun(s);
                    goal = RangePoint(s, s.RunInbound ? s.InnerRangeM : s.HoldRangeM);
                    break;
                case ManoeuvreKind.Disengage:
                    cmd = Disengage(s);
                    goal = null;
                    break;
                case ManoeuvreKind.FormUp:
                    cmd = FormUp(s);
                    goal = SlotWorld(s);
                    break;
                case ManoeuvreKind.ApproachAndStop:
                default:
                    cmd = ApproachAndStop(s);
                    goal = RestPoint(s);
                    break;
            }

            cmd.Avoid = FlightAvoidance.Assess(s, goal);
            cmd.MoveDirWorld = FlightAvoidance.Compose(cmd.MoveDirWorld, cmd.Avoid);
            if (cmd.DesiredVelocityMs.HasValue)
            {
                Vec3 want = cmd.DesiredVelocityMs.Value;
                double speed = Length(want);
                Vec3 steered = FlightAvoidance.Compose(Direction(want), cmd.Avoid);
                // An urgent avoidance steer may move a stopped hull. Give it only the terminal
                // speed, rather than converting a faint threat into a full-authority burn.
                if (speed <= 1e-6 && Length(steered) > 1e-6)
                    speed = Math.Min(s.CruiseMs, s.TerminalSpeedMs > 0 ? s.TerminalSpeedMs : s.MinApproachMs);
                cmd.DesiredVelocityMs = Scale(steered, speed);
            }
            return cmd;
        }

        /// <summary>Where <see cref="ApproachAndStop"/> brings the grid to rest: the clearance-resolved
        /// stop point, or <see cref="FlightSample.StopDistanceM"/> short of the target along the
        /// current approach axis.</summary>
        private static Vec3 RestPoint(FlightSample s)
        {
            if (s.StopPointM.HasValue) return s.StopPointM.Value;
            Vec3 fromTarget = Sub(s.PosM, s.TargetM);
            double len = Length(fromTarget);
            if (len <= s.StopDistanceM || len <= 1e-6) return s.PosM;
            return Add(s.TargetM, Scale(fromTarget, s.StopDistanceM / len));
        }

        /// <summary>Fly to the rest point — <see cref="FlightSample.StopPointM"/> when a clearance
        /// pass resolved one, else <see cref="FlightSample.StopDistanceM"/> short of the target —
        /// and come to rest there, shedding speed on a sqrt curve so the grid is already slow by
        /// the time it reaches station geometry.</summary>
        private static FlightCommand ApproachAndStop(FlightSample s)
        {
            var cmd = new FlightCommand();
            if (s.AnalogueThrust) cmd.DesiredVelocityMs = new Vec3(0, 0, 0);

            // A clearance-resolved stop point already embeds its standoff from the target, so it
            // is flown to directly; otherwise the rest point is StopDistanceM short of the target
            // along the current approach axis, as always.
            bool hasStopPoint = s.StopPointM.HasValue;
            Vec3 goal = hasStopPoint ? s.StopPointM.Value : s.TargetM;

            Vec3 toGoal = Sub(goal, s.PosM);
            double dist = Length(toGoal);

            double speed = Length(s.VelMs);

            // Degenerate: sitting exactly on the rest point. Hold, don't divide by zero.
            if (dist <= 1e-6)
            {
                cmd.MoveDirWorld = new Vec3(0, 0, 0);
                cmd.SpeedCapMs = 0;
                cmd.Arrived = speed <= s.ArriveSpeedMs;
                return cmd;
            }

            // The nose stays on the station regardless of where the parking spot is.
            cmd.FaceTargetWorld = s.TargetM;

            // Signed: negative once the grid is inside the stop radius (overshot, or spawned close).
            double distToStop = hasStopPoint ? dist : dist - s.StopDistanceM;

            // Close enough to stop thrusting — but "arrived" also requires actually being slow.
            // Reporting arrival while still doing 50 m/s would call a fly-through a landing.
            if (distToStop <= s.ArriveToleranceM)
            {
                cmd.MoveDirWorld = new Vec3(0, 0, 0);   // brake and hold
                cmd.SpeedCapMs = 0;
                cmd.Arrived = speed <= s.ArriveSpeedMs;
                return cmd;
            }

            double cap = Math.Sqrt(2.0 * s.DecelMs2 * distToStop);
            if (cap < s.MinApproachMs) cap = s.MinApproachMs;
            if (cap > s.CruiseMs) cap = s.CruiseMs;

            // Terminal bubble: a flat ceiling near the node, measured from the node itself rather
            // than from the clearance-adjusted rest point, because what it is protecting is the
            // volume the station and its parked traffic occupy. Only ever lowers the cap.
            if (s.TerminalRadiusM > 0 && s.TerminalSpeedMs > 0
                && Length(Sub(s.TargetM, s.PosM)) <= s.TerminalRadiusM
                && cap > s.TerminalSpeedMs)
                cap = s.TerminalSpeedMs;

            if (s.AnalogueThrust)
            {
                // Leave room for the velocity loop's response, including the final settle. The
                // legacy sqrt ceiling alone assumes instant full braking at the cap.
                cap = CivilianThrust.ApproachSpeedMs(distToStop, s.DecelMs2, cap);
                cmd.DesiredVelocityMs = Scale(toGoal, cap / dist);
            }
            cmd.SpeedCapMs = cap;

            // Bang-bang against the cap: over it, command zero so SE's AI-thrust dampener brakes at
            // full available thrust; under it, thrust along the approach axis.
            cmd.MoveDirWorld = speed > cap
                ? new Vec3(0, 0, 0)
                : Scale(toGoal, 1.0 / dist);

            return cmd;
        }

        /// <summary>Accelerate off the origin along the bearing to the target until the grid is
        /// making <see cref="FlightSample.LaunchHandoffMs"/>, then report
        /// <see cref="FlightCommand.Complete"/> so the caller hands it back to the cruise autopilot.
        ///
        /// <para>Deliberately never sets <see cref="FlightCommand.Arrived"/>: that is read as leg
        /// arrival, and a launch happens at the leg's start.</para></summary>
        private static FlightCommand Launch(FlightSample s)
        {
            var cmd = new FlightCommand();
            if (s.AnalogueThrust) cmd.DesiredVelocityMs = new Vec3(0, 0, 0);

            double speed = Length(s.VelMs);

            Vec3 toTarget = Sub(s.TargetM, s.PosM);
            double dist = Length(toTarget);

            // Nowhere to launch toward — a zero-length leg. Hold, and report done so the grid is not
            // held off its cruise autopilot waiting for a speed it has no reason to reach.
            if (dist <= 1e-6)
            {
                cmd.MoveDirWorld = new Vec3(0, 0, 0);
                cmd.SpeedCapMs = 0;
                cmd.Complete = true;
                return cmd;
            }

            // Up to speed AND out of the origin's neighbourhood: stop thrusting and let the caller
            // return the grid to cruise. Commanding zero here would brake it, so the direction is
            // held until the handback actually happens.
            //
            // Two conditions override the clearance requirement, because holding on past either
            // would be worse than handing back early. At cruise there is nothing further to gain
            // from the launch profile and holding would push the grid past the leg's speed limit.
            // On a leg shorter than the clearance radius, clearance could only be reached by
            // overshooting the destination.
            bool upToSpeed = speed >= s.LaunchHandoffMs;
            bool cleared   = s.LaunchClearanceM <= 0
                             || Length(Sub(s.PosM, s.LaunchOriginM)) >= s.LaunchClearanceM;
            bool atCruise  = s.CruiseMs > 0 && speed >= s.CruiseMs;
            bool shortLeg  = dist <= s.LaunchClearanceM;

            if (upToSpeed && (cleared || atCruise || shortLeg))
            {
                cmd.Complete = true;
                cmd.SpeedCapMs = s.CruiseMs;
                cmd.FaceTargetWorld = s.TargetM;
                cmd.MoveDirWorld = BearingTo(s);
                if (s.AnalogueThrust) cmd.DesiredVelocityMs = Scale(cmd.MoveDirWorld, s.CruiseMs);
                return cmd;
            }

            // Still departing. A clearance pass may have chosen a bearing that is not the bearing
            // to the target — the station the grid is parked against frequently sits between the
            // two — and the nose goes with it, because thrust authority is highest along it. In
            // gravity the host holds that heading level (FlightAttitude).
            Vec3 depart = s.LaunchDirWorld.HasValue && Length(s.LaunchDirWorld.Value) > 1e-6
                ? Direction(s.LaunchDirWorld.Value)
                : Scale(toTarget, 1.0 / dist);

            cmd.FaceTargetWorld = Add(s.PosM, Scale(depart, dist));
            cmd.SpeedCapMs = s.CruiseMs;
            cmd.MoveDirWorld = depart;
            if (s.AnalogueThrust) cmd.DesiredVelocityMs = Scale(depart, s.CruiseMs);
            return cmd;
        }

        /// <summary>True for the kinds that keep a range to a target instead of flying to a point.
        /// None of them reports <see cref="FlightCommand.Arrived"/>.</summary>
        public static bool IsCombat(ManoeuvreKind kind)
        {
            return kind == ManoeuvreKind.HoldAtRange || kind == ManoeuvreKind.Orbit
                || kind == ManoeuvreKind.Kite || kind == ManoeuvreKind.AttackRun
                || kind == ManoeuvreKind.Disengage;
        }

        /// <summary>The fastest a grid may close on (or open from) a range shell
        /// <paramref name="gapM"/> away and still come to rest on it: the inverse of
        /// <see cref="StoppingDistanceM"/>, capped at <paramref name="cruiseMs"/>. The one rule every
        /// combat manoeuvre's range term obeys, so none of them can carry a grid through the range
        /// it chose.</summary>
        public static double RangeSpeedCapMs(double gapM, double decelMs2, double cruiseMs)
        {
            if (gapM <= 0 || decelMs2 <= 0) return 0;
            double cap = Math.Sqrt(2.0 * decelMs2 * gapM);
            return cruiseMs > 0 && cap > cruiseMs ? cruiseMs : cap;
        }

        /// <summary>The fastest a grid can circle at <paramref name="radiusM"/> on the braking
        /// authority it has: centripetal acceleration v²/r may not exceed it.</summary>
        public static double OrbitSpeedCapMs(double radiusM, double decelMs2)
        {
            if (radiusM <= 0 || decelMs2 <= 0) return 0;
            return Math.Sqrt(decelMs2 * radiusM);
        }

        /// <summary>Sine of the least climb a combat manoeuvre holds while opening the range in
        /// gravity: 30 degrees above the horizon.</summary>
        public const double MinOpeningClimb = 0.5;

        /// <summary><paramref name="away"/>, raised to climb at least <see cref="MinOpeningClimb"/>
        /// when <paramref name="up"/> is set. Unchanged in space, and unchanged when it already
        /// climbs that steeply.</summary>
        public static Vec3 ClimbingAway(Vec3 away, Vec3 up)
        {
            double upLen = Length(up);
            if (upLen < 1e-6) return away;
            Vec3 u = Scale(up, 1.0 / upLen);
            double rise = Dot(away, u);
            if (rise >= MinOpeningClimb) return away;

            Vec3 level = Sub(away, Scale(u, rise));
            double levelLen = Length(level);
            if (levelLen < 1e-6) return u;   // straight down: go straight up
            double cos = Math.Sqrt(1.0 - MinOpeningClimb * MinOpeningClimb);
            return Add(Scale(level, cos / levelLen), Scale(u, MinOpeningClimb));
        }

        /// <summary>The point <paramref name="rangeM"/> from the target on the grid's side of it.</summary>
        private static Vec3 RangePoint(FlightSample s, double rangeM)
        {
            return Add(s.TargetM, Scale(AwayFromTarget(s), rangeM > 0 ? rangeM : 0));
        }

        /// <summary>Unit vector from the target to the grid. A grid sitting on the target has no
        /// such bearing; a fixed axis stands in so the result is deterministic.</summary>
        private static Vec3 AwayFromTarget(FlightSample s)
        {
            Vec3 from = Sub(s.PosM, s.TargetM);
            double len = Length(from);
            return len > 1e-6 ? Scale(from, 1.0 / len) : new Vec3(1, 0, 0);
        }

        /// <summary>The velocity, world frame, that keeps <paramref name="rangeM"/> from the target:
        /// the target's own velocity plus a radial term toward the range shell and an optional
        /// tangential one. The radial term is capped by <see cref="RangeSpeedCapMs"/> on the
        /// distance to the shell, and is zero inside <see cref="FlightSample.RangeToleranceM"/>.
        /// The whole is capped at <see cref="FlightSample.CruiseMs"/>.</summary>
        private static Vec3 RangeKeepingVelocity(FlightSample s, double rangeM, bool mayClose, bool mayOpen,
                                                 Vec3 tangentialMs)
        {
            Vec3 away = AwayFromTarget(s);
            double dist = Length(Sub(s.PosM, s.TargetM));
            double tol = s.RangeToleranceM > 0 ? s.RangeToleranceM : 0;
            double error = dist - rangeM;   // positive: too far out

            double radial = 0;
            if (error > tol && mayClose)
                radial = -RangeSpeedCapMs(error, s.DecelMs2, s.CruiseMs);
            else if (error < -tol && mayOpen)
                radial = RangeSpeedCapMs(-error, s.DecelMs2, s.CruiseMs);

            Vec3 radialDir = radial > 0 ? ClimbingAway(away, s.UpWorld) : away;
            Vec3 rel = Add(Scale(radialDir, radial), tangentialMs);

            // Opening in gravity climbs as a whole, not only its radial part: a tangential term run
            // level beside a surface target would otherwise flatten the climb.
            if (radial > 0)
            {
                double relLen = Length(rel);
                if (relLen > 1e-6) rel = Scale(ClimbingAway(Scale(rel, 1.0 / relLen), s.UpWorld), relLen);
            }
            Vec3 want = Add(s.TargetVelMs, rel);
            double len = Length(want);
            if (s.CruiseMs > 0 && len > s.CruiseMs) want = Scale(want, s.CruiseMs / len);
            return want;
        }

        /// <summary>Turns a wanted velocity into a thrust direction under the controller's
        /// bang-bang rule. At rest is wanted: command zero, SE's full dampening. Otherwise thrust
        /// along the wanted velocity while the grid's speed along it is under the wanted speed, and
        /// against it once over — SE dampens whatever lies across the commanded direction either
        /// way, which removes the part of the grid's velocity the wanted one does not have.</summary>
        private static FlightCommand Pursue(FlightSample s, Vec3 wantMs)
        {
            var cmd = new FlightCommand();
            double wantLen = Length(wantMs);
            double rest = s.ArriveSpeedMs > 0 ? s.ArriveSpeedMs : 0;
            cmd.SpeedCapMs = wantLen;

            if (wantLen <= rest || wantLen <= 1e-6)
            {
                cmd.MoveDirWorld = new Vec3(0, 0, 0);
                return cmd;
            }

            Vec3 dir = Scale(wantMs, 1.0 / wantLen);
            double along = Dot(s.VelMs, dir);
            cmd.MoveDirWorld = along > wantLen ? Scale(dir, -1.0) : dir;
            return cmd;
        }

        /// <summary>Keep <see cref="FlightSample.HoldRangeM"/> from the target, nose on it — at rest
        /// in the band, or circling inside it at <see cref="FlightSample.MinCombatSpeedMs"/>.</summary>
        private static FlightCommand HoldAtRange(FlightSample s)
        {
            FlightCommand cmd = Pursue(s, Leashed(s, RangeKeepingVelocity(s, s.HoldRangeM, true, true, HoldingTangential(s))));
            cmd.FaceTargetWorld = s.TargetM;
            return cmd;
        }

        /// <summary>No burst runs faster than the hull can shed within this share of the range it
        /// holds, so a break never carries it through its own range band.</summary>
        public const double BurstReachFraction = 0.25;

        /// <summary>An escort's sweep either side of its bearing is this many range tolerances, and
        /// at least <see cref="MinSweepM"/>.</summary>
        public const double SweepTolerances = 3.0;
        public const double MinSweepM = 50.0;

        /// <summary>The least speed a hull keeps round its target at the range it holds:
        /// <see cref="FlightSample.MinCombatSpeedMs"/> under <see cref="OrbitSpeedCapMs"/> at that range
        /// and under cruise.</summary>
        public static double OrbitFloorMs(FlightSample s)
        {
            double floor = s.MinCombatSpeedMs > 0 ? s.MinCombatSpeedMs : 0;
            if (floor <= 0) return 0;
            double cap = OrbitSpeedCapMs(s.HoldRangeM, s.DecelMs2);
            if (floor > cap) floor = cap;
            if (s.CruiseMs > 0 && floor > s.CruiseMs) floor = s.CruiseMs;
            return floor;
        }

        /// <summary>The tangential term of a range-holding kind: the burst while one runs, else the
        /// moving orbit when the hull has a floor, else the pull to its spread bearing.</summary>
        private static Vec3 HoldingTangential(FlightSample s)
        {
            if (s.Evade != EvadeDir.None) return EvadeVelocity(s);
            return OrbitFloorMs(s) > 0 ? MovingOrbitVelocity(s) : SpreadVelocity(s);
        }

        /// <summary>The moving orbit's tangential velocity at <see cref="OrbitFloorMs"/>. A hull with
        /// no spread bearing circles the target about <see cref="FlightSample.OrbitAxis"/> in its
        /// <see cref="FlightSample.OrbitSense"/>. One with a bearing sweeps either side of it: outside
        /// the sweep it heads back toward the bearing, no slower than the floor; inside it keeps
        /// going the way it is already moving round the target, so it passes through and out the
        /// other side rather than stopping on the bearing.</summary>
        private static Vec3 MovingOrbitVelocity(FlightSample s)
        {
            double speed = OrbitFloorMs(s);
            Vec3 away = AwayFromTarget(s);
            Vec3 bearing = Direction(s.SpreadBearing);
            if (Length(bearing) < 1e-6) return Scale(OrbitTangent(s, away), speed);

            double cos = Dot(away, bearing);
            if (cos > 1) cos = 1;
            if (cos < -1) cos = -1;
            double arc = Math.Acos(cos) * Length(Sub(s.PosM, s.TargetM));
            double tol = s.RangeToleranceM > 0 ? s.RangeToleranceM : 0;
            double sweep = Math.Max(MinSweepM, SweepTolerances * tol);
            if (arc > sweep)
            {
                Vec3 back = SpreadVelocity(s);
                double backLen = Length(back);
                if (backLen > 1e-6) return Scale(back, Math.Max(speed, backLen) / backLen);
            }

            Vec3 rel = Sub(s.VelMs, s.TargetVelMs);
            Vec3 across = Sub(rel, Scale(away, Dot(rel, away)));
            if (Length(across) > 1.0) return Scale(Direction(across), speed);
            return Scale(OrbitTangent(s, away), speed);
        }

        /// <summary>The unit direction round the target about the orbit axis, in the orbit's sense.</summary>
        private static Vec3 OrbitTangent(FlightSample s, Vec3 away)
        {
            Vec3 lateral = Lateral(s, away);
            return s.OrbitSense < 0 ? Scale(lateral, -1.0) : lateral;
        }

        /// <summary>Unit axis × <paramref name="away"/>: across the line of sight, square to the orbit
        /// axis. When the axis lies along the line of sight a fixed world axis stands in, so the
        /// result is deterministic.</summary>
        private static Vec3 Lateral(FlightSample s, Vec3 away)
        {
            Vec3 axis = Direction(s.OrbitAxis);
            if (Length(axis) < 1e-6) axis = Direction(s.UpWorld);
            if (Length(axis) < 1e-6) axis = new Vec3(0, 1, 0);
            Vec3 lateral = Cross(axis, away);
            if (Length(lateral) < 1e-3) lateral = Cross(Math.Abs(away.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 0, 1), away);
            return Direction(lateral);
        }

        /// <summary>The velocity of a running burst, relative to the target: left or right across the
        /// line of sight, up or down the orbit axis square to it, or a boost along the orbit. Down
        /// becomes up in gravity. Capped by cruise and by <see cref="BurstReachFraction"/>.</summary>
        public static Vec3 EvadeVelocity(FlightSample s)
        {
            if (s.Evade == EvadeDir.None || s.EvadeSpeedMs <= 0) return new Vec3(0, 0, 0);
            Vec3 away = AwayFromTarget(s);
            Vec3 lateral = Lateral(s, away);
            Vec3 up = Direction(Cross(away, lateral));

            Vec3 dir;
            switch (s.Evade)
            {
                case EvadeDir.Left:  dir = Scale(lateral, -1.0); break;
                case EvadeDir.Right: dir = lateral; break;
                case EvadeDir.Up:    dir = up; break;
                case EvadeDir.Down:  dir = Length(s.UpWorld) > 1e-6 ? up : Scale(up, -1.0); break;
                default:             dir = OrbitTangent(s, away); break;
            }

            double speed = s.EvadeSpeedMs;
            double reach = RangeSpeedCapMs(s.HoldRangeM * BurstReachFraction, s.DecelMs2, s.CruiseMs);
            if (speed > reach) speed = reach;
            if (s.CruiseMs > 0 && speed > s.CruiseMs) speed = s.CruiseMs;
            return Scale(dir, speed);
        }

        /// <summary>Circle the target at <see cref="FlightSample.HoldRangeM"/>, in the plane of the
        /// grid's current motion around it, at <see cref="FlightSample.OrbitSpeedMs"/> — never under
        /// <see cref="FlightSample.MinCombatSpeedMs"/> — held under <see cref="OrbitSpeedCapMs"/>.
        /// The radial term of <see cref="HoldAtRange"/> corrects the radius as it goes; a running
        /// burst replaces the circle.</summary>
        private static FlightCommand Orbit(FlightSample s)
        {
            if (s.Evade != EvadeDir.None)
            {
                FlightCommand evade = Pursue(s, Leashed(s, RangeKeepingVelocity(s, s.HoldRangeM, true, true, EvadeVelocity(s))));
                evade.FaceTargetWorld = s.TargetM;
                return evade;
            }

            Vec3 away = AwayFromTarget(s);
            Vec3 rel = Sub(s.VelMs, s.TargetVelMs);
            Vec3 across = Sub(rel, Scale(away, Dot(rel, away)));
            if (Length(across) < 1e-3)
            {
                // No motion around the target yet: start the circle about world Y, or about X when
                // the grid sits on the Y axis of the target.
                Vec3 axis = Math.Abs(away.Y) < 0.9 ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
                across = Cross(axis, away);
            }
            Vec3 tangent = Direction(across);

            double speed = s.OrbitSpeedMs > 0 ? s.OrbitSpeedMs : 0;
            if (s.MinCombatSpeedMs > speed) speed = s.MinCombatSpeedMs;
            double cap = OrbitSpeedCapMs(s.HoldRangeM, s.DecelMs2);
            if (speed > cap) speed = cap;
            if (s.CruiseMs > 0 && speed > s.CruiseMs) speed = s.CruiseMs;

            FlightCommand cmd = Pursue(s, Leashed(s, RangeKeepingVelocity(s, s.HoldRangeM, true, true, Scale(tangent, speed))));
            cmd.FaceTargetWorld = s.TargetM;
            return cmd;
        }

        /// <summary>Fall back to <see cref="FlightSample.HoldRangeM"/> when the target is inside it,
        /// nose on the target. Never closes: beyond the range it only matches the target's
        /// velocity, circling it at <see cref="FlightSample.MinCombatSpeedMs"/> as
        /// <see cref="HoldAtRange"/> does.</summary>
        private static FlightCommand Kite(FlightSample s)
        {
            FlightCommand cmd = Pursue(s, Leashed(s, RangeKeepingVelocity(s, s.HoldRangeM, false, true, HoldingTangential(s))));
            cmd.FaceTargetWorld = s.TargetM;
            return cmd;
        }

        /// <summary>One leg of an attack run: close to <see cref="FlightSample.InnerRangeM"/> when
        /// <see cref="FlightSample.RunInbound"/>, else open to <see cref="FlightSample.HoldRangeM"/>
        /// the way <see cref="Kite"/> does. <see cref="FlightCommand.PhaseDone"/> once the leg's
        /// range is reached. A running burst replaces the pull to the spread bearing.</summary>
        private static FlightCommand AttackRun(FlightSample s)
        {
            double dist = Length(Sub(s.PosM, s.TargetM));
            double tol = s.RangeToleranceM > 0 ? s.RangeToleranceM : 0;
            Vec3 tangential = s.Evade != EvadeDir.None ? EvadeVelocity(s) : SpreadVelocity(s);
            FlightCommand cmd;
            if (s.RunInbound)
            {
                cmd = Pursue(s, Leashed(s, RangeKeepingVelocity(s, s.InnerRangeM, true, true, tangential)));
                cmd.PhaseDone = dist <= s.InnerRangeM + tol;
            }
            else
            {
                cmd = Pursue(s, Leashed(s, RangeKeepingVelocity(s, s.HoldRangeM, false, true, tangential)));
                cmd.PhaseDone = dist >= s.HoldRangeM - tol;
            }
            cmd.FaceTargetWorld = s.TargetM;
            return cmd;
        }

        /// <summary>Leave the target at cruise until it is clear: under
        /// <see cref="FlightSample.ClearOfHostilesM"/>, once the nearest hostile grid
        /// (<see cref="FlightSample.HostileSepM"/>) is that far off, wherever the target is; otherwise
        /// once <see cref="FlightSample.HoldRangeM"/> separates it from the target. A retreat that
        /// reads as one: a straight burn directly away (climbing in gravity), whatever the target
        /// is doing, with the nose along that burn -- tail to the enemy -- and no orbit, weave or
        /// burst.</summary>
        private static FlightCommand Disengage(FlightSample s)
        {
            Vec3 away = ClimbingAway(AwayFromTarget(s), s.UpWorld);
            FlightCommand cmd = Pursue(s, Scale(away, s.CruiseMs > 0 ? s.CruiseMs : 0));
            cmd.FaceTargetWorld = Add(s.PosM, Scale(away, 1000.0));
            cmd.PhaseDone = s.ClearOfHostilesM > 0
                ? s.HostileSepM >= s.ClearOfHostilesM
                : Length(Sub(s.PosM, s.TargetM)) >= s.HoldRangeM;
            return cmd;
        }

        /// <summary>Where the hull stands against its leash this tick (<see cref="CombatLeash.Zone"/>).</summary>
        public static LeashZone Leash(FlightSample s)
        {
            return CombatLeash.Zone(Length(Sub(s.AnchorM, s.PosM)), s.LeashM);
        }

        /// <summary>A combat kind's wanted velocity with the leash applied (<see cref="CombatLeash"/>).
        /// Inside the inner edge, or with no leash, unchanged. In the edge band a velocity toward the
        /// anchor is added — the braking-capped speed over the distance back to the inner edge, scaled
        /// by <see cref="CombatLeash.EdgeDepth"/> — which moves the centre of the manoeuvre's circle
        /// toward the anchor; the whole is held under cruise. Past the leash the manoeuvre's own
        /// velocity is dropped, target's included, and the hull flies toward the anchor at that capped
        /// speed but no slower than <see cref="FlightSample.MinCombatSpeedMs"/>, climbing in gravity
        /// while the way back also opens from its target, unless the climb would turn it away from the
        /// anchor. Facing stays the manoeuvre's.</summary>
        private static Vec3 Leashed(FlightSample s, Vec3 wantMs)
        {
            if (s.LeashM <= 0) return wantMs;
            Vec3 toAnchor = Sub(s.AnchorM, s.PosM);
            double dist = Length(toAnchor);
            LeashZone zone = CombatLeash.Zone(dist, s.LeashM);
            if (zone != LeashZone.Edge && zone != LeashZone.Beyond) return wantMs;

            Vec3 home = Scale(toAnchor, 1.0 / dist);   // dist is past the inner edge, so above zero
            double speed = RangeSpeedCapMs(dist - CombatLeash.InnerM(s.LeashM), s.DecelMs2, s.CruiseMs);

            if (zone == LeashZone.Edge)
            {
                Vec3 want = Add(wantMs, Scale(home, speed * CombatLeash.EdgeDepth(dist, s.LeashM)));
                double len = Length(want);
                if (s.CruiseMs > 0 && len > s.CruiseMs) want = Scale(want, s.CruiseMs / len);
                return want;
            }

            if (s.MinCombatSpeedMs > speed) speed = s.MinCombatSpeedMs;
            if (s.CruiseMs > 0 && speed > s.CruiseMs) speed = s.CruiseMs;
            Vec3 dir = home;
            if (Dot(dir, AwayFromTarget(s)) > 0)
            {
                Vec3 climbed = ClimbingAway(dir, s.UpWorld);
                if (Dot(climbed, home) > 0) dir = climbed;
            }
            return Scale(dir, speed);
        }

        /// <summary>The tangential velocity, relative to the target, that carries a grid round the
        /// target to <see cref="FlightSample.SpreadBearing"/>: along the arc toward it, capped by
        /// <see cref="RangeSpeedCapMs"/> on the arc length left, so it comes to rest on its bearing
        /// rather than swinging past it. Zero with no bearing, or within
        /// <see cref="FlightSample.RangeToleranceM"/> of it along the arc.</summary>
        private static Vec3 SpreadVelocity(FlightSample s)
        {
            Vec3 want = Direction(s.SpreadBearing);
            if (Length(want) < 1e-6) return new Vec3(0, 0, 0);

            Vec3 away = AwayFromTarget(s);
            double cos = Dot(away, want);
            if (cos > 1) cos = 1;
            if (cos < -1) cos = -1;
            double dist = Length(Sub(s.PosM, s.TargetM));
            double arc = Math.Acos(cos) * dist;
            double tol = s.RangeToleranceM > 0 ? s.RangeToleranceM : 0;
            if (arc <= tol) return new Vec3(0, 0, 0);

            Vec3 across = Sub(want, Scale(away, cos));
            if (Length(across) < 1e-6)
            {
                // Straight across the target from its bearing: go round either way, the same one
                // every time — over the top in gravity, about world Y otherwise.
                Vec3 axis = Length(s.UpWorld) > 1e-6 ? s.UpWorld
                          : Math.Abs(away.Y) < 0.9 ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
                across = Cross(Cross(away, axis), away);
                if (Length(across) < 1e-6) across = Cross(new Vec3(0, 0, 1), away);
            }
            return Scale(Direction(across), RangeSpeedCapMs(arc, s.DecelMs2, s.CruiseMs));
        }

        /// <summary>Where <see cref="ManoeuvreKind.FormUp"/> holds the grid: the slot on its anchor.</summary>
        public static Vec3 SlotWorld(FlightSample s)
        {
            return Formation.SlotWorld(s.TargetM, s.AnchorForward, s.AnchorUp, s.SlotM);
        }

        /// <summary>Hold <see cref="FlightSample.SlotM"/> on the anchor: the anchor's velocity plus a
        /// closing term toward the slot capped by <see cref="RangeSpeedCapMs"/> on the distance left,
        /// so an escort comes to rest in its slot relative to the anchor instead of flying through
        /// it. Inside <see cref="FlightSample.RangeToleranceM"/> it matches the anchor's velocity
        /// alone. Nose along the anchor's, or left alone when the anchor has no forward.</summary>
        private static FlightCommand FormUp(FlightSample s)
        {
            Vec3 slot = SlotWorld(s);
            Vec3 toSlot = Sub(slot, s.PosM);
            double gap = Length(toSlot);
            double tol = s.RangeToleranceM > 0 ? s.RangeToleranceM : 0;

            Vec3 want = s.TargetVelMs;
            if (gap > tol && gap > 1e-6)
                want = Add(want, Scale(toSlot, RangeSpeedCapMs(gap, s.DecelMs2, s.CruiseMs) / gap));
            double len = Length(want);
            if (s.CruiseMs > 0 && len > s.CruiseMs) want = Scale(want, s.CruiseMs / len);

            FlightCommand cmd = Pursue(s, want);
            Vec3 fwd = Direction(s.AnchorForward);
            if (Length(fwd) > 1e-6) cmd.FaceTargetWorld = Add(s.PosM, Scale(fwd, 1000.0));
            return cmd;
        }

        /// <summary>Unit vector along <paramref name="v"/>, or zero when it has no direction.</summary>
        private static Vec3 Direction(Vec3 v)
        {
            double len = Length(v);
            return len > 1e-6 ? Scale(v, 1.0 / len) : new Vec3(0, 0, 0);
        }

        /// <summary>Unit vector from the grid to the target, or zero when they coincide.</summary>
        private static Vec3 BearingTo(FlightSample s)
        {
            Vec3 toTarget = Sub(s.TargetM, s.PosM);
            double dist = Length(toTarget);
            return dist > 1e-6 ? Scale(toTarget, 1.0 / dist) : new Vec3(0, 0, 0);
        }

        // ---- local vector helpers (Vec3 carries no operators) --------------------------------

        private static Vec3 Sub(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }

        private static Vec3 Add(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }

        private static Vec3 Scale(Vec3 v, double k) { return new Vec3(v.X * k, v.Y * k, v.Z * k); }

        private static double Length(Vec3 v) { return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z); }

        private static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        private static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
    }
}
