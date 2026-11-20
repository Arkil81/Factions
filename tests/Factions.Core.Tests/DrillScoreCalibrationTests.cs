using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The drill score and its litres/second coefficient are one calibration, and since
    /// FAC-228 that calibration is <b>derived from SE's own mechanic rather than fitted to preserve
    /// an earlier formula's output</b>.
    ///
    /// <para>The class this replaces guarded the opposite property: that a vanilla large drill "keeps
    /// the rate it had" under an <c>r²</c> score and a coefficient carrying the reference radius.
    /// That rate was itself inherited from the retired <c>CutOutRadius³ × Speed</c> formula, so the
    /// invariant being protected was continuity with a number nobody had justified — and it hid a
    /// dimensional error, an <c>m³</c> volume consumed as litres per second with the ×1000 dropped.
    /// The tests below pin the derivation instead, so they fail if anyone re-fits the coefficient to
    /// a target rate.</para></summary>
    public class DrillScoreCalibrationTests
    {
        // Read from decompiled SE, not chosen here.
        private const double VanillaLargeDrillCutOutRadius = 1.9;   // CubeBlocks_Tools.sbc
        private const double SeCutIntervalSeconds          = 1.5;   // 90 frames @ 60fps, MyShipDrill
        private const double SeVoxelHarvestRatio           = 0.009; // MyDrillBase.VoxelHarvestRatio

        /// <summary>The coefficient must equal SE's geometry and cadence, with no free term. Anyone
        /// nudging it to hit a gameplay target breaks this and should move
        /// <see cref="FactionTuning.MineDutyCycle"/> instead.</summary>
        [Fact]
        public void Coefficient_IsSeCutGeometryOverSeCutInterval_NotAFittedNumber()
        {
            double derived = 4.0 / 3.0 * Math.PI * SeVoxelHarvestRatio / SeCutIntervalSeconds * 1000.0;

            Assert.Equal(25.1327, derived, 4);
            Assert.Equal(derived, TestSupport.Tuning().MineDrillLitersPerScorePoint, 4);
        }

        /// <summary>The score carries the cut's volume and the coefficient carries everything else,
        /// so the two multiply back to SE's own peak for a real block.</summary>
        [Fact]
        public void VanillaLargeDrillOnIron_ReproducesSePeakOutput()
        {
            var tuning = TestSupport.Tuning();

            double peak = MineTargeting.DrillBlockScore(VanillaLargeDrillCutOutRadius)
                        * tuning.MineDrillLitersPerScorePoint
                        * tuning.OreDensityRatioFor("Ore:Iron");

            Assert.Equal(861.9, peak, 1);
        }

        /// <summary>The retired coefficient must stay retired. A live tuning.json still carrying
        /// <c>MineDrillFallbackLitersPerScorePoint: 1.9</c> — every host has one — must not reach the
        /// rate, and the same goes for v0.127.0's <c>MineRateMultiplier</c>. Both are kept only so a
        /// stale key is ignored rather than an error.</summary>
        [Fact]
        public void RetiredCoefficientAndRateMultiplier_DoNotReachTheRate()
        {
            FactionTuning t = TestSupport.TuningWithOverride(@"{
                ""MineDrillFallbackLitersPerScorePoint"": 999.0,
                ""MineRateMultiplier"": 999.0
            }");

            // They load (a stale key must not throw) ...
            Assert.Equal(999.0, t.MineDrillFallbackLitersPerScorePoint, 6);
            Assert.Equal(999.0, t.MineRateMultiplier, 6);
            // ... and change nothing about what a drill actually does.
            Assert.Equal(25.1327, t.MineDrillLitersPerScorePoint, 4);
            Assert.Equal(861.9, MineTargeting.DrillBlockScore(VanillaLargeDrillCutOutRadius)
                              * t.MineDrillLitersPerScorePoint
                              * t.OreDensityRatioFor("Ore:Iron"), 1);
        }

        /// <summary>The ore-density table is SE's <c>MinedOreRatio</c>, taking the ore-bearing rock
        /// material. The rare ores are the ones that matter: the retired <c>FreeMintScale</c> put
        /// them 20x behind iron where SE says 5x, and dropping the term entirely would have put them
        /// level with it.</summary>
        [Theory]
        [InlineData("Ore:Iron",      5.0)]
        [InlineData("Ore:Stone",     5.0)]
        [InlineData("Ore:Ice",       5.0)]
        [InlineData("Ore:Nickel",    3.0)]
        [InlineData("Ore:Cobalt",    3.0)]
        [InlineData("Ore:Silicon",   3.0)]
        [InlineData("Ore:Magnesium", 3.0)]
        [InlineData("Ore:Gold",      1.0)]
        [InlineData("Ore:Platinum",  1.0)]
        [InlineData("Ore:Silver",    1.0)]
        [InlineData("Ore:Uranium",   0.3)]
        public void OreDensityRatio_MatchesSeMinedOreRatio(string subTypeId, double expected)
        {
            Assert.Equal(expected, TestSupport.Tuning().OreDensityRatioFor(subTypeId), 6);
        }

        /// <summary>A modded ore with no row must mine slowly, not infinitely fast and not never:
        /// a zero here would make the ore unmineable rather than merely poor.</summary>
        [Fact]
        public void OreDensityRatio_UnknownOre_TakesThePreciousOreFallback()
        {
            var tuning = TestSupport.Tuning();

            Assert.Equal(1.0, tuning.OreDensityRatioFor("Ore:Unobtainium"), 6);
            Assert.True(tuning.OreDensityRatioFor("Ore:Unobtainium") > 0);
            Assert.True(tuning.OreDensityRatioFor("Ore:Unobtainium")
                      < tuning.OreDensityRatioFor("Ore:Iron"));
        }
    }
}
