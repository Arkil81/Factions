using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>SE-free planet catalogue for simulation, route planning, and environment matching.</summary>
    public sealed class StaticPlanetCatalogue : IPlanetCatalogue
    {
        private readonly PlanetRecord[] _planets;
        public StaticPlanetCatalogue(IEnumerable<PlanetRecord> planets) { _planets = planets == null ? new PlanetRecord[0] : new List<PlanetRecord>(planets).ToArray(); }
        public IEnumerable<PlanetRecord> Planets { get { return _planets; } }
        public double GravityAt(Vec3 posKm) { double strongest = 0; foreach (PlanetRecord p in _planets) { double r=p.GravityRadius>0?p.GravityRadius:p.SurfaceRadius; double d=p.Center.DistanceTo(posKm); if(r>0&&d<r) strongest=Math.Max(strongest,d<=p.SurfaceRadius?1.0:(r-d)/Math.Max(1e-6,r-p.SurfaceRadius)); } return strongest; }
        public Vec3 NearestSurfacePoint(Vec3 posKm) { PlanetRecord p=Nearest(posKm); return p==null?posKm:Add(p.Center,Scale(Normal(p,posKm),p.SurfaceRadius)); }
        public Vec3 SurfaceNormal(Vec3 posKm) { PlanetRecord p=Nearest(posKm); return p==null?new Vec3(0,1,0):Normal(p,posKm); }
        private PlanetRecord Nearest(Vec3 x) { PlanetRecord b=null; double d=double.MaxValue; foreach(var p in _planets){double n=p.Center.DistanceTo(x)-p.SurfaceRadius;if(n<d){d=n;b=p;}} return b; }
        private static Vec3 Normal(PlanetRecord p, Vec3 x) { Vec3 d=Sub(x,p.Center); double l=Length(d); return l>1e-6?Scale(d,1/l):new Vec3(0,1,0); }
        private static Vec3 Sub(Vec3 a,Vec3 b){return new Vec3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);} private static Vec3 Add(Vec3 a,Vec3 b){return new Vec3(a.X+b.X,a.Y+b.Y,a.Z+b.Z);} private static Vec3 Scale(Vec3 a,double s){return new Vec3(a.X*s,a.Y*s,a.Z*s);} private static double Dot(Vec3 a,Vec3 b){return a.X*b.X+a.Y*b.Y+a.Z*b.Z;} private static double Length(Vec3 a){return Math.Sqrt(Dot(a,a));}
    }

    /// <summary>Planet geometry shared by node placement and route planning, so the orbit shell a
    /// station is snapped into and the shell a fleet arcs along resolve to the same radius.</summary>
    public static class PlanetGeometry
    {
        /// <summary>A planet's own true surface gravity — the denominator for relative zone
        /// classification and the orbit snap-band. Sampled at a synthetic point exactly
        /// <see cref="PlanetRecord.SurfaceRadius"/> from Center along a fixed axis, never at a
        /// terrain-following surface point: gravity is a function of radius, so a mountain and a
        /// valley would otherwise give the same planet two different baselines and leave the snap
        /// non-convergent. 0 when unavailable.</summary>
        public static double LocalSurfaceG(IPlanetCatalogue planets, PlanetRecord planet)
        {
            if (planets == null || planet == null) return 0;
            return planets.GravityAt(AtRadius(planet, BaselineDirection, planet.SurfaceRadius));
        }

        /// <summary>Radius at which relative gravity (g / that planet's own surface g) falls to
        /// <paramref name="orbitGravityMin"/> — the band Orbit nodes are snapped into. Bisects the
        /// real <see cref="IPlanetCatalogue.GravityAt"/> curve along <paramref name="directionUnit"/>
        /// rather than assuming a falloff shape; the direction matters because a moon inside its
        /// parent's well does not read the same gravity on every side. Returns 0 when the planet has
        /// no usable gravity baseline or a malformed radius pair.</summary>
        public static double OrbitBandRadius(IPlanetCatalogue planets, PlanetRecord planet,
                                             double orbitGravityMin, Vec3 directionUnit)
        {
            if (planets == null || planet == null || orbitGravityMin <= 0) return 0;
            double surfaceG = LocalSurfaceG(planets, planet);
            if (surfaceG <= 0) return 0;
            return GravityBoundaryRadius(planets, planet, orbitGravityMin * surfaceG, directionUnit);
        }

        /// <summary>Radius along <paramref name="directionUnit"/> at which
        /// <see cref="IPlanetCatalogue.GravityAt"/> falls to <paramref name="targetG"/>. The raw
        /// bisection behind <see cref="OrbitBandRadius"/>, which solves a target relative to the
        /// body's own surface gravity, and behind the fuel model's FSD dropout boundary
        /// (<see cref="FactionTuning.FsdGravityDropoutG"/>), which solves an absolute one. Bisects
        /// between <see cref="PlanetRecord.SurfaceRadius"/> (gravity at its highest) and
        /// <see cref="PlanetRecord.GravityRadius"/> (gravity zero) rather than assuming a falloff
        /// shape, and returns the smallest tested radius already reading at or below the target, so
        /// the answer never sits inside the boundary. 0 for a null catalogue or planet, a
        /// non-positive target, or a malformed radius pair.
        ///
        /// <para>The unit <paramref name="targetG"/> is read in is whichever the catalogue reports.
        /// The host's <c>SePlanetCatalogue</c> returns SE's natural-gravity multiplier, which is
        /// absolute g (1.0 = 9.81 m/s²); <see cref="StaticPlanetCatalogue"/> returns a normalised
        /// proxy reading 1.0 anywhere at or inside the surface whatever the body's real surface
        /// gravity is, so the two agree only on a 1 g body.</para></summary>
        public static double GravityBoundaryRadius(IPlanetCatalogue planets, PlanetRecord planet,
                                                   double targetG, Vec3 directionUnit)
        {
            if (planets == null || planet == null || targetG <= 0) return 0;

            double loR = planet.SurfaceRadius;   // gravity at its highest here
            double hiR = planet.GravityRadius;   // gravity ~= 0 here
            if (hiR <= loR) return 0;

            double bestR = hiR;
            for (int i = 0; i < OrbitBandIterations; i++)
            {
                double midR = (loR + hiR) * 0.5;
                if (planets.GravityAt(AtRadius(planet, directionUnit, midR)) > targetG) loR = midR;
                else { hiR = midR; bestR = midR; }
            }
            return bestR;
        }

        /// <summary>The nearest planet to <paramref name="posKm"/> by <see cref="PlanetRecord.Center"/>
        /// distance, or null when the catalogue has none. Centre distance with no range check —
        /// correct for "which body's terrain is under me", wrong for anything derived from
        /// <see cref="IPlanetCatalogue.GravityAt"/>; use <see cref="GravityWellPlanet"/> for that.</summary>
        public static PlanetRecord NearestByCentre(IPlanetCatalogue planets, Vec3 posKm)
        {
            if (planets == null) return null;
            PlanetRecord nearest = null;
            double nearestDist = double.MaxValue;
            foreach (PlanetRecord p in planets.Planets)
            {
                if (p == null) continue;
                double dist = posKm.DistanceTo(p.Center);
                if (dist < nearestDist) { nearest = p; nearestDist = dist; }
            }
            return nearest;
        }

        /// <summary>The planet whose gravity well actually contains <paramref name="posKm"/> — the
        /// nearest one whose <see cref="PlanetRecord.GravityRadius"/> the point is inside — falling
        /// back to <see cref="NearestByCentre"/> when the point is outside every well.
        ///
        /// <para>This, not <see cref="NearestByCentre"/>, is the correct reference for anything
        /// derived from <see cref="IPlanetCatalogue.GravityAt"/>, which reports the gravity of the
        /// well the point is in. The two disagree around a moon: a moon orbits inside its parent's
        /// well, so a point out near the moon can be closer to the MOON's center while all the
        /// gravity there is the PARENT's.</para></summary>
        public static PlanetRecord GravityWellPlanet(IPlanetCatalogue planets, Vec3 posKm)
        {
            if (planets == null) return null;
            PlanetRecord nearest = null;
            double nearestDist = double.MaxValue;
            foreach (PlanetRecord p in planets.Planets)
            {
                if (p == null || p.GravityRadius <= 0) continue;
                double dist = posKm.DistanceTo(p.Center);
                if (dist >= p.GravityRadius) continue;
                if (dist < nearestDist) { nearest = p; nearestDist = dist; }
            }
            return nearest ?? NearestByCentre(planets, posKm);
        }

        /// <summary>Unit ray from <paramref name="planet"/>'s centre through <paramref name="posKm"/>,
        /// or +X when the position sits on the centre (no well-defined radial direction).</summary>
        public static Vec3 RadialUnit(PlanetRecord planet, Vec3 posKm)
        {
            if (planet == null) return new Vec3(1, 0, 0);
            double dx = posKm.X - planet.Center.X, dy = posKm.Y - planet.Center.Y, dz = posKm.Z - planet.Center.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-6) return new Vec3(1, 0, 0);
            return new Vec3(dx / len, dy / len, dz / len);
        }

        private const int OrbitBandIterations = 25;

        /// <summary>Fixed axis the surface-gravity baseline is sampled along, so one planet resolves
        /// to one baseline regardless of where the query came from.</summary>
        private static readonly Vec3 BaselineDirection = new Vec3(1, 0, 0);

        private static Vec3 AtRadius(PlanetRecord p, Vec3 dir, double radius)
        {
            return new Vec3(p.Center.X + dir.X * radius, p.Center.Y + dir.Y * radius, p.Center.Z + dir.Z * radius);
        }
    }

    /// <summary>Hull inputs the route planner needs to decide which altitude profile a convoy can
    /// actually fly. <see cref="Unknown"/> means "no thrust data" — such a fleet is only ever routed
    /// along the outer orbit shell, never guessed into a planet's atmosphere.</summary>
    public struct RouteHullProfile
    {
        public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands;
        public double DryMassKg;
        /// <summary>Cargo aboard this hull for the leg. Lift is judged against dry plus cargo: an
        /// atmospheric hull's ceiling falls as its hold fills.</summary>
        public double CargoMassKg;
        public double MinDryTwr;
        /// <summary>The bucket that holds this hull up in the attitude it flies
        /// (<see cref="ShipPool.LiftBandFor"/>). Read through the property so a profile nobody set it
        /// on means the belly-down hull: <c>default(ThrustDirection)</c> is <c>Forward</c>, which is
        /// a bank no hull is held up by.</summary>
        private ThrustDirection? _liftBand;
        public ThrustDirection LiftBand
        {
            get { return _liftBand.HasValue ? _liftBand.Value : ThrustDirection.Down; }
            set { _liftBand = value; }
        }

        public static RouteHullProfile Unknown { get { return new RouteHullProfile(); } }

        /// <summary>True when there is enough scanned data to evaluate lift at an arc altitude.
        /// A prefab the scanner never resolved reports <see cref="IPrefabCatalogue.DryMassKg"/> 0.</summary>
        public bool HasThrustData { get { return ThrustBands != null && DryMassKg > 0; } }
    }

    /// <summary>Route-planner shape controls. Resolved from <see cref="FactionTuning"/> at the call
    /// site so the planner itself stays a pure function of its arguments.</summary>
    public struct PlanetRouteOptions
    {
        /// <summary>Arc altitude above <see cref="PlanetRecord.SurfaceRadius"/> for the low profile.
        /// This is a floor: sampled chords touch it at their midpoints, vertices sit slightly higher.</summary>
        public double LowAltitudeKm;
        /// <summary>Clearance above max(surface, atmosphere) used when a body has no usable gravity
        /// baseline to solve an orbit band from.</summary>
        public double FallbackClearanceKm;
        /// <summary>Arc sampling step. 36° gives ten vertices for a full orbit.</summary>
        public double ArcDegreesPerVertex;
        /// <summary>Hard ceiling on generated waypoints for one plan; exceeding it fails the plan
        /// rather than emitting a colliding route.</summary>
        public int MaxWaypoints;
        /// <summary>Fewest vertices an arc is sampled into, however shallow its sweep. A distant
        /// flyby only sweeps a few degrees between its tangents, which would otherwise emit two
        /// vertices and read as a pair of corners rather than a curve.</summary>
        public int MinArcVertices;
        /// <summary>Relative-gravity ceiling defining the orbit shell — the same value node
        /// placement snaps Orbit positions to.</summary>
        public double OrbitGravityMin;

        public static PlanetRouteOptions Defaults
        {
            get
            {
                return new PlanetRouteOptions
                {
                    LowAltitudeKm = 2.0,
                    FallbackClearanceKm = 10.0,
                    ArcDegreesPerVertex = 36.0,
                    MaxWaypoints = 24,
                    MinArcVertices = 3,
                    OrbitGravityMin = 0.05,
                };
            }
        }

        public static PlanetRouteOptions FromTuning(FactionTuning tuning)
        {
            PlanetRouteOptions o = Defaults;
            if (tuning == null) return o;
            if (tuning.PlanetRouteLowAltitudeKm > 0) o.LowAltitudeKm = tuning.PlanetRouteLowAltitudeKm;
            if (tuning.PlanetRouteClearanceKm > 0) o.FallbackClearanceKm = tuning.PlanetRouteClearanceKm;
            if (tuning.PlanetRouteArcDegreesPerVertex > 0) o.ArcDegreesPerVertex = tuning.PlanetRouteArcDegreesPerVertex;
            if (tuning.PlanetRouteMaxWaypoints > 0) o.MaxWaypoints = tuning.PlanetRouteMaxWaypoints;
            if (tuning.PlanetRouteMinArcVertices > 0) o.MinArcVertices = tuning.PlanetRouteMinArcVertices;
            if (tuning.OrbitGravityMin > 0) o.OrbitGravityMin = tuning.OrbitGravityMin;
            return o;
        }
    }

    public sealed class PlanetRoutePlan
    {
        public readonly List<Vec3> Waypoints = new List<Vec3>();
        public double DistanceKm;
        public bool IsClear;
        public string FailureReason;
        /// <summary>Compact "body:profile" trail of the bypasses this plan generated, for the single
        /// per-route log line. Null when nothing was routed around.</summary>
        public string ProfileSummary;
    }

    /// <summary>Builds deterministic, multi-body clearance arcs. All calculations use km world
    /// coordinates. A blocked leg is replaced by a tangent-in, sampled-arc, tangent-out polyline
    /// around the body; an endpoint already inside the arc shell gets a radial climb (or descent)
    /// vertex instead of a tangent, which is what a launch from a surface node looks like.</summary>
    public static class PlanetRoutePlanner
    {
        public static PlanetRoutePlan BuildPlan(Vec3 origin, Vec3 destination, IPlanetCatalogue planets,
                                                RouteHullProfile hull, PlanetRouteOptions options, string stableKey)
        {
            return BuildPlan(origin, destination, planets, hull, options, stableKey, null);
        }

        /// <summary>The same plan, avoiding named bodies at a caller-chosen radius instead of the one
        /// <see cref="ShellFor"/> would pick.
        ///
        /// <para>This is what makes one route comparable with another: two candidates differ only in
        /// the radius they clear a body at, so the arc machinery, the vertex budget and the
        /// determinism are identical between them. <c>RouteChooser</c> enumerates the radii; the
        /// planner still owns the geometry and still guarantees the result is collision-clear, because
        /// an override that is too low is raised to the collision shell rather than honoured.</para>
        ///
        /// <param name="shellOverrideKm">Avoidance radius per <see cref="BodyKey"/>. Absent bodies keep
        /// <see cref="ShellFor"/>'s pick.</param></summary>
        public static PlanetRoutePlan BuildPlan(Vec3 origin, Vec3 destination, IPlanetCatalogue planets,
                                                RouteHullProfile hull, PlanetRouteOptions options, string stableKey,
                                                IDictionary<string, double> shellOverrideKm)
        {
            var result = new PlanetRoutePlan();
            var bodies = new List<PlanetRecord>();
            // Materialise once: the SE-backed catalogue enumerates every world entity per iteration.
            if (planets != null) bodies.AddRange(planets.Planets);

            var points = new List<Vec3> { origin, destination };
            var shells = new Dictionary<string, ArcShell>();
            var summary = new List<string>();
            int guard = bodies.Count * 4 + 8;

            for (int pass = 0; pass < guard; pass++)
            {
                int segment; PlanetRecord body;
                if (!FirstIntersection(points, bodies, planets, hull, options, shells, shellOverrideKm, out segment, out body))
                {
                    result.IsClear = true;
                    break;
                }

                ArcShell shell = ShellFor(body, planets, hull, options, shells, shellOverrideKm);
                List<Vec3> arc = BuildArc(points[segment], points[segment + 1], body, shell.VertexRadiusKm,
                                          shell.StepRadians, options.MinArcVertices, stableKey);
                if (points.Count - 2 + arc.Count > options.MaxWaypoints)
                {
                    result.FailureReason = "planet route exceeded " + options.MaxWaypoints + " waypoints";
                    return result;
                }
                points.InsertRange(segment + 1, arc);
                summary.Add(BodyLabel(body) + ":" + shell.Profile);
            }

            if (!result.IsClear)
            {
                if (result.FailureReason == null)
                    result.FailureReason = "planet route did not converge after " + guard + " passes";
                return result;
            }

            for (int i = 1; i < points.Count - 1; i++) result.Waypoints.Add(points[i]);
            result.DistanceKm = PolylineDistance(points);
            if (summary.Count > 0) result.ProfileSummary = string.Join(",", summary.ToArray());
            return result;
        }

        /// <summary>Convenience overload for callers holding bare records with no gravity data (the
        /// offline sim and unit fixtures). Every body falls back to the max(surface, atmosphere) +
        /// <paramref name="clearanceKm"/> shell, since no orbit band can be solved without gravity.</summary>
        public static PlanetRoutePlan BuildPlan(Vec3 origin, Vec3 destination, IEnumerable<PlanetRecord> planets,
                                                double clearanceKm, string stableKey)
        {
            PlanetRouteOptions options = PlanetRouteOptions.Defaults;
            options.FallbackClearanceKm = clearanceKm;
            return BuildPlan(origin, destination, new StaticPlanetCatalogue(planets), RouteHullProfile.Unknown,
                             options, stableKey);
        }

        /// <summary>True when the segment stays outside the body's max(surface, atmosphere) +
        /// <paramref name="clearanceKm"/> shell. The safety assertion used by tests; the planner
        /// itself tests against whichever arc shell the hull profile resolved to.</summary>
        public static bool SegmentClear(Vec3 a, Vec3 b, PlanetRecord body, double clearanceKm)
        {
            return SegmentClearOfShell(a, b, body, FallbackRadius(body, clearanceKm));
        }

        /// <summary>True when the segment never dives deeper than <paramref name="shellRadiusKm"/>.
        /// An endpoint already inside the shell (a surface node) relaxes the requirement to its own
        /// radius, so a radial climb out to the shell counts as clear while a chord that cuts further
        /// inward does not — without that, a launch leg would be flagged as blocking itself.</summary>
        public static bool SegmentClearOfShell(Vec3 a, Vec3 b, PlanetRecord body, double shellRadiusKm)
        {
            double t;
            double closestSq = SegmentDistanceSq(a, b, body.Center, out t);
            double allowed = Math.Min(shellRadiusKm, Math.Min(body.Center.DistanceTo(a), body.Center.DistanceTo(b)));
            return closestSq >= allowed * allowed - RadiusEpsilonKm;
        }

        // ---- shells and profile selection -----------------------------------------------------

        private struct ArcShell
        {
            public double FloorRadiusKm;   // lowest point the sampled path is allowed to reach
            public double VertexRadiusKm;  // arc vertices sit here, so chord midpoints land on the floor
            public double StepRadians;     // arc sampling step that puts the chord midpoints on the floor
            public string Profile;         // "low", "band", "orbit", "forced" or "unflyable" — log/diagnostic label only
        }

        /// <summary>Finest arc step a band-limited low pass is sampled at. A hull whose ceiling sits
        /// closer above the low floor than this step allows flies its chords slightly below the
        /// floor instead; 12° keeps a half orbit at 15 vertices, inside the default waypoint budget.</summary>
        private const double MinBandStepDegrees = 12.0;

        /// <summary>Bisection steps for a hull's ceiling inside an atmosphere: 25 halvings of a few
        /// km resolve it to well under a metre.</summary>
        private const int CeilingIterations = 25;

        private static ArcShell ShellAt(double floorRadiusKm, double stepRadians, string profile)
        {
            return new ArcShell
            {
                FloorRadiusKm = floorRadiusKm,
                VertexRadiusKm = floorRadiusKm / Math.Max(0.5, Math.Cos(stepRadians * 0.5)),
                StepRadians = stepRadians,
                Profile = profile,
            };
        }

        private static ArcShell ShellFor(PlanetRecord body, IPlanetCatalogue planets, RouteHullProfile hull,
                                         PlanetRouteOptions options, Dictionary<string, ArcShell> cache,
                                         IDictionary<string, double> shellOverrideKm)
        {
            string key = BodyKey(body);
            ArcShell cached;
            if (cache.TryGetValue(key, out cached)) return cached;

            double step = ArcStepRadians(options);

            double overrideKm;
            if (shellOverrideKm != null && shellOverrideKm.TryGetValue(key, out overrideKm) && overrideKm > 0)
            {
                // Never below the collision floor: a cheap route is still not allowed to fly through
                // the ground, so an override the caller got wrong makes a longer route, never an
                // unflyable one.
                double floor = Math.Max(overrideKm, FallbackRadius(body, options.FallbackClearanceKm));
                ArcShell forced = ShellAt(floor, step, "forced");
                cache[key] = forced;
                return forced;
            }

            // Outer candidate: the same shell WorldModel snaps Orbit nodes into. Sampled along a
            // fixed axis so one body resolves to one shell for the whole plan.
            double orbitFloor = OrbitFloor(body, planets, options);
            ArcShell best = ShellAt(orbitFloor, step, "orbit");

            // Inner candidate: a low pass just above the terrain, offered only to a hull with real
            // scanned thrust data that can still hold itself up down there.
            if (hull.HasThrustData)
            {
                double orbitCost = ProfileCost(planets, body, hull, best);
                ArcShell low;
                double lowCost = LowShell(body, planets, hull, options, orbitFloor, out low)
                    ? ProfileCost(planets, body, hull, low)
                    : double.PositiveInfinity;
                if (lowCost < orbitCost) best = low;
                else if (double.IsPositiveInfinity(orbitCost)) best.Profile = "unflyable";
            }

            cache[key] = best;
            return best;
        }

        private static double OrbitFloor(PlanetRecord body, IPlanetCatalogue planets, PlanetRouteOptions options)
        {
            double collisionFloor = FallbackRadius(body, options.FallbackClearanceKm);
            double orbitFloor = PlanetGeometry.OrbitBandRadius(planets, body, options.OrbitGravityMin, new Vec3(1, 0, 0));
            if (orbitFloor <= 0) orbitFloor = collisionFloor;
            return Math.Max(orbitFloor, FallbackRadius(body, 0));
        }

        /// <summary>The low pass this hull can fly around <paramref name="body"/>, or false when it
        /// has none. The standard pass puts its floor <see cref="PlanetRouteOptions.LowAltitudeKm"/>
        /// above the surface and its vertices a step's inflation above that. A hull that cannot hold
        /// itself up at those vertices — atmospheric lift fading with the air — flies its vertices
        /// at its own ceiling instead, with the step shrunk so the chords between them still clear
        /// the floor.</summary>
        private static bool LowShell(PlanetRecord body, IPlanetCatalogue planets, RouteHullProfile hull,
                                     PlanetRouteOptions options, double orbitFloor, out ArcShell low)
        {
            double nominal = body.SurfaceRadius + Math.Max(0, options.LowAltitudeKm);
            low = ShellAt(nominal, ArcStepRadians(options), "low");
            if (nominal >= orbitFloor) return false;
            if (!double.IsPositiveInfinity(ProfileCost(planets, body, hull, low))) return true;

            double ceiling = Ceiling(planets, body, hull, body.SurfaceRadius, low.VertexRadiusKm);
            if (ceiling <= body.SurfaceRadius) return false;

            double fit = ceiling > nominal ? 2.0 * Math.Acos(nominal / ceiling) : 0;
            double bandStep = Math.Min(low.StepRadians, Math.Max(MinBandStepDegrees * Math.PI / 180.0, fit));
            low = new ArcShell
            {
                VertexRadiusKm = ceiling,
                FloorRadiusKm = ceiling * Math.Cos(bandStep * 0.5),
                StepRadians = bandStep,
                Profile = "band",
            };
            return low.FloorRadiusKm > body.SurfaceRadius
                && !double.IsPositiveInfinity(ProfileCost(planets, body, hull, low));
        }

        /// <summary>Highest radius in [<paramref name="loKm"/>, <paramref name="hiKm"/>] at which the
        /// hull clears its lift gate, assuming it clears it lower down — true of atmospheric lift,
        /// which only weakens with height. <paramref name="loKm"/> or below when it clears nowhere.</summary>
        private static double Ceiling(IPlanetCatalogue planets, PlanetRecord body, RouteHullProfile hull,
                                      double loKm, double hiKm)
        {
            double gate = LiftGate(hull);
            if (LiftTwrAt(planets, body, hull, loKm) < gate) return loKm;
            for (int i = 0; i < CeilingIterations; i++)
            {
                double midKm = (loKm + hiKm) * 0.5;
                if (LiftTwrAt(planets, body, hull, midKm) >= gate) loKm = midKm;
                else hiKm = midKm;
            }
            return loKm;
        }

        /// <summary>The hull's lift TWR, cargo aboard, at <paramref name="radiusKm"/> from the body's
        /// centre: weighed against the gravity there and pushed by thrust at the air density there.</summary>
        private static double LiftTwrAt(IPlanetCatalogue planets, PlanetRecord body, RouteHullProfile hull,
                                        double radiusKm)
        {
            Vec3 probe = new Vec3(body.Center.X + radiusKm, body.Center.Y, body.Center.Z);
            double g = planets == null ? 0 : planets.GravityAt(probe);
            bool inAtmo = body.HasAtmosphere && body.AtmosphereRadius > 0 && radiusKm <= body.AtmosphereRadius;
            return ThrustPhysics.DryTwr(hull.ThrustBands, hull.DryMassKg + Math.Max(0, hull.CargoMassKg), g,
                                        ThrustPhysics.AirDensity(body, radiusKm), inAtmo, hull.LiftBand);
        }

        private static double LiftGate(RouteHullProfile hull) { return hull.MinDryTwr > 0 ? hull.MinDryTwr : 1.0; }

        /// <summary>Stable per-body key for shell caching and overrides.</summary>
        public static string BodyKey(PlanetRecord body)
        {
            if (body == null) return "";
            return body.Id ?? body.Name ?? body.Center.ToString();
        }

        /// <summary>The avoidance radii <paramref name="body"/> can be cleared at, cheapest-geometry
        /// first, for a caller that wants to cost each one as a whole route rather than pick between
        /// them on a proxy.
        ///
        /// <para>Three candidates at most, and only ones the hull can fly: <see cref="ProfileCost"/>
        /// reports infinity for an altitude the hull cannot hold itself up at, and that gate is
        /// absolute. The <b>low</b> pass is offered only when the hull has scanned thrust. The
        /// <b>orbit</b> shell is the band node placement snaps to. The <b>gravity</b> shell is the
        /// body's own <see cref="PlanetRecord.GravityRadius"/> and is the only one that leaves the
        /// well — offered only when it is outside the collision floor, which on a small body it is
        /// not, because there the atmosphere already reaches past the gravity limit. A hull without
        /// scanned thrust is offered the orbit and gravity shells unjudged.</para></summary>
        public static List<double> ShellCandidateRadiiKm(PlanetRecord body, IPlanetCatalogue planets,
                                                         RouteHullProfile hull, PlanetRouteOptions options)
        {
            var radii = new List<double>();
            if (body == null) return radii;

            double step = ArcStepRadians(options);
            double collisionFloor = FallbackRadius(body, options.FallbackClearanceKm);

            double lowFloor = body.SurfaceRadius + Math.Max(0, options.LowAltitudeKm);
            if (hull.HasThrustData && lowFloor >= collisionFloor && Flyable(planets, body, hull, lowFloor, step))
                radii.Add(lowFloor);

            double orbitFloor = PlanetGeometry.OrbitBandRadius(planets, body, options.OrbitGravityMin, new Vec3(1, 0, 0));
            if (orbitFloor <= 0) orbitFloor = collisionFloor;
            orbitFloor = Math.Max(orbitFloor, collisionFloor);
            if (!radii.Contains(orbitFloor) && Flyable(planets, body, hull, orbitFloor, step)) radii.Add(orbitFloor);

            // Clear of the well, not on it. BuildArc inflates vertices so the CHORD MIDPOINTS touch
            // the floor, so a floor of exactly GravityRadius leaves half the arc reading as in-well
            // and the candidate never shows its advantage.
            double gravityFloor = body.GravityRadius + Math.Max(0, options.FallbackClearanceKm);
            if (body.GravityRadius > 0 && gravityFloor > orbitFloor && !radii.Contains(gravityFloor)
                && Flyable(planets, body, hull, gravityFloor, step))
                radii.Add(gravityFloor);

            return radii;
        }

        private static bool Flyable(IPlanetCatalogue planets, PlanetRecord body, RouteHullProfile hull,
                                    double floorRadiusKm, double stepRadians)
        {
            return !hull.HasThrustData
                || !double.IsPositiveInfinity(ProfileCost(planets, body, hull, ShellAt(floorRadiusKm, stepRadians, "")));
        }

        /// <summary>Relative cost of arcing around <paramref name="body"/> on <paramref name="shell"/>,
        /// in "effective km" — circumference at its floor, scaled by how little lift margin the hull
        /// has there. A marginal hull therefore prefers the outer shell even though it is geometrically
        /// longer. <see cref="double.PositiveInfinity"/> means the hull cannot hold itself up
        /// somewhere on the shell: the lift margin is read at both the floor and the vertices, the
        /// lowest and highest points the arc flies, because air-breathing lift is weakest at the top
        /// and everything else at the bottom. This is the seam a fuel-burn term becomes another
        /// addend in.</summary>
        private static double ProfileCost(IPlanetCatalogue planets, PlanetRecord body, RouteHullProfile hull,
                                          ArcShell shell)
        {
            double lengthKm = Math.PI * shell.FloorRadiusKm; // half a circumference — the shape-independent term

            double twr = Math.Min(LiftTwrAt(planets, body, hull, shell.FloorRadiusKm),
                                  LiftTwrAt(planets, body, hull, shell.VertexRadiusKm));

            if (double.IsPositiveInfinity(twr)) return lengthKm; // nothing to fight out here
            double gate = LiftGate(hull);
            if (twr < gate) return double.PositiveInfinity;

            double margin = twr / gate;                                  // 1.0 = exactly at the gate
            double penalty = margin >= 2.0 ? 1.0 : 1.0 + (2.0 - margin); // up to 2x for a marginal hull
            return lengthKm * penalty;
        }

        // ---- arc construction -----------------------------------------------------------------

        /// <summary>Entry, sampled arc, and exit vertices replacing the blocked stretch of a→b around
        /// <paramref name="body"/>. All vertices sit at <paramref name="vertexRadiusKm"/>.</summary>
        private static List<Vec3> BuildArc(Vec3 a, Vec3 b, PlanetRecord body, double vertexRadiusKm,
                                           double stepRadians, int minVertices, string stableKey)
        {
            var arc = new List<Vec3>();
            Vec3 c = body.Center;
            double dA = c.DistanceTo(a), dB = c.DistanceTo(b);

            Vec3 uA = dA > 1e-6 ? Unit(Sub(a, c)) : DeterministicRadial(a, b, body, stableKey);
            Vec3 uB = dB > 1e-6 ? Unit(Sub(b, c)) : DeterministicRadial(b, a, body, stableKey);

            Vec3 normal = Cross(uA, uB);
            if (Length(normal) < 1e-6)
            {
                // Head-on or collinear with the centre: no plane is implied by the geometry, so pick
                // one deterministically from the leg direction and a stable per-fleet/body hash.
                Vec3 d = Unit(Sub(b, a));
                Vec3 axis = Math.Abs(d.Y) < 0.9 ? Cross(d, new Vec3(0, 1, 0)) : Cross(d, new Vec3(1, 0, 0));
                if ((StableHash((stableKey ?? "") + ":" + (body.Id ?? "")) & 1) != 0) axis = Scale(axis, -1);
                normal = Cross(uA, Unit(axis));
                if (Length(normal) < 1e-6) normal = Unit(axis);
            }
            normal = Unit(normal);

            // An endpoint already inside the shell has no tangent — it climbs (or descends) radially.
            Vec3 entryDir = dA > vertexRadiusKm
                ? Rotate(uA, normal, Math.Acos(Clamp(vertexRadiusKm / dA, -1, 1)))
                : uA;
            Vec3 exitDir = dB > vertexRadiusKm
                ? Rotate(uB, normal, -Math.Acos(Clamp(vertexRadiusKm / dB, -1, 1)))
                : uB;

            arc.Add(Add(c, Scale(entryDir, vertexRadiusKm)));

            double sweep = SignedAngle(entryDir, exitDir, normal);
            if (sweep > 1e-6)
            {
                int intermediate = (int)Math.Ceiling(sweep / Math.Max(1e-6, stepRadians)) - 1;
                if (intermediate < minVertices - 2) intermediate = minVertices - 2;
                if (intermediate < 0) intermediate = 0;
                for (int i = 1; i <= intermediate; i++)
                {
                    double angle = sweep * i / (intermediate + 1);
                    arc.Add(Add(c, Scale(Rotate(entryDir, normal, angle), vertexRadiusKm)));
                }
            }

            arc.Add(Add(c, Scale(exitDir, vertexRadiusKm)));
            return arc;
        }

        /// <summary>Stable radial direction for the degenerate case of an endpoint sitting on a body's
        /// exact centre — derived from the leg and a per-fleet/body hash, never from ambient state.</summary>
        private static Vec3 DeterministicRadial(Vec3 at, Vec3 other, PlanetRecord body, string stableKey)
        {
            Vec3 d = Sub(other, at);
            if (Length(d) < 1e-6) d = new Vec3(1, 0, 0);
            d = Unit(d);
            Vec3 axis = Math.Abs(d.Y) < 0.9 ? Cross(d, new Vec3(0, 1, 0)) : Cross(d, new Vec3(1, 0, 0));
            if ((StableHash((stableKey ?? "") + ":" + (body.Id ?? "")) & 1) != 0) axis = Scale(axis, -1);
            return Unit(axis);
        }

        // ---- scanning -------------------------------------------------------------------------

        /// <summary>Earliest blocker along the polyline, ordered by (segment index, closest-approach
        /// parameter). Ranking on the parameter alone compares positions in different segments'
        /// local frames, which is wrong the moment the polyline has more than one segment.</summary>
        private static bool FirstIntersection(List<Vec3> points, List<PlanetRecord> bodies, IPlanetCatalogue planets,
                                              RouteHullProfile hull, PlanetRouteOptions options,
                                              Dictionary<string, ArcShell> shells, IDictionary<string, double> shellOverrideKm,
                                              out int segment, out PlanetRecord body)
        {
            segment = -1; body = null;
            double earliest = double.MaxValue;
            for (int i = 0; i < points.Count - 1; i++)
            {
                foreach (PlanetRecord p in bodies)
                {
                    // Cheap superset test first. Every arc shell sits inside this bound, and resolving
                    // the real one bisects the live gravity curve — 25 engine samples per body that a
                    // route passing nowhere near it should not pay.
                    double bound = Math.Max(p.GravityRadius, FallbackRadius(p, options.FallbackClearanceKm));
                    if (SegmentClearOfShell(points[i], points[i + 1], p, bound)) continue;

                    double r = ShellFor(p, planets, hull, options, shells, shellOverrideKm).FloorRadiusKm;
                    if (SegmentClearOfShell(points[i], points[i + 1], p, r)) continue;
                    double t;
                    SegmentDistanceSq(points[i], points[i + 1], p.Center, out t);
                    if (body != null && t >= earliest) continue;
                    segment = i; earliest = t; body = p;
                }
                if (body != null) break; // segments are scanned in order — nothing later can be earlier
            }
            return body != null;
        }

        // ---- helpers --------------------------------------------------------------------------

        /// <summary>Slack on shell tests, in km². Absorbs the floating-point error of a tangent
        /// point that lands exactly on the shell by construction.</summary>
        private const double RadiusEpsilonKm = 1e-6;

        private static string BodyLabel(PlanetRecord p) { return p.Name ?? p.Id ?? "body"; }
        private static double ArcStepRadians(PlanetRouteOptions o) { return Math.Max(1e-3, o.ArcDegreesPerVertex) * Math.PI / 180.0; }
        private static double FallbackRadius(PlanetRecord p, double clearance) { return Math.Max(p.SurfaceRadius, p.AtmosphereRadius) + Math.Max(0, clearance); }
        private static int StableHash(string value) { unchecked { int h = 17; foreach (char c in value) h = h * 31 + c; return h; } }
        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static double PolylineDistance(List<Vec3> p) { double d = 0; for (int i = 0; i < p.Count - 1; i++) d += p[i].DistanceTo(p[i + 1]); return d; }

        /// <summary>Angle from <paramref name="from"/> to <paramref name="to"/> measured positively
        /// about <paramref name="axis"/>, in [0, 2π).</summary>
        private static double SignedAngle(Vec3 from, Vec3 to, Vec3 axis)
        {
            double angle = Math.Atan2(Dot(Cross(from, to), axis), Dot(from, to));
            return angle < 0 ? angle + Math.PI * 2 : angle;
        }

        /// <summary>Rodrigues rotation of <paramref name="v"/> about unit <paramref name="axis"/>.</summary>
        private static Vec3 Rotate(Vec3 v, Vec3 axis, double radians)
        {
            double c = Math.Cos(radians), s = Math.Sin(radians);
            Vec3 term1 = Scale(v, c);
            Vec3 term2 = Scale(Cross(axis, v), s);
            Vec3 term3 = Scale(axis, Dot(axis, v) * (1 - c));
            return Add(Add(term1, term2), term3);
        }

        private static double SegmentDistanceSq(Vec3 a, Vec3 b, Vec3 p, out double t) { Vec3 d = Sub(b, a); double q = Dot(d, d); t = q < 1e-9 ? 0 : Math.Max(0, Math.Min(1, Dot(Sub(p, a), d) / q)); Vec3 n = Sub(p, Add(a, Scale(d, t))); return Dot(n, n); }
        private static Vec3 Unit(Vec3 a) { double l = Length(a); return l > 1e-9 ? Scale(a, 1 / l) : new Vec3(1, 0, 0); }
        private static double Length(Vec3 a) { return Math.Sqrt(Dot(a, a)); }
        private static Vec3 Sub(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        private static Vec3 Add(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        private static Vec3 Scale(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        private static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        private static Vec3 Cross(Vec3 a, Vec3 b) { return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
    }
}
