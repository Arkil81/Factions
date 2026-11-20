using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// The turn-based, range-gated duel between an attacker's weapon bands and a defender's (a
    /// Guard force's and/or a station's, already merged by the caller) plus a flat, non-range-gated
    /// defender floor (<see cref="FactionTuning.StationDefensePower"/>). Each of
    /// <see cref="FactionTuning.CombatTurns"/> turns is fought at one range bracket; a band only
    /// contributes damage on turns where its own range covers that bracket.
    ///
    /// <para>Both sides track a single aggregate "combat capacity" HP pool (starting at their total
    /// DPS) — this does not model per-weapon hitpoints or subsystem loss, only when each band can
    /// act.</para>
    ///
    /// <para><b>Which bracket each turn is fought at.</b> Uncontested — asked for only by
    /// <c>factions bench duel</c> without a stance, as a baseline — the fixed closing order
    /// <see cref="FactionTuning.CombatRangeBracketsM"/>. Contested, as every real duel is, the schedule
    /// <see cref="RangeSchedule.Plan"/> builds from both sides' <see cref="DuelStance"/>, and a side
    /// that falls below its break-off fraction (<see cref="TacticChoice.BreakOffFraction"/>) while
    /// neither side is destroyed leaves at the end of that turn; the duel stops there. A side that
    /// cannot move (<see cref="DuelStance.CanBreakOff"/>) never leaves.</para>
    ///
    /// <para>A side with <see cref="DuelMunitions"/> fires only what its armed fleets carry, and
    /// holds fire once the other side is down so no rounds are spent on a finished fight. Its
    /// capacity pool is still sized from its bands: an empty magazine does not make a hull easier to
    /// kill. A side without one fires its bands without limit. Contested, each turn's range is read
    /// from what each such side can still fire (<see cref="DuelMunitions.LoadedBands"/>,
    /// <see cref="RangeSchedule.TurnAt"/>), so a side never asks for a range only its empty guns reach.</para>
    /// </summary>
    public static class ShipDuel
    {
        public struct Result
        {
            public double AttackerRatio;
            public double DefenderRatio;
            public bool AttackerDestroyed;
            public bool DefenderDestroyed;
            /// <summary>The attacker left below its break-off fraction. Never set uncontested.</summary>
            public bool AttackerBrokeOff;
            /// <summary>The defence left below its break-off fraction. Never set uncontested.</summary>
            public bool DefenderBrokeOff;
            /// <summary>The defence still stood and the meeting reads the attack as stopped: the attack is
            /// stopped as a broken-off one is (<see cref="AttackerBrokeOff"/> is set with it), and
            /// reported as repelled. Set by the meeting resolution — when the grids decided, or by
            /// <see cref="MarkRepelled"/> when the duel left the defence standing and ahead
            /// (<see cref="StandingDefenceRepels"/>) — never by the duel itself.</summary>
            public bool AttackerRepelled;
            public string TurnSummary;
            /// <summary>The fraction of each side's pool that the turns fought here left of what it
            /// brought into them: the factor a surviving hull's integrity is scaled by. Equal to the
            /// ratio for a whole duel; for a resumed one (<see cref="Fight(List{WeaponBand},List{WeaponBand},long,FactionTuning,IRandom,DuelMunitions,DuelMunitions,bool,DuelStance,DuelStance,int,int,double,double,double,double)"/>)
            /// it covers only the turns fought, so damage taken before them is not applied twice.</summary>
            public double AttackerAttrition;
            public double DefenderAttrition;
            /// <summary>Who decided the fight: <c>duel</c>, <c>physical</c> (the grids) or
            /// <c>handoff</c> (both). Set by the meeting resolution; null from a bare duel.</summary>
            public string Source;
            /// <summary>Each side's capacity pool at the start: its total DPS, the defence's with the
            /// flat floor, at least 1. Damage dealt to a side is its capacity × (1 − its ratio).</summary>
            public double AttackerCapacity;
            public double DefenderCapacity;
            /// <summary>The planned schedule, contested only; null otherwise.</summary>
            public DuelTurn[] Plan;
            /// <summary>The bracket of every turn fought, in metres, contested only; null otherwise.</summary>
            public int[] BracketsFought;
            /// <summary>How the duel was fought, set by the resolver; null when uncontested.</summary>
            public DuelTactic Tactic;
        }

        /// <summary>
        /// Whether a meeting reads <paramref name="r"/> as a raid the standing defence repelled. The
        /// defence must have brought something (<paramref name="defenceDps"/> above 0 — a bare node
        /// stands with nothing) and still stand (neither destroyed nor broken off), and the attacker
        /// must still be in the fight (neither destroyed nor broken off). Then the attacker is repelled
        /// when it lost a larger share of the capacity it brought than the defence lost of its own —
        /// a raid does not take its loot under a defence still firing on it and winning — or when
        /// neither side lost anything at all (undecided repels the attacker). An attacker that lost
        /// the same or a smaller share lands at its ratio. Shares, not absolute damage, so a large raid
        /// against a small garrison compares fairly.
        /// </summary>
        public static bool StandingDefenceRepels(Result r, double defenceDps)
        {
            if (defenceDps <= 0 || r.AttackerDestroyed || r.AttackerBrokeOff
                || r.DefenderDestroyed || r.DefenderBrokeOff) return false;
            return r.AttackerRatio < r.DefenderRatio                    // lost the larger share
                   || (r.AttackerRatio >= 1.0 && r.DefenderRatio >= 1.0); // neither side touched
        }

        /// <summary>Marks <paramref name="r"/> repelled (<see cref="Result.AttackerRepelled"/>, with
        /// <see cref="Result.AttackerBrokeOff"/>) and ends its turn summary with <c>defence_standing</c>.</summary>
        public static void MarkRepelled(ref Result r)
        {
            r.AttackerRepelled = true;
            r.AttackerBrokeOff = true;
            r.TurnSummary = string.IsNullOrEmpty(r.TurnSummary) ? "defence_standing" : r.TurnSummary + " | defence_standing";
        }

        public static Result Fight(
            List<WeaponBand> attackerBands, List<WeaponBand> defenderBands, long defenderFlatPower,
            FactionTuning tuning, IRandom rng,
            DuelMunitions attackerMunitions, DuelMunitions defenderMunitions,
            bool contested, DuelStance attacker, DuelStance defender)
        {
            double attackerMax = Math.Max(1, TotalDps(attackerBands));
            double defenderMax = Math.Max(1, TotalDps(defenderBands) + defenderFlatPower);
            return Fight(attackerBands, defenderBands, defenderFlatPower, tuning, rng, attackerMunitions,
                         defenderMunitions, contested, attacker, defender, 0, int.MaxValue,
                         attackerMax, attackerMax, defenderMax, defenderMax);
        }

        /// <summary>Turns <paramref name="fromTurn"/> up to (not including) <paramref name="toTurn"/>
        /// of a duel whose pools were <paramref name="attackerMax"/> and <paramref name="defenderMax"/>
        /// when it began and stand at <paramref name="attackerHp"/> and <paramref name="defenderHp"/>
        /// now. Ratios and break-off read against the pools the duel began with; the attrition
        /// fractions against what each side brought into these turns. The schedule is the whole
        /// duel's, so turn <c>t</c> is fought at the bracket it would have been in one pass.</summary>
        public static Result Fight(
            List<WeaponBand> attackerBands, List<WeaponBand> defenderBands, long defenderFlatPower,
            FactionTuning tuning, IRandom rng,
            DuelMunitions attackerMunitions, DuelMunitions defenderMunitions,
            bool contested, DuelStance attacker, DuelStance defender,
            int fromTurn, int toTurn, double attackerMax, double attackerHp, double defenderMax, double defenderHp)
        {
            attackerMax = Math.Max(1, attackerMax);
            defenderMax = Math.Max(1, defenderMax);
            attackerHp = Math.Max(0, Math.Min(attackerMax, attackerHp));
            defenderHp = Math.Max(0, Math.Min(defenderMax, defenderHp));
            double attackerIn = attackerHp, defenderIn = defenderHp;

            double[] brackets = RangeSchedule.FixedOrder(tuning);
            int turns = Math.Min(brackets.Length, toTurn);
            DuelTurn[] plan = contested
                ? RangeSchedule.Plan(attackerBands, defenderBands, defenderFlatPower, attacker, defender, tuning)
                : null;
            double atkLeaveBelow = TacticChoice.BreakOffFraction(attacker.BreakOff);
            double defLeaveBelow = TacticChoice.BreakOffFraction(defender.BreakOff);
            bool atkBrokeOff = false, defBrokeOff = false;
            List<int> fought = contested ? new List<int>(turns) : null;

            var summary = new System.Text.StringBuilder();
            for (int t = Math.Max(0, fromTurn); t < turns; t++)
            {
                // A side firing from magazines asks only for a range its loaded guns still reach, so
                // its turn is read from what it holds now, not from the bands it set out with.
                if (contested && (attackerMunitions != null || defenderMunitions != null))
                    plan[t] = RangeSchedule.TurnAt(t, t > 0 ? plan[t - 1].RangeM : double.NaN,
                        attackerMunitions != null ? attackerMunitions.LoadedBands() : attackerBands,
                        defenderMunitions != null ? defenderMunitions.LoadedBands() : defenderBands,
                        defenderFlatPower, attacker, defender, tuning);
                double bracket = contested ? plan[t].RangeM : brackets[t];
                if (fought != null && attackerHp > 0 && defenderHp > 0) fought.Add((int)Math.Round(bracket));
                bool atkFires = attackerHp > 0 && (attackerMunitions == null || defenderHp > 0);
                bool defFires = defenderHp > 0 && (defenderMunitions == null || attackerHp > 0);
                double atkTurnDps = !atkFires ? 0
                    : attackerMunitions != null ? attackerMunitions.Fire(bracket) : CombatStrength.InRangeDps(attackerBands, bracket);
                double defTurnDps = !defFires ? 0
                    : (defenderMunitions != null ? defenderMunitions.Fire(bracket) : CombatStrength.InRangeDps(defenderBands, bracket))
                      + defenderFlatPower;

                double atkDmg = atkTurnDps * tuning.CombatBaseDamageFraction * Jitter(rng, tuning.CombatVariance);
                double defDmg = defTurnDps * tuning.CombatBaseDamageFraction * Jitter(rng, tuning.CombatVariance);

                defenderHp = Math.Max(0, defenderHp - atkDmg);
                attackerHp = Math.Max(0, attackerHp - defDmg);

                if (summary.Length > 0) summary.Append(" | ");
                summary.Append("T").Append(t + 1).Append("(").Append(bracket.ToString("0", CultureInfo.InvariantCulture));
                if (contested) summary.Append("m set=").Append(SetterName(plan[t].SetBy)).Append(") atk=");
                else summary.Append("m) atk=");
                summary.Append(atkDmg.ToString("0", CultureInfo.InvariantCulture)).Append(" def=").Append(defDmg.ToString("0", CultureInfo.InvariantCulture));

                if (!contested || attackerHp <= 0 || defenderHp <= 0) continue;
                atkBrokeOff = attacker.CanBreakOff && attackerHp / attackerMax < atkLeaveBelow;
                defBrokeOff = defender.CanBreakOff && defenderHp / defenderMax < defLeaveBelow;
                if (atkBrokeOff || defBrokeOff)
                {
                    summary.Append(" | broke_off=").Append(atkBrokeOff && defBrokeOff ? "both" : atkBrokeOff ? "atk" : "def");
                    break;
                }
            }

            var result = new Result
            {
                AttackerRatio = Clamp01(attackerHp / attackerMax),
                DefenderRatio = Clamp01(defenderHp / defenderMax),
                AttackerDestroyed = attackerHp <= 0,
                DefenderDestroyed = defenderHp <= 0,
                AttackerBrokeOff = atkBrokeOff,
                DefenderBrokeOff = defBrokeOff,
                TurnSummary = summary.ToString(),
                AttackerAttrition = attackerIn > 0 ? Clamp01(attackerHp / attackerIn) : 0,
                DefenderAttrition = defenderIn > 0 ? Clamp01(defenderHp / defenderIn) : 0,
                AttackerCapacity = attackerMax,
                DefenderCapacity = defenderMax,
                Plan = plan,
                BracketsFought = fought != null ? fought.ToArray() : null,
            };
            return result;
        }

        public static string SetterName(DuelSetter setter)
        {
            switch (setter)
            {
                case DuelSetter.Attacker: return "atk";
                case DuelSetter.Defender: return "def";
                default: return "both";
            }
        }

        private static double TotalDps(List<WeaponBand> bands)
        {
            if (bands == null) return 0;
            double sum = 0;
            for (int i = 0; i < bands.Count; i++) sum += bands[i].Dps;
            return sum;
        }

        private static double Jitter(IRandom rng, double variance)
        {
            if (variance <= 0) return 1.0;
            return 1.0 + (rng.NextDouble() * 2.0 - 1.0) * variance;
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            return v > 1 ? 1 : v;
        }
    }
}
