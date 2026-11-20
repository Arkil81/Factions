using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Breadth-first reachability over a graph supplied by the caller. Exists so a host that
    /// holds a native adjacency list — SE's block neighbours, for one — can ask whether a node is
    /// connected to something specific without that question living in the host.</summary>
    public static class GraphReach
    {
        /// <summary>Whether <paramref name="goal"/> holds for <paramref name="start"/> or for any node
        /// reachable from it, visiting at most <paramref name="maxVisits"/> nodes.
        /// <paramref name="visited"/> reports how many were visited, so a caller can tell a node that
        /// reached its goal through the whole hull from one that reached it two blocks away, and a
        /// search that ran out of budget from one that exhausted the component.</summary>
        public static bool Reaches<TNode>(TNode start, Func<TNode, IEnumerable<TNode>> neighbours,
                                          Func<TNode, bool> goal, int maxVisits, out int visited)
        {
            visited = 0;
            if (start == null || neighbours == null || goal == null || maxVisits <= 0) return false;

            var seen = new HashSet<TNode>();
            var queue = new Queue<TNode>();
            seen.Add(start);
            queue.Enqueue(start);

            while (queue.Count > 0 && visited < maxVisits)
            {
                TNode node = queue.Dequeue();
                visited++;
                if (goal(node)) return true;

                IEnumerable<TNode> next = neighbours(node);
                if (next == null) continue;
                foreach (TNode n in next)
                    if (n != null && seen.Add(n)) queue.Enqueue(n);
            }
            return false;
        }

        /// <summary>Whether every node in <paramref name="required"/> is reachable from
        /// <paramref name="start"/>, and — when <paramref name="goal"/> is given — some reachable node
        /// satisfies it. One search answers both, stopping as soon as both are settled.
        ///
        /// <para>This is the test for a block exchange that must not split a body: the body stays in
        /// one piece exactly when everything the removed node touched is still connected to what
        /// replaced it, because every path that ran through the removed node ran between two of its
        /// neighbours.</para></summary>
        public static bool ReachesAll<TNode>(TNode start, Func<TNode, IEnumerable<TNode>> neighbours,
                                             IList<TNode> required, Func<TNode, bool> goal,
                                             int maxVisits, out int visited)
        {
            visited = 0;
            if (start == null || neighbours == null || maxVisits <= 0) return false;

            var pending = new HashSet<TNode>();
            if (required != null)
                for (int i = 0; i < required.Count; i++)
                    if (required[i] != null) pending.Add(required[i]);
            bool goalMet = goal == null;

            var seen = new HashSet<TNode>();
            var queue = new Queue<TNode>();
            seen.Add(start);
            queue.Enqueue(start);

            while (queue.Count > 0 && visited < maxVisits)
            {
                TNode node = queue.Dequeue();
                visited++;
                pending.Remove(node);
                if (!goalMet && goal(node)) goalMet = true;
                if (goalMet && pending.Count == 0) return true;

                IEnumerable<TNode> next = neighbours(node);
                if (next == null) continue;
                foreach (TNode n in next)
                    if (n != null && seen.Add(n)) queue.Enqueue(n);
            }
            return false;
        }
    }
}
