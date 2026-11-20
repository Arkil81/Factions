using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Factions.Core;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// Populates SE NPC faction station store lists with brain-driven sell offers and buy orders.
    /// Prices are <c>base_price × ScarcityMultiplier</c> with no SE price-floor clamping, resolved
    /// per inventory key rather than per <c>ItemKind</c> — two components of one kind can differ by
    /// four orders of magnitude in book value and do not share a shelf.
    ///
    /// <para>Writes directly to <c>MyFactionStation.StoreItems</c> (the list served to clients
    /// for NPC stations) rather than through <c>InsertOffer</c>/<c>InsertOrder</c>, which would
    /// impose SE's minimum price floor and write to the wrong list (<c>PlayerItems</c>).</para>
    ///
    /// <para>Called on the game thread from <see cref="FactionsPlugin.Update"/> on the GPS
    /// refresh cadence (~5 s). Clears and re-inserts all items each tick so prices stay current.</para>
    /// </summary>
    internal sealed class StationStoreManager : IStationListingPolicy
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>Per-process, off by default, toggled by <c>factions debug store</c>. Gated at
        /// every call site so the string is never built when it is off -- the Torch log is where the
        /// engine's own stall diagnostics land, and one line per station per ~5s refresh is exactly
        /// the shape that drowns them.</summary>
        internal static bool DebugEnabled;

        private readonly WorldModel             _world;
        private readonly IPriceOracle           _oracle;
        private readonly IItemRegistry          _registry;
        private readonly IItemPriceCatalogue    _prices;
        private readonly FactionTuning          _tuning;
        private readonly HashSet<string>        _registeredTags;
        private readonly Action<string>         _recordActivity; // notify GlobalPriceUpdater
        private readonly IFactionTypeCatalogue  _factionTypes;   // null = fallback to inventory-threshold buy orders
        private readonly IPrefabCatalogue       _prefabCatalogue; // null = ship listings have price 0
        private readonly PrefabBookValueCache   _prefabValues;   // retained for station book values (not ship pricing)

        // invKey-format → SerializableDefinitionId, built lazily on first Refresh.
        private Dictionary<string, SerializableDefinitionId> _defIdCache;
        // Windowed buy-order budget, one per faction, same shape FleetManager uses for Reinforce and
        // Patrol. In-memory like every other SpendBudget; a restart just starts a fresh window.
        private readonly SpendBudget _buyBudget = new SpendBudget();
        // Reaches CentralBankRecentIncome, which WorldModel does not expose. Null leaves the buy
        // budget uncapped, which is the pre-FAC-159 behaviour rather than a silent zero.
        private readonly Func<string, FactionState> _stateLookup;
        // invKey-format → TypeId string (e.g. "MyObjectBuilder_Ore"), parallel to _defIdCache.
        private Dictionary<string, string> _defIdTypeCache;
        // prefabId → PCU count, built lazily alongside _defIdCache.
        private Dictionary<string, int> _prefabPcuCache;

        // StableStationId (MyFactionStation.Id) -> station, rebuilt on a short TTL. Replaces an
        // unindexed per-node linear scan over every faction's Stations list (O(nodes × stations)
        // per RefreshAll/ClearAll pass) that measured live at ~1.88s in a single call — see
        // docs/todo.md's "Contract generation burst" entry, 2026-08-09.
        private static readonly TimeSpan StationIndexTtl = TimeSpan.FromSeconds(5);
        // Reused across nodes within one RefreshAll pass; the store refresh runs on the ~5s GPS
        // cadence over every station, so a per-node allocation here is a per-tick allocation.
        private readonly List<StationListing> _listings = new List<StationListing>();

        private Dictionary<long, MyFactionStation> _stationIndex;
        private DateTime _stationIndexBuiltUtc;

        public StationStoreManager(
            WorldModel world,
            IPriceOracle oracle,
            IItemRegistry registry,
            IItemPriceCatalogue prices,
            FactionTuning tuning,
            HashSet<string> registeredTags,
            Action<string> recordActivity,
            IFactionTypeCatalogue factionTypes  = null,
            IPrefabCatalogue prefabCatalogue    = null,
            PrefabBookValueCache prefabValues   = null,
            Func<string, FactionState> stateLookup = null)
        {
            _world           = world;
            _oracle          = oracle;
            _registry        = registry;
            _prices          = prices;
            _tuning          = tuning;
            _registeredTags  = registeredTags;
            _recordActivity  = recordActivity;
            _stateLookup     = stateLookup;
            _factionTypes    = factionTypes;
            _prefabCatalogue = prefabCatalogue;
            _prefabValues    = prefabValues;
        }

        /// <summary>Refresh NPC station store lists for all registered-faction nodes. Call on the game thread.</summary>
        public void RefreshAll()
        {
            if (_world?.Graph == null || MySession.Static == null) return;

            EnsureDefIdCache();

            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (node.OwnerFactionTag == null || node.OwnerFactionTag == NeutralFaction.Tag) continue;
                if (!_registeredTags.Contains(node.OwnerFactionTag)) continue;
                if (node.StableStationId == 0) continue;

                RefreshNode(node);
            }
        }

        private readonly List<SpatialNode> _pass = new List<SpatialNode>();
        private int _passNext;

        /// <summary>Starts a refresh of every managed station with a live grid, done a few at a time by
        /// <see cref="RefreshPassStep"/>. Rebuilding them all at once was one frame per refresh: 41 ms
        /// for 7 live stations at the end of the 23:20:27 box-local economy tick 2026-10-04, and up to
        /// 81.6 ms on the periodic cadence (FAC-445). A pass already under way restarts.</summary>
        public void BeginRefreshPass()
        {
            _pass.Clear();
            _passNext = 0;
            if (_world?.Graph == null || MySession.Static == null) return;
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (node.OwnerFactionTag == null || node.OwnerFactionTag == NeutralFaction.Tag) continue;
                if (!_registeredTags.Contains(node.OwnerFactionTag)) continue;
                if (node.StableStationId == 0) continue;
                MyFactionStation station = FindStation(node);
                if (station == null || !EconomyTickSlicer.IsLive(station)) continue;
                _pass.Add(node);
            }
        }

        /// <summary>Refreshes stations of the pass until <paramref name="budgetMs"/> is spent, at least one.
        /// Game thread, once per frame.</summary>
        public void RefreshPassStep(double budgetMs)
        {
            if (_passNext >= _pass.Count) return;
            if (MySession.Static == null) { _pass.Clear(); _passNext = 0; return; }
            EnsureDefIdCache();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double limit = budgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0;
            do
            {
                RefreshNode(_pass[_passNext++]);
            }
            while (_passNext < _pass.Count && System.Diagnostics.Stopwatch.GetTimestamp() - t0 < limit);
            if (_passNext >= _pass.Count) { _pass.Clear(); _passNext = 0; }
        }

        /// <summary>
        /// Clear all managed station store items without re-populating.  Call immediately after SE's
        /// economy tick to strip any vanilla-generated entries before our next <see cref="RefreshAll"/>.
        /// </summary>
        public void ClearAll()
        {
            if (_world?.Graph == null || MySession.Static == null) return;

            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (node.OwnerFactionTag == null || node.OwnerFactionTag == NeutralFaction.Tag) continue;
                if (!_registeredTags.Contains(node.OwnerFactionTag)) continue;
                if (node.StableStationId == 0) continue;

                MyFactionStation station = FindStation(node);
                if (station == null || !EconomyTickSlicer.IsLive(station)) continue;
                station.StoreItems.Clear();
            }
        }

        /// <summary>Whether this node's owner trades with players at all — the gate that has to sit
        /// above <i>both</i> listing entry points, not on the order keys.
        ///
        /// <para><see cref="StationListings.Build"/> runs <c>AppendOffers</c> regardless of the order
        /// keys and routes an empty order list to the inventory-threshold fallback, so a faction
        /// excluded here would otherwise keep a fully working store on both sides. That is exactly
        /// how FCTM, whose type resolves to <c>None</c>, came to have one (FAC-152/FAC-158).</para></summary>
        internal bool HasPlayerStore(SpatialNode node)
        {
            return node != null
                && FactionStorePolicy.HasPlayerStore(_factionTypes, _tuning, node.OwnerFactionTag);
        }

        /// <summary>The owning faction type's OrdersList, or null when there is no catalogue — which
        /// selects the Core builder's inventory-threshold fallback. Either way the faction's own
        /// demand joins the candidates inside <see cref="StationListings.Build"/>.</summary>
        private IReadOnlyList<string> OrderKeysFor(SpatialNode node)
        {
            string factionType;
            if (_factionTypes == null
             || !_factionTypes.TryGetFactionType(node.OwnerFactionTag, out factionType)) return null;
            return _factionTypes.OrderInvKeys(factionType);
        }

        // ---- Factions.Core.IStationListingPolicy ------------------------------------------------
        // The two host-owned facts the Core builder cannot know. An interface rather than lambdas so
        // every consumer passes the same object; see IStationListingPolicy.
        bool IStationListingPolicy.CanList(string invKey)
        {
            ItemKind kind;
            SerializableDefinitionId defId;
            return TryGetItemKind(invKey, out kind) && TryGetDefinitionId(invKey, out defId);
        }

        long IStationListingPolicy.ListingCap(string invKey)
        {
            return ListingCap(invKey);
        }

        /// <summary>Every row this node's store would carry, for a caller that is not the store
        /// block — the market surfaces, which must work for stations whose grid is not spawned.</summary>
        /// <summary>Returns how many OrdersList candidates were rejected for want of demand, so the
        /// signature is a plain delegate — an <c>out</c> parameter cannot ride on a <c>Func</c>.</summary>
        internal int BuildListings(SpatialNode node, List<StationListing> into)
        {
            if (!HasPlayerStore(node)) return 0;

            int noDemand;
            int budgetTrimmed;
            int noHeadroom;
            int offList;
            StationListings.Build(
                node, OrderKeysFor(node), _oracle, _prices,
                _world != null ? _world.Demand : null, this,
                _world != null ? _world.UtcNow : DateTime.UtcNow, into, out noDemand,
                BuyBudgetFor(node),
                _tuning == null || _tuning.OffersNeedWeighted,
                _tuning == null || _tuning.StationOreOrderPriority,
                out budgetTrimmed, out noHeadroom, out offList);
            return noDemand;
        }

        /// <summary>Credits this station's owner could commit across all of its buy orders this
        /// window. Sizes the listing; it does not reserve anything — money moves only when a player
        /// actually sells, at which point <see cref="HandleStationTransaction"/> records the real
        /// spend against the same window so a second station cannot re-promise it.</summary>
        private long BuyBudgetFor(SpatialNode node)
        {
            if (_world == null || _tuning == null || string.IsNullOrEmpty(node.OwnerFactionTag))
                return StationListings.Uncapped;

            if (_stateLookup == null) return StationListings.Uncapped;
            FactionState st = _stateLookup(node.OwnerFactionTag);
            if (st == null) return StationListings.Uncapped;

            long surplus = _tuning.StationBuyWealthFractionPerWindow > 0
                ? Economy.SpendableTreasurySurplus(
                    _world.GetTreasury(node.OwnerFactionTag), _world.GetNetFlow(node.OwnerFactionTag), _tuning)
                : 0;

            return _buyBudget.RemainingBudget(
                node.OwnerFactionTag, _world.UtcNow, st.CentralBankRecentIncome,
                _tuning.StationBuyMaxIncomeFractionPerWindow, _tuning.StationBuyBudgetFloor,
                _tuning.SpendBudgetWindowSeconds,
                surplus, _tuning.StationBuyWealthFractionPerWindow);
        }

        private void RefreshNode(SpatialNode node)
        {
            MyFactionStation station = FindStation(node);
            // No live grid means no player can reach this station's store GUI — skip the
            // (potentially expensive) population work. A live grid, not a non-zero StationEntityId:
            // 511 of 518 stations kept an id whose grid was gone (2026-10-04 16:18:48), and rebuilding
            // their stores made the periodic refresh peak at 86.9 ms and the economy tick's end 63-71 ms.
            if (station == null || !EconomyTickSlicer.IsLive(station)) return;

            station.StoreItems.Clear();
            PopulateStation(node, station);
        }

        private void PopulateStation(SpatialNode node, MyFactionStation station)
        {
            // Caller has already cleared StoreItems, so returning here leaves an empty store rather
            // than a stale one.
            if (!HasPlayerStore(node)) return;

            string nodeId = node.Id;
            int offers = 0, orders = 0, ships = 0;

            // Offers and buy orders both come from the shared Core rule, so a market view of a
            // station whose grid is NOT spawned cannot disagree with the list a player sees on
            // arrival. Only the SE mapping below is host work.
            _listings.Clear();
            int oreOrders = 0;
            int noDemand;
            int budgetTrimmed;
            int noHeadroom;
            int offList;
            long budget = BuyBudgetFor(node);
            // Collected only when someone is reading the log: a refusal list is per-refresh garbage
            // and every station refreshes on a timer whether or not anyone is watching.
            List<StationBudgetRefusal> refusals = DebugEnabled ? new List<StationBudgetRefusal>() : null;
            StationListings.Build(
                node, OrderKeysFor(node), _oracle, _prices,
                _world != null ? _world.Demand : null, this,
                _world != null ? _world.UtcNow : DateTime.UtcNow,
                _listings, out noDemand,
                budget,
                _tuning == null || _tuning.OffersNeedWeighted,
                _tuning == null || _tuning.StationOreOrderPriority,
                out budgetTrimmed, out noHeadroom, out offList, refusals);

            WriteRows(station, nodeId, _listings, out offers, out orders, out oreOrders);

            // ---- ship listings: store-reserved copies in ListedForSale -----------------------
            int shipsGated;
            ships = WriteShips(station, node.OwnerFactionTag, ShipShelf(node, out shipsGated));

            if (DebugEnabled)
            {
                Log.Info(string.Format(
                    "STORE_LIST node={0} faction={1} offers={2} orders={3} oreorders={4} offlist={5} nodemand={6} ships={7} shipsgated={8} budgettrimmed={9} noheadroom={10} budget={11}",
                    nodeId, node.OwnerFactionTag, offers, orders, oreOrders, offList, noDemand, ships, shipsGated,
                    budgetTrimmed, noHeadroom,
                    budget == StationListings.Uncapped ? "uncapped" : budget.ToString()));
                LogRefusals(nodeId, node.OwnerFactionTag, budget, refusals);
            }
        }




        /// <summary>Names the buy rows the budget could not seat, most expensive first.
        ///
        /// <para>The line FAC-176 needed and did not have. <c>budgettrimmed</c> is a bare count, so
        /// reconstructing which twelve items ISBT N007 refused took a world-save read plus a price
        /// table — and the reconstruction then rested on the wrong budget figure, because the count
        /// names neither the price that refused the row nor the budget it was refused against.
        /// Both are on this line.</para>
        ///
        /// <para>Most expensive first because that is the end of the distribution that gets refused:
        /// a refusal is <c>budget / unitPrice &lt; 1</c>, so the top of this list is exactly the set
        /// a bigger budget would buy back.</para></summary>
        private static void LogRefusals(
            string nodeId, string factionTag, long budget, List<StationBudgetRefusal> refusals)
        {
            if (refusals == null || refusals.Count == 0) return;

            refusals.Sort(delegate (StationBudgetRefusal a, StationBudgetRefusal b)
            {
                int byPrice = b.UnitPrice.CompareTo(a.UnitPrice);
                return byPrice != 0 ? byPrice : string.CompareOrdinal(a.InvKey, b.InvKey);
            });

            var sb = new System.Text.StringBuilder();
            int shown = refusals.Count < MaxRefusalsLogged ? refusals.Count : MaxRefusalsLogged;
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(refusals[i].InvKey).Append('@').Append(refusals[i].UnitPrice)
                  .Append("x").Append(refusals[i].WantUnits);
            }

            Log.Info(string.Format(
                "STORE_TRIM node={0} faction={1} budget={2} refused={3} of {4}: {5}",
                nodeId, factionTag, budget == StationListings.Uncapped ? "uncapped" : budget.ToString(),
                shown, refusals.Count, sb));
        }

        /// <summary>Enough to see the shape of a refusal set without turning one store refresh into
        /// a page of log. The count on the line itself says how many were elided.</summary>
        private const int MaxRefusalsLogged = 6;

        // Faction-built ships listed for sale: drawn from the whole owned network so a hull built
        // at any Shipyard can be sold at any of the faction's economy stations, then FILTERED to
        // what a buyer could actually fly away from THIS station.
        //
        // The pool is shared; the shelf is local. Region and gravity are properties of where the
        // hull is sold, not of the network that owns it, so two stations of one faction in
        // different regimes legitimately show different shelves (FAC-060, operator 2026-09-03).
        // Gating at the yard instead would give both the same one.
        //
        // Empty prefab IDs (free-build mode) are skipped — SE cannot spawn an unnamed grid.
        // Reads SpatialNode.ListedForSale — the store-reserved slice of the unified ship pool —
        // never the faction's whole ShipStock, so active military/trade ships are never accidentally
        // offered for sale.
        private List<NexusShelfShip> ShipShelf(SpatialNode node, out int withheld)
        {
            withheld = 0;
            var shelf = new List<NexusShelfShip>();
            string factionTag = node.OwnerFactionTag;
            if (string.IsNullOrEmpty(factionTag)) return shelf;
            EnsureDefIdCache();

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (SpatialNode src in _world.Graph.OwnedBy(factionTag))
                foreach (var kv in src.ListedForSale)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                    // Same physics the builder applies, evaluated at the SELLING station.
                    if (!StoreShelfPlan.IsShelfableAt(_prefabCatalogue, kv.Key, _world.Planets,
                                                      node.Position, _tuning.MinDryTwr))
                    {
                        withheld += kv.Value;
                        continue;
                    }
                    int c; counts.TryGetValue(kv.Key, out c);
                    counts[kv.Key] = c + kv.Value;
                }

            foreach (KeyValuePair<string, int> prefabPair in counts)
            {
                long bookValue = _prefabCatalogue != null ? _prefabCatalogue.Cost(prefabPair.Key) : 0;
                int pcu;
                _prefabPcuCache.TryGetValue(prefabPair.Key, out pcu);
                shelf.Add(new NexusShelfShip
                {
                    Prefab = prefabPair.Key, Count = prefabPair.Value, Price = Math.Max(1, bookValue), Pcu = pcu,
                });
            }
            return shelf;
        }

        /// <summary>The store rows: item and gas offers and orders, in the order given.</summary>
        private void WriteRows(MyFactionStation station, string nodeId, List<StationListing> rows,
                               out int offers, out int orders, out int oreOrders)
        {
            offers = 0; orders = 0; oreOrders = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                StationListing row = rows[i];
                if (string.IsNullOrEmpty(row.InvKey)) continue;
                bool isOffer = row.Side == StationListingSide.Offer;

                if (InventoryKeyMapper.IsGasKey(row.InvKey))
                {
                    if (TryAddGasRow(station, nodeId, row, isOffer)) { if (isOffer) offers++; else orders++; }
                    continue;
                }

                SerializableDefinitionId defId;
                if (!TryGetDefinitionId(row.InvKey, out defId)) continue;   // CanList already passed

                station.StoreItems.Add(new MyStoreItem(
                    MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.STORE_ITEM),
                    (MyDefinitionId)defId,
                    (int)Math.Min(row.Quantity, int.MaxValue),
                    (int)Math.Min(row.UnitPrice, int.MaxValue),
                    ItemTypes.PhysicalItem,
                    isOffer ? StoreItemTypes.Offer : StoreItemTypes.Order));

                if (isOffer) offers++;
                else
                {
                    orders++;
                    if (InventoryKeyMapper.IsOreKey(row.InvKey)) oreOrders++;
                }
            }
        }

        /// <summary>The shelf hulls, each remembered as <paramref name="factionTag"/>'s so a sale is
        /// booked to the seller.</summary>
        private int WriteShips(MyFactionStation station, string factionTag, List<NexusShelfShip> ships)
        {
            if (ships == null || string.IsNullOrEmpty(factionTag)) return 0;
            int listings = 0;
            for (int i = 0; i < ships.Count; i++)
            {
                NexusShelfShip s = ships[i];
                if (s == null || string.IsNullOrEmpty(s.Prefab) || s.Count <= 0) continue;
                var item = new MyStoreItem(
                    MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.STORE_ITEM),
                    s.Prefab, s.Count, (int)Math.Min(Math.Max(1, s.Price), int.MaxValue), s.Pcu, StoreItemTypes.Offer);

                // SE's grid purchase never raises the item's OnTransaction, so the sale is heard by
                // StoreShipSaleMarks, which asks this table whether the item bought is a shelf hull.
                ShipListings.Add(item, new ShipListing { FactionTag = factionTag });

                station.StoreItems.Add(item);
                listings++;
            }
            return listings;
        }

        // ---- Nexus: the authority prices, an agent's store block shows ---------------------------

        /// <summary>Authority: what this node's store block carries, for a station standing on a Nexus
        /// agent. The same rows and shelf hulls <see cref="PopulateStation"/> writes here.</summary>
        internal NexusShelf BuildShelf(SpatialNode node)
        {
            var shelf = new NexusShelf { Node = node.Id, Seller = node.OwnerFactionTag };
            if (!HasPlayerStore(node)) return shelf;
            var rows = new List<StationListing>();
            BuildListings(node, rows);
            if (rows.Count > 0) shelf.Rows = rows;
            int withheld;
            List<NexusShelfShip> ships = ShipShelf(node, out withheld);
            if (ships.Count > 0) shelf.Ships = ships;
            return shelf;
        }

        /// <summary>Agent: replaces this node's store contents with the authority's shelf. False when
        /// the node has no SE station record here.</summary>
        internal bool ApplyShelf(SpatialNode node, NexusShelf shelf)
        {
            if (node == null || shelf == null || node.StableStationId == 0 || MySession.Static == null) return false;
            MyFactionStation station = FindStation(node);
            if (station == null) return false;
            station.StoreItems.Clear();
            int offers = 0, orders = 0, oreOrders = 0;
            if (shelf.Rows != null) WriteRows(station, node.Id, shelf.Rows, out offers, out orders, out oreOrders);
            int ships = WriteShips(station, shelf.Seller, shelf.Ships);
            if (DebugEnabled)
                Log.Info("STORE_SHELF node=" + node.Id + " faction=" + shelf.Seller + " offers=" + offers
                         + " orders=" + orders + " ships=" + ships);
            return true;
        }

        // ---- transaction callbacks (game thread via SE RPC) -------------------------------------

        /// <summary>
        /// Entry point for <see cref="BuyPhysicalItemInternalPatch"/>/<see cref="SellToStationPatch"/>
        /// -- vanilla has no callback of its own for a physical-item transaction at an NPC/faction
        /// station (see those patches' doc comments), so this Harmony-patch-driven path is the only
        /// way a real player buy/sell reaches <see cref="SpatialNode.Inventory"/>.
        /// </summary>
        public void HandleStationTransaction(long stationId, MyDefinitionId itemDefId, int amount, bool isBuy, long creditsDelta, long buyerIdentityId = 0)
        {
            HandleStationTransaction(stationId, InvKeyFor(itemDefId), amount, isBuy, creditsDelta, buyerIdentityId);
        }

        /// <summary>Inventory-key overload — see the <see cref="FactionsPlugin"/> counterpart for why
        /// gas cannot go through the definition-id one.</summary>
        /// <summary>Called once per completed player store transaction that resolved to a node,
        /// with the buyer's identity, the owning faction tag, the real wallet delta and the
        /// direction. Null (the default) records nothing — the goods and the credits still land, so
        /// the store works identically with no reputation model wired.</summary>
        public Action<long, string, long, bool> OnPlayerTrade { get; set; }

        public void HandleStationTransaction(long stationId, string invKey, int amount, bool isBuy, long creditsDelta, long buyerIdentityId = 0)
        {
            if (amount <= 0 || string.IsNullOrEmpty(invKey) || _world?.Graph == null) return;
            HandleNodeTransaction(FindNodeByStation(stationId), invKey, amount, isBuy, creditsDelta, buyerIdentityId);
        }

        /// <summary>A store transaction at <paramref name="node"/>'s station, which may stand on a
        /// Nexus agent.</summary>
        public void HandleNodeTransaction(SpatialNode node, string invKey, int amount, bool isBuy, long creditsDelta, long buyerIdentityId)
        {
            if (node == null || amount <= 0 || string.IsNullOrEmpty(invKey) || _world?.Graph == null) return;

            if (isBuy) OnPlayerBought(node.Id, invKey, amount);
            else       OnPlayerSold(node.Id, invKey, amount);

            // Faction ledger is authoritative; the SE wallet is a lazily-synced copy, not the driver
            // (design decision 2026-08-06). Read the real balance delta rather than recomputing price
            // ourselves -- it transparently absorbs friendly-relation bonuses/fees vanilla applies
            // that we don't separately model, and we never have to reconcile which ledger is "right".
            if (creditsDelta != 0 && !string.IsNullOrEmpty(node.OwnerFactionTag))
            {
                if (creditsDelta > 0)
                {
                    _world.Earn(node.OwnerFactionTag, creditsDelta);
                }
                else
                {
                    _world.Spend(node.OwnerFactionTag, -creditsDelta);
                    // Charge the same window the listing was sized against, so a second station
                    // cannot re-promise credits this one has already committed.
                    if (_tuning != null)
                        _buyBudget.Record(node.OwnerFactionTag, _world.UtcNow, -creditsDelta,
                            _tuning.SpendBudgetWindowSeconds);
                }
                Log.Info((creditsDelta > 0 ? "Faction ledger credited " : "Faction ledger debited ")
                    + Math.Abs(creditsDelta) + " for " + node.OwnerFactionTag + " (station " + node.Id + " trade)");
            }

            // Attribution, after the goods and the credits have landed: a reputation model that
            // throws must never cost the player the trade they actually made.
            if (buyerIdentityId != 0 && OnPlayerTrade != null && !string.IsNullOrEmpty(node.OwnerFactionTag))
            {
                try { OnPlayerTrade(buyerIdentityId, node.OwnerFactionTag, creditsDelta, isBuy); }
                catch (Exception ex) { Log.Warn(ex, "Player trade attribution failed."); }
            }
        }

        /// <summary>The node whose <see cref="SpatialNode.StableStationId"/> is this SE station, or
        /// null when the station belongs to nothing this brain owns.</summary>
        private SpatialNode FindNodeByStation(long stationId)
        {
            if (stationId == 0 || _world?.Graph == null) return null;
            foreach (SpatialNode n in _world.Graph.Nodes.Values)
                if (n.StableStationId == stationId) return n;
            return null;
        }

        /// <summary>Whether this SE station is one of ours, for a caller deciding whether an
        /// expensive census is worth taking at all. Same lookup the settlement handlers use, so it
        /// cannot answer yes to a station they would then decline.</summary>
        public bool HasStationNode(long stationId)
        {
            return FindNodeByStation(stationId) != null;
        }

        /// <summary>Whether the Services Terminal flows are being diverted into the sim at all.</summary>
        public bool ServicesSettlementEnabled
        {
            get { return _tuning != null && _tuning.ServicesTerminalSettlement; }
        }

        // ---- Services Terminal settlements (game thread via SE RPC) -----------------------------
        //
        // Vanilla's salvage, repair and grid-storage-expedite endpoints move credits to or from the
        // PLAYER's wallet with no counterparty and raise no callback, so like the store block these
        // only reach the sim through a Harmony patch -- see ServicesTerminalPatches. Provenance:
        // FAC-280.

        /// <summary>A salvage sale completed: the yard paid the player, so the faction spends and the
        /// wreck's parts land in the node.</summary>
        /// <param name="creditsPaid">The player's real wallet gain, always positive.</param>
        /// <param name="blocks">Blocks in the salvaged mechanical group, for the log line only.</param>
        /// <param name="components">Present-component census, inventory key -> units.</param>
        /// <param name="cargo">Cargo census aboard the wreck, inventory key -> units.</param>
        public void HandleSalvageSettled(
            long stationId, long buyerIdentityId, long creditsPaid, int blocks,
            IDictionary<string, long> components, IDictionary<string, long> cargo)
        {
            HandleSalvageSettled(FindNodeByStation(stationId), buyerIdentityId, creditsPaid, blocks, components, cargo);
        }

        public void HandleSalvageSettled(
            SpatialNode node, long buyerIdentityId, long creditsPaid, int blocks,
            IDictionary<string, long> components, IDictionary<string, long> cargo)
        {
            if (!ServicesSettlementEnabled || creditsPaid <= 0) return;
            if (node == null || string.IsNullOrEmpty(node.OwnerFactionTag)) return;

            double fraction = _tuning.SalvageRecoveryFraction;
            ServiceSettlement.SalvageCredit comps = ServiceSettlement.CreditSalvage(node.Inventory, components, fraction);
            ServiceSettlement.SalvageCredit holds = ServiceSettlement.CreditSalvage(node.Inventory, cargo, fraction);

            // A station purchase is a station purchase: charge the same window the buy orders are
            // sized against so a second station cannot re-promise credits this one just committed.
            _world.Spend(node.OwnerFactionTag, creditsPaid);
            _buyBudget.Record(node.OwnerFactionTag, _world.UtcNow, creditsPaid, _tuning.SpendBudgetWindowSeconds);

            if (comps.Units > 0 || holds.Units > 0) _recordActivity?.Invoke(node.Id);

            Log.Info("SALVAGE_SETTLED node=" + node.Id + " faction=" + node.OwnerFactionTag
                   + " player=" + buyerIdentityId + " credits=" + creditsPaid
                   + " blocks=" + blocks + " compkeys=" + comps.Keys
                   + " compunits=" + comps.Units + " cargounits=" + holds.Units
                   + " ledger=" + _world.GetTreasury(node.OwnerFactionTag));
            if (DebugEnabled) LogCensus("SALVAGE_CENSUS", node.Id, components, cargo);

            AttributeTrade(buyerIdentityId, node.OwnerFactionTag, -creditsPaid, false);
        }

        /// <summary>A repair completed: the player paid the yard, so the faction earns and the
        /// components the job consumed come out of the node.</summary>
        /// <param name="cost">The player's real wallet loss, always positive.</param>
        /// <param name="components">Missing-component census taken before the repair ran.</param>
        public void HandleRepairSettled(
            long stationId, long buyerIdentityId, long cost, IDictionary<string, long> components)
        {
            HandleRepairSettled(FindNodeByStation(stationId), buyerIdentityId, cost, components);
        }

        public void HandleRepairSettled(
            SpatialNode node, long buyerIdentityId, long cost, IDictionary<string, long> components)
        {
            if (!ServicesSettlementEnabled || cost <= 0) return;
            if (node == null || string.IsNullOrEmpty(node.OwnerFactionTag)) return;

            ServiceSettlement.RepairDebit debit = ServiceSettlement.DebitRepair(node.Inventory, components);

            _world.Earn(node.OwnerFactionTag, cost);
            if (debit.Debited > 0) _recordActivity?.Invoke(node.Id);

            Log.Info("REPAIR_SETTLED node=" + node.Id + " faction=" + node.OwnerFactionTag
                   + " player=" + buyerIdentityId + " credits=" + cost
                   + " compsdebited=" + debit.Debited + " shortfall=" + debit.Shortfall
                   + " ledger=" + _world.GetTreasury(node.OwnerFactionTag));
            if (DebugEnabled) LogCensus("REPAIR_CENSUS", node.Id, components, null);

            AttributeTrade(buyerIdentityId, node.OwnerFactionTag, cost, true);
        }

        /// <summary>A grid-storage retrieval was expedited: a pure service fee, no goods either way.</summary>
        /// <param name="fee">The player's real wallet loss, always positive.</param>
        public void HandleExpediteSettled(long stationId, long buyerIdentityId, long fee)
        {
            HandleExpediteSettled(FindNodeByStation(stationId), buyerIdentityId, fee);
        }

        public void HandleExpediteSettled(SpatialNode node, long buyerIdentityId, long fee)
        {
            if (!ServicesSettlementEnabled || fee <= 0) return;
            if (node == null || string.IsNullOrEmpty(node.OwnerFactionTag)) return;

            _world.Earn(node.OwnerFactionTag, fee);

            Log.Info("EXPEDITE_SETTLED node=" + node.Id + " faction=" + node.OwnerFactionTag
                   + " player=" + buyerIdentityId + " credits=" + fee
                   + " ledger=" + _world.GetTreasury(node.OwnerFactionTag));

            AttributeTrade(buyerIdentityId, node.OwnerFactionTag, fee, true);
        }

        // ---- Dock supply: refuel and rearm (game thread, uplink handler) -------------------------

        /// <summary>This station's offer rows by inventory key — the same rows, prices and floor its
        /// store block carries, so a refuel at the terminal can never sell what the store shelf
        /// withholds or charge a different price for it. Empty for a node with no player store.</summary>
        internal Dictionary<string, StationListing> OffersFor(SpatialNode node)
        {
            var offers = new Dictionary<string, StationListing>(StringComparer.Ordinal);
            if (node == null) return offers;
            var rows = new List<StationListing>();
            BuildListings(node, rows);
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Side == StationListingSide.Offer && !string.IsNullOrEmpty(rows[i].InvKey))
                    offers[rows[i].InvKey] = rows[i];
            return offers;
        }

        /// <summary>A refuel or rearm delivered goods out of this node's stock and the buyer paid for
        /// them: the node is debited what landed, the faction earns what was paid. Not gated on
        /// <see cref="ServicesSettlementEnabled"/> — that switch diverts vanilla's flows, and this one
        /// is ours end to end: the player has already been charged by the time it is called.</summary>
        /// <param name="service">"refuel" or "rearm", for the log line.</param>
        /// <param name="unitsByKey">Inventory units that actually landed, per key.</param>
        /// <param name="credits">The player's real wallet loss, always positive.</param>
        public void HandleDockSupplySettled(SpatialNode node, long buyerIdentityId, string service,
            IDictionary<string, long> unitsByKey, long credits)
        {
            if (node == null || string.IsNullOrEmpty(node.OwnerFactionTag) || unitsByKey == null) return;

            long units = 0;
            foreach (KeyValuePair<string, long> kv in unitsByKey)
            {
                if (kv.Value <= 0) continue;
                OnPlayerBought(node.Id, kv.Key, (int)Math.Min(kv.Value, int.MaxValue));
                units += kv.Value;
            }
            if (credits > 0) _world.Earn(node.OwnerFactionTag, credits);

            Log.Info("DOCK_SUPPLY_SETTLED service=" + service + " node=" + node.Id
                   + " faction=" + node.OwnerFactionTag + " player=" + buyerIdentityId
                   + " keys=" + unitsByKey.Count + " units=" + units + " credits=" + credits
                   + " ledger=" + _world.GetTreasury(node.OwnerFactionTag));

            if (credits > 0) AttributeTrade(buyerIdentityId, node.OwnerFactionTag, credits, true);
        }

        /// <summary>Reputation attribution, after the goods and the credits have landed: a model that
        /// throws must never cost the player the transaction they actually made.</summary>
        private void AttributeTrade(long buyerIdentityId, string factionTag, long creditsDelta, bool isBuy)
        {
            if (buyerIdentityId == 0 || OnPlayerTrade == null) return;
            try { OnPlayerTrade(buyerIdentityId, factionTag, creditsDelta, isBuy); }
            catch (Exception ex) { Log.Warn(ex, "Player trade attribution failed."); }
        }

        private static void LogCensus(string tag, string nodeId,
            IDictionary<string, long> first, IDictionary<string, long> second)
        {
            LogCensusPart(tag, nodeId, "blocks", first);
            LogCensusPart(tag, nodeId, "cargo", second);
        }

        private static void LogCensusPart(string tag, string nodeId, string part, IDictionary<string, long> census)
        {
            if (census == null || census.Count == 0) return;
            foreach (KeyValuePair<string, long> kv in census)
                Log.Info(tag + " node=" + nodeId + " part=" + part + " key=" + kv.Key + " units=" + kv.Value);
        }

        /// <summary>Adds one gas row, which SE models differently from every other listing.
        ///
        /// <para>A gas store item carries <b>no</b> <see cref="MyDefinitionId"/> -- the id-less
        /// <c>MyStoreItem</c> constructor -- and is identified purely by its <c>ItemTypes</c>.
        /// Handing SE a <c>GasProperties</c> id through the <c>PhysicalItem</c> constructor would
        /// give it a definition it cannot place in an inventory, which is why this runs before
        /// <c>TryGetDefinitionId</c> rather than after it.</para>
        ///
        /// <para>A gas <see cref="StationListing"/> already arrives in kilolitres, quantity and
        /// price alike, so nothing is scaled here: the row is denominated the way SE settles it.</para></summary>
        /// <returns>False when the row cannot be expressed: under a full kilolitre, or a gas SE has
        /// no station buy path for.</returns>
        private bool TryAddGasRow(MyFactionStation station, string nodeId, StationListing row, bool isOffer)
        {
            // SE settles no gas SELL: both SellToStation and SellToPlayer fall straight through
            // `case ItemTypes.Oxygen: case ItemTypes.Hydrogen: break;`. An order row would render and
            // do nothing when a player clicked it. Nothing should generate one -- gas is not in any
            // faction type's OrdersList -- so this is a guard against a future widening, not a filter
            // on something happening today.
            if (!isOffer) return false;

            ItemTypes gasType;
            if (!TryGetGasItemType(row.InvKey, out gasType)) return false;

            long units = row.Quantity;
            if (units <= 0) return false;

            long pricePerUnit = row.UnitPrice;
            if (pricePerUnit <= 0) return false;

            station.StoreItems.Add(new MyStoreItem(
                MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.STORE_ITEM),
                (int)Math.Min(units, int.MaxValue),
                (int)Math.Min(pricePerUnit, int.MaxValue),
                StoreItemTypes.Offer,
                gasType));

            // The only place the player-facing gas numbers exist: STORE_LIST carries counts, and a
            // row's own quantity and price are otherwise readable only off the store screen.
            if (DebugEnabled)
                Log.Info("STORE_GAS node=" + nodeId + " key=" + row.InvKey
                         + " kilolitres=" + units + " price=" + pricePerUnit);
            return true;
        }

        /// <summary>The <c>ItemTypes</c> SE dispatches a station gas purchase on.
        ///
        /// <para>Only Hydrogen and Oxygen have a station buy path: <c>MyStoreBlock.BuyFromStation</c>
        /// routes those two to <c>BuyGasStation</c>, while <c>ItemTypes.Gas</c> needs
        /// <c>SelectedGasTanks</c> on the store block's own grid -- a player-built store selling out
        /// of its tanks, which a Factions station has none of. A modded gas that passes
        /// <c>CanPlayerOffer</c> but is neither of these has nowhere to go, so it is not listed.</para></summary>
        private static bool TryGetGasItemType(string invKey, out ItemTypes gasType)
        {
            gasType = ItemTypes.PhysicalItem;
            if (string.Equals(invKey, "Gas:Hydrogen", StringComparison.OrdinalIgnoreCase))
            { gasType = ItemTypes.Hydrogen; return true; }
            if (string.Equals(invKey, "Gas:Oxygen", StringComparison.OrdinalIgnoreCase))
            { gasType = ItemTypes.Oxygen; return true; }
            return false;
        }

        /// <summary>Delegates to the Core mapper so a transaction and a recipe output cannot disagree
        /// about what an inventory key is -- this was a third copy of the prefix table, and the one
        /// that had no gas arm.</summary>
        private static string InvKeyFor(MyDefinitionId defId)
        {
            return InventoryKeyMapper.FromTypeIdAndSubtype(defId.TypeId.ToString(), defId.SubtypeName);
        }

        private void OnPlayerBought(string nodeId, string invKey, int amount)
        {
            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return;

            long cur;
            node.Inventory.TryGetValue(invKey, out cur);
            node.Inventory[invKey] = Math.Max(0, cur - amount);
            // The player buying FROM the station is the station selling: the sale term in
            // PriceOracle's target-stock ranking, bounded by the player's own wallet.
            _world.RecordUnitsSold(nodeId, invKey, amount);
            Log.Info("Player bought " + amount + " " + invKey + " at " + nodeId + " (" + cur + " -> " + node.Inventory[invKey] + ")");
            _recordActivity?.Invoke(nodeId);
        }

        private void OnPlayerSold(string nodeId, string invKey, int amount)
        {
            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return;

            long cur;
            node.Inventory.TryGetValue(invKey, out cur);
            node.Inventory[invKey] = cur + amount;
            Log.Info("Player sold " + amount + " " + invKey + " at " + nodeId + " (" + cur + " -> " + node.Inventory[invKey] + ")");
            _recordActivity?.Invoke(nodeId);
        }

        // factionTag replaces nodeId: the ship may be stored at any faction node (typically the
        // Shipyard that built it), so we search the network rather than looking at the store node.
        // Decrements ShipStock and ListedForSale together — a sale removes the ship from the
        // faction's pool entirely, not just its store reservation.
        private sealed class ShipListing
        {
            public string FactionTag;
        }

        /// <summary>Every shelf hull this manager has put on a store, by the item object. Weak, so an
        /// item a refresh cleared goes with it.</summary>
        private static readonly ConditionalWeakTable<MyStoreItem, ShipListing> ShipListings =
            new ConditionalWeakTable<MyStoreItem, ShipListing>();

        /// <summary>The selling faction's tag when <paramref name="item"/> is a Factions shelf hull.</summary>
        internal static bool TryGetShipListing(MyStoreItem item, out string factionTag)
        {
            factionTag = null;
            ShipListing listing;
            if (item == null || !ShipListings.TryGetValue(item, out listing) || listing == null) return false;
            factionTag = listing.FactionTag;
            return true;
        }

        /// <summary>One shelf hull of <paramref name="prefabId"/> sold by <paramref name="factionTag"/>:
        /// off <see cref="SpatialNode.ListedForSale"/> and <see cref="SpatialNode.ShipStock"/> at the
        /// first owned node listing it. Called by <see cref="StoreShipSaleMarks"/> once per purchase
        /// SE's spawn confirmed.</summary>
        internal ShipSaleDecrement OnPlayerBoughtShip(string factionTag, string prefabId)
        {
            if (_world?.Graph == null || string.IsNullOrEmpty(factionTag)) return new ShipSaleDecrement();
            ShipSaleDecrement d = StoreShipSale.SellListed(_world.Graph.OwnedBy(factionTag), prefabId);
            if (d.Sold) _recordActivity?.Invoke(d.NodeId);
            return d;
        }

        // ---- helpers ----------------------------------------------------------------------------

        private MyFactionStation FindStation(SpatialNode node)
        {
            EnsureStationIndex();
            MyFactionStation station;
            if (_stationIndex.TryGetValue(node.StableStationId, out station))
                return station;
            // Rebuild once on a miss (a station founded since the index was last built) before
            // giving up, same self-healing idiom as FactionContractStrategy's s_stationToNode.
            RebuildStationIndex();
            return _stationIndex.TryGetValue(node.StableStationId, out station) ? station : null;
        }

        private void EnsureStationIndex()
        {
            if (_stationIndex != null && DateTime.UtcNow - _stationIndexBuiltUtc < StationIndexTtl) return;
            RebuildStationIndex();
        }

        private void RebuildStationIndex()
        {
            var index = new Dictionary<long, MyFactionStation>();
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                foreach (IMyFactionStation sta in kv.Value.Stations)
                {
                    var concrete = sta as MyFactionStation;
                    if (concrete != null) index[sta.Id] = concrete;
                }
            }
            _stationIndex = index;
            _stationIndexBuiltUtc = DateTime.UtcNow;
        }

        private long ListingCap(string invKey)
        {
            EnsureDefIdCache();
            string typeId;
            if (!_defIdTypeCache.TryGetValue(invKey, out typeId))
                return _tuning.StoreListingCapConsumable;

            if (typeId == "MyObjectBuilder_Ore"
             || typeId == "MyObjectBuilder_Ingot"
             || typeId == "MyObjectBuilder_Component")
                return _tuning.StoreListingCapBulk;

            if (typeId == "MyObjectBuilder_AmmoMagazine")
                return _tuning.StoreListingCapAmmunition;

            if (typeId == "MyObjectBuilder_PhysicalGunObject")
                return _tuning.StoreListingCapWeapon;

            // Uncapped: a single large hydrogen tank holds more than any fixed row size worth
            // picking, and a refuel is already bounded by the buyer's tank space and wallet.
            if (typeId == "MyObjectBuilder_GasProperties")
                return StationListings.Uncapped;

            // ConsumableItem, SeedItem, GasContainerObject, OxygenContainerObject, Datapad, unknown
            return _tuning.StoreListingCapConsumable;
        }

        private bool TryGetItemKind(string invKey, out ItemKind kind)
        {
            string bare = invKey;
            if (invKey.StartsWith("Ore:"))   bare = invKey.Substring(4);
            else if (invKey.StartsWith("Comp:")) bare = invKey.Substring(5);
            return _registry.TryGetItemKind(bare, out kind);
        }

        /// <summary>Builds the cache on first use: a station window or market read can arrive before
        /// any store refresh has, and a refresh pass only runs while some station grid is live.</summary>
        private bool TryGetDefinitionId(string invKey, out SerializableDefinitionId defId)
        {
            EnsureDefIdCache();
            return _defIdCache.TryGetValue(invKey, out defId);
        }

        /// <summary>The same invKey -> SE definition map this manager's listings run on, for a
        /// consumer outside the store. Exposed rather than rebuilt so the two can never disagree
        /// about which key names which subtype, and it builds the cache on first use exactly as a
        /// store refresh does — a caller may reach this before any refresh has run.
        ///
        /// <para>A true answer does <b>not</b> mean the key can be placed in an inventory: gas is in
        /// the map, mapped to its <c>MyGasProperties</c> definition, and SE has no item for it.
        /// Callers that materialise an item test the key for gas themselves.</para></summary>
        internal bool TryResolveDefinitionId(string invKey, out MyDefinitionId defId)
        {
            defId = default(MyDefinitionId);
            if (string.IsNullOrEmpty(invKey)) return false;
            EnsureDefIdCache();
            SerializableDefinitionId serializable;
            if (!TryGetDefinitionId(invKey, out serializable)) return false;
            defId = (MyDefinitionId)serializable;
            return true;
        }

        /// <summary>The inventory key <paramref name="input"/> names, rebuilt from the definition it
        /// resolves to so its case is the one node stock is kept under. Accepts the same key forms
        /// <see cref="TryResolveDefinitionId"/> does, gas included.</summary>
        internal bool TryCanonicaliseItemKey(string input, out string invKey)
        {
            invKey = null;
            MyDefinitionId defId;
            if (!TryResolveDefinitionId(input, out defId)) return false;
            invKey = InventoryKeyMapper.FromTypeIdAndSubtype(defId.TypeId.ToString(), defId.SubtypeName);
            return invKey != null;
        }

        /// <summary>Every item a Stock standing order may name: the same definition map the store and
        /// <see cref="TryCanonicaliseItemKey"/> run on, physical items and offerable gas, each with its
        /// display name and a coarse category for the client's list.</summary>
        internal List<FactionRuntime.OrderableItem> OrderableItems()
        {
            EnsureDefIdCache();
            var items = new List<FactionRuntime.OrderableItem>(_defIdCache.Count);
            foreach (KeyValuePair<string, SerializableDefinitionId> kv in _defIdCache)
            {
                string typeStr;
                _defIdTypeCache.TryGetValue(kv.Key, out typeStr);
                MyDefinitionId defId = (MyDefinitionId)kv.Value;
                string invKey = InventoryKeyMapper.FromTypeIdAndSubtype(defId.TypeId.ToString(), defId.SubtypeName);
                if (invKey == null) continue;

                MyDefinitionBase def = MyDefinitionManager.Static != null
                    ? MyDefinitionManager.Static.GetDefinition(defId) : null;
                string name = def != null && !string.IsNullOrEmpty(def.DisplayNameText) ? def.DisplayNameText : defId.SubtypeName;
                items.Add(new FactionRuntime.OrderableItem { Key = invKey, Name = name, Category = CategoryOf(typeStr) });
            }
            return items;
        }

        private static string CategoryOf(string typeStr)
        {
            if (string.IsNullOrEmpty(typeStr)) return "Other";
            if (typeStr.EndsWith("_Ore", StringComparison.OrdinalIgnoreCase)) return "Ore";
            if (typeStr.EndsWith("_Ingot", StringComparison.OrdinalIgnoreCase)) return "Ingot";
            if (typeStr.EndsWith("_Component", StringComparison.OrdinalIgnoreCase)) return "Component";
            if (typeStr.EndsWith("_AmmoMagazine", StringComparison.OrdinalIgnoreCase)) return "Ammo";
            if (typeStr.EndsWith("_GasProperties", StringComparison.OrdinalIgnoreCase)) return "Gas";
            return "Other";
        }

        private void EnsureDefIdCache()
        {
            if (_defIdCache != null) return;
            _defIdCache     = new Dictionary<string, SerializableDefinitionId>(StringComparer.OrdinalIgnoreCase);
            _defIdTypeCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _prefabPcuCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (MyDefinitionManager.Static == null) return;

            foreach (MyPhysicalItemDefinition def in MyDefinitionManager.Static.GetPhysicalItemDefinitions())
            {
                if (def == null || string.IsNullOrEmpty(def.Id.SubtypeName)) continue;
                string typeStr = def.Id.TypeId.ToString();
                string subType = def.Id.SubtypeName;

                // Inventory-key-format keys prevent Ore:Iron / Ingot:Iron collision.
                string invKey;
                if (typeStr.EndsWith("_Ore", StringComparison.OrdinalIgnoreCase))
                    invKey = "Ore:" + subType;
                else if (typeStr.EndsWith("_Ingot", StringComparison.OrdinalIgnoreCase))
                    invKey = "Ingot:" + subType;
                else if (typeStr.EndsWith("_Component", StringComparison.OrdinalIgnoreCase))
                    invKey = "Comp:" + subType;
                // A seed shares its crop's subtype; without its own prefix the first of the two
                // scanned would take the bare key and the other would have no listing at all.
                else if (typeStr.EndsWith("_SeedItem", StringComparison.OrdinalIgnoreCase))
                    invKey = InventoryKeyMapper.SeedPrefix + subType;
                else
                    invKey = subType;

                if (!_defIdCache.ContainsKey(invKey))
                {
                    _defIdCache[invKey]     = new SerializableDefinitionId(def.Id.TypeId, subType);
                    _defIdTypeCache[invKey] = typeStr;
                }
            }

            // Gas is not a physical item, so the scan above cannot see it and every gas key failed
            // CanList before any price or demand test ran. SE states which gases may be offered to a
            // player in the definition itself -- CanPlayerOffer is true for Hydrogen and Oxygen and
            // absent for Water, which is the one gas nothing in this economy consumes. Reading the
            // flag rather than listing the two subtypes keeps a modded gas working and keeps Water
            // out without naming it.
            foreach (MyDefinitionBase defBase in MyDefinitionManager.Static.GetAllDefinitions())
            {
                var gas = defBase as MyGasProperties;
                if (gas == null || !gas.CanPlayerOffer) continue;
                if (string.IsNullOrEmpty(gas.Id.SubtypeName)) continue;

                string gasKey = InventoryKeyMapper.GasPrefix + gas.Id.SubtypeName;
                if (_defIdCache.ContainsKey(gasKey)) continue;
                _defIdCache[gasKey]     = new SerializableDefinitionId(gas.Id.TypeId, gas.Id.SubtypeName);
                _defIdTypeCache[gasKey] = gas.Id.TypeId.ToString();
            }

            // Prefab PCU — sum of all block PCU values in each prefab. Used for ship store listings.
            foreach (KeyValuePair<string, MyPrefabDefinition> kv in MyDefinitionManager.Static.GetPrefabDefinitions())
            {
                if (kv.Value == null || kv.Value.CubeGrids == null) continue;
                int totalPcu = 0;
                foreach (MyObjectBuilder_CubeGrid grid in kv.Value.CubeGrids)
                {
                    if (grid?.CubeBlocks == null) continue;
                    foreach (MyObjectBuilder_CubeBlock block in grid.CubeBlocks)
                    {
                        if (block == null) continue;
                        MyCubeBlockDefinition blockDef;
                        if (MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out blockDef) && blockDef != null)
                            totalPcu += blockDef.PCU;
                    }
                }
                if (totalPcu > 0)
                    _prefabPcuCache[kv.Key] = totalPcu;
            }
        }
    }
}
