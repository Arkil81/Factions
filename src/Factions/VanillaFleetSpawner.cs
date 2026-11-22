using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;             // MyLargeTurretBaseDefinition
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;   // MyAiBlockComponent
using Sandbox.Game.GameSystems;        // MyDamageSystem
using Sandbox.Game.Entities.Cube;      // MySlimBlock
using Sandbox.Game.Entities.Character; // MyCharacter
using Sandbox.Game.Weapons;            // MyLargeTurretBase
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;           // FlightMode
using SpaceEngineers.Game.Entities.Blocks;       // MyBasicMissionBlock, MyFlightMovementBlock
using SpaceEngineers.Game.EntityComponents.Blocks; // MyBasicMissionAutopilot
using VRage.Game;
using VRage.Game.Entity;               // MyEntity
using VRage.Game.ModAPI;               // SpawningOptions
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Fleet spawner that uses <c>MyPrefabManager.SpawnPrefab</c> to materialise cargo-ship prefabs and
    /// wires a <c>MyRemoteControl</c> autopilot toward the target position.
    ///
    /// <para>Because <c>SpawnPrefab</c> is async (callback-based), <c>SpawnXxx</c> returns 0 on the first
    /// call and the entity ID on the following call once the callback fires. The caller (FactionRuntime)
    /// simply polls every Update tick — a 0 result leaves <c>MaterialisedEntityId</c> unchanged, so the
    /// next tick tries again. <paramref name="fleetKey"/> is a stable per-fleet-event string used to
    /// deduplicate re-entries while a spawn is in flight.</para>
    ///
    /// <para>Prefab selection is the fleet's own <c>ShipPrefabId</c> — the hull consumed from
    /// <c>ShipStock</c> at dispatch. Only a fleet that names no hull falls back to a role lookup:
    /// the prefab catalogue by <c>kind → faction type</c>, then <c>fleet_prefabs.json</c> (see
    /// <see cref="FleetPrefabConfig"/>), a two-level <c>kind → personality → prefab SubtypeId</c> map
    /// whose <c>"default"</c> entry catches an unlisted personality. If neither yields a name the
    /// spawn is skipped with a warning log line (no crash).</para>
    ///
    /// <para>SkipsTransit = false, so FactionRuntime uses distance-based transit time for fleet events.</para>
    /// </summary>
    public sealed class VanillaFleetSpawner : IFleetSpawner
    {
        public bool SkipsTransit { get { return false; } }

        // ---- Nexus: grids flown on an agent (FAC-506 step 3) ----------------------------------

        /// <summary>Authority on a Nexus cluster: the grids agents fly for it. Null elsewhere, and
        /// then every grid is local.</summary>
        internal NexusFleetRegistry Remote
        {
            get { return _remote; }
            set
            {
                _remote = value;
                // Whichever is set first, the registry answers with the controller's lookups.
                if (value != null)
                {
                    value.BandsOf = _flight.FleetBandsLookup;
                    value.OpponentsOf = _flight.OpponentsLookup;
                }
            }
        }
        private NexusFleetRegistry _remote;

        /// <summary>Authority: where a grid spawned at a position (km) is flown. 0 here, a server id
        /// for an agent that flies it, or -1 for a server that cannot (the spawn waits).</summary>
        internal Func<Vec3, int> RouteSpawn { get; set; }

        private bool IsRemote(long entityId) { return Remote != null && Remote.Owns(entityId); }

        /// <summary>Agent: starts or continues a spawn ordered by the authority, under the kind it
        /// resolved. Polled until it answers; 0 while it is on its way.</summary>
        internal long SpawnOrdered(string fleetKey, ActionKind kind, string factionTag, long factionId,
                                   Vec3 posKm, Vec3 targetKm, string prefabName,
                                   string munitionsId, IDictionary<string, long> munitions)
        {
            // A squadron's hulls share one pool under the fleet's key, as on the authority.
            if (munitionsId != null && munitions != null) StageMunitions(BaseSpawnKey(fleetKey), munitionsId, munitions);
            return DoSpawn(fleetKey, kind, factionTag, factionId, posKm, targetKm, prefabName);
        }

        /// <summary>Whether the spawn for <paramref name="fleetKey"/>, or one of its squadron hulls,
        /// is waiting on an agent: a wait like the local pacing queue, not a stalled spawn.</summary>
        internal bool AwaitsRemoteSpawn(string fleetKey)
        {
            return Remote != null && Remote.IsSpawningUnder(fleetKey);
        }

        /// <summary>Authority: drops a spawn its fleet no longer wants, whether an agent or the local
        /// pacer is holding it. False when it can no longer be stopped — the prefab is loading, or it
        /// has landed and waits to be taken — and the caller lets it land.</summary>
        internal bool WithdrawSpawn(string fleetKey)
        {
            if (fleetKey == null || _inFlight.Contains(fleetKey) || _completed.ContainsKey(fleetKey)) return false;
            if (Remote != null)
            {
                if (Remote.Withdraw(fleetKey, DateTime.UtcNow)) return true;
                long landed;
                if (Remote.TryPeekSpawn(fleetKey, out landed)) return false;
            }
            return SpawnPacing.Pacer.Withdraw(SpawnPacing.FleetKey(fleetKey));
        }

        /// <summary>Agent: takes a withdrawn spawn out of the local pacer's queue.</summary>
        internal void WithdrawQueued(string fleetKey)
        {
            if (fleetKey != null) SpawnPacing.Pacer.Withdraw(SpawnPacing.FleetKey(fleetKey));
        }

        /// <summary>Agent: whether the spawn for <paramref name="fleetKey"/> is waiting its turn in the
        /// local pacer's queue.</summary>
        internal bool SpawnQueued(string fleetKey)
        {
            return fleetKey != null && SpawnPacing.Pacer.IsWaiting(SpawnPacing.FleetKey(fleetKey));
        }

        /// <summary>Agent: whether a spawn under <paramref name="fleetKey"/> is still on its way.</summary>
        internal bool SpawnInFlight(string fleetKey) { return _inFlight.Contains(fleetKey); }

        /// <summary>Agent: the result of a spawn whose callback has run, taken once (0 = it failed).</summary>
        internal bool TryTakeSpawn(string fleetKey, out long eid)
        {
            if (!_completed.TryGetValue(fleetKey, out eid)) return false;
            _completed.Remove(fleetKey);
            return true;
        }

        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private readonly string _configPath;
        private readonly FactionStore _store;
        private FleetPrefabConfig _config;
        private IPrefabCatalogue _catalogue;          // preferred; set via SetCatalogue after session load
        private IFactionTypeCatalogue _factionTypes;  // companion for faction-hint lookup
        private CustomPrefabStore _customPrefabStore; // player-captured prefabs (`!factions ship capture`)

        // Async spawn dedup: in-flight keys are awaiting the prefab callback; completed keys have an
        // entity ID ready to be returned on the next SpawnXxx call for the same key.
        private readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _completed = new Dictionary<string, long>(StringComparer.Ordinal);

        // Prefab each live grid was spawned from, so a grid that leaves the world can still be named
        // when its hull is written off. Bounded exactly like _lastDamageUtc: one entry per grid this
        // spawner created, dropped on despawn.
        private readonly Dictionary<long, string> _prefabByEid = new Dictionary<long, string>();

        // Hull integrity watch: per live fleet grid, the block-integrity sum it had when the watch
        // began and the hull integrity it stood for then. A reading is that integrity scaled by how
        // much of the sum is left, so a destroyed block (gone from the grid) and a damaged one both
        // count, and whatever damage was applied at spawn is the baseline rather than a loss.
        // Dropped on despawn, and on a read that finds the grid gone.
        private readonly Dictionary<long, IntegrityWatch> _integrityWatch = new Dictionary<long, IntegrityWatch>();

        private struct IntegrityWatch
        {
            /// <summary>Negative while the damage that sets the baseline is still being written.</summary>
            public double BlockSum;
            public double Integrity;
        }

        // FAC-425: the damage a hull spawns with is written a slice per frame rather than in the frame
        // that spawned it. Live 2026-09-29 one raid wrote 9029 blocks inside the spawn window.
        private sealed class IntegrityJob
        {
            public long Eid;
            public List<MySlimBlock> Blocks;
            public List<float> Want;
            public int Next;
        }

        private const int IntegrityBlocksPerFrame = 400;
        private readonly List<IntegrityJob> _integrityJobs = new List<IntegrityJob>();

        // Fleet ammunition (FAC-386). A fleet armed from node stock stages its load under the spawn
        // key it will be materialised with; the spawn callback strips the prefab's own magazines and
        // loads from that pool; a despawn reads what the closing grids still hold into an
        // observation the runtime applies to the fleet. Keyed by the runtime's munitions id, which
        // survives a leg change where the spawn key does not.
        private readonly Dictionary<string, string> _munitionIdBySpawnKey = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, long>> _stagedMunitions =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        private readonly Dictionary<long, string> _munitionIdByEid = new Dictionary<long, string>();
        private readonly Dictionary<string, Dictionary<string, long>> _observedMunitions =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        // Magazines a spawn could find no room for aboard, per munitions id. Carried rather than
        // lost: a despawn read adds them back, so a fleet whose grids are small is not billed for
        // rounds it never had anywhere to put.
        private readonly Dictionary<string, Dictionary<string, long>> _unplacedMunitions =
            new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);

        // In-progress squadron spawns, keyed on the caller's fleet key. See SpawnSquadron.
        private readonly Dictionary<string, SquadronSpawnState> _squadrons =
            new Dictionary<string, SquadronSpawnState>(StringComparer.Ordinal);

        // Sub-keys of squadron hulls that were given up on while their prefab load was still in
        // flight, and the time from which a late result for them may be closed. See ReapSubKey.
        private readonly Dictionary<string, DateTime> _reapSubKeys = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>How long one squadron hull may take to produce a grid, per hull in the squadron:
        /// the prefab loads run concurrently and a five-hull squadron takes longer than one. Matches
        /// the runtime's own per-fleet spawn timeout, which scales by the same count, so a hull that
        /// never loads is given up on here — and the squadron reported short — before the runtime
        /// abandons the whole fleet.</summary>
        private static readonly TimeSpan HullSpawnTimeout = TimeSpan.FromSeconds(10);

        private static TimeSpan HullDeadlineFor(int hulls)
        {
            return hulls <= 1 ? HullSpawnTimeout : TimeSpan.FromTicks(HullSpawnTimeout.Ticks * hulls);
        }

        /// <summary>How long a squadron may sit unpolled before the grids it has collected are
        /// closed. The caller polls every frame while its fleet exists, so a gap this long means the
        /// fleet is gone and nothing else holds these entity ids.</summary>
        private static readonly TimeSpan SquadronAbandonTimeout = TimeSpan.FromSeconds(120);

        /// <summary>Per-fleet accumulator for <see cref="SpawnSquadron"/>: one slot per squadron
        /// member, holding the entity id that member's sub-key produced (0 when it never spawned)
        /// and whether it has finished reporting. <c>_completed</c> is popped destructively, so the
        /// ids have to be collected here until every member has landed.</summary>
        private sealed class SquadronSpawnState
        {
            public readonly long[] Eids;
            public readonly bool[] Done;
            public readonly DateTime StartedUtc;
            public DateTime LastPollUtc;

            public SquadronSpawnState(int count, DateTime now)
            {
                Eids = new long[count];
                Done = new bool[count];
                StartedUtc = now;
                LastPollUtc = now;
            }
        }

        // Last time each materialised fleet grid was seen taking damage. The observation loop reads
        // this to separate a grid shot apart in combat from one that merely vanished (admin delete,
        // engine cleanup, a reload that did not restore it) — only the first costs the faction a hull.
        // Bounded: an entry is added only for a grid this spawner created and dropped on despawn.
        private readonly Dictionary<long, DateTime> _lastDamageUtc = new Dictionary<long, DateTime>();
        private bool _damageHandlerRegistered;

        /// <summary>Subscribes once to SE's after-damage hook. Not at construction, because
        /// <c>MyDamageSystem.Static</c> only exists after the session loads. The runtime calls it when it
        /// wires the station sinks and again from the siege tick, because a breached station's damage
        /// arrives through this hook as well as a fleet's, and a station can be shot before any fleet
        /// has spawned. Idempotent.</summary>
        internal void EnsureDamageHandler()
        {
            if (_damageHandlerRegistered) return;
            MyDamageSystem ds = MyDamageSystem.Static;
            if (ds == null) return;
            ds.RegisterAfterDamageHandler(1000, OnAfterDamage);
            _damageHandlerRegistered = true;
            Log.Info("VanillaFleetSpawner: after-damage hook bound.");
        }

        /// <summary>Damage sink for a station whose safe zone is down, wired by the runtime. Null
        /// until a session loads. Arguments: grid entity id, attacker identity, damage.</summary>
        public Action<long, long, double> StationDamageSink;

        /// <summary>Whether a grid's hits are banked for the station call: a materialised station, or
        /// a station with a call standing. Wired by the runtime.</summary>
        public Func<long, bool> StationAttackWatched;

        /// <summary>A watched station grid was hit. Arguments: station grid entity id, the attacker's
        /// grid entity id (zero when the hit came from no grid).</summary>
        public Action<long, long> StationAttackerSink;

        private void OnAfterDamage(object target, MyDamageInformation info)
        {
            MySlimBlock block = target as MySlimBlock;
            if (block == null || block.CubeGrid == null) return;
            long eid = block.CubeGrid.EntityId;

            // A station whose zone a player has broken through takes real damage, and that damage is
            // what decides whether their raid succeeds. Routed before the fleet-grid filter below
            // because a station is never in that map -- it is not a grid this spawner materialised.
            if (StationDamageSink != null && info.Amount > 0)
            {
                try { StationDamageSink(eid, AttackerIdentityOf(info), info.Amount); }
                catch { /* a throwing sink must not break SE's damage pipeline */ }
            }

            // A station calling for protection is told who hit it. Asked first whether the grid is
            // watched, so a hit on anything else costs one set lookup and no entity walk.
            if (StationAttackerSink != null && info.Amount > 0 && StationAttackWatched != null)
            {
                try
                {
                    if (StationAttackWatched(eid)) StationAttackerSink(eid, WingWorld.AttackerGridOf(info.AttackerId));
                }
                catch { /* a throwing sink must not break SE's damage pipeline */ }
            }

            if (_lastDamageUtc.Count == 0) return;
            long mountedOn = 0;
            long attackerGrid = info.Amount > 0 ? WingWorld.AttackerGridOf(info.AttackerId, out mountedOn) : 0;
            if (info.Amount > 0) CountDealt(attackerGrid, info.Amount);
            if (DebugTraces.Tactics && mountedOn != 0 && mountedOn != attackerGrid) LogMount(mountedOn, attackerGrid);

            // Only grids this spawner materialised are tracked, so player structures taking damage
            // never grow this map. A hit on a rotor or hinge subgrid is a hit on the hull it is
            // mounted on.
            if (!_lastDamageUtc.ContainsKey(eid))
            {
                MyCubeGrid hull = WingWorld.HullOf(block.CubeGrid);
                if (hull == null || hull == block.CubeGrid || !_lastDamageUtc.ContainsKey(hull.EntityId)) return;
                eid = hull.EntityId;
            }
            // A zero-amount event is not a hit: stamping it would let an unscathed grid read as
            // destroyed when it next leaves the world.
            if (!GridLossRule.Stamps(info.Amount)) return;
            DateTime now = DateTime.UtcNow;
            _lastDamageUtc[eid] = now;
            double taken;
            _damageTaken.TryGetValue(eid, out taken);
            _damageTaken[eid] = taken + info.Amount;
            _incomingFire.Record(eid, attackerGrid, info.Amount, now);
        }

        // Weapon subgrids already reported by LogMount; debug only.
        private readonly HashSet<long> _mountLogged = new HashSet<long>();

        /// <summary>Under 'factions debug tactics', once per subgrid: a hit came from a weapon mounted on
        /// a rotor, hinge or piston subgrid, attributed to the hull it sits on.</summary>
        private void LogMount(long mountedOn, long hull)
        {
            if (_mountLogged.Count > 1024) _mountLogged.Clear();
            if (!_mountLogged.Add(mountedOn)) return;
            Log.Info("FIRE_MOUNT grid=" + mountedOn + " hull=" + hull);
        }

        /// <summary>Who shot each materialised hull last, for the combat read and anything else that
        /// answers fire. Game thread only. See <see cref="IncomingFire"/>.</summary>
        private readonly IncomingFire _incomingFire = new IncomingFire();

        /// <summary>See <see cref="IncomingFire"/>: the grid that last damaged each materialised hull.</summary>
        public IncomingFire IncomingFire { get { return _incomingFire; } }

        // Damage each materialised fleet grid dealt and took since it was last read, in block
        // integrity. One entry per tracked grid at most; dropped on despawn.
        private readonly Dictionary<long, double> _damageDealt = new Dictionary<long, double>();
        private readonly Dictionary<long, double> _damageTaken = new Dictionary<long, double>();

        /// <summary>Credits <paramref name="amount"/> to the fleet grid that fired it
        /// (<see cref="WingWorld.AttackerGridOf"/>); a hit from anything that is not one of our fleet grids is
        /// not counted.</summary>
        private void CountDealt(long grid, float amount)
        {
            if (grid == 0 || !_lastDamageUtc.ContainsKey(grid)) return;
            double dealt;
            _damageDealt.TryGetValue(grid, out dealt);
            _damageDealt[grid] = dealt + amount;
        }

        /// <summary>The damage grid <paramref name="entityId"/> dealt and took since the last call,
        /// and resets both.</summary>
        public void TakeFightDamage(long entityId, out double dealt, out double taken)
        {
            if (IsRemote(entityId)) { Remote.TakeFightDamage(entityId, out dealt, out taken); return; }
            if (_damageDealt.TryGetValue(entityId, out dealt)) _damageDealt.Remove(entityId);
            if (_damageTaken.TryGetValue(entityId, out taken)) _damageTaken.Remove(entityId);
        }

        /// <summary>Identity behind a damage event. <c>AttackerId</c> is the launching BLOCK's entity
        /// id rather than a player, so it has to be walked back to the grid that owns it and then to
        /// that grid's owner; a hand-held weapon reports the character instead. Returns 0 when the
        /// attacker cannot be resolved to a player, which is every NPC-on-NPC hit.</summary>
        internal static long AttackerIdentityOf(MyDamageInformation info)
        {
            if (info.AttackerId == 0) return 0;
            MyEntity attacker;
            if (!MyEntities.TryGetEntityById(info.AttackerId, out attacker) || attacker == null) return 0;

            MyCharacter character = attacker as MyCharacter;
            if (character != null) return character.GetPlayerIdentityId();

            MyCubeBlock weapon = attacker as MyCubeBlock;
            if (weapon != null) return weapon.OwnerId;

            MyCubeGrid grid = attacker as MyCubeGrid;
            if (grid != null && grid.BigOwners != null && grid.BigOwners.Count > 0) return grid.BigOwners[0];
            return 0;
        }

        // Grids Despawn closed, with when: the named grid, its mechanical group and its fleet-mates.
        // Read by the observation loop so a grid we closed is never booked as destroyed. Pruned by
        // age once it grows, since the subgrids and debris in it are never asked about.
        private readonly Dictionary<long, DateTime> _closedByUs = new Dictionary<long, DateTime>();
        private static readonly TimeSpan ClosedByUsMemory = TimeSpan.FromMinutes(10);
        private const int ClosedByUsPruneAt = 512;

        public bool ClosedByUs(long entityId) { return IsRemote(entityId) ? Remote.ClosedByUs(entityId) : _closedByUs.ContainsKey(entityId); }

        /// <summary>Records every grid of <paramref name="victims"/> (and <paramref name="entityId"/>)
        /// as closed by us and drops the damage evidence held for them: a fleet-mate closed here is
        /// not the grid Despawn was named for, so its stamp would otherwise outlive it.</summary>
        private void NoteClosedByUs(long entityId, List<MyCubeGrid> victims)
        {
            DateTime now = DateTime.UtcNow;
            if (_closedByUs.Count >= ClosedByUsPruneAt)
            {
                var stale = new List<long>();
                foreach (KeyValuePair<long, DateTime> kv in _closedByUs)
                    if (now - kv.Value > ClosedByUsMemory) stale.Add(kv.Key);
                for (int i = 0; i < stale.Count; i++) _closedByUs.Remove(stale[i]);
            }
            _closedByUs[entityId] = now;
            for (int i = 0; i < victims.Count; i++)
            {
                MyCubeGrid g = victims[i];
                if (g == null) continue;
                _closedByUs[g.EntityId] = now;
                _lastDamageUtc.Remove(g.EntityId);
                _incomingFire.Forget(g.EntityId);
                _damageTaken.Remove(g.EntityId);
            }
        }

        // Grids of disabled hulls waiting out their despawn delay: the hull and its mechanical group.
        // Bounded by the hulls disabled within one delay; each is dropped when its wreck is closed.
        private readonly HashSet<long> _withheld = new HashSet<long>();

        /// <summary>Takes a disabled hull out of every fleet mechanism while its grid stays in the
        /// world: flight control released, no thrust-loss or handback watch, and its assembly withheld
        /// from any other grid's despawn so only <see cref="Despawn"/> of this hull closes it. Returns
        /// how many grids the assembly holds.</summary>
        public int Withhold(long entityId)
        {
            if (IsRemote(entityId)) return Remote.Withhold(entityId, DateTime.UtcNow);
            _flight.Release(entityId);
            _escortsFlown.Remove(entityId);
            _thrustLost.Forget(entityId);
            _integrityWatch.Remove(entityId);
            CancelIntegrityJob(entityId);
            ClearHandbackWatch(entityId);
            var assembly = new List<MyCubeGrid>();
            GridAssembly.Collect(entityId, false, assembly);
            _withheld.Add(entityId);
            for (int i = 0; i < assembly.Count; i++) if (assembly[i] != null) _withheld.Add(assembly[i].EntityId);
            return assembly.Count;
        }

        /// <summary>Whether <paramref name="entityId"/> belongs to a disabled hull's withheld wreck.</summary>
        public bool IsWithheld(long entityId) { return _withheld.Contains(entityId); }

        /// <summary>Drops a wreck's bookkeeping without closing it (it left the world, or a player took it).</summary>
        public void ForgetWithheld(long entityId)
        {
            if (IsRemote(entityId)) { Remote.ForgetWithheld(entityId, DateTime.UtcNow); return; }
            var assembly = new List<MyCubeGrid>();
            GridAssembly.Collect(entityId, false, assembly);
            ReleaseWithheld(assembly);
            _withheld.Remove(entityId);
        }

        private void ReleaseWithheld(List<MyCubeGrid> grids)
        {
            for (int i = 0; i < grids.Count; i++) if (grids[i] != null) _withheld.Remove(grids[i].EntityId);
        }

        /// <summary>The most recent hit on hull <paramref name="entityId"/> from a grid not owned by
        /// <paramref name="ownTag"/>'s faction, and the grid that fired it; null when none is on record.
        /// A grid that has left the world since cannot be told apart and counts as hostile.</summary>
        /// <summary><see cref="LastHostileHit"/> for a hull hit inside
        /// <see cref="IncomingFire.RecentWindow"/> of <paramref name="nowUtc"/>; null without a
        /// recent hit, which is answered before any owner lookup.</summary>
        public DateTime? RecentHostileHit(long entityId, string ownTag, DateTime nowUtc, out long attackerGridId)
        {
            attackerGridId = 0;
            if (IsRemote(entityId)) return Remote.RecentHostileHit(entityId, nowUtc, IncomingFire.RecentWindow, out attackerGridId);
            if (!_incomingFire.HitRecently(entityId, nowUtc)) return null;
            DateTime? hit = LastHostileHit(entityId, ownTag, out attackerGridId);
            return hit.HasValue && nowUtc - hit.Value <= IncomingFire.RecentWindow ? hit : null;
        }

        public DateTime? LastHostileHit(long entityId, string ownTag, out long attackerGridId)
        {
            if (IsRemote(entityId)) return Remote.LastHostileHit(entityId, out attackerGridId);
            DateTime hitUtc;
            Func<long, bool> own = g => string.Equals(GetGridOwnerFactionTag(g), ownTag, StringComparison.OrdinalIgnoreCase);
            if (!_incomingFire.TryLastHit(entityId, own, out attackerGridId, out hitUtc)) return null;
            return hitUtc;
        }

        /// <summary>The identity that owns grid <paramref name="gridEid"/> (its first big owner), or 0.</summary>
        public long OwnerIdentityOf(long gridEid)
        {
            MyCubeGrid grid;
            if (gridEid == 0) return 0;
            if (!MyEntities.TryGetEntityById(gridEid, out grid, false) || grid == null)
                return Remote != null ? Remote.ContactOwner(gridEid) : 0;
            return grid.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;
        }

        public DateTime? LastDamageUtc(long entityId)
        {
            if (IsRemote(entityId)) return Remote.LastDamageUtc(entityId);
            DateTime t;
            if (!_lastDamageUtc.TryGetValue(entityId, out t)) return null;
            return t == DateTime.MinValue ? (DateTime?)null : t;
        }

        // Terminal-approach/manoeuvre half of the hybrid flight model; this class owns the cruise half.
        private readonly FleetFlightController _flight = new FleetFlightController();
        private FactionTuning _tuning;

        /// <summary>Whether this grid is under the terminal-approach autopilot. For the grid census,
        /// which separates our ships that are flying from our ships that are parked.</summary>
        public bool IsFlying(long entityId) { return IsRemote(entityId) ? Remote.IsControlled(entityId) : _flight.IsControlling(entityId); }

        // Optional debug broadcast (same delegate wired on StationMaterializer).
        public static bool DebugTrade;
        public static bool DebugRaid;
        public static bool DebugGuard;
        public static bool DebugPatrol;
        public static bool DebugFerry;
        public static Action<string> DebugBroadcast;

        // True when any per-kind flag is on — used by the GPS tick guard.
        public static bool AnyDebugEnabled { get { return DebugTrade || DebugRaid || DebugGuard || DebugPatrol || DebugFerry; } }

        public VanillaFleetSpawner(string configPath, FactionStore store)
        {
            _configPath = configPath;
            _store = store;
            _flight.Fire = _incomingFire;
            LoadConfig();
        }

        /// <summary>Wire the prefab catalogue after session load (called by FactionsPlugin).</summary>
        public void SetCatalogue(IPrefabCatalogue catalogue, IFactionTypeCatalogue factionTypes)
        {
            _catalogue    = catalogue;
            _factionTypes = factionTypes;
        }

        /// <summary>Wire the captured-prefab store (called by FactionsPlugin, same time as
        /// <see cref="SetCatalogue"/>) — <see cref="DoSpawn"/> consults it before falling back to
        /// <c>MyPrefabManager.SpawnPrefab</c>, so a player capture is spawnable through the exact
        /// same fleet-dispatch path as a vanilla prefab.</summary>
        public void SetCustomPrefabStore(CustomPrefabStore store)
        {
            _customPrefabStore = store;
        }

        // ---- IFleetSpawner -----------------------------------------------------------------

        public long SpawnRaid(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId)
        {
            return DoSpawn(fleetKey, ActionKind.Raid, factionTag, factionId, posKm, targetKm, prefabId);
        }

        public long SpawnTrade(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId)
        {
            return DoSpawn(fleetKey, ActionKind.Trade, factionTag, factionId, posKm, targetKm, prefabId);
        }

        public long SpawnPatrol(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 centerKm, string prefabId)
        {
            return DoSpawn(fleetKey, ActionKind.Patrol, factionTag, factionId, posKm, centerKm, prefabId);
        }

        public long SpawnHaul(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId)
        {
            return DoSpawn(fleetKey, ActionKind.Haul, factionTag, factionId, posKm, targetKm, prefabId);
        }

        public long SpawnMine(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId)
        {
            return DoSpawn(fleetKey, ActionKind.Mine, factionTag, factionId, posKm, targetKm, prefabId);
        }

        public long SpawnGift(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId)
        {
            return DoSpawn(fleetKey, ActionKind.Gift, factionTag, factionId, posKm, targetKm, prefabId);
        }

        /// <summary>Spawns one grid per squadron member, each under its own sub-key of
        /// <paramref name="fleetKey"/> so it gets its own async dedup slot and its own
        /// <see cref="PickSpawnPos"/> seed — hulls of the same squadron are placed apart rather than
        /// stacked on one point. Returns 0 until every member has either produced a grid or been
        /// given up on, then returns the first live member (the lead) and appends the rest to
        /// <paramref name="escortsOut"/> in squadron order. Every hull is wired for
        /// <paramref name="kind"/> exactly as the single-hull spawns are, turret arming included.</summary>
        private static readonly Dictionary<string, string[]> SubKeys = new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary><c>fleetKey + "#" + i</c>, built once per key: a squadron is polled every frame until every hull lands.</summary>
        private static string SubKey(string fleetKey, int i)
        {
            string[] keys;
            if (!SubKeys.TryGetValue(fleetKey, out keys) || keys.Length <= i)
            {
                if (SubKeys.Count > 4096) SubKeys.Clear();
                var grown = new string[Math.Max(i + 1, keys == null ? 4 : keys.Length * 2)];
                if (keys != null) Array.Copy(keys, grown, keys.Length);
                keys = grown;
                SubKeys[fleetKey] = keys;
            }
            return keys[i] ?? (keys[i] = fleetKey + "#" + i);
        }

        public long SpawnSquadron(string fleetKey, ActionKind kind, string factionTag, long factionId,
                                  Vec3 posKm, Vec3 targetKm, IReadOnlyList<string> prefabIds, List<long> escortsOut)
        {
            if (prefabIds == null || prefabIds.Count == 0) return 0;
            // A squadron is not flown on an agent yet: one there waits, as for a server that cannot fly.
            // An agent that flies no fleets: wait, as a single hull does. One that does gets each
            // hull as its own remote spawn, through DoSpawn.
            if (RouteSpawn != null && RouteSpawn(posKm) < 0) return 0;

            DateTime now = DateTime.UtcNow;
            SweepAbandonedSquadrons(now, fleetKey);

            SquadronSpawnState state;
            if (!_squadrons.TryGetValue(fleetKey, out state))
            {
                state = new SquadronSpawnState(prefabIds.Count, now);
                _squadrons[fleetKey] = state;
            }
            else if (state.Eids.Length != prefabIds.Count)
            {
                // The member list changed under a key that is supposed to be stable — start over
                // rather than pair ids with the wrong hulls.
                _squadrons.Remove(fleetKey);
                ReleaseSquadron(fleetKey, state, now);
                state = new SquadronSpawnState(prefabIds.Count, now);
                _squadrons[fleetKey] = state;
            }
            state.LastPollUtc = now;

            bool allDone = true;
            for (int i = 0; i < prefabIds.Count; i++)
            {
                if (state.Done[i]) continue;

                string subKey = SubKey(fleetKey, i);
                // This hull is wanted again, so a pending reap of an older attempt on the same
                // sub-key must not carry over and close the grid this one is about to produce.
                _reapSubKeys.Remove(subKey);
                long eid = DoSpawn(subKey, kind, factionTag, factionId, posKm, targetKm, prefabIds[i]);
                if (eid > 0)
                {
                    state.Eids[i] = eid;
                    state.Done[i] = true;
                    continue;
                }

                // A hull still waiting its turn in the spawn queue is not a hull that will not load.
                if ((now - state.StartedUtc) >= HullDeadlineFor(prefabIds.Count)
                    && !SpawnPacing.Pacer.IsQueued(SpawnPacing.FleetKey(subKey))
                    && !(Remote != null && Remote.IsSpawning(subKey)))
                {
                    // One hull that will not load must not hold the rest of the squadron on the
                    // ground. Its sub-key is reaped later in case the prefab callback still fires.
                    state.Done[i] = true;
                    _reapSubKeys[subKey] = now + HullSpawnTimeout;
                    Log.Warn("VanillaFleetSpawner: squadron hull did not spawn key=" + subKey
                             + " prefab=" + prefabIds[i] + " — squadron continues without it");
                    continue;
                }
                allDone = false;
            }
            if (!allDone) return 0;

            long lead = 0;
            for (int i = 0; i < state.Eids.Length; i++)
            {
                if (state.Eids[i] <= 0) continue;
                if (lead == 0) lead = state.Eids[i];
                else if (escortsOut != null) escortsOut.Add(state.Eids[i]);
            }
            _squadrons.Remove(fleetKey);
            return lead;
        }

        /// <summary>Closes any grid a late prefab callback produced for a hull the squadron already
        /// gave up on. Nothing references such a grid, so leaving it would orphan it.</summary>
        private void ReapSubKey(string subKey)
        {
            long lateEid;
            if (Remote != null && Remote.TryTakeSpawn(subKey, out lateEid))
            {
                if (lateEid > 0) Despawn(lateEid);
                return;
            }
            if (!_completed.TryGetValue(subKey, out lateEid)) return;
            _completed.Remove(subKey);
            if (lateEid > 0) Despawn(lateEid);
        }

        /// <summary>Retires squadrons the caller has stopped polling — their fleet is gone, so the
        /// grids they collected have no owner. <paramref name="activeKey"/> is the key being polled
        /// right now, which is never stale.</summary>
        private void SweepAbandonedSquadrons(DateTime now, string activeKey)
        {
            if (_squadrons.Count == 0) return;

            List<string> stale = null;
            foreach (KeyValuePair<string, SquadronSpawnState> kv in _squadrons)
            {
                if (kv.Key == activeKey) continue;
                if ((now - kv.Value.LastPollUtc) < SquadronAbandonTimeout) continue;
                (stale ?? (stale = new List<string>())).Add(kv.Key);
            }
            if (stale == null) return;

            for (int i = 0; i < stale.Count; i++)
            {
                SquadronSpawnState state = _squadrons[stale[i]];
                _squadrons.Remove(stale[i]);
                ReleaseSquadron(stale[i], state, now);
            }
        }

        /// <summary>Closes every grid a squadron collected and arranges for any hull still in flight
        /// to be reaped when its callback lands.</summary>
        private void ReleaseSquadron(string fleetKey, SquadronSpawnState state, DateTime now)
        {
            int closed = 0;
            for (int i = 0; i < state.Eids.Length; i++)
            {
                if (state.Eids[i] > 0) closed += Despawn(state.Eids[i]);
                string subKey = SubKey(fleetKey, i);
                ReapSubKey(subKey);
                if (!state.Done[i]) _reapSubKeys[subKey] = now + HullSpawnTimeout;
            }
            Log.Warn("VanillaFleetSpawner: squadron abandoned key=" + fleetKey
                     + " closed=" + closed + " — no fleet is polling it");
        }

        /// <summary>Pops results that arrived for hulls nobody is waiting for any more and closes
        /// the grids behind them. Runs off the flight tick so it fires even when no squadron is
        /// spawning.</summary>
        private void SweepLateSpawnResults(DateTime now)
        {
            if (_reapSubKeys.Count == 0) return;

            List<string> due = null;
            foreach (KeyValuePair<string, DateTime> kv in _reapSubKeys)
                if (now >= kv.Value) (due ?? (due = new List<string>())).Add(kv.Key);
            if (due == null) return;

            for (int i = 0; i < due.Count; i++)
            {
                // A hull whose prefab load is still running keeps its reap entry: dropping it now
                // would leave the grid its callback eventually produces with nobody to close it.
                if (_inFlight.Contains(due[i]) || (Remote != null && Remote.IsSpawning(due[i]))) continue;
                _reapSubKeys.Remove(due[i]);
                ReapSubKey(due[i]);
            }
        }

        public bool GridExists(long entityId)
        {
            if (IsRemote(entityId))
            {
                if (_handingOff.Count > 0) _handingOff.Remove(entityId);
                return Remote.Exists(entityId);
            }
            return MyEntities.GetEntityById(entityId) != null || HandingOff(entityId);
        }

        // ---- Nexus: a grid carried across a sector boundary ----------------------------------

        /// <summary>Authority: the fleet a grid flies for, as its kind and faction; false for a grid no
        /// fleet holds, which is then not handed off.</summary>
        internal delegate bool FleetOfGridLookup(long eid, out ActionKind kind, out string factionTag, out long factionId);
        internal FleetOfGridLookup FleetOfGrid { get; set; }

        private readonly Dictionary<long, DateTime> _handingOff = new Dictionary<long, DateTime>();

        /// <summary>Authority: a grid this server flew that Nexus carried into an agent's sector stands
        /// for <see cref="NexusFleetRegistry.TransitHold"/> while the agent reports it, and from then on
        /// is the agent's (<see cref="IsRemote"/>). One no agent reports is gone, as before.</summary>
        private bool HandingOff(long eid)
        {
            if (Remote == null) return false;
            DateTime now = DateTime.UtcNow;
            DateTime since;
            if (_handingOff.TryGetValue(eid, out since))
            {
                if (now - since <= NexusFleetRegistry.TransitHold) return true;
                _handingOff.Remove(eid);
                Log.Info("NEXUS_FLEET_TRANSIT_LAPSED eid=" + eid);
                return false;
            }
            byte to;
            string key;
            if (_closedByUs.ContainsKey(eid) || !SectorHandover.TryTakeLeft(eid, out to, out key) || !Remote.CanFly(to, now))
                return false;
            ActionKind kind;
            string tag;
            long factionId;
            if (FleetOfGrid == null || !FleetOfGrid(eid, out kind, out tag, out factionId)) return false;
            string munitionsId;
            _munitionIdByEid.TryGetValue(eid, out munitionsId);
            Remote.Expect(eid, new NexusFleetOrder
            {
                Key = key, Eid = eid, Kind = (int)kind, Tag = tag, FactionId = factionId,
                Prefab = GridPrefabId(eid), MunitionsId = munitionsId,
            }, LastDamageUtc(eid), now);
            _handingOff[eid] = now;
            _flight.Release(eid);
            Log.Info("NEXUS_FLEET_HANDED_OFF eid=" + eid + " key=" + key + " to=" + to);
            return true;
        }

        /// <summary>Takes over a fleet grid that arrived from another server's sector: tracks its damage,
        /// prefab and munitions as a grid spawned here, and marks its assembly under its fleet. The
        /// caller sends its leg next.</summary>
        internal bool AdoptArrival(long eid, string fleetKey, string factionTag, ActionKind kind, string prefab,
                                   string munitionsId, DateTime? damagedUtc)
        {
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(eid, out grid, false) || grid == null || grid.MarkedForClose) return false;
            EnsureDamageHandler();
            _handingOff.Remove(eid);
            _closedByUs.Remove(eid);
            _lastDamageUtc[eid] = damagedUtc ?? DateTime.MinValue;
            if (prefab != null) _prefabByEid[eid] = prefab;
            if (munitionsId != null) _munitionIdByEid[eid] = munitionsId;
            SimGridMarks.MarkAssembly(new List<MyCubeGrid> { grid }, fleetKey, factionTag, kind.ToString(), DateTime.UtcNow);
            return true;
        }

        /// <summary>Prefab this grid was spawned from, or null when it was not materialised by this
        /// spawner (a previous process spawned it) or has already been despawned.</summary>
        public string GridPrefabId(long entityId)
        {
            if (IsRemote(entityId)) return Remote.PrefabOf(entityId);
            string prefabId;
            return _prefabByEid.TryGetValue(entityId, out prefabId) ? prefabId : null;
        }

        /// <summary>Starts watching a fleet grid's integrity at <paramref name="integrity"/> (0..1),
        /// damaging its blocks first when that is below whole so the hull a player meets shows the
        /// damage it carries. Each block keeps <paramref name="integrity"/> of its maximum, but never
        /// less than just above the ratio at which it stops working: a hull applied below that would
        /// spawn with dead thrusters and stall. So a badly damaged hull looks less damaged than it
        /// is; its recorded integrity is carried by the watch, not re-derived from the blocks.
        /// Answers how many blocks it damages — written over the next frames, not in this one — or
        /// -1 when the grid is not in the world.</summary>
        public int WatchIntegrity(long entityId, double integrity)
        {
            if (IsRemote(entityId)) return Remote.WatchIntegrity(entityId, integrity, DateTime.UtcNow);
            var grid = MyEntities.GetEntityById(entityId) as MyCubeGrid;
            if (grid == null || grid.Closed) return -1;
            double target = integrity < 0 ? 0 : (integrity > 1 ? 1 : integrity);
            CancelIntegrityJob(entityId);
            IntegrityJob job = null;
            if (target < HullDamage.WholeAt)
            {
                // Snapshot first: SetIntegrity_Server never removes a block, but the set is the
                // grid's own and is not ours to enumerate while writing through it. The writes
                // themselves happen in TickIntegrityJobs, a slice per frame.
                foreach (MySlimBlock slim in grid.CubeBlocks)
                {
                    if (slim == null || slim.MaxIntegrity <= 0) continue;
                    double floorRatio = Math.Min(1.0, slim.BlockDefinition.CriticalIntegrityRatio + 0.05);
                    float want = (float)(slim.MaxIntegrity * Math.Max(target, floorRatio));
                    if (want >= slim.Integrity) continue;
                    if (job == null) job = new IntegrityJob { Eid = entityId, Blocks = new List<MySlimBlock>(), Want = new List<float>() };
                    job.Blocks.Add(slim);
                    job.Want.Add(want);
                }
            }
            if (job == null)
            {
                _integrityWatch[entityId] = new IntegrityWatch { BlockSum = BlockIntegritySum(grid), Integrity = target };
                return 0;
            }
            // The baseline is the sum after the damage lands; until then a reading is the target itself.
            _integrityWatch[entityId] = new IntegrityWatch { BlockSum = -1, Integrity = target };
            _integrityJobs.Add(job);
            return job.Blocks.Count;
        }

        private void CancelIntegrityJob(long entityId)
        {
            for (int i = _integrityJobs.Count - 1; i >= 0; i--)
                if (_integrityJobs[i].Eid == entityId) _integrityJobs.RemoveAt(i);
        }

        /// <summary>Writes up to <see cref="IntegrityBlocksPerFrame"/> queued block-damage writes, oldest
        /// grid first, and sets each grid's watch baseline once its damage is all in.</summary>
        private void TickIntegrityJobs()
        {
            int budget = IntegrityBlocksPerFrame;
            while (budget > 0 && _integrityJobs.Count > 0)
            {
                IntegrityJob job = _integrityJobs[0];
                var grid = MyEntities.GetEntityById(job.Eid) as MyCubeGrid;
                if (grid == null || grid.Closed || grid.MarkedForClose)
                {
                    _integrityJobs.RemoveAt(0);
                    continue;
                }
                while (budget > 0 && job.Next < job.Blocks.Count)
                {
                    MySlimBlock slim = job.Blocks[job.Next];
                    float want = job.Want[job.Next];
                    job.Next++;
                    budget--;
                    if (slim == null || slim.IsDestroyed || want >= slim.Integrity) continue;
                    slim.SetIntegrity_Server(slim.BuildIntegrity, want, MyIntegrityChangeEnum.Damage, 0L);
                }
                if (job.Next < job.Blocks.Count) break;
                _integrityJobs.RemoveAt(0);
                IntegrityWatch w;
                if (_integrityWatch.TryGetValue(job.Eid, out w) && w.BlockSum < 0)
                {
                    w.BlockSum = BlockIntegritySum(grid);
                    _integrityWatch[job.Eid] = w;
                }
            }
        }

        /// <summary>The watched grid's hull integrity now: the integrity its watch began at, scaled by
        /// the share of its block-integrity sum still standing. Null when the grid is not watched or
        /// no longer in the world. One pass over the grid's blocks -- called at despawn and loss, not
        /// per tick.</summary>
        public double? ReadIntegrity(long entityId)
        {
            if (IsRemote(entityId)) return Remote.ReadIntegrity(entityId);
            IntegrityWatch w;
            if (!_integrityWatch.TryGetValue(entityId, out w)) return null;
            var grid = MyEntities.GetEntityById(entityId) as MyCubeGrid;
            if (grid == null || grid.Closed)
            {
                _integrityWatch.Remove(entityId);
                return null;
            }
            if (w.BlockSum <= 0) return w.Integrity;   // no blocks, or its spawn damage still being written
            double share = BlockIntegritySum(grid) / w.BlockSum;
            if (share > 1) share = 1;
            return w.Integrity * share;
        }

        private static double BlockIntegritySum(MyCubeGrid grid)
        {
            double sum = 0;
            foreach (Sandbox.Game.Entities.Cube.MySlimBlock slim in grid.CubeBlocks)
                if (slim != null) sum += slim.Integrity;
            return sum;
        }

        /// <summary>Closes the whole assembly behind <paramref name="entityId"/> — the hull, the
        /// subgrids mechanically attached to it, and every grid marked under the same fleet key —
        /// and returns how many grids were closed. See <see cref="GridAssembly"/> for why one
        /// <c>Close()</c> leaves a turreted hull's turrets in the world.</summary>
        public int Despawn(long entityId)
        {
            if (IsRemote(entityId)) return Remote.Despawn(entityId, DateTime.UtcNow);
            // Release first so no despawn path can leave a controller entry pointing at a closed
            // grid — this is every despawn's single choke point.
            _flight.Release(entityId);
            _escortsFlown.Remove(entityId);
            _lastLaunchEnd.Remove(entityId);

            _lastDamageUtc.Remove(entityId);
            _incomingFire.Forget(entityId);
            _damageDealt.Remove(entityId);
            _damageTaken.Remove(entityId);
            _prefabByEid.Remove(entityId);
            _integrityWatch.Remove(entityId);
            CancelIntegrityJob(entityId);
            _thrustLost.Forget(entityId);
            ClearHandbackWatch(entityId);

            // A disabled hull's wreck is closed on its own, never as a fleet-mate of whatever else
            // its fleet still flies; and closing any other grid leaves the wreck where it lies.
            bool wreck = _withheld.Contains(entityId);
            var victims = new List<MyCubeGrid>();
            GridAssembly.Collect(entityId, !wreck, victims);
            if (wreck) ReleaseWithheld(victims);
            else if (_withheld.Count > 0) victims.RemoveAll(g => g != null && _withheld.Contains(g.EntityId));
            NoteClosedByUs(entityId, victims);

            // A written-off hull brings nothing back into the abstract, munitions included.
            if (wreck)
            {
                _munitionIdByEid.Remove(entityId);
                for (int i = 0; i < victims.Count; i++) if (victims[i] != null) _munitionIdByEid.Remove(victims[i].EntityId);
                return GridAssembly.CloseCollected(victims);
            }

            string munitionsId;
            if (!_munitionIdByEid.TryGetValue(entityId, out munitionsId))
                return GridAssembly.CloseCollected(victims);

            // An armed fleet's grids are read before they go: what they still hold is what the
            // fleet brings back into the abstract.
            Dictionary<string, long> observed;
            if (!_observedMunitions.TryGetValue(munitionsId, out observed))
            {
                observed = new Dictionary<string, long>(StringComparer.Ordinal);
                _observedMunitions[munitionsId] = observed;
            }
            long found = FleetMunitionLoader.Read(victims, observed);
            Dictionary<string, long> unplaced;
            if (_unplacedMunitions.TryGetValue(munitionsId, out unplaced))
            {
                foreach (KeyValuePair<string, long> kv in unplaced)
                {
                    if (kv.Value <= 0) continue;
                    long prior;
                    observed.TryGetValue(kv.Key, out prior);
                    observed[kv.Key] = prior + kv.Value;
                    found += kv.Value;
                }
                _unplacedMunitions.Remove(munitionsId);
            }
            _munitionIdByEid.Remove(entityId);
            for (int i = 0; i < victims.Count; i++) if (victims[i] != null) _munitionIdByEid.Remove(victims[i].EntityId);
            // The next spawn stages afresh from the fleet, which by then carries this reading.
            UnstageMunitions(munitionsId);
            if (DebugTraces.Munitions)
                Log.Info("MUNITIONS_READ id=" + munitionsId + " eid=" + entityId + " grids=" + victims.Count
                         + " aboard=" + found);
            return GridAssembly.CloseCollected(victims);
        }

        /// <summary>True when every live grid loaded under <paramref name="munitionsId"/> is out of
        /// ammunition — see <see cref="FleetMunitionLoader.IsOutOfAmmunition"/>. False when no grid
        /// was loaded under it, so a fleet whose grids this spawner never armed cannot read as
        /// empty.</summary>
        public bool IsOutOfMunitions(string munitionsId)
        {
            if (string.IsNullOrEmpty(munitionsId)) return false;
            if (Remote != null && Remote.IsOutOfMunitions(munitionsId)) return true;
            if (_munitionIdByEid.Count == 0) return false;
            var grids = new List<MyCubeGrid>();
            foreach (KeyValuePair<long, string> kv in _munitionIdByEid)
            {
                if (kv.Value != munitionsId) continue;
                MyCubeGrid g;
                if (MyEntities.TryGetEntityById(kv.Key, out g, false) && g != null && !g.MarkedForClose) grids.Add(g);
            }
            return FleetMunitionLoader.IsOutOfAmmunition(grids);
        }

        /// <summary>Stages an armed fleet's magazines for the grids <paramref name="spawnKey"/> is
        /// about to produce. A key already staged is left alone: the runtime polls a spawn every
        /// tick until it lands, and a squadron's hulls draw from one pool as they arrive.</summary>
        public void StageMunitions(string spawnKey, string munitionsId, IDictionary<string, long> load)
        {
            if (string.IsNullOrEmpty(spawnKey) || string.IsNullOrEmpty(munitionsId) || load == null) return;
            string staged;
            if (_munitionIdBySpawnKey.TryGetValue(spawnKey, out staged)
                && staged == munitionsId && _stagedMunitions.ContainsKey(munitionsId)) return;
            _munitionIdBySpawnKey[spawnKey] = munitionsId;
            _stagedMunitions[munitionsId] = new Dictionary<string, long>(load, StringComparer.Ordinal);
        }

        /// <summary>What an armed fleet's grids held when they were despawned, summed over every
        /// despawn since the last take. False when nothing was read.</summary>
        public bool TryTakeObservedMunitions(string munitionsId, out Dictionary<string, long> observed)
        {
            Dictionary<string, long> remote = null;
            bool fromAgent = Remote != null && Remote.TryTakeObservedMunitions(munitionsId, out remote);
            if (munitionsId != null && _observedMunitions.TryGetValue(munitionsId, out observed))
            {
                _observedMunitions.Remove(munitionsId);
                if (fromAgent)
                    foreach (KeyValuePair<string, long> kv in remote)
                    {
                        long prior;
                        observed.TryGetValue(kv.Key, out prior);
                        observed[kv.Key] = prior + kv.Value;
                    }
                return true;
            }
            observed = remote;
            return fromAgent;
        }

        /// <summary>Drops staging and readings for fleets that no longer exist.</summary>
        public void RetainMunitions(ICollection<string> liveIds)
        {
            if (Remote != null) Remote.RetainMunitions(liveIds);
            if (_stagedMunitions.Count == 0 && _observedMunitions.Count == 0 && _unplacedMunitions.Count == 0) return;
            List<string> gone = null;
            foreach (string id in _stagedMunitions.Keys) if (!liveIds.Contains(id)) (gone ?? (gone = new List<string>())).Add(id);
            foreach (string id in _observedMunitions.Keys) if (!liveIds.Contains(id)) (gone ?? (gone = new List<string>())).Add(id);
            foreach (string id in _unplacedMunitions.Keys) if (!liveIds.Contains(id)) (gone ?? (gone = new List<string>())).Add(id);
            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++)
            {
                UnstageMunitions(gone[i]);
                _observedMunitions.Remove(gone[i]);
                _unplacedMunitions.Remove(gone[i]);
            }
        }

        private void UnstageMunitions(string munitionsId)
        {
            _stagedMunitions.Remove(munitionsId);
            List<string> keys = null;
            foreach (KeyValuePair<string, string> kv in _munitionIdBySpawnKey)
                if (kv.Value == munitionsId) (keys ?? (keys = new List<string>())).Add(kv.Key);
            if (keys != null) for (int i = 0; i < keys.Count; i++) _munitionIdBySpawnKey.Remove(keys[i]);
        }

        /// <summary>Replaces the prefab's magazines on a freshly spawned assembly with the fleet's
        /// staged load. A fleet that staged nothing — unarmed, or dispatched with the model off —
        /// keeps what its prefab carries, as before.</summary>
        private void LoadStagedMunitions(string spawnKey, string fleetKey)
        {
            string munitionsId;
            if (!_munitionIdBySpawnKey.TryGetValue(spawnKey, out munitionsId)) return;
            Dictionary<string, long> pool;
            if (!_stagedMunitions.TryGetValue(munitionsId, out pool)) return;

            var eids = new List<long>();
            SimGridMarks.CopyEidsForFleet(fleetKey, eids);
            var grids = new List<MyCubeGrid>(eids.Count);
            for (int i = 0; i < eids.Count; i++)
            {
                MyCubeGrid g;
                if (!MyEntities.TryGetEntityById(eids[i], out g, false) || g == null) continue;
                grids.Add(g);
                _munitionIdByEid[eids[i]] = munitionsId;
            }

            long stripped, loaded;
            FleetMunitionLoader.StripAndLoad(grids, pool, out stripped, out loaded);

            // A squadron's hulls draw from one pool as they land, so what is left now may still fit
            // aboard a later hull. It is only unplaced once the pool is not staged any more, which a
            // despawn settles; until then the remainder is tallied against the id.
            long left = 0;
            Dictionary<string, long> unplaced;
            if (!_unplacedMunitions.TryGetValue(munitionsId, out unplaced))
            {
                unplaced = new Dictionary<string, long>(StringComparer.Ordinal);
                _unplacedMunitions[munitionsId] = unplaced;
            }
            unplaced.Clear();
            foreach (KeyValuePair<string, long> kv in pool)
                if (kv.Value > 0) { unplaced[kv.Key] = kv.Value; left += kv.Value; }
            if (DebugTraces.Munitions)
                Log.Info("MUNITIONS_LOADED id=" + munitionsId + " key=" + fleetKey + " grids=" + grids.Count
                         + " stripped=" + stripped + " loaded=" + loaded + " unplaced=" + left);
        }

        /// <summary>The fleet's spawn key for a squadron hull's sub-key (<c>key#i</c>), or the key
        /// itself.</summary>
        private static string BaseSpawnKey(string key)
        {
            int hash = key != null ? key.LastIndexOf('#') : -1;
            if (hash <= 0 || hash == key.Length - 1) return key;
            for (int i = hash + 1; i < key.Length; i++) if (!char.IsDigit(key[i])) return key;
            return key.Substring(0, hash);
        }

        public Vec3? GetGridPositionKm(long entityId)
        {
            if (IsRemote(entityId)) return Remote.PositionKm(entityId);
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false))
                return Remote != null ? Remote.ContactPositionKm(entityId) : null;
            Vector3D pos = grid.PositionComp.GetPosition();
            return new Vec3(pos.X * 0.001, pos.Y * 0.001, pos.Z * 0.001);
        }

        public double GetGridSpeedKmPerMin(long entityId)
        {
            if (IsRemote(entityId)) return Remote.SpeedKmPerMin(entityId);
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false)) return -1;
            if (grid.Physics == null) return 0;
            float speedMs = grid.Physics.LinearVelocity.Length();
            return speedMs * 0.06; // m/s → km/min
        }

        public string GetGridOwnerFactionTag(long entityId)
        {
            if (IsRemote(entityId)) return Remote.OwnerTag(entityId);
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false))
                return Remote != null ? Remote.ContactTag(entityId) : null;
            if (grid.BigOwners == null || grid.BigOwners.Count == 0) return null;
            if (MySession.Static?.Factions == null) return null;
            long ownerId = grid.BigOwners[0];
            VRage.Game.ModAPI.IMyFaction ownerFaction = MySession.Static.Factions.TryGetPlayerFaction(ownerId);
            return ownerFaction != null ? ownerFaction.Tag : null;
        }

        public bool Retarget(long entityId, Vec3 newTargetKm)
        {
            if (IsRemote(entityId)) return Remote.Retarget(entityId, newTargetKm, DateTime.UtcNow);
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false)) return false;

            // A retarget always starts a fresh cruise leg, so hand the grid back from the native
            // controller before re-wiring — otherwise both would command thrust on the same grid.
            _flight.Release(entityId);
            // A retarget is a new leg: the previous leg's verification state, retry count and
            // avoidance suppression all belong to it and must not carry over.
            ClearHandbackWatch(entityId);
            bool wired = WireAutopilot(grid, KmToWorld(newTargetKm));
            LogHandback(grid, wired);
            return wired;
        }

        /// <summary>One-line dump, behind <c>factions debug flight</c>, of a grid's cruise-flight wiring
        /// right after the native controller hands it back. Distinguishes "the autopilot never came back on" from "it is on but the
        /// grid is being held by something else" when a retargeted grid fails to depart.</summary>
        private static void LogHandback(MyCubeGrid grid, bool wired)
        {
            if (!FleetFlightController.DebugFlight) return;
            bool haveRc = false, apEnabled = false, canEnable = false;
            foreach (MyRemoteControl rc in grid.GetFatBlocks<MyRemoteControl>())
            {
                haveRc = true;
                MyAutopilotComponent apc;
                if (rc.GetComponent(out apc) && apc != null)
                {
                    apEnabled = (bool)apc.AutoPilotEnabled;
                    canEnable = apc.CanEnableAutoPilot();
                }
                break;
            }

            int aiActive = 0, aiTotal = 0;
            if (grid.GridSystems != null && grid.GridSystems.AiBlockSystem != null)
            {
                foreach (MyCubeBlock block in grid.GridSystems.AiBlockSystem.AiBlocks)
                {
                    MyAiBlockComponent comp;
                    if (block.Components.TryGet<MyAiBlockComponent>(out comp) && comp != null)
                    {
                        aiTotal++;
                        if (comp.IsActivated) aiActive++;
                    }
                }
            }

            Log.Info("FLIGHT_HANDBACK eid=" + grid.EntityId + " wired=" + wired
                     + " rc=" + haveRc + " canEnable=" + canEnable + " apEnabled=" + apEnabled
                     + " aiBlocks=" + aiActive + "/" + aiTotal
                     + " gravity=" + (grid.Physics != null ? grid.Physics.Gravity.Length() : 0f).ToString("F2"));
        }

        // ---- native flight control (see FleetFlightController) ------------------------------

        public void TickFlight()
        {
            TickIntegrityJobs();
            TickHullProbes();
            _flight.Tick();
            PruneHandbackWatches();
            SimGridMarks.Tick(DateTime.UtcNow);

            // Squadron housekeeping rides the frame tick so grids left behind by a fleet that
            // stopped polling are closed even when nothing else is spawning.
            if (_squadrons.Count == 0 && _reapSubKeys.Count == 0) return;
            DateTime now = DateTime.UtcNow;
            SweepAbandonedSquadrons(now, null);
            SweepLateSpawnResults(now);
        }

        public bool BeginManoeuvre(long entityId, Vec3 targetKm)
        {
            if (IsRemote(entityId)) return Remote.AskApproach(entityId, targetKm, DateTime.UtcNow);
            return _flight.Begin(entityId, KmToWorld(targetKm), ManoeuvreKind.ApproachAndStop);
        }

        /// <summary>Departure bookend of <see cref="BeginManoeuvre"/>: owns a grid that has not got
        /// moving yet and returns whether it is under control. Once the launch finishes — up to
        /// handoff speed, or timed out trying — it releases the grid and re-wires the cruise
        /// autopilot in the same call, exactly as <see cref="Retarget"/> does, and reports false so
        /// the caller stops treating it as manoeuvring.</summary>
        public bool BeginLaunch(long entityId, Vec3 targetKm, bool flyThrough)
        {
            if (_tuning != null && !_tuning.LaunchManoeuvreEnabled) return false;
            if (IsRemote(entityId)) return Remote.AskLaunch(entityId, targetKm, flyThrough, DateTime.UtcNow);

            string reason;
            if (_flight.IsLaunchFinished(entityId, out reason))
            {
                _flight.Release(entityId);
                MyCubeGrid grid;
                if (MyEntities.TryGetEntityById(entityId, out grid, false))
                {
                    bool avoidance = !_noAvoidance.Contains(entityId);
                    bool wired = WireAutopilot(grid, KmToWorld(targetKm), avoidance);
                    // cap is the per-grid cruise speed WireAutopilot just handed the autopilot --
                    // the only place the clamp's actual output is observable. speed is the handoff
                    // speed this launch reached, which is a different number and always far lower.
                    if (FleetFlightController.DebugFlight)
                        Log.Info("FLIGHT_LAUNCH_DONE eid=" + entityId + " reason=" + reason
                                 + " speed=" + (grid.Physics != null ? grid.Physics.LinearVelocity.Length() : 0f).ToString("F1")
                                 + " cap=" + SpeedLimitFor(grid, KmToWorld(targetKm)).ToString("F0")
                                 + " rewired=" + wired
                                 + " avoidance=" + avoidance);

                    // A handback is a claim that the cruise autopilot will now fly the grid. Nothing
                    // used to check it, and the only recovery from a handback that did not take was
                    // the launch cooldown firing again 45 s later, into the identical condition.
                    // A launch that ended early is checked the same way, so it ends in a flying grid
                    // or a give-up and never with nothing flying it. Only a timeout goes to the stall
                    // detector: 45 s without getting anywhere is how immobile debris looks.
                    _lastLaunchEnd[entityId] = new KeyValuePair<string, DateTime>(
                        wired ? reason : reason + "/unwired", DateTime.UtcNow);
                    if (wired && reason != "timeout") ArmHandbackWatch(entityId);
                    else ClearHandbackWatch(entityId);
                }
                return false;
            }

            // Runs here because this is called every tick for every materialised fleet, and because
            // a verification that fails wants to re-launch on this same tick.
            bool retried = CheckHandback(entityId);

            bool began = _flight.Begin(entityId, KmToWorld(targetKm), ManoeuvreKind.Launch, !flyThrough);

            // A retry the launch gate refuses -- the grid is already creeping, or has no braking
            // estimate to engage on -- would leave the ladder disarmed with nothing checking the
            // grid again. Re-arming counts it at the next window, so the ladder still ends in a
            // grid under way or a give-up.
            if (retried && !began) ArmHandbackWatch(entityId);

            return began;
        }

        /// <summary>Whether the launch ladder has given up on flying <paramref name="entityId"/>
        /// physically. Consumed by the observation loop, which despawns the grid and lets the leg
        /// resolve abstractly — the same outcome as a player leaving range, which loses nothing.
        /// Reading it clears it: the runtime acts once.</summary>
        public bool ConsumeLaunchGiveUp(long entityId)
        {
            if (IsRemote(entityId)) return Remote.ConsumeLaunchGiveUp(entityId);
            return _launchGaveUp.Remove(entityId);
        }

        // A grid must stay thrustless this long before it is retired, so a component that is absent
        // for a moment (mid-paste, mid-rebuild) never costs a hull its materialisation.
        private static readonly TimeSpan ThrustLossGrace = TimeSpan.FromSeconds(5);
        private readonly SustainedCondition<long> _thrustLost = new SustainedCondition<long>(ThrustLossGrace);

        /// <summary>Whether grid <paramref name="entityId"/> is out of action right now — no thrust
        /// component, or no owner — without <see cref="ConsumeThrustLost"/>'s grace. For a hull a fight
        /// site is already taking off its field, where the grace would outlast the decision.</summary>
        public bool IsOutOfAction(long entityId)
        {
            if (IsRemote(entityId)) return Remote.IsOutOfAction(entityId);
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false) || grid.Closed) return false;
            return grid.Components.Get<MyEntityThrustComponent>() == null
                || grid.BigOwners == null || grid.BigOwners.Count == 0;
        }

        /// <summary>What is holding grid <paramref name="entityId"/> to something else right now:
        /// <c>gearsLocked=</c> landing gear and magnetic plates locked, <c>connected=</c> connectors
        /// connected. Read once, by a verdict that a grid never got under way, so the line names
        /// whether the hull was anchored. Empty when the grid is gone.</summary>
        public string AnchorTally(long entityId)
        {
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false) || grid.Closed) return "";
            int gears = 0, connected = 0;
            foreach (MyCubeBlock b in grid.GetFatBlocks())
            {
                var gear = b as SpaceEngineers.Game.ModAPI.IMyLandingGear;
                if (gear != null)
                {
                    if (gear.LockMode == SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.Locked) gears++;
                    continue;
                }
                var connector = b as Sandbox.ModAPI.IMyShipConnector;
                if (connector != null && connector.Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connected)
                    connected++;
            }
            return "gearsLocked=" + gears + " connected=" + connected;
        }

        /// <summary>Whether <paramref name="entityId"/> has been unable to fly for
        /// <see cref="ThrustLossGrace"/>: it has no thrust component, or it has no owner. SE removes
        /// the component with a grid's last thruster, so a hull that has shed or lost every one can
        /// neither cruise nor brake. A grid whose last owned block is gone belongs to nobody -- SE draws
        /// it neutral -- and nothing of its faction can command it either. No other outcome retires
        /// either while it is still drifting. Consumed by the observation loop, which writes the hull
        /// off when hostile fire did it and otherwise despawns the grid and lets the leg resolve
        /// abstractly. Returns true once per unbroken run; call every tick for a materialised
        /// hull.</summary>
        public bool ConsumeThrustLost(long entityId)
        {
            if (IsRemote(entityId)) return Remote.ConsumeThrustLost(entityId);
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false) || grid.Closed)
            {
                _thrustLost.Forget(entityId);
                return false;
            }
            bool thrustless = grid.Components.Get<MyEntityThrustComponent>() == null;
            bool ownerless = grid.BigOwners == null || grid.BigOwners.Count == 0;
            return _thrustLost.Observe(entityId, thrustless || ownerless, DateTime.UtcNow);
        }

        private sealed class HandbackWatch
        {
            public DateTime DueUtc;
            public bool Armed;
            public int Retries;
        }

        // eid -> handback verification state. Bounded by the materialised-fleet count; entries are
        // dropped on retarget, despawn and give-up.
        private readonly Dictionary<long, HandbackWatch> _handback = new Dictionary<long, HandbackWatch>();

        // Grids to re-wire with SE collision avoidance OFF -- the last retry of the ladder, and the
        // experiment that names the cause if it is the one that departs.
        private readonly HashSet<long> _noAvoidance = new HashSet<long>();

        private readonly HashSet<long> _launchGaveUp = new HashSet<long>();

        // eid -> how its last launch ended ("up_to_speed", "timeout", an early end), with
        // "/unwired" when the cruise re-wire after it failed. Read by a stall verdict; dropped on
        // despawn.
        private readonly Dictionary<long, KeyValuePair<string, DateTime>> _lastLaunchEnd
            = new Dictionary<long, KeyValuePair<string, DateTime>>();

        /// <summary>The flight state a stall verdict reports: the manoeuvre holding the grid, how its
        /// last launch ended, the last launch refusal, whether a launch has ever held it, and the
        /// handback check. Names which path left a grid with nothing flying it.</summary>
        public string FlightTally(long entityId)
        {
            KeyValuePair<string, DateTime> end;
            HandbackWatch w;
            string ended = _lastLaunchEnd.TryGetValue(entityId, out end)
                ? end.Key + "@-" + (DateTime.UtcNow - end.Value).TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "s"
                : "none";
            string refusal = _flight.LastLaunchRefusal(entityId);
            bool watched = _handback.TryGetValue(entityId, out w);
            return "ctl=" + (_flight.ControlKind(entityId) ?? "none")
                   + " launchEnd=" + ended
                   + " refusal=" + (refusal ?? "none")
                   + " launchedBefore=" + _flight.LaunchedBefore(entityId)
                   + " handback=" + (watched ? (w.Armed ? "armed" : "idle") + "/" + w.Retries : "none");
        }

        private void ArmHandbackWatch(long entityId)
        {
            double sec = _tuning != null ? _tuning.HandbackVerifySeconds : 6.0;
            HandbackWatch w;
            if (!_handback.TryGetValue(entityId, out w)) { w = new HandbackWatch(); _handback[entityId] = w; }
            w.DueUtc = DateTime.UtcNow.AddSeconds(sec);
            w.Armed  = true;
        }

        private void ClearHandbackWatch(long entityId)
        {
            _handback.Remove(entityId);
            _noAvoidance.Remove(entityId);
        }

        /// <summary>Drops watches whose grid has gone without passing through <see cref="Despawn"/>
        /// — destroyed in combat, or a fleet that stopped polling. Mirrors the launch cooldown's
        /// own sweep: only past a threshold, so the ordinary case of a handful of live grids costs
        /// nothing.</summary>
        private void PruneHandbackWatches()
        {
            if (_handback.Count < 64) return;
            _deadHandbacks.Clear();
            foreach (KeyValuePair<long, HandbackWatch> kv in _handback)
            {
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById(kv.Key, out grid, false) || grid == null || grid.Closed)
                    _deadHandbacks.Add(kv.Key);
            }
            for (int i = 0; i < _deadHandbacks.Count; i++) ClearHandbackWatch(_deadHandbacks[i]);
            _deadHandbacks.Clear();
        }

        private readonly List<long> _deadHandbacks = new List<long>();   // reused; avoids a per-sweep allocation

        /// <summary>Reads back whether the cruise autopilot actually took a grid the launch handed
        /// it, and escalates when it did not.
        ///
        /// <para>The discriminator is wide, not delicate: a grid the autopilot is flying is
        /// accelerating toward cruise by the time the window closes, and one it declined to thrust
        /// has been dampened to a dead stop, because SE reads a zero autopilot thrust command as
        /// full-authority braking rather than as coasting.</para>
        ///
        /// <para>The ladder: re-launch, holding the grid on the native controller each time so it is
        /// never left to the stall detector while this plays out; drop collision avoidance on the
        /// last retry; then give up and let the leg resolve abstractly.</para></summary>
        /// <returns>True when the check failed and a re-launch is due on this tick.</returns>
        private bool CheckHandback(long entityId)
        {
            HandbackWatch w;
            if (!_handback.TryGetValue(entityId, out w) || !w.Armed) return false;
            if (DateTime.UtcNow < w.DueUtc) return false;

            // The approach controller may have taken the grid inside the window on a leg only a
            // little longer than its horizon. A grid it is braking is supposed to be slow, so the
            // handback question is moot and answering it here would read a healthy arrival as a
            // failure.
            if (_flight.IsControlling(entityId)) { ClearHandbackWatch(entityId); return false; }

            w.Armed = false;

            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(entityId, out grid, false) || grid.Physics == null)
            {
                ClearHandbackWatch(entityId);
                return false;
            }

            double handoff = _tuning != null ? _tuning.LaunchHandoffSpeedMs : 15.0;
            double frac    = _tuning != null ? _tuning.HandbackMinSpeedFrac :  0.5;
            int maxRetries = _tuning != null ? _tuning.HandbackMaxRetries   :  2;

            double speed = grid.Physics.LinearVelocity.Length();
            if (speed >= handoff * frac)
            {
                // Under way. Any avoidance suppression from an earlier retry ends with the leg that
                // needed it, so the next one starts from the normal configuration.
                ClearHandbackWatch(entityId);
                return false;
            }

            w.Retries++;
            if (w.Retries > maxRetries)
            {
                _launchGaveUp.Add(entityId);
                ClearHandbackWatch(entityId);
                // The grid is despawned right after this, and what tells no gas, no power and stale
                // thrust groups apart is only readable while it is still in the world.
                Log.Warn("FLIGHT_HANDBACK_GIVEUP eid=" + entityId
                         + " speed=" + speed.ToString("F1")
                         + " retries=" + maxRetries
                         + " " + HullThrustProbe.Describe(grid)
                         + " — the cruise autopilot will not fly this grid; resolving the leg abstractly.");
                return false;
            }

            // Clear the cooldown so the launch re-engages on this tick rather than on the 45 s
            // metronome, which is the loop itself.
            _flight.ClearLaunchCooldown(entityId);

            bool dropAvoidance = w.Retries == maxRetries;
            if (dropAvoidance) _noAvoidance.Add(entityId);

            Log.Info("FLIGHT_HANDBACK_FAILED eid=" + entityId
                     + " speed=" + speed.ToString("F1")
                     + " retry=" + w.Retries + "/" + maxRetries
                     + " nextAvoidance=" + (!dropAvoidance));
            return true;
        }

        /// <summary>Agent: whether <paramref name="entityId"/>'s assembly is written to the world save.
        /// An agent's fleet hulls are not: the authority owns their fleets and re-spawns what it still
        /// wants, and nothing can find a saved one after a restart, because Nexus's sector load
        /// (<c>EntityIDSync.LoadSector_Postfix</c>) runs SE's <c>Remap</c> on every grid, which resets
        /// the entity name <see cref="SimGridMarks"/> writes. A hull a player owns any part of is saved:
        /// it is theirs. Unsaving takes the fleet's debris and ordnance with it and passes over any
        /// grid a player owns; saving takes only the mechanical group.</summary>
        internal void SetSaved(long entityId, bool saved)
        {
            _saveScratch.Clear();
            GridAssembly.Collect(entityId, !saved, _saveScratch);
            Sandbox.Game.Multiplayer.MyPlayerCollection players = MySession.Static != null ? MySession.Static.Players : null;
            for (int i = 0; i < _saveScratch.Count; i++)
            {
                MyCubeGrid g = _saveScratch[i];
                if (g == null || g.MarkedForClose || g.Save == saved) continue;
                if (!saved && GridReaper.IsPlayerOwned(players, g)) continue;
                g.Save = saved;
            }
            _saveScratch.Clear();
        }

        private readonly List<MyCubeGrid> _saveScratch = new List<MyCubeGrid>();

        public void ReleaseControl(long entityId)
        {
            if (IsRemote(entityId)) { Remote.Release(entityId, DateTime.UtcNow); return; }
            _flight.Release(entityId);
        }
        public void ReleaseAllControl() { _flight.ReleaseAll(); }

        /// <summary>Asks the native controller to fight with a lead holding station at
        /// <paramref name="anchorKm"/>; see <see cref="FleetFlightController.Fight"/>. Call every
        /// frame the fleet holds and fights; <see cref="StopFighting"/> otherwise.</summary>
        public bool FightAtHold(long entityId, Vec3 anchorKm, IReadOnlyList<WeaponBand> ownBands, string fleetLabel,
                                bool aimedFire, TargetScope scope, TacticOrder? tacticOverride, TacticOrder? learned = null,
                                long pursueTargetId = 0)
        {
            if (IsRemote(entityId))
                return Remote.AskFight(entityId, anchorKm, ownBands, fleetLabel, aimedFire, scope, tacticOverride, learned,
                                       pursueTargetId, DateTime.UtcNow);
            return _flight.Fight(entityId, KmToWorld(anchorKm), ownBands, fleetLabel, aimedFire, scope, tacticOverride, learned,
                                 pursueTargetId);
        }

        public void StopFighting(long entityId)
        {
            if (IsRemote(entityId)) { Remote.StopFighting(entityId); return; }
            _flight.StopFighting(entityId);
        }

        /// <summary>Asks the native controller to shadow <paramref name="targetId"/> with a lead holding
        /// station at <paramref name="anchorKm"/>; see <see cref="FleetFlightController.Shadow"/>. Call
        /// every frame it shadows.</summary>
        public bool ShadowAtHold(long entityId, Vec3 anchorKm, long targetId, double standoffM, double leashM,
                                 string fleetLabel)
        {
            if (IsRemote(entityId))
                return Remote.AskShadow(entityId, anchorKm, targetId, standoffM, leashM, fleetLabel, DateTime.UtcNow);
            return _flight.Shadow(entityId, KmToWorld(anchorKm), targetId, standoffM, leashM, fleetLabel);
        }

        // Grids whose turrets HoldTurrets switched off, so the per-frame call touches blocks only on
        // a change. A despawned grid's id is dropped the next time it is asked about.
        private readonly HashSet<long> _turretsHeld = new HashSet<long>();

        /// <summary>Switches the turrets of a lead and its escorts off while <paramref name="held"/>,
        /// and back on (as <see cref="ArmTurretsForCombat"/> left them) when it clears. Vanilla turret AI
        /// fires at any Enemies grid in range on its own, so this is the only way a hull holds fire.
        /// Call every frame the fleet holds station; a no-op unless the state changes.</summary>
        public void HoldTurrets(long leadId, IList<long> escortIds, bool held)
        {
            HoldGridTurrets(leadId, held);
            if (escortIds == null) return;
            for (int i = 0; i < escortIds.Count; i++) HoldGridTurrets(escortIds[i], held);
        }

        private void HoldGridTurrets(long gridId, bool held)
        {
            if (IsRemote(gridId)) { Remote.AskHold(gridId, held, DateTime.UtcNow); return; }
            if (gridId <= 0 || held == _turretsHeld.Contains(gridId)) return;
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById(gridId, out grid, false) || grid == null || grid.MarkedForClose)
            {
                _turretsHeld.Remove(gridId);
                return;
            }
            foreach (MyLargeTurretBase turret in grid.GetFatBlocks<MyLargeTurretBase>())
                turret.Enabled = !held;
            if (held) _turretsHeld.Add(gridId);
            else _turretsHeld.Remove(gridId);
        }

        /// <summary>Sends a grid under the native controller breaking away from its fight until no
        /// hostile grid is within <paramref name="clearM"/>, away from <paramref name="fromKm"/> while
        /// none is near; see <see cref="FleetFlightController.Leave"/>. False when the controller does
        /// not hold it.</summary>
        public bool LeaveFrom(long entityId, Vec3 fromKm, double clearM, string reason = "repelled")
        {
            if (IsRemote(entityId)) return Remote.Leave(entityId, fromKm, clearM, reason, DateTime.UtcNow);
            return _flight.Leave(entityId, KmToWorld(fromKm), clearM, reason);
        }

        /// <summary>See <see cref="FleetFlightController.IsLeaveDone"/>.</summary>
        public bool IsLeaveDone(long entityId) { return IsRemote(entityId) ? Remote.IsLeaveDone(entityId) : _flight.IsLeaveDone(entityId); }

        /// <summary>See <see cref="FleetFlightController.CombatManoeuvre"/>.</summary>
        public ManoeuvreKind? CombatManoeuvre(long entityId) { return IsRemote(entityId) ? Remote.CombatManoeuvre(entityId) : _flight.CombatManoeuvre(entityId); }

        // Escorts the native controller has held, so one it no longer holds is handed back to a
        // cruise autopilot exactly once. Entries go with the grid's despawn.
        private readonly HashSet<long> _escortsFlown = new HashSet<long>();

        /// <summary>Flies an escort in formation on <paramref name="anchorId"/>; see
        /// <see cref="FleetFlightController.Escort"/>. Call every frame for every escort while escort
        /// control is on. An escort the controller has stopped holding — refused by the bubble cap,
        /// or dropped for lost braking — is re-wired onto <paramref name="fleetTargetKm"/> on the
        /// cruise autopilot, the waypoint escorts fly without the controller.</summary>
        public bool FlyEscort(long entityId, long anchorId, int slotIndex, Vec3 fleetTargetKm)
        {
            if (IsRemote(entityId)) return Remote.AskEscort(entityId, anchorId, slotIndex, fleetTargetKm, DateTime.UtcNow);
            if (_flight.Escort(entityId, anchorId, slotIndex))
            {
                _escortsFlown.Add(entityId);
                return true;
            }
            if (_escortsFlown.Remove(entityId)) Retarget(entityId, fleetTargetKm);
            return false;
        }

        /// <summary>Hands an escort the controller holds back to the cruise autopilot on
        /// <paramref name="fleetTargetKm"/>; a no-op for one it does not. For escort control being
        /// switched off, and for an escort left with no anchor.</summary>
        public void StopEscorting(long entityId, Vec3 fleetTargetKm)
        {
            if (IsRemote(entityId)) { Remote.StopEscort(entityId); return; }
            if (_escortsFlown.Remove(entityId)) Retarget(entityId, fleetTargetKm);
        }

        /// <summary>Puts a hull under a wing commander's order; see
        /// <see cref="FleetFlightController.CommandWing"/>. Asked once a second while the hull obeys.</summary>
        internal bool CommandWing(long entityId, WingDirective directive)
        {
            if (IsRemote(entityId))
                return Remote.AskWing(entityId, directive.AnchorId, directive.TargetId, directive.Holding, directive.BreakingOff,
                                      directive.Order, directive.Slot, directive.Label, directive.FleetBands, DateTime.UtcNow);
            return _flight.CommandWing(entityId, directive);
        }

        /// <summary>Ends a commander's order on a hull that obeyed it and hands the hull back to the
        /// cruise autopilot on <paramref name="fleetTargetKm"/>, the waypoint its fleet flies; the
        /// fleet's own flight re-takes it from there — an escort's formation, a lead's approach and
        /// fight. Re-wired even when the order had already lost control of the grid, since nothing
        /// of the fleet's flew it meanwhile.</summary>
        public void EndWing(long entityId, Vec3 fleetTargetKm)
        {
            if (IsRemote(entityId)) { Remote.EndWing(entityId, fleetTargetKm, DateTime.UtcNow); return; }
            _flight.EndWing(entityId);
            _escortsFlown.Remove(entityId);
            Retarget(entityId, fleetTargetKm);
        }

        /// <summary>See <see cref="FleetFlightController.TakeTickCounts"/>.</summary>
        public void TakeFlightTickCounts(out long gridTicks, out long escortTicks, out int maxControlled, out int capRefusals)
        {
            _flight.TakeTickCounts(out gridTicks, out escortTicks, out maxControlled, out capRefusals);
        }

        /// <summary>Resolves a grid belonging to a fleet to that fleet's weapon bands, so a target
        /// that is a fleet grid is read from its fleet rather than estimated from its blocks.</summary>
        public void SetFleetBandsLookup(Func<long, IReadOnlyList<WeaponBand>> lookup)
        {
            _flight.FleetBandsLookup = lookup;
            if (Remote != null) Remote.BandsOf = lookup;
        }

        /// <summary>See <see cref="FleetFlightController.OpponentsLookup"/>.</summary>
        public void SetOpponentsLookup(Func<string, ICollection<long>, string> lookup)
        {
            _flight.OpponentsLookup = lookup;
            if (Remote != null) Remote.OpponentsOf = lookup;
        }

        /// <summary>See <see cref="FleetFlightController.ZonePacing"/>.</summary>
        public void SetZonePacing(Func<long, double, double, ZoneFireCall> pacing) { _flight.ZonePacing = pacing; }

        /// <summary>Under native control, or handed back with the handback check still pending. The
        /// check is bounded by the retry ladder, which ends in a grid under way or a give-up, so a
        /// grid it is watching is not left to the stall detector meanwhile.</summary>
        public bool IsManoeuvring(long entityId)
        {
            if (IsRemote(entityId)) return Remote.IsManoeuvring(entityId);
            if (_flight.IsControlling(entityId)) return true;
            HandbackWatch w;
            return _handback.TryGetValue(entityId, out w) && w.Armed;
        }

        public bool IsManoeuvreComplete(long entityId)
        {
            return IsRemote(entityId) ? Remote.IsManoeuvreComplete(entityId) : _flight.IsManoeuvreComplete(entityId);
        }

        public void SetTuning(FactionTuning tuning)
        {
            _tuning = tuning;
            _flight.SetTuning(tuning);
        }

        /// <summary>Hands the native controller the gravity it needs to model a manoeuvre's burn
        /// for its FUEL_OBSERVED line. Separate from <see cref="SetTuning"/> because the planet
        /// catalogue is built after this spawner is.</summary>
        public void SetPlanets(Factions.Core.IPlanetCatalogue planets) { _flight.SetPlanets(planets); }

        // ---- internals ---------------------------------------------------------------------

        private long DoSpawn(string fleetKey, ActionKind kind, string factionTag, long factionId,
                             Vec3 posKm, Vec3 targetKm, string explicitPrefabName)
        {
            // Already completed — pop and return the result.
            long completedEid;
            if (_completed.TryGetValue(fleetKey, out completedEid))
            {
                _completed.Remove(fleetKey);
                return completedEid; // may be 0 if the spawn failed; caller treats 0 as "not spawned"
            }

            // Spawn already in flight — don't double-spawn.
            if (_inFlight.Contains(fleetKey)) return 0;
            if (Remote != null)
            {
                long remoteEid;
                if (Remote.TryTakeSpawn(fleetKey, out remoteEid))
                {
                    // The agent loaded the magazines; a later spawn stages afresh from the fleet.
                    string landedId;
                    if (_munitionIdBySpawnKey.TryGetValue(BaseSpawnKey(fleetKey), out landedId)) UnstageMunitions(landedId);
                    return remoteEid;
                }
                if (Remote.IsSpawning(fleetKey))
                {
                    // Still waiting on the agent: it spawns where the fleet is, not where it was.
                    Remote.Respot(fleetKey, posKm, targetKm, DateTime.UtcNow);
                    return 0;
                }
            }

            // Resolve prefab name: the dispatch-time ShipPrefabId wins when supplied, so the spawned
            // grid matches what was economically consumed instead of an independent re-roll. The
            // catalogue (faction-type aware) / legacy config role lookup is the fallback for a
            // free-built ship that never named a hull.
            string prefabName = !string.IsNullOrEmpty(explicitPrefabName)
                ? explicitPrefabName
                : PickPrefabName(kind.ToString(), factionTag);
            if (string.IsNullOrEmpty(prefabName))
            {
                Log.Warn("VanillaFleetSpawner: no prefab for kind="
                         + kind + " faction=" + factionTag);
                return 0;
            }

            // Look up the SE faction for FounderId / ownership.
            MyFaction faction = FindFaction(factionId, factionTag);
            if (faction == null)
            {
                Log.Warn("VanillaFleetSpawner: faction not found for tag=" + factionTag + " id=" + factionId);
                return 0;
            }

            int route = RouteSpawn == null ? 0 : RouteSpawn(posKm);
            if (route < 0) return 0;
            if (route > 0)
            {
                string stagedId;
                Dictionary<string, long> staged = null;
                if (_munitionIdBySpawnKey.TryGetValue(BaseSpawnKey(fleetKey), out stagedId)
                    && _stagedMunitions.TryGetValue(stagedId, out staged))
                    staged = new Dictionary<string, long>(staged, StringComparer.Ordinal);
                else stagedId = null;
                Remote.Spawn((byte)route, new NexusFleetOrder
                {
                    Key = fleetKey, Kind = (int)kind, Tag = factionTag, FactionId = factionId, Prefab = prefabName,
                    X = posKm.X, Y = posKm.Y, Z = posKm.Z, Tx = targetKm.X, Ty = targetKm.Y, Tz = targetKm.Z,
                    MunitionsId = stagedId, Munitions = staged,
                }, DateTime.UtcNow);
                return 0;
            }

            Vector3D center      = KmToWorld(posKm);
            Vector3D targetWorld = KmToWorld(targetKm);

            // One Factions spawn at a time, a spacing apart (SpawnPacing). Refused: this key waits its
            // turn and the caller's next poll asks again.
            double pacedWait;
            if (!SpawnPacing.Pacer.TryBegin(SpawnPacing.FleetKey(fleetKey), DateTime.UtcNow, out pacedWait)) return 0;

            // With rock shells on, rock away from every body is not spawned; the free-space probe must see it.
            // After the pacing slot, so a fleet waiting its turn does not spawn rock on every poll.
            FeatureRockMaterializer rock = FeatureRockMaterializer.Active;
            if (rock != null) rock.EnsureAround(posKm, _tuning);
            Vector3D spawnPos    = PickSpawnPos(center, targetWorld, fleetKey);

            EnsureDamageHandler();
            _inFlight.Add(fleetKey);

            var resultList = new List<MyCubeGrid>();
            var callbacks  = new Stack<Action>();

            // Capture locals for the closure.
            string capturedKey       = fleetKey;
            ActionKind capturedKind  = kind;
            Vector3D capturedTarget  = targetWorld;
            string capturedTag       = factionTag;

            callbacks.Push(() =>
            {
                _inFlight.Remove(capturedKey);
                SpawnPacing.Pacer.Landed(SpawnPacing.FleetKey(capturedKey), DateTime.UtcNow);

                if (resultList == null || resultList.Count == 0)
                {
                    Log.Warn("VanillaFleetSpawner: spawn callback got no grids for key=" + capturedKey
                             + " prefab=" + prefabName);
                    _completed[capturedKey] = 0;
                    return;
                }

                MyCubeGrid grid = resultList[0];
                bool costTrace = DebugTraces.SpawnCost;
                long tStep = costTrace ? Stopwatch.GetTimestamp() : 0;
                var steps = costTrace ? new StringBuilder() : null;

                // Before anything else touches the hull: the projector's first update would build
                // the prefab's loaded blueprint (see NpcProjections).
                int projectionsCleared = NpcProjections.ClearSaved(resultList);
                if (costTrace) StepMs(steps, "proj", ref tStep);

                // Wire autopilot.
                WireAutopilot(grid, capturedTarget);
                if (costTrace) StepMs(steps, "ap", ref tStep);

                // Combat fleets get their turrets armed for a real fight when observed — magic ammo
                // for now (see docs/todo.md "Wire ammo loading to fleet dispatch").
                // A convoy too: held in an interception window (FAC-307) it must be able to shoot
                // back at the raider parked beside it. Enabled and ranged only -- no target flags.
                // A reinforce holds an Engage dwell at the node it is reinforcing and fights
                // whatever attacks it there, so it needs live turrets like any other combat fleet.
                if (capturedKind == ActionKind.Raid || capturedKind == ActionKind.Retaliate
                    || capturedKind == ActionKind.Conquer || capturedKind == ActionKind.Trade
                    || capturedKind == ActionKind.Gift
                    || capturedKind == ActionKind.Guard || capturedKind == ActionKind.Patrol
                    || capturedKind == ActionKind.Reinforce)
                    ArmTurretsForCombat(grid);
                if (costTrace) StepMs(steps, "arm", ref tStep);

                long eid = grid.EntityId;
                _completed[capturedKey] = eid;
                // MinValue = tracked, no damage yet. Presence in the map is what gates OnAfterDamage.
                _lastDamageUtc[eid] = DateTime.MinValue;
                _prefabByEid[eid] = prefabName;

                // Every grid the prefab produced and everything hanging off them, not just the lead:
                // a rotor-mounted turret is its own grid, and an unmarked one outlives its hull.
                int marked = SimGridMarks.MarkAssembly(resultList, capturedKey, capturedTag,
                                                       capturedKind.ToString(), DateTime.UtcNow);
                if (costTrace) StepMs(steps, "mark", ref tStep);
                // Named for its fleet, so a ship in view -- its grid, and what its antennas and beacons
                // broadcast -- can be matched to the log and the radio.
                string fleetId = NpcGridNaming.FleetIdOfKey(capturedKey);
                int broadcasts = SimGridMarks.LabelAssembly(resultList, grid,
                    NpcGridNaming.FleetDisplayName(capturedTag, capturedKind.ToString(), fleetId),
                    NpcGridNaming.FleetBroadcastText(capturedKind.ToString(), fleetId));
                if (costTrace) StepMs(steps, "label", ref tStep);
                LoadStagedMunitions(BaseSpawnKey(capturedKey), capturedKey);
                if (costTrace)
                {
                    StepMs(steps, "ammo", ref tStep);
                    int blocks = 0;
                    foreach (MyCubeGrid g in resultList) blocks += g.BlocksCount;
                    SpawnPacing.NoteLanding(SpawnPacing.FleetKey(capturedKey), " kind=" + capturedKind + " prefab=" + prefabName
                                            + " grids=" + resultList.Count + " blocks=" + blocks + " callback:" + steps);
                }
                else SpawnPacing.NoteLanding(SpawnPacing.FleetKey(capturedKey), null);

                string msg = "Spawned " + capturedKind + " fleet for [" + capturedTag + "]"
                             + " prefab=" + prefabName + " eid=" + eid + " grids=" + marked
                             + " broadcasts=" + broadcasts
                             + (projectionsCleared > 0 ? " projectionsCleared=" + projectionsCleared : "")
                             + (pacedWait >= 0.05 ? " waited=" + pacedWait.ToString("F1") + "s" : "");
                Log.Info(msg);
                bool kindDebug = (capturedKind == ActionKind.Trade   && DebugTrade)
                              || (capturedKind == ActionKind.Raid    && DebugRaid)
                              || (capturedKind == ActionKind.Guard   && DebugGuard)
                              || (capturedKind == ActionKind.Patrol  && DebugPatrol);
                if (kindDebug) DebugBroadcast?.Invoke(msg);
            });

            // The spawn as it always was: a captured design from its stored grids synchronously, SE's own
            // prefab spawn otherwise. The fallback for assembly, at the start or after it fails.
            void SpawnTheOldWay()
            {
                // Player-captured prefabs (`!factions ship capture`) never register a real SE
                // MyPrefabDefinition (see CustomPrefabSpawner's class doc) — spawn straight from the
                // stored grids instead of MyPrefabManager.SpawnPrefab. CreateFromObjectBuilderAndAdd is
                // synchronous, so the callback fires immediately rather than waiting on SpawnPrefab's
                // own async completion.
                MyObjectBuilder_CubeGrid[] customGrids;
                if (_customPrefabStore != null && _customPrefabStore.TryGetGrids(prefabName, out customGrids))
                {
                    List<MyCubeGrid> spawned;
                    if (CustomPrefabSpawner.TrySpawn(customGrids, spawnPos, faction.FounderId, out spawned))
                        resultList.AddRange(spawned);
                    else
                        Log.Warn("VanillaFleetSpawner: CustomPrefabSpawner produced no grids for prefab=" + prefabName);

                    Action callback = callbacks.Count > 0 ? callbacks.Pop() : null;
                    callback?.Invoke();
                    return; // caller polls _completed next tick, same as the async path below
                }
                MyPrefabManager.Static.SpawnPrefab(
                    resultList,
                    prefabName,
                    spawnPos,
                    Vector3.Forward,
                    Vector3.Up,
                    // SetNpcSpawnedGrid stamps the bit into the object builder before the grid exists,
                    // so every part is simulation-born from its first frame; SimGridMarks sets the same
                    // bit on anything that reaches the world by another route.
                    spawningOptions: SpawningOptions.SetAuthorship | SpawningOptions.UseOnlyWorldMatrix
                                     | SpawningOptions.SetNpcSpawnedGrid,
                    ownerId:    faction.FounderId,
                    updateSync: true,
                    callbacks:  callbacks
                );
            }

            // FAC-443: every hull is assembled over several frames (GridAssembler), built static and
            // converted to a ship once complete. A prefab that fails to assemble spawns the old way for the
            // rest of the process.
            if (!_assemblyRefused.Contains(prefabName))
            {
                MyObjectBuilder_CubeGrid[] prepared = PrepareFleetGrids(prefabName, spawnPos, faction.FounderId);
                if (prepared != null)
                {
                    Action landed = callbacks.Pop();
                    bool begun = GridAssembler.Instance.Begin(fleetKey, prepared, true,
                        grids => { resultList.AddRange(grids); landed(); },
                        why =>
                        {
                            _assemblyRefused.Add(prefabName);
                            Log.Warn("VanillaFleetSpawner: assembly of prefab=" + prefabName + " failed, " + why
                                     + "; spawning it the old way from now on.");
                            resultList.Clear();
                            callbacks.Push(landed);
                            SpawnTheOldWay();
                        });
                    if (begun) return 0; // caller polls _completed next tick
                    _assemblyRefused.Add(prefabName);
                    callbacks.Push(landed);
                }
            }

            SpawnTheOldWay();
            return 0; // async — caller polls next tick
        }

        private struct HullProbeDue
        {
            public long Eid;
            public DateTime StartUtc;
            public DateTime DueUtc;
            public string Label;
        }

        private readonly List<HullProbeDue> _hullProbes = new List<HullProbeDue>();

        /// <summary>Logs <see cref="HullThrustProbe.Describe"/> for <paramref name="eid"/> as a
        /// <c>HULL_PROBE</c> line at each of <paramref name="seconds"/> from now. Operator verbs only.</summary>
        internal void ScheduleHullProbes(long eid, string label, params double[] seconds)
        {
            DateTime now = DateTime.UtcNow;
            foreach (double s in seconds)
                _hullProbes.Add(new HullProbeDue { Eid = eid, StartUtc = now, DueUtc = now.AddSeconds(s), Label = label });
        }

        private void TickHullProbes()
        {
            if (_hullProbes.Count == 0) return;
            DateTime now = DateTime.UtcNow;
            for (int i = _hullProbes.Count - 1; i >= 0; i--)
            {
                HullProbeDue p = _hullProbes[i];
                if (now < p.DueUtc) continue;
                _hullProbes.RemoveAt(i);
                MyCubeGrid grid;
                MyEntities.TryGetEntityById(p.Eid, out grid, false);
                Log.Info("HULL_PROBE " + p.Label + " eid=" + p.Eid
                         + " t=" + (now - p.StartUtc).TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                         + " " + HullThrustProbe.Describe(grid));
            }
        }

        /// <summary>Operator test (bridge <c>factions assemble</c>): assembles one prefab at
        /// <paramref name="spawnPos"/>, owned by an NPC faction's founder and flying nowhere, so an assembly can be tried once, on
        /// purpose, instead of on whichever hull happens to spawn next. The grids are named
        /// <c>Assembly test &lt;prefab&gt;</c>; close them with <c>factions closegrid</c>. Each grid is
        /// probed (<c>HULL_PROBE</c>) at 0, 1, 3 and 10 s after it lands. <paramref name="oneFrame"/>
        /// spawns it through SE's own <c>SpawnPrefab</c> instead, as the control.</summary>
        internal string AssembleForTest(string prefabName, Vector3D spawnPos, long ownerId, bool oneFrame)
        {
            if (oneFrame) return SpawnOneFrameForTest(prefabName, spawnPos, ownerId);
            MyObjectBuilder_CubeGrid[] prepared = PrepareFleetGrids(prefabName, spawnPos, ownerId);
            if (prepared == null)
                return "assemble: '" + prefabName + "' is not a prefab or capture, or SE has not loaded its definition yet.";
            foreach (MyObjectBuilder_CubeGrid g in prepared) g.DisplayName = "Assembly test " + prefabName;
            int grids = prepared.Length, blocks = 0;
            foreach (MyObjectBuilder_CubeGrid g in prepared) blocks += g.CubeBlocks != null ? g.CubeBlocks.Count : 0;
            bool begun = GridAssembler.Instance.Begin("test:" + prefabName, prepared, true,
                done =>
                {
                    var ids = new List<string>();
                    int stillStatic = 0;
                    foreach (MyCubeGrid g in done) { ids.Add(g.EntityId.ToString()); if (g.IsStatic) stillStatic++; }
                    Log.Info("ASSEMBLY_TEST done prefab=" + prefabName + " grids=" + done.Count + " stillStatic=" + stillStatic
                             + " eids=" + string.Join(",", ids));
                    foreach (MyCubeGrid g in done) ScheduleHullProbes(g.EntityId, "assembled " + prefabName, 0, 1, 3, 10);
                },
                why => Log.Warn("ASSEMBLY_TEST failed prefab=" + prefabName + " " + why));
            return begun
                ? "assemble: started '" + prefabName + "' (" + grids + " grid(s), " + blocks + " blocks) at "
                  + spawnPos.ToString("F0") + "; watch the Torch log for ASSEMBLY_TEST."
                : "assemble: could not start '" + prefabName + "'.";
        }

        private string SpawnOneFrameForTest(string prefabName, Vector3D spawnPos, long ownerId)
        {
            MyPrefabDefinition def = MyDefinitionManager.Static != null ? MyDefinitionManager.Static.GetPrefabDefinition(prefabName) : null;
            if (def == null) return "assemble: '" + prefabName + "' is not an SE prefab (oneframe takes SE prefabs only).";
            var grids = new List<MyCubeGrid>();
            var callbacks = new Stack<Action>();
            callbacks.Push(() =>
            {
                var ids = new List<string>();
                foreach (MyCubeGrid g in grids)
                {
                    ids.Add(g.EntityId.ToString());
                    ScheduleHullProbes(g.EntityId, "oneframe " + prefabName, 0, 1, 3, 10);
                }
                Log.Info("ASSEMBLY_TEST oneframe prefab=" + prefabName + " grids=" + grids.Count + " eids=" + string.Join(",", ids));
            });
            MyPrefabManager.Static.SpawnPrefab(grids, prefabName, spawnPos, Vector3.Forward, Vector3.Up,
                spawningOptions: SpawningOptions.SetAuthorship | SpawningOptions.UseOnlyWorldMatrix | SpawningOptions.SetNpcSpawnedGrid,
                ownerId: ownerId, updateSync: true, callbacks: callbacks);
            return "assemble: spawning '" + prefabName + "' in one frame at " + spawnPos.ToString("F0")
                 + "; watch the Torch log for ASSEMBLY_TEST and HULL_PROBE.";
        }

        /// <summary>Each prefab's grids once, with its projectors' blueprints removed, so a spawn clones the
        /// hull and not the blueprint: a Naginata's projector holds 47,187 blocks against a 2,026-block hull,
        /// and cloning it put ~120 ms on the frame each assembly started (2026-10-04 Naginata test). The
        /// first spawn of a prefab pays the clone once.</summary>
        private readonly Dictionary<string, MyObjectBuilder_CubeGrid[]> _lightened =
            new Dictionary<string, MyObjectBuilder_CubeGrid[]>(StringComparer.Ordinal);

        /// <summary>A clone of <paramref name="grid"/> whose projectors carry no blueprint, without
        /// cloning the blueprints: they are lifted off the template for the clone and put back. The first
        /// Naginata copy, cloning its 47,187-block blueprint and stripping it after, was a 344 ms frame
        /// (2026-10-04 22:08:25Z). Game thread only; the template is SE's shared definition.</summary>
        private static MyObjectBuilder_CubeGrid CloneWithoutProjections(MyObjectBuilder_CubeGrid grid)
        {
            var lifted = new List<KeyValuePair<MyObjectBuilder_ProjectorBase, KeyValuePair<MyObjectBuilder_CubeGrid, List<MyObjectBuilder_CubeGrid>>>>();
            if (grid.CubeBlocks != null)
                foreach (MyObjectBuilder_CubeBlock b in grid.CubeBlocks)
                {
                    var p = b as MyObjectBuilder_ProjectorBase;
                    if (p == null || (p.ProjectedGrid == null && p.ProjectedGrids == null)) continue;
                    lifted.Add(new KeyValuePair<MyObjectBuilder_ProjectorBase, KeyValuePair<MyObjectBuilder_CubeGrid, List<MyObjectBuilder_CubeGrid>>>(
                        p, new KeyValuePair<MyObjectBuilder_CubeGrid, List<MyObjectBuilder_CubeGrid>>(p.ProjectedGrid, p.ProjectedGrids)));
                    p.ProjectedGrid = null;
                    p.ProjectedGrids = null;
                }
            try { return (MyObjectBuilder_CubeGrid)grid.Clone(); }
            finally
            {
                foreach (var kv in lifted) { kv.Key.ProjectedGrid = kv.Value.Key; kv.Key.ProjectedGrids = kv.Value.Value; }
            }
        }

        private MyObjectBuilder_CubeGrid[] Lightened(string prefabName, MyObjectBuilder_CubeGrid[] template)
        {
            MyObjectBuilder_CubeGrid[] light;
            if (_lightened.TryGetValue(prefabName, out light)) return light;
            light = new MyObjectBuilder_CubeGrid[template.Length];
            for (int i = 0; i < template.Length; i++) light[i] = CloneWithoutProjections(template[i]);
            _lightened[prefabName] = light;
            return light;
        }

        /// <summary>Prefabs whose assembly failed this process; they spawn the old way from then on.</summary>
        private readonly HashSet<string> _assemblyRefused = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>A hull's grids ready for <see cref="GridAssembler"/>: cloned, placed as the old path
        /// places them, owned by <paramref name="ownerId"/>, NPC-flagged; ids not yet remapped. A captured
        /// design keeps its captured orientation (as <see cref="CustomPrefabSpawner.TrySpawn"/>); an SE
        /// prefab is placed as <c>SpawnPrefab</c> with <c>UseOnlyWorldMatrix</c> places it, relative to its
        /// largest grid. Null when there is nothing to assemble, or when SE has not loaded the prefab
        /// definition yet: that load would run here on the game thread, so the old path, whose worker
        /// loads it, spawns it once instead.</summary>
        internal MyObjectBuilder_CubeGrid[] PrepareFleetGrids(string prefabName, Vector3D spawnPos, long ownerId)
        {
            MyObjectBuilder_CubeGrid[] template = null;
            bool captured = _customPrefabStore != null && _customPrefabStore.TryGetGrids(prefabName, out template);
            if (!captured)
            {
                MyPrefabDefinition def = MyDefinitionManager.Static != null ? MyDefinitionManager.Static.GetPrefabDefinition(prefabName) : null;
                if (def == null || !def.Initialized) return null;
                template = def.CubeGrids;
            }
            if (template == null || template.Length == 0) return null;
            template = Lightened(prefabName, template);

            var grids = new MyObjectBuilder_CubeGrid[template.Length];
            int biggest = 0;
            for (int i = 0; i < template.Length; i++)
            {
                grids[i] = (MyObjectBuilder_CubeGrid)template[i].Clone();
                if (grids[i].CubeBlocks != null && grids[biggest].CubeBlocks != null
                    && grids[i].CubeBlocks.Count > grids[biggest].CubeBlocks.Count) biggest = i;
            }

            MatrixD reference = grids[captured ? 0 : biggest].PositionAndOrientation.HasValue
                ? grids[captured ? 0 : biggest].PositionAndOrientation.Value.GetMatrix() : MatrixD.Identity;
            foreach (MyObjectBuilder_CubeGrid g in grids)
            {
                MatrixD local = g.PositionAndOrientation.HasValue ? g.PositionAndOrientation.Value.GetMatrix() : MatrixD.Identity;
                MatrixD world;
                if (captured)
                {
                    world = local;
                    world.Translation = local.Translation + (spawnPos - reference.Translation);
                }
                else world = local * MatrixD.Invert(reference) * MatrixD.CreateWorld(spawnPos, Vector3D.Forward, Vector3D.Up);
                g.PositionAndOrientation = new VRage.MyPositionAndOrientation(world);
                g.LinearVelocity = Vector3.Zero;
                g.AngularVelocity = Vector3.Zero;
                g.IsNpcSpawnedGrid = true;
                if (g.CubeBlocks == null) continue;
                foreach (MyObjectBuilder_CubeBlock b in g.CubeBlocks)
                {
                    if (b == null) continue;
                    b.ShareMode = MyOwnershipShareModeEnum.Faction;
                    b.Owner = ownerId;
                    b.BuiltBy = ownerId;
                }
            }
            return grids;
        }

        /// <summary>
        /// Returns a spawn position that is at least <see cref="SpawnMinClearanceM"/> metres from
        /// <paramref name="center"/> and has no colliding grids or voxels within
        /// <see cref="SpawnCheckRadiusM"/> metres. Tries up to 10 random directions; if all are
        /// blocked, falls back to the travel direction with a warning.
        /// </summary>
        /// <summary>Appends " name=ms" for the step since <paramref name="t"/> and restarts it.</summary>
        private static void StepMs(StringBuilder sb, string name, ref long t)
        {
            long now = Stopwatch.GetTimestamp();
            sb.Append(' ').Append(name).Append('=').Append(((now - t) * 1000.0 / Stopwatch.Frequency).ToString("F0"));
            t = now;
        }

        private static Vector3D PickSpawnPos(Vector3D center, Vector3D target, string fleetKey)
        {
            // Seed per-fleet so the same fleet always picks the same offset (stable across poll ticks).
            var rng = new Random(fleetKey.GetHashCode());

            for (int attempt = 0; attempt < 10; attempt++)
            {
                // Uniform random point on the unit sphere (Marsaglia / trig method).
                double cosP = rng.NextDouble() * 2.0 - 1.0;
                double sinP = Math.Sqrt(1.0 - cosP * cosP);
                double theta = rng.NextDouble() * Math.PI * 2.0;
                double dist  = SpawnMinClearanceM + rng.NextDouble() * SpawnExtraRangeM;
                var dir = new Vector3D(sinP * Math.Cos(theta), sinP * Math.Sin(theta), cosP);
                Vector3D candidate = center + dir * dist;

                // FindFreePlaceCustom rejects positions inside grids, voxels, and safezones (allowSafezones=false default).
                Vector3D? free = MyEntities.FindFreePlaceCustom(candidate, SpawnCheckRadiusM);
                if (free.HasValue) return free.Value;
            }

            // All attempts blocked — offset along travel direction as a last resort.
            Vector3D fallback = target - center;
            double len = fallback.Length();
            fallback = len > 0 ? fallback / len : Vector3D.Up;
            Log.Warn("VanillaFleetSpawner: all spawn positions blocked near "
                     + center.ToString("F0") + " for key=" + fleetKey + " — using travel-axis fallback.");
            return center + fallback * SpawnMinClearanceM;
        }

        private const double SpawnMinClearanceM = 300.0; // minimum distance from abstract fleet pos (m)
        private const double SpawnExtraRangeM   = 300.0; // additional random range on top of minimum (m)
        private const float  SpawnCheckRadiusM  =  60f;  // collision-check sphere radius (m) — fits a cargo ship

        /// <summary>Fallback RC autopilot speed cap (m/s), used only when no per-grid figure can be
        /// computed. Vanilla's own default is 30 m/s, which crawls.
        ///
        /// <para>The real cap is per grid and comes from
        /// <see cref="FleetFlightController.CruiseSpeedCapMs"/>: the speed this hull can still shed
        /// before the terminal bubble, from braking authority it has been observed to deliver. One
        /// constant is wrong for both a 0.85 m/s² hauler and a 17 m/s² escort, and the constant that
        /// was here is the reason every convoy flew at 100 m/s on a server whose grid limit is
        /// 500.</para></summary>
        public static float AutopilotSpeedMs = 100f;

        /// <summary>Whether any player is within the given km of a point. Set by the host; null means
        /// unknown, and every leg keeps the braking cap.</summary>
        public Func<Vec3, double, bool> PlayerWithinKm { get; set; }

        /// <summary>The autopilot speed limit for this grid — the clamp's figure when the controller
        /// can produce one, else the flat fallback.
        ///
        /// <para><b>A leg whose target no player can see is flown at the world's limit.</b> The clamp
        /// exists so a grid can stop at its target inside the radius a player watches; a grid heading
        /// somewhere nobody is near is despawned as it leaves the last player's radius and never brakes
        /// in view. Fresh grids have no braking record, so every departure was held to the 100 m/s
        /// fallback (2026-10-01: 555 of 558 launches), keeping it in view about twice as long as it
        /// needed to be. "Nobody near" is twice the observation radius plus the despawn margin from the
        /// target, so a player near the destination is far enough off that the grid has left the
        /// origin player's radius long before.</para></summary>
        private float SpeedLimitFor(MyCubeGrid grid, Vector3D? targetWorld = null)
        {
            if (_flight == null || grid == null) return AutopilotSpeedMs;
            if (targetWorld.HasValue && TargetOutOfSight(targetWorld.Value))
            {
                double ceiling = _flight.CruiseCeilingMs(grid);
                if (ceiling > 0) return (float)ceiling;
            }
            double cap = _flight.CruiseSpeedCapMs(grid);
            return cap > 0 ? (float)cap : AutopilotSpeedMs;
        }

        private bool TargetOutOfSight(Vector3D targetWorld)
        {
            Func<Vec3, double, bool> within = PlayerWithinKm;
            if (within == null || _tuning == null) return false;
            var km = new Vec3(targetWorld.X / 1000.0, targetWorld.Y / 1000.0, targetWorld.Z / 1000.0);
            return !within(km, 2.0 * _tuning.ObservationRadiusKm + FactionRuntime.DespawnHysteresisKm);
        }

        // All fleet kinds fly OneWay to target. Patrol fleets travel from their origin node to the
        // patrol destination node; the "loop" is at the brain level (it keeps dispatching new
        // patrols), not the grid autopilot level. Returns whether an autopilot was successfully
        // wired (used both at spawn time and by Retarget to rewire an already-flying grid).
        private bool WireAutopilot(MyCubeGrid grid, Vector3D targetWorld, bool collisionAvoidance = true)
        {
            // Try legacy RemoteControl first (most vanilla cargo ships pre-2025 updates).
            MyRemoteControl rc = null;
            foreach (MyRemoteControl block in grid.GetFatBlocks<MyRemoteControl>())
            {
                rc = block;
                break;
            }

            // A present RC is not a working one: WireRemoteControl reports whether the autopilot
            // actually came on, so a damaged or uncontrollable RC falls through to the paths below
            // rather than leaving the grid inert.
            if (rc != null && WireRemoteControl(rc, targetWorld, collisionAvoidance)) return true;

            // Fall back to new AI-block system (vanilla ships with "AI Basic (Task)" + "AI Flight (Move)").
            if (WireAiAutopilot(grid, targetWorld)) return true;

            // Fallbacks 3-5: put an RC on the hull (see InjectRc).
            string donor;
            rc = InjectRc(grid, out donor);
            if (rc != null)
            {
                bool ok = WireRemoteControl(rc, targetWorld, collisionAvoidance);
                if (ok) Log.Info("VanillaFleetSpawner: replaced " + donor + " with RC on grid eid=" + grid.EntityId + RcOrientationTag());
                return ok;
            }

            Log.Warn("VanillaFleetSpawner: no RC, AI blocks, cockpit, or armor on grid eid=" + grid.EntityId
                     + " — autopilot not set.");
            return false;
        }

        /// <summary>
        /// Puts a remote control on a hull that has none, in the order the fleet path uses: on a small
        /// grid an armor cube first (<see cref="TrySwapArmorForRc"/> — a small seat spans several cells
        /// and the small RC fills one), then a seat (<see cref="TrySwapCockpitForRc"/>), then armor on
        /// a bounding-box face (<see cref="TryAddRcOnFrontFace"/>). <paramref name="donor"/> names what
        /// gave up its cell — <c>armor</c>, <c>seat</c> or <c>front-face armor</c> — or is null when
        /// nothing did. The one sequence both <see cref="WireAutopilot"/> and <c>factions bench rc</c>
        /// run, so the bench tests the fleet path rather than a copy of it.
        /// </summary>
        internal static MyRemoteControl InjectRc(MyCubeGrid grid, out string donor)
        {
            MyRemoteControl rc;
            if (grid.GridSizeEnum == MyCubeSize.Small)
            {
                rc = TrySwapArmorForRc(grid);
                if (rc != null) { donor = "armor"; return rc; }
            }

            rc = TrySwapCockpitForRc(grid);
            if (rc != null) { donor = "seat"; return rc; }

            rc = TryAddRcOnFrontFace(grid);
            if (rc != null) { donor = "front-face armor"; return rc; }

            donor = null;
            return null;
        }

        /// <summary>The attitude, cell, neighbour count and reach of the RC <see cref="InjectRc"/>
        /// placed last, in the form the wiring log line carries.</summary>
        internal static string LastRcTag() { return RcOrientationTag(); }

        /// <summary>
        /// Wires the cruise half of the hybrid flight model onto a legacy <see cref="MyRemoteControl"/>.
        /// Called both for a fresh spawn and for a retarget; <c>ClearWaypoints()</c> first makes the
        /// two cases identical, so an already-flying grid re-aims at the new leg's waypoint.
        /// Terminal approach is not vanilla's job — <see cref="FleetFlightController"/> takes the
        /// grid over inside the approach horizon.
        /// </summary>
        private bool WireRemoteControl(MyRemoteControl rc, Vector3D targetWorld, bool collisionAvoidance = true)
        {
            rc.ClearWaypoints();
            rc.ChangeFlightMode(FlightMode.OneWay);
            rc.AddWaypoint(targetWorld, "Destination");
            rc.SetAutoPilotSpeedLimit(SpeedLimitFor(rc.CubeGrid, targetWorld));

            // Cruise-phase collision avoidance: SE steers around real block geometry and throttles
            // itself, which is most of what keeps a convoy off station structure en route. It can
            // also stop a grid outright: its pathfinder clamps the autopilot's speed limit to zero
            // when it cannot route around the nearest grid, and a zero limit gates off all thrust,
            // which SE's AI-thrust path reads as full-authority braking rather than as coasting.
            // A grid whose handback has already failed twice is re-wired without it.
            rc.SetCollisionAvoidance(collisionAvoidance);

            // Safety net only. The native controller normally takes over well before arrival; if it
            // ever fails to engage, this stops vanilla aiming at the target's exact centre point
            // (its final-waypoint tolerance is otherwise a 0.625 m sphere, i.e. inside the station).
            rc.SetWaypointThresholdDistance((float)StopDistanceM);

            rc.SetAutoPilotEnabled(true);

            // SetAutoPilotEnabled is gated on CanEnableAutoPilot() and silently no-ops when that
            // fails — the RC must be functional and must not still hold a previously controlled
            // entity. Read the flag back rather than assuming the call took.
            MyAutopilotComponent apc;
            if (rc.GetComponent(out apc) && apc != null && !(bool)apc.AutoPilotEnabled)
            {
                Log.Warn("VanillaFleetSpawner: RC autopilot refused to enable on grid eid="
                         + rc.CubeGrid.EntityId + " (functional=" + rc.IsFunctional
                         + " canEnable=" + apc.CanEnableAutoPilot() + ") — trying other paths.");
                return false;
            }
            return true;
        }

        /// <summary>Approach stop distance, from tuning when available. Also the cruise autopilot's
        /// waypoint tolerance, so both halves of the hybrid agree on where "arrived" is.</summary>
        private double StopDistanceM
        {
            get { return _tuning != null ? _tuning.ApproachStopDistanceM : 50.0; }
        }

        /// <summary>
        /// First slice of live Raid combat (see docs/todo.md "Live combat behaviour for
        /// materialised fleets"): turret auto-targeting/firing is already a proven vanilla AI
        /// behavior — the only two things blocking it on a materialised grid are the live engagement
        /// <see cref="MyLargeTurretBase.ShootRangeSimple"/> defaulting to 800m regardless of the
        /// weapon's real range, and (defensively) the block possibly pasted in a disabled state.
        /// Deliberately does NOT touch <c>TargetNeutrals</c> — that's a blanket flag that would also
        /// make the turret treat any neutral entity (including a squadron-mate mid-spawn, before its
        /// ownership has settled) as a valid target. Hostility toward the specific victim/intercepting
        /// player is handled via SE faction/player reputation, not turret target flags — see the
        /// "Player Overhaul" coordination note in docs/todo.md.
        /// </summary>
        private static void ArmTurretsForCombat(MyCubeGrid grid)
        {
            foreach (MyLargeTurretBase turret in grid.GetFatBlocks<MyLargeTurretBase>())
            {
                turret.Enabled = true; // defensive: some prefabs paste weapons in an off state

                var turretDef = turret.BlockDefinition as MyLargeTurretBaseDefinition;
                float maxRangeM = turretDef != null ? turretDef.MaxRangeMeters : 0f;
                if (maxRangeM > turret.ShootRangeSimple)
                    turret.ShootRangeSimple = maxRangeM;
            }
        }

        // ---- RC injection helpers ----------------------------------------------------------

        // Cached reflection handle for MyCubeGrid.AddBlock(MyObjectBuilder_CubeBlock, bool).
        // Shared by both cockpit-swap and armor-swap paths.
        private static MethodInfo _addBlockMethod;
        private static bool _addBlockMethodSearched;

        /// <summary>Lazily resolves the private MyCubeGrid.AddBlock overload via reflection.</summary>
        private static bool EnsureAddBlockMethod()
        {
            if (_addBlockMethodSearched) return _addBlockMethod != null;
            _addBlockMethodSearched = true;
            _addBlockMethod = typeof(MyCubeGrid).GetMethod(
                "AddBlock",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new Type[] { typeof(VRage.Game.MyObjectBuilder_CubeBlock), typeof(bool) },
                null);
            if (_addBlockMethod == null)
                Log.Warn("VanillaFleetSpawner: could not reflect MyCubeGrid.AddBlock — RC injection unavailable.");
            return _addBlockMethod != null;
        }

        /// <summary>
        /// Builds the RC object-builder for <paramref name="grid"/> at <paramref name="pos"/>, facing
        /// <paramref name="orientation"/>.
        ///
        /// <para>The orientation is not cosmetic. SE's autopilot flies the hull along the <b>remote
        /// control's</b> forward — it is what <c>UpdateGyro</c> aims at the waypoint and what
        /// <c>m_currentDirection</c> feeds to <c>GetMaxThrustInDirection</c>. A hull whose authored
        /// nose is not grid-local −Z therefore flies sideways on a default-oriented RC, down whichever
        /// axis happens to have the least thrust.</para>
        /// </summary>
        private static MyObjectBuilder_RemoteControl MakeRcOb(MyCubeGrid grid, VRageMath.Vector3I pos,
                                                              VRageMath.MyBlockOrientation orientation)
        {
            return new MyObjectBuilder_RemoteControl
            {
                SubtypeName      = grid.GridSizeEnum == MyCubeSize.Large
                                       ? "LargeBlockRemoteControl"
                                       : "SmallBlockRemoteControl",
                Min              = pos,
                BlockOrientation = new VRage.Game.SerializableBlockOrientation(orientation.Forward, orientation.Up),
                Owner            = grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0L,
                ShareMode        = VRage.Game.MyOwnershipShareModeEnum.Faction,
                BuildPercent     = 1f,
                IntegrityPercent = 1f,
            };
        }

        /// <summary>Every seat on the hull, with the block behind each one so a cell can be taken
        /// back. SE's cockpit class covers furniture too, so this list is a mix of flight stations,
        /// passenger seats, beds and desks — <see cref="HullSeat.ControlsShip"/> is what separates
        /// them. An existing remote control joins the list as evidence of the authored nose without
        /// a block, since it is never a cell to take.</summary>
        private sealed class SeatScan
        {
            public readonly List<HullSeat> Seats = new List<HullSeat>();
            public readonly List<Sandbox.Game.Entities.Cube.MySlimBlock> Blocks =
                new List<Sandbox.Game.Entities.Cube.MySlimBlock>();   // null where the seat is a remote control
        }

        private static SeatScan ScanSeats(MyCubeGrid grid)
        {
            var scan = new SeatScan();
            foreach (MyCockpit c in grid.GetFatBlocks<MyCockpit>())
            {
                MyCockpitDefinition def = c.BlockDefinition;
                scan.Seats.Add(new HullSeat
                {
                    Forward      = ThrustStatsHelper.ToThrustDirection(c.SlimBlock.Orientation.Forward),
                    Up           = ThrustStatsHelper.ToThrustDirection(c.SlimBlock.Orientation.Up),
                    IsMain       = c.IsMainCockpit,
                    ControlsShip = def != null && def.EnableShipControl,
                });
                scan.Blocks.Add(c.SlimBlock);
            }
            foreach (MyRemoteControl r in grid.GetFatBlocks<MyRemoteControl>())
            {
                scan.Seats.Add(new HullSeat
                {
                    Forward      = ThrustStatsHelper.ToThrustDirection(r.SlimBlock.Orientation.Forward),
                    Up           = ThrustStatsHelper.ToThrustDirection(r.SlimBlock.Orientation.Up),
                    IsMain       = false,
                    ControlsShip = true,
                });
                scan.Blocks.Add(null);
            }
            return scan;
        }

        /// <summary>Thrust the hull can apply toward each grid-local direction, summed off block
        /// definitions rather than the live <c>MyEntityThrustComponent</c> — this runs on the tick
        /// the grid is pasted, before power, fuel or damage state settle, and the question is
        /// geometry rather than what is currently available.</summary>
        private static Dictionary<ThrustDirection, double> ThrustForceByPushDirection(MyCubeGrid grid)
        {
            var force = new Dictionary<ThrustDirection, double>();
            foreach (MyThrust t in grid.GetFatBlocks<MyThrust>())
            {
                MyThrustDefinition def = t.BlockDefinition;
                if (def == null) continue;
                // MyThrust.ThrustForce = -ThrustForwardVector * ForceMagnitude: a thruster pushes
                // the grid the way it is not facing.
                ThrustDirection push = HullOrientation.Opposite(
                    ThrustStatsHelper.ToThrustDirection(t.SlimBlock.Orientation.Forward));
                double have;
                force[push] = (force.TryGetValue(push, out have) ? have : 0) + def.ForceMagnitude;
            }
            return force;
        }

        /// <summary>The attitude to give an injected remote control: the hull's own nose, decided by
        /// <see cref="HullOrientation.SelectNose"/> from the seats its author placed and the
        /// thrust the hull carries. <paramref name="source"/> names which of those decided it.</summary>
        private static VRageMath.MyBlockOrientation HullFlightOrientation(MyCubeGrid grid, SeatScan scan,
                                                                          out HullNoseSource source)
        {
            ThrustDirection nose, up;
            HullOrientation.SelectNose(scan.Seats, ThrustForceByPushDirection(grid), out nose, out up, out source);
            return new VRageMath.MyBlockOrientation(
                ThrustStatsHelper.ToBase6Direction(nose), ThrustStatsHelper.ToBase6Direction(up));
        }

        /// <summary>
        /// Takes a seat on <paramref name="grid"/> and places an RC in the first cell of its volume
        /// that attaches to the hull without cutting anything off it (<see cref="SwapBlockForRc"/>),
        /// facing the hull's nose. Returns the new block, or null if there is no seat to take or no
        /// cell of any seat qualifies; a seat that does not qualify is put back.
        ///
        /// <para>The cell and the attitude come from different seats on purpose. Furniture is
        /// preferred as the donor so the hull keeps its flight station — and a bathroom's facing is
        /// no evidence of anything, which is exactly why the attitude is decided separately.</para>
        /// </summary>
        private static MyRemoteControl TrySwapCockpitForRc(MyCubeGrid grid)
        {
            SeatScan scan = ScanSeats(grid);
            if (scan.Seats.Count == 0) return null;
            if (!EnsureAddBlockMethod()) return null;

            HullNoseSource source;
            VRageMath.MyBlockOrientation facing = HullFlightOrientation(grid, scan, out source);

            foreach (int i in DonorOrder(scan))
            {
                Sandbox.Game.Entities.Cube.MySlimBlock slim = scan.Blocks[i];
                VRageMath.Vector3I min = slim.Min, max = slim.Max;

                MyRemoteControl rc = SwapBlockForRc(grid, slim, facing);
                if (rc != null)
                {
                    LastRcNoseSource = source;
                    return rc;
                }
                Log.Warn("VanillaFleetSpawner: seat kept, no cell of it holds an RC that leaves the hull whole, on grid eid="
                         + grid.EntityId + " min=" + min + " max=" + max + " — trying the next seat.");
            }
            return null;
        }

        /// <summary>Most armor cubes a small grid offers for its RC before the seat path is tried.
        /// Each offer is a remove, a place and a search over the hull, at spawn time.</summary>
        private const int MaxArmorDonors = 48;

        /// <summary>
        /// Small grids: gives up one full armor cube for the RC, buried cubes first and nearest the
        /// flight seat (<see cref="RcDonorOrder"/>). The seat stays, so the hull keeps its flight
        /// station, and the exchange is one cell for one cell. A cube is accepted only when the RC in
        /// its place leaves the hull in one piece (<see cref="SwapBlockForRc"/>); otherwise it is put
        /// back and the next is offered.
        /// </summary>
        private static MyRemoteControl TrySwapArmorForRc(MyCubeGrid grid)
        {
            if (!EnsureAddBlockMethod()) return null;

            SeatScan scan = ScanSeats(grid);
            HullNoseSource source;
            VRageMath.MyBlockOrientation facing = HullFlightOrientation(grid, scan, out source);

            VRageMath.Vector3 reference = (VRageMath.Vector3)(grid.Min + grid.Max) * 0.5f;
            for (int i = 0; i < scan.Seats.Count; i++)
            {
                if (scan.Blocks[i] == null || !scan.Seats[i].ControlsShip) continue;
                reference = scan.Blocks[i].Position;
                break;
            }

            var candidates = new List<Sandbox.Game.Entities.Cube.MySlimBlock>();
            var covered = new List<int>();
            var distSq = new List<double>();
            foreach (Sandbox.Game.Entities.Cube.MySlimBlock slim in grid.CubeBlocks)
            {
                if (!IsFullArmorCube(slim)) continue;
                candidates.Add(slim);
                covered.Add(CoveredFaces(grid, slim.Position));
                distSq.Add(VRageMath.Vector3.DistanceSquared(slim.Position, reference));
            }

            foreach (int i in RcDonorOrder.Order(covered, distSq, MaxArmorDonors))
            {
                MyRemoteControl rc = SwapBlockForRc(grid, candidates[i], facing);
                if (rc != null)
                {
                    LastRcNoseSource = source;
                    return rc;
                }
            }
            return null;
        }

        /// <summary>A one-cell, full-box armor cube: mounts on all six faces and carries no
        /// functional block.</summary>
        private static bool IsFullArmorCube(Sandbox.Game.Entities.Cube.MySlimBlock slim)
        {
            if (slim == null || slim.FatBlock != null) return false;
            MyCubeBlockDefinition def = slim.BlockDefinition;
            return def != null
                && def.Size == VRageMath.Vector3I.One
                && def.BlockTopology == MyBlockTopology.Cube
                && def.CubeDefinition != null
                && def.CubeDefinition.CubeTopology == MyCubeTopology.Box;
        }

        /// <summary>How many of the six face-adjacent cells of <paramref name="cell"/> are occupied.</summary>
        private static int CoveredFaces(MyCubeGrid grid, VRageMath.Vector3I cell)
        {
            int n = 0;
            if (grid.CubeExists(cell + VRageMath.Vector3I.Right))    n++;
            if (grid.CubeExists(cell + VRageMath.Vector3I.Left))     n++;
            if (grid.CubeExists(cell + VRageMath.Vector3I.Up))       n++;
            if (grid.CubeExists(cell + VRageMath.Vector3I.Down))     n++;
            if (grid.CubeExists(cell + VRageMath.Vector3I.Forward))  n++;
            if (grid.CubeExists(cell + VRageMath.Vector3I.Backward)) n++;
            return n;
        }

        /// <summary>Removes <paramref name="donor"/> and places an RC in its volume
        /// (<see cref="PlaceAttachedRc"/>), accepted only if every block the donor touched is still
        /// connected to the RC. When no cell qualifies the donor is rebuilt from its own object
        /// builder and null is returned, so a refused exchange costs the hull nothing: SE's split pass
        /// runs after the frame (<c>MyCubeGrid.DetectDisconnects</c>, scheduled OnceAfterSimulation)
        /// and reads the neighbour graph as it stands then.</summary>
        private static MyRemoteControl SwapBlockForRc(MyCubeGrid grid, Sandbox.Game.Entities.Cube.MySlimBlock donor,
                                                      VRageMath.MyBlockOrientation facing)
        {
            var formerNeighbours = new List<Sandbox.Game.Entities.Cube.MySlimBlock>(donor.Neighbours);
            MyObjectBuilder_CubeBlock ob = donor.GetObjectBuilder(true);
            VRageMath.Vector3I min = donor.Min, max = donor.Max;

            grid.RemoveBlock(donor, updatePhysics: false);
            MyRemoteControl rc = PlaceAttachedRc(grid, min, max, facing, formerNeighbours);
            if (rc != null) return rc;

            if (ob != null && _addBlockMethod.Invoke(grid, new object[] { ob, false }) == null)
                Log.Warn("VanillaFleetSpawner: could not restore " + ob.SubtypeName + " at " + min
                         + " on grid eid=" + grid.EntityId + " after a refused RC exchange.");
            return null;
        }

        /// <summary>Which seats may give up their cell, furniture first. A remote control is never a
        /// donor (it has no cell to spare and would be swapped for itself).</summary>
        private static IEnumerable<int> DonorOrder(SeatScan scan)
        {
            for (int i = 0; i < scan.Seats.Count; i++)
                if (scan.Blocks[i] != null && !scan.Seats[i].ControlsShip) yield return i;
            for (int i = 0; i < scan.Seats.Count; i++)
                if (scan.Blocks[i] != null && scan.Seats[i].ControlsShip) yield return i;
        }

        /// <summary>The attitude, the evidence behind it, the cell and the neighbour count of the last
        /// RC this spawner injected, for the wiring log line. Single-threaded: injection and logging
        /// happen in the same call.</summary>
        private static VRageMath.MyBlockOrientation LastRcOrientation;
        private static HullNoseSource LastRcNoseSource;
        private static VRageMath.Vector3I LastRcCell;
        private static int LastRcNeighbours;
        private static int LastRcReach;

        private static string RcOrientationTag()
        {
            return " fwd=" + LastRcOrientation.Forward + " up=" + LastRcOrientation.Up
                 + " src=" + LastRcNoseSource
                 + " cell=" + LastRcCell.X + "," + LastRcCell.Y + "," + LastRcCell.Z
                 + " neighbours=" + LastRcNeighbours
                 + " reach=" + LastRcReach;
        }

        /// <summary>Places a remote control in the first cell of the vacated volume
        /// <paramref name="min"/>..<paramref name="max"/> where SE attaches it to a neighbour, and
        /// returns it, or null when no cell does.
        ///
        /// <para>SE links blocks only where mount points meet on the shared face, and the disconnect
        /// pass that every <c>RemoveBlock</c> schedules splits anything without a link off as its own
        /// grid on the next frame. Attachment therefore has to be to the body the RC is meant to fly,
        /// not to whatever happens to touch it: the cell taken from a large cockpit can touch a single
        /// interior light whose own only link was that same cockpit, and the pair leaves together.
        /// Reaching a thruster is the test, because commanding thrust is what the RC is for. A hull
        /// carrying no thruster at all keeps the weaker any-neighbour test rather than going
        /// unwired.</para>
        ///
        /// <para>The RC must also reach every block in <paramref name="formerNeighbours"/> — what the
        /// donor touched. Every path through the hull that ran through the donor ran between two of
        /// those, so this is exactly the condition under which nothing splits off: a small-grid seat
        /// spans several cells, and whatever was mounted on the ones the RC does not fill is cut
        /// loose otherwise.</para></summary>
        private static MyRemoteControl PlaceAttachedRc(MyCubeGrid grid, VRageMath.Vector3I min, VRageMath.Vector3I max,
                                                       VRageMath.MyBlockOrientation facing,
                                                       List<Sandbox.Game.Entities.Cube.MySlimBlock> formerNeighbours)
        {
            bool gridHasThrust = false;
            foreach (MyThrust unused in grid.GetFatBlocks<MyThrust>()) { gridHasThrust = true; break; }
            int budget = Math.Max(64, grid.BlocksCount + 1);

            for (int x = min.X; x <= max.X; x++)
            for (int y = min.Y; y <= max.Y; y++)
            for (int z = min.Z; z <= max.Z; z++)
            {
                var cell = new VRageMath.Vector3I(x, y, z);
                var placed = _addBlockMethod.Invoke(grid, new object[] { MakeRcOb(grid, cell, facing), false })
                             as Sandbox.Game.Entities.Cube.MySlimBlock;
                if (placed == null) continue;

                MyRemoteControl rc = placed.FatBlock as MyRemoteControl;
                int reach = 0;
                bool attached = rc != null
                    && placed.Neighbours.Count > 0
                    && GraphReach.ReachesAll(placed, NeighboursOf, formerNeighbours,
                                             gridHasThrust ? IsThruster : (Func<Sandbox.Game.Entities.Cube.MySlimBlock, bool>)null,
                                             budget, out reach);
                if (attached)
                {
                    LastRcOrientation = facing;
                    LastRcCell        = cell;
                    LastRcNeighbours  = placed.Neighbours.Count;
                    LastRcReach       = reach;
                    return rc;
                }
                grid.RemoveBlock(placed, updatePhysics: false);
            }
            return null;
        }

        private static IEnumerable<Sandbox.Game.Entities.Cube.MySlimBlock> NeighboursOf(
            Sandbox.Game.Entities.Cube.MySlimBlock block)
        {
            return block.Neighbours;
        }

        private static bool IsThruster(Sandbox.Game.Entities.Cube.MySlimBlock block)
        {
            return block.FatBlock is MyThrust;
        }

        /// <summary>
        /// Scans the six faces of the grid's bounding box (front face first) for an armor block,
        /// removes it, and places an RC in its cell if the RC attaches to the hull there
        /// (<see cref="PlaceAttachedRc"/>). Returns the new RC, or null if no face yields one.
        /// "Front face" is the face on the hull's own nose (<see cref="HullOrientation.SelectNose"/>),
        /// which is grid-local −Z only on a hull that was built that way.
        /// </summary>
        private static MyRemoteControl TryAddRcOnFrontFace(MyCubeGrid grid)
        {
            if (!EnsureAddBlockMethod()) return null;

            VRageMath.Vector3I gMin = grid.Min;
            VRageMath.Vector3I gMax = grid.Max;

            SeatScan scan = ScanSeats(grid);
            HullNoseSource source;
            VRageMath.MyBlockOrientation facing = HullFlightOrientation(grid, scan, out source);

            // The nose face first, then the rest in a fixed order: −Z, +Z, −X, +X, −Y, +Y.
            int[,] faces = new int[,]
            {
                { 2, gMin.Z }, { 2, gMax.Z },
                { 0, gMin.X }, { 0, gMax.X },
                { 1, gMin.Y }, { 1, gMax.Y },
            };
            MoveNoseFaceFirst(faces, facing.Forward, gMin, gMax);

            VRage.ObjectBuilders.MyObjectBuilderType armorTypeId =
                (VRage.ObjectBuilders.MyObjectBuilderType)typeof(VRage.Game.MyObjectBuilder_CubeBlock);

            for (int f = 0; f < 6; f++)
            {
                int axis  = faces[f, 0];
                int coord = faces[f, 1];

                Sandbox.Game.Entities.Cube.MySlimBlock armorSlim = null;
                foreach (Sandbox.Game.Entities.Cube.MySlimBlock slim in grid.CubeBlocks)
                {
                    int blockCoord = axis == 0 ? slim.Min.X : (axis == 1 ? slim.Min.Y : slim.Min.Z);
                    if (blockCoord != coord) continue;
                    if (slim.BlockDefinition.Id.TypeId != armorTypeId) continue;
                    armorSlim = slim;
                    break;
                }
                if (armorSlim == null) continue;

                MyRemoteControl rc = SwapBlockForRc(grid, armorSlim, facing);
                if (rc != null)
                {
                    LastRcNoseSource = source;
                    return rc;
                }

                // Not placed, or placed where it would cut the hull; the armor is back, try the next face.
                Log.Warn("VanillaFleetSpawner: armor kept, no RC there leaves the hull whole, at face axis=" + axis
                         + " coord=" + coord + " eid=" + grid.EntityId + " — trying next face.");
            }

            return null;
        }

        /// <summary>Swaps the face on <paramref name="nose"/> into slot 0 of <paramref name="faces"/>,
        /// so the RC lands on the hull's nose rather than on whatever sits at minimum Z.</summary>
        private static void MoveNoseFaceFirst(int[,] faces, Base6Directions.Direction nose,
                                              VRageMath.Vector3I gMin, VRageMath.Vector3I gMax)
        {
            int axis, coord;
            switch (nose)
            {
                case Base6Directions.Direction.Forward:  axis = 2; coord = gMin.Z; break;
                case Base6Directions.Direction.Backward: axis = 2; coord = gMax.Z; break;
                case Base6Directions.Direction.Left:     axis = 0; coord = gMin.X; break;
                case Base6Directions.Direction.Right:    axis = 0; coord = gMax.X; break;
                case Base6Directions.Direction.Down:     axis = 1; coord = gMin.Y; break;
                default:                                 axis = 1; coord = gMax.Y; break;
            }
            for (int f = 0; f < 6; f++)
            {
                if (faces[f, 0] != axis || faces[f, 1] != coord) continue;
                int swapAxis = faces[0, 0], swapCoord = faces[0, 1];
                faces[0, 0] = axis;      faces[0, 1] = coord;
                faces[f, 0] = swapAxis;  faces[f, 1] = swapCoord;
                return;
            }
        }

        /// <summary>
        /// Wires the AI-block autopilot for grids that use an "AI Basic (Task)" mission block and an
        /// "AI Flight (Move)" block instead of a legacy <see cref="MyRemoteControl"/>. Mirrors the
        /// approach used by <c>MyNeutralShipSpawner.AiBlocksWrapper</c>:
        /// <list type="bullet">
        /// <item>Finds the first <see cref="MyFlightMovementBlock"/> in the grid's AI-block list.</item>
        /// <item>Finds the first <see cref="MyBasicMissionBlock"/> that contains a
        ///   <see cref="MyBasicMissionAutopilot"/> component.</item>
        /// <item>Clears existing waypoints, adds <paramref name="targetWorld"/> as the sole waypoint,
        ///   then activates both AI block components and marks the autopilot mission as selected.</item>
        /// </list>
        /// Returns false if the required blocks are not present (caller logs a warning).
        /// </summary>
        private bool WireAiAutopilot(MyCubeGrid grid, Vector3D targetWorld)
        {
            if (grid.GridSystems == null || grid.GridSystems.AiBlockSystem == null) return false;

            MyFlightMovementBlock flightBlock = null;
            MyBasicMissionAutopilot missionAutopilot = null;
            MyAiBlockComponent flightAiComp = null;
            MyAiBlockComponent taskAiComp = null;

            foreach (MyCubeBlock block in grid.GridSystems.AiBlockSystem.AiBlocks)
            {
                if (flightBlock == null)
                {
                    var fb = block as MyFlightMovementBlock;
                    if (fb != null)
                    {
                        flightBlock = fb;
                        block.Components.TryGet<MyAiBlockComponent>(out flightAiComp);
                        continue;
                    }
                }

                if (missionAutopilot == null)
                {
                    var mb = block as MyBasicMissionBlock;
                    if (mb != null)
                    {
                        // Iterate the block's component bag to find the autopilot mission component.
                        foreach (VRage.Game.Components.MyComponentBase comp in mb.Components)
                        {
                            var ma = comp as MyBasicMissionAutopilot;
                            if (ma != null) { missionAutopilot = ma; break; }
                        }
                        if (missionAutopilot != null)
                            block.Components.TryGet<MyAiBlockComponent>(out taskAiComp);
                    }
                }

                if (flightBlock != null && missionAutopilot != null) break;
            }

            if (flightBlock == null || missionAutopilot == null) return false;

            flightBlock.SpeedLimit = SpeedLimitFor(grid, targetWorld);
            missionAutopilot.ClearWaypoints();
            missionAutopilot.AddWaypoint(new Sandbox.Game.Screens.Helpers.MyGps
            {
                Coords = targetWorld,
                Name   = "Destination",
            });
            if (flightAiComp != null) flightAiComp.IsActivated = true;
            if (taskAiComp  != null) taskAiComp.IsActivated  = true;
            missionAutopilot.IsSelected = true;
            return true;
        }

        // ---- prefab selection (catalogue preferred, config fallback) ----------------------

        private string PickPrefabName(string kind, string factionTag)
        {
            // Catalogue path: resolve the livery, query by role.
            if (_catalogue != null)
            {
                string factionType = null;
                if (_factionTypes != null)
                    _factionTypes.TryGetFactionType(factionTag, out factionType);
                // Personality is already this class's fallback vocabulary (the config path below is
                // keyed on it), so consulting it for the livery keeps one answer rather than two.
                string personalityId = ResolvePersonality(factionTag);
                string hint = PrefabFactionHint.Resolve(factionType, personalityId);
                IReadOnlyList<string> options = PrefabOwnershipFilter.Apply(
                    _catalogue.GetNamesForRole(kind, hint), _catalogue, factionTag);
                // The shipyard's rule: a mandatory-livery faction flies its own hull wherever the
                // role has one.
                if (PrefabFactionHint.LiveryIsMandatory(personalityId))
                {
                    List<string> own = PrefabRolePool.OwnLivery(options, _catalogue);
                    if (own.Count > 0) options = own;
                }
                if (options != null && options.Count > 0)
                    return options[new Random().Next(options.Count)];
            }

            // Legacy fallback: fleet_prefabs.json personality-keyed config.
            string personality = ResolvePersonality(factionTag);
            return GetPrefabName(kind, personality);
        }

        // ---- prefab config -----------------------------------------------------------------

        private void LoadConfig()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    string json = File.ReadAllText(_configPath);
                    _config = JsonConvert.DeserializeObject<FleetPrefabConfig>(json)
                              ?? new FleetPrefabConfig();
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "VanillaFleetSpawner: failed to load config from " + _configPath
                                 + " — using defaults.");
                }
            }
            _config = FleetPrefabConfig.Defaults();
            WriteDefaultConfig();
        }

        private void WriteDefaultConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath));
                File.WriteAllText(_configPath, JsonConvert.SerializeObject(_config, Formatting.Indented));
                Log.Info("VanillaFleetSpawner: wrote default config to " + _configPath);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "VanillaFleetSpawner: could not write default config.");
            }
        }

        private string GetPrefabName(string kind, string personality)
        {
            if (_config?.Kinds == null) return null;
            Dictionary<string, List<string>> byPersonality;
            if (!_config.Kinds.TryGetValue(kind, out byPersonality) || byPersonality == null)
                return null;
            List<string> names;
            // Try exact personality first, then fall back to "default".
            if (!byPersonality.TryGetValue(personality ?? "default", out names) || names == null || names.Count == 0)
                byPersonality.TryGetValue("default", out names);
            if (names == null || names.Count == 0) return null;
            string picked = names.Count == 1 ? names[0] : names[new Random().Next(0, names.Count)];
            return string.IsNullOrEmpty(picked) ? null : picked;
        }

        private string ResolvePersonality(string factionTag)
        {
            try
            {
                if (_store == null) return "default";
                foreach (IndexEntry e in _store.LoadIndex())
                    if (string.Equals(e.Tag, factionTag, StringComparison.OrdinalIgnoreCase))
                        return e.PersonalityId ?? "default";
            }
            catch { }
            return "default";
        }

        private static MyFaction FindFaction(long factionId, string tag)
        {
            if (MySession.Static?.Factions == null) return null;
            // MyFactionCollection is iterable as KeyValuePair<long, MyFaction> — the key is the faction ID.
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                if (factionId != 0 && kv.Key == factionId) return kv.Value;
                if (string.Equals(kv.Value.Tag, tag, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return null;
        }

        private static Vector3D KmToWorld(Vec3 km)
        {
            return new Vector3D(km.X * 1000.0, km.Y * 1000.0, km.Z * 1000.0);
        }
    }

    // ---- config DTO -------------------------------------------------------------------------

    /// <summary>
    /// Two-level JSON map: kind ("Trade"|"Raid"|"Patrol") → personality → list of prefab SubtypeIds.
    /// A "default" personality entry is used as a fallback when no per-personality entry matches.
    /// When a list has multiple entries one is chosen at random each spawn.
    /// Prefab SubtypeIds must match entries in SE's Prefabs folder or a modded equivalent.
    /// </summary>
    public sealed class FleetPrefabConfig
    {
        [JsonProperty("Kinds")]
        public Dictionary<string, Dictionary<string, List<string>>> Kinds { get; set; }
        [JsonProperty("BookValues", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, long> BookValues { get; set; }

        /// <summary>Returns all candidate prefab SubtypeIds for the given fleet kind and faction
        /// personality, falling back to the "default" bucket. Returns null if the kind is not
        /// configured or the matched list is empty. The caller (ShipyardManager) sorts by BOM cost
        /// and picks the cheapest affordable candidate.</summary>
        public IList<string> ListOptions(string kind, string personalityId)
        {
            if (Kinds == null) return null;
            Dictionary<string, List<string>> byPersonality;
            if (!Kinds.TryGetValue(kind, out byPersonality) || byPersonality == null) return null;
            List<string> options;
            if (personalityId == null
                || !byPersonality.TryGetValue(personalityId, out options)
                || options == null || options.Count == 0)
                byPersonality.TryGetValue("default", out options);
            if (options == null || options.Count == 0) return null;
            return options;
        }

        /// <summary>Picks a random prefab SubtypeId for the given fleet kind and faction personality.
        /// Falls back to the "default" bucket if no per-personality entry exists. Returns null if the
        /// kind is not configured or the matched list is empty.</summary>
        public string Pick(string kind, string personalityId, Random rng)
        {
            IList<string> options = ListOptions(kind, personalityId);
            if (options == null || options.Count == 0) return null;
            return options[rng.Next(options.Count)];
        }

        /// <summary>Enumerates every unique prefab SubtypeId referenced by the config.</summary>
        public IEnumerable<string> AllPrefabIds()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Kinds == null) return seen;
            foreach (var byPersonality in Kinds.Values)
            {
                if (byPersonality == null) continue;
                foreach (var options in byPersonality.Values)
                {
                    if (options == null) continue;
                    foreach (string prefabId in options)
                    {
                        if (string.IsNullOrEmpty(prefabId) || seen.Contains(prefabId)) continue;
                        seen.Add(prefabId);
                    }
                }
            }
            return seen;
        }

        /// <summary>Returns a config seeded with vanilla cargo-ship prefabs for Trade convoys.
        /// Raid and Patrol are left as TODO placeholders — they will log a warning and skip the spawn
        /// until real prefab names are configured.</summary>
        public static FleetPrefabConfig Defaults()
        {
            return new FleetPrefabConfig
            {
                Kinds = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase)
                {
                    // Trade convoys use vanilla faction-typed cargo ships (SpawnGroups_CargoShips.sbc).
                    // Prefab SubtypeIds are the *prefab* SubtypeId inside the SpawnGroup, NOT the
                    // SpawnGroup SubtypeId itself.  "default" catches any unmapped personality.
                    // Multiple entries are chosen at random each spawn.
                    ["Trade"] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        // Trader FactionType
                        ["mercantile"] = new List<string> { "C21_Trade_ConsignmentDrone", "C23_Trade_SolarBarge", "C20_Trade_PrivateSail", "C22_Trade_Merchant" },
                        // Builder FactionType
                        ["settler"]    = new List<string> { "C03_Builder_TugDrone", "C00_Builder_Hauler", "C01_Builder_MUP", "C02_Builder_MobileShipyard" },
                        // Miner FactionType (no personality yet — reserved for future use)
                        ["miner"]      = new List<string> { "C13_Mining_SurveyDrone", "C10_Mining_Carriage", "C11_Mining_Puller", "C12_Mining_Armed_Tender" },
                        // SpacePirates Faction
                        ["raider"]     = new List<string> { "C43_Pirate_ScoutDrone", "C40_Pirate_Vulture", "C41_Pirate_Carriage", "C42_Pirate_SalvageCarrier" },
                        // Factorum: progenitor AI never trades; merc=0 means brain won't enqueue Trade actions.
                        // Absent key falls to "default" as a safe backstop.
                        // Military FactionType + Unknown Faction fallback
                        ["default"]    = new List<string> { "C34_Military_Munitions_Drone", "C30_Military_Escort", "C32_Military_MissileFrigate", "C33_Military_Enforcer" },
                    },
                    ["Raid"] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mercantile"] = new List<string> { "DS_Trade_SolarDefender" },
                        ["settler"]    = new List<string> { "DS_Gunboat_B", "DS_Builder_HullBreaker" },
                        ["miner"]      = new List<string> { "DS_Gunboat", "DS_DemoDrone" },
                        ["raider"]     = new List<string> { "DS_Pirate_Bandit", "DS_Pirate_Bruiser_A", "DS_Pirate_Bruiser_R", "DA_BullyBot", "DA_Bastion", "DS_PirateCorsair", "RE09_PirateCutter" },
                        ["factorum"]   = new List<string> { "GE_FactorumWarshipC", "DS_AutomatedGunShip", "DS_M2_Barage" },
                        ["default"]    = new List<string> { "DS_TwinrailDrone", "DS_MilitaryHeavyDrone", "DS_Harbinger" },
                    },
                    ["Guard"] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mercantile"] = new List<string> { "DS_Trade_SolarDefender", "DS_Trade_SolarOrbiter" },
                        ["settler"]    = new List<string> { "DS_Builder_SentryDrone", "DS_Builder_CoilDrone" },
                        ["miner"]      = new List<string> { "DS_Mining_ForemanDrone", "DS_Mining_MiningDrone", "DS_Miner_DrifterDrone" },
                        ["raider"]     = new List<string> { "DS_Pirate_Scimitar", "DA_TwinTailedShiv", "DS_PerimeterDrone", "DS_PrivateerDrone" },
                        ["factorum"]   = new List<string> { "DS_Factorum_RailDrone", "HighProfileEscort", "DS_FactorumSpyDrone" },
                        ["default"]    = new List<string> { "DS_Harbinger", "DS_Adjudicator", "DS_Military_GunnerDrone" },
                    },
                    ["Patrol"] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mercantile"] = new List<string> { "DS_Trade_SolarDefender", "DS_Trade_SolarOrbiter" },
                        ["settler"]    = new List<string> { "DS_Builder_SentryDrone", "DS_Builder_CoilDrone" },
                        ["miner"]      = new List<string> { "DS_Mining_ForemanDrone", "DS_Mining_MiningDrone", "DS_Miner_DrifterDrone" },
                        ["raider"]     = new List<string> { "DS_Pirate_Scimitar", "DA_TwinTailedShiv", "DS_PerimeterDrone", "DS_PrivateerDrone" },
                        ["factorum"]   = new List<string> { "DS_Factorum_RailDrone", "HighProfileEscort", "DS_FactorumSpyDrone" },
                        ["default"]    = new List<string> { "DS_Harbinger", "DS_Adjudicator", "DS_Military_GunnerDrone" },
                    },
                }
            };
        }
    }
}
