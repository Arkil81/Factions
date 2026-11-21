using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Factions.Core
{
    /// <summary>
    /// Owns Reinforce (hold-decay maintenance) and Patrol (perimeter security dispatch) — the two
    /// fleet actions with no <see cref="OperationsManager"/>/<see cref="TradeManager"/> analog.
    ///
    /// <para>Reinforce dispatches a squadron that flies to the node, holds a
    /// <see cref="FleetStopKind.Engage"/> dwell there and delivers
    /// <see cref="FactionTuning.ReinforceStep"/> scaled by its completion ratio when that leg
    /// resolves (<c>FleetResolution</c>). The cost is spent at dispatch and buys the mission, not
    /// the hold: a reinforce can be met in the air, destroyed at the node, or refused outright while
    /// the node is under live attack.</para>
    ///
    /// <para>Used to also dispatch <see cref="ActionKind.Ferry"/> convoys off a <c>Position:role</c>
    /// demand posted when some other manager's dispatch found no local ship. Both callers of that
    /// pattern (<see cref="OperationsManager.DispatchHaul"/>, <see cref="TradeManager.DispatchTrade"/>)
    /// now dispatch a single convoy with a <see cref="FleetStopKind.Pickup"/> leg directly instead
    /// (see <c>docs/fleet-waypoints-plan.md</c>), so nothing posts a <c>Position:</c> demand anymore
    /// and this manager no longer consumes them. <see cref="ActionKind.Ferry"/> itself, and
    /// <see cref="FleetResolution"/>'s handling of it, are kept as a primitive for a possible future
    /// "ship carries another ship" mechanic (see <c>docs/todo.md</c>'s stranded-ship-stock entry) —
    /// only the demand-driven dispatch trigger here was removed.</para>
    /// </summary>
    public sealed class FleetManager : IFactionManager
    {
        // Log-on-change dedup for the hold-decay skip summary: the same faction can stay
        // treasury-exhausted for a long stretch of ticks, and re-logging an unchanged summary
        // every tick (this manager ticks roughly once a second) drowns the log. Keyed by tag;
        // in-memory only, never persisted — a restart just re-logs once on the next skip.
        private readonly Dictionary<string, (int noNeed, int noWorth, int noMoney, int noBudget, int paidNoHull, int inFlight, int atCap, int underAttack, int paidGrace, long treasury)> _lastReinforceSkipSummary
            = new Dictionary<string, (int noNeed, int noWorth, int noMoney, int noBudget, int paidNoHull, int inFlight, int atCap, int underAttack, int paidGrace, long treasury)>();

        // Derived from the posting cadence, not fixed: a ship demand has to outlive the gap to the
        // next sweep or the register is empty when the consumer looks. The x2 floor means one missed
        // sweep does not drop it.
        private static TimeSpan ShipDemandTtlFor(FactionTuning t)
        {
            return TimeSpan.FromSeconds(Math.Max(30.0, FleetIntervalOf(t) * 2.0));
        }

        private static double FleetIntervalOf(FactionTuning t)
        {
            if (t == null) return 1.0;
            return t.FleetIntervalSeconds > 0 ? t.FleetIntervalSeconds : 1.0;
        }

        private readonly SpendBudget _reinforceBudget = new SpendBudget();
        // (tag|node) -> when the last REINFORCE_PAID_BLOCKED line for it was written. In-memory only.
        private readonly Dictionary<string, DateTime> _paidReinforceBlockedLogged =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly SpendBudget _patrolBudget = new SpendBudget();
        private readonly SpendBudget _guardBudget = new SpendBudget();
        private readonly SpendBudget _offensiveBudget = new SpendBudget();

        // Diagnostic throttle for the offensive block's stock-gated dispatches, the same one the
        // brain's dispatch path used to hold. In-memory only; a restart just re-reports once.
        private readonly OffensiveDispatch.BlockedFleetReports _offensiveBlockedReports =
            new OffensiveDispatch.BlockedFleetReports();

        public void Tick(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            // Built once for the whole tick, not once per faction: the defend block below asks
            // "is a raid inbound at this node" for every owned node of every faction, and asking it
            // per node is O(nodes x fleets) on the update tick.
            Dictionary<string, PendingFleetAction> inboundThreats = BuildInboundThreatIndex(states, world);
            // The other half of the same question, and for the same reason: what defence each node
            // already has committed to it. Bands rather than a bare set, because "already covered"
            // is a question about strength -- a garrison that merely survives its attacker is topped
            // up, one that kills it is left alone. Built once and added to as the loop below
            // dispatches, so two factions reached in one pass cannot both pay to cover one node.
            Dictionary<string, List<WeaponBand>> guardedStrength = BuildGuardedStrengthIndex(states);
            // The other half of the same question, for the picket rather than the garrison: which
            // hostiles are holding still somewhere, and therefore catchable at all.
            Dictionary<string, PendingFleetAction> dwellingHostiles = BuildDwellingHostileIndex(states);

            foreach (FactionState st in states)
            {
                if (FactionLifecycle.IsDormant(st, world.UtcNow)) continue;

                // Upkeep is not strategy. A player faction keeps the brain switched off — Defend,
                // Patrol and Campaign below all stay theirs to decide — but it may hold the ground
                // it took, which is what the reinforce block underneath does. Its ranked list comes
                // from the player's own persisted opt-in rather than from a brain that never ran.
                bool brainDriven = !FactionLifecycle.IsAutonomyDisabled(st);
                FactionLifecycle.ProjectPlayerReinforcePriority(st, world);
                // The other half of the same seam: a player faction's own hand-placed campaign
                // objectives get the ranked target list the brain would have written, so the
                // offensive block below reads one list whoever filled it.
                FactionLifecycle.ProjectPlayerCampaignCandidates(st, world, tuning);

                // --- Hold-decay maintenance (always-on, independent of Campaign focus) ----------
                // FleetManager owns hold-preservation: when any owned node is critically low,
                // dispatch unconditionally so territory survives between campaign cycles.
                {
                    if (st.ReinforcePriority.Count > 0)
                    {
                        long treasury = world.GetTreasury(st.Tag);
                        // Resolved per faction: a player can set how much of their own money upkeep
                        // may take, and an unset knob reads the world's tuning.
                        double reinforceIncomeFraction = st.ReinforceIncomeFraction(tuning);
                        long reinforceFloor = st.ReinforceBudgetFloorFor(tuning);
                        double reinforceWealthFraction = st.ReinforceWealthFraction(tuning);
                        long reinforceSurplus = reinforceWealthFraction > 0
                            ? Economy.SpendableTreasurySurplus(treasury, world.GetNetFlow(st.Tag), tuning)
                            : 0;
                        int skippedNoNeed = 0;
                        int skippedNoWorth = 0;
                        int skippedNoMoney = 0;
                        int skippedNoBudget = 0;
                        int paidNoHull = 0;
                        int skippedInFlight = 0;
                        int skippedAtCap = 0;
                        int skippedUnderAttack = 0;
                        int paidGrace = 0;

                        // What this faction already has committed to reinforcing. Counted once per
                        // faction rather than per candidate: the ranking below can be the whole
                        // estate, and asking the fleet list per entry is O(nodes x fleets) on a
                        // manager that ticks about once a second.
                        // One build order per faction per pass. A faction with no warships left fails
                        // this gate at EVERY node on the ranking, and a demand row per node would put
                        // the whole estate in the register to say one thing: it needs warships. The
                        // top-ranked node is the one that should get the hull anyway.
                        bool postedShipDemand = false;
                        int inFlightReinforces = 0;
                        var reinforceTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int rfi = 0; rfi < st.PendingFleets.Count; rfi++)
                        {
                            PendingFleetAction rf = st.PendingFleets[rfi];
                            if (rf.Kind != ActionKind.Reinforce || rf.Resolved || rf.Intercepted) continue;
                            inFlightReinforces++;
                            // Only an outbound one still owns its target. One on its way home has
                            // already delivered, and its Return leg names the origin node -- reading
                            // that as a claim would lock the depot out of being reinforced itself.
                            if (rf.CurrentStopKind == FleetStopKind.Return) continue;
                            string claimed = PendingFleetResolver.SemanticTargetOf(rf);
                            if (claimed != null) reinforceTargets.Add(claimed);
                        }

                        foreach (ReinforcePriorityEntry priority in st.ReinforcePriority)
                        {
                            SpatialNode decayNode;
                            if (!world.Graph.Nodes.TryGetValue(priority.NodeId, out decayNode))
                                continue;
                            if (decayNode.OwnerFactionTag != st.Tag)
                                continue;
                            // A reinforce costs a flat ReinforceCost and buys the deficit, not the
                            // step -- so a node at 0.99 paid 50,000 for 0.01. The gate asks whether
                            // the purchase is worth making rather than whether any deficit exists;
                            // budget freed here is not saved, it is spent further down the same
                            // ranking on nodes that do need it.
                            if (!tuning.ReinforceBuysEnough(decayNode.HoldStrength))
                            {
                                skippedNoNeed++;
                                continue;
                            }
                            // Nothing is bought by holding ground that mints nothing. Hold is not the
                            // point of a node, production is, and this one has none to protect: the
                            // spend would buy decay resistance on a source whose every yield channel
                            // is a hard zero. Left to decay, an engine node hands itself to UNKN and
                            // the grace sweep retires it -- so the estate stops growing by the one
                            // route nothing else bounds. Deliberately ahead of the treasury and budget
                            // gates: a faction rich enough to afford it is exactly the one that
                            // otherwise pays this forever.
                            // A player holds ground for reasons the mint model cannot see — a
                            // forward base, a chokepoint, or simply because they took it. Pruning
                            // sterile ground is right for an NPC estate that grows on its own; a
                            // player's estate is exactly what they chose, so this gate is theirs to
                            // apply, not ours.
                            if (brainDriven && Economy.MintsNothing(decayNode))
                            {
                                skippedNoWorth++;
                                continue;
                            }
                            if (treasury < tuning.ReinforceCost)
                            {
                                skippedNoMoney++;
                                continue;
                            }
                            if (!_reinforceBudget.CanSpend(st.Tag, world.UtcNow, st.CentralBankRecentIncome, tuning.ReinforceCost,
                                                            reinforceIncomeFraction, reinforceFloor,
                                                            tuning.SpendBudgetWindowSeconds,
                                                            reinforceSurplus, reinforceWealthFraction))
                            {
                                skippedNoBudget++;
                                continue;
                            }

                            // A node a siege left at 0 is in its grace (WorldModel.InHoldZeroGrace):
                            // the next decay pass past it abandons the node, and no mission lands
                            // in time -- transit plus a dwell. So it is bought outright, ahead of
                            // the hull, in-flight and cooldown gates; a mission that died on the way
                            // in has taken the cooldown and would otherwise block the payment.
                            // Still refused while the attack stands, as every paid reinforce is.
                            if (decayNode.HoldStrength <= 0 && decayNode.HoldZeroSinceUtc != default(DateTime))
                            {
                                if (TryPayReinforceWithoutAHull(states, st, world, tuning, decayNode, "hold_grace", log))
                                {
                                    paidGrace++;
                                    treasury -= tuning.ReinforceCost;
                                }
                                else skippedUnderAttack++;
                                continue;
                            }

                            // Past the money gates, so what is left is whether a mission can be
                            // flown at all. A reinforce buys a squadron, a transit and a dwell at the
                            // node -- not an outcome: it can be met in the air, fought at the node,
                            // and refused outright while the node is under attack.
                            if (inFlightReinforces >= tuning.ReinforceMaxConcurrent)
                            {
                                // Per faction, not per node: nothing further down this ranking can
                                // dispatch either, so the walk ends here.
                                skippedAtCap++;
                                break;
                            }
                            if (OnCooldown(st.ReinforceNodeCooldowns, decayNode.Id, world.UtcNow)
                                || reinforceTargets.Contains(decayNode.Id))
                            {
                                skippedInFlight++;
                                continue;
                            }

                            // The origin is never the destination -- the same trap the Patrol block
                            // below documents: a node passed as its own source wins at distance 0 and
                            // the hull flies nowhere.
                            SpatialNode reinforceOrigin = FindShipSource(world, tuning, st.Tag, decayNode.Id, decayNode.Id, "Patrol");
                            if (reinforceOrigin == null)
                            {
                                // Nothing in the estate holds a hull for this. The node asks for the
                                // one that would have answered, or the shortage is silent and never
                                // gets built out of.
                                if (!postedShipDemand)
                                {
                                    string holdBudget;
                                    double holdFloor = CombatFloorFor(st, world, tuning, out holdBudget);
                                    PostShipDemand(tuning, st.Tag, "Patrol", decayNode.Id, decayNode.Id, world, log,
                                                   holdFloor, holdBudget);
                                    postedShipDemand = true;
                                }
                                if (TryPayReinforceWithoutAHull(states, st, world, tuning, decayNode, "no_hull_anywhere", log))
                                {
                                    paidNoHull++;
                                    treasury -= tuning.ReinforceCost;
                                }
                                else skippedUnderAttack++;
                                continue;
                            }
                            // Unreachable today: FindShipSource only ever returns a role-suitable node
                            // or null, so this is the second reading of one rule and not a second
                            // rule. Kept for the day the source lookup widens, and paired with the
                            // same fallback so a widening cannot silently reintroduce the decay.
                            if (!ShipPool.HasSuitableForRole(reinforceOrigin, "Patrol", world.PrefabCatalogue))
                            {
                                if (TryPayReinforceWithoutAHull(states, st, world, tuning, decayNode, "no_role_hull", log))
                                {
                                    paidNoHull++;
                                    treasury -= tuning.ReinforceCost;
                                }
                                else skippedUnderAttack++;
                                continue;
                            }

                            // Posture-scaled the way the Guard and Patrol blocks scale theirs, minus
                            // the MusterFocusTag bonus -- that applies only to actions aimed at a
                            // rival faction, and this one is aimed at the faction's own ground.
                            int reinforceBaseSize = tuning.BurstSizeFor(ActionKind.Reinforce);
                            double reinforcePosture = Math.Max(0.0, Math.Min(1.0, st.MilitaryPosture));
                            double reinforceScale = 1.0 + reinforcePosture * (tuning.MusterConvoyScaleMax - 1.0);
                            int reinforceDesired = Math.Max(reinforceBaseSize, (int)Math.Round(reinforceBaseSize * reinforceScale));

                            ShipPool.RouteEnvironment reinforceRoute = ShipPool.RouteEnvironment.From(
                                world.Planets, reinforceOrigin.Position, decayNode.Position, tuning.MinDryTwr);
                            IPrefabCatalogue reinforceCat = world.PrefabCatalogue;
                            List<string> reinforceSquadron = SquadronAssembler.AssembleDefence(
                                reinforceOrigin, reinforceCat,
                                id => ShipPool.IsSuitableForRole("Patrol", reinforceCat, id),
                                reinforceRoute, reinforceDesired, reinforceDesired, 0);
                            int reinforceCount = reinforceSquadron.Count;
                            if (reinforceCount <= 0)
                            {
                                // Tag-suitable stock existed; nothing survives the route. Must abort
                                // here rather than fall through: a zero-ship fleet later "returns" a
                                // hull that was never consumed.
                                if (!postedShipDemand)
                                {
                                    ShipPool.MaybePostLocalShipyardInvestment(world, st, reinforceOrigin, decayNode, log, "reinforce");
                                    string routeBudget;
                                    double routeFloor = CombatFloorFor(st, world, tuning, out routeBudget);
                                    PostShipDemand(tuning, st.Tag, "Patrol", reinforceOrigin.Id, decayNode.Id, world, log,
                                                   routeFloor, routeBudget);
                                    postedShipDemand = true;
                                }
                                if (TryPayReinforceWithoutAHull(states, st, world, tuning, decayNode, "no_route_hull", log))
                                {
                                    paidNoHull++;
                                    treasury -= tuning.ReinforceCost;
                                }
                                else skippedUnderAttack++;
                                continue;
                            }

                            double holdBefore = decayNode.HoldStrength;
                            world.Spend(st.Tag, tuning.ReinforceCost);
                            _reinforceBudget.Record(st.Tag, world.UtcNow, tuning.ReinforceCost, tuning.SpendBudgetWindowSeconds);
                            treasury -= tuning.ReinforceCost;
                            st.ReinforceNodeCooldowns[decayNode.Id] = world.UtcNow + tuning.ReinforceNodeCooldown;

                            DateTime reinforceDeparted = world.UtcNow;
                            double reinforceDistKm = reinforceOrigin.Position.DistanceTo(decayNode.Position);
                            DateTime reinforceArrival = tuning.FleetSpeedKmPerMin > 0
                                ? reinforceDeparted.AddMinutes(tuning.TransitTimeMins(reinforceDistKm))
                                : reinforceDeparted;

                            // Fly there, hold station, go home. The dwell IS the mission: the hold
                            // gain lands when that leg resolves, so the whole window is contestable.
                            var reinforceStops = new List<FleetStop>
                            {
                                new FleetStop
                                {
                                    NodeId = decayNode.Id,
                                    Kind = FleetStopKind.Engage,
                                    DurationSeconds = tuning.ReinforceDwellSeconds,
                                },
                                new FleetStop { NodeId = reinforceOrigin.Id, Kind = FleetStopKind.Return },
                            };

                            var reinforceFleet = new PendingFleetAction
                            {
                                Kind = ActionKind.Reinforce,
                                ShipPrefabId = reinforceSquadron[0],
                                ConvoySize = reinforceCount,
                                Squadron = reinforceCount > 1 ? reinforceSquadron : null,
                                ActorTag = st.Tag,
                                OriginNodeId = reinforceOrigin.Id,
                                TargetNodeId = decayNode.Id,
                                OriginPosKm = reinforceOrigin.Position,
                                TargetPosKm = decayNode.Position,
                                DepartedUtc = reinforceDeparted,
                                EstimatedArrivalUtc = reinforceArrival,
                                FleetId = PendingFleetAction.MintId(st.PendingFleets, st.Tag, reinforceDeparted, decayNode.Id),
                                CurrentStopKind = FleetStopKind.Service,
                                RemainingStops = reinforceStops,
                                // A reinforce standing its dwell duels whatever attacks the node, and
                                // an empty band list is scored as zero DPS -- so one dispatched
                                // without its weapons on record would stand there as a paper defender.
                                WeaponBands = SquadronAssembler.WeaponBandsOf(reinforceCat, reinforceSquadron),
                                LastResolvedSegment = -1,
                            };
                            reinforceFleet = PendingFleetResolver.PlanFleetLeg(reinforceFleet, world, tuning, false, null, null, log);
                            FleetMunitions.Arm(ref reinforceFleet, world, tuning, log);
                            st.PendingFleets.Add(reinforceFleet);
                            inFlightReinforces++;
                            reinforceTargets.Add(decayNode.Id);

                            log.Add(st.Tag + " REINFORCE_DISPATCH cost=" + tuning.ReinforceCost
                                    + " count=" + reinforceCount
                                    + " " + reinforceOrigin.Id + " -> " + decayNode.Id
                                    + " hold=" + holdBefore.ToString("0.00", CultureInfo.InvariantCulture)
                                    + " squadron=" + SquadronAssembler.Describe(reinforceSquadron)
                                    + (st.CampaignFocusRegion != null ? " campaign=" + st.CampaignFocusRegion : ""));
                        }

                        // Only treasury/budget-blocked skips are noteworthy (a genuine stall);
                        // "no need" nodes at full hold are the routine healthy state, not logged on their own.
                        // no_worth joins them because it is how the estate is seen to shrink: the count
                        // falls as sterile nodes decay out, and a jump in it is the only warning that
                        // worth was re-derived wrongly across the estate rather than node by node.
                        // paid_no_hull and at_cap join them: at_cap is a stall a reinforce has that it
                        // could not have before, and paid_no_hull is how much of a faction's upkeep
                        // is being bought without a fleet -- the per-node REINFORCE_PAID lines carry
                        // the reason, this carries the share.
                        // under_attack is the fallback refused at a node a raid or a player is taking
                        // right now (TryPayReinforceWithoutAHull).
                        // in_flight stays unlogged on its own, like no_need: a node whose reinforce
                        // is already in the air is the healthy state, not a stall.
                        if (skippedNoWorth > 0 || skippedNoMoney > 0 || skippedNoBudget > 0
                            || paidNoHull > 0 || skippedAtCap > 0 || skippedUnderAttack > 0 || paidGrace > 0)
                        {
                            var summary = (skippedNoNeed, skippedNoWorth, skippedNoMoney, skippedNoBudget,
                                           paidNoHull, skippedInFlight, skippedAtCap, skippedUnderAttack, paidGrace, treasury);
                            (int noNeed, int noWorth, int noMoney, int noBudget, int paidNoHull, int inFlight, int atCap, int underAttack, int paidGrace, long treasury) last;
                            bool unchanged = _lastReinforceSkipSummary.TryGetValue(st.Tag, out last) && last.Equals(summary);
                            if (!unchanged)
                            {
                                log.Add(st.Tag + " REINFORCE_SKIPPED no_need=" + skippedNoNeed
                                        + " no_worth=" + skippedNoWorth
                                        + " no_money=" + skippedNoMoney + " no_budget=" + skippedNoBudget
                                        + " paid_no_hull=" + paidNoHull + " in_flight=" + skippedInFlight
                                        + " at_cap=" + skippedAtCap
                                        + " under_attack=" + skippedUnderAttack
                                        + " paid_grace=" + paidGrace
                                        + " treasury=" + treasury);
                                _lastReinforceSkipSummary[st.Tag] = summary;
                            }
                        }
                        else
                        {
                            _lastReinforceSkipSummary.Remove(st.Tag);
                        }
                    }
                }

                // --- Defend: a reactive Guard against a detected inbound raid ------------------
                // Guard is the only kind ResolveArrivals and ResolveMidTransitMeetings will accept
                // as a defender against a Raid/Retaliate/Conquer. Patrol is not a substitute -- it
                // lowers Threat at a node it reaches but is never selected as a defender by either
                // meeting path.
                // A player siege produces no PendingFleetAction, so the fleet index alone can
                // never report one. AnyPlayerAttackActive is the O(1) half of the same question.
                // The candidate set is this faction's own estate first and, only when none of it
                // needs a defender this tick, the estate of every faction it holds a pact with. Own
                // nodes strictly first, so a partner is covered by a hull that would otherwise sit
                // idle and never at the cost of leaving an own flashpoint open.
                // Runs for a player faction too. What the autonomy gate keeps theirs is strategy --
                // who to attack, where to expand, what to build; meeting a raid on ground they
                // already hold is upkeep, like the reinforce above, and the hulls they park at a
                // node were never going to answer it any other way.
                // A station calling for protection through its antenna (StationCall) is the third
                // way in: the call names the node, and this block answers it like an attack.
                // A detected enemy ship trespassing at a node (TrespassPolicy.IsIntruder) is the
                // fourth, and the least urgent of the four: nothing has been hit yet.
                if (inboundThreats.Count > 0 || world.AnyPlayerAttackActive() || world.AnyStationCall
                    || world.AnyIntruder)
                {
                    var pick = new DefencePick();

                    foreach (SpatialNode owned in world.Graph.OwnedBy(st.Tag))
                        ConsiderDefence(pick, owned, st, false, inboundThreats, guardedStrength, states, world, tuning);

                    if (pick.Node == null)
                        for (int pi = 0; pi < states.Count; pi++)
                        {
                            FactionState partner = states[pi];
                            if (partner.Tag == st.Tag) continue;
                            if (!Diplomacy.InPactWith(st, partner.Tag)) continue;
                            foreach (SpatialNode owned in world.Graph.OwnedBy(partner.Tag))
                                ConsiderDefence(pick, owned, st, true, inboundThreats, guardedStrength, states, world, tuning);
                        }

                    if (pick.Node != null)
                    {
                        // Pull an already-flying Guard over before paying for a fresh one. Its
                        // flashpoint has either gone quiet or been outranked, and a hull already in
                        // the air is both free and usually closer than a fresh dispatch would be.
                        List<WeaponBand> movedBands;
                        if (TryRedirectGuard(st, pick.Node, pick.Threat, inboundThreats, world, tuning, log, out movedBands))
                            AddGuardedStrength(guardedStrength, pick.Node.Id, movedBands);
                        else
                            MarshalDefence(st, pick.Node, pick.Threat, guardedStrength, states, world, tuning, log);
                    }
                }

                // --- Campaign: brain-set focus is a UTILITY signal, not a permission gate. Patrol
                // fires on raw threat alone whether or not a campaign is active; an active campaign
                // just biases which node wins the pick by adding the brain's own perceived-threat
                // bonus (xenophobia/foreign-presence) to nodes inside the focus region — the same
                // signal that made the brain flag that region in the first place, now also nudging
                // the operational dispatch instead of being brain-only.
                // Each defensive focus has its own persisted expiry. The compatibility region is a
                // deduplicated union, so overlapping focus regions never compound dispatch score.
                Func<string, bool> ownsCenter = nodeId =>
                {
                    SpatialNode center;
                    return world.Graph.Nodes.TryGetValue(nodeId, out center) && center.OwnerFactionTag == st.Tag;
                };
                // Every offensive focus is scored before the generic expiry can drop it unread: the
                // close decides the next campaign's budget and target, and a conquest that has taken
                // its ground ends there rather than running out its clock.
                // Runs for a player faction too: its directives expire, close on the ground being
                // taken, and close on the staging node being lost by exactly the same rules. The
                // antagonist's escalation branch inside it cannot fire for one -- that branch keys on
                // CampaignCause.Terror and a directive carries CampaignCause.Directive.
                OffensiveCampaigns.Maintain(st, tuning, world.UtcNow, ownsCenter, world, world.PrefabCatalogue, log);
                // The generic expiry stays brain-only: it also migrates the legacy scalar focus and
                // drops a focus unscored, neither of which a player faction's directive wants.
                bool hasActiveCampaign = st.CampaignFoci.Count > 0;
                if (brainDriven)
                {
                    int expiredFocusCount = CampaignFocusLifecycle.Maintain(st, world.UtcNow, ownsCenter, tuning.CampaignCooldown);
                    if (expiredFocusCount > 0)
                        log.Add(st.Tag + " CAMPAIGN_EXPIRED count=" + expiredFocusCount);
                    hasActiveCampaign = st.CampaignFoci.Count > 0;
                }
                else
                {
                    // A player faction's hand-placed defensive focus has its own close: the
                    // brain's Maintain never runs here, and the offensive close above only reads
                    // offensive objectives, so without this the focus would patrol for ever.
                    CampaignFocusLifecycle.MaintainPlayerIssued(st, world.UtcNow, ownsCenter, log);
                    hasActiveCampaign = st.CampaignFoci.Count > 0;
                }

                // --- Offensive campaigns: raid and conquest dispatch under a standing objective ---
                // The brain nominated the region and wrote the ranked node list; this decides when,
                // which of them, and with what. It reads CampaignObjective.Candidates and looks each
                // one up — it never walks the graph for a target, which is the whole reason that list
                // is a brain-side cache (docs/campaigns.md -> Who decides what).
                {
                    List<OffensiveCampaigns.Entry> offensive = OffensiveCampaigns.Active(st);
                    if (offensive.Count > 0)
                    {
                        for (int oi = 0; oi < offensive.Count; oi++)
                        {
                            CampaignFocus focus = offensive[oi].Focus;
                            CampaignObjective objective = offensive[oi].Objective;
                            // A player faction fights only what its player placed. The brain that
                            // would have nominated anything else never ran for it, so a focus of
                            // any other provenance on such a faction is a leftover, not an order.
                            if (!brainDriven && !focus.PlayerIssued) continue;
                            if (objective.Candidates.Count == 0) continue;
                            // Tempo: how often ONE campaign may strike. Per objective, so a faction
                            // running two of them fights on two clocks.
                            if (objective.LastDispatchUtc != DateTime.MinValue
                                && world.UtcNow - objective.LastDispatchUtc < tuning.OffensiveTempo) continue;

                            // The objective's WantedCombatDps is the yard's build target, read by
                            // ShipyardManager.PostProactiveShipDemands; it is not a gate here. The
                            // manager fights with the squadron it can field now and the yard closes
                            // the gap behind it -- a census-sized budget (a whole region's defenders)
                            // is routinely far above anything one node can assemble.

                            SpatialNode target = PickCampaignTarget(st, objective, world);
                            if (target == null) continue;

                            // Soften, then finish: a conquest sends Raid while its best candidate is
                            // still held above the ceiling, and Conquer once one is at or under it.
                            ActionKind kind;
                            if (objective.Kind == CampaignObjectiveKind.SeizeRegion)
                                kind = target.HoldStrength <= tuning.ConquerHoldCeiling
                                    ? ActionKind.Conquer : ActionKind.Raid;
                            else
                                kind = objective.Cause == CampaignCause.Retaliation
                                    ? ActionKind.Retaliate : ActionKind.Raid;

                            // Sized against what is actually standing there, in the currency the
                            // duel settles in. The objective's WantedCombatDps is a whole region's
                            // census and is the YARD's target; asking one squadron for it would
                            // empty the estate at the first node. This asks the node.
                            // A Guard already committed to the target counts: dispatch takes its
                            // hulls out of ShipStock, so the parked census alone reads a garrisoned
                            // node as the empty one it was before the garrison sailed -- and that
                            // garrison is precisely what the raid meets. Same index the defensive
                            // block reads, so both sides of a fight size against one census.
                            List<WeaponBand> targetStanding;
                            guardedStrength.TryGetValue(target.Id, out targetStanding);
                            double offensiveDefenceDps =
                                CombatStrength.OfNodeDefence(target, world.PrefabCatalogue, tuning, targetStanding);
                            // The same census as bands, so the commit predicate below can ask the
                            // duel's bracketed question instead of only the DPS-total one.
                            List<WeaponBand> offensiveDefenceBands =
                                CombatStrength.NodeDefenceBands(target, world.PrefabCatalogue, tuning, targetStanding);
                            // Plus the Guards the owner can still sail onto it once the raid is
                            // seen. Without them a node with nothing parked or standing reads as
                            // free, the gate below admits on defenceDps=0, and the raid flies into
                            // the reinforcement MarshalDefence sends a minute later. Read with the
                            // marshal's own walk, so both sides price one response.
                            List<string> responseHulls = ProjectedGuardResponse(world, tuning, target, null);
                            if (responseHulls.Count > 0)
                            {
                                List<WeaponBand> responseBands = new List<WeaponBand>();
                                for (int ri = 0; ri < responseHulls.Count; ri++)
                                    responseBands.AddRange(ShipPool.ConvoyWeaponBands(world.PrefabCatalogue, responseHulls[ri], 1));
                                offensiveDefenceBands.AddRange(responseBands);
                                offensiveDefenceDps += ShipPool.TotalDps(responseBands);
                            }
                            double responseDps = offensiveDefenceDps
                                - CombatStrength.OfNodeDefence(target, world.PrefabCatalogue, tuning, targetStanding);
                            double offensiveTarget = 0;
                            if (tuning.RaidOverwhelmMargin > 0 && offensiveDefenceDps > 0)
                                offensiveTarget = CombatStrength.DpsForDamage(
                                    CombatStrength.DamageToDestroy(offensiveDefenceDps, tuning.RaidOverwhelmMargin), tuning);

                            long cost = kind == ActionKind.Conquer ? tuning.ConquerCost : tuning.RaidCost;
                            long offensiveTreasury = world.GetTreasury(st.Tag);
                            if (offensiveTreasury < cost) continue;
                            long offensiveSurplus = tuning.RaidWealthFractionPerWindow > 0
                                ? Economy.SpendableTreasurySurplus(offensiveTreasury, world.GetNetFlow(st.Tag), tuning)
                                : 0;
                            if (!_offensiveBudget.CanSpend(st.Tag, world.UtcNow, st.CentralBankRecentIncome, cost,
                                                           tuning.RaidMaxIncomeFractionPerWindow, tuning.RaidBudgetFloor,
                                                           tuning.SpendBudgetWindowSeconds,
                                                           offensiveSurplus, tuning.RaidWealthFractionPerWindow))
                                continue;

                            var offensiveIntent = new FactionIntent
                            {
                                Kind = kind,
                                TargetNodeId = target.Id,
                                TargetFactionTag = target.OwnerFactionTag,
                                Cost = cost,
                            };
                            // Can what we can actually assemble survive what is already standing
                            // there? Asked as the commit predicate, so a refusal happens before the
                            // fleet is pending and before FLEET_DISPATCH is written -- a line for a
                            // fleet that never flew reads as a sortie in every later count.
                            // `MarshalDefence` has answered the mirror of this question since
                            // FAC-256: a defence that cannot hold is not sent at all.
                            bool refusedByCommit = false;
                            double refusedHaveDps = 0;
                            string refusedOrigin = null;
                            CombatStrength.DuelBracket refusedBracket = new CombatStrength.DuelBracket();
                            DuelMatchup refusedMatchup = null;
                            PendingFleetAction offensiveFleet;
                            if (!OffensiveDispatch.Dispatch(st, offensiveIntent, world, tuning,
                                    immediate: false, prefabCatalogue: world.PrefabCatalogue,
                                    itemVolumes: null, itemMasses: null,
                                    blockedReports: _offensiveBlockedReports,
                                    log: line => log.Add(line), eventWriter: null,
                                    fleet: out offensiveFleet, objectiveId: focus.Id,
                                    targetDps: offensiveTarget,
                                    commit: candidate =>
                                    {
                                        if (tuning.RaidCommitRatio <= 0 || offensiveDefenceDps <= 0) return true;
                                        double haveDps = ShipPool.TotalDps(candidate.WeaponBands);
                                        // One question, asked in the arithmetic the duel resolves in:
                                        // does the defence's own bracketed output kill this squadron
                                        // before the turns run out. A defence whose guns miss the long
                                        // brackets cannot stop a raid the DPS totals say is outmatched,
                                        // and one that reaches every turn kills a raid the totals call
                                        // comfortable -- so the totals decide neither case, and a
                                        // ratio admitting on them admits every fight between itself
                                        // and the real bar.
                                        DuelMatchup raidMatchup = RaidMatchup(world, states, target, candidate, st, tuning, responseHulls);
                                        if (CombatStrength.Survives(offensiveDefenceBands, target, tuning,
                                                                    haveDps, tuning.RaidSurvivalMargin, raidMatchup))
                                            return true;
                                        refusedMatchup = raidMatchup;
                                        refusedByCommit = true;
                                        refusedHaveDps = haveDps;
                                        refusedOrigin = candidate.OriginNodeId;
                                        // Taken here, where the squadron's own bands are still in
                                        // hand: the refusal path never builds a fleet, so this is
                                        // the last point at which what it would have flown exists.
                                        refusedBracket = CombatStrength.WorstBracketAt(
                                            candidate.WeaponBands, offensiveDefenceBands, target, tuning,
                                            raidMatchup);
                                        return false;
                                    }))
                            {
                                if (refusedByCommit)
                                {
                                    // The tempo IS stamped, unlike the no-stock abort. That one
                                    // retries freely because nothing was decided; this one decided,
                                    // and re-deciding every sweep against an unchanged target would
                                    // re-draw and re-return the same hulls forever. No cost either way.
                                    objective.LastDispatchUtc = world.UtcNow;
                                    objective.LastOutcome = CampaignOutcome.Refused;
                                    objective.LastOutcomeUtc = world.UtcNow;
                                    objective.LastOutcomeRangeM = refusedBracket.RangeM;
                                    objective.LastOutcomeOwnDps = refusedBracket.AttackerDps;
                                    objective.LastOutcomeOpposedDps = refusedBracket.DefenderDps;
                                    objective.LastOutcomeHullsLost = 0;
                                    objective.LastOutcomeHullsTotal = 0;
                                    log.Add(st.Tag + " RAID_REFUSED kind=" + kind
                                            + " origin=" + (refusedOrigin ?? "unknown")
                                            + " targetNode=" + target.Id
                                            + " targetTag=" + (target.OwnerFactionTag ?? "unknown")
                                            + " haveDps=" + refusedHaveDps.ToString("0", CultureInfo.InvariantCulture)
                                            + " defenceDps=" + offensiveDefenceDps.ToString("0", CultureInfo.InvariantCulture)
                                            + " responseDps=" + responseDps.ToString("0", CultureInfo.InvariantCulture)
                                            + " duelDmg=" + CombatStrength.DuelDamage(offensiveDefenceBands, target, tuning,
                                                                refusedMatchup).ToString("0", CultureInfo.InvariantCulture)
                                            + " surviveDmg=" + CombatStrength.DamageToDestroy(refusedHaveDps, tuning.RaidSurvivalMargin).ToString("0", CultureInfo.InvariantCulture)
                                            + " lostAtM=" + refusedBracket.RangeM.ToString("0", CultureInfo.InvariantCulture)
                                            + " lostAtOwn=" + refusedBracket.AttackerDps.ToString("0", CultureInfo.InvariantCulture)
                                            + " lostAtOpposed=" + refusedBracket.DefenderDps.ToString("0", CultureInfo.InvariantCulture));
                                }
                                continue;   // no eligible stock: no cost, no tempo stamp
                            }

                            world.Spend(st.Tag, cost);
                            _offensiveBudget.Record(st.Tag, world.UtcNow, cost, tuning.SpendBudgetWindowSeconds);
                            objective.LastDispatchUtc = world.UtcNow;
                            objective.LastDispatchFleetId = offensiveFleet.FleetId;
                        }
                    }
                }

                // A patrol already in the air answers a hostile holding still inside the focus
                // before a new one is dispatched: the hull is free and usually closer, and the
                // dispatch below then sees the circuit stop it re-queued rather than a gap.
                DivertPatrolsOntoDwellingHostiles(st, brainDriven, dwellingHostiles, world, tuning, log);

                // --- Patrol: the standing circuit a defensive focus keeps around its region.
                // Guard owns a node, Patrol owns a region, and the trigger is the difference. A
                // patrol is predictive: the focus is what sends it, so it sails while the region is
                // still quiet, and Threat only orders which member it visits first. Reading Threat
                // as the trigger instead made it a post-hoc sweep of whichever node had already been
                // attacked -- the same node Guard defends, chosen by a worse signal.
                {
                    for (int ci = 0; ci < st.CampaignFoci.Count; ci++)
                    {
                        CampaignFocus focus = st.CampaignFoci[ci];
                        if (focus == null || !HasDefendRegionObjective(focus)) continue;
                        // Provenance, as the offensive block reads it: a player faction patrols what
                        // its player asked for and nothing else. No player-issued defensive focus
                        // can be created yet, so this refuses every focus a player faction carries
                        // today and the seam is already the right shape when one can.
                        if (!brainDriven && !focus.PlayerIssued) continue;

                        // Eligible circuit members: what this faction still owns inside the focus
                        // region, minus anything already visited inside the cooldown.
                        var members = new List<SpatialNode>();
                        foreach (string memberId in focus.RegionMembers)
                        {
                            SpatialNode member;
                            if (!world.Graph.Nodes.TryGetValue(memberId, out member)) continue;
                            if (member.OwnerFactionTag != st.Tag) continue;
                            if (OnCooldown(st.PatrolNodeCooldowns, member.Id, world.UtcNow)) continue;
                            members.Add(member);
                        }
                        if (members.Count == 0) continue;

                        // Threat orders, it does not gate: the worst member leads the circuit so a
                        // region that IS hot is answered at its hottest point first, and a region
                        // that is cold still gets swept.
                        SpatialNode lead = null;
                        double leadScore = double.MinValue;
                        for (int mi = 0; mi < members.Count; mi++)
                        {
                            double score = members[mi].Threat;
                            double bonus;
                            if (st.PerceivedThreatBonusByNode.TryGetValue(members[mi].Id, out bonus))
                                score += bonus;
                            if (score > leadScore) { leadScore = score; lead = members[mi]; }
                        }
                        if (lead == null) continue;

                        // The origin is never the destination. Passing null here is what made 55 of
                        // 121 live dispatches read Nxxx -> Nxxx: the threatened node was its own
                        // nearest source at distance 0 and always won, so the hull flew nowhere.
                        SpatialNode patrolOrigin = FindShipSource(world, tuning, st.Tag, lead.Id, lead.Id, "Patrol");
                        if (patrolOrigin == null) continue;
                        if (!ShipPool.HasSuitableForRole(patrolOrigin, "Patrol", world.PrefabCatalogue)) continue;

                        // The rest of the circuit, nearest-neighbour from the lead. The origin drops
                        // out of it -- a leg that ends where the fleet started is a zero-distance
                        // arrival, and the return leg already brings it home.
                        var circuit = new List<SpatialNode> { lead };
                        var remaining = new List<SpatialNode>();
                        for (int mi = 0; mi < members.Count; mi++)
                            if (members[mi].Id != lead.Id && members[mi].Id != patrolOrigin.Id)
                                remaining.Add(members[mi]);
                        int maxStops = tuning.PatrolCircuitMaxStops > 0 ? tuning.PatrolCircuitMaxStops : 1;
                        while (circuit.Count < maxStops && remaining.Count > 0)
                        {
                            SpatialNode from = circuit[circuit.Count - 1];
                            int nearest = 0;
                            double nearestDist = double.MaxValue;
                            for (int ri = 0; ri < remaining.Count; ri++)
                            {
                                double d = from.Position.DistanceTo(remaining[ri].Position);
                                if (d < nearestDist) { nearestDist = d; nearest = ri; }
                            }
                            circuit.Add(remaining[nearest]);
                            remaining.RemoveAt(nearest);
                        }

                        long treasury = world.GetTreasury(st.Tag);
                        long patrolSurplus = tuning.PatrolWealthFractionPerWindow > 0
                            ? Economy.SpendableTreasurySurplus(treasury, world.GetNetFlow(st.Tag), tuning)
                            : 0;
                        if (treasury < tuning.PatrolCost) continue;
                        if (!_patrolBudget.CanSpend(st.Tag, world.UtcNow, st.CentralBankRecentIncome, tuning.PatrolCost,
                                                    tuning.PatrolMaxIncomeFractionPerWindow, tuning.PatrolBudgetFloor,
                                                    tuning.SpendBudgetWindowSeconds,
                                                    patrolSurplus, tuning.PatrolWealthFractionPerWindow))
                            continue;

                        // Desired convoy size scales with MilitaryPosture the same way Enact's
                        // Raid/Retaliate/Conquer/Guard block does -- minus the MusterFocusTag bonus,
                        // which only applies to actions with a rival-faction target.
                        int baseConvoySize = tuning.BurstSizeFor(ActionKind.Patrol);
                        double postureSignal = Math.Max(0.0, Math.Min(1.0, st.MilitaryPosture));
                        double scale = 1.0 + postureSignal * (tuning.MusterConvoyScaleMax - 1.0);
                        int desiredConvoySize = Math.Max(baseConvoySize, (int)Math.Round(baseConvoySize * scale));

                        ShipPool.RouteEnvironment patrolRoute = ShipPool.RouteEnvironment.From(
                            world.Planets, patrolOrigin.Position, lead.Position, tuning.MinDryTwr);
                        IPrefabCatalogue patrolCat = world.PrefabCatalogue;
                        Func<string, bool> patrolEligible = id => ShipPool.IsSuitableForRole("Patrol", patrolCat, id);
                        double patrolNeed = 0;
                        double patrolFuel = -1;
                        List<string> patrolSquadron;
                        // No fuel ledger means no leg can be priced, and every hull would tie at 0.
                        bool rankByFuel = tuning.PatrolFuelRanking && world.FuelLedger != null;
                        if (rankByFuel)
                        {
                            // Sized to what the circuit has actually faced, and flown on the hulls
                            // that meet it for the least fuel: a Naginata on a quiet circuit burned
                            // most of a small faction's hydrogen (FAC-423).
                            patrolNeed = PatrolThreatDps(st, circuit, inboundThreats, dwellingHostiles, world, tuning);
                            var legEnds = new List<Vec3> { patrolOrigin.Position };
                            for (int ci2 = 0; ci2 < circuit.Count; ci2++) legEnds.Add(circuit[ci2].Position);
                            legEnds.Add(patrolOrigin.Position);
                            var fuelCost = new HaulHullCost(world, tuning, patrolOrigin.Id,
                                                            patrolOrigin.Position, lead.Position, 0, 0);
                            int patrolMax = Math.Max(desiredConvoySize, tuning.RaidSquadronMaxFighters + 1);
                            patrolSquadron = SquadronAssembler.AssembleByCost(
                                patrolOrigin, patrolCat, patrolEligible, patrolRoute,
                                patrolNeed > 0 ? 1 : desiredConvoySize, patrolMax, patrolNeed,
                                id => fuelCost.FuelCreditsAlong(id, 0, legEnds));
                            if (patrolSquadron.Count > 0)
                            {
                                patrolFuel = 0;
                                for (int pi = 0; pi < patrolSquadron.Count; pi++)
                                {
                                    double c = fuelCost.FuelCreditsAlong(patrolSquadron[pi], 0, legEnds);
                                    if (c > 0) patrolFuel += c;
                                }
                            }
                        }
                        else
                        {
                            // Mixed, like a Guard's (FAC-312): the strongest hulls the node holds,
                            // posture-sized.
                            patrolSquadron = SquadronAssembler.AssembleDefence(
                                patrolOrigin, patrolCat, patrolEligible,
                                patrolRoute, desiredConvoySize, desiredConvoySize, 0);
                        }
                        int actualConvoySize = patrolSquadron.Count;
                        if (actualConvoySize <= 0)
                        {
                            // HasSuitableForRole above only checked tag-suitability -- assembly
                            // additionally applies the route-feasibility gate and found nothing that
                            // survives the trip. Must abort here: falling through would spend
                            // treasury and dispatch a phantom zero-ship, unknown-prefab fleet that
                            // later "returns" a ship that was never consumed (see ShipPool.Return's
                            // Math.Max(1, ConvoySize) floor).
                            ShipPool.MaybePostLocalShipyardInvestment(world, st, patrolOrigin, lead, log, "patrol");
                            string circuitBudget;
                            double circuitFloor = CombatFloorFor(st, world, tuning, out circuitBudget);
                            PostShipDemand(tuning, st.Tag, "Patrol", patrolOrigin.Id, lead.Id, world, log,
                                           circuitFloor, circuitBudget);
                            continue;
                        }

                        world.Spend(st.Tag, tuning.PatrolCost);
                        _patrolBudget.Record(st.Tag, world.UtcNow, tuning.PatrolCost, tuning.SpendBudgetWindowSeconds);
                        // Every stop is claimed, not just the lead: the circuit is covering them all,
                        // and a second patrol sent to a node this one is already inbound to is the
                        // wasted hull the per-node cooldown exists to refuse.
                        for (int si2 = 0; si2 < circuit.Count; si2++)
                            st.PatrolNodeCooldowns[circuit[si2].Id] = world.UtcNow + tuning.PatrolNodeCooldown;

                        double distKm = patrolOrigin.Position.DistanceTo(lead.Position);
                        DateTime departed = world.UtcNow;
                        DateTime arrival = tuning.FleetSpeedKmPerMin > 0
                            ? departed.AddMinutes(tuning.TransitTimeMins(distKm))
                            : departed;

                        // Later stops, then home. Each leg is planned when it is reached and gets
                        // its own graph path, so every one of them carries its own mid-transit
                        // meeting check rather than the single one a there-and-back sweep had.
                        var stops = new List<FleetStop>();
                        for (int si2 = 1; si2 < circuit.Count; si2++)
                            stops.Add(new FleetStop { NodeId = circuit[si2].Id, Kind = FleetStopKind.Service });
                        stops.Add(new FleetStop { NodeId = patrolOrigin.Id, Kind = FleetStopKind.Return });

                        var patrolFleet = new PendingFleetAction
                        {
                            Kind = ActionKind.Patrol,
                            ShipPrefabId = patrolSquadron[0],
                            ConvoySize = actualConvoySize,
                            Squadron = actualConvoySize > 1 ? patrolSquadron : null,
                            ActorTag = st.Tag,
                            OriginNodeId = patrolOrigin.Id,
                            TargetNodeId = lead.Id,
                            OriginPosKm = patrolOrigin.Position,
                            TargetPosKm = lead.Position,
                            DepartedUtc = departed,
                            EstimatedArrivalUtc = arrival,
                            FleetId = PendingFleetAction.MintId(st.PendingFleets, st.Tag, departed, lead.Id),
                            CurrentStopKind = FleetStopKind.Service,
                            RemainingStops = stops,
                            // The same bands a Guard is dispatched with. A Patrol that stays to
                            // defend duels on these, and an empty list is scored as zero DPS -- so a
                            // sweep with no weapons on record would stand as a paper defender.
                            WeaponBands = SquadronAssembler.WeaponBandsOf(patrolCat, patrolSquadron),
                            LastResolvedSegment = -1,
                        };
                        patrolFleet = PendingFleetResolver.PlanFleetLeg(patrolFleet, world, tuning, false, null, null, log);
                        FleetMunitions.Arm(ref patrolFleet, world, tuning, log);
                        st.PendingFleets.Add(patrolFleet);
                        // The focus's own objective records the dispatch, as an offensive one does
                        // at its strike: it is what the Campaigns window shows a player as the last
                        // time their order was acted on. Nothing reads it as a gate.
                        for (int oi2 = 0; oi2 < focus.Objectives.Count; oi2++)
                            if (focus.Objectives[oi2] != null && focus.Objectives[oi2].Kind == CampaignObjectiveKind.DefendRegion)
                                focus.Objectives[oi2].LastDispatchUtc = world.UtcNow;

                        var circuitIds = new List<string>();
                        for (int si2 = 0; si2 < circuit.Count; si2++) circuitIds.Add(circuit[si2].Id);
                        log.Add(st.Tag + " PATROL_DISPATCH cost=" + tuning.PatrolCost
                                + " count=" + actualConvoySize
                                + " " + patrolOrigin.Id + " -> " + lead.Id
                                + " focus=" + (focus.CenterNodeId ?? "-")
                                + " circuit=" + string.Join(">", circuitIds.ToArray())
                                + " squadron=" + SquadronAssembler.Describe(patrolSquadron)
                                + (rankByFuel
                                   ? " need=" + patrolNeed.ToString("0", CultureInfo.InvariantCulture)
                                     + " dps=" + ShipPool.TotalDps(SquadronAssembler.WeaponBandsOf(patrolCat, patrolSquadron)).ToString("0", CultureInfo.InvariantCulture)
                                     + " fuel=" + (patrolFuel >= 0 ? patrolFuel.ToString("0", CultureInfo.InvariantCulture) : "?")
                                   : ""));
                    }
                }
            }
        }

        /// <summary>The DPS a patrol on <paramref name="circuit"/> has to meet: the strongest hostile
        /// raid inbound to or holding at any of its nodes now, or recorded attacking one in this
        /// faction's fight ledger within <see cref="FactionTuning.PatrolThreatMemoryHours"/>. 0 when
        /// the circuit has faced nothing, which sizes the patrol by posture instead.
        /// <para>Provenance: FAC-423 — the only floor Patrol had was the faction's combat floor,
        /// 1.2M DPS live, which would have made patrols heavier, not lighter.</para></summary>
        private static double PatrolThreatDps(FactionState st, List<SpatialNode> circuit,
            Dictionary<string, PendingFleetAction> inboundThreats,
            Dictionary<string, PendingFleetAction> dwellingHostiles, WorldModel world, FactionTuning tuning)
        {
            var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < circuit.Count; i++) nodes.Add(circuit[i].Id);

            double need = 0;
            IPrefabCatalogue cat = world.PrefabCatalogue;
            foreach (string id in nodes)
            {
                PendingFleetAction f;
                if (inboundThreats != null && inboundThreats.TryGetValue(id, out f) && f.ActorTag != st.Tag)
                    need = Math.Max(need, FleetDps(f, cat));
                if (dwellingHostiles != null && dwellingHostiles.TryGetValue(id, out f) && f.ActorTag != st.Tag)
                    need = Math.Max(need, FleetDps(f, cat));
            }
            if (tuning.PatrolThreatMemoryHours > 0)
            {
                DateTime since = world.UtcNow.AddHours(-tuning.PatrolThreatMemoryHours);
                foreach (FightRecord fight in FightLedger.DefendedSince(st, since))
                    if (fight.NodeId != null && nodes.Contains(fight.NodeId))
                        need = Math.Max(need, fight.AttackerDps);
            }
            return need;
        }

        private static double FleetDps(PendingFleetAction f, IPrefabCatalogue cat)
        {
            if (f.WeaponBands != null && f.WeaponBands.Count > 0) return ShipPool.TotalDps(f.WeaponBands);
            return cat != null ? ShipPool.TotalDps(SquadronAssembler.WeaponBandsOf(cat, FleetMunitions.Hulls(f))) : 0;
        }

        /// <summary>Whether this focus is a defensive one — the kind
        /// <see cref="CampaignFocusLifecycle.TryStart"/> creates, carrying a single
        /// <see cref="CampaignObjectiveKind.DefendRegion"/> objective. An offensive focus stands on a
        /// staging node and names ground the faction does not hold, so its region members are not a
        /// circuit and a patrol sent around them would be flying into someone else's space.</summary>
        /// <summary>Hostile combat fleets that are currently <b>holding still</b>, keyed by the node
        /// they are holding at. Built once per tick, like the inbound-threat index beside it.
        ///
        /// <para><b>Only a stationary target can be caught.</b> <see cref="FactionTuning.FleetSpeedKmPerMin"/>
        /// is global, so nothing can overtake anything in transit; a fleet is reachable exactly while
        /// it is spending a dwell somewhere. The two dwells that mean "a hostile is busy here" are
        /// <see cref="FleetStopKind.Engage"/> — a raid holding at the node it is attacking — and
        /// <see cref="FleetStopKind.Intercept"/>, a raid holding a convoy it has caught (FAC-307).
        /// <see cref="FleetStopKind.Mine"/> is a dwell too and is deliberately absent: a mining ship
        /// is stationary, not hostile.</para>
        ///
        /// <para>The dwell's own end is the deadline. <see cref="PendingFleetAction.EstimatedArrivalUtc"/>
        /// on a dwell leg is when the hold expires, so a diversion that cannot arrive before it
        /// arrives at an empty node.</para></summary>
        private static Dictionary<string, PendingFleetAction> BuildDwellingHostileIndex(IList<FactionState> states)
        {
            var index = new Dictionary<string, PendingFleetAction>(StringComparer.OrdinalIgnoreCase);
            for (int si = 0; si < states.Count; si++)
            {
                FactionState st = states[si];
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.Resolved || f.Intercepted) continue;
                    if (!BenchScenario.SeenAsThreat(f)) continue;
                    if (f.CurrentStopKind != FleetStopKind.Engage && f.CurrentStopKind != FleetStopKind.Intercept)
                        continue;
                    if (f.Kind != ActionKind.Raid && f.Kind != ActionKind.Retaliate && f.Kind != ActionKind.Conquer)
                        continue;
                    if (f.TargetNodeId == null) continue;

                    PendingFleetAction existing;
                    // The one that will still be there longest, so a diversion has the best chance of
                    // arriving while it is still worth arriving.
                    if (index.TryGetValue(f.TargetNodeId, out existing)
                        && existing.EstimatedArrivalUtc >= f.EstimatedArrivalUtc) continue;
                    index[f.TargetNodeId] = f;
                }
            }
            return index;
        }

        /// <summary>Sends a patrol already in the air onto a hostile holding still inside one of this
        /// faction's defensive focus regions, if it can get there before the hold ends and can win
        /// when it does. Returns the number diverted.
        ///
        /// <para>Two gates, and neither is optional. <b>The deadline</b> is the dwell's own end, so a
        /// patrol that would arrive at an empty node stays on its circuit. <b>The strength rule</b> is
        /// slice 1's, read through <see cref="CombatStrength.OfDefenceAt"/> rather than
        /// <c>OfDefence</c>: the hostile may be parked on ground this faction does not hold, and a
        /// station it does not own is not fighting for it.</para>
        ///
        /// <para>Diverting rather than replacing — the circuit stop the patrol was flying is re-queued
        /// behind the detour, so a patrol that answers something comes back to finish its round. It
        /// inherits the Guard retarget hysteresis (<see cref="FactionTuning.GuardMaxRetargets"/> and
        /// <see cref="FactionTuning.GuardRetargetCooldownSeconds"/>) because it is the same concern:
        /// a fleet that re-aims every tick reaches nothing.</para></summary>
        private int DivertPatrolsOntoDwellingHostiles(FactionState st, bool brainDriven,
            Dictionary<string, PendingFleetAction> dwelling, WorldModel world, FactionTuning tuning, List<string> log)
        {
            if (dwelling.Count == 0 || tuning.GuardMaxRetargets <= 0) return 0;
            DateTime now = world.UtcNow;
            int diverted = 0;

            for (int i = 0; i < st.PendingFleets.Count; i++)
            {
                PendingFleetAction p = st.PendingFleets[i];
                if (p.Kind != ActionKind.Patrol) continue;
                if (p.Resolved || p.Intercepted || p.HasArrived(now)) continue;
                // A grid a player can see is not moved from here -- the host owns that handoff.
                if (p.MaterialisedEntityId != 0) continue;
                if (p.RetargetCount >= tuning.GuardMaxRetargets) continue;
                if (p.LastRetargetUtc != default(DateTime)
                    && (now - p.LastRetargetUtc).TotalSeconds < tuning.GuardRetargetCooldownSeconds) continue;

                SpatialNode bestNode = null;
                PendingFleetAction bestTarget = default(PendingFleetAction);
                double bestMargin = double.NegativeInfinity;

                foreach (KeyValuePair<string, PendingFleetAction> kv in dwelling)
                {
                    PendingFleetAction hostile = kv.Value;
                    if (hostile.ActorTag == st.Tag) continue;
                    if (!InDefendRegion(st, brainDriven, kv.Key)) continue;

                    SpatialNode node;
                    if (!world.Graph.Nodes.TryGetValue(kv.Key, out node)) continue;
                    if (string.Equals(p.TargetNodeId, kv.Key, StringComparison.OrdinalIgnoreCase)) continue;

                    // Can it get there before the hold ends? Scored from the patrol's interpolated
                    // position, which is a fresh point every tick and would miss the leg-geometry
                    // cache every time -- so the tiers are read from the two ends rather than walked.
                    Vec3 patrolPos = p.CurrentPosKm(now, tuning);
                    double transitMins = tuning.FleetSpeedKmPerMin > 0
                        ? tuning.TransitTimeMins(LegZoneProfile.FromEndpoints(world.Planets, tuning, patrolPos, node.Position))
                        : 0.0;
                    double marginSec = (hostile.EstimatedArrivalUtc - now.AddMinutes(transitMins)).TotalSeconds;
                    if (marginSec <= 0) continue;

                    // Can it win when it does? An attacker whose strength cannot be read is NOT
                    // chased: unlike a node under attack, nothing is lost by staying on the circuit,
                    // so the unmeasurable case refuses here where the Defend block answers it.
                    double attackDps;
                    if (!CombatStrength.TryOfAttack(hostile, out attackDps)) continue;
                    double defenceDps = CombatStrength.OfDefenceAt(p.WeaponBands, node, tuning, st.Tag);
                    if (!CombatStrength.Holds(defenceDps, attackDps, tuning.GuardCommitRatio)) continue;

                    if (marginSec <= bestMargin) continue;
                    bestMargin = marginSec;
                    bestNode = node;
                    bestTarget = hostile;
                }

                if (bestNode == null) continue;
                if (!PendingFleetResolver.Retarget(st, i, bestNode.Id,
                        PendingFleetResolver.FleetRetargetPolicy.Divert, world, tuning, log, "dwelling_hostile"))
                    continue;

                diverted++;
                log.Add(st.Tag + " PATROL_INTERCEPTS " + p.LogId + " -> " + bestNode.Id
                        + " vs=" + (bestTarget.ActorTag ?? "unknown")
                        + " kind=" + bestTarget.Kind
                        + " hostileFleet=" + (bestTarget.FleetId ?? "unknown")
                        + " window_margin_s=" + bestMargin.ToString("0", CultureInfo.InvariantCulture)
                        + " attack=" + ShipPool.TotalDps(bestTarget.WeaponBands).ToString("0", CultureInfo.InvariantCulture)
                        + " defence=" + CombatStrength.OfDefenceAt(p.WeaponBands, bestNode, tuning, st.Tag)
                              .ToString("0", CultureInfo.InvariantCulture));
            }
            return diverted;
        }

        /// <summary>Whether <paramref name="nodeId"/> lies inside any defensive focus this faction is
        /// entitled to act on — the same provenance rule the Patrol dispatch applies, so a player
        /// faction diverts only under a focus its player placed.</summary>
        private static bool InDefendRegion(FactionState st, bool brainDriven, string nodeId)
        {
            for (int ci = 0; ci < st.CampaignFoci.Count; ci++)
            {
                CampaignFocus focus = st.CampaignFoci[ci];
                if (focus == null || !HasDefendRegionObjective(focus)) continue;
                if (!brainDriven && !focus.PlayerIssued) continue;
                if (focus.RegionMembers.Contains(nodeId)) return true;
            }
            return false;
        }

        private static bool HasDefendRegionObjective(CampaignFocus focus)
        {
            return CampaignFocusLifecycle.HasDefendRegionObjective(focus);
        }

        /// <summary>Soonest-arriving unresolved Raid/Retaliate/Conquer aimed at each target node,
        /// across every faction. Deliberately not
        /// <see cref="PendingFleetResolver.TryFindIncomingThreat"/>: that one answers a different
        /// question (a bounty needs one candidate that a FleetId-keyed tracker can re-find later, so
        /// it skips FleetId-less legacy fleets and returns the first match). A defender needs the
        /// most urgent threat and does not care whether the raid can be tracked by id, so a legacy
        /// fleet counts here.</summary>
        private static Dictionary<string, PendingFleetAction> BuildInboundThreatIndex(
            IList<FactionState> states, WorldModel world)
        {
            var index = new Dictionary<string, PendingFleetAction>(StringComparer.OrdinalIgnoreCase);
            for (int si = 0; si < states.Count; si++)
            {
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (f.Kind != ActionKind.Raid && f.Kind != ActionKind.Retaliate && f.Kind != ActionKind.Conquer)
                        continue;
                    if (f.Intercepted || f.Resolved) continue;
                    if (!BenchScenario.SeenAsThreat(f)) continue;
                    string threatenedNodeId = PendingFleetResolver.SemanticTargetOf(f);
                    if (threatenedNodeId == null) continue;
                    // A combat fleet keeps its Kind on the way home, so a raider flying its Return
                    // leg reads as an inbound Raid against its OWN node. Filtered here rather than
                    // where the index is consumed: this index keeps only the soonest arrival per
                    // node, and a returning friendly usually IS the soonest, so leaving it in lets
                    // it evict a genuine hostile raid and the node is then never defended at all.
                    // The consumer's own actor check cannot recover that -- by then the real threat
                    // is gone from the index.
                    SpatialNode threatened;
                    if (world.Graph.Nodes.TryGetValue(threatenedNodeId, out threatened)
                        && threatened.OwnerFactionTag == f.ActorTag) continue;
                    PendingFleetAction existing;
                    if (index.TryGetValue(threatenedNodeId, out existing)
                        && existing.EstimatedArrivalUtc <= f.EstimatedArrivalUtc) continue;
                    index[threatenedNodeId] = f;
                }
            }
            return index;
        }

        /// <summary>The highest-ranked candidate of <paramref name="objective"/> worth sending a fleet
        /// at now: one that still exists, is still somebody else's, and has no unresolved combat fleet
        /// of this faction already inbound — a second wave on top of the first buys nothing until the
        /// first has landed, and the ranked list would otherwise name the same node every tempo.
        /// Null when the whole list is spent.</summary>
        private static SpatialNode PickCampaignTarget(FactionState st, CampaignObjective objective, WorldModel world)
        {
            for (int ci = 0; ci < objective.Candidates.Count; ci++)
            {
                string nodeId = objective.Candidates[ci];
                SpatialNode node;
                if (!world.Graph.Nodes.TryGetValue(nodeId, out node)) continue;
                if (node.OwnerFactionTag == st.Tag) continue;
                bool inbound = false;
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (f.Resolved || f.Intercepted) continue;
                    if (!OffensiveDispatch.IsCombatFleetAction(f.Kind)) continue;
                    // Through any clearance detour: a raid on one carries no TargetNodeId, and
                    // reading that field alone sent a fresh squadron every tick until it landed.
                    if (string.Equals(PendingFleetResolver.SemanticTargetOf(f), nodeId, StringComparison.Ordinal)) { inbound = true; break; }
                }
                if (inbound) continue;
                return node;
            }
            return null;
        }

        /// <summary>One wave of a marshalled defence: a squadron drawn from one node, not yet paid
        /// for. Nothing is spent until the whole marshal is planned, so a refusal can hand every
        /// hull back without having sold one.</summary>
        private struct GuardWave
        {
            public SpatialNode Origin;
            public List<string> Squadron;
            public List<WeaponBand> Bands;
            public double TransitMins;
        }

        /// <summary>Sends what <paramref name="st"/> can reach at <paramref name="defendNode"/> in
        /// time, in as many convoys as that takes.
        ///
        /// <para><b>Two bars, and they answer different questions.</b> The floor is survival: a
        /// defence that cannot hold (<see cref="FactionTuning.GuardCommitRatio"/>) is not sent at
        /// all, because a hull that dies without changing the outcome is worse than no hull. The
        /// target is destruction: a raider that merely loses the exchange flies home, repairs and
        /// comes back, so where the hulls exist the marshal keeps drawing until the duel's own
        /// arithmetic says the attacker dies (<see cref="CombatStrength.Destroys"/>). Between the
        /// two it sends what it has.</para>
        ///
        /// <para><b>Why several convoys rather than one larger one.</b> A fleet carries one origin,
        /// so a squadron can only be as strong as the pool at a single node: a faction whose
        /// warships are spread across its estate could field a winning defence and have no way to
        /// deliver it. Sources are walked nearest-first and a source that still holds hulls is drawn
        /// again before a further one is reached, so a second node is only involved when the first
        /// has nothing left to give.</para>
        ///
        /// <para>Every wave stands at the node as one force: <c>ResolvePatrolVsRaid</c> merges the
        /// bands of every active Guard there. A duel that read only the first would make each wave
        /// after it inert.</para></summary>
        private void MarshalDefence(FactionState st, SpatialNode defendNode, PendingFleetAction defendAgainst,
            Dictionary<string, List<WeaponBand>> guardedStrength, IList<FactionState> states,
            WorldModel world, FactionTuning tuning, List<string> log)
        {
            double attackDps;
            bool measurable = CombatStrength.TryOfAttack(defendAgainst, out attackDps);
            DateTime effectDeadline = EffectDeadline(defendAgainst);

            // Whatever is already standing or inbound fights alongside this, so it counts toward the
            // target and the marshal only tops up the difference.
            List<WeaponBand> standing;
            guardedStrength.TryGetValue(defendNode.Id, out standing);

            var force = new List<WeaponBand>();
            if (standing != null) force.AddRange(standing);
            double damageToDestroy = measurable
                ? CombatStrength.DamageToDestroy(attackDps, tuning.GuardOverwhelmMargin)
                : 0;

            long treasury = world.GetTreasury(st.Tag);
            long guardSurplus = tuning.GuardWealthFractionPerWindow > 0
                ? Economy.SpendableTreasurySurplus(treasury, world.GetNetFlow(st.Tag), tuning)
                : 0;

            // An attacker whose strength cannot be read is answered with one convoy and no more:
            // there is no figure to size a second against, and draining the pool into an unknown is
            // the failure the strength gate exists to prevent. Carried by the bar itself -- an
            // unreadable attacker leaves it at zero, which the first wave meets -- rather than by a
            // second cap beside it that could come to disagree with it.
            int maxWaves = CombatStrength.GuardMaxWaves(tuning);
            var waves = new List<GuardWave>();
            var barren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SpatialNode firstOrigin = null;

            // What the duel would fight with: the raid's tactic against this faction's, at the pace
            // of the slowest hull standing or marshalled so far (plus a candidate wave).
            Func<List<string>, DuelMatchup> matchupWith = candidate =>
            {
                var hulls = new List<string>();
                for (int i = 0; i < waves.Count; i++) hulls.AddRange(waves[i].Squadron);
                if (candidate != null) hulls.AddRange(candidate);
                return GuardMatchup(world, states, defendNode, defendAgainst, st, hulls);
            };

            while (waves.Count < maxWaves
                   && (waves.Count == 0
                       || CombatStrength.DuelDamage(force, defendNode, tuning, matchupWith(null)) < damageToDestroy))
            {
                SpatialNode origin = FindShipSource(world, tuning, st.Tag, barren, defendNode.Id, "Patrol");
                if (origin == null) break;

                double transitMins = tuning.FleetSpeedKmPerMin > 0
                    ? tuning.TransitTimeMins(LegZoneProfile.FromEndpoints(world.Planets, tuning, origin.Position, defendNode.Position))
                    : 0.0;

                // Two gates, and the first is the one that decides. A defender is worth sending when
                // it can be there before the raid's damage lands -- contact plus that raid's own
                // engagement dwell. Derived from the raid rather than from a constant, so retuning
                // the dwell moves this with it and a fleet already in the air keeps the deadline it
                // was dispatched under. The flat cap is only a sanity bound on how far a hull is
                // ever sent.
                // Sources are walked nearest-first, so one that cannot make the deadline ends the
                // walk: nothing further out can make it either.
                bool inTime = world.UtcNow.AddMinutes(transitMins) <= effectDeadline;
                if (!inTime) break;
                if (tuning.GuardResponseMaxTransitMinutes > 0
                    && transitMins > tuning.GuardResponseMaxTransitMinutes) break;

                // Planned cumulatively: the budget has to clear every wave this marshal intends,
                // not each one in isolation, because none of them is recorded until all are.
                long plannedCost = tuning.GuardCost * (waves.Count + 1);
                if (treasury < plannedCost) break;
                if (!_guardBudget.CanSpend(st.Tag, world.UtcNow, st.CentralBankRecentIncome, plannedCost,
                                           tuning.GuardMaxIncomeFractionPerWindow, tuning.GuardBudgetFloor,
                                           tuning.SpendBudgetWindowSeconds,
                                           guardSurplus, tuning.GuardWealthFractionPerWindow)) break;

                // Past every gate, so this node was genuinely asked for a hull. Only such a node is
                // owed a build order when nothing comes out: one that could not have arrived in time
                // is not short of ships, it is in the wrong place.
                if (firstOrigin == null) firstOrigin = origin;

                int desiredConvoySize = CombatStrength.GuardWaveMinHulls(tuning, st.MilitaryPosture);

                // What this wave is asked for, in the currency the marshal stops on: the duel damage
                // still missing, converted back into convoy DPS. Posture says how big a convoy this
                // faction likes to send and knows nothing about what is coming, so it is a floor here
                // rather than the size.
                double waveTarget = measurable
                    ? CombatStrength.DpsForDamage(
                        damageToDestroy - CombatStrength.DuelDamage(force, defendNode, tuning, matchupWith(null)), tuning)
                    : 0;

                ShipPool.RouteEnvironment guardRoute = ShipPool.RouteEnvironment.From(
                    world.Planets, origin.Position, defendNode.Position, tuning.MinDryTwr);
                IPrefabCatalogue guardCat = world.PrefabCatalogue;
                // The same bound the attacker's census prices this node's parked stock against
                // (CombatStrength.FieldableHullCap), so neither side sees a defence the other cannot.
                int maxHulls = CombatStrength.GuardWaveMaxHulls(tuning, st.MilitaryPosture);
                Func<string, bool> guardEligible = id => ShipPool.IsSuitableFor(ActionKind.Guard, guardCat, id);
                List<string> squadron = SquadronAssembler.AssembleDefence(
                    origin, guardCat, guardEligible, guardRoute, desiredConvoySize, maxHulls, waveTarget);

                // The opening bid is DPS and the bar is damage, and a hull that misses the long
                // range brackets converts one into the other at a rate only its own bands know. So
                // the wave is topped up a hull at a time against the real test, while it has room
                // and the node has anything left. Without this the convoy stops one or two hulls
                // short and the next wave trails a single ship behind it at a second GuardCost.
                if (measurable && squadron.Count > 0)
                    while (squadron.Count < maxHulls)
                    {
                        var withWave = new List<WeaponBand>(force);
                        withWave.AddRange(SquadronAssembler.WeaponBandsOf(guardCat, squadron));
                        if (CombatStrength.DuelDamage(withWave, defendNode, tuning, matchupWith(squadron))
                            >= damageToDestroy) break;
                        List<string> more = SquadronAssembler.AssembleDefence(
                            origin, guardCat, guardEligible, guardRoute, 1, 1, 0);
                        if (more.Count == 0) break;
                        squadron.AddRange(more);
                    }

                if (squadron.Count == 0)
                {
                    // Tag-suitable stock existed but nothing survives the route. Take this node out
                    // of the walk rather than asking it again, and keep looking further out.
                    barren.Add(origin.Id);
                    ShipPool.MaybePostLocalShipyardInvestment(world, st, origin, defendNode, log, "guard");
                    continue;
                }

                List<WeaponBand> bands = SquadronAssembler.WeaponBandsOf(guardCat, squadron);
                waves.Add(new GuardWave
                {
                    Origin = origin, Squadron = squadron, Bands = bands, TransitMins = transitMins,
                });
                force.AddRange(bands);
            }

            if (waves.Count == 0)
            {
                // Nothing reachable could field anything. The fight still has to buy something: the
                // node asks for the hull that would have answered it.
                if (firstOrigin != null)
                    PostShipDemand(tuning, st.Tag, "Patrol", firstOrigin.Id, defendNode.Id, world, log,
                                   minDps: measurable
                                       ? Math.Ceiling(CombatStrength.ShortfallFromShips(
                                           attackDps, defendNode, tuning, tuning.GuardCommitRatio))
                                       : 0);
                return;
            }

            double defenceDps = CombatStrength.OfDefence(force, defendNode, tuning);
            // Two questions, and the duel only answers the second. Holds compares DPS totals; the
            // fight is range-bracketed, so a defence firing where the attacker cannot reply wins
            // exchanges the totals call lost. Either verdict commits.
            bool worthTaking = CombatStrength.Holds(defenceDps, attackDps, tuning.GuardCommitRatio);
            DuelMatchup committedMatchup = matchupWith(null);
            bool killsInDuel = CombatStrength.Destroys(force, defendNode, tuning, attackDps,
                                                      tuning.GuardOverwhelmMargin, committedMatchup);
            if (measurable && !worthTaking && !killsInDuel)
            {
                // Everything reachable, plus the node's own guns, still loses. Sending anyway spends
                // the hulls and the money and changes nothing about the outcome -- the node is raided
                // either way and is then also short its warships for the next one.
                //
                // The refusal has to buy something or it decays into never defending anything: the
                // hulls go back to the pools they came from, and the fight becomes a build order
                // sized at the DPS that would have won it.
                int drawn = 0;
                for (int i = 0; i < waves.Count; i++)
                {
                    SquadronAssembler.ReturnAll(waves[i].Origin, waves[i].Squadron);
                    drawn += waves[i].Squadron.Count;
                }
                double wantDps = Math.Ceiling(CombatStrength.ShortfallFromShips(
                    attackDps, defendNode, tuning, tuning.GuardCommitRatio));
                PostShipDemand(tuning, st.Tag, "Patrol", waves[0].Origin.Id, defendNode.Id, world, log,
                               minDps: wantDps);
                log.Add(st.Tag + " GUARD_OUTGUNNED " + waves[0].Origin.Id + " -> " + defendNode.Id
                        + " vs=" + (defendAgainst.ActorTag ?? "unknown")
                        + " attack=" + attackDps.ToString("0", CultureInfo.InvariantCulture)
                        + " defence=" + defenceDps.ToString("0", CultureInfo.InvariantCulture)
                        + " duelDmg=" + CombatStrength.DuelDamage(force, defendNode, tuning, committedMatchup).ToString("0", CultureInfo.InvariantCulture)
                        + " needDmg=" + CombatStrength.DamageToDestroy(attackDps * CombatStrength.DefeatShare(committedMatchup),
                                                                       tuning.GuardOverwhelmMargin).ToString("0", CultureInfo.InvariantCulture)
                        + " station=" + CombatStrength.OfStation(defendNode, tuning).ToString("0", CultureInfo.InvariantCulture)
                        + " hulls=" + drawn
                        + " waves=" + waves.Count
                        + " wantDps=" + wantDps.ToString("0", CultureInfo.InvariantCulture));
                return;
            }

            double committedDamage = CombatStrength.DuelDamage(force, defendNode, tuning, committedMatchup);
            var dispatchedBands = new List<WeaponBand>();
            for (int i = 0; i < waves.Count; i++)
            {
                GuardWave wave = waves[i];
                world.Spend(st.Tag, tuning.GuardCost);
                _guardBudget.Record(st.Tag, world.UtcNow, tuning.GuardCost, tuning.SpendBudgetWindowSeconds);

                DateTime departed = world.UtcNow;
                DateTime arrival = tuning.FleetSpeedKmPerMin > 0
                    ? departed.AddMinutes(wave.TransitMins)
                    : departed;

                var guardFleet = new PendingFleetAction
                {
                    Kind = ActionKind.Guard,
                    ShipPrefabId = wave.Squadron[0],
                    ConvoySize = wave.Squadron.Count,
                    // Named only when mixed: a one-hull fleet keeps the single-grid shape everything
                    // downstream has always handled.
                    Squadron = wave.Squadron.Count > 1 ? wave.Squadron : null,
                    ActorTag = st.Tag,
                    OriginNodeId = wave.Origin.Id,
                    TargetNodeId = defendNode.Id,
                    TargetTag = defendNode.OwnerFactionTag,
                    OriginPosKm = wave.Origin.Position,
                    TargetPosKm = defendNode.Position,
                    DepartedUtc = departed,
                    EstimatedArrivalUtc = arrival,
                    // The origin disambiguates: every wave of one marshal shares a tag, an instant
                    // and a destination, so the destination alone would mint one id for all of them.
                    FleetId = PendingFleetAction.MintId(st.PendingFleets, st.Tag, departed, defendNode.Id,
                                                          wave.Origin.Id + ":" + i),
                    CurrentStopKind = FleetStopKind.Service,
                    WeaponBands = wave.Bands,
                    LastResolvedSegment = -1,
                    AgainstIntruder = defendAgainst.ActorTag == IntruderAttackerTag,
                };
                // Guard is one-way by design: ReturnGuardShip credits the hull back at the node it
                // defended when the duty window expires, so there is no Return stop to queue.
                guardFleet = PendingFleetResolver.PlanFleetLeg(guardFleet, world, tuning, false, null, null, log);
                FleetMunitions.Arm(ref guardFleet, world, tuning, log);
                st.PendingFleets.Add(guardFleet);
                dispatchedBands.AddRange(wave.Bands);

                // Two margins, because they answer different questions and only the first decides
                // anything. effect_margin_s is measured against when the raid's damage actually
                // lands -- contact plus its own engagement dwell -- so positive means this Guard
                // arrives while the fight is still running and can change the outcome. Ship speed is
                // global, so a Guard can never outrun a raid, only start closer; the dwell is what
                // buys the defender its time, and this is the number that says whether it buys
                // enough. contact_margin_s is the old geometric question (did it beat the raid to the
                // node) and is kept only to separate "the dwell saved it" from "it was going to be
                // there anyway".
                double marginSec = (effectDeadline - arrival).TotalSeconds;
                double contactMarginSec = (defendAgainst.EstimatedArrivalUtc - arrival).TotalSeconds;
                // Named only when the hull is going somewhere this faction does not own, which is
                // the whole of what a reader needs to tell a pact defence apart from an ordinary one.
                string allyField = defendNode.OwnerFactionTag != st.Tag
                    ? " ally=" + defendNode.OwnerFactionTag
                    : string.Empty;
                // The duel figures say what the marshal was aiming at and whether it got there:
                // the damage this whole defence expects to deal against the damage that kills the
                // attacker. Omitted against an attacker whose strength could not be read, where
                // neither number exists.
                string duelField = measurable
                    ? " duel=" + committedDamage.ToString("0", CultureInfo.InvariantCulture)
                      + "/" + damageToDestroy.ToString("0", CultureInfo.InvariantCulture)
                    : string.Empty;
                log.Add(st.Tag + " GUARD_DISPATCH cost=" + tuning.GuardCost
                        + " count=" + wave.Squadron.Count
                        + " " + wave.Origin.Id + " -> " + defendNode.Id
                        + " vs=" + (defendAgainst.ActorTag ?? "unknown")
                        + " threat=" + defendAgainst.Kind
                        + " threatFleet=" + (defendAgainst.FleetId ?? "unknown")
                        + " squadron=" + SquadronAssembler.Describe(wave.Squadron)
                        + " wave=" + (i + 1) + "/" + waves.Count
                        + duelField
                        + " effect_margin_s=" + marginSec.ToString("0", CultureInfo.InvariantCulture)
                        + " contact_margin_s=" + contactMarginSec.ToString("0", CultureInfo.InvariantCulture)
                        + allyField);
            }
            AddGuardedStrength(guardedStrength, defendNode.Id, dispatchedBands);
        }

        /// <summary>Redirects one of this faction's in-flight Guards onto <paramref name="defendNode"/>
        /// instead of dispatching a fresh one, when the node it is currently flying to has gone
        /// quiet or is now clearly the lesser emergency.
        ///
        /// <para>The urgency margin is what stops a defender ping-ponging: two flashpoints whose
        /// threat ETAs cross every tick would otherwise trade the same Guard back and forth and it
        /// would reach neither. The cooldown and the per-fleet retarget budget bound the churn the
        /// margin does not.</para>
        ///
        /// <para>A Guard whose current target has no inbound threat left is always a candidate --
        /// there is nothing there to be less urgent than.</para></summary>
        private static bool TryRedirectGuard(FactionState st, SpatialNode defendNode,
            PendingFleetAction defendAgainst, Dictionary<string, PendingFleetAction> inboundThreats,
            WorldModel world, FactionTuning tuning, List<string> log, out List<WeaponBand> movedBands)
        {
            movedBands = null;
            if (tuning.GuardMaxRetargets <= 0) return false;
            DateTime now = world.UtcNow;

            int bestIdx = -1;
            double bestSlack = double.NegativeInfinity;

            for (int i = 0; i < st.PendingFleets.Count; i++)
            {
                PendingFleetAction g = st.PendingFleets[i];
                if (g.Kind != ActionKind.Guard) continue;
                if (g.Resolved || g.Intercepted || g.HasArrived(now)) continue;
                // A grid a player can see is not moved from here -- the host owns that handoff.
                if (g.MaterialisedEntityId != 0) continue;
                if (g.RetargetCount >= tuning.GuardMaxRetargets) continue;
                if (g.LastRetargetUtc != default(DateTime)
                    && (now - g.LastRetargetUtc).TotalSeconds < tuning.GuardRetargetCooldownSeconds) continue;
                if (g.TargetNodeId == null) continue;

                PendingFleetAction currentThreat;
                double slack = inboundThreats.TryGetValue(g.TargetNodeId, out currentThreat)
                    ? (currentThreat.EstimatedArrivalUtc - defendAgainst.EstimatedArrivalUtc).TotalSeconds
                    : double.MaxValue;   // nothing inbound where it is headed
                if (slack < tuning.GuardRetargetUrgencyMarginSeconds) continue;
                if (slack <= bestSlack) continue;
                bestSlack = slack;
                bestIdx = i;
            }

            if (bestIdx < 0) return false;
            // Read before the move: the fleet's own bands are what now stands at the new node, and
            // the coverage index has to learn about them or the marshal will count the node as bare
            // and pay for a convoy that is already on its way.
            List<WeaponBand> bands = st.PendingFleets[bestIdx].WeaponBands;
            if (!PendingFleetResolver.Retarget(st, bestIdx, defendNode.Id,
                    PendingFleetResolver.FleetRetargetPolicy.Replace, world, tuning, log, "fresher_threat"))
                return false;
            PendingFleetAction moved = st.PendingFleets[bestIdx];
            moved.AgainstIntruder = defendAgainst.ActorTag == IntruderAttackerTag;
            st.PendingFleets[bestIdx] = moved;
            movedBands = bands;
            return true;
        }

        /// <summary>When <paramref name="raid"/>'s effect actually lands: its arrival plus its own
        /// engagement dwell, if it is carrying one.
        ///
        /// <para>Reads the dwell off the raid's queued <see cref="FleetStopKind.Engage"/> stop, not
        /// off live tuning. <c>CreatePending</c> bakes <see cref="FactionTuning.RaidDwellSeconds"/>
        /// into that stop at dispatch and the itinerary is persisted, so a raid already in the air
        /// keeps its dispatch-time dwell across a retune and across a reboot. Reading the live value
        /// here would give a fleet the wrong deadline whenever the two disagree, and would silently
        /// blend two populations in any measurement taken across a retune.</para>
        ///
        /// <para>A fleet already on its Engage leg has the dwell in
        /// <see cref="PendingFleetAction.EstimatedArrivalUtc"/> itself; one with no dwell queued at
        /// all -- a legacy persisted fleet, an immediate dispatch, or any fleet when the dwell is
        /// tuned off -- resolves on contact, and its deadline is its arrival.</para></summary>
        private static DateTime EffectDeadline(PendingFleetAction raid)
        {
            if (raid.CurrentStopKind == FleetStopKind.Engage) return raid.EstimatedArrivalUtc;
            if (raid.RemainingStops != null)
                for (int i = 0; i < raid.RemainingStops.Count; i++)
                    if (raid.RemainingStops[i].Kind == FleetStopKind.Engage)
                        return raid.EstimatedArrivalUtc.AddSeconds(raid.RemainingStops[i].DurationSeconds);
            return raid.EstimatedArrivalUtc;
        }

        /// <summary>A stand-in "threat" describing a player shooting <paramref name="node"/>, so the
        /// dispatch path below can treat both kinds of attack alike instead of branching twice.
        ///
        /// <para>Not a fleet and never added to anyone's <c>PendingFleets</c>. It exists to carry an
        /// arrival time (now — the attack is in progress) and a label for the dispatch line, and it
        /// deliberately has no <c>FleetId</c>: there is no fleet to correlate with, and a synthetic
        /// one would put an id in the log that nothing else in the world will ever mention.</para>
        ///
        /// <para><c>EffectDeadline</c> reads this as arriving-now with no queued dwell, so the
        /// reachability gate asks "can a defender be there immediately", which is the right question
        /// for an attack already underway and a stricter one than a raid gets.</para></summary>
        private static PendingFleetAction PlayerAttackThreat(SpatialNode node, DateTime now, FactionTuning tuning)
        {
            // Contact is now -- the attacker is already on the node -- but the node is not lost
            // until the siege sequence completes, and that window is what a defender has to arrive
            // in. Carried as an Engage dwell so EffectDeadline reads it the same way it reads a
            // raid's, rather than needing to know a player attack is a different shape.
            double dwellSeconds = 0;
            DateTime loss = PlayerSiege.LossDeadlineUtc(node, tuning);
            if (loss != DateTime.MaxValue) dwellSeconds = Math.Max(0.0, (loss - now).TotalSeconds);

            return new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = PlayerAttackerTag,
                TargetNodeId = node.Id,
                TargetPosKm = node.Position,
                OriginPosKm = node.Position,
                DepartedUtc = now,
                EstimatedArrivalUtc = now,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = node.Id, Kind = FleetStopKind.Engage, DurationSeconds = dwellSeconds }
                },
                LastResolvedSegment = -1,
            };
        }

        /// <summary>What a `vs=` reads as when the attacker is a player rather than a faction. Not a
        /// real faction tag and never matched against one.</summary>
        internal const string PlayerAttackerTag = "player";

        /// <summary>The pick urgency of an intruder-only node: later than any real arrival time, and
        /// still earlier than an empty pick.</summary>
        private static readonly DateTime IntruderUrgency = DateTime.MaxValue.AddDays(-1);

        /// <summary>What a `vs=` reads as when the threat is a detected enemy ship trespassing at the
        /// node rather than anything that has attacked it.</summary>
        internal const string IntruderAttackerTag = "intruder";

        /// <summary>The stand-in threat for a detected enemy ship at <paramref name="node"/>
        /// (<see cref="WorldModel.IsIntruded"/>): arriving now, with
        /// <see cref="TrespassPolicy.IntruderAnswerWindow"/> as its Engage dwell. It carries no weapon
        /// bands, so it is answered once and never reinforced, as any unmeasurable attacker is.</summary>
        private static PendingFleetAction IntruderThreat(SpatialNode node, DateTime now)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = IntruderAttackerTag,
                TargetNodeId = node.Id,
                TargetPosKm = node.Position,
                OriginPosKm = node.Position,
                DepartedUtc = now,
                EstimatedArrivalUtc = now,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = node.Id, Kind = FleetStopKind.Engage,
                                    DurationSeconds = TrespassPolicy.IntruderAnswerWindow.TotalSeconds }
                },
                LastResolvedSegment = -1,
            };
        }

        /// <summary>What a `vs=` reads as when a station's call named no attacking fleet.</summary>
        internal const string StationCallAttackerTag = "station_call";

        /// <summary>The stand-in threat for a station calling for protection
        /// (<see cref="StationCall"/>): arriving now, with <see cref="StationCallRules.AnswerWindow"/>
        /// as its Engage dwell, so a defender is in time when it can be there inside the window. When
        /// the call named the attacking fleet, that fleet's faction, id and weapon bands are carried,
        /// so the marshal sizes the defence against it; otherwise <c>vs=station_call</c> and one
        /// convoy, as for any attacker whose strength cannot be read.</summary>
        private static PendingFleetAction StationCallThreat(SpatialNode node, string attackerFleetId,
            IList<FactionState> states, DateTime now)
        {
            var t = new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = StationCallAttackerTag,
                TargetNodeId = node.Id,
                TargetPosKm = node.Position,
                OriginPosKm = node.Position,
                DepartedUtc = now,
                EstimatedArrivalUtc = now,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = node.Id, Kind = FleetStopKind.Engage,
                                    DurationSeconds = StationCallRules.AnswerWindow.TotalSeconds }
                },
                LastResolvedSegment = -1,
            };
            if (attackerFleetId == null || states == null) return t;
            for (int si = 0; si < states.Count; si++)
            {
                FactionState s = states[si];
                if (s == null) continue;
                for (int fi = 0; fi < s.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = s.PendingFleets[fi];
                    if (f.FleetId != attackerFleetId) continue;
                    t.ActorTag = f.ActorTag;
                    t.FleetId = f.FleetId;
                    t.WeaponBands = f.WeaponBands;
                    t.ShipPrefabId = f.ShipPrefabId;
                    t.Squadron = f.Squadron;
                    t.ConvoySize = f.ConvoySize;
                    return t;
                }
            }
            return t;
        }

        /// <summary>Every node that already has a Guard inbound to it or standing on it, across all
        /// factions. A Guard stays in PendingFleets after it resolves until its duty window expires,
        /// so one predicate covers both "on its way" and "already there".
        ///
        /// <para>Cross-faction because the resolution side is: both meeting paths accept any Guard
        /// whose actor is not the raider as the node's defender, so an ally's hull fills the slot as
        /// completely as the owner's own would and a second one buys nothing. Built once per tick
        /// and added to as the dispatch loop runs, since the states are walked in order and the
        /// node's owner may come after the ally that just covered it.</para>
        ///
        /// <para>Provenance: FAC-276.</para></summary>
        /// <summary>The matchup the raid commit gate predicts with: the squadron it would send, at its
        /// slowest hull's pace, against everything that would meet it at <paramref name="target"/> —
        /// the parked hulls its defence can field and every Guard standing on it. A node with neither
        /// is a station and cannot move.</summary>
        private static DuelMatchup RaidMatchup(WorldModel world, IList<FactionState> states, SpatialNode target,
            PendingFleetAction squadron, FactionState attacker, FactionTuning tuning, List<string> responseHulls)
        {
            List<string> defenderHulls = NodeDefenceHulls(target, world.PrefabCatalogue, tuning, states, attacker.Tag);
            if (responseHulls != null) defenderHulls.AddRange(responseHulls);
            return Matchup(world, target, squadron.WeaponBands, FleetMunitions.Hulls(squadron), attacker, attacker.Tag,
                           defenderHulls, FindState(states, target.OwnerFactionTag), target.OwnerFactionTag);
        }

        /// <summary>The hulls <paramref name="target"/>'s owner can still sail onto it once a raid is
        /// seen: the parked, Guard-suitable, route-feasible stock of its other nodes, nearest first,
        /// from every node whose transit clears both <see cref="FactionTuning.GuardResponseMaxTransitMinutes"/>
        /// and <paramref name="deadline"/> -- the walk <see cref="MarshalDefence"/> makes, read without
        /// drawing anything. The target's own parked stock is not included: the census already
        /// prices it.
        ///
        /// <para>Bounded by <see cref="CombatStrength.FieldableHullCap"/> less what the target fields
        /// itself, because both reach the fight through the same marshal and it sails at most that
        /// many hulls against one threat.</para>
        ///
        /// <para>Bounded by the defender's treasury too: the marshal pays
        /// <see cref="FactionTuning.GuardCost"/> per wave, so a defender that can afford fewer waves
        /// than <see cref="CombatStrength.GuardMaxWaves"/> fields proportionally fewer hulls, and one
        /// that cannot afford a wave fields none. Its spend-window budget is not read -- it moves
        /// between now and the raid's detection, and the treasury is the bound that does not. Pact
        /// partners' Guards are not priced.</para></summary>
        public static List<string> ProjectedGuardResponse(WorldModel world, FactionTuning tuning,
            SpatialNode target, DateTime? deadline)
        {
            var hulls = new List<string>();
            if (world == null || tuning == null || target == null) return hulls;
            string owner = target.OwnerFactionTag;
            IPrefabCatalogue cat = world.PrefabCatalogue;
            if (string.IsNullOrEmpty(owner) || cat == null) return hulls;

            int maxWaves = CombatStrength.GuardMaxWaves(tuning);
            if (tuning.GuardCost > 0)
            {
                long affordable = world.GetTreasury(owner) / tuning.GuardCost;
                if (affordable < maxWaves) maxWaves = (int)Math.Max(0, affordable);
            }
            if (maxWaves <= 0) return hulls;
            long cap = (long)maxWaves * CombatStrength.GuardWaveMaxHulls(tuning, 1.0);
            int left = cap > int.MaxValue ? int.MaxValue : (int)cap;
            List<KeyValuePair<string, int>> atTarget = CombatStrength.FieldableParkedStock(target, cat, tuning);
            for (int i = 0; i < atTarget.Count; i++) left -= atTarget[i].Value;
            if (left <= 0) return hulls;

            var sources = new List<KeyValuePair<double, SpatialNode>>();
            foreach (SpatialNode n in world.Graph.OwnedBy(owner))
            {
                if (n.Id == target.Id || n.ShipStock == null || n.ShipStock.Count == 0) continue;
                double transitMins = tuning.FleetSpeedKmPerMin > 0
                    ? tuning.TransitTimeMins(LegZoneProfile.FromEndpoints(world.Planets, tuning, n.Position, target.Position))
                    : 0.0;
                if (tuning.GuardResponseMaxTransitMinutes > 0 && transitMins > tuning.GuardResponseMaxTransitMinutes) continue;
                if (deadline.HasValue && world.UtcNow.AddMinutes(transitMins) > deadline.Value) continue;
                sources.Add(new KeyValuePair<double, SpatialNode>(n.Position.DistanceTo(target.Position), n));
            }
            sources.Sort((a, b) =>
            {
                int c = a.Key.CompareTo(b.Key);
                return c != 0 ? c : string.CompareOrdinal(a.Value.Id, b.Value.Id);
            });

            for (int s = 0; s < sources.Count && left > 0; s++)
            {
                SpatialNode origin = sources[s].Value;
                ShipPool.RouteEnvironment route = ShipPool.RouteEnvironment.From(
                    world.Planets, origin.Position, target.Position, tuning.MinDryTwr);
                List<KeyValuePair<string, int>> stock = CombatStrength.FieldableParkedStock(origin, cat, tuning);
                for (int i = 0; i < stock.Count && left > 0; i++)
                {
                    if (!ShipPool.IsRouteFeasible(cat, stock[i].Key, route)) continue;
                    int take = Math.Min(stock[i].Value, left);
                    for (int k = 0; k < take; k++) hulls.Add(stock[i].Key);
                    left -= take;
                }
            }
            return hulls;
        }

        /// <summary>The matchup a Guard commit predicts with: <paramref name="threat"/> against this
        /// faction's Guards standing on <paramref name="node"/> plus <paramref name="marshalled"/>, the
        /// hulls about to be sent.</summary>
        private static DuelMatchup GuardMatchup(WorldModel world, IList<FactionState> states, SpatialNode node,
            PendingFleetAction threat, FactionState defender, List<string> marshalled)
        {
            List<string> defenderHulls = StandingGuardHulls(node, states, threat.ActorTag);
            if (marshalled != null) defenderHulls.AddRange(marshalled);
            return Matchup(world, node, threat.WeaponBands, FleetMunitions.Hulls(threat),
                           FindState(states, threat.ActorTag), threat.ActorTag, defenderHulls, defender,
                           defender != null ? defender.Tag : null);
        }

        /// <summary>Both stances through <see cref="TacticChoice"/> — the learned pick each faction
        /// would make against the other when <see cref="WorldModel.TacticLearning"/>, in the context
        /// read from the attacker's bands and the defending hulls' catalogue bands plus the node's
        /// own turrets and floor.</summary>
        private static DuelMatchup Matchup(WorldModel world, SpatialNode node,
            IReadOnlyList<WeaponBand> attackerBands, List<string> attackerHulls, FactionState attacker, string attackerTag,
            List<string> defenderHulls, FactionState defender, string defenderTag)
        {
            double density; bool air;
            CombatMobility.EnvironmentAt(world.Planets, node, out density, out air);
            IPrefabCatalogue cat = world.PrefabCatalogue;
            double atkMobility = CombatMobility.OfHulls(cat, attackerHulls, density, air);
            double defMobility = CombatMobility.OfDefence(cat, defenderHulls, defenderHulls.Count > 0, density, air);
            if (!world.TacticLearning)
                return new DuelMatchup
                {
                    AttackerBands = attackerBands,
                    Attacker = TacticChoice.For(attacker, atkMobility),
                    Defender = TacticChoice.For(defender, defMobility),
                };

            var defenceBands = new List<WeaponBand>();
            if (cat != null)
                for (int i = 0; i < defenderHulls.Count; i++)
                {
                    IReadOnlyList<WeaponBand> b = cat.WeaponBands(defenderHulls[i]);
                    if (b != null) for (int k = 0; k < b.Count; k++) defenceBands.Add(b[k]);
                }
            if (node != null && node.StationWeapons != null) defenceBands.AddRange(node.StationWeapons);
            long flat = CombatStrength.StationFlat(node, world.Tuning);
            TacticContext atkCtx = TacticContext.Of(attackerBands, 0, defenceBands, flat, atkMobility, defMobility, world.Tuning);
            TacticContext defCtx = TacticContext.Of(defenceBands, flat, attackerBands, 0, defMobility, atkMobility, world.Tuning);
            string atkOpponent = defender != null ? defender.Tag : defenderTag;
            string defOpponent = attacker != null ? attacker.Tag : attackerTag;
            return new DuelMatchup
            {
                AttackerBands = attackerBands,
                Attacker = TacticChoice.For(attacker, atkMobility, atkOpponent, atkCtx, true, world.UtcNow),
                Defender = TacticChoice.For(defender, defMobility, defOpponent, defCtx, true, world.UtcNow),
            };
        }

        /// <summary>The prefab ids of every hull that meets an attack on <paramref name="node"/>: the
        /// parked hulls its defence can field (the census <see cref="CombatStrength.FieldableParkedStock"/>
        /// walks) and the Guards standing on it.</summary>
        private static List<string> NodeDefenceHulls(SpatialNode node, IPrefabCatalogue catalogue,
            FactionTuning tuning, IList<FactionState> states, string attackerTag)
        {
            List<string> hulls = StandingGuardHulls(node, states, attackerTag);
            if (node == null || catalogue == null) return hulls;
            List<KeyValuePair<string, int>> fielded = CombatStrength.FieldableParkedStock(node, catalogue, tuning);
            for (int i = 0; i < fielded.Count; i++) hulls.Add(fielded[i].Key);
            return hulls;
        }

        /// <summary>The hulls of every Guard standing on or inbound to <paramref name="node"/> that is
        /// not <paramref name="attackerTag"/>'s — the force <c>ResolvePatrolVsRaid</c> merges.</summary>
        private static FactionState FindState(IList<FactionState> states, string tag)
        {
            if (tag == null || states == null) return null;
            for (int i = 0; i < states.Count; i++)
                if (states[i].Tag == tag) return states[i];
            return null;
        }

        private static List<string> StandingGuardHulls(SpatialNode node, IList<FactionState> states, string attackerTag)
        {
            var hulls = new List<string>();
            if (node == null || states == null) return hulls;
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si].Tag == attackerTag) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (f.Kind == ActionKind.Guard && !f.Intercepted && f.TargetNodeId == node.Id)
                        hulls.AddRange(FleetMunitions.Hulls(f));
                }
            }
            return hulls;
        }

        private static Dictionary<string, List<WeaponBand>> BuildGuardedStrengthIndex(IList<FactionState> states)
        {
            var guarded = new Dictionary<string, List<WeaponBand>>(StringComparer.OrdinalIgnoreCase);
            for (int si = 0; si < states.Count; si++)
            {
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (f.Kind == ActionKind.Guard && !f.Intercepted && f.TargetNodeId != null)
                        AddGuardedStrength(guarded, f.TargetNodeId, f.WeaponBands);
                }
            }
            return guarded;
        }

        /// <summary>Records that <paramref name="nodeId"/> has a defence committed to it, and what
        /// that defence brings. A null or empty band list still creates the entry: a Guard with no
        /// weapons on record is a hull that has been paid for and is on its way, so the node counts
        /// as answered even though the bands add nothing to the duel.</summary>
        private static void AddGuardedStrength(Dictionary<string, List<WeaponBand>> guarded,
            string nodeId, IReadOnlyList<WeaponBand> bands)
        {
            if (nodeId == null) return;
            List<WeaponBand> at;
            if (!guarded.TryGetValue(nodeId, out at)) { at = new List<WeaponBand>(); guarded[nodeId] = at; }
            if (bands != null) at.AddRange(bands);
        }

        /// <summary>The running best defence target while the two candidate scans -- this faction's
        /// own estate, then its pact partners' -- feed one urgency ranking.
        ///
        /// <para>Provenance: FAC-276.</para></summary>
        private sealed class DefencePick
        {
            public SpatialNode Node;
            public PendingFleetAction Threat;
            public DateTime Urgency = DateTime.MaxValue;
        }

        /// <summary>Scores <paramref name="candidate"/> as a defence target and keeps it in
        /// <paramref name="pick"/> when its attack lands sooner than whatever is already there. Most
        /// urgent node wins.
        ///
        /// <para>A node that already has a defender -- anyone's, see
        /// <see cref="BuildGuardedNodeIndex"/> -- is skipped rather than reinforced, so a single
        /// flashpoint cannot drain the whole combat pool. So is a node whose attacker is
        /// <paramref name="st"/> itself.</para>
        ///
        /// <para><paramref name="partnerNode"/> marks a node owned by a pact partner rather than by
        /// <paramref name="st"/>. Those carry one further skip: an attacker that is itself a partner
        /// of <paramref name="st"/> is left alone, because a hull sent there takes a side between
        /// two allies.</para>
        ///
        /// <para>Provenance: FAC-276.</para></summary>
        private static void ConsiderDefence(DefencePick pick, SpatialNode candidate, FactionState st,
            bool partnerNode, Dictionary<string, PendingFleetAction> inboundThreats,
            Dictionary<string, List<WeaponBand>> guardedStrength, IList<FactionState> states,
            WorldModel world, FactionTuning tuning)
        {
            // A materialised station that cannot call -- no working antenna -- is cut off from help: that is the raider's counterplay.
            if (world.IsStationSilenced(candidate.Id)) return;
            PendingFleetAction threat;
            bool hasInbound = inboundThreats.TryGetValue(candidate.Id, out threat);
            // A player shooting the station is the other way a node comes under attack, and it is
            // the more urgent one: an inbound raid is minutes away and already has an arrival time
            // to sort by, while this is happening now. Synthesised as an arrival of "now" so one
            // comparison orders both kinds -- it is not a real fleet and nothing dereferences it
            // beyond the log line, which says so.
            bool underPlayerAttack = world.IsUnderPlayerAttack(candidate);
            // A station calling for protection is happening now too; it is answered like either.
            string callAttackerFleetId;
            bool calling = world.IsStationCalling(candidate.Id, out callAttackerFleetId);
            // An intruder is an enemy of the node's owner, by that owner's standing with the pilot,
            // which says nothing about a pact partner's -- so a partner does not answer one alone.
            bool intruded = !partnerNode && world.IsIntruded(candidate.Id);
            if (!hasInbound && !underPlayerAttack && !calling && !intruded) return;
            if (hasInbound && threat.ActorTag == st.Tag) return;
            if (partnerNode && hasInbound && Diplomacy.InPactWith(st, threat.ActorTag)) return;

            PendingFleetAction against = hasInbound && !underPlayerAttack
                ? threat
                : underPlayerAttack
                    ? PlayerAttackThreat(candidate, world.UtcNow, tuning)
                    : calling
                        ? StationCallThreat(candidate, callAttackerFleetId, states, world.UtcNow)
                        : IntruderThreat(candidate, world.UtcNow);

            // A node with a defence already committed is passed over only while that defence is
            // enough. "Enough" is the bar the marshal dispatches to -- a garrison that merely
            // outlasts the raider leaves it alive to repair and come back, and there may be hulls
            // standing idle that would have ended it.
            List<WeaponBand> standing;
            if (guardedStrength.TryGetValue(candidate.Id, out standing))
            {
                double committedAgainst;
                // An attacker whose strength cannot be read -- a player siege, or a fleet dispatched
                // before its bands were recorded -- is answered once and never reinforced. There is
                // no figure to size a second wave against, and draining the pool into an unknown is
                // the failure the strength gate exists to prevent.
                if (!CombatStrength.TryOfAttack(against, out committedAgainst)) return;
                DuelMatchup standingMatchup = GuardMatchup(world, states, candidate, against, st, null);
                if (CombatStrength.Destroys(standing, candidate, tuning, committedAgainst,
                                            tuning.GuardOverwhelmMargin, standingMatchup)) return;
            }

            // An intruder alone ranks behind every other kind anywhere, an inbound raid hours out
            // included: nothing has been hit yet.
            DateTime urgency = underPlayerAttack || calling
                ? world.UtcNow
                : hasInbound
                    ? threat.EstimatedArrivalUtc
                    : IntruderUrgency;
            if (urgency >= pick.Urgency) return;
            pick.Urgency = urgency;
            pick.Node = candidate;
            pick.Threat = against;
        }

        private static bool OnCooldown(Dictionary<string, DateTime> cooldowns, string key, DateTime now)
        {
            DateTime until;
            return cooldowns.TryGetValue(key, out until) && now < until;
        }


        /// <summary>Upkeep for a node no reinforce mission can reach: the pre-FAC-327 instant
        /// reinforce, at the same flat <see cref="FactionTuning.ReinforceCost"/> the mission pays.
        /// <paramref name="why"/> names which of the three hull gates refused —
        /// <c>no_hull_anywhere</c>, <c>no_role_hull</c> or <c>no_route_hull</c>.
        ///
        /// <para>Both counter-forces to
        /// <see cref="WorldModel.DecayInfluence(double,double,IList{FactionState},double)"/> need a
        /// hull — the mission itself, and the traffic credit a convoy leaves behind — so without this
        /// a faction that owns none loses its whole estate at <c>HoldDecayPerHour</c> and cannot
        /// re-acquire any of it. Money is the brake instead: the treasury and budget gates are
        /// upstream of this, and the node cooldown bounds the rate.</para>
        ///
        /// <para><b>Not offered to a faction whose hulls are merely busy.</b> The concurrency cap and
        /// the in-flight claim are both upstream of the hull gates, so a fleet that exists and is
        /// working is never a reason to reach this.</para>
        ///
        /// <para>Instant, and therefore uncontestable in the air — that is what makes it a fallback
        /// rather than a cheaper mission. The shortage demand is still posted at the call site: paying
        /// for hold is not a reason to stop wanting the hull.</para>
        ///
        /// <para><b>Refused while the node is being taken</b>, by the same test that refuses a flown
        /// reinforce on arrival (<see cref="PendingFleetResolver.AttackInProgressAt"/>): a non-allied
        /// raid standing at the node, a player siege or presence raid, or recent player damage.
        /// Otherwise a faction with no hulls would undo a raid that a faction with hulls cannot.
        /// Returns false and spends nothing when refused.</para></summary>
        private bool TryPayReinforceWithoutAHull(IList<FactionState> states, FactionState st, WorldModel world,
                                                 FactionTuning tuning, SpatialNode node, string why, IList<string> log)
        {
            string key = st.Tag + "|" + node.Id;
            string blockedBy = PendingFleetResolver.AttackInProgressAt(states, world, st, node.Id, node);
            if (blockedBy != null)
            {
                // Nothing is spent and no cooldown is taken, so the node is bought the first pass
                // after the attack ends. The line is throttled to one per cooldown per node: this
                // runs on a manager that ticks about once a second.
                DateTime loggedAt;
                if (!_paidReinforceBlockedLogged.TryGetValue(key, out loggedAt)
                    || world.UtcNow >= loggedAt + tuning.ReinforceNodeCooldown)
                {
                    _paidReinforceBlockedLogged[key] = world.UtcNow;
                    log.Add(st.Tag + " REINFORCE_PAID_BLOCKED " + node.Id
                            + " by=" + blockedBy
                            + " hold=" + node.HoldStrength.ToString("0.00", CultureInfo.InvariantCulture)
                            + " why=" + why);
                }
                return false;
            }
            _paidReinforceBlockedLogged.Remove(key);

            double holdBefore = node.HoldStrength;
            world.Spend(st.Tag, tuning.ReinforceCost);
            _reinforceBudget.Record(st.Tag, world.UtcNow, tuning.ReinforceCost, tuning.SpendBudgetWindowSeconds);
            st.ReinforceNodeCooldowns[node.Id] = world.UtcNow + tuning.ReinforceNodeCooldown;
            world.Reinforce(st.Tag, node.Id);
            log.Add(st.Tag + " REINFORCE_PAID cost=" + tuning.ReinforceCost
                    + " " + node.Id
                    + " hold=" + holdBefore.ToString("0.00", CultureInfo.InvariantCulture)
                    + " -> " + node.HoldStrength.ToString("0.00", CultureInfo.InvariantCulture)
                    + " why=" + why);
            return true;
        }

        /// <summary>Nearest owned node (excluding <paramref name="excludeNodeId"/>) with route-feasible
        /// stock of <paramref name="role"/>, preferring one whose stock can actually survive the trip to
        /// <paramref name="destNodeId"/> — same route-awareness upgrade as
        /// <c>OffensiveDispatch.FindBestOriginNode</c>, applied to Ferry/Patrol's source lookup. Falls back
        /// to the nearest merely role-suitable node (today's pre-route-awareness behaviour) when none is
        /// route-feasible, or when <paramref name="destNodeId"/> can't be resolved to a position.</summary>
        private static SpatialNode FindShipSource(WorldModel world, FactionTuning tuning, string factionTag,
            string excludeNodeId, string destNodeId, string role)
        {
            return FindShipSource(world, tuning, factionTag,
                                  excludeNodeId == null ? null : new HashSet<string>(
                                      new[] { excludeNodeId }, StringComparer.OrdinalIgnoreCase),
                                  destNodeId, role);
        }

        /// <summary>The same lookup over a <b>set</b> of nodes to skip, which is what a marshal
        /// needs: it walks sources nearest-first and calls this again after each draw, so a node
        /// that turned out to have nothing route-feasible has to stay out of the walk or the next
        /// call returns it forever.</summary>
        private static SpatialNode FindShipSource(WorldModel world, FactionTuning tuning, string factionTag,
            HashSet<string> excludeNodeIds, string destNodeId, string role)
        {
            SpatialNode destNode = null;
            bool hasDest = destNodeId != null && world.Graph.Nodes.TryGetValue(destNodeId, out destNode);

            SpatialNode bestRouteFeasible = null;
            double bestRouteFeasibleDist = double.MaxValue;
            SpatialNode bestAny = null;
            double bestAnyDist = double.MaxValue;

            foreach (SpatialNode n in world.Graph.OwnedBy(factionTag))
            {
                if (excludeNodeIds != null && excludeNodeIds.Contains(n.Id)) continue;
                if (!ShipPool.HasSuitableForRole(n, role, world.PrefabCatalogue)) continue;

                double d = hasDest ? n.Position.DistanceTo(destNode.Position) : 0;
                if (d < bestAnyDist) { bestAnyDist = d; bestAny = n; }

                if (hasDest)
                {
                    ShipPool.RouteEnvironment route = ShipPool.RouteEnvironment.From(
                        world.Planets, n.Position, destNode.Position, tuning.MinDryTwr);
                    if (d < bestRouteFeasibleDist && ShipPool.HasSuitableForRole(n, role, world.PrefabCatalogue, route))
                    {
                        bestRouteFeasibleDist = d;
                        bestRouteFeasible = n;
                    }
                }
            }

            return bestRouteFeasible ?? bestAny;
        }

        /// <summary>Posts a "Ship:{role}" demand carrying the real route that just failed to
        /// dispatch, so ShipyardManager's build-time hull selection can bias toward a prefab that
        /// actually survives <paramref name="destNodeId"/> instead of a degenerate same-point route.
        /// Reposition/combat roles carry no cargo requirement (MinCargoLiters left at its default 0).</summary>
        private static void PostShipDemand(FactionTuning tuning, string factionTag, string role, string nodeId, string destNodeId,
            WorldModel world, List<string> log, double minDps = 0, string budget = null)
        {
            bool isNew = world.Demand.Post(DemandEntry.ForShip(factionTag, role, nodeId, world.UtcNow.Add(ShipDemandTtlFor(tuning)),
                destNodeId: destNodeId, postedUtc: world.UtcNow, minDps: minDps));
            if (isNew)
                log.Add(factionTag + " SHIP_DEMAND role=" + role + " node=" + nodeId + " dest=" + destNodeId
                        + (minDps > 0 ? " minDps=" + minDps.ToString("0", CultureInfo.InvariantCulture) : "")
                        + (minDps > 0 && budget != null ? " budget=" + budget : ""));
        }

        /// <summary>The faction's standing combat power floor, for a <c>Patrol</c> demand posted
        /// because no hull could be found. Patrol draws the same pool Raid does
        /// (<see cref="ShipPool.IsSuitableFor"/>), so a Patrol build IS a combat build and has to be
        /// chosen against the same bar — a floorless one resolves on the posture blend, which at a
        /// high posture takes the strongest hull in the pool.
        ///
        /// <para>Self-gating: <see cref="ShipyardManager.CombatPowerFloor"/> is 0 for a faction with
        /// no offensive campaign and nothing in its fight ledger, so a peaceful network posts exactly
        /// what it posted before. The nodes that carry their own, sharper figure —
        /// <c>PostGarrisonDemands</c> and the marshal below — keep it; this is only for the three
        /// posts that had none.</para></summary>
        private static double CombatFloorFor(FactionState st, WorldModel world, FactionTuning tuning, out string budget)
        {
            return ShipyardManager.CombatPowerFloor(st, world, tuning, out budget);
        }
    }
}
