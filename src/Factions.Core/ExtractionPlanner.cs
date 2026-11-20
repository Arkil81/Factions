using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>Which limit stops a hub's catchment delivering what its extraction order asks for
    /// (docs/mining-plan.md, "Capacity growth").</summary>
    public enum ExtractionBound
    {
        /// <summary>Nothing binds: the order is paused, its gap is already closed, or the idle drills
        /// on free deposits cover it.</summary>
        None,
        /// <summary>Free deposits remain but there are not enough idle drills to work them: a hull
        /// problem, answered by building drill ships.</summary>
        HullBound,
        /// <summary>Every catchment deposit of the ore is being worked or cooling down (or there is
        /// none): a ground problem, answered by working more deposits.</summary>
        DepositBound,
    }

    /// <summary>One (hub, ore) standing extraction order the NPC planner authors from the measured
    /// flow gap. Every rate is in <b>units per hour</b> of the ore; <see cref="LitersPerUnit"/>
    /// converts, and <see cref="TargetRateLitersPerHour"/> is the figure a Mine dispatch sizes
    /// against.</summary>
    public sealed class StandingExtractionOrder
    {
        public string HubNodeId;
        /// <summary>Inventory key of the ore (<c>Ore:Ice</c>). One order per ore, so an NPC mines
        /// what its hub consumes rather than the deposit's natural mix.</summary>
        public string OreKey;

        /// <summary>Measured consumption at the hub.</summary>
        public double ConsumePerHour;
        /// <summary>Which measurement set <see cref="ConsumePerHour"/>: <c>draw</c> (production input
        /// draw, <see cref="SpatialNode.DemandHistory"/>), <c>burn</c> (the ice a hub's share of
        /// the faction's fuel burn implies) or <c>demand</c> (draw plus the hub's unmet production
        /// input rows spread over <see cref="FactionTuning.ExtractionStockHours"/>).</summary>
        public string ConsumeSource;
        /// <summary>Units of the ore the hub's production-input demand rows still want after its
        /// stock and inbound deliveries. Zero for ice, which reads its need from the burn.</summary>
        public double UnmetInputDemand;
        /// <summary>Passive mint of the ore across the hub's share of the catchment.</summary>
        public double PassivePerHour;
        /// <summary>Mine deliveries to the hub (<see cref="SpatialNode.MinedEma"/>).</summary>
        public double MinedPerHour;
        /// <summary>Units held at the hub and on its share of the catchment's deposits.</summary>
        public double Stock;
        /// <summary><c>consume − (passive + mined)</c>. Positive means the hub is falling behind.</summary>
        public double GapPerHour;
        /// <summary>The mined rate that closes the gap: <c>max(0, consume − passive)</c>.</summary>
        public double TargetPerHour;
        /// <summary>What the free deposits and idle drills this pass allotted to the order could add,
        /// up to the gap. Zero for a paused order or one with no gap.</summary>
        public double DeliverablePerHour;
        /// <summary>What the Mine trips already bound for the hub with this ore add, sustained: each
        /// trip's target volume over its dwell (<see cref="ExtractionFleetIndex.InFlightLitersPerHour"/>)
        /// ÷ <c>(1 + MineCooldownMultiplier)</c>, the same per-pair rate
        /// <see cref="DeliverablePerHour"/> is summed in. Read by capacity growth
        /// (<see cref="ExtractionCapacity.MissingPerHour"/>), not by the bound.</summary>
        public double InFlightPerHour;
        /// <summary>Richness factors (<see cref="MineTargeting.NodeRichnessFactor"/>) of the free
        /// deposits left without a drill, richest first. Set on a
        /// <see cref="ExtractionBound.HullBound"/> order only, null otherwise: the seams a new hull
        /// would work (<see cref="ExtractionCapacity.HullsToCover"/>).</summary>
        public List<double> UnpairedDepositFactors;
        /// <summary>The drill hulls working the hub's catchment for this ore: the Mine trips bound for
        /// the hub with it, and the Mine-suitable hulls parked at the hub or on its catchment deposits
        /// (a drill comes home to the deposit it works). Set on a
        /// <see cref="ExtractionBound.DepositBound"/> order only, null otherwise or when there is none:
        /// the hulls a drill-quality upgrade is measured against
        /// (<see cref="ExtractionCapacity.UpgradeBasis"/>).</summary>
        public List<ExtractionHull> WorkingHulls;
        public double LitersPerUnit = 1.0;

        public bool Paused;
        /// <summary>Why the order is in its state: <c>gap</c>, <c>sustain</c>, <c>resume</c>,
        /// <c>ceiling</c> or <c>hysteresis</c>. See <see cref="ExtractionPlanner.Plan"/>.</summary>
        public string Reason;
        public ExtractionBound Bound;

        public double TargetRateLitersPerHour { get { return TargetPerHour * LitersPerUnit; } }

        public string Key { get { return ExtractionPlanner.OrderKey(HubNodeId, OreKey); } }
    }

    /// <summary><see cref="Count"/> drill hulls of one prefab, and the rate each drills at:
    /// <c>DrillYieldScore × MineDrillLitersPerScorePoint × MineDutyCycle</c>, litres per second before
    /// richness.</summary>
    public sealed class ExtractionHull
    {
        public string PrefabId;
        public double LitersPerSecond;
        public int Count;
    }

    /// <summary>Fleet facts the planner needs, gathered once per faction pass so the plan itself
    /// never loops over fleets per node (CLAUDE.md rule 5).</summary>
    public sealed class ExtractionFleetIndex
    {
        /// <summary>Idle Mine-suitable hulls parked at each owned node, keyed by node id. What the
        /// planner reads a catchment's parked drills from.</summary>
        public readonly Dictionary<string, List<ExtractionHull>> IdleHullsAt =
            new Dictionary<string, List<ExtractionHull>>(StringComparer.Ordinal);

        /// <summary>The hulls of the Mine trips still out, keyed <c>ExtractionPlanner.OrderKey(delivery
        /// node, ore)</c> for each ore the trip targets or carries. One hull per trip.</summary>
        public readonly Dictionary<string, List<ExtractionHull>> MineHullsToward =
            new Dictionary<string, List<ExtractionHull>>(StringComparer.Ordinal);

        /// <summary>Largest count of one prefab at one node folded into the pool. Bounds the list
        /// for an estate with an absurd parked stock; far above any measured one (IGSL: 93 hulls).</summary>
        internal const int MaxPerStockEntry = 256;

        /// <summary>Extraction rate, litres per second per unit of ore density, of every idle
        /// Mine-suitable hull the faction has parked, one entry per hull, fastest first. The rate is
        /// <c>DrillYieldScore × MineDrillLitersPerScorePoint × MineDutyCycle</c> — the same product
        /// <c>OperationsManager</c> resolves a dwell from.</summary>
        public readonly List<double> IdleDrillLitersPerSecond = new List<double>();

        /// <summary>Deposits a Mine fleet is dwelling at or flying to right now. A deposit takes
        /// one trip at a time, so these are not free whatever their cooldown says.</summary>
        public readonly HashSet<string> WorkedDepositIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Units already on their way to a node, keyed
        /// <c>ExtractionPlanner.OrderKey(node, item)</c>: the cargo aboard every unresolved Mine and
        /// Haul fleet whose delivery stop is that node, plus a Haul's pickups not yet loaded and a
        /// Mine fleet's dwells not yet mined (target litres x weight ÷ litres per unit). What nets an
        /// unmet input row, so a want already being served is not extracted a second time.</summary>
        public readonly Dictionary<string, double> InboundUnits = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Extraction rate, litres per hour, of every Mine trip still to finish its dwell,
        /// keyed <c>ExtractionPlanner.OrderKey(delivery node, ore)</c>: target volume × weight over
        /// the dwell, the measure <c>OperationsManager</c>'s order walk closes against. The dwell
        /// already carries the overburden; the cooldown is the planner's to apply.</summary>
        public readonly Dictionary<string, double> InFlightLitersPerHour = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Litres per hour of <paramref name="itemKey"/> the Mine trips bound for
        /// <paramref name="nodeId"/> are extracting.</summary>
        public double InFlightRateTo(string nodeId, string itemKey)
        {
            double rate;
            return InFlightLitersPerHour.TryGetValue(ExtractionPlanner.OrderKey(nodeId, itemKey), out rate) ? rate : 0;
        }

        /// <summary>Units of <paramref name="itemKey"/> inbound to <paramref name="nodeId"/>.</summary>
        public double InboundTo(string nodeId, string itemKey)
        {
            double units;
            return InboundUnits.TryGetValue(ExtractionPlanner.OrderKey(nodeId, itemKey), out units) ? units : 0;
        }

        /// <summary>Builds the index in one pass over the owned nodes' parked stock and one over
        /// the faction's pending fleets.</summary>
        public static ExtractionFleetIndex Build(FactionState st, IEnumerable<SpatialNode> ownedNodes,
                                                 IPrefabCatalogue catalogue, FactionTuning tuning,
                                                 IItemVolumeCatalogue volumes = null)
        {
            var index = new ExtractionFleetIndex();
            if (tuning == null) return index;

            if (ownedNodes != null && catalogue != null)
                foreach (SpatialNode n in ownedNodes)
                {
                    if (n == null) continue;
                    foreach (KeyValuePair<string, int> kv in n.ShipStock)
                    {
                        if (kv.Value <= 0 || !ShipPool.IsMineSuitable(catalogue, kv.Key)) continue;
                        double rate = catalogue.DrillYieldScore(kv.Key) * tuning.MineDrillLitersPerScorePoint
                                    * tuning.MineDutyCycle;
                        if (rate <= 0) continue;
                        int count = kv.Value < MaxPerStockEntry ? kv.Value : MaxPerStockEntry;
                        for (int i = 0; i < count; i++) index.IdleDrillLitersPerSecond.Add(rate);
                        AddHull(index.IdleHullsAt, n.Id, kv.Key, rate, count);
                    }
                }
            index.SortPool();

            if (st != null)
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    if (f.Kind != ActionKind.Mine || f.Resolved || f.Intercepted) continue;
                    // The dwell leg's target is the deposit; a Tier-2 dispatch still flying to it
                    // carries the deposit as a queued Mine stop instead.
                    if (f.CurrentStopKind == FleetStopKind.Mine && !string.IsNullOrEmpty(f.TargetNodeId))
                        index.WorkedDepositIds.Add(f.TargetNodeId);
                    if (f.RemainingStops != null)
                        foreach (FleetStop stop in f.RemainingStops)
                            if (stop.Kind == FleetStopKind.Mine && !string.IsNullOrEmpty(stop.NodeId))
                                index.WorkedDepositIds.Add(stop.NodeId);
                }

            if (st != null)
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    index.AddInbound(f, volumes);
                    if (catalogue != null) index.AddMineHull(f, catalogue, tuning);
                }
            return index;
        }

        /// <summary>Files an unresolved Mine trip's hull under its delivery node for every ore it
        /// targets (its dwell's weights, a queued dwell's) or already carries.</summary>
        private void AddMineHull(PendingFleetAction f, IPrefabCatalogue catalogue, FactionTuning tuning)
        {
            if (f.Kind != ActionKind.Mine || f.Resolved || f.Intercepted || string.IsNullOrEmpty(f.ShipPrefabId)) return;
            string dest = DeliveryNodeOf(f);
            if (string.IsNullOrEmpty(dest)) return;
            double rate = catalogue.DrillYieldScore(f.ShipPrefabId) * tuning.MineDrillLitersPerScorePoint * tuning.MineDutyCycle;
            if (rate <= 0) return;

            List<string> ores = null;
            AddOres(ref ores, f.MineWeights);
            if (f.RemainingStops != null)
                foreach (FleetStop stop in f.RemainingStops)
                    if (stop.Kind == FleetStopKind.Mine) AddOres(ref ores, stop.MineWeights);
            if (f.Cargo != null)
                foreach (KeyValuePair<string, long> kv in f.Cargo)
                    if (kv.Value > 0 && kv.Key != null && kv.Key.StartsWith("Ore:", StringComparison.Ordinal)
                        && (ores == null || !ores.Contains(kv.Key)))
                        (ores ?? (ores = new List<string>(2))).Add(kv.Key);
            if (ores == null) return;
            foreach (string ore in ores)
                AddHull(MineHullsToward, ExtractionPlanner.OrderKey(dest, ore), f.ShipPrefabId, rate, 1);
        }

        private static void AddOres(ref List<string> ores, Dictionary<string, double> weights)
        {
            if (weights == null) return;
            foreach (KeyValuePair<string, double> kv in weights)
                if (kv.Value > 0 && kv.Key != null && (ores == null || !ores.Contains(kv.Key)))
                    (ores ?? (ores = new List<string>(2))).Add(kv.Key);
        }

        private static void AddHull(Dictionary<string, List<ExtractionHull>> map, string key, string prefabId,
                                    double rate, int count)
        {
            List<ExtractionHull> list;
            if (!map.TryGetValue(key, out list))
            {
                list = new List<ExtractionHull>(2);
                map[key] = list;
            }
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i].PrefabId, prefabId, StringComparison.Ordinal))
                {
                    list[i].Count += count;
                    return;
                }
            list.Add(new ExtractionHull { PrefabId = prefabId, LitersPerSecond = rate, Count = count });
        }

        /// <summary>The node a Mine or Haul fleet delivers to: its current leg's target when that is
        /// the service stop, otherwise its first queued service stop.</summary>
        private static string DeliveryNodeOf(PendingFleetAction f)
        {
            string dest = f.CurrentStopKind == FleetStopKind.Service ? f.TargetNodeId : null;
            if (dest == null && f.RemainingStops != null)
                foreach (FleetStop stop in f.RemainingStops)
                    if (stop.Kind == FleetStopKind.Service && !string.IsNullOrEmpty(stop.NodeId)) { dest = stop.NodeId; break; }
            return dest;
        }

        /// <summary>Folds one fleet's cargo, and what it has yet to load, into
        /// <see cref="InboundUnits"/> at its delivery stop.</summary>
        private void AddInbound(PendingFleetAction f, IItemVolumeCatalogue volumes)
        {
            if (f.Resolved || f.Intercepted) return;
            if (f.Kind != ActionKind.Mine && f.Kind != ActionKind.Haul) return;

            string dest = DeliveryNodeOf(f);
            if (string.IsNullOrEmpty(dest)) return;

            if (f.Cargo != null)
                foreach (KeyValuePair<string, long> kv in f.Cargo) AddInbound(dest, kv.Key, kv.Value);
            if (f.CurrentStopKind == FleetStopKind.Pickup)
                AddInbound(dest, f.PickupItemKey, f.PickupQuantity);
            if (f.CurrentStopKind == FleetStopKind.Mine)
            {
                AddMineInbound(dest, f.MineWeights, f.MineTargetLiters, volumes);
                // A dwell leg departs when the dwell starts and arrives when it ends.
                AddMineRate(dest, f.MineWeights, f.MineTargetLiters, (f.EstimatedArrivalUtc - f.DepartedUtc).TotalSeconds);
            }
            if (f.RemainingStops != null)
                foreach (FleetStop stop in f.RemainingStops)
                {
                    if (stop.Kind == FleetStopKind.Pickup) AddInbound(dest, stop.PickupItemKey, stop.PickupQuantity);
                    else if (stop.Kind == FleetStopKind.Mine)
                    {
                        AddMineInbound(dest, stop.MineWeights, stop.MineTargetLiters, volumes);
                        AddMineRate(dest, stop.MineWeights, stop.MineTargetLiters, stop.DurationSeconds);
                    }
                }
        }

        private void AddMineRate(string dest, Dictionary<string, double> weights, double targetLiters, double dwellSeconds)
        {
            if (weights == null || targetLiters <= 0 || dwellSeconds <= 0) return;
            double litersPerHour = targetLiters / dwellSeconds * 3600.0;
            foreach (KeyValuePair<string, double> kv in weights)
            {
                if (kv.Value <= 0) continue;
                string key = ExtractionPlanner.OrderKey(dest, kv.Key);
                double prior;
                InFlightLitersPerHour.TryGetValue(key, out prior);
                InFlightLitersPerHour[key] = prior + litersPerHour * kv.Value;
            }
        }

        private void AddMineInbound(string dest, Dictionary<string, double> weights, double targetLiters,
                                    IItemVolumeCatalogue volumes)
        {
            if (weights == null || targetLiters <= 0) return;
            foreach (KeyValuePair<string, double> kv in weights)
            {
                double lpu = volumes != null ? volumes.VolumeLitersPerUnit(kv.Key) : 0;
                AddInbound(dest, kv.Key, targetLiters * kv.Value / (lpu > 0 ? lpu : 1.0));
            }
        }

        /// <summary>Adds <paramref name="units"/> of <paramref name="itemKey"/> inbound to
        /// <paramref name="nodeId"/>. Public so a caller holding its own in-flight read can seed it.</summary>
        public void AddInbound(string nodeId, string itemKey, double units)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(itemKey) || units <= 0) return;
            string key = ExtractionPlanner.OrderKey(nodeId, itemKey);
            double prior;
            InboundUnits.TryGetValue(key, out prior);
            InboundUnits[key] = prior + units;
        }

        /// <summary>Puts the pool fastest-first. Call after filling
        /// <see cref="IdleDrillLitersPerSecond"/> by hand.</summary>
        public void SortPool()
        {
            IdleDrillLitersPerSecond.Sort((a, b) => b.CompareTo(a));
        }
    }

    /// <summary>What <see cref="ExtractionPlanner.Plan"/> reads beyond the world itself. Every
    /// member is optional; a missing one reads as "nothing known", never as a default rate.</summary>
    public sealed class ExtractionPlanInputs
    {
        /// <summary>The faction's owned nodes. Null makes the planner scan the graph for them.</summary>
        public List<SpatialNode> OwnedNodes;
        /// <summary>Ice units per unit of each gas a FuelDepot makes
        /// (<see cref="ProductionManager.DepotIcePerGasUnit"/>). Null or empty: no burn implies ice,
        /// and the ice path falls back to measured draw alone.</summary>
        public IReadOnlyDictionary<string, double> IcePerGasUnit;
        /// <summary>Litres per unit of each ore. Null: one litre per unit, the fallback
        /// <see cref="SpatialNode.DemandHistory"/> credits under.</summary>
        public IItemVolumeCatalogue Volumes;
        /// <summary>Idle drills and worked deposits. Null: no idle drill, no worked deposit — every
        /// open order with a gap then reads as hull-bound or deposit-bound.</summary>
        public ExtractionFleetIndex Fleet;
    }

    /// <summary>
    /// The NPC half of the extraction loop (docs/mining-plan.md, "The NPC planner"): turns measured
    /// flows into per-(hub, ore) standing extraction orders. Pure — it reads the world and returns
    /// orders; it dispatches nothing and writes nothing.
    ///
    /// <para><b>The gap.</b> <c>gap = consume − (passive + mined)</c>, per hub and ore, in units per
    /// hour. The terms:</para>
    /// <list type="bullet">
    /// <item><b>consume</b>, ore: the hub's production input draw,
    /// <see cref="SpatialNode.DemandHistory"/> (litres per Operations pass, credited where a
    /// capability bank or build consumes an input) converted to units per hour. Ice at a FuelDepot
    /// hub: the larger of that draw and the hub's share, by depot level, of the ice the faction's
    /// fuel burn implies (<see cref="SpatialNode.FuelBurnEma"/> × <see cref="ExtractionPlanInputs.IcePerGasUnit"/>).
    /// The burn is what departing legs needed, met or not, so it keeps reading the real need while
    /// the depots sit starved and their own draw has fallen to the supply.</item>
    /// <item><b>consume</b>, the demand term (any ore but ice): a starved bank's draw has fallen to
    /// its supply the same way, so a hub whose production-input rows (<c>Item:Ore:*</c>, class
    /// <see cref="DemandClass.RecipeInput"/> or chain-sized) still want more than its stock and
    /// inbound deliveries (<see cref="ExtractionFleetIndex.InboundUnits"/>) reads
    /// <c>draw + unmet ÷ ExtractionStockHours</c>: refilled within the ceiling's own window.</item>
    /// <item><b>passive</b>: the income pass's free mint (<see cref="Economy.SourceItemYield"/> ×
    /// the entry's weight) on the hub's share of the catchment.</item>
    /// <item><b>mined</b>: <see cref="SpatialNode.MinedEma"/> at the hub.</item>
    /// </list>
    ///
    /// <para><b>Catchment shares.</b> A deposit within <see cref="FactionTuning.MineSearchRadiusKm"/>
    /// of several hubs consuming the same ore is assigned to the nearest (ties by node id), so its
    /// passive mint, stock and drill slot count once. A hub always owns its own stock.</para>
    ///
    /// <para><b>Order rules.</b> A hub with no consumption of an ore never orders it. Otherwise,
    /// with <c>ceiling = ExtractionStockHours × consume</c> and
    /// <c>resume = ceiling × ExtractionResumeFraction</c>:</para>
    /// <list type="bullet">
    /// <item>no previous order: opens (<c>gap</c>) when the gap is positive — paused
    /// (<c>ceiling</c>) instead if stock already reaches the ceiling; no order otherwise;</item>
    /// <item>a previous open order stays open (<c>sustain</c>) while consume exceeds passive, and
    /// pauses (<c>ceiling</c>) when stock reaches the ceiling. It is <b>not</b> closed when the gap
    /// reaches zero — its own trips are what closed it;</item>
    /// <item>a previous paused order resumes (<c>resume</c>) once stock falls below
    /// <c>resume</c>, and stays paused (<c>hysteresis</c>) until then;</item>
    /// <item>any order is dropped once passive covers consumption.</item>
    /// </list>
    ///
    /// <para><b>Bound.</b> Open orders with a positive gap are taken largest gap first. Each walks
    /// its free deposits (not cooling down, not being worked, not taken by an earlier order this
    /// pass), richest first, pairing each with the fastest idle drill left in the faction pool, and
    /// adds that pair's sustained rate — drill rate × <see cref="MineTargeting.NodeRichnessFactor"/>
    /// ÷ <c>((1 + MineOverburdenFraction)(1 + MineCooldownMultiplier))</c>, since a deposit rests as
    /// long as it was worked — until the gap is covered. Covered: <see cref="ExtractionBound.None"/>.
    /// Drills ran out with free deposits left: <see cref="ExtractionBound.HullBound"/>. Free
    /// deposits ran out (or there were none): <see cref="ExtractionBound.DepositBound"/>.</para>
    ///
    /// <para><b>Never for a player faction</b> (<see cref="FactionState.IsPlayerFaction"/>): a
    /// player's orders are what the player set.</para>
    ///
    /// <para>Cost: one pass over the owned nodes' consumption and fuel burn, one over mineable
    /// owned nodes × their yield entries × the hubs consuming that ore, and one walk of the drill
    /// pool. No fleet loop — fleet facts arrive pre-indexed in <see cref="ExtractionFleetIndex"/>.</para>
    ///
    /// <para><b>Hysteresis state</b> is carried in the previous plan, which the caller keeps and
    /// passes back in; nothing is persisted. A restart re-opens a hub sitting between its resume
    /// line and its ceiling, which at worst tops it up to the ceiling.</para>
    /// </summary>
    public static class ExtractionPlanner
    {
        public const string ReasonGap = "gap";
        public const string ReasonSustain = "sustain";
        public const string ReasonResume = "resume";
        public const string ReasonCeiling = "ceiling";
        public const string ReasonHysteresis = "hysteresis";

        public const string SourceDraw = "draw";
        public const string SourceBurn = "burn";
        public const string SourceDemand = "demand";

        internal static string OrderKey(string hubNodeId, string oreKey)
        {
            return (hubNodeId ?? "") + "|" + (oreKey ?? "");
        }

        /// <summary>A hub's production input draw of <paramref name="oreKey"/> in units per hour: its
        /// <see cref="SpatialNode.DemandHistory"/> (litres per Operations pass, credited where a
        /// capability bank or build consumes the input) over the ore's litres per unit, times the
        /// passes in an hour. The planner's <c>draw</c> consumption, and what a FuelDepot node keeps
        /// of its ice when it trades (<see cref="FuelMarket.IceKeep"/>). An unknown volume counts one
        /// litre per unit, the fallback the history is credited under.</summary>
        public static double DrawPerHour(SpatialNode node, string oreKey, IItemVolumeCatalogue volumes,
            FactionTuning tuning)
        {
            if (node == null || string.IsNullOrEmpty(oreKey) || tuning == null) return 0;
            double litersPerPass;
            if (!node.DemandHistory.TryGetValue(oreKey, out litersPerPass)) return 0;
            double lpu = volumes != null ? volumes.VolumeLitersPerUnit(oreKey) : 0;
            return UnitsPerHour(litersPerPass, lpu > 0 ? lpu : 1.0, tuning);
        }

        private static double UnitsPerHour(double litersPerPass, double litersPerUnit, FactionTuning tuning)
        {
            if (litersPerPass <= 0 || litersPerUnit <= 0) return 0;
            double opsPerHour = 3600.0 / (tuning.OpsIntervalSeconds > 0 ? tuning.OpsIntervalSeconds : 10.0);
            return litersPerPass / litersPerUnit * opsPerHour;
        }

        private sealed class HubOre
        {
            public SpatialNode Hub;
            public string Ore;
            public double Consume;
            public string Source;
            public double Passive;
            public double Stock;
            public double InputDemand;
            public double Unmet;
            public readonly List<SpatialNode> Deposits = new List<SpatialNode>();
        }

        /// <summary>Plans one faction's extraction orders. See the class summary for every rule.
        /// <paramref name="previous"/> is the plan this returned last time for the same faction
        /// (null on the first pass); it carries the pause state and nothing else.</summary>
        public static List<StandingExtractionOrder> Plan(FactionState st, WorldModel world, FactionTuning tuning,
            DateTime now, ExtractionPlanInputs inputs, IList<StandingExtractionOrder> previous)
        {
            var orders = new List<StandingExtractionOrder>();
            if (st == null || world == null || tuning == null) return orders;
            if (st.IsPlayerFaction) return orders;
            if (string.IsNullOrEmpty(st.Tag) || st.Tag == NeutralFaction.Tag) return orders;
            if (inputs == null) inputs = new ExtractionPlanInputs();

            List<SpatialNode> owned = inputs.OwnedNodes ?? OwnedNodesOf(world, st.Tag);

            // ---- consumption, per (hub, ore) -------------------------------------------------
            var hubOres = new Dictionary<string, HubOre>(StringComparer.Ordinal);
            var hubsByOre = new Dictionary<string, List<HubOre>>(StringComparer.Ordinal);
            string iceKey = "Ore:" + tuning.IceOreSubTypeId;
            double burnIcePerHour = 0;
            int depotLevels = 0;

            foreach (SpatialNode n in owned)
            {
                if (n == null) continue;
                foreach (KeyValuePair<string, double> kv in n.DemandHistory)
                {
                    if (kv.Value <= 0 || !kv.Key.StartsWith("Ore:", StringComparison.Ordinal)) continue;
                    double perHour = UnitsPerHour(kv.Value, LitersPerUnit(inputs, kv.Key), tuning);
                    if (perHour <= 0) continue;
                    HubOre h = Touch(hubOres, hubsByOre, n, kv.Key);
                    h.Consume = perHour;
                    h.Source = SourceDraw;
                }

                int level;
                if (n.Capabilities != null
                    && n.Capabilities.TryGetValue(NodeCapabilityKind.FuelDepot, out level) && level > 0)
                    depotLevels += level;

                if (inputs.IcePerGasUnit != null && n.FuelBurnEma.Count > 0)
                    foreach (KeyValuePair<string, double> burned in n.FuelBurnEma)
                    {
                        double icePerGas;
                        if (!inputs.IcePerGasUnit.TryGetValue(burned.Key, out icePerGas) || icePerGas <= 0) continue;
                        double gasPerHour = FuelDraw.BurnRateAt(n, burned.Key, now, tuning);
                        if (gasPerHour > 0) burnIcePerHour += gasPerHour * icePerGas;
                    }
            }

            // The burn is metered where hulls depart, not where ice is converted, so it is shared
            // across the depots by installed level — the share of the conversion each would carry.
            if (burnIcePerHour > 0 && depotLevels > 0)
                foreach (SpatialNode n in owned)
                {
                    int level;
                    if (n == null || n.Capabilities == null
                        || !n.Capabilities.TryGetValue(NodeCapabilityKind.FuelDepot, out level) || level <= 0)
                        continue;
                    double share = burnIcePerHour * level / depotLevels;
                    HubOre h = Touch(hubOres, hubsByOre, n, iceKey);
                    if (share > h.Consume)
                    {
                        h.Consume = share;
                        h.Source = SourceBurn;
                    }
                }

            // A starved bank's draw has fallen to its supply, so the draw alone would never ask for
            // more. Its unmet input rows are the demand side: collected here, before the catchment
            // split, so a hub that has not drawn at all yet still gets its deposits.
            CollectInputDemand(st, world, inputs, iceKey, hubOres, hubsByOre);

            if (hubOres.Count == 0) return orders;

            // ---- catchment shares: passive, stock, deposits ----------------------------------
            double econPerHour = tuning.EconomyIntervalSeconds > 0 ? 3600.0 / tuning.EconomyIntervalSeconds : 0;
            var control = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (HubOre h in hubOres.Values)
            {
                long held;
                if (h.Hub.Inventory.TryGetValue(h.Ore, out held) && held > 0) h.Stock += held;
            }

            foreach (SpatialNode m in owned)
            {
                if (m == null || !NodeContent.IsMineable(m)) continue;
                NodeYieldEntry[] entries = NodeYieldResolver.EntriesFor(m, tuning, world.Registry);
                for (int i = 0; i < entries.Length; i++)
                {
                    NodeYieldEntry e = entries[i];
                    if (e == null || e.Weight <= 0 || string.IsNullOrEmpty(e.SubTypeId)) continue;
                    List<HubOre> hubs;
                    if (!hubsByOre.TryGetValue(e.SubTypeId, out hubs)) continue;
                    HubOre owner = NearestHub(hubs, m, tuning);
                    if (owner == null) continue;

                    owner.Deposits.Add(m);
                    if (!ReferenceEquals(owner.Hub, m))
                    {
                        long held;
                        if (m.Inventory.TryGetValue(e.SubTypeId, out held) && held > 0) owner.Stock += held;
                    }

                    ItemKind kind;
                    if (e.MineOnly || econPerHour <= 0 || !Enum.TryParse(e.ItemKind, out kind)) continue;
                    double c;
                    if (!control.TryGetValue(m.Id, out c))
                    {
                        c = world.Influence != null ? world.Influence.ControlAt(m.Id, st.Tag) : 1.0;
                        control[m.Id] = c;
                    }
                    long perPass = Economy.SourceItemYield(m, c, tuning, kind);
                    if (perPass > 0) owner.Passive += perPass * e.Weight * econPerHour;
                }
            }

            // ---- the demand term: draw + unmet input over the stock window ----------------------
            foreach (HubOre h in hubOres.Values)
            {
                if (h.InputDemand <= 0) continue;
                double inbound = inputs.Fleet != null ? inputs.Fleet.InboundTo(h.Hub.Id, h.Ore) : 0;
                double unmet = h.InputDemand - h.Stock - inbound;
                if (unmet <= 0 || tuning.ExtractionStockHours <= 0) continue;
                h.Unmet = unmet;
                // consume = max(draw, draw + unmet / ExtractionStockHours): refill the shortfall
                // within the same window the ceiling is measured in.
                double withDemand = h.Consume + unmet / tuning.ExtractionStockHours;
                if (withDemand > h.Consume)
                {
                    h.Consume = withDemand;
                    h.Source = SourceDemand;
                }
            }

            // ---- orders ------------------------------------------------------------------------
            Dictionary<string, StandingExtractionOrder> prior = null;
            if (previous != null)
            {
                prior = new Dictionary<string, StandingExtractionOrder>(StringComparer.Ordinal);
                foreach (StandingExtractionOrder o in previous)
                    if (o != null) prior[o.Key] = o;
            }

            double stockHours = tuning.ExtractionStockHours > 0 ? tuning.ExtractionStockHours : 0;
            double resumeFraction = tuning.ExtractionResumeFraction;
            double cooldownRest = 1.0 + Math.Max(0, tuning.MineCooldownMultiplier);

            foreach (HubOre h in hubOres.Values)
            {
                if (h.Consume <= 0) continue;
                double mined = ExtractionFlow.MinedRateAt(h.Hub, h.Ore, now, tuning);
                double target = h.Consume - h.Passive;
                if (target <= 0) continue;                  // passive covers it: nothing to extract
                double gap = h.Consume - (h.Passive + mined);

                double ceiling = stockHours * h.Consume;
                double resumeBelow = ceiling * resumeFraction;
                bool atCeiling = stockHours > 0 && h.Stock >= ceiling;

                StandingExtractionOrder was = null;
                if (prior != null) prior.TryGetValue(OrderKey(h.Hub.Id, h.Ore), out was);

                bool paused;
                string reason;
                if (was == null)
                {
                    if (gap <= 0) continue;
                    paused = atCeiling;
                    reason = paused ? ReasonCeiling : ReasonGap;
                }
                else if (was.Paused)
                {
                    bool resume = stockHours <= 0 || h.Stock < resumeBelow;
                    paused = !resume;
                    reason = resume ? ReasonResume : ReasonHysteresis;
                }
                else
                {
                    paused = atCeiling;
                    reason = paused ? ReasonCeiling : ReasonSustain;
                }

                double lpu = LitersPerUnit(inputs, h.Ore);
                double inFlight = inputs.Fleet != null
                    ? inputs.Fleet.InFlightRateTo(h.Hub.Id, h.Ore) / cooldownRest / lpu
                    : 0;

                orders.Add(new StandingExtractionOrder
                {
                    HubNodeId = h.Hub.Id,
                    OreKey = h.Ore,
                    ConsumePerHour = h.Consume,
                    ConsumeSource = h.Source,
                    UnmetInputDemand = h.Unmet,
                    PassivePerHour = h.Passive,
                    MinedPerHour = mined,
                    Stock = h.Stock,
                    GapPerHour = gap,
                    TargetPerHour = target,
                    InFlightPerHour = inFlight,
                    LitersPerUnit = lpu,
                    Paused = paused,
                    Reason = reason,
                    Bound = ExtractionBound.None,
                });
            }

            ClassifyBounds(orders, hubOres, inputs.Fleet, world, tuning, now);
            CollectWorkingHulls(orders, hubOres, inputs.Fleet);

            orders.Sort(CompareByHubThenOre);
            return orders;
        }

        /// <summary>Sets <see cref="StandingExtractionOrder.Bound"/> and
        /// <see cref="StandingExtractionOrder.DeliverablePerHour"/> on every open order with a gap.
        /// See the class summary.</summary>
        private static void ClassifyBounds(List<StandingExtractionOrder> orders, Dictionary<string, HubOre> hubOres,
            ExtractionFleetIndex fleet, WorldModel world, FactionTuning tuning, DateTime now)
        {
            var wanting = new List<StandingExtractionOrder>();
            foreach (StandingExtractionOrder o in orders)
                if (!o.Paused && o.GapPerHour > 0) wanting.Add(o);
            if (wanting.Count == 0) return;
            wanting.Sort(CompareByGapDescending);

            List<double> pool = fleet != null ? fleet.IdleDrillLitersPerSecond : null;
            int poolCount = pool != null ? pool.Count : 0;
            int next = 0;
            var taken = new HashSet<string>(StringComparer.Ordinal);
            double rest = (1.0 + Math.Max(0, tuning.MineOverburdenFraction))
                        * (1.0 + Math.Max(0, tuning.MineCooldownMultiplier));

            foreach (StandingExtractionOrder o in wanting)
            {
                HubOre h = hubOres[o.Key];
                var weights = new Dictionary<string, double>(StringComparer.Ordinal) { { o.OreKey, 1.0 } };

                var free = new List<KeyValuePair<SpatialNode, double>>();
                foreach (SpatialNode d in h.Deposits)
                {
                    if (taken.Contains(d.Id)) continue;
                    if (d.MineCooldownUntilUtc > now) continue;
                    if (fleet != null && fleet.WorkedDepositIds.Contains(d.Id)) continue;
                    double factor = MineTargeting.NodeRichnessFactor(d, tuning, weights, world.Registry);
                    if (factor <= 0) continue;
                    free.Add(new KeyValuePair<SpatialNode, double>(d, factor));
                }
                free.Sort(CompareByFactorDescending);

                double deliverable = 0;
                int used = 0;
                while (used < free.Count && next < poolCount && deliverable < o.GapPerHour)
                {
                    double litersPerHour = pool[next] * free[used].Value / rest * 3600.0;
                    deliverable += litersPerHour / o.LitersPerUnit;
                    taken.Add(free[used].Key.Id);
                    used++;
                    next++;
                }

                o.DeliverablePerHour = deliverable;
                if (deliverable >= o.GapPerHour) o.Bound = ExtractionBound.None;
                else if (used < free.Count)
                {
                    o.Bound = ExtractionBound.HullBound;
                    // The seams a new hull would work. Not claimed in `taken`: no drill flies to
                    // them this pass, so a later order may still count them as its own free ground.
                    o.UnpairedDepositFactors = new List<double>(free.Count - used);
                    for (int i = used; i < free.Count; i++) o.UnpairedDepositFactors.Add(free[i].Value);
                }
                else o.Bound = ExtractionBound.DepositBound;
            }
        }

        /// <summary>Sets <see cref="StandingExtractionOrder.WorkingHulls"/> on every
        /// <see cref="ExtractionBound.DepositBound"/> order: the Mine trips bound for its hub with its
        /// ore, and the idle drills parked at the hub or on its catchment deposits. Lookups into the
        /// pre-built index only, one per catchment deposit of a deposit-bound order.</summary>
        private static void CollectWorkingHulls(List<StandingExtractionOrder> orders,
            Dictionary<string, HubOre> hubOres, ExtractionFleetIndex fleet)
        {
            if (fleet == null) return;
            foreach (StandingExtractionOrder o in orders)
            {
                if (o.Bound != ExtractionBound.DepositBound) continue;
                HubOre h;
                if (!hubOres.TryGetValue(o.Key, out h)) continue;
                List<ExtractionHull> hulls = null;
                AppendHulls(ref hulls, fleet.MineHullsToward, o.Key);
                AppendHulls(ref hulls, fleet.IdleHullsAt, h.Hub.Id);
                foreach (SpatialNode d in h.Deposits)
                    if (!ReferenceEquals(d, h.Hub)) AppendHulls(ref hulls, fleet.IdleHullsAt, d.Id);
                o.WorkingHulls = hulls;
            }
        }

        private static void AppendHulls(ref List<ExtractionHull> hulls, Dictionary<string, List<ExtractionHull>> map, string key)
        {
            List<ExtractionHull> found;
            if (key == null || !map.TryGetValue(key, out found) || found.Count == 0) return;
            (hulls ?? (hulls = new List<ExtractionHull>(found.Count))).AddRange(found);
        }

        /// <summary>Reads the faction's <c>Item:Ore:*</c> production-input rows — class
        /// <see cref="DemandClass.RecipeInput"/>, or any row a demand chain sized
        /// (<see cref="DemandEntry.ChainSized"/>, which carries the class of the commitment it
        /// serves) — at owned nodes, into <see cref="HubOre.InputDemand"/>. Ice is left out: its
        /// need is read from the burn. One pass over one register snapshot.
        ///
        /// <para>Rows are gross (<c>MatchVectors</c> nets the destination's stock), so the largest
        /// row per (hub, ore) is taken rather than the sum: several rows at one node describe one
        /// shelf. A hub is only created from a row the hub's own stock and inbound do not already
        /// cover, so a satisfied row cannot pull deposits away from a hub that consumes.</para></summary>
        private static void CollectInputDemand(FactionState st, WorldModel world, ExtractionPlanInputs inputs,
            string iceKey, Dictionary<string, HubOre> hubOres, Dictionary<string, List<HubOre>> hubsByOre)
        {
            if (world.Demand == null) return;
            var largest = new Dictionary<string, KeyValuePair<SpatialNode, long>>(StringComparer.Ordinal);
            foreach (DemandEntry d in world.Demand.SnapshotItemDemands(st.Tag))
            {
                if (d.Quantity <= 0 || string.IsNullOrEmpty(d.NodeId)) continue;
                if (d.Class != DemandClass.RecipeInput && !d.ChainSized) continue;
                if (!d.Kind.StartsWith("Item:Ore:", StringComparison.Ordinal)) continue;
                string ore = d.Kind.Substring("Item:".Length);
                if (ore == iceKey) continue;

                SpatialNode hub;
                if (!world.Graph.Nodes.TryGetValue(d.NodeId, out hub)
                    || !string.Equals(hub.OwnerFactionTag, st.Tag, StringComparison.Ordinal)) continue;

                string key = OrderKey(hub.Id, ore);
                KeyValuePair<SpatialNode, long> seen;
                if (!largest.TryGetValue(key, out seen) || d.Quantity > seen.Value)
                    largest[key] = new KeyValuePair<SpatialNode, long>(hub, d.Quantity);
            }

            foreach (KeyValuePair<string, KeyValuePair<SpatialNode, long>> kv in largest)
            {
                SpatialNode hub = kv.Value.Key;
                string ore = kv.Key.Substring(kv.Key.IndexOf('|') + 1);
                HubOre h;
                if (!hubOres.TryGetValue(kv.Key, out h))
                {
                    long held;
                    hub.Inventory.TryGetValue(ore, out held);
                    double inbound = inputs.Fleet != null ? inputs.Fleet.InboundTo(hub.Id, ore) : 0;
                    if (kv.Value.Value - held - inbound <= 0) continue;
                    h = Touch(hubOres, hubsByOre, hub, ore);
                    h.Source = SourceDraw;
                }
                h.InputDemand = kv.Value.Value;
            }
        }

        // ---- trace ------------------------------------------------------------------------------

        /// <summary>One <c>EXTRACTION_PLAN</c> line per order, into <paramref name="log"/>, when
        /// <c>factions debug extraction</c> is on. The call site's guard, kept in one place so the
        /// caller cannot forget it.</summary>
        public static void AppendTrace(IList<string> log, string tag, IList<StandingExtractionOrder> orders)
        {
            if (!DebugTraces.Extraction || log == null || orders == null) return;
            foreach (StandingExtractionOrder o in orders) log.Add(FormatTrace(tag, o));
        }

        /// <summary>The <c>EXTRACTION_PLAN</c> line for one order. Rates in litres per hour, stock in
        /// litres:
        /// <c>EXTRACTION_PLAN &lt;tag&gt; hub=&lt;node&gt; ore=&lt;key&gt; consume=&lt;L/h&gt;
        /// src=draw|burn|demand unmet=&lt;L&gt; passive= mined= stock= gap= target= deliver= state=open|paused bound=none|hull|deposit
        /// reason=&lt;r&gt;</c>.</summary>
        public static string FormatTrace(string tag, StandingExtractionOrder o)
        {
            if (o == null) return "EXTRACTION_PLAN " + (tag ?? "?");
            double lpu = o.LitersPerUnit;
            var sb = new StringBuilder(160);
            sb.Append("EXTRACTION_PLAN ").Append(tag ?? "?")
              .Append(" hub=").Append(o.HubNodeId ?? "?")
              .Append(" ore=").Append(o.OreKey ?? "?")
              .Append(" consume=").Append(Compact(o.ConsumePerHour * lpu))
              .Append(" src=").Append(o.ConsumeSource ?? "?")
              .Append(" unmet=").Append(Compact(o.UnmetInputDemand * lpu))
              .Append(" passive=").Append(Compact(o.PassivePerHour * lpu))
              .Append(" mined=").Append(Compact(o.MinedPerHour * lpu))
              .Append(" stock=").Append(Compact(o.Stock * lpu))
              .Append(" gap=").Append(Compact(o.GapPerHour * lpu))
              .Append(" target=").Append(Compact(o.TargetRateLitersPerHour))
              .Append(" deliver=").Append(Compact(o.DeliverablePerHour * lpu))
              .Append(" state=").Append(o.Paused ? "paused" : "open")
              .Append(" bound=").Append(BoundToken(o.Bound))
              .Append(" reason=").Append(o.Reason ?? "?");
            return sb.ToString();
        }

        internal static string BoundToken(ExtractionBound bound)
        {
            switch (bound)
            {
                case ExtractionBound.HullBound: return "hull";
                case ExtractionBound.DepositBound: return "deposit";
                default: return "none";
            }
        }

        /// <summary>Whole litres below 10k, then k / M / G, sign kept — one readable token.</summary>
        public static string Compact(double v)
        {
            double a = Math.Abs(v);
            if (a < 10000) return v.ToString("0", CultureInfo.InvariantCulture);
            if (a < 1e6) return (v / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            if (a < 1e9) return (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            return (v / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + "G";
        }

        // ---- helpers ----------------------------------------------------------------------------

        private static HubOre Touch(Dictionary<string, HubOre> hubOres, Dictionary<string, List<HubOre>> hubsByOre,
                                    SpatialNode hub, string ore)
        {
            string key = OrderKey(hub.Id, ore);
            HubOre h;
            if (hubOres.TryGetValue(key, out h)) return h;
            h = new HubOre { Hub = hub, Ore = ore };
            hubOres[key] = h;
            List<HubOre> list;
            if (!hubsByOre.TryGetValue(ore, out list))
            {
                list = new List<HubOre>();
                hubsByOre[ore] = list;
            }
            list.Add(h);
            return h;
        }

        /// <summary>The consuming hub nearest <paramref name="node"/> within the Mine search radius,
        /// ties to the lower node id; the node's own hub entry when it is one.</summary>
        private static HubOre NearestHub(List<HubOre> hubs, SpatialNode node, FactionTuning tuning)
        {
            HubOre best = null;
            double bestDist = double.MaxValue;
            double radius = tuning.MineSearchRadiusKm;
            for (int i = 0; i < hubs.Count; i++)
            {
                HubOre h = hubs[i];
                double dist = ReferenceEquals(h.Hub, node) ? 0 : h.Hub.Position.DistanceTo(node.Position);
                if (radius > 0 && dist > radius) continue;
                if (best == null || dist < bestDist
                    || (dist == bestDist && string.CompareOrdinal(h.Hub.Id, best.Hub.Id) < 0))
                {
                    best = h;
                    bestDist = dist;
                }
            }
            return best;
        }

        private static double LitersPerUnit(ExtractionPlanInputs inputs, string key)
        {
            double lpu = inputs != null && inputs.Volumes != null ? inputs.Volumes.VolumeLitersPerUnit(key) : 0;
            return lpu > 0 ? lpu : 1.0;
        }

        private static List<SpatialNode> OwnedNodesOf(WorldModel world, string tag)
        {
            var owned = new List<SpatialNode>();
            foreach (SpatialNode n in world.Graph.Nodes.Values)
                if (n != null && string.Equals(n.OwnerFactionTag, tag, StringComparison.Ordinal)) owned.Add(n);
            return owned;
        }

        private static int CompareByHubThenOre(StandingExtractionOrder a, StandingExtractionOrder b)
        {
            int cmp = string.CompareOrdinal(a.HubNodeId, b.HubNodeId);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.OreKey, b.OreKey);
        }

        private static int CompareByGapDescending(StandingExtractionOrder a, StandingExtractionOrder b)
        {
            int cmp = b.GapPerHour.CompareTo(a.GapPerHour);
            return cmp != 0 ? cmp : CompareByHubThenOre(a, b);
        }

        private static int CompareByFactorDescending(KeyValuePair<SpatialNode, double> a,
                                                     KeyValuePair<SpatialNode, double> b)
        {
            int cmp = b.Value.CompareTo(a.Value);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Key.Id, b.Key.Id);
        }
    }
}
