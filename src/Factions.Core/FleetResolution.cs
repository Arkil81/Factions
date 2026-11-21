using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>BFS pathfinder over <see cref="SpatialGraph.Neighbors"/>. SE-free; used by
    /// <see cref="PendingFleetResolver"/> to route convoys through the node graph so mid-transit
    /// encounters can fire at intermediate waypoints.</summary>
    internal static class GraphPathfinder
    {
        /// <summary>BFS shortest path (fewest hops) from <paramref name="fromId"/> to
        /// <paramref name="toId"/>. Returns an ordered array [from … to] inclusive, or null when
        /// the nodes are not connected.</summary>
        public static string[] FindPath(SpatialGraph graph, string fromId, string toId)
        {
            if (fromId == null || toId == null) return null;
            if (fromId == toId) return new[] { fromId };
            if (!graph.Nodes.ContainsKey(fromId) || !graph.Nodes.ContainsKey(toId)) return null;

            var prev = new Dictionary<string, string>();
            var queue = new Queue<string>();
            prev[fromId] = null;
            queue.Enqueue(fromId);

            while (queue.Count > 0)
            {
                string cur = queue.Dequeue();
                if (cur == toId) break;
                SpatialNode node;
                if (!graph.Nodes.TryGetValue(cur, out node)) continue;
                foreach (string nbId in node.Neighbors)
                {
                    if (prev.ContainsKey(nbId)) continue;
                    prev[nbId] = cur;
                    queue.Enqueue(nbId);
                }
            }

            if (!prev.ContainsKey(toId)) return null;

            var path = new List<string>();
            string step = toId;
            while (step != null) { path.Add(step); string p; prev.TryGetValue(step, out p); step = p; }
            path.Reverse();
            return path.ToArray();
        }

        /// <summary>Sum of Euclidean distances between consecutive nodes in <paramref name="path"/>.
        /// Returns 0 for null / single-node paths.</summary>
        public static double PathDistance(string[] path, SpatialGraph graph)
        {
            if (path == null || path.Length < 2) return 0;
            double total = 0;
            for (int i = 0; i < path.Length - 1; i++)
            {
                SpatialNode a, b;
                if (graph.Nodes.TryGetValue(path[i], out a) && graph.Nodes.TryGetValue(path[i + 1], out b))
                    total += a.Position.DistanceTo(b.Position);
            }
            return total;
        }
    }

    /// <summary>Which part of a fleet's remaining itinerary has actually been routed.</summary>
    public static class FleetItinerary
    {
        /// <summary>
        /// How many leading entries of <paramref name="remainingStops"/> belong to the clearance
        /// polyline the fleet is flying right now. Everything past that count is a leg whose route
        /// does not exist yet: legs are planned one at a time, by
        /// <c>PendingFleetResolver.AdvanceToNextStop</c> at the moment the fleet resolves the
        /// stop before them, so a later stop is a bare destination with no waypoints between here
        /// and it. A surface that draws it draws a straight line through whatever body lies between.
        ///
        /// <para>The <see cref="FleetStopKind.Navigation"/> run is the discriminator, and it carries
        /// the whole answer on its own. A run of one or more means an arc is still ahead, and the
        /// semantic stop closing that run is the point the arc was planned to reach, so it is routed
        /// too. An empty run means the current target is already that point — either the last chord
        /// of an arc or a plain direct leg — and the next stop after it is unrouted either way.</para>
        /// </summary>
        public static int RoutedStopCount(IList<FleetStop> remainingStops)
        {
            if (remainingStops == null) return 0;
            int navigation = 0;
            while (navigation < remainingStops.Count
                   && remainingStops[navigation].Kind == FleetStopKind.Navigation)
            {
                navigation++;
            }
            if (navigation == 0) return 0;
            return navigation < remainingStops.Count ? navigation + 1 : navigation;
        }
    }

    /// <summary>
    /// Resolves in-flight <see cref="PendingFleetAction"/> events each sim tick: advances transit,
    /// detects meetings between fleets converging on the same node, runs combat/interception, then
    /// applies arrival effects. Fully SE-free — lives in Core so the offline sim and unit tests
    /// can exercise the same mechanic as the host.
    /// </summary>
    public static partial class PendingFleetResolver
    {
        // Why the dock sent cargo home, reported per resolve on TRADE_RESOLVED. `Other` is the
        // unattributed slot: it should stay at zero, and a non-zero one means a refusal path exists
        // that this accounting does not name.
        private const int TradeRefusalOther    = 0;
        private const int TradeRefusalFloor    = 1;   // recipient reads as oversupplied in the item
        private const int TradeRefusalWindow   = 2;   // recipient's 30 s inbound budget is spent
        private const int TradeRefusalHeadroom = 3;   // recipient's treasury is at the insolvency floor
        private const int TradeRefusalCounterparty = 4;   // the recipient refuses the pair (DockRefusal)

        /// <summary>Treasury a recipient may draw on for arriving cargo, beyond income and the floor.
        /// Zero when the wealth term is switched off, so the surplus is not computed for nothing, and
        /// zero for a faction at or below its operational reserve.</summary>
        private static long InboundWealthSurplus(string tag, WorldModel world, FactionTuning tuning)
        {
            if (tuning.TradeInboundWealthFractionPerWindow <= 0) return 0;
            return Economy.SpendableTreasurySurplus(world.GetTreasury(tag), world.GetNetFlow(tag), tuning);
        }

        // ---- factory ----------------------------------------------------------------------------

        private static bool RequiresReturnLeg(ActionKind kind)
        {
            return kind == ActionKind.Trade || kind == ActionKind.Raid
                || kind == ActionKind.Retaliate || kind == ActionKind.Conquer
                || kind == ActionKind.Patrol || kind == ActionKind.Gift;
        }

        /// <summary>Stop kinds timed from <see cref="FleetStop.DurationSeconds"/> rather than from
        /// the distance flown to them -- the fleet is already at the node and is spending time
        /// there, so the usual transit calculation would resolve them instantly.</summary>
        internal static bool IsDwellStop(FleetStopKind kind)
        {
            return kind == FleetStopKind.Mine || kind == FleetStopKind.Engage || kind == FleetStopKind.Intercept;
        }

        /// <summary>The three kinds that hold at their target for an engagement dwell
        /// (<see cref="FleetStopKind.Engage"/>) instead of resolving the moment they arrive.</summary>
        internal static bool EngagesOnArrival(ActionKind kind)
        {
            return kind == ActionKind.Raid || kind == ActionKind.Retaliate || kind == ActionKind.Conquer;
        }

        /// <summary>True while an arriving Raid/Retaliate/Conquer is only making contact: its
        /// engagement dwell is still queued ahead of it, so neither the defence duel nor the raid's
        /// own effect belongs to this arrival. False for a fleet already on its
        /// <see cref="FleetStopKind.Engage"/> leg (the dwell is over -- fight it now) and for any
        /// fleet with no dwell queued at all, which is every legacy persisted fleet, every immediate
        /// dispatch, and every fleet at all when <see cref="FactionTuning.RaidDwellSeconds"/> is 0.
        /// Those keep resolving on contact exactly as they did before the dwell existed.</summary>
        private static bool IsPreEngagementContact(PendingFleetAction fleet)
        {
            return fleet.CurrentStopKind != FleetStopKind.Engage
                   && fleet.RemainingStops != null && fleet.RemainingStops.Count > 0
                   && fleet.RemainingStops[0].Kind == FleetStopKind.Engage;
        }

        /// <summary>True when this arrival is the fleet's mission -- the leg its effect belongs to --
        /// rather than a step on the way there or the trip home. Only a mission arrival is fought
        /// over: a raider coming home is not attacking the node it is landing at, and its own
        /// garrison has no business shooting at it.
        /// <para><see cref="FleetStopKind.Engage"/> is the mission leg once a dwell is queued;
        /// <see cref="FleetStopKind.Service"/> is it for a fleet with no dwell, which is every legacy
        /// persisted fleet, every immediate dispatch, and every fleet when
        /// <see cref="FactionTuning.RaidDwellSeconds"/> is 0. Stated as a positive list rather than
        /// as "not Return" so a stop kind added later has to be admitted deliberately.</para></summary>
        private static bool IsMissionArrival(PendingFleetAction fleet)
        {
            // A retreating fleet is leaving, not attacking: nobody duels it on the way out.
            if (fleet.RetreatReason != null) return false;
            return fleet.CurrentStopKind == FleetStopKind.Engage
                || fleet.CurrentStopKind == FleetStopKind.Service;
        }

        /// <summary>Breaks a fleet off whatever it is holding station for, so it goes home.
        /// Returns false, changing nothing, for a fleet that is not holding station or is already
        /// retreating.
        /// <list type="bullet">
        ///   <item>On an <see cref="FleetStopKind.Engage"/> dwell (Raid, Retaliate, Conquer,
        ///   Reinforce): the dwell ends now and every siege dwell still queued is dropped. The leg
        ///   then resolves as a retreat on the next pass, with no effect and no loot, and the return
        ///   leg takes the fleet home.</item>
        ///   <item>A Guard standing its duty window: the window ends now, and it comes home the way
        ///   an expired Guard does.</item>
        /// </list>
        /// <para>An interception hold is not broken: that verdict belongs to the encounter. Nothing
        /// here forces a ratio, so the retreating hulls are credited home like any others.</para></summary>
        public static bool BeginRetreat(ref PendingFleetAction fleet, DateTime utcNow, FactionTuning tuning, string reason)
        {
            if (fleet.RetreatReason != null || tuning == null) return false;
            if (fleet.CurrentStopKind == FleetStopKind.Engage && !fleet.Resolved)
            {
                if (fleet.RemainingStops != null)
                    for (int i = fleet.RemainingStops.Count - 1; i >= 0; i--)
                        if (fleet.RemainingStops[i].Kind == FleetStopKind.Engage)
                            fleet.RemainingStops.RemoveAt(i);
                if (fleet.EstimatedArrivalUtc > utcNow) fleet.EstimatedArrivalUtc = utcNow;
                fleet.RetreatReason = reason ?? "retreat";
                return true;
            }
            if (fleet.IsActiveGuard(utcNow, tuning))
            {
                fleet.EstimatedArrivalUtc = utcNow - tuning.GuardDuration;
                fleet.RetreatReason = reason ?? "retreat";
                return true;
            }
            return false;
        }

        /// <summary>Whether a defence duel should run against this arriving fleet at all. A fleet
        /// that already carries a forced ratio from losing its physical grid has had its outcome
        /// decided by whoever destroyed, captured or stranded it; running a duel on top would
        /// overwrite that ratio with the duel's own -- and against a node with no station weapons
        /// the duel is a walkover the raider wins outright, turning a player's kill into a
        /// full-effect raid. Cargo and convoy kinds never reach here; this gates the attacker
        /// side of the Raid/Retaliate/Conquer meetings only.</summary>
        private static bool IsOutcomeAlreadyDecided(PendingFleetAction fleet)
        {
            if (!fleet.ForcedRatio.HasValue) return false;
            string cause = fleet.ForcedRatioCause;
            return cause == "grid_destroyed" || cause == "grid_captured"
                || cause == "grid_stalled"   || cause == DisabledHull.Cause
                || cause == "spawn_dedup"
                || cause == SiblingDismissCause
                || cause == QueueGiveUpCause;
        }

        /// <summary>The ratio a fleet whose grid left the world with no grid deciding it -- a stall
        /// (<c>grid_stalled</c>) or a dismissed duplicate (<c>spawn_dedup</c>) -- resolves at. Zero on
        /// every leg but the engagement dwell, where the elapsed fraction of the dwell stands. A lead
        /// lost in combat (<see cref="IsCombatLoss"/>) is not priced here: it forces zero, and an
        /// attack that suffers one has lost its meeting (<see cref="SettleLeadLost"/>).
        /// <para><b>Call this before rescheduling the fleet.</b> The elapsed fraction is read from
        /// <see cref="PendingFleetAction.EstimatedArrivalUtc"/>, which is the dwell's own end time; a
        /// caller that force-arrives the fleet first (sets that to now, as every grid-loss site does)
        /// has already erased the window and will read 1.0 back.</para></summary>
        public static double LossRatioFor(PendingFleetAction fleet, DateTime utcNow)
        {
            if (fleet.CurrentStopKind != FleetStopKind.Engage) return 0.0;
            double r = fleet.CompletionRatio(utcNow);
            return r < 0 ? 0.0 : (r > 1 ? 1.0 : r);
        }

        /// <summary>Hull inputs the route planner needs to pick an arc altitude. The whole convoy has
        /// to fly the same arc, so the heaviest scanned hull, with its share of the hold aboard,
        /// decides what it can clear. Returns
        /// <see cref="RouteHullProfile.Unknown"/> when nothing is scanned, which routes along the
        /// outer orbit shell rather than guessing a hull into an atmosphere.</summary>
        internal static RouteHullProfile HullProfileFor(PendingFleetAction fleet, WorldModel world,
                                                       FactionTuning tuning)
        {
            IPrefabCatalogue catalogue = world == null ? null : world.PrefabCatalogue;
            if (catalogue == null || tuning == null) return RouteHullProfile.Unknown;

            string prefabId = fleet.ShipPrefabId;
            double heaviestKg = string.IsNullOrEmpty(prefabId) ? 0 : catalogue.DryMassKg(prefabId);
            if (fleet.Squadron != null)
                foreach (string memberId in fleet.Squadron)
                {
                    double massKg = catalogue.DryMassKg(memberId);
                    if (massKg > heaviestKg) { heaviestKg = massKg; prefabId = memberId; }
                }

            if (string.IsNullOrEmpty(prefabId) || heaviestKg <= 0) return RouteHullProfile.Unknown;

            // The hold is shared in proportion to dry mass, as the fuel meter shares it, so the
            // heaviest hull carries the largest share.
            double cargoKg = FuelMeter.CargoMassKg(fleet, world.ItemMasses);
            double convoyDryKg = 0;
            foreach (string hull in FuelMeter.HullsOf(fleet)) convoyDryKg += catalogue.DryMassKg(hull);
            return new RouteHullProfile
            {
                ThrustBands = catalogue.ThrustBands(prefabId),
                DryMassKg = heaviestKg,
                CargoMassKg = convoyDryKg > 0 ? cargoKg * heaviestKg / convoyDryKg : 0,
                MinDryTwr = tuning.MinDryTwr,
                LiftBand = ShipPool.LiftBandFor(catalogue, prefabId),
            };
        }

        /// <summary>Plans one leg around any blocking bodies and emits a single line describing the
        /// whole route, rather than one per generated waypoint — the per-faction decision log keeps
        /// only its most recent entries, so per-waypoint chatter would flush everything else out.</summary>
        private static PlanetRoutePlan PlanRoute(PendingFleetAction fleet, Vec3 fromKm, Vec3 toKm,
                                                 WorldModel world, FactionTuning tuning, string keySuffix,
                                                 IList<string> log, IList<string> transitLog = null)
        {
            RouteCandidate chosen;
            int considered;
            RouteHullProfile hull = HullProfileFor(fleet, world, tuning);
            PlanetRoutePlan route = RouteChooser.Choose(world, tuning, fleet,
                hull, fromKm, toKm, fleet.FleetId + keySuffix,
                out chosen, out considered);

            // A carrier's route through hostile space is priced, and flown around when going around
            // is cheaper than the expected catch (TransitPlanner).
            if (route.IsClear && TransitRisk.Applies(world))
            {
                TransitPlanner.LegPlan transit = TransitPlanner.PlanLeg(world, tuning, fleet, hull, fromKm, toKm,
                    route, fleet.FleetId + keySuffix);
                if (transit.Pick != TransitPlanner.Pick.None)
                {
                    double laneWeight = TransitPlanner.RecordDenial(world, fleet.ActorTag, transit.Crossings,
                        transit.Cost, fleet, tuning, world.UtcNow);
                    IList<string> sink = transitLog ?? log;
                    if (sink != null)
                        sink.Add(TransitPlanner.FormatLeg(fleet, transit, laneWeight, tuning, world.PrefabCatalogue));
                    if (transit.Pick == TransitPlanner.Pick.Detour)
                    {
                        route = transit.Route;
                        chosen.Label = "transit";
                    }
                }
            }

            if (log == null) return route;
            if (!route.IsClear)
                log.Add("FLEET_ROUTE_FAILED " + fleet.LogId + " " + fleet.ActorTag + " " + route.FailureReason);
            else if (route.Waypoints.Count > 0)
                log.Add("FLEET_ROUTED " + fleet.LogId + " " + fleet.ActorTag + " via=" + route.ProfileSummary
                        + " vertices=" + route.Waypoints.Count
                        + " routed=" + route.DistanceKm.ToString("0", CultureInfo.InvariantCulture) + "km"
                        + " direct=" + fromKm.DistanceTo(toKm).ToString("0", CultureInfo.InvariantCulture) + "km"
                        + " pick=" + (chosen.Label ?? "direct")
                        + " of=" + considered.ToString(CultureInfo.InvariantCulture)
                        + " mins=" + chosen.Minutes.ToString("0.##", CultureInfo.InvariantCulture));
            return route;
        }

        /// <summary>Coordinate-only stops for <paramref name="route"/>'s waypoints from
        /// <paramref name="fromIndex"/> onward.</summary>
        private static List<FleetStop> NavigationStops(PlanetRoutePlan route, int fromIndex)
        {
            var stops = new List<FleetStop>();
            for (int i = fromIndex; i < route.Waypoints.Count; i++)
                stops.Add(new FleetStop
                {
                    Kind = FleetStopKind.Navigation,
                    HasPosition = true,
                    PositionKm = route.Waypoints[i]
                });
            return stops;
        }

        /// <summary>The current leg's semantic stop, re-queued behind the generated clearance stops.
        /// Carries the leg's Pickup/Mine payload so a routed detour does not strip it.</summary>
        private static FleetStop ReQueuedCurrentStop(PendingFleetAction fleet)
        {
            return new FleetStop
            {
                NodeId = fleet.TargetNodeId,
                Kind = fleet.CurrentStopKind,
                PickupItemKey = fleet.PickupItemKey,
                PickupQuantity = fleet.PickupQuantity,
                MineWeights = fleet.MineWeights,
                MineTargetLiters = fleet.MineTargetLiters,
            };
        }

        /// <summary>
        /// Replaces an obstructed semantic leg with persisted coordinate-only navigation stops,
        /// leaving the original semantic stop (and any later itinerary stops) after them. Direct
        /// and immediate legs deliberately retain their legacy graph/fast-path behaviour.
        /// </summary>
        public static PendingFleetAction PlanFleetLeg(PendingFleetAction fleet, WorldModel world,
                                                      FactionTuning tuning, bool immediate,
                                                      IList<string> log = null, IFuelLegSink fuelSink = null,
                                                      IList<string> fuelLog = null, IList<string> transitLog = null)
        {
            // Two log sinks, because they are two decisions. `log` turns on the route lines a
            // caller may or may not want per dispatch kind; `fuelLog` is where FUEL_DRAWN goes for
            // a caller that wants the fuel trace without the route chatter.
            IList<string> burnLog = fuelLog ?? log;
            // A generated clearance leg has no TargetNodeId; that, not the stop kind, is what marks a
            // leg as already planned. Mine dispatches travel to their source under a Navigation stop
            // kind while still carrying a real target node, and do need planning.
            if (world == null || tuning == null || immediate || tuning.FleetSpeedKmPerMin <= 0
                || fleet.TargetNodeId == null)
                return fleet;

            // The node the hull actually leaves from: what FuelDrawMode 2 takes the leg's fuel out
            // of. Null at a generated clearance waypoint, which draws nothing -- a chain is drawn
            // for once, at its start, the same way it is priced once.
            SpatialNode originNode = null;
            if (fleet.OriginNodeId != null) world.Graph.Nodes.TryGetValue(fleet.OriginNodeId, out originNode);

            PlanetRoutePlan route = PlanRoute(fleet, fleet.OriginPosKm, fleet.TargetPosKm, world, tuning, "", log,
                                              transitLog);
            if (!route.IsClear || route.Waypoints.Count == 0)
            {
                FuelMeter.PriceLeg(world, tuning, fleet, fleet.OriginPosKm, fleet.TargetPosKm, burnLog, fuelSink,
                                   originNode);
                // The straight leg still crosses whatever ground lies along it, so it is scheduled
                // against that rather than against its length. Every dispatcher reaches this, which
                // is why the tiers are stamped here instead of at each of them.
                fleet.LegZones = world.ZonesFor(fleet.OriginPosKm, fleet.TargetPosKm);
                double tieredMins = tuning.TransitTimeMins(fleet.LegZones);
                if (tieredMins > 0) fleet.EstimatedArrivalUtc = fleet.DepartedUtc.AddMinutes(tieredMins);
                return fleet;
            }

            // A clearance chain is one leg with one acceleration profile, so it is priced once here,
            // over the polyline it will fly, and its chords are skipped as they are reached. Priced
            // before the fields below are rewritten to point at the first waypoint.
            var polylineKm = new List<Vec3>(route.Waypoints.Count + 2);
            polylineKm.Add(fleet.OriginPosKm);
            for (int i = 0; i < route.Waypoints.Count; i++) polylineKm.Add(route.Waypoints[i]);
            polylineKm.Add(fleet.TargetPosKm);
            FuelMeter.PriceLeg(world, tuning, fleet, polylineKm, burnLog, fuelSink, originNode);

            List<FleetStop> stops = NavigationStops(route, 1);
            stops.Add(ReQueuedCurrentStop(fleet));
            if (fleet.RemainingStops != null)
                stops.AddRange(fleet.RemainingStops);

            Vec3 first = route.Waypoints[0];
            fleet.TargetNodeId = null;
            fleet.TargetTag = null;
            fleet.TargetPosKm = first;
            fleet.CurrentStopKind = FleetStopKind.Navigation;
            fleet.PickupItemKey = null;
            fleet.PickupQuantity = 0;
            fleet.MineWeights = null;
            fleet.MineTargetLiters = 0;
            fleet.RemainingStops = stops;
            fleet.PathNodeIds = null;
            fleet.LastResolvedSegment = -1;
            // The whole polyline runs on one acceleration profile; treating each clearance point as a
            // cold-start would make a detour take several times longer than its routed ETA.
            fleet.RouteDistanceKm = route.DistanceKm;
            fleet.RouteCoveredKm = fleet.OriginPosKm.DistanceTo(first); // where the next leg starts
            fleet.RouteDepartedUtc = fleet.DepartedUtc;
            // The tiers are read over the whole polyline, so every leg of the chain is billed its
            // slice of one schedule -- including the stretch of it that is inside a well.
            fleet.LegZones = world.ZonesFor(polylineKm);
            fleet.EstimatedArrivalUtc = fleet.DepartedUtc.AddMinutes(
                tuning.TransitElapsedMinsAtDistance(fleet.LegZones, fleet.RouteCoveredKm));
            return fleet;
        }

        /// <summary>What a mid-leg retarget does to the itinerary the fleet is already carrying.</summary>
        public enum FleetRetargetPolicy
        {
            /// <summary>The new node replaces the current destination; queued later stops are kept.
            /// For a fleet whose current destination carries no commitment -- a Guard flying to a
            /// node that is no longer the urgent one -- and the reason a Trade redirected this way
            /// still flies its Return leg afterwards.</summary>
            Replace,
            /// <summary>The new node is spliced in ahead of the current destination, which is
            /// requeued behind it. For a fleet whose current destination is owed something: a Haul
            /// with cargo committed against a delivery still has to make that delivery.</summary>
            Divert,
            /// <summary>The new node replaces the current destination, and the current stop goes
            /// there as it is: its kind, its pickup payload and the fleet's target tag are kept.
            /// For a Pickup collected from another node of the same owner
            /// (<see cref="QueueDivert.PickupSource"/>).</summary>
            KeepStop
        }

        /// <summary>
        /// Redirects a fleet that has not yet reached its current stop --
        /// <c>docs/fleet-waypoints-plan.md</c>'s Slice C, the one thing <see cref="AdvanceToNextStop"/>
        /// cannot do, since that only ever fires at a leg boundary.
        ///
        /// <para>The fleet re-origins at its own interpolated position rather than at the last graph
        /// node it passed. The plan originally specified the latter, but that predates in-place grid
        /// retargeting (<c>IFleetSpawner.Retarget</c>): re-originating from a node already behind the
        /// fleet moves the abstract position backwards, and a materialised grid does not move with
        /// it. <see cref="PendingFleetAction.OriginNodeId"/> is deliberately left alone -- it means
        /// "where this fleet was dispatched from", which is what Haul and Trade settlement read, and
        /// is reassigned at the next real leg boundary anyway.</para>
        ///
        /// <para>Only an unobserved fleet may be retargeted here. A materialised grid needs the host
        /// to be told, and that handoff is the Player Overhaul's (see <c>docs/todo.md</c>); silently
        /// moving the abstract fleet out from under a grid a player is looking at is the failure this
        /// guard exists to prevent.</para>
        ///
        /// <para>Returns false and changes nothing when the fleet is ineligible or the node is
        /// unknown. Churn control (cooldown, retarget budget, urgency margin) is the caller's:
        /// this is the mechanism, not the policy.</para>
        /// </summary>
        public static bool Retarget(FactionState owner, int fleetIndex, string newTargetNodeId,
            FleetRetargetPolicy policy, WorldModel world, FactionTuning tuning, IList<string> log,
            string cause)
        {
            if (owner == null || world == null || tuning == null) return false;
            if (fleetIndex < 0 || fleetIndex >= owner.PendingFleets.Count) return false;

            PendingFleetAction fleet = owner.PendingFleets[fleetIndex];
            DateTime now = world.UtcNow;

            if (fleet.Resolved || fleet.Intercepted) return false;
            if (fleet.MaterialisedEntityId != 0) return false;
            if (fleet.HasArrived(now)) return false;
            // A dwell is a stationary duration, not a journey: a Mine parked on its drill site and
            // a raider mid-engagement are both spending time where they already are, and neither
            // has a leg left to redirect.
            if (IsDwellStop(fleet.CurrentStopKind)) return false;
            if (newTargetNodeId == null) return false;

            SpatialNode newTarget;
            if (!world.Graph.Nodes.TryGetValue(newTargetNodeId, out newTarget)) return false;
            if (string.Equals(fleet.TargetNodeId, newTargetNodeId, StringComparison.OrdinalIgnoreCase))
                return false;

            string fromNodeId = fleet.TargetNodeId;
            Vec3 resumeFrom = fleet.CurrentPosKm(now, tuning);

            List<FleetStop> stops = policy == FleetRetargetPolicy.Divert
                ? new List<FleetStop> { ReQueuedCurrentStop(fleet) }
                : new List<FleetStop>();
            if (fleet.RemainingStops != null) stops.AddRange(fleet.RemainingStops);

            fleet.OriginPosKm = resumeFrom;
            fleet.TargetNodeId = newTarget.Id;
            fleet.TargetPosKm = newTarget.Position;
            fleet.RemainingStops = stops.Count > 0 ? stops : null;
            if (policy != FleetRetargetPolicy.KeepStop)
            {
                fleet.TargetTag = newTarget.OwnerFactionTag;
                fleet.CurrentStopKind = FleetStopKind.Service;
                // The pickup/mine payloads belong to the stop that was current a moment ago. Divert
                // requeued that stop with them attached; Replace dropped it, and they go with it.
                fleet.PickupItemKey = null;
                fleet.PickupQuantity = 0;
                fleet.MineWeights = null;
                fleet.MineTargetLiters = 0;
            }
            // The old graph path and clearance profile both describe a journey that is no longer
            // being flown. Nulling PathNodeIds costs this leg its mid-transit encounter checks
            // (ResolveMidTransitMeetings needs waypoints); PlanFleetLeg below re-plans clearance.
            fleet.PathNodeIds = null;
            fleet.LastResolvedSegment = -1;
            fleet.RouteDistanceKm = 0;
            fleet.RouteCoveredKm = 0;
            fleet.RouteDepartedUtc = default(DateTime);

            double distanceKm = resumeFrom.DistanceTo(newTarget.Position);
            fleet.DepartedUtc = now;
            fleet.EstimatedArrivalUtc = tuning.FleetSpeedKmPerMin > 0
                ? now.AddMinutes(tuning.TransitTimeMins(distanceKm))
                : now;

            fleet.RetargetCount = fleet.RetargetCount + 1;
            fleet.LastRetargetUtc = now;

            owner.PendingFleets[fleetIndex] = fleet;
            fleet = PendingFleetResolver.PlanFleetLeg(fleet, world, tuning, false, log);
            owner.PendingFleets[fleetIndex] = fleet;

            if (log != null)
                log.Add("FLEET_RETARGET " + fleet.LogId + " " + (fleet.ActorTag ?? "unknown")
                        + " kind=" + fleet.Kind
                        + " " + (fromNodeId ?? "[navigation]") + " -> " + newTarget.Id
                        + " policy=" + policy
                        + " cause=" + (cause ?? "unspecified")
                        + " n=" + fleet.RetargetCount
                        + " distKm=" + distanceKm.ToString("0", CultureInfo.InvariantCulture)
                        + " eta=" + fleet.EstimatedArrivalUtc.ToString("HH:mm:ss"));
            return true;
        }

        /// <summary>Rebuilds <see cref="PendingFleetAction.WeaponBands"/> for every in-flight fleet
        /// that has none, from the hull and count it was dispatched with. Returns how many were
        /// rebuilt. Run at load: the bands are derived from
        /// <see cref="PendingFleetAction.ShipPrefabId"/>/<see cref="PendingFleetAction.Squadron"/> and
        /// <see cref="PendingFleetAction.ConvoySize"/>, which are persisted, so they are reconstructed
        /// rather than stored.
        ///
        /// <para>Both consumers need them to be right. <c>ResolveShipDuel</c> gives a side an effective
        /// HP of <c>Math.Max(1, TotalDps(bands))</c> and <c>InRangeDps</c> returns 0 for an empty list,
        /// so a fleet with no bands cannot fight and dies to anything; <see cref="FleetManager"/>'s
        /// commit gate reads them to size a defender against what is coming.</para>
        ///
        /// <para>A fleet whose hull is unknown to the catalogue is left as it is rather than given an
        /// empty list: it is already empty, and filling it would hide a catalogue gap behind a fleet
        /// that reads as armed.</para></summary>
        public static int RebuildMissingWeaponBands(IList<FactionState> states, IPrefabCatalogue catalogue)
        {
            if (states == null || catalogue == null) return 0;
            int rebuilt = 0;
            for (int si = 0; si < states.Count; si++)
            {
                FactionState state = states[si];
                if (state == null) continue;
                for (int fi = 0; fi < state.PendingFleets.Count; fi++)
                {
                    PendingFleetAction fleet = state.PendingFleets[fi];
                    if (fleet.WeaponBands != null && fleet.WeaponBands.Count > 0) continue;

                    // A fleet with damage on record fights at it; every other one exactly as before.
                    List<WeaponBand> bands = fleet.HullIntegrity != null
                        ? HullDamage.ScaledBands(fleet, catalogue)
                        : fleet.Squadron != null && fleet.Squadron.Count > 0
                        ? SquadronBands(catalogue, fleet.Squadron)
                        : ShipPool.ConvoyWeaponBands(catalogue, fleet.ShipPrefabId, Math.Max(1, fleet.ConvoySize));
                    if (bands == null || bands.Count == 0) continue;

                    fleet.WeaponBands = bands;
                    state.PendingFleets[fi] = fleet;
                    rebuilt++;
                }
            }
            return rebuilt;
        }

        /// <summary>The bands of a squadron: one hull's worth per member, summed by concatenation the
        /// same way <see cref="ShipPool.ConvoyWeaponBands"/> scales a single-prefab convoy.</summary>
        private static List<WeaponBand> SquadronBands(IPrefabCatalogue catalogue, List<string> squadron)
        {
            var bands = new List<WeaponBand>();
            for (int i = 0; i < squadron.Count; i++)
            {
                IReadOnlyList<WeaponBand> perShip = catalogue.WeaponBands(squadron[i]);
                if (perShip == null) continue;
                for (int b = 0; b < perShip.Count; b++) bands.Add(perShip[b]);
            }
            return bands;
        }

        /// <summary>Rebuild pending arrival timestamps after a transit tuning migration. The fleet's
        /// old time fraction is retained as its spatial fraction, preventing a map/grid jump, while
        /// the remaining journey follows the new acceleration profile.</summary>
        public static int ReschedulePendingFleetTransits(IList<FactionState> states, WorldModel world,
                                                          FactionTuning tuning, DateTime utcNow)
        {
            if (states == null || world == null || tuning == null) return 0;
            int changed = 0;
            foreach (FactionState state in states)
            {
                for (int i = 0; i < state.PendingFleets.Count; i++)
                {
                    PendingFleetAction fleet = state.PendingFleets[i];
                    if (fleet.Resolved || fleet.HasArrived(utcNow)) continue;
                    double routeKm = fleet.OriginPosKm.DistanceTo(fleet.TargetPosKm);
                    if (routeKm <= 0) continue;
                    double oldSpatialRatio = fleet.CompletionRatio(utcNow);
                    // The leg is re-scheduled on its own straight line, so its tier table is rebuilt
                    // over the same geometry -- a stale one describes a polyline this fleet is no
                    // longer flying, and the route fields are cleared just below for that reason.
                    fleet.LegZones = world.ZonesFor(fleet.OriginPosKm, fleet.TargetPosKm);
                    double elapsedMins = tuning.TransitElapsedMinsAtDistance(fleet.LegZones, routeKm * oldSpatialRatio);
                    double totalMins = tuning.TransitTimeMins(fleet.LegZones);
                    if (totalMins <= 0) continue;
                    fleet.DepartedUtc = utcNow.AddMinutes(-elapsedMins);
                    fleet.EstimatedArrivalUtc = fleet.DepartedUtc.AddMinutes(totalMins);
                    // This leg is now scheduled on its own profile, so a clearance chain's shared
                    // profile no longer describes it. Its remaining legs fall back to per-leg timing.
                    fleet.RouteDistanceKm = 0;
                    fleet.RouteCoveredKm = 0;
                    fleet.RouteDepartedUtc = default(DateTime);
                    state.PendingFleets[i] = fleet;
                    changed++;
                }
            }
            return changed;
        }

        /// <summary>The first remaining stop the convoy will actually fly to, skipping every pickup
        /// still queued for <paramref name="nodeId"/> — the node it is standing on. False when the
        /// itinerary holds nothing but same-node pickups, which makes the current target the last
        /// position this fleet occupies.</summary>
        private static bool TryPeekDepartingStop(PendingFleetAction fleet, string nodeId, out FleetStop stop)
        {
            stop = default(FleetStop);
            if (fleet.RemainingStops == null) return false;
            for (int i = 0; i < fleet.RemainingStops.Count; i++)
            {
                FleetStop candidate = fleet.RemainingStops[i];
                if (IsPickupAt(candidate, nodeId)) continue;
                stop = candidate;
                return true;
            }
            return false;
        }

        /// <summary>Takes the next stop as the current pickup when it is another collection at
        /// <paramref name="nodeId"/>, the node the convoy is already standing on. Merging them into
        /// one visit is what keeps a consolidated itinerary out of zero-distance legs.</summary>
        private static bool TakeNextPickupAtSameNode(ref PendingFleetAction fleet, string nodeId)
        {
            if (fleet.RemainingStops == null || fleet.RemainingStops.Count == 0) return false;
            FleetStop next = fleet.RemainingStops[0];
            if (!IsPickupAt(next, nodeId)) return false;
            fleet.RemainingStops.RemoveAt(0);
            fleet.PickupItemKey = next.PickupItemKey;
            fleet.PickupQuantity = next.PickupQuantity;
            return true;
        }

        private static bool IsPickupAt(FleetStop stop, string nodeId)
        {
            return stop.Kind == FleetStopKind.Pickup
                   && stop.NodeId != null
                   && string.Equals(stop.NodeId, nodeId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Build a <see cref="PendingFleetAction"/> for the given <paramref name="kind"/>.
        /// <para>Origin is the actor's node closest to the target.
        /// Target position is the node position (for Raid/Patrol) or the target faction's first
        /// reachable node (for Trade). Transit time uses <see cref="FactionTuning.TransitTimeMins"/> (trapezoidal speed profile).</para>
        /// <para>Pass <paramref name="immediate"/> = true (NullFleetSpawner fast-path) to set
        /// <c>EstimatedArrivalUtc = DepartedUtc</c> so <see cref="ResolveArrivals"/> fires this tick.</para>
        /// </summary>
        /// <param name="roster">The actor's fleet roster; the new fleet's id is minted unique within
        /// it (<see cref="PendingFleetAction.MintId(IList{PendingFleetAction}, string, DateTime, string, string)"/>).
        /// Null derives the id from the dispatch's inputs alone.</param>
        /// <param name="overrideOriginNodeId">When non-null, skip the closest-node derivation and
        /// use this node directly as the convoy origin. TradeManager passes the surplus-selected node.</param>
        /// <param name="oracle">When non-null, cargo items are sorted by scarcity at the target node
        /// (most scarce first) so the volume budget is filled with the highest-priority goods.</param>
        public static PendingFleetAction CreatePending(
            ActionKind kind, string actorTag, FactionIntent intent,
            WorldModel world, FactionTuning tuning, bool immediate,
            long cargoCapacityLiters = 0, IItemVolumeCatalogue itemVolumes = null,
            string overrideOriginNodeId = null, IPriceOracle oracle = null,
            IItemPriceCatalogue prices = null, ItemKind? preferredItemKind = null,
            IItemMassCatalogue itemMasses = null, string shipPrefabId = null,
            IList<PendingFleetAction> roster = null, List<string> squadron = null, int convoySize = 0)
        {
            // Derive target info
            string targetNodeId = intent.TargetNodeId;
            string targetTag = intent.TargetFactionTag;
            Vec3 targetPos = new Vec3();

            if (targetNodeId != null)
            {
                SpatialNode tn;
                if (world.Graph.Nodes.TryGetValue(targetNodeId, out tn))
                {
                    targetPos = tn.Position;
                    if (targetTag == null) targetTag = tn.OwnerFactionTag;
                }
            }
            else if (targetTag != null)
            {
                // Trade: find target faction's first node
                string bestNodeId = null;
                foreach (SpatialNode n in world.Graph.Nodes.Values)
                    if (n.OwnerFactionTag == targetTag) { bestNodeId = n.Id; targetPos = n.Position; break; }
                targetNodeId = bestNodeId;
            }

            // Derive origin info: use override when provided, otherwise pick closest actor node.
            string originNodeId = overrideOriginNodeId;
            Vec3 originPos = new Vec3();
            if (originNodeId != null)
            {
                SpatialNode on;
                if (world.Graph.Nodes.TryGetValue(originNodeId, out on))
                    originPos = on.Position;
            }
            double bestDist = double.MaxValue;
            if (originNodeId == null)
            foreach (SpatialNode n in world.Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != actorTag) continue;
                double d = n.Position.DistanceTo(targetPos);
                if (d < bestDist) { bestDist = d; originNodeId = n.Id; originPos = n.Position; }
            }

            // Graph path (BFS), carried only so mid-transit encounters can fire at the nodes the
            // convoy passes near. It does not price the leg.
            string[] path = null;
            if (!immediate && originNodeId != null && targetNodeId != null)
                path = GraphPathfinder.FindPath(world.Graph, originNodeId, targetNodeId);

            DateTime departed = world.UtcNow;
            DateTime arrival;
            if (immediate || tuning.FleetSpeedKmPerMin <= 0)
            {
                arrival = departed;  // CompletionRatio = 1.0 immediately → fires this tick
                path = null;         // immediate fleets don't traverse intermediate nodes
            }
            else
            {
                // Bill the line the convoy actually flies: the grid is commanded straight at
                // TargetPosKm and CurrentPosKm interpolates that same line, so pricing the leg off
                // anything longer parks the fleet on its destination for the rest of the leg. A
                // detour around a blocking body is re-billed by PlanFleetLeg below, against the
                // clearance polyline it generates.
                arrival = departed.AddMinutes(tuning.TransitTimeMins(originPos.DistanceTo(targetPos)));
            }

            long cargoValue = intent.Cost;  // fallback = TradeCost when no items are available

            var fleet = new PendingFleetAction
            {
                Kind = kind,
                ActorTag = actorTag,
                TargetTag = targetTag,
                OriginNodeId = originNodeId,
                ReturnNodeId = originNodeId,
                TargetNodeId = targetNodeId,
                OriginPosKm = originPos,
                TargetPosKm = targetPos,
                DepartedUtc = departed,
                EstimatedArrivalUtc = arrival,
                ImmediateItinerary = immediate,
                CargoValue = cargoValue,
                Cargo = null,
                CargoMassTrimmed = false,
                CargoCapacityLiters = cargoCapacityLiters,
                // The hulls are known before the leg is planned below, so the route is built for
                // what will fly it rather than for an unscanned hull on the orbit shell.
                ShipPrefabId = shipPrefabId,
                Squadron = squadron,
                ConvoySize = convoySize,
                MaterialisedEntityId = 0,
                // No grids yet, so no escorts and no casualties: both stay null until the host
                // materialises the fleet and, later, loses one of its hulls.
                EscortEntityIds = null,
                SurvivingHulls = null,
                Resolved = false,
                Intercepted = false,
                FleetId = PendingFleetAction.MintId(roster, actorTag, departed, targetNodeId ?? targetTag),
                CurrentStopKind = FleetStopKind.Service,
                RemainingStops = RequiresReturnLeg(kind) && originNodeId != null
                    ? new List<FleetStop>
                    {
                        new FleetStop { NodeId = originNodeId, Kind = FleetStopKind.Return }
                    }
                    : null,
                PathNodeIds = path,
                LastResolvedSegment = -1
            };

            // A combat fleet does not resolve on contact: it holds at its target for the engagement
            // dwell first, and the effect lands when that leg resolves. Queued ahead of the return
            // stop, so the itinerary reads travel -> engage -> home. Skipped for an immediate
            // dispatch (nothing about it is observable, and a dwell would only stall the sim's
            // fast path) and when the dwell is tuned off.
            if (EngagesOnArrival(kind) && !immediate && targetNodeId != null
                && tuning.RaidDwellSeconds > 0)
            {
                if (fleet.RemainingStops == null) fleet.RemainingStops = new List<FleetStop>();
                // A siege, not a blow: the dwells are queued up front so the itinerary itself bounds
                // the raid, and ResolveSingle drops whatever is left the moment holding longer stops
                // being worth it. Queued rather than appended at each resolution because the stop
                // list already persists and a cycle counter on the fleet would not.
                int cycles = tuning.RaidSiegeCycles > 1 ? tuning.RaidSiegeCycles : 1;
                for (int c = 0; c < cycles; c++)
                    fleet.RemainingStops.Insert(0, new FleetStop
                    {
                        NodeId = targetNodeId,
                        Kind = FleetStopKind.Engage,
                        DurationSeconds = tuning.RaidDwellSeconds
                    });
            }

            // Trade convoys pull cargo from the origin node's inventory (Phase 3 SubTypeId-keyed)
            // right away when the ship is already there — the ship-fetched-via-Pickup case (see
            // ResolveSingle's Pickup guard) defers this same load to when the Pickup leg resolves.
            if (kind == ActionKind.Trade && originNodeId != null)
            {
                SpatialNode originNode;
                if (world.Graph.Nodes.TryGetValue(originNodeId, out originNode))
                    LoadTradeCargo(originNode, ref fleet, targetNodeId, targetPos, world, tuning,
                        itemVolumes, itemMasses, oracle, prices, preferredItemKind);
            }

            return PlanFleetLeg(fleet, world, tuning, immediate);
        }

        // ---- tick resolution --------------------------------------------------------------------

        /// <summary>
        /// Called once per sim tick (by FactionRuntime.StepOnce or the sim convoy mode):
        /// <list type="number">
        /// <item>Collect arriving (unresolved) fleets and active patrols.</item>
        /// <item>Detect meetings at the same <c>TargetNodeId</c> — Patrol vs Raid, Raid vs Trade,
        ///       or all three — and apply combat/interception outcomes.</item>
        /// <item>Resolve each non-intercepted arriving fleet (apply effects).</item>
        /// <item>Clean up intercepted and fully-expired fleets from <c>PendingFleets</c>.</item>
        /// </list>
        /// All outcome messages are appended to <paramref name="log"/> (caller prefixes tick/context).
        /// </summary>
        /// <param name="onGridExpired">
        /// Optional callback invoked with a <c>MaterialisedEntityId</c> just before the owning
        /// <see cref="PendingFleetAction"/> is removed from <c>PendingFleets</c>. Use this to
        /// despawn the physical SE grid on the plugin side without coupling Core to SE APIs.
        /// Only called when <c>MaterialisedEntityId &gt; 0</c> (grid was actually spawned).
        /// </param>
        public static void ResolveArrivals(
            IList<FactionState> states, WorldModel world, FactionTuning tuning,
            IRandom rng, IList<string> log, IEconomyGateway economy = null,
            Action<long> onGridExpired = null, IItemPriceCatalogue prices = null,
            IItemVolumeCatalogue itemVolumes = null, IPriceOracle oracle = null,
            SpendBudget tradeInboundBudget = null, Action<string> onFleetRemoved = null,
            IItemMassCatalogue itemMasses = null)
        {
            DateTime now = world.UtcNow;

            // Before any dock, so a procurement standing at its Pickup finds its own hold counted.
            FuelMarket.RefreshHolds(states, world, tuning);

            // Before anything resolves, so an arrival is seen while its leg is still current.
            TrafficWatch.Scan(states, world, tuning, itemVolumes);

            // A convoy meets a raid at most once per pass. A fast fleet enters several waypoints
            // between two ticks and a convoy sharing its corridor is at every one of them; before
            // FAC-306 the first meeting deleted the convoy, so the later ones found nothing. Now that
            // a robbed convoy survives, the later meetings would rob it again -- live, one raid took
            // half, then half of the rest, then half again, in one second (FAC-306 acceptance read).
            var encountered = new HashSet<(int si, int fi)>();

            // An attack whose lead the grids took out has lost, before any meeting or arrival reads it.
            SettleLostLeads(states, world, tuning, log);

            // A node that changed hands under a fleet standing at it: an attack it made moot stops
            // dwelling, and a defence that is no longer the owner's goes home.
            SettleLostGround(states, world, tuning, log);

            // Which dwell fights the grids hold, before anything reads them.
            ObserveFights(states, world, tuning, rng, log);

            // Mid-transit encounters first: fleets crossing the same intermediate waypoint.
            ResolveMidTransitMeetings(states, world, tuning, rng, log, prices, encountered);

            // Enemy fleets in a faction's space: marked, laddered, and caught at Intercept.
            if (world.FleetTrespassOn)
                ResolveTrespassCatches(states, world, tuning, rng, log, prices, itemVolumes, encountered);

            // Collect arriving and active-patrol references
            var arriving = new List<(int si, int fi, ActionKind kind)>();
            var activePatrols = new List<(int si, int fi)>();

            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.Intercepted) continue;

                    // Guards standing their duty window, and Reinforces holding their dwell at the
                    // node they came to reinforce. One predicate for both, so a fleet that counts as
                    // a defender here counts as one in the mid-transit path too.
                    if (f.StandsAsDefender(now, tuning))
                        activePatrols.Add((si, fi));

                    if (f.HasArrived(now) && !f.Resolved)
                    {
                        // Defer while a physical grid exists OR a spawn is in flight (SpawningId).
                        // Without the SpawningId check, ResolveArrivals would abstract-resolve the
                        // fleet while the async spawn is still pending, orphaning the eventual grid.
                        //
                        // The engagement dwell is the one leg this must NOT defer. The defer exists
                        // so an abstract arrival cannot race a grid that is still flying; a dwelling
                        // raider is not flying anywhere -- sitting at the target IS the leg. Deferring
                        // it would stop the dwell clock for exactly as long as a player stands nearby,
                        // so the window would be invisible when unobserved and frozen when watched,
                        // which is the opposite of what it is for.
                        // A miner's dwell is the same: its grid is out on the ring, not flying the leg.
                        bool dwelling = f.CurrentStopKind == FleetStopKind.Engage
                                        || f.CurrentStopKind == FleetStopKind.Intercept
                                        || f.CurrentStopKind == FleetStopKind.Mine;
                        if (!dwelling
                            && (f.MaterialisedEntityId > 0
                                || f.MaterialisedEntityId == PendingFleetAction.SpawningId)) continue;
                        arriving.Add((si, fi, f.Kind));
                    }
                }
            }

            if (arriving.Count > 0 || activePatrols.Count > 0)
            {
                // Meeting resolution: for each arriving Raid, find patrols and trades at same node
                foreach (var raidRef in arriving)
                {
                    if (raidRef.kind != ActionKind.Raid && raidRef.kind != ActionKind.Retaliate && raidRef.kind != ActionKind.Conquer) continue;

                    FactionState raidState = states[raidRef.si];
                    PendingFleetAction raid = raidState.PendingFleets[raidRef.fi];
                    if (raid.Intercepted) continue;
                    if (raid.TargetNodeId == null) continue;
                    // Only the mission arrival is fought over. Without this, a raider arriving home
                    // on its Return leg was duelled by its OWN station -- the meeting loop selects on
                    // ActionKind alone and ResolveStationDefense reads the arriving node's weapons
                    // with no owner check. Live 2026-09-03: every combat fleet produced a second
                    // STATION_DEFENSE at its origin, and SPRT's own base ground [6B9213] to ratio
                    // 0.30 and destroyed it (`FLEET_RETURNED ... ship_lost`). It also overwrote the
                    // ratio the real fight produced, so FinalizeReturn scaled the hull and cargo
                    // recovery by a duel that should never have happened.
                    if (!IsMissionArrival(raid)) continue;
                    // Contact only: the engagement dwell is still ahead of this fleet, so the duel
                    // belongs to the end of that window, not to the moment it showed up.
                    if (IsPreEngagementContact(raid)) continue;
                    // Its grid is already gone. Duelling it would overwrite the ratio that loss
                    // wrote with the duel's own -- and against a node carrying neither station
                    // weapons nor a StationPrefabId the duel is unopposed, so the fleet would come
                    // out at ratio 1.0 and land at FULL effect.
                    if (IsOutcomeAlreadyDecided(raid)) continue;
                    // The node is no longer the victim's: nobody is left to repel this attack, and
                    // its resolution reads the new owner instead (TargetChangedHands).
                    if (TargetChangedHands(raid, world) != null) continue;

                    string raidNode = raid.TargetNodeId;

                    // Every active Guard at this node, from any faction other than the raider --
                    // they fight as one force. A node's defence is marshalled in waves because a
                    // fleet carries one origin, so taking only the first would leave every wave
                    // after it inert: hulls drawn, money spent, nothing added to the fight.
                    var defenderRefs = new List<(int si, int fi)>();
                    foreach (var pr in activePatrols)
                    {
                        PendingFleetAction p = states[pr.si].PendingFleets[pr.fi];
                        if (p.TargetNodeId == raidNode && states[pr.si].Tag != raidState.Tag
                            && !p.Intercepted)
                            defenderRefs.Add(pr);
                    }
                    (int si, int fi) patrolRef = defenderRefs.Count > 0 ? defenderRefs[0] : (-1, -1);

                    // Arriving trades and ferries at this node (from any faction other than the raider).
                    // Ferries carry no cargo but are still interceptable — a destroyed ferry means
                    // the ship never reaches its destination and the Position: demand re-fires.
                    var tradeRefs = new List<(int si, int fi)>();
                    foreach (var tr in arriving)
                    {
                        if (tr.kind != ActionKind.Trade && tr.kind != ActionKind.Ferry) continue;
                        // A bench raid fights the node's defence and nothing else: a real convoy
                        // it caught would be robbed of real cargo.
                        if (raid.IsBench) continue;
                        PendingFleetAction t = states[tr.si].PendingFleets[tr.fi];
                        if (t.TargetNodeId == raidNode && states[tr.si].Tag != raidState.Tag
                            && !t.Intercepted && t.CurrentStopKind != FleetStopKind.Intercept
                            && !AlreadyMet(t, raid))
                            tradeRefs.Add((tr.si, tr.fi));
                    }

                    bool hasPatrol = patrolRef.si >= 0;
                    bool hasTrade = tradeRefs.Count > 0;

                    var raidKey = (raidRef.si, raidRef.fi);
                    if (hasPatrol && hasTrade)
                        ResolveTripleEncounter(raidKey, patrolRef, tradeRefs, states, world, tuning, rng, log, null, prices, encountered, defenderRefs);
                    else if (hasPatrol)
                        ResolvePatrolVsRaid(raidKey, patrolRef, states, world, tuning, rng, log, null, defenderRefs);
                    else if (hasTrade)
                        ResolveRaidVsTrade(raidKey, tradeRefs, states, world, tuning, rng, log, prices: prices, encountered: encountered);
                    else
                        ResolveStationDefense(raidKey, states, world, tuning, rng, log);
                }

                // Individual resolution for all arriving fleets (skip intercepted ones)
                for (int i = 0; i < arriving.Count; i++)
                {
                    var ar = arriving[i];
                    PendingFleetAction f = states[ar.si].PendingFleets[ar.fi];
                    if (f.Resolved || f.Intercepted) continue;
                    // The meeting phase may have just re-pinned this fleet into an interception
                    // window; it is no longer arriving, and its leg resolves when that window ends.
                    if (!f.HasArrived(now)) continue;
                    ResolveSingle(ar.si, ar.fi, states, world, tuning, log, economy, prices, itemVolumes, oracle, tradeInboundBudget, itemMasses, rng);
                }
            }

            // After every meeting and arrival of this pass, so a raid resolved just now has already
            // stopped counting as aimed at the node a joined patrol is standing on.
            ResumeSuspendedCircuits(states, world, tuning, log);

            // A bench scenario whose attack has ended is written up, and retired as a whole once its
            // grids have left, so the cleanup below removes every fleet of it and closes what is left.
            BenchScenario.Conclude(states, now, tuning, log);

            // Cleanup: always runs — intercepted fleets need removal even when nothing is arriving.
            // Notify onGridExpired before removal so the SE-side spawner can despawn the physical grid.
            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                for (int fi = st.PendingFleets.Count - 1; fi >= 0; fi--)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    bool expired = f.Kind == ActionKind.Guard
                        ? (f.Resolved && (now >= f.EstimatedArrivalUtc.Add(tuning.GuardDuration)
                                          || TrespassLadder.GuardDutyOver(f, world)))
                        : f.Resolved;
                    if (expired && f.Kind == ActionKind.Guard)
                        ReturnGuardShip(f, world, log);
                    if (f.Intercepted || expired)
                    {
                        // Lead first, then every escort: the host's despawn is per grid, and an
                        // escort left behind here has nothing pointing at it once the fleet record
                        // is gone.
                        if (onGridExpired != null)
                        {
                            if (f.MaterialisedEntityId > 0) onGridExpired(f.MaterialisedEntityId);
                            if (f.EscortEntityIds != null)
                                for (int ei = 0; ei < f.EscortEntityIds.Count; ei++)
                                    if (f.EscortEntityIds[ei] > 0) onGridExpired(f.EscortEntityIds[ei]);
                        }
                        // Fired for every fleet leaving PendingFleets, win or lose, regardless of
                        // kind — the single convergence point for "this fleet is done" that a
                        // Guard-via-PvEBounty-style feature can check its own tracking against
                        // (e.g. an accepted bounty whose target wasn't destroyed by a player should
                        // fail here, since every non-player-destruction outcome for a Raid/Retaliate/
                        // Conquer fleet passes through this exact removal).
                        onFleetRemoved?.Invoke(f.FleetId);
                        st.PendingFleets.RemoveAt(fi);
                    }
                }
            }

            // After the cleanup, so a pickup made, a convoy lost and a fleet retired this pass have
            // each released their hold before any manager reads it.
            FuelMarket.RefreshHolds(states, world, tuning);
        }

        // ---- PvEBounty target lookup (Guard→PvEBounty contract pattern) --------------------------

        /// <summary>Finds an unresolved, unintercepted Raid/Retaliate/Conquer fleet targeting
        /// <paramref name="nodeId"/> — the candidate a station's contract-generation path should
        /// offer a bounty against. This is a pure, side-effect-free lookup: finding (or even
        /// offering) a bounty must never itself change anything about the fleet — see
        /// docs/contracts.md's Guard-via-PvEBounty non-goals. Fleets without a <see
        /// cref="PendingFleetAction.FleetId"/> (pre-FleetId legacy docs) are skipped since a torch-host
        /// accepted-bounty tracker keyed by FleetId couldn't re-find them later.</summary>
        /// <summary>The node a fleet is ultimately headed for, seen through any planet-clearance
        /// detour it is currently flying.
        ///
        /// <para><see cref="PendingFleetResolver.PlanFleetLeg"/> nulls <c>TargetNodeId</c> while a
        /// fleet is on a coordinate-only <see cref="FleetStopKind.Navigation"/> leg and requeues the
        /// real destination behind it, so a raid on a detour reads as having no target at all.
        /// Measured on awl-standalone-test 2026-09-02/03: 22 of 61 combat dispatches were on a
        /// clearance leg at dispatch, and for one traced end to end the real target was only visible
        /// for ten seconds before the raid resolved -- against a manager that ticks once a minute.
        /// A defender keying on <c>TargetNodeId</c> alone is blind to better than a third of raids,
        /// and blind to precisely the long-range ones, since needing a detour implies distance.</para>
        ///
        /// <para>Returns null only when nothing in the itinerary names a node.</para>
        ///
        /// <para>Public because two callers need the same answer: <c>FleetManager</c>'s inbound-threat
        /// index, deciding who to send, and <see cref="HasIncomingRaid"/>, deciding whether a patrol
        /// already standing on the ground should stay.</para></summary>
        public static string SemanticTargetOf(PendingFleetAction fleet)
        {
            if (fleet.CurrentStopKind != FleetStopKind.Navigation && fleet.TargetNodeId != null)
                return fleet.TargetNodeId;
            if (fleet.RemainingStops != null)
                for (int i = 0; i < fleet.RemainingStops.Count; i++)
                {
                    FleetStop stop = fleet.RemainingStops[i];
                    if (stop.Kind != FleetStopKind.Navigation && stop.NodeId != null) return stop.NodeId;
                }
            return fleet.TargetNodeId;
        }

        /// <summary>Whether any unresolved Raid/Retaliate/Conquer is aimed at <paramref name="nodeId"/>.
        ///
        /// <para>Deliberately not <see cref="TryFindIncomingThreat"/>, which skips a fleet carrying no
        /// <c>FleetId</c> because its caller needs a candidate a tracker can re-find later. A defender
        /// deciding whether to stay does not care whether the attacker can be tracked by id — a legacy
        /// fleet still arrives — so this one counts it.</para>
        ///
        /// <para>A combat fleet keeps its <c>Kind</c> on the way home, so a raider flying its Return
        /// leg would otherwise read as an inbound raid against its own node. Excluded here for the same
        /// reason <c>FleetManager</c>'s index excludes it.</para></summary>
        public static bool HasIncomingRaid(IList<FactionState> states, WorldModel world, string nodeId)
        {
            if (states == null || world == null || nodeId == null) return false;
            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                if (st == null) continue;
                List<PendingFleetAction> fleets = st.PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (f.Kind != ActionKind.Raid && f.Kind != ActionKind.Retaliate && f.Kind != ActionKind.Conquer)
                        continue;
                    if (f.Intercepted || f.Resolved) continue;
                    if (!BenchScenario.SeenAsThreat(f)) continue;
                    string target = SemanticTargetOf(f);
                    if (target == null) continue;
                    if (!string.Equals(target, nodeId, StringComparison.OrdinalIgnoreCase)) continue;
                    SpatialNode threatened;
                    if (world.Graph.Nodes.TryGetValue(target, out threatened)
                        && threatened.OwnerFactionTag == f.ActorTag) continue;
                    return true;
                }
            }
            return false;
        }

        public static bool TryFindIncomingThreat(IList<FactionState> states, string nodeId, out PendingFleetAction action)
        {
            for (int si = 0; si < states.Count; si++)
            {
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (f.Kind != ActionKind.Raid && f.Kind != ActionKind.Retaliate && f.Kind != ActionKind.Conquer) continue;
                    if (f.Intercepted || f.Resolved) continue;
                    if (!BenchScenario.SeenAsThreat(f)) continue;
                    if (string.IsNullOrEmpty(f.FleetId)) continue;
                    if (!string.Equals(f.TargetNodeId, nodeId, StringComparison.OrdinalIgnoreCase)) continue;
                    action = f;
                    return true;
                }
            }
            action = default(PendingFleetAction);
            return false;
        }

        /// <summary>
        /// Load-time reconciliation: any fleet whose <c>EstimatedArrivalUtc</c> is in the past
        /// (the server was offline during transit) is resolved at full ratio (player-friendly
        /// assumption: uncontested while offline). Call once after <c>Load()</c>.
        /// </summary>
        public static void ReconcileAndResolveOnLoad(
            IList<FactionState> states, WorldModel world, FactionTuning tuning,
            IRandom rng, IList<string> log, IEconomyGateway economy = null,
            IItemPriceCatalogue prices = null, IItemVolumeCatalogue itemVolumes = null,
            IPriceOracle oracle = null, SpendBudget tradeInboundBudget = null,
            Action<string> onFleetRemoved = null, IItemMassCatalogue itemMasses = null)
        {
            DateTime now = world.UtcNow;
            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (!f.Resolved && !f.Intercepted)
                    {
                        bool overdueArrival  = f.EstimatedArrivalUtc <= now;
                        // DepartedUtc in the future means the timestamp came from a sim-clock build
                        // (the old 6× clock wrote future-relative timestamps). Such a fleet has no
                        // valid position; force-resolve it so it doesn't freeze at origin on spawn.
                        bool futureTimestamp = f.DepartedUtc > now;
                        // Stored transit time far exceeds what the current speed would produce: the
                        // fleet was dispatched under an old (much slower) speed setting. Force-resolve
                        // so it doesn't linger near origin for days. Threshold = 50× expected so a
                        // legitimate slow route never triggers this.
                        bool staleSpeed = false;
                        if (!futureTimestamp && !overdueArrival && tuning.FleetSpeedKmPerMin > 0)
                        {
                            double distKm       = f.OriginPosKm.DistanceTo(f.TargetPosKm);
                            double expectedMins = tuning.TransitTimeMins(distKm);
                            double storedMins   = (f.EstimatedArrivalUtc - f.DepartedUtc).TotalMinutes;
                            staleSpeed = expectedMins > 0 && storedMins > expectedMins * 50;
                        }

                        if (overdueArrival || futureTimestamp || staleSpeed)
                        {
                            if (futureTimestamp || staleSpeed)
                                log.Add((futureTimestamp ? "future_timestamp" : "stale_speed")
                                        + " " + f.ActorTag + " kind=" + f.Kind
                                        + " dep=" + f.DepartedUtc.ToString("o") + " — migration, force-resolving");
                            // Set both timestamps to now so HasArrived(now) fires immediately and
                            // CompletionRatio returns 1.0 (player-friendly offline assumption).
                            // For future-timestamp entries, setting EstimatedArrivalUtc=DepartedUtc
                            // would leave arrival still in the future — this pin to now fixes that.
                            var updated = f;
                            updated.DepartedUtc = now;
                            updated.EstimatedArrivalUtc = now;
                            // Carries through the whole itinerary, not just this leg: the point of
                            // the pin is that nothing about these timestamps is usable any more.
                            updated.ImmediateItinerary = true;
                            st.PendingFleets[fi] = updated;
                        }
                    }
                    // Fast-forward LastResolvedSegment so segments transited while offline are not
                    // retroactively triggered as mid-transit encounters.
                    if (f.PathNodeIds != null && f.PathNodeIds.Length >= 2)
                    {
                        var fRef = st.PendingFleets[fi];
                        fRef.LastResolvedSegment = fRef.CurrentSegmentIdx(now, world.Graph);
                        st.PendingFleets[fi] = fRef;
                    }
                }
            }
            ResolveArrivals(states, world, tuning, rng, log, economy, null, prices, itemVolumes, oracle, tradeInboundBudget, onFleetRemoved, itemMasses);
        }

        // ---- ship duel (turn-based, range-gated) -------------------------------------------------

        private static double TotalDps(List<WeaponBand> bands)
        {
            if (bands == null) return 0;
            double sum = 0;
            for (int i = 0; i < bands.Count; i++) sum += bands[i].Dps;
            return sum;
        }

        private static List<WeaponBand> MergeBands(List<WeaponBand> a, List<WeaponBand> b)
        {
            var merged = new List<WeaponBand>();
            if (a != null) merged.AddRange(a);
            if (b != null) merged.AddRange(b);
            return merged;
        }

        /// <summary>The duel (<see cref="ShipDuel.Fight"/>) over a contested range schedule, with each
        /// side's stance read for the fight at <paramref name="node"/>.
        /// <paramref name="defenderFleets"/> is the defending force (null or empty for a station
        /// alone) and <paramref name="defenderTag"/> the faction it fights for.
        ///
        /// <para>The result carries how the duel was fought (<see cref="DuelTactic"/>),
        /// also stashed on the attacking fleet for <see cref="FightLedger"/>. With
        /// <see cref="WorldModel.TacticLearning"/> (or a bench attacker), each side's stance is its
        /// learned pick and both sides learn from the outcome (<see cref="LearnFromDuel"/>).</para></summary>
        private static ShipDuel.Result ResolveShipDuel(
            List<WeaponBand> attackerBands, List<WeaponBand> defenderBands, long defenderFlatPower,
            FactionTuning tuning, IRandom rng, WorldModel world, IList<FactionState> states,
            (int si, int fi) attackerRef, IList<(int si, int fi)> defenderFleets, string defenderTag, SpatialNode node,
            IList<string> log, DuelMunitions attackerMunitions = null, DuelMunitions defenderMunitions = null)
        {
            DuelSides sides = DuelStances(world, states, attackerRef, defenderFleets, defenderTag, node,
                                          attackerBands, defenderBands, defenderFlatPower, tuning);
            DuelStance attacker = sides.Attacker, defender = sides.Defender;
            if (sides.Learning) LogPicks(sides, log);
            ShipDuel.Result duel = ShipDuel.Fight(attackerBands, defenderBands, defenderFlatPower, tuning, rng,
                                  attackerMunitions, defenderMunitions, true, attacker, defender);

            duel.Tactic = new DuelTactic
            {
                AttackerArm = attacker.Arm, AttackerBreakOff = attacker.BreakOff,
                DefenderArm = defender.Arm, DefenderBreakOff = defender.BreakOff,
                BracketsM = duel.BracketsFought,
            };
            PendingFleetAction fought = states[attackerRef.si].PendingFleets[attackerRef.fi];
            fought.DuelTactic = duel.Tactic;
            states[attackerRef.si].PendingFleets[attackerRef.fi] = fought;
            if (sides.Records)
                LearnFromDuel(duel, sides, attackerBands, defenderBands, defenderFlatPower, tuning, world, log);
            return duel;
        }

        /// <summary>Each side's learned pick, as <c>TACTIC_CHOSEN</c> behind <c>factions debug tactics</c>.</summary>
        private static void LogPicks(DuelSides sides, IList<string> log)
        {
            if (!DebugTraces.Tactics || log == null) return;
            if (sides.AttackerState != null)
                log.Add(TacticMemory.FormatChosen(sides.AttackerState.Tag, sides.AttackerFleet,
                                                  sides.DefenderTag, sides.AttackerPick));
            if (sides.DefenderState != null)
                log.Add(TacticMemory.FormatChosen(sides.DefenderState.Tag, sides.DefenderFleet,
                                                  sides.AttackerTag, sides.DefenderPick));
        }

        /// <summary>Both sides of a contested duel as the tactic layer sees them.</summary>
        private sealed class DuelSides
        {
            /// <summary>The bands each side's stance and context were read from.</summary>
            public List<WeaponBand> AttackerBands, DefenderBands;
            public DuelStance Attacker, Defender;
            /// <summary>Stances are learned picks: <see cref="WorldModel.TacticLearning"/>, or a bench
            /// scenario's attacker, which forces it on.</summary>
            public bool Learning;
            /// <summary>Both sides learn from the outcome: learning, and — for a bench fleet — only
            /// when the scenario files its fights (<see cref="BenchScenario.FilesFight"/>), since the
            /// memory it writes is the real factions'.</summary>
            public bool Records;
            public FactionState AttackerState, DefenderState;
            public string AttackerTag, DefenderTag;
            public string AttackerFleet, DefenderFleet;
            public TacticContext AttackerContext, DefenderContext;
            public TacticPick AttackerPick, DefenderPick;
            public int AttackerHulls, DefenderHulls;
            /// <summary>A side a wing commander fought: its stance is the commanded one and its
            /// outcome is learned as a demonstration (<see cref="PendingFleetAction.Commanded"/>).</summary>
            public bool AttackerCommanded, DefenderCommanded;
        }

        /// <summary>Both sides' stances for a duel at <paramref name="node"/>: each faction's
        /// <see cref="TacticChoice"/> at the combat acceleration of the hulls it brings, against the
        /// other side's faction, in the context the two forces make.</summary>
        private static DuelSides DuelStances(WorldModel world, IList<FactionState> states,
            (int si, int fi) attackerRef, IList<(int si, int fi)> defenderFleets, string defenderTag, SpatialNode node,
            List<WeaponBand> attackerBands, List<WeaponBand> defenderBands, long defenderFlat, FactionTuning tuning)
        {
            double density; bool air;
            CombatMobility.EnvironmentAt(world.Planets, node, out density, out air);
            IPrefabCatalogue cat = world.PrefabCatalogue;
            FactionState atkState = states[attackerRef.si];
            PendingFleetAction atkFleet = atkState.PendingFleets[attackerRef.fi];
            double atkMobility = CombatMobility.OfFleet(atkFleet, cat, density, air);

            var hulls = new List<string>();
            bool hasShips = defenderFleets != null && defenderFleets.Count > 0;
            int defenderHulls = 0;
            if (hasShips)
                for (int i = 0; i < defenderFleets.Count; i++)
                {
                    PendingFleetAction d = states[defenderFleets[i].si].PendingFleets[defenderFleets[i].fi];
                    hulls.AddRange(FleetMunitions.Hulls(d));
                    defenderHulls += d.HullCount;
                }
            FactionState defState = hasShips ? states[defenderFleets[0].si] : FindState(states, defenderTag);
            double defMobility = CombatMobility.OfDefence(cat, hulls, hasShips, density, air);

            var sides = new DuelSides
            {
                AttackerBands = attackerBands,
                DefenderBands = defenderBands,
                Learning = world.TacticLearning || atkFleet.IsBench,
                Records = (world.TacticLearning || atkFleet.IsBench) && BenchScenario.FilesFight(atkFleet),
                AttackerState = atkState,
                DefenderState = defState != null && defState.Tag != atkState.Tag ? defState : null,
                AttackerTag = atkState.Tag,
                DefenderTag = defState != null ? defState.Tag : defenderTag,
                AttackerFleet = atkFleet.LogId,
                DefenderFleet = hasShips ? states[defenderFleets[0].si].PendingFleets[defenderFleets[0].fi].LogId : null,
                AttackerHulls = atkFleet.HullCount,
                DefenderHulls = defenderHulls,
                AttackerContext = TacticContext.Of(attackerBands, 0, defenderBands, defenderFlat, atkMobility, defMobility, tuning),
                DefenderContext = TacticContext.Of(defenderBands, defenderFlat, attackerBands, 0, defMobility, atkMobility, tuning),
            };
            sides.Attacker = TacticChoice.For(atkState, atkMobility, sides.DefenderTag, sides.AttackerContext,
                                              sides.Learning, world.UtcNow, out sides.AttackerPick);
            sides.Defender = TacticChoice.For(defState, defMobility, sides.AttackerTag, sides.DefenderContext,
                                              sides.Learning, world.UtcNow, out sides.DefenderPick);
            if (atkFleet.Commanded.HasValue)
            {
                sides.Attacker = atkFleet.Commanded.Value.ApplyTo(sides.Attacker);
                sides.AttackerCommanded = true;
            }
            CommandedTactic? defCommanded = hasShips
                ? states[defenderFleets[0].si].PendingFleets[defenderFleets[0].fi].Commanded
                : null;
            if (defCommanded.HasValue)
            {
                sides.Defender = defCommanded.Value.ApplyTo(sides.Defender);
                sides.DefenderCommanded = true;
            }
            return sides;
        }

        /// <summary>
        /// Both sides of an abstract duel learn from it (<see cref="TacticMemory.Record"/>): the
        /// attacker against the defending faction and the defending faction — a station's owner when
        /// no ships defended — against the attacker, each in its own context.
        ///
        /// <para>Damage dealt to a side is its capacity pool less what survived. The prediction is
        /// <see cref="CombatStrength.ScheduleDamage"/> over the schedule the duel planned — the commit
        /// gates' own arithmetic for the forces that actually met — each side's figure capped at the
        /// other's pool. Hulls lost are the abstract duel's reading: a destroyed side loses every
        /// hull, any other the whole hulls inside its lost capacity. A physical fight measures all
        /// three instead.</para>
        /// </summary>
        private static void LearnFromDuel(ShipDuel.Result duel, DuelSides sides,
            List<WeaponBand> attackerBands, List<WeaponBand> defenderBands, long defenderFlat,
            FactionTuning tuning, WorldModel world, IList<string> log)
        {
            double atkDealt = duel.DefenderCapacity * (1.0 - duel.DefenderRatio);
            double defDealt = duel.AttackerCapacity * (1.0 - duel.AttackerRatio);
            double atkPredicted = Math.Min(duel.DefenderCapacity, CombatStrength.ScheduleDamage(attackerBands, 0, duel.Plan, tuning));
            double defPredicted = Math.Min(duel.AttackerCapacity, CombatStrength.ScheduleDamage(defenderBands, defenderFlat, duel.Plan, tuning));

            if (sides.AttackerState != null)
            {
                TacticReview r = sides.AttackerState.TacticMemory.Record(new TacticOutcome
                {
                    OpponentTag = sides.DefenderTag,
                    Context = sides.AttackerContext.Key,
                    Arm = sides.Attacker.Arm,
                    BreakOff = sides.Attacker.BreakOff,
                    DamageDealt = atkDealt,
                    DamageTaken = defDealt,
                    PredictedDealt = atkPredicted,
                    PredictedTaken = defPredicted,
                    HullsBrought = sides.AttackerHulls,
                    HullsLost = DuelHullsLost(sides.AttackerHulls, duel.AttackerRatio, duel.AttackerDestroyed),
                    Commanded = sides.AttackerCommanded,
                }, sides.AttackerState.PersonalityId, world.UtcNow);
                if (r.Recorded && DebugTraces.Tactics && log != null)
                    log.Add(TacticMemory.FormatReview(sides.AttackerState.Tag, r));
            }
            if (sides.DefenderState != null)
            {
                TacticReview r = sides.DefenderState.TacticMemory.Record(new TacticOutcome
                {
                    OpponentTag = sides.AttackerTag,
                    Context = sides.DefenderContext.Key,
                    Arm = sides.Defender.Arm,
                    BreakOff = sides.Defender.BreakOff,
                    DamageDealt = defDealt,
                    DamageTaken = atkDealt,
                    PredictedDealt = defPredicted,
                    PredictedTaken = atkPredicted,
                    HullsBrought = sides.DefenderHulls,
                    HullsLost = DuelHullsLost(sides.DefenderHulls, duel.DefenderRatio, duel.DefenderDestroyed),
                    Commanded = sides.DefenderCommanded,
                }, sides.DefenderState.PersonalityId, world.UtcNow);
                if (r.Recorded && DebugTraces.Tactics && log != null)
                    log.Add(TacticMemory.FormatReview(sides.DefenderState.Tag, r));
            }
        }

        /// <summary>The abstract duel's hull loss: every hull of a destroyed side, otherwise the whole
        /// hulls inside the capacity it lost.</summary>
        internal static int DuelHullsLost(int hulls, double ratio, bool destroyed)
        {
            if (hulls <= 0) return 0;
            if (destroyed) return hulls;
            return Math.Max(0, Math.Min(hulls, (int)Math.Floor((1.0 - ratio) * hulls + 1e-9)));
        }

        // ---- combat outcome ---------------------------------------------------------------------

        /// <summary>Hulls a fleet still has standing: the surviving squadron members, or the convoy
        /// count for any other fleet.</summary>
        private static int HullsStanding(PendingFleetAction fleet)
        {
            return fleet.Squadron != null && fleet.Squadron.Count > 0 ? fleet.HullsToReturn.Count : Math.Max(1, fleet.ConvoySize);
        }

        /// <summary>The hulls each side brought into a meeting, read after its duel and before its
        /// outcome is applied: what the grids counted when the fight began when they took part
        /// (<see cref="FightProgress"/>), otherwise what each side has standing now. Zero defending
        /// hulls for a station alone.</summary>
        private static void HullsBrought(IList<FactionState> states, (int si, int fi) raidRef,
            IList<(int si, int fi)> defence, out int attacker, out int defender)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            attacker = HullsStanding(raid);
            defender = 0;
            if (defence != null)
                for (int i = 0; i < defence.Count; i++)
                    defender += HullsStanding(states[defence[i].si].PendingFleets[defence[i].fi]);
            if (raid.Fight != null)
            {
                if (raid.Fight.AttackerHulls > 0) attacker = raid.Fight.AttackerHulls;
                if (raid.Fight.DefenderHulls > 0 && defence != null) defender = raid.Fight.DefenderHulls;
            }
        }

        /// <summary>Writes one <see cref="CombatOutcome"/> for a meeting whose outcome has just been
        /// applied: logs it (<c>COMBAT_OUTCOME</c>) and queues it for the host
        /// (<see cref="WorldModel.AddCombatOutcome"/>). <paramref name="breakLost"/> is the hulls a
        /// broken attack wrote off (<see cref="BreakAttack"/>, <see cref="BreakOffAttack"/>), which
        /// a fleet that is not a squadron does not carry on its record.</summary>
        private static void RecordCombatOutcome(IList<FactionState> states, WorldModel world, IList<string> log,
            (int si, int fi) raidRef, IList<(int si, int fi)> defence, SpatialNode targetNode, string node,
            CombatMeeting meeting, CombatAttackResult attack, int breakLost,
            bool defenderDestroyed, bool defenderBrokeOff, string source,
            int attackerBrought, int defenderBrought, string defenderTag, string defenderFleet)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            bool squadron = raid.Squadron != null && raid.Squadron.Count > 0;
            int atkLost = Math.Max(0, attackerBrought - HullsStanding(raid)) + (squadron ? 0 : breakLost);

            var o = new CombatOutcome
            {
                Utc = world.UtcNow,
                Kind = raid.Kind,
                Meeting = meeting,
                AttackerTag = raid.ActorTag,
                AttackerFleet = raid.LogId,
                DefenderTag = defenderTag,
                DefenderFleet = defenderFleet,
                NodeId = node,
                PositionKm = targetNode != null ? targetNode.Position : raid.TargetPosKm,
                Attack = attack,
                Defence = defenderDestroyed ? CombatDefenceResult.Destroyed
                        : defenderBrokeOff ? CombatDefenceResult.BrokeOff : CombatDefenceResult.Stood,
                AttackerHulls = attackerBrought,
                AttackerHullsLost = Math.Min(atkLost, attackerBrought),
                DefenderHulls = defenderBrought,
                Source = source,
                BenchLabel = raid.BenchLabel,
            };
            int defLost = 0;
            if (defence != null)
            {
                int standing = 0;
                for (int i = 0; i < defence.Count; i++)
                {
                    PendingFleetAction d = states[defence[i].si].PendingFleets[defence[i].fi];
                    standing += HullsStanding(d);
                    AddTag(o.DefenderTags, d.ActorTag);
                }
                defLost = defenderDestroyed ? defenderBrought : Math.Max(0, defenderBrought - standing);
            }
            AddTag(o.DefenderTags, defenderTag);
            if (targetNode != null) AddTag(o.DefenderTags, targetNode.OwnerFactionTag);
            o.DefenderHullsLost = Math.Min(defLost, defenderBrought);

            if (log != null) log.Add(o.LogLine());
            world.AddCombatOutcome(o);
        }

        /// <summary>Records how an attack's meeting stopped it (<see cref="PendingFleetAction.StoppedHow"/>).</summary>
        private static void MarkStopped(IList<FactionState> states, (int si, int fi) raidRef, string how,
                                        double ratio, string source)
        {
            PendingFleetAction f = states[raidRef.si].PendingFleets[raidRef.fi];
            f.StoppedHow = how;
            f.StoppedRatio = ratio;
            f.StoppedSource = source;
            states[raidRef.si].PendingFleets[raidRef.fi] = f;
        }

        private static void AddTag(List<string> tags, string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            for (int i = 0; i < tags.Count; i++)
                if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) return;
            tags.Add(tag);
        }

        // ---- meeting resolution -----------------------------------------------------------------

        /// <summary>Guard fleet (plus the target node's own station defense, if any) duels an
        /// incoming Raid/Retaliate/Conquer at the same node — see <see cref="ResolveShipDuel"/>.
        /// A fully-depleted raid is flagged as intercepted; a raid that breaks through but was
        /// weakened carries its surviving <see cref="PendingFleetAction.ForcedRatio"/> into
        /// <see cref="ResolveSingle"/> instead of applying full effect.
        /// <paramref name="meetNode"/> overrides the log node label for mid-transit encounters.</summary>
        private static void ResolvePatrolVsRaid(
            (int si, int fi) raidRef, (int si, int fi) patrolRef,
            IList<FactionState> states, WorldModel world, FactionTuning tuning, IRandom rng, IList<string> log,
            string meetNode = null, List<(int si, int fi)> force = null)
        {
            PendingFleetAction raid   = states[raidRef.si].PendingFleets[raidRef.fi];
            PendingFleetAction patrol = states[patrolRef.si].PendingFleets[patrolRef.fi];
            string node = meetNode ?? raid.TargetNodeId;

            SpatialNode targetNode;
            world.Graph.Nodes.TryGetValue(node, out targetNode);
            List<WeaponBand> defenderBands = MergeBands(ForceBands(states, patrolRef, force),
                                                        targetNode != null ? targetNode.StationWeapons : null);
            long defenderFlat = CombatStrength.StationFlat(targetNode, tuning);

            DuelMunitions raidMunitions = RaidMunitions(raidRef, states, world, tuning);
            DuelMunitions forceMunitions = ForceMunitions(states, patrolRef, force,
                targetNode != null ? targetNode.StationWeapons : null, world, tuning);
            ShipDuel.Result duel = ResolveMeeting(raid.WeaponBands, defenderBands, defenderFlat, tuning, rng,
                world, states, raidRef, ForceRefs(patrolRef, force), patrol.ActorTag, targetNode,
                meetNode, log, raidMunitions, forceMunitions);
            raid = states[raidRef.si].PendingFleets[raidRef.fi];
            double attackerRatio = duel.AttackerRatio;
            bool attackerDestroyed = duel.AttackerDestroyed, defenderDestroyed = duel.DefenderDestroyed;
            string turnSummary = duel.TurnSummary;
            CommitMunitions(raidMunitions, forceMunitions, states, world, log);
            long opposedDps = (long)(TotalDps(defenderBands) + defenderFlat);
            List<(int si, int fi)> defence = ForceRefs(patrolRef, force);
            int atkBrought, defBrought;
            HullsBrought(states, raidRef, defence, out atkBrought, out defBrought);
            CombatAttackResult attack;
            int breakLost = 0;

            if (duel.AttackerRepelled)
            {
                attack = CombatAttackResult.Repelled;
                RepelAttack(raidRef, raid, patrol.ActorTag, node, opposedDps, attackerRatio, duel.AttackerAttrition, duel.Source, defenderBands, defenderFlat,
                            states, world, tuning, log);
                log.Add("RAID_REPELLED " + raid.LogId + " " + raid.ActorTag + " vs " + patrol.LogId + " " + patrol.ActorTag
                        + " @" + node + " ratio=" + attackerRatio.ToString("0.00", CultureInfo.InvariantCulture)
                        + " outcome=repelled src=" + duel.Source + " (" + turnSummary + ")");
                CreditPactDefence(raid, patrol, states, world, tuning, log);
            }
            else if (!attackerDestroyed && duel.AttackerBrokeOff)
            {
                int total, lost;
                BreakOffAttack(raidRef, patrol.ActorTag, node, opposedDps, duel.AttackerAttrition, states, world, log,
                               out total, out lost);
                attack = CombatAttackResult.BrokeOff;
                breakLost = lost;
                MarkStopped(states, raidRef, "broke_off", attackerRatio, duel.Source);
                RecordAttackStopped(raidRef, raid, patrol.ActorTag, node, defenderBands, defenderFlat, states, world, tuning,
                                    lost, total);
                log.Add("RAID_BROKE_OFF " + raid.LogId + " " + raid.ActorTag + " vs " + patrol.LogId + " " + patrol.ActorTag
                        + " @" + node + " ratio=" + attackerRatio.ToString("0.00", CultureInfo.InvariantCulture)
                        + " hulls_lost=" + lost + "/" + total + " src=" + duel.Source + " (" + turnSummary + ")");
                CreditPactDefence(raid, patrol, states, world, tuning, log);
            }
            else if (attackerDestroyed)
            {
                // The defence wins: the raid is broken off without effect, and each of its hulls
                // takes its own chance of dying here rather than the whole squadron vanishing.
                int total, lost;
                BreakAttack(raidRef, patrol.ActorTag, node, opposedDps,
                            HullRoster(raid, world.PrefabCatalogue),
                            raid.Squadron != null && raid.Squadron.Count > 0,
                            states, world, tuning, rng, log, out total, out lost);
                attack = CombatAttackResult.Destroyed;
                breakLost = lost;
                MarkStopped(states, raidRef, "beaten", 0.0, duel.Source);
                PendingFleetAction intercepted = states[raidRef.si].PendingFleets[raidRef.fi];
                if (!raid.IsBench) RecordRaidRepelled(world, raid, patrol.ActorTag, node, lost, total);
                RecordFight(states, intercepted, patrol.ActorTag, 0, true, world, tuning);
                RecordRepelledAttack(intercepted, world);
                RecordCampaignOutcome(states, raidRef, CampaignOutcome.Intercepted,
                    CombatStrength.WorstBracket(raid.WeaponBands, defenderBands, null, defenderFlat, tuning), world);
                log.Add("GUARD_INTERCEPT " + patrol.LogId + " " + patrol.ActorTag + " defeated " + raid.LogId + " " + raid.ActorTag
                        + " @" + node + " hulls_lost=" + lost + "/" + total + " src=" + duel.Source + " (" + turnSummary + ")");
                CreditPactDefence(raid, patrol, states, world, tuning, log);
            }
            else
            {
                // Raid breaks through, carrying whatever fraction of its power survived the duel.
                attack = CombatAttackResult.Landed;
                var survivor = raid;
                survivor.ForcedRatio = attackerRatio;
                survivor.ForcedRatioCause = "guard_duel";
                survivor.OpposedDps = opposedDps;
                survivor.DuelTactic = duel.Tactic;
                SettleDuelHulls(ref survivor, attackerRatio, duel.AttackerAttrition, duel.Source, "guard_duel", world, log);
                states[raidRef.si].PendingFleets[raidRef.fi] = survivor;
                log.Add("RAID_BREAKS_GUARD " + raid.LogId + " " + raid.ActorTag + " ratio=" + attackerRatio.ToString("0.00", CultureInfo.InvariantCulture)
                        + " vs " + patrol.LogId + " " + patrol.ActorTag + " @" + node + " src=" + duel.Source + " (" + turnSummary + ")");
            }

            if (defenderDestroyed)
                MarkForceDestroyed(states, patrolRef, force, raid.ActorTag, node, world, log);
            else
            {
                RecordForceAttrition(states, patrolRef, force, duel.DefenderAttrition, "guard_duel", world, log);
                if (duel.DefenderBrokeOff)
                    ForceBreaksOff(states, patrolRef, force, node, world, tuning, log);
            }
            RecordCombatOutcome(states, world, log, raidRef, defence, targetNode, node, CombatMeeting.Guard, attack, breakLost,
                defenderDestroyed, duel.DefenderBrokeOff, duel.Source, atkBrought, defBrought, patrol.ActorTag, patrol.LogId);
        }

        /// <summary>Every fleet in the defending force, lead alone when there is no force list.</summary>
        private static List<(int si, int fi)> ForceRefs((int si, int fi) lead, List<(int si, int fi)> force)
        {
            return force != null && force.Count > 0 ? force : new List<(int si, int fi)> { lead };
        }

        /// <summary>A defending force that left a contested duel below its break-off fraction
        /// (<see cref="ShipDuel.Result.DefenderBrokeOff"/>). The node is left to the attacker, which
        /// carries its surviving ratio on as it would past a beaten defence; each Guard in the force
        /// ends its duty window now and goes home with its hulls, as a retreating Guard does
        /// (<see cref="BeginRetreat"/>).</summary>
        private static void ForceBreaksOff(IList<FactionState> states, (int si, int fi) lead,
            List<(int si, int fi)> force, string node, WorldModel world, FactionTuning tuning, IList<string> log)
        {
            List<(int si, int fi)> refs = ForceRefs(lead, force);
            for (int i = 0; i < refs.Count; i++)
            {
                PendingFleetAction guard = states[refs[i].si].PendingFleets[refs[i].fi];
                if (!BeginRetreat(ref guard, world.UtcNow, tuning, "broke_off")) continue;
                states[refs[i].si].PendingFleets[refs[i].fi] = guard;
                log.Add("GUARD_BROKE_OFF " + guard.LogId + " " + guard.ActorTag + " @" + node);
            }
        }

        /// <summary>A raid whose meeting was undecided while the defence still stood
        /// (<see cref="ShipDuel.Result.AttackerRepelled"/>), whoever decided it (<paramref name="source"/>).
        /// Undecided repels the attacker: both sides would draw off to rearm, and the defence, at home,
        /// would run down attackers it had left without ammunition, so the attackers go at once. It
        /// lands no effect and takes no loot; the stop is recorded as a repelled attack is (event, both
        /// ledgers, the incident, the campaign's intercepted outcome); and it leaves its dwell now
        /// through <see cref="BeginRetreat"/>, so this pass takes it onto its return leg. Decided by the
        /// grids, every hull still flying goes home at its measured integrity
        /// (<see cref="PendingFleetAction.HullsMeasured"/>); decided by the duel, its hulls carry the
        /// duel's attrition (<paramref name="attrition"/>), written off by capacity at a ratio the
        /// hull-return threshold would take whole (<see cref="SettleDuelHulls"/>). A raid with no dwell to leave cannot
        /// retreat, and is settled on the spot as a broken-off attack is.</summary>
        private static void RepelAttack((int si, int fi) raidRef, PendingFleetAction raid, string defenderTag, string node,
            long opposedDps, double ratio, double attrition, string source, List<WeaponBand> defenderBands, long defenderFlat,
            IList<FactionState> states, WorldModel world, FactionTuning tuning, IList<string> log)
        {
            PendingFleetAction repelled = states[raidRef.si].PendingFleets[raidRef.fi];
            int hullsLost = 0, hullsBrought = repelled.HullCount;
            repelled.OpposedDps = opposedDps;
            repelled.ForcedRatio = ratio;
            repelled.ForcedRatioCause = source ?? SourcePhysical;
            if (BeginRetreat(ref repelled, world.UtcNow, tuning, "repelled"))
            {
                hullsLost = SettleDuelHulls(ref repelled, ratio, attrition, source ?? SourcePhysical, "repelled", world, log);
                states[raidRef.si].PendingFleets[raidRef.fi] = repelled;
            }
            else
            {
                states[raidRef.si].PendingFleets[raidRef.fi] = repelled;
                int total, lost;
                BreakOffAttack(raidRef, defenderTag, node, opposedDps, attrition, states, world, log, out total, out lost,
                               "repelled:");
                MarkStopped(states, raidRef, "repelled", ratio, source);
                hullsLost = lost;
                hullsBrought = total;
            }
            RecordAttackStopped(raidRef, raid, defenderTag, node, defenderBands, defenderFlat, states, world, tuning,
                                hullsLost, hullsBrought);
        }

        /// <summary>What a stopped attack records beside its hull ledger: the repelled event, the fight
        /// in both ledgers, the repelled-attack memory and the campaign's intercepted outcome.</summary>
        private static void RecordAttackStopped((int si, int fi) raidRef, PendingFleetAction raid, string defenderTag,
            string node, List<WeaponBand> defenderBands, long defenderFlat,
            IList<FactionState> states, WorldModel world, FactionTuning tuning, int hullsLost, int hullsBrought)
        {
            PendingFleetAction stopped = states[raidRef.si].PendingFleets[raidRef.fi];
            if (!raid.IsBench) RecordRaidRepelled(world, raid, defenderTag, node, hullsLost, hullsBrought);
            RecordFight(states, stopped, defenderTag, 0, true, world, tuning);
            RecordRepelledAttack(stopped, world);
            RecordCampaignOutcome(states, raidRef, CampaignOutcome.Intercepted,
                CombatStrength.WorstBracket(raid.WeaponBands, defenderBands, null, defenderFlat, tuning), world);
        }

        /// <summary>Files <see cref="FactionEventKind.RaidRepelled"/> with what the stop cost the attacker:
        /// <see cref="FactionEvent.Amount"/> the hulls lost, <see cref="FactionEvent.Ratio"/> their share of
        /// the hulls it brought. A ratio of 1 is the only shape a reader may call the raid destroyed.</summary>
        private static void RecordRaidRepelled(WorldModel world, PendingFleetAction raid, string defenderTag,
                                               string node, int hullsLost, int hullsBrought)
        {
            int lost = Math.Max(0, hullsLost);
            double share = hullsBrought > 0 ? Math.Min(1.0, (double)lost / hullsBrought) : 0.0;
            world.RecordEvent(FactionEventKind.RaidRepelled, raid.ActorTag, defenderTag, node, lost, share);
        }

        /// <summary>The bands every fleet in a node's defending force brings to one duel, lead
        /// first. <paramref name="force"/> null — every mid-transit meeting, where the defender met
        /// the raid between nodes and no garrison is present — is the lead alone, which is what a
        /// single defender has always resolved as.</summary>
        private static List<WeaponBand> ForceBands(IList<FactionState> states,
            (int si, int fi) lead, List<(int si, int fi)> force)
        {
            if (force == null || force.Count <= 1)
                return states[lead.si].PendingFleets[lead.fi].WeaponBands;
            var bands = new List<WeaponBand>();
            for (int i = 0; i < force.Count; i++)
            {
                List<WeaponBand> own = states[force[i].si].PendingFleets[force[i].fi].WeaponBands;
                if (own != null) bands.AddRange(own);
            }
            return bands;
        }

        /// <summary>The raid's side of a duel as <see cref="DuelMunitions"/> sees it, or null when it
        /// was never armed and fights on its bands as before.</summary>
        private static DuelMunitions RaidMunitions((int si, int fi) raidRef, IList<FactionState> states,
                                                   WorldModel world, FactionTuning tuning)
        {
            return DuelMunitions.ForSide(states, new[] { raidRef }, f => f.WeaponBands, null, 1.0, world, tuning);
        }

        /// <summary>A defending force's side: every fleet <see cref="ForceBands"/> reads, plus the
        /// node's own station weapons, which fire without limit.</summary>
        private static DuelMunitions ForceMunitions(IList<FactionState> states, (int si, int fi) lead,
            List<(int si, int fi)> force, List<WeaponBand> stationBands, WorldModel world, FactionTuning tuning)
        {
            IList<(int si, int fi)> fleets = force == null || force.Count <= 1
                ? (IList<(int si, int fi)>)new[] { lead } : force;
            return DuelMunitions.ForSide(states, fleets, f => f.WeaponBands, stationBands, 1.0, world, tuning);
        }

        private static void CommitMunitions(DuelMunitions attacker, DuelMunitions defender,
                                            IList<FactionState> states, WorldModel world, IList<string> log)
        {
            if (attacker != null) attacker.Commit(states, world, log);
            if (defender != null) defender.Commit(states, world, log);
        }

        /// <summary>Applies an abstract fight's surviving fraction to the hulls of one fleet that came
        /// out of it (<see cref="HullDamage.ApplyAttrition"/>) and, when that damaged anything, logs
        /// one <c>HULL_DAMAGED</c> line.</summary>
        private static void RecordAttrition(ref PendingFleetAction fleet, double survivingFraction, string cause,
                                            WorldModel world, IList<string> log)
        {
            if (!HullDamage.ApplyAttrition(ref fleet, survivingFraction, world.PrefabCatalogue)) return;
            log.Add("HULL_DAMAGED " + fleet.LogId + " " + fleet.ActorTag + " cause=" + cause
                    + " surviving=" + survivingFraction.ToString("0.00", CultureInfo.InvariantCulture)
                    + " hulls=" + HullDamage.Describe(fleet));
        }

        /// <summary><see cref="RecordAttrition"/> for every fleet of a defending force that survived
        /// its duel -- the same set <see cref="MarkForceDestroyed"/> would have written off.</summary>
        private static void RecordForceAttrition(IList<FactionState> states, (int si, int fi) lead,
            List<(int si, int fi)> force, double survivingFraction, string cause, WorldModel world, IList<string> log)
        {
            if (force == null || force.Count == 0) force = new List<(int si, int fi)> { lead };
            for (int i = 0; i < force.Count; i++)
            {
                PendingFleetAction f = states[force[i].si].PendingFleets[force[i].fi];
                if (f.Intercepted) continue;
                RecordAttrition(ref f, survivingFraction, cause, world, log);
                states[force[i].si].PendingFleets[force[i].fi] = f;
            }
        }

        /// <summary>Marks a lost defence. The force fought as one, so it is destroyed as one —
        /// crediting the loss to the lead alone would leave the rest of the garrison standing on a
        /// node whose defence the duel has already decided was overwhelmed.
        ///
        /// <para>Each fleet it writes off logs one <c>SHIP_LOST ... why=guard_destroyed</c> with its
        /// hulls and records a <see cref="FactionEventKind.ShipLost"/> for its faction. A fleet already
        /// written off -- a force that two raids resolved in one pass -- is skipped, so a loss is
        /// logged once. A bench fleet drew no hull and loses none.</para>
        /// <para>Provenance: FAC-432 fix 5 (101 guard hulls died with no line).</para></summary>
        private static void MarkForceDestroyed(IList<FactionState> states,
            (int si, int fi) lead, List<(int si, int fi)> force, string attackerTag, string node,
            WorldModel world, IList<string> log)
        {
            if (force == null || force.Count == 0) force = new List<(int si, int fi)> { lead };
            for (int i = 0; i < force.Count; i++)
            {
                PendingFleetAction defeated = states[force[i].si].PendingFleets[force[i].fi];
                if (defeated.Intercepted) continue;
                defeated.Intercepted = true;
                states[force[i].si].PendingFleets[force[i].fi] = defeated;
                if (defeated.IsBench) continue;

                int hulls = HullsStanding(defeated);
                bool squadron = defeated.Squadron != null && defeated.Squadron.Count > 0;
                world.RecordEvent(FactionEventKind.ShipLost, defeated.ActorTag, attackerTag, node, hulls, 0.0);
                if (log != null)
                    log.Add("SHIP_LOST " + defeated.LogId + " " + defeated.ActorTag
                            + " prefab=" + (squadron ? "squadron" : defeated.ShipPrefabId ?? "")
                            + " count=" + hulls + " node=" + (node ?? "?")
                            + " why=guard_destroyed" + (attackerTag != null ? " by=" + attackerTag : "")
                            + (squadron ? " hulls=" + string.Join(",", defeated.HullsToReturn) : ""));
            }
        }

        /// <summary>A raid arriving at a node with no Guard fleet present still duels the node's own
        /// station defense (weapon bands + <see cref="FactionTuning.StationDefensePower"/> flat
        /// floor) instead of reaching <see cref="ResolveSingle"/> completely unopposed — every
        /// station-backed node is an implicit combatant. A bare node with neither a station nor a
        /// Guard still resolves a token duel (both sides floor to DPS 1) rather than special-casing
        /// "no defense at all" as a separate path.</summary>
        private static void ResolveStationDefense(
            (int si, int fi) raidRef, IList<FactionState> states, WorldModel world, FactionTuning tuning,
            IRandom rng, IList<string> log)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            string node = raid.TargetNodeId;

            SpatialNode targetNode;
            world.Graph.Nodes.TryGetValue(node, out targetNode);
            List<WeaponBand> defenderBands = targetNode != null ? targetNode.StationWeapons : null;
            long defenderFlat = CombatStrength.StationFlat(targetNode, tuning);

            DuelMunitions raidMunitions = RaidMunitions(raidRef, states, world, tuning);
            string stationOwner = targetNode != null ? targetNode.OwnerFactionTag : null;
            ShipDuel.Result duel = ResolveMeeting(raid.WeaponBands, defenderBands, defenderFlat, tuning, rng,
                world, states, raidRef, null, stationOwner, targetNode, null, log, raidMunitions, null);
            raid = states[raidRef.si].PendingFleets[raidRef.fi];
            double attackerRatio = duel.AttackerRatio;
            bool attackerDestroyed = duel.AttackerDestroyed;
            string turnSummary = duel.TurnSummary;
            CommitMunitions(raidMunitions, null, states, world, log);
            long opposedDps = (long)(TotalDps(defenderBands) + defenderFlat);
            int atkBrought, defBrought;
            HullsBrought(states, raidRef, null, out atkBrought, out defBrought);
            CombatAttackResult attack;
            int breakLost = 0;

            if (duel.AttackerRepelled)
            {
                // Decided by the grids with the station's turrets still working: repelled.
                attack = CombatAttackResult.Repelled;
                RepelAttack(raidRef, raid, stationOwner, node, opposedDps, attackerRatio, duel.AttackerAttrition, duel.Source, defenderBands, defenderFlat,
                            states, world, tuning, log);
                log.Add("RAID_REPELLED " + raid.LogId + " " + raid.ActorTag + " vs station @" + node
                        + " ratio=" + attackerRatio.ToString("0.00", CultureInfo.InvariantCulture)
                        + " outcome=repelled src=" + duel.Source + " (" + turnSummary + ")");
            }
            else if (!attackerDestroyed && duel.AttackerBrokeOff)
            {
                int total, lost;
                BreakOffAttack(raidRef, stationOwner, node, opposedDps, duel.AttackerAttrition, states, world, log,
                               out total, out lost);
                attack = CombatAttackResult.BrokeOff;
                breakLost = lost;
                MarkStopped(states, raidRef, "broke_off", attackerRatio, duel.Source);
                RecordAttackStopped(raidRef, raid, stationOwner, node, defenderBands, defenderFlat, states, world, tuning,
                                    lost, total);
                log.Add("RAID_BROKE_OFF " + raid.LogId + " " + raid.ActorTag + " vs station @" + node
                        + " ratio=" + attackerRatio.ToString("0.00", CultureInfo.InvariantCulture)
                        + " hulls_lost=" + lost + "/" + total + " src=" + duel.Source + " (" + turnSummary + ")");
            }
            else if (attackerDestroyed)
            {
                string ownerTag = targetNode != null ? targetNode.OwnerFactionTag : null;
                int total, lost;
                BreakAttack(raidRef, ownerTag, node, opposedDps,
                            HullRoster(raid, world.PrefabCatalogue),
                            raid.Squadron != null && raid.Squadron.Count > 0,
                            states, world, tuning, rng, log, out total, out lost);
                attack = CombatAttackResult.Destroyed;
                breakLost = lost;
                MarkStopped(states, raidRef, "beaten", 0.0, duel.Source);
                PendingFleetAction intercepted = states[raidRef.si].PendingFleets[raidRef.fi];
                if (!raid.IsBench) RecordRaidRepelled(world, raid, ownerTag, node, lost, total);
                RecordFight(states, intercepted, ownerTag, 0, true, world, tuning);
                RecordRepelledAttack(intercepted, world);
                RecordCampaignOutcome(states, raidRef, CampaignOutcome.Intercepted,
                    CombatStrength.WorstBracket(raid.WeaponBands, defenderBands, null, defenderFlat, tuning), world);
                log.Add("STATION_INTERCEPT " + raid.LogId + " " + raid.ActorTag + " repelled by station defense @" + node
                        + " hulls_lost=" + lost + "/" + total + " src=" + duel.Source + " (" + turnSummary + ")");
            }
            else
            {
                attack = CombatAttackResult.Landed;
                var survivor = raid;
                survivor.ForcedRatio = attackerRatio;
                survivor.ForcedRatioCause = "station_duel";
                survivor.OpposedDps = opposedDps;
                survivor.DuelTactic = duel.Tactic;
                SettleDuelHulls(ref survivor, attackerRatio, duel.AttackerAttrition, duel.Source, "station_duel", world, log);
                states[raidRef.si].PendingFleets[raidRef.fi] = survivor;
                log.Add("STATION_DEFENSE " + raid.LogId + " " + raid.ActorTag + " ratio=" + attackerRatio.ToString("0.00", CultureInfo.InvariantCulture)
                        + " @" + node + " src=" + duel.Source + " (" + turnSummary + ")");
            }
            RecordCombatOutcome(states, world, log, raidRef, null, targetNode, node, CombatMeeting.Station, attack, breakLost,
                duel.DefenderDestroyed, duel.DefenderBrokeOff, duel.Source, atkBrought, defBrought, stationOwner, null);
        }

        /// <summary>Raid intercepts Trade(s) converging on the same node. Base chance
        /// (<see cref="FactionTuning.RaidInterceptsTradeChance"/>) scales with the raider's longest
        /// weapon range — a longer-ranged raider is more likely to run down a fleeing convoy.
        /// Intercepted trades fail; successful interceptions loot
        /// <see cref="FactionTuning.RaidLootFraction"/> of cargo.
        /// <paramref name="meetNode"/> overrides the log node label for mid-transit encounters.</summary>
        /// <summary>Writes down the hull a raid interception costs its victim. The intercepted
        /// convoy is removed at the end of the pass without ever reaching a terminal, so nothing
        /// credits its hull back and, until this, nothing said it had died: the only trace was the
        /// raider's <c>RAID_LOOTS</c> line. This is the same <c>SHIP_LOST</c> verb every other hull
        /// terminal uses, with the fields <see cref="LossDetail"/> gives them, plus the raid that
        /// took it, and a <see cref="FactionEventKind.ShipLost"/> event for the victim. A convoy that
        /// consumed no hull (an empty prefab) has nothing to write down.
        /// <para>Provenance: FAC-305.</para></summary>
        private static void RecordInterceptedHull(PendingFleetAction trade, PendingFleetAction raid,
                                                  string node, WorldModel world, IList<string> log)
        {
            if (string.IsNullOrEmpty(trade.ShipPrefabId)) return;
            int lost = trade.Squadron != null && trade.Squadron.Count > 0
                ? trade.HullsToReturn.Count
                : Math.Max(1, trade.ConvoySize);
            if (lost <= 0) return;
            world.RecordEvent(FactionEventKind.ShipLost, trade.ActorTag, raid.ActorTag, node, lost, 0.0);
            log.Add("SHIP_LOST " + trade.LogId + " " + trade.ActorTag
                    + LossDetail(trade, 0.0, "intercepted:" + raid.ActorTag)
                    + " raid=" + raid.LogId);
        }

        // ---- convoy encounter (FAC-306) ----------------------------------------------------------

        /// <summary>A fleet's bands as the duel should read them: the ones set at dispatch when it
        /// has any, else rebuilt from the catalogue — a squadron member by member, anything else as
        /// <see cref="PendingFleetAction.ShipPrefabId"/> × <see cref="PendingFleetAction.ConvoySize"/>.
        /// Convoys are not given bands at dispatch and no fleet's bands survive a reload, so without
        /// this every convoy reads unarmed and a reloaded raid does too. Empty, never null.</summary>
        internal static List<WeaponBand> EffectiveBands(PendingFleetAction fleet, IPrefabCatalogue cat)
        {
            if (fleet.WeaponBands != null && fleet.WeaponBands.Count > 0) return fleet.WeaponBands;
            var bands = new List<WeaponBand>();
            if (cat == null) return bands;
            if (fleet.HullIntegrity != null) return HullDamage.ScaledBands(fleet, cat);
            if (fleet.Squadron != null && fleet.Squadron.Count > 0)
            {
                IReadOnlyList<string> hulls = fleet.HullsToReturn;
                for (int i = 0; i < hulls.Count; i++) bands.AddRange(ShipPool.ConvoyWeaponBands(cat, hulls[i], 1));
                return bands;
            }
            return ShipPool.ConvoyWeaponBands(cat, fleet.ShipPrefabId, Math.Max(1, fleet.ConvoySize));
        }

        private static double PerHullDps(IPrefabCatalogue cat, string prefabId)
        {
            if (cat == null || string.IsNullOrEmpty(prefabId)) return 0;
            IReadOnlyList<WeaponBand> bands = cat.WeaponBands(prefabId);
            if (bands == null) return 0;
            double sum = 0;
            for (int i = 0; i < bands.Count; i++) sum += bands[i].Dps;
            return sum;
        }

        private static List<WeaponBand> ScaleBands(List<WeaponBand> bands, double factor)
        {
            var scaled = new List<WeaponBand>(bands.Count);
            for (int i = 0; i < bands.Count; i++)
                scaled.Add(new WeaponBand { Dps = (float)(bands[i].Dps * factor), RangeM = bands[i].RangeM });
            return scaled;
        }

        /// <summary>A raid meets a given convoy once. The convoy keeps the id of the raid that last
        /// held it, and that pair is skipped by every later meeting -- live, a pirate robbed a convoy
        /// at the end of its window and caught it again ten seconds later at the next waypoint the
        /// two shared, which is the multi-waypoint re-robbery of v0.216.0 in slow motion.</summary>
        private static bool AlreadyMet(PendingFleetAction convoy, PendingFleetAction raid)
        {
            return !string.IsNullOrEmpty(convoy.InterceptedByFleetId) && !string.IsNullOrEmpty(raid.FleetId)
                   && string.Equals(convoy.InterceptedByFleetId, raid.FleetId, StringComparison.Ordinal);
        }

        /// <summary>Whether the meeting node is ground the victim can count on: its own, or a
        /// pact partner's. A waypoint counts exactly as an arrival node does.</summary>
        private static bool MeetingNodeIsFriendly(string nodeId, string victimTag, IList<FactionState> states, WorldModel world)
        {
            SpatialNode n;
            if (nodeId == null || !world.Graph.Nodes.TryGetValue(nodeId, out n)) return false;
            string owner = n.OwnerFactionTag;
            if (string.IsNullOrEmpty(owner)) return false;
            if (string.Equals(owner, victimTag, StringComparison.OrdinalIgnoreCase)) return true;
            for (int i = 0; i < states.Count; i++)
                if (states[i] != null && string.Equals(states[i].Tag, victimTag, StringComparison.OrdinalIgnoreCase))
                    return Diplomacy.InPactWith(states[i], owner);
            return false;
        }

        /// <summary>"weaker", "equal" or "stronger" — the victim measured against the pirate. No DPS
        /// is always weaker, whatever the pirate brought.</summary>
        public static string ClassifyConvoy(double victimDps, double pirateDps, FactionTuning tuning)
        {
            if (victimDps <= 0) return "weaker";
            double ratio = victimDps / Math.Max(1.0, pirateDps);
            if (ratio < tuning.ConvoyEqualBandLower) return "weaker";
            if (ratio > tuning.ConvoyEqualBandUpper) return "stronger";
            return "equal";
        }

        // ---- interception window (FAC-307) ---------------------------------------------------

        /// <summary>Holds a convoy and the raid that caught it at the meeting node for
        /// <see cref="FactionTuning.InterceptWindowSeconds"/>. Each fleet's current leg is re-queued
        /// behind the hold, so afterwards it flies on to wherever it was going; the convoy remembers
        /// the raid by <see cref="PendingFleetAction.InterceptedByFleetId"/>. A raid already dwelling
        /// (<see cref="FleetStopKind.Engage"/>) at this very node is not re-pinned -- it is held there
        /// already, by its own dwell -- and the convoy's window ends when that dwell does, so the
        /// encounter is decided while the pirate is still present.</summary>
        private static void OpenInterceptWindow(
            (int si, int fi) raidRef, (int si, int fi) tradeRef, string node,
            string prefix, string rollNote,
            IList<FactionState> states, WorldModel world, FactionTuning tuning, IList<string> log)
        {
            PendingFleetAction raid  = states[raidRef.si].PendingFleets[raidRef.fi];
            PendingFleetAction trade = states[tradeRef.si].PendingFleets[tradeRef.fi];
            SpatialNode meet;
            world.Graph.Nodes.TryGetValue(node, out meet);
            DateTime now = world.UtcNow;
            DateTime until = now.AddSeconds(tuning.InterceptWindowSeconds);

            bool raidAlreadyHeldHere = raid.CurrentStopKind == FleetStopKind.Engage
                                       && string.Equals(raid.TargetNodeId, node, StringComparison.Ordinal);
            // A Guard that caught a trespasser at its own node is held there by its duty window.
            bool guardHeldHere = !raidAlreadyHeldHere && GuardHeldAt(raid, node, now, tuning);
            if (raidAlreadyHeldHere)
            {
                if (raid.EstimatedArrivalUtc > now && raid.EstimatedArrivalUtc < until) until = raid.EstimatedArrivalUtc;
            }
            else if (guardHeldHere)
            {
                DateTime dutyEnds = raid.EstimatedArrivalUtc.Add(tuning.GuardDuration);
                if (dutyEnds > now && dutyEnds < until) until = dutyEnds;
            }
            else
            {
                PinAtNode(ref raid, node, meet, now, until);
                states[raidRef.si].PendingFleets[raidRef.fi] = raid;
            }

            PinAtNode(ref trade, node, meet, now, until);
            trade.InterceptedByFleetId = raid.FleetId;
            states[tradeRef.si].PendingFleets[tradeRef.fi] = trade;

            // A pirate parked over a node is hostile activity there, sized as a visit rather than a
            // landing (FAC-261's decayed level; FAC-307).
            if (meet != null && tuning.InterceptThreat > 0) world.AddThreat(meet, tuning.InterceptThreat);

            log.Add(prefix + "INTERCEPT_WINDOW " + raid.LogId + " " + raid.ActorTag + " holds " + trade.LogId + " " + trade.ActorTag
                    + " @" + node + " until=" + until.ToString("HH:mm:ss")
                    + " window=" + (until - now).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s"
                    + (raidAlreadyHeldHere ? " raid=dwelling" : guardHeldHere ? " raid=guard" : "") + rollNote);
        }

        /// <summary>Parks <paramref name="fleet"/> at <paramref name="node"/> on an
        /// <see cref="FleetStopKind.Intercept"/> leg ending at <paramref name="until"/>. The leg it
        /// was flying is pushed back onto the itinerary as the next stop, with its own kind and
        /// payload, so nothing about the mission is lost -- an arrival leg re-flies as a zero-length
        /// hop and resolves the moment the hold ends; a mid-transit leg is re-planned from here.</summary>
        private static void PinAtNode(ref PendingFleetAction fleet, string node, SpatialNode meet, DateTime now, DateTime until)
        {
            if (fleet.RemainingStops == null) fleet.RemainingStops = new List<FleetStop>();
            if (fleet.CurrentStopKind != FleetStopKind.Intercept
                && (fleet.TargetNodeId != null || fleet.CurrentStopKind == FleetStopKind.Navigation))
            {
                fleet.RemainingStops.Insert(0, new FleetStop
                {
                    NodeId          = fleet.TargetNodeId,
                    Kind            = fleet.CurrentStopKind,
                    HasPosition     = fleet.TargetNodeId == null,
                    PositionKm      = fleet.TargetPosKm,
                    PickupItemKey   = fleet.PickupItemKey,
                    PickupQuantity  = fleet.PickupQuantity,
                    MineWeights     = fleet.MineWeights,
                    MineTargetLiters = fleet.MineTargetLiters,
                    // A dwell that was interrupted restarts in full; the fraction already spent is
                    // what the hold cost the fleet.
                    DurationSeconds = IsDwellStop(fleet.CurrentStopKind)
                        ? (fleet.EstimatedArrivalUtc - fleet.DepartedUtc).TotalSeconds : 0
                });
            }
            Vec3 pos = meet != null ? meet.Position : fleet.CurrentPosKm(now);
            fleet.OriginPosKm = pos;
            fleet.TargetPosKm = pos;
            fleet.TargetNodeId = node;
            fleet.TargetTag = meet != null ? meet.OwnerFactionTag : fleet.TargetTag;
            fleet.DepartedUtc = now;
            fleet.EstimatedArrivalUtc = until;
            fleet.CurrentStopKind = FleetStopKind.Intercept;
            fleet.PickupItemKey = null;
            fleet.PickupQuantity = 0;
            fleet.MineWeights = null;
            fleet.MineTargetLiters = 0;
            fleet.PathNodeIds = null;
            fleet.LastResolvedSegment = -1;
            fleet.RouteDistanceKm = 0;
            fleet.RouteCoveredKm = 0;
            fleet.RouteDepartedUtc = default(DateTime);
            fleet.Resolved = false;
        }

        /// <summary>The terminal of an <see cref="FleetStopKind.Intercept"/> leg. For the raid it is
        /// nothing more than the end of a hold: it flies on, and whatever the convoy's terminal did
        /// to it (repelled, marked lost) stands. For the convoy it is the encounter itself, decided
        /// against the raid it was held by -- unless a player settled it first: a convoy whose grid
        /// was destroyed or captured inside the window is lost to the pirate, and a raid that is gone
        /// (destroyed, repelled, or no longer in the sky) frees the convoy to continue untouched.</summary>
        private static void ResolveInterceptWindow(
            int si, int fi, IList<FactionState> states, WorldModel world, FactionTuning tuning,
            IRandom rng, IList<string> log, IItemPriceCatalogue prices)
        {
            FactionState owner = states[si];
            PendingFleetAction fleet = owner.PendingFleets[fi];
            string node = fleet.TargetNodeId;

            // A hauler or miner is held only by a Guard that caught it trespassing (FleetTrespass).
            bool isConvoy = fleet.Kind == ActionKind.Trade || fleet.Kind == ActionKind.Ferry
                            || fleet.Kind == ActionKind.Haul || fleet.Kind == ActionKind.Mine;
            if (isConvoy && fleet.InterceptedByFleetId != null)
            {
                // The link is kept after the verdict: it is what stops the same raid catching this
                // convoy again at the next shared node (AlreadyMet). A different raid overwrites it.
                string raidId = fleet.InterceptedByFleetId;

                (int si, int fi) raidRef = (-1, -1);
                for (int rs = 0; rs < states.Count && raidRef.si < 0; rs++)
                {
                    FactionState st = states[rs];
                    if (st == null) continue;
                    for (int rf = 0; rf < st.PendingFleets.Count; rf++)
                    {
                        PendingFleetAction r = st.PendingFleets[rf];
                        if (r.Intercepted || string.IsNullOrEmpty(r.FleetId)
                            || !string.Equals(r.FleetId, raidId, StringComparison.Ordinal)) continue;
                        if (r.Kind != ActionKind.Raid && r.Kind != ActionKind.Retaliate && r.Kind != ActionKind.Conquer
                            && r.Kind != ActionKind.Guard) continue;
                        // A Guard holds only a trespasser it caught at its own faction's node.
                        if (r.Kind == ActionKind.Guard
                            && !string.Equals(r.ActorTag, fleet.TargetTag, StringComparison.Ordinal)) continue;
                        if (IsOutcomeAlreadyDecided(r)) continue;   // a grid a player already destroyed or took
                        raidRef = (rs, rf);
                        break;
                    }
                }

                if (fleet.IsPhysicalLoss)
                {
                    // The player settled it against the convoy: whoever held it collects.
                    if (raidRef.si >= 0)
                    {
                        PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
                        long loot = LootCargo(ref fleet, 1.0, raid, world, tuning, prices);
                        world.RecordEvent(FactionEventKind.ConvoyLooted, raid.ActorTag, fleet.ActorTag, node, loot);
                        log.Add("RAID_LOOTS " + raid.LogId + " " + raid.ActorTag + " intercepted " + fleet.LogId + " " + fleet.ActorTag
                                + " @" + node + " loot=" + loot + " hull=lost why=" + (fleet.ForcedRatioCause ?? "grid_lost")
                                + " (window)");
                        fleet.Intercepted = true;
                        owner.PendingFleets[fi] = fleet;
                        RecordInterceptedHull(fleet, raid, node, world, log);
                    }
                    else
                    {
                        world.RecordEvent(FactionEventKind.ShipLost, fleet.ActorTag, null, node, Math.Max(1, fleet.ConvoySize), 0.0);
                        log.Add("SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag + LossDetail(fleet, 0.0) + " (window)");
                        fleet.Intercepted = true;
                        owner.PendingFleets[fi] = fleet;
                    }
                    return;
                }

                if (raidRef.si < 0)
                {
                    log.Add("INTERCEPT_FREED " + fleet.LogId + " " + fleet.ActorTag + " @" + node
                            + " raid=[" + raidId + "] why=raid_gone");
                }
                else
                {
                    PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
                    double raidStrength = raid.ForcedRatio.HasValue ? Math.Max(0.0, Math.Min(1.0, raid.ForcedRatio.Value)) : 1.0;
                    ResolveConvoyEncounter(raidRef, (si, fi), node, raidStrength, "", " (window)",
                                           states, world, tuning, rng, log, prices);
                    fleet = owner.PendingFleets[fi];
                    if (fleet.Intercepted) return;   // lost to the pirate; the cleanup pass removes it
                }
            }

            // The hold is over: fly on. A fleet with nothing left to fly is done.
            if (fleet.RemainingStops != null && fleet.RemainingStops.Count > 0
                && AdvanceToNextStop(owner, fi, fleet, world, tuning, log))
                return;
            fleet.Resolved = true;
            owner.PendingFleets[fi] = fleet;
        }

        /// <summary>What happens once the intercept roll has passed. Replaces "loot and delete":
        /// the victim's firepower against the pirate's decides whether it is robbed, fights, or
        /// sends the pirate home, and the hull is a roll rather than a certainty.
        /// <para>Provenance: FAC-306 (rules by the operator, 2026-09-13).</para></summary>
        private static void ResolveConvoyEncounter(
            (int si, int fi) raidRef, (int si, int fi) tradeRef, string node, double raidStrength,
            string prefix, string rollNote,
            IList<FactionState> states, WorldModel world, FactionTuning tuning, IRandom rng,
            IList<string> log, IItemPriceCatalogue prices)
        {
            PendingFleetAction raid  = states[raidRef.si].PendingFleets[raidRef.fi];
            PendingFleetAction trade = states[tradeRef.si].PendingFleets[tradeRef.fi];
            IPrefabCatalogue cat = world.PrefabCatalogue;

            // Caught, but not yet decided: hold both at the meeting node for the window a player
            // could settle it in. The abstract rules below run when that window ends, from
            // ResolveInterceptWindow, which calls back in here with the convoy already held.
            if (trade.CurrentStopKind != FleetStopKind.Intercept
                && tuning.InterceptWindowSeconds > 0 && !string.IsNullOrEmpty(raid.FleetId))
            {
                OpenInterceptWindow(raidRef, tradeRef, node, prefix, rollNote, states, world, tuning, log);
                return;
            }

            List<WeaponBand> victimBands = EffectiveBands(trade, cat);
            List<WeaponBand> pirateBands = EffectiveBands(raid, cat);
            if (raidStrength < 1.0) pirateBands = ScaleBands(pirateBands, raidStrength);
            double victimDps = TotalDps(victimBands);
            double pirateDps = TotalDps(pirateBands);
            bool friendly = MeetingNodeIsFriendly(node, trade.ActorTag, states, world);
            string verdict = ClassifyConvoy(victimDps, pirateDps, tuning);
            string dpsNote = " victim_dps=" + victimDps.ToString("0", CultureInfo.InvariantCulture)
                           + " pirate_dps=" + pirateDps.ToString("0", CultureInfo.InvariantCulture)
                           + " victim=" + verdict + (friendly ? " friendly_node" : "");

            if (verdict == "equal")
            {
                DuelMunitions pirateMunitions = DuelMunitions.ForSide(states, new[] { raidRef },
                    f => EffectiveBands(f, cat), null, raidStrength < 1.0 ? raidStrength : 1.0, world, tuning);
                DuelMunitions victimMunitions = DuelMunitions.ForSide(states, new[] { tradeRef },
                    f => EffectiveBands(f, cat), null, 1.0, world, tuning);
                SpatialNode meetAt;
                world.Graph.Nodes.TryGetValue(node ?? "", out meetAt);
                ShipDuel.Result duel = ResolveShipDuel(pirateBands, victimBands, 0, tuning, rng,
                    world, states, raidRef, new List<(int si, int fi)> { tradeRef }, trade.ActorTag, meetAt,
                    log, pirateMunitions, victimMunitions);
                double atkRatio = duel.AttackerRatio, defRatio = duel.DefenderRatio;
                bool atkDestroyed = duel.AttackerDestroyed, defDestroyed = duel.DefenderDestroyed;
                string turns = duel.TurnSummary;
                CommitMunitions(pirateMunitions, victimMunitions, states, world, log);
                // A side that broke off conceded the encounter: a pirate leaves without the cargo, a
                // convoy gives it up to get away.
                bool pirateWins = !atkDestroyed && !duel.AttackerBrokeOff
                                  && (defDestroyed || duel.DefenderBrokeOff || defRatio < atkRatio);
                log.Add(prefix + "CONVOY_DUEL " + raid.LogId + " " + raid.ActorTag + " vs " + trade.LogId + " " + trade.ActorTag
                        + " @" + node + " result=" + (pirateWins ? "pirate_wins" : "victim_wins")
                        + " pirate_ratio=" + atkRatio.ToString("0.00", CultureInfo.InvariantCulture)
                        + " victim_ratio=" + defRatio.ToString("0.00", CultureInfo.InvariantCulture)
                        + dpsNote + " (" + turns + ")");
                if (pirateWins)
                    RobConvoy(raidRef, tradeRef, node, raidStrength,
                              friendly ? tuning.ConvoyDuelHullLossChanceFriendly : tuning.ConvoyDuelHullLossChance,
                              prefix, rollNote, dpsNote, states, world, tuning, rng, log, prices);
                else if (duel.AttackerBrokeOff && !atkDestroyed)
                {
                    int total, lost;
                    BreakOffAttack(raidRef, trade.ActorTag, node, (long)victimDps, atkRatio, states, world, log,
                                   out total, out lost);
                    PendingFleetAction broken = states[raidRef.si].PendingFleets[raidRef.fi];
                    RecordRaidRepelled(world, raid, trade.ActorTag, node, lost, total);
                    RecordFight(states, broken, trade.ActorTag, 0, true, world, tuning);
                    RecordRepelledAttack(broken, world);
                    log.Add(prefix + "CONVOY_REPELS " + trade.LogId + " " + trade.ActorTag + " repelled " + raid.LogId + " " + raid.ActorTag
                            + " @" + node + " reason=broke_off hulls_lost=" + lost + "/" + total + dpsNote);
                }
                else
                    RepelPirate(raidRef, trade, node, "convoy_duel", prefix, dpsNote, victimDps, states, world, tuning, rng, log);
                // Whichever side is still flying carries its own share of the duel home.
                PendingFleetAction pirateAfter = states[raidRef.si].PendingFleets[raidRef.fi];
                if (!pirateAfter.Intercepted)
                {
                    RecordAttrition(ref pirateAfter, atkRatio, "convoy_duel", world, log);
                    states[raidRef.si].PendingFleets[raidRef.fi] = pirateAfter;
                }
                PendingFleetAction victimAfter = states[tradeRef.si].PendingFleets[tradeRef.fi];
                if (!victimAfter.Intercepted)
                {
                    RecordAttrition(ref victimAfter, defRatio, "convoy_duel", world, log);
                    states[tradeRef.si].PendingFleets[tradeRef.fi] = victimAfter;
                }
                return;
            }

            if (verdict == "stronger")
            {
                RepelPirate(raidRef, trade, node, "outgunned", prefix, dpsNote, victimDps, states, world, tuning, rng, log);
                return;
            }

            RobConvoy(raidRef, tradeRef, node, raidStrength,
                      friendly ? tuning.ConvoyWeakHullLossChanceFriendly : tuning.ConvoyWeakHullLossChance,
                      prefix, rollNote, dpsNote, states, world, tuning, rng, log, prices);
        }

        /// <summary>The robbery: loot leaves the convoy, then one roll decides the hull. A surviving
        /// convoy is left un-intercepted and carries on with what it has; a lost one is marked
        /// intercepted for the cleanup pass and its death written down.</summary>
        private static void RobConvoy(
            (int si, int fi) raidRef, (int si, int fi) tradeRef, string node, double raidStrength, double hullLossChance,
            string prefix, string rollNote, string dpsNote,
            IList<FactionState> states, WorldModel world, FactionTuning tuning, IRandom rng,
            IList<string> log, IItemPriceCatalogue prices)
        {
            PendingFleetAction raid  = states[raidRef.si].PendingFleets[raidRef.fi];
            PendingFleetAction trade = states[tradeRef.si].PendingFleets[tradeRef.fi];

            long loot = LootCargo(ref trade, raidStrength, raid, world, tuning, prices);
            double lossRoll = rng.NextDouble();
            bool hullLost = hullLossChance > 0 && lossRoll < hullLossChance;
            if (hullLost) trade.Intercepted = true;
            states[tradeRef.si].PendingFleets[tradeRef.fi] = trade;

            world.RecordEvent(FactionEventKind.ConvoyLooted, raid.ActorTag, trade.ActorTag, node, loot);
            // A Guard robbing a carrier is a trespass catch: what it took is what the owner's space
            // cost the victim's lane.
            if (raid.Kind == ActionKind.Guard && loot > 0)
            {
                SpatialNode caughtAt;
                if (node != null && world.Graph.Nodes.TryGetValue(node, out caughtAt))
                    states[tradeRef.si].TransitDenial.Add(raid.ActorTag,
                        FactionBrain.CampaignKeyFor(NodeRegion.Of(caughtAt), caughtAt), loot, world.UtcNow);
            }
            log.Add(prefix + "RAID_LOOTS " + raid.LogId + " " + raid.ActorTag + " intercepted " + trade.LogId + " " + trade.ActorTag
                    + " @" + node + " loot=" + loot + " hull=" + (hullLost ? "lost" : "survived")
                    + " loss_chance=" + hullLossChance.ToString("0.00", CultureInfo.InvariantCulture)
                    + " loss_roll=" + lossRoll.ToString("0.00", CultureInfo.InvariantCulture)
                    + dpsNote + rollNote);
            if (hullLost) RecordInterceptedHull(trade, raid, node, world, log);
        }

        /// <summary>The pirate lost: the raid is cancelled ("too damaged to continue"), each hull rolls
        /// the loss series smallest-DPS first, the survivors are credited home the way a returning
        /// squadron is, and the dead are written down. The raid is marked intercepted so the cleanup
        /// pass removes it and the host despawns its grids.</summary>
        private static void RepelPirate(
            (int si, int fi) raidRef, PendingFleetAction victim, string node, string why,
            string prefix, string dpsNote, double victimDps,
            IList<FactionState> states, WorldModel world, FactionTuning tuning, IRandom rng, IList<string> log)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            bool isSquadron = raid.Squadron != null && raid.Squadron.Count > 0;
            List<KeyValuePair<string, double>> hulls = HullRoster(raid, world.PrefabCatalogue);
            int total, lost;
            BreakAttack(raidRef, victim.ActorTag, node, (long)victimDps, hulls, isSquadron,
                        states, world, tuning, rng, log, out total, out lost);

            PendingFleetAction broken = states[raidRef.si].PendingFleets[raidRef.fi];
            RecordRaidRepelled(world, raid, victim.ActorTag, node, lost, total);
            RecordFight(states, broken, victim.ActorTag, 0, true, world, tuning);
            RecordRepelledAttack(broken, world);

            log.Add(prefix + "CONVOY_REPELS " + victim.LogId + " " + victim.ActorTag + " repelled " + raid.LogId + " " + raid.ActorTag
                    + " @" + node + " reason=" + why + " hulls_lost=" + lost + "/" + total + dpsNote);
        }

        /// <summary>Breaks off an attack that lost its fight and settles what it cost, hull by hull.
        ///
        /// <para><b>Why the losses are rolled rather than total.</b> A beaten attack is a force that
        /// stops attacking, not one that is annihilated where it stands: each hull takes its own
        /// chance from the descending <see cref="FactionTuning.PirateHullLossChances"/> series,
        /// weakest first, so the escorts die before the flagship and a large squadron leaves
        /// survivors. The alternative — deleting every hull the moment the duel is lost — is the
        /// same all-or-nothing drain FAC-260 found on the other side of the same fight, and it wrote
        /// nothing down: a fleet marked intercepted is removed by the cleanup pass with no
        /// <c>SHIP_LOST</c> line at all.</para>
        ///
        /// <para>Survivors go home through the ownership-aware landing every other hull credit uses.
        /// The caller owns the event it records and the line it writes; this owns the ledger.</para>
        ///
        /// <para><paramref name="hulls"/> is every hull with its per-hull DPS, in persisted order —
        /// the caller builds it because only the caller knows whether it is reading a
        /// <see cref="PendingFleetAction.Squadron"/> or a prefab-times-count convoy.</para></summary>
        private static void BreakAttack(
            (int si, int fi) raidRef, string victimTag, string node, long opposedDps,
            List<KeyValuePair<string, double>> hulls, bool isSquadron,
            IList<FactionState> states, WorldModel world, FactionTuning tuning, IRandom rng, IList<string> log,
            out int total, out int lost)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];

            List<int> order = WeakestFirst(hulls);
            double[] series = tuning.PirateHullLossChances;
            var lostPrefabs = new List<string>();
            for (int k = 0; k < order.Count; k++)
            {
                double chance = series == null || series.Length == 0 ? 0 : series[Math.Min(k, series.Length - 1)];
                if (chance > 0 && rng.NextDouble() < chance)
                {
                    lostPrefabs.Add(hulls[order[k]].Key);
                    if (isSquadron) raid.MarkHullLost(hulls[order[k]].Key);
                }
            }
            SettleBrokenAttack(ref raid, raidRef, victimTag, node, opposedDps, hulls.Count, lostPrefabs,
                               "repelled:", isSquadron, 0.0, states, world, log);
            total = hulls.Count;
            lost = lostPrefabs.Count;
        }

        /// <summary>Hull indices smallest DPS first; indices break ties so the persisted order is kept.</summary>
        private static List<int> WeakestFirst(List<KeyValuePair<string, double>> hulls)
        {
            var order = new List<int>(); for (int i = 0; i < hulls.Count; i++) order.Add(i);
            order.Sort((a, b) => { int c = hulls[a].Value.CompareTo(hulls[b].Value); return c != 0 ? c : a.CompareTo(b); });
            return order;
        }

        /// <summary>An attack that left a contested duel below its break-off fraction
        /// (<see cref="ShipDuel.Result.AttackerBrokeOff"/>). It forfeits its mission exactly as a
        /// repelled attack does — marked intercepted, no effect, no loot — but its losses are the
        /// capacity the duel actually took, not a roll: armed hulls are written off smallest DPS
        /// first while their DPS still fits inside <c>(1 - ratio)</c> of the squadron's total, so the
        /// rounding favours the survivors. Unarmed hulls carry no capacity and are never written off
        /// here.
        ///
        /// <para>The surviving fraction is deliberately not carried as a
        /// <see cref="PendingFleetAction.ForcedRatio"/>: <c>FinalizeReturn</c> writes off every hull of
        /// a fleet returning at 0.5 or less, which is where every break-off fraction sits, so a
        /// squadron that left to save its hulls would lose them all on landing. Survivors are
        /// credited home now, the way a repelled attack's are.</para></summary>
        private static void BreakOffAttack(
            (int si, int fi) raidRef, string victimTag, string node, long opposedDps, double ratio,
            IList<FactionState> states, WorldModel world, IList<string> log, out int total, out int lost,
            string why = "broke_off:")
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            bool isSquadron = raid.Squadron != null && raid.Squadron.Count > 0;
            double survivorFraction;
            List<string> lostPrefabs = WriteOffByCapacity(ref raid, ratio, world.PrefabCatalogue,
                                                          out total, out survivorFraction);
            SettleBrokenAttack(ref raid, raidRef, victimTag, node, opposedDps, total, lostPrefabs,
                               why, isSquadron, survivorFraction, states, world, log);
            lost = lostPrefabs.Count;
        }

        /// <summary>The hulls a duel's spent capacity accounts for: armed hulls written off smallest
        /// DPS first while their DPS still fits inside <c>(1 - attrition)</c> of the fleet's total,
        /// so the rounding favours the survivors. Unarmed hulls carry no capacity and are never
        /// written off. A squadron's casualties are marked lost on <paramref name="fleet"/>; a convoy
        /// has no roster, so its caller owns the count. <paramref name="survivorFraction"/> is what
        /// the duel spent beyond the written-off hulls, spread over the ones left standing — the
        /// attrition those carry.</summary>
        private static List<string> WriteOffByCapacity(ref PendingFleetAction fleet, double attrition,
            IPrefabCatalogue cat, out int total, out double survivorFraction)
        {
            bool isSquadron = fleet.Squadron != null && fleet.Squadron.Count > 0;
            List<KeyValuePair<string, double>> hulls = HullRoster(fleet, cat);
            List<int> order = WeakestFirst(hulls);

            double capacity = 0;
            for (int i = 0; i < hulls.Count; i++) capacity += hulls[i].Value;
            double spent = capacity * (1.0 - Math.Max(0.0, Math.Min(1.0, attrition)));
            var lostPrefabs = new List<string>();
            double written = 0;
            for (int k = 0; k < order.Count; k++)
            {
                double dps = hulls[order[k]].Value;
                if (dps <= 0) continue;
                if (written + dps > spent + 1e-9) break;
                written += dps;
                lostPrefabs.Add(hulls[order[k]].Key);
                if (isSquadron) fleet.MarkHullLost(hulls[order[k]].Key);
            }
            double standing = capacity - written;
            survivorFraction = standing > 0 ? 1.0 - Math.Max(0.0, spent - written) / standing : 0.0;
            total = hulls.Count;
            return lostPrefabs;
        }

        /// <summary>The mission ratio at or under which a fleet's terminal lands no hull, unless the
        /// hulls it carries were settled one by one (<see cref="PendingFleetAction.HullsMeasured"/>,
        /// <see cref="PendingFleetAction.HullsSettled"/>) or it is a squadron that lost a grid.</summary>
        public const double HullReturnThreshold = 0.5;

        /// <summary>Applies a duel to the hulls of an attacker that carries on from it — a raid that
        /// broke a guard or a station, or was repelled onto its way home — at mission ratio
        /// <paramref name="ratio"/> with <paramref name="attrition"/> of its capacity left.
        ///
        /// <para>Above <see cref="HullReturnThreshold"/> every hull keeps <paramref name="attrition"/>
        /// of its integrity and comes home, as before. At or under it the terminal used to write off
        /// every hull, all or nothing at 0.5: a 24-hull squadron at 0.46 lost all 24, one at 0.51
        /// none. Instead the hulls are written off by the capacity the duel spent, as a broken-off
        /// attack's are (<see cref="WriteOffByCapacity"/>), the rest carry what is left of the damage,
        /// and <see cref="PendingFleetAction.HullsSettled"/> lands them whatever the ratio.</para>
        ///
        /// <para>Not for <c>src=physical</c>, whose hulls the grids already settled
        /// (<see cref="PendingFleetAction.HullsMeasured"/>), nor a bench fleet, which drew no hull from
        /// stock. A convoy whose every hull the capacity takes keeps the threshold: it has no roster
        /// to empty, and the terminal writes it off whole (and says so).</para>
        /// <para>Provenance: FAC-172.</para></summary>
        public static int SettleDuelHulls(ref PendingFleetAction fleet, double ratio, double attrition,
            string source, string cause, WorldModel world, IList<string> log)
        {
            if (ratio > HullReturnThreshold || source == SourcePhysical || fleet.IsBench)
            {
                RecordAttrition(ref fleet, attrition, cause, world, log);
                return 0;
            }
            bool isSquadron = fleet.Squadron != null && fleet.Squadron.Count > 0;
            int total; double survivorFraction;
            // Marks a squadron's casualties; a convoy is not touched until its count is set below.
            List<string> lost = WriteOffByCapacity(ref fleet, attrition, world.PrefabCatalogue,
                                                   out total, out survivorFraction);
            int survivors = total - lost.Count;
            if (!isSquadron && survivors <= 0)
            {
                RecordAttrition(ref fleet, attrition, cause, world, log);
                return 0;
            }
            if (!isSquadron) fleet.ConvoySize = survivors;
            if (survivors > 0) RecordAttrition(ref fleet, survivorFraction, cause, world, log);
            fleet.HullsSettled = true;
            if (lost.Count == 0) return 0;
            world.RecordEvent(FactionEventKind.ShipLost, fleet.ActorTag, fleet.TargetTag, fleet.TargetNodeId,
                              lost.Count, ratio);
            log.Add("SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag
                    + " prefab=" + (fleet.ShipPrefabId ?? "") + " count=" + lost.Count.ToString(CultureInfo.InvariantCulture)
                    + " from=" + (fleet.OriginNodeId ?? "?") + " to=" + (fleet.TargetNodeId ?? "?")
                    + " ratio=" + ratio.ToString("0.00", CultureInfo.InvariantCulture)
                    + " why=" + cause + " hulls=" + string.Join(",", lost.ToArray()));
            return lost.Count;
        }

        /// <summary>The ledger of an attack that stopped: marked intercepted so the cleanup pass
        /// removes it, survivors credited home through the ownership-aware landing, the dead written
        /// down as <c>SHIP_LOST ... why=&lt;why&gt;&lt;victim&gt;</c>. The survivors' count is kept on
        /// the fleet (<see cref="PendingFleetAction.HullsLeaving"/>) so the host flies their grids out
        /// of the fight rather than closing them where they stand.</summary>
        private static void SettleBrokenAttack(
            ref PendingFleetAction raid, (int si, int fi) raidRef, string victimTag, string node, long opposedDps,
            int total, List<string> lostPrefabs, string why, bool isSquadron, double survivorFraction,
            IList<FactionState> states, WorldModel world, IList<string> log)
        {
            int lost = lostPrefabs.Count;

            // The hulls that leave carry what the duel did to them home: a beaten attack's at the
            // survivor floor (fraction 0), a broken-off one's at whatever its losses did not absorb.
            if (total - lost > 0)
                RecordAttrition(ref raid, survivorFraction, why.TrimEnd(':'), world, log);

            raid.Intercepted = true;
            raid.OpposedDps = opposedDps;
            raid.HullsLeaving = Math.Max(0, total - lost);
            states[raidRef.si].PendingFleets[raidRef.fi] = raid;
            // A bench fleet drew no hull from stock, so none is credited home or written off.
            if (raid.IsBench) return;

            // Survivors home, through the same ownership-aware landing every other hull credit uses.
            int survivors = total - lost;
            if (survivors > 0)
            {
                ShipPool.Landing landing = ResolveReturnNode(world, raid, raid.OriginNodeId, raid.OriginNodeId);
                if (landing.NodeId != null)
                {
                    string redirect = landing.Reason == "nearest_owned" ? landing.RedirectSuffix : "";
                    FleetMunitions.Credit(ref raid, landing.Node, survivors, world, log);
                    if (isSquadron)
                        ReturnSquadronToNode(world, raid, landing.Node, landing.NodeId, redirect, log);
                    else
                    {
                        HullDamage.Land(world, landing.Node, landing.NodeId, raid.ShipPrefabId, survivors,
                                        raid.IntegrityOf(0), raid.LogId, raid.ActorTag, log);
                        int poolStock;
                        landing.Node.ShipStock.TryGetValue(raid.ShipPrefabId ?? "", out poolStock);
                        log.Add("SHIP_RETURNED " + raid.ActorTag + " prefab=" + (raid.ShipPrefabId ?? "")
                                + " node=" + landing.NodeId + " count=" + survivors + " stock=" + poolStock + redirect);
                    }
                }
                else
                {
                    world.RecordEvent(FactionEventKind.ShipLost, raid.ActorTag, victimTag, node, survivors, 0.0);
                    log.Add("SHIP_LOST " + raid.LogId + " " + raid.ActorTag + LossDetail(raid, 0.0, NoLandingWhy(landing))
                            + " hulls=" + survivors);
                }
            }
            if (lost > 0)
            {
                world.RecordEvent(FactionEventKind.ShipLost, raid.ActorTag, victimTag, node, lost, 0.0);
                log.Add("SHIP_LOST " + raid.LogId + " " + raid.ActorTag
                        + " prefab=" + (raid.ShipPrefabId ?? "") + " count=" + lost
                        + " from=" + (raid.OriginNodeId ?? "?") + " to=" + (raid.TargetNodeId ?? "?")
                        + " ratio=0.00 why=" + why + victimTag
                        + " hulls=" + string.Join(",", lostPrefabs.ToArray()));
            }
        }

        /// <summary>Every hull of an attacking fleet with its own DPS, in persisted order — the list
        /// <see cref="BreakAttack"/> rolls its loss series over. A prefab-times-count convoy reads as
        /// that many identical entries, which is what makes a non-squadron fleet lose hulls the same
        /// way a squadron does.</summary>
        private static List<KeyValuePair<string, double>> HullRoster(PendingFleetAction fleet, IPrefabCatalogue cat)
        {
            var hulls = new List<KeyValuePair<string, double>>();
            if (fleet.Squadron != null && fleet.Squadron.Count > 0)
            {
                IReadOnlyList<string> members = fleet.HullsToReturn;
                for (int i = 0; i < members.Count; i++)
                    hulls.Add(new KeyValuePair<string, double>(members[i], PerHullDps(cat, members[i])));
            }
            else
            {
                int n = Math.Max(1, fleet.ConvoySize);
                double per = PerHullDps(cat, fleet.ShipPrefabId);
                for (int i = 0; i < n; i++) hulls.Add(new KeyValuePair<string, double>(fleet.ShipPrefabId ?? "", per));
            }
            return hulls;
        }

        private static void ResolveRaidVsTrade(
            (int si, int fi) raidRef, List<(int si, int fi)> tradeRefs,
            IList<FactionState> states, WorldModel world, FactionTuning tuning,
            IRandom rng, IList<string> log, string meetNode = null,
            IItemPriceCatalogue prices = null, HashSet<(int si, int fi)> encountered = null)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            string node = meetNode ?? raid.TargetNodeId;

            double maxRange = 0;
            if (raid.WeaponBands != null)
                for (int i = 0; i < raid.WeaponBands.Count; i++)
                    if (raid.WeaponBands[i].RangeM > maxRange) maxRange = raid.WeaponBands[i].RangeM;
            // No known range (bands not yet populated, or a genuinely unarmed convoy) reproduces
            // today's flat baseline chance exactly (factor 1.0) — only a raider with a *known*,
            // longer-than-nominal range gets a boosted chance to run down a fleeing convoy, capped
            // so it never becomes a near-certainty.
            double rangeFactor = 1.0 + Math.Min(0.5, maxRange / 4000.0);
            double effectiveChance = Math.Min(0.98, tuning.RaidInterceptsTradeChance * rangeFactor);

            foreach (var tr in tradeRefs)
            {
                if (encountered != null && !encountered.Add(tr)) continue;   // already met a raid this pass
                PendingFleetAction trade = states[tr.si].PendingFleets[tr.fi];
                double roll = rng.NextDouble();
                if (roll < effectiveChance)
                {
                    string rollNote = " (roll=" + roll.ToString("0.00", CultureInfo.InvariantCulture)
                                    + " chance=" + effectiveChance.ToString("0.00", CultureInfo.InvariantCulture) + ")";
                    ResolveConvoyEncounter(raidRef, tr, node, 1.0, "", rollNote, states, world, tuning, rng, log, prices);
                    // A pirate the convoy repelled is out of the fight: it cannot go on to rob the next one.
                    if (states[raidRef.si].PendingFleets[raidRef.fi].Intercepted) break;
                }
                else
                {
                    world.RecordEvent(FactionEventKind.ConvoyEscaped, raid.ActorTag, trade.ActorTag, node);
                    log.Add("TRADE_ESCAPES " + trade.LogId + " " + trade.ActorTag + " evaded " + raid.LogId + " " + raid.ActorTag
                            + " @" + node + " (roll=" + roll.ToString("0.00", CultureInfo.InvariantCulture) + " chance=" + effectiveChance.ToString("0.00", CultureInfo.InvariantCulture) + ")");
                }
            }
        }

        /// <summary>
        /// Triple encounter: Guard/station defense duels the Raid first, then the surviving Raid
        /// strength (0..1, the duel's <c>attackerRatio</c>) determines the Trade's escape chance. A
        /// fully-depleted Raid cannot intercept the Trade.
        /// <paramref name="meetNode"/> overrides the log node label for mid-transit encounters.
        /// </summary>
        private static void ResolveTripleEncounter(
            (int si, int fi) raidRef, (int si, int fi) patrolRef, List<(int si, int fi)> tradeRefs,
            IList<FactionState> states, WorldModel world, FactionTuning tuning,
            IRandom rng, IList<string> log, string meetNode = null, IItemPriceCatalogue prices = null,
            HashSet<(int si, int fi)> encountered = null, List<(int si, int fi)> force = null)
        {
            PendingFleetAction raid   = states[raidRef.si].PendingFleets[raidRef.fi];
            PendingFleetAction patrol = states[patrolRef.si].PendingFleets[patrolRef.fi];
            string node = meetNode ?? raid.TargetNodeId;

            SpatialNode targetNode;
            world.Graph.Nodes.TryGetValue(node, out targetNode);
            List<WeaponBand> defenderBands = MergeBands(ForceBands(states, patrolRef, force),
                                                        targetNode != null ? targetNode.StationWeapons : null);
            long defenderFlat = CombatStrength.StationFlat(targetNode, tuning);

            DuelMunitions raidMunitions = RaidMunitions(raidRef, states, world, tuning);
            DuelMunitions forceMunitions = ForceMunitions(states, patrolRef, force,
                targetNode != null ? targetNode.StationWeapons : null, world, tuning);
            ShipDuel.Result duel = ResolveMeeting(raid.WeaponBands, defenderBands, defenderFlat, tuning, rng,
                world, states, raidRef, ForceRefs(patrolRef, force), patrol.ActorTag, targetNode,
                meetNode, log, raidMunitions, forceMunitions);
            raid = states[raidRef.si].PendingFleets[raidRef.fi];
            double raidStrength = duel.AttackerRatio;
            bool attackerDestroyed = duel.AttackerDestroyed, defenderDestroyed = duel.DefenderDestroyed;
            string turnSummary = duel.TurnSummary;
            CommitMunitions(raidMunitions, forceMunitions, states, world, log);
            long opposedDps = (long)(TotalDps(defenderBands) + defenderFlat);
            List<(int si, int fi)> defence = ForceRefs(patrolRef, force);
            int atkBrought, defBrought;
            HullsBrought(states, raidRef, defence, out atkBrought, out defBrought);
            CombatAttackResult attack;
            int breakLost = 0;

            if (duel.AttackerRepelled)
            {
                // Undecided by the grids with the Guard still standing: the raid leaves at once, and
                // intercepts none of the trades.
                attack = CombatAttackResult.Repelled;
                RepelAttack(raidRef, raid, patrol.ActorTag, node, opposedDps, raidStrength, duel.AttackerAttrition, duel.Source, defenderBands, defenderFlat,
                            states, world, tuning, log);
                log.Add("TRIPLE RAID_REPELLED " + raid.LogId + " " + raid.ActorTag + " vs " + patrol.LogId + " " + patrol.ActorTag
                        + " @" + node + " ratio=" + raidStrength.ToString("0.00", CultureInfo.InvariantCulture)
                        + " outcome=repelled src=" + duel.Source + " (" + turnSummary + ")");
                raidStrength = 0;
                CreditPactDefence(raid, patrol, states, world, tuning, log);
            }
            else if (!attackerDestroyed && duel.AttackerBrokeOff)
            {
                // The raid left the fight: it neither lands nor goes on to intercept the trades.
                int total, lost;
                BreakOffAttack(raidRef, patrol.ActorTag, node, opposedDps, duel.AttackerAttrition, states, world, log,
                               out total, out lost);
                attack = CombatAttackResult.BrokeOff;
                breakLost = lost;
                MarkStopped(states, raidRef, "broke_off", raidStrength, duel.Source);
                RecordAttackStopped(raidRef, raid, patrol.ActorTag, node, defenderBands, defenderFlat, states, world, tuning,
                                    lost, total);
                log.Add("TRIPLE RAID_BROKE_OFF " + raid.LogId + " " + raid.ActorTag + " vs " + patrol.LogId + " " + patrol.ActorTag
                        + " @" + node + " ratio=" + raidStrength.ToString("0.00", CultureInfo.InvariantCulture)
                        + " hulls_lost=" + lost + "/" + total + " src=" + duel.Source + " (" + turnSummary + ")");
                raidStrength = 0;
                CreditPactDefence(raid, patrol, states, world, tuning, log);
            }
            else if (attackerDestroyed)
            {
                // Guard/station wins — Raid defeated, hull by hull.
                int total, lost;
                BreakAttack(raidRef, patrol.ActorTag, node, opposedDps,
                            HullRoster(raid, world.PrefabCatalogue),
                            raid.Squadron != null && raid.Squadron.Count > 0,
                            states, world, tuning, rng, log, out total, out lost);
                attack = CombatAttackResult.Destroyed;
                breakLost = lost;
                MarkStopped(states, raidRef, "beaten", 0.0, duel.Source);
                PendingFleetAction interceptedRaid = states[raidRef.si].PendingFleets[raidRef.fi];
                raidStrength = 0;
                if (!raid.IsBench) RecordRaidRepelled(world, raid, patrol.ActorTag, node, lost, total);
                RecordFight(states, interceptedRaid, patrol.ActorTag, 0, true, world, tuning);
                RecordRepelledAttack(interceptedRaid, world);
                RecordCampaignOutcome(states, raidRef, CampaignOutcome.Intercepted,
                    CombatStrength.WorstBracket(raid.WeaponBands, defenderBands, null, defenderFlat, tuning), world);
                log.Add("TRIPLE GUARD_WINS " + patrol.LogId + " " + patrol.ActorTag + " vs " + raid.LogId + " " + raid.ActorTag
                        + " @" + node + " hulls_lost=" + lost + "/" + total + " src=" + duel.Source + " (" + turnSummary + ")");
                CreditPactDefence(raid, patrol, states, world, tuning, log);
            }
            else
            {
                // Raid wins through but is weakened; carry the surviving fraction into ResolveSingle.
                attack = CombatAttackResult.Landed;
                var survivor = raid;
                survivor.ForcedRatio = raidStrength;
                survivor.ForcedRatioCause = "triple_duel";
                survivor.OpposedDps = opposedDps;
                survivor.DuelTactic = duel.Tactic;
                SettleDuelHulls(ref survivor, raidStrength, duel.AttackerAttrition, duel.Source, "triple_duel", world, log);
                states[raidRef.si].PendingFleets[raidRef.fi] = survivor;
                log.Add("TRIPLE RAID_WINS_COMBAT " + raid.LogId + " " + raid.ActorTag + " vs " + patrol.LogId + " " + patrol.ActorTag
                        + " strength=" + raidStrength.ToString("0.00", CultureInfo.InvariantCulture)
                        + " @" + node + " src=" + duel.Source + " (" + turnSummary + ")");
            }
            if (defenderDestroyed)
                MarkForceDestroyed(states, patrolRef, force, raid.ActorTag, node, world, log);
            else
            {
                RecordForceAttrition(states, patrolRef, force, duel.DefenderAttrition, "triple_duel", world, log);
                if (duel.DefenderBrokeOff)
                    ForceBreaksOff(states, patrolRef, force, node, world, tuning, log);
            }
            RecordCombatOutcome(states, world, log, raidRef, defence, targetNode, node, CombatMeeting.Triple, attack, breakLost,
                defenderDestroyed, duel.DefenderBrokeOff, duel.Source, atkBrought, defBrought, patrol.ActorTag, patrol.LogId);

            // Raid vs Trade: reduced interception chance proportional to Raid's remaining strength
            if (raidStrength > 0)
            {
                double interceptThreshold = tuning.RaidInterceptsTradeChance * raidStrength;
                foreach (var tr in tradeRefs)
                {
                    if (encountered != null && !encountered.Add(tr)) continue;   // already met a raid this pass
                    PendingFleetAction trade = states[tr.si].PendingFleets[tr.fi];
                    double tradeRoll = rng.NextDouble();
                    if (tradeRoll < interceptThreshold)
                    {
                        string rollNote = " (roll=" + tradeRoll.ToString("0.00", CultureInfo.InvariantCulture) + ")";
                        ResolveConvoyEncounter(raidRef, tr, node, raidStrength, "TRIPLE ", rollNote, states, world, tuning, rng, log, prices);
                        if (states[raidRef.si].PendingFleets[raidRef.fi].Intercepted) break;
                    }
                    else
                    {
                        world.RecordEvent(FactionEventKind.ConvoyEscaped, raid.ActorTag, trade.ActorTag, node);
                        log.Add("TRIPLE TRADE_ESCAPES " + trade.LogId + " " + trade.ActorTag
                                + " @" + node + " (roll=" + tradeRoll.ToString("0.00", CultureInfo.InvariantCulture) + ")");
                    }
                }
            }
            else
            {
                foreach (var tr in tradeRefs)
                {
                    PendingFleetAction tf = states[tr.si].PendingFleets[tr.fi];
                    log.Add("TRIPLE TRADE_FREE " + tf.LogId + " " + tf.ActorTag
                            + " @" + node + " (raid defeated by patrol)");
                }
            }
        }

        // ---- mid-transit encounter resolution ---------------------------------------------------

        /// <summary>
        /// Called at the start of each tick before arrival processing. Advances
        /// <see cref="PendingFleetAction.LastResolvedSegment"/> for every in-flight convoy that has
        /// moved into a new path segment, then groups newly-reached intermediate waypoints and runs
        /// the same Patrol/Raid/Trade meeting logic as destination encounters.
        /// <para>A "meeting" fires exactly once per waypoint crossing, regardless of how many ticks
        /// the convoy spends in the same segment.</para>
        /// </summary>
        private static void ResolveMidTransitMeetings(
            IList<FactionState> states, WorldModel world, FactionTuning tuning,
            IRandom rng, IList<string> log, IItemPriceCatalogue prices = null,
            HashSet<(int si, int fi)> encountered = null)
        {
            DateTime now = world.UtcNow;
            SpatialGraph graph = world.Graph;

            // Pass 1: advance LastResolvedSegment and collect (convoy, waypointNodeId) pairs for
            // every intermediate node the convoy has just entered this tick.
            // Intermediate nodes = PathNodeIds[1 .. Length-2] (origin and destination are excluded).
            var justEntered = new List<(int si, int fi, string nodeId)>();

            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.Intercepted || f.HasArrived(now)) continue;
                    if (f.PathNodeIds == null || f.PathNodeIds.Length < 3) continue; // no intermediate nodes

                    int curSeg = f.CurrentSegmentIdx(now, graph);
                    int lastSeg = f.LastResolvedSegment;
                    if (curSeg <= lastSeg) continue; // no new segments entered

                    // Emit a meeting entry for each newly-entered intermediate waypoint.
                    // Entering segment seg means passing through PathNodeIds[seg] (except seg=0 which is the origin).
                    int firstNew = Math.Max(lastSeg + 1, 1);                     // skip origin (seg=0)
                    int lastNew  = Math.Min(curSeg, f.PathNodeIds.Length - 2);   // skip destination
                    for (int seg = firstNew; seg <= lastNew; seg++)
                        justEntered.Add((si, fi, f.PathNodeIds[seg]));

                    // Advance watermark so this segment is not re-triggered.
                    var updated = f;
                    updated.LastResolvedSegment = curSeg;
                    st.PendingFleets[fi] = updated;
                }
            }

            if (justEntered.Count < 2) return;

            // Pass 2: group by waypoint node and run meeting detection (same logic as destination).
            // Build a temporary multimap: nodeId → list of (si,fi) at that node this tick.
            var byNode = new Dictionary<string, List<(int si, int fi, ActionKind kind)>>();
            foreach (var entry in justEntered)
            {
                if (!byNode.ContainsKey(entry.nodeId))
                    byNode[entry.nodeId] = new List<(int si, int fi, ActionKind kind)>();
                PendingFleetAction f = states[entry.si].PendingFleets[entry.fi];
                if (!f.Intercepted)
                    byNode[entry.nodeId].Add((entry.si, entry.fi, f.Kind));
            }

            foreach (var kv in byNode)
            {
                string meetNode = kv.Key;
                var fleets = kv.Value;
                if (fleets.Count < 2) continue;

                // Find raids, patrols, trades at this waypoint
                foreach (var raidEntry in fleets)
                {
                    if (raidEntry.kind != ActionKind.Raid && raidEntry.kind != ActionKind.Retaliate && raidEntry.kind != ActionKind.Conquer) continue;
                    PendingFleetAction raid = states[raidEntry.si].PendingFleets[raidEntry.fi];
                    if (raid.Intercepted) continue;
                    // A raid already holding a convoy at one node this pass cannot also be at
                    // another: it stays where it was pinned, and this waypoint's meeting is skipped.
                    if (raid.CurrentStopKind == FleetStopKind.Intercept) continue;

                    var raidKey = (raidEntry.si, raidEntry.fi);

                    (int si, int fi) patrolRef = (-1, -1);
                    var tradeRefs = new List<(int si, int fi)>();

                    foreach (var other in fleets)
                    {
                        if (other.si == raidEntry.si && other.fi == raidEntry.fi) continue;
                        PendingFleetAction ot = states[other.si].PendingFleets[other.fi];
                        if (ot.Intercepted) continue;
                        if (states[other.si].Tag == states[raidEntry.si].Tag) continue; // same faction

                        if (PendingFleetAction.IsDefenceKind(other.kind) && patrolRef.si < 0)
                            patrolRef = (other.si, other.fi);
                        else if (other.kind == ActionKind.Trade && ot.CurrentStopKind != FleetStopKind.Intercept
                                 && !AlreadyMet(ot, raid))
                            tradeRefs.Add((other.si, other.fi));
                    }

                    bool hasPatrol = patrolRef.si >= 0;
                    bool hasTrade  = tradeRefs.Count > 0;

                    if (hasPatrol && hasTrade)
                        ResolveTripleEncounter(raidKey, patrolRef, tradeRefs, states, world, tuning, rng, log, meetNode, prices, encountered);
                    else if (hasPatrol)
                        ResolvePatrolVsRaid(raidKey, patrolRef, states, world, tuning, rng, log, meetNode);
                    else if (hasTrade)
                        ResolveRaidVsTrade(raidKey, tradeRefs, states, world, tuning, rng, log, meetNode, prices, encountered);
                }
            }
        }


        // ---- loot helper (Change 4) -------------------------------------------------------------

        /// <summary>Transfer <paramref name="strengthFraction"/> × <see cref="FactionTuning.RaidLootFraction"/>
        /// of the trade's item cargo to the raider's origin node inventory. Returns the credit-equivalent
        /// value of items taken (for log lines). Falls back to a credit transfer when the convoy has no
        /// item cargo (empty convoy case).</summary>
        private static long LootCargo(
            ref PendingFleetAction trade, double strengthFraction,
            PendingFleetAction raid, WorldModel world, FactionTuning tuning,
            IItemPriceCatalogue prices = null)
        {
            double fraction = tuning.RaidLootFraction * strengthFraction;
            if (trade.Cargo != null && trade.Cargo.Count > 0)
            {
                long lootValue = 0;
                // What the raider takes leaves the convoy: a robbed convoy that survives (FAC-306)
                // carries on with the remainder, so the loot must not be a copy.
                var keys = new List<string>(trade.Cargo.Keys);
                foreach (string key in keys)
                {
                    long held = trade.Cargo[key];
                    long taken = (long)(held * fraction);
                    if (taken <= 0) continue;
                    var kv = new KeyValuePair<string, long>(key, held);
                    if (taken >= held) trade.Cargo.Remove(key); else trade.Cargo[key] = held - taken;
                    if (raid.OriginNodeId != null)
                        world.AddNodeInventory(raid.OriginNodeId, kv.Key, taken);  // kv.Key is SubTypeId
                    else
                        world.AddSubTypeInventory(raid.ActorTag, kv.Key, taken);
                    long unitValue = prices != null ? prices.BasePrice(kv.Key) : 0;
                    if (unitValue <= 0)
                    {
                        ItemKind ik;
                        unitValue = world.TryGetInventoryItemKind(kv.Key, out ik) ? tuning.FallbackFor(ik) : 1;
                    }
                    lootValue += ItemCredits.For(kv.Key, taken, unitValue);
                }
                if (lootValue > 0) return lootValue;
                // All item quantities rounded to zero (tiny cargo) — fall through to credit fallback
            }
            // Fallback: no item cargo or zero-loot rounding — credit-value loot
            long creditLoot = (long)(trade.CargoValue * fraction);
            if (creditLoot > 0)
            {
                world.Earn(raid.ActorTag, creditLoot);
                trade.CargoValue -= creditLoot;
            }
            return creditLoot;
        }

        // ---- individual resolution --------------------------------------------------------------

        private static bool AdvanceToNextStop(FactionState owner, int fi, PendingFleetAction fleet,
            WorldModel world, FactionTuning tuning, IList<string> log)
        {
            FleetStop next = fleet.RemainingStops[0];
            bool immediate = fleet.ImmediateItinerary;
            // A miner leaves its dwell from wherever on the ring it has got to, not from the deposit.
            bool leavingWander = MineWander.Applies(fleet);
            Vec3 leaveFromKm = leavingWander ? MineWander.PositionAt(fleet, world.UtcNow, tuning) : fleet.TargetPosKm;

            // Later legs are planned when they are reached, not at original dispatch, so the persisted
            // world catalogue is consulted again before each semantic hop. Skipped once the fleet is
            // already flying a generated clearance chain (its stops are the plan) and for a dwell
            // stop, which happens at the node the fleet is already parked on.
            SpatialNode nextStopNode;
            if (!immediate && fleet.CurrentStopKind != FleetStopKind.Navigation
                && next.Kind != FleetStopKind.Navigation && !IsDwellStop(next.Kind)
                && next.NodeId != null && world.Graph.Nodes.TryGetValue(next.NodeId, out nextStopNode))
            {
                PlanetRoutePlan route = PlanRoute(fleet, leaveFromKm, nextStopNode.Position,
                    world, tuning, ":" + next.Kind, log);
                if (route.IsClear && route.Waypoints.Count > 0)
                {
                    // One leg, one price: the whole clearance polyline, charged here rather than
                    // chord by chord as the recursion below walks it.
                    var polylineKm = new List<Vec3>(route.Waypoints.Count + 2);
                    polylineKm.Add(leaveFromKm);
                    for (int i = 0; i < route.Waypoints.Count; i++) polylineKm.Add(route.Waypoints[i]);
                    polylineKm.Add(nextStopNode.Position);
                    // The fleet is parked at TargetNodeId, which is the stop this chain leaves from
                    // and therefore what mode 2 draws it out of.
                    SpatialNode departingFrom = null;
                    if (fleet.TargetNodeId != null)
                        world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out departingFrom);
                    FuelMeter.PriceLeg(world, tuning, fleet, polylineKm, log, null, departingFrom);

                    List<FleetStop> routedStops = NavigationStops(route, 0);
                    routedStops.AddRange(fleet.RemainingStops);
                    fleet.RemainingStops = routedStops;
                    fleet.RouteDistanceKm = route.DistanceKm;
                    fleet.RouteCoveredKm = 0;
                    fleet.RouteDepartedUtc = world.UtcNow;
                    owner.PendingFleets[fi] = fleet;
                    return AdvanceToNextStop(owner, fi, fleet, world, tuning, log);
                }
            }

            next = fleet.RemainingStops[0];
            fleet.RemainingStops.RemoveAt(0);

            SpatialNode currentNode = null;
            SpatialNode nextNode = null;
            bool hasCurrentNode = fleet.TargetNodeId != null
                && world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out currentNode);
            bool hasNextNode = next.NodeId != null
                && world.Graph.Nodes.TryGetValue(next.NodeId, out nextNode);
            if ((fleet.TargetNodeId != null && !hasCurrentNode)
                || (next.Kind != FleetStopKind.Navigation && !hasNextNode)
                || (next.Kind == FleetStopKind.Navigation && !next.HasPosition && next.NodeId == null))
            {
                // A deleted node must not strand a live convoy forever. Finish the current mission
                // as a legacy single-leg fleet; its standard lifecycle below returns any ship.
                log.Add("FLEET_ITINERARY_ABORTED " + fleet.LogId + " missing_stop=" + (next.NodeId ?? "?")
                        + " — completing current leg");
                return false;
            }

            DateTime departed = world.UtcNow;
            Vec3 currentPos = leavingWander ? leaveFromKm : hasCurrentNode ? currentNode.Position : fleet.TargetPosKm;
            Vec3 nextPos = hasNextNode ? nextNode.Position : next.PositionKm;
            string[] path = immediate || !hasCurrentNode || !hasNextNode
                ? null : GraphPathfinder.FindPath(world.Graph, currentNode.Id, nextNode.Id);
            // The leg is flown straight from currentPos to nextPos, so that is what it is billed;
            // path only carries the encounter waypoints. A clearance chain is billed below instead,
            // against its own polyline.
            double distanceKm = currentPos.DistanceTo(nextPos);
            // Mine and Engage are the stop kinds whose arrival time is NOT distance-derived -- each
            // is a stationary dwell for a real precomputed duration (Mine: gather + overburden from
            // the requested volume and the drill's rate; Engage: RaidDwellSeconds). Either at the
            // node the fleet already sits on, so without this bypass they would resolve instantly
            // like any other zero-distance leg, which is exactly the gap it closes.
            // A leg belonging to a live clearance chain is billed its slice of the whole polyline's
            // single acceleration profile. Charging each short arc chord a full accelerate-and-brake
            // trapezoid would make a smooth detour take several times longer than its routed ETA.
            // RouteCoveredKm is the distance from the route's start to where this leg begins.
            bool onRoute = fleet.RouteDistanceKm > 0 && fleet.RouteCoveredKm < fleet.RouteDistanceKm
                           && !immediate && tuning.FleetSpeedKmPerMin > 0
                           && !IsDwellStop(next.Kind);

            DateTime arrival;
            if (IsDwellStop(next.Kind))
                arrival = departed.AddSeconds(next.DurationSeconds);
            else if (immediate || tuning.FleetSpeedKmPerMin <= 0)
                arrival = departed;
            else if (onRoute)
            {
                double coveredAtArrivalKm = Math.Min(fleet.RouteDistanceKm, fleet.RouteCoveredKm + distanceKm);
                arrival = fleet.RouteDepartedUtc.AddMinutes(fleet.LegZones != null
                    ? tuning.TransitElapsedMinsAtDistance(fleet.LegZones, coveredAtArrivalKm)
                    : tuning.TransitElapsedMinsAtDistance(fleet.RouteDistanceKm, coveredAtArrivalKm));
                if (arrival < departed) arrival = departed; // fell behind the schedule; never run backwards
                fleet.RouteCoveredKm = coveredAtArrivalKm;
            }
            else
            {
                // A later semantic leg crosses its own ground, so it gets its own tier table.
                fleet.LegZones = world.ZonesFor(currentPos, nextPos);
                double tieredMins = tuning.TransitTimeMins(fleet.LegZones);
                arrival = departed.AddMinutes(tieredMins > 0 ? tieredMins : tuning.TransitTimeMins(distanceKm));
            }

            // Each later semantic leg is priced as it is reached. A dwell flies nowhere, and a leg
            // inside a live clearance chain was priced whole when the chain was planned -- onRoute
            // is exactly "this fleet is inside a chain that has already been charged".
            if (!onRoute && !immediate && !IsDwellStop(next.Kind))
                FuelMeter.PriceLeg(world, tuning, fleet, currentPos, nextPos, log, null,
                                   hasCurrentNode ? currentNode : null);

            if (!onRoute)
            {
                fleet.RouteDistanceKm = 0;
                fleet.RouteCoveredKm = 0;
                fleet.RouteDepartedUtc = default(DateTime);
            }

            fleet.OriginNodeId = hasCurrentNode ? currentNode.Id : null;
            // OriginNodeId is per-leg and legitimately null on a leg that starts at a clearance
            // waypoint; the hull's home is not. The last REAL node the fleet departed from is what
            // the terminal credits the hull to (for Mine, the dwell site).
            if (hasCurrentNode) fleet.ReturnNodeId = currentNode.Id;
            fleet.TargetNodeId = hasNextNode ? nextNode.Id : null;
            fleet.TargetTag = hasNextNode ? nextNode.OwnerFactionTag : null;
            fleet.OriginPosKm = currentPos;
            fleet.TargetPosKm = nextPos;
            fleet.DepartedUtc = departed;
            fleet.EstimatedArrivalUtc = arrival;
            fleet.CurrentStopKind = next.Kind;
            fleet.PickupItemKey = next.Kind == FleetStopKind.Pickup ? next.PickupItemKey : null;
            fleet.PickupQuantity = next.Kind == FleetStopKind.Pickup ? next.PickupQuantity : 0;
            fleet.MineWeights = next.Kind == FleetStopKind.Mine ? next.MineWeights : null;
            fleet.MineTargetLiters = next.Kind == FleetStopKind.Mine ? next.MineTargetLiters : 0;
            fleet.MineWanderUp = next.Kind == FleetStopKind.Mine ? MineWander.UpAt(world.Planets, nextPos) : null;
            fleet.PathNodeIds = path;
            fleet.LastResolvedSegment = -1;
            fleet.Resolved = false;
            // A new leg is a new fight: what the last dwell's grids measured is spent.
            fleet.Fight = null;
            fleet.FightDamageDealt = 0;
            fleet.FightDamageTaken = 0;
            fleet.FightZoneHits = 0;
            owner.PendingFleets[fi] = fleet;

            // The engagement dwell is an arrival, not a departure -- the fleet is not going anywhere,
            // it has reached what it came to attack. It gets its own line and its own event because
            // this is the moment a defender can still act on: RAID_ENGAGED opens the window,
            // RAID_RESOLVED or an intercept line closes it, and the gap between them is the dwell.
            if (next.Kind == FleetStopKind.Engage)
            {
                // A Reinforce holds the same dwell but is not an attack: it opens no raid, so it
                // writes no RaidEngaged event and gets its own verb.
                bool attacking = EngagesOnArrival(fleet.Kind);
                if (attacking)
                    world.RecordEvent(FactionEventKind.RaidEngaged, fleet.ActorTag, fleet.TargetTag,
                                      fleet.TargetNodeId, (long)next.DurationSeconds);
                log.Add((attacking ? "RAID_ENGAGED " : "REINFORCE_ENGAGED ") + fleet.LogId + " " + fleet.ActorTag + " @ "
                        + (fleet.TargetNodeId ?? "?") + " kind=" + fleet.Kind
                        + " dwell=" + next.DurationSeconds.ToString("0", CultureInfo.InvariantCulture) + "s"
                        + " until=" + arrival.ToString("HH:mm:ss"));
            }
            // Clearance waypoints are transit milestones, not events. One FLEET_ROUTED line per plan
            // already describes them, and the per-faction log keeps only its most recent entries.
            else if (next.Kind != FleetStopKind.Navigation)
                log.Add("FLEET_DEPARTED " + fleet.LogId + " " + fleet.ActorTag + " "
                        + (hasCurrentNode ? currentNode.Id : "[navigation]")
                        + " -> " + (hasNextNode ? nextNode.Id : "[navigation]") + " stop=" + next.Kind
                        + " eta=" + arrival.ToString("HH:mm:ss"));
            return true;
        }

        /// <summary>Engage dwells still queued on <paramref name="fleet"/> — how much siege it has
        /// left. Zero means this resolution is the one it leaves on.</summary>
        private static int CountRemainingEngages(PendingFleetAction fleet)
        {
            if (fleet.RemainingStops == null) return 0;
            int n = 0;
            for (int i = 0; i < fleet.RemainingStops.Count; i++)
                if (fleet.RemainingStops[i].Kind == FleetStopKind.Engage) n++;
            return n;
        }

        /// <summary>Ends the siege early when holding station has stopped being worth it, by dropping
        /// every Engage dwell still queued and leaving the return leg. Returns how many dwells were
        /// dropped, which is what the log line reports as the siege being cut short.
        ///
        /// <para>Three reasons, and each is a case where another dwell buys the attacker nothing:
        /// the squadron is down to half strength and will be written off on return anyway; the node
        /// is already at or under <see cref="FactionTuning.ConquerHoldCeiling"/>, so there is nothing
        /// left to strip and the campaign's next dispatch is the Conquer that finishes it; or the
        /// node has changed hands and the fleet is now besieging someone it was not sent to
        /// fight.</para></summary>
        private static int DropSiegeIfSpent(ref PendingFleetAction fleet, WorldModel world,
            FactionTuning tuning, double ratio, string victimTag)
        {
            int remaining = CountRemainingEngages(fleet);
            if (remaining <= 0) return 0;

            // Out of ammunition is a fourth reason: another dwell is a fight with nothing to fire.
            bool spent = ratio <= 0.5 || FleetMunitions.IsEmpty(fleet);
            if (!spent && fleet.TargetNodeId != null)
            {
                SpatialNode target;
                if (world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out target))
                    spent = target.HoldStrength <= tuning.ConquerHoldCeiling
                         || (victimTag != null && target.OwnerFactionTag != victimTag);
            }
            if (!spent) return 0;

            for (int i = fleet.RemainingStops.Count - 1; i >= 0; i--)
                if (fleet.RemainingStops[i].Kind == FleetStopKind.Engage)
                    fleet.RemainingStops.RemoveAt(i);
            return remaining;
        }

        /// <summary>Why an attack that resolved and is leaving goes: the first of
        /// <see cref="DropSiegeIfSpent"/>'s reasons that holds, ammunition first as the most specific,
        /// or "the raid is done" when it simply had no dwell left. Worded for the player.</summary>
        private static string WithdrawalReason(PendingFleetAction fleet, WorldModel world, FactionTuning tuning,
            double ratio, string victimTag)
        {
            if (FleetMunitions.IsEmpty(fleet)) return "out of ammunition";
            if (ratio <= 0.5) return "worn down";
            SpatialNode target;
            if (fleet.TargetNodeId != null && world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out target))
            {
                if (target.HoldStrength <= tuning.ConquerHoldCeiling) return "the node's hold is spent";
                if (victimTag != null && target.OwnerFactionTag != victimTag) return "the node changed hands";
            }
            return "the raid is done";
        }

        /// <summary>The siege's state as one suffix on the resolution line: what is still to come, or
        /// what was given up. Absent entirely for a single-dwell raid, so a tuning that turns the
        /// siege off leaves the line exactly as it was.</summary>
        private static string SiegeSuffix(PendingFleetAction fleet, int droppedCycles, bool leaving)
        {
            int left = CountRemainingEngages(fleet);
            if (droppedCycles > 0)
                return " siege_broken_off dropped=" + droppedCycles + (FleetMunitions.IsEmpty(fleet) ? " out_of_ammo" : "");
            if (!leaving) return " siege_holds cycles_left=" + left;
            return "";
        }

        /// <summary>Writes what became of an attack back onto the campaign objective that ordered
        /// it, so the Campaigns view can tell a strike from a squadron that never arrived. A fleet
        /// flown under no objective -- every brain-driven sortie outside a campaign -- matches
        /// nothing and records nothing.</summary>
        private static void RecordCampaignOutcome(IList<FactionState> states, (int si, int fi) fleetRef,
            CampaignOutcome outcome, CombatStrength.DuelBracket bracket, WorldModel world)
        {
            FactionState actor = states[fleetRef.si];
            PendingFleetAction fleet = actor.PendingFleets[fleetRef.fi];
            CampaignObjective objective = OffensiveCampaigns.FindByDispatch(actor, fleet.FleetId);
            if (objective == null) return;
            objective.LastOutcome = outcome;
            objective.LastOutcomeUtc = world.UtcNow;
            objective.LastOutcomeRangeM = bracket.RangeM;
            objective.LastOutcomeOwnDps = bracket.AttackerDps;
            objective.LastOutcomeOpposedDps = bracket.DefenderDps;
            objective.LastOutcomeHullsTotal = fleet.HullCount;
            objective.LastOutcomeHullsLost = fleet.Squadron != null && fleet.Squadron.Count > 0
                ? Math.Max(0, fleet.Squadron.Count - fleet.HullsToReturn.Count)
                : 0;
        }

        /// <summary>Files the fight to <see cref="FightLedger"/> at an attacking fleet's terminal —
        /// the intercept that destroys it or the resolution that lands it — keyed on the MISSION
        /// target's node and region even when the duel was fought at a mid-transit waypoint, so a
        /// campaign against a region finds every raid it sent there.</summary>
        private static void RecordFight(IList<FactionState> states, PendingFleetAction attacker, string defenderTag,
            double ratio, bool intercepted, WorldModel world, FactionTuning tuning)
        {
            if (!BenchScenario.FilesFight(attacker)) return;
            SpatialNode targetNode = null;
            if (attacker.TargetNodeId != null) world.Graph.Nodes.TryGetValue(attacker.TargetNodeId, out targetNode);
            FightLedger.Record(states, attacker, defenderTag, targetNode, ratio, intercepted, attacker.OpposedDps,
                world.UtcNow, tuning.FightHistoryCap);
        }

        /// <summary>Files the diplomatic incident for an attack destroyed before it landed. Called
        /// at every intercept terminal beside <see cref="RecordFight"/>: the ledger records who
        /// fought whom, this records that an attack happened at all.
        ///
        /// <para>The victim is the owner of the MISSION target, read from
        /// <see cref="PendingFleetAction.TargetNodeId"/> — never the node the fight took place at.
        /// A mid-transit meeting is resolved at a waypoint the attacker was only passing through,
        /// and its owner is a bystander; the faction that was attacked is the one the fleet was
        /// on its way to hit.</para></summary>
        private static void RecordRepelledAttack(PendingFleetAction attacker, WorldModel world)
        {
            if (attacker.IsBench) return;
            SpatialNode target;
            if (attacker.TargetNodeId == null
                || !world.Graph.Nodes.TryGetValue(attacker.TargetNodeId, out target)) return;
            world.RecordRepelledAttack(attacker.ActorTag, target.OwnerFactionTag,
                                       attacker.TargetNodeId, attacker.Kind);
        }

        /// <summary>Whether a squadron's surviving hulls come home despite a failed mission. A
        /// squadron credits back what physically survived, so a fleet whose grid was destroyed,
        /// captured or stalled still lands the members that were not lost -- <see
        /// cref="PendingFleetAction.MarkHullLost"/> has already taken the casualties out of
        /// <see cref="PendingFleetAction.HullsToReturn"/>, so a one-hull squadron whose only grid
        /// died has nothing left in it and credits nothing. The bookkeeping causes are excluded: a
        /// dedup-dismissed or sibling-dismissed entry never had grids to survive.</summary>
        /// <summary>Whether the hulls <paramref name="fleet"/> carries were settled one by one — by the
        /// grids (<see cref="PendingFleetAction.HullsMeasured"/>) or by a duel's capacity write-off
        /// (<see cref="PendingFleetAction.HullsSettled"/>) — so its terminal lands them whatever its
        /// ratio. Not a single-grid fleet whose grid was lost afterwards: it has nothing left.</summary>
        private static bool LandsWhatItCarries(PendingFleetAction fleet)
        {
            bool isSquadron = fleet.Squadron != null && fleet.Squadron.Count > 0;
            return (fleet.HullsMeasured || fleet.HullsSettled) && (isSquadron || !fleet.IsPhysicalLoss);
        }

        private static bool ReturnsSurvivingHulls(PendingFleetAction fleet)
        {
            return fleet.Squadron != null && fleet.Squadron.Count > 0 && fleet.IsPhysicalLoss;
        }

        /// <summary>Lands each of <paramref name="fleet"/>'s <see cref="PendingFleetAction.HullsToReturn"/>
        /// at <paramref name="node"/> -- into its pool, or into repair when that hull is damaged -- and
        /// answers how many landed.</summary>
        private static int ReturnHulls(WorldModel world, PendingFleetAction fleet, SpatialNode node, string nodeId,
                                       IList<string> log)
        {
            IReadOnlyList<string> hulls = fleet.HullsToReturn;
            for (int i = 0; i < hulls.Count; i++)
                HullDamage.Land(world, node, nodeId, hulls[i], 1, fleet.IntegrityOf(i), fleet.LogId, fleet.ActorTag, log);
            return hulls.Count;
        }

        /// <summary>Credits a squadron's <see cref="PendingFleetAction.HullsToReturn"/> to
        /// <paramref name="returnNode"/> and logs one SHIP_RETURNED line per distinct prefab.
        /// <paramref name="redirectSuffix"/> is the landing helper's redirect note, appended as on every
        /// other credit line. <c>count=</c> stays the per-prefab tally it has always been; <c>hulls_returned=</c> is the
        /// whole-squadron total against <see cref="PendingFleetAction.HullCount"/>, so a partial
        /// return reads as one on the line rather than having to be summed across prefabs. Answers
        /// how many hulls were credited.</summary>
        private static int ReturnSquadronToNode(WorldModel world, PendingFleetAction fleet, SpatialNode returnNode,
                                                string returnNodeId, string redirectSuffix, IList<string> log)
        {
            IReadOnlyList<string> hulls = fleet.HullsToReturn;
            var counts = new Dictionary<string, int>();
            int repairing = 0;
            for (int i = 0; i < hulls.Count; i++)
            {
                repairing += HullDamage.Land(world, returnNode, returnNodeId, hulls[i], 1, fleet.IntegrityOf(i),
                                             fleet.LogId, fleet.ActorTag, log);
                int c;
                counts.TryGetValue(hulls[i], out c);
                counts[hulls[i]] = c + 1;
            }
            foreach (var kv in counts)
            {
                int poolStock;
                returnNode.ShipStock.TryGetValue(kv.Key, out poolStock);
                log.Add("SHIP_RETURNED " + fleet.ActorTag + " prefab=" + kv.Key
                        + " node=" + returnNodeId + " count=" + kv.Value + " stock=" + poolStock
                        + " hulls_returned=" + hulls.Count + "/" + fleet.HullCount
                        + (repairing > 0 ? " repairing=" + repairing : "")
                        + (redirectSuffix ?? ""));
            }
            return hulls.Count;
        }

        /// <summary>The <see cref="PendingFleetAction.ForcedRatioCause"/> the host writes on a fleet
        /// entry it dismissed as a duplicate of another. Resolved by <see cref="RecallToOrigin"/>.</summary>
        public const string SiblingDismissCause = "sibling_dismiss";

        /// <summary>The <see cref="PendingFleetAction.ForcedRatioCause"/> the host writes on a Pickup
        /// arrival that waited out its station queue with no other node to collect from. Resolved by
        /// <see cref="RecallToOrigin"/>, like a dismissed duplicate: the errand is given up, the hull
        /// and whatever it already carries go home.</summary>
        public const string QueueGiveUpCause = "traffic_give_up";

        /// <summary>Ends a dismissed duplicate without its mission: every hull it drew lands back at
        /// its origin (or, when the faction no longer owns that, wherever <see cref="ShipPool.ResolveLanding"/>
        /// sends it) and whatever its hold carries is unloaded at the same node. Nothing about the
        /// mission is applied -- no delivery, no effect, no loss event. Each entry of a pair was
        /// dispatched with a hull and cargo of its own, so dismissing one must not write either off.
        /// Answers how many hulls landed.</summary>
        public static int RecallToOrigin(ref PendingFleetAction fleet, WorldModel world, IList<string> log)
        {
            ShipPool.Landing landing = ResolveReturnNode(world, fleet, fleet.ReturnNodeId ?? fleet.OriginNodeId,
                                                        fleet.OriginNodeId);
            bool isSquadron = fleet.Squadron != null && fleet.Squadron.Count > 0;
            int hulls = isSquadron ? fleet.HullsToReturn.Count : Math.Max(1, fleet.ConvoySize);
            if (landing.NodeId == null)
            {
                world.RecordEvent(FactionEventKind.ShipLost, fleet.ActorTag, fleet.TargetTag,
                                  fleet.TargetNodeId, hulls, 0.0);
                log.Add("SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag
                        + LossDetail(fleet, 0.0, NoLandingWhy(landing)));
                return 0;
            }

            long units = 0;
            if (fleet.Cargo != null)
                foreach (var kv in fleet.Cargo)
                    if (kv.Value > 0)
                    {
                        world.AddNodeInventory(landing.NodeId, kv.Key, kv.Value);
                        units += kv.Value;
                    }

            if (isSquadron)
                ReturnHulls(world, fleet, landing.Node, landing.NodeId, log);
            else
                HullDamage.Land(world, landing.Node, landing.NodeId, fleet.ShipPrefabId, hulls,
                                fleet.IntegrityOf(0), fleet.LogId, fleet.ActorTag, log);
            FleetMunitions.Credit(ref fleet, landing.Node, hulls, world, log);

            log.Add("FLEET_RECALLED " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                    + " node=" + landing.NodeId
                    + " hulls=" + hulls.ToString(CultureInfo.InvariantCulture)
                    + (string.IsNullOrEmpty(fleet.ShipPrefabId) ? "" : " prefab=" + fleet.ShipPrefabId)
                    + " cargo=" + units.ToString(CultureInfo.InvariantCulture)
                    + " why=" + (fleet.ForcedRatioCause ?? "recall")
                    + landing.RedirectSuffix);
            return hulls;
        }

        /// <summary>The terminal of an itinerary's <see cref="FleetStopKind.Return"/> leg — the way
        /// home for every kind with <see cref="RequiresReturnLeg"/> true. Cargo is delivered to the
        /// home node when it still exists; the hull is credited through the same ownership-aware
        /// resolution every other hull terminal uses, so a home node abandoned or removed during the
        /// return leg no longer parks the hull on someone else's ground or drops it in silence.
        /// A fleet the hull-return threshold writes off logs <c>SHIP_LOST</c> and records
        /// <see cref="FactionEventKind.ShipLost"/>, as the one-way terminal does.
        /// <para>Provenance: FAC-278; the write-off line, FAC-172.</para></summary>
        private static void FinalizeReturn(PendingFleetAction fleet, double ratio, WorldModel world, IList<string> log)
        {
            SpatialNode home;
            if (fleet.TargetNodeId != null && world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out home)
                && fleet.Cargo != null)
                foreach (var kv in fleet.Cargo)
                    if (kv.Value > 0) world.AddNodeInventory(home.Id, kv.Key, kv.Value);

            bool isSquadron = fleet.Squadron != null && fleet.Squadron.Count > 0;
            int hullsReturned = 0;
            bool shipReturned = false;
            string redirect = "";
            // A completed mission lands every hull it still has; a squadron that failed through a
            // physical loss still lands the members that were not shot down, and so does a fleet
            // whose fight the grids decided, or whose duel wrote its losses off hull by hull: the
            // hulls it carries are the ones that survived.
            bool measured = LandsWhatItCarries(fleet);
            if (ratio > HullReturnThreshold || ReturnsSurvivingHulls(fleet) || measured)
            {
                ShipPool.Landing landing = ResolveReturnNode(world, fleet, fleet.TargetNodeId,
                                                            fleet.ReturnNodeId ?? fleet.OriginNodeId);
                if (landing.NodeId != null)
                {
                    if (isSquadron)
                        hullsReturned = ReturnHulls(world, fleet, landing.Node, landing.NodeId, log);
                    else
                        HullDamage.Land(world, landing.Node, landing.NodeId, fleet.ShipPrefabId, Math.Max(1, fleet.ConvoySize),
                                        fleet.IntegrityOf(0), fleet.LogId, fleet.ActorTag, log);
                    FleetMunitions.Credit(ref fleet, landing.Node,
                                          isSquadron ? hullsReturned : fleet.HullCount, world, log);
                    shipReturned = (ratio > HullReturnThreshold || measured) && (!isSquadron || hullsReturned > 0);
                    // Measured against TargetNodeId, not the helper's PriorNodeId: this line always
                    // prints "-> <TargetNodeId>", so that is the node it would have named even when
                    // the node itself is gone from the graph.
                    if (!string.Equals(landing.NodeId, fleet.TargetNodeId, StringComparison.Ordinal))
                        redirect = " redirected_from=" + (fleet.TargetNodeId ?? "?") + " why=" + landing.Reason;
                }
                else
                {
                    int lost = isSquadron ? fleet.HullsToReturn.Count : Math.Max(1, fleet.ConvoySize);
                    if (lost > 0)
                    {
                        world.RecordEvent(FactionEventKind.ShipLost, fleet.ActorTag, fleet.TargetTag,
                                          fleet.TargetNodeId, lost, ratio);
                        log.Add("SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag
                                + LossDetail(fleet, ratio, NoLandingWhy(landing)));
                    }
                }
            }
            else
            {
                // Written off by the hull-return threshold. Logged and recorded as the one-way
                // terminal's write-off is, so the loss names its hull, its leg and its cause.
                int lost = isSquadron ? fleet.HullsToReturn.Count : Math.Max(1, fleet.ConvoySize);
                if (lost > 0)
                {
                    world.RecordEvent(FactionEventKind.ShipLost, fleet.ActorTag, fleet.TargetTag,
                                      fleet.TargetNodeId, lost, ratio);
                    log.Add("SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag + LossDetail(fleet, ratio)
                            + (isSquadron
                               ? " hulls_returned=0/" + fleet.HullCount.ToString(CultureInfo.InvariantCulture)
                               : ""));
                }
            }

            log.Add("FLEET_RETURNED " + fleet.LogId + " " + fleet.ActorTag + " -> "
                    + (fleet.TargetNodeId ?? "?") + " ratio=" + ratio.ToString("0.00", CultureInfo.InvariantCulture)
                    + (fleet.Cargo != null && fleet.Cargo.Count > 0 ? " cargo_returned" : "")
                    + (shipReturned ? " ship_returned" : " ship_lost")
                    + (isSquadron
                       ? " hulls_returned=" + hullsReturned.ToString(CultureInfo.InvariantCulture)
                         + "/" + fleet.HullCount.ToString(CultureInfo.InvariantCulture)
                       : "")
                    + redirect);
        }

        /// <summary>Resolves which prefab in this fleet is actually carrying the loot — the sole
        /// hull for a solo dispatch, or the one cargo-suitable member of a mixed Raid/Retaliate/
        /// Conquer squadron. Null when unresolvable (no squadron member is cargo-suitable), which
        /// callers treat the same as "no mass catalogue wired" — no mass constraint applied rather
        /// than blocking looting outright.</summary>
        private static string ResolveCargoPrefabId(PendingFleetAction fleet, IPrefabCatalogue cat)
        {
            if (fleet.Squadron == null || fleet.Squadron.Count == 0) return fleet.ShipPrefabId;
            foreach (string pid in fleet.Squadron)
                if (ShipPool.IsCargoSuitable(cat, pid)) return pid;
            return null;
        }

        /// <summary>The raiders' tank room per gas key for the leg that carries the loot away: from
        /// the raided node to the next stop when the itinerary continues, otherwise home.</summary>
        private static Func<string, long> GasTankRoomForLoot(PendingFleetAction fleet, WorldModel world,
            FactionTuning tuning, bool returning)
        {
            Vec3 fromKm = fleet.TargetPosKm;
            Vec3 toKm = fleet.OriginPosKm;
            if (returning)
            {
                FleetStop next = fleet.RemainingStops[0];
                SpatialNode nextNode;
                if (next.NodeId != null && world.Graph.Nodes.TryGetValue(next.NodeId, out nextNode))
                    toKm = nextNode.Position;
                else if (next.HasPosition)
                    toKm = next.PositionKm;
            }
            return key => GasCarriage.RoomLitres(world, tuning, fleet, key, fromKm, toKm);
        }

        private static long CollectNodeLoot(ref PendingFleetAction fleet, WorldModel world, double ratio,
            FactionTuning tuning, IItemPriceCatalogue prices, IItemVolumeCatalogue itemVolumes,
            IItemMassCatalogue itemMasses = null)
        {
            string cargoPrefabId = ResolveCargoPrefabId(fleet, world.PrefabCatalogue);

            bool returning = fleet.RemainingStops != null && fleet.RemainingStops.Count > 0;
            Func<string, long> gasTankRoom = GasTankRoomForLoot(fleet, world, tuning, returning);
            if (!returning)
                return world.RaidLootNodeInventory(fleet.OriginNodeId, fleet.TargetNodeId, ratio,
                    fleet.CargoCapacityLiters, prices, itemVolumes, itemMasses, cargoPrefabId, gasTankRoom);

            bool massTrimmed;
            Dictionary<string, long> taken = world.TakeRaidLootNodeInventory(fleet.TargetNodeId, ratio,
                fleet.CargoCapacityLiters, prices, itemVolumes, itemMasses, cargoPrefabId, out massTrimmed,
                gasTankRoom);
            if (massTrimmed) fleet.CargoMassTrimmed = true;
            if (taken == null) return 0;
            if (fleet.Cargo == null) fleet.Cargo = new Dictionary<string, long>();

            long value = 0;
            foreach (var kv in taken)
            {
                long current;
                fleet.Cargo.TryGetValue(kv.Key, out current);
                fleet.Cargo[kv.Key] = current + kv.Value;
                long unitPrice = prices != null ? prices.BasePrice(kv.Key) : 0;
                if (unitPrice <= 0)
                {
                    ItemKind itemKind;
                    unitPrice = world.TryGetInventoryItemKind(kv.Key, out itemKind) ? tuning.FallbackFor(itemKind) : 1;
                }
                value += ItemCredits.For(kv.Key, kv.Value, unitPrice);
            }
            return value;
        }

        /// <summary>Book value of a whole cargo manifest, for a convoy that loads stock rather than
        /// buying it: each line at <paramref name="prices"/>, then <see cref="WorldModel.ItemUnitValue"/>'s
        /// tuned-fallback chain, through <see cref="ItemCredits"/> so gas is valued per kilolitre.
        /// Valued off the manifest rather than the load just made, so a convoy that loads at several
        /// stops carries the sum of them.</summary>
        public static long CargoValueOf(Dictionary<string, long> cargo, WorldModel world, IItemPriceCatalogue prices)
        {
            if (cargo == null || world == null) return 0;
            long value = 0;
            foreach (var kv in cargo)
            {
                long unitPrice = prices != null ? prices.BasePrice(kv.Key) : 0;
                if (unitPrice <= 0) unitPrice = world.ItemUnitValue(kv.Key);
                value += ItemCredits.For(kv.Key, kv.Value, unitPrice);
            }
            return value;
        }

        /// <summary>Loads up to <paramref name="requestedQty"/> of <paramref name="itemKey"/> from
        /// <paramref name="pickupNode"/>'s inventory into <paramref name="fleet"/>'s hold, capped by
        /// what's actually in stock right now, remaining cargo volume (accounting for cargo already
        /// aboard), and the loaded-mass TWR ceiling for the leg to <paramref name="nextLegDestPosKm"/>.
        /// Debits the node's inventory and merges into <see cref="PendingFleetAction.Cargo"/> for
        /// whatever fits; returns the quantity actually loaded (0 if nothing fit or nothing was in
        /// stock). Shared by <see cref="OperationsManager.DispatchHaul"/>'s local-stock fast path
        /// (loading at dispatch time) and <c>ResolveSingle</c>'s <see cref="FleetStopKind.Pickup"/>
        /// handling (loading at a later leg boundary) so the cap math isn't duplicated.</summary>
        public static long LoadCargo(SpatialNode pickupNode, ref PendingFleetAction fleet, string itemKey,
            long requestedQty, WorldModel world, FactionTuning tuning,
            IItemVolumeCatalogue itemVolumes, IItemMassCatalogue itemMasses,
            Vec3 nextLegDestPosKm, out bool massTrimmed)
        {
            massTrimmed = false;
            if (pickupNode == null)
                return 0;
            return LoadCargoCore(
                key => { long v; pickupNode.Inventory.TryGetValue(key, out v); return v; },
                (key, delta) => world.AddNodeInventory(pickupNode.Id, key, delta),
                pickupNode.Position, ref fleet, itemKey, requestedQty, world, tuning,
                itemVolumes, itemMasses, nextLegDestPosKm, out massTrimmed);
        }

        /// <summary>Execution-context-based counterpart to <see cref="LoadCargo"/> for a staged
        /// Operations cycle (Slice 3) — identical volume/mass-trim math (shared via <see
        /// cref="LoadCargoCore"/>), but reads/debits pickup-node inventory through <paramref
        /// name="ctx"/> instead of <paramref name="pickupNode"/>.Inventory and <c>world.AddNodeInventory</c>
        /// directly, so a discarded cycle never touches live stock. <paramref name="world"/> is still
        /// used for read-only catalogue/planet-geometry access (<see cref="ShipPool.RouteEnvironment.From"/>),
        /// which Operations does not stage.</summary>
        public static long LoadCargo(IOperationsExecutionContext ctx, SpatialNode pickupNode, ref PendingFleetAction fleet,
            string itemKey, long requestedQty, WorldModel world, FactionTuning tuning,
            IItemVolumeCatalogue itemVolumes, IItemMassCatalogue itemMasses,
            Vec3 nextLegDestPosKm, out bool massTrimmed)
        {
            massTrimmed = false;
            if (ctx == null || pickupNode == null)
                return 0;
            return LoadCargoCore(
                key => ctx.GetNodeInventory(pickupNode.Id, key),
                (key, delta) => ctx.AddNodeInventory(pickupNode.Id, key, delta),
                pickupNode.Position, ref fleet, itemKey, requestedQty, world, tuning,
                itemVolumes, itemMasses, nextLegDestPosKm, out massTrimmed);
        }

        /// <summary>The cargo mass <paramref name="prefabId"/> can lift on the loaded leg from
        /// <paramref name="fromKm"/> to <paramref name="toKm"/>: the loaded-TWR ceiling at both ends
        /// (<see cref="ShipPool.RouteEnvironment.MaxCargoMassKg"/>, with the
        /// <see cref="FactionTuning.OrbitGravityMin"/> floor). +Infinity for an unscanned hull. The one
        /// figure behind <c>cargo_mass_trimmed</c> and the bulk hull choice's lift cap
        /// (<see cref="HaulHullCost.LiftKgPerLiter"/>).</summary>
        public static double LoadedLiftKg(WorldModel world, FactionTuning tuning, string prefabId, Vec3 fromKm, Vec3 toKm)
        {
            if (world == null || tuning == null || string.IsNullOrEmpty(prefabId)) return double.PositiveInfinity;
            ShipPool.RouteEnvironment route = ShipPool.RouteEnvironment.From(
                world.Planets, fromKm, toKm, tuning.MinDryTwr, tuning.OrbitGravityMin);
            return route.MaxCargoMassKg(world.PrefabCatalogue, prefabId);
        }

        /// <summary>Shared volume/mass-trim/debit logic for both <see cref="LoadCargo"/> overloads —
        /// only how pickup-node inventory is read/debited differs between the live and staged paths,
        /// via <paramref name="getInventory"/>/<paramref name="addInventory"/>.
        /// <para>A gas key is capped by <see cref="GasCarriage.RoomLitres"/> for the leg to
        /// <paramref name="nextLegDestPosKm"/> instead of by the hold, and a solid key by the hold
        /// with gas aboard left out of it.</para></summary>
        private static long LoadCargoCore(Func<string, long> getInventory, Action<string, long> addInventory,
            Vec3 pickupPos, ref PendingFleetAction fleet, string itemKey, long requestedQty,
            WorldModel world, FactionTuning tuning,
            IItemVolumeCatalogue itemVolumes, IItemMassCatalogue itemMasses,
            Vec3 nextLegDestPosKm, out bool massTrimmed)
        {
            massTrimmed = false;
            if (string.IsNullOrEmpty(itemKey) || requestedQty <= 0) return 0;

            long available = getInventory(itemKey);
            long qty = Math.Min(requestedQty, available);
            if (qty <= 0) return 0;

            string prefabId = fleet.ShipPrefabId ?? "";

            if (GasCarriage.InTanks(itemKey))
            {
                // Gas boards the tanks built for it and never the hold, so the hold's volume does
                // not enter; a convoy with no tank for this gas takes none.
                long room = GasCarriage.RoomLitres(world, tuning, fleet, itemKey, pickupPos, nextLegDestPosKm);
                if (room < qty) qty = room;
            }
            else if (fleet.CargoCapacityLiters > 0 && itemVolumes != null)
            {
                double litersPerUnit = itemVolumes.VolumeLitersPerUnit(itemKey);
                if (litersPerUnit > 0)
                {
                    double usedLiters = 0;
                    if (fleet.Cargo != null)
                        foreach (var kv in fleet.Cargo)
                        {
                            // Gas aboard sits in the tanks, not in the hold.
                            if (GasCarriage.InTanks(kv.Key)) continue;
                            double lpu = itemVolumes.VolumeLitersPerUnit(kv.Key);
                            if (lpu > 0) usedLiters += kv.Value * lpu;
                        }
                    double remainingLiters = fleet.CargoCapacityLiters - usedLiters;
                    long maxByVolume = remainingLiters > 0 ? (long)(remainingLiters / litersPerUnit) : 0;
                    if (maxByVolume < qty) qty = maxByVolume;
                }
            }

            if (qty > 0 && itemMasses != null && prefabId.Length > 0)
            {
                double maxCargoMassKg = LoadedLiftKg(world, tuning, prefabId, pickupPos, nextLegDestPosKm);
                if (!double.IsPositiveInfinity(maxCargoMassKg))
                {
                    double unitMassKg = itemMasses.MassKgPerUnit(itemKey);
                    if (unitMassKg > 0)
                    {
                        long maxByMass = (long)(maxCargoMassKg / unitMassKg);
                        if (maxByMass < qty) { qty = maxByMass; massTrimmed = true; }
                    }
                }
            }

            if (qty <= 0) return 0;

            addInventory(itemKey, -qty);
            if (fleet.Cargo == null) fleet.Cargo = new Dictionary<string, long>();
            long existing;
            fleet.Cargo.TryGetValue(itemKey, out existing);
            fleet.Cargo[itemKey] = existing + qty;
            return qty;
        }

        /// <summary>Loads a multi-item Trade cargo basket from <paramref name="pickupNode"/>'s
        /// whole inventory into <paramref name="fleet"/>'s hold: preferred-kind then
        /// oracle-scarcity sorted (most-needed-at-destination first), filled by remaining volume
        /// budget (falls back to per-ItemKind count caps when volume data is unavailable), then
        /// mass/TWR-capped for the leg to <paramref name="nextLegDestPosKm"/> using
        /// <paramref name="fleet"/>'s own <see cref="PendingFleetAction.ShipPrefabId"/>/
        /// <see cref="PendingFleetAction.CargoCapacityLiters"/> (must already be set). Debits the
        /// pickup node's inventory for whatever is actually loaded. Sets
        /// <see cref="PendingFleetAction.Cargo"/>/<see cref="PendingFleetAction.CargoValue"/> only
        /// when something loads — otherwise leaves them at whatever <paramref name="fleet"/>
        /// already carried in (the caller's own fallback, e.g. flat <see cref="FactionIntent.Cost"/>).
        /// <para>The whole-inventory basket is the shape a <see cref="FactionIntent"/>-driven Trade
        /// convoy takes, which is <see cref="CreatePending"/>'s only Trade caller. A convoy formed
        /// from a <see cref="MaterialVector"/> loads that vector's own
        /// <see cref="PendingFleetAction.PickupItemKey"/> through <see cref="LoadCargo"/> instead --
        /// sized to the demand row rather than to the hold. See docs/trade-haul-shaped.md.</para>
        /// <para>Each gas key is sized by <see cref="GasCarriage.RoomLitres"/> and takes nothing from
        /// the hold's volume budget, so a full hold stops the solids and not the gas. Where the
        /// per-kind count cap binds — the hold's capacity or the gas's volume unknown — it binds the
        /// gas too, and the gas is charged against it: tank room is a second ceiling, never a way
        /// round the first.</para></summary>
        public static void LoadTradeCargo(SpatialNode pickupNode, ref PendingFleetAction fleet,
            string destNodeIdForScarcity, Vec3 nextLegDestPosKm, WorldModel world, FactionTuning tuning,
            IItemVolumeCatalogue itemVolumes, IItemMassCatalogue itemMasses, IPriceOracle oracle,
            IItemPriceCatalogue prices, ItemKind? preferredItemKind)
        {
            if (pickupNode == null) return;

            var cargo = new Dictionary<string, long>();
            long cv = 0;
            bool massTrimmed = false;
            bool useVolume = fleet.CargoCapacityLiters > 0 && itemVolumes != null;
            long remainingLiters = fleet.CargoCapacityLiters;
            var kindSent = new Dictionary<ItemKind, long>();

            // Loaded-TWR mass ceiling (Slice E) — same Down-thrust/gravity-floor math as the
            // Haul/loot trims, computed once up front since pickup/next-leg-dest/prefab are fixed
            // for this load. +Infinity ("don't constrain") when unscanned/no mass catalogue.
            bool useMass = itemMasses != null && !string.IsNullOrEmpty(fleet.ShipPrefabId);
            double remainingMassKg = double.PositiveInfinity;
            if (useMass)
            {
                ShipPool.RouteEnvironment massRoute = ShipPool.RouteEnvironment.From(
                    world.Planets, pickupNode.Position, nextLegDestPosKm, tuning.MinDryTwr, tuning.OrbitGravityMin);
                remainingMassKg = massRoute.MaxCargoMassKg(world.PrefabCatalogue, fleet.ShipPrefabId);
                useMass = !double.IsPositiveInfinity(remainingMassKg);
            }

            // Snapshot before iterating: AddNodeInventory mutates the same dict mid-loop.
            List<KeyValuePair<string, long>> nodeItems =
                new List<KeyValuePair<string, long>>(world.GetNodeInventoryAll(pickupNode.Id));

            // Sort items before packing:
            //   1. Preferred kind (surplus-liquidation hint) floats to the top.
            //   2. Within groups, oracle scarcity descending (most needed at destination first).
            //
            // Scarcity is read per inventory key, never per kind: a hold picked by kind loads gravel
            // ahead of gold whenever the destination is deep in cheap ingots, because one number
            // answered for every ingot it holds.
            //
            // The values are read ONCE into a map rather than from inside the comparator: each
            // read derives a reference stock, which a sort would repeat an unpredictable number of
            // times.
            bool hasPref   = preferredItemKind.HasValue;
            bool hasOracle = oracle != null && destNodeIdForScarcity != null;
            if ((hasPref || hasOracle) && nodeItems.Count > 1)
            {
                ItemKind prefKind = hasPref ? preferredItemKind.Value : default(ItemKind);
                Dictionary<string, double> scarcityByKey = null;
                if (hasOracle)
                {
                    scarcityByKey = new Dictionary<string, double>(nodeItems.Count, StringComparer.Ordinal);
                    foreach (var item in nodeItems)
                        if (!scarcityByKey.ContainsKey(item.Key))
                            scarcityByKey[item.Key] = oracle.ScarcityMultiplier(destNodeIdForScarcity, item.Key);
                }
                nodeItems.Sort((a, b) =>
                {
                    if (hasPref)
                    {
                        ItemKind ka, kb;
                        bool aPref = world.TryGetInventoryItemKind(a.Key, out ka) && ka == prefKind;
                        bool bPref = world.TryGetInventoryItemKind(b.Key, out kb) && kb == prefKind;
                        if (aPref != bPref) return aPref ? -1 : 1;
                    }
                    if (!hasOracle) return 0;
                    double sa, sb;
                    if (!scarcityByKey.TryGetValue(a.Key, out sa)) sa = 1.0;
                    if (!scarcityByKey.TryGetValue(b.Key, out sb)) sb = 1.0;
                    return sb.CompareTo(sa); // descending: most scarce first
                });
            }

            foreach (var kv in nodeItems)
            {
                if (kv.Value <= 0) continue;
                // Gas is sized by the convoy's tanks for it and takes nothing from the hold, so a full
                // hold stops the solids and not the gas.
                bool inTanks = GasCarriage.InTanks(kv.Key);
                if (useVolume && remainingLiters <= 0 && !inTanks) continue;

                ItemKind ik;
                if (!world.TryGetInventoryItemKind(kv.Key, out ik)) continue;

                long toSend;
                if (inTanks)
                {
                    long room = GasCarriage.RoomLitres(world, tuning, fleet, kv.Key, pickupNode.Position, nextLegDestPosKm);
                    toSend = kv.Value < room ? kv.Value : room;
                    if (toSend <= 0) continue;
                    if (!useVolume || itemVolumes.VolumeLitersPerUnit(kv.Key) <= 0)
                    {
                        long cap = tuning.TradeCargoFor(ik);
                        if (cap <= 0) continue;
                        long sent; kindSent.TryGetValue(ik, out sent);
                        long rem = cap - sent;
                        if (rem < toSend) toSend = rem;
                        if (toSend <= 0) continue;
                        kindSent[ik] = sent + toSend;
                    }
                }
                else if (useVolume)
                {
                    double unitVol = itemVolumes.VolumeLitersPerUnit(kv.Key);
                    if (unitVol > 0)
                    {
                        // Volume-based cap: fill until hold is full.
                        long maxByVol = (long)(remainingLiters / unitVol);
                        toSend = kv.Value < maxByVol ? kv.Value : maxByVol;
                        if (toSend <= 0) continue;
                        remainingLiters -= (long)(toSend * unitVol);
                    }
                    else
                    {
                        // Unknown volume for this item — count-cap fallback.
                        long cap = tuning.TradeCargoFor(ik);
                        if (cap <= 0) continue;
                        long sent; kindSent.TryGetValue(ik, out sent);
                        long rem = cap - sent;
                        toSend = rem > 0 ? (kv.Value < rem ? kv.Value : rem) : 0;
                        if (toSend <= 0) continue;
                        kindSent[ik] = sent + toSend;
                    }
                }
                else
                {
                    // Count-cap loading (existing behaviour when capacity is unknown).
                    long cap = tuning.TradeCargoFor(ik);
                    if (cap <= 0) continue;
                    long sent; kindSent.TryGetValue(ik, out sent);
                    long rem = cap - sent;
                    toSend = rem > 0 ? (kv.Value < rem ? kv.Value : rem) : 0;
                    if (toSend <= 0) continue;
                    kindSent[ik] = sent + toSend;
                }

                if (useMass)
                {
                    double unitMassKg = itemMasses.MassKgPerUnit(kv.Key);
                    if (unitMassKg > 0)
                    {
                        long maxByMass = (long)(remainingMassKg / unitMassKg);
                        if (maxByMass < toSend) { toSend = maxByMass; massTrimmed = true; }
                        if (toSend <= 0) continue;
                        remainingMassKg -= toSend * unitMassKg;
                    }
                }

                long unitPrice = prices != null ? prices.BasePrice(kv.Key) : 0;
                if (unitPrice <= 0) unitPrice = tuning.FallbackFor(ik);

                cargo[kv.Key] = toSend;
                cv += ItemCredits.For(kv.Key, toSend, unitPrice);
                world.AddNodeInventory(pickupNode.Id, kv.Key, -toSend);
            }

            fleet.CargoMassTrimmed = massTrimmed;
            if (cargo.Count > 0)
            {
                fleet.Cargo = cargo;
                fleet.CargoValue = cv;
            }
        }

        /// <summary>The acquisition direction of Trade: a convoy has arrived at
        /// <paramref name="sellerNode"/>, a node belonging to <paramref name="sellerTag"/>, to buy
        /// the one thing it was sent for. The vector it was formed from is aboard as
        /// <see cref="PendingFleetAction.PickupItemKey"/>/<see cref="PendingFleetAction.PickupQuantity"/>,
        /// and nothing else is bought: the money moves for exactly the units that board, and the
        /// hold is filled to the demand row rather than to its own capacity.
        ///
        /// <para>**Seller consent.** The seller parts only with what it is itself genuinely
        /// oversupplied in (<see cref="FactionTuning.ProcurementSellerScarcityCeiling"/>), and never
        /// with stock its own build queue has already claimed
        /// (<see cref="SpatialNode.BuildQueueReserved"/>). Without both, procurement would be a
        /// polite name for stripping a partner's shipyard. The vector's source was chosen against
        /// this same pair of tests, so this is the dock re-checking a decision, not making one. A fuel
        /// key (<see cref="FuelMarket.IsFuelKey"/>) sells only what the node holds above its keep
        /// (<see cref="FuelMarket.SellableAboveKeep"/>).</para>
        ///
        /// <para>**Buyer affordability.** The quantity is capped by the buyer's own headroom above
        /// <see cref="FactionTuning.InsolvencyFloor"/> and by its remaining share of the shared
        /// trade window budget. An item with no price on either side is left where it is rather than
        /// moved for free.</para>
        ///
        /// <para>Volume, mass and stock caps are <see cref="LoadCargo"/>'s, so payment follows the
        /// load rather than predicting it. Returns the total credits transferred.</para>
        ///
        /// <para>**The seller's say.** Nothing is bought while <see cref="Diplomacy.TradeBarred"/> holds
        /// for the pair, or from a player faction whose sell switch is off: either can start while the
        /// convoy is in the air, and it then flies home empty. <see cref="DockRefusal"/> names which.
        /// A null <paramref name="sellerState"/> skips both, as a missing state always has.</para></summary>
        public static long BuyTradeCargo(SpatialNode sellerNode, string sellerTag, ref PendingFleetAction fleet,
            string destNodeIdForScarcity, Vec3 nextLegDestPosKm, WorldModel world, FactionTuning tuning,
            IItemVolumeCatalogue itemVolumes, IItemMassCatalogue itemMasses, IPriceOracle oracle,
            IItemPriceCatalogue prices, FactionState buyerState, SpendBudget tradeInboundBudget,
            FactionState sellerState = null)
        {
            string emptyReason;
            return BuyTradeCargo(sellerNode, sellerTag, ref fleet, destNodeIdForScarcity, nextLegDestPosKm, world,
                tuning, itemVolumes, itemMasses, oracle, prices, buyerState, tradeInboundBudget, sellerState,
                out emptyReason);
        }

        /// <summary><see cref="BuyTradeCargo(SpatialNode, string, ref PendingFleetAction, string, Vec3, WorldModel, FactionTuning, IItemVolumeCatalogue, IItemMassCatalogue, IPriceOracle, IItemPriceCatalogue, FactionState, SpendBudget, FactionState)"/>,
        /// naming in <paramref name="emptyReason"/> the gate that let nothing aboard, or null when
        /// something boarded:
        /// <list type="bullet">
        /// <item><c>seller</c>: no seller node or tag, or the buyer is the seller;</item>
        /// <item><c>refused</c>: <see cref="DockRefusal"/> (its <c>refused=</c> names which);</item>
        /// <item><c>nothing</c>: the convoy carries no item or quantity to buy;</item>
        /// <item><c>unpriced</c>: the item has no price on either side;</item>
        /// <item><c>consent</c>: the seller's consent gate refuses the item;</item>
        /// <item><c>stock</c>, <c>held</c>, <c>claimed</c>: nothing sellable at the node, and which layer
        /// took it (<see cref="FuelMarket.NothingSellable"/>; a key that is not a fuel key only ever
        /// reads <c>stock</c>). A fuel key refused by consent for holding nothing above its keep
        /// reads as one of these, not as <c>consent</c>;</item>
        /// <item><c>floor</c>: the buyer has no headroom above <see cref="FactionTuning.InsolvencyFloor"/>;</item>
        /// <item><c>budget</c>: the buyer's trade window budget is spent;</item>
        /// <item><c>mass</c>, <c>tank</c>, <c>hold</c>: nothing fits aboard — the loaded leg's lift, no
        /// tank room for a gas, or no room in the hold.</item>
        /// </list></summary>
        public static long BuyTradeCargo(SpatialNode sellerNode, string sellerTag, ref PendingFleetAction fleet,
            string destNodeIdForScarcity, Vec3 nextLegDestPosKm, WorldModel world, FactionTuning tuning,
            IItemVolumeCatalogue itemVolumes, IItemMassCatalogue itemMasses, IPriceOracle oracle,
            IItemPriceCatalogue prices, FactionState buyerState, SpendBudget tradeInboundBudget,
            FactionState sellerState, out string emptyReason)
        {
            emptyReason = "seller";
            if (sellerNode == null || string.IsNullOrEmpty(sellerTag)) return 0;
            string buyerTag = fleet.ActorTag;
            if (string.IsNullOrEmpty(buyerTag) || sellerTag == buyerTag) return 0;
            emptyReason = "refused";
            if (DockRefusal(buyerState, sellerState, false, tuning) != null) return 0;

            emptyReason = "nothing";
            string itemKey = fleet.PickupItemKey;
            long wanted = fleet.PickupQuantity;
            if (string.IsNullOrEmpty(itemKey) || wanted <= 0) return 0;

            emptyReason = "unpriced";
            ItemKind kind;
            bool knownKind = world.TryGetInventoryItemKind(itemKey, out kind);
            long unitPrice = prices != null ? prices.BasePrice(itemKey) : 0;
            if (unitPrice <= 0 && knownKind) unitPrice = tuning.FallbackFor(kind);
            if (unitPrice <= 0) return 0;   // unpriced: no way to pay for it, so don't take it

            // This convoy's own procurement hold is its to collect: consent and the sellable excess
            // count it back in, and every other convoy's hold stays out.
            long ownHold = FuelMarket.OwnHold(fleet, sellerNode, world, tuning);
            bool fuelKey = FuelMarket.IsFuelKey(world, sellerTag, itemKey, tuning);

            // Consent is asked about THIS item, not about its kind: a seller deep in stone ingots is
            // not thereby willing to part with its gold, and one short of iron is not thereby
            // unwilling to sell the silver it is glutted in.
            if (oracle != null)
            {
                TradeConsent.Reason consent = TradeConsent.EvaluateAtDock(oracle, world, sellerTag, sellerNode,
                    itemKey, tuning, ownHold);
                if (consent != TradeConsent.Reason.Consents)
                {
                    // A fuel key's consent is "anything above the keep": its refusal is an empty node.
                    emptyReason = fuelKey && consent == TradeConsent.Reason.Scarce
                        ? FuelMarket.NothingSellable(sellerNode, itemKey, world, tuning, ownHold)
                        : "consent";
                    return 0;               // the seller needs it themselves
                }
            }

            DateTime nowUtc = world.UtcNow;
            long stock;
            sellerNode.Inventory.TryGetValue(itemKey, out stock);
            long available = stock - sellerNode.BuildQueueReserved(itemKey, nowUtc);
            emptyReason = "stock";
            // A fuel key sells what the node holds above its keep, the margin the
            // vector was sized to (TradeConsent.ConsentingMargin), never the keep itself.
            if (fuelKey)
            {
                long sellable = FuelMarket.SellableAboveKeep(sellerNode, itemKey, world, tuning, ownHold);
                if (sellable < available) available = sellable;
                if (available <= 0 && stock - sellerNode.BuildQueueReserved(itemKey, nowUtc) > 0)
                    emptyReason = FuelMarket.NothingSellable(sellerNode, itemKey, world, tuning, ownHold);
            }
            if (available <= 0) return 0;

            long floorHeadroom = world.GetTreasury(buyerTag) - tuning.InsolvencyFloor;
            if (floorHeadroom < 0) floorHeadroom = 0;
            long windowRemaining = tradeInboundBudget != null && buyerState != null
                ? tradeInboundBudget.RemainingBudget(buyerTag, nowUtc, buyerState.CentralBankRecentIncome,
                    tuning.TradeInboundMaxIncomeFractionPerWindow, tuning.TradeInboundBudgetFloor,
                    tuning.SpendBudgetWindowSeconds,
                    InboundWealthSurplus(buyerTag, world, tuning), tuning.TradeInboundWealthFractionPerWindow)
                : long.MaxValue;

            long allowed = wanted;
            if (available < allowed) allowed = available;
            long byFloor  = ItemCredits.Affordable(itemKey, floorHeadroom, unitPrice);
            long byWindow = ItemCredits.Affordable(itemKey, windowRemaining, unitPrice);
            if (byFloor  < allowed) allowed = byFloor;
            if (byWindow < allowed) allowed = byWindow;
            if (allowed <= 0)
            {
                emptyReason = byFloor <= 0 ? "floor" : "budget";
                return 0;
            }

            bool massTrimmed;
            long loaded = LoadCargo(sellerNode, ref fleet, itemKey, allowed, world, tuning,
                itemVolumes, itemMasses, nextLegDestPosKm, out massTrimmed);
            if (massTrimmed) fleet.CargoMassTrimmed = true;
            if (loaded <= 0)
            {
                emptyReason = massTrimmed ? "mass" : GasCarriage.InTanks(itemKey) ? "tank" : "hold";
                return 0;
            }

            emptyReason = null;
            long spent = ItemCredits.For(itemKey, loaded, unitPrice);
            world.Spend(buyerTag, spent);
            world.Earn(sellerTag, spent);
            fleet.CargoValue = spent;
            if (tradeInboundBudget != null)
                tradeInboundBudget.Record(buyerTag, nowUtc, spent, tuning.SpendBudgetWindowSeconds);
            return spent;
        }

        /// <summary>Why a trade convoy's counterparty refuses it at the dock, or null when it does not.
        /// Asked at both settlements, so a convoy already flying when either condition starts settles
        /// nothing: "barred" when <see cref="Diplomacy.TradeBarred"/> holds for the pair, "opted_out"
        /// when the counterparty is a player faction whose switch for its side of the trade is off
        /// (selling, when <paramref name="actorSells"/> is false; buying, when true).</summary>
        internal static string DockRefusal(FactionState actor, FactionState counterparty, bool actorSells,
            FactionTuning tuning)
        {
            if (counterparty == null) return null;
            if (Diplomacy.TradeBarred(actor, counterparty, tuning)) return "barred";
            bool willing = actorSells ? FactionLifecycle.TradeBuyEnabled(counterparty)
                                      : FactionLifecycle.TradeSellEnabled(counterparty);
            return willing ? null : "opted_out";
        }

        private static FactionState FindState(IList<FactionState> states, string tag)
        {
            if (tag == null) return null;
            for (int k = 0; k < states.Count; k++)
                if (states[k].Tag == tag) return states[k];
            return null;
        }

        private static void ResolveSingle(
            int si, int fi, IList<FactionState> states, WorldModel world,
            FactionTuning tuning, IList<string> log, IEconomyGateway economy,
            IItemPriceCatalogue prices = null, IItemVolumeCatalogue itemVolumes = null,
            IPriceOracle oracle = null, SpendBudget tradeInboundBudget = null,
            IItemMassCatalogue itemMasses = null, IRandom rng = null)
        {
            FactionState owner = states[si];
            PendingFleetAction fleet = owner.PendingFleets[fi];
            double ratio = fleet.ForcedRatio.HasValue ? fleet.ForcedRatio.Value : fleet.CompletionRatio(world.UtcNow);

            // A bench fleet's leg ends here with no effect at all: no territory credit, no hold
            // damage, loot, incident or relation, and no hull home. An attacker's dwell hands it a
            // leg away from the fight instead of ending it (BenchScenario.BeginAftermath), so its
            // grids are seen to leave; BenchScenario.Conclude writes the scenario up in the same
            // pass and retires it once they have.
            if (fleet.IsBench)
            {
                bool dwellEnded = fleet.CurrentStopKind == FleetStopKind.Engage;
                // A repelled attack was filed when it was stopped.
                if (dwellEnded && EngagesOnArrival(fleet.Kind) && fleet.RetreatReason == null)
                    RecordFight(states, fleet, fleet.TargetTag, ratio, false, world, tuning);
                if (!BenchScenario.BeginAftermath(ref fleet, states, world.Graph, world.UtcNow))
                    fleet.Resolved = true;
                owner.PendingFleets[fi] = fleet;
                return;
            }

            // A duplicate the host dismissed never flies its mission. Ahead of the activity credit
            // and the per-stop handling: it reached no stop, and its hull and hold go home as they are.
            if (fleet.ForcedRatioCause == SiblingDismissCause || fleet.ForcedRatioCause == QueueGiveUpCause)
            {
                RecallToOrigin(ref fleet, world, log);
                fleet.Resolved = true;
                owner.PendingFleets[fi] = fleet;
                return;
            }

            // Ship traffic as territorial presence: every stop resolution passes through here, so
            // arrival, dwell-end and return all credit from one seam. Navigation and Intercept stops
            // are transit milestones rather than mission destinations and are excluded, matching how
            // the per-ActionKind effect switch below treats them. WorldModel.CreditShipActivity
            // enforces the owner test; an intercepted or already-resolved fleet credits nothing.
            if (!fleet.Resolved && !fleet.Intercepted
                && fleet.CurrentStopKind != FleetStopKind.Navigation
                && fleet.CurrentStopKind != FleetStopKind.Intercept)
                world.CreditShipActivity(fleet.TargetNodeId, fleet.ActorTag);

            if (fleet.CurrentStopKind == FleetStopKind.Return)
            {
                FinalizeReturn(fleet, ratio, world, log);
                fleet.Resolved = true;
                owner.PendingFleets[fi] = fleet;
                return;
            }

            // Coordinate-only clearance stops are transit milestones, never mission destinations.
            // In particular, NodeId is intentionally null, so falling through to the action switch
            // would risk applying an economic/combat effect on an arbitrary navigation point.
            if (fleet.CurrentStopKind == FleetStopKind.Navigation)
            {
                if (fleet.RemainingStops != null && fleet.RemainingStops.Count > 0
                    && AdvanceToNextStop(owner, fi, fleet, world, tuning, log))
                    return;
                fleet.Resolved = true;
                owner.PendingFleets[fi] = fleet;
                return;
            }

            // The interception window ended: decide the encounter (convoy) or fly on (raid).
            if (fleet.CurrentStopKind == FleetStopKind.Intercept)
            {
                ResolveInterceptWindow(si, fi, states, world, tuning, rng ?? new SeededRandom((int)(world.UtcNow.Ticks & 0x7FFFFFFF)), log, prices);
                return;
            }

            // Pickup stops load cargo on arrival, then continue — never a mission destination in
            // their own right, so they fall out of the per-ActionKind effect switch below exactly
            // like Navigation stops do.
            if (fleet.CurrentStopKind == FleetStopKind.Pickup)
            {
                SpatialNode pickupNode;
                if (fleet.TargetNodeId != null && world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out pickupNode))
                {
                    // The next leg is the one the convoy actually flies, so every pickup still
                    // queued for the node it is standing on is skipped past when sizing it. Those
                    // are collected in this same visit by the loop below, not flown to.
                    Vec3 nextLegDestPosKm = fleet.TargetPosKm;
                    string nextLegDestNodeId = null;
                    FleetStop departingStop;
                    if (TryPeekDepartingStop(fleet, pickupNode.Id, out departingStop))
                    {
                        SpatialNode nextNode;
                        if (departingStop.NodeId != null && world.Graph.Nodes.TryGetValue(departingStop.NodeId, out nextNode))
                        {
                            nextLegDestPosKm = nextNode.Position;
                            nextLegDestNodeId = departingStop.NodeId;
                        }
                        else if (departingStop.HasPosition)
                            nextLegDestPosKm = departingStop.PositionKm;
                    }

                    // Consolidation routes a node's pickups adjacent (OperationsManager.OrderPickupRoute)
                    // and they are collected together, in one visit. Flying them as separate legs
                    // costs a zero-distance leg apiece, and such a leg arrives the instant it
                    // departs -- it has no duration for the host to re-aim the hull in, so the
                    // materialised grid is stashed for a reclaim that never comes.
                    do
                    {
                        if (fleet.Kind == ActionKind.Trade && fleet.IsProcurement)
                        {
                            // Acquisition direction: this pickup node belongs to the partner, so the
                            // basket is bought rather than drawn down. Payment happens here, at the
                            // seller, for exactly what fits aboard — the remaining Return leg is then
                            // an ordinary "carry it home", needing no settlement of its own.
                            FactionState sellerState = FindState(states, fleet.TargetTag);
                            long heldHere = FuelMarket.OwnHold(fleet, pickupNode, world, tuning);
                            string emptyReason;
                            long paid = BuyTradeCargo(pickupNode, fleet.TargetTag, ref fleet, nextLegDestNodeId,
                                nextLegDestPosKm, world, tuning, itemVolumes, itemMasses, oracle, prices,
                                owner, tradeInboundBudget, sellerState, out emptyReason);
                            // The convoy leaves this Pickup whatever it bought, so its hold goes now:
                            // a later convoy docking here in the same pass must not find it still held.
                            world.ReleaseProcurementHold(pickupNode.Id, fleet.PickupItemKey, heldHere);
                            string refusal = DockRefusal(owner, sellerState, false, tuning);
                            int items = fleet.Cargo != null ? fleet.Cargo.Count : 0;
                            // An empty buy names the gate that let nothing aboard; a buy that boarded
                            // something carries no reason.
                            log.Add("PROCURE_BOUGHT " + fleet.LogId + " " + fleet.ActorTag + " <- "
                                    + (fleet.TargetTag ?? "?") + " @ " + pickupNode.Id
                                    + " items=" + items
                                    + " paid=" + paid
                                    + (refusal != null ? " refused=" + refusal : "")
                                    + (fleet.CargoMassTrimmed ? " cargo_mass_trimmed" : "")
                                    + (items == 0 && emptyReason != null
                                       ? " reason=" + emptyReason : ""));
                        }
                        else if (fleet.Kind == ActionKind.Trade
                                 && !string.IsNullOrEmpty(fleet.PickupItemKey) && fleet.PickupQuantity > 0)
                        {
                            // Seller-flies, hull fetched from elsewhere: the convoy is standing at its own
                            // faction's source node, so the stock is simply drawn down -- the same single
                            // (item, quantity) load a Haul pickup does, sized to the demand row this
                            // convoy was formed for rather than to the hold.
                            // A procurement's hold at its own node is not its to load.
                            bool tradeMassTrimmed;
                            long tradeRequest = FuelMarket.HaulLoadable(pickupNode, fleet.PickupItemKey,
                                fleet.PickupQuantity, world, tuning);
                            long tradeLoaded = LoadCargo(pickupNode, ref fleet, fleet.PickupItemKey, tradeRequest,
                                world, tuning, itemVolumes, itemMasses, nextLegDestPosKm, out tradeMassTrimmed);
                            if (tradeLoaded > 0)
                                fleet.CargoValue = CargoValueOf(fleet.Cargo, world, prices);
                            log.Add("TRADE_PICKUP " + fleet.LogId + " " + fleet.ActorTag + " @ " + pickupNode.Id
                                    + " loaded=" + tradeLoaded + " " + fleet.PickupItemKey
                                    + " value=" + fleet.CargoValue
                                    + (tradeMassTrimmed ? " cargo_mass_trimmed" : "")
                                    + (tradeRequest < fleet.PickupQuantity
                                       ? " held=" + (fleet.PickupQuantity - tradeRequest) : ""));
                        }
                        else if (!string.IsNullOrEmpty(fleet.PickupItemKey) && fleet.PickupQuantity > 0)
                        {
                            // Loads around a procurement's hold (FuelMarket.HaulLoadable), and gives up
                            // its own claim as it leaves the Pickup, so a procurement docking later in
                            // this pass is not still offered less on its account.
                            bool massTrimmed;
                            long claimedHere = FuelMarket.OwnClaim(fleet, pickupNode, world, tuning);
                            long request = FuelMarket.HaulLoadable(pickupNode, fleet.PickupItemKey,
                                fleet.PickupQuantity, world, tuning);
                            long loaded = LoadCargo(pickupNode, ref fleet, fleet.PickupItemKey, request,
                                world, tuning, itemVolumes, itemMasses, nextLegDestPosKm, out massTrimmed);
                            world.ReleaseHaulPickupClaim(pickupNode.Id, fleet.PickupItemKey, claimedHere);
                            if (loaded > 0) fleet.CargoValue = CargoValueOf(fleet.Cargo, world, prices);
                            log.Add("HAUL_PICKUP " + fleet.LogId + " " + fleet.ActorTag + " @ " + pickupNode.Id
                                    + " loaded=" + loaded + " " + fleet.PickupItemKey
                                    + (massTrimmed ? " cargo_mass_trimmed" : "")
                                    + (request < fleet.PickupQuantity ? " held=" + (fleet.PickupQuantity - request) : ""));
                        }
                    } while (TakeNextPickupAtSameNode(ref fleet, pickupNode.Id));
                }
                fleet.PickupItemKey = null;
                fleet.PickupQuantity = 0;
                if (fleet.RemainingStops != null && fleet.RemainingStops.Count > 0
                    && AdvanceToNextStop(owner, fi, fleet, world, tuning, log))
                    return;
                fleet.Resolved = true;
                owner.PendingFleets[fi] = fleet;
                return;
            }

            // Mine dwell stops mint + load on resolution (not at dispatch), then continue -- never a
            // mission destination in their own right, so they fall out of the per-ActionKind effect
            // switch below exactly like Pickup/Navigation stops do. This is where the volume-targeted
            // extraction actually happens: ratio-scaled (mid-dwell interception), split across
            // fleet.MineWeights, and where this node's real MineCooldownUntilUtc is set from the
            // dwell's own actual duration (see FactionTuning.MineCooldownMultiplier).
            if (fleet.CurrentStopKind == FleetStopKind.Mine)
            {
                SpatialNode mineNode;
                if (fleet.TargetNodeId != null && world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out mineNode)
                    && fleet.MineWeights != null && fleet.MineTargetLiters > 0)
                {
                    Vec3 nextLegDestPosKm = fleet.TargetPosKm;
                    if (fleet.RemainingStops != null && fleet.RemainingStops.Count > 0)
                    {
                        FleetStop nextStop = fleet.RemainingStops[0];
                        SpatialNode nextNode;
                        if (nextStop.NodeId != null && world.Graph.Nodes.TryGetValue(nextStop.NodeId, out nextNode))
                            nextLegDestPosKm = nextNode.Position;
                        else if (nextStop.HasPosition)
                            nextLegDestPosKm = nextStop.PositionKm;
                    }

                    var minedSummary = new System.Text.StringBuilder();
                    foreach (var kv in fleet.MineWeights)
                    {
                        string subTypeId = kv.Key;
                        double litersPerUnit = itemVolumes != null ? itemVolumes.VolumeLitersPerUnit(subTypeId) : 0;
                        if (litersPerUnit <= 0) continue;
                        long mintedQty = (long)(fleet.MineTargetLiters * kv.Value * ratio / litersPerUnit);
                        if (mintedQty <= 0) continue;
                        world.AddNodeInventory(mineNode.Id, subTypeId, mintedQty);
                        bool massTrimmed;
                        long loaded = LoadCargo(mineNode, ref fleet, subTypeId, mintedQty, world, tuning,
                            itemVolumes, itemMasses, nextLegDestPosKm, out massTrimmed);
                        minedSummary.Append(subTypeId).Append("x").Append(loaded).Append(' ');
                    }

                    double dwellSeconds = (fleet.EstimatedArrivalUtc - fleet.DepartedUtc).TotalSeconds;
                    mineNode.LastMinedUtc = world.UtcNow;
                    mineNode.MineCooldownUntilUtc = world.UtcNow.AddSeconds(dwellSeconds * tuning.MineCooldownMultiplier);

                    log.Add("MINE_EXTRACTED " + fleet.LogId + " " + fleet.ActorTag + " @ " + mineNode.Id
                            + " dwell=" + dwellSeconds.ToString("0", CultureInfo.InvariantCulture) + "s "
                            + minedSummary.ToString().TrimEnd());
                }
                fleet.MineWeights = null;
                fleet.MineTargetLiters = 0;
                if (fleet.RemainingStops != null && fleet.RemainingStops.Count > 0
                    && AdvanceToNextStop(owner, fi, fleet, world, tuning, log))
                    return;
                fleet.Resolved = true;
                owner.PendingFleets[fi] = fleet;
                return;
            }

            // Contact is not resolution for a combat fleet: arriving at the target only starts the
            // engagement dwell. The switch below is reached on the Engage leg instead, once that
            // window has run out -- which is what makes the raid contestable for its whole length
            // rather than only at the instant it touched down.
            // An attack whose target changed hands under it, in flight or on its dwell, has nobody
            // left there to fight: it neither starts nor finishes a dwell, and goes home.
            bool changedHands = fleet.RetreatReason == null
                                && ResolveChangedHands(states, (si, fi), ref fleet, world, ratio, log);

            if (!changedHands && IsPreEngagementContact(fleet)
                && AdvanceToNextStop(owner, fi, fleet, world, tuning, log))
                return;

            // A fleet that broke off (BeginRetreat) leaves its dwell with nothing: the attack it was
            // holding station for is forfeited, and so is the loot that goes with leaving. Earlier
            // siege cycles already landed and keep their effect. The itinerary below takes it home.
            bool retreating = fleet.RetreatReason != null && fleet.CurrentStopKind == FleetStopKind.Engage;
            if (retreating)
                log.Add("FLEET_RETREATED " + fleet.LogId + " " + fleet.ActorTag + " kind=" + fleet.Kind
                        + " @" + (fleet.TargetNodeId ?? "?") + " reason=" + fleet.RetreatReason);

            if (!retreating && !changedHands)
            switch (fleet.Kind)
            {
                case ActionKind.Raid:
                case ActionKind.Retaliate:
                {
                    // Apply scaled hold-damage and threat; Raid() records the incident for ApplyDiplomacy
                    if (fleet.TargetNodeId != null)
                    {
                        string raidVictim = world.Raid(fleet.ActorTag, fleet.TargetNodeId, ratio);
                        // P9: truce-breach grudge — if actor raids while under an active truce with the
                        // target faction, the victim records a grudge against the attacker.
                        if (fleet.TargetTag != null && Diplomacy.UnderTruce(owner, fleet.TargetTag, world.UtcNow))
                        {
                            FactionState victim = null;
                            for (int k = 0; k < states.Count; k++)
                                if (states[k].Tag == fleet.TargetTag) { victim = states[k]; break; }
                            if (victim != null)
                            {
                                Diplomacy.RecordTruceBreachGrudge(victim, fleet.ActorTag, world.UtcNow, tuning);
                                world.RecordEvent(FactionEventKind.TruceBreach, fleet.ActorTag, fleet.TargetTag,
                                                  fleet.TargetNodeId);
                                log.Add("TRUCE_BREACH " + fleet.LogId + " " + fleet.ActorTag
                                        + " raided " + fleet.TargetTag + " during truce — grudge recorded");
                            }
                        }
                        // The siege's own decision, taken after the strip so it reads the hold this
                        // cycle just produced: hold station for another dwell, or leave.
                        int heldCycles = DropSiegeIfSpent(ref fleet, world, tuning, ratio, raidVictim);
                        bool leaving = CountRemainingEngages(fleet) == 0;
                        if (leaving)
                            world.MarkCombatOutcomeWithdrawal(fleet.LogId, WithdrawalReason(fleet, world, tuning, ratio, raidVictim));
                        // Loot once, on the way out. A siege strips hold every cycle; carrying the
                        // node's inventory off is a single act and belongs to the departure.
                        long loot = leaving
                            ? CollectNodeLoot(ref fleet, world, ratio, tuning, prices, itemVolumes, itemMasses)
                            : 0;
                        world.RecordEvent(FactionEventKind.RaidResolved, fleet.ActorTag, raidVictim,
                                          fleet.TargetNodeId, loot, ratio);
                        log.Add("RAID_RESOLVED " + fleet.LogId + " " + fleet.ActorTag + " -> " + fleet.TargetNodeId
                                + " ratio=" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + (fleet.Kind == ActionKind.Retaliate ? " [retaliate]" : "")
                                + (loot > 0 ? " loot=" + loot : "")
                                + SiegeSuffix(fleet, heldCycles, leaving)
                                + (fleet.CargoMassTrimmed ? " cargo_mass_trimmed" : ""));
                        RecordFight(states, fleet, raidVictim, ratio, false, world, tuning);
                        RecordCampaignOutcome(states, (si, fi), CampaignOutcome.Landed,
                                              new CombatStrength.DuelBracket(), world);
                    }
                    break;
                }
                case ActionKind.Conquer:
                {
                    if (fleet.TargetNodeId != null)
                    {
                        SpatialNode targetNode;
                        world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out targetNode);

                        string conquerVictim = world.Conquer(fleet.ActorTag, fleet.TargetNodeId, ratio);

                        bool captured = targetNode != null && targetNode.OwnerFactionTag == fleet.ActorTag;

                        if (fleet.TargetTag != null && Diplomacy.UnderTruce(owner, fleet.TargetTag, world.UtcNow))
                        {
                            FactionState victim = null;
                            for (int k = 0; k < states.Count; k++)
                                if (states[k].Tag == fleet.TargetTag) { victim = states[k]; break; }
                            if (victim != null)
                            {
                                Diplomacy.RecordTruceBreachGrudge(victim, fleet.ActorTag, world.UtcNow, tuning);
                                world.RecordEvent(FactionEventKind.TruceBreach, fleet.ActorTag, fleet.TargetTag,
                                                  fleet.TargetNodeId);
                                log.Add("TRUCE_BREACH " + fleet.LogId + " " + fleet.ActorTag
                                        + " conquered " + fleet.TargetTag + " during truce — grudge recorded");
                            }
                        }
                        // Only loot on the repelled/degrade path — a successful capture already
                        // inherits the target's whole inventory via ownership transfer, so explicit
                        // loot there would be redundant (and would double-count the same items).
                        // A capture ends the siege by definition -- the node is no longer the
                        // victim's, which DropSiegeIfSpent reads directly.
                        int heldCycles = DropSiegeIfSpent(ref fleet, world, tuning, ratio, conquerVictim);
                        bool leaving = CountRemainingEngages(fleet) == 0;
                        if (leaving && !captured)
                            world.MarkCombatOutcomeWithdrawal(fleet.LogId, WithdrawalReason(fleet, world, tuning, ratio, conquerVictim));
                        long loot = !captured && leaving
                            ? CollectNodeLoot(ref fleet, world, ratio, tuning, prices, itemVolumes, itemMasses)
                            : 0;
                        // A repelled Conquer is a raid as far as the defender is concerned; only a
                        // capture is the event that changes who owns what.
                        world.RecordEvent(captured ? FactionEventKind.NodeConquered : FactionEventKind.RaidResolved,
                                          fleet.ActorTag, conquerVictim, fleet.TargetNodeId, loot, ratio);
                        log.Add("CONQUER_RESOLVED " + fleet.LogId + " " + fleet.ActorTag + " -> " + fleet.TargetNodeId
                                + " ratio=" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + (captured ? " [captured]" : " [repelled]")
                                + (loot > 0 ? " loot=" + loot : "")
                                + SiegeSuffix(fleet, heldCycles, leaving)
                                + (fleet.CargoMassTrimmed ? " cargo_mass_trimmed" : "")
                                + (captured ? CapturedHoldings(world, fleet.TargetNodeId) : ""));
                        RecordFight(states, fleet, conquerVictim, ratio, false, world, tuning);
                        RecordCampaignOutcome(states, (si, fi), CampaignOutcome.Landed,
                                              new CombatStrength.DuelBracket(), world);
                    }
                    break;
                }
                case ActionKind.Trade:
                {
                    FactionState actorState = null;
                    FactionState recipientState = null;
                    for (int k = 0; k < states.Count; k++)
                    {
                        if (states[k].Tag == fleet.ActorTag) actorState = states[k];
                        if (states[k].Tag == fleet.TargetTag) recipientState = states[k];
                    }

                    if (actorState != null && fleet.TargetTag != null)
                    {
                        // The recipient's say, re-asked at the dock: a bar or a switched-off buyer that
                        // started while the convoy flew settles nothing, and the whole hold rides home.
                        // Nothing traded, so nothing warms either -- a barred pair never lifts itself
                        // back over the line it is barred by.
                        string refusal = DockRefusal(actorState, recipientState, true, tuning);
                        if (refusal == null)
                        {
                            Diplomacy.LiftRelation(actorState, fleet.TargetTag, tuning);
                            // Recipient also warms slightly — being traded with builds goodwill even passively.
                            if (recipientState != null)
                                Diplomacy.LiftRelation(recipientState, fleet.ActorTag, tuning.TradeRelationBoostRecipient);
                        }

                        long settledTotal = 0;
                        long returnedTotal = 0;

                        // Units refused at the dock, indexed by the gate that actually refused them
                        // (TradeRefusal*). The resolve line used to carry settled/returned only, so a
                        // scarcity-floor refusal and a budget refusal produced an identical line;
                        // telling them apart needed a sibling node whose window was provably slack.
                        // Slot 0 catches anything unattributed, so a miscount reads as a number
                        // rather than as silence.
                        long[] refusedUnits = new long[5];
                        long unpricedUnits = 0;

                        if (fleet.Cargo != null && fleet.Cargo.Count > 0)
                        {
                            // Real goods aboard: settle per item in the destination's own scarcity
                            // order (most-needed first), capped by what the recipient can actually
                            // afford right now — nothing here is decided at dispatch time. Whatever
                            // doesn't sell, because the recipient is out of budget or already
                            // oversupplied on it, rides back home in the hold.
                            var items = new List<(string key, long qty, long unitPrice, double scarcity)>();
                            foreach (var kv in fleet.Cargo)
                            {
                                long qty = (long)(kv.Value * ratio);
                                if (qty <= 0) continue;
                                ItemKind ik;
                                bool known = world.TryGetInventoryItemKind(kv.Key, out ik);
                                long unitPrice = prices != null ? prices.BasePrice(kv.Key) : 0;
                                if (unitPrice <= 0 && known) unitPrice = tuning.FallbackFor(ik);
                                // Per inventory key. Asking by kind let a node's cheap bulk answer
                                // for the one item it holds none of: a shipyard sitting on 107,094
                                // stone/iron/silicon ingots read as oversupplied in Ingot and
                                // refused every gram of gold flown to it (FAC-286).
                                double scarcity = oracle != null && fleet.TargetNodeId != null
                                    ? oracle.ScarcityMultiplier(fleet.TargetNodeId, kv.Key) : 1.0;
                                items.Add((kv.Key, qty, unitPrice, scarcity));
                            }
                            items.Sort((a, b) => b.scarcity.CompareTo(a.scarcity)); // most-needed first

                            long floorHeadroom = long.MaxValue;
                            long windowRemaining = long.MaxValue;
                            if (recipientState != null)
                            {
                                long headroom = world.GetTreasury(fleet.TargetTag) - tuning.InsolvencyFloor;
                                floorHeadroom = headroom > 0 ? headroom : 0;
                                windowRemaining = tradeInboundBudget != null
                                    ? tradeInboundBudget.RemainingBudget(fleet.TargetTag, world.UtcNow,
                                        recipientState.CentralBankRecentIncome, tuning.TradeInboundMaxIncomeFractionPerWindow,
                                        tuning.TradeInboundBudgetFloor, tuning.SpendBudgetWindowSeconds,
                                        InboundWealthSurplus(fleet.TargetTag, world, tuning),
                                        tuning.TradeInboundWealthFractionPerWindow)
                                    : long.MaxValue;
                            }

                            var returned = new Dictionary<string, long>();
                            // A refused counterparty stops the loop before its first item, so every
                            // item rides home through the same path an unsold remainder takes.
                            bool stopped = refusal != null;
                            // The gate that set `stopped`, so the items behind it are attributed to
                            // the cause rather than all defaulting to the floor.
                            int stopReason = refusal != null ? TradeRefusalCounterparty : TradeRefusalOther;
                            for (int idx = 0; idx < items.Count; idx++)
                            {
                                var it = items[idx];

                                if (stopped || (recipientState != null && it.scarcity <= tuning.TradeScarcityFloor))
                                {
                                    // Sorted descending — once one item falls at/below the oversupply
                                    // floor, everything after it is equally-or-less wanted.
                                    if (!stopped) stopReason = TradeRefusalFloor;
                                    stopped = true;
                                    returned[it.key] = it.qty;
                                    refusedUnits[stopReason] += it.qty;
                                    continue;
                                }

                                long sellQty = it.qty;
                                if (recipientState != null && it.unitPrice > 0)
                                {
                                    long affordableByFloor  = ItemCredits.Affordable(it.key, floorHeadroom, it.unitPrice);
                                    long affordableByWindow = ItemCredits.Affordable(it.key, windowRemaining, it.unitPrice);
                                    long affordable = affordableByFloor < affordableByWindow ? affordableByFloor : affordableByWindow;
                                    if (affordable < sellQty)
                                    {
                                        sellQty = affordable;
                                        // Which of the two caps bound, so the line says whether the
                                        // recipient is out of money or merely out of window.
                                        stopReason = affordableByFloor < affordableByWindow
                                            ? TradeRefusalHeadroom : TradeRefusalWindow;
                                    }
                                }
                                else if (recipientState != null && it.qty > 0)
                                {
                                    // Unpriced: the affordability cap above cannot be evaluated, so
                                    // the whole quantity lands and no money moves either way. Counted
                                    // because it is delivery without settlement, not a refusal.
                                    unpricedUnits += it.qty;
                                }

                                if (sellQty > 0)
                                {
                                    if (fleet.TargetNodeId != null)
                                        world.AddNodeInventory(fleet.TargetNodeId, it.key, sellQty);  // it.key is SubTypeId
                                    else
                                        world.AddSubTypeInventory(fleet.TargetTag, it.key, sellQty);

                                    long value = ItemCredits.For(it.key, sellQty, it.unitPrice);
                                    if (value > 0 && recipientState != null)
                                    {
                                        // The sale term, credited at the SELLER's node: a paying
                                        // counterparty with its own budget took these units, which
                                        // is what makes this shelf worth keeping stocked.
                                        world.RecordUnitsSold(fleet.OriginNodeId, it.key, sellQty);
                                        world.Earn(fleet.ActorTag, value);
                                        // Buyer side now tracked as a real expense too (symmetric with
                                        // the seller's Earn above) so a faction's own trade-purchase
                                        // spend shows up in its GetNetFlow/expense figures.
                                        world.Spend(fleet.TargetTag, value);
                                        floorHeadroom -= value;
                                        windowRemaining -= value;
                                        settledTotal += value;
                                    }
                                }

                                long leftover = it.qty - sellQty;
                                if (leftover > 0)
                                {
                                    returned[it.key] = leftover;
                                    refusedUnits[stopReason] += leftover;
                                    if (recipientState != null) stopped = true; // ran out of budget here
                                }
                            }

                            if (settledTotal > 0 && tradeInboundBudget != null)
                                tradeInboundBudget.Record(fleet.TargetTag, world.UtcNow, settledTotal, tuning.SpendBudgetWindowSeconds);

                            bool hasReturnLeg = fleet.RemainingStops != null && fleet.RemainingStops.Count > 0;
                            if (hasReturnLeg)
                            {
                                // The physical/abstract hold stays with the convoy until its explicit
                                // return stop. Destination inventory and treasury settlement above are
                                // the service-stop economics; only unsold goods remain aboard.
                                fleet.Cargo = returned.Count > 0 ? returned : null;
                                foreach (var kv in returned)
                                {
                                    long unitPrice;
                                    foreach (var it in items)
                                        if (it.key == kv.Key) { unitPrice = it.unitPrice; returnedTotal += ItemCredits.For(kv.Key, kv.Value, unitPrice); break; }
                                }
                            }
                            else if (returned.Count > 0 && fleet.OriginNodeId != null)
                            {
                                var priceByKey = new Dictionary<string, long>();
                                foreach (var it in items) priceByKey[it.key] = it.unitPrice;
                                foreach (var kv in returned)
                                {
                                    world.AddNodeInventory(fleet.OriginNodeId, kv.Key, kv.Value);
                                    long unitPrice;
                                    priceByKey.TryGetValue(kv.Key, out unitPrice);
                                    returnedTotal += ItemCredits.For(kv.Key, kv.Value, unitPrice);
                                }
                            }
                        }
                        else if (fleet.CargoValue > 0 && recipientState != null && refusal == null)
                        {
                            // No real item manifest (nothing was available to load at dispatch) —
                            // settle the nominal flat fee as before; nothing to gate on scarcity or
                            // per-item affordability without item-level data.
                            settledTotal = (long)(fleet.CargoValue * ratio);
                            world.Earn(fleet.ActorTag, settledTotal);
                            world.Spend(fleet.TargetTag, settledTotal);
                        }

                        if (refusal == null)
                            world.RecordEvent(FactionEventKind.TradeResolved, fleet.ActorTag, fleet.TargetTag,
                                              fleet.TargetNodeId, settledTotal, ratio);
                        log.Add("TRADE_RESOLVED " + fleet.LogId + " " + fleet.ActorTag + " -> " + fleet.TargetTag
                                + " ratio=" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + " settled=" + settledTotal
                                + (returnedTotal > 0 ? " returned=" + returnedTotal : "")
                                + (refusedUnits[TradeRefusalFloor]    > 0 ? " refused_floor="    + refusedUnits[TradeRefusalFloor]    : "")
                                + (refusedUnits[TradeRefusalWindow]   > 0 ? " refused_budget="   + refusedUnits[TradeRefusalWindow]   : "")
                                + (refusedUnits[TradeRefusalHeadroom] > 0 ? " refused_headroom=" + refusedUnits[TradeRefusalHeadroom] : "")
                                + (refusedUnits[TradeRefusalOther]    > 0 ? " refused_other="    + refusedUnits[TradeRefusalOther]    : "")
                                + (refusedUnits[TradeRefusalCounterparty] > 0 ? " refused_counterparty=" + refusedUnits[TradeRefusalCounterparty] : "")
                                + (refusal != null ? " refused=" + refusal : "")
                                + (unpricedUnits > 0 ? " unpriced=" + unpricedUnits : ""));
                    }
                    break;
                }
                case ActionKind.Gift:
                {
                    // The sender pays; only the recipient gains a diplomatic benefit.
                    FactionState recipient = null;
                    for (int k = 0; k < states.Count; k++)
                        if (states[k].Tag == fleet.TargetTag) { recipient = states[k]; break; }
                    if (recipient != null && fleet.ActorTag != null)
                    {
                        Diplomacy.GiftRelation(recipient, fleet.ActorTag, tuning);
                        world.RecordEvent(FactionEventKind.GiftResolved, fleet.ActorTag, fleet.TargetTag,
                                          fleet.TargetNodeId, fleet.CargoValue);
                        log.Add("GIFT_RESOLVED " + fleet.LogId + " " + fleet.ActorTag + " -> " + fleet.TargetTag
                                + " rel+" + tuning.GiftRelationBoost.ToString("0.000", CultureInfo.InvariantCulture));
                    }
                    break;
                }
                case ActionKind.Reinforce:
                {
                    if (fleet.TargetNodeId != null)
                    {
                        SpatialNode holdNode;
                        world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out holdNode);
                        string blockedBy = AttackInProgressAt(states, world, owner, fleet.TargetNodeId, holdNode);
                        if (blockedBy != null)
                        {
                            // A reinforce cannot bust through a live siege to undo the raid that is
                            // running. The hull turns for home on the queued Return leg and the cost
                            // stays spent.
                            log.Add("REINFORCE_BLOCKED " + fleet.LogId + " " + fleet.ActorTag
                                    + " @ " + fleet.TargetNodeId + " by=" + blockedBy
                                    + " hold=" + (holdNode != null ? holdNode.HoldStrength : 0).ToString("0.00", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            double holdBefore = holdNode != null ? holdNode.HoldStrength : 0;
                            world.Reinforce(fleet.ActorTag, fleet.TargetNodeId, ratio);
                            double holdAfter = holdNode != null ? holdNode.HoldStrength : 0;
                            log.Add("REINFORCE_RESOLVED " + fleet.LogId + " " + fleet.ActorTag
                                    + " @ " + fleet.TargetNodeId
                                    + " ratio=" + ratio.ToString("0.00", CultureInfo.InvariantCulture)
                                    + " hold=" + holdBefore.ToString("0.00", CultureInfo.InvariantCulture)
                                    + "->" + holdAfter.ToString("0.00", CultureInfo.InvariantCulture));
                        }
                    }
                    break;
                }
                case ActionKind.Guard:
                {
                    // Reduce threat at the target node on first arrival (effect only fires once — Resolved guards re-fire)
                    // A Guard withdrawn as it arrived (SettleLostGround) stands no window there.
                    if (fleet.TargetNodeId != null && fleet.RetreatReason == null)
                    {
                        SpatialNode n;
                        string thrTag = "";
                        if (world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out n))
                        {
                            n.Threat = n.Threat > 0.05 ? n.Threat - 0.05 : 0;
                            thrTag = " thr->" + n.Threat.ToString("0.00", CultureInfo.InvariantCulture);
                        }
                        log.Add("GUARD_ARRIVED " + fleet.LogId + " " + fleet.ActorTag + " @ " + fleet.TargetNodeId
                                + thrTag + " (holds " + tuning.GuardDuration.TotalMinutes + " min)");
                    }
                    break;
                }
                case ActionKind.Patrol:
                {
                    // Perimeter sweep: reduce threat at the waypoint and log transit
                    if (fleet.TargetNodeId != null)
                    {
                        SpatialNode n;
                        string thrTag = "";
                        bool stands = false;
                        bool raidInbound = false;
                        string defender = null;
                        if (world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out n))
                        {
                            n.Threat = n.Threat > 0.05 ? n.Threat - 0.05 : 0;
                            thrTag = " thr->" + n.Threat.ToString("0.00", CultureInfo.InvariantCulture);

                            // A sweep that arrives to find the node still threatened stays and mans
                            // the guns. It becomes a Guard rather than gaining defensive rules of
                            // its own: Guard is the only kind either meeting path accepts as a
                            // defender, so a Patrol that leaves is why a raid landing a minute later
                            // met nothing. Dropping the queued Return leg hands the hull to the
                            // Guard duty window -- ReturnGuardShip credits it at this same node when
                            // the window expires, which is where a Patrol's hull ended up anyway.
                            //
                            // Three conditions, and each refuses a case that would make things
                            // worse rather than better:
                            //   - nothing worth staying for: a raid is on its way here, or the node is
                            //     still hot enough on its own to be worth a garrison. Threat merely
                            //     being non-zero is not enough -- a circuit visits every member of its
                            //     focus region, so nearly all of them clear that bar.
                            //   - a defender already here, in any faction's colours, and no raid to
                            //     fight: a hot node needs one garrison, and a second would tie up a
                            //     hull for no added cover. With a raid aimed here the patrol joins
                            //     instead -- every Guard at the node duels the raid as one force, so
                            //     the second hull is cover, and refusing it sent 70 of 71 diverted
                            //     patrols home from the fight they were diverted to (FAC-321).
                            //   - no weapons on record: a bandless fleet duels at zero DPS, so
                            //     standing would occupy the node's one defender slot with a hull
                            //     that cannot fight and block a real Guard from being sent.
                            raidInbound = HasIncomingRaid(states, world, fleet.TargetNodeId);
                            defender = DefenderAt(states, fleet.TargetNodeId, si, fi);
                            stands = (raidInbound || n.Threat >= tuning.PatrolStandThreatFloor)
                                     && fleet.WeaponBands != null && fleet.WeaponBands.Count > 0
                                     && (defender == null || raidInbound);
                        }

                        if (stands)
                        {
                            fleet.Kind = ActionKind.Guard;
                            // A patrol joining a defence keeps its circuit and flies it again once the
                            // raid is gone (ResumeSuspendedCircuits); one standing alone garrisons
                            // the node for the duty window, as before.
                            bool joins = defender != null;
                            if (joins && fleet.RemainingStops != null && fleet.RemainingStops.Count > 0)
                                fleet.SuspendedStops = fleet.RemainingStops;
                            fleet.RemainingStops = null;
                            log.Add("PATROL_STANDS " + fleet.LogId + " " + fleet.ActorTag
                                    + " @ " + fleet.TargetNodeId + thrTag
                                    + " why=" + (raidInbound ? "raid" : "threat")
                                    + (joins ? " beside=" + defender : "")
                                    + (fleet.SuspendedStops != null
                                        ? " (holds until the raid ends)"
                                        : " (holds " + tuning.GuardDuration.TotalMinutes + " min)"));
                        }
                        else
                        {
                            log.Add("PATROL_AT " + fleet.LogId + " " + fleet.ActorTag + " @ " + fleet.TargetNodeId + thrTag);
                        }
                    }
                    break;
                }
                case ActionKind.Ferry:
                {
                    // Ship delivery: stock transfer is handled in the lifecycle section below.
                    log.Add("FERRY_ARRIVED " + fleet.LogId + " " + fleet.ActorTag
                            + " role=" + (fleet.FerryRole ?? "?") + " -> " + fleet.TargetNodeId);
                    break;
                }
                case ActionKind.Haul:
                {
                    // Own stock moving between own nodes: credit the destination directly, no
                    // scarcity/affordability gate and no settlement — this is not a sale (contrast
                    // Trade above). Scaled by ratio for consistency with every other kind, though in
                    // practice an unarmed Haul convoy either arrives whole or is Intercepted outright.
                    //
                    // Inbound neglect clock updates unconditionally, even if delivered ends up 0 (a
                    // near-total interception) -- "traffic touched this node" is the point, independent
                    // of what actually got delivered. docs/material-vectors.md's DemandHistory addendum.
                    SpatialNode haulDest;
                    if (fleet.TargetNodeId != null && world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out haulDest))
                        haulDest.LastHaulArrivedUtc = world.UtcNow;

                    // DemandHistory is NOT credited here (or on the bootstrap-instant fallback) --
                    // it's credited on actual consumption (ProductionManager's recipe-input use,
                    // ShipyardManager's BOM absorption) instead of delivery, since a delivery-fed
                    // gravity term is a pure self-reinforcing loop with no external anchor
                    // (docs/material-vectors.md's dated correction note has the full account).
                    long delivered = 0;
                    List<KeyValuePair<string, long>> deliveredItems = null;
                    if (fleet.TargetNodeId != null && fleet.Cargo != null)
                    {
                        foreach (var kv in fleet.Cargo)
                        {
                            long qty = (long)(kv.Value * ratio);
                            if (qty <= 0) continue;
                            world.AddNodeInventory(fleet.TargetNodeId, kv.Key, qty);
                            delivered += qty;
                            if (deliveredItems == null) deliveredItems = new List<KeyValuePair<string, long>>(2);
                            deliveredItems.Add(new KeyValuePair<string, long>(kv.Key, qty));
                        }
                    }
                    if (fleet.RelocationRole != null)
                    {
                        // Resolved here as well as in the ship-lifecycle block below so the pair does
                        // not contradict itself: this line names where the hull is, and the credit
                        // that follows puts it there. Both calls are pure and agree by construction.
                        ShipPool.Landing arrival = ratio > 0.5
                            ? ResolveReturnNode(world, fleet, fleet.TargetNodeId, fleet.OriginNodeId)
                            : new ShipPool.Landing();
                        log.Add("SHIP_RELOCATE_ARRIVED " + fleet.LogId + " " + fleet.ActorTag
                                + " role=" + fleet.RelocationRole
                                + " -> " + (arrival.NodeId ?? fleet.TargetNodeId)
                                + arrival.RedirectSuffix);
                    }
                    else
                    {
                        // Only a haul that actually carried something. An arrival with an empty
                        // hold is a real leg but not information a player can act on, and it is
                        // common enough (live: 4 of the first 6 EXP hauls filed) to spend most of a
                        // 60-deep ring saying nothing.
                        if (delivered > 0)
                            world.RecordEvent(FactionEventKind.HaulResolved, fleet.ActorTag, null,
                                              fleet.TargetNodeId, delivered, ratio);
                        log.Add("HAUL_RESOLVED " + fleet.LogId + " " + fleet.ActorTag
                                + " -> " + fleet.TargetNodeId + " delivered=" + delivered
                                + DescribeDeliveredItems(deliveredItems));
                    }
                    break;
                }
                case ActionKind.Mine:
                {
                    // Ore was already minted into the source node's inventory and loaded aboard when
                    // the Mine dwell leg resolved (this method's own FleetStopKind.Mine pre-check,
                    // above) -- arrival at the real destination just credits it the same way Haul
                    // does. Not a sale (own extraction, own network), no scarcity/affordability gate.
                    long delivered = 0;
                    if (fleet.TargetNodeId != null && fleet.Cargo != null)
                    {
                        SpatialNode mineDest;
                        world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out mineDest);
                        foreach (var kv in fleet.Cargo)
                        {
                            long qty = (long)(kv.Value * ratio);
                            if (qty <= 0) continue;
                            world.AddNodeInventory(fleet.TargetNodeId, kv.Key, qty);
                            delivered += qty;
                            // The extraction planner's mined term: what arrived, at the hub it reached.
                            ExtractionFlow.CreditMined(mineDest, world.UtcNow, tuning, kv.Key, qty);
                        }
                    }
                    world.RecordEvent(FactionEventKind.MineResolved, fleet.ActorTag, null,
                                      fleet.TargetNodeId, delivered, ratio);
                    log.Add("MINE_RESOLVED " + fleet.LogId + " " + fleet.ActorTag
                            + " -> " + fleet.TargetNodeId + " delivered=" + delivered);
                    break;
                }
            }

            // A service stop with a queued itinerary continues to its next leg. This deliberately
            // happens after the service effect (and instant money settlement) but before any ship or
            // residual-cargo lifecycle work. Legacy fleets have no queued stop and continue through
            // the original single-leg completion path below, which is their reboot-safe resolution.
            if (fleet.RemainingStops != null && fleet.RemainingStops.Count > 0)
            {
                if (AdvanceToNextStop(owner, fi, fleet, world, tuning, log)) return;
                fleet.RemainingStops = null;
            }

            // Ship lifecycle: Trade/Raid/Patrol/Gift/Mine each consume one ship at dispatch.
            // ratio > 0.5 → mission succeeded, ship becomes reusable at its return node.
            // ratio ≤ 0.5 → ship is lost / damaged beyond repair (consumed permanently).
            // Guard consumes a Patrol ship too, but it only returns when the guard window expires.
            // Mine comes home to ReturnNodeId same as Trade/Raid/Gift -- the last real node the
            // fleet departed from (AdvanceToNextStop pins it), which for Mine is always the dwell
            // site in both the direct-dispatch and via-pickup tiers: the drill ship comes home to
            // the deposit it works, not to the delivery destination. The per-leg OriginNodeId is
            // null on a terminal leg that departs a clearance waypoint, so it is only the fallback.
            if ((fleet.Kind == ActionKind.Trade || fleet.Kind == ActionKind.Raid
                || fleet.Kind == ActionKind.Retaliate || fleet.Kind == ActionKind.Conquer
                || fleet.Kind == ActionKind.Patrol || fleet.Kind == ActionKind.Gift
                || fleet.Kind == ActionKind.Mine || fleet.Kind == ActionKind.Reinforce)
                && (ratio > HullReturnThreshold || (fleet.HullsSettled && LandsWhatItCarries(fleet))))
            {
                // Trade/Raid/Retaliate/Gift: ship returns home. Patrol: ship stays at target.
                // The dispatch origin is the fallback, and covers a fleet persisted before
                // ReturnNodeId existed as well as one whose home node has since been deleted.
                ShipPool.Landing landing = ResolveReturnNode(world, fleet,
                    fleet.Kind == ActionKind.Patrol ? fleet.TargetNodeId : fleet.ReturnNodeId,
                    fleet.OriginNodeId);
                SpatialNode returnNode = landing.Node;
                string returnNodeId = landing.NodeId;
                string returnRedirect = landing.RedirectSuffix;
                if (returnNodeId != null)
                {
                    FleetMunitions.Credit(ref fleet, returnNode, fleet.HullsToReturn.Count > 0
                                          ? fleet.HullsToReturn.Count : fleet.HullCount, world, log);
                    if (fleet.Squadron != null && fleet.Squadron.Count > 0)
                    {
                        // Mixed squadron: no role bucket to route back to — each member just credits
                        // its own prefab back to the unified pool.
                        ReturnSquadronToNode(world, fleet, returnNode, returnNodeId, returnRedirect, log);
                    }
                    else
                    {
                        int returned = Math.Max(1, fleet.ConvoySize); // floor: legacy/free-built fleets may have ConvoySize unset
                        HullDamage.Land(world, returnNode, returnNodeId, fleet.ShipPrefabId, returned,
                                        fleet.IntegrityOf(0), fleet.LogId, fleet.ActorTag, log);
                        int poolStock;
                        returnNode.ShipStock.TryGetValue(fleet.ShipPrefabId ?? "", out poolStock);
                        log.Add("SHIP_RETURNED " + fleet.ActorTag + " prefab=" + (fleet.ShipPrefabId ?? "")
                                + " node=" + returnNodeId + " count=" + returned + " stock=" + poolStock
                                + returnRedirect);
                    }
                }
                else
                {
                    // Nowhere left to land it. The hull is gone either way, but a hull that
                    // disappears without a line is invisible to every count that would notice a
                    // faction quietly running out of ships.
                    world.RecordEvent(FactionEventKind.ShipLost, fleet.ActorTag, fleet.TargetTag,
                                      fleet.TargetNodeId, Math.Max(1, fleet.ConvoySize), ratio);
                    log.Add("SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag
                            + LossDetail(fleet, ratio, NoLandingWhy(landing)));
                }
            }
            else if (fleet.Kind == ActionKind.Trade || fleet.Kind == ActionKind.Raid
                || fleet.Kind == ActionKind.Retaliate || fleet.Kind == ActionKind.Conquer
                || fleet.Kind == ActionKind.Patrol || fleet.Kind == ActionKind.Gift
                || fleet.Kind == ActionKind.Mine || fleet.Kind == ActionKind.Reinforce)
            {
                // A squadron that lost its physical grids still brings home whatever was not shot
                // down: the mission failed, but only the hulls that actually died are consumed.
                // The mission ratio stays what it was -- it means completion, not survivorship.
                if (ReturnsSurvivingHulls(fleet))
                {
                    ShipPool.Landing landing = ResolveReturnNode(world, fleet,
                        fleet.Kind == ActionKind.Patrol ? fleet.TargetNodeId : fleet.ReturnNodeId,
                        fleet.OriginNodeId);
                    int credited = landing.NodeId != null
                        ? ReturnSquadronToNode(world, fleet, landing.Node, landing.NodeId, landing.RedirectSuffix, log) : 0;
                    FleetMunitions.Credit(ref fleet, landing.Node, credited, world, log);
                    int casualties = fleet.HullCount - credited;
                    if (casualties > 0)
                    {
                        world.RecordEvent(FactionEventKind.ShipLost, fleet.ActorTag, fleet.TargetTag,
                                          fleet.TargetNodeId, casualties, ratio);
                        log.Add("SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag + LossDetail(fleet, ratio)
                                + " hulls_returned=" + credited + "/" + fleet.HullCount);
                    }
                }
                else
                {
                    world.RecordEvent(FactionEventKind.ShipLost, fleet.ActorTag, fleet.TargetTag,
                                      fleet.TargetNodeId, Math.Max(1, fleet.ConvoySize), ratio);
                    log.Add("SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag + LossDetail(fleet, ratio));
                }
            }

            // Ferry: on successful arrival deliver the ship to the destination node.
            // On interception/loss the ship is destroyed; the Position: demand TTL keeps it active
            // so FleetManager re-attempts from remaining stock next tick.
            if (fleet.Kind == ActionKind.Ferry)
            {
                if (ratio > 0.5 && fleet.FerryRole != null && fleet.TargetNodeId != null)
                {
                    // The delivery node can have decayed to UNKN while the ferry was in the air --
                    // delivering there hands the hull to the graveyard the grace sweep empties.
                    ShipPool.Landing landing = ResolveReturnNode(world, fleet, fleet.TargetNodeId, fleet.OriginNodeId);
                    if (landing.NodeId != null)
                    {
                        int delivered = Math.Max(1, fleet.ConvoySize);
                        HullDamage.Land(world, landing.Node, landing.NodeId, fleet.ShipPrefabId, delivered,
                                        fleet.IntegrityOf(0), fleet.LogId, fleet.ActorTag, log);
                        FleetMunitions.Credit(ref fleet, landing.Node, delivered, world, log);
                        int poolStock;
                        landing.Node.ShipStock.TryGetValue(fleet.ShipPrefabId ?? "", out poolStock);
                        log.Add("FERRY_DELIVERED " + fleet.ActorTag + " role=" + fleet.FerryRole
                                + " node=" + landing.NodeId + " count=" + delivered + " stock=" + poolStock
                                + (fleet.ShipPrefabId != null && fleet.ShipPrefabId.Length > 0
                                   ? " prefab=" + fleet.ShipPrefabId : "")
                                + landing.RedirectSuffix);
                    }
                    else
                    {
                        world.RecordEvent(FactionEventKind.ShipLost, fleet.ActorTag, fleet.TargetTag,
                                          fleet.TargetNodeId, Math.Max(1, fleet.ConvoySize), ratio);
                        log.Add("SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag
                                + LossDetail(fleet, ratio, NoLandingWhy(landing)));
                    }
                }
                else if (ratio <= 0.5)
                {
                    log.Add("FERRY_LOST " + fleet.LogId + " " + fleet.ActorTag
                            + " role=" + (fleet.FerryRole ?? "?") + LossDetail(fleet, ratio));
                }
            }

            // Haul: one-way like Ferry (no return leg) — the hauler ship itself is credited to the
            // destination it just delivered to, not consumed permanently, mirroring Ferry's own
            // ship-lifecycle exactly (contrast Trade/Raid/Gift above, which return to origin).
            if (fleet.Kind == ActionKind.Haul)
            {
                if (ratio > 0.5 && fleet.TargetNodeId != null)
                {
                    // Hold decay runs 100-200 nodes a day, so a destination claimed by Expand
                    // routinely goes UNKN inside one haul's flight time -- the hull is credited to
                    // wherever the actor still owns instead, relocation hauls included.
                    ShipPool.Landing landing = ResolveReturnNode(world, fleet, fleet.TargetNodeId, fleet.OriginNodeId);
                    if (landing.NodeId != null && fleet.Squadron != null && fleet.Squadron.Count > 0)
                    {
                        // An escorted haul (TransitPlanner) lands hull by hull: the escort stays with
                        // the hauler it flew beside, as its own prefab.
                        int landed = ReturnSquadronToNode(world, fleet, landing.Node, landing.NodeId,
                                                          landing.RedirectSuffix, log);
                        FleetMunitions.Credit(ref fleet, landing.Node, landed, world, log);
                    }
                    else if (landing.NodeId != null)
                    {
                        int delivered = Math.Max(1, fleet.ConvoySize);
                        HullDamage.Land(world, landing.Node, landing.NodeId, fleet.ShipPrefabId, delivered,
                                        fleet.IntegrityOf(0), fleet.LogId, fleet.ActorTag, log);
                        FleetMunitions.Credit(ref fleet, landing.Node, delivered, world, log);
                        int poolStock;
                        landing.Node.ShipStock.TryGetValue(fleet.ShipPrefabId ?? "", out poolStock);
                        log.Add("HAUL_SHIP_RETURNED " + fleet.ActorTag + " node=" + landing.NodeId
                                + " count=" + delivered + " stock=" + poolStock
                                + (fleet.ShipPrefabId != null && fleet.ShipPrefabId.Length > 0
                                   ? " prefab=" + fleet.ShipPrefabId : "")
                                + landing.RedirectSuffix);
                    }
                    else
                    {
                        // HAUL_SHIP_LOST, not SHIP_LOST: one verb counts every hauler hull this path
                        // loses, whatever lost it.
                        world.RecordEvent(FactionEventKind.ShipLost, fleet.ActorTag, fleet.TargetTag,
                                          fleet.TargetNodeId, Math.Max(1, fleet.ConvoySize), ratio);
                        log.Add("HAUL_SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag
                                + LossDetail(fleet, ratio, NoLandingWhy(landing)));
                    }
                }
                else if (ratio <= 0.5)
                {
                    log.Add("HAUL_SHIP_LOST " + fleet.LogId + " " + fleet.ActorTag + LossDetail(fleet, ratio));
                }
            }

            var resolved = fleet;
            resolved.Resolved = true;
            owner.PendingFleets[fi] = resolved;
        }

        /// <summary>What a captured node handed its conqueror (<see cref="NodeHoldings.Suffix"/>), for
        /// the <c>CONQUER_RESOLVED ... [captured]</c> line. Empty when the node is gone.</summary>
        private static string CapturedHoldings(WorldModel world, string nodeId)
        {
            SpatialNode node;
            return nodeId != null && world.Graph.Nodes.TryGetValue(nodeId, out node)
                ? NodeHoldings.Of(node).Suffix() : "";
        }

        /// <summary>The <c>items=</c> half of a HAUL_RESOLVED line: what reached the destination, per
        /// inventory key, as <c>key=qty</c> joined by commas in ordinal key order. <c>delivered=</c>
        /// sums units across keys, which cannot say what a consolidated hold delivered or be split
        /// into gas and solids; this can, from the resolution alone. Empty when nothing arrived.</summary>
        internal static string DescribeDeliveredItems(List<KeyValuePair<string, long>> items)
        {
            if (items == null || items.Count == 0) return "";
            items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            var sb = new System.Text.StringBuilder(" items=");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(items[i].Key).Append('=').Append(items[i].Value.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Attribution suffix shared by every ship-loss line: which fleet, which hull, how
        /// many, which leg, and where the resolving ratio came from. <c>why=forced:&lt;cause&gt;</c>
        /// names the writer that set <see cref="PendingFleetAction.ForcedRatio"/>;
        /// <c>why=elapsed</c> means the ratio is the ordinary transit-progress one. A caller that
        /// loses a hull for a reason the ratio does not explain passes <paramref name="whyOverride"/>
        /// instead (<c>why=return_node_missing</c>: the mission succeeded, but there was nowhere left
        /// to credit the hull to).</summary>
        internal static string LossDetail(PendingFleetAction fleet, double ratio, string whyOverride = null)
        {
            return " prefab=" + (fleet.ShipPrefabId ?? "")
                   + " count=" + Math.Max(1, fleet.ConvoySize)
                   + " from=" + (fleet.OriginNodeId ?? "?")
                   + " to=" + (fleet.TargetNodeId ?? "?")
                   + " ratio=" + ratio.ToString("0.00", CultureInfo.InvariantCulture)
                   + " why=" + (whyOverride ?? (fleet.ForcedRatio.HasValue
                                ? "forced:" + (fleet.ForcedRatioCause ?? "unknown")
                                : "elapsed"));
        }

        /// <summary>The node a surviving hull is credited to: <paramref name="preferredNodeId"/>
        /// when it still exists AND the fleet's actor still owns it, otherwise
        /// <paramref name="fallbackNodeId"/> (the fleet's dispatch origin) on the same terms,
        /// otherwise the nearest owned node the hull can reach. <see cref="ShipPool.Landing.NodeId"/>
        /// is null when nothing resolves -- the caller logs the loss rather than deleting the hull in
        /// silence.
        /// <para>Provenance: FAC-277 (existence), FAC-278 (ownership: a hull credited to a node the
        /// faction no longer owns is parked on UNKN territory and deleted with it).</para></summary>
        private static ShipPool.Landing ResolveReturnNode(WorldModel world, PendingFleetAction fleet,
                                                          string preferredNodeId, string fallbackNodeId)
        {
            // The fleet's terminal position anchors the nearest-owned scan when BOTH named nodes have
            // been removed from the graph -- without it a hull whose home and origin were both swept
            // is destroyed while the faction still owns reachable ground.
            return ShipPool.ResolveLanding(world, fleet.ActorTag, fleet.ShipPrefabId,
                                           preferredNodeId, fallbackNodeId, fleet.TargetPosKm);
        }

        /// <summary>Why a hull with nowhere to land was lost: the pre-ownership rule found no node at
        /// all (<c>return_node_missing</c>, unchanged), or it found one the actor no longer owns and
        /// nothing else was reachable (<c>no_owned_node</c>).
        /// <para>Provenance: FAC-278.</para></summary>
        private static string NoLandingWhy(ShipPool.Landing landing)
        {
            return landing.PriorNodeId == null ? "return_node_missing" : "no_owned_node";
        }

        /// <summary>The <see cref="PendingFleetAction.LogId"/> of a Guard any faction already has
        /// standing at, or inbound to, <paramref name="nodeId"/>, or null when there is none —
        /// ignoring the fleet at <paramref name="selfStateIndex"/>/<paramref name="selfFleetIndex"/>,
        /// which is the one asking.
        ///
        /// <para>Every faction's Guards count, not just the node owner's: the meeting paths accept
        /// any Guard whose actor is not the raider as the node's defender, so an ally's Guard already
        /// covers it. Mirrors the one-defender-per-node rule the dispatch path enforces across
        /// factions with its own index. Kept here rather than shared because the two ask at different
        /// moments and only this one has to exclude itself.</para>
        /// <para>Provenance: FAC-276; returns the defender rather than a flag since FAC-321, so a
        /// patrol that joins it can say whom it joined.</para></summary>
        private static string DefenderAt(IList<FactionState> states, string nodeId, int selfStateIndex, int selfFleetIndex)
        {
            if (states == null || nodeId == null) return null;
            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                if (st == null) continue;
                for (int i = 0; i < st.PendingFleets.Count; i++)
                {
                    if (si == selfStateIndex && i == selfFleetIndex) continue;
                    PendingFleetAction f = st.PendingFleets[i];
                    if (f.Kind == ActionKind.Guard && !f.Intercepted
                        && string.Equals(f.TargetNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
                        return f.LogId;
                }
            }
            return null;
        }

        /// <summary>Sends a patrol that stood beside another defender back onto its circuit once no
        /// raid is aimed at the node any more — beaten, broken off, landed or turned for home, the
        /// fight it joined is over. The circuit resumes from the stop the patrol was flying to when it
        /// arrived, through the same <see cref="AdvanceToNextStop"/> every other itinerary uses.
        ///
        /// <para>A grid a player can see is left alone: the host owns a materialised fleet's legs,
        /// and the diversion that brought the patrol here refuses to move one for the same reason.
        /// It stands out its duty window instead and is handed back at the node, which is what every
        /// standing patrol did before.</para>
        ///
        /// <para>Provenance: FAC-321.</para></summary>
        private static void ResumeSuspendedCircuits(IList<FactionState> states, WorldModel world,
            FactionTuning tuning, IList<string> log)
        {
            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.Kind != ActionKind.Guard || f.SuspendedStops == null) continue;
                    if (f.Intercepted || !f.Resolved || f.MaterialisedEntityId != 0) continue;
                    // Withdrawn because the node left its side (SettleLostGround): going home is
                    // the answer already given, and the cleanup below gives it.
                    if (f.RetreatReason != null) continue;
                    if (HasIncomingRaid(states, world, f.TargetNodeId)) continue;

                    f.Kind = ActionKind.Patrol;
                    f.RemainingStops = f.SuspendedStops;
                    f.SuspendedStops = null;
                    log.Add("PATROL_RESUMES " + f.LogId + " " + f.ActorTag + " @ " + f.TargetNodeId
                            + " stops=" + f.RemainingStops.Count);
                    if (AdvanceToNextStop(st, fi, f, world, tuning, log)) continue;

                    // The next stop no longer exists. Stand out the window as an ordinary Guard;
                    // the cleanup below hands the hull back at this node when it ends.
                    f.Kind = ActionKind.Guard;
                    f.RemainingStops = null;
                    st.PendingFleets[fi] = f;
                }
            }
        }

        /// <summary>Who is attacking <paramref name="nodeId"/> right now, or null when nothing is.
        /// A non-allied Raid/Retaliate/Conquer fleet standing at the node -- holding its engagement
        /// dwell, or arrived and not yet resolved -- names its faction; a live player siege names
        /// itself.
        ///
        /// <para>Non-allied is the resolution code's own test: a different faction tag, minus anyone
        /// this faction holds a pact with (<see cref="Diplomacy.InPactWith"/>, the same call
        /// <see cref="MeetingNodeIsFriendly"/> and <see cref="CreditPactDefence"/> make).</para></summary>
        internal static string AttackInProgressAt(IList<FactionState> states, WorldModel world,
            FactionState owner, string nodeId, SpatialNode node)
        {
            DateTime now = world.UtcNow;
            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                if (st.Tag == owner.Tag) continue;
                if (Diplomacy.InPactWith(owner, st.Tag)) continue;
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.Resolved || f.Intercepted) continue;
                    if (f.Kind != ActionKind.Raid && f.Kind != ActionKind.Retaliate
                        && f.Kind != ActionKind.Conquer) continue;
                    if (!string.Equals(f.TargetNodeId, nodeId, StringComparison.Ordinal)) continue;
                    // Standing at the node: mid-dwell (HasArrived is false for the whole window) or
                    // arrived on the mission leg and not yet resolved.
                    if (f.CurrentStopKind != FleetStopKind.Engage && !f.HasArrived(now)) continue;
                    return st.Tag;
                }
            }
            if (node != null && node.ZoneBreachedUtc != default(DateTime)) return "player_siege";
            if (node != null && world.IsUnderPlayerAttack(node)) return "player_attack";
            return null;
        }


        /// <summary>Credits an expiring Guard's hull back into a ship pool — the same instant credit
        /// the kind has always taken, at the node it was standing over.
        ///
        /// <para>A Guard that stood over a node belonging to someone else is credited at its own
        /// origin node instead, and the line says which node it was defending. A hull lent to an ally
        /// comes home rather than being the price of alliance: a faction whose combat pool drained
        /// into its partners one Guard at a time would stop being able to defend them, or itself.
        /// The fallback is deliberate — an origin that is gone or no longer the actor's leaves the
        /// hull at the defended node, since a hull with nowhere of its own to land is better parked
        /// at the ally than deleted.</para>
        /// <para>Provenance: FAC-276; FAC-278 widened the fallback from the origin alone to the
        /// nearest owned node the hull can reach, so an origin that has itself decayed no longer
        /// leaves the hull standing on someone else's ground.</para></summary>
        private static void ReturnGuardShip(PendingFleetAction fleet, WorldModel world, IList<string> log)
        {
            if (fleet.IsBench) return;   // drew no hull, so returns none
            if (fleet.TargetNodeId == null) return;
            if (!ShipPool.RequiresShip(fleet.Kind)) return;

            SpatialNode defended;
            if (!world.Graph.Nodes.TryGetValue(fleet.TargetNodeId, out defended)) return;

            ShipPool.Landing landing = ShipPool.ResolveLanding(world, fleet.ActorTag, fleet.ShipPrefabId,
                                                              fleet.TargetNodeId, fleet.OriginNodeId);

            // Ally parking, unchanged: a hull with nowhere of its own to land is better left at the
            // node it was defending than deleted.
            SpatialNode returnNode = landing.Node ?? defended;
            string returnNodeId = landing.NodeId ?? fleet.TargetNodeId;
            string defendedNodeId = string.Equals(returnNodeId, fleet.TargetNodeId, StringComparison.Ordinal)
                ? null : fleet.TargetNodeId;

            string suffix = (defendedNodeId != null ? " defended=" + defendedNodeId : "")
                            + (landing.Reason == "nearest_owned" ? landing.RedirectSuffix : "");
            // A mixed Guard (FAC-312) lands member by member, casualties already taken out of
            // HullsToReturn; the single-prefab shape credits as it always did.
            if (fleet.Squadron != null && fleet.Squadron.Count > 0)
            {
                int landed = ReturnSquadronToNode(world, fleet, returnNode, returnNodeId, suffix, log);
                FleetMunitions.Credit(ref fleet, returnNode, landed, world, log);
                return;
            }
            int returned = Math.Max(1, fleet.ConvoySize);
            HullDamage.Land(world, returnNode, returnNodeId, fleet.ShipPrefabId, returned,
                            fleet.IntegrityOf(0), fleet.LogId, fleet.ActorTag, log);
            FleetMunitions.Credit(ref fleet, returnNode, returned, world, log);
            int poolStock;
            returnNode.ShipStock.TryGetValue(fleet.ShipPrefabId ?? "", out poolStock);
            log.Add("SHIP_RETURNED " + fleet.ActorTag + " prefab=" + (fleet.ShipPrefabId ?? "")
                    + " node=" + returnNodeId + " count=" + returned + " stock=" + poolStock + suffix);
        }

        /// <summary>Pays the owner of a defended node a standing lift toward the faction whose Guard
        /// destroyed a raid on it, and logs <c>PACT_DEFENDED</c>.
        ///
        /// <para>Only an ally is paid, and only for a kill: a pact that is never exercised earns
        /// nothing, a defence that fails earns nothing, a faction defending its own node earns
        /// nothing, and an unallied third party whose Guard happened to be standing there earns
        /// nothing either. The owner is read off the raid's destination rather than the meeting node,
        /// because a raid stopped mid-transit was still aimed at somebody.</para>
        /// <para>Provenance: FAC-276.</para></summary>
        private static void CreditPactDefence(
            PendingFleetAction raid, PendingFleetAction patrol,
            IList<FactionState> states, WorldModel world, FactionTuning tuning, IList<string> log)
        {
            if (tuning.PactDefenceRelationBoost <= 0) return;
            if (raid.IsBench) return;
            if (patrol.ActorTag == null || raid.TargetNodeId == null) return;

            SpatialNode defended;
            if (!world.Graph.Nodes.TryGetValue(raid.TargetNodeId, out defended)) return;
            string ownerTag = defended.OwnerFactionTag;
            if (ownerTag == null || ownerTag == patrol.ActorTag) return;

            FactionState ownerState = null;
            for (int i = 0; i < states.Count; i++)
                if (states[i] != null && states[i].Tag == ownerTag) { ownerState = states[i]; break; }
            if (ownerState == null) return;
            if (!Diplomacy.InPactWith(ownerState, patrol.ActorTag)) return;

            Diplomacy.LiftRelation(ownerState, patrol.ActorTag, tuning.PactDefenceRelationBoost);
            log.Add("PACT_DEFENDED " + ownerTag + " by " + patrol.ActorTag + " @" + raid.TargetNodeId
                    + " rel+" + tuning.PactDefenceRelationBoost.ToString("0.000", CultureInfo.InvariantCulture));
        }

    }
}
