using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class PlayerThrottlePlannerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

        private static PlayerThrottlePlanner.PlayerState P(string party, int step, bool over = true)
        {
            return new PlayerThrottlePlanner.PlayerState { Party = party, Step = step, Over = over };
        }

        private static Func<string, ThrottleClass, double> Costs(double refinery, double assembler, double welder)
        {
            return (party, c) => c == ThrottleClass.Refinery ? refinery : c == ThrottleClass.Assembler ? assembler
                               : c == ThrottleClass.Welder ? welder : 0.0;
        }

        [Fact]
        public void BelowStepThreeNothingIsPaused()
        {
            var planner = new PlayerThrottlePlanner(180);
            var changes = planner.Update(new[] { P("Arkil", 2) }, Costs(1, 1, 1), T0);
            Assert.Empty(changes);
            Assert.False(planner.Any);
        }

        [Fact]
        public void AtStepThreeTheCostliestClassIsPausedFirst()
        {
            var planner = new PlayerThrottlePlanner(180);
            var changes = planner.Update(new[] { P("Arkil", 3) }, Costs(0.2, 0.5, 0.1), T0);

            var c = Assert.Single(changes);
            Assert.Equal(ThrottleClass.Assembler, c.Class);
            Assert.True(c.On);
            Assert.Equal("governor", c.Cause);
            Assert.True(planner.IsThrottled("Arkil", ThrottleClass.Assembler));
            Assert.False(planner.IsThrottled("Arkil", ThrottleClass.Refinery));
        }

        [Fact]
        public void OneMoreClassPerIntervalWhileStillOver()
        {
            var planner = new PlayerThrottlePlanner(180);
            var costs = Costs(0.2, 0.5, 0.1);
            planner.Update(new[] { P("Arkil", 3) }, costs, T0);

            Assert.Empty(planner.Update(new[] { P("Arkil", 3) }, costs, T0.AddSeconds(179)));

            var second = Assert.Single(planner.Update(new[] { P("Arkil", 4) }, costs, T0.AddSeconds(180)));
            Assert.Equal(ThrottleClass.Refinery, second.Class);
            var third = Assert.Single(planner.Update(new[] { P("Arkil", 4) }, costs, T0.AddSeconds(360)));
            Assert.Equal(ThrottleClass.Welder, third.Class);
            Assert.Empty(planner.Update(new[] { P("Arkil", 4) }, costs, T0.AddSeconds(540)));
        }

        [Fact]
        public void NotOverShareHoldsWithoutAddingOrReleasing()
        {
            var planner = new PlayerThrottlePlanner(180);
            planner.Update(new[] { P("Arkil", 3) }, Costs(0.2, 0.5, 0.1), T0);
            Assert.Empty(planner.Update(new[] { P("Arkil", 3, over: false) }, Costs(0.2, 0.5, 0.1), T0.AddSeconds(400)));
            Assert.Equal(new[] { ThrottleClass.Assembler }, planner.ClassesFor("Arkil").ToArray());
        }

        [Fact]
        public void AClassWithNoMeasuredCostIsNeverPaused()
        {
            var planner = new PlayerThrottlePlanner(180);
            Assert.Empty(planner.Update(new[] { P("Arkil", 3) }, Costs(0, 0, 0), T0));
            var only = Assert.Single(planner.Update(new[] { P("Arkil", 3) }, Costs(0, 0, 0.3), T0.AddSeconds(1)));
            Assert.Equal(ThrottleClass.Welder, only.Class);
            Assert.Empty(planner.Update(new[] { P("Arkil", 3) }, Costs(0, 0, 0.3), T0.AddSeconds(500)));
        }

        [Fact]
        public void DroppingBelowStepThreeReleasesEverythingAtOnce()
        {
            var planner = new PlayerThrottlePlanner(180);
            var costs = Costs(0.2, 0.5, 0.1);
            planner.Update(new[] { P("Arkil", 3) }, costs, T0);
            planner.Update(new[] { P("Arkil", 4) }, costs, T0.AddSeconds(180));

            var released = planner.Update(new[] { P("Arkil", 2, over: false) }, costs, T0.AddSeconds(200));
            Assert.Equal(2, released.Count);
            Assert.All(released, c => Assert.False(c.On));
            Assert.False(planner.Any);
        }

        [Fact]
        public void APlayerWhoLeftIsReleased()
        {
            var planner = new PlayerThrottlePlanner(180);
            planner.Update(new[] { P("Arkil", 3), P("Bea", 3) }, Costs(0.2, 0.5, 0.1), T0);
            var released = planner.Update(new[] { P("Bea", 3) }, Costs(0.2, 0.5, 0.1), T0.AddSeconds(10));
            var c = Assert.Single(released);
            Assert.Equal("Arkil", c.Party);
            Assert.False(c.On);
        }

        [Fact]
        public void AForcedClassSurvivesTheGovernorReleasingAndIsSkippedByIt()
        {
            var planner = new PlayerThrottlePlanner(180);
            var forced = planner.Force("Arkil", ThrottleClass.Assembler, true);
            Assert.NotNull(forced);
            Assert.Equal("admin", forced.Cause);

            // The governor skips the already-paused assembler and takes the next costliest.
            var c = Assert.Single(planner.Update(new[] { P("Arkil", 3) }, Costs(0.2, 0.5, 0.1), T0));
            Assert.Equal(ThrottleClass.Refinery, c.Class);

            var released = planner.Update(new[] { P("Arkil", 0, over: false) }, Costs(0.2, 0.5, 0.1), T0.AddSeconds(10));
            Assert.Equal(ThrottleClass.Refinery, Assert.Single(released).Class);
            Assert.True(planner.IsThrottled("Arkil", ThrottleClass.Assembler));

            var cleared = planner.Force("Arkil", ThrottleClass.Assembler, false);
            Assert.False(cleared.On);
            Assert.False(planner.Any);
        }

        [Fact]
        public void ForcingAClassTheGovernorAlreadyHoldsIsNoChange()
        {
            var planner = new PlayerThrottlePlanner(180);
            planner.Update(new[] { P("Arkil", 3) }, Costs(0.2, 0.5, 0.1), T0);
            Assert.Null(planner.Force("Arkil", ThrottleClass.Assembler, true));
            // Releasing the governor's hold leaves the forced one, so no off change is reported.
            Assert.Empty(planner.Update(new[] { P("Arkil", 2) }, Costs(0.2, 0.5, 0.1), T0.AddSeconds(10)));
            Assert.True(planner.IsThrottled("Arkil", ThrottleClass.Assembler));
        }

        [Fact]
        public void ReleaseAllGovernedKeepsForcedClasses()
        {
            var planner = new PlayerThrottlePlanner(180);
            planner.Force("Bea", ThrottleClass.Welder, true);
            planner.Update(new[] { P("Arkil", 3) }, Costs(0.2, 0.5, 0.1), T0);
            var released = planner.ReleaseAllGoverned();
            Assert.Equal("Arkil", Assert.Single(released).Party);
            Assert.Equal(new List<string> { "Bea" }, planner.Parties());
        }

        [Theory]
        [InlineData("refinery", ThrottleClass.Refinery)]
        [InlineData("Refineries", ThrottleClass.Refinery)]
        [InlineData("assemblers", ThrottleClass.Assembler)]
        [InlineData("welder", ThrottleClass.Welder)]
        [InlineData("BaR", ThrottleClass.Welder)]
        [InlineData("programmable", ThrottleClass.Programmable)]
        [InlineData("PBs", ThrottleClass.Programmable)]
        public void ClassNamesParse(string text, ThrottleClass expected)
        {
            ThrottleClass c;
            Assert.True(PlayerThrottlePlanner.TryParse(text, out c));
            Assert.Equal(expected, c);
        }

        [Fact]
        public void ProgrammableBlocksAreTheFourthClassAndTakeTheirTurnByCost()
        {
            var planner = new PlayerThrottlePlanner(180);
            Func<string, ThrottleClass, double> costs = (party, c) => c == ThrottleClass.Programmable ? 0.9 : 0.1;
            var c1 = Assert.Single(planner.Update(new[] { P("Arkil", 3) }, costs, T0));
            Assert.Equal(ThrottleClass.Programmable, c1.Class);
            Assert.Equal("programmable", PlayerThrottlePlanner.Name(ThrottleClass.Programmable));
        }

        [Fact]
        public void AnUnknownClassNameDoesNotParse()
        {
            ThrottleClass c;
            Assert.False(PlayerThrottlePlanner.TryParse("turret", out c));
            Assert.False(PlayerThrottlePlanner.TryParse("", out c));
        }
    }
}
