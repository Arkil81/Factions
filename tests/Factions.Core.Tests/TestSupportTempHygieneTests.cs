using System.IO;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="TestSupport.TuningWithOverride"/> builds a real <c>FactionStore</c> over a temporary
    /// directory, because <c>LoadTuning</c> reads two files and writes one back and there is no
    /// in-memory path to it. That directory has to be removed again.
    ///
    /// <para>Left behind it is unbounded: one directory per call, 988 call sites, every run of the
    /// suite. On the Windows box on 2026-09-14 the pile had reached 1.26 million and the suite was
    /// spending 1,831 s in the kernel against 16 s of user time — it had stopped measuring the code and
    /// started measuring an NTFS directory index. Nothing failed; it just got slower every time anyone
    /// ran it, which is why it survived so long.</para>
    /// </summary>
    public class TestSupportTempHygieneTests
    {
        private const string Pattern = "factions-test-tuning-*";

        private static int Leftovers()
        {
            return Directory.GetDirectories(Path.GetTempPath(), Pattern).Length;
        }

        /// <summary>The bound is loose on purpose. Test classes run in parallel, so peer tests are
        /// creating and deleting directories matching this pattern throughout — a delta of exactly
        /// zero would be flaky for reasons that have nothing to do with the leak. With the leak the
        /// delta is at least <see cref="Calls"/>; without it, only whatever peers hold open at the
        /// instant of the second count. The two populations do not overlap.</summary>
        private const int Calls = 100;

        [Fact]
        public void RepeatedTuningCalls_LeaveNoTemporaryDirectoriesBehind()
        {
            int before = Leftovers();
            for (int i = 0; i < Calls; i++)
                TestSupport.Tuning();
            int after = Leftovers();

            Assert.True(after - before < Calls / 4,
                "TestSupport.Tuning() leaked temporary directories: " + before + " -> " + after +
                " across " + Calls + " calls.");
        }

        [Fact]
        public void AnOverrideAlsoCleansUp_AndStillApplies()
        {
            int before = Leftovers();
            FactionTuning tuned = TestSupport.TuningWithOverride("{\"ThreatDecayPerHour\": 0.25}");
            int after = Leftovers();

            // Proves the directory the override was written into was real and was read, so the
            // cleanup above is removing a directory that did its job rather than one that never
            // mattered.
            Assert.Equal(0.25, tuned.ThreatDecayPerHour);
            Assert.True(after - before < 25, "TuningWithOverride leaked: " + before + " -> " + after);
        }
    }
}
