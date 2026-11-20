using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;                       // AccessTools
using Sandbox.Definitions;              // MyDefinitionManager
using Sandbox.Engine.Voxels;            // MyStorageBase, MyOctreeStorage
using Sandbox.Game.Entities;            // MyEntities
using Sandbox.Game.World;               // MySession, MyWorldGenerator
using VRage.Game;                       // MyVoxelMaterialDefinition
using VRage.Voxels;                     // IMyStorageDataProvider
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Spawns physical SE voxel-map asteroids near Factions nodes whose deposit earns
    /// a nonzero <see cref="FactionTuning.AsteroidField"/> profile (any node with a deposit,
    /// RareOreField by default, lightly Refinery) AND whose <see cref="SpatialNode.Zone"/> is
    /// <see cref="NodeZone.DeepSpace"/> -- Surface/Orbit nodes sit near a planet, where a
    /// materialized field risks obstructing terrain, vanilla asteroid belts, or a station's
    /// approach (see <see cref="AsteroidFieldPlanner.Plan"/>). Unlike <see cref="StationMaterializer"/>
    /// this is a spawn-once lifecycle: a voxel map carries none of a live station grid's simulation
    /// cost (no blocks, no safe zone), so there is no despawn/grace-timer cycle -- once
    /// <see cref="SpatialNode.AsteroidFieldSeed"/> is set, this node is never revisited.
    ///
    /// A node that already has a founded station (<see cref="SpatialNode.StableStationId"/> != 0)
    /// still gets its asteroid field -- a resource node's identity doesn't stop being an OreField
    /// just because it grew a station -- but individual asteroid placements are kept clear of the
    /// station's safe zone (<see cref="StationMaterializer.SafeZoneRadius"/> plus margin) so a rock
    /// never spawns on top of it.
    ///
    /// Shape reuse: vanilla's own irregular-asteroid shape generator
    /// (<c>Sandbox.Game.World.Generator.MyCompositeShapeProvider.CreateAsteroidShape</c>) is a public
    /// static method, but its containing class is internal, so it is only reachable via reflection
    /// from this assembly -- same idiom <see cref="StationMaterializer"/> already uses for
    /// SpawnPrefabInternal/SetPosition. Everything downstream of that one call
    /// (<see cref="MyOctreeStorage"/>, <see cref="MyStorageBase.OverwriteAllMaterials(MyVoxelMaterialDefinition)"/>,
    /// <see cref="MyWorldGenerator.AddVoxelMap(string,MyStorageBase,Vector3D,long)"/>) is public API.
    /// We take vanilla's natural shape and repaint it with our own chosen material -- vanilla's own
    /// per-asteroid material selection (<c>MyCompositeShapes</c>) is never touched.
    /// </summary>
    public sealed class AsteroidFieldMaterializer
    {
        private static readonly Type CompositeShapeProviderType =
            AccessTools.TypeByName("Sandbox.Game.World.Generator.MyCompositeShapeProvider");
        private static readonly MethodInfo CreateAsteroidShapeM =
            CompositeShapeProviderType != null
                ? AccessTools.Method(CompositeShapeProviderType, "CreateAsteroidShape",
                    new[] { typeof(int), typeof(float), typeof(int), typeof(int?) })
                : null;

        // Lazily built: MinedOre name ("Iron", "Ice", ...) -> candidate material definitions that
        // actually spawn in asteroids. Several definitions can share a MinedOre (e.g. Iron_01/Iron_02).
        private static Dictionary<string, List<MyVoxelMaterialDefinition>> _materialsByOreName;

        /// <summary>Per-tick entry point, called alongside <see cref="StationMaterializer.UpdateObservation"/>.
        /// No-op when no session is loaded or the reflected shape factory is unavailable.</summary>
        public void UpdateObservation(
            SpatialGraph graph, IList<SpatialNode> nodesNearPlayers, FactionTuning tuning)
        {
            if (graph == null || tuning == null || MySession.Static == null) return;
            if (CreateAsteroidShapeM == null) return; // reflected factory missing (game update?) -- fail closed

            int spawnedThisTick = 0;
            int budget = Math.Max(0, tuning.MaxAsteroidMaterializationsPerTick);
            if (budget == 0) return;

            // Only nodes an online player has actually reached, resolved by the caller through
            // NodeProximityIndex. This loop used to walk every graph node on every Update() frame and
            // reject almost all of them on CouldHaveField -- the cheapest possible per-node work, but
            // performed ~740 times a frame to reach a verdict that needs a player to change. With
            // nobody near anything the list is empty and this costs nothing.
            int count = nodesNearPlayers == null ? 0 : nodesNearPlayers.Count;
            for (int i = 0; i < count; i++)
            {
                if (spawnedThisTick >= budget) break;
                SpatialNode node = nodesNearPlayers[i];
                if (node == null) continue;
                if (node.AsteroidFieldSeed != 0) continue;           // already materialized

                // Filter order is still load-bearing among what is left: CouldHaveField is a plain
                // enum compare plus one dictionary probe, while Plan allocates its plan object and
                // Deposits list before its own first branch resolves.
                if (!AsteroidFieldPlanner.CouldHaveField(node, node.Zone, tuning)) continue;

                AsteroidFieldPlan plan = AsteroidFieldPlanner.Plan(node, node.ResourceValue, node.Zone, tuning);
                if (plan.Count <= 0) continue;                       // CouldHaveField is necessary, not sufficient

                SpawnField(node, plan);
                spawnedThisTick++;
            }
        }

        /// <summary>Called with a node's id once its field is placed. A Nexus agent reports it to the
        /// authority, which owns <see cref="SpatialNode.AsteroidFieldSeed"/>.</summary>
        public Action<string> FieldPlaced;

        /// <summary>Places the field <see cref="AsteroidFieldLayout"/> describes. Each asteroid takes the
        /// stable entity id of its storage name, so one that already exists -- placed before a restart,
        /// or by a server whose report has not reached the authority yet -- is left alone rather than
        /// doubled.</summary>
        private void SpawnField(SpatialNode node, AsteroidFieldPlan plan)
        {
            Vector3D centerM = new Vector3D(node.Position.X * 1000.0, node.Position.Y * 1000.0, node.Position.Z * 1000.0);
            List<AsteroidPlacement> layout = AsteroidFieldLayout.Layout(
                node.Id, plan, node.ResourceValue, node.StableStationId != 0, StationMaterializer.SafeZoneRadius);
            for (int i = 0; i < layout.Count; i++)
            {
                AsteroidPlacement p = layout[i];
                string storageName = node.Id + "_ast" + i;
                long entityId = FeatureRockMaterializer.StableVoxelEntityId(storageName);
                if (MyEntities.EntityExists(entityId)) continue;
                MyVoxelMaterialDefinition material = PickMaterial(plan, p.MaterialRoll, p.VariantRoll);
                if (material == null) continue; // no matching content-defined material -- skip this asteroid
                SpawnAsteroid(storageName, entityId,
                    centerM + new Vector3D(p.OffsetM.X, p.OffsetM.Y, p.OffsetM.Z), (float)p.Size, p.ShapeSeed, material);
            }

            node.AsteroidFieldSeed = AsteroidFieldLayout.FieldSeed(node.Id); // nonzero: never re-placed
            Action<string> placed = FieldPlaced;
            if (placed != null) placed(node.Id);
        }

        private void SpawnAsteroid(string storageName, long entityId, Vector3D centerPos, float size, int seed,
                                   MyVoxelMaterialDefinition material)
        {
            object shapeProvider = CreateAsteroidShapeM.Invoke(null, new object[] { seed, size, 0, null });
            var dataProvider = shapeProvider as IMyStorageDataProvider;
            if (dataProvider == null) return;

            Vector3I voxelSize = new Vector3I(Math.Max(64, (int)Math.Ceiling(size)));
            MyStorageBase storage = new MyOctreeStorage(dataProvider, voxelSize);
            storage.OverwriteAllMaterials(material);

            Vector3D minCorner = centerPos - new Vector3D(MathHelper.GetNearestBiggerPowerOfTwo(size) / 2.0);
            MyWorldGenerator.AddVoxelMap(storageName, storage, minCorner, entityId);
        }

        private static MyVoxelMaterialDefinition PickMaterial(AsteroidFieldPlan plan, double materialRoll, double variantRoll)
        {
            double depositWeightTotal = 0;
            foreach (AsteroidDepositCandidate d in plan.Deposits) depositWeightTotal += d.Weight;
            double baseWeight = depositWeightTotal > 0 ? depositWeightTotal : 1.0; // keep base representative alongside deposits

            double roll = materialRoll * (baseWeight + depositWeightTotal);
            if (roll < baseWeight) return ResolveMaterial(plan.BaseMaterialOreName, variantRoll);

            roll -= baseWeight;
            foreach (AsteroidDepositCandidate d in plan.Deposits)
            {
                if (roll < d.Weight) return ResolveMaterial(d.OreName, variantRoll);
                roll -= d.Weight;
            }
            return ResolveMaterial(plan.BaseMaterialOreName, variantRoll);
        }

        /// <summary>Candidates are in definition order, which is the same on every server running the
        /// same mod list.</summary>
        private static MyVoxelMaterialDefinition ResolveMaterial(string oreName, double variantRoll)
        {
            if (string.IsNullOrEmpty(oreName)) return null;
            if (_materialsByOreName == null) BuildMaterialIndex();
            List<MyVoxelMaterialDefinition> candidates;
            if (!_materialsByOreName.TryGetValue(oreName, out candidates) || candidates.Count == 0) return null;
            int index = Math.Min(candidates.Count - 1, (int)(variantRoll * candidates.Count));
            return candidates[index];
        }

        private static void BuildMaterialIndex()
        {
            var index = new Dictionary<string, List<MyVoxelMaterialDefinition>>();
            foreach (MyVoxelMaterialDefinition def in MyDefinitionManager.Static.GetVoxelMaterialDefinitions())
            {
                if (!def.SpawnsInAsteroids || string.IsNullOrEmpty(def.MinedOre)) continue;
                List<MyVoxelMaterialDefinition> list;
                if (!index.TryGetValue(def.MinedOre, out list))
                    index[def.MinedOre] = list = new List<MyVoxelMaterialDefinition>();
                list.Add(def);
            }
            _materialsByOreName = index;
        }
    }
}
