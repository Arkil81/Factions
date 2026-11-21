using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The Services Terminal's ship rows on a Nexus agent come from the authority's
    /// snapshot: a player faction's node carries its hull stock and listings there, an NPC's does not,
    /// and a withdrawal or send goes back as one Hull event.</summary>
    public class NexusHullStockTests
    {
        [Fact]
        public void APlayerNodeCarriesItsHullsAndAnNpcNodeDoesNot()
        {
            var g = new SpatialGraph();
            SpatialNode mine = g.Add(new Vec3(1, 2, 3), 1, "P1");
            mine.OwnerFactionTag = "EXP";
            mine.ShipStock["Kestrel"] = 3;
            mine.ListedForSale["Kestrel"] = 1;
            SpatialNode npc = g.Add(new Vec3(4, 5, 6), 1, "N1");
            npc.OwnerFactionTag = "IMPR";
            npc.ShipStock["Hauler"] = 9;
            var exp = TradeTest.State("EXP");
            exp.IsPlayerFaction = true;
            var factions = new List<FactionState> { exp, TradeTest.State("IMPR") };

            NexusNodeSnapshot back;
            Assert.True(NexusNodeSnapshot.TryDecode(NexusNodeSnapshot.Build(g.Nodes.Values, factions, 1).Encode(), out back));
            SpatialGraph there = back.ToGraph();

            Assert.Equal(3, there.Nodes["P1"].ShipStock["Kestrel"]);
            Assert.Equal(1, there.Nodes["P1"].ListedForSale["Kestrel"]);
            Assert.Equal(2, ShipAssetRules.WithdrawableCount(there.Nodes["P1"].ShipStock["Kestrel"],
                                                              there.Nodes["P1"].ListedForSale["Kestrel"]));
            Assert.Empty(there.Nodes["N1"].ShipStock);
            Assert.Null(back.Nodes.Find(n => n.Id == "N1").Hulls);
        }

        [Fact]
        public void AHullEventSurvivesTheReport()
        {
            var report = new NexusAgentReport
            {
                Session = "s",
                StoreEvents = new List<NexusStoreEvent>
                {
                    new NexusStoreEvent { Kind = NexusStoreEventKind.Hull, Node = "P1", Prefab = "Kestrel", Buy = true },
                    new NexusStoreEvent
                    {
                        Kind = NexusStoreEventKind.Hull, Node = "P1", Prefab = "Kestrel", Buy = false,
                        Parts = new Dictionary<string, long> { { "Ore:Iron", 1200 } },
                    },
                },
            };
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(report.Encode(), out back));
            Assert.Equal(2, back.StoreEvents.Count);
            Assert.True(back.StoreEvents[0].Buy);
            Assert.Equal("Kestrel", back.StoreEvents[0].Prefab);
            Assert.False(back.StoreEvents[1].Buy);
            Assert.Equal(1200, back.StoreEvents[1].Parts["Ore:Iron"]);
        }
    }
}
