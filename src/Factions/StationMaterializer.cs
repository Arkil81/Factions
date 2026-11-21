using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;                       // AccessTools
using Sandbox.Common.ObjectBuilders;   // SpawningOptions
using Sandbox.Game.AI;                 // MyAIComponent, MyAgentBot
using Sandbox.Game.Entities;            // MyCubeGrid, MyEntities, MySafeZone, MyPrefabManager, MyCockpit
using Sandbox.Game.Entities.Planet;    // MyPlanetEnvironmentSessionComponent, MyPlanetSurfacePlacement
using Sandbox.Game.GameSystems;        // MyGravityProviderSystem
using Sandbox.Game.Localization;       // MySpaceTexts
using Sandbox.Game.Multiplayer;        // Sync
using Sandbox.Game.SessionComponents;  // MySessionComponentSafeZones, MySessionComponentEconomy
using Sandbox.Game.World;              // MyFaction, MyFactionStation, MySession
using VRage;                            // MyTexts
using VRage.Game;                      // MyObjectBuilder_CubeGrid
using VRage.Game.Entity;               // MyEntity
using VRage.Game.ModAPI;               // IMyFactionStation, IMyFaction
using VRage.Game.ObjectBuilders.Components;  // MySafeZoneAction
using VRageMath;
using Factions.Core;

namespace Factions
{
    internal struct MaterializedStation
    {
        public long GridEntityId;        // 0 while pending
        public long SafeZoneEntityId;    // 0 if vanilla-owned or not yet created
        public string FactionTag;
        public long StableStationId;     // MyFactionStation.Id
        public DateTime LeaveTime;       // MinValue = players present; set when last player left
        public bool SpawnPending;        // true while async SpawnPrefab is in flight
    }

    /// <summary>Summary returned by <see cref="StationMaterializer.FixNodeStationDrift"/>.
    /// Mirrors <see cref="StationOwnershipFixResult"/>'s shape so the bridge verbs read alike.</summary>
    public struct StationDriftFixResult
    {
        public int Checked;
        public int Drifted;
        public int Fixed;
        public int Skipped;          // drifted further than any ordinary reposition could account for
        public double WorstDriftM;
        public List<string> Details;
    }

    /// <summary>Summary returned by <see cref="StationMaterializer.CheckSurfaceSites"/>.</summary>
    public struct StationSiteCheckResult
    {
        public int Checked;          // surface stations measured
        public int Steep;            // over SurfaceSiteSearch.MaxGrade
        public int Respawned;        // steep and standing, respawned by 'apply'
        public double WorstGrade;
        public List<string> Details;
    }

    /// <summary>
    /// Lazy-spawn lifecycle for Factions-managed SE economy station nodes (E### sources).
    /// Spawns a grid when a player is nearby, co-spawns a safe zone, and tears both down after
    /// a grace period once all players leave.
    ///
    /// On spawn the station is "claimed" exactly the way vanilla MyStationCellGenerator does it
    /// (Economy 2): StationEntityId/SafeZoneEntityId set on the MyFactionStation, grid marked
    /// IsGenerated (read by MySessionComponentTrash), grid registered with
    /// MySessionComponentEconomy, and station resources refreshed. Grid and safe zone are both
    /// spawned with <c>Save = false</c> -- MyEntities.Save gates on EntityFlags.Save and on nothing
    /// else, so that flag alone is what keeps a materialized station out of the sector file and
    /// therefore out of the next boot.
    ///
    /// The vanilla cell generator is held off by <see cref="StationCellGeneratorPatches"/>, which
    /// reads <see cref="IsManaged"/> -- the set of every founded station a Factions node owns, not
    /// just the ones with a grid standing right now. See <see cref="_managedIds"/>.
    /// </summary>
    public sealed class StationMaterializer
    {
        /// <summary>Nodes whose assembly failed this process (FAC-443); they spawn in one frame from then on.</summary>
        private readonly HashSet<string> _assemblyRefused = new HashSet<string>(StringComparer.Ordinal);


        // Radius of a Factions station's safe zone. Vanilla's MyFactionStation.SAFEZONE_SIZE is
        // 150 m; ours is wider because traffic crowds a busy station and ships collided just
        // outside the smaller zone (operator, 2026-10-02). Internal so the asteroid and feature-rock
        // materializers keep placement clear of a founded node's zone without duplicating it.
        internal const float SafeZoneRadius = 500f;

        // The station's own footprint allowance, independent of the safe zone: vanilla's 150 m
        // SAFEZONE_SIZE, which every prefab clearance here was historically sized to.
        private const float StationFootprintAllowanceM = 150f;

        // Fallback probe radius when a prefab's own bounding box cannot be measured: the historical
        // safe-zone-sized sphere, so an unmeasurable prefab degrades to the old behaviour rather
        // than to no clearance check at all. See StationFootprintObstructed.
        private const float UnmeasuredPrefabRadiusM = StationFootprintAllowanceM;

        // Fraction of a planetary station's own local "up-axis" extent that should sit below
        // the terrain surface point once placed — mirrors vanilla MyStationGenerator's outpost
        // placement (which nudges by a fixed 0.25 m, an amount that only works because vanilla's
        // own outpost prefabs are authored with their local origin already near their base).
        // Our station prefabs are not guaranteed to follow that convention, so instead of a fixed
        // nudge we measure the prefab's own local bounding box and solve for the translation that
        // leaves this fraction embedded, whatever the prefab's shape.
        private const double StationEmbedFraction = 0.10;
        private const double StationEmbedFloorM   = 0.25;   // vanilla's own fixed embed, used as a floor

        // Footprint reach used by the surface-slope search when the prefab cannot be measured. The
        // 150 m clearance fallback above is sized to a safe zone and would make every unmeasured
        // design read as standing on a mountainside.
        private const double UnmeasuredFootprintReachM = 30.0;

        // A surface station is snapped to terrain when it is within this distance of the surface.
        private const double SurfaceSnapRangeM = 2000.0;

        // Cap on per-node lines in a sitecheck report, for the same reason as MaxDriftDetails.
        private const int MaxSiteDetails = 20;

        // Cap on per-node lines in a FixNodeStationDrift report -- the bridge/chat result is a single
        // string, and a whole-map sweep can legitimately flag many nodes at once.
        private const int MaxDriftDetails = 20;

        // MyFactionStation.Update(MyFaction) is internal; it regenerates the station's stocked
        // resources from StationEntityId, which vanilla does after every spawn.
        private static readonly MethodInfo StationResourcesUpdate =
            AccessTools.Method(typeof(MyFactionStation), "Update", new[] { typeof(MyFaction) });

        // MyCubeGrid.IsGenerated has an internal setter; vanilla flags its station grids with it
        // so they are excluded from the world save.
        private static readonly MethodInfo GridIsGeneratedSetter =
            AccessTools.PropertySetter(typeof(MyCubeGrid), "IsGenerated");

        // MyFactionStation.SetPosition(Vector3D, Vector3, Vector3) is internal; it rewrites the
        // station's own stored transformation (Forward/Up are otherwise get-only, backed by a
        // private m_transformation field). Critical: vanilla's own MyStationCellGenerator reads
        // station.Forward/station.Up directly when it spawns a station's grid — a Torch-plugin-side
        // "BeginSpawn" that only overrides orientation in a local variable is invisible to vanilla,
        // which runs as a core SE procedural-generation system independent of (and typically earlier
        // than) any Torch plugin's Update() tick, so it usually wins the very first spawn race. The
        // only fix that also works for vanilla-spawned grids is correcting the STORED data — which
        // also then survives into the world save, healing every future load regardless of who spawns.
        private static readonly MethodInfo StationSetPosition =
            AccessTools.Method(typeof(MyFactionStation), "SetPosition",
                new[] { typeof(Vector3D), typeof(Vector3), typeof(Vector3) });

        // MySpawnPrefabProperties and the SpawnPrefabInternal(props, Action, Action) overload are
        // both internal. We reach them via reflection so we can pass faction.CustomColor, which the
        // public SpawnPrefab overload does not expose.
        private static readonly Type SpawnPropsType =
            AccessTools.TypeByName("Sandbox.Game.World.MySpawnPrefabProperties");
        private static readonly MethodInfo SpawnPrefabInternalM =
            SpawnPropsType != null
                ? AccessTools.Method(typeof(MyPrefabManager), "SpawnPrefabInternal",
                    new[] { SpawnPropsType, typeof(Action), typeof(Action) })
                : null;

        // Every founded station a Factions node owns -- seeded from the graph at session load
        // (SeedClaims) and re-derived by RefreshClaimsIfDue, then queried from the game thread by
        // StationCellGeneratorPatches to suppress vanilla spawn/despawn.
        //
        // The claim covers the whole founded set, not the currently-materialized subset. Vanilla's
        // MyStationCellGenerator picks a station's seed up on player proximity and spawns it
        // asynchronously; a claim taken at spawn time is taken after that seed is already in
        // flight, which loses the race and leaves two grids on one node.
        //
        // Session load rather than the first tick is what makes the cover complete: vanilla reaches
        // GenerateObjects from MyProceduralWorldGenerator.UpdateBeforeSimulation, so every
        // simulation frame before the plugin's first Update is a frame with no claim standing.
        private static readonly HashSet<long> _managedIds = new HashSet<long>();
        public static bool IsManaged(long stationId) { return _managedIds.Contains(stationId); }

        // SE faction ids of the Factions registry, rebuilt with the claim. A station record of one of
        // these factions is ours whether or not its id is in the claim at this moment: every such
        // record is node-backed at rest, and the claim lags it in two places -- a station founded
        // since the last resync (never claimed yet) and a node mid-handover (dropped for a resync).
        // Vanilla spawned into both gaps, and each spawn left a Save = true grid in the sector file
        // (FAC-025).
        private static readonly HashSet<long> _managedFactionIds = new HashSet<long>();

        /// <summary>Whether <paramref name="station"/> is Factions' to spawn and close rather than
        /// vanilla's: its id is claimed, or its faction is in the Factions registry. Read by
        /// <c>StationCellGeneratorPatches</c> on the game thread, the same thread that rebuilds both
        /// sets.</summary>
        public static bool IsOurs(IMyFactionStation station)
        {
            return station != null
                && StationClaimLedger.Covers(station.Id, station.FactionId, _managedIds, _managedFactionIds);
        }

        private static void RefreshManagedFactions(IList<FactionState> registry)
        {
            _managedFactionIds.Clear();
            if (registry == null || MySession.Static == null) return;
            for (int i = 0; i < registry.Count; i++)
            {
                MyFaction faction = MySession.Static.Factions.TryGetFactionByTag(registry[i].Tag);
                if (faction != null) _managedFactionIds.Add(faction.FactionId);
            }
        }

        // Which node each claimed id came from and when an id left the claim: the record a vanilla
        // spawn of a station we should be holding is read against. _managedIds stays the hot-path
        // set; this is rebuilt beside it on the same cadence.
        private static readonly StationClaimLedger _claimLedger = new StationClaimLedger();
        private static readonly List<string> _claimLines = new List<string>();

        /// <summary>Where claim changes and vanilla station spawns are recorded — the Factions day
        /// log, set by <c>FactionRuntime</c>. Static because the cell-generator patch that writes the
        /// spawn line is. Null writes nothing.</summary>
        public static Action<string> ClaimLog;

        /// <summary>Records a station the cell generator is about to spawn because the claim does
        /// not cover it, with what the claim knows about that id. Called from
        /// <c>StationCellGeneratorPatches</c> for a seed it let through; a station already standing
        /// is not a spawn and is not reported.</summary>
        internal static void NoteVanillaSpawn(IMyFactionStation station)
        {
            Action<string> sink = ClaimLog;
            if (sink == null || station == null || station.StationEntityId != 0) return;
            IMyFaction faction = MySession.Static != null ? MySession.Static.Factions.TryGetFactionById(station.FactionId) : null;
            if (faction == null) return; // the generator skips a station with no faction
            DateTime now = DateTime.UtcNow;
            int held;
            if (!_claimLedger.AdmitVanillaSpawn(station.Id, now, out held)) return;
            sink(DescribeVanillaSeed("STATION_VANILLA_SPAWN", station, faction, now, held));
        }

        /// <summary>Records a station the cell generator would have spawned and the patch withheld
        /// because its faction is in the registry, though the claim did not cover its id. Each line
        /// is a pin that did not happen; what follows <c>station=</c> says which gap it fell in.</summary>
        internal static void NoteVanillaBlock(IMyFactionStation station)
        {
            Action<string> sink = ClaimLog;
            if (sink == null || station == null || station.StationEntityId != 0) return;
            IMyFaction faction = MySession.Static != null ? MySession.Static.Factions.TryGetFactionById(station.FactionId) : null;
            if (faction == null) return;
            DateTime now = DateTime.UtcNow;
            int held;
            if (!_claimLedger.AdmitVanillaBlock(station.Id, now, out held)) return;
            sink(DescribeVanillaSeed("STATION_VANILLA_BLOCKED", station, faction, now, held));
        }

        private static string DescribeVanillaSeed(string kind, IMyFactionStation station, IMyFaction faction, DateTime now, int held)
        {
            Vector3D p = station.Position;
            return kind + " station=" + station.Id + " tag=" + faction.Tag + " type=" + station.Type
                 + " " + _claimLedger.DescribeUnclaimed(station.Id, now)
                 + " claimed=" + _managedIds.Count
                 + " pos=" + ((long)p.X).ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + "," + ((long)p.Y).ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + "," + ((long)p.Z).ToString(System.Globalization.CultureInfo.InvariantCulture)
                 + (held > 0 ? " repeats=" + held : "");
        }

        // ---- player siege support ---------------------------------------------------------------

        /// <summary>Node id owning a materialised station grid, or null when the grid is not one of
        /// ours. Linear over materialised stations, which is a handful — the alternative is a second
        /// index to keep in step with <see cref="_materialized"/> across spawn, despawn, adoption and
        /// permanent removal, and a stale reverse index would silently attribute damage to the wrong
        /// node.</summary>
        public string NodeIdForStationGrid(long gridEntityId)
        {
            if (gridEntityId == 0) return null;
            foreach (var kv in _materialized)
                if (kv.Value.GridEntityId == gridEntityId) return kv.Key;
            return null;
        }

        /// <summary>The live safe-zone entity for a node, or 0 when it has none — an unprotected
        /// faction's station, a zone that failed to create, or a station not currently materialised.
        /// Every caller already treats 0 as "no zone", which is the same value the other two cases
        /// produce.</summary>
        public long SafeZoneEntityIdFor(string nodeId)
        {
            MaterializedStation ms;
            return nodeId != null && _materialized.TryGetValue(nodeId, out ms) ? ms.SafeZoneEntityId : 0L;
        }

        /// <summary>Stations standing or being spawned.</summary>
        public int MaterializedCount { get { return _materialized.Count; } }

        /// <summary>The nodes <see cref="IsObserved"/> answers true for; null when none. A Nexus agent
        /// reports them so the authority holds a refit as it does for its own.</summary>
        public List<string> ObservedNodes()
        {
            List<string> list = null;
            foreach (var kv in _materialized)
            {
                if (!IsObserved(kv.Key)) continue;
                if (list == null) list = new List<string>();
                list.Add(kv.Key);
            }
            return list;
        }

        /// <summary>Whether a player is standing at this node's station right now — the question a
        /// station refit has to answer before it closes the grid under them.
        ///
        /// <para>True only while a grid is up (or one is on its way) with nobody having left: an
        /// entry whose <c>LeaveTime</c> is set is inside the grace period, which is the window the
        /// despawn itself uses and so is safe to rebuild in. A node with no entry at all has no grid
        /// standing and is likewise unobserved.</para></summary>
        public bool IsObserved(string nodeId)
        {
            MaterializedStation ms;
            if (nodeId == null || !_materialized.TryGetValue(nodeId, out ms)) return false;
            if (ms.SpawnPending) return true;
            return ms.GridEntityId != 0 && ms.LeaveTime == DateTime.MinValue;
        }

        /// <summary>Drops this node's materialisation bookkeeping so the ordinary proximity path
        /// spawns it again from scratch, and clears the remembered decline reason so the next one is
        /// reported afresh. Exactly what <see cref="ForceRespawn"/> does before it calls
        /// <see cref="BeginSpawn"/>; called by the refit swap, which has already closed the grid and
        /// the zone itself and wants the respawn to happen on approach rather than now.</summary>
        internal void ForgetMaterialization(string nodeId)
        {
            if (nodeId == null) return;
            _materialized.Remove(nodeId);
            _declineReason.Remove(nodeId);
        }

        /// <summary>The live station grid for a node, or 0 when it is not materialised.</summary>
        public long StationGridEntityIdFor(string nodeId)
        {
            MaterializedStation ms;
            return nodeId != null && _materialized.TryGetValue(nodeId, out ms) ? ms.GridEntityId : 0L;
        }

        /// <summary>Every materialised station with a grid standing, as (nodeId, gridEntityId), into
        /// <paramref name="into"/> (cleared first). The station call's once-a-second read walks this.</summary>
        public void MaterializedStationGrids(List<KeyValuePair<string, long>> into)
        {
            into.Clear();
            foreach (var kv in _materialized)
                if (kv.Value.GridEntityId != 0)
                    into.Add(new KeyValuePair<string, long>(kv.Key, kv.Value.GridEntityId));
        }

        /// <summary>Every currently materialised station, as (nodeId, safeZoneEntityId). The siege
        /// tick walks this rather than the whole node graph: a zone only exists while its station is
        /// spawned, so an unmaterialised node has nothing to switch.</summary>
        /// <summary>Every standing station's node id and grid entity id.</summary>
        public IEnumerable<KeyValuePair<string, long>> MaterializedGrids()
        {
            foreach (var kv in _materialized)
                if (kv.Value.GridEntityId != 0 && !kv.Value.SpawnPending)
                    yield return new KeyValuePair<string, long>(kv.Key, kv.Value.GridEntityId);
        }

        public IEnumerable<KeyValuePair<string, long>> MaterializedSafeZones()
        {
            foreach (var kv in _materialized)
                if (kv.Value.SafeZoneEntityId != 0)
                    yield return new KeyValuePair<string, long>(kv.Key, kv.Value.SafeZoneEntityId);
        }

        /// <summary>Switches a safe zone on or off and optionally recolours it, through SE's own
        /// server-side update path so the change reaches clients.
        ///
        /// <para>Deliberately <c>Enabled</c> and not <c>Close()</c>: a disabled zone is still a live
        /// entity at the same position with the same radius, and <c>SafeZoneEntityId</c> keeps
        /// pointing at it. Closing it would make the station permanently unprotectable and would
        /// strip its bot anchor.</para>
        ///
        /// <para>Returns false when there is nothing to change — no zone, or already in the wanted
        /// state with no colour move — so the caller can avoid a pointless network broadcast.</para></summary>
        public bool SetSafeZoneState(long safeZoneEntityId, bool enabled, Vector3? color, bool visible)
        {
            if (safeZoneEntityId == 0) return false;
            MySafeZone zone;
            if (!MyEntities.TryGetEntityById(safeZoneEntityId, out zone, false) || zone == null) return false;

            // ModelColor is a Color and the caller speaks Vector3, which is also the object
            // builder's own type -- SE converts the same way in MySafeZone.InitInternal.
            bool colorMoves = color.HasValue && new Color(color.Value) != zone.ModelColor;
            // Visibility is deliberately absent from this early-out: MySafeZone.IsVisible is
            // PRIVATE, readable only off the object builder, so there is nothing here to compare it
            // against. The caller gates on its own change detection instead.
            if (zone.Enabled == enabled && !colorMoves) return false;

            try
            {
                MyObjectBuilder_SafeZone ob = zone.GetObjectBuilder(false) as MyObjectBuilder_SafeZone;
                if (ob == null) return false;
                ob.Enabled = enabled;
                ob.IsVisible = visible;
                if (color.HasValue) ob.ModelColor = color.Value;
                // sync: true broadcasts to every client -- a zone that changed only on the server
                // would still look intact to the player shooting it.
                MySessionComponentSafeZones.UpdateSafeZone(ob, true);

                // Read back. UpdateSafeZone returns void and drops the update SILENTLY when SE's
                // own collision gate refuses it, so the call succeeding proves nothing -- the same
                // trap GrantLandingGearLock documents below. A siege whose breach never reached the
                // zone would leave a station that reads "down" in Core and is still invulnerable
                // in-world, which is the worst failure this feature has.
                if (zone.Enabled != enabled)
                {
                    FactionsPlugin.Log.Warn("StationMaterializer: safe zone " + safeZoneEntityId
                        + " refused Enabled=" + enabled + "; it still reads " + zone.Enabled + ".");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "StationMaterializer: safe-zone state change threw for "
                    + safeZoneEntityId + ".");
                return false;
            }
        }

        // Node ids a player is standing near this tick, and a snapshot of _materialized's keys so
        // the departure pass can close entries while walking. Both reused, both cleared on use.
        private readonly HashSet<string> _nearScratch = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _materializedScratch = new List<string>();

        // Set by FactionsPlugin; when true, spawn/despawn events are broadcast as chat messages.
        public static bool DebugEnabled;
        public static Action<string> DebugBroadcast;

        // Set by FactionRuntime (instance, not static -- unlike the debug hooks above, this needs
        // no cross-instance sharing). BeginSpawn's IsEffectivelyNpc() guard also passes for any tag
        // this returns true for -- lets a Magnate-tier player faction's founded station actually
        // spawn a physical grid, not just the abstract MyFactionStation StationFounder creates.
        public Func<string, bool> IsPlayerFactionLookup;

        // Set by FactionRuntime. False for a faction whose stations must stand attackable -- see
        // Factions.Core.StationSafeZonePolicy. Unset means every station is protected, which is the
        // behaviour before this hook existed.
        public Func<string, bool> SpawnsSafeZoneLookup;

        // Set by FactionRuntime. Answers the saved object builders of a player-scanned station
        // design, or null for anything the content walk found. This is the one thing that makes a
        // refit to a scanned design visible in world: MyFactionStation.PrefabName deliberately stays
        // a vanilla prefab SE can resolve (see StationFounder.ReplaceStationPrefab), so the node's
        // own StationPrefabId is the only place the truth lives.
        public Func<string, MyObjectBuilder_CubeGrid[]> CustomStationGrids;

        /// <summary>Whether a node's safe zone should be created switched ON. Unset means always,
        /// which is the pre-siege behaviour exactly. Wired to <c>PlayerSiege.ZoneShouldBeEnabled</c>
        /// so a station that respawns mid-siege, or inside an NPC raid's knockdown window, does not
        /// hand its protection back for free.</summary>
        public Func<string, bool> ZoneShouldStartEnabled;

        /// <summary>Whether a station's turrets are fed from its node's stock rather than refilled
        /// for free by vanilla. Unset means vanilla's refill, the behaviour before FAC-441.</summary>
        public Func<bool> TurretsFromStock;

        /// <summary>A stock-fed station has just stood: its node id and every grid of its assembly.
        /// Returns a fragment for the <c>Spawned</c> line, or null.</summary>
        public Func<string, List<MyCubeGrid>, string> StationStood;

        /// <summary>A station grid has been closed: its node id and the gun-feeding magazines its
        /// assembly was read to hold just before (empty when the grid was already gone). Not called
        /// when the close was refused. Returns a fragment for the <c>Despawned</c> line, or null.</summary>
        public Func<string, Dictionary<string, long>, string> StationClosed;

        /// <summary>Grids whose turrets vanilla's resource generator must leave alone — read by
        /// <see cref="StationTurretRefillPatch"/>. Game thread only.</summary>
        internal static readonly HashSet<long> StockFedGrids = new HashSet<long>();

        /// <summary>Whether a station's tagged containers are filled from its node's stock rather than
        /// left to vanilla. Unset means vanilla's.</summary>
        public Func<bool> LootFromStock;

        /// <summary>A loot-fed station has just stood: its node id, every grid of its assembly, and
        /// whether its guns are fed from stock. Returns a fragment for the <c>Spawned</c> line, or null.</summary>
        public Func<string, List<MyCubeGrid>, bool, string> LootStood;

        /// <summary>A loot-fed station grid has been closed: its node id and what its loot blocks were
        /// read to hold just before. Not called when the close was refused. Returns a fragment for the
        /// <c>Despawned</c> line, or null.</summary>
        public Func<string, Dictionary<string, long>, string> LootClosed;

        /// <summary>Grids whose containers vanilla's resource generator must leave alone — read by
        /// <see cref="StationTurretRefillPatch"/>. Game thread only.</summary>
        internal static readonly HashSet<long> LootFedGrids = new HashSet<long>();

        // Every grid a station's spawn produced, keyed by the station's grid. A prefab can be several
        // grids that no mechanical group joins -- the Factorum StorageFacility is twenty-six -- so the
        // station grid's own group is not the station. Game thread only; a station grid is never saved,
        // so nothing here needs to survive a restart.
        private static readonly Dictionary<long, long[]> _assemblies = new Dictionary<long, long[]>();

        private static void RecordAssembly(long stationGridId, List<MyCubeGrid> spawned)
        {
            var ids = new List<long>(spawned.Count);
            for (int i = 0; i < spawned.Count; i++)
                if (spawned[i] != null) ids.Add(spawned[i].EntityId);
            _assemblies[stationGridId] = ids.ToArray();
        }

        /// <summary>Every live grid of the station standing on <paramref name="stationGridId"/>: each
        /// grid its spawn produced, with their mechanical groups, appended to <paramref name="into"/>
        /// once each. A station this process did not spawn yields its grid's own group.</summary>
        internal static void CollectStation(long stationGridId, List<MyCubeGrid> into)
        {
            GridAssembly.Collect(stationGridId, false, into);
            long[] parts;
            if (!_assemblies.TryGetValue(stationGridId, out parts)) return;
            var part = new List<MyCubeGrid>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == stationGridId) continue;
                part.Clear();
                GridAssembly.Collect(parts[i], false, part);
                for (int g = 0; g < part.Count; g++)
                    if (!into.Contains(part[g])) into.Add(part[g]);
            }
        }

        private static MyCubeGrid Largest(List<MyCubeGrid> grids)
        {
            MyCubeGrid largest = null;
            for (int i = 0; i < grids.Count; i++)
                if (grids[i] != null && (largest == null || grids[i].BlocksCount > largest.BlocksCount)) largest = grids[i];
            return largest;
        }

        private bool _cacheClearMissLogged;

        private readonly Dictionary<string, MaterializedStation> _materialized =
            new Dictionary<string, MaterializedStation>();

        // nodeId -> the reason BeginSpawn last declined to spawn it, so LogSpawnDecline can report a
        // change of state instead of repeating itself on every one of UpdateObservation's ticks.
        // Cleared when the node finally spawns, so a recurrence is reported again.
        private readonly Dictionary<string, string> _declineReason = new Dictionary<string, string>();

        /// <summary>Where a surface station on steep ground actually stands, per node, for this
        /// session. Never persisted: the station record and the node keep the original site, so the
        /// drift heal has nothing to undo, and the deterministic search lands the station in the same
        /// place on every spawn. Keyed on the record's position so a moved record is measured again.</summary>
        private readonly Dictionary<string, SurfaceSite> _surfaceSites = new Dictionary<string, SurfaceSite>();

        private struct SurfaceSite
        {
            public Vector3D Recorded;     // the station record's position this was measured from
            public string Prefab;
            public Vector3D Position;     // where the grid origin goes
            public Vector3 Up, Forward;
            public SurfaceSiteChoice Choice;
        }

        /// <summary>
        /// Reports, once per change, that <see cref="BeginSpawn"/> looked at a node and did not spawn
        /// it. Exists because every one of those paths used to return in silence, which made the
        /// resulting log unreadable in the one situation that matters: a station that will not come
        /// back. Live 2026-08-29, node N007 sat unspawned for eight minutes across a reboot with a
        /// player beside it and NOT ONE line in either log -- so "BeginSpawn declined" and "BeginSpawn
        /// was never called" were indistinguishable, and neither could be ruled out.
        ///
        /// <para>Deduplicated on the reason rather than rate-limited: UpdateObservation reaches every
        /// founded node on every tick, so an undeduplicated line here would be thousands per second.
        /// Keyed per node, so two nodes declining for the same reason are both reported.</para>
        /// </summary>
        private void LogSpawnDecline(string nodeId, string reason)
        {
            string previous;
            if (_declineReason.TryGetValue(nodeId, out previous) && previous == reason) return;
            _declineReason[nodeId] = reason;
            FactionsPlugin.Log.Info("StationMaterializer: not spawning node=" + nodeId + " -- " + reason);
        }

        /// <summary>
        /// Builds the claim over every founded station from the graph alone, without observing
        /// anything. Called once at session load, because that is the only point early enough:
        /// SE reaches <c>MyStationCellGenerator.GenerateObjects</c> from
        /// <c>MyProceduralWorldGenerator.UpdateBeforeSimulation</c>, i.e. on a simulation frame,
        /// while Torch raises <c>TorchSessionState.Loaded</c> from <c>MySession.AfterLoading</c> --
        /// before the first such frame. Claiming on the first <see cref="UpdateObservation"/> tick
        /// instead leaves vanilla unsuppressed for every frame in between.
        /// </summary>
        public void SeedClaims(SpatialGraph graph, IList<FactionState> registry)
        {
            if (graph == null) return;
            RefreshManagedFactions(registry);

            _claimLedger.Reset();
            _claimLedger.Rebuild(graph, DateTime.UtcNow, null);

            _managedIds.Clear();
            foreach (long id in _claimLedger.Ids) _managedIds.Add(id);

            _claimsDirty = false;
            _claimsNextResyncUtc = DateTime.UtcNow.AddSeconds(ClaimResyncSeconds);

            FactionsPlugin.Log.Info("StationMaterializer: claimed " + _managedIds.Count
                + " founded station(s) and the stations of " + _managedFactionIds.Count
                + " registry faction(s) before the first simulation frame.");
        }

        /// <summary>How often the claim set is re-derived from the graph when nothing has signalled
        /// a change. The claim covers the founded set, which moves when a station is founded, purged
        /// or adopted -- all of which call <see cref="MarkClaimsDirty"/>. This is the convergence
        /// bound for a founded-set change that reaches the graph by some path that does not, and it
        /// is why the claim is not left to signals alone.</summary>
        private const double ClaimResyncSeconds = 30.0;

        private DateTime _claimsNextResyncUtc = DateTime.MinValue;
        private static bool _claimsDirty;

        /// <summary>Marks the claim set for rebuild on the next observation tick. Called by anything
        /// that changes which nodes are founded stations.</summary>
        public static void MarkClaimsDirty() { _claimsDirty = true; }

        /// <summary>Re-derives <see cref="_managedIds"/> when something signalled a founded-set
        /// change, or when the resync interval has elapsed.
        ///
        /// <para>This used to run inside <see cref="UpdateObservation"/>, which meant walking every
        /// graph node sixty times a second to notice a set that changes on the order of once an hour.
        /// The claim's guarantee is unchanged: it still covers the whole founded set rather than the
        /// materialised subset, and <see cref="SeedClaims"/> still establishes it before the first
        /// simulation frame, which is the part the vanilla race actually turns on.</para></summary>
        public void RefreshClaimsIfDue(SpatialGraph graph, IList<FactionState> registry, DateTime nowUtc)
        {
            if (graph == null) return;
            if (!_claimsDirty && nowUtc < _claimsNextResyncUtc) return;
            _claimsDirty = false;
            _claimsNextResyncUtc = nowUtc.AddSeconds(ClaimResyncSeconds);
            RefreshManagedFactions(registry);

            _claimLines.Clear();
            _claimLedger.Rebuild(graph, nowUtc, _claimLines);
            Action<string> sink = ClaimLog;
            if (sink != null)
                for (int i = 0; i < _claimLines.Count; i++) sink(_claimLines[i]);
            _claimLines.Clear();

            // Compared, not trusted from Rebuild's answer: BeginSpawn and the purge path both edit
            // _managedIds directly between resyncs. Stable across the overwhelming majority of them.
            if (!_managedIds.SetEquals(_claimLedger.Ids))
            {
                _managedIds.Clear();
                foreach (long id in _claimLedger.Ids) _managedIds.Add(id);
            }
        }

        /// <summary>Spawns stations players have reached and despawns the ones they have left.
        ///
        /// <para><paramref name="nodesNearPlayers"/> is the nodes within the observation radius of
        /// some online player, resolved by the caller through <c>NodeProximityIndex</c>. The spawn
        /// half is driven by that list rather than by a walk of the graph asking each node whether
        /// anyone is near it, so with nobody online it costs nothing; the despawn half is driven by
        /// what is actually standing, which is a set the size of the materialised stations rather
        /// than of the world.</para></summary>
        /// <param name="nodesToKeep">Nodes within the despawn radius of some observer (the observation radius plus
        /// <see cref="FactionRuntime.DespawnHysteresisKm"/>, as for fleet hulls). A standing station outside it closes
        /// once <paramref name="graceSeconds"/> have passed since it left. Null keeps only the near set.</param>
        public void UpdateObservation(SpatialGraph graph, IList<SpatialNode> nodesNearPlayers,
                                      IList<SpatialNode> nodesToKeep = null, double graceSeconds = 0)
        {
            if (graph == null || MySession.Static == null) return;
            DateTime now = DateTime.UtcNow;

            // Pass 1 -- arrivals. Only a node someone has reached can begin a spawn, and only these
            // nodes hold a materialised station's leave timer open.
            _nearScratch.Clear();
            int nearCount = nodesNearPlayers == null ? 0 : nodesNearPlayers.Count;
            for (int i = 0; i < nearCount; i++)
            {
                SpatialNode node = nodesNearPlayers[i];
                if (node == null || node.StableStationId == 0) continue;
                if (node.OwnerFactionTag == null) continue;

                _nearScratch.Add(node.Id);

                MaterializedStation ms;
                bool exists = _materialized.TryGetValue(node.Id, out ms);
                if (exists && ms.SpawnPending) continue;

                if (!exists || ms.GridEntityId == 0) { BeginSpawn(node); continue; }

                if (ms.LeaveTime != DateTime.MinValue)
                {
                    ms.LeaveTime = DateTime.MinValue;
                    _materialized[node.Id] = ms;
                }
            }

            if (nodesToKeep != null)
                for (int i = 0; i < nodesToKeep.Count; i++)
                    if (nodesToKeep[i] != null) _nearScratch.Add(nodesToKeep[i].Id);

            // Pass 2 -- departures and reaping, over what is standing rather than over the world.
            // Snapshotted because closing an entry removes it.
            if (_materialized.Count == 0) { _nearScratch.Clear(); return; }
            _materializedScratch.Clear();
            foreach (string nodeId in _materialized.Keys) _materializedScratch.Add(nodeId);

            for (int i = 0; i < _materializedScratch.Count; i++)
            {
                string nodeId = _materializedScratch[i];
                MaterializedStation ms;
                if (!_materialized.TryGetValue(nodeId, out ms)) continue;
                if (ms.SpawnPending) continue;
                if (ms.GridEntityId == 0) continue;   // pass 1 owns the not-yet-spawned case

                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById(ms.GridEntityId, out grid, false))
                {
                    CloseEntry(nodeId, ref ms);
                    _materialized.Remove(nodeId);
                    continue;
                }

                if (_nearScratch.Contains(nodeId)) continue;   // pass 1 already cleared its timer

                if (ms.LeaveTime == DateTime.MinValue)
                {
                    ms.LeaveTime = now;
                    _materialized[nodeId] = ms;
                }
                if ((now - ms.LeaveTime).TotalSeconds >= graceSeconds)
                {
                    CloseEntry(nodeId, ref ms);
                    _materialized.Remove(nodeId);
                }
            }

            _nearScratch.Clear();
            _materializedScratch.Clear();
        }

        private void BeginSpawn(SpatialNode node)
        {
            if (MySession.Static == null) return;
            MyFaction faction = FindFaction(node.OwnerFactionTag);
            // IsEffectivelyNpc, not SE's IsEveryoneNpc(): a station's own crew bots are faction
            // members whose identities SE never marks NPC, so the raw check reads false for any
            // faction that currently has crew standing anywhere.
            bool isNpc = faction != null && FactionsPlugin.IsEffectivelyNpc(faction);
            bool isAllowedPlayerFaction = !isNpc && (IsPlayerFactionLookup?.Invoke(node.OwnerFactionTag) ?? false);
            if (faction == null || (!isNpc && !isAllowedPlayerFaction))
            {
                LogSpawnDecline(node.Id, faction == null
                    ? "no SE faction for tag '" + node.OwnerFactionTag + "'."
                    : "faction '" + node.OwnerFactionTag + "' is neither all-NPC nor a spawn-allowed player faction.");
                return;
            }

            string nodeId     = node.Id;
            string factionTag = node.OwnerFactionTag;
            long   stableId   = node.StableStationId;

            foreach (IMyFactionStation station in faction.Stations)
            {
                if (station.Id != stableId) continue;

                MyFactionStation concrete = station as MyFactionStation;
                if (concrete == null)
                {
                    LogSpawnDecline(nodeId, "station #" + stableId + " on " + factionTag
                        + " is not a MyFactionStation.");
                    return;
                }

                // A non-zero StationEntityId with a live, correctly-oriented grid means vanilla
                // (or a previous claim) already spawned this station — leave it alone. Pointing
                // at a missing entity is a stale claim from an earlier session (the grid was
                // spawned Save=false and so never reached the sector file): heal it the same way
                // vanilla CloseObjectSeed does, then spawn normally. A live grid whose Up axis has drifted from the local gravity axis
                // (e.g. a vanilla-spawned economy station BeginSpawn never touched before, sitting
                // on uneven terrain) is healed the same way so it picks up correct orientation.
                if (concrete.StationEntityId != 0)
                {
                    MyCubeGrid existing;
                    if (MyEntities.TryGetEntityById(concrete.StationEntityId, out existing, false))
                    {
                        if (!NeedsGravityRealignment(existing))
                        {
                            // Only worth a line when the two sides disagree: we were called because
                            // this node has no _materialized entry, yet SE resolves a live grid for
                            // its claim. That is the state a lost grid would leave behind, and it is
                            // exactly the one that was unreadable on 2026-08-29.
                            if (!_materialized.ContainsKey(nodeId))
                                LogSpawnDecline(nodeId, "station #" + stableId + " already has live grid "
                                    + existing.EntityId + " (correctly oriented); leaving it alone.");

                            // Leaving the grid alone must not mean leaving its safe zone wrong. A
                            // station vanilla spawned first -- and any deep-space one, where
                            // NeedsGravityRealignment can never be true -- takes this return every
                            // time, so the zone it keeps is vanilla's, at AllowedActions 0: landing
                            // gear forbidden. This is the only place such a zone is ever reachable.
                            GrantLandingGearLock(concrete.SafeZoneEntityId, factionTag, stableId);

                            // Adopt it properly rather than merely tolerating it. A grid that came
                            // back from the sector file carries Save=true -- the flag is only ever
                            // cleared on the spawn path -- so leaving it alone pinned it in the save
                            // forever: it never despawned, and every autosave rewrote it. Clear the
                            // flag on the grid and its zone, and register the entry so the ordinary
                            // grace-period despawn takes it from here.
                            AdoptExistingGrid(nodeId, existing, concrete, factionTag, stableId);
                            return;
                        }
                        FactionsPlugin.Log.Info("StationMaterializer: reclaiming misoriented station #"
                            + stableId + " (gridId=" + existing.EntityId + ") for gravity realignment.");
                        var misoriented = new List<MyCubeGrid>();
                        CollectStation(existing.EntityId, misoriented);
                        GridAssembly.CloseCollected(misoriented, false);
                        _assemblies.Remove(existing.EntityId);
                    }
                    MySession.Static.GetComponent<MySessionComponentEconomy>()?.RemoveStationGrid(concrete.StationEntityId);
                    concrete.StationEntityId = 0L;
                }
                if (concrete.SafeZoneEntityId != 0)
                {
                    MySafeZone staleZone;
                    if (MyEntities.TryGetEntityById(concrete.SafeZoneEntityId, out staleZone, false))
                        staleZone.Close();

                    // Last moment this station and its old crew are still connected: a bot's
                    // AnchorEntityId is the SAFE ZONE's id, and the next line throws that id away.
                    DespawnOrphanedCrew(concrete.SafeZoneEntityId, factionTag, stableId, nodeId);

                    concrete.SafeZoneEntityId = 0L;
                }

                Vector3D stPos      = station.Position;
                Vector3  stForward  = (Vector3)concrete.Forward;  // station transform, not world
                Vector3  stUp       = (Vector3)concrete.Up;       // axes -- planetary stations tilt
                string   prefabName = station.PrefabName;

                // The node's own design decides which spawn path runs. A scanned station has saved
                // grids behind it and is built from them synchronously; everything else falls
                // through to the vanilla prefab path below, unchanged.
                MyObjectBuilder_CubeGrid[] customGrids =
                    CustomStationGrids != null && !string.IsNullOrEmpty(node.StationPrefabId)
                        ? CustomStationGrids(node.StationPrefabId)
                        : null;

                // Heal node/station position drift before anything else reads stPos, so the surface
                // branch below re-snaps from the node's own (already surface-corrected) position
                // rather than a stale one. StationFounder.FoundStation seeds a station's position
                // from node.Position, but WorldModel.ReclassifyNodes -- which runs on EVERY boot
                // (FactionRuntime.LoadAll) -- can afterwards move the node itself: Surface nodes onto
                // the terrain, Orbit nodes into the relative-gravity band via SnapToOrbitBand. Nothing
                // propagated that back to the MyFactionStation, and the only pre-existing SetPosition
                // call is the surface branch below (gated on distToSurf < 2000), so an Orbit or
                // DeepSpace node's station could never self-correct: the offset was permanent and
                // re-widened on every restart that re-snapped the node. Live-confirmed 2026-08-19:
                // EXP node N164 sat 3.46 km from its own station, the node landing exactly on
                // Triton's gravity radius -- the SnapToOrbitBand fingerprint.
                //
                // The target is the bare node, for every faction alike. Clearing an obstruction is
                // the free-place probe's job further down, and its result is deliberately not
                // persisted -- so nothing the spawn does can widen this gap on the next boot.
                if (StationPlacement.HasDriftedFromNode(node, stPos.X, stPos.Y, stPos.Z))
                {
                    double driftM = StationPlacement.NodeStationDriftM(node, stPos.X, stPos.Y, stPos.Z);
                    stPos = StationTarget(node);
                    if (StationSetPosition != null)
                        StationSetPosition.Invoke(concrete, new object[] { stPos, stUp, stForward });
                    FactionsPlugin.Log.Info("StationMaterializer: healed " + factionTag + " station #"
                        + stableId + " (node=" + nodeId + ") back onto its node, was "
                        + Math.Round(driftM) + " m adrift.");
                }

                // The prefab's own local bounding box, measured once: the surface branch solves the
                // embed lift from it, and the clearance probe further down builds the station's
                // footprint from it. Null when the prefab cannot be measured; both readers have
                // their own fallback for that.
                BoundingBox? prefabBox = MeasurePrefabBox(customGrids, prefabName);

                // For stations on or near a planet surface: snap to the true terrain point and
                // orient the grid to the true gravity axis. GetSurfacePlacementMatrix's Up is
                // already the radial "away from planet center" direction (confirmed from decompiled
                // MyPlanet.GetSurfacePlacementMatrix — not a terrain-slope normal), so we just take
                // it directly.
                bool surfaceSnapped = false;
                MyPlanet nearPlanet = MyGamePruningStructure.GetClosestPlanet(stPos);
                if (nearPlanet != null)
                {
                    Vector3D surfPoint = nearPlanet.GetClosestSurfacePointGlobal(stPos);
                    double distToSurf = (stPos - surfPoint).Length();
                    if (distToSurf < SurfaceSnapRangeM)
                    {
                        surfaceSnapped = true;
                        MatrixD surfMatrix = nearPlanet.GetSurfacePlacementMatrix(surfPoint);
                        stUp      = (Vector3)surfMatrix.Up;
                        stForward = (Vector3)surfMatrix.Forward;

                        // Snapping the grid origin to the bare terrain point buries whatever part
                        // of the prefab sits below its own local origin (mirrors vanilla
                        // MyStationGenerator.PlaceRandomStation_Outpost, which nudges by a fixed
                        // 0.25 m — an amount that only works because vanilla's own outpost prefabs
                        // are authored with their local origin already near their base). Our station
                        // prefabs don't all follow that convention, so measure the prefab's own local
                        // bounding box (grid-local Y = the axis that maps to world "up" on spawn) and
                        // solve for the vertical offset that leaves only StationEmbedFraction of its
                        // height buried, whatever the prefab's actual shape.
                        double liftAlongUp = 0.0;
                        if (prefabBox.HasValue)
                        {
                            double belowOriginM = Math.Max(0.0, -(double)prefabBox.Value.Min.Y);
                            double totalHeightM = Math.Max(0.0, (double)(prefabBox.Value.Max.Y - prefabBox.Value.Min.Y));
                            double desiredEmbedM = Math.Max(StationEmbedFloorM, totalHeightM * StationEmbedFraction);
                            liftAlongUp = belowOriginM - desiredEmbedM;
                        }
                        stPos = surfPoint + (Vector3D)stUp * liftAlongUp;

                        // Persist the correction onto the station's own stored transform, not just
                        // the local spawn-time variables above. Vanilla's MyStationCellGenerator
                        // reads station.Forward/station.Up directly and runs as a core SE system
                        // independent of (and typically earlier than) any Torch plugin's Update()
                        // tick — it usually wins the very first spawn race, so a fix that only lives
                        // in this method's local variables never actually reaches the grid vanilla
                        // spawns. Correcting the stored data instead means every future spawn
                        // (ours or vanilla's) uses it, and it survives into the world save.
                        if (StationSetPosition != null)
                            StationSetPosition.Invoke(concrete, new object[] { stPos, stUp, stForward });

                        // Steep ground: this spawn stands on the nearest level-enough site instead
                        // (SurfaceSiteSearch). Deliberately after the persist above and applied only
                        // to the locals, like the obstruction bump below: the record and the node
                        // keep the original site, and the same search answers the same way next time.
                        SurfaceSite site = SurfaceSiteFor(nodeId, nearPlanet, stPos, surfPoint,
                            prefabName, prefabBox, liftAlongUp);
                        if (site.Choice.Moved)
                        {
                            stPos     = site.Position;
                            stUp      = site.Up;
                            stForward = site.Forward;
                            FactionsPlugin.Log.Info("StationMaterializer: moved " + factionTag
                                + " station #" + stableId + " (node=" + nodeId + ") "
                                + Math.Round(site.Choice.DistanceM) + " m off steep ground, grade "
                                + site.Choice.OriginalGrade.ToString("0.00", CultureInfo.InvariantCulture) + " -> "
                                + site.Choice.Grade.ToString("0.00", CultureInfo.InvariantCulture)
                                + (site.Choice.Level ? "" : " (best found, still steep)")
                                + " (not persisted).");
                        }
                        else if (!site.Choice.Level)
                        {
                            FactionsPlugin.Log.Warn("StationMaterializer: " + factionTag + " station #" + stableId
                                + " (node=" + nodeId + ") stands on grade "
                                + site.Choice.OriginalGrade.ToString("0.00", CultureInfo.InvariantCulture)
                                + " and nothing within " + Math.Round(SurfaceSiteSearch.MaxMoveM(site.Choice.ReachM))
                                + " m is better -- spawning on the node regardless.");
                        }
                    }
                }

                // Gravity alignment for a station that is IN a gravity well but nowhere near a
                // surface -- the Cauldron Ring above all. Measured 2026-08-29 from the live
                // planets.json and world_features.json: the gas giant's gravity reaches 304.9 km
                // while its three inner ore bands span 195-271.7 km, so every station on them
                // stands in real gravity ~185-262 km from the nearest surface. You walk inside
                // them sideways and cannot anchor a ship.
                //
                // DELIBERATELY OUTSIDE the `nearPlanet != null` block above, and that is the whole
                // point of this branch. v0.79.0 shipped it nested inside, which made it unreachable
                // for exactly the stations it was written for: GetClosestPlanet builds a DEGENERATE
                // box at the query point and asks the VOXEL tree what overlaps it, so it answers
                // only inside a planet's own AABB -- ~32.8 km across for this gas giant (9.5 km
                // surface radius rounded up to a power-of-two storage cube). At 240 km it returns
                // null. Gravity is a separate mechanism that reaches far further, so the pull is
                // available precisely where the planet is not. Live proof it never ran: zero
                // `gravity-aligned` lines all day, and zero `reclaiming misoriented` beside them,
                // because NeedsGravityRealignment failed at the same gate.
                //
                // Position is untouched -- the node is where the station belongs (see the drift heal
                // above) and there is no terrain to snap to. Only the axes change.
                Vector3 gravityPull;
                if (!surfaceSnapped && TryGetNaturalGravity(stPos, out gravityPull))
                {
                    Vec3 alignedUp, alignedForward;
                    if (StationPlacement.GravityAlignedOrientationFromPull(
                            new Vec3(gravityPull.X, gravityPull.Y, gravityPull.Z),
                            new Vec3(stForward.X, stForward.Y, stForward.Z),
                            out alignedUp, out alignedForward))
                    {
                        Vector3 newUp  = new Vector3(alignedUp.X, alignedUp.Y, alignedUp.Z);
                        Vector3 newFwd = new Vector3(alignedForward.X, alignedForward.Y, alignedForward.Z);
                        bool changed = newUp != stUp || newFwd != stForward;
                        stUp      = newUp;
                        stForward = newFwd;

                        // Persisted for the same reason the surface branch persists: vanilla's
                        // MyStationCellGenerator reads station.Forward/Up directly and can win the
                        // spawn race, so a correction living only in these locals never reaches the
                        // grid vanilla spawns.
                        if (StationSetPosition != null)
                            StationSetPosition.Invoke(concrete, new object[] { stPos, stUp, stForward });
                        if (changed)
                            FactionsPlugin.Log.Info("StationMaterializer: gravity-aligned " + factionTag
                                + " station #" + stableId + " (node=" + nodeId + ") to a pull of "
                                + gravityPull.Length().ToString("0.00") + " m/s2.");
                    }
                }

                // Clear the spawn point of obstructions -- but only a real one. The displacement is
                // recomputed on every materialization and never persisted, so a base that moves away
                // stops costing this station its position. Deliberately AFTER both StationSetPosition
                // calls above and applied only to the local stPos: from here it reaches the safe zone
                // and the spawn and nothing else, leaving node.Position the single answer to where
                // the station belongs.
                //
                // The probe is TWO steps, and the first one is the point. FindFreePlaceCustom on its
                // own answers "is a 150 m sphere around the node clear?", which is the whole safe
                // zone -- so a player leaving a ship parked in the safe zone over a restart moved the
                // station instead. Live-confirmed 2026-08-27..29 on the standalone test server: five
                // bumps, every one of them 150 m or 300 m, i.e. exactly one or two ring steps of
                // radius=150 with nothing physically in the station's way:
                //   bumped IGSL station #867634283999268577 (node=N002) 300 m clear of an obstruction
                //   bumped ISBT station #889509180942242533 (node=N007) 150 m clear of an obstruction
                // So ask the narrower question first: does anything actually overlap the volume the
                // station prefab will occupy? Only if something does is FindFreePlaceCustom called to
                // find somewhere to put it -- and then at the prefab's own radius rather than the safe
                // zone's, so the displacement is the size of the problem and not of the zone. It still
                // bounds its own search at ceil(maxTestCount/testsPerDistance)=4 rings of `radius`,
                // so no clamp of our own is needed.
                //
                // Both steps are skipped on the surface branch: IsShapePenetrating is unreliable
                // against terrain, and the ring search would fight the embed lift computed above.
                if (!surfaceSnapped)
                {
                    MatrixD spawnMatrix = MatrixD.CreateWorld(stPos, (Vector3D)stForward, (Vector3D)stUp);
                    string obstruction;
                    if (StationFootprintObstructed(prefabBox, spawnMatrix, stableId, out obstruction))
                    {
                        float probeRadius = PrefabClearanceRadiusM(prefabBox);
                        Vector3D? freePos = MyEntities.FindFreePlaceCustom(stPos, probeRadius);
                        if (!freePos.HasValue)
                        {
                            FactionsPlugin.Log.Warn("StationMaterializer: no free placement within "
                                + (probeRadius * 4) + " m of node=" + nodeId + " for " + factionTag
                                + " station #" + stableId + " (blocked by " + obstruction
                                + ") -- spawning on the node regardless.");
                        }
                        else if (freePos.Value != stPos)
                        {
                            double bumpM = (freePos.Value - stPos).Length();
                            stPos = freePos.Value;
                            FactionsPlugin.Log.Info("StationMaterializer: bumped " + factionTag
                                + " station #" + stableId + " (node=" + nodeId + ") "
                                + Math.Round(bumpM) + " m clear of " + obstruction + " (not persisted).");
                        }
                    }
                }

                string   stType     = station.Type.ToString();
                string   stName     = concrete.Name;
                MyFaction mFaction  = faction;
                IMyFaction iFaction = faction;

                // Belt and braces for a station founded since the last observation tick, which the
                // graph refresh has not seen yet.
                // One Factions spawn at a time, a spacing apart (SpawnPacing). Refused: the next
                // observation pass comes back here and asks again.
                double pacedWait;
                if (!SpawnPacing.Pacer.TryBegin(SpawnPacing.StationKey(nodeId), DateTime.UtcNow, out pacedWait)) return;

                _managedIds.Add(stableId);
                _materialized[nodeId] = new MaterializedStation
                {
                    SpawnPending    = true,
                    FactionTag      = factionTag,
                    StableStationId = stableId
                };

                // FAC-443 option 3: a single-grid design is added over several frames (GridAssembler);
                // the one-frame spawn below is its fallback.
                if (customGrids != null && customGrids.Length == 1 && !_assemblyRefused.Contains(nodeId))
                {
                    MyObjectBuilder_CubeGrid[] prepared =
                        CustomPrefabSpawner.PrepareStatic(customGrids, stPos, stUp, stForward, mFaction.FounderId);
                    string design = node.StationPrefabId;
                    bool begun = prepared != null && GridAssembler.Instance.Begin(nodeId, prepared, false,
                        grids => AdoptSpawnedStation(nodeId, factionTag, stableId, concrete, mFaction, iFaction,
                                                     grids, stPos, stType, stName, stampBeacons: true),
                        why =>
                        {
                            // Not again this process: the next pass spawns it in one frame. Retrying on
                            // every pass made 92 attempts in 108 s on the first live run.
                            _assemblyRefused.Add(nodeId);
                            SpawnPacing.Pacer.Landed(SpawnPacing.StationKey(nodeId), DateTime.UtcNow);
                            _materialized.Remove(nodeId);
                            FactionsPlugin.Log.Warn("StationMaterializer: assembly of '" + design + "' failed, " + why
                                                    + "; spawning it in one frame instead.");
                        });
                    if (!begun)
                    {
                        _assemblyRefused.Add(nodeId);
                        SpawnPacing.Pacer.Landed(SpawnPacing.StationKey(nodeId), DateTime.UtcNow);
                        _materialized.Remove(nodeId);
                        FactionsPlugin.Log.Warn("StationMaterializer: could not start assembling scanned design '"
                            + design + "' at node=" + nodeId + ".");
                    }
                    _declineReason.Remove(nodeId);
                    return;
                }

                if (customGrids != null)
                {
                    // Synchronous: the design is already in memory as object builders, so there is
                    // no prefab definition to load and no callback to wait for. Bookkeeping matches
                    // the async path exactly -- the SpawnPending entry written just above is
                    // replaced by the real one inside the adoption, and a failure clears it here so
                    // the next approach retries instead of being skipped forever.
                    List<MyCubeGrid> customSpawned;
                    if (CustomPrefabSpawner.TrySpawnStatic(customGrids, stPos, stUp, stForward,
                            mFaction.FounderId, out customSpawned))
                    {
                        AdoptSpawnedStation(nodeId, factionTag, stableId, concrete, mFaction, iFaction,
                            customSpawned, stPos, stType, stName, stampBeacons: true);
                    }
                    else
                    {
                        SpawnPacing.Pacer.Landed(SpawnPacing.StationKey(nodeId), DateTime.UtcNow);
                        _materialized.Remove(nodeId);
                        FactionsPlugin.Log.Warn("StationMaterializer: could not stand scanned design '"
                            + node.StationPrefabId + "' at node=" + nodeId + ".");
                    }
                    _declineReason.Remove(nodeId);
                    return;
                }

                var resultList = new List<MyCubeGrid>();
                var callbacks  = new Stack<Action>();

                callbacks.Push(() => AdoptSpawnedStation(nodeId, factionTag, stableId, concrete,
                    mFaction, iFaction, resultList, stPos, stType, stName, stampBeacons: false,
                    voxelPrefab: prefabName));

                var spawnOpts = SpawningOptions.SetAuthorship
                              | SpawningOptions.ReplaceColor
                              | SpawningOptions.UseOnlyWorldMatrix;
                string beaconName = string.IsNullOrEmpty(stName) ? null : stName;

                if (SpawnPrefabInternalM != null && SpawnPropsType != null)
                {
                    // Use the internal path so we can pass faction.CustomColor; mirrors vanilla's
                    // MyStationCellGenerator.GenerateObjects exactly.
                    object props = Activator.CreateInstance(SpawnPropsType, nonPublic: true);
                    AccessTools.Property(SpawnPropsType, "ResultList").SetValue(props, resultList);
                    AccessTools.Property(SpawnPropsType, "PrefabName").SetValue(props, prefabName);
                    AccessTools.Property(SpawnPropsType, "Position").SetValue(props, stPos);
                    AccessTools.Property(SpawnPropsType, "Forward").SetValue(props, stForward);
                    AccessTools.Property(SpawnPropsType, "Up").SetValue(props, stUp);
                    AccessTools.Property(SpawnPropsType, "BeaconName").SetValue(props, beaconName);
                    AccessTools.Property(SpawnPropsType, "SpawningOptions").SetValue(props, spawnOpts);
                    AccessTools.Property(SpawnPropsType, "OwnerId").SetValue(props, mFaction.FounderId);
                    AccessTools.Property(SpawnPropsType, "Color").SetValue(props, mFaction.CustomColor);
                    AccessTools.Property(SpawnPropsType, "UpdateSync").SetValue(props, true);
                    Action onSuccess = callbacks.Pop();  // our single closure
                    Action onFailed  = () =>
                    {
                        SpawnPacing.Pacer.Landed(SpawnPacing.StationKey(nodeId), DateTime.UtcNow);
                        _materialized.Remove(nodeId);
                        FactionsPlugin.Log.Warn("StationMaterializer: SpawnPrefabInternal failed node=" + nodeId);
                    };
                    SpawnPrefabInternalM.Invoke(MyPrefabManager.Static,
                        new object[] { props, onSuccess, onFailed });
                }
                else
                {
                    // Fallback: public API — no faction colour tinting.
                    FactionsPlugin.Log.Warn("StationMaterializer: SpawnPrefabInternal unavailable, falling back to public SpawnPrefab.");
                    MyPrefabManager.Static.SpawnPrefab(
                        resultList, prefabName, stPos, stForward, stUp,
                        beaconName:      beaconName,
                        spawningOptions: SpawningOptions.SetAuthorship | SpawningOptions.UseOnlyWorldMatrix,
                        ownerId:         mFaction.FounderId,
                        updateSync:      true,
                        callbacks:       callbacks);
                }
                _declineReason.Remove(nodeId);   // it got as far as a spawn; a later decline is news again
                return;
            }

            // Fell out of the loop: the node names a StableStationId this faction does not have.
            LogSpawnDecline(nodeId, "faction " + factionTag + " has no station #" + stableId
                + " (" + CountStations(faction) + " station(s) on record).");
        }

        /// <summary>Everything that turns freshly-spawned grids into a working station: the claim SE
        /// reads, the ephemerality flags, the name, the safe zone, the economy registration and the
        /// contract board.
        ///
        /// <para>Shared by both spawn paths. The vanilla prefab path reaches it from
        /// <c>SpawnPrefabInternal</c>'s success callback; a player-scanned design reaches it inline,
        /// because <see cref="CustomPrefabSpawner.TrySpawnStatic"/> is synchronous. That is the only
        /// difference between them, and <paramref name="stampBeacons"/> is its one consequence: the
        /// vanilla spawner names beacons itself from the spawn properties, and nothing names a
        /// capture's.</para>
        ///
        /// <para><paramref name="spawned"/> may hold more than one grid -- a design's subgrids, which
        /// is how a station's Store, Contract or Services block can sit on a rotor-mounted part, or a
        /// prefab's separate grids. The largest is the station, and all of them are recorded as its
        /// assembly (<see cref="CollectStation"/>); every one of them is made ephemeral, or the ones left behind would
        /// be pinned in the save forever.</para>
        ///
        /// <para><paramref name="voxelPrefab"/> is the SE prefab whose spawn group's rock
        /// (<see cref="StationVoxels"/>) stands around the grids; null for a scanned design, which has
        /// no spawn group.</para></summary>
        private void AdoptSpawnedStation(string nodeId, string factionTag, long stableId,
            MyFactionStation concrete, MyFaction mFaction, IMyFaction iFaction,
            List<MyCubeGrid> spawned, Vector3D stPos, string stType, string stName, bool stampBeacons,
            string voxelPrefab = null)
        {
            // The catch below clears this node's SpawnPending flag and logs, so a failure partway
            // through -- CrateSafeZone throwing because the spawn point is physically obstructed is
            // the one seen live -- leaves the node retryable on the next observation tick instead of
            // stuck: UpdateObservation skips any node whose SpawnPending is still true, forever and
            // with no trace. ForceRespawn remains the only recovery for the rarer case where
            // SpawnPrefabInternal never invokes either callback at all, which no handler here can
            // see.
            SpawnPacing.Pacer.Landed(SpawnPacing.StationKey(nodeId), DateTime.UtcNow);
            SpawnPacing.NoteLanding(SpawnPacing.StationKey(nodeId),
                DebugTraces.SpawnCost ? " kind=Station grids=" + (spawned != null ? spawned.Count : 0) : null);
            try
            {
              _materialized.Remove(nodeId);

              if (spawned == null || spawned.Count == 0)
              {
                  FactionsPlugin.Log.Warn("StationMaterializer: spawn failed node=" + nodeId);
                  return;
              }

              // The largest grid is the station. A prefab's grids are not ordered by importance: the
              // Factorum StorageFacility's first grid is a scrap of debris, and a station keyed on it
              // closed and read only that scrap, leaving the real station standing unread.
              MyCubeGrid grid = Largest(spawned);
              if (grid == null)
              {
                  FactionsPlugin.Log.Warn("StationMaterializer: spawn failed node=" + nodeId);
                  return;
              }
              RecordAssembly(grid.EntityId, spawned);

              // Claim the station vanilla-style (Economy 2 MyStationCellGenerator):
              // the cell generator now sees StationEntityId != 0 and skips its own spawn.
              concrete.StationEntityId = grid.EntityId;

              // Keep materialized grids aligned with the station catalogue and dashboard:
              // curated names are tag-free, with a tag-free generic fallback.
              string gridName = string.IsNullOrEmpty(stName)
                  ? stType.ToString() + " #" + stableId
                  : stName;
              grid.DisplayName = gridName;

              // Every grid, not just the first. A design's subgrids are as ephemeral as the hull
              // they hang off: MyEntities.Save gates on EntityFlags.Save and nothing else, so one
              // left with the flag set is pinned in the sector file forever. IsGenerated is what
              // MySessionComponentTrash reads, and it has the same reach.
              for (int gi = 0; gi < spawned.Count; gi++)
              {
                  MyCubeGrid part = spawned[gi];
                  if (part == null) continue;
                  if (GridIsGeneratedSetter != null)
                      GridIsGeneratedSetter.Invoke(part, new object[] { true });
                  part.Save = false;
                  // The vanilla spawner stamps beacons from its own BeaconName property; nothing
                  // names a capture's, so the custom path asks for it here.
                  if (stampBeacons) StationFounder.RenameBeacons(part, gridName);
              }

              // The safe zone is what makes a station unshootable, so a faction whose stations
              // are meant to be a target does not get one. Every reader of SafeZoneEntityId
              // already guards on 0 -- it is the same value a zone that failed to create
              // leaves behind -- so this is a state the code already handles rather than a
              // new one.
              bool protectedStation = SpawnsSafeZoneLookup == null || SpawnsSafeZoneLookup(factionTag);
              // A station whose zone Core says is DOWN -- a raid landed, or a player is inside
              // a breach -- must not come back up protected simply because its grid respawned.
              // The zone is re-created from scratch on every materialisation, so this is the
              // only place that state can be honoured at creation time; the siege tick handles
              // it from then on.
              bool zoneStartsEnabled = ZoneShouldStartEnabled == null || ZoneShouldStartEnabled(nodeId);
              MySafeZone zone = null;
              if (protectedStation)
              {
                  // Safe zone name matches vanilla MyFactionStation.CreateSafeZone.
                  string szName = iFaction.Tag + " " + stableId + " " + stName;
                  MatrixD transform = MatrixD.CreateWorld(stPos, Vector3D.Forward, Vector3D.Up);
                  // Blacklist with empty lists = everyone can enter; no combat/building inside.
                  // (Whitelist with empty lists would be a physical barrier — nobody allowed in.)
                  zone = MySessionComponentSafeZones.CrateSafeZone(
                      transform,
                      MySafeZoneShape.Sphere,
                      MySafeZoneAccess.Blacklist,
                      null,
                      null,
                      SafeZoneRadius,
                      enable:          zoneStartsEnabled,
                      isVisible:       false,
                      color:           new Vector3(0f, 0.09f, 0.196f),
                      visualTexture:   "SafeZone_Texture_Aura",
                      safeZoneBlockId: 0L,
                      name:            szName
                  ) as MySafeZone;
              }

              concrete.SafeZoneEntityId = zone != null ? zone.EntityId : 0L;

              if (zone != null)
              {
                  // Let players anchor inside the zone. CrateSafeZone never touches
                  // AllowedActions and MyObjectBuilder_SafeZone.AllowedActions has no
                  // initialiser, so every zone it makes -- vanilla's own station zones
                  // included -- comes out at 0, and MySafeZone.IsActionAllowed is
                  // `(AllowedActions & action) == action`: a set bit is a PERMISSION, so 0
                  // forbids everything. Landing gear was therefore dead inside a station's
                  // zone, which is the one place a ship most wants to be parked and, in the
                  // Ring, the one place gravity will drag it away if it is not.
                  // Only LandingGearLock is granted -- shooting, grinding, building and the
                  // rest stay forbidden exactly as before.
                  zone.AllowedActions |= MySafeZoneAction.LandingGearLock;
                  zone.Save = false;   // ephemeral with its grid, same reason
              }

              // Register with the economy session component (store/contract terminals) and
              // restock generated resources, as vanilla does after every station spawn.
              MySession.Static.GetComponent<MySessionComponentEconomy>()?.AddStationGrid(grid.EntityId);
              // Before the refill below, which is where vanilla builds the block cache the turret
              // filter edits.
              bool stockFed = TurretsFromStock != null && TurretsFromStock();
              if (stockFed)
                  for (int gi = 0; gi < spawned.Count; gi++)
                      if (spawned[gi] != null) StockFedGrids.Add(spawned[gi].EntityId);
              bool lootFed = LootFromStock != null && LootFromStock();
              if (lootFed)
                  for (int gi = 0; gi < spawned.Count; gi++)
                      if (spawned[gi] != null) LootFedGrids.Add(spawned[gi].EntityId);
              if (StationResourcesUpdate != null)
                  StationResourcesUpdate.Invoke(concrete, new object[] { mFaction });

              // CanBeGenerated (FactionContractStrategy) skips contract generation for any
              // Factions-managed station with no live grid (docs/todo.md's "Contract generation
              // burst" entry -- avoids the expensive per-station work in vanilla's periodic
              // EconomyTick for the large majority of founded stations nobody is near). This
              // station just became reachable, so its board would otherwise sit empty until the
              // next periodic tick (up to 10 minutes) -- populate it right now instead.
              if (FactionContractStrategy.IsRegistered(factionTag))
                  FactionContractStrategy.RefreshBoardOnSpawn(concrete, mFaction);

              // Clear trees/bushes that would clip into planetary station prefabs.
              MyPlanetEnvironmentSessionComponent planetEnv =
                  MySession.Static.GetComponent<MyPlanetEnvironmentSessionComponent>();
              if (planetEnv != null)
              {
                  BoundingBoxD bbox = new BoundingBoxD(stPos - StationFootprintAllowanceM, stPos + StationFootprintAllowanceM);
                  planetEnv.ClearEnvironmentItemsBlocking(bbox);
              }

              _materialized[nodeId] = new MaterializedStation
              {
                  GridEntityId     = grid.EntityId,
                  SafeZoneEntityId = zone != null ? zone.EntityId : 0L,
                  FactionTag       = factionTag,
                  StableStationId  = stableId,
                  LeaveTime        = DateTime.MinValue
              };

              // The zone's granted actions are appended because there is nowhere else to read
              // them: the zone is Save=false, so it never reaches disk and no persisted file
              // can be checked afterwards. Extending this already-enumerated line rather than
              // adding a new one keeps the Torch log a diagnostic channel, and doubles as the
              // artifact control for this build -- an older DLL's Spawned line has no `allow=`.
              string rock = null;
              if (voxelPrefab != null && FeatureSwitch.IsOn(FeatureSwitch.StationVoxels))
              {
                  try { rock = StationVoxels.Ensure(stableId, voxelPrefab, spawned); }
                  catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "StationMaterializer: station voxels threw node=" + nodeId); }
              }

              // Loot before ammunition: the loot pass clears its blocks, and the munitions pass may
              // put magazines in cargo.
              string loot = null;
              if (lootFed && LootStood != null)
              {
                  try { loot = LootStood(nodeId, spawned, stockFed); }
                  catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "StationMaterializer: station loot threw node=" + nodeId); }
              }

              string ammo = null;
              if (stockFed && StationStood != null)
              {
                  try { ammo = StationStood(nodeId, spawned); }
                  catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "StationMaterializer: station munitions load threw node=" + nodeId); }
              }

              string spawnMsg = "Spawned " + factionTag + " " + stType + " #" + stableId
                  + " (gridId=" + grid.EntityId + ")"
                  + " allow=" + (zone != null ? zone.AllowedActions.ToString() : "no-safezone")
                  + (ammo != null ? " " + ammo : "")
                  + (loot != null ? " " + loot : "")
                  + (rock != null ? " " + rock : "");
              FactionsPlugin.Log.Info(spawnMsg);
              if (DebugEnabled) DebugBroadcast?.Invoke(spawnMsg);
            }
            catch (Exception ex)
            {
              _materialized.Remove(nodeId);
              // StationEntityId was claimed at the top of the try. Releasing it here is what
              // makes the next player approach retry from scratch: left set, it points at a
              // grid with no safe zone, no economy registration and no restock, which both
              // BeginSpawn and vanilla's cell generator then read as "already spawned".
              concrete.StationEntityId = 0L;
              concrete.SafeZoneEntityId = 0L;
              FactionsPlugin.Log.Warn(ex, "StationMaterializer: spawn callback threw node=" + nodeId);
            }
        }

        /// <summary>Station count for a decline message; <see cref="MyFaction.Stations"/> is an
        /// enumerable, not a list.</summary>
        private static int CountStations(MyFaction faction)
        {
            int n = 0;
            if (faction != null) foreach (IMyFactionStation s in faction.Stations) n++;
            return n;
        }

        /// <summary>Where a node's station belongs in world-space metres -- the node itself plus any
        /// deliberate founding offset. Thin SE-side adapter over <see cref="StationPlacement
        /// .StationTargetM"/> so the drift threshold and the repair target stay single-sourced in Core.</summary>
        private static Vector3D StationTarget(SpatialNode node)
        {
            double x, y, z;
            StationPlacement.StationTargetM(node, out x, out y, out z);
            return new Vector3D(x, y, z);
        }

        /// <summary>
        /// Grants <see cref="MySafeZoneAction.LandingGearLock"/> on an ALREADY-LIVE safe zone --
        /// one this materializer did not just create, so clients may already hold a copy of it.
        ///
        /// <para>That is why this goes the long way round rather than assigning the property: a
        /// freshly built zone is set before it has ever replicated, but an existing one has to be
        /// pushed. <c>MySessionComponentSafeZones.UpdateSafeZone(ob, sync: true)</c> re-inits the
        /// server-side zone from an object builder and broadcasts it, which is the same path the
        /// admin menu's own checkbox takes.</para>
        ///
        /// <para>No-ops when the zone is missing, already granted, or refused by SE's own
        /// <c>IsSafeZoneColliding</c> gate inside <c>UpdateSafeZone</c> -- a refusal is silent
        /// there, so the grant is read back and a failure logged rather than assumed.</para>
        /// </summary>
        /// <summary>
        /// Takes over a station grid this process did not spawn -- one loaded from the sector file,
        /// or one vanilla's cell generator won the race for -- so it behaves like any other
        /// materialized station instead of living forever.
        ///
        /// <para>Two things make it ephemeral: <c>Save = false</c> on the grid and its safe zone
        /// (<c>MyEntities.Save</c> gates on that bit and nothing else, so a grid read back from the
        /// save is pinned until it is cleared), and a <see cref="_materialized"/> entry, which is
        /// what <see cref="UpdateObservation"/>'s grace-period branch needs in order to despawn it
        /// when the last player leaves.</para>
        /// </summary>
        private void AdoptExistingGrid(string nodeId, MyCubeGrid grid, MyFactionStation concrete,
                                       string factionTag, long stableId)
        {
            if (grid == null) return;

            bool wasPinned = grid.Save;
            grid.Save = false;

            MySafeZone zone;
            if (concrete.SafeZoneEntityId != 0
                && MyEntities.TryGetEntityById(concrete.SafeZoneEntityId, out zone, false))
                zone.Save = false;

            if (!_materialized.ContainsKey(nodeId))
            {
                MaterializedStation adopted = new MaterializedStation();
                adopted.GridEntityId     = grid.EntityId;
                adopted.SafeZoneEntityId = concrete.SafeZoneEntityId;
                adopted.FactionTag       = factionTag;
                adopted.StableStationId  = stableId;
                adopted.LeaveTime        = DateTime.MinValue;
                adopted.SpawnPending     = false;
                _materialized[nodeId] = adopted;
            }

            // Only worth a line for the case that had no other trace: a grid that was in the save.
            if (wasPinned)
                FactionsPlugin.Log.Info("StationMaterializer: adopted persisted grid " + grid.EntityId
                    + " for " + factionTag + " station #" + stableId + " (node=" + nodeId
                    + "); it will despawn with the node from now on.");
        }

        private static void GrantLandingGearLock(long safeZoneEntityId, string factionTag, long stableId)
        {
            if (safeZoneEntityId == 0) return;

            MySafeZone zone;
            if (!MyEntities.TryGetEntityById(safeZoneEntityId, out zone, false)) return;
            if ((zone.AllowedActions & MySafeZoneAction.LandingGearLock) == MySafeZoneAction.LandingGearLock) return;

            try
            {
                MyObjectBuilder_SafeZone ob = zone.GetObjectBuilder(false) as MyObjectBuilder_SafeZone;
                if (ob == null) return;
                ob.AllowedActions |= MySafeZoneAction.LandingGearLock;
                MySessionComponentSafeZones.UpdateSafeZone(ob, true);

                // Read back: UpdateSafeZone returns void and drops the update silently when SE's
                // collision gate refuses it, so the only honest confirmation is the zone itself.
                if ((zone.AllowedActions & MySafeZoneAction.LandingGearLock) != MySafeZoneAction.LandingGearLock)
                {
                    FactionsPlugin.Log.Warn("StationMaterializer: safe zone " + safeZoneEntityId + " for "
                        + factionTag + " station #" + stableId
                        + " refused the landing-gear grant; it still reads " + zone.AllowedActions + ".");
                    return;
                }
                FactionsPlugin.Log.Info("StationMaterializer: granted landing-gear lock on the existing safe zone "
                    + safeZoneEntityId + " for " + factionTag + " station #" + stableId
                    + " (now " + zone.AllowedActions + ").");
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "StationMaterializer: landing-gear grant threw for safe zone "
                    + safeZoneEntityId + " (" + factionTag + " station #" + stableId + ").");
            }
        }

        /// <summary>
        /// Removes the crew bots left behind by a previous incarnation of this station, identified
        /// by the safe-zone id they are anchored to. Returns how many were removed.
        ///
        /// <para><b>Why this is ours to do.</b> SE already has an orphan sweep --
        /// <c>MyBotAnchorComponent.CleanupOrphanedHumanoidBots</c> -- and a stale claim of ours
        /// defeats it. The order in <c>BeforeStart</c> is <c>SyncStationAnchors()</c> and only then
        /// <c>m_orphanCleanupPending = true</c>, and <c>SyncStationAnchors</c> registers an anchor for
        /// every station matching <c>{ SafeZoneEntityId: not 0, StationEntityId: not 0 }</c>, keyed on
        /// the safe-zone id. Both ids are persisted on <c>MyObjectBuilder_Station</c> and survive a
        /// reboot; the entities they name do not, because both are spawned <c>Save = false</c>. So on
        /// every boot we hand SE a phantom anchor under the dead zone's id, and its cleanup then skips
        /// exactly the bots that need removing:
        /// <c>if (bot.AnchorEntityId != 0 &amp;&amp; !m_anchors.ContainsKey(bot.AnchorEntityId))</c> is
        /// false for them. <c>CullClosedAnchors</c> drops the phantom two seconds later, by which time
        /// <c>m_orphanCleanupPending</c> is already false and the sweep will not run again this
        /// session. Live 2026-08-29: 3 <c>MyObjectBuilder_Character</c> in a save holding 4 CubeGrids,
        /// and the operator watched a station respawn the instant one bot was deleted by hand.
        ///
        /// <para>Removing the phantom anchor instead would be the tidier fix and is not available:
        /// it would mean zeroing those ids before SE's <c>BeforeStart</c>, and a Torch plugin's
        /// session-load hook has no guaranteed ordering against a session component's. Doing it here
        /// is ordering-free -- this runs when a station is about to materialize, which is the only
        /// moment that matters.</para>
        ///
        /// <para>The match is exact, on this station's own stale zone id. A bot anchored to a live
        /// station, or carrying no anchor at all, is never touched.</para>
        /// </summary>
        internal static int DespawnOrphanedCrew(long staleSafeZoneEntityId, string factionTag, long stableId, string nodeId)
        {
            if (staleSafeZoneEntityId == 0 || MySession.Static == null) return 0;
            if (MyAIComponent.Static == null || MyAIComponent.Static.Bots == null) return 0;

            List<int> doomed = null;
            try
            {
                foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
                {
                    // SerialId 0 is a real player's own character; bots take the nonzero serials.
                    if (player == null || player.Id.SerialId == 0) continue;

                    MyAgentBot bot = MyAIComponent.Static.Bots.TryGetBot<MyAgentBot>(player.Id.SerialId);
                    if (bot == null || bot.AnchorEntityId != staleSafeZoneEntityId) continue;

                    if (doomed == null) doomed = new List<int>();
                    doomed.Add(player.Id.SerialId);
                }
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "StationMaterializer: crew sweep failed to enumerate bots for "
                    + factionTag + " station #" + stableId + " (node=" + nodeId + ").");
                return 0;
            }

            if (doomed == null) return 0;

            int removed = 0;
            foreach (int serialId in doomed)
            {
                try
                {
                    MyPlayer.PlayerId pid = new MyPlayer.PlayerId(Sync.MyId, serialId);
                    MyPlayer player;
                    if (MySession.Static.Players.TryGetPlayerById(pid, out player) && player.Character != null)
                    {
                        // Unseat first: RemoveBot on a seated bot leaves the cockpit holding a dead
                        // pilot reference. Mirrors CleanupOrphanedHumanoidBots' own ordering.
                        MyCockpit cockpit = player.Character.Parent as MyCockpit;
                        if (cockpit != null) cockpit.RemovePilot();
                    }
                    MyAIComponent.Static.RemoveBot(pid, true);
                    removed++;
                }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn(ex, "StationMaterializer: could not remove orphaned crew bot serial="
                        + serialId + " for " + factionTag + " station #" + stableId + ".");
                }
            }

            if (removed > 0)
                FactionsPlugin.Log.Info("StationMaterializer: removed " + removed + " orphaned crew bot(s) anchored to "
                    + factionTag + " station #" + stableId + "'s (node=" + nodeId + ") dead safe zone "
                    + staleSafeZoneEntityId + " before respawning it.");
            return removed;
        }

        /// <summary>True when <paramref name="pos"/> lies inside some planet's natural gravity well.
        /// The same test <see cref="NeedsGravityRealignment"/> applies to a standing grid, so the
        /// branch that ORIENTS a station and the check that reclaims a misoriented one agree on
        /// which stations are "in gravity" at all.</summary>
        private static bool IsInNaturalGravity(Vector3D pos)
        {
            Vector3 pull;
            return TryGetNaturalGravity(pos, out pull);
        }

        /// <summary>
        /// The natural-gravity vector at <paramref name="pos"/>, and whether there is any.
        ///
        /// <para><b>Use this, never <c>MyGamePruningStructure.GetClosestPlanet</c>, to decide whether
        /// a position is in gravity.</b> That call builds a DEGENERATE box at the point and asks the
        /// voxel tree what overlaps it, so it answers only inside a planet's own AABB — the gas
        /// giant's is roughly 32.8 km across (a 9.5 km surface radius rounded to a power-of-two voxel
        /// storage cube) while its gravity reaches 304.9 km and the Cauldron Ring's inner bands sit at
        /// 195-271.7 km. Gravity is a different mechanism: <c>MyGravityProviderSystem</c> walks its own
        /// provider collection and asks each one <c>IsPositionInRange</c>. So out on the ring the pull
        /// is available and the planet is not, and anything gated on the planet is dead code there.</para>
        /// </summary>
        private static bool TryGetNaturalGravity(Vector3D pos, out Vector3 pull)
        {
            float gravityStrength;
            pull = MyGravityProviderSystem.CalculateNaturalGravityInPoint(pos, out gravityStrength);
            return gravityStrength > 0f && pull.LengthSquared() > 0f;
        }

        /// <summary>The footprint's horizontal half-diagonal: local X and Z are the horizontal axes
        /// once a surface station stands (grid-local Y maps to world up, see the embed lift).</summary>
        private static double FootprintReachM(BoundingBox? box)
        {
            if (!box.HasValue) return UnmeasuredFootprintReachM;
            double x = Math.Max(Math.Abs((double)box.Value.Min.X), Math.Abs((double)box.Value.Max.X));
            double z = Math.Max(Math.Abs((double)box.Value.Min.Z), Math.Abs((double)box.Value.Max.Z));
            double reach = Math.Sqrt(x * x + z * z);
            return reach > 1.0 ? reach : UnmeasuredFootprintReachM;
        }

        /// <summary>Terrain altitude (distance from the planet's centre, metres) at an east/north
        /// offset on the tangent plane through <paramref name="surfPoint"/>.</summary>
        private static Func<double, double, double> AltitudeSampler(MyPlanet planet, Vector3D surfPoint,
                                                                    out Vector3D east, out Vector3D north)
        {
            Vector3D centre = planet.PositionComp.WorldVolume.Center;
            Vector3D radial = Vector3D.Normalize(surfPoint - centre);
            Vector3D e = Vector3D.CalculatePerpendicularVector(radial);
            Vector3D n = Vector3D.Cross(radial, e);
            east = e;
            north = n;
            return (de, dn) => (planet.GetClosestSurfacePointGlobal(surfPoint + e * de + n * dn) - centre).Length();
        }

        /// <summary>Where this spawn of a surface station stands: the snapped site itself, or the
        /// nearest level-enough one (<see cref="SurfaceSiteSearch"/>). Measured once per node per
        /// session and reused while the record and the design are unchanged.</summary>
        private SurfaceSite SurfaceSiteFor(string nodeId, MyPlanet planet, Vector3D recorded, Vector3D surfPoint,
                                           string prefabName, BoundingBox? prefabBox, double liftAlongUp)
        {
            SurfaceSite cached;
            if (_surfaceSites.TryGetValue(nodeId, out cached)
                && string.Equals(cached.Prefab, prefabName, StringComparison.Ordinal)
                && Vector3D.DistanceSquared(cached.Recorded, recorded) < 1.0)
                return cached;

            Vector3D east, north;
            Func<double, double, double> altitude = AltitudeSampler(planet, surfPoint, out east, out north);
            SurfaceSiteChoice choice = SurfaceSiteSearch.Choose(altitude, FootprintReachM(prefabBox));

            var site = new SurfaceSite { Recorded = recorded, Prefab = prefabName, Choice = choice, Position = recorded };
            if (choice.Moved)
            {
                Vector3D ground = planet.GetClosestSurfacePointGlobal(surfPoint + east * choice.EastM + north * choice.NorthM);
                MatrixD frame = planet.GetSurfacePlacementMatrix(ground);
                site.Up = (Vector3)frame.Up;
                site.Forward = (Vector3)frame.Forward;
                site.Position = ground + frame.Up * liftAlongUp;
            }
            _surfaceSites[nodeId] = site;
            return site;
        }

        /// <summary>
        /// Bridge verb <c>factions sitecheck [apply] [nodeId]</c>: measures the ground under every
        /// surface station this process can see against SE's installation slope limit
        /// (<see cref="SurfaceSiteSearch.MaxGrade"/>) and lists the steep ones, steepest first. With
        /// a node id the full search runs too and the report says where the station would move.
        /// <c>apply</c> respawns each steep station that is standing, so it comes back on level
        /// ground; one that is not standing is moved on its next spawn without being asked.
        /// </summary>
        public StationSiteCheckResult CheckSurfaceSites(SpatialGraph graph, bool apply, string onlyNodeId = null)
        {
            var result = new StationSiteCheckResult { Details = new List<string>() };
            if (MySession.Static == null || graph == null) return result;

            var steep = new List<KeyValuePair<double, string>>();
            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node == null || node.StableStationId == 0 || node.OwnerFactionTag == null) continue;
                if (onlyNodeId != null && !string.Equals(node.Id, onlyNodeId, StringComparison.OrdinalIgnoreCase)) continue;

                MyFaction faction = FindFaction(node.OwnerFactionTag);
                MyFactionStation concrete = faction != null
                    ? faction.GetStationById(node.StableStationId) as MyFactionStation
                    : null;
                if (concrete == null) continue;

                Vector3D pos = concrete.Position;
                MyPlanet planet = MyGamePruningStructure.GetClosestPlanet(pos);
                if (planet == null) continue;
                Vector3D surf = planet.GetClosestSurfacePointGlobal(pos);
                if ((pos - surf).Length() >= SurfaceSnapRangeM) continue;
                result.Checked++;

                MyObjectBuilder_CubeGrid[] customGrids =
                    CustomStationGrids != null && !string.IsNullOrEmpty(node.StationPrefabId)
                        ? CustomStationGrids(node.StationPrefabId)
                        : null;
                double reach = FootprintReachM(MeasurePrefabBox(customGrids, concrete.PrefabName));
                Vector3D east, north;
                Func<double, double, double> altitude = AltitudeSampler(planet, surf, out east, out north);
                double rise;
                double grade = SurfaceSiteSearch.GradeAtNode(altitude, reach, out rise);
                if (grade > result.WorstGrade) result.WorstGrade = grade;
                if (grade <= SurfaceSiteSearch.MaxGrade) continue;
                result.Steep++;

                MyCubeGrid live;
                bool standing = concrete.StationEntityId != 0
                    && MyEntities.TryGetEntityById(concrete.StationEntityId, out live, false)
                    && !live.MarkedForClose;
                string line = node.OwnerFactionTag + " #" + node.StableStationId + " " + node.Id
                    + " (" + (concrete.Name ?? node.DisplayName) + "): grade "
                    + grade.ToString("0.00", CultureInfo.InvariantCulture)
                    + " (" + Math.Round(rise) + " m over " + Math.Round(reach) + " m)"
                    + (standing ? ", standing grid " + concrete.StationEntityId : ", not standing");

                if (onlyNodeId != null)
                {
                    SurfaceSiteChoice choice = SurfaceSiteSearch.Choose(altitude, reach);
                    line += choice.Moved
                        ? " -> moves " + Math.Round(choice.DistanceM) + " m to grade "
                          + choice.Grade.ToString("0.00", CultureInfo.InvariantCulture)
                          + (choice.Level ? "" : " (best found, still steep)")
                        : " -> nothing better within " + Math.Round(SurfaceSiteSearch.MaxMoveM(reach)) + " m";
                }

                if (apply && standing)
                {
                    _surfaceSites.Remove(node.Id);
                    ForceRespawn(graph, node.OwnerFactionTag, node.Id);
                    result.Respawned++;
                    line += " -> respawned";
                }
                steep.Add(new KeyValuePair<double, string>(grade, line));
            }

            steep.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (int i = 0; i < steep.Count && i < MaxSiteDetails; i++) result.Details.Add(steep[i].Value);
            if (steep.Count > MaxSiteDetails) result.Details.Add("... " + (steep.Count - MaxSiteDetails) + " more");
            return result;
        }

        /// <summary>The station design's own local bounding box, or null when it cannot be measured.
        /// A capture is not an SE prefab definition, so SE cannot measure it; its box is solved from
        /// the design's own block coordinates instead -- same grid-local frame either way.</summary>
        private static BoundingBox? MeasurePrefabBox(MyObjectBuilder_CubeGrid[] customGrids, string prefabName)
        {
            if (customGrids != null) return CustomPrefabSpawner.LocalBoundingBox(customGrids);
            try
            {
                return MyPlanetSurfacePlacement.ComputePrefabBoundingBox(prefabName);
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "StationMaterializer: ComputePrefabBoundingBox threw for prefab=" + prefabName);
                return null;
            }
        }

        /// <summary>
        /// Where a player respawning at this node should be set down: the station's origin, its up
        /// axis, and the radius from that origin that clears the whole station. Read from the live
        /// grid when one stands, otherwise from the design the next materialisation will build, so
        /// an arrival placed outside the radius is never inside the hull that spawns around them --
        /// a character is not an obstruction to <see cref="StationFootprintObstructed"/>.
        /// </summary>
        internal bool TryGetArrivalFrame(SpatialNode node, out Vector3D stationPos, out Vector3D stationUp,
                                         out float clearanceM, out string error)
        {
            stationPos = Vector3D.Zero;
            stationUp = Vector3D.Up;
            clearanceM = UnmeasuredPrefabRadiusM;
            error = null;
            if (MySession.Static == null || node == null || node.StableStationId == 0)
            {
                error = "the node has no station";
                return false;
            }

            MyFaction faction = FindFaction(node.OwnerFactionTag);
            MyFactionStation concrete = faction != null
                ? faction.GetStationById(node.StableStationId) as MyFactionStation
                : null;
            if (concrete == null)
            {
                error = "station #" + node.StableStationId + " is not registered with " + node.OwnerFactionTag;
                return false;
            }

            MyCubeGrid live;
            if (concrete.StationEntityId != 0
                && MyEntities.TryGetEntityById(concrete.StationEntityId, out live, false)
                && !live.MarkedForClose)
            {
                stationPos = live.PositionComp.GetPosition();
                stationUp = live.WorldMatrix.Up;
                clearanceM = (float)FarthestCornerM(live.PositionComp.WorldAABB, stationPos);
                return true;
            }

            // Not standing: where BeginSpawn will put it. A drifted station is put back on its node.
            Vector3D recorded = concrete.Position;
            stationPos = StationPlacement.HasDriftedFromNode(node, recorded.X, recorded.Y, recorded.Z)
                ? StationTarget(node)
                : recorded;
            stationUp = concrete.Up;

            // A surface station on steep ground spawns where SurfaceSiteFor put it last time.
            SurfaceSite site;
            if (_surfaceSites.TryGetValue(node.Id, out site) && site.Choice.Moved
                && Vector3D.DistanceSquared(site.Recorded, stationPos) < 1.0)
            {
                stationPos = site.Position;
                stationUp = site.Up;
            }

            MyObjectBuilder_CubeGrid[] customGrids =
                CustomStationGrids != null && !string.IsNullOrEmpty(node.StationPrefabId)
                    ? CustomStationGrids(node.StationPrefabId)
                    : null;
            BoundingBox? box = MeasurePrefabBox(customGrids, concrete.PrefabName);
            if (box.HasValue)
            {
                Vector3 far = Vector3.Max(Vector3.Abs(box.Value.Min), Vector3.Abs(box.Value.Max));
                if (far.Length() > 1f) clearanceM = far.Length();
            }
            return true;
        }

        private static double FarthestCornerM(BoundingBoxD box, Vector3D from)
        {
            double best = 0;
            for (int i = 0; i < 8; i++)
            {
                Vector3D corner = new Vector3D((i & 1) == 0 ? box.Min.X : box.Max.X,
                                               (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                                               (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                double d = (corner - from).Length();
                if (d > best) best = d;
            }
            return best;
        }

        /// <summary>Radius of a sphere that encloses <paramref name="prefabBox"/> at ANY orientation:
        /// half its diagonal. Used as the FindFreePlaceCustom probe radius so a displacement is
        /// scaled to the station rather than to its safe zone, while still being conservative enough
        /// that a position found clear for the sphere is clear for the grid however it is turned.
        /// Falls back to <see cref="UnmeasuredPrefabRadiusM"/> for an unmeasurable prefab.</summary>
        private static float PrefabClearanceRadiusM(BoundingBox? prefabBox)
        {
            if (!prefabBox.HasValue) return UnmeasuredPrefabRadiusM;
            float half = prefabBox.Value.Size.Length() * 0.5f;
            return half > 1f ? half : UnmeasuredPrefabRadiusM;
        }

        /// <summary>
        /// True when a real physical object overlaps the volume the station prefab will occupy at
        /// <paramref name="spawnMatrix"/> -- a grid (anyone's ship or base) or an asteroid. Names the
        /// first one found in <paramref name="obstruction"/> so the log says WHAT moved the station.
        ///
        /// <para>This is the "actual collision" half of the clearance probe. The station's footprint
        /// is the prefab's own local bounding box carried onto the spawn transform, so the test is
        /// an oriented box against each candidate's own oriented box -- not a 150 m sphere, and not
        /// world AABBs, either of which would flag a ship merely parked NEXT to the station. A grid's
        /// world AABB is a loose fit for anything not axis-aligned, so each candidate is rebuilt from
        /// its LocalAABB and WorldMatrix instead.</para>
        ///
        /// <para>Safe zones are deliberately not obstructions: they have no physical presence and the
        /// station's own zone is one of them. So is the station's own rock (<see cref="StationVoxels"/>). Planets are excluded too -- a planet's bounds cover
        /// everything near it, and terrain is the surface branch's business, not this one's. An
        /// unmeasurable prefab (<paramref name="prefabBox"/> null) reports obstructed so the caller
        /// falls back to the old unconditional safe-zone-radius probe rather than skipping clearance
        /// altogether.</para>
        /// </summary>
        private static bool StationFootprintObstructed(BoundingBox? prefabBox, MatrixD spawnMatrix, long stationId,
                                                       out string obstruction)
        {
            obstruction = null;
            if (!prefabBox.HasValue)
            {
                obstruction = "an unmeasurable prefab's assumed footprint";
                return true;
            }

            BoundingBoxD localBox = new BoundingBoxD(prefabBox.Value.Min, prefabBox.Value.Max);
            MyOrientedBoundingBoxD footprint = new MyOrientedBoundingBoxD(localBox, spawnMatrix);

            // Broad phase on the footprint's world AABB, then the precise oriented test per hit.
            BoundingBoxD searchBox = footprint.GetAABB();
            List<MyEntity> hits = new List<MyEntity>();
            MyGamePruningStructure.GetTopMostEntitiesInBox(ref searchBox, hits);

            for (int i = 0; i < hits.Count; i++)
            {
                MyEntity e = hits[i];
                if (e == null || e.MarkedForClose) continue;

                MyCubeGrid grid = e as MyCubeGrid;
                MyVoxelBase voxel = grid == null ? e as MyVoxelBase : null;
                if (grid == null && (voxel == null || voxel is MyPlanet)) continue;
                // The station's own rock, left standing by a restart or a drill, is where it belongs.
                if (voxel != null && StationVoxels.IsStationVoxel(stationId, voxel.EntityId)) continue;

                MyOrientedBoundingBoxD candidate =
                    new MyOrientedBoundingBoxD((BoundingBoxD)e.PositionComp.LocalAABB, e.WorldMatrix);
                if (!footprint.Intersects(ref candidate)) continue;

                obstruction = grid != null
                    ? "grid '" + grid.DisplayName + "' (" + grid.EntityId + ")"
                    : "voxel '" + voxel.StorageName + "' (" + voxel.EntityId + ")";
                return true;
            }
            return false;
        }

        /// <summary>
        /// True if <paramref name="grid"/> sits under meaningful natural gravity but its Up axis
        /// is not (close to) pointing away from the planet center. Deep-space grids and grids
        /// already gravity-aligned return false — nothing to fix.
        /// </summary>
        private static bool NeedsGravityRealignment(MyCubeGrid grid)
        {
            Vector3D pos = grid.PositionComp.GetPosition();

            // Deliberately NOT gated on GetClosestPlanet any more. It was, and that made this check
            // dead for every ring grid: see TryGetNaturalGravity. Live 2026-08-29, zero
            // `reclaiming misoriented` lines across a whole day -- which was the gate, not evidence
            // that anything was correctly oriented.
            Vector3 pull;
            if (!TryGetNaturalGravity(pos, out pull)) return false;

            Vector3D gridUp = grid.WorldMatrix.Up;
            return StationPlacement.IsGravityMisalignedForPull(
                new Vec3(pull.X, pull.Y, pull.Z),
                new Vec3(gridUp.X, gridUp.Y, gridUp.Z));
        }

        /// <summary>
        /// Admin sweep for the drift <see cref="BeginSpawn"/> now heals on approach: walks every
        /// founded node, reports (and with <paramref name="applyFix"/>, corrects) any station whose
        /// stored position has fallen more than <see cref="StationPlacement.NodeDriftToleranceM"/>
        /// from its own node. Exists because <see cref="BeginSpawn"/>'s heal only fires when a player
        /// actually wanders near, so an untouched corner of the map would stay wrong indefinitely --
        /// the same "nothing is scheduled to revisit it" gap <see cref="ForceRespawn"/> was added for.
        ///
        /// <para>Unlike <see cref="ForceRespawn"/> this never calls <see cref="BeginSpawn"/>: it
        /// corrects the stored position and releases any live claim, then leaves the normal
        /// player-proximity path to respawn the grid at the corrected spot. Respawning every drifted
        /// station worldwide in one sweep would defeat the whole point of lazy materialization.</para>
        ///
        /// <para>Skips nodes with a spawn already in flight rather than mutating a station out from
        /// under an in-flight prefab callback, and skips any whose gap exceeds
        /// <paramref name="maxPlausibleDriftM"/>. Naming <paramref name="onlyNodeId"/> restricts the
        /// sweep to that one node AND overrides that fence, which exists to defer exactly this
        /// decision to an operator.</para>
        /// </summary>
        public StationDriftFixResult FixNodeStationDrift(SpatialGraph graph, bool applyFix,
            Func<SpatialNode, double> maxPlausibleDriftM, string onlyNodeId = null)
        {
            var result = new StationDriftFixResult { Details = new List<string>() };
            if (MySession.Static == null || graph == null) return result;

            foreach (KeyValuePair<string, SpatialNode> kv in graph.Nodes)
            {
                SpatialNode node = kv.Value;
                if (onlyNodeId != null && !string.Equals(node.Id, onlyNodeId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (node.StableStationId == 0) continue;
                if (string.IsNullOrEmpty(node.OwnerFactionTag)) continue;

                MaterializedStation inFlight;
                if (_materialized.TryGetValue(node.Id, out inFlight) && inFlight.SpawnPending) continue;

                MyFaction faction = FindFaction(node.OwnerFactionTag);
                if (faction == null) continue;

                MyFactionStation concrete = faction.GetStationById(node.StableStationId) as MyFactionStation;
                if (concrete == null) continue;

                result.Checked++;
                Vector3D stPos = concrete.Position;
                // Same predicate BeginSpawn's heal uses -- it screens out scan-backed nodes (where the
                // station is the real save asset and the node is the ingested copy), the only
                // screening the target does; otherwise it measures against the bare node.
                if (!StationPlacement.HasDriftedFromNode(node, stPos.X, stPos.Y, stPos.Z)) continue;
                double driftM = StationPlacement.NodeStationDriftM(node, stPos.X, stPos.Y, stPos.Z);

                result.Drifted++;
                if (driftM > result.WorstDriftM) result.WorstDriftM = driftM;
                // A gap wider than any ordinary reposition could have opened means the node is not
                // reliably the authoritative side, so re-anchoring onto it is not a safe automatic
                // repair. Report it and leave it for a deliberate per-node decision.
                double limitM = maxPlausibleDriftM != null ? maxPlausibleDriftM(node) : double.MaxValue;
                bool implausible = limitM > 0 && driftM > limitM;
                // Naming one node IS the deliberate per-node decision the fence defers to, so it
                // overrides the fence rather than being blocked by it.
                bool forced = implausible && onlyNodeId != null;
                if (implausible && !forced) result.Skipped++;

                if (result.Details.Count < MaxDriftDetails)
                    result.Details.Add(node.OwnerFactionTag + " " + node.Id + " #" + node.StableStationId
                        + " " + Math.Round(driftM) + "m"
                        + (forced ? " FORCED(>" + Math.Round(limitM) + "m)"
                                  : implausible ? " SKIPPED(>" + Math.Round(limitM) + "m)" : ""));

                if (!applyFix || (implausible && !forced)) continue;

                if (StationSetPosition != null)
                    StationSetPosition.Invoke(concrete,
                        new object[] { StationTarget(node), (Vector3)concrete.Up, (Vector3)concrete.Forward });

                // Release the claim so the corrected position is what actually gets built: a live grid
                // is still standing at the OLD spot, and leaving StationEntityId set would make both
                // BeginSpawn and vanilla's cell generator treat the station as already spawned.
                if (concrete.StationEntityId != 0)
                {
                    CloseStationGrid(node.Id, concrete.StationEntityId, concrete);
                    MySession.Static.GetComponent<MySessionComponentEconomy>()?.RemoveStationGrid(concrete.StationEntityId);
                    concrete.StationEntityId = 0L;
                }
                if (concrete.SafeZoneEntityId != 0)
                {
                    MySafeZone sz;
                    if (MyEntities.TryGetEntityById(concrete.SafeZoneEntityId, out sz, false)) sz.Close();
                    concrete.SafeZoneEntityId = 0L;
                }
                _materialized.Remove(node.Id);
                result.Fixed++;
            }

            return result;
        }

        /// <summary>
        /// Admin escape hatch: force-closes an already-spawned station's grid/safe zone and
        /// immediately re-triggers <see cref="BeginSpawn"/>, regardless of player proximity.
        /// <see cref="BeginSpawn"/>'s own stale/misoriented-claim healing (see
        /// <see cref="NeedsGravityRealignment"/>) normally handles this the next time a player
        /// wanders near — this exists for deterministic, on-demand correction (e.g. a station
        /// BeginSpawn has genuinely never touched before, so nothing is scheduled to revisit it).
        /// Keyed by <paramref name="nodeId"/> rather than the SE-side StableStationId — the node
        /// ID is what an admin can actually read off <c>factions node</c>/the dashboard.
        /// </summary>
        public string ForceRespawn(SpatialGraph graph, string factionTag, string nodeId)
        {
            if (MySession.Static == null || graph == null) return "reclaim: no session loaded.";

            SpatialNode node;
            if (!graph.Nodes.TryGetValue(nodeId, out node)) return "reclaim: no node '" + nodeId + "'.";

            long stableStationId = node.StableStationId;
            if (stableStationId == 0) return "reclaim: node '" + nodeId + "' has no station.";

            MyFaction faction = FindFaction(factionTag);
            if (faction == null) return "reclaim: faction '" + factionTag + "' not found.";

            MyFactionStation concrete = faction.GetStationById(stableStationId) as MyFactionStation;
            if (concrete == null) return "reclaim: station " + stableStationId + " (node '" + nodeId + "') not found on " + factionTag + ".";

            long oldGridId = concrete.StationEntityId;
            if (oldGridId != 0)
            {
                CloseStationGrid(nodeId, oldGridId, concrete);
                MySession.Static.GetComponent<MySessionComponentEconomy>()?.RemoveStationGrid(oldGridId);
                concrete.StationEntityId = 0L;
            }
            int crewRemoved = 0;
            if (concrete.SafeZoneEntityId != 0)
            {
                MySafeZone sz;
                if (MyEntities.TryGetEntityById(concrete.SafeZoneEntityId, out sz, false)) sz.Close();
                // Same reason as BeginSpawn's: this line is the last moment the bots' anchor id
                // still names this station. BeginSpawn cannot do it for us here -- by the time it
                // runs, the id it would match on is already zero.
                crewRemoved = DespawnOrphanedCrew(concrete.SafeZoneEntityId, factionTag, stableStationId, nodeId);
                concrete.SafeZoneEntityId = 0L;
            }
            _materialized.Remove(node.Id);
            _declineReason.Remove(node.Id);   // an operator asked; report the next decline afresh

            BeginSpawn(node);
            return "reclaim: closed gridId=" + oldGridId + " for " + factionTag + " node=" + nodeId
                + " (stationId=" + stableStationId + "); removed " + crewRemoved
                + " orphaned crew bot(s); respawn requested (check log for the new gridId).";
        }

        /// <summary>Permanently removes a founded station -- both the live SE-side record
        /// (<c>MyFaction.m_stations</c> entry) and any currently-materialized grid/safe-zone -- for
        /// the grace-period sweep (<c>FactionRuntime.PruneExpiredUnknTerritory</c>). Unlike
        /// <see cref="ForceRespawn"/> (close then immediately respawn), this never calls
        /// <see cref="BeginSpawn"/> again: the underlying Factions node is about to be deleted from
        /// the graph too (<c>WorldModel.RemoveNode</c>), so there is nothing left to respawn.
        /// Removing the <c>MyFactionStation</c> record itself (not just closing its grid, which the
        /// routine lazy-despawn <see cref="CloseEntry"/> already does on its own) is the step that
        /// matters here -- without it, the abstract station record and whatever internal station-slot
        /// machinery it occupies becomes exactly the same class of orphaned SE-side record
        /// docs/todo.md's pre-Aug-12 "Orphaned duplicate station grids" bug already diagnosed once.
        /// No-op (returns a diagnostic string, does not throw) if the station can't be found -- the
        /// caller still deletes the Factions-side node either way.
        ///
        /// <para>The record is removed from whichever faction holds it, not only from
        /// <paramref name="factionTag"/>'s: a node and its record can be filed under different
        /// factions (a node abandoned to UNKN whose record stayed with the previous owner), and
        /// removing from the node owner's list alone then removes nothing while the node goes —
        /// which strands the record with no node and no claim (FAC-119).</para></summary>
        public string PermanentlyRemove(string nodeId, long stableStationId, string factionTag)
        {
            if (MySession.Static == null) return "densify-prune: no session loaded.";
            if (stableStationId == 0) return "densify-prune: node '" + nodeId + "' has no founded station.";

            string heldBy;
            int removed = PurgeStationRecordAnywhere(stableStationId, factionTag, nodeId, out heldBy);

            string msg = removed > 0
                ? "Permanently removed " + heldBy + " station #" + stableStationId + " (node=" + nodeId + ")"
                  + (string.Equals(heldBy, factionTag, StringComparison.OrdinalIgnoreCase) ? "" : "; the node was " + factionTag + "'s")
                : "Station #" + stableStationId + " (node=" + nodeId + ") is on no faction's station list; nothing to remove";
            FactionsPlugin.Log.Info(msg);
            if (DebugEnabled) DebugBroadcast?.Invoke(msg);
            return msg;
        }

        /// <summary>
        /// <see cref="PurgeStationRecord"/> against every faction that lists <paramref name="stationId"/>,
        /// <paramref name="preferredTag"/>'s first. Returns how many records were removed;
        /// <paramref name="heldBy"/> names the factions they were under (comma-separated, empty when
        /// none). A record and the node that names it are not always filed under the same faction,
        /// so a removal keyed on the node's owner alone can miss.
        /// </summary>
        internal int PurgeStationRecordAnywhere(long stationId, string preferredTag, string nodeId, out string heldBy)
        {
            heldBy = "";
            if (MySession.Static == null || MySession.Static.Factions == null || stationId == 0) return 0;

            var holders = new List<MyFaction>();
            MyFaction preferred = FindFaction(preferredTag);
            if (preferred != null && preferred.GetStationById(stationId) != null) holders.Add(preferred);
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                MyFaction f = kv.Value;
                if (f == null || f == preferred) continue;
                if (f.GetStationById(stationId) != null) holders.Add(f);
            }

            int removed = 0;
            var tags = new List<string>(holders.Count);
            foreach (MyFaction f in holders)
            {
                bool gridClosed, zoneClosed;
                if (PurgeStationRecord(f, stationId, nodeId, out gridClosed, out zoneClosed))
                {
                    removed++;
                    tags.Add(f.Tag);
                }
            }
            heldBy = string.Join(",", tags.ToArray());
            return removed;
        }

        /// <summary>
        /// Tear down one station record: close its grid and safe zone if it ever materialized,
        /// deregister the grid from the economy component, and drop the
        /// <c>MyFaction.m_stations</c> entry. Keyed on the station rather than on a Factions node,
        /// so it also reaches a station no node has ever referenced -- which is every station on a
        /// world whose economy stations came from SE's own generator (<see cref="StationPurger"/>).
        ///
        /// <paramref name="nodeId"/> is optional and only clears this instance's materialization
        /// bookkeeping; the caller owns whatever the node itself should become. Returns true when
        /// the faction actually held the station.
        /// </summary>
        internal bool PurgeStationRecord(MyFaction faction, long stationId, string nodeId,
            out bool gridClosed, out bool zoneClosed)
        {
            gridClosed = false;
            zoneClosed = false;
            if (MySession.Static == null || faction == null || stationId == 0) return false;

            var concrete = faction.GetStationById(stationId) as MyFactionStation;
            if (concrete != null)
            {
                if (concrete.StationEntityId != 0)
                {
                    gridClosed = CloseStationGrid(nodeId, concrete.StationEntityId, concrete).Closed;
                    MySession.Static.GetComponent<MySessionComponentEconomy>()?.RemoveStationGrid(concrete.StationEntityId);
                }
                if (concrete.SafeZoneEntityId != 0)
                {
                    MySafeZone sz;
                    if (MyEntities.TryGetEntityById(concrete.SafeZoneEntityId, out sz, false))
                    {
                        sz.Close();
                        zoneClosed = true;
                    }
                }
            }
            // Rock outlives its grid in the save, so a station gone for good takes its rock with it
            // whether or not a grid was standing.
            StationVoxels.CloseAll(stationId, null);

            bool removed = false;
            if (StationFounder.StationsDictField != null)
            {
                var stations = StationFounder.StationsDictField.GetValue(faction) as Dictionary<long, IMyFactionStation>;
                if (stations != null) removed = stations.Remove(stationId);
            }

            _managedIds.Remove(stationId);
            if (nodeId != null) _materialized.Remove(nodeId);
            return removed;
        }

        /// <summary>
        /// Every station record whose <c>StationEntityId</c> names <paramref name="gridEntityId"/>,
        /// as <c>#id TAG prefab</c>. With <paramref name="apply"/> each is unlinked: its grid and
        /// safe-zone pointers are cleared and its safe zone closed, while the record itself stays
        /// on its faction's list. That is the state <see cref="CloseEntry"/> leaves a lazily
        /// despawned station in, so whichever materialiser owns the record spawns the grid afresh.
        /// Static because it keys on the SE record alone and must work on a process that runs no
        /// simulation.
        /// </summary>
        internal static List<string> UnlinkStationRecords(long gridEntityId, bool apply)
        {
            var found = new List<string>();
            if (MySession.Static == null || MySession.Static.Factions == null || gridEntityId == 0) return found;
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                MyFaction faction = kv.Value;
                if (faction == null) continue;
                foreach (var station in faction.Stations)   // var: MyFaction.Stations' element type
                {
                    var concrete = station as MyFactionStation;
                    if (concrete == null || concrete.StationEntityId != gridEntityId) continue;
                    found.Add("#" + concrete.Id + " " + faction.Tag + " " + concrete.PrefabName);
                    if (!apply) continue;
                    if (concrete.SafeZoneEntityId != 0)
                    {
                        MySafeZone sz;
                        if (MyEntities.TryGetEntityById(concrete.SafeZoneEntityId, out sz, false)) sz.Close();
                    }
                    concrete.StationEntityId  = 0L;
                    concrete.SafeZoneEntityId = 0L;
                }
            }
            return found;
        }

        private void CloseEntry(string nodeId, ref MaterializedStation ms)
        {
            // Clears the station's own grid/safe-zone pointers only. The _managedIds claim is not
            // released: the node still owns this station, so vanilla must stay out of it whether or
            // not a grid is standing.
            MyFaction faction = FindFaction(ms.FactionTag);
            MyFactionStation concrete = faction != null
                ? faction.GetStationById(ms.StableStationId) as MyFactionStation
                : null;
            if (concrete != null)
            {
                concrete.StationEntityId  = 0L;
                concrete.SafeZoneEntityId = 0L;
            }
            if (ms.GridEntityId != 0)
                MySession.Static?.GetComponent<MySessionComponentEconomy>()?.RemoveStationGrid(ms.GridEntityId);

            if (ms.SafeZoneEntityId != 0)
            {
                MySafeZone sz;
                if (MyEntities.TryGetEntityById(ms.SafeZoneEntityId, out sz, false))
                    sz.Close();
            }

            var close = new StationGridClose();
            if (ms.GridEntityId != 0)
                close = CloseStationGrid(nodeId, ms.GridEntityId, concrete, ms.StableStationId);

            string despawnMsg = "Despawned " + ms.FactionTag + " station #" + ms.StableStationId
                + " (gridId=" + ms.GridEntityId + ")"
                + (close.Ammo != null ? " " + close.Ammo : "")
                + (close.Loot != null ? " " + close.Loot : "")
                + (close.Rock != null ? " " + close.Rock : "");
            FactionsPlugin.Log.Info(despawnMsg);
            if (DebugEnabled) DebugBroadcast?.Invoke(despawnMsg);
        }

        private struct StationGridClose
        {
            public bool Closed;
            public string Ammo;
            public string Loot;
            public string Rock;
        }

        /// <summary>Closes a station's grid assembly, reading the magazines aboard first for
        /// <see cref="StationClosed"/> and the loot for <see cref="LootClosed"/>, closes its pristine
        /// rock, and drops the station's vanilla resource cache so its next spawn builds one from the
        /// new grid. A grid already gone reads as holding nothing. A close refused because a player is
        /// aboard changes nothing and reports nothing.</summary>
        private StationGridClose CloseStationGrid(string nodeId, long gridEntityId, MyFactionStation concrete,
                                                  long stationId = 0)
        {
            var result = new StationGridClose();
            if (stationId == 0 && concrete != null) stationId = concrete.Id;
            var grids = new List<MyCubeGrid>();
            CollectStation(gridEntityId, grids);
            bool stockFed = StockFedGrids.Contains(gridEntityId);
            Dictionary<string, long> observed = stockFed && StationClosed != null
                ? new Dictionary<string, long>(StringComparer.Ordinal) : null;
            if (observed != null) FleetMunitionLoader.Read(grids, observed);
            bool lootFed = LootFedGrids.Contains(gridEntityId);
            Dictionary<string, long> lootSeen = lootFed && LootClosed != null
                ? new Dictionary<string, long>(StringComparer.Ordinal) : null;
            if (lootSeen != null) StationLootLoader.Read(grids, lootSeen);

            var gridIds = new HashSet<long> { gridEntityId };
            for (int i = 0; i < grids.Count; i++) if (grids[i] != null) gridIds.Add(grids[i].EntityId);

            int closed = GridAssembly.CloseCollected(grids);
            if (grids.Count > 0 && closed == 0) return result;
            result.Closed = closed > 0;
            _assemblies.Remove(gridEntityId);

            foreach (long id in gridIds)
            {
                StockFedGrids.Remove(id);
                LootFedGrids.Remove(id);
            }
            try { result.Rock = StationVoxels.CloseAll(stationId, gridIds); }
            catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "StationMaterializer: closing station voxels threw node=" + nodeId); }
            if (concrete != null && !StationTurretRefillPatch.ClearResourceCache(concrete) && !_cacheClearMissLogged)
            {
                _cacheClearMissLogged = true;
                FactionsPlugin.Log.Error("StationMaterializer: MyFactionStation.ResourcesGenerator.ClearBlocksCache did "
                    + "not resolve; a station respawned in this process gets no vanilla power or ice refill.");
            }
            if (observed != null && nodeId != null)
            {
                try { result.Ammo = StationClosed(nodeId, observed); }
                catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "StationMaterializer: station munitions credit threw node=" + nodeId); }
            }
            if (lootSeen != null && nodeId != null)
            {
                try { result.Loot = LootClosed(nodeId, lootSeen); }
                catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "StationMaterializer: station loot credit threw node=" + nodeId); }
            }
            return result;
        }

        private static MyFaction FindFaction(string tag)
        {
            if (MySession.Static?.Factions == null) return null;
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
                if (string.Equals(kv.Value.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }
    }
}
