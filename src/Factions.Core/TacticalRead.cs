using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One range a squadron could fight at, and what each side lands there.</summary>
    public struct BracketPick
    {
        /// <summary>The duel bracket this pick stands for, metres (<see cref="FactionTuning.CombatRangeBracketsM"/>).</summary>
        public double BracketM;

        /// <summary>The separation to hold: inside every own band the bracket counts, and outside
        /// every enemy band it does not.</summary>
        public double HoldRangeM;

        /// <summary>The longest enemy band this pick keeps out of, metres; 0 when it keeps out of
        /// none. The hold's tolerance never reaches inside it.</summary>
        public double AvoidInsideM;

        /// <summary><see cref="CombatStrength.InRangeDps"/> of each side at <see cref="BracketM"/>.</summary>
        public double OwnDps;
        public double TheirDps;

        /// <summary>False when the enemy's weapons could not be read and were assumed to reach
        /// every bracket; <see cref="TheirDps"/> is then 0 and means nothing.</summary>
        public bool TheirsKnown;
    }

    /// <summary>What the lead flies for an order: the manoeuvre and its ranges.</summary>
    public struct CombatPlan
    {
        public ManoeuvreKind Kind;
        public TacticArm Arm;
        public BreakOffLevel BreakOff;
        public double HoldRangeM;
        public double InnerRangeM;
        public double RangeToleranceM;
        public double OrbitSpeedMs;

        /// <summary>The bracket the plan was sized from.</summary>
        public BracketPick Pick;
    }

    /// <summary>The tactical layer's read of a fight — which range to fight at and which manoeuvre
    /// flies it — from the two sides' <see cref="WeaponBand"/>s and the duel's own brackets, so a
    /// physical fight seeks the range the abstract duel would score it at. SE-free; the host calls
    /// it about once a second per fighting lead. See docs/fleet-tactics.md.</summary>
    public static class TacticalRead
    {
        /// <summary>Share of the shortest counted own band to hold at, so the guns reach with
        /// margin rather than at their limit.</summary>
        public const double HoldFraction = 0.85;

        /// <summary>No hold closer than this, whatever the bands say: two hulls holding a few tens
        /// of metres apart are a collision, not a range.</summary>
        public const double MinHoldRangeM = 150.0;

        /// <summary>The on-station band either side of the hold is this share of it, and at least
        /// <see cref="MinToleranceM"/>.</summary>
        public const double ToleranceFraction = 0.05;
        public const double MinToleranceM = 25.0;

        /// <summary>Tangential speed a Brawl's orbit asks for before its centripetal cap.</summary>
        public const double BrawlOrbitSpeedMs = 40.0;

        /// <summary>Whether <paramref name="fleet"/> fights where it holds: a Raid, Retaliate or
        /// Conquer on its dwell, or a Guard standing its duty window.</summary>
        public static bool FightsAtHold(PendingFleetAction fleet, DateTime utcNow, FactionTuning tuning)
        {
            if (tuning == null || !fleet.HoldsStation(utcNow, tuning)) return false;
            return PendingFleetResolver.EngagesOnArrival(fleet.Kind) || fleet.IsActiveGuard(utcNow, tuning);
        }

        /// <summary>The range to fight at for bracket <paramref name="bracketM"/>. The own bands it
        /// counts are those reaching the bracket (all of them at 0); the hold sits at
        /// <see cref="HoldFraction"/> of the shortest of those, but never inside the midpoint between
        /// it and the longest enemy band that does not reach the bracket, and never under
        /// <see cref="MinHoldRangeM"/>. <paramref name="theirs"/> null means unknown.</summary>
        public static BracketPick PickAt(IReadOnlyList<WeaponBand> own, IReadOnlyList<WeaponBand> theirs, double bracketM)
        {
            var pick = new BracketPick
            {
                BracketM = bracketM,
                OwnDps = CombatStrength.InRangeDps(own, bracketM),
                TheirsKnown = theirs != null,
                TheirDps = theirs != null ? CombatStrength.InRangeDps(theirs, bracketM) : 0,
            };

            double reach = double.PositiveInfinity;
            if (own != null)
                for (int i = 0; i < own.Count; i++)
                    if (own[i].Dps > 0 && (bracketM <= 0 || own[i].RangeM >= bracketM) && own[i].RangeM < reach)
                        reach = own[i].RangeM;

            double avoid = 0;
            if (theirs != null && bracketM > 0)
                for (int i = 0; i < theirs.Count; i++)
                    if (theirs[i].Dps > 0 && theirs[i].RangeM < bracketM && theirs[i].RangeM > avoid)
                        avoid = theirs[i].RangeM;
            pick.AvoidInsideM = avoid;

            double hold;
            if (double.IsPositiveInfinity(reach))
            {
                // Nothing of ours reaches: keep the bracket's own distance.
                hold = bracketM;
            }
            else
            {
                hold = reach * HoldFraction;
                double mid = (avoid + reach) * 0.5;
                if (hold < mid) hold = mid;
                if (hold > reach) hold = reach;
            }
            if (hold < MinHoldRangeM) hold = MinHoldRangeM;
            pick.HoldRangeM = hold;
            return pick;
        }

        /// <summary>The bracket at which our in-range DPS over theirs is best, from
        /// <paramref name="bracketsM"/> (the duel's <see cref="FactionTuning.CombatRangeBracketsM"/>).
        /// A bracket where they land nothing and we land something beats every other; among equals
        /// the one where we land more wins, then the longer one. Unknown enemy weapons are assumed to
        /// reach every bracket equally, which ranks the brackets by our own output. A side with no
        /// weapon reaching any bracket holds at the longest.</summary>
        public static BracketPick PickStandoff(IReadOnlyList<WeaponBand> own, IReadOnlyList<WeaponBand> theirs,
                                               IReadOnlyList<double> bracketsM)
        {
            bool found = false;
            BracketPick best = new BracketPick();
            double bestScore = 0;
            double longest = 0;

            int n = bracketsM != null && bracketsM.Count > 0 ? bracketsM.Count : 1;
            for (int i = 0; i < n; i++)
            {
                double b = bracketsM != null && bracketsM.Count > 0 ? bracketsM[i] : 0;
                if (b > longest) longest = b;

                BracketPick p = PickAt(own, theirs, b);
                if (p.OwnDps <= 0) continue;

                double score = theirs == null ? p.OwnDps
                             : p.TheirDps <= 0 ? double.PositiveInfinity
                             : p.OwnDps / p.TheirDps;

                if (!found || Better(score, p, bestScore, best))
                {
                    found = true;
                    best = p;
                    bestScore = score;
                }
            }

            return found ? best : PickAt(null, theirs, longest);
        }

        private static bool Better(double score, BracketPick p, double bestScore, BracketPick best)
        {
            const double eps = 1e-9;
            bool bothInf = double.IsPositiveInfinity(score) && double.IsPositiveInfinity(bestScore);
            if (!bothInf)
            {
                if (score > bestScore + eps) return true;
                if (score < bestScore - eps) return false;
            }
            if (p.OwnDps > best.OwnDps + eps) return true;
            if (p.OwnDps < best.OwnDps - eps) return false;
            return p.BracketM > best.BracketM;
        }

        /// <summary>The manoeuvre and ranges that fly <paramref name="order"/>'s arm. Standoff holds
        /// the best bracket; Kite holds it without ever closing; Brawl orbits at the point-blank
        /// pick; AttackRun runs in to the point-blank pick and back out to the best bracket.</summary>
        public static CombatPlan Plan(TacticOrder order, IReadOnlyList<WeaponBand> own,
                                      IReadOnlyList<WeaponBand> theirs, IReadOnlyList<double> bracketsM)
        {
            BracketPick standoff = PickStandoff(own, theirs, bracketsM);
            var plan = new CombatPlan { Arm = order.Arm, BreakOff = order.BreakOff };

            switch (order.Arm)
            {
                case TacticArm.Brawl:
                {
                    BracketPick close = PickAt(own, theirs, 0);
                    plan.Kind = ManoeuvreKind.Orbit;
                    plan.Pick = close;
                    plan.HoldRangeM = close.HoldRangeM;
                    plan.OrbitSpeedMs = BrawlOrbitSpeedMs;
                    break;
                }
                case TacticArm.Kite:
                    plan.Kind = ManoeuvreKind.Kite;
                    plan.Pick = standoff;
                    plan.HoldRangeM = standoff.HoldRangeM;
                    break;
                case TacticArm.AttackRun:
                {
                    BracketPick close = PickAt(own, theirs, 0);
                    plan.Kind = ManoeuvreKind.AttackRun;
                    plan.Pick = standoff;
                    plan.HoldRangeM = standoff.HoldRangeM;
                    plan.InnerRangeM = close.HoldRangeM < standoff.HoldRangeM
                        ? close.HoldRangeM
                        : Math.Max(MinHoldRangeM, standoff.HoldRangeM * 0.5);
                    break;
                }
                case TacticArm.Standoff:
                default:
                    plan.Kind = ManoeuvreKind.HoldAtRange;
                    plan.Pick = standoff;
                    plan.HoldRangeM = standoff.HoldRangeM;
                    break;
            }

            plan.RangeToleranceM = Tolerance(plan.HoldRangeM, plan.Pick.AvoidInsideM);
            return plan;
        }

        /// <summary>The plan for shadowing a ship rather than fighting it: hold
        /// <paramref name="standoffM"/> from it, whatever either side carries. Standoff arm, so the
        /// break-off and the escorts' fan read it like any hold.</summary>
        public static CombatPlan ShadowPlan(double standoffM)
        {
            double hold = standoffM > MinHoldRangeM ? standoffM : MinHoldRangeM;
            var plan = new CombatPlan
            {
                Kind = ManoeuvreKind.HoldAtRange,
                Arm = TacticArm.Standoff,
                HoldRangeM = hold,
                Pick = new BracketPick { HoldRangeM = hold, BracketM = hold },
            };
            plan.RangeToleranceM = Tolerance(hold, 0);
            return plan;
        }

        /// <summary>The on-station band for a hold: <see cref="ToleranceFraction"/> of it, at least
        /// <see cref="MinToleranceM"/>, but never so wide that its inner edge reaches halfway to the
        /// enemy band the hold keeps out of, nor a quarter of the hold itself.</summary>
        public static double Tolerance(double holdM, double avoidInsideM)
        {
            double tol = holdM * ToleranceFraction;
            if (tol < MinToleranceM) tol = MinToleranceM;
            if (avoidInsideM > 0 && holdM > avoidInsideM)
            {
                double room = (holdM - avoidInsideM) * 0.5;
                if (tol > room) tol = room;
            }
            if (tol > holdM * 0.25) tol = holdM * 0.25;
            return tol;
        }
    }
}
