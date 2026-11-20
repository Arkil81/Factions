using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using NLog;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// FAC-445 lever A: pristine feature rock gets its Havok
    /// collision grid only once a moving body may reach it within <see cref="LookaheadS"/> seconds plus
    /// <see cref="ReachM"/>, instead of as soon as a physics cluster covers it.
    ///
    /// <para><b>Why.</b> <c>MyVoxelPhysicsBody.CreateRigidBodies</c> builds an empty <c>HkUniformGridShape</c> of
    /// ~640 KB for every voxel map in a stepped world, from 64 m to 1 km alike, and a cluster is at least 20 km round
    /// any dynamic body, so all ~1,850 rocks in one observer's view pay it. Five observed areas exhaust Havok's memory
    /// (FAC-445 runs 12-18). SE already builds the cell meshes only where a body's prediction box reaches; this moves
    /// the empty grid to the same moment.</para>
    ///
    /// <para><b>How.</b> A prefix on <c>CreateRigidBodies</c> declines for rock this session's
    /// <see cref="FeatureRockMaterializer"/> owns and that is not in the hot set. SE re-enters the method every tenth
    /// frame (<c>UpdateBeforeSimulation10</c>) for a voxel in a world without bodies, a state vanilla's own lazy voxel
    /// physics produces, so a declined rock is picked up within ten frames of turning hot. Every
    /// <see cref="ScanFrames"/> frames each top-level non-voxel entity with non-static physics marks the owned rock
    /// inside its swept contact box (<see cref="RockContact.SweptBox"/>) hot. A rock spawning this frame is tested
    /// against the last scan's boxes directly, so rock built round a moving hull is not a scan late.</para>
    ///
    /// <para><b>Released when it leaves the band.</b> A rock outside every box inflated by <see cref="ReleaseMarginM"/>
    /// loses its grid in place — it was built 2 s ahead, so 1 km past the build box is the hysteresis (operator,
    /// 2026-10-05: <i>"since we build it 2s ahead, we can drop it at 2km+margin"</i>): its pending cell jobs are cancelled, its body leaves the world and is disposed through
    /// SE's own <c>CloseRigidBody</c>, and <c>m_bodiesInitialized</c> is cleared. That is the state vanilla's lazy
    /// voxel physics leaves a rock in before its first grid (<c>Activate</c>, <c>GetShape</c>, <c>PrefetchShapeOnRay</c>
    /// and the batch-job callbacks all handle a null body), and the entity is untouched, so a client sees nothing.
    /// v0.457.0 released nothing and built 1,518 grids in 100 s of a 1,000 m/s dive with a 10 s lookahead (FAC-445).
    /// Edited rock leaves the materialiser's ownership and is never held back or released. Switching off lets every
    /// held rock build its grid within ten frames.</para>
    ///
    /// <para><b>What it cannot see.</b> A long-range shot or ray at cold rock beyond <see cref="ReachM"/> of every
    /// moving body passes through it.</para>
    /// </summary>
    internal static class RockContactPhysics
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private const double ReachM = 1500;
        /// <summary>A rock turning hot builds within ~20 frames (one scan, then SE's tenth-frame re-entry), ~0.33 s;
        /// 2 s is a 6x margin and keeps a 1,000 m/s hull's box 2 km ahead instead of 10.</summary>
        private const double LookaheadS = 2;
        private const double ReleaseMarginM = 1000;
        private const int ReleasesPerScan = 50;
        private const int ScanFrames = 10;
        private const double ReportSeconds = 10;

        private static HashSet<long> _hot = new HashSet<long>();
        private static HashSet<long> _next = new HashSet<long>();
        private static readonly List<BoundingBoxD> Boxes = new List<BoundingBoxD>();
        private static readonly List<MyVoxelBase> Found = new List<MyVoxelBase>();
        private static int _frame;
        private static long _deferred, _released, _freed, _freeFailed, _scanTicks, _scans, _windowStart;

        /// <summary>Where a scan's time goes, summed over the report window: walking the entity set, the pruning
        /// queries, testing the rock they return, and releasing. <c>_entities</c> and <c>_returned</c> are the
        /// counts those phases scale with, and <c>_rockMovers</c> the movers whose query returned any rock.</summary>
        private static long _tEnumerate, _tQuery, _tTest, _tRelease, _entities, _returned, _rockMovers, _cachedMovers;

        /// <summary>Non-voxel top-level entities, kept from SE's add and remove events instead of walking
        /// <c>MyEntities.GetEntities()</c>: every live feature rock is a top-level entity, so the walk grew with the belt
        /// (~13,600 entities for ~110 movers with five observed areas, 1.4-1.6 ms per scan, FAC-445 run 21). Events are
        /// queued and drained on the scan, which runs on the game thread.</summary>
        private static readonly HashSet<MyEntity> Candidates = new HashSet<MyEntity>();
        private static readonly ConcurrentQueue<MyEntity> Added = new ConcurrentQueue<MyEntity>();
        private static readonly ConcurrentQueue<MyEntity> Removed = new ConcurrentQueue<MyEntity>();
        private static readonly List<MyEntity> Dead = new List<MyEntity>();
        private static bool _subscribed;

        /// <summary>A mover at rest reuses its last query until it moves <see cref="RestMoveM"/> or
        /// <see cref="RestRefreshScans"/> scans pass: ~100 floating objects and station grids query every scan
        /// otherwise, with nobody online (run 21). Rock spawning beside one is caught at spawn against
        /// <see cref="Boxes"/>, and the refresh picks up anything else.</summary>
        private sealed class RestCache { internal Vector3D Pos; internal int Scan; internal readonly List<long> Keep = new List<long>(); internal readonly List<long> Hot = new List<long>(); }
        private static readonly Dictionary<long, RestCache> AtRest = new Dictionary<long, RestCache>();
        private const double RestSpeedMs = 1.0;
        private const double RestMoveM = 50.0;
        private const int RestRefreshScans = 60;
        private static int _scanNo;
        /// <summary>Rock let through to build its grid, and the rock inside any release box this scan.</summary>
        private static readonly HashSet<long> Tracked = new HashSet<long>();
        private static readonly HashSet<long> Keep = new HashSet<long>();
        private static readonly List<long> Expired = new List<long>();
        private static int _movers;

        /// <summary>Set by the materialiser across <c>AddVoxelMap</c>, while the new rock is not yet in its owned set.</summary>
        [ThreadStatic] internal static bool SpawningPristine;

        private static readonly AccessTools.FieldRef<MyVoxelPhysicsBody, bool> BodiesInitialized =
            Ref("m_bodiesInitialized");

        private static AccessTools.FieldRef<MyVoxelPhysicsBody, bool> Ref(string name)
        {
            try { return AccessTools.FieldRefAccess<MyVoxelPhysicsBody, bool>(name); } catch { return null; }
        }

        private static readonly FieldInfo WorkTrackerF = AccessTools.Field(typeof(MyVoxelPhysicsBody), "m_workTracker");
        private static readonly FieldInfo RunningBatchF = AccessTools.Field(typeof(MyVoxelPhysicsBody), "RunningBatchTask");
        private static readonly MethodInfo CloseRigidBodyM = AccessTools.Method(typeof(MyPhysicsBody), "CloseRigidBody");
        private static MethodInfo _cancelAllM;

        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo target = AccessTools.Method(typeof(MyVoxelPhysicsBody), "CreateRigidBodies");
                if (target == null || BodiesInitialized == null)
                {
                    Log.Error("Rock contact physics: MyVoxelPhysicsBody.CreateRigidBodies or m_bodiesInitialized did not"
                              + " resolve; the switch has no effect.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(RockContactPhysics).GetMethod("Prefix",
                    BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First });
                Log.Info("Rock contact physics: grid gate bound.");
            }
            catch (Exception ex)
            {
                Log.Error("Rock contact physics: could not patch CreateRigidBodies: " + ex.Message);
            }
        }

        private static bool Prefix(MyVoxelPhysicsBody __instance)
        {
            try
            {
                if (BodiesInitialized(__instance)) return true;
                var voxel = __instance.Entity as MyVoxelBase;
                if (voxel == null) return true;
                FeatureRockMaterializer rock = FeatureRockMaterializer.Active;
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

        /// <summary>Once per frame from the runtime tick; scans every <see cref="ScanFrames"/>th frame.</summary>
        internal static void Tick()
        {
            if (++_frame % ScanFrames != 0) return;
            long t0 = Stopwatch.GetTimestamp();
            try { Scan(); }
            catch (Exception ex) { Log.Warn("Rock contact physics scan failed: " + ex.Message); }
            _scanTicks += Stopwatch.GetTimestamp() - t0;
            _scans++;
            Report(t0);
        }

        private static void Scan()
        {
            ScanBoxes();
            foreach (long id in _hot) Tracked.Add(id);
            long r0 = Stopwatch.GetTimestamp();
            ReleaseOutsideBand();
            _tRelease += Stopwatch.GetTimestamp() - r0;
        }

        /// <summary>Releases the grid of tracked rock outside every release box, at most
        /// <see cref="ReleasesPerScan"/> per scan.</summary>
        private static void ReleaseOutsideBand()
        {
            Expired.Clear();
            foreach (long id in Tracked)
                if (!Keep.Contains(id)) { Expired.Add(id); if (Expired.Count >= ReleasesPerScan) break; }
            FeatureRockMaterializer rock = FeatureRockMaterializer.Active;
            foreach (long id in Expired)
            {
                Tracked.Remove(id);
                MyEntity e;
                var voxel = MyEntities.TryGetEntityById(id, out e) ? e as MyVoxelBase : null;
                if (voxel == null || voxel.MarkedForClose || rock == null || !rock.OwnsPristine(voxel)) continue;
                var body = voxel.Physics as MyVoxelPhysicsBody;
                if (body == null || !BodiesInitialized(body)) continue;
                if (TryRelease(body)) _freed++; else _freeFailed++;
            }
        }

        private static bool TryRelease(MyVoxelPhysicsBody body)
        {
            try
            {
                if (WorkTrackerF == null || RunningBatchF == null || CloseRigidBodyM == null) return false;
                if (body.RigidBody2 != null) return false;
                var running = RunningBatchF.GetValue(body) as Array;
                if (running != null)
                    foreach (object job in running) if (job != null) return false;
                object tracker = WorkTrackerF.GetValue(body);
                if (tracker != null)
                {
                    if (_cancelAllM == null) _cancelAllM = AccessTools.Method(tracker.GetType(), "CancelAll");
                    if (_cancelAllM != null) _cancelAllM.Invoke(tracker, null);
                }
                Havok.HkRigidBody rb = body.RigidBody;
                Havok.HkWorld world = body.HavokWorld;
                if (rb != null && world != null && rb.InWorld) world.RemoveRigidBody(rb);
                CloseRigidBodyM.Invoke(body, null);
                BodiesInitialized(body) = false;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Rock contact physics: release failed: " + ex.Message);
                return false;
            }
        }

        private static void OnAdd(MyEntity e) { if (e != null && !(e is MyVoxelBase)) Added.Enqueue(e); }
        private static void OnRemove(MyEntity e) { if (e != null && !(e is MyVoxelBase)) Removed.Enqueue(e); }

        /// <summary>Adds every live top-level entity with non-static physics to <paramref name="into"/>, from the same
        /// event-kept candidate set the scan walks. Game thread only.</summary>
        internal static void CollectMovers(List<MyEntity> into)
        {
            SyncCandidates();
            foreach (MyEntity e in Candidates)
                if (!e.Closed && !e.MarkedForClose && e.Physics != null && !e.Physics.IsStatic) into.Add(e);
        }

        private static void SyncCandidates()
        {
            if (!_subscribed)
            {
                MyEntities.OnEntityAdd += OnAdd;
                MyEntities.OnEntityRemove += OnRemove;
                _subscribed = true;
                foreach (MyEntity e in MyEntities.GetEntities())
                    if (e != null && !(e is MyVoxelBase)) Candidates.Add(e);
            }
            MyEntity x;
            while (Added.TryDequeue(out x)) Candidates.Add(x);
            while (Removed.TryDequeue(out x)) { Candidates.Remove(x); AtRest.Remove(x.EntityId); }
        }

        private static void ScanBoxes()
        {
            FeatureRockMaterializer rock = FeatureRockMaterializer.Active;
            _next.Clear();
            Keep.Clear();
            Boxes.Clear();
            int movers = 0;
            _scanNo++;
            long walk0 = Stopwatch.GetTimestamp(), inner = 0;
            SyncCandidates();
            Dead.Clear();
            foreach (MyEntity e in Candidates)
            {
                _entities++;
                if (e.Closed) { Dead.Add(e); continue; }
                if (e.MarkedForClose || e.Physics == null || e.Physics.IsStatic) continue;
                Vector3D p = e.PositionComp.WorldAABB.Center;
                Vector3 v = e.Physics.LinearVelocity;
                Vec3 min, max;
                RockContact.SweptBox(new Vec3(p.X, p.Y, p.Z), new Vec3(v.X, v.Y, v.Z), ReachM + e.PositionComp.WorldAABB.HalfExtents.Max(),
                    LookaheadS, out min, out max);
                var box = new BoundingBoxD(new Vector3D(min.X, min.Y, min.Z), new Vector3D(max.X, max.Y, max.Z));
                Boxes.Add(box);
                movers++;
                if (rock == null) continue;
                RestCache rest;
                bool atRest = v.LengthSquared() < RestSpeedMs * RestSpeedMs;
                if (atRest && AtRest.TryGetValue(e.EntityId, out rest)
                    && Vector3D.DistanceSquared(rest.Pos, p) < RestMoveM * RestMoveM && _scanNo - rest.Scan < RestRefreshScans)
                {
                    foreach (long id in rest.Keep) Keep.Add(id);
                    foreach (long id in rest.Hot) _next.Add(id);
                    _cachedMovers++;
                    continue;
                }
                BoundingBoxD band = box.GetInflated(ReleaseMarginM);
                Found.Clear();
                long q0 = Stopwatch.GetTimestamp();
                MyGamePruningStructure.GetAllVoxelMapsInBox(ref band, Found);
                long q1 = Stopwatch.GetTimestamp();
                rest = null;
                if (atRest)
                {
                    if (!AtRest.TryGetValue(e.EntityId, out rest)) AtRest[e.EntityId] = rest = new RestCache();
                    rest.Pos = p;
                    rest.Scan = _scanNo;
                    rest.Keep.Clear();
                    rest.Hot.Clear();
                }
                else AtRest.Remove(e.EntityId);
                foreach (MyVoxelBase voxel in Found)
                {
                    if (!rock.OwnsPristine(voxel)) continue;
                    Keep.Add(voxel.EntityId);
                    if (rest != null) rest.Keep.Add(voxel.EntityId);
                    if (box.Intersects(voxel.PositionComp.WorldAABB))
                    {
                        _next.Add(voxel.EntityId);
                        if (rest != null) rest.Hot.Add(voxel.EntityId);
                    }
                }
                long q2 = Stopwatch.GetTimestamp();
                _tQuery += q1 - q0;
                _tTest += q2 - q1;
                inner += q2 - q0;
                _returned += Found.Count;
                if (Found.Count > 0) _rockMovers++;
            }
            foreach (MyEntity d in Dead) { Candidates.Remove(d); AtRest.Remove(d.EntityId); }
            _tEnumerate += Stopwatch.GetTimestamp() - walk0 - inner;
            foreach (long id in _hot) if (!_next.Contains(id)) _released++;
            HashSet<long> swap = _hot;
            _hot = _next;
            _next = swap;
            _movers = movers;
        }

        /// <summary>Per-scan averages: ms walking entities (and the count walked), ms in pruning queries, ms testing the
        /// rock returned (and how many), ms releasing, and movers whose query found rock.</summary>
        private static string SplitText()
        {
            if (_scans == 0) return "-";
            double f = 1000.0 / Stopwatch.Frequency / _scans;
            return "enumerate=" + (_tEnumerate * f).ToString("0.00", CultureInfo.InvariantCulture) + "ms/" + _entities / _scans
                   + " query=" + (_tQuery * f).ToString("0.00", CultureInfo.InvariantCulture) + "ms"
                   + " test=" + (_tTest * f).ToString("0.00", CultureInfo.InvariantCulture) + "ms/" + _returned / _scans
                   + " release=" + (_tRelease * f).ToString("0.00", CultureInfo.InvariantCulture) + "ms"
                   + " rockMovers=" + _rockMovers / _scans + " cachedMovers=" + _cachedMovers / _scans;
        }

        private static void Report(long now)
        {
            if (!DebugTraces.Physics) return;
            if (_windowStart == 0) { _windowStart = now; return; }
            if (now - _windowStart < ReportSeconds * Stopwatch.Frequency) return;
            int withGrid = 0, owned = 0;
            FeatureRockMaterializer rock = FeatureRockMaterializer.Active;
            if (rock != null)
                foreach (FeatureRockMaterializer.LiveRock live in rock.LiveRocks())
                {
                    MyEntity e;
                    var v = MyEntities.TryGetEntityById(live.EntityId, out e) ? e as MyVoxelBase : null;
                    var b = v != null ? v.Physics as MyVoxelPhysicsBody : null;
                    if (b == null) continue;
                    owned++;
                    if (BodiesInitialized(b)) withGrid++;
                }
            Log.Info("ROCK_CONTACT movers=" + _movers + " hot=" + _hot.Count + " deferred=" + _deferred
                     + " cooled=" + _released + " released=" + _freed + " releaseRefused=" + _freeFailed
                     + " withGrid=" + withGrid + "/" + owned + " split:" + SplitText() + " scanMsAvg="
                     + (_scans > 0 ? (_scanTicks * 1000.0 / Stopwatch.Frequency / _scans).ToString("0.00", CultureInfo.InvariantCulture) : "-"));
            _windowStart = now;
            _deferred = _released = _freed = _freeFailed = _scanTicks = _scans = 0;
            _tEnumerate = _tQuery = _tTest = _tRelease = _entities = _returned = _rockMovers = _cachedMovers = 0;
        }
    }
}
