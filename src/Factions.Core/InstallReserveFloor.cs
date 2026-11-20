using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The mirror image of <c>ProductionManager.ShipBuildFloor</c>: how much of an inventory key a
    /// still-unfunded capability install has a claim on, and at what rank. A ship build whose own
    /// <see cref="BuildQueueEntry.Priority"/> sits below that rank must leave this amount alone —
    /// otherwise raising a capability above the build queues competing for the same component only
    /// removes the build's floor without stopping the build from re-absorbing the component first.
    ///
    /// <para>Scoped to capabilities with a registered real-block install BOM
    /// (<see cref="FactionTuning.InstallBoms"/>), which is the only path that names specific
    /// component keys. A capability charged through the flat <see cref="ItemKind"/> costs claims
    /// "any key of this kind" and cannot be attributed to a key at all; there,
    /// <c>ShipBuildFloor</c>'s own rank check is the whole lever.</para>
    /// </summary>
    public static class InstallReserveFloor
    {
        /// <summary>One key's outstanding install claim: how much is still unreserved, and the
        /// highest rank among the requests asking for it.</summary>
        public struct Claim
        {
            public long Outstanding;
            public int  Rank;
        }

        /// <summary>Per-key outstanding install claims across <paramref name="ownedNodes"/>: the
        /// summed BOM target of every active BOM-backed request minus what is already sitting in
        /// <see cref="SpatialNode.ReservedInventory"/>. Empty when nothing is being installed, which
        /// is the overwhelmingly common case — callers can skip the whole check on an empty map.</summary>
        public static Dictionary<string, Claim> ByKey(FactionState state, IEnumerable<InvestmentRequest> requests,
            IEnumerable<SpatialNode> ownedNodes, FactionTuning tuning)
        {
            var claims = new Dictionary<string, Claim>(StringComparer.Ordinal);
            if (state == null || requests == null || tuning == null) return claims;

            foreach (InvestmentRequest req in requests)
            {
                Dictionary<string, long> bom;
                if (!tuning.TryGetInstallBom(req.Cap, out bom)) continue;
                int rank = state.InstallRankFor(req.Cap, tuning);
                foreach (KeyValuePair<string, long> kv in bom)
                {
                    if (kv.Value <= 0) continue;
                    Claim prior;
                    bool seen = claims.TryGetValue(kv.Key, out prior);
                    prior.Outstanding += kv.Value;
                    // Two capabilities can want the same component at different ranks; the strongest
                    // claim on the key is the one a build has to respect.
                    prior.Rank = seen ? Math.Max(prior.Rank, rank) : rank;
                    claims[kv.Key] = prior;
                }
            }
            if (claims.Count == 0) return claims;

            // Subtract what is already staged. Reserved stock is physically out of routable
            // Inventory, so a build could never have taken it anyway -- only the shortfall still
            // competes for the same routable pool a build absorbs from.
            if (ownedNodes != null)
            {
                foreach (SpatialNode node in ownedNodes)
                {
                    foreach (KeyValuePair<string, long> kv in node.ReservedInventory)
                    {
                        Claim claim;
                        if (kv.Value <= 0 || !claims.TryGetValue(kv.Key, out claim)) continue;
                        claim.Outstanding -= kv.Value;
                        claims[kv.Key] = claim;
                    }
                }
            }

            var empty = new List<string>();
            foreach (KeyValuePair<string, Claim> kv in claims)
                if (kv.Value.Outstanding <= 0) empty.Add(kv.Key);
            foreach (string key in empty) claims.Remove(key);
            return claims;
        }

        /// <summary>How much of <paramref name="invKey"/> a build ranked
        /// <paramref name="buildPriority"/> must leave for a capability install. Zero unless an
        /// install genuinely outranks the build — a tie goes to the build, so the default ranks
        /// (0 and 0) never withhold anything.
        ///
        /// <para>Deliberately applied in full at every node rather than split across them: a claim
        /// this function reports is network-wide, and over-protecting is the safe direction for a
        /// claim that is bounded by the install BOM and released as soon as the install funds.</para></summary>
        public static long For(Dictionary<string, Claim> claims, string invKey, int buildPriority)
        {
            Claim claim;
            if (claims == null || claims.Count == 0 || !claims.TryGetValue(invKey, out claim)) return 0;
            if (claim.Rank <= buildPriority) return 0;
            return claim.Outstanding > 0 ? claim.Outstanding : 0;
        }
    }
}
