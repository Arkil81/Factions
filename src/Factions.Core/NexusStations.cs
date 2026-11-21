using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// One Factions station standing on a Nexus agent, as the agent reports it in every
    /// <see cref="NexusAgentReport"/> until the authority acknowledges its close. State, not events:
    /// every field is the agent's current view, so a lost or repeated frame changes nothing.
    /// </summary>
    public sealed class NexusStationState
    {
        public string Node;
        /// <summary>The agent's id for this standing of the station; a re-stand gets a new one.</summary>
        public long Stand;
        public bool Closed;

        /// <summary>What the station's guns hold when full, read on the grid. The authority sizes the
        /// load from the design's munition data and falls back to this.</summary>
        public Dictionary<string, long> AmmoCap;
        /// <summary>The newest ammunition grant the agent has loaded (0: none).</summary>
        public long AmmoThrough;
        /// <summary>What of grant <see cref="AmmoThrough"/> found no room aboard.</summary>
        public Dictionary<string, long> AmmoUnplaced;
        /// <summary>The magazines aboard at the agent's last read, taken after loading
        /// <see cref="AmmoThrough"/>. Null before the first read.</summary>
        public Dictionary<string, long> AmmoSeen;

        /// <summary>Per loot block, what its table rolled. Sent until the loot grant arrives; null
        /// when the station has no loot blocks or loot is not stock-fed.</summary>
        public List<Dictionary<string, long>> LootWants;
        public long LootThrough;
        public Dictionary<string, long> LootUnplaced;
        /// <summary>The loot blocks' contents at close. Only meaningful with <see cref="Closed"/>.</summary>
        public Dictionary<string, long> LootSeen;
    }

    /// <summary>The authority's answer for one station: the grants the agent has not yet loaded,
    /// re-sent until it has, and the acknowledgement of a close.</summary>
    public sealed class NexusStationGrant
    {
        public string Node;
        public long Stand;
        public bool CloseAcked;
        public long AmmoGrant;
        public Dictionary<string, long> Ammo;
        public long LootGrant;
        /// <summary>Per loot block, in the order of <see cref="NexusStationState.LootWants"/>.</summary>
        public List<Dictionary<string, long>> Loot;
    }

    /// <summary>Authority to agent, relay kind 7: answers to the stations in one report.</summary>
    public sealed class NexusStationReply
    {
        public const int CurrentVersion = 1;
        public int Version = CurrentVersion;
        public List<NexusStationGrant> Grants = new List<NexusStationGrant>();
        /// <summary>Siege events applied through this sequence number; 0 when none were sent.</summary>
        public long SiegeThrough;
        /// <summary>Wallet transfers booked through this sequence number; 0 when none were sent.</summary>
        public long TransfersThrough;
        /// <summary>Store events booked through this sequence number; 0 when none were sent.</summary>
        public long StoreThrough;
        /// <summary>DecisionLog lines written through this sequence number; 0 when none were sent.</summary>
        public long LogThrough;
        /// <summary>Store contents for standing stations, each replacing what that store carries.</summary>
        public List<NexusShelf> Shelves;
        /// <summary>Every standing station off the default zone state; the rest are on and untouched.</summary>
        public List<NexusZoneState> Zones;
        public List<NexusNotice> Notices;
        /// <summary>Fleet orders not yet acked, oldest first.</summary>
        public List<NexusFleetOrder> Orders;
        /// <summary>Launch and approach asks made of this agent's grids over the last few seconds.</summary>
        public List<NexusFleetAsk> Asks;
        /// <summary>Every fleet grid the authority holds on this agent; the agent closes any other.</summary>
        public List<long> Held;
        /// <summary>Weapon bands of the held grids that have any: the flight controller reads a
        /// target's bands from these, as it does on the authority.</summary>
        public List<NexusFleetBands> Bands;
        /// <summary>Station traffic of the last few seconds not yet handed to this agent, for its
        /// players' traffic voice; null when there is none or nobody there to hear it.</summary>
        public List<NexusTrafficCall> Traffic;
        /// <summary>Fights resolved over the last few seconds not yet handed to this agent, for the
        /// combat notices of its players near them; null when there are none or nobody there.</summary>
        public List<NexusCombatCall> Combat;
        /// <summary>Node stock to carry into or out of a block for a player connected to this agent,
        /// re-sent until the agent's report acknowledges it.</summary>
        public List<NexusStockMove> StockMoves;
        public bool IsEmpty
        {
            get
            {
                return (Grants == null || Grants.Count == 0) && SiegeThrough == 0 && TransfersThrough == 0 && StoreThrough == 0
                    && LogThrough == 0
                    && (Shelves == null || Shelves.Count == 0)
                    && (Zones == null || Zones.Count == 0) && (Notices == null || Notices.Count == 0)
                    && (Orders == null || Orders.Count == 0) && (Held == null || Held.Count == 0)
                    && (Traffic == null || Traffic.Count == 0)
                    && (Combat == null || Combat.Count == 0)
                    && (StockMoves == null || StockMoves.Count == 0);
            }
        }

        public byte[] Encode() { return NexusHandsCodec.Encode(this, NexusHandsCodec.Lean); }

        public static bool TryDecode(byte[] payload, out NexusStationReply reply)
        {
            reply = NexusHandsCodec.Decode<NexusStationReply>(payload, NexusHandsCodec.Lean);
            if (reply == null || reply.Version != CurrentVersion) { reply = null; return false; }
            if (reply.Grants == null) reply.Grants = new List<NexusStationGrant>();
            return true;
        }
    }

    /// <summary>What applying one report did, for the authority's log.</summary>
    public struct NexusStationTally
    {
        public int Stood, Closed, Stranded;
        public long AmmoDrawn, AmmoSpent, AmmoBack, LootDrawn, LootBack;
    }

    /// <summary>
    /// The authority's side of the station economy on Nexus agents: the munitions and loot rules of
    /// <see cref="StationMunitions"/> and <see cref="StationLoot"/>, applied to stations that stand on
    /// another server.
    ///
    /// <para><b>Grants.</b> Stock is drawn here and handed to the agent as a numbered grant. A draw
    /// raises the node's record at once; the agent loads the grant and reports, in its next state,
    /// that it has (<c>Through</c>) and what found no room (<c>Unplaced</c>), which is returned to
    /// stock once. Until then the grant is re-sent and no further draw is made, so an observation
    /// is never read against a record holding magazines the grid has not been given.</para>
    ///
    /// <para><b>Restarts.</b> An agent that restarts reports a new session; its grids did not survive,
    /// so every station it held is credited back in full. An authority restart forgets the ledger:
    /// a station that reports a grant this ledger did not issue is taken as newly stood, its unplaced
    /// part ignored (the restart credit already counted it), and its load drawn up from what it reads.
    /// What it held when the authority stopped is aboard and in stock both until it closes, at most
    /// one load per station per restart.</para>
    /// </summary>
    public sealed class NexusStationLedger
    {
        private sealed class Record
        {
            public byte Home;
            public long Stand;
            public Dictionary<string, long> Target;
            public long AmmoIssued;
            public Dictionary<string, long> AmmoPool;
            public long AmmoSettled;
            public DateTime AmmoDrawnUtc;
            public long LootIssued;
            public List<Dictionary<string, long>> LootPools;
            public long LootSettled;
        }

        private readonly Dictionary<string, Record> _byNode = new Dictionary<string, Record>(StringComparer.Ordinal);
        private readonly Dictionary<byte, string> _sessionByHome = new Dictionary<byte, string>();
        private long _lastGrant;

        /// <summary>Stations this ledger holds standing, over every agent.</summary>
        public int Count { get { return _byNode.Count; } }

        public int CountFor(byte home)
        {
            int n = 0;
            foreach (Record r in _byNode.Values) if (r.Home == home) n++;
            return n;
        }

        /// <summary>Applies an agent's station states and returns the reply owed to it (empty when
        /// nothing is outstanding). <paramref name="munitions"/> and <paramref name="loot"/> are the
        /// authority's own gates: with one off, that half draws nothing.</summary>
        public NexusStationReply Apply(byte home, string session, IList<NexusStationState> states, WorldModel world,
                                       bool munitions, bool loot, TimeSpan topUpInterval, DateTime nowUtc,
                                       out NexusStationTally tally)
        {
            tally = new NexusStationTally();
            var reply = new NexusStationReply();
            if (world == null) return reply;

            string held;
            if (session != null && (!_sessionByHome.TryGetValue(home, out held) || held != session))
            {
                if (held != null) tally.Stranded += StrandHome(home, world, ref tally);
                _sessionByHome[home] = session;
            }
            if (states == null) return reply;

            for (int i = 0; i < states.Count; i++)
            {
                NexusStationState s = states[i];
                if (s == null || s.Node == null) continue;
                SpatialNode node;
                world.Graph.Nodes.TryGetValue(s.Node, out node);
                Record r;
                _byNode.TryGetValue(s.Node, out r);
                bool ours = r != null && r.Home == home && r.Stand == s.Stand;

                if (s.Closed)
                {
                    if (ours && node != null) Close(world, node, r, s, ref tally);
                    if (ours) _byNode.Remove(s.Node);
                    reply.Grants.Add(new NexusStationGrant { Node = s.Node, Stand = s.Stand, CloseAcked = true });
                    continue;
                }
                if (node == null) continue;

                if (!ours)
                {
                    // A record for another standing is a grid that went without a close reaching us.
                    if (r != null) { Strand(world, node, r, ref tally); tally.Stranded++; }
                    else
                    {
                        StationMunitions.CreditStranded(world, node);
                        StationLoot.CreditStale(world, node);
                    }
                    // Settled at zero whatever the agent has loaded: a grant this ledger did not
                    // issue (an authority restart) is never settled here, and the next draw is ours.
                    r = new Record { Home = home, Stand = s.Stand };
                    _byNode[s.Node] = r;
                    tally.Stood++;
                    StandLoot(world, node, r, s, loot, nowUtc, ref tally);
                }
                if (r.Target == null || r.Target.Count == 0)
                {
                    r.Target = StationMunitions.FullLoad(node, world.PrefabCatalogue, world.Tuning);
                    if (r.Target.Count == 0 && s.AmmoCap != null)
                        r.Target = new Dictionary<string, long>(s.AmmoCap, StringComparer.Ordinal);
                }

                SettleAmmo(world, node, r, s, ref tally);
                // AmmoCap is set only by an agent that stripped the guns for stock: a station it stood
                // with vanilla's own refill is not drawn for, whatever this side's gate says.
                if (munitions && s.AmmoCap != null && r.AmmoIssued == r.AmmoSettled
                    && (r.AmmoIssued == 0 || (s.AmmoSeen != null && nowUtc - r.AmmoDrawnUtc >= topUpInterval)))
                {
                    if (s.AmmoSeen != null) tally.AmmoSpent += StationMunitions.ApplyObserved(world, node, s.AmmoSeen);
                    Dictionary<string, long> pool = StationMunitions.Draw(world, node, r.Target, s.AmmoSeen);
                    r.AmmoIssued = NextGrant();
                    r.AmmoPool = pool;
                    r.AmmoDrawnUtc = nowUtc;
                    tally.AmmoDrawn += Sum(pool);
                }
                SettleLoot(world, node, r, s, ref tally);

                bool ammoOwed = r.AmmoIssued != 0 && r.AmmoIssued != s.AmmoThrough;
                bool lootOwed = r.LootIssued != 0 && r.LootIssued != s.LootThrough;
                if (ammoOwed || lootOwed)
                    reply.Grants.Add(new NexusStationGrant
                    {
                        Node = s.Node, Stand = s.Stand,
                        AmmoGrant = ammoOwed ? r.AmmoIssued : 0, Ammo = ammoOwed ? r.AmmoPool : null,
                        LootGrant = lootOwed ? r.LootIssued : 0, Loot = lootOwed ? r.LootPools : null,
                    });
            }
            return reply;
        }

        private void StandLoot(WorldModel world, SpatialNode node, Record r, NexusStationState s, bool loot,
                               DateTime nowUtc, ref NexusStationTally tally)
        {
            if (s.LootWants == null || s.LootThrough != 0) return;
            TimeSpan left;
            bool draw = loot && !StationLoot.Restocking(node, nowUtc, out left);
            var pools = new List<Dictionary<string, long>>(s.LootWants.Count);
            for (int i = 0; i < s.LootWants.Count; i++)
            {
                Dictionary<string, long> drawn = draw && s.LootWants[i] != null
                    ? StationLoot.Draw(world, node, s.LootWants[i], world.Tuning)
                    : new Dictionary<string, long>(StringComparer.Ordinal);
                tally.LootDrawn += Sum(drawn);
                pools.Add(drawn);
            }
            r.LootPools = pools;
            r.LootIssued = NextGrant();
        }

        private static void SettleAmmo(WorldModel world, SpatialNode node, Record r, NexusStationState s, ref NexusStationTally tally)
        {
            if (r.AmmoIssued == 0 || s.AmmoThrough != r.AmmoIssued || r.AmmoSettled == r.AmmoIssued) return;
            if (s.AmmoUnplaced != null) tally.AmmoBack += StationMunitions.ReturnUnplaced(world, node, s.AmmoUnplaced);
            r.AmmoSettled = r.AmmoIssued;
            r.AmmoPool = null;
        }

        private static void SettleLoot(WorldModel world, SpatialNode node, Record r, NexusStationState s, ref NexusStationTally tally)
        {
            if (r.LootIssued == 0 || s.LootThrough != r.LootIssued || r.LootSettled == r.LootIssued) return;
            if (s.LootUnplaced != null) tally.LootBack += StationLoot.ReturnUnplaced(world, node, s.LootUnplaced);
            r.LootSettled = r.LootIssued;
            r.LootPools = null;
        }

        /// <summary>A close: whatever grant the agent never loaded goes back, then the final read is
        /// applied and the rest credited, as a despawn on the authority does.</summary>
        private static void Close(WorldModel world, SpatialNode node, Record r, NexusStationState s, ref NexusStationTally tally)
        {
            SettleAmmo(world, node, r, s, ref tally);
            SettleLoot(world, node, r, s, ref tally);
            if (r.AmmoIssued != r.AmmoSettled && r.AmmoPool != null)
                tally.AmmoBack += StationMunitions.ReturnUnplaced(world, node, r.AmmoPool);
            if (r.LootIssued != r.LootSettled && r.LootPools != null)
                foreach (Dictionary<string, long> pool in r.LootPools)
                    tally.LootBack += StationLoot.ReturnUnplaced(world, node, pool);
            long spent, taken;
            tally.AmmoBack += StationMunitions.Credit(world, node, s.AmmoSeen, out spent);
            tally.AmmoSpent += spent;
            if (node.StationLoot != null)
                tally.LootBack += StationLoot.Credit(world, node, s.LootSeen, out taken);
            tally.Closed++;
        }

        private static void Strand(WorldModel world, SpatialNode node, Record r, ref NexusStationTally tally)
        {
            tally.AmmoBack += StationMunitions.CreditStranded(world, node);
            tally.LootBack += StationLoot.CreditStale(world, node);
        }

        private int StrandHome(byte home, WorldModel world, ref NexusStationTally tally)
        {
            var gone = new List<string>();
            foreach (KeyValuePair<string, Record> kv in _byNode)
            {
                if (kv.Value.Home != home) continue;
                gone.Add(kv.Key);
                SpatialNode node;
                if (world.Graph.Nodes.TryGetValue(kv.Key, out node)) Strand(world, node, kv.Value, ref tally);
            }
            for (int i = 0; i < gone.Count; i++) _byNode.Remove(gone[i]);
            return gone.Count;
        }

        /// <summary>Clock-seeded so a restarted authority never re-issues an id an agent has loaded.</summary>
        private long NextGrant()
        {
            _lastGrant = Math.Max(_lastGrant + 1, DateTime.UtcNow.Ticks);
            return _lastGrant;
        }

        private static long Sum(Dictionary<string, long> d)
        {
            long t = 0;
            if (d != null) foreach (KeyValuePair<string, long> kv in d) if (kv.Value > 0) t += kv.Value;
            return t;
        }
    }
}
