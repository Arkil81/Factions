using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>One station design a node could be rebuilt as, with the numbers a player needs to
    /// choose between them. Structured rather than rendered, because two surfaces format it: the
    /// chat verb prints it, and the refit panel puts each field in its own column.</summary>
    public sealed class StationRefitCandidate
    {
        public string PrefabId;
        /// <summary>True for a player capture, false for a design from the content walk.</summary>
        public bool Captured;
        /// <summary>"Surface" or "Space", as the catalogue recorded it.</summary>
        public string Site;
        public int BlockCount;
        public double Dps;
        public long Cost;
        /// <summary>False when the design has no registered recipe, which is not the same as an
        /// empty bill: the first is a design nothing can price, the second one the node already
        /// stands as everything of.</summary>
        public bool HasRecipe;
        /// <summary>The net bill against the design the node stands as now — per component, what
        /// the target wants beyond what is already here.</summary>
        public Dictionary<string, long> NetBom = new Dictionary<string, long>();
        /// <summary>Summed units of <see cref="NetBom"/>. Meaningless when
        /// <see cref="HasRecipe"/> is false.</summary>
        public long NetBomUnits;
        /// <summary>The design this node stands as right now.</summary>
        public bool Current;
    }

    /// <summary>Everything both refit surfaces read about one node: what it stands as, what it is
    /// becoming, and what it could become. <see cref="Refusal"/> is the single refusal sentence when
    /// the node cannot be refitted at all, and null otherwise.</summary>
    public sealed class StationRefitOverview
    {
        public string NodeId;
        /// <summary>Null when this faction may refit this node. A sentence naming the gate
        /// otherwise, in the words the chat verb answers with.</summary>
        public string Refusal;
        public string CurrentPrefabId;
        /// <summary>A rebuild that is paid for and waiting to be stood up, or null.</summary>
        public string PendingPrefabId;
        public StationSite Site;
        /// <summary>A siege is running here, so every refit is refused until it ends.</summary>
        public bool SiegeBlocked;
        /// <summary>Somebody is standing at this station, so a finished rebuild is held rather than
        /// applied.</summary>
        public bool Observed;
        /// <summary>The design a queued rebuild is collecting toward, or null.</summary>
        public string CollectingPrefabId;
        public long CollectedUnits;
        public long CollectingBomUnits;
        /// <summary>Empty when this faction has no design for this node's site yet — which is a
        /// state to report, not a refusal.</summary>
        public List<StationRefitCandidate> Candidates = new List<StationRefitCandidate>();
    }
}
