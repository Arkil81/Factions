using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Factions.Core;
using HarmonyLib;
using Havok;
using NLog;
using Sandbox.Engine.Physics;
using VRage;
using VRage.Library.Utils;
using VRageMath;
using VRageMath.Spatial;

namespace Factions
{
    /// <summary>
    /// Behind <c>factions feature physics-per-world</c> (FAC-445 diagnostic): <c>MyPhysics.StepWorldsInternal</c>
    /// steps each stepped cluster one after another, through SE's own <c>StepSingleWorld</c> and its
    /// <c>IsClusterActive</c> rule, and times each, instead of one parallel job. A step of 2 s or more logs
    /// <c>PHYSICS_PER_WORLD</c> with every stepped cluster's time, size, bodies and voxel maps. Five parked
    /// phantoms froze the server on single 19-29 s steps (2026-10-05) with no cluster standing out by active
    /// bodies, and the parallel job cannot say which world took the time. The sequential schedule is itself a
    /// change, so a run with the switch on is not comparable to one without.
    /// </summary>
    [HarmonyPatch(typeof(MyPhysics), "StepWorlds")]
    [HarmonyPriority(Priority.Last)]
    internal static class PhysicsPerWorldStep
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private static readonly MethodInfo StepSingleWorldM = AccessTools.Method(typeof(MyPhysics), "StepSingleWorld", new[] { typeof(HkWorld) });
        private static readonly MethodInfo IsClusterActiveM = AccessTools.Method(typeof(MyPhysics), "IsClusterActive", new[] { typeof(int), typeof(int) });

        private static bool Prepare()
        {
            return StepSingleWorldM != null && IsClusterActiveM != null;
        }

        /// <summary>On <c>StepWorlds</c>, not <c>StepWorldsInternal</c>: v0.456.6 patched the latter, which the JIT
        /// inlines, and the switch did nothing (run 9, 2026-10-05 14:29:19 box-local, no line for an 18.9 s step).
        /// On a dedicated server with selective physics updates <c>StepWorlds</c> only calls
        /// <c>StepWorldsInternal(null)</c>, so replacing it is the same. Last, so the step timer's prefix has run.</summary>
        private static bool Prefix(MyPhysics __instance)
        {
            if (!FeatureSwitch.IsOn(FeatureSwitch.PhysicsPerWorld)) return true;

            long t0 = Stopwatch.GetTimestamp();
            var rows = new List<KeyValuePair<double, string>>();
            foreach (MyClusterTree.MyCluster cluster in MyPhysics.Clusters.GetClusters())
            {
                var world = cluster.UserData as HkWorld;
                if (world == null) continue;
                int chars = world.CharacterRigidBodies != null ? world.CharacterRigidBodies.Count : 0;
                if (!(bool)IsClusterActiveM.Invoke(__instance, new object[] { cluster.ClusterId, chars })) continue;
                long w0 = Stopwatch.GetTimestamp();
                InFlight.Begin(cluster, world, chars, w0);
                try { StepSingleWorldM.Invoke(__instance, new object[] { world }); }
                finally { InFlight.End(); }
                double ms = (Stopwatch.GetTimestamp() - w0) * 1000.0 / Stopwatch.Frequency;
                rows.Add(new KeyValuePair<double, string>(ms, Describe(cluster, world, chars)));
            }
            if (HkBaseSystem.IsOutOfMemory) throw new OutOfMemoryException("Havok run out of memory");

            double total = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (total >= PhysicsStallTrace.StallMs)
            {
                rows.Sort((a, b) => b.Key.CompareTo(a.Key));
                var sb = new StringBuilder("PHYSICS_PER_WORLD totalMs=").Append(total.ToString("0", CultureInfo.InvariantCulture))
                    .Append(" stepped=").Append(rows.Count);
                foreach (KeyValuePair<double, string> r in rows)
                    sb.Append(" | ").Append(r.Key.ToString("0", CultureInfo.InvariantCulture)).Append("ms ").Append(r.Value);
                Log.Info(sb.ToString());
            }
            return false;
        }

        /// <summary>The world being stepped, watched from a background thread: a step that outlasts Torch's 60 s
        /// watchdog never reaches the end-of-step line (run 10, 2026-10-05 14:33-14:35 box-local, a freeze past
        /// 60 s with the switch on and nothing logged), so a world still stepping after
        /// <see cref="InFlight.ReportAfterMs"/> is named while it runs, from counts alone.</summary>
        private static class InFlight
        {
            internal const double ReportAfterMs = 5000;
            private static readonly object Gate = new object();
            private static MyClusterTree.MyCluster _cluster;
            private static HkWorld _world;
            private static int _chars;
            private static long _since;
            private static bool _reported;
            private static System.Threading.Thread _thread;

            internal static void Begin(MyClusterTree.MyCluster cluster, HkWorld world, int chars, long since)
            {
                lock (Gate) { _cluster = cluster; _world = world; _chars = chars; _since = since; _reported = false; }
                if (_thread == null)
                {
                    _thread = new System.Threading.Thread(Watch) { IsBackground = true, Name = "Factions physics in-flight" };
                    _thread.Start();
                }
            }

            internal static void End()
            {
                lock (Gate) { _world = null; _cluster = null; }
            }

            private static void Watch()
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(1000);
                    try
                    {
                        string line = null;
                        lock (Gate)
                        {
                            if (_world == null || _reported) continue;
                            double ms = (Stopwatch.GetTimestamp() - _since) * 1000.0 / Stopwatch.Frequency;
                            if (ms < ReportAfterMs) continue;
                            _reported = true;
                            Vector3D c = _cluster.AABB.Center, s = _cluster.AABB.Size;
                            line = "PHYSICS_WORLD_STEPPING inProgressMs=" + ms.ToString("0", CultureInfo.InvariantCulture)
                                   + " centreKm=(" + (c.X / 1000).ToString("0", CultureInfo.InvariantCulture) + "," + (c.Y / 1000).ToString("0", CultureInfo.InvariantCulture)
                                   + "," + (c.Z / 1000).ToString("0", CultureInfo.InvariantCulture) + ") sizeKm=" + (s.Max() / 1000).ToString("0", CultureInfo.InvariantCulture)
                                   + " chars=" + _chars + " active=" + _world.ActiveRigidBodies.Count
                                   + " total=" + (_world.RigidBodies != null ? _world.RigidBodies.Count : 0);
                        }
                        if (line != null) Log.Info(line);
                    }
                    catch { }
                }
            }
        }

        private static string Describe(MyClusterTree.MyCluster cluster, HkWorld world, int chars)
        {
            Vector3D c = cluster.AABB.Center, s = cluster.AABB.Size;
            int voxels = 0;
            if (world.RigidBodies != null)
                foreach (HkRigidBody b in world.RigidBodies)
                {
                    var pb = b == null ? null : b.UserObject as MyPhysicsBody;
                    if (pb != null && pb.Entity is Sandbox.Game.Entities.MyVoxelBase) voxels++;
                }
            return "centreKm=(" + (c.X / 1000).ToString("0", CultureInfo.InvariantCulture) + "," + (c.Y / 1000).ToString("0", CultureInfo.InvariantCulture)
                   + "," + (c.Z / 1000).ToString("0", CultureInfo.InvariantCulture) + ") sizeKm=" + (s.Max() / 1000).ToString("0", CultureInfo.InvariantCulture)
                   + " chars=" + chars + " active=" + world.ActiveRigidBodies.Count
                   + " total=" + (world.RigidBodies != null ? world.RigidBodies.Count : 0) + " voxels=" + voxels
                   + " constraints=" + world.GetConstraintCount();
        }
    }
}
