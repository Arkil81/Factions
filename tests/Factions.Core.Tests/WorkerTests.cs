using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Food slice 2 (docs/food-and-population.md, FAC-493): workers live in a Habitat's beds, eat and
    /// breathe out of their node's stock, leave when it cannot keep them, and are bought out of
    /// Tellus's civilian pool.
    /// </summary>
    public sealed class WorkerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        private sealed class Menu : INutritionCatalogue
        {
            public double NutritionPerUnit(string invKey)
            {
                switch (invKey)
                {
                    case "Fruit": return 0.06;
                    case "MealPack_VeggieBurger": return 0.46;
                    case "MealPack_SteakDinner": return 1.0;
                    default: return 0;   // Grain: not a consumable in SE
                }
            }
        }

        private sealed class Bodies : IPlanetCatalogue
        {
            public readonly List<PlanetRecord> Records = new List<PlanetRecord>();
            public IEnumerable<PlanetRecord> Planets { get { return Records; } }
            public double GravityAt(Vec3 posKm) { return 1.0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static SpatialNode Habitat(int level, double workers)
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            n.OwnerFactionTag = "EXP";
            if (level > 0) n.Capabilities[NodeCapabilityKind.Habitat] = level;
            n.Workers = workers;
            n.WorkersAtUtc = workers > 0 ? T0 : default(DateTime);
            return n;
        }

        private static FactionTuning Tuning()
        {
            return new FactionTuning { HabitatBedsPerLevel = 20 };
        }

        // ---- upkeep -----------------------------------------------------------------------------

        [Fact]
        public void TenWorkersFedForAnHour_EatTheirSeNutrition_AndBreatheTheirOxygen_AndNobodyLeaves()
        {
            SpatialNode n = Habitat(1, 10);
            n.Inventory["MealPack_SteakDinner"] = 100;
            n.Inventory[WorkerUpkeep.OxygenKey] = 10000;

            WorkerUpkeepResult r = WorkerUpkeep.Run(n, Tuning(), new Menu(), null, T0.AddHours(1));

            // 10 workers × 2.23 nutrition: 23 whole dinners, 0.7 of the last carried forward.
            Assert.Equal(22.3, r.NutritionOwed, 6);
            Assert.Equal(77, n.Inventory["MealPack_SteakDinner"]);
            Assert.Equal(0.7, n.WorkerFoodBank, 6);
            Assert.Equal(2268, r.OxygenDrawn);
            Assert.Equal(10000 - 2268, n.Inventory[WorkerUpkeep.OxygenKey]);
            Assert.Equal(1.0, r.FedShare, 6);
            Assert.Equal(0.0, r.Left);
            Assert.Equal(10, WorkerUpkeep.Headcount(n));
        }

        [Fact]
        public void RawCropsAreEatenBeforeMeals_AndGrainFeedsNobody()
        {
            SpatialNode n = Habitat(1, 1);
            n.Inventory["Grain"] = 500;
            n.Inventory["Fruit"] = 10;
            n.Inventory["MealPack_VeggieBurger"] = 10;
            n.Inventory[WorkerUpkeep.OxygenKey] = 1000;

            WorkerUpkeep.Run(n, Tuning(), new Menu(), null, T0.AddHours(1));

            // 2.23 owed: ten fruit give 0.6, then four burgers the remaining 1.63.
            Assert.Equal(0, n.Inventory["Fruit"]);
            Assert.Equal(6, n.Inventory["MealPack_VeggieBurger"]);
            Assert.Equal(500, n.Inventory["Grain"]);
        }

        [Fact]
        public void WithNoAir_TheUnfedShareLeavesAtTheTuningRate_NotAllAtOnce()
        {
            SpatialNode n = Habitat(1, 20);
            n.Inventory["MealPack_SteakDinner"] = 1000;

            WorkerUpkeepResult r = WorkerUpkeep.Run(n, Tuning(), new Menu(), null, T0.AddHours(1));

            Assert.Equal(0.0, r.FedShare, 6);
            Assert.Equal(5.0, r.Left, 6);    // 25% of 20 in one hour
            Assert.Equal(15.0, n.Workers, 6);
        }

        [Fact]
        public void HalfTheFood_LeavesHalfTheWorkersUnfed_AndOnlyTheyLeave()
        {
            SpatialNode n = Habitat(1, 20);
            n.Inventory[WorkerUpkeep.OxygenKey] = 100000;
            n.WorkerFoodBank = 22.3;   // half of 20 × 2.23

            WorkerUpkeepResult r = WorkerUpkeep.Run(n, Tuning(), new Menu(), null, T0.AddHours(1));

            Assert.Equal(0.5, r.FedShare, 6);
            Assert.Equal(2.5, r.Left, 6);    // 25% of the 10 unfed
        }

        [Fact]
        public void ANodeWithNothing_EmptiesCompletely_DecayGoesToZero()
        {
            SpatialNode n = Habitat(1, 3);
            DateTime t = T0;
            for (int i = 0; i < 12 && n.Workers > 0; i++)
            {
                t = t.AddHours(1);
                WorkerUpkeep.Run(n, Tuning(), new Menu(), null, t);
            }

            Assert.Equal(0.0, n.Workers);
            Assert.Equal(0, WorkerUpkeep.Headcount(n));
            Assert.Equal(default(DateTime), n.WorkersAtUtc);
        }

        [Fact]
        public void WorkersOverTheBeds_LeaveAtOnce()
        {
            SpatialNode n = Habitat(1, 25);
            n.Inventory["MealPack_SteakDinner"] = 1000;
            n.Inventory[WorkerUpkeep.OxygenKey] = 100000;

            WorkerUpkeepResult r = WorkerUpkeep.Run(n, Tuning(), new Menu(), null, T0.AddMinutes(3));

            Assert.Equal(5.0, r.Left, 6);
            Assert.Equal(20, WorkerUpkeep.Headcount(n));
        }

        [Fact]
        public void ADowntimeIsNotBilled_APassChargesAtMostAnHour()
        {
            SpatialNode n = Habitat(1, 1);
            n.Inventory["MealPack_SteakDinner"] = 100;
            n.Inventory[WorkerUpkeep.OxygenKey] = 100000;

            WorkerUpkeepResult r = WorkerUpkeep.Run(n, Tuning(), new Menu(), null, T0.AddHours(10));

            Assert.Equal(WorkerUpkeep.MaxHoursPerPass, r.Hours, 6);
            Assert.Equal(97, n.Inventory["MealPack_SteakDinner"]);
        }

        [Fact]
        public void HoursSupplied_IsTheShorterOfFoodAndAir()
        {
            SpatialNode n = Habitat(1, 10);
            n.Inventory["MealPack_SteakDinner"] = 223;           // 10 hours of food
            n.Inventory[WorkerUpkeep.OxygenKey] = 4536;          // 2 hours of air

            Assert.Equal(2.0, WorkerUpkeep.HoursSupplied(n, Tuning(), new Menu(), null), 6);
        }

        // ---- breathable air ---------------------------------------------------------------------

        /// <summary>A Tellus-shaped body: 60 km terrain, 12 km of breathable atmosphere above it, the
        /// node at (0,0,0) on its surface.</summary>
        private static Bodies Tellus(double breathableOxygen)
        {
            var bodies = new Bodies();
            bodies.Records.Add(new PlanetRecord
            {
                Id = "T", Name = "Tellus", Center = new Vec3(0, 0, -60), SurfaceRadius = 60,
                AtmosphereRadius = 72, HasAtmosphere = true, BreathableOxygen = breathableOxygen,
            });
            return bodies;
        }

        [Fact]
        public void WorkersOnABreathableSurface_DrawNoOxygen_AndAreKeptWithNoneInStock()
        {
            SpatialNode n = Habitat(1, 10);
            n.Inventory["MealPack_SteakDinner"] = 100;

            WorkerUpkeepResult r = WorkerUpkeep.Run(n, Tuning(), new Menu(), Tellus(1.0), T0.AddHours(1));

            Assert.Equal(0, r.OxygenDrawn);
            Assert.Equal(1.0, r.FedShare, 6);
            Assert.Equal(10, n.Workers, 6);
            Assert.True(double.IsPositiveInfinity(WorkerUpkeep.AirHours(n, Tuning(), Tellus(1.0))));
        }

        [Fact]
        public void TheSameNodeInThinAir_OrUnderAnUnbreathableAtmosphere_BreathesItsStock()
        {
            SpatialNode thin = Habitat(1, 10);
            thin.Position = new Vec3(0, 0, 8);                    // 8 of 12 km up: air 0.33, oxygen below 0.5
            thin.Inventory["MealPack_SteakDinner"] = 100;
            thin.Inventory[WorkerUpkeep.OxygenKey] = 10000;
            WorkerUpkeepResult r = WorkerUpkeep.Run(thin, Tuning(), new Menu(), Tellus(1.0), T0.AddHours(1));
            Assert.Equal(2268, r.OxygenDrawn);

            SpatialNode agni = Habitat(1, 10);
            agni.Inventory["MealPack_SteakDinner"] = 100;
            agni.Inventory[WorkerUpkeep.OxygenKey] = 10000;
            r = WorkerUpkeep.Run(agni, Tuning(), new Menu(), Tellus(0.0), T0.AddHours(1));
            Assert.Equal(2268, r.OxygenDrawn);
        }

        [Fact]
        public void ABreathableNodeWithNoFood_ReadsZeroFoodHours_AndNoAirOwed()
        {
            // The live N099 shape: oxygen in stock, nothing edible, on Tellus. The window must say the
            // food ran out, not the air.
            SpatialNode n = Habitat(1, 4);
            n.Inventory[WorkerUpkeep.OxygenKey] = 3778497;

            Assert.Equal(0.0, WorkerUpkeep.FoodHours(n, Tuning(), new Menu()), 6);
            Assert.True(double.IsPositiveInfinity(WorkerUpkeep.AirHours(n, Tuning(), Tellus(1.0))));
        }

        // ---- recruitment ------------------------------------------------------------------------

        private static WorldModel World(SpatialNode template, bool tellus, long treasury)
        {
            var world = new WorldModel(new SeededRandom(7)) { Tuning = Tuning() };
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 0.5, "N1");
            n.OwnerFactionTag = "EXP";
            n.PlanetId = "83859379220048820";
            foreach (var kv in template.Capabilities) n.Capabilities[kv.Key] = kv.Value;
            n.Workers = template.Workers;
            var bodies = new Bodies();
            bodies.Records.Add(new PlanetRecord { Id = "83859379220048820", Name = "Tellus", Recruits = tellus });
            world.Planets = bodies;
            world.SetTreasury("EXP", treasury);
            return world;
        }

        [Fact]
        public void Recruiting_IsTrimmedToTheBedsLeft_AndToWhatTheTreasuryBuys()
        {
            int granted; long cost, have;
            WorldModel rich = World(Habitat(1, 15), true, 10000000);
            Assert.Equal(WorkerRecruitResult.Ok,
                WorkerRecruitValidator.Validate(rich, rich.Tuning, "EXP", "N1", 50, out granted, out cost, out have));
            Assert.Equal(5, granted);
            Assert.Equal(500000, cost);

            WorldModel poor = World(Habitat(1, 0), true, 250000);
            Assert.Equal(WorkerRecruitResult.Ok,
                WorkerRecruitValidator.Validate(poor, poor.Tuning, "EXP", "N1", 20, out granted, out cost, out have));
            Assert.Equal(2, granted);
        }

        [Fact]
        public void Recruiting_IsRefused_OffTellus_WithoutAHabitat_WhenFull_AndWhenBroke()
        {
            int granted; long cost, have;
            WorldModel offTellus = World(Habitat(1, 0), false, 10000000);
            Assert.Equal(WorkerRecruitResult.NoCivilianPool,
                WorkerRecruitValidator.Validate(offTellus, offTellus.Tuning, "EXP", "N1", 1, out granted, out cost, out have));

            WorldModel bare = World(Habitat(0, 0), true, 10000000);
            Assert.Equal(WorkerRecruitResult.NoHabitat,
                WorkerRecruitValidator.Validate(bare, bare.Tuning, "EXP", "N1", 1, out granted, out cost, out have));

            WorldModel full = World(Habitat(1, 20), true, 10000000);
            Assert.Equal(WorkerRecruitResult.NoRoom,
                WorkerRecruitValidator.Validate(full, full.Tuning, "EXP", "N1", 1, out granted, out cost, out have));

            WorldModel broke = World(Habitat(1, 0), true, 99999);
            Assert.Equal(WorkerRecruitResult.InsufficientTreasury,
                WorkerRecruitValidator.Validate(broke, broke.Tuning, "EXP", "N1", 1, out granted, out cost, out have));
            Assert.Equal(0, granted);

            Assert.Equal(WorkerRecruitResult.NotOwned,
                WorkerRecruitValidator.Validate(full, full.Tuning, "UNGD", "N1", 1, out granted, out cost, out have));
        }

        [Fact]
        public void ARecruit_StartsTheClockOnArrival_SoTheFirstPassOwesNothingForEarlier()
        {
            SpatialNode n = Habitat(1, 0);
            n.WorkersAtUtc = T0.AddDays(-3);
            WorkerRecruitValidator.Admit(n, 4, T0);

            Assert.Equal(4.0, n.Workers);
            Assert.Equal(T0, n.WorkersAtUtc);
        }

        // ---- the capability, the planet flag, persistence and the wire -----------------------------

        [Fact]
        public void Habitat_IsAPlayerInstallOnly_AndLevelsUpToItsOwnCeiling()
        {
            Assert.DoesNotContain(NodeCapabilityKind.Habitat, FactionTuning.AllCapabilities);
            var t = new FactionTuning { MaxHabitatUpgrades = 7 };
            Assert.Equal(7, t.MaxLevelFor(NodeCapabilityKind.Habitat));
            Assert.Equal(40, WorkerUpkeep.Capacity(Habitat(2, 0), Tuning()));
        }

        [Fact]
        public void TheRecipeFlag_IsStampedOntoTheBody_ByGenerator()
        {
            var recipe = new WorldRecipe
            {
                Planets = new List<RecipePlanet>
                {
                    new RecipePlanet { Name = "Tellus", Definition = "Tellus", Recruits = true },
                    new RecipePlanet { Name = "Agni", Definition = "Agni" },
                },
            };
            SurfaceNodePolicy policy;
            string why;
            Assert.True(SurfaceNodePolicy.TryBuild(recipe, out policy, out why), why);

            var tellus = new PlanetRecord();
            policy.ApplyTo(tellus, "Tellus", true);
            var agni = new PlanetRecord();
            policy.ApplyTo(agni, "Agni", true);

            Assert.True(tellus.Recruits);
            Assert.False(agni.Recruits);
        }

        [Fact]
        public void Workers_RoundTripThroughTheBrainFile_AndANodeWithoutThemWritesNothing()
        {
            var graph = new SpatialGraph();
            SpatialNode a = graph.Add(new Vec3(1, 2, 3), 0.5, "N1");
            a.OwnerFactionTag = "EXP";
            a.Capabilities[NodeCapabilityKind.Habitat] = 1;
            a.Workers = 12.5;
            a.WorkersAtUtc = T0;
            a.WorkerFoodBank = 0.4;
            SpatialNode b = graph.Add(new Vec3(4, 5, 6), 0.5, "N2");
            b.OwnerFactionTag = "EXP";

            var st = new FactionState { Tag = "EXP", FactionId = 7, PersonalityId = "settler", RngSeed = 1 };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("EXP"), FactionStore.DefaultLogCap));
                string json = File.ReadAllText(Directory.GetFiles(dir, "EXP*.json", SearchOption.AllDirectories)[0]);
                Assert.Equal(1, CountOf(json, "\"Workers\""));

                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("EXP"), reloaded);
                Assert.Equal(12.5, reloaded.Nodes["N1"].Workers);
                Assert.Equal(T0, reloaded.Nodes["N1"].WorkersAtUtc);
                Assert.Equal(0.4, reloaded.Nodes["N1"].WorkerFoodBank, 6);
                Assert.Equal(0.0, reloaded.Nodes["N2"].Workers);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        private static int CountOf(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        [Fact]
        public void TheWorkerFields_RoundTripOnTheWire_AndTheFieldsAfterThemDoNotMove()
        {
            var detail = new WireNodeDetail
            {
                Exists = true,
                Id = "N1",
                DisplayName = "Home",
                OwnerTag = "EXP",
                Owned = true,
                HasStation = true,
                HasStore = true,
                Workers = 12,
                WorkerBeds = 20,
                RecruitPrice = 100000,
                WorkerFoodHours = 3.5,
                WorkerAirHours = -1.0,
                Capabilities = new[] { new WireAssetCapability { Cap = "Habitat", Level = 1 } },
            };
            WireNodeDetail read;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(detail), out read));

            Assert.True(read.HasStore);
            Assert.Equal(12, read.Workers);
            Assert.Equal(20, read.WorkerBeds);
            Assert.Equal(100000, read.RecruitPrice);
            Assert.Equal(3.5, read.WorkerFoodHours);
            Assert.Equal(-1.0, read.WorkerAirHours);
            Assert.Single(read.Capabilities);
            Assert.Equal("Habitat", read.Capabilities[0].Cap);
        }

        [Fact]
        public void TheRecruitCommand_CarriesItsCount()
        {
            byte[] data = FactionsWire.WriteNodeCommandRequest("N1", WireNodeCommand.Recruit, "", 5);
            string node, target; WireNodeCommand command; int value;
            Assert.True(FactionsWire.TryReadNodeCommandRequest(data, out node, out command, out target, out value));
            Assert.Equal(WireNodeCommand.Recruit, command);
            Assert.Equal(5, value);
        }
    }
}
