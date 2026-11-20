using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The minted-income loop: the pure <see cref="Economy"/> formula, and that the brain
    /// actually senses cash flow (bleeding pushes Wealth even when the treasury stock looks fine).</summary>
    public class EconomyTests
    {
        private static SpatialNode Src(double value, double hold)
        {
            return new SpatialNode
            {
                ResourceValue = value,
                HoldStrength = hold,
                OwnerFactionTag = "F",
                StableStationId = 1,
            };
        }

        [Fact]
        public void HeldUncontestedSource_NetsPositive()
        {
            var t = TestSupport.Tuning();
            Assert.True(Economy.SourceNet(Src(0.7, 0.8), 1.0, t) > 0,
                "a well-held, valuable, uncontested source should pay its upkeep and more");
        }

        [Fact]
        public void BesiegedSource_YieldsNothing_AndDrains()
        {
            var t = TestSupport.Tuning();
            // control 0 = a rival dominates the spot -> no yield, only upkeep -> a net drain
            Assert.Equal(0, Economy.SourceYield(Src(1.0, 1.0), 0.0, t));
            Assert.True(Economy.SourceNet(Src(1.0, 1.0), 0.0, t) < 0, "a besieged outpost is a net drain");
        }

        [Fact]
        public void UnfoundedBesiegedClaim_DoesNotBecomeAnEconomicDrain()
        {
            var t = TestSupport.Tuning();
            var claim = new SpatialNode
            {
                ResourceValue = 1.0,
                HoldStrength = 1.0,
                OwnerFactionTag = "F",
                StableStationId = 0,
            };

            Assert.Equal(0, Economy.SourceNet(claim, 0.0, t));
        }

        [Fact]
        public void Yield_ScalesWith_Value_Hold_Control()
        {
            var t = TestSupport.Tuning();
            long full = Economy.SourceYield(Src(1.0, 1.0), 1.0, t);
            Assert.Equal(t.BaseYield, full);                                  // value=hold=control=1 -> exactly BaseYield
            Assert.True(full > Economy.SourceYield(Src(0.5, 0.5), 1.0, t));   // weaker source pays less
            Assert.True(full > Economy.SourceYield(Src(1.0, 1.0), 0.5, t));   // contested source pays less
        }

        // The brain must SEE cash flow: bleeding raises Wealth pressure even at the same (healthy) treasury.
        [Fact]
        public void Bleeding_RaisesWealthPressure_VsHealthyFlow()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            FactionState Sensed(long netFlow)
            {
                var w = new FakeWorld { Treasury = 9_000_000, NetFlow = netFlow }; // near ledger comfort -> stock-Wealth is low
                var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "H");
                home.OwnerFactionTag = "F";
                home.HoldStrength = 0.6;
                var s = new FactionState { Tag = "F", RngSeed = 1 };
                brain.Think(s, Persona(), w, new SeededRandom(1), t);
                return s;
            }

            FactionState bleeding = Sensed(-600_000); // 15 ticks of runway on 9M
            FactionState healthy = Sensed(+6000);
            Assert.True(bleeding.Drives[Drive.Wealth] > healthy.Drives[Drive.Wealth],
                "negative cash flow should raise Wealth pressure even at the same treasury");
        }

        // Cash-flow guard: a faction that is bleeding should not take on NEW ground (no Expand/Claim).
        [Fact]
        public void Bleeding_DoesNotExpand()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 180000, NetFlow = -6000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "H");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.6;
            var frontier = w.Graph.Add(new Vec3(1000, 0, 0), 0.9, "A"); // tempting frontier
            w.Graph.Connect("H", "A");

            var s = new FactionState { Tag = "F", RngSeed = 3 };
            Decision d = brain.Think(s, Persona(), w, new SeededRandom(3), t);

            Assert.NotEqual(ActionKind.Expand, d.Intent.Kind);
            Assert.NotEqual(ActionKind.Claim, d.Intent.Kind);   // reinforcing the base or idling is fine; growing is not
        }

        // Consolidation: a flush faction with a developable (controlled, sub-max) source considers Develop.
        [Fact]
        public void Develop_IsACandidate_WhenHeldSourceHasHeadroom()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 300000, NetFlow = 5000 };
            var home = w.Graph.Add(new Vec3(0, 0, 0), 0.4, "H");   // value 0.4 -> headroom to develop
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.9;
            home.StableStationId = 1;   // must be founded before Develop is offered (capabilities are station sub-modules)

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(), w, new SeededRandom(1), t);

            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Develop);
        }

        // Consolidation: a broke faction sheds a net-negative source (yield < upkeep) — the shed costs 0.
        [Fact]
        public void NetNegativeSource_IsWithdrawn_WhenBroke()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 1000, NetFlow = -1100 }; // can't afford anything that costs
            var drain = w.Graph.Add(new Vec3(0, 0, 0), 0.05, "D");      // tiny value -> yield < upkeep -> drain
            drain.OwnerFactionTag = "F";
            drain.HoldStrength = 0.5;
            drain.StableStationId = 1;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.01), w, new SeededRandom(1), t); // low temp -> deterministic pick

            Assert.Equal(ActionKind.Withdraw, d.Intent.Kind);   // shedding the drain costs 0 and stops the bleed
        }

        [Fact]
        public void MarginalScanBackedDrain_IsNotWithdrawn_WhileStillGrowing()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 10_000_000, NetFlow = -4500 };
            var drain = w.Graph.Add(new Vec3(0, 0, 0), 0.2, "E884476593655831594");
            drain.OwnerFactionTag = "F";
            drain.HoldStrength = 0.7;
            drain.StableStationId = 884476593655831594;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Settler(), w, new SeededRandom(1), t);

            Assert.NotEqual(ActionKind.Withdraw, d.Intent.Kind);
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Withdraw);
        }

        [Fact]
        public void LowValueDrain_IsWithdrawn_BeforeDevelopedHold()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 800_000, Ledger = 2_000_000, NetFlow = -100_000 };
            var rich = w.Graph.Add(new Vec3(0, 0, 0), 0.95, "R");
            rich.OwnerFactionTag = "F";
            rich.HoldStrength = 0.8;
            rich.StableStationId = 1;
            rich.Capabilities[NodeCapabilityKind.Refinery] = 1;

            var poor = w.Graph.Add(new Vec3(1000, 0, 0), 0.05, "P");
            poor.OwnerFactionTag = "F";
            poor.HoldStrength = 0.8;
            poor.StableStationId = 1;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.0), w, new SeededRandom(1), t);

            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Withdraw && c.Intent.TargetNodeId == poor.Id);
            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Withdraw && c.Intent.TargetNodeId == rich.Id);
        }

        [Fact]
        public void HealthyLedger_DoesNotWithdrawOnlyDevelopedNode()
        {
            var brain = new FactionBrain();
            var t = TestSupport.Tuning();

            var w = new FakeWorld { Treasury = 2_000_000, Ledger = 20_000_000, NetFlow = -100_000 };
            var node = w.Graph.Add(new Vec3(0, 0, 0), 0.15, "S");
            node.OwnerFactionTag = "F";
            node.HoldStrength = 0.8;
            node.StableStationId = 1;
            node.Capabilities[NodeCapabilityKind.Refinery] = 1;

            var s = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = brain.Think(s, Persona(0.0), w, new SeededRandom(1), t);

            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Withdraw);
        }

        private static Personality Persona(double temp = 0.5)
        {
            var p = new Personality { Temperature = temp };
            p.Weights[Drive.Security] = 1; p.Weights[Drive.Growth] = 1; p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Rivalry] = 1; p.Weights[Drive.Caution] = 1;
            return p;
        }

        private static Personality Settler()
        {
            return Personalities.Get("settler");
        }
    }
}
