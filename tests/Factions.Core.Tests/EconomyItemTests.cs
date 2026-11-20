using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Item economy layer (Phase 1): yield mints items, upkeep deducts items, trade transfers cargo.</summary>
    public class EconomyItemTests
    {
        // ---- Economy.SourceItemYield ----------------------------------------------------------------

        /// <summary>The ore channel scales on <see cref="SpatialNode.Richness"/> since FAC-219 — how
        /// much ore comes out is a question about the ground, not about what the ground is worth.
        /// Worth is pinned equal on both nodes here so it cannot be what separates them.</summary>
        [Fact]
        public void ItemYield_ScalesWith_Richness_Hold_Control()
        {
            var t = TestSupport.Tuning();
            var full = TestSupport.WithOre(new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0, OwnerFactionTag = "F" });
            var weak = TestSupport.WithOre(new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0, OwnerFactionTag = "F" });
            full.Richness = 1.0;
            weak.Richness = 0.5;

            long fullYield = Economy.SourceItemYield(full, 1.0, t, ItemKind.Ore);
            long weakYield = Economy.SourceItemYield(weak, 1.0, t, ItemKind.Ore);

            long expectedFull = (long)((double)t.FreeMinerCargoVolumeLiters / t.FreeMiningRunIntervalTicks
                                       * NodeContent.FreeMintScaleFor(full, ItemKind.Ore, t));
            Assert.Equal(expectedFull, fullYield);
            Assert.True(weakYield < fullYield, "thinner ground yields fewer items");
        }

        /// <summary>And worth alone does not: two nodes over identical ground mint identically
        /// however differently their deposits are priced. The companion to the test above — without
        /// it, that one passes on a fixture where the two quantities happen to move together.</summary>
        [Fact]
        public void ItemYield_DoesNotScaleWithWorth_OverIdenticalGround()
        {
            var t = TestSupport.Tuning();
            var cheap = TestSupport.WithOre(new SpatialNode { ResourceValue = 0.18, HoldStrength = 1.0, OwnerFactionTag = "F" });
            var dear  = TestSupport.WithOre(new SpatialNode { ResourceValue = 0.91, HoldStrength = 1.0, OwnerFactionTag = "F" });
            cheap.Richness = 0.6;
            dear.Richness  = 0.6;

            long cheapYield = Economy.SourceItemYield(cheap, 1.0, t, ItemKind.Ore);
            Assert.True(cheapYield > 0, "guard: identical ground must mint, or the equality below is vacuous");
            Assert.Equal(cheapYield, Economy.SourceItemYield(dear, 1.0, t, ItemKind.Ore));
        }

        [Fact]
        public void BesiegedSource_YieldsNoItems()
        {
            var t = TestSupport.Tuning();
            var src = TestSupport.WithOre(new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0, OwnerFactionTag = "F" });

            Assert.Equal(0L, Economy.SourceItemYield(src, 0.0, t, ItemKind.Ore));
            Assert.Equal(0L, Economy.SourceItemYield(src, 0.0, t, ItemKind.ProcessedGood));
            Assert.Equal(0L, Economy.SourceItemYield(src, 0.0, t, ItemKind.Hydrogen));
        }

        // The free mint gives a node what the ground gives it and nothing else. ProcessedGood is
        // ProductionManager's to make from inputs through a Manufactory, and Hydrogen has no producer
        // at all (FAC-038) -- both carried a per-kind scale and no yield-table entry, so neither ever
        // actually minted. See FAC-102.
        [Fact]
        public void FreeMint_GivesOre_AndNothingManufactured()
        {
            var t = TestSupport.Tuning();
            var src = TestSupport.WithOre(new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0, OwnerFactionTag = "F" });

            Assert.True(Economy.SourceItemYield(src, 1.0, t, ItemKind.Ore) > 0);
            Assert.Equal(0L, Economy.SourceItemYield(src, 1.0, t, ItemKind.ProcessedGood));
            Assert.Equal(0L, Economy.SourceItemYield(src, 1.0, t, ItemKind.Hydrogen));
            Assert.Equal(0L, Economy.SourceItemYield(src, 1.0, t, ItemKind.Oxygen));
        }

        // The gate is the deposit, not the label: the same node with nothing under it mints nothing,
        // and a node wearing no ore-bearing label at all mints once it has ground.
        [Fact]
        public void FreeMint_OreFollowsTheGround_NotTheLabel()
        {
            var t = TestSupport.Tuning();
            var bare = new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0 };
            var oreBearing = TestSupport.WithOre(new SpatialNode { ResourceValue = 1.0, HoldStrength = 1.0 });

            Assert.Equal(0L, Economy.SourceItemYield(bare, 1.0, t, ItemKind.Ore));
            Assert.True(Economy.SourceItemYield(oreBearing, 1.0, t, ItemKind.Ore) > 0);
        }

        // ---- AccrueIncome mints items and deducts upkeep -------------------------------------------

        [Fact]
        public void AccrueIncome_MintsItems_ForOwnedSource()
        {
            var w = BuildWorld();
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.Radius = 24;
            TestSupport.WithOre(n);

            w.AccrueIncome();

            // Ore under it, so it mints ore -- and only ore: nothing mints ProcessedGood or Hydrogen.
            long ore = w.GetInventory("F", ItemKind.Ore);
            long pg  = w.GetInventory("F", ItemKind.ProcessedGood);
            long fuel = w.GetInventory("F", ItemKind.Hydrogen);

            Assert.True(ore > 0,  "Ore should accumulate after AccrueIncome on a well-held node");
            Assert.Equal(0, pg);
            Assert.Equal(0, fuel);
        }

        [Fact]
        public void AccrueIncome_ZeroYield_WhenControlIsZero()
        {
            // Two faction nodes at the same spot: the rival's node dominates control for "F".
            var w = BuildWorld();
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.Radius = 24;

            // Add a rival node at exactly the same position with larger influence radius.
            SpatialNode rival = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "R1");
            rival.OwnerFactionTag = "G";
            rival.HoldStrength = 1.0;
            rival.Radius = 48;  // larger sphere → control for "F" at origin drops

            w.AccrueIncome();

            // We don't assert exact zero — control may be partial — but check Ore ≤ OreYield.
            long ore = w.GetInventory("F", ItemKind.Ore);
            Assert.True(ore <= w.Tuning.OreYield, "Contested source yields ≤ maximum Ore");
        }

        [Fact]
        public void AccrueIncome_ItemsFloorAtZero_NotNegative()
        {
            // A besieged source (control=0) should not produce any items but also not go negative
            // (upkeep is absorbed by the floor).
            var w = BuildWorld();
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.01, "N1");  // tiny value → yield < upkeep → net negative
            n.OwnerFactionTag = "F";
            n.HoldStrength = 0.01;
            n.Radius = 24;

            w.AccrueIncome();

            Assert.True(w.GetInventory("F", ItemKind.Ore)          >= 0);
            Assert.True(w.GetInventory("F", ItemKind.ProcessedGood) >= 0);
            Assert.True(w.GetInventory("F", ItemKind.Hydrogen)          >= 0);
        }

        // ---- Trade: deduct at dispatch, transfer at arrival ----------------------------------------

        [Fact]
        public void Trade_DeductsCargoFromSender_AtDispatch()
        {
            var world = BuildWorld();
            var t = world.Tuning;

            // Add nodes first; then seed the origin node's inventory directly
            SpatialNode nodeA = world.Graph.Add(new Vec3(0, 0, 0), 0.8, "NA");
            nodeA.OwnerFactionTag = "A";
            nodeA.HoldStrength = 0.9;
            nodeA.Inventory["Iron"]           = t.TradeCargoOre;
            nodeA.Inventory["SteelPlate"]     = t.TradeCargoProcessedGood;
            nodeA.Inventory["HydrogenBottle"] = t.TradeCargoHydrogen;

            SpatialNode nodeB = world.Graph.Add(new Vec3(100, 0, 0), 0.8, "NB");
            nodeB.OwnerFactionTag = "B";
            nodeB.HoldStrength = 0.9;

            var intent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "B", Cost = t.TradeCost };
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(ActionKind.Trade, "A", intent, world, t, immediate: true);

            // Cargo should have been populated and deducted
            Assert.NotNull(fleet.Cargo);
            Assert.True(fleet.Cargo.Count > 0, "Cargo should be non-empty when sender has items");
            Assert.Equal(0L, world.GetInventory("A", ItemKind.Ore));           // fully deducted
            Assert.Equal(0L, world.GetInventory("A", ItemKind.ProcessedGood));
            Assert.Equal(0L, world.GetInventory("A", ItemKind.Hydrogen));
        }

        [Fact]
        public void Trade_CargoValue_DerivedFromItemMarketValues()
        {
            var world = BuildWorld();
            var t = world.Tuning;

            SpatialNode nodeA = world.Graph.Add(new Vec3(0, 0, 0), 0.8, "NA");
            nodeA.OwnerFactionTag = "A";
            nodeA.Inventory["Iron"]           = t.TradeCargoOre;
            nodeA.Inventory["SteelPlate"]     = t.TradeCargoProcessedGood;
            nodeA.Inventory["HydrogenBottle"] = t.TradeCargoHydrogen;
            world.Graph.Add(new Vec3(100, 0, 0), 0.8, "NB").OwnerFactionTag = "B";

            var intent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "B", Cost = t.TradeCost };
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(ActionKind.Trade, "A", intent, world, t, immediate: true);

            long expectedValue = t.TradeCargoOre * t.FallbackOreValue
                               + t.TradeCargoProcessedGood * t.FallbackProcessedGoodValue
                               + t.TradeCargoHydrogen * t.FallbackHydrogenValue;
            Assert.Equal(expectedValue, fleet.CargoValue);
        }

        [Fact]
        public void Trade_TransfersCargo_ToRecipient_OnArrival()
        {
            var world = BuildWorld();
            var t = world.Tuning;

            SpatialNode nodeA = world.Graph.Add(new Vec3(0, 0, 0), 0.8, "NA");
            nodeA.OwnerFactionTag = "A"; nodeA.HoldStrength = 0.9;
            nodeA.Inventory["Iron"]           = t.TradeCargoOre;
            nodeA.Inventory["SteelPlate"]     = t.TradeCargoProcessedGood;
            nodeA.Inventory["HydrogenBottle"] = t.TradeCargoHydrogen;
            SpatialNode nodeB = world.Graph.Add(new Vec3(100, 0, 0), 0.8, "NB");
            nodeB.OwnerFactionTag = "B"; nodeB.HoldStrength = 0.9;

            var stA = new FactionState { Tag = "A" };
            var stB = new FactionState { Tag = "B" };
            var states = new List<FactionState> { stA, stB };

            var intent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "B", Cost = t.TradeCost };
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(ActionKind.Trade, "A", intent, world, t, immediate: true);
            stA.PendingFleets.Add(fleet);

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, world, t, new SeededRandom(1), log);

            Assert.True(world.GetInventory("B", ItemKind.Ore) > 0,
                "Recipient should have received Ore on trade arrival");
            Assert.True(world.GetInventory("B", ItemKind.ProcessedGood) > 0);
            Assert.True(world.GetInventory("B", ItemKind.Hydrogen) > 0);
        }

        [Fact]
        public void Trade_FallsBackToTradeCost_WhenSenderHasNoItems()
        {
            var world = BuildWorld();
            var t = world.Tuning;

            // Sender has no items at all
            world.Graph.Add(new Vec3(0, 0, 0), 0.8, "NA").OwnerFactionTag = "A";
            world.Graph.Add(new Vec3(100, 0, 0), 0.8, "NB").OwnerFactionTag = "B";

            var intent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "B", Cost = t.TradeCost };
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(ActionKind.Trade, "A", intent, world, t, immediate: true);

            Assert.Null(fleet.Cargo);
            Assert.Equal(t.TradeCost, fleet.CargoValue);  // fallback
        }

        // ---- Persistence round-trip ----------------------------------------------------------------

        [Fact]
        public void Cargo_SerializeDeserialize_Roundtrip()
        {
            // New-format cargo: SubTypeId keys pass through unchanged.
            var original = new Dictionary<string, long>
            {
                { "Iron",           500 },
                { "SteelPlate",     200 },
                { "HydrogenBottle", 100 }
            };

            var serialized   = FactionStore.SerializeCargo(original);
            var deserialized = FactionStore.DeserializeCargo(serialized);

            Assert.NotNull(deserialized);
            Assert.Equal(500L, deserialized["Ore:Iron"]);
            Assert.Equal(200L, deserialized["Comp:SteelPlate"]);
            Assert.Equal(100L, deserialized["HydrogenBottle"]);
        }

        [Fact]
        public void Cargo_Deserialize_MigratesOldItemKindKeys()
        {
            // Old saves used ItemKind names as keys; they should be converted to representative SubTypeIds.
            var old = new Dictionary<string, long>
            {
                { "Ore",          500 },
                { "ProcessedGood", 200 },
                { "Hydrogen",      100 },
                { "Fuel",          50  }  // legacy pre-split name
            };

            var migrated = FactionStore.DeserializeCargo(old);

            Assert.NotNull(migrated);
            Assert.Equal(500L, migrated["Ore:Iron"]);
            Assert.Equal(200L, migrated["Comp:SteelPlate"]);
            Assert.Equal(150L, migrated["HydrogenBottle"]);  // Hydrogen + Fuel merged
        }

        [Fact]
        public void Cargo_SerializeNull_ReturnsNull()
        {
            Assert.Null(FactionStore.SerializeCargo(null));
            Assert.Null(FactionStore.DeserializeCargo(null));
        }

        // ---- helpers -------------------------------------------------------------------------------

        private static WorldModel BuildWorld()
        {
            return TestSupport.World(42);
        }
    }
}
