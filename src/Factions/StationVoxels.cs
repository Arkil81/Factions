using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Definitions;             // MyDefinitionManager, MySpawnGroupDefinition, MyPrefabDefinition
using Sandbox.Engine.Voxels;           // MyStorageBase
using Sandbox.Game.Entities;           // MyCubeGrid, MyEntities, MyVoxelBase, MyPlanet
using Sandbox.Game.Entities.Character; // MyCharacter
using Sandbox.Game.World;              // MyWorldGenerator
using VRage.Game;                      // MyObjectBuilder_CubeGrid
using VRage.Game.Entity;               // MyEntity
using VRageMath;

namespace Factions
{
    /// <summary>
    /// The asteroids a station design stands in. SE's Factorum encounters are authored inside rock:
    /// their spawn groups carry <c>Voxels</c> beside the prefab, and vanilla's global-encounter
    /// generator places both. A Factions station spawns the prefab alone, so without this a design
    /// built into an asteroid stands in empty space with its interior open.
    ///
    /// <para><b>Placement.</b> Vanilla spawns the prefab by its first grid's origin at
    /// <c>E + prefab.Position</c> and each voxel at <c>E + Offset</c>, rotated by its own
    /// <c>Orientation</c>. A Factions station is spawned with <c>UseOnlyWorldMatrix</c>, which puts
    /// the prefab's largest grid on the station's matrix instead. So each voxel's pose is fixed
    /// relative to that largest grid once per prefab, and placed from the spawned grid's actual world
    /// matrix: whatever the probe, the surface snap or gravity did to the station, its rock follows.</para>
    ///
    /// <para><b>Identity.</b> A voxel is named for its station and its index, and its entity id is
    /// derived from that name. It is spawned with <c>Save</c> left true, because a pristine voxel
    /// without it never replicates (FAC-074), so it outlives a restart; the stable id is what lets the
    /// next spawn adopt it rather than stack a second rock on it, and what lets a close reach it with
    /// nothing in memory.</para>
    ///
    /// <para><b>Lifecycle.</b> Rock closes with its station when it is pristine and nothing but the
    /// station stands on it. Rock a player has drilled is kept as it is, and adopted by the next
    /// spawn, so the ore taken from it stays taken.</para>
    ///
    /// <para>A surface-sited station gets no rock: these are deep-space designs, and an asteroid
    /// pushed into terrain is not what any of them was authored as.</para>
    /// </summary>
    internal static class StationVoxels
    {
        /// <summary>The highest voxel index a station is ever closed at; vanilla's largest Factorum
        /// group places three.</summary>
        private const int MaxPerStation = 8;

        private const double SurfaceRangeM = 2000.0;           // BeginSpawn's own surface-snap range
        private const double AdoptToleranceM = 1.0;
        private const double ProximityRefusalMarginM = 300.0;  // FeatureRockMaterializer's

        private struct VoxelSpec
        {
            public string StorageName;
            public bool CenterOffset;
            public Vector3D Offset;
            public MatrixD Rotation;
        }

        private sealed class PrefabSpec
        {
            public List<VoxelSpec> Voxels = new List<VoxelSpec>();
            /// <summary>Vanilla frame (relative to the encounter point) of the prefab's largest grid.</summary>
            public MatrixD AnchorVanilla;
            public int AnchorBlocks;
        }

        // Per prefab subtype; null when no spawn group places rock with it.
        private static readonly Dictionary<string, PrefabSpec> Specs = new Dictionary<string, PrefabSpec>(StringComparer.Ordinal);

        internal static string NameFor(long stationId, int index)
        {
            return "FactionsStation_" + stationId.ToString(CultureInfo.InvariantCulture) + "_" + index.ToString(CultureInfo.InvariantCulture);
        }

        internal static long IdFor(long stationId, int index)
        {
            return FeatureRockMaterializer.StableVoxelEntityId(NameFor(stationId, index));
        }

        /// <summary>Whether <paramref name="entityId"/> is one of <paramref name="stationId"/>'s rocks.</summary>
        internal static bool IsStationVoxel(long stationId, long entityId)
        {
            if (stationId == 0 || entityId == 0) return false;
            for (int i = 0; i < MaxPerStation; i++)
                if (IdFor(stationId, i) == entityId) return true;
            return false;
        }

        /// <summary>Stands the rock <paramref name="prefabName"/>'s spawn group places around a station
        /// whose grids have just stood. Returns a fragment for the <c>Spawned</c> line, or null when the
        /// design has none.</summary>
        internal static string Ensure(long stationId, string prefabName, IList<MyCubeGrid> grids)
        {
            PrefabSpec spec = SpecFor(prefabName);
            int wanted = spec != null ? spec.Voxels.Count : 0;

            // Rock under indices this design does not use belongs to the station's previous design.
            int stale = 0;
            string ignored;
            for (int i = wanted; i < MaxPerStation; i++)
                if (ClosePristine(IdFor(stationId, i), null, out ignored)) stale++;
            if (spec == null) return stale > 0 ? "voxels=0 stale_closed=" + stale : null;

            MyCubeGrid anchor = Anchor(grids, spec.AnchorBlocks);
            if (anchor == null) return "voxels=0/" + wanted + " (largest grid not found)";
            Vector3D at = anchor.PositionComp.GetPosition();
            if (IsSurfaceSited(at)) return "voxels=0/" + wanted + " (surface site)";

            var stationGrids = new HashSet<long>();
            for (int g = 0; g < grids.Count; g++) if (grids[g] != null) stationGrids.Add(grids[g].EntityId);

            int spawned = 0, adopted = 0, skipped = 0;
            string why = null;
            MatrixD anchorWorld = anchor.WorldMatrix;
            for (int i = 0; i < spec.Voxels.Count; i++)
            {
                VoxelSpec v = spec.Voxels[i];
                MyStorageBase storage;
                try { storage = MyStorageBase.LoadFromFile(MyWorldGenerator.GetVoxelPrefabPath(v.StorageName)); }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn(ex, "StationVoxels: loading '" + v.StorageName + "' threw.");
                    storage = null;
                }
                if (storage == null) { skipped++; why = "no storage '" + v.StorageName + "'"; continue; }

                MatrixD world = VanillaPose(v, storage) * MatrixD.Invert(spec.AnchorVanilla) * anchorWorld;
                long id = IdFor(stationId, i);
                string name = NameFor(stationId, i);

                MyEntity existing;
                if (MyEntities.TryGetEntityById(id, out existing, allowClosed: true))
                {
                    var live = existing as MyVoxelBase;
                    if (live == null || live.Closed || live.MarkedForClose) { skipped++; why = "previous rock still closing"; continue; }
                    if (Vector3D.Distance(live.PositionComp.GetPosition(), world.Translation) <= AdoptToleranceM || IsEdited(live))
                    {
                        adopted++;
                        continue;
                    }
                    // Pristine rock where the station no longer stands: it goes, and the next spawn
                    // places it again. Close is deferred, so the id is not free this frame.
                    string refusal;
                    ClosePristine(id, stationGrids, out refusal);
                    skipped++;
                    why = "previous rock elsewhere";
                    continue;
                }

                string obstruction = Obstruction(storage, world, stationGrids);
                if (obstruction != null) { skipped++; why = "blocked by " + obstruction; continue; }

                MyVoxelMap voxel = MyWorldGenerator.AddVoxelMap(name, storage, world, id, lazyPhysics: false, useVoxelOffset: false);
                if (voxel == null) { skipped++; why = "AddVoxelMap refused '" + name + "'"; continue; }
                // Without this SE re-sends the whole storage on every stream-in
                // (FeatureRockMaterializer.SpawnObject has the measurement).
                voxel.AsteroidName = name;
                spawned++;
            }

            return "voxels=" + (spawned + adopted).ToString(CultureInfo.InvariantCulture) + "/" + wanted.ToString(CultureInfo.InvariantCulture)
                 + (adopted > 0 ? " adopted=" + adopted.ToString(CultureInfo.InvariantCulture) : "")
                 + (stale > 0 ? " stale_closed=" + stale.ToString(CultureInfo.InvariantCulture) : "")
                 + (skipped > 0 ? " skipped=" + skipped.ToString(CultureInfo.InvariantCulture) + " (" + why + ")" : "");
        }

        /// <summary>Closes every pristine rock of <paramref name="stationId"/>'s that nothing besides
        /// <paramref name="stationGrids"/> stands on. Returns a fragment for the <c>Despawned</c> line, or
        /// null when the station had none.</summary>
        internal static string CloseAll(long stationId, ICollection<long> stationGrids)
        {
            if (stationId == 0) return null;
            int closed = 0, kept = 0;
            string why = null;
            for (int i = 0; i < MaxPerStation; i++)
            {
                string refusal;
                if (ClosePristine(IdFor(stationId, i), stationGrids, out refusal)) closed++;
                else if (refusal != null) { kept++; why = refusal; }
            }
            if (closed == 0 && kept == 0) return null;
            return "voxels_closed=" + closed.ToString(CultureInfo.InvariantCulture)
                 + (kept > 0 ? " kept=" + kept.ToString(CultureInfo.InvariantCulture) + " (" + why + ")" : "");
        }

        /// <summary>True when the voxel was closed. <paramref name="refusal"/> is null when there was
        /// nothing under that id, otherwise the veto that kept it.</summary>
        private static bool ClosePristine(long entityId, ICollection<long> stationGrids, out string refusal)
        {
            refusal = null;
            MyEntity entity;
            if (!MyEntities.TryGetEntityById(entityId, out entity)) return false;
            var voxel = entity as MyVoxelBase;
            if (voxel == null || voxel.MarkedForClose) return false;
            if (IsEdited(voxel)) { refusal = "drilled"; return false; }
            if (AnythingStandingOn(voxel, stationGrids)) { refusal = "a grid or player is on it"; return false; }
            voxel.Close();
            return true;
        }

        private static bool IsEdited(MyVoxelBase voxel)
        {
            if (voxel.ContentChanged || voxel.BeforeContentChanged || voxel.CreatedByUser) return true;
            var storage = voxel.Storage as MyStorageBase;
            return storage != null && storage.HasPendingWrites;
        }

        private static bool AnythingStandingOn(MyVoxelBase voxel, ICollection<long> stationGrids)
        {
            BoundingBoxD box = voxel.PositionComp.WorldAABB;
            box.Inflate(ProximityRefusalMarginM);
            var hits = new List<MyEntity>();
            MyGamePruningStructure.GetTopMostEntitiesInBox(ref box, hits);
            for (int i = 0; i < hits.Count; i++)
            {
                MyEntity e = hits[i];
                if (e == null || e.MarkedForClose) continue;
                var grid = e as MyCubeGrid;
                if (grid != null && (stationGrids == null || !stationGrids.Contains(grid.EntityId))) return true;
                var character = e as MyCharacter;
                if (character != null && character.IsPlayer) return true;
            }
            return false;
        }

        /// <summary>A grid or player inside the rock's oriented bounds, or null.</summary>
        private static string Obstruction(MyStorageBase storage, MatrixD world, ICollection<long> stationGrids)
        {
            Vector3D half = new Vector3D(storage.Size.X, storage.Size.Y, storage.Size.Z) * 0.5;
            var rock = new MyOrientedBoundingBoxD(new BoundingBoxD(-half, half), world);
            BoundingBoxD search = rock.GetAABB();
            var hits = new List<MyEntity>();
            MyGamePruningStructure.GetTopMostEntitiesInBox(ref search, hits);
            for (int i = 0; i < hits.Count; i++)
            {
                MyEntity e = hits[i];
                if (e == null || e.MarkedForClose) continue;
                var grid = e as MyCubeGrid;
                var character = e as MyCharacter;
                if (grid == null && (character == null || !character.IsPlayer)) continue;
                if (grid != null && stationGrids.Contains(grid.EntityId)) continue;
                var other = new MyOrientedBoundingBoxD((BoundingBoxD)e.PositionComp.LocalAABB, e.WorldMatrix);
                if (!rock.Intersects(ref other)) continue;
                return grid != null ? "grid " + grid.EntityId : "a player";
            }
            return null;
        }

        private static bool IsSurfaceSited(Vector3D at)
        {
            MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(at);
            if (planet == null) return false;
            return (at - planet.GetClosestSurfacePointGlobal(at)).Length() < SurfaceRangeM;
        }

        /// <summary>The voxel's pose in vanilla's encounter frame. A centred voxel stands on its own
        /// orientation at its offset; an uncentred one is axis-aligned with its offset as the minimum
        /// corner.</summary>
        private static MatrixD VanillaPose(VoxelSpec v, MyStorageBase storage)
        {
            if (v.CenterOffset)
            {
                MatrixD pose = v.Rotation;
                pose.Translation = v.Offset;
                return pose;
            }
            return MatrixD.CreateTranslation(v.Offset + new Vector3D(storage.Size.X, storage.Size.Y, storage.Size.Z) * 0.5);
        }

        /// <summary>The spawned grid that stands on the station's matrix: SE places the first grid
        /// with the most blocks there, and the spawn keeps the prefab's grid order. Matched on the
        /// prefab's block count first; failing that, the largest spawned grid.</summary>
        private static MyCubeGrid Anchor(IList<MyCubeGrid> grids, int blocks)
        {
            if (grids == null) return null;
            MyCubeGrid largest = null;
            for (int i = 0; i < grids.Count; i++)
            {
                MyCubeGrid g = grids[i];
                if (g == null || g.MarkedForClose) continue;
                if (g.BlocksCount == blocks) return g;
                if (largest == null || g.BlocksCount > largest.BlocksCount) largest = g;
            }
            return largest;
        }

        private static PrefabSpec SpecFor(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName) || MyDefinitionManager.Static == null) return null;
            PrefabSpec spec;
            if (Specs.TryGetValue(prefabName, out spec)) return spec;
            spec = null;
            try { spec = Build(prefabName); }
            catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "StationVoxels: reading the spawn group of '" + prefabName + "' threw."); }
            Specs[prefabName] = spec;
            return spec;
        }

        private static PrefabSpec Build(string prefabName)
        {
            MyPrefabDefinition prefab = MyDefinitionManager.Static.GetPrefabDefinition(prefabName);
            if (prefab == null || prefab.CubeGrids == null || prefab.CubeGrids.Length == 0) return null;

            foreach (MySpawnGroupDefinition group in MyDefinitionManager.Static.GetSpawnGroupDefinitions())
            {
                if (group == null || group.Voxels == null || group.Voxels.Count == 0 || group.Prefabs == null) continue;
                for (int p = 0; p < group.Prefabs.Count; p++)
                {
                    if (!string.Equals(group.Prefabs[p].SubtypeId, prefabName, StringComparison.Ordinal)) continue;

                    var spec = new PrefabSpec();
                    MyObjectBuilder_CubeGrid[] cubeGrids = prefab.CubeGrids;
                    Vector3D firstOrigin = cubeGrids[0].PositionAndOrientation.HasValue
                        ? (Vector3D)cubeGrids[0].PositionAndOrientation.Value.Position
                        : Vector3D.Zero;
                    MatrixD largest = MatrixD.Identity;
                    int most = 0;
                    for (int g = 0; g < cubeGrids.Length; g++)
                    {
                        if (cubeGrids[g].CubeBlocks.Count <= most) continue;
                        most = cubeGrids[g].CubeBlocks.Count;
                        largest = cubeGrids[g].PositionAndOrientation.HasValue
                            ? cubeGrids[g].PositionAndOrientation.Value.GetMatrix()
                            : MatrixD.Identity;
                    }
                    spec.AnchorBlocks = most;
                    // UseGridOrigin: every grid shifted so the first grid's origin lands on the prefab position.
                    spec.AnchorVanilla = largest * MatrixD.CreateTranslation((Vector3D)group.Prefabs[p].Position - firstOrigin);

                    for (int i = 0; i < group.Voxels.Count; i++)
                    {
                        MySpawnGroupDefinition.SpawnGroupVoxel gv = group.Voxels[i];
                        if (string.IsNullOrEmpty(gv.StorageName)) continue;
                        spec.Voxels.Add(new VoxelSpec
                        {
                            StorageName = gv.StorageName,
                            CenterOffset = gv.CenterOffset,
                            Offset = gv.Offset,
                            Rotation = gv.Orientation.HasValue && gv.Orientation.Value != Quaternion.Zero
                                ? MatrixD.CreateFromQuaternion(gv.Orientation.Value)
                                : MatrixD.Identity
                        });
                    }
                    if (spec.Voxels.Count > MaxPerStation) spec.Voxels.RemoveRange(MaxPerStation, spec.Voxels.Count - MaxPerStation);
                    return spec.Voxels.Count > 0 ? spec : null;
                }
            }
            return null;
        }
    }
}
