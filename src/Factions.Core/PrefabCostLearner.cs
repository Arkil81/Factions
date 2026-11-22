using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>
    /// What a materialised ship prefab actually costs the server per frame, learned from the
    /// server-perf windows it was present in (operator, 2026-11-21: the ceiling's bench formula let
    /// <c>Alsvidr</c> through at a predicted 0.66 ms while each one flying on production Main cost
    /// 1.3-2.9 ms).
    ///
    /// <para><b>Physics</b> has no per-grid reading: the Havok step is one number per window. It is
    /// recovered by regression across windows,
    /// <c>physics = b0 + k x other + sum_p c_p x n_p</c>, where <c>n_p</c> is how many awake hulls
    /// of prefab <c>p</c> were in the window and <c>other</c> is the ledger's formula prediction
    /// (ms) for every other simulated grid. Each coefficient is ridge-pulled toward its prior — the
    /// formula's own prediction for <c>c_p</c>, 1 for <c>k</c> — with a weight of
    /// <see cref="Options.PriorWindows"/> windows, so a prefab seen twice stays near the formula
    /// and one seen for an hour reads what it costs. Windows where a hull came or went are what
    /// identify it: the regression is an event study generalised to many prefabs at once.</para>
    ///
    /// <para><b>Logic</b> is measured per grid, so it is a plain mean over the hull's grids per
    /// window it was present in. The formula has no logic term at all.</para>
    ///
    /// <para>The statistics decay with a half-life of <see cref="Options.HalfLifeWindows"/>, so a
    /// refit or an SE update is learned over again rather than averaged away.</para>
    /// </summary>
    public sealed class PrefabCostLearner
    {
        public sealed class Options
        {
            /// <summary>How many windows of evidence the formula's prior is worth.</summary>
            public double PriorWindows = 10;

            /// <summary>Statistics halve over this many windows: 20,160 is a week of 30 s windows.</summary>
            public double HalfLifeWindows = 20160;

            /// <summary>Windows a prefab must have been present in before its learned cost is used
            /// instead of the formula.</summary>
            public double MinWindows = 20;

            /// <summary>A prefab whose decayed presence falls below this is dropped from the model.</summary>
            public double ForgetBelowWindows = 0.5;
        }

        /// <summary>One materialised hull of a known prefab in a window: its grids taken together.</summary>
        public struct Hull
        {
            public string PrefabId;
            public bool Awake;
            /// <summary>Measured logic of all its grids, ms per frame.</summary>
            public double LogicMs;
        }

        /// <summary>A prefab's learned cost.</summary>
        public sealed class Estimate
        {
            public string PrefabId;
            public double PhysicsMs;
            public double LogicMs;
            /// <summary>The formula's prediction it was pulled toward, ms per frame.</summary>
            public double PriorMs;
            /// <summary>Decayed count of windows it was present in.</summary>
            public double Windows;
            [JsonIgnore] public double TotalMs { get { return PhysicsMs + LogicMs; } }
        }

        /// <summary>The persisted state. Lean: dense only over features ever seen together.</summary>
        public sealed class Doc
        {
            public int Version = 1;
            public List<string> Prefabs = new List<string>();
            public List<double> PriorMs = new List<double>();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public List<double[]> Ata;
            public List<double> Aty = new List<double>();
            public List<double> Presence = new List<double>();
            public List<double> LogicSum = new List<double>();
            public List<double> LogicCount = new List<double>();
            public double TotalWindows;
            /// <summary>The solved estimates, so a reader (the authority) need not solve.</summary>
            public List<Estimate> Estimates = new List<Estimate>();
        }

        private const int Fixed = 2; // 0 = intercept, 1 = k (other grids)

        private readonly Options _o;
        private readonly List<string> _prefabs = new List<string>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<double> _prior = new List<double>();
        private readonly List<double> _presence = new List<double>();
        private readonly List<double> _logicSum = new List<double>();
        private readonly List<double> _logicCount = new List<double>();
        private double[,] _ata = new double[Fixed, Fixed];
        private double[] _aty = new double[Fixed];
        private double _windows;
        private Dictionary<string, Estimate> _estimates = new Dictionary<string, Estimate>(StringComparer.OrdinalIgnoreCase);

        public PrefabCostLearner(Options options = null) { _o = options ?? new Options(); }

        public double TotalWindows { get { return _windows; } }
        public int PrefabCount { get { return _prefabs.Count; } }

        /// <summary>Folds in one window.</summary>
        /// <param name="measuredPhysicsMs">The window's Havok step plus transforms, ms per frame.</param>
        /// <param name="otherPredictedMs">The formula's prediction for every simulated grid that is not
        /// part of a hull in <paramref name="hulls"/>, ms per frame, in the same units as the prior.</param>
        /// <param name="hulls">Every materialised hull of a known prefab.</param>
        /// <param name="priorMs">The formula's awake prediction for a prefab, ms per frame.</param>
        public void Observe(double measuredPhysicsMs, double otherPredictedMs, IList<Hull> hulls,
                            Func<string, double> priorMs)
        {
            if (double.IsNaN(measuredPhysicsMs) || measuredPhysicsMs < 0) return;
            if (double.IsNaN(otherPredictedMs) || otherPredictedMs < 0) otherPredictedMs = 0;

            double decay = Math.Pow(0.5, 1.0 / Math.Max(1.0, _o.HalfLifeWindows));
            Decay(decay);

            // Awake counts per feature, and logic per prefab.
            var counts = new Dictionary<int, double>();
            if (hulls != null)
            {
                for (int i = 0; i < hulls.Count; i++)
                {
                    Hull h = hulls[i];
                    if (string.IsNullOrEmpty(h.PrefabId)) continue;
                    int f = FeatureOf(h.PrefabId, priorMs);
                    int p = f - Fixed;
                    if (!double.IsNaN(h.LogicMs) && h.LogicMs >= 0) { _logicSum[p] += h.LogicMs; _logicCount[p] += 1; }
                    if (!h.Awake) continue;
                    double c;
                    counts.TryGetValue(f, out c);
                    counts[f] = c + 1;
                }
            }

            var x = new List<KeyValuePair<int, double>>(counts.Count + Fixed);
            x.Add(new KeyValuePair<int, double>(0, 1.0));
            if (otherPredictedMs > 0) x.Add(new KeyValuePair<int, double>(1, otherPredictedMs));
            foreach (KeyValuePair<int, double> kv in counts)
            {
                x.Add(kv);
                _presence[kv.Key - Fixed] += 1;
            }
            for (int a = 0; a < x.Count; a++)
            {
                _aty[x[a].Key] += x[a].Value * measuredPhysicsMs;
                for (int b = 0; b < x.Count; b++)
                    _ata[x[a].Key, x[b].Key] += x[a].Value * x[b].Value;
            }
            _windows += 1;
            Forget();
            Solve();
        }

        private int FeatureOf(string prefabId, Func<string, double> priorMs)
        {
            int p;
            if (_index.TryGetValue(prefabId, out p)) return p + Fixed;
            p = _prefabs.Count;
            _prefabs.Add(prefabId);
            _index[prefabId] = p;
            double prior = priorMs != null ? priorMs(prefabId) : 0.0;
            _prior.Add(double.IsNaN(prior) || prior < 0 ? 0.0 : prior);
            _presence.Add(0);
            _logicSum.Add(0);
            _logicCount.Add(0);
            Grow();
            return p + Fixed;
        }

        private void Grow()
        {
            int n = Fixed + _prefabs.Count;
            var ata = new double[n, n];
            var aty = new double[n];
            int old = _aty.Length;
            for (int i = 0; i < old; i++)
            {
                aty[i] = _aty[i];
                for (int j = 0; j < old; j++) ata[i, j] = _ata[i, j];
            }
            _ata = ata;
            _aty = aty;
        }

        private void Decay(double d)
        {
            int n = _aty.Length;
            for (int i = 0; i < n; i++)
            {
                _aty[i] *= d;
                for (int j = 0; j < n; j++) _ata[i, j] *= d;
            }
            for (int p = 0; p < _prefabs.Count; p++)
            {
                _presence[p] *= d;
                _logicSum[p] *= d;
                _logicCount[p] *= d;
            }
            _windows *= d;
        }

        /// <summary>Drops prefabs whose evidence has decayed away, so the system stays the size of
        /// what this server actually flies.</summary>
        private void Forget()
        {
            var keep = new List<int>();
            for (int p = 0; p < _prefabs.Count; p++)
                if (_presence[p] >= _o.ForgetBelowWindows || _logicCount[p] >= _o.ForgetBelowWindows) keep.Add(p);
            if (keep.Count == _prefabs.Count) return;
            Rebuild(keep);
        }

        private void Rebuild(List<int> keep)
        {
            int n = Fixed + keep.Count;
            var ata = new double[n, n];
            var aty = new double[n];
            Func<int, int> src = i => i < Fixed ? i : keep[i - Fixed] + Fixed;
            for (int i = 0; i < n; i++)
            {
                aty[i] = _aty[src(i)];
                for (int j = 0; j < n; j++) ata[i, j] = _ata[src(i), src(j)];
            }
            var prefabs = new List<string>(); var prior = new List<double>(); var presence = new List<double>();
            var ls = new List<double>(); var lc = new List<double>();
            foreach (int p in keep)
            {
                prefabs.Add(_prefabs[p]); prior.Add(_prior[p]); presence.Add(_presence[p]);
                ls.Add(_logicSum[p]); lc.Add(_logicCount[p]);
            }
            _prefabs.Clear(); _prefabs.AddRange(prefabs);
            _prior.Clear(); _prior.AddRange(prior);
            _presence.Clear(); _presence.AddRange(presence);
            _logicSum.Clear(); _logicSum.AddRange(ls);
            _logicCount.Clear(); _logicCount.AddRange(lc);
            _index.Clear();
            for (int p = 0; p < _prefabs.Count; p++) _index[_prefabs[p]] = p;
            _ata = ata;
            _aty = aty;
        }

        /// <summary>Ridge solve toward the priors: (A'A + L) b = A'y + L mu.</summary>
        private void Solve()
        {
            int n = _aty.Length;
            var m = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) m[i, j] = _ata[i, j];
                double lambda, mu;
                PriorOf(i, out lambda, out mu);
                m[i, i] += lambda;
                m[i, n] = _aty[i] + lambda * mu;
            }
            double[] b = GaussSolve(m, n);
            var est = new Dictionary<string, Estimate>(StringComparer.OrdinalIgnoreCase);
            for (int p = 0; p < _prefabs.Count; p++)
            {
                double phys = b != null ? b[p + Fixed] : _prior[p];
                est[_prefabs[p]] = new Estimate
                {
                    PrefabId = _prefabs[p],
                    PhysicsMs = Math.Max(0.0, phys),
                    LogicMs = _logicCount[p] > 0 ? _logicSum[p] / _logicCount[p] : 0.0,
                    PriorMs = _prior[p],
                    Windows = Math.Max(_presence[p], _logicCount[p]),
                };
            }
            _estimates = est;
        }

        private void PriorOf(int feature, out double lambda, out double mu)
        {
            if (feature == 0) { lambda = 1e-3; mu = 0; return; }                 // intercept: free
            if (feature == 1) { lambda = _o.PriorWindows; mu = 1.0; return; }    // other grids: formula is right
            lambda = _o.PriorWindows;
            mu = _prior[feature - Fixed];
        }

        private static double[] GaussSolve(double[,] m, int n)
        {
            for (int c = 0; c < n; c++)
            {
                int piv = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
                if (Math.Abs(m[piv, c]) < 1e-12) return null;
                if (piv != c)
                    for (int k = c; k <= n; k++) { double t = m[c, k]; m[c, k] = m[piv, k]; m[piv, k] = t; }
                for (int r = c + 1; r < n; r++)
                {
                    double f = m[r, c] / m[c, c];
                    if (f == 0) continue;
                    for (int k = c; k <= n; k++) m[r, k] -= f * m[c, k];
                }
            }
            var x = new double[n];
            for (int r = n - 1; r >= 0; r--)
            {
                double s = m[r, n];
                for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k];
                x[r] = s / m[r, r];
            }
            return x;
        }

        public Estimate Get(string prefabId)
        {
            Estimate e;
            return !string.IsNullOrEmpty(prefabId) && _estimates.TryGetValue(prefabId, out e) ? e : null;
        }

        public List<Estimate> Estimates()
        {
            var list = new List<Estimate>(_estimates.Values);
            list.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
            return list;
        }

        public Doc ToDoc()
        {
            var d = new Doc { TotalWindows = _windows };
            int n = _aty.Length;
            d.Prefabs.AddRange(_prefabs);
            d.PriorMs.AddRange(_prior);
            d.Presence.AddRange(_presence);
            d.LogicSum.AddRange(_logicSum);
            d.LogicCount.AddRange(_logicCount);
            d.Aty.AddRange(_aty);
            d.Ata = new List<double[]>(n);
            for (int i = 0; i < n; i++)
            {
                var row = new double[n];
                for (int j = 0; j < n; j++) row[j] = _ata[i, j];
                d.Ata.Add(row);
            }
            d.Estimates = Estimates();
            return d;
        }

        /// <summary>Restores a learner from its document; a malformed one restores nothing.</summary>
        public static PrefabCostLearner FromDoc(Doc d, Options options = null)
        {
            var l = new PrefabCostLearner(options);
            if (d == null || d.Prefabs == null) return l;
            int p = d.Prefabs.Count, n = Fixed + p;
            if (Len(d.PriorMs) != p || Len(d.Presence) != p || Len(d.LogicSum) != p || Len(d.LogicCount) != p
                || Len(d.Aty) != n || d.Ata == null || d.Ata.Count != n) return l;
            for (int i = 0; i < n; i++) if (d.Ata[i] == null || d.Ata[i].Length != n) return l;
            for (int i = 0; i < p; i++)
            {
                l._prefabs.Add(d.Prefabs[i]);
                l._index[d.Prefabs[i]] = i;
            }
            l._prior.AddRange(d.PriorMs);
            l._presence.AddRange(d.Presence);
            l._logicSum.AddRange(d.LogicSum);
            l._logicCount.AddRange(d.LogicCount);
            l._ata = new double[n, n];
            l._aty = new double[n];
            for (int i = 0; i < n; i++)
            {
                l._aty[i] = d.Aty[i];
                for (int j = 0; j < n; j++) l._ata[i, j] = d.Ata[i][j];
            }
            l._windows = d.TotalWindows;
            l.Solve();
            return l;
        }

        private static int Len<T>(List<T> l) { return l == null ? -1 : l.Count; }

        /// <summary>Combines the estimates several servers learned, weighting each by the windows it
        /// was present in there. Each server's own regression has its own baseline, so estimates are
        /// merged rather than statistics.</summary>
        public static Dictionary<string, Estimate> Merge(IEnumerable<IEnumerable<Estimate>> sets)
        {
            var sum = new Dictionary<string, Estimate>(StringComparer.OrdinalIgnoreCase);
            if (sets == null) return sum;
            foreach (IEnumerable<Estimate> set in sets)
            {
                if (set == null) continue;
                foreach (Estimate e in set)
                {
                    if (e == null || string.IsNullOrEmpty(e.PrefabId) || !(e.Windows > 0)) continue;
                    Estimate acc;
                    if (!sum.TryGetValue(e.PrefabId, out acc))
                        sum[e.PrefabId] = acc = new Estimate { PrefabId = e.PrefabId, PriorMs = e.PriorMs };
                    acc.PhysicsMs += e.PhysicsMs * e.Windows;
                    acc.LogicMs += e.LogicMs * e.Windows;
                    acc.Windows += e.Windows;
                }
            }
            foreach (Estimate acc in sum.Values)
            {
                acc.PhysicsMs /= acc.Windows;
                acc.LogicMs /= acc.Windows;
            }
            return sum;
        }

        /// <summary>The <c>factions serverperf prefabs</c> response: learned against formula,
        /// costliest first.</summary>
        public static string Format(IList<Estimate> estimates, double minWindows, double ceilingMs, int n)
        {
            if (estimates == null || estimates.Count == 0)
                return "serverperf prefabs: nothing learned yet (no materialised hull of a known prefab in a window with per-entity cost).";
            CultureInfo inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("serverperf prefabs: ").Append(estimates.Count).Append(" learned, ms/frame awake")
              .Append("; used once present in ").Append(minWindows.ToString("0", inv)).Append(" windows")
              .Append(", ceiling ").Append(ceilingMs.ToString("F2", inv)).Append(':');
            int shown = Math.Min(n, estimates.Count);
            for (int i = 0; i < shown; i++)
            {
                Estimate e = estimates[i];
                sb.Append('\n').Append(e.PrefabId).Append(' ').Append(e.TotalMs.ToString("F2", inv))
                  .Append(" (physics ").Append(e.PhysicsMs.ToString("F2", inv))
                  .Append(" + logic ").Append(e.LogicMs.ToString("F2", inv))
                  .Append("; formula ").Append(e.PriorMs.ToString("F2", inv))
                  .Append("; ").Append(e.Windows.ToString("0.#", inv)).Append(" windows")
                  .Append(e.Windows >= minWindows ? (e.TotalMs > ceilingMs ? "; OVER" : "") : "; not yet used")
                  .Append(')');
            }
            if (estimates.Count > shown) sb.Append("\n+").Append(estimates.Count - shown).Append(" more");
            return sb.ToString();
        }
    }
}
