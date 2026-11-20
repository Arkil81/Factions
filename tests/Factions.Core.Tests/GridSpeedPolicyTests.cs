using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class GridSpeedPolicyTests
    {
        [Fact]
        public void Clamp_MirrorsSeOwnBounds()
        {
            // MyGridPhysics.LargeShipMaxLinearVelocity() is Max(0, Min(c, value)); anything this
            // plugin reports has to be what SE will actually enforce, not what was asked for.
            Assert.Equal(0.0, GridSpeedPolicy.Clamp(-1.0));
            Assert.Equal(100.0, GridSpeedPolicy.Clamp(100.0));
            Assert.Equal(GridSpeedPolicy.MaxSpeedMs, GridSpeedPolicy.Clamp(GridSpeedPolicy.MaxSpeedMs * 2));
        }

        [Fact]
        public void SpeedFloor_IsTheHostsOwnConversion()
        {
            // m/s -> km/min. 100 m/s is SE's vanilla large-grid limit and 6 km/min is the floor the
            // host has been stamping from it, so this pins the existing behaviour rather than
            // inventing a new one.
            Assert.Equal(6.0, GridSpeedPolicy.SpeedFloorKmPerMin(100.0), 9);
            Assert.Equal(0.0, GridSpeedPolicy.SpeedFloorKmPerMin(0.0), 9);
        }

        [Fact]
        public void SpeedFloor_ClampsBeforeConverting()
        {
            // A negative limit must not produce a negative floor, which would invert the transit
            // ramp rather than merely making it slow.
            Assert.Equal(0.0, GridSpeedPolicy.SpeedFloorKmPerMin(-500.0), 9);
        }

        [Theory]
        [InlineData("100", 100.0)]
        [InlineData("250.5", 250.5)]
        [InlineData("1e3", 1000.0)]
        public void TryParseSpeed_AcceptsPlainNumbers(string text, double expected)
        {
            double mps;
            string error;
            Assert.True(GridSpeedPolicy.TryParseSpeed(text, out mps, out error));
            Assert.Null(error);
            Assert.Equal(expected, mps, 9);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("fast")]
        [InlineData("0")]
        [InlineData("-100")]
        public void TryParseSpeed_RejectsWithAReason(string text)
        {
            double mps;
            string error;
            Assert.False(GridSpeedPolicy.TryParseSpeed(text, out mps, out error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void TryParseSpeed_RejectsAboveSeCeiling()
        {
            double mps;
            string error;
            Assert.False(GridSpeedPolicy.TryParseSpeed("200000000", out mps, out error));
            Assert.Contains("at most", error);
        }

        /// <summary>The value crosses a chat command, the file bridge and a JSON file. If parsing
        /// followed the host's culture, "250.5" would mean 250.5 on one server and 2505 on another —
        /// and the second would silently persist the wrong world.
        ///
        /// <para>This asserts the contract directly rather than flipping
        /// <c>Thread.CurrentCulture</c>, because the container test host runs with
        /// <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1</c> and throws on any non-invariant culture —
        /// a culture-flipping test would be red there and green on Windows, which is the wrong way
        /// round for a gate that must run in both.</para></summary>
        [Fact]
        public void TryParseSpeed_TakesTheDotAsDecimalAndRefusesTheComma()
        {
            double dotted;
            string error;
            Assert.True(GridSpeedPolicy.TryParseSpeed("250.5", out dotted, out error));
            Assert.Equal(250.5, dotted, 9);

            // The dangerous outcome is not a rejection, it is "250,5" quietly becoming 2505 under a
            // culture that reads the comma as a group separator. NumberStyles.Float carries no
            // AllowThousands, so the comma form is refused outright and the operator is told.
            double comma;
            Assert.False(GridSpeedPolicy.TryParseSpeed("250,5", out comma, out error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void NoOverride_IsZeroAndDistinctFromAnyRealSpeed()
        {
            // Zero has to mean "leave the world alone" rather than "a world where nothing moves",
            // or the persisted value would be ambiguous on the next load.
            Assert.False(GridSpeedPolicy.IsOverride(GridSpeedPolicy.NoOverride));
            Assert.False(GridSpeedPolicy.IsOverride(0.0));
            Assert.True(GridSpeedPolicy.IsOverride(100.0));

            double mps;
            string error;
            Assert.False(GridSpeedPolicy.TryParseSpeed("0", out mps, out error));
        }

        [Fact]
        public void ShippedDefaults_LeaveTheWorldAlone()
        {
            // A fresh install must not change anybody's physics. The feature is opt-in.
            FactionTuning tuning = TestSupport.Tuning();
            Assert.False(GridSpeedPolicy.IsOverride(tuning.GridMaxSpeedLarge));
            Assert.False(GridSpeedPolicy.IsOverride(tuning.GridMaxSpeedSmall));
        }
    }
}
