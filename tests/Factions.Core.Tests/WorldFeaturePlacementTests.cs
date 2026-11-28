using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-096 stage 1: inside an authored region the world recipe places nodes, not the
    /// three-bucket gravity heuristic. See docs/economy-territory.md, "Placement authority".</summary>
    public class WorldFeaturePlacementTests
    {
        private static readonly Vec3 Origin = new Vec3(0, 0, 0);

        /// <summary>A ring in the shape Cauldron's was authored in when FAC-096 was filed: 180-450 km,
        /// half-thickness 1.8 km, disc normal +Y. Only the first bands matter here — they are the ones
        /// the gravity heuristic misreads as Surface.
        /// <para><b>These are fixture coordinates, not live ones.</b> The live ring moved outward to
        /// 195-465 km on 2026-08-25 so its first band clears the 191.3 km atmosphere. The geometry
        /// under test is the same either way, so the fixture is left where the measured defect was
        /// taken — but do not read these numbers as the world's.</para></summary>
        private static WorldFeature CauldronRing()
        {
            return new WorldFeature
            {
                Name = "Cauldron Ring",
                Shape = WorldFeatureShape.Ring,
                Center = Origin,
                Normal = new Vec3(0, 1, 0),
                InnerRadiusKm = 180.0,
                OuterRadiusKm = 450.0,
                HalfThicknessKm = 1.8,
                Bands = new List<FeatureBand>
                {
                    new FeatureBand { InnerRadiusKm = 180.0,  OuterRadiusKm = 193.5 },
                    new FeatureBand { InnerRadiusKm = 198.9,  OuterRadiusKm = 225.9 },
                    new FeatureBand { InnerRadiusKm = 231.3,  OuterRadiusKm = 256.68 },
                    new FeatureBand { InnerRadiusKm = 265.59, OuterRadiusKm = 304.2 },
                },
            };
        }

        private static WorldFeaturePlacement Placement(params WorldFeature[] features)
        {
            return new WorldFeaturePlacement(features, null);
        }

        /// <summary>
        /// The gas giant from the live measurement (`factions gravityprobe RealGasGiant`,
        /// 2026-08-25): a 9.5 km voxel core inside a 191.3 km atmosphere, gravity limit 304.89 km.
        /// Gravity does not peak at the surface -- it rises to 2.0 g by 39 km and holds it to 157 km
        /// -- which is exactly why relative-gravity classification calls everything out to ~220 km
        /// Surface on this body.
        /// </summary>
        private sealed class GasGiantCatalogue : IPlanetCatalogue
        {
            internal const double SurfaceR = 9.5;
            internal const double GravityR = 304.89;

            private static readonly double[] _r = { 0, 9.5, 39.04, 157.19, 186.73, 216.27, 245.81, 275.35, 304.89 };
            private static readonly double[] _g = { 0, 1.0556, 2.0, 2.0, 1.5467, 0.5533, 0.2258, 0.1020, 0.0 };

            internal readonly PlanetRecord Record = new PlanetRecord
            {
                Id = "Cauldron", Center = new Vec3(0, 0, 0),
                SurfaceRadius = SurfaceR, GravityRadius = GravityR,
                AtmosphereRadius = 191.3, HasAtmosphere = true,
            };

            public IEnumerable<PlanetRecord> Planets { get { yield return Record; } }

            public double GravityAt(Vec3 posKm)
            {
                double d = posKm.DistanceTo(Record.Center);
                if (d >= GravityR) return 0;
                for (int i = 1; i < _r.Length; i++)
                {
                    if (d > _r[i]) continue;
                    double t = (d - _r[i - 1]) / (_r[i] - _r[i - 1]);
                    return _g[i - 1] + t * (_g[i] - _g[i - 1]);
                }
                return 0;
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm)
            {
                double d = posKm.DistanceTo(Record.Center);
                if (d < 1e-6) return new Vec3(SurfaceR, 0, 0);
                double k = SurfaceR / d;
                return new Vec3(posKm.X * k, posKm.Y * k, posKm.Z * k);
            }

            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private static FactionTuning ZoneTuning()
        {
            return new FactionTuning
            {
                OrbitGravityMin = 0.05,
                NodeSeparationSurfaceKm = 15.0,
                NodeSeparationOrbitKm = 30.0,
                NodeSeparationDeepSpaceKm = 500.0,
                NodeExpansionReachPct = 0.25,
                NodeExpansionSampleCount = 4,
            };
        }

        private static double RadialFrom(Vec3 pos) { return System.Math.Sqrt(pos.X * pos.X + pos.Z * pos.Z); }

        /// <summary>A radius just inside where <see cref="GasGiantCatalogue"/>'s gravity curve puts
        /// the orbit band under <see cref="ZoneTuning"/>'s <c>OrbitGravityMin</c> of 0.05 — so the
        /// snap still wants to push a node here outward, which is what
        /// <see cref="WorldModel.ReclassifyNodes"/>'s own gate requires before it will snap at all —
        /// and inside the fixture ring's fourth band (265.59-304.2 km), so the ring claims it too.
        /// That the snap's move from here really is the shorter one is pinned by
        /// <see cref="AtThatPoint_TheOrbitSnapMovesLessThanTheRingDoes"/> rather than asserted.</summary>
        private const double JustInsideOrbitBandR = 280.0;

        /// <summary>Where a clamp lands a point it had to move: inside the face by
        /// <see cref="WorldFeature.ContainmentInsetKm"/>, not on it.</summary>
        private const double SlabFaceY = 1.8 - WorldFeature.ContainmentInsetKm;

        // ---- geometry: nearest point inside ----------------------------------------------------

        [Fact]
        public void ANodeAlreadyInsideABand_MovesZero()
        {
            WorldFeature ring = CauldronRing();
            var inside = new Vec3(186.73, 0.5, 0);

            Vec3 placed;
            Assert.True(Placement(ring).TryPlace(inside, out placed));
            Assert.Equal(0.0, placed.DistanceTo(inside), 9);
        }

        [Fact]
        public void ANodeInAGapBetweenBands_GoesToTheNearerBandEdge_KeepingItsAzimuth()
        {
            WorldFeature ring = CauldronRing();
            var inGap = new Vec3(0, 0, 195.0);   // between band 1 (..193.5) and band 2 (198.9..)

            Vec3 placed;
            Assert.True(Placement(ring).TryPlace(inGap, out placed));

            Assert.Equal(193.5 - WorldFeature.ContainmentInsetKm, RadialFrom(placed), 6);   // the nearer edge, 1.5 km away
            Assert.True(placed.Z > 0);                    // same side of the disc: azimuth preserved
            Assert.Equal(0.0, placed.X, 6);
        }

        [Fact]
        public void ANodeAboveTheDisc_HasItsHeightClamped_AndItsRadiusPreserved()
        {
            WorldFeature ring = CauldronRing();
            var above = new Vec3(210.0, 40.0, 0);   // radially inside band 2, 40 km off the plane

            Vec3 placed;
            Assert.True(Placement(ring).TryPlace(above, out placed));

            Assert.Equal(SlabFaceY, placed.Y, 6);           // clamped to just inside HalfThicknessKm
            Assert.Equal(210.0, RadialFrom(placed), 6);     // radius untouched
        }

        [Fact]
        public void ARingGivesUpTheHoleInItsMiddle_SoTheBodyItHangsOffStillPlacesItsOwnNodes()
        {
            WorldFeature ring = CauldronRing();

            Vec3 placed;
            Assert.False(Placement(ring).TryPlace(new Vec3(5, 0, 0), out placed));      // near the core
            Assert.False(Placement(ring).TryPlace(new Vec3(0, 400, 0), out placed));    // up the axis
        }

        [Fact]
        public void ARingClaimsNothingBeyondItsOwnOuterRadius()
        {
            WorldFeature ring = CauldronRing();

            Vec3 placed;
            Assert.False(Placement(ring).TryPlace(new Vec3(600, 0, 0), out placed));
            Assert.False(Placement(ring).TryPlace(new Vec3(300, 800, 0), out placed));  // far off the plane
        }

        [Fact]
        public void ASolidRingWithNoBands_ClampsIntoTheWholeAnnulus()
        {
            WorldFeature ring = CauldronRing();
            ring.Bands = new List<FeatureBand>();

            Vec3 placed;
            Assert.True(Placement(ring).TryPlace(new Vec3(195.0, 0, 0), out placed));
            Assert.Equal(195.0, RadialFrom(placed), 6);   // no gaps, so nothing to clamp to
        }

        [Fact]
        public void AShellClampsRadiusAndKeepsDirection_AndKeepsItsHole()
        {
            var shell = new WorldFeature
            {
                Name = "Tellus Orbit", Shape = WorldFeatureShape.Shell,
                Center = Origin, InnerRadiusKm = 40, OuterRadiusKm = 60,
            };

            Vec3 placed;
            // Unlike a ring, a shell claims its hole: it is meant to compete with the body inside it.
            Assert.True(Placement(shell).TryPlace(new Vec3(0, 10, 0), out placed));
            Assert.Equal(40.0 + WorldFeature.ContainmentInsetKm, placed.DistanceTo(Origin), 6);
            Assert.Equal(0.0, placed.X, 6);              // direction preserved

            Assert.True(Placement(shell).TryPlace(new Vec3(50, 0, 0), out placed));
            Assert.Equal(0.0, placed.DistanceTo(new Vec3(50, 0, 0)), 9);   // already inside
        }

        [Fact]
        public void ASphereNeverDisplacesAPointItClaims()
        {
            var sphere = new WorldFeature
            {
                Name = "Pocket", Shape = WorldFeatureShape.Sphere, Center = Origin, OuterRadiusKm = 50,
            };

            Vec3 placed;
            Assert.True(Placement(sphere).TryPlace(new Vec3(30, 20, 10), out placed));
            Assert.Equal(0.0, placed.DistanceTo(new Vec3(30, 20, 10)), 9);
            Assert.Equal(0.0, sphere.MaxPlacementDisplacementKm(), 9);
        }

        [Fact]
        public void TheInnermostClaimingFeatureWins()
        {
            var broad = new WorldFeature { Name = "Belt", Shape = WorldFeatureShape.Shell, Center = Origin, InnerRadiusKm = 100, OuterRadiusKm = 500 };
            var pocket = new WorldFeature { Name = "Pocket", Shape = WorldFeatureShape.Shell, Center = Origin, InnerRadiusKm = 200, OuterRadiusKm = 220 };

            Vec3 placed;
            Assert.True(Placement(broad, pocket).TryPlace(new Vec3(150, 0, 0), out placed));
            Assert.Equal(200.0 + WorldFeature.ContainmentInsetKm, placed.DistanceTo(Origin), 6);   // the pocket placed it, not the belt
        }

        // ---- the live regression: a ring node is not dragged onto the voxel core ----------------

        private static WorldModel GasGiantWorld(params WorldFeature[] features)
        {
            var w = new WorldModel(new SeededRandom(7))
            {
                Tuning = ZoneTuning(),
                Planets = new GasGiantCatalogue(),
            };
            if (features.Length > 0) w.Features = Placement(features);
            return w;
        }

        /// <summary>The gas giant as the live world authors it: nothing may land on it
        /// (<c>AllowSurfaceNodes: false</c> in the genesis recipe). The whole of a well is Surface by
        /// gravity, so on this body it is that rule, not a low-gravity reading, that makes the upper
        /// well Orbit and puts the orbit snap in play at all.</summary>
        private static WorldModel UnlandableGasGiantWorld(params WorldFeature[] features)
        {
            WorldModel w = GasGiantWorld(features);
            ((GasGiantCatalogue)w.Planets).Record.AllowsSurfaceStations = false;
            return w;
        }

        [Fact]
        public void WithoutAFeature_TheHeuristicDragsARingNodeOntoTheVoxelCore()
        {
            // The defect FAC-096 reports, pinned so the fix below is measured against it rather than
            // asserted in a vacuum.
            WorldModel w = GasGiantWorld();
            SpatialNode n = w.Graph.Add(new Vec3(186.73, 0, 0), 0.5, "N1");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(NodeZone.Surface, w.ClassifyZone(new Vec3(186.73, 0, 0)));
            Assert.Equal(1, r.Repositioned);
            Assert.Equal(GasGiantCatalogue.SurfaceR, n.Position.DistanceTo(Origin), 3);
        }

        [Fact]
        public void WithTheRingAuthored_ANodeInBandOneStaysWhereTheRecipePutIt()
        {
            WorldModel w = GasGiantWorld(CauldronRing());
            SpatialNode n = w.Graph.Add(new Vec3(186.73, 0, 0), 0.5, "N1");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(0, r.Repositioned);
            Assert.Equal(0, r.FeaturePlaced);                       // already where it belongs
            Assert.Equal(186.73, n.Position.DistanceTo(Origin), 6);
        }

        [Fact]
        public void ANodeOffTheRingPlane_IsPulledIntoTheRing_NotDownToTheCore()
        {
            WorldModel w = GasGiantWorld(CauldronRing());
            SpatialNode n = w.Graph.Add(new Vec3(186.73, 20, 0), 0.5, "N1");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(1, r.FeaturePlaced);
            Assert.Equal(0, r.Repositioned);
            Assert.Equal(SlabFaceY, n.Position.Y, 6);
            Assert.Equal(186.73, RadialFrom(n.Position), 6);
        }

        /// <summary>FAC-213: a node a hand's breadth outside the ring is outside it completely — the
        /// ore seam declines the position and its survey stops forever — so containment, not the size
        /// of the correction, has to decide whether it is snapped back.
        ///
        /// <para>The overshoot here is the one measured live on 2026-08-31: 189 of the estate's 428
        /// ring nodes sat at exactly 0.0005 km beyond <c>HalfThicknessKm</c>, every one of them under
        /// the 1 m orbit-snap floor that was gating this branch, and therefore never corrected.</para>
        /// </summary>
        [Fact]
        public void ANodeHalfAMetreOutsideTheRing_IsStillPulledBackIn()
        {
            WorldModel w = GasGiantWorld(CauldronRing());
            SpatialNode n = w.Graph.Add(new Vec3(210.0, 1.8005, 0), 0.5, "N1");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(1, r.FeaturePlaced);
            Assert.Equal(SlabFaceY, n.Position.Y, 6);
            Assert.Equal(210.0, RadialFrom(n.Position), 6);
        }

        /// <summary>The other half of the same rule: being inside must still cost nothing, or the
        /// smaller floor turns every boot into a rewrite of every brain file.</summary>
        [Fact]
        public void ANodeExactlyOnTheRingBoundary_IsLeftAlone()
        {
            WorldModel w = GasGiantWorld(CauldronRing());
            w.Graph.Add(new Vec3(210.0, 1.8, 0), 0.5, "N1");

            Assert.Equal(0, w.ReclassifyNodes(true).FeaturePlaced);
        }

        [Fact]
        public void FeaturePlacementIsIdempotent_SoItDoesNotRewriteEveryBrainEveryBoot()
        {
            WorldModel w = GasGiantWorld(CauldronRing());
            w.Graph.Add(new Vec3(186.73, 20, 0), 0.5, "N1");

            Assert.Equal(1, w.ReclassifyNodes(true).FeaturePlaced);
            Assert.Equal(0, w.ReclassifyNodes(true).FeaturePlaced);
        }

        [Fact]
        public void ANodeOnTheBodyItself_IsStillPlacedByThePlanet()
        {
            // The ring gives up its hole, so nothing competes for a node near the core.
            WorldModel w = GasGiantWorld(CauldronRing());
            SpatialNode n = w.Graph.Add(new Vec3(4, 0, 0), 0.5, "N1");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(1, r.Repositioned);
            Assert.Equal(0, r.FeaturePlaced);
            Assert.Equal(GasGiantCatalogue.SurfaceR, n.Position.DistanceTo(Origin), 3);
        }

        /// <summary>FAC-225: the shape of the defect. A ring is thin and wide, so pulling a point
        /// into its plane is a move of at most <see cref="WorldFeature.HalfThicknessKm"/> while
        /// snapping it radially to the orbit band is however far it sits from that radius. Close to
        /// the band radius and far off the plane, the radial move is the shorter one — which is what
        /// put 120 of the pre-re-seed world's ring nodes on Cauldron's gravity limit instead of in
        /// its ring, all in a single 25 km radius bucket.
        ///
        /// <para>The radial distance here is <see cref="JustInsideOrbitBandR"/>, so the orbit snap's
        /// own move is a few km against the ring's 28, and it would win a displacement contest
        /// outright. It no longer gets one.</para>
        /// </summary>
        [Fact]
        public void ANodeOffThePlaneInsideTheOrbitBand_GoesToTheRing_NotToTheGravityLimit()
        {
            WorldModel w = UnlandableGasGiantWorld(CauldronRing());
            SpatialNode n = w.Graph.Add(new Vec3(JustInsideOrbitBandR, 30, 0), 0.5, "N1");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(NodeZone.Orbit, w.PlaceableZoneAt(new Vec3(JustInsideOrbitBandR, 30, 0)));
            Assert.Equal(1, r.FeaturePlaced);
            Assert.Equal(0, r.OrbitRepositioned);
            Assert.Equal(SlabFaceY, n.Position.Y, 6);
            Assert.Equal(JustInsideOrbitBandR, RadialFrom(n.Position), 6);
        }

        /// <summary>The same point through the fresh-placement path <c>ExpandFrom</c> and the estate
        /// seeder use, not the retrofit pass — the rule lives in one place and both read it.</summary>
        [Fact]
        public void PlaceNode_GivesTheRingTheOrbitSnapsPoint()
        {
            WorldModel w = UnlandableGasGiantWorld(CauldronRing());
            var pos = new Vec3(JustInsideOrbitBandR, 30, 0);

            Vec3 placed = w.PlaceNode(pos, w.PlaceableZoneAt(pos));

            Assert.Equal(SlabFaceY, placed.Y, 6);
            Assert.Equal(JustInsideOrbitBandR, RadialFrom(placed), 6);
        }

        /// <summary>The measurement that makes the test above mean something: the orbit snap really
        /// does move this point less. Without it the assertion could pass because the feature was
        /// winning the old displacement contest anyway.</summary>
        [Fact]
        public void AtThatPoint_TheOrbitSnapMovesLessThanTheRingDoes()
        {
            WorldModel w = UnlandableGasGiantWorld(CauldronRing());
            var pos = new Vec3(JustInsideOrbitBandR, 30, 0);

            // No feature: the orbit snap is the only authority, so this is its move alone.
            WorldModel bare = UnlandableGasGiantWorld();
            SpatialNode snapped = bare.Graph.Add(pos, 0.5, "N1");
            bare.ReclassifyNodes(true);
            double orbitMove = snapped.Position.DistanceTo(pos);

            Vec3 featurePos;
            Assert.True(w.Features.TryPlace(pos, out featurePos));
            double featureMove = featurePos.DistanceTo(pos);

            Assert.True(orbitMove < featureMove,
                "orbitMove=" + orbitMove + " featureMove=" + featureMove);
        }

        /// <summary>Outside every feature's claim the orbit snap is untouched — it is the fallback for
        /// where nothing is authored, which is the whole point of taking its precedence away inside a
        /// claim.</summary>
        [Fact]
        public void OutsideTheRingsClaim_TheOrbitSnapStillPlacesTheNode()
        {
            WorldModel w = UnlandableGasGiantWorld(CauldronRing());
            // Over the pole, inside the ring's hole: the ring gives that up, so nothing competes,
            // and the body forbids landing, so the well there is Orbit.
            SpatialNode n = w.Graph.Add(new Vec3(100, 200, 0), 0.5, "N1");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(0, r.FeaturePlaced);
            Assert.Equal(1, r.OrbitRepositioned);
        }

        [Fact]
        public void AShellOverAPlanet_LosesToTheSurfaceSnapForANodeJustOffTheGround()
        {
            // "Planets as rings without asteroids": a shell claims the body inside it, so the two
            // authorities must be settled by displacement or surface stations would be flung upward.
            var shell = new WorldFeature
            {
                Name = "Cauldron Shell", Shape = WorldFeatureShape.Shell,
                Center = Origin, InnerRadiusKm = 200, OuterRadiusKm = 260,
            };
            WorldModel w = GasGiantWorld(shell);
            SpatialNode n = w.Graph.Add(new Vec3(11, 0, 0), 0.5, "N1");   // 1.5 km off a 9.5 km surface

            w.ReclassifyNodes(true);

            Assert.Equal(GasGiantCatalogue.SurfaceR, n.Position.DistanceTo(Origin), 3);
        }

        [Fact]
        public void ExpandFrom_KeepsANewNodeInsideTheRing()
        {
            // Authored as the live ring is. Unauthored, the ring's points read the well's 15 km
            // Surface floor and a candidate that fell through its hole onto the core scores the same.
            WorldFeature authored = CauldronRing();
            authored.Zone = NodeZone.Orbit;
            authored.NodeSeparationKm = 30.0;
            WorldModel w = GasGiantWorld(authored);
            SpatialNode from = w.Graph.Add(new Vec3(210, 0, 0), 0.5, "N1");
            from.OwnerFactionTag = "F";

            SpatialNode child = w.ExpandFrom("F", from.Id);

            WorldFeature ring = CauldronRing();
            Assert.True(ring.Contains(child.Position, Origin));
        }

        [Fact]
        public void MaxPlausibleSnapDistance_CoversAFeaturePlacement()
        {
            var inRing = new Vec3(300, 0, 0);
            WorldModel bare = GasGiantWorld();
            WorldModel authored = GasGiantWorld(CauldronRing());

            double featureReach = CauldronRing().MaxPlacementDisplacementKm();
            Assert.True(featureReach > bare.MaxPlausibleSnapDistanceKm(inRing));
            Assert.Equal(featureReach, authored.MaxPlausibleSnapDistanceKm(inRing), 6);
        }

        // ---- the recipe authors the zone, and zone's remaining job is separation ---------------

        [Fact]
        public void WithoutAnAuthoredZone_GravityCutsTheRingInHalf()
        {
            // The defect: Cauldron's well ends at 304.89 km, the ring spans 195-465 km, so the
            // boundary falls through the middle of band 4 -- one authored feature, two separation
            // floors, split by something that has nothing to do with the ring.
            WorldModel w = UnlandableGasGiantWorld(CauldronRing());

            Assert.Equal(NodeZone.Orbit,     w.PlaceableZoneAt(new Vec3(300, 0, 0)));   // inside the well
            Assert.Equal(NodeZone.DeepSpace, w.PlaceableZoneAt(new Vec3(310, 0, 0)));   // outside it
            Assert.NotEqual(w.Tuning.NodeSeparationFor(NodeZone.Orbit),
                            w.Tuning.NodeSeparationFor(NodeZone.DeepSpace));
        }

        [Fact]
        public void AnAuthoredZone_AppliesAcrossTheWholeRing_WhateverTheGravitySays()
        {
            WorldFeature ring = CauldronRing();
            ring.Zone = NodeZone.Orbit;
            WorldModel w = GasGiantWorld(ring);

            // Every radius the ring covers, on both sides of the gravity limit.
            foreach (double r in new double[] { 200, 250, 300, 310, 350, 400, 445 })
                Assert.Equal(NodeZone.Orbit, w.PlaceableZoneAt(new Vec3(r, 0, 0)));

            // One separation floor for the whole feature, which is the point.
            Assert.Equal(w.Tuning.NodeSeparationFor(NodeZone.Orbit),
                         w.Tuning.NodeSeparationFor(w.PlaceableZoneAt(new Vec3(445, 0, 0))));
        }

        [Fact]
        public void AnAuthoredZone_DoesNotLeakOutsideTheFeaturesClaim()
        {
            WorldFeature ring = CauldronRing();
            ring.Zone = NodeZone.Orbit;
            WorldModel w = GasGiantWorld(ring);

            // Beyond the ring's own outer radius, and in the hole it gives up, gravity still decides.
            Assert.Equal(NodeZone.DeepSpace, w.PlaceableZoneAt(new Vec3(5000, 0, 0)));
            Assert.Equal(NodeZone.Surface,   w.PlaceableZoneAt(new Vec3(5, 0, 0)));
        }

        [Fact]
        public void ClassifyZoneItselfIsUntouched_SoTheGravityReadingStaysAvailable()
        {
            WorldFeature ring = CauldronRing();
            ring.Zone = NodeZone.Orbit;
            WorldModel w = GasGiantWorld(ring);

            // The authored answer is a placement/separation policy, not a claim about gravity.
            Assert.Equal(NodeZone.Surface, w.ClassifyZone(new Vec3(200, 0, 0)));
            Assert.Equal(NodeZone.Orbit,   w.PlaceableZoneAt(new Vec3(200, 0, 0)));
        }

        [Fact]
        public void AnAuthoredSurfaceZone_IsStillDemotedOnABodyThatForbidsSurfaceStations()
        {
            // Belt and braces: the two rules cannot combine into something neither would allow.
            var catalogue = new GasGiantCatalogue();
            catalogue.Record.AllowsSurfaceStations = false;
            WorldFeature ring = CauldronRing();
            ring.Zone = NodeZone.Surface;
            var w = new WorldModel(new SeededRandom(7))
            {
                Tuning = ZoneTuning(), Planets = catalogue, Features = Placement(ring),
            };

            Assert.Equal(NodeZone.Orbit, w.PlaceableZoneAt(new Vec3(200, 0, 0)));
        }

        [Fact]
        public void AnAuthoredZoneSurvivesAReclassifyPass_SinceItIsReDerivedNotStored()
        {
            // Zone is not persisted; ReclassifyNodes rebuilds it every boot. An authored zone that
            // did not come back from the region would silently revert to the gravity answer.
            WorldFeature ring = CauldronRing();
            ring.Zone = NodeZone.Orbit;
            WorldModel w = GasGiantWorld(ring);
            SpatialNode n = w.Graph.Add(new Vec3(350, 0, 0), 0.5, "N1");   // outside the well

            w.ReclassifyNodes(true);
            Assert.Equal(NodeZone.Orbit, n.Zone);

            n.Zone = NodeZone.DeepSpace;          // as a fresh boot leaves it
            w.ReclassifyNodes(true);
            Assert.Equal(NodeZone.Orbit, n.Zone);
        }

        [Fact]
        public void AFeatureWithNoAuthoredZone_DoesNotCancelABroaderOnesOpinion()
        {
            // Innermost-wins must only run among features that actually author a zone.
            WorldFeature ring = CauldronRing();
            ring.Zone = NodeZone.Orbit;
            var pocket = new WorldFeature
            {
                Name = "Placement Only", Shape = WorldFeatureShape.Sphere,
                Center = new Vec3(250, 0, 0), OuterRadiusKm = 20,
            };
            WorldModel w = GasGiantWorld(ring, pocket);

            Assert.Equal(NodeZone.Orbit, w.PlaceableZoneAt(new Vec3(250, 0, 0)));
        }

        [Fact]
        public void ParsingAFeature_ReadsAnAuthoredZoneByName()
        {
            const string json = @"{ ""Features"": [
                { ""Name"": ""Cauldron Ring"", ""Shape"": ""Ring"", ""Zone"": ""Orbit"",
                  ""InnerRadiusKm"": 195, ""OuterRadiusKm"": 465, ""HalfThicknessKm"": 1.8 } ] }";

            string status;
            WorldFeature f = Assert.Single(WorldFeatureFile.Parse(json, out status));

            Assert.Equal(NodeZone.Orbit, f.Zone);
        }

        [Fact]
        public void ParsingAFeatureWithNoZoneKey_DefersToGravity()
        {
            const string json = @"{ ""Features"": [
                { ""Name"": ""Belt"", ""Shape"": ""Sphere"", ""OuterRadiusKm"": 50 } ] }";

            string status;
            WorldFeature f = Assert.Single(WorldFeatureFile.Parse(json, out status));

            Assert.Null(f.Zone);
        }

        // ---- separation is authored per region and per body ------------------------------------

        [Fact]
        public void WithNothingAuthored_SeparationIsTheGlobalPerZoneFloor()
        {
            WorldModel w = GasGiantWorld();

            // The whole well is Surface, low in it and high.
            Assert.Equal(w.Tuning.NodeSeparationSurfaceKm,   w.SeparationAtKm(new Vec3(5, 0, 0)), 6);
            Assert.Equal(w.Tuning.NodeSeparationSurfaceKm,   w.SeparationAtKm(new Vec3(250, 0, 0)), 6);
            Assert.Equal(w.Tuning.NodeSeparationDeepSpaceKm, w.SeparationAtKm(new Vec3(5000, 0, 0)), 6);
            // Orbit without an authored region: a well nothing may land on.
            Assert.Equal(w.Tuning.NodeSeparationOrbitKm,
                         UnlandableGasGiantWorld().SeparationAtKm(new Vec3(250, 0, 0)), 6);
        }

        [Fact]
        public void AnAuthoredFeatureSeparation_AppliesAcrossTheWholeRegion()
        {
            WorldFeature ring = CauldronRing();
            ring.NodeSeparationKm = 30.0;
            WorldModel w = GasGiantWorld(ring);

            // Both sides of the gravity limit, where the global floors are 30 and 500.
            Assert.Equal(30.0, w.SeparationAtKm(new Vec3(250, 0, 0)), 6);
            Assert.Equal(30.0, w.SeparationAtKm(new Vec3(400, 0, 0)), 6);
        }

        [Fact]
        public void AnAuthoredFeatureSeparation_IsNotCapped()
        {
            // Operator, 2026-08-26: "No cap, authored intent wins." A deep-space feature at 15 km
            // holds orders of magnitude more nodes than the 500 km floor, and that is the point.
            var belt = new WorldFeature
            {
                Name = "FCTM Belt", Shape = WorldFeatureShape.Sphere,
                Center = new Vec3(50000, 0, 0), OuterRadiusKm = 200, NodeSeparationKm = 15.0,
            };
            WorldModel w = GasGiantWorld(belt);

            Assert.Equal(15.0, w.SeparationAtKm(new Vec3(50050, 0, 0)), 6);
            Assert.Equal(w.Tuning.NodeSeparationDeepSpaceKm, w.SeparationAtKm(new Vec3(60000, 0, 0)), 6);
        }

        [Fact]
        public void AnAuthoredBodySeparation_AppliesAcrossItsWellOnly()
        {
            var planets = new GasGiantCatalogue();
            planets.Record.NodeSeparationKm = 10.0;
            var w = new WorldModel(new SeededRandom(7)) { Tuning = ZoneTuning(), Planets = planets };

            Assert.Equal(10.0, w.SeparationAtKm(new Vec3(GasGiantCatalogue.SurfaceR, 0, 0)), 6);
            // High in the well is still the body's: a node there is placed on its ground.
            Assert.Equal(10.0, w.SeparationAtKm(new Vec3(250, 0, 0)), 6);
            // Outside the well it is not a surface question any more.
            Assert.Equal(w.Tuning.NodeSeparationDeepSpaceKm, w.SeparationAtKm(new Vec3(400, 0, 0)), 6);
        }

        [Fact]
        public void AFeatureBeatsTheBody_WhereBothAuthorTheSamePlace()
        {
            var planets = new GasGiantCatalogue();
            planets.Record.NodeSeparationKm = 10.0;
            var shell = new WorldFeature
            {
                Name = "Hugging Shell", Shape = WorldFeatureShape.Shell,
                Center = Origin, InnerRadiusKm = 0, OuterRadiusKm = 400, NodeSeparationKm = 42.0,
            };
            var w = new WorldModel(new SeededRandom(7))
            {
                Tuning = ZoneTuning(), Planets = planets, Features = Placement(shell),
            };

            Assert.Equal(42.0, w.SeparationAtKm(new Vec3(GasGiantCatalogue.SurfaceR, 0, 0)), 6);
        }

        [Fact]
        public void ExpandFrom_HonoursAnAuthoredSeparation()
        {
            // The floor is what ExpandFrom rejects candidates against, so an authored value has to
            // reach it or the whole change is cosmetic.
            WorldFeature ring = CauldronRing();
            ring.Zone = NodeZone.Orbit;
            ring.NodeSeparationKm = 30.0;
            WorldModel w = GasGiantWorld(ring);

            SpatialNode from = w.Graph.Add(new Vec3(250, 0, 0), 0.5, "N1");
            from.OwnerFactionTag = "F";
            SpatialNode child = w.ExpandFrom("F", from.Id);

            Assert.True(child.Position.DistanceTo(from.Position) >= 30.0,
                "expected at least the authored 30 km, got " + child.Position.DistanceTo(from.Position));
        }

        [Fact]
        public void DensifyBucketGrows_WhenAnAuthoredSeparationExceedsEveryGlobalFloor()
        {
            // MaxReachCap sizes the spatial bucket DensifyTopology scans with. A bucket smaller than
            // the reach being searched drops pairs silently, so an authored value above every global
            // floor has to be counted.
            var belt = new WorldFeature
            {
                Name = "Sparse Belt", Shape = WorldFeatureShape.Sphere,
                Center = Origin, OuterRadiusKm = 4000, NodeSeparationKm = 900.0,
            };
            WorldModel w = GasGiantWorld(belt);
            SpatialNode a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A");
            SpatialNode b = w.Graph.Add(new Vec3(1000, 0, 0), 0.5, "B");

            TopologyDensifyResult r = w.DensifyTopology(applyFix: true);

            // 900 * 1.25 = 1125 km reach on both sides, so a 1000 km pair links -- which it cannot do
            // if the bucket was still sized off the 500 km deep-space floor.
            Assert.Equal(1, r.LinksAdded);
            Assert.Contains(b.Id, a.Neighbors);
        }

        // ---- an unauthored world does not move ------------------------------------------------

        [Fact]
        public void AWorldWithNoFeatures_BehavesExactlyAsItDidBefore()
        {
            var w = new WorldModel(new SeededRandom(7)) { Tuning = ZoneTuning(), Planets = new GasGiantCatalogue() };

            Assert.Equal(0, w.Features.Count);
            w.Graph.Add(new Vec3(186.73, 20, 0), 0.5, "N1");
            w.Graph.Add(new Vec3(2000, 0, 0), 0.5, "N2");        // outside every well

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(0, r.FeaturePlaced);
            Assert.Equal(1, r.Repositioned);
            Assert.Equal(1, r.DeepSpace);
        }

        [Fact]
        public void AFeatureThatClaimsNothingNearby_LeavesDeepSpaceNodesAlone()
        {
            WorldModel w = GasGiantWorld(CauldronRing());
            SpatialNode n = w.Graph.Add(new Vec3(5000, 0, 0), 0.5, "N1");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(0, r.FeaturePlaced);
            Assert.Equal(5000.0, n.Position.DistanceTo(Origin), 6);
        }

        // ---- FAC-175: the retrofit may not create the crowding placement refuses -----------------

        /// <summary>The live ring's own authoring: it states both the zone it is and the separation it
        /// keeps, so a node inside it is judged by 30 km and not by whatever gravity would have
        /// called that point.</summary>
        private static WorldFeature RingWithAuthoredSeparation()
        {
            WorldFeature ring = CauldronRing();
            ring.Zone = NodeZone.Orbit;
            ring.NodeSeparationKm = 30.0;
            return ring;
        }

        [Fact]
        public void ANodeIsNotPulledIntoTheRing_WhenThatWouldLandItOnTopOfANeighbour()
        {
            // 40 km apart standing, 1.8 km apart once the slab clamp has had both of them -- the
            // shape of all 158 crowded ring nodes measured live on 2026-08-29.
            WorldModel w = GasGiantWorld(RingWithAuthoredSeparation());
            w.Graph.Add(new Vec3(186.73, 0, 0), 0.5, "N1");            // already in band 1
            SpatialNode drifting = w.Graph.Add(new Vec3(186.73, 40, 0), 0.5, "N2");

            var log = new List<string>();
            NodeReclassifyResult r = w.ReclassifyNodes(true, log);

            Assert.Equal(0, r.FeaturePlaced);
            Assert.Equal(1, r.SeparationBlocked);
            Assert.Equal(40.0, drifting.Position.Y, 6);               // left exactly where it stood
            Assert.Contains(log, l => l.Contains("NODE_REPOSITION_BLOCKED node=N2")
                                      && l.Contains("neighbor=N1"));
        }

        [Fact]
        public void TheSameNodeIsStillPulledIn_WhenNothingIsInTheWay()
        {
            // The guard refuses a crowding move, not every move -- without N1 this is the retrofit
            // ANodeOffTheRingPlane_IsPulledIntoTheRing_NotDownToTheCore asserts.
            WorldModel w = GasGiantWorld(RingWithAuthoredSeparation());
            SpatialNode drifting = w.Graph.Add(new Vec3(186.73, 40, 0), 0.5, "N2");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(1, r.FeaturePlaced);
            Assert.Equal(0, r.SeparationBlocked);
            Assert.Equal(SlabFaceY, drifting.Position.Y, 6);
        }

        [Fact]
        public void ADryRunReportsTheBlockTheApplyWouldMake()
        {
            WorldModel w = GasGiantWorld(RingWithAuthoredSeparation());
            w.Graph.Add(new Vec3(186.73, 0, 0), 0.5, "N1");
            SpatialNode drifting = w.Graph.Add(new Vec3(186.73, 40, 0), 0.5, "N2");

            NodeReclassifyResult dry = w.ReclassifyNodes(false);

            Assert.Equal(1, dry.SeparationBlocked);
            Assert.Equal(0, dry.FeaturePlaced);
            Assert.Equal(40.0, drifting.Position.Y, 6);
        }

        [Fact]
        public void CrowdingAlreadyStanding_IsCountedEvenWhereThePassMovesNothing()
        {
            // Both nodes are already inside a band, so no branch wants to move either -- and until
            // this count existed nothing in the running world reported that they are 1 km apart
            // under a 30 km floor.
            WorldModel w = GasGiantWorld(RingWithAuthoredSeparation());
            w.Graph.Add(new Vec3(186.73, 0, 0), 0.5, "N1");
            w.Graph.Add(new Vec3(187.73, 0, 0), 0.5, "N2");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(0, r.FeaturePlaced);
            Assert.Equal(0, r.SeparationBlocked);
            Assert.Equal(2, r.StandingViolations);      // per position, both sides of the one pair
        }

        // ---- FAC-213: a moving anchor, a crowded nudge, and ore under a node the guard keeps out ----

        [Fact]
        public void AClampedNode_StaysInsideWhenTheAnchorMovesAFewMetres()
        {
            // The live gas giant read 0.0015 km on 2026-08-31 and 0.0035 km on 2026-10-09; a node
            // clamped onto the face at the first reading was 2 m outside at the second.
            WorldFeature ring = CauldronRing();
            WorldModel w = GasGiantWorld(ring);
            SpatialNode n = w.Graph.Add(new Vec3(210.0, -20, 0), 0.5, "N1");

            Assert.Equal(1, w.ReclassifyNodes(true).FeaturePlaced);
            Assert.True(ring.Contains(n.Position, new Vec3(0.003, 0.003, 0.003)));
        }

        [Fact]
        public void ANodeAlreadyCrowded_IsStillNudgedBackInsideTheRing()
        {
            // The shape of the 158 refused live on 2026-10-09: a few metres outside the slab, and
            // already inside 30 km of a neighbour, so the nudge in creates nothing.
            WorldModel w = GasGiantWorld(RingWithAuthoredSeparation());
            w.Graph.Add(new Vec3(210.0, 0, 20.0), 0.5, "N1");            // band 2, 20 km away
            SpatialNode outside = w.Graph.Add(new Vec3(210.0, 1.802, 0), 0.5, "N2");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(1, r.FeaturePlaced);
            Assert.Equal(0, r.SeparationBlocked);
            Assert.Equal(SlabFaceY, outside.Position.Y, 6);
            Assert.Equal(2, r.StandingViolations);      // the crowding is still reported, not cured
        }

        [Fact]
        public void AShortMoveThatWouldCreateCrowding_IsStillRefused()
        {
            // N2 sits 50 m outside band 2 and 30.02 km from N1; the clamp brings it to 29.98 km.
            // The nudge allowance is for a node already crowded, not for making one.
            WorldModel w = GasGiantWorld(RingWithAuthoredSeparation());
            double z = System.Math.Sqrt(30.02 * 30.02 - 20.95 * 20.95);
            w.Graph.Add(new Vec3(205.0, 0, z), 0.5, "N1");
            SpatialNode outside = w.Graph.Add(new Vec3(225.95, 0, 0), 0.5, "N2");

            NodeReclassifyResult r = w.ReclassifyNodes(true);

            Assert.Equal(0, r.FeaturePlaced);
            Assert.Equal(1, r.SeparationBlocked);
            Assert.Equal(225.95, outside.Position.X, 9);
        }

        private static WorldModel OreBearingRingWorld(out WorldFeature ring)
        {
            ring = RingWithAuthoredSeparation();
            ring.AddMaterial(new FeatureMaterial { SubTypeId = "Iron" });
            WorldModel w = GasGiantWorld(ring);
            w.Ores = new WorldFeatureGeography(new[] { ring }, null);
            return w;
        }

        [Fact]
        public void ANodeKeptOffTheRingPlane_ReadsTheOreOfTheSlabBeneathIt()
        {
            // The 10 live nodes 36-66 km off the plane: refused the reposition, and until this held
            // no deposit at all because their own position is outside every feature.
            WorldFeature ring;
            WorldModel w = OreBearingRingWorld(out ring);
            var at = new Vec3(210.0, 40, 0);
            SpatialNode n = w.Graph.Add(at, 0.5, "N1");
            n.OwnerFactionTag = "F";

            DepositResolution result;
            w.ResolveDeposit(n, out result);

            Assert.Equal(DepositResolution.Sampled, result);
            Assert.True(NodeContent.IsMineable(n));
            Assert.Equal(0.0, n.Position.DistanceTo(at), 9);     // read beneath, not moved

            long before = n.Deposit.PointsExamined;
            Assert.True(w.TrySurveyOnce(n, 16));
            Assert.True(n.Deposit.PointsExamined > before);
        }

        [Fact]
        public void ANodeNoFeatureClaims_IsStillDeclined()
        {
            WorldFeature ring;
            WorldModel w = OreBearingRingWorld(out ring);
            SpatialNode n = w.Graph.Add(new Vec3(2000.0, 0, 0), 0.5, "N1");

            DepositResolution result;
            w.ResolveDeposit(n, out result);

            Assert.Equal(DepositResolution.Declined, result);
            Assert.Null(n.Deposit);
        }

        [Fact]
        public void AWorldThatKeepsItsSpacing_ReportsNoStandingViolations()
        {
            WorldModel w = GasGiantWorld(RingWithAuthoredSeparation());
            w.Graph.Add(new Vec3(186.73, 0, 0), 0.5, "N1");
            w.Graph.Add(new Vec3(0, 0, 186.73), 0.5, "N2");           // a quarter of the way round

            Assert.Equal(0, w.ReclassifyNodes(true).StandingViolations);
        }
    

        // ---- which feature a node stands in: the key SpatialNode.FeatureKey carries -------------

        [Fact]
        public void TryFeatureAt_NamesTheClaimingFeature_AndNothingOutsideEveryFeature()
        {
            var nest = new WorldFeature
            {
                Name = "Far Nest Umbra", Shape = WorldFeatureShape.Sphere,
                Center = new Vec3(2202, 5033, -3083), OuterRadiusKm = 750,
            };
            WorldFeaturePlacement placement = Placement(nest);

            string name;
            Assert.True(placement.TryFeatureAt(new Vec3(2202 + 700, 5033, -3083), out name));
            Assert.Equal("Far Nest Umbra", name);
            Assert.False(placement.TryFeatureAt(new Vec3(2202 + 800, 5033, -3083), out name));
            Assert.Null(name);
            Assert.False(WorldFeaturePlacement.Empty.TryFeatureAt(new Vec3(2202, 5033, -3083), out name));
        }

        [Fact]
        public void TryFeatureAt_InnermostClaimantWins_SameTieBreakAsPlacement()
        {
            // A small region authored inside a broad one governs its own interior for placement;
            // the recorded feature has to be the same one, or a node would be placed by one feature
            // and counted in another.
            WorldFeature ring = CauldronRing();
            var pocket = new WorldFeature
            {
                Name = "Pocket", Shape = WorldFeatureShape.Sphere, Center = new Vec3(250, 0, 0), OuterRadiusKm = 20,
            };
            WorldFeaturePlacement placement = Placement(ring, pocket);

            string name;
            Assert.True(placement.TryFeatureAt(new Vec3(250, 0, 0), out name));
            Assert.Equal("Pocket", name);
            Assert.True(placement.TryFeatureAt(new Vec3(0, 0, 300), out name));
            Assert.Equal("Cauldron Ring", name);
        }

        [Fact]
        public void ReclassifyNodes_DerivesFeatureKey_LikeZoneAndSite()
        {
            // Not persisted, so an existing estate only learns which nest it stands in through the
            // boot pass. Two nodes inside, one outside, none stamped yet: two changes, then none.
            var nest = new WorldFeature
            {
                Name = "Far Nest Umbra", Shape = WorldFeatureShape.Sphere,
                Center = new Vec3(20000, 0, 0), OuterRadiusKm = 750,
            };
            var w = new WorldModel(new SeededRandom(1))
            {
                Tuning = TestSupport.Tuning(),
                Features = Placement(nest),
            };
            w.Graph.Add(new Vec3(20000, 0, 0), 0.5, "IN1");
            w.Graph.Add(new Vec3(20000, 600, 0), 0.5, "IN2");
            w.Graph.Add(new Vec3(40000, 0, 0), 0.5, "OUT");

            NodeReclassifyResult first = w.ReclassifyNodes(true);

            Assert.Equal(2, first.FeatureKeyChanged);
            Assert.Equal("Far Nest Umbra", w.Graph.Nodes["IN1"].FeatureKey);
            Assert.Equal("Far Nest Umbra", w.Graph.Nodes["IN2"].FeatureKey);
            Assert.Null(w.Graph.Nodes["OUT"].FeatureKey);
            Assert.Equal(0, w.ReclassifyNodes(true).FeatureKeyChanged);
        }
    }
}
