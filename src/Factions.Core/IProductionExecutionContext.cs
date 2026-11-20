using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Seam between ProductionManager's tick logic and its WorldModel effects. A
    /// <see cref="LiveProductionExecutionContext"/> passes every call straight through to a
    /// <see cref="WorldModel"/>, reproducing today's synchronous behavior exactly. A
    /// <see cref="StagedProductionExecutionContext"/> records effects privately in a
    /// <see cref="ProductionStagedWorldEffects"/> journal until a guarded commit publishes them.
    ///
    /// <para><see cref="UtcNow"/> is frozen at construction so every call site uses the same instant —
    /// matching <see cref="ProductionCycleCommitGuard.CapturedUtc"/> — instead of re-reading
    /// <c>WorldModel.UtcNow</c> mid-cycle, which today several helpers do independently of the `now`
    /// value <c>TickCore</c> captures once at the top of its per-faction loop.</para>
    /// </summary>
    public interface IProductionExecutionContext
    {
        /// <summary>The cycle's single frozen instant. Never re-reads a live clock.</summary>
        System.DateTime UtcNow { get; }

        /// <summary>Registry used to classify inventory keys into <see cref="ItemKind"/> buckets.</summary>
        IItemRegistry Registry { get; }

        /// <summary>The faction's owned nodes. Read-only node metadata (capabilities, build queue,
        /// position, resource value, hold strength) is read directly off the returned live
        /// <see cref="SpatialNode"/> — it is not staged, matching <see cref="ProductionCycleCommitGuard"/>'s
        /// own invalidate-on-any-change treatment of that same metadata.</summary>
        IEnumerable<SpatialNode> OwnedNodes(string factionTag);

        /// <summary>The world's bodies, for what a node's body decides — which crops grow wild there
        /// (<see cref="FarmCrops"/>).</summary>
        IPlanetCatalogue Planets { get; }

        /// <summary>Influence control at a position — a world-wide read, never staged.</summary>
        double InfluenceControl(Vec3 position, string factionTag);

        bool TryGetInventoryItemKind(string inventoryKey, out ItemKind kind);

        long GetNodeInventory(string nodeId, string inventoryKey);
        long GetReservedInventory(string nodeId, string inventoryKey);
        IEnumerable<KeyValuePair<string, long>> SnapshotNodeInventory(string nodeId);
        IEnumerable<KeyValuePair<string, long>> SnapshotReservedInventory(string nodeId);
        void AddNodeInventory(string nodeId, string inventoryKey, long delta);
        void AddReservedInventory(string nodeId, string inventoryKey, long delta);
        long MoveInventoryToReserved(string nodeId, string inventoryKey, long quantity);
        long MoveReservedToInventory(string nodeId, string inventoryKey, long quantity);

        /// <summary>Per-<see cref="BuildQueueEntry"/> mutable state (<see cref="BuildQueueEntry.Collected"/>,
        /// <see cref="BuildQueueEntry.ReservedInputs"/>, <see cref="BuildQueueEntry.RecipeProgress"/>),
        /// identified by <paramref name="nodeId"/> + <paramref name="entryId"/> (<see cref="BuildQueueEntry.Id"/>).
        /// Everything else on a <see cref="BuildQueueEntry"/> (Bom, Role, PrefabId, ExpiresUtc) is
        /// read-only metadata read directly off the live <see cref="SpatialNode"/>, same as <see cref="OwnedNodes"/>'s
        /// doc note — only these three fields are staged, since they're the only ones this cycle mutates.</summary>
        long GetBuildCollected(string nodeId, string entryId, string invKey);
        void AddBuildCollected(string nodeId, string entryId, string invKey, long delta);
        long GetBuildReservedInput(string nodeId, string entryId, string invKey);
        /// <summary>Consumes (negative delta) or restores (positive) reserved input already in this
        /// entry's own bucket — unlike <see cref="MoveInventoryToBuildReserved"/>, this never touches
        /// routable <c>Inventory</c> on either side; a negative delta represents the reserved stock
        /// being transformed into recipe output, not returned anywhere.</summary>
        void AddBuildReservedInput(string nodeId, string entryId, string invKey, long delta);
        IEnumerable<KeyValuePair<string, long>> SnapshotBuildReservedInputs(string nodeId, string entryId);

        /// <summary>Physically separates up to <paramref name="quantity"/> of a node's routable
        /// inventory into one build entry's own <see cref="BuildQueueEntry.ReservedInputs"/> bucket —
        /// the <see cref="BuildQueueEntry"/>-scoped counterpart to <see cref="MoveInventoryToReserved"/>.
        /// Returns the amount actually moved.</summary>
        long MoveInventoryToBuildReserved(string nodeId, string entryId, string invKey, long quantity);

        /// <summary>This entry's own dedicated production progress for one recipe (mirrors
        /// <see cref="BuildQueueEntry.RecipeProgress"/>'s <c>Accum</c>/<c>LastTick</c> shape).</summary>
        double GetBuildRecipeAccum(string nodeId, string entryId, string blueprintId, out System.DateTime lastTick);
        void SetBuildRecipeProgress(string nodeId, string entryId, string blueprintId, double accum, System.DateTime lastTick);

        /// <summary>Credits a real recipe-input consumption toward <see cref="SpatialNode.DemandHistory"/>'s
        /// EMA (docs/material-vectors.md's DemandHistory addendum, dated correction note) -- the gravity
        /// term is fed by actual consumption, not delivery, so a staged cycle that never commits must
        /// never let this reach <see cref="WorldModel"/>, same discipline as <see cref="AddNodeInventory"/>.</summary>
        void AddPendingDemandHistoryCredit(string nodeId, string itemKey, double weightedQty);

        long GetFactionInventory(string factionTag, ItemKind kind);
        long GetFactionReservedInventory(string factionTag, ItemKind kind);
        long GetFactionInventoryByKey(string factionTag, string inventoryKey);
        long GetFactionReservedInventoryByKey(string factionTag, string inventoryKey);

        /// <summary>Active Item: demands, including anything <see cref="PostDemand"/> already staged
        /// earlier in this same cycle — matches live <c>DemandRegister</c>'s same-call visibility.</summary>
        List<DemandEntry> SnapshotItemDemands(string factionTag);
        bool PostDemand(DemandEntry demand);

        /// <summary>Active investment requests, including anything <see cref="PostInvestment"/> already
        /// staged earlier in this same cycle.</summary>
        List<InvestmentRequest> SnapshotInvestments(string factionTag);
        bool PostInvestment(InvestmentRequest investment);

        void AddLog(string message);
    }
}
