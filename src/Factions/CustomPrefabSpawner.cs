using System.Collections.Generic;
using Sandbox.Game.Entities;
using VRage;
using VRage.Game;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Spawns a player-captured prefab (`!factions ship capture`, stored in <see cref="CustomPrefabStore"/>)
    /// directly from its saved <c>MyObjectBuilder_CubeGrid</c>s via <c>MyEntities
    /// .CreateFromObjectBuilderAndAdd</c> — deliberately bypassing <c>MyPrefabManager.SpawnPrefab</c>/
    /// <c>MyDefinitionManager</c> entirely, since a capture is never registered as a real SE prefab
    /// definition (see <c>VanillaFleetSpawner.DoSpawn</c>'s custom-prefab branch, which calls this
    /// instead of <c>MyPrefabManager.Static.SpawnPrefab</c> for a captured prefab id). This is what
    /// makes a capture immediately spawnable with no session restart.
    /// </summary>
    internal static class CustomPrefabSpawner
    {
        /// <summary>Clones <paramref name="templateGrids"/> (the stored template is never mutated —
        /// the same capture may be spawned many times), translates the whole group so its root grid
        /// lands at <paramref name="spawnPos"/> (relative offsets between mechanically-connected
        /// grids are preserved as captured; orientation is left as-captured, matching how vanilla
        /// fleet spawns don't reorient to face travel direction either — see <c>VanillaFleetSpawner
        /// .DoSpawn</c>'s fixed <c>Vector3.Forward/Up</c>), re-stamps ownership to <paramref
        /// name="ownerId"/> on every block, remaps entity ids to avoid collisions with the live
        /// world, and spawns each grid synchronously.</summary>
        public static bool TrySpawn(MyObjectBuilder_CubeGrid[] templateGrids, Vector3D spawnPos, long ownerId,
            out List<MyCubeGrid> resultGrids)
        {
            resultGrids = null;
            if (templateGrids == null || templateGrids.Length == 0) return false;

            var clones = new MyObjectBuilder_CubeGrid[templateGrids.Length];
            for (int i = 0; i < templateGrids.Length; i++)
                clones[i] = (MyObjectBuilder_CubeGrid)templateGrids[i].Clone();

            Vector3D rootOriginalPos = clones[0].PositionAndOrientation.HasValue
                ? (Vector3D)clones[0].PositionAndOrientation.Value.Position
                : Vector3D.Zero;
            Vector3D offset = spawnPos - rootOriginalPos;

            foreach (MyObjectBuilder_CubeGrid grid in clones)
            {
                if (grid.PositionAndOrientation.HasValue)
                {
                    MyPositionAndOrientation pos = grid.PositionAndOrientation.Value;
                    pos.Position = (Vector3D)pos.Position + offset;
                    grid.PositionAndOrientation = pos;
                }

                if (grid.CubeBlocks != null)
                {
                    foreach (var block in grid.CubeBlocks)
                    {
                        if (block == null) continue;
                        block.Owner = ownerId;
                        block.BuiltBy = ownerId;
                    }
                }
            }

            // Fresh entity ids for every grid/block, avoiding collisions with the live world —
            // MyPrefabManager.GetGridPrefab does the exact same call before handing grids off to
            // its own spawn path.
            MyEntities.RemapObjectBuilderCollection(clones);

            resultGrids = new List<MyCubeGrid>(clones.Length);
            foreach (MyObjectBuilder_CubeGrid grid in clones)
            {
                var entity = MyEntities.CreateFromObjectBuilderAndAdd(grid, true) as MyCubeGrid;
                if (entity != null) resultGrids.Add(entity);
            }

            return resultGrids.Count > 0;
        }

        /// <summary>The station flavour of <see cref="TrySpawn"/>, for a player-scanned design a
        /// node is standing as. Three differences, all of them because a station is not a ship:
        ///
        /// <list type="bullet">
        /// <item><description><b>Orientation comes from the station.</b> The fleet version leaves a
        /// capture pointing wherever it was scanned; a station has to stand on the
        /// <paramref name="up"/>/<paramref name="forward"/> axes the materializer has already
        /// gravity-corrected, so the root grid is placed on exactly the world matrix the vanilla
        /// prefab branch would have used.</description></item>
        /// <item><description><b>The group keeps its shape.</b> Every other grid is placed at its
        /// captured transform relative to the root, so a subgrid that was 12 m off the hull and
        /// rolled 30 degrees still is. A design's Store, Contract or Services block may sit on any
        /// of them, so losing one loses the station's ability to trade.</description></item>
        /// <item><description><b>Static, and at rest.</b> <c>IsStatic</c> on every grid, velocities
        /// zeroed: the design was scanned static and a station that drifts is not one.</description></item>
        /// </list>
        ///
        /// <para>Like <see cref="TrySpawn"/> the template is cloned rather than mutated, ownership is
        /// re-stamped, and ids are remapped before the grids reach the world.</para></summary>
        public static bool TrySpawnStatic(MyObjectBuilder_CubeGrid[] templateGrids, Vector3D spawnPos,
            Vector3 up, Vector3 forward, long ownerId, out List<MyCubeGrid> resultGrids)
        {
            resultGrids = null;
            MyObjectBuilder_CubeGrid[] clones = PrepareStatic(templateGrids, spawnPos, up, forward, ownerId);
            if (clones == null) return false;

            MyEntities.RemapObjectBuilderCollection(clones);

            resultGrids = new List<MyCubeGrid>(clones.Length);
            foreach (MyObjectBuilder_CubeGrid grid in clones)
            {
                var entity = MyEntities.CreateFromObjectBuilderAndAdd(grid, true) as MyCubeGrid;
                if (entity != null) resultGrids.Add(entity);
            }

            return resultGrids.Count > 0;
        }

        /// <summary><see cref="TrySpawnStatic"/>'s placement without the spawn: the design cloned, placed
        /// on the station's axes, made static and re-owned, ids not yet remapped. Null when there is no
        /// design. <see cref="GridAssembler"/> adds the result in chunks.</summary>
        public static MyObjectBuilder_CubeGrid[] PrepareStatic(MyObjectBuilder_CubeGrid[] templateGrids, Vector3D spawnPos,
            Vector3 up, Vector3 forward, long ownerId)
        {
            if (templateGrids == null || templateGrids.Length == 0) return null;

            var clones = new MyObjectBuilder_CubeGrid[templateGrids.Length];
            for (int i = 0; i < templateGrids.Length; i++)
                clones[i] = (MyObjectBuilder_CubeGrid)templateGrids[i].Clone();

            // Where the root stood when it was scanned, and where it is to stand now. Composing the
            // inverse of the first with the second maps every captured transform into the new frame,
            // which is what keeps the group rigid instead of merely co-located.
            MatrixD capturedRoot = clones[0].PositionAndOrientation.HasValue
                ? clones[0].PositionAndOrientation.Value.GetMatrix()
                : MatrixD.Identity;
            MatrixD intoRootFrame = MatrixD.Invert(capturedRoot);
            MatrixD target = MatrixD.CreateWorld(spawnPos, (Vector3D)forward, (Vector3D)up);

            foreach (MyObjectBuilder_CubeGrid grid in clones)
            {
                MatrixD world = target;
                if (grid.PositionAndOrientation.HasValue)
                    world = grid.PositionAndOrientation.Value.GetMatrix() * intoRootFrame * target;
                grid.PositionAndOrientation = new MyPositionAndOrientation(world);

                grid.IsStatic = true;
                grid.LinearVelocity = Vector3.Zero;
                grid.AngularVelocity = Vector3.Zero;

                if (grid.CubeBlocks != null)
                {
                    foreach (var block in grid.CubeBlocks)
                    {
                        if (block == null) continue;
                        block.Owner = ownerId;
                        block.BuiltBy = ownerId;
                    }
                }
            }

            return clones;
        }

        /// <summary>The root grid's own local bounding box in metres, solved from its block
        /// coordinates. Stands in for <c>MyPlanetSurfacePlacement.ComputePrefabBoundingBox</c>, which
        /// can only answer for a registered SE prefab definition and a capture is never one.
        ///
        /// <para>Measured on the root alone and in the same grid-local frame
        /// <see cref="TrySpawnStatic"/> maps onto the station's axes, so local Y is the axis that
        /// becomes world up — which is exactly what the surface-embed solver reads it for. The block
        /// grid stores each block's minimum cell, so the far side is extended by one cell rather than
        /// measured: an estimate, and the readers of a box already have a fallback for having none at
        /// all.</para>
        ///
        /// <para>Null when the design has no blocks to measure.</para></summary>
        public static BoundingBox? LocalBoundingBox(MyObjectBuilder_CubeGrid[] grids)
        {
            if (grids == null || grids.Length == 0) return null;
            MyObjectBuilder_CubeGrid root = grids[0];
            if (root == null || root.CubeBlocks == null || root.CubeBlocks.Count == 0) return null;

            Vector3I min = Vector3I.MaxValue;
            Vector3I max = Vector3I.MinValue;
            int counted = 0;
            foreach (var block in root.CubeBlocks)
            {
                if (block == null) continue;
                Vector3I cell = block.Min;
                min = Vector3I.Min(min, cell);
                max = Vector3I.Max(max, cell);
                counted++;
            }
            if (counted == 0) return null;

            float cellM = root.GridSizeEnum == MyCubeSize.Small ? 0.5f : 2.5f;
            return new BoundingBox(new Vector3(min) * cellM, (new Vector3(max) + Vector3.One) * cellM);
        }
    }
}
