using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Per-hull integrity: the slot bookkeeping on <see cref="PendingFleetAction"/>, its
    /// persistence, the abstract attrition rule, the repair bill, the landing of a damaged hull into
    /// repair rather than stock, the yard's repair pass, and a duel writing its attrition into the
    /// hulls that come out of it.</summary>
    public class HullIntegrityTests
    {
        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private static PendingFleetAction Squadron(params string[] hulls)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", ShipPrefabId = hulls[0], ConvoySize = hulls.Length,
                Squadron = new List<string>(hulls), FleetId = "F00001",
            };
        }

        private static ArmedYard.ArmedCatalogue Catalogue()
        {
            return new ArmedYard.ArmedCatalogue()
                .Add("Fighter", 100, "Small")
                .Add("Gunship", 400, "Large")
                .Add("Hauler", 0, "Large", cargo: 5000);
        }

        private static ArmedYard.CostRecipes Recipes()
        {
            var r = new ArmedYard.CostRecipes();
            r.CostByPrefab["Fighter"] = 100;
            r.CostByPrefab["Gunship"] = 400;
            r.CostByPrefab["Hauler"] = 50;
            return r;
        }

        // ---- slot bookkeeping ------------------------------------------------------------------

        [Fact]
        public void SetIntegrity_StaysNullWhileWhole_AndSnapsNearWholeToWhole()
        {
            PendingFleetAction f = Squadron("Fighter", "Fighter");
            f.SetIntegrity(0, 1.0);
            Assert.Null(f.HullIntegrity);
            f.SetIntegrity(1, 0.995);
            Assert.Null(f.HullIntegrity);

            f.SetIntegrity(1, 0.6);
            Assert.Equal(new List<double> { 1.0, 0.6 }, f.HullIntegrity);
            f.SetIntegrity(1, 1.0);
            Assert.Null(f.HullIntegrity);
        }

        [Fact]
        public void SetIntegrity_DoesNotReachAStructCopyThatSharesTheList()
        {
            PendingFleetAction f = Squadron("Fighter", "Fighter");
            f.SetIntegrity(0, 0.5);
            PendingFleetAction copy = f;
            f.SetIntegrity(1, 0.4);
            Assert.Equal(1.0, copy.IntegrityOf(1));
            Assert.Equal(0.4, f.IntegrityOf(1));
        }

        [Fact]
        public void SingleHullFleet_HasOneSlot_AndIgnoresOthers()
        {
            var f = new PendingFleetAction { Kind = ActionKind.Trade, ShipPrefabId = "Hauler", ConvoySize = 3 };
            Assert.Equal(1, f.HullSlotCount);
            f.SetIntegrity(1, 0.5);
            Assert.Null(f.HullIntegrity);
            f.SetIntegrity(0, 0.5);
            Assert.Equal(0.5, f.IntegrityOf(0));
        }

        [Fact]
        public void MarkHullLost_WithoutDamage_RemovesTheFirstOccurrence_AsBefore()
        {
            PendingFleetAction f = Squadron("Fighter", "Gunship", "Fighter");
            f.MarkHullLost("Fighter");
            Assert.Equal(new List<string> { "Gunship", "Fighter" }, f.SurvivingHulls);
            Assert.Null(f.HullIntegrity);
        }

        [Fact]
        public void MarkHullLost_TakesTheMostDamagedHullOfThatPrefab_AndKeepsTheListsParallel()
        {
            PendingFleetAction f = Squadron("Fighter", "Gunship", "Fighter");
            f.SetIntegrity(0, 0.9);
            f.SetIntegrity(1, 0.7);
            f.SetIntegrity(2, 0.3);
            f.MarkHullLost("Fighter");
            Assert.Equal(new List<string> { "Fighter", "Gunship" }, f.SurvivingHulls);
            Assert.Equal(new List<double> { 0.9, 0.7 }, f.HullIntegrity);
        }

        [Fact]
        public void MarkHullLostAt_RemovesExactlyThatSlot()
        {
            PendingFleetAction f = Squadron("Fighter", "Fighter", "Gunship");
            f.SetIntegrity(0, 0.2);
            f.SetIntegrity(1, 0.8);
            f.MarkHullLostAt(1);
            Assert.Equal(new List<string> { "Fighter", "Gunship" }, f.SurvivingHulls);
            Assert.Equal(new List<double> { 0.2, 1.0 }, f.HullIntegrity);
            Assert.Equal(2, f.HullSlotCount);
        }

        [Fact]
        public void LosingTheOnlyDamagedHull_LeavesTheFleetWhole()
        {
            PendingFleetAction f = Squadron("Fighter", "Gunship");
            f.SetIntegrity(1, 0.4);
            f.MarkHullLost("Gunship");
            Assert.Null(f.HullIntegrity);
            Assert.Equal(new List<string> { "Fighter" }, f.HullsToReturn.ToList());
        }

        // ---- persistence -----------------------------------------------------------------------

        [Fact]
        public void Brain_RoundTripsHullIntegrity_AndDamagedHulls()
        {
            var graph = new SpatialGraph();
            SpatialNode home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "A"; home.HoldStrength = 0.8;
            var hull = new DamagedHull
            {
                PrefabId = "Gunship", Integrity = 0.45,
                QueuedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            hull.Bill["Comp:Steel"] = 220;
            hull.Collected["Comp:Steel"] = 20;
            home.DamagedHulls.Add(hull);

            var st = new FactionState { Tag = "A" };
            PendingFleetAction f = Squadron("Fighter", "Gunship");
            f.OriginNodeId = "H1"; f.TargetNodeId = "E1";
            f.SetIntegrity(1, 0.62);
            st.PendingFleets.Add(f);

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("A"), FactionStore.DefaultLogCap));
                var graph2 = new SpatialGraph();
                FactionState st2 = FactionStore.ApplyBrainDoc(store.LoadBrain("A"), graph2);

                Assert.Equal(new List<double> { 1.0, 0.62 }, st2.PendingFleets[0].HullIntegrity);
                DamagedHull back = Assert.Single(graph2.Nodes["H1"].DamagedHulls);
                Assert.Equal("Gunship", back.PrefabId);
                Assert.Equal(0.45, back.Integrity);
                Assert.Equal(220, back.Bill["Comp:Steel"]);
                Assert.Equal(20, back.Collected["Comp:Steel"]);
                Assert.Equal(default(DateTime), back.PartsCompleteUtc);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Brain_OmitsBothFields_WhenEverythingIsWhole()
        {
            var graph = new SpatialGraph();
            SpatialNode home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "A"; home.HoldStrength = 0.8;
            var st = new FactionState { Tag = "A" };
            PendingFleetAction f = Squadron("Fighter", "Gunship");
            f.OriginNodeId = "H1"; f.TargetNodeId = "E1";
            st.PendingFleets.Add(f);

            string json = JsonConvert.SerializeObject(FactionStore.ToBrainDoc(st, graph.OwnedBy("A"), FactionStore.DefaultLogCap),
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            Assert.DoesNotContain("HullIntegrity", json, StringComparison.Ordinal);
            Assert.DoesNotContain("DamagedHulls", json, StringComparison.Ordinal);
        }

        [Fact]
        public void Brain_LegacyDocWithoutTheFields_LoadsWhole()
        {
            var graph = new SpatialGraph();
            SpatialNode home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "A"; home.HoldStrength = 0.8;
            var st = new FactionState { Tag = "A" };
            PendingFleetAction f = Squadron("Fighter", "Gunship");
            f.OriginNodeId = "H1"; f.TargetNodeId = "E1";
            st.PendingFleets.Add(f);
            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("A"), FactionStore.DefaultLogCap);
            doc.PendingFleets[0].HullIntegrity = null;

            var graph2 = new SpatialGraph();
            FactionState back = FactionStore.ApplyBrainDoc(
                JsonConvert.DeserializeObject<BrainDoc>(JsonConvert.SerializeObject(doc)), graph2);
            Assert.Null(back.PendingFleets[0].HullIntegrity);
            Assert.Empty(graph2.Nodes["H1"].DamagedHulls);
        }

        [Fact]
        public void Brain_IntegrityThatDoesNotMatchTheHulls_LoadsWhole()
        {
            Assert.Null(FactionStore.NormalizeHullIntegrity(new List<double> { 0.5 }, 2));
            Assert.Null(FactionStore.NormalizeHullIntegrity(new List<double> { 1.0, 0.999 }, 2));
            Assert.Equal(new List<double> { 0.0, 1.0 },
                         FactionStore.NormalizeHullIntegrity(new List<double> { -3, double.NaN }, 2));
        }

        // ---- repair bill -----------------------------------------------------------------------

        [Fact]
        public void RepairBill_IsTheBomTimesTheMissingIntegrity_RoundedUp()
        {
            var bom = new Dictionary<string, long> { { "Comp:Steel", 100 }, { "Comp:Grid", 7 } };
            Dictionary<string, long> bill = HullDamage.RepairBill(bom, 0.6);
            Assert.Equal(40, bill["Comp:Steel"]);
            Assert.Equal(3, bill["Comp:Grid"]);   // 2.8 rounds up

            Assert.Empty(HullDamage.RepairBill(bom, 0.995));
            Assert.Equal(100, HullDamage.RepairBill(bom, 0.0)["Comp:Steel"]);
            Assert.Equal(1, HullDamage.RepairBill(bom, 0.98)["Comp:Grid"]);   // never free
        }

        [Fact]
        public void RepairTime_ScalesWithTheMissingIntegrity()
        {
            Assert.Equal(TimeSpan.FromHours(HullDamage.RepairHoursPerHull * 0.4), HullDamage.RepairTime(0.6));
            Assert.Equal(TimeSpan.Zero, HullDamage.RepairTime(1.0));
        }

        // ---- abstract attrition ----------------------------------------------------------------

        [Fact]
        public void Attrition_IsUniform_Floored_AndDeterministic()
        {
            ArmedYard.ArmedCatalogue cat = Catalogue();
            PendingFleetAction a = Squadron("Fighter", "Gunship", "Fighter");
            a.WeaponBands = HullDamage.ScaledBands(a, cat);
            a.SetIntegrity(2, 0.2);
            PendingFleetAction b = a;
            b.HullIntegrity = new List<double>(a.HullIntegrity);

            Assert.True(HullDamage.ApplyAttrition(ref a, 0.5, cat));
            Assert.True(HullDamage.ApplyAttrition(ref b, 0.5, cat));
            Assert.Equal(a.HullIntegrity, b.HullIntegrity);
            // Each slot keeps half, floored at the survivor minimum; a hull already under it stays.
            Assert.Equal(new List<double> { 0.5, 0.5, 0.2 }, a.HullIntegrity);

            HullDamage.ApplyAttrition(ref a, 0.0, cat);
            Assert.Equal(new List<double> { HullDamage.MinSurvivingIntegrity, HullDamage.MinSurvivingIntegrity, 0.2 },
                         a.HullIntegrity);
        }

        [Fact]
        public void Attrition_RescalesAnArmedFleetsBands_ButGivesAConvoyNone()
        {
            ArmedYard.ArmedCatalogue cat = Catalogue();
            PendingFleetAction f = Squadron("Fighter", "Gunship");
            f.WeaponBands = HullDamage.ScaledBands(f, cat);
            Assert.Equal(500, f.WeaponBands.Sum(w => w.Dps), 3);
            HullDamage.ApplyAttrition(ref f, 0.5, cat);
            Assert.Equal(250, f.WeaponBands.Sum(w => w.Dps), 3);

            var convoy = new PendingFleetAction { Kind = ActionKind.Trade, ShipPrefabId = "Hauler", ConvoySize = 2 };
            HullDamage.ApplyAttrition(ref convoy, 0.5, cat);
            Assert.Null(convoy.WeaponBands);
            Assert.Equal(0.5, convoy.IntegrityOf(0));
        }

        [Fact]
        public void Attrition_AtFullStrength_ChangesNothing()
        {
            PendingFleetAction f = Squadron("Fighter", "Gunship");
            Assert.False(HullDamage.ApplyAttrition(ref f, 1.0, Catalogue()));
            Assert.Null(f.HullIntegrity);
        }

        // ---- landing ---------------------------------------------------------------------------

        private static WorldModel ReturnWorld(out SpatialNode home)
        {
            WorldModel w = TestSupport.World();
            home = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "A1");
            home.OwnerFactionTag = "A"; home.HoldStrength = 0.8; home.Radius = 40;
            SpatialNode target = w.Graph.Add(new Vec3(10, 0, 0), 0.6, "B1");
            target.OwnerFactionTag = "B"; target.HoldStrength = 0.8; target.Radius = 40;
            w.PrefabCatalogue = Catalogue();
            w.Recipes = Recipes();
            return w;
        }

        /// <summary>A squadron whose lead the player killed: the terminal lands the survivors, the
        /// same path <c>FleetConvoyTests.Raid_SquadronLeadDestroyed_*</c> covers for whole hulls.</summary>
        private static PendingFleetAction LeadLostSquadron(WorldModel w, double haulerIntegrity)
        {
            var raid = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1", OriginNodeId = "A1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow, CargoValue = 3000,
                ShipPrefabId = "Gunship", ConvoySize = 3, FleetId = "F00002",
                Squadron = new List<string> { "Gunship", "Hauler", "Fighter" },
            };
            raid.MarkHullLost("Gunship");
            raid.SetIntegrity(0, haulerIntegrity);   // Hauler is slot 0 of the survivors
            raid.MaterialisedEntityId = -1;
            raid.ForcedRatio = 0.0;
            raid.ForcedRatioCause = "grid_destroyed";
            return raid;
        }

        [Fact]
        public void DamagedHull_LandsInRepair_NotInStock()
        {
            SpatialNode home;
            WorldModel w = ReturnWorld(out home);
            var attacker = new FactionState { Tag = "A" };
            attacker.PendingFleets.Add(LeadLostSquadron(w, 0.4));
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { attacker, new FactionState { Tag = "B" } },
                w, w.Tuning, new FixedRandom(0.0), log, null);

            int hauler, fighter;
            home.ShipStock.TryGetValue("Hauler", out hauler);
            home.ShipStock.TryGetValue("Fighter", out fighter);
            Assert.Equal(0, hauler);    // the failure this guards against: a damaged hull back in stock
            Assert.Equal(1, fighter);
            DamagedHull repair = Assert.Single(home.DamagedHulls);
            Assert.Equal("Hauler", repair.PrefabId);
            Assert.Equal(0.4, repair.Integrity);
            Assert.Equal(30, repair.Bill["Comp:Steel"]);   // 50 x 0.6

            string joined = string.Join("\n", log);
            Assert.Contains("REPAIR_QUEUED A fleet=[F00002] prefab=Hauler node=A1 integrity=0.40 bill_units=30", joined);
            Assert.Contains("yard=none", joined);
            Assert.Contains("hulls_returned=2/3 repairing=1", joined);
        }

        [Fact]
        public void WholeFleet_LandsExactlyAsBefore()
        {
            SpatialNode home;
            WorldModel w = ReturnWorld(out home);
            var attacker = new FactionState { Tag = "A" };
            PendingFleetAction raid = LeadLostSquadron(w, 1.0);
            Assert.Null(raid.HullIntegrity);
            attacker.PendingFleets.Add(raid);
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { attacker, new FactionState { Tag = "B" } },
                w, w.Tuning, new FixedRandom(0.0), log, null);

            Assert.Equal(1, home.ShipStock["Hauler"]);
            Assert.Equal(1, home.ShipStock["Fighter"]);
            Assert.Empty(home.DamagedHulls);
            Assert.DoesNotContain(log, l => l.Contains("REPAIR_") || l.Contains("repairing="));
        }

        [Fact]
        public void DamagedSingleHullConvoy_LandsEveryHullInRepair()
        {
            SpatialNode home;
            WorldModel w = ReturnWorld(out home);
            home.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            var log = new List<string>();
            int queued = HullDamage.Land(w, home, "A1", "Hauler", 2, 0.5, "[F]", "A", log);
            Assert.Equal(2, queued);
            Assert.Equal(2, home.DamagedHulls.Count);
            Assert.DoesNotContain(log, l => l.Contains("yard=none"));
            Assert.False(home.ShipStock.ContainsKey("Hauler"));
        }

        [Fact]
        public void DamagedHullWithNoRecipe_LandsInStock_AndSaysWhy()
        {
            SpatialNode home;
            WorldModel w = ReturnWorld(out home);
            var log = new List<string>();
            Assert.Equal(0, HullDamage.Land(w, home, "A1", "Mystery", 1, 0.5, "[F]", "A", log));
            Assert.Equal(1, home.ShipStock["Mystery"]);
            Assert.Contains(log, l => l.StartsWith("REPAIR_SKIPPED A fleet=[F] prefab=Mystery", StringComparison.Ordinal));
        }

        // ---- repair pass -----------------------------------------------------------------------

        private static WorldModel YardWithDamagedHull(double integrity, long steel, out SpatialNode yard)
        {
            WorldModel w = ArmedYard.Yard(Catalogue(), Recipes(), out yard);
            HullDamage.Land(w, yard, yard.Id, "Gunship", 1, integrity, "[F]", "F", new List<string>());
            if (steel > 0) w.AddNodeInventory(yard.Id, "Comp:Steel", steel);
            return w;
        }

        [Fact]
        public void Repair_ConsumesItsBill_ThenTakesItsTime_ThenReturnsTheHullToStock()
        {
            SpatialNode yard;
            WorldModel w = YardWithDamagedHull(0.5, 500, out yard);   // bill: 400 x 0.5 = 200 Steel
            var log = new List<string>();

            Assert.Equal(0, HullDamage.ServiceRepairs(w, yard, "F", log));
            Assert.Equal(300, yard.Inventory["Comp:Steel"]);
            Assert.Equal(w.UtcNow, yard.DamagedHulls[0].PartsCompleteUtc);

            w.UtcNow = w.UtcNow.Add(HullDamage.RepairTime(0.5)).AddSeconds(-1);
            Assert.Equal(0, HullDamage.ServiceRepairs(w, yard, "F", log));

            w.UtcNow = w.UtcNow.AddSeconds(1);
            Assert.Equal(1, HullDamage.ServiceRepairs(w, yard, "F", log));
            Assert.Empty(yard.DamagedHulls);
            Assert.Equal(1, yard.ShipStock["Gunship"]);
            Assert.Equal(300, yard.Inventory["Comp:Steel"]);
            Assert.Contains(log, l => l.StartsWith("F REPAIR_DONE prefab=Gunship node=YARD integrity=0.50 consumed=Comp:Steel:200",
                                                  StringComparison.Ordinal));
        }

        [Fact]
        public void Repair_WaitsForParts_AndNeverTakesWhatABuildIsOwed()
        {
            SpatialNode yard;
            WorldModel w = YardWithDamagedHull(0.5, 250, out yard);
            var build = new BuildQueueEntry
            {
                Id = "Raid:001", Role = "Raid", PrefabId = "Fighter",
                CreatedUtc = w.UtcNow, ExpiresUtc = w.UtcNow.AddDays(1),
            };
            build.Bom["Comp:Steel"] = 100;
            yard.BuildQueue.Add(build);
            var log = new List<string>();

            HullDamage.ServiceRepairs(w, yard, "F", log);
            Assert.Equal(150, yard.DamagedHulls[0].Collected["Comp:Steel"]);   // 250 less the build's 100
            Assert.Equal(100, yard.Inventory["Comp:Steel"]);
            Assert.Equal(default(DateTime), yard.DamagedHulls[0].PartsCompleteUtc);

            w.UtcNow = w.UtcNow.AddDays(2);
            Assert.Equal(0, HullDamage.ServiceRepairs(w, yard, "F", log));   // still 50 short
        }

        [Fact]
        public void Repair_NeedsAShipyard_AndAnUnpausedOne()
        {
            SpatialNode yard;
            WorldModel w = YardWithDamagedHull(0.5, 500, out yard);
            yard.ShipyardPaused = true;
            Assert.Equal(0, HullDamage.ServiceRepairs(w, yard, "F", new List<string>()));
            Assert.Equal(500, yard.Inventory["Comp:Steel"]);

            yard.ShipyardPaused = false;
            yard.Capabilities.Remove(NodeCapabilityKind.Shipyard);
            Assert.Equal(0, HullDamage.ServiceRepairs(w, yard, "F", new List<string>()));
            Assert.Equal(500, yard.Inventory["Comp:Steel"]);
        }

        [Fact]
        public void ShipyardTick_RunsTheRepairPass()
        {
            SpatialNode yard;
            WorldModel w = YardWithDamagedHull(0.9, 500, out yard);   // bill 40, time 0.4 h
            var st = new FactionState { Tag = "F" };
            var log = new List<string>();
            var shipyard = new ShipyardManager();
            shipyard.Tick(new List<FactionState> { st }, w, w.Tuning, log);
            w.UtcNow = w.UtcNow.AddHours(1);
            shipyard.Tick(new List<FactionState> { st }, w, w.Tuning, log);
            Assert.Empty(yard.DamagedHulls);
            Assert.Contains(log, l => l.StartsWith("F REPAIR_DONE prefab=Gunship", StringComparison.Ordinal));
        }

        // ---- a duel's attrition ------------------------------------------------------------------

        /// <summary>An armed Guard over its own node, and a two-fighter raid arriving at it that
        /// outguns it: the raid breaks through with part of its strength.</summary>
        private static List<FactionState> GuardDuel(WorldModel w)
        {
            SpatialNode node = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "M1");
            node.OwnerFactionTag = "M"; node.HoldStrength = 0.8; node.Radius = 40;
            w.PrefabCatalogue = Catalogue();
            DateTime now = w.UtcNow;
            var defender = new FactionState { Tag = "M" };
            defender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "M", TargetNodeId = "M1", FleetId = "G00001",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5), Resolved = true,
                ShipPrefabId = "Fighter", ConvoySize = 1,
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 100, RangeM = 800 } },
            });
            var raider = new FactionState { Tag = "R" };
            var raid = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "R", TargetTag = "M", TargetNodeId = "M1", FleetId = "R00001",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now, CargoValue = 3000,
                ShipPrefabId = "Gunship", ConvoySize = 2, Squadron = new List<string> { "Gunship", "Gunship" },
            };
            raid.WeaponBands = HullDamage.ScaledBands(raid, w.PrefabCatalogue);
            SpatialNode home = w.Graph.Add(new Vec3(5000, 0, 0), 0.6, "R1");
            home.OwnerFactionTag = "R"; home.HoldStrength = 0.8; home.Radius = 40;
            raid.OriginNodeId = "R1"; raid.ReturnNodeId = "R1";
            raid.RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "R1", Kind = FleetStopKind.Return } };
            raider.PendingFleets.Add(raid);
            return new List<FactionState> { defender, raider };
        }

        private static bool IsIntegrityLine(string l)
        {
            return l.Contains("HULL_DAMAGED") || l.Contains("REPAIR_") || l.Contains("repairing=");
        }

        [Fact]
        public void TheRaidThatBrokeThroughCarriesTheDuelInItsHullsAndBands()
        {
            WorldModel w = TestSupport.World(7);
            List<FactionState> states = GuardDuel(w);
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            string broke = log.Single(l => l.StartsWith("RAID_BREAKS_GUARD", StringComparison.Ordinal));
            double r = double.Parse(broke.Split(' ').Single(t => t.StartsWith("ratio=", StringComparison.Ordinal)).Substring(6),
                                    System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(r > 0 && r < 1, "the duel should leave the raid part of its strength, was " + r);
            double expected = Math.Max(HullDamage.MinSurvivingIntegrity, r);

            // The raid is now on its way home, and the leg carries what the duel did to it.
            PendingFleetAction raid = states[1].PendingFleets.Single(f => f.FleetId == "R00001");
            Assert.Equal(FleetStopKind.Return, raid.CurrentStopKind);
            Assert.Equal(expected, raid.IntegrityOf(0), 2);
            Assert.Equal(expected, raid.IntegrityOf(1), 2);
            Assert.Equal(raid.IntegrityOf(0), raid.IntegrityOf(1));
            Assert.Equal(800 * raid.IntegrityOf(0), raid.WeaponBands.Sum(b => b.Dps), 2);
        }
    }
}
