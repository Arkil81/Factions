using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class RelationTraceBooksTests
    {
        [Fact]
        public void ChangeBook_ReportsFirstSightAndChangesOnly()
        {
            var book = new ChangeBook();
            string prev;
            Assert.True(book.Changed("1|UNGD", "table/missing/Enemy", out prev));
            Assert.Null(prev);
            Assert.False(book.Changed("1|UNGD", "table/missing/Enemy", out prev));
            Assert.True(book.Changed("1|UNGD", "no-viewer/-/Neutral", out prev));
            Assert.Equal("table/missing/Enemy", prev);
            Assert.True(book.Changed("2|UNGD", "table/missing/Enemy", out prev));
            Assert.Equal(2, book.Count);
            book.Clear();
            Assert.True(book.Changed("1|UNGD", "no-viewer/-/Neutral", out prev));
            Assert.Null(prev);
        }

        [Fact]
        public void Tally_AFreshSphereIsFirstSends_NotShifts()
        {
            var t = new RelationShiftTally();
            t.Add("UNGD", null, "Enemy");
            t.Add("IMPR", null, "Enemy");
            Assert.Equal(2, t.Sent);
            Assert.Equal(2, t.First);
            Assert.Equal(0, t.Changed);
            Assert.Equal("-", t.FormatShifts());
        }

        [Fact]
        public void Tally_GroupsShiftsByOwnerAndTransition()
        {
            // The reported event: a client holding white contacts is sent them red.
            var t = new RelationShiftTally();
            t.Add("UNGD", "Neutral", "Enemy");
            t.Add("UNGD", "Neutral", "Enemy");
            t.Add("IMPR", "Neutral", "Enemy");
            t.Add("IMPR", "Enemy", "Enemy");
            t.Add(null, "Unknown", "Neutral");
            Assert.Equal(5, t.Sent);
            Assert.Equal(0, t.First);
            Assert.Equal(4, t.Changed);
            Assert.Equal("-:Unknown>Neutral*1,IMPR:Neutral>Enemy*1,UNGD:Neutral>Enemy*2", t.FormatShifts());
        }
    }
}
