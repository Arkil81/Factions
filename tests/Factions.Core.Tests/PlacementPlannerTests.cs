using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class PlacementPlannerTests
    {
        private static PlacementRegion Surface(string generator)
        {
            return new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = generator };
        }

        private static PlacementRegion Feature(string name)
        {
            return new PlacementRegion { Kind = PlacementRegionKind.Feature, Key = name };
        }

        private static PlacementAssignment A(string tag, PlacementRegion region, int nodes)
        {
            return new PlacementAssignment { Tag = tag, Region = region, Nodes = nodes };
        }

        /// <summary>The live system: three landable bodies plus the ring.</summary>
        private static PlacementRegionGeometry LiveGeometry(PlacementRegion r)
        {
            if (r.Kind == PlacementRegionKind.Feature && r.Key == "Cauldron Ring")
                return new PlacementRegionGeometry { InnerRadiusKm = 195, OuterRadiusKm = 465, HalfThicknessKm = 1.8 };
            if (r.Kind != PlacementRegionKind.PlanetSurface) return null;
            switch (r.Key)
            {
                case "Tellus": return new PlacementRegionGeometry { SurfaceRadiusKm = 30.0 };
                case "Agni":   return new PlacementRegionGeometry { SurfaceRadiusKm = 15.0 };
                case "Kor":    return new PlacementRegionGeometry { SurfaceRadiusKm = 9.5 };
                case "RealGasGiant":
                    return new PlacementRegionGeometry { SurfaceRadiusKm = 9.5, AllowsSurface = false };
                default: return null;
            }
        }

        private static double OldSeparation(PlacementRegion r)
        {
            return r.Kind == PlacementRegionKind.PlanetSurface ? 15.0 : 30.0;
        }

        private static double AuthoredSeparation(PlacementRegion r)
        {
            if (r.Kind == PlacementRegionKind.Feature) return 30.0;             // ring, now Orbit
            if (r.Key == "Kor" || r.Key == "Agni") return 10.0;              // operator's cut
            return 14.0;                                                     // Tellus
        }

        private static PlacementPlan Plan(IEnumerable<PlacementAssignment> a, System.Func<PlacementRegion, double> sep)
        {
            return PlacementPlanner.Plan(a, LiveGeometry, sep);
        }

        // ---- the finding this class exists for -------------------------------------------------

        [Fact]
        public void ElevenFactionsOnKor_IsRefusedAtTheOldFloor()
        {
            var assignments = Enumerable.Range(1, 11)
                .Select(i => A("F" + i.ToString("00"), Surface("Kor"), 1))
                .ToList();

            PlacementPlan plan = Plan(assignments, OldSeparation);

            Assert.False(plan.IsFeasible);
            Assert.Contains(plan.Refusals, r => r.Contains("Kor"));
        }

        [Fact]
        public void TheFloorCutHelpsKorButStillDoesNotReachElevenClaimants()
        {
            // The 15 -> 10 km cut was made to fit eleven factions on Kor. Measured against what the
            // sampler can actually place, Kor goes 3 -> 8: a large improvement and still short.
            // Eight is also exactly what the authored estate assigns it, so there is no headroom.
            var eleven = Enumerable.Range(1, 11)
                .Select(i => A("F" + i.ToString("00"), Surface("Kor"), 1))
                .ToList();
            Assert.False(Plan(eleven, AuthoredSeparation).IsFeasible);

            var eight = Enumerable.Range(1, 8)
                .Select(i => A("F" + i.ToString("00"), Surface("Kor"), 1))
                .ToList();
            PlacementPlan plan = Plan(eight, AuthoredSeparation);
            Assert.True(plan.IsFeasible, string.Join(" | ", plan.Refusals));
        }

        [Fact]
        public void ARefusalNamesTheNumbersNeededToRecutIt()
        {
            var assignments = Enumerable.Range(1, 11)
                .Select(i => A("F" + i.ToString("00"), Surface("Kor"), 1))
                .ToList();

            string refusal = Plan(assignments, OldSeparation).Refusals.Single();

            Assert.Contains("11 node(s) requested", refusal);
            Assert.Contains("11 faction(s)", refusal);
            Assert.Contains("per faction", refusal);
        }

        // ---- the explicit-count rule -----------------------------------------------------------

        [Fact]
        public void ARegionWithNoCount_IsRefused_NotTreatedAsFillIt()
        {
            PlacementPlan plan = Plan(new[] { A("FCTM", Feature("Cauldron Ring"), 0) }, AuthoredSeparation);

            Assert.False(plan.IsFeasible);
            Assert.Contains(plan.Refusals, r => r.Contains("explicit and positive"));
        }

        // ---- rules that must not become geometry -----------------------------------------------

        [Fact]
        public void AnUnlandableBody_RefusesASurfaceEstate_EvenForOneNode()
        {
            PlacementPlan plan = Plan(new[] { A("SPRT", Surface("RealGasGiant"), 1) }, AuthoredSeparation);

            Assert.False(plan.IsFeasible);
            Assert.Contains(plan.Refusals, r => r.Contains("RealGasGiant"));
        }

        [Fact]
        public void ARegionThisWorldDoesNotHave_IsRefused_NotSkipped()
        {
            PlacementPlan plan = Plan(new[] { A("SPRT", Surface("Pertam"), 1) }, AuthoredSeparation);

            Assert.False(plan.IsFeasible);
            Assert.Contains(plan.Refusals, r => r.Contains("no such region"));
        }

        // ---- aggregation -----------------------------------------------------------------------

        [Fact]
        public void DemandAggregatesAcrossFactions_AndCountsEachFactionOnce()
        {
            var assignments = new[]
            {
                A("AAA", Surface("Tellus"), 5),
                A("BBB", Surface("Tellus"), 5),
                A("AAA", Surface("Tellus"), 2),   // same faction, second claim on one region
            };

            PlacementRegionDemand tellus = Plan(assignments, AuthoredSeparation).Regions.Single();

            Assert.Equal(12, tellus.NodesRequested);
            Assert.Equal(2, tellus.Claimants);
        }

        [Fact]
        public void TenPowerBasesOfFive_OverSubscribeTellus()
        {
            // Five nodes each x ten factions is 50 against a Tellus that places 43 at 14 km. This was
            // the shape the estate was first sketched with, and an area-based capacity estimate of ~52
            // approved it. It has to be four each or fewer.
            var assignments = new List<PlacementAssignment>();
            for (int i = 0; i < 10; i++) assignments.Add(A("F" + i.ToString("00"), Surface("Tellus"), 5));

            PlacementPlan plan = Plan(assignments, AuthoredSeparation);

            Assert.False(plan.IsFeasible);
            Assert.Contains(plan.Refusals, r => r.Contains("Tellus") && r.Contains("capacity 43"));
        }

        [Fact]
        public void TheAuthoredFortyOnTellus_DoesNotFitAtThe15kmFloor()
        {
            // faction_recipe.json puts 40 nodes on Tellus. The lattice places 38 at the 15 km floor,
            // so at that floor the estate over-subscribes it by two.
            var assignments = new List<PlacementAssignment>();
            for (int i = 0; i < 10; i++) assignments.Add(A("F" + i.ToString("00"), Surface("Tellus"), 4));

            PlacementPlan plan = Plan(assignments, OldSeparation);

            Assert.False(plan.IsFeasible);
            Assert.Contains(plan.Refusals, r => r.Contains("40 node(s)") && r.Contains("capacity 38"));
        }

        [Fact]
        public void TheAuthoredFortyOnTellus_FitsAtItsAuthored14km()
        {
            // The paired positive: world_recipe.json authors Tellus at 14 km, which places 43.
            var assignments = new List<PlacementAssignment>();
            for (int i = 0; i < 10; i++) assignments.Add(A("F" + i.ToString("00"), Surface("Tellus"), 4));

            PlacementPlan plan = Plan(assignments, AuthoredSeparation);

            Assert.True(plan.IsFeasible, string.Join(" | ", plan.Refusals));
            Assert.Equal(40, plan.TotalNodesRequested);
        }

        [Fact]
        public void ThreeEachOnTellus_Fits()
        {
            var assignments = new List<PlacementAssignment>();
            for (int i = 0; i < 10; i++) assignments.Add(A("F" + i.ToString("00"), Surface("Tellus"), 3));

            PlacementPlan plan = Plan(assignments, AuthoredSeparation);

            Assert.True(plan.IsFeasible, string.Join(" | ", plan.Refusals));
            Assert.Equal(30, plan.TotalNodesRequested);
        }

        [Fact]
        public void ARecutBrief_IsFeasibleUnderTheAuthoredSeparations()
        {
            // Ten factions: a four-node Tellus power base plus one forward presence each, rotated
            // across the three forward regions rather than every faction on every body.
            var assignments = new List<PlacementAssignment>();
            var forward = new[] { Feature("Cauldron Ring"), Surface("Agni"), Surface("Kor") };
            for (int i = 0; i < 10; i++)
            {
                string tag = "F" + i.ToString("00");
                assignments.Add(A(tag, Surface("Tellus"), 3));
                assignments.Add(A(tag, forward[i % forward.Length], 2));
            }
            // SPRT: power base in the ring, small estates on Agni and Kor.
            assignments.Add(A("SPRT", Feature("Cauldron Ring"), 8));
            assignments.Add(A("SPRT", Surface("Agni"), 2));
            assignments.Add(A("SPRT", Surface("Kor"), 2));

            PlacementPlan plan = Plan(assignments, AuthoredSeparation);

            Assert.True(plan.IsFeasible, string.Join(" | ", plan.Refusals));
            Assert.Equal(30 + 20 + 12, plan.TotalNodesRequested);
        }

        [Fact]
        public void TheSameBrief_IsRefusedUnderTheOldSeparations()
        {
            // The paired negative: the authored cut is load-bearing, not cosmetic.
            var assignments = new List<PlacementAssignment>();
            var forward = new[] { Feature("Cauldron Ring"), Surface("Agni"), Surface("Kor") };
            for (int i = 0; i < 10; i++)
            {
                string tag = "F" + i.ToString("00");
                assignments.Add(A(tag, Surface("Tellus"), 3));
                assignments.Add(A(tag, forward[i % forward.Length], 2));
            }
            assignments.Add(A("SPRT", Feature("Cauldron Ring"), 8));
            assignments.Add(A("SPRT", Surface("Agni"), 2));
            assignments.Add(A("SPRT", Surface("Kor"), 2));

            Assert.False(Plan(assignments, OldSeparation).IsFeasible);
        }
    }
}
