using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>A unit of demand posted into the inter-manager signal buffer.
    /// Kind encodes both the category and subject:
    /// <list type="bullet">
    ///   <item>"Ship:Trade" / "Ship:Raid" / "Ship:Patrol" — posted by Enact when a fleet dispatch is blocked by
    ///   zero ShipStock; consumed by <see cref="ShipyardManager"/> after building the ship.</item>
    ///   <item>"Item:{SubTypeId}" — posted by ShipyardManager when it cannot build because components are
    ///   insufficient; consumed by a future OperationsManager.</item>
    ///   <item>"Need:{invKey}" — the outstanding requirement of one operational commitment: an install
    ///   BOM, a founding cost, a ship BOM, or a blocked recipe's inputs. Read by <see cref="PriceOracle"/>
    ///   as the demand half of a node's reference stock, and aggregated per (faction, invKey) by
    ///   <see cref="DemandRegister.OperationalDemandFor"/>.</item>
    /// </list>
    /// <para>A "Need:" demand rides <em>beside</em> the "Item:" demand its poster also raises rather
    /// than replacing it, because several posters share one unkeyed "Item:{invKey}" slot and
    /// <see cref="DemandRegister.Post"/> is last-write-wins per (faction, kind, node). Measured live
    /// 2026-08-27: <c>PostInstallCostDemands</c> writes SteelPlate need=2828 and
    /// <c>PostCatalogDemandsScalar</c> overwrites the same slot with 11000 later in the same cycle
    /// (see the ordering note in ProductionManager.Cycle.cs). A provenance flag on the shared row
    /// would record only whichever poster wrote last; a separate kind keeps the real need
    /// addressable, with its own TTL, and leaves every existing "Item:" consumer reading exactly what
    /// it read before.</para>
    /// </summary>
    /// <summary>What raised a demand, and therefore how much a shortfall of it actually costs the
    /// faction. Ordering and the Haul/Mine choice both read this through
    /// <see cref="DemandUrgency"/>; nothing else branches on it, and it is deliberately not part of
    /// the <see cref="DemandRegister"/> dedup key -- two posters sharing one key keep sharing it, and
    /// the stronger class wins the row (see <see cref="DemandEntry.PreserveOnRefresh"/>).
    /// <para>Ordered weakest to strongest so a numeric comparison is the class comparison. A poster
    /// that leaves it at <see cref="Unclassified"/> is treated as <see cref="Catalog"/> by
    /// <see cref="DemandUrgency"/> rather than as zero -- an unstamped demand must not silently rank
    /// below every stamped one.</para></summary>
    public enum DemandClass
    {
        Unclassified = 0,
        /// <summary>A standing sale-floor or surplus top-up. Wanting it is a preference.</summary>
        Catalog = 1,
        /// <summary>A store's stock target for a listing players can buy.</summary>
        StoreStock = 2,
        /// <summary>A blocked recipe's input at a production bank.</summary>
        RecipeInput = 3,
        /// <summary>A founded node's fuel reserve (docs/fuel-plan.md). Ranked just under
        /// <see cref="Install"/>: a node out of fuel cannot fly anything out of itself, which is
        /// dearer than a blocked recipe and cheaper than committed install credits.
        /// <para><see cref="DemandUrgency"/> scales it by the node's parked hull count, so a dry
        /// shipyard outranks a dry outpost without either being a separate class.</para>
        /// <para>Also carries a combat departure's ammunition shortfall
        /// (<see cref="FleetMunitions.PostShortfall"/>): the same kind of want, supply a hull
        /// leaving the node needs, ranked the same way.</para></summary>
        Fuel = 4,
        /// <summary>A capability install's BOM -- committed credits, retryable.</summary>
        Install = 5,
        /// <summary>An active ship build's outstanding BOM. Already-collected components are sunk
        /// into it and a stall eventually refunds them, so this is the most expensive to leave
        /// unmet.</summary>
        ShipBuild = 6,
        /// <summary>Food for the workers living at a node's Habitat
        /// (<see cref="WorkerFoodDemand"/>). Appended rather than ranked into place, so no persisted
        /// ordinal moves; its weight in <see cref="FactionTuning.UrgencyClassWeights"/> matches
        /// <see cref="Fuel"/>: a node that runs out loses its people, as a dry node loses its
        /// departures.</summary>
        Upkeep = 7,
    }

    public struct DemandEntry
    {
        public string FactionTag;
        public string Kind;       // "Ship:Trade", "Ship:Raid", "Ship:Patrol", "Item:SteelPlate", …
        public string NodeId;     // node where the demand must be satisfied
        public DateTime ExpiresUtc;
        public long Quantity;     // for "Item:" demands: units needed; 0 = unspecified (Ship demands)

        /// <summary>What raised this demand. Read only by <see cref="DemandUrgency"/>; see
        /// <see cref="DemandClass"/>. Not part of the dedup key.</summary>
        public DemandClass Class;

        /// <summary>True when <see cref="ProductionManager"/> sized this row from a demand chain.
        /// Not part of the dedup key, and last-writer-wins on refresh: a poster that raises the row
        /// for its own reasons is telling the truth about it.
        ///
        /// <para><b>Why a flag and not a class test.</b> A chain-sized row must never become a chain
        /// root — that is a bank's own output re-entering as evidence for itself, and it compounds
        /// every cycle. Excluding <see cref="DemandClass.RecipeInput"/> was the first attempt at
        /// that rule and it broke the moment chain attribution shipped: a chain-reached row is
        /// reclassed to the commitment it serves, which lifted it straight out of the exclusion.
        /// Class cannot express this, because a chain rooted in a catalogue want stamps
        /// <see cref="DemandClass.Catalog"/> on its own lower rows too. Provenance is the property
        /// actually being tested, so it is recorded rather than inferred.</para></summary>
        public bool ChainSized;

        // "Ship:*" demands only -- ignored for "Item:"/"Position:" demands.
        /// <summary>null = no specific destination (e.g. a proactive "keep this zone warm" demand);
        /// else the real second endpoint of the route that the demanding dispatch actually needs a
        /// hull to survive, resolved via <see cref="WorldModel.Graph"/> at build-selection time
        /// rather than cached as a route/position.</summary>
        public string DestNodeId;

        // "Ship:*" demands only -- ignored for "Item:"/"Position:" demands. Not part of the dedup
        // key (same convention as MinCargoLiters/RequiredPlanetId/RequiredZone below).
        /// <summary>null = the demand's only real leg is NodeId-&gt;DestNodeId (or NodeId is itself
        /// the whole route, e.g. a proactive "keep this zone warm" demand); else the real
        /// intermediate waypoint a fallback-built hull must ALSO survive reaching before it can ever
        /// serve DestNodeId -- e.g. <see cref="OperationsManager.DispatchHaul"/>'s Tier-3 shipyard
        /// fallback posts the pickup source here, since the ship is built near the pickup (nearest
        /// Shipyard to it), not near the final delivery point, and the pickup leg is exactly the one
        /// <see cref="OperationsManager.TryDispatchHaulViaPickup"/> will later test the built hull
        /// against. Without this, build-time route selection only ever sees NodeId-&gt;DestNodeId and
        /// is blind to the pickup leg entirely -- when the shipyard node and DestNodeId happen to
        /// coincide (a common case: the shipyard also being the material's final destination), that
        /// collapses to a trivial same-point route that any hull passes regardless of whether it can
        /// actually reach the real (possibly off-planet) pickup source.</summary>
        public string ViaNodeId;

        // "Ship:*" demands only -- ignored for "Item:"/"Position:" demands, same convention as Quantity above.
        /// <summary>&gt;0 = the demanded ship must be cargo-suitable (Trade/Gift/Haul roles); 0 = no
        /// cargo requirement (Raid/Patrol combat roles).</summary>
        public double MinCargoLiters;
        // "Ship:*" demands only -- ignored for "Item:"/"Position:" demands. Not part of the dedup
        // key, same convention as MinCargoLiters.
        /// <summary>&gt;0 = the demanded ship must carry at least this much summed weapon DPS; 0 = no
        /// power requirement. The combat counterpart of <see cref="MinCargoLiters"/>: a requirement
        /// stated in DPS (a terror campaign's budget, <c>TerrorCampaigns.Budget</c>) has to reach the
        /// yard as a floor on the hull, or a peacetime cheapest-first ranker closes a 10,000-DPS gap
        /// with 500-DPS drones one at a time. Read by <c>ShipyardManager.SelectPrefabBom</c> and
        /// clamped, as the cargo floor is, to the largest hull that can actually be built.</summary>
        public double MinDps;
        // "Ship:*" demands only. Not part of the dedup key, same convention as MinCargoLiters.
        /// <summary>&gt;0 = the demanded hull must drill at least this well
        /// (<see cref="IPrefabCatalogue.DrillYieldScore"/>); 0 = no drill requirement. The Mine
        /// counterpart of <see cref="MinDps"/>: a drill-quality upgrade
        /// (<c>ShipyardManager.PostExtractionUpgradeDemands</c>) asks for a hull faster than the ones
        /// the catchment already flies, and without the floor reaching the yard a slow drill parked
        /// there, or relocated to it, answers the demand as "met" and no build is ever queued.
        /// Read by the already-met check and the relocation tier.</summary>
        public double MinDrillYield;
        /// <summary>null = not planet-scoped; else the demanding node's own <see cref="SpatialNode.PlanetId"/>
        /// — mirrors <see cref="InvestmentRequest.TargetPlanetId"/>.</summary>
        public string RequiredPlanetId;
        /// <summary>null = not zone-restricted; else the demanding node's own <see cref="SpatialNode.Zone"/>.</summary>
        public NodeZone? RequiredZone;

        // "Ship:*" demands only. PART OF THE DEDUP KEY: two shelf slots wanting different grid
        // sizes at one yard are two different needs, exactly as two destinations are -- and with the
        // size outside the key the second post refreshed the first and carried its own size over,
        // so the shelf only ever asked for whichever size was posted last.
        /// <summary>null = any grid size; else "Small" or "Large", matched against
        /// <see cref="IPrefabCatalogue.GridSize"/>. Set by the store, whose shelf has a small/large
        /// composition (<see cref="FactionTuning.StoreShipSlotsSmallGrid"/>/<c>...LargeGrid</c>) that
        /// survived the role retirement: what was retired is the two roles NAMED after grid size,
        /// not grid size itself, which is still a real property of a hull.</summary>
        public string RequiredGridSize;

        // "Ship:*" demands only. PART OF THE DEDUP KEY -- see DemandKey.
        /// <summary>True when this demand exists to stock a player store rather than the faction's
        /// own fleet. Read by <c>FleetFitness.SizingFloor</c>, which excludes a shelf build from
        /// fleet sizing, and by <see cref="ShipyardManager"/> when it reserves the finished hull.
        ///
        /// <para><b>Why this is part of the key and not just payload.</b> Purpose used to be carried
        /// by the role string itself (<c>Ship:StoreSmall</c> vs <c>Ship:Trade</c>). With the store
        /// roles retired both purposes name the same role, so a shelf demand and the proactive fleet
        /// demand at one yard become the same key — one row, last poster wins, and the flag every
        /// consumer reads is whichever purpose posted last. A build then succeeds, sized and reserved
        /// for the wrong one, with nothing to signal it.</para></summary>
        public bool DestinedForSale;

        // Set for every kind. Not part of the dedup key.
        /// <summary>When this demand key was first posted. <see cref="DemandRegister.Post"/> carries
        /// it forward across every refresh, so it measures how long the need has genuinely gone
        /// unmet rather than resetting on each TTL bump the way <see cref="ExpiresUtc"/> does.
        /// <para>A caller may set it ("Ship:" posters do, via <see cref="ForShip"/>); when it is
        /// left unset, <see cref="DemandRegister.Post"/> stamps it from the world clock as the entry
        /// is first keyed. It is therefore only <c>default(DateTime)</c> on a register built with no
        /// clock at all.</para></summary>
        public DateTime FirstPostedUtc;

        /// <summary>The largest <see cref="Quantity"/> this key has been posted at since
        /// <see cref="FirstPostedUtc"/>. Carried forward by <see cref="PreserveOnRefresh"/> like the
        /// age, so when the demand ends its <see cref="DemandEpisode"/> can say how big the ask got,
        /// not just what the last refresh happened to post.</summary>
        public long PeakQuantity;

        /// <summary>Builds a "Ship:{role}" demand entry. Every poster (<see cref="OperationsManager"/>,
        /// <see cref="ShipyardManager"/>, <see cref="TradeManager"/>, <see cref="FleetManager"/>,
        /// <c>FactionRuntime</c>) constructed this same shape independently, differing only in which
        /// optional fields they had available -- centralised here so the field list has one definition.
        /// Posting mechanism (live <see cref="DemandRegister.Post"/> vs. a staged execution context) and
        /// TTL/log wording stay with each caller, since those genuinely differ per site.</summary>
        public static DemandEntry ForShip(string factionTag, string role, string nodeId, DateTime expiresUtc,
            string destNodeId = null, double minCargoLiters = 0,
            string requiredPlanetId = null, NodeZone? requiredZone = null, string viaNodeId = null,
            DateTime postedUtc = default(DateTime), bool destinedForSale = false,
            string requiredGridSize = null, double minDps = 0, double minDrillYield = 0)
        {
            return new DemandEntry
            {
                FactionTag       = factionTag,
                Kind             = "Ship:" + role,
                NodeId           = nodeId,
                DestNodeId       = destNodeId,
                ViaNodeId        = viaNodeId,
                ExpiresUtc       = expiresUtc,
                MinCargoLiters   = minCargoLiters,
                RequiredPlanetId = requiredPlanetId,
                RequiredZone     = requiredZone,
                FirstPostedUtc   = postedUtc,
                DestinedForSale  = destinedForSale,
                RequiredGridSize = requiredGridSize,
                MinDps           = minDps,
                MinDrillYield    = minDrillYield,
            };
        }

        /// <summary>Builds a "Need:{invKey}" entry — one operational commitment's outstanding
        /// requirement. <paramref name="quantity"/> is what is still missing, not the whole BOM, so a
        /// partially-gathered need shrinks as it is met and disappears when it is satisfied.
        /// <paramref name="nodeId"/> is null for a faction-wide commitment (an install or founding
        /// cost, which any owned node's stock can pay for) and set for one pinned to a place (a ship
        /// BOM at its shipyard).</summary>
        public static DemandEntry ForNeed(string factionTag, string inventoryKey, string nodeId,
            DateTime expiresUtc, long quantity, DemandClass demandClass = DemandClass.Unclassified)
        {
            return new DemandEntry
            {
                FactionTag = factionTag,
                Kind       = DemandRegister.NeedKind(inventoryKey),
                NodeId     = nodeId,
                ExpiresUtc = expiresUtc,
                Quantity   = quantity,
                Class      = demandClass,
            };
        }

        /// <summary>Merges the fields a refresh must NOT clobber from the entry already under this
        /// key into <paramref name="incoming"/>, and returns it. Every <c>Post</c> implementation
        /// (<see cref="DemandRegister.Post"/> and the two staged mirrors in
        /// <c>OperationsStagedWorldEffects</c>/<c>ProductionStagedWorldEffects</c>) routes its
        /// refresh branch through here so the three cannot drift apart.
        /// <para><see cref="MinCargoLiters"/> and <see cref="MinDps"/> keep the larger of the two —
        /// equivalent posts for the same exact route can carry different cargo or power requirements
        /// and the largest is the real constraint a built hull must satisfy. <see cref="FirstPostedUtc"/> keeps the earlier set
        /// value, which is the whole point of the field: a TTL refresh must not reset the age.
        /// <see cref="Class"/> keeps the stronger of the two: several posters share one unkeyed
        /// "Item:{invKey}" slot, and a catalogue top-up refreshing the row a ship build also wants
        /// must not demote it.</para></summary>
        public static DemandEntry PreserveOnRefresh(DemandEntry prior, DemandEntry incoming)
        {
            if (prior.MinCargoLiters > incoming.MinCargoLiters)
                incoming.MinCargoLiters = prior.MinCargoLiters;
            if (prior.MinDps > incoming.MinDps)
                incoming.MinDps = prior.MinDps;
            if (prior.MinDrillYield > incoming.MinDrillYield)
                incoming.MinDrillYield = prior.MinDrillYield;
            if (prior.FirstPostedUtc != default(DateTime)
                && (incoming.FirstPostedUtc == default(DateTime)
                    || prior.FirstPostedUtc < incoming.FirstPostedUtc))
                incoming.FirstPostedUtc = prior.FirstPostedUtc;
            if (prior.Class > incoming.Class) incoming.Class = prior.Class;
            long peak = Math.Max(Math.Max(prior.PeakQuantity, prior.Quantity), incoming.Quantity);
            if (peak > incoming.PeakQuantity) incoming.PeakQuantity = peak;
            return incoming;
        }
    }

    /// <summary>One demand that has left the register: what it asked for at one node, for how long,
    /// and how it went. Recorded by <see cref="DemandRegister"/> for the station window's demand
    /// history; nothing in the simulation reads it.</summary>
    public struct DemandEpisode
    {
        public string FactionTag;
        public string Kind;
        public string NodeId;
        public string DestNodeId;
        public bool DestinedForSale;
        public DemandClass Class;
        /// <summary><see cref="DemandEntry.PeakQuantity"/> at the end: the largest ask of the episode.</summary>
        public long PeakQuantity;
        public DateTime FirstPostedUtc;
        /// <summary>When the register removed it.</summary>
        public DateTime EndedUtc;
        /// <summary>True when a manager retired it (<see cref="DemandRegister.Consume"/> /
        /// <see cref="DemandRegister.ConsumeAll"/> — a shipyard delivering the hull); false when it
        /// lapsed because nothing re-posted it before <see cref="DemandEntry.ExpiresUtc"/>, which is how
        /// every <c>Item:</c> demand ends once its poster is no longer short.</summary>
        public bool Consumed;
    }

    /// <summary>Global async buffer for inter-manager demand signalling. Managers post demands when blocked;
    /// other managers read and fulfil them. Not accessed by the brain — pure infra-layer coordination.
    /// Demands that expire (short TTL) are pruned each manager tick to prevent accumulation.</summary>
    public sealed class DemandRegister
    {
        // Supplied by the owning WorldModel. Only ever read to stamp FirstPostedUtc on a NEW entry,
        // so a register built without one still behaves exactly as it did before -- it just cannot
        // report an age. Every production path goes through WorldModel and therefore has a clock.
        private readonly Func<DateTime> _clock;

        public DemandRegister() : this(null) { }

        /// <summary>Builds a register that stamps <see cref="DemandEntry.FirstPostedUtc"/> on every
        /// newly-keyed demand from <paramref name="clock"/>. Before this, only the "Ship:" posters
        /// passed a time of their own, so every "Item:" demand carried an unset age and anything
        /// reading it (see <see cref="DemandUrgency"/>) silently saw a brand-new demand forever.</summary>
        public DemandRegister(Func<DateTime> clock) { _clock = clock; }

        /// <summary>How long an ended demand stays in <see cref="SnapshotEndedAtNode"/> and
        /// <see cref="SnapshotEndedForFaction"/>. Demands run for minutes to hours, so the production
        /// history's five minutes would show almost nothing ending. Public so the windows can say how
        /// far back it reaches.</summary>
        public const double EndedRetentionSeconds = 1800.0;   // 30 minutes

        /// <summary>Bound on one node's ended demands, oldest dropped first. A key that lapses and
        /// is re-posted every few minutes would otherwise grow without limit inside the window.</summary>
        public const int MaxEndedPerNode = 256;

        /// <summary>Bound on one faction's ended faction-wide demands, oldest dropped first, for the
        /// reason <see cref="MaxEndedPerNode"/> bounds a node's.</summary>
        public const int MaxEndedFactionWidePerFaction = 256;

        /// <summary>node id -> its ended demands, oldest first. Only <c>Item:</c> and <c>Ship:</c>
        /// rows are kept: those are what the station and faction demand windows show. Not persisted,
        /// like the production history.</summary>
        private readonly Dictionary<string, List<DemandEpisode>> _ended =
            new Dictionary<string, List<DemandEpisode>>(StringComparer.Ordinal);

        /// <summary>faction tag -> its ended demands that named no node, oldest first. Kept apart from
        /// <see cref="_ended"/> so every faction's nodeless churn cannot evict another's under one
        /// shared key.</summary>
        private readonly Dictionary<string, List<DemandEpisode>> _endedFactionWide =
            new Dictionary<string, List<DemandEpisode>>(StringComparer.Ordinal);

        private struct DemandKey : IEquatable<DemandKey>
        {
            public readonly string FactionTag;
            public readonly string Kind;
            public readonly string NodeId;
            public readonly string DestNodeId;
            /// <summary>See <see cref="DemandEntry.DestinedForSale"/> for why purpose is identity
            /// rather than payload.</summary>
            public readonly bool DestinedForSale;
            /// <summary>See <see cref="DemandEntry.RequiredGridSize"/>: two sizes are two needs.</summary>
            public readonly string RequiredGridSize;

            public DemandKey(string factionTag, string kind, string nodeId, string destNodeId,
                bool destinedForSale, string requiredGridSize)
            {
                FactionTag = factionTag;
                Kind = kind;
                NodeId = nodeId;
                DestNodeId = destNodeId ?? "";
                DestinedForSale = destinedForSale;
                RequiredGridSize = requiredGridSize ?? "";
            }

            public bool Equals(DemandKey other)
            {
                return string.Equals(FactionTag, other.FactionTag, StringComparison.Ordinal)
                    && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
                    && string.Equals(NodeId, other.NodeId, StringComparison.Ordinal)
                    && string.Equals(DestNodeId, other.DestNodeId, StringComparison.Ordinal)
                    && DestinedForSale == other.DestinedForSale
                    && string.Equals(RequiredGridSize, other.RequiredGridSize, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is DemandKey && Equals((DemandKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = FactionTag == null ? 0 : StringComparer.Ordinal.GetHashCode(FactionTag);
                    hash = (hash * 397) ^ (Kind == null ? 0 : StringComparer.Ordinal.GetHashCode(Kind));
                    hash = (hash * 397) ^ (NodeId == null ? 0 : StringComparer.Ordinal.GetHashCode(NodeId));
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(DestNodeId);
                    hash = (hash * 397) ^ (DestinedForSale ? 1 : 0);
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(RequiredGridSize);
                    return hash;
                }
            }
        }

        // Ordered list remains canonical for deterministic snapshots; the index makes repeated blocked
        // production posts O(1) rather than scanning every active demand for each post.
        private readonly List<DemandEntry> _demands = new List<DemandEntry>();
        private readonly Dictionary<DemandKey, int> _indexes = new Dictionary<DemandKey, int>();

        /// <summary>faction tag + separator + invKey -> summed live "Need:" quantity across every node.
        /// Maintained incrementally by <see cref="Post"/> and rebuilt by <see cref="RebuildIndex"/>,
        /// which every removal path already routes through, so <see cref="OperationalDemandFor"/> is
        /// O(1). <see cref="PriceOracle.ScarcityMultiplier"/> is called per dest-node-per-kind by
        /// TradeManager, per recipe output by ProductionManager and per item by StationStoreManager;
        /// a <see cref="GetFor"/> scan on that path would be O(all demands) per query.</summary>
        private readonly Dictionary<string, long> _operationalByItem = new Dictionary<string, long>();

        /// <summary>node id + separator + invKey -> summed live "Need:" quantity pinned to that node.
        /// Kept apart from <see cref="_operationalByItem"/> because the two are scoped differently: a
        /// faction-wide need (an install BOM, payable from any owned node's stock) is one need shared
        /// across the faction's holders, while a node-keyed one (a ship BOM) is owed in full at that
        /// node and nowhere else. Summing them would both under-apply at the node that owes it and
        /// wrongly apply it everywhere else.</summary>
        private readonly Dictionary<string, long> _operationalByNodeItem = new Dictionary<string, long>();

        // The same two scopes for ordinary "Item:" wants. Kept as O(1) aggregates for the same
        // reason the operational pair is: PriceOracle asks per (node, item) on the trade and
        // production hot paths, where GetFor's linear scan would be a per-query walk of every
        // demand in the world.
        private readonly Dictionary<string, long> _itemByItem = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _itemByNodeItem = new Dictionary<string, long>();

        // Bumped whenever either aggregate above actually changes. A reader that memoises a derived
        // value needs to know the underlying need moved WITHIN a tick, not only across ticks: install
        // demands are posted during the production cycle and read by the income pass, which can share
        // one UtcNow, so a clock-only cache would serve a need that is already stale by one pass.
        private int _operationalRevision;

        /// <summary>Increments on every change to the operational-need aggregates. Cheap to compare;
        /// never interpret the value, only whether it differs from one you stored.</summary>
        public int OperationalRevision { get { return _operationalRevision; } }

        /// <summary>Kind prefix for an operational commitment's outstanding requirement.</summary>
        public const string NeedPrefix = "Need:";

        /// <summary>Kind prefix for an ordinary concrete-item want — a recipe input, a surplus
        /// intent, a catalogue demand. Disjoint from <see cref="NeedPrefix"/>, which carries only
        /// the outstanding requirement of a committed BOM.</summary>
        public const string ItemPrefix = "Item:";

        /// <summary>The "Need:" demand kind for a concrete inventory key (e.g. "Comp:SteelPlate").</summary>
        public static string NeedKind(string inventoryKey) { return NeedPrefix + inventoryKey; }

        /// <summary>The "Item:" demand kind for a concrete inventory key, the counterpart of
        /// <see cref="NeedKind"/>.</summary>
        public static string ItemKind(string inventoryKey) { return ItemPrefix + inventoryKey; }

        /// <summary>Post a demand. If a matching entry (same faction + kind + nodeId) already exists
        /// its expiry and quantity are refreshed. Returns true if this was a new entry, false if
        /// it was a refresh — callers can use this to suppress redundant log lines.
        /// <para>A refresh carries forward the fields listed on <see cref="DemandEntry.PreserveOnRefresh"/>
        /// instead of last-write-wins clobbering them.</para></summary>
        public bool Post(DemandEntry entry)
        {
            DemandKey key = new DemandKey(entry.FactionTag, entry.Kind, entry.NodeId, entry.DestNodeId,
                entry.DestinedForSale, entry.RequiredGridSize);
            int index;
            if (_indexes.TryGetValue(key, out index))
            {
                DemandEntry prior = _demands[index];
                DemandEntry merged = DemandEntry.PreserveOnRefresh(prior, entry);
                _demands[index] = merged;
                AdjustOperational(prior, -1);
                AdjustOperational(merged, 1);
                AdjustItem(prior, -1);
                AdjustItem(merged, 1);
                return false;
            }
            // A new key is the only place an age can begin. Callers that set it themselves (the
            // "Ship:" posters, and a staged cycle replaying its own journal) keep the time they
            // chose; everyone else gets the world clock rather than default(DateTime).
            if (entry.FirstPostedUtc == default(DateTime) && _clock != null)
                entry.FirstPostedUtc = _clock();
            if (entry.Quantity > entry.PeakQuantity) entry.PeakQuantity = entry.Quantity;
            _indexes[key] = _demands.Count;
            _demands.Add(entry);
            AdjustOperational(entry, 1);
            AdjustItem(entry, 1);
            return true;
        }

        /// <summary>Looks up one exact canonical demand in O(1) without allocating a category
        /// snapshot. The four fields are the same identity used by <see cref="Post"/> and
        /// <see cref="Consume"/>; the ordered list remains the canonical enumeration store.</summary>
        public bool TryGet(string factionTag, string kind, string nodeId, string destNodeId,
            out DemandEntry entry, bool destinedForSale = false, string requiredGridSize = null)
        {
            int index;
            if (_indexes.TryGetValue(
                    new DemandKey(factionTag, kind, nodeId, destNodeId, destinedForSale, requiredGridSize),
                    out index))
            {
                entry = _demands[index];
                return true;
            }
            entry = default(DemandEntry);
            return false;
        }

        /// <summary>Returns a snapshot of all active "Ship:*" demands for a faction.
        /// Snapshot so callers can Consume() inside the loop without invalidating iteration.</summary>
        public List<DemandEntry> SnapshotShipDemands(string factionTag)
        {
            var result = new List<DemandEntry>();
            foreach (DemandEntry d in _demands)
                if (d.FactionTag == factionTag && d.Kind.StartsWith("Ship:"))
                    result.Add(d);
            return result;
        }

        /// <summary>Returns a snapshot of all active "Position:*" demands for a faction.
        /// Posted by <c>FactionRuntime</c> when a fleet action cannot dispatch due to no ship stock
        /// at the origin node; consumed by <c>FleetManager</c> to dispatch ferry convoys.</summary>
        public List<DemandEntry> SnapshotPositioningDemands(string factionTag)
        {
            var result = new List<DemandEntry>();
            foreach (DemandEntry d in _demands)
                if (d.FactionTag == factionTag && d.Kind.StartsWith("Position:"))
                    result.Add(d);
            return result;
        }

        /// <summary>Returns a snapshot of all active "Item:*" demands for a faction.</summary>
        public List<DemandEntry> SnapshotItemDemands(string factionTag)
        {
            var result = new List<DemandEntry>();
            foreach (DemandEntry d in _demands)
                if (d.FactionTag == factionTag && d.Kind.StartsWith("Item:"))
                    result.Add(d);
            return result;
        }

        /// <summary>Returns a snapshot of every active demand of any kind for a faction — used to seed
        /// a staged Operations cycle's read set/upsert view in one call rather than unioning the
        /// per-kind snapshots above.</summary>
        public List<DemandEntry> SnapshotFactionDemands(string factionTag)
        {
            var result = new List<DemandEntry>();
            foreach (DemandEntry d in _demands)
                if (d.FactionTag == factionTag)
                    result.Add(d);
            return result;
        }

        /// <summary>All active demands matching faction + exact kind (for external enumeration).</summary>
        public IEnumerable<DemandEntry> GetFor(string factionTag, string kind)
        {
            foreach (DemandEntry d in _demands)
                if (d.FactionTag == factionTag && d.Kind == kind)
                    yield return d;
        }

        /// <summary>Remove the first demand matching faction + kind + nodeId + destNodeId (fulfilled
        /// by a manager). <paramref name="destNodeId"/> must match whatever the demand was posted
        /// with — it's part of the dedup key so a route-carrying demand can coexist with other
        /// destinations at the same origin node.</summary>
        public void Consume(string factionTag, string kind, string nodeId, string destNodeId = null,
            bool destinedForSale = false, string requiredGridSize = null)
        {
            DemandKey key = new DemandKey(factionTag, kind, nodeId, destNodeId, destinedForSale,
                requiredGridSize);
            int index;
            if (!_indexes.TryGetValue(key, out index)) return;
            if (_clock != null) RecordEnded(_demands[index], _clock(), true);
            _demands.RemoveAt(index);
            RebuildIndex();
        }

        /// <summary>Removes EVERY entry matching faction + kind + nodeId, whatever its
        /// <see cref="DemandEntry.DestNodeId"/>, and returns how many went. <see cref="ShipyardManager"/>
        /// maintains exactly one <c>BuildQueueEntry</c> per (node, role), so the several destination-
        /// keyed demands that can share one build slot are a single unit of need — retiring only the
        /// one that happened to drive the build leaves its siblings alive to re-drive it. One backward
        /// pass and one <see cref="RebuildIndex"/>, rather than the full index rebuild <see cref="Consume"/>
        /// costs per call.</summary>
        public int ConsumeAll(string factionTag, string kind, string nodeId, bool destinedForSale = false)
        {
            int removed = 0;
            for (int i = _demands.Count - 1; i >= 0; i--)
            {
                DemandEntry d = _demands[i];
                if (!string.Equals(d.FactionTag, factionTag, StringComparison.Ordinal)
                    || !string.Equals(d.Kind, kind, StringComparison.Ordinal)
                    || !string.Equals(d.NodeId, nodeId, StringComparison.Ordinal)
                    || d.DestinedForSale != destinedForSale) continue;
                if (_clock != null) RecordEnded(d, _clock(), true);
                _demands.RemoveAt(i);
                removed++;
            }
            if (removed > 0) RebuildIndex();
            return removed;
        }

        /// <summary>Remove all expired entries. Call once per manager tick before reading demands.
        /// <paramref name="removedShipDemands"/>, when supplied, collects the expired <c>Ship:*</c>
        /// entries so a caller holding a log buffer can report them; <c>Item:</c>/<c>Position:</c>
        /// expiries are never collected (they churn far harder and nothing reports them today).</summary>
        public void Prune(DateTime utcNow, List<DemandEntry> removedShipDemands = null)
        {
            bool changed = false;
            for (int i = _demands.Count - 1; i >= 0; i--)
            {
                if (_demands[i].ExpiresUtc > utcNow) continue;
                if (removedShipDemands != null && _demands[i].Kind != null
                    && _demands[i].Kind.StartsWith("Ship:", StringComparison.Ordinal))
                    removedShipDemands.Add(_demands[i]);
                RecordEnded(_demands[i], utcNow, false);
                _demands.RemoveAt(i);
                changed = true;
            }
            if (changed) RebuildIndex();
            PruneEnded(utcNow);
        }

        /// <summary>Every live <c>Item:</c> and <c>Ship:</c> demand <paramref name="factionTag"/> has
        /// pinned to <paramref name="nodeId"/>, in register order. <c>Need:</c> rows are left out: each
        /// rides beside an <c>Item:</c> row for the same shortfall. Faction-wide rows are left out too —
        /// they are owed at no node in particular.</summary>
        public List<DemandEntry> SnapshotNodeDemands(string factionTag, string nodeId)
        {
            var result = new List<DemandEntry>();
            if (string.IsNullOrEmpty(nodeId)) return result;
            foreach (DemandEntry d in _demands)
                if (d.FactionTag == factionTag && string.Equals(d.NodeId, nodeId, StringComparison.Ordinal)
                    && IsWindowKind(d.Kind))
                    result.Add(d);
            return result;
        }

        /// <summary>The demands <paramref name="factionTag"/> had pinned to <paramref name="nodeId"/>
        /// that ended inside <see cref="EndedRetentionSeconds"/> of <paramref name="utcNow"/>, most
        /// recently ended first. Filtered by faction so a node that changed hands shows only its
        /// current owner's.</summary>
        public List<DemandEpisode> SnapshotEndedAtNode(string factionTag, string nodeId, DateTime utcNow)
        {
            var result = new List<DemandEpisode>();
            List<DemandEpisode> ended;
            if (string.IsNullOrEmpty(nodeId) || !_ended.TryGetValue(nodeId, out ended)) return result;
            DateTime cutoff = utcNow.AddSeconds(-EndedRetentionSeconds);
            for (int i = ended.Count - 1; i >= 0; i--)
                if (ended[i].FactionTag == factionTag && ended[i].EndedUtc >= cutoff) result.Add(ended[i]);
            return result;
        }

        /// <summary>Every live <c>Item:</c> and <c>Ship:</c> demand <paramref name="factionTag"/> has,
        /// pinned to a node or faction-wide, in register order. <c>Need:</c> rows are left out for the
        /// reason <see cref="SnapshotNodeDemands"/> gives; <see cref="SnapshotFactionDemands"/> is
        /// every kind.</summary>
        public List<DemandEntry> SnapshotFactionWindowDemands(string factionTag)
        {
            var result = new List<DemandEntry>();
            foreach (DemandEntry d in _demands)
                if (d.FactionTag == factionTag && IsWindowKind(d.Kind))
                    result.Add(d);
            return result;
        }

        /// <summary>The demands <paramref name="factionTag"/> had that ended inside
        /// <see cref="EndedRetentionSeconds"/> of <paramref name="utcNow"/> — its faction-wide ones and
        /// those pinned to any of <paramref name="nodeIds"/> — most recently ended first. The caller
        /// names the nodes (the faction's own) so the read stays proportional to the faction rather
        /// than to every node in the world.</summary>
        public List<DemandEpisode> SnapshotEndedForFaction(string factionTag, IEnumerable<string> nodeIds,
            DateTime utcNow)
        {
            var result = new List<DemandEpisode>();
            if (string.IsNullOrEmpty(factionTag)) return result;
            DateTime cutoff = utcNow.AddSeconds(-EndedRetentionSeconds);
            List<DemandEpisode> ended;
            if (_endedFactionWide.TryGetValue(factionTag, out ended))
                AppendEnded(result, ended, factionTag, cutoff);
            if (nodeIds != null)
                foreach (string nodeId in nodeIds)
                    if (!string.IsNullOrEmpty(nodeId) && _ended.TryGetValue(nodeId, out ended))
                        AppendEnded(result, ended, factionTag, cutoff);
            // Each list is in time order; the merge is not. A stable sort keeps a tie in arrival order.
            var order = new List<KeyValuePair<int, DemandEpisode>>(result.Count);
            for (int i = 0; i < result.Count; i++) order.Add(new KeyValuePair<int, DemandEpisode>(i, result[i]));
            order.Sort((a, b) =>
            {
                int byTime = b.Value.EndedUtc.CompareTo(a.Value.EndedUtc);
                return byTime != 0 ? byTime : b.Key.CompareTo(a.Key);
            });
            for (int i = 0; i < order.Count; i++) result[i] = order[i].Value;
            return result;
        }

        private static void AppendEnded(List<DemandEpisode> into, List<DemandEpisode> ended, string factionTag,
            DateTime cutoff)
        {
            for (int i = 0; i < ended.Count; i++)
                if (ended[i].FactionTag == factionTag && ended[i].EndedUtc >= cutoff) into.Add(ended[i]);
        }

        private static bool IsWindowKind(string kind)
        {
            return kind != null && (kind.StartsWith(ItemPrefix, StringComparison.Ordinal)
                                    || kind.StartsWith("Ship:", StringComparison.Ordinal));
        }

        private void RecordEnded(DemandEntry d, DateTime endedUtc, bool consumed)
        {
            if (!IsWindowKind(d.Kind)) return;
            bool nodeless = string.IsNullOrEmpty(d.NodeId);
            if (nodeless && string.IsNullOrEmpty(d.FactionTag)) return;
            Dictionary<string, List<DemandEpisode>> store = nodeless ? _endedFactionWide : _ended;
            string storeKey = nodeless ? d.FactionTag : d.NodeId;
            List<DemandEpisode> ended;
            if (!store.TryGetValue(storeKey, out ended))
            {
                ended = new List<DemandEpisode>();
                store[storeKey] = ended;
            }
            if (ended.Count >= (nodeless ? MaxEndedFactionWidePerFaction : MaxEndedPerNode)) ended.RemoveAt(0);
            ended.Add(new DemandEpisode
            {
                FactionTag = d.FactionTag,
                Kind = d.Kind,
                NodeId = d.NodeId,
                DestNodeId = d.DestNodeId,
                DestinedForSale = d.DestinedForSale,
                Class = d.Class,
                PeakQuantity = Math.Max(d.PeakQuantity, d.Quantity),
                FirstPostedUtc = d.FirstPostedUtc,
                EndedUtc = endedUtc,
                Consumed = consumed,
            });
        }

        /// <summary>Drops ended demands past the window. A list is appended in time order, so only
        /// its head can be stale and the common case is one comparison per node or faction.</summary>
        private void PruneEnded(DateTime utcNow)
        {
            DateTime cutoff = utcNow.AddSeconds(-EndedRetentionSeconds);
            PruneEnded(_ended, cutoff);
            PruneEnded(_endedFactionWide, cutoff);
        }

        private static void PruneEnded(Dictionary<string, List<DemandEpisode>> store, DateTime cutoff)
        {
            if (store.Count == 0) return;
            List<string> empty = null;
            foreach (KeyValuePair<string, List<DemandEpisode>> kv in store)
            {
                List<DemandEpisode> ended = kv.Value;
                int stale = 0;
                while (stale < ended.Count && ended[stale].EndedUtc < cutoff) stale++;
                if (stale == 0) continue;
                ended.RemoveRange(0, stale);
                if (ended.Count == 0)
                {
                    if (empty == null) empty = new List<string>();
                    empty.Add(kv.Key);
                }
            }
            if (empty != null) foreach (string key in empty) store.Remove(key);
        }

        public int Count { get { return _demands.Count; } }

        /// <summary>Total outstanding "Need:" quantity a faction has for one concrete inventory key,
        /// summed across every node that raised one. O(1). Zero when nothing is committed to it.
        /// <para>Expiry is <see cref="Prune"/>-driven, exactly like every other demand read: a need
        /// stops counting when its row expires or its poster stops refreshing it, which is what
        /// happens the moment the commitment behind it is satisfied.</para></summary>
        public long OperationalDemandFor(string factionTag, string inventoryKey)
        {
            if (string.IsNullOrEmpty(factionTag) || string.IsNullOrEmpty(inventoryKey)) return 0;
            long quantity;
            return _operationalByItem.TryGetValue(OperationalMapKey(factionTag, inventoryKey), out quantity)
                ? quantity : 0;
        }

        /// <summary>Outstanding "Need:" quantity for one concrete inventory key that is owed at
        /// <paramref name="nodeId"/> specifically — a ship BOM at its shipyard. O(1); zero when
        /// nothing is pinned there. Disjoint from <see cref="OperationalDemandFor"/>, which covers
        /// only the faction-wide needs no single node owes.</summary>
        public long OperationalDemandAtNode(string nodeId, string inventoryKey)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(inventoryKey)) return 0;
            long quantity;
            return _operationalByNodeItem.TryGetValue(OperationalMapKey(nodeId, inventoryKey), out quantity)
                ? quantity : 0;
        }

        /// <summary>Outstanding ordinary "Item:" want for one concrete key that is posted at
        /// <paramref name="nodeId"/> specifically. O(1). Zero when nothing there wants it.
        /// <para>A <c>Quantity</c> of 0 is "unspecified", which several posters use legitimately, so
        /// this counts <b>rows</b> rather than units — the question it answers is "does anything here
        /// want this at all", not "how much".</para></summary>
        public int ItemDemandRowsAtNode(string nodeId, string inventoryKey)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(inventoryKey)) return 0;
            long rows;
            return _itemByNodeItem.TryGetValue(OperationalMapKey(nodeId, inventoryKey), out rows) ? (int)rows : 0;
        }

        /// <summary>The faction-wide counterpart of <see cref="ItemDemandRowsAtNode"/> — every row the
        /// faction raised for the key, pinned or not, so a shipyard's BOM row counts at each of the
        /// faction's nodes. O(1).</summary>
        public int ItemDemandRowsFor(string factionTag, string inventoryKey)
        {
            if (string.IsNullOrEmpty(factionTag) || string.IsNullOrEmpty(inventoryKey)) return 0;
            long rows;
            return _itemByItem.TryGetValue(OperationalMapKey(factionTag, inventoryKey), out rows) ? (int)rows : 0;
        }

        private static string OperationalMapKey(string scope, string inventoryKey)
        {
            return scope + "\u0001" + inventoryKey;
        }

        /// <summary>Adds (<paramref name="sign"/> = 1) or removes (-1) one entry's contribution to
        /// <see cref="_operationalByItem"/>. A non-"Need:" entry contributes nothing.</summary>
        private void AdjustOperational(DemandEntry entry, int sign)
        {
            if (entry.Quantity <= 0 || entry.Kind == null
                || !entry.Kind.StartsWith(NeedPrefix, StringComparison.Ordinal)) return;
            if (string.IsNullOrEmpty(entry.FactionTag)) return;

            string invKey = entry.Kind.Substring(NeedPrefix.Length);
            if (string.IsNullOrEmpty(entry.NodeId))
                Accumulate(_operationalByItem, OperationalMapKey(entry.FactionTag, invKey), sign * entry.Quantity);
            else
                Accumulate(_operationalByNodeItem, OperationalMapKey(entry.NodeId, invKey), sign * entry.Quantity);
        }

        /// <summary>The <see cref="ItemPrefix"/> counterpart of <see cref="AdjustOperational"/>.
        /// Counts rows, not units: a "Item:" demand may legitimately carry Quantity 0.</summary>
        private void AdjustItem(DemandEntry entry, int sign)
        {
            if (entry.Kind == null || !entry.Kind.StartsWith(ItemPrefix, StringComparison.Ordinal)) return;
            if (string.IsNullOrEmpty(entry.FactionTag)) return;

            string invKey = entry.Kind.Substring(ItemPrefix.Length);
            if (string.IsNullOrEmpty(invKey)) return;
            Accumulate(_itemByItem, OperationalMapKey(entry.FactionTag, invKey), sign);
            if (!string.IsNullOrEmpty(entry.NodeId))
                Accumulate(_itemByNodeItem, OperationalMapKey(entry.NodeId, invKey), sign);
        }

        private void Accumulate(Dictionary<string, long> map, string mapKey, long delta)
        {
            if (delta == 0) return;
            long current;
            map.TryGetValue(mapKey, out current);
            long updated = current + delta;
            if (updated > 0) map[mapKey] = updated;
            else map.Remove(mapKey);
            _operationalRevision++;
        }

        private void RebuildIndex()
        {
            _indexes.Clear();
            _operationalByItem.Clear();
            _operationalByNodeItem.Clear();
            _itemByItem.Clear();
            _itemByNodeItem.Clear();
            for (int i = 0; i < _demands.Count; i++)
            {
                DemandEntry entry = _demands[i];
                _indexes[new DemandKey(entry.FactionTag, entry.Kind, entry.NodeId, entry.DestNodeId,
                    entry.DestinedForSale, entry.RequiredGridSize)] = i;
                AdjustOperational(entry, 1);
                AdjustItem(entry, 1);
            }
        }
    }
}
