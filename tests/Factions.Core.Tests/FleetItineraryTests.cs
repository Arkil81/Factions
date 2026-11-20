using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Which of a fleet's remaining stops a map may draw. The legs past that point have no
    /// waypoints yet, so the line a renderer would draw for them is a chord through whatever body
    /// the real route will arc around.
    /// </summary>
    public class FleetItineraryTests
    {
        private static FleetStop Nav(double x)
        {
            return new FleetStop { Kind = FleetStopKind.Navigation, HasPosition = true, PositionKm = new Vec3(x, 0, 0) };
        }

        private static FleetStop Semantic(FleetStopKind kind, string nodeId)
        {
            return new FleetStop { Kind = kind, NodeId = nodeId };
        }

        [Fact]
        public void NoStops_IsZero()
        {
            Assert.Equal(0, FleetItinerary.RoutedStopCount(null));
            Assert.Equal(0, FleetItinerary.RoutedStopCount(new List<FleetStop>()));
        }

        [Fact]
        public void DirectLeg_RoutesNothingAhead()
        {
            // No clearance waypoints: the current target IS the next semantic stop, and the Return
            // behind it is the leg nobody has planned.
            var stops = new List<FleetStop> { Semantic(FleetStopKind.Return, "HOME") };

            Assert.Equal(0, FleetItinerary.RoutedStopCount(stops));
        }

        [Fact]
        public void MidArc_RoutesTheWaypointsAndTheStopTheyReach()
        {
            var stops = new List<FleetStop>
            {
                Nav(100), Nav(200), Nav(300),
                Semantic(FleetStopKind.Service, "DEST"),
                Semantic(FleetStopKind.Return, "HOME"),
            };

            // Three chords plus the tangent-out onto DEST. HOME is a separate leg, unplanned.
            Assert.Equal(4, FleetItinerary.RoutedStopCount(stops));
        }

        [Fact]
        public void LastChordOfAnArc_RoutesNothingAhead()
        {
            // The fleet's current target is the arc's final vertex, so the destination it lands on
            // is already the current leg and what follows is the unplanned return.
            var stops = new List<FleetStop>
            {
                Semantic(FleetStopKind.Service, "DEST"),
                Semantic(FleetStopKind.Return, "HOME"),
            };

            Assert.Equal(0, FleetItinerary.RoutedStopCount(stops));
        }

        [Fact]
        public void WaypointsWithNothingBehindThem_AreAllRouted()
        {
            var stops = new List<FleetStop> { Nav(100), Nav(200) };

            Assert.Equal(2, FleetItinerary.RoutedStopCount(stops));
        }

        [Fact]
        public void WaypointsOnALaterLeg_AreNotReachedThroughAnUnroutedOne()
        {
            // A Navigation run that starts behind a semantic stop belongs to a leg the fleet is not
            // on. Counting from the front is what keeps it out.
            var stops = new List<FleetStop>
            {
                Semantic(FleetStopKind.Service, "DEST"),
                Nav(400), Nav(500),
                Semantic(FleetStopKind.Return, "HOME"),
            };

            Assert.Equal(0, FleetItinerary.RoutedStopCount(stops));
        }
    }
}
