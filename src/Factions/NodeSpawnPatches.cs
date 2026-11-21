using HarmonyLib;
using Factions.Wire;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SpaceEngineers.Game.World;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Turns a respawn request naming <see cref="NodeSpawnSentinel"/> into a spawn beside that node's
    /// station (FAC-481).
    ///
    /// <c>HandleRespawnRequest</c> already has the branch this needs: with <c>spawnPosition</c> set
    /// and no ship id it calls <c>MyPlayer.SpawnAt(..., findFreePlace: true)</c> — a suit at a given
    /// transform. The network message never sets it; the prefix does, after
    /// <see cref="NodeSpawnManager.TryPlace"/> has re-run the policy. Every other request, and a
    /// sentinel on a server not running the simulation, falls through to vanilla, which finds no entity for
    /// a negative id and refuses the respawn — the client's screen reopens.
    ///
    /// <para>A refusal is returned as <c>false</c> without running vanilla, which is the same answer
    /// <c>FindRespawnById</c> would give and makes SE send its own failure event to the client.</para>
    /// </summary>
    [HarmonyPatch(typeof(MySpaceRespawnComponent), "HandleRespawnRequest")]
    internal static class NodeSpawnPatches
    {
        private static bool Prefix(bool newIdentity, long respawnEntityId, ref string respawnShipId,
            MyPlayer.PlayerId playerId, ref Vector3D? spawnPosition, ref Vector3? direction, ref Vector3? up,
            ref bool __result, out long __state)
        {
            __state = 0;
            long stationId;
            if (!NodeSpawnSentinel.TryDecode(respawnEntityId, out stationId)) return true;
            if ((SimulationGate.Deferred && !NodeSpawnManager.OnAgent) || newIdentity) return true;

            NodeSpawnManager manager = NodeSpawnManager.Instance;
            MyPlayer player = Sync.Players != null ? Sync.Players.GetPlayerById(playerId) : null;
            if (manager == null || player == null || player.Identity == null) return true;

            MatrixD spawn;
            string nodeId;
            if (!manager.TryPlace(player, stationId, out spawn, out nodeId))
            {
                __result = false;
                return false;
            }

            respawnShipId = null;
            spawnPosition = spawn.Translation;
            direction = (Vector3)spawn.Forward;
            up = (Vector3)spawn.Up;
            __state = player.Identity.IdentityId;
            return true;
        }

        private static void Postfix(bool __result, long __state)
        {
            if (__state == 0 || !__result) return;
            NodeSpawnManager manager = NodeSpawnManager.Instance;
            if (manager != null) manager.RecordSpawn(__state);
        }
    }
}
