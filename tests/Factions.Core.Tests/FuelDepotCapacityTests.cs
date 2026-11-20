using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The supply half of docs/fuel-plan.md: a FuelDepot can be upgraded, and something asks for the
    /// upgrade. Before this, <c>MaxLevelFor(FuelDepot)</c> was 1 and
    /// <c>PostCapabilityInvestmentRequests</c> treated one installed depot as covering a faction's
    /// whole fuel need, so network gas capacity was fixed at 259.2 ML/day per depot node whatever the
    /// fleet burned.
    /// </summary>
    public class FuelDepotCapacityTests
    {
        private const string HydrogenKey = "Gas:Hydrogen";

        /// <summary>SE's own O2/H2 generator numbers, the pair the live scan resolves: 25 ice a
        /// second in, 500 L of hydrogen out, quoted per the one second they are stated over.</summary>
        private static ConversionRecipe IceToHydrogen()
        {
            return new ConversionRecipe
            {
                BlueprintId = "Gas/OxygenGenerator/Hydrogen",
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Ice", Amount = 25.0 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_GasProperties", SubTypeId = "Hydrogen", Amount = 500.0 } },
                TimeSecs = 1.0f,
            };
        }

        private static ProductionManager Manager()
        {
            return new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.FuelDepot, new List<ConversionRecipe> { IceToHydrogen() } },
            });
        }

        /// <summary>One owned, founded node with <paramref name="depotLevel"/> depot levels, an ice
        /// pile and a measured hydrogen burn.</summary>
        private static SpatialNode Node(WorldModel world, int depotLevel, long ice, double burnUnits)
        {
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            if (depotLevel > 0) node.Capabilities[NodeCapabilityKind.FuelDepot] = depotLevel;
            if (ice > 0) node.Inventory["Ore:" + world.Tuning.IceOreSubTypeId] = ice;
            if (burnUnits > 0)
                FuelDraw.CreditBurn(node, world.UtcNow, world.Tuning, HydrogenKey, burnUnits);
            return node;
        }

        private static WorldModel World()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
            return world;
        }

        private static object MeasureGasStage(ProductionManager manager, WorldModel world,
                                              List<SpatialNode> owned, List<string> log)
        {
            MethodInfo measure = typeof(ProductionManager).GetMethod("MeasureGasStage",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(measure);
            return measure.Invoke(manager, new object[]
            {
                new FactionState { Tag = "F" }, owned, new LiveProductionExecutionContext(world, log), world.Tuning,
            });
        }

        private static double Field(object gas, string name)
        {
            FieldInfo f = gas.GetType().GetField(name);
            Assert.NotNull(f);
            return Convert.ToDouble(f.GetValue(gas));
        }

        /// <summary>Runs the measurement and the decision the way the production pass does, and
        /// returns the THROUGHPUT_INVEST lines.</summary>
        private static List<string> Decide(int depotLevel, long ice, double burnUnits)
        {
            WorldModel world = World();
            ProductionManager manager = Manager();
            var log = new List<string>();
            SpatialNode node = Node(world, depotLevel, ice, burnUnits);
            var owned = new List<SpatialNode> { node };

            object gas = MeasureGasStage(manager, world, owned, log);

            MethodInfo decide = typeof(ProductionManager).GetMethod("DecideThroughputInvestments",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(decide);
            decide.Invoke(manager, new object[]
            {
                world.Tuning, new FactionState { Tag = "F" },
                new HashSet<NodeCapabilityKind>(node.Capabilities.Keys),
                new LiveProductionExecutionContext(world, log),
                0.0, 0.0, 0.0, 0.0, 0.0, 0.0, gas,
            });

            var posts = new List<string>();
            foreach (string line in log)
                if (line.Contains("THROUGHPUT_INVEST") && line.Contains("FuelDepot")) posts.Add(line);
            return posts;
        }

        /// <summary>A level is 6 slots x 500 L/s = 180,000 L/min; the ice that feeds it is
        /// 6 x 25/s = 9,000 units/min. Both are read off the recipe rather than restated, so a
        /// changed recipe moves the test with the code.</summary>
        private const double LevelLitresPerMin = 6 * 500.0 * 60.0;
        private const double LevelIcePerMin    = 6 * 25.0 * 60.0;

        /// <summary>Hydrogen units that, folded into the EMA, leave the node burning
        /// <paramref name="litresPerMin"/>. <c>CreditBurn</c> stores units over the time constant.</summary>
        private static double BurnUnitsFor(WorldModel world, double litresPerMin)
        {
            return litresPerMin * 60.0 * world.Tuning.FuelBurnEmaHours;
        }

        // ---- the ceiling --------------------------------------------------------------------

        [Fact]
        public void ADepotHasAnUpgradeCeilingOfItsOwn()
        {
            var t = new FactionTuning { MaxFuelDepotUpgrades = 4 };
            Assert.Equal(4, t.MaxLevelFor(NodeCapabilityKind.FuelDepot));
        }

        /// <summary>The ceiling is what <see cref="InvestmentTargeting"/> reads to decide whether an
        /// installed depot is still a target. At 1 it never was, so no request could ever land on
        /// the node that already had one — which is the whole estate, once each faction has its
        /// first depot.</summary>
        [Fact]
        public void AnInstalledDepotBelowItsCeilingIsStillAnInvestmentTarget()
        {
            var t = TestSupport.Tuning();
            t.MaxFuelDepotUpgrades = 3;
            var w = new FakeWorld { Treasury = 5_000_000 };
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "DEP");
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.StableStationId = 42;
            n.Capabilities[NodeCapabilityKind.FuelDepot] = 1;

            var req = new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.FuelDepot, Urgency = 0.8,
                ExpiresUtc = new DateTime(2026, 9, 19, 13, 0, 0, DateTimeKind.Utc),
            };

            InvestmentTarget target = InvestmentTargeting.Resolve(w, "F", req, t);
            Assert.True(target.Resolved);
            Assert.Equal("DEP", target.NodeId);

            // The same node at the ceiling is refused, so the resolution above is the ceiling being
            // read and not the gate having been removed.
            n.Capabilities[NodeCapabilityKind.FuelDepot] = 3;
            InvestmentTarget atMax = InvestmentTargeting.Resolve(w, "F", req, t);
            Assert.False(atMax.Resolved);
            Assert.Equal(InvestmentTargetBlock.AtMaxLevel, atMax.Block);
        }

        // ---- the measurement ------------------------------------------------------------------

        /// <summary>Capacity is what is installed, not what is running. Every slot here is idle —
        /// no demand, no recipe held — and the stage must still report a full level's output, or it
        /// would read "capacity is not the constraint" at exactly the moment a fleet is burning more
        /// than the estate can make.</summary>
        [Fact]
        public void CapacityIsReadFromInstalledLevels_NotFromRunningSlots()
        {
            WorldModel world = World();
            var log = new List<string>();
            SpatialNode node = Node(world, depotLevel: 2, ice: 1_000_000, burnUnits: 0);

            object gas = MeasureGasStage(Manager(), world, new List<SpatialNode> { node }, log);

            Assert.Equal(2 * LevelLitresPerMin, Field(gas, "CapacityPerMin"), 3);
            Assert.Equal(LevelIcePerMin, Field(gas, "IcePerLevelPerMin"), 3);
            Assert.Equal(1_000_000.0, Field(gas, "IceUnits"), 3);
            Assert.Equal(0.0, Field(gas, "BurnPerMin"), 3);
        }

        [Fact]
        public void BurnIsTheNodesOwnMeasuredRate()
        {
            WorldModel world = World();
            var log = new List<string>();
            SpatialNode node = Node(world, depotLevel: 1, ice: 1,
                                    burnUnits: BurnUnitsFor(world, 12_345.0));

            object gas = MeasureGasStage(Manager(), world, new List<SpatialNode> { node }, log);

            Assert.Equal(12_345.0, Field(gas, "BurnPerMin"), 3);
        }

        // ---- the decision ---------------------------------------------------------------------

        [Fact]
        public void BurningMoreThanTheDepotsCanMake_AsksForAnotherLevel()
        {
            // Three times capacity saturates the raw imbalance, and an hour of ice per level takes
            // the damper to 1.00, so the post is the undamped number.
            List<string> posts = Decide(depotLevel: 1,
                                        ice: (long)(LevelIcePerMin * 60),
                                        burnUnits: BurnUnitsFor(World(), 3 * LevelLitresPerMin));

            Assert.Single(posts);
            Assert.Contains("urgency=1.00", posts[0]);
            Assert.Contains("gas burn/capacity=3.00", posts[0]);
            Assert.Contains("fuel=" + HydrogenKey, posts[0]);
        }

        /// <summary>The fixed point. Capacity is a property of what is built, so each level added
        /// raises it and the urgency falls — unlike a reading taken from running slots, where the new
        /// machines idle for the same reason the old ones did and the ratio never moves.</summary>
        [Fact]
        public void CapacityThatCoversTheBurn_AsksForNothing()
        {
            List<string> posts = Decide(depotLevel: 4,
                                        ice: (long)(LevelIcePerMin * 60),
                                        burnUnits: BurnUnitsFor(World(), 3 * LevelLitresPerMin));

            Assert.Empty(posts);
        }

        /// <summary>A depot converts ice. With none held, another one converts nothing, and asking
        /// for it would take a Develop tick from whatever would have fixed the real shortage.</summary>
        [Fact]
        public void NoIce_AsksForNothing()
        {
            List<string> posts = Decide(depotLevel: 1, ice: 0,
                                        burnUnits: BurnUnitsFor(World(), 3 * LevelLitresPerMin));

            Assert.Empty(posts);
        }

        /// <summary>Ice cover grounds the urgency: a tenth of an hour's feed for one level asks at a
        /// tenth. Without it a faction three orders of magnitude short of its own burn would post a
        /// flat 1.00 every cycle forever.</summary>
        [Fact]
        public void TheUrgencyIsScaledByTheIceActuallyHeld()
        {
            List<string> tenth = Decide(depotLevel: 1,
                                        ice: (long)(LevelIcePerMin * 6),          // 0.1 h of one level
                                        burnUnits: BurnUnitsFor(World(), 3 * LevelLitresPerMin));

            Assert.Single(tenth);
            Assert.Contains("urgency=0.10", tenth[0]);
            Assert.Contains("ice_cover=0.10", tenth[0]);
        }

        /// <summary>No depot anywhere is the capability path's case, not this one: it posts the
        /// structural 0.90 request off the gas demand itself. This stage only ever asks for more.</summary>
        [Fact]
        public void AFactionWithNoDepotAtAll_IsNotThisStagesCase()
        {
            List<string> posts = Decide(depotLevel: 0,
                                        ice: (long)(LevelIcePerMin * 60),
                                        burnUnits: BurnUnitsFor(World(), 3 * LevelLitresPerMin));

            Assert.Empty(posts);
        }
    }
}
