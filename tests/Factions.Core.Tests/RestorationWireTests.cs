using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Round trips of the restoration messages: the two ends compile the same codec, so a
    /// round trip is the check that they agree on the byte layout.</summary>
    public class RestorationWireTests
    {
        [Fact]
        public void Quote_request_round_trips()
        {
            long t, g;
            Assert.True(FactionsWire.TryReadRestoreQuoteRequest(FactionsWire.WriteRestoreQuoteRequest(11, 22), out t, out g));
            Assert.Equal(11, t);
            Assert.Equal(22, g);
        }

        [Fact]
        public void Quote_round_trips_with_its_panel_and_lines()
        {
            var sent = new WireRestoreQuote
            {
                TerminalEntityId = 1, GridEntityId = 2, Managed = true, Offer = WireRestoreOffer.Running,
                Message = "paused", Source = "matched:Laevateinn", Score = 0.912,
                Welds = 219, Places = 3, Blocked = 1, Extra = 4, Unreachable = 2,
                Credits = 1700, Seconds = 42, Placed = 5, Welded = 17, Skipped = 1, Total = 222, Paid = 900,
                PauseReason = "range",
                Lines = new[]
                {
                    new WireRestoreLine { Name = "SteelPlate", Need = 20, FromShip = 7, FromNode = 5, NodeCredits = 500, FromBank = 8, BankCredits = 1200 },
                    new WireRestoreLine { Name = "Computer", Need = 3, FromShip = 1, Short = 2 },
                },
            };
            WireRestoreQuote q;
            Assert.True(FactionsWire.TryReadRestoreQuote(FactionsWire.WriteRestoreQuote(sent), out q));
            Assert.True(q.Managed);
            Assert.Equal(WireRestoreOffer.Running, q.Offer);
            Assert.Equal("matched:Laevateinn", q.Source);
            Assert.Equal(0.912, q.Score, 6);
            Assert.Equal(219, q.Welds);
            Assert.Equal(3, q.Places);
            Assert.Equal(1, q.Blocked);
            Assert.Equal(4, q.Extra);
            Assert.Equal(2, q.Unreachable);
            Assert.Equal(1700, q.Credits);
            Assert.Equal(42, q.Seconds);
            Assert.Equal(5, q.Placed);
            Assert.Equal(17, q.Welded);
            Assert.Equal(1, q.Skipped);
            Assert.Equal(222, q.Total);
            Assert.Equal(900, q.Paid);
            Assert.Equal("range", q.PauseReason);
            Assert.Equal(2, q.Lines.Length);
            Assert.Equal(1200, q.Lines[0].BankCredits);
            Assert.Equal(2, q.Lines[1].Short);
        }

        [Fact]
        public void A_quote_with_an_unknown_offer_or_a_truncated_body_is_refused()
        {
            byte[] data = FactionsWire.WriteRestoreQuote(new WireRestoreQuote { Offer = WireRestoreOffer.Register, Message = "x" });
            WireRestoreQuote q;

            byte[] cut = new byte[data.Length - 3];
            System.Array.Copy(data, cut, cut.Length);
            Assert.False(FactionsWire.TryReadRestoreQuote(cut, out q));

            byte[] bad = (byte[])data.Clone();
            bad[6 + 16 + 1] = 99; // offer byte: after the header (two-byte kind, int version), two ids and the managed flag
            Assert.False(FactionsWire.TryReadRestoreQuote(bad, out q));
            Assert.True(FactionsWire.TryReadRestoreQuote(data, out q));
        }

        [Fact]
        public void Request_round_trips_and_refuses_an_unknown_action()
        {
            long t, g, max;
            WireRestoreAction action;
            Assert.True(FactionsWire.TryReadRestoreRequest(
                FactionsWire.WriteRestoreRequest(5, 6, WireRestoreAction.Register, 50000), out t, out g, out action, out max));
            Assert.Equal(WireRestoreAction.Register, action);
            Assert.Equal(50000, max);
            Assert.False(FactionsWire.TryReadRestoreRequest(
                FactionsWire.WriteRestoreRequest(5, 6, (WireRestoreAction)9, 0), out t, out g, out action, out max));
        }

        [Fact]
        public void Result_round_trips()
        {
            long g, balance;
            bool ok, notice;
            string message;
            Assert.True(FactionsWire.TryReadRestoreResult(
                FactionsWire.WriteRestoreResult(7, true, true, "paused", 123), out g, out ok, out notice, out message, out balance));
            Assert.Equal(7, g);
            Assert.True(ok);
            Assert.True(notice);
            Assert.Equal("paused", message);
            Assert.Equal(123, balance);
        }
    }
}
