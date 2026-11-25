using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Factions.Core;
using Sandbox.Game.Entities;

namespace Factions
{
    /// <summary>
    /// <c>factions bench roots</c>: walks the object graph breadth-first from every static field of the game, engine,
    /// Torch and plugin assemblies through fields, collections and delegates (<see cref="ObjectGraphWalker"/>), and
    /// reports the shortest paths that reach a voxel map or a cube grid that has already closed. A closed entity is
    /// garbage unless something rooted holds it, so any path found is the holder. An open grid or voxel map is not
    /// walked into unless <c>open=1</c>. <c>depth=N</c> (1-16, default 4) sets the hop limit, <c>objects=N</c>
    /// (default 1,500,000, at most 8,000,000) the distinct objects it may reach, and <c>all=1</c> walks every loaded
    /// assembly except the framework's. <c>completeDepth=</c> is how many hops deep the answer is exhaustive when the
    /// object or time bound (20 s) stopped it. Seconds on the game thread. GC handles and stacks are not walked: a
    /// holder only they reach shows up in <c>factions bench dump</c>, not here.
    /// </summary>
    internal static class BenchRoots
    {
        private const int DefaultDepth = 4;
        private const int MaxDepth = 16;
        private const int DefaultObjects = 1500000;
        private const int MaxObjects = 8000000;
        private const double MaxSeconds = 20.0;
        private const int Voxel = 0, Grid = 1;

        internal static string Run(string[] args)
        {
            int depth = DefaultDepth, objects = DefaultObjects;
            bool all = false, open = false;
            for (int i = 3; args != null && i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                int n;
                if (a.StartsWith("depth=", StringComparison.Ordinal)
                    && int.TryParse(a.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    depth = Math.Max(1, Math.Min(MaxDepth, n));
                else if (a.StartsWith("objects=", StringComparison.Ordinal)
                    && int.TryParse(a.Substring(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    objects = Math.Max(1000, Math.Min(MaxObjects, n));
                else if (a == "all=1") all = true;
                else if (a == "open=1") open = true;
            }
            long t0 = Stopwatch.GetTimestamp();
            ObjectGraphWalker walker;
            int statics;
            try
            {
                List<KeyValuePair<string, object>> roots = Statics(all);
                statics = roots.Count;
                walker = new ObjectGraphWalker((object o, out int category) => Classify(o, open, out category),
                    2, depth, objects, MaxSeconds);
                walker.Run(roots);
            }
            catch (Exception ex)
            {
                return "bench roots: failed: " + ex.Message;
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            var sb = new StringBuilder("bench roots: statics=").Append(statics)
                .Append(" expanded=").Append(walker.Expanded).Append(" discovered=").Append(walker.Discovered)
                .Append(walker.Partial ? " (capped by objects or time; partial)" : "")
                .Append(" completeDepth=").Append(walker.CompleteDepth)
                .Append(" ms=").Append(ms.ToString("0", CultureInfo.InvariantCulture))
                .Append(" depth=").Append(depth).Append(" objects=").Append(objects)
                .Append(all ? " all" : "").Append(open ? " open" : "");
            Append(sb, "closedVoxels", "closedVoxelPaths", walker, Voxel, " ");
            Append(sb, "closedGrids", "closedGridPaths", walker, Grid, " || ");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string countName, string pathsName, ObjectGraphWalker walker,
                                   int category, string lead)
        {
            List<KeyValuePair<string, int>> paths = walker.HitPaths(category);
            sb.Append(lead).Append(countName).Append('=').Append(walker.Hits(category))
              .Append(' ').Append(pathsName).Append('=').Append(paths.Count);
            foreach (KeyValuePair<string, int> kv in paths.Take(12))
                sb.Append(" | ").Append(kv.Key).Append('=').Append(kv.Value);
        }

        private static WalkVerdict Classify(object o, bool open, out int category)
        {
            category = 0;
            var voxel = o as MyVoxelBase;
            if (voxel != null)
            {
                category = Voxel;
                if (voxel.Closed) return WalkVerdict.Hit;
                return open ? WalkVerdict.Expand : WalkVerdict.Leaf;
            }
            var grid = o as MyCubeGrid;
            if (grid != null)
            {
                category = Grid;
                if (grid.Closed) return WalkVerdict.Hit;
                return open ? WalkVerdict.Expand : WalkVerdict.Leaf;
            }
            return WalkVerdict.Expand;
        }

        private static List<KeyValuePair<string, object>> Statics(bool all)
        {
            var roots = new List<KeyValuePair<string, object>>();
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
                        if (v != null) roots.Add(new KeyValuePair<string, object>(t.Name + "." + f.Name, v));
                    }
                }
            }
            return roots;
        }

        private static bool IsFramework(string name)
        {
            return name == "mscorlib" || name == "netstandard" || name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("Microsoft", StringComparison.Ordinal);
        }
    }
}
