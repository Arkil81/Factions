using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Game;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// The grid half of a siege on a station that stands on a Nexus agent. The siege itself -- the
    /// integrity pool, the breach, the raid's success, the conquer offer -- runs on the authority
    /// (<see cref="PlayerSiegeManager"/>), which owns the node. This side catches a player's fire
    /// where the authority cannot see it, sends it there numbered, and applies what comes back: the
    /// zone's state and colour, and the notices for the player.
    ///
    /// <para>Captures what the authority's own manager captures for a player: rounds and missiles a
    /// station's zone absorbed, and block damage once it is down. NPC fire is not counted: the raid
    /// fleets that make it fly on the authority.</para>
    ///
    /// <para>Reputation for absorbed fire is charged here, against the grid this server holds; block
    /// damage costs standing through SE's own damage path, as it does on the authority.</para>
    /// </summary>
    internal sealed class AgentSiege
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const string NoticeAuthor = "Factions";

        private readonly StationMaterializer _stations;
        private HitInterceptor _interceptor;
        private bool _registered;
        private bool _damageHooked;
        private bool _closed;

        private struct Key : IEquatable<Key>
        {
            public string Node;
            public long Attacker;
            public bool Equals(Key o) { return o.Attacker == Attacker && string.Equals(o.Node, Node, StringComparison.Ordinal); }
            public override bool Equals(object o) { return o is Key && Equals((Key)o); }
            public override int GetHashCode() { return (Node ?? "").GetHashCode() ^ Attacker.GetHashCode(); }
        }

        private readonly Dictionary<Key, double> _zone = new Dictionary<Key, double>();
        private readonly Dictionary<Key, double> _block = new Dictionary<Key, double>();
        private readonly List<NexusSiegeEvent> _unacked = new List<NexusSiegeEvent>();
        private long _seq;

        private Dictionary<string, NexusZoneState> _zones = new Dictionary<string, NexusZoneState>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _band = new Dictionary<string, int>(StringComparer.Ordinal);
        private long _noticesThrough;
        // identity -> when the authority's offer to them was shown; their next yes or no is the answer.
        private readonly Dictionary<long, DateTime> _awaiting = new Dictionary<long, DateTime>();
        /// <summary>An offer unanswered this long stops claiming the player's yes and no.</summary>
        private static readonly TimeSpan AnswerWindow = TimeSpan.FromMinutes(30);

        public AgentSiege(StationMaterializer stations)
        {
            _stations = stations;
        }

        public int Unacked { get { return _unacked.Count; } }
        public int ZonesOff { get { return _zones.Count; } }
        public long NoticesThrough { get { return _noticesThrough; } }

        /// <summary>Subscribes to SE's projectile hits, missile absorption and block damage. Retried
        /// each frame until SE's statics are up.</summary>
        public void Register()
        {
            if (!_registered && MyProjectiles.Static != null)
            {
                try
                {
                    _interceptor = OnProjectileHit;
                    MyProjectiles.Static.AddOnHitInterceptor(1000, _interceptor);
                    SafeZoneMissilePatches.Sink = OnMissileAbsorbed;
                    _registered = true;
                    Log.Info("AgentSiege: watching absorbed fire on station safe zones.");
                }
                catch (Exception ex)
                {
                    _registered = true;     // not retried: a throw here is not a timing problem
                    Log.Warn(ex, "AgentSiege: could not register the projectile interceptor; sieges here will not be counted.");
                }
            }
            if (!_damageHooked && Sandbox.Game.GameSystems.MyDamageSystem.Static != null)
            {
                Sandbox.Game.GameSystems.MyDamageSystem.Static.RegisterAfterDamageHandler(1000, OnAfterDamage);
                _damageHooked = true;
            }
        }

        /// <summary>SE keeps no way to remove an after-damage handler; this one is inert once unregistered.</summary>
        public void Unregister()
        {
            if (_registered && _interceptor != null)
            {
                try { MyProjectiles.Static.RemoveOnHitInterceptor(_interceptor); }
                catch (Exception ex) { Log.Warn(ex, "AgentSiege: interceptor removal threw."); }
                SafeZoneMissilePatches.Sink = null;
            }
            _registered = false;
            _closed = true;
        }

        /// <summary>Whether the authority holds <paramref name="nodeId"/>'s zone down; null when it
        /// named no state for it, which is on and untouched.</summary>
        public bool? ZoneOff(string nodeId)
        {
            NexusZoneState z;
            return nodeId != null && _zones.TryGetValue(nodeId, out z) ? z.Off : (bool?)null;
        }

        private void OnProjectileHit(ref MyProjectileInfo projectile, ref MyProjectileHitInfo info)
        {
            if (_closed) return;
            try
            {
                MyEntity hit = info.HitEntity as MyEntity;
                if (hit == null) return;
                var ammo = projectile.ProjectileAmmoDefinition as Sandbox.Definitions.MyProjectileAmmoDefinition;
                if (ammo == null || ammo.ProjectileMassDamage <= 0) return;
                long attacker = PlayerSiegeManager.IdentityOfSteamId(projectile.OwningPlayer);
                if (attacker == 0) return;
                string node = null;
                MySafeZone zone = hit as MySafeZone;
                if (zone != null) node = NodeForZone(zone.EntityId);
                else if (info.Damage <= 0)
                {
                    MyCubeGrid grid = hit as MyCubeGrid;
                    if (grid != null) node = _stations.NodeIdForStationGrid(grid.EntityId);
                }
                if (node != null) Add(_zone, node, attacker, ammo.ProjectileMassDamage);
            }
            catch (Exception ex) { Log.Warn(ex, "AgentSiege: projectile interceptor threw."); }
        }

        private void OnMissileAbsorbed(long safeZoneEntityId, long attackerIdentityId, long launcherEntityId, double damage)
        {
            if (damage <= 0 || !FactionsPlugin.IsHumanIdentity(attackerIdentityId)) return;
            string node = NodeForZone(safeZoneEntityId);
            if (node != null) Add(_zone, node, attackerIdentityId, damage);
        }

        private void OnAfterDamage(object target, MyDamageInformation info)
        {
            if (_closed || info.Amount <= 0) return;
            try
            {
                var block = target as Sandbox.Game.Entities.Cube.MySlimBlock;
                if (block == null || block.CubeGrid == null) return;
                string node = _stations.NodeIdForStationGrid(block.CubeGrid.EntityId);
                if (node == null) return;
                long attacker = VanillaFleetSpawner.AttackerIdentityOf(info);
                if (!FactionsPlugin.IsHumanIdentity(attacker)) return;
                Add(_block, node, attacker, info.Amount);
            }
            catch { /* a throwing handler must not break SE's damage pipeline */ }
        }

        private static void Add(Dictionary<Key, double> into, string node, long attacker, double damage)
        {
            var k = new Key { Node = node, Attacker = attacker };
            double current;
            into.TryGetValue(k, out current);
            into[k] = current + damage;
        }

        private string NodeForZone(long safeZoneEntityId)
        {
            foreach (var kv in _stations.MaterializedSafeZones())
                if (kv.Value == safeZoneEntityId) return kv.Key;
            return null;
        }

        /// <summary>Turns the fire caught since the last call into numbered events, charges the
        /// reputation of the absorbed part, and returns every event not yet acknowledged.</summary>
        public List<NexusSiegeEvent> Flush()
        {
            if (_zone.Count > 0 || _block.Count > 0)
            {
                var keys = new HashSet<Key>(_zone.Keys);
                keys.UnionWith(_block.Keys);
                foreach (Key k in keys)
                {
                    double zone, blockDamage;
                    _zone.TryGetValue(k, out zone);
                    _block.TryGetValue(k, out blockDamage);
                    long gridId = _stations.StationGridEntityIdFor(k.Node);
                    if (zone > 0) ChargeReputation(k.Attacker, gridId, zone);
                    _unacked.Add(new NexusSiegeEvent
                    {
                        Seq = ++_seq, Node = k.Node, Attacker = k.Attacker, AttackerTag = TagOf(k.Attacker),
                        Zone = zone, Block = blockDamage,
                        PoweredDown = blockDamage > 0 && PlayerSiegeManager.IsPoweredDown(gridId),
                    });
                }
                _zone.Clear();
                _block.Clear();
            }
            return _unacked.Count > 0 ? new List<NexusSiegeEvent>(_unacked) : null;
        }

        /// <summary>The authority's answer: drops what it applied, takes the zone states it holds,
        /// and shows the notices not shown yet.</summary>
        public void Apply(NexusStationReply reply)
        {
            if (reply == null) return;
            if (reply.SiegeThrough > 0) _unacked.RemoveAll(e => e.Seq <= reply.SiegeThrough);
            var zones = new Dictionary<string, NexusZoneState>(StringComparer.Ordinal);
            if (reply.Zones != null)
                foreach (NexusZoneState z in reply.Zones)
                    if (z != null && z.Node != null) zones[z.Node] = z;
            _zones = zones;
            if (reply.Notices != null)
                foreach (NexusNotice n in reply.Notices)
                {
                    if (n == null || n.Seq <= _noticesThrough) continue;
                    _noticesThrough = n.Seq;
                    if (n.Offer && n.Identity != 0) _awaiting[n.Identity] = DateTime.UtcNow;
                    Show(n);
                }
        }

        /// <summary>A yes or no typed here. True, and queued for the authority, when it answers an offer
        /// the authority made this player; the chat line is then not shown to anyone.</summary>
        public bool TryAnswer(long identity, bool yes, string nodeId)
        {
            DateTime shown;
            if (identity == 0 || !_awaiting.TryGetValue(identity, out shown)) return false;
            _awaiting.Remove(identity);
            if (DateTime.UtcNow - shown > AnswerWindow) return false;
            _unacked.Add(new NexusSiegeEvent
            {
                Seq = ++_seq, Attacker = identity, Answer = yes ? NexusAnswer.Yes : NexusAnswer.No, Node = nodeId,
            });
            return true;
        }

        /// <summary>Per frame: every standing station's zone as the authority last said, the default
        /// (on, untouched) for any it did not name.</summary>
        public void ApplyZones()
        {
            foreach (var kv in _stations.MaterializedSafeZones())
            {
                NexusZoneState z;
                bool on = true;
                double fraction = 1.0;
                if (_zones.TryGetValue(kv.Key, out z)) { on = !z.Off; fraction = z.Fraction; }
                bool visible = on && fraction < 1.0;
                int band = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, fraction)) * PlayerSiegeManager.ColorSteps);
                int last;
                bool bandMoved = !_band.TryGetValue(kv.Key, out last) || last != band;
                if (_stations.SetSafeZoneState(kv.Value, on, PlayerSiegeManager.ColorFor(fraction), visible) || bandMoved)
                    _band[kv.Key] = band;
            }
        }

        /// <summary>Puts a notice's combat line on the player's FactionsUI HUD: identity, tone, text.</summary>
        internal static Action<long, CombatTone, string> CombatSink;

        private static void Show(NexusNotice n)
        {
            if (n.Identity == 0 || string.IsNullOrEmpty(n.Text)) return;
            if (MySession.Static == null || MySession.Static.Players == null
                || MySession.Static.Players.TryGetPlayer(n.Identity) == null) return;
            try
            {
                string font = n.Font ?? "White";
                if (n.HudMs > 0) MyVisualScriptLogicProvider.ShowNotification(n.Text, n.HudMs, font, n.Identity);
                if (n.Chat) MyVisualScriptLogicProvider.SendChatMessage(n.Text, n.Author ?? NoticeAuthor, n.Identity, font);
                Action<long, CombatTone, string> combat = CombatSink;
                if (n.Combat > 0 && combat != null) combat(n.Identity, (CombatTone)(n.Combat - 1), n.Text);
            }
            catch (Exception ex) { Log.Warn(ex, "AgentSiege: notice to " + n.Identity + " threw."); }
        }

        private static void ChargeReputation(long attacker, long gridId, double damage)
        {
            try
            {
                MyCubeGrid grid;
                if (gridId == 0 || !MyEntities.TryGetEntityById(gridId, out grid, false) || grid == null) return;
                if (grid.BigOwners == null || grid.BigOwners.Count == 0) return;
                MySession.Static.Factions.DamageFactionPlayerReputation(
                    attacker, grid.BigOwners[0], MyReputationDamageType.Damaging, (float)damage);
            }
            catch (Exception ex) { Log.Warn(ex, "AgentSiege: reputation charge threw."); }
        }

        private static string TagOf(long identity)
        {
            try
            {
                var f = MySession.Static.Factions.GetPlayerFaction(identity);
                return f == null ? null : f.Tag;
            }
            catch { return null; }
        }
    }
}
