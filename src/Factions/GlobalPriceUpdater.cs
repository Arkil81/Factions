using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;
using NLog;

namespace Factions
{
    /// <summary>
    /// Runs a weekly volatility-damper pass over <c>item_prices.json</c> base prices.
    ///
    /// <para>Each cycle: collect the mean scarcity multiplier across all nodes that had
    /// activity since the last update, then apply the damper:
    /// <c>new_base = old_base + α × (old_base × mean_scarcity − old_base)</c>
    ///           = <c>old_base × (1 + α × (mean_scarcity − 1))</c>
    /// This lets sustained scarcity or glut pull the base price toward its real value
    /// without a single week's spike permanently repricing an item.</para>
    ///
    /// <para>Only nodes that have had at least one player or NPC trade transaction since
    /// the last cycle contribute to the mean. Dead nodes don't anchor prices.</para>
    /// </summary>
    internal sealed class GlobalPriceUpdater
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>Default cycle period (1 week real-time).</summary>
        public static readonly TimeSpan DefaultCyclePeriod = TimeSpan.FromDays(7);

        /// <summary>Volatility damper learning rate. 0.5 = halfway to the new average each cycle.</summary>
        public const double DefaultAlpha = 0.5;

        private readonly WorldModel          _world;
        private readonly IPriceOracle        _oracle;
        private readonly IItemRegistry       _registry;
        private readonly string              _storageDir;
        private readonly FactionTuning       _tuning;
        private readonly TimeSpan            _cyclePeriod;
        private readonly double              _alpha;

        private DateTime _lastCycleUtc = DateTime.MinValue;

        // Nodes that have seen trade activity (player or NPC) since the last cycle.
        private readonly HashSet<string> _activeNodes = new HashSet<string>();

        public GlobalPriceUpdater(
            WorldModel world,
            IPriceOracle oracle,
            IItemRegistry registry,
            string storageDir,
            FactionTuning tuning,
            TimeSpan? cyclePeriod = null,
            double alpha = DefaultAlpha)
        {
            _world       = world;
            _oracle      = oracle;
            _registry    = registry;
            _storageDir  = storageDir;
            _tuning      = tuning;
            _cyclePeriod = cyclePeriod ?? DefaultCyclePeriod;
            _alpha       = alpha;
        }

        /// <summary>Mark a node as having had trade activity this cycle.</summary>
        public void RecordActivity(string nodeId)
        {
            if (!string.IsNullOrEmpty(nodeId))
                _activeNodes.Add(nodeId);
        }

        /// <summary>
        /// Check whether a cycle is due and run it. Call from the game thread once per update frame.
        /// The actual work is cheap (arithmetic over active nodes); no SE API calls.
        /// </summary>
        public void Tick(DateTime utcNow)
        {
            if (utcNow - _lastCycleUtc < _cyclePeriod) return;
            RunCycle(utcNow);
        }

        // ---- internals --------------------------------------------------------------------------

        internal void RunCycle(DateTime utcNow)
        {
            if (_activeNodes.Count == 0)
            {
                _lastCycleUtc = utcNow;
                return;
            }

            // The nodes this cycle's mean is taken over, resolved once.
            var liveNodes = new List<string>();
            foreach (string nodeId in _activeNodes)
                if (_world.Graph.Nodes.ContainsKey(nodeId)) liveNodes.Add(nodeId);

            if (liveNodes.Count == 0)
            {
                _lastCycleUtc = utcNow;
                _activeNodes.Clear();
                return;
            }

            // Load current prices from disk.
            string path = Path.Combine(_storageDir, "item_prices.json");
            Dictionary<string, long> prices;
            try
            {
                prices = File.Exists(path)
                    ? JsonConvert.DeserializeObject<Dictionary<string, long>>(File.ReadAllText(path))
                      ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "GlobalPriceUpdater: failed to read item_prices.json; skipping cycle.");
                _lastCycleUtc = utcNow;
                _activeNodes.Clear();
                return;
            }

            // Apply damper: new_base = old_base × (1 + α × (mean_scarcity − 1))
            bool changed = false;
            var updated  = new Dictionary<string, long>(prices, StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, long> kv in prices)
            {
                if (kv.Value <= 0) continue;

                string subType = kv.Key;

                // Recognition only — an item the registry cannot place has no scarcity to read.
                ItemKind ignoredKind;
                if (!_registry.TryGetItemKind(subType, out ignoredKind)) continue;

                // The mean is taken over THIS item at each active node. Taking it per ItemKind made
                // one number set the drift for every subtype in that kind, so gold's base price
                // followed the gravel that shares its kind. A node holding no reference for the item
                // returns a neutral 1.0 and so contributes nothing either way, exactly as a node
                // with no reference for the kind did before.
                double scarcitySum = 0;
                for (int i = 0; i < liveNodes.Count; i++)
                    scarcitySum += _oracle.ScarcityMultiplier(liveNodes[i], subType);

                double meanScarcity = scarcitySum / liveNodes.Count;
                double newBase = kv.Value * (1.0 + _alpha * (meanScarcity - 1.0));
                long   rounded = Math.Max(1L, (long)Math.Round(newBase));
                if (rounded == kv.Value) continue;

                updated[subType] = rounded;
                changed = true;
            }

            if (!changed)
            {
                _lastCycleUtc = utcNow;
                _activeNodes.Clear();
                return;
            }

            try
            {
                Directory.CreateDirectory(_storageDir);
                File.WriteAllText(path, JsonConvert.SerializeObject(updated, Formatting.Indented));
                Log.Info(string.Format(
                    "GlobalPriceUpdater: price cycle complete. Active nodes={0}, items updated={1}.",
                    _activeNodes.Count, updated.Count));
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "GlobalPriceUpdater: failed to write item_prices.json.");
            }

            _lastCycleUtc = utcNow;
            _activeNodes.Clear();
        }
    }
}
