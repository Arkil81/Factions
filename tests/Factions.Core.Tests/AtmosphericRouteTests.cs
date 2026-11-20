using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// An atmospheric hull is routed only where its thrusters work. Geometry and hull are taken from
    /// the live case (2026-10-11): Tellus is 30 km to the average radius with a 4.86 km atmosphere,
    /// and gravity there reads 0.595 g 3.65 km up. <c>Spadi-Atmo</c> has 576 kN of atmospheric lift
    /// and a dry mass of 16,534 kg. The old low arc put its vertices at 33.65 km, where air density
    /// is 0.25 and the thrusters produce nothing.
    /// </summary>
    public class AtmosphericRouteTests
    {
        private static readonly PlanetRecord Tellus = new PlanetRecord
        {
            Id = "tellus", Name = "Tellus", Generator = "Tellus",
            Center = new Vec3(0, 0, 0), SurfaceRadius = 30, AtmosphereRadius = 34.86,
            GravityRadius = 48.785, HasAtmosphere = true,
        };

        private static readonly PlanetRecord Agni = new PlanetRecord
        {
            Id = "agni", Name = "Agni", Generator = "Agni",
            Center = new Vec3(1500, 0, 0), SurfaceRadius = 15, AtmosphereRadius = 15.759,
            GravityRadius = 24.335, HasAtmosphere = true,
        };

        /// <summary>SE's gravity shape: full strength inside the hills, falling as (R/r)^7 beyond
        /// them, nothing past the gravity radius. Hill radius chosen so 33.65 km reads 0.595 g.</summary>
        private sealed class SeGravityCatalogue : IPlanetCatalogue
        {
            private readonly PlanetRecord[] _planets;
            public SeGravityCatalogue(params PlanetRecord[] planets) { _planets = planets; }
            public IEnumerable<PlanetRecord> Planets { get { return _planets; } }

            public double GravityAt(Vec3 posKm)
            {
                double strongest = 0;
                foreach (PlanetRecord p in _planets)
                {
                    double r = posKm.DistanceTo(p.Center);
                    if (r >= p.GravityRadius) continue;
                    double hill = p.SurfaceRadius * 1.0413;
                    strongest = Math.Max(strongest, r <= hill ? 1.0 : Math.Pow(hill / r, 7));
                }
                return strongest;
            }

            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private const double MinDryTwr = 1.05;
        private const double SpadiDryMassKg = 16534;

        private static ThrustBand Atmospheric(float forceN)
        {
            return new ThrustBand
            {
                ThrusterType = "Atmospheric", ForceMagnitudeN = forceN,
                MinPlanetaryInfluence = 0.3f, MaxPlanetaryInfluence = 1f,
                EffectivenessAtMinInfluence = 0f, EffectivenessAtMaxInfluence = 1f,
                NeedsAtmosphereForInfluence = true,
            };
        }

        private static ThrustBand Hydrogen(float forceN)
        {
            return new ThrustBand
            {
                ThrusterType = "Hydrogen", ForceMagnitudeN = forceN,
                MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 0f,
                EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f,
                NeedsAtmosphereForInfluence = false,
            };
        }

        private static Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> Bands(Func<float, ThrustBand> kind)
        {
            return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
            {
                { ThrustDirection.Down, new List<ThrustBand> { kind(576000f) } },
                { ThrustDirection.Backward, new List<ThrustBand> { kind(192000f) } },
            };
        }

        private static RouteHullProfile Hull(Func<float, ThrustBand> kind)
        {
            return new RouteHullProfile { ThrustBands = Bands(kind), DryMassKg = SpadiDryMassKg, MinDryTwr = MinDryTwr };
        }

        private static double LiftTwrAt(IPlanetCatalogue planets, PlanetRecord body, double radiusKm)
        {
            Vec3 probe = new Vec3(body.Center.X + radiusKm, body.Center.Y, body.Center.Z);
            return ThrustPhysics.DryTwr(Bands(Atmospheric), SpadiDryMassKg, planets.GravityAt(probe),
                ThrustPhysics.AirDensity(body, radiusKm), radiusKm <= body.AtmosphereRadius, ThrustDirection.Down);
        }

        private static List<Vec3> Polyline(Vec3 from, PlanetRoutePlan plan, Vec3 to)
        {
            var points = new List<Vec3> { from };
            points.AddRange(plan.Waypoints);
            points.Add(to);
            return points;
        }

        // ---- the physics -----------------------------------------------------------------------

        [Fact]
        public void AirDensity_FallsLinearlyFromTheAverageRadiusToTheAtmosphereEdge()
        {
            Assert.Equal(1.0, ThrustPhysics.AirDensity(Tellus, 30.0), 6);
            Assert.Equal(1.0, ThrustPhysics.AirDensity(Tellus, 29.0), 6);   // a valley is still full air
            Assert.Equal(0.249, ThrustPhysics.AirDensity(Tellus, 33.65), 3);
            Assert.Equal(0.0, ThrustPhysics.AirDensity(Tellus, 34.86), 6);
            Assert.Equal(0.0, ThrustPhysics.AirDensity(Tellus, 40.0), 6);

            var airless = new PlanetRecord { SurfaceRadius = 9.5, AtmosphereRadius = 10.07, HasAtmosphere = false };
            Assert.Equal(0.0, ThrustPhysics.AirDensity(airless, 9.5), 6);
        }

        [Fact]
        public void AtmosphericLift_IsZeroWhereTheLiveHullAborted_ThoughGravityWouldPredictPlenty()
        {
            // The live abort read liftTwr=0.00 at g=5.84 m/s^2, 33.65 km from Tellus' centre.
            double g = 5.84 / ThrustPhysics.StandardGravityMs2;
            double density = ThrustPhysics.AirDensity(Tellus, 33.65);

            double fromAir = ThrustPhysics.DryTwr(Bands(Atmospheric), SpadiDryMassKg, g, density, true, ThrustDirection.Down);
            double fromGravity = ThrustPhysics.DryTwr(Bands(Atmospheric), SpadiDryMassKg, g, g, true, ThrustDirection.Down);

            Assert.Equal(0.0, fromAir, 6);
            Assert.True(fromGravity > 2.0, "gravity as the influence reads " + fromGravity);
        }

        // ---- the planner -----------------------------------------------------------------------

        [Fact]
        public void LowArc_ForAnAtmosphericHull_StaysWhereItsLiftHolds()
        {
            var planets = new SeGravityCatalogue(Tellus);
            var from = new Vec3(30.0, 0, 0);
            var to = new Vec3(-30.0, 0.5, 0);

            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(from, to, planets, Hull(Atmospheric),
                PlanetRouteOptions.Defaults, "fleet-1");

            Assert.True(plan.IsClear, plan.FailureReason);
            Assert.Contains("Tellus:band", plan.ProfileSummary);
            Assert.NotEmpty(plan.Waypoints);
            foreach (Vec3 w in plan.Waypoints)
            {
                double r = w.DistanceTo(Tellus.Center);
                Assert.True(r < 33.65, "waypoint at r=" + r.ToString("F3"));
                Assert.True(LiftTwrAt(planets, Tellus, r) >= MinDryTwr,
                    "waypoint at r=" + r.ToString("F3") + " holds TWR " + LiftTwrAt(planets, Tellus, r).ToString("F2"));
            }
        }

        [Fact]
        public void LowArc_ForALoadedAtmosphericHull_SitsLowerAndStillHolds()
        {
            var planets = new SeGravityCatalogue(Tellus);
            var from = new Vec3(30.0, 0, 0);
            var to = new Vec3(-30.0, 0.5, 0);
            RouteHullProfile loaded = Hull(Atmospheric);
            loaded.CargoMassKg = 3196;   // the live [0EB17C]'s ice: 19% on top of the dry hull

            PlanetRoutePlan empty = PlanetRoutePlanner.BuildPlan(from, to, planets, Hull(Atmospheric),
                PlanetRouteOptions.Defaults, "fleet-1");
            PlanetRoutePlan full = PlanetRoutePlanner.BuildPlan(from, to, planets, loaded,
                PlanetRouteOptions.Defaults, "fleet-1");

            Assert.True(full.IsClear, full.FailureReason);
            Assert.True(full.Waypoints[0].DistanceTo(Tellus.Center) < empty.Waypoints[0].DistanceTo(Tellus.Center));
            foreach (Vec3 w in full.Waypoints)
            {
                double r = w.DistanceTo(Tellus.Center);
                Vec3 probe = new Vec3(r, 0, 0);
                double twr = ThrustPhysics.DryTwr(Bands(Atmospheric), SpadiDryMassKg + 3196, planets.GravityAt(probe),
                    ThrustPhysics.AirDensity(Tellus, r), true, ThrustDirection.Down);
                Assert.True(twr >= MinDryTwr, "loaded waypoint at r=" + r.ToString("F3") + " holds TWR " + twr.ToString("F2"));
            }
        }

        [Fact]
        public void LowArc_ForAnAtmosphericHull_KeepsItsChordsAboveTheSurface()
        {
            var planets = new SeGravityCatalogue(Tellus);
            var from = new Vec3(30.0, 0, 0);
            var to = new Vec3(-30.0, 0.5, 0);

            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(from, to, planets, Hull(Atmospheric),
                PlanetRouteOptions.Defaults, "fleet-1");

            List<Vec3> points = Polyline(from, plan, to);
            for (int i = 1; i < points.Count - 2; i++)
                Assert.True(PlanetRoutePlanner.SegmentClearOfShell(points[i], points[i + 1], Tellus, Tellus.SurfaceRadius + 1.5),
                    "chord " + i + " dips below 1.5 km");
        }

        [Fact]
        public void LowArc_ForAHydrogenHull_IsUnchanged()
        {
            var planets = new SeGravityCatalogue(Tellus);

            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(new Vec3(30.0, 0, 0), new Vec3(-30.0, 0.5, 0), planets,
                Hull(Hydrogen), PlanetRouteOptions.Defaults, "fleet-1");

            Assert.True(plan.IsClear, plan.FailureReason);
            Assert.Contains("Tellus:low", plan.ProfileSummary);
            double vertex = 32.0 / Math.Cos(18.0 * Math.PI / 180.0);
            foreach (Vec3 w in plan.Waypoints)
                Assert.Equal(vertex, w.DistanceTo(Tellus.Center), 6);
        }

        [Fact]
        public void AnAtmosphericHull_IsNeverOfferedAShellAboveTheAir()
        {
            var planets = new SeGravityCatalogue(Tellus);

            List<double> atmo = PlanetRoutePlanner.ShellCandidateRadiiKm(Tellus, planets, Hull(Atmospheric),
                PlanetRouteOptions.Defaults);
            List<double> hydrogen = PlanetRoutePlanner.ShellCandidateRadiiKm(Tellus, planets, Hull(Hydrogen),
                PlanetRouteOptions.Defaults);

            Assert.Empty(atmo);
            Assert.NotEmpty(hydrogen);
        }

        [Fact]
        public void AHullThatCannotHoldAnywhereOnTheArc_IsLabelledUnflyable()
        {
            var planets = new SeGravityCatalogue(Tellus);
            RouteHullProfile weak = Hull(Atmospheric);
            weak.DryMassKg = 60000;   // TWR 0.98 at the surface

            PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(new Vec3(30.0, 0, 0), new Vec3(-30.0, 0.5, 0), planets,
                weak, PlanetRouteOptions.Defaults, "fleet-1");

            Assert.Contains("Tellus:unflyable", plan.ProfileSummary);
        }

        // ---- the dispatch gate -----------------------------------------------------------------

        private sealed class OneHullCatalogue : IPrefabCatalogue
        {
            private readonly Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> _bands;
            public OneHullCatalogue(Func<float, ThrustBand> kind) { _bands = Bands(kind); }

            public long CargoLiters(string prefabId) { return 3375; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public double DrillYieldScore(string prefabId) { return 0; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) { return _bands; }
            public double DryMassKg(string prefabId) { return SpadiDryMassKg; }
        }

        private static readonly Vec3 TellusSurface = new Vec3(30.0, 0, 0);
        private static readonly Vec3 TellusFarSide = new Vec3(-30.0, 0.5, 0);
        private static readonly Vec3 AgniSurface = new Vec3(1500 - 14.96, 0, 0);

        [Fact]
        public void Gate_RefusesAnAtmosphericHull_BetweenTwoPlanets()
        {
            var planets = new SeGravityCatalogue(Tellus, Agni);
            ShipPool.RouteEnvironment leg = ShipPool.RouteEnvironment.From(planets, AgniSurface, TellusSurface, MinDryTwr);

            Assert.True(leg.OriginInAtmosphere);
            Assert.True(leg.DestInAtmosphere);
            Assert.True(leg.LeavesAtmosphere);
            Assert.False(ShipPool.IsRouteFeasible(new OneHullCatalogue(Atmospheric), "Spadi-Atmo", leg));
            Assert.True(ShipPool.IsRouteFeasible(new OneHullCatalogue(Hydrogen), "H2", leg));
        }

        [Fact]
        public void Gate_KeepsAnAtmosphericHull_OnALegInsideOneAtmosphere()
        {
            var planets = new SeGravityCatalogue(Tellus, Agni);
            ShipPool.RouteEnvironment leg = ShipPool.RouteEnvironment.From(planets, TellusSurface, TellusFarSide, MinDryTwr);

            Assert.False(leg.LeavesAtmosphere);
            Assert.True(ShipPool.IsRouteFeasible(new OneHullCatalogue(Atmospheric), "Spadi-Atmo", leg));
        }

        [Fact]
        public void Gate_RefusesAnAtmosphericHull_ClimbingToOrbitOfItsOwnPlanet()
        {
            var planets = new SeGravityCatalogue(Tellus, Agni);
            var orbit = new Vec3(45.0, 0, 0);   // inside the well, above the air
            ShipPool.RouteEnvironment leg = ShipPool.RouteEnvironment.From(planets, TellusSurface, orbit, MinDryTwr);

            Assert.True(leg.LeavesAtmosphere);
            Assert.True(leg.ExitGravityG > 0.4);
            Assert.False(ShipPool.IsRouteFeasible(new OneHullCatalogue(Atmospheric), "Spadi-Atmo", leg));
        }

        // ---- every other reader of thrust effectiveness ----------------------------------------

        /// <summary>3.65 km up on Tellus: gravity 0.6 g, air 0.25 — the live abort's altitude.</summary>
        private static Vec3 HighInTheAir(double angleRadians)
        {
            return new Vec3(33.65 * Math.Cos(angleRadians), 33.65 * Math.Sin(angleRadians), 0);
        }

        [Fact]
        public void Gate_ReadsAirAtTheEnds_SoAnAtmosphericHullIsRefusedHighInTheAir()
        {
            var planets = new SeGravityCatalogue(Tellus);
            ShipPool.RouteEnvironment leg = ShipPool.RouteEnvironment.From(planets, HighInTheAir(0), HighInTheAir(0.01), MinDryTwr);

            Assert.False(leg.LeavesAtmosphere);
            Assert.Equal(0.249, leg.OriginAirDensity, 3);
            Assert.False(ShipPool.IsRouteFeasible(new OneHullCatalogue(Atmospheric), "Spadi-Atmo", leg));
            Assert.True(ShipPool.IsRouteFeasible(new OneHullCatalogue(Hydrogen), "H2", leg));
        }

        [Fact]
        public void CargoCap_ReadsAirAtTheEnds()
        {
            var planets = new SeGravityCatalogue(Tellus);
            ShipPool.RouteEnvironment high = ShipPool.RouteEnvironment.From(planets, HighInTheAir(0), HighInTheAir(0.01), MinDryTwr, 0.05);
            ShipPool.RouteEnvironment low = ShipPool.RouteEnvironment.From(planets, TellusSurface, TellusFarSide, MinDryTwr, 0.05);

            Assert.Equal(0.0, high.MaxCargoMassKg(new OneHullCatalogue(Atmospheric), "Spadi-Atmo"), 6);
            Assert.True(low.MaxCargoMassKg(new OneHullCatalogue(Atmospheric), "Spadi-Atmo") > 10000);
        }

        [Fact]
        public void StrandedAudit_CountsAnAtmosphericHullParkedHighInTheAir()
        {
            WorldModel world = TestSupport.World();
            world.Planets = new SeGravityCatalogue(Tellus);
            world.PrefabCatalogue = new OneHullCatalogue(Atmospheric);
            SpatialNode high = world.Graph.Add(HighInTheAir(0), 1.0, "High");
            high.OwnerFactionTag = "F";
            high.ShipStock["Spadi-Atmo"] = 1;
            SpatialNode ground = world.Graph.Add(TellusSurface, 1.0, "Ground");
            ground.OwnerFactionTag = "F";
            ground.ShipStock["Spadi-Atmo"] = 1;

            StrandedShipStockResult r = world.AuditStrandedShipStock();

            Assert.Equal(1, r.StrandedUnits);
            Assert.Contains(r.Details, l => l.Contains("node=High"));
        }

        [Fact]
        public void CombatMobility_ReadsAirWhereTheFightIs()
        {
            WorldModel world = TestSupport.World();
            var planets = new SeGravityCatalogue(Tellus);
            SpatialNode high = world.Graph.Add(HighInTheAir(0), 1.0, "High");
            SpatialNode ground = world.Graph.Add(TellusSurface, 1.0, "Ground");
            var cat = new OneHullCatalogue(Atmospheric);

            double highAir, groundAir; bool highIn, groundIn;
            CombatMobility.EnvironmentAt(planets, high, out highAir, out highIn);
            CombatMobility.EnvironmentAt(planets, ground, out groundAir, out groundIn);

            Assert.Equal(0.249, highAir, 3);
            Assert.Equal(1.0, groundAir, 6);
            Assert.Equal(0.0, CombatMobility.OfHull(cat, "Spadi-Atmo", highAir, highIn), 6);
            Assert.True(CombatMobility.OfHull(cat, "Spadi-Atmo", groundAir, groundIn) > 30);
        }

        [Fact]
        public void LegProfile_CarriesTheAirItFliesThrough_NotTheGravity()
        {
            var planets = new SeGravityCatalogue(Tellus);
            LegImpulseProfile profile = LegImpulseProfile.Build(planets, TestSupport.Tuning(),
                new List<Vec3> { HighInTheAir(0), HighInTheAir(0.02) });

            Assert.True(profile.RepresentativeInAtmosphere);
            Assert.Equal(0.25, profile.RepresentativeAirDensity, 2);
            Assert.True(profile.RepresentativeG > 0.55, "g " + profile.RepresentativeG);
        }

        // ---- a raid is routed for the hulls it flies -------------------------------------------

        private sealed class ArmedAtmoCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 500; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 1000; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new[] { new WeaponBand { Dps = 900, RangeM = 1200 } }; }
            public string GridSize(string prefabId) { return "Large"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) { return Bands(Atmospheric); }
            public double DryMassKg(string prefabId) { return SpadiDryMassKg; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        [Fact]
        public void ARaid_IsRoutedForItsOwnHulls_NotOnTheOrbitShell()
        {
            WorldModel world = TestSupport.World(7);
            world.Planets = new SeGravityCatalogue(Tellus);
            var cat = new ArmedAtmoCatalogue();
            world.PrefabCatalogue = cat;

            SpatialNode origin = world.Graph.Add(new Vec3(30.5, 0, 0), 0.6, "O");
            origin.OwnerFactionTag = "AAA";
            origin.HoldStrength = 0.9;
            origin.ShipStock["Raider"] = 2;
            SpatialNode target = world.Graph.Add(new Vec3(-30.5, 0.5, 0), 0.6, "T");
            target.OwnerFactionTag = "BBB";
            target.HoldStrength = 0.5;

            var actor = new FactionState { Tag = "AAA", RngSeed = 7, MilitaryPosture = 0.75 };
            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "T", TargetFactionTag = "BBB" };

            PendingFleetAction fleet;
            Assert.True(OffensiveDispatch.Dispatch(actor, intent, world, world.Tuning, false, cat, null, null,
                null, null, null, out fleet));

            var waypoints = new List<double>();
            foreach (FleetStop stop in fleet.RemainingStops)
                if (stop.Kind == FleetStopKind.Navigation && stop.HasPosition)
                    waypoints.Add(stop.PositionKm.DistanceTo(Tellus.Center));
            waypoints.Add(fleet.TargetPosKm.DistanceTo(Tellus.Center));

            Assert.NotEmpty(waypoints);
            foreach (double r in waypoints)
                Assert.True(r < 33.65, "raid waypoint at r=" + r.ToString("F2"));
        }
    }
}
