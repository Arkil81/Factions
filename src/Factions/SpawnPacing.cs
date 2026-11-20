using System;
using System.Collections.Generic;
using System.Diagnostics;
using Factions.Core;
using NLog;

namespace Factions
{
    /// <summary>The one <see cref="SpawnPacer"/> every Factions spawn waits on: fleet hulls
    /// (<see cref="VanillaFleetSpawner"/>, keys <c>fleet:</c>) and stations
    /// (<see cref="StationMaterializer"/>, keys <c>station:</c>). Spacing is
    /// <c>SpawnSpacingSeconds</c> in <c>server_perf.json</c>, set at plugin init.
    ///
    /// <para><b>The landing frame is measured here.</b> SE adds a prefab's grids to the world and then
    /// runs our landing callback in one main-thread step, so neither the callback's own stopwatch nor a
    /// prediction sees the whole cost. A landing is noted when the callback runs; the next frame tick
    /// takes the tick-to-tick time across it, which holds SE's add as well, and hands it to the pacer,
    /// which spaces the next spawn by it.</para></summary>
    internal static class SpawnPacing
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        internal static readonly SpawnPacer Pacer = new SpawnPacer(1.0);

        private static readonly Dictionary<string, string> PacingKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary><c>"fleet:" + fleetKey</c>, built once per key: a fleet waiting its turn asks every frame.</summary>
        internal static string FleetKey(string fleetKey)
        {
            if (fleetKey == null) return "fleet:";
            string k;
            if (PacingKeys.TryGetValue(fleetKey, out k)) return k;
            if (PacingKeys.Count > 8192) PacingKeys.Clear();
            k = "fleet:" + fleetKey;
            PacingKeys[fleetKey] = k;
            return k;
        }
        internal static string StationKey(string nodeId) { return "station:" + nodeId; }

        private static long _lastTick;
        private static string _landedKey;
        private static string _landedDetail;

        /// <summary>A spawn landed this frame. <paramref name="detail"/> is the SPAWN_COST line's
        /// description of it; only built when the trace is on.</summary>
        internal static void NoteLanding(string pacerKey, string detail)
        {
            _landedKey = pacerKey;
            _landedDetail = detail;
        }

        /// <summary>Once per frame: the time since the last tick, handed to the pacer when a landing
        /// happened in between.</summary>
        internal static void TickFrame()
        {
            long now = Stopwatch.GetTimestamp();
            if (_landedKey != null && _lastTick != 0)
            {
                double frameMs = (now - _lastTick) * 1000.0 / Stopwatch.Frequency;
                Pacer.ReportLandingFrame(_landedKey, frameMs);
                if (DebugTraces.SpawnCost)
                    Log.Info("SPAWN_COST " + _landedKey + " frame=" + frameMs.ToString("F0") + "ms gap="
                             + Pacer.GapSeconds.ToString("F1") + "s" + (_landedDetail ?? ""));
                _landedKey = null;
                _landedDetail = null;
            }
            _lastTick = now;
        }
    }
}
