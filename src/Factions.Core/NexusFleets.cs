using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>A command the authority sends an agent about one fleet grid. Applied once, in
    /// sequence order.</summary>
    public enum NexusFleetOp
    {
        None = 0,
        Spawn = 1,
        Despawn = 2,
        Retarget = 3,
        Release = 4,
        /// <summary>Damage the grid to integrity <c>Tx</c> and watch it from there.</summary>
        Integrity = 5,
        /// <summary>Keep the grid as a wreck: out of its fleet's assembly and every controller.</summary>
        Withhold = 6,
        Unwithhold = 7,
        /// <summary>Break away from the fight, away from <c>Tx,Ty,Tz</c>, until nothing hostile is
        /// within <see cref="NexusFleetOrder.ClearM"/>.</summary>
        Leave = 8,
        /// <summary>End a commander's order and hand the grid back to its cruise autopilot on <c>Tx,Ty,Tz</c>.</summary>
        EndWing = 9,
        /// <summary>Take over a fleet grid that arrived here from another server's sector, under
        /// <c>Key</c>, <c>Kind</c>, <c>Tag</c>, <c>Prefab</c> and <c>MunitionsId</c>. A retarget follows.</summary>
        Adopt = 10,
        /// <summary>A spawn still waiting: move it to <c>X,Y,Z</c>, aimed at <c>Tx,Ty,Tz</c>, where
        /// the fleet is now. An agent's pacer can hold a spawn for minutes (FAC-512).</summary>
        Respot = 11,
        /// <summary>Drop the spawn under <c>Key</c>: no player is near the fleet any more. One that
        /// already landed, or lands after this, is closed.</summary>
        Withdraw = 12,
    }

    /// <summary>A held grid's weapon bands.</summary>
    public sealed class NexusFleetBands
    {
        public long Eid;
        public List<WeaponBand> Bands;
    }

    /// <summary>One armed fleet's munitions on an agent; see <see cref="NexusAgentReport.Munitions"/>.</summary>
    public sealed class NexusMunitionsTotal
    {
        public string Id;
        public Dictionary<string, long> Closed;
        /// <summary>Grids under this id stand here, and every one is out of ammunition.</summary>
        public bool Live, Out;
    }

    /// <summary>What the authority's observation loop asked of a fleet grid's weapons: fight or
    /// shadow at a hold, with the same arguments a local grid gets.</summary>
    public enum NexusCombat
    {
        None = 0,
        Fight = 1,
        Shadow = 2,
    }

    public sealed class NexusFleetOrder
    {
        /// <summary>Clock-seeded, so it keeps rising across an authority restart.</summary>
        public long Seq;
        public NexusFleetOp Op;
        /// <summary>The fleet key the grid is spawned under.</summary>
        public string Key;
        /// <summary>The agent's grid; 0 on a spawn.</summary>
        public long Eid;
        /// <summary>Spawn and adopt: <see cref="ActionKind"/> as its integer, owner, and prefab.</summary>
        public int Kind;
        public string Tag;
        public long FactionId;
        public string Prefab;
        /// <summary>Spawn position in km (spawn only).</summary>
        public double X, Y, Z;
        /// <summary>Target in km (spawn, retarget); integrity in <c>Tx</c>.</summary>
        public double Tx, Ty, Tz;
        /// <summary>Spawn: the fleet's staged magazines, which replace the prefab's. Adopt: the id only.</summary>
        public string MunitionsId;
        public Dictionary<string, long> Munitions;
        /// <summary>Leave only: the clearance to reach, and why.</summary>
        public double ClearM;
        public string Reason;
    }

    /// <summary>What the authority's observation loop asked of a grid's flight controller over the
    /// last window: the same per-frame launch and approach asks it makes of a local grid.</summary>
    public sealed class NexusFleetAsk
    {
        public long Eid;
        public bool Launch;
        public bool Through;
        public double Lx, Ly, Lz;
        public bool Approach;
        public double Ax, Ay, Az;
        /// <summary>Hold slot <see cref="Slot"/> in formation on grid <see cref="Anchor"/>; fall back to
        /// the cruise autopilot on (Ex, Ey, Ez), the fleet's waypoint, when the controller drops it.</summary>
        public bool Escort;
        public long Anchor;
        public int Slot;
        public double Ex, Ey, Ez;
        /// <summary>Turrets off while true.</summary>
        public bool Hold;
        public NexusCombat Combat;
        /// <summary>The hold anchor in km, fight and shadow.</summary>
        public double Hx, Hy, Hz;
        public string Label;
        public List<WeaponBand> FightBands;
        public bool Aimed;
        public TargetScope Scope;
        public TacticOrder? Tactic;
        public TacticOrder? Learned;
        public long Pursue;
        /// <summary>What the authority's opponents lookup answered for <see cref="Label"/>.</summary>
        public string OpponentsLabel;
        public List<long> Opponents;
        public long ShadowTarget;
        public double Standoff, Leash;
        /// <summary>A wing commander's order on the grid, asked once a second while it obeys.</summary>
        public bool Wing;
        public long WingAnchor, WingTarget;
        public bool WingHolding, WingBreakingOff;
        public TacticOrder WingOrder;
        public int WingSlot;
        public string WingLabel;
        public List<WeaponBand> WingBands;
    }

    /// <summary>One fleet grid on an agent, as of its report.</summary>
    public sealed class NexusFleetGrid
    {
        public string Key;
        public long Eid;
        /// <summary>Position in km; speed in km/min.</summary>
        public double X, Y, Z;
        public double Speed;
        public string Owner;
        public DateTime Damaged;
        public string Prefab;
        /// <summary>Results of the last launch and approach asks, and the controller's own state.</summary>
        public bool Launching;
        public bool Approaching;
        public bool Manoeuvring;
        public bool Complete;
        public bool Escorting;
        /// <summary>The controller holds it at all (<c>IsFlying</c>).</summary>
        public bool Controlled;
        /// <summary>The last fight or shadow ask was taken.</summary>
        public bool Fighting;
        /// <summary><see cref="ManoeuvreKind"/> as its integer; -1 for none.</summary>
        public int Manoeuvre = -1;
        /// <summary>Watched integrity, 0..1; -1 when not watched.</summary>
        public double Integrity = -1;
        public bool OutOfAction;
        /// <summary>Breaking away on a leave order and not yet clear.</summary>
        public bool Leaving;
        /// <summary>The last wing ask was taken.</summary>
        public bool Winged;
        /// <summary>Damage dealt and taken in fights, summed since the grid spawned.</summary>
        public double Dealt, Taken;
        /// <summary>The last hit from a grid outside the fleet's faction: when, and the grid that
        /// fired it, its owner and faction, and where it was.</summary>
        public DateTime Hit;
        public long HitBy;
        public long HitByOwner;
        public string HitByTag;
        public bool HitByPlaced;
        public double HitByX, HitByY, HitByZ;
        /// <summary>How many times each latch has fired on the agent since the grid spawned.</summary>
        public int ThrustLost;
        public int GaveUp;
    }

    /// <summary>
    /// The authority's half of fleets flown on agents (FAC-506 step 3): which grids each agent
    /// holds for which fleet key, their last reported state, and the orders owed to each agent.
    ///
    /// <para>Every read the observation loop makes of a local grid has an answer here from the
    /// agent's latest report. A grid missing from a newer report is gone; it was closed by us when
    /// the agent closed it on an order, or when its server restarted or stopped reporting, since
    /// neither of those is a ship shot down.</para>
    /// </summary>
    public sealed class NexusFleetRegistry
    {
        /// <summary>An agent silent this long has its grids written off as closed by us.</summary>
        public static readonly TimeSpan MaxSilence = TimeSpan.FromSeconds(30);
        /// <summary>An ask not repeated for this long is no longer sent.</summary>
        public static readonly TimeSpan AskFresh = TimeSpan.FromSeconds(3);
        /// <summary>A spawn the agent has not answered in this long is given up as failed.</summary>
        public static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(120);
        /// <summary>A gone grid is remembered this long, for the reads made after it went.</summary>
        public static readonly TimeSpan GoneMemory = TimeSpan.FromMinutes(10);
        /// <summary>How long a grid Nexus carried across a sector boundary is held for the server that
        /// receives it. Nexus closes it on the source in the frame it sends it and spawns it on the
        /// target as soon as a free spot is found, so a report a second later normally carries it;
        /// one that never arrives is gone, read as any other grid that left the world.</summary>
        public static readonly TimeSpan TransitHold = TimeSpan.FromSeconds(20);
        /// <summary>A grid that hit one of ours is answered for this long after its last report.</summary>
        public static readonly TimeSpan ContactMemory = TimeSpan.FromMinutes(2);

        /// <summary>The authority's weapon-band and opponents lookups, as handed to its own flight
        /// controller; read once per reply for the grids on each agent.</summary>
        public Func<long, IReadOnlyList<WeaponBand>> BandsOf { get; set; }
        public Func<string, ICollection<long>, string> OpponentsOf { get; set; }

        private sealed class Grid
        {
            public byte Home;
            public string Key;
            public long Eid;
            /// <summary>The fleet's faction, from the spawn order.</summary>
            public string Tag;
            public NexusFleetGrid Last;
            /// <summary>An unacknowledged leave order: the grid counts as leaving until the agent has applied it.</summary>
            public long LeaveSeq;
            public DateTime WingAskUtc;
            public bool Gone;
            public bool ClosedByUs;
            public DateTime GoneUtc;
            /// <summary>Its home reported it carried into another server's sector; no server holds it yet.</summary>
            public bool Transit;
            public DateTime TransitUtc;
            /// <summary>What the receiving server is told to adopt it as: kind, owner, prefab, munitions.</summary>
            public NexusFleetOrder Template;
            /// <summary>The last hit its earlier homes saw: a new home's damage clock starts empty.</summary>
            public DateTime DamagedFloor = DateTime.MinValue;
            public int ThrustConsumed, GaveUpConsumed;
            public NexusFleetAsk Ask;
            public DateTime LaunchAskUtc, ApproachAskUtc, EscortAskUtc, HoldAskUtc, CombatAskUtc;
            public double DealtTaken, TakenTaken;
        }

        private struct Contact
        {
            public Vec3? PosKm;
            public long Owner;
            public string Tag;
            public DateTime SeenUtc;
        }

        private sealed class Munitions
        {
            public Dictionary<string, long> Closed = new Dictionary<string, long>(StringComparer.Ordinal);
            public Dictionary<string, long> Taken = new Dictionary<string, long>(StringComparer.Ordinal);
            public bool Live, Out;
        }

        private sealed class Home
        {
            public string Session;
            public DateTime LastUtc;
            public bool Fleets;
            public readonly List<NexusFleetOrder> Outbox = new List<NexusFleetOrder>();
            public readonly Dictionary<string, NexusFleetOrder> Spawning = new Dictionary<string, NexusFleetOrder>(StringComparer.Ordinal);
            public readonly Dictionary<string, DateTime> SpawnedUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            /// <summary>When each waiting spawn was last moved; pruned by <see cref="Expire"/>.</summary>
            public readonly Dictionary<string, DateTime> RespotUtc = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        }

        private readonly Dictionary<long, Grid> _grids = new Dictionary<long, Grid>();
        private readonly Dictionary<byte, Home> _homes = new Dictionary<byte, Home>();
        private readonly Dictionary<string, long> _completed = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<long, Contact> _contacts = new Dictionary<long, Contact>();
        private readonly Dictionary<long, Expected> _expected = new Dictionary<long, Expected>();
        private readonly List<long> _rehomed = new List<long>();

        /// <summary>A grid the authority flew itself that Nexus carried into an agent's sector.</summary>
        private sealed class Expected
        {
            public NexusFleetOrder Template;
            public DateTime Damaged;
            public DateTime SinceUtc;
        }
        private readonly Dictionary<byte, Dictionary<string, Munitions>> _munitions = new Dictionary<byte, Dictionary<string, Munitions>>();
        private long _lastSeq;

        /// <summary>Grids live on agents.</summary>
        public int LiveCount
        {
            get { int n = 0; foreach (Grid g in _grids.Values) if (!g.Gone) n++; return n; }
        }

        public int SpawningCount
        {
            get { int n = 0; foreach (Home h in _homes.Values) n += h.Spawning.Count; return n; }
        }

        /// <summary>Whether <paramref name="home"/> reports fleet hands and has reported recently.</summary>
        public bool CanFly(byte home, DateTime nowUtc)
        {
            Home h;
            return _homes.TryGetValue(home, out h) && h.Fleets && nowUtc - h.LastUtc <= MaxSilence;
        }

        // ---- spawns ------------------------------------------------------------------------

        /// <summary>Queues a spawn under <paramref name="order"/>'s key unless one is already on its way.</summary>
        public void Spawn(byte home, NexusFleetOrder order, DateTime nowUtc)
        {
            if (order == null || order.Key == null) return;
            Home h = HomeOf(home);
            if (h.Spawning.ContainsKey(order.Key) || _completed.ContainsKey(order.Key)) return;
            order.Op = NexusFleetOp.Spawn;
            order.Eid = 0;
            Queue(h, order, nowUtc);
            h.Spawning[order.Key] = order;
            h.SpawnedUtc[order.Key] = nowUtc;
        }

        /// <summary>A waiting spawn is moved no more often than this.</summary>
        public static readonly TimeSpan RespotEvery = TimeSpan.FromSeconds(2);
        /// <summary>Nor for less than this, in its position or its target.</summary>
        public const double RespotMinKm = 0.5;

        /// <summary>Moves the spawn waiting under <paramref name="key"/> to where its fleet is now
        /// (<see cref="NexusFleetOp.Respot"/>). An agent spawns at the order's position whenever its
        /// pacer lets it, which on production was a median 27 s after the order for the spawns that
        /// landed where no player was (FAC-512). True when an order was queued.</summary>
        public bool Respot(string key, Vec3 posKm, Vec3 targetKm, DateTime nowUtc)
        {
            if (key == null) return false;
            foreach (Home h in _homes.Values)
            {
                NexusFleetOrder o;
                if (!h.Spawning.TryGetValue(key, out o)) continue;
                DateTime last;
                if (h.RespotUtc.TryGetValue(key, out last) && nowUtc - last < RespotEvery) return false;
                double moved = posKm.DistanceTo(new Vec3(o.X, o.Y, o.Z));
                double turned = targetKm.DistanceTo(new Vec3(o.Tx, o.Ty, o.Tz));
                if (moved < RespotMinKm && turned < RespotMinKm) return false;
                o.X = posKm.X; o.Y = posKm.Y; o.Z = posKm.Z;
                o.Tx = targetKm.X; o.Ty = targetKm.Y; o.Tz = targetKm.Z;
                h.RespotUtc[key] = nowUtc;
                Queue(h, new NexusFleetOrder
                {
                    Op = NexusFleetOp.Respot, Key = key,
                    X = posKm.X, Y = posKm.Y, Z = posKm.Z, Tx = targetKm.X, Ty = targetKm.Y, Tz = targetKm.Z,
                }, nowUtc);
                return true;
            }
            return false;
        }

        /// <summary>Gives up the spawn waiting under <paramref name="key"/> and tells its agent to drop
        /// it (<see cref="NexusFleetOp.Withdraw"/>). A grid the agent reports under the key afterwards
        /// is not taken: it is not ours, and the agent closes it. False when no spawn was waiting.</summary>
        public bool Withdraw(string key, DateTime nowUtc)
        {
            if (key == null) return false;
            foreach (Home h in _homes.Values)
            {
                if (!h.Spawning.Remove(key)) continue;
                h.SpawnedUtc.Remove(key);
                h.RespotUtc.Remove(key);
                Queue(h, new NexusFleetOrder { Op = NexusFleetOp.Withdraw, Key = key }, nowUtc);
                return true;
            }
            return false;
        }

        public bool IsSpawning(string key)
        {
            if (key == null) return false;
            foreach (Home h in _homes.Values) if (h.Spawning.ContainsKey(key)) return true;
            return false;
        }

        /// <summary>Whether a spawn for <paramref name="fleetKey"/>, or for one of its squadron hulls
        /// (<c>fleetKey#i</c>), is still waiting on an agent.</summary>
        public bool IsSpawningUnder(string fleetKey)
        {
            if (fleetKey == null) return false;
            foreach (Home h in _homes.Values)
                foreach (string key in h.Spawning.Keys)
                    if (key.Length >= fleetKey.Length && string.CompareOrdinal(key, 0, fleetKey, 0, fleetKey.Length) == 0
                        && (key.Length == fleetKey.Length || key[fleetKey.Length] == '#'))
                        return true;
            return false;
        }

        /// <summary>Whether a finished spawn waits to be taken under <paramref name="key"/>, without taking it.</summary>
        public bool TryPeekSpawn(string key, out long eid)
        {
            eid = 0;
            return key != null && _completed.TryGetValue(key, out eid);
        }

        /// <summary>The finished spawn for <paramref name="key"/>, taken once: the grid's id, or 0
        /// when the agent could not spawn it.</summary>
        public bool TryTakeSpawn(string key, out long eid)
        {
            eid = 0;
            if (key == null || !_completed.TryGetValue(key, out eid)) return false;
            _completed.Remove(key);
            return true;
        }

        // ---- sector handover ---------------------------------------------------------------

        /// <summary>A grid the authority flew itself left at a position an agent's sector holds:
        /// the agent that reports it under <paramref name="template"/>'s key within
        /// <see cref="TransitHold"/> is told to adopt it.</summary>
        public void Expect(long eid, NexusFleetOrder template, DateTime? damagedUtc, DateTime nowUtc)
        {
            if (eid <= 0 || template == null || template.Key == null || _grids.ContainsKey(eid)) return;
            _expected[eid] = new Expected { Template = template, Damaged = damagedUtc ?? DateTime.MinValue, SinceUtc = nowUtc };
        }

        public bool IsExpected(long eid) { return _expected.ContainsKey(eid); }

        /// <summary>A grid an agent flew arrived on the authority: it stops being an agent's, and the
        /// authority flies it under what this returns. False for a grid it does not hold.</summary>
        public bool TakeBack(long eid, out NexusFleetOrder template, out DateTime? damagedUtc)
        {
            template = null;
            damagedUtc = null;
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return false;
            template = AdoptOrder(g);
            damagedUtc = LastDamageUtc(eid);
            _grids.Remove(eid);
            return true;
        }

        public bool InTransit(long eid) { Grid g; return _grids.TryGetValue(eid, out g) && !g.Gone && g.Transit; }

        /// <summary>The grids that changed server since the last call: each wants its current leg re-sent.</summary>
        public List<long> TakeRehomed()
        {
            var taken = new List<long>(_rehomed);
            _rehomed.Clear();
            return taken;
        }

        private void Rehome(Grid g, byte home, NexusFleetGrid r, DateTime nowUtc)
        {
            if (g.Last != null)
            {
                if (g.Last.Damaged > g.DamagedFloor) g.DamagedFloor = g.Last.Damaged;
                if (r.Prefab == null) r.Prefab = g.Last.Prefab;
            }
            g.Home = home;
            g.Transit = false;
            g.Last = r;
            g.LeaveSeq = 0;
            // The new home counts these from zero.
            g.ThrustConsumed = g.GaveUpConsumed = 0;
            g.DealtTaken = g.TakenTaken = 0;
            QueueAdopt(g, nowUtc);
        }

        private void QueueAdopt(Grid g, DateTime nowUtc)
        {
            Queue(HomeOf(g.Home), AdoptOrder(g), nowUtc);
            _rehomed.Add(g.Eid);
        }

        private static NexusFleetOrder AdoptOrder(Grid g)
        {
            NexusFleetOrder t = g.Template;
            return new NexusFleetOrder
            {
                Op = NexusFleetOp.Adopt, Key = g.Key, Eid = g.Eid, Tag = g.Tag,
                Kind = t != null ? t.Kind : 0, FactionId = t != null ? t.FactionId : 0,
                Prefab = g.Last != null && g.Last.Prefab != null ? g.Last.Prefab : t != null ? t.Prefab : null,
                MunitionsId = t != null ? t.MunitionsId : null,
            };
        }

        // ---- reads -------------------------------------------------------------------------

        /// <summary>Whether <paramref name="eid"/> is a grid on an agent, live or recently gone.</summary>
        public bool Owns(long eid) { return eid > 0 && _grids.ContainsKey(eid); }

        public bool Exists(long eid) { Grid g; return _grids.TryGetValue(eid, out g) && !g.Gone; }

        public bool ClosedByUs(long eid) { Grid g; return _grids.TryGetValue(eid, out g) && g.Gone && g.ClosedByUs; }

        public DateTime? LastDamageUtc(long eid)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g)) return null;
            DateTime at = g.Last != null && g.Last.Damaged > g.DamagedFloor ? g.Last.Damaged : g.DamagedFloor;
            if (at == DateTime.MinValue) return null;
            return at;
        }

        public string PrefabOf(long eid) { Grid g; return _grids.TryGetValue(eid, out g) && g.Last != null ? g.Last.Prefab : null; }

        public Vec3? PositionKm(long eid)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone || g.Last == null) return null;
            return new Vec3(g.Last.X, g.Last.Y, g.Last.Z);
        }

        /// <summary>km/min; -1 when the grid is not live, as a local read answers.</summary>
        public double SpeedKmPerMin(long eid)
        {
            Grid g;
            return _grids.TryGetValue(eid, out g) && !g.Gone && g.Last != null ? g.Last.Speed : -1;
        }

        public string OwnerTag(long eid) { Grid g; return _grids.TryGetValue(eid, out g) && !g.Gone && g.Last != null ? g.Last.Owner : null; }

        public bool IsManoeuvring(long eid) { Grid g; return _grids.TryGetValue(eid, out g) && !g.Gone && g.Last != null && g.Last.Manoeuvring; }

        public bool IsManoeuvreComplete(long eid) { Grid g; return _grids.TryGetValue(eid, out g) && !g.Gone && g.Last != null && g.Last.Complete; }

        public bool ConsumeThrustLost(long eid)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone || g.Last == null || g.Last.ThrustLost <= g.ThrustConsumed) return false;
            g.ThrustConsumed = g.Last.ThrustLost;
            return true;
        }

        public bool ConsumeLaunchGiveUp(long eid)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone || g.Last == null || g.Last.GaveUp <= g.GaveUpConsumed) return false;
            g.GaveUpConsumed = g.Last.GaveUp;
            return true;
        }

        public bool IsControlled(long eid) { Grid g; return _grids.TryGetValue(eid, out g) && !g.Gone && g.Last != null && g.Last.Controlled; }

        public bool IsOutOfAction(long eid) { Grid g; return _grids.TryGetValue(eid, out g) && !g.Gone && g.Last != null && g.Last.OutOfAction; }

        public ManoeuvreKind? CombatManoeuvre(long eid)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone || g.Last == null || g.Last.Manoeuvre < 0) return null;
            return (ManoeuvreKind)g.Last.Manoeuvre;
        }

        /// <summary>Watched integrity; null when the agent watches none, or the grid is gone.</summary>
        public double? ReadIntegrity(long eid)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone || g.Last == null || g.Last.Integrity < 0) return null;
            return g.Last.Integrity;
        }

        /// <summary>The last hostile hit and the grid that fired it, kept after the grid is gone
        /// for the verdict on why it went.</summary>
        public DateTime? LastHostileHit(long eid, out long attackerGridId)
        {
            attackerGridId = 0;
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Last == null || g.Last.Hit == DateTime.MinValue) return null;
            attackerGridId = g.Last.HitBy;
            return g.Last.Hit;
        }

        public DateTime? RecentHostileHit(long eid, DateTime nowUtc, TimeSpan window, out long attackerGridId)
        {
            DateTime? hit = LastHostileHit(eid, out attackerGridId);
            if (hit.HasValue && nowUtc - hit.Value <= window) return hit;
            attackerGridId = 0;
            return null;
        }

        /// <summary>Fight damage since the last take, as a local grid's tally answers it.</summary>
        public void TakeFightDamage(long eid, out double dealt, out double taken)
        {
            dealt = taken = 0;
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Last == null) return;
            dealt = Math.Max(0, g.Last.Dealt - g.DealtTaken);
            taken = Math.Max(0, g.Last.Taken - g.TakenTaken);
            g.DealtTaken = Math.Max(g.DealtTaken, g.Last.Dealt);
            g.TakenTaken = Math.Max(g.TakenTaken, g.Last.Taken);
        }

        /// <summary>A grid an agent reported firing on one of ours: where it was, and who owns it.</summary>
        public Vec3? ContactPositionKm(long eid) { Contact c; return _contacts.TryGetValue(eid, out c) ? c.PosKm : null; }
        public long ContactOwner(long eid) { Contact c; return _contacts.TryGetValue(eid, out c) ? c.Owner : 0; }
        public string ContactTag(long eid) { Contact c; return _contacts.TryGetValue(eid, out c) ? c.Tag : null; }

        /// <summary>True when grids under <paramref name="munitionsId"/> stand on an agent and every
        /// one is out of ammunition.</summary>
        public bool IsOutOfMunitions(string munitionsId)
        {
            if (munitionsId == null) return false;
            foreach (Dictionary<string, Munitions> byId in _munitions.Values)
            {
                Munitions m;
                if (byId.TryGetValue(munitionsId, out m) && m.Live && m.Out) return true;
            }
            return false;
        }

        /// <summary>What closed grids under <paramref name="munitionsId"/> held, since the last take.</summary>
        public bool TryTakeObservedMunitions(string munitionsId, out Dictionary<string, long> observed)
        {
            observed = null;
            if (munitionsId == null) return false;
            foreach (Dictionary<string, Munitions> byId in _munitions.Values)
            {
                Munitions m;
                if (!byId.TryGetValue(munitionsId, out m)) continue;
                foreach (KeyValuePair<string, long> kv in m.Closed)
                {
                    long taken;
                    m.Taken.TryGetValue(kv.Key, out taken);
                    if (kv.Value <= taken) continue;
                    if (observed == null) observed = new Dictionary<string, long>(StringComparer.Ordinal);
                    long prior;
                    observed.TryGetValue(kv.Key, out prior);
                    observed[kv.Key] = prior + (kv.Value - taken);
                    m.Taken[kv.Key] = kv.Value;
                }
            }
            return observed != null;
        }

        /// <summary>Drops what is kept for fleets that no longer exist.</summary>
        public void RetainMunitions(ICollection<string> liveIds)
        {
            foreach (Dictionary<string, Munitions> byId in _munitions.Values)
            {
                List<string> gone = null;
                foreach (string id in byId.Keys) if (!liveIds.Contains(id)) (gone ?? (gone = new List<string>())).Add(id);
                if (gone != null) foreach (string id in gone) byId.Remove(id);
            }
        }

        // ---- commands and asks -------------------------------------------------------------

        /// <summary>Damages the grid to <paramref name="integrity"/> on its agent and watches it.
        /// 0 for a live grid (the block count is the agent's), -1 otherwise, as for a missing one.</summary>
        public int WatchIntegrity(long eid, double integrity, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return -1;
            Order(g, NexusFleetOp.Integrity, new Vec3(integrity, 0, 0), nowUtc);
            return 0;
        }

        public int Withhold(long eid, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return 0;
            Order(g, NexusFleetOp.Withhold, default(Vec3), nowUtc);
            g.Ask = null;
            return 1;
        }

        public void ForgetWithheld(long eid, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return;
            Order(g, NexusFleetOp.Unwithhold, default(Vec3), nowUtc);
            LetGoIfTaken(g);
        }

        /// <summary>Turrets off while asked every frame with <paramref name="held"/>; on again the
        /// moment it is asked without, or the ask goes stale.</summary>
        public void AskHold(long eid, bool held, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return;
            if (!held) { if (g.Ask != null) g.Ask.Hold = false; return; }
            AskOf(g).Hold = true;
            g.HoldAskUtc = nowUtc;
        }

        /// <summary>This frame's fight ask; answers the agent's last word on whether it fights.</summary>
        public bool AskFight(long eid, Vec3 anchorKm, IReadOnlyList<WeaponBand> ownBands, string label, bool aimedFire,
                             TargetScope scope, TacticOrder? tacticOverride, TacticOrder? learned, long pursueTargetId,
                             DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return false;
            NexusFleetAsk a = AskOf(g);
            a.Combat = NexusCombat.Fight;
            a.Hx = anchorKm.X; a.Hy = anchorKm.Y; a.Hz = anchorKm.Z;
            a.FightBands = ownBands != null ? new List<WeaponBand>(ownBands) : null;
            a.Label = label; a.Aimed = aimedFire; a.Scope = scope;
            a.Tactic = tacticOverride; a.Learned = learned; a.Pursue = pursueTargetId;
            g.CombatAskUtc = nowUtc;
            return g.Last != null && g.Last.Fighting;
        }

        public bool AskShadow(long eid, Vec3 anchorKm, long targetId, double standoffM, double leashM, string label,
                              DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return false;
            NexusFleetAsk a = AskOf(g);
            a.Combat = NexusCombat.Shadow;
            a.Hx = anchorKm.X; a.Hy = anchorKm.Y; a.Hz = anchorKm.Z;
            a.ShadowTarget = targetId; a.Standoff = standoffM; a.Leash = leashM; a.Label = label;
            g.CombatAskUtc = nowUtc;
            return g.Last != null && g.Last.Fighting;
        }

        public void StopFighting(long eid)
        {
            Grid g;
            if (_grids.TryGetValue(eid, out g) && g.Ask != null) g.Ask.Combat = NexusCombat.None;
        }

        /// <summary>Closes the grid on its agent. It counts as gone and closed by us from now.</summary>
        public int Despawn(long eid, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return 0;
            Order(g, NexusFleetOp.Despawn, default(Vec3), nowUtc);
            MarkGone(g, true, nowUtc);
            return 1;
        }

        /// <summary>A new cruise leg. True while the grid is live; the agent wires it.</summary>
        public bool Retarget(long eid, Vec3 targetKm, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return false;
            Order(g, NexusFleetOp.Retarget, targetKm, nowUtc);
            g.Ask = null;
            return true;
        }

        public void Release(long eid, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return;
            Order(g, NexusFleetOp.Release, default(Vec3), nowUtc);
            g.Ask = null;
            LetGoIfTaken(g);
        }

        /// <summary>A grid another faction owns now, let go by the fleet: no longer held, so the agent
        /// stops flying and reporting it, and keeps it as the new owner's.</summary>
        private void LetGoIfTaken(Grid g)
        {
            if (g.Tag == null || g.Last == null || g.Last.Owner == null) return;
            if (string.Equals(g.Last.Owner, g.Tag, StringComparison.OrdinalIgnoreCase)) return;
            _grids.Remove(g.Eid);
        }

        /// <summary>Starts a leave on the agent when it holds the grid under the controller and not
        /// launching, as a local grid answers.</summary>
        public bool Leave(long eid, Vec3 fromKm, double clearM, string reason, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone || g.Last == null || !g.Last.Controlled || g.Last.Launching)
                return false;
            NexusFleetOrder o = Order(g, NexusFleetOp.Leave, fromKm, nowUtc);
            o.ClearM = clearM;
            o.Reason = reason;
            g.LeaveSeq = o.Seq;
            if (g.Ask != null) g.Ask.Combat = NexusCombat.None;
            return true;
        }

        /// <summary>True once the agent has applied the last leave and reports the grid clear, or
        /// no longer holds it; true for a gone grid, as for a missing local one.</summary>
        public bool IsLeaveDone(long eid)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return true;
            return g.LeaveSeq == 0 && (g.Last == null || !g.Last.Leaving);
        }

        public bool AskWing(long eid, long anchorId, long targetId, bool holding, bool breakingOff, TacticOrder order,
                            int slot, string label, IReadOnlyList<WeaponBand> fleetBands, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return false;
            NexusFleetAsk a = AskOf(g);
            a.Wing = true;
            a.WingAnchor = anchorId; a.WingTarget = targetId;
            a.WingHolding = holding; a.WingBreakingOff = breakingOff;
            a.WingOrder = order; a.WingSlot = slot; a.WingLabel = label;
            a.WingBands = fleetBands != null ? new List<WeaponBand>(fleetBands) : null;
            g.WingAskUtc = nowUtc;
            return g.Last != null && g.Last.Winged;
        }

        public void EndWing(long eid, Vec3 fleetTargetKm, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return;
            if (g.Ask != null) g.Ask.Wing = false;
            Order(g, NexusFleetOp.EndWing, fleetTargetKm, nowUtc);
        }

        /// <summary>Records this frame's launch ask and answers the agent's last word on it. A grid
        /// with no report since it spawned is taken as launching, which is what it is doing.</summary>
        public bool AskLaunch(long eid, Vec3 targetKm, bool flyThrough, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return false;
            NexusFleetAsk a = AskOf(g);
            a.Launch = true; a.Through = flyThrough;
            a.Lx = targetKm.X; a.Ly = targetKm.Y; a.Lz = targetKm.Z;
            g.LaunchAskUtc = nowUtc;
            return g.Last == null || g.Last.Launching;
        }

        public bool AskApproach(long eid, Vec3 targetKm, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return false;
            NexusFleetAsk a = AskOf(g);
            a.Approach = true;
            a.Ax = targetKm.X; a.Ay = targetKm.Y; a.Az = targetKm.Z;
            g.ApproachAskUtc = nowUtc;
            return g.Last != null && g.Last.Approaching;
        }

        /// <summary>Records this frame's formation ask for an escort and answers the agent's last
        /// word on whether its controller holds it.</summary>
        public bool AskEscort(long eid, long anchorEid, int slot, Vec3 fleetTargetKm, DateTime nowUtc)
        {
            Grid g;
            if (!_grids.TryGetValue(eid, out g) || g.Gone) return false;
            NexusFleetAsk a = AskOf(g);
            a.Escort = true; a.Anchor = anchorEid; a.Slot = slot;
            a.Ex = fleetTargetKm.X; a.Ey = fleetTargetKm.Y; a.Ez = fleetTargetKm.Z;
            g.EscortAskUtc = nowUtc;
            return g.Last != null && g.Last.Escorting;
        }

        /// <summary>Ends the formation ask at once rather than when it goes stale.</summary>
        public void StopEscort(long eid)
        {
            Grid g;
            if (_grids.TryGetValue(eid, out g) && g.Ask != null) g.Ask.Escort = false;
        }

        // ---- the exchange ------------------------------------------------------------------

        /// <summary>What a reply to <paramref name="home"/> carries.</summary>
        public void FillReply(byte home, DateTime nowUtc, out List<NexusFleetOrder> orders,
                              out List<NexusFleetAsk> asks, out List<long> held)
        {
            List<NexusFleetBands> bands;
            FillReply(home, nowUtc, out orders, out asks, out held, out bands);
        }

        public void FillReply(byte home, DateTime nowUtc, out List<NexusFleetOrder> orders,
                              out List<NexusFleetAsk> asks, out List<long> held, out List<NexusFleetBands> bands)
        {
            orders = null; asks = null; held = null; bands = null;
            Home h;
            if (!_homes.TryGetValue(home, out h)) return;
            if (h.Outbox.Count > 0) orders = new List<NexusFleetOrder>(h.Outbox);
            foreach (Grid g in _grids.Values)
            {
                if (g.Home != home || g.Gone || g.Transit) continue;
                if (held == null) held = new List<long>();
                held.Add(g.Eid);
                IReadOnlyList<WeaponBand> own = BandsOf != null ? BandsOf(g.Eid) : null;
                if (own != null && own.Count > 0)
                    (bands ?? (bands = new List<NexusFleetBands>())).Add(new NexusFleetBands { Eid = g.Eid, Bands = new List<WeaponBand>(own) });
                if (g.Ask == null) continue;
                NexusFleetAsk a = g.Ask;
                bool launch = a.Launch && nowUtc - g.LaunchAskUtc <= AskFresh;
                bool approach = a.Approach && nowUtc - g.ApproachAskUtc <= AskFresh;
                bool escort = a.Escort && nowUtc - g.EscortAskUtc <= AskFresh;
                bool hold = a.Hold && nowUtc - g.HoldAskUtc <= AskFresh;
                NexusCombat combat = nowUtc - g.CombatAskUtc <= AskFresh ? a.Combat : NexusCombat.None;
                bool wing = a.Wing && nowUtc - g.WingAskUtc <= AskFresh;
                if (!launch && !approach && !escort && !hold && combat == NexusCombat.None && !wing) continue;
                var sent = new NexusFleetAsk
                {
                    Eid = g.Eid, Launch = launch, Through = launch && a.Through,
                    Lx = a.Lx, Ly = a.Ly, Lz = a.Lz,
                    Approach = approach, Ax = a.Ax, Ay = a.Ay, Az = a.Az,
                    Escort = escort, Anchor = escort ? a.Anchor : 0, Slot = escort ? a.Slot : 0,
                    Ex = a.Ex, Ey = a.Ey, Ez = a.Ez,
                    Hold = hold, Combat = combat, Wing = wing,
                };
                if (wing)
                {
                    sent.WingAnchor = a.WingAnchor; sent.WingTarget = a.WingTarget;
                    sent.WingHolding = a.WingHolding; sent.WingBreakingOff = a.WingBreakingOff;
                    sent.WingOrder = a.WingOrder; sent.WingSlot = a.WingSlot; sent.WingLabel = a.WingLabel;
                    sent.WingBands = a.WingBands;
                }
                if (combat != NexusCombat.None)
                {
                    sent.Hx = a.Hx; sent.Hy = a.Hy; sent.Hz = a.Hz; sent.Label = a.Label;
                    if (combat == NexusCombat.Fight)
                    {
                        sent.FightBands = a.FightBands; sent.Aimed = a.Aimed; sent.Scope = a.Scope;
                        sent.Tactic = a.Tactic; sent.Learned = a.Learned; sent.Pursue = a.Pursue;
                        if (OpponentsOf != null)
                        {
                            var found = new HashSet<long>();
                            sent.OpponentsLabel = OpponentsOf(a.Label, found);
                            if (found.Count > 0) sent.Opponents = new List<long>(found);
                        }
                    }
                    else
                    {
                        sent.ShadowTarget = a.ShadowTarget; sent.Standoff = a.Standoff; sent.Leash = a.Leash;
                    }
                }
                (asks ?? (asks = new List<NexusFleetAsk>())).Add(sent);
            }
        }

        /// <summary>Takes an agent's report. Returns the fleet keys whose spawn finished.</summary>
        public List<string> Apply(byte home, string session, bool fleets, IList<NexusFleetGrid> grids,
                                  IList<string> spawnFailed, IList<long> closed, long ordersThrough, DateTime nowUtc)
        {
            return Apply(home, new NexusAgentReport
            {
                Session = session, Fleets = fleets, Grids = grids != null ? new List<NexusFleetGrid>(grids) : null,
                SpawnFailed = spawnFailed != null ? new List<string>(spawnFailed) : null,
                Closed = closed != null ? new List<long>(closed) : null, OrdersThrough = ordersThrough,
            }, nowUtc);
        }

        public List<string> Apply(byte home, NexusAgentReport report, DateTime nowUtc)
        {
            string session = report.Session;
            bool fleets = report.Fleets;
            List<NexusFleetGrid> grids = report.Grids;
            List<string> spawnFailed = report.SpawnFailed;
            List<long> closed = report.Closed;
            long ordersThrough = report.OrdersThrough;
            TimeSpan skew = report.ClockUtc == default(DateTime) ? TimeSpan.Zero : nowUtc - report.ClockUtc;
            var finished = new List<string>();
            Home h = HomeOf(home);
            if (h.Session != null && !string.Equals(h.Session, session, StringComparison.Ordinal))
                Lose(home, h, nowUtc, finished);
            h.Session = session;
            h.LastUtc = nowUtc;
            h.Fleets = fleets;
            h.Outbox.RemoveAll(o => o.Seq <= ordersThrough);

            var present = new HashSet<long>();
            if (grids != null)
                foreach (NexusFleetGrid r in grids)
                {
                    if (r == null || r.Eid <= 0) continue;
                    present.Add(r.Eid);
                    if (r.Damaged != DateTime.MinValue) r.Damaged += skew;
                    if (r.Hit != DateTime.MinValue)
                    {
                        r.Hit += skew;
                        if (r.HitBy != 0)
                            _contacts[r.HitBy] = new Contact
                            {
                                PosKm = r.HitByPlaced ? new Vec3(r.HitByX, r.HitByY, r.HitByZ) : (Vec3?)null,
                                Owner = r.HitByOwner, Tag = r.HitByTag, SeenUtc = nowUtc,
                            };
                    }
                    Grid g;
                    if (_grids.TryGetValue(r.Eid, out g))
                    {
                        if (g.Home == home && !g.Gone)
                        {
                            g.Last = r;
                            if (g.LeaveSeq != 0 && ordersThrough >= g.LeaveSeq) g.LeaveSeq = 0;
                        }
                        else if (g.Home != home && !g.Gone && string.Equals(r.Key, g.Key, StringComparison.Ordinal))
                            Rehome(g, home, r, nowUtc);
                        continue;
                    }
                    Expected x;
                    if (r.Key != null && _expected.TryGetValue(r.Eid, out x)
                        && string.Equals(r.Key, x.Template.Key, StringComparison.Ordinal))
                    {
                        _expected.Remove(r.Eid);
                        if (r.Prefab == null) r.Prefab = x.Template.Prefab;
                        var arrived = new Grid
                        {
                            Home = home, Key = r.Key, Eid = r.Eid, Tag = x.Template.Tag, Last = r,
                            Template = x.Template, DamagedFloor = x.Damaged,
                        };
                        _grids[r.Eid] = arrived;
                        QueueAdopt(arrived, nowUtc);
                        continue;
                    }
                    NexusFleetOrder spawn;
                    if (r.Key == null || !h.Spawning.TryGetValue(r.Key, out spawn)) continue;   // not ours: the agent closes it
                    h.Spawning.Remove(r.Key);
                    h.SpawnedUtc.Remove(r.Key);
                    if (r.Prefab == null) r.Prefab = spawn.Prefab;
                    _grids[r.Eid] = new Grid { Home = home, Key = r.Key, Eid = r.Eid, Tag = spawn.Tag, Last = r, Template = spawn };
                    _completed[r.Key] = r.Eid;
                    finished.Add(r.Key);
                }
            if (spawnFailed != null)
                foreach (string key in spawnFailed)
                    if (key != null && h.Spawning.Remove(key)) { h.SpawnedUtc.Remove(key); _completed[key] = 0; finished.Add(key); }
            if (closed != null)
                foreach (long eid in closed)
                {
                    Grid g;
                    if (_grids.TryGetValue(eid, out g) && g.Home == home) g.ClosedByUs = true;
                }
            HashSet<long> handedOff = report.HandedOff != null && report.HandedOff.Count > 0 ? new HashSet<long>(report.HandedOff) : null;
            foreach (Grid g in _grids.Values)
            {
                if (g.Home != home || g.Gone || g.Transit || present.Contains(g.Eid)) continue;
                if (handedOff != null && handedOff.Contains(g.Eid) && !g.ClosedByUs)
                {
                    g.Transit = true;
                    g.TransitUtc = nowUtc;
                    g.Ask = null;
                    continue;
                }
                MarkGone(g, g.ClosedByUs, nowUtc);
            }
            if (report.Munitions != null)
            {
                Dictionary<string, Munitions> byId = MunitionsOf(home);
                foreach (NexusMunitionsTotal t in report.Munitions)
                {
                    if (t == null || t.Id == null) continue;
                    Munitions m;
                    if (!byId.TryGetValue(t.Id, out m)) { m = new Munitions(); byId[t.Id] = m; }
                    m.Live = t.Live; m.Out = t.Out;
                    if (t.Closed != null)
                        foreach (KeyValuePair<string, long> kv in t.Closed)
                        {
                            long had;
                            if (!m.Closed.TryGetValue(kv.Key, out had) || kv.Value > had) m.Closed[kv.Key] = kv.Value;
                        }
                }
                foreach (KeyValuePair<string, Munitions> kv in byId)
                {
                    bool listed = false;
                    foreach (NexusMunitionsTotal t in report.Munitions) if (t != null && t.Id == kv.Key) { listed = true; break; }
                    if (!listed) kv.Value.Live = false;
                }
            }
            else if (_munitions.ContainsKey(home))
                foreach (Munitions m in _munitions[home].Values) m.Live = false;
            return finished;
        }

        /// <summary>Writes off every agent silent past <see cref="MaxSilence"/>, gives up spawns
        /// unanswered past <see cref="SpawnTimeout"/>, and forgets grids gone past
        /// <see cref="GoneMemory"/>. Returns the fleet keys whose spawn was abandoned.</summary>
        public List<string> Expire(DateTime nowUtc)
        {
            var abandoned = new List<string>();
            foreach (Home h in _homes.Values)
            {
                if (h.RespotUtc.Count > 0)
                {
                    List<string> done = null;
                    foreach (string key in h.RespotUtc.Keys) if (!h.Spawning.ContainsKey(key)) (done ?? (done = new List<string>())).Add(key);
                    if (done != null) foreach (string key in done) h.RespotUtc.Remove(key);
                }
                List<string> late = null;
                foreach (KeyValuePair<string, DateTime> kv in h.SpawnedUtc)
                    if (nowUtc - kv.Value > SpawnTimeout) (late ?? (late = new List<string>())).Add(kv.Key);
                if (late == null) continue;
                foreach (string key in late)
                {
                    h.Spawning.Remove(key);
                    h.SpawnedUtc.Remove(key);
                    _completed[key] = 0;
                    abandoned.Add(key);
                }
            }
            foreach (KeyValuePair<byte, Home> kv in _homes)
                if (nowUtc - kv.Value.LastUtc > MaxSilence && (kv.Value.Session != null || kv.Value.Spawning.Count > 0))
                {
                    Lose(kv.Key, kv.Value, nowUtc, abandoned);
                    kv.Value.Session = null;
                    kv.Value.Fleets = false;
                }
            foreach (Grid g in _grids.Values)
                if (g.Transit && !g.Gone && nowUtc - g.TransitUtc > TransitHold)
                {
                    g.Transit = false;
                    MarkGone(g, false, nowUtc);
                }
            List<long> lapsed = null;
            foreach (KeyValuePair<long, Expected> kv in _expected)
                if (nowUtc - kv.Value.SinceUtc > TransitHold) (lapsed ?? (lapsed = new List<long>())).Add(kv.Key);
            if (lapsed != null) foreach (long eid in lapsed) _expected.Remove(eid);
            List<long> forget = null;
            foreach (Grid g in _grids.Values)
                if (g.Gone && nowUtc - g.GoneUtc > GoneMemory) (forget ?? (forget = new List<long>())).Add(g.Eid);
            if (forget != null) foreach (long eid in forget) _grids.Remove(eid);
            List<long> stale = null;
            foreach (KeyValuePair<long, Contact> kv in _contacts)
                if (nowUtc - kv.Value.SeenUtc > ContactMemory) (stale ?? (stale = new List<long>())).Add(kv.Key);
            if (stale != null) foreach (long eid in stale) _contacts.Remove(eid);
            return abandoned;
        }

        // ---- internals ---------------------------------------------------------------------

        private Dictionary<string, Munitions> MunitionsOf(byte home)
        {
            Dictionary<string, Munitions> byId;
            if (!_munitions.TryGetValue(home, out byId))
            {
                byId = new Dictionary<string, Munitions>(StringComparer.Ordinal);
                _munitions[home] = byId;
            }
            return byId;
        }

        private Home HomeOf(byte home)
        {
            Home h;
            if (!_homes.TryGetValue(home, out h)) { h = new Home { LastUtc = DateTime.MinValue }; _homes[home] = h; }
            return h;
        }

        private void Queue(Home h, NexusFleetOrder order, DateTime nowUtc)
        {
            _lastSeq = Math.Max(_lastSeq + 1, nowUtc.Ticks);
            order.Seq = _lastSeq;
            h.Outbox.Add(order);
        }

        private NexusFleetOrder Order(Grid g, NexusFleetOp op, Vec3 target, DateTime nowUtc)
        {
            var o = new NexusFleetOrder { Op = op, Key = g.Key, Eid = g.Eid, Tx = target.X, Ty = target.Y, Tz = target.Z };
            Queue(HomeOf(g.Home), o, nowUtc);
            return o;
        }

        private static NexusFleetAsk AskOf(Grid g)
        {
            if (g.Ask == null) g.Ask = new NexusFleetAsk { Eid = g.Eid };
            return g.Ask;
        }

        private static void MarkGone(Grid g, bool closedByUs, DateTime nowUtc)
        {
            g.Gone = true;
            g.ClosedByUs = closedByUs;
            g.GoneUtc = nowUtc;
            g.Ask = null;
        }

        /// <summary>The agent's grids are gone with its process: closed by us, never shot down.</summary>
        private void Lose(byte home, Home h, DateTime nowUtc, List<string> finished)
        {
            // One in transit has left this server already: its new home settles it.
            foreach (Grid g in _grids.Values)
                if (g.Home == home && !g.Gone && !g.Transit) MarkGone(g, true, nowUtc);
            foreach (string key in h.Spawning.Keys) { _completed[key] = 0; finished.Add(key); }
            h.Spawning.Clear();
            h.SpawnedUtc.Clear();
            h.Outbox.Clear();
            // Its totals restart with the process; what the old one had not reported is lost with it.
            _munitions.Remove(home);
        }
    }

    /// <summary>The agent's half of the order stream: each order applied once, in order.</summary>
    public sealed class NexusOrderInbox
    {
        /// <summary>The last order applied; the report acks through it.</summary>
        public long Through { get; private set; }

        public List<NexusFleetOrder> Accept(IList<NexusFleetOrder> orders)
        {
            var fresh = new List<NexusFleetOrder>();
            if (orders == null) return fresh;
            foreach (NexusFleetOrder o in orders) if (o != null && o.Seq > Through) fresh.Add(o);
            fresh.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            if (fresh.Count > 0) Through = fresh[fresh.Count - 1].Seq;
            return fresh;
        }
    }
}
