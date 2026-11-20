using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// Refitting parked hulls of an older version of a design to its latest
    /// (docs/issues/FAC-437-prefab-versions.md), on the repair pipeline: a refit is a
    /// <see cref="DamagedHull"/> whose <see cref="DamagedHull.RefitTo"/> names the target, billed the
    /// net difference between the two designs, served by <see cref="HullDamage.ServiceRepairs"/>
    /// and supplied by <see cref="RepairSupply"/> like any repair.
    ///
    /// <para><b>A player sets how many at once</b> (<see cref="FactionState.HullRefitCaps"/>): each
    /// pass takes parked hulls out of stock at owned, unpaused yards until that many of the design
    /// are in refit, and keeps doing so until none is left on an older version — including after a
    /// later version is published.</para>
    ///
    /// <para><b>What a refit replaces is credited back</b> to the yard's inventory when it finishes:
    /// per component, what the old design holds beyond the new one. Versions only move forward, so
    /// the credit cannot be cycled into a component pump the way a two-way station refit
    /// could.</para>
    ///
    /// <para><b>Only hulls parked at a yard are taken.</b> A hull parked elsewhere is refit once it
    /// is back at one.</para>
    /// </summary>
    public static class HullRefit
    {
        /// <summary>The components a refit from <paramref name="fromPrefabId"/> to
        /// <paramref name="toPrefabId"/> needs: per key, what the target holds beyond the old
        /// design.</summary>
        public static Dictionary<string, long> NetBill(IRecipeRegistry recipes, string fromPrefabId, string toPrefabId,
                                                        out bool targetHasRecipe)
        {
            return ShipyardManager.NetRefitBom(recipes, fromPrefabId, toPrefabId, out targetHasRecipe);
        }

        /// <summary>The components a finished refit hands back: per key, what the old design holds
        /// beyond the new one.</summary>
        public static Dictionary<string, long> Surplus(IRecipeRegistry recipes, string fromPrefabId, string toPrefabId)
        {
            bool fromHas, toHas;
            Dictionary<string, long> from = ProductionManager.GetPrefabBom(recipes, fromPrefabId, out fromHas);
            Dictionary<string, long> to = ProductionManager.GetPrefabBom(recipes, toPrefabId, out toHas);
            return ShipyardManager.NetRefitBom(to, from);
        }

        /// <summary>What a station standing as <paramref name="fromPrefabId"/> hands back when it
        /// is rebuilt as <paramref name="toPrefabId"/>: the same <see cref="Surplus"/> a hull refit
        /// credits, but only for an upgrade to a newer version of the same design. A rebuild between
        /// unrelated designs credits nothing, because two designs could be refit back and forth into
        /// a component pump; a version only moves forward.</summary>
        public static Dictionary<string, long> StationUpgradeCredit(IPrefabCatalogue catalogue, IRecipeRegistry recipes,
                                                                    string fromPrefabId, string toPrefabId)
        {
            var versions = catalogue as IPrefabVersionCatalogue;
            if (versions == null || string.IsNullOrEmpty(fromPrefabId) || string.IsNullOrEmpty(toPrefabId)
                || !PrefabVersions.SameFamily(catalogue, fromPrefabId, toPrefabId)
                || versions.VersionOf(toPrefabId) <= versions.VersionOf(fromPrefabId))
                return new Dictionary<string, long>();
            return Surplus(recipes, fromPrefabId, toPrefabId);
        }

        /// <summary>The share of the target design's bill that <paramref name="netBill"/> is, 0..1 —
        /// what sets a refit's yard time.</summary>
        public static double Share(IRecipeRegistry recipes, string toPrefabId, IReadOnlyDictionary<string, long> netBill)
        {
            bool has;
            Dictionary<string, long> target = ProductionManager.GetPrefabBom(recipes, toPrefabId, out has);
            long total = 0, net = 0;
            foreach (KeyValuePair<string, long> kv in target) if (kv.Value > 0) total += kv.Value;
            if (netBill != null) foreach (KeyValuePair<string, long> kv in netBill) if (kv.Value > 0) net += kv.Value;
            if (total <= 0) return 0;
            double share = (double)net / total;
            return share < 0 ? 0 : (share > 1 ? 1 : share);
        }

        /// <summary>Hulls of <paramref name="family"/> in refit on <paramref name="factionTag"/>'s
        /// estate.</summary>
        public static int InRefit(WorldModel world, string factionTag, string family)
        {
            int n = 0;
            foreach (SpatialNode node in world.Graph.OwnedBy(factionTag))
                for (int i = 0; i < node.DamagedHulls.Count; i++)
                {
                    DamagedHull h = node.DamagedHulls[i];
                    if (h != null && h.IsRefit && PrefabVersions.SameFamily(world.PrefabCatalogue, h.RefitTo, family)) n++;
                }
            return n;
        }

        /// <summary>Hulls of <paramref name="family"/> parked on <paramref name="factionTag"/>'s
        /// estate on a version older than <paramref name="latest"/>, available to dispatch — the
        /// refit's remaining work.</summary>
        public static int Outdated(WorldModel world, string factionTag, string family, string latest)
        {
            int n = 0;
            foreach (SpatialNode node in world.Graph.OwnedBy(factionTag))
                foreach (KeyValuePair<string, int> kv in node.ShipStock)
                    if (IsOutdated(world.PrefabCatalogue, kv.Key, family, latest))
                        n += ShipPool.Available(node, kv.Key);
            return n;
        }

        private static bool IsOutdated(IPrefabCatalogue catalogue, string prefabId, string family, string latest)
        {
            return !string.Equals(prefabId, latest, StringComparison.OrdinalIgnoreCase)
                && PrefabVersions.SameFamily(catalogue, prefabId, family);
        }

        /// <summary>One design a faction has hulls of on an older version, or has set a refit cap
        /// for — what the Fleet window's Refits list shows.</summary>
        public sealed class Row
        {
            public string Family;
            /// <summary>The newest version the faction may use.</summary>
            public string Latest;
            /// <summary>Why no hull is refit to <see cref="Latest"/>: <see cref="WorldModel.ExclusionBanned"/>
            /// or <see cref="WorldModel.ExclusionOverCeiling"/>; null when refits may proceed.</summary>
            public string Blocked;
            /// <summary>0 when no refit is set.</summary>
            public int Cap;
            public int InRefit;
            /// <summary>Hulls on an older version parked anywhere, available to dispatch.</summary>
            public int OutdatedParked;
            /// <summary>Of those, the ones at an owned, unpaused yard — what a refit can take now.</summary>
            public int OutdatedAtYards;
        }

        /// <summary>Every design with a refit set, a hull in refit, or a parked hull on an older
        /// version, ordered by name. Empty for a faction with none of those.</summary>
        public static List<Row> Rows(WorldModel world, FactionState st)
        {
            var rows = new List<Row>();
            if (world == null || st == null) return rows;
            IPrefabCatalogue cat = world.PrefabCatalogue;

            var families = new HashSet<string>(st.HullRefitCaps.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
            {
                foreach (KeyValuePair<string, int> kv in node.ShipStock)
                    if (kv.Value > 0 && PrefabVersions.IsSupersededFor(cat, kv.Key, st.Tag))
                        families.Add(PrefabVersions.FamilyOf(cat, kv.Key));
                for (int i = 0; i < node.DamagedHulls.Count; i++)
                    if (node.DamagedHulls[i] != null && node.DamagedHulls[i].IsRefit)
                        families.Add(PrefabVersions.FamilyOf(cat, node.DamagedHulls[i].RefitTo));
            }

            foreach (string family in families)
            {
                string latest = PrefabVersions.LatestFor(cat, family, st.Tag);
                int cap;
                st.HullRefitCaps.TryGetValue(family, out cap);
                var row = new Row
                {
                    Family = family,
                    Latest = latest,
                    Blocked = world.PrefabExclusion(latest),
                    Cap = cap,
                    InRefit = InRefit(world, st.Tag, family),
                };
                foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
                {
                    bool yard = !node.ShipyardPaused && RepairTransfer.HasYard(node);
                    foreach (KeyValuePair<string, int> kv in node.ShipStock)
                    {
                        if (!IsOutdated(cat, kv.Key, family, latest)) continue;
                        int available = ShipPool.Available(node, kv.Key);
                        row.OutdatedParked += available;
                        if (yard) row.OutdatedAtYards += available;
                    }
                }
                rows.Add(row);
            }
            rows.Sort((a, b) => string.Compare(a.Family, b.Family, StringComparison.OrdinalIgnoreCase));
            return rows;
        }

        /// <summary>Takes parked hulls into refit for every design <paramref name="st"/> has set a cap
        /// on, until that many are in refit. Logs <c>REFIT_QUEUED</c> per hull. Answers how many it
        /// took.</summary>
        public static int Intake(FactionState st, WorldModel world, IList<string> log)
        {
            if (st == null || world == null || st.HullRefitCaps.Count == 0) return 0;
            var families = new List<string>(st.HullRefitCaps.Keys);
            families.Sort(StringComparer.OrdinalIgnoreCase);

            var yards = new List<SpatialNode>();
            foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
                if (!node.ShipyardPaused && RepairTransfer.HasYard(node)) yards.Add(node);
            yards.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            if (yards.Count == 0) return 0;

            int taken = 0;
            foreach (string family in families)
            {
                int cap = st.HullRefitCaps[family];
                string latest = PrefabVersions.LatestFor(world.PrefabCatalogue, family, st.Tag);
                // The refit pass never reaches QueueBuild, so the build gates do not apply here: a
                // family whose latest version is banned or over the ceiling is not refit at all,
                // the way ServiceDoctrine skips such a line rather than building an older version.
                if (world.PrefabExclusion(latest) != null) continue;
                int open = cap - InRefit(world, st.Tag, family);
                if (open <= 0) continue;

                foreach (SpatialNode yard in yards)
                {
                    if (open <= 0) break;
                    var ids = new List<string>(yard.ShipStock.Keys);
                    ids.Sort(StringComparer.Ordinal);
                    foreach (string id in ids)
                    {
                        if (open <= 0) break;
                        if (!IsOutdated(world.PrefabCatalogue, id, family, latest)) continue;
                        bool hasRecipe;
                        Dictionary<string, long> bill = NetBill(world.Recipes, id, latest, out hasRecipe);
                        if (!hasRecipe) continue;
                        double share = Share(world.Recipes, latest, bill);
                        while (open > 0 && ShipPool.Available(yard, id) > 0)
                        {
                            yard.ShipStock[id] = yard.ShipStock[id] - 1;
                            if (yard.ShipStock[id] <= 0) yard.ShipStock.Remove(id);
                            var hull = new DamagedHull
                            {
                                PrefabId = id,
                                Integrity = 1.0,
                                QueuedUtc = world.UtcNow,
                                RefitTo = latest,
                                RefitShare = share,
                            };
                            foreach (KeyValuePair<string, long> kv in bill) if (kv.Value > 0) hull.Bill[kv.Key] = kv.Value;
                            yard.DamagedHulls.Add(hull);
                            open--;
                            taken++;
                            if (log != null)
                                log.Add(st.Tag + " REFIT_QUEUED prefab=" + id + " to=" + latest + " node=" + yard.Id
                                        + " bill_units=" + hull.BillUnits().ToString(CultureInfo.InvariantCulture)
                                        + " hours=" + HullDamage.WorkTime(hull).TotalHours.ToString("0.0", CultureInfo.InvariantCulture));
                        }
                    }
                }
            }
            return taken;
        }
    }
}
