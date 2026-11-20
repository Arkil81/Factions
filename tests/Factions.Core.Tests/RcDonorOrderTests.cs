using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers <see cref="RcDonorOrder"/> — which armor cube a small grid gives up for its
    /// remote control.</summary>
    public class RcDonorOrderTests
    {
        [Fact]
        public void Buried_cubes_come_first_whatever_their_distance()
        {
            var faces = new List<int> { 3, 6, 5 };
            var dist  = new List<double> { 1, 900, 4 };
            Assert.Equal(new List<int> { 1, 2, 0 }, RcDonorOrder.Order(faces, dist, 10));
        }

        [Fact]
        public void Equal_cover_goes_to_the_nearest()
        {
            var faces = new List<int> { 6, 6, 6 };
            var dist  = new List<double> { 50, 2, 9 };
            Assert.Equal(new List<int> { 1, 2, 0 }, RcDonorOrder.Order(faces, dist, 10));
        }

        [Fact]
        public void A_full_tie_keeps_index_order_so_a_hull_always_gets_the_same_cell()
        {
            var faces = new List<int> { 4, 4 };
            var dist  = new List<double> { 7, 7 };
            Assert.Equal(new List<int> { 0, 1 }, RcDonorOrder.Order(faces, dist, 10));
        }

        [Fact]
        public void The_list_is_capped()
        {
            var faces = new List<int> { 1, 2, 3, 4 };
            var dist  = new List<double> { 0, 0, 0, 0 };
            Assert.Equal(new List<int> { 3, 2 }, RcDonorOrder.Order(faces, dist, 2));
        }

        [Fact]
        public void Nothing_offered_gives_an_empty_order()
        {
            Assert.Empty(RcDonorOrder.Order(new List<int>(), new List<double>(), 10));
            Assert.Empty(RcDonorOrder.Order(null, null, 10));
        }
    }
}
