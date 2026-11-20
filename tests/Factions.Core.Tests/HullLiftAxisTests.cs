using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-215 slice 2: the lift gate reads the thrusters that hold a hull up in the attitude it
    /// actually flies, not the <c>Down</c> bucket.
    ///
    /// <para>The hull here is the live <c>Clipper</c>, whose author put the main cockpit on grid
    /// Backward with its up on grid Right. Belly-down it reads 0.85 TWR at 1 g and the gate refuses
    /// it; in the attitude SE flies it, on the bank pushing toward its own up, it reads 2.56.</para>
    /// </summary>
    public class HullLiftAxisTests
    {
        private const string Clipper = "Clipper";
        private const double OneG = 1.0;

        private static ThrustBand Band(float forceN)
        {
            return new ThrustBand
            {
                ThrusterType = "Atmospheric",
                ForceMagnitudeN = forceN,
                MinPlanetaryInfluence = 0f,
                MaxPlanetaryInfluence = 1f,
                EffectivenessAtMinInfluence = 1f,
                EffectivenessAtMaxInfluence = 1f,
                NeedsAtmosphereForInfluence = false,
            };
        }

        private const double DryMassKg = 100000;

        /// <summary>Down holds 0.85 of the hull's weight, Left (which pushes it toward its own up,
        /// grid Right) holds 2.56.</summary>
        private static Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ClipperBands()
        {
            return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                [ThrustDirection.Down] = new List<ThrustBand> { Band((float)(0.85 * DryMassKg * 9.81)) },
                [ThrustDirection.Left] = new List<ThrustBand> { Band((float)(2.56 * DryMassKg * 9.81)) },
            };
        }

        private static FrameFakeCatalogue Catalogue(bool withFrame)
        {
            var cat = new FrameFakeCatalogue();
            cat.Bands[Clipper] = ClipperBands();
            cat.DryMass[Clipper] = DryMassKg;
            if (withFrame)
                cat.Frames[Clipper] = new HullFrame
                {
                    Nose = ThrustDirection.Backward, Up = ThrustDirection.Right,
                    Source = HullNoseSource.MainCockpit, Known = true,
                };
            return cat;
        }

        private static ShipPool.RouteEnvironment Surface()
        {
            return new ShipPool.RouteEnvironment
            {
                OriginGravityG = OneG, DestGravityG = OneG,
                OriginInAtmosphere = true, DestInAtmosphere = true,
                OriginAirDensity = 1.0, DestAirDensity = 1.0,
                MinDryTwr = 1.05, GravityFloorG = 0.01,
            };
        }

        [Fact]
        public void LiftBand_IsTheBucketPushingTowardTheHullsOwnUp()
        {
            var frame = new HullFrame { Up = ThrustDirection.Right, Known = true };
            Assert.Equal(ThrustDirection.Left, frame.LiftBand);
        }

        [Fact]
        public void AnUnknownFrame_KeepsTheBellyDownAssumption()
        {
            Assert.Equal(ThrustDirection.Down, HullFrame.Unknown.LiftBand);
            Assert.Equal(ThrustDirection.Down, ShipPool.LiftBandFor(Catalogue(false), Clipper));
        }

        [Fact]
        public void DryTwr_ReadsTheLiftBand()
        {
            Assert.Equal(0.85, ThrustPhysics.DryTwr(ClipperBands(), DryMassKg, OneG, 1.0, true), 2);
            Assert.Equal(2.56, ThrustPhysics.DryTwr(ClipperBands(), DryMassKg, OneG, 1.0, true, ThrustDirection.Left), 2);
        }

        /// <summary>The live consequence: with the frame the gate accepts the hull at a 1 g node, and
        /// without it the same hull is refused — the two readings disagree, and only one of them is
        /// the attitude SE flies.</summary>
        [Fact]
        public void TheRouteGate_AcceptsAHullOnTheAxisItFliesAndRefusesItBellyDown()
        {
            Assert.True(ShipPool.IsRouteFeasible(Catalogue(true), Clipper, Surface()));
            Assert.False(ShipPool.IsRouteFeasible(Catalogue(false), Clipper, Surface()));
        }

        /// <summary>Cargo capacity moves with the same axis, or a hull the gate just accepted would be
        /// loaded against thrust it does not fly on.</summary>
        [Fact]
        public void CargoCapacity_MovesWithTheLiftBand()
        {
            double withFrame = Surface().MaxCargoMassKg(Catalogue(true), Clipper);
            double bellyDown = Surface().MaxCargoMassKg(Catalogue(false), Clipper);
            Assert.True(withFrame > bellyDown,
                "cargo capacity must follow the flown axis: " + withFrame + " vs " + bellyDown);
            Assert.Equal(0.0, bellyDown, 6);   // 0.85 TWR cannot clear the 1.05 floor at all
        }

        /// <summary>The route planner asks the same question about flying round a body, so it has to
        /// read the same axis; otherwise a hull the dispatch gate accepted is refused every altitude
        /// profile and the leg goes unplanned.</summary>
        [Fact]
        public void TheRouteProfileCarriesTheLiftBand()
        {
            RouteHullProfile profile = new RouteHullProfile
            {
                ThrustBands = ClipperBands(),
                DryMassKg = DryMassKg,
                MinDryTwr = 1.05,
                LiftBand = ShipPool.LiftBandFor(Catalogue(true), Clipper),
            };
            Assert.Equal(ThrustDirection.Left, profile.LiftBand);
            Assert.Equal(2.56, ThrustPhysics.DryTwr(profile.ThrustBands, profile.DryMassKg, OneG, 1.0, true, profile.LiftBand), 2);
        }

        private sealed class FrameFakeCatalogue : IPrefabCatalogue, IHullFramePrefabCatalogue
        {
            public readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> Bands
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>();
            public readonly Dictionary<string, double> DryMass = new Dictionary<string, double>();
            public readonly Dictionary<string, HullFrame> Frames = new Dictionary<string, HullFrame>();

            public HullFrame HullFrame(string prefabId)
            {
                HullFrame f;
                return Frames.TryGetValue(prefabId, out f) ? f : Factions.Core.HullFrame.Unknown;
            }

            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> b;
                return Bands.TryGetValue(prefabId, out b)
                    ? b : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }

            public double DryMassKg(string prefabId)
            {
                double v;
                return DryMass.TryGetValue(prefabId, out v) ? v : 0;
            }

            public long CargoLiters(string prefabId) { return 10000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new List<WeaponBand>(); }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new List<string>(); }
            public double DrillYieldScore(string prefabId) { return 0; }
        }
    }
}
