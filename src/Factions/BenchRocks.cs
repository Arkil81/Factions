using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Voxels;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Test asteroids of a chosen size, for measuring what one costs Havok at sizes the ring does not author
    /// (FAC-445: ~630 KB per map at 64 and 128 m). Placed in a lattice 6 km above a node, inside the physics
    /// cluster of a phantom parked there, so each gets its collision grid at once and
    /// <c>HAVOK_VOXEL_GRIDS</c> reports it under its size. <c>Save = false</c>: never written to the world.
    /// <c>factions bench rock spawn &lt;count&gt; &lt;sizeM&gt; &lt;node&gt;</c>, <c>factions bench rock clear</c>.
    /// </summary>
    internal static class BenchRocks
    {
        private const string Usage = "bench rock spawn <count> <sizeM> <node> | bench rock clear";
        private static readonly List<long> Spawned = new List<long>();

        private static readonly MethodInfo CreateAsteroidShapeM =
            AccessTools.Method(AccessTools.TypeByName("Sandbox.Game.World.Generator.MyCompositeShapeProvider"),
                "CreateAsteroidShape", new[] { typeof(int), typeof(float), typeof(int), typeof(int?) });

        internal static string Handle(string[] t)
        {
            string sub = t.Length > 3 ? t[3].ToLowerInvariant() : "";
            if (sub == "clear") return Clear();
            if (sub != "spawn" || t.Length < 7) return Usage;
            int count, size;
            if (!int.TryParse(t[4], out count) || count < 1 || count > 200) return "bench rock: count 1-200.";
            if (!int.TryParse(t[5], out size) || size < 32 || size > 2048) return "bench rock: sizeM 32-2048.";
            FactionsPlugin plugin = FactionsPlugin.Instance;
            FactionRuntime rt = plugin != null ? plugin.Runtime : null;
            if (rt == null || rt.BenchWorld == null || MySession.Static == null) return "bench rock: no session loaded.";
            SpatialNode node;
            if (!rt.BenchWorld.Graph.Nodes.TryGetValue(t[6], out node)) return "bench rock: no node '" + t[6] + "'.";
            if (CreateAsteroidShapeM == null) return "bench rock: MyCompositeShapeProvider.CreateAsteroidShape did not resolve.";

            int side = (int)Math.Ceiling(Math.Sqrt(count));
            double spacingM = Math.Max(1000.0, size * 3.0);
            var origin = new Vector3D(node.Position.X * 1000.0, node.Position.Y * 1000.0 + 6000.0, node.Position.Z * 1000.0);
            int made = 0;
            for (int i = 0; i < count; i++)
            {
                var centre = origin + new Vector3D((i % side - side / 2.0) * spacingM, 0, (i / side - side / 2.0) * spacingM);
                var provider = CreateAsteroidShapeM.Invoke(null, new object[] { 9100 + i, (float)size, 0, (int?)4 })
                    as IMyStorageDataProvider;
                if (provider == null) continue;
                var storage = new MyOctreeStorage(provider, new Vector3I(size));
                Vector3D minCorner = centre - new Vector3D(MathHelper.GetNearestBiggerPowerOfTwo((float)size) / 2.0);
                MyVoxelBase voxel = MyWorldGenerator.AddVoxelMap("bench-rock-" + size + "-" + i, storage, minCorner);
                if (voxel == null) continue;
                voxel.Save = false;
                Spawned.Add(voxel.EntityId);
                made++;
            }
            FactionsPlugin.Log.Info("Bench: rocks spawned " + made + " size=" + size + "m node=" + node.Id);
            return "bench rock: spawned " + made + " of " + count + " at " + size + " m, 6 km above " + node.Id
                   + ". HAVOK_VOXEL_GRIDS reports them once a cluster covers them (park a phantom at " + node.Id + ").";
        }

        private static string Clear()
        {
            int closed = 0;
            foreach (long id in Spawned)
            {
                MyEntity e;
                if (MyEntities.TryGetEntityById(id, out e) && !e.MarkedForClose) { e.Close(); closed++; }
            }
            Spawned.Clear();
            return "bench rock: closed " + closed + ".";
        }
    }
}
