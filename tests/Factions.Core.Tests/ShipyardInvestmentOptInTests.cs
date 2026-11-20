using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The reservation opt-in gate (<see cref="FactionLifecycle.CanPostInvestmentFor"/>) as it applies
    /// to the local-Shipyard request both <see cref="ShipPool.MaybePostLocalShipyardInvestment"/>
    /// overloads post.
    ///
    /// <para>A Shipyard's install BOM is a whole prefab yard (<c>capability_recipes.json</c> maps it to
    /// <c>GE_SmallShipyard_Empty</c>), so one ungated request reserves and demands most of the
    /// component catalogue. Live 2026-09-06, player faction EXP held 2,756 components reserved with
    /// only 260 of them belonging to the one capability its operator opted into — the other 2,496 were
    /// staged toward a yard nobody asked for. See <c>docs/issues/FAC-279</c>.</para>
    ///
    /// <para>Every test here uses the same faction tag, and
    /// <see cref="RefusesEveryBlockedLeg_NotJustTheFirst"/> calls the poster repeatedly with it, on
    /// purpose: the decision is a pure function of the state handed in, and nothing about it may
    /// depend on what an earlier call, an earlier test or an earlier world did.</para>
    /// </summary>
    public class ShipyardInvestmentOptInTests
    {
        private const string Tag = "EXP";
        private const string Planet = "Terra";

        /// <summary>A world whose only region wants a yard: two founded stations on one body's
        /// surface, no Shipyard anywhere, and the Shipyard's prerequisite capability installed
        /// off in the network so <see cref="ShipyardCoverage.Wants"/> has nothing left to refuse on.
        /// </summary>
        private static WorldModel MakeWantingWorld(out SpatialNode sourceNode, out SpatialNode destNode)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            sourceNode = AddNode(world, "Source");
            destNode   = AddNode(world, "Dest");

            SpatialNode manu = AddNode(world, "Manu");
            manu.PlanetId = "Elsewhere";
            manu.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            return world;
        }

        private static SpatialNode AddNode(WorldModel world, string id)
        {
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = Tag;
            n.HoldStrength    = 1.0;
            n.StableStationId = 4001;      // founded -- a capability can only land on a station
            n.PlanetId        = Planet;
            n.Zone            = NodeZone.Surface;
            n.Site            = StationSite.Surface;
            return n;
        }

        private static FactionState Npc()
        {
            return new FactionState { Tag = Tag };
        }

        private static FactionState Player(params NodeCapabilityKind[] optIn)
        {
            var s = new FactionState { Tag = Tag, IsPlayerFaction = true };
            foreach (NodeCapabilityKind cap in optIn) s.ReservationOptIn.Add(cap);
            return s;
        }

        /// <summary>Asserts the fixture actually wants a yard. Without this the "posts nothing"
        /// assertions below would pass for a world that was never going to ask.</summary>
        private static void AssertWantsAYard(WorldModel world)
        {
            int yards, working;
            Assert.True(ShipyardCoverage.WantsAnotherYard(world.Graph.OwnedBy(Tag),
                NodeRegion.Body(Planet, StationSite.Surface), world.Tuning, out yards, out working));
        }

        // ---- WorldModel overload -------------------------------------------------------------

        [Fact]
        public void PlayerFactionWithoutOptIn_AsksForNoYard_AndSaysWhy()
        {
            SpatialNode source, dest;
            WorldModel world = MakeWantingWorld(out source, out dest);
            FactionState st = Player(NodeCapabilityKind.ManufactoryII);
            AssertWantsAYard(world);

            var log = new List<string>();
            ShipPool.MaybePostLocalShipyardInvestment(world, st, source, dest, log, "haul");

            Assert.Empty(world.Investments.SnapshotFor(Tag));
            // Pinned whole: the refusal has to stay greppable as an INVEST_REQUEST -- that keyword is
            // what FactionRuntime filters the Investment trace on -- and has to carry the verdict.
            Assert.Equal(
                "EXP INVEST_REQUEST Shipyard planet=Terra site=Surface yards=0/0 refused=opt-in"
                    + " (local haul unservable Source->Dest)",
                Assert.Single(log));
        }

        [Fact]
        public void RefusesEveryBlockedLeg_NotJustTheFirst()
        {
            // The volume control is DebugTraces.Investment, which an operator can switch off; a
            // refusal reported only once would have to remember that it had, and that memory would
            // outlive the world it was true of.
            SpatialNode source, dest;
            WorldModel world = MakeWantingWorld(out source, out dest);
            FactionState st = Player(NodeCapabilityKind.ManufactoryII);

            var log = new List<string>();
            for (int i = 0; i < 5; i++)
                ShipPool.MaybePostLocalShipyardInvestment(world, st, source, dest, log, "haul");

            Assert.Empty(world.Investments.SnapshotFor(Tag));
            Assert.Equal(5, log.Count);
            Assert.All(log, l => Assert.Contains("refused=opt-in", l));
        }

        [Fact]
        public void OptingInMidRun_StartsAsking_WithNoMemoryOfTheRefusal()
        {
            // Same faction, same world, same nodes -- only the opt-in set moves. The gate reads the
            // state it is handed, so the toggle takes effect on the very next call.
            SpatialNode source, dest;
            WorldModel world = MakeWantingWorld(out source, out dest);
            FactionState st = Player(NodeCapabilityKind.ManufactoryII);

            var log = new List<string>();
            ShipPool.MaybePostLocalShipyardInvestment(world, st, source, dest, log, "haul");
            Assert.Empty(world.Investments.SnapshotFor(Tag));

            st.ReservationOptIn.Add(NodeCapabilityKind.Shipyard);
            ShipPool.MaybePostLocalShipyardInvestment(world, st, source, dest, log, "haul");

            Assert.Single(world.Investments.SnapshotFor(Tag));
            Assert.Equal(2, log.Count);
            Assert.Contains("refused=opt-in", log[0]);
            Assert.DoesNotContain("refused=opt-in", log[1]);
        }

        [Fact]
        public void NpcFaction_StillAsksForItsYard()
        {
            SpatialNode source, dest;
            WorldModel world = MakeWantingWorld(out source, out dest);
            AssertWantsAYard(world);

            var log = new List<string>();
            ShipPool.MaybePostLocalShipyardInvestment(world, Npc(), source, dest, log, "haul");

            List<InvestmentRequest> posted = world.Investments.SnapshotFor(Tag);
            Assert.Single(posted);
            Assert.Equal(NodeCapabilityKind.Shipyard, posted[0].Cap);
            Assert.Equal(Planet, posted[0].TargetPlanetId);
            Assert.Contains(log, l => l.Contains("INVEST_REQUEST Shipyard planet=" + Planet));
            Assert.DoesNotContain(log, l => l.Contains("refused=opt-in"));
        }

        [Fact]
        public void PlayerFactionThatOptedIn_StillAsksForItsYard()
        {
            SpatialNode source, dest;
            WorldModel world = MakeWantingWorld(out source, out dest);
            FactionState st = Player(NodeCapabilityKind.ManufactoryII, NodeCapabilityKind.Shipyard);
            AssertWantsAYard(world);

            var log = new List<string>();
            ShipPool.MaybePostLocalShipyardInvestment(world, st, source, dest, log, "haul");

            List<InvestmentRequest> posted = world.Investments.SnapshotFor(Tag);
            Assert.Single(posted);
            Assert.Equal(NodeCapabilityKind.Shipyard, posted[0].Cap);
            Assert.DoesNotContain(log, l => l.Contains("refused=opt-in"));
        }

        // ---- IOperationsExecutionContext overload ---------------------------------------------
        // The staged Operations cycle posts through its own overload; a gate on only one of the two
        // leaves the Haul and Mine dispatch paths -- the ones that actually fired live -- ungated.

        [Fact]
        public void Staged_PlayerFactionWithoutOptIn_AsksForNoYard_AndSaysWhy()
        {
            SpatialNode source, dest;
            WorldModel world = MakeWantingWorld(out source, out dest);
            FactionState st = Player(NodeCapabilityKind.ManufactoryII);
            AssertWantsAYard(world);

            var log = new List<string>();
            var ctx = new LiveOperationsExecutionContext(world, st, log);
            ShipPool.MaybePostLocalShipyardInvestment(ctx, world.Tuning, st, source, dest, "mine");

            Assert.Empty(world.Investments.SnapshotFor(Tag));
            Assert.Contains(log, l => l.Contains("INVEST_REQUEST Shipyard") && l.Contains("refused=opt-in"));
        }

        [Fact]
        public void Staged_NpcFaction_StillAsksForItsYard()
        {
            SpatialNode source, dest;
            WorldModel world = MakeWantingWorld(out source, out dest);
            FactionState st = Npc();
            AssertWantsAYard(world);

            var log = new List<string>();
            var ctx = new LiveOperationsExecutionContext(world, st, log);
            ShipPool.MaybePostLocalShipyardInvestment(ctx, world.Tuning, st, source, dest, "mine");

            List<InvestmentRequest> posted = world.Investments.SnapshotFor(Tag);
            Assert.Single(posted);
            Assert.Equal(NodeCapabilityKind.Shipyard, posted[0].Cap);
            Assert.DoesNotContain(log, l => l.Contains("refused=opt-in"));
        }
    }
}
