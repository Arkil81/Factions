using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a node's repair queue is still short of, and the demand rows that order it.
    ///
    /// <para>A repair bill is a committed BOM exactly as an active build's is: the parts a waiting hull
    /// has already absorbed are sunk into it, and nothing else fetches the rest. So the shortfall is
    /// posted the way <c>ShipyardManager.ServiceEntry</c> posts a build's -- an <c>Item:</c> row for
    /// Operations to haul against and Production to admit a recipe for, and a node-pinned
    /// <c>Need:</c> row for <see cref="PriceOracle"/> -- and Production roots its chain on it the way
    /// it roots one on an active build (<see cref="AddOutstandingKeys"/>).</para>
    ///
    /// <para>Only a yard that services its queue posts: a node with no Shipyard, or a paused one,
    /// repairs nothing (<see cref="HullDamage.ServiceRepairs"/>), so parts hauled there would sit
    /// unused. See docs/fleet-events.md, "Hull integrity".</para></summary>
    public static class RepairSupply
    {
        /// <summary>Whether <paramref name="node"/>'s yard works its repair queue: a Shipyard at level
        /// 1 or above, not paused. The same gate <see cref="HullDamage.ServiceRepairs"/> applies.</summary>
        public static bool Services(SpatialNode node)
        {
            if (node == null || node.ShipyardPaused) return false;
            int yard;
            return node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out yard) && yard >= 1;
        }

        /// <summary>Adds to <paramref name="into"/>, per inventory key, what every hull at
        /// <paramref name="node"/> still waiting for parts lacks (bill less collected). Nothing for a
        /// node whose yard does not service repairs. Answers how many hulls contributed.
        ///
        /// <para>A sum over the whole queue, not per hull: parts move between waiting hulls in served
        /// order, so the queue's total shortfall is the need, whichever hull ends up holding them.</para></summary>
        public static int AddOutstanding(SpatialNode node, Dictionary<string, long> into)
        {
            if (into == null || node == null || node.DamagedHulls.Count == 0 || !Services(node)) return 0;
            int hulls = 0;
            for (int i = 0; i < node.DamagedHulls.Count; i++)
            {
                DamagedHull hull = node.DamagedHulls[i];
                if (hull == null || hull.PartsComplete) continue;
                bool shortOfAny = false;
                foreach (KeyValuePair<string, long> kv in hull.Bill)
                {
                    long have;
                    hull.Collected.TryGetValue(kv.Key, out have);
                    long missing = kv.Value - have;
                    if (missing <= 0) continue;
                    long prior;
                    into.TryGetValue(kv.Key, out prior);
                    into[kv.Key] = prior + missing;
                    shortOfAny = true;
                }
                if (shortOfAny) hulls++;
            }
            return hulls;
        }

        /// <summary>Adds to <paramref name="into"/> every key some waiting hull at
        /// <paramref name="node"/> still lacks; the key-only counterpart of
        /// <see cref="AddOutstanding"/>, for Production's active-build output set.</summary>
        public static void AddOutstandingKeys(SpatialNode node, HashSet<string> into)
        {
            if (into == null || node == null || node.DamagedHulls.Count == 0 || !Services(node)) return;
            for (int i = 0; i < node.DamagedHulls.Count; i++)
            {
                DamagedHull hull = node.DamagedHulls[i];
                if (hull == null || hull.PartsComplete) continue;
                foreach (KeyValuePair<string, long> kv in hull.Bill)
                {
                    long have;
                    hull.Collected.TryGetValue(kv.Key, out have);
                    if (kv.Value > have) into.Add(kv.Key);
                }
            }
        }

        /// <summary>Posts <paramref name="node"/>'s repair shortfall as an <c>Item:</c> row and a
        /// node-pinned <c>Need:</c> row per key, both <see cref="DemandClass.ShipBuild"/> with
        /// <paramref name="ttl"/>. Called after the node's builds have posted theirs in the same pass
        /// and after <see cref="HullDamage.ServiceRepairs"/> has absorbed what the node holds.
        ///
        /// <para>A build and a repair wanting the same key at the same node share one register row,
        /// so the quantity posted is the repair shortfall plus the node's active-build shortfall for
        /// that key; posting the repair's alone would overwrite the build's. Nothing is consumed
        /// explicitly: a key the queue no longer lacks is simply not refreshed and its rows expire
        /// on <paramref name="ttl"/>, as a finished build's do.</para>
        ///
        /// <para>Logs <c>ITEM_DEMAND</c> only for a row that is new. Answers how many keys were
        /// posted.</para></summary>
        public static int PostDemands(WorldModel world, SpatialNode node, string factionTag, TimeSpan ttl,
                                      IList<string> log)
        {
            if (world == null || node == null || node.DamagedHulls.Count == 0) return 0;
            var outstanding = new Dictionary<string, long>(StringComparer.Ordinal);
            int hulls = AddOutstanding(node, outstanding);
            if (hulls == 0 || outstanding.Count == 0) return 0;

            DateTime expires = world.UtcNow.Add(ttl);
            var keys = new List<string>(outstanding.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int k = 0; k < keys.Count; k++)
            {
                string key = keys[k];
                long repairShort = outstanding[key];
                long quantity = repairShort + ActiveBuildShortfall(node, key);
                bool isNew = world.Demand.Post(new DemandEntry
                {
                    FactionTag = factionTag,
                    Kind       = DemandRegister.ItemKind(key),
                    NodeId     = node.Id,
                    ExpiresUtc = expires,
                    Quantity   = quantity,
                    Class      = DemandClass.ShipBuild,
                });
                world.Demand.Post(DemandEntry.ForNeed(factionTag, key, node.Id, expires, quantity,
                                                      DemandClass.ShipBuild));
                if (isNew && log != null)
                {
                    long have;
                    node.Inventory.TryGetValue(key, out have);
                    log.Add(factionTag + " ITEM_DEMAND " + key + " need=" + quantity + " have=" + have
                            + " repair_short=" + repairShort + " repair_hulls=" + hulls + " node=" + node.Id);
                }
            }
            return keys.Count;
        }

        /// <summary>What the node's active builds still lack of <paramref name="key"/>: the builds
        /// holding a slot, which are the ones <c>ShipyardManager</c> services and posts for.</summary>
        private static long ActiveBuildShortfall(SpatialNode node, string key)
        {
            long total = 0;
            for (int i = 0; i < node.BuildQueue.Count; i++)
            {
                BuildQueueEntry build = node.BuildQueue[i];
                if (build == null || !node.IsActiveBuild(build)) continue;
                long need;
                if (!build.Bom.TryGetValue(key, out need)) continue;
                long collected;
                build.Collected.TryGetValue(key, out collected);
                if (need > collected) total += need - collected;
            }
            return total;
        }
    }
}
