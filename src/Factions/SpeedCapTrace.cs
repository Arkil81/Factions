using System;
using System.Collections.Generic;
using System.Globalization;
using NLog;
using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;
using VRageMath;
using VRageMath.Spatial;

namespace Factions
{
    /// <summary>
    /// Grids running at the world speed cap (FAC-445). In every multi-area physics stall the fastest bodies ran
    /// at exactly the cap: 1,000 m/s (the small-grid cap) on 2026-10-05 13:30:41 box-local, 100 m/s at 13:37:25
    /// with the cap set to 100. A grid an asteroid has swallowed is pushed out at whatever speed the cap
    /// allows, so this records, for a grid at 95% of its cap or more: its size, how long it has been in the
    /// world, how many asteroids its bounds overlap and whether any corner of its box lies inside one.
    ///
    /// <para>Once a second from the physics step; one line per grid per <see cref="RepeatSeconds"/>. Under
    /// <c>factions debug physics</c>.</para>
    /// </summary>
    internal static class SpeedCapTrace
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private const double CapShare = 0.95;
        private const double RepeatSeconds = 10;

        private static int _steps;
        private static readonly Dictionary<long, DateTime> FirstSeen = new Dictionary<long, DateTime>();
        private static readonly Dictionary<long, DateTime> LastLogged = new Dictionary<long, DateTime>();
        private static readonly List<MyVoxelBase> Voxels = new List<MyVoxelBase>();

        internal static void AfterStep()
        {
            if (!DebugTraces.Physics || ++_steps < 60) return;
            _steps = 0;
            try { Sample(DateTime.UtcNow); }
            catch (Exception ex) { Log.Warn("SPEED_CAP trace failed: " + ex.Message); }
        }

        private static void Sample(DateTime now)
        {
            float largeCap = MyGridPhysics.LargeShipMaxLinearVelocity();
            float smallCap = MyGridPhysics.SmallShipMaxLinearVelocity();
            var seen = new HashSet<long>();
            foreach (MyEntity e in MyEntities.GetEntities())
            {
                var grid = e as MyCubeGrid;
                if (grid == null || grid.Physics == null || grid.IsStatic || grid.MarkedForClose) continue;
                seen.Add(grid.EntityId);
                if (!FirstSeen.ContainsKey(grid.EntityId)) FirstSeen[grid.EntityId] = now;

                float cap = grid.GridSizeEnum == VRage.Game.MyCubeSize.Large ? largeCap : smallCap;
                float speed = grid.Physics.LinearVelocity.Length();
                if (cap <= 0 || speed < cap * CapShare) continue;
                DateTime last;
                if (LastLogged.TryGetValue(grid.EntityId, out last) && (now - last).TotalSeconds < RepeatSeconds) continue;
                LastLogged[grid.EntityId] = now;

                BoundingBoxD box = grid.PositionComp.WorldAABB;
                Voxels.Clear();
                MyGamePruningStructure.GetAllVoxelMapsInBox(ref box, Voxels);
                int inside = 0;
                MatrixD wm = grid.WorldMatrix;
                foreach (MyVoxelBase v in Voxels)
                    if (v != null && !v.MarkedForClose && v.IsAnyAabbCornerInside(ref wm, grid.PositionComp.LocalAABB)) inside++;

                Vector3D p = grid.PositionComp.GetPosition();
                string cl = "?";
                try
                {
                    MyClusterTree.MyCluster c = Sandbox.Engine.Physics.MyPhysics.Clusters.GetClusterForPosition(p);
                    var w = c == null ? null : c.UserData as Havok.HkWorld;
                    if (w != null)
                        cl = "chars=" + (w.CharacterRigidBodies != null ? w.CharacterRigidBodies.Count : 0) + " stepped=" + PhysicsStallTrace.Stepped(c, w);
                }
                catch { }
                Log.Info("SPEED_CAP eid=" + grid.EntityId + " name=\"" + grid.DisplayName + "\" blocks=" + grid.BlocksCount
                         + " size=" + grid.GridSizeEnum + " speed=" + speed.ToString("0", CultureInfo.InvariantCulture)
                         + "/" + cap.ToString("0", CultureInfo.InvariantCulture)
                         + " ageS=" + (now - FirstSeen[grid.EntityId]).TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                         + " voxelsOverlapped=" + Voxels.Count + " voxelsCornerInside=" + inside + " cluster:" + cl
                         + " km=(" + (p.X / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "," + (p.Y / 1000).ToString("0.0", CultureInfo.InvariantCulture)
                         + "," + (p.Z / 1000).ToString("0.0", CultureInfo.InvariantCulture) + ")");
            }
            if (FirstSeen.Count > seen.Count + 500)
            {
                var gone = new List<long>();
                foreach (long id in FirstSeen.Keys) if (!seen.Contains(id)) gone.Add(id);
                foreach (long id in gone) { FirstSeen.Remove(id); LastLogged.Remove(id); }
            }
        }
    }
}
