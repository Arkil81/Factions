using HarmonyLib;
using Sandbox.Game.Entities.Character;

namespace Factions
{
    /// <summary>FAC-440: observe native simulation only for explicitly tracked trial crew.
    /// This prefix leaves the original method and its inputs unchanged. No trial means no counters.</summary>
    [HarmonyPatch(typeof(MyCharacter))]
    internal static class CrewSimulationProbe
    {
        internal static bool Bound
        {
            get
            {
                var patches = Harmony.GetPatchInfo(typeof(MyCharacter).GetMethod(nameof(MyCharacter.Simulate)));
                if (patches == null) return false;
                foreach (var prefix in patches.Prefixes)
                    if (prefix.PatchMethod.DeclaringType == typeof(CrewSimulationProbe)) return true;
                return false;
            }
        }

        internal static bool MovementBound
        {
            get
            {
                var patches = Harmony.GetPatchInfo(typeof(MyCharacter).GetMethod(nameof(MyCharacter.UpdateZeroMovement)));
                if (patches == null) return false;
                foreach (var prefix in patches.Prefixes)
                    if (prefix.PatchMethod.DeclaringType == typeof(CrewSimulationProbe)) return true;
                return false;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MyCharacter.Simulate))]
        private static void BeforeSimulation(MyCharacter __instance)
        {
            CrewPresence.ObserveSimulation(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MyCharacter.UpdateZeroMovement))]
        private static bool BeforeZeroMovement(MyCharacter __instance, bool ___m_moveAndRotateCalled)
        {
            return CrewPresence.BeforeZeroMovement(__instance, ___m_moveAndRotateCalled);
        }
    }
}
