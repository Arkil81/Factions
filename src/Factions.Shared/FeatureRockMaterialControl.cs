using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using VRage.Game;                       // MyVoxelMaterialDefinition

namespace Factions
{
    /// <summary>
    /// Makes a feature's authored material decide what its rock is actually made of.
    ///
    /// <para><b>Why a patch at all.</b> SE picks an asteroid's ore from a global pool with no
    /// per-call bias: <c>MyCompositeShapes.FillMaterials</c> walks every loaded voxel material and,
    /// on generator 4, files everything whose <c>MinedOre</c> is not "Stone" into one deposit list.
    /// So an authored Ice band produced rock holding gold, uranium and iron — measured live
    /// 2026-08-25. <c>MyStorageBase.OverwriteAllMaterials</c>, the obvious lever, has an empty body
    /// on <c>MyOctreeStorage</c> (FAC-075), and painting materials afterwards is a LOD0-only
    /// <c>WriteRange</c> — 64M voxels for a 400 m rock, which is not affordable.</para>
    ///
    /// <para><b>Why it is shape-neutral.</b> The whole material setup runs inside
    /// <c>MyRandom.Instance.PushSeed(generatorSeed)</c> and its matching <c>Dispose</c>, which
    /// restores the RNG state on exit. Nothing consumed while choosing materials can reach shape
    /// generation, so narrowing the pool changes the ore and never the geometry. That property is
    /// what lets a client rebuild the same rock from the same seed.</para>
    ///
    /// <para><b>Fail-open.</b> If the reflected type or method ever moves, <see cref="Active"/> stays
    /// false and rock spawns with vanilla's mix — visibly wrong, never broken. <see cref="Active"/>
    /// is the signal a client needs in order to fall back in step rather than render rock the server
    /// disagrees with (FAC-090).</para>
    /// </summary>
    public static class FeatureRockMaterialControl
    {
        /// <summary>True once the patch is applied and material control is really in force. False
        /// means every rock carries vanilla's global ore mix — the state a client must be told about,
        /// or the two sides will disagree about what the same rock is made of.</summary>
        public static bool Active { get; private set; }

        /// <summary>Why control is unavailable, for the boot log and the bridge. Null when active.</summary>
        public static string InactiveReason { get; private set; }

        /// <summary>How many times the postfix has actually narrowed a list.
        /// <para><see cref="Active"/> only says the patch was <i>installed</i>. It was true for a
        /// whole live session while the patched method was never reached at all, because the
        /// generator version being passed made SE return before it built any material list. A count
        /// of zero after rock has spawned is therefore the real failure signal, and the one worth
        /// reading.</para></summary>
        public static long TimesApplied;

        /// <summary>The MinedOre the rock currently being generated should be made of.
        /// <para>Thread-static because SE may generate shapes off the update thread; a shared field
        /// would let one rock's material leak into another's.</para></summary>
        [ThreadStatic] private static string _wantedMinedOre;

        /// <summary>Ores SE actually accepted while building the rock currently in scope, and the
        /// count it withheld.
        /// <para>This exists because every diagnostic so far has reported what we <i>asked</i> for.
        /// A rock authored Iron measured Stone 99% / Ice 1% / Nickel 0% with the patch demonstrably
        /// firing — and that is consistent with two opposite causes: a pool correctly narrowed to
        /// {Stone, Iron} where the ore is simply never <i>placed</i>, or a pool never narrowed at all
        /// for that rock. Nothing recorded which. This does.</para></summary>
        [ThreadStatic] private static List<string> _acceptedThisRock;
        [ThreadStatic] private static int _rejectedThisRock;

        /// <summary>Scopes <paramref name="minedOre"/> over one shape construction. Always pair with
        /// <see cref="Clear"/> in a finally — a leaked value would silently repaint later rock.</summary>
        public static void Want(string minedOre)
        {
            _wantedMinedOre = minedOre;
            if (_acceptedThisRock == null) _acceptedThisRock = new List<string>();
            _acceptedThisRock.Clear();
            _rejectedThisRock = 0;
        }

        public static void Clear() { _wantedMinedOre = null; }

        // ---- Seed-keyed fallback -----------------------------------------------------------
        //
        // An explicit Want()/Clear() scope only covers generation WE initiate. It does not cover the
        // one that matters most on a client: SE regenerating a rock from its replicated storage.
        // A pristine rock's replicated "content" is a regeneration instruction, not voxels
        // (FAC-092), so the first time anyone drills a rock the server replicates it and the client
        // rebuilds the untouched regions itself -- through MyCompositeShapeProvider.ReadFrom ->
        // InitFromState, with no scope of ours anywhere on the stack. Measured live 2026-08-25: a
        // rock that rendered iron-only sprouted nickel spots on the first cut, while the server's
        // own storage stayed Iron 1% / Stone 99% and mining the phantom nickel returned stone.
        //
        // The fix is to stop needing to know WHO is generating. Every path -- ours and SE's --
        // funnels through MyCompositeShapes.ProceduralGenerator/CombinedGenerator, and both take the
        // asteroid seed as a parameter. Registering seed -> ore when a rock is first planned means
        // the scope can be re-established from the seed alone, whoever asks for it and whenever.

        private static readonly Dictionary<int, string> _oreBySeed = new Dictionary<int, string>();
        private static readonly object _seedLock = new object();

        /// <summary>Bound on the seed map. A session sees thousands of rocks and each entry is tiny,
        /// but it must not grow without limit across a long-running server. Oldest-cleared rather
        /// than LRU: losing an entry costs one rock its narrowing until it is re-registered, which is
        /// visible and self-correcting, and an LRU here would be machinery for no gain.</summary>
        private const int MaxSeedEntries = 16384;

        /// <summary>The seed currently being generated on this thread, set by the generator patch.
        /// Zero when no generation is in flight.</summary>
        [ThreadStatic] private static int _seedInFlight;
        [ThreadStatic] private static bool _seedInFlightSet;

        /// <summary>Records what ore a rock's seed should produce, so its material can be re-imposed
        /// on any later regeneration -- including one SE starts on its own. Safe to call repeatedly
        /// for the same seed.</summary>
        public static void Register(int shapeSeed, string minedOre)
        {
            if (string.IsNullOrEmpty(minedOre)) return;
            lock (_seedLock)
            {
                if (_oreBySeed.Count >= MaxSeedEntries && !_oreBySeed.ContainsKey(shapeSeed))
                    _oreBySeed.Clear();
                _oreBySeed[shapeSeed] = minedOre;
            }
        }

        /// <summary>Live entry count, so a diagnostic can tell "no ore registered for this seed"
        /// apart from "the registry was never populated at all".</summary>
        public static int RegisteredSeeds { get { lock (_seedLock) { return _oreBySeed.Count; } } }

        /// <summary>The ore in force: an explicit scope if one is open, otherwise whatever the seed
        /// currently being generated was registered as. The explicit scope wins so a caller can
        /// always override, and so the server's existing behaviour is unchanged.</summary>
        private static string WantedOre()
        {
            string explicitScope = _wantedMinedOre;
            if (explicitScope != null) return explicitScope;
            if (!_seedInFlightSet) return null;

            string ore;
            lock (_seedLock)
            {
                if (!_oreBySeed.TryGetValue(_seedInFlight, out ore)) return null;
            }
            return ore;
        }

        /// <summary>What SE accepted, and how much was withheld, for the rock last built on this
        /// thread. Read after the shape call and before the next <see cref="Want"/>.</summary>
        public static string LastPoolSummary()
        {
            List<string> a = _acceptedThisRock;
            if (a == null || a.Count == 0) return "pool=(none) withheld=" + _rejectedThisRock;
            var sorted = new List<string>(a);
            sorted.Sort(StringComparer.Ordinal);
            return "pool=" + string.Join("/", sorted.ToArray()) + " withheld=" + _rejectedThisRock;
        }

        /// <summary>Applies the patch. Safe to call once at plugin start; reports rather than throws.</summary>
        public static void TryApply(Harmony harmony)
        {
            if (harmony == null) { InactiveReason = "no Harmony instance"; return; }
            try
            {
                Type shapes = AccessTools.TypeByName("Sandbox.Game.World.Generator.MyCompositeShapes");
                if (shapes == null) { InactiveReason = "MyCompositeShapes not found"; return; }

                // IsAcceptedAsteroidMaterial is the gate at the top of FillMaterials' loop, so it
                // governs EVERY path that builds the material lists. That is the whole reason it is
                // the target.
                //
                // The first attempt patched FilterKindDuplicates instead, which the constructor
                // calls -- and missed a second, lazy fill guarded on `m_coreMaterials == null` that
                // rebuilds the lists and never calls it. Rock kept coming out with vanilla's mix
                // while the patch reported itself installed and firing, because it *was* firing, on
                // the other path. Patch the gate, not one of the consumers.
                //
                // It is also private STATIC with plain parameters: MyCompositeShapes is an internal
                // *struct*, so anything needing __instance could not name its type from here.
                MethodInfo target = AccessTools.Method(shapes, "IsAcceptedAsteroidMaterial",
                    new[] { typeof(MyVoxelMaterialDefinition), typeof(int) });
                if (target == null) { InactiveReason = "MyCompositeShapes.IsAcceptedAsteroidMaterial not found"; return; }

                MethodInfo postfix = typeof(FeatureRockMaterialControl)
                    .GetMethod("RejectUnwantedOre", BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));

                // The seed scope has to open on the CONSTRUCTOR, and this is the whole subtlety.
                //
                // The first attempt opened it on ProceduralGenerator/CombinedGenerator, which is too
                // late by a wide margin: MyCompositeShapes' constructor calls FillMaterials and then
                // FilterKindDuplicates/LimitMaterials on all three lists, so every material decision
                // is already made before either generator method is entered. Measured live
                // 2026-08-25 -- phantom ore still appeared on first drill with both generators
                // patched, because the narrowing postfix ran with no scope open.
                //
                // The constructor takes asteroidSeed as a parameter, so it can establish the scope
                // for exactly the rock being built, whoever started the build. That is what covers
                // SE regenerating a rock from replicated storage, where nothing of ours is on the
                // stack. Private, on an internal STRUCT: the patch names neither, and does not need
                // to -- Harmony binds asteroidSeed by name.
                ConstructorInfo ctor = AccessTools.Constructor(shapes,
                    new[] { typeof(int), typeof(int), typeof(int) });
                if (ctor == null)
                {
                    InactiveReason = "MyCompositeShapes(int,int,int) constructor not found"
                                   + " -- material could not be re-imposed on SE's own regeneration";
                    return;
                }

                MethodInfo enter = typeof(FeatureRockMaterialControl)
                    .GetMethod("EnterSeedScope", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo leave = typeof(FeatureRockMaterialControl)
                    .GetMethod("LeaveSeedScope", BindingFlags.NonPublic | BindingFlags.Static);

                harmony.Patch(ctor, prefix: new HarmonyMethod(enter), postfix: new HarmonyMethod(leave));
                ScopedSites = 1;

                // Also scoped over the generator bodies. Not where the materials are chosen, so this
                // is not what makes the feature work -- it is here so that anything SE moves into
                // those methods later still lands inside a scope rather than silently outside one.
                foreach (string name in new[] { "ProceduralGenerator", "CombinedGenerator" })
                {
                    MethodInfo gen = AccessTools.Method(shapes, name,
                        new[] { typeof(int), typeof(int), typeof(float) });
                    if (gen == null) continue;
                    harmony.Patch(gen, prefix: new HarmonyMethod(enter), postfix: new HarmonyMethod(leave));
                    ScopedSites++;
                }

                Active = true;
                InactiveReason = null;
            }
            catch (Exception ex)
            {
                Active = false;
                InactiveReason = ex.Message;
            }
        }

        /// <summary>Hides every ore but the authored one from SE's material fill.
        /// <para><b>Stone is always let through.</b> It is what SE files into the surface list, and
        /// emptying every list makes <c>FillMaterials</c> throw outright — so the rock becomes stone
        /// veined with exactly the ore the band names, which is also what a real asteroid looks
        /// like. Rejecting only what SE would otherwise have accepted keeps this subtractive: it can
        /// never introduce an ore, only withhold one.</para></summary>
        private static void Accept(string ore)
        {
            List<string> a = _acceptedThisRock;
            if (a == null) return;
            for (int i = 0; i < a.Count; i++) if (a[i] == ore) return;
            a.Add(ore);
        }

        /// <summary>How many call sites open a seed scope. The constructor is the one that matters
        /// and is required; the generator bodies are additional cover. Reported at boot so "the patch
        /// is installed" and "the patch is installed where the decision is made" can be told apart --
        /// a distinction that cost a live session.</summary>
        public static int ScopedSites { get; private set; }

        /// <summary>Opens a seed scope for the rock SE is about to build. Nested generation does not
        /// occur -- a generator call builds one asteroid and returns -- but the previous value is
        /// still restored rather than cleared, so a future nesting cannot silently unscope a caller.</summary>
        private struct SeedScope
        {
            public int Seed;
            public bool Set;
        }

        /// <summary>
        /// Takes the seed POSITIONALLY, as <c>__1</c>, rather than by name.
        ///
        /// <para>The same patch serves the constructor <c>(generatorSeed, asteroidSeed, version)</c>
        /// and the generator bodies <c>(version, seed, size)</c>. The seed is the second argument of
        /// both, but it is called <c>asteroidSeed</c> in one and <c>seed</c> in the other, so binding
        /// by name would silently fail to bind on one of them -- and a scope that never opens is
        /// exactly the failure this whole mechanism exists to prevent.</para>
        /// </summary>
        private static void EnterSeedScope(int __1, out SeedScope __state)
        {
            __state = new SeedScope { Seed = _seedInFlight, Set = _seedInFlightSet };
            _seedInFlight = __1;
            _seedInFlightSet = true;
        }

        private static void LeaveSeedScope(SeedScope __state)
        {
            _seedInFlight = __state.Seed;
            _seedInFlightSet = __state.Set;
        }

        private static void RejectUnwantedOre(MyVoxelMaterialDefinition material, ref bool __result)
        {
            // SE refused it on its own terms (SpawnsInAsteroids, Min/MaxVersion). Never widen -- but
            // DO record it, because "the wanted ore was rejected before we ever saw it" is one of the
            // two causes this diagnostic exists to separate.
            string wanted = WantedOre();
            if (!__result)
            {
                if (material != null && !string.IsNullOrEmpty(material.MinedOre)
                    && string.Equals(material.MinedOre, wanted, StringComparison.OrdinalIgnoreCase))
                    Accept("!" + material.MinedOre + "-REFUSED-BY-SE");
                return;
            }
            if (wanted == null || material == null) return;

            string ore = material.MinedOre;
            if (string.IsNullOrEmpty(ore)) return;

            if (string.Equals(ore, "Stone", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ore, wanted, StringComparison.OrdinalIgnoreCase))
            {
                Accept(ore);
                return;
            }

            __result = false;
            _rejectedThisRock++;
            TimesApplied++;
        }
    }
}
