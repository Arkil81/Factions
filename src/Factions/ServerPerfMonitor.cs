using System;
using System.Collections.Generic;
using System.Diagnostics;
using Factions.Core;
using NLog;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Records what the server thread actually did, window by window, and ships it somewhere it can
    /// be plotted against last week.
    ///
    /// <para><b>The gap it fills.</b> <c>perf.json</c> holds one window and is overwritten by the
    /// next, SE's own <c>STATISTICS</c> line is once a minute and carries no simulation ratio at all
    /// (that lives on <c>STATX</c>, behind <c>MyFakes.STATS_LOGGING_HIGH_FREQUENCY</c>, off), and
    /// neither says how much of a slow frame was physics. So a slowdown could be noticed and never
    /// be characterised: by the time anyone looked, the window that contained it was gone.</para>
    ///
    /// <para><b>What it measures, and the one thing it deliberately does not.</b> Tick rate and
    /// simulation ratio; the frame-gap distribution; this plugin's own share of it; the Havok step;
    /// and a census of the standing grid population by what put each grid there. It does not
    /// attribute physics milliseconds to individual grids, because Havok steps a world rather than a
    /// body and that number does not exist — see <see cref="GridCensus"/>.</para>
    ///
    /// <para><b>Cost.</b> Per frame: one property read, one histogram record. Per window: a batch
    /// built into a reused buffer and handed to a background sender. Per census interval: one walk
    /// of the entity list. Nothing here waits on a socket, and nothing allocates per frame.</para>
    /// </summary>
    internal sealed partial class ServerPerfMonitor : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>A gap past this is a visible hitch rather than a busy frame. Same threshold
        /// <c>TICK_STALL</c> uses, so the two channels count the same events.</summary>
        private const double StallMs = 250.0;

        /// <summary>Per-window log line, off by default like every other debug channel. The Influx
        /// series is the standing record; this is for watching a live window without a dashboard.</summary>
        internal static bool DebugLog;

        private readonly ServerPerfConfig _config;
        private readonly InfluxPublisher _publisher;
        private readonly InfluxLineProtocol _batch = new InfluxLineProtocol();
        private readonly GridCensus _census = new GridCensus();
        private readonly HashSet<long> _fleetIds = new HashSet<long>();

        private readonly FrameTimingWindow _gap;
        private readonly FrameTimingWindow _ours;

        private readonly string _hostTag;
        private readonly string _instanceTag;

        private DateTime _windowStartUtc;
        private DateTime _nextCensusUtc;
        private bool _censusValid;
        /// <summary>The census ran in the window being flushed. The per-grid simulated-set rows are
        /// written only then, so a 60 s census is not written twice at a 30 s window.</summary>
        private bool _censusFresh;

        /// <summary>Cap on per-grid simulated-set rows per census, largest grids first. Party
        /// totals always cover every simulated grid.</summary>
        private const int SimGridRowCap = 100;

        /// <summary>Havok step plus world transforms for the window being flushed, ms per frame; NaN
        /// when either half is unbound. Set by <see cref="BuildPhysicsPoint"/>, read by the
        /// ledger, which must attribute the same figure the physics series reports.</summary>
        private double _physicsMsPerFrame = double.NaN;

        /// <summary>The load governor's controller, dry run: evaluated every window the ledger is,
        /// recorded, and read by nothing that acts.</summary>
        private readonly LoadGovernor _governor;

        /// <summary>The governor's current Factions step, read by the runtime's materialise gate
        /// when <c>GovernorFactionsActuate</c> is on.</summary>
        internal int GovernorFactionsStep { get { return _governor.FactionsStep; } }

        internal bool GovernorFactionsActuate { get { return _config.GovernorFactionsActuate; } }

        /// <summary>Supplies the count of fleets held off-screen since the last window; set by the
        /// plugin, which owns the runtime.</summary>
        internal Func<int> HeldOffScreenReader;

        /// <summary>Supplies (waiting, expired) combat-wait counts since the last window.</summary>
        internal Action<int[]> CombatWaitReader;

        /// <summary>Traffic control's budget (docs/traffic-control.md): <see cref="ProbeSpawn"/> records
        /// whether each spawn would have fitted, and each window calibrates the prediction against
        /// the ledger. The presence rule reads its Factions budget and multipliers.</summary>
        internal readonly TrafficBudget Traffic;

        /// <summary>The load governor's current toolbox step for a player, by party name; 0 when
        /// they are not over their share. Computed whether or not the toolbox acts.</summary>
        internal int GovernorPlayerStep(string party) { return _governor.PlayerStep(party); }

        /// <summary>One materialised fleet as the budget sees it: the grids it put in the world and
        /// its unscaled predicted cost.</summary>
        internal struct FleetLoad
        {
            public long LeadGridId;
            public List<long> EscortGridIds;
            public TrafficClass Class;
            public double BaseMs;
        }

        /// <summary>Fills the list with every materialised fleet; set by the plugin, which owns the
        /// runtime and the prefab catalogue.</summary>
        internal Action<List<FleetLoad>> FleetLoadReader;

        /// <summary>The presence rule's current plan, null while it is off; and (refused, shed) since
        /// the last window. Set by the plugin, which owns the runtime.</summary>
        internal Func<TrafficPresence.Plan> PresencePlanReader;
        internal Action<int[]> PresenceCountReader;
        /// <summary>Station-queue events since the last window: joined, cleared, diverted, delay called.</summary>
        internal Action<int[]> QueueCountReader;

        private readonly List<FleetLoad> _fleetLoads = new List<FleetLoad>();
        private int _trafficUnknownHulls;

        internal TimeSpan GovernorCombatMaxWait { get { return TimeSpan.FromSeconds(_config.GovernorCombatMaxWaitSeconds); } }

        /// <summary>Player toolbox step 3: which block classes each player has paused. Acts only when
        /// <c>GovernorPlayersThrottle</c> is on; the admin override works either way.</summary>
        private readonly PlayerThrottlePlanner _throttle;

        /// <summary>Tells a player, by party name, that a class of their blocks was paused or
        /// released. Set by the plugin, which owns the chat manager.</summary>
        internal Action<string, string> PlayerNotifier;

        /// <summary>This window's ms per grid and class for the pausable classes, drained each flush.</summary>
        private Dictionary<long, double[]> _classCostMs = new Dictionary<long, double[]>();

        private long _frames;
        private long _stalls;
        private double _simRatioSum;
        private double _simRatioMin = double.MaxValue;
        private long _simRatioSamples;
        private long _censusTicks;

        internal ServerPerfMonitor(ServerPerfConfig config, string instanceDir)
        {
            _config = config;

            _hostTag = !string.IsNullOrEmpty(config.HostTag) ? config.HostTag : SafeMachineName();
            _instanceTag = !string.IsNullOrEmpty(config.InstanceTag)
                ? config.InstanceTag
                : SafeInstanceName(instanceDir);

            // The ring has to cover the window it serves, or the percentile describes the tail while
            // count and average describe the whole -- FrameTimingWindow's own warning. 60 Hz plus
            // headroom for a window that overruns its interval.
            int samples = Math.Max(1024, config.WindowSeconds * 80);
            _gap = new FrameTimingWindow(Stopwatch.Frequency, StallMs, samples);
            _ours = new FrameTimingWindow(Stopwatch.Frequency, 0.5, samples);

            if (config.CanPublish) _publisher = new InfluxPublisher(config);

            _governor = new LoadGovernor(new LoadGovernor.Settings
            {
                FrameBudgetMs = FrameBudgetMs,
                AdjustBusyPct = config.GovernorAdjustBusyPct,
                ReleaseBusyPct = config.GovernorReleaseBusyPct,
                SimFloor = config.GovernorSimFloor,
                IntervalSeconds = config.GovernorIntervalSeconds,
                FactionsStepPct = config.GovernorFactionsStepPct,
                FactionsFloorMs = config.GovernorFactionsFloorMs,
                FactionsMaxSteps = config.GovernorFactionsMaxSteps,
            });

            _throttle = new PlayerThrottlePlanner(config.GovernorIntervalSeconds);
            Traffic = new TrafficBudget { WindowSeconds = config.WindowSeconds };
            if (config.PbLimitEnabled)
                PbLimiterPatches.Limiter = new PbLimiter(new PbLimiter.Settings
                {
                    CapMsPerFrame = config.PbLimitCapMsPerFrame,
                    PressureCapMsPerFrame = config.PbLimitPressureCapMsPerFrame,
                    StopRunMs = config.PbLimitStopRunMs,
                    StopWindows = Math.Max(1, (int)Math.Ceiling(config.PbLimitStopSeconds / config.WindowSeconds)),
                    StopSeconds = config.PbLimitRetrySeconds,
                });
            BlockThrottle.PartyOfGrid = id => { string party; return _census.PartyByGrid.TryGetValue(id, out party) ? party : null; };

            _windowStartUtc = DateTime.UtcNow;
            _nextCensusUtc = DateTime.UtcNow;   // first census on the first flush
        }

        /// <summary>Whether anything is being sent. Collection without a destination is a supported
        /// state, so this is not the same question as "is it running".</summary>
        internal bool Publishing { get { return _publisher != null; } }

        /// <summary>Called once per frame from <c>ProbeFrameGap</c>, before the session-ready test:
        /// a stall while the simulation is deferred is still a stall, and a window that silently
        /// excluded them would report the server as healthy through a load.</summary>
        /// <summary>The 60 tick/s frame budget, in ms. Busy and idle are shares of this.</summary>
        private const double FrameBudgetMs = 1000.0 / 60.0;

        /// <summary>A frame at or above this share of the budget counts as saturated -- the
        /// governor's adjustment trigger (docs/load-governor.md).</summary>
        private const double SaturatedShare = 0.95;

        /// <summary>SE's own main-thread time for each frame, summed over the window.
        ///
        /// <para><b>Why not <c>CPULoadSmooth</c>.</b> That is the same quantity, but exponentially
        /// smoothed and sampled here once per window, so a window's figure is whatever the last
        /// few frames happened to be. <c>MySandboxGame.FrameTimeTicks</c> is written every frame
        /// by <c>RunSingleFrame</c> around <c>UpdateInternal</c> -- everything the game thread
        /// does, physics included -- and this hook runs once per frame, so summing it is exact.
        /// It is the previous frame's value, which is the only complete one available from
        /// inside a frame.</para></summary>
        private long _busyTicks;
        private long _busyMaxTicks;
        private long _busySamples;
        private long _busySaturated;
        private static readonly long SaturatedTicks =
            (long)(FrameBudgetMs * SaturatedShare * Stopwatch.Frequency / 1000.0);

        internal void SampleFrame(long gapTicks, long ourTicks)
        {
            _frames++;
            _gap.Record(gapTicks);
            _ours.Record(ourTicks);
            if (gapTicks * 1000.0 / Stopwatch.Frequency >= StallMs) _stalls++;

            try
            {
                MySandboxGame game = MySandboxGame.Static;
                long busy = game != null ? game.FrameTimeTicks : 0;
                if (busy > 0)
                {
                    _busyTicks += busy;
                    _busySamples++;
                    if (busy > _busyMaxTicks) _busyMaxTicks = busy;
                    if (busy >= SaturatedTicks) _busySaturated++;
                }
            }
            catch { /* reported absent rather than zero */ }

            // Physics' own view of whether it is keeping up. Cheap -- a rounded division of a step
            // count SE already maintains -- but wrapped, because it is reachable before a session.
            try
            {
                float ratio = MyPhysics.SimulationRatio;
                if (ratio > 0f && !float.IsNaN(ratio))
                {
                    _simRatioSum += ratio;
                    _simRatioSamples++;
                    if (ratio < _simRatioMin) _simRatioMin = ratio;
                }
            }
            catch { /* no session yet; the window reports what it has */ }
        }

        /// <summary>Closes the window if it is due and starts the next. Called once per frame from
        /// the plugin's update, after the session-ready test.</summary>
        /// <param name="sessionReady">False suppresses the census only — the frame statistics are
        /// still worth a point, and a long load is exactly when the gap distribution matters.</param>
        internal void TickFlush(DateTime nowUtc,
                               bool sessionReady,
                               HashSet<long> fleetGridIds,
                               Func<long, bool> isFlying,
                               Func<long, bool> isStationGrid,
                               Func<VRageMath.Vector3D, bool> isNearAnyPlayer)
        {
            double windowSec = (nowUtc - _windowStartUtc).TotalSeconds;
            if (windowSec < _config.WindowSeconds) return;

            if (sessionReady && nowUtc >= _nextCensusUtc)
            {
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    _fleetIds.Clear();
                    if (fleetGridIds != null) foreach (long id in fleetGridIds) _fleetIds.Add(id);
                    _census.Run(_fleetIds, isFlying, isStationGrid, isNearAnyPlayer);
                    _censusValid = true;
                    _censusFresh = true;
                }
                catch (Exception ex)
                {
                    _censusValid = false;
                    Log.Warn("Server perf: grid census failed: " + ex.Message);
                }
                _censusTicks = Stopwatch.GetTimestamp() - t0;
                _nextCensusUtc = nowUtc.AddSeconds(_config.CensusSeconds);
            }

            try { Flush(nowUtc, windowSec); }
            catch (Exception ex) { Log.Warn("Server perf: flush failed: " + ex.Message); }
            _censusFresh = false;

            _windowStartUtc = nowUtc;
            _frames = 0;
            _stalls = 0;
            _simRatioSum = 0;
            _simRatioSamples = 0;
            _simRatioMin = double.MaxValue;
            _gap.Reset();
            _ours.Reset();
            _busyTicks = 0;
            _busyMaxTicks = 0;
            _busySamples = 0;
            _busySaturated = 0;
        }

        private void Flush(DateTime nowUtc, double windowSec)
        {
            long timestamp = InfluxLineProtocol.ToUnixNanos(nowUtc);
            double tickRate = _frames / windowSec;
            double simRatio = _simRatioSamples > 0 ? _simRatioSum / _simRatioSamples : 0.0;
            double simRatioMin = _simRatioMin == double.MaxValue ? 0.0 : _simRatioMin;

            long stepTicks, steps, stepMaxTicks;
            PhysicsStepPatches.TakeStepTicks(out stepTicks, out steps, out stepMaxTicks);
            double stepTotalMs = ToMs(stepTicks);
            double stepAvgMs = steps > 0 ? stepTotalMs / steps : 0.0;

            // Drained here, above the publisher test, because draining IS the clear. The wrappers
            // accumulate whenever EntityCostPatches.Enabled, publisher or not, so leaving this below
            // the early return would grow the tallies for the life of the process on a host
            // configured to collect without publishing -- which is a supported configuration.
            // TakeStepTicks above has the same property, for the same reason.
            EntityCostPatches.Window entityCost =
                EntityCostPatches.Bound
                    ? EntityCostPatches.Take(_config.EntityCostTopN,
                                             _censusValid && _census.SimSetValid ? (Func<long, int>)GridClassOf : null)
                    : null;
            if (entityCost != null && !EntityCostPatches.Enabled) entityCost = null;
            _classCostMs = BlockThrottle.TakeCostMs();
            List<PbCostSummary.Block> pbCost = PbCostPatches.Bound ? PbCostPatches.Take() : null;
            if (pbCost != null && !EntityCostPatches.Enabled) pbCost = null;
            if (pbCost != null) AddScriptCostToClassCost(pbCost);
            EvaluateLimiter(nowUtc);

            if (DebugLog)
                Log.Info("SERVER_PERF " + windowSec.ToString("F0") + "s"
                         + " tickRate=" + tickRate.ToString("F1")
                         + " simRatio=" + simRatio.ToString("F2") + "/" + simRatioMin.ToString("F2")
                         + " gap=" + _gap.AverageMilliseconds().ToString("F2")
                             + "/" + _gap.PercentileMilliseconds(0.99).ToString("F1")
                             + "/" + _gap.MaxMilliseconds().ToString("F1") + "ms"
                         + " stalls=" + _stalls
                         + " ours=" + _ours.AverageMilliseconds().ToString("F2") + "ms"
                         + " step=" + stepAvgMs.ToString("F2") + "/" + ToMs(stepMaxTicks).ToString("F1") + "ms"
                         + (_censusValid ? " grids=" + _census.TotalGrids
                                           + " approach=" + _census[GridOrigin.FleetApproach].Grids
                                           + " npcLoose=" + _census[GridOrigin.NpcLoose].Grids
                                           + " player=" + _census[GridOrigin.Player].Grids
                                           + " moving=" + MovingTotal()
                                         : " grids=n/a"));

            // A fresh census can carry new grids or blocks for a player already paused. Above the
            // publisher test: pausing must not depend on whether anything is being plotted.
            if (_censusValid && _censusFresh && _throttle.Any)
                BlockThrottle.Rebuild(_throttle, _census.PartyByGrid);

            // The points are built with or without a publisher: the governor and the traffic budget
            // read the same pass, and a host with nothing to plot still needs both to act.
            _batch.Reset();
            _batch.CommonTag("host", _hostTag).CommonTag("instance", _instanceTag);

            BuildServerPoint(timestamp, windowSec, tickRate, simRatio, simRatioMin);
            BuildPhysicsPoint(timestamp, steps, stepTotalMs, stepAvgMs, stepMaxTicks);
            if (_censusValid) BuildCensusPoints(timestamp);
            if (_censusValid && _censusFresh && _census.SimSetValid) BuildSimSetPoints(timestamp);
            if (entityCost != null) BuildEntityCostPoints(entityCost, timestamp);
            PbCostSummary.Result pbSummary = pbCost != null ? BuildPbCostPoints(pbCost, timestamp) : null;
            _tailFrames = 0;
            _tailStallMs = 0;
            _tailByCause.Clear();
            if (HitchProbe.Bound) BuildHitchPoints(timestamp);
            BuildSpawnPacerPoint(timestamp);
            BuildLimiterPoints(timestamp);
            LoadLedger.Result ledger = null;
            if (entityCost != null && entityCost.AllGrids != null && _censusValid && _census.SimSetValid)
            {
                ledger = BuildLedgerPoints(entityCost, timestamp);
                if (ledger != null && _busySamples > 0)
                    EvaluateGovernor(ledger, simRatio, timestamp);
                if (ledger != null) ObservePrefabCosts(entityCost, ledger.ParallelScale);
            }
            if (_busySamples > 0)
                EvaluateTraffic(ledger, nowUtc, windowSec, timestamp);

            try { _lastTop = BuildTop(nowUtc, windowSec, entityCost, ledger, pbSummary); }
            catch (Exception ex) { _lastTop = null; Log.Warn("Server perf: top snapshot failed: " + ex.Message); }

            if (_publisher != null) _publisher.Enqueue(_batch.Build());
        }

        /// <summary>A window total spread over the frames that ran in it — the unit an SE server
        /// operator budgets in (16.67 ms/frame at 60 tick/s).
        ///
        /// <para><b>Per frame, not per call.</b> <c>stepAvgMs</c> divides by Havok steps and
        /// <c>transformAvgMs</c> by transform passes; both usually equal the frame count, but not
        /// when SE runs zero or two physics steps in a frame, and then a per-call average stops
        /// being a share of the frame. The window total over <see cref="_frames"/> always is, and
        /// the per-frame figures of different subsystems add up against the same budget.</para>
        ///
        /// <para>The <c>*TotalMs</c> fields stay beside these so existing history and queries keep
        /// working.</para></summary>
        private static Process _self;
        private long _allocatedAtFlush;
        private int _gen2AtFlush, _gen0AtFlush, _gen1AtFlush;
        private static bool _gcModeLogged;

        private double PerFrame(double totalMs)
        {
            return _frames > 0 ? Math.Round(totalMs / _frames, 4) : 0.0;
        }

        /// <summary><see cref="PerFrame"/> without the rounding, for inputs that are summed again:
        /// a hundred grids each rounded to 0.0001 ms would lose what they add up to.</summary>
        private double PerFrameRaw(double totalMs)
        {
            return _frames > 0 ? totalMs / _frames : 0.0;
        }

        private void BuildServerPoint(long timestamp, double windowSec,
                                      double tickRate, double simRatio, double simRatioMin)
        {
            var p = _batch.Point("torch_server", timestamp)
                .Field("windowSeconds", Math.Round(windowSec, 1))
                .Field("tickRate", Math.Round(tickRate, 2))
                .Field("simRatio", Math.Round(simRatio, 3))
                .Field("simRatioMin", Math.Round(simRatioMin, 3))
                .Field("frames", _frames)
                .Field("stalls", _stalls)
                .Field("frameGapAvgMs", Math.Round(_gap.AverageMilliseconds(), 3))
                .Field("frameGapP99Ms", Math.Round(_gap.PercentileMilliseconds(0.99), 3))
                .Field("frameGapMaxMs", Math.Round(_gap.MaxMilliseconds(), 3))
                .Field("factionsAvgMs", Math.Round(_ours.AverageMilliseconds(), 4))
                .Field("factionsMaxMs", Math.Round(_ours.MaxMilliseconds(), 3))
                // The plugin's total share of the window, which an average cannot express: this is
                // what "is Factions the problem" is actually answered against.
                .Field("factionsTotalMs", Math.Round(_ours.TotalMilliseconds(), 1))
                .Field("factionsMsPerFrame", PerFrame(_ours.TotalMilliseconds()))
                .Field("censusMs", Math.Round(ToMs(_censusTicks), 3));

            // Headroom. Sim ratio is capped at 1.0 and so can only show its absence; this shows how
            // much of the frame is left before sim starts to fall.
            if (_busySamples > 0)
            {
                double busyMs = ToMs(_busyTicks) / _busySamples;
                double busyPct = busyMs / FrameBudgetMs * 100.0;
                p.Field("busyMsPerFrame", Math.Round(busyMs, 4))
                 .Field("busyMaxMs", Math.Round(ToMs(_busyMaxTicks), 3))
                 .Field("busyPct", Math.Round(busyPct, 2))
                 .Field("idlePct", Math.Round(Math.Max(0.0, 100.0 - busyPct), 2))
                 // Share of frames that used 95% of the budget or more: the governor's trigger,
                 // seen frame by frame rather than as an average that a few idle frames dilute.
                 .Field("saturatedFramePct", Math.Round(100.0 * _busySaturated / _busySamples, 2));
            }

            // SE's own smoothed load figures, the same two the STATISTICS line carries -- so a
            // Grafana panel and a log archaeology pass agree rather than diverge.
            try
            {
                MySandboxGame game = MySandboxGame.Static;
                if (game != null)
                {
                    p.Field("cpuLoadSmooth", Math.Round((double)game.CPULoadSmooth, 2));
                    p.Field("threadLoadSmooth", Math.Round((double)game.ThreadLoadSmooth, 2));
                }
            }
            catch { /* reported absent rather than zero */ }

            try
            {
                p.Field("gcMemoryMB", Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1));
                // Environment.WorkingSet wraps at 4 GB in this process; Process reads the full 64-bit figure.
                if (_self == null) _self = Process.GetCurrentProcess(); else _self.Refresh();
                p.Field("processMemoryMB", Math.Round(_self.WorkingSet64 / 1048576.0, 1));
                p.Field("privateMemoryMB", Math.Round(_self.PrivateMemorySize64 / 1048576.0, 1));
            }
            catch { }

            // gcMemoryMB counts garbage not yet collected. The survived figure is what outlived the most recent
            // collection of any generation, so it is the live heap only in a window that saw a gen-2.
            try
            {
                if (!AppDomain.MonitoringIsEnabled) AppDomain.MonitoringIsEnabled = true;
                long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                int gen2 = GC.CollectionCount(2);
                int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1);
                if (!_gcModeLogged)
                {
                    _gcModeLogged = true;
                    Log.Info("GC mode: server=" + System.Runtime.GCSettings.IsServerGC
                             + " latency=" + System.Runtime.GCSettings.LatencyMode
                             + " heaps(cpus)=" + Environment.ProcessorCount
                             + " COMPlus_GCgen0size=" + (Environment.GetEnvironmentVariable("COMPlus_GCgen0size") ?? "unset"));
                }
                p.Field("gcSurvivedMB", Math.Round(AppDomain.MonitoringSurvivedProcessMemorySize / 1048576.0, 1));
                if (_allocatedAtFlush > 0 && windowSec > 0)
                {
                    p.Field("gcAllocMBps", Math.Round((allocated - _allocatedAtFlush) / 1048576.0 / windowSec, 1));
                    p.Field("gc2Count", (double)(gen2 - _gen2AtFlush));
                    // gen0Count counts every collection (a gen1 is also a gen0), gen1Count those that reached gen1.
                    p.Field("gc0Count", (double)(gen0 - _gen0AtFlush));
                    p.Field("gc1Count", (double)(gen1 - _gen1AtFlush));
                }
                _gen0AtFlush = gen0;
                _gen1AtFlush = gen1;
                _allocatedAtFlush = allocated;
                _gen2AtFlush = gen2;
            }
            catch { }

            try
            {
                // "players" counts people; SE's own count, which includes its bot player objects (crew
                // and NPC characters), is "sessionPlayers".
                if (MySession.Static != null && MySession.Static.Players != null)
                {
                    long humans = 0;
                    foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
                        if (player != null && !player.IsBot && player.Identity != null) humans++;
                    p.Field("players", humans);
                    p.Field("sessionPlayers", (long)MySession.Static.Players.GetOnlinePlayerCount());
                }
            }
            catch { }

            if (_publisher != null)
            {
                // Rides on the telemetry rather than in a series of its own so a gap in the graph
                // and the reason for it are read off the same panel.
                p.Field("publishFailed", _publisher.FailedBatches)
                 .Field("publishDropped", _publisher.DroppedBatches)
                 .Field("publishQueue", (long)_publisher.QueueDepth);
            }

            p.End();
        }

        private void BuildPhysicsPoint(long timestamp, long steps,
                                       double stepTotalMs, double stepAvgMs, long stepMaxTicks)
        {
            _physicsMsPerFrame = double.NaN;
            var p = _batch.Point("torch_physics", timestamp)
                .Field("bound", PhysicsStepPatches.Bound)
                .Field("havokMemoryMB", Math.Round(PhysicsStepPatches.HavokMemoryBytes / 1048576.0, 1))
                .Field("havokMemoryMaxMB", Math.Round(PhysicsStepPatches.TakeHavokMemoryMax() / 1048576.0, 1));

            if (PhysicsStepPatches.Bound)
            {
                p.Field("steps", steps)
                 .Field("stepAvgMs", Math.Round(stepAvgMs, 4))
                 .Field("stepMaxMs", Math.Round(ToMs(stepMaxTicks), 3))
                 // The share of the window Havok owned. Against factionsTotalMs on the same
                 // timestamp, this is the comparison the whole instrument exists for.
                 .Field("stepTotalMs", Math.Round(stepTotalMs, 1))
                 .Field("stepMsPerFrame", PerFrame(stepTotalMs));
            }

            int active, total, clusters, islands, constraints;
            PhysicsStepPatches.BodyKinds kinds;
            if (PhysicsStepPatches.TryReadBodies(out active, out total, out clusters,
                                                 out islands, out constraints, out kinds))
            {
                p.Field("activeBodies", (long)active)
                 .Field("totalBodies", (long)total)
                 .Field("clusters", (long)clusters)
                 // activeBodies / islands is the size of the thing Havok actually solves as a unit;
                 // constraints is what the 8 solver iterations run over.
                 .Field("islands", (long)islands)
                 .Field("constraints", (long)constraints)
                 .Field("actions", (long)PhysicsStepPatches.LastActionCount)
                 // What the active set is made of. Read rather than assumed: the count has sat far
                 // above the grid count all along and nothing said what the remainder was.
                 .Field("activeGrids", (long)kinds.Grids)
                 .Field("activeCharacters", (long)kinds.Characters)
                 .Field("activeFloating", (long)kinds.Floating)
                 .Field("activeDetached", (long)kinds.Detached)
                 .Field("activeOther", (long)kinds.Other);
            }

            // The other half of SimulateInternal, and the half the step figure never covered.
            long tTicks, tCalls, tMaxTicks;
            PhysicsStepPatches.TakeTransformTicks(out tTicks, out tCalls, out tMaxTicks);
            p.Field("transformBound", PhysicsStepPatches.TransformBound);
            if (PhysicsStepPatches.TransformBound)
            {
                double tTotalMs = ToMs(tTicks);
                p.Field("transformTotalMs", Math.Round(tTotalMs, 1))
                 .Field("transformAvgMs", Math.Round(tCalls > 0 ? tTotalMs / tCalls : 0.0, 4))
                 .Field("transformMaxMs", Math.Round(ToMs(tMaxTicks), 3))
                 .Field("transformCalls", tCalls)
                 .Field("transformMsPerFrame", PerFrame(tTotalMs));
                // Both halves of MyPhysics.SimulateInternal, which is what "physics" costs a frame.
                if (PhysicsStepPatches.Bound)
                {
                    _physicsMsPerFrame = PerFrame(stepTotalMs + tTotalMs);
                    p.Field("physicsMsPerFrame", _physicsMsPerFrame);
                }
            }

            p.End();

            // One point per distinct entity type behind an active body. A tagged series rather than
            // a field per type: the set is small but not fixed, and a new type must not need a code
            // change to become visible.
            foreach (KeyValuePair<string, int> kv in PhysicsStepPatches.ActiveBodyTypes)
            {
                _batch.Point("active_body_types", timestamp)
                    .Tag("type", kv.Key)
                    .Field("count", (long)kv.Value)
                    .End();
            }
        }

        /// <summary>The load ledger (load governor, build step 3): logic measured and physics
        /// predicted per simulated grid, physics normalised to this window's measured total, summed
        /// per party by owner. Read-only. The census it uses is up to one census interval old.</summary>
        private LoadLedger.Result BuildLedgerPoints(EntityCostPatches.Window w, long timestamp)
        {
            var simulated = new List<LoadLedger.SimulatedGrid>(_census.Simulated.Count);
            foreach (GridCensus.SimGridRow row in _census.Simulated)
                simulated.Add(new LoadLedger.SimulatedGrid
                {
                    GridId = row.GridId, Party = row.Party, Name = row.Name,
                    Blocks = row.Blocks, Awake = row.Awake, IsStatic = row.IsStatic,
                });

            var logic = new Dictionary<long, LoadLedger.GridLogic>(w.AllGrids.Count);
            foreach (KeyValuePair<long, EntityCostPatches.GridTicks> kv in w.AllGrids)
                logic[kv.Key] = new LoadLedger.GridLogic
                {
                    SerialMs = PerFrameRaw(ToMs(kv.Value.Serial)),
                    ParallelCpuMs = PerFrameRaw(ToMs(kv.Value.Parallel)),
                };

            var coefficients = new LoadLedger.Coefficients
            {
                SizeExponent = _config.LedgerSizeExponent,
                AwakeMultiplier = _config.LedgerAwakeMultiplier,
            };
            LoadLedger.Result r = LoadLedger.Compute(simulated, logic,
                id => { string party; return _census.PartyByGrid.TryGetValue(id, out party) ? party : null; },
                _physicsMsPerFrame,
                PerFrameRaw(ToMs(w.ParallelWallTicks)),
                PerFrameRaw(ToMs(w.ParallelTicks)),
                coefficients);

            double attributed = 0;
            foreach (LoadLedger.PartyCost pc in r.Parties.Values) attributed += pc.TotalMs;

            _batch.Point("ledger", timestamp)
                .Field("measuredPhysicsMsPerFrame", Math.Round(r.MeasuredPhysicsMs, 4))
                .Field("unattributedPhysicsMsPerFrame", Math.Round(r.UnattributedPhysicsMs, 4))
                .Field("logicMsPerFrame", Math.Round(r.LogicMs, 4))
                .Field("attributedMsPerFrame", Math.Round(attributed, 4))
                .Field("parallelScale", Math.Round(r.ParallelScale, 4))
                .Field("sizeExponent", coefficients.SizeExponent)
                .Field("awakeMultiplier", coefficients.AwakeMultiplier)
                .End();

            foreach (KeyValuePair<string, LoadLedger.PartyCost> kv in r.Parties)
            {
                LoadLedger.PartyCost pc = kv.Value;
                _batch.Point("ledger_party", timestamp)
                    .Tag("party", kv.Key)
                    .Field("logicMsPerFrame", Math.Round(pc.LogicMs, 4))
                    .Field("physicsMsPerFrame", Math.Round(pc.PhysicsMs, 4))
                    .Field("totalMsPerFrame", Math.Round(pc.TotalMs, 4))
                    .Field("simulatedGrids", (long)pc.SimulatedGrids)
                    .Field("sharePct", attributed > 0 ? Math.Round(100.0 * pc.TotalMs / attributed, 2) : 0.0)
                    .End();
            }

            int n = Math.Min(_config.LedgerTopN, r.Grids.Count);
            for (int i = 0; i < n; i++)
            {
                LoadLedger.GridCost g = r.Grids[i];
                _batch.Point("ledger_grid", timestamp)
                    .Tag("gridId", g.GridId.ToString())
                    .Tag("grid", (g.Name ?? "").Replace('\\', '/'))
                    .Tag("party", g.Party)
                    .Field("logicMsPerFrame", Math.Round(g.LogicMs, 4))
                    .Field("physicsMsPerFrame", Math.Round(g.PhysicsMs, 4))
                    .Field("totalMsPerFrame", Math.Round(g.TotalMs, 4))
                    .End();
            }
            return r;
        }

        /// <summary>The load governor, dry run (build step 4). Factions' cost is its grids in the
        /// ledger plus this plugin's own tick; every other named party is a player.</summary>
        private void EvaluateGovernor(LoadLedger.Result ledger, double simRatio, long timestamp)
        {
            var input = new LoadGovernor.Input
            {
                BusyMs = ToMs(_busyTicks) / _busySamples,
                SimRatio = simRatio > 0 ? simRatio : 1.0,
                FactionsMs = PerFrameRaw(_ours.TotalMilliseconds()),
                TailFrames = _tailFrames,
                TailStallMs = _tailStallMs,
                FastPath = true,
            };
            foreach (KeyValuePair<string, double> kv in _tailByCause) input.TailStallByCause[kv.Key] = kv.Value;
            foreach (KeyValuePair<string, LoadLedger.PartyCost> kv in ledger.Parties)
            {
                if (kv.Key == SimSetTally.Factions) input.FactionsMs += kv.Value.TotalMs;
                else if (kv.Key == SimSetTally.Unowned || kv.Key == LoadLedger.World || kv.Key == LoadLedger.Unknown) continue;
                else input.PlayerMs[kv.Key] = kv.Value.TotalMs;
            }

            LoadGovernor.Decision d = _governor.Evaluate(input, DateTime.UtcNow);

            int over = 0;
            foreach (LoadGovernor.PlayerView v in d.Players) if (v.Over) over++;
            var waits = new int[2];
            if (CombatWaitReader != null) CombatWaitReader(waits);
            _batch.Point("governor", timestamp)
                .Field("pressure", d.Pressure.ToString())
                .Field("short", d.Pressure == LoadGovernor.Pressure.Short)
                .Field("cause", d.Cause)
                .Field("busyPct", Math.Round(d.BusyPct, 2))
                .Field("shortMs", Math.Round(d.ShortMs, 4))
                .Field("otherMs", Math.Round(d.OtherMs, 4))
                .Field("playerPoolMs", Math.Round(d.PlayerPoolMs, 4))
                .Field("factionsMs", Math.Round(d.FactionsMs, 4))
                .Field("factionsTargetMs", Math.Round(d.FactionsTargetMs, 4))
                .Field("factionsStep", (long)d.FactionsStep)
                .Field("players", (long)d.Players.Count)
                .Field("overSharePlayers", (long)over)
                .Field("changed", d.Changed)
                .Field("action", d.Action)
                .Field("actuating", _config.GovernorFactionsActuate)
                .Field("heldOffScreen", (long)(HeldOffScreenReader != null ? HeldOffScreenReader() : 0))
                .Field("combatWaiting", (long)waits[0])
                .Field("combatWaitExpired", (long)waits[1])
                .Field("playersThrottle", _config.GovernorPlayersThrottle)
                .Field("pausedBlocks", (long)BlockThrottle.Count)
                .Field("tailPressure", d.TailPressure.ToString())
                .Field("tailFrames", (long)d.TailFrames)
                .Field("tailStallMs", Math.Round(d.TailStallMs, 1))
                .Field("tailFactionsMs", Math.Round(d.TailFactionsMs, 1))
                .Field("tailTopCause", d.TailTopCause)
                .Field("tailTopCauseMs", Math.Round(d.TailTopCauseMs, 1))
                .Field("stallBackoff", Math.Round(ApplyStallBackoff(d), 3))
                .End();

            if (d.TailPressure != _lastTailPressure && DebugTraces.Governor)
                Log.Info("GOVERNOR_TAIL " + _lastTailPressure + "->" + d.TailPressure + " frames=" + d.TailFrames
                         + " stallMs=" + d.TailStallMs.ToString("F0") + " factionsMs=" + d.TailFactionsMs.ToString("F0")
                         + " top=" + (d.TailTopCause.Length > 0 ? d.TailTopCause : "-") + ":" + d.TailTopCauseMs.ToString("F0") + "ms");
            _lastTailPressure = d.TailPressure;

            Dictionary<string, double[]> classMs = ClassCostByParty();
            var states = new List<PlayerThrottlePlanner.PlayerState>();
            foreach (LoadGovernor.PlayerView v in d.Players)
                states.Add(new PlayerThrottlePlanner.PlayerState { Party = v.Party, Step = v.Step, Over = v.Over });
            List<PlayerThrottlePlanner.Change> changes = _config.GovernorPlayersThrottle
                ? _throttle.Update(states, (party, c) => { double[] m; return classMs.TryGetValue(party, out m) ? m[(int)c] : 0.0; }, DateTime.UtcNow)
                : _throttle.ReleaseAllGoverned();
            ApplyThrottleChanges(changes);

            foreach (LoadGovernor.PlayerView v in d.Players)
            {
                _batch.Point("governor_party", timestamp)
                    .Tag("party", v.Party)
                    .Field("costMs", Math.Round(v.CostMs, 4))
                    .Field("shareMs", Math.Round(v.ShareMs, 4))
                    .Field("over", v.Over)
                    .Field("step", (long)v.Step)
                    .Field("pausedClasses", (long)_throttle.ClassesFor(v.Party).Count)
                    .End();
            }
            BuildThrottlePoints(classMs, timestamp);

            if (d.Changed && DebugTraces.Governor)
                Log.Info("GOVERNOR " + d.Action + " pressure=" + d.Pressure + " cause=" + d.Cause
                         + " busy=" + d.BusyPct.ToString("F1") + "% short=" + d.ShortMs.ToString("F2") + "ms"
                         + " factions=" + d.FactionsMs.ToString("F2") + "->" + d.FactionsTargetMs.ToString("F2") + "ms"
                         + " pool=" + d.PlayerPoolMs.ToString("F2") + "ms" + DescribePlayers(d));
        }

        /// <summary>The window's pausable-class cost summed per party, ms per frame. A grid the census
        /// has not seen is left out: it cannot be charged, and it cannot be paused either.</summary>
        private Dictionary<string, double[]> ClassCostByParty()
        {
            var byParty = new Dictionary<string, double[]>(StringComparer.Ordinal);
            if (_frames <= 0) return byParty;
            foreach (KeyValuePair<long, double[]> kv in _classCostMs)
            {
                string party;
                if (!_census.PartyByGrid.TryGetValue(kv.Key, out party) || string.IsNullOrEmpty(party)) continue;
                double[] sum;
                if (!byParty.TryGetValue(party, out sum)) { sum = new double[kv.Value.Length]; byParty[party] = sum; }
                for (int i = 0; i < sum.Length; i++) sum[i] += kv.Value[i] / _frames;
            }
            return byParty;
        }

        /// <summary>Rebuilds the paused set, then tells each affected player and, behind
        /// <c>factions debug governor</c>, the log.</summary>
        private void ApplyThrottleChanges(List<PlayerThrottlePlanner.Change> changes)
        {
            if (changes == null || changes.Count == 0) return;
            BlockThrottle.Rebuild(_throttle, _census.PartyByGrid);
            foreach (PlayerThrottlePlanner.Change c in changes)
            {
                int blocks = 0;
                int[] n;
                if (BlockThrottle.CountsByParty.TryGetValue(c.Party, out n)) blocks = n[(int)c.Class];
                if (DebugTraces.Governor)
                    Log.Info("THROTTLE party=" + c.Party + " class=" + PlayerThrottlePlanner.Name(c.Class)
                             + " " + (c.On ? "paused" : "released") + " blocks=" + blocks + " cause=" + c.Cause
                             + " totalPaused=" + BlockThrottle.Count);
                if (PlayerNotifier != null) PlayerNotifier(c.Party, ThrottleNotice(c, blocks));
            }
        }

        private string ThrottleNotice(PlayerThrottlePlanner.Change c, int blocks)
        {
            string what = Plural(c.Class);
            if (c.Class == ThrottleClass.Programmable)
                return c.On
                    ? "Server load: your programmable blocks now run under a tighter limit ("
                      + _config.PbLimitPressureCapMsPerFrame + " ms per frame each) because "
                      + (c.Cause == "admin" ? "an admin set it" : "the server is over capacity and your grids are using more than your share of it")
                      + ". A script over it runs less often; commands sent to it still run."
                    : "Server load: your programmable blocks are back under the normal limit.";
            if (!c.On) return "Server load: your " + what + " are running again.";
            string why = c.Cause == "admin"
                ? "an admin paused them"
                : "the server is over capacity and your grids are using more than your share of it";
            return "Server load: your " + what + " are paused on all your grids (" + blocks + " block"
                 + (blocks == 1 ? "" : "s") + ") because " + why + ". They stay powered and resume by "
                 + "themselves when the load drops; a Factions station can do the same work for you meanwhile.";
        }

        private static string Plural(ThrottleClass c)
        {
            switch (c)
            {
                case ThrottleClass.Refinery: return "refineries";
                case ThrottleClass.Assembler: return "assemblers";
                case ThrottleClass.Programmable: return "programmable blocks";
                default: return "welders (Build and Repair included)";
            }
        }

        /// <summary>Pausable-class cost per party and class, with whether it is paused and how many
        /// blocks the pause holds. Written only for a class that cost something or is paused.</summary>
        private void BuildThrottlePoints(Dictionary<string, double[]> classMs, long timestamp)
        {
            var parties = new HashSet<string>(classMs.Keys, StringComparer.Ordinal);
            foreach (string p in _throttle.Parties()) parties.Add(p);
            foreach (string party in parties)
            {
                double[] ms;
                classMs.TryGetValue(party, out ms);
                int[] n;
                BlockThrottle.CountsByParty.TryGetValue(party, out n);
                foreach (ThrottleClass c in PlayerThrottlePlanner.AllClasses)
                {
                    double cost = ms != null ? ms[(int)c] : 0.0;
                    bool paused = _throttle.IsThrottled(party, c);
                    if (cost <= 0 && !paused) continue;
                    _batch.Point("throttle_class", timestamp)
                        .Tag("party", party.Replace('\\', '/'))
                        .Tag("class", PlayerThrottlePlanner.Name(c))
                        .Field("msPerFrame", Math.Round(cost, 4))
                        .Field("paused", paused)
                        .Field("forced", _throttle.IsForced(party, c))
                        .Field("blocks", (long)(n != null ? n[(int)c] : 0))
                        .End();
                }
            }
        }

        /// <summary>Adds the window's script time to the per-grid class cost, as the programmable
        /// class, so the step-3 planner ranks it by what it measurably costs like the other three.</summary>
        private void AddScriptCostToClassCost(List<PbCostSummary.Block> blocks)
        {
            int idx = (int)ThrottleClass.Programmable;
            foreach (PbCostSummary.Block b in blocks)
            {
                double[] row;
                if (!_classCostMs.TryGetValue(b.GridId, out row))
                {
                    row = new double[PlayerThrottlePlanner.AllClasses.Length];
                    _classCostMs[b.GridId] = row;
                }
                row[idx] += b.ScheduledMs + b.ExternalMs;
            }
        }

        private List<PbLimiter.Change> _limiterChanges = new List<PbLimiter.Change>();

        /// <summary>The per-block cap for a script on this grid: the pressure cap when the governor
        /// has picked its owner's programmable blocks (or an admin has), or, for Factions' own grids,
        /// from Factions step 2 while the governor acts on Factions.</summary>
        private double CapForGrid(long gridId)
        {
            string party;
            if (gridId != 0 && _census.PartyByGrid.TryGetValue(gridId, out party) && party != null)
            {
                if (party == SimSetTally.Factions)
                {
                    if (_config.GovernorFactionsActuate && _governor.FactionsStep >= 2) return _config.PbLimitPressureCapMsPerFrame;
                }
                else if (_throttle.IsThrottled(party, ThrottleClass.Programmable)) return _config.PbLimitPressureCapMsPerFrame;
            }
            return _config.PbLimitCapMsPerFrame;
        }

        /// <summary>One limiter window. Above the publisher test in <see cref="Flush"/>: limiting must
        /// not depend on whether anything is being plotted. It reads the planner as the last governor
        /// evaluation left it, one window behind at most.</summary>
        private void EvaluateLimiter(DateTime nowUtc)
        {
            PbLimiter limiter = PbLimiterPatches.Limiter;
            if (limiter == null) { _limiterChanges = new List<PbLimiter.Change>(); return; }
            PbLimiterPatches.PruneCarried();
            List<PbLimiter.Change> changes = PbLimiterPatches.TakeResets();
            changes.AddRange(limiter.Evaluate(_frames, nowUtc, CapForGrid));
            _limiterChanges = changes;
            if (changes.Count == 0) return;

            foreach (PbLimiter.Change c in changes)
            {
                string party = BlockThrottle.PartyOfGrid(c.GridId) ?? PbCostSummary.UnknownParty;
                if (DebugTraces.Governor)
                    Log.Info("PB_LIMIT block=" + c.BlockId + " grid=" + c.GridId + " party=" + party
                             + " " + c.From + "->" + c.To + " divisor=" + c.Divisor
                             + " msPerRun=" + c.MsPerRun.ToString("F2"));
                if (PlayerNotifier == null || party == SimSetTally.Factions || party == PbCostSummary.UnknownParty) continue;
                if (c.To == PbLimiter.State.Stopped)
                    PlayerNotifier(party, "Server load: a programmable block's script (" + BlockName(c.BlockId)
                        + ") is stopped for " + (_config.PbLimitRetrySeconds / 60.0).ToString("0") + " minutes: each run took "
                        + c.MsPerRun.ToString("F1") + " ms on average, long enough to stall the server. It stays powered; "
                        + "recompiling the script retries it at once.");
                else if (c.From == PbLimiter.State.Stopped)
                    PlayerNotifier(party, "Server load: the script on " + BlockName(c.BlockId) + " may run again.");
            }
            BlockThrottle.RaiseNoticesChanged();
        }

        /// <summary>Every block the limiter slows or stops, and the window's totals.</summary>
        private void BuildLimiterPoints(long timestamp)
        {
            PbLimiter limiter = PbLimiterPatches.Limiter;
            if (limiter == null) return;
            List<PbLimiter.BlockView> limited = limiter.Limited();
            int slowed = 0, stopped = 0;
            foreach (PbLimiter.BlockView b in limited)
            {
                if (b.State == PbLimiter.State.Stopped) stopped++; else slowed++;
                string grid = b.GridId == EntityCostPatches.NotOnAGrid ? "(not on a grid)" : GridName(b.GridId);
                _batch.Point("pb_limit", timestamp)
                    .Tag("blockId", b.BlockId.ToString())
                    .Tag("grid", (grid ?? "").Replace('\\', '/'))
                    .Tag("party", (BlockThrottle.PartyOfGrid(b.GridId) ?? PbCostSummary.UnknownParty).Replace('\\', '/'))
                    .Field("block", BlockName(b.BlockId))
                    .Field("state", b.State.ToString())
                    .Field("divisor", (long)b.Divisor)
                    .Field("msPerRun", Math.Round(b.MsPerRun, 3))
                    .End();
            }
            int toSlowed = 0, toStopped = 0;
            foreach (PbLimiter.Change c in _limiterChanges)
            {
                if (c.To == PbLimiter.State.Slowed && c.From == PbLimiter.State.Normal) toSlowed++;
                if (c.To == PbLimiter.State.Stopped) toStopped++;
            }
            _batch.Point("pb_limit_total", timestamp)
                .Field("slowed", (long)slowed)
                .Field("stopped", (long)stopped)
                .Field("newlySlowed", (long)toSlowed)
                .Field("newlyStopped", (long)toStopped)
                .Field("capMsPerFrame", _config.PbLimitCapMsPerFrame)
                .Field("pressureCapMsPerFrame", _config.PbLimitPressureCapMsPerFrame)
                .End();
        }

        /// <summary><c>factions throttle</c> with no arguments: what is paused, for whom, and why.</summary>
        internal string ThrottleReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Player toolbox step 3 (pause refineries, assemblers, welders): governor ")
              .Append(_config.GovernorPlayersThrottle ? "ACTS" : "does not act (GovernorPlayersThrottle is off)")
              .Append("; production hooks ").Append(ProductionThrottlePatches.Bound ? "bound" : "NOT bound")
              .Append(", update gate ").Append(EntityCostPatches.Bound ? "bound" : "NOT bound")
              .Append(". Paused blocks: ").Append(BlockThrottle.Count).Append('.');
            PbLimiter limiter = PbLimiterPatches.Limiter;
            if (limiter == null) sb.Append(" Programmable-block limiter: off (PbLimitEnabled).");
            else
            {
                List<PbLimiter.BlockView> limited = limiter.Limited();
                int stopped = 0;
                foreach (PbLimiter.BlockView b in limited) if (b.State == PbLimiter.State.Stopped) stopped++;
                sb.Append(" Programmable-block limiter: cap ").Append(_config.PbLimitCapMsPerFrame)
                  .Append(" ms/frame (pressure ").Append(_config.PbLimitPressureCapMsPerFrame).Append("), gates ")
                  .Append(PbLimiterPatches.Bound ? "bound" : "NOT bound").Append("; ")
                  .Append(limited.Count - stopped).Append(" slowed, ").Append(stopped).Append(" stopped.");
                foreach (PbLimiter.BlockView b in limited)
                    sb.Append(' ').Append(BlockName(b.BlockId)).Append(" [").Append(BlockThrottle.PartyOfGrid(b.GridId) ?? "?")
                      .Append("] ").Append(b.State == PbLimiter.State.Stopped ? "stopped" : "1/" + b.Divisor)
                      .Append(" at ").Append(b.MsPerRun.ToString("F2")).Append(" ms/run;");
            }
            List<string> parties = _throttle.Parties();
            if (parties.Count == 0) sb.Append(" Nothing is paused.");
            foreach (string party in parties)
            {
                int[] n;
                BlockThrottle.CountsByParty.TryGetValue(party, out n);
                sb.Append(' ').Append(party).Append(':');
                foreach (ThrottleClass c in _throttle.ClassesFor(party))
                    sb.Append(' ').Append(PlayerThrottlePlanner.Name(c))
                      .Append(_throttle.IsForced(party, c) ? "(admin)" : "(governor)")
                      .Append('=').Append(n != null ? n[(int)c] : 0);
                sb.Append('.');
            }
            return sb.ToString();
        }

        /// <summary><c>factions throttle &lt;player&gt; &lt;class|all&gt; on|off</c>: the admin
        /// override. The player is matched, case-insensitively, against the parties the last census
        /// charged grids to.</summary>
        internal string ForceThrottle(string player, string classText, bool on)
        {
            string party = null;
            foreach (string p in _census.PartyByGrid.Values)
            {
                if (p == null || p == SimSetTally.Factions || p == SimSetTally.Unowned) continue;
                if (string.Equals(p, player, StringComparison.OrdinalIgnoreCase)) { party = p; break; }
            }
            if (party == null)
                return "No player named '" + player + "' owns a grid in the last census.";

            var classes = new List<ThrottleClass>();
            if (string.Equals(classText, "all", StringComparison.OrdinalIgnoreCase)) classes.AddRange(PlayerThrottlePlanner.AllClasses);
            else
            {
                ThrottleClass c;
                if (!PlayerThrottlePlanner.TryParse(classText, out c))
                    return "Unknown class '" + classText + "': use refinery, assembler, welder or all.";
                classes.Add(c);
            }

            var changes = new List<PlayerThrottlePlanner.Change>();
            foreach (ThrottleClass c in classes)
            {
                PlayerThrottlePlanner.Change change = _throttle.Force(party, c, on);
                if (change != null) changes.Add(change);
            }
            if (changes.Count == 0) BlockThrottle.Rebuild(_throttle, _census.PartyByGrid);
            else ApplyThrottleChanges(changes);
            return (changes.Count == 0 ? "No change. " : "") + ThrottleReport();
        }

        private static string DescribePlayers(LoadGovernor.Decision d)
        {
            var sb = new System.Text.StringBuilder();
            foreach (LoadGovernor.PlayerView v in d.Players)
                sb.Append(' ').Append(v.Party).Append('=').Append(v.CostMs.ToString("F2"))
                  .Append('/').Append(v.ShareMs.ToString("F2")).Append("ms step=").Append(v.Step)
                  .Append(v.Over ? " OVER" : "");
            return sb.ToString();
        }

        /// <summary><c>factions governor</c>: the controller's last decision, and its settings.</summary>
        internal string GovernorReport()
        {
            LoadGovernor.Decision d = _governor.Last;
            string settings = "adjust at " + _config.GovernorAdjustBusyPct + "% busy or sim < " + _config.GovernorSimFloor
                            + ", release below " + _config.GovernorReleaseBusyPct + "%, one step per "
                            + _config.GovernorIntervalSeconds + " s, Factions -" + _config.GovernorFactionsStepPct
                            + "% per step to a " + _config.GovernorFactionsFloorMs + " ms floor over at most "
                            + _config.GovernorFactionsMaxSteps + " steps";
            if (d == null)
                return "governor (dry run): no decision yet -- it runs once per window that has a ledger, which needs "
                       + "EntityCostEnabled and a census. Settings: " + settings + ".";
            return "governor (dry run): " + d.Pressure + (d.Cause.Length > 0 ? " (" + d.Cause + ")" : "")
                 + ", busy " + d.BusyPct.ToString("F1") + "%, short " + d.ShortMs.ToString("F2") + " ms"
                 + "; Factions step " + d.FactionsStep + ", cost " + d.FactionsMs.ToString("F2") + " ms -> target "
                 + d.FactionsTargetMs.ToString("F2") + " ms; player pool " + d.PlayerPoolMs.ToString("F2") + " ms"
                 + DescribePlayers(d) + "; last action: " + d.Action
                 + "; stall tail " + d.TailPressure + " (" + d.TailFrames + " slow frames, " + d.TailStallMs.ToString("F0")
                 + " ms over budget, Factions " + d.TailFactionsMs.ToString("F0") + " ms, top "
                 + (d.TailTopCause.Length > 0 ? d.TailTopCause + " " + d.TailTopCauseMs.ToString("F0") + " ms" : "none")
                 + "; reported, not acted on). Settings: " + settings + ". "
                 + (_config.GovernorFactionsActuate
                        ? "ACTS on Factions: from step 1 traffic a player comes near resolves off-screen; from step 2 combat waits, "
                          + "up to " + _config.GovernorCombatMaxWaitSeconds + " s."
                        : "Acts on nothing (GovernorFactionsActuate is off).")
                 + (_config.GovernorPlayersThrottle
                        ? " ACTS on players at step 3: pauses refineries, assemblers and welders, one class per interval."
                        : "");
        }

        /// <summary>Whether the last census found this grid simulated. Up to one census old.</summary>
        private int GridClassOf(long gridId)
        {
            if (_census.SimulatedIds.Contains(gridId)) return EntityCostPatches.GridClass.Simulated;
            if (_census.UnsimulatedIds.Contains(gridId)) return EntityCostPatches.GridClass.Unsimulated;
            return EntityCostPatches.GridClass.Unknown;
        }

        /// <summary>The simulated set (load governor, build step 2): what SE is stepping, per grid
        /// and summed per party by owner.</summary>
        private void BuildSimSetPoints(long timestamp)
        {
            _batch.Point("sim_set", timestamp)
                .Field("clusters", (long)_census.Clusters)
                .Field("activeClusters", (long)_census.ActiveClusters)
                .Field("selectiveUpdates", _census.SelectiveUpdates)
                .Field("simulatedGrids", (long)_census.Parties.SimulatedGrids)
                .Field("unsimulatedGrids", (long)_census.Parties.UnsimulatedGrids)
                .End();

            foreach (KeyValuePair<string, SimSetTally.Party> kv in _census.Parties.Parties)
            {
                SimSetTally.Party p = kv.Value;
                _batch.Point("sim_party", timestamp)
                    .Tag("party", kv.Key)
                    .Field("grids", (long)p.Grids)
                    .Field("awake", (long)p.Awake)
                    .Field("moving", (long)p.Moving)
                    .Field("blocks", p.Blocks)
                    .Field("pcu", p.Pcu)
                    .Field("shapes", p.Shapes)
                    .Field("massTons", Math.Round(p.MassTons, 1))
                    .End();
            }

            List<GridCensus.SimGridRow> rows = _census.Simulated;
            if (rows.Count > SimGridRowCap)
            {
                rows = new List<GridCensus.SimGridRow>(rows);
                rows.Sort((a, b) => b.Blocks.CompareTo(a.Blocks));
                rows.RemoveRange(SimGridRowCap, rows.Count - SimGridRowCap);
            }
            for (int i = 0; i < rows.Count; i++)
            {
                GridCensus.SimGridRow r = rows[i];
                BuildGridSimPoint(r, timestamp);
            }
        }

        private void BuildGridSimPoint(GridCensus.SimGridRow r, long timestamp)
        {
            var point = _batch.Point("grid_sim", timestamp)
                .Tag("gridId", r.GridId.ToString())
                .Tag("grid", (r.Name ?? "").Replace('\\', '/'))
                .Tag("party", r.Party)
                .Tag("origin", GridCensus.OriginTag(r.Origin))
                .Field("blocks", (long)r.Blocks)
                .Field("pcu", r.Pcu)
                .Field("shapes", r.Shapes)
                .Field("awake", r.Awake)
                .Field("moving", r.Moving)
                .Field("massTons", Math.Round(r.MassTons, 1));
            if (r.NearestPlayerKm >= 0) point.Field("nearestPlayerKm", Math.Round(r.NearestPlayerKm, 2));
            point.End();
        }

        private void BuildCensusPoints(long timestamp)
        {
            for (int i = 0; i < GridCensus.OriginCount; i++)
            {
                var origin = (GridOrigin)i;
                GridCensus.Bucket b = _census[origin];
                // Emitted even at zero: a series that stops when a category empties reads as a
                // collection gap rather than as "there are none", and "there are none right now" is
                // exactly the control the busy windows are judged against.
                _batch.Point("grid_census", timestamp)
                    .Tag("origin", GridCensus.OriginTag(origin))
                    .Field("grids", (long)b.Grids)
                    .Field("moving", (long)b.Moving)
                    .Field("static", (long)b.Static)
                    .Field("farFromPlayer", (long)b.FarFromPlayer)
                    .Field("playerOwned", (long)b.PlayerOwned)
                    .Field("movingInGravity", (long)b.MovingInGravity)
                    .Field("inGravity", (long)b.InGravity)
                    .Field("peakGravityG", Math.Round(b.PeakGravityG, 4))
                    .Field("subgrids", (long)b.Subgrids)
                    .Field("transformChildren", b.TransformChildren)
                    .Field("shapeCount", b.ShapeCount)
                    .Field("blocks", b.Blocks)
                    .Field("pcu", b.Pcu)
                    .Field("massTons", Math.Round(b.MassTons, 1))
                    .End();
            }
        }

        /// <summary>Per-entity update cost for the window.
        ///
        /// <para><b>Serial</b> is game-thread time and adds directly into <c>busyMsPerFrame</c>.
        /// <b>Parallel</b> is worker CPU summed across threads: it is the right figure for splitting
        /// cost between grids, but it does not add into the game thread's frame. What the game thread
        /// actually waits for during the parallel passes is <c>entityParallelWallMsPerFrame</c>.</para></summary>
        private void BuildEntityCostPoints(EntityCostPatches.Window w, long timestamp)
        {
            var totals = _batch.Point("torch_server_entities", timestamp)
                .Field("serialMsPerFrame", PerFrame(ToMs(w.SerialTicks)))
                .Field("parallelMsPerFrame", PerFrame(ToMs(w.ParallelTicks)))
                .Field("parallelWallMsPerFrame", PerFrame(ToMs(w.ParallelWallTicks)))
                .Field("callsPerFrame", _frames > 0 ? Math.Round((double)w.Calls / _frames, 1) : 0.0)
                .Field("parallelPasses", w.ParallelPasses);
            // Serial + parallel CPU over every grid, split by whether SE was simulating it. Answers
            // whether block logic keeps running on a grid SE has stopped stepping: if it does,
            // unsimulatedGridMsPerFrame is not zero and the governor must charge for those grids.
            if (w.TicksByClass != null)
                totals.Field("simulatedGridMsPerFrame", PerFrame(ToMs(w.TicksByClass[EntityCostPatches.GridClass.Simulated])))
                      .Field("unsimulatedGridMsPerFrame", PerFrame(ToMs(w.TicksByClass[EntityCostPatches.GridClass.Unsimulated])))
                      .Field("unknownGridMsPerFrame", PerFrame(ToMs(w.TicksByClass[EntityCostPatches.GridClass.Unknown])))
                      .Field("notOnGridMsPerFrame", PerFrame(ToMs(w.TicksByClass[EntityCostPatches.GridClass.NotOnAGrid])));
            totals.End();

            for (int i = 0; i < w.TopGrids.Count; i++)
            {
                EntityCostPatches.GridRow r = w.TopGrids[i];
                string name = r.GridId == EntityCostPatches.NotOnAGrid ? "(not on a grid)" : GridName(r.GridId);
                // The name is a tag as well as a field so a dashboard can label a per-grid series
                // by it; InfluxQL can group and alias by tags only. Backslash is swapped out because
                // a trailing one would escape the separator after the tag value.
                _batch.Point("entity_cost", timestamp)
                    .Tag("gridId", r.GridId.ToString())
                    .Tag("grid", (name ?? "").Replace('\\', '/'))
                    .Field("name", name)
                    .Field("serialMsPerFrame", PerFrame(ToMs(r.SerialTicks)))
                    .Field("parallelMsPerFrame", PerFrame(ToMs(r.ParallelTicks)))
                    .Field("totalMsPerFrame", PerFrame(ToMs(r.TotalTicks)))
                    .Field("calls", r.Calls)
                    .End();
            }

            for (int i = 0; i < w.TopTypes.Count; i++)
            {
                EntityCostPatches.TypeRow r = w.TopTypes[i];
                _batch.Point("entity_cost_type", timestamp)
                    .Tag("type", r.Type)
                    .Field("serialMsPerFrame", PerFrame(ToMs(r.SerialTicks)))
                    .Field("parallelMsPerFrame", PerFrame(ToMs(r.ParallelTicks)))
                    .Field("totalMsPerFrame", PerFrame(ToMs(r.SerialTicks + r.ParallelTicks)))
                    .Field("calls", r.Calls)
                    .End();
            }
        }

        /// <summary>Programmable-block script cost for the window: a summary, the split per party and
        /// the costliest blocks (<see cref="PbCostPatches"/>). Script time is game-thread time, so
        /// <c>msPerFrame</c> adds into <c>busyMsPerFrame</c> like the serial entity figure does.
        ///
        /// <para>The party is the grid's, from the last census; a grid it has not seen yet reads
        /// <see cref="PbCostSummary.UnknownParty"/>.</para></summary>
        private PbCostSummary.Result BuildPbCostPoints(List<PbCostSummary.Block> blocks, long timestamp)
        {
            Func<long, string> partyOf = null;
            if (_censusValid)
                partyOf = id => { string party; return _census.PartyByGrid.TryGetValue(id, out party) ? party : null; };
            PbCostSummary.Result r = PbCostSummary.Summarise(blocks, _frames, _config.PbCostOverMsPerFrame,
                                                             partyOf, _config.PbCostTopN);

            _batch.Point("pb_cost", timestamp)
                .Field("blocks", r.Blocks)
                .Field("runsPerFrame", _frames > 0 ? Math.Round((double)r.Runs / _frames, 3) : 0.0)
                .Field("externalRunsPerFrame", _frames > 0 ? Math.Round((double)r.ExternalRuns / _frames, 3) : 0.0)
                .Field("msPerFrame", Math.Round(r.MsPerFrame, 4))
                .Field("scheduledMsPerFrame", Math.Round(r.ScheduledMsPerFrame, 4))
                .Field("externalMsPerFrame", Math.Round(r.ExternalMsPerFrame, 4))
                .Field("p50MsPerFrame", Math.Round(r.P50MsPerFrame, 4))
                .Field("p95MsPerFrame", Math.Round(r.P95MsPerFrame, 4))
                .Field("maxMsPerFrame", Math.Round(r.MaxMsPerFrame, 4))
                .Field("maxRunMs", Math.Round(r.MaxRunMs, 3))
                .Field("over", r.Over)
                .Field("overThresholdMsPerFrame", r.OverThresholdMsPerFrame)
                .End();

            for (int i = 0; i < r.Parties.Count; i++)
            {
                PbCostSummary.PartyRow p = r.Parties[i];
                _batch.Point("pb_cost_party", timestamp)
                    .Tag("party", p.Party.Replace('\\', '/'))
                    .Field("blocks", p.Blocks)
                    .Field("over", p.Over)
                    .Field("msPerFrame", Math.Round(p.MsPerFrame, 4))
                    .End();
            }

            for (int i = 0; i < r.Top.Count; i++)
            {
                PbCostSummary.BlockRow b = r.Top[i];
                string grid = b.Block.GridId == EntityCostPatches.NotOnAGrid ? "(not on a grid)" : GridName(b.Block.GridId);
                _batch.Point("pb_cost_block", timestamp)
                    .Tag("blockId", b.Block.BlockId.ToString())
                    .Tag("grid", (grid ?? "").Replace('\\', '/'))
                    .Tag("party", b.Party.Replace('\\', '/'))
                    .Field("block", BlockName(b.Block.BlockId))
                    .Field("msPerFrame", Math.Round(b.MsPerFrame, 4))
                    .Field("scheduledMsPerFrame", Math.Round(b.ScheduledMsPerFrame, 4))
                    .Field("externalMsPerFrame", Math.Round(b.ExternalMsPerFrame, 4))
                    .Field("scheduledRuns", b.Block.ScheduledRuns)
                    .Field("externalRuns", b.Block.ExternalRuns)
                    .Field("maxRunMs", Math.Round(b.Block.MaxRunMs, 3))
                    .End();
            }
            return r;
        }

        /// <summary>Every frame over the hitch threshold in this window, with where it went, and the
        /// window's session-component and Torch-plugin cost by type (<see cref="HitchProbe"/>).
        /// Section fields nest (components sit inside sessionUpdate, which sits inside gameUpdate),
        /// so they are read side by side, never summed.</summary>
        /// <summary>The window's stall tail for the governor (FAC-445): every hitch's time over the frame
        /// budget, by its measured cause.</summary>
        private int _tailFrames;
        private double _tailStallMs;
        private readonly Dictionary<string, double> _tailByCause = new Dictionary<string, double>(StringComparer.Ordinal);
        private LoadGovernor.Pressure _lastTailPressure;
        private readonly StallBackoff _stallBackoff = new StallBackoff();

        /// <summary>Steps <see cref="StallBackoff"/> on this window's tail and hands the scale to the budgets
        /// that read it.</summary>
        private double ApplyStallBackoff(LoadGovernor.Decision d)
        {
            bool changed = _stallBackoff.Update(d.TailPressure);
            StallBackoffScale.Value = _stallBackoff.Scale;
            SpawnPacing.Pacer.Backoff = _stallBackoff.Scale;
            if (changed && DebugTraces.Governor)
                Log.Info("GOVERNOR_STALL_BACKOFF scale=" + _stallBackoff.Scale.ToString("F2") + " tail=" + d.TailPressure
                         + " stallMs=" + d.TailStallMs.ToString("F0") + " top="
                         + (d.TailTopCause.Length > 0 ? d.TailTopCause : "-"));
            return _stallBackoff.Scale;
        }

        private void AddToTail(HitchProbe.Hitch h)
        {
            double over = h.FrameMs - FrameBudgetMs;
            if (over <= 0) return;
            string cause = TailCause(h);
            double v;
            _tailByCause.TryGetValue(cause, out v);
            _tailByCause[cause] = v + over;
            _tailFrames++;
            _tailStallMs += over;
        }

        /// <summary>What a slow frame is charged to: Factions when this plugin took half of it, a gen-2
        /// collection when one ran in it, else SE's top session component.</summary>
        internal static string TailCause(HitchProbe.Hitch h)
        {
            if (h.TopPlugin != null && h.TopPlugin.IndexOf("Factions", StringComparison.Ordinal) >= 0
                && h.TopPluginMs >= h.FrameMs * 0.5)
                return LoadGovernor.FactionsCause;
            if (h.Gc2 > 0) return "GC gen2";
            return string.IsNullOrEmpty(h.TopComponent) ? "other" : h.TopComponent;
        }

        private void BuildHitchPoints(long timestamp)
        {
            int dropped;
            List<HitchProbe.Hitch> hitches = HitchProbe.TakeHitches(out dropped);
            foreach (HitchProbe.Hitch h in hitches)
            {
                AddToTail(h);
                var p = _batch.Point("hitch", InfluxLineProtocol.ToUnixNanos(h.Utc))
                    .Tag("topComponent", Safe(h.TopComponent))
                    .Tag("topPlugin", Safe(h.TopPlugin))
                    .Tag("topEntity", Safe(h.TopEntity))
                    .Field("frameMs", Math.Round(h.FrameMs, 2));
                for (int i = 0; i < h.SectionMs.Length; i++)
                    p.Field(HitchProbe.SectionNames[i] + "Ms", Math.Round(h.SectionMs[i], 2));
                string grid = h.TopEntityGrid == 0 ? "" : GridName(h.TopEntityGrid);
                p.Field("componentsMs", Math.Round(h.ComponentsMs, 2))
                 .Field("topComponentMs", Math.Round(h.TopComponentMs, 2))
                 .Field("pluginsMs", Math.Round(h.PluginsMs, 2))
                 .Field("topPluginMs", Math.Round(h.TopPluginMs, 2))
                 .Field("entityMs", Math.Round(h.EntityMs, 2))
                 .Field("topEntityMs", Math.Round(h.TopEntityMs, 2))
                 .Field("topEntityGrid", grid ?? "")
                 .Field("parallelWallMs", Math.Round(h.ParallelWallMs, 2))
                 .Field("scriptMs", Math.Round(h.ScriptMs, 2))
                 .Field("topScriptMs", Math.Round(h.TopScriptMs, 2))
                 .Field("topScriptBlock", h.TopScriptBlock == 0 ? "" : BlockName(h.TopScriptBlock))
                 .Field("gc0", (long)h.Gc0).Field("gc1", (long)h.Gc1).Field("gc2", (long)h.Gc2)
                 .End();
            }
            _batch.Point("hitch_window", timestamp)
                .Field("hitches", (long)hitches.Count)
                .Field("dropped", (long)dropped)
                .End();

            foreach (HitchProbe.TypeCost c in HitchProbe.TakeComponents(_config.EntityCostTopN))
                _batch.Point("session_cost_type", timestamp)
                    .Tag("type", Safe(c.Type))
                    .Field("msPerFrame", PerFrame(c.Ms))
                    .Field("calls", c.Calls)
                    .End();
            foreach (HitchProbe.TypeCost c in HitchProbe.TakePlugins(_config.EntityCostTopN))
                _batch.Point("plugin_cost_type", timestamp)
                    .Tag("type", Safe(c.Type))
                    .Field("msPerFrame", PerFrame(c.Ms))
                    .Field("calls", c.Calls)
                    .End();
        }

        /// <summary>The spawn pacer's window: spawns started, how many had to wait, the longest
        /// wait and queue, and the two safety releases.</summary>
        /// <summary>A spawn is starting: would it have fitted the budget? The prediction is reserved
        /// either way: the spawn goes ahead whatever the verdict, so its cost is coming.</summary>
        internal TrafficBudget.Decision ProbeSpawn(string key, TrafficClass cls, double baseMs, int unknownHulls, int blocks, DateTime nowUtc)
        {
            _trafficUnknownHulls += unknownHulls;
            Traffic.NoteSpawnStall(TrafficBudget.SpawnStallMs(blocks), nowUtc);
            TrafficBudget.Decision d = Traffic.Consider(cls, baseMs, nowUtc);
            Traffic.Reserve(key, d.PredictedMs, nowUtc);
            return d;
        }

        /// <summary>Closes the budget's window: the busy time it measured, and, when the ledger ran,
        /// the fleet grids' measured cost against their prediction for calibration. Only fleets whose
        /// lead grid the census saw are compared, so both sides describe the same grids.</summary>
        private void EvaluateTraffic(LoadLedger.Result ledger, DateTime nowUtc, double windowSec, long timestamp)
        {
            _fleetLoads.Clear();
            if (FleetLoadReader != null) FleetLoadReader(_fleetLoads);

            double liveCiv = 0, liveCombat = 0;
            foreach (FleetLoad f in _fleetLoads)
            {
                if (f.Class == TrafficClass.Combat) liveCombat += f.BaseMs; else liveCiv += f.BaseMs;
            }

            double measured = 0, civ = 0, combat = 0;
            int matched = 0;
            if (ledger != null)
            {
                var seen = new Dictionary<long, double>(ledger.Grids.Count);
                foreach (LoadLedger.GridCost g in ledger.Grids) seen[g.GridId] = g.TotalMs;
                foreach (FleetLoad f in _fleetLoads)
                {
                    double leadMs;
                    if (!seen.TryGetValue(f.LeadGridId, out leadMs)) continue;
                    matched++;
                    measured += leadMs;
                    if (f.EscortGridIds != null)
                        foreach (long e in f.EscortGridIds)
                        {
                            double ms;
                            if (seen.TryGetValue(e, out ms)) measured += ms;
                        }
                    if (f.Class == TrafficClass.Combat) combat += f.BaseMs; else civ += f.BaseMs;
                }
            }

            // Read before the window is observed: observing it releases the stalls it measured.
            double spawnStall = Traffic.SpawnStallLoadMs;
            Traffic.ObserveWindow(new TrafficBudget.WindowInput
            {
                WindowStartUtc = nowUtc.AddSeconds(-windowSec),
                BusyMs = ToMs(_busyTicks) / _busySamples,
                FleetMeasuredMs = measured,
                CivilianBaseMs = civ,
                CombatBaseMs = combat,
                UnmatchedFleetMs = Traffic.CivilianMultiplier * Math.Max(0.0, liveCiv - civ)
                                 + Traffic.CombatMultiplier * Math.Max(0.0, liveCombat - combat),
            }, nowUtc);

            int wouldAdmit, wouldRefuse;
            double admittedMs, refusedMs;
            Traffic.TakeWindow(out wouldAdmit, out wouldRefuse, out admittedMs, out refusedMs);
            double kCiv = Traffic.CivilianMultiplier, kCombat = Traffic.CombatMultiplier;
            _batch.Point("traffic_control", timestamp)
                .Field("targetMs", Math.Round(Traffic.TargetMs, 4))
                .Field("busyMs", Math.Round(Traffic.LastBusyMs, 4))
                .Field("headroomMs", Math.Round(Traffic.HeadroomMs(nowUtc), 4))
                .Field("reservedMs", Math.Round(Traffic.ReservedMs(nowUtc), 4))
                .Field("reservations", (long)Traffic.ReservationCount)
                .Field("kCivilian", Math.Round(kCiv, 4))
                .Field("kCombat", Math.Round(kCombat, 4))
                .Field("civilianSamples", (long)Traffic.CivilianSamples)
                .Field("combatSamples", (long)Traffic.CombatSamples)
                .Field("fleetsMatched", (long)matched)
                .Field("fleetMeasuredMs", Math.Round(measured, 4))
                .Field("fleetPredictedMs", Math.Round(kCiv * civ + kCombat * combat, 4))
                .Field("fleetBaseMs", Math.Round(civ + combat, 4))
                .Field("liveFleets", (long)_fleetLoads.Count)
                .Field("livePredictedMs", Math.Round(kCiv * liveCiv + kCombat * liveCombat, 4))
                .Field("wouldAdmit", (long)wouldAdmit)
                .Field("wouldRefuse", (long)wouldRefuse)
                .Field("admittedMs", Math.Round(admittedMs, 4))
                .Field("refusedMs", Math.Round(refusedMs, 4))
                .Field("unknownHulls", (long)_trafficUnknownHulls)
                .End();
            _trafficUnknownHulls = 0;

            var presence = new int[2];
            if (PresenceCountReader != null) PresenceCountReader(presence);
            TrafficPresence.Plan plan = PresencePlanReader != null ? PresencePlanReader() : null;
            var queue = new int[4];
            if (QueueCountReader != null) QueueCountReader(queue);
            _batch.Point("traffic_presence", timestamp)
                .Field("queueJoined", (long)queue[0])
                .Field("queueCleared", (long)queue[1])
                .Field("queueDiverted", (long)queue[2])
                .Field("queueDelayed", (long)queue[3])
                .Field("queued", (long)(plan != null ? plan.Queued.Count : 0))
                .Field("on", plan != null)
                .Field("factionsBudgetMs", Math.Round(Traffic.FactionsBudgetMs, 4))
                .Field("unmatchedFleetMs", Math.Round(Traffic.LastUnmatchedFleetMs, 4))
                .Field("spawnStallMs", Math.Round(spawnStall, 4))
                .Field("refused", (long)presence[0])
                .Field("shed", (long)presence[1])
                .Field("neighbourhoods", (long)(plan != null ? plan.Neighbourhoods.Count : 0))
                .End();
            if (plan == null) return;
            foreach (TrafficPresence.Neighbourhood nb in plan.Neighbourhoods)
            {
                _batch.Point("traffic_neighbourhood", timestamp)
                    .Tag("party", Safe(nb.Label))
                    .Field("players", (long)nb.Parties.Count)
                    .Field("weight", Math.Round(nb.Weight, 4))
                    .Field("shareMs", Math.Round(nb.ShareMs, 4))
                    .Field("usedMs", Math.Round(nb.UsedMs, 4))
                    .Field("demandMs", Math.Round(nb.DemandMs, 4))
                    .Field("admitted", (long)nb.Admitted)
                    .Field("refused", (long)nb.Refused)
                    .Field("shed", (long)nb.Shed)
                    .Field("effectiveRadiusKm", Math.Round(nb.EffectiveRadiusKm, 3))
                    .Field("queuedArrivals", (long)nb.QueuedArrivals)
                    .Field("queuedDepartures", (long)nb.QueuedDepartures)
                    .Field("cleared", (long)nb.Cleared)
                    .Field("holdingVisible", (long)nb.HoldingVisible)
                    .End();
            }
        }

        private void BuildSpawnPacerPoint(long timestamp)
        {
            SpawnPacer p = SpawnPacing.Pacer;
            _batch.Point("spawn_pacer", timestamp)
                .Field("spacingSeconds", p.SpacingSeconds)
                .Field("granted", (long)p.Granted)
                .Field("queued", (long)p.Queued)
                .Field("maxQueue", (long)p.MaxQueue)
                .Field("maxWaitSeconds", Math.Round(p.MaxWaitSeconds, 2))
                .Field("staleInFlight", (long)p.StaleInFlight)
                .Field("droppedFromQueue", (long)p.DroppedFromQueue)
                .Field("queueNow", (long)p.QueueLength)
                .End();
            p.ResetWindow();
        }

        private static string Safe(string tag)
        {
            return string.IsNullOrEmpty(tag) ? "none" : tag.Replace('\\', '/');
        }

        /// <summary>A terminal block's custom name, for the top-N rows only; empty when it has
        /// closed since the window.</summary>
        private static string BlockName(long id)
        {
            try
            {
                VRage.Game.Entity.MyEntity e;
                if (Sandbox.Game.Entities.MyEntities.TryGetEntityById(id, out e, false))
                {
                    var tb = e as Sandbox.Game.Entities.Cube.MyTerminalBlock;
                    if (tb != null && tb.CustomName != null) return tb.CustomName.ToString();
                }
            }
            catch { }
            return "";
        }

        /// <summary>A grid's display name for the top-N rows only. A grid that closed during the
        /// window has no name left to read, which is reported as such rather than dropped.</summary>
        private static string GridName(long id)
        {
            try
            {
                VRage.Game.Entity.MyEntity e;
                if (Sandbox.Game.Entities.MyEntities.TryGetEntityById(id, out e, false) && e != null)
                    return e.DisplayName ?? "(unnamed)";
            }
            catch { }
            return "(closed)";
        }

        private int MovingTotal()
        {
            int moving = 0;
            for (int i = 0; i < GridCensus.OriginCount; i++) moving += _census[(GridOrigin)i].Moving;
            return moving;
        }

        /// <summary>One-line state for <c>factions perf server</c>. A command response, unlike a log
        /// line, may explain itself.</summary>
        /// <summary>The last closed window's costliest rows, for <c>factions serverperf top</c>.
        /// Null until a window closes. Written and read on the game thread.</summary>
        private ServerPerfTop _lastTop;

        internal string DescribeTop(int n)
        {
            return ServerPerfTop.Format(_lastTop, n, DateTime.UtcNow);
        }

        /// <summary>Copies the rows this window already computed for its Influx points; measures
        /// nothing. Entity rows are absent when per-entity cost is off, ledger rows when there was
        /// no census, script rows when no script ran.</summary>
        private ServerPerfTop BuildTop(DateTime nowUtc, double windowSec, EntityCostPatches.Window w,
                                       LoadLedger.Result ledger, PbCostSummary.Result pb)
        {
            var t = new ServerPerfTop
            {
                AtUtc = nowUtc,
                WindowSeconds = windowSec,
                Frames = _frames,
                BusyMs = _busySamples > 0 ? ToMs(_busyTicks) / _busySamples : double.NaN,
                OursMs = PerFrameRaw(_ours.TotalMilliseconds()),
                PhysicsMs = _physicsMsPerFrame,
            };
            if (w != null)
            {
                t.SerialMs = PerFrameRaw(ToMs(w.SerialTicks));
                t.ParallelCpuMs = PerFrameRaw(ToMs(w.ParallelTicks));
                t.ParallelWallMs = PerFrameRaw(ToMs(w.ParallelWallTicks));
                foreach (EntityCostPatches.GridRow r in w.TopGrids)
                {
                    string party = null;
                    if (_censusValid && r.GridId != EntityCostPatches.NotOnAGrid)
                        _census.PartyByGrid.TryGetValue(r.GridId, out party);
                    t.EntityGrids.Add(new ServerPerfTop.Row
                    {
                        Name = r.GridId == EntityCostPatches.NotOnAGrid ? "(not on a grid)" : GridName(r.GridId),
                        Party = party,
                        LogicMs = PerFrameRaw(ToMs(r.TotalTicks)),
                        Count = r.Calls,
                    });
                }
                foreach (EntityCostPatches.TypeRow r in w.TopTypes)
                    t.Types.Add(new ServerPerfTop.Row
                    {
                        Name = r.Type,
                        LogicMs = PerFrameRaw(ToMs(r.SerialTicks + r.ParallelTicks)),
                        Count = r.Calls,
                    });
            }
            if (ledger != null)
            {
                t.HasLedger = true;
                t.UnattributedPhysicsMs = ledger.UnattributedPhysicsMs;
                foreach (KeyValuePair<string, LoadLedger.PartyCost> kv in ledger.Parties)
                    t.Parties.Add(new ServerPerfTop.Row
                    {
                        Name = kv.Key, LogicMs = kv.Value.LogicMs, PhysicsMs = kv.Value.PhysicsMs,
                        Count = kv.Value.SimulatedGrids,
                    });
                int n = Math.Min(_config.LedgerTopN, ledger.Grids.Count);
                for (int i = 0; i < n; i++)
                {
                    LoadLedger.GridCost g = ledger.Grids[i];
                    t.Grids.Add(new ServerPerfTop.Row
                    {
                        Name = g.Name, Party = g.Party, LogicMs = g.LogicMs, PhysicsMs = g.PhysicsMs,
                    });
                }
            }
            if (pb != null)
                foreach (PbCostSummary.BlockRow b in pb.Top)
                    t.Scripts.Add(new ServerPerfTop.Row
                    {
                        Name = BlockName(b.Block.BlockId) + " on "
                               + (b.Block.GridId == EntityCostPatches.NotOnAGrid ? "(not on a grid)" : GridName(b.Block.GridId)),
                        Party = b.Party,
                        LogicMs = b.MsPerFrame,
                    });
            return t;
        }

        internal string Describe()
        {
            string where = _publisher == null
                ? (_config.Enabled ? "collecting, not publishing (no InfluxUrl/Database set)" : "disabled")
                : "publishing to " + _config.InfluxUrl + " db=" + _config.Database;

            string s = "Server perf: " + where
                     + "; window " + _config.WindowSeconds + "s, census " + _config.CensusSeconds + "s"
                     + "; host=" + _hostTag + " instance=" + _instanceTag
                     + "; physics step hook " + (PhysicsStepPatches.Bound
                            ? "bound"
                            : "NOT bound (" + (PhysicsStepPatches.InactiveReason ?? "unknown") + ")")
                     + "; world-transform hook " + (PhysicsStepPatches.TransformBound ? "bound" : "NOT bound")
                     + "; per-entity cost " + (EntityCostPatches.Enabled
                            ? (EntityCostPatches.Bound ? "ON" : "requested but NOT bound")
                            : "off");

            if (_publisher != null)
            {
                s += "; sent=" + _publisher.SentBatches
                   + " failed=" + _publisher.FailedBatches
                   + " dropped=" + _publisher.DroppedBatches
                   + " queued=" + _publisher.QueueDepth;
                string err = _publisher.LastError;
                if (err != null)
                    s += "; last error " + _publisher.LastErrorUtc.ToString("HH:mm:ss") + "Z: " + err;
            }

            if (_censusValid)
            {
                s += "; grids " + _census.TotalGrids + " (";
                for (int i = 0; i < GridCensus.OriginCount; i++)
                {
                    var o = (GridOrigin)i;
                    if (i > 0) s += ", ";
                    s += GridCensus.OriginTag(o) + " " + _census[o].Grids
                       + "/" + _census[o].Moving + " moving"
                       + "/" + _census[o].FarFromPlayer + " far"
                       + "/" + _census[o].PlayerOwned + " playerOwned"
                       + "/" + _census[o].InGravity + " inGravity"
                       + "/" + _census[o].PeakGravityG.ToString("F3") + "g peak"
                       + "/" + _census[o].Subgrids + " subgrid"
                       + "/" + _census[o].TransformChildren + " xformKids";
                }
                s += ")";
            }
            else s += "; no census yet";

            return s;
        }

        private static double ToMs(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static string SafeMachineName()
        {
            try { return Environment.MachineName; }
            catch { return "unknown"; }
        }

        /// <summary>The instance directory's parent folder name — <c>awl-standalone-test</c> for
        /// <c>C:\Torch\awl-standalone-test\Instance\Factions</c>. What distinguishes two Torch roots
        /// on one box, which is the case this tag exists for.</summary>
        private static string SafeInstanceName(string instanceDir)
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(instanceDir);
                // <root>\Instance\Factions -> up past Factions and Instance.
                if (dir.Parent != null && dir.Parent.Parent != null) return dir.Parent.Parent.Name;
                return dir.Name;
            }
            catch { return "unknown"; }
        }

        public void Dispose()
        {
            SavePrefabCosts();
            if (_publisher != null) _publisher.Dispose();
        }
    }
}
