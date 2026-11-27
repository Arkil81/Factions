using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class TransportedPilotLedgerTests
    {
        [Fact]
        public void PilotSentWithAShip_IsMatchedByEntity()
        {
            var ledger = new TransportedPilotLedger();
            ledger.Record(2510324540301277366, 144, 100.0);

            double age;
            bool byEntity;
            Assert.True(ledger.TryMatch(2510324540301277366, 144, 102.0, out age, out byEntity));
            Assert.True(byEntity);
            Assert.Equal(2.0, age, 6);
        }

        [Fact]
        public void EjectedUnderANewEntity_IsMatchedByIdentity()
        {
            var ledger = new TransportedPilotLedger();
            ledger.Record(11, 144, 100.0);

            double age;
            bool byEntity;
            Assert.True(ledger.TryMatch(99, 144, 101.0, out age, out byEntity));
            Assert.False(byEntity);
        }

        [Fact]
        public void SomeoneElse_IsNotMatched()
        {
            var ledger = new TransportedPilotLedger();
            ledger.Record(11, 144, 100.0);

            double age;
            bool byEntity;
            Assert.False(ledger.TryMatch(12, 145, 101.0, out age, out byEntity));
            // Identity 0 is "no identity", never a match on its own.
            ledger.Record(13, 0, 100.0);
            Assert.False(ledger.TryMatch(14, 0, 101.0, out age, out byEntity));
        }

        [Fact]
        public void PastTheWindow_IsNotMatchedAndIsForgotten()
        {
            var ledger = new TransportedPilotLedger();
            ledger.Record(11, 144, 100.0);

            double age;
            bool byEntity;
            Assert.True(ledger.TryMatch(11, 144, 100.0 + TransportedPilotLedger.WindowSeconds, out age, out byEntity));
            Assert.False(ledger.TryMatch(11, 144, 100.1 + TransportedPilotLedger.WindowSeconds, out age, out byEntity));
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void ASecondCrossing_ReplacesTheFirstRecord()
        {
            var ledger = new TransportedPilotLedger();
            ledger.Record(11, 144, 100.0);
            ledger.Record(22, 144, 150.0);
            Assert.Equal(1, ledger.Count);

            double age;
            bool byEntity;
            Assert.False(ledger.TryMatch(11, 0, 151.0, out age, out byEntity));
            Assert.True(ledger.TryMatch(22, 144, 151.0, out age, out byEntity));
            Assert.Equal(1.0, age, 6);
        }

        [Theory]
        // The leftover of a pilot whose player has left: closed with the switch on, observed with it off.
        [InlineData(true, true, false, LeftoverPilotAction.Close)]
        [InlineData(false, true, false, LeftoverPilotAction.SendObserved)]
        // Still on this server: their only character, never touched.
        [InlineData(true, true, true, LeftoverPilotAction.Send)]
        [InlineData(false, true, true, LeftoverPilotAction.Send)]
        // Not a recorded pilot: Nexus's own on-foot crossing, untouched either way.
        [InlineData(true, false, false, LeftoverPilotAction.Send)]
        [InlineData(false, false, false, LeftoverPilotAction.Send)]
        public void Decide_ClosesOnlyAnOfflineLeftoverWithTheSwitchOn(bool switchOn, bool matched, bool online,
                                                                     LeftoverPilotAction expected)
        {
            Assert.Equal(expected, TransportedPilotLedger.Decide(switchOn, matched, online));
        }
    }
}
