using System;

namespace Factions.Core
{
    /// <summary>Read-only world-level monetary snapshot. Slice A exposes central-bank observability
    /// before any policy operations are allowed to inject or withdraw liquidity.</summary>
    public struct CentralBankSnapshot
    {
        public DateTime UtcNow;
        public long CurrentMoneySupply;
        /// <summary>Instantaneous collateral: what the world's assets are worth right now.</summary>
        public long WorldCollateralValue;
        /// <summary>Collateral the target is actually set against. Equals
        /// <see cref="WorldCollateralValue"/> unless collateral has fallen recently, in which case it
        /// lies above it and is closing the distance by <c>CentralBankAnchorHalfLife</c>.</summary>
        public long AnchorCollateralValue;
        /// <summary>The last completed income pass's policy: "expand" (gap positive), "hold" (at or
        /// above target but inside the contraction band) or "contract". Empty before the first pass.</summary>
        public string LastPolicyMode;
        /// <summary>Treasury the last pass levied (positive), contraction and rescue funding together.</summary>
        public long LastPolicyLevied;
        /// <summary>Grants plus support-loan principal the last pass issued.</summary>
        public long LastPolicyRescued;
        public long TargetMoneySupply;
        public long MoneySupplyGap;
        public double ReserveRatio;
        public int TerritoryNodeCount;
        public int FoundedNodeCount;
        public int CapabilityLevelCount;
        /// <summary>Hulls parked in a node's <see cref="SpatialNode.ShipStock"/> (store-listed ones
        /// included). Falls every time a fleet departs and rises again when it returns, so it is a
        /// snapshot of what is idle right now, not of what the world owns.</summary>
        public int IdleShipCount;
        /// <summary>Hulls aboard unresolved <see cref="FactionState.PendingFleets"/>. A resolved
        /// fleet is excluded: its ships have already been credited back to
        /// <see cref="SpatialNode.ShipStock"/> and would otherwise be counted twice.</summary>
        public int InFlightShipCount;
        /// <summary>Every hull the world owns: <see cref="IdleShipCount"/> +
        /// <see cref="InFlightShipCount"/>. Stable across dispatch and return — a ship leaving port
        /// moves between the two components without changing this total.</summary>
        public int ShipCount;
    }

    /// <summary>One faction's hulls, from <see cref="WorldModel.TallyShipsByFaction"/>.</summary>
    public struct ShipTally
    {
        /// <summary>Parked in <see cref="SpatialNode.ShipStock"/> at nodes the faction owns.</summary>
        public int Idle;
        /// <summary>Aboard the faction's unresolved fleets.</summary>
        public int InFlight;
        public int Total { get { return Idle + InFlight; } }
    }
}
