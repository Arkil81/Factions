using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>The two-way split a station design is offered on: a grid either stands on terrain or
    /// it does not. <c>StationFounder.PickPrefab</c> chooses a founding prefab on exactly this
    /// split, and a refit has to agree with it — an outpost hung in orbit and a space station sunk
    /// into a hillside are the two failures this prevents.
    ///
    /// <para>The catalogue records where a design was measured standing, which may be any of the
    /// three <see cref="StationSite"/> values; both sides are collapsed here rather than at the
    /// point of capture, so an orbital scan is offered at every non-surface node instead of at
    /// none.</para></summary>
    public static class StationPrefabSite
    {
        public const string Surface = "Surface";
        public const string Space = "Space";

        /// <summary>The token a node of this site is refitted from.</summary>
        public static string For(StationSite site)
        {
            return site == StationSite.Surface ? Surface : Space;
        }

        /// <summary>Which site a station prefab belongs to, or null when nothing can say. The vanilla
        /// families name it outright; anything else is read from <paramref name="seListedSites"/>,
        /// SE's own per-station-type lists, rather than guessed at.</summary>
        public static string OfPrefab(string prefabId, IDictionary<string, string> seListedSites)
        {
            if (string.IsNullOrEmpty(prefabId)) return null;
            if (prefabId.IndexOf("Outpost", StringComparison.OrdinalIgnoreCase) >= 0) return Surface;
            if (prefabId.IndexOf("SpaceStation", StringComparison.OrdinalIgnoreCase) >= 0) return Space;
            string site;
            return seListedSites != null && seListedSites.TryGetValue(prefabId, out site) ? site : null;
        }

        /// <summary>Whether a design recorded at <paramref name="prefabSite"/> may stand at a node
        /// sited at <paramref name="nodeSite"/>. A design with no recorded site matches nothing:
        /// where it was built is measured, never assumed.</summary>
        public static bool Matches(string prefabSite, StationSite nodeSite)
        {
            if (string.IsNullOrEmpty(prefabSite)) return false;
            bool prefabOnGround = string.Equals(prefabSite, Surface, StringComparison.OrdinalIgnoreCase);
            return prefabOnGround == (nodeSite == StationSite.Surface);
        }
    }

    /// <summary>Why a station refit was refused, or <see cref="Ok"/>. Mirrors
    /// <see cref="DevelopStationResult"/>'s shape so the host command maps a verdict to text without
    /// string-sniffing.</summary>
    public enum StationRefitResult
    {
        Ok,
        NodeNotFound,
        NotOwned,
        /// <summary>No station stands here yet — <c>station found</c> builds the first one, free.</summary>
        NotFounded,
        /// <summary>No such design in the catalogue, or the catalogue holds no station designs at all.</summary>
        UnknownPrefab,
        /// <summary>A known design, but a ship hull rather than a station.</summary>
        NotStationPrefab,
        /// <summary>A capture restricted to another faction.</summary>
        PrefabNotUsable,
        /// <summary>A surface design at a node in space, or the reverse.</summary>
        SiteMismatch,
        /// <summary>The node already stands as this design.</summary>
        AlreadyThisPrefab,
        /// <summary>This design is already built and paid for, waiting for the host to stand it up
        /// (<see cref="SpatialNode.PendingStationPrefabId"/>). Charging the bill a second time is
        /// what this refuses; <c>station refit apply</c> is what the player wants.</summary>
        AlreadyPending,
        /// <summary>The design has no build recipe, so there is no bill to charge for it.</summary>
        NoRecipe,
        /// <summary>A siege is running here — the walls are being shot at.</summary>
        SiegeInProgress,
        /// <summary>A ship build at this node is mid-collection and would lose the working slot.
        /// Passing <c>force</c> queues the refit anyway; the ship build keeps everything it has
        /// collected and resumes once the refit is done.</summary>
        BuildInProgress,
        /// <summary>A refit is already queued at this node. Passing <c>force</c> replaces it and
        /// refunds what it had collected.</summary>
        RefitInProgress,
    }

    /// <summary>
    /// Player-triggered station-refit readiness — whether this faction may rebuild this founded node
    /// as this design, and what that costs from where the node stands. Shaped like
    /// <see cref="DevelopStationValidator"/>: pure policy, single-sourced, offline-testable, and
    /// called by both the verb and any preview beside it so the two can never disagree.
    ///
    /// <para><b>What it charges.</b> The net bill (<see cref="ShipyardManager.NetRefitBom"/>) — per
    /// component, what the target design wants beyond what the current one already stands as.
    /// Surplus in the old design is not refunded: that is what grinding a station down and
    /// rebuilding in place would lose, and refunding it would make refit-cycling a component
    /// pump.</para>
    ///
    /// <para><b>A design whose current prefab has no recipe pays gross.</b> The current side of the
    /// diff resolves through the same <c>Prefab/&lt;id&gt;</c> registry the target does, and an
    /// unregistered current design contributes nothing to subtract. That errs toward charging more,
    /// never less.</para>
    ///
    /// <para><b>A held rebuild is not the current design.</b> A refit that finished while the
    /// station was being watched leaves <see cref="SpatialNode.PendingStationPrefabId"/> set and the
    /// node still physically standing as <see cref="SpatialNode.StationPrefabId"/>. Asking for the
    /// held design again is refused (<see cref="StationRefitResult.AlreadyPending"/>) rather than
    /// billed twice. Asking for a THIRD design is allowed, and its bill is the difference from what
    /// stands now — never from the held one, which was never built in world and whose components
    /// are already spent. Queueing it supersedes the held rebuild.</para>
    /// </summary>
    public static class RefitStationValidator
    {
        /// <summary>Runs every gate in order and reports the first refusal.
        /// <paramref name="netBom"/> is the bill the refit would be queued with (empty when the
        /// target needs nothing the node does not already stand as — a refit that completes on the
        /// spot); <paramref name="blocking"/> is the queue entry behind
        /// <see cref="StationRefitResult.BuildInProgress"/>/<see cref="StationRefitResult.RefitInProgress"/>,
        /// and null for every other verdict.</summary>
        public static StationRefitResult Validate(WorldModel world, string factionTag, string nodeId,
            string prefabId, bool force, out SpatialNode node, out Dictionary<string, long> netBom,
            out BuildQueueEntry blocking)
        {
            node = null;
            netBom = new Dictionary<string, long>(StringComparer.Ordinal);
            blocking = null;
            if (world == null) return StationRefitResult.NodeNotFound;

            SpatialNode found;
            if (string.IsNullOrEmpty(nodeId) || !world.Graph.Nodes.TryGetValue(nodeId, out found))
                return StationRefitResult.NodeNotFound;
            node = found;
            if (!string.Equals(node.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase))
                return StationRefitResult.NotOwned;
            if (node.StableStationId == 0)
                return StationRefitResult.NotFounded;

            if (string.IsNullOrEmpty(prefabId)) return StationRefitResult.UnknownPrefab;
            // A catalogue that cannot answer "is this a station" refuses every refit rather than
            // letting a fleet hull through as one. See IStationPrefabCatalogue.
            IStationPrefabCatalogue stations = world.PrefabCatalogue as IStationPrefabCatalogue;
            if (stations == null) return StationRefitResult.UnknownPrefab;
            if (!stations.IsStationPrefab(prefabId))
            {
                // A hull the catalogue knows is a different refusal from one it has never heard of:
                // the player picked a real prefab, just not a station.
                return world.PrefabCatalogue != null
                       && !string.Equals(world.PrefabCatalogue.GridSize(prefabId), "Unknown", StringComparison.Ordinal)
                    ? StationRefitResult.NotStationPrefab
                    : StationRefitResult.UnknownPrefab;
            }

            IOwnedPrefabCatalogue owned = world.PrefabCatalogue as IOwnedPrefabCatalogue;
            if (owned != null)
            {
                string ownerTag = owned.OwnerFactionTag(prefabId);
                if (!string.IsNullOrEmpty(ownerTag)
                    && !string.Equals(ownerTag, factionTag, StringComparison.OrdinalIgnoreCase))
                    return StationRefitResult.PrefabNotUsable;
            }

            if (!StationPrefabSite.Matches(stations.Site(prefabId), SiteOf(world, node)))
                return StationRefitResult.SiteMismatch;

            if (string.Equals(node.StationPrefabId, prefabId, StringComparison.OrdinalIgnoreCase))
                return StationRefitResult.AlreadyThisPrefab;
            // Paid for and waiting to be stood up. The node still stands as the OLD design, so every
            // gate below would price this as a fresh rebuild and charge the net bill again.
            if (string.Equals(node.PendingStationPrefabId, prefabId, StringComparison.OrdinalIgnoreCase))
                return StationRefitResult.AlreadyPending;

            bool targetHasRecipe;
            netBom = ShipyardManager.NetRefitBom(world.Recipes, node.StationPrefabId, prefabId,
                out targetHasRecipe);
            if (!targetHasRecipe)
            {
                netBom = new Dictionary<string, long>(StringComparer.Ordinal);
                return StationRefitResult.NoRecipe;
            }

            if (IsUnderSiege(node, world.UtcNow))
                return StationRefitResult.SiegeInProgress;

            for (int i = 0; i < node.BuildQueue.Count; i++)
            {
                BuildQueueEntry existing = node.BuildQueue[i];
                if (existing == null) continue;
                if (!ShipyardManager.IsStationRefit(existing)) continue;
                blocking = existing;
                // Force replaces it, refunding what it had collected -- the same override
                // ShipyardManager.QueueBuild does for an occupied (node, role) slot. Without force a
                // second request is refused rather than silently restarting a half-collected
                // rebuild, including a repeat of the same design.
                if (!force) return StationRefitResult.RefitInProgress;
            }

            for (int i = 0; i < node.BuildQueue.Count; i++)
            {
                BuildQueueEntry existing = node.BuildQueue[i];
                if (existing == null || ShipyardManager.IsStationRefit(existing)) continue;
                if (!node.IsActiveBuild(existing)) continue;
                if (existing.Collected.Count == 0) continue;   // holding nothing; nothing to displace
                blocking = existing;
                if (!force) return StationRefitResult.BuildInProgress;
                break;
            }

            return StationRefitResult.Ok;
        }

        /// <summary>Where this node stands, asked of the world's own geometry when it is available
        /// and read off the node's cached classification otherwise — the Core-side counterpart of
        /// <c>StationFounder.SiteFor</c>, so the refit gate and the founding pool answer the same
        /// question the same way.</summary>
        public static StationSite SiteOf(WorldModel world, SpatialNode node)
        {
            if (node == null) return StationSite.Space;
            if (world == null || world.Planets == null) return node.Site;
            return StationSiting.Classify(world.Planets, node.Position);
        }

        /// <summary>Whether the node's zone is currently being fought over. Two halves, because
        /// either alone is blind to the other's case: a player siege stamps
        /// <see cref="SpatialNode.SiegeStartedUtc"/> the moment the first shot lands, long before
        /// the pool empties, and <see cref="PlayerSiege.ZoneShouldBeEnabled"/> is the one question
        /// that folds every reason the zone is down — a breach held open, and an NPC raid that
        /// landed — so neither kind of attacker gets to be rebuilt through.</summary>
        public static bool IsUnderSiege(SpatialNode node, DateTime now)
        {
            if (node == null) return false;
            return node.SiegeStartedUtc != default(DateTime)
                || !PlayerSiege.ZoneShouldBeEnabled(node, now);
        }
    }
}
