using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>
    /// What every faction-held station would meet an attack with, by the offensive gate's own
    /// arithmetic: <see cref="CombatStrength.OfNodeDefence(SpatialNode,IPrefabCatalogue,FactionTuning,IReadOnlyList{WeaponBand})"/>
    /// with the Guard committed to the node counted, as <c>FleetManager</c>'s commit gate reads it.
    ///
    /// <para>The distribution is what the defence band edges of the intelligence grades are read off
    /// (docs/fog-of-war.md → Economic stage, slice 4), and the per-faction list is the read-out an
    /// operator uses to see why a campaign is refused.</para>
    /// </summary>
    public static class NodeDefenceCensus
    {
        public struct Row
        {
            public string NodeId;
            public string OwnerTag;
            public double Station;
            public double Parked;
            public double Standing;
            public double Total { get { return Station + Parked + Standing; } }
        }

        /// <summary>Stations held by a faction that is not claimable (<c>UNKN</c> holds nothing it
        /// defends), sorted strongest first.</summary>
        public static List<Row> Take(SpatialGraph graph, IList<FactionState> states, IPrefabCatalogue catalogue,
                                     FactionTuning tuning)
        {
            var rows = new List<Row>();
            if (graph == null || tuning == null) return rows;
            Dictionary<string, List<WeaponBand>> guarded = states != null
                ? FleetManager.BuildGuardedStrengthIndex(states)
                : new Dictionary<string, List<WeaponBand>>();
            foreach (SpatialNode n in graph.Nodes.Values)
            {
                if (n == null || n.StableStationId <= 0) continue;
                if (string.IsNullOrEmpty(n.OwnerFactionTag) || NeutralFaction.IsClaimable(n.OwnerFactionTag)) continue;
                List<WeaponBand> standing;
                guarded.TryGetValue(n.Id, out standing);
                double withStation = CombatStrength.OfNodeDefence(n, catalogue, tuning);
                double station = CombatStrength.OfStation(n, tuning);
                rows.Add(new Row
                {
                    NodeId = n.Id,
                    OwnerTag = n.OwnerFactionTag,
                    Station = station,
                    Parked = withStation - station,
                    Standing = ShipPool.TotalDps(standing),
                });
            }
            rows.Sort((a, b) =>
            {
                int c = b.Total.CompareTo(a.Total);
                return c != 0 ? c : string.CompareOrdinal(a.NodeId, b.NodeId);
            });
            return rows;
        }

        /// <summary>The value at quantile <paramref name="q"/> of an ascending list, nearest rank.</summary>
        public static double Quantile(IList<double> ascending, double q)
        {
            if (ascending == null || ascending.Count == 0) return 0;
            int i = (int)Math.Ceiling(q * ascending.Count) - 1;
            if (i < 0) i = 0;
            if (i >= ascending.Count) i = ascending.Count - 1;
            return ascending[i];
        }

        /// <summary>The read-out: the distribution over every row and of each term, then — for
        /// <paramref name="tag"/>, when given — that faction's stations.</summary>
        public static string Describe(List<Row> rows, string tag, int listLimit = 40)
        {
            var sb = new StringBuilder();
            sb.Append("defence: stations=").Append(rows.Count);
            if (rows.Count > 0)
            {
                AppendDistribution(sb, "total", rows, r => r.Total);
                AppendDistribution(sb, "station", rows, r => r.Station);
                AppendDistribution(sb, "parked", rows, r => r.Parked);
                AppendDistribution(sb, "standing", rows, r => r.Standing);
            }
            if (!string.IsNullOrEmpty(tag))
            {
                int shown = 0, held = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    Row r = rows[i];
                    if (!string.Equals(r.OwnerTag, tag, StringComparison.OrdinalIgnoreCase)) continue;
                    held++;
                    if (shown >= listLimit) continue;
                    shown++;
                    sb.Append("\n  ").Append(r.NodeId).Append(' ').Append(r.OwnerTag)
                      .Append(" total=").Append(F(r.Total))
                      .Append(" station=").Append(F(r.Station))
                      .Append(" parked=").Append(F(r.Parked))
                      .Append(" standing=").Append(F(r.Standing));
                }
                sb.Append("\n  ").Append(tag).Append(": ").Append(held).Append(" stations")
                  .Append(held > shown ? ", strongest " + shown + " shown" : "");
            }
            return sb.ToString();
        }

        private static void AppendDistribution(StringBuilder sb, string name, List<Row> rows, Func<Row, double> of)
        {
            var v = new List<double>(rows.Count);
            int zeros = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                double x = of(rows[i]);
                if (x <= 0) zeros++;
                v.Add(x);
            }
            v.Sort();
            sb.Append("\n  ").Append(name).Append(": zero=").Append(zeros)
              .Append(" p10=").Append(F(Quantile(v, 0.10)))
              .Append(" p25=").Append(F(Quantile(v, 0.25)))
              .Append(" p50=").Append(F(Quantile(v, 0.50)))
              .Append(" p75=").Append(F(Quantile(v, 0.75)))
              .Append(" p90=").Append(F(Quantile(v, 0.90)))
              .Append(" p99=").Append(F(Quantile(v, 0.99)))
              .Append(" max=").Append(F(v[v.Count - 1]));
        }

        private static string F(double x)
        {
            return x.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
