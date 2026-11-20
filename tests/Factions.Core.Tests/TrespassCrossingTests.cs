using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The <c>factions debug trespass</c> trace: which passes are a border crossing, and what the
    /// TRESPASS_CROSSING line says about them.
    /// </summary>
    public sealed class TrespassCrossingTests
    {
        private static TrespassCrossingBook.Where In(string tag, int seen) { return new TrespassCrossingBook.Where(tag, false, seen); }
        private static TrespassCrossingBook.Where Outside { get { return TrespassCrossingBook.Where.Nowhere; } }

        [Fact]
        public void AFirstSightingOutsideAnySpaceIsNotACrossing()
        {
            var book = new TrespassCrossingBook();
            TrespassCrossingBook.Where was;
            Assert.False(book.Observe(1, Outside, out was));
            Assert.Equal(0, book.Count);
        }

        [Fact]
        public void EntryStayAndExitAreTwoCrossings()
        {
            var book = new TrespassCrossingBook();
            TrespassCrossingBook.Where was;
            Assert.True(book.Observe(1, In("SPRT", 1), out was));
            Assert.Null(was.Tag);
            Assert.False(book.Observe(1, In("SPRT", 1), out was));   // staying is silent
            Assert.True(book.Observe(1, Outside, out was));
            Assert.Equal("SPRT", was.Tag);
            Assert.Equal(0, book.Count);
        }

        [Fact]
        public void AnUnseenEntryThenItsSightingAreSeparateLines()
        {
            // The hail fires on the sighting; the entry itself is what the hail book cannot show.
            var book = new TrespassCrossingBook();
            TrespassCrossingBook.Where was;
            Assert.True(book.Observe(1, In("SPRT", 0), out was));
            Assert.True(book.Observe(1, In("SPRT", 1), out was));
            Assert.Equal(0, was.Seen);
        }

        [Fact]
        public void SpaceToSpaceIsOneCrossingNamingBoth()
        {
            var book = new TrespassCrossingBook();
            TrespassCrossingBook.Where was;
            book.Observe(1, In("SPRT", 1), out was);
            Assert.True(book.Observe(1, In("MEMN", 0), out was));
            Assert.Equal("SPRT", was.Tag);
        }

        [Fact]
        public void PilotsAreTrackedApart()
        {
            var book = new TrespassCrossingBook();
            TrespassCrossingBook.Where was;
            book.Observe(1, In("SPRT", 1), out was);
            Assert.True(book.Observe(2, In("SPRT", 1), out was));
            Assert.Null(was.Tag);
        }

        [Fact]
        public void LoggingOutInsideASpaceIsAnExit_AndLoggingOutOutsideIsNothing()
        {
            var book = new TrespassCrossingBook();
            TrespassCrossingBook.Where was;
            book.Observe(1, In("SPRT", 1), out was);
            book.Observe(2, Outside, out was);
            book.Observe(3, In("MEMN", 0), out was);

            List<KeyValuePair<long, TrespassCrossingBook.Where>> gone = book.Prune(new HashSet<long> { 3 });
            Assert.Single(gone);
            Assert.Equal(1L, gone[0].Key);
            Assert.Equal("SPRT", gone[0].Value.Tag);
            Assert.Equal(1, book.Count);
            Assert.Empty(book.Prune(new HashSet<long> { 3 }));
        }

        [Fact]
        public void NoSpaceMeansNotAsked_WhateverTheCallerPassed()
        {
            var w = new TrespassCrossingBook.Where(null, true, 1);
            Assert.False(w.PlayerOwned);
            Assert.Equal(-1, w.Seen);
            Assert.True(w.SameAs(TrespassCrossingBook.Where.Nowhere));
        }

        [Fact]
        public void TheLineNamesBothSidesTheSightAndThePosition()
        {
            string line = TrespassCrossingBook.Line(42, 7, Outside, In("SPRT", 0),
                new Vec3(1.24, -2, 3000.04), 98.6, "move");
            Assert.Equal("TRESPASS_CROSSING identity=42 eid=7 from=- to=SPRT wasSeen=- seen=0 posKm=1.2,-2,3000 speed=99 why=move",
                line);
        }

        [Fact]
        public void APlayerFactionsSpaceIsMarked_AndAnOfflineExitHasNoPosition()
        {
            var home = new TrespassCrossingBook.Where("PLYR", true, -1);
            string line = TrespassCrossingBook.Line(42, 0, home, Outside, null, null, "offline");
            Assert.Equal("TRESPASS_CROSSING identity=42 eid=0 from=PLYR(player) to=- wasSeen=- seen=- posKm=- speed=- why=offline",
                line);
        }
    }
}
