using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Sandbox.Game.Entities;

namespace Factions
{
    /// <summary>
    /// <c>factions bench roots</c>: walks the object graph from every static field of the game, engine, Torch and
    /// plugin assemblies, a few hops deep through fields, collections and delegates, and reports the paths that
    /// reach a voxel map or a cube grid that has already closed. A closed entity is garbage unless something rooted
    /// holds it, so any path found is the holder. <c>depth=N</c> (1-10, default 4) sets the hop limit and
    /// <c>all=1</c> walks every loaded assembly except the framework's. Bounded by depth, objects visited and
    /// time; a test-server diagnostic that takes seconds on the game thread. GC handles and stacks are not walked:
    /// a holder only they reach shows up in <c>factions bench dump</c>, not here.
    /// </summary>
    internal static class BenchRoots
    {
        private const int DefaultDepth = 4;
        private static int _maxDepth = DefaultDepth;
        private const int MaxObjects = 1500000;
        private const double MaxSeconds = 20.0;
        private static long _deadline;
        private const int MaxItemsPerCollection = 2000000;

        private sealed class RefEq : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }

        private static HashSet<object> _seen;
        private static Dictionary<string, int> _hits;
        private static Dictionary<string, int> _gridHits;
        private static int _visited;
        private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();

        internal static string Run(string[] args)
        {
            _maxDepth = DefaultDepth;
            bool all = false;
            for (int i = 3; args != null && i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                int d;
                if (a.StartsWith("depth=", StringComparison.Ordinal)
                    && int.TryParse(a.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out d))
                    _maxDepth = Math.Max(1, Math.Min(10, d));
                else if (a == "all=1") all = true;
            }
            long t0 = Stopwatch.GetTimestamp();
            _seen = new HashSet<object>(new RefEq());
            _hits = new Dictionary<string, int>(StringComparer.Ordinal);
            _gridHits = new Dictionary<string, int>(StringComparer.Ordinal);
            _visited = 0;
            _deadline = t0 + (long)(MaxSeconds * Stopwatch.Frequency);
            int statics = 0;
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string n = asm.GetName().Name;
                    if (all ? IsFramework(n) : !(n.StartsWith("Sandbox", StringComparison.Ordinal) || n.StartsWith("VRage", StringComparison.Ordinal)
                          || n.StartsWith("SpaceEngineers", StringComparison.Ordinal) || n.StartsWith("Torch", StringComparison.Ordinal)
                          || n.StartsWith("Factions", StringComparison.Ordinal) || n.StartsWith("0Harmony", StringComparison.Ordinal)))
                        continue;
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }
                    foreach (Type t in types)
                    {
                        if (t.ContainsGenericParameters) continue;
                        FieldInfo[] fs;
                        try { fs = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                        catch { continue; }
                        foreach (FieldInfo f in fs)
                        {
                            if (f.IsLiteral || f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string)) continue;
                            if (f.IsDefined(typeof(ThreadStaticAttribute), false)) continue;
                            object v;
                            try { v = f.GetValue(null); } catch { continue; }
                            if (v == null) continue;
                            statics++;
                            Visit(v, t.Name + "." + f.Name, 0);
                            if (_visited >= MaxObjects) break;
                        }
                        if (_visited >= MaxObjects) break;
                    }
                    if (_visited >= MaxObjects) break;
                }
            }
            catch (Exception ex)
            {
                return "bench roots: failed after " + _visited + " objects: " + ex.Message;
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            var sb = new StringBuilder("bench roots: statics=").Append(statics).Append(" visited=").Append(_visited)
                .Append(_visited >= MaxObjects ? " (capped by objects or time; partial)" : "").Append(" ms=").Append(ms.ToString("0", CultureInfo.InvariantCulture))
                .Append(" depth=").Append(_maxDepth).Append(all ? " all" : "")
                .Append(" closedVoxelPaths=").Append(_hits.Count);
            foreach (KeyValuePair<string, int> kv in _hits.OrderByDescending(k => k.Value).Take(12))
                sb.Append(" | ").Append(kv.Key).Append('=').Append(kv.Value);
            sb.Append(" || closedGridPaths=").Append(_gridHits.Count);
            foreach (KeyValuePair<string, int> kv in _gridHits.OrderByDescending(k => k.Value).Take(12))
                sb.Append(" | ").Append(kv.Key).Append('=').Append(kv.Value);
            _seen = null;
            return sb.ToString();
        }

        private static void Visit(object v, string path, int depth)
        {
            if (v == null || _visited >= MaxObjects) return;
            if ((_visited & 1023) == 0 && Stopwatch.GetTimestamp() > _deadline) { _visited = MaxObjects; return; }
            Type t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || v is string || v is Type || v is MemberInfo || v is Assembly || v is Pointer) return;
            if (v is WeakReference) return;

            var voxel = v as MyVoxelBase;
            if (voxel != null)
            {
                if (voxel.Closed)
                {
                    int c;
                    _hits.TryGetValue(path, out c);
                    _hits[path] = c + 1;
                }
                return;   // an open voxel is legitimately held; its own fields are not the question
            }
            var grid = v as MyCubeGrid;
            if (grid != null)
            {
                if (grid.Closed)
                {
                    int c;
                    _gridHits.TryGetValue(path, out c);
                    _gridHits[path] = c + 1;
                }
                return;   // an open grid is legitimately held
            }
            if (!t.IsValueType && !_seen.Add(v)) return;
            _visited++;
            if (depth >= _maxDepth) return;

            var del = v as Delegate;
            if (del != null)
            {
                Delegate[] list;
                try { list = del.GetInvocationList(); } catch { return; }
                foreach (Delegate d in list)
                    if (d.Target != null) Visit(d.Target, path + "->" + d.Method.Name, depth + 1);
                return;
            }

            if (t.IsArray && t.GetElementType().IsPrimitive) return;
            var en = v as IEnumerable;
            if (en != null)
            {
                int i = 0;
                try
                {
                    foreach (object item in en)
                    {
                        if (++i > MaxItemsPerCollection || _visited >= MaxObjects) break;
                        if (item == null) continue;
                        Type it = item.GetType();
                        PropertyInfo[] kv = KeyValue(it);
                        if (kv != null)
                        {
                            Visit(kv[0].GetValue(item, null), path + "[k]", depth + 1);
                            Visit(kv[1].GetValue(item, null), path + "[v]", depth + 1);
                        }
                        else Visit(item, path + "[]", depth + 1);
                    }
                }
                catch { }
                return;
            }

            foreach (FieldInfo f in Fields(t))
            {
                object fv;
                try { fv = f.GetValue(v); } catch { continue; }
                if (fv != null) Visit(fv, path + "." + f.Name, depth + 1);
            }
        }

        private static bool IsFramework(string name)
        {
            return name == "mscorlib" || name == "netstandard" || name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("Microsoft", StringComparison.Ordinal);
        }

        private static readonly Dictionary<Type, PropertyInfo[]> KvCache = new Dictionary<Type, PropertyInfo[]>();

        private static PropertyInfo[] KeyValue(Type t)
        {
            PropertyInfo[] kv;
            if (KvCache.TryGetValue(t, out kv)) return kv;
            kv = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
                ? new[] { t.GetProperty("Key"), t.GetProperty("Value") } : null;
            KvCache[t] = kv;
            return kv;
        }

        private static FieldInfo[] Fields(Type t)
        {
            FieldInfo[] fs;
            if (FieldCache.TryGetValue(t, out fs)) return fs;
            var list = new List<FieldInfo>();
            for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                foreach (FieldInfo f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!f.FieldType.IsPrimitive && !f.FieldType.IsEnum && f.FieldType != typeof(string)
                        && !f.FieldType.IsPointer && f.FieldType != typeof(IntPtr))
                        list.Add(f);
            fs = list.ToArray();
            FieldCache[t] = fs;
            return fs;
        }
    }
}
