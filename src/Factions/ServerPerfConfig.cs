using System;
using System.IO;
using Newtonsoft.Json;
using NLog;

namespace Factions
{
    /// <summary>
    /// Where server-thread telemetry goes and how often.
    ///
    /// <para><b>Its own file, not <c>tuning.json</c>.</b> Every value here is a property of the box
    /// this Torch root runs on — a metrics URL, a database name, a sampling cadence — and none of it
    /// changes what the simulation does. <c>tuning.json</c> is versioned gameplay state that the
    /// <c>TuneVersion</c> gate migrates; a host address has no business travelling through that
    /// machinery, and a second Torch root on the same box needs a different value for it.</para>
    ///
    /// <para><b>Per root, deliberately.</b> This sits beside <c>tuning.json</c> and <c>bridge/</c> in
    /// the instance directory rather than in the shared data directory: two roots writing the same
    /// <c>instance</c> tag would interleave two servers' samples into one series.</para>
    ///
    /// <para><b>Missing keys are filled and the file is rewritten.</b> A config seeded only when
    /// absent leaves a key added later permanently unset on every instance that predates it, reading
    /// as a deliberate zero — see <see cref="CapabilityRecipesConfig.MergeDefaults"/> for the same
    /// failure caught the expensive way.</para>
    /// </summary>
    public sealed class ServerPerfConfig
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public const string FileName = "server_perf.json";

        /// <summary>Master switch for collection. False stops the census and the publisher; the
        /// per-frame counters keep running either way, because they are arithmetic on values
        /// <c>ProbeFrameGap</c> has already computed and switching them off saves nothing.</summary>
        public bool Enabled { get; set; }

        /// <summary>Base URL of the InfluxDB server, e.g. <c>http://influx.example:8086</c>. Empty
        /// means collect but do not publish, which is what <c>factions perf server</c> reads.</summary>
        public string InfluxUrl { get; set; }

        /// <summary>InfluxDB 1.x database name. Created by hand, not by this plugin: a write to a
        /// missing database is a 404, and auto-creating one would let a typo silently open a second
        /// series nobody is looking at.</summary>
        public string Database { get; set; }

        /// <summary>Optional HTTP basic credentials. Blank on an unauthenticated 1.x server.</summary>
        public string Username { get; set; }
        public string Password { get; set; }

        /// <summary>How often a window closes and a batch is sent. The default is well under the
        /// 180-second <c>perf.json</c> window on purpose: a sim-rate dip that lasts a minute is
        /// invisible in a three-minute average, and averaging is what hid it until now.</summary>
        public int WindowSeconds { get; set; }

        /// <summary>How often the grid census walks the entity list. Separate from
        /// <see cref="WindowSeconds"/> because it is the only part of this that costs real time on
        /// the game thread, and it answers a question that changes on the scale of a minute.</summary>
        public int CensusSeconds { get; set; }

        /// <summary>Per-entity update cost, attributed to grids and entity types
        /// (<see cref="EntityCostPatches"/>). Default off: it wraps every entity update SE's
        /// orchestrator dispatches, so its cost scales with the population it measures. Patched at
        /// boot only when true; <c>factions debug entitycost</c> then switches it live.</summary>
        public bool EntityCostEnabled { get; set; }

        /// <summary>How many grids, and how many entity types, the per-entity series names per
        /// window. The tail is long and uninteresting, and an unbounded top-N makes the series
        /// cardinality grow with the world.</summary>
        public int EntityCostTopN { get; set; }

        /// <summary>Load-ledger physics model: a dynamic grid weighs blocks to this power
        /// (docs/load-governor.md, "Prediction"). 1.3 is two large-grid hulls measured one at a time
        /// on the bench (2026-09-23); the earlier 2.2 split two hulls 9.6:1 against a measured 4:1.</summary>
        public double LedgerSizeExponent { get; set; }

        /// <summary>Load-ledger physics model: how much more an awake grid weighs than the same grid
        /// asleep. 15 is one Angurvadal at rest against the same hull coasting.</summary>
        public double LedgerAwakeMultiplier { get; set; }

        /// <summary>How many grids the per-grid ledger series names per window, costliest first.</summary>
        public int LedgerTopN { get; set; }

        /// <summary>Load governor (dry run): short at this % of the frame busy (operator decision 4).</summary>
        public double GovernorAdjustBusyPct { get; set; }

        /// <summary>Load governor: clear only below this % busy. The gap to the adjust line is the
        /// hysteresis. No measured basis yet; the dry run is where it gets one.</summary>
        public double GovernorReleaseBusyPct { get; set; }

        /// <summary>Load governor: short whenever sim falls below this (operator decision 3).</summary>
        public double GovernorSimFloor { get; set; }

        /// <summary>Load governor: at most one step change per this many seconds.</summary>
        public double GovernorIntervalSeconds { get; set; }

        /// <summary>Load governor: Factions' cost is cut by this % of itself per step (decision 5).</summary>
        public double GovernorFactionsStepPct { get; set; }

        /// <summary>Load governor: Factions is never cut below this, ms per frame, so the world is
        /// never empty. A value of 0 or less in the file reads as unset and takes the default.</summary>
        public double GovernorFactionsFloorMs { get; set; }

        /// <summary>Load governor: most Factions steps before players are stepped.</summary>
        public int GovernorFactionsMaxSteps { get; set; }

        /// <summary>Load governor: act on Factions (build step 5). False keeps the whole governor a
        /// dry run; true lets its Factions step hold traffic off-screen (FactionsLoadActuator).
        /// Players are never acted on by this switch.</summary>
        public bool GovernorFactionsActuate { get; set; }

        /// <summary>Load governor: act on players at toolbox step 3 (docs/load-governor.md). False
        /// keeps players a dry run; true pauses an over-share player's refineries, assemblers and
        /// welders, one class per interval, costliest first, on every grid they own. Default off.
        /// <c>factions throttle</c> works either way: it is the admin override.</summary>
        public bool GovernorPlayersThrottle { get; set; }

        /// <summary>Load governor: the longest a combat fleet waits before it materialises anyway,
        /// seconds (operator, 2026-09-24: 30 minutes).</summary>
        public double GovernorCombatMaxWaitSeconds { get; set; }

        /// <summary>Prefab pool performance ceiling, ms per frame of predicted awake cost (FAC-385,
        /// operator 2026-09-24). A ship prefab over it is excluded from every role pool. Negative
        /// disables it; 0 or absent in the file reads as unset and takes the default.</summary>
        public double PrefabCeilingMsPerFrame { get; set; }

        /// <summary>The ceiling's prediction: ms per frame per block^LedgerSizeExponent for an awake
        /// hull, from the 2026-09-23 bench median.</summary>
        public double PrefabCostCoefficient { get; set; }

        /// <summary>Programmable-block cost (<see cref="PbCostPatches"/>): a block whose scripts took
        /// more than this many ms per frame over a window is counted as over. Operator, 2026-09-24:
        /// the 0.5 ms HaE PBLimiter enforced, kept as the reference line while the distribution is
        /// measured.</summary>
        public double PbCostOverMsPerFrame { get; set; }

        /// <summary>Programmable blocks published per window, costliest first.</summary>
        public int PbCostTopN { get; set; }

        /// <summary>The programmable-block limiter (<see cref="Factions.Core.PbLimiter"/>). Off unless
        /// set; needs <see cref="EntityCostEnabled"/>, since it steers on the same timing.</summary>
        public bool PbLimitEnabled { get; set; }

        /// <summary>Per-block cap in normal running, ms per frame (operator, 2026-09-24: 0.25).</summary>
        public double PbLimitCapMsPerFrame { get; set; }

        /// <summary>Per-block cap for a player whose programmable blocks the governor picked at step 3,
        /// and for Factions' from its step 2, ms per frame.</summary>
        public double PbLimitPressureCapMsPerFrame { get; set; }

        /// <summary>Stop tier: a script whose average run exceeds this many ms...</summary>
        public double PbLimitStopRunMs { get; set; }

        /// <summary>...for this many seconds of whole windows is stopped.</summary>
        public double PbLimitStopSeconds { get; set; }

        /// <summary>How long a stopped script stays stopped before it is retried.</summary>
        public double PbLimitRetrySeconds { get; set; }

        /// <summary>Hitch probe (<see cref="HitchProbe"/>): a frame whose main-thread time reaches
        /// this many ms is recorded with where it went. 0 or absent takes the default; negative turns
        /// the probe off. Needs <see cref="EntityCostEnabled"/>, which binds it.</summary>
        public double HitchThresholdMs { get; set; }

        /// <summary>Factions spawns (fleet hulls and stations) run one at a time, this many seconds
        /// after the previous landed (operator, 2026-09-25: 1 s). 0 or absent takes the default;
        /// negative turns the pacing off.</summary>
        public double SpawnSpacingSeconds { get; set; }

        /// <summary>Value of the <c>host</c> tag. Defaults to the machine name.</summary>
        public string HostTag { get; set; }

        /// <summary>Value of the <c>instance</c> tag — which Torch root this is. Defaults to the
        /// instance directory's name, which is what distinguishes two roots on one box.</summary>
        public string InstanceTag { get; set; }

        /// <summary>HTTP timeout for one batch POST.</summary>
        public int TimeoutSeconds { get; set; }

        /// <summary>How many unsent batches the publisher holds before it starts dropping the
        /// oldest. Bounds the memory cost of an Influx outage; the drop is counted and reported, so
        /// a gap in the series is never silent.</summary>
        public int MaxQueuedBatches { get; set; }

        public static ServerPerfConfig Defaults()
        {
            return new ServerPerfConfig
            {
                Enabled          = false,
                InfluxUrl        = "",
                Database         = "torch",
                Username         = "",
                Password         = "",
                WindowSeconds    = 30,
                CensusSeconds    = 60,
                EntityCostEnabled = false,
                EntityCostTopN   = 20,
                LedgerSizeExponent    = 1.3,
                LedgerAwakeMultiplier = 15.0,
                LedgerTopN       = 20,
                GovernorAdjustBusyPct   = 95.0,
                GovernorReleaseBusyPct  = 85.0,
                GovernorSimFloor        = 0.8,
                GovernorIntervalSeconds = 180.0,
                GovernorFactionsStepPct = 25.0,
                GovernorFactionsFloorMs = 0.5,
                GovernorFactionsMaxSteps = 3,
                GovernorCombatMaxWaitSeconds = 1800.0,
                PrefabCeilingMsPerFrame  = 2.0,
                PrefabCostCoefficient    = 5.0e-5,
                PbCostOverMsPerFrame     = 0.5,
                PbCostTopN               = 10,
                HitchThresholdMs         = 100.0,
                SpawnSpacingSeconds      = 1.0,
                PbLimitEnabled           = false,
                PbLimitCapMsPerFrame     = 0.25,
                PbLimitPressureCapMsPerFrame = 0.1,
                PbLimitStopRunMs         = 10.0,
                PbLimitStopSeconds       = 60.0,
                PbLimitRetrySeconds      = 600.0,
                HostTag          = "",
                InstanceTag      = "",
                TimeoutSeconds   = 10,
                MaxQueuedBatches = 20,
            };
        }

        /// <summary>Reads the config, seeding or completing the file as needed. Never throws: a
        /// malformed or unreadable config disables collection and says so, because a telemetry
        /// instrument must not be able to stop the plugin loading.</summary>
        public static ServerPerfConfig Load(string instanceDir)
        {
            ServerPerfConfig config = Defaults();
            string path = null;
            try
            {
                path = Path.Combine(instanceDir, FileName);
                if (File.Exists(path))
                {
                    var read = JsonConvert.DeserializeObject<ServerPerfConfig>(File.ReadAllText(path));
                    if (read != null) config = read;
                }
                string added = MergeDefaults(config);
                if (!File.Exists(path) || added != null)
                {
                    File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
                    if (added != null) Log.Info("Server perf: added missing config key(s): " + added + ".");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Server perf: could not read " + (path ?? FileName) + " (" + ex.Message
                         + "); collection disabled.");
                config = Defaults();
            }

            config.Clamp();
            return config;
        }

        /// <summary>Fills keys a file written by an older build does not carry.
        /// <para>Only the keys whose unset value is indistinguishable from a deliberate setting: a
        /// zero window would divide by zero, a blank database cannot be written to. A blank
        /// <see cref="InfluxUrl"/> is left blank — that one genuinely means "do not publish".</para></summary>
        /// <returns>The keys filled, comma-separated, or null when the file was already complete —
        /// which is the common case and the one that must not rewrite the file.</returns>
        private static string MergeDefaults(ServerPerfConfig config)
        {
            ServerPerfConfig d = Defaults();
            string added = null;
            if (config.Database == null)      { config.Database = d.Database;             added = Append(added, "Database"); }
            if (config.InfluxUrl == null)     { config.InfluxUrl = d.InfluxUrl;           added = Append(added, "InfluxUrl"); }
            if (config.Username == null)      { config.Username = d.Username;             added = Append(added, "Username"); }
            if (config.Password == null)      { config.Password = d.Password;             added = Append(added, "Password"); }
            if (config.HostTag == null)       { config.HostTag = d.HostTag;               added = Append(added, "HostTag"); }
            if (config.InstanceTag == null)   { config.InstanceTag = d.InstanceTag;       added = Append(added, "InstanceTag"); }
            if (config.WindowSeconds <= 0)    { config.WindowSeconds = d.WindowSeconds;   added = Append(added, "WindowSeconds"); }
            if (config.CensusSeconds <= 0)    { config.CensusSeconds = d.CensusSeconds;   added = Append(added, "CensusSeconds"); }
            if (config.EntityCostTopN <= 0)   { config.EntityCostTopN = d.EntityCostTopN; added = Append(added, "EntityCostTopN"); }
            if (config.LedgerSizeExponent <= 0)    { config.LedgerSizeExponent = d.LedgerSizeExponent;       added = Append(added, "LedgerSizeExponent"); }
            if (config.LedgerAwakeMultiplier <= 0) { config.LedgerAwakeMultiplier = d.LedgerAwakeMultiplier; added = Append(added, "LedgerAwakeMultiplier"); }
            if (config.LedgerTopN <= 0)       { config.LedgerTopN = d.LedgerTopN;         added = Append(added, "LedgerTopN"); }
            if (config.GovernorAdjustBusyPct <= 0)   { config.GovernorAdjustBusyPct = d.GovernorAdjustBusyPct;     added = Append(added, "GovernorAdjustBusyPct"); }
            if (config.GovernorReleaseBusyPct <= 0)  { config.GovernorReleaseBusyPct = d.GovernorReleaseBusyPct;   added = Append(added, "GovernorReleaseBusyPct"); }
            if (config.GovernorSimFloor <= 0)        { config.GovernorSimFloor = d.GovernorSimFloor;               added = Append(added, "GovernorSimFloor"); }
            if (config.GovernorIntervalSeconds <= 0) { config.GovernorIntervalSeconds = d.GovernorIntervalSeconds; added = Append(added, "GovernorIntervalSeconds"); }
            if (config.GovernorFactionsStepPct <= 0) { config.GovernorFactionsStepPct = d.GovernorFactionsStepPct; added = Append(added, "GovernorFactionsStepPct"); }
            if (config.GovernorFactionsFloorMs <= 0) { config.GovernorFactionsFloorMs = d.GovernorFactionsFloorMs; added = Append(added, "GovernorFactionsFloorMs"); }
            if (config.GovernorFactionsMaxSteps <= 0) { config.GovernorFactionsMaxSteps = d.GovernorFactionsMaxSteps; added = Append(added, "GovernorFactionsMaxSteps"); }
            if (config.GovernorCombatMaxWaitSeconds <= 0) { config.GovernorCombatMaxWaitSeconds = d.GovernorCombatMaxWaitSeconds; added = Append(added, "GovernorCombatMaxWaitSeconds"); }
            if (config.PrefabCeilingMsPerFrame == 0) { config.PrefabCeilingMsPerFrame = d.PrefabCeilingMsPerFrame; added = Append(added, "PrefabCeilingMsPerFrame"); }
            if (config.PrefabCostCoefficient <= 0)   { config.PrefabCostCoefficient = d.PrefabCostCoefficient;     added = Append(added, "PrefabCostCoefficient"); }
            if (config.PbCostOverMsPerFrame <= 0) { config.PbCostOverMsPerFrame = d.PbCostOverMsPerFrame; added = Append(added, "PbCostOverMsPerFrame"); }
            if (config.SpawnSpacingSeconds == 0) { config.SpawnSpacingSeconds = d.SpawnSpacingSeconds; added = Append(added, "SpawnSpacingSeconds"); }
            if (config.HitchThresholdMs == 0) { config.HitchThresholdMs = d.HitchThresholdMs; added = Append(added, "HitchThresholdMs"); }
            if (config.PbCostTopN <= 0)       { config.PbCostTopN = d.PbCostTopN;         added = Append(added, "PbCostTopN"); }
            if (config.PbLimitCapMsPerFrame <= 0) { config.PbLimitCapMsPerFrame = d.PbLimitCapMsPerFrame; added = Append(added, "PbLimitCapMsPerFrame"); }
            if (config.PbLimitPressureCapMsPerFrame <= 0) { config.PbLimitPressureCapMsPerFrame = d.PbLimitPressureCapMsPerFrame; added = Append(added, "PbLimitPressureCapMsPerFrame"); }
            if (config.PbLimitStopRunMs <= 0) { config.PbLimitStopRunMs = d.PbLimitStopRunMs; added = Append(added, "PbLimitStopRunMs"); }
            if (config.PbLimitStopSeconds <= 0) { config.PbLimitStopSeconds = d.PbLimitStopSeconds; added = Append(added, "PbLimitStopSeconds"); }
            if (config.PbLimitRetrySeconds <= 0) { config.PbLimitRetrySeconds = d.PbLimitRetrySeconds; added = Append(added, "PbLimitRetrySeconds"); }
            if (config.TimeoutSeconds <= 0)   { config.TimeoutSeconds = d.TimeoutSeconds; added = Append(added, "TimeoutSeconds"); }
            if (config.MaxQueuedBatches <= 0) { config.MaxQueuedBatches = d.MaxQueuedBatches; added = Append(added, "MaxQueuedBatches"); }
            return added;
        }

        private static string Append(string list, string key)
        {
            return list == null ? key : list + ", " + key;
        }

        /// <summary>Holds hand-edited values inside what the collector can actually honour. A
        /// one-second window would publish 60 batches a minute for no extra resolution — the sample
        /// underneath it is per frame either way.</summary>
        private void Clamp()
        {
            if (WindowSeconds < 5)      WindowSeconds = 5;
            if (WindowSeconds > 600)    WindowSeconds = 600;
            if (CensusSeconds < 10)     CensusSeconds = 10;
            if (CensusSeconds > 3600)   CensusSeconds = 3600;
            if (EntityCostTopN > 100)   EntityCostTopN = 100;
            if (LedgerTopN > 100)       LedgerTopN = 100;
            if (PbCostTopN > 100)       PbCostTopN = 100;
            if (LedgerSizeExponent > 4) LedgerSizeExponent = 4;
            if (GovernorReleaseBusyPct > GovernorAdjustBusyPct) GovernorReleaseBusyPct = GovernorAdjustBusyPct;
            if (GovernorIntervalSeconds < 30) GovernorIntervalSeconds = 30;
            if (TimeoutSeconds > 60)    TimeoutSeconds = 60;
            if (MaxQueuedBatches > 500) MaxQueuedBatches = 500;
            if (InfluxUrl != null)      InfluxUrl = InfluxUrl.Trim().TrimEnd('/');
        }

        /// <summary>Whether a batch can actually be sent. Collection without a destination is a
        /// supported state — the window still reaches the log and <c>factions perf server</c>.</summary>
        public bool CanPublish
        {
            get
            {
                return Enabled
                    && !string.IsNullOrEmpty(InfluxUrl)
                    && !string.IsNullOrEmpty(Database);
            }
        }
    }
}
