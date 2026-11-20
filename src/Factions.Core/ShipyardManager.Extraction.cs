using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    public sealed partial class ShipyardManager
    {
        /// <summary>Drill-quality upgrades posted and not yet seen coming, keyed <c>tag|hub</c>: one
        /// decision per hub, whatever ores it orders. An entry is dropped the first pass the upgrade is covered
        /// (a build entry, a live demand or an idle hull clearing the bar), and one found uncovered is
        /// a demand that vanished without a build: the key then waits out
        /// <see cref="FactionTuning.ExtractionUpgradeRetrySeconds"/> before posting again. In memory
        /// only.</summary>
        private readonly Dictionary<string, UpgradePost> _upgradePosts =
            new Dictionary<string, UpgradePost>(StringComparer.Ordinal);

        private sealed class UpgradePost
        {
            public DateTime PostedUtc;
            public string YardId;
            /// <summary>The ores the upgrade was posted for, as its line named them.</summary>
            public string Ores;
            public bool LapseLogged;
        }

        /// <summary>
        /// Capacity growth for hull-bound extraction orders (docs/mining-plan.md, "### 3. Capacity
        /// growth"; FAC-395 slice 3). An NPC order whose catchment has free deposits and no idle
        /// drill to work them gets proactive <c>Ship:Mine</c> demands sized to its missing rate,
        /// instead of waiting for a dispatch to fail and stage one fallback hull.
        ///
        /// <para><b>How many.</b> <see cref="ExtractionCapacity.HullsToCover"/> at the rate of the hull
        /// the yard nearest the hub would build for it — the same <see cref="SelectPrefabBom"/> call a
        /// Mine build makes, so the drill-rate-per-cost pick of slice 1 — less the hulls already on
        /// their way: every Mine build entry in the estate, every live <c>Ship:Mine</c> demand at a
        /// node with no Mine entry yet (it becomes one this tick), and every idle drill parked
        /// anywhere (a hull built since the plan, not yet dispatched). The supply is faction-wide,
        /// like the planner's drill pool, and is handed to the neediest order first.</para>
        ///
        /// <para><b>Where.</b> One demand per yard, nearest the hub first, and only at a yard holding
        /// no Mine entry and no Mine demand: a yard keeps one build entry per role, so a second demand
        /// there would build nothing more. A pending build is therefore never re-posted — its yard is
        /// skipped and its hull counted. <see cref="FactionTuning.ExtractionMaxHullOrdersPerPass"/>
        /// caps the demands one pass posts across all of the faction's orders.</para>
        ///
        /// <para><b>Never for a player faction</b>, and not for a faction with an active fleet
        /// doctrine: the doctrine is its only builder (<see cref="PostProactiveShipDemands"/>). A
        /// deposit-bound order builds nothing here — more hulls cannot work ground that is not free;
        /// its signal is <see cref="FactionState.ExtractionGroundSignals"/>.</para>
        ///
        /// <para>Cost: nothing unless an order is hull-bound. Then one pass over the owned nodes'
        /// build queues and parked stock, one over the faction's Ship demands, and one prefab
        /// selection per hull-bound order.</para>
        /// </summary>
        /// <returns>The demands posted, which the drill-quality upgrade's share of the same per-pass cap
        /// is reduced by (<see cref="PostExtractionUpgradeDemands"/>).</returns>
        private int PostExtractionHullDemands(FactionState st, WorldModel world, FactionTuning tuning, List<string> log)
        {
            if (st == null || st.IsPlayerFaction || tuning == null) return 0;
            if (FactionLifecycle.DoctrineActive(st)) return 0;
            int budget = tuning.ExtractionMaxHullOrdersPerPass;
            if (budget <= 0 || st.ExtractionOrders.Count == 0) return 0;
            int cap = budget;

            List<StandingExtractionOrder> hullBound = null;
            foreach (StandingExtractionOrder o in st.ExtractionOrders)
                if (o != null && !o.Paused && o.Bound == ExtractionBound.HullBound
                    && ExtractionCapacity.MissingPerHour(o) > 0)
                    (hullBound ?? (hullBound = new List<StandingExtractionOrder>())).Add(o);
            if (hullBound == null) return 0;
            hullBound.Sort(CompareByGapDescending);

            var owned = new List<SpatialNode>(world.Graph.OwnedBy(st.Tag));
            var yards = new List<SpatialNode>();
            // Yards with a Mine build or a Mine demand already: posting there builds nothing more.
            var busyYards = new HashSet<string>(StringComparer.Ordinal);
            int supply = 0;
            foreach (SpatialNode n in owned)
            {
                int level;
                if (!n.ShipyardPaused && n.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out level) && level >= 1)
                    yards.Add(n);
                foreach (BuildQueueEntry e in n.BuildQueue)
                    if (IsMineBuild(e, world.PrefabCatalogue)) { supply++; busyYards.Add(n.Id); }
                if (world.PrefabCatalogue != null)
                    foreach (KeyValuePair<string, int> kv in n.ShipStock)
                        if (kv.Value > 0 && ShipPool.IsMineSuitable(world.PrefabCatalogue, kv.Key)) supply += kv.Value;
            }
            if (yards.Count == 0) return 0;   // no yard: the dispatch fallback's demand asks for one
            foreach (DemandEntry d in world.Demand.SnapshotShipDemands(st.Tag))
                if (d.Kind == "Ship:" + MineRole && !d.DestinedForSale && d.NodeId != null && busyYards.Add(d.NodeId))
                    supply++;

            foreach (StandingExtractionOrder order in hullBound)
            {
                if (budget <= 0) break;
                SpatialNode hub;
                if (order.HubNodeId == null || !world.Graph.Nodes.TryGetValue(order.HubNodeId, out hub)) continue;

                List<SpatialNode> free = FreeYardsNearest(yards, busyYards, hub);
                SpatialNode via = MineTargeting.HardestGravityDeposit(owned, hub.Position, world.Planets, tuning, hub);
                string pick = free.Count > 0 ? ExpectedMinePick(st, world, tuning, free[0], hub, via) : null;
                double drillRate = pick != null && world.PrefabCatalogue != null
                    ? world.PrefabCatalogue.DrillYieldScore(pick) * tuning.MineDrillLitersPerScorePoint * tuning.MineDutyCycle
                    : 0;

                int hulls = ExtractionCapacity.HullsToCover(order, drillRate, tuning);
                int coming = Math.Min(supply, hulls);
                supply -= coming;
                int post = Math.Min(Math.Min(hulls - coming, budget), free.Count);
                if (post <= 0) continue;

                var posted = new StringBuilder();
                for (int i = 0; i < post; i++)
                {
                    SpatialNode yard = free[i];
                    world.Demand.Post(DemandEntry.ForShip(st.Tag, MineRole, yard.Id,
                        world.UtcNow.Add(DemandTtlFor(world.Tuning)), destNodeId: hub.Id, minCargoLiters: 1,
                        viaNodeId: via != null ? via.Id : null, postedUtc: world.UtcNow));
                    busyYards.Add(yard.Id);
                    if (posted.Length > 0) posted.Append(',');
                    posted.Append(yard.Id);
                }
                budget -= post;

                log.Add("EXTRACTION_HULL_ORDER " + st.Tag + " hub=" + hub.Id + " ore=" + order.OreKey
                        + " missing=" + ExtractionPlanner.Compact(ExtractionCapacity.MissingPerHour(order) * order.LitersPerUnit)
                        + " hulls=" + post.ToString(CultureInfo.InvariantCulture)
                        + " need=" + hulls.ToString(CultureInfo.InvariantCulture)
                        + " coming=" + coming.ToString(CultureInfo.InvariantCulture)
                        + " yard=" + posted
                        + " prefab=" + (pick ?? "none"));
            }
            return cap - budget;
        }

        /// <summary>
        /// Drill-quality upgrade for deposit-bound extraction orders (docs/mining-plan.md, "Slice 3b as
        /// built"). A hub whose every deposit is worked or cooling cannot use more hulls, but under the
        /// per-deposit cooldown (cooldown = dwell) a faster drill shortens both the dwell and the rest,
        /// so its drills' quality is its real limit. Such a hub gets one proactive <c>Ship:Mine</c> at a
        /// time when the hull its nearest free yard would build drills much faster than what works its
        /// catchment now.
        ///
        /// <para><b>Per hub, not per ore.</b> A hub orders each ore it consumes separately, but the
        /// drills are the hub's: the same hulls work every one of its catchments. Deciding per (hub,
        /// ore) posted one upgrade per ore, and the one pending build covered one of them, so the rest
        /// lapsed (live 09-28: METC N095 posted for five ores, twice in one pass three times). The
        /// hub's open deposit-bound orders are pooled: their working hulls, each once, set the median,
        /// and the largest of their missing rates is the need.</para>
        ///
        /// <para><b>When.</b> <see cref="ExtractionCapacity.WantsHubUpgrade"/>: an open
        /// deposit-bound order at the hub is still short, and the yard's pick
        /// (<see cref="ExpectedMinePick"/>, the drill-rate-per-cost rank a Mine build uses) drills at
        /// least <see cref="FactionTuning.ExtractionUpgradeRateRatio"/> times the upper median of the
        /// pooled working hulls.</para>
        ///
        /// <para><b>One at a time.</b> An upgrade already coming covers the order: a Mine build entry
        /// whose prefab clears the same bar, a live <c>Ship:Mine</c> demand at a yard with no Mine
        /// entry yet (it will build the same pick), or an idle drill parked anywhere that clears the
        /// bar — a hull built and not yet flown, or one waiting at a deposit for its seam to free. The
        /// supply is faction-wide and handed out neediest hub first (largest gap), one per hub, so a
        /// hub whose upgrade is pending never asks again. The lapse backoff is per hub too. Yards follow slice 3: nearest the hub first, only a
        /// yard holding no Mine entry and no Mine demand. The demands count against
        /// <see cref="FactionTuning.ExtractionMaxHullOrdersPerPass"/> with the hull-bound ones posted
        /// this pass.</para>
        ///
        /// <para><b>The slow hulls stay.</b> Nothing retires a drill hull. The new one displaces a slow
        /// one at dispatch — the order walk flies the fastest idle drill that can make the trip — and
        /// the slow hull stays in stock, flying only when every faster drill is busy. The upper median
        /// is what stops the upgrades once the fast hulls are as many as the slow ones.</para>
        ///
        /// <para><b>Never for a player faction</b>, nor a faction with an active doctrine.</para>
        ///
        /// <para>Cost: nothing unless an order is deposit-bound with a working hull. Then one pass over
        /// the owned nodes' build queues and parked stock, one over the Ship demands, and one prefab
        /// selection per such hub.</para>
        /// </summary>
        private void PostExtractionUpgradeDemands(FactionState st, WorldModel world, FactionTuning tuning,
            int budget, List<string> log)
        {
            if (st == null || st.IsPlayerFaction || tuning == null) return;
            ForgetOldUpgradePosts(world.UtcNow, tuning);
            if (FactionLifecycle.DoctrineActive(st)) return;
            if (budget <= 0 || tuning.ExtractionUpgradeRateRatio <= 1.0 || world.PrefabCatalogue == null) return;
            double perScore = tuning.MineDrillLitersPerScorePoint * tuning.MineDutyCycle;
            if (perScore <= 0) return;

            // One decision per hub: its deposit-bound orders, one per ore, share the hub's drills,
            // so one pending upgrade answers all of them.
            List<HubUpgrade> candidates = HubUpgradeCandidates(st.ExtractionOrders);
            if (candidates == null) return;

            IPrefabCatalogue cat = world.PrefabCatalogue;
            var owned = new List<SpatialNode>(world.Graph.OwnedBy(st.Tag));
            var yards = new List<SpatialNode>();
            var busyYards = new HashSet<string>(StringComparer.Ordinal);
            // Every hull already coming, by drill rate: an upgrade covers an order when it clears
            // that order's bar. A demand's hull is not chosen yet; it will be the yard's pick.
            var coming = new List<double>();
            foreach (SpatialNode n in owned)
            {
                int level;
                if (!n.ShipyardPaused && n.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out level) && level >= 1)
                    yards.Add(n);
                foreach (BuildQueueEntry e in n.BuildQueue)
                    if (IsMineBuild(e, cat))
                    {
                        busyYards.Add(n.Id);
                        coming.Add(e.PrefabId != null ? cat.DrillYieldScore(e.PrefabId) * perScore : 0);
                    }
                foreach (KeyValuePair<string, int> kv in n.ShipStock)
                    if (kv.Value > 0 && ShipPool.IsMineSuitable(cat, kv.Key))
                    {
                        double rate = cat.DrillYieldScore(kv.Key) * perScore;
                        int count = Math.Min(kv.Value, ExtractionFleetIndex.MaxPerStockEntry);
                        for (int i = 0; i < count; i++) coming.Add(rate);
                    }
            }
            if (yards.Count == 0) return;
            foreach (DemandEntry d in world.Demand.SnapshotShipDemands(st.Tag))
                if (d.Kind == "Ship:" + MineRole && !d.DestinedForSale && d.NodeId != null && busyYards.Add(d.NodeId))
                    coming.Add(double.PositiveInfinity);
            // Slowest first, so each order takes the slowest hull that clears its bar and leaves the
            // faster ones for orders with a higher bar.
            coming.Sort();

            foreach (HubUpgrade candidate in candidates)
            {
                if (budget <= 0) break;
                SpatialNode hub;
                if (!world.Graph.Nodes.TryGetValue(candidate.HubNodeId, out hub)) continue;
                ExtractionHull basis = ExtractionCapacity.UpgradeBasis(candidate.Hulls);
                double bar = tuning.ExtractionUpgradeRateRatio * basis.LitersPerSecond;

                string postKey = st.Tag + "|" + candidate.HubNodeId;
                int covering = coming.FindIndex(r => r >= bar);
                if (covering >= 0)
                {
                    coming.RemoveAt(covering);
                    _upgradePosts.Remove(postKey);
                    continue;
                }
                if (InUpgradeBackoff(postKey, st.Tag, candidate.HubNodeId, world.UtcNow, tuning, log)) continue;

                List<SpatialNode> free = FreeYardsNearest(yards, busyYards, hub);
                if (free.Count == 0) continue;
                SpatialNode yard = free[0];
                SpatialNode via = MineTargeting.HardestGravityDeposit(owned, hub.Position, world.Planets, tuning, hub);
                string pick = ExpectedMinePick(st, world, tuning, yard, hub, via);
                double pickRate = pick != null ? cat.DrillYieldScore(pick) * perScore : 0;
                if (!ExtractionCapacity.WantsHubUpgrade(candidate.Orders, pickRate, tuning)) continue;

                // The floor is the bar itself: a hull that does not clear it is not the upgrade, so
                // a slow drill parked at the yard, or one the relocation tier could fetch, must not
                // answer the demand as met.
                world.Demand.Post(DemandEntry.ForShip(st.Tag, MineRole, yard.Id,
                    world.UtcNow.Add(DemandTtlFor(world.Tuning)), destNodeId: hub.Id, minCargoLiters: 1,
                    viaNodeId: via != null ? via.Id : null, postedUtc: world.UtcNow,
                    minDrillYield: bar / perScore));
                busyYards.Add(yard.Id);
                budget--;
                string ores = candidate.OreList();
                _upgradePosts[postKey] = new UpgradePost { PostedUtc = world.UtcNow, YardId = yard.Id, Ores = ores };

                log.Add("EXTRACTION_HULL_UPGRADE " + st.Tag + " hub=" + hub.Id + " ore=" + ores
                        + " working=" + basis.PrefabId + "@" + Rate(basis.LitersPerSecond)
                        + " hulls=" + CountHulls(candidate.Hulls).ToString(CultureInfo.InvariantCulture)
                        + " pick=" + pick + "@" + Rate(pickRate)
                        + " yard=" + yard.Id);
            }
        }

        /// <summary>One hub's case for a drill-quality upgrade: its open deposit-bound orders, their
        /// pooled working hulls, and the largest of their gaps, which orders the hubs.</summary>
        private sealed class HubUpgrade
        {
            public string HubNodeId;
            public readonly List<StandingExtractionOrder> Orders = new List<StandingExtractionOrder>();
            public List<ExtractionHull> Hulls;
            public double Gap;

            /// <summary>The ores the upgrade serves, ordinal order, comma-separated.</summary>
            public string OreList()
            {
                var ores = new List<string>(Orders.Count);
                foreach (StandingExtractionOrder o in Orders) ores.Add(o.OreKey ?? "?");
                ores.Sort(StringComparer.Ordinal);
                return string.Join(",", ores);
            }
        }

        /// <summary>The hubs with a case for an upgrade, largest gap first (hub id on a tie): hubs with
        /// an open deposit-bound order, a working hull across those orders and a missing rate on at
        /// least one. Null when there is none.</summary>
        private static List<HubUpgrade> HubUpgradeCandidates(IList<StandingExtractionOrder> orders)
        {
            Dictionary<string, HubUpgrade> byHub = null;
            foreach (StandingExtractionOrder o in orders)
            {
                if (o == null || o.HubNodeId == null || o.Paused || o.Bound != ExtractionBound.DepositBound) continue;
                if (byHub == null) byHub = new Dictionary<string, HubUpgrade>(StringComparer.Ordinal);
                HubUpgrade h;
                if (!byHub.TryGetValue(o.HubNodeId, out h)) byHub[o.HubNodeId] = h = new HubUpgrade { HubNodeId = o.HubNodeId };
                h.Orders.Add(o);
                if (o.GapPerHour > h.Gap) h.Gap = o.GapPerHour;
            }
            if (byHub == null) return null;

            List<HubUpgrade> candidates = null;
            foreach (HubUpgrade h in byHub.Values)
            {
                if (ExtractionCapacity.HubMissingLitersPerHour(h.Orders) <= 0) continue;
                h.Hulls = ExtractionCapacity.PooledWorkingHulls(h.Orders);
                if (ExtractionCapacity.UpgradeBasis(h.Hulls) == null) continue;
                (candidates ?? (candidates = new List<HubUpgrade>())).Add(h);
            }
            if (candidates != null)
                candidates.Sort((a, b) =>
                {
                    int cmp = b.Gap.CompareTo(a.Gap);
                    return cmp != 0 ? cmp : string.CompareOrdinal(a.HubNodeId, b.HubNodeId);
                });
            return candidates;
        }

        /// <summary>True while <paramref name="postKey"/>'s last upgrade vanished without a build and
        /// the retry window has not run out. Logs <c>EXTRACTION_HULL_UPGRADE_LAPSED</c> once per lapsed
        /// post; the entry is dropped once the window has passed, so the next pass may post again.</summary>
        private bool InUpgradeBackoff(string postKey, string tag, string hubNodeId, DateTime now,
            FactionTuning tuning, List<string> log)
        {
            UpgradePost post;
            if (!_upgradePosts.TryGetValue(postKey, out post)) return false;
            double age = (now - post.PostedUtc).TotalSeconds;
            if (age >= tuning.ExtractionUpgradeRetrySeconds)
            {
                _upgradePosts.Remove(postKey);
                return false;
            }
            if (!post.LapseLogged)
            {
                post.LapseLogged = true;
                log.Add("EXTRACTION_HULL_UPGRADE_LAPSED " + tag + " hub=" + hubNodeId + " ore=" + post.Ores
                        + " yard=" + post.YardId
                        + " posted=" + age.ToString("0", CultureInfo.InvariantCulture) + "s"
                        + " retry_in=" + (tuning.ExtractionUpgradeRetrySeconds - age).ToString("0", CultureInfo.InvariantCulture) + "s");
            }
            return true;
        }

        /// <summary>Drops every remembered upgrade post older than the retry window, so an order that
        /// is gone does not leave its entry behind.</summary>
        private void ForgetOldUpgradePosts(DateTime now, FactionTuning tuning)
        {
            if (_upgradePosts.Count == 0) return;
            List<string> old = null;
            foreach (KeyValuePair<string, UpgradePost> kv in _upgradePosts)
                if ((now - kv.Value.PostedUtc).TotalSeconds >= tuning.ExtractionUpgradeRetrySeconds)
                    (old ?? (old = new List<string>())).Add(kv.Key);
            if (old != null)
                foreach (string key in old) _upgradePosts.Remove(key);
        }

        private static string Rate(double litersPerSecond)
        {
            return litersPerSecond.ToString("0.#", CultureInfo.InvariantCulture);
        }

        /// <summary>A fleet build that will produce a mining hull: a Mine entry, or a build named by
        /// hull whose hull is drill-equipped (<see cref="EntryAnswersRole"/>).</summary>
        private static bool IsMineBuild(BuildQueueEntry e, IPrefabCatalogue cat)
        {
            return !e.DestinedForSale && EntryAnswersRole(e, MineRole, cat);
        }

        private static int CountHulls(List<ExtractionHull> hulls)
        {
            int n = 0;
            if (hulls != null)
                foreach (ExtractionHull h in hulls) n += h.Count;
            return n;
        }

        /// <summary>The prefab the Mine build at <paramref name="yard"/> would pick for a hull serving
        /// <paramref name="hub"/> — the build's own <see cref="SelectPrefabBom"/> call, with the
        /// route and via legs the posted demand carries. Null when no candidate qualifies.</summary>
        private string ExpectedMinePick(FactionState st, WorldModel world, FactionTuning tuning,
            SpatialNode yard, SpatialNode hub, SpatialNode via)
        {
            bool ownLiveryOnly;
            IList<string> candidates = CandidatesFor(MineRole, st, world, false, out ownLiveryOnly);
            if (candidates == null || candidates.Count == 0) return null;

            ShipPool.RouteEnvironment buildRoute = ShipPool.RouteEnvironment.From(
                world.Planets, yard.Position, hub.Position, tuning.MinDryTwr);
            ShipPool.RouteEnvironment? viaRoute = null;
            if (via != null)
                viaRoute = ShipPool.RouteEnvironment.From(world.Planets, yard.Position, via.Position, tuning.MinDryTwr);

            string prefabId;
            bool zoneFallback, universalFallback, cargoClamped, dpsClamped;
            IList<string> rankedPool;
            SelectPrefabBom(MineRole, candidates, world, st.MilitaryPosture, null, buildRoute,
                FleetFitness.SizingFloor(st, MineRole, tuning, 1, false), out prefabId,
                new Dictionary<string, long>(), out zoneFallback, out universalFallback, out cargoClamped,
                out rankedPool, viaRoute, null, false, 0, out dpsClamped, ownLiveryOnly);
            return prefabId;
        }

        /// <summary>Yards not in <paramref name="busy"/>, nearest <paramref name="hub"/> first, ties by
        /// node id.</summary>
        private static List<SpatialNode> FreeYardsNearest(List<SpatialNode> yards, HashSet<string> busy, SpatialNode hub)
        {
            var free = new List<SpatialNode>();
            foreach (SpatialNode y in yards)
                if (!busy.Contains(y.Id)) free.Add(y);
            free.Sort((a, b) =>
            {
                int cmp = a.Position.DistanceTo(hub.Position).CompareTo(b.Position.DistanceTo(hub.Position));
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Id, b.Id);
            });
            return free;
        }

        private static int CompareByGapDescending(StandingExtractionOrder a, StandingExtractionOrder b)
        {
            int cmp = b.GapPerHour.CompareTo(a.GapPerHour);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(a.HubNodeId, b.HubNodeId);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.OreKey, b.OreKey);
        }
    }
}
