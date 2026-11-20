using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text;

namespace Factions.Core
{
    /// <summary>
    /// Turns a fleet-bearing <see cref="FactionIntent"/> into a dispatched
    /// <see cref="PendingFleetAction"/>: origin choice, posture-scaled convoy size, squadron
    /// assembly or single-role stock consumption, and the fleet record itself. One implementation
    /// is shared by the Torch host and the offline sim so both run identical dispatch mechanics.
    /// <para>Every host-held dependency arrives as a parameter — the prefab/item catalogues, the
    /// blocked-report throttle and the log sink — so the class stays SE-free and side-effect-free
    /// beyond the world and the faction state it is handed.</para>
    /// </summary>
    public static class OffensiveDispatch
    {
        /// <summary>Lifetime of a <c>Ship:{role}</c> demand posted when squadron assembly finds a
        /// preferred slot empty.</summary>
        public static readonly TimeSpan ShipDemandTtl = TimeSpan.FromSeconds(30);

        /// <summary>How long one blocked-dispatch identity stays suppressed in
        /// <see cref="BlockedFleetReports"/>.</summary>
        public static readonly TimeSpan BlockedReportTtl = TimeSpan.FromMinutes(1);

        /// <summary>Diagnostic throttle for stock-gated combat actions: the same actor/kind/route
        /// combination reports <c>FLEET_BLOCKED</c> at most once per <see cref="BlockedReportTtl"/>,
        /// so a faction that re-picks the same impossible raid every tick writes one line, not
        /// hundreds. Purely a log filter — it holds no simulation state and is safe to clear at any
        /// time (the caller does so on reload).</summary>
        public sealed class BlockedFleetReports
        {
            private readonly Dictionary<string, DateTime> _reports = new Dictionary<string, DateTime>();

            public void Clear() { _reports.Clear(); }

            public bool ShouldReport(DateTime nowUtc, ActionKind kind, string actor, string originNodeId,
                                     string targetNodeId, string targetTag)
            {
                var expired = new List<string>();
                foreach (KeyValuePair<string, DateTime> report in _reports)
                    if ((nowUtc - report.Value) >= BlockedReportTtl)
                        expired.Add(report.Key);
                foreach (string key in expired) _reports.Remove(key);

                string identity = kind + "\u001f" + (actor ?? "unknown") + "\u001f" + (originNodeId ?? "unknown")
                                  + "\u001f" + (targetNodeId ?? "unknown") + "\u001f" + (targetTag ?? "unknown");
                if (_reports.ContainsKey(identity)) return false;
                _reports[identity] = nowUtc;
                return true;
            }
        }

        /// <summary>Kinds whose dispatch is worth a <c>FLEET_DISPATCH</c> line and a
        /// <see cref="FactionEventKind.FleetDispatched"/> record; everything else is ordinary
        /// convoy traffic.</summary>
        public static bool IsCombatFleetAction(ActionKind kind)
        {
            return kind == ActionKind.Raid
                || kind == ActionKind.Retaliate
                || kind == ActionKind.Conquer;
        }

        /// <summary>" squadron=PrefabA,PrefabB,..." for a mixed squadron dispatch, or "" for a
        /// single-role convoy (squadron null/empty) — appended to FLEET_DISPATCH so live logs show
        /// composition, not just a convoySize count.</summary>
        public static string SquadronLogSuffix(List<string> squadron)
        {
            if (squadron == null || squadron.Count == 0) return "";
            var sb = new StringBuilder(" squadron=");
            for (int i = 0; i < squadron.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(squadron[i]);
            }
            return sb.ToString();
        }

        /// <summary>The <c>Ship:{role}</c> demand role a dispatch of <paramref name="kind"/> draws
        /// from, or null when the kind needs no ship.</summary>
        public static string ShipRoleFor(ActionKind kind)
        {
            switch (kind)
            {
                case ActionKind.Trade:  return "Trade";
                case ActionKind.Gift:   return "Trade";
                case ActionKind.Raid:
                case ActionKind.Retaliate:
                case ActionKind.Conquer: return "Raid";
                case ActionKind.Guard:  return "Patrol";
                case ActionKind.Patrol: return "Patrol";
                // A reinforce demands the same combat hull a patrol does; the role vocabulary has no
                // fifth entry for it (see ShipPool.IsSuitableForRole).
                case ActionKind.Reinforce: return "Patrol";
                default: return null;
            }
        }

        /// <summary>Pick the faction node to dispatch from: the nearest owned node that actually has a
        /// route-feasible ship of the required role in stock, falling back to the plain nearest-to-target
        /// node when the action needs no ship (role == null) or when NO owned node has any route-feasible
        /// stock for that role (in which case <see cref="CheckAndConsumeShipStock"/> returns null and the
        /// dispatch aborts for free). This keeps the fleet's actual departure point consistent with
        /// wherever ShipStock is physically consumed from — see <c>CreatePending</c>'s
        /// <c>overrideOriginNodeId</c> parameter.
        /// <para>Route-aware: a node whose only stock can't survive the trip to
        /// <paramref name="intent"/>'s target (e.g. an Ion-only hull dispatched toward a planet surface)
        /// is skipped in favour of a farther node whose stock actually can — the same node-vs-stock rule
        /// (see docs/decision-engine.md's "Origin-node-vs-stock" section) applied to the route axis.
        /// Falls back to route-blind role-suitability when the target can't be resolved to a position
        /// (no <c>TargetNodeId</c>/<c>TargetFactionTag</c>) rather than gating against a meaningless
        /// (0,0,0) route.</para></summary>
        public static string FindBestOriginNode(WorldModel world, FactionTuning tuning,
            IPrefabCatalogue prefabCatalogue, string actorTag, FactionIntent intent)
        {
            Vec3 targetPos = new Vec3();
            bool hasTarget = false;
            if (intent.TargetNodeId != null)
            {
                SpatialNode tn;
                if (world.Graph.Nodes.TryGetValue(intent.TargetNodeId, out tn))
                { targetPos = tn.Position; hasTarget = true; }
            }
            else if (intent.TargetFactionTag != null)
            {
                foreach (SpatialNode n in world.Graph.Nodes.Values)
                    if (n.OwnerFactionTag == intent.TargetFactionTag) { targetPos = n.Position; hasTarget = true; break; }
            }

            bool needsShip = ShipPool.RequiresShip(intent.Kind); // false for non-ship actions — don't gate on stock

            string bestId = null;
            double bestDist = double.MaxValue;
            string bestStockedId = null;
            double bestStockedDist = double.MaxValue;

            foreach (SpatialNode n in world.Graph.Nodes.Values)
            {
                if (n.OwnerFactionTag != actorTag) continue;
                double d = n.Position.DistanceTo(targetPos);
                if (d < bestDist) { bestDist = d; bestId = n.Id; }

                if (needsShip && d < bestStockedDist)
                {
                    ShipPool.RouteEnvironment? route = hasTarget
                        ? ShipPool.RouteEnvironment.From(world.Planets, n.Position, targetPos, tuning.MinDryTwr)
                        : (ShipPool.RouteEnvironment?)null;
                    if (ShipPool.HasSuitable(n, intent.Kind, prefabCatalogue, route))
                    {
                        bestStockedDist = d;
                        bestStockedId = n.Id;
                    }
                }
            }

            return bestStockedId ?? bestId;
        }

        /// <summary>Consumes up to <paramref name="desiredCount"/> ships of the role required by
        /// <paramref name="kind"/> from <paramref name="nodeId"/>'s stock. <paramref name="actualCount"/>
        /// is how many were really consumed — capped by available stock (partial fulfillment, not
        /// all-or-nothing) — so a faction with fewer ships than desired still dispatches with what
        /// it has rather than spin-failing forever waiting for a count that may never be reached.
        /// The "don't block" early-outs (no origin, no role) report the full desired count back
        /// since there's no real stock to check in those cases. <paramref name="destNodeId"/> gates
        /// candidates by real dry-TWR at both ends when resolvable (see
        /// <see cref="ShipPool.RouteEnvironment"/>) — null/unresolvable destinations (ground-target-less
        /// actions, or a target not in the graph) skip the gate rather than blocking dispatch.
        /// <para>Returns null when no ship is available (caller should abort). Returns "" when the
        /// origin is unknown or the action does not use ships (don't block).</para></summary>
        public static string CheckAndConsumeShipStock(WorldModel world, FactionTuning tuning,
            IPrefabCatalogue prefabCatalogue, ActionKind kind, string nodeId, string destNodeId,
            int desiredCount, out int actualCount)
        {
            actualCount = desiredCount;
            if (nodeId == null) return ""; // no origin known — don't block
            SpatialNode node;
            if (!world.Graph.Nodes.TryGetValue(nodeId, out node)) return ""; // can't check — don't block
            if (!ShipPool.RequiresShip(kind)) return ""; // not a ship action — don't block

            ShipPool.RouteEnvironment? route = null;
            SpatialNode destNode;
            if (destNodeId != null && world.Graph.Nodes.TryGetValue(destNodeId, out destNode))
                route = ShipPool.RouteEnvironment.From(world.Planets, node.Position, destNode.Position, tuning.MinDryTwr);

            string prefabId;
            int consumed = ShipPool.Consume(node, kind, prefabCatalogue, desiredCount, out prefabId, route);
            if (consumed <= 0) { actualCount = 0; return null; } // no ship available — block dispatch

            actualCount = consumed;
            return prefabId ?? "";
        }

        /// <summary>Post a <c>Ship:{role}</c> demand to the DemandRegister so ShipyardManager can
        /// fulfil it.
        ///
        /// <para>A combat demand carries the faction's power floor
        /// (<see cref="ShipyardManager.CombatPowerFloor"/>), the same figure the proactive poster
        /// attaches. Without it this path — which posts at EVERY origin whose dispatch failed, and so
        /// outnumbers the proactive one — produced floorless combat demands, and a floorless combat
        /// build resolves on the posture blend: at a high posture that takes the strongest hull in the
        /// pool, which is the flagship the floor's own ranking exists to rule out. Measured live
        /// 2026-09-18: SPRT's floor reached N010/N011 only, and a freed slot at N130 re-queued a
        /// 229,659-unit Naginata.</para>
        /// <paramref name="st"/> and <paramref name="tuning"/> null (a caller with neither in hand)
        /// post no floor, exactly as before.</summary>
        public static void PostShipDemand(WorldModel world, string factionTag, ActionKind kind,
            string nodeId, Action<string> log, FactionState st = null, FactionTuning tuning = null)
        {
            string role = ShipRoleFor(kind);
            if (role == null) return;
            double minDps = 0;
            string budget = null;
            if (st != null && tuning != null && IsCombatFleetAction(kind))
                minDps = ShipyardManager.CombatPowerFloor(st, world, tuning, out budget);
            world.Demand.Post(DemandEntry.ForShip(factionTag, role, nodeId, world.UtcNow.Add(ShipDemandTtl),
                postedUtc: world.UtcNow, minDps: minDps));
            if (log != null)
                log("SHIP_DEMAND " + factionTag + " role=" + role + " node=" + nodeId
                    + (minDps > 0 ? " minDps=" + minDps.ToString("0", CultureInfo.InvariantCulture) : "")
                    + (minDps > 0 && budget != null ? " budget=" + budget : ""));
        }

        /// <summary>Owned nodes other than <paramref name="excludeNodeId"/> that hold combat-suitable
        /// stock, ordered nearest the target first (ties by node id) — the nodes a squadron draws from
        /// beside its origin. Mirrors <c>FleetManager.FindShipSource</c>'s nearest-first walk, but
        /// returns the whole ordering in one pass because the assembler draws from all of them as one
        /// pool. The order is the assembler's tie-break between equal hulls.
        ///
        /// <para>Route feasibility is not filtered here but per hull, inside
        /// <see cref="SquadronAssembler.Assemble"/>: a drawn hull must clear both its own leg to the
        /// origin, where it joins the squadron, and the origin's leg to the target, which the
        /// squadron flies as one.</para></summary>
        private static List<SpatialNode> CombatStockOrigins(WorldModel world, IPrefabCatalogue catalogue,
            string factionTag, string excludeNodeId, string targetNodeId)
        {
            var result = new List<SpatialNode>();
            if (world == null) return result;
            SpatialNode target = null;
            if (targetNodeId != null && !world.Graph.Nodes.TryGetValue(targetNodeId, out target)) target = null;

            var scored = new List<KeyValuePair<double, SpatialNode>>();
            foreach (SpatialNode n in world.Graph.OwnedBy(factionTag))
            {
                if (excludeNodeId != null && string.Equals(n.Id, excludeNodeId, StringComparison.Ordinal)) continue;
                if (!ShipPool.HasSuitableForRole(n, "Raid", catalogue)) continue;
                double d = target != null ? n.Position.DistanceTo(target.Position) : 0;
                scored.Add(new KeyValuePair<double, SpatialNode>(d, n));
            }
            scored.Sort((a, b) =>
            {
                int c = a.Key.CompareTo(b.Key);
                return c != 0 ? c : string.CompareOrdinal(a.Value.Id, b.Value.Id);
            });
            for (int i = 0; i < scored.Count; i++) result.Add(scored[i].Value);
            return result;
        }

        /// <summary>
        /// Dispatch <paramref name="intent"/> as a fleet: pick the origin, size the convoy against
        /// <see cref="FactionState.MilitaryPosture"/>, pull the ships out of stock, and add the
        /// resulting <see cref="PendingFleetAction"/> to <paramref name="st"/>'s pending list.
        /// Returns false — leaving <paramref name="fleet"/> at its default — when no eligible ship
        /// stock exists; the caller must then abort without charging cost or cooldown, so the brain
        /// is free to pick something else next tick.
        /// </summary>
        /// <param name="immediate">true when the spawner skips transit — the fleet arrives on the
        /// same tick it departs.</param>
        /// <param name="blockedReports">Throttle for the <c>FLEET_BLOCKED</c> diagnostic; null
        /// reports every occurrence.</param>
        /// <param name="log">Sink for <c>GROUND_OP</c>, <c>FLEET_BLOCKED</c>, <c>SHIP_DEMAND</c> and
        /// <c>FLEET_DISPATCH</c> lines, each without any caller-specific prefix. Null writes none.</param>
        /// <param name="eventWriter">Live dispatch feed; null skips it.</param>
        /// <param name="objectiveId">The <see cref="CampaignFocus.Id"/> this dispatch is made under,
        /// appended to <c>FLEET_DISPATCH</c> as <c>objective=&lt;id&gt;</c>. Null (a dispatch that
        /// belongs to no campaign) leaves the line exactly as it was.</param>
        /// <param name="targetDps">Convoy Dps this dispatch is sized to reach, from the defence
        /// standing at the target (see <see cref="CombatStrength.OfNodeDefence"/>). Zero — every
        /// caller that has no target in view, the sim and every test — keeps the posture-scaled
        /// roster untouched.</param>
        /// <param name="commit">Last word on an assembled fleet, asked once, before the fleet is
        /// pending and before any line is written. Returning false hands the squadron back and
        /// returns false, so nothing downstream sees a fleet that never flew. Null commits every
        /// fleet it assembles.</param>
        public static bool Dispatch(
            FactionState st, FactionIntent intent, WorldModel world, FactionTuning tuning,
            bool immediate, IPrefabCatalogue prefabCatalogue,
            IItemVolumeCatalogue itemVolumes, IItemMassCatalogue itemMasses,
            BlockedFleetReports blockedReports, Action<string> log, ILiveEventWriter eventWriter,
            out PendingFleetAction fleet, string objectiveId = null, double targetDps = 0,
            Func<PendingFleetAction, bool> commit = null)
        {
            fleet = default(PendingFleetAction);
            // Pre-compute origin so the stock check uses the same node CreatePending will choose.
            string originNodeId = FindBestOriginNode(world, tuning, prefabCatalogue, st.Tag, intent);

            // Patrol and Guard on the same planetary surface don't require a ship —
            // ground forces self-deploy when both ends are surface nodes on the same planet.
            bool groundOnly = false;
            if (intent.Kind == ActionKind.Patrol || intent.Kind == ActionKind.Guard)
            {
                SpatialNode originNode, targetNode;
                if (originNodeId != null
                    && world.Graph.Nodes.TryGetValue(originNodeId, out originNode)
                    && intent.TargetNodeId != null
                    && world.Graph.Nodes.TryGetValue(intent.TargetNodeId, out targetNode)
                    && FleetRules.IsSamePlanetSurface(originNode, targetNode, world.Planets))
                {
                    groundOnly = true;
                    if (log != null)
                        log("GROUND_OP " + st.Tag
                            + " " + intent.Kind + " " + originNodeId + " -> " + intent.TargetNodeId);
                }
            }

            // Desired convoy size is computed BEFORE stock consumption so the stock check
            // knows how many ships to actually try to pull, rather than always consuming a
            // flat 1 and treating ConvoySize as a free display multiplier.
            int baseConvoySize = tuning.BurstSizeFor(intent.Kind);
            int desiredConvoySize = baseConvoySize;
            if (baseConvoySize > 0 && (intent.Kind == ActionKind.Raid || intent.Kind == ActionKind.Retaliate
                || intent.Kind == ActionKind.Conquer || intent.Kind == ActionKind.Guard))
            {
                // Deterrence build-up payoff: elevated MilitaryPosture commits more ships per
                // dispatch on both offense (Raid/Retaliate/Conquer) and defense (Guard) — this
                // one scale governs both, so a single factor covers the whole point of building up
                // a fleet. A dispatch aimed at the faction's active Muster focus uses the (usually
                // higher) targeted priority instead of the general posture floor.
                double postureSignal = st.MilitaryPosture;
                if (intent.TargetFactionTag != null && intent.TargetFactionTag == st.MusterFocusTag)
                    postureSignal = Math.Max(postureSignal, st.MusterFocusPriority);
                postureSignal = Math.Max(0.0, Math.Min(1.0, postureSignal));
                double scale = 1.0 + postureSignal * (tuning.MusterConvoyScaleMax - 1.0);
                desiredConvoySize = Math.Max(baseConvoySize, (int)Math.Round(baseConvoySize * scale));
            }

            string shipPrefabId;
            int convoySize;
            long cargoCapacityLiters;
            List<WeaponBand> weaponBands;
            List<string> squadron = null;
            List<string> contributingNodes = null;
            List<string> squadronSources = null;
            bool isSquadronKind = intent.Kind == ActionKind.Raid || intent.Kind == ActionKind.Retaliate
                               || intent.Kind == ActionKind.Conquer;

            if (groundOnly)
            {
                // Ground forces self-deploy — no ship stock consumed, so the desired size
                // is the real size (nothing to cap it by).
                shipPrefabId = "";
                convoySize = desiredConvoySize;
                cargoCapacityLiters = 0;
                weaponBands = new List<WeaponBand>();
            }
            else if (isSquadronKind)
            {
                SpatialNode originNode;
                // No origin known (faction owns no nodes at all) — mirror
                // CheckAndConsumeShipStock's "can't check, don't block" contract rather than
                // blocking a dispatch that never checked stock before either.
                if (originNodeId == null || !world.Graph.Nodes.TryGetValue(originNodeId, out originNode))
                {
                    shipPrefabId = "";
                    convoySize = desiredConvoySize;
                    cargoCapacityLiters = 0;
                    weaponBands = new List<WeaponBand>();
                }
                else
                {
                    int maxFighters = Math.Min(desiredConvoySize, tuning.RaidSquadronMaxFighters);
                    // Posture says how big a convoy this faction likes to send and knows nothing
                    // about what is standing at the target, so it is the floor and targetDps is the
                    // size. Without the raised ceiling the top-up has nowhere to go: four fighters of
                    // a cheap hull is less power than one flagship, which is what made a swarm the
                    // wrong answer before it could be fielded.
                    int squadronMaxHulls = targetDps > 0
                        ? Math.Max(desiredConvoySize, tuning.RaidSquadronMaxHulls)
                        : 0;
                    ShipPool.RouteEnvironment? squadronRoute = null;
                    SpatialNode squadronTargetNode;
                    if (intent.TargetNodeId != null && world.Graph.Nodes.TryGetValue(intent.TargetNodeId, out squadronTargetNode))
                        squadronRoute = ShipPool.RouteEnvironment.From(
                            world.Planets, originNode.Position, squadronTargetNode.Position, tuning.MinDryTwr);
                    // Further owned nodes that hold combat stock, nearest the target first. They and
                    // the origin are one pool: every slot takes the best hull wherever it is parked.
                    // A drawn hull joins THIS squadron at the origin rather than flying as a convoy of
                    // its own -- the duel merges defenders, never attackers, so several raids at one
                    // target would be destroyed one at a time -- and the squadron departs from, and
                    // returns to, the origin. Only gathered when a target is actually being sized
                    // against, so every caller that passes none keeps the single-node draw.
                    List<SpatialNode> reinforcements = targetDps > 0
                        ? CombatStockOrigins(world, prefabCatalogue, st.Tag, originNodeId, intent.TargetNodeId)
                        : null;
                    SpatialNode staging = originNode;
                    Func<SpatialNode, ShipPool.RouteEnvironment?> musterRoute = from =>
                        ShipPool.RouteEnvironment.From(world.Planets, from.Position, staging.Position, tuning.MinDryTwr);
                    // The hold the raid needs to carry off what the target holds -- what the cargo
                    // slot sizes an armed hull's hold against before it falls back to a hauler.
                    double lootLiters = intent.TargetNodeId != null
                        ? world.LootableLiters(intent.TargetNodeId, itemVolumes ?? world.ItemVolumes)
                        : 0;
                    contributingNodes = new List<string>();
                    var assembled = SquadronAssembler.Assemble(originNode, prefabCatalogue, maxFighters, squadronRoute,
                                                               squadronMaxHulls, targetDps,
                                                               reinforcements, contributingNodes,
                                                               musterRoute, lootLiters);
                    squadron = assembled.Squadron;
                    squadronSources = assembled.SourceNodeIds;
                    if (squadron.Count == 0)
                    {
                        ReportBlocked(world, blockedReports, log, intent.Kind, st.Tag, originNodeId,
                            intent.TargetNodeId, intent.TargetFactionTag);
                        return false;
                    }
                    // Missing preferred slots post the same Ship:{role} demand the build loop
                    // already understands — no ShipyardManager changes needed.
                    if (assembled.MissingLargeCombat) PostShipDemand(world, st.Tag, ActionKind.Raid, originNodeId, log, st, tuning);
                    if (assembled.MissingCargo)       PostShipDemand(world, st.Tag, ActionKind.Trade, originNodeId, log);

                    shipPrefabId = squadron[0]; // representative, for dashboards/aggregates
                    convoySize = squadron.Count;
                    cargoCapacityLiters = 0;
                    weaponBands = new List<WeaponBand>();
                    if (prefabCatalogue != null)
                        foreach (string prefabId in squadron)
                        {
                            cargoCapacityLiters += prefabCatalogue.CargoLiters(prefabId);
                            IReadOnlyList<WeaponBand> perShip = prefabCatalogue.WeaponBands(prefabId);
                            for (int i = 0; i < perShip.Count; i++)
                                weaponBands.Add(new WeaponBand { Dps = perShip[i].Dps, RangeM = perShip[i].RangeM });
                        }
                }
            }
            else
            {
                int actualConvoySize;
                shipPrefabId = CheckAndConsumeShipStock(world, tuning, prefabCatalogue, intent.Kind,
                    originNodeId, intent.TargetNodeId, desiredConvoySize, out actualConvoySize);
                if (shipPrefabId == null)
                {
                    // No ship available. ShipyardManager posts Ship: demands proactively, so no
                    // demand posting is needed here. Abort without cost or cooldown so the brain
                    // can pick something else.
                    if (IsCombatFleetAction(intent.Kind))
                        ReportBlocked(world, blockedReports, log, intent.Kind, st.Tag, originNodeId,
                            intent.TargetNodeId, intent.TargetFactionTag);
                    return false;
                }
                // Partial fulfillment: stock may hold fewer ships than desired — dispatch
                // with however many are actually available rather than spin-failing forever
                // waiting for a count that may never be reached.
                convoySize = actualConvoySize;
                cargoCapacityLiters = prefabCatalogue != null && !string.IsNullOrEmpty(shipPrefabId)
                    ? prefabCatalogue.CargoLiters(shipPrefabId) * convoySize
                    : 0;
                weaponBands = ShipPool.ConvoyWeaponBands(prefabCatalogue, shipPrefabId, convoySize);
            }

            fleet = PendingFleetResolver.CreatePending(
                intent.Kind, st.Tag, intent, world, tuning, immediate,
                cargoCapacityLiters, itemVolumes, originNodeId, itemMasses: itemMasses, roster: st.PendingFleets,
                shipPrefabId: shipPrefabId, squadron: squadron, convoySize: convoySize);
            fleet.ShipPrefabId        = shipPrefabId;
            fleet.CargoCapacityLiters = cargoCapacityLiters;
            fleet.TradeMagnitude      = intent.TradeMagnitude;
            fleet.ConvoySize          = convoySize;
            fleet.WeaponBands         = weaponBands;
            fleet.Squadron            = squadron;

            // The caller's last word, before the fleet is pending and before it is announced. A
            // fleet refused here never existed: no pending entry, no event, no FLEET_DISPATCH.
            if (commit != null && !commit(fleet))
            {
                // Each hull goes back to the node it was drawn from: the squadron never departed, so
                // nothing has moved and a refusal must not relocate stock to the origin.
                SpatialNode handback;
                if (squadron != null && squadron.Count > 0
                    && originNodeId != null
                    && world.Graph.Nodes.TryGetValue(originNodeId, out handback))
                    SquadronAssembler.ReturnToSources(world, squadron, squadronSources, handback);
                fleet = default(PendingFleetAction);
                return false;
            }

            FleetMunitions.Arm(ref fleet, world, tuning, log);
            st.PendingFleets.Add(fleet);
            // Every dispatch is filed, combat or not: "fleet events" is what the tab was
            // asked for, and an outbound convoy is one. Volume is handled by the ring's
            // retention rule, not by refusing to record -- but only a combat dispatch is
            // notable, so a faction's own trade traffic cannot crowd out an incoming raid.
            world.RecordEvent(IsCombatFleetAction(fleet.Kind)
                                  ? FactionEventKind.FleetDispatched
                                  : FactionEventKind.ConvoyDispatched,
                              fleet.ActorTag, fleet.TargetTag, fleet.TargetNodeId);
            if (IsCombatFleetAction(fleet.Kind) && log != null)
            {
                log("FLEET_DISPATCH " + (fleet.ActorTag ?? "unknown")
                    + " kind=" + fleet.Kind
                    + " origin=" + (fleet.OriginNodeId ?? "unknown")
                    + " targetNode=" + (fleet.TargetNodeId ?? "unknown")
                    + " targetTag=" + (fleet.TargetTag ?? "unknown")
                    + " fleetId=" + (Convert.ToString(fleet.FleetId) ?? "unknown")
                    + " convoySize=" + convoySize
                    + " etaUtc=" + fleet.EstimatedArrivalUtc.ToUniversalTime().ToString("o")
                    + (targetDps > 0
                        ? " wantDps=" + targetDps.ToString("0", CultureInfo.InvariantCulture)
                          + " gotDps=" + ShipPool.TotalDps(weaponBands).ToString("0", CultureInfo.InvariantCulture)
                        : "")
                    + (contributingNodes != null && contributingNodes.Count > 0
                        ? " drewFrom=" + string.Join("+", contributingNodes.ToArray())
                        : "")
                    + SquadronLogSuffix(squadron)
                    + (objectiveId != null ? " objective=" + objectiveId : ""));
            }
            if (eventWriter != null)
                eventWriter.WriteFleetDispatch(st.Tag, fleet.FleetId, fleet.Kind.ToString(),
                    fleet.OriginNodeId, fleet.TargetNodeId, fleet.TargetTag);
            return true;
        }

        private static void ReportBlocked(WorldModel world, BlockedFleetReports blockedReports,
            Action<string> log, ActionKind kind, string actorTag, string originNodeId,
            string targetNodeId, string targetTag)
        {
            if (log == null) return;
            if (blockedReports != null
                && !blockedReports.ShouldReport(world.UtcNow, kind, actorTag, originNodeId, targetNodeId, targetTag))
                return;
            log("FLEET_BLOCKED " + (actorTag ?? "unknown")
                + " kind=" + kind
                + " origin=" + (originNodeId ?? "unknown")
                + " targetNode=" + (targetNodeId ?? "unknown")
                + " targetTag=" + (targetTag ?? "unknown")
                + " reason=missing_eligible_ship_stock");
        }
    }
}
