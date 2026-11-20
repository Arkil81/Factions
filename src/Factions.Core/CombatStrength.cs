using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// How strong a side of a fight is, in the units the fight is actually resolved in.
    ///
    /// <para><b>Why this is a type rather than an expression at each call site.</b>
    /// <c>PendingFleetResolver.ResolveShipDuel</c> gives each side an effective HP equal to its total
    /// weapon DPS — <c>defenderMax = TotalDps(bands) + flat</c> — so "can this defence hold" has an
    /// exact answer and it is the duel's own arithmetic. A dispatch gate that reimplemented it would
    /// be measuring something adjacent to what the duel later computes, and the two would drift the
    /// first time either changed. <see cref="FleetManager"/> asks before committing a hull and
    /// <c>ResolvePatrolVsRaid</c> asks when the hulls meet; both read the station term from here.</para>
    ///
    /// <para><b>Totals and brackets answer different questions, and a commit needs both.</b> The duel
    /// is range-bracketed (<see cref="InRangeDps"/> per turn), so reach decides fights that a DPS
    /// total calls even: a side firing in a bracket the other cannot reach takes a whole turn of free
    /// damage. <see cref="Holds"/> compares totals and is the cheap "is this exchange worth taking"
    /// filter; <see cref="Destroys"/> and <see cref="Survives"/> run the duel's own bracketed
    /// arithmetic and are what a commit turns on. A gate resting on totals alone refuses fights reach
    /// would have won — measured at 58 declined defences that each carried a 2000 m bracket the
    /// attacker could not answer.</para>
    /// </summary>
    public static class CombatStrength
    {
        /// <summary>The flat, non-range-gated defence a station-backed node contributes to a duel at
        /// it, or 0 for a node with no station. The single reading of that rule: the duel applies it
        /// every turn on top of the bands, and the gate counts it before deciding to send anyone.</summary>
        public static long StationFlat(SpatialNode node, FactionTuning tuning)
        {
            if (node == null || tuning == null) return 0;
            return node.StationPrefabId != null ? tuning.StationDefensePower : 0;
        }

        /// <summary>What the node itself brings without any fleet: its turrets plus
        /// <see cref="StationFlat"/>. A bare claim brings nothing.</summary>
        public static double OfStation(SpatialNode node, FactionTuning tuning)
        {
            if (node == null) return 0;
            return ShipPool.TotalDps(node.StationWeapons) + StationFlat(node, tuning);
        }

        /// <summary>Everything defending <paramref name="node"/> if <paramref name="convoyBands"/>
        /// stands on it — the defender's effective HP in the duel.</summary>
        public static double OfDefence(IReadOnlyList<WeaponBand> convoyBands, SpatialNode node, FactionTuning tuning)
        {
            return ShipPool.TotalDps(convoyBands) + OfStation(node, tuning);
        }

        /// <summary>The defence at a node for a fleet that does not necessarily hold it — the shape a
        /// diverting Patrol needs, where <see cref="OfDefence"/>'s unconditional station term would be
        /// wrong.
        ///
        /// <para>A Guard is sent to ground its faction holds or a pact partner holds, so the station
        /// there fires alongside it. A Patrol diverting onto a hostile parked somewhere else has no
        /// such guarantee, and a station it does not own is as likely to be shooting at it. The
        /// station counts only when <paramref name="defenderTag"/> owns the node — deliberately the
        /// conservative reading, since under-counting refuses a fight and over-counting loses
        /// one.</para></summary>
        public static double OfDefenceAt(IReadOnlyList<WeaponBand> convoyBands, SpatialNode node,
            FactionTuning tuning, string defenderTag)
        {
            double own = ShipPool.TotalDps(convoyBands);
            if (node == null || defenderTag == null) return own;
            return node.OwnerFactionTag == defenderTag ? own + OfStation(node, tuning) : own;
        }

        /// <summary>Everything that can stand at <paramref name="node"/> against an attack on it — its
        /// turrets and flat floor plus the combat-suitable hulls parked there that its defence can
        /// actually sail (<see cref="FieldableParkedStock"/>). The defender's effective HP in the
        /// duel, read from the node rather than from a fleet, which is the shape an <em>attacker</em>
        /// sizing a dispatch needs: <see cref="OfDefence"/> asks what a convoy it already holds is
        /// worth, and an attacker holds none of the defender's.
        ///
        /// <para>Hulls only: a Guard already committed to the node has left its pool and is invisible
        /// here. Pass its bands to the overload below to count it, and see
        /// <see cref="FactionTuning.RaidOverwhelmMargin"/> for the slack that covers a Guard
        /// dispatched after the raid is already in the air.</para></summary>
        public static double OfNodeDefence(SpatialNode node, IPrefabCatalogue catalogue, FactionTuning tuning)
        {
            if (node == null) return 0;
            return OfStation(node, tuning) + ShipPool.TotalDps(ParkedDefenceBands(node, catalogue, tuning));
        }

        /// <summary>The same census plus <paramref name="standingDefence"/> — the bands of a Guard
        /// already inbound to or standing on the node.
        ///
        /// <para><b>Dispatch removes a hull from <see cref="SpatialNode.ShipStock"/>.</b> A garrison
        /// that has sailed is therefore absent from the parked census while being exactly what an
        /// arriving raid meets, so an attacker reading stock alone under-counts a defended node for
        /// as long as its Guard's duty window lasts.</para></summary>
        public static double OfNodeDefence(SpatialNode node, IPrefabCatalogue catalogue, FactionTuning tuning,
            IReadOnlyList<WeaponBand> standingDefence)
        {
            return OfNodeDefence(node, catalogue, tuning) + ShipPool.TotalDps(standingDefence);
        }

        /// <summary>How many convoys the defensive marshal sends against one threat. One of the two
        /// numbers that bound what a node's defence can put in the air; the marshal reads it to stop
        /// and the census reads it to price, so an attacker never weighs a defence the marshal could
        /// not deliver.</summary>
        public static int GuardMaxWaves(FactionTuning tuning)
        {
            return tuning == null ? 1 : Math.Max(1, tuning.GuardMaxConvoysPerThreat);
        }

        /// <summary>The hulls one marshalled wave is asked for at least: the Guard burst size scaled
        /// by <paramref name="posture"/> up to <see cref="FactionTuning.MusterConvoyScaleMax"/>.
        /// What a defender likes to send, knowing nothing about what is coming.</summary>
        public static int GuardWaveMinHulls(FactionTuning tuning, double posture)
        {
            if (tuning == null) return 1;
            int baseConvoySize = tuning.BurstSizeFor(ActionKind.Guard);
            double signal = Math.Max(0.0, Math.Min(1.0, posture));
            double scale = 1.0 + signal * (tuning.MusterConvoyScaleMax - 1.0);
            return Math.Max(baseConvoySize, (int)Math.Round(baseConvoySize * scale));
        }

        /// <summary>The most hulls one marshalled wave can carry — the other bound on what a node's
        /// defence can put in the air.
        ///
        /// <para><b>As many as one raid squadron.</b> A wave is a squadron of the same shape, so it is
        /// bounded by <see cref="FactionTuning.RaidSquadronMaxHulls"/> like the raid it answers.
        /// Bounded by the old fighter roster (<see cref="FactionTuning.RaidSquadronMaxFighters"/> + 1)
        /// alone, three waves fielded 15 hulls against a 24-hull raid and a node holding 144
        /// warships refused at 98% of the bar (FAC-452). The roster stays the bound when the raid
        /// top-up is off (<c>RaidSquadronMaxHulls &lt;= 0</c>).</para></summary>
        public static int GuardWaveMaxHulls(FactionTuning tuning, double posture)
        {
            if (tuning == null) return 1;
            int squadron = Math.Max(tuning.RaidSquadronMaxFighters + 1, tuning.RaidSquadronMaxHulls);
            return Math.Max(GuardWaveMinHulls(tuning, posture), squadron);
        }

        /// <summary>The most parked hulls a node's defence can sail against one attack:
        /// <see cref="GuardMaxWaves"/> waves of <see cref="GuardWaveMaxHulls"/> at full posture. Taken
        /// at full posture because the attacker cannot know the defender's, and a posture that rises
        /// between dispatch and contact raises the wave with it. Unbounded without tuning.</summary>
        public static int FieldableHullCap(FactionTuning tuning)
        {
            if (tuning == null) return int.MaxValue;
            long cap = (long)GuardMaxWaves(tuning) * GuardWaveMaxHulls(tuning, 1.0);
            return cap > int.MaxValue ? int.MaxValue : (int)cap;
        }

        /// <summary>The attacking fleet's effective HP, when it is knowable.
        ///
        /// <para>Returns <b>false</b> for a threat carrying no weapon bands, which is not the same as
        /// zero and must not be read as a harmless attacker. Two real cases reach it: a player siege,
        /// synthesised by <c>FleetManager.PlayerAttackThreat</c> with no fleet behind it at all, and
        /// any fleet dispatched before its bands were recorded. An unmeasurable attacker is answered
        /// rather than weighed — a defender that declined to sail because it could not see the enemy
        /// would leave a player's target undefended every time.</para></summary>
        public static bool TryOfAttack(PendingFleetAction threat, out double dps)
        {
            dps = 0;
            if (threat.WeaponBands == null || threat.WeaponBands.Count == 0) return false;
            dps = ShipPool.TotalDps(threat.WeaponBands);
            return dps > 0;
        }

        /// <summary>Whether a defence of <paramref name="defenceDps"/> is worth committing against an
        /// attack of <paramref name="attackDps"/>. <paramref name="ratio"/> at or below zero disables
        /// the gate, which is how the tuning restores the old always-send behaviour.</summary>
        public static bool Holds(double defenceDps, double attackDps, double ratio)
        {
            if (ratio <= 0) return true;
            return defenceDps >= attackDps * ratio;
        }

        /// <summary>The part of <paramref name="bands"/> that reaches a duel turn fought at
        /// <paramref name="bracketM"/> — zero range meaning point blank, where everything reaches.
        /// The single reading of the range rule: <c>ResolveShipDuel</c> asks it once per turn, and
        /// <see cref="DuelDamage"/> sums it across the turns that duel will fight.</summary>
        public static double InRangeDps(IReadOnlyList<WeaponBand> bands, double bracketM)
        {
            if (bands == null) return 0;
            double sum = 0;
            for (int i = 0; i < bands.Count; i++)
                if (bracketM <= 0 || bands[i].RangeM >= bracketM) sum += bands[i].Dps;
            return sum;
        }

        /// <summary>One turn of a duel, named by the range it is fought at and what each side can
        /// land there. <see cref="CombatStrength.WorstBracket"/> answers with the turn that decided
        /// the fight.</summary>
        public struct DuelBracket
        {
            /// <summary>The bracket's range in metres; 0 is point blank, where every weapon reaches.</summary>
            public double RangeM;
            /// <summary>The attacker's output at this range — the part of its loadout that reaches.</summary>
            public double AttackerDps;
            /// <summary>The defence's output at this range, station turrets and flat floor included.</summary>
            public double DefenderDps;
            /// <summary>No turn was examined, or neither side could act at any of them.</summary>
            public bool IsNone { get { return AttackerDps <= 0 && DefenderDps <= 0; } }
        }

        /// <summary>The duel turn the fight turns on: the bracket at which the defence most
        /// out-reaches the attack. Reported as DPS rather than as the rolled damage a duel logs, so
        /// two readings of the same pairing agree and a refusal — which never rolls anything — can
        /// answer in the same units a lost fight does.
        ///
        /// <para>A DPS total says how badly outgunned a squadron is; this says <em>where</em>, and
        /// only the second names the hulls that would change it. The gap is measured, not the ratio:
        /// a bracket neither side can reach is even at zero and never wins the comparison.</para></summary>
        public static DuelBracket WorstBracket(IReadOnlyList<WeaponBand> attackerBands,
            IReadOnlyList<WeaponBand> defenceBands, IReadOnlyList<WeaponBand> stationBands,
            long defenderFlat, FactionTuning tuning)
        {
            if (tuning == null) return new DuelBracket();
            return WorstOf(RangeSchedule.FixedOrder(tuning), attackerBands, defenceBands, stationBands, defenderFlat);
        }

        private static DuelBracket WorstOf(double[] brackets, IReadOnlyList<WeaponBand> attackerBands,
            IReadOnlyList<WeaponBand> defenceBands, IReadOnlyList<WeaponBand> stationBands, long defenderFlat)
        {
            var worst = new DuelBracket();
            double worstGap = double.NegativeInfinity;
            for (int t = 0; t < brackets.Length; t++)
            {
                double bracket = brackets[t];
                double attack = InRangeDps(attackerBands, bracket);
                double defence = InRangeDps(defenceBands, bracket) + InRangeDps(stationBands, bracket) + defenderFlat;
                double gap = defence - attack;
                if (gap <= worstGap) continue;
                worstGap = gap;
                worst.RangeM = bracket;
                worst.AttackerDps = attack;
                worst.DefenderDps = defence;
            }
            return worst;
        }

        /// <summary><see cref="WorstBracket"/> for a defence standing at <paramref name="node"/>,
        /// taking the same arguments <see cref="Survives"/> does so a gate can report in the units it
        /// refused in: <paramref name="defenceBands"/> excludes the node's own turrets, which are
        /// counted here.</summary>
        public static DuelBracket WorstBracketAt(IReadOnlyList<WeaponBand> attackerBands,
            IReadOnlyList<WeaponBand> defenceBands, SpatialNode node, FactionTuning tuning)
        {
            return WorstBracket(attackerBands, defenceBands,
                                node != null ? node.StationWeapons : null, StationFlat(node, tuning), tuning);
        }

        /// <summary><see cref="WorstBracketAt(IReadOnlyList{WeaponBand},IReadOnlyList{WeaponBand},SpatialNode,FactionTuning)"/>
        /// over the brackets the duel would actually fight: the contested schedule when a
        /// <paramref name="matchup"/> is given, the fixed order otherwise.</summary>
        public static DuelBracket WorstBracketAt(IReadOnlyList<WeaponBand> attackerBands,
            IReadOnlyList<WeaponBand> defenceBands, SpatialNode node, FactionTuning tuning,
            DuelMatchup matchup)
        {
            if (matchup == null || tuning == null)
                return WorstBracketAt(attackerBands, defenceBands, node, tuning);
            IReadOnlyList<WeaponBand> stationBands = node != null ? node.StationWeapons : null;
            long flat = StationFlat(node, tuning);
            DuelTurn[] plan = RangeSchedule.Plan(attackerBands, Merge(defenceBands, stationBands), flat,
                                                 matchup.Attacker, matchup.Defender, tuning);
            var brackets = new double[plan.Length];
            for (int t = 0; t < plan.Length; t++) brackets[t] = plan[t].RangeM;
            return WorstOf(brackets, attackerBands, defenceBands, stationBands, flat);
        }

        /// <summary>The damage a defence of <paramref name="convoyBands"/> standing at
        /// <paramref name="node"/> deals over a whole duel — the same per-turn, range-bracketed
        /// arithmetic <c>ResolveShipDuel</c> runs, with the jitter at its mean and the node's own
        /// turrets and flat floor counted in.
        ///
        /// <para><b>This is the figure that separates destroying an attacker from surviving it.</b>
        /// An attacker's effective HP is its own total DPS (<see cref="TryOfAttack"/>), so it dies
        /// exactly when this reaches <see cref="DamageToDestroy"/>. <see cref="Holds"/> compares DPS
        /// totals and answers a different question — whether the exchange is worth taking at all.</para>
        ///
        /// <para>Optimistic in one known way: the duel stops a side's output the turn it is
        /// destroyed, and this sums every turn. The bias is the same one <see cref="Holds"/> already
        /// carries and points the same way — toward committing.</para></summary>
        public static double DuelDamage(IReadOnlyList<WeaponBand> convoyBands, SpatialNode node, FactionTuning tuning)
        {
            if (tuning == null) return 0;
            int turns = tuning.CombatTurns > 0 ? tuning.CombatTurns : 1;
            double[] brackets = (tuning.CombatRangeBracketsM != null && tuning.CombatRangeBracketsM.Length == turns)
                ? tuning.CombatRangeBracketsM : new double[turns];
            IReadOnlyList<WeaponBand> stationBands = node != null ? node.StationWeapons : null;
            long flat = StationFlat(node, tuning);
            double total = 0;
            for (int t = 0; t < turns; t++)
            {
                double bracket = brackets[t];
                total += (InRangeDps(convoyBands, bracket) + InRangeDps(stationBands, bracket) + flat)
                         * tuning.CombatBaseDamageFraction;
            }
            return total;
        }

        /// <summary><see cref="DuelDamage(IReadOnlyList{WeaponBand},SpatialNode,FactionTuning)"/>
        /// predicted with both sides' tactics: summed over the schedule <see cref="RangeSchedule.Plan"/>
        /// builds for <paramref name="matchup"/> — the same schedule the duel fights, since it reads
        /// only bands, arms and mobility. Without a matchup it is the fixed-order figure exactly.
        ///
        /// <para>Optimistic in the same way the fixed-order figure is, and one more: it sums every
        /// turn, while the duel stops at the end of a turn in which either side breaks off.</para></summary>
        public static double DuelDamage(IReadOnlyList<WeaponBand> convoyBands, SpatialNode node, FactionTuning tuning,
            DuelMatchup matchup)
        {
            if (matchup == null || tuning == null) return DuelDamage(convoyBands, node, tuning);
            double[] perTurn = DuelTurnDamage(convoyBands, node, tuning, matchup);
            double total = 0;
            for (int t = 0; t < perTurn.Length; t++) total += perTurn[t];
            return total;
        }

        /// <summary>The damage the defence deals on each turn of the contested schedule, jitter at its
        /// mean — what <see cref="DuelDamage(IReadOnlyList{WeaponBand},SpatialNode,FactionTuning,DuelMatchup)"/>
        /// sums, kept per turn so the raid gate can tell a raid destroyed outright from one that leaves
        /// first.</summary>
        public static double[] DuelTurnDamage(IReadOnlyList<WeaponBand> convoyBands, SpatialNode node,
            FactionTuning tuning, DuelMatchup matchup)
        {
            IReadOnlyList<WeaponBand> stationBands = node != null ? node.StationWeapons : null;
            long flat = StationFlat(node, tuning);
            List<WeaponBand> defence = Merge(convoyBands, stationBands);
            DuelTurn[] plan = RangeSchedule.Plan(matchup.AttackerBands, defence, flat,
                                                 matchup.Attacker, matchup.Defender, tuning);
            return ScheduleTurnDamage(defence, flat, plan, tuning);
        }

        /// <summary>The damage <paramref name="bands"/> (plus an always-in-range
        /// <paramref name="flat"/>) deal on each turn of <paramref name="plan"/>, the jitter at its
        /// mean — the arithmetic both commit gates predict with, for either side.</summary>
        public static double[] ScheduleTurnDamage(IReadOnlyList<WeaponBand> bands, long flat, DuelTurn[] plan, FactionTuning tuning)
        {
            if (plan == null || tuning == null) return new double[0];
            var perTurn = new double[plan.Length];
            for (int t = 0; t < plan.Length; t++)
                perTurn[t] = (InRangeDps(bands, plan[t].RangeM) + flat) * tuning.CombatBaseDamageFraction;
            return perTurn;
        }

        /// <summary><see cref="ScheduleTurnDamage"/> summed over the whole schedule.</summary>
        public static double ScheduleDamage(IReadOnlyList<WeaponBand> bands, long flat, DuelTurn[] plan, FactionTuning tuning)
        {
            double total = 0;
            foreach (double d in ScheduleTurnDamage(bands, flat, plan, tuning)) total += d;
            return total;
        }

        /// <summary>The share of an attacker's effective HP the defence must take for its attack to
        /// fail: all of it, or — when the attacker can leave — enough to push it below its
        /// break-off fraction, at which point it goes home without its mission.</summary>
        public static double DefeatShare(DuelMatchup matchup)
        {
            if (matchup == null || !matchup.Attacker.CanBreakOff) return 1.0;
            return 1.0 - TacticChoice.BreakOffFraction(matchup.Attacker.BreakOff);
        }

        private static List<WeaponBand> Merge(IReadOnlyList<WeaponBand> a, IReadOnlyList<WeaponBand> b)
        {
            var merged = new List<WeaponBand>();
            if (a != null) merged.AddRange(a);
            if (b != null) merged.AddRange(b);
            return merged;
        }

        /// <summary>The duel damage that destroys an attacker of <paramref name="attackDps"/>.
        /// <paramref name="margin"/> buys the difference between killing it on the average roll and
        /// killing it on a bad one: each turn's damage is scaled by a jitter in
        /// 1 ± <see cref="FactionTuning.CombatVariance"/>, so at variance 0.2 a defence that rolls
        /// low throughout delivers 0.8 of <see cref="DuelDamage"/>. At or below zero it is parity —
        /// destruction on the mean roll and a coin flip in practice.</summary>
        public static double DamageToDestroy(double attackDps, double margin)
        {
            return attackDps * (margin > 0 ? margin : 1.0);
        }

        /// <summary>An opening estimate of the convoy DPS that covers <paramref name="damageGap"/>
        /// of duel damage — <see cref="DuelDamage"/> inverted for a hull that reaches every turn.
        ///
        /// <para>Optimistic on purpose, and only ever an opening bid: a hull whose weapons miss the
        /// long brackets deals less than this credits it with, so the caller checks the assembled
        /// squadron against <see cref="Destroys"/> and tops it up. Asking for the pessimistic figure
        /// instead would fill every convoy to its cap against an attacker two hulls could have
        /// killed.</para></summary>
        public static double DpsForDamage(double damageGap, FactionTuning tuning)
        {
            if (damageGap <= 0 || tuning == null || tuning.CombatBaseDamageFraction <= 0) return 0;
            int turns = tuning.CombatTurns > 0 ? tuning.CombatTurns : 1;
            return damageGap / (tuning.CombatBaseDamageFraction * turns);
        }

        /// <summary>Whether a defence of <paramref name="convoyBands"/> at <paramref name="node"/>
        /// kills an attacker of <paramref name="attackDps"/> outright rather than merely outlasting
        /// it. A repelled raider repairs and comes back; a destroyed one does not.</summary>
        public static bool Destroys(IReadOnlyList<WeaponBand> convoyBands, SpatialNode node,
            FactionTuning tuning, double attackDps, double margin)
        {
            return DuelDamage(convoyBands, node, tuning) >= DamageToDestroy(attackDps, margin);
        }

        /// <summary><see cref="Destroys(IReadOnlyList{WeaponBand},SpatialNode,FactionTuning,double,double)"/>
        /// predicted with both sides' tactics. "Destroys" reads as <em>defeats</em>: the defence deals
        /// the attacker's whole effective HP or enough to drive it below its break-off fraction
        /// (<see cref="DefeatShare"/>) — either way its attack fails. Without a matchup it is the
        /// fixed-order answer exactly.</summary>
        public static bool Destroys(IReadOnlyList<WeaponBand> convoyBands, SpatialNode node,
            FactionTuning tuning, double attackDps, double margin, DuelMatchup matchup)
        {
            if (matchup == null) return Destroys(convoyBands, node, tuning, attackDps, margin);
            return DuelDamage(convoyBands, node, tuning, matchup)
                   >= DamageToDestroy(attackDps * DefeatShare(matchup), margin);
        }

        /// <summary><see cref="Survives(IReadOnlyList{WeaponBand},SpatialNode,FactionTuning,double,double)"/>
        /// predicted with both sides' tactics: the attacker is not destroyed before it can break off.
        ///
        /// <para>A raid that would only break off still commits — it goes home with its hulls, and that
        /// is a risk its faction accepts (operator, 2026-09-27). So the gate walks the contested
        /// schedule turn by turn against the attacker's effective HP scaled by
        /// <paramref name="margin"/>, as <see cref="DamageToDestroy"/> scales it: a turn that ends
        /// below the break-off fraction with HP left is a survival, a turn that ends at zero first is
        /// the refusal. <see cref="Destroys(IReadOnlyList{WeaponBand},SpatialNode,FactionTuning,double,double,DuelMatchup)"/>
        /// keeps counting a break-off as a defeat: that is the defender's question, and for a defender
        /// a raider that leaves has been stopped.</para>
        ///
        /// <para>Optimistic in the way the fixed-order figure is: the defence fires every turn even if
        /// the attacker would have destroyed it or driven it off first.</para></summary>
        public static bool Survives(IReadOnlyList<WeaponBand> defenceBands, SpatialNode node,
            FactionTuning tuning, double attackDps, double margin, DuelMatchup matchup)
        {
            if (attackDps <= 0) return false;
            if (matchup == null || tuning == null)
                return Survives(defenceBands, node, tuning, attackDps, margin);
            double hp = DamageToDestroy(attackDps, margin);
            double leaveBelow = matchup.Attacker.CanBreakOff
                ? hp * TacticChoice.BreakOffFraction(matchup.Attacker.BreakOff) : 0;
            double[] perTurn = DuelTurnDamage(defenceBands, node, tuning, matchup);
            for (int t = 0; t < perTurn.Length; t++)
            {
                hp -= perTurn[t];
                if (hp <= 0) return false;
                if (hp < leaveBelow) return true;
            }
            return true;
        }

        /// <summary>The parked hulls at <paramref name="node"/> that its defence can actually field, as
        /// (prefab, count): every available combat-suitable hull, strongest first (ties by prefab id),
        /// up to <see cref="FieldableHullCap"/>.
        ///
        /// <para><b>Parked stock only fights by being sailed.</b> The duel at a node merges the Guards
        /// standing there with the station; a hull sitting in <see cref="SpatialNode.ShipStock"/> is in
        /// neither until the marshal sends it, and the marshal sends at most
        /// <see cref="GuardMaxWaves"/> waves of <see cref="GuardWaveMaxHulls"/>. Counting the whole
        /// stockpile would make any depot un-raidable: 416 parked 2,500-Dps hulls read as 1,040,000
        /// against the 180,000 of them three 24-hull waves can sail. Strongest first, because the marshal
        /// draws strongest first and the attacker must price the best the defence can send.</para>
        ///
        /// <para>The single census walk: <see cref="ParkedDefenceBands"/>, <see cref="OfNodeDefence"/>
        /// and the region census all read it, so no consumer can see a different defence.</para></summary>
        public static List<KeyValuePair<string, int>> FieldableParkedStock(SpatialNode node,
            IPrefabCatalogue catalogue, FactionTuning tuning)
        {
            var stock = new List<KeyValuePair<string, int>>();
            if (node == null || catalogue == null) return stock;
            var dpsOf = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kv in node.ShipStock)
            {
                if (!ShipPool.IsSuitableFor(ActionKind.Guard, catalogue, kv.Key)) continue;
                int available = ShipPool.Available(node, kv.Key);
                if (available <= 0) continue;
                stock.Add(new KeyValuePair<string, int>(kv.Key, available));
                dpsOf[kv.Key] = ShipPool.TotalDps(catalogue.WeaponBands(kv.Key));
            }
            stock.Sort((a, b) =>
            {
                int c = dpsOf[b.Key].CompareTo(dpsOf[a.Key]);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            int left = FieldableHullCap(tuning);
            var fielded = new List<KeyValuePair<string, int>>();
            for (int i = 0; i < stock.Count && left > 0; i++)
            {
                int take = Math.Min(stock[i].Value, left);
                fielded.Add(new KeyValuePair<string, int>(stock[i].Key, take));
                left -= take;
            }
            return fielded;
        }

        /// <summary>The weapon bands of <see cref="FieldableParkedStock"/> — what
        /// <see cref="OfNodeDefence"/> totals, kept as bands so the range brackets survive. Summing
        /// this reproduces that figure less the station, which is what stops the scalar reading and
        /// the bracketed one disagreeing about which hulls are present.</summary>
        public static List<WeaponBand> ParkedDefenceBands(SpatialNode node, IPrefabCatalogue catalogue,
            FactionTuning tuning)
        {
            var bands = new List<WeaponBand>();
            List<KeyValuePair<string, int>> fielded = FieldableParkedStock(node, catalogue, tuning);
            for (int i = 0; i < fielded.Count; i++)
                bands.AddRange(ShipPool.ConvoyWeaponBands(catalogue, fielded[i].Key, fielded[i].Value));
            return bands;
        }

        /// <summary>The bands of everything that meets an attack on <paramref name="node"/>: the
        /// parked hulls its defence can field plus <paramref name="standingDefence"/>, the Guard
        /// already committed to it. The bracketed counterpart of the
        /// <see cref="OfNodeDefence(SpatialNode,IPrefabCatalogue,FactionTuning,IReadOnlyList{WeaponBand})"/>
        /// total, and summing it reproduces that figure less the station's flat floor.</summary>
        public static List<WeaponBand> NodeDefenceBands(SpatialNode node, IPrefabCatalogue catalogue,
            FactionTuning tuning, IReadOnlyList<WeaponBand> standingDefence)
        {
            List<WeaponBand> bands = ParkedDefenceBands(node, catalogue, tuning);
            if (standingDefence != null) bands.AddRange(standingDefence);
            return bands;
        }

        /// <summary>Whether an attacker of <paramref name="attackDps"/> survives the duel a defence of
        /// <paramref name="defenceBands"/> at <paramref name="node"/> fights — the attacker's side of
        /// <see cref="Destroys"/>, asked with the same bracketed arithmetic.
        ///
        /// <para>The margin works the same way it does for the defender: it is the bad roll the
        /// attacker is refusing to be caught by, so a larger one commits less readily.</para></summary>
        public static bool Survives(IReadOnlyList<WeaponBand> defenceBands, SpatialNode node,
            FactionTuning tuning, double attackDps, double margin)
        {
            if (attackDps <= 0) return false;
            return !Destroys(defenceBands, node, tuning, attackDps, margin);
        }

        /// <summary>The ship DPS still missing before <paramref name="node"/> can hold against
        /// <paramref name="attackDps"/> — what the station cannot cover on its own. Zero when the
        /// station already clears the bar, which is the case a defender is still sent for (it buys the
        /// duty window) but no build is owed for.</summary>
        public static double ShortfallFromShips(double attackDps, SpatialNode node, FactionTuning tuning, double ratio)
        {
            double required = attackDps * (ratio > 0 ? ratio : 1.0);
            double gap = required - OfStation(node, tuning);
            return gap > 0 ? gap : 0;
        }
    }
}
