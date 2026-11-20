using Factions;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The landing probe compares a server's and a client's voxel content by checksum. That
    /// comparison only means something if both ends read the same box, so the box must not move
    /// when the probe point moves a little.
    /// </summary>
    public sealed class LandingProbeMathTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(15)]
        [InlineData(16)]
        [InlineData(100)]
        [InlineData(-1)]
        [InlineData(-16)]
        [InlineData(-17)]
        [InlineData(524287)]
        public void ThePointSitsAtLeastAMarginInsideItsBox(int coord)
        {
            int min = LandingProbeMath.BoxMin(coord);
            int max = min + LandingProbeMath.Span - 1;
            Assert.True(coord - min >= LandingProbeMath.Margin, "below: " + (coord - min));
            Assert.True(max - coord >= LandingProbeMath.Margin, "above: " + (max - coord));
        }

        [Fact]
        public void PointsInOneCellShareABox_AndTheNextCellMovesItByOneCell()
        {
            // Floor, not truncation: -1 and -16 are one cell, 0 is the next. Truncating toward zero
            // would make -15..15 a single 31-wide cell.
            Assert.Equal(LandingProbeMath.BoxMin(-16), LandingProbeMath.BoxMin(-1));
            Assert.Equal(LandingProbeMath.BoxMin(0), LandingProbeMath.BoxMin(15));
            Assert.Equal(LandingProbeMath.Cell, LandingProbeMath.BoxMin(16) - LandingProbeMath.BoxMin(15));
            Assert.Equal(LandingProbeMath.Cell, LandingProbeMath.BoxMin(0) - LandingProbeMath.BoxMin(-1));
        }

        [Fact]
        public void TheChecksumIsFnv1a_AndSeesOneVoxelChange()
        {
            // Published FNV-1a 32 vectors: "" -> 811c9dc5, "a" -> e40c292c.
            Assert.Equal(0x811c9dc5u, LandingProbeMath.Fnv1a(new byte[0], 0));
            Assert.Equal(0xe40c292cu, LandingProbeMath.Fnv1a(new[] { (byte)'a' }, 1));

            var full = new byte[32 * 32 * 32];
            for (int i = 0; i < full.Length; i++) full[i] = 255;
            var cratered = (byte[])full.Clone();
            cratered[12345] = 254;
            Assert.NotEqual(LandingProbeMath.Fnv1a(full, full.Length), LandingProbeMath.Fnv1a(cratered, cratered.Length));
            Assert.Equal(1, LandingProbeMath.Sum(full, full.Length) - LandingProbeMath.Sum(cratered, cratered.Length));
        }

        [Fact]
        public void OnlyTheCountedPrefixIsRead()
        {
            // MyStorageData reuses a buffer larger than the box it was last resized to; the tail is
            // stale bytes from an earlier read and must not reach the checksum.
            var a = new byte[] { 1, 2, 3, 9, 9 };
            var b = new byte[] { 1, 2, 3, 7, 7 };
            Assert.Equal(LandingProbeMath.Fnv1a(a, 3), LandingProbeMath.Fnv1a(b, 3));
            Assert.Equal(6, LandingProbeMath.Sum(a, 3));
        }
    }
}
