using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// A restore's own block placement inside a Factions station's safe zone. The zone allows only
    /// <c>LandingGearLock</c>, and <c>MyCubeGrid.BuildBlockRequestInternal</c> asks it for
    /// <c>Building</c> on the grid (<c>IsActionAllowed(MyEntity, …)</c>) and on the block's box
    /// (<c>IsActionAllowed(BoundingBoxD, …)</c>, from <c>TestPlacementAreaCube</c>). While
    /// <see cref="RestorationPlacementScope"/> is open — around <see cref="RestorationService"/>'s
    /// own synchronous call, on the game thread — both are answered yes: for the grids of the ship being restored,
    /// and for the box of the block being placed. Every other question gets the zone's own answer,
    /// and the zone's <c>AllowedActions</c> are never changed, so a player still cannot build there.
    ///
    /// <para>Outside a restore the prefixes cost one thread-static read and fall through. A method
    /// that does not resolve disables its prefix and logs an error — placement in a station zone is
    /// then refused (<c>RESTORE_PLACE_REFUSED … reason=safezone-building</c>) and nothing else breaks.</para>
    /// </summary>
    static class RestorationSafeZonePatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetLogger(FactionsPlugin.PluginName);

        private static bool Resolve(MethodBase method, string what)
        {
            if (method == null)
                Log.Error("Restoration safe-zone patch: MySessionComponentSafeZones.IsActionAllowed(" + what
                          + ") did not resolve; a restore cannot place blocks inside a station's safe zone.");
            return method != null;
        }

        [HarmonyPatch]
        static class OnEntity
        {
            [HarmonyPrepare]
            private static bool Prepare() { return Resolve(Target(), "MyEntity"); }

            [HarmonyTargetMethod]
            private static MethodBase Target()
            {
                return AccessTools.Method(typeof(MySessionComponentSafeZones), "IsActionAllowed",
                    new[] { typeof(MyEntity), typeof(MySafeZoneAction), typeof(long), typeof(ulong) });
            }

            [HarmonyPrefix]
            private static bool Prefix(MyEntity entity, MySafeZoneAction action, ref bool __result)
            {
                if (!RestorationPlacementScope.IsOpen || action != MySafeZoneAction.Building || entity == null) return true;
                if (!RestorationPlacementScope.AllowsBuildingOn(entity.EntityId)) return true;
                __result = true;
                return false;
            }
        }

        [HarmonyPatch]
        static class OnArea
        {
            [HarmonyPrepare]
            private static bool Prepare() { return Resolve(Target(), "BoundingBoxD"); }

            [HarmonyTargetMethod]
            private static MethodBase Target()
            {
                return AccessTools.Method(typeof(MySessionComponentSafeZones), "IsActionAllowed",
                    new[] { typeof(BoundingBoxD), typeof(MySafeZoneAction), typeof(long), typeof(ulong) });
            }

            [HarmonyPrefix]
            private static bool Prefix(MySafeZoneAction action, ref bool __result)
            {
                if (action != MySafeZoneAction.Building || !RestorationPlacementScope.AllowsBuildingInArea()) return true;
                __result = true;
                return false;
            }
        }
    }
}
