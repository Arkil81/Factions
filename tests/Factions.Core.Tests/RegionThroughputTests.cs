using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The per-region ore ledger and its one rule: a region holding founded stations and a hoard of
    /// ore, with no refining installed in it, asks for a Refinery THERE. The network-wide stage
    /// cannot see this case at all -- it sums ore income over the whole estate against refinery
    /// consumption over the whole estate, so FCTM's five refinery-less nests holding four to six
    /// million ore each read as <c>urgency=0.00</c> on 2026-09-05.
    /// </summary>
    public sealed class RegionThroughputTests
    {
        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> OneStageRecipe()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                [NodeCapabilityKind.Refinery] = new List<ConversionRecipe>
                {
                    new ConversionRecipe
                    {
                        BlueprintId = "IronIngot",
                        TimeSecs = 1,
                        Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore", SubTypeId = "Iron", Amount = 1 } },
                        Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
                    },
                },
            };
        }

        private static SpatialNode Node(WorldModel w, string id, string feature, long ore, bool founded = true)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            n.StableStationId = founded ? 1 : 0;
            n.FeatureKey = feature;
            if (ore > 0) n.Inventory["Ore:Iron"] = ore;
            return n;
        }

        /// <summary>FCTM's shape, small: a home nest that refines and holds ore, a far nest that only
        /// holds ore, and a node in open space that belongs to no region.</summary>
        private static WorldModel TwoNests(long farOre = 200_000, bool farFounded = true, long voidOre = 0)
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode home = Node(w, "HOME", "Cinder Nest", 600_000);
            home.Capabilities[NodeCapabilityKind.Refinery] = 1;
            Node(w, "FAR1", "Far Nest Umbra", farOre / 2, farFounded);
            Node(w, "FAR2", "Far Nest Umbra", farOre - farOre / 2, farFounded);
            Node(w, "VOID", null, voidOre);
            return w;
        }

        private static List<string> Pass(ProductionManager mgr, WorldModel w)
        {
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { new FactionState { Tag = "F" } }, w, w.Tuning, log);
            return log;
        }

        private static List<InvestmentRequest> RegionRefineryRequests(WorldModel w)
        {
            return w.Investments.SnapshotFor("F")
                .FindAll(r => r.Cap == NodeCapabilityKind.Refinery && !NodeRegion.Of(r).IsNone);
        }

        private static List<string> RegionLines(List<string> log)
        {
            List<string> lines = log.FindAll(l => l.Contains("REGION_THROUGHPUT"));
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        [Fact]
        public void AHoardingRegionWithNoRefining_AsksForARefineryThere_AndTheRefiningOneDoesNot()
        {
            WorldModel w = TwoNests(farOre: 200_000, voidOre: 1_000_000);
            List<string> log = Pass(new ProductionManager(OneStageRecipe()), w);

            InvestmentRequest req = Assert.Single(RegionRefineryRequests(w));
            Assert.Equal(NodeRegion.Feature("Far Nest Umbra"), NodeRegion.Of(req));
            // Urgency is the region's share of the faction's ore: 200k of 800k. The open-space node's
            // million is in no region and must not be in the denominator.
            Assert.Equal(0.25, req.Urgency, 3);

            string line = Assert.Single(RegionLines(log));
            Assert.Contains("F REGION_THROUGHPUT feature=Far Nest Umbra ore=200000 trend=n/a", line);
            Assert.Contains(" refine=0 ", line);
            Assert.Contains(" share=0.25 ", line);
            Assert.Contains(" nodes=2 posted", line);
            Assert.DoesNotContain(log, l => l.Contains("REGION_THROUGHPUT feature=Cinder Nest"));
        }

        [Fact]
        public void ARegionBelowTheFloor_IsNotACandidate_AndPrintsNothing()
        {
            WorldModel w = TwoNests(farOre: 20_000);
            Assert.True(w.Tuning.RegionOreHoardFloor > 20_000);
            List<string> log = Pass(new ProductionManager(OneStageRecipe()), w);

            Assert.Empty(RegionRefineryRequests(w));
            Assert.Empty(RegionLines(log));
        }

        [Fact]
        public void ARegionWithNoFoundedStation_IsRefused_AndSaysWhy()
        {
            // The same reason ShipyardCoverage refuses: a request nothing can place only reserves
            // its BOM out of routable stock.
            WorldModel w = TwoNests(farFounded: false);
            List<string> log = Pass(new ProductionManager(OneStageRecipe()), w);

            Assert.Empty(RegionRefineryRequests(w));
            string line = Assert.Single(RegionLines(log));
            Assert.EndsWith("refused=nowhere", line);
        }

        [Fact]
        public void ARegionBeingDrained_IsRefused_UntilItStopsFalling()
        {
            WorldModel w = TwoNests(farOre: 200_000);
            var mgr = new ProductionManager(OneStageRecipe());
            Pass(mgr, w);                                            // pass 1: posts, no trend yet
            Assert.Single(RegionRefineryRequests(w));

            // Let the request lapse so the register cannot answer for the verdict.
            w.UtcNow = w.UtcNow.AddMinutes(30);
            w.Investments.Prune(w.UtcNow);
            Assert.Empty(RegionRefineryRequests(w));

            w.Graph.Nodes["FAR1"].Inventory["Ore:Iron"] = 60_000;   // 100k -> 60k: a real drain
            List<string> log2 = Pass(mgr, w);
            Assert.Empty(RegionRefineryRequests(w));
            Assert.Contains(RegionLines(log2), l => l.Contains("trend=-40000") && l.EndsWith("refused=draining"));

            // Flat again next pass: the hoard stands and the ask returns.
            w.UtcNow = w.UtcNow.AddMinutes(1);
            List<string> log3 = Pass(mgr, w);
            Assert.Single(RegionRefineryRequests(w));
            Assert.Contains(RegionLines(log3), l => l.Contains("trend=0") && l.EndsWith("posted"));
        }

        [Fact]
        public void AStandingHoard_PrintsOnChange_NotEveryPass()
        {
            WorldModel w = TwoNests();
            var mgr = new ProductionManager(OneStageRecipe());
            List<string> first = Pass(mgr, w);
            List<string> second = Pass(mgr, w);
            List<string> third = Pass(mgr, w);

            Assert.Single(RegionLines(first));                       // posted
            Assert.Single(RegionLines(second));                      // standing (refreshed, not new)
            Assert.EndsWith("standing", RegionLines(second)[0]);
            Assert.Empty(RegionLines(third));                        // no change, no line
        }

        [Fact]
        public void OnceTheRegionRefines_TheEpisodeClosesWithOneLine()
        {
            WorldModel w = TwoNests();
            var mgr = new ProductionManager(OneStageRecipe());
            Pass(mgr, w);
            w.Graph.Nodes["FAR1"].Capabilities[NodeCapabilityKind.Refinery] = 1;
            List<string> log = Pass(mgr, w);

            string line = Assert.Single(RegionLines(log));
            Assert.Contains(" refine=", line);
            Assert.EndsWith("refines", line);
            Assert.Empty(RegionLines(Pass(mgr, w)));
        }

        [Fact]
        public void TheResumableCycle_ReachesTheSameRegionVerdictAsTheSynchronousPass()
        {
            // The ledger is filled across the batched node and slot phases; a cursor that failed to
            // carry across a return would show up as a different verdict here.
            WorldModel cycleWorld = TwoNests();
            var cycleLog = new List<string>();
            var cycleMgr = new ProductionManager(OneStageRecipe());
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            Invoke(cycleMgr, "BeginSweepForTesting", states);
            bool done = false; int guard = 0;
            while (!done && guard++ < 10_000)
                done = (bool)Invoke(cycleMgr, "StepSweepOnceForTesting", states, cycleWorld, cycleWorld.Tuning, cycleLog);

            WorldModel syncWorld = TwoNests();
            var syncLog = new List<string>();
            MethodInfo m = typeof(ProductionManager).GetMethod("RunFactionSynchronously", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(m);
            m.Invoke(new ProductionManager(OneStageRecipe()), new object[] { states[0], syncWorld, syncWorld.Tuning, syncLog, 0 });

            Assert.Equal(RegionLines(syncLog), RegionLines(cycleLog));
            Assert.Single(RegionLines(cycleLog));
            Assert.Equal(NodeRegion.Feature("Far Nest Umbra"), NodeRegion.Of(Assert.Single(RegionRefineryRequests(cycleWorld))));
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.Invoke(target, arguments);
        }
    

        [Fact]
        public void ARegionHoldingATrivialShareOfTheEstate_IsNotACandidate()
        {
            // IGSL, first live pass of v0.174.0: a three-node surface region holding 55,000 ore --
            // 0.2% of its estate -- cleared the absolute floor and was handed a refinery, because a
            // standing request at urgency 0.00 still outbids exploratory development.
            WorldModel w = TwoNests(farOre: 60_000);
            w.Graph.Nodes["HOME"].Inventory["Ore:Iron"] = 6_000_000;   // far share ~1%
            Assert.True(w.Tuning.RegionOreHoardMinShare > 0.01);
            List<string> log = Pass(new ProductionManager(OneStageRecipe()), w);

            Assert.Empty(RegionRefineryRequests(w));
            Assert.Empty(RegionLines(log));
        }
    }
}
