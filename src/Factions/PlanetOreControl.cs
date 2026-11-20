using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels.Planet;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Applies the recipe's authored ore map to a planet, at the one instant SE reads it.
    ///
    /// <para><b>Why here and not anywhere earlier.</b>
    /// <c>MyPlanetGeneratorDefinition.OreMappings</c> has exactly one consumer — the
    /// <c>MyPlanetMaterialProvider</c> constructor — which bakes it into a decode table and never
    /// re-reads it. That constructor is reached only from the <b>private</b>
    /// <c>MyPlanetStorageProvider.Init(long seed, bool loadTextures)</c>, and it runs after every
    /// mod's <c>.sbc</c> and <c>LoadData</c> have had their say — which an edit at Torch's
    /// <c>Loading</c> state does not.</para>
    ///
    /// <para><b>The private overload is the only one both paths reach, and that is not obvious.</b>
    /// A body the world builder creates arrives through the public
    /// <c>Init(long, MyPlanetGeneratorDefinition, double, bool)</c>; a body already in the save does
    /// not touch it at all. Its storage is deserialized, so it arrives through
    /// <c>ReadFrom(int, Stream, int, ref bool)</c>, which reads the generator's subtype name out of
    /// the stream, resolves the definition itself and calls the private overload directly. Patching
    /// the public one covers new bodies and silently misses every existing planet — measured on the
    /// live world 2026-09-20, where an authored map loaded and no body applied it.</para>
    ///
    /// <para><b>The definition is restored immediately.</b> Generators are shared: a second body on
    /// the same definition, or a later <c>factions world</c> run, must not inherit the first body's
    /// authored map. The provider reads the array synchronously inside <c>Init</c>, so a postfix put
    /// the original back before anything else can observe the change.</para>
    ///
    /// <para><b>Server-side only, and the client still sees the original.</b> A client builds its own
    /// provider from its own copy of the definitions at join, so it renders the generator's material
    /// where the server has the authored one. Yields are the server's, so it misleads rather than
    /// cheats — the same trade docs/client-plugin.md records for feature rocks. Closing it needs the
    /// map on the client before its world loads, which is unsolved; until then this deliberately does
    /// not live in <c>Factions.Shared</c>, because compiling it into the client without that delivery
    /// would put a lever there that nothing can pull.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class PlanetOreControl
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>What the prefix stashes so the postfix can put the definition back. Null when
        /// the body was left alone, which is why it is a class and not a bare array: a body whose
        /// authored map is empty also stashes an array, and the two must stay distinguishable.</summary>
        private sealed class Restore
        {
            public MyPlanetOreMapping[] Original;
        }

        private static PlanetOrePolicy _policy;

        /// <summary>True once a target has resolved and a policy with at least one authored body is
        /// loaded. "The patch is bound" and "the patch has something to do" are separate claims.</summary>
        internal static bool Active
        {
            get { return Bound && _policy != null && _policy.Count > 0; }
        }

        /// <summary>Whether the Harmony target resolved at all.</summary>
        internal static bool Bound { get; private set; }

        /// <summary>Bodies whose ore map was replaced this session.</summary>
        internal static int TimesApplied { get; private set; }

        /// <summary>Bodies that kept their generator's own map — the denominator. Applied without
        /// this is a count with nothing to be a fraction of.</summary>
        internal static int TimesInherited { get; private set; }

        /// <summary>Hands the authored policy to the patch. Called from plugin <c>Init</c>, which is
        /// before any session load and therefore before the first planet is built.</summary>
        internal static void Load(PlanetOrePolicy policy)
        {
            _policy = policy;
        }

        [HarmonyPrepare]
        private static bool Prepare()
        {
            MethodInfo target = ResolveTarget();
            Bound = target != null;
            if (!Bound)
                Log.Error("Planet ore control: MyPlanetStorageProvider.Init(long, bool) did not "
                        + "resolve, so every body keeps its generator's own ore map and any authored "
                        + "map is NOT in effect.");
            return Bound;
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            return ResolveTarget();
        }

        private static MethodInfo ResolveTarget()
        {
            // Private; the name has to be a string. See the class remarks for why this overload and
            // not the public four-argument one.
            return AccessTools.Method(typeof(MyPlanetStorageProvider), "Init",
                new[] { typeof(long), typeof(bool) });
        }

        [HarmonyPrefix]
        private static void Prefix(MyPlanetStorageProvider __instance, long seed, out Restore __state)
        {
            __state = null;
            try
            {
                // Both callers have already assigned Generator before this runs -- the public
                // overload from its argument, ReadFrom from the subtype name in the storage stream.
                MyPlanetGeneratorDefinition generator = __instance == null ? null : __instance.Generator;
                if (_policy == null || generator == null) { CountInherited(); return; }

                string subtype = generator.Id.SubtypeName;
                IList<RecipeOreMapping> authored;
                if (!_policy.TryGet(subtype, unchecked((int)seed), out authored)) { CountInherited(); return; }

                string why;
                MyPlanetOreMapping[] replacement = Convert(authored, out why);
                if (replacement == null)
                {
                    // Refused whole rather than applied in part: a map missing some of its entries is
                    // a body nobody designed, and it would read as a working feature.
                    Log.Error("Planet ore map REFUSED: generator=" + subtype + " seed=" + seed
                            + " (" + why + "); this body keeps its generator's own map.");
                    CountInherited();
                    return;
                }

                __state = new Restore { Original = generator.OreMappings };
                generator.OreMappings = replacement;
                TimesApplied++;
                Log.Info("Planet ore map applied: generator=" + subtype + " seed=" + seed
                       + " entries=" + replacement.Length);
            }
            catch (Exception ex)
            {
                __state = null;
                Log.Warn(ex, "Planet ore control: prefix failed; this body keeps its generator's own map.");
            }
        }

        [HarmonyPostfix]
        private static void Postfix(MyPlanetStorageProvider __instance, Restore __state)
        {
            try
            {
                if (__state == null || __instance == null || __instance.Generator == null) return;
                __instance.Generator.OreMappings = __state.Original;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Planet ore control: could not restore the generator's own ore map.");
            }
        }

        private static void CountInherited()
        {
            TimesInherited++;
        }

        /// <summary>Builds SE's array from the authored entries, or null with a reason. A material
        /// this world did not resolve is the whole reason this can fail: SE's own loop skips one
        /// silently, which would leave a band of the map missing and nothing saying so.</summary>
        private static MyPlanetOreMapping[] Convert(IList<RecipeOreMapping> authored, out string why)
        {
            why = null;
            var result = new MyPlanetOreMapping[authored.Count];
            for (int i = 0; i < authored.Count; i++)
            {
                RecipeOreMapping m = authored[i];
                if (MyDefinitionManager.Static != null
                    && MyDefinitionManager.Static.GetVoxelMaterialDefinition(m.Material) == null)
                {
                    why = "voxel material '" + m.Material + "' on Value " + m.Value
                        + " does not exist in this world";
                    return null;
                }

                var mapping = new MyPlanetOreMapping
                {
                    Value = m.Value,
                    Type  = m.Material,
                    Start = m.Start,
                    Depth = m.Depth,
                };
                if (!string.IsNullOrEmpty(m.TargetColor))
                {
                    // SE parses the hex in the setter, so a malformed colour throws here rather than
                    // reading back as "no tint". Named, because the whole map is refused over it.
                    try { mapping.TargetColor = m.TargetColor; }
                    catch (Exception ex)
                    {
                        why = "TargetColor '" + m.TargetColor + "' on Value " + m.Value
                            + " is not a colour SE can parse (" + ex.Message + ")";
                        return null;
                    }
                }
                if (m.ColorInfluence.HasValue) mapping.ColorInfluence = m.ColorInfluence.Value;
                result[i] = mapping;
            }
            return result;
        }
    }
}
