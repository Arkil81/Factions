using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class MunitionSpeedPolicyTests
    {
        [Fact]
        public void NoScale_IsTheIdentity_NotZero()
        {
            // This is a multiplier, so the value that changes nothing is 1. A zero sentinel would
            // mean "every round hangs in the air" the moment anything applied it literally.
            Assert.Equal(1.0, MunitionSpeedPolicy.NoScale);
            Assert.False(MunitionSpeedPolicy.IsOverride(MunitionSpeedPolicy.NoScale));
            Assert.True(MunitionSpeedPolicy.IsOverride(5.0));
            Assert.True(MunitionSpeedPolicy.IsOverride(0.5));
        }

        [Fact]
        public void IsOverride_RejectsValuesThatCannotBeApplied()
        {
            // A tuning file carrying any of these must leave the definitions alone rather than
            // multiply them by it.
            Assert.False(MunitionSpeedPolicy.IsOverride(0.0));
            Assert.False(MunitionSpeedPolicy.IsOverride(-2.0));
            Assert.False(MunitionSpeedPolicy.IsOverride(double.NaN));
        }

        [Fact]
        public void Clamp_TurnsAnUnusableValueIntoNoChange()
        {
            // Not into zero: a zero multiplier stops every round dead, which is never what a
            // malformed value meant.
            Assert.Equal(MunitionSpeedPolicy.NoScale, MunitionSpeedPolicy.Clamp(0.0));
            Assert.Equal(MunitionSpeedPolicy.NoScale, MunitionSpeedPolicy.Clamp(-3.0));
            Assert.Equal(MunitionSpeedPolicy.NoScale, MunitionSpeedPolicy.Clamp(double.NaN));
            Assert.Equal(MunitionSpeedPolicy.NoScale, MunitionSpeedPolicy.Clamp(double.PositiveInfinity));
            Assert.Equal(MunitionSpeedPolicy.MaxScale, MunitionSpeedPolicy.Clamp(MunitionSpeedPolicy.MaxScale * 10));
            Assert.Equal(4.0, MunitionSpeedPolicy.Clamp(4.0));
        }

        [Fact]
        public void ScaleSpeed_MultipliesAndPreservesTheContentsOrdering()
        {
            // The reason this multiplies rather than floors: a 150 m/s torpedo is slow so point
            // defence can meet it, and a 2000 m/s railgun slug is fast because that is its whole
            // character. Both must still be true after scaling.
            Assert.Equal(2500f, MunitionSpeedPolicy.ScaleSpeed(500f, 5.0));   // vanilla artillery
            Assert.Equal(750f, MunitionSpeedPolicy.ScaleSpeed(150f, 5.0));    // an AWP torpedo
            Assert.True(MunitionSpeedPolicy.ScaleSpeed(150f, 5.0) < MunitionSpeedPolicy.ScaleSpeed(500f, 5.0));
        }

        [Fact]
        public void ScaleSpeed_AtNoScale_ReturnsTheDefinitionUntouched()
        {
            Assert.Equal(500f, MunitionSpeedPolicy.ScaleSpeed(500f, MunitionSpeedPolicy.NoScale));
            Assert.Equal(600f, MunitionSpeedPolicy.ScaleAcceleration(600f, MunitionSpeedPolicy.NoScale));
        }

        [Fact]
        public void ScaleAcceleration_HoldsTheSpinUpDistanceFixed()
        {
            // v^2 / 2a is the distance a round covers reaching top speed, and MaxTrajectory does not
            // scale -- so if that distance grew with the scale, a torpedo would spend a different
            // fraction of its (unchanged) range still accelerating and point defence would meet a
            // different weapon. Squaring the acceleration is what keeps it identical.
            const float baseSpeed = 200f;    // the vanilla rocket
            const float baseAccel = 600f;
            const double k = 5.0;

            float scaledSpeed = MunitionSpeedPolicy.ScaleSpeed(baseSpeed, k);
            float scaledAccel = MunitionSpeedPolicy.ScaleAcceleration(baseAccel, k);

            double baseDistance = baseSpeed * baseSpeed / (2.0 * baseAccel);
            double scaledDistance = scaledSpeed * scaledSpeed / (2.0 * scaledAccel);
            Assert.Equal(baseDistance, scaledDistance, 6);

            // ...and the flight is correspondingly k times faster, which is the whole point.
            Assert.Equal((double)baseSpeed / baseAccel / k, (double)scaledSpeed / scaledAccel, 9);
        }

        [Fact]
        public void ScaleAcceleration_LeavesANonAcceleratingRoundAtZero()
        {
            // Every Warfare 2 shell declares MissileAcceleration 0 with InitialSpeed == DesiredSpeed.
            // Scaling must not conjure a spin-up profile for a round that has never had one.
            Assert.Equal(0f, MunitionSpeedPolicy.ScaleAcceleration(0f, 5.0));
        }

        [Theory]
        [InlineData("5", 5.0)]
        [InlineData("2.5", 2.5)]
        [InlineData("0.5", 0.5)]
        public void TryParseScale_AcceptsPlainNumbers(string text, double expected)
        {
            double scale;
            string error;
            Assert.True(MunitionSpeedPolicy.TryParseScale(text, out scale, out error));
            Assert.Null(error);
            Assert.Equal(expected, scale, 9);
        }

        [Fact]
        public void TryParseScale_IsCultureInvariant()
        {
            // The value crosses a chat command, a file bridge and a JSON file; a decimal comma read
            // as a group separator would silently mean a different world on a different host.
            double scale;
            string error;
            Assert.False(MunitionSpeedPolicy.TryParseScale("2,5", out scale, out error));
            Assert.NotNull(error);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("")]
        [InlineData("fast")]
        [InlineData("1000")]
        public void TryParseScale_RefusesWithAReason(string text)
        {
            double scale;
            string error;
            Assert.False(MunitionSpeedPolicy.TryParseScale(text, out scale, out error));
            Assert.NotNull(error);
            Assert.Equal(MunitionSpeedPolicy.NoScale, scale);
        }
    }
}
