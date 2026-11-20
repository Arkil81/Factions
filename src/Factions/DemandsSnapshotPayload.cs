using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>Builds the JSON payload of a <c>"demands"</c> live event. Split out of
    /// <see cref="LiveEventWriter"/> so the per-kind row cap is testable without a file or a host.
    /// <para>The cap is what keeps one snapshot on one line of a reasonable size: a faction's demand
    /// set runs to thousands of rows (live 2026-08-22: MTDW 5,961), and every row is shipped to each
    /// dashboard client on every flush sweep and rendered as its own DOM node. Beyond a couple of
    /// hundred rows per kind the panel is not readable anyway, so the snapshot carries a bounded
    /// prefix plus <c>demandTotals</c> — the full per-kind counts — and the panel says how many it is
    /// not showing.</para>
    /// <para><b>Why a row carries its class, its urgency and the stock behind it.</b> Without them a
    /// reader can see that a demand is large and old and nothing else — not that it is a
    /// <c>RecipeInput</c> ranking at half the weight of the <c>ShipBuild</c> it feeds, and not that
    /// its quantity is an appetite no dispatch could close. Both are what
    /// docs/demand-chains.md is about, and neither was visible from the dashboard before. The three
    /// come from <see cref="DemandSnapshotRow"/>, which is what the ranking already measured, so
    /// they cost no extra lookup and cannot disagree with the order the rows arrive in.</para></summary>
    public static class DemandsSnapshotPayload
    {
        /// <summary>Rows carried per kind bucket ("Ship:", "Position:", "Item:", anything else).
        /// Capped per bucket rather than over the flat list so a faction with thousands of item
        /// demands cannot crowd out its handful of ship demands.</summary>
        public const int MaxRowsPerKind = 200;

        private const string ShipPrefix = "Ship:";
        private const string PositionPrefix = "Position:";
        private const string ItemPrefix = "Item:";

        internal static string Iso(DateTime dt)
            => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        public static Dictionary<string, object> Build(string factionTag,
                                                       DateTime atUtc,
                                                       List<DemandSnapshotRow> demands,
                                                       List<InvestmentSignal> investments)
        {
            int shipTotal = 0, positionTotal = 0, itemTotal = 0, otherTotal = 0;
            int shipKept = 0, positionKept = 0, itemKept = 0, otherKept = 0;

            var demandArr = new List<object>(Math.Min(demands.Count, MaxRowsPerKind * 4));
            foreach (DemandSnapshotRow snapshotRow in demands)
            {
                DemandEntry d = snapshotRow.Demand;
                string kind = d.Kind ?? "";
                bool keep;
                if (kind.StartsWith(ShipPrefix, StringComparison.Ordinal))
                {
                    shipTotal++;
                    keep = shipKept < MaxRowsPerKind;
                    if (keep) shipKept++;
                }
                else if (kind.StartsWith(PositionPrefix, StringComparison.Ordinal))
                {
                    positionTotal++;
                    keep = positionKept < MaxRowsPerKind;
                    if (keep) positionKept++;
                }
                else if (kind.StartsWith(ItemPrefix, StringComparison.Ordinal))
                {
                    itemTotal++;
                    keep = itemKept < MaxRowsPerKind;
                    if (keep) itemKept++;
                }
                else
                {
                    otherTotal++;
                    keep = otherKept < MaxRowsPerKind;
                    if (keep) otherKept++;
                }
                if (!keep) continue;

                var row = new Dictionary<string, object>
                {
                    { "kind",   kind },
                    { "nodeId", d.NodeId ?? "" },
                    { "qty",    d.Quantity },
                    { "exp",    Iso(d.ExpiresUtc) },
                    // What raised the demand, which is the half of docs/demand-chains.md no
                    // quantity shows: an Ore:Iron row classed RecipeInput and the Comp:Thrust
                    // ShipBuild it feeds are the same shortfall at 2.0 and 4.0.
                    { "cls",    d.Class.ToString() },
                };
                // Route constraints, carried only when they constrain anything. Live-measured
                // 2026-08-31 over 10,812 rows: minCargoLiters was set on 427, requiredPlanetId on 32
                // and requiredZone on 37, so emitting all three unconditionally spent ~665 KB of a
                // 2.3 MB window on defaults -- the same waste destNodeId/viaNodeId were fixed for
                // below, one field group over.
                if (d.MinCargoLiters > 0) row["minCargoLiters"] = d.MinCargoLiters;
                if (d.RequiredPlanetId != null) row["requiredPlanetId"] = d.RequiredPlanetId;
                if (d.RequiredZone.HasValue) row["requiredZone"] = d.RequiredZone.Value.ToString();
                // Only for a row that names an item at a live node. Absent means "not applicable",
                // never "zero" -- a demand at a node holding none of the key still carries have=0,
                // which is the reading that says the shortfall is total.
                if (snapshotRow.Scored)
                {
                    row["urgency"] = Math.Round(snapshotRow.Urgency, 3);
                    row["have"]    = snapshotRow.HaveAtNode;
                }
                // Ship: demands only -- omitted entirely rather than written as null, per this repo's
                // lean-JSON rule. Item:/Position: demands are the overwhelming majority of a snapshot
                // and never carry any of the three, so emitting them unconditionally spent about a
                // quarter of events.jsonl on literal nulls (live-measured 2026-08-22: ~1.0 MB of a
                // 4 MB window across 18k demands).
                if (d.DestNodeId != null) row["destNodeId"] = d.DestNodeId;
                if (d.ViaNodeId != null) row["viaNodeId"] = d.ViaNodeId;
                // Part of the register's dedup key (destNodeId) and the real intermediate leg a built
                // hull must survive (viaNodeId) -- without them several distinct demands at one node
                // render as identical rows. firstPostedUtc survives TTL refresh, so it is the only
                // way to see how long a demand has gone unmet.
                if (d.FirstPostedUtc != default(DateTime)) row["firstPostedUtc"] = Iso(d.FirstPostedUtc);
                demandArr.Add(row);
            }

            var invArr = new List<object>(investments.Count);
            foreach (InvestmentSignal sig in investments)
            {
                var inv = new Dictionary<string, object>
                {
                    { "cap",            sig.Request.Cap.ToString() },
                    { "urgency",        sig.Request.Urgency },
                    { "exp",            Iso(sig.Request.ExpiresUtc) },
                    { "targetNodeId",   sig.Target.NodeId },
                    { "targetPlanetId", sig.Request.TargetPlanetId },
                    // Which half of that body was asked for -- its ground or its well. A request that
                    // named a region no owned node stands in resolves to nothing at all, and says so
                    // through blockedReason rather than being placed somewhere else.
                    { "targetSite",     sig.Request.TargetSite.HasValue ? sig.Request.TargetSite.Value.ToString() : null },
                    // Or the authored feature, for a request scoped to a region no body claims.
                    { "targetFeature",  sig.Request.TargetFeature },
                    // The crop a Farm was asked for, which is where it may be placed.
                    { "crop",           sig.Request.Crop },
                    { "onTargetPlanet", sig.Target.OnTargetPlanet },
                    { "blockedReason",  sig.Target.Resolved ? null : InvestmentTargeting.Describe(sig.Target.Block) },
                    { "affordable",     sig.Affordable },
                };
                // The install's material gate, in the same need/held shape a Shipyard BuildQueueEntry
                // reports its BOM. A handful of rows per request, and only for requests that cost
                // items at all -- omitted entirely rather than written as an empty array.
                if (sig.Materials != null && sig.Materials.Count > 0)
                {
                    var bomArr = new List<object>(sig.Materials.Count);
                    foreach (InstallMaterialRow row in sig.Materials)
                    {
                        var r = new Dictionary<string, object>
                        {
                            { "key",      row.Key },
                            { "need",     row.Need },
                            { "reserved", row.Reserved },
                            { "stock",    row.Routable },
                        };
                        // Only on the flat-cost path, where the key names an ItemKind bucket rather
                        // than a component -- the common BOM row carries no marker.
                        if (row.ByKind) r["byKind"] = true;
                        bomArr.Add(r);
                    }
                    inv["bom"] = bomArr;
                }
                invArr.Add(inv);
            }

            var payload = new Dictionary<string, object>
            {
                { "type",        "demands" },
                { "at",          Iso(atUtc) },
                { "factionTag",  factionTag },
                { "demands",     demandArr },
                { "investments", invArr },
            };

            // Omitted when nothing was capped: the array is then the whole truth and the panel reads
            // the totals off it, so shipping them would only repeat what is already on the line.
            if (shipTotal > shipKept || positionTotal > positionKept
                || itemTotal > itemKept || otherTotal > otherKept)
            {
                var totals = new Dictionary<string, object>
                {
                    { "ship",     shipTotal },
                    { "position", positionTotal },
                    { "item",     itemTotal },
                };
                if (otherTotal > 0) totals["other"] = otherTotal;
                payload["demandTotals"] = totals;
            }

            return payload;
        }
    }
}
