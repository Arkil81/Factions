using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

namespace Factions.Core
{
    /// <summary>One node as a Nexus agent needs it to materialise what stands there. Short names:
    /// the snapshot goes to every agent on a cadence.</summary>
    public sealed class NexusSnapshotNode
    {
        public string Id;
        public double X, Y, Z;
        public int Zone;
        /// <summary><see cref="StationSite"/> as its integer: respawn placement's tiers.</summary>
        public int Site;
        /// <summary>The node's display name, for a respawn waypoint.</summary>
        public string Name;
        public string Planet;
        public string Owner;
        public long Station;
        public string Prefab;
        public string PendingPrefab;
        public DateTime Breached;
        public DateTime ZoneDownUntil;
        /// <summary>Siege pacing stamps: an agent's fleet grids pace their fire on a station's zone
        /// from these, as the authority's do.</summary>
        public DateTime SiegeStarted, ZoneIntegrityAt;
        public double Value;
        public int AsteroidSeed;
        public DateTime LootRestock;
        /// <summary>Null when the node has no deposit; empty when it is barren. Richest first, as
        /// <see cref="NodeDeposit.Entries"/> holds them.</summary>
        public List<NexusSnapshotOre> Ores;
        /// <summary>The authority's SE record for the node's station, filled by the host; null when it
        /// holds none. Nexus copies station records between servers only at a save sync, and never a
        /// player faction's, so an agent keeps its own from this.</summary>
        public NexusStationRecord Record;
        /// <summary>A player faction's node: its hull stock, and the hulls of it listed for sale;
        /// null when it holds none, and always null for an NPC's node.</summary>
        public Dictionary<string, int> Hulls;
        public Dictionary<string, int> Listed;
    }

    /// <summary>What an agent needs to rebuild a faction station record as the authority holds it.</summary>
    public sealed class NexusStationRecord
    {
        /// <summary>SE-facing prefab name, which for a scanned design is a vanilla stand-in.</summary>
        public string Se;
        /// <summary>SE's station type enum, as its integer.</summary>
        public int Type;
        public string Name;
        /// <summary>Position in metres, and the record's up and forward.</summary>
        public double X, Y, Z;
        public double Ux, Uy, Uz;
        public double Fx, Fy, Fz;
    }

    public sealed class NexusSnapshotOre
    {
        public string Ore;
        public double W;
    }

    public sealed class NexusSnapshotFaction
    {
        public string Tag;
        public bool Player;
        public string Personality;
        /// <summary>The registered factions whose members this one refuses at its stations, as
        /// <see cref="StoreAccess.Decide"/> judges them on the authority; null when none.</summary>
        public List<NexusTradeBar> Bars;
    }

    /// <summary>One customer faction a station owner refuses, with what the customer is told.</summary>
    public sealed class NexusTradeBar
    {
        public string Tag;
        public string Message;
        public string Cause;
    }

    /// <summary>
    /// What the authority sends every agent so it can run station and rock materialisation for its
    /// own sectors. No sector routing: every materialiser is triggered by local player positions,
    /// and an agent's players are in that agent's sectors, so the whole graph goes to everyone and
    /// each uses the part its players reach.
    ///
    /// <para>JSON then gzip. Built and decoded off nothing but Core types, so the same codec runs on
    /// both ends and in tests.</para>
    /// </summary>
    public sealed class NexusNodeSnapshot
    {
        /// <summary>Bumped on any change an older reader would misread.</summary>
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        /// <summary>The authority's send counter; an agent ignores a snapshot older than the one it holds.</summary>
        public long Sequence;
        public List<NexusSnapshotNode> Nodes = new List<NexusSnapshotNode>();
        public List<NexusSnapshotFaction> Factions = new List<NexusSnapshotFaction>();
        /// <summary>The authority's gates: whether stations' guns are fed from stock, and their loot.
        /// An agent strips and clears only what the authority will refill.</summary>
        public bool Munitions;
        public bool Loot;
        /// <summary>The authority's capture generation: an agent that sees it change reloads the
        /// captured designs it spawns from.</summary>
        public int Captures;

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        public static NexusNodeSnapshot Build(IEnumerable<SpatialNode> nodes, IEnumerable<FactionState> factions, long sequence)
        {
            var s = new NexusNodeSnapshot { Sequence = sequence };
            var players = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (factions != null)
                foreach (FactionState f in factions)
                    if (f != null && f.Tag != null && f.IsPlayerFaction) players.Add(f.Tag);
            if (nodes != null)
                foreach (SpatialNode n in nodes)
                {
                    if (n == null || n.Id == null) continue;
                    var row = new NexusSnapshotNode
                    {
                        Id = n.Id, X = n.Position.X, Y = n.Position.Y, Z = n.Position.Z,
                        Zone = (int)n.Zone, Site = (int)n.Site, Name = n.DisplayName,
                        Planet = n.PlanetId, Owner = n.OwnerFactionTag,
                        Station = n.StableStationId, Prefab = n.StationPrefabId,
                        PendingPrefab = n.PendingStationPrefabId,
                        Breached = n.ZoneBreachedUtc, ZoneDownUntil = n.SafeZoneDownUntilUtc,
                        SiegeStarted = n.SiegeStartedUtc, ZoneIntegrityAt = n.ZoneIntegrityAtUtc,
                        Value = n.ResourceValue, AsteroidSeed = n.AsteroidFieldSeed,
                        LootRestock = n.LootRestockUtc,
                    };
                    if (n.OwnerFactionTag != null && players.Contains(n.OwnerFactionTag))
                    {
                        if (n.ShipStock.Count > 0) row.Hulls = new Dictionary<string, int>(n.ShipStock, StringComparer.OrdinalIgnoreCase);
                        if (n.ListedForSale.Count > 0) row.Listed = new Dictionary<string, int>(n.ListedForSale, StringComparer.OrdinalIgnoreCase);
                    }
                    if (n.Deposit != null)
                    {
                        row.Ores = new List<NexusSnapshotOre>(n.Deposit.Entries.Count);
                        foreach (DepositEntry e in n.Deposit.Entries)
                            row.Ores.Add(new NexusSnapshotOre { Ore = e.SubTypeId, W = e.Weight });
                    }
                    s.Nodes.Add(row);
                }
            if (factions != null)
                foreach (FactionState f in factions)
                {
                    if (f == null || f.Tag == null) continue;
                    s.Factions.Add(new NexusSnapshotFaction { Tag = f.Tag, Player = f.IsPlayerFaction, Personality = f.PersonalityId });
                }
            return s;
        }

        /// <summary>Records, on each NPC faction's row, the registered factions it refuses at its
        /// stations: every pair <see cref="StoreAccess.Decide"/> refuses. A customer in no registered
        /// faction is judged on the agent by their own reputation, which SE carries there.</summary>
        public static void FillTradeBars(NexusNodeSnapshot snapshot, IList<FactionState> factions, FactionTuning tuning)
        {
            if (snapshot == null || factions == null) return;
            var byTag = new Dictionary<string, FactionState>(StringComparer.OrdinalIgnoreCase);
            foreach (FactionState f in factions) if (f != null && f.Tag != null) byTag[f.Tag] = f;
            foreach (NexusSnapshotFaction row in snapshot.Factions)
            {
                FactionState owner;
                if (row == null || row.Player || row.Tag == null || !byTag.TryGetValue(row.Tag, out owner)) continue;
                row.Bars = null;
                foreach (FactionState customer in factions)
                {
                    if (customer == null || customer.Tag == null || customer == owner) continue;
                    StoreAccessDecision d = StoreAccess.Decide(owner, customer, null, tuning);
                    if (!d.Refused) continue;
                    if (row.Bars == null) row.Bars = new List<NexusTradeBar>();
                    row.Bars.Add(new NexusTradeBar { Tag = customer.Tag, Message = d.Message, Cause = d.Cause });
                }
            }
        }

        /// <summary>The nodes as a graph an agent's materialisers can read. Fresh objects each time:
        /// an agent replaces its graph whole rather than patching it.</summary>
        public SpatialGraph ToGraph()
        {
            var g = new SpatialGraph();
            foreach (NexusSnapshotNode row in Nodes)
            {
                if (row == null || row.Id == null) continue;
                SpatialNode n = g.Add(new Vec3(row.X, row.Y, row.Z), row.Value, row.Id);
                n.Zone = (NodeZone)row.Zone;
                n.Site = (StationSite)row.Site;
                n.DisplayName = row.Name;
                n.PlanetId = row.Planet;
                n.OwnerFactionTag = row.Owner;
                n.StableStationId = row.Station;
                n.StationPrefabId = row.Prefab;
                n.PendingStationPrefabId = row.PendingPrefab;
                n.ZoneBreachedUtc = row.Breached;
                n.SafeZoneDownUntilUtc = row.ZoneDownUntil;
                n.SiegeStartedUtc = row.SiegeStarted;
                n.ZoneIntegrityAtUtc = row.ZoneIntegrityAt;
                n.AsteroidFieldSeed = row.AsteroidSeed;
                n.LootRestockUtc = row.LootRestock;
                if (row.Hulls != null) foreach (KeyValuePair<string, int> h in row.Hulls) n.ShipStock[h.Key] = h.Value;
                if (row.Listed != null) foreach (KeyValuePair<string, int> h in row.Listed) n.ListedForSale[h.Key] = h.Value;
                if (row.Ores != null)
                {
                    n.Deposit = new NodeDeposit();
                    foreach (NexusSnapshotOre o in row.Ores)
                        n.Deposit.Entries.Add(new DepositEntry(o.Ore, o.W, 0));
                }
            }
            return g;
        }

        /// <summary>Faction stand-ins carrying only what materialisation reads.</summary>
        public List<FactionState> ToFactions()
        {
            var list = new List<FactionState>(Factions.Count);
            foreach (NexusSnapshotFaction f in Factions)
                if (f != null && f.Tag != null)
                    list.Add(new FactionState { Tag = f.Tag, IsPlayerFaction = f.Player, PersonalityId = f.Personality });
            return list;
        }

        public byte[] Encode()
        {
            return NexusHandsCodec.Encode(this, Settings);
        }

        public static bool TryDecode(byte[] payload, out NexusNodeSnapshot snapshot)
        {
            snapshot = NexusHandsCodec.Decode<NexusNodeSnapshot>(payload, Settings);
            if (snapshot == null || snapshot.Version != CurrentVersion) { snapshot = null; return false; }
            if (snapshot.Nodes == null) snapshot.Nodes = new List<NexusSnapshotNode>();
            if (snapshot.Factions == null) snapshot.Factions = new List<NexusSnapshotFaction>();
            return true;
        }
    }

    /// <summary>
    /// An agent's traffic budget, from its own frame. The authority plans the civilian traffic near
    /// that agent's players against it (<see cref="TrafficPresence"/>), because the agent's frame, not
    /// the authority's, pays for every fleet grid flown there.
    /// </summary>
    public sealed class NexusAgentLoad
    {
        /// <summary>What Factions fleets may cost there in all, ms per frame
        /// (<see cref="TrafficBudget.FactionsBudgetMs"/>).</summary>
        public double BudgetMs;
        /// <summary>Its learnt predicted-to-measured multipliers, per class.</summary>
        public double KCivilian = 1.0;
        public double KCombat = 1.0;
        /// <summary>Its load governor's Factions step; 0 when the governor is not acting.</summary>
        public int FactionsStep;
    }

    /// <summary>
    /// A player-owned grid on an agent as NPC sensors find it: the agent samples its signature with
    /// the same walk the authority runs on its own grids, and the authority's factions sense it
    /// (<see cref="PlayerContact"/>).
    /// </summary>
    public sealed class NexusPlayerContact
    {
        public long Identity;
        public long Grid;
        /// <summary>Position, km, to the metre.</summary>
        public double X, Y, Z;
        public double Signature;
        public double BroadcastKm;

        public PlayerContact ToContact()
        {
            return new PlayerContact
            {
                IdentityId = Identity, GridEntityId = Grid, PositionKm = new Vec3(X, Y, Z),
                Signature = Signature, BroadcastRadiusKm = BroadcastKm,
            };
        }

        public static NexusPlayerContact From(PlayerContact c)
        {
            return new NexusPlayerContact
            {
                Identity = c.IdentityId, Grid = c.GridEntityId,
                X = Math.Round(c.PositionKm.X, 3), Y = Math.Round(c.PositionKm.Y, 3), Z = Math.Round(c.PositionKm.Z, 3),
                Signature = Math.Round(c.Signature, 4), BroadcastKm = Math.Round(c.BroadcastRadiusKm, 3),
            };
        }
    }

    /// <summary>
    /// A player-owned grid standing on an agent, for the authority's assets window: a player's
    /// ships can be on any shard, and the authority's own sweep sees only its own.
    /// </summary>
    public sealed class NexusOwnedGrid
    {
        public long Id;
        public string Name;
        public bool Static;
        /// <summary>SE's grid size enum: 0 large, 1 small.</summary>
        public byte Size;
        public int Blocks;
        /// <summary>Position, metres, to the metre.</summary>
        public double X, Y, Z;
        /// <summary>The owning identity: big owners first, the first non-zero.</summary>
        public long Owner;
        /// <summary>The owner's display name as the agent knows it, for an owner the authority's
        /// identity table has not met.</summary>
        public string OwnerName;
    }

    /// <summary>
    /// The authority's latest owned-grid list from each agent. A report replaces that agent's
    /// list; an agent that stops reporting ages out whole, so a crashed shard's grids do not stay
    /// listed.
    /// </summary>
    public sealed class NexusOwnedGridBook
    {
        private readonly Dictionary<byte, KeyValuePair<List<NexusOwnedGrid>, DateTime>> _byHome =
            new Dictionary<byte, KeyValuePair<List<NexusOwnedGrid>, DateTime>>();

        public int HomeCount { get { return _byHome.Count; } }

        public void Update(byte homeServerId, List<NexusOwnedGrid> grids, DateTime nowUtc)
        {
            if (grids == null) return;
            _byHome[homeServerId] = new KeyValuePair<List<NexusOwnedGrid>, DateTime>(grids, nowUtc);
        }

        public void Clear() { _byHome.Clear(); }

        /// <summary>Every grid from an agent that reported within <paramref name="maxAge"/>.</summary>
        public List<NexusOwnedGrid> Fresh(DateTime nowUtc, TimeSpan maxAge)
        {
            var all = new List<NexusOwnedGrid>();
            foreach (KeyValuePair<byte, KeyValuePair<List<NexusOwnedGrid>, DateTime>> kv in _byHome)
            {
                if (nowUtc - kv.Value.Value > maxAge) continue;
                List<NexusOwnedGrid> list = kv.Value.Key;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].Owner != 0) all.Add(list[i]);
            }
            return all;
        }
    }

    /// <summary>
    /// What an agent tells the authority happened in its sectors. Every entry is idempotent on the
    /// authority (applying it twice changes nothing), so an agent re-sends until the snapshot shows
    /// it applied, and a lost frame costs a cadence, not a write.
    /// </summary>
    /// <summary>A player's display name as the server they are connected to holds it.</summary>
    public sealed class NexusPlayerName
    {
        public ulong Steam;
        public string Name;
    }

    public sealed class NexusAgentReport
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        /// <summary>Nodes whose asteroid field this agent placed. The authority stamps
        /// <see cref="AsteroidFieldLayout.FieldSeed"/> on any that still read zero.</summary>
        public List<string> AsteroidFields;
        /// <summary>The agent process's id. A new one means its grids are gone: every station the
        /// authority held standing for it is credited back.</summary>
        public string Session;
        /// <summary>Every station standing on the agent, and every close not yet acknowledged.</summary>
        public List<NexusStationState> Stations;
        /// <summary>Fire into stations not yet acknowledged, oldest first.</summary>
        public List<NexusSiegeEvent> Siege;
        /// <summary>Siege notices shown through this sequence number.</summary>
        public long NoticesThrough;
        /// <summary>Nodes whose station a player is at on the agent: the authority holds a finished
        /// refit for these, as it does for its own observed stations.</summary>
        public List<string> Observed;
        /// <summary>True when this agent flies fleet grids for the authority.</summary>
        public bool Fleets;
        /// <summary>Every fleet grid standing here, in full each report; one missing is gone.</summary>
        public List<NexusFleetGrid> Grids;
        /// <summary>Fleet keys whose spawn order could not be carried out.</summary>
        public List<string> SpawnFailed;
        /// <summary>Recently closed grids that were closed on an order, not shot down.</summary>
        public List<long> Closed;
        /// <summary>Recently gone grids that left at a position another server's sector holds: Nexus
        /// carried them across, and the authority holds them in transit for the server that receives
        /// them instead of reading them as lost.</summary>
        public List<long> HandedOff;
        /// <summary>Fleet orders applied through this sequence number.</summary>
        public long OrdersThrough;
        /// <summary>Node stock moves applied through this sequence number.</summary>
        public long StockMovesThrough;
        /// <summary>The agent's clock when it built this report. The authority shifts the report's
        /// hit and damage times by its own clock's difference, so a skewed shard clock does not
        /// move a hit in or out of a window.</summary>
        public DateTime ClockUtc;
        /// <summary>Per armed fleet: what its grids held when they were closed here, summed since
        /// this process started; and whether every live grid under it is out of ammunition.</summary>
        public List<NexusMunitionsTotal> Munitions;
        /// <summary>This server's own frame load, as its traffic budget reads it; null before its
        /// first measured window, or with server perf off here.</summary>
        public NexusAgentLoad Load;
        /// <summary>Every player-owned grid this server sampled since the last report; null when it
        /// has not sampled since, and the authority keeps the last list it had.</summary>
        public List<NexusPlayerContact> Contacts;
        /// <summary>Every player-owned grid standing here, as of this server's last sweep; null when
        /// it has not swept since, and the authority keeps the last list it had.</summary>
        public List<NexusOwnedGrid> OwnedGrids;
        /// <summary>Faction-terminal transfers made here and not yet acknowledged, oldest first.</summary>
        public List<NexusWalletTransfer> Transfers;
        /// <summary>Store and Services Terminal events made here and not yet acknowledged, oldest first.</summary>
        public List<NexusStoreEvent> StoreEvents;
        /// <summary>The display names of the players connected here, for the authority's player
        /// index. Sent when the set changes and on a slow cadence, not every report; null between.</summary>
        public List<NexusPlayerName> Names;
        /// <summary>The seats' grids here that are locked to another grid by a connector, for the
        /// authority's trespass ladder: a docked ship is never lingering. Every report; null for none.</summary>
        public List<long> Docked;
        /// <summary>The nodes whose NPC station stands here with no working antenna of its owner's,
        /// so cannot call (<see cref="StationCallRules.Silenced"/>). Every report; null for none.</summary>
        public List<string> Muted;
        /// <summary>DecisionLog lines written here and not yet acknowledged: a slice, oldest first.</summary>
        public List<NexusLogLine> Log;
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        public bool IsEmpty
        {
            get
            {
                return (AsteroidFields == null || AsteroidFields.Count == 0)
                    && (Stations == null || Stations.Count == 0)
                    && (Siege == null || Siege.Count == 0)
                    && (Grids == null || Grids.Count == 0) && (SpawnFailed == null || SpawnFailed.Count == 0)
                    && (Transfers == null || Transfers.Count == 0) && (StoreEvents == null || StoreEvents.Count == 0);
            }
        }

        public byte[] Encode() { return NexusHandsCodec.Encode(this, Settings); }

        public static bool TryDecode(byte[] payload, out NexusAgentReport report)
        {
            report = NexusHandsCodec.Decode<NexusAgentReport>(payload, Settings);
            if (report == null || report.Version != CurrentVersion) { report = null; return false; }
            return true;
        }

        /// <summary>Applies the report to the authority's graph. Returns how many nodes changed.</summary>
        public int ApplyTo(SpatialGraph graph)
        {
            if (graph == null || AsteroidFields == null) return 0;
            int changed = 0;
            foreach (string id in AsteroidFields)
            {
                SpatialNode node;
                if (id == null || !graph.Nodes.TryGetValue(id, out node) || node.AsteroidFieldSeed != 0) continue;
                node.AsteroidFieldSeed = AsteroidFieldLayout.FieldSeed(id);
                changed++;
            }
            return changed;
        }
    }

    internal static class NexusHandsCodec
    {
        public static readonly JsonSerializerSettings Lean = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        /// <summary>Cap on the inflated size, so a corrupt frame cannot inflate without bound.</summary>
        private const int MaxInflatedBytes = 32 * 1024 * 1024;

        public static byte[] Encode(object value, JsonSerializerSettings settings)
        {
            byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, settings));
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
                    gz.Write(json, 0, json.Length);
                return ms.ToArray();
            }
        }

        public static T Decode<T>(byte[] payload, JsonSerializerSettings settings) where T : class
        {
            if (payload == null || payload.Length == 0) return null;
            try
            {
                using (var input = new MemoryStream(payload))
                using (var gz = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buf = new byte[16384];
                    int read;
                    while ((read = gz.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (output.Length + read > MaxInflatedBytes) return null;
                        output.Write(buf, 0, read);
                    }
                    return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(output.ToArray()), settings);
                }
            }
            catch { return null; }
        }
    }
}
