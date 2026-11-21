using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>One wave per area per night: the second wave at a base is refused until half a day
    /// has passed, and the wave that opened the lock keeps all its bots.</summary>
    public sealed class FaunaNightGateTests
    {
        private static readonly object Wolves = new object();
        private const long Lock = 60L * 60 * 1000; // a 120-minute day

        [Fact]
        public void ASecondWaveAtTheSameBaseIsRefused()
        {
            var g = new FaunaNightGate();
            object first = new object(), second = new object();
            Assert.True(g.Admit(Wolves, first, 0, 0, 0, 0, Lock));
            Assert.False(g.Admit(Wolves, second, 50, 0, 0, 200000, Lock));
            Assert.Equal(1, g.Refused);
        }

        [Fact]
        public void EveryBotOfTheAdmittedWaveSpawns()
        {
            var g = new FaunaNightGate();
            object wave = new object();
            for (int i = 0; i < 6; i++) Assert.True(g.Admit(Wolves, wave, 0, 0, 0, 0, Lock));
            Assert.Equal(1, g.Opened);
        }

        [Fact]
        public void ARefusedWaveCountsOnce()
        {
            var g = new FaunaNightGate();
            g.Admit(Wolves, new object(), 0, 0, 0, 0, Lock);
            object late = new object();
            for (int i = 0; i < 5; i++) Assert.False(g.Admit(Wolves, late, 0, 0, 0, 1000, Lock));
            Assert.Equal(1, g.Refused);
        }

        [Fact]
        public void TheNextNightHasItsWave()
        {
            var g = new FaunaNightGate();
            Assert.True(g.Admit(Wolves, new object(), 0, 0, 0, 0, Lock));
            Assert.True(g.Admit(Wolves, new object(), 0, 0, 0, Lock + 1, Lock));
        }

        [Fact]
        public void FarAwayIsAnotherArea()
        {
            var g = new FaunaNightGate();
            Assert.True(g.Admit(Wolves, new object(), 0, 0, 0, 0, Lock));
            Assert.True(g.Admit(Wolves, new object(), FaunaNightGate.RadiusM + 1, 0, 0, 1000, Lock));
        }

        [Fact]
        public void AnotherRuleIsNotLocked()
        {
            var g = new FaunaNightGate();
            Assert.True(g.Admit(Wolves, new object(), 0, 0, 0, 0, Lock));
            Assert.True(g.Admit(new object(), new object(), 0, 0, 0, 1000, Lock));
        }

        [Fact]
        public void TheLockIsHalfTheWorldDay()
        {
            Assert.Equal(Lock, FaunaNightGate.LockMs(true, 120.0));
            Assert.Equal(FaunaNightGate.FixedSunLockMs, FaunaNightGate.LockMs(false, 120.0));
            Assert.Equal(FaunaNightGate.FixedSunLockMs, FaunaNightGate.LockMs(true, 0.0));
        }

        [Fact]
        public void OncePerNightParsesFromTheRecipe()
        {
            string error;
            WorldRecipe r = WorldRecipe.Parse(
                "{ \"Planets\": [ { \"Name\": \"T\", \"Definition\": \"Tellus\", \"Fauna\": [ "
                + "{ \"Animals\": [\"Wolf\"], \"When\": \"Night\", \"OncePerNight\": true } ] } ] }", out error);
            Assert.True(r != null, error);
            Assert.True(r.Planets[0].Fauna[0].OncePerNight);
        }
    }
}
