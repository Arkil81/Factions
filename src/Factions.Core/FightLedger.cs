using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>How a contested duel was fought: both sides' arms and break-offs and the bracket of
    /// every turn actually fought, in order (a duel that ended in a break-off is shorter than the
    /// schedule). Immutable once built; shared between the fleet that fought and its records.</summary>
    public sealed class DuelTactic
    {
        public TacticArm AttackerArm;
        public BreakOffLevel AttackerBreakOff;
        public TacticArm DefenderArm;
        public BreakOffLevel DefenderBreakOff;
        /// <summary>Metres, one per turn fought.</summary>
        public int[] BracketsM;
    }

    /// <summary>
    /// One fight, as the record a faction can learn from. Filed to both sides at the terminal of
    /// every Raid/Retaliate/Conquer — the intercept that destroys the attacker, or the resolution
    /// that lands it — with what each side brought and what came of it. The decision log states
    /// what happened; this is the state the next decision reads (docs/logging-conventions.md).
    /// </summary>
    public struct FightRecord
    {
        public DateTime Utc;
        /// <summary>The attacking mission: Raid, Retaliate or Conquer.</summary>
        public ActionKind Kind;
        /// <summary>True on the attacker's copy, false on the defender's.</summary>
        public bool Attacking;
        /// <summary>The other side. Null when a node with no owner was raided.</summary>
        public string OpponentTag;
        public string NodeId;
        /// <summary>The target node's <see cref="NodeRegion"/> key, null when it stands in none —
        /// what lets a campaign against a region find its own fights.</summary>
        public string RegionKey;
        /// <summary>Summed weapon DPS the attacker brought.</summary>
        public long AttackerDps;
        /// <summary>Summed DPS the attacker met — Guard bands, station bands and the flat station
        /// floor. 0 when the arrival was unopposed.</summary>
        public long DefenderDps;
        /// <summary>The attacker's surviving fraction: <see cref="PendingFleetAction.ForcedRatio"/>
        /// or the elapsed ratio at resolution, 0 when it was destroyed before landing.</summary>
        public double AttackerRatio;
        /// <summary>Hulls the attacker lost: its whole convoy when it was intercepted or came out at
        /// or under the return threshold, otherwise none — the same rule <c>FinalizeReturn</c>
        /// brings the ships home by.</summary>
        public int AttackerHullsLost;
        /// <summary>The arms both sides fought with and the brackets fought, from the duel that
        /// decided this fight; null when range was not contested or no duel was fought.</summary>
        public DuelTactic Tactic;

        /// <summary>Did the attacker's mission land — the ratio above the threshold that also
        /// brings its hulls home.</summary>
        public bool AttackerWon { get { return AttackerRatio > 0.5; } }
        /// <summary>Did THIS side's copy win.</summary>
        public bool Won { get { return Attacking ? AttackerWon : !AttackerWon; } }
    }

    /// <summary>Files <see cref="FightRecord"/>s into <see cref="FactionState.Fights"/>.</summary>
    public static class FightLedger
    {
        /// <summary>Record one fight to both sides. <paramref name="intercepted"/> means the attacker
        /// was destroyed before landing, so its ratio is 0 whatever <paramref name="attackerRatio"/>
        /// says. Either side may be absent from <paramref name="states"/> (a player faction, an
        /// unowned node); the other still gets its copy.</summary>
        public static FightRecord Record(IList<FactionState> states, PendingFleetAction attacker, string defenderTag,
            SpatialNode targetNode, double attackerRatio, bool intercepted, long opposedDps, DateTime nowUtc, int cap)
        {
            double ratio = intercepted ? 0 : Math.Max(0, Math.Min(1, attackerRatio));
            int convoy = Math.Max(1, attacker.ConvoySize);
            var record = new FightRecord
            {
                Utc = nowUtc,
                Kind = attacker.Kind,
                Attacking = true,
                OpponentTag = defenderTag,
                NodeId = attacker.TargetNodeId,
                RegionKey = targetNode != null ? NodeRegion.Of(targetNode).ToKey() : null,
                AttackerDps = (long)TotalDps(attacker.WeaponBands),
                DefenderDps = Math.Max(0, opposedDps),
                AttackerRatio = ratio,
                AttackerHullsLost = ratio > 0.5 ? 0 : convoy,
                Tactic = attacker.DuelTactic,
            };
            Append(Find(states, attacker.ActorTag), record, cap);

            if (defenderTag != null && defenderTag != attacker.ActorTag)
            {
                FightRecord theirs = record;
                theirs.Attacking = false;
                theirs.OpponentTag = attacker.ActorTag;
                Append(Find(states, defenderTag), theirs, cap);
            }
            return record;
        }

        /// <summary>Records a player's occupation of <paramref name="node"/> that has ended, taken or
        /// not, as one Raid fought against <paramref name="owner"/> by
        /// <paramref name="attacker"/>, the occupier's registered player faction. That is what makes
        /// it a grievance: <see cref="RecentAttackers"/> names the attacker, so the owner can answer
        /// it with a retaliation campaign against that faction's ground whatever it thinks of them,
        /// as it answers an NPC raid.
        ///
        /// <para>Nothing is recorded for an occupier whose faction is not registered: retaliation
        /// targets ground, and only a registered faction holds any. No weapons are known, so both DPS
        /// figures are 0, which every reader of the ledger takes as "nothing to size against".
        /// <paramref name="taken"/> sets the attacker's ratio to 1 (it won) or 0.</para>
        /// <para>Provenance: FAC-483 (occupation).</para></summary>
        public static bool RecordOccupation(FactionState owner, FactionState attacker, SpatialNode node,
            bool taken, DateTime nowUtc, int cap)
        {
            if (owner == null || attacker == null || node == null || owner.Tag == attacker.Tag) return false;
            var record = new FightRecord
            {
                Utc = nowUtc,
                Kind = ActionKind.Raid,
                Attacking = true,
                OpponentTag = owner.Tag,
                NodeId = node.Id,
                RegionKey = NodeRegion.Of(node).ToKey(),
                AttackerRatio = taken ? 1.0 : 0.0,
            };
            Append(attacker, record, cap);
            FightRecord theirs = record;
            theirs.Attacking = false;
            theirs.OpponentTag = attacker.Tag;
            Append(owner, theirs, cap);
            return true;
        }

        /// <summary>Append, dropping the oldest past <paramref name="cap"/>.</summary>
        public static void Append(FactionState s, FightRecord record, int cap)
        {
            if (s == null) return;
            s.Fights.Add(record);
            int limit = Math.Max(1, cap);
            if (s.Fights.Count > limit) s.Fights.RemoveRange(0, s.Fights.Count - limit);
        }

        /// <summary>The fights <paramref name="s"/> attacked with since <paramref name="sinceUtc"/>
        /// against nodes in the region keyed <paramref name="regionKey"/>.</summary>
        public static List<FightRecord> AttacksOn(FactionState s, string regionKey, DateTime sinceUtc)
        {
            var result = new List<FightRecord>();
            if (s == null || regionKey == null) return result;
            foreach (FightRecord f in s.Fights)
                if (f.Attacking && f.Utc >= sinceUtc && string.Equals(f.RegionKey, regionKey, StringComparison.Ordinal))
                    result.Add(f);
            return result;
        }

        /// <summary>The fights <paramref name="s"/> DEFENDED since <paramref name="sinceUtc"/> —
        /// its copy of every attack brought against it, whatever the attacker's mission or where it
        /// fell. What <see cref="DefenceBudget"/> sizes a faction's own guns from.</summary>
        public static List<FightRecord> DefendedSince(FactionState s, DateTime sinceUtc)
        {
            var result = new List<FightRecord>();
            if (s == null) return result;
            foreach (FightRecord f in s.Fights)
                if (!f.Attacking && f.Utc >= sinceUtc) result.Add(f);
            return result;
        }

        /// <summary>Every faction that attacked <paramref name="s"/> at or after
        /// <paramref name="sinceUtc"/> — <see cref="DefendedSince"/> read by opponent. Empty, never
        /// null.
        ///
        /// <para>This is what lets a faction answer an attack it has no standing quarrel about: the
        /// record of having been attacked is the fact, and the attacker's standing is a separate
        /// question. A record with no opponent (an unowned node) names nobody and is skipped.</para>
        ///
        /// <para>Provenance: FAC-275 slice 2.</para></summary>
        public static HashSet<string> RecentAttackers(FactionState s, DateTime sinceUtc)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (FightRecord f in DefendedSince(s, sinceUtc))
                if (f.OpponentTag != null) result.Add(f.OpponentTag);
            return result;
        }

        private static FactionState Find(IList<FactionState> states, string tag)
        {
            if (states == null || tag == null) return null;
            for (int i = 0; i < states.Count; i++)
                if (states[i] != null && states[i].Tag == tag) return states[i];
            return null;
        }

        private static double TotalDps(List<WeaponBand> bands)
        {
            if (bands == null) return 0;
            double sum = 0;
            for (int i = 0; i < bands.Count; i++) sum += bands[i].Dps;
            return sum;
        }
    }
}
