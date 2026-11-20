using System.Collections.Generic;
using Factions.Core;
using Xunit;
using static Factions.Core.Tests.ArmedYard;

namespace Factions.Core.Tests
{
    /// <summary>FAC-479. A faction whose livery is mandatory builds its own hull for every role its
    /// livery has one for, and a generic hull only for a role it has none for.
    ///
    /// <para>Live 2026-10-13: FCTM's whole build queue was five <c>Alsvidr</c> (Raid), five
    /// <c>DS_MiniMuleDrone</c> (Trade) and five <c>Badger</c> (Mine), with seven Factorum hulls in
    /// the catalogue carrying Raid and six carrying Trade. Its own hulls cost 1.5-2 bn against
    /// 0.3 bn and 2 M, and FAC-237 lets a faction's own hull win only a cost tie.</para></summary>
    public class MandatoryLiveryTests
    {
        // ---- the rule --------------------------------------------------------------------------

        [Fact]
        public void TheFactorumPersonalityHasAMandatoryLivery_AndNoOtherDoes()
        {
            Assert.True(PrefabFactionHint.LiveryIsMandatory("factorum"));
            Assert.True(PrefabFactionHint.LiveryIsMandatory("Factorum"));
            Assert.False(PrefabFactionHint.LiveryIsMandatory("raider"));
            Assert.False(PrefabFactionHint.LiveryIsMandatory("trader"));
            Assert.False(PrefabFactionHint.LiveryIsMandatory(null));
        }

        [Fact]
        public void OwnLivery_KeepsTheHintedHullsInPoolOrder_AndDropsTheUniversalOnes()
        {
            var cat = new ArmedCatalogue().Add("Own_B", 1, "Large").Add("Own_A", 1, "Large").Add("Generic", 1, "Small")
                .Livery("Own_B", "Factorum").Livery("Own_A", "Factorum");

            Assert.Equal(new[] { "Own_B", "Own_A" }, PrefabRolePool.OwnLivery(new[] { "Own_B", "Generic", "Own_A" }, cat).ToArray());
            Assert.Empty(PrefabRolePool.OwnLivery(new[] { "Generic" }, cat));
            Assert.False(PrefabRolePool.IsOwnLivery(cat, null));
            Assert.False(PrefabRolePool.IsOwnLivery(cat, "Generic"));
        }

        // ---- selection -------------------------------------------------------------------------

        /// <summary>THE DEFECT. The own hull is a thousand times dearer and carries no cargo, the
        /// generic one is cheap and does: under cost-first ranking the generic hull wins, and under
        /// the cargo floor the universal pool rescues the build away from the livery.</summary>
        [Fact]
        public void AMandatoryLiveryFaction_BuildsItsOwnHull_OverACheaperGenericOne()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = LiveryYard("factorum", out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Warship", "Alsvidr" } : new string[0] };

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            BuildQueueEntry e = yard.BuildQueue.Find(x => x.Role == "Raid");
            Assert.NotNull(e);
            Assert.Equal("Warship", e.PrefabId);
            Assert.DoesNotContain(log, l => l.Contains("BOM_ZONE_UNIVERSAL_FALLBACK"));
        }

        /// <summary>The paired direction: the same pool for any other faction is FAC-237's union,
        /// and the cheaper generic hull still wins. Without this the test above passes under a rule
        /// that re-liveries everyone.</summary>
        [Fact]
        public void AnyOtherFaction_StillTakesTheCheaperGenericHull()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = LiveryYard("trader", out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Warship", "Alsvidr" } : new string[0] };

            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            BuildQueueEntry e = yard.BuildQueue.Find(x => x.Role == "Raid");
            Assert.NotNull(e);
            Assert.Equal("Alsvidr", e.PrefabId);
        }

        /// <summary>A role the livery has no hull for is built generic: the Factorum mines in a
        /// generic hull because it has no drill of its own.</summary>
        [Fact]
        public void ARoleTheLiveryHasNoHullFor_IsBuiltGeneric()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = LiveryYard("factorum", out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Alsvidr" } : new string[0] };

            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            BuildQueueEntry e = yard.BuildQueue.Find(x => x.Role == "Raid");
            Assert.NotNull(e);
            Assert.Equal("Alsvidr", e.PrefabId);
        }

        /// <summary>The universal pool rescues a build whose cargo floor no hull of the faction's
        /// own clears. For a mandatory-livery faction it must not: its own hull can fly the leg, so
        /// the floor is clamped to the biggest of its own instead. The paired faction is rescued.</summary>
        [Theory]
        [InlineData("factorum", "SmallFactorum")]
        [InlineData("trader", "GenericHauler")]
        public void ACargoFloorTheLiveryCannotClear_IsClampedWithinIt_NotRescuedByTheUniversalPool(
            string personality, string expected)
        {
            var cat = new ArmedCatalogue()
                .Add("SmallFactorum", 0, "Large", cargo: 100)
                .Add("GenericHauler", 0, "Large", cargo: 1000)
                .Livery("SmallFactorum", "Factorum");
            cat.Pools["Trade"] = new List<string> { "GenericHauler" };   // the universal pool
            var recipes = new CostRecipes();
            recipes.CostByPrefab["SmallFactorum"] = 500;
            recipes.CostByPrefab["GenericHauler"] = 50;
            SpatialNode yard;
            WorldModel world = ArmedYard.Yard(cat, recipes, out yard);
            var st = new FactionState { Tag = "F", PersonalityId = personality };
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = yard.Id, MinCargoLiters = 500,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            var mgr = new ShipyardManager
            {
                PrefabListPicker = (r, t) => r == "Trade" ? new[] { "SmallFactorum", "GenericHauler" } : new string[0],
            };

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            BuildQueueEntry e = yard.BuildQueue.Find(x => x.Role == "Trade");
            Assert.NotNull(e);
            Assert.Equal(expected, e.PrefabId);
        }

        // ---- an incumbent generic build ----------------------------------------------------------

        /// <summary>The live state: the yard already holds a generic build. It is replaced by the
        /// faction's own hull, however much larger, and what it had collected goes back to the node.</summary>
        [Fact]
        public void AGenericIncumbent_IsReplacedByTheLivery_AndItsComponentsReturnToTheNode()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = LiveryYard("factorum", out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Warship", "Alsvidr" } : new string[0] };
            var incumbent = new BuildQueueEntry
            {
                Id = "Raid:001", Role = "Raid", PrefabId = "Alsvidr",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
            };
            incumbent.Bom["Comp:Steel"] = 300;
            incumbent.Collected["Comp:Steel"] = 120;
            yard.BuildQueue.Add(incumbent);

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            BuildQueueEntry now = yard.BuildQueue.Find(x => x.Role == "Raid");
            Assert.NotNull(now);
            Assert.Equal("Warship", now.PrefabId);
            Assert.Contains(log, l => l.Contains("BUILD_RESELECTED") && l.Contains("from=Alsvidr")
                                      && l.Contains("to=Warship") && l.Contains("reason=livery"));
            long collectedNow;
            now.Collected.TryGetValue("Comp:Steel", out collectedNow);
            Assert.Equal(120, world.GetNodeInventory(yard.Id, "Comp:Steel") + collectedNow);
        }

        /// <summary>...and the replacement is never reconsidered on that ground again, so the swap
        /// happens once.</summary>
        [Fact]
        public void TheLiveryReplacement_IsNotSwappedAgain()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = LiveryYard("factorum", out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Warship", "Alsvidr" } : new string[0] };
            var incumbent = new BuildQueueEntry
            {
                Id = "Raid:001", Role = "Raid", PrefabId = "Alsvidr",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
            };
            incumbent.Bom["Comp:Steel"] = 300;
            yard.BuildQueue.Add(incumbent);

            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());
            string idAfterSwap = yard.BuildQueue.Find(x => x.Role == "Raid").Id;
            world.UtcNow = world.UtcNow.AddMinutes(3);
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            BuildQueueEntry now = yard.BuildQueue.Find(x => x.Role == "Raid");
            Assert.Equal(idAfterSwap, now.Id);
            Assert.Equal("Warship", now.PrefabId);
            Assert.DoesNotContain(log, l => l.Contains("BUILD_RESELECTED"));
        }

        /// <summary>A generic incumbent for a role the livery has no hull for is left alone: there
        /// is nothing of its own to swap to.</summary>
        [Fact]
        public void AGenericIncumbent_ForARoleWithNoLiveryHull_IsKept()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = LiveryYard("factorum", out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Alsvidr" } : new string[0] };
            var incumbent = new BuildQueueEntry
            {
                Id = "Raid:001", Role = "Raid", PrefabId = "Alsvidr",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
            };
            incumbent.Bom["Comp:Steel"] = 300;
            yard.BuildQueue.Add(incumbent);

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            BuildQueueEntry now = yard.BuildQueue.Find(x => x.Role == "Raid");
            Assert.Equal("Raid:001", now.Id);
            Assert.DoesNotContain(log, l => l.Contains("BUILD_RESELECTED"));
        }

        // ---- a build no demand reaches ------------------------------------------------------------

        /// <summary>Live 2026-10-13, after v0.523.0: the <c>Alsvidr</c> Raid builds at four FCTM
        /// yards stood, because the Raid demand sits at one node per pass and only a demand reached
        /// the swap. A build no demand reaches is swapped too, against the yard itself. Guard here,
        /// because nothing in this fixture posts a Guard demand.</summary>
        [Theory]
        [InlineData("factorum", "Warship")]
        [InlineData("trader", "Alsvidr")]
        public void AGenericBuildNoDemandReaches_IsSwappedForTheLivery_OnlyWhereTheLiveryIsMandatory(
            string personality, string expected)
        {
            SpatialNode yard; FactionState st;
            WorldModel world = LiveryYard(personality, out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Guard" ? new[] { "Warship", "Alsvidr" } : new string[0] };
            var incumbent = new BuildQueueEntry
            {
                Id = "Guard:001", Role = "Guard", PrefabId = "Alsvidr",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
            };
            incumbent.Bom["Comp:Steel"] = 300;
            incumbent.Collected["Comp:Steel"] = 120;
            yard.BuildQueue.Add(incumbent);

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            BuildQueueEntry now = Assert.Single(yard.BuildQueue, x => x.Role == "Guard");
            Assert.Equal(expected, now.PrefabId);
            if (expected == "Warship")
                Assert.Contains(log, l => l.Contains("BUILD_RESELECTED") && l.Contains("from=Alsvidr")
                                          && l.Contains("reason=livery demand=none"));
            else
                Assert.DoesNotContain(log, l => l.Contains("BUILD_RESELECTED"));
            long collectedNow;
            now.Collected.TryGetValue("Comp:Steel", out collectedNow);
            Assert.Equal(120, world.GetNodeInventory(yard.Id, "Comp:Steel") + collectedNow);
        }

        /// <summary>A build no demand reaches, for a role the livery has no hull for, is kept.</summary>
        [Fact]
        public void AGenericBuildNoDemandReaches_ForARoleWithNoLiveryHull_IsKept()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = LiveryYard("factorum", out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Guard" ? new[] { "Alsvidr" } : new string[0] };
            var incumbent = new BuildQueueEntry
            {
                Id = "Guard:001", Role = "Guard", PrefabId = "Alsvidr",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
            };
            incumbent.Bom["Comp:Steel"] = 300;
            yard.BuildQueue.Add(incumbent);

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Equal("Guard:001", Assert.Single(yard.BuildQueue, x => x.Role == "Guard").Id);
            Assert.DoesNotContain(log, l => l.Contains("BUILD_RESELECTED"));
        }

        /// <summary>The live catalogue's shape: a Factorum <c>Warship</c> with real guns and no
        /// hold, against a cheap generic <c>Alsvidr</c> with a hold. A personality-tagged faction
        /// under an unreachable Dps floor, so the Raid demand reaches the shipyard.</summary>
        private static WorldModel LiveryYard(string personality, out SpatialNode yard, out FactionState st)
        {
            var cat = new ArmedCatalogue()
                .Add("Warship", 2000, "Large", cargo: 0)
                .Add("Alsvidr", 1000, "Large", cargo: 296875)
                .Livery("Warship", "Factorum");
            var recipes = new CostRecipes();
            recipes.CostByPrefab["Warship"] = 6000;
            recipes.CostByPrefab["Alsvidr"] = 300;
            WorldModel world = ArmedYard.Yard(cat, recipes, out yard);
            st = new FactionState { Tag = "F", PersonalityId = personality, TerrorStandingDps = 500000 };
            return world;
        }
    }
}
