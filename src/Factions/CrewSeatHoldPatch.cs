using Factions.Core;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>Refuses a bot the seat <see cref="CrewSeatYield"/> is holding for a player. The AI
    /// reservation stops only the native seat search; a bot that chose the seat before the hold, the
    /// populator's spawn placement and Factions' own crew all end in this call.</summary>
    [HarmonyPatch(typeof(MyCockpit), nameof(MyCockpit.AttachPilot),
        new[] { typeof(MyCharacter), typeof(int), typeof(bool), typeof(bool), typeof(bool) })]
    static class CrewSeatHoldPatch
    {
        [HarmonyPrefix]
        static bool Prefix(MyCockpit __instance, MyCharacter pilot, bool calledFromInit)
        {
            if (pilot == null || calledFromInit || !CrewSeatYield.Held(__instance)) return true;
            MyPlayer.PlayerId id;
            bool realPlayer = pilot.GetPlayerId(out id) && id.SerialId == 0;
            return !SeatYield.RefusesBot(true, realPlayer, false);
        }
    }
}
