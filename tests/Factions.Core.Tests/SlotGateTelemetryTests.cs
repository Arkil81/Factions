using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// `DebugTraces.SlotGate` (`factions debug slots`) — why a bank left machines idle. Four independent
    /// gates produce that outcome and none was observable, so the attribution had been done by hand
    /// against live brains twice. These pin the two things a hand-attribution kept needing: that the
    /// reasons are told apart, and that a saturated bank stays silent.
    /// </summary>
    [Collection(DebugTracesCollection.Name)]
    public sealed class SlotGateTelemetryTests : IDisposable
    {
        /// <summary>Per-process static: a test that flips it and throws would leak it into whatever
        /// xUnit runs next. Dispose runs after every test in this class.</summary>
        public void Dispose() { DebugTraces.ResetToDefaults(); }

        private static ConversionRecipe IronIngot()
        {
            return new ConversionRecipe
            {
                BlueprintId = "IronOreToIngot", TimeSecs = 1f,
                Inputs  = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ore",   SubTypeId = "Iron", Amount = 1 } },
                Outputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Ingot", SubTypeId = "Iron", Amount = 1 } },
            };
        }

        private static Dictionary<NodeCapabilityKind, List<ConversionRecipe>> Refinery()
        {
            return new Dictionary<NodeCapabilityKind, List<ConversionRecipe>>
            {
                { NodeCapabilityKind.Refinery, new List<ConversionRecipe> { IronIngot() } },
            };
        }

        /// <summary>Answers whatever the test needs it to, so the scarcity gate's two rejection
        /// states can be produced on demand. The real <see cref="PriceOracle"/> reaches both, but
        /// only through node capability/inventory arithmetic that would make it unclear which of the
        /// two a failing assertion was actually about.</summary>
        private sealed class StubOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            internal double Scarcity = 1.0;
            internal long Reference;
            public double ScarcityMultiplier(string nodeId, string inventoryKey) { return Scarcity; }
            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference)
            { reference = Reference; return Reference > 0; }
            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            { reference = Reference; return Reference > 0; }
        }

        private static List<string> Run(bool logSlotGate, long oreOnHand, bool postDemand,
                                        IPriceOracle oracle = null)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 1.0;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;
            if (oreOnHand > 0) node.Inventory["Ore:Iron"] = oreOnHand;
            if (postDemand)
                world.Demand.Post(new DemandEntry
                {
                    FactionTag = "F", Kind = "Item:Ingot:Iron", NodeId = "N1",
                    Quantity = 100000, ExpiresUtc = world.UtcNow.AddHours(1),
                });

            DebugTraces.SlotGate = logSlotGate;
            var manager = new ProductionManager(Refinery(), null, oracle);
            var states = new List<FactionState> { new FactionState { Tag = "F" } };
            var log = new List<string>();
            world.UtcNow = world.UtcNow.AddSeconds(30);
            manager.Tick(states, world, world.Tuning, log);

            var gate = new List<string>();
            foreach (string l in log) if (l.Contains("SLOT_GATE")) gate.Add(l);
            return gate;
        }

        [Fact]
        public void OffByDefault_EmitsNothing()
        {
            // The toggle defaults false and this trace is per bank per tick; a regression that made
            // it unconditional would be a log-volume defect, not a cosmetic one.
            Assert.Empty(Run(logSlotGate: false, oreOnHand: 500, postDemand: false));
        }

        [Fact]
        public void NobodyAskedForTheOutput_IsReportedAsNodemand_NotAsMissingInput()
        {
            // Ore on hand, no demand. These are opposite findings -- "the bank is starved" would send
            // someone to look at haulage, and the answer is that nothing asked.
            List<string> gate = Run(logSlotGate: true, oreOnHand: 500, postDemand: false);

            Assert.Single(gate);
            Assert.Contains("nodemand=1", gate[0]);
            Assert.Contains("noinput=0", gate[0]);
            Assert.Contains("awarded=0", gate[0]);
            Assert.Contains("cap=Refinery", gate[0]);
            Assert.Contains("node=N1", gate[0]);
        }

        [Fact]
        public void DemandedButNoInput_IsReportedAsNoinput_NotAsNodemand()
        {
            // The mirror case, and the one that distinguishes a starved bank from an unasked one.
            List<string> gate = Run(logSlotGate: true, oreOnHand: 0, postDemand: true);

            Assert.Single(gate);
            Assert.Contains("nodemand=0", gate[0]);
            Assert.Contains("noinput=1", gate[0]);
        }

        [Fact]
        public void MeasuredGlut_IsReportedSeparatelyFromNoOpinion()
        {
            // Oracle has a reference for the output and the node is over it -- a real, measured glut.
            // This is the half of `oversupplied` that means "stop making this".
            var oracle = new StubOracle { Scarcity = 0.5, Reference = 500 };
            List<string> gate = Run(logSlotGate: true, oreOnHand: 500, postDemand: true, oracle: oracle);

            Assert.Single(gate);
            Assert.Contains("oversupplied=1", gate[0]);
            Assert.Contains("glut=1", gate[0]);
            Assert.Contains("noopinion=0", gate[0]);
        }

        [Fact]
        public void NoReferenceStock_IsReportedAsNoOpinion_NotAsGlut()
        {
            // The mirror: the oracle holds no reference for this output, so its 1.0 is a default and
            // not a measurement. Same `oversupplied` total, opposite meaning and opposite fix -- this
            // one says the item is unpriced, not that the shelves are full.
            var oracle = new StubOracle { Scarcity = 1.0, Reference = 0 };
            List<string> gate = Run(logSlotGate: true, oreOnHand: 500, postDemand: true, oracle: oracle);

            Assert.Single(gate);
            Assert.Contains("oversupplied=1", gate[0]);
            Assert.Contains("glut=0", gate[0]);
            Assert.Contains("noopinion=1", gate[0]);
        }

        [Fact]
        public void RejectionReportsTheUnflooredMultiplier_NotTheClampedOne()
        {
            // FAC-231 Finding 2 read 107 of 107 rejections at exactly 1.00 and took the constant for
            // a signal about PriceOracle. It was a tautology: ScarcityPriority floors at 1.0 and the
            // gate rejects on <= 1.0, so a rejected recipe COULD NOT report anything else. This is
            // the assertion that fails if the reported figure ever goes back through that clamp.
            var oracle = new StubOracle { Scarcity = 0.5, Reference = 500 };
            List<string> gate = Run(logSlotGate: true, oreOnHand: 500, postDemand: true, oracle: oracle);

            Assert.Single(gate);
            // Ordinal on both halves. xUnit's default is CurrentCulture, and this repo has already
            // shipped a negative string assertion that was green under the container's
            // invariant-globalization build and red on the Windows box for exactly that reason.
            Assert.Contains("worstOversupplied=IronOreToIngot@0.50", gate[0], StringComparison.Ordinal);
            Assert.DoesNotContain("@1.00", gate[0], StringComparison.Ordinal);
        }

        [Fact]
        public void ASaturatedBankSaysNothing()
        {
            // The volume guard: the line is emitted only when machines were actually left idle, so
            // the trace scales with the problem rather than with the number of banks in the world.
            List<string> gate = Run(logSlotGate: true, oreOnHand: 1000000, postDemand: true);

            Assert.Empty(gate);
        }
    }
}
