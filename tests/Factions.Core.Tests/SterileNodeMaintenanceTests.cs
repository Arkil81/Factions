using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Ground that mints nothing is neither maintained nor retaken.
    ///
    /// <para>Holding a node costs <see cref="FactionTuning.ReinforceCost"/> a pass, and that cost is
    /// invisible to <see cref="Economy.SourceNet"/> — only a physical station pays upkeep there — so a
    /// stationless source that produces nothing reads net 0, never becomes a drain, and is reinforced
    /// forever. Live 2026-08-30: 488 of 1,189 owned nodes minted nothing at all and took 42.6% of the
    /// day's reinforce spend.</para></summary>
    public class SterileNodeMaintenanceTests
    {
        // ---- the claim MintsNothing rests on -------------------------------------------------

        /// <summary>Both readings at zero is a hard zero on every channel, not a small number — the
        /// property <see cref="Economy.MintsNothing"/> asserts on the node's behalf. Stated at full
        /// hold and uncontested control, so the two node quantities are the only things zeroing the
        /// output. If a future yield channel stops scaling on one of them, this is what says so.
        ///
        /// <para>Since FAC-219 that is two quantities and the predicate is a conjunction, so this
        /// states the sterile case over ground that really is sterile: no deposit, and a site worth
        /// nothing. Which is also the only shape the live world produces — <c>ResourceValueCap</c>
        /// returns 0 for open space with no ore, and ore under a node lifts the cap to 1.0.</para></summary>
        [Fact]
        public void BothReadingsZero_ZeroesEveryYieldChannel()
        {
            FactionTuning t = TestSupport.Tuning();
            var n = new SpatialNode { ResourceValue = 0.0, Richness = 0.0, HoldStrength = 1.0, OwnerFactionTag = "F" };

            Assert.Equal(0, Economy.SourceYield(n, 1.0, t));
            foreach (ItemKind kind in FactionTuning.AllItems)
                Assert.Equal(0, Economy.SourceItemYield(n, 1.0, t, kind));
            Assert.True(Economy.MintsNothing(n));

            // ...and the same node with worth is not sterile, so the predicate is not vacuous.
            n.ResourceValue = 0.5;
            Assert.False(Economy.MintsNothing(n));
            Assert.True(Economy.SourceYield(n, 1.0, t) > 0
                        || FactionTuning.AllItems.Any(k => Economy.SourceItemYield(n, 1.0, t, k) > 0),
                "a node with worth must mint on some channel, or the sterile predicate separates nothing");
        }

        /// <summary>Neither reading alone makes a node sterile, and each one alone still mints on its
        /// own channel. The conjunction in <see cref="Economy.MintsNothing"/> is load-bearing in both
        /// directions: as a disjunction it would condemn ore-bearing rock in open space for being
        /// worth nothing to stand on, and buildable ore-less ground for having no seam — and the
        /// second of those is 42.6% of a day's reinforce spend's worth of nodes away from what this
        /// predicate was built to find.</summary>
        [Fact]
        public void EitherReadingAlone_IsNotSterile_AndStillMintsOnItsOwnChannel()
        {
            FactionTuning t = TestSupport.Tuning();

            // Ore-bearing rock in open space: no site value, but there is a seam to work.
            var seam = new SpatialNode { ResourceValue = 0.0, Richness = 0.6, HoldStrength = 1.0, OwnerFactionTag = "F" };
            TestSupport.WithOre(seam, "Iron", "Gold");
            seam.Richness = 0.6;   // after WithOre, which derives its own
            Assert.False(Economy.MintsNothing(seam));
            Assert.True(Economy.SourceItemYield(seam, 1.0, t, ItemKind.Ore) > 0, "a worked seam mints ore");
            Assert.Equal(0, Economy.SourceYield(seam, 1.0, t));

            // Buildable ground with no seam under it: somewhere to stand, nothing to mine.
            var site = new SpatialNode { ResourceValue = 0.8, Richness = 0.0, HoldStrength = 1.0, OwnerFactionTag = "F" };
            Assert.False(Economy.MintsNothing(site));
            Assert.True(Economy.SourceYield(site, 1.0, t) > 0, "a site worth standing on scores above zero");
            Assert.Equal(0, Economy.SourceItemYield(site, 1.0, t, ItemKind.Ore));
        }

        /// <summary>Worth is a property of the ground, not of the contest: a valuable source pinned
        /// at zero control is worth fighting for, and must not read as sterile.</summary>
        [Fact]
        public void ABesiegedValuableSource_IsNotSterile()
        {
            var n = new SpatialNode { ResourceValue = 0.9, HoldStrength = 0.05, OwnerFactionTag = "F" };
            Assert.Equal(0, Economy.SourceYield(n, 0.0, TestSupport.Tuning()));
            Assert.False(Economy.MintsNothing(n));
        }

        // ---- FleetManager: the spend --------------------------------------------------------

        private static FactionTuning FleetTuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.ReinforceCost = 500;
            t.ReinforceStep = 0.1;
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0;
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            return t;
        }

        private static SpatialNode Owned(WorldModel w, string id, Vec3 pos, double value, double hold = 0.5)
        {
            SpatialNode n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        [Fact]
        public void FleetManager_WontSpendOnGroundThatMintsNothing_AndStillSpendsOnGroundThatDoes()
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("F", 10_000_000);
            FactionTuning t = FleetTuning();

            SpatialNode sterile = Owned(w, "N001", new Vec3(0, 0, 0), value: 0.0);
            SpatialNode worthy = Owned(w, "N002", new Vec3(100, 0, 0), value: 0.6);
            // The reinforce is a flying mission and never starts at its own destination, so the
            // sterile node doubles as the depot -- which is also the sharper fixture: it proves the
            // worthy node was served from ground the faction refuses to spend on.
            ReinforceMissionSupport.Stock(w, sterile, 4);

            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = sterile.Id, Score = 9.0 });
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = worthy.Id, Score = 1.0 });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            new FleetManager().Tick(states, w, t, log);

            Assert.DoesNotContain(log, l => l.Contains("REINFORCE_DISPATCH") && l.Contains("-> " + sterile.Id));
            Assert.Contains(log, l => l.Contains("REINFORCE_DISPATCH") && l.Contains("-> " + worthy.Id));

            ReinforceMissionSupport.FlyFleets(w, t, states, log);
            Assert.Equal(0.5, sterile.HoldStrength, 6);
            Assert.Equal(0.6, worthy.HoldStrength, 6);
        }

        /// <summary>The skip is reported even when nothing else is wrong. It ranks ahead of the
        /// treasury and budget gates on purpose — a faction rich enough to afford the spend is
        /// exactly the one that otherwise pays it forever — so a solvent faction is the case that
        /// has to produce the line.</summary>
        [Fact]
        public void FleetManager_ReportsTheSkip_ForASolventFaction()
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("F", 10_000_000);
            FactionTuning t = FleetTuning();

            SpatialNode sterile = Owned(w, "N001", new Vec3(0, 0, 0), value: 0.0);
            var st = new FactionState { Tag = "F", RngSeed = 1 };
            st.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = sterile.Id, Score = 1.0 });

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            string line = Assert.Single(log.Where(l => l.Contains("REINFORCE_SKIPPED")));
            Assert.Contains("no_worth=1", line);
            Assert.Contains("no_money=0", line);
            Assert.Contains("no_budget=0", line);
            Assert.Contains("paid_no_hull=0", line);
        }

        // ---- Brain: the retake --------------------------------------------------------------

        private static Personality Grower()
        {
            var p = new Personality
            {
                Temperature = 0.0,
                Traits = new Traits { Xenophobia = 0.1, Expansionism = 1.0, Mercantilism = 0.5, Aggression = 0.1 },
            };
            p.Weights[Drive.Growth] = 2.0;
            return p;
        }

        private static FactionTuning BrainTuning()
        {
            return new FactionTuning
            {
                ClaimCost = 10_000,
                ExpandCost = 50_000,
                InsolvencyFloor = 1,
                ReinforceCost = 10_000,
                GuardCost = 5_000,
                ComfortLedger = 1_000_000,
                IdleUtility = 0.15,
            };
        }

        [Fact]
        public void ASterileDerelict_IsNotWorthRetaking()
        {
            var w = new FakeWorld { Treasury = 500_000 };
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "HOME");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;
            SpatialNode sterile = w.Graph.Add(new Vec3(40, 0, 0), 0.0, "N001");
            sterile.OwnerFactionTag = NeutralFaction.Tag;
            SpatialNode worthy = w.Graph.Add(new Vec3(80, 0, 0), 0.4, "N002");
            worthy.OwnerFactionTag = NeutralFaction.Tag;
            w.Graph.Connect(home.Id, sterile.Id);
            w.Graph.Connect(home.Id, worthy.Id);

            var state = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = new FactionBrain().Think(state, Grower(), w, new SeededRandom(1), BrainTuning());

            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Claim && c.Intent.TargetNodeId == sterile.Id);
            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Claim && c.Intent.TargetNodeId == worthy.Id);
        }

        /// <summary>A reachable derelict suppresses Expand — reclaim before growth. A sterile one
        /// must not, or a faction ringed by ground it will never retake stops growing entirely
        /// while waiting on a Claim it never makes.</summary>
        [Fact]
        public void SterileDerelicts_DoNotSuppressExpansion()
        {
            var w = new FakeWorld { Treasury = 500_000 };
            SpatialNode home = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "HOME");
            home.OwnerFactionTag = "F";
            home.HoldStrength = 0.8;
            SpatialNode sterile = w.Graph.Add(new Vec3(40, 0, 0), 0.0, "N001");
            sterile.OwnerFactionTag = NeutralFaction.Tag;
            w.Graph.Connect(home.Id, sterile.Id);

            var state = new FactionState { Tag = "F", RngSeed = 1 };
            Decision d = new FactionBrain().Think(state, Grower(), w, new SeededRandom(1), BrainTuning());

            Assert.DoesNotContain(d.Scored, c => c.Intent.Kind == ActionKind.Claim);
            Assert.Contains(d.Scored, c => c.Intent.Kind == ActionKind.Expand);
        }
    }
}
