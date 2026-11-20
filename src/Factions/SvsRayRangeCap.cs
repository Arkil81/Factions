using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Factions
{
    /// <summary>
    /// Caps the range of Synthetic Vision Systems' server-side cockpit ray (Workshop 3810223083).
    ///
    /// <para>SVS casts each seated player's camera ray in server physics, up to 1,000 km, and publishes the
    /// nearest hit's position, distance and entity id as a cockpit property a programmable block reads. The
    /// server holds every materialised hull within <c>ObservationRadiusKm</c> of any player, so the ray found
    /// grids past the client's sync distance that the fog of war withholds (docs/mod-compatibility.md).
    /// This prefix clamps the <c>range</c> argument of <c>ProgrammableBlockRayApiRuntime.CastRay</c> to
    /// <see cref="MaxRangeM"/>. The mod's own limit is a compile-time constant, so the cast is the lever.</para>
    ///
    /// <para>Applied by hand at session load, never by attribute: SE compiles mod scripts while the world
    /// loads, after plugin init, and a mod that renamed the method must not take anything else down.</para>
    /// </summary>
    internal static class SvsRayRangeCap
    {
        private const string TypeName = "SyntheticVisionSystems.ProgrammableBlockRayApiRuntime";
        private const string MethodName = "CastRay";

        /// <summary>The cap in metres; 0 or less leaves the mod's own range.</summary>
        internal static double MaxRangeM;

        /// <summary>Methods carrying the prefix after the last <see cref="TryPatch"/>, 0 when SVS is not loaded.</summary>
        internal static int Patched { get; private set; }
        internal static string InactiveReason { get; private set; }

        /// <summary>Patches every loaded copy of the SVS type not already patched. Returns false when SVS
        /// is loaded and could not be patched; <see cref="InactiveReason"/> then says why.</summary>
        internal static bool TryPatch(Harmony harmony, double maxRangeM)
        {
            MaxRangeM = maxRangeM;
            Patched = 0;
            InactiveReason = null;
            if (harmony == null) { InactiveReason = "no Harmony instance"; return false; }
            var targets = new List<MethodInfo>();
            bool typeFound = false;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = asm.GetType(TypeName, false); }
                catch (Exception) { continue; }
                if (type == null) continue;
                typeFound = true;
                MethodInfo method = AccessTools.Method(type, MethodName);
                if (method != null && HasRangeParameter(method)) targets.Add(method);
            }
            if (!typeFound) return true;
            if (targets.Count == 0)
            {
                InactiveReason = TypeName + "." + MethodName + "(..., double range) not found";
                return false;
            }
            try
            {
                var prefix = new HarmonyMethod(typeof(SvsRayRangeCap)
                    .GetMethod(nameof(Before), BindingFlags.NonPublic | BindingFlags.Static));
                foreach (MethodInfo target in targets)
                {
                    Patches info = Harmony.GetPatchInfo(target);
                    if (info == null || !info.Owners.Contains(harmony.Id)) harmony.Patch(target, prefix: prefix);
                    Patched++;
                }
                return true;
            }
            catch (Exception ex)
            {
                InactiveReason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static bool HasRangeParameter(MethodInfo method)
        {
            foreach (ParameterInfo p in method.GetParameters())
                if (p.Name == "range" && p.ParameterType == typeof(double)) return true;
            return false;
        }

        private static void Before(ref double range)
        {
            double cap = MaxRangeM;
            if (cap > 0.0 && range > cap) range = cap;
        }
    }
}
