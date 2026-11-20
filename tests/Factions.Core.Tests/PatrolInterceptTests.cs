using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-309 slice 4: a patrol already in the air answers a hostile that is holding still inside
    /// its defensive focus — if it can arrive before the hold ends and can win when it does.
    ///
    /// <para>Interception is only possible against a stationary target: <c>FleetSpeedKmPerMin</c> is
    /// global, so nothing overtakes anything in transit. The catchable set is exactly the fleets
    /// spending a dwell — a raid on its <c>Engage</c> window, or one holding a convoy it caught on an
    /// <c>Intercept</c> window (FAC-307).</para>
    /// </summary>
    public class PatrolInterceptTests
    {
        private sealed class Cat : IPrefabCatalogue
        {
            public readonly Dictionary<string, float> Dps = new Dictionary<string, float>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                float d;
                return Dps.TryGetValue(prefabId ?? "", out d) && d > 0
                    ? new[] { new WeaponBand { Dps = d, RangeM = 800 } }
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

        private static FactionTuning Tuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.FleetSpeedKmPerMin = 1200.0;
            t.FleetSpeedMinKmPerMin = 1200.0;      // no ramp: transit is distKm/speed exactly
            t.GuardCommitRatio = 1.0;              // stated, not inherited from the shipped balance
            t.GuardMaxRetargets = 2;
            t.GuardRetargetCooldownSeconds = 300.0;
            t.StationDefensePower = 0;
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            t.GuardMaxIncomeFractionPerWindow = 0; t.GuardBudgetFloor = 0;
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

        /// <summary>HOME and FAR are both ours and both inside one defensive focus. A patrol is in
        /// flight HOME -> HOME2 with a circuit stop queued; a hostile holds at FAR.</summary>
        private static WorldModel MakeWorld(out FactionState def, out FactionState foe, FactionTuning t,
                                            double patrolDps, double hostileDps,
                                            double hostileHoldSeconds, double farKm,
                                            FleetStopKind hostileStop = FleetStopKind.Engage,
                                            string hostileNodeOwner = "DEF")
        {
            WorldModel w = TestSupport.World();
            w.Tuning = t;
            w.SetTreasury("DEF", 500000);
            var cat = new Cat();
            cat.Dps["Picket"] = (float)patrolDps;
            cat.Dps["Raider"] = (float)hostileDps;
            w.PrefabCatalogue = cat;

            AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            AddNode(w, "HOME2", "DEF", new Vec3(10, 0, 0));
            AddNode(w, "FAR", hostileNodeOwner, new Vec3(farKm, 0, 0));

            def = new FactionState { Tag = "DEF", RngSeed = 1 };
            DefensiveFocusFixtures.GiveDefensiveFocus(def, w.UtcNow, "HOME", "HOME2", "FAR");

            // A patrol in the air, mid-circuit: one stop still queued behind it.
            def.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Patrol, ActorTag = "DEF",
                ShipPrefabId = "Picket", ConvoySize = 1,
                OriginNodeId = "HOME", TargetNodeId = "HOME2",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow.AddMinutes(30),
                FleetId = "PATROL1",
                CurrentStopKind = FleetStopKind.Service,
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "HOME", Kind = FleetStopKind.Return } },
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = (float)patrolDps, RangeM = 800 } },
                LastResolvedSegment = -1,
            });

            foe = new FactionState { Tag = "RVN", RngSeed = 1 };
            foe.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN",
                ShipPrefabId = "Raider", ConvoySize = 1,
                TargetNodeId = "FAR",
                OriginPosKm = new Vec3(farKm, 0, 0), TargetPosKm = new Vec3(farKm, 0, 0),
                DepartedUtc = w.UtcNow,
                // On a dwell leg EstimatedArrivalUtc is when the hold expires.
                EstimatedArrivalUtc = w.UtcNow.AddSeconds(hostileHoldSeconds),
                FleetId = "RAID1",
                CurrentStopKind = hostileStop,
                WeaponBands = hostileDps > 0
                    ? new List<WeaponBand> { new WeaponBand { Dps = (float)hostileDps, RangeM = 800 } }
                    : null,
                LastResolvedSegment = -1,
            });
            return w;
        }

        private static List<string> Run(WorldModel w, FactionTuning t, params FactionState[] states)
        {
            var log = new List<string>();
            new FleetManager().Tick(states, w, t, log);
            return log;
        }

        private static string Line(List<string> log, string token)
        {
            return log.FirstOrDefault(m => m.Contains(token));
        }

        // ---- the headline ------------------------------------------------------------------------

        // The pair the slice exists for. Same world, same patrol, same hostile — only the hold length
        // differs, so nothing but the deadline can explain the two outcomes.
        [Fact]
        public void APatrolDivertsOntoAHostileItCanReachInTime_AndNotOntoOneItCannot()
        {
            FactionTuning t = Tuning();
            FactionState d, f;

            // FAR is 12,000 km out = 10 min at 1200 km/min. A 20-minute hold is reachable.
            WorldModel reach = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                         hostileHoldSeconds: 1200, farKm: 12000);
            List<string> got = Run(reach, t, d, f);
            string line = Line(got, "PATROL_INTERCEPTS");
            Assert.NotNull(line);
            Assert.Contains("-> FAR", line);
            Assert.Contains("vs=RVN", line);
            Assert.Equal("FAR", d.PendingFleets.Single(x => x.Kind == ActionKind.Patrol).TargetNodeId);

            // Same geometry, a hold that ends in one minute: the patrol would arrive at an empty node.
            WorldModel late = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                        hostileHoldSeconds: 60, farKm: 12000);
            Assert.Null(Line(Run(late, t, d, f), "PATROL_INTERCEPTS"));
            Assert.Equal("HOME2", d.PendingFleets.Single(x => x.Kind == ActionKind.Patrol).TargetNodeId);
        }

        // Slice 1's rule, applied to the picket. Same reachable hostile, only its strength differs.
        [Fact]
        public void APatrolDoesNotDivertOntoAFightItWouldLose()
        {
            FactionTuning t = Tuning();
            FactionState d, f;

            WorldModel winnable = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                            hostileHoldSeconds: 1200, farKm: 12000);
            Assert.NotNull(Line(Run(winnable, t, d, f), "PATROL_INTERCEPTS"));

            WorldModel hopeless = MakeWorld(out d, out f, t, patrolDps: 100, hostileDps: 9000,
                                            hostileHoldSeconds: 1200, farKm: 12000);
            Assert.Null(Line(Run(hopeless, t, d, f), "PATROL_INTERCEPTS"));
        }

        // The circuit is resumed, not abandoned — the whole reason this uses Divert rather than the
        // Replace that Guard's redirect uses.
        [Fact]
        public void ADivertedPatrolStillHasItsCircuitBehindIt()
        {
            FactionTuning t = Tuning();
            FactionState d, f;
            WorldModel w = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                     hostileHoldSeconds: 1200, farKm: 12000);

            Assert.NotNull(Line(Run(w, t, d, f), "PATROL_INTERCEPTS"));

            PendingFleetAction p = d.PendingFleets.Single(x => x.Kind == ActionKind.Patrol);
            Assert.Equal("FAR", p.TargetNodeId);
            Assert.NotNull(p.RemainingStops);
            // The stop it was flying is re-queued ahead of the rest of the round.
            Assert.Contains(p.RemainingStops, s => s.NodeId == "HOME2");
            Assert.Contains(p.RemainingStops, s => s.NodeId == "HOME" && s.Kind == FleetStopKind.Return);
        }

        // ---- what must NOT be chased --------------------------------------------------------------

        // Scope. A hostile holding outside every defensive focus is someone else's problem; chasing it
        // takes the picket off the region it was posted to.
        [Fact]
        public void AHostileOutsideTheFocusRegionIsNotChased()
        {
            FactionTuning t = Tuning();
            FactionState d, f;
            WorldModel w = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                     hostileHoldSeconds: 1200, farKm: 12000);
            // Same world, but the focus no longer covers FAR.
            d.CampaignFoci.Clear();
            DefensiveFocusFixtures.GiveDefensiveFocus(d, w.UtcNow, "HOME", "HOME2");

            Assert.Null(Line(Run(w, t, d, f), "PATROL_INTERCEPTS"));
        }

        /// <summary>A hostile whose strength cannot be read is left alone, which is the OPPOSITE of the
        /// Defend block's answer and deliberately so: a node under attack loses something if nobody
        /// sails, while a patrol that stays on its circuit loses nothing.</summary>
        [Fact]
        public void AnUnmeasurableHostileIsNotChased()
        {
            FactionTuning t = Tuning();
            FactionState d, f;
            WorldModel w = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 0,
                                     hostileHoldSeconds: 1200, farKm: 12000);
            Assert.True(f.PendingFleets[0].WeaponBands == null);

            Assert.Null(Line(Run(w, t, d, f), "PATROL_INTERCEPTS"));
        }

        // A dwell is not the same as a threat. Mine is stationary by the identical mechanism and must
        // not put a mining ship on the target list.
        [Fact]
        public void AMiningDwellIsNotAHostile()
        {
            FactionTuning t = Tuning();
            FactionState d, f;
            WorldModel w = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                     hostileHoldSeconds: 1200, farKm: 12000);
            PendingFleetAction m = f.PendingFleets[0];
            m.Kind = ActionKind.Mine;
            m.CurrentStopKind = FleetStopKind.Mine;
            f.PendingFleets[0] = m;

            Assert.Null(Line(Run(w, t, d, f), "PATROL_INTERCEPTS"));
        }

        // A raid still flying at its target cannot be caught — global speed — so it is not offered.
        [Fact]
        public void AHostileStillInTransitIsNotChased()
        {
            FactionTuning t = Tuning();
            FactionState d, f;
            WorldModel w = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                     hostileHoldSeconds: 1200, farKm: 12000);
            PendingFleetAction r = f.PendingFleets[0];
            r.CurrentStopKind = FleetStopKind.Service;    // in transit, not holding
            f.PendingFleets[0] = r;

            Assert.Null(Line(Run(w, t, d, f), "PATROL_INTERCEPTS"));
        }

        // A convoy-robbing hold is the other catchable shape, and the one a patrol is uniquely for:
        // no node defence answers a pirate parked on a corridor.
        [Fact]
        public void AnInterceptWindowIsAlsoACatchableHold()
        {
            FactionTuning t = Tuning();
            FactionState d, f;
            WorldModel w = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                     hostileHoldSeconds: 1200, farKm: 12000,
                                     hostileStop: FleetStopKind.Intercept);

            Assert.NotNull(Line(Run(w, t, d, f), "PATROL_INTERCEPTS"));
        }

        // ---- the station term ---------------------------------------------------------------------

        /// <summary>A station this faction does not own is not fighting for it. Same numbers either
        /// way; only the node's owner differs, so nothing else can explain the two outcomes.</summary>
        [Fact]
        public void AStationCountsOnlyWhereTheFactionOwnsTheNode()
        {
            FactionTuning t = Tuning();
            t.StationDefensePower = 0;
            FactionState d, f;

            // Patrol 100 against 500: hopeless alone, and FAR is ours with 900 of turrets.
            WorldModel ours = MakeWorld(out d, out f, t, patrolDps: 100, hostileDps: 500,
                                        hostileHoldSeconds: 1200, farKm: 12000);
            SpatialNode mine = ours.Graph.Nodes["FAR"];
            mine.StationPrefabId = "Fort";
            mine.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 900, RangeM = 800 } };
            Assert.NotNull(Line(Run(ours, t, d, f), "PATROL_INTERCEPTS"));

            // Identical, except FAR belongs to somebody else.
            WorldModel theirs = MakeWorld(out d, out f, t, patrolDps: 100, hostileDps: 500,
                                          hostileHoldSeconds: 1200, farKm: 12000,
                                          hostileNodeOwner: "OTHER");
            SpatialNode notMine = theirs.Graph.Nodes["FAR"];
            notMine.StationPrefabId = "Fort";
            notMine.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 900, RangeM = 800 } };
            Assert.Null(Line(Run(theirs, t, d, f), "PATROL_INTERCEPTS"));
        }

        [Fact]
        public void CombatStrength_OfDefenceAt_CountsTheStationOnlyForItsOwner()
        {
            FactionTuning t = Tuning();
            t.StationDefensePower = 250;
            WorldModel w = TestSupport.World();
            SpatialNode n = AddNode(w, "N", "DEF", new Vec3(0, 0, 0));
            n.StationPrefabId = "Fort";
            n.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 400, RangeM = 800 } };
            var bands = new List<WeaponBand> { new WeaponBand { Dps = 100, RangeM = 800 } };

            Assert.Equal(750, CombatStrength.OfDefenceAt(bands, n, t, "DEF"));   // 100 + 400 + 250
            Assert.Equal(100, CombatStrength.OfDefenceAt(bands, n, t, "OTHER")); // the hull alone
            Assert.Equal(750, CombatStrength.OfDefence(bands, n, t));            // the Guard reading
        }

        // ---- hysteresis ---------------------------------------------------------------------------

        // A fleet that re-aims every tick reaches nothing. Same bound the Guard redirect uses.
        [Fact]
        public void APatrolAtItsRetargetLimitStaysOnItsCircuit()
        {
            FactionTuning t = Tuning();
            FactionState d, f;
            WorldModel w = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                     hostileHoldSeconds: 1200, farKm: 12000);
            PendingFleetAction p = d.PendingFleets[0];
            p.RetargetCount = t.GuardMaxRetargets;
            d.PendingFleets[0] = p;

            Assert.Null(Line(Run(w, t, d, f), "PATROL_INTERCEPTS"));
        }

        [Fact]
        public void APatrolInsideItsRetargetCooldownStaysOnItsCircuit()
        {
            FactionTuning t = Tuning();
            FactionState d, f;
            WorldModel w = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                     hostileHoldSeconds: 1200, farKm: 12000);
            PendingFleetAction p = d.PendingFleets[0];
            p.LastRetargetUtc = w.UtcNow.AddSeconds(-(t.GuardRetargetCooldownSeconds / 2));
            d.PendingFleets[0] = p;

            Assert.Null(Line(Run(w, t, d, f), "PATROL_INTERCEPTS"));
        }

        // A materialised grid is the host's to move, never this manager's.
        [Fact]
        public void APatrolAPlayerCanSeeIsNotTeleported()
        {
            FactionTuning t = Tuning();
            FactionState d, f;
            WorldModel w = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                     hostileHoldSeconds: 1200, farKm: 12000);
            PendingFleetAction p = d.PendingFleets[0];
            p.MaterialisedEntityId = 12345;
            d.PendingFleets[0] = p;

            Assert.Null(Line(Run(w, t, d, f), "PATROL_INTERCEPTS"));
        }

        // Our own raid holding somewhere is not a target.
        [Fact]
        public void AFactionDoesNotChaseItsOwnDwellingFleet()
        {
            FactionTuning t = Tuning();
            FactionState d, f;
            WorldModel w = MakeWorld(out d, out f, t, patrolDps: 1000, hostileDps: 500,
                                     hostileHoldSeconds: 1200, farKm: 12000);
            PendingFleetAction r = f.PendingFleets[0];
            r.ActorTag = "DEF";
            f.PendingFleets[0] = r;

            Assert.Null(Line(Run(w, t, d, f), "PATROL_INTERCEPTS"));
        }
    }
}
