using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CrewEscapeTests
    {
        [Fact] public void EscapeRequiresCheckedDestination()
        {
            var escape = new CrewEscape();
            Assert.Equal(CrewEscapeAction.None, escape.Begin(true, false, false));
            Assert.False(escape.Flying);
        }
        [Fact] public void EmbeddedActorsSkipFlight()
        {
            var escape = new CrewEscape();
            Assert.Equal(CrewEscapeAction.Teleport, escape.Begin(true, true, true));
            Assert.False(escape.Flying);
        }
        [Fact] public void ObstructedEscapeUsesTeleport()
        {
            Assert.Equal(CrewEscapeAction.Teleport, new CrewEscape().Begin(false, false, true));
        }
        [Fact] public void FlightTimesOutOnceAndCooldownPreventsRepeats()
        {
            var escape = new CrewEscape();
            Assert.Equal(CrewEscapeAction.Fly, escape.Begin(false, true, true));
            Assert.Equal(CrewEscapeAction.None, escape.Advance(3.5, false));
            Assert.Equal(CrewEscapeAction.Teleport, escape.Advance(0.5, false));
            Assert.Equal(CrewEscapeAction.None, escape.Advance(1, false));
            escape.Finish();
            Assert.Equal(CrewEscapeAction.None, escape.Begin(true, false, true));
            escape.Advance(10, false);
            Assert.Equal(CrewEscapeAction.Teleport, escape.Begin(true, false, true));
        }
        [Fact] public void ReachingDestinationEndsFlightWithoutTeleport()
        {
            var escape = new CrewEscape();
            escape.Begin(false, true, true);
            Assert.Equal(CrewEscapeAction.None, escape.Advance(1, true));
            Assert.False(escape.Flying);
            Assert.Equal(CrewEscapeAction.None, escape.Advance(20, false));
        }
        [Fact] public void InvalidTimeCannotForceTeleport()
        {
            var escape = new CrewEscape();
            escape.Begin(false, true, true);
            Assert.Equal(CrewEscapeAction.None, escape.Advance(double.NaN, false));
            Assert.Equal(CrewEscapeAction.None, escape.Advance(double.PositiveInfinity, false));
            Assert.Equal(CrewEscapeAction.None, escape.Advance(-1, false));
            Assert.True(escape.Flying);
        }
    }
}
