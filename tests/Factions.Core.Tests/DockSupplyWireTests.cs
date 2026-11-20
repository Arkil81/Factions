using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The refuel and rearm messages. Additive kinds behind their own feature bit, so they
    /// ship without a ProtocolVersion bump — which makes round-tripping the only check that the two
    /// sides agree on their shape.</summary>
    public class DockSupplyWireTests
    {
        [Fact]
        public void DockRefuelRequest_RoundTrips()
        {
            byte[] data = FactionsWire.WriteDockRefuelRequest(11, 22, WireDockGas.Oxygen, true, 12345);

            long terminal, grid, max;
            WireDockGas gas;
            bool commit;
            Assert.True(FactionsWire.TryReadDockRefuelRequest(data, out terminal, out grid, out gas, out commit, out max));
            Assert.Equal(11L, terminal);
            Assert.Equal(22L, grid);
            Assert.Equal(WireDockGas.Oxygen, gas);
            Assert.True(commit);
            Assert.Equal(12345L, max);
        }

        [Fact]
        public void DockRefuelRequest_UnknownGas_IsRejected()
        {
            byte[] data = FactionsWire.WriteDockRefuelRequest(11, 22, WireDockGas.Hydrogen, false, 0);
            // Header is the kind byte and an int protocol version, then two longs; the gas byte follows.
            data[6 + 16] = 9;

            long terminal, grid, max;
            WireDockGas gas;
            bool commit;
            Assert.False(FactionsWire.TryReadDockRefuelRequest(data, out terminal, out grid, out gas, out commit, out max));
        }

        [Fact]
        public void DockRefuelResult_RoundTrips()
        {
            byte[] data = FactionsWire.WriteDockRefuelResult(WireDockGas.Hydrogen, true, true, 40000, 200000, "Fill 3 tanks?");

            WireDockGas gas;
            bool ok, quote;
            long litres, credits;
            string message;
            Assert.True(FactionsWire.TryReadDockRefuelResult(data, out gas, out ok, out quote, out litres, out credits, out message));
            Assert.Equal(WireDockGas.Hydrogen, gas);
            Assert.True(ok);
            Assert.True(quote);
            Assert.Equal(40000L, litres);
            Assert.Equal(200000L, credits);
            Assert.Equal("Fill 3 tanks?", message);
        }

        [Fact]
        public void RearmSurvey_RoundTrips_WithNestedOptions()
        {
            var mags = new[]
            {
                new WireRearmMagazine { Key = "NATO_25x184mm", Name = "Gatling Ammo Box", UnitPrice = 30, Available = 400, UnitLitres = 16 },
                new WireRearmMagazine { Key = "Missile200mm", Name = "Rocket", UnitPrice = 210, Available = 12, UnitLitres = 60 },
            };
            var groups = new[]
            {
                new WireRearmGroup
                {
                    GroupId = "MyObjectBuilder_LargeGatlingTurret/", Name = "Gatling Turret", Blocks = 4,
                    CurrentKey = "NATO_25x184mm",
                    Options = new[] { new WireRearmOption { Key = "NATO_25x184mm", Fits = 32 } },
                },
                new WireRearmGroup
                {
                    GroupId = "MyObjectBuilder_SmallMissileLauncher/LargeMissileLauncher", Name = "Rocket Launcher",
                    Blocks = 1, CurrentKey = "", Options = new WireRearmOption[0],
                },
            };
            byte[] data = FactionsWire.WriteRearmSurvey(5, 6, true, "", 99999, 1234.5, mags, groups);

            long terminal, grid, balance;
            bool ok;
            string message;
            double cargo;
            WireRearmMagazine[] readMags;
            WireRearmGroup[] readGroups;
            Assert.True(FactionsWire.TryReadRearmSurvey(data, out terminal, out grid, out ok, out message,
                out balance, out cargo, out readMags, out readGroups));
            Assert.Equal(5L, terminal);
            Assert.Equal(6L, grid);
            Assert.True(ok);
            Assert.Equal(99999L, balance);
            Assert.Equal(1234.5, cargo);
            Assert.Equal(2, readMags.Length);
            Assert.Equal("Rocket", readMags[1].Name);
            Assert.Equal(60.0, readMags[1].UnitLitres);
            Assert.Equal(12L, readMags[1].Available);
            Assert.Equal(2, readGroups.Length);
            Assert.Equal(4, readGroups[0].Blocks);
            Assert.Equal(32L, readGroups[0].Options[0].Fits);
            Assert.Empty(readGroups[1].Options);
            Assert.Equal("", readGroups[1].CurrentKey);
        }

        [Fact]
        public void RearmRequest_RoundTrips()
        {
            byte[] data = FactionsWire.WriteRearmRequest(5, 6, 5000,
                new[] { new WireRearmPick { GroupId = "g1", Key = "NATO_25x184mm" } },
                new[] { new WireRearmReserve { Key = "NATO_25x184mm", Count = 50 } });

            long terminal, grid, max;
            WireRearmPick[] picks;
            WireRearmReserve[] reserves;
            Assert.True(FactionsWire.TryReadRearmRequest(data, out terminal, out grid, out max, out picks, out reserves));
            Assert.Equal(5000L, max);
            Assert.Equal("g1", picks[0].GroupId);
            Assert.Equal(50L, reserves[0].Count);
        }

        [Fact]
        public void RearmRequest_OverlongKey_IsRejected()
        {
            byte[] data = FactionsWire.WriteRearmRequest(5, 6, 5000,
                new[] { new WireRearmPick { GroupId = "g1", Key = new string('x', 200) } }, null);

            long terminal, grid, max;
            WireRearmPick[] picks;
            WireRearmReserve[] reserves;
            Assert.False(FactionsWire.TryReadRearmRequest(data, out terminal, out grid, out max, out picks, out reserves));
        }

        [Fact]
        public void RearmResult_RoundTrips()
        {
            byte[] data = FactionsWire.WriteRearmResult(false, "No guns.");
            bool ok;
            string message;
            Assert.True(FactionsWire.TryReadRearmResult(data, out ok, out message));
            Assert.False(ok);
            Assert.Equal("No guns.", message);
        }

        [Fact]
        public void RearmResult_ReceiptRoundTrips()
        {
            var lines = new[]
            {
                new WireReceiptLine { Key = "NATO_25x184mm", Name = "Gatling Ammo Box", Loaded = 12, Cargo = 0, UnitPrice = 92683, Credits = 1112196 },
                new WireReceiptLine { Key = "Missile200mm", Name = "Rocket", Loaded = 28, Cargo = 1, UnitPrice = 31053, Credits = 900537 },
            };
            byte[] data = FactionsWire.WriteRearmResult(true, "Loaded.", 5000, lines);

            bool ok;
            string message;
            long balance;
            WireReceiptLine[] read;
            Assert.True(FactionsWire.TryReadRearmResult(data, out ok, out message, out balance, out read));
            Assert.Equal(5000L, balance);
            Assert.Equal(2, read.Length);
            Assert.Equal(28L, read[1].Loaded);
            Assert.Equal(1L, read[1].Cargo);
            Assert.Equal("Rocket", read[1].Name);
            Assert.Equal(900537L, read[1].Credits);
        }

        [Fact]
        public void RearmResult_FromAServerWithoutReceipts_ReadsWithAnEmptyReceipt()
        {
            // The first artifact to carry this kind ended the payload at the message. Its shape is
            // the current one minus the tail: an 8-byte balance and a 4-byte zero count.
            byte[] current = FactionsWire.WriteRearmResult(true, "Loaded.");
            byte[] legacy = new byte[current.Length - 12];
            System.Array.Copy(current, legacy, legacy.Length);

            bool ok;
            string message;
            long balance;
            WireReceiptLine[] read;
            Assert.True(FactionsWire.TryReadRearmResult(legacy, out ok, out message, out balance, out read));
            Assert.Equal("Loaded.", message);
            Assert.Empty(read);
        }

        [Fact]
        public void DockRefuelResult_ReceiptRoundTrips()
        {
            byte[] data = FactionsWire.WriteDockRefuelResult(WireDockGas.Hydrogen, true, false, 40000, 200, "Filled.",
                777, new[] { new WireReceiptLine { Key = "Gas:Hydrogen", Name = "Hydrogen", Loaded = 40000, UnitPrice = 5, Credits = 200 } });

            WireDockGas gas;
            bool ok, quote;
            long litres, credits, balance;
            string message;
            WireReceiptLine[] read;
            Assert.True(FactionsWire.TryReadDockRefuelResult(data, out gas, out ok, out quote, out litres, out credits,
                out message, out balance, out read));
            Assert.Equal(777L, balance);
            Assert.Single(read);
            Assert.Equal(40000L, read[0].Loaded);
        }

        [Fact]
        public void DockSupply_KindsAreNotReadAsEachOther()
        {
            byte[] survey = FactionsWire.WriteRearmSurveyRequest(1, 2);
            bool ok;
            string message;
            Assert.False(FactionsWire.TryReadRearmResult(survey, out ok, out message));
        }
    }
}
