using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Which side chose the range a duel turn was fought at.</summary>
    public enum DuelSetter
    {
        /// <summary>Both sides wanted this range; nobody had to take it.</summary>
        Both = 0,
        Attacker = 1,
        Defender = 2,
    }

    /// <summary>One side's tactic in a duel: the range it seeks, the surviving fraction at which it
    /// leaves, and how fast it can open or close the range.</summary>
    public struct DuelStance
    {
        public TacticArm Arm;
        public BreakOffLevel BreakOff;
        /// <summary>Combat acceleration in m/s² (<see cref="CombatMobility"/>). 0 is a side that
        /// cannot move at all — a station; below 0 is unknown (an unscanned hull).</summary>
        public double Mobility;

        /// <summary>A side that cannot move cannot leave: a station fights to the end.</summary>
        public bool CanBreakOff { get { return Mobility != 0; } }
    }

    /// <summary>The attacker a defence is being measured against, with both sides' tactics — what
    /// <see cref="CombatStrength.DuelDamage(IReadOnlyList{WeaponBand},SpatialNode,FactionTuning,DuelMatchup)"/>
    /// needs to predict the range schedule the duel will actually fight.</summary>
    public sealed class DuelMatchup
    {
        public IReadOnlyList<WeaponBand> AttackerBands;
        public DuelStance Attacker;
        public DuelStance Defender;
    }

    /// <summary>One turn of a contested duel: the range it is fought at and who set it.</summary>
    public struct DuelTurn
    {
        public double RangeM;
        public DuelSetter SetBy;
    }

    /// <summary>
    /// The range each duel turn is fought at when range is contested (docs/fleet-tactics.md, "The
    /// duel gets a range schedule").
    ///
    /// <para><b>Preferences.</b> Each side names a bracket per turn from its <see cref="TacticArm"/>,
    /// out of the distinct values of <see cref="FactionTuning.CombatRangeBracketsM"/>:</para>
    /// <list type="bullet">
    ///   <item><b>Brawl</b> — the closest bracket, every turn.</item>
    ///   <item><b>Standoff</b> — the bracket where its own in-range DPS divided by the enemy's is
    ///   best (<see cref="StandoffBracket"/>), every turn.</item>
    ///   <item><b>Kite</b> — the Standoff bracket; on a turn after one fought closer than that, the
    ///   longest bracket at which it still lands anything (it opens the range the enemy closed).</item>
    ///   <item><b>AttackRun</b> — the closest bracket on the first turn, the Standoff bracket
    ///   after it.</item>
    /// </list>
    ///
    /// <para><b>A side only asks for a range it can shoot at.</b> Standoff and Kite choose among the
    /// brackets where the side's own weapons land at least <see cref="NegligibleOutputShare"/> of what
    /// they land at their best bracket (<see cref="InReach"/>). The station's flat floor is left out of
    /// that test — it lands the same at every range, so it says nothing about where the guns reach —
    /// but still counts in the Standoff ratio. A side whose weapons land nothing at any bracket keeps
    /// every bracket.</para>
    ///
    /// <para><b>Allocation is deterministic.</b> Each turn both sides bank their mobility share
    /// (<see cref="AttackerShare"/>) and the side with more banked credit sets the turn and spends
    /// one turn of credit; a tie goes to the attacker, which chooses the moment of contact. Over
    /// <c>n</c> turns a share of <c>s</c> therefore sets <c>round(s·n)</c> of them, spread evenly
    /// rather than front-loaded — a 0.7 share sets 7 turns in 10. When both sides want the same
    /// bracket the turn is <see cref="DuelSetter.Both"/>, and the credit is still spent: who set it
    /// did not change where it was fought.</para>
    ///
    /// <para>The schedule reads bands, arms and mobility only — never hit points — so the commit
    /// gates can predict it exactly (<see cref="CombatStrength.DuelDamage(IReadOnlyList{WeaponBand},SpatialNode,FactionTuning,DuelMatchup)"/>).</para>
    /// </summary>
    public static class RangeSchedule
    {
        /// <summary>The share of a side's best-bracket weapon DPS below which a bracket is out of its
        /// reach: a range where it lands under a tenth of what it can land elsewhere is one it cannot
        /// meaningfully shoot at, and no arm asks for it while another bracket gives it output.</summary>
        public const double NegligibleOutputShare = 0.1;

        /// <summary>The per-turn brackets of the fixed closing order — <see cref="FactionTuning.CombatRangeBracketsM"/>
        /// when it has one entry per turn, point blank every turn otherwise.</summary>
        public static double[] FixedOrder(FactionTuning tuning)
        {
            int turns = tuning.CombatTurns > 0 ? tuning.CombatTurns : 1;
            return (tuning.CombatRangeBracketsM != null && tuning.CombatRangeBracketsM.Length == turns)
                ? tuning.CombatRangeBracketsM : new double[turns];
        }

        /// <summary>The distinct brackets of the fixed order, closest first.</summary>
        public static double[] Brackets(FactionTuning tuning)
        {
            return Distinct(FixedOrder(tuning));
        }

        /// <summary>The fraction of turns the attacker sets. A side with mobility 0 sets none against
        /// one that can move, so an attacker always sets the range against a station. An unknown
        /// mobility on either side, or two immobile sides, splits evenly.</summary>
        public static double AttackerShare(double attackerMobility, double defenderMobility)
        {
            if (defenderMobility == 0 && attackerMobility != 0) return 1.0;
            if (attackerMobility == 0 && defenderMobility != 0) return 0.0;
            if (attackerMobility < 0 || defenderMobility < 0) return 0.5;
            double sum = attackerMobility + defenderMobility;
            return sum > 0 ? attackerMobility / sum : 0.5;
        }

        /// <summary>The contested schedule for one duel. <paramref name="defenderBands"/> is every
        /// band the defence fires (convoy and station turrets); <paramref name="defenderFlat"/> is the
        /// station's always-in-range floor, which counts on the defence's side of every ratio.</summary>
        public static DuelTurn[] Plan(IReadOnlyList<WeaponBand> attackerBands, IReadOnlyList<WeaponBand> defenderBands,
            long defenderFlat, DuelStance attacker, DuelStance defender, FactionTuning tuning)
        {
            double[] order = FixedOrder(tuning);
            int turns = order.Length;
            double[] brackets = Distinct(order);
            double closest = brackets[0];
            double atkStandoff = StandoffBracket(attackerBands, 0, defenderBands, defenderFlat, brackets);
            double defStandoff = StandoffBracket(defenderBands, defenderFlat, attackerBands, 0, brackets);
            double atkReach = LongestReach(attackerBands, 0, brackets);
            double defReach = LongestReach(defenderBands, defenderFlat, brackets);

            double share = AttackerShare(attacker.Mobility, defender.Mobility);
            double atkCredit = 0, defCredit = 0;
            var plan = new DuelTurn[turns];
            double previous = double.NaN;
            for (int t = 0; t < turns; t++)
            {
                double atkWants = Preferred(attacker.Arm, t, previous, closest, atkStandoff, atkReach);
                double defWants = Preferred(defender.Arm, t, previous, closest, defStandoff, defReach);
                atkCredit += share;
                defCredit += 1.0 - share;
                bool attackerSets = atkCredit >= defCredit - 1e-9;
                if (attackerSets) atkCredit -= 1.0; else defCredit -= 1.0;

                DuelTurn turn;
                turn.RangeM = attackerSets ? atkWants : defWants;
                turn.SetBy = atkWants == defWants ? DuelSetter.Both
                           : attackerSets ? DuelSetter.Attacker : DuelSetter.Defender;
                plan[t] = turn;
                previous = turn.RangeM;
            }
            return plan;
        }

        /// <summary>Turn <paramref name="turn"/> of the schedule <see cref="Plan"/> builds, with each
        /// side's preference read from <paramref name="attackerBands"/> and <paramref name="defenderBands"/>
        /// as they stand now and the previous turn fought at <paramref name="previousRangeM"/> (NaN on
        /// the first). Who sets it is the same allocation as the whole plan's. The duel asks this each
        /// turn for a side whose magazines decide what it can still fire.</summary>
        public static DuelTurn TurnAt(int turn, double previousRangeM,
            IReadOnlyList<WeaponBand> attackerBands, IReadOnlyList<WeaponBand> defenderBands,
            long defenderFlat, DuelStance attacker, DuelStance defender, FactionTuning tuning)
        {
            double[] brackets = Distinct(FixedOrder(tuning));
            double closest = brackets[0];
            double atkWants = Preferred(attacker.Arm, turn, previousRangeM, closest,
                StandoffBracket(attackerBands, 0, defenderBands, defenderFlat, brackets),
                LongestReach(attackerBands, 0, brackets));
            double defWants = Preferred(defender.Arm, turn, previousRangeM, closest,
                StandoffBracket(defenderBands, defenderFlat, attackerBands, 0, brackets),
                LongestReach(defenderBands, defenderFlat, brackets));
            double share = AttackerShare(attacker.Mobility, defender.Mobility);
            double atkCredit = 0, defCredit = 0;
            bool attackerSets = true;
            for (int t = 0; t <= turn; t++)
            {
                atkCredit += share;
                defCredit += 1.0 - share;
                attackerSets = atkCredit >= defCredit - 1e-9;
                if (attackerSets) atkCredit -= 1.0; else defCredit -= 1.0;
            }
            DuelTurn result;
            result.RangeM = attackerSets ? atkWants : defWants;
            result.SetBy = atkWants == defWants ? DuelSetter.Both
                         : attackerSets ? DuelSetter.Attacker : DuelSetter.Defender;
            return result;
        }

        /// <summary>The bracket <paramref name="arm"/> asks for on turn <paramref name="turn"/>, given
        /// the range the previous turn was fought at (NaN on the first).</summary>
        public static double Preferred(TacticArm arm, int turn, double previousRangeM,
            double closest, double standoff, double longestReach)
        {
            switch (arm)
            {
                case TacticArm.Brawl:
                    return closest;
                case TacticArm.Kite:
                    return turn > 0 && previousRangeM < standoff ? Math.Max(standoff, longestReach) : standoff;
                case TacticArm.AttackRun:
                    return turn == 0 ? closest : standoff;
                default:
                    return standoff;
            }
        }

        /// <summary>The bracket where <paramref name="own"/>'s in-range DPS divided by
        /// <paramref name="enemy"/>'s is best, among the brackets in <paramref name="own"/>'s reach
        /// (<see cref="InReach"/>). A bracket the enemy cannot reach and this side can is best of all;
        /// ties go to the bracket where this side lands more, then to the longer one. Flat terms are
        /// always in range.</summary>
        public static double StandoffBracket(IReadOnlyList<WeaponBand> own, long ownFlat,
            IReadOnlyList<WeaponBand> enemy, long enemyFlat, double[] brackets)
        {
            double best = brackets[brackets.Length - 1];
            double bestRatio = double.NegativeInfinity, bestOwn = double.NegativeInfinity;
            bool[] reach = InReach(own, brackets);
            for (int i = brackets.Length - 1; i >= 0; i--)   // longest first, so ties keep the longer
            {
                if (!reach[i]) continue;
                double mine = CombatStrength.InRangeDps(own, brackets[i]) + ownFlat;
                double theirs = CombatStrength.InRangeDps(enemy, brackets[i]) + enemyFlat;
                double ratio = theirs > 0 ? mine / theirs : (mine > 0 ? double.PositiveInfinity : 0);
                if (ratio > bestRatio || (ratio == bestRatio && mine > bestOwn))
                {
                    best = brackets[i];
                    bestRatio = ratio;
                    bestOwn = mine;
                }
            }
            return best;
        }

        /// <summary>The longest bracket in <paramref name="own"/>'s reach (<see cref="InReach"/>).
        /// When its weapons land nothing anywhere: the longest bracket if the flat floor
        /// (<paramref name="ownFlat"/>) lands there, the closest otherwise.</summary>
        public static double LongestReach(IReadOnlyList<WeaponBand> own, long ownFlat, double[] brackets)
        {
            if (BestOutput(own, brackets) > 0)
            {
                bool[] reach = InReach(own, brackets);
                for (int i = brackets.Length - 1; i >= 0; i--)
                    if (reach[i]) return brackets[i];
            }
            return ownFlat > 0 ? brackets[brackets.Length - 1] : brackets[0];
        }

        /// <summary>Per bracket, whether <paramref name="own"/>'s weapons land at least
        /// <see cref="NegligibleOutputShare"/> of their best bracket's DPS there. Every bracket when
        /// they land nothing anywhere, so a side with no guns in the fight keeps its old choice.</summary>
        public static bool[] InReach(IReadOnlyList<WeaponBand> own, double[] brackets)
        {
            var reach = new bool[brackets.Length];
            double best = BestOutput(own, brackets);
            for (int i = 0; i < brackets.Length; i++)
                reach[i] = best <= 0 || CombatStrength.InRangeDps(own, brackets[i]) >= NegligibleOutputShare * best;
            return reach;
        }

        private static double BestOutput(IReadOnlyList<WeaponBand> own, double[] brackets)
        {
            double best = 0;
            for (int i = 0; i < brackets.Length; i++)
                best = Math.Max(best, CombatStrength.InRangeDps(own, brackets[i]));
            return best;
        }

        /// <summary>The distinct brackets, closest first.</summary>
        private static double[] Distinct(double[] order)
        {
            var set = new List<double>();
            for (int i = 0; i < order.Length; i++)
                if (!set.Contains(order[i])) set.Add(order[i]);
            set.Sort();
            return set.ToArray();
        }
    }

    /// <summary>
    /// The one place a faction's tactic for a fight is chosen — by the duel and by both commit gates.
    ///
    /// <para>With learning off it is a fixed default per personality (<see cref="DefaultFor"/>).
    /// With learning on (<see cref="WorldModel.TacticLearning"/>) it is the faction's own
    /// <see cref="TacticMemory.Pick"/> against that opponent in that context, whose prior is the same
    /// default. The pick is deterministic for a given memory state, so a gate asking "what would I
    /// pick" and the duel that follows agree.</para>
    ///
    /// <para>Break-off fractions are code constants, not tuning: they are the learner's action
    /// space, not an operator balance knob.</para></summary>
    public static class TacticChoice
    {
        public const double BreakOffLowFraction = 0.2;
        public const double BreakOffMidFraction = 0.35;
        public const double BreakOffHighFraction = 0.5;

        /// <summary>The stance <paramref name="faction"/> fights with at <paramref name="mobility"/>.
        /// A null faction (an unowned node's station) takes the default personality's.</summary>
        public static DuelStance For(FactionState faction, double mobility)
        {
            DuelStance stance = DefaultFor(faction != null ? faction.PersonalityId : null);
            stance.Mobility = mobility;
            return stance;
        }

        /// <summary>The stance <paramref name="faction"/> fights <paramref name="opponentTag"/> with in
        /// <paramref name="context"/>: <see cref="For(FactionState,double)"/> exactly when
        /// <paramref name="learning"/> is off or there is no faction, the learned pick otherwise.</summary>
        public static DuelStance For(FactionState faction, double mobility, string opponentTag,
            TacticContext context, bool learning, DateTime nowUtc)
        {
            TacticPick pick;
            return For(faction, mobility, opponentTag, context, learning, nowUtc, out pick);
        }

        /// <summary><see cref="For(FactionState,double,string,TacticContext,bool,DateTime)"/>, with the
        /// pick behind it; <paramref name="pick"/> is default when nothing was learned from.</summary>
        public static DuelStance For(FactionState faction, double mobility, string opponentTag,
            TacticContext context, bool learning, DateTime nowUtc, out TacticPick pick)
        {
            pick = new TacticPick();
            if (!learning || faction == null) return For(faction, mobility);
            pick = faction.TacticMemory.Pick(faction.Tag, faction.PersonalityId, opponentTag, context, nowUtc);
            return new DuelStance { Arm = pick.Arm, BreakOff = pick.BreakOff, Mobility = mobility };
        }

        /// <summary>The fixed per-personality default. factorum fights to the end at close range; a
        /// raider makes attack runs; a mercantile faction kites and leaves early; everyone else holds
        /// the range that suits it.</summary>
        public static DuelStance DefaultFor(string personalityId)
        {
            var stance = new DuelStance { Arm = TacticArm.Standoff, BreakOff = BreakOffLevel.Mid };
            switch (personalityId)
            {
                case "factorum":
                    stance.Arm = TacticArm.Brawl; stance.BreakOff = BreakOffLevel.Low; break;
                case "raider":
                    stance.Arm = TacticArm.AttackRun; stance.BreakOff = BreakOffLevel.Mid; break;
                case "mercantile":
                    stance.Arm = TacticArm.Kite; stance.BreakOff = BreakOffLevel.High; break;
            }
            return stance;
        }

        /// <summary>The surviving fraction below which a side at <paramref name="level"/> leaves.</summary>
        public static double BreakOffFraction(BreakOffLevel level)
        {
            switch (level)
            {
                case BreakOffLevel.Low: return BreakOffLowFraction;
                case BreakOffLevel.High: return BreakOffHighFraction;
                default: return BreakOffMidFraction;
            }
        }
    }

    /// <summary>
    /// How fast a squadron can open or close range: its combat acceleration.
    ///
    /// <para><b>Axes.</b> A hull's combat acceleration is its strongest single thrust direction
    /// (<see cref="IPrefabCatalogue.ThrustBands"/>) at its dry mass, in the environment of the fight.
    /// Every weapon the duel counts is a turret that fires in any attitude, so a hull turns its
    /// strongest face along the range axis whichever way it is moving; nothing ties it to its nose.
    /// Cargo mass is not counted.</para>
    ///
    /// <para><b>A squadron is as mobile as its slowest hull</b> — the same rule the physical fight
    /// imposes on a squadron that keeps together. An unscanned hull (dry mass 0) is unknown and does
    /// not count; a squadron with no scanned hull is unknown (<see cref="Unknown"/>). A scanned hull
    /// with no thrust is immobile. A defence with no ships is a station, <see cref="Station"/>.</para>
    ///
    /// <para>Computed from the prefab ids a fleet already carries whenever a duel or a gate asks —
    /// never persisted.</para></summary>
    public static class CombatMobility
    {
        public const double Station = 0.0;
        public const double Unknown = -1.0;

        /// <summary>One hull's combat acceleration in m/s², or <see cref="Unknown"/>.</summary>
        public static double OfHull(IPrefabCatalogue catalogue, string prefabId, double airDensity, bool inAtmosphere)
        {
            if (catalogue == null || string.IsNullOrEmpty(prefabId)) return Unknown;
            double dryKg = catalogue.DryMassKg(prefabId);
            if (dryKg <= 0) return Unknown;
            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = catalogue.ThrustBands(prefabId);
            double best = 0;
            if (bands != null)
                foreach (KeyValuePair<ThrustDirection, IReadOnlyList<ThrustBand>> dir in bands)
                {
                    double accel = ThrustPhysics.ForceN(dir.Value, airDensity, inAtmosphere) / dryKg;
                    if (accel > best) best = accel;
                }
            return best;
        }

        /// <summary>The slowest known hull of <paramref name="prefabIds"/>, or <see cref="Unknown"/>
        /// when none is known.</summary>
        public static double OfHulls(IPrefabCatalogue catalogue, IEnumerable<string> prefabIds,
            double airDensity, bool inAtmosphere)
        {
            double slowest = Unknown;
            if (prefabIds == null) return slowest;
            foreach (string id in prefabIds)
            {
                double m = OfHull(catalogue, id, airDensity, inAtmosphere);
                if (m < 0) continue;
                if (slowest < 0 || m < slowest) slowest = m;
            }
            return slowest;
        }

        /// <summary>A fleet's combat acceleration where it fights.</summary>
        public static double OfFleet(PendingFleetAction fleet, IPrefabCatalogue catalogue, double airDensity, bool inAtmosphere)
        {
            return OfHulls(catalogue, FleetMunitions.Hulls(fleet), airDensity, inAtmosphere);
        }

        /// <summary>A defence's combat acceleration: its ships' when it has any, <see cref="Station"/>
        /// when it has none. <paramref name="hasShips"/> distinguishes a defence of unscanned ships
        /// (unknown) from one of no ships at all.</summary>
        public static double OfDefence(IPrefabCatalogue catalogue, IEnumerable<string> shipPrefabIds, bool hasShips,
            double airDensity, bool inAtmosphere)
        {
            if (!hasShips) return Station;
            return OfHulls(catalogue, shipPrefabIds, airDensity, inAtmosphere);
        }

        /// <summary>Air where a fight at <paramref name="node"/> happens — the density that sets
        /// thrust effectiveness, and whether it is atmosphere at all; open space when there is no
        /// node.</summary>
        public static void EnvironmentAt(IPlanetCatalogue planets, SpatialNode node, out double airDensity, out bool inAtmosphere)
        {
            airDensity = 0;
            inAtmosphere = false;
            if (planets == null || node == null) return;
            airDensity = ThrustPhysics.AirDensity(planets, node.Position);
            inAtmosphere = ThrustPhysics.InAtmosphere(planets, node.Position);
        }
    }
}
