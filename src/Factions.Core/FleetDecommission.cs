using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>What one decommission did, or would do on a dry run.</summary>
    public sealed class DecommissionResult
    {
        /// <summary>The design family matched; every version of it is in scope.</summary>
        public string Family;
        /// <summary>Parked hulls turned back into components.</summary>
        public int Dismantled;
        /// <summary>The part of <see cref="Dismantled"/> that was on a shelf and was taken off it first.</summary>
        public int Delisted;
        /// <summary>The part of <see cref="Dismantled"/> taken out of a yard's repair or refit queue.</summary>
        public int FromYard;
        /// <summary>Hulls in flight marked to be dismantled when they park
        /// (<see cref="FactionState.DecommissionOnReturn"/>). Only a decommission of every hull marks.</summary>
        public long MarkedOnReturn;
        /// <summary>Component units credited, summed over every node.</summary>
        public long BomUnitsCredited;
        /// <summary>Hulls of the design left alone because they are flying and not marked: a
        /// decommission with a count marks none.</summary>
        public long SkippedFlying;
        /// <summary>Hulls of the design in repair or refit left alone: only the count stops short of them.</summary>
        public long SkippedInRepair;
        /// <summary>Queue entries still building the design. A decommission does not cancel them.</summary>
        public int StillQueued;
        /// <summary>Doctrine lines naming the design that were removed, so the doctrine does not
        /// rebuild what was just dismantled.</summary>
        public readonly List<string> RemovedDoctrineLines = new List<string>();
        /// <summary>Shelf lines naming the design that were removed, so the store does not restock it.</summary>
        public readonly List<string> RemovedShelfLines = new List<string>();
        /// <summary>Hulls dismantled per node, in node-id order.</summary>
        public readonly List<KeyValuePair<string, int>> ByNode = new List<KeyValuePair<string, int>>();
    }

    /// <summary>Retires a hull design from one faction's fleet (FAC-449): its parked hulls, shelf
    /// listings included, become their components again at the node that held them, and the doctrine
    /// and shelf lines naming it are removed so nothing rebuilds it.
    ///
    /// <para><b>Every hull it can reach, and a mark for the rest.</b> Parked hulls go first, then the
    /// shelf, then hulls in a yard's repair or refit queue, which are dismantled for what they hold
    /// (<see cref="CreditFromYard"/>) rather than repaired first. A hull flying on a pending fleet
    /// cannot be: turning a hull mid-mission into components has no SE-side counterpart and no
    /// accounting path. A decommission of every hull marks how many are flying
    /// (<see cref="FactionState.DecommissionOnReturn"/>), and <see cref="SweepReturned"/> dismantles
    /// them as they park or queue for repair. Parked stock is abstract (only a fleet carries a
    /// materialised grid), so there is no SE grid to close.</para>
    ///
    /// <para><b>Full BOM, no recovery fraction.</b> Nothing is created: the components were spent on
    /// the hull and come back out of it. A player can rebuild a different design from them, which is
    /// the point.</para>
    ///
    /// <para>One implementation for the player verb, the Fleet window and the admin twin; the caller
    /// resolves the faction and the design name.</para></summary>
    public static class FleetDecommission
    {
        /// <summary>Decommissions up to <paramref name="maxCount"/> hulls of <paramref name="family"/>'s
        /// design (every version, <see cref="PrefabVersions.SameFamily"/>), all of them when
        /// <paramref name="maxCount"/> is 0 or less. Unreserved parked hulls go first, then the shelf,
        /// then the yards' repair and refit queues, so a count trims the ready fleet before the shop.
        /// With no count, the hulls in flight are marked to be dismantled on return.
        /// <paramref name="apply"/> false changes nothing and reports what would happen.</summary>
        public static DecommissionResult Run(WorldModel world, FactionState st, string family, int maxCount,
            bool apply, List<string> log)
        {
            var result = new DecommissionResult { Family = family };
            if (world == null || st == null || string.IsNullOrEmpty(family)) return result;
            IPrefabCatalogue cat = world.PrefabCatalogue;
            int remaining = maxCount > 0 ? maxCount : int.MaxValue;

            var nodes = OwnedNodes(world, st);

            // Parked hulls nobody has reserved, then the shelves, then the yards.
            DismantleParked(world, st, nodes, family, false, apply, ref remaining, result, log);
            DismantleParked(world, st, nodes, family, true, apply, ref remaining, result, log);
            DismantleFromYards(world, st, nodes, family, apply, ref remaining, result, log);
            result.ByNode.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            // The lines go with the design: a doctrine line would queue it again at once, and a shelf
            // line would build it for the shop.
            foreach (DoctrineLine line in new List<DoctrineLine>(st.FleetDoctrine))
            {
                if (line == null || !Matches(cat, line.PrefabId, family)) continue;
                result.RemovedDoctrineLines.Add(line.Id);
                if (!apply) continue;
                st.RemoveDoctrineLine(line);
                if (log != null)
                    log.Add(st.Tag + " DOCTRINE_LINE_REMOVED line=" + line.Id + " prefab=" + line.PrefabId
                            + " reason=decommission");
            }
            foreach (ShelfLine line in new List<ShelfLine>(st.Shelf))
            {
                if (line == null || !Matches(cat, line.PrefabId, family)) continue;
                result.RemovedShelfLines.Add(line.Id);
                if (!apply) continue;
                st.RemoveShelfLine(line);
                if (log != null)
                    log.Add(st.Tag + " SHELF_LINE_REMOVED line=" + line.Id + " prefab=" + line.PrefabId
                            + " reason=decommission");
            }

            // What stays, so the reply can say so. A decommission of every hull leaves nothing behind
            // in a yard, so what it cannot reach is the flying, and those it marks.
            long flying = Flying(world, st, family);
            result.SkippedInRepair = apply ? InYards(world, st, family) : Math.Max(0, InYards(world, st, family) - result.FromYard);
            if (maxCount <= 0 && flying > 0)
            {
                result.MarkedOnReturn = flying;
                if (apply)
                {
                    int marked;
                    st.DecommissionOnReturn.TryGetValue(family, out marked);
                    st.DecommissionOnReturn[family] = (int)Math.Max(marked, flying);
                    if (log != null)
                        log.Add(st.Tag + " DECOMMISSION_ON_RETURN family=" + family
                                + " flying=" + flying.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                result.SkippedFlying = flying;
            }
            foreach (SpatialNode n in nodes)
                foreach (BuildQueueEntry e in n.BuildQueue)
                    if (!ShipyardManager.IsStationRefit(e) && Matches(cat, e.PrefabId, family)) result.StillQueued++;

            return result;
        }

        /// <summary>Dismantles hulls of every design <paramref name="st"/> marked on decommission as they
        /// come home (<see cref="FactionState.DecommissionOnReturn"/>): parked, or queued for repair
        /// because they came back damaged. A mark takes no more than the hulls that were flying when it
        /// was set, so a build of the design that finishes afterwards is not eaten, and it is dropped
        /// when it is used up, when nothing of the design is left flying, or when a doctrine or shelf
        /// line names the design again (the owner has taken it back). Runs on the shipyard pass.
        /// Returns how many it dismantled.</summary>
        public static int SweepReturned(WorldModel world, FactionState st, List<string> log)
        {
            if (world == null || st == null || st.DecommissionOnReturn.Count == 0) return 0;
            IPrefabCatalogue cat = world.PrefabCatalogue;
            List<SpatialNode> nodes = null;
            int total = 0;

            var families = new List<string>(st.DecommissionOnReturn.Keys);
            families.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string family in families)
            {
                if (NamedByALine(st, cat, family))
                {
                    st.DecommissionOnReturn.Remove(family);
                    if (log != null) log.Add(st.Tag + " DECOMMISSION_ON_RETURN_DROPPED family=" + family + " reason=line");
                    continue;
                }

                int remaining = st.DecommissionOnReturn[family];
                if (nodes == null) nodes = OwnedNodes(world, st);
                var result = new DecommissionResult { Family = family };
                // The yards first: a hull of the design in repair now can only be one that came home
                // damaged, since the decommission emptied the yards, whereas parked stock may hold a
                // build of the design that finished since.
                DismantleFromYards(world, st, nodes, family, true, ref remaining, result, log);
                DismantleParked(world, st, nodes, family, false, true, ref remaining, result, log);
                total += result.Dismantled;

                if (remaining <= 0 || Flying(world, st, family) == 0)
                {
                    st.DecommissionOnReturn.Remove(family);
                    if (log != null)
                        log.Add(st.Tag + " DECOMMISSION_ON_RETURN_DONE family=" + family
                                + " unused=" + Math.Max(0, remaining).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    st.DecommissionOnReturn[family] = remaining;
                }
            }
            return total;
        }

        private static List<SpatialNode> OwnedNodes(WorldModel world, FactionState st)
        {
            var nodes = new List<SpatialNode>(world.Graph.OwnedBy(st.Tag));
            nodes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return nodes;
        }

        /// <summary>One pass over parked stock: unreserved hulls, or (<paramref name="shelf"/>) hulls
        /// taken off the shelf first.</summary>
        private static void DismantleParked(WorldModel world, FactionState st, List<SpatialNode> nodes, string family,
            bool shelf, bool apply, ref int remaining, DecommissionResult result, List<string> log)
        {
            IPrefabCatalogue cat = world.PrefabCatalogue;
            foreach (SpatialNode n in nodes)
            {
                if (remaining <= 0) return;
                var keys = new List<string>(n.ShipStock.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string prefabId in keys)
                {
                    if (remaining <= 0) return;
                    if (!Matches(cat, prefabId, family)) continue;

                    int take;
                    if (!shelf)
                    {
                        take = Math.Min(ShipPool.Available(n, prefabId), remaining);
                    }
                    else
                    {
                        int listed;
                        n.ListedForSale.TryGetValue(prefabId, out listed);
                        int stock;
                        n.ShipStock.TryGetValue(prefabId, out stock);
                        take = Math.Min(Math.Min(listed, stock), remaining);
                        if (take <= 0) continue;
                        if (apply)
                            take = StoreShelfRelease.HandBack(n, prefabId, take, st.Tag,
                                StoreShelfRelease.ReasonDecommission, null, world, log);
                        result.Delisted += take;
                    }
                    if (take <= 0) continue;

                    long credited = world.DismantleParkedHulls(n, prefabId, take, apply);
                    result.Dismantled += take;
                    result.BomUnitsCredited += credited;
                    remaining -= take;
                    AddByNode(result, n.Id, take);
                    if (log != null && apply)
                        log.Add(st.Tag + " SHIP_DECOMMISSIONED node=" + n.Id + " prefab=" + prefabId
                                + " count=" + take.ToString(CultureInfo.InvariantCulture)
                                + " bomCredited=" + credited.ToString(CultureInfo.InvariantCulture)
                                + (shelf ? " fromShelf=true" : ""));
                }
            }
        }

        /// <summary>Hulls of the design in a yard's repair or refit queue, dismantled where they stand
        /// instead of being repaired or refit first.</summary>
        private static void DismantleFromYards(WorldModel world, FactionState st, List<SpatialNode> nodes, string family,
            bool apply, ref int remaining, DecommissionResult result, List<string> log)
        {
            IPrefabCatalogue cat = world.PrefabCatalogue;
            foreach (SpatialNode n in nodes)
            {
                for (int i = n.DamagedHulls.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    DamagedHull h = n.DamagedHulls[i];
                    if (h == null || !Matches(cat, h.PrefabId, family)) continue;

                    Dictionary<string, long> credit = CreditFromYard(world.Recipes, h);
                    long credited = 0;
                    foreach (KeyValuePair<string, long> kv in credit)
                    {
                        if (apply) world.AddNodeInventory(n.Id, kv.Key, kv.Value);
                        credited += kv.Value;
                    }
                    if (apply) n.DamagedHulls.RemoveAt(i);

                    result.Dismantled++;
                    result.FromYard++;
                    result.BomUnitsCredited += credited;
                    remaining--;
                    AddByNode(result, n.Id, 1);
                    if (log != null && apply)
                        log.Add(st.Tag + " SHIP_DECOMMISSIONED node=" + n.Id + " prefab=" + h.PrefabId
                                + " count=1 bomCredited=" + credited.ToString(CultureInfo.InvariantCulture)
                                + " from=" + (h.IsRefit ? "refit" : "repair"));
                }
            }
        }

        /// <summary>What a hull in a yard is made of, per inventory key. A hull in repair is its design's
        /// BOM less what was shot off it (<see cref="DamagedHull.Bill"/>, the parts the repair needs);
        /// a hull in refit is whole, its <see cref="DamagedHull.Bill"/> being the new version's parts.
        /// Either way the parts already collected for the work (<see cref="DamagedHull.Collected"/>)
        /// come back too.</summary>
        public static Dictionary<string, long> CreditFromYard(IRecipeRegistry recipes, DamagedHull hull)
        {
            var credit = new Dictionary<string, long>(StringComparer.Ordinal);
            if (hull == null) return credit;
            bool hasRecipe;
            Dictionary<string, long> bom = ProductionManager.GetPrefabBom(recipes, hull.PrefabId, out hasRecipe);
            foreach (KeyValuePair<string, long> kv in bom)
            {
                long missing = 0;
                if (!hull.IsRefit) hull.Bill.TryGetValue(kv.Key, out missing);
                long left = kv.Value - missing;
                if (left > 0) credit[kv.Key] = left;
            }
            foreach (KeyValuePair<string, long> kv in hull.Collected)
            {
                if (kv.Value <= 0) continue;
                long have;
                credit.TryGetValue(kv.Key, out have);
                credit[kv.Key] = have + kv.Value;
            }
            return credit;
        }

        private static long Flying(WorldModel world, FactionState st, string family)
        {
            return ShipyardManager.CountDoctrineHullsInFlight(st, new DoctrineLine { PrefabId = family }, world.PrefabCatalogue);
        }

        private static long InYards(WorldModel world, FactionState st, string family)
        {
            return ShipyardManager.CountDoctrineHullsInRepair(world, st.Tag, new DoctrineLine { PrefabId = family });
        }

        private static bool NamedByALine(FactionState st, IPrefabCatalogue cat, string family)
        {
            foreach (DoctrineLine line in st.FleetDoctrine)
                if (line != null && Matches(cat, line.PrefabId, family)) return true;
            foreach (ShelfLine line in st.Shelf)
                if (line != null && Matches(cat, line.PrefabId, family)) return true;
            return false;
        }

        /// <summary>Whether <paramref name="prefabId"/> is a version of <paramref name="family"/>. Case
        /// is ignored, as the catalogue's own ids are; a prefab the catalogue no longer knows still
        /// matches its own name, so a design whose mod was removed can still be decommissioned.</summary>
        public static bool Matches(IPrefabCatalogue cat, string prefabId, string family)
        {
            if (string.IsNullOrEmpty(prefabId) || string.IsNullOrEmpty(family)) return false;
            return PrefabVersions.SameFamily(cat, prefabId, family);
        }

        private static void AddByNode(DecommissionResult result, string nodeId, int count)
        {
            for (int i = 0; i < result.ByNode.Count; i++)
                if (result.ByNode[i].Key == nodeId)
                {
                    result.ByNode[i] = new KeyValuePair<string, int>(nodeId, result.ByNode[i].Value + count);
                    return;
                }
            result.ByNode.Add(new KeyValuePair<string, int>(nodeId, count));
        }
    }
}
