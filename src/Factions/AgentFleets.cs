using System;
using System.Collections.Generic;
using Factions.Core;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace Factions
{
    /// <summary>
    /// A Nexus agent's fleet grids (FAC-506 step 3): spawned and flown here on the authority's
    /// orders, by the same <see cref="VanillaFleetSpawner"/> and flight controller the authority
    /// runs for its own sectors. Every decision about the fleet stays on the authority; this class
    /// only carries out its orders and asks, and reports each grid's state in full on every report.
    ///
    /// <para>A grid the authority stops holding is closed after <see cref="UnheldGrace"/>, which
    /// covers an authority restart and any write-off whose order was lost. A grid left in this
    /// server's own save from an earlier process is closed on the first frame. Either way a grid a
    /// player owns is kept: it was captured, and the authority's reaper would leave it too.</para>
    ///
    /// <para><b>Across a sector boundary</b> (<see cref="SectorHandover"/>). A grid Nexus carried out of
    /// this server's sector is reported as handed off, and the authority holds it for the server that
    /// receives it. A fleet grid Nexus carried in is held at once, unflown, and reported under the key
    /// its name carries; the authority answers with an adopt order and its leg. One the authority does
    /// not take within <see cref="UnheldGrace"/> is let go, to the reaper, rather than closed.</para>
    /// </summary>
    internal sealed class AgentFleets
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private static readonly TimeSpan UnheldGrace = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan ReportedFor = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MunitionsKept = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan AmmoCheckEvery = TimeSpan.FromSeconds(5);

        private sealed class Held
        {
            public string Key;
            public long Eid;
            public string Tag;
            public ActionKind Kind;
            public string MunitionsId;
            public DateTime BornUtc;
            public bool InCombat, Fighting;
            public bool InWing, Winged;
            /// <summary>The fleet's waypoint as last ordered: where a lapsed wing order hands it back.</summary>
            public Vec3 TargetKm;
            public double Dealt, Taken;
            public DateTime UnheldSince = DateTime.MinValue;
            public NexusFleetAsk Ask;
            public bool Launching, Approaching, Escorting, Escorted;
            public Vec3 EscortTargetKm;
            public int ThrustLost, GaveUp;
            /// <summary>Carried in by Nexus and not yet adopted: held, reported, not flown.</summary>
            public bool Arrived;
        }

        private sealed class Spawning
        {
            public NexusFleetOrder Order;
            public DateTime StartedUtc;
        }

        private readonly VanillaFleetSpawner _spawner;
        private readonly NexusOrderInbox _inbox = new NexusOrderInbox();
        private readonly Dictionary<long, Held> _grids = new Dictionary<long, Held>();
        private readonly Dictionary<string, Spawning> _spawning = new Dictionary<string, Spawning>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _failed = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<long, DateTime> _closed = new Dictionary<long, DateTime>();
        private readonly Dictionary<long, DateTime> _handedOff = new Dictionary<long, DateTime>();
        private readonly List<long> _arrivals = new List<long>();
        private readonly List<long> _scratch = new List<long>();
        private readonly List<string> _keys = new List<string>();
        private bool _swept;

        // The authority's answers to the flight controller's lookups, from its latest reply.
        private Dictionary<long, List<WeaponBand>> _bands = new Dictionary<long, List<WeaponBand>>();
        private readonly Dictionary<string, KeyValuePair<string, List<long>>> _opponents =
            new Dictionary<string, KeyValuePair<string, List<long>>>(StringComparer.Ordinal);

        private sealed class MunitionsTotal
        {
            public readonly Dictionary<string, long> Closed = new Dictionary<string, long>(StringComparer.Ordinal);
            public DateTime ChangedUtc;
            public bool Out;
            public DateTime CheckedUtc;
        }
        private readonly Dictionary<string, MunitionsTotal> _munitions = new Dictionary<string, MunitionsTotal>(StringComparer.Ordinal);

        public AgentFleets(VanillaFleetSpawner spawner)
        {
            _spawner = spawner;
            _spawner.SetFleetBandsLookup(eid =>
            {
                List<WeaponBand> b;
                return _bands.TryGetValue(eid, out b) ? b : null;
            });
            _spawner.SetOpponentsLookup((label, grids) =>
            {
                KeyValuePair<string, List<long>> o;
                if (label == null || !_opponents.TryGetValue(label, out o)) return null;
                if (o.Value != null && grids != null) foreach (long eid in o.Value) grids.Add(eid);
                return o.Key;
            });
        }

        /// <summary>A station's zone fire pacing, decided here from the snapshot's node.</summary>
        public Func<long, double, double, ZoneFireCall> ZonePacing
        {
            set { _spawner.SetZonePacing(value); }
        }

        public int Count { get { return _grids.Count; } }

        /// <summary>Whether this server flies the grid for the authority: the reaper's held test.</summary>
        public bool Holds(long eid) { return _grids.ContainsKey(eid); }

        /// <summary>Every grid this server flies, for the grid census.</summary>
        public void CollectGridIds(HashSet<long> into)
        {
            foreach (long eid in _grids.Keys) into.Add(eid);
        }

        /// <summary>Each flown grid as the traffic budget prices it: its class, and the bench law's
        /// prediction for its live block count, since this server has no prefab catalogue.</summary>
        public void ReadLoads(List<ServerPerfMonitor.FleetLoad> into, PrefabCeiling law)
        {
            if (law == null) return;
            foreach (Held g in _grids.Values)
            {
                MyCubeGrid grid;
                int blocks = MyEntities.TryGetEntityById(g.Eid, out grid, false) && grid != null && !grid.MarkedForClose
                           ? grid.BlocksCount : 0;
                into.Add(new ServerPerfMonitor.FleetLoad
                {
                    LeadGridId = g.Eid,
                    Class = FleetLoadCost.ClassOf(g.Kind),
                    BaseMs = blocks > 0 ? law.PredictedAwakeMs(blocks) : TrafficBudget.UnknownHullMs,
                });
            }
        }
        public int SpawningCount { get { return _spawning.Count; } }

        /// <summary>The authority's orders, asks and held set, from its reply.</summary>
        public void Apply(NexusStationReply reply, DateTime nowUtc)
        {
            if (reply == null) return;
            foreach (NexusFleetOrder o in _inbox.Accept(reply.Orders))
            {
                try { Execute(o, nowUtc); }
                catch (Exception ex) { Log.Warn(ex, "Fleet order " + o.Op + " for " + o.Key + " threw."); }
            }

            var asks = new Dictionary<long, NexusFleetAsk>();
            _opponents.Clear();
            if (reply.Asks != null)
                foreach (NexusFleetAsk a in reply.Asks)
                {
                    if (a == null) continue;
                    asks[a.Eid] = a;
                    if (a.Combat == NexusCombat.Fight && a.Label != null)
                        _opponents[a.Label] = new KeyValuePair<string, List<long>>(a.OpponentsLabel, a.Opponents);
                }
            var bands = new Dictionary<long, List<WeaponBand>>();
            if (reply.Bands != null) foreach (NexusFleetBands b in reply.Bands) if (b != null && b.Bands != null) bands[b.Eid] = b.Bands;
            _bands = bands;
            var held = new HashSet<long>();
            if (reply.Held != null) foreach (long eid in reply.Held) held.Add(eid);
            foreach (Held g in _grids.Values)
            {
                NexusFleetAsk a;
                g.Ask = asks.TryGetValue(g.Eid, out a) ? a : null;
                if (held.Contains(g.Eid)) g.UnheldSince = DateTime.MinValue;
                else if (g.UnheldSince == DateTime.MinValue) g.UnheldSince = nowUtc;
            }
        }

        private void Execute(NexusFleetOrder o, DateTime nowUtc)
        {
            Held h;
            switch (o.Op)
            {
                case NexusFleetOp.Spawn:
                    if (o.Key == null || _spawning.ContainsKey(o.Key)) return;
                    foreach (Held g in _grids.Values) if (g.Key == o.Key) return;
                    _spawning[o.Key] = new Spawning { Order = o, StartedUtc = nowUtc };
                    return;
                case NexusFleetOp.Despawn:
                    Close(o.Eid, nowUtc);
                    return;
                case NexusFleetOp.Retarget:
                    if (!_grids.TryGetValue(o.Eid, out h)) return;
                    // The order replaces the formation: no hand-back onto the old waypoint after it.
                    h.Ask = null; h.Escorted = h.Escorting = false;
                    h.TargetKm = new Vec3(o.Tx, o.Ty, o.Tz);
                    _spawner.Retarget(o.Eid, h.TargetKm);
                    return;
                case NexusFleetOp.Release:
                    if (!_grids.TryGetValue(o.Eid, out h)) return;
                    // The order replaces the formation: no hand-back onto the old waypoint after it.
                    h.Ask = null; h.Escorted = h.Escorting = false;
                    _spawner.ReleaseControl(o.Eid);
                    return;
                case NexusFleetOp.Integrity:
                    if (_grids.ContainsKey(o.Eid)) _spawner.WatchIntegrity(o.Eid, o.Tx);
                    return;
                case NexusFleetOp.Withhold:
                    if (!_grids.TryGetValue(o.Eid, out h)) return;
                    h.Ask = null; h.Escorted = h.Escorting = h.InCombat = h.Fighting = false;
                    _spawner.Withhold(o.Eid);
                    return;
                case NexusFleetOp.Unwithhold:
                    if (_grids.ContainsKey(o.Eid)) _spawner.ForgetWithheld(o.Eid);
                    return;
                case NexusFleetOp.Leave:
                    if (!_grids.TryGetValue(o.Eid, out h)) return;
                    h.InCombat = h.Fighting = false;
                    _spawner.LeaveFrom(o.Eid, new Vec3(o.Tx, o.Ty, o.Tz), o.ClearM, o.Reason ?? "repelled");
                    return;
                case NexusFleetOp.EndWing:
                    if (!_grids.TryGetValue(o.Eid, out h)) return;
                    h.TargetKm = new Vec3(o.Tx, o.Ty, o.Tz);
                    h.InWing = h.Winged = false;
                    _spawner.EndWing(o.Eid, h.TargetKm);
                    return;
                case NexusFleetOp.Adopt:
                    if (!_grids.TryGetValue(o.Eid, out h)) return;
                    if (o.Key != null) h.Key = o.Key;
                    if (o.Tag != null) h.Tag = o.Tag;
                    h.Kind = (ActionKind)o.Kind;
                    h.MunitionsId = o.MunitionsId;
                    h.Arrived = false;
                    bool adopted = _spawner.AdoptArrival(o.Eid, h.Key, h.Tag, h.Kind, o.Prefab, o.MunitionsId, null);
                    Log.Info("NEXUS_FLEET_ADOPTED key=" + h.Key + " eid=" + o.Eid + " kind=" + h.Kind + " ok=" + adopted);
                    return;
            }
        }

        private void Close(long eid, DateTime nowUtc)
        {
            Held g;
            if (!_grids.TryGetValue(eid, out g)) return;
            _grids.Remove(eid);
            _spawner.Despawn(eid);
            _closed[eid] = nowUtc;
            // What it still held goes back to its fleet on the authority.
            Dictionary<string, long> observed;
            if (g.MunitionsId == null || !_spawner.TryTakeObservedMunitions(g.MunitionsId, out observed)) return;
            MunitionsTotal t = TotalFor(g.MunitionsId);
            foreach (KeyValuePair<string, long> kv in observed)
            {
                long prior;
                t.Closed.TryGetValue(kv.Key, out prior);
                t.Closed[kv.Key] = prior + kv.Value;
            }
            t.ChangedUtc = nowUtc;
        }

        private MunitionsTotal TotalFor(string id)
        {
            MunitionsTotal t;
            if (!_munitions.TryGetValue(id, out t)) { t = new MunitionsTotal(); _munitions[id] = t; }
            return t;
        }

        /// <summary>Per frame.</summary>
        public void Tick(DateTime nowUtc)
        {
            if (!_swept) { _swept = true; SweepLeftovers(); }
            _spawner.TickFlight();
            TickSpawns(nowUtc);
            TakeArrivals(nowUtc);

            SweepGone(nowUtc);
            _scratch.Clear();
            foreach (Held g in _grids.Values)
            {
                if (!_spawner.GridExists(g.Eid)) { _scratch.Add(g.Eid); continue; }
                if (g.UnheldSince != DateTime.MinValue && nowUtc - g.UnheldSince >= UnheldGrace
                    && nowUtc - g.BornUtc >= UnheldGrace)
                {
                    // Taken by a player: theirs to keep, as the authority's reaper leaves it. One that
                    // arrived and was never taken is not ours to close either.
                    bool taken = IsPlayerOwned(g.Eid);
                    _scratch.Add(taken || g.Arrived ? g.Eid : -g.Eid);
                    if (taken) Log.Info("NEXUS_FLEET_UNHELD eid=" + g.Eid + " kept=player");
                    else if (g.Arrived) Log.Info("NEXUS_FLEET_UNHELD eid=" + g.Eid + " kept=unclaimed");
                    continue;
                }
                if (g.Arrived) continue;
                NexusFleetAsk a = g.Ask;
                g.Launching = a != null && a.Launch
                              && _spawner.BeginLaunch(g.Eid, new Vec3(a.Lx, a.Ly, a.Lz), a.Through);
                g.Approaching = !g.Launching && a != null && a.Approach
                                && _spawner.BeginManoeuvre(g.Eid, new Vec3(a.Ax, a.Ay, a.Az));
                bool wing = a != null && a.Wing;
                if (wing)
                {
                    // The order flies it now; the formation's hand-back would undo that.
                    g.Escorted = g.Escorting = false;
                }
                else if (a != null && a.Escort)
                {
                    g.EscortTargetKm = new Vec3(a.Ex, a.Ey, a.Ez);
                    g.Escorting = _spawner.FlyEscort(g.Eid, a.Anchor, a.Slot, g.EscortTargetKm);
                    g.Escorted = true;
                }
                else if (g.Escorted)
                {
                    _spawner.StopEscorting(g.Eid, g.EscortTargetKm);
                    g.Escorted = g.Escorting = false;
                }
                if (wing)
                {
                    g.Winged = _spawner.CommandWing(g.Eid, new WingDirective
                    {
                        AnchorId = a.WingAnchor, TargetId = a.WingTarget, Holding = a.WingHolding,
                        BreakingOff = a.WingBreakingOff, Order = a.WingOrder, Slot = a.WingSlot, Label = a.WingLabel,
                        FleetBands = a.WingBands,
                    });
                    g.InWing = true;
                }
                else if (g.InWing)
                {
                    _spawner.EndWing(g.Eid, g.TargetKm);
                    g.InWing = g.Winged = false;
                }
                _spawner.HoldTurrets(g.Eid, null, a != null && a.Hold);
                NexusCombat combat = a != null ? a.Combat : NexusCombat.None;
                if (combat == NexusCombat.Fight)
                {
                    g.Fighting = _spawner.FightAtHold(g.Eid, new Vec3(a.Hx, a.Hy, a.Hz), a.FightBands, a.Label, a.Aimed,
                                                      a.Scope, a.Tactic, a.Learned, a.Pursue);
                    g.InCombat = true;
                }
                else if (combat == NexusCombat.Shadow)
                {
                    g.Fighting = _spawner.ShadowAtHold(g.Eid, new Vec3(a.Hx, a.Hy, a.Hz), a.ShadowTarget, a.Standoff,
                                                       a.Leash, a.Label);
                    g.InCombat = true;
                }
                else if (g.InCombat)
                {
                    _spawner.StopFighting(g.Eid);
                    g.InCombat = g.Fighting = false;
                }
                if (_spawner.ConsumeThrustLost(g.Eid)) g.ThrustLost++;
                if (_spawner.ConsumeLaunchGiveUp(g.Eid)) g.GaveUp++;
            }
            foreach (long id in _scratch)
            {
                if (id > 0) { Forget(id, nowUtc); continue; }
                Log.Info("NEXUS_FLEET_UNHELD eid=" + (-id) + " closed");
                Close(-id, nowUtc);
            }
            Prune(_failed, nowUtc);
            Prune(_closed, nowUtc);
            Prune(_handedOff, nowUtc);
            TickMunitions(nowUtc);
        }

        /// <summary>Drops each held grid that left the world without an order. Run before every report
        /// too: one missing from a report and not listed as handed off reads as lost.</summary>
        private void SweepGone(DateTime nowUtc)
        {
            List<long> gone = null;
            foreach (Held g in _grids.Values)
                if (!_spawner.GridExists(g.Eid)) (gone ?? (gone = new List<long>())).Add(g.Eid);
            if (gone != null) foreach (long id in gone) Forget(id, nowUtc);
        }

        /// <summary>Stops holding a grid without closing it: gone, taken by a player, or never claimed.</summary>
        private void Forget(long id, DateTime nowUtc)
        {
            _grids.Remove(id);
            _spawner.ReleaseControl(id);
            _spawner.SetSaved(id, true);
            byte to;
            string key;
            if (SectorHandover.TryTakeLeft(id, out to, out key))
            {
                _handedOff[id] = nowUtc;
                Log.Info("NEXUS_FLEET_HANDED_OFF eid=" + id + " key=" + key + " to=" + to);
            }
        }

        /// <summary>Holds each fleet hull Nexus carried in, unflown, until the authority adopts it.</summary>
        private void TakeArrivals(DateTime nowUtc)
        {
            SectorHandover.TakeArrivals(_arrivals);
            foreach (long eid in _arrivals)
            {
                if (_grids.ContainsKey(eid) || _closed.ContainsKey(eid)) continue;
                MyCubeGrid grid;
                long namedEid;
                string key;
                if (!MyEntities.TryGetEntityById(eid, out grid, false) || grid == null || grid.MarkedForClose
                    || !NpcGridNaming.TryParseName(grid.Name, out namedEid, out key) || namedEid != eid) continue;
                // A rotor-mounted turret carries the name too and travels with its hull.
                if (WingWorld.HullOf(grid) != grid || IsPlayerOwned(eid)) continue;
                Vec3? at = _spawner.GetGridPositionKm(eid);
                _grids[eid] = new Held
                {
                    Key = key, Eid = eid, Tag = _spawner.GetGridOwnerFactionTag(eid), BornUtc = nowUtc,
                    UnheldSince = nowUtc, Arrived = true, TargetKm = at ?? default(Vec3),
                };
                _spawner.SetSaved(eid, false);
                Log.Info("NEXUS_FLEET_ARRIVED key=" + key + " eid=" + eid);
            }
        }

        private void TickMunitions(DateTime nowUtc)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (Held g in _grids.Values) if (g.MunitionsId != null) live.Add(g.MunitionsId);
            foreach (string id in live)
            {
                MunitionsTotal t = TotalFor(id);
                if (t.ChangedUtc == default(DateTime)) t.ChangedUtc = nowUtc;
                if (nowUtc - t.CheckedUtc < AmmoCheckEvery) continue;
                t.CheckedUtc = nowUtc;
                t.Out = _spawner.IsOutOfMunitions(id);   // walks the grids' blocks; hence the interval
            }
            foreach (Spawning sp in _spawning.Values) if (sp.Order.MunitionsId != null) live.Add(sp.Order.MunitionsId);
            _spawner.RetainMunitions(live);
            List<string> old = null;
            foreach (KeyValuePair<string, MunitionsTotal> kv in _munitions)
                if (!live.Contains(kv.Key) && nowUtc - kv.Value.ChangedUtc > MunitionsKept) (old ?? (old = new List<string>())).Add(kv.Key);
            if (old != null) foreach (string id in old) _munitions.Remove(id);
        }

        private void TickSpawns(DateTime nowUtc)
        {
            if (_spawning.Count == 0) return;
            _keys.Clear();
            _keys.AddRange(_spawning.Keys);
            foreach (string key in _keys)
            {
                Spawning s = _spawning[key];
                NexusFleetOrder o = s.Order;
                long eid;
                if (!_spawner.TryTakeSpawn(key, out eid))
                {
                    if (_spawner.SpawnInFlight(key)) continue;
                    if (nowUtc - s.StartedUtc >= SpawnTimeout) eid = 0;
                    else
                    {
                        eid = _spawner.SpawnOrdered(key, (ActionKind)o.Kind, o.Tag, o.FactionId,
                                                    new Vec3(o.X, o.Y, o.Z), new Vec3(o.Tx, o.Ty, o.Tz), o.Prefab,
                                                    o.MunitionsId, o.Munitions);
                        if (eid == 0) continue;
                    }
                }
                _spawning.Remove(key);
                if (eid > 0)
                {
                    _grids[eid] = new Held
                    {
                        Key = key, Eid = eid, Tag = o.Tag, Kind = (ActionKind)o.Kind, MunitionsId = o.MunitionsId, BornUtc = nowUtc,
                        TargetKm = new Vec3(o.Tx, o.Ty, o.Tz),
                    };
                    _spawner.SetSaved(eid, false);
                    Log.Info("NEXUS_FLEET_SPAWNED key=" + key + " eid=" + eid + " prefab=" + o.Prefab);
                }
                else
                {
                    _failed[key] = nowUtc;
                    Log.Info("NEXUS_FLEET_SPAWN_FAILED key=" + key + " prefab=" + o.Prefab);
                }
            }
        }

        /// <summary>Closes fleet grids this server saved in an earlier process: nothing here holds
        /// them, and the authority has re-spawned or written off their fleets since. Finds nothing
        /// under Nexus, whose sector load resets every grid's entity name to its id; there an agent's
        /// hulls are kept out of the save instead (<see cref="VanillaFleetSpawner.SetSaved"/>).</summary>
        private static bool IsPlayerOwned(long eid)
        {
            MyCubeGrid grid;
            return MyEntities.TryGetEntityById(eid, out grid, false) && grid != null
                && GridReaper.IsPlayerOwned(MySession.Static != null ? MySession.Static.Players : null, grid);
        }

        private void SweepLeftovers()
        {
            int closed = 0;
            var victims = new List<long>();
            foreach (MyEntity e in MyEntities.GetEntities())
            {
                var grid = e as MyCubeGrid;
                long namedEid; string key;
                if (grid == null || grid.MarkedForClose
                    || !NpcGridNaming.TryParseName(grid.Name, out namedEid, out key) || namedEid != grid.EntityId) continue;
                if (GridReaper.IsPlayerOwned(MySession.Static != null ? MySession.Static.Players : null, grid)) continue;
                victims.Add(grid.EntityId);
            }
            foreach (long eid in victims) closed += _spawner.Despawn(eid);
            if (victims.Count > 0) Log.Info("NEXUS_FLEET_LEFTOVERS closed=" + closed + " named=" + victims.Count);
        }

        /// <summary>The fleet half of the report.</summary>
        public void FillReport(NexusAgentReport report)
        {
            report.Fleets = true;
            report.OrdersThrough = _inbox.Through;
            report.ClockUtc = DateTime.UtcNow;
            SweepGone(report.ClockUtc);
            foreach (Held g in _grids.Values)
            {
                Vec3? p = _spawner.GetGridPositionKm(g.Eid);
                if (!p.HasValue) continue;
                // Re-applied each cadence: a part that attached since, or a hull a player took.
                _spawner.SetSaved(g.Eid, IsPlayerOwned(g.Eid));
                DateTime? damaged = _spawner.LastDamageUtc(g.Eid);
                double dealt, taken;
                _spawner.TakeFightDamage(g.Eid, out dealt, out taken);
                g.Dealt += dealt; g.Taken += taken;
                ManoeuvreKind? manoeuvre = _spawner.CombatManoeuvre(g.Eid);
                double? integrity = _spawner.ReadIntegrity(g.Eid);
                if (report.Grids == null) report.Grids = new List<NexusFleetGrid>();
                var row = new NexusFleetGrid
                {
                    Key = g.Key, Eid = g.Eid, X = p.Value.X, Y = p.Value.Y, Z = p.Value.Z,
                    Speed = _spawner.GetGridSpeedKmPerMin(g.Eid),
                    Owner = _spawner.GetGridOwnerFactionTag(g.Eid),
                    Damaged = damaged ?? DateTime.MinValue,
                    Prefab = _spawner.GridPrefabId(g.Eid),
                    Launching = g.Launching, Approaching = g.Approaching,
                    Manoeuvring = _spawner.IsManoeuvring(g.Eid),
                    Complete = _spawner.IsManoeuvreComplete(g.Eid),
                    Escorting = g.Escorting,
                    ThrustLost = g.ThrustLost, GaveUp = g.GaveUp,
                    Controlled = _spawner.IsFlying(g.Eid), Fighting = g.Fighting,
                    Manoeuvre = manoeuvre.HasValue ? (int)manoeuvre.Value : -1,
                    Integrity = integrity ?? -1, OutOfAction = _spawner.IsOutOfAction(g.Eid),
                    Leaving = !_spawner.IsLeaveDone(g.Eid), Winged = g.InWing && g.Winged,
                    Dealt = g.Dealt, Taken = g.Taken,
                };
                long by;
                DateTime? hit = _spawner.LastHostileHit(g.Eid, g.Tag, out by);
                if (hit.HasValue)
                {
                    row.Hit = hit.Value;
                    row.HitBy = by;
                    if (by != 0)
                    {
                        row.HitByOwner = _spawner.OwnerIdentityOf(by);
                        row.HitByTag = _spawner.GetGridOwnerFactionTag(by);
                        Vec3? at = _spawner.GetGridPositionKm(by);
                        if (at.HasValue) { row.HitByPlaced = true; row.HitByX = at.Value.X; row.HitByY = at.Value.Y; row.HitByZ = at.Value.Z; }
                    }
                }
                report.Grids.Add(row);
            }
            var liveIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Held g in _grids.Values) if (g.MunitionsId != null) liveIds.Add(g.MunitionsId);
            foreach (KeyValuePair<string, MunitionsTotal> kv in _munitions)
            {
                bool live = liveIds.Contains(kv.Key);
                if (report.Munitions == null) report.Munitions = new List<NexusMunitionsTotal>();
                report.Munitions.Add(new NexusMunitionsTotal
                {
                    Id = kv.Key, Live = live, Out = live && kv.Value.Out,
                    Closed = kv.Value.Closed.Count > 0 ? new Dictionary<string, long>(kv.Value.Closed, StringComparer.Ordinal) : null,
                });
            }
            if (_failed.Count > 0) report.SpawnFailed = new List<string>(_failed.Keys);
            if (_closed.Count > 0) report.Closed = new List<long>(_closed.Keys);
            if (_handedOff.Count > 0) report.HandedOff = new List<long>(_handedOff.Keys);
        }

        private static void Prune<T>(Dictionary<T, DateTime> d, DateTime nowUtc)
        {
            if (d.Count == 0) return;
            List<T> old = null;
            foreach (KeyValuePair<T, DateTime> kv in d)
                if (nowUtc - kv.Value > ReportedFor) (old ?? (old = new List<T>())).Add(kv.Key);
            if (old != null) foreach (T k in old) d.Remove(k);
        }
    }
}
