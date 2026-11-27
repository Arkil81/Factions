using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class FactionIntelCensusTests
    {
        [Fact]
        public void Take_CountsStagesDefenceAndTheStrongestArmedFleet_PerFaction()
        {
            FactionTuning t = TestSupport.Tuning();
            var g = new SpatialGraph();
            var a1 = new SpatialNode { Id = "A1", OwnerFactionTag = "AAA", StableStationId = 1 };
            a1.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 3000, RangeM = 800 } };
            var a2 = new SpatialNode { Id = "A2", OwnerFactionTag = "AAA" };
            var u = new SpatialNode { Id = "U", OwnerFactionTag = NeutralFaction.Tag, StableStationId = 2 };
            foreach (SpatialNode n in new[] { a1, a2, u }) g.Nodes[n.Id] = n;
            var st = new FactionState { Tag = "AAA" };
            st.PendingFleets.Add(new PendingFleetAction { WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 900, RangeM = 500 } } });
            st.PendingFleets.Add(new PendingFleetAction { WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 4000, RangeM = 500 } } });
            st.PendingFleets.Add(new PendingFleetAction());

            List<FactionIntelCensus.Row> rows = FactionIntelCensus.Take(g, new List<FactionState> { st }, null, t,
                                                                        tag => 1234, tag => 50);

            FactionIntelCensus.Row r = Assert.Single(rows);
            Assert.Equal("AAA", r.Tag);
            Assert.Equal(2, r.Nodes);
            Assert.Equal(2, r.ArmedFleets);
            Assert.Equal(4000, r.StrongestFleet, 6);
            Assert.Equal(CombatStrength.OfNodeDefence(a1, null, t), r.DefenceSum, 6);
            Assert.Equal(1234, r.Ledger);
            Assert.Contains("factionintel: factions=1", FactionIntelCensus.Describe(rows));
        }
    }
}
