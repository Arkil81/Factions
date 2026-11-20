using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The founded-station claim the host holds against vanilla's station cell generator, kept as
    /// station id → the node that claimed it, plus when each id last left the claim.
    ///
    /// <para><b>Why the node is remembered.</b> The host's claim is a bare id set, rebuilt from the
    /// graph every resync. When an id falls out of it, the graph has already moved on, so the only
    /// record of which node it belonged to — and why it stopped qualifying — is the one taken at the
    /// moment of the diff. The vanilla generator does nothing with a claimed station; with an
    /// unclaimed one whose grid is not standing it spawns its own, saved in the sector file. Telling
    /// which unclaimed case a vanilla spawn was (never claimed, or dropped seconds earlier) is what
    /// this exists for.</para>
    ///
    /// <para>A station qualifies when its node carries a <see cref="SpatialNode.StableStationId"/>
    /// and an <see cref="SpatialNode.OwnerFactionTag"/> — the same rule the host's claim has always
    /// used.</para>
    /// </summary>
    public sealed class StationClaimLedger
    {
        private struct Departure
        {
            public string NodeId;
            public DateTime AtUtc;
        }

        private struct Quiet
        {
            public DateTime NextUtc;
            public int Suppressed;
        }

        /// <summary>How long one station's line of one kind stays quiet after it is written. A
        /// station whose claim flaps every resync writes a DROPPED/ADDED pair per window, not per
        /// resync; the next line carries how many it held back.</summary>
        public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);

        /// <summary>Above this many ids leaving (or joining) in one resync, one summary line is
        /// written instead of one per id: that is a whole-graph event, and its size is the
        /// finding.</summary>
        public const int BulkThreshold = 20;

        private Dictionary<long, string> _nodeById = new Dictionary<long, string>();
        private Dictionary<long, string> _scratch = new Dictionary<long, string>();
        private readonly Dictionary<long, Departure> _departed = new Dictionary<long, Departure>();
        private readonly Dictionary<long, Quiet> _quietDropped = new Dictionary<long, Quiet>();
        private readonly Dictionary<long, Quiet> _quietAdded = new Dictionary<long, Quiet>();
        private readonly Dictionary<long, Quiet> _quietSpawn = new Dictionary<long, Quiet>();
        private readonly Dictionary<long, Quiet> _quietBlock = new Dictionary<long, Quiet>();
        private readonly List<string> _pending = new List<string>();
        private readonly Dictionary<string, int> _reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Claimed station ids.</summary>
        public ICollection<long> Ids { get { return _nodeById.Keys; } }

        public int Count { get { return _nodeById.Count; } }

        public bool Contains(long stationId) { return _nodeById.ContainsKey(stationId); }

        /// <summary>Whether a station belongs to Factions rather than to vanilla's cell generator: its
        /// id is in <paramref name="claimedIds"/>, or its SE faction is in
        /// <paramref name="registryFactionIds"/>. The faction half covers the claim's own lag — a
        /// station founded since the last resync, a node dropped for one resync mid-handover — which
        /// the id half alone leaves open to vanilla.</summary>
        public static bool Covers(long stationId, long factionId,
                                  HashSet<long> claimedIds, HashSet<long> registryFactionIds)
        {
            return claimedIds.Contains(stationId) || registryFactionIds.Contains(factionId);
        }

        /// <summary>Forgets everything, including departures. A new session's claim owes nothing to
        /// the last one's.</summary>
        public void Reset()
        {
            _nodeById.Clear();
            _departed.Clear();
            _quietDropped.Clear();
            _quietAdded.Clear();
            _quietSpawn.Clear();
            _quietBlock.Clear();
        }

        /// <summary>Re-derives the claim from <paramref name="graph"/>. With
        /// <paramref name="lines"/> non-null, appends one <c>STATION_CLAIM_DROPPED</c> per id that
        /// left and one <c>STATION_CLAIM_ADDED</c> per id that joined, subject to
        /// <see cref="RepeatWindow"/> and <see cref="BulkThreshold"/>; null is the seeding pass,
        /// which reports nothing. Returns true when the set of ids changed.</summary>
        public bool Rebuild(SpatialGraph graph, DateTime nowUtc, List<string> lines)
        {
            if (graph == null) return false;

            _scratch.Clear();
            foreach (KeyValuePair<string, SpatialNode> kv in graph.Nodes)
            {
                SpatialNode node = kv.Value;
                if (node.StableStationId == 0 || node.OwnerFactionTag == null) continue;
                if (!_scratch.ContainsKey(node.StableStationId)) _scratch[node.StableStationId] = node.Id;
            }

            bool changed = _scratch.Count != _nodeById.Count;

            _pending.Clear();
            _reasonCounts.Clear();
            int dropped = 0;
            foreach (KeyValuePair<long, string> kv in _nodeById)
            {
                if (_scratch.ContainsKey(kv.Key)) continue;
                changed = true;
                dropped++;
                _departed[kv.Key] = new Departure { NodeId = kv.Value, AtUtc = nowUtc };
                if (lines == null) continue;
                string reason = WhyNotClaimed(graph, kv.Value);
                string kind = reason.StartsWith("restationed", StringComparison.Ordinal) ? "restationed" : reason;
                int n; _reasonCounts.TryGetValue(kind, out n); _reasonCounts[kind] = n + 1;
                int held;
                if (Admit(_quietDropped, kv.Key, nowUtc, out held))
                    _pending.Add("STATION_CLAIM_DROPPED station=" + kv.Key + " node=" + kv.Value
                                 + " reason=" + reason + (held > 0 ? " repeats=" + held : ""));
            }
            if (lines != null)
            {
                if (dropped > BulkThreshold)
                    lines.Add("STATION_CLAIM_DROPPED_BULK count=" + dropped + " reasons=" + Reasons());
                else
                    lines.AddRange(_pending);
            }

            _pending.Clear();
            int added = 0;
            foreach (KeyValuePair<long, string> kv in _scratch)
            {
                if (_nodeById.ContainsKey(kv.Key)) continue;
                changed = true;
                added++;
                Departure d;
                bool returning = _departed.TryGetValue(kv.Key, out d);
                if (returning) _departed.Remove(kv.Key);
                int held;
                if (lines != null && Admit(_quietAdded, kv.Key, nowUtc, out held))
                    _pending.Add("STATION_CLAIM_ADDED station=" + kv.Key + " node=" + kv.Value
                                 + (returning ? " after=" + Seconds(nowUtc - d.AtUtc) + "s" : "")
                                 + (held > 0 ? " repeats=" + held : ""));
            }
            if (lines != null)
            {
                if (added > BulkThreshold) lines.Add("STATION_CLAIM_ADDED_BULK count=" + added);
                else lines.AddRange(_pending);
            }
            _pending.Clear();

            Dictionary<long, string> swap = _nodeById;
            _nodeById = _scratch;
            _scratch = swap;
            _scratch.Clear();
            return changed;
        }

        /// <summary>The claim's account of a station vanilla is about to spawn: <c>claim=never</c>,
        /// <c>claim=dropped node=… ago=…s</c>, or <c>claim=held node=…</c> when the ledger still
        /// holds an id the host's set no longer does (a removal between resyncs).</summary>
        public string DescribeUnclaimed(long stationId, DateTime nowUtc)
        {
            string nodeId;
            if (_nodeById.TryGetValue(stationId, out nodeId)) return "claim=held node=" + nodeId;
            Departure d;
            if (_departed.TryGetValue(stationId, out d))
                return "claim=dropped node=" + d.NodeId + " ago=" + Seconds(nowUtc - d.AtUtc) + "s";
            return "claim=never";
        }

        /// <summary>Whether a vanilla spawn of <paramref name="stationId"/> should be written now,
        /// under the same <see cref="RepeatWindow"/>; <paramref name="suppressed"/> is how many were
        /// held back since the last one written.</summary>
        public bool AdmitVanillaSpawn(long stationId, DateTime nowUtc, out int suppressed)
        {
            return Admit(_quietSpawn, stationId, nowUtc, out suppressed);
        }

        /// <summary>Whether a vanilla spawn the host blocked for <paramref name="stationId"/> should be
        /// written now — a station of a registry faction the claim did not cover — under the same
        /// <see cref="RepeatWindow"/>, kept apart from <see cref="AdmitVanillaSpawn"/> so one kind
        /// never quiets the other.</summary>
        public bool AdmitVanillaBlock(long stationId, DateTime nowUtc, out int suppressed)
        {
            return Admit(_quietBlock, stationId, nowUtc, out suppressed);
        }

        private static bool Admit(Dictionary<long, Quiet> quiet, long stationId, DateTime nowUtc, out int suppressed)
        {
            Quiet q;
            if (quiet.TryGetValue(stationId, out q) && nowUtc < q.NextUtc)
            {
                q.Suppressed++;
                quiet[stationId] = q;
                suppressed = 0;
                return false;
            }
            suppressed = q.Suppressed;
            quiet[stationId] = new Quiet { NextUtc = nowUtc + RepeatWindow, Suppressed = 0 };
            return true;
        }

        private string Reasons()
        {
            var parts = new List<string>(_reasonCounts.Count);
            foreach (KeyValuePair<string, int> kv in _reasonCounts) parts.Add(kv.Key + ":" + kv.Value);
            parts.Sort(StringComparer.Ordinal);
            return string.Join(",", parts.ToArray());
        }

        private static string WhyNotClaimed(SpatialGraph graph, string nodeId)
        {
            SpatialNode node;
            if (nodeId == null || !graph.Nodes.TryGetValue(nodeId, out node)) return "node_gone";
            if (node.OwnerFactionTag == null) return "unowned";
            if (node.StableStationId == 0) return "unfounded";
            return "restationed now=" + node.StableStationId;
        }

        private static long Seconds(TimeSpan span)
        {
            return (long)Math.Max(0.0, span.TotalSeconds);
        }
    }
}
