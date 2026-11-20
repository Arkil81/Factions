using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using Factions.Core;
using HarmonyLib;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Torch.API.Managers;
using Torch.Managers.ChatManager;
using VRage.Game.Components;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Why a grid under dampeners does not hold still in gravity (bridge and chat <c>factions dampprobe</c>).
    ///
    /// <para>SE's dampener asks for <c>m_d (g - 2v)</c> of lift, with <c>m_d</c> from the thrust component's
    /// <c>CalculateMass</c>: the body's mass plus every unanchored grid in its <c>PhysicalDynamic</c> group that
    /// has no dampened thrust of its own. So a steady creep is one of two things, and they read apart:
    /// <c>m_d</c> above the body's mass with lift equal to weight (a mass the dampener holds up that the body
    /// does not have), or <c>m_d</c> equal to the body's mass with lift short of weight (an upward push, or
    /// gravity the body did not get). Live 2026-10-12: a ~1,000 t large grid, no subgrids, empty cargo, climbs
    /// ~1 m/s piloted and unpiloted.</para>
    ///
    /// <para>Lift and climb rate are averaged over <see cref="SampleFrames"/> game frames, because the thrust
    /// a single frame reads can sit either side of the hold. Game thread only, except the force hook.</para>
    ///
    /// <para><b>The force hook.</b> The same grid read <c>lift/weight=0.5000</c> at a steady 2.45 m/s climb
    /// (= g/4) and fell at 9.80 m/s² with its thrust off, so the thrust reaching the body is twice what SE
    /// computes. While a sample runs, a prefix on <c>MyPhysicsBody.AddForce</c> counts the forces queued for the
    /// probed body (SE's thrust path queues one per frame and applies it before the step) and those applied at
    /// once, and keeps the call stack of each distinct caller: two queued per frame, with the second stack, names
    /// what runs the thrust twice. Idle cost is one reference comparison per force.</para>
    /// </summary>
    internal static partial class DampenerProbe
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        internal const int SampleFrames = 180;
        private const int MaxGroupListed = 6;

        private static readonly MethodInfo CalculateMassM = typeof(MyEntityThrustComponent).GetMethod(
            "CalculateMass", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        private sealed class Sampler
        {
            public long Eid;
            public ulong ReplyTo;
            public int Frames;
            public int Moved;
            public double LiftSum;
            public double ClimbSum;
            public double GravitySum;
            public double ClimbFirst;
            public double ClimbLast;
            public Vector3D LastCom;
            public bool HasLast;
            public DateTime StartUtc;
        }

        private static readonly List<Sampler> Active = new List<Sampler>();

        private const int MaxCallers = 10;
        private const int StackDepth = 16;
        private static volatile MyPhysicsBody _watched;
        private static int _queued;
        private static int _immediate;
        private static readonly object CallersLock = new object();
        private static readonly Dictionary<string, int> Callers = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int _callersDropped;

        /// <summary>Whether the force hook bound; without it the force counts read <c>n/a</c>.</summary>
        internal static bool ForceHookBound { get; private set; }

        /// <summary>Binds the force hook and the trace's velocity-write hooks. Each is wrapped, so a miss costs
        /// its own count only.</summary>
        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) return;
            TryPatchVelocityWrites(harmony);
            try
            {
                MethodInfo target = AccessTools.Method(typeof(MyPhysicsBody), "AddForce");
                if (target == null)
                {
                    Log.Info("dampprobe: MyPhysicsBody.AddForce not found; force counts disabled.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(DampenerProbe)
                    .GetMethod("AddForcePrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                ForceHookBound = true;
            }
            catch (Exception ex)
            {
                Log.Info("dampprobe: could not patch MyPhysicsBody.AddForce: " + ex.Message);
            }
        }

        private static void AddForcePrefix(MyPhysicsBody __instance, MyPhysicsForceType type, Vector3? force, bool applyImmediately)
        {
            MyPhysicsBody watched = _watched;
            if (watched == null || !ReferenceEquals(__instance, watched)) return;
            TraceForce(__instance, type, force, applyImmediately);
            if (applyImmediately) Interlocked.Increment(ref _immediate);
            else Interlocked.Increment(ref _queued);
            string key = (applyImmediately ? "immediate " : "queued ") + type + " " + Stack();
            lock (CallersLock)
            {
                int n;
                if (Callers.TryGetValue(key, out n)) Callers[key] = n + 1;
                else if (Callers.Count < MaxCallers) Callers[key] = 1;
                else _callersDropped++;
            }
        }

        private static string Stack()
        {
            var trace = new System.Diagnostics.StackTrace(2, false);
            var sb = new StringBuilder();
            int frames = Math.Min(trace.FrameCount, StackDepth);
            for (int i = 0; i < frames; i++)
            {
                MethodBase m = trace.GetFrame(i).GetMethod();
                if (m == null) continue;
                if (sb.Length > 0) sb.Append(" < ");
                sb.Append(m.DeclaringType != null ? m.DeclaringType.Name : "?").Append('.').Append(m.Name);
            }
            return sb.ToString();
        }
        private static readonly List<MyCubeGrid> GroupScratch = new List<MyCubeGrid>();

        /// <summary>Starts a sample of <paramref name="grid"/> and returns the static half at once. The averaged
        /// line follows as <c>DAMP_PROBE</c> in the Torch log, and to <paramref name="replyTo"/> in chat when
        /// that is a player.</summary>
        internal static string Start(MyCubeGrid grid, ulong replyTo)
        {
            if (grid == null || grid.Closed || grid.Physics == null) return "dampprobe: grid has no physics.";
            if (_trace != null) return "dampprobe: a trace of " + _trace.Eid + " is running; try again when it ends.";
            Active.Add(new Sampler { Eid = grid.EntityId, ReplyTo = replyTo, StartUtc = DateTime.UtcNow });
            if (_watched == null)
            {
                lock (CallersLock) { Callers.Clear(); _callersDropped = 0; }
                Interlocked.Exchange(ref _queued, 0);
                Interlocked.Exchange(ref _immediate, 0);
                _watched = grid.Physics;
            }
            return "dampprobe " + grid.EntityId + " \"" + grid.DisplayName + "\": " + Describe(grid)
                   + "; sampling " + SampleFrames + " frames, DAMP_PROBE line follows.";
        }

        internal static void Tick()
        {
            TraceTick();
            if (Active.Count == 0) return;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Sampler s = Active[i];
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById(s.Eid, out grid, false) || grid == null || grid.Closed || grid.Physics == null)
                {
                    Active.RemoveAt(i);
                    _watched = null;
                    Finish(s, "DAMP_PROBE eid=" + s.Eid + " grid gone after " + s.Frames + " frames");
                    continue;
                }
                try
                {
                    Sample(s, grid);
                }
                catch (Exception ex)
                {
                    Active.RemoveAt(i);
                    Finish(s, "DAMP_PROBE eid=" + s.Eid + " sample failed: " + ex.Message);
                    continue;
                }
                if (s.Frames < SampleFrames) continue;
                Active.RemoveAt(i);
                Finish(s, Report(s, grid));
            }
        }

        private static void Sample(Sampler s, MyCubeGrid grid)
        {
            var thrust = grid.Components.Get<MyEntityThrustComponent>();
            Vector3 g = grid.Physics.Gravity;
            float gLen = g.Length();
            Vector3 up = gLen > 0.001f ? -g / gLen : Vector3.Zero;
            double lift = 0;
            if (thrust != null)
            {
                Vector3D finalWorld = Vector3D.TransformNormal((Vector3D)thrust.FinalThrust, grid.WorldMatrix);
                lift = Vector3D.Dot(finalWorld, (Vector3D)up);
            }
            double climb = Vector3.Dot(grid.Physics.LinearVelocity, up);
            Vector3D com = grid.Physics.CenterOfMassWorld;
            if (s.HasLast && Vector3D.DistanceSquared(com, s.LastCom) > 1e-10) s.Moved++;
            s.LastCom = com;
            s.HasLast = true;
            if (s.Frames == 0) s.ClimbFirst = climb;
            s.ClimbLast = climb;
            s.LiftSum += lift;
            s.ClimbSum += climb;
            s.GravitySum += gLen;
            s.Frames++;
        }

        private static string Report(Sampler s, MyCubeGrid grid)
        {
            int n = Math.Max(1, s.Frames);
            double g = s.GravitySum / n;
            double lift = s.LiftSum / n;
            double climb = s.ClimbSum / n;
            double seconds = Math.Max(1e-3, (DateTime.UtcNow - s.StartUtc).TotalSeconds);
            double body = grid.Physics.Mass;
            double damp = DampenerMass(grid);
            double weight = g * body;

            var sb = new StringBuilder("DAMP_PROBE eid=").Append(grid.EntityId)
                .Append(" name=\"").Append(grid.DisplayName).Append('"')
                .Append(" frames=").Append(s.Frames).Append(" s=").Append(F(seconds, 1))
                .Append(" g=").Append(F(g, 3))
                .Append(" weightN=").Append(F(weight, 0))
                .Append(" liftN=").Append(F(lift, 0))
                .Append(" lift/weight=").Append(weight > 0 ? F(lift / weight, 4) : "n/a")
                .Append(" unaccountedN=").Append(F(DampenerBalance.UnaccountedLiftN(g, body, lift), 0))
                .Append(" climbMs=").Append(F(climb, 3))
                .Append(" climbAccelMs2=").Append(F((s.ClimbLast - s.ClimbFirst) / seconds, 4))
                .Append(" moved=").Append(s.Moved).Append('/').Append(Math.Max(0, s.Frames - 1))
                .Append(" massClimbMs=").Append(damp > 0 ? F(DampenerBalance.EquilibriumClimbMs(g, body, damp), 3) : "n/a");
            if (ForceHookBound && ReferenceEquals(_watched, grid.Physics))
            {
                sb.Append(" queuedForces=").Append(Volatile.Read(ref _queued))
                  .Append(" immediateForces=").Append(Volatile.Read(ref _immediate))
                  .Append(" perFrame=").Append(F(Volatile.Read(ref _queued) / (double)n, 2));
            }
            else sb.Append(" queuedForces=n/a");
            sb.Append(" | ").Append(Describe(grid));
            return sb.ToString();
        }

        /// <summary>The static half: the two masses, the group behind the difference, the gravity sources and
        /// what else is commanding the thrust.</summary>
        private static string Describe(MyCubeGrid grid)
        {
            MyGridPhysics physics = grid.Physics;
            var thrust = grid.Components.Get<MyEntityThrustComponent>();
            double body = physics.Mass;
            double damp = DampenerMass(grid);
            double shape = -1;
            try
            {
                if (physics.Shape != null && physics.Shape.MassProperties.HasValue)
                    shape = physics.Shape.MassProperties.Value.Mass;
            }
            catch { }

            var sb = new StringBuilder();
            sb.Append("bodyKg=").Append(F(body, 0))
              .Append(" dampKg=").Append(damp >= 0 ? F(damp, 0) : "n/a")
              .Append(" damp/body=").Append(damp > 0 && body > 0 ? F(damp / body, 4) : "n/a")
              .Append(" shapeKg=").Append(shape >= 0 ? F(shape, 0) : "n/a");

            Vector3 cached = physics.Gravity;
            Vector3 body3 = physics.RigidBody != null ? physics.RigidBody.Gravity : Vector3.Zero;
            Vector3 natural = MyGravityProviderSystem.CalculateNaturalGravityInPoint(physics.CenterOfMassWorld);
            Vector3 total = MyGravityProviderSystem.CalculateTotalGravityInPoint(physics.CenterOfMassWorld);
            sb.Append(" gCached=").Append(F(cached.Length(), 3))
              .Append(" gBodyDelta=").Append(F((body3 - cached).Length(), 4))
              .Append(" gNaturalDelta=").Append(F((natural - cached).Length(), 4))
              .Append(" gArtificial=").Append(F((total - natural).Length(), 3));

            if (thrust == null) sb.Append(" thrust=none");
            else
            {
                int overridden = 0;
                foreach (MyCubeBlock b in grid.GetFatBlocks())
                {
                    var t = b as MyThrust;
                    if (t != null && ((Sandbox.ModAPI.Ingame.IMyThrust)t).ThrustOverride > 0) overridden++;
                }
                sb.Append(" dampeners=").Append(thrust.DampenersEnabled)
                  .Append(" ctl=").Append(F(thrust.ControlThrust.Length(), 3))
                  .Append(" autopilot=").Append(F(thrust.AutoPilotControlThrust.Length(), 3))
                  .Append(" slowdown=").Append(F(thrust.SlowdownFactor, 3))
                  .Append(" overridden=").Append(overridden);
            }
            sb.Append(" predicted=").Append(grid.IsClientPredicted);

            MyCubeGridGroups groups = MyCubeGridGroups.Static;
            sb.Append(" dynamicGroup=");
            if (groups == null) sb.Append("n/a");
            else
            {
                GroupScratch.Clear();
                try { groups.PhysicalDynamic.GetGroupNodes(grid, GroupScratch); AppendNodes(sb, grid); }
                catch (Exception ex) { sb.Append("error:").Append(ex.GetType().Name); }
                sb.Append(" physicalGroup=");
                GroupScratch.Clear();
                try { groups.Physical.GetGroupNodes(grid, GroupScratch); sb.Append(GroupScratch.Count); }
                catch (Exception ex) { sb.Append("error:").Append(ex.GetType().Name); }
                sb.Append(" mechanicalGroup=");
                GroupScratch.Clear();
                try { groups.Mechanical.GetGroupNodes(grid, GroupScratch); sb.Append(GroupScratch.Count); }
                catch (Exception ex) { sb.Append("error:").Append(ex.GetType().Name); }
            }
            return sb.ToString();
        }

        /// <summary>The group's size, then every other grid in it: what <c>CalculateMass</c> may add.</summary>
        private static void AppendNodes(StringBuilder sb, MyCubeGrid grid)
        {
            sb.Append(GroupScratch.Count);
            int listed = 0;
            foreach (MyCubeGrid other in GroupScratch)
            {
                if (other == grid) continue;
                if (listed++ >= MaxGroupListed) { sb.Append(" [...]"); break; }
                var thrust = other.Components.Get<MyEntityThrustComponent>();
                bool dampened = thrust != null && thrust.Enabled && thrust.HasThrust && thrust.DampenersEnabled;
                Vector3D d = other.PositionComp.WorldAABB.Center - grid.PositionComp.WorldAABB.Center;
                sb.Append(" [").Append(other.EntityId).Append(" \"").Append(other.DisplayName).Append("\" ")
                  .Append(other.Physics != null ? F(other.Physics.Mass, 0) : "?").Append("kg ")
                  .Append(F(d.Length(), 0)).Append("m")
                  .Append(other.IsStatic ? " static" : "")
                  .Append(MyFixedGrids.IsRooted(other) ? " rooted" : "")
                  .Append(dampened ? " dampened" : "")
                  .Append(']');
            }
        }

        /// <summary>The mass SE's dampener holds up, from the thrust component's own <c>CalculateMass</c>;
        /// -1 when it cannot be read.</summary>
        private static double DampenerMass(MyCubeGrid grid)
        {
            var thrust = grid.Components.Get<MyEntityThrustComponent>();
            if (thrust == null || CalculateMassM == null) return -1;
            try { return Convert.ToDouble(CalculateMassM.Invoke(thrust, null), CultureInfo.InvariantCulture); }
            catch { return -1; }
        }

        private static void Finish(Sampler s, string line)
        {
            Log.Info(line);
            MyPhysicsBody watched = _watched;
            if (watched != null && watched.Entity != null && watched.Entity.EntityId == s.Eid)
            {
                _watched = null;
                lock (CallersLock)
                {
                    foreach (KeyValuePair<string, int> kv in Callers)
                        Log.Info("DAMP_PROBE_CALLER eid=" + s.Eid + " n=" + kv.Value + " " + kv.Key);
                    if (_callersDropped > 0) Log.Info("DAMP_PROBE_CALLER eid=" + s.Eid + " dropped=" + _callersDropped);
                    Callers.Clear();
                }
            }
            Reply(s.ReplyTo, line);
        }

        private static void Reply(ulong replyTo, string line)
        {
            if (replyTo == 0) return;
            try
            {
                FactionsPlugin plugin = FactionsPlugin.Instance;
                ChatManagerServer chat = plugin != null && plugin.Torch != null && plugin.Torch.CurrentSession != null
                    ? plugin.Torch.CurrentSession.Managers.GetManager<ChatManagerServer>()
                    : null;
                if (chat != null) chat.SendMessageAsOther("Factions", line, Color.LightSkyBlue, replyTo);
            }
            catch (Exception ex)
            {
                Log.Warn("dampprobe reply to " + replyTo + " failed: " + ex.Message);
            }
        }

        private static string F(double v, int places)
        {
            return v.ToString("F" + places, CultureInfo.InvariantCulture);
        }
    }
}
