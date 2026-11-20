using HarmonyLib;
using Sandbox.Game.World;
using SpaceEngineers.Game.World;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Hooks SE's respawn-ship placement so <see cref="RespawnPlacement"/> can replace the position
    /// it arrives at.
    ///
    /// <c>SpawnAtShip</c> is the whole scope: the respawn-screen options that hand out a pod or a
    /// rover. The two position methods are patched rather than <c>SpawnAtShip</c> itself because the
    /// position lives in a local there — reachable only where SE writes it.
    ///
    /// Both are private statics taking SE's own private <c>SpawnInfo</c> struct. Harmony matches
    /// patch parameters by name, so the struct is simply not declared and the three the placement
    /// needs are; SE's <c>out</c> parameters arrive as <c>ref</c>.
    ///
    /// <c>GetSpawnPositionInSpace</c> falls back to the planet path when it can find no free location
    /// near an asteroid, so the planet postfix can run nested inside the space one. Nothing here
    /// guards against that: <see cref="RespawnPlacement.Offer"/> decides once per spawn, and it reads
    /// the environment off the position it is handed rather than off which method produced it.
    /// </summary>
    [HarmonyPatch(typeof(MySpaceRespawnComponent))]
    internal static class RespawnPlacementPatches
    {
        [HarmonyPatch("SpawnAtShip")]
        [HarmonyPrefix]
        private static void BeforeSpawnAtShip(MyPlayer player, string respawnShipId, long? planetId)
        {
            if (!RespawnPlacement.Active) return;
            RespawnPlacement placement = RespawnPlacement.Instance;
            if (placement != null) placement.Begin(player, respawnShipId, planetId);
        }

        [HarmonyPatch("SpawnAtShip")]
        [HarmonyFinalizer]
        private static void AfterSpawnAtShip()
        {
            if (!RespawnPlacement.Active) return;
            // A finalizer rather than a postfix: an early return inside SpawnAtShip is ordinary
            // control flow and still runs one, but a throw would leave the spawn open for the next
            // player through if this only ran on the happy path.
            RespawnPlacement placement = RespawnPlacement.Instance;
            if (placement != null) placement.End();
        }

        [HarmonyPatch("GetSpawnPositionNearPlanet")]
        [HarmonyPostfix]
        private static void AfterGetSpawnPositionNearPlanet(
            ref Vector3D position, ref Vector3 forward, ref Vector3 up)
        {
            if (!RespawnPlacement.Active) return;
            RespawnPlacement placement = RespawnPlacement.Instance;
            if (placement != null) placement.Offer(ref position, ref forward, ref up);
        }

        [HarmonyPatch("GetSpawnPositionInSpace")]
        [HarmonyPostfix]
        private static void AfterGetSpawnPositionInSpace(
            ref Vector3D position, ref Vector3 forward, ref Vector3 up)
        {
            if (!RespawnPlacement.Active) return;
            RespawnPlacement placement = RespawnPlacement.Instance;
            if (placement != null) placement.Offer(ref position, ref forward, ref up);
        }
    }
}
