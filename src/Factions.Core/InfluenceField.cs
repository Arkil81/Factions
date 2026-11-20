using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Layer 2 — the influence heatmap, DERIVED from Layer-1 sources (the owned graph nodes).
    /// Per-source exponential falloff; nothing is materialized — every value is computed on demand.
    ///
    /// Call <see cref="BuildSnapshot"/> once at the start of each economy tick and each brain tick
    /// to pre-bake all (node, faction) sample values. Hot methods (Pressure, ControlAt)
    /// use the snapshot for O(1) lookups instead of recomputing the full O(nodes) exponential sum.
    /// Without the snapshot the influence loops are O(nodes² × factions) with Math.Exp — acceptable
    /// for a handful of nodes but ~7M Math.Exp calls per tick with 300+ nodes across 20 factions.
    /// </summary>
    public sealed class InfluenceField : IInfluenceField
    {
        private readonly SpatialGraph _graph;

        // Per-tick snapshot: _snap[queryNodeId] → (factionTag → total influence at that node from that faction).
        // Rebuilt by BuildSnapshot() before each expensive tick; O(1) to query after that.
        // Inner dicts are reused across builds to minimise allocations.
        private Dictionary<string, Dictionary<string, double>> _snap =
            new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

        // Resumable rebuild state (see BeginSnapshotBuild/AdvanceSnapshotBuild/CommitSnapshotBuild
        // below) — a second buffer that ping-pongs with _snap on commit, so row dictionaries are
        // reused across builds the same way the original single-buffer BuildSnapshot() reused them,
        // without ever exposing a partially-built row set to a reader mid-rebuild.
        private Dictionary<string, Dictionary<string, double>> _alt =
            new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        private List<string> _buildOrder;
        private int _buildIndex;
        private int _buildStartNodeCount;
        private bool _buildInProgress;

        // Read through an accessor, not a captured reference: WorldModel.Tuning is a settable property
        // and a hot reload replaces the object, so a snapshot taken at construction would go stale.
        private readonly Func<FactionTuning> _tuning;

        public InfluenceField(SpatialGraph graph, Func<FactionTuning> tuning = null)
        {
            _graph = graph;
            _tuning = tuning;
        }

        /// <summary>The rival influence-mass share that <see cref="Pressure"/> reports as 1.0.
        /// Falls back to the shipped constant when no tuning is wired up (a bare field built for
        /// <see cref="RivalTags"/> alone) or when a loaded file carries a nonsense value — zero here
        /// would divide, and a negative would invert the whole drive chain.</summary>
        /// <summary>The live tuning, or null when none is wired up (a bare field built for
        /// <see cref="RivalTags"/> alone). <see cref="InfluenceRadius.For"/> handles null itself.</summary>
        private FactionTuning Tuning()
        {
            return _tuning == null ? null : _tuning();
        }

        private double FullContestShare()
        {
            FactionTuning t = Tuning();
            double share = t == null ? FactionTuning.DefaultBorderPressureFullContestShare
                                     : t.BorderPressureFullContestShare;
            return share > 0 ? share : FactionTuning.DefaultBorderPressureFullContestShare;
        }

        /// <summary>Pre-compute all Sample(nodePosition, faction) values for the current tick.
        /// Cost: O(nodes × source_nodes) arithmetic — ~90K multiplies for a 300-node world with
        /// 20 factions, versus ~7M Math.Exp without the cache.  Must be called before AccrueIncome
        /// (economy tick) and before the brain loop (StepOnce), since DecayInfluence between them
        /// changes HoldStrength and invalidates the previous snapshot.</summary>
        internal void BuildSnapshot()
        {
            // Clear existing inner dicts so they can be reused without re-allocating.
            foreach (Dictionary<string, double> row in _snap.Values)
                row.Clear();

            foreach (KeyValuePair<string, SpatialNode> kvQ in _graph.Nodes)
            {
                Dictionary<string, double> row;
                if (!_snap.TryGetValue(kvQ.Key, out row))
                {
                    row = new Dictionary<string, double>(4, StringComparer.Ordinal);
                    _snap[kvQ.Key] = row;
                }

                Vec3 qPos = kvQ.Value.Position;
                foreach (SpatialNode src in _graph.Nodes.Values)
                {
                    if (src.OwnerFactionTag == null || src.OwnerFactionTag == NeutralFaction.Tag) continue;
                    double r = InfluenceRadius.For(src, Tuning());
                    double cutoffSq = r * r * 16; // (4r)²
                    double distSq = src.Position.DistanceSquaredTo(qPos);
                    if (distSq > cutoffSq) continue;
                    double dist = Math.Sqrt(distSq); // sqrt only for in-range pairs
                    double c = src.HoldStrength * Math.Exp(-dist / r);
                    double cur;
                    row.TryGetValue(src.OwnerFactionTag, out cur);
                    row[src.OwnerFactionTag] = cur + c;
                }
            }

            // Drop rows whose nodes were removed from the graph between builds.
            var toRemove = new List<string>();
            foreach (string key in _snap.Keys)
                if (!_graph.Nodes.ContainsKey(key)) toRemove.Add(key);
            foreach (string key in toRemove) _snap.Remove(key);
        }

        // ---- Resumable rebuild (host-driven, one query-node row per step) --------------------------
        // At live scale this world's node count makes the O(nodes × source_nodes) full rebuild take
        // several hundred milliseconds in one call (live-measured 2026-08-17, ~93% of the Factions
        // "econ" perf bucket) -- far over a single Torch frame's budget. BuildSnapshot() above remains
        // the atomic all-in-one entry point (used by the offline Step()/Sim path and tests, which are
        // allowed to block); this resumable variant lets a live-cadence caller spread the same work
        // across many frames while every reader (SampleAt/ControlAt/etc., which only ever read _snap)
        // keeps seeing the last fully-committed snapshot until CommitSnapshotBuild swaps it in as one
        // atomic reference assignment -- no reader ever observes a partially-built row set.

        /// <summary>Starts a new resumable rebuild. Must be paired with a later
        /// <see cref="CommitSnapshotBuild"/> or <see cref="AbandonSnapshotBuild"/> before calling this
        /// again.</summary>
        internal void BeginSnapshotBuild()
        {
            _buildOrder = new List<string>(_graph.Nodes.Keys);
            _buildIndex = 0;
            _buildStartNodeCount = _graph.Nodes.Count;
            _buildInProgress = true;
            foreach (Dictionary<string, double> row in _alt.Values) row.Clear();
        }

        /// <summary>Computes exactly one query-node row (the same inner-loop body as
        /// <see cref="BuildSnapshot"/>) into the private build buffer. Returns true once every row in
        /// this build has been processed -- the caller must then call <see cref="CommitSnapshotBuild"/>
        /// (after checking <see cref="IsSnapshotBuildStale"/>) or <see cref="AbandonSnapshotBuild"/>.</summary>
        internal bool AdvanceSnapshotBuild()
        {
            if (_buildIndex >= _buildOrder.Count) return true;

            string queryId = _buildOrder[_buildIndex];
            SpatialNode qNode;
            if (_graph.Nodes.TryGetValue(queryId, out qNode))
            {
                Dictionary<string, double> row;
                if (!_alt.TryGetValue(queryId, out row))
                {
                    row = new Dictionary<string, double>(4, StringComparer.Ordinal);
                    _alt[queryId] = row;
                }

                Vec3 qPos = qNode.Position;
                foreach (SpatialNode src in _graph.Nodes.Values)
                {
                    if (src.OwnerFactionTag == null || src.OwnerFactionTag == NeutralFaction.Tag) continue;
                    double r = InfluenceRadius.For(src, Tuning());
                    double cutoffSq = r * r * 16; // (4r)²
                    double distSq = src.Position.DistanceSquaredTo(qPos);
                    if (distSq > cutoffSq) continue;
                    double dist = Math.Sqrt(distSq); // sqrt only for in-range pairs
                    double c = src.HoldStrength * Math.Exp(-dist / r);
                    double cur;
                    row.TryGetValue(src.OwnerFactionTag, out cur);
                    row[src.OwnerFactionTag] = cur + c;
                }
            }
            // else: this query node was removed from the graph mid-build -- simply produce no row for
            // it, matching BuildSnapshot()'s own post-hoc pruning of rows for nodes that no longer exist.

            _buildIndex++;
            return _buildIndex >= _buildOrder.Count;
        }

        /// <summary>Cheap (O(1)) signal that the graph's node count changed since
        /// <see cref="BeginSnapshotBuild"/> -- the most structurally disruptive class of mid-build
        /// drift (a query node's row now missing, or a new node absent from this build entirely).
        /// A caller that sees this true should discard the in-progress build (<see cref="AbandonSnapshotBuild"/>)
        /// and start fresh rather than commit a structurally-stale result. Pure ownership/hold-strength
        /// drift on nodes that still exist is accepted as a bounded, self-correcting approximation --
        /// the same order of staleness the existing periodic rebuild cadence already tolerates between
        /// passes, and far cheaper than re-verifying full node state on every resumed step, which would
        /// repay the exact O(nodes) cost this chunking exists to avoid.</summary>
        internal bool IsSnapshotBuildStale()
        {
            return _buildInProgress && _graph.Nodes.Count != _buildStartNodeCount;
        }

        // ---- Diagnostics only (live troubleshooting, 2026-08-17) ------------------------------------
        internal int DiagnosticBuildIndex { get { return _buildIndex; } }
        internal int DiagnosticBuildTotal { get { return _buildOrder == null ? 0 : _buildOrder.Count; } }
        internal bool DiagnosticBuildInProgress { get { return _buildInProgress; } }

        /// <summary>Publishes a completed build as the new active snapshot -- a single reference swap,
        /// so every reader sees either the fully-old or the fully-new snapshot, never a mix.</summary>
        internal void CommitSnapshotBuild()
        {
            var toRemove = new List<string>();
            foreach (string key in _alt.Keys)
                if (!_graph.Nodes.ContainsKey(key)) toRemove.Add(key);
            foreach (string key in toRemove) _alt.Remove(key);

            Dictionary<string, Dictionary<string, double>> committed = _alt;
            _alt = _snap;
            _snap = committed;
            _buildInProgress = false;
            _buildOrder = null;
        }

        /// <summary>Discards an in-progress build without publishing it. The active snapshot (last
        /// commit) is unaffected.</summary>
        internal void AbandonSnapshotBuild()
        {
            _buildInProgress = false;
            _buildOrder = null;
        }

        // O(1) lookup when snapshot is available; O(nodes) Math.Exp fallback otherwise.
        public double SampleAt(string nodeId, string factionTag)
        {
            Dictionary<string, double> row;
            if (_snap.TryGetValue(nodeId, out row))
            {
                double v;
                return row.TryGetValue(factionTag, out v) ? v : 0.0;
            }
            SpatialNode n;
            return _graph.Nodes.TryGetValue(nodeId, out n) ? Sample(n.Position, factionTag) : 0.0;
        }

        // Cached DominantAt by nodeId — O(factions) scan of the snapshot row instead of O(nodes) Math.Exp.
        private void DominantAtNode(string nodeId, out string topTag, out double topVal, out double secondVal)
        {
            topTag = null; topVal = 0; secondVal = 0;
            Dictionary<string, double> row;
            if (_snap.TryGetValue(nodeId, out row))
            {
                foreach (KeyValuePair<string, double> kv in row)
                {
                    if (kv.Value > topVal) { secondVal = topVal; topVal = kv.Value; topTag = kv.Key; }
                    else if (kv.Value > secondVal) secondVal = kv.Value;
                }
                return;
            }
            // Fallback: live computation.
            SpatialNode n;
            if (_graph.Nodes.TryGetValue(nodeId, out n))
                DominantAt(n.Position, out topTag, out topVal, out secondVal);
        }

        /// <summary>Snapshot-accelerated Control for a graph node by ID.
        /// Used by WorldModel.AccrueIncome where the query is always at a node position.</summary>
        public double ControlAt(string nodeId, string factionTag)
        {
            double mine = SampleAt(nodeId, factionTag);
            string top; double topVal, secondVal;
            DominantAtNode(nodeId, out top, out topVal, out secondVal);
            double bestRival = (top == factionTag) ? secondVal : topVal;
            double denom = mine + bestRival;
            return denom <= 1e-9 ? 1.0 : mine / denom;
        }

        public double Sample(Vec3 pos, string factionTag)
        {
            double sum = 0;
            foreach (SpatialNode n in _graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != factionTag) continue;
                double r = InfluenceRadius.For(n, Tuning());
                double dist = n.Position.DistanceTo(pos);
                if (dist > r * 4) continue;
                sum += n.HoldStrength * Math.Exp(-dist / r);
            }
            return sum;
        }

        /// <summary>Dominant faction + its value at a point, plus the runner-up value (for contest).</summary>
        public void DominantAt(Vec3 pos, out string topTag, out double topVal, out double secondVal)
        {
            DominantAt(pos, _graph.Nodes.Values, new Dictionary<string, double>(), out topTag, out topVal, out secondVal);
        }

        /// <summary><see cref="DominantAt(Vec3, out string, out double, out double)"/> over
        /// <paramref name="among"/> only. Exact when <paramref name="among"/> holds every owned node
        /// within four radii of <paramref name="pos"/>, which <see cref="NodesReaching"/> guarantees
        /// for any point on its polyline. <paramref name="scratch"/> is cleared and reused.</summary>
        public void DominantAt(Vec3 pos, IEnumerable<SpatialNode> among, Dictionary<string, double> scratch,
                               out string topTag, out double topVal, out double secondVal)
        {
            topTag = null; topVal = 0; secondVal = 0;
            scratch.Clear();
            FactionTuning t = Tuning();
            foreach (SpatialNode n in among)
            {
                if (n.OwnerFactionTag == null || n.OwnerFactionTag == NeutralFaction.Tag) continue;
                double r = InfluenceRadius.For(n, t);
                double dist = n.Position.DistanceTo(pos);
                if (dist > r * 4) continue;
                double c = n.HoldStrength * Math.Exp(-dist / r);
                double cur; scratch.TryGetValue(n.OwnerFactionTag, out cur);
                scratch[n.OwnerFactionTag] = cur + c;
            }
            foreach (var kv in scratch)
            {
                if (kv.Value > topVal) { secondVal = topVal; topVal = kv.Value; topTag = kv.Key; }
                else if (kv.Value > secondVal) secondVal = kv.Value;
            }
        }

        /// <summary>Every owned node whose influence reaches the polyline: within four of its radii
        /// of some segment. The set the subset <c>DominantAt</c> needs to read any point on the
        /// route exactly, found in one pass over the graph.</summary>
        public List<SpatialNode> NodesReaching(IList<Vec3> polylineKm)
        {
            var result = new List<SpatialNode>();
            if (polylineKm == null || polylineKm.Count == 0) return result;
            FactionTuning t = Tuning();
            foreach (SpatialNode n in _graph.Nodes.Values)
            {
                if (n.OwnerFactionTag == null || n.OwnerFactionTag == NeutralFaction.Tag) continue;
                double reach = InfluenceRadius.For(n, t) * 4;
                if (DistanceToPolylineKm(polylineKm, n.Position) <= reach) result.Add(n);
            }
            return result;
        }

        private static double DistanceToPolylineKm(IList<Vec3> polylineKm, Vec3 p)
        {
            if (polylineKm.Count == 1) return polylineKm[0].DistanceTo(p);
            double best = double.MaxValue;
            for (int i = 0; i < polylineKm.Count - 1; i++)
            {
                Vec3 a = polylineKm[i], b = polylineKm[i + 1];
                double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
                double lengthSq = abx * abx + aby * aby + abz * abz;
                double apx = p.X - a.X, apy = p.Y - a.Y, apz = p.Z - a.Z;
                double s = lengthSq <= 1e-12 ? 0 : (apx * abx + apy * aby + apz * abz) / lengthSq;
                if (s < 0) s = 0; else if (s > 1) s = 1;
                double dx = apx - abx * s, dy = apy - aby * s, dz = apz - abz * s;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < best) best = d;
            }
            return best;
        }

        /// <summary>0..1 share of influence a faction holds AT a point vs its strongest rival there:
        /// 1 = uncontested, 0.5 = evenly contested, ->0 = besieged (a rival dominates the spot).</summary>
        public double Control(Vec3 pos, string factionTag)
        {
            double mine = Sample(pos, factionTag);
            string top; double topVal, secondVal;
            DominantAt(pos, out top, out topVal, out secondVal);
            double bestRival = (top == factionTag) ? secondVal : topVal;
            double denom = mine + bestRival;
            return denom <= 1e-9 ? 1.0 : mine / denom;
        }

        /// <summary>Every faction tag that owns at least one node other than <paramref name="factionTag"/>.
        /// The rival set <c>FactionBrain.DampedRivalPressure</c> loops over, one Pressure call each,
        /// so every contribution can be weighed by that rival's fleet power before the worst is
        /// taken. A pre-aggregated worst-rival figure cannot answer that, which is why this is
        /// exposed rather than kept private.</summary>
        public IEnumerable<string> RivalTags(string factionTag)
        {
            var rivals = new HashSet<string>(StringComparer.Ordinal);
            foreach (SpatialNode m in _graph.Nodes.Values)
                if (m.OwnerFactionTag != null && m.OwnerFactionTag != factionTag && m.OwnerFactionTag != NeutralFaction.Tag)
                    rivals.Add(m.OwnerFactionTag);
            return rivals;
        }

        /// <summary>0..1: how hard <paramref name="byFaction"/> presses on <paramref name="ofFaction"/>'s
        /// territory. Feeds Security/Rivalry (via <c>FactionBrain.DampedRivalPressure</c>) and relation
        /// drift (<c>Diplomacy.Drift</c>).
        ///
        /// <para>The rival's share of the total influence mass standing over this faction's own estate,
        /// normalised so that <see cref="FactionTuning.BorderPressureFullContestShare"/> reads 1.0.
        /// Two properties the drives depend on:</para>
        ///
        /// <para><b>Mass-weighted, so the estate is the denominator.</b> One contested node on the rim
        /// of a hundred-node holding is a hundredth of that holding, and reads as one. The value falls
        /// as the owner takes ground back and rises as it loses ground, so it is something a faction
        /// can act on rather than a latch.</para>
        ///
        /// <para><b>Directional.</b> <c>theirs/(mine+theirs)</c> asks "how much of my ground is under
        /// their influence", which keeps climbing as the owner is pushed out — right up to 1.0 for an
        /// estate that projects nothing of its own. It is deliberately not symmetric about parity:
        /// a node being lost is the loudest signal the field has, not the quietest.</para>
        ///
        /// <para>Uses the snapshot when available — O(owned_nodes) instead of
        /// O(owned_nodes × nodes × Math.Exp).</para></summary>
        public double Pressure(string ofFaction, string byFaction)
        {
            double theirs = 0, total = 0;
            foreach (SpatialNode n in _graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != ofFaction) continue;
                double mine = SampleAt(n.Id, ofFaction);
                double rival = SampleAt(n.Id, byFaction);
                theirs += rival;
                total  += mine + rival;
            }
            if (total <= 1e-9) return 0;
            return MathUtil.Clamp01(theirs / total / FullContestShare());
        }
    }
}
