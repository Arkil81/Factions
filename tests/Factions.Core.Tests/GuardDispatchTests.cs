using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FleetManager's reactive Guard dispatch: an inbound Raid/Retaliate/Conquer detected against an
    /// owned node puts a defender on it. Guard is the only kind either meeting path in
    /// <see cref="PendingFleetResolver"/> accepts as a defender, so these cover the one route by
    /// which a raid can be contested at all.
    /// </summary>
    public class GuardDispatchTests
    {
        private sealed class Cat : IPrefabCatalogue
        {
            public readonly HashSet<string> Armed = new HashSet<string>();
            public float DpsPerShip = 500f;
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return Armed.Contains(prefabId)
                    ? new[] { new WeaponBand { Dps = DpsPerShip, RangeM = 800 } }
                    : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static FactionTuning GuardTuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.GuardCost = 1000;
            t.FleetSpeedKmPerMin = 1200.0;
            t.GuardMaxIncomeFractionPerWindow = 0; t.GuardBudgetFloor = 0;
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0;
            return t;
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.9;
            n.Radius = 40;
            return n;
        }

        private static Cat StockCombatShip(WorldModel w, SpatialNode node, string prefabId = "Gunship")
        {
            node.ShipStock[prefabId] = 1;
            Cat cat = w.PrefabCatalogue as Cat;
            if (cat == null) w.PrefabCatalogue = cat = new Cat();
            cat.Armed.Add(prefabId);
            return cat;
        }

        private static FactionState Raider(WorldModel w, string tag, string targetNodeId, double etaMinutes,
                                           ActionKind kind = ActionKind.Raid)
        {
            var st = new FactionState { Tag = tag, RngSeed = 1 };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = kind,
                ActorTag = tag,
                TargetNodeId = targetNodeId,
                OriginPosKm = new Vec3(500, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(etaMinutes),
                FleetId = "RAID" + targetNodeId,
                LastResolvedSegment = -1,
            });
            return st;
        }

        private static string GuardLine(List<string> log)
        {
            return log.FirstOrDefault(m => m.Contains("GUARD_DISPATCH"));
        }

        /// <summary>Marks <paramref name="node"/> as under player attack the way the host does --
        /// through the same public entry point, so these tests exercise the real clock rather than
        /// setting the field behind it.</summary>
        private static void PlayerIsAttacking(WorldModel w, SpatialNode node, FactionTuning t)
        {
            w.Tuning = t;
            node.SiegeStartedUtc = w.UtcNow;                       // the siege clock the deadline reads
            w.RegisterPlayerAggression(node.Id, t.PlayerAggressionMinDamage + 1);
        }

        // The headline: a player besieging a node is an attack, and it must summon a defender on its
        // own. Nothing else in the world is happening -- no faction has a fleet in the air -- which is
        // exactly the case that produced no Guard live on 2026-09-04.
        [Fact]
        public void PlayerAttack_WithNoFleetAnywhereInTheWorld_DispatchesGuard()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            PlayerIsAttacking(w, w.Graph.Nodes["N002"], t);

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender }, w, t, log);

            string line = GuardLine(log);
            Assert.NotNull(line);
            Assert.Contains("-> N002", line);
            Assert.Contains("vs=player", line);
            Assert.Single(defender.PendingFleets.Where(f => f.Kind == ActionKind.Guard
                                                            && f.TargetNodeId == "N002"));
        }

        /// <summary>Tuning whose transit time is exactly distance/speed, so a test can state a
        /// defender's travel time in minutes and mean it.
        ///
        /// <para>The shipped defaults ramp between <c>FleetSpeedMinKmPerMin</c> and
        /// <c>FleetSpeedKmPerMin</c>, so transit is a two-phase profile rather than a round number.
        /// Setting the two equal takes <c>TryTransitProfile</c>'s no-ramp path, where transit is
        /// distKm/speed exactly.</para>
        ///
        /// <para>Both must be set together. <c>TryTransitProfile</c> rejects a max below the min and
        /// <c>TransitTimeMins</c> reports that rejection as <b>0 minutes</b>, so lowering the speed
        /// alone yields an instant defender and a test that passes without testing
        /// anything.</para></summary>
        private static FactionTuning TimedTransitTuning()
        {
            FactionTuning t = GuardTuning();
            t.GuardResponseMaxTransitMinutes = 0;    // disabled: only the deadline may refuse
            t.FleetAccelKmPerMin2 = 1000.0;
            t.FleetSpeedKmPerMin = 1200.0;
            t.FleetSpeedMinKmPerMin = 1200.0;        // no ramp, so transit is distKm / 1200 exactly
            t.RaidZoneSiegeDwellSeconds = 3600.0;    // 60 min
            t.PlayerConquerOfferMinutes = 1.0;       // ... + 1 = a 61-minute fuse before success
            return t;
        }

        private static List<string> RunPlayerSiegeAt(FactionTuning t, double defenderDistanceKm,
                                                     bool raidAlreadySucceeded)
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            SpatialNode home = AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "N002", "DEF", new Vec3(defenderDistanceKm, 0, 0));
            w.Graph.Connect("HOME", "N002");
            StockCombatShip(w, home);
            PlayerIsAttacking(w, target, t);
            if (raidAlreadySucceeded) target.PlayerRaidSucceededUtc = w.UtcNow;

            var log = new List<string>();
            new FleetManager().Tick(new[] { new FactionState { Tag = "DEF", RngSeed = 1 } }, w, t, log);
            return log;
        }

        // The deadline is a real bound, not a formality. Same attack, same money and stock -- only
        // the distance changes, and with the flat transit cap switched off the siege clock is the
        // only thing left that can refuse. Without this, a manager that ignored the deadline
        // entirely would still pass the test above.
        [Fact]
        public void PlayerAttack_DefenderThatCannotArriveBeforeTheNodeIsLost_IsNotDispatched()
        {
            FactionTuning t = TimedTransitTuning();   // 61-minute fuse

            Assert.NotNull(GuardLine(RunPlayerSiegeAt(t, 12000, false)));   // 10 min out: makes it
            Assert.Null(GuardLine(RunPlayerSiegeAt(t, 120000, false)));     // 100 min out: does not
        }

        // A raid past its success threshold has a shorter, exactly-known fuse: the conquer offer.
        // Same node, same distance, same defender -- only the success stamp differs, so nothing but
        // the deadline can explain the two outcomes.
        [Fact]
        public void PlayerRaidAlreadySucceeded_DeadlineShortensToTheConquerOffer()
        {
            FactionTuning t = TimedTransitTuning();   // 61 min before success, 1 min after

            Assert.NotNull(GuardLine(RunPlayerSiegeAt(t, 12000, false)));   // 10 min out, 61 to go
            Assert.Null(GuardLine(RunPlayerSiegeAt(t, 12000, true)));       // 10 min out, 1 to go
        }

        // The O(1) gate that lets the defensive sweep be entered at all must close again, or every
        // faction's estate is walked on every tick forever after one round is fired.
        [Fact]
        public void AnyPlayerAttackActive_ClosesWhenTheResponseWindowExpires()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            Assert.False(w.AnyPlayerAttackActive(), "nothing has been shot yet");

            PlayerIsAttacking(w, w.Graph.Nodes["N002"], t);
            Assert.True(w.AnyPlayerAttackActive());

            w.UtcNow = w.UtcNow.AddSeconds(t.PlayerAggressionResponseWindowSeconds + 1);
            Assert.False(w.AnyPlayerAttackActive(), "the window must expire");
        }

        // A dispatched Patrol has to carry the weapons it is actually flying. A sweep that stays to
        // defend (FAC-257) duels on this list, and an empty one is scored as zero Dps -- so without
        // it a standing patrol is a paper defender that also occupies the node's one defender slot.
        // The arrival-side tests build their fleet by hand, so this is the only thing asserting the
        // dispatch end sets it.
        [Fact]
        public void PatrolDispatch_CarriesTheWeaponBandsOfTheHullItSends()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            w.Graph.Nodes["N002"].Threat = 0.8;      // orders the circuit; the focus is the trigger
            var st = new FactionState { Tag = "DEF", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(st, w.UtcNow, "N002");

            var log = new List<string>();
            new FleetManager().Tick(new[] { st }, w, t, log);

            Assert.Contains("PATROL_DISPATCH", string.Join("\n", log));
            PendingFleetAction patrol = st.PendingFleets.Single(f => f.Kind == ActionKind.Patrol);
            Assert.NotNull(patrol.WeaponBands);
            Assert.NotEmpty(patrol.WeaponBands);
            Assert.Equal(500f, patrol.WeaponBands.Sum(b => b.Dps));   // Cat.DpsPerShip x 1 hull
        }

        // The whole point: a detected inbound raid produces a defender at the threatened node.
        [Fact]
        public void InboundRaid_DispatchesGuardToThreatenedNode()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            FactionState raider = Raider(w, "RVN", "N002", 30);

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender, raider }, w, t, log);

            string line = GuardLine(log);
            Assert.NotNull(line);
            Assert.Contains("-> N002", line);
            Assert.Contains("vs=RVN", line);
            Assert.Single(defender.PendingFleets.Where(f => f.Kind == ActionKind.Guard
                                                            && f.TargetNodeId == "N002"));
        }

        // Negative control for the trigger: owning a node with combat stock and money is not itself
        // a reason to dispatch. Without this, every assertion above would also pass on a manager
        // that dispatched a Guard unconditionally.
        [Fact]
        public void NoInboundRaid_DispatchesNoGuard()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender }, w, t, log);

            Assert.Null(GuardLine(log));
            Assert.Empty(defender.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        // A node already covered is not reinforced: one defender per node, so a single flashpoint
        // cannot drain the combat pool. Covers the standing case (a Guard that has already resolved
        // still occupies the node until its duty window expires).
        [Fact]
        public void NodeAlreadyGuarded_DispatchesNoSecondGuard()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            defender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "DEF", TargetNodeId = "N002",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow.AddMinutes(-5), EstimatedArrivalUtc = w.UtcNow.AddMinutes(-4),
                Resolved = true, LastResolvedSegment = -1,
            });
            FactionState raider = Raider(w, "RVN", "N002", 30);

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender, raider }, w, t, log);

            Assert.Null(GuardLine(log));
            Assert.Single(defender.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        // A raid whose effect lands before any defender could arrive is not worth paying for.
        // The deadline is the deciding gate; the flat cap below is only a sanity bound.
        [Fact]
        public void ThreatLandingBeforeAnyGuardCouldArrive_DispatchesNoGuard()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            // vMax below vMin makes TryTransitProfile bail out and report a zero-length
            // trip, so the floor has to come down with the ceiling.
            t.FleetSpeedKmPerMin = 0.2; t.FleetSpeedMinKmPerMin = 0.1;   // ~50 min to cross the 10 km to N002
            t.GuardResponseMaxTransitMinutes = 0;   // uncapped, so the DEADLINE is what refuses
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            FactionState raider = Raider(w, "RVN", "N002", 1);  // lands in a minute, no dwell queued

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender, raider }, w, t, log);

            Assert.Null(GuardLine(log));
        }

        // An engagement dwell is what makes an otherwise-hopeless response worth making: the same
        // raid, same speeds, but the damage now lands at contact + dwell rather than at contact.
        // Deliberately reads the dwell off the raid's own queued stop, so a fleet in the air keeps
        // the deadline it was dispatched under.
        [Fact]
        public void EngagementDwell_MakesAnOtherwiseTooLateResponseWorthMaking()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            t.FleetSpeedKmPerMin = 0.2; t.FleetSpeedMinKmPerMin = 0.1;
            t.GuardResponseMaxTransitMinutes = 0;   // uncapped, so the DEADLINE is what admits
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            FactionState raider = Raider(w, "RVN", "N002", 1);
            PendingFleetAction r = raider.PendingFleets[0];
            r.RemainingStops = new List<FleetStop>
            {
                new FleetStop { NodeId = "N002", Kind = FleetStopKind.Engage, DurationSeconds = 7200 }
            };
            raider.PendingFleets[0] = r;

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender, raider }, w, t, log);

            string line = GuardLine(log);
            Assert.NotNull(line);
            // Beats the effect deadline but not contact -- exactly what the dwell bought.
            Assert.Contains("effect_margin_s=", line);
            Assert.Matches(@"contact_margin_s=-\d+", line);
        }

        // A raid on a planet-clearance detour has TargetNodeId nulled and its real destination
        // requeued behind Navigation stops. 22 of 61 live combat dispatches were in that state;
        // a defender keying on TargetNodeId alone never sees them.
        [Fact]
        public void RaidOnAClearanceLeg_IsStillSeenAtItsRealTarget()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            FactionState raider = Raider(w, "RVN", "N002", 30);
            PendingFleetAction r = raider.PendingFleets[0];
            r.TargetNodeId = null;                               // as PlanFleetLeg leaves it
            r.CurrentStopKind = FleetStopKind.Navigation;
            r.RemainingStops = new List<FleetStop>
            {
                new FleetStop { PositionKm = new Vec3(5, 0, 0), HasPosition = true, Kind = FleetStopKind.Navigation },
                new FleetStop { NodeId = "N002", Kind = FleetStopKind.Service },
            };
            raider.PendingFleets[0] = r;

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender, raider }, w, t, log);

            string line = GuardLine(log);
            Assert.NotNull(line);
            Assert.Contains("-> N002", line);
        }

        // The flat cap is the only difference between the two halves, so a pass here is that cap
        // and not the deadline gate or some other refusal.
        [Fact]
        public void ThreatBeyondMaxTransit_DispatchesNoGuard_ButDoesWithinIt()
        {
            for (int i = 0; i < 2; i++)
            {
                bool reachable = i == 1;
                WorldModel w = MakeDefenderWorld();
                FactionTuning t = GuardTuning();
                t.GuardResponseMaxTransitMinutes = reachable ? 20.0 : 0.0001;
                var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
                FactionState raider = Raider(w, "RVN", "N002", 30);

                var log = new List<string>();
                new FleetManager().Tick(new[] { defender, raider }, w, t, log);

                if (reachable) Assert.NotNull(GuardLine(log));
                else Assert.Null(GuardLine(log));
            }
        }

        // Tie-break: the loser must be genuinely eligible -- threatened, unguarded, reachable, and
        // equally affordable -- or a pass would only mean there was no contest.
        [Fact]
        public void MostUrgentThreatenedNodeWins()
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            AddNode(w, "SOON", "DEF", new Vec3(10, 0, 0));
            AddNode(w, "LATER", "DEF", new Vec3(10, 10, 0));
            StockCombatShip(w, w.Graph.Nodes["HOME"]);
            w.Graph.Nodes["HOME"].ShipStock["Gunship"] = 4; // enough for either target

            FactionTuning t = GuardTuning();
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            FactionState slow = Raider(w, "SLW", "LATER", 40);
            FactionState fast = Raider(w, "FST", "SOON", 5);

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender, slow, fast }, w, t, log);

            string line = GuardLine(log);
            Assert.NotNull(line);
            Assert.Contains("-> SOON", line);
            Assert.Contains("vs=FST", line);
        }

        // A defender with no weapon bands loses every duel it is selected for, so dispatching one
        // would leave the raid uncontested while looking contested in the log.
        [Fact]
        public void DispatchedGuardCarriesScaledWeaponBands()
        {
            WorldModel w = MakeDefenderWorld();
            w.Graph.Nodes["HOME"].ShipStock["Gunship"] = 3;
            FactionTuning t = GuardTuning();
            t.GuardBurstSize = 3;
            t.MusterConvoyScaleMax = 1.0; // isolate the burst size from the posture multiplier
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            FactionState raider = Raider(w, "RVN", "N002", 30);

            new FleetManager().Tick(new[] { defender, raider }, w, t, new List<string>());

            PendingFleetAction guard = defender.PendingFleets.Single(f => f.Kind == ActionKind.Guard);
            Assert.Equal(3, guard.ConvoySize);
            Assert.NotNull(guard.WeaponBands);
            Assert.Single(guard.WeaponBands);
            Assert.Equal(1500f, guard.WeaponBands[0].Dps); // 500 per hull x 3
        }

        // A node can change hands under a raid already in flight (Conquer flips ownership), leaving
        // a faction holding the target of its own attack. It must not garrison against itself.
        [Fact]
        public void OwnRaidAgainstOwnNode_DispatchesNoGuard()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            defender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "DEF", TargetNodeId = "N002",
                OriginPosKm = new Vec3(500, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(30),
                FleetId = "SELF", LastResolvedSegment = -1,
            });

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender }, w, t, log);

            Assert.Null(GuardLine(log));
        }

        // A combat fleet keeps Kind == Raid on its Return leg, so a raider flying home reads as an
        // inbound raid against its own node. Live 2026-09-03: 34 of 36 combat fleets took a station
        // duel at their OWN origin on the way back, so this is the common shape, not a corner.
        // The index must drop it, because it keeps only the soonest arrival per node and a
        // returning friendly is usually the soonest -- leaving it in evicts the real threat and the
        // node is never defended. A check at the consumer cannot recover what the index discarded.
        [Fact]
        public void ReturningOwnRaider_DoesNotMaskAGenuineThreatAtTheSameNode()
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            // DEF's own raider, flying home to N002, arriving SOONER than the hostile below.
            defender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "DEF", TargetNodeId = "N002",
                OriginPosKm = new Vec3(900, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(2),
                FleetId = "HOMEBOUND", LastResolvedSegment = -1,
            });
            FactionState raider = Raider(w, "RVN", "N002", 30);   // the real one, arriving later

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender, raider }, w, t, log);

            string line = GuardLine(log);
            Assert.NotNull(line);
            Assert.Contains("-> N002", line);
            Assert.Contains("vs=RVN", line);          // the hostile, not the homebound friendly
            Assert.Contains("threatFleet=RAIDN002", line);
        }

        [Fact]
        public void InsufficientTreasury_DispatchesNoGuard()
        {
            WorldModel w = MakeDefenderWorld();
            w.SetTreasury("DEF", 10); // below GuardCost
            FactionTuning t = GuardTuning();
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            FactionState raider = Raider(w, "RVN", "N002", 30);

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender, raider }, w, t, log);

            Assert.Null(GuardLine(log));
        }

        // Retaliate and Conquer are the other two kinds the meeting paths treat as raids; a
        // defender that only answered ActionKind.Raid would leave both uncontested.
        [Theory]
        [InlineData(ActionKind.Retaliate)]
        [InlineData(ActionKind.Conquer)]
        public void InboundRetaliateOrConquer_AlsoDispatchesGuard(ActionKind kind)
        {
            WorldModel w = MakeDefenderWorld();
            FactionTuning t = GuardTuning();
            var defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            FactionState raider = Raider(w, "RVN", "N002", 30, kind);

            var log = new List<string>();
            new FleetManager().Tick(new[] { defender, raider }, w, t, log);

            Assert.NotNull(GuardLine(log));
        }

        // ---- redirect consumer (PendingFleetResolver.Retarget) ---------------------------------

        /// <summary>HOME holds the hull; QUIET is where a Guard is already flying; URGENT is the
        /// fresher emergency. <paramref name="quietEtaMinutes"/> below 0 means nothing is inbound
        /// at QUIET at all.</summary>
        private static WorldModel MakeRedirectWorld(out FactionState defender, out List<FactionState> states,
                                                    double urgentEtaMinutes, double quietEtaMinutes)
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            SpatialNode home = AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            AddNode(w, "QUIET", "DEF", new Vec3(300, 0, 0));
            AddNode(w, "URGENT", "DEF", new Vec3(0, 300, 0));
            StockCombatShip(w, home);
            home.ShipStock["Gunship"] = 4;

            defender = new FactionState { Tag = "DEF", RngSeed = 1 };
            defender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "DEF",
                OriginNodeId = "HOME", TargetNodeId = "QUIET",
                OriginPosKm = home.Position, TargetPosKm = w.Graph.Nodes["QUIET"].Position,
                DepartedUtc = w.UtcNow.AddMinutes(-1), EstimatedArrivalUtc = w.UtcNow.AddMinutes(15),
                FleetId = "GUARD1", ConvoySize = 1, ShipPrefabId = "Gunship",
                LastResolvedSegment = -1,
            });

            states = new List<FactionState> { defender, Raider(w, "FST", "URGENT", urgentEtaMinutes) };
            if (quietEtaMinutes >= 0) states.Add(Raider(w, "SLW", "QUIET", quietEtaMinutes));
            return w;
        }

        // The point of the consumer: a hull already in the air is pulled over instead of buying a
        // second one. Asserting the treasury too, because "redirected" and "redirected AND did not
        // also dispatch" are different outcomes and only the second one is the saving.
        [Fact]
        public void InFlightGuard_IsRedirectedToFresherThreat_InsteadOfDispatchingASecond()
        {
            FactionState defender; List<FactionState> states;
            WorldModel w = MakeRedirectWorld(out defender, out states, 5, 40);
            FactionTuning t = GuardTuning();
            long treasuryBefore = w.GetTreasury("DEF");

            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);

            Assert.Contains(log, m => m.Contains("FLEET_RETARGET") && m.Contains("-> URGENT")
                                      && m.Contains("cause=fresher_threat"));
            Assert.Null(GuardLine(log));
            Assert.Equal(treasuryBefore, w.GetTreasury("DEF"));
            PendingFleetAction guard = defender.PendingFleets.Single(f => f.Kind == ActionKind.Guard);
            Assert.Equal("URGENT", guard.TargetNodeId);
            Assert.Equal(1, guard.RetargetCount);
        }

        // A Guard whose own flashpoint has gone quiet has nothing to be less urgent than.
        [Fact]
        public void InFlightGuard_IsRedirected_WhenItsOwnTargetHasNoThreatLeft()
        {
            FactionState defender; List<FactionState> states;
            WorldModel w = MakeRedirectWorld(out defender, out states, 5, -1);

            var log = new List<string>();
            new FleetManager().Tick(states, w, GuardTuning(), log);

            Assert.Contains(log, m => m.Contains("FLEET_RETARGET") && m.Contains("-> URGENT"));
        }

        // Hysteresis. Two flashpoints one minute apart must not trade the same defender back and
        // forth; the fresh dispatch is the correct answer there, not a redirect.
        [Fact]
        public void InFlightGuard_NotRedirected_WhenUrgencyGapIsInsideTheMargin()
        {
            FactionState defender; List<FactionState> states;
            WorldModel w = MakeRedirectWorld(out defender, out states, 5, 6); // 60s apart
            FactionTuning t = GuardTuning();
            t.GuardRetargetUrgencyMarginSeconds = 120.0;

            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);

            Assert.DoesNotContain(log, m => m.Contains("FLEET_RETARGET"));
            Assert.NotNull(GuardLine(log));
            Assert.Equal("QUIET", defender.PendingFleets.First(f => f.Kind == ActionKind.Guard
                                                                   && f.FleetId == "GUARD1").TargetNodeId);
        }

        [Fact]
        public void InFlightGuard_NotRedirected_WhileOnRetargetCooldown()
        {
            FactionState defender; List<FactionState> states;
            WorldModel w = MakeRedirectWorld(out defender, out states, 5, 40);
            FactionTuning t = GuardTuning();
            PendingFleetAction g = defender.PendingFleets[0];
            g.LastRetargetUtc = w.UtcNow.AddSeconds(-10);   // well inside the 300s cooldown
            g.RetargetCount = 1;
            defender.PendingFleets[0] = g;

            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);

            Assert.DoesNotContain(log, m => m.Contains("FLEET_RETARGET"));
        }

        [Fact]
        public void InFlightGuard_NotRedirected_PastItsRetargetBudget()
        {
            FactionState defender; List<FactionState> states;
            WorldModel w = MakeRedirectWorld(out defender, out states, 5, 40);
            FactionTuning t = GuardTuning();
            PendingFleetAction g = defender.PendingFleets[0];
            g.RetargetCount = t.GuardMaxRetargets;
            g.LastRetargetUtc = w.UtcNow.AddHours(-1);      // cooldown long expired, budget is the gate
            defender.PendingFleets[0] = g;

            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);

            Assert.DoesNotContain(log, m => m.Contains("FLEET_RETARGET"));
        }

        private static WorldModel MakeDefenderWorld()
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            SpatialNode home = AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            AddNode(w, "N002", "DEF", new Vec3(10, 0, 0));
            w.Graph.Connect("HOME", "N002");
            StockCombatShip(w, home);
            return w;
        }
    }
}
