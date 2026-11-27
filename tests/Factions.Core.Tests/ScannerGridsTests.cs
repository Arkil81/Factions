using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-533: grids the sim does not own, on the scanner and the map.</summary>
    public class ScannerGridsTests
    {
        private static ScannerGridCandidate Grid(long id, double xKm, long owner = 501, string tag = "ABC",
                                                 string ownerName = "Alice", double broadcastKm = 0)
        {
            return new ScannerGridCandidate
            {
                EntityId = id, PositionKm = new Vec3(xKm, 0, 0), Signature = 1.0, BroadcastRadiusKm = broadcastKm,
                OwnerIdentityId = owner, OwnerTag = tag, OwnerName = ownerName, OwnerOffline = true,
                Large = true, Blocks = 340, Name = "Hauler Mk2", WeaponDps = 5000,
            };
        }

        private static IList<ScannerContact> Resolve(double rangeKm, long exclude, params ScannerGridCandidate[] grids)
        {
            return PlayerScanner.Resolve(1, new Vec3(), rangeKm, null, null, TestSupport.Tuning(), null, null, null,
                                         grids, exclude);
        }

        [Fact]
        public void Resolve_FindsGridsInRange_AndLeavesOutTheOneBeingFlown()
        {
            IList<ScannerContact> contacts = Resolve(10, 7, Grid(5, 3), Grid(6, 500), Grid(7, 0.1));
            ScannerContact c = Assert.Single(contacts);
            Assert.Equal(ScannerContactKind.Grid, c.Kind);
            Assert.Equal("grid:5", c.Key);
            Assert.Equal(5, c.EntityId);
        }

        [Fact]
        public void Resolve_ABroadcastingGridIsFoundAtLeastAsFarAsItBroadcasts()
        {
            Assert.Empty(Resolve(10, 0, Grid(5, 40)));
            Assert.Single(Resolve(10, 0, Grid(5, 40, broadcastKm: 50)));
        }

        [Fact]
        public void Withhold_EachTierShowsOnlyWhatItHasEarned()
        {
            FactionTuning t = TestSupport.Tuning();
            ScannerContact Graded(IntelTier tier)
            {
                ScannerContact c = ScannerGrids.ToContact(Grid(5, 1), new SensorReading { Detected = true, SensorSees = true });
                c.Tier = tier;
                var list = new List<ScannerContact> { c };
                PlayerScanner.Withhold(list, null, t);
                return list[0];
            }

            ScannerContact contact = Graded(IntelTier.Contact);
            Assert.Equal("Grid", contact.Label);
            Assert.Null(contact.DisplayName);
            Assert.Null(contact.OwnerTag);
            Assert.Equal(0, contact.OwnerIdentityId);
            Assert.Equal(0, contact.GridBlocks);

            Assert.Equal("Large grid, 340 blocks", Graded(IntelTier.Class).Label);
            Assert.Null(Graded(IntelTier.Class).OwnerTag);

            ScannerContact identity = Graded(IntelTier.Identity);
            Assert.Equal("Alice [ABC] · Large grid, 340 blocks · owner offline", identity.Label);
            Assert.Equal("Hauler Mk2", identity.DisplayName);
            Assert.Equal("ABC", identity.OwnerTag);

            Assert.Equal("Alice [ABC] · Large grid, 340 blocks · armed · owner offline",
                         Graded(IntelTier.Armament).Label);
        }

        [Fact]
        public void Label_NamesNpcOwnersByTag_UnownedGrids_AndHeavyArmament()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal("[SPRT] · Small grid, 12 blocks · heavily armed",
                ScannerGrids.Label(true, true, true, false, 12, 77, "SPRT", null, false, t.IntelGridHeavyDps, t));
            Assert.Equal("Unowned · Large grid, 40 blocks · unarmed",
                ScannerGrids.Label(true, true, true, true, 40, 0, null, null, false, 0, t));
            Assert.Equal("Bob · Large grid, 40 blocks",
                ScannerGrids.Label(true, true, false, true, 40, 9, null, "Bob", false, 0, t));
        }

        [Fact]
        public void Grade_AFactionlessOwnersOwnGridIsFull_AndABroadcastingGridIsIdentified()
        {
            FactionTuning t = TestSupport.Tuning();
            var contacts = new List<ScannerContact>
            {
                ScannerGrids.ToContact(Grid(5, 9, owner: 501, tag: null), new SensorReading { Detected = true, SensorSees = true, MarginDb = 0.1 }),
                ScannerGrids.ToContact(Grid(6, 9, owner: 502, tag: null, broadcastKm: 20), new SensorReading { Detected = true, SensorSees = true, MarginDb = 0.1 }),
            };
            PlayerScanner.Grade(contacts, null, tag => false, t, 501);
            Assert.Equal(IntelTier.Full, contacts[0].Tier);
            Assert.True(contacts[1].Tier >= IntelTier.Identity);
        }

        [Fact]
        public void Wire_AGridContactCarriesItsNameAndMotion_AndAMapGridItsLabel()
        {
            var contact = new WireContact
            {
                Key = "grid:5", Kind = FactionsWire.ContactKindGrid, Label = "Large grid, 340 blocks",
                DisplayName = "Hauler Mk2", Moving = true, VelXKmPerMin = 1.5, EntityId = 5,
            };
            List<WireContact> present; List<string> removed; bool reset;
            Assert.True(FactionsWire.TryReadScannerContacts(
                FactionsWire.WriteScannerContacts(new List<WireContact> { contact }, null, false),
                out present, out removed, out reset));
            WireContact got = Assert.Single(present);
            Assert.Equal("Hauler Mk2", got.DisplayName);
            Assert.Equal(1.5, got.VelXKmPerMin);
            Assert.Equal(FactionsWire.ContactKindGrid, got.Kind);

            var fleets = new List<WireMapFleet>
            {
                new WireMapFleet { Key = "grid:5", IsGrid = true, Label = "Unowned · Small grid, 9 blocks", Name = "Wreck", EntityId = 5 },
                new WireMapFleet { Key = "fleet:after", Kind = 8, ConvoySize = 3 },
            };
            List<WireMapFleet> read; List<WireMapAlarm> alarms;
            Assert.True(FactionsWire.TryReadMapFleets(FactionsWire.WriteMapFleets(fleets, null), out read, out alarms));
            Assert.True(read[0].IsGrid);
            Assert.Equal("Wreck", read[0].Name);
            Assert.Equal("Unowned · Small grid, 9 blocks", read[0].Label);
            Assert.False(read[1].IsGrid);
            Assert.Equal(3, read[1].ConvoySize);
        }
    }
}
