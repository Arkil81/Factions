using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// The NPC half of the one Mine loop (docs/mining-plan.md, slice 2b): an NPC's standing
    /// extraction orders are authored by <see cref="ExtractionPlanner"/> once per Operations pass and
    /// walked through the same <see cref="WalkMineCatchment"/> a player's standing order uses. There
    /// is no other automatic Mine trigger: the per-cycle demand-row trigger and its Mine-vs-Haul
    /// comparison were retired with this.
    /// </summary>
    public sealed partial class OperationsManager
    {
        /// <summary>The last committed plan per NPC faction tag. In memory only: the planner's
        /// hysteresis is carried in the previous plan, and a restart re-opening a hub inside its band
        /// at worst tops it up to the ceiling (docs/mining-plan.md, "as built (2a)").</summary>
        private readonly Dictionary<string, List<StandingExtractionOrder>> _extractionOrders =
            new Dictionary<string, List<StandingExtractionOrder>>(StringComparer.Ordinal);

        private static readonly List<StandingExtractionOrder> NoExtractionOrders = new List<StandingExtractionOrder>();

        /// <summary>Per NPC faction tag, when each (hub, ore) was last deposit-bound and the
        /// <c>since</c> it carried, so a signal that drops for a few passes keeps its clock
        /// (<see cref="FactionTuning.ExtractionGroundGraceSeconds"/>). In memory only, like the plan.</summary>
        private readonly Dictionary<string, Dictionary<string, ExtractionGroundMemo>> _groundMemory =
            new Dictionary<string, Dictionary<string, ExtractionGroundMemo>>(StringComparer.Ordinal);

        /// <summary>Ice units a FuelDepot eats per unit of each gas, by tuning --
        /// <see cref="ProductionManager.DepotIcePerGasUnit"/>, which the host wires in because the
        /// recipes live on the production manager. Null: no burn implies ice, and an ice hub reads its
        /// measured draw alone.</summary>
        public Func<FactionTuning, Dictionary<string, double>> IcePerGasUnitSource { get; set; }

        private FactionTuning _icePerGasTuning;
        private Dictionary<string, double> _icePerGas;

        /// <summary>The extraction orders the last committed pass planned for
        /// <paramref name="factionTag"/>; empty for a player faction or one not yet planned.</summary>
        public IList<StandingExtractionOrder> ExtractionOrdersFor(string factionTag)
        {
            List<StandingExtractionOrder> orders;
            return factionTag != null && _extractionOrders.TryGetValue(factionTag, out orders)
                ? orders.AsReadOnly()
                : NoExtractionOrders.AsReadOnly();
        }

        /// <summary>Plans <paramref name="st"/>'s extraction orders for this pass, from the previous
        /// committed plan. Null for a player faction: a player's orders are what the player set.
        ///
        /// <para>Runs at the very start of the faction's pass, before <c>FoldDemandHistory</c> and
        /// before any dispatch, so every input it reads straight off the world -- inventories, parked
        /// hulls, demand history, pending fleets -- is the same under the live context and the staged
        /// one, whose writes are deferred to commit. Cost: <see cref="ExtractionFleetIndex.Build"/>
        /// (one pass over the owned nodes' parked stock, one over pending fleets) plus
        /// <see cref="ExtractionPlanner.Plan"/> (see its summary). No fleet loop per node.</para></summary>
        private List<StandingExtractionOrder> PlanExtraction(FactionState st, List<SpatialNode> ownedNodes,
            IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning)
        {
            if (st == null || st.IsPlayerFaction) return null;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();

            List<StandingExtractionOrder> previous;
            _extractionOrders.TryGetValue(st.Tag, out previous);

            var inputs = new ExtractionPlanInputs
            {
                OwnedNodes = ownedNodes,
                IcePerGasUnit = IcePerGasFor(tuning),
                Volumes = _volumes,
                Fleet = ExtractionFleetIndex.Build(st, ownedNodes, world.PrefabCatalogue, tuning, _volumes),
            };
            List<StandingExtractionOrder> orders = ExtractionPlanner.Plan(st, world, tuning, ctx.UtcNow, inputs, previous);

            if (DebugTraces.Extraction)
            {
                var lines = new List<string>(orders.Count);
                ExtractionPlanner.AppendTrace(lines, st.Tag, orders);
                foreach (string line in lines) ctx.AddLog(line);
            }
            _lastTelemetry.ExtractionPlanTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            return orders;
        }

        private IReadOnlyDictionary<string, double> IcePerGasFor(FactionTuning tuning)
        {
            if (IcePerGasUnitSource == null || tuning == null) return null;
            if (_icePerGas == null || !ReferenceEquals(_icePerGasTuning, tuning))
            {
                _icePerGas = IcePerGasUnitSource(tuning);
                _icePerGasTuning = tuning;
            }
            return _icePerGas;
        }

        /// <summary>Keeps <paramref name="orders"/> as <paramref name="st"/>'s previous plan for the
        /// next pass, and publishes it with its deposit-bound signals on the faction
        /// (<see cref="FactionState.ExtractionOrders"/>, <see cref="FactionState.ExtractionGroundSignals"/>)
        /// for capacity growth to read. The synchronous tick calls it at once; the staged cycle calls
        /// it when its commit succeeds, so a discarded attempt never becomes the hysteresis state or
        /// the published plan.
        ///
        /// <para>The signals are derived here, from the signals the faction already carries and the
        /// faction's ground memory, so a (hub, ore) that stays deposit-bound — or drops off the bound
        /// for no longer than <see cref="FactionTuning.ExtractionGroundGraceSeconds"/> — keeps its
        /// first <c>since</c>. One <c>EXTRACTION_GROUND</c> trace line per signal when
        /// <c>factions debug extraction</c> is on.</para></summary>
        private void CommitExtractionPlan(FactionState st, List<StandingExtractionOrder> orders, DateTime now,
            FactionTuning tuning, Action<string> addLog)
        {
            if (st == null || st.Tag == null) return;
            if (orders == null) _extractionOrders.Remove(st.Tag);
            else _extractionOrders[st.Tag] = orders;

            Dictionary<string, ExtractionGroundMemo> memory = null;
            if (orders == null) _groundMemory.Remove(st.Tag);
            else if (!_groundMemory.TryGetValue(st.Tag, out memory))
            {
                memory = new Dictionary<string, ExtractionGroundMemo>(StringComparer.Ordinal);
                _groundMemory[st.Tag] = memory;
            }
            List<ExtractionGroundSignal> signals = ExtractionCapacity.DeriveGroundSignals(orders,
                st.ExtractionGroundSignals, memory, now, tuning != null ? tuning.ExtractionGroundGraceSeconds : 0);
            st.ExtractionOrders.Clear();
            if (orders != null) st.ExtractionOrders.AddRange(orders);
            st.ExtractionGroundSignals.Clear();
            st.ExtractionGroundSignals.AddRange(signals);

            if (DebugTraces.Extraction && addLog != null)
                foreach (ExtractionGroundSignal s in signals)
                    addLog(ExtractionCapacity.FormatGroundTrace(st.Tag, s));
        }

        /// <summary>The orders this pass dispatches for, neediest first: open (not paused) with a
        /// positive target rate, largest gap first, then hub and ore for a stable order under both
        /// contexts. Empty when there are none -- the common case, which lets a faction with no
        /// demand, no player order and nothing to extract keep its early-out.</summary>
        private static List<StandingExtractionOrder> DispatchableExtractionOrders(List<StandingExtractionOrder> orders)
        {
            if (orders == null || orders.Count == 0) return NoExtractionOrders;
            List<StandingExtractionOrder> open = null;
            foreach (StandingExtractionOrder o in orders)
                if (!o.Paused && o.TargetRateLitersPerHour > 0)
                    (open ?? (open = new List<StandingExtractionOrder>())).Add(o);
            if (open == null) return NoExtractionOrders;
            open.Sort(CompareExtractionOrdersForDispatch);
            return open;
        }

        private static int CompareExtractionOrdersForDispatch(StandingExtractionOrder a, StandingExtractionOrder b)
        {
            int cmp = b.GapPerHour.CompareTo(a.GapPerHour);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(a.HubNodeId, b.HubNodeId);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.OreKey, b.OreKey);
        }

        /// <summary><c>order=</c> token of an extraction order's trips: <c>&lt;hub&gt;:&lt;ore&gt;</c>.</summary>
        internal static string ExtractionOrderTag(StandingExtractionOrder order)
        {
            return order.HubNodeId + ":" + order.OreKey;
        }

        /// <summary>Walks one open extraction order's catchment (<see cref="WalkMineCatchment"/>):
        /// every deposit within <see cref="FactionTuning.MineSearchRadiusKm"/> of the hub that yields
        /// the order's ore, richest first, one full-hold trip per free deposit, composition the
        /// order's ore alone.
        ///
        /// <para><b>The limit is the target rate.</b> Before each candidate the walk compares the
        /// extraction rate of the Mine trips already bound for the hub with this ore -- prior cycles'
        /// and this pass's, each its target volume over its dwell
        /// (<see cref="FactionTickContext.MineRateLitersPerHour"/>) -- against
        /// <see cref="StandingExtractionOrder.TargetRateLitersPerHour"/>, and stops once it is met.
        /// Delivered ore is not subtracted a second time: it is already in the planner's gap, which
        /// opens the order, and in the stock its ceiling pauses it. Closing the in-flight rate
        /// against <c>target - mined</c> instead would settle at half the target, since a trip in
        /// flight and the same trip delivered would each be counted.</para>
        ///
        /// <para>No hub slot cap, unlike a player order: the one-trip-per-deposit rule
        /// (<see cref="FactionTickContext.WorkedMineSources"/>, the pass's claims) is what bounds
        /// concurrency, and it needs no <see cref="CountInFlightMineFleetsToward"/> scan. The walk
        /// ends at the first trip that finds no idle drill anywhere; that trip has staged the
        /// <c>Ship:Mine</c> fallback and, for a faction that owns no drill at all, minted the
        /// FAC-204 bootstrap.</para></summary>
        private void ScanExtractionOrder(FactionState st, StandingExtractionOrder order, StandingMineScanPass pass,
            List<SpatialNode> ownedNodes, IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning,
            FactionTickContext context, bool allowBootstrap)
        {
            ExtractionOrderWalk walk = BeginExtractionWalk(st, order, pass, ownedNodes, ctx, world, tuning, context,
                allowBootstrap);
            if (walk != null) StepExtractionWalk(st, walk, pass, ownedNodes, ctx, world, tuning, context, 0);
        }

        /// <summary>One extraction order's walk in progress: what <see cref="ScanExtractionOrder"/>
        /// does in one call, held so the staged cycle can spread it over several steps
        /// (<see cref="FactionTuning.ExtractionMineAttemptsPerStep"/>).</summary>
        private sealed class ExtractionOrderWalk
        {
            internal StandingExtractionOrder Order;
            internal string Tag;
            internal List<SpatialNode> Catchment;
            internal MineCatchmentWalk Walk;
            internal double InFlightAtStart;
        }

        /// <summary>Opens <paramref name="order"/>'s walk: resolves the hub and builds its catchment
        /// from the pass's indexes (<see cref="StandingMineScanPass.ExtractionCatchment"/>). Null,
        /// with the <c>hub_unavailable</c> trace, when the hub is gone or no longer this faction's.</summary>
        private ExtractionOrderWalk BeginExtractionWalk(FactionState st, StandingExtractionOrder order,
            StandingMineScanPass pass, List<SpatialNode> ownedNodes, IOperationsExecutionContext ctx, WorldModel world,
            FactionTuning tuning, FactionTickContext context, bool allowBootstrap)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            string tag = ExtractionOrderTag(order);
            SpatialNode hub;
            if (string.IsNullOrEmpty(order.HubNodeId)
                || !world.Graph.Nodes.TryGetValue(order.HubNodeId, out hub)
                || !string.Equals(hub.OwnerFactionTag, st.Tag, StringComparison.OrdinalIgnoreCase))
            {
                if (DebugTraces.Extraction)
                    ctx.AddLog(st.Tag + " MINE_ORDER_SCAN order=" + tag + " hub_unavailable");
                _lastTelemetry.ExtractionWalkTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                return null;
            }

            var weights = new Dictionary<string, double>(StringComparer.Ordinal) { { order.OreKey, 1.0 } };
            var walk = new ExtractionOrderWalk
            {
                Order = order,
                Tag = tag,
                Catchment = pass.ExtractionCatchment(hub, order.OreKey, weights, ownedNodes, tuning, world.Registry),
                InFlightAtStart = context.MineRateToward(order.HubNodeId, order.OreKey),
                Walk = new MineCatchmentWalk
                {
                    OrderId = tag,
                    HubNodeId = order.HubNodeId,
                    Weights = weights,
                    RateOreKey = order.OreKey,
                    TargetLitersPerHour = order.TargetRateLitersPerHour,
                    StopOnNoShip = true,
                    AllowBootstrap = allowBootstrap,
                    OrderTag = tag,
                },
            };
            _lastTelemetry.ExtractionWalkTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            return walk;
        }

        /// <summary>Continues <paramref name="walk"/> for at most <paramref name="attemptBudget"/>
        /// Mine trips (0: to the end; see <see cref="WalkMineCatchment"/>). Returns true once the walk
        /// is over, after its <c>MINE_ORDER_SCAN</c> trace.</summary>
        private bool StepExtractionWalk(FactionState st, ExtractionOrderWalk walk, StandingMineScanPass pass,
            List<SpatialNode> ownedNodes, IOperationsExecutionContext ctx, WorldModel world, FactionTuning tuning,
            FactionTickContext context, int attemptBudget)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            int attemptsBefore = walk.Walk.Attempts;
            bool done = WalkMineCatchment(st, walk.Catchment, walk.Walk, pass, ownedNodes, ctx, world, tuning, context,
                attemptBudget);
            _lastTelemetry.ExtractionMineAttempts += walk.Walk.Attempts - attemptsBefore;

            if (done && DebugTraces.Extraction)
                ctx.AddLog(st.Tag + " MINE_ORDER_SCAN order=" + walk.Tag
                    + " target=" + ExtractionPlanner.Compact(walk.Order.TargetRateLitersPerHour)
                    + " inflight=" + ExtractionPlanner.Compact(walk.InFlightAtStart)
                    + " catchment=" + walk.Catchment.Count
                    + " dispatched=" + walk.Walk.Dispatched.ToString(CultureInfo.InvariantCulture)
                    + (walk.Walk.Tokens.Length > 0 ? " [" + walk.Walk.Tokens + "]" : ""));
            _lastTelemetry.ExtractionWalkTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            return done;
        }
    }
}
