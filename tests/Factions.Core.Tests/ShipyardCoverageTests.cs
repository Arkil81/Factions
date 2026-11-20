using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The coverage question a local Shipyard request asks, and the one the install answers. These
    /// exist because those were two different questions (FAC-236): the ask keyed on PlanetId, which
    /// two nodes in a body's well satisfy, and the answer demanded a yard on that body's terrain,
    /// which a body forbidding surface stations can never have. A request nothing could satisfy was
    /// re-posted every tick, and since a Shipyard caps at level 1 each tick spent an install on the
    /// next node until every founded node had one.
    /// </summary>
    public class ShipyardCoverageTests
    {
        private static SpatialNode Node(string id, string planetId, StationSite site, int shipyardLevel = 0)
        {
            var n = new SpatialNode
            {
                Id               = id,
                OwnerFactionTag  = "F",
                StableStationId  = 42,
                PlanetId         = planetId,
                Site             = site,
            };
            if (shipyardLevel > 0) n.Capabilities[NodeCapabilityKind.Shipyard] = shipyardLevel;
            return n;
        }

        /// <summary>Builds filling EVERY one of the yard's slots, so it reads as saturated.
        /// <paramref name="stalledSeconds"/> is the
        /// serviced time it has gone without absorbing a component.</summary>
        private static void GiveBuild(SpatialNode yard, double stalledSeconds)
        {
            // Every slot, not one: saturation is what ShipyardCoverage.IsWorking answers, so a
            // multi-slot yard given a single build is genuinely NOT working and a fixture that gave
            // it one would be testing the wrong state.
            for (int i = 0; i < yard.BuildSlotCount(); i++)
            {
                var e = new BuildQueueEntry { Id = yard.MintBuildEntryId("Trade"), Role = "Trade", PrefabId = "P" };
                e.Bom["Comp:SteelPlate"] = 100;
                e.StalledSeconds = stalledSeconds;
                yard.BuildQueue.Add(e);
            }
        }

        /// <summary>A node carrying the Shipyard's prerequisite, somewhere off in the network. Every
        /// coverage question presupposes the faction could install a Shipyard at all, so a fixture
        /// without one is asking a question the answer to which is always "no" — see
        /// <see cref="PrerequisiteInstalledNowhere_AsksForNothing"/>, which is that case on purpose.</summary>
        private static SpatialNode Prereq()
        {
            SpatialNode n = Node("MANU", "Elsewhere", StationSite.Space);
            n.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            return n;
        }

        /// <summary>Asks the coverage question with everything <em>except</em> coverage satisfied: the
        /// prerequisite is installed, and the region holds a founded station below the level cap for
        /// an install to land on.
        ///
        /// <para><b>The spare station is what keeps these tests honest.</b> Without it, every fixture
        /// whose only in-region station already holds a Shipyard is refused for having nowhere to put
        /// another — so an assertion about idleness, pausing or stalling would pass without the
        /// condition it names being read at all. The refusal tests below deliberately do NOT use this
        /// helper; they call the API with fixtures that lack exactly the thing they are about.</para></summary>
        private static bool Wants(IEnumerable<SpatialNode> owned, string planetId, StationSite site,
            FactionTuning t, out int yards, out int working)
        {
            var satisfiable = new List<SpatialNode>(owned) { Prereq(), Node("SPARE", planetId, site) };
            return ShipyardCoverage.WantsAnotherYard(satisfiable, NodeRegion.Body(planetId, site), t, out yards, out working);
        }

        /// <summary>A node in open space claimed by an authored feature: no body, a feature key.</summary>
        private static SpatialNode NestNode(string id, string feature, int shipyardLevel = 0)
        {
            SpatialNode n = Node(id, null, StationSite.Space, shipyardLevel);
            n.FeatureKey = feature;
            return n;
        }

        // ---- the region a node stands in -----------------------------------------------------

        [Fact]
        public void RegionOf_SeparatesAbodysGroundFromItsWell()
        {
            Assert.Equal(NodeRegion.Body("Terra", StationSite.Surface), ShipyardCoverage.RegionOf(Node("A", "Terra", StationSite.Surface)));
            Assert.Equal(NodeRegion.Body("Terra", StationSite.Orbital), ShipyardCoverage.RegionOf(Node("B", "Terra", StationSite.Orbital)));
        }

        [Fact]
        public void RegionOf_TreatsAnUnderivedSiteAsTheWell()
        {
            // Site is derived, not persisted, so an unset value is a real state. The answer that
            // grants no ground capacity is the safe one -- the same reason StationSite.Space is zero.
            Assert.Equal(NodeRegion.Body("Terra", StationSite.Orbital), ShipyardCoverage.RegionOf(Node("C", "Terra", StationSite.Space)));
        }

        [Fact]
        public void RegionOf_ANodeNoBodyClaims_IsItsFeature()
        {
            // The deep-space case this exists for: PlanetId null, so before FeatureKey every such
            // node read as one undifferentiated place and no coverage question could name a nest.
            NodeRegion r = ShipyardCoverage.RegionOf(NestNode("N", "Far Nest Umbra"));
            Assert.True(r.IsFeature);
            Assert.Equal(NodeRegion.Feature("Far Nest Umbra"), r);
        }

        [Fact]
        public void RegionOf_ABodyClaimsANodeBeforeAFeatureDoes()
        {
            // A ring authored inside a well places nodes that still carry the well's PlanetId; the
            // well is the region a leg between them runs in, and the feature is placement authority
            // only.
            SpatialNode ringNode = Node("R", "Cauldron", StationSite.Orbital);
            ringNode.FeatureKey = "Cauldron Ring";
            Assert.Equal(NodeRegion.Body("Cauldron", StationSite.Orbital), ShipyardCoverage.RegionOf(ringNode));
        }

        [Fact]
        public void RegionOf_OpenSpace_IsNoRegionAtAll()
        {
            Assert.True(ShipyardCoverage.RegionOf(Node("V", null, StationSite.Space)).IsNone);
        }

        [Fact]
        public void InRegion_IsFalseAcrossBodiesAndAcrossRegionsOfOneBody()
        {
            SpatialNode ground = Node("G", "Terra", StationSite.Surface);
            Assert.True(ShipyardCoverage.InRegion(ground, NodeRegion.Body("Terra", StationSite.Surface)));
            Assert.False(ShipyardCoverage.InRegion(ground, NodeRegion.Body("Terra", StationSite.Orbital)));
            Assert.False(ShipyardCoverage.InRegion(ground, NodeRegion.Body("Mars", StationSite.Surface)));
        }

        [Fact]
        public void InRegion_IsFalseAcrossFeatures_AndNeverTrueForNoRegion()
        {
            SpatialNode nest = NestNode("N", "Far Nest Umbra");
            Assert.True(ShipyardCoverage.InRegion(nest, NodeRegion.Feature("Far Nest Umbra")));
            Assert.False(ShipyardCoverage.InRegion(nest, NodeRegion.Feature("Far Nest Vigil")));
            Assert.False(ShipyardCoverage.InRegion(nest, NodeRegion.Body("Terra", StationSite.Orbital)));
            // Two nodes in open space share nothing: "in no region" is not a region they are both in.
            Assert.False(ShipyardCoverage.InRegion(Node("V", null, StationSite.Space), NodeRegion.None));
        }

        // ---- feature regions answer the same coverage question a body does ----------------------

        [Fact]
        public void AYardInAnotherNest_CoversNothingHere()
        {
            // FCTM's shape live: both yards in one nest, five nests with none. The far nest is a
            // region of its own and reads as uncovered however many yards the home nest holds.
            var t = TestSupport.Tuning();
            // At the cap, so the home nest's refusal stays about coverage: below it, a saturated
            // yard is somewhere to put more capacity and the region would legitimately ask.
            var home = NestNode("HOME", "Cinder Nest", shipyardLevel: t.MaxLevelFor(NodeCapabilityKind.Shipyard));
            GiveBuild(home, stalledSeconds: 0);
            var owned = new List<SpatialNode> { home, NestNode("FAR", "Far Nest Umbra"), Prereq() };

            int yards, working;
            Assert.True(ShipyardCoverage.WantsAnotherYard(owned, NodeRegion.Feature("Far Nest Umbra"), t, out yards, out working));
            Assert.Equal(0, yards);
            Assert.False(ShipyardCoverage.WantsAnotherYard(owned, NodeRegion.Feature("Cinder Nest"), t, out yards, out working));
            Assert.Equal(1, yards);
        }

        [Fact]
        public void NoRegion_IsNeverAskedFor()
        {
            var t = TestSupport.Tuning();
            var owned = new List<SpatialNode> { Node("VOID", null, StationSite.Space), Prereq() };
            int yards, working;
            Assert.False(ShipyardCoverage.WantsAnotherYard(owned, NodeRegion.None, t, out yards, out working));
        }

        [Fact]
        public void Census_BucketsByRegion_AndCountsNoNodeInOpenSpace()
        {
            var t = TestSupport.Tuning();
            var yard = NestNode("Y", "Cinder Nest", shipyardLevel: 1);
            GiveBuild(yard, stalledSeconds: 0);
            var owned = new List<SpatialNode>
            {
                yard, NestNode("A", "Cinder Nest"), NestNode("B", "Far Nest Umbra"),
                Node("G", "Terra", StationSite.Surface), Node("VOID", null, StationSite.Space),
            };

            var census = ShipyardCoverage.Census(owned, t);
            Assert.Equal(3, census.Count);
            ShipyardCensus cinder = census[NodeRegion.Feature("Cinder Nest")];
            Assert.Equal(2, cinder.Nodes);
            Assert.Equal(1, cinder.Yards);
            Assert.Equal(1, cinder.Working);
            Assert.True(cinder.Hostable);
            ShipyardCensus umbra = census[NodeRegion.Feature("Far Nest Umbra")];
            Assert.Equal(1, umbra.Nodes);
            Assert.Equal(0, umbra.Yards);
            Assert.Equal(1, census[NodeRegion.Body("Terra", StationSite.Surface)].Nodes);
        }

        // ---- coverage: does this region already answer the need? ------------------------------

        [Fact]
        public void EmptyRegion_WantsAYard()
        {
            var t = TestSupport.Tuning();
            var owned = new List<SpatialNode> { Node("WELL", "Cauldron", StationSite.Orbital) };

            int yards, working;
            Assert.True(Wants(owned, "Cauldron", StationSite.Orbital, t, out yards, out working));
            Assert.Equal(0, yards);
        }

        [Fact]
        public void AYardInTheWell_IsCoverageForTheWell_WhichTheOldGroundOnlyCheckCouldNotSee()
        {
            // The gas giant's case: the body forbids surface stations, so its only region is its
            // well. Before FAC-236 this asked "is there a yard on the ground", the answer was
            // permanently no however many yards the well held, and every install made against the
            // request left it standing. The yard is idle here so the census is the only thing
            // deciding -- saturation has its own tests below.
            var t = TestSupport.Tuning();
            var owned = new List<SpatialNode> { Node("RING", "Cauldron", StationSite.Orbital, shipyardLevel: 1) };

            int yards, working;
            Assert.False(Wants(owned, "Cauldron", StationSite.Orbital, t, out yards, out working));
            Assert.Equal(1, yards);
        }

        [Fact]
        public void AYardOnTheGround_DoesNotCoverTheWellOfTheSameBody()
        {
            var t = TestSupport.Tuning();
            var yard = Node("GROUND", "Terra", StationSite.Surface, shipyardLevel: 1);
            GiveBuild(yard, stalledSeconds: 0);
            var owned = new List<SpatialNode> { yard, Node("WELL", "Terra", StationSite.Orbital) };

            int yards, working;
            Assert.True(Wants(owned, "Terra", StationSite.Orbital, t, out yards, out working));
            Assert.Equal(0, yards);
        }

        [Fact]
        public void AYardOnAnotherBody_CoversNothingHere()
        {
            var t = TestSupport.Tuning();
            var yard = Node("MARSYARD", "Mars", StationSite.Surface, shipyardLevel: 1);
            GiveBuild(yard, stalledSeconds: 0);
            var owned = new List<SpatialNode> { yard, Node("TERRA", "Terra", StationSite.Surface) };

            int yards, working;
            Assert.True(Wants(owned, "Terra", StationSite.Surface, t, out yards, out working));
            Assert.Equal(0, yards);
        }

        // ---- saturation: a second yard in one region has to be earned -------------------------

        [Fact]
        public void AnIdleYard_DoesNotEarnASecondOneBesideIt()
        {
            // Nothing queued at all: the yard is producing nothing, and another yard cannot raise
            // output when the constraint is that no build is running.
            var t = TestSupport.Tuning();
            var owned = new List<SpatialNode> { Node("YARD", "Terra", StationSite.Surface, shipyardLevel: 1) };

            int yards, working;
            Assert.False(Wants(owned, "Terra", StationSite.Surface, t, out yards, out working));
            Assert.Equal(1, yards);
            Assert.Equal(0, working);
        }

        [Fact]
        public void AStalledBuild_DoesNotCountAsWorking()
        {
            // The slot is occupied, so "busy" would read true forever. Absorbing nothing for longer
            // than the working window is exactly the case where a second yard splits the same
            // starved component stream instead of adding throughput.
            var t = TestSupport.Tuning();
            var yard = Node("YARD", "Terra", StationSite.Surface, shipyardLevel: 1);
            GiveBuild(yard, stalledSeconds: t.ShipyardWorkingStallSeconds + 1.0);
            var owned = new List<SpatialNode> { yard };

            int yards, working;
            Assert.False(Wants(owned, "Terra", StationSite.Surface, t, out yards, out working));
            Assert.Equal(0, working);
        }

        [Fact]
        public void AWorkingYard_EarnsASecondOneInTheSameRegion()
        {
            var t = TestSupport.Tuning();
            var yard = Node("YARD", "Terra", StationSite.Surface, shipyardLevel: 1);
            GiveBuild(yard, stalledSeconds: t.ShipyardWorkingStallSeconds - 1.0);
            var owned = new List<SpatialNode> { yard };

            int yards, working;
            Assert.True(Wants(owned, "Terra", StationSite.Surface, t, out yards, out working));
            Assert.Equal(1, yards);
            Assert.Equal(1, working);
        }

        [Fact]
        public void OneIdleYardAmongTwo_StopsTheAsk()
        {
            // Every yard in the region must be working: an idle one is spare capacity the region
            // already holds, and handing it a third is the runaway this replaces.
            var t = TestSupport.Tuning();
            var busy = Node("BUSY", "Terra", StationSite.Surface, shipyardLevel: 1);
            GiveBuild(busy, stalledSeconds: 0);
            var owned = new List<SpatialNode>
            {
                busy,
                Node("IDLE", "Terra", StationSite.Surface, shipyardLevel: 1),
            };

            int yards, working;
            Assert.False(Wants(owned, "Terra", StationSite.Surface, t, out yards, out working));
            Assert.Equal(2, yards);
            Assert.Equal(1, working);
        }

        [Fact]
        public void APausedYard_IsNotWorkingHoweverBusyItsQueueLooks()
        {
            var t = TestSupport.Tuning();
            var yard = Node("YARD", "Terra", StationSite.Surface, shipyardLevel: 1);
            GiveBuild(yard, stalledSeconds: 0);
            yard.ShipyardPaused = true;
            var owned = new List<SpatialNode> { yard };

            int yards, working;
            Assert.False(Wants(owned, "Terra", StationSite.Surface, t, out yards, out working));
            Assert.Equal(0, working);
        }

        [Fact]
        public void AQueuedBuildBehindTheWorkingSlot_DoesNotCountAsWorking()
        {
            // A yard works BuildSlots entries at a time; the ones waiting absorb nothing. A stalled
            // build in the slot with a fresh entry queued behind it is still a stalled yard.
            var t = TestSupport.Tuning();
            var yard = Node("YARD", "Terra", StationSite.Surface, shipyardLevel: 1);
            GiveBuild(yard, stalledSeconds: t.ShipyardWorkingStallSeconds + 1.0);
            GiveBuild(yard, stalledSeconds: 0);
            var owned = new List<SpatialNode> { yard };

            int yards, working;
            Assert.False(Wants(owned, "Terra", StationSite.Surface, t, out yards, out working));
            Assert.Equal(0, working);
        }

        // ---- refusals: an ask nobody can answer still costs reserved stock (FAC-238) ------------

        [Fact]
        public void NoFoundedStationInTheRegion_AsksForNothing()
        {
            // A leg between two unfounded nodes is in the region, but capabilities are station
            // sub-modules: there is nowhere in this region an install could go. Live 2026-09-02,
            // IGSL's case — 7,180 component units reserved against a request resolving to nothing.
            var t = TestSupport.Tuning();
            SpatialNode mine = Node("ORE", "Terra", StationSite.Surface);
            mine.StableStationId = 0;
            var owned = new List<SpatialNode> { mine };

            int yards, working;
            Assert.False(ShipyardCoverage.WantsAnotherYard(new List<SpatialNode>(owned) { Prereq() },
                NodeRegion.Body("Terra", StationSite.Surface), t, out yards, out working));
            Assert.Equal(0, yards);
        }

        [Fact]
        public void EveryStationInTheRegionAtMaxLevel_AsksForNothing()
        {
            // IMPR's case: a Shipyard on every founded node in the region, all of them working, so
            // the saturation arm says yes and there is still nowhere to put one. 6,913 units held.
            var t = TestSupport.Tuning();
            var yard = Node("YARD", "Terra", StationSite.Surface,
                shipyardLevel: t.MaxLevelFor(NodeCapabilityKind.Shipyard));
            GiveBuild(yard, stalledSeconds: 0);
            var owned = new List<SpatialNode> { yard };

            int yards, working;
            Assert.False(ShipyardCoverage.WantsAnotherYard(new List<SpatialNode>(owned) { Prereq() },
                NodeRegion.Body("Terra", StationSite.Surface), t, out yards, out working));
            Assert.Equal(1, yards);
            Assert.Equal(1, working);   // saturated: it WOULD have asked but for the max-level refusal
        }

        [Fact]
        public void SaturatedYardBelowTheCap_AsksForMoreCapacity()
        {
            // A Shipyard can be upgraded now, so a region whose only yard is full and not yet at its
            // ceiling has somewhere to put capacity -- the yard itself. Hostable covers an upgrade
            // target, not only an empty station.
            var t = TestSupport.Tuning();
            var yard = Node("YARD", "Terra", StationSite.Surface, shipyardLevel: 1);
            GiveBuild(yard, stalledSeconds: 0);

            int yards, working;
            Assert.True(ShipyardCoverage.WantsAnotherYard(new List<SpatialNode> { yard, Prereq() },
                NodeRegion.Body("Terra", StationSite.Surface), t, out yards, out working));
            Assert.Equal(1, yards);
            Assert.Equal(1, working);
        }

        [Fact]
        public void IdleYardBelowTheCap_AsksForNothing()
        {
            // The sprawl gate, and the paired negative the test above needs: the same upgradable yard
            // that is NOT being fed earns nothing. Capacity is earned by saturation, and a bigger
            // yard and a second yard are both capacity -- neither answers a starved component stream.
            var t = TestSupport.Tuning();
            var yard = Node("YARD", "Terra", StationSite.Surface, shipyardLevel: 1);
            GiveBuild(yard, stalledSeconds: t.ShipyardWorkingStallSeconds + 1);

            int yards, working;
            Assert.False(ShipyardCoverage.WantsAnotherYard(new List<SpatialNode> { yard, Prereq() },
                NodeRegion.Body("Terra", StationSite.Surface), t, out yards, out working));
            Assert.Equal(1, yards);
            Assert.Equal(0, working);
        }

        [Fact]
        public void MultiSlotYardFeedingOneBuild_IsNotSaturated()
        {
            // What the slot count buys the gate: a level-3 yard working one build has two slots
            // spare, so it is not full and does not justify more capacity beside it. Counting "any
            // fed build" instead would read this as saturated and ask.
            var t = TestSupport.Tuning();
            var yard = Node("YARD", "Terra", StationSite.Surface, shipyardLevel: 3);
            var single = new BuildQueueEntry { Id = yard.MintBuildEntryId("Trade"), Role = "Trade", PrefabId = "P" };
            single.Bom["Comp:SteelPlate"] = 100;
            yard.BuildQueue.Add(single);

            Assert.True(yard.IsActiveBuild(single));
            Assert.False(ShipyardCoverage.IsWorking(yard, t));
        }

        [Fact]
        public void AnEmptyStationBesideTheFullOne_StillAsks()
        {
            // The discriminator for the test above: the refusal is "nowhere to put one", not "this
            // region already has a yard". One founded station below the cap is somewhere to put one.
            var t = TestSupport.Tuning();
            var yard = Node("YARD", "Terra", StationSite.Surface, shipyardLevel: 1);
            GiveBuild(yard, stalledSeconds: 0);
            var owned = new List<SpatialNode> { yard, Node("SPARE2", "Terra", StationSite.Surface), Prereq() };

            int yards, working;
            Assert.True(ShipyardCoverage.WantsAnotherYard(owned, NodeRegion.Body("Terra", StationSite.Surface), t,
                out yards, out working));
        }

        [Fact]
        public void PrerequisiteInstalledNowhere_AsksForNothing()
        {
            // Shipyard requires Manufactory somewhere in the network. Without one the request can
            // never convert, so posting it only reserves its BOM. Read through the same
            // CapabilityPrerequisites map InvestmentTargeting resolves against, never a named
            // capability, so the two cannot disagree about what a Shipyard needs.
            var t = TestSupport.Tuning();
            var owned = new List<SpatialNode> { Node("STATION", "Terra", StationSite.Surface) };

            int yards, working;
            Assert.False(ShipyardCoverage.WantsAnotherYard(owned, NodeRegion.Body("Terra", StationSite.Surface), t,
                out yards, out working));

            // ...and the same fixture with the prerequisite present does ask, so the refusal above
            // is the prerequisite and not the fixture.
            owned.Add(Prereq());
            Assert.True(ShipyardCoverage.WantsAnotherYard(owned, NodeRegion.Body("Terra", StationSite.Surface), t,
                out yards, out working));
        }
    

        // ---- the region a request carries is the region it is resolved against -----------------

        [Fact]
        public void Scope_ThenOf_RoundTripsEveryKind()
        {
            var req = new InvestmentRequest { FactionTag = "F", Cap = NodeCapabilityKind.Shipyard };
            Assert.Equal(NodeRegion.Body("Terra", StationSite.Orbital),
                NodeRegion.Of(NodeRegion.Body("Terra", StationSite.Orbital).Scope(req)));
            Assert.Equal(NodeRegion.Feature("Far Nest Umbra"),
                NodeRegion.Of(NodeRegion.Feature("Far Nest Umbra").Scope(req)));
            Assert.True(NodeRegion.Of(NodeRegion.None.Scope(req)).IsNone);
            // A feature scope leaves no body fields behind, and vice versa: the register keys on
            // all three, so a stale field would make two asks for one region.
            InvestmentRequest feature = NodeRegion.Feature("Far Nest Umbra").Scope(
                NodeRegion.Body("Terra", StationSite.Surface).Scope(req));
            Assert.Null(feature.TargetPlanetId);
            Assert.Null(feature.TargetSite);
        }

        [Fact]
        public void Of_ABodyWithoutASite_ReadsAsItsGround()
        {
            // The shape every planet-scoped poster wrote before TargetSite existed.
            var req = new InvestmentRequest { FactionTag = "F", Cap = NodeCapabilityKind.Shipyard, TargetPlanetId = "Terra" };
            Assert.Equal(NodeRegion.Body("Terra", StationSite.Surface), NodeRegion.Of(req));
        }
    }
}
