using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>How a role's faction-specific prefab pool and the universal <c>"All"</c> pool are
    /// combined into the candidate list a build ranks.
    ///
    /// <para>This rule lives in Core rather than in the SE catalogue that owns the two lists, so it
    /// is testable without a host. The catalogue's job is to say which prefabs carry the role and
    /// which hint each one declares; deciding what a faction may therefore choose from is a
    /// simulation rule.</para>
    ///
    /// <para><b>FAC-237.</b> The rule used to be an override — <c>if (specific.Count > 0) return
    /// specific;</c> — so a faction with any hull of its own for a role never saw the universal pool
    /// at all. Measured live 2026-09-03: six of seven faction-specific `Trade` pools contained no
    /// plain hull, and the 75 plain hulls in the universal pool were unreachable to them. IMPR
    /// (Miner hint, 5 hulls, 0 plain) and SPRT (Pirate, 15 hulls, 0 plain) built 0 plain haulers
    /// between them across a day while IGSL (Trader, 3 plain of 4) built 44. A cargo build for those
    /// factions took a drill or a gun every time, which is FAC-235's specialist-consumption
    /// problem created at build time.</para>
    ///
    /// <para><b>Order is the preference, and it is load-bearing.</b> Specific hulls come first, and
    /// the ranker breaks cost ties by pool position — so a faction still flies its own hulls
    /// wherever they are competitive, and reaches a generic one when it is genuinely better for the
    /// job. That is why <see cref="Combine"/> must not sort: whatever order the catalogue hands over
    /// within each group is preserved, and the group boundary carries the meaning.</para></summary>
    public static class PrefabRolePool
    {
        /// <summary>Specific first, then universal, de-duplicated, order preserved within each
        /// group. Either side may be null or empty; the result is never null.</summary>
        public static IReadOnlyList<string> Combine(
            IReadOnlyList<string> specific, IReadOnlyList<string> universal)
        {
            int total = (specific != null ? specific.Count : 0) + (universal != null ? universal.Count : 0);
            if (total == 0) return EmptyIds;

            var combined = new List<string>(total);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Append(combined, seen, specific);
            Append(combined, seen, universal);
            return combined;
        }

        /// <summary>How a build ranks two candidates from a <see cref="Combine"/>d pool: cheapest
        /// first, and at equal cost the one earlier in the pool — which, because the pool is
        /// faction-specific-first, means the faction's own hull.
        ///
        /// <para><b>This is a named rule rather than an inline lambda because it is the only place
        /// the preference is observable.</b> It cannot be tested through the sort: mutation-checked
        /// 2026-09-03, deleting the position term left the suite green across three runs at both 2
        /// and 20 tied candidates, because <c>List&lt;T&gt;.Sort</c>'s introsort happens to leave
        /// element 0 in place either way. A test that drives the sort therefore cannot fail when
        /// the rule is removed. This signature can.</para>
        ///
        /// <para>It also makes an equal-cost field resolve the same way every time. The previous
        /// cost-only comparison left the winner among tied candidates to an unstable sort — stable
        /// in practice, guaranteed by nothing.</para></summary>
        public static int CompareByCostThenPoolPosition(
            long costA, int positionA, long costB, int positionB)
        {
            int byCost = costA.CompareTo(costB);
            return byCost != 0 ? byCost : positionA.CompareTo(positionB);
        }

        /// <summary>Whether <paramref name="prefabId"/> carries a faction's livery rather than the
        /// universal <see cref="PrefabFactionHint.Universal"/> one. An unknown or empty id does not.</summary>
        public static bool IsOwnLivery(IPrefabCatalogue catalogue, string prefabId)
        {
            if (catalogue == null || string.IsNullOrEmpty(prefabId)) return false;
            string hint = catalogue.FactionHint(prefabId);
            return !string.IsNullOrEmpty(hint)
                && !string.Equals(hint, PrefabFactionHint.Universal, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The members of a <see cref="Combine"/>d pool that carry a livery, in pool order.
        /// The pool holds only the faction's own hint and the universal one, so these are the
        /// faction's own hulls. Empty when the role has none: the case where a
        /// <see cref="PrefabFactionHint.LiveryIsMandatory"/> faction keeps the whole pool.</summary>
        public static List<string> OwnLivery(IEnumerable<string> pool, IPrefabCatalogue catalogue)
        {
            var own = new List<string>();
            if (pool == null || catalogue == null) return own;
            foreach (string id in pool)
                if (IsOwnLivery(catalogue, id)) own.Add(id);
            return own;
        }

        private static void Append(List<string> into, HashSet<string> seen, IReadOnlyList<string> from)
        {
            if (from == null) return;
            for (int i = 0; i < from.Count; i++)
            {
                string id = from[i];
                if (string.IsNullOrEmpty(id)) continue;
                if (seen.Add(id)) into.Add(id);
            }
        }

        private static readonly IReadOnlyList<string> EmptyIds = new string[0];
    }
}
