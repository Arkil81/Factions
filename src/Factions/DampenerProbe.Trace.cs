using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Components;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// <c>factions dampprobe [grid] trace [seconds]</c>: the server's frame-by-frame account of one grid, for a
    /// grid whose velocity the pilot sees jerk. Every frame it records the body's velocity, spin, attitude and
    /// gravity, the thrust SE computed, every force queued for the body and every direct write to its velocity
    /// (with the caller), and writes them to <c>logs/damptrace-&lt;eid&gt;-&lt;stamp&gt;.csv</c> beside a
    /// <c>DAMP_TRACE</c> summary.
    ///
    /// <para><b>What it separates.</b> A piloted grid is predicted on its pilot's client and corrected toward the
    /// server's snapshots: the client compares each snapshot against its own history at the same timestamp and,
    /// for a velocity error above 0.01 m/s, eases 80% of it in over 30 frames (<c>MyPredictedSnapshotSync</c>).
    /// A pilot who sees the velocity jerk every half second is therefore watching either the server's own body
    /// being kicked, or the two ends disagreeing about an undisturbed one. The residual this trace reports, the
    /// change in velocity a frame that gravity and the queued forces do not explain, answers the first; the
    /// client's own trace, started by the same command when the caller is linked, answers the second.</para>
    ///
    /// <para>Forces and writes reach the hooks from the parallel update threads, so they are summed under a lock
    /// and taken once a frame. Idle cost is the reference comparison the force hook already pays, plus one per
    /// velocity write.</para>
    /// </summary>
    internal static partial class DampenerProbe
    {
        internal const int TraceDefaultSeconds = 10;
        internal const int TraceMaxSeconds = 60;

        /// <summary>Unexplained acceleration, m/s², above which a frame counts as a kick.</summary>
        private const double TraceSpikeMs2 = 0.5;
        private const int TraceSpikesListed = 16;

        private struct TraceRow
        {
            public long Frame;
            public double Ms;
            public float Sim;
            public Vector3D Com;
            public Vector3 V, W, G;
            public Vector3 Fwd, Up, Right;
            public Vector3 ThrustLocal;
            public Vector3D Force;
            public int Forces;
            public Vector3D Impulse;
            public int Impulses;
            public int VelocitySets;
            public Vector3D VelocitySetDelta;
        }

        private sealed class TraceState
        {
            public long Eid;
            public string Name;
            public ulong ReplyTo;
            public int Frames;
            public string Stamp;
            public string ClientNote;
            public long StartTicks;
            public readonly List<TraceRow> Rows = new List<TraceRow>();
        }

        private static TraceState _trace;

        private static readonly object TraceLock = new object();
        private static Vector3D _traceForce, _traceImpulse, _traceSetDelta;
        private static int _traceForces, _traceImpulses, _traceSets;

        [ThreadStatic] private static bool _inBodySet;

        /// <summary>Whether the velocity-write hooks bound; without them the trace's <c>velsets</c> reads n/a.</summary>
        internal static bool VelocityHookBound { get; private set; }

        private static void TryPatchVelocityWrites(Harmony harmony)
        {
            try
            {
                MethodInfo body = AccessTools.PropertySetter(typeof(MyPhysicsBody), "LinearVelocity");
                // Declared on HkEntity; HkRigidBody inherits it, and Harmony patches only the declaring method.
                MethodInfo rigid = AccessTools.PropertySetter(typeof(HkEntity), "LinearVelocity");
                if (body == null || rigid == null)
                {
                    Log.Info("dampprobe: a LinearVelocity setter did not resolve; trace velocity writes disabled.");
                    return;
                }
                harmony.Patch(body,
                    prefix: new HarmonyMethod(typeof(DampenerProbe).GetMethod("BodyVelocitySetPrefix", BindingFlags.NonPublic | BindingFlags.Static)),
                    postfix: new HarmonyMethod(typeof(DampenerProbe).GetMethod("BodyVelocitySetPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
                harmony.Patch(rigid,
                    prefix: new HarmonyMethod(typeof(DampenerProbe).GetMethod("RigidVelocitySetPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                VelocityHookBound = true;
            }
            catch (Exception ex)
            {
                Log.Info("dampprobe: could not patch the LinearVelocity setters: " + ex.Message);
            }
        }

        private static void BodyVelocitySetPrefix(MyPhysicsBody __instance, Vector3 value)
        {
            MyPhysicsBody watched = _watched;
            if (watched == null || _trace == null || !ReferenceEquals(__instance, watched)) return;
            _inBodySet = true;
            HkRigidBody rb = __instance.RigidBody;
            RecordVelocitySet(rb != null ? rb.LinearVelocity : value, value, "body");
        }

        private static void BodyVelocitySetPostfix()
        {
            _inBodySet = false;
        }

        /// <summary>A write straight to the Havok body (the setter is <c>HkEntity</c>'s), which <c>MyPhysicsBody</c>'s own setter also ends in; that
        /// one is already counted, so the flag skips it.</summary>
        private static void RigidVelocitySetPrefix(HkEntity __instance, Vector3 value)
        {
            if (_inBodySet) return;
            MyPhysicsBody watched = _watched;
            if (watched == null || _trace == null || !ReferenceEquals(__instance, watched.RigidBody)) return;
            RecordVelocitySet(__instance.LinearVelocity, value, "havok");
        }

        private static void RecordVelocitySet(Vector3 was, Vector3 value, string path)
        {
            string key = "velocity-set " + path + " " + Stack();
            lock (TraceLock)
            {
                _traceSets++;
                _traceSetDelta += (Vector3D)(value - was);
            }
            lock (CallersLock)
            {
                int n;
                if (Callers.TryGetValue(key, out n)) Callers[key] = n + 1;
                else if (Callers.Count < MaxCallers) Callers[key] = 1;
                else _callersDropped++;
            }
        }

        /// <summary>A force or impulse for the watched body, in world terms. A body force is given in the grid's
        /// frame and applied at the centre of mass (<c>MyThrusterBlockThrustComponent.UpdateThrusts</c>). Only a queued
        /// force is summed: <c>MyPhysics.Simulate</c> applies each queued force through <c>AddForce</c> again with
        /// <c>applyImmediately</c>, so counting both counts thrust twice. An immediate force from anywhere else is
        /// left out of the expected acceleration and so shows up in the residual.</summary>
        private static void TraceForce(MyPhysicsBody body, MyPhysicsForceType type, Vector3? force, bool applyImmediately)
        {
            if (_trace == null || !force.HasValue || applyImmediately) return;
            Vector3D f = force.Value;
            bool impulse = false;
            switch (type)
            {
                case MyPhysicsForceType.ADD_BODY_FORCE_AND_BODY_TORQUE:
                    if (body.Entity != null) f = Vector3D.TransformNormal(f, body.Entity.WorldMatrix);
                    break;
                case MyPhysicsForceType.APPLY_WORLD_IMPULSE_AND_WORLD_ANGULAR_IMPULSE:
                    impulse = true;
                    break;
            }
            lock (TraceLock)
            {
                if (impulse) { _traceImpulse += f; _traceImpulses++; }
                else { _traceForce += f; _traceForces++; }
            }
        }

        /// <summary>Starts a trace of <paramref name="grid"/> for <paramref name="seconds"/>.
        /// <paramref name="clientNote"/> says whether the caller's client was asked to trace alongside.</summary>
        internal static string StartTrace(MyCubeGrid grid, ulong replyTo, int seconds, string stamp, string clientNote)
        {
            if (grid == null || grid.Closed || grid.Physics == null) return "dampprobe: grid has no physics.";
            if (_trace != null) return "dampprobe: a trace of " + _trace.Eid + " is already running.";
            if (Active.Count > 0 || _watched != null) return "dampprobe: a sample is running; try again when it ends.";
            if (!ForceHookBound) return "dampprobe: the force hook is not bound, so a trace cannot see the forces.";
            seconds = Math.Max(1, Math.Min(TraceMaxSeconds, seconds));
            lock (CallersLock) { Callers.Clear(); _callersDropped = 0; }
            TakeTraceSums();
            _trace = new TraceState
            {
                Eid = grid.EntityId,
                Name = grid.DisplayName,
                ReplyTo = replyTo,
                Frames = seconds * 60,
                Stamp = stamp,
                ClientNote = clientNote,
                StartTicks = Stopwatch.GetTimestamp(),
            };
            _watched = grid.Physics;
            return "dampprobe trace " + grid.EntityId + " \"" + grid.DisplayName + "\": " + seconds + " s ("
                   + _trace.Frames + " frames), stamp " + stamp + "; " + clientNote + "; DAMP_TRACE line follows.";
        }

        private static void TakeTraceSums(out Vector3D force, out int forces, out Vector3D impulse, out int impulses,
                                          out int sets, out Vector3D setDelta)
        {
            lock (TraceLock)
            {
                force = _traceForce; forces = _traceForces;
                impulse = _traceImpulse; impulses = _traceImpulses;
                sets = _traceSets; setDelta = _traceSetDelta;
                _traceForce = _traceImpulse = _traceSetDelta = Vector3D.Zero;
                _traceForces = _traceImpulses = _traceSets = 0;
            }
        }

        private static void TakeTraceSums()
        {
            Vector3D a, b, c;
            int x, y, z;
            TakeTraceSums(out a, out x, out b, out y, out z, out c);
        }

        private static void TraceTick()
        {
            TraceState t = _trace;
            if (t == null) return;
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(t.Eid, out grid, false) || grid == null || grid.Closed || grid.Physics == null)
            {
                EndTrace(t, null);
                return;
            }
            if (!ReferenceEquals(_watched, grid.Physics)) _watched = grid.Physics;
            try
            {
                MyGridPhysics physics = grid.Physics;
                var thrust = grid.Components.Get<MyEntityThrustComponent>();
                MatrixD wm = grid.WorldMatrix;
                var row = new TraceRow
                {
                    Frame = MySession.Static != null ? MySession.Static.GameplayFrameCounter : 0,
                    Ms = (Stopwatch.GetTimestamp() - t.StartTicks) * 1000.0 / Stopwatch.Frequency,
                    Sim = Sync.ServerSimulationRatio,
                    Com = physics.CenterOfMassWorld,
                    V = physics.LinearVelocity,
                    W = physics.AngularVelocity,
                    G = physics.RigidBody != null ? physics.RigidBody.Gravity : physics.Gravity,
                    Fwd = (Vector3)wm.Forward,
                    Up = (Vector3)wm.Up,
                    Right = (Vector3)wm.Right,
                    ThrustLocal = thrust != null ? thrust.FinalThrust : Vector3.Zero,
                };
                TakeTraceSums(out row.Force, out row.Forces, out row.Impulse, out row.Impulses,
                              out row.VelocitySets, out row.VelocitySetDelta);
                t.Rows.Add(row);
            }
            catch (Exception ex)
            {
                EndTrace(t, "sample failed: " + ex.Message);
                return;
            }
            if (t.Rows.Count >= t.Frames) EndTrace(t, null);
        }

        private static void EndTrace(TraceState t, string failure)
        {
            _trace = null;
            _watched = null;
            MyCubeGrid grid;
            double mass = MyEntities.TryGetEntityById(t.Eid, out grid, false) && grid != null && grid.Physics != null
                ? grid.Physics.Mass : 0;
            string file = null;
            try { file = WriteTraceCsv(t, mass); }
            catch (Exception ex) { failure = (failure != null ? failure + "; " : "") + "csv failed: " + ex.Message; }
            string line = TraceSummary(t, mass, file, failure);
            Log.Info(line);
            lock (CallersLock)
            {
                foreach (KeyValuePair<string, int> kv in Callers)
                    Log.Info("DAMP_PROBE_CALLER eid=" + t.Eid + " n=" + kv.Value + " " + kv.Key);
                if (_callersDropped > 0) Log.Info("DAMP_PROBE_CALLER eid=" + t.Eid + " dropped=" + _callersDropped);
                Callers.Clear();
            }
            Reply(t.ReplyTo, line);
        }

        /// <summary>Per-frame residual: the velocity change gravity and the forces queued for that step do not
        /// explain, m/s². <paramref name="lag"/> 0 pairs a step with the forces taken in the same frame, 1 with
        /// those of the frame before -- which one is right depends on where the plugin update sits relative to
        /// the thrust update, so the summary takes whichever explains the trace better.</summary>
        private static Vector3D Residual(List<TraceRow> rows, int i, double mass, int lag)
        {
            TraceRow a = rows[i - 1], b = rows[i];
            long steps = Math.Max(1, b.Frame - a.Frame);
            Vector3D dv = (Vector3D)(b.V - a.V) * (60.0 / steps);
            Vector3D force = rows[Math.Max(0, i - lag)].Force;
            Vector3D impulse = rows[Math.Max(0, i - lag)].Impulse * 60.0;
            Vector3D expected = (Vector3D)a.G + (mass > 0 ? (force + impulse) / mass : Vector3D.Zero);
            return dv - expected - b.VelocitySetDelta * (60.0 / steps);
        }

        private static string TraceSummary(TraceState t, double mass, string file, string failure)
        {
            List<TraceRow> rows = t.Rows;
            var sb = new StringBuilder("DAMP_TRACE eid=").Append(t.Eid).Append(" name=\"").Append(t.Name).Append('"')
                .Append(" stamp=").Append(t.Stamp).Append(" frames=").Append(rows.Count);
            if (failure != null) sb.Append(" failure=\"").Append(failure).Append('"');
            if (rows.Count < 3)
                return sb.Append(" too-few-frames").ToString();

            int gaps = 0, doubles = 0, zeroes = 0, impulses = 0, sets = 0;
            double simMin = double.MaxValue, speed = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0 && rows[i].Frame - rows[i - 1].Frame != 1) gaps++;
                if (rows[i].Forces == 0) zeroes++;
                if (rows[i].Forces > 1) doubles++;
                impulses += rows[i].Impulses;
                sets += rows[i].VelocitySets;
                simMin = Math.Min(simMin, rows[i].Sim);
                speed += rows[i].V.Length();
            }

            double ss0 = 0, ss1 = 0;
            for (int i = 2; i < rows.Count; i++)
            {
                ss0 += Residual(rows, i, mass, 0).LengthSquared();
                ss1 += Residual(rows, i, mass, 1).LengthSquared();
            }
            int lag = ss1 < ss0 ? 1 : 0;
            int n = rows.Count - 2;
            Vector3D meanLocal = Vector3D.Zero;
            double max = 0;
            int maxAt = 0;
            var spikes = new List<int>();
            for (int i = 2; i < rows.Count; i++)
            {
                Vector3D r = Residual(rows, i, mass, lag);
                TraceRow b = rows[i];
                meanLocal += new Vector3D(Vector3D.Dot(r, b.Fwd), Vector3D.Dot(r, b.Up), Vector3D.Dot(r, b.Right));
                double len = r.Length();
                if (len > max) { max = len; maxAt = i; }
                if (len > TraceSpikeMs2) spikes.Add(i);
            }
            meanLocal /= n;

            double spinMax = 0;
            for (int i = 1; i < rows.Count; i++)
                spinMax = Math.Max(spinMax, (rows[i].W - rows[i - 1].W).Length() * 60.0);

            sb.Append(" s=").Append(F(rows[rows.Count - 1].Ms / 1000.0, 1))
              .Append(" massKg=").Append(F(mass, 0))
              .Append(" speedMs=").Append(F(speed / rows.Count, 2))
              .Append(" simMin=").Append(F(simMin, 2))
              .Append(" frameGaps=").Append(gaps)
              .Append(" framesNoForce=").Append(zeroes)
              .Append(" framesMultiForce=").Append(doubles)
              .Append(" impulses=").Append(impulses)
              .Append(" velocitySets=").Append(VelocityHookBound ? sets.ToString(CultureInfo.InvariantCulture) : "n/a")
              .Append(" lag=").Append(lag)
              .Append(" residualRmsMs2=").Append(F(Math.Sqrt((lag == 1 ? ss1 : ss0) / n), 4))
              .Append(" residualMeanFwdUpRight=(").Append(F(meanLocal.X, 4)).Append(',').Append(F(meanLocal.Y, 4))
              .Append(',').Append(F(meanLocal.Z, 4)).Append(')')
              .Append(" residualMaxMs2=").Append(F(max, 3)).Append("@").Append(maxAt)
              .Append(" spinAccelMaxRadS2=").Append(F(spinMax, 3))
              .Append(" kicks>").Append(F(TraceSpikeMs2, 1)).Append('=').Append(spikes.Count);
            if (spikes.Count > 0)
            {
                sb.Append(" kickFrames=");
                for (int i = 0; i < spikes.Count && i < TraceSpikesListed; i++)
                    sb.Append(i > 0 ? "," : "").Append(spikes[i]);
                if (spikes.Count > 1)
                {
                    var gapsBetween = new List<int>();
                    for (int i = 1; i < spikes.Count; i++) gapsBetween.Add(spikes[i] - spikes[i - 1]);
                    gapsBetween.Sort();
                    sb.Append(" kickSpacingMedianFrames=").Append(gapsBetween[gapsBetween.Count / 2]);
                }
            }
            if (file != null) sb.Append(" csv=").Append(file);
            return sb.ToString();
        }

        private static string WriteTraceCsv(TraceState t, double mass)
        {
            FactionsPlugin plugin = FactionsPlugin.Instance;
            if (plugin == null || string.IsNullOrEmpty(plugin.StorageDir) || t.Rows.Count == 0) return null;
            string dir = Path.Combine(plugin.StorageDir, "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "damptrace-" + t.Eid + "-" + t.Stamp + ".csv");
            var sb = new StringBuilder();
            sb.Append("i,frame,ms,sim,vFwd,vUp,vRight,speed,wFwd,wUp,wRight,gFwd,gUp,gRight,")
              .Append("thrustFwd,thrustUp,thrustRight,forceFwd,forceUp,forceRight,forces,impulses,velocitySets,")
              .Append("setFwd,setUp,setRight,resFwd,resUp,resRight,comX,comY,comZ\n");
            List<TraceRow> rows = t.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                TraceRow r = rows[i];
                Vector3D res = i >= 2 ? Residual(rows, i, mass, 0) : Vector3D.Zero;
                sb.Append(i).Append(',').Append(r.Frame).Append(',').Append(F(r.Ms, 2)).Append(',').Append(F(r.Sim, 3));
                Local(sb, r.V, r); sb.Append(',').Append(F(r.V.Length(), 4));
                Local(sb, r.W, r);
                Local(sb, r.G, r);
                // FinalThrust is grid-local: forward is -Z, up +Y, right +X.
                sb.Append(',').Append(F(-r.ThrustLocal.Z, 0)).Append(',').Append(F(r.ThrustLocal.Y, 0)).Append(',').Append(F(r.ThrustLocal.X, 0));
                Local(sb, r.Force, r, 0);
                sb.Append(',').Append(r.Forces).Append(',').Append(r.Impulses).Append(',').Append(r.VelocitySets);
                Local(sb, r.VelocitySetDelta, r);
                Local(sb, res, r);
                sb.Append(',').Append(F(r.Com.X, 3)).Append(',').Append(F(r.Com.Y, 3)).Append(',').Append(F(r.Com.Z, 3)).Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        private static void Local(StringBuilder sb, Vector3D v, TraceRow r, int places = 4)
        {
            sb.Append(',').Append(F(Vector3D.Dot(v, r.Fwd), places))
              .Append(',').Append(F(Vector3D.Dot(v, r.Up), places))
              .Append(',').Append(F(Vector3D.Dot(v, r.Right), places));
        }
    }
}
