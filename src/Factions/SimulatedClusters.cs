using System;
using System.Collections.Generic;
using System.Reflection;
using Havok;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Game.World;
using VRageMath.Spatial;

namespace Factions
{
    /// <summary>
    /// Which Havok worlds SE is stepping this frame — the server's simulated set, read from SE's
    /// own decision rather than approximated by a distance.
    ///
    /// <para><b>SE's rule.</b> <c>MyPhysics.StepWorlds*</c> steps a cluster only when
    /// <c>IsClusterActive(clusterId, world.CharacterRigidBodies.Count)</c> is true: always when a
    /// character is in it, and otherwise when <c>MyWorldObserver</c> reports an entity in it that is
    /// replicated to some client. The observer exists only on a dedicated server with
    /// <c>EnableSelectivePhysicsUpdates</c>; without it every cluster is stepped, and
    /// <see cref="Snapshot"/> reports that as <c>selective=false</c>.</para>
    ///
    /// <para><b>Called, not re-derived.</b> <c>IsClusterActive</c> is private, so it is bound once by
    /// reflection and invoked with the same two arguments SE passes. Re-implementing it would drift
    /// the first time SE changed the rule.</para>
    /// </summary>
    internal static class SimulatedClusters
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static Func<MyPhysics, int, int, bool> _isClusterActive;
        private static FieldInfo _observerField;
        private static bool _resolved;
        private static bool _warned;

        internal static string InactiveReason { get; private set; }

        private static bool Resolve()
        {
            if (_resolved) return _isClusterActive != null;
            _resolved = true;
            try
            {
                MethodInfo m = typeof(MyPhysics).GetMethod("IsClusterActive",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(int), typeof(int) }, null);
                if (m == null || m.ReturnType != typeof(bool))
                {
                    InactiveReason = "MyPhysics.IsClusterActive(int, int) not found";
                    return false;
                }
                _isClusterActive = (Func<MyPhysics, int, int, bool>)Delegate.CreateDelegate(
                    typeof(Func<MyPhysics, int, int, bool>), m);
                _observerField = typeof(MyPhysics).GetField("m_worldObserver",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                return true;
            }
            catch (Exception ex)
            {
                InactiveReason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Fills <paramref name="activeByWorld"/> with every cluster's world and whether SE
        /// steps it. Game thread only: the cluster list is mutated there.</summary>
        /// <returns>False when the rule could not be read; the caller then has no simulated set and
        /// must say so rather than treat every grid as unsimulated.</returns>
        internal static bool Snapshot(Dictionary<HkWorld, bool> activeByWorld,
                                      out int clusters, out int activeClusters, out bool selective)
        {
            clusters = 0;
            activeClusters = 0;
            selective = false;
            activeByWorld.Clear();
            if (!Resolve())
            {
                WarnOnce();
                return false;
            }
            try
            {
                MyPhysics physics = MySession.Static != null ? MySession.Static.GetComponent<MyPhysics>() : null;
                if (physics == null || MyPhysics.Clusters == null)
                {
                    InactiveReason = "no MyPhysics session component";
                    return false;
                }
                selective = _observerField != null && _observerField.GetValue(physics) != null;
                foreach (MyClusterTree.MyCluster cluster in MyPhysics.Clusters.GetClusters())
                {
                    var world = cluster.UserData as HkWorld;
                    if (world == null) continue;
                    clusters++;
                    bool active = _isClusterActive(physics, cluster.ClusterId, world.CharacterRigidBodies.Count);
                    activeByWorld[world] = active;
                    if (active) activeClusters++;
                }
                return true;
            }
            catch (Exception ex)
            {
                // A cluster list reordering under the walk is the expected failure; the next census
                // retries, and a missed sample is not worth a line every minute.
                InactiveReason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static void WarnOnce()
        {
            if (_warned) return;
            _warned = true;
            Log.Warn("Server perf: simulated set unavailable (" + InactiveReason
                     + "); the census reports no per-grid simulated state.");
        }
    }
}
