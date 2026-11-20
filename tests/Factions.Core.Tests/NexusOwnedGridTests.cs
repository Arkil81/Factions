using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>An agent's player-owned grids reach the authority's assets window through its report.</summary>
    public class NexusOwnedGridTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 20, 19, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(120);

        private static NexusOwnedGrid Grid(long id, long owner)
        {
            return new NexusOwnedGrid { Id = id, Owner = owner, Name = "g" + id, Blocks = 40 };
        }

        [Fact]
        public void AGridArrivesWithEveryFieldTheWindowShows()
        {
            var sent = new NexusOwnedGrid
            {
                Id = 87654321012345L, Name = "Prospector", Static = true, Size = 1, Blocks = 312,
                X = 1234567, Y = -98765, Z = 4321, Owner = 144115188075855900L, OwnerName = "Pilot",
            };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(
                new NexusAgentReport { Session = "s", OwnedGrids = new List<NexusOwnedGrid> { sent } }.Encode(), out back));

            NexusOwnedGrid g = Assert.Single(back.OwnedGrids);
            Assert.Equal(sent.Id, g.Id);
            Assert.Equal("Prospector", g.Name);
            Assert.True(g.Static);
            Assert.Equal((byte)1, g.Size);
            Assert.Equal(312, g.Blocks);
            Assert.Equal(1234567.0, g.X);
            Assert.Equal(-98765.0, g.Y);
            Assert.Equal(4321.0, g.Z);
            Assert.Equal(sent.Owner, g.Owner);
            Assert.Equal("Pilot", g.OwnerName);
        }

        /// <summary>A report between owned-grid reports carries none, and must decode as null rather
        /// than empty, or every one of them would clear the authority's list.</summary>
        [Fact]
        public void AReportWithoutGridsDecodesAsNullAndAnEmptyListAsEmpty()
        {
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(new NexusAgentReport { Session = "s" }.Encode(), out back));
            Assert.Null(back.OwnedGrids);

            Assert.True(NexusAgentReport.TryDecode(
                new NexusAgentReport { Session = "s", OwnedGrids = new List<NexusOwnedGrid>() }.Encode(), out back));
            Assert.NotNull(back.OwnedGrids);
            Assert.Empty(back.OwnedGrids);
        }

        [Fact]
        public void NullKeepsTheLastListAndAListReplacesIt()
        {
            var book = new NexusOwnedGridBook();
            book.Update(2, new List<NexusOwnedGrid> { Grid(1, 10), Grid(2, 10) }, T0);
            book.Update(2, null, T0.AddSeconds(1));
            Assert.Equal(2, book.Fresh(T0.AddSeconds(1), MaxAge).Count);

            book.Update(2, new List<NexusOwnedGrid> { Grid(3, 10) }, T0.AddSeconds(30));
            Assert.Equal(3L, Assert.Single(book.Fresh(T0.AddSeconds(30), MaxAge)).Id);

            book.Update(2, new List<NexusOwnedGrid>(), T0.AddSeconds(60));
            Assert.Empty(book.Fresh(T0.AddSeconds(60), MaxAge));
        }

        [Fact]
        public void EachAgentKeepsItsOwnListAndAQuietOneAgesOut()
        {
            var book = new NexusOwnedGridBook();
            book.Update(2, new List<NexusOwnedGrid> { Grid(1, 10) }, T0);
            book.Update(4, new List<NexusOwnedGrid> { Grid(2, 11) }, T0.AddSeconds(100));

            Assert.Equal(2, book.Fresh(T0.AddSeconds(110), MaxAge).Count);
            // Server 2 last reported 121 s ago: its grids go, server 4's stay.
            Assert.Equal(2L, Assert.Single(book.Fresh(T0.AddSeconds(121), MaxAge)).Id);
        }

        [Fact]
        public void AnUnownedGridIsNotListed()
        {
            var book = new NexusOwnedGridBook();
            book.Update(2, new List<NexusOwnedGrid> { Grid(1, 0), null, Grid(2, 10) }, T0);
            Assert.Equal(2L, Assert.Single(book.Fresh(T0, MaxAge)).Id);
        }
    }
}
