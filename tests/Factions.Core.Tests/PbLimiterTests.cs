using System;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class PbLimiterTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        private const long Frames = 1800;   // a 30 s window at 60 tick/s

        /// <summary>One window of a script that asks to run every frame and costs
        /// <paramref name="msPerRun"/> each time it does. Returns how many runs were allowed.</summary>
        private static int Window(PbLimiter l, long id, double msPerRun, long opportunities = Frames, double externalMs = 0, int externalRuns = 0)
        {
            int ran = 0;
            for (long i = 0; i < opportunities; i++)
            {
                if (!l.AllowScheduled(id)) continue;
                l.RecordRun(id, 7, msPerRun, false);
                ran++;
            }
            for (int i = 0; i < externalRuns; i++) l.RecordRun(id, 7, externalMs, true);
            return ran;
        }

        private static PbLimiter Limiter()
        {
            return new PbLimiter(new PbLimiter.Settings());
        }

        [Fact]
        public void AScriptUnderTheCapIsNeverSlowed()
        {
            var l = Limiter();
            Window(l, 1, 0.2);
            Assert.Empty(l.Evaluate(Frames, T0, g => 0.25));
            Assert.Equal(PbLimiter.State.Normal, l.StateOf(1));
            Assert.Equal((int)Frames, Window(l, 1, 0.2));
        }

        [Fact]
        public void AScriptOverTheCapRunsEveryNthTickUnderIt()
        {
            var l = Limiter();
            Window(l, 1, 1.0);   // 1 ms per frame unthrottled against a 0.25 cap: N = 4
            var c = Assert.Single(l.Evaluate(Frames, T0, g => 0.25));
            Assert.Equal(PbLimiter.State.Slowed, c.To);
            Assert.Equal(4, c.Divisor);

            int ran = Window(l, 1, 1.0);
            Assert.Equal((int)Frames / 4, ran);
            Assert.True(ran * 1.0 / Frames <= 0.25 + 1e-9);
        }

        [Fact]
        public void TheDivisorHoldsWhileTheScriptStaysOverAndDoesNotFlap()
        {
            var l = Limiter();
            Window(l, 1, 1.0);
            l.Evaluate(Frames, T0, g => 0.25);
            for (int w = 1; w <= 5; w++)
            {
                Window(l, 1, 1.0);
                Assert.Empty(l.Evaluate(Frames, T0.AddSeconds(30 * w), g => 0.25));
            }
            Assert.Equal(PbLimiter.State.Slowed, l.StateOf(1));
        }

        [Fact]
        public void RecoveryIsGradualAndEndsNormal()
        {
            var l = Limiter();
            Window(l, 1, 4.0);                              // 4 ms/frame: N = 16
            Assert.Equal(16, l.Evaluate(Frames, T0, g => 0.25).Single().Divisor);

            int windows = 0;
            PbLimiter.State state = PbLimiter.State.Slowed;
            while (state != PbLimiter.State.Normal && windows < 40)
            {
                Window(l, 1, 0.1);                          // the script got cheap
                l.Evaluate(Frames, T0.AddSeconds(30 * ++windows), g => 0.25);
                state = l.StateOf(1);
            }
            Assert.Equal(PbLimiter.State.Normal, state);
            Assert.True(windows > 1, "one quarter per window, not a jump back to every tick");
        }

        [Fact]
        public void ExternalRunsAreNeverSkippedBySlowing()
        {
            var l = Limiter();
            Window(l, 1, 1.0);
            l.Evaluate(Frames, T0, g => 0.25);
            Assert.False(l.IsStopped(1));   // the ExecuteCode gate lets every run through
        }

        [Fact]
        public void APressureCapSlowsHarder()
        {
            var normal = Limiter();
            var pressured = Limiter();
            Window(normal, 1, 0.5);
            Window(pressured, 1, 0.5);
            int nNormal = normal.Evaluate(Frames, T0, g => 0.25).Single().Divisor;
            int nPressured = pressured.Evaluate(Frames, T0, g => 0.1).Single().Divisor;
            Assert.Equal(2, nNormal);
            Assert.Equal(5, nPressured);
        }

        [Fact]
        public void LongRunsForTwoWindowsStopTheScriptAndItIsRetriedLater()
        {
            var l = Limiter();
            Window(l, 1, 12.0, opportunities: 18);          // Update100-style: 18 runs of 12 ms
            Assert.DoesNotContain(l.Evaluate(Frames, T0, g => 0.25), c => c.To == PbLimiter.State.Stopped);
            Window(l, 1, 12.0, opportunities: 18);
            var stop = l.Evaluate(Frames, T0.AddSeconds(30), g => 0.25).Single();
            Assert.Equal(PbLimiter.State.Stopped, stop.To);
            Assert.True(l.IsStopped(1));
            Assert.Equal(0, Window(l, 1, 12.0, opportunities: 18));

            l.Evaluate(Frames, T0.AddSeconds(60), g => 0.25);
            Assert.True(l.IsStopped(1));
            var retry = l.Evaluate(Frames, T0.AddSeconds(30 + 600), g => 0.25).Single();
            Assert.Equal(PbLimiter.State.Normal, retry.To);
            Assert.False(l.IsStopped(1));
        }

        [Fact]
        public void OneLongFirstRunDoesNotStopAScript()
        {
            var l = Limiter();
            // A first run of 184 ms after spawn, then ordinary 0.1 ms runs.
            Assert.True(l.AllowScheduled(1));
            l.RecordRun(1, 7, 184.0, false);
            Window(l, 1, 0.1, opportunities: Frames - 1);
            l.Evaluate(Frames, T0, g => 0.25);
            Window(l, 1, 0.1);
            l.Evaluate(Frames, T0.AddSeconds(30), g => 0.25);
            Assert.NotEqual(PbLimiter.State.Stopped, l.StateOf(1));
        }

        [Fact]
        public void AnUpdate100ScriptWithOneSlowFirstRunIsNotStoppedEither()
        {
            var l = Limiter();
            Assert.True(l.AllowScheduled(1));
            l.RecordRun(1, 7, 184.0, false);                // window mean (184 + 17) / 18 = 11.2 ms
            Window(l, 1, 1.0, opportunities: 17);
            l.Evaluate(Frames, T0, g => 0.25);
            Window(l, 1, 1.0, opportunities: 18);
            l.Evaluate(Frames, T0.AddSeconds(30), g => 0.25);
            Assert.NotEqual(PbLimiter.State.Stopped, l.StateOf(1));
        }

        [Fact]
        public void ARecompileClearsAStop()
        {
            var l = Limiter();
            for (int w = 0; w < 2; w++) { Window(l, 1, 20.0, opportunities: 18); l.Evaluate(Frames, T0.AddSeconds(30 * w), g => 0.25); }
            Assert.True(l.IsStopped(1));
            var c = l.Reset(1);
            Assert.Equal(PbLimiter.State.Normal, c.To);
            Assert.False(l.IsStopped(1));
        }

        [Fact]
        public void AnIdleNormalBlockIsForgotten()
        {
            var l = Limiter();
            Window(l, 1, 0.1);
            l.Evaluate(Frames, T0, g => 0.25);
            l.Evaluate(Frames, T0.AddSeconds(30), g => 0.25);
            Assert.Empty(l.Limited());
            Assert.Equal(PbLimiter.State.Normal, l.StateOf(1));
        }

        [Fact]
        public void LimitedListsSlowedAndStoppedBlocks()
        {
            var l = Limiter();
            Window(l, 1, 1.0);
            for (int w = 0; w < 2; w++) Window(l, 2, 20.0, opportunities: 18);
            l.Evaluate(Frames, T0, g => 0.25);
            Window(l, 1, 1.0);
            Window(l, 2, 20.0, opportunities: 18);
            l.Evaluate(Frames, T0.AddSeconds(30), g => 0.25);
            var limited = l.Limited().OrderBy(b => b.BlockId).ToList();
            Assert.Equal(2, limited.Count);
            Assert.Equal(PbLimiter.State.Slowed, limited[0].State);
            Assert.Equal(PbLimiter.State.Stopped, limited[1].State);
        }
    }
}
