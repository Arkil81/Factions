using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CostTallyTests
    {
        [Fact]
        public void AddAccumulatesTicksAndCallsPerKey()
        {
            var t = new CostTally<long>();
            t.Add(7, 10);
            t.Add(7, 5);
            t.Add(9, 1);

            CostTally<long>.Entry e;
            Assert.True(t.TryGet(7, out e));
            Assert.Equal(15, e.Ticks);
            Assert.Equal(2, e.Calls);
            Assert.Equal(16, t.TotalTicks);
            Assert.Equal(3, t.TotalCalls);
            Assert.Equal(2, t.Count);
        }

        [Fact]
        public void MergeIntoSumsOverlappingKeysAndCopiesNewOnes()
        {
            var a = new CostTally<long>();
            a.Add(1, 10);
            a.Add(2, 20);
            var b = new CostTally<long>();
            b.Add(2, 5);
            b.Add(3, 7);

            a.MergeInto(b);

            CostTally<long>.Entry e;
            Assert.True(b.TryGet(2, out e));
            Assert.Equal(25, e.Ticks);
            Assert.Equal(2, e.Calls);
            Assert.True(b.TryGet(1, out e));
            Assert.Equal(10, e.Ticks);
            Assert.Equal(42, b.TotalTicks);
            // the source is left as it was; clearing it is the caller's job
            Assert.Equal(30, a.TotalTicks);
        }

        [Fact]
        public void MergeIntoItselfChangesNothing()
        {
            var a = new CostTally<long>();
            a.Add(1, 10);
            a.MergeInto(a);
            Assert.Equal(10, a.TotalTicks);
            Assert.Equal(1, a.TotalCalls);
        }

        [Fact]
        public void TopRanksByTotalTicksNotAverage()
        {
            var t = new CostTally<string>();
            // one expensive call ...
            t.Add("rare", 50);
            // ... against many cheap ones that add up to more
            for (int i = 0; i < 10; i++) t.Add("every-frame", 6);
            t.Add("small", 1);

            var top = t.Top(2);

            Assert.Equal(2, top.Count);
            Assert.Equal("every-frame", top[0].Key);
            Assert.Equal("rare", top[1].Key);
        }

        [Fact]
        public void TopReturnsEverythingWhenFewerKeysThanRequested()
        {
            var t = new CostTally<long>();
            t.Add(1, 3);
            Assert.Single(t.Top(20));
        }

        [Fact]
        public void SumBySplitsEveryKeyNotJustTheTop()
        {
            var t = new CostTally<long>();
            t.Add(1, 10);
            t.Add(2, 20);
            t.Add(3, 5);
            t.Add(4, 7);
            var into = new long[2];
            t.SumBy(k => k % 2 == 0 ? 0 : 1, into);
            Assert.Equal(27, into[0]);
            Assert.Equal(15, into[1]);
        }

        [Fact]
        public void SumBySkipsABucketOutOfRange()
        {
            var t = new CostTally<long>();
            t.Add(1, 10);
            t.Add(2, 20);
            var into = new long[1];
            t.SumBy(k => k == 1 ? 0 : 5, into);
            Assert.Equal(10, into[0]);
        }

        [Fact]
        public void ClearEmptiesTheTally()
        {
            var t = new CostTally<long>();
            t.Add(1, 3);
            t.Clear();
            Assert.Equal(0, t.Count);
            Assert.Equal(0, t.TotalTicks);
        }
    }
}
