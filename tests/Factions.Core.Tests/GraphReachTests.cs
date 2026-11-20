using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class GraphReachTests
    {
        // "hull" is 1-2-3 with a thruster at 3; 9-10 is a fragment hanging off nothing.
        private static readonly Dictionary<int, int[]> Graph = new Dictionary<int, int[]>
        {
            { 1, new[] { 2 } },
            { 2, new[] { 1, 3 } },
            { 3, new[] { 2 } },
            { 9, new[] { 10 } },
            { 10, new[] { 9 } },
        };

        private static IEnumerable<int> Next(int n)
        {
            int[] e;
            return Graph.TryGetValue(n, out e) ? e : Enumerable.Empty<int>();
        }

        private static bool IsThruster(int n) { return n == 3; }

        [Fact]
        public void Reaches_the_goal_across_the_component()
        {
            int visited;
            Assert.True(GraphReach.Reaches(1, Next, IsThruster, 64, out visited));
            Assert.Equal(3, visited);
        }

        [Fact]
        public void A_fragment_does_not_reach_the_goal()
        {
            int visited;
            Assert.False(GraphReach.Reaches(9, Next, IsThruster, 64, out visited));
            Assert.Equal(2, visited);   // the whole fragment was searched, not a budget cut-off
        }

        [Fact]
        public void The_start_node_can_be_the_goal()
        {
            int visited;
            Assert.True(GraphReach.Reaches(3, Next, IsThruster, 64, out visited));
            Assert.Equal(1, visited);
        }

        [Fact]
        public void An_isolated_node_reaches_nothing()
        {
            int visited;
            Assert.False(GraphReach.Reaches(42, Next, IsThruster, 64, out visited));
            Assert.Equal(1, visited);
        }

        [Fact]
        public void The_visit_budget_stops_the_search()
        {
            int visited;
            Assert.False(GraphReach.Reaches(1, Next, IsThruster, 2, out visited));
            Assert.Equal(2, visited);
        }

        [Fact]
        public void A_cycle_is_visited_once_per_node()
        {
            var ring = new Dictionary<int, int[]>
            {
                { 1, new[] { 2, 3 } }, { 2, new[] { 1, 3 } }, { 3, new[] { 1, 2 } },
            };
            int visited;
            Assert.False(GraphReach.Reaches(1, n => ring[n], n => false, 64, out visited));
            Assert.Equal(3, visited);
        }

        // ---- ReachesAll: does an exchange keep the body in one piece --------------------------

        // After a seat (gone) is exchanged for an RC (0): the RC links the hull block 1, and the
        // light 9 whose only link was the seat now hangs off nothing.
        private static readonly Dictionary<int, int[]> Swapped = new Dictionary<int, int[]>
        {
            { 0, new[] { 1 } },
            { 1, new[] { 0, 2 } },
            { 2, new[] { 1, 3 } },
            { 3, new[] { 2 } },
            { 9, new int[0] },
        };

        private static IEnumerable<int> SwappedNext(int n)
        {
            int[] e;
            return Swapped.TryGetValue(n, out e) ? e : Enumerable.Empty<int>();
        }

        [Fact]
        public void ReachesAll_fails_when_a_former_neighbour_is_cut_off()
        {
            int visited;
            Assert.False(GraphReach.ReachesAll(0, SwappedNext, new List<int> { 1, 9 }, IsThruster, 64, out visited));
        }

        [Fact]
        public void ReachesAll_passes_when_every_former_neighbour_still_connects()
        {
            int visited;
            Assert.True(GraphReach.ReachesAll(0, SwappedNext, new List<int> { 1, 2 }, IsThruster, 64, out visited));
        }

        [Fact]
        public void ReachesAll_needs_the_goal_as_well()
        {
            // Every former neighbour connected, but no thruster in the component.
            int visited;
            Assert.False(GraphReach.ReachesAll(9, SwappedNext, new List<int> { 9 }, IsThruster, 64, out visited));
            Assert.True(GraphReach.ReachesAll(9, SwappedNext, new List<int> { 9 }, null, 64, out visited));
        }

        [Fact]
        public void ReachesAll_stops_once_both_are_settled()
        {
            // Required 1 and the thruster 3 are both found by the fourth visit; nothing further is read.
            int visited;
            Assert.True(GraphReach.ReachesAll(0, SwappedNext, new List<int> { 1 }, IsThruster, 64, out visited));
            Assert.Equal(4, visited);
        }

        [Fact]
        public void ReachesAll_out_of_budget_is_a_failure()
        {
            int visited;
            Assert.False(GraphReach.ReachesAll(0, SwappedNext, new List<int> { 3 }, IsThruster, 2, out visited));
            Assert.Equal(2, visited);
        }
    }
}
