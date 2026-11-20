using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// What a faction should keep armed to survive the next attack on it, read from its own fight
    /// ledger. The terror budget (<see cref="TerrorCampaigns.WantedDps"/>) sizes an antagonist's
    /// offence from the region it means to hit; this is its defensive counterpart, and every
    /// faction has one: the strongest attacker seen inside <see cref="FactionTuning.DefenceMemoryWindow"/>
    /// times <see cref="FactionTuning.DefenceBudgetRatio"/>. A faction nobody has attacked wants
    /// nothing, so a peaceful network still builds only its posture-derived hull count.
    ///
    /// <para>Derived from <see cref="FactionState.Fights"/> on every pass, never persisted: the
    /// window closes by itself as the ledger ages, and a stored copy would outlive the records it
    /// was computed from.</para>
    ///
    /// <para>Provenance: FAC-275 slice 3, generalising FAC-271's campaign budget to the defence
    /// side.</para>
    /// </summary>
    public static class DefenceBudget
    {
        /// <summary>Combat DPS <paramref name="s"/> should keep stocked against being attacked: the
        /// largest <see cref="FightRecord.AttackerDps"/> among the fights it DEFENDED inside the
        /// memory window, times the ratio, rounded up. 0 when it has defended nothing recently, or
        /// when either tuning key is at or below zero — each is a disable switch.</summary>
        /// <remarks>The attacker's <see cref="FightRecord.Kind"/> is not filtered: a Conquer fleet's
        /// guns have to be beaten by the same hull a Raid's do.</remarks>
        public static long WantedDps(FactionState s, FactionTuning t, DateTime nowUtc)
        {
            if (s == null || t == null) return 0;
            if (t.DefenceBudgetRatio <= 0 || t.DefenceMemoryWindow <= TimeSpan.Zero) return 0;

            long strongest = 0;
            List<FightRecord> defended = FightLedger.DefendedSince(s, nowUtc - t.DefenceMemoryWindow);
            for (int i = 0; i < defended.Count; i++)
                if (defended[i].AttackerDps > strongest) strongest = defended[i].AttackerDps;

            if (strongest <= 0) return 0;
            return (long)Math.Ceiling(strongest * t.DefenceBudgetRatio);
        }

        /// <summary>The same budget, per node: the strongest attacker seen <b>at one place</b> inside
        /// the window, times the ratio. <see cref="WantedDps"/> answers "how strong a hull should this
        /// faction own"; this answers "how strong a garrison should stand <i>here</i>", which is the
        /// question a defender arriving from three nodes away is too late to answer.
        ///
        /// <para><see cref="FightRecord.NodeId"/> is the MISSION target even when the duel happened at
        /// a mid-transit waypoint, so a node's history is the attacks aimed at it rather than the ones
        /// that happened to be resolved near it.</para>
        ///
        /// <para>0 for a node nobody has attacked inside the window, which is nearly every node — the
        /// ledger is the bound on the work, and a faction nobody has touched garrisons nothing.</para></summary>
        public static long WantedDpsAt(FactionState s, string nodeId, FactionTuning t, DateTime nowUtc)
        {
            if (s == null || t == null || string.IsNullOrEmpty(nodeId)) return 0;
            if (t.DefenceBudgetRatio <= 0 || t.DefenceMemoryWindow <= TimeSpan.Zero) return 0;

            long strongest = 0;
            List<FightRecord> defended = FightLedger.DefendedSince(s, nowUtc - t.DefenceMemoryWindow);
            for (int i = 0; i < defended.Count; i++)
                if (string.Equals(defended[i].NodeId, nodeId, StringComparison.Ordinal)
                    && defended[i].AttackerDps > strongest) strongest = defended[i].AttackerDps;

            if (strongest <= 0) return 0;
            return (long)Math.Ceiling(strongest * t.DefenceBudgetRatio);
        }

        /// <summary>Every node this faction has been attacked at inside the window, most-recently-hit
        /// first. The candidate set for a per-node garrison, bounded by
        /// <see cref="FactionTuning.FightHistoryCap"/> rather than by the size of the estate.</summary>
        public static List<string> AttackedNodesSince(FactionState s, DateTime sinceUtc)
        {
            var result = new List<string>();
            if (s == null) return result;

            // Ordered by each node's OWN most recent attack rather than by ledger position. The two
            // agree while records are appended as fights happen, and a caller that reads position as
            // time gets the oldest flashpoints first the moment they do not.
            var latest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            List<FightRecord> defended = FightLedger.DefendedSince(s, sinceUtc);
            for (int i = 0; i < defended.Count; i++)
            {
                string id = defended[i].NodeId;
                if (string.IsNullOrEmpty(id)) continue;
                DateTime seen;
                if (!latest.TryGetValue(id, out seen)) { latest[id] = defended[i].Utc; result.Add(id); }
                else if (defended[i].Utc > seen) latest[id] = defended[i].Utc;
            }
            result.Sort((a, b) => latest[b].CompareTo(latest[a]));
            return result;
        }
    }
}
