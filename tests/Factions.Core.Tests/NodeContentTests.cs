using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Newtonsoft.Json;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-102: a node's content is read from the world, never rolled for it and never loaded from a
    /// label. Covers the three things that decision actually changes — the world is always asked, the
    /// parent no longer reaches the child, and the persisted ordinal grants nothing.
    /// </summary>
    public class NodeContentTests
    {
        // ---- doubles -----------------------------------------------------------------------------

        private sealed class OreEverywhere : IOreGeography
        {
            public readonly List<OreObservation> Found = new List<OreObservation>();
            public int SampleCalls;

            public bool TrySample(Vec3 posKm, double radiusKm, out OreSample sample)
            {
                SampleCalls++;
                var s = new OreSample { PointsExamined = 1000 };
                s.Ores.AddRange(Found);
                sample = s;
                return true;
            }

            public bool TryResample(Vec3 posKm, double radiusKm, long startIndex, int count, out OreSample sample)
            {
                if (!TrySample(posKm, radiusKm, out sample)) return false;
                sample.PointsExamined = count;
                return true;
            }

            public OreProspect CouldBearOre(Vec3 posKm)
            {
                return Found.Count > 0 ? OreProspect.Possible : OreProspect.None;
            }

            public IEnumerable<string> MintableOres
            {
                get { foreach (OreObservation o in Found) yield return o.SubTypeId; }
            }
        }

        /// <summary>One 30 km body at the origin, gravity out to 48.79 km — the ordinary-planet shape
        /// <see cref="StationSitingTests"/> already uses.</summary>
        private sealed class OnePlanet : IPlanetCatalogue
        {
            internal const double SurfaceR = 30.0;
            internal const double GravityR = 48.79;

            private readonly PlanetRecord _r = new PlanetRecord
            {
                Id = "Tellus", Name = "Tellus", Generator = "EarthLike",
                Center = new Vec3(0, 0, 0),
                SurfaceRadius = SurfaceR, GravityRadius = GravityR,
                AtmosphereRadius = 34.86, HasAtmosphere = true,
            };

            public IEnumerable<PlanetRecord> Planets { get { yield return _r; } }

            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_r.Center);
                return d >= GravityR ? 0 : 1.0 * (1.0 - d / GravityR);
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                double d = posKm.DistanceTo(_r.Center);
                if (d < 1e-6) return new Vec3(SurfaceR, 0, 0);
                double k = SurfaceR / d;
                return new Vec3(posKm.X * k, posKm.Y * k, posKm.Z * k);
            }

            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static SpatialNode NodeAt(Vec3 pos, params string[] ores)
        {
            var n = new SpatialNode { Id = "N1", Position = pos };
            if (ores != null && ores.Length > 0)
            {
                n.Deposit = new NodeDeposit();
                foreach (string ore in ores)
                    n.Deposit.Entries.Add(new DepositEntry(ore, 1.0 / ores.Length, 100.0 / ores.Length));
            }
            return n;
        }

        // ---- mineability is a property of the ground ---------------------------------------------

        [Fact]
        public void IsMineable_FollowsTheDeposit_NotTheLabel()
        {
            SpatialNode labelledButEmpty = NodeAt(new Vec3(1000, 0, 0));      // the label a retired die would have rolled
            Assert.False(NodeContent.IsMineable(labelledButEmpty));

            SpatialNode unlabelledButOreBearing = NodeAt(new Vec3(1000, 0, 0), "Iron");   // a kind that could never be mined before
            Assert.True(NodeContent.IsMineable(unlabelledButOreBearing));
        }

        [Fact]
        public void BarrenDeposit_IsNotMineable()
        {
            SpatialNode n = NodeAt(new Vec3(1000, 0, 0));
            n.Deposit = new NodeDeposit();                  // sampled, and the ground held nothing
            Assert.False(NodeContent.IsMineable(n));
        }

        // ---- what the world says about a node ------------------------------------------------------

        [Fact]
        public void IsIce_FollowsTheDominantOre()
        {
            var tuning = TestSupport.Tuning();
            Assert.True(NodeContent.IsIce(NodeAt(new Vec3(1000, 0, 0), tuning.IceOreSubTypeId), tuning));
            Assert.False(NodeContent.IsIce(NodeAt(new Vec3(1000, 0, 0), "Iron"), tuning));
            Assert.False(NodeContent.IsIce(NodeAt(new Vec3(1000, 0, 0)), tuning));
        }

        // Nothing under it and nothing to stand on -- the reading the retired Empty label expressed.
        [Theory]
        [InlineData(null, true)]        // deep space, no ore
        [InlineData("Tellus", false)]   // no ore, but a body to stand on
        public void IsEmptySpace_NeedsBothNoOreAndNoBody(string planetId, bool expected)
        {
            SpatialNode n = NodeAt(new Vec3(1000, 0, 0));
            n.PlanetId = planetId;
            Assert.Equal(expected, NodeContent.IsEmptySpace(n));
        }

        [Fact]
        public void IsEmptySpace_IsFalseOnceThereIsOre()
        {
            SpatialNode n = NodeAt(new Vec3(1000, 0, 0), "Iron");
            Assert.False(NodeContent.IsEmptySpace(n));
        }

        [Theory]
        [InlineData(500.0, 0.0)]   // nothing there and nowhere to be
        [InlineData(30.0, 0.8)]    // no ore, but a body to stand on
        public void ResourceValueCap_NoOre_ReadsTheSite(double distanceKm, double expected)
        {
            SpatialNode n = NodeAt(new Vec3(distanceKm, 0, 0));
            n.Deposit = new NodeDeposit();
            Assert.Equal(expected, NodeContent.ResourceValueCap(n, new OnePlanet(), TestSupport.Tuning()), 6);
        }

        [Fact]
        public void ResourceValueCap_OreBearing_IsNotCappedBySite()
        {
            SpatialNode deepSpaceOre = NodeAt(new Vec3(5000, 0, 0), "Iron");
            Assert.Equal(1.0, NodeContent.ResourceValueCap(deepSpaceOre, new OnePlanet(), TestSupport.Tuning()), 6);
        }

        // ---- what a player is shown ------------------------------------------------------------------

        // The wire carries the ground's own answer since FAC-102 stage 3. Empty means no ore; Surveyed
        // is what separates "sampled and barren" from "nobody has looked", which an empty ore name
        // alone would collapse -- and since FAC-101 surveys the two are both common.
        [Fact]
        public void NodeDisplay_SeparatesBarrenGroundFromUnsampledGround()
        {
            SpatialNode unsampled = NodeAt(new Vec3(1000, 0, 0));
            SpatialNode barren = NodeAt(new Vec3(1000, 0, 0));
            barren.Deposit = new NodeDeposit();
            SpatialNode oreBearing = NodeAt(new Vec3(1000, 0, 0), "Platinum");

            Assert.Equal("", NodeDisplay.DominantOre(unsampled));
            Assert.False(NodeDisplay.Surveyed(unsampled));

            Assert.Equal("", NodeDisplay.DominantOre(barren));
            Assert.True(NodeDisplay.Surveyed(barren));

            Assert.Equal("Platinum", NodeDisplay.DominantOre(oreBearing));
            Assert.True(NodeDisplay.Surveyed(oreBearing));
        }

        [Fact]
        public void NodeDisplay_CapabilityMask_SetsOneBitPerInstalledCapability()
        {
            SpatialNode n = NodeAt(new Vec3(1000, 0, 0));
            Assert.Equal(0, NodeDisplay.CapabilityMask(n));

            n.Capabilities[NodeCapabilityKind.Refinery] = 1;      // ordinal 0
            n.Capabilities[NodeCapabilityKind.Shipyard] = 2;      // ordinal 2
            n.Capabilities[NodeCapabilityKind.Manufactory] = 0;   // level 0 is not installed

            Assert.Equal((byte)0b0000_0101, NodeDisplay.CapabilityMask(n));
        }

        // ---- the parent no longer reaches the child ------------------------------------------------

        // The defect FAC-102 names, at its source. A rich OreField parent used to make an ore-bearing
        // child the likeliest roll; here the child's content comes only from the ground it lands on,
        // and the ground holds nothing.
        [Fact]
        public void ExpandFrom_RichParent_DoesNotMakeTheChildRich()
        {
            var w = new WorldModel(new SeededRandom(1234)) { Tuning = TestSupport.Tuning() };
            SpatialNode parent = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "P001");
            parent.OwnerFactionTag = "F";
            parent.Deposit = new NodeDeposit();
            parent.Deposit.Entries.Add(new DepositEntry("Platinum", 1.0, 100.0));

            // The sphere around one parent saturates at the deep-space floor well before 25, and a
            // refused expansion has no child to inspect; the ones that do land are what this pins.
            int placed = 0;
            for (int i = 0; i < 25; i++)
            {
                SpatialNode child = w.ExpandFrom("F", "P001");
                if (child == null) continue;
                placed++;
                Assert.False(NodeContent.IsMineable(child));
                Assert.Equal("", NodeDisplay.DominantOre(child));
                Assert.Equal(0.0, child.ResourceValue);
            }
            Assert.True(placed > 0, "every expansion was refused, so no child was inspected");
        }

        // faction-founder's genesis edge (FAC-105): a faction's first node has no predecessor to sample
        // a kind from. Nothing consults a parent any more, so the no-parent call resolves by the same
        // path as any other rather than falling through to a default.
        [Fact]
        public void ExpandFrom_WithNoParentAtAll_ResolvesByTheSamePath()
        {
            var ores = new OreEverywhere();
            ores.Found.Add(new OreObservation("Iron", 60, 10.0, 5.0));
            var w = new WorldModel(new SeededRandom(1234)) { Tuning = TestSupport.Tuning(), Ores = ores };

            SpatialNode genesis = w.ExpandFrom("F", null);

            Assert.NotNull(genesis);
            Assert.Equal(1, ores.SampleCalls);
            Assert.True(NodeContent.IsMineable(genesis));
            Assert.Equal("Iron", NodeDisplay.DominantOre(genesis));
        }

        // ---- the persisted ordinal grants nothing --------------------------------------------------

        // FAC-102's "Persistence - the trap", closed against a real brain file rather than a
        // synthesised one: 169 nodes off the live server, every one of the eight ordinals in use, and
        // not one deposit among them. Every node must come back derived from its site, because the
        // world is the only thing left that can answer.
        [Fact]
        public void ARealBrainFile_LoadsWithEveryPersistedOrdinalDiscarded()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UNKN.brain.json");
            BrainDoc doc = JsonConvert.DeserializeObject<BrainDoc>(File.ReadAllText(path));

            Assert.NotNull(doc.Sources);
            Assert.Equal(169, doc.Sources.Count);
            // Guard the fixture itself: if it ever stops carrying the full ordinal spread, this test
            // stops proving what it claims to.
            var persisted = new HashSet<int>();
            foreach (SourceDoc sd in doc.Sources) if (sd.Kind.HasValue) persisted.Add(sd.Kind.Value);
            Assert.Equal(8, persisted.Count);

            var graph = new SpatialGraph();
            FactionStore.ApplyBrainDoc(doc, graph);

            var w = new WorldModel(new SeededRandom(1)) { Tuning = TestSupport.Tuning(), Planets = new OnePlanet() };
            foreach (SpatialNode n in graph.Nodes.Values) w.Graph.Nodes[n.Id] = n;

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(169, r.Total);
            // Not one node has a deposit, so not one is mineable and not one shows an ore to a player.
            // The persisted ordinals granted nothing: there is no field left for them to grant it to.
            foreach (SpatialNode n in w.Graph.Nodes.Values)
            {
                Assert.False(NodeContent.IsMineable(n));
                Assert.Equal("", NodeDisplay.DominantOre(n));
                Assert.False(NodeDisplay.Surveyed(n));
            }
        }

        // The ordinal is written by no current save. A file round-tripped after FAC-102 drops it, which
        // is what stops a stale label outliving the world reading that replaced it.
        [Fact]
        public void ASavedBrain_CarriesTheGroundsAnswer_NotAKindOrdinal()
        {
            var graph = new SpatialGraph();
            SpatialNode n = graph.Add(new Vec3(1000, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "ACME";
            TestSupport.WithOre(n, "Platinum");

            BrainDoc doc = FactionStore.ToBrainDoc(new FactionState { Tag = "ACME" },
                                                   graph.OwnedBy("ACME"), FactionStore.DefaultLogCap);

            Assert.Null(doc.Sources[0].Kind);
            Assert.Null(doc.Sources[0].KindLabel);
            string json = JsonConvert.SerializeObject(doc.Sources[0]);
            Assert.DoesNotContain("\"Kind\":", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"KindLabel\":", json, StringComparison.Ordinal);

            // What replaces both is a rendering of the ground, for readers that only see the file.
            Assert.Equal("Platinum", doc.Sources[0].DominantOre);
        }

        // ---- what a node mints, and what it is good for (FAC-102 stage 2) ---------------------------

        // The free mint is gated on the ground, not the label. Both directions, because the label is
        // the thing that used to decide and must now be inert.
        [Fact]
        public void FreeMintScale_Ore_FollowsTheDeposit_NotTheLabel()
        {
            FactionTuning t = TestSupport.Tuning();

            SpatialNode bare = NodeAt(new Vec3(1000, 0, 0));
            Assert.Equal(0.0, NodeContent.FreeMintScaleFor(bare, ItemKind.Ore, t), 6);

            SpatialNode oreBearing = NodeAt(new Vec3(1000, 0, 0), "Iron");
            Assert.True(NodeContent.FreeMintScaleFor(oreBearing, ItemKind.Ore, t) > 0);
        }

        // Ingot WAS the gravel trickle, ungated on a deposit so every node in the graph minted it.
        // FAC-233 retired the channel: gravel is mined and refined now, so the scale is gone and the
        // ungated read is 0 wherever it is taken. Both directions, because "ungated" is what made
        // this the one channel a bare node could still mint.
        [Fact]
        public void FreeMintScale_Ingot_IsRetired_OnAnyNode()
        {
            FactionTuning t = TestSupport.Tuning();

            SpatialNode bare = NodeAt(new Vec3(1000, 0, 0));
            Assert.Equal(0.0, NodeContent.FreeMintScaleFor(bare, ItemKind.Ingot, t), 6);

            SpatialNode oreBearing = NodeAt(new Vec3(1000, 0, 0), "Iron");
            Assert.Equal(0.0, NodeContent.FreeMintScaleFor(oreBearing, ItemKind.Ingot, t), 6);
        }

        // These three carried a per-kind scale and had no yield-table entry anywhere, so nothing ever
        // minted them. ProcessedGood is ProductionManager's to make through a Manufactory; Hydrogen and
        // Oxygen have no producer at all (FAC-038). Retired rather than re-keyed, and pinned so a
        // future edit cannot quietly reintroduce a free source for any of them.
        [Theory]
        [InlineData(ItemKind.ProcessedGood)]
        [InlineData(ItemKind.Hydrogen)]
        [InlineData(ItemKind.Oxygen)]
        public void FreeMintScale_ManufacturedChannels_AreNeverFreelyMinted(ItemKind kind)
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode rich = NodeAt(new Vec3(1000, 0, 0), "Iron");
            Assert.Equal(0.0, NodeContent.FreeMintScaleFor(rich, kind, t), 6);
        }

        // The shipped table states stone and nothing else: a node's real ores are its deposit's, so the
        // curated five-ore fallback mix every per-kind row used to carry is gone.
        [Fact]
        public void ShippedYieldTable_StatesOnlyStone()
        {
            FactionTuning t = TestSupport.Tuning();
            foreach (NodeYieldEntry e in t.NodeYieldTable)
                Assert.EndsWith("Stone", e.SubTypeId, StringComparison.Ordinal);
        }

        [Fact]
        public void CapabilityAffinity_Refinery_WantsOreUnderIt()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode withOre = NodeAt(new Vec3(1000, 0, 0), "Iron");
            SpatialNode without = NodeAt(new Vec3(1000, 0, 0));

            Assert.True(NodeContent.CapabilityAffinityFor(withOre, NodeCapabilityKind.Refinery, t)
                      > NodeContent.CapabilityAffinityFor(without, NodeCapabilityKind.Refinery, t));
        }

        [Fact]
        public void CapabilityAffinity_FuelDepot_WantsIce_NotJustAnyOre()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode ice = NodeAt(new Vec3(1000, 0, 0), t.IceOreSubTypeId);
            SpatialNode iron = NodeAt(new Vec3(1000, 0, 0), "Iron");

            Assert.True(NodeContent.CapabilityAffinityFor(ice, NodeCapabilityKind.FuelDepot, t)
                      > NodeContent.CapabilityAffinityFor(iron, NodeCapabilityKind.FuelDepot, t));
        }

        // A shipyard wants to sit where industry already stands. That is what the retired table's
        // Core-label 0.9 row was reaching for, and it is a property of what has been built rather than
        // of where the node is -- so it beats a node on a body with nothing on it.
        [Fact]
        public void CapabilityAffinity_Shipyard_PrefersBuiltIndustry_OverMerelyBeingOnABody()
        {
            FactionTuning t = TestSupport.Tuning();

            SpatialNode industrial = NodeAt(new Vec3(5000, 0, 0));   // deep space, but built up
            industrial.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            SpatialNode onABody = NodeAt(new Vec3(30, 0, 0));
            onABody.PlanetId = "Tellus";

            Assert.True(NodeContent.CapabilityAffinityFor(industrial, NodeCapabilityKind.Shipyard, t)
                      > NodeContent.CapabilityAffinityFor(onABody, NodeCapabilityKind.Shipyard, t));
        }

        // Manufactory reads the site, and nothing about the label: two nodes differing only in Kind
        // must score identically.
        [Fact]
        public void CapabilityAffinity_IsTheSameForTwoIdenticalSites()
        {
            FactionTuning t = TestSupport.Tuning();
            SpatialNode a = NodeAt(new Vec3(1000, 0, 0));
            SpatialNode b = NodeAt(new Vec3(1000, 0, 0));

            foreach (NodeCapabilityKind cap in (NodeCapabilityKind[])Enum.GetValues(typeof(NodeCapabilityKind)))
                Assert.Equal(NodeContent.CapabilityAffinityFor(a, cap, t),
                             NodeContent.CapabilityAffinityFor(b, cap, t), 6);
        }


    }
}
