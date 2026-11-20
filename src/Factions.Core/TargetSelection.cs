using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Which hostile grids a fighting squadron may shoot.</summary>
    public enum TargetScope
    {
        /// <summary>Any hostile grid, nearest first. A bench fleet staged with <c>hostile=any</c>.</summary>
        Any = 0,

        /// <summary>Any hostile grid, but a grid of the fleet's opponent
        /// (<see cref="TargetSelection.CollectOpponents"/>) before any other, whatever the distance.
        /// Every fleet the simulation dispatched.</summary>
        PreferOpponents = 1,

        /// <summary>Only grids of the fleet's opponent. A bench fleet: it fights its scenario and
        /// never a player or an unrelated grid. With no opponent in reach it has no target, and keeps
        /// flying the manoeuvre it holds under.</summary>
        OpponentsOnly = 2,
    }

    /// <summary>Who has been shooting a hull or its squadron, from <see cref="IncomingFire"/>.</summary>
    public struct FireRead
    {
        /// <summary>The grid with the most decayed damage inside the window, or 0 for none.</summary>
        public long AttackerId;
        public double AttackerDamage;

        /// <summary>The decayed damage the held target has dealt inside the window; 0 when it has not
        /// hit, or there is no held target.</summary>
        public double CurrentDamage;
    }

    /// <summary>One hostile grid in reach of a fighting hull, as the host read it.</summary>
    public struct TargetCandidate
    {
        public long EntityId;

        /// <summary>From the hull choosing, metres.</summary>
        public double DistanceM;

        /// <summary>The grid belongs to the fleet's opponent.</summary>
        public bool Opponent;

        /// <summary>From the choosing hull's anchor (<see cref="CombatLeash"/>), metres. Read only when
        /// <see cref="TargetSelection.Pick"/> is given a leash.</summary>
        public double AnchorDistanceM;
    }

    /// <summary>
    /// Whom a materialised fighting hull shoots, re-decided on every tactical read (about 1 Hz). The
    /// rule, for a lead and for each escort choosing from its lead's candidates:
    /// <list type="number">
    ///   <item>Only candidates its <see cref="TargetScope"/> admits are eligible.</item>
    ///   <item>Fired on (<see cref="FireRead"/>, from <see cref="IncomingFire"/>): a held target that
    ///   is itself shooting the hull or its squadron is kept, unless another eligible attacker has
    ///   dealt more than <see cref="SwitchDamageFactor"/> times its damage. A held target that is not
    ///   shooting gives way to the eligible attacker with the most damage — whatever the distance and
    ///   whether or not it is the opponent's.</item>
    ///   <item>The best eligible candidate is an opponent's grid before any other under
    ///   <see cref="TargetScope.PreferOpponents"/>, then the nearest — where, given the hull's leash,
    ///   every metre a candidate lies past the leash's inner edge from the hull's anchor counts as
    ///   <see cref="CombatLeash.TargetAnchorWeight"/> metres more (<see cref="CombatLeash.TargetPenaltyM"/>),
    ///   so a hull prefers hostiles nearer what it fights for.</item>
    ///   <item>A current target that still qualifies (live, hostile, inside the release radius and
    ///   the node leash, which the host checks) and is still eligible is kept, unless the best is an
    ///   opponent's grid where the current one is not, or is nearer than
    ///   <see cref="SwitchNearerFactor"/> of the current target's distance, both charged the anchor
    ///   penalty.</item>
    /// </list>
    /// The margins keep two hostiles at similar range, or two attackers trading hits, from being
    /// swapped back and forth every read: a hull that changes target every second never settles its aim.
    /// A commander's order (<see cref="WingOrder"/>) names its own target and bypasses this.
    /// </summary>
    public static class TargetSelection
    {
        /// <summary>A kept target gives way to a candidate nearer than this fraction of its distance.</summary>
        public const double SwitchNearerFactor = 0.7;

        /// <summary>A held target that is shooting back gives way only to an attacker that has dealt
        /// more than this multiple of its damage.</summary>
        public const double SwitchDamageFactor = 2.0;

        /// <summary>A bench fleet fights only its scenario unless it was staged with
        /// <see cref="BenchFlags.HostileAny"/>; every other fleet prefers its opponent.</summary>
        public static TargetScope ScopeFor(PendingFleetAction fleet)
        {
            if (!fleet.IsBench) return TargetScope.PreferOpponents;
            return (fleet.BenchFlags & BenchFlags.HostileAny) != 0 ? TargetScope.Any : TargetScope.OpponentsOnly;
        }

        public static bool Eligible(TargetScope scope, bool opponent)
        {
            return scope != TargetScope.OpponentsOnly || opponent;
        }

        /// <summary>The entity id to fight, or 0 for none. <paramref name="currentId"/> is the target
        /// held from the last read, or 0 when there is none or it stopped qualifying;
        /// <paramref name="currentDistanceM"/> and <paramref name="currentOpponent"/> describe it.
        /// <paramref name="fire"/> is who has been shooting the hull or its squadron. Reads the first
        /// <paramref name="count"/> entries of <paramref name="candidates"/>, which may or may not
        /// include the current target. <paramref name="leashM"/> is the hull's leash, zero for none,
        /// and <paramref name="currentAnchorM"/> the current target's distance from the hull's anchor.</summary>
        public static long Pick(TargetScope scope, long currentId, double currentDistanceM, bool currentOpponent,
                                FireRead fire, TargetCandidate[] candidates, int count,
                                double leashM = 0, double currentAnchorM = 0)
        {
            bool keep = currentId != 0 && Eligible(scope, currentOpponent);

            int best = -1, attacker = -1;
            for (int i = 0; i < count; i++)
            {
                if (candidates[i].EntityId == 0 || !Eligible(scope, candidates[i].Opponent)) continue;
                if (fire.AttackerId != 0 && candidates[i].EntityId == fire.AttackerId) attacker = i;
                if (best < 0 || Better(scope, candidates[i], candidates[best], leashM)) best = i;
            }

            if (keep && fire.CurrentDamage > 0)
            {
                bool outshot = attacker >= 0 && fire.AttackerId != currentId
                               && fire.AttackerDamage > fire.CurrentDamage * SwitchDamageFactor;
                return outshot ? fire.AttackerId : currentId;
            }
            if (attacker >= 0) return fire.AttackerId;

            if (!keep) return best >= 0 ? candidates[best].EntityId : 0;
            if (best < 0) return currentId;

            TargetCandidate b = candidates[best];
            if (b.EntityId == currentId) return currentId;
            if (scope == TargetScope.PreferOpponents && b.Opponent != currentOpponent)
                return b.Opponent ? b.EntityId : currentId;
            double currentCost = currentDistanceM + CombatLeash.TargetPenaltyM(currentAnchorM, leashM);
            return Cost(b, leashM) < currentCost * SwitchNearerFactor ? b.EntityId : currentId;
        }

        private static bool Better(TargetScope scope, TargetCandidate a, TargetCandidate b, double leashM)
        {
            if (scope == TargetScope.PreferOpponents && a.Opponent != b.Opponent) return a.Opponent;
            return Cost(a, leashM) < Cost(b, leashM);
        }

        /// <summary>A candidate's distance with the anchor penalty added.</summary>
        private static double Cost(TargetCandidate c, double leashM)
        {
            return c.DistanceM + CombatLeash.TargetPenaltyM(c.AnchorDistanceM, leashM);
        }

        /// <summary>
        /// The opponent of fleet <paramref name="fi"/> of faction <paramref name="si"/>, as the meeting
        /// at its node has it. Adds to <paramref name="grids"/>
        /// the materialised lead and escort ids of every fleet on the other side at the same
        /// <see cref="PendingFleetAction.TargetNodeId"/> — the defenders standing there
        /// (<see cref="PendingFleetAction.IsDefenceKind"/>) for an attacker, the attackers there for
        /// a defender. Returns the faction whose grids are the opponent too: an attacker's
        /// <see cref="PendingFleetAction.TargetTag"/>, the node's owner, so its station counts; null
        /// for a defender and for a bench fight, whose node is not a real one.
        /// </summary>
        public static string CollectOpponents(IList<FactionState> states, int si, int fi, ICollection<long> grids)
        {
            if (states == null || si < 0 || si >= states.Count || states[si] == null
                || fi < 0 || fi >= states[si].PendingFleets.Count) return null;
            PendingFleetAction fleet = states[si].PendingFleets[fi];
            if (string.IsNullOrEmpty(fleet.TargetNodeId)) return null;
            bool attacker = PendingFleetResolver.EngagesOnArrival(fleet.Kind);
            bool defender = PendingFleetAction.IsDefenceKind(fleet.Kind);
            if (!attacker && !defender) return null;

            for (int s = 0; s < states.Count; s++)
            {
                FactionState st = states[s];
                if (st == null) continue;
                List<PendingFleetAction> fleets = st.PendingFleets;
                for (int f2 = 0; f2 < fleets.Count; f2++)
                {
                    if (s == si && f2 == fi) continue;
                    PendingFleetAction f = fleets[f2];
                    if (!string.Equals(f.TargetNodeId, fleet.TargetNodeId, StringComparison.Ordinal)) continue;
                    if (string.Equals(f.ActorTag, fleet.ActorTag, StringComparison.OrdinalIgnoreCase)) continue;
                    bool otherSide = attacker ? PendingFleetAction.IsDefenceKind(f.Kind) : PendingFleetResolver.EngagesOnArrival(f.Kind);
                    if (!otherSide) continue;
                    if (f.MaterialisedEntityId > 0) grids.Add(f.MaterialisedEntityId);
                    if (f.EscortEntityIds != null)
                        for (int e = 0; e < f.EscortEntityIds.Count; e++)
                            if (f.EscortEntityIds[e] > 0) grids.Add(f.EscortEntityIds[e]);
                }
            }

            if (!attacker || fleet.TargetNodeId.StartsWith(BenchScenario.NodePrefix, StringComparison.Ordinal)) return null;
            return string.IsNullOrEmpty(fleet.TargetTag) ? null : fleet.TargetTag;
        }
    }
}
