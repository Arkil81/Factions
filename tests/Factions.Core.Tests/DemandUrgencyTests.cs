using System;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class DemandUrgencyTests
    {
        private static readonly DateTime Now = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private static FactionTuning Tuning()
        {
            // Explicit rather than defaulted: every assertion below is about a relationship between
            // terms, and a later default change must not quietly turn one of them off.
            return new FactionTuning
            {
                UrgencyStarvationExponent = 1.0,
                UrgencyAgeHorizonHours    = 6.0,
                UrgencyAgeWeight          = 1.0,
                UrgencyHistoryWeight      = 0.5,
                UrgencyHistorySaturation  = 500.0,
                // Indexed by DemandClass's own value: Unclassified, Catalog, StoreStock,
                // RecipeInput, Fuel, Install, ShipBuild. Seven entries, not six -- a short array
                // gives every class past its end the default weight instead of its own.
                UrgencyClassWeights       = new double[] { 1.0, 1.0, 1.5, 2.0, 2.5, 3.0, 4.0 },
                FuelUrgencyPerParkedHull  = 0.25,
                FuelUrgencyHullCap        = 4.0,
            };
        }

        private static DemandEntry Demand(long quantity, DemandClass cls = DemandClass.Catalog,
            DateTime firstPosted = default(DateTime))
        {
            return new DemandEntry
            {
                FactionTag     = "F",
                Kind           = "Item:Ore:Iron",
                NodeId         = "N1",
                Quantity       = quantity,
                Class          = cls,
                FirstPostedUtc = firstPosted,
            };
        }

        [Fact]
        public void MetDemand_ScoresZero_HoweverOldAndHoweverStronglyClassed()
        {
            FactionTuning t = Tuning();
            // Every amplifier at maximum, and the node holds the whole want.
            DemandEntry met = Demand(100, DemandClass.ShipBuild, Now.AddDays(-30));

            Assert.Equal(0.0, DemandUrgency.Score(met, 100, 100000, Now, t));
            Assert.Equal(0.0, DemandUrgency.Score(met, 250, 100000, Now, t));
        }

        [Fact]
        public void Starvation_EmptierNodeOutranksPartlyStockedOne_AllElseEqual()
        {
            FactionTuning t = Tuning();
            double empty  = DemandUrgency.Score(Demand(100), 0,  0, Now, t);
            double half   = DemandUrgency.Score(Demand(100), 50, 0, Now, t);
            double nearly = DemandUrgency.Score(Demand(100), 99, 0, Now, t);

            Assert.True(empty > half, "empty " + empty + " should outrank half-stocked " + half);
            Assert.True(half > nearly, "half " + half + " should outrank nearly-met " + nearly);
            Assert.True(nearly > 0);
        }

        [Fact]
        public void ClassOutranksAtEqualStarvation_ShipBuildOverCatalog()
        {
            FactionTuning t = Tuning();
            double build   = DemandUrgency.Score(Demand(100, DemandClass.ShipBuild), 0, 0, Now, t);
            double catalog = DemandUrgency.Score(Demand(100, DemandClass.Catalog),   0, 0, Now, t);

            Assert.True(build > catalog, "ship build " + build + " should outrank catalog " + catalog);
        }

        [Fact]
        public void UnclassifiedRanksAsCatalog_NotAsZero()
        {
            FactionTuning t = Tuning();
            double unclassified = DemandUrgency.Score(Demand(100, DemandClass.Unclassified), 0, 0, Now, t);
            double catalog      = DemandUrgency.Score(Demand(100, DemandClass.Catalog),      0, 0, Now, t);

            Assert.True(unclassified > 0);
            Assert.Equal(catalog, unclassified);
        }

        [Fact]
        public void Age_RaisesTheScore_AndClampsAtTheHorizon()
        {
            FactionTuning t = Tuning();
            double fresh    = DemandUrgency.Score(Demand(100, DemandClass.Catalog, Now), 0, 0, Now, t);
            double halfAged = DemandUrgency.Score(Demand(100, DemandClass.Catalog, Now.AddHours(-3)), 0, 0, Now, t);
            double aged     = DemandUrgency.Score(Demand(100, DemandClass.Catalog, Now.AddHours(-6)), 0, 0, Now, t);
            double ancient  = DemandUrgency.Score(Demand(100, DemandClass.Catalog, Now.AddHours(-600)), 0, 0, Now, t);

            Assert.True(halfAged > fresh);
            Assert.True(aged > halfAged);
            // Clamped: past the horizon the term stops growing, so age can never swamp class or
            // starvation just by the demand having existed for a long time.
            Assert.Equal(aged, ancient);
        }

        [Fact]
        public void UnsetFirstPostedUtc_ReadsAsBrandNew_NotAsFullyAged()
        {
            FactionTuning t = Tuning();
            double unset = DemandUrgency.Score(Demand(100, DemandClass.Catalog, default(DateTime)), 0, 0, Now, t);
            double fresh = DemandUrgency.Score(Demand(100, DemandClass.Catalog, Now), 0, 0, Now, t);
            double aged  = DemandUrgency.Score(Demand(100, DemandClass.Catalog, Now.AddHours(-6)), 0, 0, Now, t);

            Assert.Equal(fresh, unset);
            Assert.True(aged > unset);
        }

        [Fact]
        public void History_AmplifiesButSaturates_SoOneBusyNodeCannotSwampTheOtherTerms()
        {
            FactionTuning t = Tuning();
            double quiet   = DemandUrgency.Score(Demand(100), 0, 0,        Now, t);
            double hungry  = DemandUrgency.Score(Demand(100), 0, 500,      Now, t);
            double vast    = DemandUrgency.Score(Demand(100), 0, 50000000, Now, t);

            Assert.True(hungry > quiet);
            Assert.True(vast > hungry);
            // The whole history term is bounded by (1 + weight), so an unbounded EMA cannot
            // outweigh the class ladder: a starving ship build still beats a well-fed catalogue want.
            Assert.True(vast <= quiet * (1.0 + t.UrgencyHistoryWeight) + 1e-9);
            double starvingBuild = DemandUrgency.Score(Demand(100, DemandClass.ShipBuild), 0, 0, Now, t);
            Assert.True(starvingBuild > vast);
        }

        [Fact]
        public void BurningIronBeatsAToppedUpStoreWant_TheLiveOrderingThisExistsFor()
        {
            FactionTuning t = Tuning();
            // ISBT/N007, 2026-08-31: a ship build wanted 31 Comp:Thrust with none on hand while
            // store and catalogue demands for well-stocked items sat ahead of it in post order.
            DemandEntry burning = Demand(263731, DemandClass.ShipBuild, Now.AddHours(-9));
            DemandEntry comfortable = Demand(200, DemandClass.StoreStock, Now.AddMinutes(-2));

            double burningScore = DemandUrgency.Score(burning, 0, 0, Now, t);
            double comfortableScore = DemandUrgency.Score(comfortable, 180, 0, Now, t);

            Assert.True(burningScore > comfortableScore,
                "burning " + burningScore + " must outrank comfortable " + comfortableScore);
        }

        [Fact]
        public void ZeroQuantityDemand_ScoresZero_SoShipDemandsNeverEnterTheItemOrdering()
        {
            FactionTuning t = Tuning();
            DemandEntry ship = new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", Quantity = 0,
                Class = DemandClass.ShipBuild, FirstPostedUtc = Now.AddDays(-1),
            };
            Assert.Equal(0.0, DemandUrgency.Score(ship, 0, 1000, Now, t));
        }

        [Fact]
        public void StarvationExponent_SharpensRatherThanReorders()
        {
            FactionTuning flat = Tuning();
            FactionTuning sharp = Tuning();
            sharp.UrgencyStarvationExponent = 3.0;

            double flatNearly  = DemandUrgency.Score(Demand(100), 90, 0, Now, flat);
            double sharpNearly = DemandUrgency.Score(Demand(100), 90, 0, Now, sharp);
            double flatEmpty   = DemandUrgency.Score(Demand(100), 0,  0, Now, flat);
            double sharpEmpty  = DemandUrgency.Score(Demand(100), 0,  0, Now, sharp);

            // A nearly-met demand is pushed down; a fully empty one is unchanged (1^k == 1).
            Assert.True(sharpNearly < flatNearly);
            Assert.Equal(flatEmpty, sharpEmpty);
        }

        [Fact]
        public void PartlyCoveredRow_ScoresZeroWhenTheQuantityWasNetted_AndNonZeroWhenItIsGross()
        {
            // FAC-205 defect 1, pinned here because it cannot be measured from a live window: the
            // two sizings produce different POPULATIONS of `have >= quantity`, so no payload
            // comparison can separate them (world-builder-3 retracted exactly that test as circular).
            //
            // One physical situation -- the node needs 1000 and holds 900 -- expressed both ways.
            FactionTuning t = Tuning();

            DemandEntry gross  = Demand(1000, DemandClass.RecipeInput);
            DemandEntry netted = Demand(1000 - 900, DemandClass.RecipeInput);   // the shortfall

            double grossScore  = DemandUrgency.Score(gross,  900, 0, Now, t);
            double nettedScore = DemandUrgency.Score(netted, 900, 0, Now, t);

            // Gross: one tenth of the want is missing, and the row ranks accordingly.
            Assert.Equal(0.1, DemandUrgency.Starvation(gross.Quantity, 900, t), 6);
            Assert.True(grossScore > 0);

            // Netted: haveAtNode >= quantity, so Starvation short-circuits to 0 and Score returns 0 --
            // which every caller reads as "no claim", not as "nearly met". The row leaves the
            // ranking entirely while still being 100 units short.
            Assert.Equal(0.0, nettedScore);
        }

        [Fact]
        public void NettedSizing_DropsARowFromRankingWheneverTheNodeHoldsHalfOfWhatItNeeds()
        {
            // The exact boundary, because it says how wide the defect was rather than that it
            // existed. Netted, `have >= quantity` reduces to 2 x held >= requirement -- so every row
            // at a node holding half or more of its requirement scored zero.
            FactionTuning t = Tuning();

            // Requirement 1000. Just under half held: still ranks. Just over: silent.
            Assert.True(DemandUrgency.Score(Demand(1000 - 499), 499, 0, Now, t) > 0);
            Assert.Equal(0.0, DemandUrgency.Score(Demand(1000 - 501), 501, 0, Now, t));

            // Gross, the same two nodes both rank, and the better-stocked one ranks lower.
            double half  = DemandUrgency.Score(Demand(1000), 499, 0, Now, t);
            double most  = DemandUrgency.Score(Demand(1000), 501, 0, Now, t);
            Assert.True(half > most && most > 0);
        }
    }
}
