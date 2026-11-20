using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The strength mark drawn over a fleet marker, and the one property it has that a table of
    /// expected values cannot pin on its own: it must agree with the web dashboard's copy.
    ///
    /// <para>Two surfaces draw this ladder for the same people — the in-game map through
    /// <see cref="FleetEchelon"/>, and the dashboard through <c>fleetEchelon.ts</c>. Only one of
    /// the two can be C#, so the agreement cannot be had by sharing code and is instead checked
    /// here, against the TypeScript source itself. A doc comment saying "change one and change
    /// both" is a wish; <see cref="MirrorsTheDashboardLadder"/> is the thing that fails.</para>
    /// </summary>
    public class FleetEchelonTests
    {
        // ---- The ladder itself ------------------------------------------------------------

        [Theory]
        // Zero is a legacy fleet whose size was never written, and reads as one hull.
        [InlineData(0, EchelonGlyph.None, 0)]
        [InlineData(1, EchelonGlyph.None, 0)]
        [InlineData(2, EchelonGlyph.Dot, 2)]
        [InlineData(3, EchelonGlyph.Dot, 3)]
        [InlineData(4, EchelonGlyph.Bar, 1)]
        [InlineData(6, EchelonGlyph.Bar, 1)]
        [InlineData(7, EchelonGlyph.Bar, 2)]
        [InlineData(11, EchelonGlyph.Bar, 2)]
        [InlineData(12, EchelonGlyph.Bar, 3)]
        [InlineData(17, EchelonGlyph.Bar, 3)]
        [InlineData(18, EchelonGlyph.Cross, 1)]
        // RaidSquadronMaxHulls is 24, so the top band is reached in play rather than in theory.
        [InlineData(24, EchelonGlyph.Cross, 1)]
        public void LadderBands(int convoySize, EchelonGlyph glyph, int count)
        {
            FleetEchelon.Mark mark = FleetEchelon.For(convoySize);
            Assert.Equal(glyph, mark.Glyph);
            Assert.Equal(count, mark.Count);
        }

        /// <summary>A negative size cannot arrive from the wire, but a marker that throws would
        /// take the whole map frame with it.</summary>
        [Fact]
        public void NegativeSizeDrawsNothing()
        {
            Assert.Equal(EchelonGlyph.None, FleetEchelon.For(-3).Glyph);
        }

        /// <summary>
        /// At two and three hulls the dot count IS the hull count, which is the reason those two
        /// bands exist separately instead of banding with 4-6. A change that quietly made them a
        /// fixed pair would still pass <see cref="LadderBands"/> at 2 and fail here at 3.
        /// </summary>
        [Fact]
        public void DotCountIsTheHullCount()
        {
            Assert.Equal(2, FleetEchelon.For(2).Count);
            Assert.Equal(3, FleetEchelon.For(3).Count);
        }

        /// <summary>The mark never grows without bound — that is the whole point of banding, and
        /// what the marker-size axis failed to do.</summary>
        [Theory]
        [InlineData(1)]
        [InlineData(50)]
        [InlineData(5000)]
        public void MarkNeverExceedsThreeGlyphs(int convoySize)
        {
            Assert.InRange(FleetEchelon.For(convoySize).Count, 0, 3);
        }

        // ---- The text form ------------------------------------------------------------------

        [Fact]
        public void TextRepeatsTheGlyph()
        {
            Assert.Equal("", FleetEchelon.Text(1));
            Assert.Equal("●●", FleetEchelon.Text(2));
            Assert.Equal("▮▮▮", FleetEchelon.Text(14));
            Assert.Equal("✕", FleetEchelon.Text(20));
        }

        // ---- Parity with the dashboard --------------------------------------------------------

        /// <summary>
        /// Reads the dashboard's own ladder out of <c>fleetEchelon.ts</c> and requires it to give
        /// the same answer as this one for every convoy size a fleet can have.
        ///
        /// <para><b>A missing file fails rather than skips.</b> A parity test that quietly passes
        /// when it cannot find the thing it is comparing against is worse than no test: it reports
        /// agreement it never checked. If the TypeScript moves, this should say so.</para>
        /// </summary>
        [Fact]
        public void MirrorsTheDashboardLadder()
        {
            string path = DashboardLadderPath();
            Assert.True(File.Exists(path),
                "the dashboard's fleetEchelon.ts was not found at " + path
                + " -- if it moved, this test's path must follow it, because the two ladders "
                + "still have to agree.");

            List<Band> bands = ParseLadder(File.ReadAllText(path));
            Assert.NotEmpty(bands);

            for (int size = 0; size <= 40; size++)
            {
                Band expected = Resolve(bands, size);
                FleetEchelon.Mark actual = FleetEchelon.For(size);
                Assert.Equal(expected.Glyph, actual.Glyph.ToString().ToLowerInvariant());
                Assert.Equal(expected.Count, actual.Count);
            }
        }

        private struct Band
        {
            public int MinShips;
            public string Glyph;
            public int Count;
        }

        /// <summary>The dashboard picks the LAST band whose <c>minShips</c> the size reaches —
        /// mirrored here rather than reimplemented, so this test compares tables and not two
        /// guesses at how to read one.</summary>
        private static Band Resolve(List<Band> bands, int convoySize)
        {
            Band band = bands[0];
            for (int i = 0; i < bands.Count; i++)
                if (convoySize >= bands[i].MinShips) band = bands[i];
            return band;
        }

        private static List<Band> ParseLadder(string source)
        {
            var bands = new List<Band>();
            var entry = new Regex(
                @"\{\s*minShips:\s*(?<min>\d+)\s*,\s*glyph:\s*'(?<glyph>\w+)'\s*,\s*count:\s*(?<count>\d+)\s*\}",
                RegexOptions.CultureInvariant);

            foreach (Match match in entry.Matches(source))
            {
                var band = new Band();
                band.MinShips = int.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture);
                band.Glyph = match.Groups["glyph"].Value;
                band.Count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
                bands.Add(band);
            }
            return bands;
        }

        /// <summary>Walks up from the test binary to the repo root. The test project sits at
        /// <c>tests/Factions.Core.Tests</c>, so the marker to look for is the sibling
        /// <c>src</c>.</summary>
        private static string DashboardLadderPath()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null
                   && !Directory.Exists(Path.Combine(directory.FullName, "src", "Factions.Dashboard")))
            {
                directory = directory.Parent;
            }

            string root = directory != null ? directory.FullName : AppContext.BaseDirectory;
            return Path.Combine(root, "src", "Factions.Dashboard", "src", "map", "fleetEchelon.ts");
        }
    }
}
