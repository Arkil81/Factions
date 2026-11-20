using System;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class StationBuyDemandTests
    {
        private static readonly DateTime Now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static DemandRegister WithItemDemand(string nodeId, long quantity, TimeSpan? ttl = null)
        {
            var demand = new DemandRegister();
            demand.Post(new DemandEntry
            {
                FactionTag = "ACME",
                Kind       = "Item:Comp:SteelPlate",
                NodeId     = nodeId,
                Quantity   = quantity,
                ExpiresUtc = Now.Add(ttl ?? TimeSpan.FromMinutes(5)),
            });
            return demand;
        }

        [Fact]
        public void NoDemand_DoesNotQualify()
        {
            long quantity;
            Assert.False(StationBuyDemand.TryGetBuyQuantity(
                new DemandRegister(), "ACME", "N1", "Comp:SteelPlate", Now, out quantity));
            Assert.Equal(0, quantity);
        }

        [Fact]
        public void NodeScopedDemandAtThisNode_Qualifies()
        {
            long quantity;
            Assert.True(StationBuyDemand.TryGetBuyQuantity(
                WithItemDemand("N1", 250), "ACME", "N1", "Comp:SteelPlate", Now, out quantity));
            Assert.Equal(250, quantity);
        }

        [Fact]
        public void NodeScopedDemandAtAnotherNode_DoesNotQualify()
        {
            // The need is owed at N2. N1 selling into it would deliver to the wrong place.
            long quantity;
            Assert.False(StationBuyDemand.TryGetBuyQuantity(
                WithItemDemand("N2", 250), "ACME", "N1", "Comp:SteelPlate", Now, out quantity));
            Assert.Equal(0, quantity);
        }

        [Fact]
        public void FactionScalarDemand_QualifiesAtAnyOfItsNodes()
        {
            DemandRegister demand = WithItemDemand(null, 1100);

            long atN1, atN2;
            Assert.True(StationBuyDemand.TryGetBuyQuantity(
                demand, "ACME", "N1", "Comp:SteelPlate", Now, out atN1));
            Assert.True(StationBuyDemand.TryGetBuyQuantity(
                demand, "ACME", "N2", "Comp:SteelPlate", Now, out atN2));
            Assert.Equal(1100, atN1);
            Assert.Equal(1100, atN2);
        }

        [Fact]
        public void NodeScopedQuantityWins_OverAFactionScalarOne()
        {
            var demand = new DemandRegister();
            demand.Post(new DemandEntry
            {
                FactionTag = "ACME", Kind = "Item:Comp:SteelPlate", NodeId = null,
                Quantity = 11000, ExpiresUtc = Now.AddMinutes(5),
            });
            demand.Post(new DemandEntry
            {
                FactionTag = "ACME", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                Quantity = 2828, ExpiresUtc = Now.AddMinutes(5),
            });

            long quantity;
            Assert.True(StationBuyDemand.TryGetBuyQuantity(
                demand, "ACME", "N1", "Comp:SteelPlate", Now, out quantity));
            Assert.Equal(2828, quantity);
        }

        [Fact]
        public void ExpiredDemand_DoesNotQualify()
        {
            // Prune runs on the manager tick; stores refresh far more often. Since this read now
            // decides whether the item lists at all, expiry has to be evaluated here.
            long quantity;
            Assert.False(StationBuyDemand.TryGetBuyQuantity(
                WithItemDemand("N1", 250, TimeSpan.FromMinutes(-1)),
                "ACME", "N1", "Comp:SteelPlate", Now, out quantity));
            Assert.Equal(0, quantity);
        }

        [Fact]
        public void AnotherFactionsDemand_DoesNotQualify()
        {
            long quantity;
            Assert.False(StationBuyDemand.TryGetBuyQuantity(
                WithItemDemand("N1", 250), "OTHER", "N1", "Comp:SteelPlate", Now, out quantity));
        }

        [Fact]
        public void ADifferentItemsDemand_DoesNotQualify()
        {
            long quantity;
            Assert.False(StationBuyDemand.TryGetBuyQuantity(
                WithItemDemand("N1", 250), "ACME", "N1", "Comp:InteriorPlate", Now, out quantity));
        }

        [Fact]
        public void ZeroQuantityDemand_DoesNotQualify()
        {
            long quantity;
            Assert.False(StationBuyDemand.TryGetBuyQuantity(
                WithItemDemand("N1", 0), "ACME", "N1", "Comp:SteelPlate", Now, out quantity));
        }
    }
}
