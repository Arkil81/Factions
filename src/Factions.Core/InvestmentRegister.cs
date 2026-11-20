using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>A capability investment request posted by a manager when it detects a structural
    /// gap it cannot self-resolve — e.g. ShipyardManager stalled on a component no installed
    /// capability can produce. Read by the brain during Think to generate high-priority Develop
    /// candidates. Parallel to <see cref="DemandRegister"/> but consumed by the brain, not managers.</summary>
    public struct InvestmentRequest
    {
        public string FactionTag;
        public NodeCapabilityKind Cap;
        /// <summary>0..1 urgency. Scales Develop candidate quality above the exploratory floor.
        /// Computed by the posting manager from stall duration or shortfall magnitude.</summary>
        public double Urgency;
        public DateTime ExpiresUtc;
        /// <summary>Null (default) for every capability request that isn't planet-scoped — preserves
        /// today's behavior exactly. Set to a <see cref="SpatialNode.PlanetId"/> when the request is
        /// for local, same-planet capacity (e.g. a Shipyard needed because a same-planet Haul leg has
        /// no route-feasible ship) — see <see cref="OperationsManager"/>'s local-Shipyard trigger.
        /// Part of this register's dedupe key so a Terra-scoped and a Mars-scoped request for the same
        /// (FactionTag, Cap) never collide.</summary>
        public string TargetPlanetId;
        /// <summary>Which of the target body's two regions the capability is wanted in — its terrain
        /// (<see cref="StationSite.Surface"/>) or its well (<see cref="StationSite.Orbital"/>). Null
        /// (default) whenever <see cref="TargetPlanetId"/> is, and meaningless without it.
        /// <para>A body that forbids surface stations has only a well, and a faction whose presence at
        /// a landable body is entirely off the ground has only its well too — so a request that can
        /// name only the terrain is one no node can ever satisfy. Also part of the dedupe key: a leg
        /// on a body's ground and a leg in its well are different needs.</para></summary>
        public StationSite? TargetSite;
        /// <summary>The authored feature the capability is wanted in — a <see
        /// cref="SpatialNode.FeatureKey"/> — for a request scoped to a region no body claims. Null
        /// whenever <see cref="TargetPlanetId"/> is set: a body claims a node before a feature does,
        /// so a request names one or the other, never both (<see cref="NodeRegion"/>). Part of
        /// the dedupe key: two nests are two needs.</summary>
        public string TargetFeature;
        /// <summary>For a Farm, the crop it is wanted for — a seed subtype such as <c>Grain</c> — or
        /// null when the request came from a meal and any crop will do. Siting prefers a node where
        /// that crop grows with no seed fetched (<see cref="FarmCrops.SitingPreference"/>). Not part of
        /// the dedupe key: install parts are claimed per request, and one Farm answers every crop.</summary>
        public string Crop;
    }

    /// <summary>Strategic signal board: managers post <see cref="InvestmentRequest"/> entries here
    /// when they detect capability gaps. The brain reads a snapshot each Think cycle to generate
    /// high-priority Develop candidates that outbid the exploratory gap×fit path.
    /// TTL-based: managers re-post each tick while still blocked; entries expire automatically when
    /// the need is gone without requiring an explicit remove.</summary>
    public sealed class InvestmentRegister
    {
        private struct InvestmentKey : IEquatable<InvestmentKey>
        {
            public readonly string FactionTag;
            public readonly NodeCapabilityKind Cap;
            public readonly string TargetPlanetId;
            public readonly StationSite? TargetSite;
            public readonly string TargetFeature;

            public InvestmentKey(string factionTag, NodeCapabilityKind cap, string targetPlanetId,
                StationSite? targetSite, string targetFeature)
            {
                FactionTag = factionTag;
                Cap = cap;
                TargetPlanetId = targetPlanetId;
                TargetSite = targetSite;
                TargetFeature = targetFeature;
            }

            public bool Equals(InvestmentKey other)
            {
                return string.Equals(FactionTag, other.FactionTag, StringComparison.Ordinal)
                    && Cap == other.Cap
                    && string.Equals(TargetPlanetId, other.TargetPlanetId, StringComparison.Ordinal)
                    && TargetSite == other.TargetSite
                    && string.Equals(TargetFeature, other.TargetFeature, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is InvestmentKey && Equals((InvestmentKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = FactionTag == null ? 0 : StringComparer.Ordinal.GetHashCode(FactionTag);
                    hash = (hash * 397) ^ (int)Cap;
                    hash = (hash * 397) ^ (TargetPlanetId == null ? 0 : StringComparer.Ordinal.GetHashCode(TargetPlanetId));
                    hash = (hash * 397) ^ (TargetSite == null ? 0 : (int)TargetSite.Value + 1);
                    hash = (hash * 397) ^ (TargetFeature == null ? 0 : StringComparer.Ordinal.GetHashCode(TargetFeature));
                    return hash;
                }
            }
        }

        // Ordered list remains canonical for deterministic snapshots; the index makes repeated
        // blocked-manager posts O(1) rather than scanning every active request for each post.
        private readonly List<InvestmentRequest> _requests = new List<InvestmentRequest>();
        private readonly Dictionary<InvestmentKey, int> _indexes = new Dictionary<InvestmentKey, int>();

        /// <summary>Post or refresh a request. Keyed by (factionTag, cap, targetPlanetId, targetSite, targetFeature) — last post
        /// wins on refresh so urgency stays current. Returns true if newly added, false if refreshed.</summary>
        public bool Post(InvestmentRequest req)
        {
            InvestmentKey key = new InvestmentKey(req.FactionTag, req.Cap, req.TargetPlanetId, req.TargetSite, req.TargetFeature);
            int index;
            if (_indexes.TryGetValue(key, out index))
            {
                InvestmentRequest existing = _requests[index];
                // Within one tick several passes can claim the same capability for different reasons
                // -- a structural "nothing can produce this" gap and a measured throughput imbalance
                // both name RefineryII. The STRONGEST claim survives, not whichever pass happened to
                // run last: throughput urgency is a ratio now, so a 0.03 post must not erase a 0.90
                // capability-gap post that ran before it.
                //
                // Same tick is identified by an identical ExpiresUtc: every poster stamps
                // ctx.UtcNow + the same TTL, and UtcNow is frozen for the cycle. A LATER tick
                // replaces outright, which is what keeps a decayed urgency from being pinned up by a
                // stale higher one -- max-keeping across ticks would hold the first 1.0 forever,
                // since every re-post also refreshes the expiry.
                if (existing.ExpiresUtc == req.ExpiresUtc && existing.Urgency > req.Urgency)
                    req.Urgency = existing.Urgency;
                _requests[index] = req;
                return false;
            }
            _indexes[key] = _requests.Count;
            _requests.Add(req);
            return true;
        }

        /// <summary>Snapshot all active requests for a faction. Snapshot so the caller can
        /// iterate safely regardless of concurrent Post/Prune calls on the same tick.</summary>
        public List<InvestmentRequest> SnapshotFor(string factionTag)
        {
            var result = new List<InvestmentRequest>();
            foreach (InvestmentRequest r in _requests)
                if (r.FactionTag == factionTag)
                    result.Add(r);
            return result;
        }

        /// <summary>Remove all expired entries. Call once per manager tick.</summary>
        public void Prune(DateTime utcNow)
        {
            bool changed = false;
            for (int i = _requests.Count - 1; i >= 0; i--)
            {
                if (_requests[i].ExpiresUtc > utcNow) continue;
                _requests.RemoveAt(i);
                changed = true;
            }
            if (changed) RebuildIndex();
        }

        public int Count { get { return _requests.Count; } }

        private void RebuildIndex()
        {
            _indexes.Clear();
            for (int i = 0; i < _requests.Count; i++)
            {
                InvestmentRequest r = _requests[i];
                _indexes[new InvestmentKey(r.FactionTag, r.Cap, r.TargetPlanetId, r.TargetSite, r.TargetFeature)] = i;
            }
        }
    }
}
