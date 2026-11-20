using System;
using System.Collections.Generic;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A player faction naming what its shop sells (FAC-322): the evaluator, the two gates,
    /// the release phase and the round-trip.
    ///
    /// <para>The discriminating pair throughout is the shelf against the fleet. They count different
    /// things about the same hull -- <see cref="SpatialNode.ListedForSale"/> against
    /// <see cref="SpatialNode.ShipStock"/> -- and a change that conflated them would leave one list
    /// satisfying the other's target, which is exactly the miscount this ticket was filed over.</para></summary>
    public class PlayerShelfTests
    {
        private const string Hauler = "Hauler";
        private const string Drill  = "Driller";

        private sealed class Catalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, long> Cargo = new Dictionary<string, long>();
            public readonly Dictionary<string, double> DrillScore = new Dictionary<string, double>();
            public long CargoLiters(string p) { long v; return Cargo.TryGetValue(p, out v) ? v : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string p) { return new Dictionary<string, long>(); }
            public long Cost(string p) { return 10; }
            public string FactionHint(string p) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string p) { return new WeaponBand[0]; }
            public string GridSize(string p) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string r, string h) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string p)
            { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string p) { return 0; }
            public double DrillYieldScore(string p) { double v; return DrillScore.TryGetValue(p, out v) ? v : 0; }
        }

        private sealed class Recipes : IRecipeRegistry
        {
            public readonly HashSet<string> Buildable = new HashSet<string>(StringComparer.Ordinal);
            public IEnumerable<ConversionRecipe> GetRecipesFor(string outType, string outSub)
            {
                if (outType != "Prefab" || !Buildable.Contains(outSub)) yield break;
                yield return new ConversionRecipe
                {
                    BlueprintId = outSub,
                    Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Steel", Amount = 10 } },
                    Outputs = new RecipeItem[0],
                };
            }
            public IEnumerable<ConversionRecipe> AllRecipes { get { yield break; } }
            public int Count { get { return Buildable.Count; } }
        }

        private static WorldModel MakeWorld(out Catalogue cat)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            cat = new Catalogue();
            cat.Cargo[Hauler] = 5000;        // classes as Cargo -> the "Trade" role
            cat.DrillScore[Drill] = 1.0;     // classes as Drill -> the "Mine" role
            var recipes = new Recipes();
            recipes.Buildable.Add(Hauler);
            recipes.Buildable.Add(Drill);
            world.PrefabCatalogue = cat;
            world.Recipes = recipes;
            return world;
        }

        private static SpatialNode AddYard(WorldModel world, string id, string tag = "EXP")
        {
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = 1.0;
            n.StableStationId = Math.Abs(id.GetHashCode()) + 1;
            n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return n;
        }

        private static FactionState Player(string tag = "EXP")
        {
            return new FactionState { Tag = tag, IsPlayerFaction = true };
        }

        private static ShelfLine AddLine(FactionState st, string prefabId, int target)
        {
            var line = new ShelfLine { Id = st.MintShelfLineId(), PrefabId = prefabId, TargetCount = target };
            st.AddShelfLine(line);
            return line;
        }

        private static List<string> Tick(WorldModel world, params FactionState[] states)
        {
            var log = new List<string>();
            new StoreStockManager().Tick(new List<FactionState>(states), world, world.Tuning, log);
            return log;
        }

        private static int ShelfBuilds(SpatialNode node, string prefabId)
        {
            int n = 0;
            foreach (BuildQueueEntry e in node.BuildQueue)
                if (e.DestinedForSale && e.PrefabId == prefabId) n++;
            return n;
        }

        // ---- the predicate ----------------------------------------------------------------------

        [Fact]
        public void ShelfActive_IsAPlayerFactionAndANonEmptyList()
        {
            FactionState playerWithLines = Player();
            AddLine(playerWithLines, Hauler, 2);
            Assert.True(FactionLifecycle.ShelfActive(playerWithLines));

            Assert.False(FactionLifecycle.ShelfActive(Player()));

            var npcWithLines = new FactionState { Tag = "NPC" };
            AddLine(npcWithLines, Hauler, 2);
            Assert.False(FactionLifecycle.ShelfActive(npcWithLines));
        }

        // ---- the evaluator ----------------------------------------------------------------------

        [Fact]
        public void AShelfLine_PlacesAStoreBuildForTheHullItNames()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            FactionState st = Player();
            AddLine(st, Hauler, 2);

            List<string> log = Tick(world, st);

            BuildQueueEntry built = Assert.Single(yard.BuildQueue);
            Assert.Equal(Hauler, built.PrefabId);
            // The flag is what makes the finished hull reserve into ListedForSale instead of joining
            // the fleet, and what ranks it below every fleet role.
            Assert.True(built.DestinedForSale);
            Assert.Equal("Trade", built.Role);   // derived from the hull, not asked of the player
            Assert.Equal(world.Tuning.StoreShipBuildPriority, built.Priority);
            Assert.Contains(log, l => l.Contains("SHELF_BUILD ") && l.Contains("line=SH-001")
                                      && l.Contains("target=2") && l.Contains("placed=1"));
        }

        [Fact]
        public void AShelfActiveFaction_PostsNoStoreShipDemand_AndAnNpcStillDoes()
        {
            // The gate, with its control in the same Tick call: a gate that turned the old path off
            // for everyone would pass the "EXP is silent" half on its own.
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            AddYard(world, "N1");
            AddYard(world, "N9", "NPC");
            FactionState player = Player();
            AddLine(player, Hauler, 2);
            var npc = new FactionState { Tag = "NPC" };

            List<string> log = Tick(world, player, npc);

            Assert.DoesNotContain(log, l => l.StartsWith("EXP STORE_SHIP_DEMAND", StringComparison.Ordinal));
            Assert.Contains(log, l => l.StartsWith("NPC STORE_SHIP_DEMAND", StringComparison.Ordinal));
        }

        [Fact]
        public void AtTarget_NothingIsBuilt()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            yard.ShipStock[Hauler] = 2;
            yard.ListedForSale[Hauler] = 2;      // both already on sale
            FactionState st = Player();
            AddLine(st, Hauler, 2);

            Tick(world, st);

            Assert.Empty(yard.BuildQueue);
        }

        [Fact]
        public void HullsTheFleetOwns_DoNotSatisfyAShelfLine()
        {
            // The discriminating case for "have". Two Haulers parked, none of them listed: the fleet
            // owns them and the shop has nothing, so the line must still build. Counting ShipStock
            // here would leave a player's shop permanently empty while reporting it full.
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            yard.ShipStock[Hauler] = 2;
            FactionState st = Player();
            AddLine(st, Hauler, 2);

            Tick(world, st);

            Assert.Equal(1, ShelfBuilds(yard, Hauler));
        }

        [Fact]
        public void AFleetBuildOfTheSameHull_DoesNotCountAsShelfProgress()
        {
            // The mirror of the test above, on the "building" term: a fleet build of this hull is
            // destined for the fleet and never reaches the shelf.
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            var fleetBuild = new BuildQueueEntry
            {
                Id = yard.MintBuildEntryId("Trade"), Role = "Trade", PrefabId = Hauler,
                DestinedForSale = false, ExpiresUtc = world.UtcNow.AddHours(4),
            };
            fleetBuild.Bom["Comp:Steel"] = 10;
            yard.BuildQueue.Add(fleetBuild);
            FactionState st = Player();
            AddLine(st, Hauler, 1);

            Tick(world, st);

            Assert.Equal(1, ShelfBuilds(yard, Hauler));
        }

        [Fact]
        public void OneShelfBuildAtATimePerLine()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            AddYard(world, "N2");
            FactionState st = Player();
            AddLine(st, Hauler, 5);

            Tick(world, st);
            Tick(world, st);

            // Two yards and a shortfall of five, but a shop hull is the one thing nothing is waiting
            // on: a second build would put the yard's queue to work on stock nobody asked for.
            Assert.Equal(1, ShelfBuilds(world.Graph.Nodes["N1"], Hauler)
                          + ShelfBuilds(world.Graph.Nodes["N2"], Hauler));
        }

        [Fact]
        public void APausedLine_IsNotEvaluated()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            FactionState st = Player();
            ShelfLine line = AddLine(st, Hauler, 2);
            st.SetShelfLinePaused(line, true);

            Tick(world, st);

            Assert.Empty(yard.BuildQueue);
        }

        [Fact]
        public void AnUnclassedHull_SaysSoRatherThanFailingSilently()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            AddYard(world, "N1");
            FactionState st = Player();
            AddLine(st, "NoSuchHull", 2);     // no cargo, no weapons, no drill

            List<string> log = Tick(world, st);

            Assert.Contains(log, l => l.Contains("SHELF_LINE_BLOCKED") && l.Contains("reason=unclassed"));
            // Log-on-change: a line blocked for a day says so once.
            Assert.DoesNotContain(Tick(world, st), l => l.Contains("SHELF_LINE_BLOCKED"));
        }

        // ---- the off switch ---------------------------------------------------------------------

        [Fact]
        public void StockingOff_RefundsStoreBuilds_AndAnNpcIsUntouched()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            AddYard(world, "N9", "NPC");
            var shelfBuild = new BuildQueueEntry
            {
                Id = yard.MintBuildEntryId("Trade"), Role = "Trade", PrefabId = Hauler,
                DestinedForSale = true, ExpiresUtc = world.UtcNow.AddHours(4),
            };
            shelfBuild.Bom["Comp:Steel"] = 100;
            shelfBuild.Collected["Comp:Steel"] = 40;
            yard.BuildQueue.Add(shelfBuild);

            FactionState st = Player();
            st.SetStoreShelfOptOut(true);
            var npc = new FactionState { Tag = "NPC" };

            List<string> log = Tick(world, st, npc);

            Assert.Empty(yard.BuildQueue);
            long refunded;
            yard.Inventory.TryGetValue("Comp:Steel", out refunded);
            Assert.Equal(40, refunded);                       // refunded, not destroyed
            Assert.Contains(log, l => l.Contains("EXP SHELF_STOCKING_OFF"));
            Assert.Contains(log, l => l.StartsWith("NPC STORE_SHIP_DEMAND", StringComparison.Ordinal));
        }

        [Fact]
        public void StockingOff_DoesNotUnlistWhatIsAlreadyForSale()
        {
            // The player stopped ordering stock, not offering it. A hull a customer could have bought
            // a minute ago must not vanish from the shelf.
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            yard.ShipStock[Hauler] = 3;
            yard.ListedForSale[Hauler] = 3;
            FactionState st = Player();
            st.SetStoreShelfOptOut(true);

            Tick(world, st);

            Assert.Equal(3, yard.ListedForSale[Hauler]);
        }

        // ---- handing stock back -----------------------------------------------------------------

        [Fact]
        public void RemovingALine_HandsItsStockBackWithNoDeadBand()
        {
            // Removing a line is the reservation's reason lapsing entirely, which is the case a
            // target-keyed loop cannot see. The dead band exists to stop a stocking pass and a
            // draining pass fighting over one count; nothing stocks a hull no line names.
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            yard.ShipStock[Hauler] = 1;
            yard.ListedForSale[Hauler] = 1;
            FactionState st = Player();
            AddLine(st, Drill, 1);            // shelf-active, but nothing names the Hauler

            Tick(world, st);

            Assert.False(yard.ListedForSale.ContainsKey(Hauler));
            Assert.Equal(1, yard.ShipStock[Hauler]);   // released to the fleet, not destroyed
        }

        [Fact]
        public void ALoweredTarget_KeepsTheDeadBand()
        {
            // The paired negative: a row still being stocked keeps its margin, or the stocking pass
            // and the release pass oscillate against each other one hull at a time.
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            yard.ShipStock[Hauler] = 2;
            yard.ListedForSale[Hauler] = 2;
            FactionState st = Player();
            AddLine(st, Hauler, 1);           // one over target, and the margin is 1

            Tick(world, st);

            Assert.Equal(2, yard.ListedForSale[Hauler]);
        }

        // ---- the report the Store window is drawn from ------------------------------------------

        [Fact]
        public void Report_CountsEachLineFromTheManagersOwnHelpers()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            yard.ShipStock[Hauler] = 5;
            yard.ListedForSale[Hauler] = 2;      // three are the fleet's, two are on sale
            var building = new BuildQueueEntry
            {
                Id = yard.MintBuildEntryId("Trade"), Role = "Trade", PrefabId = Hauler,
                DestinedForSale = true, ExpiresUtc = world.UtcNow.AddHours(4),
            };
            building.Bom["Comp:Steel"] = 10;
            yard.BuildQueue.Add(building);
            FactionState st = Player();
            AddLine(st, Hauler, 4);

            PlayerShelfReport report = PlayerShelfReport.Build(world, st);

            ShelfLineReport row = Assert.Single(report.Lines);
            // Two, not five: the three the fleet owns are not on sale, which is the distinction the
            // whole feature rests on.
            Assert.Equal(2, row.OnSale);
            Assert.Equal(1, row.Building);
            Assert.Equal("Trade", row.Role);
            Assert.False(report.StockingOff);
        }

        [Fact]
        public void Report_MarksCensusRowsNoLineNames()
        {
            // The reason the Store window exists. The Fleet window's census counts UnlistedStockAt,
            // which subtracts this stock out entirely, so before this report no surface in the game
            // showed a hull that was for sale.
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            yard.ShipStock[Hauler] = 1;
            yard.ListedForSale[Hauler] = 1;
            yard.ShipStock[Drill] = 3;
            yard.ListedForSale[Drill] = 3;
            FactionState st = Player();
            AddLine(st, Hauler, 1);              // nothing names the drill hull

            PlayerShelfReport report = PlayerShelfReport.Build(world, st);

            Assert.Equal(2, report.Census.Count);
            ShelfCensusRow drill = report.Census.Find(r => r.PrefabId == Drill);
            ShelfCensusRow hauler = report.Census.Find(r => r.PrefabId == Hauler);
            Assert.NotNull(drill);
            Assert.NotNull(hauler);
            Assert.True(drill.Unlined);
            Assert.Equal(3, drill.OnSale);
            Assert.False(hauler.Unlined);
        }

        [Fact]
        public void Report_CensusCountsOnlyWhatIsReservedForSale()
        {
            // The paired negative for the census: a hull the fleet owns and nobody listed is not on
            // the shelf and must not appear on it. A census built from ShipStock would put the
            // faction's whole fleet in the shop window.
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            SpatialNode yard = AddYard(world, "N1");
            yard.ShipStock[Hauler] = 9;          // parked, none listed
            FactionState st = Player();
            AddLine(st, Drill, 1);

            PlayerShelfReport report = PlayerShelfReport.Build(world, st);

            Assert.Empty(report.Census);
        }

        [Fact]
        public void Report_CarriesTheBlockedReasonTheManagerRecorded()
        {
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            AddYard(world, "N1");
            FactionState st = Player();
            AddLine(st, "NoSuchHull", 2);

            Tick(world, st);                     // the pass that records the reason
            PlayerShelfReport report = PlayerShelfReport.Build(world, st);

            // The bare token, not the whole log signature: only the reason is a fact about the line.
            Assert.Equal("unclassed", Assert.Single(report.Lines).BlockedReason);
        }

        [Fact]
        public void Report_BlockedReasonIsNullBeforeTheFirstPass()
        {
            // Stated because it is a real limit of reading manager-owned state rather than
            // recomputing it: the field is not persisted, so a window opened immediately after a
            // restart shows no reason until the store manager has run once.
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            AddYard(world, "N1");
            FactionState st = Player();
            AddLine(st, "NoSuchHull", 2);

            PlayerShelfReport report = PlayerShelfReport.Build(world, st);

            Assert.Null(Assert.Single(report.Lines).BlockedReason);
        }

        [Fact]
        public void Report_CarriesTheStockingSwitch()
        {
            // Sent separately from the lines because a shop switched off and a shop with nothing to
            // do are otherwise the same picture, and only one of them is something a player fixes.
            Catalogue cat;
            WorldModel world = MakeWorld(out cat);
            AddYard(world, "N1");
            FactionState st = Player();
            AddLine(st, Hauler, 1);
            st.SetStoreShelfOptOut(true);

            Assert.True(PlayerShelfReport.Build(world, st).StockingOff);
        }

        // ---- and survive a restart --------------------------------------------------------------

        private static FactionState RoundTrip(FactionState st)
        {
            var graph = new SpatialGraph();
            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy(st.Tag), FactionStore.DefaultLogCap);
            return FactionStore.ApplyBrainDoc(doc, new SpatialGraph(), null, null);
        }

        [Fact]
        public void ShelfLine_RoundTripsEveryPlayerSuppliedField()
        {
            FactionState st = Player("ACME");
            ShelfLine original = AddLine(st, Hauler, 7);
            st.SetShelfLinePaused(original, true);
            original.CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            st.SetStoreShelfOptOut(true);

            FactionState back = RoundTrip(st);
            ShelfLine loaded = Assert.Single(back.Shelf);

            Assert.Equal("SH-001", loaded.Id);
            Assert.Equal(Hauler, loaded.PrefabId);
            Assert.Equal(7, loaded.TargetCount);
            Assert.True(loaded.Paused);
            Assert.Equal(original.CreatedUtc, loaded.CreatedUtc);
            Assert.True(back.StoreShelfOptOut);
            // The counter travels with the list, so the next line minted after a restart cannot
            // collide with an id the player already has written down.
            Assert.Equal(1, back.ShelfSeq);
            Assert.Equal("SH-002", back.MintShelfLineId());
        }

        [Fact]
        public void AnEmptyShelf_IsOmittedFromTheSerializedBrain()
        {
            var graph = new SpatialGraph();
            BrainDoc doc = FactionStore.ToBrainDoc(new FactionState { Tag = "ACME" },
                graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);

            Assert.Null(doc.Shelf);
            // "Keep JSON lean": every NPC brain would otherwise carry the key. Asserted on the
            // SERIALIZED text, not the DTO -- a null field with the wrong NullValueHandling still
            // writes "Shelf": null.
            Assert.DoesNotContain("Shelf", JsonConvert.SerializeObject(doc), StringComparison.Ordinal);
        }
    }
}
