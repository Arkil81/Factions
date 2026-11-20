using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Investment requests name a capability, never a node — the node is resolved from the current
    /// graph. These pin the resolution rule (a region-scoped request resolves only inside its own
    /// region, with no off-region fallback), the blocked reasons reported when nothing qualifies, and
    /// that the brain's Phase-1 Develop candidate lands on the node the resolver reports.
    /// </summary>
    public class InvestmentTargetingTests
    {
        /// <summary>Influence field with one control value for every node, so control gating can be
        /// exercised (the shared ZeroField is permanently uncontested at 1.0).</summary>
        private sealed class FixedControlField : IInfluenceField
        {
            private readonly double _control;
            public FixedControlField(double control) { _control = control; }
            public double Sample(Vec3 pos, string factionTag) { return 0; }
            public double Control(Vec3 pos, string factionTag) { return _control; }
            public double Pressure(string ofFaction, string byFaction) { return 0; }
            public IEnumerable<string> RivalTags(string factionTag) { return new string[0]; }
            public double SampleAt(string nodeId, string factionTag) { return 0; }
            public double ControlAt(string nodeId, string factionTag) { return _control; }
        }

        private static SpatialNode AddStation(FakeWorld w, string id, string planetId = null, NodeZone zone = NodeZone.DeepSpace, bool withOre = false)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength    = 1.0;
            n.StableStationId = 42;
            n.PlanetId        = planetId;
            n.Zone            = zone;
            // Site is what the planet-scoped gate reads now; Zone stays because separation uses it.
            // Derived from the zone so the two agree, which is what ReclassifyNodes does per node.
            n.Site            = zone == NodeZone.Surface ? StationSite.Surface
                              : zone == NodeZone.Orbit   ? StationSite.Orbital
                                                         : StationSite.Space;
            // Capability affinity is a world reading since FAC-102: a refinery wants ore beneath it,
            // not a node called "Refinery".
            if (withOre) TestSupport.WithOre(n);
            return n;
        }

        /// <summary>A founded station in open space that an authored feature claims: no body, a
        /// feature key -- a nest node.</summary>
        private static SpatialNode AddNestStation(FakeWorld w, string id, string feature)
        {
            SpatialNode n = AddStation(w, id);
            n.FeatureKey = feature;
            return n;
        }

        private static InvestmentRequest Request(NodeCapabilityKind cap, string planetId = null,
            StationSite? site = null)
        {
            return new InvestmentRequest
            {
                FactionTag     = "F",
                Cap            = cap,
                Urgency        = 0.8,
                ExpiresUtc     = new System.DateTime(2026, 1, 1, 1, 0, 0, System.DateTimeKind.Utc),
                TargetPlanetId = planetId,
                TargetSite     = planetId == null ? site : (site ?? StationSite.Surface),
            };
        }

        private static InvestmentTarget Resolve(FakeWorld w, InvestmentRequest req, FactionTuning t)
        {
            return InvestmentTargeting.Resolve(w, "F", req, t);
        }

        // ---- resolution ---------------------------------------------------------------------

        [Fact]
        public void Resolve_PicksHighestAffinityOwnedNode()
        {
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            AddStation(w, "GEN");                          // no ore -> Default 0.20
            AddStation(w, "REF", withOre: true);          // ore -> Mineable 0.90

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Refinery), t);

            Assert.True(target.Resolved);
            Assert.Equal("REF", target.NodeId);
            Assert.Equal(InvestmentTargetBlock.None, target.Block);
            Assert.False(target.OnTargetPlanet);                 // request was not planet-scoped
        }

        [Fact]
        public void PlanetScopedRequest_PrefersSamePlanetSurfaceNode_OverBetterFitOffPlanet()
        {
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            // Better Shipyard affinity (0.9) but off-planet — must lose to the local Generic (0.2).
            SpatialNode core = AddStation(w, "CORE");
            core.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            core.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            AddStation(w, "LOCAL", planetId: "Terra", zone: NodeZone.Surface);

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Shipyard, "Terra"), t);

            Assert.Equal("LOCAL", target.NodeId);
            Assert.True(target.OnTargetPlanet);
        }

        [Fact]
        public void RegionScopedRequest_ResolvesNothingOffRegion_AndReportsWhy()
        {
            // The install that used to happen here is the FAC-236 defect: a yard placed off the
            // region cannot serve the leg that asked and cannot satisfy the poster's coverage check
            // either, so the request stands and the next tick spends another install on the next
            // node. Resolving to nothing is the correct answer, and it has to be a legible one.
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            SpatialNode core = AddStation(w, "CORE");
            core.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            core.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            // Same planet id but in the well, not on the ground: not the region that was asked for.
            AddStation(w, "ORBIT", planetId: "Terra", zone: NodeZone.Orbit);

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Shipyard, "Terra"), t);

            Assert.False(target.Resolved);
            Assert.Null(target.NodeId);
            Assert.False(target.OnTargetPlanet);
            Assert.Equal(InvestmentTargetBlock.OutOfRegion, target.Block);
        }

        [Fact]
        public void RegionScopedRequest_OffRegionNodeAtMaxLevel_DoesNotDecideTheReason()
        {
            // FAC-238. The region is a precondition, not a gate: an out-of-region node must not
            // colour the reported reason. Ordered the other way, ANY owned node at max level pinned
            // the answer at AtMaxLevel and the region test was never reached, so a faction owning no
            // station in the region asked for read "already at max level" — live, IMPR did.
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            SpatialNode core = AddStation(w, "CORE");
            core.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            core.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            core.Capabilities[NodeCapabilityKind.Shipyard]    = 1;   // off-region, and at max
            AddStation(w, "ORBIT", planetId: "Terra", zone: NodeZone.Orbit);

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Shipyard, "Terra"), t);

            Assert.False(target.Resolved);
            Assert.Equal(InvestmentTargetBlock.OutOfRegion, target.Block);
        }

        [Fact]
        public void RegionScopedRequest_ReasonComesFromTheRegionsOwnNodes_NotElsewhere()
        {
            // The case that distinguishes the REORDER from the "nothing in the region" verdict: the
            // region does hold a node, so the verdict falls through to the ladder, and the ladder
            // must have been walked by that node alone. Region node is unfounded (NoFoundedStation);
            // an unrelated node elsewhere is at max. Before FAC-238 the far node's AtMaxLevel won.
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            SpatialNode core = AddStation(w, "CORE");
            core.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            core.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            core.Capabilities[NodeCapabilityKind.Shipyard]    = 1;   // off-region, at max
            SpatialNode local = AddStation(w, "LOCAL", planetId: "Terra", zone: NodeZone.Surface);
            local.StableStationId = 0;                                // in region, not founded

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Shipyard, "Terra"), t);

            Assert.False(target.Resolved);
            Assert.Equal(InvestmentTargetBlock.NoFoundedStation, target.Block);
        }

        [Fact]
        public void RegionScopedRequest_InRegionNodeAtMaxLevel_StillReportsAtMaxLevel()
        {
            // The other side of it: the ladder must keep working for nodes that ARE in the region,
            // or the fix above would have replaced one uninformative answer with another.
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            SpatialNode core = AddStation(w, "CORE");
            core.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            core.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            SpatialNode local = AddStation(w, "LOCAL", planetId: "Terra", zone: NodeZone.Surface);
            local.Capabilities[NodeCapabilityKind.Shipyard] = t.MaxLevelFor(NodeCapabilityKind.Shipyard);  // in region, and at max

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Shipyard, "Terra"), t);

            Assert.False(target.Resolved);
            Assert.Equal(InvestmentTargetBlock.AtMaxLevel, target.Block);
        }

        [Fact]
        public void RegionScopedRequest_ResolvesInTheWell_WhenTheWellIsTheRegionAsked()
        {
            // The gas giant's case, and the whole point of carrying the site: the body forbids
            // surface stations, so its only region is its well. A request naming that region has to
            // be satisfiable there, or nothing can ever satisfy it.
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            SpatialNode core = AddStation(w, "CORE");
            core.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            core.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            AddStation(w, "RING", planetId: "Cauldron", zone: NodeZone.Orbit);

            InvestmentTarget target = Resolve(w,
                Request(NodeCapabilityKind.Shipyard, "Cauldron", StationSite.Orbital), t);

            Assert.Equal("RING", target.NodeId);
            Assert.True(target.OnTargetPlanet);
        }

        [Fact]
        public void RegionScopedRequest_IgnoresTheSameBodysOtherRegion()
        {
            // A landable body has both regions. A request for its well must not be answered on its
            // ground, or the poster's coverage check reads a yard the leg cannot use.
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            SpatialNode core = AddStation(w, "CORE");
            core.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            core.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            AddStation(w, "GROUND", planetId: "Terra", zone: NodeZone.Surface, withOre: true);
            AddStation(w, "WELL",   planetId: "Terra", zone: NodeZone.Orbit);

            InvestmentTarget target = Resolve(w,
                Request(NodeCapabilityKind.Shipyard, "Terra", StationSite.Orbital), t);

            Assert.Equal("WELL", target.NodeId);
        }

        // ---- blocked reasons ----------------------------------------------------------------

        [Fact]
        public void Blocked_NoOwnedNodes()
        {
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Refinery), t);

            Assert.False(target.Resolved);
            Assert.Equal(InvestmentTargetBlock.NoOwnedNodes, target.Block);
        }

        [Fact]
        public void Blocked_NoFoundedStation()
        {
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            AddStation(w, "N1").StableStationId = 0;

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Refinery), t);

            Assert.Equal(InvestmentTargetBlock.NoFoundedStation, target.Block);
        }

        [Fact]
        public void Blocked_ControlTooLow()
        {
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000, Influence = new FixedControlField(0.4) };
            AddStation(w, "N1");

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Refinery), t);

            Assert.Equal(InvestmentTargetBlock.ControlTooLow, target.Block);
        }

        [Fact]
        public void Blocked_PrereqMissing()
        {
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            AddStation(w, "N1");   // no Refinery installed anywhere

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Manufactory), t);

            Assert.Equal(InvestmentTargetBlock.PrereqMissing, target.Block);
        }

        [Fact]
        public void Blocked_AtMaxLevel()
        {
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            SpatialNode n = AddStation(w, "N1");
            n.Capabilities[NodeCapabilityKind.Refinery] = t.MaxRefineryUpgrades;

            InvestmentTarget target = Resolve(w, Request(NodeCapabilityKind.Refinery), t);

            Assert.Equal(InvestmentTargetBlock.AtMaxLevel, target.Block);
        }

        // ---- snapshot -----------------------------------------------------------------------

        [Fact]
        public void SnapshotSignals_ResolvesEveryActiveRequest_AndFlagsAffordability()
        {
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            AddStation(w, "REF");
            w.InvestmentRegister.Post(Request(NodeCapabilityKind.Refinery));
            w.InvestmentRegister.Post(Request(NodeCapabilityKind.FuelDepot));

            List<InvestmentSignal> rich = InvestmentTargeting.SnapshotSignals(w, "F", t);
            Assert.Equal(2, rich.Count);
            Assert.All(rich, sig => Assert.Equal("REF", sig.Target.NodeId));
            Assert.All(rich, sig => Assert.True(sig.Affordable));

            w.Treasury = t.InsolvencyFloor + t.DevelopCost;   // exactly at the floor is not affordable
            List<InvestmentSignal> broke = InvestmentTargeting.SnapshotSignals(w, "F", t);
            Assert.All(broke, sig => Assert.False(sig.Affordable));
            // Still resolves a target — the panel shows where it would go, not that it is going.
            Assert.All(broke, sig => Assert.True(sig.Target.Resolved));
        }

        [Fact]
        public void SnapshotSignals_IsEmpty_WhenNoRequestsPosted()
        {
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            AddStation(w, "REF");

            Assert.Empty(InvestmentTargeting.SnapshotSignals(w, "F", t));
        }

        // ---- brain parity -------------------------------------------------------------------

        [Fact]
        public void BrainDevelopCandidate_LandsOnTheResolvedNode()
        {
            var t     = TestSupport.Tuning();
            var brain = new FactionBrain();
            var w     = new FakeWorld { Treasury = 5_000_000 };
            AddStation(w, "GEN");
            AddStation(w, "REF");

            InvestmentRequest req = Request(NodeCapabilityKind.RefineryII);
            w.InvestmentRegister.Post(req);
            w.Graph.Nodes["REF"].Capabilities[NodeCapabilityKind.Refinery] = 1;

            InvestmentTarget target = Resolve(w, req, t);

            var p = new Personality
            {
                Temperature = 0.5,
                Traits = new Traits { Xenophobia = 0.5, Expansionism = 0.5, Mercantilism = 0.5, Aggression = 0.5 }
            };
            p.Weights[Drive.Security] = 1; p.Weights[Drive.Growth] = 1; p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Rivalry] = 1; p.Weights[Drive.Caution] = 0;

            Decision d = brain.Think(new FactionState { Tag = "F", RngSeed = 1 }, p, w, new SeededRandom(1), t);

            Assert.True(target.Resolved);
            Assert.Contains(d.Scored,
                c => c.Intent.Kind == ActionKind.Develop
                  && c.Intent.TargetCapability == NodeCapabilityKind.RefineryII
                  && c.Intent.TargetNodeId == target.NodeId);
        }
    

        [Fact]
        public void FeatureScopedRequest_ResolvesOnlyInsideTheFeature()
        {
            // The deep-space half of the region model: a request naming a nest lands in that nest,
            // never in the home nest that happens to have the better-fit station.
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            SpatialNode home = AddNestStation(w, "HOME", "Cinder Nest");
            home.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            home.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            TestSupport.WithOre(home);                                   // the better fit, off-region
            AddNestStation(w, "FAR", "Far Nest Umbra");

            InvestmentTarget target = Resolve(w, NodeRegion.Feature("Far Nest Umbra")
                .Scope(Request(NodeCapabilityKind.Shipyard)), t);

            Assert.True(target.Resolved);
            Assert.Equal("FAR", target.NodeId);
            Assert.True(target.OnTargetPlanet);
        }

        [Fact]
        public void FeatureScopedRequest_ResolvesNothingOffFeature_AndReportsWhy()
        {
            var t = TestSupport.Tuning();
            var w = new FakeWorld { Treasury = 5_000_000 };
            SpatialNode home = AddNestStation(w, "HOME", "Cinder Nest");
            home.Capabilities[NodeCapabilityKind.Refinery]    = 1;
            home.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            AddStation(w, "VOID");                                       // open space: no region

            InvestmentTarget target = Resolve(w, NodeRegion.Feature("Far Nest Umbra")
                .Scope(Request(NodeCapabilityKind.Shipyard)), t);

            Assert.False(target.Resolved);
            Assert.Equal(InvestmentTargetBlock.OutOfRegion, target.Block);
        }
    }
}
