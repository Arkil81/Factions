using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Covers the node/station position-agreement rule that
    /// <c>StationMaterializer.BeginSpawn</c> (heal on approach) and
    /// <c>StationMaterializer.FixNodeStationDrift</c> (admin sweep) both gate on.</summary>
    public sealed class StationPlacementTests
    {
        // Node positions are kilometres; station positions are world-space metres.
        private static SpatialNode NodeAtKm(double x, double y, double z)
        {
            return new SpatialNode { Id = "N164", Position = new Vec3(x, y, z), StableStationId = 42 };
        }

        [Fact]
        public void Drift_IsZero_WhenStationSitsExactlyOnItsNode()
        {
            SpatialNode n = NodeAtKm(-319.775, -2414.386, 303.836);
            Assert.Equal(0.0, StationPlacement.NodeStationDriftM(n, -319775.0, -2414386.0, 303836.0), 0);
        }

        [Fact]
        public void Drift_ConvertsNodeKilometresToMetres()
        {
            SpatialNode n = NodeAtKm(1, 0, 0);
            // Station 1 km further out along X than the node => 1000 m of drift, not 999 km.
            Assert.Equal(1000.0, StationPlacement.NodeStationDriftM(n, 2000.0, 0.0, 0.0), 3);
        }

        [Fact]
        public void HasDrifted_False_ForSpawnTimeAdjustmentsInsideTolerance()
        {
            SpatialNode n = NodeAtKm(0, 0, 0);
            // Stands in for the planetary embed lift, which legitimately moves a station tens of
            // metres off its node and must not be mistaken for drift.
            Assert.False(StationPlacement.HasDriftedFromNode(n, 0.0, 80.0, 0.0));
        }

        [Fact]
        public void HasDrifted_False_ExactlyAtTolerance()
        {
            SpatialNode n = NodeAtKm(0, 0, 0);
            Assert.False(StationPlacement.HasDriftedFromNode(n, StationPlacement.NodeDriftToleranceM, 0.0, 0.0));
        }

        [Fact]
        public void HasDrifted_True_JustPastTolerance()
        {
            SpatialNode n = NodeAtKm(0, 0, 0);
            Assert.True(StationPlacement.HasDriftedFromNode(n, StationPlacement.NodeDriftToleranceM + 1.0, 0.0, 0.0));
        }

        /// <summary>The live case that motivated the fix: EXP node N164, snapped onto Triton's
        /// gravity radius by <c>WorldModel.ReclassifyNodes</c> while its station stayed put.</summary>
        [Fact]
        public void HasDrifted_True_ForTheLiveN164Case()
        {
            SpatialNode n = NodeAtKm(-319.7751747756897, -2414.3864107005356, 303.8362532124911);
            double drift = StationPlacement.NodeStationDriftM(n,
                -319738.74707123952, -2414561.522113781, 300382.5856011592);
            Assert.InRange(drift, 3450.0, 3470.0);
            Assert.True(StationPlacement.HasDriftedFromNode(n,
                -319738.74707123952, -2414561.522113781, 300382.5856011592));
        }

        /// <summary>Scan-backed nodes are the reversed case: the SE station is the real save asset and
        /// the node is the copy WorldScanner ingested from it, so a divergence means the NODE drifted.
        /// Re-anchoring the station onto it would teleport vanilla's own economy stations.</summary>
        [Theory]
        [InlineData("E877554327087518895")]   // live: 128 km adrift, the worst case measured
        [InlineData("G123456789")]
        public void HasDrifted_False_ForScanBackedNodes_HoweverFarApart(string scanBackedId)
        {
            var n = new SpatialNode { Id = scanBackedId, Position = new Vec3(0, 0, 0), StableStationId = 42 };
            Assert.True(StationPlacement.NodeStationDriftM(n, 128428.0, 0.0, 0.0) > StationPlacement.NodeDriftToleranceM);
            Assert.False(StationPlacement.HasDriftedFromNode(n, 128428.0, 0.0, 0.0));
        }

        [Fact]
        public void HasDrifted_True_ForEngineFoundedNodes()
        {
            var n = new SpatialNode { Id = "N702", Position = new Vec3(0, 0, 0), StableStationId = 42 };
            Assert.True(StationPlacement.HasDriftedFromNode(n, 6254.0, 0.0, 0.0));
        }

        /// <summary>A kilometre-scale founding offset used to be persisted as
        /// SpatialNode.FoundOffsetM and subtracted from the drift measurement, so the station read as
        /// "in place" wherever it stood. That field was retired 2026-08-21: the node is the whole
        /// target, and clearing an obstruction is now a spawn-local displacement that is never
        /// persisted. Live case: EXP N640 ("Isley Munitions"), 2614 m off its own node, one of seven
        /// EXP nodes that carried a legacy offset -- all invisible to the drift sweep until this
        /// changed.</summary>
        [Fact]
        public void LegacyFoundingOffsetDistance_NowReadsAsDrift()
        {
            var n = new SpatialNode { Id = "N640", Position = new Vec3(0, 0, 0), StableStationId = 42 };
            Assert.True(StationPlacement.HasDriftedFromNode(n, 2614.0, 0.0, 0.0));
            Assert.Equal(2614.0, StationPlacement.NodeStationDriftM(n, 2614.0, 0.0, 0.0), 3);
        }

        [Fact]
        public void StationTarget_IsTheBareNode()
        {
            var n = new SpatialNode { Id = "N164", Position = new Vec3(1, 2, 3), StableStationId = 42 };
            double x, y, z;
            StationPlacement.StationTargetM(n, out x, out y, out z);
            Assert.Equal(1000.0, x, 3);
            Assert.Equal(2000.0, y, 3);
            Assert.Equal(3000.0, z, 3);
        }

        /// <summary>A player faction's node is repaired like any other -- the offset field, not a
        /// faction-type carve-out, is what protects a deliberate placement. N164 (the reported case)
        /// belongs to player faction EXP, so a carve-out would have exempted it.</summary>
        [Fact]
        public void PlayerFactionNode_WithNoDeliberateOffset_IsRepairedLikeAnyOther()
        {
            var n = new SpatialNode { Id = "N164", Position = new Vec3(0, 0, 0), StableStationId = 42 };
            Assert.True(StationPlacement.HasDriftedFromNode(n, 3458.0, 0.0, 0.0));
        }

        [Fact]
        public void NullNode_ReportsNoDrift()
        {
            Assert.Equal(0.0, StationPlacement.NodeStationDriftM(null, 1e6, 1e6, 1e6), 0);
            Assert.False(StationPlacement.HasDriftedFromNode(null, 1e6, 1e6, 1e6));
        }

        // ---- Gravity alignment -------------------------------------------------------------
        // The orientation StationMaterializer.BeginSpawn gives a station standing inside a gravity
        // well but too far from any surface for the surface-snap branch to have oriented it. The
        // live case is the Cauldron Ring: measured 2026-08-29, the gas giant's gravity radius is
        // 304.9 km and its three inner ore bands span 195-271.7 km, all of it >180 km from the
        // 9.5 km surface.

        private static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        private static double Len(Vec3 a) { return Math.Sqrt(Dot(a, a)); }

        /// <summary>A gas-giant centre at the origin and a station out on the 195 km inner band,
        /// deliberately off-axis so no coordinate accident can make the test pass.</summary>
        private static Vec3 InnerBandStationM()
        {
            // |(150, 40, 115)| km ~= 195 km.
            return new Vec3(150000.0, 40000.0, 115132.0);
        }

        [Fact]
        public void GravityAlignedOrientation_PointsUpDirectlyAwayFromThePlanetCentre()
        {
            Vec3 station = InnerBandStationM();
            Vec3 centre = new Vec3(0, 0, 0);
            Vec3 up, forward;

            Assert.True(StationPlacement.GravityAlignedOrientation(
                station, centre, new Vec3(1, 0, 0), out up, out forward));

            // Up is the radial direction: its dot with the normalised station vector is exactly 1.
            double r = Len(station);
            Vec3 radial = new Vec3(station.X / r, station.Y / r, station.Z / r);
            Assert.Equal(1.0, Dot(up, radial), 9);
        }

        [Fact]
        public void GravityAlignedOrientation_ReturnsAnOrthonormalPair()
        {
            Vec3 up, forward;
            Assert.True(StationPlacement.GravityAlignedOrientation(
                InnerBandStationM(), new Vec3(0, 0, 0), new Vec3(0.3, -0.9, 0.2), out up, out forward));

            Assert.Equal(1.0, Len(up), 9);
            Assert.Equal(1.0, Len(forward), 9);
            Assert.Equal(0.0, Dot(up, forward), 9);
        }

        /// <summary>The forward is the station's OLD forward re-squared against the new Up, not an
        /// arbitrary axis -- a station keeps as much of its heading as the new Up allows.</summary>
        [Fact]
        public void GravityAlignedOrientation_KeepsTheHeadingItCan()
        {
            // A station on the +X axis: Up becomes +X, so a forward already perpendicular to +X
            // must survive untouched.
            Vec3 up, forward;
            Assert.True(StationPlacement.GravityAlignedOrientation(
                new Vec3(200000.0, 0, 0), new Vec3(0, 0, 0), new Vec3(0, 0, 1), out up, out forward));

            Assert.Equal(1.0, Dot(up, new Vec3(1, 0, 0)), 9);
            Assert.Equal(1.0, Dot(forward, new Vec3(0, 0, 1)), 9);
        }

        /// <summary>A forward parallel to the new Up carries no heading at all once projected out.
        /// The fallback must still produce a usable perpendicular rather than a zero vector -- this
        /// is the case that would otherwise hand SE a degenerate spawn matrix.</summary>
        [Fact]
        public void GravityAlignedOrientation_FallsBackWhenTheOldForwardIsTheNewUp()
        {
            Vec3 up, forward;
            Assert.True(StationPlacement.GravityAlignedOrientation(
                new Vec3(200000.0, 0, 0), new Vec3(0, 0, 0), new Vec3(1, 0, 0), out up, out forward));

            Assert.Equal(1.0, Len(forward), 9);
            Assert.Equal(0.0, Dot(up, forward), 9);
        }

        [Fact]
        public void GravityAlignedOrientation_FailsOnlyAtThePlanetCentre()
        {
            Vec3 up, forward;
            Assert.False(StationPlacement.GravityAlignedOrientation(
                new Vec3(0, 0, 0), new Vec3(0, 0, 0), new Vec3(1, 0, 0), out up, out forward));
        }

        /// <summary>The producer and the reclaim check are the same rule: an orientation
        /// GravityAlignedOrientation just produced can never be one IsGravityMisaligned rejects.
        /// Before this pair existed, StationMaterializer's realignment check demanded an orientation
        /// nothing downstream ever produced, so the reclaim it triggered could not converge.</summary>
        [Fact]
        public void AlignedOrientation_IsNeverReportedMisaligned()
        {
            Vec3 centre = new Vec3(0, 0, 0);
            Vec3[] stations =
            {
                InnerBandStationM(),
                new Vec3(200000.0, 0, 0),
                new Vec3(-96000.0, 213000.0, -44000.0),
                new Vec3(0, 271700.0, 0),
            };

            foreach (Vec3 station in stations)
            {
                Vec3 up, forward;
                Assert.True(StationPlacement.GravityAlignedOrientation(
                    station, centre, new Vec3(0.2, 0.5, -0.84), out up, out forward));
                Assert.False(StationPlacement.IsGravityMisaligned(station, centre, up));
            }
        }

        /// <summary>The complaint, stated as a test: a Ring station left standing on the world Y
        /// axis while its own radial is X is misaligned -- that is the grid you walk into sideways.
        /// The same station once aligned is not.</summary>
        [Fact]
        public void IsGravityMisaligned_FlagsAWorldAxisUpOnARingStation()
        {
            Vec3 station = new Vec3(200000.0, 0, 0);
            Vec3 centre = new Vec3(0, 0, 0);

            Assert.True(StationPlacement.IsGravityMisaligned(station, centre, new Vec3(0, 1, 0)));
            Assert.False(StationPlacement.IsGravityMisaligned(station, centre, new Vec3(1, 0, 0)));
        }

        /// <summary>The threshold is a tolerance, not an equality: a small tilt is left alone so an
        /// approach does not reclaim a grid over floating-point noise.</summary>
        [Fact]
        public void IsGravityMisaligned_ToleratesATiltInsideTheThreshold()
        {
            Vec3 station = new Vec3(200000.0, 0, 0);
            Vec3 centre = new Vec3(0, 0, 0);

            // GravityAlignmentDotMin is cos(~11.5 deg); 5 deg is inside it, 20 deg is not.
            double five = 5.0 * Math.PI / 180.0;
            double twenty = 20.0 * Math.PI / 180.0;
            Assert.False(StationPlacement.IsGravityMisaligned(
                station, centre, new Vec3(Math.Cos(five), Math.Sin(five), 0)));
            Assert.True(StationPlacement.IsGravityMisaligned(
                station, centre, new Vec3(Math.Cos(twenty), Math.Sin(twenty), 0)));
        }

        // ---- The gravity-vector form -------------------------------------------------------
        // v0.79.0 shipped only the planet-centre form, reachable solely through
        // MyGamePruningStructure.GetClosestPlanet -- which builds a DEGENERATE box at the query
        // point and asks the VOXEL tree what overlaps it, so it answers only inside a planet's own
        // AABB (~32.8 km for this gas giant). Gravity reaches 304.9 km and the ring sits at
        // 195-271.7 km, so the branch was unreachable for every station it was written for. These
        // cover the form that IS reachable there.

        /// <summary>Up is straight against the pull, whatever direction the pull happens to be.</summary>
        [Fact]
        public void FromPull_PutsUpDirectlyOppositeTheGravityVector()
        {
            Vec3 up, forward;
            // A pull toward the gas giant from a station out on the +X side of the ring.
            Assert.True(StationPlacement.GravityAlignedOrientationFromPull(
                new Vec3(-9.81, 0, 0), new Vec3(0, 0, 1), out up, out forward));

            Assert.Equal(1.0, Dot(up, new Vec3(1, 0, 0)), 9);
            Assert.Equal(1.0, Len(up), 9);
            Assert.Equal(0.0, Dot(up, forward), 9);
        }

        /// <summary>Magnitude is irrelevant — only direction. A ring station's pull is a fraction of
        /// a g, and that must orient it exactly as a 9.81 one would.</summary>
        [Fact]
        public void FromPull_IgnoresTheMagnitudeOfThePull()
        {
            Vec3 upWeak, fwdWeak, upStrong, fwdStrong;
            Vec3 dir = new Vec3(-0.37, 0.62, -0.19);

            Assert.True(StationPlacement.GravityAlignedOrientationFromPull(
                dir, new Vec3(1, 0, 0), out upWeak, out fwdWeak));
            Assert.True(StationPlacement.GravityAlignedOrientationFromPull(
                new Vec3(dir.X * 1000.0, dir.Y * 1000.0, dir.Z * 1000.0),
                new Vec3(1, 0, 0), out upStrong, out fwdStrong));

            Assert.Equal(1.0, Dot(upWeak, upStrong), 9);
            Assert.Equal(1.0, Dot(fwdWeak, fwdStrong), 9);
        }

        /// <summary>The two forms are the same rule. A pull pointing from the station at the planet
        /// centre must give exactly what the centre-based overload gives — this is what lets the
        /// host swap to the reachable one without changing behaviour anywhere it already worked.</summary>
        [Fact]
        public void FromPull_AgreesWithThePlanetCentreForm()
        {
            Vec3 station = InnerBandStationM();
            Vec3 centre = new Vec3(0, 0, 0);
            Vec3 fwd = new Vec3(0.2, 0.5, -0.84);

            Vec3 upA, fwdA, upB, fwdB;
            Assert.True(StationPlacement.GravityAlignedOrientation(station, centre, fwd, out upA, out fwdA));
            Assert.True(StationPlacement.GravityAlignedOrientationFromPull(
                new Vec3(centre.X - station.X, centre.Y - station.Y, centre.Z - station.Z),
                fwd, out upB, out fwdB));

            Assert.Equal(1.0, Dot(upA, upB), 9);
            Assert.Equal(1.0, Dot(fwdA, fwdB), 9);
        }

        [Fact]
        public void FromPull_FailsOnlyOnZeroGravity()
        {
            Vec3 up, forward;
            Assert.False(StationPlacement.GravityAlignedOrientationFromPull(
                new Vec3(0, 0, 0), new Vec3(1, 0, 0), out up, out forward));
        }

        /// <summary>Producer and check agree in the pull form too, so a reclaim converges.</summary>
        [Fact]
        public void FromPull_AlignedOrientationIsNeverReportedMisaligned()
        {
            Vec3[] pulls =
            {
                new Vec3(-9.81, 0, 0),
                new Vec3(0.31, -0.02, 0.44),
                new Vec3(-0.0007, 0.0011, -0.0003),   // the faint outer-band pull
            };

            foreach (Vec3 pull in pulls)
            {
                Vec3 up, forward;
                Assert.True(StationPlacement.GravityAlignedOrientationFromPull(
                    pull, new Vec3(0.2, 0.5, -0.84), out up, out forward));
                Assert.False(StationPlacement.IsGravityMisalignedForPull(pull, up));
            }
        }

        /// <summary>The complaint in pull form: a ring grid still standing on the world Y axis while
        /// the pull is along -X is misaligned.</summary>
        [Fact]
        public void IsGravityMisalignedForPull_FlagsAWorldAxisUp()
        {
            Assert.True(StationPlacement.IsGravityMisalignedForPull(
                new Vec3(-9.81, 0, 0), new Vec3(0, 1, 0)));
            Assert.False(StationPlacement.IsGravityMisalignedForPull(
                new Vec3(-9.81, 0, 0), new Vec3(1, 0, 0)));
        }

        [Fact]
        public void IsGravityMisalignedForPull_IsFalseWithNoPullAndWithNoUp()
        {
            Assert.False(StationPlacement.IsGravityMisalignedForPull(new Vec3(0, 0, 0), new Vec3(0, 1, 0)));
            Assert.False(StationPlacement.IsGravityMisalignedForPull(new Vec3(-9.81, 0, 0), new Vec3(0, 0, 0)));
        }

        [Fact]
        public void IsGravityMisaligned_IsFalseWhereTheRadialIsUndefined()
        {
            // On the planet centre "away from the centre" names no direction, and a zero Up axis
            // says nothing either -- neither is grounds for reclaiming a grid.
            Assert.False(StationPlacement.IsGravityMisaligned(
                new Vec3(0, 0, 0), new Vec3(0, 0, 0), new Vec3(0, 1, 0)));
            Assert.False(StationPlacement.IsGravityMisaligned(
                new Vec3(200000.0, 0, 0), new Vec3(0, 0, 0), new Vec3(0, 0, 0)));
        }
    }
}
