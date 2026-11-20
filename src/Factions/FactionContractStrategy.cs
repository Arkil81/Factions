using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using NLog;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Contracts;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using VRage;
using VRage.Game;
using VRage.Game.Definitions.SessionComponents;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components.Contracts;
using VRage.Library.Utils;
using VRage.ObjectBuilder;
using VRage.ObjectBuilders;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Wraps a vanilla <see cref="MyContractTypeBaseStrategy"/> and overrides contract generation
    /// at stations owned by registered Factions factions. For those stations, contract parameters
    /// (item type, amount, reward) are driven by the demand register instead of the vanilla
    /// random-item picker. Stations not managed by Factions delegate to the vanilla strategy.
    ///
    /// <para>Injected into <c>MySessionComponentContractSystem.m_contractTypeStrategies</c> at
    /// session load via reflection. One wrapper instance per contract type definition.</para>
    /// </summary>
    internal sealed class FactionContractStrategy : MyContractTypeBaseStrategy
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private readonly MyContractTypeBaseStrategy _vanilla;
        private readonly HashSet<string>            _registeredTags;
        private readonly DemandRegister             _demands;
        private readonly IItemPriceCatalogue        _prices;
        private readonly WorldModel                 _world;
        private readonly FactionRuntime             _runtime;

        public FactionContractStrategy(
            MySessionComponentEconomyDefinition economyDefinition,
            MyContractTypeDefinition            contractTypeDefinition,
            MyContractTypeBaseStrategy          vanilla,
            HashSet<string>                     registeredTags,
            DemandRegister                      demands,
            IItemPriceCatalogue                 prices,
            WorldModel                          world,
            FactionRuntime                      runtime)
            : base(economyDefinition, contractTypeDefinition)
        {
            _vanilla        = vanilla;
            _registeredTags = registeredTags;
            _demands        = demands;
            _prices         = prices;
            _world          = world;
            _runtime        = runtime;
        }

        public override bool CanBeGenerated(IMyFactionStation station, MyFaction faction)
        {
            // Factions-managed stations are lazily materialised (StationMaterializer/Cornerstone II —
            // faction.Stations holds every founded station's abstract record regardless of whether a
            // physical grid exists yet; StationEntityId is 0 until BeginSpawn's success callback sets
            // it, and reset to 0 by CloseEntry on despawn). No physical grid means no physical contract
            // terminal, so nobody could ever see or accept a contract generated here anyway --
            // live-confirmed 2026-08-08 that vanilla's own per-tick CreateContractsForStation burst
            // (every EconomyTickInSeconds, synchronous, blocks the whole sim thread) processes every
            // one of these regardless, and Factions can have 600+ founded stations with only a handful
            // ever spawned at once. Skipping the expensive GenerateContract work here (this method is
            // checked before that retry loop, not inside it) is the real fix; RefreshBoardOnSpawn below
            // reactively populates the board the moment a station actually spawns instead of waiting
            // for the next periodic tick.
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (_registeredTags.Contains(faction.Tag)) return station.StationEntityId != 0;
                return _vanilla.CanBeGenerated(station, faction);
            }
            finally
            {
                EconomyTickProfiler.CanBeGen.Record(Stopwatch.GetTimestamp() - t0);
            }
        }

        // ---- spawn-reactive board population (docs/todo.md's "Contract generation burst" entry) ---

        // Shared across every FactionContractStrategy instance (one per wrapped contract-type
        // definition) -- all of them receive the identical registeredTags reference from
        // InjectStrategies, so stashing it once here lets StationMaterializer (which doesn't otherwise
        // hold a reference to any FactionContractStrategy instance) ask "is this faction ours" without
        // a new constructor dependency.
        private static HashSet<string> s_registeredTags;

        internal static bool IsRegistered(string tag)
        {
            return s_registeredTags != null && tag != null && s_registeredTags.Contains(tag);
        }

        // CreateContractsForStation is internal -- reflected the same way StationMaterializer.cs's
        // StationResourcesUpdate and StationFounder.cs's StationNameSetter already reach other internal
        // SE members. Not a Harmony patch: vanilla's own method is untouched, this just calls it an
        // extra time (at spawn) instead of only from its own periodic tick.
        private static readonly MethodInfo CreateContractsForStationM = AccessTools.Method(
            typeof(MySessionComponentContractSystem), "CreateContractsForStation");

        /// <summary>Called from <see cref="StationMaterializer"/>'s spawn-success callback so a
        /// station's board is populated the moment it becomes physically reachable, rather than left
        /// empty until vanilla's own next <c>EconomyTickInSeconds</c> tick (up to 10 minutes on the
        /// live world). Mirrors what that periodic tick would eventually do for this one station:
        /// current contract count/list come from the public <see
        /// cref="MySessionComponentContractSystem.GetAvailableContractsForFactionStation"/> (the same
        /// API <see cref="CollectAlreadyOfferedItems"/> already uses) rather than the internal
        /// <c>GetAvailableContractCountsByStation</c>, so this needs reflection only for the one
        /// internal call itself.</summary>
        internal static void RefreshBoardOnSpawn(IMyFactionStation station, MyFaction faction)
        {
            try
            {
                MySessionComponentContractSystem contractSystem =
                    MySession.Static?.GetComponent<MySessionComponentContractSystem>();
                MySessionComponentEconomy economy =
                    MySession.Static?.GetComponent<MySessionComponentEconomy>();
                if (contractSystem == null || economy == null || CreateContractsForStationM == null || station == null)
                    return;

                var existing = new List<MyContract>();
                foreach (IMyContract c in contractSystem.GetAvailableContractsForFactionStation(station.Id))
                {
                    MyContract concrete = c as MyContract;
                    if (concrete != null) existing.Add(concrete);
                }

                var cGen = new MyContractGenerator(economy.EconomyDefinition);
                MyTimeSpan now = MyTimeSpan.FromMilliseconds(MySandboxGame.TotalGamePlayTimeInMilliseconds);
                var args = new object[] { cGen, faction, station, existing.Count, existing };
                CreateContractsForStationM.Invoke(contractSystem, args);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "RefreshBoardOnSpawn failed for station " + (station != null ? station.Id.ToString() : "?") + ".");
            }
        }

        public override MyContractCreationResults GenerateContract(
            out MyContract contract, long factionId, long stationId,
            MyMinimalPriceCalculator calculator, MyTimeSpan now)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                return GenerateContractCore(out contract, factionId, stationId, calculator, now);
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - t0;
                FactionsPlugin.Instance?.RecordContractGenTiming(elapsed);
                EconomyTickProfiler.OurGenerate.Record(elapsed);
            }
        }

        private MyContractCreationResults GenerateContractCore(
            out MyContract contract, long factionId, long stationId,
            MyMinimalPriceCalculator calculator, MyTimeSpan now)
        {
            contract = null;
            MyFaction faction = MySession.Static?.Factions?.TryGetFactionById(factionId) as MyFaction;
            if (faction == null) return MyContractCreationResults.Error;

            if (!_registeredTags.Contains(faction.Tag))
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            // ---- brain-driven path -------------------------------------------------------------
            // A contract must reflect THIS station's own node's outstanding need — SnapshotItemDemands
            // returns demand posted anywhere in the faction, so without the NodeId filter below the
            // largest-quantity entry from a completely unrelated node (e.g. a shipyard's component
            // shortfall) could win and get priced/delivered here instead.
            string nodeId = FindNodeForStation(faction.Tag, stationId);
            if (nodeId == null)
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            // Reinforce-via-Repair: a distinct path from the Acquisition/Hauling demand-driven logic
            // below — Repair has no item-demand concept, it substitutes for the automated Reinforce
            // action instead. Fully reimplemented (not vanilla-delegating) because MinGridDistance/
            // MaxGridDistance live on the shared, session-wide MyContractTypeRepairDefinition and
            // MyContractRepair.PrepareGPS is private/non-virtual — neither is overridable per-instance.
            if (_vanilla is MyContractTypeRepairStrategy)
                return GenerateReinforceRepairContract(out contract, faction, nodeId, factionId, stationId, calculator, now);

            // Guard-via-PvEBounty: same "player substitutes for an automated action" pattern as
            // Reinforce-via-Repair, applied to Guard. Wraps vanilla's own MyContractTypePvEBountyStrategy
            // (already shipped in base-game content as PvEBounty_HuntPirate/PvEBounty_HuntTrader) but
            // completely overrides target selection: instead of vanilla's random spawn-group pirate,
            // the bounty's target IS the real incoming Raid/Retaliate/Conquer fleet threatening this
            // node, once that fleet already has a real physical grid (Cornerstone II — a player has to
            // already be close enough to have triggered its spawn; see GenerateGuardBountyContract).
            if (_vanilla is MyContractTypePvEBountyStrategy)
                return GenerateGuardBountyContract(out contract, faction, nodeId, factionId, stationId, calculator, now);

            // Haul (vanilla's "Courier"/Hauling flavor): the mirror image of the Acquisition path
            // below -- generated at the SOURCE station (nodeId here is this station's own node, which
            // must currently hold real surplus of something another owned node needs), not the need
            // node. See GenerateHaulContract and docs/material-vectors.md's Slice C.
            if (_vanilla is MyContractTypeHaulingStrategy)
                return GenerateHaulContract(out contract, faction, nodeId, factionId, stationId, calculator, now);

            // Only offer items this specific contract-type variant is actually allowed to request —
            // e.g. ObtainAndDeliver_Military's AvailableItems is ammo/weapons only; without this check
            // a "Military" flavored contract could ask for SteelPlate just because that's what won the
            // quantity sort.
            List<SerializableDefinitionId> allowedItems =
                (m_contractTypeDefinition as MyContractTypeObtainAndDeliverDefinition)?.AvailableItems;

            // Vanilla's own CreateContractsForStation retries GenerateContract several times per
            // refill pass (one call per open contract-board slot) and already rejects an exact
            // duplicate item via CheckContractAgainstOther — but without this exclusion set, every
            // retry deterministically re-picks the SAME top-quantity demand, gets rejected as a
            // duplicate, and the remaining slots go unfilled instead of surfacing the next-largest
            // outstanding demand (e.g. an ingot shortfall dwarfed by a single Comp:Explosives entry).
            HashSet<MyDefinitionId> alreadyOffered = CollectAlreadyOfferedItems(stationId);

            List<DemandEntry> demands = ItemDemandsFor(faction.Tag);
            demands.RemoveAll(d => !string.Equals(d.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
            demands.Sort((a, b) => b.Quantity.CompareTo(a.Quantity));

            DemandEntry best = default(DemandEntry);
            MyPhysicalItemDefinition itemDef = null;
            string invKey = null;
            string subTypeId = null;
            int skippedNoDef = 0, skippedNotAllowed = 0, skippedAlreadyOffered = 0;
            foreach (DemandEntry d in demands)
            {
                if (!d.Kind.StartsWith("Item:")) continue;
                // Kind is "Item:" + invKey, and invKey carries an Ore:/Ingot:/Comp: category prefix
                // for anything ProductionManager.InvKey produced. FindItemDefinition (SE physical-item
                // lookup) and AvailableItems need the bare SubTypeId, but IItemPriceCatalogue.BasePrice
                // needs the prefixed invKey to resolve its exact (TypeId, subtype) price — passing it
                // the bare name skips that resolution and falls back to a subtype-only price table that
                // collapses to the MAX price across every TypeId sharing that bare name, which can wildly
                // overprice an item that happens to share a subtype string with something pricier.
                string candidateInvKey = d.Kind.Substring(5);
                string candidateSubType = StripInvKeyPrefix(candidateInvKey);
                MyPhysicalItemDefinition candidateDef = FindItemDefinition(candidateSubType);
                if (candidateDef == null) { skippedNoDef++; continue; }
                if (allowedItems != null && allowedItems.Count > 0 && !ContainsSubtype(allowedItems, candidateSubType))
                    { skippedNotAllowed++; continue; }
                if (alreadyOffered.Contains(candidateDef.Id)) { skippedAlreadyOffered++; continue; }
                best = d;
                itemDef = candidateDef;
                invKey = candidateInvKey;
                subTypeId = candidateSubType;
                break;
            }
            if (itemDef == null)
            {
                if (DebugTraces.Contracts)
                {
                    Log.Info("Contract generation [" + m_contractTypeDefinition.Id.SubtypeName + "] station=" + stationId
                        + " node=" + nodeId + ": no eligible candidate among " + demands.Count + " node-scoped Item: demand(s)"
                        + " (noItemDef=" + skippedNoDef + " notAllowed=" + skippedNotAllowed
                        + " alreadyOffered=" + skippedAlreadyOffered + ") — falling back to vanilla.");
                }
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);
            }

            // Get the station to use as the end point (deliver here).
            IMyFactionStation factionStation =
                (MySession.Static.Factions.TryGetFactionById(factionId) as MyFaction)?.GetStationById(stationId);
            if (factionStation == null) return MyContractCreationResults.Error;

            // Compute reward: base price × scarcity × requested amount, rounded to thousands.
            long basePrice = _prices.BasePrice(invKey);
            if (basePrice <= 0)
            {
                // Fall back to calculator price.
                int minPrice;
                calculator.CalculateMinimalPrices(new SerializableDefinitionId[] { itemDef.Id });
                calculator.TryGetItemMinimalPrice(itemDef.Id, out minPrice);
                basePrice = minPrice;
            }

            int amount = (int)Math.Min(Math.Max(best.Quantity, 1L), int.MaxValue);

            // Priced before the affordability trim, at the midpoint of the full request: a trimmed
            // contract would price higher on the curve, and the reward stays the conservative one.
            double scarcity = QueryRowScarcity(nodeId, invKey, amount);

            // A contract is a promise to pay. Trim the quantity to what the faction can actually
            // fund this window, keeping the per-unit reward honest -- see
            // ContractEconomy.AffordableContractQuantity for why the quantity moves and the price
            // does not. Refuse outright rather than post an offer that cannot be honoured (FAC-156).
            long unitReward = (long)Math.Round(basePrice * scarcity);
            long contractBudget = ContractRewardSliceFor(faction.Tag);
            int affordable = ContractEconomy.AffordableContractQuantity(amount, unitReward, contractBudget);
            if (affordable <= 0)
            {
                if (DebugTraces.Contracts)
                {
                    Log.Info("Contract skipped [" + m_contractTypeDefinition.Id.SubtypeName + "] faction=" + faction.Tag
                        + " node=" + nodeId + " item=" + subTypeId + " qty=" + amount
                        + " unitReward=" + unitReward + " budget=" + contractBudget + " reason=unaffordable");
                }
                return MyContractCreationResults.Fail_Common;
            }
            amount = affordable;

            long rewardMoney = (unitReward * amount / 1000) * 1000;
            rewardMoney = Math.Max(rewardMoney, m_contractTypeDefinition.MinimumMoney);

            // Charged at generation, not at acceptance: an offer standing on a station's board is an
            // outstanding liability, and without this every contract generated in one window would
            // see the whole budget and they would collectively over-promise -- the same per-row vs
            // whole-set defect StationListings had.
            s_contractBudget.Record(faction.Tag, _world.UtcNow, rewardMoney, _world.Tuning.SpendBudgetWindowSeconds);

            // Slice C: know whether a real in-network surplus source backs this demand. Reward math
            // is unchanged either way (docs/material-vectors.md) -- this is purely so the log records
            // it and so a sibling Haul contract for the same vector, if one exists, can be found by
            // OnHaulVectorContractAccepted below. The completion hook (crediting nodeId's inventory)
            // and the vector-contract registration both apply regardless of sourceKnown: the player
            // still delivers real items here even when our own network happened to have no surplus.
            MaterialVector vector;
            bool sourceKnown = _runtime.TryMatchHaulSource(faction.Tag, nodeId, subTypeId, amount, out vector);

            if (DebugTraces.Contracts)
            {
                Log.Info("Contract generated [" + m_contractTypeDefinition.Id.SubtypeName + "] station=" + stationId
                    + " node=" + nodeId + " item=" + subTypeId + " (" + invKey + ") qty=" + amount
                    + " basePrice=" + basePrice + " scarcity=" + scarcity.ToString("0.###")
                    + " reward=" + rewardMoney + " sourceKnown=" + sourceKnown
                    + (sourceKnown ? " source=" + vector.SourceNodeId + " available=" + vector.Quantity : "")
                    + " (candidates: eligible=1 skipped noItemDef=" + skippedNoDef
                    + " notAllowed=" + skippedNotAllowed + " alreadyOffered=" + skippedAlreadyOffered + ")");
            }

            // Build the ObtainAndDeliver object builder (mirrors MyContractTypeAcquisitionStrategy).
            long contractId   = MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.CONTRACT);
            long conditionId  = MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.CONTRACT_CONDITION);

            var conditionOb = new MyObjectBuilder_ContractConditionDeliverItems
            {
                Id           = conditionId,
                ContractId   = contractId,
                FactionEndId = factionId,
                StationEndId = stationId,
                BlockEndId   = 0L,
                SubId        = 0,
                IsFinished   = false,
                TransferItems = false,
                ItemType     = itemDef.Id,
                ItemAmount   = amount,
                ItemVolume   = itemDef.Volume * amount,
            };

            MyObjectBuilder_ContractObtainAndDeliver contractOb =
                CreateCommonOb<MyObjectBuilder_ContractObtainAndDeliver>(
                    m_contractTypeDefinition, contractId, factionId, factionStation, rewardMoney);
            contractOb.RemainingTimeInS             = null;
            contractOb.ContractCondition            = conditionOb;
            contractOb.ContractTypeDefinitionId     = m_contractTypeDefinition.Id;

            var result = new MyContractObtainAndDeliver();
            result.Init(contractOb);
            contract = result;

            // Slice C: completion feedback loop + Acquisition/Haul cross-invalidation -- see
            // "---- Slice C: Acquisition/Haul vector-contract tracking ----" below. Wired
            // unconditionally (not gated on sourceKnown): the player is delivering real items to
            // nodeId either way, so the completion hook must credit its inventory regardless; the
            // registration is what lets a real Haul sibling (if one exists) find and invalidate this
            // contract on its own acceptance, and vice versa.
            contract.OnContractAcquired = (Action<long>)Delegate.Combine(contract.OnContractAcquired,
                new Action<long>(_ => OnHaulVectorContractAccepted(contractId, faction.Tag, nodeId, subTypeId, amount, "Acquisition")));
            contract.ContractChangedState = (Action<long, MyContractStateEnum, MyContractStateEnum>)
                Delegate.Combine(contract.ContractChangedState,
                    new Action<long, MyContractStateEnum, MyContractStateEnum>(OnHaulVectorContractStateChanged));
            RegisterVectorContract(faction.Tag, nodeId, subTypeId, contractId);

            return MyContractCreationResults.Success;
        }

        // Zero-gravity search tolerance, mirrors vanilla MyContractTypeRepairStrategy's own epsilon.
        private const float RepairGravitySquaredEpsilon = 0.0001f;
        private const int   RepairPlacementMaxTries = 20;

        /// <summary>
        /// Reimplements MyContractTypeRepairStrategy.GenerateContract (decompiled reference) with a
        /// tuning-driven, much tighter placement radius (vanilla ships 300m-10,000m; a player should
        /// be able to walk/fly this in a couple of minutes) and a faction-funded reward that mirrors
        /// the automated Reinforce action's own cost instead of vanilla's distance/prefab formula.
        /// On completion (see <see cref="OnRepairContractStateChanged"/>) the faction pays
        /// <see cref="FactionTuning.ReinforceCost"/> and the node is reinforced for real.
        /// </summary>
        private static string s_demandMemoTag;
        private static long s_demandMemoTicks;
        private static List<DemandEntry> s_demandMemo;

        /// <summary>The faction's item demands, read once per 100 ms. SE calls <see cref="GenerateContract"/>
        /// once per board slot and again on every rejection, ~58 times per station on 2026-10-04
        /// (408 calls, 7 stations), and each call scanned the whole demand register; one station's refill
        /// reached 51 ms in a single frame. A board refill runs inside one frame, so the copy it reads is
        /// the register as it stood then. Game thread only.</summary>
        private List<DemandEntry> ItemDemandsFor(string factionTag)
        {
            long now = Stopwatch.GetTimestamp();
            if (s_demandMemo == null || s_demandMemoTag != factionTag || now - s_demandMemoTicks > Stopwatch.Frequency / 10)
            {
                s_demandMemo = _demands.SnapshotItemDemands(factionTag);
                s_demandMemoTag = factionTag;
                s_demandMemoTicks = now;
            }
            return new List<DemandEntry>(s_demandMemo);
        }

        private MyContractCreationResults GenerateReinforceRepairContract(
            out MyContract contract, MyFaction faction, string nodeId, long factionId, long stationId,
            MyMinimalPriceCalculator calculator, MyTimeSpan now)
        {
            contract = null;
            FactionTuning tuning = _world.Tuning;

            // An insolvent faction doesn't get to grant free maintenance via a player either --
            // mirrors FleetManager's own treasury gate for automated Reinforce. Fall through to a
            // plain, un-hooked vanilla Repair contract instead of blocking the board slot outright.
            if (_world.GetTreasury(faction.Tag) < tuning.ReinforceCost)
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            // Repair has no "item" to compare, so vanilla's own CheckContractAgainstOther duplicate
            // check (built for MyContractObtainAndDeliver) never fires for it -- without this guard,
            // vanilla's per-slot retry loop in CreateContractsForStation deterministically regenerates
            // another Reinforce-via-Repair for the SAME node on every open board slot in one pass
            // (live-confirmed: one restart produced 4-5 near-identical contracts at a single station in
            // the same millisecond). Each would be independently completable, so a player clearing
            // several in a row would drain the faction's ReinforceCost repeatedly for a node that's
            // already back near full hold after the first one. Cap at one active hooked Repair contract
            // per station.
            if (StationHasActiveRepairContract(stationId))
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            IMyFactionStation factionStation = faction.GetStationById(stationId);
            if (factionStation == null) return MyContractCreationResults.Error;
            if (factionStation.Type == MyStationTypeEnum.Outpost)
                return MyContractCreationResults.Fail_Impossible;

            // MyContractTypeRepairDefinition is `internal` in Sandbox.Game -- not nameable from this
            // assembly -- so its fields are read via reflection off the public MyContractTypeDefinition
            // base reference we already hold. MinGridDistance/MaxGridDistance are deliberately NOT
            // read here; tuning fully replaces them.
            List<string> prefabNames = GetFieldValue<List<string>>(m_contractTypeDefinition, "PrefabNames", null);
            double durationBaseTime = GetFieldValue(m_contractTypeDefinition, "Duration_BaseTime", 0.0);
            double durationPerMeter = GetFieldValue(m_contractTypeDefinition, "Duration_TimePerMeter", 0.0);
            float timeToPriceDenom = GetFieldValue(m_contractTypeDefinition, "TimeToPriceDenominator", 60f);

            var searchSphere = new BoundingSphereD(factionStation.Position, tuning.RepairGridMaxDistanceM);
            Vector3D targetPos = Vector3D.Zero;
            bool found = false;
            for (int i = 0; i <= RepairPlacementMaxTries; i++)
            {
                Vector3D? candidate = searchSphere.RandomToUniformPointInSphereWithInnerCutout(
                    MyRandom.Instance.NextDouble(), MyRandom.Instance.NextDouble(), MyRandom.Instance.NextDouble(),
                    tuning.RepairGridMinDistanceM);
                if (!candidate.HasValue) continue;

                var seeds = new List<MyObjectSeed>();
                MyProceduralWorldGenerator.Static.OverlapAllAsteroidAndEncountersInSphere(
                    new BoundingSphereD(candidate.Value, 50), seeds);
                if (seeds.Count > 0) continue;

                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(candidate.Value).LengthSquared()
                    <= RepairGravitySquaredEpsilon)
                {
                    targetPos = candidate.Value;
                    found = true;
                    break;
                }
            }
            if (!found)
                return MyContractCreationResults.Fail_Common;

            string prefabName = (prefabNames != null && prefabNames.Count > 0)
                ? prefabNames[MyRandom.Instance.Next(0, prefabNames.Count)]
                : null;
            if (string.IsNullOrEmpty(prefabName))
                return MyContractCreationResults.Fail_Common;

            int gridPrice;
            if (!calculator.TryGetPrefabMinimalRepairPrice(prefabName, out gridPrice))
            {
                calculator.CalculatePrefabInformation(new[] { prefabName });
                if (!calculator.TryGetPrefabMinimalRepairPrice(prefabName, out gridPrice))
                    return MyContractCreationResults.Fail_Common;
            }

            int totalPcu;
            if (calculator.TryGetPrefabTotalPcu(prefabName, out totalPcu)
                && MySession.Static.NPCBlockLimits.Pirate.PCU < totalPcu)
                return MyContractCreationResults.Fail_Common;

            double gridDistance = (targetPos - factionStation.Position).Length();
            int repairComponentTimeInS = timeToPriceDenom != 0f ? (int)(gridPrice / timeToPriceDenom) : 0;
            long remainingTimeInS = (long)(m_contractTypeDefinition.DurationMultiplier
                * (durationBaseTime + gridDistance * durationPerMeter + repairComponentTimeInS));

            double urgency = ComputeReinforceUrgency(nodeId, tuning);
            long rewardMoney = ContractEconomy.ComputePremiumReward(tuning.ReinforceCost, urgency, tuning.ContractRewardPremiumMax);

            long contractId = MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.CONTRACT);
            MyObjectBuilder_ContractRepair contractOb =
                CreateCommonOb<MyObjectBuilder_ContractRepair>(
                    m_contractTypeDefinition, contractId, factionId, factionStation, rewardMoney);
            contractOb.GridPosition         = targetPos;
            contractOb.GridId               = 0L;
            contractOb.PrefabName           = prefabName;
            contractOb.BlocksToRepair       = new MySerializableList<Vector3I>();
            contractOb.KeepGridAtTheEnd     = false;
            contractOb.UnrepairedBlockCount = 0;
            contractOb.RemainingTimeInS     = remainingTimeInS;
            contractOb.ContractTypeDefinitionId = m_contractTypeDefinition.Id;

            var result = new MyContractRepair();
            result.Init(contractOb);
            contract = result;

            TrackHookedRepairContract(contractId, faction.Tag, nodeId, tuning.ReinforceCost);
            contract.ContractChangedState = (Action<long, MyContractStateEnum, MyContractStateEnum>)
                Delegate.Combine(contract.ContractChangedState,
                    new Action<long, MyContractStateEnum, MyContractStateEnum>(OnRepairContractStateChanged));

            if (DebugTraces.Contracts)
            {
                Log.Info("Contract generated [Reinforce-via-Repair] station=" + stationId + " node=" + nodeId
                    + " distance=" + gridDistance.ToString("0") + " urgency=" + urgency.ToString("0.###")
                    + " reward=" + rewardMoney);
            }

            return MyContractCreationResults.Success;
        }

        /// <summary>
        /// Guard-via-PvEBounty: overrides vanilla's MyContractTypePvEBountyStrategy target selection
        /// entirely. Per docs/contracts.md: the raid must proceed exactly as if this contract didn't
        /// exist unless a player both accepts it AND physically destroys the fleet -- generation and
        /// even acceptance alone have zero effect on the raid. <see cref="FactionRuntime.
        /// TryFindIncomingThreat"/> is a pure lookup (no gate on <c>MaterialisedEntityId</c>, no side
        /// effect on the fleet) so the contract can be offered while the raid is still in abstract
        /// transit. The player accepts, then physically finds the fleet; when they get close, the
        /// EXISTING Cornerstone II observation loop (<see cref="FactionRuntime.UpdateObservation"/>)
        /// materialises it exactly as it would for any other raid -- no early/forced spawn, since
        /// abstract transit runs at thousands of km/min while a real grid flies at ~100 m/s and could
        /// never catch up to (or keep pace with) the abstract position if spawned early.
        ///
        /// <para>Because no real grid may exist yet, <c>Targets</c> is left empty and
        /// <c>TargetPosition</c> is a one-time snapshot of the fleet's current interpolated position
        /// (a live-updating marker is a later slice). Vanilla's own spawn-on-approach and AI-block
        /// "was it killed" detection both assume a grid exists to point at, so both are suppressed by
        /// reflectively flipping the plain <see cref="MyContractPvEBounty"/> instance's own private/
        /// protected fields (<c>m_spawned</c>/<c>m_targetKilled</c>) rather than subclassing it --
        /// <see cref="MyContract.GetObjectBuilder"/>'s <c>MyContractFactory.CreateObjectBuilder</c>
        /// call resolves the object-builder type by the contract's exact concrete .NET type, scanned
        /// only from Sandbox.Game/SE mod assemblies, never Torch plugin assemblies; a genuine subclass
        /// living in this assembly was confirmed live (2026-08-07) to NullReferenceException on every
        /// autosave once created, since the factory can't find an entry for it.</para>
        ///
        /// <para>Tracking is keyed by acceptance, not generation: <see cref="OnBountyAccepted"/> (wired
        /// to <c>MyContract.OnContractAcquired</c> below) is the first point anything gets recorded.
        /// Completion is driven entirely by the raid's own existing resolution lifecycle rather than
        /// any new pause/timeout: <see cref="FactionRuntime.OnPvEBountyTargetDestroyed"/> fires from the
        /// pre-existing FLEET_DESTROYED detection for *any* destroyed grid (success, if this one was
        /// tracked); <see cref="FactionRuntime.OnFleetResolved"/> fires from the pre-existing
        /// `ResolveArrivals` cleanup pass for *any* fleet removed for *any* reason (failure, if this one
        /// was tracked and it wasn't the destroy-path that just handled it) -- see
        /// <see cref="HandleBountyTargetDestroyed"/>/<see cref="HandleFleetResolved"/>.</para>
        /// </summary>
        private MyContractCreationResults GenerateGuardBountyContract(
            out MyContract contract, MyFaction faction, string nodeId, long factionId, long stationId,
            MyMinimalPriceCalculator calculator, MyTimeSpan now)
        {
            contract = null;
            FactionTuning tuning = _world.Tuning;

            PendingFleetAction threat;
            if (!_runtime.TryFindIncomingThreat(nodeId, out threat))
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            // Mirrors StationHasActiveRepairContract's dedup reasoning: vanilla's own per-slot retry
            // loop would otherwise regenerate another bounty against the same threat on every open
            // board slot in one pass.
            if (StationHasActivePvEBountyContract(stationId))
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            MyFaction raiderFaction = FindFactionByTag(threat.ActorTag);
            if (raiderFaction == null)
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            IMyFactionStation factionStation = faction.GetStationById(stationId);
            if (factionStation == null) return MyContractCreationResults.Error;

            // Abstract interpolated position, in km -- a one-time snapshot. May already be stale by
            // the time the player looks at the board (abstract transit is fast); acceptable for now,
            // a live-updating GPS marker is a deferred follow-up (docs/contracts.md).
            Vec3 posKm = threat.CurrentPosKm(_world.UtcNow, tuning);
            Vector3D targetPos = new Vector3D(posKm.X * 1000.0, posKm.Y * 1000.0, posKm.Z * 1000.0);

            double urgency = ComputeReinforceUrgency(nodeId, tuning);
            long rewardMoney = ContractEconomy.ComputePremiumReward(tuning.GuardCost, urgency, tuning.ContractRewardPremiumMax);

            long contractId = MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.CONTRACT);
            MyObjectBuilder_PvEBountyContract contractOb =
                CreateCommonOb<MyObjectBuilder_PvEBountyContract>(
                    m_contractTypeDefinition, contractId, factionId, factionStation, rewardMoney);
            contractOb.TargetFactionId          = raiderFaction.FactionId;
            contractOb.TargetPosition           = targetPos;
            contractOb.TargetDistance           = Vector3D.Distance(factionStation.Position, targetPos);
            contractOb.Targets                  = null; // no real grid at generation time -- see method doc above
            contractOb.RewardModifier           = 1f;
            contractOb.PrefabRewardMultiplier   = 1f;
            // Vanilla's own contract lifecycle owns cleanup of an abandoned/unaccepted offer -- no
            // Core-side pause or timeout is involved on our side (docs/contracts.md non-goal 6).
            contractOb.RemainingTimeInS         = tuning.PvEBountyTimeout.TotalSeconds;
            contractOb.ContractTypeDefinitionId = m_contractTypeDefinition.Id;

            var result = new MyContractPvEBounty();
            result.Init(contractOb);
            // Suppress vanilla's own spawn-on-approach: with Targets empty, MyContractWithSpawnableGrid.
            // Update() would otherwise try to Spawn() one of vanilla's random spawn-group ships once a
            // player nears the (static, possibly stale) TargetPosition snapshot -- and TargetPrefab was
            // never set, so that Spawn() would hit a null prefab name. m_spawned is a plain instance
            // field (no property/setter), so this needs reflection rather than a subclass override --
            // see the method doc above for why subclassing broke autosave.
            SetFieldValue(result, "m_spawned", true);
            contract = result;

            // Acceptance, not generation, is the first point any tracking is created -- generating (or
            // even a player merely holding) the contract must have zero effect on the raid.
            contract.OnContractAcquired = (Action<long>)Delegate.Combine(contract.OnContractAcquired,
                new Action<long>(_ => OnBountyAccepted(contractId, threat.FleetId, faction.Tag, tuning.GuardCost)));
            contract.ContractChangedState = (Action<long, MyContractStateEnum, MyContractStateEnum>)
                Delegate.Combine(contract.ContractChangedState,
                    new Action<long, MyContractStateEnum, MyContractStateEnum>(OnBountyContractStateChanged));

            if (DebugTraces.Contracts)
            {
                Log.Info("Contract generated [Guard-via-PvEBounty] station=" + stationId + " node=" + nodeId
                    + " raider=" + threat.ActorTag + " fleet=" + threat.FleetId
                    + " urgency=" + urgency.ToString("0.###") + " reward=" + rewardMoney);
            }

            return MyContractCreationResults.Success;
        }

        /// <summary>Haul (vanilla's "Courier"/Hauling flavor): generated at a SOURCE station that
        /// currently holds real surplus of something another owned node needs -- the mirror image of
        /// the Acquisition path above, which generates at the NEED node instead. Template borrowed
        /// from Acquisition's own <see cref="MyObjectBuilder_ContractObtainAndDeliver"/> build (same
        /// object/condition types -- <see cref="CollectAlreadyOfferedItems"/>'s dedup already keys off
        /// <c>MyContractObtainAndDeliver.GetItemId()</c> regardless of which strategy generated it, so
        /// no separate dedup guard is needed), but NOT constrained to Acquisition's own field values:
        /// <c>StationEndId</c>/<c>FactionEndId</c> point at the vector's <c>NeedNodeId</c>'s station
        /// (the drop-off), not this generating station, and reward/duration are Haul-specific
        /// (distance-scaled reward on top of the same item-value term, time-constrained unlike
        /// Acquisition's open-ended <c>RemainingTimeInS = null</c>). Distance/transit-time use the
        /// same abstract <see cref="SpatialNode.Position"/> metric the real NPC Haul convoy's own
        /// economics already run on (<see cref="OperationsManager"/>'s <c>DispatchHaul</c>), not real
        /// SE world positions -- the two are unrelated coordinate spaces. See
        /// docs/material-vectors.md's Slice C.</summary>
        private MyContractCreationResults GenerateHaulContract(
            out MyContract contract, MyFaction faction, string nodeId, long factionId, long stationId,
            MyMinimalPriceCalculator calculator, MyTimeSpan now)
        {
            contract = null;
            FactionTuning tuning = _world.Tuning;

            MaterialVector vector;
            if (!_runtime.TryMatchHaulDemand(faction.Tag, nodeId, out vector))
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            // vector.ItemKey is the prefixed invKey (e.g. "Comp:Explosives") -- same two-step split
            // Acquisition's own candidate loop above applies: the prefixed form for price/scarcity
            // lookups, the bare SubTypeId for the SE item definition and dedup.
            string invKey = vector.ItemKey;
            string subTypeId = StripInvKeyPrefix(invKey);
            MyPhysicalItemDefinition itemDef = FindItemDefinition(subTypeId);
            if (itemDef == null)
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            HashSet<MyDefinitionId> alreadyOffered = CollectAlreadyOfferedItems(stationId);
            if (alreadyOffered.Contains(itemDef.Id))
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            SpatialNode sourceNode, destNode;
            if (_world?.Graph == null
                || !_world.Graph.Nodes.TryGetValue(nodeId, out sourceNode)
                || !_world.Graph.Nodes.TryGetValue(vector.NeedNodeId, out destNode)
                || destNode.StableStationId == 0)
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            IMyFactionStation destStation = faction.GetStationById(destNode.StableStationId);
            if (destStation == null)
                return _vanilla.GenerateContract(out contract, factionId, stationId, calculator, now);

            IMyFactionStation sourceStation = faction.GetStationById(stationId);
            if (sourceStation == null) return MyContractCreationResults.Error;

            double distanceKm = sourceNode.Position.DistanceTo(destNode.Position);

            long basePrice = _prices.BasePrice(invKey);
            if (basePrice <= 0)
            {
                int minPrice;
                calculator.CalculateMinimalPrices(new SerializableDefinitionId[] { itemDef.Id });
                calculator.TryGetItemMinimalPrice(itemDef.Id, out minPrice);
                basePrice = minPrice;
            }
            int amount = (int)Math.Min(Math.Max(vector.Quantity, 1L), int.MaxValue);
            double scarcity = QueryRowScarcity(vector.NeedNodeId, invKey, amount);

            // Same basePrice x scarcity x amount term Acquisition uses, plus a flat per-km premium --
            // Haul pays more the farther the cargo has to travel, unlike Acquisition's sourced-from-
            // anywhere shape.
            // Same affordability bound as Acquisition (FAC-156). The per-km premium is charged
            // against the budget too but does not scale the quantity: it is a flat travel payment,
            // not a price for the goods, so trimming the load would not reduce it.
            long haulUnitReward = (long)Math.Round(basePrice * scarcity);
            long haulBudget = ContractRewardSliceFor(faction.Tag);
            long kmPremium = (long)(distanceKm * tuning.HaulContractRewardPerKm);
            int haulAffordable = ContractEconomy.AffordableContractQuantity(
                amount, haulUnitReward, haulBudget > kmPremium ? haulBudget - kmPremium : 0);
            if (haulAffordable <= 0)
            {
                if (DebugTraces.Contracts)
                {
                    Log.Info("Contract skipped [Haul] faction=" + faction.Tag + " node=" + nodeId
                        + " item=" + subTypeId + " qty=" + amount + " unitReward=" + haulUnitReward
                        + " kmPremium=" + kmPremium + " budget=" + haulBudget + " reason=unaffordable");
                }
                return MyContractCreationResults.Fail_Common;
            }
            amount = haulAffordable;

            long rewardMoney = (haulUnitReward * amount / 1000) * 1000;
            rewardMoney += kmPremium;
            rewardMoney = Math.Max(rewardMoney, m_contractTypeDefinition.MinimumMoney);
            s_contractBudget.Record(faction.Tag, _world.UtcNow, rewardMoney, tuning.SpendBudgetWindowSeconds);

            // Reuses the same TransitTimeMins formula the real NPC Haul convoy runs on, scaled up
            // (a player isn't as direct as an abstract convoy) and floored so a short hop still gives
            // reasonable time. Vanilla's own contract lifecycle owns cleanup on expiry -- no Core-side
            // pause/timeout involved (same reasoning as PvEBountyTimeout above).
            long remainingTimeInS = (long)Math.Max(
                tuning.HaulContractMinTimeoutMinutes * 60,
                tuning.TransitTimeMins(_world != null
                    ? _world.ZonesFor(sourceNode.Position, destNode.Position)
                    : LegZoneProfile.PlainDeepSpace(distanceKm))
                * 60 * tuning.HaulContractTimeMultiplier);

            long contractId  = MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.CONTRACT);
            long conditionId = MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.CONTRACT_CONDITION);

            var conditionOb = new MyObjectBuilder_ContractConditionDeliverItems
            {
                Id            = conditionId,
                ContractId    = contractId,
                FactionEndId  = factionId,
                StationEndId  = destNode.StableStationId,
                BlockEndId    = 0L,
                SubId         = 0,
                IsFinished    = false,
                TransferItems = false,
                ItemType      = itemDef.Id,
                ItemAmount    = amount,
                ItemVolume    = itemDef.Volume * amount,
            };

            MyObjectBuilder_ContractObtainAndDeliver contractOb =
                CreateCommonOb<MyObjectBuilder_ContractObtainAndDeliver>(
                    m_contractTypeDefinition, contractId, factionId, sourceStation, rewardMoney);
            contractOb.RemainingTimeInS         = remainingTimeInS;
            contractOb.ContractCondition        = conditionOb;
            contractOb.ContractTypeDefinitionId = m_contractTypeDefinition.Id;

            var result = new MyContractObtainAndDeliver();
            result.Init(contractOb);
            contract = result;

            contract.OnContractAcquired = (Action<long>)Delegate.Combine(contract.OnContractAcquired,
                new Action<long>(_ => OnHaulVectorContractAccepted(contractId, faction.Tag, vector.NeedNodeId, invKey, amount, "Haul")));
            contract.ContractChangedState = (Action<long, MyContractStateEnum, MyContractStateEnum>)
                Delegate.Combine(contract.ContractChangedState,
                    new Action<long, MyContractStateEnum, MyContractStateEnum>(OnHaulVectorContractStateChanged));
            RegisterVectorContract(faction.Tag, vector.NeedNodeId, invKey, contractId);

            if (DebugTraces.Contracts)
            {
                Log.Info("Contract generated [Haul] station=" + stationId + " node=" + nodeId
                    + " -> need=" + vector.NeedNodeId + " item=" + subTypeId + " (" + invKey + ") qty=" + amount
                    + " distanceKm=" + distanceKm.ToString("0.0") + " reward=" + rewardMoney
                    + " timeoutMin=" + (remainingTimeInS / 60.0).ToString("0.0"));
            }

            return MyContractCreationResults.Success;
        }

        // ---- Slice C: Acquisition/Haul vector-contract tracking ---------------------------------
        //
        // Acquisition ("bring X here", generated at the need node) and Haul ("take X there",
        // generated at a source node with real surplus) are two views of the same material vector
        // once one exists for a given (NeedNodeId, ItemKey) pair -- see docs/material-vectors.md's
        // Slice C. Tracking begins at ACCEPTANCE, not generation -- same lesson Guard-via-PvEBounty's
        // three implementation passes already established (see that section's own history note): a
        // merely-offered, never-accepted contract must have zero footprint. A generated-but-unaccepted
        // sibling is simply left to vanilla's own board-refresh lifecycle; only an ACCEPTED contract
        // invalidates its sibling, and nothing here ever re-checks DemandRegister -- a contract is
        // never auto-cancelled just because the underlying demand entry later disappears for an
        // unrelated reason (e.g. Trade happens to satisfy it first).

        private struct HookedHaulVectorContract
        {
            public string FactionTag;
            public string NodeId;    // NeedNodeId/DeliverNodeId -- the claim key, shared by both flavors
            public string ItemKey;
            public long   Quantity;
            public string Label;     // "Acquisition" or "Haul" -- logging only
        }

        // Keyed by the vector's identity (faction, need node, item) -- both flavors register
        // themselves here at successful GENERATION (not acceptance), purely so an acceptance on
        // either side can find its sibling. Last generation wins (a regenerated board offer naturally
        // overwrites a stale id); acceptance-time lookups always re-validate liveness via
        // GetContractById before acting, so an overwritten or expired stale entry is harmless.
        private static readonly Dictionary<(string FactionTag, string NodeId, string ItemKey), long>
            s_activeVectorContract = new Dictionary<(string, string, string), long>();

        // Tracking begins at ACCEPTANCE -- see section doc above.
        /// <summary>Windowed reward budget shared by every station's contract generation, keyed by
        /// faction tag inside <see cref="SpendBudget"/> itself. Static because strategies are
        /// injected per faction-station and a per-instance budget would let each station promise the
        /// faction's whole capacity independently.</summary>
        private static readonly SpendBudget s_contractBudget = new SpendBudget();

        /// <summary>Credits this faction may still promise across contract rewards this window.
        /// <see cref="StationListings.Uncapped"/> when the faction state cannot be reached, which is
        /// the pre-FAC-156 behaviour rather than a silent zero that would stop all contracts.</summary>
        private long ContractRewardBudgetFor(string factionTag)
        {
            FactionTuning tuning = _world != null ? _world.Tuning : null;
            if (tuning == null || string.IsNullOrEmpty(factionTag)) return long.MaxValue;

            FactionState st = _runtime != null ? _runtime.FindFaction(factionTag) : null;
            if (st == null) return long.MaxValue;

            long surplus = tuning.ContractRewardWealthFractionPerWindow > 0
                ? Economy.SpendableTreasurySurplus(
                    _world.GetTreasury(factionTag), _world.GetNetFlow(factionTag), tuning)
                : 0;

            // SE asks for one contract at a time, so there is no batch here to allocate across the
            // way StationListings can -- the share cap is the part of FAC-160's fix that transfers,
            // and it is what stops the first contract of a tick taking the whole window.
            return s_contractBudget.RemainingBudget(
                factionTag, _world.UtcNow, st.CentralBankRecentIncome,
                tuning.ContractRewardMaxIncomeFractionPerWindow, tuning.ContractRewardBudgetFloor,
                tuning.SpendBudgetWindowSeconds,
                surplus, tuning.ContractRewardWealthFractionPerWindow);
        }

        /// <summary>The per-contract slice of a window, so one offer cannot consume the faction's
        /// whole capacity and refuse every later one — live 2026-08-29 every skipped contract read
        /// `budget=0` for exactly that reason (FAC-160).</summary>
        private long ContractRewardSliceFor(string factionTag)
        {
            long budget = ContractRewardBudgetFor(factionTag);
            if (budget == long.MaxValue) return budget;
            double share = _world != null && _world.Tuning != null ? _world.Tuning.BudgetMaxSharePerRow : 1.0;
            if (share <= 0 || share >= 1.0) return budget;
            long slice = (long)(budget * share);
            return slice > 0 ? slice : budget;
        }

        private static readonly Dictionary<long, HookedHaulVectorContract> s_hookedHaulVectorContracts =
            new Dictionary<long, HookedHaulVectorContract>();

        private static void RegisterVectorContract(string factionTag, string nodeId, string itemKey, long contractId)
        {
            s_activeVectorContract[(factionTag, nodeId, itemKey)] = contractId;
        }

        /// <summary>Wired to <c>MyContract.OnContractAcquired</c> by both the Acquisition and Haul
        /// generation paths above -- the first point any tracking is created. Claims the (node, item)
        /// pair (see <see cref="FactionRuntime.ClaimHaulTarget"/>) and, if a sibling contract for the
        /// same vector is currently tracked AND still just a bare, unaccepted board offer, invalidates
        /// it -- accepting either flavor covers the same underlying demand, so completing both would
        /// over-deliver.
        ///
        /// <para>Deliberately does NOT invalidate a sibling that has ALSO already been accepted
        /// (checked via <see cref="s_hookedHaulVectorContracts"/>, not just <see
        /// cref="s_activeVectorContract"/>): two players -- or the same player -- grabbing both flavors
        /// in the same race window is rare and, left alone, costs at worst a harmless double-delivery
        /// (same "not worth engineering around" call this page already makes for Trade spoiling a
        /// demand elsewhere). Failing to guard this would be actively worse than doing nothing: without
        /// it, whichever acceptance is processed second sees the first one now sitting in <see
        /// cref="s_activeVectorContract"/> and fails IT too, so a player who legitimately double-
        /// accepted loses both contracts instead of completing either.</para></summary>
        private void OnHaulVectorContractAccepted(long contractId, string factionTag, string nodeId,
            string itemKey, long quantity, string label)
        {
            s_hookedHaulVectorContracts[contractId] = new HookedHaulVectorContract
            {
                FactionTag = factionTag,
                NodeId     = nodeId,
                ItemKey    = itemKey,
                Quantity   = quantity,
                Label      = label,
            };
            _runtime.ClaimHaulTarget(factionTag, nodeId, itemKey);

            var key = (factionTag, nodeId, itemKey);
            long siblingId;
            if (s_activeVectorContract.TryGetValue(key, out siblingId) && siblingId != contractId
                && !s_hookedHaulVectorContracts.ContainsKey(siblingId))
            {
                MySessionComponentContractSystem contractSystem =
                    MySession.Static?.GetComponent<MySessionComponentContractSystem>();
                MyContract sibling = contractSystem?.GetContractById(siblingId) as MyContract;
                if (sibling != null)
                {
                    // Fail() is assumed safe to call on an already-terminal contract (mirrors
                    // HandleFleetResolved's identical unguarded use below) -- no local state check.
                    sibling.Fail();
                    Log.Info("Contract invalidated [vector already claimed by " + label + "] faction=" + factionTag
                        + " node=" + nodeId + " item=" + itemKey + " sibling=" + siblingId + " by=" + contractId);
                }
            }
            s_activeVectorContract[key] = contractId;
        }

        /// <summary>Wired to <c>MyContract.ContractChangedState</c> by both the Acquisition and Haul
        /// generation paths above. On <c>Finished</c>, credits <paramref name="hooked"/>'s node
        /// inventory exactly as a real Haul arrival would (see <see
        /// cref="OperationsManager.RealizeBootstrapInstant"/>/<c>FleetResolution</c>'s own
        /// <c>ActionKind.Haul</c> credit) -- the actual completion feedback loop this slice adds.</summary>
        private void OnHaulVectorContractStateChanged(long contractId, MyContractStateEnum oldState, MyContractStateEnum newState)
        {
            HookedHaulVectorContract hooked;
            if (!s_hookedHaulVectorContracts.TryGetValue(contractId, out hooked)) return;

            if (newState == MyContractStateEnum.Finished)
            {
                _world.AddNodeInventory(hooked.NodeId, hooked.ItemKey, hooked.Quantity);
                Log.Info("Contract completed [" + hooked.Label + "] faction=" + hooked.FactionTag
                    + " node=" + hooked.NodeId + " item=" + hooked.ItemKey + " qty=" + hooked.Quantity);
            }

            if (newState == MyContractStateEnum.Finished || newState == MyContractStateEnum.Failed
                || newState == MyContractStateEnum.ToBeDisposed)
            {
                _runtime.ReleaseHaulTarget(hooked.FactionTag, hooked.NodeId, hooked.ItemKey);
                s_hookedHaulVectorContracts.Remove(contractId);
                MySessionComponentContractSystem contractSystem =
                    MySession.Static?.GetComponent<MySessionComponentContractSystem>();
                MyContract liveContract = contractSystem?.GetContractById(contractId) as MyContract;
                if (liveContract != null)
                {
                    liveContract.ContractChangedState = (Action<long, MyContractStateEnum, MyContractStateEnum>)
                        Delegate.Remove(liveContract.ContractChangedState,
                            new Action<long, MyContractStateEnum, MyContractStateEnum>(OnHaulVectorContractStateChanged));
                }
            }
        }

        // ---- Guard-via-PvEBounty completion tracking --------------------------------------------

        private struct HookedBountyContract
        {
            public string FactionTag;
            public string FleetId;
            public long   ActionCost;
        }

        // Tracking begins at ACCEPTANCE (see OnBountyAccepted), never at generation -- a generated or
        // merely-offered contract must have zero footprint here. Two indices: contractId is what
        // OnBountyContractStateChanged/GetContractById need; FleetId is what the raid's own resolution
        // lifecycle (destroy/resolve) needs to look a bounty up by.
        private static readonly Dictionary<long, HookedBountyContract> s_hookedBountyContracts =
            new Dictionary<long, HookedBountyContract>();
        private static readonly Dictionary<string, long> s_acceptedContractIdByFleetId =
            new Dictionary<string, long>();

        /// <summary>Wired to <c>MyContract.OnContractAcquired</c> in <see
        /// cref="GenerateGuardBountyContract"/> -- the first point any tracking is created for a
        /// Guard-via-PvEBounty contract. Generation and a player merely holding an unaccepted offer
        /// must never reach here.</summary>
        private static void OnBountyAccepted(long contractId, string fleetId, string factionTag, long actionCost)
        {
            s_hookedBountyContracts[contractId] = new HookedBountyContract
            {
                FactionTag = factionTag,
                FleetId    = fleetId,
                ActionCost = actionCost,
            };
            s_acceptedContractIdByFleetId[fleetId] = contractId;
        }

        /// <summary>Called via <see cref="FactionRuntime.OnPvEBountyTargetDestroyed"/> for *any*
        /// fleet whose materialised grid is confirmed destroyed -- most have no bounty at all, so a
        /// lookup miss is the common case and must return silently, not warn. When this fleet does
        /// have an accepted bounty (the actual success case), vanilla's own AI-block-based completion
        /// never fires (no Targets were ever set, see <see cref="GenerateGuardBountyContract"/>), so
        /// the private <c>m_targetKilled</c> field is set directly via reflection (not a subclass
        /// override -- see the method doc referenced above for why that broke autosave) before calling
        /// the normal, public <c>Finish()</c> -- the resulting Finished state change reaches <see
        /// cref="OnBountyContractStateChanged"/> exactly as any other contract's completion would.</summary>
        internal static void HandleBountyTargetDestroyed(string fleetId)
        {
            long contractId;
            if (!s_acceptedContractIdByFleetId.TryGetValue(fleetId, out contractId)) return;
            MySessionComponentContractSystem contractSystem =
                MySession.Static?.GetComponent<MySessionComponentContractSystem>();
            MyContractPvEBounty liveContract =
                contractSystem?.GetContractById(contractId) as MyContractPvEBounty;
            if (liveContract == null)
            {
                Log.Warn("HandleBountyTargetDestroyed: fleet " + fleetId + " tracked as accepted contract "
                    + contractId + " but the live contract is gone or the wrong type -- no-op.");
                return;
            }
            SetFieldValue(liveContract, "m_targetKilled", true);
            liveContract.Finish();
        }

        /// <summary>Called via <see cref="FactionRuntime.OnFleetResolved"/> for *every* fleet removed
        /// from <c>PendingFleets</c>, win or lose -- most have no bounty at all, so a lookup miss is
        /// the common case and must return silently. When this fleet does have an accepted bounty, its
        /// being removed here (rather than via the destroy-path above, which would already have
        /// cleared this same tracking entry first) means it resolved some other way -- reached its
        /// target, or was intercepted by an NPC Guard/station -- so the bounty simply fails. No
        /// ordering hazard with the destroy path: that runs on a faster, independent cadence
        /// (<c>UpdateObservation</c> vs. a sim tick) and always clears tracking before this fleet's
        /// natural cleanup-pass removal could reach here.</summary>
        internal static void HandleFleetResolved(string fleetId)
        {
            long contractId;
            if (!s_acceptedContractIdByFleetId.TryGetValue(fleetId, out contractId)) return;
            MySessionComponentContractSystem contractSystem =
                MySession.Static?.GetComponent<MySessionComponentContractSystem>();
            MyContract liveContract = contractSystem?.GetContractById(contractId) as MyContract;
            if (liveContract == null)
            {
                Log.Warn("HandleFleetResolved: fleet " + fleetId + " tracked as accepted contract "
                    + contractId + " but the live contract is gone -- no-op.");
                return;
            }
            liveContract.Fail();
        }

        private void OnBountyContractStateChanged(long contractId, MyContractStateEnum oldState, MyContractStateEnum newState)
        {
            HookedBountyContract hooked;
            if (!s_hookedBountyContracts.TryGetValue(contractId, out hooked)) return;

            if (newState == MyContractStateEnum.Finished)
            {
                // Mirrors what an automated Guard dispatch would have cost the faction's own private
                // ledger -- SE's contract system separately pays the player's real reward out of the
                // faction's SE credits, this is our sim-side bookkeeping on top of that. Nothing else
                // needed on the Core side: the FLEET_DESTROYED handling that led here already zeroed
                // the raid's effect (ForcedRatio = 0.0) regardless of any bounty.
                _world.Spend(hooked.FactionTag, hooked.ActionCost);
                Log.Info("Contract completed [Guard-via-PvEBounty] faction=" + hooked.FactionTag
                    + " fleet=" + hooked.FleetId + " cost=" + hooked.ActionCost);
            }

            if (newState == MyContractStateEnum.Finished || newState == MyContractStateEnum.Failed
                || newState == MyContractStateEnum.ToBeDisposed)
            {
                s_hookedBountyContracts.Remove(contractId);
                s_acceptedContractIdByFleetId.Remove(hooked.FleetId);
                MySessionComponentContractSystem contractSystem =
                    MySession.Static?.GetComponent<MySessionComponentContractSystem>();
                MyContract liveContract = contractSystem?.GetContractById(contractId) as MyContract;
                if (liveContract != null)
                {
                    liveContract.ContractChangedState = (Action<long, MyContractStateEnum, MyContractStateEnum>)
                        Delegate.Remove(liveContract.ContractChangedState,
                            new Action<long, MyContractStateEnum, MyContractStateEnum>(OnBountyContractStateChanged));
                }
            }
        }

        private static bool StationHasActivePvEBountyContract(long stationId)
        {
            MySessionComponentContractSystem contractSystem =
                MySession.Static?.GetComponent<MySessionComponentContractSystem>();
            if (contractSystem == null) return false;
            try
            {
                foreach (IMyContract existing in contractSystem.GetAvailableContractsForFactionStation(stationId))
                    if (existing is MyContractPvEBounty) return true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "StationHasActivePvEBountyContract failed for station " + stationId + ".");
            }
            return false;
        }

        private static MyFaction FindFactionByTag(string tag)
        {
            if (MySession.Static?.Factions == null) return null;
            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
                if (string.Equals(kv.Value.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        /// <summary>0 for an already-healthy node, up to 1 as hold approaches 0 -- how urgently this
        /// faction needs the node reinforced, feeding the contract reward's urgency premium.</summary>
        private double ComputeReinforceUrgency(string nodeId, FactionTuning tuning)
        {
            SpatialNode node;
            if (_world?.Graph == null || !_world.Graph.Nodes.TryGetValue(nodeId, out node)) return 0.0;
            if (tuning.HoldMaintenanceThreshold <= 0) return 0.0;
            double urgency = (tuning.HoldMaintenanceThreshold - node.HoldStrength) / tuning.HoldMaintenanceThreshold;
            return Math.Max(0.0, Math.Min(1.0, urgency));
        }

        // ---- Reinforce-via-Repair completion tracking ------------------------------------------

        private struct HookedRepairContract
        {
            public string FactionTag;
            public string NodeId;
            public long   ActionCost;
        }

        private static readonly Dictionary<long, HookedRepairContract> s_hookedRepairContracts =
            new Dictionary<long, HookedRepairContract>();

        private void TrackHookedRepairContract(long contractId, string factionTag, string nodeId, long actionCost)
        {
            s_hookedRepairContracts[contractId] = new HookedRepairContract
            {
                FactionTag = factionTag,
                NodeId     = nodeId,
                ActionCost = actionCost,
            };
        }

        /// <summary>Queried by <see cref="ContractRepairGpsPatch"/> to decide whether to append the
        /// reinforcement note to a Repair contract's GPS description.</summary>
        internal static bool IsHookedRepairContract(long contractId)
        {
            return s_hookedRepairContracts.ContainsKey(contractId);
        }

        private void OnRepairContractStateChanged(long contractId, MyContractStateEnum oldState, MyContractStateEnum newState)
        {
            HookedRepairContract hooked;
            if (!s_hookedRepairContracts.TryGetValue(contractId, out hooked)) return;

            if (newState == MyContractStateEnum.Finished)
            {
                _world.Spend(hooked.FactionTag, hooked.ActionCost);
                _world.Reinforce(hooked.FactionTag, hooked.NodeId);
                Log.Info("Contract completed [Reinforce-via-Repair] faction=" + hooked.FactionTag
                    + " node=" + hooked.NodeId + " cost=" + hooked.ActionCost);
            }

            if (newState == MyContractStateEnum.Finished || newState == MyContractStateEnum.Failed
                || newState == MyContractStateEnum.ToBeDisposed)
            {
                s_hookedRepairContracts.Remove(contractId);
                MySessionComponentContractSystem contractSystem =
                    MySession.Static?.GetComponent<MySessionComponentContractSystem>();
                // GetActiveContractById is internal to Sandbox.Game; GetContractById is the public
                // equivalent (checks both active and inactive collections).
                MyContract liveContract = contractSystem?.GetContractById(contractId) as MyContract;
                if (liveContract != null)
                {
                    liveContract.ContractChangedState = (Action<long, MyContractStateEnum, MyContractStateEnum>)
                        Delegate.Remove(liveContract.ContractChangedState,
                            new Action<long, MyContractStateEnum, MyContractStateEnum>(OnRepairContractStateChanged));
                }
            }
        }

        private static T GetFieldValue<T>(object obj, string fieldName, T fallback)
        {
            if (obj == null) return fallback;
            FieldInfo fi = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi == null) return fallback;
            try { return (T)fi.GetValue(obj); } catch { return fallback; }
        }

        private static void SetFieldValue(object obj, string fieldName, object value)
        {
            if (obj == null) return;
            FieldInfo fi = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi == null)
            {
                Log.Warn("SetFieldValue: field '" + fieldName + "' not found on " + obj.GetType().FullName + ".");
                return;
            }
            try { fi.SetValue(obj, value); }
            catch (Exception ex) { Log.Warn(ex, "SetFieldValue: failed to set '" + fieldName + "' on " + obj.GetType().FullName + "."); }
        }

        protected override int GetRewardRep(MyContractTypeDefinition def)
        {
            return def.MinimumReputation;
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static string StripInvKeyPrefix(string invKey)
        {
            if (invKey.StartsWith("Ore:", StringComparison.Ordinal))   return invKey.Substring(4);
            if (invKey.StartsWith("Ingot:", StringComparison.Ordinal)) return invKey.Substring(6);
            if (invKey.StartsWith("Comp:", StringComparison.Ordinal))  return invKey.Substring(5);
            return invKey;
        }

        private static bool ContainsSubtype(List<SerializableDefinitionId> items, string subTypeId)
        {
            foreach (SerializableDefinitionId id in items)
                if (string.Equals(id.SubtypeName, subTypeId, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Session-lifetime cache -- SE physical item definitions never change after session load, and
        // this is shared across every FactionContractStrategy instance (one per wrapped contract-type
        // definition). Previously a full linear scan of every SE physical item definition per demand
        // candidate examined; live-confirmed as a real freeze source (55,630 GenerateContract calls
        // logged since the last restart, 686 landing in a single ~9s window) once vanilla's own
        // per-station retry loop multiplied it across many stations refilling contracts at once.
        private static Dictionary<string, MyPhysicalItemDefinition> s_itemDefsBySubtype;

        private static MyPhysicalItemDefinition FindItemDefinition(string subTypeId)
        {
            if (MyDefinitionManager.Static == null) return null;
            if (s_itemDefsBySubtype == null)
            {
                var map = new Dictionary<string, MyPhysicalItemDefinition>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (MyPhysicalItemDefinition def in MyDefinitionManager.Static.GetPhysicalItemDefinitions())
                        if (def != null && !string.IsNullOrEmpty(def.Id.SubtypeName) && !map.ContainsKey(def.Id.SubtypeName))
                            map[def.Id.SubtypeName] = def;
                }
                catch { return null; }
                s_itemDefsBySubtype = map;
            }
            MyPhysicalItemDefinition found;
            return s_itemDefsBySubtype.TryGetValue(subTypeId, out found) ? found : null;
        }

        private static HashSet<MyDefinitionId> CollectAlreadyOfferedItems(long stationId)
        {
            var result = new HashSet<MyDefinitionId>();
            MySessionComponentContractSystem contractSystem =
                MySession.Static?.GetComponent<MySessionComponentContractSystem>();
            if (contractSystem == null) return result;
            try
            {
                foreach (IMyContract existing in contractSystem.GetAvailableContractsForFactionStation(stationId))
                {
                    MyDefinitionId? itemId = (existing as MyContractObtainAndDeliver)?.GetItemId();
                    if (itemId.HasValue) result.Add(itemId.Value);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "CollectAlreadyOfferedItems failed for station " + stationId + ".");
            }
            return result;
        }

        /// <summary>True if the station's board already lists a Repair contract (inactive/unaccepted
        /// counts -- <c>GetAvailableContractsForFactionStation</c> is the same board-state API
        /// <see cref="CollectAlreadyOfferedItems"/> uses for Acquisition's duplicate check). Every
        /// Repair contract at a Factions-managed station is one of ours, so this is a simple dedup:
        /// at most one Reinforce-via-Repair contract active per station at a time.</summary>
        private static bool StationHasActiveRepairContract(long stationId)
        {
            MySessionComponentContractSystem contractSystem =
                MySession.Static?.GetComponent<MySessionComponentContractSystem>();
            if (contractSystem == null) return false;
            try
            {
                foreach (IMyContract existing in contractSystem.GetAvailableContractsForFactionStation(stationId))
                    if (existing is MyContractRepair) return true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "StationHasActiveRepairContract failed for station " + stationId + ".");
            }
            return false;
        }

        // stationId -> nodeId index, shared across every FactionContractStrategy instance (they all
        // hold the same WorldModel). Previously a full linear scan of every node in the graph on
        // every single call -- see the freeze evidence noted on FindItemDefinition above. The node
        // graph does mutate at runtime (founding, claiming, conquest) unlike physical item defs, so
        // this is short-lived (StationIndexTtl) rather than session-lifetime, and a lookup miss
        // triggers one immediate rebuild-and-retry so a station founded moments ago isn't invisible
        // until the TTL expires. Ownership itself is still read live from the graph on every call
        // (not cached), so a station changing owner is reflected immediately regardless of TTL.
        private static readonly TimeSpan StationIndexTtl = TimeSpan.FromSeconds(5);
        private static Dictionary<long, string> s_stationToNode;
        private static DateTime s_stationToNodeBuiltUtc = DateTime.MinValue;

        private string FindNodeForStation(string factionTag, long stationId)
        {
            if (_world?.Graph == null) return null;

            if (s_stationToNode == null || DateTime.UtcNow - s_stationToNodeBuiltUtc > StationIndexTtl)
                RebuildStationIndex();

            string nodeId;
            if (!s_stationToNode.TryGetValue(stationId, out nodeId))
            {
                RebuildStationIndex();
                if (!s_stationToNode.TryGetValue(stationId, out nodeId)) return null;
            }

            SpatialNode node;
            if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return null;
            return string.Equals(node.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase) ? nodeId : null;
        }

        private void RebuildStationIndex()
        {
            var map = new Dictionary<long, string>();
            foreach (SpatialNode node in _world.Graph.Nodes.Values)
                if (node.StableStationId != 0)
                    map[node.StableStationId] = node.Id;
            s_stationToNode = map;
            s_stationToNodeBuiltUtc = DateTime.UtcNow;
        }

        // inventoryKey is the prefixed invKey (e.g. "Comp:Explosives"), matching every other
        // PriceOracle.ScarcityMultiplier call site (ProductionManager, TradeManager, WorldModel) —
        // it derives reference stock and current node inventory by that key, not the bare subtype.
        // A contract buys through the player's delivery, so it is priced as an order: at the midpoint
        // of the stock the delivery would leave (RowScarcity).
        private double QueryRowScarcity(string nodeId, string inventoryKey, long units)
        {
            if (_world?.PriceOracle == null) return 1.0;
            return RowScarcity.ForOrder(_world.PriceOracle, nodeId, inventoryKey,
                                        _world.GetNodeInventory(nodeId, inventoryKey), units);
        }

        // ---- strategy injection ----------------------------------------------------------------

        /// <summary>
        /// Replace entries in <c>MySessionComponentContractSystem.m_contractTypeStrategies</c>
        /// with <see cref="FactionContractStrategy"/> wrappers for Acquisition and Hauling types.
        /// Called at session load on the game thread.
        /// </summary>
        public static void InjectStrategies(
            HashSet<string>    registeredTags,
            DemandRegister     demands,
            IItemPriceCatalogue prices,
            WorldModel         world,
            FactionRuntime     runtime)
        {
            s_registeredTags = registeredTags;
            try
            {
                MySessionComponentContractSystem contractSystem =
                    MySession.Static?.GetComponent<MySessionComponentContractSystem>();
                if (contractSystem == null)
                {
                    Log.Warn("FactionContractStrategy.InjectStrategies: contract system not found.");
                    return;
                }

                FieldInfo field = typeof(MySessionComponentContractSystem).GetField(
                    "m_contractTypeStrategies",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null)
                {
                    Log.Warn("FactionContractStrategy.InjectStrategies: m_contractTypeStrategies field not found.");
                    return;
                }

                var strategies = field.GetValue(contractSystem)
                    as Dictionary<MyDefinitionId, MyContractTypeBaseStrategy>;
                if (strategies == null)
                {
                    Log.Warn("FactionContractStrategy.InjectStrategies: could not cast strategy dictionary.");
                    return;
                }

                // Get the economy definition from any existing strategy.
                MySessionComponentEconomyDefinition economyDef = null;
                foreach (MyContractTypeBaseStrategy s in strategies.Values)
                {
                    economyDef = GetProtectedField<MySessionComponentEconomyDefinition>(
                        s, "m_economyDefinition");
                    if (economyDef != null) break;
                }
                if (economyDef == null)
                {
                    Log.Warn("FactionContractStrategy.InjectStrategies: could not resolve economy definition.");
                    return;
                }

                int wrapped = 0;
                var keys = new List<MyDefinitionId>(strategies.Keys);
                foreach (MyDefinitionId key in keys)
                {
                    MyContractTypeBaseStrategy vanilla = strategies[key];
                    if (vanilla is FactionContractStrategy) continue; // already wrapped

                    MyContractTypeDefinition contractTypeDef =
                        GetProtectedField<MyContractTypeDefinition>(vanilla, "m_contractTypeDefinition");
                    if (contractTypeDef == null) continue;

                    // Only wrap Acquisition, Hauling, Repair, and PvEBounty strategies; leave others as vanilla.
                    if (!(vanilla is MyContractTypeAcquisitionStrategy)
                        && !(vanilla is MyContractTypeHaulingStrategy)
                        && !(vanilla is MyContractTypeRepairStrategy)
                        && !(vanilla is MyContractTypePvEBountyStrategy))
                        continue;

                    strategies[key] = new FactionContractStrategy(
                        economyDef, contractTypeDef, vanilla,
                        registeredTags, demands, prices, world, runtime);
                    wrapped++;
                }

                Log.Info(string.Format("FactionContractStrategy: wrapped {0} contract type strategy(ies).", wrapped));
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "FactionContractStrategy.InjectStrategies failed.");
            }
        }

        private static T GetProtectedField<T>(object obj, string fieldName) where T : class
        {
            if (obj == null) return null;
            FieldInfo fi = obj.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return fi?.GetValue(obj) as T;
        }
    }
}
