using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class ObjectGraphWalkerTests
    {
        private sealed class Node
        {
            public object A;
            public object B;
        }

        /// <summary>Stands in for an SE entity: a closed one is the hit, an open one is a leaf.</summary>
        private sealed class Entity
        {
            public bool Closed;
            public object Held;
        }

        private sealed class Owner
        {
            public Action Callback;
        }

        private static WalkVerdict Classify(object o, out int category)
        {
            category = 0;
            var e = o as Entity;
            if (e == null) return WalkVerdict.Expand;
            return e.Closed ? WalkVerdict.Hit : WalkVerdict.Leaf;
        }

        private static ObjectGraphWalker Walk(int depth, int objects, params KeyValuePair<string, object>[] roots)
        {
            var w = new ObjectGraphWalker(Classify, 1, depth, objects, 30.0);
            w.Run(roots);
            return w;
        }

        private static KeyValuePair<string, object> Root(string name, object v)
        {
            return new KeyValuePair<string, object>(name, v);
        }

        [Fact]
        public void A_hit_is_credited_to_its_shortest_path_and_counted_once()
        {
            var closed = new Entity { Closed = true };
            var deep = new Node { A = new Node { A = new Node { A = closed } } };
            var shallow = new Node { B = closed };

            var w = Walk(6, 1000, Root("S.Deep", deep), Root("S.Shallow", shallow));

            Assert.Equal(1, w.Hits(0));
            var paths = w.HitPaths(0);
            Assert.Single(paths);
            Assert.Equal("S.Shallow.B", paths[0].Key);
            Assert.False(w.Partial);
        }

        [Fact]
        public void An_object_first_reached_at_the_depth_limit_is_still_walked_from_a_shorter_path()
        {
            // A depth-first walk marks `shared` seen at the depth limit through the first root, never expands it,
            // and then skips it when the second root reaches it at depth 1: the hit behind it is never found.
            var closed = new Entity { Closed = true };
            var shared = new Node { A = closed };
            var first = new Node { A = new Node { A = new Node { A = shared } } };
            var second = new Node { A = shared };

            var w = Walk(3, 1000, Root("S.First", first), Root("S.Second", second));

            Assert.Equal(1, w.Hits(0));
            Assert.Equal("S.Second.A.A", w.HitPaths(0)[0].Key);
        }

        [Fact]
        public void A_leaf_is_not_walked_into()
        {
            var closed = new Entity { Closed = true };
            var open = new Entity { Closed = false, Held = closed };

            var w = Walk(6, 1000, Root("S.Open", open));

            Assert.Equal(0, w.Hits(0));
        }

        [Fact]
        public void Collection_paths_collapse_indices_and_name_keys_and_values()
        {
            var list = new List<object> { new Entity { Closed = true }, new Entity { Closed = true } };
            var dict = new Dictionary<object, object> { { new Entity { Closed = true }, new Entity { Closed = true } } };

            var w = Walk(4, 1000, Root("S.List", list), Root("S.Dict", dict));

            Assert.Equal(4, w.Hits(0));
            var paths = w.HitPaths(0).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(2, paths["S.List[]"]);
            Assert.Equal(1, paths["S.Dict[k]"]);
            Assert.Equal(1, paths["S.Dict[v]"]);
        }

        [Fact]
        public void A_delegate_target_is_named_by_its_method()
        {
            var target = new HoldsClosed { Held = new Entity { Closed = true } };
            var holder = new Owner { Callback = target.Run };

            var w = Walk(4, 1000, Root("S.Holder", holder));

            Assert.Equal("S.Holder.Callback->Run.Held", w.HitPaths(0)[0].Key);
        }

        private sealed class HoldsClosed
        {
            public Entity Held;
            public void Run() { }
        }

        [Fact]
        public void The_object_bound_reports_how_deep_the_answer_is_complete()
        {
            // 20 roots, each a chain of 10 nodes ending in a hit: 200+ objects against a bound of 50.
            var roots = new List<KeyValuePair<string, object>>();
            for (int r = 0; r < 20; r++)
            {
                object tail = new Entity { Closed = true };
                for (int i = 0; i < 10; i++) tail = new Node { A = tail };
                roots.Add(Root("S.R" + r, tail));
            }

            var w = new ObjectGraphWalker(Classify, 1, 16, 50, 30.0);
            w.Run(roots);

            Assert.True(w.Partial);
            Assert.True(w.CompleteDepth < 16);
            Assert.Equal(0, w.Hits(0));
        }

        [Fact]
        public void Without_a_bound_reached_the_answer_is_complete_to_the_depth_limit()
        {
            var w = Walk(5, 1000, Root("S.X", new Node { A = new Node() }));

            Assert.False(w.Partial);
            Assert.Equal(5, w.CompleteDepth);
        }

        [Fact]
        public void A_cycle_is_walked_once()
        {
            var a = new Node();
            var b = new Node { A = a };
            a.A = b;
            a.B = new Entity { Closed = true };

            var w = Walk(10, 1000, Root("S.A", a));

            Assert.Equal(1, w.Hits(0));
            Assert.Equal(2, w.Expanded);
            // Read after Run returns: a, b and the closed entity.
            Assert.Equal(3, w.Discovered);
        }
    }
}
