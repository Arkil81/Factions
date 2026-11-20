using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using Factions.Core;
using HarmonyLib;
using Havok;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Attributes growth of Havok's memory pool (<c>HkBaseSystem.GetCurrentMemoryConsumption</c>) to what was running
    /// when it grew, under <c>factions debug physics</c>. Each observed area costs Havok ~1.1 GB and the pool keeps it
    /// until restart (FAC-445 runs 13-15, 2026-10-05), and the cost does not track the area's voxel-map count; this
    /// says which code path takes it.
    ///
    /// <para>Windows read the pool before and after: a voxel map's collision grid being created
    /// (<c>MyVoxelPhysicsBody.CreateRigidBodies</c>, with its cell count), a voxel cell's mesh shape being built
    /// (<c>CreateShape</c>, on precalc workers), finished shapes being inserted (<c>OnBatchTaskComplete</c> /
    /// <c>OnTaskComplete</c>), any other body being created (<c>MyPhysicsBody.CreateFromCollisionObject</c>, by entity
    /// type), and the physics step. The pool is shared, so a window on one thread also sees growth another thread
    /// caused at the same moment; the worker figure is a sum of overlapping windows and can exceed what the workers
    /// alone took. <c>other</c> is pool growth no game-thread window saw.</para>
    ///
    /// <para>Patched by hand and wrapped: a miss disables this class and logs one line.</para>
    /// </summary>
    internal static class HavokMemoryTrace
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const double ReportSeconds = 10;

        private sealed class Bucket { internal long N; internal long Bytes; }

        private static readonly Dictionary<string, Bucket> Bodies = new Dictionary<string, Bucket>();
        private static long _gridN, _gridBytes, _gridCells, _gridMaxCells, _gridMaxBytes;
        private static long _applyN, _applyBytes, _stepBytes;
        private static long _shapeN, _shapeBytes;
        private static long _poolAtReport = -1, _windowStart;

        [ThreadStatic] private static int _inGrid;

        /// <summary>Real grid creations by the voxel map's largest dimension in metres: count, pool growth, cells.
        /// <c>CreateRigidBodies</c> is re-entered every tenth frame (<c>UpdateBeforeSimulation10</c>) and returns early
        /// unless the map has a mesher and a world and no grid yet; those calls are counted by reason instead.</summary>
        private sealed class SizeBucket { internal long N; internal long Bytes; internal long Cells; internal long Ticks; internal long MaxTicks; }
        private static readonly SortedDictionary<int, SizeBucket> Created = new SortedDictionary<int, SizeBucket>();
        private static long _skipInit, _skipWorld, _skipMesher, _skipClosing, _skipDeclined;
        private static readonly AccessTools.FieldRef<MyVoxelPhysicsBody, bool> BodiesInitialized =
            Ref<MyVoxelPhysicsBody, bool>("m_bodiesInitialized");
        private static readonly AccessTools.FieldRef<MyVoxelPhysicsBody, object> Mesher =
            Ref<MyVoxelPhysicsBody, object>("m_mesher");
        private static readonly AccessTools.FieldRef<MyPhysicsBody, HkWorld> World =
            Ref<MyPhysicsBody, HkWorld>("m_world");

        private static AccessTools.FieldRef<T, F> Ref<T, F>(string name)
        {
            try { return AccessTools.FieldRefAccess<T, F>(name); } catch { return null; }
        }

        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) return;
            var bound = new List<string>();
            var missing = new List<string>();
            Bind(harmony, typeof(MyVoxelPhysicsBody), "CreateRigidBodies", "GridPrefix", "GridPostfix", bound, missing);
            Bind(harmony, typeof(MyVoxelPhysicsBody), "CreateShape", "ShapePrefix", "ShapePostfix", bound, missing);
            Bind(harmony, typeof(MyVoxelPhysicsBody), "OnBatchTaskComplete", "ApplyPrefix", "ApplyPostfix", bound, missing);
            Bind(harmony, typeof(MyVoxelPhysicsBody), "OnTaskComplete", "ApplyPrefix", "ApplyPostfix", bound, missing);
            Bind(harmony, typeof(MyPhysicsBody), "CreateFromCollisionObject", "BodyPrefix", "BodyPostfix", bound, missing);
            Log.Info("Havok memory attribution: bound " + string.Join(",", bound)
                     + (missing.Count > 0 ? "; not bound " + string.Join(",", missing) : "") + ".");
        }

        private static void Bind(Harmony harmony, Type type, string name, string prefix, string postfix,
                                 List<string> bound, List<string> missing)
        {
            try
            {
                MethodInfo target = AccessTools.Method(type, name);
                if (target == null) { missing.Add(name); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(Method(prefix)), postfix: new HarmonyMethod(Method(postfix)));
                bound.Add(name);
            }
            catch (Exception ex) { missing.Add(name + "(" + ex.Message + ")"); }
        }

        private static MethodInfo Method(string name)
        {
            return typeof(HavokMemoryTrace).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        private static long Pool()
        {
            try { return HkBaseSystem.GetCurrentMemoryConsumption(); } catch { return 0; }
        }

        /// <summary>Harmony runs every prefix even after one returns false, so a call another prefix declined
        /// (<see cref="RockContactPhysics"/> holds pristine rock's grid back on each tenth-frame re-entry) is only
        /// counted. Sampling the Havok pool costs tens of microseconds, and declined re-entries number thousands
        /// per frame once phantoms or players spread rock across the ring.</summary>
        private static void GridPrefix(MyVoxelPhysicsBody __instance, bool __runOriginal, out long __state)
        {
            __state = -1;
            _inGrid++;
            if (!DebugTraces.Physics) return;
            if (!__runOriginal) { _skipDeclined++; return; }
            try
            {
                if (BodiesInitialized != null && BodiesInitialized(__instance)) { _skipInit++; return; }
                if (__instance.Entity == null || __instance.Entity.MarkedForClose) { _skipClosing++; return; }
                if (Mesher != null && Mesher(__instance) == null) { _skipMesher++; return; }
                if (World != null && World(__instance) == null) { _skipWorld++; return; }
            }
            catch { }
            __state = Pool();
            _gridStart = Stopwatch.GetTimestamp();
        }

        private static long _gridStart;

        /// <summary><c>__state</c> 0 means this prefix did not run (another prefix skipped the original first), so
        /// the depth it would have raised is not lowered either.</summary>
        private static void GridPostfix(MyVoxelPhysicsBody __instance, long __state)
        {
            if (__state == 0) return;
            _inGrid--;
            if (__state < 0) return;
            long ticks = Stopwatch.GetTimestamp() - _gridStart;
            try
            {
                if (BodiesInitialized != null && !BodiesInitialized(__instance)) return;
                long d = Math.Max(0, Pool() - __state);
                var voxel = __instance.Entity as MyVoxelBase;
                long cells = 0;
                if (voxel != null)
                {
                    Vector3I c = voxel.Size >> 3;
                    cells = (long)c.X * c.Y * c.Z;
                }
                if (voxel != null)
                {
                    int m = Math.Max(voxel.Size.X, Math.Max(voxel.Size.Y, voxel.Size.Z));
                    SizeBucket sb;
                    if (!Created.TryGetValue(m, out sb)) Created[m] = sb = new SizeBucket();
                    sb.N++;
                    sb.Bytes += d;
                    sb.Cells += cells;
                    sb.Ticks += ticks;
                    if (ticks > sb.MaxTicks) sb.MaxTicks = ticks;
                }
                _gridN++;
                _gridBytes += d;
                _gridCells += cells;
                if (cells > _gridMaxCells) _gridMaxCells = cells;
                if (d > _gridMaxBytes) _gridMaxBytes = d;
            }
            catch { }
        }

        private static void ShapePrefix(out long __state)
        {
            __state = DebugTraces.Physics ? Pool() : -1;
        }

        private static void ShapePostfix(long __state)
        {
            if (__state < 0) return;
            long d = Pool() - __state;
            Interlocked.Increment(ref _shapeN);
            if (d > 0) Interlocked.Add(ref _shapeBytes, d);
        }

        private static void ApplyPrefix(out long __state)
        {
            __state = DebugTraces.Physics ? Pool() : -1;
        }

        private static void ApplyPostfix(long __state)
        {
            if (__state < 0) return;
            _applyN++;
            _applyBytes += Math.Max(0, Pool() - __state);
        }

        private static void BodyPrefix(out long __state)
        {
            __state = DebugTraces.Physics && _inGrid == 0 ? Pool() : -1;
        }

        private static void BodyPostfix(MyPhysicsBody __instance, long __state)
        {
            if (__state < 0) return;
            try
            {
                long d = Math.Max(0, Pool() - __state);
                string type = __instance.Entity != null ? __instance.Entity.GetType().Name : "none";
                Bucket b;
                if (!Bodies.TryGetValue(type, out b)) Bodies[type] = b = new Bucket();
                b.N++;
                b.Bytes += d;
            }
            catch { }
        }

        /// <summary>The pool before the physics step; <see cref="AfterStep"/> takes the difference.</summary>
        internal static long BeforeStep()
        {
            return DebugTraces.Physics ? Pool() : -1;
        }

        /// <summary>Called on the game thread after each physics step; logs every <see cref="ReportSeconds"/>
        /// when the pool grew.</summary>
        internal static void AfterStep(long before)
        {
            if (before < 0) return;
            long now = Pool();
            _stepBytes += Math.Max(0, now - before);
            long t = Stopwatch.GetTimestamp();
            if (_poolAtReport < 0) { Reset(now, t); return; }
            if ((t - _windowStart) < ReportSeconds * Stopwatch.Frequency) return;
            long growth = now - _poolAtReport;
            if (growth > 0)
            {
                long shapeBytes = Interlocked.Read(ref _shapeBytes);
                long other = growth - _gridBytes - _applyBytes - _stepBytes;
                foreach (Bucket b in Bodies.Values) other -= b.Bytes;
                var sb = new StringBuilder("HAVOK_MEMORY_BY growthMB=").Append(Mb(growth))
                    .Append(" poolMB=").Append(Mb(now))
                    .Append(" voxelGrids=").Append(_gridN).Append('/').Append(Mb(_gridBytes)).Append("MB")
                    .Append(" cells=").Append(_gridCells)
                    .Append(" maxCells=").Append(_gridMaxCells)
                    .Append(" maxGridMB=").Append(Mb(_gridMaxBytes))
                    .Append(" voxelShapesWorker=").Append(Interlocked.Read(ref _shapeN)).Append('/').Append(Mb(shapeBytes)).Append("MB")
                    .Append(" shapeInsert=").Append(_applyN).Append('/').Append(Mb(_applyBytes)).Append("MB")
                    .Append(" step=").Append(Mb(_stepBytes)).Append("MB");
                foreach (KeyValuePair<string, Bucket> kv in Bodies)
                    sb.Append(" body:").Append(kv.Key).Append('=').Append(kv.Value.N).Append('/').Append(Mb(kv.Value.Bytes)).Append("MB");
                sb.Append(" other=").Append(Mb(other)).Append("MB");
                Log.Info(sb.ToString());
            }
            if (_gridN > 0 || _skipWorld > 0 || _skipMesher > 0 || _skipDeclined > 0)
            {
                var vb = new StringBuilder("HAVOK_VOXEL_GRIDS created=").Append(_gridN)
                    .Append(" MB=").Append(Mb(_gridBytes)).Append(" cells=").Append(_gridCells)
                    .Append(" bytesPerCell=").Append(_gridCells > 0 ? (_gridBytes / (double)_gridCells).ToString("0.0", CultureInfo.InvariantCulture) : "-")
                    .Append(" kbPerMap=").Append(_gridN > 0 ? (_gridBytes / 1024.0 / _gridN).ToString("0", CultureInfo.InvariantCulture) : "-")
                    .Append(" skipped:initialised=").Append(_skipInit).Append(",noWorld=").Append(_skipWorld)
                    .Append(",noMesher=").Append(_skipMesher).Append(",closing=").Append(_skipClosing)
                    .Append(",declined=").Append(_skipDeclined)
                    .Append(" |");
                foreach (KeyValuePair<int, SizeBucket> kv in Created)
                    vb.Append(' ').Append(kv.Key).Append("m=").Append(kv.Value.N).Append('/').Append(Mb(kv.Value.Bytes))
                      .Append("MB/").Append(kv.Value.Cells).Append("cells/")
                      .Append((kv.Value.Ticks * 1000.0 / Stopwatch.Frequency / kv.Value.N).ToString("0.000", CultureInfo.InvariantCulture))
                      .Append("msAvg/")
                      .Append((kv.Value.MaxTicks * 1000.0 / Stopwatch.Frequency).ToString("0.00", CultureInfo.InvariantCulture))
                      .Append("msMax");
                Log.Info(vb.ToString());
            }
            Reset(now, t);
        }

        private static void Reset(long pool, long t)
        {
            _poolAtReport = pool;
            _windowStart = t;
            _gridN = _gridBytes = _gridCells = _gridMaxCells = _gridMaxBytes = 0;
            _skipInit = _skipWorld = _skipMesher = _skipClosing = _skipDeclined = 0;
            Created.Clear();
            _applyN = _applyBytes = _stepBytes = 0;
            Interlocked.Exchange(ref _shapeN, 0);
            Interlocked.Exchange(ref _shapeBytes, 0);
            Bodies.Clear();
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
