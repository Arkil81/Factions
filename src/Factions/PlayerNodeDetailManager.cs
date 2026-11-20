using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the per-node window. A client watches exactly one node at a time — the window
    /// is the only thing that asks, and only one is ever open — so this is a map from SteamID to
    /// node id rather than a subscriber set.
    ///
    /// It exists as its own manager, and its own wire kinds, because neither list that opens the
    /// window can supply what it shows. <see cref="PlayerMapManager"/> sends every node at low
    /// detail; <see cref="PlayerAssetsManager"/> sends the handful the faction owns at register
    /// detail. Stock, reserved stock, the build queue as rows and the ships standing at a node are
    /// per-node and heavy, and would be paid for on every node of every pass if either list carried
    /// them.
    ///
    /// It is also the one place a client message changes server state. Every command is re-resolved
    /// from the sender's SteamID to their SE faction and run through the same
    /// <see cref="FactionRuntime"/> entry point the chat verb uses, which re-checks node ownership
    /// itself — the client's claim about which node it is watching is never trusted.
    /// </summary>
    internal sealed class PlayerNodeDetailManager
    {
        /// <summary>Longest a stock listing gets before it is trimmed to the largest holdings. A
        /// node can carry a long tail of single units; the window is not a manifest.</summary>
        private const int MaxStockRows = 40;

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamID -> the node id that client's window is showing.</summary>
        private readonly Dictionary<ulong, string> _watching = new Dictionary<ulong, string>();

        /// <summary>Watchers owed a snapshot on the next tick — they just opened the window, changed
        /// node, or ran a command whose effect they should see without waiting out the cadence.</summary>
        private readonly HashSet<ulong> _owesSnapshot = new HashSet<ulong>();

        private readonly ViewerRelations _relations = new ViewerRelations();
        private readonly PlayerScannerManager _scanner;
        /// <summary>Read per snapshot rather than held: the store manager is built after this one.</summary>
        private readonly Func<StationStoreManager> _store;
        /// <summary>Reused across sends; one watcher's stores, rebuilt per snapshot.</summary>
        private readonly List<FactionKnowledge> _viewerSources = new List<FactionKnowledge>(2);

        public PlayerNodeDetailManager(FactionRuntime runtime, UplinkManager uplink,
                                       PlayerScannerManager scanner, Func<StationStoreManager> store)
        {
            _runtime = runtime;
            _uplink = uplink;
            _scanner = scanner;
            _store = store;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.NodeDetailRequest, OnDetailRequest);
            _uplink.RegisterHandler(WireMessageKind.NodeCommandRequest, OnCommandRequest);
            _uplink.RegisterHandler(WireMessageKind.BuildOptionsRequest, OnBuildOptionsRequest);
            _uplink.RegisterHandler(WireMessageKind.BuildRequest, OnBuildRequest);
        }

        public int SubscriberCount { get { return _watching.Count; } }

        private void OnDetailRequest(ulong steamId, byte[] data)
        {
            string nodeId;
            if (!FactionsWire.TryReadNodeDetailRequest(data, out nodeId))
            {
                FactionsPlugin.Log.Warn("Node detail request from " + steamId + " was unreadable.");
                return;
            }

            if (string.IsNullOrEmpty(nodeId))
            {
                _watching.Remove(steamId);
                _owesSnapshot.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("NODE_UNWATCH steam=" + steamId);
                return;
            }

            _watching[steamId] = nodeId;
            _owesSnapshot.Add(steamId);
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("NODE_WATCH steam=" + steamId + " node=" + nodeId);
        }

        /// <summary>
        /// Runs on the scanner cadence, and does nothing while no window is open. One node per
        /// watcher is cheap enough that there is no slower sub-cadence here the way the map has one
        /// for its world payload — a window the player is reading should keep up with the numbers
        /// changing in it.
        /// </summary>
        public void Tick()
        {
            if (_watching.Count == 0) return;
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;

            foreach (LinkedViewer player in LinkedViewers.Collect())
            {
                ulong steamId = player.SteamId;
                string nodeId;
                if (!_watching.TryGetValue(steamId, out nodeId)) continue;

                _owesSnapshot.Remove(steamId);
                SendSnapshot(steamId, player.IdentityId, nodeId);
            }
        }

        private void SendSnapshot(ulong steamId, long viewerIdentityId, string nodeId)
        {
            // Standings are viewer-relative, so the cache is only valid within one player.
            _relations.Clear();
            _uplink.Send(steamId, FactionsWire.WriteNodeDetailSnapshot(
                Describe(viewerIdentityId, nodeId)));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("NODE_DETAIL steam=" + steamId + " node=" + nodeId);
        }

        /// <summary>The survey behind the two readings, and the deposit ore by ore. Derived here from
        /// the same Core functions that set <c>ResourceValue</c> and <c>Richness</c>, so the targets
        /// and the ore mix the window shows read back to the numbers beside them.</summary>
        private void DescribeGround(SpatialNode node, ref WireNodeDetail detail)
        {
            WorldModel world = _runtime.World;
            FactionTuning tuning = world.Tuning;
            detail.SurveyConfidence = NodeContent.SurveyConfidence(node, tuning);
            detail.ResourceValueTarget = NodeContent.ResourceValueTarget(
                node, world.Planets, tuning, world.PriceCatalogue, world.Registry);
            detail.RichnessTarget = NodeContent.RichnessTarget(node, tuning);
            detail.ResolvedShareFloor = node.Deposit != null ? node.Deposit.ResolvedShareFloor : 1.0;

            List<OreBreakdown> mix = DepositModel.Breakdown(node.Deposit, world.PriceCatalogue, world.Registry);
            var ores = new WireNodeOre[mix.Count];
            for (int i = 0; i < mix.Count; i++)
            {
                ores[i] = new WireNodeOre
                {
                    SubTypeId = mix[i].SubTypeId,
                    Share = mix[i].Share,
                    PriceRatio = mix[i].PriceRatio,
                    WorthShare = mix[i].WorthShare,
                    Resolved = mix[i].Resolved,
                };
            }
            detail.Ores = ores;
        }

        /// <summary>
        /// One node as this viewer may see it. Everything past <c>Owned</c> is only ever filled in
        /// for a node the viewer's own faction holds — the filtering is here rather than on the
        /// client, so unowned stock and queues never reach the wire at all.
        /// </summary>
        private WireNodeDetail Describe(long viewerIdentityId, string nodeId)
        {
            SpatialNode node;
            if (!_runtime.World.Graph.Nodes.TryGetValue(nodeId, out node) || node == null)
                return new WireNodeDetail { Exists = false, Id = nodeId };

            NodeBelief belief = Believe(viewerIdentityId, node);
            string ownerTag = belief.OwnerTag;
            bool claimable = NeutralFaction.IsClaimable(ownerTag);

            var detail = new WireNodeDetail
            {
                Exists = true,
                Id = node.Id,
                DisplayName = node.DisplayName,
                DominantOre = NodeDisplay.DominantOre(node),
                Surveyed = NodeDisplay.Surveyed(node),
                Zone = (byte)node.Zone,
                OwnerTag = claimable ? "" : ownerTag,
                Relation = (byte)_relations.For(ownerTag, viewerIdentityId, claimable),
                HasStation = node.StableStationId != 0,
                XKm = node.Position.X,
                YKm = node.Position.Y,
                ZKm = node.Position.Z,
                HoldStrength = belief.HoldStrength,
            };
            // Above the ownership seam: a safe zone is a bubble anyone standing near it can see,
            // and a defender reading this window during a raid is who needs it most.
            DescribeZone(node, ref detail);

            // Everything past here is the node's own register, so it is gated on who holds it
            // *now* -- never on the belief above. A faction that has lost a node still remembers it
            // as theirs, and answering that memory with the new owner's stock and queues would hand
            // over someone else's books.
            FactionState owner = ViewerFaction(viewerIdentityId);
            if (owner == null || NeutralFaction.IsClaimable(node.OwnerFactionTag)
                || !string.Equals(node.OwnerFactionTag, owner.Tag, StringComparison.OrdinalIgnoreCase))
            {
                return detail;   // identity only: not this player's territory
            }

            bool founded = node.StableStationId != 0;
            detail.Owned = true;
            detail.ResourceValue = node.ResourceValue;
            detail.Richness = node.Richness;
            DescribeGround(node, ref detail);
            // Priced through the same validator the command itself runs, so the button the window
            // offers and the refusal it would get back can never disagree. Zero once founded,
            // matching the register: a price beside a built station reads as an outstanding bill.
            DescribeFound(owner, node, ref detail);
            detail.ShipyardPaused = node.ShipyardPaused;
            detail.Capabilities = DescribeCapabilities(node);
            StationStoreManager store = _store != null ? _store() : null;
            detail.HasStore = store != null && store.HasPlayerStore(node);
            detail.Stock = DescribeStock(node, detail.HasStore ? store.OffersFor(node) : null);
            DescribeWorkers(node, ref detail);
            BuildSupply.EstateView estate = BuildSupply.EstateView.For(_runtime.World, owner, node);
            detail.Builds = DescribeBuilds(node, estate);
            detail.Repairs = DescribeRepairs(node, estate);
            detail.RepairHulls = node.DamagedHulls.Count;
            detail.Ships = DescribeShips(node);
            detail.Production = DescribeProduction(owner, node);
            detail.ProductionWindowSeconds = (int)ProductionManager.HistoryRetentionSeconds;
            detail.Demand = DescribeDemand(owner, node);
            detail.EndedDemand = DescribeEndedDemand(owner, node);
            detail.DemandWindowSeconds = (int)DemandRegister.EndedRetentionSeconds;
            // Only for a founded station: nothing can be installed into a bare claim, so the whole
            // menu would be nine rows of the same refusal the window already says in its heading.
            if (founded) detail.Develop = DescribeDevelopOptions(owner, node);

            // Hold upkeep. Offered on any owned node, founded or not: hold decays on a bare claim
            // too, and losing one costs the same territory.
            int rank;
            detail.HoldOptIn = owner.ReinforceOptIn.TryGetValue(node.Id, out rank);
            detail.HoldRank = detail.HoldOptIn ? rank : 0;
            FactionTuning t = _runtime.World.Tuning;
            detail.HoldCost = t.ReinforceCost;
            detail.HoldBudgetIncomeFraction = owner.ReinforceIncomeFraction(t);
            detail.HoldBudgetTreasuryFraction = owner.ReinforceWealthFraction(t);
            detail.HoldBudgetFloor = owner.ReinforceBudgetFloorFor(t);
            detail.HoldBudgetOverrideMask =
                (byte)((owner.ReinforceIncomeFractionOverride.HasValue ? 1 : 0)
                     | (owner.ReinforceWealthFractionOverride.HasValue ? 2 : 0)
                     | (owner.ReinforceBudgetFloorOverride.HasValue ? 4 : 0));

            // Trade. Faction-wide, like the develop reservation and the budget knobs above, and
            // carried here because the station window is where the player is standing.
            detail.TradeBuyOptIn = owner.TradeBuyOptIn;
            detail.TradeSellOptIn = owner.TradeSellOptIn;
            detail.TradeUnserved = owner.UnservedDemands.Count;
            detail.TradeInFlight = TradeManager.InFlightTradeCount(owner);
            detail.TradeMaxFleets = t.MaxTradeFleets;
            detail.TradeBudgetIncomeFraction = owner.TradeIncomeFraction(t);
            detail.TradeBudgetTreasuryFraction = owner.TradeWealthFraction(t);
            detail.TradeBudgetFloor = owner.TradeBudgetFloorFor(t);
            detail.TradeBudgetOverrideMask =
                (byte)((owner.TradeIncomeFractionOverride.HasValue ? 1 : 0)
                     | (owner.TradeWealthFractionOverride.HasValue ? 2 : 0)
                     | (owner.TradeBudgetFloorOverride.HasValue ? 4 : 0));
            // Only worth computing when something would act on it, and the projection is a graph
            // walk per call. An opted-out faction reports zero partners, which is the truth.
            if (owner.TradeBuyOptIn || owner.TradeSellOptIn)
            {
                FactionLifecycle.ProjectPlayerTradePriority(owner, _runtime.World, t);
                detail.TradePartners = owner.TradePriority.Count;
                detail.TradeHasHull = OperationsManager.OwnsSuitableHull(_runtime.World, owner, ActionKind.Trade);
            }
            return detail;
        }

        /// <summary>What founding a station on this claim would cost and what is stopping it, run
        /// through the same <see cref="FoundStationValidator"/> gate the command itself runs — the
        /// window must not be able to offer a purchase the server would then refuse.
        ///
        /// <para>The two structural refusals never reach here: this is called past the ownership
        /// seam, on a node the server just resolved.</para></summary>
        private void DescribeFound(FactionState owner, SpatialNode node, ref WireNodeDetail detail)
        {
            long cost, have;
            FoundStationResult result = FoundStationValidator.Validate(
                _runtime.World, _runtime.World.Tuning, owner.Tag, node.Id, out cost, out have);
            if (result == FoundStationResult.AlreadyFounded)
            {
                detail.FoundBlock = (byte)WireFoundBlock.AlreadyFounded;
                return;
            }
            detail.FoundCost = cost;
            detail.FoundHave = have;
            detail.FoundBlock = (byte)(result == FoundStationResult.InsufficientTreasury
                ? WireFoundBlock.InsufficientTreasury
                : WireFoundBlock.None);
        }

        /// <summary>
        /// The develop menu for one node: every capability, where it stands here, what the next
        /// install costs, and what is stopping it.
        ///
        /// <para>The readiness itself comes from <see cref="DevelopStationPreview"/>, which runs the
        /// same <see cref="DevelopStationValidator"/> gate the install does — the menu must not be
        /// able to say a capability is ready and then have the install refuse it. Only the two
        /// faction-wide fields are read here, because they are not part of that gate.</para>
        /// </summary>
        private WireNodeCapabilityOption[] DescribeDevelopOptions(FactionState owner, SpatialNode node)
        {
            List<DevelopOption> options = DevelopStationPreview.For(
                _runtime.World, _runtime.World.Tuning, owner.Tag, node.Id);
            if (options.Count == 0) return null;

            var rows = new List<WireNodeCapabilityOption>(options.Count);
            foreach (DevelopOption option in options)
            {
                WireDevelopBlock block;
                if (!TryMapBlock(option.Result, out block)) continue;
                rows.Add(new WireNodeCapabilityOption
                {
                    Cap = option.Capability.ToString(),
                    Level = option.Level,
                    MaxLevel = option.MaxLevel,
                    Cost = option.TreasuryCost,
                    Block = (byte)block,
                    BlockKey = option.BlockKey,
                    BlockHave = option.BlockHave,
                    BlockNeed = option.BlockNeed,
                    Reserved = owner.ReservationOptIn.Contains(option.Capability),
                    // The raw rank the player set, not InstallRankFor's tuning-adjusted effective
                    // one: the window shows back what SetInstallPriorityForPlayer wrote.
                    Priority = owner.InstallRankFor(option.Capability),
                });
            }
            return rows.Count == 0 ? null : rows.ToArray();
        }

        /// <summary>Maps a readiness verdict onto the wire's block reason. The two structural
        /// refusals — no such node, not this faction's node — have no wire value on purpose: the
        /// whole menu sits past the ownership seam, so reaching either would mean the seam itself
        /// failed, and dropping the row is better than inventing a reason for it.</summary>
        private static bool TryMapBlock(DevelopStationResult result, out WireDevelopBlock block)
        {
            switch (result)
            {
                case DevelopStationResult.Ok:                    block = WireDevelopBlock.None; return true;
                case DevelopStationResult.AlreadyInstalled:      block = WireDevelopBlock.AtMaxLevel; return true;
                case DevelopStationResult.PrerequisiteMissing:   block = WireDevelopBlock.PrerequisiteMissing; return true;
                case DevelopStationResult.InsufficientTreasury:  block = WireDevelopBlock.InsufficientTreasury; return true;
                case DevelopStationResult.InsufficientItemStock: block = WireDevelopBlock.InsufficientStock; return true;
                case DevelopStationResult.NotFounded:            block = WireDevelopBlock.NotFounded; return true;
                default:                                        block = WireDevelopBlock.None; return false;
            }
        }

        /// <summary>What this station's protection is doing, for any viewer. The state, the shield
        /// and the raid bar come from <see cref="PlayerSiege"/> rather than from field tests here,
        /// so the window, the map's alarm ring and the zone the player is shooting cannot disagree
        /// about one station.</summary>
        private void DescribeZone(SpatialNode node, ref WireNodeDetail detail)
        {
            DateTime nowUtc = _runtime.World.UtcNow;
            FactionTuning tuning = _runtime.World.Tuning;
            PlayerSiege.ZoneState state = PlayerSiege.StateOf(node, nowUtc);
            detail.ZoneState = (byte)state;
            detail.ZoneIntegrity = PlayerSiege.IntegrityFraction(node, tuning, nowUtc);
            detail.ZoneRaidProgress = PlayerSiege.RaidProgressFraction(node, tuning);

            DateTime until;
            if (state == PlayerSiege.ZoneState.Besieged) until = PlayerSiege.BreachDueUtc(node, tuning);
            else if (state == PlayerSiege.ZoneState.Down) until = node.SafeZoneDownUntilUtc;
            else until = DateTime.MinValue;
            detail.ZoneUntilUtcTicks = until == DateTime.MinValue ? 0L : until.Ticks;
        }

        private static WireAssetCapability[] DescribeCapabilities(SpatialNode node)
        {
            if (node.Capabilities.Count == 0) return null;
            var caps = new WireAssetCapability[node.Capabilities.Count];
            int i = 0;
            foreach (KeyValuePair<NodeCapabilityKind, int> pair in node.Capabilities)
            {
                caps[i].Cap = pair.Key.ToString();
                caps[i].Level = pair.Value;
                i++;
            }
            return caps;
        }

        /// <summary>
        /// Routable and reserved stock in one list, largest first, trimmed to
        /// <see cref="MaxStockRows"/>. Reserved-only entries are included: stock a build cannot
        /// spend is exactly what a player looking at a stalled queue needs to see.
        ///
        /// <para>Each row also says what the store sells of it — the offer rows a refuel or rearm at
        /// this station's terminal draws on, so the window and the terminal cannot disagree — and
        /// why the rest is kept (<see cref="StockAvailability"/>).</para>
        /// </summary>
        /// <param name="offers">The store's offer rows by key; null when the station has no store.</param>
        private WireNodeStockItem[] DescribeStock(SpatialNode node, Dictionary<string, StationListing> offers)
        {
            if (node.Inventory.Count == 0 && node.ReservedInventory.Count == 0) return null;

            var merged = new Dictionary<string, WireNodeStockItem>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> pair in node.Inventory)
            {
                if (pair.Value <= 0) continue;
                merged[pair.Key] = new WireNodeStockItem { Key = pair.Key, Quantity = pair.Value };
            }
            foreach (KeyValuePair<string, long> pair in node.ReservedInventory)
            {
                if (pair.Value <= 0) continue;
                WireNodeStockItem existing;
                merged.TryGetValue(pair.Key, out existing);
                existing.Key = pair.Key;
                existing.Reserved = pair.Value;
                merged[pair.Key] = existing;
            }
            if (merged.Count == 0) return null;

            var rows = new List<WireNodeStockItem>(merged.Values);
            rows.Sort((a, b) => (b.Quantity + b.Reserved).CompareTo(a.Quantity + a.Reserved));
            if (rows.Count > MaxStockRows) rows.RemoveRange(MaxStockRows, rows.Count - MaxStockRows);

            PriceOracle oracle = _runtime.TargetOracle;
            for (int i = 0; i < rows.Count; i++)
            {
                WireNodeStockItem row = rows[i];
                if (row.Quantity <= 0) continue;
                StationListing offer = default(StationListing);
                bool listed = offers != null && offers.TryGetValue(row.Key, out offer);
                if (listed) row.ForSale = Math.Min(row.Quantity, DockSupply.OfferUnits(row.Key, offer.Quantity));
                PriceOracle.TargetBreakdown target = oracle != null
                    ? oracle.ExplainTarget(node.Id, row.Key)
                    : new PriceOracle.TargetBreakdown { Winner = "none" };
                row.Target = target.KeepBack;
                row.KeptFor = (byte)StockAvailability.Classify(row.Quantity, row.ForSale, offers != null, listed,
                    target, ItemCredits.UnitsPerPricedUnit(row.Key));
                rows[i] = row;
            }
            return rows.ToArray();
        }

        /// <summary>Every queue entry as one row, in the yard's own claim order
        /// (<see cref="SpatialNode.OrderedBuildQueue"/>) rather than raw list order — the first
        /// <see cref="SpatialNode.BuildSlotCount"/> rows are the builds actually being worked, the rest
        /// are waiting their turn, which is what <see cref="WireNodeBuild.Working"/> carries.
        ///
        /// <para>The per-component BOM rides along, because a summed total says how far a build has
        /// got and never why it stopped. <see cref="BuildSupply"/> answers that in Core, off one
        /// pass over the estate for the whole queue rather than one per entry.</para></summary>
        private static WireNodeBuild[] DescribeBuilds(SpatialNode node, BuildSupply.EstateView estate)
        {
            if (node.BuildQueue.Count == 0) return null;
            List<BuildQueueEntry> ordered = node.OrderedBuildQueue();
            var rows = new WireNodeBuild[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                BuildQueueEntry e = ordered[i];
                long bomTotal = 0, collected = 0;
                foreach (KeyValuePair<string, long> pair in e.Bom) bomTotal += pair.Value;
                foreach (KeyValuePair<string, long> pair in e.Collected) collected += pair.Value;
                rows[i] = new WireNodeBuild
                {
                    Id = e.Id,
                    Role = e.Role,
                    PrefabId = e.PrefabId,
                    BomTotal = bomTotal,
                    Collected = collected,
                    Priority = e.Priority,
                    ExpiresUtcTicks = e.ExpiresUtc.Ticks,
                    StalledSeconds = e.StalledSeconds,
                    CreatedUtcTicks = e.CreatedUtc.Ticks,
                    // The node's own rule, not this loop's index: a free build is skipped when
                    // counting who is ahead, so position in the ordered queue and position in the
                    // contest for the working slot are different numbers whenever one is queued.
                    Working = node.IsActiveBuild(e),
                    WaitingBehind = WaitingBehindOf(node, e),
                    Bom = DescribeBom(node, e, estate),
                    ReservedInputs = DescribeReservedInputs(e),
                };
            }
            return rows;
        }

        /// <summary>How many builds this one is waiting behind — 0 for one that holds a slot, and
        /// for a free build, which never queues against anything.</summary>
        private static int WaitingBehindOf(SpatialNode node, BuildQueueEntry entry)
        {
            if (node.IsActiveBuild(entry)) return 0;
            int ahead = node.BuildsAheadOf(entry);
            int slots = node.BuildSlotCount();
            return ahead < slots ? 0 : ahead - slots + 1;
        }

        /// <summary>The node's repair queue in the yard's served order, identical hulls folded
        /// into one row (<see cref="HullDamage.QueueView"/>), each with its bill in the build
        /// rows' component shape.</summary>
        private static WireNodeRepair[] DescribeRepairs(SpatialNode node, BuildSupply.EstateView estate)
        {
            if (node.DamagedHulls.Count == 0) return null;
            List<RepairQueueRow> view = HullDamage.QueueView(node);
            int count = Math.Min(view.Count, FactionsWire.MaxRepairRowsPerNode);
            var rows = new WireNodeRepair[count];
            for (int i = 0; i < count; i++)
            {
                RepairQueueRow r = view[i];
                rows[i] = new WireNodeRepair
                {
                    PrefabId = r.PrefabId,
                    Count = r.Count,
                    Position = r.Position,
                    Integrity = r.Integrity,
                    State = (byte)r.State,
                    BillTotal = r.BillUnits,
                    Collected = r.CollectedUnits,
                    QueuedUtcTicks = r.QueuedUtc.Ticks,
                    PartsCompleteUtcTicks = r.PartsCompleteUtc == default(DateTime) ? 0 : r.PartsCompleteUtc.Ticks,
                    DoneUtcTicks = r.DoneUtc == default(DateTime) ? 0 : r.DoneUtc.Ticks,
                    Bill = ToWire(BuildSupply.Rows(node, r.Hull, estate)),
                };
            }
            return rows;
        }

        private static WireBuildComponent[] DescribeBom(
            SpatialNode node, BuildQueueEntry entry, BuildSupply.EstateView estate)
        {
            return ToWire(BuildSupply.Rows(node, entry, estate));
        }

        private static WireBuildComponent[] ToWire(List<BuildSupply.ComponentRow> rows)
        {
            if (rows.Count == 0) return null;
            var wire = new WireBuildComponent[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                wire[i].Key = rows[i].Key;
                wire[i].Need = rows[i].Need;
                wire[i].Have = rows[i].Have;
                wire[i].AtNode = rows[i].AtNode;
                wire[i].Elsewhere = rows[i].Elsewhere;
                wire[i].EnRoute = rows[i].EnRoute;
                wire[i].Unserved = rows[i].Unserved;
            }
            return wire;
        }

        /// <summary>Largest staged input first, and nothing that has fallen to zero: a refunded
        /// reservation is no longer holding anything out of routable stock.</summary>
        private static WireBuildReservedInput[] DescribeReservedInputs(BuildQueueEntry entry)
        {
            if (entry.ReservedInputs.Count == 0) return null;
            var rows = new List<WireBuildReservedInput>(entry.ReservedInputs.Count);
            foreach (KeyValuePair<string, long> pair in entry.ReservedInputs)
            {
                if (pair.Value <= 0) continue;
                rows.Add(new WireBuildReservedInput { Key = pair.Key, Quantity = pair.Value });
            }
            if (rows.Count == 0) return null;
            rows.Sort(delegate (WireBuildReservedInput a, WireBuildReservedInput b)
            {
                int byQuantity = b.Quantity.CompareTo(a.Quantity);
                return byQuantity != 0 ? byQuantity : string.CompareOrdinal(a.Key, b.Key);
            });
            return rows.ToArray();
        }

        /// <summary>The node's production history, read under the owner's tag: a span is recorded
        /// against the faction that ran it, so a node that changed hands inside the window shows only
        /// what its current owner made there.</summary>
        private WireNodeProductionSpan[] DescribeProduction(FactionState owner, SpatialNode node)
        {
            List<ProductionManager.ProductionHistoryEntry> spans = _runtime.ProductionHistoryFor(owner.Tag, node.Id);
            int count = Math.Min(spans.Count, FactionsWire.MaxProductionRowsPerNode);
            var rows = new WireNodeProductionSpan[count];
            for (int i = 0; i < count; i++)
            {
                ProductionManager.ProductionHistoryEntry e = spans[i];
                rows[i] = new WireNodeProductionSpan
                {
                    RecipeId = e.RecipeId,
                    Key = e.InvKey,
                    IsOutput = e.IsOutput,
                    Quantity = e.Quantity,
                    StartUtcTicks = e.StartUtc.Ticks,
                    EndUtcTicks = e.EndUtc.Ticks,
                };
            }
            return rows;
        }

        /// <summary>What the owner wants at this node, ranked by the same urgency score the dispatcher
        /// orders on, so the row cap keeps the shortfalls that matter. Each row carries the node's
        /// smoothed use of the item, per hour, for comparing the ask against the draw behind it.</summary>
        private WireNodeDemand[] DescribeDemand(FactionState owner, SpatialNode node)
        {
            WorldModel world = _runtime.World;
            List<DemandSnapshotRow> ranked = DemandSnapshotOrder.RankForSnapshot(
                world.Demand.SnapshotNodeDemands(owner.Tag, node.Id), world, world.Tuning);
            int count = Math.Min(ranked.Count, FactionsWire.MaxDemandRowsPerNode);
            var rows = new WireNodeDemand[count];
            for (int i = 0; i < count; i++)
            {
                DemandEntry d = ranked[i].Demand;
                string key = d.Kind.StartsWith(DemandRegister.ItemPrefix, StringComparison.Ordinal)
                    ? d.Kind.Substring(DemandRegister.ItemPrefix.Length) : null;
                long have = 0;
                if (key != null) node.Inventory.TryGetValue(key, out have);
                rows[i] = new WireNodeDemand
                {
                    Kind = d.Kind,
                    DestNodeId = d.DestNodeId,
                    Quantity = d.Quantity,
                    HasHave = key != null,
                    Have = have,
                    UsedPerHour = key != null ? ExtractionPlanner.DrawPerHour(node, key, world.ItemVolumes, world.Tuning) : 0,
                    Class = (byte)d.Class,
                    DestinedForSale = d.DestinedForSale,
                    FirstPostedUtcTicks = d.FirstPostedUtc.Ticks,
                };
            }
            return rows;
        }

        /// <summary>The owner's demands at this node that ended inside the register's retention,
        /// most recently ended first.</summary>
        private WireNodeEndedDemand[] DescribeEndedDemand(FactionState owner, SpatialNode node)
        {
            WorldModel world = _runtime.World;
            List<DemandEpisode> ended = world.Demand.SnapshotEndedAtNode(owner.Tag, node.Id, world.UtcNow);
            int count = Math.Min(ended.Count, FactionsWire.MaxEndedDemandRowsPerNode);
            var rows = new WireNodeEndedDemand[count];
            for (int i = 0; i < count; i++)
            {
                DemandEpisode e = ended[i];
                rows[i] = new WireNodeEndedDemand
                {
                    Kind = e.Kind,
                    DestNodeId = e.DestNodeId,
                    PeakQuantity = e.PeakQuantity,
                    Class = (byte)e.Class,
                    DestinedForSale = e.DestinedForSale,
                    Consumed = e.Consumed,
                    FirstPostedUtcTicks = e.FirstPostedUtc.Ticks,
                    EndedUtcTicks = e.EndedUtc.Ticks,
                };
            }
            return rows;
        }

        /// <summary>The node's workers, its beds, and the recruit price where its body has a civilian
        /// pool. The price is the validator's own figure, so the button and the reply agree. FAC-493.</summary>
        private void DescribeWorkers(SpatialNode node, ref WireNodeDetail detail)
        {
            WorldModel world = _runtime.World;
            FactionTuning tuning = world.Tuning;
            if (tuning == null) return;
            detail.Workers = WorkerUpkeep.Headcount(node);
            detail.WorkerBeds = WorkerUpkeep.Capacity(node, tuning);
            detail.RecruitPrice = detail.WorkerBeds > 0 && WorkerRecruitValidator.HasCivilianPool(node, world.Planets)
                ? Math.Max(1L, tuning.WorkerRecruitPrice) : 0L;
            double food = WorkerUpkeep.FoodHours(node, tuning, world.Nutrition);
            double air = WorkerUpkeep.AirHours(node, tuning, world.Planets);
            detail.WorkerFoodHours = double.IsInfinity(food) ? -1.0 : food;
            detail.WorkerAirHours = double.IsInfinity(air) ? -1.0 : air;
        }

        private static WireNodeShip[] DescribeShips(SpatialNode node)
        {
            if (node.ShipStock.Count == 0) return null;
            var rows = new List<WireNodeShip>(node.ShipStock.Count);
            foreach (KeyValuePair<string, int> pair in node.ShipStock)
            {
                if (pair.Value <= 0) continue;
                int listed;
                node.ListedForSale.TryGetValue(pair.Key, out listed);
                rows.Add(new WireNodeShip
                {
                    // "" is the free-built marker in ShipStock; the window names it rather than
                    // rendering a blank row.
                    PrefabId = string.IsNullOrEmpty(pair.Key) ? "(free build)" : pair.Key,
                    Ready = pair.Value,
                    ListedForSale = listed,
                });
            }
            return rows.Count == 0 ? null : rows.ToArray();
        }

        // ---- Commands -------------------------------------------------------------------------

        private void OnCommandRequest(ulong steamId, byte[] data)
        {
            string nodeId, target;
            WireNodeCommand command;
            int value;
            if (!FactionsWire.TryReadNodeCommandRequest(data, out nodeId, out command, out target, out value))
            {
                FactionsPlugin.Log.Warn("Node command from " + steamId + " was unreadable.");
                return;
            }

            string message = Run(steamId, nodeId, command, target, value);
            // The runtime's player-facing entry points answer in prose whether they succeeded or
            // refused, so "ok" here means the command was dispatched at all, not that it changed
            // anything. The snapshot that follows is the authoritative answer either way.
            _uplink.Send(steamId, FactionsWire.WriteNodeCommandResult(message != null, message ?? "No such command."));

            // Show the effect now rather than at the next cadence tick.
            if (_watching.ContainsKey(steamId)) _owesSnapshot.Add(steamId);
            FlushOwed();

            // Behind the same 'factions debug uplink' gate as every other line here, not the Torch
            // log by default. The mutation itself is already recorded in the Factions day log by the
            // code that performs it — SHIPYARD_PAUSE_BY_PLAYER and BUILD_PRIORITY_BY_PLAYER from
            // FactionRuntime, BUILD_CANCELLED_BY_PLAYER from ShipyardManager — so what this line
            // adds is the SteamID behind the click, which is diagnostic, not the record. The chat
            // verb has the same gap and logs the same faction tag.
            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("NODE_COMMAND steam=" + steamId + " node=" + nodeId
                                     + " command=" + command + " target=" + target + " value=" + value);
            }
        }

        // ---- Build button ---------------------------------------------------------------------

        /// <summary>Answers "what may I build at this yard" for one client, once per ask. A
        /// request/response pair rather than a subscription for the reason
        /// <see cref="WireMessageKind.NodeStockRequest"/> records: the dialog opens over the node
        /// window, which holds this client's single watch, and a second subscription would take that
        /// watch away from it.
        ///
        /// <para>The node id is echoed in the reply so a client that changed station while this was
        /// in flight drops it rather than showing one yard's hulls under another's name.</para></summary>
        private void OnBuildOptionsRequest(ulong steamId, byte[] data)
        {
            string nodeId;
            if (!FactionsWire.TryReadBuildOptionsRequest(data, out nodeId))
            {
                FactionsPlugin.Log.Warn("Build options request from " + steamId + " was unreadable.");
                return;
            }

            var prefabs = new List<WireBuildPrefab>();
            long identityId = IdentityFor(steamId);
            FactionState owner = identityId != 0L ? ViewerFaction(identityId) : null;
            if (owner != null && _runtime != null && _runtime.World != null)
            {
                foreach (BuildPrefabOption option in _runtime.BuildOptionsForPlayer(owner, nodeId))
                {
                    prefabs.Add(new WireBuildPrefab
                    {
                        PrefabId = option.PrefabId ?? "",
                        Label = option.Label ?? "",
                        Roles = option.Roles.ToArray(),
                        CargoLiters = option.CargoLiters,
                        Dps = option.Dps,
                        GridSize = option.GridSize ?? "",
                        Propulsion = option.Propulsion ?? "",
                        DrillYieldScore = option.DrillYieldScore,
                        BomUnits = option.BomUnits,
                        CanFlyFromHere = option.CanFlyFromHere,
                    });
                }
            }

            _uplink.Send(steamId, FactionsWire.WriteBuildOptions(
                nodeId ?? "", ShipyardManager.PlayerQueueRoles, prefabs));
            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("BUILD_OPTIONS steam=" + steamId + " node=" + nodeId
                                     + " prefabs=" + prefabs.Count);
            }
        }

        /// <summary>Queues one hull, once. Routed into the same <c>FactionRuntime</c> entry point
        /// <c>!factions station queue build</c> reaches, which re-checks the node, its ownership, its
        /// Shipyard, that the hull fits some build role, and its build recipe — so the
        /// pick-list the dialog was built from is a convenience and never an authorisation, and the
        /// reply is that method's own text.
        ///
        /// <para>No log line of its own: the mutation is already recorded by the code that performs
        /// it (<c>BUILD_QUEUED_BY_PLAYER</c>, and <c>BUILD_OVERRIDDEN_BY_PLAYER</c> when it displaced
        /// an entry), identically for this path and the chat verb. The gated traffic line adds the
        /// SteamID behind the click, exactly as <c>NODE_COMMAND</c> does.</para></summary>
        private void OnBuildRequest(ulong steamId, byte[] data)
        {
            string nodeId, prefabId;
            if (!FactionsWire.TryReadBuildRequest(data, out nodeId, out prefabId))
            {
                FactionsPlugin.Log.Warn("Build request from " + steamId + " was unreadable.");
                return;
            }

            string message = RunBuild(steamId, nodeId, prefabId);
            _uplink.Send(steamId, FactionsWire.WriteBuildResult(message != null, message ?? "No such command."));

            // Show the queued entry now rather than at the next cadence tick.
            if (_watching.ContainsKey(steamId)) _owesSnapshot.Add(steamId);
            FlushOwed();

            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("BUILD_REQUEST steam=" + steamId + " node=" + nodeId
                                     + " prefab=" + prefabId);
            }
        }

        private string RunBuild(ulong steamId, string nodeId, string prefabId)
        {
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";
            if (string.IsNullOrEmpty(nodeId)) return "No station selected.";

            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";

            FactionState owner = ViewerFaction(identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) return refusal;

            return _runtime.QueueBuildForPlayer(owner, nodeId, prefabId);
        }

        /// <summary>
        /// Runs one command against the caller's own faction. Every path here re-resolves the
        /// faction from the SteamID and hands it to the same <see cref="FactionRuntime"/> entry point
        /// the chat verb calls, so a client is never trusted about who it is.
        ///
        /// <para>The node-scoped verbs additionally hand over the node id, and the entry point checks
        /// ownership of it itself — a client naming a node it does not own is refused by the code
        /// that already refuses the chat verb. <see cref="WireNodeCommand.ReserveToggle"/> and
        /// <see cref="WireNodeCommand.InstallPriority"/> are faction-wide and take no node: the
        /// re-resolved faction IS the authorisation, exactly as it is for their chat verbs, which
        /// name no node either.</para>
        /// </summary>
        private string Run(ulong steamId, string nodeId, WireNodeCommand command, string target, int value)
        {
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";
            if (string.IsNullOrEmpty(nodeId)) return "No station selected.";

            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";

            FactionState owner = ViewerFaction(identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) return refusal;

            switch (command)
            {
                case WireNodeCommand.QueuePause:
                    return _runtime.PauseShipyardForPlayer(owner, nodeId, true);
                case WireNodeCommand.QueueResume:
                    return _runtime.PauseShipyardForPlayer(owner, nodeId, false);
                case WireNodeCommand.QueueCancel:
                    if (string.IsNullOrEmpty(target)) return "Select a build to cancel.";
                    return _runtime.CancelBuildForPlayer(owner, nodeId, target);
                case WireNodeCommand.QueuePriority:
                    if (string.IsNullOrEmpty(target)) return "Select a build to rank.";
                    return _runtime.SetBuildPriorityForPlayer(owner, nodeId, target, value);
                case WireNodeCommand.Develop:
                case WireNodeCommand.ReserveToggle:
                case WireNodeCommand.InstallPriority:
                    return RunDevelop(owner, nodeId, command, target, value);
                case WireNodeCommand.HoldToggle:
                    // A toggle, so the server decides the new state from what it already holds
                    // rather than trusting a client that may be showing a stale snapshot.
                    return _runtime.SetReinforceOptInForPlayer(
                        owner, nodeId, owner.ReinforceOptIn.ContainsKey(nodeId) ? (int?)null : 0);
                case WireNodeCommand.HoldRank:
                    if (!owner.ReinforceOptIn.ContainsKey(nodeId))
                        return "Hold this node before ranking it.";
                    return _runtime.SetReinforceOptInForPlayer(owner, nodeId, value);
                case WireNodeCommand.HoldBudget:
                    return RunHoldBudget(owner, target, value);
                case WireNodeCommand.TradeToggle:
                    // Not a blind toggle, unlike HoldToggle: there are two switches and the request
                    // names which one, so the client sends the state it is asking for and the server
                    // applies it. A stale snapshot then re-asserts what the player clicked rather
                    // than inverting the other direction.
                    return _runtime.SetTradeOptInForPlayer(owner, target, value != 0);
                case WireNodeCommand.TradeBudget:
                    return RunTradeBudget(owner, target, value);
                case WireNodeCommand.Recruit:
                    return _runtime.RecruitWorkersForPlayer(owner, nodeId, value);
                case WireNodeCommand.Found:
                    // Anything but the one word that means fortified is standard: a style is a look,
                    // and a typo must not spend the faction's money on the wrong one or refuse a
                    // purchase the player has already confirmed.
                    return _runtime.FoundStationForPlayer(
                        owner, nodeId,
                        string.Equals(target, "fortified", StringComparison.OrdinalIgnoreCase));
                default:
                    return null;
            }
        }

        /// <summary>One trade budget knob, on exactly the terms <see cref="RunHoldBudget"/>
        /// documents: the two fractions arrive as whole percent, -1 clears back to the world's
        /// tuning, and "floor" is plain credits.</summary>
        private string RunTradeBudget(FactionState owner, string knob, int value)
        {
            if (string.IsNullOrEmpty(knob)) return "Name a budget knob.";
            bool clear = value < 0;
            bool isFloor = string.Equals(knob, "floor", StringComparison.OrdinalIgnoreCase);
            double? resolved = clear ? (double?)null : (isFloor ? value : value / 100.0);
            return _runtime.SetTradeBudgetForPlayer(owner, knob, resolved);
        }

        /// <summary>One reinforce budget knob, arriving as whole percent for the two fractions and
        /// plain credits for the floor; -1 clears it back to the world's tuning.
        ///
        /// <para>Percent is the client-facing unit because a share is a percentage everywhere a
        /// player meets one, and the request carries an int. The cost is that a default finer than a
        /// percent cannot be typed — the shipped treasury fraction is 0.001 — so clearing is the way
        /// back to it, not "set 0", which would mean a real zero.</para></summary>
        private string RunHoldBudget(FactionState owner, string knob, int value)
        {
            if (string.IsNullOrEmpty(knob)) return "Name a budget knob.";
            bool clear = value < 0;
            bool isFloor = string.Equals(knob, "floor", StringComparison.OrdinalIgnoreCase);
            double? resolved = clear ? (double?)null : (isFloor ? value : value / 100.0);
            return _runtime.SetReinforceBudgetForPlayer(owner, knob, resolved);
        }

        /// <summary>The three capability verbs, which all name a capability where the queue verbs
        /// name a build entry. Parsed through <see cref="CapabilityTokens"/> rather than a bare
        /// <c>Enum.TryParse</c>: that accepts any numeric string, so an unchecked parse would let a
        /// client write a capability key into a node's persisted map that no reader has a name
        /// for.</summary>
        private string RunDevelop(FactionState owner, string nodeId, WireNodeCommand command,
                                  string target, int value)
        {
            NodeCapabilityKind capability;
            if (!CapabilityTokens.TryParse(target, out capability))
                return "Select a capability.";

            switch (command)
            {
                case WireNodeCommand.Develop:
                    return _runtime.DevelopStationForPlayer(owner, nodeId, capability);
                case WireNodeCommand.ReserveToggle:
                    return _runtime.ToggleReservationForPlayer(owner, capability);
                default:
                    return _runtime.SetInstallPriorityForPlayer(owner, capability, value);
            }
        }

        /// <summary>Sends to every watcher owed one, outside the cadence — used after a command so
        /// the window shows what the command did without a wait.</summary>
        private void FlushOwed()
        {
            if (_owesSnapshot.Count == 0 || MySession.Static == null) return;
            var owed = new List<ulong>(_owesSnapshot);
            foreach (ulong steamId in owed)
            {
                string nodeId;
                if (!_watching.TryGetValue(steamId, out nodeId)) { _owesSnapshot.Remove(steamId); continue; }
                long identityId = IdentityFor(steamId);
                if (identityId == 0L) continue;   // leave it owed; the next Tick retries
                _owesSnapshot.Remove(steamId);
                SendSnapshot(steamId, identityId, nodeId);
            }
        }

        /// <summary>The Factions faction behind a player identity, or null when they are in no SE
        /// faction or in one that has claimed nothing yet. Mirrors
        /// <c>PlayerCommandHelpers.TryResolveCallerFaction</c>, including its player-faction
        /// requirement — an NPC tag must not be reachable from a client.</summary>
        /// <summary>Who holds this node and how firmly, as this viewer may know it. The window is
        /// one click off a map marker, so it answers from the same memory the marker was drawn from
        /// (<see cref="ViewerKnowledge"/>) rather than from the live graph — otherwise opening a dim
        /// marker would report today's owner and the dimming would buy nothing.
        ///
        /// <para>A node the viewer has never discovered reads live, because this window has no
        /// discovery gate of its own: it answers any node id a client asks for. That is its own
        /// defect and its own fix (FAC-359); until then this must not pretend to a gate it does not
        /// have.</para></summary>
        private NodeBelief Believe(long viewerIdentityId, SpatialNode node)
        {
            ViewerKnowledge.BuildSources(_runtime, _scanner, viewerIdentityId, _viewerSources);
            string viewerTag = ViewerKnowledge.FactionTag(viewerIdentityId);
            DateTime nowUtc = _runtime.World.UtcNow;
            NodeBelief belief;
            if (ViewerKnowledge.IsDetected(_viewerSources, node.Id, nowUtc)
                || !KnowledgeGate.TryBelief(
                        _runtime.World.Graph, viewerTag, _viewerSources, node, nowUtc,
                        _runtime.World.Tuning.DiscoveryHorizon, _runtime.World.Tuning.BirthrightRangeKm,
                        out belief))
            {
                return new NodeBelief
                {
                    Node = node,
                    OwnerTag = node.OwnerFactionTag,
                    HoldStrength = node.HoldStrength,
                    ResourceValue = node.ResourceValue,
                    IsLive = true,
                };
            }
            return belief;
        }

        private FactionState ViewerFaction(long identityId)
        {
            if (identityId == 0L || MySession.Static == null || MySession.Static.Factions == null) return null;
            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (seFaction == null) return null;
            FactionState owner = _runtime.FindFaction(seFaction.Tag);
            return owner != null && owner.IsPlayerFaction ? owner : null;
        }

        private static long IdentityFor(ulong steamId)
        {
            if (MySession.Static == null) return 0L;
            return LinkedViewers.IdentityFor(steamId);
        }

        /// <summary>Drops watchers that are no longer online, on the same cadence and for the same
        /// reason <see cref="UplinkManager.PruneOffline"/> drops links.</summary>
        public void PruneOffline(ICollection<ulong> onlineSteamIds)
        {
            if (_watching.Count == 0) return;
            List<ulong> stale = null;
            foreach (ulong steamId in _watching.Keys)
            {
                if (onlineSteamIds.Contains(steamId)) continue;
                if (stale == null) stale = new List<ulong>();
                stale.Add(steamId);
            }
            if (stale == null) return;
            foreach (ulong steamId in stale)
            {
                _watching.Remove(steamId);
                _owesSnapshot.Remove(steamId);
            }
        }

        public void Close()
        {
            _watching.Clear();
            _owesSnapshot.Clear();
        }
    }
}
