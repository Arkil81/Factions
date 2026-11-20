using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>One route under consideration, with what it costs.</summary>
    public struct RouteCandidate
    {
        /// <summary>The plan. Always collision-clear; a plan that is not is never a candidate.</summary>
        public PlanetRoutePlan Plan;
        /// <summary>Origin, waypoints and destination — what the cost was measured over.</summary>
        public List<Vec3> PolylineKm;
        /// <summary>Transit time in minutes, across the tiers the route actually crosses.</summary>
        public double Minutes;
        /// <summary>Credits the burn would cost at import price. 0 when the fuel term is switched
        /// off, which is its default — see <see cref="FactionTuning.RouteFuelWeightPerMinute"/>.</summary>
        public double FuelCredits;
        /// <summary>What the candidate is ranked on.</summary>
        public double Cost;
        /// <summary>Short label for the one route line: "direct", or "body:radiusKm".</summary>
        public string Label;
    }

    /// <summary>Picks a fleet's route by what it costs rather than by whether it collides.
    ///
    /// <para><b>Why this is not the planner's job.</b> <see cref="PlanetRoutePlanner"/> answers one
    /// question — is this line clear — and answers it without a world. Choosing between two clear
    /// routes needs the transit model, the fuel model and the prices, none of which belong inside a
    /// geometry routine. So the planner generates candidates by clearing a body at a caller-chosen
    /// radius, and this ranks them.</para>
    ///
    /// <para><b>What makes a longer route cheaper.</b> A leg inside a gravity well is capped at the
    /// world's grid limit, against open-space cruise outside it — a 16.7x penalty per kilometre on
    /// this world. Going around a body rather than through it is at most ~1.6x the distance, so the
    /// detour pays whenever it removes a meaningful share of the in-well path. The same detour also
    /// removes the hover burn, which is charged per second inside a well and is the great majority of
    /// what this world's fleets consume, so the two terms rarely disagree.</para></summary>
    public static class RouteChooser
    {
        /// <summary>Candidate routes scored for one leg. One is the direct plan; the rest clear the
        /// body that costs the most at each radius it can be cleared at.</summary>
        public const int MaxCandidates = 4;

        /// <summary>The cheapest route for this leg, and what it was chosen over.
        /// <paramref name="considered"/> is the number of candidates costed, so a caller can tell a
        /// route that won a contest from one that never had one.</summary>
        public static PlanetRoutePlan Choose(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                             RouteHullProfile hull, Vec3 fromKm, Vec3 toKm, string stableKey,
                                             out RouteCandidate chosen, out int considered)
        {
            chosen = new RouteCandidate();
            considered = 0;

            PlanetRouteOptions options = PlanetRouteOptions.FromTuning(tuning);
            PlanetRoutePlan direct = PlanetRoutePlanner.BuildPlan(fromKm, toKm, world.Planets, hull,
                                                                 options, stableKey, null);
            if (!direct.IsClear) return direct;

            RouteCandidate best = Score(world, tuning, fleet, direct, fromKm, toKm, "direct");
            considered = 1;
            if (best.Cost <= 0) { chosen = best; return direct; }

            PlanetRecord costly = CostliestWell(world.Planets, best.PolylineKm, tuning);
            if (costly == null) { chosen = best; return direct; }

            List<double> radii = PlanetRoutePlanner.ShellCandidateRadiiKm(costly, world.Planets, hull, options);
            string key = PlanetRoutePlanner.BodyKey(costly);
            var overrides = new Dictionary<string, double>(StringComparer.Ordinal);

            for (int i = 0; i < radii.Count && considered < MaxCandidates; i++)
            {
                overrides[key] = radii[i];
                PlanetRoutePlan plan = PlanetRoutePlanner.BuildPlan(fromKm, toKm, world.Planets, hull,
                                                                   options, stableKey, overrides);
                if (!plan.IsClear) continue;

                considered++;
                RouteCandidate candidate = Score(world, tuning, fleet, plan, fromKm, toKm,
                    BodyLabel(costly) + ":" + radii[i].ToString("0", CultureInfo.InvariantCulture) + "km");
                if (candidate.Cost > 0 && candidate.Cost < best.Cost) best = candidate;
            }

            chosen = best;
            return best.Plan;
        }

        /// <summary>The body whose gravity well the route spends the most length inside. Null when the
        /// route never enters one, which is the common case and the cheap exit.</summary>
        private static PlanetRecord CostliestWell(IPlanetCatalogue planets, IList<Vec3> polylineKm,
                                                  FactionTuning tuning)
        {
            if (planets == null || polylineKm == null || polylineKm.Count < 2) return null;

            PlanetRecord worst = null;
            double worstKm = 0;
            foreach (PlanetRecord body in planets.Planets)
            {
                if (body == null || body.GravityRadius <= 0) continue;
                // Sphere test before sampling: most legs come nowhere near most bodies, and this
                // runs per dispatch at roughly 1.6 a second.
                if (!ApproachesSphere(polylineKm, body.Center, body.GravityRadius)) continue;

                double insideKm = LengthInsideSphereKm(polylineKm, body.Center, body.GravityRadius);
                if (insideKm > worstKm) { worstKm = insideKm; worst = body; }
            }
            return worst;
        }

        /// <summary>Whether any segment comes within <paramref name="radiusKm"/> of the centre — a
        /// handful of arithmetic per body against a sampled walk of the whole polyline.</summary>
        private static bool ApproachesSphere(IList<Vec3> polylineKm, Vec3 centre, double radiusKm)
        {
            double radiusSq = radiusKm * radiusKm;
            for (int i = 0; i < polylineKm.Count - 1; i++)
            {
                Vec3 a = polylineKm[i], b = polylineKm[i + 1];
                double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
                double lengthSq = abx * abx + aby * aby + abz * abz;
                double acx = centre.X - a.X, acy = centre.Y - a.Y, acz = centre.Z - a.Z;

                double dx, dy, dz;
                if (lengthSq <= 1e-12) { dx = acx; dy = acy; dz = acz; }
                else
                {
                    double t = (acx * abx + acy * aby + acz * abz) / lengthSq;
                    if (t < 0) t = 0; else if (t > 1) t = 1;
                    dx = acx - abx * t; dy = acy - aby * t; dz = acz - abz * t;
                }
                if (dx * dx + dy * dy + dz * dz < radiusSq) return true;
            }
            return false;
        }

        /// <summary>Kilometres of <paramref name="polylineKm"/> inside the sphere, by sampling at the
        /// same step the tier walk uses so the two agree on where a boundary falls.</summary>
        private static double LengthInsideSphereKm(IList<Vec3> polylineKm, Vec3 centre, double radiusKm)
        {
            double inside = 0;
            double radiusSq = radiusKm * radiusKm;
            for (int segment = 0; segment < polylineKm.Count - 1; segment++)
            {
                Vec3 from = polylineKm[segment], to = polylineKm[segment + 1];
                double segmentKm = from.DistanceTo(to);
                if (segmentKm <= 0) continue;

                int steps = (int)Math.Ceiling(segmentKm / LegZoneProfile.SampleStepKm);
                if (steps < 1) steps = 1;
                if (steps > LegZoneProfile.MaxSamplesPerSegment) steps = LegZoneProfile.MaxSamplesPerSegment;
                double stepKm = segmentKm / steps;

                for (int step = 0; step < steps; step++)
                {
                    double t = (step + 0.5) / steps;
                    double dx = from.X + (to.X - from.X) * t - centre.X;
                    double dy = from.Y + (to.Y - from.Y) * t - centre.Y;
                    double dz = from.Z + (to.Z - from.Z) * t - centre.Z;
                    if (dx * dx + dy * dy + dz * dz < radiusSq) inside += stepKm;
                }
            }
            return inside;
        }

        private static RouteCandidate Score(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                            PlanetRoutePlan plan, Vec3 fromKm, Vec3 toKm, string label)
        {
            var polyline = new List<Vec3>(plan.Waypoints.Count + 2);
            polyline.Add(fromKm);
            for (int i = 0; i < plan.Waypoints.Count; i++) polyline.Add(plan.Waypoints[i]);
            polyline.Add(toKm);

            var candidate = new RouteCandidate
            {
                Plan = plan,
                PolylineKm = polyline,
                Label = label,
                Minutes = tuning.TransitTimeMins(world.ZonesFor(polyline)),
            };

            // The fuel term is skipped, not multiplied by zero: pricing a convoy walks its hulls and
            // its manifest, and the default weight asks for none of it.
            double weight = tuning.RouteFuelWeightPerMinute;
            if (weight > 0)
            {
                candidate.FuelCredits = FuelCreditsFor(world, tuning, fleet, polyline);
                candidate.Cost = candidate.Minutes + weight * candidate.FuelCredits;
            }
            else candidate.Cost = candidate.Minutes;

            return candidate;
        }

        /// <summary>What one route's burn would cost at import price — the same price the treasury
        /// pays when a node cannot supply a departure, so the two cannot drift apart.</summary>
        public static double FuelCreditsFor(WorldModel world, FactionTuning tuning, PendingFleetAction fleet,
                                            IList<Vec3> polylineKm)
        {
            LegBurn burn = FuelMeter.BurnForPolyline(world, tuning, fleet, polylineKm);
            if (burn == null) return 0;

            double credits = 0;
            foreach (KeyValuePair<string, double> gas in burn.GasLitres)
                credits += ItemCredits.For(gas.Key, gas.Value,
                                           FuelDraw.ImportUnitPrice(world, tuning, gas.Key));
            if (burn.ReactorFuelKg > 0 && burn.ReactorFuelKey != null)
                credits += ItemCredits.For(burn.ReactorFuelKey, burn.ReactorFuelKg,
                                           FuelDraw.ImportUnitPrice(world, tuning, burn.ReactorFuelKey));
            return credits;
        }

        private static string BodyLabel(PlanetRecord body)
        {
            return body.Name ?? body.Id ?? "body";
        }
    }
}
