using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.AI.Navigation;
using Sandbox.Game.AI.Pathfinding;
using Sandbox.Game.AI.Pathfinding.RecastDetour;
using Sandbox.Game.Entities.Character;
using VRageMath;

namespace Factions
{
    // FAC-440: replace only intermediate waypoint tolerances during tracked crew steering.
    // Final interaction distances, navmesh clearance and other agents retain their native values.
    [HarmonyPatch(typeof(MyPathSteering), "SetNextTarget")]
    [PatchGroup("CrewCornerTolerance")]
    internal static class CrewCornerTolerance
    {
        [ThreadStatic] private static MyPathSteering _steering;
        private static readonly FieldInfo PathField = AccessTools.Field(typeof(MyPathSteering), "m_path");
        private static readonly FieldInfo PointsField = AccessTools.Field(typeof(MyRDPath), "m_pathPoints");
        private static readonly FieldInfo IndexField = AccessTools.Field(typeof(MyRDPath), "m_currentPointIndex");

        internal static bool Bound => HasPatch(typeof(MyPathSteering), "SetNextTarget", typeof(CrewCornerTolerance))
            && HasPatch(typeof(MyRDPath), "GetNextTarget", typeof(CrewCornerPathTolerance));

        private static bool HasPatch(Type target, string method, Type owner)
        {
            var patches = Harmony.GetPatchInfo(AccessTools.Method(target, method));
            if (patches == null) return false;
            foreach (var patch in patches.Transpilers)
                if (patch.PatchMethod.DeclaringType == owner) return true;
            return false;
        }

        [HarmonyPrefix]
        private static void Before(MyPathSteering __instance, out MyPathSteering __state)
        {
            __state = _steering;
            _steering = __instance;
        }

        [HarmonyFinalizer]
        private static Exception After(Exception __exception, MyPathSteering __state)
        {
            _steering = __state;
            return __exception;
        }

        private static float Adjust(float original, IMyPath path)
        {
            if (_steering == null || !(path is MyRDPath) || PointsField == null || IndexField == null) return original;
            var character = _steering.Parent.BotEntity as MyCharacter;
            float radius = CrewPresence.CornerRadius(character);
            if (radius <= 0) return original;
            var points = PointsField.GetValue(path) as List<Vector3D>;
            int index = (int)IndexField.GetValue(path);
            // A fresh path is empty here; steering applies the tolerance after its points are generated.
            if (points == null || points.Count < 3 || index >= points.Count - 1) return original;
            CrewPresence.ObserveCornerTolerance(character);
            return Math.Min(original, radius);
        }

        internal static float PathRadius(MyRDPath path) => Adjust(0.8f, path);
        internal static float SteeringRadius(float original) => _steering == null || PathField == null
            ? original : Adjust(original, PathField.GetValue(_steering) as IMyPath);

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            var getter = AccessTools.PropertyGetter(typeof(MyBotNavigation), "PathArrivalRadius");
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.Calls(getter))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CrewCornerTolerance), nameof(SteeringRadius)));
                    replaced++;
                }
            }
            if (replaced != 1) throw new InvalidOperationException("Unexpected native steering arrival radius sites: " + replaced);
        }
    }

    [HarmonyPatch(typeof(MyRDPath), nameof(MyRDPath.GetNextTarget))]
    [PatchGroup("CrewCornerTolerance")]
    internal static class CrewCornerPathTolerance
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float && (float)instruction.operand == 0.8f)
                {
                    // Keep labels/exception blocks on the replacement's first instruction.
                    instruction.opcode = OpCodes.Ldarg_0;
                    instruction.operand = null;
                    yield return instruction;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CrewCornerTolerance), nameof(CrewCornerTolerance.PathRadius)));
                    replaced++;
                }
                else yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("Unexpected native path arrival radius sites: " + replaced);
        }
    }
}
