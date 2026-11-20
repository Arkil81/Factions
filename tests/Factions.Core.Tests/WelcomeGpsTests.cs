using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The server writes the welcome GPS and later retires it, and the client finds it;
    /// all three go through <see cref="WelcomeGps"/>, so the round trip is the contract.</summary>
    public class WelcomeGpsTests
    {
        [Theory]
        [InlineData("Weighhouse Scale", "UVCO")]
        [InlineData("Citadel's Keep", "UNGD")]
        public void TheServersMarker_IsRecognised_ByItsDescription(string station, string tag)
        {
            // Named after the station, as the server now sends it: the name proves nothing.
            Assert.True(WelcomeGps.IsWelcome(station, WelcomeGps.Description(station, tag)));
        }

        /// <summary>The description already on players' GPS lists, written out, so a change to the
        /// prefix shows up as a test failure rather than as markers the server can no longer
        /// retire.</summary>
        [Fact]
        public void TheDescriptionIsTheOneAlreadyOnPlayersGpsLists()
        {
            Assert.Equal("Nearest trading station: Weighhouse Scale [UVCO]",
                WelcomeGps.Description("Weighhouse Scale", "UVCO"));
        }

        /// <summary>Markers sent before the GPS was named after its station: node-id names, as read
        /// from a live save. The next respawn must find them to retire them.</summary>
        [Theory]
        [InlineData("Factions-Welcome-N105", "Nearest trading station: Citadel's Keep [UNGD]")]
        [InlineData("Factions-Welcome-N269", null)]
        [InlineData("Factions-Welcome-N269", "edited by the player")]
        public void LegacyNodeIdMarkers_AreRecognised(string name, string description)
        {
            Assert.True(WelcomeGps.IsWelcome(name, description));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData("Weighhouse Scale", null)]
        [InlineData("Weighhouse Scale", "Factions node N108")]
        [InlineData("Economy station", "Nearest trading station")]
        [InlineData("Respawn Pod", "Location of your respawn pod")]
        [InlineData("factions-welcome-N677", "nearest trading station: X [Y]")]
        [InlineData("Factions-WelcomeN677", "My Nearest trading station: X [Y]")]
        [InlineData("My Factions-Welcome-N677", null)]
        public void OtherGpses_AreNotWelcomeMarkers(string name, string description)
        {
            Assert.False(WelcomeGps.IsWelcome(name, description));
        }
    }
}
