using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>What one region of an estate holds and does at the ore stage, summed by the
    /// throughput passes. The network-wide rates <see cref="ProductionManager"/> already measures
    /// answer "is the estate in balance"; this answers "where is the ore", which those rates cannot —
    /// a region holding a million ore and no refinery reads as balanced when its ore is counted
    /// against a refinery somewhere else.</summary>
    internal sealed class RegionOreLedger
    {
        /// <summary>Owned nodes standing in the region.</summary>
        public int Nodes;
        /// <summary>A founded station in the region below the Refinery level cap — somewhere an
        /// install could land. Read with the same cap <see cref="InvestmentTargeting"/> resolves
        /// against, so the ask and the placement cannot disagree about it.</summary>
        public bool Hostable;
        /// <summary>Minted ore yield of the region's nodes, units per minute.</summary>
        public double OreIncomePerMin;
        /// <summary>Ore units held across the region's nodes (<c>Ore:*</c> inventory).</summary>
        public double OreStock;
        /// <summary>Refinery-tier machines installed in the region, however busy.</summary>
        public int RefineMachines;
        /// <summary>Ore the region's busy refinery slots consume, units per minute.</summary>
        public double OreConsumedPerMin;
    }

    public sealed partial class ProductionManager
    {
        /// <summary>Last ore stock read per (faction, region), so a pass can tell a region that is
        /// hoarding from one a haul stream is draining. In memory only: it is a reading of the
        /// previous pass and rebuilds itself one pass after any restart.</summary>
        private readonly Dictionary<string, double> _regionOreStockLast = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Last verdict logged per (faction, region), so the line is written on change
        /// rather than every pass — a hoarding region would otherwise print itself every 30 s.</summary>
        private readonly Dictionary<string, string> _regionVerdictLast = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>A region reads as being drained when its ore fell by more than this share of
        /// itself since the previous pass. Passes are ~30 s apart, so a haul convoy of a few
        /// thousand litres never trips it against a million-unit hoard; only a stream that is
        /// genuinely emptying the region does, and that region is being served, not stranded.</summary>
        private const double RegionDrainShare = 0.01;

        /// <summary>One lookup per node, taken once per pass and shared by the node and slot
        /// accumulators — the slot pass sees node ids, not nodes.</summary>
        private static Dictionary<string, NodeRegion> IndexNodeRegions(List<SpatialNode> owned)
        {
            var byId = new Dictionary<string, NodeRegion>(StringComparer.Ordinal);
            if (owned == null) return byId;
            foreach (SpatialNode n in owned)
                if (n != null && n.Id != null) byId[n.Id] = NodeRegion.Of(n);
            return byId;
        }

        private static RegionOreLedger EntryFor(Dictionary<NodeRegion, RegionOreLedger> ledger, NodeRegion region)
        {
            RegionOreLedger entry;
            if (!ledger.TryGetValue(region, out entry))
            {
                entry = new RegionOreLedger();
                ledger[region] = entry;
            }
            return entry;
        }

        /// <summary>Fold one node into its region's ledger. A node in no region (open space) is
        /// counted nowhere, the same rule <see cref="ShipyardCoverage.Census"/> applies.</summary>
        private void AccumulateRegionNode(Dictionary<NodeRegion, RegionOreLedger> ledger, SpatialNode n,
            Dictionary<string, NodeRegion> regionById, double oreIncomePerMin,
            IProductionExecutionContext ctx, FactionTuning tuning, HashSet<NodeCapabilityKind> refineCaps)
        {
            if (ledger == null || n == null) return;
            NodeRegion region;
            if (!regionById.TryGetValue(n.Id, out region) || region.IsNone) return;

            RegionOreLedger entry = EntryFor(ledger, region);
            entry.Nodes++;
            entry.OreIncomePerMin += oreIncomePerMin;
            foreach (KeyValuePair<string, long> kv in ctx.SnapshotNodeInventory(n.Id))
                if (kv.Value > 0 && kv.Key.StartsWith("Ore:", StringComparison.Ordinal)) entry.OreStock += kv.Value;

            int level;
            n.Capabilities.TryGetValue(NodeCapabilityKind.Refinery, out level);
            if (n.StableStationId != 0 && level < tuning.MaxLevelFor(NodeCapabilityKind.Refinery)) entry.Hostable = true;

            if (refineCaps == null) return;
            foreach (KeyValuePair<NodeCapabilityKind, int> cap in n.Capabilities)
            {
                if (cap.Value <= 0 || !refineCaps.Contains(cap.Key)) continue;
                int idx = (int)cap.Key;
                if (idx < 0 || idx >= _models.Length) continue;
                entry.RefineMachines += cap.Value * _models[idx].MachineCount;
            }
        }

        /// <summary>Fold one busy slot's ore draw into its node's region.</summary>
        private void AccumulateRegionSlot(Dictionary<NodeRegion, RegionOreLedger> ledger,
            Dictionary<string, NodeRegion> regionById, ThroughputSlotRef slot, HashSet<NodeCapabilityKind> refineCaps)
        {
            if (ledger == null || slot.NodeId == null) return;
            NodeRegion region;
            if (!regionById.TryGetValue(slot.NodeId, out region) || region.IsNone) return;
            double rate = SlotRatePerMin(slot, refineCaps, "Ore:", inputSide: true);
            if (rate <= 0) return;
            EntryFor(ledger, region).OreConsumedPerMin += rate;
        }

        /// <summary>
        /// The one rule on the ledger: a region holding founded stations and at least
        /// <see cref="FactionTuning.RegionOreHoardFloor"/> ore, with <b>no refining installed in
        /// it</b>, asks for a Refinery <em>there</em> — a region-scoped
        /// <see cref="InvestmentRequest"/> (<see cref="NodeRegion.Scope"/>), which
        /// <see cref="InvestmentTargeting"/> resolves only inside that region.
        ///
        /// <para><b>Why a separate rule and not a term in the network comparison.</b> The network
        /// stage sums ore income over the whole estate against refinery consumption over the whole
        /// estate; FCTM on 2026-09-05 held every refinery in one nest and four to six million ore in
        /// each of five others, and the stage read <c>urgency=0.00</c>. No weighting of two sums
        /// recovers where the ore is. Region-scoping the ask is the same move FAC-236 made for
        /// Shipyards, for the same reason: the remedy has to land where the need is.</para>
        ///
        /// <para><b>Urgency is the region's share of the faction's ore</b>, 0..1 — "how much of
        /// what I mine is stranded here". A measured ratio, per <see cref="ImbalanceUrgency"/>'s
        /// note on constants; a region holding a fifth of the estate's ore asks at 0.20 and one
        /// holding most of it asks louder.</para>
        ///
        /// <para><b>Three refusals</b>, each carried in the line: no founded station below the level
        /// cap in the region (<c>nowhere</c> — the same reason <see cref="ShipyardCoverage"/> refuses,
        /// an ask nobody can answer only reserves stock); the stock falling by more than
        /// <see cref="RegionDrainShare"/> since the previous pass (<c>draining</c> — a haul stream is
        /// already serving the region, and a refinery would compete with it for the same ore); and a
        /// player faction that has not opted in (<c>opt-in</c>). Below the absolute floor, or below
        /// <see cref="FactionTuning.RegionOreHoardMinShare"/> of the faction's ore, a region is not a
        /// candidate at all and prints nothing — the share gate is what keeps a three-node surface
        /// region holding 0.2% of an estate's ore from being handed a refinery (IGSL, first pass of
        /// v0.174.0).</para>
        ///
        /// <para>The <c>REGION_THROUGHPUT</c> line is written when the verdict changes, and on every
        /// fresh post — not every pass — so a standing hoard costs two lines, not one per 30 s.</para>
        /// </summary>
        internal void DecideRegionThroughputInvestments(FactionTuning tuning, FactionState st,
            IProductionExecutionContext ctx, Dictionary<NodeRegion, RegionOreLedger> ledger)
        {
            if (ledger == null || ledger.Count == 0) return;

            double factionOre = 0;
            foreach (KeyValuePair<NodeRegion, RegionOreLedger> kv in ledger) factionOre += kv.Value.OreStock;

            foreach (KeyValuePair<NodeRegion, RegionOreLedger> kv in ledger)
            {
                NodeRegion region = kv.Key;
                RegionOreLedger entry = kv.Value;
                string key = st.Tag + "|" + region.Describe();

                double prev;
                bool hasPrev = _regionOreStockLast.TryGetValue(key, out prev);
                _regionOreStockLast[key] = entry.OreStock;

                // A region that refines is the network stage's business; one below the floor is
                // holding, not hoarding. Neither is a candidate, and neither carries a verdict to
                // print -- but a region that WAS a candidate last pass and stopped being one says so
                // once, so the log shows the episode closing rather than going silent.
                double shareOfEstate = factionOre > 0 ? entry.OreStock / factionOre : 0;
                if (entry.RefineMachines > 0 || entry.OreStock < tuning.RegionOreHoardFloor
                    || shareOfEstate < tuning.RegionOreHoardMinShare)
                {
                    string closing = entry.RefineMachines > 0 ? "refines"
                        : entry.OreStock < tuning.RegionOreHoardFloor ? "below-floor" : "below-share";
                    string was;
                    if (_regionVerdictLast.TryGetValue(key, out was) && was != closing)
                    {
                        _regionVerdictLast[key] = closing;
                        ctx.AddLog(RegionLine(st.Tag, region, entry, hasPrev ? entry.OreStock - prev : 0, hasPrev, factionOre, closing));
                    }
                    continue;
                }

                double trend = hasPrev ? entry.OreStock - prev : 0;
                double share = shareOfEstate > 1.0 ? 1.0 : shareOfEstate;

                string verdict;
                bool posted = false;
                if (!entry.Hostable)
                    verdict = "refused=nowhere";
                else if (hasPrev && prev > 0 && -trend > prev * RegionDrainShare)
                    verdict = "refused=draining";
                else if (!FactionLifecycle.CanPostInvestmentFor(st, NodeCapabilityKind.Refinery))
                    verdict = "refused=opt-in";
                else
                {
                    posted = ctx.PostInvestment(region.Scope(new InvestmentRequest
                    {
                        FactionTag = st.Tag,
                        Cap        = NodeCapabilityKind.Refinery,
                        Urgency    = share,
                        ExpiresUtc = ctx.UtcNow.Add(InvestmentTtlFor(tuning)),
                    }));
                    verdict = posted ? "posted" : "standing";
                }

                string last;
                bool changed = !_regionVerdictLast.TryGetValue(key, out last) || last != verdict;
                _regionVerdictLast[key] = verdict;
                if (posted || changed)
                    ctx.AddLog(RegionLine(st.Tag, region, entry, trend, hasPrev, factionOre, verdict));
            }
        }

        private static string RegionLine(string tag, NodeRegion region, RegionOreLedger entry, double trend,
            bool hasTrend, double factionOre, string verdict)
        {
            double share = factionOre > 0 ? entry.OreStock / factionOre : 0;
            return tag + " REGION_THROUGHPUT " + region.Describe()
                + " ore=" + ((long)entry.OreStock).ToString(CultureInfo.InvariantCulture)
                + " trend=" + (hasTrend ? ((long)trend).ToString("+#;-#;0", CultureInfo.InvariantCulture) : "n/a")
                + " income=" + entry.OreIncomePerMin.ToString("F1", CultureInfo.InvariantCulture)
                + " refine=" + entry.RefineMachines.ToString(CultureInfo.InvariantCulture)
                + " consumed=" + entry.OreConsumedPerMin.ToString("F1", CultureInfo.InvariantCulture)
                + " share=" + share.ToString("F2", CultureInfo.InvariantCulture)
                + " nodes=" + entry.Nodes.ToString(CultureInfo.InvariantCulture)
                + " " + verdict;
        }
    }
}
