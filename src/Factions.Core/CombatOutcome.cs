using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>How an attack came out of its meeting.</summary>
    public enum CombatAttackResult : byte
    {
        /// <summary>Broke through: the raid goes on to land at the ratio it kept.</summary>
        Landed = 0,
        /// <summary>The grids left it undecided with the defence still standing; it leaves at once.</summary>
        Repelled = 1,
        /// <summary>It fell below its break-off fraction and left the fight.</summary>
        BrokeOff = 2,
        /// <summary>The defence beat it.</summary>
        Destroyed = 3,
    }

    /// <summary>How the defence came out of its meeting.</summary>
    public enum CombatDefenceResult : byte
    {
        Stood = 0,
        BrokeOff = 1,
        Destroyed = 2,
    }

    /// <summary>What one viewer makes of a fight. The byte values ride the wire
    /// (<c>WireMessageKind.CombatOutcomeNotice</c>).</summary>
    public enum CombatTone : byte
    {
        /// <summary>The viewer's faction is on neither side.</summary>
        Neutral = 0,
        Win = 1,
        Loss = 2,
    }

    /// <summary>Which meeting resolved: Guards (with the node's station), the station alone, or
    /// Guards with trade in the crossfire.</summary>
    public enum CombatMeeting : byte
    {
        Guard = 0,
        Station = 1,
        Triple = 2,
    }

    /// <summary>
    /// One meeting resolution: who attacked whom, where, how each side came out, what happens next
    /// and what it cost in hulls. Written by <see cref="PendingFleetResolver"/> at every meeting
    /// resolution (a Guard force, a station alone, or a triple) whatever decided it -- the duel,
    /// the grids, or a handoff between them -- logged once as <c>COMBAT_OUTCOME</c> and queued on
    /// <see cref="WorldModel.TakeCombatOutcomes"/> for the host to tell the players who could see it.
    /// </summary>
    public sealed class CombatOutcome
    {
        public DateTime Utc;
        /// <summary>The attacking fleet's kind: Raid, Retaliate or Conquer.</summary>
        public ActionKind Kind;
        public CombatMeeting Meeting;
        public string AttackerTag;
        /// <summary>The attacking fleet's log id (<c>[b.W1.atk]</c>).</summary>
        public string AttackerFleet;
        /// <summary>The faction the defence fought for: the lead Guard's, or the station's owner
        /// when the station stood alone. Null for an unowned node.</summary>
        public string DefenderTag;
        /// <summary>The lead Guard's log id, or null when the station stood alone.</summary>
        public string DefenderFleet;
        /// <summary>Every faction on the defending side: each Guard's, and the node's owner.</summary>
        public List<string> DefenderTags = new List<string>();
        public string NodeId;
        /// <summary>Where it was fought: the node, or the attacker's position when the meeting point
        /// is not a graph node (a bench fight).</summary>
        public Vec3 PositionKm;
        public CombatAttackResult Attack;
        public CombatDefenceResult Defence;
        public int AttackerHulls, AttackerHullsLost;
        /// <summary>Zero when the station stood alone: a station is not counted in hulls.</summary>
        public int DefenderHulls, DefenderHullsLost;
        /// <summary><c>duel</c>, <c>physical</c> or <c>handoff</c>: what decided it.</summary>
        public string Source;
        /// <summary>The bench scenario the attacker belongs to, or null.</summary>
        public string BenchLabel;
        /// <summary>Why an attack that broke through is leaving anyway, or null while it carries on.
        /// Set by the resolution that ends its stay (<see cref="WorldModel.MarkCombatOutcomeWithdrawal"/>),
        /// which runs in the same pass as the meeting: "out of ammunition", "worn down", "the node's
        /// hold is spent", "the node changed hands", or "the raid is done".</summary>
        public string Withdrawal;

        /// <summary>The attack broke through.</summary>
        public bool AttackerWon { get { return Attack == CombatAttackResult.Landed; } }

        /// <summary>The attack was stopped and the defence is still there.</summary>
        public bool DefenceHeld
        {
            get { return Attack != CombatAttackResult.Landed && Defence != CombatDefenceResult.Destroyed; }
        }

        /// <summary>Whether <paramref name="viewerTag"/>'s side won, lost, or was not in it. The
        /// attacker wins by breaking through; the defence by stopping it and still standing, so a
        /// fight that destroyed both is a loss for both.</summary>
        public CombatTone ToneFor(string viewerTag)
        {
            if (string.IsNullOrEmpty(viewerTag)) return CombatTone.Neutral;
            if (string.Equals(viewerTag, AttackerTag, StringComparison.OrdinalIgnoreCase))
                return AttackerWon ? CombatTone.Win : CombatTone.Loss;
            if (IsDefender(viewerTag)) return DefenceHeld ? CombatTone.Win : CombatTone.Loss;
            return CombatTone.Neutral;
        }

        private bool IsDefender(string tag)
        {
            if (string.Equals(tag, DefenderTag, StringComparison.OrdinalIgnoreCase)) return true;
            if (DefenderTags == null) return false;
            for (int i = 0; i < DefenderTags.Count; i++)
                if (string.Equals(tag, DefenderTags[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>What the attackers do now, as a log token.</summary>
        public string NextStep
        {
            get
            {
                switch (Attack)
                {
                    case CombatAttackResult.Landed: return Withdrawal == null ? "press_on" : "withdraw";
                    case CombatAttackResult.Repelled:
                    case CombatAttackResult.BrokeOff: return "withdraw";
                    default: return "none";
                }
            }
        }

        private string AttackNoun
        {
            get
            {
                switch (Kind)
                {
                    case ActionKind.Retaliate: return "reprisal";
                    case ActionKind.Conquer: return "assault";
                    default: return "raid";
                }
            }
        }

        private static string Bracket(string tag) { return "[" + (tag ?? "?") + "]"; }

        /// <summary>The player-facing sentence, the same for every viewer: e.g.
        /// <c>[EXP] raid on [IGSL] at bench:W1 was repelled; the raiders are withdrawing. Hulls lost: EXP 0/3, IGSL 1/2.</c></summary>
        public string Sentence()
        {
            var sb = new StringBuilder();
            sb.Append(Bracket(AttackerTag)).Append(' ').Append(AttackNoun).Append(" on ");
            if (Meeting == CombatMeeting.Station)
                sb.Append(DefenderTag != null ? Bracket(DefenderTag) + "'s station" : "the station");
            else sb.Append(Bracket(DefenderTag));
            sb.Append(" at ").Append(NodeId ?? "?");

            switch (Attack)
            {
                case CombatAttackResult.Landed:
                    sb.Append(Defence == CombatDefenceResult.Destroyed ? " broke through; the defence was destroyed"
                              : Defence == CombatDefenceResult.BrokeOff ? " broke through; the defenders withdrew"
                              : " broke through the defence");
                    sb.Append(Withdrawal == null ? ", and the raiders press the attack."
                                                 : ", and the raiders are withdrawing (" + Withdrawal + ").");
                    break;
                case CombatAttackResult.Repelled:
                    sb.Append(" was repelled; the raiders are withdrawing.");
                    break;
                case CombatAttackResult.BrokeOff:
                    sb.Append(" broke off; the raiders are withdrawing.");
                    break;
                default:
                    sb.Append(Defence == CombatDefenceResult.Destroyed
                              ? " ended with both sides destroyed." : " was destroyed by the defence.");
                    break;
            }
            if (Attack != CombatAttackResult.Landed && Defence == CombatDefenceResult.BrokeOff)
                sb.Append(" The defenders withdrew.");

            sb.Append(" Hulls lost: ").Append(AttackerTag ?? "?").Append(' ')
              .Append(AttackerHullsLost.ToString(CultureInfo.InvariantCulture)).Append('/')
              .Append(AttackerHulls.ToString(CultureInfo.InvariantCulture));
            if (DefenderHulls > 0)
                sb.Append(", ").Append(DefenderTag ?? "?").Append(' ')
                  .Append(DefenderHullsLost.ToString(CultureInfo.InvariantCulture)).Append('/')
                  .Append(DefenderHulls.ToString(CultureInfo.InvariantCulture));
            sb.Append('.');
            return sb.ToString();
        }

        /// <summary>The decision-log line, written once per resolution.</summary>
        public string LogLine()
        {
            var sb = new StringBuilder("COMBAT_OUTCOME ");
            sb.Append(AttackerFleet ?? "?").Append(' ').Append(AttackerTag ?? "?")
              .Append(" vs ").Append(DefenderFleet ?? "station").Append(' ').Append(DefenderTag ?? "-")
              .Append(" @").Append(NodeId ?? "?")
              .Append(" kind=").Append(Kind)
              .Append(" meeting=").Append(Meeting)
              .Append(" attack=").Append(Attack)
              .Append(" defence=").Append(Defence)
              .Append(" next=").Append(NextStep)
              .Append(" hulls_lost atk=").Append(AttackerHullsLost.ToString(CultureInfo.InvariantCulture))
              .Append('/').Append(AttackerHulls.ToString(CultureInfo.InvariantCulture))
              .Append(" def=").Append(DefenderHullsLost.ToString(CultureInfo.InvariantCulture))
              .Append('/').Append(DefenderHulls.ToString(CultureInfo.InvariantCulture))
              .Append(" src=").Append(Source ?? "?");
            if (BenchLabel != null) sb.Append(" bench=").Append(BenchLabel);
            return sb.ToString();
        }

        /// <summary>Who is told about this fight, and in which tone: every player
        /// <paramref name="playersWithin"/> finds inside <paramref name="observationRadiusKm"/> of
        /// where it was fought -- the radius a fleet materialises in, so a viewer is told only of a
        /// fight they could have seen -- each with the tone their faction
        /// (<paramref name="factionTagOf"/>, null for none) reads it in.</summary>
        public List<CombatNotice> Recipients(Func<Vec3, double, IEnumerable<ulong>> playersWithin,
            double observationRadiusKm, Func<ulong, string> factionTagOf)
        {
            var notices = new List<CombatNotice>();
            if (playersWithin == null || observationRadiusKm <= 0) return notices;
            IEnumerable<ulong> viewers = playersWithin(PositionKm, observationRadiusKm);
            if (viewers == null) return notices;
            foreach (ulong id in viewers)
                notices.Add(new CombatNotice
                {
                    SteamId = id,
                    Tone = ToneFor(factionTagOf != null ? factionTagOf(id) : null),
                });
            return notices;
        }
    }

    /// <summary>One viewer's notice of a <see cref="CombatOutcome"/>.</summary>
    public struct CombatNotice
    {
        public ulong SteamId;
        public CombatTone Tone;
    }
}
