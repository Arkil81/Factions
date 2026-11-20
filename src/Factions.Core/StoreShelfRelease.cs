using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Hands reservations back. <see cref="SpatialNode.ListedForSale"/> is a hard floor under
    /// <see cref="ShipPool.Available"/>, so a hull reserved for a reason that has since lapsed is
    /// not merely idle — it is out of fleet dispatch permanently, and off every shelf too if no
    /// station can sell it. That is the stale-reservation failure this repo has already fixed twice
    /// on the production side (<c>ProductionManager.ReserveInstallStock</c>'s release phase), and a
    /// reservation function that only ever adds re-creates it.
    ///
    /// <para><b>Anti-churn is structural, and it has to cover every reason, not one.</b> Stocking
    /// stops at the slot target and releasing does not begin until
    /// <see cref="FactionTuning.StoreShelfReleaseMarginSlots"/> above it, so there is a dead band in
    /// which neither fires.
    ///
    /// <para>The first cut of this class had "a node needs this hull" as its own release reason,
    /// outside that band, on the reasoning that releasing satisfies the demand because the hull gets
    /// dispatched. It does not: release only makes the hull <i>available</i>, and
    /// <c>ShipyardManager.PostProactiveShipDemands</c> re-posts that demand every cadence while the
    /// zone still has no route-feasible stock. So the next pass released the next hull. Live on
    /// v0.145.0 that drained three factions' shelves one hull per pass over 45 minutes and emptied
    /// IGSL's outright. A node need is therefore a <b>priority within</b> the over-target release —
    /// it decides WHICH hull is handed back when the shelf can spare one, never WHETHER one is.</para></para>
    ///
    /// <para><b>A hand-back has to leave the hull somewhere useful.</b> The over-target drain skips a
    /// prefab whose dry TWR fails at the node holding it: dropping that reservation does not return a
    /// hull to the fleet, it puts one in a pool that can never dispatch it, at a node it can never
    /// leave. The shelf keeps it, still on sale, because a buyer takes delivery at the station they
    /// are standing in. <see cref="ReasonUnsellable"/> is the deliberate exception — a hull no station
    /// can offer is merchandise nobody can buy, and releasing it is what lets
    /// <c>WorldModel.ClearOrbitStranding</c> reclaim its BOM, which it can only do through
    /// <see cref="ShipPool.Available"/>.</para>
    ///
    /// <para><b>Phase 3 iterates what is reserved, not what is wanted.</b> Same rule
    /// <c>ReserveInstallStock</c> had to learn: a lapsed reason drops its key out of the target
    /// entirely, so a target-keyed loop stops looking at exactly the stock that most needs
    /// releasing.</para>
    /// </summary>
    public static class StoreShelfRelease
    {
        /// <summary>Why a hull was handed back. Named because a released hull is a visible change to
        /// what a shop offers, and "the shelf shrank" is not a diagnosis.</summary>
        public const string ReasonUnsellable = "unsellable_here";
        public const string ReasonOverTarget = "over_target";
        /// <summary>The design was banned on this server (<see cref="PrefabBanEnforcement"/>).</summary>
        public const string ReasonBanned = "banned";
        /// <summary>A design decommissioned by its owner or an admin (<see cref="FleetDecommission"/>).</summary>
        public const string ReasonDecommission = "decommission";

        /// <summary>Which hull the over-target release chose, logged as <c>pick=</c> beside the
        /// reason. A node need steers this and nothing else — keeping the two words apart is what
        /// stops "a node wants one" from reading as "therefore release one".</summary>
        public const string PickNodeNeeds   = "node_needs_it";
        public const string PickMostStocked = "most_stocked";
        /// <summary>The player's shelf no longer names this hull at all, so its reservation has no
        /// reason left rather than merely too much of one.</summary>
        public const string PickNoLine      = "no_shelf_line";

        /// <summary>Runs one release pass for one faction. Returns how many units were handed back.
        /// <paramref name="storeStations"/> is the faction's own stations that actually sell — the
        /// set a hull has to be shelvable at for its reservation to mean anything.</summary>
        public static int Run(FactionState state, WorldModel world, FactionTuning tuning,
                              IList<SpatialNode> storeStations, List<string> log)
        {
            if (state == null || world == null || tuning == null) return 0;
            // 0 or less is OFF, not "fall back to 1". The first cut coerced it to 1, which left the
            // operator with a rate knob that had no off position and therefore no live lever when
            // this pass misbehaved. A knob that cannot be set to nothing is not a knob.
            int budget = tuning.StoreShelfReleasesPerPass;
            if (budget <= 0) return 0;
            int released = 0;

            var owned = new List<SpatialNode>(world.Graph.OwnedBy(state.Tag));

            // Phase 1: reservations no station of ours can sell. Released whatever the shelf count
            // is -- the dead band is about a shelf being too FULL, and a hull nobody can be offered
            // is not filling a slot, it is occupying one while invisible.
            for (int i = 0; i < owned.Count && released < budget; i++)
            {
                SpatialNode node = owned[i];
                foreach (string prefabId in Keys(node.ListedForSale))
                {
                    if (released >= budget) break;
                    if (Sellable(world, tuning, prefabId, storeStations)) continue;
                    released += HandBack(node, prefabId, 1, state.Tag, ReasonUnsellable, null, world, log);
                }
            }

            // Phase 2: the shelf is over target by more than the dead band. A node need steers
            // WHICH hull goes back; it never adds a release of its own.
            if (released < budget)
                released += FactionLifecycle.ShelfActive(state)
                    ? ReleaseOverLineTarget(state, world, owned, budget - released, log)
                    : ReleaseOverTarget(state, world, tuning, owned, budget - released, log);

            return released;
        }

        /// <summary>Phase 2 for a faction that names its own shelf: the target is the line's, per
        /// prefab, and a hull NO line names has a target of zero.
        ///
        /// <para><b>The no-line case is why this cannot be the grid-size pass with a different
        /// number.</b> Removing a line is the reservation's reason lapsing, and a lapsed reason drops
        /// its key out of the target entirely -- so a loop over the player's lines would stop looking
        /// at exactly the stock that most needs releasing. This iterates what is RESERVED, which is
        /// the rule <c>ProductionManager.ReserveInstallStock</c> had to learn and the reason its
        /// phase 3 is written the same way.</para>
        ///
        /// <para><b>No dead band on an unwanted hull.</b> The band exists so the pass that stocks and
        /// the pass that drains cannot both fire on one count; nothing is stocking a prefab no line
        /// names, so there is no oscillation to damp and holding a margin of it would strand stock
        /// against a reason that no longer exists. A line that merely LOWERED its target keeps the
        /// band, because that row is still being stocked.</para></summary>
        private static int ReleaseOverLineTarget(FactionState state, WorldModel world,
                                                 List<SpatialNode> owned, int budget, List<string> log)
        {
            int released = 0;
            foreach (SpatialNode node in owned)
            {
                if (released >= budget) break;
                foreach (string prefabId in Keys(node.ListedForSale))
                {
                    if (released >= budget) break;
                    ShelfLine line = state.ShelfLineFor(prefabId);
                    int target = line == null ? 0 : line.TargetCount;
                    int margin = line == null ? 0 : world.Tuning.StoreShelfReleaseMarginSlots;

                    long listed = 0;
                    foreach (SpatialNode n in owned)
                    {
                        int c;
                        if (n.ListedForSale.TryGetValue(prefabId, out c) && c > 0) listed += c;
                    }

                    // A line that still names the hull is trimming a shelf that is too deep, so the
                    // same rule as the grid-size pass applies: do not hand back what the fleet cannot
                    // dispatch from here. A line the player has REMOVED is an instruction, and is
                    // released either way -- see UsableByTheFleetHere.
                    if (line != null && !UsableByTheFleetHere(world, world.Tuning, node, prefabId)) continue;

                    long excess = listed - (target + margin);
                    while (excess > 0 && released < budget)
                    {
                        int gone = HandBack(node, prefabId, 1, state.Tag, ReasonOverTarget,
                                            line == null ? PickNoLine : PickMostStocked, world, log);
                        if (gone == 0) break;
                        released += gone;
                        excess -= gone;
                    }
                }
            }
            return released;
        }

        /// <summary>Phase 2 for a faction on the manager's own targets. The target is per SITE, the
        /// same scope <see cref="StoreStockManager"/> stocks against: counting the whole estate
        /// against a single-shelf target would read every yard's shelf as one oversized one and drain
        /// them all.</summary>
        private static int ReleaseOverTarget(FactionState state, WorldModel world, FactionTuning tuning,
                                             List<SpatialNode> owned, int budget, List<string> log)
        {
            IPrefabCatalogue cat = world.PrefabCatalogue;
            if (cat == null) return 0;

            int released = 0;
            int margin = tuning.StoreShelfReleaseMarginSlots;
            for (int i = 0; i < owned.Count && released < budget; i++)
            {
                SpatialNode node = owned[i];
                foreach (string gridSize in new[] { "Small", "Large" })
                {
                    if (released >= budget) break;
                    int target = string.Equals(gridSize, "Small", StringComparison.OrdinalIgnoreCase)
                        ? tuning.StoreShipSlotsSmallGrid
                        : tuning.StoreShipSlotsLargeGrid;

                    int listed = 0;
                    foreach (var kv in node.ListedForSale)
                        if (kv.Value > 0 && string.Equals(cat.GridSize(kv.Key), gridSize,
                                                          StringComparison.OrdinalIgnoreCase))
                            listed += kv.Value;

                    // The dead band. Strictly greater: at exactly target + margin nothing moves, so
                    // the pass that stocks and the pass that drains can never both fire on one count.
                    int excess = listed - (target + margin);
                    while (excess > 0 && released < budget)
                    {
                        string prefabId, pick;
                        if (!ChooseRelease(state.Tag, world, tuning, node, cat, gridSize, out prefabId, out pick))
                            break;
                        int gone = HandBack(node, prefabId, 1, state.Tag, ReasonOverTarget, pick, world, log);
                        if (gone == 0) break;
                        released += gone;
                        excess -= gone;
                    }
                }
            }
            return released;
        }

        /// <summary>True when at least one station this faction sells from could offer the hull.
        /// No stations at all reads as sellable: a faction that has not founded a shop yet is not
        /// holding an unsellable reservation, it is holding stock for a shop it does not have.</summary>
        private static bool Sellable(WorldModel world, FactionTuning tuning, string prefabId,
                                     IList<SpatialNode> storeStations)
        {
            if (storeStations == null || storeStations.Count == 0) return true;
            for (int i = 0; i < storeStations.Count; i++)
                if (StoreShelfPlan.IsShelfableAt(world.PrefabCatalogue, prefabId, world.Planets,
                                                 storeStations[i].Position, tuning.MinDryTwr))
                    return true;
            return false;
        }

        /// <summary>Whether this node carries a live fleet <c>Ship:{role}</c> demand that the
        /// reserved hull would satisfy. Shelf demands are excluded by their own flag — a shelf
        /// demand being met by a shelf hull is the reservation working, not a need going unmet.</summary>
        private static bool NodeNeeds(WorldModel world, string factionTag, SpatialNode node, string prefabId)
        {
            IPrefabCatalogue cat = world.PrefabCatalogue;
            if (cat == null) return false;
            foreach (DemandEntry d in world.Demand.SnapshotShipDemands(factionTag))
            {
                if (d.DestinedForSale) continue;
                if (!string.Equals(d.NodeId, node.Id, StringComparison.Ordinal)) continue;
                if (d.Kind == null || d.Kind.Length <= 5) continue;
                if (ShipPool.IsSuitableForRole(d.Kind.Substring(5), cat, prefabId)) return true;
            }
            return false;
        }

        /// <summary>Which hull to hand back, given that the shelf has already been judged able to
        /// spare one. A hull some node of ours is currently trying to build wins — that is the
        /// operator's "shrink based on node needs", expressed where it cannot create a release of its
        /// own. Failing that, the most-stocked prefab goes, which is the inverse of the
        /// diversification that filled the shelf.</summary>
        private static bool ChooseRelease(string factionTag, WorldModel world, FactionTuning tuning,
                                          SpatialNode node, IPrefabCatalogue cat, string gridSize,
                                          out string prefabId, out string pick)
        {
            foreach (string id in Keys(node.ListedForSale))
            {
                if (!string.Equals(cat.GridSize(id), gridSize, StringComparison.OrdinalIgnoreCase)) continue;
                if (!UsableByTheFleetHere(world, tuning, node, id)) continue;
                if (!NodeNeeds(world, factionTag, node, id)) continue;
                prefabId = id; pick = PickNodeNeeds;
                return true;
            }

            pick = PickMostStocked;
            return MostStocked(world, tuning, node, cat, gridSize, out prefabId);
        }

        private static bool MostStocked(WorldModel world, FactionTuning tuning, SpatialNode node,
                                        IPrefabCatalogue cat, string gridSize, out string prefabId)
        {
            prefabId = null;
            int best = 0;
            foreach (var kv in node.ListedForSale)
            {
                if (kv.Value <= best) continue;
                if (!string.Equals(cat.GridSize(kv.Key), gridSize, StringComparison.OrdinalIgnoreCase)) continue;
                if (!UsableByTheFleetHere(world, tuning, node, kv.Key)) continue;
                best = kv.Value; prefabId = kv.Key;
            }
            return prefabId != null;
        }

        /// <summary>Whether handing <paramref name="prefabId"/> back at <paramref name="node"/> gives
        /// the fleet something it can dispatch: the hull's dry TWR clears <c>MinDryTwr</c> at this
        /// node, the same gate <see cref="ShipyardManager.CanFlyFromYard"/> applies to a build here
        /// and <see cref="ShipPool"/> applies to the dispatch itself.
        ///
        /// <para>An over-target release of a hull that fails it is not a hand-back, it is a strand:
        /// the reservation is dropped and the hull joins a pool that can never send it anywhere, at a
        /// node it can never leave. The shelf keeps it instead — still on sale, since a buyer takes
        /// delivery at the station they are standing in rather than flying it out of here.</para>
        ///
        /// <para>Deliberately NOT applied to <see cref="ReasonUnsellable"/>. A hull no station of ours
        /// can offer is merchandise nobody can buy; releasing it is what lets
        /// <c>WorldModel.ClearOrbitStranding</c> reclaim its BOM, and that path reads
        /// <see cref="ShipPool.Available"/>, which a reservation excludes. Nor to
        /// <see cref="PickNoLine"/>: a player removing the line is an instruction to stop holding the
        /// hull, and the same reclaim applies.</para></summary>
        private static bool UsableByTheFleetHere(WorldModel world, FactionTuning tuning,
                                                 SpatialNode node, string prefabId)
        {
            return ShipyardManager.CanFlyFromYard(world, tuning, node, prefabId);
        }

        /// <summary>Drops the reservation without touching <see cref="SpatialNode.ShipStock"/> — the
        /// hull is still there, it is simply available to the fleet again. Only a sale removes the
        /// hull itself.</summary>
        internal static int HandBack(SpatialNode node, string prefabId, int units, string factionTag,
                                    string reason, string pick, WorldModel world, List<string> log)
        {
            int reserved;
            if (!node.ListedForSale.TryGetValue(prefabId, out reserved) || reserved <= 0) return 0;
            int take = units < reserved ? units : reserved;
            if (take <= 0) return 0;

            if (reserved - take > 0) node.ListedForSale[prefabId] = reserved - take;
            else                     node.ListedForSale.Remove(prefabId);

            if (log != null)
                log.Add(factionTag + " STORE_SHELF_RELEASED prefab=" + prefabId + " node=" + node.Id
                        + " units=" + take + " reason=" + reason
                        + (pick != null ? " pick=" + pick : ""));
            return take;
        }

        /// <summary>A snapshot of the keys, because a hand-back mutates the dictionary.</summary>
        private static List<string> Keys(Dictionary<string, int> listed)
        {
            var keys = new List<string>(listed.Count);
            foreach (var kv in listed)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value > 0) keys.Add(kv.Key);
            return keys;
        }
    }
}
