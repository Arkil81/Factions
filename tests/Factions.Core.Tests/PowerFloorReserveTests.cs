using System.Collections.Generic;
using Factions.Core;
using Xunit;
using static Factions.Core.Tests.ArmedYard;

namespace Factions.Core.Tests
{
    /// <summary>FAC-467: a combat power floor no squadron can reach stops asking once a full squadron
    /// has a second one's worth parked behind it. Live 2026-10-05..09, SPRT and FCTM each built a Raid
    /// hull every yard tick toward a terror budget of millions, and parked 3,388 and 437 of them.</summary>
    public class PowerFloorReserveTests
    {
        private static ArmedCatalogue Catalogue()
        {
            return new ArmedCatalogue().Add("Drone", 500, "Small").Add("Gunboat", 1000, "Large").Add("Hauler", 0, "Large", 5000);
        }

        private static List<string> Tick(int drones, int gunboats, out SpatialNode yard)
        {
            var recipes = new CostRecipes(); recipes.CostByPrefab["Drone"] = 10; recipes.CostByPrefab["Gunboat"] = 40;
            WorldModel world = Yard(Catalogue(), recipes, out yard);
            yard.ShipStock["Drone"] = drones; yard.ShipStock["Gunboat"] = gunboats;
            var state = new FactionState { Tag = "F", TerrorStandingDps = 50000 };   // beyond any squadron here
            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => role == "Raid" ? new[] { "Drone", "Gunboat" } : new[] { "Hauler" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);
            return log;
        }

        [Fact]
        public void AFullSquadronWithAFullReserve_PostsNoFloor_AndQueuesNothing()
        {
            // One large and four fighters fly; five more drones wait. The gap is 47,000 and no hull
            // here closes any of it by being built.
            SpatialNode yard;
            List<string> log = Tick(9, 1, out yard);
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid"));
            Assert.Null(yard.BuildQueue.Find(e => e.Role == "Raid"));
        }

        [Fact]
        public void OneShortOfAFullReserve_StillBuilds()
        {
            // The boundary from below: four spare drones are not a full second squadron.
            SpatialNode yard;
            List<string> log = Tick(8, 1, out yard);
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid") && l.Contains("budget=terror"));
            Assert.NotNull(yard.BuildQueue.Find(e => e.Role == "Raid"));
        }

        [Fact]
        public void LargeHullsOnly_StopAtASquadronsWorthOfSpares_ThoughFighterSlotsStayOpen()
        {
            // The live FCTM shape: every hull is large, so the best squadron is one Gunboat with four
            // fighter slots open, and a large hull built toward the floor can never fill them. Five
            // spare Gunboats are a squadron's capacity in reserve.
            SpatialNode yard;
            List<string> log = Tick(0, 6, out yard);
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid"));
            Assert.Null(yard.BuildQueue.Find(e => e.Role == "Raid"));
        }

        [Fact]
        public void LargeHullsOnly_OneShortOfTheCapacity_StillBuilds()
        {
            SpatialNode yard;
            List<string> log = Tick(0, 5, out yard);
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid") && l.Contains("budget=terror"));
        }

        [Fact]
        public void NoCombatHullAtAll_IsNotAFullReserve()
        {
            // An empty roster reports no open slot and no hull; zero spares against zero hulls is not
            // a reserve, and the floor must still be posted.
            SpatialNode yard;
            List<string> log = Tick(0, 0, out yard);
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE role=Raid") && l.Contains("budget=terror"));
        }
    }
}
