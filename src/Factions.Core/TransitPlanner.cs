using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>What a carrier does about a route through hostile space, decided on cost.
    ///
    /// <para><b>Four answers, one currency.</b> Every option is priced in credits: accepting the route
    /// costs its expected loss (<see cref="TransitRisk.RiskCredits"/>); a detour costs its extra fuel plus
    /// whatever risk it still carries; an escort costs its own fuel plus the risk left once its guns
    /// count in the convoy verdict; re-picking a source costs the extra kilometres to the other source at
    /// the fuel rate of the hull that flew this lane (<see cref="TransitLedger"/>). The cheapest is taken.
    /// Fuel is valued as the convoy gate values it (<see cref="HaulHullCost.CreditsOf"/>). Time is not
    /// priced: a hull's minutes have no credit value in this model, so a detour is bounded by
    /// <see cref="FactionTuning.TransitDetourMaxStretch"/> instead.</para>
    ///
    /// <para><b>Where each is decided.</b> Accept or detour, per leg, when the leg is planned
    /// (<see cref="PlanLeg"/>, from <c>PendingFleetResolver.PlanRoute</c>), so a later leg is judged on the
    /// world when it is flown. An escort at dispatch (<see cref="ConsiderEscort"/>), the one moment an
    /// idle armed hull can join. A re-pick at the next match, from what this dispatch recorded.</para>
    ///
    /// <para><b>Unpriced fuel cannot be weighed.</b> With no fuel price the route is accepted as
    /// planned and nothing is escorted: a cost that cannot be set against distance does not move a
    /// choice, as the convoy gate refuses nothing on absent data.</para></summary>
    public static class TransitPlanner
    {
        public enum Pick { None = 0, Accept = 1, Detour = 2, Escort = 3 }

        /// <summary>One leg's decision.</summary>
        public sealed class LegPlan
        {
            public Pick Pick;
            /// <summary>The route to fly: the one handed in, or the detour.</summary>
            public PlanetRoutePlan Route;
            /// <summary>Hostile stays on the route as first planned.</summary>
            public List<TransitRisk.Crossing> Crossings = new List<TransitRisk.Crossing>();
            /// <summary>Expected loss flying the route as planned.</summary>
            public double RiskCredits;
            /// <summary>The cheapest detour's extra fuel plus its residual risk; negative when no
            /// detour was found.</summary>
            public double DetourCredits = -1;
            public string DetourLabel;
            /// <summary>What the pick costs.</summary>
            public double Cost;
            /// <summary>Fuel for the route as planned, at its value where the hull departs.</summary>
            public double FuelCredits;
            public double RouteKm;
        }

        /// <summary>Detour clearances tried around the costliest node, in its influence radii. At four
        /// radii a node's influence is cut off entirely.</summary>
        private static readonly double[] DetourRadii = { 2.0, 4.0 };

        /// <summary>Accept or detour, for one leg about to be flown along <paramref name="route"/>.
        /// <see cref="Pick.None"/> when the route carries no expected loss.</summary>
        public static LegPlan PlanLeg(WorldModel world, FactionTuning t, PendingFleetAction fleet,
                                      RouteHullProfile hull, Vec3 fromKm, Vec3 toKm, PlanetRoutePlan route,
                                      string stableKey)
        {
            var plan = new LegPlan { Pick = Pick.None, Route = route };
            if (!TransitRisk.Applies(world) || t == null || route == null || !route.IsClear) return plan;
            if (FleetTrespass.CatchOf(fleet) != FleetTrespass.CatchKind.Convoy) return plan;

            List<Vec3> polyline = Polyline(fromKm, route.Waypoints, toKm);
            plan.RouteKm = FuelMeter.PolylineLengthKm(polyline);
            double minutes = t.TransitTimeMins(world.ZonesFor(polyline));
            plan.Crossings = TransitRisk.Assess(world, t, fleet, polyline, minutes);
            if (plan.Crossings.Count == 0) return plan;

            double cargo = fleet.CargoValue;
            double hulls = TransitRisk.HullCredits(fleet, world.PrefabCatalogue);
            double dps = TransitRisk.ConvoyDps(fleet, world.PrefabCatalogue);
            plan.RiskCredits = TransitRisk.RiskCredits(plan.Crossings, cargo, hulls, dps, t);
            if (plan.RiskCredits <= 0) return plan;

            plan.Pick = Pick.Accept;
            plan.Cost = plan.RiskCredits;
            plan.FuelCredits = FuelValue(world, t, fleet, polyline);
            if (plan.FuelCredits <= 0) return plan;

            int worst = Costliest(plan.Crossings, cargo, hulls, dps, t);
            SpatialNode centre;
            if (worst < 0 || plan.Crossings[worst].NodeId == null
                || !world.Graph.Nodes.TryGetValue(plan.Crossings[worst].NodeId, out centre))
                return plan;

            PlanetRouteOptions options = PlanetRouteOptions.FromTuning(t);
            Vec3 away = AwayFrom(polyline, centre.Position);
            double radius = InfluenceRadius.For(centre, t);
            for (int k = 0; k < DetourRadii.Length; k++)
                for (int side = 1; side >= -1; side -= 2)
                {
                    double off = side * DetourRadii[k] * radius;
                    var via = new Vec3(centre.Position.X + away.X * off, centre.Position.Y + away.Y * off,
                                       centre.Position.Z + away.Z * off);
                    PlanetRoutePlan detour = Via(world, hull, options, fromKm, via, toKm, stableKey);
                    if (detour == null) continue;
                    List<Vec3> detourLine = Polyline(fromKm, detour.Waypoints, toKm);
                    double detourKm = FuelMeter.PolylineLengthKm(detourLine);
                    if (detourKm > Math.Max(1.0, t.TransitDetourMaxStretch) * plan.RouteKm) continue;
                    double detourFuel = FuelValue(world, t, fleet, detourLine);
                    if (detourFuel <= 0) continue;
                    List<TransitRisk.Crossing> residual = TransitRisk.Assess(world, t, fleet, detourLine,
                        t.TransitTimeMins(world.ZonesFor(detourLine)));
                    double cost = Math.Max(0, detourFuel - plan.FuelCredits)
                                  + TransitRisk.RiskCredits(residual, cargo, hulls, dps, t);
                    if (plan.DetourCredits < 0 || cost < plan.DetourCredits)
                    {
                        plan.DetourCredits = cost;
                        plan.DetourLabel = DetourRadii[k].ToString("0", CultureInfo.InvariantCulture) + "r"
                                           + (side > 0 ? "+" : "-");
                        if (cost < plan.Cost)
                        {
                            plan.Pick = Pick.Detour;
                            plan.Cost = cost;
                            plan.Route = detour;
                        }
                    }
                }
            return plan;
        }

        /// <summary>Whether an idle armed hull at the departure node should fly with this convoy, decided
        /// by setting the itinerary's cheapest cost without one against its fuel plus the cost left with
        /// one. Applied to <paramref name="haul"/> when cheaper; the node keeps at least one armed hull.
        /// Records the lane's cost for the next match either way, and logs one <c>TRANSIT_DISPATCH</c>
        /// line when the itinerary carries any cost.</summary>
        public static Pick ConsiderEscort(ref PendingFleetAction haul, IOperationsExecutionContext ctx,
                                          WorldModel world, FactionTuning t, string sourceNodeId,
                                          string deliverNodeId)
        {
            if (!TransitRisk.Applies(world) || ctx == null || t == null) return Pick.None;
            if (FleetTrespass.CatchOf(haul) != FleetTrespass.CatchKind.Convoy) return Pick.None;

            List<Leg> legs = Legs(haul, world);
            // Most dispatches cross no hostile space: settle those on the straight legs before any
            // route is planned. Each leg is still priced on its planned route when it is flown.
            if (!AnyCaughtStraight(world, t, haul, legs))
            {
                world.TransitLedger.Record(haul.ActorTag, sourceNodeId, deliverNodeId, 0, 0, ctx.UtcNow);
                return Pick.None;
            }
            double fuelPerKm;
            LegPlan worstLeg;
            double bare = ItineraryCost(world, t, haul, legs, out fuelPerKm, out worstLeg);
            if (bare <= 0 || worstLeg == null)
            {
                world.TransitLedger.Record(haul.ActorTag, sourceNodeId, deliverNodeId, 0, 0, ctx.UtcNow);
                return Pick.None;
            }

            string escortId = null;
            double escortFuel = 0, best = bare;
            string origin = haul.OriginNodeId;
            IPrefabCatalogue cat = world.PrefabCatalogue;
            if (origin != null && cat != null && fuelPerKm > 0)
            {
                foreach (string candidate in ArmedSpares(ctx, origin, cat))
                {
                    PendingFleetAction escorted = WithEscort(haul, candidate, cat);
                    double candidateFuel = EscortFuel(world, t, haul, candidate, legs);
                    if (candidateFuel <= 0) continue;
                    double ignored;
                    LegPlan ignoredLeg;
                    double total = candidateFuel + ItineraryCost(world, t, escorted, legs, out ignored, out ignoredLeg);
                    if (total < best) { best = total; escortId = candidate; escortFuel = candidateFuel; }
                }
            }

            Pick pick = Pick.Accept;
            if (escortId != null && ctx.ConsumeShipPrefab(origin, escortId, 1) == 1)
            {
                haul = WithEscort(haul, escortId, cat);
                pick = Pick.Escort;
                RecordDenial(world, haul.ActorTag, worstLeg.Crossings, escortFuel, haul, t, ctx.UtcNow);
            }
            else best = bare;

            world.TransitLedger.Record(haul.ActorTag, sourceNodeId, deliverNodeId, best, fuelPerKm, ctx.UtcNow);

            TransitRisk.Crossing c = worstLeg.Crossings[Costliest(worstLeg.Crossings, haul.CargoValue,
                TransitRisk.HullCredits(haul, cat), TransitRisk.ConvoyDps(haul, cat), t)];
            CultureInfo inv = CultureInfo.InvariantCulture;
            ctx.AddLog("TRANSIT_DISPATCH " + haul.ActorTag
                       + " fleet=" + haul.LogId
                       + " kind=" + haul.Kind
                       + " from=" + (sourceNodeId ?? "-")
                       + " to=" + (deliverNodeId ?? "-")
                       + " enemy=" + c.OwnerTag
                       + " node=" + (c.NodeId ?? "-")
                       + " region=" + (c.RegionKey ?? "-")
                       + " bare=" + bare.ToString("0", inv)
                       + " pick=" + (pick == Pick.Escort ? "escort" : "route")
                       + (pick == Pick.Escort
                           ? " escort=" + escortId + " escortFuel=" + escortFuel.ToString("0", inv)
                           : "")
                       + " cost=" + best.ToString("0", inv)
                       + " fuelPerKm=" + fuelPerKm.ToString("0.##", inv));
            return pick;
        }

        /// <summary>Adds what this leg's pick cost to each caught enemy region the route crossed, shared
        /// by each one's part of the expected loss, and returns the largest region's weight after.</summary>
        public static double RecordDenial(WorldModel world, string actorTag, IList<TransitRisk.Crossing> crossings,
                                          double credits, PendingFleetAction fleet, FactionTuning t, DateTime nowUtc)
        {
            if (world == null || crossings == null || credits <= 0) return 0;
            FactionState actor = TransitRisk.StateOf(world.TransitStates, actorTag);
            if (actor == null) return 0;
            IPrefabCatalogue cat = world.PrefabCatalogue;
            double cargo = fleet.CargoValue, hulls = TransitRisk.HullCredits(fleet, cat), dps = TransitRisk.ConvoyDps(fleet, cat);
            double total = 0;
            var shares = new double[crossings.Count];
            for (int i = 0; i < crossings.Count; i++)
            {
                shares[i] = TransitRisk.LossCredits(crossings[i], cargo, hulls, dps, t);
                total += shares[i];
            }
            if (total <= 0)
            {
                // Escorted, so nothing is expected lost any more: share by the bare convoy's loss.
                for (int i = 0; i < crossings.Count; i++)
                {
                    shares[i] = TransitRisk.LossCredits(crossings[i], cargo, hulls, 0, t);
                    total += shares[i];
                }
            }
            if (total <= 0) return 0;
            double heaviest = 0;
            for (int i = 0; i < crossings.Count; i++)
            {
                if (shares[i] <= 0 || crossings[i].RegionKey == null) continue;
                double w = actor.TransitDenial.Add(crossings[i].OwnerTag, crossings[i].RegionKey,
                                                   credits * shares[i] / total, nowUtc);
                if (w > heaviest) heaviest = w;
            }
            return heaviest;
        }

        /// <summary>The <c>TRANSIT_LEG</c> line for one priced leg.</summary>
        public static string FormatLeg(PendingFleetAction fleet, LegPlan plan, double laneWeight, FactionTuning t,
                                       IPrefabCatalogue cat)
        {
            TransitRisk.Crossing c = plan.Crossings[Costliest(plan.Crossings, fleet.CargoValue,
                TransitRisk.HullCredits(fleet, cat), TransitRisk.ConvoyDps(fleet, cat), t)];
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "TRANSIT_LEG " + fleet.ActorTag
                   + " fleet=" + fleet.LogId
                   + " kind=" + fleet.Kind
                   + " to=" + (fleet.TargetNodeId ?? "[nav]")
                   + " enemy=" + c.OwnerTag
                   + " node=" + (c.NodeId ?? "-")
                   + " region=" + (c.RegionKey ?? "-")
                   + " stayS=" + c.Seconds.ToString("0", inv)
                   + " shadowS=" + c.ShadowSeconds.ToString("0", inv)
                   + " catcherDps=" + c.CatcherDps.ToString("0", inv)
                   + " risk=" + plan.RiskCredits.ToString("0", inv)
                   + " detour=" + (plan.DetourCredits >= 0 ? plan.DetourCredits.ToString("0", inv) : "-")
                   + " pick=" + (plan.Pick == Pick.Detour ? "detour:" + plan.DetourLabel : "accept")
                   + " cost=" + plan.Cost.ToString("0", inv)
                   + " laneW=" + laneWeight.ToString("0", inv);
        }

        /// <summary>The <c>TRANSIT_REPICK</c> line: a source passed over because its lane's recorded
        /// transit cost, read as distance, put a cleaner one ahead.</summary>
        public static string FormatRepick(string flyerTag, string itemKey, string needNodeId, string choseNodeId,
                                          string overNodeId, double penaltyKm, double overKm, double choseKm)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "TRANSIT_REPICK " + flyerTag
                   + " item=" + itemKey
                   + " to=" + needNodeId
                   + " chose=" + choseNodeId
                   + " over=" + overNodeId
                   + " penaltyKm=" + penaltyKm.ToString("0", inv)
                   + " overKm=" + overKm.ToString("0", inv)
                   + " choseKm=" + choseKm.ToString("0", inv);
        }

        /// <summary><paramref name="nearestFirst"/> re-ordered by distance plus each lane's recorded
        /// transit cost as distance (<see cref="TransitLedger.PenaltyKm"/>), ties on node id as the
        /// distance order breaks them. The same list when no lane carries a cost.</summary>
        public static List<SpatialNode> ReorderByLane(List<SpatialNode> nearestFirst, string actorTag,
                                                      string deliverNodeId, Vec3 deliverPos, WorldModel world)
        {
            if (nearestFirst == null || nearestFirst.Count < 2) return nearestFirst;
            double[] penalty = null;
            for (int i = 0; i < nearestFirst.Count; i++)
            {
                double p = world.TransitLedger.PenaltyKm(actorTag, nearestFirst[i].Id, deliverNodeId, world.UtcNow);
                if (p <= 0) continue;
                if (penalty == null) penalty = new double[nearestFirst.Count];
                penalty[i] = p;
            }
            if (penalty == null) return nearestFirst;
            var keyed = new List<KeyValuePair<double, SpatialNode>>(nearestFirst.Count);
            for (int i = 0; i < nearestFirst.Count; i++)
                keyed.Add(new KeyValuePair<double, SpatialNode>(
                    nearestFirst[i].Position.DistanceTo(deliverPos) + penalty[i], nearestFirst[i]));
            keyed.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : string.CompareOrdinal(a.Value.Id, b.Value.Id));
            var ordered = new List<SpatialNode>(keyed.Count);
            for (int i = 0; i < keyed.Count; i++) ordered.Add(keyed[i].Value);
            return ordered;
        }

        // ---- internals ---------------------------------------------------------------------------

        private static double ItineraryCost(WorldModel world, FactionTuning t, PendingFleetAction fleet,
                                            List<Leg> legs, out double fuelPerKm, out LegPlan worstLeg)
        {
            fuelPerKm = 0;
            worstLeg = null;
            double total = 0;
            for (int i = 0; i < legs.Count; i++)
            {
                Vec3 from = legs[i].From, to = legs[i].To;
                fleet.CargoValue = (long)legs[i].Cargo;
                string key = (fleet.FleetId ?? fleet.LogId) + ":transit" + i.ToString(CultureInfo.InvariantCulture);
                RouteHullProfile hull = PendingFleetResolver.HullProfileFor(fleet, world, t);
                RouteCandidate chosen;
                int considered;
                PlanetRoutePlan route = RouteChooser.Choose(world, t, fleet, hull, from, to, key, out chosen, out considered);
                if (!route.IsClear) continue;
                LegPlan plan = PlanLeg(world, t, fleet, hull, from, to, route, key);
                if (i == 0)
                {
                    double fuel = plan.FuelCredits > 0 ? plan.FuelCredits
                        : FuelValue(world, t, fleet, Polyline(from, route.Waypoints, to));
                    double km = plan.RouteKm > 0 ? plan.RouteKm : FuelMeter.PolylineLengthKm(Polyline(from, route.Waypoints, to));
                    if (fuel > 0 && km > 0) fuelPerKm = fuel / km;
                }
                if (plan.Pick == Pick.None) continue;
                total += plan.Cost;
                if (worstLeg == null || plan.Cost > worstLeg.Cost) worstLeg = plan;
            }
            return total;
        }

        private static bool AnyCaughtStraight(WorldModel world, FactionTuning t, PendingFleetAction fleet,
                                              List<Leg> legs)
        {
            for (int i = 0; i < legs.Count; i++)
            {
                var line = new List<Vec3> { legs[i].From, legs[i].To };
                List<TransitRisk.Crossing> crossings = TransitRisk.Assess(world, t, fleet, line,
                    t.TransitTimeMins(world.ZonesFor(line)));
                for (int c = 0; c < crossings.Count; c++) if (crossings[c].Caught) return true;
            }
            return false;
        }

        /// <summary>One flown leg of an itinerary, with the cargo value aboard while it is flown.</summary>
        private struct Leg
        {
            public Vec3 From, To;
            public double Cargo;
        }

        /// <summary>The itinerary's flown legs: the current one, then each later stop with a position.
        /// A dwell at the node already reached flies nowhere. The cargo aboard follows the stops: a
        /// Pickup adds what it boards, a Service delivers the hold.</summary>
        private static List<Leg> Legs(PendingFleetAction fleet, WorldModel world)
        {
            var legs = new List<Leg>();
            double aboard = Math.Max(0, fleet.CargoValue);
            Vec3 at = fleet.TargetPosKm;
            legs.Add(new Leg { From = fleet.OriginPosKm, To = at, Cargo = aboard });
            aboard = AfterStop(world, aboard, fleet.CurrentStopKind, fleet.PickupItemKey, fleet.PickupQuantity);
            if (fleet.RemainingStops == null) return legs;
            for (int i = 0; i < fleet.RemainingStops.Count; i++)
            {
                FleetStop stop = fleet.RemainingStops[i];
                Vec3 next;
                SpatialNode node;
                if (stop.NodeId != null && world.Graph.Nodes.TryGetValue(stop.NodeId, out node)) next = node.Position;
                else if (stop.HasPosition) next = stop.PositionKm;
                else continue;
                if (next.DistanceTo(at) > 1e-6)
                {
                    legs.Add(new Leg { From = at, To = next, Cargo = aboard });
                    at = next;
                }
                aboard = AfterStop(world, aboard, stop.Kind, stop.PickupItemKey, stop.PickupQuantity);
            }
            return legs;
        }

        private static double AfterStop(WorldModel world, double aboard, FleetStopKind kind, string pickupItemKey,
                                        long pickupQuantity)
        {
            if (kind == FleetStopKind.Service) return 0;
            if (kind == FleetStopKind.Pickup && pickupItemKey != null && pickupQuantity > 0)
                return aboard + ItemCredits.For(pickupItemKey, pickupQuantity,
                    world.ItemUnitValue(ItemPriceResolver.StripInventoryPrefix(pickupItemKey)));
            return aboard;
        }

        /// <summary>Idle armed hulls at the node, cheapest first, when the node holds at least two:
        /// an escort never takes the last one.</summary>
        private static List<string> ArmedSpares(IOperationsExecutionContext ctx, string nodeId, IPrefabCatalogue cat)
        {
            var armed = new List<KeyValuePair<string, long>>();
            int count = 0;
            foreach (KeyValuePair<string, int> kv in ctx.SnapshotShipStock(nodeId))
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                if (ShipPool.TotalDps(cat.WeaponBands(kv.Key)) <= 0) continue;
                int available = ctx.AvailableShips(nodeId, kv.Key);
                if (available <= 0) continue;
                count += available;
                armed.Add(new KeyValuePair<string, long>(kv.Key, cat.Cost(kv.Key)));
            }
            var result = new List<string>();
            if (count < 2) return result;
            armed.Sort((a, b) => a.Value != b.Value ? a.Value.CompareTo(b.Value) : string.CompareOrdinal(a.Key, b.Key));
            for (int i = 0; i < armed.Count && i < 3; i++) result.Add(armed[i].Key);
            return result;
        }

        private static PendingFleetAction WithEscort(PendingFleetAction haul, string escortId, IPrefabCatalogue cat)
        {
            var squadron = new List<string>();
            if (haul.Squadron != null && haul.Squadron.Count > 0) squadron.AddRange(haul.Squadron);
            else for (int i = 0; i < Math.Max(1, haul.ConvoySize); i++) squadron.Add(haul.ShipPrefabId ?? "");
            squadron.Add(escortId);
            haul.Squadron = squadron;
            haul.SurvivingHulls = null;
            haul.ConvoySize = squadron.Count;
            if (haul.WeaponBands != null && haul.WeaponBands.Count > 0)
            {
                var bands = new List<WeaponBand>(haul.WeaponBands);
                bands.AddRange(ShipPool.ConvoyWeaponBands(cat, escortId, 1));
                haul.WeaponBands = bands;
            }
            return haul;
        }

        /// <summary>The escort's own burn over the legs, flown empty beside the convoy.</summary>
        private static double EscortFuel(WorldModel world, FactionTuning t, PendingFleetAction haul, string escortId,
                                         List<Leg> legs)
        {
            var escort = new PendingFleetAction
            {
                Kind = ActionKind.Guard,
                ActorTag = haul.ActorTag,
                ShipPrefabId = escortId,
                ConvoySize = 1,
                FleetId = haul.FleetId,
            };
            double sum = 0;
            for (int i = 0; i < legs.Count; i++)
            {
                double fuel = FuelValue(world, t, escort, new List<Vec3> { legs[i].From, legs[i].To });
                if (fuel <= 0) return 0;
                sum += fuel;
            }
            return sum;
        }

        private static PlanetRoutePlan Via(WorldModel world, RouteHullProfile hull, PlanetRouteOptions options,
                                           Vec3 fromKm, Vec3 viaKm, Vec3 toKm, string stableKey)
        {
            PlanetRoutePlan first = PlanetRoutePlanner.BuildPlan(fromKm, viaKm, world.Planets, hull, options,
                                                                 stableKey + ":via1", null);
            if (!first.IsClear) return null;
            PlanetRoutePlan second = PlanetRoutePlanner.BuildPlan(viaKm, toKm, world.Planets, hull, options,
                                                                  stableKey + ":via2", null);
            if (!second.IsClear) return null;
            var plan = new PlanetRoutePlan { IsClear = true };
            plan.Waypoints.AddRange(first.Waypoints);
            plan.Waypoints.Add(viaKm);
            plan.Waypoints.AddRange(second.Waypoints);
            plan.DistanceKm = FuelMeter.PolylineLengthKm(Polyline(fromKm, plan.Waypoints, toKm));
            var trail = new List<string>();
            if (first.ProfileSummary != null) trail.Add(first.ProfileSummary);
            trail.Add("transit");
            if (second.ProfileSummary != null) trail.Add(second.ProfileSummary);
            plan.ProfileSummary = string.Join(",", trail.ToArray());
            return plan;
        }

        /// <summary>Unit vector from <paramref name="centre"/> toward the nearest point of the route, so a
        /// detour swings the side the route already passes on (or the other). Perpendicular to the
        /// route when it runs through the centre.</summary>
        private static Vec3 AwayFrom(IList<Vec3> polyline, Vec3 centre)
        {
            double best = double.MaxValue;
            Vec3 nearest = polyline[0], dir = new Vec3(1, 0, 0);
            for (int i = 0; i < polyline.Count - 1; i++)
            {
                Vec3 a = polyline[i], b = polyline[i + 1];
                double abx = b.X - a.X, aby = b.Y - a.Y, abz = b.Z - a.Z;
                double lengthSq = abx * abx + aby * aby + abz * abz;
                if (lengthSq <= 1e-12) continue;
                double s = ((centre.X - a.X) * abx + (centre.Y - a.Y) * aby + (centre.Z - a.Z) * abz) / lengthSq;
                if (s < 0) s = 0; else if (s > 1) s = 1;
                var q = new Vec3(a.X + abx * s, a.Y + aby * s, a.Z + abz * s);
                double d = q.DistanceTo(centre);
                if (d < best) { best = d; nearest = q; dir = new Vec3(abx, aby, abz); }
            }
            var away = new Vec3(nearest.X - centre.X, nearest.Y - centre.Y, nearest.Z - centre.Z);
            if (Length(away) > 1e-6) return Scale(away, 1.0 / Length(away));
            Vec3 perp = Cross(dir, new Vec3(0, 0, 1));
            if (Length(perp) <= 1e-6) perp = Cross(dir, new Vec3(0, 1, 0));
            return Scale(perp, 1.0 / Length(perp));
        }

        private static int Costliest(IList<TransitRisk.Crossing> crossings, double cargo, double hulls, double dps,
                                     FactionTuning t)
        {
            int worst = -1;
            double most = -1;
            for (int i = 0; i < crossings.Count; i++)
            {
                double loss = TransitRisk.LossCredits(crossings[i], cargo, hulls, dps, t);
                if (!crossings[i].Caught) loss = -0.5;
                if (loss > most) { most = loss; worst = i; }
            }
            return worst < 0 ? 0 : worst;
        }

        /// <summary>What <paramref name="fleet"/> would burn flying <paramref name="polylineKm"/>, valued as
        /// the convoy gate values fuel (<see cref="HaulHullCost.CreditsOf"/>) at the node it leaves: the
        /// fuel the faction gives up, not the import price, which is 0 while nothing is imported.</summary>
        private static double FuelValue(WorldModel world, FactionTuning t, PendingFleetAction fleet, IList<Vec3> polylineKm)
        {
            return HaulHullCost.CreditsOf(world, fleet.OriginNodeId ?? fleet.ReturnNodeId,
                                          FuelMeter.BurnForPolyline(world, t, fleet, polylineKm));
        }

        private static List<Vec3> Polyline(Vec3 fromKm, IList<Vec3> waypoints, Vec3 toKm)
        {
            var line = new List<Vec3>(waypoints.Count + 2) { fromKm };
            for (int i = 0; i < waypoints.Count; i++) line.Add(waypoints[i]);
            line.Add(toKm);
            return line;
        }

        private static double Length(Vec3 v) { return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z); }
        private static Vec3 Scale(Vec3 v, double k) { return new Vec3(v.X * k, v.Y * k, v.Z * k); }
        private static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
    }
}
