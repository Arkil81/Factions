using System;
using System.Linq;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The two fields <see cref="DemandUrgency"/> reads that a poster does not always
    /// supply: when a demand first appeared, and what raised it.</summary>
    public sealed class DemandProvenanceTests
    {
        private static readonly DateTime Start = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Post_StampsFirstPostedUtc_OnAnItemDemandThatSuppliedNone()
        {
            DateTime now = Start;
            var demand = new DemandRegister(() => now);

            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore:Iron", NodeId = "N1",
                Quantity = 100, ExpiresUtc = now.AddMinutes(5),
            });

            DemandEntry stored = Assert.Single(demand.GetFor("F", "Item:Ore:Iron"));
            Assert.Equal(Start, stored.FirstPostedUtc);
        }

        [Fact]
        public void Refresh_KeepsTheOriginalAge_SoATtlBumpDoesNotResetIt()
        {
            DateTime now = Start;
            var demand = new DemandRegister(() => now);

            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore:Iron", NodeId = "N1",
                Quantity = 100, ExpiresUtc = now.AddMinutes(5),
            });

            now = Start.AddHours(9);
            Assert.False(demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore:Iron", NodeId = "N1",
                Quantity = 263731, ExpiresUtc = now.AddMinutes(5),
            }));

            DemandEntry stored = Assert.Single(demand.GetFor("F", "Item:Ore:Iron"));
            Assert.Equal(Start, stored.FirstPostedUtc);
            Assert.Equal(263731, stored.Quantity);
        }

        [Fact]
        public void Post_DoesNotOverwriteAnAgeTheCallerSupplied()
        {
            DateTime now = Start.AddHours(5);
            var demand = new DemandRegister(() => now);

            demand.Post(DemandEntry.ForShip("F", "Trade", "N1", now.AddMinutes(5), postedUtc: Start));

            DemandEntry stored = Assert.Single(demand.GetFor("F", "Ship:Trade"));
            Assert.Equal(Start, stored.FirstPostedUtc);
        }

        [Fact]
        public void ClocklessRegister_StillPostsAndReadsAsBrandNew()
        {
            // The parameterless ctor is what the tests and the sim harness build. It must keep
            // working, and an unstamped entry must read as new rather than as infinitely old.
            var demand = new DemandRegister();
            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore:Iron", NodeId = "N1",
                Quantity = 100, ExpiresUtc = Start.AddMinutes(5),
            });

            DemandEntry stored = Assert.Single(demand.GetFor("F", "Item:Ore:Iron"));
            Assert.Equal(default(DateTime), stored.FirstPostedUtc);
            Assert.Equal(0.0, DemandUrgency.Age01(stored.FirstPostedUtc, Start.AddDays(30), new FactionTuning()));
        }

        [Fact]
        public void Refresh_KeepsTheStrongerClass_WhenTwoPostersShareOneKey()
        {
            DateTime now = Start;
            var demand = new DemandRegister(() => now);

            // The live case: a ship BOM and a catalogue top-up both want the same component at the
            // same node, and Post is last-write-wins on everything except what PreserveOnRefresh
            // guards. Demoting the row to Catalog would drop the build's claim on the next refresh.
            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:Thrust", NodeId = "N007",
                Quantity = 31, ExpiresUtc = now.AddMinutes(5), Class = DemandClass.ShipBuild,
            });
            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:Thrust", NodeId = "N007",
                Quantity = 200, ExpiresUtc = now.AddMinutes(5), Class = DemandClass.Catalog,
            });

            DemandEntry stored = Assert.Single(demand.GetFor("F", "Item:Comp:Thrust"));
            Assert.Equal(DemandClass.ShipBuild, stored.Class);
            Assert.Equal(200, stored.Quantity);
        }

        [Fact]
        public void Refresh_PromotesToTheStrongerClass_WhenTheStrongerPosterArrivesSecond()
        {
            DateTime now = Start;
            var demand = new DemandRegister(() => now);

            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:Thrust", NodeId = "N007",
                Quantity = 200, ExpiresUtc = now.AddMinutes(5), Class = DemandClass.Catalog,
            });
            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:Thrust", NodeId = "N007",
                Quantity = 31, ExpiresUtc = now.AddMinutes(5), Class = DemandClass.ShipBuild,
            });

            Assert.Equal(DemandClass.ShipBuild, Assert.Single(demand.GetFor("F", "Item:Comp:Thrust")).Class);
        }

        [Fact]
        public void ShipyardBomDemand_IsClassedAsShipBuild_EndToEnd()
        {
            // Guards the stamp at the poster, not just the register's ability to carry it: the
            // whole ordering rests on ShipyardManager actually marking its BOM shortfall.
            var world = new WorldModel { UtcNow = Start, Tuning = new FactionTuning() };
            var node = new SpatialNode { Id = "N007", OwnerFactionTag = "F" };
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Graph.Nodes[node.Id] = node;

            var entry = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "Hauler", CreatedUtc = Start,
                ExpiresUtc = Start.AddHours(24),
            };
            entry.Bom["Comp:Thrust"] = 31;
            node.BuildQueue.Add(entry);

            var st = new FactionState { Tag = "F" };
            var log = new System.Collections.Generic.List<string>();
            new ShipyardManager().Tick(new[] { st }, world, world.Tuning, log);

            DemandEntry posted = world.Demand.GetFor("F", "Item:Comp:Thrust").Single();
            Assert.Equal(DemandClass.ShipBuild, posted.Class);
            Assert.Equal(Start, posted.FirstPostedUtc);
        }
    }
}
