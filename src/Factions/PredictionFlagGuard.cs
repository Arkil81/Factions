using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NLog;
using Sandbox.Game.Entities;

namespace Factions
{
    /// <summary>
    /// Skips SE's per-frame <c>MyCubeGrid.UpdatePredictionFlag</c> for a grid that is in the scene
    /// but has no physics body. On the server that method reads <c>Physics</c> without a null check
    /// once a player (a bot counts) controls the grid, and the exception escapes the parallel update
    /// and kills the process. A projector's preview grid seated by crew was the case on Main on
    /// 2026-11-25 (two crashes, server down until restarted by hand); <see cref="CrewPresence.MayCrew"/>
    /// removes that cause, and this guard keeps any other path to the same state from being fatal.
    /// The method only sets client-prediction flags, which mean nothing for a grid without a body.
    /// </summary>
    internal static class PredictionFlagGuard
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly HashSet<long> Reported = new HashSet<long>();

        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo target = AccessTools.Method(typeof(MyCubeGrid), "UpdatePredictionFlag");
                if (target == null) { Log.Error("Prediction guard: MyCubeGrid.UpdatePredictionFlag did not resolve; a controlled grid without physics stays fatal."); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(PredictionFlagGuard).GetMethod("Prefix",
                    BindingFlags.NonPublic | BindingFlags.Static)));
                Log.Info("Prediction guard: bound.");
            }
            catch (Exception ex)
            {
                Log.Error("Prediction guard: could not patch UpdatePredictionFlag: " + ex.Message);
            }
        }

        // Runs on the parallel update's worker threads.
        private static bool Prefix(MyCubeGrid __instance)
        {
            if (__instance == null || __instance.Physics != null || !__instance.InScene) return true;
            bool first;
            lock (Reported) first = Reported.Add(__instance.EntityId);
            if (first)
                Log.Warn("Prediction guard: skipped grid eid=" + __instance.EntityId + " name='" + __instance.DisplayName
                         + "' preview=" + __instance.IsPreview + " blocks=" + __instance.BlocksCount
                         + ": controlled, in scene, no physics.");
            return false;
        }
    }
}
