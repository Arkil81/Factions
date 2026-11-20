using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One member of a player faction, as one NPC faction sees them: the SE reputation that
    /// faction holds toward this individual, and when the individual was last active.</summary>
    public struct MemberReputation
    {
        /// <summary>SE identity id. Carried for diagnosis only — the aggregate does not key on it.</summary>
        public long IdentityId;
        /// <summary>Normalised to -1..1 on the same scale as <see cref="FactionState.Relations"/>.
        /// The host owns the conversion because it owns the world's <see cref="ReputationScale"/>;
        /// Core must not carry a second copy of those marks (see that type's remarks).</summary>
        public double Standing;
        /// <summary>When this member was last seen. Drives the activity weight.</summary>
        public DateTime LastSeenUtc;
    }

    /// <summary>
    /// One NPC faction's aggregate opinion of a player faction, derived from the SE reputations it
    /// holds toward that faction's individual members.
    ///
    /// <para>This is the single, one-directional link between the two levels of the reputation model
    /// (docs/player-progression.md → Reputation and standing): the individual level lives in SE and
    /// the faction level lives in <see cref="FactionState.Relations"/>, and <b>nothing is copied</b>.
    /// The aggregate is <i>sensed</i> as a drift term in <see cref="Diplomacy.Drift"/>, the way border
    /// pressure is sensed off the influence field.</para>
    /// </summary>
    public static class MemberStandingAggregate
    {
        /// <summary>
        /// <c>α·mean + (1−α)·worst</c> over the roster, both terms weighted by how recently each
        /// member was active.
        ///
        /// <para><b>Weights are relative to the faction's most active member</b>, not absolute. That
        /// is what makes the three intended consequences hold at once:</para>
        /// <list type="bullet">
        /// <item>A faction repairs its standing by removing the offender — a departed member is off
        /// the roster outright, and one who merely stops showing up while their peers keep playing
        /// converges on the rest of the faction. Removing them still beats waiting them out.</item>
        /// <item>A solo Magnate is judged entirely on their own conduct — with one member the
        /// relative weight is exactly 1, so mean equals worst and α drops out. The Consumer→Magnate
        /// transition therefore produces no discontinuity, <b>at any level of activity</b>. Absolute
        /// weights would fail this: a lone inactive player's worst term would decay toward neutral
        /// while their mean stayed put, and α would silently start to matter.</item>
        /// <item>Inactivity holds rather than resets — a wholly dormant faction keeps being judged on
        /// what its members did, because relative weights cannot all decay to nothing. Falling back
        /// to neutral would be a second, accidental laundering path.</item>
        /// </list>
        ///
        /// <para>The one genuinely undefined case left is an <b>empty</b> roster, which returns false
        /// so the caller holds its last value rather than inventing a neutral one.</para>
        /// </summary>
        /// <param name="members">The player faction's roster as this NPC faction sees it.</param>
        /// <param name="nowUtc">Clock, passed in so the result is deterministic under test.</param>
        /// <param name="alpha">Share of the aggregate taken from the mean; the remainder comes from
        /// the worst member. 0 judges a faction purely by its worst conduct, 1 purely by its average.</param>
        /// <param name="activityHalfLifeDays">Days over which an absent member's contribution halves,
        /// measured against the roster's most recently active member. Non-positive disables the
        /// weighting entirely (every member counts the same).</param>
        /// <param name="aggregate">-1..1, on <see cref="FactionState.Relations"/>' scale.</param>
        /// <returns>False when there is nothing to compute from.</returns>
        public static bool TryCompute(IList<MemberReputation> members, DateTime nowUtc,
                                      double alpha, double activityHalfLifeDays, out double aggregate)
        {
            aggregate = 0.0;
            if (members == null || members.Count == 0) return false;

            // Pass 1: raw activity weight per member, and the roster's best.
            var weights = new double[members.Count];
            double maxWeight = 0.0;
            for (int i = 0; i < members.Count; i++)
            {
                weights[i] = ActivityWeight(members[i].LastSeenUtc, nowUtc, activityHalfLifeDays);
                if (weights[i] > maxWeight) maxWeight = weights[i];
            }

            // Every member's last-seen is so far past that its weight underflowed to zero. Relative
            // weighting has nothing to divide by, so fall back to counting everyone equally -- which
            // is what "inactivity holds rather than resets" means at the limit.
            if (maxWeight <= 0.0)
            {
                for (int i = 0; i < weights.Length; i++) weights[i] = 1.0;
                maxWeight = 1.0;
            }

            var relatives = new double[members.Count];
            double weightedSum = 0.0, weightTotal = 0.0;
            for (int i = 0; i < members.Count; i++)
            {
                relatives[i] = weights[i] / maxWeight;                 // (0..1], and exactly 1 for the most active
                weightedSum += relatives[i] * MathUtil.Clamp(members[i].Standing, -1.0, 1.0);
                weightTotal += relatives[i];
            }
            if (weightTotal <= 0.0) return false;
            double mean = weightedSum / weightTotal;

            // An absent member converges on the rest of the faction, NOT on neutral. Fading toward
            // neutral makes an idle member with GOOD standing the roster's minimum -- a faction of
            // two well-regarded members, one of them away, reads worse than the same two both
            // active, for conduct nobody engaged in. Converging on the mean instead means absence
            // stops distinguishing you from your faction, in either direction, which is what leaves
            // the worst term measuring conduct rather than attendance.
            double worst = double.MaxValue;
            for (int i = 0; i < members.Count; i++)
            {
                double standing = MathUtil.Clamp(members[i].Standing, -1.0, 1.0);
                double faded = mean + relatives[i] * (standing - mean);
                if (faded < worst) worst = faded;
            }

            double a = MathUtil.Clamp01(alpha);
            aggregate = MathUtil.Clamp(a * mean + (1.0 - a) * worst, -1.0, 1.0);
            return true;
        }

        /// <summary>Exponential recency weight: 1 at the moment of activity, halving every
        /// <paramref name="halfLifeDays"/>. A last-seen in the future (clock skew) reads as now.</summary>
        private static double ActivityWeight(DateTime lastSeenUtc, DateTime nowUtc, double halfLifeDays)
        {
            if (halfLifeDays <= 0.0) return 1.0;
            if (lastSeenUtc == default(DateTime)) return 0.0;
            double days = (nowUtc - lastSeenUtc).TotalDays;
            if (days <= 0.0) return 1.0;
            return Math.Pow(0.5, days / halfLifeDays);
        }
    }

    /// <summary>
    /// Supplies <see cref="Diplomacy.Drift"/> with the member-reputation aggregate one NPC faction
    /// holds toward a player faction. Implemented by the host, which is the only side that can read
    /// SE's own player↔faction reputation; Core sees a normalised number and never the engine.
    /// </summary>
    public interface IMemberStandingSource
    {
        /// <summary>The aggregate <paramref name="npcFactionTag"/> holds toward
        /// <paramref name="playerFactionTag"/>, -1..1.</summary>
        /// <returns>False when the target is not a registered player faction, when its roster is
        /// empty, or when no SE-backed source is wired — all of which mean "do not apply the term",
        /// never "the term is zero".</returns>
        bool TryGetAggregate(string npcFactionTag, string playerFactionTag, out double aggregate);
    }

    /// <summary>The offline stand-in: no player factions, so no term. Used by the Sim and by any test
    /// that has not wired a source, and what makes NPC-vs-NPC drift provably unchanged.</summary>
    public sealed class NullMemberStandingSource : IMemberStandingSource
    {
        public static readonly NullMemberStandingSource Instance = new NullMemberStandingSource();
        public bool TryGetAggregate(string npcFactionTag, string playerFactionTag, out double aggregate)
        {
            aggregate = 0.0;
            return false;
        }
    }
}
