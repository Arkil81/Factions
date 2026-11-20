using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Havok;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRageMath;

namespace FactionsUI.Rock;

/// <summary>
/// The client half of FAC-445 lever A: rock this client derived (<see cref="RockComponent"/>) gets its Havok
/// collision grid only once a moving body may reach it within <see cref="LookaheadS"/> seconds plus
/// <see cref="ReachM"/>, and loses it again past <see cref="ReleaseMarginM"/> beyond that.
///
/// <para><b>Why.</b> <c>MyVoxelPhysicsBody.CreateRigidBodies</c> builds an empty <c>HkUniformGridShape</c> of
/// ~640 KB for every voxel map in a stepped physics world, whatever the rock's size, and a cluster is at least 20 km
/// round any dynamic body. The client runs the same code as the server here (no single-cluster mode, LOD1 voxel
/// physics off), so every derived rock round the player's ship pays it, and the server's own gate does not reach
/// rock this client built.</para>
///
/// <para><b>How.</b> The same mechanism as the server's <c>Factions.RockContactPhysics</c>: a prefix on
/// <c>CreateRigidBodies</c> declines for owned pristine rock outside the hot set, and SE's tenth-frame re-entry
/// (<c>UpdateBeforeSimulation10</c>) builds it within ten frames of turning hot. A release cancels the body's
/// pending cell jobs, removes it from its world, disposes it through SE's <c>CloseRigidBody</c> and clears
/// <c>m_bodiesInitialized</c>: the state a rock is in before its first grid. The entity, storage and render are
/// untouched. Edited rock has left <see cref="RockComponent"/>'s set and is never held or released.</para>
///
/// <para><b>Switch.</b> <see cref="Config.RockContactPhysics"/>, read every frame. Switching on marks every owned
/// rock that already holds a grid for release, so one session can read both states; switching off lets every held
/// rock build within ten frames.</para>
///
/// <para><b>What it cannot see.</b> A ray or projectile at cold rock beyond <see cref="ReachM"/> of every moving
/// body passes through it on this client.</para>
/// </summary>
internal static class RockContactPhysics
{
    private const double ReachM = 1500;
    private const double LookaheadS = 2;
    private const double ReleaseMarginM = 1000;
    private const int ReleasesPerScan = 50;
    private const int ScanFrames = 10;
    private const double ReportSeconds = 10;

    /// <summary>A mover this close to a rock's bounds must find its grid built: the build box leads it by
    /// <see cref="ReachM"/> plus <see cref="LookaheadS"/> of travel, and a hot rock builds within ~20 frames.</summary>
    private const double NearM = 100;
    private const int MaxColdNearLogs = 20;

    /// <summary>Measured on the server (FAC-445 run 18): flat from 64 m to 1,024 m maps.</summary>
    private const double GridKb = 640;

    private static HashSet<long> _hot = new HashSet<long>();
    private static HashSet<long> _next = new HashSet<long>();
    private static readonly List<BoundingBoxD> Boxes = new List<BoundingBoxD>();
    private static readonly List<MyVoxelBase> Found = new List<MyVoxelBase>();
    private static int _frame;
    private static bool _wasOn;
    private static long _deferred, _cooled, _freed, _freeFailed, _scanTicks, _scans, _windowStart;
    private static long _warmNear, _coldNear;
    private static int _coldNearLogs;

    private static readonly HashSet<MyEntity> Candidates = new HashSet<MyEntity>();
    private static readonly ConcurrentQueue<MyEntity> Added = new ConcurrentQueue<MyEntity>();
    private static readonly ConcurrentQueue<MyEntity> Removed = new ConcurrentQueue<MyEntity>();
    private static readonly List<MyEntity> Dead = new List<MyEntity>();
    private static bool _subscribed;

    /// <summary>Rock let through to build its grid, and the rock inside any release box this scan.</summary>
    private static readonly HashSet<long> Tracked = new HashSet<long>();
    private static readonly HashSet<long> Keep = new HashSet<long>();
    private static readonly List<long> Expired = new List<long>();
    private static int _movers;

    /// <summary>Set by <see cref="RockComponent"/> across <c>AddVoxelMap</c>, while the new rock is not yet in its
    /// owned set.</summary>
    [ThreadStatic] internal static bool SpawningPristine;

    private static readonly AccessTools.FieldRef<MyVoxelPhysicsBody, bool> BodiesInitialized = Ref("m_bodiesInitialized");
    private static readonly FieldInfo WorkTrackerF = AccessTools.Field(typeof(MyVoxelPhysicsBody), "m_workTracker");
    private static readonly FieldInfo RunningBatchF = AccessTools.Field(typeof(MyVoxelPhysicsBody), "RunningBatchTask");
    private static readonly MethodInfo CloseRigidBodyM = AccessTools.Method(typeof(MyPhysicsBody), "CloseRigidBody");
    private static MethodInfo _cancelAllM;

    private static AccessTools.FieldRef<MyVoxelPhysicsBody, bool> Ref(string name)
    {
        try { return AccessTools.FieldRefAccess<MyVoxelPhysicsBody, bool>(name); } catch { return null; }
    }

    private static bool On => Config.Current.RockContactPhysics && BodiesInitialized != null;

    internal static void TryPatch(Harmony harmony)
    {
        try
        {
            MethodInfo target = AccessTools.Method(typeof(MyVoxelPhysicsBody), "CreateRigidBodies");
            if (target == null || BodiesInitialized == null)
            {
                Plugin.Log("rock contact physics: MyVoxelPhysicsBody.CreateRigidBodies or m_bodiesInitialized did not "
                           + "resolve; every derived rock keeps its collision grid.");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(RockContactPhysics).GetMethod(nameof(Prefix),
                BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
            Plugin.Log("rock contact physics: grid gate bound");
        }
        catch (Exception ex)
        {
            Plugin.Log("rock contact physics: could not patch CreateRigidBodies: " + ex.Message);
        }
    }

    private static bool Prefix(MyVoxelPhysicsBody __instance)
    {
        if (!On) return true;
        try
        {
            if (BodiesInitialized(__instance)) return true;
            if (__instance.Entity is not MyVoxelBase voxel) return true;
            RockComponent rock = RockComponent.Instance;
            bool owned = SpawningPristine || (rock != null && rock.OwnsPristine(voxel));
            if (!owned || _hot.Contains(voxel.EntityId)) return true;
            if (SpawningPristine)
            {
                BoundingBoxD aabb = voxel.PositionComp.WorldAABB;
                foreach (BoundingBoxD box in Boxes)
                    if (box.Intersects(aabb)) return true;
            }
            _deferred++;
            return false;
        }
        catch { return true; }
    }

    /// <summary>Once per frame from <see cref="RockComponent"/>; scans every <see cref="ScanFrames"/>th frame and
    /// reports every <see cref="ReportSeconds"/> whether or not the switch is on.</summary>
    internal static void Tick(RockComponent rock)
    {
        if (++_frame % ScanFrames != 0) return;
        long t0 = Stopwatch.GetTimestamp();
        bool on = On;
        if (on && !_wasOn) SeedRelease(rock);
        if (!on && _wasOn) { _hot.Clear(); Boxes.Clear(); Tracked.Clear(); Keep.Clear(); }
        _wasOn = on;
        if (on)
        {
            try
            {
                ScanBoxes(rock);
                foreach (long id in _hot) Tracked.Add(id);
                ReleaseOutsideBand(rock);
            }
            catch (Exception ex) { Plugin.Log("rock contact physics: scan failed: " + ex.Message); }
            _scanTicks += Stopwatch.GetTimestamp() - t0;
            _scans++;
        }
        Report(rock, on);
    }

    internal static void Reset()
    {
        _hot.Clear(); _next.Clear(); Boxes.Clear(); Tracked.Clear(); Keep.Clear();
        Candidates.Clear();
        while (Added.TryDequeue(out _)) { }
        while (Removed.TryDequeue(out _)) { }
        if (_subscribed)
        {
            MyEntities.OnEntityAdd -= OnAdd;
            MyEntities.OnEntityRemove -= OnRemove;
            _subscribed = false;
        }
        _wasOn = false;
        _windowStart = 0;
        _coldNearLogs = 0;
    }

    /// <summary>Rock that built its grid while the switch was off is not tracked; on the switch, every owned rock is,
    /// and the next scans release whatever lies outside every band.</summary>
    private static void SeedRelease(RockComponent rock)
    {
        if (rock == null) return;
        foreach (long id in rock.OwnedIds) Tracked.Add(id);
    }

    private static void ReleaseOutsideBand(RockComponent rock)
    {
        Expired.Clear();
        foreach (long id in Tracked)
            if (!Keep.Contains(id)) { Expired.Add(id); if (Expired.Count >= ReleasesPerScan) break; }
        foreach (long id in Expired)
        {
            Tracked.Remove(id);
            var voxel = MyEntities.TryGetEntityById(id, out MyEntity e) ? e as MyVoxelBase : null;
            if (voxel == null || voxel.MarkedForClose || rock == null || !rock.OwnsPristine(voxel)) continue;
            if (voxel.Physics is not MyVoxelPhysicsBody body || !BodiesInitialized(body)) continue;
            if (TryRelease(body)) _freed++; else _freeFailed++;
        }
    }

    private static bool TryRelease(MyVoxelPhysicsBody body)
    {
        try
        {
            if (WorkTrackerF == null || RunningBatchF == null || CloseRigidBodyM == null) return false;
            if (body.RigidBody2 != null) return false;
            if (RunningBatchF.GetValue(body) is Array running)
                foreach (object job in running) if (job != null) return false;
            object tracker = WorkTrackerF.GetValue(body);
            if (tracker != null)
            {
                _cancelAllM ??= AccessTools.Method(tracker.GetType(), "CancelAll");
                _cancelAllM?.Invoke(tracker, null);
            }
            HkRigidBody rb = body.RigidBody;
            HkWorld world = body.HavokWorld;
            if (rb != null && world != null && rb.InWorld) world.RemoveRigidBody(rb);
            CloseRigidBodyM.Invoke(body, null);
            BodiesInitialized(body) = false;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log("rock contact physics: release failed: " + ex.Message);
            return false;
        }
    }

    private static void OnAdd(MyEntity e) { if (e != null && e is not MyVoxelBase) Added.Enqueue(e); }
    private static void OnRemove(MyEntity e) { if (e != null && e is not MyVoxelBase) Removed.Enqueue(e); }

    /// <summary>Non-voxel top-level entities, from SE's add and remove events: every derived rock is a top-level
    /// entity, so walking <c>MyEntities.GetEntities()</c> would grow with the belt.</summary>
    private static void SyncCandidates()
    {
        if (!_subscribed)
        {
            MyEntities.OnEntityAdd += OnAdd;
            MyEntities.OnEntityRemove += OnRemove;
            _subscribed = true;
            foreach (MyEntity e in MyEntities.GetEntities())
                if (e != null && e is not MyVoxelBase) Candidates.Add(e);
        }
        while (Added.TryDequeue(out MyEntity x)) Candidates.Add(x);
        while (Removed.TryDequeue(out MyEntity y)) Candidates.Remove(y);
    }

    private static void ScanBoxes(RockComponent rock)
    {
        _next.Clear();
        Keep.Clear();
        Boxes.Clear();
        int movers = 0;
        SyncCandidates();
        Dead.Clear();
        foreach (MyEntity e in Candidates)
        {
            if (e.Closed) { Dead.Add(e); continue; }
            if (e.MarkedForClose || e.Physics == null || e.Physics.IsStatic) continue;
            BoundingBoxD aabb = e.PositionComp.WorldAABB;
            BoundingBoxD box = SweptBox(aabb.Center, e.Physics.LinearVelocity, ReachM + aabb.HalfExtents.Max());
            Boxes.Add(box);
            movers++;
            if (rock == null) continue;
            BoundingBoxD near = aabb.GetInflated(NearM);
            BoundingBoxD band = box.GetInflated(ReleaseMarginM);
            Found.Clear();
            MyGamePruningStructure.GetAllVoxelMapsInBox(ref band, Found);
            foreach (MyVoxelBase voxel in Found)
            {
                if (!rock.OwnsPristine(voxel)) continue;
                Keep.Add(voxel.EntityId);
                BoundingBoxD rockBox = voxel.PositionComp.WorldAABB;
                if (box.Intersects(rockBox)) _next.Add(voxel.EntityId);
                if (near.Intersects(rockBox)) CheckNear(e, voxel);
            }
        }
        foreach (MyEntity d in Dead) Candidates.Remove(d);
        foreach (long id in _hot) if (!_next.Contains(id)) _cooled++;
        (_hot, _next) = (_next, _hot);
        _movers = movers;
    }

    /// <summary>Counts a mover inside <see cref="NearM"/> of an owned rock's bounds by whether the rock has its grid.
    /// A cold one is rock this client's own prediction would fly through while the server collides.</summary>
    private static void CheckNear(MyEntity mover, MyVoxelBase voxel)
    {
        if (voxel.Physics is not MyVoxelPhysicsBody body) return;
        if (BodiesInitialized(body)) { _warmNear++; return; }
        _coldNear++;
        if (!Config.Current.DebugLogging || _coldNearLogs >= MaxColdNearLogs) return;
        _coldNearLogs++;
        Plugin.Log("ROCK_COLD_NEAR rock=" + voxel.EntityId + " hot=" + (_hot.Contains(voxel.EntityId) ? 1 : 0)
                   + " mover=" + mover.EntityId + " " + mover.GetType().Name
                   + " speed=" + mover.Physics.LinearVelocity.Length().ToString("F0")
                   + (_coldNearLogs >= MaxColdNearLogs ? " (last ROCK_COLD_NEAR this session)" : ""));
    }

    /// <summary>The server's <c>RockContact.SweptBox</c>: the body's position now and where its velocity carries it
    /// within <see cref="LookaheadS"/>, inflated on every side by <paramref name="reachM"/>.</summary>
    private static BoundingBoxD SweptBox(Vector3D pos, Vector3 vel, double reachM)
    {
        Vector3D end = pos + (Vector3D)vel * LookaheadS;
        var r = new Vector3D(reachM);
        return new BoundingBoxD(Vector3D.Min(pos, end) - r, Vector3D.Max(pos, end) + r);
    }

    /// <summary>Owned rock holding a grid, and Havok's pool. The pool is a high-water mark that a release does not
    /// lower (FAC-445 run 15), so <c>gridMB</c> — grids held x ~640 KB — is the reading that moves. <c>swapped</c> is
    /// owned rock whose storage the server replicated over ours; <c>unheld</c> is owned rock the gate may not touch,
    /// which should read 0. <c>near</c> is mover-rock pairs within <see cref="NearM"/> per scan, with a grid / without;
    /// the second should read 0.</summary>
    private static void Report(RockComponent rock, bool on)
    {
        if (!Config.Current.DebugLogging) { _windowStart = 0; return; }
        long now = Stopwatch.GetTimestamp();
        if (_windowStart == 0) { _windowStart = now; return; }
        if (now - _windowStart < ReportSeconds * Stopwatch.Frequency) return;
        int withGrid = 0, owned = 0, swapped = 0, unheld = 0;
        if (rock != null && BodiesInitialized != null)
            foreach (long id in rock.OwnedIds)
            {
                var v = MyEntities.TryGetEntityById(id, out MyEntity e) ? e as MyVoxelBase : null;
                if (v?.Physics is not MyVoxelPhysicsBody b) continue;
                owned++;
                if (v.ContentChanged) swapped++;
                if (!rock.OwnsPristine(v)) unheld++;
                if (BodiesInitialized(b)) withGrid++;
            }
        long pool;
        try { pool = HkBaseSystem.GetCurrentMemoryConsumption(); } catch { pool = -1; }
        long privateMb;
        try { using (Process p = Process.GetCurrentProcess()) privateMb = p.PrivateMemorySize64 >> 20; } catch { privateMb = -1; }
        Plugin.Log("ROCK_CONTACT gate=" + (on ? "on" : "off")
                   + " withGrid=" + withGrid + "/" + owned
                   + " gridMB=" + (withGrid * GridKb / 1024).ToString("F0")
                   + " swapped=" + swapped + " unheld=" + unheld
                   + " havokMB=" + (pool < 0 ? "?" : (pool >> 20).ToString())
                   + " privateMB=" + privateMb
                   + " movers=" + _movers + " hot=" + _hot.Count + " near=" + _warmNear + "/" + _coldNear
                   + " deferred=" + _deferred + " cooled=" + _cooled
                   + " released=" + _freed + " releaseRefused=" + _freeFailed
                   + " scanMsAvg=" + (_scans > 0 ? (_scanTicks * 1000.0 / Stopwatch.Frequency / _scans).ToString("F2") : "-"));
        _windowStart = now;
        _deferred = _cooled = _freed = _freeFailed = _scanTicks = _scans = _warmNear = _coldNear = 0;
    }
}
