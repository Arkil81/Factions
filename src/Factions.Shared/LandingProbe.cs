using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Sandbox.Engine.Physics;           // MyPhysics, GetHitEntity
using Sandbox.Game.Entities;            // MyCubeGrid, MyVoxelBase, MyPlanet
using Sandbox.Game.GameSystems;         // MyGravityProviderSystem
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.Entity;                // MyEntity
using VRage.Voxels;                     // MyStorageData, MyVoxelCoordSystems
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Why a ship landed on a planet looks sunk into the ground until its gear is released, and then
    /// jumps up (<c>LANDING_PROBE</c>; server <c>factions debug landing</c>, client "Log diagnostics").
    ///
    /// <para>While a gear holds a grid to a voxel, the client shows exactly the server's pose: on lock
    /// <c>MyLandingGear.EntityReattach</c> snaps the client grid to the server's relative pose, the
    /// fixed constraint exists only on the server, and <c>MyFixedGrids.MarkGridRoot</c> makes the
    /// client body Fixed and unpredicted. Only on release (<c>Detach</c> →
    /// <c>UnmarkGridRoot</c>) does the client simulate the grid against its own voxels. So the sinking
    /// is one of two things, and these lines separate them:</para>
    /// <list type="number">
    /// <item>the server's pose really is inside the server's terrain (Havok penetration at touchdown,
    /// frozen by the stiff lock): the server's <c>ray=</c> is already short or negative at lock, and
    /// the server's own grid moves up after release;</item>
    /// <item>the two machines' terrain differs there (explosion craters are cut locally on each
    /// machine): the two sides' <c>fnv=</c> differ for the same <c>box=</c>, the client's
    /// <c>ray=</c> at reattach is short where the server's is not, and only the client moves after
    /// release.</item>
    /// </list>
    ///
    /// <para>Shared source because the two ends must measure identically: the same lock pivot, the
    /// same downward ray on the voxel layer, the same snapped box and the same checksum
    /// (<see cref="LandingProbeMath"/>). Each host supplies its gate, its log sink and its sample
    /// schedule. Game thread only.</para>
    /// </summary>
    public static class LandingProbe
    {
        /// <summary>Half-length of the vertical ray through the pivot, metres. Long enough to find a
        /// surface the pivot is buried under, short enough to stay on this hill.</summary>
        private const double RayReach = 25.0;

        private static Func<bool> _enabled;
        private static Action<string> _sink;
        private static string _side = "?";
        private static double[] _sampleSeconds = new double[0];

        /// <summary>Which hooks bound, for the host's boot line.</summary>
        public static string BindReport { get; private set; }

        /// <summary>Every hook bound. A host reports <see cref="BindReport"/> only when this is false.</summary>
        public static bool AllBound { get; private set; }

        private sealed class Pending
        {
            public MyCubeGrid Grid;
            public MyLandingGear Gear;
            public MyVoxelBase Voxel;
            public Vector3D ReleasePos;
            public Vector3D Up;
            public long StartTicks;
            public int Next;
        }

        private static readonly List<Pending> Samples = new List<Pending>();
        private static readonly List<MyPhysics.HitInfo> Hits = new List<MyPhysics.HitInfo>();
        private static MyStorageData _data;

        /// <summary>Binds the hooks. Each is wrapped on its own, so a miss costs that event only and
        /// never the host.</summary>
        /// <param name="side">"server" or "client" — the first token after the line name.</param>
        /// <param name="enabled">The host's gate, read at every hook before any work.</param>
        /// <param name="sink">Where a finished line goes.</param>
        /// <param name="sampleSeconds">When, after the last gear on a grid releases from a voxel, to
        /// log the grid's position again.</param>
        public static void TryPatch(Harmony harmony, string side, Func<bool> enabled, Action<string> sink,
                                    double[] sampleSeconds)
        {
            _side = side ?? "?";
            _enabled = enabled;
            _sink = sink;
            _sampleSeconds = sampleSeconds ?? new double[0];
            if (harmony == null) { BindReport = "no harmony"; return; }

            var parts = new List<string>();
            parts.Add(Bind(harmony, "AttachEntity", "AttachPostfix", null));
            parts.Add(Bind(harmony, "EntityReattach", "ReattachPostfix", "ReattachPrefix"));
            parts.Add(Bind(harmony, "Detach", "DetachPostfix", "DetachPrefix"));
            BindReport = string.Join(", ", parts.ToArray());
            AllBound = true;
            foreach (string p in parts) if (!p.EndsWith(" bound", StringComparison.Ordinal)) AllBound = false;
        }

        private static string Bind(Harmony harmony, string method, string postfix, string prefix)
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(MyLandingGear), method);
                if (target == null) return method + " not found";
                HarmonyMethod pre = prefix == null ? null
                    : new HarmonyMethod(typeof(LandingProbe).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static));
                HarmonyMethod post = new HarmonyMethod(typeof(LandingProbe).GetMethod(postfix, BindingFlags.NonPublic | BindingFlags.Static));
                harmony.Patch(target, prefix: pre, postfix: post);
                return method + " bound";
            }
            catch (Exception ex)
            {
                return method + " failed (" + ex.Message + ")";
            }
        }

        private static bool On
        {
            get
            {
                Func<bool> e = _enabled;
                if (e == null || _sink == null) return false;
                try { return e(); } catch { return false; }
            }
        }

        // --- hooks -------------------------------------------------------------------------------

        /// <summary>The server's lock (and any local lock a client makes itself).</summary>
        private static void AttachPostfix(MyLandingGear __instance, MyEntity otherEntity)
        {
            if (!On) return;
            try
            {
                var voxel = otherEntity as MyVoxelBase;
                if (voxel == null || !IsAttachedTo(__instance, voxel)) return;
                CancelSamples(__instance.CubeGrid);
                Emit(Line("lock", __instance, voxel, null));
            }
            catch (Exception ex) { Emit("LANDING_PROBE side=" + _side + " event=lock error=" + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void ReattachPrefix(MyLandingGear __instance, out Vector3D __state)
        {
            __state = Vector3D.Zero;
            if (!On) return;
            try { if (__instance.CubeGrid != null) __state = __instance.CubeGrid.PositionComp.GetPosition(); }
            catch { }
        }

        /// <summary>The client's lock: the grid has just been snapped to the server's relative pose.
        /// <c>snap_up=</c> is how far that snap moved the grid along up — negative is the client
        /// grid being pulled down to where the server holds it.</summary>
        private static void ReattachPostfix(MyLandingGear __instance, MyEntity otherEntity, Vector3D __state)
        {
            if (!On) return;
            try
            {
                var voxel = otherEntity as MyVoxelBase;
                if (voxel == null) return;
                CancelSamples(__instance.CubeGrid);
                string snap = null;
                if (__state != Vector3D.Zero)
                {
                    Vector3D pivot; float half;
                    Pivot(__instance, out pivot, out half);
                    Vector3D up;
                    if (TryUp(voxel, pivot, out up))
                        snap = F(Vector3D.Dot(__instance.CubeGrid.PositionComp.GetPosition() - __state, up));
                }
                Emit(Line("reattach", __instance, voxel, snap == null ? null : " snap_up=" + snap));
            }
            catch (Exception ex) { Emit("LANDING_PROBE side=" + _side + " event=reattach error=" + ex.GetType().Name + ": " + ex.Message); }
        }

        private static void DetachPrefix(MyLandingGear __instance, out MyVoxelBase __state)
        {
            __state = null;
            if (!On) return;
            try { __state = Attached(__instance) as MyVoxelBase; }
            catch { }
        }

        /// <summary>Release from a voxel, on either side. The grid only becomes dynamic again when no
        /// other gear still holds it to a voxel (<c>root=released</c>); only then are the follow-up
        /// samples scheduled.</summary>
        private static void DetachPostfix(MyLandingGear __instance, MyVoxelBase __state)
        {
            if (__state == null || !On) return;
            try
            {
                if (Attached(__instance) != null) return; // Detach refused; still attached
                MyCubeGrid grid = __instance.CubeGrid;
                int held = 0;
                foreach (MyLandingGear other in grid.GetFatBlocks<MyLandingGear>())
                    if (!ReferenceEquals(other, __instance) && Attached(other) is MyVoxelBase) held++;
                Emit(Line("release", __instance, __state,
                    " held_by=" + held + " root=" + (held == 0 ? "released" : "held")));

                if (held != 0 || _sampleSeconds.Length == 0) return;
                Vector3D pivot; float half;
                Pivot(__instance, out pivot, out half);
                Vector3D up;
                if (!TryUp(__state, pivot, out up)) up = Vector3D.Zero;
                CancelSamples(grid);
                Samples.Add(new Pending
                {
                    Grid = grid, Gear = __instance, Voxel = __state,
                    ReleasePos = grid.PositionComp.GetPosition(), Up = up,
                    StartTicks = Stopwatch.GetTimestamp(), Next = 0,
                });
            }
            catch (Exception ex) { Emit("LANDING_PROBE side=" + _side + " event=release error=" + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>Per frame from each host. Free when nothing was released recently.</summary>
        public static void Tick()
        {
            if (Samples.Count == 0) return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                for (int i = Samples.Count - 1; i >= 0; i--)
                {
                    Pending p = Samples[i];
                    if (p.Grid == null || p.Grid.MarkedForClose || p.Gear.MarkedForClose || p.Next >= _sampleSeconds.Length)
                    {
                        Samples.RemoveAt(i);
                        continue;
                    }
                    double elapsed = (now - p.StartTicks) / (double)Stopwatch.Frequency;
                    if (elapsed < _sampleSeconds[p.Next]) continue;
                    p.Next++;
                    if (On) Emit(AfterLine(p, elapsed));
                    if (p.Next >= _sampleSeconds.Length) Samples.RemoveAt(i);
                }
            }
            catch (Exception ex)
            {
                Samples.Clear();
                Emit("LANDING_PROBE side=" + _side + " event=after error=" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // --- measurement -------------------------------------------------------------------------

        private static string Line(string evt, MyLandingGear gear, MyVoxelBase voxel, string tail)
        {
            MyCubeGrid grid = gear.CubeGrid;
            Vector3D pivot; float half;
            Pivot(gear, out pivot, out half);
            var sb = new StringBuilder(320);
            sb.Append("LANDING_PROBE side=").Append(_side).Append(" event=").Append(evt)
              .Append(" grid=").Append(grid.EntityId)
              .Append(" name=\"").Append(grid.DisplayName).Append('"')
              .Append(" gear=").Append(gear.EntityId)
              .Append(" voxel=").Append(voxel.RootVoxel != null ? voxel.RootVoxel.StorageName : voxel.StorageName)
              .Append(" pos=").Append(V(grid.PositionComp.GetPosition()))
              .Append(" pivot=").Append(V(pivot))
              .Append(" lock_half=").Append(F(half))
              .Append(" vel=").Append(grid.Physics != null ? F(grid.Physics.LinearVelocity.Length()) : "n/a")
              .Append(" predicted=").Append(grid.IsClientPredicted ? "true" : "false");
            AppendGround(sb, voxel, pivot);
            AppendContent(sb, voxel, pivot);
            if (tail != null) sb.Append(tail);
            return sb.ToString();
        }

        private static string AfterLine(Pending p, double elapsed)
        {
            MyCubeGrid grid = p.Grid;
            Vector3D pos = grid.PositionComp.GetPosition();
            Vector3D pivot; float half;
            Pivot(p.Gear, out pivot, out half);
            var sb = new StringBuilder(256);
            sb.Append("LANDING_PROBE side=").Append(_side).Append(" event=after t=").Append(F(elapsed))
              .Append(" grid=").Append(grid.EntityId)
              .Append(" gear=").Append(p.Gear.EntityId)
              .Append(" pos=").Append(V(pos))
              .Append(" moved=").Append(F((pos - p.ReleasePos).Length()))
              .Append(" moved_up=").Append(p.Up == Vector3D.Zero ? "n/a" : F(Vector3D.Dot(pos - p.ReleasePos, p.Up)))
              .Append(" vel=").Append(grid.Physics != null ? F(grid.Physics.LinearVelocity.Length()) : "n/a")
              .Append(" predicted=").Append(grid.IsClientPredicted ? "true" : "false");
            AppendGround(sb, p.Voxel, pivot);
            return sb.ToString();
        }

        /// <summary><c>ray=</c>: signed metres from the gear's lock pivot down to the first voxel
        /// surface a Havok ray on <c>VoxelCollisionLayer</c> meets, searched from
        /// <see cref="RayReach"/> above to as far below; negative means the surface is above the
        /// pivot. This is the terrain the machine's own physics collides with, craters included.
        /// <c>surf=</c>: the same height against the planet's analytic surface
        /// (<c>GetClosestSurfacePointGlobal</c>), which knows nothing of craters.</summary>
        private static void AppendGround(StringBuilder sb, MyVoxelBase voxel, Vector3D pivot)
        {
            Vector3D up;
            if (!TryUp(voxel, pivot, out up))
            {
                sb.Append(" ray=n/a surf=n/a");
                return;
            }
            MyVoxelBase root = voxel.RootVoxel ?? voxel;
            sb.Append(" ray=");
            if (MyPhysics.InsideSimulation) sb.Append("insim");
            else
            {
                Vector3D from = pivot + up * RayReach;
                Hits.Clear();
                MyPhysics.CastRay(from, pivot - up * RayReach, Hits, MyPhysics.CollisionLayers.VoxelCollisionLayer);
                double best = double.MaxValue;
                Vector3D hitPos = Vector3D.Zero;
                foreach (MyPhysics.HitInfo hit in Hits)
                {
                    var hv = hit.HkHitInfo.GetHitEntity() as MyVoxelBase;
                    if (hv == null || !ReferenceEquals(hv.RootVoxel ?? hv, root)) continue;
                    double d = Vector3D.DistanceSquared(from, hit.Position);
                    if (d < best) { best = d; hitPos = hit.Position; }
                }
                Hits.Clear();
                sb.Append(best == double.MaxValue ? "none" : F(Vector3D.Dot(pivot - hitPos, up)));
            }
            var planet = root as MyPlanet;
            sb.Append(" surf=");
            if (planet == null) sb.Append("n/a");
            else
            {
                Vector3D centre = planet.PositionComp.GetPosition();
                Vector3D surface = planet.GetClosestSurfacePointGlobal(ref pivot);
                sb.Append(F((pivot - centre).Length() - (surface - centre).Length()));
            }
        }

        /// <summary><c>box=</c> the snapped voxel box's minimum corner in storage coordinates (it is
        /// <see cref="LandingProbeMath.Span"/> on a side); <c>fnv=</c> and <c>sum=</c> its LOD0
        /// content as this machine's storage holds it. Equal boxes with unequal checksums are
        /// terrain the two machines disagree about.</summary>
        private static void AppendContent(StringBuilder sb, MyVoxelBase voxel, Vector3D pivot)
        {
            MyVoxelBase root = voxel.RootVoxel ?? voxel;
            if (root.Storage == null) { sb.Append(" box=n/a"); return; }
            Vector3I vc;
            MyVoxelCoordSystems.WorldPositionToVoxelCoord(root.PositionLeftBottomCorner, ref pivot, out vc);
            vc += root.StorageMin;
            var min = new Vector3I(LandingProbeMath.BoxMin(vc.X), LandingProbeMath.BoxMin(vc.Y), LandingProbeMath.BoxMin(vc.Z));
            var max = min + (LandingProbeMath.Span - 1);
            if (_data == null) _data = new MyStorageData(MyStorageDataTypeFlags.Content);
            _data.Resize(min, max);
            root.Storage.ReadRange(_data, MyStorageDataTypeFlags.Content, 0, min, max);
            byte[] content = _data[MyStorageDataTypeEnum.Content];
            int n = _data.SizeLinear;
            sb.Append(" box=").Append(min.X).Append(',').Append(min.Y).Append(',').Append(min.Z)
              .Append(" fnv=").Append(LandingProbeMath.Fnv1a(content, n).ToString("x8", CultureInfo.InvariantCulture))
              .Append(" sum=").Append(LandingProbeMath.Sum(content, n));
        }

        /// <summary>The centre of the gear's first lock box in world space — the point FindBody
        /// tests for contact — and that box's half-height. The gear's own position when the model
        /// has no lock dummy.</summary>
        private static void Pivot(MyLandingGear gear, out Vector3D pivot, out float half)
        {
            Matrix[] locks = gear.LockPositions;
            if (locks != null && locks.Length > 0)
            {
                Vector3 halfExtents; Quaternion orientation;
                gear.GetBoxFromMatrix(locks[0], out halfExtents, out pivot, out orientation);
                half = halfExtents.Y;
                return;
            }
            pivot = gear.PositionComp.GetPosition();
            half = 0f;
        }

        /// <summary>Up at <paramref name="at"/>: against natural gravity, else radial from a planet.</summary>
        private static bool TryUp(MyVoxelBase voxel, Vector3D at, out Vector3D up)
        {
            Vector3 g = MyGravityProviderSystem.CalculateNaturalGravityInPoint(at);
            if (g.LengthSquared() > 1e-6f)
            {
                up = -Vector3D.Normalize((Vector3D)g);
                return true;
            }
            var planet = (voxel.RootVoxel ?? voxel) as MyPlanet;
            if (planet != null)
            {
                Vector3D r = at - planet.PositionComp.GetPosition();
                if (r.LengthSquared() > 1e-6)
                {
                    up = Vector3D.Normalize(r);
                    return true;
                }
            }
            up = Vector3D.Zero;
            return false;
        }

        private static VRage.ModAPI.IMyEntity Attached(MyLandingGear gear)
        {
            return ((SpaceEngineers.Game.ModAPI.IMyLandingGear)gear).GetAttachedEntity();
        }

        private static bool IsAttachedTo(MyLandingGear gear, MyVoxelBase voxel)
        {
            return ReferenceEquals(Attached(gear), voxel);
        }

        private static void CancelSamples(MyCubeGrid grid)
        {
            for (int i = Samples.Count - 1; i >= 0; i--)
                if (ReferenceEquals(Samples[i].Grid, grid)) Samples.RemoveAt(i);
        }

        private static void Emit(string line)
        {
            Action<string> s = _sink;
            if (s != null) s(line);
        }

        private static string F(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        private static string V(Vector3D v)
        {
            return v.X.ToString("0.000", CultureInfo.InvariantCulture) + ","
                 + v.Y.ToString("0.000", CultureInfo.InvariantCulture) + ","
                 + v.Z.ToString("0.000", CultureInfo.InvariantCulture);
        }
    }
}
