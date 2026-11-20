using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The legacy asteroid-field planner. Since FAC-102 it takes a node rather than a label: rock goes
    /// where there is ore to find, and how much is the node's own ResourceValue. The three per-kind
    /// rows it used to read carried near-identical counts and sizes and were only ever nonzero for the
    /// mineable kinds, so the label was a slow way of asking whether the node has a deposit.
    /// <para>FAC-096 retires this planner with the zone model and feature rock replaces it; these tests
    /// hold its current contract until then.</para>
    /// </summary>
    public sealed class AsteroidFieldPlannerTests
    {
        private static FactionTuning BuildTuning()
        {
            return new FactionTuning
            {
                AsteroidField = new NodeKindAsteroidFieldEntry
                {
                    MaxCount = 6, SizeMin = 64, SizeMax = 256, RadiusKm = 1.5
                },
                FreeMintScale = new Dictionary<string, double> { { "Ore", 2.00 }, { "RareOre", 0.10 } },
                NodeYieldTable = new[]
                {
                    new NodeYieldEntry { SubTypeId = "Ore:Iron",     ItemKind = "Ore",     Weight = 0.35 },
                    new NodeYieldEntry { SubTypeId = "Ore:Nickel",   ItemKind = "Ore",     Weight = 0.30 },
                    new NodeYieldEntry { SubTypeId = "Ore:Silver",   ItemKind = "RareOre", Weight = 0.50 },
                    new NodeYieldEntry { SubTypeId = "Ingot:Stone",  ItemKind = "Ingot",   Weight = 1.00 },
                },
                IceOreSubTypeId = "Ice",
            };
        }

        private static SpatialNode Node(double resourceValue, params string[] ores)
        {
            var n = new SpatialNode { Id = "N1", ResourceValue = resourceValue };
            if (ores != null && ores.Length > 0)
            {
                n.Deposit = new NodeDeposit();
                foreach (string ore in ores)
                    n.Deposit.Entries.Add(new DepositEntry(ore, 1.0 / ores.Length, 100.0 / ores.Length));
            }
            return n;
        }

        [Fact]
        public void Plan_CountScalesWithResourceValue_CappedAtMaxCount()
        {
            FactionTuning tuning = BuildTuning();

            Assert.Equal(3, AsteroidFieldPlanner.Plan(Node(0.5, "Iron"), 0.5, NodeZone.DeepSpace, tuning).Count);
            Assert.Equal(6, AsteroidFieldPlanner.Plan(Node(1.0, "Iron"), 1.0, NodeZone.DeepSpace, tuning).Count);
            Assert.Equal(6, AsteroidFieldPlanner.Plan(Node(1.0, "Iron"), 5.0, NodeZone.DeepSpace, tuning).Count);
        }

        // The gate is the ground now: a node nobody has found ore under gets no rock, however rich its
        // ResourceValue happens to be.
        [Fact]
        public void Plan_NodeWithNoDeposit_YieldsZeroCount()
        {
            Assert.Equal(0, AsteroidFieldPlanner.Plan(Node(1.0), 1.0, NodeZone.DeepSpace, BuildTuning()).Count);
        }

        [Fact]
        public void Plan_BarrenDeposit_YieldsZeroCount()
        {
            SpatialNode barren = Node(1.0);
            barren.Deposit = new NodeDeposit();

            Assert.Equal(0, AsteroidFieldPlanner.Plan(barren, 1.0, NodeZone.DeepSpace, BuildTuning()).Count);
        }

        [Fact]
        public void Plan_UnconfiguredProfile_YieldsZeroCount()
        {
            FactionTuning tuning = BuildTuning();
            tuning.AsteroidField = null;

            Assert.Equal(0, AsteroidFieldPlanner.Plan(Node(1.0, "Iron"), 1.0, NodeZone.DeepSpace, tuning).Count);
        }

        [Theory]
        [InlineData(NodeZone.Surface)]
        [InlineData(NodeZone.Orbit)]
        public void Plan_SurfaceOrOrbitZone_YieldsZeroCount(NodeZone zone)
        {
            Assert.Equal(0, AsteroidFieldPlanner.Plan(Node(1.0, "Iron"), 1.0, zone, BuildTuning()).Count);
        }

        [Fact]
        public void Plan_IceGround_BaseIsIce_AndIceIsNotAlsoADeposit()
        {
            AsteroidFieldPlan plan = AsteroidFieldPlanner.Plan(Node(1.0, "Ice"), 1.0, NodeZone.DeepSpace, BuildTuning());

            Assert.Equal("Ice", plan.BaseMaterialOreName);
            Assert.DoesNotContain(plan.Deposits, d => d.OreName == "Ice");
        }

        [Fact]
        public void Plan_OrdinaryGround_BaseIsStone_DepositsAreMostlyCommonOre()
        {
            AsteroidFieldPlan plan = AsteroidFieldPlanner.Plan(Node(1.0, "Iron"), 1.0, NodeZone.DeepSpace, BuildTuning());

            Assert.Equal("Stone", plan.BaseMaterialOreName);
            double common = plan.Deposits.Where(d => d.OreName == "Iron" || d.OreName == "Nickel").Sum(d => d.Weight);
            double rare = plan.Deposits.Where(d => d.OreName == "Silver").Sum(d => d.Weight);
            Assert.True(common > rare * 5, "deposits should be dominated by common ore, not rare ore");
        }

        [Fact]
        public void Plan_IsDeterministic_GivenSameInputs()
        {
            FactionTuning tuning = BuildTuning();

            AsteroidFieldPlan a = AsteroidFieldPlanner.Plan(Node(0.7, "Iron"), 0.7, NodeZone.DeepSpace, tuning);
            AsteroidFieldPlan b = AsteroidFieldPlanner.Plan(Node(0.7, "Iron"), 0.7, NodeZone.DeepSpace, tuning);

            Assert.Equal(a.Count, b.Count);
            Assert.Equal(a.BaseMaterialOreName, b.BaseMaterialOreName);
            Assert.Equal(a.Deposits.Select(d => d.OreName + "=" + d.Weight),
                         b.Deposits.Select(d => d.OreName + "=" + d.Weight));
        }

        // The allocation-free pre-check must never reject a node the real Plan would have given a
        // field: it exists so a per-frame caller can skip allocating, and a false negative would lose
        // rock silently.
        [Theory]
        [InlineData(NodeZone.DeepSpace)]
        [InlineData(NodeZone.Orbit)]
        [InlineData(NodeZone.Surface)]
        public void CouldHaveField_IsNecessaryConditionForANonZeroPlan(NodeZone zone)
        {
            FactionTuning tuning = BuildTuning();

            foreach (SpatialNode node in new[] { Node(1.0), Node(1.0, "Iron"), Node(0.0, "Iron") })
            {
                int count = AsteroidFieldPlanner.Plan(node, node.ResourceValue, zone, tuning).Count;
                if (count > 0)
                    Assert.True(AsteroidFieldPlanner.CouldHaveField(node, zone, tuning),
                                "Plan produced a field the pre-check rejected");
            }
        }

        [Fact]
        public void CouldHaveField_RejectsANodeWithNoDeposit()
        {
            FactionTuning tuning = BuildTuning();

            Assert.False(AsteroidFieldPlanner.CouldHaveField(Node(1.0), NodeZone.DeepSpace, tuning));
            Assert.True(AsteroidFieldPlanner.CouldHaveField(Node(1.0, "Iron"), NodeZone.DeepSpace, tuning));
        }

        // Deliberately independent of ResourceValue: the pre-check is necessary, not sufficient, and
        // Count still scales with RV.
        [Fact]
        public void CouldHaveField_DoesNotDependOnResourceValue()
        {
            Assert.True(AsteroidFieldPlanner.CouldHaveField(Node(0.0, "Iron"), NodeZone.DeepSpace, BuildTuning()));
        }

        [Fact]
        public void CouldHaveField_NullTuning_IsRejectedRatherThanThrowing()
        {
            Assert.False(AsteroidFieldPlanner.CouldHaveField(Node(1.0, "Iron"), NodeZone.DeepSpace, null));
        }

        [Fact]
        public void RealTuningDefaults_CarryAnAsteroidFieldProfile()
        {
            FactionTuning tuning = TestSupport.Tuning();

            Assert.NotNull(tuning.AsteroidField);
            Assert.True(tuning.AsteroidField.MaxCount > 0, "the shipped profile must plan a field");
            Assert.True(tuning.AsteroidField.SizeMax >= tuning.AsteroidField.SizeMin);
        }
    }
}
