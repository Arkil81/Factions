using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Factions.Wire;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.World;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Replication.History;
using VRage.FileSystem;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Library.Utils;
using VRage.ModAPI;
using VRageMath;

namespace FactionsUI;

/// <summary>
/// The client half of <c>!factions dampprobe trace</c>: what this client's prediction of one grid disagreed with
/// the server about, frame by frame, over the seconds the server traces the same grid.
///
/// <para><b>Why on the client.</b> SE's server never measures a correction. It sends the piloted grid's snapshot
/// every sync frame, and <c>MyPredictedSnapshotSync.UpdateFromServerQueue</c> on the client diffs it against the
/// client's own history at the same timestamp (<c>m_debugLastDelta</c>, server minus client). A velocity error
/// above 0.01 m/s is eased in, 80% of it over 30 frames; a position error above 5 mm likewise. So a velocity the
/// pilot sees jerk every half second is this window restarting, and only this side can say on which axis, by how
/// much and how often.</para>
///
/// <para>Started by the server's <see cref="WireMessageKind.NetTraceStart"/>, which names the grid, the frame count
/// and the stamp the server's own <c>damptrace</c> file carries. Writes
/// <c>Storage/Factions/nettrace-&lt;eid&gt;-&lt;stamp&gt;.csv</c> and one <c>NET_TRACE</c> line to the client log.
/// Idle cost is one integer test per predicted entity per frame.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
public class NetSyncTrace : MySessionComponentBase
{
    private const float VelocityThreshold = 0.01f;
    private const double PositionThreshold = 0.005;

    private struct Row
    {
        public double ClientMs, ServerMs;
        public bool Fresh, Valid;
        public int ResultType, AnimLinear, AnimPosition;
        public Vector3D DPos;
        public Vector3 DVel, DAng, V;
        public Vector3 Fwd, Up, Right;
        public Vector3 ThrustLocal, G;
        public float Mass;
        public Vector3D Com, VWorld;
        public double CameraM, RotDeltaDeg;
        public int AnimRotation;
        public string Profile;
        public Vector3D Force, Immediate, SetDelta;
        public int Forces, Immediates, Sets;
    }

    private static long _eid;
    private static int _framesLeft;
    private static int _waitLeft;
    private static string _stamp = "";
    private static readonly List<Row> Rows = new();
    private static double _lastServerMs = double.NaN;
    private static long _skippedAtStart;

    private static readonly AccessTools.FieldRef<MyPredictedSnapshotSync, MyEntity> EntityF = Ref<MyEntity>("m_entity");
    private static readonly AccessTools.FieldRef<MyPredictedSnapshotSync, MySnapshot> DeltaF = Ref<MySnapshot>("m_debugLastDelta");
    private static readonly AccessTools.FieldRef<MyPredictedSnapshotSync, MyTimeSpan> LastServerF = Ref<MyTimeSpan>("m_lastServerTimestamp");
    private static readonly AccessTools.FieldRef<MyPredictedSnapshotSync, int> AnimLinearF = Ref<int>("m_animDeltaLinearVelocityIterations");
    private static readonly AccessTools.FieldRef<MyPredictedSnapshotSync, int> AnimPositionF = Ref<int>("m_animDeltaPositionIterations");
    private static readonly AccessTools.FieldRef<MyPredictedSnapshotSync, int> AnimRotationF = Ref<int>("m_animDeltaRotationIterations");

    private static bool _bound;
    private static bool _forceHooks;

    // What reaches the traced grid's body between two samples: every force and every direct velocity write, with
    // the caller. Forces arrive from the parallel update threads, so they are summed under a lock.
    private static readonly object SumLock = new();
    private static Vector3D _force, _immediate, _setDelta;
    private static int _forces, _immediates, _sets;
    private static readonly Dictionary<string, int> Callers = new(StringComparer.Ordinal);
    private const int MaxCallers = 24;
    [ThreadStatic] private static bool _inBodySet;

    private static bool Watching(MyPhysicsBody body) =>
        _framesLeft > 0 && body?.Entity != null && body.Entity.EntityId == _eid;

    private static void AddForcePrefix(MyPhysicsBody __instance, MyPhysicsForceType type, Vector3? force, bool applyImmediately)
    {
        if (!Watching(__instance) || !force.HasValue) return;
        Vector3D f = force.Value;
        if (type == MyPhysicsForceType.ADD_BODY_FORCE_AND_BODY_TORQUE) f = Vector3D.TransformNormal(f, __instance.Entity.WorldMatrix);
        else if (type == MyPhysicsForceType.APPLY_WORLD_IMPULSE_AND_WORLD_ANGULAR_IMPULSE) f *= 60.0;
        lock (SumLock)
        {
            if (applyImmediately) { _immediate += f; _immediates++; }
            else { _force += f; _forces++; }
        }
        Caller((applyImmediately ? "immediate " : "queued ") + type);
    }

    private static void BodySetPrefix(MyPhysicsBody __instance, Vector3 value)
    {
        if (!Watching(__instance)) return;
        _inBodySet = true;
        RecordSet(__instance.LinearVelocity, value, "body");
    }

    private static void BodySetPostfix() => _inBodySet = false;

    /// <summary>A write straight to the Havok body; <c>MyPhysicsBody</c>'s own setter ends here too and is
    /// already counted, so the flag skips it.</summary>
    private static void HavokSetPrefix(HkEntity __instance, Vector3 value)
    {
        if (_inBodySet || _framesLeft <= 0) return;
        if (__instance is not HkRigidBody rb || rb.UserObject is not MyPhysicsBody body || !Watching(body)) return;
        RecordSet(rb.LinearVelocity, value, "havok");
    }

    /// <summary>SE's client-only collision prediction (<c>MyGridPhysics.PredictContactImpulse</c>) adds
    /// 0.005 x the relative speed x the partner's mass to the grid's velocity for every contact manifold. Names the
    /// partner, so a contact that should not exist -- one that pushes the grid every frame -- says what it is.</summary>
    private static void PredictPrefix(MyGridPhysics __instance, IMyEntity otherEntity)
    {
        if (_framesLeft <= 0 || otherEntity == null || __instance?.Entity == null || __instance.Entity.EntityId != _eid) return;
        string what;
        if (otherEntity is MyEntitySubpart sp)
        {
            string name = "?";
            if (sp.Parent != null)
                foreach (KeyValuePair<string, MyEntitySubpart> kv in sp.Parent.Subparts)
                    if (ReferenceEquals(kv.Value, sp)) { name = kv.Key; break; }
            var block = sp.Parent as MyCubeBlock;
            string owner = block == null ? "?" : block.CubeGrid != null && block.CubeGrid.EntityId == _eid ? "self" : block.CubeGrid?.DisplayName ?? "?";
            what = "subpart " + name + " of " + (block != null ? block.BlockDefinition.Id.SubtypeName + "@" + block.Position : "?") + " grid=" + owner;
        }
        else what = otherEntity.GetType().Name + " \"" + otherEntity.DisplayName + "\" " + otherEntity.EntityId;
        double rel = otherEntity.Physics != null ? (otherEntity.Physics.LinearVelocity - __instance.LinearVelocity).Length() : -1;
        Caller("contact " + what + " relSpeed~" + (Math.Round(rel / 10) * 10).ToString(CultureInfo.InvariantCulture), stack: false);
    }

    private static void RecordSet(Vector3 was, Vector3 value, string path)
    {
        lock (SumLock) { _setDelta += (Vector3D)(value - was); _sets++; }
        Caller("velocity-set " + path);
    }

    private static void Caller(string what, bool stack = true)
    {
        var trace = new System.Diagnostics.StackTrace(2, false);
        var sb = new StringBuilder(what);
        for (int i = 0; stack && i < Math.Min(trace.FrameCount, 10); i++)
        {
            MethodBase m = trace.GetFrame(i).GetMethod();
            if (m == null) continue;
            sb.Append(" < ").Append(m.DeclaringType != null ? m.DeclaringType.Name : "?").Append('.').Append(m.Name);
        }
        string key = sb.ToString();
        lock (SumLock)
        {
            if (Callers.TryGetValue(key, out int n)) Callers[key] = n + 1;
            else if (Callers.Count < MaxCallers) Callers[key] = 1;
        }
    }

    private static AccessTools.FieldRef<MyPredictedSnapshotSync, T> Ref<T>(string name)
    {
        try { return AccessTools.FieldRefAccess<MyPredictedSnapshotSync, T>(name); } catch { return null; }
    }

    internal static void TryPatch(Harmony harmony)
    {
        try
        {
            MethodInfo target = AccessTools.Method(typeof(MyPredictedSnapshotSync), "UpdateFromServerQueue");
            if (target == null || EntityF == null || DeltaF == null || LastServerF == null)
            {
                Plugin.Log("net trace: MyPredictedSnapshotSync.UpdateFromServerQueue or its fields did not resolve; "
                           + "dampprobe trace will have no client half.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(NetSyncTrace).GetMethod(nameof(Postfix),
                BindingFlags.NonPublic | BindingFlags.Static)));
            _bound = true;
            MethodInfo addForce = AccessTools.Method(typeof(MyPhysicsBody), "AddForce");
            if (addForce != null)
                harmony.Patch(addForce, prefix: new HarmonyMethod(typeof(NetSyncTrace).GetMethod(nameof(AddForcePrefix),
                    BindingFlags.NonPublic | BindingFlags.Static)));
            MethodInfo bodySet = AccessTools.PropertySetter(typeof(MyPhysicsBody), "LinearVelocity");
            MethodInfo havokSet = AccessTools.PropertySetter(typeof(HkEntity), "LinearVelocity");
            if (bodySet != null)
                harmony.Patch(bodySet, prefix: new HarmonyMethod(typeof(NetSyncTrace).GetMethod(nameof(BodySetPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static)), postfix: new HarmonyMethod(typeof(NetSyncTrace).GetMethod(nameof(BodySetPostfix),
                    BindingFlags.NonPublic | BindingFlags.Static)));
            if (havokSet != null)
                harmony.Patch(havokSet, prefix: new HarmonyMethod(typeof(NetSyncTrace).GetMethod(nameof(HavokSetPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static)));
            _forceHooks = addForce != null && bodySet != null && havokSet != null;
            MethodInfo predict = AccessTools.Method(typeof(MyGridPhysics), "PredictContactImpulse");
            if (predict != null)
                harmony.Patch(predict, prefix: new HarmonyMethod(typeof(NetSyncTrace).GetMethod(nameof(PredictPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static)));
        }
        catch (Exception ex)
        {
            Plugin.Log("net trace: could not patch UpdateFromServerQueue: " + ex.Message);
        }
    }

    public override void LoadData()
    {
        UplinkComponent.RegisterHandler(WireMessageKind.NetTraceStart, OnStart);
    }

    private static void OnStart(byte[] data)
    {
        if (!FactionsWire.TryReadNetTraceStart(data, out long eid, out int frames, out string stamp)) return;
        if (!_bound)
        {
            Plugin.Log("NET_TRACE eid=" + eid + " stamp=" + stamp + " refused: prediction hook not bound");
            return;
        }
        Rows.Clear();
        _lastServerMs = double.NaN;
        lock (SumLock)
        {
            Callers.Clear();
            _force = _immediate = _setDelta = Vector3D.Zero;
            _forces = _immediates = _sets = 0;
        }
        _skippedAtStart = Interlocked.Read(ref Patches.OwnSubpartContactPatch.Skipped);
        _eid = eid;
        _stamp = stamp ?? "";
        _framesLeft = Math.Max(1, frames);
        _waitLeft = _framesLeft + 300;
        Plugin.Log("NET_TRACE eid=" + eid + " stamp=" + _stamp + " started frames=" + _framesLeft);
    }

    public override void UpdateAfterSimulation()
    {
        if (_framesLeft <= 0) return;
        if (--_waitLeft <= 0) Finish("timed out: prediction ran for " + Rows.Count + " of the frames");
    }

    private static void Postfix(MyPredictedSnapshotSync __instance, MyTimeSpan clientTimestamp, MySnapshotHistory.MyItem __result)
    {
        if (_framesLeft <= 0) return;
        try
        {
            MyEntity entity = EntityF(__instance);
            if (entity == null || entity.EntityId != _eid) return;
            double serverMs = LastServerF(__instance).Milliseconds;
            bool fresh = serverMs != _lastServerMs;
            _lastServerMs = serverMs;
            MySnapshot delta = DeltaF(__instance);
            MatrixD wm = entity.WorldMatrix;
            var thrust = entity.Components.Get<MyEntityThrustComponent>();
            var physics = entity.Physics;
            delta.Rotation.GetAxisAngle(out Vector3 _, out float angle);
            if (angle > Math.PI) angle = (float)(2 * Math.PI) - angle;
            Vector3D force, immediate, setDelta;
            int forces, immediates, sets;
            lock (SumLock)
            {
                force = _force; immediate = _immediate; setDelta = _setDelta;
                forces = _forces; immediates = _immediates; sets = _sets;
                _force = _immediate = _setDelta = Vector3D.Zero;
                _forces = _immediates = _sets = 0;
            }
            Rows.Add(new Row
            {
                Com = physics != null ? physics.CenterOfMassWorld : wm.Translation,
                VWorld = physics != null ? (Vector3D)physics.LinearVelocity : Vector3D.Zero,
                CameraM = MySector.MainCamera != null ? Vector3D.Distance(wm.Translation, MySector.MainCamera.Position) : -1,
                RotDeltaDeg = fresh ? MathHelper.ToDegrees(angle) : 0,
                AnimRotation = AnimRotationF != null ? AnimRotationF(__instance) : -1,
                Profile = (entity.LastSnapshotFlags as MyPredictedSnapshotSyncSetup)?.ProfileName ?? "?",
                Force = force, Immediate = immediate, SetDelta = setDelta,
                Forces = forces, Immediates = immediates, Sets = sets,
                ClientMs = clientTimestamp.Milliseconds,
                ServerMs = serverMs,
                Fresh = fresh,
                Valid = __result.Valid,
                ResultType = (int)__result.Type,
                AnimLinear = AnimLinearF != null ? AnimLinearF(__instance) : -1,
                AnimPosition = AnimPositionF != null ? AnimPositionF(__instance) : -1,
                DPos = fresh ? delta.Position : Vector3D.Zero,
                DVel = fresh ? delta.LinearVelocity : Vector3.Zero,
                DAng = fresh ? delta.AngularVelocity : Vector3.Zero,
                V = physics != null ? physics.LinearVelocity : Vector3.Zero,
                Mass = physics != null ? physics.Mass : 0f,
                G = physics != null && physics.RigidBody != null ? physics.RigidBody.Gravity : Vector3.Zero,
                ThrustLocal = thrust != null ? thrust.FinalThrust : Vector3.Zero,
                Fwd = (Vector3)wm.Forward,
                Up = (Vector3)wm.Up,
                Right = (Vector3)wm.Right,
            });
            if (--_framesLeft == 0) Finish(null);
        }
        catch (Exception ex)
        {
            Finish("sample failed: " + ex.Message);
        }
    }

    private static void Finish(string failure)
    {
        _framesLeft = 0;
        string file = null;
        try { file = WriteCsv(); }
        catch (Exception ex) { failure = (failure != null ? failure + "; " : "") + "csv failed: " + ex.Message; }
        Plugin.Log(Summary(file, failure));
        lock (SumLock)
        {
            foreach (KeyValuePair<string, int> kv in Callers)
                Plugin.Log("NET_TRACE_CALLER eid=" + _eid + " n=" + kv.Value + " " + kv.Key);
            Callers.Clear();
        }
        Rows.Clear();
    }

    private static string Summary(string file, string failure)
    {
        var sb = new StringBuilder("NET_TRACE eid=").Append(_eid).Append(" stamp=").Append(_stamp)
            .Append(" frames=").Append(Rows.Count);
        if (failure != null) sb.Append(" failure=\"").Append(failure).Append('"');
        int fresh = 0, velOver = 0, posOver = 0, invalidFresh = 0;
        Vector3D dvMean = Vector3D.Zero, dpMean = Vector3D.Zero;
        double dvAbs = 0, dvMax = 0, dpAbs = 0, dpMax = 0, accelMax = 0;
        int lastFresh = -1;
        var spacing = new List<int>();
        var kicks = new List<int>();
        for (int i = 0; i < Rows.Count; i++)
        {
            Row r = Rows[i];
            if (i > 0)
            {
                double accel = (r.V - Rows[i - 1].V).Length() * 60.0;
                accelMax = Math.Max(accelMax, accel);
            }
            if (!r.Fresh) continue;
            fresh++;
            if (!r.Valid) invalidFresh++;
            if (lastFresh >= 0) spacing.Add(i - lastFresh);
            lastFresh = i;
            double dv = r.DVel.Length(), dp = r.DPos.Length();
            dvAbs += dv; dpAbs += dp;
            dvMax = Math.Max(dvMax, dv); dpMax = Math.Max(dpMax, dp);
            if (dv > VelocityThreshold) { velOver++; kicks.Add(i); }
            if (dp > PositionThreshold) posOver++;
            dvMean += Local(r.DVel, r);
            dpMean += Local(r.DPos, r);
        }
        if (fresh > 0) { dvMean /= fresh; dpMean /= fresh; dvAbs /= fresh; dpAbs /= fresh; }
        Vector3D aThrust = Vector3D.Zero;
        double mass = 0;
        foreach (Row r in Rows)
        {
            if (r.Mass > 0) aThrust += ThrustAxes(r) / r.Mass;
            mass += r.Mass;
        }
        if (Rows.Count > 0) { aThrust /= Rows.Count; mass /= Rows.Count; }
        spacing.Sort();
        sb.Append(" compares=").Append(fresh)
          .Append(" spacingMedianFrames=").Append(spacing.Count > 0 ? spacing[spacing.Count / 2] : 0)
          .Append(" velOver").Append(F(VelocityThreshold, 2)).Append('=').Append(velOver)
          .Append(" posOver").Append(F(PositionThreshold, 3)).Append('=').Append(posOver)
          .Append(" dvMeanFwdUpRight=").Append(V3(dvMean, 4))
          .Append(" dvAbsMean=").Append(F(dvAbs, 4)).Append(" dvMax=").Append(F(dvMax, 4))
          .Append(" dpMeanFwdUpRight=").Append(V3(dpMean, 4))
          .Append(" dpAbsMean=").Append(F(dpAbs, 4)).Append(" dpMax=").Append(F(dpMax, 4))
          .Append(" notApplied=").Append(invalidFresh)
          .Append(" clientAccelMaxMs2=").Append(F(accelMax, 3))
          .Append(" massKg=").Append(F(mass, 0))
          .Append(" thrustOverMassFwdUpRight=").Append(V3(aThrust, 4));
        if (kicks.Count > 1)
        {
            var gaps = new List<int>();
            for (int i = 1; i < kicks.Count; i++) gaps.Add(kicks[i] - kicks[i - 1]);
            gaps.Sort();
            sb.Append(" velCorrectionSpacingMedianFrames=").Append(gaps[gaps.Count / 2]);
        }
        double camMin = double.MaxValue, camMax = 0, rotMax = 0;
        int forcesTotal = 0, immTotal = 0, setsTotal = 0;
        var profiles = new Dictionary<string, int>();
        foreach (Row r in Rows)
        {
            if (r.CameraM >= 0) { camMin = Math.Min(camMin, r.CameraM); camMax = Math.Max(camMax, r.CameraM); }
            rotMax = Math.Max(rotMax, r.RotDeltaDeg);
            forcesTotal += r.Forces; immTotal += r.Immediates; setsTotal += r.Sets;
            string key = r.Profile ?? "?";
            profiles[key] = profiles.TryGetValue(key, out int c) ? c + 1 : 1;
        }
        sb.Append(" cameraM=").Append(F(camMin == double.MaxValue ? -1 : camMin, 1)).Append("..").Append(F(camMax, 1))
          .Append(" rotDeltaMaxDeg=").Append(F(rotMax, 2))
          .Append(" profiles=").Append(string.Join("/", profiles.Select(kv => kv.Key + ":" + kv.Value)))
          .Append(" queuedForces=").Append(_forceHooks ? forcesTotal.ToString(CultureInfo.InvariantCulture) : "n/a")
          .Append(" immediateForces=").Append(immTotal)
          .Append(" velocitySets=").Append(setsTotal)
          .Append(" ownSubpartImpulsesSkipped=").Append(Interlocked.Read(ref Patches.OwnSubpartContactPatch.Skipped) - _skippedAtStart);
        if (file != null) sb.Append(" csv=").Append(file);
        return sb.ToString();
    }

    private static string WriteCsv()
    {
        if (Rows.Count == 0) return null;
        string dir = Path.Combine(MyFileSystem.UserDataPath, "Storage", "Factions");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "nettrace-" + _eid + "-" + _stamp + ".csv");
        var sb = new StringBuilder("i,clientMs,serverMs,fresh,applied,resultType,animLinear,animPosition,"
                                   + "dvFwd,dvUp,dvRight,dpFwd,dpUp,dpRight,daFwd,daUp,daRight,vFwd,vUp,vRight,speed,accelMs2,"
                                   + "massKg,thrustFwd,thrustUp,thrustRight,gFwd,gUp,gRight,"
                                   + "forceFwd,forceUp,forceRight,forces,immFwd,immUp,immRight,immediates,setFwd,setUp,setRight,sets,"
                                   + "rotDeltaDeg,animRotation,cameraM,profile,comX,comY,comZ,vx,vy,vz\n");
        for (int i = 0; i < Rows.Count; i++)
        {
            Row r = Rows[i];
            double accel = i > 0 ? (r.V - Rows[i - 1].V).Length() * 60.0 : 0;
            sb.Append(i).Append(',').Append(F(r.ClientMs, 1)).Append(',').Append(F(r.ServerMs, 1))
              .Append(',').Append(r.Fresh ? 1 : 0).Append(',').Append(r.Valid ? 1 : 0).Append(',').Append(r.ResultType)
              .Append(',').Append(r.AnimLinear).Append(',').Append(r.AnimPosition)
              .Append(',').Append(V3Csv(Local(r.DVel, r), 4))
              .Append(',').Append(V3Csv(Local(r.DPos, r), 4))
              .Append(',').Append(V3Csv(Local(r.DAng, r), 5))
              .Append(',').Append(V3Csv(Local(r.V, r), 4))
              .Append(',').Append(F(r.V.Length(), 4)).Append(',').Append(F(accel, 4))
              .Append(',').Append(F(r.Mass, 0))
              .Append(',').Append(V3Csv(ThrustAxes(r), 0))
              .Append(',').Append(V3Csv(Local(r.G, r), 4))
              .Append(',').Append(V3Csv(Local(r.Force, r), 0)).Append(',').Append(r.Forces)
              .Append(',').Append(V3Csv(Local(r.Immediate, r), 0)).Append(',').Append(r.Immediates)
              .Append(',').Append(V3Csv(Local(r.SetDelta, r), 4)).Append(',').Append(r.Sets)
              .Append(',').Append(F(r.RotDeltaDeg, 3)).Append(',').Append(r.AnimRotation)
              .Append(',').Append(F(r.CameraM, 1)).Append(',').Append(r.Profile)
              .Append(',').Append(V3Csv(r.Com, 3)).Append(',').Append(V3Csv(r.VWorld, 4)).Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>FinalThrust is grid-local: forward is -Z, up +Y, right +X.</summary>
    private static Vector3D ThrustAxes(Row r) => new(-r.ThrustLocal.Z, r.ThrustLocal.Y, r.ThrustLocal.X);

    private static Vector3D Local(Vector3D v, Row r)
    {
        return new Vector3D(Vector3D.Dot(v, r.Fwd), Vector3D.Dot(v, r.Up), Vector3D.Dot(v, r.Right));
    }

    private static string V3(Vector3D v, int places) => "(" + V3Csv(v, places) + ")";

    private static string V3Csv(Vector3D v, int places) => F(v.X, places) + "," + F(v.Y, places) + "," + F(v.Z, places);

    private static string F(double v, int places) => v.ToString("F" + places, CultureInfo.InvariantCulture);
}
