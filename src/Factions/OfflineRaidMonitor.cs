using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// Offline-raid and combat-log detection (FAC-534). Records and warns; it changes no damage.
    ///
    /// <para>Protected is a grid whose majority owner is a human identity, that is neither a node's
    /// station nor a sim hull, and whose owner's faction (or the owner alone, without one) has had no
    /// human member online for <see cref="FactionTuning.OfflineRaidGraceMinutes"/>. A grid owned by an
    /// NPC identity has no protection, a player faction's own NPC member included: it has agency.</para>
    ///
    /// <para>The damage handler only adds the hit to a per (attacking entity, grid) total, so a fight
    /// costs one entry per pair however many rounds land. Judging, logging and notices run on
    /// <see cref="Tick"/>, on the game thread, with the grid and owner judgements cached.</para>
    /// </summary>
    internal sealed class OfflineRaidMonitor
    {
        private struct HitKey : IEquatable<HitKey>
        {
            public long Attacker;
            public long GridEntityId;
            public bool Equals(HitKey o) { return Attacker == o.Attacker && GridEntityId == o.GridEntityId; }
            public override bool Equals(object obj) { return obj is HitKey && Equals((HitKey)obj); }
            public override int GetHashCode() { return Attacker.GetHashCode() * 397 ^ GridEntityId.GetHashCode(); }
        }

        /// <summary>Hits from one attacking entity on one grid since the last tick.</summary>
        private sealed class HitTotal
        {
            public double Amount;
            public int Destroyed;
            public string Type;
        }

        private struct Hit
        {
            public long Attacker;
            public long GridEntityId;
            public double Amount;
            public int Destroyed;
            public string Type;
        }

        private struct GridJudgement
        {
            public bool Protected;
            public long Owner;
            public string VictimKey;
            public string VictimTag;
            public DateTime AtUtc;
        }

        private struct VictimState
        {
            public bool Online;
            public DateTime OfflineSinceUtc;
            public bool LastSeenKnown;
            public long LastMember;
            public DateTime AtUtc;
        }

        private static readonly TimeSpan GridJudgementTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan VictimStateTtl = TimeSpan.FromSeconds(5);
        /// <summary>A remote player's last-seen is refreshed every 10 s by the player index; three
        /// refreshes missed is offline.</summary>
        private static readonly TimeSpan OnlineSlack = TimeSpan.FromSeconds(30);
        private const int WarnShowMs = 10000;
        private const string NoticeAuthor = "Factions";

        private readonly FactionRuntime _runtime;
        private readonly PlayerIndexWriter _index;
        private readonly Action<string> _log;
        private readonly ConcurrentDictionary<HitKey, HitTotal> _pending = new ConcurrentDictionary<HitKey, HitTotal>();
        private readonly List<HitKey> _drain = new List<HitKey>();
        private readonly OfflineRaidLedger _ledger = new OfflineRaidLedger();
        private readonly Dictionary<long, GridJudgement> _grids = new Dictionary<long, GridJudgement>();
        private readonly Dictionary<string, VictimState> _victims = new Dictionary<string, VictimState>(StringComparer.Ordinal);
        private readonly List<OfflineRaidSummary> _ended = new List<OfflineRaidSummary>();
        private readonly List<string> _keys = new List<string>();
        private readonly List<long> _staleGrids = new List<long>();
        private MyDamageSystem _damageSystem;
        private DateTime _nextSweepUtc;
        private bool _closed;

        public OfflineRaidMonitor(FactionRuntime runtime, PlayerIndexWriter index, Action<string> log)
        {
            _runtime = runtime;
            _index = index;
            _log = log;
        }

        public void Close() { _closed = true; }

        private FactionTuning Tuning { get { return _runtime != null && _runtime.World != null ? _runtime.World.Tuning : null; } }

        /// <summary>Whether damage to a grid this identity owns would be an offline raid now: a human
        /// owner whose faction (or who, without one) has been wholly offline past the grace. The
        /// scanner marks such a grid <c>owner offline</c> (FAC-533).</summary>
        public bool IsProtectedOwner(long ownerIdentityId)
        {
            if (_closed || ownerIdentityId == 0 || MySession.Static == null || MySession.Static.Players == null) return false;
            FactionTuning t = Tuning;
            if (t == null || !FactionsPlugin.IsHumanIdentity(ownerIdentityId)) return false;
            DateTime now = DateTime.UtcNow;
            string tag = FactionTagOf(ownerIdentityId);
            var grid = new GridJudgement
            {
                Protected = true, Owner = ownerIdentityId, VictimTag = tag,
                VictimKey = tag ?? "id:" + ownerIdentityId.ToString(CultureInfo.InvariantCulture),
            };
            VictimState s = StateOf(grid, now);
            DateTime? since = s.Online ? (DateTime?)null : s.OfflineSinceUtc;
            return OfflineRaidLedger.Classify(since, now, TimeSpan.FromMinutes(Math.Max(0.0, t.OfflineRaidGraceMinutes)))
                   == OfflineState.Offline;
        }

        private void EnsureHandler()
        {
            MyDamageSystem system = MyDamageSystem.Static;
            if (system == null || ReferenceEquals(system, _damageSystem)) return;
            system.RegisterAfterDamageHandler(1000, OnAfterDamage);
            _damageSystem = system;
        }

        private void OnAfterDamage(object target, MyDamageInformation info)
        {
            if (_closed || info.Amount <= 0 || info.AttackerId == 0) return;
            try
            {
                var block = target as MySlimBlock;
                if (block == null || block.CubeGrid == null) return;
                var key = new HitKey { Attacker = info.AttackerId, GridEntityId = block.CubeGrid.EntityId };
                HitTotal total = _pending.GetOrAdd(key, k => new HitTotal());
                lock (total)
                {
                    total.Amount += info.Amount;
                    if (block.IsDestroyed) total.Destroyed++;
                    if (total.Type == null) total.Type = info.Type.String;
                }
            }
            catch { /* a throwing handler must not break SE's damage pipeline */ }
        }

        /// <summary>On the plugin's player cadence.</summary>
        public void Tick()
        {
            if (_closed || MySession.Static == null || MySession.Static.Players == null) return;
            FactionTuning t = Tuning;
            if (t == null) return;
            EnsureHandler();
            DateTime now = DateTime.UtcNow;

            _drain.Clear();
            foreach (KeyValuePair<HitKey, HitTotal> p in _pending) _drain.Add(p.Key);
            for (int i = 0; i < _drain.Count; i++)
            {
                HitTotal total;
                if (!_pending.TryRemove(_drain[i], out total)) continue;
                Hit hit;
                lock (total)
                {
                    hit = new Hit
                    {
                        Attacker = _drain[i].Attacker, GridEntityId = _drain[i].GridEntityId,
                        Amount = total.Amount, Destroyed = total.Destroyed, Type = total.Type,
                    };
                }
                Judge(hit, t, now);
            }

            if (now < _nextSweepUtc) return;
            _nextSweepUtc = now.AddSeconds(5);
            Sweep(t, now);
        }

        private void Judge(Hit hit, FactionTuning t, DateTime now)
        {
            long attacker = CrewIntruders.AttackerIdentity(hit.Attacker);
            if (attacker == 0 || !FactionsPlugin.IsHumanIdentity(attacker)) return;
            GridJudgement grid = JudgementOf(hit.GridEntityId, now);
            if (!grid.Protected || grid.Owner == attacker) return;
            if (grid.VictimTag != null && string.Equals(grid.VictimTag, FactionTagOf(attacker), StringComparison.Ordinal)) return;

            VictimState victim = StateOf(grid, now);
            DateTime? since = victim.Online ? (DateTime?)null : victim.OfflineSinceUtc;
            OfflineState state = OfflineRaidLedger.Classify(since, now, TimeSpan.FromMinutes(Math.Max(0.0, t.OfflineRaidGraceMinutes)));
            OfflineRaidVerdict v = _ledger.Hit(attacker, grid.Owner, grid.VictimKey, hit.GridEntityId, hit.Amount,
                                               hit.Destroyed, state, now, t);
            if (v.Began) OnBegan(attacker, grid, victim, hit, now);
            if (v.WarnAttacker) WarnAttacker(attacker, hit.GridEntityId);
        }

        private void OnBegan(long attacker, GridJudgement grid, VictimState victim, Hit hit, DateTime now)
        {
            MyCubeGrid g = MyEntities.GetEntityById(hit.GridEntityId) as MyCubeGrid;
            string gridName = g != null ? g.DisplayName : null;
            var sb = new StringBuilder("OFFLINE_RAID attacker=").Append(attacker)
                .Append(" attacker_name=").Append(Quote(NameOf(attacker)))
                .Append(" attacker_tag=").Append(FactionTagOf(attacker) ?? "-")
                .Append(" victim=").Append(grid.Owner)
                .Append(" victim_name=").Append(Quote(NameOf(grid.Owner)))
                .Append(" victim_tag=").Append(grid.VictimTag ?? "-")
                .Append(" grid=").Append(hit.GridEntityId)
                .Append(" grid_name=").Append(Quote(gridName))
                .Append(" type=").Append(hit.Type ?? "?")
                .Append(" offline_min=").Append(victim.LastSeenKnown
                    ? (now - victim.OfflineSinceUtc).TotalMinutes.ToString("0", CultureInfo.InvariantCulture) : "unknown");
            if (g != null)
            {
                VRageMath.Vector3D p = g.PositionComp.GetPosition();
                sb.Append(" pos_km=").Append((p.X / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                  .Append((p.Y / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                  .Append((p.Z / 1000.0).ToString("0.0", CultureInfo.InvariantCulture));
            }
            Write(sb.ToString());
            TellAdmins("Offline raid: " + (NameOf(attacker) ?? attacker.ToString(CultureInfo.InvariantCulture))
                       + " is attacking " + (gridName ?? "a grid") + " of "
                       + (grid.VictimTag ?? NameOf(grid.Owner) ?? "an offline player") + ", whose members are all offline.");
        }

        private void WarnAttacker(long attacker, long gridEntityId)
        {
            if (MySession.Static.Players.TryGetPlayer(attacker) == null) return;
            try
            {
                MyVisualScriptLogicProvider.ShowNotification(
                    "The owners of this grid are offline. Attacking an offline faction is against the server rules. This has been logged.",
                    WarnShowMs, "Red", attacker);
            }
            catch { }
        }

        private void Sweep(FactionTuning t, DateTime now)
        {
            _ended.Clear();
            _ledger.TakeEnded(now, t, _ended);
            for (int i = 0; i < _ended.Count; i++)
            {
                OfflineRaidSummary s = _ended[i];
                Write("OFFLINE_RAID_END attacker=" + s.Attacker + " attacker_name=" + Quote(NameOf(s.Attacker))
                      + " victim=" + s.Victim + " victim_key=" + s.VictimKey
                      + " grids=" + s.Grids + " blocks_destroyed=" + s.BlocksDestroyed
                      + " damage=" + s.Damage.ToString("0", CultureInfo.InvariantCulture)
                      + " minutes=" + (s.LastHitUtc - s.StartedUtc).TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture));
            }

            _ledger.KeysUnderFire(_keys);
            for (int i = 0; i < _keys.Count; i++)
            {
                VictimState state;
                if (!TryFreshState(_keys[i], now, out state)) continue;
                CombatLogRecord record;
                if (!_ledger.ObserveOnline(_keys[i], state.Online, now, t, out record)) continue;
                Write("COMBAT_LOG victim_key=" + record.VictimKey
                      + " member=" + state.LastMember + " member_name=" + Quote(NameOf(state.LastMember))
                      + " attacker=" + record.Attacker + " attacker_name=" + Quote(NameOf(record.Attacker))
                      + " grid=" + record.GridEntityId
                      + " since_hit_s=" + (now - record.LastHostileUtc).TotalSeconds.ToString("0", CultureInfo.InvariantCulture));
                TellAdmins("Combat log: " + (NameOf(state.LastMember) ?? "a player") + " logged off while "
                           + (NameOf(record.Attacker) ?? "a player") + " was attacking their grids. Their faction is now offline.");
            }

            _staleGrids.Clear();
            foreach (KeyValuePair<long, GridJudgement> p in _grids)
                if (now - p.Value.AtUtc >= GridJudgementTtl) _staleGrids.Add(p.Key);
            for (int i = 0; i < _staleGrids.Count; i++) _grids.Remove(_staleGrids[i]);
        }

        private GridJudgement JudgementOf(long gridEntityId, DateTime now)
        {
            GridJudgement j;
            if (_grids.TryGetValue(gridEntityId, out j) && now - j.AtUtc < GridJudgementTtl) return j;
            j = new GridJudgement { AtUtc = now };
            MyCubeGrid grid = MyEntities.GetEntityById(gridEntityId) as MyCubeGrid;
            if (grid != null && !grid.MarkedForClose)
            {
                long owner = grid.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0L;
                bool sim = SimGridMarks.IsMarked(gridEntityId) || NpcGridNaming.IsSimBornName(grid.Name, gridEntityId);
                if (owner != 0 && !sim && FactionsPlugin.IsHumanIdentity(owner)
                    && (_runtime == null || !_runtime.IsStationGrid(gridEntityId)))
                {
                    j.Protected = true;
                    j.Owner = owner;
                    j.VictimTag = FactionTagOf(owner);
                    j.VictimKey = j.VictimTag ?? "id:" + owner.ToString(CultureInfo.InvariantCulture);
                }
            }
            _grids[gridEntityId] = j;
            return j;
        }

        private VictimState StateOf(GridJudgement grid, DateTime now)
        {
            VictimState s;
            if (_victims.TryGetValue(grid.VictimKey, out s) && now - s.AtUtc < VictimStateTtl) return s;
            s = Measure(grid.VictimTag, grid.Owner, now);
            _victims[grid.VictimKey] = s;
            return s;
        }

        /// <summary>The state for a key the ledger names; false when the key was never judged here.</summary>
        private bool TryFreshState(string key, DateTime now, out VictimState state)
        {
            if (!_victims.TryGetValue(key, out state)) return false;
            if (now - state.AtUtc < VictimStateTtl) return true;
            string tag = key.StartsWith("id:", StringComparison.Ordinal) ? null : key;
            long owner = 0;
            if (tag == null) long.TryParse(key.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out owner);
            state = Measure(tag, owner, now);
            _victims[key] = state;
            return true;
        }

        /// <summary>Online when any human member was seen within <see cref="OnlineSlack"/>; otherwise
        /// offline since the most recent member's last-seen. A faction none of whose members the
        /// player index knows reads as offline since <see cref="DateTime.MinValue"/>.</summary>
        private VictimState Measure(string tag, long owner, DateTime now)
        {
            var s = new VictimState { AtUtc = now, OfflineSinceUtc = DateTime.MinValue };
            MyFaction faction = tag != null && MySession.Static.Factions != null
                ? MySession.Static.Factions.TryGetFactionByTag(tag) as MyFaction : null;
            if (faction != null)
            {
                foreach (KeyValuePair<long, MyFactionMember> m in faction.Members)
                    Observe(m.Key, now, ref s);
            }
            else if (owner != 0) Observe(owner, now, ref s);
            return s;
        }

        private void Observe(long identityId, DateTime now, ref VictimState s)
        {
            if (!FactionsPlugin.IsHumanIdentity(identityId)) return;
            DateTime seen;
            if (_index == null || !_index.TryGetLastSeen(identityId, now, out seen)) return;
            if (now - seen < OnlineSlack) s.Online = true;
            if (!s.LastSeenKnown || seen > s.OfflineSinceUtc)
            {
                s.OfflineSinceUtc = seen;
                s.LastMember = identityId;
                s.LastSeenKnown = true;
            }
        }

        private void TellAdmins(string text)
        {
            foreach (MyPlayer p in MySession.Static.Players.GetOnlinePlayers())
            {
                if (p == null || p.IsBot || p.Identity == null) continue;
                if (MySession.Static.GetUserPromoteLevel(p.Id.SteamId) < MyPromoteLevel.Admin) continue;
                try { MyVisualScriptLogicProvider.SendChatMessage(text, NoticeAuthor, p.Identity.IdentityId, "Red"); }
                catch { }
            }
        }

        private void Write(string line)
        {
            if (_log != null) _log(line);
        }

        private static string FactionTagOf(long identityId)
        {
            if (MySession.Static.Factions == null) return null;
            MyFaction f = MySession.Static.Factions.GetPlayerFaction(identityId);
            return f != null ? f.Tag : null;
        }

        private static string NameOf(long identityId)
        {
            if (identityId == 0) return null;
            MyIdentity identity = MySession.Static.Players.TryGetIdentity(identityId);
            return identity != null ? identity.DisplayName : null;
        }

        private static string Quote(string s)
        {
            return "\"" + (s ?? "?").Replace("\"", "'") + "\"";
        }
    }
}
