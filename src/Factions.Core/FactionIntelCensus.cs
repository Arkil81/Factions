using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>
    /// Every faction's true figures for the Factions page Overview's word bands: its nodes by
    /// economic stage, its ledger, the strongest armed fleet it has in flight, and the defence of its
    /// stations by the offensive gate's own arithmetic.
    ///
    /// <para>The distribution is what the wealth and fleet-strength band edges of the intelligence
    /// grades are read off (docs/fog-of-war.md → The Factions page, slice 5), as the defence pips were
    /// read off <see cref="NodeDefenceCensus"/>. The estimate a viewer sees is built from what they
    /// have graded; this is what a viewer with full intel would see.</para>
    /// </summary>
    public static class FactionIntelCensus
    {
        public struct Row
        {
            public string Tag;
            public int Nodes;
            /// <summary>Owned nodes by <see cref="NodeStage"/>, indexed by its ordinal.</summary>
            public int[] Stages;
            public long Ledger;
            public int ArmedFleets;
            public double StrongestFleet;
            public double FleetPower;
            public double DefenceSum;
            public double DefenceMax;
        }

        /// <summary>Factions with at least one owned node, the claimable placeholder excepted, most
        /// nodes first.</summary>
        public static List<Row> Take(SpatialGraph graph, IList<FactionState> states, IPrefabCatalogue catalogue,
                                     FactionTuning tuning, Func<string, long> ledgerOf,
                                     Func<string, long> fleetPowerOf)
        {
            var byTag = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            if (graph == null || tuning == null) return new List<Row>();
            NodeStages stages = NodeStages.Build(graph, tuning);
            Dictionary<string, List<WeaponBand>> guarded = states != null
                ? FleetManager.BuildGuardedStrengthIndex(states)
                : new Dictionary<string, List<WeaponBand>>();
            foreach (SpatialNode n in graph.Nodes.Values)
            {
                if (n == null || string.IsNullOrEmpty(n.OwnerFactionTag) || NeutralFaction.IsClaimable(n.OwnerFactionTag))
                    continue;
                Row r;
                if (!byTag.TryGetValue(n.OwnerFactionTag, out r))
                    r = new Row { Tag = n.OwnerFactionTag, Stages = new int[(int)NodeStage.Capital + 1] };
                r.Nodes++;
                NodeStage s = stages.Of(n);
                if ((int)s < r.Stages.Length) r.Stages[(int)s]++;
                if (n.StableStationId > 0)
                {
                    List<WeaponBand> standing;
                    guarded.TryGetValue(n.Id, out standing);
                    double d = CombatStrength.OfNodeDefence(n, catalogue, tuning, standing);
                    r.DefenceSum += d;
                    if (d > r.DefenceMax) r.DefenceMax = d;
                }
                byTag[n.OwnerFactionTag] = r;
            }
            if (states != null)
            {
                foreach (FactionState st in states)
                {
                    Row r;
                    if (st == null || !byTag.TryGetValue(st.Tag ?? "", out r)) continue;
                    foreach (PendingFleetAction f in st.PendingFleets)
                    {
                        double dps = ShipPool.TotalDps(f.WeaponBands);
                        if (dps <= 0) continue;
                        r.ArmedFleets++;
                        if (dps > r.StrongestFleet) r.StrongestFleet = dps;
                    }
                    byTag[st.Tag] = r;
                }
            }
            var rows = new List<Row>(byTag.Count);
            foreach (Row r0 in byTag.Values)
            {
                Row r = r0;
                r.Ledger = ledgerOf != null ? ledgerOf(r.Tag) : 0;
                r.FleetPower = fleetPowerOf != null ? fleetPowerOf(r.Tag) : 0;
                rows.Add(r);
            }
            rows.Sort((a, b) =>
            {
                int c = b.Nodes.CompareTo(a.Nodes);
                return c != 0 ? c : string.CompareOrdinal(a.Tag, b.Tag);
            });
            return rows;
        }

        /// <summary>A stage-weighted node count: Outpost 1, Settlement 2, Hub 4, Capital 8. The
        /// census reports it beside the ledger so the weights can be checked against the real
        /// figure before a band is drawn on them.</summary>
        public static double StageScore(Row r)
        {
            if (r.Stages == null) return 0;
            return r.Stages[(int)NodeStage.Outpost] + 2.0 * r.Stages[(int)NodeStage.Settlement]
                   + 4.0 * r.Stages[(int)NodeStage.Hub] + 8.0 * r.Stages[(int)NodeStage.Capital];
        }

        public static string Describe(List<Row> rows, int listLimit = 80)
        {
            var sb = new StringBuilder();
            sb.Append("factionintel: factions=").Append(rows.Count);
            if (rows.Count > 0)
            {
                Dist(sb, "nodes", rows, r => r.Nodes);
                Dist(sb, "stations", rows, r => r.Stages[(int)NodeStage.Settlement] + r.Stages[(int)NodeStage.Hub] + r.Stages[(int)NodeStage.Capital]);
                Dist(sb, "stageScore", rows, StageScore);
                Dist(sb, "ledger", rows, r => r.Ledger);
                Dist(sb, "armedFleets", rows, r => r.ArmedFleets);
                Dist(sb, "strongestFleet", rows, r => r.StrongestFleet);
                Dist(sb, "fleetPower", rows, r => r.FleetPower);
                Dist(sb, "defenceSum", rows, r => r.DefenceSum);
                Dist(sb, "defenceMax", rows, r => r.DefenceMax);
            }
            int shown = 0;
            foreach (Row r in rows)
            {
                if (shown++ >= listLimit) break;
                sb.Append("\n  ").Append(r.Tag)
                  .Append(" nodes=").Append(r.Nodes)
                  .Append(" f/o/s/h/c=").Append(r.Stages[(int)NodeStage.Field]).Append('/')
                  .Append(r.Stages[(int)NodeStage.Outpost]).Append('/').Append(r.Stages[(int)NodeStage.Settlement])
                  .Append('/').Append(r.Stages[(int)NodeStage.Hub]).Append('/').Append(r.Stages[(int)NodeStage.Capital])
                  .Append(" score=").Append(F(StageScore(r)))
                  .Append(" ledger=").Append(r.Ledger.ToString(CultureInfo.InvariantCulture))
                  .Append(" fleets=").Append(r.ArmedFleets)
                  .Append(" strongest=").Append(F(r.StrongestFleet))
                  .Append(" power=").Append(F(r.FleetPower))
                  .Append(" defSum=").Append(F(r.DefenceSum))
                  .Append(" defMax=").Append(F(r.DefenceMax));
            }
            if (rows.Count > shown) sb.Append("\n  (").Append(rows.Count - listLimit).Append(" more)");
            return sb.ToString();
        }

        private static void Dist(StringBuilder sb, string name, List<Row> rows, Func<Row, double> of)
        {
            var v = new List<double>(rows.Count);
            int zeros = 0;
            foreach (Row r in rows)
            {
                double x = of(r);
                if (x <= 0) zeros++;
                v.Add(x);
            }
            v.Sort();
            sb.Append("\n  ").Append(name).Append(": zero=").Append(zeros)
              .Append(" p10=").Append(F(NodeDefenceCensus.Quantile(v, 0.10)))
              .Append(" p25=").Append(F(NodeDefenceCensus.Quantile(v, 0.25)))
              .Append(" p50=").Append(F(NodeDefenceCensus.Quantile(v, 0.50)))
              .Append(" p75=").Append(F(NodeDefenceCensus.Quantile(v, 0.75)))
              .Append(" p90=").Append(F(NodeDefenceCensus.Quantile(v, 0.90)))
              .Append(" max=").Append(F(v[v.Count - 1]));
        }

        private static string F(double x)
        {
            return x.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
