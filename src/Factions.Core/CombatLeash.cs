using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Where a fighting hull stands against its leash (<see cref="CombatLeash"/>).</summary>
    public enum LeashZone
    {
        /// <summary>No leash: the hull has no anchor.</summary>
        None = 0,

        /// <summary>Inside the inner edge: the manoeuvre flies unchanged.</summary>
        Inside = 1,

        /// <summary>In the outer <see cref="CombatLeash.EdgeFraction"/> of the leash: the manoeuvre's
        /// circle is drawn back toward the anchor, harder the deeper the hull is.</summary>
        Edge = 2,

        /// <summary>Past the leash: the hull stops chasing and flies back toward the anchor, nose on
        /// its target.</summary>
        Beyond = 3,
    }

    /// <summary>
    /// What holds a fighting hull to what it is fighting for. Every combatant has an <b>anchor</b>, a
    /// world point that may move: a raider's target node, a Guard's or a standing defender's node, a
    /// bench side's node point, a commanded wing hull's commander grid, a station-call hull's station.
    /// Its <b>leash</b> is its longest weapon range plus <see cref="MarginM"/> around that anchor
    /// (<see cref="RadiusM"/>).
    ///
    /// <para>Inside the leash's inner edge the combat manoeuvres fly as they would without one. In the
    /// outer <see cref="EdgeFraction"/> a velocity toward the anchor is added to what the manoeuvre
    /// wants, which moves the centre of the circle it flies round its target back toward the anchor.
    /// Past the leash the hull stops following its target and flies back toward the anchor under the
    /// braking cap, never slower than its combat floor, still facing its target
    /// (<see cref="FlightManoeuvre"/>). Target selection counts every metre a candidate lies past the
    /// inner edge as <see cref="TargetAnchorWeight"/> metres of distance
    /// (<see cref="TargetSelection.Pick"/>).</para>
    ///
    /// <para>A break-off is measured from the enemy, not the node:
    /// <see cref="ManoeuvreKind.Disengage"/> under <see cref="FlightSample.ClearOfHostilesM"/> is over
    /// once no hostile grid is within <see cref="DisengageClearM"/> of the hull.</para>
    ///
    /// <para>Code constants, not tuning. See docs/fleet-tactics.md and docs/fleet-events.md.</para>
    /// </summary>
    public static class CombatLeash
    {
        /// <summary>How far past its longest weapon's reach a hull may fight from its anchor, metres.</summary>
        public const double MarginM = 500.0;

        /// <summary>The outer share of the leash in which the manoeuvre is drawn back toward the anchor.</summary>
        public const double EdgeFraction = 0.25;

        /// <summary>How far a breaking-off hull must be from every hostile grid before its break-off
        /// is over, metres: past the flight controller's 3 km acquisition radius, so no hostile lead
        /// holding its own leash takes it as a target.</summary>
        public const double DisengageClearM = 3500.0;

        /// <summary>How many metres of distance each metre past the inner edge of the leash costs a
        /// target candidate.</summary>
        public const double TargetAnchorWeight = 2.0;

        /// <summary>The leash of a hull with <paramref name="bands"/>: the longest range among them,
        /// never under the range <paramref name="holdRangeM"/> its manoeuvre keeps from its target,
        /// plus <see cref="MarginM"/>. The hold range keeps an unarmed hull, and one whose bands could
        /// not be read, from being leashed tighter than the range it is asked to hold.</summary>
        public static double RadiusM(IReadOnlyList<WeaponBand> bands, double holdRangeM)
        {
            double longest = holdRangeM > 0 ? holdRangeM : 0;
            if (bands != null)
                for (int i = 0; i < bands.Count; i++)
                    if (bands[i].RangeM > longest) longest = bands[i].RangeM;
            return longest + MarginM;
        }

        /// <summary>The inner edge of a leash of <paramref name="leashM"/>, metres.</summary>
        public static double InnerM(double leashM)
        {
            return leashM > 0 ? leashM * (1.0 - EdgeFraction) : 0;
        }

        /// <summary>Where a hull <paramref name="anchorDistM"/> from its anchor stands against a leash
        /// of <paramref name="leashM"/>; <see cref="LeashZone.None"/> when the leash is zero.</summary>
        public static LeashZone Zone(double anchorDistM, double leashM)
        {
            if (leashM <= 0) return LeashZone.None;
            if (anchorDistM > leashM) return LeashZone.Beyond;
            if (anchorDistM > InnerM(leashM)) return LeashZone.Edge;
            return LeashZone.Inside;
        }

        /// <summary>How deep into the edge a hull is: 0 at the inner edge or inside it, 1 at the leash
        /// or past it.</summary>
        public static double EdgeDepth(double anchorDistM, double leashM)
        {
            if (leashM <= 0) return 0;
            double inner = InnerM(leashM);
            double band = leashM - inner;
            if (band <= 0 || anchorDistM <= inner) return 0;
            double d = (anchorDistM - inner) / band;
            return d > 1 ? 1 : d;
        }

        /// <summary>The distance a target candidate <paramref name="anchorDistM"/> from the choosing
        /// hull's anchor is charged on top of its own: <see cref="TargetAnchorWeight"/> per metre past
        /// the leash's inner edge, zero inside it and with no leash.</summary>
        public static double TargetPenaltyM(double anchorDistM, double leashM)
        {
            if (leashM <= 0) return 0;
            double over = anchorDistM - InnerM(leashM);
            return over > 0 ? over * TargetAnchorWeight : 0;
        }
    }
}
