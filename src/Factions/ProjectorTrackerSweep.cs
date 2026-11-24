using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Prunes SE's static <c>MyBlueprintIdTracker.TrackedIdToProjector</c> of projectors that have closed. A
    /// projector that loads a projection maps every reserved block id there, and SE unmaps them only when the
    /// projection is removed outright; <c>MyProjectorBase.Closing</c> does that only while the projector is
    /// projecting, so an unpowered projector, or one whose projection was hidden and kept, closes with its ids
    /// still mapped. Each entry keeps the projector, its grid and its preview grids reachable, and fleet churn
    /// turns that into unbounded heap growth. The removal goes through <c>MyBlueprintIdTracker.OnRemove</c>, as
    /// SE's own does, so its <c>OnTrackedIdRemoved</c> listeners hear it. Runs on the game thread.
    /// </summary>
    internal static class ProjectorTrackerSweep
    {
        private const int IntervalFrames = 600;

        private static readonly FieldInfo MapField = typeof(MyBlueprintIdTracker)
            .GetField("TrackedIdToProjector", BindingFlags.Static | BindingFlags.NonPublic);

        private static readonly Dictionary<MyProjectorBase, HashSet<long>> Stale =
            new Dictionary<MyProjectorBase, HashSet<long>>();
        private static readonly List<long> Orphans = new List<long>();

        private static int _frame;
        private static long _removedIds, _removedProjectors;
        private static int _lastCount;
        private static bool _missingReported;

        internal static void Tick()
        {
            if (++_frame < IntervalFrames) return;
            _frame = 0;
            if (MySession.Static == null) return;
            Sweep();
        }

        /// <summary>Unmaps every id whose projector is null, closed, or on a closed grid; returns how many.</summary>
        internal static int Sweep()
        {
            var map = MapField != null ? MapField.GetValue(null) as Dictionary<long, MyProjectorBase> : null;
            if (map == null)
            {
                if (!_missingReported)
                {
                    _missingReported = true;
                    FactionsPlugin.Log.Warn("Projector tracker sweep disabled: MyBlueprintIdTracker.TrackedIdToProjector not found.");
                }
                return 0;
            }
            Stale.Clear();
            Orphans.Clear();
            foreach (KeyValuePair<long, MyProjectorBase> kv in map)
            {
                MyProjectorBase p = kv.Value;
                if (p == null) { Orphans.Add(kv.Key); continue; }
                if (!p.Closed && p.CubeGrid != null && !p.CubeGrid.Closed) continue;
                HashSet<long> ids;
                if (!Stale.TryGetValue(p, out ids)) { ids = new HashSet<long>(); Stale[p] = ids; }
                ids.Add(kv.Key);
            }
            int removed = Orphans.Count;
            foreach (long id in Orphans) map.Remove(id);
            foreach (KeyValuePair<MyProjectorBase, HashSet<long>> kv in Stale)
            {
                try { MyBlueprintIdTracker.OnRemove(kv.Key, kv.Value); }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn("Projector tracker sweep: a removal listener threw: " + ex.Message);
                }
                // OnRemove skips its work while the tracker is between sessions; never leave a closed projector mapped.
                foreach (long id in kv.Value) map.Remove(id);
                removed += kv.Value.Count;
            }
            _removedProjectors += Stale.Count;
            Stale.Clear();
            Orphans.Clear();
            _lastCount = map.Count;
            _removedIds += removed;
            return removed;
        }

        internal static string Census()
        {
            return "projectorIds count=" + _lastCount + " removedTotal=" + _removedIds
                 + " projectorsRemoved=" + _removedProjectors;
        }
    }
}
