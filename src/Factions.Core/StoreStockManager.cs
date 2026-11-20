using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Maintains a per-Shipyard quota of store-listed ships by posting <c>Ship:{role}</c> demands
    /// at Shipyard nodes, flagged <see cref="DemandEntry.DestinedForSale"/>.
    ///
    /// <para>No prefab is store merchandise any more (FAC-060): the shelf is drawn from the same
    /// capability-derived pool the faction builds its fleet from, so this manager chooses rather
    /// than filters. <see cref="StoreShelfPlan"/> answers what kind of hull the next slot wants --
    /// faction type leads, personality shades -- and the demand names that capability's real role.
    /// The <b>grid size</b> the slot wants rides on <see cref="DemandEntry.RequiredGridSize"/>:
    /// what was retired is the two roles NAMED after grid size, not grid size itself, so
    /// <see cref="FactionTuning.StoreShipSlotsSmallGrid"/>/<c>...LargeGrid</c> keep their exact
    /// meaning.</para>
    ///
    /// <para><b>Stock is counted per site.</b> Each Shipyard keeps its own small/large shelf and
    /// posts only for what its own <see cref="SpatialNode.ListedForSale"/> is short of. Selling is
    /// still network-wide -- <see cref="Factions.StationStoreManager"/> aggregates every owned node's
    /// listings and filters them by the selling station's own gravity -- so a hull built at one yard
    /// can be bought at another station. What a site-scoped target buys is that a yard in a gravity
    /// well is not read as stocked by hulls sitting at a yard in vacuum, and that each yard's builds
    /// are hulls that can leave it.</para>
    ///
    /// <para>Slot targets (per Shipyard node, tunable via <see cref="FactionTuning"/>):</para>
    /// <list type="bullet">
    ///   <item><see cref="FactionTuning.StoreShipSlotsSmallGrid"/> — default 5</item>
    ///   <item><see cref="FactionTuning.StoreShipSlotsLargeGrid"/> — default 2</item>
    /// </list>
    ///
    /// <para><see cref="StoreShelfRelease"/>'s over-target drain is scoped the same way: a stocker
    /// and a releaser reading the same count at different scopes is an oscillation, not a dead
    /// band.</para>
    /// </summary>
    public sealed class StoreStockManager : IFactionManager
    {
        private const string SmallGrid = "Small";
        private const string LargeGrid  = "Large";

        // null = no catalogue wired (the Sim, and tests): every faction keeps its store, which is
        // the behaviour before the gate existed. See FactionStorePolicy.HasPlayerStore.
        private readonly IFactionTypeCatalogue _factionTypes;

        public StoreStockManager(IFactionTypeCatalogue factionTypes = null)
        {
            _factionTypes = factionTypes;
        }

        public void Tick(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            int targetSmall = tuning.StoreShipSlotsSmallGrid;
            int targetLarge = tuning.StoreShipSlotsLargeGrid;
            var ttl = System.TimeSpan.FromSeconds(
                tuning.StoreStockIntervalSeconds > 0 ? tuning.StoreStockIntervalSeconds * 2 : 120.0);

            foreach (FactionState st in states)
            {
                // No IsAutonomyDisabled check -- self-sufficient (restock-target driven), so a
                // Magnate-tier player faction's store keeps restocking. See
                // docs/economy-territory.md -> "Player-faction claiming".
                if (FactionLifecycle.IsDormant(st, world.UtcNow)) continue;

                // A faction whose world-stated type sells nothing to players keeps no shelf, so it
                // must not spend its yard's throughput stocking one. Anything already in flight is
                // refunded rather than abandoned -- see ShipyardManager.CancelStoreBuilds for why
                // abandoning it would never expire.
                // Swept every tick rather than once: a Ship:Store* demand posted just before the
                // gate began firing survives its TTL, and ShipyardManager can open an entry from it
                // after this manager has stopped posting. The pass is silent unless it removes
                // something, and costs one scan of an estate the eligible path scans twice anyway.
                if (!FactionStorePolicy.HasPlayerStore(_factionTypes, tuning, st.Tag))
                {
                    int cancelled = ShipyardManager.CancelStoreBuilds(world, st.Tag, log);
                    if (cancelled > 0)
                        log.Add(st.Tag + " STORE_STOCK_DISABLED cancelled=" + cancelled);
                    continue;
                }

                // The player switched restocking off. Same handling as a faction type that sells
                // nothing -- refund what is in flight rather than abandon it -- and deliberately NOT
                // an un-listing: a hull already on sale stays on sale, because the owner stopped
                // ordering more, not offering what they have.
                if (!FactionLifecycle.ShelfStockingEnabled(st))
                {
                    int cancelled = ShipyardManager.CancelStoreBuilds(world, st.Tag, log);
                    if (cancelled > 0)
                        log.Add(st.Tag + " SHELF_STOCKING_OFF cancelled=" + cancelled);
                    continue;
                }

                // Hand back reservations whose reason has lapsed, BEFORE counting -- a pass that
                // counted first would stock against a shelf it is about to shrink.
                var storeStations = new List<SpatialNode>();
                foreach (SpatialNode n in world.Graph.OwnedBy(st.Tag))
                    if (Economy.HasPhysicalStation(n)) storeStations.Add(n);
                StoreShelfRelease.Run(st, world, tuning, storeStations, log);

                // A player naming their own shelf replaces both halves of the branch below: which
                // hull, and how many. The grid-size targets are the manager's depth control and stop
                // applying to this faction -- a per-prefab list already says how deep each row is.
                if (FactionLifecycle.ShelfActive(st))
                {
                    ServiceShelf(st, world, tuning, storeStations, log);
                    continue;
                }

                string factionType = null;
                if (_factionTypes != null) _factionTypes.TryGetFactionType(st.Tag, out factionType);
                Personality personality = Personalities.Get(st.PersonalityId);

                // The target is per SITE: every yard keeps its own small/large shelf, counted from
                // its own listed stock and filled by its own builds. Grid size is read from the
                // catalogue rather than through a role string -- grid size was never what FAC-060
                // retired, the two ROLES named after it were.
                foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
                {
                    int shipyardLevel;
                    if (!node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out shipyardLevel)
                        || shipyardLevel < 1)
                        continue;

                    bool needSmall = ListedCount(node, SmallGrid, world.PrefabCatalogue) < targetSmall;
                    bool needLarge = ListedCount(node, LargeGrid, world.PrefabCatalogue) < targetLarge;
                    if (!needSmall && !needLarge) continue;

                    // What kind of hull this site's next shelf slot wants. Type leads, personality
                    // shades, and the fill is by largest shortfall so a five-slot shelf reads as the
                    // intended mix. Tallied at this node, since this node's shelf is what it fills.
                    var listedByClass = new Dictionary<string, int>(StringComparer.Ordinal);
                    TallyClasses(node, world.PrefabCatalogue, listedByClass);
                    string role = StoreShelfPlan.NextClass(factionType, personality, null, listedByClass,
                        targetSmall + targetLarge);

                    if (needSmall) PostDemand(st.Tag, node, role, SmallGrid, ttl, world, log);
                    if (needLarge) PostDemand(st.Tag, node, role, LargeGrid, ttl, world, log);
                }
            }
        }

        /// <summary>Evaluates a player faction's <see cref="FactionState.Shelf"/> -- "keep N of this
        /// hull on sale" -- and places at most the shortfall as real shelf builds through
        /// <see cref="ShipyardManager.QueueBuild"/>, the same primitive the fleet doctrine and the
        /// player's own Build button use. The store's counterpart to
        /// <c>ShipyardManager.ServiceDoctrine</c>, and the same shape deliberately.
        ///
        /// <para><b>It posts no demand.</b> A <c>Ship:{role}</c> demand cannot name a prefab, which is
        /// the whole reason the manager used to choose one: the demand said "a cargo hull" and
        /// <c>SelectPrefabBom</c> answered "this one". Naming the hull means placing the build.</para>
        ///
        /// <para><b>What counts as "have" is the reservation, not the stock.</b>
        /// <see cref="SpatialNode.ListedForSale"/> across the estate -- the slice fleet dispatch may
        /// not draw down. A hull the fleet owns is not on the shelf however many of it exist, which is
        /// what keeps this list and the fleet doctrine from ever fighting over one hull.</para>
        ///
        /// <para><b>One build per line at a time.</b> No concurrency field: a shop hull is the one
        /// thing nothing else is waiting on (<see cref="FactionTuning.StoreShipBuildPriority"/> ranks
        /// it below every fleet role), so letting a shelf line open several builds at once would put
        /// the yard's queue to work on stock no one has asked for.</para></summary>
        private static void ServiceShelf(FactionState st, WorldModel world, FactionTuning tuning,
                                         IList<SpatialNode> storeStations, List<string> log)
        {
            IPrefabCatalogue cat = world.PrefabCatalogue;
            var owned = new List<SpatialNode>(world.Graph.OwnedBy(st.Tag));

            for (int i = 0; i < st.Shelf.Count; i++)
            {
                ShelfLine line = st.Shelf[i];
                if (line.Paused) continue;
                if (string.IsNullOrEmpty(line.PrefabId) || line.TargetCount <= 0) continue;

                // A line names a design, not a revision: every version on the shelf counts, and the
                // yard builds the latest this faction may use.
                string buildId = PrefabVersions.LatestFor(cat, line.PrefabId, st.Tag);
                long listed = CountListed(owned, line.PrefabId, cat);
                int building = CountShelfBuildsInProgress(owned, line.PrefabId, cat);
                long shortfall = line.TargetCount - listed - building;

                // The role is the hull's own capability class, not a player choice: someone picking a
                // ship to sell is not also deciding what the yard files it as.
                string role = StoreShelfPlan.ClassOf(cat, buildId);

                var placeable = new List<SpatialNode>();
                int routeBlocked = 0;
                if (role != null)
                    foreach (SpatialNode node in owned)
                    {
                        int level;
                        if (node.ShipyardPaused
                            || !node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out level)
                            || level < 1)
                            continue;
                        if (!ShipyardManager.CanFlyFromYard(world, tuning, node, buildId))
                        {
                            routeBlocked++;
                            continue;
                        }
                        placeable.Add(node);
                    }
                placeable.Sort(CompareNodeById);

                int placed = 0;
                if (role != null && shortfall > 0 && building < 1 && placeable.Count > 0)
                {
                    Dictionary<string, long> bom;
                    PlayerQueueResult result = new ShipyardManager().QueueBuild(world, tuning, st.Tag,
                        placeable[0].Id, role, buildId, out bom, log, destinedForSale: true);
                    if (result == PlayerQueueResult.Ok) placed++;
                    else
                        log.Add(st.Tag + " SHELF_BUILD_REJECTED line=" + line.Id
                                + " prefab=" + buildId + " role=" + role
                                + " node=" + placeable[0].Id + " reason=" + result);
                }

                string signature = "line=" + line.Id
                    + " prefab=" + buildId
                    + " role=" + (role ?? "none")
                    + " listed=" + listed
                    + " building=" + building
                    + " target=" + line.TargetCount
                    + " placeable=" + placeable.Count
                    + " placed=" + placed;
                // Log-on-change, the rule DOCTRINE_BUILD follows: a satisfied line is re-evaluated
                // every store cadence and must stay silent while nothing about it moves.
                if (placed > 0 || !string.Equals(signature, line.LastLogSignature, StringComparison.Ordinal))
                {
                    line.LastLogSignature = signature;
                    log.Add(st.Tag + " SHELF_BUILD " + signature);
                }

                // Two ways a line can be unbuildable, and they want different fixes: a hull no yard
                // can launch, and a hull the catalogue cannot class at all (so no role names it).
                string blocked = null;
                if (role == null) blocked = "line=" + line.Id + " prefab=" + line.PrefabId + " reason=unclassed";
                else if (placeable.Count == 0 && routeBlocked > 0)
                    blocked = "line=" + line.Id + " prefab=" + line.PrefabId + " reason=route";
                else if (!SellableAnywhere(world, tuning, buildId, storeStations))
                    blocked = "line=" + line.Id + " prefab=" + line.PrefabId + " reason=unsellable";
                if (!string.Equals(blocked, line.LastBlockedLogSignature, StringComparison.Ordinal))
                {
                    line.LastBlockedLogSignature = blocked;
                    if (blocked != null) log.Add(st.Tag + " SHELF_LINE_BLOCKED " + blocked);
                }
            }
        }

        /// <summary>How many of <paramref name="prefabId"/> this estate holds reserved for sale --
        /// the shelf's "have". Public because the player-facing listing shows this number beside the
        /// line, and a listing counting it a second way could say a line was satisfied while the
        /// manager kept building. With a <paramref name="catalogue"/>, every version of the design
        /// counts.</summary>
        public static long CountListed(IEnumerable<SpatialNode> owned, string prefabId, IPrefabCatalogue catalogue = null)
        {
            if (owned == null || string.IsNullOrEmpty(prefabId)) return 0;
            long listed = 0;
            foreach (SpatialNode n in owned)
                foreach (KeyValuePair<string, int> kv in n.ListedForSale)
                    if (kv.Value > 0 && PrefabVersions.SameFamily(catalogue, kv.Key, prefabId)) listed += kv.Value;
            return listed;
        }

        /// <summary>Shelf builds of <paramref name="prefabId"/> already in progress on this estate.
        /// Only <see cref="BuildQueueEntry.DestinedForSale"/> entries count: a fleet build of the same
        /// hull is destined for the fleet and will never reach the shelf. With a
        /// <paramref name="catalogue"/>, a build of any version of the design counts.</summary>
        public static int CountShelfBuildsInProgress(IEnumerable<SpatialNode> owned, string prefabId,
                                                     IPrefabCatalogue catalogue = null)
        {
            if (owned == null || string.IsNullOrEmpty(prefabId)) return 0;
            int building = 0;
            foreach (SpatialNode n in owned)
                foreach (BuildQueueEntry e in n.BuildQueue)
                    if (e.DestinedForSale && PrefabVersions.SameFamily(catalogue, e.PrefabId, prefabId))
                        building++;
            return building;
        }

        /// <summary>True when at least one station this faction sells from could offer the hull -- the
        /// same judgement <see cref="StoreShelfRelease"/> hands a reservation back on, asked before
        /// the BOM is spent rather than after.</summary>
        private static bool SellableAnywhere(WorldModel world, FactionTuning tuning, string prefabId,
                                             IList<SpatialNode> storeStations)
        {
            if (storeStations == null || storeStations.Count == 0) return true;
            for (int i = 0; i < storeStations.Count; i++)
                if (StoreShelfPlan.IsShelfableAt(world.PrefabCatalogue, prefabId, world.Planets,
                                                 storeStations[i].Position, tuning.MinDryTwr))
                    return true;
            return false;
        }

        /// <summary>Deterministic placement order: OwnedBy's enumeration order is not a contract, and
        /// the sim and the host must place the same build given the same world.</summary>
        private static int CompareNodeById(SpatialNode a, SpatialNode b)
        {
            return string.CompareOrdinal(a.Id, b.Id);
        }

        private static void PostDemand(string factionTag, SpatialNode node, string role, string gridSize,
                                       System.TimeSpan ttl, WorldModel world, List<string> log)
        {
            bool isNew = world.Demand.Post(new DemandEntry
            {
                FactionTag = factionTag,
                Kind       = "Ship:" + role,
                NodeId     = node.Id,
                ExpiresUtc = world.UtcNow.Add(ttl),
                FirstPostedUtc = world.UtcNow,
                // The purpose is now carried explicitly rather than inferred from the role name.
                // Load-bearing already: the finished hull is reserved into ListedForSale on this
                // flag, and a store build is cancelled on it.
                DestinedForSale = true,
                RequiredGridSize = gridSize,
            });

            if (isNew)
                log.Add(factionTag + " STORE_SHIP_DEMAND role=" + role + " grid=" + gridSize
                        + " node=" + node.Id);
        }

        // "" (free-build, unknown prefab) can't be classified by grid size and doesn't count toward
        // either target.
        private static int ListedCount(SpatialNode node, string gridSize, IPrefabCatalogue catalogue)
        {
            if (catalogue == null) return 0;
            int total = 0;
            foreach (var kv in node.ListedForSale)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                if (string.Equals(catalogue.GridSize(kv.Key), gridSize, StringComparison.OrdinalIgnoreCase))
                    total += kv.Value;
            }
            return total;
        }

        /// <summary>Adds this node's listed hulls to the per-capability-class tally the shelf plan
        /// fills against. A hull the catalogue cannot classify is skipped rather than counted under
        /// a default: counting it would make the plan believe a class is stocked when it is not.</summary>
        private static void TallyClasses(SpatialNode node, IPrefabCatalogue catalogue,
                                         Dictionary<string, int> tally)
        {
            if (catalogue == null) return;
            foreach (var kv in node.ListedForSale)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                string cls = StoreShelfPlan.ClassOf(catalogue, kv.Key);
                if (cls == null) continue;
                int have;
                tally.TryGetValue(cls, out have);
                tally[cls] = have + kv.Value;
            }
        }
    }
}
