using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;

namespace Factions
{
    /// <summary>The per-prefab learned cost (<see cref="PrefabCostLearner"/>): each window that has a
    /// ledger, every materialised Factions hull of a known prefab is grouped with the rotor
    /// subgrids it carries and folded into the learner against the window's measured physics.
    /// Runs on every server that materialises fleets; an agent's estimates travel to the
    /// authority in its report, and the authority's ceiling reads them all.</summary>
    internal sealed partial class ServerPerfMonitor
    {
        public const string PrefabCostsFileName = "server_perf_prefabs.json";

        /// <summary>Windows between saves of the learned table: five minutes at the default window.</summary>
        private const int PrefabCostSaveEvery = 10;

        /// <summary>Most estimates an agent sends per report, costliest first.</summary>
        private const int PrefabCostReportMax = 64;

        /// <summary>Prefab id of a materialised fleet hull by its entity id; null for any other grid.
        /// Set by the plugin.</summary>
        internal Func<long, string> PrefabOfGrid;

        private PrefabCostLearner _prefabCosts = new PrefabCostLearner();
        private string _prefabCostsPath;
        private int _prefabCostWindowsUnsaved;
        private bool _prefabCostsFresh;

        internal PrefabCostLearner PrefabCosts { get { return _prefabCosts; } }

        /// <summary>Loads the table this server learned before its restart, from
        /// <paramref name="dir"/>. A missing or unreadable file starts empty.</summary>
        internal string LoadPrefabCosts(string dir)
        {
            _prefabCostsPath = Path.Combine(dir, PrefabCostsFileName);
            if (!File.Exists(_prefabCostsPath)) return "Prefab costs: none learned yet (" + PrefabCostsFileName + ").";
            try
            {
                var doc = JsonConvert.DeserializeObject<PrefabCostLearner.Doc>(File.ReadAllText(_prefabCostsPath));
                _prefabCosts = PrefabCostLearner.FromDoc(doc);
                return "Prefab costs: " + _prefabCosts.PrefabCount + " prefab(s) over "
                       + _prefabCosts.TotalWindows.ToString("0") + " window(s) loaded from " + PrefabCostsFileName + ".";
            }
            catch (Exception ex)
            {
                _prefabCosts = new PrefabCostLearner();
                return "Prefab costs: " + PrefabCostsFileName + " unreadable (" + ex.Message + "); starting empty.";
            }
        }

        internal void SavePrefabCosts()
        {
            if (_prefabCostsPath == null || _prefabCostWindowsUnsaved == 0) return;
            try
            {
                string tmp = _prefabCostsPath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(_prefabCosts.ToDoc()));
                if (File.Exists(_prefabCostsPath)) File.Delete(_prefabCostsPath);
                File.Move(tmp, _prefabCostsPath);
                _prefabCostWindowsUnsaved = 0;
            }
            catch (Exception ex)
            {
                Log.Warn("Prefab costs: could not write " + PrefabCostsFileName + ": " + ex.Message);
            }
        }

        /// <summary>The estimates once per window that changed them, null otherwise: the agent's report
        /// carries a list only when there is a new one.</summary>
        internal List<PrefabCostLearner.Estimate> TakeFreshPrefabCosts()
        {
            if (!_prefabCostsFresh) return null;
            _prefabCostsFresh = false;
            List<PrefabCostLearner.Estimate> all = _prefabCosts.Estimates();
            if (all.Count > PrefabCostReportMax) all.RemoveRange(PrefabCostReportMax, all.Count - PrefabCostReportMax);
            return all;
        }

        private struct HullSum
        {
            public string PrefabId;
            public int Blocks;
            public bool Awake;
            public double LogicMs;
        }

        private void ObservePrefabCosts(EntityCostPatches.Window w, double parallelScale)
        {
            Func<long, string> prefabOf = PrefabOfGrid;
            if (prefabOf == null || w == null || w.AllGrids == null) return;
            if (double.IsNaN(_physicsMsPerFrame) || _physicsMsPerFrame < 0) return;
            try
            {
                double coef = _config.PrefabCostCoefficient, exp = _config.LedgerSizeExponent;
                double awakeMult = _config.LedgerAwakeMultiplier > 0 ? _config.LedgerAwakeMultiplier : 1.0;
                var hulls = new Dictionary<long, HullSum>();
                double other = 0;
                foreach (GridCensus.SimGridRow row in _census.Simulated)
                {
                    string prefab = row.OnFleetHull ? prefabOf(row.HullId) : null;
                    if (string.IsNullOrEmpty(prefab))
                    {
                        if (!row.IsStatic && row.Blocks > 0)
                            other += coef * Math.Pow(row.Blocks, exp) / (row.Awake ? 1.0 : awakeMult);
                        continue;
                    }
                    HullSum h;
                    hulls.TryGetValue(row.HullId, out h);
                    h.PrefabId = prefab;
                    h.Blocks += row.Blocks;
                    if (row.Awake && !row.IsStatic) h.Awake = true;
                    EntityCostPatches.GridTicks ticks;
                    if (w.AllGrids.TryGetValue(row.GridId, out ticks))
                        h.LogicMs += PerFrameRaw(ToMs(ticks.Serial)) + PerFrameRaw(ToMs(ticks.Parallel)) * parallelScale;
                    hulls[row.HullId] = h;
                }

                var list = new List<PrefabCostLearner.Hull>(hulls.Count);
                var blocksOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (HullSum h in hulls.Values)
                {
                    // An asleep hull costs what the formula says an asleep grid does, and that stays
                    // with the other grids; only an awake one identifies its prefab's physics.
                    if (!h.Awake) other += coef * Math.Pow(h.Blocks, exp) / awakeMult;
                    blocksOf[h.PrefabId] = h.Blocks;
                    list.Add(new PrefabCostLearner.Hull { PrefabId = h.PrefabId, Awake = h.Awake, LogicMs = h.LogicMs });
                }
                _prefabCosts.Observe(_physicsMsPerFrame, other, list,
                    id => { int b; return blocksOf.TryGetValue(id, out b) && b > 0 ? coef * Math.Pow(b, exp) : 0.0; });
                _prefabCostsFresh = true;
                if (++_prefabCostWindowsUnsaved >= PrefabCostSaveEvery) SavePrefabCosts();
            }
            catch (Exception ex)
            {
                Log.Warn("Prefab costs: window not learned: " + ex.Message);
            }
        }
    }
}
