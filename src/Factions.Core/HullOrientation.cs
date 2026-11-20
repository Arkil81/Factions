using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One seat found on a hull — a cockpit, a passenger seat, a cryo chamber, a desk.
    /// SE models all of them as cockpits, so the flags are what separate a flight station from
    /// furniture.</summary>
    public struct HullSeat
    {
        /// <summary>The direction the occupant faces, in grid-local terms.</summary>
        public ThrustDirection Forward;
        public ThrustDirection Up;
        /// <summary>The author marked this seat as the ship's main cockpit.</summary>
        public bool IsMain;
        /// <summary>The seat's definition enables ship control — false for furniture, passenger
        /// seats and cryo chambers.</summary>
        public bool ControlsShip;
    }

    /// <summary>Which signal decided a hull's nose. Carried into the log line so a wrong attitude
    /// can be traced to the evidence that produced it.</summary>
    public enum HullNoseSource { MainCockpit, ControlSeat, AnySeat, Thrust, Default }

    /// <summary>
    /// Picks the direction a hull is built to fly, from the seats its author placed and the thrust
    /// it carries.
    ///
    /// <para>This exists because an injected remote control defines the flight attitude: SE aims
    /// the <b>remote control's</b> forward at the waypoint, so a hull given an RC facing its Left
    /// flies sideways however its thrusters are arranged. "The first cockpit on the grid" is not a
    /// usable answer — SE's cockpit class also covers bathrooms, desks, beds and passenger seats,
    /// and on a hull like <c>Angurvadal</c> one of those is enumerated first, mounted 90° off the
    /// nose.</para>
    ///
    /// <para><b>Authored evidence outranks thrust.</b> Measured over the live prefab estate
    /// (2026-09-12, 123 hulls with both seats and thrust): the strongest thrust direction disagrees
    /// with the authored flight station on 30% of hulls, because an atmospheric hull's lift bank is
    /// larger than its main drive. Thrust therefore only ranks candidates within a tier and vetoes
    /// a direction the hull cannot push at all — it never overrides a seat.</para>
    /// </summary>
    public static class HullOrientation
    {
        private static readonly ThrustDirection[] AllDirections =
        {
            ThrustDirection.Forward, ThrustDirection.Backward, ThrustDirection.Up,
            ThrustDirection.Down, ThrustDirection.Left, ThrustDirection.Right,
        };

        public static ThrustDirection Opposite(ThrustDirection d)
        {
            switch (d)
            {
                case ThrustDirection.Forward:  return ThrustDirection.Backward;
                case ThrustDirection.Backward: return ThrustDirection.Forward;
                case ThrustDirection.Up:       return ThrustDirection.Down;
                case ThrustDirection.Down:     return ThrustDirection.Up;
                case ThrustDirection.Left:     return ThrustDirection.Right;
                default:                       return ThrustDirection.Left;
            }
        }

        /// <summary>Whether two directions lie on the same axis — a valid block orientation needs a
        /// forward and an up that do not.</summary>
        public static bool SameAxis(ThrustDirection a, ThrustDirection b)
        {
            return a == b || a == Opposite(b);
        }

        /// <summary>
        /// The hull's nose, and an up vector square to it.
        ///
        /// <para>The ladder, strongest evidence first: the seat the author marked as main cockpit;
        /// else the ship-controlling seat with the most thrust behind its facing; else any seat by
        /// the same rule; else the direction the hull can push hardest. A candidate whose facing
        /// carries no thrust at all is skipped — nothing flies nose-first down an axis it cannot
        /// accelerate along.</para>
        /// </summary>
        /// <param name="seats">Every seat on the hull, in the order the caller found them. Ties are
        /// broken toward the earlier entry.</param>
        /// <param name="forceNByPushDirection">Thrust force (N) the hull can apply <b>toward</b>
        /// each direction. Note this is the opposite of how a thruster's mounted facing buckets it
        /// (a thruster mounted facing Down pushes the grid Up) — see <see cref="Opposite"/>.</param>
        /// <returns>Index into <paramref name="seats"/> of the seat whose orientation was adopted,
        /// or -1 when no seat supplied it.</returns>
        public static int SelectNose(IList<HullSeat> seats,
                                     IReadOnlyDictionary<ThrustDirection, double> forceNByPushDirection,
                                     out ThrustDirection nose, out ThrustDirection up,
                                     out HullNoseSource source)
        {
            int main    = BestSeat(seats, forceNByPushDirection, true,  false);
            int control = main >= 0 ? -1 : BestSeat(seats, forceNByPushDirection, false, true);
            int any     = (main >= 0 || control >= 0) ? -1 : BestSeat(seats, forceNByPushDirection, false, false);

            int chosen = main >= 0 ? main : (control >= 0 ? control : any);
            if (chosen >= 0)
            {
                nose   = seats[chosen].Forward;
                up     = SameAxis(seats[chosen].Up, nose)
                             ? PerpendicularUp(nose, forceNByPushDirection)
                             : seats[chosen].Up;
                source = main >= 0 ? HullNoseSource.MainCockpit
                       : control >= 0 ? HullNoseSource.ControlSeat
                       : HullNoseSource.AnySeat;
                return chosen;
            }

            ThrustDirection strongest;
            if (StrongestDirection(forceNByPushDirection, out strongest))
            {
                nose   = strongest;
                up     = PerpendicularUp(nose, forceNByPushDirection);
                source = HullNoseSource.Thrust;
                return -1;
            }

            // A hull with neither a seat nor a working thruster. Grid-local forward is a guess, and
            // is flagged as one.
            nose   = ThrustDirection.Forward;
            up     = ThrustDirection.Up;
            source = HullNoseSource.Default;
            return -1;
        }

        /// <summary>The direction square to <paramref name="nose"/> with the most thrust behind it
        /// — on a hull that flies in gravity this is the lift axis, i.e. the hull's own up.</summary>
        public static ThrustDirection PerpendicularUp(ThrustDirection nose,
                                                      IReadOnlyDictionary<ThrustDirection, double> forceNByPushDirection)
        {
            ThrustDirection best = SameAxis(nose, ThrustDirection.Up) ? ThrustDirection.Forward : ThrustDirection.Up;
            double bestForce = -1;
            for (int i = 0; i < AllDirections.Length; i++)
            {
                ThrustDirection d = AllDirections[i];
                if (SameAxis(d, nose)) continue;
                double f = ForceToward(forceNByPushDirection, d);
                if (f > bestForce) { bestForce = f; best = d; }
            }
            return best;
        }

        private static int BestSeat(IList<HullSeat> seats,
                                    IReadOnlyDictionary<ThrustDirection, double> forceNByPushDirection,
                                    bool requireMain, bool requireControl)
        {
            if (seats == null) return -1;
            int best = -1;
            double bestForce = 0;
            for (int i = 0; i < seats.Count; i++)
            {
                HullSeat s = seats[i];
                if (requireMain && !s.IsMain) continue;
                if (requireControl && !s.ControlsShip) continue;

                // The veto: a facing the hull cannot push along is not its nose, whatever is bolted
                // there. Everything else is ranked, not refused.
                double f = ForceToward(forceNByPushDirection, s.Forward);
                if (f <= 0) continue;
                if (best < 0 || f > bestForce) { best = i; bestForce = f; }
            }
            return best;
        }

        private static bool StrongestDirection(IReadOnlyDictionary<ThrustDirection, double> forceNByPushDirection,
                                               out ThrustDirection strongest)
        {
            strongest = ThrustDirection.Forward;
            double best = 0;
            for (int i = 0; i < AllDirections.Length; i++)
            {
                double f = ForceToward(forceNByPushDirection, AllDirections[i]);
                if (f > best) { best = f; strongest = AllDirections[i]; }
            }
            return best > 0;
        }

        private static double ForceToward(IReadOnlyDictionary<ThrustDirection, double> forceNByPushDirection,
                                          ThrustDirection d)
        {
            double f;
            if (forceNByPushDirection == null || !forceNByPushDirection.TryGetValue(d, out f)) return 0;
            return f > 0 ? f : 0;
        }
    }
}
