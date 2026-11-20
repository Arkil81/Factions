using System;
using System.Collections.Generic;
using Factions.Core;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;   // MyGridPhysics
using Sandbox.Game.GameSystems;   // MyGravityProviderSystem
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;

namespace Factions
{
    /// <summary>What put a grid in the world. The dimension the sim-rate question turns on.</summary>
    internal enum GridOrigin
    {
        /// <summary>A Factions fleet grid under the terminal-approach autopilot — the manoeuvring
        /// half of the hybrid flight model, which is the most physics-active state one of our ships
        /// reaches.</summary>
        FleetApproach = 0,
        /// <summary>Every other materialised Factions fleet grid: cruising, parked at a node, or
        /// holding.
        /// <para>Deliberately not called "idle". <c>FleetFlightController.IsControlling</c> knows
        /// only about terminal approach, so a ship at cruise — the ordinary state of a Haul fleet in
        /// transit — is not in it. The first live census read <c>15/15 moving</c> in this bucket,
        /// which a bucket named for being at rest would have flatly contradicted. Whether a grid is
        /// moving is the <c>moving</c> field's job, in every bucket.</para></summary>
        Fleet = 1,
        /// <summary>A station grid — one this plugin materialised for a founded node, or one an SE
        /// faction claims as a station. Both are answered by <c>FactionRuntime.IsStationGrid</c>, and
        /// the wider reading is the right one here: a station is static either way, so what this
        /// bucket is really reporting is the part of the population Havok is not integrating.</summary>
        Station = 2,
        /// <summary>NPC-spawned, but with no fleet record behind it: a turret or rotor subgrid off
        /// one of our hulls, debris it shed, ordnance in flight, or a hull whose record this process
        /// never wrote because it predates the last restart.
        ///
        /// <para>These are the bulk of the world by count and they are emphatically not the
        /// player's. Live 2026-09-21: 12 <c>[NPC] PDC Type 1a GAT</c> and 12 <c>[NPC] PDGT</c>
        /// turret subgrids were all being reported as <c>player</c>.</para></summary>
        NpcLoose = 3,
        /// <summary>A real player's grid — judged on <c>BigOwners</c>, the identities tied for the
        /// most owned functional blocks, never on <c>SmallOwners</c>. See
        /// <see cref="GridCensus.Classify"/> for why that distinction is the whole bug.</summary>
        Player = 4,
        /// <summary>Everything else: unowned wrecks, and vanilla content carrying none of the three
        /// NPC signals.</summary>
        Other = 5,
    }

    /// <summary>
    /// Counts the materialised world by what put each grid there, so a sim-rate dip can be attributed
    /// rather than guessed at.
    ///
    /// <para><b>Why a census and not an event count.</b> Measured on 2026-09-21, tick rate correlates
    /// with the standing population of grids (r = -0.40 against <c>GridsCount</c>) and not at all
    /// with spawn and despawn events in the same window (r = -0.02): a window with six spawns and
    /// nine despawns held a flat 60 tick/s, while windows that merely *had* 150-200 grids standing
    /// ran at 39-47. Counting what happened would have measured the wrong thing.</para>
    ///
    /// <para><b>Physics cost is not divisible by grid.</b> Havok steps a whole world at once, so
    /// there is no per-grid share of the step to read — which is why this reports the physics
    /// *inputs* per origin (how many are moving, how many blocks, how much mass) rather than a
    /// per-grid millisecond figure that would have to be invented. <see cref="EntityCostPatches"/>
    /// measures the per-grid cost that genuinely is separable, the entity updates.</para>
    ///
    /// <para><b>Cost.</b> One walk of the entity list per <c>CensusSeconds</c>, reading already-cached
    /// properties. It runs on the game thread because the entity list is not safe to walk off it.</para>
    /// </summary>
    internal sealed class GridCensus
    {
        internal const int OriginCount = 6;

        /// <summary>Natural gravity past this counts as "in gravity", as a fraction of 1 g. SE's own
        /// cutoff for the edge of a gravity well is 0.05 g
        /// (<c>MySphericalNaturalGravityComponent.CalculateGravityLimit</c>), so this is the same
        /// boundary the world generator uses rather than a number chosen here.</summary>
        private const double GravityFloorG = 0.05;

        /// <summary>A grid counts as moving past this, in m/s. Above a drifting rounding error and
        /// well below anything under power — the distinction being drawn is "Havok is integrating
        /// this" versus "this is parked", not a speed band.</summary>
        private const double MovingSpeedMs = 0.5;

        internal sealed class Bucket
        {
            public int Grids;
            public int Moving;
            public int Static;
            /// <summary>Grids further than the observation radius from every online player.
            /// <para>The census walks SE's whole entity list, so it has always <i>seen</i> these —
            /// but nothing said how many there were, and "materialised far from anyone" is the shape
            /// a leak takes: our own spawn/despawn is driven by player proximity, so a grid that is
            /// both ours and far from everybody is one the despawn did not collect.</para></summary>
            public int FarFromPlayer;
            /// <summary>Grids in this origin whose dominant owner is a player identity.
            ///
            /// <para><b>Ownership is a second axis, not a value of the first.</b> Operator,
            /// 2026-09-21: <i>"my Factions grids appear as owned by me, so they're indistinguishable
            /// from grids I've created. In reality I haven't personally authored any grids."</i> A
            /// hull bought from a faction changes hands without changing what put it in the world,
            /// so forcing the two onto one axis has to lose one of them. Origin is provenance;
            /// this is tenure.</para>
            /// <para>So <c>fleet</c> with a non-zero <c>playerOwned</c> is "a Factions ship now in
            /// player hands", and the <c>player</c> origin means grids a player actually built —
            /// which on this server should be nearly none.</para></summary>
            public int PlayerOwned;
            /// <summary>Moving grids standing in natural gravity of at least
            /// <see cref="GravityFloorG"/>.
            ///
            /// <para>Gravity is not a multiplier on a moving grid's cost — it changes how long the
            /// grid stays costly. A body under constant acceleration never sleeps, so it stays in
            /// <c>ActiveRigidBodies</c> indefinitely, and everything in
            /// <see cref="TransformChildren"/> is then paid every frame for as long as it is
            /// there.</para></summary>
            public int MovingInGravity;
            /// <summary>Grids in natural gravity of at least <see cref="GravityFloorG"/>,
            /// <b>regardless of whether they are moving</b>.
            ///
            /// <para><b>Why this exists separately from <see cref="MovingInGravity"/>.</b> That one
            /// is the conjunction, and at rest it reads zero by construction — so it cannot tell
            /// "not in the well" from "in the well and stationary". An at-rest comparison either
            /// side of a gravity boundary is precisely the experiment that distinguishes gravity
            /// preventing sleep from gravity costing nothing, and the conjunction is blind to
            /// it.</para></summary>
            public int InGravity;
            /// <summary>The strongest natural gravity any grid in this bucket stands in, in g.
            ///
            /// <para><b>A measured boundary rather than a derived one.</b> <c>planets.json</c>'s
            /// <c>GravityRadius</c> is the world-builder's computed 0.05 g limit, and on 2026-09-21
            /// a hold 0.2 km inside it registered no gravity at all by SE's own provider. Reporting
            /// the value read from <c>MyGravityProviderSystem</c> means the question "was the ship
            /// actually in gravity" stops depending on a number nothing measured.</para></summary>
            public double PeakGravityG;
            /// <summary>Grids that are a non-root member of a mechanical group — a turret, rotor
            /// head or piston.
            ///
            /// <para>These are not cheap passengers. SE's <c>MyPhysics.IterateBodies</c> admits a
            /// body only when <c>Entity.Parent == null</c>, and a rotor-mounted subgrid is a
            /// separate root entity joined by a constraint rather than a parented child — so each
            /// one iterates on its own and pays a full world transform.</para></summary>
            public int Subgrids;
            /// <summary>Summed <c>Hierarchy.ChildrenNeedingWorldMatrix.Count</c> over this bucket's
            /// <b>moving</b> grids.
            ///
            /// <para><b>The cost driver itself, not a proxy for it.</b>
            /// <c>MyPhysicsBody.OnMotionDynamic</c> calls <c>PositionComp.SetWorldMatrix</c>, which
            /// walks exactly this collection and recurses
            /// (<c>MyPositionComponent.UpdateChildren</c>). Block count and mass are both
            /// approximations of it; this is the loop length.</para></summary>
            public long TransformChildren;
            /// <summary>Summed <c>MyGridShape.ShapeCount</c> — the number of collision shapes Havok
            /// actually holds for these grids.
            ///
            /// <para><b>Why this is not the block count.</b> <c>MyGridShape.AddShapesFromCollector</c>
            /// feeds blocks through <c>MyVoxelSegmentation</c>, which merges runs of identical cubes
            /// into single boxes, so a solid armour brick collapses to a handful of shapes while a
            /// hull of the same block count in varied blocks does not. Block count is what a player
            /// builds; this is what the solver is handed.</para>
            ///
            /// <para><b>What it is here to settle.</b> Over 763 windows <c>constraints</c> tracks
            /// blocks at r = +0.933 and subgrids at only +0.643, so the cost term is block-shaped
            /// rather than subgrid-shaped. What blocks cannot distinguish is raw count from
            /// geometric complexity — and those two are the same number until this one is read
            /// beside them.</para></summary>
            public long ShapeCount;
            public long Blocks;
            public long Pcu;
            public double MassTons;

            public void Reset()
            {
                Grids = 0; Moving = 0; Static = 0; FarFromPlayer = 0; PlayerOwned = 0;
                MovingInGravity = 0; InGravity = 0; PeakGravityG = 0; Subgrids = 0; TransformChildren = 0;
                ShapeCount = 0; Blocks = 0; Pcu = 0; MassTons = 0;
            }
        }

        /// <summary>One grid SE is stepping, as the load governor's cost model will read it.</summary>
        internal sealed class SimGridRow
        {
            public long GridId;
            public string Name;
            public string Party;
            public GridOrigin Origin;
            public int Blocks;
            public long Pcu;
            public long Shapes;
            public bool Awake;
            public bool Moving;
            public bool IsStatic;
            public double MassTons;
            /// <summary>Kilometres to the nearest online player's character; negative when nobody
            /// is online. Recorded for reference: whether SE simulates the grid is read from SE, not
            /// derived from this.</summary>
            public double NearestPlayerKm;
        }

        private readonly Bucket[] _buckets = new Bucket[OriginCount];
        private readonly Dictionary<HkWorld, bool> _activeByWorld = new Dictionary<HkWorld, bool>();
        private readonly List<Vector3D> _playerPositions = new List<Vector3D>(8);

        /// <summary>Every grid SE stepped at the last census. See <see cref="SimulatedClusters"/>.</summary>
        internal readonly List<SimGridRow> Simulated = new List<SimGridRow>();
        internal readonly HashSet<long> SimulatedIds = new HashSet<long>();
        internal readonly HashSet<long> UnsimulatedIds = new HashSet<long>();
        /// <summary>The party every grid is charged to, simulated or not: block logic keeps running
        /// on grids SE is not stepping, and the ledger charges it to their owner too.</summary>
        internal readonly Dictionary<long, string> PartyByGrid = new Dictionary<long, string>();
        /// <summary>Simulated grids summed per party, charged by owner.</summary>
        internal readonly SimSetTally Parties = new SimSetTally();
        /// <summary>False when SE's cluster rule could not be read this census; every simulated-set
        /// member above is then empty and means "unknown", not "nothing simulated".</summary>
        internal bool SimSetValid { get; private set; }
        internal int Clusters { get; private set; }
        internal int ActiveClusters { get; private set; }
        /// <summary>Whether SE is skipping player-free clusters at all
        /// (<c>EnableSelectivePhysicsUpdates</c> on a dedicated server). When false, every cluster
        /// is stepped and the simulated set is the whole world.</summary>
        internal bool SelectiveUpdates { get; private set; }
        private readonly HashSet<long> _fleetIds = new HashSet<long>();
        private readonly List<MyEntity> _scratch = new List<MyEntity>(512);
        private readonly List<MyCubeGrid> _groupScratch = new List<MyCubeGrid>(16);

        internal GridCensus()
        {
            for (int i = 0; i < OriginCount; i++) _buckets[i] = new Bucket();
        }

        internal Bucket this[GridOrigin origin] { get { return _buckets[(int)origin]; } }

        /// <summary>Total grids seen by the last <see cref="Run"/>. Compared against SE's own
        /// <c>GridsCount</c> in the <c>STATISTICS</c> line, this says whether the walk saw the whole
        /// world — a census that silently sees half of it would attribute half of it.</summary>
        internal int TotalGrids { get; private set; }

        internal static string OriginTag(GridOrigin origin)
        {
            switch (origin)
            {
                case GridOrigin.FleetApproach: return "fleet-approach";
                case GridOrigin.Fleet:         return "fleet";
                case GridOrigin.Station:       return "station";
                case GridOrigin.NpcLoose:      return "npc-loose";
                case GridOrigin.Player:        return "player";
                default:                       return "other";
            }
        }

        /// <summary>Walks every grid and re-fills the buckets.
        /// <para><paramref name="fleetGridIds"/> and <paramref name="isFlying"/> come from the
        /// runtime; <paramref name="isStationGrid"/> from the station materialiser. All three are
        /// passed in rather than reached for so this stays testable and so the walk holds no
        /// reference to the runtime it is measuring.</para></summary>
        internal void Run(HashSet<long> fleetGridIds,
                          Func<long, bool> isFlying,
                          Func<long, bool> isStationGrid,
                          Func<VRageMath.Vector3D, bool> isNearAnyPlayer)
        {
            for (int i = 0; i < OriginCount; i++) _buckets[i].Reset();
            TotalGrids = 0;

            _fleetIds.Clear();
            if (fleetGridIds != null)
                foreach (long id in fleetGridIds) _fleetIds.Add(id);

            MyPlayerCollection players = MySession.Static != null ? MySession.Static.Players : null;

            Simulated.Clear();
            SimulatedIds.Clear();
            UnsimulatedIds.Clear();
            PartyByGrid.Clear();
            Parties.Clear();
            int clusters, activeClusters;
            bool selective;
            SimSetValid = SimulatedClusters.Snapshot(_activeByWorld, out clusters, out activeClusters, out selective);
            Clusters = clusters;
            ActiveClusters = activeClusters;
            SelectiveUpdates = selective;
            CollectPlayerPositions(players);

            // Copied first, then classified: the classification calls back into the runtime and the
            // station lookup, and holding SE's live entity set open across all of that is a longer
            // window for a spawn to invalidate the enumerator than it needs to be.
            _scratch.Clear();
            foreach (MyEntity entity in MyEntities.GetEntities()) _scratch.Add(entity);

            for (int i = 0; i < _scratch.Count; i++)
            {
                var grid = _scratch[i] as MyCubeGrid;
                if (grid == null || grid.MarkedForClose || grid.Closed) continue;

                TotalGrids++;
                GridOrigin origin = Classify(grid, players, isFlying, isStationGrid);
                Bucket bucket = _buckets[(int)origin];
                bucket.Grids++;
                bucket.Blocks += grid.BlocksCount;
                bucket.Pcu += grid.BlocksPCU;

                if (grid.IsStatic) bucket.Static++;
                bool moving = IsMoving(grid);
                if (moving) bucket.Moving++;
                if (IsSubgrid(grid)) bucket.Subgrids++;

                // Gravity is read for EVERY grid, moving or not: whether a body can sleep is the
                // question, and a sleeping body is exactly the case the moving-only reading cannot
                // see. Four generators to iterate, so it is cheap at census cadence.
                double g = NaturalGravityG(grid);
                if (g >= GravityFloorG) bucket.InGravity++;
                if (g > bucket.PeakGravityG) bucket.PeakGravityG = g;

                // TransformChildren stays moving-only: a sleeping body is not in ActiveRigidBodies
                // and pays none of that cost, so counting it would dilute the number being explained.
                if (moving)
                {
                    bucket.TransformChildren += TransformChildCount(grid);
                    if (g >= GravityFloorG) bucket.MovingInGravity++;
                }
                if (isNearAnyPlayer != null && !isNearAnyPlayer(grid.PositionComp.GetPosition()))
                    bucket.FarFromPlayer++;
                if (OwnersIncludePlayer(players, grid.BigOwners)) bucket.PlayerOwned++;

                MyPhysicsComponentBase physics = grid.Physics;
                double massTons = physics != null ? physics.Mass / 1000.0 : 0.0;
                long shapes = ShapeCountOf(physics);
                bucket.MassTons += massTons;
                bucket.ShapeCount += shapes;

                if (SimSetValid)
                    AddToSimulatedSet(grid, origin, players, moving, massTons, shapes);
            }
            _scratch.Clear();
        }

        private void AddToSimulatedSet(MyCubeGrid grid, GridOrigin origin, MyPlayerCollection players,
                                       bool moving, double massTons, long shapes)
        {
            bool simulated = IsSimulated(grid);
            bool awake = IsAwake(grid);
            bool npcProvenance = origin == GridOrigin.FleetApproach || origin == GridOrigin.Fleet
                              || origin == GridOrigin.Station || origin == GridOrigin.NpcLoose;
            string party = SimSetTally.PartyFor(npcProvenance, DominantPlayerName(players, grid.BigOwners));
            Parties.Add(party, simulated, grid.BlocksCount, grid.BlocksPCU, shapes, awake, moving, massTons);
            PartyByGrid[grid.EntityId] = party;
            if (!simulated)
            {
                UnsimulatedIds.Add(grid.EntityId);
                return;
            }
            SimulatedIds.Add(grid.EntityId);
            Simulated.Add(new SimGridRow
            {
                GridId = grid.EntityId,
                Name = grid.DisplayName ?? "(unnamed)",
                Party = party,
                Origin = origin,
                Blocks = grid.BlocksCount,
                Pcu = grid.BlocksPCU,
                Shapes = shapes,
                Awake = awake,
                Moving = moving,
                IsStatic = grid.IsStatic,
                MassTons = massTons,
                NearestPlayerKm = NearestPlayerKm(grid),
            });
        }

        /// <summary>Whether SE stepped the Havok world this grid's body lives in. A grid with no
        /// body is in no world and is not simulated.</summary>
        private bool IsSimulated(MyCubeGrid grid)
        {
            try
            {
                var body = grid.Physics as MyPhysicsBody;
                HkWorld world = body != null ? body.HavokWorld : null;
                bool active;
                return world != null && _activeByWorld.TryGetValue(world, out active) && active;
            }
            catch { return false; }
        }

        /// <summary>Havok has the body awake rather than asleep. Static grids read false: a fixed
        /// body is never integrated.</summary>
        private static bool IsAwake(MyCubeGrid grid)
        {
            try
            {
                MyPhysicsComponentBase physics = grid.Physics;
                return physics != null && physics.Enabled && !grid.IsStatic && physics.IsActive;
            }
            catch { return false; }
        }

        private void CollectPlayerPositions(MyPlayerCollection players)
        {
            _playerPositions.Clear();
            if (players == null) return;
            try
            {
                foreach (MyPlayer player in players.GetOnlinePlayers())
                {
                    if (player == null || player.IsBot || player.Character == null) continue;
                    _playerPositions.Add(player.Character.PositionComp.GetPosition());
                }
            }
            catch { _playerPositions.Clear(); }
        }

        private double NearestPlayerKm(MyCubeGrid grid)
        {
            if (_playerPositions.Count == 0) return -1.0;
            Vector3D pos = grid.PositionComp.GetPosition();
            double best = double.MaxValue;
            for (int i = 0; i < _playerPositions.Count; i++)
            {
                double d = Vector3D.DistanceSquared(pos, _playerPositions[i]);
                if (d < best) best = d;
            }
            return Math.Sqrt(best) / 1000.0;
        }

        /// <summary>The first player identity among the dominant owners, by display name, or null
        /// when none is a player. The same <c>BigOwners</c> reading as
        /// <see cref="OwnersIncludePlayer"/>.</summary>
        private static string DominantPlayerName(MyPlayerCollection players, List<long> owners)
        {
            if (players == null || owners == null) return null;
            for (int i = 0; i < owners.Count; i++)
            {
                long id = owners[i];
                if (id == 0 || players.IdentityIsNpc(id)) continue;
                MyIdentity identity = players.TryGetIdentity(id);
                return identity != null && !string.IsNullOrEmpty(identity.DisplayName)
                    ? identity.DisplayName
                    : id.ToString();
            }
            return null;
        }

        /// <summary>What put this grid in the world.
        ///
        /// <para><b>Order is the whole design.</b> Every "this is ours" signal is tested before any
        /// ownership test, because ownership is the weakest evidence on the list and the only one a
        /// player can contaminate by accident.</para>
        ///
        /// <para><b>Why ownership is weak, measured.</b> SE's <c>SmallOwners</c> is <i>every</i>
        /// identity owning <i>any</i> block
        /// (<c>MyCubeGridOwnershipManager.UpdatePlayerGrids</c>), so a single block flips a whole
        /// grid. Live 2026-09-21, from the save's own block owner tags: twelve
        /// <c>[NPC] PDC Type 1a GAT</c> and twelve <c>[NPC] PDGT</c> turret subgrids each carried
        /// exactly <b>one</b> block owned by the operator's identity — and all twenty-four read as
        /// <c>player</c>. So did an NPC station, <c>TFCS … Homestead's Promise</c>, on eight blocks
        /// out of a thousand.</para>
        ///
        /// <para><b>And <c>BigOwners</c> alone does not fix it.</b> It is the identities tied for the
        /// <i>maximum</i> owned-functional-block count, and blocks owned by nobody are not counted at
        /// all — so on a two-block turret where one block is the player's and the other is unowned,
        /// the player is the maximum and lands in <c>BigOwners</c> too. The NPC signals are what
        /// settle it; <c>BigOwners</c> only narrows what is left.</para>
        ///
        /// <para>The three NPC signals are the ones <see cref="GridReaper"/> already trusts, and for
        /// its reasons: our own per-process record, then SE's <c>IsNpcSpawnedGrid</c> bit, then the
        /// <c>[NPC]</c>/<c>factions:npc:</c> entity name — the last being the only one that survives
        /// both a restart and SE's claim timer clearing the bit.</para></summary>
        private GridOrigin Classify(MyCubeGrid grid,
                                    MyPlayerCollection players,
                                    Func<long, bool> isFlying,
                                    Func<long, bool> isStationGrid)
        {
            long eid = grid.EntityId;

            // 1. In the fleet ledger. By entity id rather than ownership: a fleet grid and a station
            //    grid are owned by the same NPC faction, so ownership cannot tell them apart.
            if (_fleetIds.Contains(eid))
                return isFlying != null && isFlying(eid) ? GridOrigin.FleetApproach : GridOrigin.Fleet;

            // 2. A station, ours or an SE faction's. IsGenerated covers a vanilla economy station
            //    the runtime has no node for yet.
            if (grid.IsGenerated || (isStationGrid != null && isStationGrid(eid)))
                return GridOrigin.Station;

            // 3. NPC-spawned with no fleet record: subgrids, debris, ordnance, pre-restart hulls.
            if (IsNpcSpawned(grid)) return GridOrigin.NpcLoose;

            // 4. Attached to something with better evidence than this grid has. A two-block turret
            //    subgrid carries almost no evidence of its own; the hull it rides does.
            GridOrigin? fromGroup = GroupOrigin(grid, isFlying, isStationGrid);
            if (fromGroup.HasValue) return fromGroup.Value;

            // 5. Nothing says anything put it here but a player. Only now, and only on the
            //    dominant owner -- and note this is the ORIGIN axis: a Factions hull the player has
            //    since bought keeps its own origin above and is counted in that bucket's
            //    `playerOwned` instead.
            if (OwnersIncludePlayer(players, grid.BigOwners)) return GridOrigin.Player;

            return GridOrigin.Other;
        }

        /// <summary>The durable marks that say SE or this plugin spawned the grid, in decreasing
        /// order of how much a restart and SE's claim timer can take away from them.
        ///
        /// <para><b>Measured on the live server, 2026-09-21, and this is why the display tag is on
        /// the list.</b> Across 78 grids straight after a restart, <c>factions grids census</c>
        /// reported <c>marked=0 bit_only=0 name_only=0</c> — every one of the first three signals
        /// absent at once. The record is per-process and dies with the restart by design; SE's
        /// <c>IsNpcSpawnedGrid</c> bit is cleared by <c>MyNPCGridClaimSessionComponent</c> once a
        /// non-NPC identity has owned a block past <c>NPCGridClaimTimeLimit</c>, which one stray
        /// operator-owned block is enough to trigger; and these grids carry no <c>factions:npc:</c>
        /// entity name at all. The <c>[NPC]</c> display tag was the only mark left standing.</para></summary>
        private static bool IsNpcSpawned(MyCubeGrid grid)
        {
            SimGridRecord record;
            if (SimGridMarks.TryGet(grid.EntityId, out record) && record != null) return true;
            if (grid.IsNpcSpawnedGrid) return true;
            if (NpcGridNaming.IsSimBornName(grid.Name)) return true;
            // Cosmetic to a player, durable to everyone else: this plugin writes it and SE has no
            // path that rewrites a DisplayName.
            string display = grid.DisplayName;
            return display != null && display.StartsWith(NpcGridNaming.Tag, StringComparison.Ordinal);
        }

        /// <summary>The origin of the grid this one is mechanically attached to, or null when it is
        /// unattached or nothing in the group is better evidence than the grid itself.
        ///
        /// <para><b>A turret is not a ship.</b> A <c>PDC Type 1a GAT</c> rides a rotor on one of our
        /// hulls and is its own <c>MyCubeGrid</c> with two blocks, so on the live server twelve of
        /// them were being counted and attributed independently of the hull carrying them. Ownership
        /// on such a subgrid is close to meaningless — one owned block is a majority of two — so the
        /// group's own evidence has to win.</para></summary>
        private GridOrigin? GroupOrigin(MyCubeGrid grid,
                                        Func<long, bool> isFlying,
                                        Func<long, bool> isStationGrid)
        {
            _groupScratch.Clear();
            if (MyCubeGridGroups.Static == null || MyCubeGridGroups.Static.Mechanical == null) return null;
            try { MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, _groupScratch); }
            catch { return null; }
            if (_groupScratch.Count <= 1) return null;   // a group of one is just the grid

            // Strongest evidence in the group wins, in the same order Classify uses.
            for (int i = 0; i < _groupScratch.Count; i++)
            {
                MyCubeGrid member = _groupScratch[i];
                if (member == null || member == grid) continue;
                if (_fleetIds.Contains(member.EntityId))
                    return isFlying != null && isFlying(member.EntityId)
                        ? GridOrigin.FleetApproach : GridOrigin.Fleet;
            }
            for (int i = 0; i < _groupScratch.Count; i++)
            {
                MyCubeGrid member = _groupScratch[i];
                if (member == null || member == grid) continue;
                if (member.IsGenerated || (isStationGrid != null && isStationGrid(member.EntityId)))
                    return GridOrigin.Station;
            }
            for (int i = 0; i < _groupScratch.Count; i++)
            {
                MyCubeGrid member = _groupScratch[i];
                if (member == null || member == grid) continue;
                if (IsNpcSpawned(member)) return GridOrigin.NpcLoose;
            }
            return null;
        }

        /// <summary>Whether Havok is integrating this grid rather than leaving it asleep.
        /// <para>A static grid is never moving whatever its velocity field says, and a grid with no
        /// physics body is not in the simulation at all.</para></summary>
        private static bool IsMoving(MyCubeGrid grid)
        {
            if (grid.IsStatic) return false;
            MyPhysicsComponentBase physics = grid.Physics;
            if (physics == null || !physics.Enabled) return false;
            return physics.LinearVelocity.LengthSquared() > MovingSpeedMs * MovingSpeedMs
                || physics.AngularVelocity.LengthSquared() > 0.01;
        }

        /// <summary>Natural gravity at the grid, in g. Zero on any failure — a missed sample must
        /// read as "not in gravity" rather than inflate the bucket it lands in.</summary>
        private static double NaturalGravityG(MyCubeGrid grid)
        {
            try
            {
                if (grid.PositionComp == null) return 0.0;
                float mult;
                Vector3 g = MyGravityProviderSystem.CalculateNaturalGravityInPoint(
                    grid.PositionComp.GetPosition(), out mult);
                return g.Length() / 9.81;
            }
            catch { return 0.0; }
        }

        /// <summary>How many children SE walks when this grid's world matrix is set — the length of
        /// the loop in <c>MyPositionComponent.UpdateChildren</c>.</summary>
        /// <summary>Collision shapes Havok holds for this grid, or 0 when it has no grid body yet.
        /// <para>Reached through <c>MyGridPhysics.Shape</c>, which is public and already built —
        /// this is a counter read, not a walk.</para></summary>
        private static long ShapeCountOf(MyPhysicsComponentBase physics)
        {
            try
            {
                var grid = physics as MyGridPhysics;
                if (grid == null || grid.Shape == null) return 0;
                return grid.Shape.ShapeCount;
            }
            catch { return 0; }
        }

        private static int TransformChildCount(MyCubeGrid grid)
        {
            try
            {
                return grid.Hierarchy == null ? 0 : grid.Hierarchy.ChildrenNeedingWorldMatrix.Count;
            }
            catch { return 0; }
        }

        /// <summary>Whether this grid hangs off a larger one by a mechanical joint. SE exposes no
        /// "is a subgrid" flag, and the group carries no root, so the largest member by block count
        /// is taken as the hull and everything else as attached to it.</summary>
        private bool IsSubgrid(MyCubeGrid grid)
        {
            _groupScratch.Clear();
            if (MyCubeGridGroups.Static == null || MyCubeGridGroups.Static.Mechanical == null) return false;
            try { MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, _groupScratch); }
            catch { return false; }
            if (_groupScratch.Count <= 1) return false;
            for (int i = 0; i < _groupScratch.Count; i++)
            {
                MyCubeGrid other = _groupScratch[i];
                if (other != null && other != grid && other.BlocksCount > grid.BlocksCount) return true;
            }
            return false;
        }

        /// <summary>The <c>BigOwners</c> idiom used by <see cref="DebrisAbsorbManager"/> and
        /// <see cref="GridReaper"/>: an identity that is not an NPC is a player's.</summary>
        private static bool OwnersIncludePlayer(MyPlayerCollection players, List<long> owners)
        {
            if (players == null || owners == null) return false;
            for (int i = 0; i < owners.Count; i++)
            {
                long id = owners[i];
                if (id == 0) continue;              // unowned blocks belong to nobody
                if (!players.IdentityIsNpc(id)) return true;
            }
            return false;
        }
    }
}
