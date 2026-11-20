using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The material half of a capability install's gate, as the dashboard and <c>station info</c>
    /// report it. These pin that it sums the same two pools <see cref="DevelopStationValidator"/>
    /// gates on (staged + routable), that a BOM-backed capability is reported per component while a
    /// flat-cost one is reported per <see cref="ItemKind"/>, and that the blocking rows sort to the
    /// top — the whole point of the readout is answering "what, if anything, is holding this up".
    /// </summary>
    public class InstallMaterialsTests
    {
        private const string Plate = "Comp:SteelPlate";
        private const string Grid  = "Comp:MetalGrid";
        private const string Motor = "Comp:Motor";

        private static FactionTuning BomTuning()
        {
            var t = new FactionTuning();
            t.InstallBoms[NodeCapabilityKind.Shipyard] = new Dictionary<string, long>
            {
                { Plate, 500 }, { Grid, 140 }, { Motor, 20 },
            };
            return t;
        }

        private static InstallMaterialRow Row(List<InstallMaterialRow> rows, string key)
        {
            foreach (InstallMaterialRow r in rows)
                if (r.Key == key) return r;
            Assert.True(false, "no row for " + key);
            return default(InstallMaterialRow);
        }

        [Fact]
        public void BomCapability_ReportsEveryComponentWithStagedAndRoutableSplit()
        {
            var w = new FakeWorld();
            FactionTuning t = BomTuning();
            w.ReservedByKey[Plate] = 340;
            w.InventoryByKey[Plate] = 80;
            w.ReservedByKey[Grid] = 12;
            // Motor: nothing anywhere.

            List<InstallMaterialRow> rows = InstallMaterials.For(w, "F", NodeCapabilityKind.Shipyard, t);

            Assert.Equal(3, rows.Count);
            InstallMaterialRow plate = Row(rows, Plate);
            Assert.Equal(500, plate.Need);
            Assert.Equal(340, plate.Reserved);
            Assert.Equal(80, plate.Routable);
            // Both pools count -- reserved alone would report 160 short on a key that is only 80 short.
            Assert.Equal(420, plate.Have);
            Assert.Equal(80, plate.Shortfall);

            Assert.Equal(128, Row(rows, Grid).Shortfall);
            Assert.Equal(20, Row(rows, Motor).Shortfall);
        }

        [Fact]
        public void RoutableStockAloneClosesTheGate()
        {
            var w = new FakeWorld();
            FactionTuning t = BomTuning();
            w.InventoryByKey[Plate] = 500;
            w.InventoryByKey[Grid] = 140;
            w.InventoryByKey[Motor] = 20;

            List<InstallMaterialRow> rows = InstallMaterials.For(w, "F", NodeCapabilityKind.Shipyard, t);

            foreach (InstallMaterialRow r in rows) Assert.Equal(0, r.Shortfall);
            Assert.Equal(1.0, InstallMaterials.Progress(rows), 6);
        }

        [Fact]
        public void BlockingRowsSortAboveSatisfiedOnes_LargestShortfallFirst()
        {
            var w = new FakeWorld();
            FactionTuning t = BomTuning();
            w.InventoryByKey[Plate] = 500;   // covered
            w.InventoryByKey[Grid] = 12;     // short 128
            w.InventoryByKey[Motor] = 5;     // short 15

            List<InstallMaterialRow> rows = InstallMaterials.For(w, "F", NodeCapabilityKind.Shipyard, t);

            Assert.Equal(Grid, rows[0].Key);
            Assert.Equal(Motor, rows[1].Key);
            Assert.Equal(Plate, rows[2].Key);
        }

        [Fact]
        public void Progress_IsWeightedByNeed_NotAveragedOverRows()
        {
            var w = new FakeWorld();
            FactionTuning t = BomTuning();
            w.InventoryByKey[Plate] = 250;   // half of the 500-unit key
            w.InventoryByKey[Grid] = 140;    // the two small keys are fully covered
            w.InventoryByKey[Motor] = 20;

            double p = InstallMaterials.Progress(InstallMaterials.For(w, "F", NodeCapabilityKind.Shipyard, t));

            // Row-averaged would read 83%; need-weighted is (250+140+20)/660.
            Assert.Equal(410.0 / 660.0, p, 6);
        }

        [Fact]
        public void Progress_NeverExceedsOne_WhenAKeyIsOverstocked()
        {
            var w = new FakeWorld();
            FactionTuning t = BomTuning();
            w.InventoryByKey[Plate] = 50_000;
            w.InventoryByKey[Grid] = 140;
            w.InventoryByKey[Motor] = 20;

            Assert.Equal(1.0, InstallMaterials.Progress(InstallMaterials.For(w, "F", NodeCapabilityKind.Shipyard, t)), 6);
        }

        [Fact]
        public void FlatCostCapability_ReportsItemKindBucketsMarkedByKind()
        {
            var w = new FakeWorld();
            var t = new FactionTuning();   // no InstallBoms registered at all
            w.Inventory = 0;
            w.ReservedByKind[ItemKind.ProcessedGood] = 3;

            // Shipyard is charged Ore + ProcessedGood by the flat table, and only kinds it is
            // actually charged for get a row -- a zero-cost kind is not a satisfied row, it is absent.
            List<InstallMaterialRow> rows = InstallMaterials.For(w, "F", NodeCapabilityKind.Shipyard, t);

            Assert.Equal(2, rows.Count);
            foreach (InstallMaterialRow r in rows)
            {
                Assert.True(r.ByKind);
                Assert.False(r.Key.Contains(":"));   // an ItemKind name, not an inventory key
                Assert.Equal(t.InstallCostFor(NodeCapabilityKind.Shipyard, Kind(r.Key)), r.Need);
            }
            Assert.Equal(3, Row(rows, "ProcessedGood").Reserved);
            Assert.Equal(0, Row(rows, "ProcessedGood").Routable);
        }

        [Fact]
        public void AgreesWithTheDevelopGate_ForABomCapability()
        {
            var w = new FakeWorld();
            FactionTuning t = BomTuning();
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.StableStationId = 42;
            n.Capabilities[NodeCapabilityKind.Manufactory] = 1;   // Shipyard's prerequisite
            w.Treasury = 10_000_000;
            w.InventoryByKey[Plate] = 500;
            w.InventoryByKey[Grid] = 140;
            // Motor deliberately absent.

            List<InstallMaterialRow> rows = InstallMaterials.For(w, "F", NodeCapabilityKind.Shipyard, t);
            Assert.Contains(rows, r => r.Key == Motor && r.Shortfall > 0);

            NodeCapabilityKind? prereq; long cost; string missingKey; long have, need;
            DevelopStationResult result = DevelopStationValidator.Validate(w, t, "F", "N1",
                NodeCapabilityKind.Shipyard, out prereq, out cost, out missingKey, out have, out need);
            Assert.Equal(DevelopStationResult.InsufficientItemStock, result);
            Assert.Equal(Motor, missingKey);

            // ...and once that one row is covered, so is the gate.
            w.InventoryByKey[Motor] = 20;
            Assert.All(InstallMaterials.For(w, "F", NodeCapabilityKind.Shipyard, t), r => Assert.Equal(0, r.Shortfall));
            Assert.Equal(DevelopStationResult.Ok, DevelopStationValidator.Validate(w, t, "F", "N1",
                NodeCapabilityKind.Shipyard, out prereq, out cost, out missingKey, out have, out need));
        }

        [Fact]
        public void SnapshotSignals_CarriesMaterialsAndFlagsTheBlockedInstall()
        {
            var w = new FakeWorld();
            FactionTuning t = BomTuning();
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.StableStationId = 42;
            n.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            TestSupport.WithOre(n);
            w.Treasury = 10_000_000;
            w.InvestmentRegister.Post(new InvestmentRequest
            {
                FactionTag = "F",
                Cap        = NodeCapabilityKind.Shipyard,
                Urgency    = 0.8,
                ExpiresUtc = w.UtcNow.AddHours(1),
            });
            w.InventoryByKey[Plate] = 500;
            w.InventoryByKey[Grid] = 140;

            List<InvestmentSignal> signals = InvestmentTargeting.SnapshotSignals(w, "F", t);

            InvestmentSignal sig = Assert.Single(signals);
            Assert.True(sig.Affordable);              // treasury is fine...
            Assert.False(sig.MaterialsMet);           // ...and the materials are what block it
            Assert.NotNull(sig.Materials);
            Assert.Contains(sig.Materials, r => r.Key == Motor && r.Shortfall == 20);

            w.InventoryByKey[Motor] = 20;
            Assert.True(InvestmentTargeting.SnapshotSignals(w, "F", t)[0].MaterialsMet);
        }

        private static ItemKind Kind(string name)
        {
            return (ItemKind)System.Enum.Parse(typeof(ItemKind), name);
        }
    }
}
