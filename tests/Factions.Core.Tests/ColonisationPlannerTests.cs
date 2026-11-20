using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The colonisation trigger: strategic, driven by economics. FAC-106.
    /// </summary>
    public class ColonisationPlannerTests
    {
        private sealed class Index : IRegionMaterialIndex
        {
            public readonly Dictionary<string, List<ColonisationCandidate>> Yielding =
                new Dictionary<string, List<ColonisationCandidate>>();
            public readonly List<ColonisationCandidate> Near = new List<ColonisationCandidate>();

            public IEnumerable<ColonisationCandidate> RegionsYielding(string subTypeId)
            {
                List<ColonisationCandidate> l;
                return Yielding.TryGetValue(subTypeId, out l) ? l : new List<ColonisationCandidate>();
            }

            public IEnumerable<ColonisationCandidate> RegionsNear(PlacementRegion full) { return Near; }
        }

        private static ColonisationCandidate Surface(string key, double share, double distanceKm,
                                                     double radiusKm, double sepKm, int occupied)
        {
            return new ColonisationCandidate
            {
                Region = new PlacementRegion { Kind = PlacementRegionKind.PlanetSurface, Key = key },
                Geometry = new PlacementRegionGeometry { SurfaceRadiusKm = radiusKm, AllowsSurface = true },
                SeparationKm = sepKm,
                Share = share,
                DistanceKm = distanceKm,
                OccupiedNodes = occupied,
            };
        }

        private static ColonisationCandidate Orbit(string key, double distanceKm, int occupied)
        {
            return new ColonisationCandidate
            {
                Region = new PlacementRegion { Kind = PlacementRegionKind.PlanetOrbit, Key = key },
                Geometry = new PlacementRegionGeometry
                {
                    InnerRadiusKm = 20, OuterRadiusKm = 60, HalfThicknessKm = 10, AllowsSurface = false
                },
                SeparationKm = 30,
                Share = 0.2,
                DistanceKm = distanceKm,
                OccupiedNodes = occupied,
            };
        }

        // ---- the trigger is a comparison, not an urge ------------------------------------------

        [Fact]
        public void BuyingCheaplyNeverTriggersAMove()
        {
            var index = new Index();
            index.Yielding["Ore:Platinum"] = new List<ColonisationCandidate> { Surface("Agni", 0.9, 500, 15, 10, 0) };

            // 100/pass over 30 passes = 3,000 against a 100,000 move. Buying wins by two orders.
            var costs = new Dictionary<string, double> { { "Ore:Platinum", 100 } };
            Assert.Null(ColonisationPlanner.Evaluate(costs, index, new ColonisationRules()));
        }

        [Fact]
        public void ASustainedExpensiveNeedTriggersAMove()
        {
            var index = new Index();
            index.Yielding["Ore:Platinum"] = new List<ColonisationCandidate> { Surface("Agni", 0.9, 500, 15, 10, 0) };

            var costs = new Dictionary<string, double> { { "Ore:Platinum", 20000 } };
            ColonisationIntent intent = ColonisationPlanner.Evaluate(costs, index, new ColonisationRules());

            Assert.NotNull(intent);
            Assert.Equal("Ore:Platinum", intent.SubTypeId);
            Assert.Equal("Agni", intent.Target.Region.Key);
            Assert.False(intent.IsBeachhead);
            Assert.True(intent.Advantage > 0);
            Assert.Contains("buying costs", intent.Why);
        }

        [Fact]
        public void TheUbiquitousMaterialLosesToTheScarceOneWithoutATuningKnob()
        {
            // The property worth protecting: iron is everywhere, so it is cheap, so it never wins.
            // Nothing here says "iron" -- the cost difference is the geography's doing.
            var index = new Index();
            index.Yielding["Ore:Iron"] = new List<ColonisationCandidate> { Surface("Tellus", 0.9, 100, 30, 15, 0) };
            index.Yielding["Ore:Platinum"] = new List<ColonisationCandidate> { Surface("Agni", 0.9, 500, 15, 10, 0) };

            var costs = new Dictionary<string, double>
            {
                { "Ore:Iron", 4000 },        // cheap per pass, because it is everywhere
                { "Ore:Platinum", 20000 },   // expensive per pass, because it is not
            };

            ColonisationIntent intent = ColonisationPlanner.Evaluate(costs, index, new ColonisationRules());
            Assert.Equal("Ore:Platinum", intent.SubTypeId);
        }

        // ---- richness against distance ----------------------------------------------------------

        [Fact]
        public void DistanceIsAWeightingNotACutoff()
        {
            // A rich distant source still beats a poor near one; the near one wins only when the
            // richness gap closes.
            var index = new Index();
            index.Yielding["Ore:Platinum"] = new List<ColonisationCandidate>
            {
                Surface("Agni", 0.90, 3000, 15, 10, 0),   // rich, far
                Surface("Kor",  0.10,  100,  9.5, 10, 0), // poor, near
            };

            var costs = new Dictionary<string, double> { { "Ore:Platinum", 20000 } };
            Assert.Equal("Agni", ColonisationPlanner.Evaluate(costs, index, new ColonisationRules()).Target.Region.Key);
        }

        // ---- the full-body diversion ------------------------------------------------------------

        [Fact]
        public void AFullRegionDivertsToAForwardOutpostAndRemembersTheObjective()
        {
            // Operator, 2026-08-26: a full body diverts to a close forward outpost that can lead to
            // further conquest. The objective has to survive the diversion or the outpost becomes the
            // goal.
            var index = new Index();
            // Agni's surface: 15 km radius at 10 km separation is ~29 sites. Fill it.
            index.Yielding["Ore:Platinum"] = new List<ColonisationCandidate>
            {
                Surface("Agni", 0.9, 500, 15, 10, 500)
            };
            index.Near.Add(Orbit("Agni", 520, 0));

            var costs = new Dictionary<string, double> { { "Ore:Platinum", 20000 } };
            ColonisationIntent intent = ColonisationPlanner.Evaluate(costs, index, new ColonisationRules());

            Assert.NotNull(intent);
            Assert.True(intent.IsBeachhead);
            Assert.Equal(PlacementRegionKind.PlanetOrbit, intent.Target.Region.Kind);
            Assert.Equal("Agni", intent.Target.Region.Key);
            Assert.Equal(PlacementRegionKind.PlanetSurface, intent.DivertedFrom.Kind);
            Assert.Contains("is full, staging at", intent.Why);
        }

        [Fact]
        public void ARegionWithRoomBeatsStagingBesideARicherFullOne()
        {
            // The beachhead still has to fight its way in, and that cost is not modelled -- so an
            // open region wins even when it is poorer.
            var index = new Index();
            index.Yielding["Ore:Platinum"] = new List<ColonisationCandidate>
            {
                Surface("Agni", 0.95, 500, 15, 10, 500),   // richest, full
                Surface("Kor",  0.20, 900,  9.5, 10, 0),   // poorer, open
            };
            index.Near.Add(Orbit("Agni", 520, 0));

            var costs = new Dictionary<string, double> { { "Ore:Platinum", 20000 } };
            ColonisationIntent intent = ColonisationPlanner.Evaluate(costs, index, new ColonisationRules());

            Assert.False(intent.IsBeachhead);
            Assert.Equal("Kor", intent.Target.Region.Key);
        }

        [Fact]
        public void NoRoomAnywhereNearAFullRegionYieldsNoIntent()
        {
            var index = new Index();
            index.Yielding["Ore:Platinum"] = new List<ColonisationCandidate>
            {
                Surface("Agni", 0.9, 500, 15, 10, 500)
            };
            index.Near.Add(Orbit("Agni", 520, 100000));   // the orbit is full too

            var costs = new Dictionary<string, double> { { "Ore:Platinum", 20000 } };
            Assert.Null(ColonisationPlanner.Evaluate(costs, index, new ColonisationRules()));
        }

        [Fact]
        public void ARegionThatYieldsNothingWantedIsNeverATarget()
        {
            var index = new Index();
            index.Yielding["Ore:Platinum"] = new List<ColonisationCandidate>
            {
                Surface("Tellus", 0.0, 100, 30, 15, 0)   // no platinum here
            };

            var costs = new Dictionary<string, double> { { "Ore:Platinum", 20000 } };
            Assert.Null(ColonisationPlanner.Evaluate(costs, index, new ColonisationRules()));
        }

        [Fact]
        public void AnUnlandableSurfaceHasNoRoomAtAll()
        {
            // The gas giant. PlacementCapacity returns 0 for a surface the body forbids, so this must
            // fall through to a diversion rather than reading as open.
            var index = new Index();
            ColonisationCandidate giant = Surface("RealGasGiant", 0.9, 300, 9.5, 10, 0);
            giant.Geometry.AllowsSurface = false;
            index.Yielding["Ore:Platinum"] = new List<ColonisationCandidate> { giant };
            index.Near.Add(Orbit("RealGasGiant", 320, 0));

            var costs = new Dictionary<string, double> { { "Ore:Platinum", 20000 } };
            ColonisationIntent intent = ColonisationPlanner.Evaluate(costs, index, new ColonisationRules());

            Assert.True(intent.IsBeachhead);
            Assert.Equal(PlacementRegionKind.PlanetOrbit, intent.Target.Region.Kind);
        }

        [Fact]
        public void TheIntentCarriesNoPosition()
        {
            // Placement is being rebuilt (FAC-102). A decision layer that resolved a region to a
            // coordinate would have to be rewritten with it -- asserted so the seam cannot erode.
            Assert.Null(typeof(ColonisationIntent).GetField("Position"));
            Assert.Null(typeof(ColonisationIntent).GetField("PositionKm"));
            Assert.Null(typeof(ColonisationIntent).GetField("NodeId"));
        }
    }
}
