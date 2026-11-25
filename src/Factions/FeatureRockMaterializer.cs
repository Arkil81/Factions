using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;                       // AccessTools
using Sandbox.Definitions;              // MyDefinitionManager
using Sandbox.Engine.Voxels;            // MyStorageBase, MyOctreeStorage
using Sandbox.Game.Entities;            // MyVoxelBase, MyEntities, MyCubeGrid
using Sandbox.Game.Entities.Character;  // MyCharacter
using Sandbox.Game.World;               // MySession, MyWorldGenerator
using VRage.Game;                       // MyVoxelMaterialDefinition
using VRage.Game.Entity;                // MyEntity
using VRage.Voxels;                     // IMyStorageDataProvider
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Materialises the physical rock an authored <see cref="WorldFeature"/> asks for, and unloads it
    /// again when nobody can see it.
    ///
    /// <para><b>Player presence is the only trigger.</b> Rock exists because a feature says so, not
    /// because a node does — nodes are placed by reading the geography, so the rock predates them.
    /// Nodes appear here only as a <i>suppressor</i>, and only where one has a founded station: an
    /// unfounded resource node stays surrounded by rock, which is the whole point of it.</para>
    ///
    /// <para><b>Two radii, both read from the world's own settings rather than invented here.</b>
    /// <c>SyncDistance</c> (3 km live) is time-critical — rock inside it is being replicated and
    /// simulated, so it is built ahead of everything else and ignores the lazy budget.
    /// <c>ViewDistance</c> (15 km live) is lazy: rock there only has to be visible eventually, so it
    /// is built a few cells per tick. Outside view distance pristine rock is dropped immediately.
    /// Reading SE's numbers means the two systems cannot disagree about how far "near" is.</para>
    ///
    /// <para><b>We unload pristine rock; SE reverts edited rock.</b> A rock nobody has touched is a
    /// pure function of its cell, so unloading it costs nothing and reclaims an entity, an octree, a
    /// physics body, a clipmap and its replication to every client in range. A rock a player has
    /// drilled is *their work*: it is handed to SE's own voxel trash reverter, which is built for
    /// exactly that and is far too lazy to be the answer for pristine rock.</para>
    /// </summary>
    public sealed class FeatureRockMaterializer
    {
        private static readonly Type CompositeShapeProviderType =
            AccessTools.TypeByName("Sandbox.Game.World.Generator.MyCompositeShapeProvider");
        private static readonly MethodInfo CreateAsteroidShapeM =
            CompositeShapeProviderType != null
                ? AccessTools.Method(CompositeShapeProviderType, "CreateAsteroidShape",
                    new[] { typeof(int), typeof(float), typeof(int), typeof(int?) })
                : null;

        /// <summary>Despawn is held off until a little past view distance. Not a delay — a band, so a
        /// player hovering exactly on the boundary does not thrash a cell in and out every tick.</summary>
        private const double DespawnHysteresis = 1.15;

        /// <summary>Fallbacks for when the session settings cannot be read. The live world's own
        /// values, so behaviour degrades to the right shape rather than to zero.</summary>
        private const double DefaultSyncDistanceM = 3000.0;
        private const double DefaultViewDistanceM = 15000.0;

        /// <summary>A rock this materialiser built and still owns. The plan is kept alongside the
        /// entity id because a linked client is told what to build from exactly these fields -- the
        /// cell can no longer be reduced to a list of ids once something downstream has to describe
        /// the rock rather than merely close it.</summary>
        public struct LiveRock
        {
            /// <summary>The rock's stable id, derived from its storage name: known before it is spawned.</summary>
            public long EntityId;
            public FeatureRockObject Plan;
            /// <summary>Whether the server has spawned it. Most planned rock is
            /// not: it exists only on linked clients, which build it from the plan.</summary>
            public bool Spawned;
        }

        private sealed class MaterializedCell
        {
            public List<LiveRock> Rocks = new List<LiveRock>();
            /// <summary>Objects suppressed by a station bubble when this cell was built. Kept so the
            /// cell is not rebuilt from scratch to rediscover them.</summary>
            public int Suppressed;
        }

        private struct CellKey : IEquatable<CellKey>
        {
            public readonly int FeatureIndex;
            public readonly CellCoord Cell;
            public CellKey(int featureIndex, CellCoord cell) { FeatureIndex = featureIndex; Cell = cell; }
            public bool Equals(CellKey o) { return FeatureIndex == o.FeatureIndex && Cell.Equals(o.Cell); }
            public override bool Equals(object o) { return o is CellKey && Equals((CellKey)o); }
            public override int GetHashCode() { unchecked { return FeatureIndex * 397 ^ Cell.GetHashCode(); } }
        }

        private readonly Dictionary<CellKey, MaterializedCell> _live = new Dictionary<CellKey, MaterializedCell>();
        /// <summary>For each cell coordinate with planned rock in it, a bit per feature index holding that cell, so the
        /// shell pass does one lookup per cell rather than one per feature.</summary>
        private readonly Dictionary<CellCoord, long> _liveMask = new Dictionary<CellCoord, long>();
        /// <summary>Entity ids of the rock this materialiser currently owns. An exact answer to "is
        /// this voxel ours", which the replication gate needs per client per voxel and cannot get
        /// from the storage name without guessing at a prefix.</summary>
        private readonly HashSet<long> _owned = new HashSet<long>();
        private readonly List<WorldFeature> _features = new List<WorldFeature>();
        private readonly List<Vec3> _centers = new List<Vec3>();

        private static Dictionary<string, List<MyVoxelMaterialDefinition>> _materialsBySubType;

        public static bool DebugEnabled;

        /// <summary>Generator version handed to CreateAsteroidShape. Resolved from tuning at the
        /// first spawn and held, so it cannot change mid-session under a tuning reload and leave
        /// half a field built by a different algorithm.</summary>
        private static int _generatorVersion = 4;
        public static int GeneratorVersion { get { return _generatorVersion; } }

        /// <summary>Bumped whenever the live set gains or loses a rock. Lets a reader skip its own
        /// per-player diff entirely on the great majority of frames, when nothing has moved in or
        /// out and only the players have.</summary>
        public int Revision { get; private set; }

        /// <summary>Live cell count, for telemetry and the bridge.</summary>
        public int LiveCells { get { return _live.Count; } }
        /// <summary>Rock the server has spawned.</summary>
        public int LiveObjects
        {
            get
            {
                int n = 0;
                foreach (MaterializedCell c in _live.Values)
                    for (int i = 0; i < c.Rocks.Count; i++) if (c.Rocks[i].Spawned) n++;
                return n;
            }
        }

        /// <summary>Rock planned in the cells around players, spawned or not.</summary>
        public int PlannedObjects
        {
            get
            {
                int n = 0;
                foreach (MaterializedCell c in _live.Values) n += c.Rocks.Count;
                return n;
            }
        }

        /// <summary>Closes every feature rock in the world and forgets it, so the next approach
        /// rebuilds from scratch.
        /// <para>Exists because the things that decide a rock's content — its authored material, the
        /// generator version — are also part of its storage name, so changing one orphans everything
        /// built under the old name. Without this the operator deletes rock by hand, which is how
        /// this session went.</para>
        /// <para>Skips rock a player has edited unless <paramref name="includeEdited"/>: that is
        /// their work, and SE's own reverter is what reclaims it. "Edited" here means the persisted
        /// signals only — see the note in the loop on why IsSeedOpen cannot be one of them.</para></summary>
        public string PurgeAll(bool includeEdited)
        {
            _live.Clear();
            _liveMask.Clear();
            _queue.Clear();
            _queueNext = 0;
            _spawnedRefs.Clear();
            _plannedFrom.Clear();
            _keptUntil.Clear();

            // Instances, not GetInstances(list, predicate) -- that overload is ModAPI's
            // IMyVoxelMaps, and a Torch plugin binds the concrete MyVoxelMaps, which has neither.
            // Copied into a list first because Close() mutates the collection being walked.
            var voxels = new List<MyVoxelBase>();
            foreach (MyVoxelBase v in MySession.Static.VoxelMaps.Instances)
                if (v != null && v.StorageName != null
                    && v.StorageName.StartsWith("fr", StringComparison.Ordinal))
                    voxels.Add(v);

            int attempted = 0, keptEdited = 0;
            var closedIds = new List<long>();
            foreach (MyVoxelBase v in voxels)
            {
                // IsSeedOpen is deliberately NOT part of this test. It is a runtime field that does
                // not survive a save, so every rock reloaded from disk reads null -- which made the
                // first version of this call 181 untouched rocks "edited" and purge nothing at all.
                // Only the persisted signals can answer "did a player work this" after a reload.
                bool edited = v.ContentChanged || v.CreatedByUser;
                if (edited && !includeEdited) { keptEdited++; continue; }
                v.Close();
                attempted++;
                closedIds.Add(v.EntityId);
            }

            // Verified on the only signal that is meaningful synchronously.
            //
            // Two wrong versions preceded this. The first counted Close() CALLS and reported
            // "closed 362" while 181 rocks were still in the world. The second re-queried
            // TryGetEntityById immediately and reported "closed 0 of 176" -- because Close() is
            // DEFERRED, so an entity is still resolvable in the same tick no matter what happened.
            // That second version could only ever report zero, and it was worded to be believed over
            // the operator's own eyes. An inverted lie is worse than the original one.
            //
            // MarkedForClose IS set synchronously by Close(), so it answers "did the request take"
            // honestly. It does not answer "is it gone yet" -- nothing can, in this tick -- so the
            // wording promises only what was accepted, and defers the rest.
            int accepted = 0, refused = 0;
            foreach (long id in closedIds)
            {
                MyEntity e;
                if (!MyEntities.TryGetEntityById(id, out e, allowClosed: true)) { accepted++; continue; }
                if (e.Closed || e.MarkedForClose) accepted++;
                else refused++;
            }

            string verdict = "rockpurge: " + accepted + " of " + attempted
                           + " feature rock(s) accepted the close, kept " + keptEdited + " edited.";
            if (refused > 0)
                verdict += " " + refused + " did NOT accept it and are still ours -- that is a real"
                         + " refusal, not deferral.";
            verdict += " Closure completes on a later tick; re-run to confirm the world is empty"
                     + " rather than trusting this line.";
            return verdict;
        }

        /// <summary>
        /// Reads back what a rock is ACTUALLY made of, from its storage, and reports the histogram.
        ///
        /// <para>Every ROCK_ line records the material we <i>asked</i> for. Nothing has ever recorded
        /// what SE built, which is why four separate wrong theories survived a whole session of live
        /// testing: the rule was correct and unobservable. This is the missing fact — if the server's
        /// own histogram disagrees with what a player sees, the disagreement is downstream of here;
        /// if it agrees, material control never worked.</para>
        ///
        /// <para>Sampled at a coarse LOD. Reading a 400 m rock at LOD0 is 64M voxels; at LOD4 it is
        /// a few thousand, which is ample for "what is this made of".</para>
        /// </summary>
        public string Inspect(IList<Vec3> playerPositionsKm, string storageNameOrNull)
        {
            if (MySession.Static == null) return "rockinspect: no session.";

            var candidates = new List<MyVoxelBase>();
            foreach (MyVoxelBase v in MySession.Static.VoxelMaps.Instances)
            {
                if (v == null || v.StorageName == null) continue;
                if (!v.StorageName.StartsWith("fr", StringComparison.Ordinal)) continue;
                if (storageNameOrNull != null
                    && v.StorageName.IndexOf(storageNameOrNull, StringComparison.OrdinalIgnoreCase) < 0) continue;
                candidates.Add(v);
            }
            if (candidates.Count == 0) return "rockinspect: no feature rock matched.";

            // Nearest to a player, so "the one in front of me" is what gets reported.
            MyVoxelBase target = candidates[0];
            if (playerPositionsKm != null && playerPositionsKm.Count > 0)
            {
                double best = double.MaxValue;
                foreach (MyVoxelBase v in candidates)
                {
                    Vector3D c = v.PositionComp.WorldAABB.Center;
                    foreach (Vec3 p in playerPositionsKm)
                    {
                        double dx = c.X / 1000.0 - p.X, dy = c.Y / 1000.0 - p.Y, dz = c.Z / 1000.0 - p.Z;
                        double d = dx * dx + dy * dy + dz * dz;
                        if (d < best) { best = d; target = v; }
                    }
                }
            }

            var storage = target.Storage as MyStorageBase;
            if (storage == null) return "rockinspect: " + target.StorageName + " has no storage.";

            // Pick the finest LOD whose sample count stays bounded. LOD4 gave 173 solid samples on a
            // real rock, which cannot distinguish "no iron" from "iron veins finer than the grid" --
            // and ore deposits are exactly that: a few percent of the volume, in veins. Stepping down
            // until the box exceeds the budget keeps small rocks sharp and large ones affordable.
            const int MaxSamples = 300000;
            int lod = 4;
            while (lod > 0)
            {
                Vector3I probe = (storage.Size >> (lod - 1));
                double n = (double)probe.X * probe.Y * probe.Z;
                if (n > MaxSamples) break;
                lod--;
            }

            Vector3I max = (storage.Size >> lod) - Vector3I.One;
            if (max.X < 0 || max.Y < 0 || max.Z < 0) max = Vector3I.Zero;

            var data = new MyStorageData();
            data.Resize(Vector3I.Zero, max);
            storage.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, lod, Vector3I.Zero, max);

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int solid = 0;
            Vector3I size = data.Size3D;
            for (int x = 0; x < size.X; x++)
            for (int y = 0; y < size.Y; y++)
            for (int z = 0; z < size.Z; z++)
            {
                var p = new Vector3I(x, y, z);
                if (data.Content(ref p) == 0) continue;      // empty space carries a meaningless material
                solid++;
                byte idx = data.Material(ref p);
                MyVoxelMaterialDefinition def = MyDefinitionManager.Static.GetVoxelMaterialDefinition(idx);
                string ore = def == null ? "(index " + idx + ")" : (def.MinedOre ?? def.Id.SubtypeName);
                int n;
                counts.TryGetValue(ore, out n);
                counts[ore] = n + 1;
            }

            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in counts)
                parts.Add(kv.Key + " " + (solid > 0 ? (100.0 * kv.Value / solid).ToString("F0") : "0") + "%");
            parts.Sort(StringComparer.Ordinal);

            return "rockinspect " + target.StorageName
                 + " | ACTUAL: " + (parts.Count == 0 ? "(no solid voxels sampled)" : string.Join(", ", parts.ToArray()))
                 + " | solidSamples=" + solid + " lod=" + lod + " storageSize=" + storage.Size.X
                 + " | materialControl=" + (FeatureRockMaterialControl.Active ? "on" : "OFF")
                 + " applied=" + FeatureRockMaterialControl.TimesApplied;
        }

        /// <summary>Every rock currently materialised and still ours, for whoever has to describe it
        /// rather than simulate it. Enumerated rather than copied: the caller filters by distance to
        /// one player and keeps a handful, and a per-tick snapshot list of the whole belt is exactly
        /// the per-tick allocation this file is otherwise careful to avoid.</summary>
        public IEnumerable<LiveRock> LiveRocks()
        {
            foreach (MaterializedCell cell in _live.Values)
                for (int i = 0; i < cell.Rocks.Count; i++)
                    yield return cell.Rocks[i];
        }

        /// <summary>The materialiser the live session is running, for the replication gate -- which
        /// is a static Harmony patch on an SE type and has no other way to reach it. Null outside a
        /// loaded session, which the gate treats as "not ours" and leaves SE's own answer alone.</summary>
        public static FeatureRockMaterializer Active;

        /// <summary>
        /// True when this voxel is rock we planned AND nothing has edited it yet. Both halves matter
        /// to the gate: the first says a client could rebuild it from a name, the second says the
        /// client's copy is still identical to ours. The moment either fails the voxel must
        /// replicate normally again, or the player who just drilled it would never be sent the hole.
        ///
        /// <para>The pristine half is read from the voxel itself rather than trusted to the
        /// bookkeeping, and that is deliberate. <see cref="SpawnObject"/>'s adopt path returns an id
        /// for a rock a player may already have edited, and it attaches no <c>RangeChanged</c>
        /// handler -- so an owned-set maintained only by that hook would mark an edited rock as ours
        /// and withhold the edit from every enrolled client. Asking the flag cannot go stale that
        /// way.</para>
        /// </summary>
        /// <summary>The same question by id, for a caller that holds a record rather than an entity.
        /// A rock that has stopped being pristine is still in this materialiser's cell records until
        /// its cell despawns, so anything describing rock to a client has to ask rather than assume.</summary>
        /// <summary>True when an entity that is not closing stands under <paramref name="entityId"/>: for planned rock
        /// the server has not spawned, that is edited rock persisted from an earlier visit.</summary>
        public static bool HasForeignEntity(long entityId)
        {
            MyEntity e;
            return MyEntities.TryGetEntityById(entityId, out e) && !e.Closed && !e.MarkedForClose;
        }

        public bool OwnsPristine(long entityId)
        {
            if (!_owned.Contains(entityId)) return false;
            MyEntity entity;
            if (!MyEntities.TryGetEntityById(entityId, out entity)) return false;
            return OwnsPristine(entity as MyVoxelBase);
        }

        public bool OwnsPristine(MyVoxelBase voxel)
        {
            if (voxel == null) return false;
            if (!_owned.Contains(voxel.EntityId)) return false;
            if (!voxel.IsSeedOpen.HasValue || !voxel.IsSeedOpen.Value) return false;
            return !voxel.ContentChanged && !voxel.BeforeContentChanged && !voxel.CreatedByUser;
        }

        /// <summary>Anchors resolve once, here, rather than per cell per tick.</summary>
        public void SetFeatures(IEnumerable<WorldFeature> features, IPlanetCatalogue planets)
        {
            Active = this;
            ArmReclaim();
            _features.Clear();
            _centers.Clear();
            if (features == null) return;

            Dictionary<string, Vec3> byId = null;
            if (planets != null)
            {
                byId = new Dictionary<string, Vec3>(StringComparer.Ordinal);
                foreach (PlanetRecord p in planets.Planets)
                    if (p != null && !string.IsNullOrEmpty(p.Id)) byId[p.Id] = p.Center;
            }

            foreach (WorldFeature f in features)
            {
                if (f == null) continue;
                Vec3 center = f.Center;
                if (!string.IsNullOrEmpty(f.AnchorPlanetId) && byId != null)
                {
                    Vec3 anchored;
                    if (byId.TryGetValue(f.AnchorPlanetId, out anchored)) center = anchored;
                }
                _features.Add(f);
                _centers.Add(center);
            }
        }

        /// <summary>Per-tick entry point. <paramref name="playerPositionsKm"/> is the frame's cached
        /// player positions; with nobody online there is nothing to build and everything to drop.
        /// <para>Rock is planned -- listed, nothing spawned -- in the cells within the view radius of a player, and
        /// spawned from that plan: only what lies inside a <see cref="RockShell"/>. Both halves are bounded per tick: a jump or a teleport can drop a
        /// player into the middle of a dense belt, and an unbounded burst of synchronous AddVoxelMap calls is the
        /// voxel stall this server has a documented history of.</para></summary>
        public void UpdateObservation(IList<Vec3> playerPositionsKm, SpatialGraph graph, FactionTuning tuning)
        {
            long entry = Stopwatch.GetTimestamp();
            try { UpdateObservationCore(playerPositionsKm, graph, tuning); }
            finally
            {
                long ticks = Stopwatch.GetTimestamp() - entry;
                _tTotal += ticks;
                if (ticks > _tTotalMax) _tTotalMax = ticks;
                if (ticks * 2000 > Stopwatch.Frequency) _overHalfMs++;
            }
        }

        private void UpdateObservationCore(IList<Vec3> playerPositionsKm, SpatialGraph graph, FactionTuning tuning)
        {
            if (tuning == null || MySession.Static == null) return;

            // Before anything else, and to completion before anything spawns. Two reasons, both
            // load-bearing:
            //
            // It must not be gated on the checks below. Rock left in the save by a previous session
            // has to be reclaimed even in a world that has since lost its features or its shape
            // factory -- those are exactly the states where nothing will ever come back for it.
            //
            // It must finish before the first spawn. Both ends address a rock by the same id derived
            // from its storage name, and Close() is deferred, so a cell built in the same tick its
            // old rock was closed would adopt a dying entity, spawn nothing, and still be recorded in
            // _live -- retiring that cell for the rest of the session.
            if (ReclaimReloadedRock(tuning)) return;

            if (CreateAsteroidShapeM == null) return;   // reflected factory missing (game update?) -- fail closed
            if (_features.Count == 0) return;           // no authored features: deep space holds nothing

            double cellKm = tuning.FeatureRockCellSizeKm;
            if (cellKm <= 0) return;
            if (tuning.FeatureRockGeneratorVersion > 2) _generatorVersion = tuning.FeatureRockGeneratorVersion;

            _graph = graph;
            _frameNo++;
            long a0 = AllocProbe.Begin();
            double viewKm = ViewRadiusKm(tuning);
            ExpireHolds();
            IList<Vec3> keepers = WithHolds(playerPositionsKm);

            int every = Math.Max(1, tuning.FeatureRockShellEvalFrames);
            _frames++;
            AllocProbe.End("rock.setup", a0); a0 = AllocProbe.Begin();
            long t0 = Stopwatch.GetTimestamp();
            if (_frameNo % every == 0 || keepers == null || keepers.Count == 0)
                DespawnOutOfRange(keepers, viewKm * DespawnHysteresis, cellKm);
            long t1 = Stopwatch.GetTimestamp();
            _tDespawn += t1 - t0;
            AllocProbe.End("rock.despawn", a0); a0 = AllocProbe.Begin();

            if (keepers == null || keepers.Count == 0) { _queue.Clear(); _plannedFrom.Clear(); return; }

            PlanRing(keepers, graph, tuning, cellKm, viewKm, Math.Max(1, tuning.FeatureRockPlanCellsPerTick));
            long t2 = Stopwatch.GetTimestamp();
            _tWalk += t2 - t1;
            AllocProbe.End("rock.plan", a0); a0 = AllocProbe.Begin();

            int budget = Math.Max(0, tuning.MaxFeatureRockSpawnsPerTick) * SpawnBudgetMultiplier;
            _spawnBudgetTicks = (long)(Math.Max(0.05, tuning.FeatureRockSpawnBudgetMs) * Stopwatch.Frequency / 1000.0);

            if (_frameNo % every == 0) StartPass(playerPositionsKm, tuning, every);
            AllocProbe.End("rock.passStart", a0); a0 = AllocProbe.Begin();
            StepPass(budget);
            AllocProbe.End("rock.passStep", a0); a0 = AllocProbe.Begin();
            long t3 = Stopwatch.GetTimestamp();
            SpawnQueued(budget);
            _tSpawn += Stopwatch.GetTimestamp() - t3;
            AllocProbe.End("rock.spawn", a0);
            if (DebugEnabled) ReportShell();
        }

        /// <summary>Multiplies <c>MaxFeatureRockSpawnsPerTick</c> to give the spawn pass its per-tick budget.</summary>
        private const int SpawnBudgetMultiplier = 4;

        /// <summary>The radius around a player within which rock is planned and named to linked clients:
        /// <see cref="FactionTuning.FeatureRockViewKm"/>, or SE's ViewDistance when that is 0, never below SyncDistance.</summary>
        public static double ViewRadiusKm(FactionTuning tuning)
        {
            double syncKm = SessionSyncDistanceM() / 1000.0;
            double viewKm = tuning != null && tuning.FeatureRockViewKm > 0
                ? tuning.FeatureRockViewKm
                : SessionViewDistanceM() / 1000.0;
            return viewKm < syncKm ? syncKm : viewKm;
        }

        private SpatialGraph _graph;
        private long _frameNo;
        private long _spawnBudgetTicks = Stopwatch.Frequency / 1000;

        /// <summary>Where a planning walk last completed with nothing left to plan. A keeper within
        /// <see cref="PlanRecomputeKm"/> of one is skipped: every cell in its view is live, and stays live while it
        /// stands there, because a cell drops only when no keeper is within the view radius x 1.15 of it. An entry is
        /// forgotten as soon as no keeper stands at it.</summary>
        private readonly List<Vec3> _plannedFrom = new List<Vec3>();
        private const double PlanRecomputeKm = 0.5;

        private void PlanRing(IList<Vec3> players, SpatialGraph graph, FactionTuning tuning,
                              double cellKm, double outerKm, int cellBudget)
        {
            if (outerKm <= 0) return;
            for (int k = _plannedFrom.Count - 1; k >= 0; k--)
                if (!AnyWithinKm(players, _plannedFrom[k], PlanRecomputeKm)) _plannedFrom.RemoveAt(k);

            int planned = 0;
            for (int p = 0; p < players.Count; p++)
            {
                if (AnyWithinKm(_plannedFrom, players[p], PlanRecomputeKm)) continue;
                foreach (CellCoord cell in FeatureRockPlanner.CellsWithin(players[p], outerKm, cellKm))
                {
                    if (CellDistanceKm(cell, cellKm, players[p]) > outerKm) continue;   // AABB corner overshoot
                    for (int i = 0; i < _features.Count; i++)
                    {
                        var key = new CellKey(i, cell);
                        if (_live.ContainsKey(key)) continue;
                        if (!FeatureRockPlanner.CellCouldTouch(_features[i], _centers[i], cell, cellKm)) continue;
                        PlanCell(key, _features[i], _centers[i], cell, graph, tuning, cellKm);
                        if (++planned >= cellBudget) return;
                    }
                }
                _plannedFrom.Add(players[p]);
            }
        }

        private void MaskAdd(CellKey key)
        {
            if (key.FeatureIndex >= 64) return;
            long m;
            _liveMask.TryGetValue(key.Cell, out m);
            _liveMask[key.Cell] = m | (1L << key.FeatureIndex);
        }

        private void MaskRemove(CellKey key)
        {
            long m;
            if (key.FeatureIndex >= 64 || !_liveMask.TryGetValue(key.Cell, out m)) return;
            m &= ~(1L << key.FeatureIndex);
            if (m == 0) _liveMask.Remove(key.Cell); else _liveMask[key.Cell] = m;
        }

        private static bool AnyWithinKm(IList<Vec3> points, Vec3 p, double km)
        {
            double k2 = km * km;
            for (int i = 0; i < points.Count; i++) if (points[i].DistanceSquaredTo(p) <= k2) return true;
            return false;
        }

        /// <summary>Lists every object one cell asks for, nothing spawned. A cell that produced nothing -- the common
        /// case -- is still recorded, so it is not re-planned on every tick for the rest of the session.</summary>
        private void PlanCell(CellKey key, WorldFeature feature, Vec3 center, CellCoord cell,
                              SpatialGraph graph, FactionTuning tuning, double cellKm)
        {
            long tp = Stopwatch.GetTimestamp();
            var record = new MaterializedCell();
            _live[key] = record;
            MaskAdd(key);
            Revision++;
            _plannedCellsWindow++;

            _cellObjects.Clear();
            double maxSizeM = 0;
            foreach (FeatureRockObject o in FeatureRockPlanner.ObjectsInCell(
                         feature, center, cell, WorldSeed(), tuning.FeatureMaxDensityPer1000Km3, cellKm))
            {
                _cellObjects.Add(o);
                if (o.SizeM > maxSizeM) maxSizeM = o.SizeM;
            }
            NearFoundedStations(graph, cell, cellKm, maxSizeM);

            foreach (FeatureRockObject obj in _cellObjects)
            {
                if (IsInsideAStationBubble(_nearFounded, obj))
                {
                    record.Suppressed++;
                    if (DebugEnabled)
                        FactionsPlugin.Log.Info("ROCK_SUPPRESSED " + obj.StorageName
                            + " feature='" + feature.Name + "' -- inside a founded station's safe zone.");
                    continue;
                }

                if (ResolveMaterial(obj.MaterialSubTypeId) == null)
                {
                    // Not debug-gated: an authored material this world cannot render is an operator
                    // error that would otherwise present as silently missing rock.
                    WarnUnresolvedMaterialOnce(obj.MaterialSubTypeId, feature.Name);
                    continue;
                }

                record.Rocks.Add(new LiveRock { EntityId = StableVoxelEntityId(obj.StorageName), Plan = obj });
                double radiusKm = obj.SizeM / 2000.0;
                if (radiusKm > _maxRockRadiusKm) _maxRockRadiusKm = radiusKm;
            }

            if (DebugEnabled && (record.Rocks.Count > 0 || record.Suppressed > 0))
                FactionsPlugin.Log.Info("ROCK_CELL feature='" + feature.Name + "' cell=" + cell
                    + " planned=" + record.Rocks.Count + " suppressed=" + record.Suppressed);
            _tPlanCell += Stopwatch.GetTimestamp() - tp;
        }

        private readonly List<FeatureRockObject> _cellObjects = new List<FeatureRockObject>();
        private readonly List<Vec3> _founded = new List<Vec3>();
        private readonly List<Vec3> _nearFounded = new List<Vec3>();
        private long _foundedFrame = -1;

        /// <summary>Fills <see cref="_nearFounded"/> with the founded stations whose bubble could reach a rock of up to
        /// <paramref name="maxSizeM"/> in <paramref name="cell"/>. The founded list is read from the graph once per frame.</summary>
        private void NearFoundedStations(SpatialGraph graph, CellCoord cell, double cellKm, double maxSizeM)
        {
            if (_foundedFrame != _frameNo)
            {
                _foundedFrame = _frameNo;
                _founded.Clear();
                if (graph != null)
                    foreach (SpatialNode node in graph.Nodes.Values)
                        if (node.StableStationId != 0) _founded.Add(node.Position);
            }
            _nearFounded.Clear();
            double reachKm = (StationMaterializer.SafeZoneRadius + maxSizeM + 100.0) / 1000.0;
            for (int i = 0; i < _founded.Count; i++)
                if (CellDistanceKm(cell, cellKm, _founded[i]) < reachKm) _nearFounded.Add(_founded[i]);
        }

        /// <summary>Spawns planned rock <paramref name="index"/> of <paramref name="record"/>. Returns 1 when it spawned
        /// (or adopted a live entity under its id), 0 otherwise -- a dying entity under the same id is retried on a
        /// later pass.</summary>
        private int SpawnOne(CellKey key, MaterializedCell record, int index)
        {
            LiveRock rock = record.Rocks[index];
            if (rock.Spawned) return 0;
            MyVoxelMaterialDefinition material = ResolveMaterial(rock.Plan.MaterialSubTypeId);
            if (material == null) return 0;

            FeatureRockObject obj = rock.Plan;
            if (OverlapsABody(obj)) return 0;   // queued again on a later pass, once the space is clear
            long id = SpawnObject(obj, material);
            if (id == 0) return 0;

            rock.EntityId = id;
            rock.Spawned = true;
            record.Rocks[index] = rock;
            _spawnedRefs.Add(new RockRef(key, index));
            _owned.Add(id);
            Revision++;
            _spawnedWindow++;
            if (DebugEnabled)
                FactionsPlugin.Log.Info(string.Format(
                    "ROCK_SPAWN {0} pos=({1:F2},{2:F2},{3:F2})km size={4:F0}m mat={5} id={6} {7}",
                    obj.StorageName, obj.PositionKm.X, obj.PositionKm.Y, obj.PositionKm.Z,
                    obj.SizeM, obj.MaterialSubTypeId, id,
                    // Read after the spawn returned: Clear() drops the wanted ore but leaves
                    // the accepted set standing until the next rock's Want(). This is the
                    // pool SE was actually left with.
                    FeatureRockMaterialControl.LastPoolSummary()));
            return 1;
        }

        // ---- rock shells -------------------------------------------------------------------------------

        private struct Queued
        {
            public CellKey Key;
            public int Index;
            public double DistKm;
        }

        private struct Hold
        {
            public Vec3 PosKm;
            public double RadiusKm;
            public long UntilTicks;
        }

        private readonly List<RockShellAnchor> _anchors = new List<RockShellAnchor>();
        private readonly List<double> _anchorBounds = new List<double>();
        private readonly List<Queued> _queue = new List<Queued>();
        private readonly List<Hold> _holds = new List<Hold>();
        private readonly List<MyEntity> _movers = new List<MyEntity>();
        private int _queueNext;
        private double _maxRockRadiusKm;
        private int _spawnedWindow, _closedWindow, _keptWindow, _passes, _plannedCellsWindow, _frames, _overHalfMs;
        private long _passTicks, _reportStart, _tDespawn, _tWalk, _tPlanCell, _tSpawn, _tTotal, _tTotalMax;

        /// <summary>A planned rock by its cell and its index in that cell.</summary>
        private struct RockRef : IEquatable<RockRef>
        {
            public readonly CellKey Key;
            public readonly int Index;
            public RockRef(CellKey key, int index) { Key = key; Index = index; }
            public bool Equals(RockRef o) { return Index == o.Index && Key.Equals(o.Key); }
            public override bool Equals(object o) { return o is RockRef && Equals((RockRef)o); }
            public override int GetHashCode() { unchecked { return Key.GetHashCode() * 31 + Index; } }
        }

        /// <summary>Every rock the server has spawned and still owns or holds, so the shell pass walks those rather than
        /// every planned cell.</summary>
        private readonly HashSet<RockRef> _spawnedRefs = new HashSet<RockRef>();
        private readonly List<RockRef> _refScratch = new List<RockRef>();

        /// <summary>The anchors (index >= 0) and holds (bitwise complement of the hold index) whose shell may reach each
        /// cell, rebuilt every pass.</summary>
        private readonly Dictionary<CellCoord, List<int>> _cellAnchors = new Dictionary<CellCoord, List<int>>();
        private readonly Stack<List<int>> _listPool = new Stack<List<int>>();

        private readonly List<RockShellProbe> _probes = new List<RockShellProbe>();
        /// <summary>The holds as they stood when the pass started. <see cref="_holds"/> gains and loses entries on the
        /// frames the pass is spread over, so the indices registered in <see cref="_cellAnchors"/> refer to this copy.</summary>
        private readonly List<Hold> _passHolds = new List<Hold>();
        private readonly List<KeyValuePair<CellCoord, double>> _passCells = new List<KeyValuePair<CellCoord, double>>();
        private readonly Dictionary<CellCoord, double> _cellNearest = new Dictionary<CellCoord, double>();
        private int _passCellNext, _passRefNext, _passSlices = 1;
        private static readonly Comparison<KeyValuePair<CellCoord, double>> NearestCellFirst = (x, y) => x.Value.CompareTo(y.Value);
        private double _passSpawnScale = 1.0;

        /// <summary>Anchors (players, phantoms, and moving grids and characters near one) are re-read and entered into
        /// the cells their shells reach, nearest cell first. <see cref="StepPass"/> then works through those cells and the
        /// spawned set over the next <paramref name="slices"/> frames, so no one frame carries a whole pass. Work scales
        /// with the shells and the spawned set, not the planned set.</summary>
        private void StartPass(IList<Vec3> players, FactionTuning tuning, int slices)
        {
            long t0 = Stopwatch.GetTimestamp();
            RockShellShape shape = RockShellShape.From(tuning);
            _passSpawnScale = 1.0 / Math.Max(1.0, tuning.FeatureRockShellHysteresis);
            _passSlices = Math.Max(1, slices);
            double cellKm = tuning.FeatureRockCellSizeKm;
            CollectAnchors(players, tuning, shape);
            _probes.Clear();
            for (int i = 0; i < _anchors.Count; i++) _probes.Add(new RockShellProbe(_anchors[i], shape));

            foreach (List<int> list in _cellAnchors.Values) { list.Clear(); _listPool.Push(list); }
            _cellAnchors.Clear();
            _cellNearest.Clear();
            for (int i = 0; i < _anchors.Count; i++) Register(_anchors[i].PosKm, _anchorBounds[i] + _maxRockRadiusKm, cellKm, i);
            _passHolds.Clear();
            _passHolds.AddRange(_holds);
            for (int h = 0; h < _passHolds.Count; h++) Register(_passHolds[h].PosKm, _passHolds[h].RadiusKm + _maxRockRadiusKm, cellKm, ~h);

            _passCells.Clear();
            foreach (KeyValuePair<CellCoord, double> kv in _cellNearest) _passCells.Add(kv);
            _passCells.Sort(NearestCellFirst);
            _refScratch.Clear();
            _refScratch.AddRange(_spawnedRefs);
            _passCellNext = _passRefNext = 0;
            _queue.Clear();
            _queueNext = 0;
            _passTicks += Stopwatch.GetTimestamp() - t0;
            _passes++;
        }

        /// <summary>One frame's share of the current pass: planned rock in the next cells that lies inside a shell is
        /// queued, and spawned rock in the next slice of the spawned set that lies outside every shell is closed.</summary>
        private void StepPass(int closeBudget)
        {
            long t0 = Stopwatch.GetTimestamp();
            int cellQuota = (_passCells.Count + _passSlices - 1) / _passSlices;
            for (int c = 0; c < cellQuota && _passCellNext < _passCells.Count; c++, _passCellNext++)
            {
                CellCoord cell = _passCells[_passCellNext].Key;
                long mask;
                if (!_liveMask.TryGetValue(cell, out mask)) continue;
                List<int> ids = _cellAnchors[cell];
                for (int f = 0; f < _features.Count && f < 64; f++)
                {
                    if ((mask & (1L << f)) == 0) continue;
                    var key = new CellKey(f, cell);
                    MaterializedCell record;
                    if (!_live.TryGetValue(key, out record)) continue;
                    for (int j = 0; j < record.Rocks.Count; j++)
                    {
                        LiveRock rock = record.Rocks[j];
                        if (rock.Spawned) continue;
                        double nearest;
                        if (InsideAny(rock, ids, _passSpawnScale, out nearest))
                            _queue.Add(new Queued { Key = key, Index = j, DistKm = nearest });
                    }
                }
            }

            int refQuota = (_refScratch.Count + _passSlices - 1) / _passSlices;
            int closed = 0;
            for (int r = 0; r < refQuota && _passRefNext < _refScratch.Count; r++, _passRefNext++)
            {
                RockRef rr = _refScratch[_passRefNext];
                MaterializedCell record;
                if (!_live.TryGetValue(rr.Key, out record) || rr.Index >= record.Rocks.Count || !record.Rocks[rr.Index].Spawned)
                {
                    _spawnedRefs.Remove(rr);
                    continue;
                }
                List<int> ids;
                double unused;
                if (_cellAnchors.TryGetValue(rr.Key.Cell, out ids) && InsideAny(record.Rocks[rr.Index], ids, 1.0, out unused))
                    continue;
                if (closed >= closeBudget) continue;   // looked at again on the next pass
                CloseOne(rr.Key, record, rr.Index);
                closed++;
            }
            _passTicks += Stopwatch.GetTimestamp() - t0;
        }

        private void Register(Vec3 posKm, double reachKm, double cellKm, int id)
        {
            foreach (CellCoord cell in FeatureRockPlanner.CellsWithin(posKm, reachKm, cellKm))
            {
                double d = CellDistanceKm(cell, cellKm, posKm);
                if (d > reachKm) continue;
                double nearest;
                if (!_cellNearest.TryGetValue(cell, out nearest) || d < nearest) _cellNearest[cell] = d;
                List<int> list;
                if (!_cellAnchors.TryGetValue(cell, out list))
                {
                    list = _listPool.Count > 0 ? _listPool.Pop() : new List<int>(4);
                    _cellAnchors[cell] = list;
                }
                list.Add(id);
            }
        }

        /// <summary>Whether <paramref name="rock"/> lies inside a shell at <paramref name="scale"/> or a hold, of those
        /// listed; <paramref name="nearestKm"/> is its distance to the nearest that contains it.</summary>
        private bool InsideAny(LiveRock rock, List<int> ids, double scale, out double nearestKm)
        {
            Vec3 pos = rock.Plan.PositionKm;
            double radiusKm = rock.Plan.SizeM / 2000.0;
            nearestKm = double.MaxValue;
            for (int k = 0; k < ids.Count; k++)
            {
                int id = ids[k];
                double d;
                if (id >= 0)
                {
                    RockShellProbe a = _probes[id];
                    if (!a.Touches(pos, radiusKm, scale)) continue;
                    d = pos.DistanceTo(a.PosKm);
                }
                else
                {
                    Hold h = _passHolds[~id];
                    d = pos.DistanceTo(h.PosKm);
                    if (d > h.RadiusKm + radiusKm) continue;
                }
                if (d < nearestKm) nearestKm = d;
            }
            return nearestKm < double.MaxValue;
        }

        /// <summary>Closes spawned rock <paramref name="index"/> when it is still pristine and ours. Rock a player has
        /// edited is SE's and is never closed here; rock something stands on is kept until it is not.</summary>
        /// <summary>Closes spawned rock <paramref name="index"/> when it is still pristine and ours. Rock a player has
        /// edited is handed to SE and no longer tracked: it is never closed here, and asking again would repeat SE's
        /// whole-storage <c>IsRangeModified</c> walk every pass. Rock refused for any other reason (something stands on
        /// it, pending writes) is asked again after <see cref="KeptRetryFrames"/>.</summary>
        private void CloseOne(CellKey key, MaterializedCell record, int index)
        {
            LiveRock rock = record.Rocks[index];
            var rr = new RockRef(key, index);
            if (!rock.Spawned) { _spawnedRefs.Remove(rr); return; }
            long retryAt;
            if (_keptUntil.TryGetValue(rock.EntityId, out retryAt) && _frameNo < retryAt) return;
            if (!OwnsPristine(rock.EntityId))
            {
                MyEntity e;
                _spawnedRefs.Remove(rr);
                _owned.Remove(rock.EntityId);
                _keptUntil.Remove(rock.EntityId);
                if (MyEntities.TryGetEntityById(rock.EntityId, out e)) return;   // edited: SE's from here
                rock.Spawned = false;                                            // gone without us
                record.Rocks[index] = rock;
                Revision++;
                return;
            }
            string refusal;
            if (CloseIfPristine(rock.EntityId, out refusal))
            {
                _owned.Remove(rock.EntityId);
                rock.Spawned = false;
                record.Rocks[index] = rock;
                _spawnedRefs.Remove(rr);
                _keptUntil.Remove(rock.EntityId);
                Revision++;
                _closedWindow++;
                return;
            }
            _keptWindow++;
            if (DebugEnabled && refusal != null)
                FactionsPlugin.Log.Info("ROCK_KEPT id=" + rock.EntityId + " -- " + refusal);
            if (IsEditRefusal(refusal))
            {
                // A player's work: SE's reverter owns it, as the despawn walk already decides.
                _spawnedRefs.Remove(rr);
                _owned.Remove(rock.EntityId);
                _keptUntil.Remove(rock.EntityId);
                Revision++;
                _releasedWindow++;
            }
            else _keptUntil[rock.EntityId] = _frameNo + KeptRetryFrames;
        }

        private const int KeptRetryFrames = 600;
        private readonly Dictionary<long, long> _keptUntil = new Dictionary<long, long>();
        private int _releasedWindow;

        private static bool IsEditRefusal(string refusal)
        {
            return refusal != null
                && (refusal == "ContentChanged" || refusal == "CreatedByUser" || refusal == "storage range modified"
                    || refusal.StartsWith("seed released", StringComparison.Ordinal));
        }

        /// <summary>Spawns queued rock nearest first, up to <paramref name="budget"/> rocks and
        /// <see cref="_spawnBudgetTicks"/> of time this frame; at least one when any is queued. The rest waits for the
        /// following frames, so a pass's rock arrives over several frames rather than in one.</summary>
        private void SpawnQueued(int budget)
        {
            int built = 0;
            long start = Stopwatch.GetTimestamp();
            while (_queueNext < _queue.Count && built < budget)
            {
                if (built > 0 && Stopwatch.GetTimestamp() - start >= _spawnBudgetTicks) break;
                Queued q = _queue[_queueNext++];
                MaterializedCell record;
                if (!_live.TryGetValue(q.Key, out record) || q.Index >= record.Rocks.Count) continue;
                built += SpawnOne(q.Key, record, q.Index);
            }
        }

        private void CollectAnchors(IList<Vec3> players, FactionTuning tuning, RockShellShape shape)
        {
            _anchors.Clear();
            _anchorBounds.Clear();
            var still = new Vec3(0, 0, 0);
            if (players != null)
                for (int i = 0; i < players.Count; i++) _anchors.Add(new RockShellAnchor(players[i], still));

            double range = Math.Max(0, tuning.FeatureRockShellAnchorRangeKm);
            if (range > 0 && players != null && players.Count > 0)
            {
                _movers.Clear();
                RockContactPhysics.CollectMovers(_movers);
                double range2 = range * range;
                try
                {
                    foreach (MyEntity e in _movers)
                    {
                        if (!(e is MyCubeGrid) && !(e is MyCharacter)) continue;
                        Vector3D p = e.PositionComp.GetPosition();
                        var posKm = new Vec3(p.X / 1000.0, p.Y / 1000.0, p.Z / 1000.0);
                        bool near = false;
                        for (int i = 0; i < players.Count && !near; i++) near = players[i].DistanceSquaredTo(posKm) <= range2;
                        if (!near) continue;
                        Vector3 v = e.Physics.LinearVelocity;
                        _anchors.Add(new RockShellAnchor(posKm, new Vec3(v.X, v.Y, v.Z)));
                    }
                }
                finally
                {
                    // Scratch only: a grid left here outlives its close until the next pass with a player near.
                    _movers.Clear();
                }
            }

            for (int i = 0; i < _anchors.Count; i++) _anchorBounds.Add(RockShell.BoundingRadiusKm(_anchors[i], shape, 1.0));
        }

        /// <summary>
        /// Spawns the planned rock within <see cref="FactionTuning.FeatureRockPlacementRadiusM"/> of a point before a
        /// free-space probe runs there, and keeps it for <see cref="FactionTuning.FeatureRockPlacementHoldSeconds"/>.
        /// With rock shells on, rock away from every body does not exist on the server, so a probe for a respawn or a
        /// fleet spawn would find space free that a linked client draws rock in. Synchronous and capped at
        /// <see cref="FactionTuning.FeatureRockPlacementMaxSpawns"/>.
        /// </summary>
        public int EnsureAround(Vec3 posKm, FactionTuning tuning)
        {
            if (tuning == null) return 0;
            if (CreateAsteroidShapeM == null || _features.Count == 0 || MySession.Static == null) return 0;
            double cellKm = tuning.FeatureRockCellSizeKm;
            double radiusKm = Math.Max(0, tuning.FeatureRockPlacementRadiusM) / 1000.0;
            if (cellKm <= 0 || radiusKm <= 0) return 0;

            long now = Stopwatch.GetTimestamp();
            for (int h = 0; h < _holds.Count; h++)
                if (_holds[h].UntilTicks > now && _holds[h].PosKm.DistanceTo(posKm) < 0.1) return 0;

            int max = Math.Max(0, tuning.FeatureRockPlacementMaxSpawns), built = 0;
            foreach (CellCoord cell in FeatureRockPlanner.CellsWithin(posKm, radiusKm + _maxRockRadiusKm, cellKm))
            {
                for (int i = 0; i < _features.Count; i++)
                {
                    var key = new CellKey(i, cell);
                    MaterializedCell record;
                    if (!_live.TryGetValue(key, out record))
                    {
                        if (!FeatureRockPlanner.CellCouldTouch(_features[i], _centers[i], cell, cellKm)) continue;
                        PlanCell(key, _features[i], _centers[i], cell, _graph, tuning, cellKm);
                        record = _live[key];
                    }
                    for (int j = 0; j < record.Rocks.Count && built < max; j++)
                    {
                        LiveRock rock = record.Rocks[j];
                        if (rock.Spawned) continue;
                        if (rock.Plan.PositionKm.DistanceTo(posKm) > radiusKm + rock.Plan.SizeM / 2000.0) continue;
                        built += SpawnOne(key, record, j);
                    }
                }
            }

            _holds.Add(new Hold
            {
                PosKm = posKm,
                RadiusKm = radiusKm,
                UntilTicks = now + (long)(Math.Max(0, tuning.FeatureRockPlacementHoldSeconds) * Stopwatch.Frequency),
            });
            if (DebugEnabled)
                FactionsPlugin.Log.Info("ROCK_PLACEMENT pos=" + posKm + " radiusKm=" + radiusKm.ToString("0.0", CultureInfo.InvariantCulture)
                    + " spawned=" + built);
            return built;
        }

        private static readonly List<MyEntity> _overlapBuffer = new List<MyEntity>();

        /// <summary>A grid or character inside the rock's bounding box. With rock shells on, rock appears around bodies
        /// as they move; spawning it onto one would have Havok push the two apart.</summary>
        private static bool OverlapsABody(FeatureRockObject obj)
        {
            double half = obj.SizeM / 2.0;
            var c = new Vector3D(obj.PositionKm.X * 1000.0, obj.PositionKm.Y * 1000.0, obj.PositionKm.Z * 1000.0);
            var box = new BoundingBoxD(c - new Vector3D(half), c + new Vector3D(half));
            _overlapBuffer.Clear();
            MyGamePruningStructure.GetTopMostEntitiesInBox(ref box, _overlapBuffer, MyEntityQueryType.Both);
            for (int i = 0; i < _overlapBuffer.Count; i++)
                if (_overlapBuffer[i] is MyCubeGrid || _overlapBuffer[i] is MyCharacter) return true;
            return false;
        }

        private void ExpireHolds()
        {
            if (_holds.Count == 0) return;
            long now = Stopwatch.GetTimestamp();
            for (int i = _holds.Count - 1; i >= 0; i--)
                if (_holds[i].UntilTicks <= now) _holds.RemoveAt(i);
        }

        private readonly List<Vec3> _keepers = new List<Vec3>();

        private IList<Vec3> WithHolds(IList<Vec3> players)
        {
            if (_holds.Count == 0) return players;
            _keepers.Clear();
            if (players != null) for (int i = 0; i < players.Count; i++) _keepers.Add(players[i]);
            for (int i = 0; i < _holds.Count; i++) _keepers.Add(_holds[i].PosKm);
            return _keepers;
        }

        private string PerFrame(long ticks)
        {
            return _frames > 0
                ? (ticks * 1000.0 / Stopwatch.Frequency / _frames).ToString("0.000", CultureInfo.InvariantCulture)
                : "-";
        }

        private void ReportShell()
        {
            long now = Stopwatch.GetTimestamp();
            if (_reportStart == 0) { _reportStart = now; return; }
            if (now - _reportStart < 10 * Stopwatch.Frequency) return;
            double passMs = _passes > 0 ? _passTicks * 1000.0 / Stopwatch.Frequency / _passes : 0;
            FactionsPlugin.Log.Info("ROCK_SHELL anchors=" + _anchors.Count + " holds=" + _holds.Count
                + " cells=" + _live.Count + " planned=" + PlannedObjects + " spawned=" + LiveObjects
                + " queued=" + Math.Max(0, _queue.Count - _queueNext)
                + " spawnedWin=" + _spawnedWindow + " closedWin=" + _closedWindow + " keptWin=" + _keptWindow
                + " releasedWin=" + _releasedWindow
                + " passMsAvg=" + passMs.ToString("0.00", CultureInfo.InvariantCulture) + " passes=" + _passes
                + " cellsPlanned=" + _plannedCellsWindow
                + " msPerFrame:despawn=" + PerFrame(_tDespawn) + ",walk=" + PerFrame(Math.Max(0, _tWalk - _tPlanCell))
                + ",plan=" + PerFrame(_tPlanCell) + ",pass=" + PerFrame(_passTicks) + ",spawn=" + PerFrame(_tSpawn)
                + ",total=" + PerFrame(_tTotal)
                + " totalMaxMs=" + (_tTotalMax * 1000.0 / Stopwatch.Frequency).ToString("0.00", CultureInfo.InvariantCulture)
                + " over0.5ms=" + _overHalfMs + " frames=" + _frames);
            _reportStart = now;
            _spawnedWindow = _closedWindow = _keptWindow = _releasedWindow = _passes = _plannedCellsWindow = _frames = _overHalfMs = 0;
            _passTicks = _tDespawn = _tWalk = _tPlanCell = _tSpawn = _tTotal = _tTotalMax = 0;
        }

        /// <summary>
        /// The one place nodes influence rock: an object may not spawn inside a founded station's
        /// safe zone.
        ///
        /// <para>Gated on <see cref="SpatialNode.StableStationId"/>, not on the node existing. Nodes
        /// are placed where the geography says ore is, so suppressing rock at every node would empty
        /// exactly the coordinates a scanner sends a player to mine.</para>
        ///
        /// <para>The margin is generous rather than a tight tangent: vanilla's CSG shapes are not
        /// spheres, and torus or capsule lobes protrude close to the full <c>size</c> from centre.
        /// Confirmed live 2026-08-16, when a tighter margin let an asteroid obstruct a station's
        /// spawn point and silently soft-lock it.</para>
        /// </summary>
        private static bool IsInsideAStationBubble(List<Vec3> foundedStations, FeatureRockObject obj)
        {
            double clearanceKm = (StationMaterializer.SafeZoneRadius + obj.SizeM + 100.0) / 1000.0;
            double r2 = clearanceKm * clearanceKm;
            for (int i = 0; i < foundedStations.Count; i++)
                if (foundedStations[i].DistanceSquaredTo(obj.PositionKm) < r2) return true;
            return false;
        }

        private long SpawnObject(FeatureRockObject obj, MyVoxelMaterialDefinition material)
        {
            // The authored material is imposed here, while the shape is built -- see
            // FeatureRockMaterialControl for why this is the only affordable lever and why it cannot
            // disturb the geometry. Cleared in a finally: a leaked value repaints later rock.
            // Registered as well as scoped. The explicit scope covers this call; the registration
            // covers every LATER regeneration of the same rock -- SE rebuilding it from its own
            // storage after an edit or a reload, where nothing of ours is on the stack. Without it
            // the ore silently reverts to vanilla's mix on first drill (measured live 2026-08-25).
            FeatureRockMaterialControl.Register(obj.ShapeSeed, obj.MaterialSubTypeId);

            object shapeProvider;
            long appliedBefore = FeatureRockMaterialControl.TimesApplied;
            FeatureRockMaterialControl.Want(obj.MaterialSubTypeId);

            // SE reads DepositsCountCoefficient/DepositSizeDenominator straight off the session
            // while it builds the rock, so an authored per-material value is applied by scoping the
            // setting across that one call and restoring it afterwards. Safe because generation is
            // eager and synchronous (MyCompositeShapeProvider.InitFromState invokes the generator
            // delegate directly), and because ProceduralDensity is 0 on a feature world -- nothing
            // else is generating asteroids to catch the altered value.
            MyObjectBuilder_SessionSettings settings = MySession.Static.Settings;
            float countBefore = settings.DepositsCountCoefficient;
            float sizeBefore = settings.DepositSizeDenominator;
            try
            {
                if (obj.DepositCountCoefficient.HasValue)
                    settings.DepositsCountCoefficient = (float)obj.DepositCountCoefficient.Value;
                if (obj.DepositSizeDenominator.HasValue && obj.DepositSizeDenominator.Value > 0)
                    settings.DepositSizeDenominator = (float)obj.DepositSizeDenominator.Value;
                // The 4th argument is the generator VERSION, not an index. Passing 0 selected a
                // pre-v3 generator whose MyCompositeShapes constructor returns before it builds any
                // material list -- so FilterKindDuplicates was never called and material control,
                // though reported ACTIVE, could never run. Found live 2026-08-25 when authored ice
                // bands still came out holding every ore.
                shapeProvider = CreateAsteroidShapeM.Invoke(
                    null, new object[] { obj.ShapeSeed, (float)obj.SizeM, 0, (int?)_generatorVersion });
            }
            finally
            {
                // Restored unconditionally: a leaked setting would silently retune every asteroid
                // the world generates afterwards, including other features' rock.
                settings.DepositsCountCoefficient = countBefore;
                settings.DepositSizeDenominator = sizeBefore;
                FeatureRockMaterialControl.Clear();
            }

            // Checked per rock, not globally. A global "has it ever fired" counter is exactly the
            // check that stayed green while this rock was built by a second fill path the patch
            // never reached.
            WarnIfControlDidNotFireFor(obj, appliedBefore);

            var dataProvider = shapeProvider as IMyStorageDataProvider;
            if (dataProvider == null) return 0;

            var voxelSize = new Vector3I(Math.Max(64, (int)Math.Ceiling(obj.SizeM)));
            MyStorageBase storage = new MyOctreeStorage(dataProvider, voxelSize);

            var centerM = new Vector3D(obj.PositionKm.X * 1000.0, obj.PositionKm.Y * 1000.0, obj.PositionKm.Z * 1000.0);
            Vector3D minCorner = centerM - new Vector3D(MathHelper.GetNearestBiggerPowerOfTwo((float)obj.SizeM) / 2.0);

            // Stable id derived from the name, exactly as vanilla's generator does. It makes the
            // spawn idempotent: rock that survived in the save reloads under the same id, so a
            // returning player adopts it rather than stacking a duplicate on top of it.
            long entityId = StableVoxelEntityId(obj.StorageName);
            MyEntity existing;
            if (MyEntities.TryGetEntityById(entityId, out existing, allowClosed: true))
            {
                // Close() is deferred, so a cell despawned and re-approached a moment later still
                // finds its own dying entity registered. Adopting that produces a record pointing at
                // a rock the player never sees, and -- because the adopted entity's IsSeedOpen was
                // never re-asserted -- one that reads as player-edited forever and is never reclaimed.
                // Measured live 2026-08-25: 21 rocks "kept" against a single real edit.
                var live = existing as MyVoxelBase;
                if (live == null || existing.Closed) return 0;   // let the next pass retry cleanly

                // A genuine adoption -- rock that survived in the save, or a cell rebuilt while its
                // entity was still alive. Re-assert ownership unless a player has already edited it.
                if (live.IsSeedOpen.HasValue && live.IsSeedOpen.Value) return entityId;
                // A rock reloaded from the save arrives with IsSeedOpen null -- the field is runtime
                // only. Reclaiming it here is what stops reloaded rock being unmanaged forever:
                // never despawned, never purged, and indistinguishable from a player's own work.
                if (!live.ContentChanged && !live.CreatedByUser) live.IsSeedOpen = true;
                return entityId;
            }

            MyVoxelBase voxel;
            RockContactPhysics.SpawningPristine = true;
            try { voxel = MyWorldGenerator.AddVoxelMap(obj.StorageName, storage, minCorner, entityId); }
            finally { RockContactPhysics.SpawningPristine = false; }
            if (voxel == null) return 0;
            RockRetention.Track(voxel, storage, shapeProvider);

            // Save is deliberately LEFT TRUE, and that is not an oversight.
            //
            // MyVoxelReplicable.ShouldReplicate refuses any voxel that is simultaneously !Save,
            // !ContentChanged and !BeforeContentChanged -- exactly what a pristine rock is. Clearing
            // Save therefore makes rock that exists on the server, collides, and never reaches a
            // single client: solid and invisible. Confirmed live 2026-08-25.
            //
            // Vanilla clears it on its own procedural asteroids and gets away with it because
            // MyProceduralAsteroidCellGenerator runs on the CLIENT too and regenerates the same rock
            // locally from the same seed. Ours has no client-side counterpart, so replication is the
            // only way it can be seen. Save size is bounded by the despawn lifecycle instead: only
            // rock currently near a player is materialised, so only that is ever written.

            // SE's trash reverter skips a voxel whose seed is open (MySessionComponentTrash:932),
            // which is what stops it reclaiming pristine rock we still own and intend to despawn
            // ourselves. Released on first edit below, handing edited rock to it deliberately.
            voxel.IsSeedOpen = true;

            // Without this SE re-sends the entire storage blob on every stream-in, forever.
            // MyVoxelReplicable.Serialize ends its cache test with an unconditional
            // `sendContent |= Voxel.AsteroidName == null;`, and AddVoxelMap never sets the field --
            // so its own per-client cache (MyMultiplayer.Static.VoxelMapData, keyed on StorageName)
            // can never register a hit. Vanilla never meets this because ShouldReplicate refuses its
            // pristine asteroids long before Serialize runs. Found by the Factions-UI session,
            // 2026-08-25, while measuring FAC-090; it costs one line and helps a vanilla client too.
            voxel.AsteroidName = obj.StorageName;

            MyStorageBase storageRef = storage;
            MyVoxelBase captured = voxel;
            storageRef.RangeChanged += (min, max, flags) =>
            {
                // First edit of any cause: the rock stops being ours. Releasing the seed is what
                // lets SE's own reverter reclaim the player's spoil heap later.
                if (!captured.IsSeedOpen.HasValue) return;
                captured.IsSeedOpen = null;
                // It also stops being rock a client may derive: from here the geometry is the
                // player's, not the seed's, so the gate must let it replicate again.
                _owned.Remove(captured.EntityId);
                if (DebugEnabled)
                    FactionsPlugin.Log.Info("ROCK_EDITED " + captured.StorageName
                        + " -- first edit; released to SE's reverter, no longer ours to despawn.");
            };

            return voxel.EntityId;
        }

        /// <summary>Drops cells no player can see. Pristine rock only: anything a player has touched
        /// has already promoted itself out of our bookkeeping and belongs to SE's reverter.</summary>
        private void DespawnOutOfRange(IList<Vec3> players, double keepRadiusKm, double cellKm)
        {
            if (_live.Count == 0) return;

            List<CellKey> drop = null;
            foreach (KeyValuePair<CellKey, MaterializedCell> kv in _live)
            {
                if (AnyPlayerWithin(players, kv.Key.Cell, keepRadiusKm, cellKm)) continue;
                if (drop == null) drop = new List<CellKey>();
                drop.Add(kv.Key);
            }
            if (drop == null) return;

            foreach (CellKey key in drop)
            {
                MaterializedCell record = _live[key];
                _live.Remove(key);
                MaskRemove(key);

                int closed = 0, kept = 0;
                for (int j = 0; j < record.Rocks.Count; j++)
                {
                    LiveRock rock = record.Rocks[j];
                    if (!rock.Spawned) continue;
                    _spawnedRefs.Remove(new RockRef(key, j));
                    // Dropped from the owned set whether or not it actually closed. A rock we kept is
                    // one a player has claimed by editing or standing on; it is SE's from here, and
                    // leaving it marked ours would keep withholding it from the very client that
                    // just changed it.
                    _owned.Remove(rock.EntityId);
                    Revision++;

                    string refusal;
                    if (CloseIfPristine(rock.EntityId, out refusal)) closed++;
                    else
                    {
                        kept++;
                        if (DebugEnabled && refusal != null)
                            FactionsPlugin.Log.Info("ROCK_KEPT id=" + rock.EntityId + " cell=" + key.Cell
                                + " -- " + refusal);
                    }
                }

                if (DebugEnabled && (closed > 0 || kept > 0))
                    FactionsPlugin.Log.Info(string.Format(
                        "ROCK_DESPAWN feature='{0}' cell={1} closed={2} kept={3}",
                        _features[key.FeatureIndex].Name, key.Cell, closed, kept));
            }
        }

        /// <summary>Re-arms the reclaim sweep. Called from <see cref="SetFeatures"/>, which runs once
        /// at session load, so the sweep runs once per session -- and again if the world's features
        /// are ever reloaded, which is exactly when rock built under an older storage name stops
        /// being nameable and starts being unreachable.</summary>
        private void ArmReclaim()
        {
            _reclaimArmed = true;
            _reclaimQueue = null;
            _reclaimIndex = 0;
            _reclaimClosed = 0;
            _reclaimKept = 0;
        }

        private bool _reclaimArmed;
        private List<long> _reclaimQueue;
        private int _reclaimIndex;
        private int _reclaimClosed;
        private int _reclaimKept;

        /// <summary>Rock still waiting to be examined by the reclaim sweep, for the bridge.</summary>
        public int ReclaimPending
        {
            get { return _reclaimQueue == null ? 0 : _reclaimQueue.Count - _reclaimIndex; }
        }

        /// <summary>
        /// Closes feature rock that belongs to no live record, a bounded slice per tick. Returns true
        /// while it still has work, which is the caller's signal to spawn nothing this tick.
        ///
        /// <para><b>Why this exists.</b> Rock is written into the save -- <c>Save</c> is deliberately
        /// true, see <see cref="SpawnObject"/> -- but the record that owns it is not. <c>_live</c> and
        /// <c>_owned</c> are in-memory, so every rock that was materialised when the process stopped
        /// comes back as a world entity that <see cref="DespawnOutOfRange"/> cannot see: it walks
        /// <c>_live</c>, which is empty. The only way back into management was a player re-entering
        /// that exact cell, and cells nobody revisits accumulated for good. Measured on the live save
        /// 2026-09-05: 2,541 rocks, 2,535 of them untouched, the oldest first written nine days and
        /// ~144 restarts earlier.</para>
        ///
        /// <para><b>Why it closes rather than adopts.</b> Pristine rock is a pure function of its
        /// cell, so closing it costs nothing that the next approach does not rebuild -- and closing
        /// needs one predicate where adoption needs the whole plan reconstructed. It also settles the
        /// case adoption cannot reach at all: rock whose storage name no longer matches what the
        /// planner would produce (the name carries the material's content tag) can never be adopted
        /// by id, only duplicated beside itself. 21 cells in the live save held two generations of
        /// rock for that reason.</para>
        ///
        /// <para>Rock this session spawned is skipped outright, and a player's own work is refused by
        /// the same test everything else uses.</para>
        /// </summary>
        private bool ReclaimReloadedRock(FactionTuning tuning)
        {
            if (!_reclaimArmed) return false;

            int budget = Math.Max(0, tuning.FeatureRockReclaimPerTick);
            if (budget == 0) { _reclaimArmed = false; return false; }   // 0 disables reclamation

            if (_reclaimQueue == null)
            {
                _reclaimQueue = new List<long>();
                foreach (MyVoxelBase v in MySession.Static.VoxelMaps.Instances)
                {
                    if (v == null || v is MyPlanet) continue;
                    if (v.StorageName == null || !v.StorageName.StartsWith("fr", StringComparison.Ordinal)) continue;
                    _reclaimQueue.Add(v.EntityId);
                }
            }

            int examined = 0;
            while (_reclaimIndex < _reclaimQueue.Count && examined < budget)
            {
                long id = _reclaimQueue[_reclaimIndex++];
                examined++;

                if (_owned.Contains(id)) continue;   // spawned this session; its cell owns it

                string refusal;
                // The seed veto is dropped here and nowhere else: a rock reloaded from the save
                // reads IsSeedOpen null whether or not anyone ever touched it.
                if (CloseIfPristine(id, false, out refusal)) _reclaimClosed++;
                else
                {
                    _reclaimKept++;
                    if (DebugEnabled && refusal != null)
                        FactionsPlugin.Log.Info("ROCK_RECLAIM_KEPT id=" + id + " -- " + refusal);
                }
            }

            if (_reclaimIndex < _reclaimQueue.Count) return true;

            _reclaimArmed = false;
            // One line per session, not per rock, and deliberately not debug-gated: this closes world
            // entities, and a silent bulk close is worse than a noisy one. Torch log.
            if (_reclaimQueue.Count > 0)
                FactionsPlugin.Log.Info("Feature rock reclaim: examined " + _reclaimQueue.Count
                    + " reloaded rock(s), closed " + _reclaimClosed + ", kept " + _reclaimKept
                    + " (edited, occupied, or ours this session). Closure completes on a later tick.");
            return false;
        }

        /// <summary>Closes one voxel if nothing has touched it.
        /// <para><c>ContentChanged</c> is deliberately not the primary test. Live asteroid
        /// <c>N4715_ast3</c> carried <c>ContentChanged=false</c> while its storage held six real
        /// octree nodes; despawning on that flag would have regenerated it, restoring ore a player
        /// had removed. <see cref="MyStorageBase.IsRangeModified"/> reads octree structure instead —
        /// only writes subdivide, reads never do — and fails safe. The flags remain as extra vetoes
        /// because the write cache can hide a just-drilled region from the structural test.</para></summary>
        /// <summary>Returns true when the voxel was closed. <paramref name="refusal"/> names the
        /// veto that stopped it — which is the whole diagnostic value during a test run, since every
        /// refusal here is otherwise indistinguishable from "nothing happened".</summary>
        private static bool CloseIfPristine(long entityId, out string refusal)
        {
            return CloseIfPristine(entityId, true, out refusal);
        }

        /// <summary><paramref name="requireOpenSeed"/> false drops the seed veto, and only the
        /// reclaim sweep may do that. <c>IsSeedOpen</c> is runtime-only: every rock reloaded from the
        /// save reads null, which is indistinguishable from a seed released by an edit. Trusting it
        /// there is what made the first version of <see cref="PurgeAll"/> call 181 untouched rocks
        /// edited and reclaim nothing. The persisted signals and the structural test below still
        /// have to agree before anything closes.</summary>
        private static bool CloseIfPristine(long entityId, bool requireOpenSeed, out string refusal)
        {
            refusal = null;
            MyEntity entity;
            if (!MyEntities.TryGetEntityById(entityId, out entity)) { refusal = "already gone"; return false; }
            var voxel = entity as MyVoxelBase;
            if (voxel == null) { refusal = "not a voxel"; return false; }

            // Save is true on every rock we spawn (it must be, or it never replicates), so it cannot
            // serve as the edited test the way it does for vanilla's own asteroids. The released seed
            // is our marker instead, backed by the structural check below.
            if (requireOpenSeed && (!voxel.IsSeedOpen.HasValue || !voxel.IsSeedOpen.Value))
                { refusal = "seed released by an edit -- SE's reverter owns it"; return false; }
            if (voxel.ContentChanged) { refusal = "ContentChanged"; return false; }
            if (voxel.CreatedByUser) { refusal = "CreatedByUser"; return false; }

            MyStorageBase storage = voxel.Storage as MyStorageBase;
            if (storage == null)          { refusal = "no storage -- cannot prove it is untouched"; return false; }
            if (storage.HasPendingWrites) { refusal = "pending writes"; return false; }

            var whole = new BoundingBoxI(Vector3I.Zero, storage.Size - Vector3I.One);
            if (storage.IsRangeModified(ref whole)) { refusal = "storage range modified"; return false; }

            // Mirrors vanilla's own refusal (VoxelRevertor_CanRevertCurrent): nothing crashes if a
            // grid or character is standing on it, but a landed ship starts drifting and a standing
            // character starts falling.
            if (AnythingStandingOn(voxel)) { refusal = "a grid or character is on it"; return false; }

            voxel.Close();                       // deferred and cheap; safe on the update thread
            return true;
        }

        private static bool _warnedControlNeverFired;

        /// <summary>Says out loud, once, when rock is spawning but the authored ore is not reaching
        /// it. Not debug-gated: "control is installed but never runs" is exactly the state that hid
        /// for a whole live session behind an ACTIVE flag, and it is invisible from a log that only
        /// records the ore we asked for.</summary>
        private static void WarnIfControlDidNotFireFor(FeatureRockObject obj, long appliedBefore)
        {
            if (_warnedControlNeverFired) return;
            if (!FeatureRockMaterialControl.Active) { _warnedControlNeverFired = true; return; }
            if (FeatureRockMaterialControl.TimesApplied > appliedBefore) return;   // fired for THIS rock

            _warnedControlNeverFired = true;
            FactionsPlugin.Log.Warn("FeatureRockMaterialControl: installed, but withheld nothing while building "
                + obj.StorageName + " (wanted=" + obj.MaterialSubTypeId
                + ", generatorVersion=" + GeneratorVersion + "). That rock carries vanilla's global ore mix.");
        }

        private static readonly HashSet<string> _warnedMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void WarnUnresolvedMaterialOnce(string subTypeId, string featureName)
        {
            string key = (subTypeId ?? "(null)") + "|" + featureName;
            if (!_warnedMaterials.Add(key)) return;
            FactionsPlugin.Log.Warn("FeatureRockMaterializer: feature '" + featureName + "' names material '"
                + subTypeId + "', which no loaded voxel material mines -- that rock will not spawn.");
        }

        private static readonly List<MyEntity> _proximityBuffer = new List<MyEntity>();

        private static bool AnythingStandingOn(MyVoxelBase voxel)
        {
            BoundingBoxD box = voxel.PositionComp.WorldAABB;
            box.Inflate(ProximityRefusalMarginM);

            _proximityBuffer.Clear();
            MyGamePruningStructure.GetTopMostEntitiesInBox(ref box, _proximityBuffer);
            try
            {
                foreach (MyEntity e in _proximityBuffer)
                    if (e is MyCubeGrid || e is MyCharacter) return true;
                return false;
            }
            finally { _proximityBuffer.Clear(); }
        }

        /// <summary>Margin around a rock within which a grid or character vetoes unloading it. A
        /// ship length, not a whole sync ring: the concern is something physically resting on the
        /// voxel, not anything that happens to be in the neighbourhood.</summary>
        private const double ProximityRefusalMarginM = 300.0;

        private static bool AnyPlayerWithin(IList<Vec3> players, CellCoord cell, double radiusKm, double cellKm)
        {
            if (players == null || players.Count == 0) return false;
            for (int i = 0; i < players.Count; i++)
                if (CellDistanceKm(cell, cellKm, players[i]) <= radiusKm) return true;
            return false;
        }

        /// <summary>Distance from a point to the nearest face of a cell, in km. Zero inside it.</summary>
        private static double CellDistanceKm(CellCoord cell, double cellKm, Vec3 posKm)
        {
            double loX = cell.X * cellKm, hiX = loX + cellKm;
            double loY = cell.Y * cellKm, hiY = loY + cellKm;
            double loZ = cell.Z * cellKm, hiZ = loZ + cellKm;

            double dx = posKm.X < loX ? loX - posKm.X : (posKm.X > hiX ? posKm.X - hiX : 0);
            double dy = posKm.Y < loY ? loY - posKm.Y : (posKm.Y > hiY ? posKm.Y - hiY : 0);
            double dz = posKm.Z < loZ ? loZ - posKm.Z : (posKm.Z > hiZ ? posKm.Z - hiZ : 0);
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>The world's own replication distance. Read from the session rather than tuned
        /// here so Factions and SE cannot disagree about how far "near" is.</summary>
        private static double SessionSyncDistanceM()
        {
            try
            {
                double v = MySession.Static.Settings.SyncDistance;
                return v > 0 ? v : DefaultSyncDistanceM;
            }
            catch { return DefaultSyncDistanceM; }
        }

        private static double SessionViewDistanceM()
        {
            try
            {
                double v = MySession.Static.Settings.ViewDistance;
                return v > 0 ? v : DefaultViewDistanceM;
            }
            catch { return DefaultViewDistanceM; }
        }

        /// <summary>The world's content seed. A hardcoded literal today — the authored
        /// <c>WorldSeed</c> never reaches runtime (FAC-088). One argument changes when that lands;
        /// everything downstream already derives from whatever this returns.</summary>
        private static int WorldSeed() { return 20260606; }

        /// <summary>Entity id derived from the storage name, in SE's own voxel id range -- the same
        /// shape as <c>MyProceduralAsteroidCellGenerator.GetAsteroidEntityId</c>. Stable across
        /// processes, so a given rock always reclaims the same id and a reload cannot duplicate it.
        /// FNV-1a rather than SE's own string hash, so the value cannot drift with the engine.</summary>
        internal static long StableVoxelEntityId(string storageName)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                if (!string.IsNullOrEmpty(storageName))
                    for (int i = 0; i < storageName.Length; i++)
                    {
                        h ^= storageName[i];
                        h *= 1099511628211UL;
                    }
                return (long)((h & 0xFFFFFFFFFFFFFFUL) | 0x600000000000000UL);
            }
        }

        private static MyVoxelMaterialDefinition ResolveMaterial(string subTypeId)
        {
            if (string.IsNullOrEmpty(subTypeId)) return null;
            if (_materialsBySubType == null) BuildMaterialIndex();
            List<MyVoxelMaterialDefinition> candidates;
            if (!_materialsBySubType.TryGetValue(subTypeId, out candidates) || candidates.Count == 0) return null;
            return candidates[0]; // deterministic: the pick must not vary between regenerations
        }

        private static void BuildMaterialIndex()
        {
            var index = new Dictionary<string, List<MyVoxelMaterialDefinition>>(StringComparer.OrdinalIgnoreCase);
            foreach (MyVoxelMaterialDefinition def in MyDefinitionManager.Static.GetVoxelMaterialDefinitions())
            {
                if (!def.SpawnsInAsteroids || string.IsNullOrEmpty(def.MinedOre)) continue;
                List<MyVoxelMaterialDefinition> list;
                if (!index.TryGetValue(def.MinedOre, out list))
                    index[def.MinedOre] = list = new List<MyVoxelMaterialDefinition>();
                list.Add(def);
            }
            // Sorted so "the first candidate" is a stable answer across boots, not definition order.
            foreach (List<MyVoxelMaterialDefinition> list in index.Values)
                list.Sort((a, b) => string.CompareOrdinal(a.Id.SubtypeName, b.Id.SubtypeName));
            _materialsBySubType = index;
        }
    }
}
