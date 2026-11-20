using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.AI.Pathfinding;
using Sandbox.Game.AI.Pathfinding.RecastDetour;
using Sandbox.Game.Entities;
using Sandbox.Engine.Utils;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Inside a crewed hull, plan on the hull (docs/ground-operations.md, Traversal).
    ///
    /// <para><c>MyRDPathfinding.GetNavigationArea</c> answers with the planet area for any point
    /// within 500 m of a planet's surface before it looks at grids, and the planet navmesh holds
    /// voxels, trees and static grids only. So on the ground a dynamic hull's deck is not walkable
    /// at all and every goal aboard is planned on the terrain under it. This prefix answers for a
    /// point inside a crewed hull with that hull's own area, built exactly as vanilla builds it in
    /// space: a static grid with gravity gets <c>MyGridNavigationArea</c>, any other grid
    /// <c>MyDynamicGridNavigationArea</c>. Points outside every crewed hull are untouched.</para>
    ///
    /// <para>Applied by hand, never by attribute: a method this SE build renamed must not take
    /// plugin init down.</para>
    /// </summary>
    internal static class CrewNavigationArea
    {
        private static FieldInfo _cache;

        /// <summary>The hulls a crew lives on; set by <see cref="CrewPresence"/> on the game thread.</summary>
        internal static MyCubeGrid[] Hulls = new MyCubeGrid[0];

        internal static bool Bound { get; private set; }
        internal static string InactiveReason { get; private set; }
        internal static long Answered;

        internal static void TryPatch(Harmony harmony)
        {
            Bound = false;
            InactiveReason = null;
            try
            {
                if (harmony == null) { InactiveReason = "no Harmony instance"; return; }
                MethodInfo target = AccessTools.Method(typeof(MyRDPathfinding), "GetNavigationArea",
                    new[] { typeof(Vector3D), typeof(bool) });
                _cache = AccessTools.Field(typeof(MyRDPathfinding), "m_navigationAreaCache");
                if (target == null) { InactiveReason = "MyRDPathfinding.GetNavigationArea(Vector3D, bool) not found"; return; }
                if (_cache == null) { InactiveReason = "MyRDPathfinding.m_navigationAreaCache not found"; return; }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(CrewNavigationArea)
                    .GetMethod("Before", BindingFlags.NonPublic | BindingFlags.Static)));
                Bound = true;
            }
            catch (Exception ex)
            {
                InactiveReason = ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>The crewed hull whose box holds <paramref name="position"/>, or null.</summary>
        internal static MyCubeGrid HullAt(Vector3D position)
        {
            MyCubeGrid[] hulls = Hulls;
            for (int i = 0; i < hulls.Length; i++)
            {
                MyCubeGrid hull = hulls[i];
                if (hull == null || hull.Closed || hull.MarkedForClose || hull.IsStatic) continue;
                if (Inside(hull, position)) return hull;
            }
            return null;
        }

        /// <summary>True when <paramref name="position"/> lies in the hull's own box, 0.2 m in from
        /// its faces.</summary>
        internal static bool Inside(MyCubeGrid hull, Vector3D position)
        {
            Vector3D local = Vector3D.Transform(position, hull.PositionComp.WorldMatrixNormalizedInv);
            BoundingBoxD box = hull.PositionComp.LocalAABB;
            box.Inflate(-0.2);
            return box.Contains(local) == ContainmentType.Contains;
        }

        private static bool Before(MyRDPathfinding __instance, Vector3D position, bool createIfMissing, ref IMyNavigationArea __result)
        {
            MyCubeGrid hull = HullAt(position);
            if (hull == null) return true;
            var cache = _cache.GetValue(__instance) as Dictionary<long, IMyNavigationArea>;
            if (cache == null) return true;
            IMyNavigationArea area;
            if (!cache.TryGetValue(hull.EntityId, out area))
            {
                if (createIfMissing)
                {
                    if (hull.IsStatic) area = new MyGridNavigationArea(hull);
                    else if (MyFakes.ENABLE_DYNAMIC_GRID_PATHFINDING) area = new MyDynamicGridNavigationArea(hull);
                    else return true;
                    cache[hull.EntityId] = area;
                }
            }
            __result = area;
            Answered++;
            return false;
        }

        /// <summary>The kind of area vanilla's cache holds for a point, without creating one; for
        /// <c>crew visits</c>.</summary>
        internal static string AreaKind(Vector3D position)
        {
            try
            {
                var pathfinding = Sandbox.Game.AI.MyAIComponent.Static != null
                    ? Sandbox.Game.AI.MyAIComponent.Static.Pathfinding as MyRDPathfinding : null;
                if (pathfinding == null) return "none";
                IMyNavigationArea area = pathfinding.GetNavigationArea(position, false);
                if (area == null) return "none";
                string name = area.GetType().Name;
                return name.StartsWith("My", StringComparison.Ordinal) ? name.Substring(2).Replace("NavigationArea", "") : name;
            }
            catch { return "error"; }
        }
    }
}
