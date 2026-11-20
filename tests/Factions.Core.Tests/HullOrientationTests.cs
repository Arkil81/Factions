using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="HullOrientation"/> — which way an injected remote control has to face for
    /// a hull to fly nose-first. The fixtures are real hulls off the live prefab estate, because
    /// the failure this guards against is a plausible-looking rule that is wrong on a specific
    /// ship: <c>Angurvadal</c> (a sideways passenger seat enumerated ahead of the flight station)
    /// and <c>Alsvidr</c> (lift thrust larger than the main drive).
    /// </summary>
    public class HullOrientationTests
    {
        private static HullSeat Seat(ThrustDirection fwd, ThrustDirection up,
                                     bool control = false, bool main = false)
        {
            return new HullSeat { Forward = fwd, Up = up, ControlsShip = control, IsMain = main };
        }

        /// <summary>Angurvadal's real thrust, in newtons toward each direction: a 44-cell hull built
        /// nose-down, whose main drive pushes it toward grid-local Down.</summary>
        private static Dictionary<ThrustDirection, double> AngurvadalForce()
        {
            return new Dictionary<ThrustDirection, double>
            {
                { ThrustDirection.Down,     39_990_000 },
                { ThrustDirection.Up,       17_670_000 },
                { ThrustDirection.Left,     15_510_000 },
                { ThrustDirection.Right,    15_510_000 },
                { ThrustDirection.Backward, 13_750_000 },
                { ThrustDirection.Forward,  12_960_000 },
            };
        }

        /// <summary>Alsvidr: the author's main cockpit faces Backward, and the hull's strongest
        /// thrust is Up — its atmospheric lift bank, not its drive.</summary>
        private static Dictionary<ThrustDirection, double> AlsvidrForce()
        {
            return new Dictionary<ThrustDirection, double>
            {
                { ThrustDirection.Up,       19_080_000 },
                { ThrustDirection.Backward, 13_680_000 },
                { ThrustDirection.Forward,   6_480_000 },
                { ThrustDirection.Left,      6_480_000 },
                { ThrustDirection.Right,     6_480_000 },
                { ThrustDirection.Down,      5_400_000 },
            };
        }

        [Fact]
        public void MainCockpitBeatsASidewaysSeatEnumeratedFirst()
        {
            // Angurvadal as the grid actually presents it: a Left-facing passenger seat first, the
            // marked flight station fourth. Picking the first seat is what flew the hull sideways.
            var seats = new List<HullSeat>
            {
                Seat(ThrustDirection.Left,  ThrustDirection.Down, control: true),
                Seat(ThrustDirection.Left,  ThrustDirection.Down),
                Seat(ThrustDirection.Right, ThrustDirection.Down),
                Seat(ThrustDirection.Down,  ThrustDirection.Backward, control: true, main: true),
            };

            ThrustDirection nose, up;
            HullNoseSource source;
            int idx = HullOrientation.SelectNose(seats, AngurvadalForce(), out nose, out up, out source);

            Assert.Equal(3, idx);
            Assert.Equal(ThrustDirection.Down, nose);
            Assert.Equal(ThrustDirection.Backward, up);
            Assert.Equal(HullNoseSource.MainCockpit, source);
        }

        [Fact]
        public void ThrustNeverOverridesAnAuthoredSeat()
        {
            // Alsvidr: Up carries 19 MN against the main cockpit's 13.7 MN. A rule that took the
            // strongest direction would fly this hull straight up.
            var seats = new List<HullSeat>
            {
                Seat(ThrustDirection.Right,    ThrustDirection.Up),
                Seat(ThrustDirection.Backward, ThrustDirection.Up, control: true, main: true),
            };

            ThrustDirection nose, up;
            HullNoseSource source;
            HullOrientation.SelectNose(seats, AlsvidrForce(), out nose, out up, out source);

            Assert.Equal(ThrustDirection.Backward, nose);
            Assert.Equal(HullNoseSource.MainCockpit, source);
        }

        [Fact]
        public void FurnitureLosesToAControllingSeat()
        {
            // A bathroom facing Left, a flight seat facing Down. No main-cockpit flag anywhere.
            var seats = new List<HullSeat>
            {
                Seat(ThrustDirection.Left, ThrustDirection.Down),
                Seat(ThrustDirection.Down, ThrustDirection.Backward, control: true),
            };

            ThrustDirection nose, up;
            HullNoseSource source;
            int idx = HullOrientation.SelectNose(seats, AngurvadalForce(), out nose, out up, out source);

            Assert.Equal(1, idx);
            Assert.Equal(ThrustDirection.Down, nose);
            Assert.Equal(HullNoseSource.ControlSeat, source);
        }

        [Fact]
        public void AmongControllingSeatsTheOneWithThrustBehindItWins()
        {
            // Angurvadal with the main-cockpit flag stripped: five control seats, four of them
            // side-facing observation posts. Thrust is the tie-break that still finds the nose.
            var seats = new List<HullSeat>
            {
                Seat(ThrustDirection.Left,  ThrustDirection.Down, control: true),
                Seat(ThrustDirection.Right, ThrustDirection.Down, control: true),
                Seat(ThrustDirection.Down,  ThrustDirection.Backward, control: true),
                Seat(ThrustDirection.Left,  ThrustDirection.Down, control: true),
            };

            ThrustDirection nose, up;
            HullNoseSource source;
            int idx = HullOrientation.SelectNose(seats, AngurvadalForce(), out nose, out up, out source);

            Assert.Equal(2, idx);
            Assert.Equal(ThrustDirection.Down, nose);
            Assert.Equal(HullNoseSource.ControlSeat, source);
        }

        [Fact]
        public void ASeatFacingADirectionTheHullCannotPushIsSkipped()
        {
            var force = new Dictionary<ThrustDirection, double>
            {
                { ThrustDirection.Forward, 1_000_000 },
                { ThrustDirection.Up,        500_000 },
            };
            var seats = new List<HullSeat>
            {
                Seat(ThrustDirection.Left,    ThrustDirection.Up, control: true, main: true), // no thrust that way
                Seat(ThrustDirection.Forward, ThrustDirection.Up, control: true),
            };

            ThrustDirection nose, up;
            HullNoseSource source;
            int idx = HullOrientation.SelectNose(seats, force, out nose, out up, out source);

            Assert.Equal(1, idx);
            Assert.Equal(ThrustDirection.Forward, nose);
            Assert.Equal(HullNoseSource.ControlSeat, source);
        }

        [Fact]
        public void FurnitureOnlyHullStillPrefersTheSeatWithThrustBehindIt()
        {
            var seats = new List<HullSeat>
            {
                Seat(ThrustDirection.Forward,  ThrustDirection.Up),
                Seat(ThrustDirection.Backward, ThrustDirection.Up),
            };
            var force = new Dictionary<ThrustDirection, double>
            {
                { ThrustDirection.Forward,    200_000 },
                { ThrustDirection.Backward, 1_200_000 },
                { ThrustDirection.Up,       3_000_000 },   // lift, and no seat faces it
            };

            ThrustDirection nose, up;
            HullNoseSource source;
            int idx = HullOrientation.SelectNose(seats, force, out nose, out up, out source);

            Assert.Equal(1, idx);
            Assert.Equal(ThrustDirection.Backward, nose);
            Assert.Equal(HullNoseSource.AnySeat, source);
        }

        [Fact]
        public void WithNoSeatAtAllTheNoseComesFromThrust()
        {
            ThrustDirection nose, up;
            HullNoseSource source;
            int idx = HullOrientation.SelectNose(new List<HullSeat>(), AngurvadalForce(),
                                                 out nose, out up, out source);

            Assert.Equal(-1, idx);
            Assert.Equal(ThrustDirection.Down, nose);
            Assert.Equal(HullNoseSource.Thrust, source);
            // Square to the nose — Up is on the nose's own axis here and cannot be it — and the
            // strongest of what is left.
            Assert.False(HullOrientation.SameAxis(nose, up));
            Assert.Equal(ThrustDirection.Left, up);
        }

        [Fact]
        public void WithNeitherSeatsNorThrustTheGuessIsFlagged()
        {
            ThrustDirection nose, up;
            HullNoseSource source;
            HullOrientation.SelectNose(new List<HullSeat>(), new Dictionary<ThrustDirection, double>(),
                                       out nose, out up, out source);

            Assert.Equal(ThrustDirection.Forward, nose);
            Assert.Equal(ThrustDirection.Up, up);
            Assert.Equal(HullNoseSource.Default, source);
        }

        [Fact]
        public void TheUpVectorIsNeverOnTheNoseAxis()
        {
            // A seat whose recorded up has been mangled onto the nose axis would produce an invalid
            // block orientation, and SE would reject or mis-place the block.
            var seats = new List<HullSeat> { Seat(ThrustDirection.Down, ThrustDirection.Up, control: true) };

            ThrustDirection nose, up;
            HullNoseSource source;
            HullOrientation.SelectNose(seats, AngurvadalForce(), out nose, out up, out source);

            Assert.Equal(ThrustDirection.Down, nose);
            Assert.False(HullOrientation.SameAxis(nose, up));
        }

        [Fact]
        public void OppositePairsTheSixDirections()
        {
            Assert.Equal(ThrustDirection.Backward, HullOrientation.Opposite(ThrustDirection.Forward));
            Assert.Equal(ThrustDirection.Forward,  HullOrientation.Opposite(ThrustDirection.Backward));
            Assert.Equal(ThrustDirection.Down,     HullOrientation.Opposite(ThrustDirection.Up));
            Assert.Equal(ThrustDirection.Up,       HullOrientation.Opposite(ThrustDirection.Down));
            Assert.Equal(ThrustDirection.Right,    HullOrientation.Opposite(ThrustDirection.Left));
            Assert.Equal(ThrustDirection.Left,     HullOrientation.Opposite(ThrustDirection.Right));
        }
    }
}
