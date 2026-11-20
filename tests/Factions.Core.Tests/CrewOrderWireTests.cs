using System;
using System.Linq;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The crew-order toolbar ids and wire messages (docs/ground-operations.md). A toolbar
    /// slot saves the action id, so the bytes and ids are pinned here.</summary>
    public class CrewOrderWireTests
    {
        [Fact]
        public void ActionIdsAreStableAndDistinct()
        {
            Assert.Equal(1, (int)WireCrewOrder.Mount);
            Assert.Equal(2, (int)WireCrewOrder.Dismount);
            Assert.Equal(3, (int)WireCrewOrder.Ease);
            Assert.Equal(4, (int)WireCrewOrder.Auto);
            Assert.Equal("FactionsCrewMount", CrewActions.Id(WireCrewOrder.Mount));
            Assert.Equal("FactionsCrewDismount", CrewActions.Id(WireCrewOrder.Dismount));
            Assert.Equal(CrewActions.All.Length, CrewActions.All.Select(CrewActions.Id).Distinct().Count());
            foreach (WireCrewOrder o in CrewActions.All)
                Assert.StartsWith("Crew: ", CrewActions.Name(o), StringComparison.Ordinal);
            Assert.False(CrewActions.IsKnown(0));
            Assert.False(CrewActions.IsKnown(5));
        }

        [Fact]
        public void EveryWireOrderNamesTheSameCoreOrder()
        {
            Assert.Equal(CrewOrders.Word(CrewOrder.Mount), WireCrewOrder.Mount.ToString().ToLowerInvariant());
            Assert.Equal(CrewOrders.Word(CrewOrder.Dismount), WireCrewOrder.Dismount.ToString().ToLowerInvariant());
            Assert.Equal(CrewOrders.Word(CrewOrder.Ease), WireCrewOrder.Ease.ToString().ToLowerInvariant());
            Assert.Equal(CrewOrders.Word(CrewOrder.Auto), WireCrewOrder.Auto.ToString().ToLowerInvariant());
        }

        [Fact]
        public void CrewOrderRequest_RoundTrips_AndRefusesAnUnknownOrder()
        {
            foreach (WireCrewOrder order in CrewActions.All)
            {
                long seat;
                WireCrewOrder back;
                Assert.True(FactionsWire.TryReadCrewOrderRequest(
                    FactionsWire.WriteCrewOrderRequest(123456789012L, order), out seat, out back));
                Assert.Equal(123456789012L, seat);
                Assert.Equal(order, back);
            }
            byte[] data = FactionsWire.WriteCrewOrderRequest(1, WireCrewOrder.Mount);
            data[6 + 8] = 99;                                   // header, seat, then the order byte
            long s;
            WireCrewOrder o;
            Assert.False(FactionsWire.TryReadCrewOrderRequest(data, out s, out o));
            Assert.False(FactionsWire.TryReadCrewOrderRequest(new byte[] { 1, 2 }, out s, out o));
            Assert.False(FactionsWire.TryReadCrewOrderRequest(
                FactionsWire.WriteCrewOrderResult(true, WireCrewOrder.Mount, "x"), out s, out o));
        }

        [Fact]
        public void CrewOrderResult_RoundTrips()
        {
            bool ok;
            WireCrewOrder order;
            string message;
            Assert.True(FactionsWire.TryReadCrewOrderResult(
                FactionsWire.WriteCrewOrderResult(true, WireCrewOrder.Dismount, "Crew of APC: dismount"), out ok, out order, out message));
            Assert.True(ok);
            Assert.Equal(WireCrewOrder.Dismount, order);
            Assert.Equal("Crew of APC: dismount", message);
            Assert.False(FactionsWire.TryReadCrewOrderResult(
                FactionsWire.WriteCrewOrderRequest(1, WireCrewOrder.Mount), out ok, out order, out message));
        }
    }
}
