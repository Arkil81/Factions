using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Havok;
using Factions.Core;
using HarmonyLib;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using VRage.ModAPI;

namespace Factions
{
    /// <summary>
    /// Times SE's Havok step, so "the frame took 22 ms and 1.4 ms of it was ours" can say what the
    /// other 20 ms was instead of leaving it as a subtraction.
    ///
    /// <para><b>Why the frame envelope is not enough.</b> <c>TICK_STALL</c> already measures the gap
    /// between two <c>Update()</c> calls and reports this plugin's own cost beside it, which
    /// separates us from everything else — but "everything else" is Torch, every other plugin, entity
    /// updates, replication and physics in one number. The hypothesis this instrument exists to test
    /// names physics specifically, and a subtraction cannot confirm or refute it.</para>
    ///
    /// <para><b>One patch, one method per frame.</b> <c>StepWorlds</c> is called once per simulation
    /// step, not once per grid, so the stopwatch pair costs the same whether the world holds fifty
    /// grids or five hundred. That is why this is on by default and
    /// <see cref="EntityCostPatches"/>, which runs once per entity, is not.</para>
    ///
    /// <para><b>Patched by hand and wrapped,</b> so a measurement instrument binds or fails on its own
    /// and reports which — the same shape as <see cref="WeaponFireTelemetry.TryPatch"/>.</para>
    /// </summary>
    internal static class PhysicsStepPatches
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Whether the step hook is bound. False means every physics figure this plugin
        /// reports is absent rather than zero, and the reader has to be told which.</summary>
        internal static bool Bound { get; private set; }

        /// <summary>Why it is not bound, for the one log line that says so.</summary>
        internal static string InactiveReason { get; private set; }

        /// <summary>Ticks accumulated since the last <see cref="TakeStepTicks"/>, and the number of
        /// steps they came from. Written only from the game thread inside the postfix.</summary>
        private static long _stepTicks;
        private static long _stepCount;
        private static long _stepMaxTicks;

        /// <summary>The other half of <c>MyPhysics.SimulateInternal</c>, and the half that was
        /// invisible until 2026-09-21: <c>UpdateActiveRigidBodies</c> runs <b>after</b>
        /// <c>StepWorlds</c> returns, so every figure this class reported before covered the Havok
        /// solver and nothing else.</summary>
        private static long _transformTicks;
        private static long _transformCount;
        private static long _transformMaxTicks;

        /// <summary>Whether the transform hook is bound, separately from the step hook — one can
        /// bind while the other does not, and a zero must never be confused with an absence.</summary>
        internal static bool TransformBound { get; private set; }

        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo target = AccessTools.Method(typeof(MyPhysics), "StepWorlds");
                if (target == null)
                {
                    InactiveReason = "MyPhysics.StepWorlds not found";
                    Log.Info("Server perf: " + InactiveReason + "; physics step timing disabled.");
                }
                else
                {
                    harmony.Patch(target,
                        prefix:  new HarmonyMethod(Method("StepPrefix")),
                        postfix: new HarmonyMethod(Method("StepPostfix")));
                    Bound = true;
                }

                // The world-transformation half. MyPhysicsBody.OnMotionDynamic runs from here, and
                // for a MyCubeGrid it takes its expensive branch unconditionally -- the source reads
                // `(moved) || entity is MyCubeGrid`, so a grid pays GetWorldMatrix, SetWorldMatrix
                // and UpdateCluster every frame whether or not it moved. Patched separately from the
                // step so the two are reported apart rather than summed.
                MethodInfo transform = AccessTools.Method(typeof(MyPhysics), "UpdateActiveRigidBodies");
                if (transform == null)
                {
                    Log.Info("Server perf: MyPhysics.UpdateActiveRigidBodies not found;"
                             + " world-transform timing disabled.");
                }
                else
                {
                    harmony.Patch(transform,
                        prefix:  new HarmonyMethod(Method("TransformPrefix")),
                        postfix: new HarmonyMethod(Method("TransformPostfix")));
                    TransformBound = true;
                }
            }
            catch (Exception ex)
            {
                InactiveReason = ex.Message;
                Log.Info("Server perf: could not patch the physics step: " + ex.Message);
            }
        }

        private static MethodInfo Method(string name)
        {
            return typeof(PhysicsStepPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        private static long _havokBeforeStep = -1;

        private static void StepPrefix(out long __state)
        {
            if (DebugTraces.Physics) PhysicsStallTrace.BeforeStep();
            _havokBeforeStep = HavokMemoryTrace.BeforeStep();
            __state = Stopwatch.GetTimestamp();
        }

        private static void StepPostfix(long __state)
        {
            long elapsed = Stopwatch.GetTimestamp() - __state;
            if (elapsed < 0) elapsed = 0;
            _stepTicks += elapsed;
            _stepCount++;
            if (elapsed > _stepMaxTicks) _stepMaxTicks = elapsed;
            if (DebugTraces.Physics || ++_havokTick % 60 == 0) SampleHavokMemory();
            HavokMemoryTrace.AfterStep(_havokBeforeStep);
            if (DebugTraces.Physics)
            {
                PhysicsStallTrace.AfterStep(elapsed * 1000.0 / Stopwatch.Frequency);
                SpeedCapTrace.AfterStep();
            }
        }

        private static void TransformPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void TransformPostfix(long __state)
        {
            long elapsed = Stopwatch.GetTimestamp() - __state;
            if (elapsed < 0) elapsed = 0;
            _transformTicks += elapsed;
            _transformCount++;
            if (elapsed > _transformMaxTicks) _transformMaxTicks = elapsed;
        }

        /// <summary>Havok's own memory in use (<c>HkBaseSystem.GetCurrentMemoryConsumption</c>), read after every
        /// step: the latest and the window's maximum. Five areas of bench phantoms ended in single 19-29 s physics
        /// steps, and stepping clusters one at a time in <c>Havok run out of memory</c> (2026-10-05 14:33:25 and
        /// 14:47:34 box-local); SE initialises Havok with a fixed 16 MB solver budget (<c>MySandboxGame</c>,
        /// <c>HkBaseSystem.Init(16777216, ...)</c>). With <c>factions debug physics</c>, each further 16 MB logs
        /// HAVOK_MEMORY.</summary>
        internal static long HavokMemoryBytes { get; private set; }
        private static long _havokMemoryMax, _havokLogged;
        private static int _havokTick;
        private const long HavokLogStep = 16L << 20;

        private static void SampleHavokMemory()
        {
            try
            {
                long mem = HkBaseSystem.GetCurrentMemoryConsumption();
                HavokMemoryBytes = mem;
                if (mem > _havokMemoryMax) _havokMemoryMax = mem;
                if (DebugTraces.Physics && mem >= _havokLogged + HavokLogStep)
                {
                    _havokLogged = mem - mem % HavokLogStep;
                    Log.Info("HAVOK_MEMORY mb=" + (mem >> 20) + " outOfMemory=" + (HkBaseSystem.IsOutOfMemory ? 1 : 0));
                }
                else if (mem < _havokLogged - 2 * HavokLogStep) _havokLogged = mem - mem % HavokLogStep;
            }
            catch { }
        }

        /// <summary>Reads and clears the window's maximum Havok memory, bytes.</summary>
        internal static long TakeHavokMemoryMax()
        {
            long m = _havokMemoryMax;
            _havokMemoryMax = HavokMemoryBytes;
            return m;
        }

        /// <summary>Reads and clears the world-transform accumulator.</summary>
        internal static void TakeTransformTicks(out long totalTicks, out long calls, out long maxTicks)
        {
            totalTicks = _transformTicks;
            calls = _transformCount;
            maxTicks = _transformMaxTicks;
            _transformTicks = 0;
            _transformCount = 0;
            _transformMaxTicks = 0;
        }

        /// <summary>Reads and clears the accumulator. Called once per telemetry window from the game
        /// thread, which is the only other thread that touches these.</summary>
        internal static void TakeStepTicks(out long totalTicks, out long steps, out long maxTicks)
        {
            totalTicks = _stepTicks;
            steps = _stepCount;
            maxTicks = _stepMaxTicks;
            _stepTicks = 0;
            _stepCount = 0;
            _stepMaxTicks = 0;
        }

        /// <summary>What Havok is actually being asked to solve, read per cluster.
        ///
        /// <para><b>Body count alone does not explain the cost, and measurement said so.</b> Over 252
        /// windows the step ran at roughly the <i>square</i> of the active body count (log-log
        /// exponent 2.1, and 2.2 with the top decile removed), with the marginal cost of one body
        /// rising from 0.9 to 14 microseconds between the sparse and dense ends. A per-body cost does
        /// not do that; a per-<i>interaction</i> cost does. These are the interactions.</para>
        ///
        /// <list type="bullet">
        /// <item><b>Islands.</b> Havok solves each simulation island as a unit, so
        /// <c>activeBodies / islands</c> is the size of the thing actually being solved. Fifty bodies
        /// in fifty islands and fifty bodies in one island cost very differently.</item>
        /// <item><b>Constraints.</b> A subgrid joint and a contact point are both constraints, and
        /// the solver runs over every one of them <c>PhysicsIterations</c> times — 8 on this world,
        /// which is also SE's floor (<c>CreateWorldCInfo</c> clamps anything lower), so it is a
        /// multiplier that cannot be turned down.</item>
        /// </list>
        ///
        /// <para>A sleeping body is in <c>RigidBodies</c> and not in <c>ActiveRigidBodies</c>, and
        /// costs almost nothing — live, only about 13% of bodies are active.</para>
        ///
        /// <para>O(clusters) — a handful — plus a few counter reads each. Once per window on the
        /// game thread.</para></summary>
        /// <summary>What the active bodies actually are.
        ///
        /// <para><b>Why this had to be measured rather than assumed.</b> Through 2026-09-21 the
        /// active count sat around 220 against roughly 60 grids in the world, and every reading this
        /// session was interpreted as though bodies were grids. They are not, and nothing said what
        /// the remainder was. A cost model built on the wrong denominator is worse than none.</para></summary>
        internal struct BodyKinds
        {
            public int Grids;
            public int Characters;
            public int Floating;
            /// <summary>A body whose <c>UserObject</c> resolves to no entity at all — voxel physics
            /// bodies and other engine-internal shapes land here.</summary>
            public int Detached;
            public int Other;
        }

        /// <summary>Active bodies by the runtime type name of the entity behind each, for the
        /// <c>other</c> bucket that the five coarse counters cannot name.
        ///
        /// <para>Measured 2026-09-21: 6 of 220 active bodies were grids, 100 were floating objects,
        /// and <b>114 were none of the named kinds</b>. A cost model cannot be built on a majority
        /// that has no name, and guessing what it is has already cost this session three wrong
        /// hypotheses.</para>
        ///
        /// <para>Bounded by the number of distinct entity types SE gives a rigid body, which is a
        /// handful — so this is safe to emit as a tagged series rather than a field per type.</para></summary>
        private static readonly Dictionary<string, int> TypeCounts = new Dictionary<string, int>(16);

        /// <summary>The per-type tally from the last <see cref="TryReadBodies"/>. Cleared and
        /// refilled each call, so a type that stops appearing drops out rather than going stale.</summary>
        internal static Dictionary<string, int> ActiveBodyTypes { get { return TypeCounts; } }

        /// <summary>Buckets one cluster's active bodies by the entity behind each.
        /// <para>Reached through <c>HkRigidBody.UserObject</c>, which SE sets to the owning
        /// <c>MyPhysicsBody</c> (<c>MyPhysicsBody.RigidBody.UserObject = this</c>). O(active
        /// bodies) once per window; a few hundred type tests.</para></summary>
        private static void Tally(string typeName)
        {
            int n;
            TypeCounts[typeName] = TypeCounts.TryGetValue(typeName, out n) ? n + 1 : 1;
        }

        private static void ClassifyActive(HkWorld world, ref BodyKinds kinds)
        {
            foreach (HkRigidBody body in world.ActiveRigidBodies)
            {
                if (body == null) continue;
                var physics = body.UserObject as MyPhysicsBody;
                IMyEntity entity = physics == null ? null : physics.Entity;
                if (entity == null) { kinds.Detached++; Tally("(no entity)"); continue; }
                if (entity is MyCubeGrid) kinds.Grids++;
                else if (entity is MyCharacter) kinds.Characters++;
                else if (entity is MyFloatingObject) kinds.Floating++;
                else kinds.Other++;
                Tally(entity.GetType().Name);
            }
        }

        /// <summary>Havok actions registered across every world at the last <see cref="TryReadBodies"/>.
        ///
        /// <para><b>Why it is read.</b> <c>ProcessActionsInSingleThread</c> is a world setting that only
        /// matters if there are actions to process, and SE's managed code creates exactly one kind —
        /// an <c>HkEasePenetrationAction</c> on each visual debris piece, lasting three seconds
        /// (<c>MyDebrisBase.Init</c>) — and never calls <c>HkWorld.AddAction</c> itself. Native code
        /// could still register its own, so the count is read rather than inferred.</para></summary>
        internal static int LastActionCount { get; private set; }

        internal static bool TryReadBodies(out int activeBodies, out int totalBodies, out int clusters,
                                           out int islands, out int constraints, out BodyKinds kinds)
        {
            activeBodies = 0;
            totalBodies = 0;
            clusters = 0;
            islands = 0;
            constraints = 0;
            int actions = 0;
            kinds = default(BodyKinds);
            TypeCounts.Clear();
            try
            {
                if (MyPhysics.Clusters == null) return false;
                foreach (object entry in MyPhysics.Clusters.GetList())
                {
                    var world = entry as HkWorld;
                    if (world == null) continue;
                    clusters++;
                    activeBodies += world.ActiveRigidBodies.Count;
                    if (world.RigidBodies != null) totalBodies += world.RigidBodies.Count;
                    islands += world.GetActiveSimulationIslandsCount();
                    constraints += world.GetConstraintCount();
                    actions += world.GetActionCount();
                    ClassifyActive(world, ref kinds);
                }
                LastActionCount = actions;
                return true;
            }
            catch (Exception ex)
            {
                // A cluster list mutating under the walk is the expected failure, and a missed
                // sample is not worth a log line every window.
                InactiveReason = ex.Message;
                return false;
            }
        }
    }
}
