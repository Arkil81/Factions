namespace Factions.Core
{
    /// <summary>Throughput for a single recipe running on one or more machine slots at a node,
    /// produced by <see cref="ProductionManager.SnapshotThroughput"/>. Reports capacity and reality
    /// side by side: a bank sitting far below its nominal rate is starved of input, and only the
    /// pair makes that visible.</summary>
    public struct ThroughputEntry
    {
        public string             FactionTag;
        public string             NodeId;
        public NodeCapabilityKind Cap;
        /// <summary>Inventory key of the primary input (e.g. "Ore:Iron").</summary>
        public string             PrimaryInputId;
        /// <summary>Inventory key of the primary output (e.g. "Ingot:Iron").</summary>
        public string             PrimaryOutputId;
        /// <summary>Capacity in items/minute for the primary output across all slots on this recipe:
        /// effective machine speed × slot count × yield scaling. Says nothing about whether the
        /// inputs to sustain it exist — compare against <see cref="ObservedItemsPerMinute"/>.</summary>
        public double             NominalItemsPerMinute;
        /// <summary>What this recipe actually put into inventory per minute over the production
        /// history window, from the recorded spans rather than from the rate model.</summary>
        public double             ObservedItemsPerMinute;
        /// <summary>Per-machine speed actually in force: <c>(BaseSpeed + Productivity) ×
        /// WorldSpeedMultiplier</c>. Exposed so a rate can be checked against the world it came from.</summary>
        public double             EffectiveMachineSpeed;
        /// <summary>Yield scaling applied to every output (SE <c>MaterialEfficiency</c>).</summary>
        public double             OutputScale;
        /// <summary>Number of machine slots currently assigned to this recipe.</summary>
        public int                SlotCount;
    }
}
