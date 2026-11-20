using Factions.Core;

namespace Factions
{
    internal sealed class OperationsTelemetryWindow
    {
        public int Calls;
        public long Factions, OwnedNodes, Demands, ItemGroups, Targets, CandidateSources, DistanceOrderBuilds, Vectors;
        public long RouteCacheHits, RouteCacheMisses, DirectDispatches, PickupDispatches, NoShipOutcomes, RouteInfeasibleOutcomes, LoadCargoFailures;
        public long AlternateSourceScans, ShipyardScans, ShipSuitabilityChecks, FallbackCandidates, UniqueFallbackKeys, DemandPosts, DemandRefreshes;
        public long PreparationTicks, DemandHistoryTicks, ClaimIndexTicks, TargetMatchingTicks, RoutePlanningTicks, DirectDispatchTicks, AlternatePickupTicks, AlternateScanTicks, AlternateRevalidateTicks, ShipyardFallbackTicks, FallbackPublicationTicks;
        // Slice 3 resumable-cycle counters. Unlike every field above (recorded once per completed
        // sweep, same as Calls), these accumulate the *whole sweep's* Yields/Restarts/SweepsCompleted
        // from OperationsTickTelemetry as of the single completion-time Record call — see
        // FactionRuntime.DrainManagerPhase's case 1 for why Record is only ever called on completion.
        public long Yields, Restarts, SweepsCompleted;
        public void Record(OperationsTickTelemetry v)
        {
            Calls++; Factions += v.Factions; OwnedNodes += v.OwnedNodes; Demands += v.Demands; ItemGroups += v.ItemGroups; Targets += v.Targets; CandidateSources += v.CandidateSources; DistanceOrderBuilds += v.DistanceOrderBuilds; Vectors += v.Vectors;
            RouteCacheHits += v.RouteCacheHits; RouteCacheMisses += v.RouteCacheMisses; DirectDispatches += v.DirectDispatches; PickupDispatches += v.PickupDispatches; NoShipOutcomes += v.NoShipOutcomes; RouteInfeasibleOutcomes += v.RouteInfeasibleOutcomes; LoadCargoFailures += v.LoadCargoFailures;
            AlternateSourceScans += v.AlternateSourceScans; ShipyardScans += v.ShipyardScans; ShipSuitabilityChecks += v.ShipSuitabilityChecks; FallbackCandidates += v.FallbackCandidates; UniqueFallbackKeys += v.UniqueFallbackKeys; DemandPosts += v.DemandPosts; DemandRefreshes += v.DemandRefreshes;
            PreparationTicks += v.PreparationTicks; DemandHistoryTicks += v.DemandHistoryTicks; ClaimIndexTicks += v.ClaimIndexTicks; TargetMatchingTicks += v.TargetMatchingTicks; RoutePlanningTicks += v.RoutePlanningTicks; DirectDispatchTicks += v.DirectDispatchTicks; AlternatePickupTicks += v.AlternatePickupTicks; AlternateScanTicks += v.AlternateScanTicks; AlternateRevalidateTicks += v.AlternateRevalidateTicks; ShipyardFallbackTicks += v.ShipyardFallbackTicks; FallbackPublicationTicks += v.FallbackPublicationTicks;
            Yields += v.Yields; Restarts += v.Restarts; SweepsCompleted += v.SweepsCompleted;
        }
        public void Reset()
        {
            Calls = 0; Factions = OwnedNodes = Demands = ItemGroups = Targets = CandidateSources = DistanceOrderBuilds = Vectors = 0;
            RouteCacheHits = RouteCacheMisses = DirectDispatches = PickupDispatches = NoShipOutcomes = RouteInfeasibleOutcomes = LoadCargoFailures = 0;
            AlternateSourceScans = ShipyardScans = ShipSuitabilityChecks = FallbackCandidates = UniqueFallbackKeys = DemandPosts = DemandRefreshes = 0;
            PreparationTicks = DemandHistoryTicks = ClaimIndexTicks = TargetMatchingTicks = RoutePlanningTicks = DirectDispatchTicks = AlternatePickupTicks = AlternateScanTicks = AlternateRevalidateTicks = ShipyardFallbackTicks = FallbackPublicationTicks = 0;
            Yields = Restarts = SweepsCompleted = 0;
        }
    }
}
