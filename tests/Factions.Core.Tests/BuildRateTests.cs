using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;
using static Factions.Core.Tests.ArmedYard;

namespace Factions.Core.Tests
{
    /// <summary>FAC-351 slice 1: a build carries an inflow rate, so "will this finish in a time that
    /// matters" becomes answerable.
    ///
    /// <para><c>StalledSeconds</c> resets on every arrival, so a build absorbing a trickle reads
    /// healthy forever. Live 2026-09-18: SPRT's <c>Raid:142</c> sat at 47% of a 229,659-unit
    /// <c>Naginata</c> after three days with <c>stalled=0</c>, holding the (node, role) slot that is
    /// also where hull selection happens.</para></summary>
    public class BuildRateTests
    {
        /// <summary>The two clocks measure different things and must not be the same field: an entry
        /// that keeps absorbing has its stall clock reset to zero on every arrival while its serviced
        /// clock keeps climbing. That divergence is the whole feature.</summary>
        [Fact]
        public void ATricklingBuild_ResetsItsStallClockButAccumulatesServicedTime()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = Yard(out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Fighter" } : new string[0] };

            // Six sweeps, a component arriving on every one.
            for (int i = 0; i < 6; i++)
            {
                mgr.Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());
                BuildQueueEntry e = yard.BuildQueue.Find(x => x.Role == "Raid");
                if (e == null) break;
                world.AddNodeInventory(yard.Id, "Comp:Steel", 5);
                world.UtcNow = world.UtcNow.AddMinutes(3);
            }

            BuildQueueEntry entry = yard.BuildQueue.Find(x => x.Role == "Raid");
            Assert.NotNull(entry);
            Assert.True(entry.ServicedSeconds > 0,
                "serviced time must accumulate; got " + entry.ServicedSeconds);
            Assert.True(entry.ServicedSeconds > entry.StalledSeconds,
                "a build that keeps absorbing resets its stall clock while its serviced clock climbs: "
                + "serviced=" + entry.ServicedSeconds + " stalled=" + entry.StalledSeconds);
        }

        /// <summary>Both supply paths count, because the numerator is <c>Collected</c> itself rather
        /// than the absorb loop's own counter. Components manufactured straight into the entry by
        /// `ProductionManager` are progress, and a rate blind to them would repeat the defect where a
        /// build accrued stall time while visibly filling up.</summary>
        [Fact]
        public void ComponentsWrittenStraightIntoTheEntry_CountTowardTheRate()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = Yard(out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Fighter" } : new string[0] };
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            BuildQueueEntry entry = yard.BuildQueue.Find(x => x.Role == "Raid");
            Assert.NotNull(entry);
            long before = 0;
            foreach (var kv in entry.Collected) before += kv.Value;

            // The ProductionManager path: straight into Collected, never through node inventory.
            entry.Collected["Comp:Steel"] = before + 7;
            world.UtcNow = world.UtcNow.AddMinutes(3);
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            long after = 0;
            foreach (var kv in entry.Collected) after += kv.Value;
            Assert.True(after > before, "the manufactured units are in Collected");
            Assert.True(entry.ServicedSeconds > 0,
                "and the rate's denominator advanced over them; got " + entry.ServicedSeconds);
        }

        /// <summary>The clock survives a restart, or every reload hands a three-day build a fresh
        /// rate and the horizon can never see it.</summary>
        [Fact]
        public void TheServicedClock_RoundTripsThroughPersistence()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = Yard(out yard, out st);
            var mgr = new ShipyardManager { PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Fighter" } : new string[0] };
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());
            BuildQueueEntry entry = yard.BuildQueue.Find(x => x.Role == "Raid");
            Assert.NotNull(entry);
            entry.ServicedSeconds = 12345.0;

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "factions-fac351-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, world.Graph.OwnedBy("F"), FactionStore.DefaultLogCap));
                FactionState loaded = FactionStore.ApplyBrainDoc(store.LoadBrain("F"), world.Graph);
                SpatialNode n = null;
                foreach (SpatialNode candidate in world.Graph.OwnedBy("F")) { n = candidate; break; }
                BuildQueueEntry back = n.BuildQueue.Find(x => x.Role == "Raid");
                Assert.NotNull(back);
                Assert.Equal(12345.0, back.ServicedSeconds, 3);
                Assert.NotNull(loaded);
            }
            finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); }
        }

        /// <summary>The rule. An entry holding a hull it cannot finish is replaced when a fresh
        /// selection's WHOLE BOM is smaller than what is left of the incumbent — strictly sooner, no
        /// margin, nothing to calibrate.
        ///
        /// <para>Live 2026-09-18: SPRT's <c>Raid:142</c> had 120,638 units left of a
        /// <c>Naginata</c>. Any hull in the pool costing less than that finishes first, at any yard
        /// speed — which is why the yard's throughput does not appear in the comparison.</para></summary>
        [Fact]
        public void AnEntryThatCannotFinish_IsReplacedByOneThatCan()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = ReselectYard(out yard, out st);
            var mgr = new ShipyardManager
            {
                PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Swarmer", "Flagship" } : new string[0],
            };
            // An incumbent Flagship, barely started.
            var incumbent = new BuildQueueEntry
            {
                Id = "Raid:001", Role = "Raid", PrefabId = "Flagship",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
            };
            incumbent.Bom["Comp:Steel"] = 4000;
            incumbent.Collected["Comp:Steel"] = 100;
            yard.BuildQueue.Add(incumbent);

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            BuildQueueEntry now = yard.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(now);
            Assert.Equal("Swarmer", now.PrefabId);
            Assert.Contains(log, l => l.Contains("BUILD_RESELECTED") && l.Contains("from=Flagship"));
            // Nothing was thrown away: the collected units went back to the node this build stands on.
            Assert.True(world.GetNodeInventory(yard.Id, "Comp:Steel") >= 100
                        || (now.Collected.ContainsKey("Comp:Steel") && now.Collected["Comp:Steel"] > 0),
                "the 100 collected units are either back in inventory or already re-absorbed");
        }

        /// <summary>...and an entry that IS the fastest thing available keeps its slot. This is the
        /// arm that stops the rule becoming "rebuild everything every pass": the comparison is an
        /// equality the pass after a swap, so it declines.</summary>
        [Fact]
        public void AnEntryAlreadyHoldingTheFastestHull_IsLeftAlone()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = ReselectYard(out yard, out st);
            var mgr = new ShipyardManager
            {
                PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Swarmer", "Flagship" } : new string[0],
            };
            var incumbent = new BuildQueueEntry
            {
                Id = "Raid:001", Role = "Raid", PrefabId = "Swarmer",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
            };
            incumbent.Bom["Comp:Steel"] = 200;
            yard.BuildQueue.Add(incumbent);
            string idBefore = incumbent.Id;

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            BuildQueueEntry now = yard.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(now);
            Assert.Equal(idBefore, now.Id);
            Assert.DoesNotContain(log, l => l.Contains("BUILD_RESELECTED"));
        }

        /// <summary>A shelf build keeps the old contract. What the shop stocks is not a fleet
        /// decision, and a store hull swapped out from under a listing is a different bug.</summary>
        [Fact]
        public void AShelfBuild_IsNeverReselected()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = ReselectYard(out yard, out st);
            var mgr = new ShipyardManager
            {
                PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Swarmer", "Flagship" } : new string[0],
            };
            var shelf = new BuildQueueEntry
            {
                Id = "Raid:001", Role = "Raid", PrefabId = "Flagship", DestinedForSale = true,
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
            };
            shelf.Bom["Comp:Steel"] = 4000;
            yard.BuildQueue.Add(shelf);

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            BuildQueueEntry now = yard.BuildQueue.Find(e => e.Role == "Raid" && e.DestinedForSale);
            Assert.NotNull(now);
            Assert.Equal("Flagship", now.PrefabId);
        }

        /// <summary>A build a player or a doctrine line named by hull is never reselected: the hull
        /// is the instruction. The Raid demand adopts it (its hull is armed) and feeds it, and the
        /// cheaper candidate that would replace an automatic Raid entry is not built beside it.</summary>
        [Fact]
        public void ABuildNamedByHull_IsNeverReselected()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = ReselectYard(out yard, out st);
            var mgr = new ShipyardManager
            {
                PrefabListPicker = (r, t) => r == "Raid" ? new[] { "Swarmer", "Flagship" } : new string[0],
            };
            var named = new BuildQueueEntry
            {
                Id = "B:001", PrefabId = "Flagship",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
            };
            named.Bom["Comp:Steel"] = 4000;
            named.Collected["Comp:Steel"] = 100;
            yard.BuildQueue.Add(named);

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("role=Raid"));   // the demand this test needs did reach the loop
            BuildQueueEntry now = Assert.Single(yard.BuildQueue);
            Assert.Equal("B:001", now.Id);
            Assert.Equal("Flagship", now.PrefabId);
            Assert.DoesNotContain(log, l => l.Contains("BUILD_RESELECTED"));
        }

        /// <summary>A yard whose Raid demand carries an unreachable floor, and a pool where the
        /// cheapest hull is not the strongest — the live shape.</summary>
        private static WorldModel ReselectYard(out SpatialNode yard, out FactionState st)
        {
            var cat = new ArmedCatalogue().Add("Swarmer", 900, "Small").Add("Flagship", 1000, "Large");
            var recipes = new CostRecipes();
            recipes.CostByPrefab["Swarmer"] = 200;
            recipes.CostByPrefab["Flagship"] = 4000;
            WorldModel world = ArmedYard.Yard(cat, recipes, out yard);
            st = new FactionState { Tag = "F", TerrorStandingDps = 500000 };
            return world;
        }

        private static WorldModel Yard(out SpatialNode yard, out FactionState st)
        {
            var cat = new ArmedCatalogue().Add("Fighter", 1000, "Small");
            var recipes = new CostRecipes();
            recipes.CostByPrefab["Fighter"] = 400;      // big enough that it cannot finish in the window
            WorldModel world = ArmedYard.Yard(cat, recipes, out yard);
            st = new FactionState { Tag = "F", TerrorStandingDps = 500000 };
            return world;
        }
    }
}
