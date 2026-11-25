using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Factions.Core
{
    /// <summary>What <see cref="ObjectGraphWalker"/> does with an object it reaches.</summary>
    public enum WalkVerdict
    {
        /// <summary>Walk its fields, items or delegate targets.</summary>
        Expand,
        /// <summary>Do not walk into it.</summary>
        Leaf,
        /// <summary>Record the path that reached it, under the category the classifier names; do not walk into it.</summary>
        Hit,
    }

    /// <summary>Decides what the walker does with <paramref name="o"/>; <paramref name="category"/> is read on a hit.</summary>
    public delegate WalkVerdict WalkClassifier(object o, out int category);

    /// <summary>
    /// Breadth-first walk of an object graph from named roots, through instance fields, collection items and
    /// delegate targets, recording the path that first reaches each object the classifier calls a hit. Every
    /// object is reached first by one of its shortest paths, so a holder near a root is found before the walk
    /// spends its budget anywhere deeper, and each hit object is counted once. Paths collapse collection indices:
    /// <c>Type.Field.inner[]</c>, <c>[k]</c>/<c>[v]</c> for a dictionary key/value, <c>->Method</c> for a delegate
    /// target. Bounded by depth, discovered objects and time; <see cref="CompleteDepth"/> says how deep the
    /// answer is exhaustive when a bound stops it. Not thread-safe; reflection only, so it reads but never runs
    /// the walked code (a collection's enumerator excepted).
    /// </summary>
    public sealed class ObjectGraphWalker
    {
        private struct Entry
        {
            public object Obj;
            public int Path;
            public int Depth;
        }

        private sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }

        private const int MaxItemsPerCollection = 2000000;

        private readonly WalkClassifier _classify;
        private readonly int _maxDepth;
        private readonly int _maxObjects;
        private readonly long _maxTicks;

        private readonly HashSet<object> _seen = new HashSet<object>(new RefEq());
        private readonly Queue<Entry> _queue = new Queue<Entry>();
        private readonly List<string> _paths = new List<string>();
        private readonly Dictionary<string, int> _suffixIds = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<long, int> _children = new Dictionary<long, int>();
        private readonly Dictionary<int, int>[] _hitPaths;
        private readonly int[] _hits;
        private readonly Dictionary<Type, FieldInfo[]> _fields = new Dictionary<Type, FieldInfo[]>();
        private readonly Dictionary<Type, PropertyInfo[]> _keyValue = new Dictionary<Type, PropertyInfo[]>();
        private int _droppedDepth = int.MaxValue;

        /// <param name="classify">Called once per distinct object reached.</param>
        /// <param name="categories">How many hit categories the classifier names (0..categories-1).</param>
        /// <param name="maxDepth">Hops from a root; an object this deep is classified but not walked.</param>
        /// <param name="maxObjects">Distinct objects the walk may discover.</param>
        /// <param name="maxSeconds">Wall-clock budget for <see cref="Run"/>.</param>
        public ObjectGraphWalker(WalkClassifier classify, int categories, int maxDepth, int maxObjects, double maxSeconds)
        {
            if (classify == null) throw new ArgumentNullException("classify");
            _classify = classify;
            _maxDepth = Math.Max(1, maxDepth);
            _maxObjects = Math.Max(1, maxObjects);
            _maxTicks = (long)(Math.Max(0.0, maxSeconds) * Stopwatch.Frequency);
            int n = Math.Max(1, categories);
            _hitPaths = new Dictionary<int, int>[n];
            for (int i = 0; i < n; i++) _hitPaths[i] = new Dictionary<int, int>();
            _hits = new int[n];
            CompleteDepth = _maxDepth;
        }

        /// <summary>Objects whose contents were walked.</summary>
        public int Expanded { get; private set; }

        /// <summary>Distinct reference objects reached.</summary>
        public int Discovered { get; private set; }

        /// <summary>True when the object or time bound stopped the walk before the depth bound did.</summary>
        public bool Partial { get; private set; }

        /// <summary>Every hit at this many hops or fewer was found; equals the depth bound unless <see cref="Partial"/>.</summary>
        public int CompleteDepth { get; private set; }

        /// <summary>Distinct objects hit in <paramref name="category"/>.</summary>
        public int Hits(int category) { return _hits[category]; }

        /// <summary>Paths that first reached a hit in <paramref name="category"/>, with how many distinct hits each reached, most first.</summary>
        public List<KeyValuePair<string, int>> HitPaths(int category)
        {
            var list = new List<KeyValuePair<string, int>>(_hitPaths[category].Count);
            foreach (KeyValuePair<int, int> kv in _hitPaths[category])
                list.Add(new KeyValuePair<string, int>(_paths[kv.Key], kv.Value));
            list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            return list;
        }

        /// <summary>Walks from every root at once: all of depth 1 before any of depth 2.</summary>
        public void Run(IEnumerable<KeyValuePair<string, object>> roots)
        {
            long deadline = Stopwatch.GetTimestamp() + _maxTicks;
            foreach (KeyValuePair<string, object> root in roots)
            {
                if (root.Value == null) continue;
                _paths.Add(root.Key ?? "?");
                Discover(root.Value, _paths.Count - 1, 0);
            }
            while (_queue.Count > 0)
            {
                if ((Expanded & 1023) == 0 && Stopwatch.GetTimestamp() > deadline)
                {
                    Partial = true;
                    CompleteDepth = Math.Min(CompleteDepth, _queue.Peek().Depth);
                    break;
                }
                Entry e = _queue.Dequeue();
                Expanded++;
                Expand(e);
            }
            if (_droppedDepth != int.MaxValue) CompleteDepth = Math.Min(CompleteDepth, _droppedDepth);
            Discovered = _seen.Count;
            _queue.Clear();
            _seen.Clear();
        }

        private void Discover(object v, int path, int depth)
        {
            Type t = v.GetType();
            if (IsOpaque(t, v)) return;
            if (!t.IsValueType && !_seen.Add(v)) return;
            int category;
            WalkVerdict verdict = _classify(v, out category);
            if (verdict == WalkVerdict.Hit)
            {
                if (category < 0 || category >= _hits.Length) return;
                _hits[category]++;
                int c;
                _hitPaths[category].TryGetValue(path, out c);
                _hitPaths[category][path] = c + 1;
                return;
            }
            if (verdict == WalkVerdict.Leaf || depth >= _maxDepth) return;
            if (_seen.Count > _maxObjects)
            {
                Partial = true;
                if (depth < _droppedDepth) _droppedDepth = depth;
                return;
            }
            _queue.Enqueue(new Entry { Obj = v, Path = path, Depth = depth });
        }

        private void Expand(Entry e)
        {
            object v = e.Obj;
            int next = e.Depth + 1;

            var del = v as Delegate;
            if (del != null)
            {
                Delegate[] list;
                try { list = del.GetInvocationList(); } catch { return; }
                foreach (Delegate d in list)
                    if (d.Target != null) Discover(d.Target, Child(e.Path, "->" + d.Method.Name), next);
                return;
            }

            Type t = v.GetType();
            if (t.IsArray && t.GetElementType().IsPrimitive) return;
            var en = v as IEnumerable;
            if (en != null)
            {
                int i = 0;
                try
                {
                    foreach (object item in en)
                    {
                        if (++i > MaxItemsPerCollection) break;
                        if (item == null) continue;
                        PropertyInfo[] kv = KeyValue(item.GetType());
                        if (kv != null)
                        {
                            object k = kv[0].GetValue(item, null), val = kv[1].GetValue(item, null);
                            if (k != null) Discover(k, Child(e.Path, "[k]"), next);
                            if (val != null) Discover(val, Child(e.Path, "[v]"), next);
                        }
                        else Discover(item, Child(e.Path, "[]"), next);
                    }
                }
                catch { }
                return;
            }

            foreach (FieldInfo f in Fields(t))
            {
                object fv;
                try { fv = f.GetValue(v); } catch { continue; }
                if (fv != null) Discover(fv, Child(e.Path, "." + f.Name), next);
            }
        }

        private int Child(int parent, string suffix)
        {
            int s;
            if (!_suffixIds.TryGetValue(suffix, out s)) { s = _suffixIds.Count; _suffixIds[suffix] = s; }
            long key = ((long)parent << 32) | (uint)s;
            int id;
            if (_children.TryGetValue(key, out id)) return id;
            _paths.Add(_paths[parent] + suffix);
            id = _paths.Count - 1;
            _children[key] = id;
            return id;
        }

        private static bool IsOpaque(Type t, object v)
        {
            return t.IsPrimitive || t.IsEnum || v is string || v is Type || v is MemberInfo || v is Assembly
                || v is Pointer || v is WeakReference || t == typeof(IntPtr) || t == typeof(UIntPtr);
        }

        private PropertyInfo[] KeyValue(Type t)
        {
            PropertyInfo[] kv;
            if (_keyValue.TryGetValue(t, out kv)) return kv;
            kv = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
                ? new[] { t.GetProperty("Key"), t.GetProperty("Value") } : null;
            _keyValue[t] = kv;
            return kv;
        }

        private FieldInfo[] Fields(Type t)
        {
            FieldInfo[] fs;
            if (_fields.TryGetValue(t, out fs)) return fs;
            var list = new List<FieldInfo>();
            for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!f.FieldType.IsPrimitive && !f.FieldType.IsEnum && f.FieldType != typeof(string)
                        && !f.FieldType.IsPointer && f.FieldType != typeof(IntPtr))
                        list.Add(f);
            fs = list.ToArray();
            _fields[t] = fs;
            return fs;
        }
    }
}
