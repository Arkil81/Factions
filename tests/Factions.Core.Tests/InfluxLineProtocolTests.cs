using System;
using System.Globalization;
using System.Threading;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class InfluxLineProtocolTests
    {
        private const long Ts = 1_600_000_000_000_000_000L;

        private static InfluxLineProtocol New()
        {
            return new InfluxLineProtocol();
        }

        [Fact]
        public void BuildsAMinimalPoint()
        {
            var b = New();
            b.Point("torch_server", Ts).Field("tickRate", 59.5).End();
            Assert.Equal("torch_server tickRate=59.5 1600000000000000000\n", b.Build());
        }

        [Fact]
        public void EmptyBatchIsNullNotEmptyString()
        {
            // An empty POST body is a 400 from Influx. A caller testing for null cannot send one.
            Assert.Null(New().Build());
            Assert.True(New().IsEmpty);
        }

        [Fact]
        public void IntegerFieldsCarryTheISuffixAndFloatsDoNot()
        {
            var b = New();
            b.Point("m", Ts).Field("n", 42L).Field("x", 1.5).End();
            Assert.Equal("m n=42i,x=1.5 1600000000000000000\n", b.Build());
        }

        [Fact]
        public void TagsAndFieldsAreSeparatedByTheFirstSpace()
        {
            var b = New();
            b.Point("grid_census", Ts).Tag("kind", "fleet").Field("count", 7L).End();
            Assert.Equal("grid_census,kind=fleet count=7i 1600000000000000000\n", b.Build());
        }

        [Fact]
        public void CommonTagsPrecedePerPointTagsOnEveryLine()
        {
            var b = New();
            b.CommonTag("host", "awl-standalone-test");
            b.Point("m", Ts).Tag("kind", "a").Field("v", 1L).End();
            b.Point("m", Ts).Tag("kind", "b").Field("v", 2L).End();
            Assert.Equal(
                "m,host=awl-standalone-test,kind=a v=1i 1600000000000000000\n" +
                "m,host=awl-standalone-test,kind=b v=2i 1600000000000000000\n",
                b.Build());
        }

        [Fact]
        public void MeasurementEscapesCommaAndSpaceButNotEquals()
        {
            var b = New();
            b.Point("we ird,name=x", Ts).Field("v", 1L).End();
            Assert.StartsWith(@"we\ ird\,name=x v=1i", b.Build());
        }

        [Fact]
        public void TagKeysAndValuesEscapeEqualsAsWell()
        {
            var b = New();
            b.Point("m", Ts).Tag("a=b", "c,d e=f").Field("v", 1L).End();
            Assert.Equal(@"m,a\=b=c\,d\ e\=f v=1i 1600000000000000000" + "\n", b.Build());
        }

        [Fact]
        public void StringFieldsAreQuotedAndEscapeQuoteAndBackslash()
        {
            var b = New();
            b.Point("m", Ts).Field("name", "Big \"Ship\" C:\\x, y").End();
            Assert.Equal("m name=\"Big \\\"Ship\\\" C:\\\\x, y\" 1600000000000000000\n", b.Build());
        }

        [Fact]
        public void EmptyTagValueIsSkippedRatherThanWrittenAsBare()
        {
            // `tag=` is a syntax error that rejects the whole batch, and "no value" is the ordinary
            // reason a caller has one -- so it must not be able to poison the other points.
            var b = New();
            b.Point("m", Ts).Tag("faction", "").Tag("kind", (string)null).Tag("ok", "y")
                            .Field("v", 1L).End();
            Assert.Equal("m,ok=y v=1i 1600000000000000000\n", b.Build());
        }

        [Fact]
        public void PointWithNoFieldsIsDiscardedAndLeavesTheBatchIntact()
        {
            var b = New();
            b.Point("m", Ts).Field("v", 1L).End();
            b.Point("m", Ts).Tag("k", "v").End();          // no field at all
            b.Point("m", Ts).Field("v", 2L).End();
            Assert.Equal(2, b.LineCount);
            Assert.Equal(
                "m v=1i 1600000000000000000\nm v=2i 1600000000000000000\n",
                b.Build());
        }

        [Fact]
        public void NaNAndInfinityAreSkippedSoOneBadSampleCannotRejectTheBatch()
        {
            var b = New();
            b.Point("m", Ts).Field("bad", double.NaN)
                            .Field("worse", double.PositiveInfinity)
                            .Field("good", 3.0).End();
            Assert.Equal("m good=3 1600000000000000000\n", b.Build());
        }

        [Fact]
        public void APointThatIsOnlyNaNIsDiscardedEntirely()
        {
            var b = New();
            b.Point("m", Ts).Field("bad", double.NaN).End();
            Assert.True(b.IsEmpty);
            Assert.Null(b.Build());
        }

        [Fact]
        public void NewlinesCannotSplitALine()
        {
            var b = New();
            b.Point("m\nx", Ts).Tag("k\nv", "a\nb").Field("s", "one\ntwo").End();
            string line = b.Build();
            Assert.Equal(1, b.LineCount);
            // Exactly one newline, and it is the terminator.
            Assert.EndsWith("\n", line);
            Assert.Equal(1, line.Split('\n').Length - 1);
        }

        [Fact]
        public void TagAfterFieldThrowsRatherThanEmittingAnUnparseableLine()
        {
            var b = New();
            Assert.Throws<InvalidOperationException>(() =>
            {
                var p = b.Point("m", Ts).Field("v", 1L);
                p.Tag("late", "yes");
            });
        }

        [Fact]
        public void FloatFormattingIsInvariantUnderALocaleThatUsesADecimalComma()
        {
            // The separator is set on a cloned culture rather than by naming "fi-FI": the Linux
            // container runs with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT, under which every culture
            // name resolves to the invariant culture -- so a name-based version of this test would
            // pass there without a decimal comma ever existing, and only fail on Windows.
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            comma.NumberFormat.NumberGroupSeparator = ".";
            Assert.Equal("1,25", 1.25.ToString("0.######", comma));   // the hazard is real here

            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = comma;
                var b = New();
                b.Point("m", Ts).Field("v", 1.25).End();
                Assert.Equal("m v=1.25 1600000000000000000\n", b.Build());
            }
            finally { Thread.CurrentThread.CurrentCulture = original; }
        }

        [Fact]
        public void LargeAndSmallDoublesStayOutOfExponentForm()
        {
            var b = New();
            b.Point("m", Ts).Field("big", 12345678901.0).Field("small", 0.000001).End();
            string s = b.Build();
            Assert.DoesNotContain("E", s);
            Assert.DoesNotContain("e+", s);
            Assert.Contains("big=12345678901", s);
        }

        [Fact]
        public void BoolFieldsUseTAndF()
        {
            var b = New();
            b.Point("m", Ts).Field("up", true).Field("down", false).End();
            Assert.Equal("m up=T,down=F 1600000000000000000\n", b.Build());
        }

        [Fact]
        public void NullStringFieldIsSkipped()
        {
            var b = New();
            b.Point("m", Ts).Field("s", (string)null).Field("v", 1L).End();
            Assert.Equal("m v=1i 1600000000000000000\n", b.Build());
        }

        [Fact]
        public void ResetClearsLinesAndCommonTags()
        {
            var b = New();
            b.CommonTag("host", "h");
            b.Point("m", Ts).Field("v", 1L).End();
            b.Reset();
            Assert.True(b.IsEmpty);
            b.Point("m", Ts).Field("v", 2L).End();
            Assert.Equal("m v=2i 1600000000000000000\n", b.Build());
        }

        [Fact]
        public void ToUnixNanosMatchesTheEpochAndAKnownInstant()
        {
            Assert.Equal(0L, InfluxLineProtocol.ToUnixNanos(
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(1_600_000_000_000_000_000L, InfluxLineProtocol.ToUnixNanos(
                new DateTime(2020, 9, 13, 12, 26, 40, DateTimeKind.Utc)));
        }

        [Fact]
        public void ToUnixNanosConvertsALocalTimeRatherThanReadingItsTicks()
        {
            var utc = new DateTime(2020, 9, 13, 12, 26, 40, DateTimeKind.Utc);
            Assert.Equal(InfluxLineProtocol.ToUnixNanos(utc),
                         InfluxLineProtocol.ToUnixNanos(utc.ToLocalTime()));
        }
    }
}
