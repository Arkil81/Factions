using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The purpose of a build (fleet vs shelf) is part of its identity, not a payload
    /// field it carries. FAC-060 slice 2 retires the <c>StoreSmall</c>/<c>StoreLarge</c> role
    /// strings, and those strings were what kept a shelf build and a fleet build apart in three
    /// different keys. These tests fail if any of the three collapses them back together.
    /// <para>Each one is written around the characteristic failure: not "the flag round-trips", but
    /// "the two builds are still two". A merged pair does not throw — one build simply becomes the
    /// other, sized and reserved for a purpose that is not its own.</para></summary>
    public class BuildPurposeIdentityTests
    {
        private static DemandRegister Register(DateTime now)
        {
            return new DemandRegister(() => now);
        }

        private static DemandEntry Ship(string role, string node, DateTime expires, bool forSale)
        {
            return DemandEntry.ForShip("F", role, node, expires, destinedForSale: forSale);
        }

        [Fact]
        public void ShelfAndFleetDemandOfOneRoleAtOneNode_AreTwoRows_NotOneOverwritingTheOther()
        {
            DateTime now = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
            DemandRegister reg = Register(now);

            bool fleetIsNew = reg.Post(Ship("Trade", "N1", now.AddMinutes(5), forSale: false));
            bool shelfIsNew = reg.Post(Ship("Trade", "N1", now.AddMinutes(5), forSale: true));

            // The second post must be a NEW row, not a refresh of the first. A refresh here is the
            // whole defect: one row survives, carrying whichever purpose posted last.
            Assert.True(fleetIsNew);
            Assert.True(shelfIsNew);

            List<DemandEntry> ships = reg.SnapshotShipDemands("F");
            Assert.Equal(2, ships.Count);

            int forSale = 0, forFleet = 0;
            foreach (DemandEntry d in ships)
            {
                if (d.DestinedForSale) forSale++; else forFleet++;
            }
            Assert.Equal(1, forSale);
            Assert.Equal(1, forFleet);
        }

        [Fact]
        public void ConsumingTheShelfDemand_LeavesTheFleetDemandOfTheSameRoleStanding()
        {
            DateTime now = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
            DemandRegister reg = Register(now);
            reg.Post(Ship("Trade", "N1", now.AddMinutes(5), forSale: false));
            reg.Post(Ship("Trade", "N1", now.AddMinutes(5), forSale: true));

            int retired = reg.ConsumeAll("F", "Ship:Trade", "N1", destinedForSale: true);

            Assert.Equal(1, retired);
            List<DemandEntry> left = reg.SnapshotShipDemands("F");
            Assert.Single(left);
            Assert.False(left[0].DestinedForSale);
        }

        [Fact]
        public void TryGet_DoesNotHandTheShelfDemandToAFleetLookup()
        {
            DateTime now = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
            DemandRegister reg = Register(now);
            reg.Post(Ship("Trade", "N1", now.AddMinutes(5), forSale: true));

            DemandEntry found;
            Assert.False(reg.TryGet("F", "Ship:Trade", "N1", null, out found));
            Assert.True(reg.TryGet("F", "Ship:Trade", "N1", null, out found, destinedForSale: true));
            Assert.True(found.DestinedForSale);
        }

        [Fact]
        public void EachPurposeKeepsItsOwnAge_SoAShelfRowDoesNotInheritAnOlderFleetNeed()
        {
            DateTime now = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
            DateTime clock = now;
            var reg = new DemandRegister(() => clock);

            reg.Post(Ship("Trade", "N1", now.AddMinutes(5), forSale: false));

            // Half an hour of unmet fleet need later, the store asks for the same role at the same
            // node. Refreshing the fleet row must not age the shelf row into existence with it.
            clock = now.AddMinutes(30);
            reg.Post(Ship("Trade", "N1", clock.AddMinutes(5), forSale: false));
            reg.Post(Ship("Trade", "N1", clock.AddMinutes(5), forSale: true));

            DemandEntry fleet, shelf;
            Assert.True(reg.TryGet("F", "Ship:Trade", "N1", null, out fleet));
            Assert.True(reg.TryGet("F", "Ship:Trade", "N1", null, out shelf, destinedForSale: true));

            // The fleet row keeps the age it has genuinely carried; the shelf row starts now.
            // Merged, the shelf row would read as 30 minutes unmet on the tick it was first posted,
            // which is what DemandUrgency ranks on.
            Assert.Equal(now, fleet.FirstPostedUtc);
            Assert.Equal(now.AddMinutes(30), shelf.FirstPostedUtc);
        }

        [Fact]
        public void ABuildEntryDefaultsToTheFleet_SoNothingIsReservedWithoutBeingAskedFor()
        {
            var entry = new BuildQueueEntry { Id = "Trade:001", Role = "Trade" };
            Assert.False(entry.DestinedForSale);
        }
    }
}
