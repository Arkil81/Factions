using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Slice 0 of docs/fleet-fitness-plan.md. The two properties worth protecting are that
    /// the requirement is a PERCENTILE (a mean hides the tail a hull has to be bought for) and that
    /// buildability never outranks the requirement (the easiest solution THAT SOLVES THE PROBLEM).</summary>
    public class FleetFitnessTests
    {
        private sealed class Cat : IPrefabCatalogue
        {
            public readonly Dictionary<string, long> Cargo = new Dictionary<string, long>();
            public readonly Dictionary<string, long> Costs = new Dictionary<string, long>();
            public bool HasPrefab(string p) => true;
            public long CargoLiters(string p) { long v; return Cargo.TryGetValue(p ?? "", out v) ? v : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string p) { return new Dictionary<string, long>(); }
            public long Cost(string p) { long v; return Costs.TryGetValue(p ?? "", out v) ? v : 0; }
            public double Dps(string p) => 0;
            public double DryMassKg(string p) => 0;
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string p) => null;
            public string GridSize(string p) => "Small";
            public string FactionHint(string p) => "All";
            public IReadOnlyList<WeaponBand> WeaponBands(string p) => new WeaponBand[0];
            public IReadOnlyList<string> GetNamesForRole(string r, string h) => new string[0];
            public IEnumerable<string> AllPrefabIds => Cargo.Keys;
            public double DrillYieldScore(string p) => 0;
            public IReadOnlyList<string> RolesFor(string p) => new[] { "Trade" };
        }

        [Fact]
        public void Requirement_IsAPercentile_SoTheTailSizesTheHullAndNotTheTypicalJob()
        {
            // 80 jobs of ~1 L and 20 of ~5,000 L. The MEAN is ~1,000 L and the MEDIAN is ~1 L; only a
            // high percentile reaches the jobs a hull actually has to be bought for. Same lesson as
            // FAC-232's window, which read mean fill 0.377 against median 0.050 on one set of convoys.
            var r = new RoleRequirement();
            for (int i = 0; i < 80; i++) r.Observe(1);
            for (int i = 0; i < 20; i++) r.Observe(5000);

            Assert.True(r.SizeAtPercentile(0.5) < 10, "median must reflect the many small jobs");
            Assert.True(r.SizeAtPercentile(0.9) >= 1024,
                "p90 must reach the large-job bucket: got " + r.SizeAtPercentile(0.9));
        }

        [Fact]
        public void Requirement_PercentileMustExceedTheTailFraction_ToSizeForTheTail()
        {
            // The boundary property, found by this suite rather than assumed: with the big jobs at
            // exactly 10% of the sample, p90 lands on the LAST SMALL JOB and sizes for the drone.
            // That is arithmetically right -- 90% of jobs really are small -- and it is the thing to
            // know when reading FleetRequirementPercentile: the knob sizes for the tail only when it
            // is set above the tail's share. Not a defect; a calibration fact with teeth.
            var r = new RoleRequirement();
            for (int i = 0; i < 90; i++) r.Observe(1);
            for (int i = 0; i < 10; i++) r.Observe(5000);

            Assert.True(r.SizeAtPercentile(0.90) < 10, "p90 of a 10% tail is still a small job");
            Assert.True(r.SizeAtPercentile(0.95) >= 1024, "p95 clears it");
        }

        [Fact]
        public void Requirement_ColdRecord_HasNoOpinion()
        {
            // A fresh faction must degrade to today's behaviour, not to "the requirement is zero"
            // (which would make every hull eligible for the wrong reason) and not to a demand for the
            // biggest hull (which would stop it bootstrapping).
            var r = new RoleRequirement();
            Assert.False(r.IsWarm);
            Assert.Equal(0, r.SizeAtPercentile(0.9));

            r.Observe(500);
            Assert.False(r.IsWarm);   // one sample is not evidence
        }

        [Fact]
        public void Choose_TakesTheCheapestHullThatMeetsTheRequirement_NotTheCheapestOverall()
        {
            // The core contract: "the easiest solution that solves the problem". Tiny is cheapest by
            // far and would win any cost-first or blended score; it cannot serve a 1,000 L job.
            var cat = new Cat();
            cat.Cargo["Tiny"]  = 375;    cat.Costs["Tiny"]  = 10;
            cat.Cargo["Right"] = 3375;   cat.Costs["Right"] = 100;
            cat.Cargo["Huge"]  = 375000; cat.Costs["Huge"]  = 9000;

            bool degraded;
            string chosen = FleetFitness.Choose(cat, new[] { "Tiny", "Right", "Huge" }, 1000, out degraded);

            Assert.Equal("Right", chosen);
            Assert.False(degraded);
        }

        [Fact]
        public void Choose_WithNoRequirement_TakesTheCheapest_WhichIsTodaysBehaviour()
        {
            // Converging on the current answer is CORRECT when the jobs are small -- the design adds
            // the reason, not necessarily a different hull. A cold faction must be indistinguishable
            // from today.
            var cat = new Cat();
            cat.Cargo["Tiny"]  = 375;  cat.Costs["Tiny"]  = 10;
            cat.Cargo["Right"] = 3375; cat.Costs["Right"] = 100;

            bool degraded;
            Assert.Equal("Tiny", FleetFitness.Choose(cat, new[] { "Right", "Tiny" }, 0, out degraded));
            Assert.False(degraded);
        }

        [Fact]
        public void Choose_WhenNothingMeetsTheRequirement_TakesTheLargestAndSaysSo()
        {
            // Must never block: a faction that cannot build for the work it has still fields the best
            // it can, and the caller is told the requirement was not met rather than being handed a
            // silent under-sized hull. Note the largest is also the DEAREST here, so this cannot pass
            // by falling back to cost.
            var cat = new Cat();
            cat.Cargo["Tiny"]  = 375;  cat.Costs["Tiny"]  = 10;
            cat.Cargo["Small"] = 1000; cat.Costs["Small"] = 90;

            bool degraded;
            string chosen = FleetFitness.Choose(cat, new[] { "Tiny", "Small" }, 999999, out degraded);

            Assert.Equal("Small", chosen);
            Assert.True(degraded);
        }

        [Fact]
        public void Requirement_Decays_SoItTracksThePresent()
        {
            var r = new RoleRequirement();
            for (int i = 0; i < 50; i++) r.Observe(100000);
            double before = r.SizeAtPercentile(0.9);

            for (int i = 0; i < 40; i++) { r.Decay(0.5); r.Observe(10); }

            Assert.True(r.SizeAtPercentile(0.9) < before,
                "an old large-job era must fade rather than size the fleet forever");
        }

        // ---- FAC-239: the Dps blend is gated on the role, not on the candidate set ---------------

        [Fact]
        public void CombatBuildsStillRankOnPower_WhichIsTheThingThatMustNotBreak()
        {
            // The acceptance FAC-239 names first: gating the blend must not reduce combat-role
            // builds, or it has broken Muster to fix haulage. All four combat kinds, at posture.
            foreach (string role in new[] { "Raid", "Patrol", "Guard", "Retaliate", "Conquer" })
                Assert.True(ShipPool.ShouldRankOnCombatPower(role, 0.5, 3, true),
                    role + " must still rank on weapon power at posture");
        }

        [Fact]
        public void CargoBuildsDoNotRankOnPower_EvenWhenArmedHullsAreInTheSet()
        {
            // The defect. Measured live 2026-09-03: every hull built for a Trade role in the sample
            // carried Trade AND Raid/Patrol/Guard with real Dps (Nagamaki 23,387; Alsvidr 48,428),
            // so hasAnyPower was TRUE and cargo builds were resolved on weapon power. Note the
            // arguments here are exactly the ones that used to select the blend.
            Assert.False(ShipPool.ShouldRankOnCombatPower("Trade", 0.9, 5, true));
            Assert.False(ShipPool.ShouldRankOnCombatPower("Mine", 0.9, 5, true));
        }

        [Fact]
        public void TheOriginalMusterGuardsAreUnchanged()
        {
            // Peacetime, a single candidate, and a set with nothing armed all still fall back to
            // cheapest-first. These three predate FAC-239 and the fix must not disturb them --
            // otherwise a green combat test could be passing for a reason that hides a regression.
            Assert.False(ShipPool.ShouldRankOnCombatPower("Raid", 0.0, 5, true));   // peacetime
            Assert.False(ShipPool.ShouldRankOnCombatPower("Raid", 0.9, 1, true));   // one candidate
            Assert.False(ShipPool.ShouldRankOnCombatPower("Raid", 0.9, 5, false));  // nothing armed
        }

        [Fact]
        public void AShelfBuildNeverRanksOnPower_EvenForARoleThatWantsIt()
        {
            // FAC-060 + FAC-239. MilitaryPosture is a statistic about the BUILDING faction's war
            // footing; a shelf hull is chosen for a buyer, so the seller's posture is the wrong
            // input. Same exclusion FleetFitness.SizingFloor already makes, for the same reason.
            //
            // Every argument here is one that WOULD select the blend -- a combat role, high
            // posture, several candidates, armed hulls present. destinedForSale is the only thing
            // refusing it, so this cannot pass for an unrelated reason.
            foreach (string role in new[] { "Raid", "Patrol", "Guard", "Retaliate", "Conquer" })
                Assert.False(ShipPool.ShouldRankOnCombatPower(role, 0.9, 5, true, destinedForSale: true),
                    "a shelf build for " + role + " must not be resolved on the seller's posture");

            // And the paired direction, or the test above passes for a gate that refuses everything.
            foreach (string role in new[] { "Raid", "Patrol", "Guard", "Retaliate", "Conquer" })
                Assert.True(ShipPool.ShouldRankOnCombatPower(role, 0.9, 5, true, destinedForSale: false),
                    "the same fleet build for " + role + " must still rank on power");
        }

        [Fact]
        public void TheSaleGateIsTestedOnThePredicate_BecauseSelectPrefabBomCannotReachIt()
        {
            // Documents why the test above targets the predicate rather than a built prefab.
            // SelectPrefabBom tests `stockByPrefab != null` FIRST, and a shelf build always passes
            // a non-null map, so it takes the diversification branch and never reaches the blend.
            // An integration test would therefore pass with the gate REMOVED -- a check that cannot
            // fail. The gate states the exclusion as a property of purpose so that branch order is
            // not load-bearing; this asserts the property, which is the only thing observable.
            //
            // If this ever starts disagreeing with a SelectPrefabBom-level test, branch order has
            // changed and the gate has become reachable -- which is the point of having it.
            Assert.False(ShipPool.ShouldRankOnCombatPower("Raid", 0.9, 5, true, destinedForSale: true));
            Assert.True(ShipPool.ShouldRankOnCombatPower("Raid", 0.9, 5, true, destinedForSale: false));
        }

        // ---- slice 1: the requirement raises the build's cargo floor -----------------------------

        private static FactionState WarmFaction(double observedLiters, string role = "Trade")
        {
            var st = new FactionState { Tag = "F" };
            RoleRequirement r = st.RequirementFor(role);
            for (int i = 0; i < 40; i++) r.Observe(observedLiters);
            return st;
        }

        [Fact]
        public void SizingFloor_RaisesToTheRequirement_WhenTheLiveShipmentFloorIsBelowIt()
        {
            // The measured case: across 16 live builds on 2026-09-03 the live per-shipment floor ran
            // 3.2x to 30.3x BELOW the role's p90, in every single one. A hull bought to that floor is
            // sized for one delivery rather than for the work.
            FactionState st = WarmFaction(5000);
            FactionTuning t = TestSupport.Tuning();

            double floor = FleetFitness.SizingFloor(st, "Trade", t, 480);

            Assert.True(floor >= 4096, "expected the p90 bucket edge, got " + floor);
        }

        [Fact]
        public void SizingFloor_NeverLowersTheLiveFloor()
        {
            // The whole change has ONE direction, which is what makes its failure mode watchable: a
            // build can never come out smaller than it does today. Here the live shipment genuinely
            // needs more than history suggests, and that must win.
            FactionState st = WarmFaction(5000);
            FactionTuning t = TestSupport.Tuning();

            Assert.Equal(500000, FleetFitness.SizingFloor(st, "Trade", t, 500000));
        }

        [Fact]
        public void SizingFloor_LeavesNonCargoRolesAlone()
        {
            // Mine and combat have their own requirements (slices 2 and 3) and neither is built. A
            // Trade-shaped floor applied to a Raid build would be sizing it on the wrong evidence.
            FactionState st = WarmFaction(5000);
            FactionTuning t = TestSupport.Tuning();

            Assert.Equal(480, FleetFitness.SizingFloor(st, "Raid", t, 480));
        }

        [Fact]
        public void SizingFloor_ColdFactionAndDisabledSwitchBothKeepTodaysBehaviour()
        {
            FactionTuning t = TestSupport.Tuning();

            // Cold: one observation is not evidence, and a fresh faction must still bootstrap.
            var cold = new FactionState { Tag = "F" };
            cold.RequirementFor("Trade").Observe(5000);
            Assert.Equal(480, FleetFitness.SizingFloor(cold, "Trade", t, 480));

            // And the switch is a real off switch, so this is backable out from tuning.json without
            // a deploy if the sizing turns out wrong.
            FactionState warm = WarmFaction(5000);
            t.FleetRequirementDrivesBuild = false;
            Assert.Equal(480, FleetFitness.SizingFloor(warm, "Trade", t, 480));
        }

        [Fact]
        public void SizingFloor_AStoreBuildIsExcluded_NotGivenItsOwnFitness()
        {
            // FAC-060 retires the StoreSmall/StoreLarge role strings, so role alone will stop
            // discriminating a shelf build from a fleet one. A shelf hull sized against the faction's
            // own haulage p90 is sized by a statistic about work the FACTION does, not about anything
            // a buyer wants -- and it would look entirely plausible, because the build succeeds.
            FactionState st = WarmFaction(5000);
            FactionTuning t = TestSupport.Tuning();

            Assert.True(FleetFitness.SizingFloor(st, "Trade", t, 480) > 480);            // fleet build: raised
            Assert.Equal(480, FleetFitness.SizingFloor(st, "Trade", t, 480, true));      // shelf build: untouched
        }

        [Fact]
        public void Choose_OnlyEverReturnsAMemberOfThePoolItWasGiven()
        {
            // The property the counterfactual's honesty rests on. It reported a DIFFERS for 25 builds
            // in a day because it was handed the raw same-hint candidate list while the real chooser
            // had rescued from the universal "All" pool -- so `implied=` named a hull that could not
            // fly the legs, and every such build read as a disagreement. It is now handed the pool
            // the ranker actually ranked, which makes that structurally impossible; this pins the
            // half of it that IS unit-testable.
            var cat = new Cat();
            cat.Cargo["A"] = 1000; cat.Costs["A"] = 5;
            cat.Cargo["B"] = 9000; cat.Costs["B"] = 50;
            cat.Cargo["NotOffered"] = 999999; cat.Costs["NotOffered"] = 1;

            var pool = new[] { "A", "B" };
            bool degraded;
            Assert.Contains(FleetFitness.Choose(cat, pool, 0, out degraded), pool);          // no requirement
            Assert.Contains(FleetFitness.Choose(cat, pool, 5000, out degraded), pool);       // filters to B
            Assert.Contains(FleetFitness.Choose(cat, pool, 999999, out degraded), pool);     // degraded
            Assert.True(degraded);
        }

        [Fact]
        public void Choose_RanksOnTheCallersOwnCostMeasure_NotTheCataloguePrice()
        {
            // Found live in slice 0's first FLEET_FITNESS lines: the real chooser ranks by summed BOM
            // units while IPrefabCatalogue.Cost is a component-price sum. Comparing a choice made on
            // one scale against a choice made on the other manufactures DIFFERS that say nothing
            // about the requirement. Here the two measures disagree on purpose -- catalogue price
            // prefers "Pricey", the caller's BOM measure prefers it less -- so only honouring the
            // supplied measure gives "Cheap".
            var cat = new Cat();
            cat.Cargo["Pricey"] = 5000; cat.Costs["Pricey"] = 10;    // cheapest by catalogue price
            cat.Cargo["Cheap"]  = 5000; cat.Costs["Cheap"]  = 900;

            var bomUnits = new Dictionary<string, long> { { "Pricey", 900 }, { "Cheap", 10 } };

            bool degraded;
            Assert.Equal("Pricey", FleetFitness.Choose(cat, new[] { "Pricey", "Cheap" }, 1000, out degraded));
            Assert.Equal("Cheap", FleetFitness.Choose(cat, new[] { "Pricey", "Cheap" }, 1000, out degraded,
                id => bomUnits[id]));
        }

        [Fact]
        public void Requirement_SamplesKeepCountingWhileBucketsFade_SoWarmthIsNotUndoneByDecay()
        {
            // Decay ages the SIZE evidence; it must not un-warm a faction that has genuinely been
            // observing. Samples is a count of what was seen, not a weight, so the two move
            // independently -- otherwise a long-running faction would flicker between warm and cold.
            var r = new RoleRequirement();
            for (int i = 0; i < 25; i++) r.Observe(500);
            Assert.True(r.IsWarm);

            for (int i = 0; i < 200; i++) r.Decay(0.9);

            Assert.True(r.IsWarm, "decay must not un-warm a faction that has been observing");
            // And once the surviving weight falls below one observation the requirement reports NO
            // OPINION rather than the size its traffic used to need. Geometric decay never reaches
            // zero, so without the explicit floor a faction that stopped hauling days ago would keep
            // sizing its fleet from a world that no longer exists -- and stale evidence is worse than
            // none, because it looks like evidence.
            Assert.Equal(0, r.SizeAtPercentile(0.9));
        }

        [Fact]
        public void Unmet_IsSeparateFromSize_BecauseSmallJobsCanStillMeanAGrowingBacklog()
        {
            // The two signals the design keeps apart: identical job sizes, opposite saturation. If
            // these were one term the second faction would look exactly like the first.
            var flat = new RoleRequirement();
            var behind = new RoleRequirement();
            for (int i = 0; i < 30; i++)
            {
                flat.Observe(500);   flat.ObserveUnmet(0.0, 30);
                behind.Observe(500); behind.ObserveUnmet(0.9, 30);
            }

            Assert.Equal(flat.SizeAtPercentile(0.9), behind.SizeAtPercentile(0.9));
            Assert.True(behind.UnmetEma > flat.UnmetEma + 0.5,
                "saturation must distinguish worlds that size identically");
        }

        // ---- what one observation measures: a trip, not the want (FAC-237) ----------------------

        private sealed class Litres : IItemVolumeCatalogue
        {
            private readonly Dictionary<string, double> _l = new Dictionary<string, double>(StringComparer.Ordinal);
            public Litres With(string key, double litresPerUnit) { _l[key] = litresPerUnit; return this; }
            public double VolumeLitersPerUnit(string invKey) { double v; return _l.TryGetValue(invKey ?? "", out v) ? v : 0; }
        }

        [Fact]
        public void TripLiters_IsTheVectorsOwnQuantity_NotTheWantItServes()
        {
            // The live shape: a want of millions served from a source that can spare a few hundred.
            // Filing the want pinned every faction's p90 at the top bucket.
            var v = new MaterialVector { ItemKey = "Ore", Quantity = 100, Shortfall = 1000000 };
            Assert.Equal(300, FleetFitness.TripLiters(v, new Litres().With("Ore", 3.0)));
        }

        [Fact]
        public void TripLiters_GasAndUnknownVolumes_FileNothing()
        {
            // Gas travels in tanks, which a cargo floor does not size -- and the volume catalogue
            // answers it at 1 L/unit, so the unknown-volume skip alone does not catch it.
            var volumes = new Litres().With("Gas:Hydrogen", 1.0);
            Assert.Equal(0, FleetFitness.TripLiters(
                new MaterialVector { ItemKey = "Gas:Hydrogen", Quantity = 14687538, Shortfall = 14687538 }, volumes));
            Assert.Equal(0, FleetFitness.TripLiters(
                new MaterialVector { ItemKey = "Mystery", Quantity = 50, Shortfall = 50 }, volumes));
        }

        [Fact]
        public void AnOperationsPass_FilesTheTrip_NotTheShortfall()
        {
            var world = new WorldModel();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            world.Planets = new StaticPlanetCatalogue(new PlanetRecord[0]);
            SpatialNode dest = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Dest");
            SpatialNode source = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "Source");
            foreach (SpatialNode n in new[] { dest, source })
            {
                n.OwnerFactionTag = "F";
                n.HoldStrength = 1.0;
                n.StableStationId = 4242;
            }
            source.Inventory["Ore"] = 100;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore", NodeId = dest.Id,
                Quantity = 1000000, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var state = new FactionState { Tag = "F" };
            new OperationsManager(new Litres().With("Ore", 3.0))
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            RoleRequirement r;
            Assert.True(state.FleetRequirements.TryGetValue("Trade", out r), "the pass must match a vector");
            Assert.Equal(1, r.Samples);
            // 300 L lands in [256, 1024); the 3,000,000 L want would have landed in [1048576, 4194304).
            Assert.Equal(1.0, r.Buckets[4]);
            Assert.Equal(0.0, r.Buckets[10]);
        }

        [Fact]
        public void APersistedRequirement_OnAnotherBasis_IsDiscardedOnLoad()
        {
            // The saturated whole-shortfall records already in every brain file decay too slowly to
            // be trusted after the switch; a record on the current basis must still round-trip.
            var graph = new SpatialGraph();
            FactionState st = WarmFaction(500);
            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("F"), FactionStore.DefaultLogCap);
            Assert.Equal(RoleRequirement.CurrentBasis, doc.FleetRequirements["Trade"].Basis);

            FactionState kept = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal(40, kept.FleetRequirements["Trade"].Samples);

            doc.FleetRequirements["Trade"].Basis = 0;   // a file written before the basis existed
            FactionState legacy = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            RoleRequirement r;
            Assert.False(legacy.FleetRequirements.TryGetValue("Trade", out r) && r.Samples > 0,
                "a whole-shortfall record must not survive the load");
        }
    }
}
