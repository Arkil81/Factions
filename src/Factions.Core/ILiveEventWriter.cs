using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Emits real-time events to an external consumer (e.g. the dashboard API).
    /// Implementations must be thread-safe; FactionRuntime calls from the game update thread.</summary>
    public interface ILiveEventWriter
    {
        /// <summary>Called immediately after a fleet is added to PendingFleets.</summary>
        void WriteFleetDispatch(string factionTag, string fleetId, string kind,
                                string originNodeId, string targetNodeId, string targetTag);

        /// <summary>Called on each flush with a full snapshot of active demands/investments per faction.
        /// Demands arrive as <see cref="DemandSnapshotRow"/>, ranked, so the consumer reads the same
        /// urgency and node stock the ranking placed them by rather than a second reading of its own.
        /// Investments arrive as <see cref="InvestmentSignal"/> so the consumer sees the node each
        /// request resolves to (or why it resolves to none).</summary>
        void WriteDemandsSnapshot(string factionTag,
                                  List<DemandSnapshotRow> demands,
                                  List<InvestmentSignal> investments);

        /// <summary>Called on each flush as a keepalive tick.</summary>
        void WriteHeartbeat(long simTick);

        /// <summary>Called when a faction loses a node without an explicit action of its own —
        /// currently hold decay handing it to UNKN (<paramref name="cause"/> = "decay").</summary>
        void WriteTerritoryLoss(string factionTag, string nodeId, string cause);
    }
}
