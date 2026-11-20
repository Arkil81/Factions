using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Investment urgency is a measurement, not a constant. `InvestmentRequest.Urgency` has always
    /// documented itself as "computed by the posting manager from stall duration or shortfall
    /// magnitude"; every poster sent a fixed number, and a flat throughput 1.0 wins every Develop
    /// tick on some fresh node forever, because the request is re-posted each tick and
    /// `InvestmentTargeting` only skips nodes already at max level.
    /// </summary>
    public sealed class InvestmentUrgencyTests
    {
        private static double Imbalance(double income, double consumed)
        {
            MethodInfo m = typeof(ProductionManager).GetMethod("ImbalanceUrgency",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(m);
            return (double)m.Invoke(null, new object[] { income, consumed });
        }

        [Theory]
        [InlineData(100, 100, 0.0)]     // parity: nothing to ask for
        [InlineData(103, 100, 0.03)]    // barely oversupplied: barely asks
        [InlineData(150, 100, 0.50)]
        [InlineData(200, 100, 1.00)]    // twice consumption saturates
        [InlineData(1000, 100, 1.00)]   // and stays saturated
        [InlineData(50, 100, 0.0)]      // undersupplied: the caller's gate blocks this anyway
        public void Imbalance_IsTheRatioAboveParity_ClampedAtDouble(double income, double consumed, double expected)
        {
            Assert.Equal(expected, Imbalance(income, consumed), 3);
        }

        [Fact]
        public void NothingConsumingAtAll_IsTheStrongestImbalance_NotAnUndefinedRatio()
        {
            // Stage 2 deliberately fires when ingotConsumedPerMin is 0 -- an idle Manufactory falling
            // behind refinery output is a stronger signal, not a reason to suppress. A naive
            // income/consumed would divide by zero here.
            Assert.Equal(1.0, Imbalance(500, 0), 3);
            Assert.Equal(1.0, Imbalance(1, 0), 3);
        }

        [Fact]
        public void AMildImbalanceRanksBelowAStructuralGap()
        {
            // The whole point of grounding it: a faction 3% oversupplied must stop outranking a
            // faction that cannot produce the item at all. 0.90 is the capability-gap constant.
            Assert.True(Imbalance(103, 100) < 0.90);
            Assert.True(Imbalance(150, 100) < 0.90);
            // ...and a genuinely severe one still outranks it, which is also intended.
            Assert.True(Imbalance(200, 100) > 0.90);
        }

        // ---- the register keeps the strongest claim within a tick -------------------------------

        private static InvestmentRequest Req(string tag, NodeCapabilityKind cap, double urgency, DateTime expires)
        {
            return new InvestmentRequest { FactionTag = tag, Cap = cap, Urgency = urgency, ExpiresUtc = expires };
        }

        [Fact]
        public void SameTick_TheStrongerClaimSurvivesRegardlessOfPostOrder()
        {
            // Both passes name RefineryII in the same tick for different reasons: nothing can produce
            // the ingot (0.90) and the stage is mildly oversupplied (0.03). Before urgency was
            // measured, ordering was load-bearing and documented as such; a 0.03 posted second would
            // now erase the structural request.
            var reg = new InvestmentRegister();
            DateTime exp = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

            reg.Post(Req("F", NodeCapabilityKind.RefineryII, 0.90, exp));
            reg.Post(Req("F", NodeCapabilityKind.RefineryII, 0.03, exp));
            Assert.Equal(0.90, Only(reg, "F").Urgency, 3);

            var reversed = new InvestmentRegister();
            reversed.Post(Req("F", NodeCapabilityKind.RefineryII, 0.03, exp));
            reversed.Post(Req("F", NodeCapabilityKind.RefineryII, 0.90, exp));
            Assert.Equal(0.90, Only(reversed, "F").Urgency, 3);
        }

        [Fact]
        public void ALaterTickReplacesOutright_SoADecayedUrgencyIsNeverPinnedUp()
        {
            // The failure mode max-keeping would otherwise introduce: every poster re-posts each tick
            // AND refreshes the expiry, so keeping the max across ticks would hold the first 1.0 for
            // as long as the need persists -- exactly the flat constant this change removes, with an
            // extra step.
            var reg = new InvestmentRegister();
            DateTime t1 = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
            DateTime t2 = t1.AddSeconds(30);

            reg.Post(Req("F", NodeCapabilityKind.RefineryII, 1.00, t1));
            reg.Post(Req("F", NodeCapabilityKind.RefineryII, 0.05, t2));

            InvestmentRequest r = Only(reg, "F");
            Assert.Equal(0.05, r.Urgency, 3);
            Assert.Equal(t2, r.ExpiresUtc);
        }

        [Fact]
        public void DifferentCapabilities_DoNotShareAnUrgency()
        {
            var reg = new InvestmentRegister();
            DateTime exp = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
            reg.Post(Req("F", NodeCapabilityKind.RefineryII, 0.90, exp));
            reg.Post(Req("F", NodeCapabilityKind.ManufactoryII, 0.05, exp));

            List<InvestmentRequest> all = reg.SnapshotFor("F");
            Assert.Equal(2, all.Count);
            foreach (InvestmentRequest r in all)
                Assert.Equal(r.Cap == NodeCapabilityKind.RefineryII ? 0.90 : 0.05, r.Urgency, 3);
        }

        // ---- utilisation scaling ----------------------------------------------------------------

        private static double Utilisation(ProductionManager m, List<ThroughputSlotRef2> slots,
            HashSet<NodeCapabilityKind> caps, List<SpatialNode> nodes)
        {
            MethodInfo mi = typeof(ProductionManager).GetMethod("StageUtilisation",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            Type slotType = typeof(ProductionManager).Assembly.GetType("Factions.Core.ProductionManager+ThroughputSlotRef", false)
                            ?? mi.GetParameters()[0].ParameterType.GetGenericArguments()[0];
            var list = (System.Collections.IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(slotType));
            foreach (ThroughputSlotRef2 sr in slots)
            {
                object o = Activator.CreateInstance(slotType);
                slotType.GetField("CapIdx", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(o, sr.CapIdx);
                list.Add(o);
            }
            return (double)mi.Invoke(m, new object[] { list, caps, nodes });
        }

        /// <summary>Stand-in for the manager's internal slot ref, which the test cannot name.</summary>
        internal struct ThroughputSlotRef2 { public int CapIdx; }

        private static SpatialNode NodeWith(NodeCapabilityKind cap, int level)
        {
            var n = new SpatialNode { Id = "N" + cap + level };
            n.Capabilities[cap] = level;
            return n;
        }

        [Fact]
        public void Utilisation_IsBusySlotsOverInstalledMachines_AndCountsInstalledLevels()
        {
            // Six machines per level, so a level-2 RefineryII is twelve. Live 2026-09-02 this term
            // read 50 busy against 384 installed world-wide -- 13% -- which is why the raw ratio
            // asks for machines the faction already has and cannot fill.
            var m = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            var caps = new HashSet<NodeCapabilityKind> { NodeCapabilityKind.Refinery, NodeCapabilityKind.RefineryII };
            var nodes = new List<SpatialNode> { NodeWith(NodeCapabilityKind.Refinery, 1), NodeWith(NodeCapabilityKind.RefineryII, 1) };
            var busy = new List<ThroughputSlotRef2>();
            for (int i = 0; i < 6; i++) busy.Add(new ThroughputSlotRef2 { CapIdx = (int)NodeCapabilityKind.Refinery });

            // 6 busy / (6 + 6) installed -- FCTM's own ore-stage reading on the day this shipped.
            Assert.Equal(0.5, Utilisation(m, busy, caps, nodes), 3);
        }

        [Fact]
        public void Utilisation_IgnoresSlotsOutsideTheStage()
        {
            // The ore stage must not count assembler slots as its own busy-ness, or a busy
            // Manufactory would mask an idle Refinery.
            var m = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            var refineCaps = new HashSet<NodeCapabilityKind> { NodeCapabilityKind.Refinery };
            var nodes = new List<SpatialNode> { NodeWith(NodeCapabilityKind.Refinery, 1) };
            var busy = new List<ThroughputSlotRef2>
            {
                new ThroughputSlotRef2 { CapIdx = (int)NodeCapabilityKind.ManufactoryII },
                new ThroughputSlotRef2 { CapIdx = (int)NodeCapabilityKind.ManufactoryII },
            };

            Assert.Equal(0.0, Utilisation(m, busy, refineCaps, nodes), 3);
        }

        [Fact]
        public void NothingInstalled_IsNotDilutedToZero()
        {
            // No machines means no dilution to apply; the bootstrap branch owns that case, and
            // returning 0 here would silence a faction that has nothing at all.
            var m = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            Assert.Equal(1.0, Utilisation(m, new List<ThroughputSlotRef2>(),
                new HashSet<NodeCapabilityKind> { NodeCapabilityKind.Refinery }, new List<SpatialNode>()), 3);
        }

        /// <summary>Drives the real decision at fixed rates and a fixed utilisation, and reads the
        /// urgency it posts. Without this, removing the `* utilisation` term from the posting sites
        /// leaves every other test in this file green -- the computation would still be correct and
        /// simply unused, which is exactly the shape of the defect being fixed.</summary>
        private static List<string> PostAt(double income, double consumed, double utilisation)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;

            var manager = new ProductionManager(new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>());
            var state = new FactionState { Tag = "F" };
            var log = new List<string>();
            var ctx = new LiveProductionExecutionContext(world, log);

            MethodInfo decide = typeof(ProductionManager).GetMethod("DecideThroughputInvestments",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(decide);
            decide.Invoke(manager, new object[]
            {
                world.Tuning, state, new HashSet<NodeCapabilityKind>(node.Capabilities.Keys), ctx,
                income, consumed, 0.0, 0.0, utilisation, 1.0, DefaultGasStage(),
            });
            var thr = new List<string>();
            foreach (string l in log) if (l.Contains("THROUGHPUT_INVEST")) thr.Add(l);
            return thr;
        }

        /// <summary>A zeroed gas stage: no burn, no capacity, no ice, so stage 3 posts nothing and
        /// these cases measure the two stages they are about.</summary>
        private static object DefaultGasStage()
        {
            Type gas = typeof(ProductionManager).GetNestedType("GasStageRates", BindingFlags.NonPublic);
            Assert.NotNull(gas);
            return Activator.CreateInstance(gas);
        }

        [Fact]
        public void ThePostedUrgencyIsScaledByUtilisation_NotJustTheRatio()
        {
            // 2x consumption saturates the raw imbalance at 1.00; a quarter of the machines running
            // must take the posted number to 0.25. This is FCTM's live shape on the day it shipped:
            // in/out well past 2.0, 6 of 24 refinery machines busy.
            List<string> full = PostAt(200, 100, 1.0);
            List<string> quarter = PostAt(200, 100, 0.25);

            Assert.Contains(full, l => l.Contains("urgency=1.00"));
            Assert.Contains(quarter, l => l.Contains("urgency=0.25"));
            Assert.DoesNotContain(quarter, l => l.Contains("urgency=1.00"));
            // The line carries the term it was scaled by, so a posted urgency stays checkable.
            Assert.Contains(quarter, l => l.Contains("util=0.25"));
        }

        [Fact]
        public void AStageWithNoMachinesRunningAsksForNothing()
        {
            // The deliberate reversal: stage 2's older note called an idle Manufactory "a stronger
            // signal". A bank running no recipes cannot use another machine -- something else is
            // blocking it, and asking for capacity buries that.
            List<string> idle = PostAt(200, 100, 0.0);
            Assert.Contains(idle, l => l.Contains("urgency=0.00"));
        }

        private static InvestmentRequest Only(InvestmentRegister reg, string tag)
        {
            List<InvestmentRequest> all = reg.SnapshotFor(tag);
            Assert.Single(all);
            return all[0];
        }
    }
}
