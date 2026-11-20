using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Havok;                           // HkSphereShape — the stop-point clearance probe
using Sandbox.Definitions;             // MyCubeBlockDefinition — a combat target's weapon read
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character; // MyCharacter — an avoidance obstacle
using Sandbox.Game.EntityComponents;   // MyAutopilotComponent, MyAiBlockComponent
using Sandbox.Game.GameSystems;        // MyEntityThrustComponent, MyGridGyroSystem
using Sandbox.Game.World;              // MySession, MyFaction — a fleet's opponent faction
using VRage.Game.Entity;               // MyEntity — the avoidance neighbourhood query
using VRage.ModAPI;                    // IMyEntity — the clearance probe's ignore filter
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Native per-tick flight controller for materialised fleet grids — the manoeuvre half of the
    /// hybrid flight model. Long cruise legs stay on the vanilla <c>MyRemoteControl</c> autopilot
    /// (<see cref="VanillaFleetSpawner.WireAutopilot"/>), which brings its own collision avoidance;
    /// this takes over inside the terminal-approach horizon, where vanilla aims at the target's
    /// centre point and arrives far too fast.
    ///
    /// <para>Translation and rotation are commanded independently, which is what the later combat
    /// manoeuvres (orbit, strafe, flee, dock) need — moving one way while pointing another. Both
    /// ride SE's own solvers rather than raw thrust overrides:
    /// <see cref="MyEntityThrustComponent.AutoPilotControlThrust"/> takes a grid-local direction and
    /// <c>ComputeAiThrust</c> supplies gravity compensation, per-axis thrust scaling and
    /// lateral-drift cancellation; a <b>zero</b> direction dampens the whole velocity vector, which
    /// is the strongest braking available and how this controller stops.
    /// <see cref="MyGridGyroSystem.ControlTorque"/> handles facing and comes with SE's own angular
    /// damping. The experimental civilian override path uses per-block effective thrust for its
    /// force tables: SE's <c>GetMaxThrustInDirection</c> excludes overridden thrusters. It runs with
    /// dampeners off and restores every override on release or a transition to combat.</para>
    ///
    /// <para>Game-thread only — driven from <c>FactionsPlugin.Update()</c> via
    /// <see cref="FactionRuntime.UpdateObservation"/>. Deliberately not built on
    /// <c>MyAutopilotComponent.SetCustomMovementFunction</c>, whose delegate runs on an SE parallel
    /// worker thread.</para>
    ///
    /// <para>Three SE behaviours this depends on, each of which silently does nothing if missed:
    /// <c>Thrust.Enabled</c> defaults false on an unmanned grid and gates thrust application
    /// entirely; <c>GyroSystem.AutopilotEnabled</c> must be true or <c>MyShipController</c> zeroes
    /// <c>ControlTorque</c> every frame; and the gyro system deschedules itself unless
    /// <c>MarkDirty()</c> follows a torque command.</para>
    /// </summary>
    internal sealed partial class FleetFlightController
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>Angle error below which facing is considered good enough and torque is released
        /// to SE's angular damping. The remote-control block definitions omit RotationDeadzone, so
        /// this is ours rather than SE's.</summary>
        private const double RotationDeadzoneRad = 0.02;   // ~1.1 degrees

        /// <summary>Proportional gain on the facing error. Kept modest: SE's own SlowdownTorque
        /// supplies the derivative term every frame, so this is the P of a PD loop, not the whole
        /// controller.</summary>
        private const double RotationGain = 0.5;

        private sealed class FlightState
        {
            public long EntityId;
            public MyCubeGrid Grid;
            public MyEntityThrustComponent Thrust;
            public MyGridGyroSystem Gyros;

            /// <summary>The remote control the cruise autopilot flies from, whose forward is the hull's
            /// nose and whose up is its lift side. Null when the grid has none; the grid's own axes
            /// stand in.</summary>
            public MyCubeBlock Reference;
            public Vector3D TargetWorld;
            public ManoeuvreKind Kind;
            /// <summary>Set by <see cref="Leave"/>: the point the grid is breaking away from, flown as
            /// <see cref="ManoeuvreKind.Disengage"/> — away from the nearest hostile grid while one is
            /// within <see cref="LeaveSeparationM"/>, else away from this point — until no hostile grid
            /// is within <see cref="LeaveSeparationM"/>, then <see cref="LeaveDone"/>. Null for every
            /// other grid.</summary>
            public Vector3D? LeaveFromWorld;
            public double LeaveSeparationM;
            public bool LeaveDone;
            public DateTime LeaveStartUtc;

            /// <summary>The nearest hostile grid to a leaving grid, re-found every
            /// <see cref="CombatReadInterval"/> (<see cref="NearestHostile"/>); null when none is within
            /// <see cref="LeaveSeparationM"/>. Its distance is read every tick.</summary>
            public MyCubeGrid LeaveThreat;
            public DateTime NextLeaveScanUtc;
            public bool AnnouncedStop;
            public DateTime EngagedUtc;
            public DateTime LastTraceUtc;

            /// <summary>AI-block activation flags as they were before <see cref="Begin"/> stood them
            /// down, so <see cref="Release"/> can put them back.</summary>
            public List<KeyValuePair<MyAiBlockComponent, bool>> AiBlocks;

            /// <summary>Clearance-resolved rest point (see <see cref="ResolveStopPoint"/>), or null
            /// when no clearance pass has run — the manoeuvre then rests at the classic
            /// ApproachStopDistanceM short of the target.</summary>
            public Vector3D? StopPointWorld;
            public DateTime LastProbeUtc;

            /// <summary>The open braking run: when the grid last began braking continuously, and
            /// how fast it was going then. A run is closed into one sample once it has lasted
            /// <see cref="FactionTuning.BrakeSampleWindowSeconds"/>, and abandoned the moment the
            /// grid stops braking. See <see cref="FleetFlightController.ObserveBraking"/>.</summary>
            public DateTime BrakeRunStartUtc;
            public double BrakeRunStartSpeedMs;
            public bool PrevBrakeCommanded;

            /// <summary>Clearance-resolved departure bearing, world axes, for a
            /// <see cref="ManoeuvreKind.Launch"/>. Resolved once at engage and held: a launch that
            /// re-picked its bearing every tick would weave as the grid moved along it.</summary>
            public Vector3D? LaunchDirWorld;

            /// <summary>Set once the grid is close to its stop point; freezes re-probing so the
            /// grid is not chasing a moving parking spot while it settles.</summary>
            public bool StopPointFrozen;

            /// <summary>Entities near the grid that the avoidance term tracks, refreshed every
            /// <see cref="FactionTuning.FlightAvoidProbeIntervalSec"/>. Membership only: where they
            /// are and how fast they move is read fresh every tick into <see cref="Obstacles"/>.</summary>
            public List<MyEntity> AvoidEntities;
            public DateTime LastAvoidProbeUtc;

            /// <summary>This tick's obstacle spheres, handed to Core, and the entity behind each so
            /// a threat can be named in the log. Reused across ticks; grown, never shrunk.</summary>
            public FlightObstacle[] Obstacles;
            public MyEntity[] ObstacleEntities;

            /// <summary>The obstacle the last FLIGHT_AVOID line named, 0 when no episode is open;
            /// when the episode opened, and the highest urgency seen in it.</summary>
            public long AvoidingId;
            public DateTime AvoidSinceUtc;
            public double AvoidPeak;

            /// <summary>The grid-local direction written to <c>AutoPilotControlThrust</c> last tick.
            /// Read back at the top of the next tick: if what the component holds is no longer this,
            /// something other than this controller is writing the grid's thrust.</summary>
            public Vector3 LastCmdLocal;
            public AnalogueThrust Analogue;
            public AxisThrust ReleasedThrustAxes;
            public DateTime ReleasedThrustAxesUntil;
            public Vector3 TraceInput;
            public int ThrustChanges;
            public int ThrustReversals;
            public float ThrustMaxStep;

            /// <summary>Mirror of the last command's Arrived — at rest within tolerance and slow.
            /// Surfaced through <see cref="IsManoeuvreComplete"/> so leg arrival can accept a
            /// clearance-adjusted stop point outside the node's own arrival radius.</summary>
            public bool Arrived;

            /// <summary>Set by a Launch that has reached handoff speed. Separate from
            /// <see cref="Arrived"/>, which the runtime reads as leg arrival.</summary>
            public bool LaunchComplete;

            /// <summary>Where a Launch engaged. The handback condition is measured from here, not
            /// from the target: what a launch has to escape is behind it.</summary>
            public Vector3D LaunchOriginWorld;

            /// <summary>The grid's fuel stores, thrust bands and manifest as they were at engage.
            /// Null unless <see cref="DebugTraces.Fuel"/> was on then — which is also what decides
            /// whether this manoeuvre produces a FUEL_OBSERVED line at its end, so arming the toggle
            /// mid-flight starts measuring at the next engage rather than half-measuring this one.</summary>
            public GridFuelState FuelAtEngage;

            /// <summary>Set once this manoeuvre's FUEL_OBSERVED line has been written, so an
            /// approach that drifts back out of its arrival envelope and stops again reports one
            /// burn rather than two.</summary>
            public bool FuelLogged;

            /// <summary>Fastest the grid was seen going during the manoeuvre. The modelled Δv is
            /// charged at this speed rather than at
            /// <see cref="FactionTuning.ThrusterCruiseSpeedMs"/>, so the line prices the manoeuvre
            /// that happened; the two agree when the controller's grant and the tuning field
            /// agree.</summary>
            public double PeakSpeedMs;

            /// <summary>Why a Launch is ending before it finished, set on the tick that ends it. Read
            /// once the grid is released, so the spawner hands it on like a finished launch instead of
            /// leaving it with nothing flying it.</summary>
            public string EndReason;

            /// <summary>The fight this grid is holding station in, while its fleet asks for one (see
            /// <see cref="Fight"/>). Null otherwise. Flown in place of <see cref="Kind"/> only while
            /// it has a live target; <see cref="Kind"/> itself is left alone, so the approach it
            /// holds resumes the moment the target goes. An escort's is set by its own read of its
            /// anchor's fight (see <see cref="ReadEscort"/>), never by <see cref="Fight"/>.</summary>
            public CombatState Combat;

            /// <summary>Taken by <see cref="Escort"/>: flies <see cref="ManoeuvreKind.FormUp"/> on
            /// <see cref="AnchorId"/>, or its anchor's fight. Never reports arrival and never owns
            /// its fleet's leg.</summary>
            public bool IsEscort;
            public long AnchorId;

            /// <summary>The anchor grid, resolved when <see cref="AnchorId"/> is set and re-resolved
            /// when it closes; null while none is live, and the escort then brakes and holds.</summary>
            public MyCubeGrid Anchor;

            /// <summary>The block whose orientation is the anchor's nose: its flight state's
            /// <see cref="Reference"/> when this controller flies it, else its remote control, else
            /// null and the anchor grid's own axes.</summary>
            public MyCubeBlock AnchorReference;
            public int SlotIndex;

            /// <summary>Slot spacing and how many escorts share the anchor, re-read with
            /// <see cref="NextEscortReadUtc"/>. See <see cref="Formation.SlotSpacingM"/>.</summary>
            public double SlotSpacingM = Formation.MinSlotSpacingM;
            public int FormationSize = 1;
            public DateTime NextEscortReadUtc;

            /// <summary>Its fixed guns and turrets while its fight has aimed fire on; null otherwise.
            /// See <see cref="ReadAim"/>.</summary>
            public AimState Aim;
        }

        /// <summary>One lead's fight: whom it is shooting, what it knows of both sides' weapons, and
        /// the plan the last tactical read produced. Re-read every
        /// <see cref="CombatReadInterval"/>; flown every tick from the live target.</summary>
        private sealed class CombatState
        {
            public string FleetLabel;
            /// <summary>The fleet's learned pick for its fight, from the runtime; null for none.</summary>
            public TacticOrder? Learned;
            public Vector3D AnchorWorld;
            public IReadOnlyList<WeaponBand> OwnBands;

            public MyCubeGrid Target;
            public long TargetId;

            /// <summary>The target's bands, or null when they could not be read — the tactical read
            /// then assumes they reach every bracket. Refreshed on a target change and every
            /// <see cref="TargetBandsRefresh"/>.</summary>
            public IReadOnlyList<WeaponBand> TargetBands;
            public DateTime TargetBandsUtc;

            public CombatPlan Plan;

            /// <summary>The hull points its fixed guns and focuses its turrets (see
            /// <see cref="ReadAim"/>). An escort takes its anchor's.</summary>
            public bool AimedFire;

            /// <summary>The standing override the owner set for this fleet, handed to
            /// <see cref="TacticResolver"/> as its player-override rung; null for none.</summary>
            public TacticOrder? Override;

            /// <summary>A plan is being flown. False while there is no target.</summary>
            public bool Active;

            /// <summary>Set by <see cref="Shadow"/>: the one grid this hull follows, at
            /// <see cref="ShadowStandoffM"/>, with its weapons held. 0 for a fight.</summary>
            public long ShadowTargetId;
            public double ShadowStandoffM;
            /// <summary>A shadow's leash from <see cref="AnchorWorld"/>, in place of the one its guns
            /// would set (<see cref="CombatLeash"/>).</summary>
            public double ShadowLeashM;

            /// <summary>Set by <see cref="Fight"/>'s pursuit: a grid this hull chases past its node.
            /// It is always a candidate while it lives, it is never released for distance, and
            /// <see cref="AnchorWorld"/> follows it, so the leash moves with the chase. 0 otherwise.</summary>
            public long PursueTargetId;

            /// <summary>Which leg of an attack run is being flown; see
            /// <see cref="FlightSample.RunInbound"/>.</summary>
            public bool RunInbound = true;

            public DateTime NextReadUtc;

            /// <summary>Every hostile grid the last read found in reach that <see cref="Scope"/>
            /// admits, nearest-first not guaranteed, at most <see cref="MaxCandidates"/>. The lead
            /// fills it; its escorts pick their own targets from it. Reused across reads.</summary>
            public readonly List<MyCubeGrid> Candidates = new List<MyCubeGrid>();

            /// <summary>Per entry of <see cref="Candidates"/>: the grid is the fleet's opponent's.</summary>
            public readonly bool[] CandidateOpponent = new bool[MaxCandidates];

            /// <summary>Which hostile grids the fleet may shoot. See <see cref="TargetSelection"/>.</summary>
            public TargetScope Scope;

            /// <summary>The fleet's opponent's lead and escort grids, re-read on every read from
            /// <see cref="OpponentsLookup"/>. A lead's; an escort reads its lead's.</summary>
            public readonly HashSet<long> OpponentGrids = new HashSet<long>();

            /// <summary>The SE faction whose grids are the opponent too, or 0 for none.</summary>
            public long OpponentFactionId;

            /// <summary>Who has been shooting this hull's squadron (or, for an escort under fire
            /// itself, the escort), as of the last read. See <see cref="ReadFire"/>.</summary>
            public FireRead Fire;

            /// <summary>An escort's bearing from its target to hold range on; zero for a lead. See
            /// <see cref="FlightSample.SpreadBearing"/>.</summary>
            public Vector3D SpreadBearing;

            /// <summary>When an escort last read its own weapons.</summary>
            public DateTime OwnBandsUtc;

            /// <summary>The hull's combat acceleration (<see cref="CombatAgility.AccelMs2"/>), re-read
            /// from live thrust and mass every <see cref="CombatReadInterval"/>, and what it buys this
            /// tick. See <see cref="FlightSample.MinCombatSpeedMs"/>.</summary>
            public double AgilityAccelMs2;
            public DateTime NextAgilityUtc;
            public CombatMotion Motion;

            /// <summary>Its evasive bursts; see <see cref="Evasion"/>.</summary>
            public EvadeState Evade;

            /// <summary>What the last FLIGHT_TACTIC line reported, so the next is written only when
            /// the read changes.</summary>
            public long LoggedTargetId;
            public TacticArm LoggedArm;
            public double LoggedBracketM = double.NaN;
            public double LoggedHoldM = double.NaN;
        }

        /// <summary>How often a fighting lead's target and plan are re-read.</summary>
        private static readonly TimeSpan CombatReadInterval = TimeSpan.FromSeconds(1);

        /// <summary>How often a target's weapons are re-read while it stays the target, so damage
        /// that silences its guns reaches the plan.</summary>
        private static readonly TimeSpan TargetBandsRefresh = TimeSpan.FromSeconds(10);

        /// <summary>How far from the lead a hostile grid is looked for. Past the longest vanilla
        /// weapon's reach, so a target a standoff could fight is inside it.</summary>
        private const double CombatAcquireRadiusM = 3000.0;

        /// <summary>A target is kept until it is this far from the lead, so one skirting the edge
        /// of the acquisition sphere is not dropped and re-found on alternate reads.</summary>
        private const double CombatReleaseRadiusM = 3750.0;

        /// <summary>No target further than this from the node the fleet holds at, so a lead does not
        /// pick something across the sector. How far the hull itself may fight from its anchor is
        /// its leash (<see cref="CombatLeash"/>), which is tighter.</summary>
        private const double TargetFromAnchorM = 5000.0;

        /// <summary>How many hostile grids one read keeps for a squadron's escorts to choose from.</summary>
        private const int MaxCandidates = 16;

        /// <summary>How long an escort the controller declined or dropped waits before it is asked
        /// again, so a refusal (the bubble cap, no braking estimate) costs one check per interval
        /// rather than one per frame.</summary>
        private static readonly TimeSpan EscortRetryInterval = TimeSpan.FromSeconds(5);

        /// <summary>Weapon stats per block definition. SE's definitions do not change in a session,
        /// so each is read once. Game thread only.</summary>
        private static readonly Dictionary<VRage.Game.MyDefinitionId, WeaponBand> WeaponStatsByDefinition
            = new Dictionary<VRage.Game.MyDefinitionId, WeaponBand>();

        /// <summary>Resolves a grid that belongs to a fleet to that fleet's weapon bands, or null.
        /// Set by the runtime, which is what knows the fleets.</summary>
        public Func<long, IReadOnlyList<WeaponBand>> FleetBandsLookup;

        /// <summary>Fills a fleet's opponent grids, by fleet id, and returns the tag of the faction
        /// whose grids are its opponent too, or null. Set by the runtime, which is what knows the
        /// meetings. See <see cref="TargetSelection.CollectOpponents"/>.</summary>
        public Func<string, ICollection<long>, string> OpponentsLookup;

        /// <summary>Who last shot each materialised hull; set by the spawner, which records the hits.
        /// Null reads as nobody shooting.</summary>
        public IncomingFire Fire;

        /// <summary>The pacing call (<see cref="ZoneSiegePacing.Decide"/>) for fire on the station grid
        /// given by id, from an attacker of the given DPS that last fired into its zone the given
        /// seconds ago. Set by the runtime, which knows the stations and their sieges; null fires freely.</summary>
        public Func<long, double, double, ZoneFireCall> ZonePacing;

        /// <summary>The tactical read's candidates as <see cref="TargetSelection.Pick"/> reads them.
        /// Game thread only; reused.</summary>
        private readonly TargetCandidate[] _pickScratch = new TargetCandidate[MaxCandidates];

        /// <summary>A lead and the escorts on it, for <see cref="ReadFire"/>. Game
        /// thread only; grown, never shrunk.</summary>
        private long[] _squadScratch = new long[8];

        /// <summary>Logs FLIGHT_TACTIC; see <see cref="DebugTraces.Tactics"/>.</summary>
        private static bool DebugTactics { get { return DebugTraces.Tactics; } }

        private static readonly TimeSpan TraceInterval = TimeSpan.FromSeconds(2);

        /// <summary>Logs the per-manoeuvre approach trace. Off by default: at one line per 2 s per
        /// flying grid it is the single largest writer to the Torch log, which is also where the
        /// engine's own stall diagnostics land. Also gates the routine lifecycle lines — engage,
        /// release, stopped, stop point, launch direction, and the spawner's launch-done and
        /// handback — which fire once or more per grid per flight, several thousand a day with a
        /// player flying. Failures stay on: every FLIGHT_ABORT, FLIGHT_HANDBACK_FAILED,
        /// FLIGHT_HANDBACK_GIVEUP and the warnings. Tested before each line is built, not inside a
        /// logger, so nothing is formatted on the normal path. Toggle with 'factions debug
        /// flight'.</summary>
        public static bool DebugFlight;

        private readonly Dictionary<long, FlightState> _controlled = new Dictionary<long, FlightState>();

        // eid -> earliest UtcNow at which Escort may try to take it again. Written on a refusal and
        // when the controller drops an escort on its own; swept when the grid is gone.
        private readonly Dictionary<long, DateTime> _escortRetryAfter = new Dictionary<long, DateTime>();

        // Escorts whose bubble-cap refusal has been logged, so FLIGHT_CAPPED fires once per hull.
        private readonly HashSet<long> _cappedLogged = new HashSet<long>();

        // Scratch for the bubble count: positions of every controlled grid. Grown, never shrunk.
        private Vec3[] _bubbleScratch = new Vec3[16];

        // Per-window counts for the perf snapshot; see TakeTickCounts.
        private long _gridTicks;
        private long _escortTicks;
        private int _maxControlled;
        private int _capRefusals;

        /// <summary>Drains the counts the perf snapshot reports beside the flight bucket's time:
        /// controlled grids summed over every tick (the flight time divided by this is the cost of
        /// one grid for one tick), the escorts' share of that sum, the most grids controlled at once,
        /// and how many escort takeovers the bubble cap refused.</summary>
        public void TakeTickCounts(out long gridTicks, out long escortTicks, out int maxControlled, out int capRefusals)
        {
            gridTicks = _gridTicks;
            escortTicks = _escortTicks;
            maxControlled = _maxControlled;
            capRefusals = _capRefusals;
            _gridTicks = 0;
            _escortTicks = 0;
            _maxControlled = 0;
            _capRefusals = 0;
        }

        // eid -> earliest UtcNow at which a launch may re-engage it. Written on every launch handback.
        private readonly Dictionary<long, DateTime> _launchCooldownUntil = new Dictionary<long, DateTime>();

        // Grids a launch has held at least once. A grid not in here has had no handback to chatter
        // against, so it engages at LaunchFirstEngageSpeedMs rather than LaunchEngageSpeedMs. Swept
        // when the grid is gone.
        private readonly HashSet<long> _launchedBefore = new HashSet<long>();

        // eid -> the last launch refusal logged for it, so the debug line fires on a change of reason
        // rather than on every tick. Cleared on engage; swept when the grid is gone.
        private readonly Dictionary<long, string> _lastLaunchRefusal = new Dictionary<long, string>();

        // eid -> why a launch ended before finishing. Consumed by IsLaunchFinished on the spawner's
        // next call for that grid; swept when the grid is gone.
        private readonly Dictionary<long, string> _launchEndedEarly = new Dictionary<long, string>();

        /// <summary>What a grid's brake has actually been observed to deliver, m/s², carried across
        /// legs. The thrust-derived estimate is an upper bound on this and on some hulls a wild one,
        /// so cruise speed is granted against whichever is lower — a brake that does not bite buys
        /// the grid less speed rather than more.</summary>
        private sealed class BrakeRecord
        {
            public double ObservedMs2;   // best seen while a full brake was commanded
            public int Samples;
        }
        private readonly Dictionary<long, BrakeRecord> _brakeAuthority = new Dictionary<long, BrakeRecord>();

        /// <summary>Stop points other approaches have already claimed, so two grids converging on
        /// one node cannot be handed the same parking spot. The clearance lattice is deterministic
        /// by design, so without this two similar grids on similar bearings resolve to the identical
        /// point — each probing before the other has arrived, so each reads it clear.</summary>
        private readonly Dictionary<long, KeyValuePair<Vector3D, double>> _claimedStopPoints
            = new Dictionary<long, KeyValuePair<Vector3D, double>>();
        private readonly List<long> _dead = new List<long>();   // reused across ticks; avoids per-frame allocation

        // Scratch for the avoidance neighbourhood query, and the centre its sort measures from.
        // Game thread only, like everything else here.
        private readonly List<MyEntity> _avoidQuery = new List<MyEntity>();
        private static Vector3D _avoidSortCentre;
        private static readonly Comparison<MyEntity> NearestFirst = delegate (MyEntity a, MyEntity b)
        {
            return Vector3D.DistanceSquared(a.PositionComp.WorldVolume.Center, _avoidSortCentre)
                .CompareTo(Vector3D.DistanceSquared(b.PositionComp.WorldVolume.Center, _avoidSortCentre));
        };
        private FactionTuning _tuning;

        /// <summary>Gravity along a manoeuvre's path, for the modelled burn of the FUEL_OBSERVED
        /// line. Nothing else here needs it — the controller flies off live grid physics.</summary>
        private IPlanetCatalogue _planets;

        public void SetTuning(FactionTuning tuning) { _tuning = tuning; }

        public void SetPlanets(IPlanetCatalogue planets) { _planets = planets; }

        public bool IsControlling(long entityId) { return _controlled.ContainsKey(entityId); }

        /// <summary>The manoeuvre holding <paramref name="entityId"/>, or null when nothing is.</summary>
        public string ControlKind(long entityId)
        {
            FlightState st;
            return _controlled.TryGetValue(entityId, out st) ? st.Kind.ToString() : null;
        }

        /// <summary>The reason the last launch attempt on <paramref name="entityId"/> was declined,
        /// or null when the last attempt engaged or none has been made.</summary>
        public string LastLaunchRefusal(long entityId)
        {
            string reason;
            return _lastLaunchRefusal.TryGetValue(entityId, out reason) ? reason : null;
        }

        public bool LaunchedBefore(long entityId) { return _launchedBefore.Contains(entityId); }

        /// <summary>Breaks a controlled grid away from its fight: the fight, if it had one, ends, and it
        /// flies <see cref="ManoeuvreKind.Disengage"/> — at cruise, nose along the way out, climbing
        /// in gravity, no evasive bursts — away from the nearest hostile grid while one is within
        /// <paramref name="clearM"/>, else away from <paramref name="fromWorld"/>, until no hostile
        /// grid is within <paramref name="clearM"/> of it; then it brakes and holds until the caller
        /// hands it on (<see cref="IsLeaveDone"/>). The break-off is measured from the enemy, not
        /// from the node: a fight that ended far from its node is not already "clear". False,
        /// changing nothing, for a grid it does not control. <paramref name="reason"/> (<c>repelled</c>,
        /// <c>broke_off</c>, <c>beaten</c>) names why on the <c>FLIGHT_ENGAGE</c> line.</summary>
        public bool Leave(long entityId, Vector3D fromWorld, double clearM, string reason = "repelled")
        {
            FlightState st;
            if (!_controlled.TryGetValue(entityId, out st) || st.Kind == ManoeuvreKind.Launch) return false;
            st.Combat = null;
            st.LeaveFromWorld = fromWorld;
            st.LeaveSeparationM = clearM;
            st.LeaveDone = false;
            st.LeaveStartUtc = DateTime.UtcNow;
            st.LeaveThreat = null;
            st.NextLeaveScanUtc = DateTime.MinValue;
            if (DebugFlight)
                Log.Info("FLIGHT_ENGAGE eid=" + entityId + " kind=Disengage from=node sep="
                         + (st.Grid != null ? (st.Grid.PositionComp.GetPosition() - fromWorld).Length().ToString("F0") : "?")
                         + "m need=" + clearM.ToString("F0") + "m reason=" + (reason ?? "repelled"));
            return true;
        }

        /// <summary>True once a grid sent off with <see cref="Leave"/> is clear, or no longer under
        /// control at all (so the caller does not wait on it).</summary>
        public bool IsLeaveDone(long entityId)
        {
            FlightState st;
            return !_controlled.TryGetValue(entityId, out st) || st.LeaveFromWorld == null || st.LeaveDone;
        }

        /// <summary>Drops a grid's post-handback launch cooldown so a launch may re-engage at once.
        /// For the one case the cooldown must not apply to: a handback that has been read back and
        /// found not to have taken. Waiting out the cooldown there is the 45 s metronome rather than
        /// a recovery from it.</summary>
        public void ClearLaunchCooldown(long entityId) { _launchCooldownUntil.Remove(entityId); }

        /// <summary>True while a controlled grid holds at its manoeuvre's rest point — at rest
        /// within tolerance and slow enough to count as stopped. Leg arrival accepts this alongside
        /// the distance-to-node test, because a clearance-adjusted stop point may sit outside the
        /// node's own arrival radius.</summary>
        public bool IsManoeuvreComplete(long entityId)
        {
            FlightState st;
            return _controlled.TryGetValue(entityId, out st)
                && st.Kind == ManoeuvreKind.ApproachAndStop
                && st.Arrived;
        }

        /// <summary>True once a <see cref="ManoeuvreKind.Launch"/> has finished — the grid is up to
        /// handoff speed, or it has held the grid past <see cref="FactionTuning.LaunchTimeoutSeconds"/>
        /// without getting there. Both outcomes want the same thing from the caller: release the grid
        /// and re-wire its cruise autopilot. <paramref name="reason"/> distinguishes them for the log,
        /// because "up to speed" and "could not move it" are very different events.</summary>
        public bool IsLaunchFinished(long entityId, out string reason)
        {
            reason = null;
            FlightState st;
            if (!_controlled.TryGetValue(entityId, out st))
            {
                // Ended early on the controller's own tick and already released: the caller still
                // owes it the re-wire a finished launch gets.
                string early;
                if (!_launchEndedEarly.TryGetValue(entityId, out early)) return false;
                _launchEndedEarly.Remove(entityId);
                reason = early;
                return true;
            }
            if (st.Kind != ManoeuvreKind.Launch) return false;

            if (st.LaunchComplete) { reason = "up_to_speed"; return true; }

            double timeoutSec = _tuning != null ? _tuning.LaunchTimeoutSeconds : 45.0;
            if ((DateTime.UtcNow - st.EngagedUtc).TotalSeconds >= timeoutSec)
            {
                reason = "timeout";
                return true;
            }
            return false;
        }

        /// <summary>Considers taking a grid off the cruise autopilot and onto the native controller,
        /// and returns whether it is now under control. Safe and cheap to call every tick for every
        /// materialised grid: the decision of *when* to take over belongs here, because it depends on
        /// the grid's own stopping distance rather than any fixed radius. Idempotent — for a grid
        /// already under control it just refreshes the target.</summary>
        public bool Begin(long entityId, Vector3D targetWorld, ManoeuvreKind kind)
        {
            return Begin(entityId, targetWorld, kind, true);
        }

        /// <param name="approachFollows">False on a leg the observation loop will never ask an approach
        /// for (a fly-through navigation waypoint), where a launch refused for being inside the
        /// approach horizon would leave the grid flown by nothing.</param>
        public bool Begin(long entityId, Vector3D targetWorld, ManoeuvreKind kind, bool approachFollows)
        {
            FlightState existing;
            if (_controlled.TryGetValue(entityId, out existing))
            {
                // An escort is flown by Escort alone; re-aiming it here would turn its formation
                // into a leg of its own.
                if (existing.IsEscort) return false;

                // A launch is a start-of-leg manoeuvre and may only ever be STARTED on a grid nothing
                // else is flying. Without this, the kind assignment below converts a grid already on
                // its terminal approach into a launch every frame: the launch then reports Complete
                // instantly at cruise speed, the caller hands the grid back, and the approach
                // re-engages -- tearing down and restarting the approach profile, its stop-point
                // probe and its AI-block state on every tick of the arrival.
                if (kind == ManoeuvreKind.Launch && existing.Kind != ManoeuvreKind.Launch) return false;

                // Same-target refresh is the overwhelmingly common case — this is called every
                // tick for every materialised fleet, and every few seconds per leg on a
                // zero-distance itinerary. The cached stop point stays put for it; only a target
                // that genuinely moved invalidates the clearance result.
                if ((existing.TargetWorld - targetWorld).LengthSquared() > 1.0)
                {
                    existing.StopPointWorld = null;
                    existing.StopPointFrozen = false;
                    existing.LaunchDirWorld = null;
                    _claimedStopPoints.Remove(entityId);
                }
                existing.TargetWorld = targetWorld;
                existing.Kind = kind;
                return true;
            }

            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false) || grid.Physics == null) return false;

            // Cheapest refusal first, before any thrust-table or horizon work: this runs every tick
            // for every materialised grid, alongside the approach's own call.
            bool launchedBefore = _launchedBefore.Contains(entityId);
            double engageMs      = _tuning != null ? _tuning.LaunchEngageSpeedMs      :  2.0;
            double firstEngageMs = _tuning != null ? _tuning.LaunchFirstEngageSpeedMs : 10.0;
            double handoffMs     = _tuning != null ? _tuning.LaunchHandoffSpeedMs     : 15.0;
            double speedMs = grid.Physics.LinearVelocity.Length();
            if (kind == ManoeuvreKind.Launch)
            {
                // Speed half only -- the horizon is not computed yet and this is purely an early-out
                // so a moving grid costs one vector length instead of two thrust-table walks. The
                // authority is LaunchGate below, which sees both halves.
                if (speedMs >= FlightManoeuvre.EngageLimitMs(launchedBefore, engageMs, firstEngageMs, handoffMs))
                {
                    NoteLaunchRefusal(entityId, "too_fast", speedMs, double.NaN, false, approachFollows, launchedBefore);
                    return false;
                }

                // A grid just handed back is left alone for one timeout, so a hull that cannot hold
                // speed re-launches on a bounded cadence instead of once per frame.
                DateTime until;
                if (_launchCooldownUntil.TryGetValue(entityId, out until))
                {
                    if (DateTime.UtcNow < until)
                    {
                        NoteLaunchRefusal(entityId, "cooldown", speedMs, double.NaN, false, approachFollows, launchedBefore);
                        return false;
                    }
                    _launchCooldownUntil.Remove(entityId);
                }
            }

            MyEntityThrustComponent thrust = grid.Components.Get<MyEntityThrustComponent>();
            MyGridGyroSystem gyros = grid.GridSystems != null ? grid.GridSystems.GyroSystem : null;
            // SE removes the component with the grid's last thruster. Refused silently: this runs every
            // tick for every materialised lead, and the observation loop retires a thrustless grid
            // (VanillaFleetSpawner.ConsumeThrustLost) with a line of its own.
            if (thrust == null)
            {
                if (kind == ManoeuvreKind.Launch)
                    NoteLaunchRefusal(entityId, "no_thrust", speedMs, double.NaN, false, approachFollows, launchedBefore);
                return false;
            }

            // No usable braking estimate means no usable approach profile: taking the grid over
            // would cap it at the crawl floor for the whole leg. Leave it on the vanilla autopilot,
            // which at least flies at speed and brakes with its own logic. A launch never brakes, so
            // it is not refused here; and no approach can take such a grid, so the horizon below
            // cannot hand it to one either. Judged on the planning value, as the per-tick abort
            // is: a grid whose observed braking is zero would otherwise pass here on its thrust
            // estimate, be taken, and be aborted on its first tick, every tick.
            double decel = EstimateDecelMs2(grid, thrust, grid.Physics.LinearVelocity);
            if (FlightManoeuvre.AbortsOnLostBraking(kind, PlanningDecelMs2(entityId, decel))) return false;

            bool withinHorizon = decel > 0 && WithinApproachHorizon(grid, thrust, targetWorld);
            if (kind == ManoeuvreKind.Launch)
            {
                // Single authority for the launch gate, both halves together: slow enough, and not yet
                // close enough for an approach that will actually be asked for.
                LaunchRefusal refusal = FlightManoeuvre.LaunchGate(speedMs, withinHorizon, approachFollows && decel > 0,
                    launchedBefore, engageMs, firstEngageMs, handoffMs);
                if (refusal != LaunchRefusal.None)
                {
                    NoteLaunchRefusal(entityId, refusal == LaunchRefusal.TooFast ? "too_fast"
                                                : refusal == LaunchRefusal.InsideApproachHorizon ? "inside_horizon"
                                                : "degenerate_band",
                                      speedMs, decel, withinHorizon, approachFollows, launchedBefore);
                    return false;
                }
                _lastLaunchRefusal.Remove(entityId);
            }
            else if (!withinHorizon)
            {
                // Still cruising: vanilla's autopilot (with collision avoidance) owns the long leg.
                return false;
            }

            FlightState st = TakeOver(entityId, grid, thrust, gyros, targetWorld, kind);

            // decel is the raw thrust estimate; plan is what the horizon and the profile actually
            // used. They differ whenever the closed loop has an observation, and reporting only the
            // first makes an engage at the horizon cap unreadable.
            if (DebugFlight)
            {
                double planDecel = PlanningDecelMs2(entityId, decel);
                double obsMs2; int obsN;
                ReadBrakeRecord(entityId, out obsMs2, out obsN);

                Log.Info("FLIGHT_ENGAGE eid=" + entityId + " kind=" + kind
                         + " dist=" + (grid.PositionComp.GetPosition() - targetWorld).Length().ToString("F0") + "m"
                         + " decel=" + decel.ToString("F2") + "m/s2"
                         + " plan=" + planDecel.ToString("F2")
                         + " obs=" + obsMs2.ToString("F2") + "/" + obsN
                         + " cap=" + CruiseSpeedCapMs(grid).ToString("F0")
                         + " mass=" + grid.Physics.Mass.ToString("F0") + "kg"
                         + " nose=" + (st.Reference != null ? "rc" : "grid"));
            }

            // The fuel calibration's opening read (docs/fuel-plan.md, "Verification and balance").
            // One walk of the grid's blocks, here and once more at the manoeuvre's end.
            if (DebugTraces.Fuel) st.FuelAtEngage = GridFuelState.Capture(grid, true);

            // A launch has no rest point to clear; what it needs cleared is the way out.
            if (kind == ManoeuvreKind.ApproachAndStop && (_tuning == null || _tuning.ApproachClearanceEnabled))
                ResolveStopPoint(st, grid.PositionComp.GetPosition(), DateTime.UtcNow);
            else if (kind == ManoeuvreKind.Launch)
                ResolveLaunchDirection(st, grid.PositionComp.GetPosition());

            return true;
        }

        /// <summary>Hands a grid from whatever was flying it to this controller and records it: the
        /// common half of <see cref="Begin"/> and <see cref="Escort"/>, run once their own gates
        /// have passed.</summary>
        private FlightState TakeOver(long entityId, MyCubeGrid grid, MyEntityThrustComponent thrust,
                                     MyGridGyroSystem gyros, Vector3D targetWorld, ManoeuvreKind kind)
        {
            // Hand over from whatever was flying it. SetAutoPilotEnabled(false) stops the vanilla
            // waypoint logic; SetAutopilot(true) is a different call that flips the thrust and gyro
            // systems' own autopilot flags, which is what lets us command them.
            MyAutopilotComponent apc = FindAutopilot(grid);
            if (apc != null)
            {
                apc.SetAutoPilotEnabled(false);
                apc.SetAutopilot(true);
            }
            else
            {
                thrust.AutopilotEnabled = true;
                if (gyros != null) gyros.AutopilotEnabled = true;
            }

            // Both systems are marked dirty on every takeover, not just the RC one. SE's own
            // SetAutopilot pairs each flag with a MarkDirty, and the flag alone is inert: on the
            // thrust component MarkDirty is what sets m_thrustsChanged, which gates the rebuild of
            // the per-axis MaxPositiveThrust tables behind both GetMaxThrustInDirection and the
            // clamp SE bounds its own dampener with, and wakes a sleeping rigid body. A grid taken
            // over through the branch above reaches its approach planning against whatever those
            // tables last held. Idempotent, so re-marking after SetAutopilot costs nothing.
            thrust.MarkDirty();
            if (gyros != null) gyros.MarkDirty();

            // AI-block-piloted grids (AI Flight + AI Basic, no RC): stand the blocks down so they
            // don't fight our commands. The controller itself needs no remote control. Their prior
            // state is kept so Release can restore it.
            List<KeyValuePair<MyAiBlockComponent, bool>> aiBlocks = DeactivateAiBlocks(grid);

            var st = new FlightState
            {
                EntityId    = entityId,
                Grid        = grid,
                Thrust      = thrust,
                Gyros       = gyros,
                TargetWorld = targetWorld,
                Kind        = kind,
                EngagedUtc  = DateTime.UtcNow,
                AiBlocks    = aiBlocks,
                Reference   = apc != null ? apc.Entity as MyCubeBlock : null,
                LaunchOriginWorld = grid.PositionComp.GetPosition(),
            };
            _controlled[entityId] = st;
            return st;
        }

        /// <summary>Asks for a combat manoeuvre on a grid holding station where its fleet fights.
        /// Called every frame for as long as the fleet holds; <see cref="StopFighting"/> ends it.
        /// Returns true while a combat manoeuvre is flying the grid, false while it has no target —
        /// then the manoeuvre the grid was already under keeps flying it.
        ///
        /// <para>Only takes a grid this controller already flies, and never a launch: the approach
        /// that brought the lead in is what it falls back to. Never sets
        /// <see cref="FlightState.Arrived"/>, so <see cref="IsManoeuvreComplete"/> is false for as
        /// long as it fights.</para></summary>
        public bool Fight(long entityId, Vector3D anchorWorld, IReadOnlyList<WeaponBand> ownBands, string fleetLabel,
                          bool aimedFire, TargetScope scope, TacticOrder? tacticOverride, TacticOrder? learned = null,
                          long pursueTargetId = 0)
        {
            FlightState st;
            if (!_controlled.TryGetValue(entityId, out st) || st.Kind == ManoeuvreKind.Launch) return false;
            if (_wing.ContainsKey(entityId)) return false;   // a commander's order flies it

            CombatState c = st.Combat;
            if (c == null)
            {
                c = new CombatState();
                st.Combat = c;
            }
            c.FleetLabel = fleetLabel;
            // A pursuit's anchor is the chased grid, moved by each read; the node anchors anything else.
            if (pursueTargetId == 0) c.AnchorWorld = anchorWorld;
            c.OwnBands = ownBands;
            c.Learned = learned;
            c.AimedFire = aimedFire;
            c.Scope = scope;
            c.Override = tacticOverride;
            bool wasShadow = c.ShadowTargetId != 0;
            c.ShadowTargetId = 0;
            c.ShadowStandoffM = 0;
            c.ShadowLeashM = 0;
            bool pursuitChanged = c.PursueTargetId != pursueTargetId;
            if (pursuitChanged && DebugFlight)
                Log.Info("FLIGHT_ENGAGE eid=" + entityId + " kind=" + st.Kind
                         + " reason=" + (pursueTargetId != 0 ? "pursue target=" + pursueTargetId : "pursuit_over"));
            c.PursueTargetId = pursueTargetId;

            DateTime nowUtc = DateTime.UtcNow;
            if (wasShadow || pursuitChanged || nowUtc >= c.NextReadUtc)
            {
                c.NextReadUtc = nowUtc + CombatReadInterval;
                ReadFight(st, c, nowUtc);
            }
            return c.Active && TargetLive(c);
        }

        /// <summary>Asks a grid holding station to shadow <paramref name="targetId"/>: hold
        /// <paramref name="standoffM"/> from it while it stays within <paramref name="leashM"/> of
        /// <paramref name="anchorWorld"/>, without aiming or firing a gun. Called every frame for as
        /// long as it shadows; <see cref="Fight"/> or <see cref="StopFighting"/> ends it. Its escorts
        /// follow it at the same standoff. Returns true while the shadow is being flown.
        ///
        /// <para>The turrets' own AI is not this controller's: a caller that wants the hull silent
        /// switches them off as well.</para></summary>
        public bool Shadow(long entityId, Vector3D anchorWorld, long targetId, double standoffM, double leashM,
                           string fleetLabel)
        {
            FlightState st;
            if (!_controlled.TryGetValue(entityId, out st) || st.Kind == ManoeuvreKind.Launch) return false;
            if (_wing.ContainsKey(entityId)) return false;

            CombatState c = st.Combat;
            if (c == null)
            {
                c = new CombatState();
                st.Combat = c;
            }
            bool retarget = c.ShadowTargetId != targetId;
            c.FleetLabel = fleetLabel;
            c.AnchorWorld = anchorWorld;
            c.AimedFire = false;
            c.Scope = TargetScope.Any;
            c.Override = null;
            c.ShadowTargetId = targetId;
            c.ShadowStandoffM = standoffM;
            c.ShadowLeashM = leashM;

            DateTime nowUtc = DateTime.UtcNow;
            if (retarget || nowUtc >= c.NextReadUtc)
            {
                c.NextReadUtc = nowUtc + CombatReadInterval;
                ReadShadow(st, c, leashM);
            }
            return c.Active && TargetLive(c);
        }

        /// <summary>The ~1 Hz read of a shadow: the pinned grid while it is live and inside the leash,
        /// as the only candidate, and the shadow plan.</summary>
        private void ReadShadow(FlightState st, CombatState c, double leashM)
        {
            c.Candidates.Clear();
            MyCubeGrid target;
            if (!MyEntities.TryGetEntityById(c.ShadowTargetId, out target, false) || target == null
                || target.MarkedForClose || target.Closed
                || Vector3D.DistanceSquared(target.PositionComp.WorldVolume.Center, c.AnchorWorld) > leashM * leashM)
            {
                if (c.Active && DebugFlight)
                    Log.Info("FLIGHT_ENGAGE eid=" + st.EntityId + " kind=" + st.Kind + " reason=shadow_lost");
                c.Active = false;
                c.Target = null;
                c.TargetId = 0;
                return;
            }

            c.Candidates.Add(target);
            c.CandidateOpponent[0] = false;
            c.Target = target;
            c.TargetId = target.EntityId;
            CombatPlan plan = TacticalRead.ShadowPlan(c.ShadowStandoffM);
            bool kindChanged = !c.Active || plan.Kind != c.Plan.Kind || plan.HoldRangeM != c.Plan.HoldRangeM;
            if (kindChanged) c.RunInbound = true;
            c.Plan = plan;
            c.Active = true;

            if (kindChanged && DebugFlight)
                Log.Info("FLIGHT_ENGAGE eid=" + st.EntityId + " kind=" + plan.Kind
                         + " target=" + c.TargetId
                         + " sep=" + (target.PositionComp.WorldVolume.Center - st.Grid.PositionComp.WorldVolume.Center).Length().ToString("F0") + "m"
                         + " hold=" + plan.HoldRangeM.ToString("F0") + "m reason=shadow");
        }

        /// <summary>Ends a grid's fight, if it has one; the manoeuvre it was under resumes.</summary>
        public void StopFighting(long entityId)
        {
            FlightState st;
            if (!_controlled.TryGetValue(entityId, out st) || st.Combat == null) return;
            if (_wing.ContainsKey(entityId)) return;   // the order's fight is the order's to end
            bool wasActive = st.Combat.Active;
            st.Combat = null;
            if (wasActive && DebugFlight)
                Log.Info("FLIGHT_ENGAGE eid=" + entityId + " kind=" + st.Kind + " reason=fight_over");
        }

        /// <summary>True when a grid is flying a combat manoeuvre this frame.</summary>
        public bool IsFighting(long entityId)
        {
            FlightState st;
            return _controlled.TryGetValue(entityId, out st) && st.Combat != null
                && st.Combat.Active && TargetLive(st.Combat);
        }

        /// <summary>The combat manoeuvre a grid is flying this frame, or null when it is not fighting.</summary>
        public ManoeuvreKind? CombatManoeuvre(long entityId)
        {
            FlightState st;
            if (!_controlled.TryGetValue(entityId, out st) || st.Combat == null
                || !st.Combat.Active || !TargetLive(st.Combat)) return null;
            return st.Combat.Plan.Kind;
        }

        /// <summary>Takes an escort under native control to hold slot <paramref name="slotIndex"/>
        /// on <paramref name="anchorId"/> (<see cref="ManoeuvreKind.FormUp"/>), and returns whether
        /// it is now under control. Called every frame for every escort of a materialised
        /// squadron; for one already held it only re-points the anchor and slot. While the anchor
        /// fights (<see cref="Fight"/>), the escort fights beside it — see <see cref="ReadEscort"/>.
        ///
        /// <para>Refused — and asked again only after <see cref="EscortRetryInterval"/> — when the
        /// grid or its anchor is gone, it has no thrust or no braking estimate, or
        /// <see cref="Formation.MaxControlledPerBubble"/> grids are already controlled within
        /// <see cref="Formation.BubbleRadiusM"/> of it. A refused escort stays on whatever flies it,
        /// which is the caller's to make the cruise autopilot. Leads are never subject to the cap.</para></summary>
        public bool Escort(long entityId, long anchorId, int slotIndex)
        {
            FlightState st;
            if (_controlled.TryGetValue(entityId, out st))
            {
                // Flown for another reason: left to it, and reported as controlled so the caller does
                // not hand it to a cruise autopilot underneath.
                if (!st.IsEscort || _wing.ContainsKey(entityId)) return true;
                if (st.AnchorId != anchorId)
                {
                    st.AnchorId = anchorId;
                    st.Anchor = null;
                    st.AnchorReference = null;
                    st.NextEscortReadUtc = DateTime.MinValue;
                }
                st.SlotIndex = slotIndex;
                return true;
            }
            if (anchorId <= 0 || slotIndex < 0 || anchorId == entityId) return false;

            DateTime nowUtc = DateTime.UtcNow;
            DateTime retry;
            if (_escortRetryAfter.TryGetValue(entityId, out retry))
            {
                if (nowUtc < retry) return false;
                _escortRetryAfter.Remove(entityId);
            }

            MyCubeGrid grid, anchor;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false) || grid.Physics == null
                || grid.MarkedForClose || grid.Closed
                || !MyEntities.TryGetEntityById(anchorId, out anchor, false) || anchor.MarkedForClose || anchor.Closed)
                return RefuseEscort(entityId, nowUtc);

            MyEntityThrustComponent thrust = grid.Components.Get<MyEntityThrustComponent>();
            MyGridGyroSystem gyros = grid.GridSystems != null ? grid.GridSystems.GyroSystem : null;
            if (thrust == null) return RefuseEscort(entityId, nowUtc);

            // FormUp brakes into its slot on the same estimate an approach does; without one it could
            // only crawl, and the cruise autopilot at least keeps up.
            double decel = EstimateDecelMs2(grid, thrust, grid.Physics.LinearVelocity);
            if (FlightManoeuvre.AbortsOnLostBraking(ManoeuvreKind.FormUp, decel)) return RefuseEscort(entityId, nowUtc);

            Vector3D pos = grid.PositionComp.GetPosition();
            int nearby = ControlledWithin(pos, Formation.BubbleRadiusM);
            if (!Formation.AdmitsControl(nearby, Formation.MaxControlledPerBubble))
            {
                _capRefusals++;
                if (DebugFlight && _cappedLogged.Add(entityId))
                    Log.Info("FLIGHT_CAPPED eid=" + entityId + " anchor=" + anchorId
                             + " nearby=" + nearby + " cap=" + Formation.MaxControlledPerBubble);
                return RefuseEscort(entityId, nowUtc);
            }

            st = TakeOver(entityId, grid, thrust, gyros, anchor.PositionComp.GetPosition(), ManoeuvreKind.FormUp);
            st.IsEscort = true;
            st.AnchorId = anchorId;
            st.SlotIndex = slotIndex;
            ResolveAnchor(st);

            if (DebugFlight)
                Log.Info("FLIGHT_ENGAGE eid=" + entityId + " kind=FormUp anchor=" + anchorId
                         + " slot=" + slotIndex
                         + " dist=" + (pos - anchor.PositionComp.GetPosition()).Length().ToString("F0") + "m"
                         + " decel=" + decel.ToString("F2") + "m/s2"
                         + " nearby=" + nearby
                         + " nose=" + (st.Reference != null ? "rc" : "grid"));
            return true;
        }

        private bool RefuseEscort(long entityId, DateTime nowUtc)
        {
            _escortRetryAfter[entityId] = nowUtc + EscortRetryInterval;
            return false;
        }

        /// <summary>Controlled grids within <paramref name="radiusM"/> of <paramref name="pos"/>.</summary>
        private int ControlledWithin(Vector3D pos, double radiusM)
        {
            if (_bubbleScratch.Length < _controlled.Count)
                _bubbleScratch = new Vec3[Math.Max(_controlled.Count, _bubbleScratch.Length * 2)];
            int n = 0;
            foreach (KeyValuePair<long, FlightState> kv in _controlled)
            {
                MyCubeGrid g = kv.Value.Grid;
                if (g == null || g.Closed) continue;
                _bubbleScratch[n++] = ToVec3(g.PositionComp.GetPosition());
            }
            return Formation.CountWithin(ToVec3(pos), _bubbleScratch, n, radiusM);
        }

        /// <summary>Re-resolves an escort's anchor grid and the block its nose is read from, when the
        /// one it holds has closed or was never resolved. Leaves <see cref="FlightState.Anchor"/>
        /// null while none is live.</summary>
        private void ResolveAnchor(FlightState st)
        {
            MyCubeGrid a = st.Anchor;
            if (a != null && !a.Closed && !a.MarkedForClose && a.EntityId == st.AnchorId) return;

            st.Anchor = null;
            st.AnchorReference = null;
            MyCubeGrid found;
            if (!MyEntities.TryGetEntityById(st.AnchorId, out found, false) || found.Closed || found.MarkedForClose)
                return;
            st.Anchor = found;

            FlightState anchorSt;
            if (_controlled.TryGetValue(st.AnchorId, out anchorSt) && anchorSt.Reference != null)
                st.AnchorReference = anchorSt.Reference;
            else
            {
                MyAutopilotComponent apc = FindAutopilot(found);
                st.AnchorReference = apc != null ? apc.Entity as MyCubeBlock : null;
            }
        }

        /// <summary>World orientation of an escort's anchor: its nose block while that is still on
        /// the anchor, else the anchor grid's own axes.</summary>
        private static MatrixD AnchorMatrix(FlightState st)
        {
            MyCubeBlock rc = st.AnchorReference;
            if (rc != null && !rc.Closed && !rc.MarkedForClose && rc.CubeGrid == st.Anchor)
                return rc.PositionComp.WorldMatrixRef;
            return st.Anchor.PositionComp.WorldMatrixRef;
        }

        /// <summary>An escort's once-a-second read: the formation's spacing — one number for every
        /// escort on the anchor, from the largest hull among them and the anchor — and whether it
        /// fights. It fights while its anchor does, from the anchor's own acquisition.</summary>
        private void ReadEscort(FlightState st, DateTime nowUtc)
        {
            ReadFormationSpacing(st);

            MyCubeGrid anchor = st.Anchor;
            FlightState anchorSt;
            CombatState lead = anchor != null && _controlled.TryGetValue(st.AnchorId, out anchorSt) ? anchorSt.Combat : null;
            if (lead == null || !lead.Active || !TargetLive(lead))
            {
                EndEscortFight(st, "fight_over");
                return;
            }
            ReadEscortFight(st, lead, nowUtc);
        }

        /// <summary>The formation's spacing — one number for every hull on the anchor, from the
        /// largest hull among them and the anchor — and its size.</summary>
        private void ReadFormationSpacing(FlightState st)
        {
            MyCubeGrid anchor = st.Anchor;
            double largest = anchor != null ? anchor.PositionComp.WorldVolume.Radius : 0;
            int size = 0;
            foreach (KeyValuePair<long, FlightState> kv in _controlled)
            {
                FlightState o = kv.Value;
                if (!o.IsEscort || o.AnchorId != st.AnchorId || o.Grid == null || o.Grid.Closed) continue;
                size++;
                double r = o.Grid.PositionComp.WorldVolume.Radius;
                if (r > largest) largest = r;
            }
            st.SlotSpacingM = Formation.SlotSpacingM(largest);
            st.FormationSize = size > 0 ? size : 1;
        }

        /// <summary>Back to the slot: drops an escort's fight, if it had one.</summary>
        private static void EndEscortFight(FlightState st, string reason)
        {
            if (st.Combat == null) return;
            bool wasActive = st.Combat.Active;
            st.Combat = null;
            if (wasActive && DebugFlight)
                Log.Info("FLIGHT_ENGAGE eid=" + st.EntityId + " kind=FormUp anchor=" + st.AnchorId + " reason=" + reason);
        }

        /// <summary>An escort fights beside its anchor: the squadron's arm and break-off, its own
        /// target — kept while the anchor's acquisition still holds it, else the candidate nearest
        /// the escort, else the anchor's own — its own weapons and so its own range, and a bearing
        /// from the target fanned out from the anchor's by its slot so hulls do not stack.</summary>
        private void ReadEscortFight(FlightState st, CombatState lead, DateTime nowUtc)
        {
            CombatState c = st.Combat;
            if (c == null)
            {
                c = new CombatState();
                st.Combat = c;
            }
            c.FleetLabel = lead.FleetLabel;
            c.AnchorWorld = lead.AnchorWorld;
            c.AimedFire = lead.AimedFire;
            c.ShadowTargetId = lead.ShadowTargetId;
            c.ShadowStandoffM = lead.ShadowStandoffM;
            c.ShadowLeashM = lead.ShadowLeashM;

            // Unknown weapons borrow the fleet's; an unarmed escort keeps its empty read and so
            // holds at the longest bracket, out of the way.
            if (c.OwnBands == null || nowUtc - c.OwnBandsUtc >= TargetBandsRefresh)
            {
                c.OwnBandsUtc = nowUtc;
                IReadOnlyList<WeaponBand> own = EstimateBands(st.Grid);
                c.OwnBands = own ?? lead.OwnBands;
            }

            // Its lead's scope and candidates, its own distances; the lead's target when none of
            // the candidates is live.
            Vector3D self = st.Grid.PositionComp.WorldVolume.Center;
            MyCubeGrid current = TargetLive(c) && (c.Target == lead.Target || lead.Candidates.Contains(c.Target))
                ? c.Target : null;
            c.Fire = ReadFire(st.EntityId, st.AnchorId, current != null ? current.EntityId : 0, true, nowUtc);
            MyCubeGrid target = PickTarget(lead, self, c.AnchorWorld, CombatLeash.RadiusM(c.OwnBands, lead.Plan.HoldRangeM),
                                           current, IsOpponent(lead, current), c.Fire);
            c.Target = target ?? lead.Target;
            if (!TargetLive(c))
            {
                EndEscortFight(st, "no_target");
                return;
            }
            ReadTargetBands(c, lead, nowUtc);

            var order = new TacticOrder
            {
                Arm = lead.Plan.Arm,
                BreakOff = lead.Plan.BreakOff,
                TargetEntityId = c.TargetId,
                AnchorEntityId = st.AnchorId,
            };
            double[] brackets = _tuning != null ? _tuning.CombatRangeBracketsM : null;
            // A shadowing lead's escorts shadow with it, at its standoff rather than their own guns'.
            CombatPlan plan = c.ShadowTargetId != 0
                ? TacticalRead.ShadowPlan(c.ShadowStandoffM)
                : TacticalRead.Plan(order, c.OwnBands, c.TargetBands, brackets);

            bool kindChanged = !c.Active || plan.Kind != c.Plan.Kind;
            if (kindChanged) c.RunInbound = true;
            c.Plan = plan;
            c.Active = true;

            Vector3D targetPos = c.Target.PositionComp.WorldVolume.Center;
            Vector3D anchorPos = st.Anchor.PositionComp.WorldVolume.Center;
            Vector3 gravity = st.Grid.Physics != null ? st.Grid.Physics.Gravity : Vector3.Zero;
            Vector3D axis = gravity.LengthSquared() > 0.0025f ? -(Vector3D)gravity : AnchorMatrix(st).Up;
            double step = Formation.SpreadStepRad(st.SlotSpacingM, plan.HoldRangeM, st.FormationSize);
            Vec3 bearing = Formation.SpreadBearing(ToVec3(anchorPos - targetPos), ToVec3(axis), st.SlotIndex, step);
            c.SpreadBearing = new Vector3D(bearing.X, bearing.Y, bearing.Z);

            if (kindChanged && DebugFlight)
                Log.Info("FLIGHT_ENGAGE eid=" + st.EntityId + " kind=" + plan.Kind
                         + " target=" + c.TargetId
                         + " sep=" + (targetPos - st.Grid.PositionComp.WorldVolume.Center).Length().ToString("F0") + "m"
                         + " hold=" + plan.HoldRangeM.ToString("F0") + "m"
                         + " anchor=" + st.AnchorId
                         + " spreadDeg=" + (step * 180.0 / Math.PI).ToString("F0"));

            if (c.AimedFire) ReadAim(st, c, nowUtc);
        }

        private static bool TargetLive(CombatState c)
        {
            MyCubeGrid t = c.Target;
            return t != null && !t.MarkedForClose && !t.Closed;
        }

        /// <summary>The ~1 Hz tactical read: keep or find a target, read its weapons, resolve the
        /// order and plan the manoeuvre.</summary>
        private void ReadFight(FlightState st, CombatState c, DateTime nowUtc)
        {
            MyCubeGrid lead = st.Grid;
            if (lead == null || lead.Closed || lead.MarkedForClose) return;
            Vector3D leadPos = lead.PositionComp.WorldVolume.Center;
            long owner = lead.BigOwners.Count > 0 ? lead.BigOwners[0] : 0;

            // A pursued grid anchors the read: the chase's leash and acquisition are around it.
            MyCubeGrid pursued = null;
            if (c.PursueTargetId != 0)
            {
                MyCubeGrid g;
                if (MyEntities.TryGetEntityById(c.PursueTargetId, out g, false) && g != null && !g.MarkedForClose
                    && !g.Closed && IsHostileGrid(g, lead, owner))
                {
                    pursued = g;
                    c.AnchorWorld = g.PositionComp.WorldVolume.Center;
                }
            }

            // One acquisition per read, kept whole for the escorts to choose from; the lead keeps
            // its target or switches by TargetSelection's rule.
            ReadOpponents(c);
            FindTarget(lead, owner, leadPos, c);
            if (pursued != null && !c.Candidates.Contains(pursued))
            {
                if (c.Candidates.Count >= MaxCandidates) c.Candidates.RemoveAt(c.Candidates.Count - 1);
                c.CandidateOpponent[c.Candidates.Count] = IsOpponent(c, pursued);
                c.Candidates.Add(pursued);
            }
            MyCubeGrid current = TargetLive(c)
                && (c.Target == pursued || StillTarget(c.Target, lead, owner, leadPos, c.AnchorWorld))
                && TargetSelection.Eligible(c.Scope, IsOpponent(c, c.Target)) ? c.Target : null;
            c.Fire = ReadFire(st.EntityId, st.EntityId, current != null ? current.EntityId : 0, false, nowUtc);
            c.Target = PickTarget(c, leadPos, c.AnchorWorld, CombatLeash.RadiusM(c.OwnBands, c.Plan.HoldRangeM),
                                  current, IsOpponent(c, current), c.Fire);

            if (!TargetLive(c))
            {
                if (c.Active && DebugFlight)
                    Log.Info("FLIGHT_ENGAGE eid=" + st.EntityId + " kind=" + st.Kind + " reason=no_target");
                c.Active = false;
                c.Target = null;
                c.TargetId = 0;
                return;
            }

            ReadTargetBands(c, null, nowUtc);

            TacticOrder order = TacticResolver.Resolve(new TacticRequest
            {
                TargetEntityId = c.TargetId,
                AnchorEntityId = st.EntityId,
                Learned = c.Learned,
                PlayerOverride = c.Override,
            });
            double[] brackets = _tuning != null ? _tuning.CombatRangeBracketsM : null;
            CombatPlan plan = TacticalRead.Plan(order, c.OwnBands, c.TargetBands, brackets);

            bool kindChanged = !c.Active || plan.Kind != c.Plan.Kind;
            if (kindChanged) c.RunInbound = true;
            c.Plan = plan;
            c.Active = true;

            double sepM = (c.Target.PositionComp.WorldVolume.Center - leadPos).Length();
            if (kindChanged && DebugFlight)
                Log.Info("FLIGHT_ENGAGE eid=" + st.EntityId + " kind=" + plan.Kind
                         + " target=" + c.TargetId
                         + " sep=" + sepM.ToString("F0") + "m"
                         + " hold=" + plan.HoldRangeM.ToString("F0") + "m");

            if (DebugTactics
                && (c.LoggedTargetId != c.TargetId || c.LoggedArm != plan.Arm
                    || c.LoggedBracketM != plan.Pick.BracketM || c.LoggedHoldM != plan.HoldRangeM))
            {
                c.LoggedTargetId = c.TargetId;
                c.LoggedArm = plan.Arm;
                c.LoggedBracketM = plan.Pick.BracketM;
                c.LoggedHoldM = plan.HoldRangeM;
                Log.Info("FLIGHT_TACTIC fleet=" + c.FleetLabel
                         + " eid=" + st.EntityId
                         + " target=" + c.TargetId
                         + " scope=" + c.Scope
                         + " opponent=" + (IsOpponent(c, c.Target) ? "yes" : "no")
                         + " shotBy=" + c.Fire.AttackerId
                         + " shotDmg=" + c.Fire.AttackerDamage.ToString("F0")
                         + " targetDmg=" + c.Fire.CurrentDamage.ToString("F0")
                         + " arm=" + plan.Arm
                         + " breakoff=" + plan.BreakOff
                         + " source=" + order.Source
                         + " kind=" + plan.Kind
                         + " bracket=" + plan.Pick.BracketM.ToString("F0")
                         + " hold=" + plan.HoldRangeM.ToString("F0")
                         + " tol=" + plan.RangeToleranceM.ToString("F0")
                         + " avoid=" + plan.Pick.AvoidInsideM.ToString("F0")
                         + " range=" + sepM.ToString("F0")
                         + " own=" + plan.Pick.OwnDps.ToString("F0")
                         + " their=" + (plan.Pick.TheirsKnown ? plan.Pick.TheirDps.ToString("F0") : "unknown"));
            }

            if (c.AimedFire) ReadAim(st, c, nowUtc);
        }

        /// <summary>Refreshes <paramref name="c"/>'s read of its target's weapons on a target change
        /// and every <see cref="TargetBandsRefresh"/>: <paramref name="shared"/>'s when it is fighting
        /// the same grid, else the target's fleet's, else read off its blocks.</summary>
        private void ReadTargetBands(CombatState c, CombatState shared, DateTime nowUtc)
        {
            if (c.Target.EntityId == c.TargetId && nowUtc - c.TargetBandsUtc < TargetBandsRefresh) return;
            c.TargetId = c.Target.EntityId;
            c.TargetBandsUtc = nowUtc;
            if (shared != null && shared.Target == c.Target && shared.TargetId == c.TargetId)
            {
                c.TargetBands = shared.TargetBands;
                return;
            }
            IReadOnlyList<WeaponBand> fleetBands = FleetBandsLookup != null ? FleetBandsLookup(c.TargetId) : null;
            c.TargetBands = fleetBands != null && fleetBands.Count > 0 ? fleetBands : EstimateBands(c.Target);
        }

        /// <summary>Whether a kept target still qualifies: hostile, inside the release radius of the
        /// lead and the leash of the node.</summary>
        private static bool StillTarget(MyCubeGrid t, MyCubeGrid lead, long owner, Vector3D leadPos, Vector3D anchor)
        {
            Vector3D c = t.PositionComp.WorldVolume.Center;
            return Vector3D.DistanceSquared(c, leadPos) <= CombatReleaseRadiusM * CombatReleaseRadiusM
                && Vector3D.DistanceSquared(c, anchor) <= TargetFromAnchorM * TargetFromAnchorM
                && IsHostileGrid(t, lead, owner);
        }

        /// <summary>Every grid hostile to the lead's owner within <see cref="CombatAcquireRadiusM"/>
        /// of it and <see cref="TargetFromAnchorM"/> of the node that <see cref="CombatState.Scope"/>
        /// admits, up to <see cref="MaxCandidates"/>, into <see cref="CombatState.Candidates"/> — an
        /// opponent's grid displacing another when the list is full. A grid on a rotor, hinge or piston
        /// is counted as the hull it is mounted on (<see cref="WingWorld.HullOf"/>). One pruning-structure query per
        /// read; the lists are reused.</summary>
        private void FindTarget(MyCubeGrid lead, long owner, Vector3D leadPos, CombatState c)
        {
            List<MyCubeGrid> candidates = c.Candidates;
            candidates.Clear();
            if (owner == 0) return;
            var sphere = new BoundingSphereD(leadPos, CombatAcquireRadiusM);
            _avoidQuery.Clear();
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, _avoidQuery, MyEntityQueryType.Both);

            for (int i = 0; i < _avoidQuery.Count; i++)
            {
                MyCubeGrid g = _avoidQuery[i] as MyCubeGrid;
                if (g == null || g.MarkedForClose || g.Closed || g.IsPreview) continue;
                // A turret on a rotor or hinge is its ship: the hull is the target.
                g = WingWorld.HullOf(g);
                if (g == null || g.MarkedForClose || g.Closed || candidates.Contains(g)) continue;
                Vector3D centre = g.PositionComp.WorldVolume.Center;
                if (Vector3D.DistanceSquared(centre, c.AnchorWorld) > TargetFromAnchorM * TargetFromAnchorM) continue;
                if (!IsHostileGrid(g, lead, owner)) continue;
                bool opponent = IsOpponent(c, g);
                if (!TargetSelection.Eligible(c.Scope, opponent)) continue;
                if (candidates.Count < MaxCandidates)
                {
                    c.CandidateOpponent[candidates.Count] = opponent;
                    candidates.Add(g);
                    continue;
                }
                if (!opponent) continue;
                for (int k = 0; k < candidates.Count; k++)
                {
                    if (c.CandidateOpponent[k]) continue;
                    candidates[k] = g;
                    c.CandidateOpponent[k] = true;
                    break;
                }
            }
            _avoidQuery.Clear();
        }

        /// <summary>Whom a hull fights this read, by <see cref="TargetSelection.Pick"/> over
        /// <paramref name="source"/>'s candidates measured from <paramref name="self"/>, each charged
        /// for how far past the inner edge of the hull's leash <paramref name="leashM"/> it lies from
        /// <paramref name="anchor"/>: <paramref name="current"/> — its target, when that still
        /// qualifies, else null — or a candidate, or null for none.</summary>
        private MyCubeGrid PickTarget(CombatState source, Vector3D self, Vector3D anchor, double leashM,
                                      MyCubeGrid current, bool currentOpponent, FireRead fire)
        {
            int n = 0;
            for (int i = 0; i < source.Candidates.Count && n < _pickScratch.Length; i++)
            {
                MyCubeGrid g = source.Candidates[i];
                if (g == null || g.Closed || g.MarkedForClose) continue;
                _pickScratch[n].EntityId = g.EntityId;
                Vector3D centre = g.PositionComp.WorldVolume.Center;
                _pickScratch[n].DistanceM = Vector3D.Distance(centre, self);
                _pickScratch[n].AnchorDistanceM = Vector3D.Distance(centre, anchor);
                _pickScratch[n].Opponent = source.CandidateOpponent[i];
                n++;
            }
            long currentId = current != null ? current.EntityId : 0;
            double currentM = 0, currentAnchorM = 0;
            if (current != null)
            {
                Vector3D centre = current.PositionComp.WorldVolume.Center;
                currentM = Vector3D.Distance(centre, self);
                currentAnchorM = Vector3D.Distance(centre, anchor);
            }
            long id = TargetSelection.Pick(source.Scope, currentId, currentM, currentOpponent, fire, _pickScratch, n,
                                           leashM, currentAnchorM);
            if (id == 0) return null;
            if (id == currentId) return current;
            for (int i = 0; i < source.Candidates.Count; i++)
                if (source.Candidates[i] != null && source.Candidates[i].EntityId == id) return source.Candidates[i];
            return null;
        }

        /// <summary>Who has been shooting a hull, for <see cref="TargetSelection.Pick"/>: an escort's
        /// own attackers when it has been hit itself (<paramref name="ownFirst"/>), else those of the
        /// squadron on <paramref name="leadId"/> — the lead and every escort this controller flies on
        /// it — with the damage the held target <paramref name="currentId"/> dealt the same hulls.</summary>
        private FireRead ReadFire(long selfId, long leadId, long currentId, bool ownFirst, DateTime nowUtc)
        {
            var read = new FireRead();
            if (Fire == null || Fire.Count == 0) return read;
            if (ownFirst)
            {
                _squadScratch[0] = selfId;
                read.AttackerId = Fire.TopAttacker(_squadScratch, 1, nowUtc, out read.AttackerDamage);
                if (read.AttackerId != 0)
                {
                    read.CurrentDamage = Fire.DamageBy(_squadScratch, 1, currentId, nowUtc);
                    return read;
                }
            }
            int n = 0;
            _squadScratch[n++] = leadId;
            foreach (KeyValuePair<long, FlightState> kv in _controlled)
            {
                if (!kv.Value.IsEscort || kv.Value.AnchorId != leadId) continue;
                if (n == _squadScratch.Length) Array.Resize(ref _squadScratch, n * 2);
                _squadScratch[n++] = kv.Key;
            }
            read.AttackerId = Fire.TopAttacker(_squadScratch, n, nowUtc, out read.AttackerDamage);
            read.CurrentDamage = Fire.DamageBy(_squadScratch, n, currentId, nowUtc);
            return read;
        }

        /// <summary>Re-reads a lead's opponent from <see cref="OpponentsLookup"/>.</summary>
        private void ReadOpponents(CombatState c)
        {
            c.OpponentGrids.Clear();
            c.OpponentFactionId = 0;
            string tag = OpponentsLookup != null && c.FleetLabel != null ? OpponentsLookup(c.FleetLabel, c.OpponentGrids) : null;
            if (string.IsNullOrEmpty(tag) || MySession.Static == null || MySession.Static.Factions == null) return;
            MyFaction f = MySession.Static.Factions.TryGetFactionByTag(tag);
            if (f != null) c.OpponentFactionId = f.FactionId;
        }

        /// <summary>Whether <paramref name="g"/> is one of <paramref name="c"/>'s opponent's grids:
        /// a lead or escort of the fleet it meets, or a grid whose majority owner is in the opponent
        /// faction.</summary>
        private static bool IsOpponent(CombatState c, MyCubeGrid g)
        {
            if (g == null) return false;
            if (c.OpponentGrids.Count > 0 && c.OpponentGrids.Contains(g.EntityId)) return true;
            if (c.OpponentFactionId == 0 || g.BigOwners.Count == 0 || MySession.Static == null
                || MySession.Static.Factions == null) return false;
            MyFaction f = MySession.Static.Factions.GetPlayerFaction(g.BigOwners[0]);
            return f != null && f.FactionId == c.OpponentFactionId;
        }

        /// <summary>Hostile the way SE's own relation rules have it — faction relations between
        /// NPC factions, reputation between a player and one — read from the grids' majority
        /// owners. An unowned grid is nobody's enemy, and the lead's own physical group (its
        /// subgrids, anything docked to it) never is.</summary>
        private static bool IsHostileGrid(MyCubeGrid g, MyCubeGrid lead, long owner)
        {
            if (g == lead || g.BigOwners.Count == 0) return false;
            long theirs = g.BigOwners[0];
            if (theirs == 0 || theirs == owner) return false;
            if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Physical != null
                && MyCubeGridGroups.Static.Physical.HasSameGroup(lead, g))
                return false;
            return MyIDModule.GetRelationPlayerPlayer(owner, theirs) == MyRelationsBetweenPlayers.Enemies;
        }

        /// <summary>A grid's weapon bands read off its working weapon blocks, or null when one of
        /// them is a weapon whose damage could not be read — an unknown the tactical read must not
        /// mistake for an unarmed grid. Subgrids are not walked.</summary>
        /// <summary>The summed DPS of <paramref name="grid"/>'s working weapons, each scored as
        /// <see cref="EstimateBands"/> scores it; a working weapon whose stats cannot be scored counts
        /// 1, so an armed grid never reads as unarmed. One pass over its blocks.</summary>
        internal static double WorkingWeaponDps(MyCubeGrid grid)
        {
            var hullWeapons = new WeaponStatsHelper.HullWeaponPricer();
            foreach (MyCubeBlock block in grid.GetFatBlocks())
            {
                if (block == null || !block.IsFunctional) continue;
                MyCubeBlockDefinition def = block.BlockDefinition;
                if (def == null || !WeaponStatsHelper.IsScoreableWeapon(def)) continue;
                WeaponBand stats = StandaloneStats(def);
                hullWeapons.Add(def, stats.Dps, stats.RangeM);
            }
            double sum = 0;
            foreach (var priced in hullWeapons.Finish())
                sum += priced.dps > 0 ? priced.dps : 1;
            return sum;
        }

        /// <summary>A weapon block's price alone, cached per definition; a hull's circuit is priced
        /// on top by <see cref="WeaponStatsHelper.HullWeaponPricer"/>.</summary>
        private static WeaponBand StandaloneStats(MyCubeBlockDefinition def)
        {
            WeaponBand stats;
            if (!WeaponStatsByDefinition.TryGetValue(def.Id, out stats))
            {
                float dps, rangeM;
                WeaponStatsHelper.ComputeWeaponStats(def, out dps, out rangeM);
                stats = new WeaponBand { Dps = dps, RangeM = rangeM };
                WeaponStatsByDefinition[def.Id] = stats;
            }
            return stats;
        }

        private static IReadOnlyList<WeaponBand> EstimateBands(MyCubeGrid grid)
        {
            var hullWeapons = new WeaponStatsHelper.HullWeaponPricer();
            foreach (MyCubeBlock block in grid.GetFatBlocks())
            {
                if (block == null || !block.IsFunctional) continue;
                MyCubeBlockDefinition def = block.BlockDefinition;
                if (def == null || !WeaponStatsHelper.IsScoreableWeapon(def)) continue;

                WeaponBand stats = StandaloneStats(def);
                if (!WeaponStatsHelper.HullWeaponPricer.Prices(def, stats.Dps)) return null;
                hullWeapons.Add(def, stats.Dps, stats.RangeM);
            }
            var perBlock = new List<(float dps, float rangeM)>();
            foreach (var priced in hullWeapons.Finish())
                if (priced.dps > 0f) perBlock.Add((priced.dps, priced.rangeM));
            return WeaponStatsHelper.GroupIntoBands(perBlock);
        }

        /// <summary>Releases a grid. Leaves it dampening to a stop rather than holding a stale
        /// command — the caller re-wires a cruise autopilot if the grid keeps flying.
        ///
        /// <para>A <see cref="ManoeuvreKind.Launch"/> is the exception and must NOT be dampened: a
        /// zero <c>AutoPilotControlThrust</c> is this codebase's "brake and hold", so zeroing it here
        /// would shed the very speed the launch just built, one handback at a time.</para></summary>
        public void Release(long entityId)
        {
            FlightState st;
            if (!_controlled.TryGetValue(entityId, out st)) return;
            _controlled.Remove(entityId);
            _wing.Remove(entityId);

            // The parking claim belongs to the approach; the brake record deliberately outlives it,
            // because what a grid's brake achieved on this leg is what the next leg plans against.
            _claimedStopPoints.Remove(entityId);

            if (st.Kind == ManoeuvreKind.Launch)
            {
                double cooldownSec = _tuning != null ? _tuning.LaunchTimeoutSeconds : 45.0;
                _launchCooldownUntil[entityId] = DateTime.UtcNow.AddSeconds(cooldownSec);
                _launchedBefore.Add(entityId);
            }

            // Every manoeuvre reports here, whatever ended it. A launch has no rest point and so
            // never reaches FLIGHT_STOPPED; an approach reaches it only when it latches Arrived, and
            // one released first -- retargeted, despawned, or resolved at the node's arrival radius
            // while the grid was still descending -- is a flown manoeuvre that burned real fuel.
            if (st.FuelAtEngage != null && !st.FuelLogged)
                LogFuelObserved(st, "released");

            EndAim(st, "released");

            try
            {
                if (st.Analogue != null)
                {
                    st.Analogue.Release();
                    st.Analogue = null;
                }
                if (st.Thrust != null && st.Kind != ManoeuvreKind.Launch)
                    st.Thrust.AutoPilotControlThrust = Vector3.Zero;
                if (st.Gyros != null)
                {
                    st.Gyros.ControlTorque = Vector3.Zero;
                    st.Gyros.MarkDirty();
                }
                RestoreAiBlocks(st.AiBlocks);
            }
            catch (Exception ex)
            {
                Log.Warn("FleetFlightController: release of eid=" + entityId + " failed: " + ex.Message);
            }

            if (DebugFlight) Log.Info("FLIGHT_RELEASE eid=" + entityId);
        }

        /// <summary>Advances every controlled grid. Called once per host frame.</summary>
        public void Tick()
        {
            if (_controlled.Count == 0) return;

            _gridTicks += _controlled.Count;
            if (_controlled.Count > _maxControlled) _maxControlled = _controlled.Count;

            _dead.Clear();
            foreach (KeyValuePair<long, FlightState> kv in _controlled)
            {
                FlightState st = kv.Value;
                if (st.IsEscort) _escortTicks++;
                try
                {
                    if (!TickOne(st)) _dead.Add(kv.Key);
                }
                catch (Exception ex)
                {
                    Log.Warn("FleetFlightController: tick failed for eid=" + kv.Key + ": " + ex.Message);
                    st.EndReason = "tick_failed";
                    _dead.Add(kv.Key);
                }
            }

            // Release, not a bare Remove: every exit path must put the grid's AI blocks and thrust
            // back the way Begin found them, including the abort paths above. A launch that ended
            // with a reason is recorded for the spawner, which re-wires it; one whose grid has gone
            // has none and needs nothing.
            // An escort dropped here waits out the retry interval before Escort may take it again,
            // so a condition that aborts it every tick does not re-engage it every frame.
            DateTime nowUtc = DateTime.UtcNow;
            for (int i = 0; i < _dead.Count; i++)
            {
                FlightState ended;
                if (_controlled.TryGetValue(_dead[i], out ended))
                {
                    if (ended.Kind == ManoeuvreKind.Launch && ended.EndReason != null)
                        _launchEndedEarly[_dead[i]] = ended.EndReason;
                    if (ended.IsEscort) _escortRetryAfter[_dead[i]] = nowUtc + EscortRetryInterval;
                }
                Release(_dead[i]);
            }

            PruneLaunchCooldowns();
            PruneEscortRecords();
            PruneLaunchEnds();
            PruneLaunchRecords();
            PruneBrakeAuthority();
        }

        /// <summary>Drops braking records whose grid has gone. The record survives a leg on purpose
        /// — the next leg plans against what the last one measured — so nothing else removes it, and
        /// a session's worth of destroyed hulls would otherwise accumulate. Swept only past a
        /// threshold, like the cooldown map above.</summary>
        private void PruneBrakeAuthority()
        {
            if (_brakeAuthority.Count < 256) return;
            _dead.Clear();
            foreach (KeyValuePair<long, BrakeRecord> kv in _brakeAuthority)
            {
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById(kv.Key, out grid, false) || grid == null || grid.Closed)
                    _dead.Add(kv.Key);
            }
            for (int i = 0; i < _dead.Count; i++) _brakeAuthority.Remove(_dead[i]);
            _dead.Clear();
        }

        /// <summary>Drops escort retry stamps that have expired and cap-log marks whose grid has gone.
        /// Swept only past a threshold, like the launch cooldowns below.</summary>
        private void PruneEscortRecords()
        {
            if (_escortRetryAfter.Count >= 64)
            {
                DateTime now = DateTime.UtcNow;
                _dead.Clear();
                foreach (KeyValuePair<long, DateTime> kv in _escortRetryAfter)
                    if (now >= kv.Value) _dead.Add(kv.Key);
                for (int i = 0; i < _dead.Count; i++) _escortRetryAfter.Remove(_dead[i]);
                _dead.Clear();
            }
            if (_cappedLogged.Count >= 256)
            {
                _dead.Clear();
                foreach (long eid in _cappedLogged)
                    if (!GridAlive(eid)) _dead.Add(eid);
                for (int i = 0; i < _dead.Count; i++) _cappedLogged.Remove(_dead[i]);
                _dead.Clear();
            }
        }

        /// <summary>Drops expired launch cooldowns so the map cannot grow for the life of the
        /// session -- one entry is added per handback, and a grid that never returns would otherwise
        /// leave its entry behind forever. Swept only past a threshold, so the ordinary case with a
        /// handful of live grids costs nothing.</summary>
        private void PruneLaunchCooldowns()
        {
            if (_launchCooldownUntil.Count < 64) return;
            DateTime now = DateTime.UtcNow;
            _dead.Clear();
            foreach (KeyValuePair<long, DateTime> kv in _launchCooldownUntil)
                if (now >= kv.Value) _dead.Add(kv.Key);
            for (int i = 0; i < _dead.Count; i++) _launchCooldownUntil.Remove(_dead[i]);
            _dead.Clear();
        }

        /// <summary>Records the reason a launch declines a grid, always, so a stall verdict can name
        /// it; logs it behind <c>factions debug flight</c> on the first tick and on each change of
        /// reason. <paramref name="decelMs2"/> is NaN when the refusal came before it was
        /// computed.</summary>
        private void NoteLaunchRefusal(long entityId, string reason, double speedMs, double decelMs2,
                                      bool withinHorizon, bool approachFollows, bool launchedBefore)
        {
            string last;
            if (_lastLaunchRefusal.TryGetValue(entityId, out last) && last == reason) return;
            _lastLaunchRefusal[entityId] = reason;
            if (!DebugFlight) return;
            Log.Info("FLIGHT_LAUNCH_REFUSED eid=" + entityId + " reason=" + reason
                     + " speed=" + speedMs.ToString("F1")
                     + " decel=" + (double.IsNaN(decelMs2) ? "n/a" : decelMs2.ToString("F2"))
                     + " withinHorizon=" + withinHorizon
                     + " approachFollows=" + approachFollows
                     + " launchedBefore=" + launchedBefore);
        }

        /// <summary>Drops the launched-before set and the refusal memory for grids that have gone.
        /// Swept only past a threshold, like the cooldown map above.</summary>
        private void PruneLaunchRecords()
        {
            if (_launchedBefore.Count + _lastLaunchRefusal.Count < 256) return;
            _dead.Clear();
            foreach (long eid in _launchedBefore)
                if (!GridAlive(eid)) _dead.Add(eid);
            foreach (long eid in _lastLaunchRefusal.Keys)
                if (!_dead.Contains(eid) && !GridAlive(eid)) _dead.Add(eid);
            for (int i = 0; i < _dead.Count; i++)
            {
                _launchedBefore.Remove(_dead[i]);
                _lastLaunchRefusal.Remove(_dead[i]);
            }
            _dead.Clear();
        }

        private static bool GridAlive(long entityId)
        {
            MyCubeGrid grid;
            return MyEntities.TryGetEntityById(entityId, out grid, false) && grid != null && !grid.Closed;
        }

        /// <summary>Drops early-end records whose grid has gone before the spawner read them. Swept
        /// only past a threshold, like the cooldown map above.</summary>
        private void PruneLaunchEnds()
        {
            if (_launchEndedEarly.Count < 64) return;
            _dead.Clear();
            foreach (KeyValuePair<long, string> kv in _launchEndedEarly)
            {
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById(kv.Key, out grid, false) || grid == null || grid.Closed)
                    _dead.Add(kv.Key);
            }
            for (int i = 0; i < _dead.Count; i++) _launchEndedEarly.Remove(_dead[i]);
            _dead.Clear();
        }

        /// <summary>Returns false when the grid has gone away and should be dropped.</summary>
        private bool TickOne(FlightState st)
        {
            MyCubeGrid grid = st.Grid;
            if (grid == null || grid.MarkedForClose || grid.Closed || grid.Physics == null) return false;

            Vector3D posWorld = grid.PositionComp.GetPosition();
            Vector3 velWorld = grid.Physics.LinearVelocity;
            DateTime nowUtc = DateTime.UtcNow;

            if (st.FuelAtEngage != null)
            {
                double speedNowMs = velWorld.Length();
                if (speedNowMs > st.PeakSpeedMs) st.PeakSpeedMs = speedNowMs;
            }

            // An escort re-resolves its anchor every tick — a closed one is dropped at once — and
            // re-reads its formation and its anchor's fight once a second. Without a live anchor it
            // brakes and holds; the observation loop settles the squadron.
            bool anchorLive = false;
            WingState wing = WingOf(st.EntityId);
            if (wing != null) anchorLive = TickWing(st, wing, nowUtc);
            else if (st.IsEscort)
            {
                ResolveAnchor(st);
                anchorLive = st.Anchor != null;
                if (!anchorLive) EndEscortFight(st, "no_anchor");
                else if (nowUtc >= st.NextEscortReadUtc)
                {
                    st.NextEscortReadUtc = nowUtc + CombatReadInterval;
                    ReadEscort(st, nowUtc);
                }
            }

            // A fight with a live target flies its combat manoeuvre in place of the grid's own; the
            // grid's own resumes, untouched, the moment the target goes.
            CombatState combat = st.Combat != null && st.Combat.Active && TargetLive(st.Combat) ? st.Combat : null;
            bool leaving = st.LeaveFromWorld.HasValue;
            if (leaving) combat = null;
            ManoeuvreKind kind = leaving ? (st.LeaveDone ? ManoeuvreKind.FormUp : ManoeuvreKind.Disengage)
                               : combat != null ? combat.Plan.Kind : st.Kind;

            bool analogue = !st.IsEscort
                && !leaving && combat == null
                && (kind == ManoeuvreKind.Launch || kind == ManoeuvreKind.ApproachAndStop);
            if (analogue)
            {
                if (st.Analogue == null) st.Analogue = new AnalogueThrust(grid, st.Thrust, nowUtc);
                st.Analogue.Prepare(nowUtc);
            }
            else if (st.Analogue != null)
            {
                // SE rebuilds normal direction tables on its next update. Use the independent
                // capacity briefly so feature-off/combat entry cannot see the old excluded tables.
                st.ReleasedThrustAxes = st.Analogue.Available;
                st.ReleasedThrustAxesUntil = nowUtc.AddSeconds(0.2);
                st.Analogue.Release();
                st.Analogue = null;
            }

            // Read the standing command back before this tick overwrites it, alongside what the last
            // tick actually wrote. Nothing else should have touched it in between.
            Vector3 heldCmd = st.Thrust != null ? st.Thrust.AutoPilotControlThrust : Vector3.Zero;
            Vector3 prevCmd = st.LastCmdLocal;

            // Stop-point clearance cadence: resolve when absent (fresh engage or a moved target),
            // re-probe on the tuned interval while still far out, freeze once close — the grid
            // must not chase a moving parking spot while it is trying to settle. The lattice is
            // deterministic, so a re-probe under unchanged occupancy returns the identical point.
            bool clearanceEnabled = kind == ManoeuvreKind.ApproachAndStop
                && (_tuning == null || _tuning.ApproachClearanceEnabled);
            if (clearanceEnabled)
            {
                if (!st.StopPointWorld.HasValue)
                {
                    ResolveStopPoint(st, posWorld, nowUtc);
                }
                else if (!st.StopPointFrozen)
                {
                    double freezeM = _tuning != null ? _tuning.ApproachProbeFreezeM : 300.0;
                    double intervalSec = _tuning != null ? _tuning.ApproachProbeIntervalSec : 2.0;
                    if ((posWorld - st.StopPointWorld.Value).Length() <= freezeM)
                        st.StopPointFrozen = true;
                    else if ((nowUtc - st.LastProbeUtc).TotalSeconds >= intervalSec)
                        ResolveStopPoint(st, posWorld, nowUtc);
                }
            }

            double rawEstimate = EstimateDecelMs2(grid, st.Thrust, velWorld);
            double decel = PlanningDecelMs2(st.EntityId, rawEstimate);
            if (FlightManoeuvre.AbortsOnLostBraking(kind, decel))
            {
                // Lost the braking estimate mid-approach (power loss, thrusters destroyed). Holding
                // the grid here would crawl it at the speed floor indefinitely; hand it back instead.
                Log.Warn("FLIGHT_ABORT eid=" + st.EntityId + " no braking thrust — returning to cruise autopilot");
                return false;
            }

            // A launch faces the grid's Forward along its departure. Where that attitude cannot hold
            // the grid up, the cruise autopilot, which climbs it level, gets it instead.
            if (st.Kind == ManoeuvreKind.Launch)
            {
                double liftTwr, gravityMs2;
                if (LaunchCannotHoldAltitude(grid, st.Thrust, out liftTwr, out gravityMs2))
                {
                    st.EndReason = "cannot_hold_altitude";
                    Log.Info("FLIGHT_ABORT eid=" + st.EntityId + " kind=Launch reason=cannot_hold_altitude"
                             + " liftTwr=" + liftTwr.ToString("F2")
                             + " g=" + gravityMs2.ToString("F2")
                             + " speed=" + velWorld.Length().ToString("F1")
                             + (DebugFlight ? " " + HullThrustProbe.Describe(grid) : ""));
                    return false;
                }
            }

            bool avoidEnabled = _tuning == null || _tuning.FlightAvoidanceEnabled;
            BoundingSphereD selfVol = grid.PositionComp.WorldVolume;
            int obstacleCount = avoidEnabled ? GatherObstacles(st, grid, selfVol, velWorld.Length(), nowUtc) : 0;

            var sample = new FlightSample
            {
                AnalogueThrust   = analogue,
                PosM             = ToVec3(posWorld),
                TargetM          = ToVec3(st.TargetWorld),
                VelMs            = new Vec3(velWorld.X, velWorld.Y, velWorld.Z),
                DecelMs2         = decel,
                CruiseMs         = CruiseSpeedCapMs(grid),
                StopDistanceM    = _tuning != null ? _tuning.ApproachStopDistanceM    :  50.0,
                ArriveToleranceM = _tuning != null ? _tuning.ApproachArriveToleranceM :   5.0,
                MinApproachMs    = _tuning != null ? _tuning.ApproachMinSpeedMs       :   5.0,
                ArriveSpeedMs    = _tuning != null ? _tuning.ApproachArriveSpeedMs    :   2.0,
                TerminalRadiusM  = _tuning != null ? _tuning.ApproachTerminalRadiusM  : 300.0,
                TerminalSpeedMs  = _tuning != null ? _tuning.ApproachTerminalSpeedMs  :  10.0,
                LaunchHandoffMs  = _tuning != null ? _tuning.LaunchHandoffSpeedMs     :  15.0,
                LaunchOriginM    = ToVec3(st.LaunchOriginWorld),
                LaunchClearanceM = _tuning != null ? _tuning.LaunchClearanceM          : 400.0,
                SelfCenterM       = ToVec3(selfVol.Center),
                SelfRadiusM       = avoidEnabled ? selfVol.Radius : 0,
                Obstacles         = st.Obstacles,
                ObstacleCount     = obstacleCount,
                AvoidLookaheadSec = _tuning != null ? _tuning.FlightAvoidLookaheadSec : 8.0,
                AvoidMarginM      = _tuning != null ? _tuning.FlightAvoidMarginM      : 25.0,
            };
            if (st.LaunchDirWorld.HasValue) sample.LaunchDirWorld = ToVec3(st.LaunchDirWorld.Value);
            if (clearanceEnabled && st.StopPointWorld.HasValue)
                sample.StopPointM = ToVec3(st.StopPointWorld.Value);

            Vector3D combatTargetWorld = Vector3D.Zero;
            if (combat != null)
            {
                MyCubeGrid target = combat.Target;
                combatTargetWorld = target.PositionComp.WorldVolume.Center;
                Vector3 targetVel = target.Physics != null ? target.Physics.LinearVelocity : Vector3.Zero;
                Vector3 gravity = grid.Physics.Gravity;
                float g = gravity.Length();
                sample.TargetM         = ToVec3(combatTargetWorld);
                sample.TargetVelMs     = new Vec3(targetVel.X, targetVel.Y, targetVel.Z);
                sample.HoldRangeM      = combat.Plan.HoldRangeM;
                sample.InnerRangeM     = combat.Plan.InnerRangeM;
                sample.RangeToleranceM = combat.Plan.RangeToleranceM;
                sample.OrbitSpeedMs    = combat.Plan.OrbitSpeedMs;
                sample.RunInbound      = combat.RunInbound;
                sample.UpWorld         = g > 0.05f ? new Vec3(-gravity.X / g, -gravity.Y / g, -gravity.Z / g) : new Vec3(0, 0, 0);
                sample.SpreadBearing   = ToVec3(combat.SpreadBearing);
                ReadCombatMotion(st, combat, wing, g > 0.05f, nowUtc);
                sample.MinCombatSpeedMs = combat.Motion.FloorMs;
                sample.OrbitSense       = CombatAgility.OrbitSense(st.IsEscort && st.AnchorId != 0 ? st.AnchorId : st.EntityId);
                if (g <= 0.05f)
                    sample.OrbitAxis = ToVec3(st.IsEscort && st.Anchor != null ? AnchorMatrix(st).Up : ReferenceMatrix(st).Up);
                if (Evasion.Active(combat.Evade, nowUtc))
                {
                    sample.Evade        = combat.Evade.Dir;
                    sample.EvadeSpeedMs = combat.Evade.SpeedMs;
                }
                Vector3D leashAnchor;
                if (TryLeashAnchor(st, combat, wing, out leashAnchor))
                {
                    sample.AnchorM = ToVec3(leashAnchor);
                    sample.LeashM  = combat.ShadowTargetId != 0
                        ? combat.ShadowLeashM
                        : CombatLeash.RadiusM(combat.OwnBands, combat.Plan.HoldRangeM);
                }
            }
            else if (leaving)
            {
                // Away from the nearest hostile at cruise until every hostile is clear (away from the
                // node while none is near); then a FormUp on its own position, zero wanted velocity,
                // brakes it to rest until the caller wires its way home.
                Vector3 gravity = grid.Physics.Gravity;
                float g = gravity.Length();
                Vector3D awayFrom = st.LeaveFromWorld.Value;
                double hostileM = double.PositiveInfinity;
                if (!st.LeaveDone)
                {
                    if (nowUtc >= st.NextLeaveScanUtc)
                    {
                        st.NextLeaveScanUtc = nowUtc + CombatReadInterval;
                        st.LeaveThreat = NearestHostile(grid, selfVol.Center, st.LeaveSeparationM);
                    }
                    MyCubeGrid threat = st.LeaveThreat;
                    if (threat != null && !threat.Closed && !threat.MarkedForClose)
                    {
                        Vector3D threatPos = threat.PositionComp.WorldVolume.Center;
                        hostileM = (threatPos - selfVol.Center).Length();
                        if (hostileM < st.LeaveSeparationM) awayFrom = threatPos;
                    }
                }
                sample.TargetM    = st.LeaveDone ? sample.PosM : ToVec3(awayFrom);
                sample.TargetVelMs = new Vec3(0, 0, 0);
                sample.HoldRangeM = st.LeaveSeparationM;
                sample.ClearOfHostilesM = st.LeaveSeparationM;
                sample.HostileSepM = hostileM;
                sample.UpWorld    = g > 0.05f ? new Vec3(-gravity.X / g, -gravity.Y / g, -gravity.Z / g) : new Vec3(0, 0, 0);
            }
            else if (st.IsEscort)
            {
                // Slot geometry runs between volume centres, the anchor's and the escort's own, so a
                // hull whose origin sits off its middle is not placed half a hull out of its slot.
                sample.PosM = ToVec3(selfVol.Center);
                sample.RangeToleranceM = Formation.SlotToleranceM;
                if (anchorLive)
                {
                    MyCubeGrid anchor = st.Anchor;
                    Vector3 anchorVel = anchor.Physics != null ? anchor.Physics.LinearVelocity : Vector3.Zero;
                    MatrixD am = AnchorMatrix(st);
                    sample.TargetM       = ToVec3(anchor.PositionComp.WorldVolume.Center);
                    sample.TargetVelMs   = new Vec3(anchorVel.X, anchorVel.Y, anchorVel.Z);
                    sample.AnchorForward = ToVec3(am.Forward);
                    sample.AnchorUp      = ToVec3(am.Up);
                    sample.SlotM         = Formation.SlotOffset(st.SlotIndex, st.SlotSpacingM);
                }
                else
                {
                    // Slot on itself, anchor at rest: FormUp's wanted velocity is zero, a full brake.
                    sample.TargetM = sample.PosM;
                }
            }

            FlightCommand cmd = FlightManoeuvre.Tick(kind, sample);
            if (leaving && !st.LeaveDone && cmd.PhaseDone)
            {
                st.LeaveDone = true;
                if (DebugFlight)
                    Log.Info("FLIGHT_ENGAGE eid=" + st.EntityId + " kind=FormUp reason=clear"
                             + " clear=" + ClearToken(sample.HostileSepM)
                             + " need=" + st.LeaveSeparationM.ToString("F0") + "m"
                             + " from=node sep=" + (posWorld - st.LeaveFromWorld.Value).Length().ToString("F0") + "m"
                             + " secs=" + (nowUtc - st.LeaveStartUtc).TotalSeconds.ToString("F0"));
            }
            st.Arrived = !leaving && cmd.Arrived;
            if (cmd.Complete) st.LaunchComplete = true;
            if (combat != null && cmd.PhaseDone && kind == ManoeuvreKind.AttackRun)
                combat.RunInbound = !combat.RunInbound;
            NoteAvoidance(st, cmd, obstacleCount, velWorld.Length(), nowUtc);

            // Re-asserted every tick, not just at Begin: Enabled in particular defaults false on an
            // unmanned grid and gates thrust application entirely.
            st.Thrust.Enabled = true;
            st.Thrust.AutopilotEnabled = !analogue;

            Matrix toLocal = grid.PositionComp.WorldMatrixNormalizedInv.GetOrientation();

            var moveWorld = new Vector3D(cmd.MoveDirWorld.X, cmd.MoveDirWorld.Y, cmd.MoveDirWorld.Z);
            Vector3 cmdLocal = moveWorld.LengthSquared() > 1e-12
                ? Vector3.Normalize(Vector3.Transform((Vector3)moveWorld, ref toLocal))
                : Vector3.Zero;   // zero == dampen the whole velocity vector == brake and hold
            if (analogue)
            {
                st.Analogue.Apply(cmd, kind, velWorld, grid.Physics.Gravity, grid.Physics.Mass, ref toLocal, nowUtc);
                NoteThrustInput(st, st.Analogue.Input);
            }
            else
            {
                st.Thrust.AutoPilotControlThrust = cmdLocal;
                NoteThrustInput(st, cmdLocal);
            }
            st.LastCmdLocal = analogue ? Vector3.Zero : cmdLocal;

            // Close the loop on the brake. A zero command is a request for full braking authority,
            // so a sustained run of them is the measurement everything above plans against.
            // A proportional correction is not a full-authority brake sample. Keep prior proven
            // records, and collect new full-brake observations only on the legacy path.
            st.PrevBrakeCommanded = !analogue && cmdLocal == Vector3.Zero && kind == ManoeuvreKind.ApproachAndStop;
            ObserveBraking(st, velWorld.Length(), nowUtc, rawEstimate, sample.ArriveSpeedMs);

            bool aimed = AimTick(st, combat, kind, velWorld, ref cmd, nowUtc);
            ApplyFacing(st, cmd, posWorld, ref toLocal, aimed);

            // Debug-gated, throttled approach telemetry — the profile's inputs and its decision,
            // which is what distinguishes "braking correctly" from "capped at the crawl floor" in a
            // live log.
            if (DebugFlight && (nowUtc - st.LastTraceUtc) >= TraceInterval)
            {
                st.LastTraceUtc = nowUtc;
                Vector3D goalWorld = combat != null
                    ? combatTargetWorld + CombatHoldOffset(posWorld, combatTargetWorld, sample)
                    : st.IsEscort ? ToVector3D(FlightManoeuvre.SlotWorld(sample))
                    : sample.StopPointM.HasValue ? st.StopPointWorld.Value : st.TargetWorld;
                Vector3D toGoal = goalWorld - posWorld;
                double goalLen = toGoal.Length();

                // The three cosines the runaway diagnosis turns on, each in [-1, 1]:
                //   cmdDot  commanded thrust vs the bearing to the rest point — is the profile aiming right
                //   thrDot  thrust SE actually applied vs what was commanded — is another writer winning
                //   velDot  travel vs the bearing to the rest point — negative means moving away
                // Compared in grid-local space, which is where both the command and SE's applied
                // thrust already live, through the same matrix the command itself went through: a
                // disagreement here is then a real one and not an artefact of a second conversion.
                Vector3 goalLocal = goalLen > 1e-6
                    ? Vector3.Transform((Vector3)(toGoal / goalLen), ref toLocal)
                    : Vector3.Zero;
                Vector3 velLocal = Vector3.Transform(velWorld, ref toLocal);
                Vector3 finalThrust = st.Thrust.FinalThrust;

                // heldDot: what the thrust component still held when this tick began, against what
                // the last tick wrote into it. 1.00 means this controller is the only writer;
                // anything else means something overwrote the command between ticks. Reads 0.00 on
                // the first line of an engagement and after a braking tick, because a zero vector
                // carries no direction to compare -- `braking` and `thrN` tell those apart.
                double heldDot = Dot(heldCmd, prevCmd);

                // The AI blocks, counted two ways, because they disagree: `flag` is the component's
                // own IsActivated, `sys` is whether MyAiBlockSystem still lists it as the active
                // component for its type -- the view SE's own ComputeAiThrust branches on.
                int aiFlag, aiSys, aiTotal;
                CountAiBlocks(grid, out aiFlag, out aiSys, out aiTotal);

                Log.Info("FLIGHT_TRACE eid=" + st.EntityId
                         + " dist=" + (posWorld - st.TargetWorld).Length().ToString("F0")
                         + " dstop=" + goalLen.ToString("F0")
                         + " speed=" + velWorld.Length().ToString("F1")
                         + " cap=" + cmd.SpeedCapMs.ToString("F1")
                         + " phase=" + kind
                         + " thrust=" + (analogue ? "analogue" : "ai")
                         + " changes=" + st.ThrustChanges
                         + " reversals=" + st.ThrustReversals
                         + " maxStep=" + st.ThrustMaxStep.ToString("F3")
                         + (analogue ? " input=" + st.Analogue.Input.ToString()
                             + " dampeners=" + st.Thrust.DampenersEnabled
                             + " autopilot=" + st.Thrust.AutopilotEnabled
                             + " writes=" + st.Analogue.Writes
                             + " errorMs=" + (ToVector3D(cmd.DesiredVelocityMs.Value) - velWorld).Length().ToString("F2") : "")
                         + " decel=" + decel.ToString("F2")
                         + " mass=" + grid.Physics.Mass.ToString("F0")
                         + " cmdDot=" + Dot(cmdLocal, goalLocal).ToString("F2")
                         + " thrDot=" + Dot(finalThrust, cmdLocal).ToString("F2")
                         + " velDot=" + Dot(velLocal, goalLocal).ToString("F2")
                         + " thrN=" + finalThrust.Length().ToString("F0")
                         + " heldDot=" + heldDot.ToString("F2")
                         + " ai=" + aiFlag + "/" + aiSys + "/" + aiTotal
                         + " obstacles=" + obstacleCount
                         + " avoid=" + cmd.Avoid.Urgency.ToString("F2")
                         + " braking=" + (!analogue && cmd.MoveDirWorld.X == 0 && cmd.MoveDirWorld.Y == 0 && cmd.MoveDirWorld.Z == 0)
                         + (combat != null
                             ? " kind=" + kind
                               + " target=" + combat.TargetId
                               + " sep=" + (posWorld - combatTargetWorld).Length().ToString("F0")
                               + " hold=" + combat.Plan.HoldRangeM.ToString("F0")
                               + " tol=" + combat.Plan.RangeToleranceM.ToString("F0")
                               + " floor=" + FlightManoeuvre.OrbitFloorMs(sample).ToString("F1")
                               + " agil=" + combat.AgilityAccelMs2.ToString("F1")
                               + (sample.Evade != EvadeDir.None ? " evade=" + sample.Evade : "")
                               + (sample.LeashM > 0
                                   ? " anchorDist=" + sample.AnchorM.DistanceTo(sample.PosM).ToString("F0")
                                     + " leash=" + sample.LeashM.ToString("F0")
                                     + " zone=" + FlightManoeuvre.Leash(sample)
                                   : "")
                             : leaving && !st.LeaveDone
                             ? " kind=Disengage clear=" + ClearToken(sample.HostileSepM)
                               + " need=" + st.LeaveSeparationM.ToString("F0")
                             : "")
                         + (st.IsEscort
                             ? " anchor=" + st.AnchorId
                               + " slot=" + st.SlotIndex
                               + (combat == null ? " slotErr=" + SlotErrorM(sample).ToString("F0") : "")
                             : ""));
                st.ThrustChanges = st.ThrustReversals = 0;
                st.ThrustMaxStep = 0;
                if (st.Analogue != null) st.Analogue.Writes = 0;
            }

            if (cmd.Arrived && !st.AnnouncedStop)
            {
                st.AnnouncedStop = true;
                if (DebugFlight)
                    Log.Info("FLIGHT_STOPPED eid=" + st.EntityId
                             + " dist=" + (posWorld - st.TargetWorld).Length().ToString("F0") + "m"
                             + " speed=" + velWorld.Length().ToString("F1") + "m/s");

                if (st.FuelAtEngage != null && !st.FuelLogged) LogFuelObserved(st, "arrived");
            }
            else if (!cmd.Arrived)
            {
                st.AnnouncedStop = false;   // re-armed if the grid drifts back out (e.g. shoved)
            }

            return true;
        }

        /// <summary>How far an escort's volume centre is from its slot, metres — the trace's
        /// <c>slotErr</c>.</summary>
        private static double SlotErrorM(FlightSample s)
        {
            return FlightManoeuvre.SlotWorld(s).DistanceTo(s.PosM);
        }

        private static Vector3D ToVector3D(Vec3 v) { return new Vector3D(v.X, v.Y, v.Z); }

        /// <summary>The trace's and the break-off line's <c>clear=</c>: metres to the nearest hostile
        /// grid, or <c>none</c> when no hostile grid is within the distance a break-off needs.</summary>
        private static string ClearToken(double hostileM)
        {
            return double.IsInfinity(hostileM) ? "none" : hostileM.ToString("F0") + "m";
        }

        /// <summary>The point a fighting hull is leashed to (<see cref="CombatLeash"/>) this tick, the
        /// one place it is decided: a commanded hull's anchor grid — its commander's, or the station
        /// that called for it — while that lives; otherwise the anchor its fight was handed, which is
        /// the node its fleet holds at (a raider's target node, a Guard's or standing defender's node,
        /// a bench side's point), taken by an escort from its lead. False for a commanded hull on no
        /// anchor grid: it holds on itself and has no leash.</summary>
        private static bool TryLeashAnchor(FlightState st, CombatState c, WingState wing, out Vector3D anchorWorld)
        {
            if (wing != null)
            {
                MyCubeGrid a = st.Anchor;
                if (a == null || a.Closed || a.MarkedForClose)
                {
                    anchorWorld = Vector3D.Zero;
                    return false;
                }
                anchorWorld = a.PositionComp.WorldVolume.Center;
                return true;
            }
            anchorWorld = c.AnchorWorld;
            return true;
        }

        /// <summary>The hostile grid nearest <paramref name="centre"/> within <paramref name="radiusM"/>
        /// of it, by <see cref="IsHostileGrid"/> against <paramref name="grid"/>'s owner, counted as the
        /// hull it is mounted on; null for none. One pruning-structure query.</summary>
        private MyCubeGrid NearestHostile(MyCubeGrid grid, Vector3D centre, double radiusM)
        {
            long owner = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
            if (owner == 0 || radiusM <= 0) return null;
            var sphere = new BoundingSphereD(centre, radiusM);
            _avoidQuery.Clear();
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, _avoidQuery, MyEntityQueryType.Both);
            MyCubeGrid best = null;
            double bestSq = radiusM * radiusM;
            for (int i = 0; i < _avoidQuery.Count; i++)
            {
                MyCubeGrid g = _avoidQuery[i] as MyCubeGrid;
                if (g == null || g.MarkedForClose || g.Closed || g.IsPreview) continue;
                g = WingWorld.HullOf(g);
                if (g == null || g.MarkedForClose || g.Closed || !IsHostileGrid(g, grid, owner)) continue;
                double d = Vector3D.DistanceSquared(g.PositionComp.WorldVolume.Center, centre);
                if (d < bestSq)
                {
                    bestSq = d;
                    best = g;
                }
            }
            _avoidQuery.Clear();
            return best;
        }

        /// <summary>From the combat target to the range a combat manoeuvre is keeping, on the
        /// grid's side of it — the trace's "goal" while fighting.</summary>
        private static Vector3D CombatHoldOffset(Vector3D posWorld, Vector3D targetWorld, FlightSample s)
        {
            Vector3D away = posWorld - targetWorld;
            double len = away.Length();
            if (len < 1e-6) return Vector3D.Zero;
            double range = s.RunInbound && s.InnerRangeM > 0 ? s.InnerRangeM : s.HoldRangeM;
            return away * (range / len);
        }

        /// <summary>Fills <see cref="FlightState.Obstacles"/> with the current bounding sphere and
        /// velocity of every tracked neighbour, refreshing which neighbours are tracked on the probe
        /// interval. Returns how many entries are live.</summary>
        private int GatherObstacles(FlightState st, MyCubeGrid grid, BoundingSphereD selfVol,
                                    double speedMs, DateTime nowUtc)
        {
            double intervalSec = _tuning != null ? _tuning.FlightAvoidProbeIntervalSec : 0.5;
            if (st.AvoidEntities == null || (nowUtc - st.LastAvoidProbeUtc).TotalSeconds >= intervalSec)
                RefreshAvoidNeighbours(st, grid, selfVol, speedMs, nowUtc);

            int n = 0;
            for (int i = 0; i < st.AvoidEntities.Count; i++)
            {
                MyEntity e = st.AvoidEntities[i];
                if (e == null || e.MarkedForClose || e.Closed) continue;
                BoundingSphereD vol = e.PositionComp.WorldVolume;
                Vector3 vel = e.Physics != null ? e.Physics.LinearVelocity : Vector3.Zero;
                st.Obstacles[n] = new FlightObstacle
                {
                    CenterM = ToVec3(vol.Center),
                    VelMs   = new Vec3(vel.X, vel.Y, vel.Z),
                    RadiusM = vol.Radius,
                };
                st.ObstacleEntities[n] = e;
                n++;
            }
            return n;
        }

        /// <summary>Re-reads which entities are near enough to matter: every dynamic grid and
        /// character within the distance the grid and a closing obstacle could cover in the
        /// look-ahead, nearest first, capped. The grid's own physical group — its subgrids, and
        /// anything docked to it — moves with it and is never an obstacle. Static grids and voxels
        /// are not queried: stations and terrain are the clearance probes' job, and a station's
        /// bounding sphere swallows the stop point beside it.</summary>
        private void RefreshAvoidNeighbours(FlightState st, MyCubeGrid grid, BoundingSphereD selfVol,
                                            double speedMs, DateTime nowUtc)
        {
            st.LastAvoidProbeUtc = nowUtc;
            if (st.AvoidEntities == null) st.AvoidEntities = new List<MyEntity>();
            st.AvoidEntities.Clear();

            double lookahead = _tuning != null ? _tuning.FlightAvoidLookaheadSec     :    8.0;
            double margin    = _tuning != null ? _tuning.FlightAvoidMarginM          :   25.0;
            double obsSpeed  = _tuning != null ? _tuning.FlightAvoidObstacleSpeedMs  :  100.0;
            double maxReach  = _tuning != null ? _tuning.FlightAvoidQueryMaxRadiusM  : 2000.0;
            int    maxCount  = _tuning != null ? _tuning.FlightAvoidMaxObstacles     :     32;
            if (maxCount < 1) maxCount = 1;

            double reach = selfVol.Radius + margin + (speedMs + obsSpeed) * lookahead;
            if (maxReach > 0 && reach > maxReach) reach = maxReach;

            var sphere = new BoundingSphereD(selfVol.Center, reach);
            _avoidQuery.Clear();
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, _avoidQuery, MyEntityQueryType.Dynamic);
            for (int i = 0; i < _avoidQuery.Count; i++)
                if (IsAvoidable(_avoidQuery[i], grid)) st.AvoidEntities.Add(_avoidQuery[i]);
            _avoidQuery.Clear();

            if (st.AvoidEntities.Count > maxCount)
            {
                _avoidSortCentre = selfVol.Center;
                st.AvoidEntities.Sort(NearestFirst);
                st.AvoidEntities.RemoveRange(maxCount, st.AvoidEntities.Count - maxCount);
            }

            int need = st.AvoidEntities.Count;
            if (st.Obstacles == null || st.Obstacles.Length < need)
            {
                int size = need > 8 ? need : 8;
                st.Obstacles = new FlightObstacle[size];
                st.ObstacleEntities = new MyEntity[size];
            }
        }

        private static bool IsAvoidable(MyEntity e, MyCubeGrid self)
        {
            if (e == null || e == self || e.MarkedForClose || e.Closed) return false;

            MyCubeGrid g = e as MyCubeGrid;
            if (g != null)
            {
                if (g.Physics == null || g.IsStatic || g.IsPreview) return false;
                if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Physical != null
                    && MyCubeGridGroups.Static.Physical.HasSameGroup(self, g))
                    return false;
                return true;
            }
            return e is MyCharacter;
        }

        // An avoidance episode opens at this urgency and closes below the lower one. Urgency is
        // continuous and sits near zero along the edge of the separation sphere, so a single
        // threshold opens and closes on alternate ticks for a grid skimming that edge.
        private const double AvoidLogOpenUrgency  = 0.05;
        private const double AvoidLogCloseUrgency = 0.01;

        /// <summary>Debug-gated: one FLIGHT_AVOID line when the grid starts steering around an
        /// obstacle or the worst threat changes during an episode, and one FLIGHT_AVOID_CLEAR, with
        /// the episode's peak urgency, when it ends.</summary>
        private static void NoteAvoidance(FlightState st, FlightCommand cmd, int obstacleCount,
                                          double speedMs, DateTime nowUtc)
        {
            int wi = cmd.Avoid.WorstIndex;
            MyEntity threat = wi >= 0 && wi < obstacleCount ? st.ObstacleEntities[wi] : null;
            long threatId = threat != null ? threat.EntityId : 0;
            double urgency = threat != null ? cmd.Avoid.Urgency : 0;
            bool open = st.AvoidingId != 0;

            if (open && urgency > st.AvoidPeak) st.AvoidPeak = urgency;

            if (open && urgency < AvoidLogCloseUrgency)
            {
                if (DebugFlight)
                    Log.Info("FLIGHT_AVOID_CLEAR eid=" + st.EntityId
                             + " obstacle=" + st.AvoidingId
                             + " peak=" + st.AvoidPeak.ToString("F2")
                             + " secs=" + (nowUtc - st.AvoidSinceUtc).TotalSeconds.ToString("F1"));
                st.AvoidingId = 0;
                return;
            }

            if (urgency < AvoidLogOpenUrgency || threatId == st.AvoidingId) return;

            if (!open)
            {
                st.AvoidSinceUtc = nowUtc;
                st.AvoidPeak = urgency;
            }
            st.AvoidingId = threatId;
            if (DebugFlight)
            {
                MyCubeGrid tg = threat as MyCubeGrid;
                Log.Info("FLIGHT_AVOID eid=" + st.EntityId
                         + " kind=" + st.Kind
                         + " obstacle=" + threatId
                         + " type=" + (tg != null ? "grid" : "character")
                         + " urgency=" + urgency.ToString("F2")
                         + " t=" + cmd.Avoid.TimeSec.ToString("F1")
                         + " miss=" + cmd.Avoid.MissM.ToString("F0")
                         + " sep=" + cmd.Avoid.SeparationM.ToString("F0")
                         + " speed=" + speedMs.ToString("F1")
                         + " name=" + (tg != null ? tg.DisplayName : threat.DisplayName));
            }
        }

        /// <summary>One FUEL_OBSERVED line: what this manoeuvre actually took out of the hull's
        /// tanks, batteries and reactors, beside what <see cref="FuelBurn"/> says it should have
        /// cost. A materialised NPC grid carries no FSD block, so the modelled side is built
        /// thrusters-only over the straight line between the manoeuvre's two ends.
        ///
        /// <para>Observed is the engage snapshot minus the state now, so a positive number is
        /// consumption. A store the hull does not carry reads <c>n/a</c> rather than 0, because a
        /// hull with no tanks and a hull whose tanks did not move are different findings.
        /// <c>ratio=</c> is observed over modelled on whichever the model actually charged: total
        /// gas litres when any gas was charged, otherwise electricity.</para>
        ///
        /// <para>The modelled side prices the manoeuvre that happened, not a nominal one: Δv is
        /// charged at the manoeuvre's own peak speed (<c>vmodel=</c>, falling back to
        /// <see cref="FactionTuning.ThrusterCruiseSpeedMs"/> when nothing was sampled), and only the
        /// end the manoeuvre flew — a <see cref="ManoeuvreKind.Launch"/> accelerates and never
        /// brakes, an <see cref="ManoeuvreKind.ApproachAndStop"/> engages on a moving grid and only
        /// brakes. Hover is charged for the seconds the manoeuvre took (<c>secs=</c>), not for its
        /// length over its peak speed: a hop of a few metres holds the hull up several times longer
        /// than that quotient. <c>hover=</c> and <c>dv=</c> split the modelled gas into the two
        /// terms, which answer to different inputs: hover to seconds in the well, Δv to speed.</para>
        ///
        /// <para><c>end=</c> says how the manoeuvre finished. <c>arrived</c> latched the stop point;
        /// <c>released</c> did not, so an <see cref="ManoeuvreKind.ApproachAndStop"/> so marked never
        /// completed the brake to rest that its modelled Δv charges, and the two populations must not
        /// be pooled.</para>
        ///
        /// <para>Runs twice per manoeuvre, never per tick, and swallows a block that will not read
        /// into an <c>n/a</c> rather than into the flight loop.</para></summary>
        private void LogFuelObserved(FlightState st, string endReason)
        {
            // Set before anything can fail: a manoeuvre reports once, whatever the read produced.
            st.FuelLogged = true;
            if (_tuning == null) return;

            MyCubeGrid grid = st.Grid;
            if (grid == null || grid.Closed || grid.MarkedForClose) return;

            GridFuelState start = st.FuelAtEngage;
            GridFuelState end;
            try
            {
                end = GridFuelState.Capture(grid, false);
            }
            catch (Exception ex)
            {
                Log.Warn("FleetFlightController: fuel read for eid=" + st.EntityId + " failed: " + ex.Message);
                return;
            }

            double massKg = start.MassKg > 0 ? start.MassKg : end.MassKg;
            var polylineKm = new List<Vec3>();
            polylineKm.Add(ToVec3Km(start.PosWorld));
            polylineKm.Add(ToVec3Km(end.PosWorld));

            double modelSpeedMs = st.PeakSpeedMs > 0 ? st.PeakSpeedMs : _tuning.ThrusterCruiseSpeedMs;
            ThrusterStretchEnds ends = st.Kind == ManoeuvreKind.Launch
                ? ThrusterStretchEnds.AccelerateOnly
                : ThrusterStretchEnds.BrakeOnly;

            LegImpulseProfile profile = LegImpulseProfile.Build(_planets, _tuning, polylineKm, true,
                                                                modelSpeedMs, ends);
            profile.RetimeHover((end.Utc - start.Utc).TotalSeconds);
            LegBurn modelled = FuelBurn.ForLeg(profile, massKg, start.Bands, start.Manifest, _tuning);

            var line = new System.Text.StringBuilder(256);
            line.Append("FUEL_OBSERVED eid=").Append(st.EntityId)
                .Append(" kind=").Append(st.Kind)
                .Append(" secs=").Append(Fixed((end.Utc - start.Utc).TotalSeconds, 1))
                .Append(" mass=").Append(Fixed(massKg, 0))
                .Append(" dist=").Append(Fixed((end.PosWorld - start.PosWorld).Length(), 0));

            // Every gas the hull carries and every gas the model charged, so a burn the hull has no
            // tank for is as visible as a tank the model never touched.
            var gasKeys = new List<string>();
            foreach (KeyValuePair<string, double> kv in start.GasLitres) gasKeys.Add(kv.Key);
            foreach (KeyValuePair<string, double> kv in modelled.GasLitres)
                if (!gasKeys.Contains(kv.Key)) gasKeys.Add(kv.Key);

            double observedChargedGas = 0;
            for (int i = 0; i < gasKeys.Count; i++)
            {
                string key = gasKeys[i];
                double held = 0, left = 0, modelledLitres = 0;
                bool readable = start.HasGasTanks && start.GasLitres.TryGetValue(key, out held);
                end.GasLitres.TryGetValue(key, out left);
                modelled.GasLitres.TryGetValue(key, out modelledLitres);

                double drawn = held - left;
                if (readable && modelledLitres > 0) observedChargedGas += drawn;
                line.Append(' ').Append(key).Append('=')
                    .Append(readable ? Fixed(drawn, 1) : "n/a")
                    .Append('/').Append(Fixed(modelledLitres, 1));
            }

            // Modelled electricity is the battery's share plus whatever neither battery nor reactor
            // covered; the reactor's share is reported as the fuel it costs, on rkg below.
            double modelledElecMwh = modelled.BatteryMwh + modelled.ElectricMwh;
            double observedElecMwh = start.BatteryMwh - end.BatteryMwh;
            line.Append(" elec=").Append(start.HasBatteries ? Fixed(observedElecMwh, 4) : "n/a")
                .Append('/').Append(Fixed(modelledElecMwh, 4));

            string reactorKey = modelled.ReactorFuelKey;
            if (string.IsNullOrEmpty(reactorKey) && start.Manifest != null) reactorKey = start.Manifest.ReactorFuelKey;
            line.Append(" rfuel=").Append(string.IsNullOrEmpty(reactorKey) ? "n/a" : reactorKey)
                .Append(" rkg=").Append(start.HasReactors ? Fixed(start.ReactorFuelKg - end.ReactorFuelKg, 3) : "n/a")
                .Append('/').Append(Fixed(modelled.ReactorFuelKg, 3));

            double modelledGas = modelled.TotalGasLitres;
            string ratio;
            if (modelledGas > 0 && start.HasGasTanks) ratio = Fixed(observedChargedGas / modelledGas, 3);
            else if (modelledElecMwh > 0 && start.HasBatteries) ratio = Fixed(observedElecMwh / modelledElecMwh, 3);
            else ratio = "n/a";

            line.Append(" hover=").Append(Fixed(modelled.HoverGasLitres, 1))
                .Append(" dv=").Append(Fixed(modelled.DeltaVGasLitres, 1))
                .Append(" g=").Append(Fixed(profile.RepresentativeG, 3))
                .Append(" ratio=").Append(ratio)
                .Append(" peak=").Append(Fixed(st.PeakSpeedMs, 1))
                .Append(" vmodel=").Append(Fixed(modelSpeedMs, 1))
                .Append(" end=").Append(endReason);

            Log.Info(line.ToString());
        }

        /// <summary>Fixed-decimal, invariant: these numbers are read by a calibration pass and by
        /// greps, not by a locale.</summary>
        private static string Fixed(double value, int decimals)
        {
            return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                                  CultureInfo.InvariantCulture);
        }

        private static Vec3 ToVec3Km(Vector3D v) { return new Vec3(v.X * 0.001, v.Y * 0.001, v.Z * 0.001); }

        private void ApplyFacing(FlightState st, FlightCommand cmd, Vector3D posWorld, ref Matrix toLocal, bool aimed)
        {
            MyGridGyroSystem gyros = st.Gyros;
            if (gyros == null) return;

            gyros.AutopilotEnabled = true;   // or MyShipController zeroes ControlTorque every frame

            if (!cmd.FaceTargetWorld.HasValue)
            {
                gyros.ControlTorque = Vector3.Zero;
                gyros.MarkDirty();
                return;
            }

            Vec3 face = cmd.FaceTargetWorld.Value;
            Vector3D dir = new Vector3D(face.X, face.Y, face.Z) - posWorld;
            if (dir.LengthSquared() < 1e-9)
            {
                gyros.ControlTorque = Vector3.Zero;
                gyros.MarkDirty();
                return;
            }
            dir.Normalize();

            // The hull's nose and lift side are its remote control's, the frame SE's cruise autopilot
            // flies. In gravity the manoeuvre holds that up away from the planet with the nose level,
            // as the cruise autopilot does, so the thrusters holding the hull up are the same ones on
            // both sides of a handback.
            MatrixD reference = ReferenceMatrix(st);
            bool alignToGravity = _tuning == null || _tuning.FlightGravityAlignEnabled;
            Vector3 gravity = alignToGravity && st.Grid.Physics != null ? st.Grid.Physics.Gravity : Vector3.Zero;
            // An aiming hull may also pitch its nose onto the target, within a cap; see AimedFire.
            AttitudeTarget attitude = aimed
                ? FlightAttitude.ResolveAimed(ToVec3(dir), ToVec3(gravity), ToVec3(reference.Forward), AimedFire.MaxAimPitchRad)
                : FlightAttitude.Resolve(ToVec3(dir), ToVec3(gravity), ToVec3(reference.Forward));

            Vector3D forward = new Vector3D(attitude.Forward.X, attitude.Forward.Y, attitude.Forward.Z);
            Vector3D up = attitude.HasUp
                ? new Vector3D(attitude.Up.X, attitude.Up.Y, attitude.Up.Z)
                : reference.Up;
            QuaternionD current = QuaternionD.CreateFromRotationMatrix(reference.GetOrientation());
            QuaternionD target = QuaternionD.CreateFromForwardUp(forward, up);

            Vector3D errWorld = AngleVelocity(current, target);
            double angle = errWorld.Length();

            if (angle < RotationDeadzoneRad)
            {
                // Good enough — hand the remaining spin to SE's own angular damping rather than
                // fighting it, which is what would make the ship oscillate.
                gyros.ControlTorque = Vector3.Zero;
                gyros.MarkDirty();
                return;
            }

            Vector3 errLocal = Vector3.Transform((Vector3)errWorld, ref toLocal);
            Vector3 torque = errLocal * (float)RotationGain;
            gyros.ControlTorque = Vector3.Clamp(torque, -Vector3.One, Vector3.One);
            gyros.MarkDirty();
        }

        /// <summary>World orientation of the block the hull is flown from: the remote control while it
        /// is still on the grid, else the grid itself.</summary>
        private static MatrixD ReferenceMatrix(FlightState st)
        {
            MyCubeBlock rc = st.Reference;
            if (rc != null && !rc.Closed && !rc.MarkedForClose && rc.CubeGrid == st.Grid)
                return rc.PositionComp.WorldMatrixRef;
            st.Reference = null;
            return st.Grid.PositionComp.WorldMatrixRef;
        }

        /// <summary>Axis-angle error between two orientations. Port of SE's own
        /// <c>MyAutopilotComponent.GetAngleVelocity</c>.</summary>
        private static Vector3D AngleVelocity(QuaternionD q1, QuaternionD q2)
        {
            q1.Conjugate();
            QuaternionD q = q2 * q1;

            double axisLen = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            if (axisLen < 1e-9) return Vector3D.Zero;

            double angle = 2.0 * Math.Acos(MathHelper.Clamp(q.W, -1.0, 1.0));
            if (angle > Math.PI) angle -= Math.PI * 2.0;

            return angle * new Vector3D(q.X, q.Y, q.Z) / axisLen;
        }

        /// <summary>True once the grid is close enough that it must start shedding speed. The
        /// threshold is the grid's own stopping distance plus margin, not a constant: at 100 m/s a
        /// 0.5 m/s² hauler needs ~10 km to stop while a 2 m/s² one needs ~2.5 km.</summary>
        private bool WithinApproachHorizon(MyCubeGrid grid, MyEntityThrustComponent thrust, Vector3D targetWorld)
        {
            Vector3D posWorld = grid.PositionComp.GetPosition();
            Vector3 velWorld = grid.Physics.LinearVelocity;

            var sample = new FlightSample
            {
                PosM          = ToVec3(posWorld),
                TargetM       = ToVec3(targetWorld),
                VelMs         = new Vec3(velWorld.X, velWorld.Y, velWorld.Z),
                DecelMs2      = PlanningDecelMs2(grid.EntityId, EstimateDecelMs2(grid, thrust, velWorld)),
                // The granted cruise speed, not the tuning constant: a grid the clamp let run at
                // 400 m/s needs its horizon sized for 400, or it is handed over far too late.
                CruiseMs      = CruiseSpeedCapMs(grid),
                StopDistanceM = _tuning != null ? _tuning.ApproachStopDistanceM :  50.0,
            };

            double margin = _tuning != null ? _tuning.ApproachHorizonMargin : 1.5;
            // Cap at the observation radius: beyond it the grid would be despawned anyway, and an
            // unstoppable (zero-thrust) grid must not claim an infinite horizon.
            double maxHorizonM = (_tuning != null ? _tuning.ObservationRadiusKm : 15.0) * 1000.0;

            double horizon = FlightManoeuvre.HorizonM(sample, margin, maxHorizonM);
            return (posWorld - targetWorld).Length() <= horizon;
        }

        /// <summary>Braking acceleration available against the current course, from live grid state
        /// so damage and power loss are reflected.
        ///
        /// <para>Sums the thrust of all three grid-local axes that oppose travel, each weighted by
        /// how much of the course lies along it — the same decomposition SE's own autopilot uses for
        /// its brake force. Picking only the single dominant axis instead gives a violently unstable
        /// estimate: it jumps between an incidental strong axis and the real retro axis as attitude
        /// shifts, and reads zero outright whenever the dominant axis happens to carry no thrusters.
        /// Derated by <see cref="FactionTuning.ApproachDecelSafetyFactor"/>, because live thrust can
        /// be below the block maximum and planning optimistically means arriving too fast.</para></summary>
        private double EstimateDecelMs2(MyCubeGrid grid, MyEntityThrustComponent thrust, Vector3 velWorld)
        {
            float mass = grid.Physics.Mass;
            if (mass <= 0) return 0;

            // Not moving yet: plan against the nose direction, so a stationary grid still gets a
            // sensible horizon instead of a zero one.
            Vector3 course = velWorld.LengthSquared() < 1e-6
                ? (Vector3)grid.PositionComp.WorldMatrixRef.Forward
                : velWorld;

            Matrix toLocal = grid.PositionComp.WorldMatrixNormalizedInv.GetOrientation();
            Vector3 brakeLocal = Vector3.Transform(-course, ref toLocal);
            if (brakeLocal.LengthSquared() < 1e-9) return 0;
            brakeLocal.Normalize();

            AxisThrust axes = AvailableAxisThrust(grid, thrust);
            double thrustN =
                  Math.Abs(brakeLocal.X) * (brakeLocal.X >= 0 ? axes.RightN : axes.LeftN)
                + Math.Abs(brakeLocal.Y) * (brakeLocal.Y >= 0 ? axes.UpN : axes.DownN)
                + Math.Abs(brakeLocal.Z) * (brakeLocal.Z >= 0 ? axes.BackwardN : axes.ForwardN);

            if (thrustN <= 0) return 0;

            double safety = _tuning != null ? _tuning.ApproachDecelSafetyFactor : 0.4;

            // Gravity works along the course, not against the airframe: descending to a surface
            // station it adds to speed and fights the brake, climbing away it helps. Ignoring it
            // plans optimistically in exactly the case where overshooting means hitting terrain.
            // (SE's AI-thrust path compensates gravity when applying thrust; this is the planning
            // side, which has to know what is actually left over for braking.) The safety factor
            // lands on what is left after gravity, not on the thrust alone -- see
            // FlightManoeuvre.BrakingDecelMs2.
            double gravityAlongBrake = 0;
            Vector3 gravity = grid.Physics.Gravity;
            if (gravity.LengthSquared() > 1e-6)
            {
                Vector3 brakeWorld = -course;
                brakeWorld.Normalize();
                gravityAlongBrake = Vector3.Dot(gravity, brakeWorld);   // negative when gravity opposes braking
            }

            return FlightManoeuvre.BrakingDecelMs2(thrustN / mass, gravityAlongBrake, safety);
        }

        /// <summary>Whether the grid's current attitude cannot hold it up against local gravity, from
        /// live thrust and mass. False in zero gravity.</summary>
        private bool LaunchCannotHoldAltitude(MyCubeGrid grid, MyEntityThrustComponent thrust,
                                              out double liftTwr, out double gravityMs2)
        {
            liftTwr = double.PositiveInfinity;
            Vector3 gravity = grid.Physics.Gravity;
            gravityMs2 = gravity.Length();
            if (gravityMs2 <= 1e-3) return false;

            Matrix toLocal = grid.PositionComp.WorldMatrixNormalizedInv.GetOrientation();
            Vector3 upLocal = Vector3.Transform(-gravity / (float)gravityMs2, ref toLocal);

            AxisThrust axes = AvailableAxisThrust(grid, thrust);
            liftTwr = FlightManoeuvre.LiftTwr(new Vec3(upLocal.X, upLocal.Y, upLocal.Z), axes,
                                              grid.Physics.Mass, gravityMs2);
            return FlightManoeuvre.LaunchCannotHoldAltitude(liftTwr,
                _tuning != null ? _tuning.LaunchAbortLiftTwr : 1.0);
        }

        /// <summary>The most force each grid-local direction's thrusters can apply, from live state.</summary>
        private static AxisThrust ReadAxisThrust(MyEntityThrustComponent thrust)
        {
            return new AxisThrust
            {
                UpN       = thrust.GetMaxThrustInDirection(Base6Directions.Direction.Up),
                DownN     = thrust.GetMaxThrustInDirection(Base6Directions.Direction.Down),
                LeftN     = thrust.GetMaxThrustInDirection(Base6Directions.Direction.Left),
                RightN    = thrust.GetMaxThrustInDirection(Base6Directions.Direction.Right),
                ForwardN  = thrust.GetMaxThrustInDirection(Base6Directions.Direction.Forward),
                BackwardN = thrust.GetMaxThrustInDirection(Base6Directions.Direction.Backward),
            };
        }

        /// <summary>A fighting hull's motion this tick: its agility, re-read once a
        /// <see cref="CombatReadInterval"/> from live thrust over mass, and whether a fresh hit starts
        /// an evasive burst. A hull a commander holds or forms up (<see cref="FormationHeld"/>) keeps
        /// no floor and never breaks. Logs FLIGHT_EVADE once per burst under 'factions debug flight'.</summary>
        private void ReadCombatMotion(FlightState st, CombatState c, WingState wing, bool inGravity, DateTime nowUtc)
        {
            if (nowUtc >= c.NextAgilityUtc)
            {
                c.NextAgilityUtc = nowUtc + CombatReadInterval;
                c.AgilityAccelMs2 = st.Thrust != null && st.Grid.Physics != null
                    ? CombatAgility.AccelMs2(ReadAxisThrust(st.Thrust), st.Grid.Physics.Mass)
                    : 0;
            }
            bool held = FormationHeld(st, wing);
            c.Motion = CombatAgility.Motion(c.AgilityAccelMs2, held);

            DateTime hitUtc = DateTime.MinValue;
            long by = 0;
            bool hit = Fire != null && Fire.Count > 0 && Fire.TryRecent(st.EntityId, nowUtc, out by, out hitUtc);
            if (Evasion.Consider(ref c.Evade, nowUtc, hit ? hitUtc : (DateTime?)null, st.EntityId, c.Motion, held, inGravity)
                && DebugFlight)
                Log.Info("FLIGHT_EVADE eid=" + st.EntityId
                         + " reason=hit"
                         + " by=" + by
                         + " dir=" + c.Evade.Dir
                         + " dur=" + c.Evade.DurationSec.ToString("F1")
                         + " speed=" + c.Evade.SpeedMs.ToString("F1")
                         + " kind=" + c.Plan.Kind
                         + " target=" + c.TargetId);
        }

        /// <summary>What the closed loop currently holds for a grid: the best braking figure
        /// observed, and how many samples stand behind it. <paramref name="observedMs2"/> is 0 with
        /// no samples, which is distinguishable from a real reading because only a positive achieved
        /// deceleration is ever recorded.</summary>
        private void ReadBrakeRecord(long entityId, out double observedMs2, out int samples)
        {
            BrakeRecord rec;
            if (_brakeAuthority.TryGetValue(entityId, out rec)) { observedMs2 = rec.ObservedMs2; samples = rec.Samples; }
            else { observedMs2 = 0; samples = 0; }
        }

        /// <summary>Braking authority to plan this grid against: the live thrust estimate, held
        /// down to what the grid has actually delivered once enough braking has been seen.
        ///
        /// <para>Everything downstream — the horizon, the approach profile's speed cap, and the
        /// cruise speed the grid is granted for the whole leg — scales with this number, so an
        /// estimate that over-reports does not merely mis-plan, it buys the grid speed it cannot
        /// shed. The observation is the only thing in the loop that has actually been measured.</para></summary>
        private double PlanningDecelMs2(long entityId, double estimateMs2)
        {
            BrakeRecord rec;
            int minSamples = _tuning != null ? _tuning.BrakeObservationMinSamples : 4;
            bool has = _brakeAuthority.TryGetValue(entityId, out rec) && rec.Samples >= minSamples;
            return FlightManoeuvre.PlanningDecelMs2(
                estimateMs2,
                has ? rec.ObservedMs2 : 0,
                has,
                _tuning != null ? _tuning.BrakeObservationSlack : 1.5);
        }

        /// <summary>The cruise speed this grid may be given for its leg — the speed it can still
        /// shed before the terminal bubble, within the deepest horizon the host can observe.
        /// Read by <see cref="VanillaFleetSpawner"/> when it wires the cruise autopilot, and used
        /// here for the horizon and the approach profile so all three agree.
        ///
        /// <para>A grid with no braking observation yet is held to
        /// <see cref="FactionTuning.ApproachCruiseSpeedMs"/> — the speed that has been flying all
        /// along. Nothing goes faster than today until it has demonstrated that it can stop.</para></summary>
        public double CruiseSpeedCapMs(MyCubeGrid grid)
        {
            double fallback = _tuning != null ? _tuning.ApproachCruiseSpeedMs : 100.0;
            if (grid == null || grid.Physics == null) return fallback;

            MyEntityThrustComponent thrust = grid.Components.Get<MyEntityThrustComponent>();
            if (thrust == null) return fallback;

            double estimate = EstimateDecelMs2(grid, thrust, grid.Physics.LinearVelocity);
            if (estimate <= 0) return fallback;

            BrakeRecord rec;
            int minSamples = _tuning != null ? _tuning.BrakeObservationMinSamples : 4;
            bool proven = _brakeAuthority.TryGetValue(grid.EntityId, out rec) && rec.Samples >= minSamples;

            double budget = FlightManoeuvre.BrakeBudgetM(
                (_tuning != null ? _tuning.ObservationRadiusKm : 15.0) * 1000.0,
                _tuning != null ? _tuning.ApproachHorizonMargin   : 1.5,
                _tuning != null ? _tuning.ApproachTerminalRadiusM : 300.0);

            double cap = FlightManoeuvre.CruiseCapMs(
                PlanningDecelMs2(grid.EntityId, estimate),
                budget,
                _tuning != null ? _tuning.ApproachTerminalSpeedMs : 10.0,
                _tuning != null ? _tuning.CruiseSpeedFloorMs      : 40.0,
                CruiseCeilingMs(grid));

            // Unproven grids may be slowed by the clamp but never sped up by it.
            if (!proven && cap > fallback) cap = fallback;
            return cap;
        }

        /// <summary>Upper bound on any granted cruise speed. Zero tuning means the world's own limit
        /// for the hull size, which is the only speed SE will actually let the grid reach —
        /// commanding above it just pins the autopilot at a number it cannot make.</summary>
        public double CruiseCeilingMs(MyCubeGrid grid)
        {
            double configured = _tuning != null ? _tuning.CruiseSpeedCeilingMs : 0.0;
            if (configured > 0) return configured;

            double large, small;
            if (!GridSpeedManager.TryReadCurrent(out large, out small))
                return _tuning != null ? _tuning.ApproachCruiseSpeedMs : 100.0;

            return grid.GridSizeEnum == VRage.Game.MyCubeSize.Small ? small : large;
        }

        /// <summary>Records what the grid's brake achieves over a <b>sustained</b> run of braking.
        ///
        /// <para>Only runs while the profile is commanding a full brake: it is bang-bang, so a zero
        /// direction means the grid was asked for everything it has, and what happens next is its
        /// real authority rather than whatever the speed cap allowed.</para>
        ///
        /// <para><b>Measured over a window, never per tick.</b> A single frame's velocity delta is
        /// dominated by physics noise, and this record keeps the maximum — so per-tick sampling
        /// converges on the largest one-frame artifact rather than on braking authority. Measured
        /// live at 60 samples a second it reported 71 m/s² on a hull whose thrust allows 11, which
        /// made the record exceed the estimate on every grid and the clamp inert.</para>
        ///
        /// <para><b>A run that ends at rest is not a measurement either.</b> Inside the arrival
        /// envelope the profile holds the grid with the same zero vector it brakes with, so runs
        /// keep closing for the whole time a grid sits at its rest point, reading jitter or the
        /// tail of the settle. Which runs count is <see cref="FlightManoeuvre.BrakeSampleMs2"/>'s
        /// decision — this method only owns the run itself.</para></summary>
        private void ObserveBraking(FlightState st, double speedMs, DateTime nowUtc,
                                    double estimateMs2, double arriveSpeedMs)
        {
            if (!st.PrevBrakeCommanded)
            {
                // Not braking: abandon any partial run rather than measuring across the gap.
                st.BrakeRunStartUtc = default(DateTime);
                return;
            }

            if (st.BrakeRunStartUtc == default(DateTime))
            {
                st.BrakeRunStartUtc = nowUtc;
                st.BrakeRunStartSpeedMs = speedMs;
                return;
            }

            double window = _tuning != null ? _tuning.BrakeSampleWindowSeconds : 1.0;
            if (window <= 0) window = 1.0;

            double dt = (nowUtc - st.BrakeRunStartUtc).TotalSeconds;
            if (dt < window) return;

            double achieved = FlightManoeuvre.BrakeSampleMs2(
                dt, st.BrakeRunStartSpeedMs, speedMs, estimateMs2, arriveSpeedMs);

            // Start the next run from here whatever this one produced, so a rejected sample does
            // not wedge the accumulator open.
            st.BrakeRunStartUtc = nowUtc;
            st.BrakeRunStartSpeedMs = speedMs;

            if (achieved <= 0) return;

            BrakeRecord rec;
            if (!_brakeAuthority.TryGetValue(st.EntityId, out rec))
            {
                rec = new BrakeRecord();
                _brakeAuthority[st.EntityId] = rec;
            }
            rec.Samples++;
            if (achieved > rec.ObservedMs2) rec.ObservedMs2 = achieved;
        }

        /// <summary>Runs the clearance pass for a grid's stop point: probes the preferred rest
        /// point (ApproachStopDistanceM short of the target) for parked grids and terrain, and
        /// lets <see cref="ApproachClearance"/> pick a nearby clear point when it is occupied.
        ///
        /// <para>The probe is a direct <see cref="MyEntities.IsShapePenetrating(HkShape, ref
        /// Vector3D, ref Quaternion, Func{IMyEntity, bool}, int, bool)"/> call — one filtered
        /// physics query per candidate. The filter ignores safezones (the destination's own
        /// safezone envelops every candidate) and the manoeuvring grid itself; terrain still
        /// blocks wherever voxel physics exists, which near a station is always. SE's
        /// FindFreePlaceCustom / TestPlaceInSpace helpers are unsuitable here: the former
        /// relocates to random directions outside the arrival envelope, and the latter re-tests
        /// planet-overlapped points through an unfiltered penetration query, so a planetside
        /// safezone would read as permanently blocked.</para></summary>
        private void ResolveStopPoint(FlightState st, Vector3D posWorld, DateTime nowUtc)
        {
            st.LastProbeUtc = nowUtc;
            MyCubeGrid grid = st.Grid;
            if (grid == null || grid.Physics == null) return;

            double radius = grid.PositionComp.WorldVolume.Radius
                * (_tuning != null ? _tuning.ApproachClearanceRadiusFactor : 1.5)
                + (_tuning != null ? _tuning.ApproachClearanceMarginM : 15.0);

            var p = new ClearanceParams
            {
                StopDistanceM = _tuning != null ? _tuning.ApproachStopDistanceM : 50.0,
                MaxStandoffM  = _tuning != null ? _tuning.ApproachMaxStandoffM  : 120.0,
                ProbeRadiusM  = radius,
            };

            long ownId = grid.EntityId;
            Func<IMyEntity, bool> ignore = delegate (IMyEntity e)
            {
                return e is MySafeZone || (e != null && e.EntityId == ownId);
            };

            ClearanceResult result;
            HkShape shape = new HkSphereShape((float)radius);
            try
            {
                // One Havok shape reused across every candidate this pass, per SE's own pattern.
                result = ApproachClearance.Resolve(ToVec3(st.TargetWorld), ToVec3(posWorld), p,
                    delegate (Vec3 pt, double rad)
                    {
                        var probePos = new Vector3D(pt.X, pt.Y, pt.Z);
                        // A spot another inbound grid has already claimed is occupied, even though
                        // nothing is physically there yet. Checked before the physics query because
                        // it is the cheaper of the two and rules out the whole candidate.
                        if (IsClaimedByPeer(ownId, probePos, rad)) return false;
                        Quaternion rot = Quaternion.Identity;
                        return !MyEntities.IsShapePenetrating(shape, ref probePos, ref rot, ignore);
                    });
            }
            finally
            {
                shape.RemoveReference();
            }

            var chosen = new Vector3D(result.StopPointM.X, result.StopPointM.Y, result.StopPointM.Z);

            // Nothing probed clear inside the envelope. Falling back to the preferred point flies
            // the grid into whatever blocked it -- which for a hull too big to fit anywhere near a
            // station is the station. Hold off at the far end of the envelope instead: still the
            // caller's own axis, still inside the radius leg arrival accepts, and out of the
            // geometry rather than in it.
            if (result.Blocked)
            {
                Vector3D axis = chosen - st.TargetWorld;
                double axisLen = axis.Length();
                if (axisLen > 1e-6)
                    chosen = st.TargetWorld + axis / axisLen * (p.StopDistanceM + p.MaxStandoffM);
            }

            bool moved = !st.StopPointWorld.HasValue
                         || (st.StopPointWorld.Value - chosen).LengthSquared() > 0.25;
            st.StopPointWorld = chosen;
            _claimedStopPoints[st.EntityId] = new KeyValuePair<Vector3D, double>(chosen, radius);

            if (moved && DebugFlight)
                Log.Info("FLIGHT_STOPPOINT eid=" + st.EntityId
                         + " adjusted=" + result.Adjusted
                         + " blocked=" + result.Blocked
                         + " offsetM=" + ((chosen - st.TargetWorld).Length() - p.StopDistanceM).ToString("F0")
                         + " probeR=" + radius.ToString("F0"));
        }

        /// <summary>Whether another approach has already claimed a spot overlapping this candidate.
        /// Both clearance spheres have to fit, so the test is against the sum of the two radii.</summary>
        private bool IsClaimedByPeer(long ownId, Vector3D candidate, double radius)
        {
            foreach (KeyValuePair<long, KeyValuePair<Vector3D, double>> kv in _claimedStopPoints)
            {
                if (kv.Key == ownId) continue;
                if (!_controlled.ContainsKey(kv.Key)) continue;   // claim outlived its approach
                double sep = radius + kv.Value.Value;
                if ((kv.Value.Key - candidate).LengthSquared() < sep * sep) return true;
            }
            return false;
        }

        /// <summary>Resolves which way a launch leaves its origin. The launch profile otherwise
        /// thrusts at the destination from the moment it engages, and a grid parked against a
        /// station is routinely on the wrong side of it — a departure straight through the hull,
        /// with SE's own avoidance off because the grid is under native control.
        ///
        /// <para>Probes a corridor rather than a point: a departure is a path, and the thing being
        /// escaped sits along it rather than at its end.</para></summary>
        private void ResolveLaunchDirection(FlightState st, Vector3D posWorld)
        {
            if (_tuning != null && !_tuning.LaunchClearanceProbeEnabled) return;

            MyCubeGrid grid = st.Grid;
            if (grid == null || grid.Physics == null) return;

            double clearanceM = _tuning != null ? _tuning.LaunchClearanceM : 400.0;
            if (clearanceM <= 0) return;

            double radius = grid.PositionComp.WorldVolume.Radius
                * (_tuning != null ? _tuning.ApproachClearanceRadiusFactor : 1.5)
                + (_tuning != null ? _tuning.ApproachClearanceMarginM : 15.0);

            int samples = _tuning != null ? _tuning.LaunchClearanceProbeSamples : 6;
            if (samples < 1) samples = 1;

            long ownId = grid.EntityId;
            Func<IMyEntity, bool> ignore = delegate (IMyEntity e)
            {
                return e is MySafeZone || (e != null && e.EntityId == ownId);
            };

            LaunchClearanceResult result;
            HkShape shape = new HkSphereShape((float)radius);
            try
            {
                result = LaunchClearance.Resolve(ToVec3(posWorld), ToVec3(st.TargetWorld),
                    clearanceM, radius,
                    delegate (Vec3 dir, double len, double rad)
                    {
                        var d = new Vector3D(dir.X, dir.Y, dir.Z);
                        // Skip the origin itself: the grid is standing in its own corridor, and the
                        // dock it is leaving is geometry it is already clear of.
                        for (int i = 1; i <= samples; i++)
                        {
                            Vector3D at = posWorld + d * (len * i / samples);
                            Quaternion rot = Quaternion.Identity;
                            if (MyEntities.IsShapePenetrating(shape, ref at, ref rot, ignore)) return false;
                        }
                        return true;
                    });
            }
            finally
            {
                shape.RemoveReference();
            }

            st.LaunchDirWorld = new Vector3D(result.DirWorld.X, result.DirWorld.Y, result.DirWorld.Z);

            if ((result.Adjusted || result.Blocked) && DebugFlight)
                Log.Info("FLIGHT_LAUNCHDIR eid=" + st.EntityId
                         + " adjusted=" + result.Adjusted
                         + " blocked=" + result.Blocked
                         + " deflectDeg=" + (result.DeflectionRad * 180.0 / Math.PI).ToString("F0")
                         + " probeR=" + radius.ToString("F0"));
        }

        private static MyAutopilotComponent FindAutopilot(MyCubeGrid grid)
        {
            foreach (MyRemoteControl rc in grid.GetFatBlocks<MyRemoteControl>())
            {
                MyAutopilotComponent apc;
                if (rc.GetComponent(out apc) && apc != null) return apc;
            }
            return null;
        }

        /// <summary>Stands every AI block down and returns its previous activation flag, which
        /// <see cref="Release"/> restores. The grid's own wiring cannot be relied on to bring them
        /// back: <c>VanillaFleetSpawner.WireAiAutopilot</c> activates AI blocks only for grids that
        /// have no remote control.</summary>
        /// <summary>Counts a grid's AI blocks three ways: how many report <c>IsActivated</c>, how many
        /// <see cref="MyAiBlockSystem"/> still holds as the active component for their type, and how
        /// many there are. The first two can disagree -- <c>DeactivateComponent</c> declines to drop a
        /// block from the active set under some conditions -- and only the second is what SE's own
        /// thrust path tests.</summary>
        private static void CountAiBlocks(MyCubeGrid grid, out int flagActive, out int sysActive, out int total)
        {
            flagActive = 0; sysActive = 0; total = 0;
            if (grid.GridSystems == null || grid.GridSystems.AiBlockSystem == null) return;

            MyAiBlockSystem system = grid.GridSystems.AiBlockSystem;
            foreach (MyCubeBlock block in system.AiBlocks)
            {
                MyAiBlockComponent comp;
                if (!block.Components.TryGet<MyAiBlockComponent>(out comp) || comp == null) continue;
                total++;
                if (comp.IsActivated) flagActive++;
                if (ReferenceEquals(system.GetActiveAiComponent(comp.AiBlockType), comp)) sysActive++;
            }
        }

        private static List<KeyValuePair<MyAiBlockComponent, bool>> DeactivateAiBlocks(MyCubeGrid grid)
        {
            if (grid.GridSystems == null || grid.GridSystems.AiBlockSystem == null) return null;

            List<KeyValuePair<MyAiBlockComponent, bool>> saved = null;
            foreach (MyCubeBlock block in grid.GridSystems.AiBlockSystem.AiBlocks)
            {
                MyAiBlockComponent comp;
                if (block.Components.TryGet<MyAiBlockComponent>(out comp) && comp != null)
                {
                    if (saved == null) saved = new List<KeyValuePair<MyAiBlockComponent, bool>>();
                    saved.Add(new KeyValuePair<MyAiBlockComponent, bool>(comp, comp.IsActivated));
                    comp.IsActivated = false;
                }
            }
            return saved;
        }

        private static void RestoreAiBlocks(List<KeyValuePair<MyAiBlockComponent, bool>> saved)
        {
            if (saved == null) return;
            for (int i = 0; i < saved.Count; i++) saved[i].Key.IsActivated = saved[i].Value;
        }

        private static Vec3 ToVec3(Vector3D v) { return new Vec3(v.X, v.Y, v.Z); }

        /// <summary>Cosine of the angle between two vectors, 0 when either has no direction. Only
        /// the direction carries diagnostic meaning here, so the magnitudes are divided out.</summary>
        private static double Dot(Vector3 a, Vector3 b)
        {
            double la = a.Length(), lb = b.Length();
            if (la < 1e-9 || lb < 1e-9) return 0;
            return (a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb);
        }
    }
}
