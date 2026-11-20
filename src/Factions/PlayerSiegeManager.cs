using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;
using Factions.Core;
using Factions.Wire;

namespace Factions
{
    /// <summary>
    /// The host half of a player siege: counting fire a safe zone absorbs, switching the zone, and
    /// showing the player what their fire is doing.
    ///
    /// <para><b>Why absorbed fire has to be counted at all.</b> A Factions station's safe zone
    /// forbids Damage, Shooting and Grinding, so a player cannot scratch a protected station and no
    /// damage event is ever raised. Without this there is no way for a player to start a fight with
    /// any faction that owns one. See <see cref="PlayerSiege"/> for the state machine; this class
    /// only feeds it and acts on what it says.</para>
    ///
    /// <para><b>Two seams, because SE absorbs the two ammo classes differently.</b> A projectile
    /// flies in, hits the grid, and has its damage zeroed — it still raises a hit, which
    /// <see cref="OnProjectileHit"/> catches through the public interceptor API. A missile is
    /// destroyed at the zone boundary and never hits anything, so it needs
    /// <see cref="SafeZoneMissilePatches"/> to catch it on the way in. Covering only one would
    /// silently exclude half the weapons in the game.</para>
    ///
    /// <para><b>Damage is accumulated and flushed once per tick.</b> SE raises these per block per
    /// round; a burst of autocannon fire is hundreds of events a second, and calling into Core on
    /// each would put a dictionary write and a clock read on the hot path for no extra fidelity.</para>
    /// </summary>
    internal sealed class PlayerSiegeManager
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>Colour steps for the zone readout. A continuous update would broadcast to every
        /// client on every shot for a change nobody can see; ten bands are visible and cheap.</summary>
        internal const int ColorSteps = 10;

        /// <summary>How often the attacker's HUD status line is refreshed. Each one is a network
        /// message to one player; five seconds keeps a breach countdown readable at that cost.</summary>
        private const double StatusIntervalSeconds = 5.0;
        /// <summary>How often a panel-drawing client's item is rebuilt. Costs no network message
        /// unless what it shows moved (<see cref="StatusPanelPublisher"/>), and has to beat the
        /// publisher's staleness window or a running siege would blink off the panel.</summary>
        private const double PanelIntervalSeconds = 1.0;
        private const int StatusShowMs = 5200;
        private const int NoticeShowMs = 10000;
        private const string NoticeAuthor = "Factions";

        private readonly WorldModel _world;
        private readonly FactionTuning _tuning;
        private readonly StationMaterializer _stations;

        // nodeId -> absorbed damage awaiting the next flush (phase 1, against the zone).
        private readonly Dictionary<string, double> _pendingZoneDamage =
            new Dictionary<string, double>(StringComparer.Ordinal);
        // nodeId -> block damage awaiting the next flush (phase 2, against the station itself).
        private readonly Dictionary<string, double> _pendingBlockDamage =
            new Dictionary<string, double>(StringComparer.Ordinal);
        // (hull grid, nodeId) -> absorbed damage from a fleet grid with no player behind it, awaiting
        // the next flush, where the runtime says which raid it belongs to.
        private readonly Dictionary<(long grid, string node), double> _pendingRaidZoneDamage =
            new Dictionary<(long grid, string node), double>();
        private readonly Dictionary<(string node, string tag), double> _raidZoneScratch =
            new Dictionary<(string node, string tag), double>();
        // nodeId -> the NPC faction whose raid is besieging it, while no player's fire is counted there.
        private readonly Dictionary<string, string> _raidSiegeByNode =
            new Dictionary<string, string>(StringComparer.Ordinal);
        // nodeId -> identity of the player whose fire is being counted, for reputation and the offer.
        private readonly Dictionary<string, long> _attackerByNode =
            new Dictionary<string, long>(StringComparer.Ordinal);
        // nodeId -> last colour band pushed, so an unchanged band costs no broadcast.
        private readonly Dictionary<string, int> _lastColorBand =
            new Dictionary<string, int>(StringComparer.Ordinal);
        // nodeId -> when the attacker's HUD status line is next due.
        private readonly Dictionary<string, DateTime> _nextStatusUtc =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // nodeId -> who owned it when the player's siege or occupation was first seen, so a change of
        // hands under the attack is noticed (TickOwnerChanges).
        private readonly Dictionary<string, string> _ownerAtStart =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _nodeScratch = new List<string>();

        // Scratch for the presence sweep, reused so a per-tick pass allocates nothing.
        private readonly List<PresentPlayer> _players = new List<PresentPlayer>();
        private readonly HashSet<long> _holders = new HashSet<long>();
        // Nodes the materialised pass maintained this tick, so the unobserved sweep skips them.
        private readonly HashSet<string> _maintained = new HashSet<string>(StringComparer.Ordinal);
        private readonly ViewerRelations _relations = new ViewerRelations();

        private HitInterceptor _interceptor;
        private bool _registered;
        private Func<Vec3, double, bool> _anyPlayerWithinKm;
        // TEMPORARY (v0.154.2): bounded so a burst of autocannon fire cannot flood the log.
        private int _diagRemaining = 40;

        /// <summary>Set by the runtime. Credits absorbed fire from fleet hull <c>grid</c> at the zone
        /// of <c>node</c> to the raid dwelling there that the grid belongs to
        /// (<see cref="PendingFleetAction.FightZoneHits"/>) and answers its faction tag, or null when
        /// the grid is no such raid. Null: NPC fire is not counted.</summary>
        public Func<long, string, double, string> CreditRaidZoneHit { get; set; }

        /// <summary>Set by the runtime: whether a raid of faction <c>tag</c> is still on its dwell at
        /// <c>node</c>. What holds a breach a raid made, as a player's presence holds theirs.</summary>
        public Func<string, string, bool> RaidDwellingAt { get; set; }

        /// <summary>Set by the runtime: the HUD status panel. Null, or a player whose client does not
        /// draw it, falls back to the notification.</summary>
        public StatusPanelPublisher Panel { get; set; }

        /// <summary>Set by the runtime: told each step of a presence raid on an NPC faction's ground --
        /// its opening strip, every later strip, and its end -- so the owner can hail the occupier,
        /// charge them and remember it (<see cref="TrespassHailer.OnOccupation"/>). Null: the raid runs
        /// silent, as it did before.</summary>
        public Action<OccupationNotice> Occupied { get; set; }

        /// <summary>Set by the plugin on a Nexus authority: hands a notice for a player who is not on
        /// this server to the agent they are on (text, font, HUD ms or 0, whether to chat it).
        /// Null: such a player is told nothing.</summary>
        public Action<long, string, string, int, bool> RemoteNotice { get; set; }

        /// <summary>Set by the plugin on a Nexus authority: the players on agents, for presence
        /// raids on their ground. Null: only this server's players.</summary>
        public Func<IList<RemotePlayer>> RemotePlayers { get; set; }

        // nodeId -> whether the agent standing the station read it unpowered at its last flush.
        private readonly Dictionary<string, bool> _remotePoweredDown =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        // identity -> the faction tag an agent read for them, when this server cannot resolve one.
        private readonly Dictionary<long, string> _remoteTags = new Dictionary<long, string>();

        /// <summary>Fire into a station standing on a Nexus agent, counted as if it had been seen here.
        /// Reputation for absorbed fire is charged by the agent, which holds the grid.</summary>
        public void AddRemote(NexusSiegeEvent e)
        {
            if (e == null || e.Node == null || e.Attacker == 0) return;
            if (e.AttackerTag != null) _remoteTags[e.Attacker] = e.AttackerTag;
            if (e.Zone > 0) AccumulateZone(e.Node, e.Attacker, e.Zone);
            if (e.Block > 0)
            {
                double current;
                _pendingBlockDamage.TryGetValue(e.Node, out current);
                _pendingBlockDamage[e.Node] = current + e.Block;
                _attackerByNode[e.Node] = e.Attacker;
                _remotePoweredDown[e.Node] = e.PoweredDown;
            }
        }

        /// <summary>The owner and the occupier's faction tag of each presence raid in progress, by node,
        /// as they were when it began: the owner is what its end is reported against, and a node that
        /// changed hands no longer says who it was taken from.</summary>
        private readonly Dictionary<string, Occupier> _occupations = new Dictionary<string, Occupier>(StringComparer.Ordinal);

        private struct Occupier
        {
            public string OwnerTag;
            public string AttackerTag;
            public long AttackerIdentityId;
        }

        public PlayerSiegeManager(WorldModel world, FactionTuning tuning, StationMaterializer stations)
        {
            _world = world;
            _tuning = tuning;
            _stations = stations;
        }

        /// <summary>Subscribes to SE's projectile hit stream. Called after the session loads, for the
        /// same reason the fleet spawner defers its damage handler: the statics do not exist before.</summary>
        public void Register()
        {
            if (_registered) return;
            try
            {
                _interceptor = OnProjectileHit;
                MyProjectiles.Static.AddOnHitInterceptor(1000, _interceptor);
                SafeZoneMissilePatches.Sink = OnMissileAbsorbed;
                _registered = true;
                Log.Info("PlayerSiegeManager: watching absorbed fire on station safe zones.");
            }
            catch (Exception ex)
            {
                // Non-fatal by design: without this the siege simply never starts, which is the
                // pre-v0.152.0 behaviour. Taking the plugin down over it would be worse.
                Log.Warn(ex, "PlayerSiegeManager: could not register the projectile interceptor; "
                           + "player sieges will not be counted.");
            }
        }

        public void Unregister()
        {
            if (!_registered) return;
            try { MyProjectiles.Static.RemoveOnHitInterceptor(_interceptor); }
            catch (Exception ex) { Log.Warn(ex, "PlayerSiegeManager: interceptor removal threw."); }
            SafeZoneMissilePatches.Sink = null;
            _registered = false;
        }

        /// <summary>A projectile hit. The zone zeroes the damage of anything it absorbs but the hit
        /// still happens, so a zero-damage hit on one of our station grids from a round that carries
        /// real damage is exactly the signature of absorbed fire.</summary>
        private void OnProjectileHit(ref MyProjectileInfo projectile, ref MyProjectileHitInfo info)
        {
            try
            {
                // TEMPORARY DIAGNOSTIC (v0.154.2): the interceptor was silent live against
                // autocannon fire on a visibly-present safe zone, and every bail below was a quiet
                // return -- so there was no way to tell which one. Logs the first N hits with what
                // it actually saw. Remove once the cause is known.
                bool interesting = (info.HitEntity as MySafeZone) != null
                    || ((info.HitEntity as MyCubeGrid) != null
                        && _stations.NodeIdForStationGrid(((MyCubeGrid)info.HitEntity).EntityId) != null);
                if (_diagRemaining > 0 && interesting)
                {
                    _diagRemaining--;
                    var ammoDbg = projectile.ProjectileAmmoDefinition as MyProjectileAmmoDefinition;
                    Log.Info("SIEGE_DIAG hit"
                        + " dmg=" + info.Damage.ToString("0.##")
                        + " entity=" + (info.HitEntity == null ? "null" : info.HitEntity.GetType().Name)
                        + " name=" + (info.HitEntity == null ? "-" : (info.HitEntity.DisplayName ?? "-"))
                        + " ammo=" + (ammoDbg == null ? "null" : ammoDbg.Id.SubtypeName)
                        + " ammoDmg=" + (ammoDbg == null ? "-" : ammoDbg.ProjectileMassDamage.ToString("0.##"))
                        + " owner=" + projectile.OwningPlayer
                        + " asGrid=" + ((info.HitEntity as MyCubeGrid) == null ? "NO" : "yes")
                        + " asZone=" + ((info.HitEntity as MySafeZone) == null ? "NO" : "yes")
                        + " node=" + (((info.HitEntity as MyCubeGrid) == null) ? "-" :
                              (_stations.NodeIdForStationGrid(((MyCubeGrid)info.HitEntity).EntityId) ?? "NOT_A_STATION")));
                }

                MyEntity hit = info.HitEntity as MyEntity;
                if (hit == null) return;

                var ammo = projectile.ProjectileAmmoDefinition as MyProjectileAmmoDefinition;
                if (ammo == null || ammo.ProjectileMassDamage <= 0) return;

                long attacker = IdentityOfSteamId(projectile.OwningPlayer);
                // No player behind it. A round fired by one of our raid grids is that raid's siege;
                // anything else -- a meteor, debris -- is weather, and counting it would let a meteor
                // shower strip a station's protection with nobody present. Found live: meteor impacts
                // were accruing siege damage.
                if (attacker == 0)
                {
                    if (projectile.OwnerEntity != null)
                        AbsorbRaid(WingWorld.AttackerGridOf(projectile.OwnerEntity.EntityId), hit, info.Damage,
                                   ammo.ProjectileMassDamage);
                    return;
                }

                // A round fired at a protected station terminates on the SAFE ZONE, not on the hull
                // -- the zone is a physics entity and the projectile's raycast stops there, which is
                // exactly what a player sees as hits sparking on the bubble. So the zone IS the
                // absorption signal, and it identifies its node directly rather than through a grid
                // lookup. `Damage` is deliberately not consulted on this path: a hit on the zone is
                // absorbed by definition, whatever figure the hit carries.
                MySafeZone zone = hit as MySafeZone;
                if (zone != null)
                {
                    string zoneNode = NodeIdForSafeZone(zone.EntityId);
                    if (zoneNode != null) AccumulateZone(zoneNode, attacker, ammo.ProjectileMassDamage);
                    return;
                }

                // Hull hit with the damage zeroed -- the other absorption shape, kept because a
                // round that starts inside the bubble has no zone surface to stop at.
                if (info.Damage > 0) return;                  // it landed; not absorbed
                Absorb(hit, attacker, ammo.ProjectileMassDamage);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PlayerSiegeManager: projectile interceptor threw.");
            }
        }

        /// <summary>A missile destroyed on entering a safe zone, from the Harmony prefix. Unlike a
        /// projectile it never reaches a grid, so the zone entity is what identifies the target.</summary>
        private void OnMissileAbsorbed(long safeZoneEntityId, long attackerIdentityId, long launcherEntityId, double damage)
        {
            try
            {
                if (damage <= 0) return;
                string nodeId = NodeIdForSafeZone(safeZoneEntityId);
                if (nodeId == null) return;
                // A missile carries its owner's identity, and an NPC turret's owner is a real,
                // non-zero identity -- so whether a player is behind it has to be asked here. One that
                // is not is a raid's siege when its launcher sits on one of our raid grids.
                if (!FactionsPlugin.IsHumanIdentity(attackerIdentityId))
                {
                    AccumulateRaidZone(WingWorld.AttackerGridOf(launcherEntityId), nodeId, damage);
                    return;
                }
                AccumulateZone(nodeId, attackerIdentityId, damage);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PlayerSiegeManager: missile absorption sink threw.");
            }
        }

        private void Absorb(MyEntity hitEntity, long attackerIdentityId, double damage)
        {
            // A projectile hit reports the GRID, not a block -- MySlimBlock is not an entity at all,
            // so there is nothing to unwrap here.
            MyCubeGrid grid = hitEntity as MyCubeGrid;
            if (grid == null) return;
            string nodeId = _stations.NodeIdForStationGrid(grid.EntityId);
            if (nodeId == null) return;
            AccumulateZone(nodeId, attackerIdentityId, damage);
        }

        /// <summary>A round with no player behind it, absorbed at <paramref name="hitEntity"/>: on a
        /// station zone, or on a station hull with its damage zeroed. Counted against the grid that
        /// fired it, which the flush resolves to a raid or drops.</summary>
        private void AbsorbRaid(long firingGrid, MyEntity hitEntity, float hitDamage, double damage)
        {
            if (firingGrid == 0) return;
            MySafeZone zone = hitEntity as MySafeZone;
            string nodeId = null;
            if (zone != null) nodeId = NodeIdForSafeZone(zone.EntityId);
            else if (hitDamage <= 0)
            {
                MyCubeGrid grid = hitEntity as MyCubeGrid;
                if (grid != null) nodeId = _stations.NodeIdForStationGrid(grid.EntityId);
            }
            AccumulateRaidZone(firingGrid, nodeId, damage);
        }

        private void AccumulateRaidZone(long firingGrid, string nodeId, double damage)
        {
            if (firingGrid == 0 || nodeId == null || damage <= 0 || CreditRaidZoneHit == null) return;
            var key = (firingGrid, nodeId);
            double current;
            _pendingRaidZoneDamage.TryGetValue(key, out current);
            _pendingRaidZoneDamage[key] = current + damage;
        }

        private void AccumulateZone(string nodeId, long attackerIdentityId, double damage)
        {
            double current;
            _pendingZoneDamage.TryGetValue(nodeId, out current);
            _pendingZoneDamage[nodeId] = current + damage;
            if (attackerIdentityId != 0) _attackerByNode[nodeId] = attackerIdentityId;
        }

        /// <summary>Identity of the player whose fire is being counted against a node, or 0. The
        /// conquer offer asks THIS player rather than whoever happens to be standing there when the
        /// clock expires -- they are not necessarily the same person.</summary>
        public long AttackerFor(string nodeId)
        {
            long id;
            return nodeId != null && _attackerByNode.TryGetValue(nodeId, out id) ? id : 0L;
        }

        /// <summary>Block damage a player did to a station whose zone is already down. Called from
        /// the widened after-damage handler; phase 2 damage is real and arrives through SE's ordinary
        /// path rather than through either absorption seam.
        ///
        /// <para>The handler reports every attacker, NPC turrets included. Only a player's damage is
        /// a raid: an NPC faction whose own raid opened the zone would otherwise pass
        /// <see cref="PlayerSiege.MayAddRaidDamage"/> as "its players", and its ships' fire would be
        /// credited as a player raid.</para></summary>
        public void RecordBlockDamage(long gridEntityId, long attackerIdentityId, double damage)
        {
            if (damage <= 0) return;
            string nodeId = _stations.NodeIdForStationGrid(gridEntityId);
            if (nodeId == null) return;
            if (!FactionsPlugin.IsHumanIdentity(attackerIdentityId)) return;
            double current;
            _pendingBlockDamage.TryGetValue(nodeId, out current);
            _pendingBlockDamage[nodeId] = current + damage;
            if (attackerIdentityId != 0) _attackerByNode[nodeId] = attackerIdentityId;
        }

        /// <summary>Once per tick: charge accumulated damage, maintain every siege, and push the
        /// zone state and colour that Core's answer implies.</summary>
        public void Tick(Func<Vec3, double, bool> anyPlayerWithinKm)
        {
            try
            {
                _anyPlayerWithinKm = anyPlayerWithinKm;
                FlushZoneDamage();
                FlushRaidZoneDamage();
                FlushBlockDamage();
                MaintainAndApply();
                TickOwnerChanges();
                TickPresenceRaids();
                if (Panel != null) Panel.Flush(_world.UtcNow);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PlayerSiegeManager: tick threw.");
            }
        }

        private void FlushZoneDamage()
        {
            if (_pendingZoneDamage.Count == 0) return;
            foreach (var kv in _pendingZoneDamage)
            {
                SpatialNode n;
                if (!_world.Graph.Nodes.TryGetValue(kv.Key, out n)) continue;

                long attacker;
                _attackerByNode.TryGetValue(kv.Key, out attacker);
                string attackerFaction = FactionTagOf(attacker);

                // The owner shooting their own shield is not a siege, and costs no standing either.
                if (PlayerSiege.IsOwnersFire(n, attackerFaction)) continue;

                PlayerSiege.SiegeStep step = PlayerSiege.AbsorbZoneDamage(n, _tuning, _world.UtcNow, kv.Value,
                                                                          attackerFaction);

                // Reputation is charged 1:1 on what the zone absorbed -- the same call SE makes
                // itself when a round lands on a block (MySlimBlock's Damaging path). Shooting a
                // faction's station costs standing whether or not the shield stopped it.
                DamageReputation(attacker, kv.Key, kv.Value);
                _world.RegisterPlayerAggression(kv.Key, kv.Value);

                if (step.SiegeStarted)
                {
                    Log.Info("SIEGE_BEGAN node=" + kv.Key + " owner=" + (n.OwnerFactionTag ?? "?")
                             + " attacker=" + attacker + " pool=" + step.Integrity.ToString("0"));
                    Tell(attacker, PlayerSiegeNotice.Began(n, _tuning), "White");
                    _nextStatusUtc.Remove(kv.Key);
                }
                if (step.Breached)
                {
                    Log.Info("SIEGE_BREACHED node=" + kv.Key + " owner=" + (n.OwnerFactionTag ?? "?")
                             + " attacker=" + attacker);
                    Tell(attacker, PlayerSiegeNotice.Breached(n, _tuning), "Red");
                    _nextStatusUtc.Remove(kv.Key);
                }
            }
            _pendingZoneDamage.Clear();
        }

        /// <summary>Absorbed raid fire: credited to the raid that fired it, then charged to the zone
        /// through the same pool, dwell and breach a player's siege runs. No reputation and no
        /// player-aggression stamp: neither is a player's.</summary>
        private void FlushRaidZoneDamage()
        {
            if (_pendingRaidZoneDamage.Count == 0) return;
            Func<long, string, double, string> credit = CreditRaidZoneHit;
            _raidZoneScratch.Clear();
            foreach (var kv in _pendingRaidZoneDamage)
            {
                string tag = credit != null ? credit(kv.Key.grid, kv.Key.node, kv.Value) : null;
                if (tag == null) continue;
                var key = (kv.Key.node, tag);
                double current;
                _raidZoneScratch.TryGetValue(key, out current);
                _raidZoneScratch[key] = current + kv.Value;
            }
            _pendingRaidZoneDamage.Clear();

            foreach (var kv in _raidZoneScratch)
            {
                SpatialNode n;
                if (!_world.Graph.Nodes.TryGetValue(kv.Key.node, out n)) continue;
                if (PlayerSiege.IsOwnersFire(n, kv.Key.tag)) continue;
                PlayerSiege.SiegeStep step = PlayerSiege.AbsorbZoneDamage(n, _tuning, _world.UtcNow, kv.Value, kv.Key.tag);
                if (!_attackerByNode.ContainsKey(kv.Key.node)) _raidSiegeByNode[kv.Key.node] = kv.Key.tag;
                if (step.SiegeStarted)
                    Log.Info("SIEGE_BEGAN node=" + kv.Key.node + " owner=" + (n.OwnerFactionTag ?? "?")
                             + " raid=" + kv.Key.tag + " pool=" + step.Integrity.ToString("0"));
                if (step.Breached)
                    Log.Info("SIEGE_BREACHED node=" + kv.Key.node + " owner=" + (n.OwnerFactionTag ?? "?")
                             + " raid=" + kv.Key.tag);
            }
            _raidZoneScratch.Clear();
        }

        private void FlushBlockDamage()
        {
            if (_pendingBlockDamage.Count == 0) return;
            foreach (var kv in _pendingBlockDamage)
            {
                SpatialNode n;
                if (!_world.Graph.Nodes.TryGetValue(kv.Key, out n)) continue;

                long attacker;
                _attackerByNode.TryGetValue(kv.Key, out attacker);
                string attackerFaction = FactionTagOf(attacker);

                // The owner's own fire is never a raid, even through a breach.
                if (PlayerSiege.IsOwnersFire(n, attackerFaction)) continue;

                // Phase 2 fire is still a player attacking this node, and it is the only kind the
                // node takes once its zone is down -- so the aggression clock has to be stamped here
                // too. Stamped before the success test, which returns early on every flush that is
                // not the winning one and on every flush after it.
                //
                // Reputation is deliberately NOT charged here, unlike the zone path: phase 2 rounds
                // land on blocks, so SE has already charged standing through its own damage path.
                _world.RegisterPlayerAggression(kv.Key, kv.Value);

                // A zone a raid by the attacker's own faction took down is entered as a breach on
                // their first fire, so from here it is phase 2 of their raid. Core decides who
                // qualifies (PlayerSiege.MayAddRaidDamage); an unrelated player's fire is refused.
                if (PlayerSiege.EnterOpenedRaid(n, _tuning, _world.UtcNow, attackerFaction))
                {
                    Log.Info("SIEGE_BREACHED_BY_RAID node=" + kv.Key + " owner=" + (n.OwnerFactionTag ?? "?")
                             + " attacker=" + attacker + " faction=" + attackerFaction
                             + " windowLeftS=" + (n.SafeZoneDownUntilUtc - _world.UtcNow).TotalSeconds.ToString("0"));
                    Tell(attacker, PlayerSiegeNotice.Breached(n, _tuning), "Red");
                    _nextStatusUtc.Remove(kv.Key);
                }

                bool poweredDown = StationIsPoweredDown(kv.Key);
                bool firstSuccess = n.PlayerRaidSucceededUtc == default(DateTime);
                if (!PlayerSiege.RecordRaidDamage(n, _tuning, _world.UtcNow, kv.Value, poweredDown)) continue;
                if (firstSuccess) Tell(attacker, PlayerSiegeNotice.RaidSucceeded(n, _tuning), "Green");

                Log.Info("PLAYER_RAID_SUCCEEDED node=" + kv.Key + " owner=" + (n.OwnerFactionTag ?? "?")
                         + " attacker=" + attacker
                         + " blockDamage=" + n.PlayerRaidBlockDamage.ToString("0")
                         + (poweredDown ? " poweredDown=True" : ""));
                _world.ResolvePlayerRaid(kv.Key, attackerFaction);
            }
            _pendingBlockDamage.Clear();
        }

        private void MaintainAndApply()
        {
            _maintained.Clear();
            foreach (var kv in _stations.MaterializedSafeZones())
            {
                string nodeId = kv.Key;
                SpatialNode n;
                if (!_world.Graph.Nodes.TryGetValue(nodeId, out n)) continue;
                _maintained.Add(nodeId);

                bool attackerPresent = AttackerNear(n);
                if (PlayerSiege.Maintain(n, _tuning, _world.UtcNow, attackerPresent))
                {
                    _raidSiegeByNode.Remove(nodeId);
                    LogLapse(n, true);
                }
                else
                    ShowStatus(n);

                bool shouldBeOn = PlayerSiege.ZoneShouldBeEnabled(n, _world.UtcNow);
                double fraction = PlayerSiege.IntegrityFraction(n, _tuning, _world.UtcNow);

                // Invisible while untouched -- a healthy station should look exactly as it always
                // has. It lights up the moment it is hit and reddens as it weakens, which is the
                // only feedback a player gets that the siege is working.
                bool visible = shouldBeOn && fraction < 1.0;
                int band = (int)Math.Round(fraction * ColorSteps);
                int lastBand;
                bool bandMoved = !_lastColorBand.TryGetValue(nodeId, out lastBand) || lastBand != band;

                if (_stations.SetSafeZoneState(kv.Value, shouldBeOn, ColorFor(fraction), visible) || bandMoved)
                    _lastColorBand[nodeId] = band;
            }

            // Every other station siege. Maintain is the only thing that ends one, and a station
            // unloads when its attacker leaves -- so a siege whose station is not materialised has to
            // keep being maintained here, or it stands for as long as nobody comes back. There is no
            // zone entity to switch: the pass above applies the state once the station spawns again.
            foreach (SpatialNode n in _world.Graph.Nodes.Values)
            {
                if (!PlayerSiege.HasStationSiege(n) || _maintained.Contains(n.Id)) continue;
                if (PlayerSiege.Maintain(n, _tuning, _world.UtcNow, AttackerNear(n)))
                {
                    _raidSiegeByNode.Remove(n.Id);
                    LogLapse(n, false);
                }
            }
        }

        private void LogLapse(SpatialNode n, bool materialised)
        {
            Log.Info("SIEGE_LAPSED node=" + n.Id + " owner=" + (n.OwnerFactionTag ?? "?")
                     + " materialised=" + materialised);
            Tell(AttackerFor(n.Id), PlayerSiegeNotice.Lapsed(n), "Red");
            _nextStatusUtc.Remove(n.Id);
        }

        /// <summary>Refreshes the attacker's HUD status line for a siege whose station is
        /// materialised -- which already means a player is near it. Throttled per node; the line
        /// is shown slightly longer than the interval so it does not blink between refreshes.</summary>
        private void ShowStatus(SpatialNode n)
        {
            long attacker = AttackerFor(n.Id);
            if (attacker == 0) return;
            DateTime now = _world.UtcNow;
            DateTime due;
            if (_nextStatusUtc.TryGetValue(n.Id, out due) && now < due) return;

            if (DrawsPanel(attacker))
            {
                StatusPanelItem item = PlayerSiegeNotice.StatusPanel(n, _tuning, now);
                if (item == null) { _nextStatusUtc.Remove(n.Id); return; }
                _nextStatusUtc[n.Id] = now.AddSeconds(PanelIntervalSeconds);
                Panel.Set(attacker, item, now);
                return;
            }

            string status = PlayerSiegeNotice.Status(n, _tuning, now);
            if (status == null) { _nextStatusUtc.Remove(n.Id); return; }
            _nextStatusUtc[n.Id] = now.AddSeconds(StatusIntervalSeconds);
            if (!IsOnline(attacker)) { TellRemoteStatus(attacker, status); return; }
            MyVisualScriptLogicProvider.ShowNotification(status, StatusShowMs, "White", attacker);
        }

        private bool DrawsPanel(long identityId)
        {
            return Panel != null && identityId != 0 && Panel.Draws(identityId);
        }

        /// <summary>A siege transition, told to the attacker in chat, where it can be read back, and
        /// on the HUD unless their client draws the status panel: the panel already shows the new
        /// state, and a ten-second centre-screen paragraph on top of it is what it replaced. Nothing is
        /// sent to a player who is not online.</summary>
        private void Tell(long identityId, string text, string font)
        {
            if (identityId == 0 || string.IsNullOrEmpty(text)) return;
            if (!IsOnline(identityId))
            {
                Action<long, string, string, int, bool> remote = RemoteNotice;
                if (remote != null) remote(identityId, text, font, DrawsPanel(identityId) ? 0 : NoticeShowMs, true);
                return;
            }
            try
            {
                if (!DrawsPanel(identityId))
                    MyVisualScriptLogicProvider.ShowNotification(text, NoticeShowMs, font, identityId);
                MyVisualScriptLogicProvider.SendChatMessage(text, NoticeAuthor, identityId, font);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PlayerSiegeManager: notice to " + identityId + " threw.");
            }
        }

        private void TellRemoteStatus(long identityId, string status)
        {
            Action<long, string, string, int, bool> remote = RemoteNotice;
            if (remote != null) remote(identityId, status, "White", StatusShowMs, false);
        }

        private static bool IsOnline(long identityId)
        {
            return MySession.Static != null && MySession.Static.Players != null
                && MySession.Static.Players.TryGetPlayer(identityId) != null;
        }

        /// <summary>One online player, as the presence sweep needs them.</summary>
        private struct PresentPlayer
        {
            public long IdentityId;
            public long FactionId;    // 0 when they are in no SE faction
            public Vec3 PosKm;
        }

        /// <summary>A node that changes hands while a player is sieging or occupying it ends that
        /// attack. Core clears the raid with the ownership (<see cref="WorldModel.Withdraw(FactionState, string)"/>,
        /// claim and conquest all reset it), which leaves nothing on the node to say why the attack
        /// stopped -- so the owner at the start is remembered here and compared each tick.
        ///
        /// <para>An owner that abandons the node before the offer is due has given the attacker the
        /// ground: they are told so, and the claim offer an unclaimed node raises does the rest. A
        /// conquest by the attacker's own faction is the ordinary ending and is told by the offer
        /// flow, so it only clears the bookkeeping here.</para></summary>
        private void TickOwnerChanges()
        {
            foreach (KeyValuePair<string, long> kv in _attackerByNode)
            {
                if (_ownerAtStart.ContainsKey(kv.Key)) continue;
                SpatialNode n;
                if (!_world.Graph.Nodes.TryGetValue(kv.Key, out n)) continue;
                if (PlayerSiege.HasStationSiege(n) || PlayerSiege.IsPresenceRaid(n))
                    _ownerAtStart[kv.Key] = n.OwnerFactionTag;
            }
            if (_ownerAtStart.Count == 0) return;

            _nodeScratch.Clear();
            _nodeScratch.AddRange(_ownerAtStart.Keys);
            for (int i = 0; i < _nodeScratch.Count; i++)
            {
                string nodeId = _nodeScratch[i];
                string priorOwner = _ownerAtStart[nodeId];
                SpatialNode n;
                if (!_world.Graph.Nodes.TryGetValue(nodeId, out n)) { _ownerAtStart.Remove(nodeId); continue; }

                if (string.Equals(n.OwnerFactionTag, priorOwner, StringComparison.Ordinal))
                {
                    // Same owner: still running, or ended by its own path (lapse, a declined offer).
                    if (!PlayerSiege.HasStationSiege(n) && !PlayerSiege.IsPresenceRaid(n))
                    {
                        _ownerAtStart.Remove(nodeId);
                        EndOccupation(n, false);
                    }
                    continue;
                }

                long attacker = AttackerFor(nodeId);
                string why = NeutralFaction.IsClaimable(n.OwnerFactionTag) ? "abandoned"
                           : string.Equals(n.OwnerFactionTag, FactionTagOf(attacker), StringComparison.Ordinal) ? "taken"
                           : "changed_hands";
                Log.Info("SIEGE_ENDED node=" + nodeId + " owner=" + (priorOwner ?? "?")
                         + " now=" + (n.OwnerFactionTag ?? "?") + " attacker=" + attacker + " why=" + why);
                EndOccupation(n, why == "taken");
                if (why == "abandoned")
                    Tell(attacker, PlayerSiegeNotice.AbandonedUnderRaid(nodeId, priorOwner), "Green");

                _ownerAtStart.Remove(nodeId);
                _attackerByNode.Remove(nodeId);
                _nextStatusUtc.Remove(nodeId);
                _raidSiegeByNode.Remove(nodeId);
            }
        }

        /// <summary>Presence raids, which are how a player takes ground that carries no station.
        /// Everything above this point resolves its target through a materialised grid or safe zone,
        /// and an unfounded node has neither -- so there is nothing to besiege, nothing to shoot, and
        /// standing on it is the whole attack. See <see cref="PlayerSiege.BeginPresenceRaid"/>.
        ///
        /// <para>An attacker holds at most one node at a time, and it is the nearest one they
        /// qualified for. Several at once would each raise their own conquer offer, and a player
        /// answering "yes" has no way to say which one they meant.</para></summary>
        private void TickPresenceRaids()
        {
            if (_world == null || _world.Graph == null || _tuning == null) return;
            double startKm = _tuning.PresenceRaidRadiusKm;
            if (startKm <= 0) return;
            double holdKm = startKm + Math.Max(0.0, _tuning.PresenceRaidHysteresisKm);

            _players.Clear();
            CollectOnlinePlayers(_players);

            // Pass 1: raids already running, whether or not anyone is online. An empty player list is
            // what lapses a raid whose attacker logged off, and it is also what clears one left over
            // from before a restart -- the attacker map is in memory only, so a restarted server
            // cannot say who was holding and must not honour the raid.
            _holders.Clear();
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (!PlayerSiege.IsPresenceRaid(node)) continue;

                long attacker;
                _attackerByNode.TryGetValue(node.Id, out attacker);
                long holder = attacker != 0 ? HolderOf(node, attacker, holdKm * holdKm) : 0L;
                bool held = holder != 0;

                if (PlayerSiege.Maintain(node, _tuning, _world.UtcNow, held))
                {
                    _attackerByNode.Remove(node.Id);
                    _nextStatusUtc.Remove(node.Id);
                    Log.Info("PRESENCE_RAID_LAPSED node=" + node.Id
                             + " owner=" + (node.OwnerFactionTag ?? "?") + " attacker=" + attacker);
                    Tell(attacker, PlayerSiegeNotice.PresenceLapsed(node), "Red");
                    EndOccupation(node, false);
                    continue;
                }
                if (!held) continue;

                _holders.Add(attacker);
                // Nothing else stamps the defender's window on this path: it is carried by damage
                // flushes everywhere else, and there is no damage here.
                Occupier occupier;
                _occupations.TryGetValue(node.Id, out occupier);
                int strips = node.PresenceStrips;
                _world.MarkPlayerPresenceAttack(node.Id, occupier.AttackerTag);
                if (node.PresenceStrips > strips) Notify(node, occupier, holder, false, false);
                ShowPresenceStatus(node, holder);
            }

            if (_players.Count == 0) return;

            // Pass 2: one raid per attacker who is not already holding one.
            for (int i = 0; i < _players.Count; i++)
            {
                PresentPlayer p = _players[i];
                if (p.FactionId == 0) continue;              // no faction, nowhere to put a node
                if (_holders.Contains(p.IdentityId)) continue;

                SpatialNode target = NearestPresenceTarget(p, startKm * startKm);
                if (target == null) continue;
                var begun = new Occupier
                {
                    OwnerTag = target.OwnerFactionTag,
                    AttackerTag = FactionTagOf(p.IdentityId),
                    AttackerIdentityId = p.IdentityId,
                };
                if (!_world.BeginPresenceRaid(target.Id, begun.AttackerTag)) continue;

                _attackerByNode[target.Id] = p.IdentityId;
                _occupations[target.Id] = begun;
                _holders.Add(p.IdentityId);
                Tell(p.IdentityId, PlayerSiegeNotice.PresenceBegan(target, _tuning), "White");
                _nextStatusUtc.Remove(target.Id);
                Log.Info("PRESENCE_RAID_BEGAN node=" + target.Id
                         + " owner=" + (target.OwnerFactionTag ?? "?")
                         + " attacker=" + p.IdentityId
                         + " km=" + Math.Sqrt(p.PosKm.DistanceSquaredTo(target.Position)).ToString("0.##"));
                Notify(target, begun, p.IdentityId, false, false);
            }
        }

        /// <summary>Reports the end of the presence raid at <paramref name="n"/>, once, if one was
        /// running. <paramref name="taken"/>: the occupier's faction now owns the node.</summary>
        private void EndOccupation(SpatialNode n, bool taken)
        {
            Occupier occupier;
            if (n == null || !_occupations.TryGetValue(n.Id, out occupier)) return;
            _occupations.Remove(n.Id);
            Notify(n, occupier, occupier.AttackerIdentityId, true, taken);
        }

        private void Notify(SpatialNode n, Occupier occupier, long holder, bool ended, bool taken)
        {
            Action<OccupationNotice> sink = Occupied;
            if (sink == null || occupier.OwnerTag == null) return;
            try
            {
                sink(new OccupationNotice
                {
                    Node = n,
                    OwnerTag = occupier.OwnerTag,
                    AttackerTag = occupier.AttackerTag,
                    HolderIdentityId = holder,
                    Strip = n.PresenceStrips,
                    Ended = ended,
                    Taken = taken,
                });
            }
            catch (Exception ex) { Log.Warn(ex, "PlayerSiegeManager: occupation notice for " + n.Id + " threw."); }
        }

        /// <summary>The nearest unfounded, hostile, unraided node inside the start radius, or null.
        /// Hostility is asked only of nodes already close enough to matter, which is what keeps the
        /// relation lookup off a sweep across the whole graph.</summary>
        private SpatialNode NearestPresenceTarget(PresentPlayer p, double startKm2)
        {
            SpatialNode best = null;
            double bestDistance2 = double.MaxValue;
            _relations.Clear();     // relations are viewer-relative and this is a new viewer

            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (Economy.HasPhysicalStation(node)) continue;              // the siege path owns these
                if (node.OwnerFactionTag == null
                    || NeutralFaction.IsClaimable(node.OwnerFactionTag)) continue;
                if (node.PlayerRaidSucceededUtc != default(DateTime)) continue;   // already under a raid

                double distance2 = p.PosKm.DistanceSquaredTo(node.Position);
                if (distance2 > startKm2 || distance2 >= bestDistance2) continue;
                if (_relations.For(node.OwnerFactionTag, p.IdentityId) != WireRelation.Enemy) continue;

                best = node;
                bestDistance2 = distance2;
            }
            return best;
        }

        /// <summary>Who is holding the node: the attacker when they are inside the hold radius,
        /// otherwise the first of their faction who is, or 0 when nobody is. A faction-mate counts:
        /// the node is offered to the attacker's faction, and a squad relieving each other on station
        /// is holding it in every sense the mechanic means. The holder is who sees the status
        /// line.</summary>
        private long HolderOf(SpatialNode node, long attackerIdentityId, double holdKm2)
        {
            long attackerFactionId = FactionIdOf(attackerIdentityId);
            long mate = 0L;
            for (int i = 0; i < _players.Count; i++)
            {
                PresentPlayer p = _players[i];
                bool isAttacker = p.IdentityId == attackerIdentityId;
                if (!isAttacker && (attackerFactionId == 0 || p.FactionId != attackerFactionId)) continue;
                if (p.PosKm.DistanceSquaredTo(node.Position) > holdKm2) continue;
                if (isAttacker) return p.IdentityId;
                if (mate == 0L) mate = p.IdentityId;
            }
            return mate;
        }

        /// <summary>Refreshes the holder's HUD status line for a presence raid, on the same throttle
        /// as a station siege's. A presence raid is held by someone standing on the node, so the
        /// line goes to that person.</summary>
        private void ShowPresenceStatus(SpatialNode n, long holder)
        {
            if (holder == 0) return;
            DateTime now = _world.UtcNow;
            DateTime due;
            if (_nextStatusUtc.TryGetValue(n.Id, out due) && now < due) return;

            if (DrawsPanel(holder))
            {
                StatusPanelItem item = PlayerSiegeNotice.PresencePanel(n, _tuning, now);
                if (item == null) { _nextStatusUtc.Remove(n.Id); return; }
                _nextStatusUtc[n.Id] = now.AddSeconds(PanelIntervalSeconds);
                Panel.Set(holder, item, now);
                return;
            }

            string status = PlayerSiegeNotice.PresenceStatus(n, _tuning, now);
            if (status == null) { _nextStatusUtc.Remove(n.Id); return; }
            _nextStatusUtc[n.Id] = now.AddSeconds(StatusIntervalSeconds);
            if (!IsOnline(holder)) { TellRemoteStatus(holder, status); return; }
            MyVisualScriptLogicProvider.ShowNotification(status, StatusShowMs, "White", holder);
        }

        private void CollectOnlinePlayers(List<PresentPlayer> into)
        {
            if (MySession.Static == null || MySession.Static.Players == null) return;
            Func<IList<RemotePlayer>> remote = RemotePlayers;
            IList<RemotePlayer> others = remote != null ? remote() : null;
            if (others != null)
                for (int i = 0; i < others.Count; i++)
                {
                    RemotePlayer r = others[i];
                    if (r.IdentityId == 0) continue;
                    into.Add(new PresentPlayer
                    {
                        IdentityId = r.IdentityId,
                        FactionId = FactionIdOf(r.IdentityId),
                        PosKm = new Vec3(r.PositionM.X / 1000.0, r.PositionM.Y / 1000.0, r.PositionM.Z / 1000.0),
                    });
                }
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Identity == null) continue;
                var character = player.Character;
                if (character == null) continue;

                // SE world coordinates are metres; the node graph is kilometres.
                Vector3D pos = character.PositionComp.GetPosition();
                into.Add(new PresentPlayer
                {
                    IdentityId = player.Identity.IdentityId,
                    FactionId = FactionIdOf(player.Identity.IdentityId),
                    PosKm = new Vec3(pos.X / 1000.0, pos.Y / 1000.0, pos.Z / 1000.0),
                });
            }
        }

        /// <summary>The SE faction tag of <paramref name="identityId"/>, or null when they have none.
        /// NPC raids carry their actor's tag and a player faction is registered under its SE tag,
        /// so this is the tag a raid by the player's own faction was recorded under.</summary>
        private string FactionTagOf(long identityId)
        {
            if (identityId == 0) return null;
            string remote;
            try
            {
                MyFaction faction = MySession.Static.Factions.GetPlayerFaction(identityId);
                if (faction != null) return faction.Tag;
            }
            catch { }
            return _remoteTags.TryGetValue(identityId, out remote) ? remote : null;
        }

        private static long FactionIdOf(long identityId)
        {
            if (identityId == 0) return 0L;
            try
            {
                MyFaction faction = MySession.Static.Factions.GetPlayerFaction(identityId);
                return faction == null ? 0L : faction.FactionId;
            }
            catch { return 0L; }
        }

        /// <summary>Calm blue at full, through amber, to hot red as the pool empties.</summary>
        internal static Vector3 ColorFor(double fraction)
        {
            float f = (float)Math.Max(0.0, Math.Min(1.0, fraction));
            // Quantised to the same bands the broadcast gate uses, so the colour a client receives
            // and the band we remember having sent cannot disagree.
            f = (float)Math.Round(f * ColorSteps) / ColorSteps;
            return new Vector3(1f - f, 0.09f + 0.3f * f, 0.196f * f);
        }

        private bool AttackerNear(SpatialNode n)
        {
            // A siege only a raid's fire has fed is held by that raid still dwelling there. A player
            // standing at their own station must not hold open a breach an NPC made.
            string raidTag;
            if (!_attackerByNode.ContainsKey(n.Id) && _raidSiegeByNode.TryGetValue(n.Id, out raidTag))
                return RaidDwellingAt != null && RaidDwellingAt(n.Id, raidTag);
            // Presence sustains a breach. Reuses the observation radius rather than a new knob: it
            // is already "close enough for this node to be a live place", and a second radius that
            // meant almost the same thing would drift from it.
            return _anyPlayerWithinKm != null
                && _anyPlayerWithinKm(n.Position, _tuning.ObservationRadiusKm);
        }

        /// <summary>True when a station has no working power producer. Deliberately not SE's
        /// <c>MyResourceStateEnum.NoPower</c>: that carries transient overload states that would read
        /// as "powered down" during an ordinary brown-out, while "you killed the reactors" is what a
        /// player thinks they did and is what this should mean.</summary>
        private bool StationIsPoweredDown(string nodeId)
        {
            long gridId = _stations.StationGridEntityIdFor(nodeId);
            bool remote;
            if (gridId == 0) return _remotePoweredDown.TryGetValue(nodeId, out remote) && remote;
            return IsPoweredDown(gridId);
        }

        /// <summary>Whether a station grid has no working power producer. Also read by a Nexus agent,
        /// which holds the grid of a station whose siege runs on the authority.</summary>
        internal static bool IsPoweredDown(long gridId)
        {
            MyCubeGrid grid;
            if (gridId == 0 || !MyEntities.TryGetEntityById(gridId, out grid, false) || grid == null) return false;
            foreach (MyCubeBlock block in grid.GetFatBlocks())
            {
                var producer = block as IMyPowerProducer;
                if (producer != null && producer.Enabled && producer.IsWorking && producer.MaxOutput > 0)
                    return false;
            }
            return true;
        }

        private string NodeIdForSafeZone(long safeZoneEntityId)
        {
            foreach (var kv in _stations.MaterializedSafeZones())
                if (kv.Value == safeZoneEntityId) return kv.Key;
            return null;
        }

        /// <summary>Charges standing exactly as SE would have if the round had landed. Player-to-
        /// faction only: a solo attacker must not sour a whole faction's relations, per the scope
        /// note in docs/todo.md.</summary>
        private void DamageReputation(long attackerIdentityId, string nodeId, double damage)
        {
            if (attackerIdentityId == 0 || nodeId == null || damage <= 0) return;
            try
            {
                // SE charges standing against the OWNER IDENTITY of what was damaged and resolves
                // the faction itself (MySlimBlock's own Damaging call does exactly this), so take
                // the identity off the grid rather than substituting a founder id -- a faction whose
                // founder is not the station's owner would otherwise be charged the wrong way round.
                long gridId = _stations.StationGridEntityIdFor(nodeId);
                if (gridId == 0) return;
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById(gridId, out grid, false) || grid == null) return;
                if (grid.BigOwners == null || grid.BigOwners.Count == 0) return;

                MySession.Static.Factions.DamageFactionPlayerReputation(
                    attackerIdentityId, grid.BigOwners[0], MyReputationDamageType.Damaging, (float)damage);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PlayerSiegeManager: reputation charge threw for node " + nodeId + ".");
            }
        }

        internal static long IdentityOfSteamId(ulong steamId)
        {
            if (steamId == 0) return 0;
            try { return MySession.Static.Players.TryGetIdentityId(steamId); }
            catch { return 0; }
        }
    }

    /// <summary>One step of a player's occupation of a faction's unfounded node (a presence raid), as
    /// <see cref="PlayerSiegeManager.Occupied"/> reports it.</summary>
    internal struct OccupationNotice
    {
        public SpatialNode Node;
        /// <summary>The node's owner when the occupation began.</summary>
        public string OwnerTag;
        /// <summary>The occupier's SE faction tag, or null when they have none.</summary>
        public string AttackerTag;
        /// <summary>Who is on the node now: hailed and charged. The occupier, or a faction-mate holding
        /// it for them; the occupier on an end.</summary>
        public long HolderIdentityId;
        /// <summary>Hold strips charged so far, the opening one included.</summary>
        public int Strip;
        public bool Ended;
        /// <summary>On an end: the occupier's faction took the node.</summary>
        public bool Taken;
    }
}
