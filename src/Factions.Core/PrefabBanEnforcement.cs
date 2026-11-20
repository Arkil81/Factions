using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>What banning one design undid, and what of it is left in the world.</summary>
    public sealed class PrefabBanResult
    {
        /// <summary>Queue entries building the design, cancelled with their components refunded.</summary>
        public int BuildsCancelled;
        /// <summary>Hulls taken off a shelf. They stay in stock as fleet hulls.</summary>
        public int Delisted;
        /// <summary>Refits to the design, cancelled: the hull is back in stock as the version it was,
        /// and the components collected for the refit are refunded.</summary>
        public int RefitsCancelled;
        /// <summary>Hulls of the design still parked, across every faction. Decommissioning removes them.</summary>
        public int Parked;
        /// <summary>Hulls of the design flying on a pending fleet, across every faction.</summary>
        public long Flying;
        /// <summary>Per faction tag, parked hulls of the design, for the admin reply.</summary>
        public readonly SortedDictionary<string, int> ParkedByFaction = new SortedDictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>What a ban does to work already under way on the design (FAC-451; operator, 2026-10-07:
    /// cancel and delist). The ban itself is the exclusion predicate
    /// (<see cref="WorldModel.PrefabBanned"/>), which every new build, doctrine placement, shelf build,
    /// role pool and refit reads; this sweeps what existed before it was set.
    ///
    /// <para>A ban stops building, not owning: parked, flying and damaged hulls of the design stay,
    /// and the result counts them so the admin can decommission them (FAC-449).</para></summary>
    public static class PrefabBanEnforcement
    {
        /// <summary>Cancels and refunds every queued build of <paramref name="family"/>, hands every
        /// shelf listing of it back to the fleet, and cancels every refit to it, across every node in
        /// the graph. Runs once, when the ban is set; the predicate keeps it from coming back.</summary>
        public static PrefabBanResult Apply(WorldModel world, IEnumerable<FactionState> states, string family,
            List<string> log)
        {
            var r = new PrefabBanResult();
            if (world == null || string.IsNullOrEmpty(family)) return r;
            IPrefabCatalogue cat = world.PrefabCatalogue;

            var nodes = new List<SpatialNode>(world.Graph.Nodes.Values);
            nodes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            foreach (SpatialNode n in nodes)
            {
                string tag = n.OwnerFactionTag;

                for (int i = n.BuildQueue.Count - 1; i >= 0; i--)
                {
                    BuildQueueEntry e = n.BuildQueue[i];
                    if (ShipyardManager.IsStationRefit(e) || !FleetDecommission.Matches(cat, e.PrefabId, family)) continue;
                    ShipyardManager.RefundEntry(world, n, e);
                    n.BuildQueue.RemoveAt(i);
                    r.BuildsCancelled++;
                    if (tag != null) world.RecordEvent(FactionEventKind.BuildCancelled, tag, null, n.Id, 1);
                    if (log != null)
                        log.Add((tag ?? "-") + " BUILD_CANCELLED id=" + e.Id + " node=" + n.Id + " prefab=" + e.PrefabId
                                + " reason=banned");
                }

                foreach (string prefabId in new List<string>(n.ListedForSale.Keys))
                {
                    if (!FleetDecommission.Matches(cat, prefabId, family)) continue;
                    int listed = n.ListedForSale[prefabId];
                    r.Delisted += StoreShelfRelease.HandBack(n, prefabId, listed, tag ?? "-",
                        StoreShelfRelease.ReasonBanned, null, world, log);
                }

                for (int i = n.DamagedHulls.Count - 1; i >= 0; i--)
                {
                    DamagedHull h = n.DamagedHulls[i];
                    if (h == null || !h.IsRefit || !FleetDecommission.Matches(cat, h.RefitTo, family)) continue;
                    foreach (KeyValuePair<string, long> kv in h.Collected)
                        if (kv.Value > 0) world.AddNodeInventory(n.Id, kv.Key, kv.Value);
                    n.DamagedHulls.RemoveAt(i);
                    ShipPool.Return(n, h.PrefabId, 1);
                    r.RefitsCancelled++;
                    if (log != null)
                        log.Add((tag ?? "-") + " REFIT_CANCELLED prefab=" + h.PrefabId + " to=" + h.RefitTo
                                + " node=" + n.Id + " reason=banned");
                }

                foreach (KeyValuePair<string, int> kv in n.ShipStock)
                {
                    if (kv.Value <= 0 || !FleetDecommission.Matches(cat, kv.Key, family)) continue;
                    r.Parked += kv.Value;
                    string key = tag ?? "-";
                    int have;
                    r.ParkedByFaction.TryGetValue(key, out have);
                    r.ParkedByFaction[key] = have + kv.Value;
                }
            }

            if (states != null)
            {
                var probe = new DoctrineLine { PrefabId = family };
                foreach (FactionState st in states)
                    r.Flying += ShipyardManager.CountDoctrineHullsInFlight(st, probe, cat);
            }
            return r;
        }
    }
}
