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
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;
using VRageMath.Spatial;

namespace Factions
{
    /// <summary>
    /// What physics was doing when a step ran for seconds (FAC-445). With bench phantoms in five areas of
    /// the ring the server froze on single physics steps of 23.8, 26.7, 113.6 and 24.2 s (2026-10-05
    /// 12:18-12:58 box-local), Havok reporting one simulation island and three clusters where a quiet run
    /// holds ~95 and 7-8; one phantom alone, parked or at 500 m/s, ran clean.
    ///
    /// <para><b>PHYSICS_CLUSTER_REORDER.</b> <c>MyClusterTree.MoveObject</c> reorders clusters over the box
    /// spanning a body's old and new bounds whenever it leaves its cluster, so a body that jumps far reorders
    /// everything between. Logged with the box size, the entity, and the time it took, for boxes past
    /// <see cref="ReorderLogKm"/>.</para>
    ///
    /// <para><b>PHYSICS_STALL.</b> After a <c>StepWorlds</c> past <see cref="StallMs"/>: every cluster's size
    /// and centre, active and total bodies, islands and constraints, and the fastest active bodies.</para>
    ///
    /// <para>Both under <c>factions debug physics</c>; when it is off, the hooks cost a flag test.</para>
    /// </summary>
    internal static class PhysicsStallTrace
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        internal const double StallMs = 2000.0;
        internal const double ReorderLogKm = 40.0;
        private const int FastestBodies = 8;

        private static readonly FieldInfo ObjectsDataF = AccessTools.Field(typeof(MyClusterTree), "m_objectsData");

        private static readonly MethodInfo IsClusterActiveM = AccessTools.Method(typeof(MyPhysics), "IsClusterActive", new[] { typeof(int), typeof(int) });

        /// <summary>Whether SE steps this cluster: with selective physics updates on, a cluster holding no
        /// character is stepped only when the world observer marks it active (<c>MyPhysics.IsClusterActive</c>,
        /// read in <c>StepWorldsParallel</c>). "?" when it cannot be read.</summary>
        internal static string Stepped(MyClusterTree.MyCluster cluster, HkWorld world)
        {
            try
            {
                var physics = Sandbox.Game.World.MySession.Static == null ? null : Sandbox.Game.World.MySession.Static.GetComponent<MyPhysics>();
                if (physics == null || IsClusterActiveM == null || cluster == null || world == null) return "?";
                return (bool)IsClusterActiveM.Invoke(physics, new object[] { cluster.ClusterId, world.CharacterRigidBodies.Count }) ? "yes" : "no";
            }
            catch { return "?"; }
        }

        /// <summary>Each world's body count before the step, and its pending-operations time inside it.</summary>
        private static readonly Dictionary<HkWorld, int> BodiesBefore = new Dictionary<HkWorld, int>();
        private static readonly Dictionary<HkWorld, double> PendingMs = new Dictionary<HkWorld, double>();

        /// <summary>Called from <see cref="PhysicsStepPatches"/> before each step: the body counts the next
        /// stall line reports its gains against. O(clusters).</summary>
        internal static void BeforeStep()
        {
            if (!DebugTraces.Physics) return;
            try
            {
                BodiesBefore.Clear();
                PendingMs.Clear();
                foreach (object o in MyPhysics.Clusters.GetList())
                {
                    var w = o as HkWorld;
                    if (w != null && w.RigidBodies != null) BodiesBefore[w] = w.RigidBodies.Count;
                }
            }
            catch { }
        }

        /// <summary>Times each world's <c>ExecutePendingCriticalOperations</c>: the bodies and shapes queued
        /// since the last step, applied on the game thread before the parallel solve.</summary>
        [HarmonyPatch(typeof(HkWorld), "ExecutePendingCriticalOperations")]
        private static class PendingPatch
        {
            private static void Prefix(out long __state)
            {
                __state = DebugTraces.Physics ? Stopwatch.GetTimestamp() : 0;
            }

            private static void Postfix(HkWorld __instance, long __state)
            {
                if (__state == 0) return;
                double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                double was;
                PendingMs.TryGetValue(__instance, out was);
                PendingMs[__instance] = was + ms;
            }
        }

        /// <summary>The entity behind a cluster-tree object id, or 0.</summary>
        private static long EntityOf(MyClusterTree tree, ulong id)
        {
            try
            {
                var dict = ObjectsDataF == null ? null : ObjectsDataF.GetValue(tree) as System.Collections.IDictionary;
                if (dict == null || !dict.Contains(id)) return 0;
                object data = dict[id];
                FieldInfo f = data.GetType().GetField("EntityId");
                return f == null ? 0 : (long)f.GetValue(data);
            }
            catch { return 0; }
        }

        private static string Describe(long entityId)
        {
            if (entityId == 0) return "entity=?";
            MyEntity e;
            if (!MyEntities.TryGetEntityById(entityId, out e, true) || e == null) return "entity=" + entityId + "(gone)";
            Vector3 v = e.Physics != null ? e.Physics.LinearVelocity : Vector3.Zero;
            Vector3D p = e.PositionComp.GetPosition();
            return "entity=" + entityId + " type=" + e.GetType().Name + " name=\"" + (e.DisplayName ?? e.Name ?? "") + "\""
                   + " speed=" + v.Length().ToString("0", CultureInfo.InvariantCulture)
                   + " km=(" + (p.X / 1000).ToString("0", CultureInfo.InvariantCulture) + "," + (p.Y / 1000).ToString("0", CultureInfo.InvariantCulture)
                   + "," + (p.Z / 1000).ToString("0", CultureInfo.InvariantCulture) + ")";
        }

        [HarmonyPatch(typeof(MyClusterTree), "ReorderClusters")]
        private static class ReorderPatch
        {
            private static void Prefix(out long __state)
            {
                __state = DebugTraces.Physics ? Stopwatch.GetTimestamp() : 0;
            }

            private static void Postfix(MyClusterTree __instance, BoundingBoxD aabb, ulong objectId, long __state)
            {
                if (__state == 0) return;
                try
                {
                    double km = aabb.Size.Max() / 1000.0;
                    if (km < ReorderLogKm) return;
                    double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                    Vector3D c = aabb.Center;
                    Log.Info("PHYSICS_CLUSTER_REORDER boxKm=" + km.ToString("0", CultureInfo.InvariantCulture)
                             + " centreKm=(" + (c.X / 1000).ToString("0", CultureInfo.InvariantCulture) + "," + (c.Y / 1000).ToString("0", CultureInfo.InvariantCulture)
                             + "," + (c.Z / 1000).ToString("0", CultureInfo.InvariantCulture) + ")"
                             + " ms=" + ms.ToString("F1", CultureInfo.InvariantCulture)
                             + " clusters=" + __instance.GetClusters().Count + " "
                             + (objectId == ulong.MaxValue ? "object=none" : Describe(EntityOf(__instance, objectId))));
                }
                catch (Exception ex) { Log.Warn("PHYSICS_CLUSTER_REORDER trace failed: " + ex.Message); }
            }
        }

        /// <summary>Called from <see cref="PhysicsStepPatches"/> after each step, with its length.</summary>
        internal static void AfterStep(double stepMs)
        {
            if (!DebugTraces.Physics || stepMs < StallMs) return;
            try
            {
                var sb = new StringBuilder("PHYSICS_STALL stepMs=").Append(stepMs.ToString("0", CultureInfo.InvariantCulture))
                    .Append(" havokMB=").Append(PhysicsStepPatches.HavokMemoryBytes >> 20)
                    .Append(" outOfMemory=").Append(HkBaseSystem.IsOutOfMemory ? 1 : 0);
                var fastest = new List<KeyValuePair<float, IMyEntity>>();
                int clusters = 0;
                foreach (MyClusterTree.MyCluster cluster in MyPhysics.Clusters.GetClusters())
                {
                    var world = cluster.UserData as HkWorld;
                    if (world == null) continue;
                    clusters++;
                    Vector3D size = cluster.AABB.Size, c = cluster.AABB.Center;
                    sb.Append(" | c").Append(clusters).Append(" sizeKm=")
                      .Append((size.X / 1000).ToString("0", CultureInfo.InvariantCulture)).Append('x')
                      .Append((size.Y / 1000).ToString("0", CultureInfo.InvariantCulture)).Append('x')
                      .Append((size.Z / 1000).ToString("0", CultureInfo.InvariantCulture))
                      .Append(" centreKm=(").Append((c.X / 1000).ToString("0", CultureInfo.InvariantCulture)).Append(',')
                      .Append((c.Y / 1000).ToString("0", CultureInfo.InvariantCulture)).Append(',')
                      .Append((c.Z / 1000).ToString("0", CultureInfo.InvariantCulture)).Append(')')
                      .Append(" active=").Append(world.ActiveRigidBodies.Count)
                      .Append(" total=").Append(world.RigidBodies != null ? world.RigidBodies.Count : 0)
                      .Append(" islands=").Append(world.GetActiveSimulationIslandsCount())
                      .Append(" constraints=").Append(world.GetConstraintCount())
                      .Append(" chars=").Append(world.CharacterRigidBodies != null ? world.CharacterRigidBodies.Count : 0)
                      .Append(" stepped=").Append(Stepped(cluster, world));
                    int before, total = world.RigidBodies != null ? world.RigidBodies.Count : 0;
                    double pending;
                    sb.Append(" gained=").Append(BodiesBefore.TryGetValue(world, out before) ? (total - before).ToString(CultureInfo.InvariantCulture) : "?")
                      .Append(" pendingMs=").Append(PendingMs.TryGetValue(world, out pending) ? pending.ToString("0", CultureInfo.InvariantCulture) : "?");
                    if (world.RigidBodies != null && total > 50)
                    {
                        var types = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (HkRigidBody b in world.RigidBodies)
                        {
                            var pb = b == null ? null : b.UserObject as MyPhysicsBody;
                            string t = pb == null || pb.Entity == null ? "(none)" : pb.Entity.GetType().Name;
                            int n; types.TryGetValue(t, out n); types[t] = n + 1;
                        }
                        var rows = new List<KeyValuePair<string, int>>(types);
                        rows.Sort((x, y) => y.Value.CompareTo(x.Value));
                        sb.Append(" bodies=");
                        for (int i = 0; i < rows.Count && i < 3; i++)
                            sb.Append(i == 0 ? "" : ",").Append(rows[i].Key).Append(':').Append(rows[i].Value);
                    }
                    foreach (HkRigidBody body in world.ActiveRigidBodies)
                    {
                        var physics = body == null ? null : body.UserObject as MyPhysicsBody;
                        IMyEntity e = physics == null ? null : physics.Entity;
                        if (e == null) continue;
                        fastest.Add(new KeyValuePair<float, IMyEntity>(body.LinearVelocity.Length(), e));
                    }
                }
                fastest.Sort((a, b) => b.Key.CompareTo(a.Key));
                sb.Append(" | fastest:");
                for (int i = 0; i < fastest.Count && i < FastestBodies; i++)
                    sb.Append(i == 0 ? " " : "; ").Append(Describe(fastest[i].Value.EntityId));
                Log.Info(sb.ToString());
            }
            catch (Exception ex) { Log.Warn("PHYSICS_STALL trace failed: " + ex.Message); }
        }
    }
}
