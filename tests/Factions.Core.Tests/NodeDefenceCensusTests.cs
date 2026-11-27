using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class NodeDefenceCensusTests
    {
        private static SpatialNode Station(string id, string owner, float dps)
        {
            var n = new SpatialNode { Id = id, OwnerFactionTag = owner, StableStationId = 1 };
            if (dps > 0) n.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = 800 } };
            return n;
        }

        [Fact]
        public void Take_CountsFactionHeldStationsByTheGatesArithmetic_StrongestFirst()
        {
            FactionTuning t = TestSupport.Tuning();
            var g = new SpatialGraph();
            foreach (SpatialNode n in new[]
            {
                Station("A", "AAA", 1000), Station("B", "AAA", 5000), Station("C", "BBB", 0),
                Station("D", NeutralFaction.Tag, 9000), Station("E", null, 9000),
                new SpatialNode { Id = "F", OwnerFactionTag = "AAA" },   // a claim, no station
            })
                g.Nodes[n.Id] = n;

            List<NodeDefenceCensus.Row> rows = NodeDefenceCensus.Take(g, new List<FactionState>(), null, t);

            Assert.Equal(new[] { "B", "A", "C" }, rows.ConvertAll(r => r.NodeId).ToArray());
            Assert.Equal(CombatStrength.OfNodeDefence(g.Nodes["B"], null, t), rows[0].Total, 6);
            Assert.Equal(0.0, rows[0].Parked, 6);
        }

        [Fact]
        public void Quantile_IsNearestRank()
        {
            var v = new List<double> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            Assert.Equal(5, NodeDefenceCensus.Quantile(v, 0.5));
            Assert.Equal(9, NodeDefenceCensus.Quantile(v, 0.9));
            Assert.Equal(1, NodeDefenceCensus.Quantile(v, 0.0));
            Assert.Equal(10, NodeDefenceCensus.Quantile(v, 1.0));
            Assert.Equal(0, NodeDefenceCensus.Quantile(new List<double>(), 0.5));
        }

        [Fact]
        public void Describe_ListsOneFactionOnlyWhenAsked()
        {
            var rows = new List<NodeDefenceCensus.Row>
            {
                new NodeDefenceCensus.Row { NodeId = "N1", OwnerTag = "AAA", Station = 100 },
                new NodeDefenceCensus.Row { NodeId = "N2", OwnerTag = "BBB", Station = 50 },
            };
            string all = NodeDefenceCensus.Describe(rows, null);
            Assert.StartsWith("defence: stations=2", all, System.StringComparison.Ordinal);
            Assert.DoesNotContain("N1 AAA", all, System.StringComparison.Ordinal);
            string aaa = NodeDefenceCensus.Describe(rows, "aaa");
            Assert.Contains("N1 AAA total=100", aaa, System.StringComparison.Ordinal);
            Assert.DoesNotContain("N2", aaa, System.StringComparison.Ordinal);
        }
    }
}
