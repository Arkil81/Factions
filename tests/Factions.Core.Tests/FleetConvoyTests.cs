using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Verifies the PendingFleetAction convoy mechanic:
    ///   - Effects are absent before arrival and applied on arrival.
    ///   - Immediate fast-path (PendingFleetAction.ImmediateItinerary) resolves this tick.
    ///   - Transit time is distance-based when not immediate.
    ///   - Patrol intercepts Raid at the same node (combat resolution).
    ///   - Raid intercepts Trade at the same node (high-chance interception + loot).
    ///   - Triple encounter: Patrol vs Raid resolves first, Raid strength gates Trade escape.
    ///   - Active patrol (arrived, within GuardDuration) blocks a later Raid.
    ///   - Load-time reconciliation: overdue fleets are resolved at ratio=1 on ReconcileAndResolveOnLoad.
    ///   - CreatePending stores graph path; arrival time uses path distance (not straight-line).
    ///   - Mid-transit Raid vs Trade encounter fires at intermediate waypoint node.
    ///   - Mid-transit Patrol vs Raid encounter fires at intermediate waypoint node.
    ///   - Mid-transit triple encounter (Patrol+Raid+Trade all at same intermediate node).
    ///   - LastResolvedSegment prevents the same waypoint encounter from firing twice.
    ///   - ReconcileOnLoad fast-forwards LastResolvedSegment so past segments are not retriggered.
    /// </summary>
    public class FleetConvoyTests
    {
        // ---- helpers -----------------------------------------------------------------------

        private static WorldModel MakeWorld(int seed = 1)
        {
            return TestSupport.World(seed);
        }

        private static FactionState MakeState(string tag, int seed = 1)
            => new FactionState { Tag = tag, RngSeed = seed };

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
                                           double hold = 0.8, double value = 0.6)
        {
            SpatialNode n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        /// <summary>Create a pending fleet directly (skipping CreatePending) for deterministic tests.</summary>
        private static PendingFleetAction MakePending(ActionKind kind, string actor, string targetTag,
            string targetNodeId, DateTime departed, DateTime arrival, long cargo = 3000)
            => new PendingFleetAction
            {
                Kind = kind, ActorTag = actor, TargetTag = targetTag, TargetNodeId = targetNodeId,
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = departed, EstimatedArrivalUtc = arrival,
                CargoValue = cargo
            };

        private static IRandom AlwaysWin()  => new FixedRandom(0.0);  // roll=0 → patrol/raid wins
        private static IRandom AlwaysLose() => new FixedRandom(1.0);  // roll=1 → attacker loses

        /// <summary>A single-band weapon loadout for duel tests — overwhelming Dps at a given range
        /// so the outcome is decisive regardless of the +/-20% jitter <see cref="AlwaysWin"/>/
        /// <see cref="AlwaysLose"/> produce.</summary>
        private static List<WeaponBand> Bands(float dps, float rangeM = 2000)
            => new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = rangeM } };

        // ---- FAC-257: a Patrol that finds trouble stays -------------------------------------

        /// <summary>A Patrol on its arrival tick at <paramref name="nodeId"/>, built the way
        /// FleetManager dispatches one — a Service stop at the target with a Return leg queued
        /// behind it, which is what makes it leave again.</summary>
        private static PendingFleetAction ArrivingPatrol(WorldModel w, string originId, string nodeId,
                                                         List<WeaponBand> bands)
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Patrol,
                ActorTag = "MERC",
                ShipPrefabId = "Gunship",
                ConvoySize = 1,
                OriginNodeId = originId,
                TargetNodeId = nodeId,
                OriginPosKm = new Vec3(0, 0, 0),
                TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow,
                CurrentStopKind = FleetStopKind.Service,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = originId, Kind = FleetStopKind.Return }
                },
                WeaponBands = bands,
                LastResolvedSegment = -1,
            };
        }

        [Fact]
        public void Patrol_ArrivingAtAStillThreatenedNode_StaysAsAGuard()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("MERC");
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.8;
            st.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1", Bands(500)));

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new[] { st }, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Contains("PATROL_STANDS", string.Join("\n", log));
            Assert.Equal(ActionKind.Guard, st.PendingFleets[0].Kind);
            Assert.Null(st.PendingFleets[0].RemainingStops);
            Assert.True(st.PendingFleets[0].IsActiveGuard(w.UtcNow, w.Tuning),
                        "a standing patrol has to satisfy the same predicate the meeting paths ask");
        }

        // The point of the whole ticket: a fleet that stayed must actually fight. A relabelled
        // Patrol with no weapons on record duels at zero Dps, so this is the assertion that
        // separates "became a Guard" from "became a defender".
        [Fact]
        public void PatrolThatStood_IsARealDefender_AndRepelsARaid()
        {
            WorldModel w = MakeWorld();
            var defender = MakeState("MERC");
            var raider   = MakeState("RVN");
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            target.Threat = 0.8;
            defender.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1", Bands(5000)));

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new[] { defender }, w, w.Tuning, AlwaysWin(), log, null);
            Assert.Equal(ActionKind.Guard, defender.PendingFleets[0].Kind);

            // Now send a raid at the node it is standing on.
            var raid = MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", w.UtcNow, w.UtcNow);
            raid.WeaponBands = Bands(1);
            raider.PendingFleets.Add(raid);

            var log2 = new List<string>();
            PendingFleetResolver.ResolveArrivals(new[] { defender, raider }, w, w.Tuning, AlwaysWin(), log2, null);

            // An intercepted fleet is removed by the same pass, so the surviving evidence is the
            // log line and the damage that never happened -- which is the claim worth making anyway.
            Assert.Contains("GUARD_INTERCEPT", string.Join("\n", log2));
            Assert.Empty(raider.PendingFleets);
            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength);
        }

        [Fact]
        public void Patrol_ArrivingAtAPacifiedNode_GoesHomeAsBefore()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("MERC");
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.04;               // the sweep's own -0.05 takes this to zero
            st.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1", Bands(500)));

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new[] { st }, w, w.Tuning, AlwaysWin(), log, null);

            string joined = string.Join("\n", log);
            Assert.Contains("PATROL_AT", joined);
            Assert.DoesNotContain("PATROL_STANDS", joined);
            Assert.Equal(ActionKind.Patrol, st.PendingFleets[0].Kind);
        }

        // A bandless sweep must NOT take the slot. It would duel at zero Dps and, worse, satisfy
        // the one-defender-per-node check that stops a real Guard being dispatched here.
        [Fact]
        public void Patrol_WithNoWeaponsOnRecord_DoesNotStand()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("MERC");
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.8;
            st.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1", new List<WeaponBand>()));

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new[] { st }, w, w.Tuning, AlwaysWin(), log, null);

            Assert.DoesNotContain("PATROL_STANDS", string.Join("\n", log));
            Assert.Equal(ActionKind.Patrol, st.PendingFleets[0].Kind);
        }

        [Fact]
        public void Patrol_AtANodeAlreadyCovered_DoesNotStandASecondDefender()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("MERC");
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.8;
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = w.UtcNow.AddMinutes(-5), EstimatedArrivalUtc = w.UtcNow.AddMinutes(-5),
                Resolved = true,
            });
            st.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1", Bands(500)));

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new[] { st }, w, w.Tuning, AlwaysWin(), log, null);

            Assert.DoesNotContain("PATROL_STANDS", string.Join("\n", log));
            Assert.Equal(ActionKind.Patrol, st.PendingFleets[1].Kind);
        }

        // The hull must be credited once, by the Guard duty window, at the node it defended --
        // not twice, and not by the Patrol return leg it no longer has.
        [Fact]
        public void PatrolThatStood_CreditsItsHullOnceWhenTheDutyWindowExpires()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("MERC");
            AddNode(w, "HOME", "MERC", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0));
            target.Threat = 0.8;
            st.PendingFleets.Add(ArrivingPatrol(w, "HOME", "MERC1", Bands(500)));

            PendingFleetResolver.ResolveArrivals(new[] { st }, w, w.Tuning, AlwaysWin(), new List<string>(), null);
            int stockOnArrival;
            target.ShipStock.TryGetValue("Gunship", out stockOnArrival);
            Assert.Equal(0, stockOnArrival);     // still flying its duty: nothing credited yet

            w.UtcNow = w.UtcNow.Add(w.Tuning.GuardDuration).AddMinutes(1);
            PendingFleetResolver.ResolveArrivals(new[] { st }, w, w.Tuning, AlwaysWin(), new List<string>(), null);

            Assert.Equal(1, target.ShipStock["Gunship"]);
            Assert.Empty(st.PendingFleets);
            int atHome;
            w.Graph.Nodes["HOME"].ShipStock.TryGetValue("Gunship", out atHome);
            Assert.Equal(0, atHome);             // credited once, at the node it defended
        }

        private static FactionTuning SlowFleetTuning()
        {
            var t = TestSupport.Tuning();
            t.FleetSpeedKmPerMin = 1.0;
            t.FleetSpeedMinKmPerMin = 0.999;
            t.FleetAccelKmPerMin2 = 1000000.0;
            return t;
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private sealed class FakePriceCatalogue : IItemPriceCatalogue
        {
            public readonly Dictionary<string, long> Prices = new Dictionary<string, long>();
            public long BasePrice(string subTypeId)
            {
                long v;
                return Prices.TryGetValue(subTypeId, out v) ? v : 0;
            }
        }

        private sealed class FixedVolumeCatalogue : IItemVolumeCatalogue
        {
            private readonly double _vol;
            public FixedVolumeCatalogue(double vol) { _vol = vol; }
            public double VolumeLitersPerUnit(string invKey) { return _vol; }
        }

        private sealed class FakeMassCatalogue : IItemMassCatalogue
        {
            private readonly Dictionary<string, double> _massByKey = new Dictionary<string, double>();
            public FakeMassCatalogue Set(string invKey, double kgPerUnit) { _massByKey[invKey] = kgPerUnit; return this; }
            public double MassKgPerUnit(string invKey)
            {
                double v;
                return _massByKey.TryGetValue(invKey, out v) ? v : 0;
            }
        }

        /// <summary>Same shape as OperationsManagerTests's identically-named fake — full-effectiveness-
        /// everywhere Hydrogen thrust so a test can exercise the mass-trim ceiling without also
        /// tripping the (unrelated) dry-TWR route gate.</summary>
        private sealed class FakePrefabCatalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, double> _dryMassKgByPrefab = new Dictionary<string, double>();
            private readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> _thrustBandsByPrefab
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>();

            public void SetHydrogenDown(string prefabId, double dryMassKg, float forceN)
            {
                _dryMassKgByPrefab[prefabId] = dryMassKg;
                _thrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                    {
                        ThrusterType = "Hydrogen", ForceMagnitudeN = forceN,
                        MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 0f,
                        EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f,
                        NeedsAtmosphereForInfluence = false,
                    }},
                };
            }

            public long CargoLiters(string prefabId) { return 1_000_000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }

            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands;
                return _thrustBandsByPrefab.TryGetValue(prefabId, out bands)
                    ? bands : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }

            public double DryMassKg(string prefabId)
            {
                double v;
                return _dryMassKgByPrefab.TryGetValue(prefabId, out v) ? v : 0;
            }

            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private sealed class ConstantGravityCatalogue : IPlanetCatalogue
        {
            private readonly double _g;
            public ConstantGravityCatalogue(double g) { _g = g; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        // ---- sub-task 1/3: effects absent before arrival, applied at arrival ----------------

        [Fact]
        public void Raid_EffectAbsentBeforeArrival_AppliedOnArrival()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("A");
            w.SetTreasury("A", 100000);
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);

            // Fleet departs now, arrives 10 ticks later
            DateTime departed = w.UtcNow;
            DateTime arrival  = departed.AddMinutes(10);
            st.PendingFleets.Add(MakePending(ActionKind.Raid, "A", "B", "B1", departed, arrival));

            var states = new List<FactionState> { st, MakeState("B") };
            var log    = new List<string>();

            // Tick before arrival — no effect
            w.UtcNow = departed.AddMinutes(5);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);
            Assert.Equal(0.8, target.HoldStrength);  // untouched
            Assert.Single(st.PendingFleets);          // still in flight

            // Tick of arrival — effect applied
            w.UtcNow = arrival;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);
            Assert.True(target.HoldStrength < 0.8, "hold should drop on raid arrival");
            Assert.Empty(st.PendingFleets);           // cleaned up after resolution
        }

        [Fact]
        public void Trade_EffectAbsentBeforeArrival_AppliedOnArrival()
        {
            WorldModel w = MakeWorld();
            var stA = MakeState("A"); var stB = MakeState("B");
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            DateTime departed = w.UtcNow;
            DateTime arrival  = departed.AddMinutes(10);
            stA.PendingFleets.Add(MakePending(ActionKind.Trade, "A", "B", "B1", departed, arrival));

            var states = new List<FactionState> { stA, stB };
            var log    = new List<string>();

            double relBefore = stA.RelationTo("B");

            w.UtcNow = departed.AddMinutes(5);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);
            Assert.Equal(relBefore, stA.RelationTo("B")); // no change yet

            w.UtcNow = arrival;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);
            Assert.True(stA.RelationTo("B") > relBefore, "trade should lift relation on arrival");
            Assert.Empty(stA.PendingFleets);
        }

        [Fact]
        public void Trade_PaysSupplierAndChargesRecipient()
        {
            WorldModel w = MakeWorld();
            var supplier  = MakeState("A");
            var recipient = MakeState("B");
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));
            w.SetTreasury("A", 10000);
            w.SetTreasury("B", 5000);

            DateTime departed = w.UtcNow;
            supplier.PendingFleets.Add(MakePending(ActionKind.Trade, "A", "B", "B1", departed, departed, cargo: 4000));

            var states = new List<FactionState> { supplier, recipient };
            var log    = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Contains("TRADE_RESOLVED", string.Join("\n", log));
            Assert.Equal(14000, w.GetTreasury("A")); // supplier paid: +4000
            Assert.Equal(1000, w.GetTreasury("B"));  // recipient charged: -4000
        }

        [Fact]
        public void Trade_RecipientCanGoNegative()
        {
            WorldModel w = MakeWorld();
            var supplier  = MakeState("A");
            var recipient = MakeState("B");
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));
            w.SetTreasury("A", 0);
            w.SetTreasury("B", 1000); // not enough to cover the trade

            DateTime departed = w.UtcNow;
            supplier.PendingFleets.Add(MakePending(ActionKind.Trade, "A", "B", "B1", departed, departed, cargo: 4000));

            var states = new List<FactionState> { supplier, recipient };
            var log    = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal(4000, w.GetTreasury("A"));
            Assert.Equal(-3000, w.GetTreasury("B"));
        }

        // ---- sub-task 4: NullSpawner immediate fast-path -----------------------------------

        [Fact]
        public void ImmediateFastPath_ResolvesThisTick()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("A");
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            // EstimatedArrivalUtc == DepartedUtc → CompletionRatio = 1.0, HasArrived immediately
            st.PendingFleets.Add(MakePending(ActionKind.Raid, "A", "B", "B1", now, now));

            var states = new List<FactionState> { st, MakeState("B") };
            var log    = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.True(target.HoldStrength < 0.8, "immediate fast-path should apply raid effect this tick");
            Assert.Empty(st.PendingFleets);
        }

        // ---- sub-task 1: CreatePending factory sets transit time from distance/speed --------

        [Fact]
        public void CreatePending_TransitTimeBasedOnDistance()
        {
            var t = SlowFleetTuning();
            WorldModel w = MakeWorld();
            w.Tuning = t;
            AddNode(w, "A1", "A", new Vec3(0, 0, 0), hold: 0.9);
            AddNode(w, "B1", "B", new Vec3(100, 0, 0), hold: 0.9); // 100 km away

            var st = MakeState("A");
            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 12000 };

            PendingFleetAction fleet = PendingFleetResolver.CreatePending(ActionKind.Raid, "A", intent, w, t, immediate: false);

            // Distance = 100 km, speed = 1 km/min → 100 minutes transit
            double transitMins = (fleet.EstimatedArrivalUtc - fleet.DepartedUtc).TotalMinutes;
            Assert.Equal(100.0, transitMins, 1);
        }

        [Fact]
        public void CreatePending_Immediate_ZeroTransit()
        {
            var t = SlowFleetTuning();
            WorldModel w = MakeWorld();
            w.Tuning = t;
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(100, 0, 0));

            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 0 };
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(ActionKind.Raid, "A", intent, w, t, immediate: true);

            Assert.Equal(fleet.DepartedUtc, fleet.EstimatedArrivalUtc);
            Assert.Equal(1.0, fleet.CompletionRatio(w.UtcNow), 8);
        }

        [Fact]
        public void CreatePending_ReturnableMission_AppendsTypedReturnStop()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(100, 0, 0));
            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 0 };

            PendingFleetAction fleet = PendingFleetResolver.CreatePending(
                ActionKind.Raid, "A", intent, w, w.Tuning, immediate: true);

            Assert.Equal(FleetStopKind.Service, fleet.CurrentStopKind);
            Assert.Single(fleet.RemainingStops);
            Assert.Equal("A1", fleet.RemainingStops[0].NodeId);
            Assert.Equal(FleetStopKind.Return, fleet.RemainingStops[0].Kind);
        }

        [Fact]
        public void CreatePending_PlanetBlockedRoute_QueuesNavigationStopsBeforeService()
        {
            var tuning = SlowFleetTuning();
            WorldModel w = MakeWorld();
            w.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 50 },
            });
            AddNode(w, "A1", "A", new Vec3(-200, 0, 0));
            AddNode(w, "B1", "B", new Vec3(200, 0, 0));
            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 0 };

            PendingFleetAction fleet = PendingFleetResolver.CreatePending(
                ActionKind.Raid, "A", intent, w, tuning, immediate: false);

            // A raid's tail is Service -> Engage x RaidSiegeCycles -> Return: the clearance chain is
            // followed by the arrival at the target, the siege held there, and only then the trip
            // home. Read off the tuning rather than hardcoded, so a siege length that stops reaching
            // the itinerary fails here instead of silently shortening the tail.
            int cycles = tuning.RaidSiegeCycles;
            int tail = 2 + cycles;
            int n = fleet.RemainingStops.Count;
            Assert.Equal(FleetStopKind.Navigation, fleet.CurrentStopKind);
            Assert.Null(fleet.TargetNodeId);
            Assert.True(n >= tail + 1);
            Assert.All(fleet.RemainingStops.GetRange(0, n - tail), stop =>
            {
                Assert.Equal(FleetStopKind.Navigation, stop.Kind);
                Assert.True(stop.HasPosition);
                Assert.Null(stop.NodeId);
            });
            Assert.Equal("B1", fleet.RemainingStops[n - tail].NodeId);
            Assert.Equal(FleetStopKind.Service, fleet.RemainingStops[n - tail].Kind);
            for (int c = 1; c <= cycles; c++)
            {
                Assert.Equal("B1", fleet.RemainingStops[n - tail + c].NodeId);
                Assert.Equal(FleetStopKind.Engage, fleet.RemainingStops[n - tail + c].Kind);
            }
            Assert.Equal(tuning.RaidDwellSeconds, fleet.RemainingStops[n - 2].DurationSeconds);
            Assert.Equal("A1", fleet.RemainingStops[n - 1].NodeId);
            Assert.Equal(FleetStopKind.Return, fleet.RemainingStops[n - 1].Kind);
        }

        [Fact]
        public void PlanetNavigationStops_DoNotResolveMissionAndReturnLegReroutes()
        {
            var tuning = SlowFleetTuning();
            WorldModel w = MakeWorld();
            w.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 50 },
            });
            AddNode(w, "A1", "A", new Vec3(-200, 0, 0));
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(200, 0, 0), hold: 0.8);
            var actor = MakeState("A");
            var states = new List<FactionState> { actor, MakeState("B") };
            var log = new List<string>();
            DateTime dispatched = w.UtcNow;

            actor.PendingFleets.Add(PendingFleetResolver.CreatePending(ActionKind.Raid, "A",
                new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 0 },
                w, tuning, immediate: false));

            while (actor.PendingFleets[0].CurrentStopKind == FleetStopKind.Navigation)
            {
                w.UtcNow = actor.PendingFleets[0].EstimatedArrivalUtc;
                PendingFleetResolver.ResolveArrivals(states, w, tuning, AlwaysWin(), log, null);
                Assert.Equal(0.8, target.HoldStrength);
            }

            PendingFleetAction service = actor.PendingFleets[0];
            Assert.True((service.EstimatedArrivalUtc - dispatched).TotalMinutes
                > tuning.TransitTimeMins(400), "the routed service ETA must exceed the direct route ETA");
            w.UtcNow = service.EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, tuning, AlwaysWin(), log, null);

            // Reaching the target is contact, not resolution: the raid holds for its engagement
            // dwell and the node is untouched for the whole of it.
            PendingFleetAction engaging = actor.PendingFleets[0];
            Assert.Equal(FleetStopKind.Engage, engaging.CurrentStopKind);
            Assert.Equal(0.8, target.HoldStrength);
            Assert.Equal(tuning.RaidDwellSeconds,
                (engaging.EstimatedArrivalUtc - engaging.DepartedUtc).TotalSeconds, 3);

            w.UtcNow = engaging.EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, tuning, AlwaysWin(), log, null);
            Assert.True(target.HoldStrength < 0.8, "the raid resolves only after its engagement dwell ends");

            // The siege holds for its remaining dwells before the return leg is planned, and it is
            // the return leg -- not the dwell -- that reroutes around the planet.
            int guard = 0;
            while (actor.PendingFleets[0].CurrentStopKind == FleetStopKind.Engage && guard++ < 16)
            {
                w.UtcNow = actor.PendingFleets[0].EstimatedArrivalUtc;
                PendingFleetResolver.ResolveArrivals(states, w, tuning, AlwaysWin(), log, null);
            }

            Assert.Single(actor.PendingFleets);
            Assert.Equal(FleetStopKind.Navigation, actor.PendingFleets[0].CurrentStopKind);
            Assert.Null(actor.PendingFleets[0].TargetNodeId);
        }

        [Fact]
        public void PlanetNavigationStops_ShareOneAccelerationProfileAcrossTheWholeArc()
        {
            var tuning = SlowFleetTuning();
            WorldModel w = MakeWorld();
            w.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 50 },
            });
            AddNode(w, "A1", "A", new Vec3(-200, 0, 0));
            AddNode(w, "B1", "B", new Vec3(200, 0, 0), hold: 0.8);
            var actor = MakeState("A");
            var states = new List<FactionState> { actor, MakeState("B") };
            var log = new List<string>();
            DateTime dispatched = w.UtcNow;

            actor.PendingFleets.Add(PendingFleetResolver.CreatePending(ActionKind.Raid, "A",
                new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 0 },
                w, tuning, immediate: false));

            PendingFleetAction dispatchedFleet = actor.PendingFleets[0];
            Assert.Equal(FleetStopKind.Navigation, dispatchedFleet.CurrentStopKind);
            double routeKm = dispatchedFleet.RouteDistanceKm;
            Assert.True(routeKm > 0, "a routed fleet carries the polyline it is flying");
            int legs = 1;

            while (actor.PendingFleets[0].CurrentStopKind == FleetStopKind.Navigation)
            {
                w.UtcNow = actor.PendingFleets[0].EstimatedArrivalUtc;
                PendingFleetResolver.ResolveArrivals(states, w, tuning, AlwaysWin(), log, null);
                legs++;
            }

            // The whole detour costs one accelerate/cruise/decelerate profile over the routed length.
            // Billing each arc chord its own trapezoid would multiply this by the number of legs.
            double flownMins = (actor.PendingFleets[0].EstimatedArrivalUtc - dispatched).TotalMinutes;
            Assert.True(legs >= 4, "expected a sampled arc, got " + legs + " legs");
            Assert.Equal(tuning.TransitTimeMins(routeKm), flownMins, 6);
            Assert.True(flownMins < legs * tuning.TransitTimeMins(routeKm / legs),
                "a chained route must beat per-leg cold starts");
        }

        [Fact]
        public void PlanetNavigationStops_SurviveABrainRoundTripMidArc()
        {
            var tuning = SlowFleetTuning();
            WorldModel w = MakeWorld();
            w.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 50 },
            });
            AddNode(w, "A1", "A", new Vec3(-200, 0, 0));
            AddNode(w, "B1", "B", new Vec3(200, 0, 0), hold: 0.8);
            var actor = MakeState("A");

            actor.PendingFleets.Add(PendingFleetResolver.CreatePending(ActionKind.Raid, "A",
                new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 0 },
                w, tuning, immediate: false));
            PendingFleetAction before = actor.PendingFleets[0];

            BrainDoc doc = FactionStore.ToBrainDoc(actor, new SpatialNode[0], 10);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(doc);
            FactionState reloaded = FactionStore.ApplyBrainDoc(
                Newtonsoft.Json.JsonConvert.DeserializeObject<BrainDoc>(json), w.Graph);
            PendingFleetAction after = reloaded.PendingFleets[0];

            // A clearance stop carries no Pickup/Mine payload; those fields must not reach the file.
            // A routed detour writes many of these stops, so the dead weight would compound.
            foreach (Newtonsoft.Json.Linq.JToken stop in
                     Newtonsoft.Json.Linq.JObject.Parse(json).SelectTokens("$..RemainingStops[*]"))
            {
                if ((string)stop["Kind"] != "Navigation") continue;
                Assert.Null(stop["PickupItemKey"]);
                Assert.Null(stop["PickupQuantity"]);
                Assert.Null(stop["DurationSeconds"]);
                Assert.Null(stop["MineWeights"]);
                Assert.Null(stop["MineTargetLiters"]);
                Assert.Null(stop["NodeId"]);
            }

            Assert.Equal(before.RemainingStops.Count, after.RemainingStops.Count);
            Assert.Equal(before.RouteDistanceKm, after.RouteDistanceKm, 6);
            Assert.Equal(before.RouteCoveredKm, after.RouteCoveredKm, 6);
            Assert.Equal(before.RouteDepartedUtc, after.RouteDepartedUtc);
            for (int i = 0; i < before.RemainingStops.Count; i++)
            {
                Assert.Equal(before.RemainingStops[i].Kind, after.RemainingStops[i].Kind);
                Assert.Equal(before.RemainingStops[i].HasPosition, after.RemainingStops[i].HasPosition);
                Assert.Equal(before.RemainingStops[i].PositionKm.ToString(),
                             after.RemainingStops[i].PositionKm.ToString());
            }
        }

        [Fact]
        public void Trade_ServiceStop_DefersUnboughtCargoUntilReturnStop()
        {
            WorldModel w = MakeWorld();
            w.Tuning.InsolvencyFloor = 1; // recipient treasury below floor: nothing can sell
            var supplier = MakeState("A");
            var recipient = MakeState("B");
            SpatialNode origin = AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            SpatialNode destination = AddNode(w, "B1", "B", new Vec3(10, 0, 0));
            w.SetTreasury("B", 0);

            PendingFleetAction fleet = MakePending(ActionKind.Trade, "A", "B", "B1", w.UtcNow, w.UtcNow);
            fleet.OriginNodeId = "A1";
            fleet.CurrentStopKind = FleetStopKind.Service;
            fleet.RemainingStops = new List<FleetStop>
            {
                new FleetStop { NodeId = "A1", Kind = FleetStopKind.Return }
            };
            fleet.Cargo = new Dictionary<string, long> { { "SteelPlate", 10 } };
            supplier.PendingFleets.Add(fleet);
            var prices = new FakePriceCatalogue();
            prices.Prices["SteelPlate"] = 100;
            var states = new List<FactionState> { supplier, recipient };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null, null, prices);

            Assert.Single(supplier.PendingFleets);
            PendingFleetAction returning = supplier.PendingFleets[0];
            Assert.Equal("A1", returning.TargetNodeId);
            Assert.Equal(FleetStopKind.Return, returning.CurrentStopKind);
            Assert.Equal(10, returning.Cargo["SteelPlate"]);
            Assert.False(destination.Inventory.ContainsKey("SteelPlate"));
            Assert.False(origin.Inventory.ContainsKey("SteelPlate"));

            // The Return leg is flown, not teleported: it is billed B1 -> A1 like any other.
            w.UtcNow = returning.EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null, null, prices);

            Assert.Empty(supplier.PendingFleets);
            Assert.Equal(10, origin.Inventory["SteelPlate"]);
            Assert.Contains("FLEET_RETURNED", string.Join("\n", log));
        }

        // ---- Mine arrival: credits destination inventory the same way Haul does -----------------

        [Fact]
        public void Mine_Resolves_CreditsDestinationInventory_SameAsHaul()
        {
            WorldModel w = MakeWorld();
            var actor = MakeState("A");
            AddNode(w, "OreField1", "A", new Vec3(0, 0, 0));
            SpatialNode dest = AddNode(w, "Refinery1", "A", new Vec3(10, 0, 0));

            PendingFleetAction fleet = MakePending(ActionKind.Mine, "A", null, "Refinery1", w.UtcNow, w.UtcNow);
            fleet.OriginNodeId = "OreField1";
            fleet.Cargo = new Dictionary<string, long> { ["Ore:Iron"] = 250 };
            actor.PendingFleets.Add(fleet);
            var states = new List<FactionState> { actor };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal(250, dest.Inventory["Ore:Iron"]);
            Assert.Empty(actor.PendingFleets);
            Assert.Contains(log, l => l.Contains("MINE_RESOLVED") && l.Contains("delivered=250"));
        }

        [Fact]
        public void PlanFleetLeg_RoutesAMineTravelLeg_WhichIsDispatchedUnderANavigationStopKind()
        {
            var tuning = SlowFleetTuning();
            WorldModel w = MakeWorld();
            w.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 50 },
            });
            AddNode(w, "A1", "A", new Vec3(-200, 0, 0));
            SpatialNode source = AddNode(w, "OreField1", "A", new Vec3(200, 0, 0));

            // The shape OperationsManager builds: travel to the mine source under a Navigation stop
            // kind, but with a real target node — that is a leg needing a plan, not a planned one.
            var mine = new PendingFleetAction
            {
                Kind = ActionKind.Mine,
                ActorTag = "A",
                OriginNodeId = "A1",
                TargetNodeId = source.Id,
                OriginPosKm = new Vec3(-200, 0, 0),
                TargetPosKm = source.Position,
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(tuning.TransitTimeMins(400)),
                CurrentStopKind = FleetStopKind.Navigation,
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { NodeId = source.Id, Kind = FleetStopKind.Mine, DurationSeconds = 60 },
                },
                FleetId = "MINE01",
                LastResolvedSegment = -1,
            };

            PendingFleetAction routed = PendingFleetResolver.PlanFleetLeg(mine, w, tuning, false);

            Assert.Null(routed.TargetNodeId);
            Assert.True(routed.RouteDistanceKm > 400, "the routed leg must be longer than the blocked straight line");
            Assert.True(routed.RemainingStops.Count >= 3);
            // The re-queued travel stop keeps its node, and the Mine dwell still follows it.
            FleetStop travel = routed.RemainingStops[routed.RemainingStops.Count - 2];
            Assert.Equal(FleetStopKind.Navigation, travel.Kind);
            Assert.Equal(source.Id, travel.NodeId);
            Assert.Equal(FleetStopKind.Mine, routed.RemainingStops[routed.RemainingStops.Count - 1].Kind);
        }

        [Fact]
        public void PlanFleetLeg_RoutedPickupLeg_KeepsItsPickupPayload()
        {
            var tuning = SlowFleetTuning();
            WorldModel w = MakeWorld();
            w.Planets = new StaticPlanetCatalogue(new[]
            {
                new PlanetRecord { Id = "P", Center = new Vec3(0, 0, 0), SurfaceRadius = 50 },
            });
            AddNode(w, "A1", "A", new Vec3(-200, 0, 0));
            SpatialNode pickup = AddNode(w, "P1", "A", new Vec3(200, 0, 0));

            var fleet = new PendingFleetAction
            {
                Kind = ActionKind.Trade,
                ActorTag = "A",
                OriginNodeId = "A1",
                TargetNodeId = pickup.Id,
                OriginPosKm = new Vec3(-200, 0, 0),
                TargetPosKm = pickup.Position,
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(tuning.TransitTimeMins(400)),
                CurrentStopKind = FleetStopKind.Pickup,
                PickupItemKey = "Ore:Iron",
                PickupQuantity = 500,
                FleetId = "PICK01",
                LastResolvedSegment = -1,
            };

            PendingFleetAction routed = PendingFleetResolver.PlanFleetLeg(fleet, w, tuning, false);

            Assert.Equal(FleetStopKind.Navigation, routed.CurrentStopKind);
            FleetStop requeued = routed.RemainingStops[routed.RemainingStops.Count - 1];
            Assert.Equal(FleetStopKind.Pickup, requeued.Kind);
            Assert.Equal("Ore:Iron", requeued.PickupItemKey);
            Assert.Equal(500, requeued.PickupQuantity);
        }

        // ---- Pickup stops: load cargo on arrival, then continue (Haul's ship-fetch fallback) ----

        [Fact]
        public void Pickup_LoadsCargoOnArrival_ThenDeliversOnNextStop()
        {
            WorldModel w = MakeWorld();
            var actor = MakeState("A");
            SpatialNode pickup = AddNode(w, "P1", "A", new Vec3(0, 0, 0));
            SpatialNode dest = AddNode(w, "D1", "A", new Vec3(10, 0, 0));
            pickup.Inventory["Ore"] = 500;

            PendingFleetAction fleet = MakePending(ActionKind.Haul, "A", null, "P1", w.UtcNow, w.UtcNow);
            fleet.OriginNodeId = "P1";
            fleet.CurrentStopKind = FleetStopKind.Pickup;
            fleet.PickupItemKey = "Ore";
            fleet.PickupQuantity = 100;
            fleet.RemainingStops = new List<FleetStop>
            {
                new FleetStop { NodeId = "D1", Kind = FleetStopKind.Service }
            };
            actor.PendingFleets.Add(fleet);
            var states = new List<FactionState> { actor };
            var log = new List<string>();

            // Call 1: the pickup leg resolves — cargo loads, node debits, convoy re-departs toward D1.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal(400, pickup.Inventory["Ore"]);
            Assert.Single(actor.PendingFleets);
            PendingFleetAction inTransit = actor.PendingFleets[0];
            Assert.False(inTransit.Resolved);
            Assert.Equal(100, inTransit.Cargo["Ore"]);
            Assert.Equal("D1", inTransit.TargetNodeId);
            Assert.Equal(FleetStopKind.Service, inTransit.CurrentStopKind);
            Assert.Null(inTransit.PickupItemKey);
            Assert.Contains(log, l => l.Contains("HAUL_PICKUP"));

            // Call 2: the delivery leg resolves — cargo credits D1, convoy has no more stops.
            // P1 -> D1 is a real leg with a real transit time, so the clock has to reach it.
            w.UtcNow = inTransit.EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal(100, dest.Inventory["Ore"]);
            Assert.Empty(actor.PendingFleets);
        }

        // A consolidated convoy's stops at one node are collected in one visit. Flying them as
        // separate legs bills each a zero-distance leg, and such a leg arrives the instant it
        // departs -- which is what left the host's parked hull with no window to be re-aimed in.
        [Fact]
        public void Pickup_ConsecutiveStopsAtSameNode_CollectedInOneVisit_ThenLeavesOnARealLeg()
        {
            WorldModel w = MakeWorld();
            var actor = MakeState("A");
            SpatialNode pickup = AddNode(w, "P1", "A", new Vec3(0, 0, 0));
            AddNode(w, "D1", "A", new Vec3(10, 0, 0));
            pickup.Inventory["Ore:Iron"] = 500;
            pickup.Inventory["Ore:Nickel"] = 300;
            pickup.Inventory["Ore:Cobalt"] = 200;

            PendingFleetAction fleet = MakePending(ActionKind.Haul, "A", null, "P1", w.UtcNow, w.UtcNow);
            fleet.OriginNodeId = "P1";
            fleet.CurrentStopKind = FleetStopKind.Pickup;
            fleet.PickupItemKey = "Ore:Iron";
            fleet.PickupQuantity = 100;
            fleet.RemainingStops = new List<FleetStop>
            {
                new FleetStop { NodeId = "P1", Kind = FleetStopKind.Pickup, PickupItemKey = "Ore:Nickel", PickupQuantity = 50 },
                new FleetStop { NodeId = "P1", Kind = FleetStopKind.Pickup, PickupItemKey = "Ore:Cobalt", PickupQuantity = 25 },
                new FleetStop { NodeId = "D1", Kind = FleetStopKind.Service },
            };
            actor.PendingFleets.Add(fleet);
            var states = new List<FactionState> { actor };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            PendingFleetAction departing = actor.PendingFleets[0];
            Assert.Equal(100, departing.Cargo["Ore:Iron"]);
            Assert.Equal(50, departing.Cargo["Ore:Nickel"]);
            Assert.Equal(25, departing.Cargo["Ore:Cobalt"]);
            Assert.Equal(400, pickup.Inventory["Ore:Iron"]);
            Assert.Equal(3, log.Count(l => l.Contains("HAUL_PICKUP")));

            // One visit, then away: the convoy leaves on the delivery leg, not on another stop at
            // the node it is standing on, and that leg is billed real transit time.
            Assert.Equal(FleetStopKind.Service, departing.CurrentStopKind);
            Assert.Equal("D1", departing.TargetNodeId);
            Assert.Empty(departing.RemainingStops);
            Assert.True(departing.EstimatedArrivalUtc > departing.DepartedUtc);
        }

        // Two distinct nodes CAN sit at one position, so a zero-length leg survives the merge above.
        // It must stay a property of that leg: reading it back as "this fleet resolves instantly"
        // turned every later leg of a real itinerary into teleportation.
        [Fact]
        public void ZeroLengthLeg_DoesNotMakeTheRestOfTheItineraryImmediate()
        {
            WorldModel w = MakeWorld();
            var actor = MakeState("A");
            SpatialNode first = AddNode(w, "P1", "A", new Vec3(0, 0, 0));
            SpatialNode second = AddNode(w, "P2", "A", new Vec3(0, 0, 0)); // co-located, different node
            AddNode(w, "D1", "A", new Vec3(10, 0, 0));
            first.Inventory["Ore:Iron"] = 500;
            second.Inventory["Ore:Nickel"] = 500;

            PendingFleetAction fleet = MakePending(ActionKind.Haul, "A", null, "P1", w.UtcNow, w.UtcNow);
            fleet.OriginNodeId = "P1";
            fleet.CurrentStopKind = FleetStopKind.Pickup;
            fleet.PickupItemKey = "Ore:Iron";
            fleet.PickupQuantity = 100;
            fleet.RemainingStops = new List<FleetStop>
            {
                new FleetStop { NodeId = "P2", Kind = FleetStopKind.Pickup, PickupItemKey = "Ore:Nickel", PickupQuantity = 50 },
                new FleetStop { NodeId = "D1", Kind = FleetStopKind.Service },
            };
            actor.PendingFleets.Add(fleet);
            var states = new List<FactionState> { actor };
            var log = new List<string>();

            // P1 -> P2 covers no distance, so it legitimately arrives the moment it departs.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);
            PendingFleetAction atSecond = actor.PendingFleets[0];
            Assert.Equal(FleetStopKind.Pickup, atSecond.CurrentStopKind);
            Assert.Equal("P2", atSecond.TargetNodeId);
            Assert.Equal(atSecond.DepartedUtc, atSecond.EstimatedArrivalUtc);

            // P2 -> D1 is 10 km and must be billed as such.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);
            PendingFleetAction toDest = actor.PendingFleets[0];
            Assert.Equal(FleetStopKind.Service, toDest.CurrentStopKind);
            Assert.Equal("D1", toDest.TargetNodeId);
            Assert.True(toDest.EstimatedArrivalUtc > toDest.DepartedUtc);
        }

        [Fact]
        public void Pickup_InsufficientInventory_LoadsWhateverIsAvailableAndContinues()
        {
            WorldModel w = MakeWorld();
            var actor = MakeState("A");
            SpatialNode pickup = AddNode(w, "P1", "A", new Vec3(0, 0, 0));
            AddNode(w, "D1", "A", new Vec3(10, 0, 0));
            pickup.Inventory["Ore"] = 30; // less than the requested 100

            PendingFleetAction fleet = MakePending(ActionKind.Haul, "A", null, "P1", w.UtcNow, w.UtcNow);
            fleet.OriginNodeId = "P1";
            fleet.CurrentStopKind = FleetStopKind.Pickup;
            fleet.PickupItemKey = "Ore";
            fleet.PickupQuantity = 100;
            fleet.RemainingStops = new List<FleetStop>
            {
                new FleetStop { NodeId = "D1", Kind = FleetStopKind.Service }
            };
            actor.PendingFleets.Add(fleet);
            var states = new List<FactionState> { actor };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal(0, pickup.Inventory["Ore"]);
            PendingFleetAction inTransit = actor.PendingFleets[0];
            Assert.False(inTransit.Resolved);
            Assert.Equal(30, inTransit.Cargo["Ore"]);
        }

        [Fact]
        public void Pickup_MassCapsLoadedQuantity_LogsCargoMassTrimmed()
        {
            // Same fixture shape as DispatchHaul_ManifestOverloadsTwr_TrimsQuantity_LogsCargoMassTrimmed
            // (OperationsManagerTests.cs) — proves the shared PendingFleetResolver.LoadCargo helper
            // caps identically whether it's called at dispatch time or at a later Pickup stop.
            WorldModel w = MakeWorld();
            w.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cat = new FakePrefabCatalogue();
            cat.SetHydrogenDown("Hauler", dryMassKg: 1000, forceN: 15000);
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(1.0);

            var actor = MakeState("A");
            SpatialNode pickup = AddNode(w, "P1", "A", new Vec3(0, 0, 0));
            AddNode(w, "D1", "A", new Vec3(10, 0, 0));
            pickup.Inventory["Ore"] = 1000;

            PendingFleetAction fleet = MakePending(ActionKind.Haul, "A", null, "P1", w.UtcNow, w.UtcNow);
            fleet.OriginNodeId = "P1";
            fleet.ShipPrefabId = "Hauler";
            fleet.CurrentStopKind = FleetStopKind.Pickup;
            fleet.PickupItemKey = "Ore";
            fleet.PickupQuantity = 100; // 100 * 10kg = 1000kg — way over the ~456kg ceiling
            fleet.RemainingStops = new List<FleetStop>
            {
                new FleetStop { NodeId = "D1", Kind = FleetStopKind.Service }
            };
            actor.PendingFleets.Add(fleet);
            var states = new List<FactionState> { actor };
            var masses = new FakeMassCatalogue().Set("Ore", 10);
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, itemMasses: masses);

            Assert.Equal(45, actor.PendingFleets[0].Cargo["Ore"]);
            Assert.Contains(log, l => l.Contains("cargo_mass_trimmed"));
        }

        // ---- overrideOriginNodeId: origin selection must not silently re-derive its own -----
        // (regression coverage for the OffensiveDispatch.FindBestOriginNode ship-stock-aware fix —
        // once the host starts passing an explicit ship-stocked origin for Raid/Retaliate, that
        // origin must be honored verbatim, not overridden by CreatePending's own nearest-node
        // fallback loop.)

        [Fact]
        public void CreatePending_Raid_UsesOverrideOriginNode_NotNearestNode()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));   // nearest to target
            AddNode(w, "A2", "A", new Vec3(500, 0, 0)); // far from target, but stock lives here
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 12000 };
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(
                ActionKind.Raid, "A", intent, w, w.Tuning, immediate: true,
                overrideOriginNodeId: "A2");

            Assert.Equal("A2", fleet.OriginNodeId);
        }

        [Fact]
        public void CreatePending_Raid_NoOverride_FallsBackToNearestOwnedNode()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));   // nearest to target
            AddNode(w, "A2", "A", new Vec3(500, 0, 0)); // far from target
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 12000 };
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(
                ActionKind.Raid, "A", intent, w, w.Tuning, immediate: true);

            Assert.Equal("A1", fleet.OriginNodeId);
        }

        [Fact]
        public void CreatePending_Retaliate_UsesOverrideOriginNode()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "A2", "A", new Vec3(500, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            var intent = new FactionIntent { Kind = ActionKind.Retaliate, TargetNodeId = "B1", Cost = 12000 };
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(
                ActionKind.Retaliate, "A", intent, w, w.Tuning, immediate: true,
                overrideOriginNodeId: "A2");

            Assert.Equal("A2", fleet.OriginNodeId);
        }

        // ---- Patrol vs Raid meeting ---------------------------------------------------------

        [Fact]
        public void PatrolInterceptsRaid_PatrolWins_RaidHasNoEffect()
        {
            WorldModel w = MakeWorld();
            var stRaider  = MakeState("RVN");
            var stDefender = MakeState("MERC");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;

            // Patrol has already arrived and is active, and heavily outguns the (unarmed) raid.
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5), // arrived 5 min ago
                Resolved = true,  // effects already applied
                WeaponBands = Bands(1000),
            });

            // Raid arrives this tick
            stRaider.PendingFleets.Add(MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now));

            var states = new List<FactionState> { stDefender, stRaider };
            var log    = new List<string>();

            // Guard's overwhelming Dps destroys the unarmed raid in the duel regardless of jitter.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength); // no damage — raid intercepted
            Assert.Contains("GUARD_INTERCEPT", string.Join("\n", log));
        }

        [Fact]
        public void PatrolInterceptsRaid_RaidWins_HoldIsDamaged()
        {
            WorldModel w = MakeWorld();
            var stRaider   = MakeState("RVN");
            var stDefender = MakeState("MERC");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true,
            });
            var raid = MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now);
            raid.WeaponBands = Bands(1000); // heavily outguns the unarmed Guard
            stRaider.PendingFleets.Add(raid);

            var states = new List<FactionState> { stDefender, stRaider };
            var log    = new List<string>();

            // Raid's overwhelming Dps breaks through and destroys the unarmed Guard.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysLose(), log, null);

            Assert.True(w.Graph.Nodes["MERC1"].HoldStrength < 0.8, "raid should damage hold when it wins combat");
            Assert.Contains("RAID_BREAKS_GUARD", string.Join("\n", log));
        }

        // ---- Raid vs Trade meeting ----------------------------------------------------------

        [Fact]
        public void RaidInterceptsTrade_HighChance_TradeFailsRaidLoots()
        {
            WorldModel w = MakeWorld();
            var stRaider  = MakeState("RVN");
            var stTrader  = MakeState("MERC");
            AddNode(w, "MERC1", "OTH", new Vec3(0, 0, 0), hold: 0.8);   // foreign ground (FAC-306): roll 0 loses the hull
            AddNode(w, "B1", "B", new Vec3(20, 0, 0));
            w.SetTreasury("RVN", 50000);

            DateTime now = w.UtcNow;
            const long cargo = 3000;

            // Both heading to MERC1 this tick
            stRaider.PendingFleets.Add(MakePending(ActionKind.Raid,  "RVN",  "MERC", "MERC1", now, now));
            stTrader.PendingFleets.Add(MakePending(ActionKind.Trade, "MERC", "MERC", "MERC1", now, now, cargo));

            var states = new List<FactionState> { stRaider, stTrader };
            var log    = new List<string>();

            // AlwaysWin = roll 0.0 < RaidInterceptsTradeChance (0.80) → Trade intercepted
            long treasuryBefore = w.GetTreasury("RVN");
            double relBefore = stTrader.RelationTo("MERC");
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Contains("RAID_LOOTS", string.Join("\n", log));
            Assert.Equal(relBefore, stTrader.RelationTo("MERC")); // no trade relation boost
            Assert.True(w.GetTreasury("RVN") > treasuryBefore, "raider should loot cargo value");
        }

        [Fact]
        public void RaidInterceptsTrade_TradeEscapes_WhenRollHigh()
        {
            WorldModel w = MakeWorld();
            var stRaider  = MakeState("RVN");
            var stTrader  = MakeState("MERC");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            stRaider.PendingFleets.Add(MakePending(ActionKind.Raid,  "RVN",  "MERC", "MERC1", now, now));
            stTrader.PendingFleets.Add(MakePending(ActionKind.Trade, "MERC", "MERC", "MERC1", now, now));

            var states = new List<FactionState> { stRaider, stTrader };
            var log    = new List<string>();

            // AlwaysLose = roll 1.0 ≥ RaidInterceptsTradeChance (0.80) → Trade escapes
            double relBefore = stTrader.RelationTo("MERC");
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysLose(), log, null);

            Assert.Contains("TRADE_ESCAPES", string.Join("\n", log));
            Assert.True(stTrader.RelationTo("MERC") > relBefore, "escaped trade should still lift relation");
        }

        // ---- Triple encounter: Patrol + Raid + Trade ----------------------------------------

        [Fact]
        public void TripleEncounter_PatrolWins_TradeIsFree()
        {
            WorldModel w = MakeWorld();
            var stRaider   = MakeState("RVN");
            var stDefender = MakeState("MERC");
            var stTrader   = MakeState("STL");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            w.SetTreasury("RVN", 50000);

            DateTime now = w.UtcNow;
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true,
                WeaponBands = Bands(1000), // heavily outguns the unarmed raid
            });
            stRaider.PendingFleets.Add(MakePending(ActionKind.Raid,  "RVN", "MERC", "MERC1", now, now));
            stTrader.PendingFleets.Add(MakePending(ActionKind.Trade, "STL", "MERC", "MERC1", now, now));

            var states = new List<FactionState> { stDefender, stRaider, stTrader };
            var log    = new List<string>();
            double relBefore = stTrader.RelationTo("MERC");

            // Guard's overwhelming Dps destroys the raid; Trade goes free.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            string logStr = string.Join("\n", log);
            Assert.Contains("TRIPLE GUARD_WINS", logStr);
            Assert.Contains("TRIPLE TRADE_FREE", logStr);
            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength); // no raid damage
            Assert.True(stTrader.RelationTo("MERC") > relBefore, "trade should complete unhindered");
        }

        [Fact]
        public void TripleEncounter_RaidWins_TradeInterceptChanceIsReduced()
        {
            // Raid (100 Dps) narrowly outguns the Guard (90 Dps): with CombatBaseDamageFraction=0.35
            // over 3 turns at rng=0.5 (jitter cancels to exactly 1.0), the Guard is destroyed but the
            // Raid ends the duel at ~5.5/100 = ~0.055 surviving ratio — badly weakened. That low
            // raidStrength drags the Raid-vs-Trade intercept threshold (0.80 * 0.055 ≈ 0.044) far
            // below the fixed 0.5 trade roll, so the Trade escapes despite the Raid winning.
            WorldModel w = MakeWorld();
            var stRaider   = MakeState("RVN");
            var stDefender = MakeState("MERC");
            var stTrader   = MakeState("STL");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            w.SetTreasury("RVN", 50000);

            DateTime now = w.UtcNow;
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true,
                WeaponBands = Bands(90),
            });
            var raid = MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now);
            raid.WeaponBands = Bands(100);
            stRaider.PendingFleets.Add(raid);
            stTrader.PendingFleets.Add(MakePending(ActionKind.Trade, "STL", "MERC", "MERC1", now, now));

            var states = new List<FactionState> { stDefender, stRaider, stTrader };
            var log    = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            string logStr = string.Join("\n", log);
            Assert.Contains("TRIPLE RAID_WINS_COMBAT", logStr);
            Assert.Contains("TRIPLE TRADE_ESCAPES", logStr);
        }

        // ---- Active patrol duration: blocks later Raid even after patrol has already resolved ----

        [Fact]
        public void ActivePatrol_StillBlocksRaid_AfterInitialResolution()
        {
            WorldModel w = MakeWorld();
            var stDefender = MakeState("MERC");
            var stRaider   = MakeState("RVN");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);

            // Patrol arrived 5 ticks ago (resolved=true), but patrol window is 60 min
            DateTime now = w.UtcNow;
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-10), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true, // already fired its effect
                WeaponBands = Bands(1000), // heavily outguns the unarmed raid
            });

            // Raid arrives now (5 ticks after patrol arrived, well within 60 min window)
            stRaider.PendingFleets.Add(MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now));

            var states = new List<FactionState> { stDefender, stRaider };
            var log    = new List<string>();

            // Patrol is still active → combat fires; AlwaysWin → patrol wins
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength);
            Assert.Contains("GUARD_INTERCEPT", string.Join("\n", log));
        }

        [Fact]
        public void ActivePatrol_DoesNotBlockRaid_AfterPatrolExpires()
        {
            WorldModel w = MakeWorld();
            var stDefender = MakeState("MERC");
            var stRaider   = MakeState("RVN");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            // Patrol arrived 90 minutes ago — well past the 60-min GuardDuration
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-100), EstimatedArrivalUtc = now.AddMinutes(-90),
                Resolved = true,
            });

            stRaider.PendingFleets.Add(MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now));

            var states = new List<FactionState> { stDefender, stRaider };
            var log    = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            // No patrol combat — patrol expired; Raid proceeds
            Assert.True(w.Graph.Nodes["MERC1"].HoldStrength < 0.8, "expired patrol should not block raid");
            Assert.DoesNotContain("GUARD_INTERCEPT", string.Join("\n", log));
        }

        // ---- Load-time reconciliation (sub-task 7) ------------------------------------------

        [Fact]
        public void ReconcileOnLoad_OverdueFleetsResolveAtFullRatio()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("A");
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);

            // Fleet was supposed to arrive 10 minutes ago (in the past relative to world.UtcNow)
            DateTime now = w.UtcNow;
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = now.AddMinutes(-30), EstimatedArrivalUtc = now.AddMinutes(-10),
                CargoValue = 0, Resolved = false
            });

            var states = new List<FactionState> { st, MakeState("B") };
            var log    = new List<string>();
            PendingFleetResolver.ReconcileAndResolveOnLoad(states, w, w.Tuning, AlwaysWin(), log, null);

            // Should have resolved at full ratio
            Assert.True(target.HoldStrength < 0.8, "overdue raid should resolve at full damage");
            Assert.Empty(st.PendingFleets);
            Assert.Contains("RAID_RESOLVED", string.Join("\n", log));
        }

        [Fact]
        public void ReconcileOnLoad_FutureFleetsAreNotResolved()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("A");
            // 3000 km route — expected transit at 300 km/min = 10 min, so a 15-min stored transit
            // is within the stale-speed guard's 50× threshold and must survive reconciliation.
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(3000, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            // Arrives in the future — server was not down for long enough
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(3000, 0, 0),
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(10),
                CargoValue = 0, Resolved = false
            });

            var states = new List<FactionState> { st, MakeState("B") };
            var log    = new List<string>();
            PendingFleetResolver.ReconcileAndResolveOnLoad(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal(0.8, target.HoldStrength); // not arrived yet
            Assert.Single(st.PendingFleets);         // still in flight
        }

        [Fact]
        public void ReconcileOnLoad_StaleSpeedFleetsAreForceResolved()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("A");
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            // 10 km at 300 km/min = 0.033 min expected; stored transit = 3 days = classic 1 km/min artifact.
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = now.AddMinutes(-60), EstimatedArrivalUtc = now.AddMinutes(14400),
                CargoValue = 0, Resolved = false
            });

            var states = new List<FactionState> { st, MakeState("B") };
            var log    = new List<string>();
            PendingFleetResolver.ReconcileAndResolveOnLoad(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.True(target.HoldStrength < 0.8, "stale-speed raid should force-resolve at full damage");
            Assert.Empty(st.PendingFleets);
            Assert.Contains("stale_speed", string.Join("\n", log));
        }

        // ---- Mid-transit pathfinding and encounters -------------------------------------------

        /// <summary>Build a 3-node chain: A -(50km)- B -(50km)- C, all connected.
        /// Returns node IDs in order [A, B, C].</summary>
        private static string[] MakeChain(WorldModel w, string ownerA, string ownerB, string ownerC)
        {
            // A at origin, B halfway, C at end (along X axis)
            AddNode(w, "A", ownerA, new Vec3(0, 0, 0));
            AddNode(w, "B", ownerB, new Vec3(50, 0, 0));
            AddNode(w, "C", ownerC, new Vec3(100, 0, 0));
            w.Graph.Connect("A", "B");
            w.Graph.Connect("B", "C");
            return new[] { "A", "B", "C" };
        }

        [Fact]
        public void CreatePending_StoresGraphPath_WhenNodesAreConnected()
        {
            WorldModel w = MakeWorld();
            MakeChain(w, "A", null, "B");

            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "C", Cost = 12000 };
            PendingFleetAction f = PendingFleetResolver.CreatePending(ActionKind.Raid, "A", intent, w, w.Tuning, immediate: false);

            Assert.NotNull(f.PathNodeIds);
            Assert.Equal(3, f.PathNodeIds.Length);
            Assert.Equal("A", f.PathNodeIds[0]);
            Assert.Equal("B", f.PathNodeIds[1]);
            Assert.Equal("C", f.PathNodeIds[2]);
            Assert.Equal(-1, f.LastResolvedSegment);
        }

        /// <summary>A(0,0,0) -- B(0,50,0) -- C(100,50,0): the graph detour is 50+100 = 150 km while
        /// the line the convoy is actually commanded along is |AC| = ~111.8 km.</summary>
        private static WorldModel MakeDetourGraph()
        {
            WorldModel w = MakeWorld();
            w.Tuning = SlowFleetTuning();
            AddNode(w, "A", "FA", new Vec3(0, 0, 0));
            AddNode(w, "B", null,  new Vec3(0, 50, 0));
            AddNode(w, "C", "FB", new Vec3(100, 50, 0));
            w.Graph.Connect("A", "B");
            w.Graph.Connect("B", "C");
            return w;
        }

        private const double DetourStraightLineKm = 111.8033988749895;  // |AC| in MakeDetourGraph

        [Fact]
        public void CreatePending_BillsTheLineItFlies_NotTheGraphDetour()
        {
            WorldModel w = MakeDetourGraph();

            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "C", Cost = 0 };
            PendingFleetAction f = PendingFleetResolver.CreatePending(ActionKind.Raid, "FA", intent, w, w.Tuning, immediate: false);

            double mins = (f.EstimatedArrivalUtc - f.DepartedUtc).TotalMinutes;
            Assert.True(Math.Abs(mins - DetourStraightLineKm) < 0.5,
                "Transit must be billed for the straight line (~112 min), got " + mins);
            // The graph route survives as the encounter-waypoint list; it just no longer prices the leg.
            Assert.NotNull(f.PathNodeIds);
            Assert.Equal(3, f.PathNodeIds.Length);
        }

        [Fact]
        public void CurrentPosKm_ReachesTheTarget_ExactlyAtTheEstimatedArrival()
        {
            WorldModel w = MakeDetourGraph();

            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "C", Cost = 0 };
            PendingFleetAction f = PendingFleetResolver.CreatePending(ActionKind.Raid, "FA", intent, w, w.Tuning, immediate: false);
            double totalMins = (f.EstimatedArrivalUtc - f.DepartedUtc).TotalMinutes;

            // Still short of the destination at 95% of the leg -- a fleet billed for the 150 km graph
            // route would have been parked on its target since the 75% mark.
            Vec3 nearEnd = f.CurrentPosKm(f.DepartedUtc.AddMinutes(totalMins * 0.95), w.Tuning);
            Assert.True(nearEnd.DistanceTo(f.TargetPosKm) > 1.0,
                "the fleet must still be in transit at 95% of its billed time, was "
                + nearEnd.DistanceTo(f.TargetPosKm) + " km out");

            Assert.True(f.CurrentPosKm(f.EstimatedArrivalUtc, w.Tuning).DistanceTo(f.TargetPosKm) < 0.01,
                "the fleet must be on its target at the estimated arrival");
        }

        [Fact]
        public void AdvanceToNextStop_BillsTheReturnLegForTheLineItFlies()
        {
            WorldModel w = MakeDetourGraph();
            var actor = MakeState("FA");
            var states = new List<FactionState> { actor, MakeState("FB") };
            var log = new List<string>();

            actor.PendingFleets.Add(PendingFleetResolver.CreatePending(ActionKind.Raid, "FA",
                new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "C", Cost = 0 },
                w, w.Tuning, immediate: false));

            // The target, then every engagement dwell held there. The return leg only exists once the
            // siege is spent, so the walk is driven off CurrentStopKind rather than a fixed count --
            // AlwaysWin keeps the ratio high enough that no cycle is broken off early.
            w.UtcNow = actor.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);
            Assert.Equal(FleetStopKind.Engage, actor.PendingFleets[0].CurrentStopKind);
            int guard = 0;
            while (actor.PendingFleets[0].CurrentStopKind == FleetStopKind.Engage && guard++ < 16)
            {
                w.UtcNow = actor.PendingFleets[0].EstimatedArrivalUtc;
                PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);
            }

            PendingFleetAction ret = actor.PendingFleets[0];
            Assert.Equal(FleetStopKind.Return, ret.CurrentStopKind);
            double mins = (ret.EstimatedArrivalUtc - ret.DepartedUtc).TotalMinutes;
            Assert.True(Math.Abs(mins - DetourStraightLineKm) < 0.5,
                "the return leg must be billed for the straight line (~112 min), got " + mins);
        }

        [Fact]
        public void CreatePending_FallsBackToStraightLine_WhenNodesDisconnected()
        {
            // Same as existing test: nodes not connected → straight-line fallback
            WorldModel w = MakeWorld();
            w.Tuning = SlowFleetTuning();
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(100, 0, 0));
            // No w.Graph.Connect call

            var intent = new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "B1", Cost = 0 };
            PendingFleetAction f = PendingFleetResolver.CreatePending(ActionKind.Raid, "A", intent, w, w.Tuning, immediate: false);

            Assert.Null(f.PathNodeIds);
            double mins = (f.EstimatedArrivalUtc - f.DepartedUtc).TotalMinutes;
            Assert.Equal(100.0, mins, 1); // straight-line fallback
        }

        [Fact]
        public void MidTransit_RaidInterceptsTrade_AtIntermediateNode()
        {
            // Graph: TRADE_ORIGIN -(50km)- MID -(50km)- RAID_ORIGIN
            //   Trade:  TRADE_ORIGIN → MID → RAID_ORIGIN  (both passing through MID)
            //   Raid:   RAID_ORIGIN  → MID → TRADE_ORIGIN
            // Both convoys should be at MID at t=50min; encounter fires there.
            WorldModel w = MakeWorld();
            w.Tuning = SlowFleetTuning();

            AddNode(w, "TO", "MERC", new Vec3(0, 0, 0));   // Trade origin / Raid destination
            AddNode(w, "MID", null,  new Vec3(50, 0, 0));   // intermediate waypoint
            AddNode(w, "RO", "RVN",  new Vec3(100, 0, 0)); // Raid origin / Trade destination
            w.Graph.Connect("TO", "MID");
            w.Graph.Connect("MID", "RO");
            w.SetTreasury("RVN", 50000);

            DateTime t0 = w.UtcNow;
            const long cargo = 6000;

            // Trade: MERC → RO (path TO→MID→RO, 100 km → 100 min)
            var stMerc = MakeState("MERC");
            stMerc.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = "RVN", TargetNodeId = "RO",
                OriginNodeId = "TO", OriginPosKm = new Vec3(0,0,0), TargetPosKm = new Vec3(100,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                CargoValue = cargo, PathNodeIds = new[] { "TO", "MID", "RO" }, LastResolvedSegment = -1
            });

            // Raid: RVN → TO (path RO→MID→TO, 100 km → 100 min)
            var stRvn = MakeState("RVN");
            stRvn.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "TO",
                OriginNodeId = "RO", OriginPosKm = new Vec3(100,0,0), TargetPosKm = new Vec3(0,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                CargoValue = 0, PathNodeIds = new[] { "RO", "MID", "TO" }, LastResolvedSegment = -1
            });

            var states = new List<FactionState> { stMerc, stRvn };
            var log    = new List<string>();

            // Advance to t=51 min: both convoys have just entered segment 1 (past MID).
            w.UtcNow = t0.AddMinutes(51);
            long treasuryBefore = w.GetTreasury("RVN");
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            string logStr = string.Join("\n", log);
            Assert.Contains("RAID_LOOTS", logStr);
            Assert.Contains("@MID", logStr);
            Assert.True(w.GetTreasury("RVN") > treasuryBefore, "raider should loot cargo at mid-transit encounter");
        }

        [Fact]
        public void MidTransit_PatrolInterceptsRaid_AtIntermediateNode()
        {
            // Graph: A -(50km)- MID -(50km)- B
            //   Patrol: A → MID → B  (MERC patrol in transit)
            //   Raid:   B → MID → A  (RVN raid in transit)
            WorldModel w = MakeWorld();
            w.Tuning = SlowFleetTuning();

            AddNode(w, "A", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            AddNode(w, "MID", null, new Vec3(50, 0, 0));
            AddNode(w, "B", "MERC", new Vec3(100, 0, 0));
            w.Graph.Connect("A", "MID");
            w.Graph.Connect("MID", "B");

            DateTime t0 = w.UtcNow;

            var stMerc = MakeState("MERC");
            stMerc.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "B",
                OriginNodeId = "A", OriginPosKm = new Vec3(0,0,0), TargetPosKm = new Vec3(100,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                PathNodeIds = new[] { "A", "MID", "B" }, LastResolvedSegment = -1,
                WeaponBands = Bands(1000), // heavily outguns the unarmed raid
            });

            var stRvn = MakeState("RVN");
            stRvn.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "A",
                OriginNodeId = "B", OriginPosKm = new Vec3(100,0,0), TargetPosKm = new Vec3(0,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                PathNodeIds = new[] { "B", "MID", "A" }, LastResolvedSegment = -1
            });

            var states = new List<FactionState> { stMerc, stRvn };
            var log    = new List<string>();

            // t=51: both are past MID → encounter fires; Guard's overwhelming Dps wins the duel.
            w.UtcNow = t0.AddMinutes(51);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            string logStr = string.Join("\n", log);
            Assert.Contains("GUARD_INTERCEPT", logStr);
            Assert.Contains("@MID", logStr);
            // Raid is intercepted mid-route — target node A should be undamaged
            Assert.Equal(0.8, w.Graph.Nodes["A"].HoldStrength);
        }

        [Fact]
        public void MidTransit_LastResolvedSegment_PreventsDoubleFire()
        {
            // Same setup as MidTransit_RaidInterceptsTrade but we call ResolveArrivals twice at t=51.
            // The second call must not fire another encounter.
            WorldModel w = MakeWorld();
            w.Tuning = SlowFleetTuning();

            AddNode(w, "TO", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "MID", null,  new Vec3(50, 0, 0));
            AddNode(w, "RO", "RVN",  new Vec3(100, 0, 0));
            w.Graph.Connect("TO", "MID");
            w.Graph.Connect("MID", "RO");
            w.SetTreasury("RVN", 50000);

            DateTime t0 = w.UtcNow;

            var stMerc = MakeState("MERC");
            stMerc.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = "RVN", TargetNodeId = "RO",
                OriginNodeId = "TO", OriginPosKm = new Vec3(0,0,0), TargetPosKm = new Vec3(100,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                CargoValue = 6000, PathNodeIds = new[] { "TO", "MID", "RO" }, LastResolvedSegment = -1
            });

            var stRvn = MakeState("RVN");
            stRvn.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "TO",
                OriginNodeId = "RO", OriginPosKm = new Vec3(100,0,0), TargetPosKm = new Vec3(0,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                CargoValue = 0, PathNodeIds = new[] { "RO", "MID", "TO" }, LastResolvedSegment = -1
            });

            var states = new List<FactionState> { stMerc, stRvn };
            var log1   = new List<string>();
            var log2   = new List<string>();

            w.UtcNow = t0.AddMinutes(51);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log1, null);

            // Second call at same time — encounter must not repeat
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log2, null);

            int raidLoots1 = 0; foreach (string l in log1) if (l.Contains("RAID_LOOTS")) raidLoots1++;
            int raidLoots2 = 0; foreach (string l in log2) if (l.Contains("RAID_LOOTS")) raidLoots2++;

            Assert.Equal(1, raidLoots1); // fired once in first call
            Assert.Equal(0, raidLoots2); // not re-fired
        }

        [Fact]
        public void MidTransit_Triple_PatrolWins_TradeFree()
        {
            // Graph: A -(50km)- MID -(50km)- B
            //   Patrol (MERC):  A → MID → B
            //   Raid   (RVN):   B → MID → A
            //   Trade  (STL):   B → MID → A (heading from B to A alongside the raid)
            WorldModel w = MakeWorld();
            w.Tuning = SlowFleetTuning();

            AddNode(w, "A", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            AddNode(w, "MID", null, new Vec3(50, 0, 0));
            AddNode(w, "B", "MERC", new Vec3(100, 0, 0));
            w.Graph.Connect("A", "MID");
            w.Graph.Connect("MID", "B");
            w.SetTreasury("RVN", 50000);

            DateTime t0 = w.UtcNow;

            var stMerc = MakeState("MERC");
            stMerc.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "B",
                OriginNodeId = "A", OriginPosKm = new Vec3(0,0,0), TargetPosKm = new Vec3(100,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                PathNodeIds = new[] { "A", "MID", "B" }, LastResolvedSegment = -1,
                WeaponBands = Bands(1000), // heavily outguns the unarmed raid
            });

            var stRvn = MakeState("RVN");
            stRvn.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "A",
                OriginNodeId = "B", OriginPosKm = new Vec3(100,0,0), TargetPosKm = new Vec3(0,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                PathNodeIds = new[] { "B", "MID", "A" }, LastResolvedSegment = -1
            });

            var stStl = MakeState("STL");
            stStl.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "STL", TargetTag = "MERC", TargetNodeId = "A",
                OriginNodeId = "B", OriginPosKm = new Vec3(100,0,0), TargetPosKm = new Vec3(0,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                CargoValue = 4000, PathNodeIds = new[] { "B", "MID", "A" }, LastResolvedSegment = -1
            });

            var states = new List<FactionState> { stMerc, stRvn, stStl };
            var log    = new List<string>();

            w.UtcNow = t0.AddMinutes(51);
            // AlwaysWin → patrol wins combat, trade goes free
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            string logStr = string.Join("\n", log);
            Assert.Contains("TRIPLE GUARD_WINS", logStr);
            Assert.Contains("TRIPLE TRADE_FREE", logStr);
            Assert.Contains("@MID", logStr);
            Assert.Equal(0.8, w.Graph.Nodes["A"].HoldStrength); // raid never reached A
        }

        [Fact]
        public void ReconcileOnLoad_FastForwardsLastResolvedSegment_NoPastEncounters()
        {
            // Convoy loaded mid-transit (already past MID); no enemy at MID at load time.
            // LastResolvedSegment should be fast-forwarded so the past intermediate encounter is not re-fired.
            WorldModel w = MakeWorld();
            w.Tuning = SlowFleetTuning();

            AddNode(w, "TO", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "MID", null,  new Vec3(50, 0, 0));
            AddNode(w, "RO", "RVN",  new Vec3(100, 0, 0), hold: 0.8);
            w.Graph.Connect("TO", "MID");
            w.Graph.Connect("MID", "RO");

            DateTime t0 = w.UtcNow.AddMinutes(-70); // departed 70 min ago

            // Raid: already past MID, heading to RO; arrives at t0+100=now+30
            var stMerc = MakeState("MERC");
            stMerc.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "MERC", TargetTag = "RVN", TargetNodeId = "RO",
                OriginNodeId = "TO", OriginPosKm = new Vec3(0,0,0), TargetPosKm = new Vec3(100,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                CargoValue = 0, PathNodeIds = new[] { "TO", "MID", "RO" }, LastResolvedSegment = -1
            });

            // No enemy convoy — there should be no mid-transit encounter event from the reconcile call.
            var states = new List<FactionState> { stMerc, MakeState("RVN") };
            var log    = new List<string>();
            PendingFleetResolver.ReconcileAndResolveOnLoad(states, w, w.Tuning, AlwaysWin(), log, null);

            // Raid has not arrived yet (30 min remaining) — no RAID_RESOLVED
            Assert.DoesNotContain("RAID_RESOLVED", string.Join("\n", log));
            // No mid-transit RAID_LOOTS or GUARD_INTERCEPT
            Assert.DoesNotContain("RAID_LOOTS", string.Join("\n", log));
            // The fleet is still in flight
            Assert.Single(stMerc.PendingFleets);
            // LastResolvedSegment fast-forwarded to skip past segments
            Assert.True(stMerc.PendingFleets[0].LastResolvedSegment >= 1,
                "LastResolvedSegment should be fast-forwarded past MID after load reconciliation");
        }

        [Fact]
        public void Gift_ReturnsTradeShipToOrigin_OnSuccessfulArrival()
        {
            WorldModel w = MakeWorld();
            var sender = MakeState("A");
            var recipient = MakeState("B");
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0));

            DateTime now = w.UtcNow;
            var gift = MakePending(ActionKind.Gift, "A", "B", "B1", now, now);
            gift.OriginNodeId = "A1";
            gift.ShipPrefabId = "TradeHauler";
            sender.PendingFleets.Add(gift);

            var states = new List<FactionState> { sender, recipient };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            SpatialNode origin = w.Graph.Nodes["A1"];
            int stock;
            origin.ShipStock.TryGetValue("TradeHauler", out stock);
            Assert.Equal(1, stock);
            Assert.Empty(sender.PendingFleets);
        }

        [Fact]
        public void Guard_ReturnsPatrolShipToTarget_WhenGuardWindowExpires()
        {
            WorldModel w = MakeWorld();
            var defender = MakeState("A");
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));

            DateTime departed = w.UtcNow;
            var guard = MakePending(ActionKind.Guard, "A", "A", "A1", departed, departed);
            guard.OriginNodeId = "A1";
            guard.ShipPrefabId = "PatrolCargoship";
            defender.PendingFleets.Add(guard);

            var states = new List<FactionState> { defender };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);
            Assert.Single(defender.PendingFleets);

            w.UtcNow = departed.Add(w.Tuning.GuardDuration).AddSeconds(1);
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            SpatialNode target = w.Graph.Nodes["A1"];
            int stock;
            target.ShipStock.TryGetValue("PatrolCargoship", out stock);
            Assert.Equal(1, stock);
            Assert.Empty(defender.PendingFleets);
        }

        [Fact]
        public void Raid_ReturnsConvoySizeShips_NotAlwaysOne()
        {
            // A successful Raid with ConvoySize=3 (e.g. Muster-scaled) must credit 3 units of its
            // prefab back to the unified ShipStock pool — not the old hardcoded +1, which would
            // silently leak ships once dispatch started honestly consuming ConvoySize units.
            WorldModel w = MakeWorld();
            var attacker = MakeState("A");
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            var raid = MakePending(ActionKind.Raid, "A", "B", "B1", now, now);
            raid.OriginNodeId = "A1";
            raid.ConvoySize = 3;
            raid.ShipPrefabId = "SomeRaidPrefab";
            attacker.PendingFleets.Add(raid);

            var states = new List<FactionState> { attacker, MakeState("B") };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            SpatialNode origin = w.Graph.Nodes["A1"];
            int stock;
            origin.ShipStock.TryGetValue("SomeRaidPrefab", out stock);
            Assert.Equal(3, stock);
        }

        [Fact]
        public void Raid_LootsMostValuableItemsFirst_VolumeCappedByCargoCapacity()
        {
            WorldModel w = MakeWorld();
            var attacker = MakeState("A");
            SpatialNode origin = AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);
            target.Inventory["Gold"] = 10;
            target.Inventory["Iron"] = 1000;

            var prices = new FakePriceCatalogue();
            prices.Prices["Gold"] = 1000;
            prices.Prices["Iron"] = 1;
            var volumes = new FixedVolumeCatalogue(1.0); // 1 L/unit for everything

            DateTime now = w.UtcNow;
            var raid = MakePending(ActionKind.Raid, "A", "B", "B1", now, now);
            raid.OriginNodeId = "A1";
            raid.ConvoySize = 1;
            raid.CargoCapacityLiters = 10; // room for only the 10 units of Gold
            attacker.PendingFleets.Add(raid);

            var states = new List<FactionState> { attacker, MakeState("B") };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null, null, prices, volumes);

            // Most valuable first: all 10 Gold taken; the bulkier, cheaper Iron is left behind once
            // the volume budget is exhausted.
            Assert.Equal(0, target.Inventory["Gold"]);
            Assert.Equal(1000, target.Inventory["Iron"]);
            Assert.Equal(10, origin.Inventory["Gold"]);
            Assert.False(origin.Inventory.ContainsKey("Iron"));
            Assert.Contains("loot=10000", string.Join("\n", log));
        }

        [Fact]
        public void Raid_LootsNothing_WhenConvoyHasNoCargoCapacity()
        {
            WorldModel w = MakeWorld();
            var attacker = MakeState("A");
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);
            target.Inventory["Gold"] = 10;

            var prices = new FakePriceCatalogue();
            prices.Prices["Gold"] = 1000;

            DateTime now = w.UtcNow;
            var raid = MakePending(ActionKind.Raid, "A", "B", "B1", now, now);
            raid.OriginNodeId = "A1";
            raid.ConvoySize = 1;
            raid.CargoCapacityLiters = 0; // a fighters-only squadron with no cargo slot
            attacker.PendingFleets.Add(raid);

            var states = new List<FactionState> { attacker, MakeState("B") };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null, null, prices, null);

            Assert.Equal(10, target.Inventory["Gold"]); // untouched — no crash, no fractional/negative transfer
            Assert.DoesNotContain("loot=", string.Join("\n", log));
        }

        [Fact]
        public void Raid_SquadronMembers_EachReturnToItsOwnPrefabStock()
        {
            // A mixed squadron of three different prefabs — a successful return must credit each
            // member's own prefab in the unified pool, not just the fleet's representative
            // ShipPrefabId × ConvoySize (which would silently misattribute two of the three ships).
            WorldModel w = MakeWorld();
            var attacker = MakeState("A");
            SpatialNode origin = AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            var raid = MakePending(ActionKind.Raid, "A", "B", "B1", now, now);
            raid.OriginNodeId = "A1";
            raid.ConvoySize = 3;
            raid.ShipPrefabId = "BigGunship";
            raid.Squadron = new List<string> { "BigGunship", "Hauler", "Fighter" };
            attacker.PendingFleets.Add(raid);

            var states = new List<FactionState> { attacker, MakeState("B") };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            int bigGunshipStock, haulerStock, fighterStock;
            origin.ShipStock.TryGetValue("BigGunship", out bigGunshipStock);
            origin.ShipStock.TryGetValue("Hauler", out haulerStock);
            origin.ShipStock.TryGetValue("Fighter", out fighterStock);
            Assert.Equal(1, bigGunshipStock);
            Assert.Equal(1, haulerStock);
            Assert.Equal(1, fighterStock);
        }

        // ---- squadron survivorship: what comes home when a hull dies -------------------------

        /// <summary>Builds the three-prefab squadron the survivorship cases share, at "A1", with a
        /// mission target at "B1" it arrives at this tick.</summary>
        private static PendingFleetAction MakeSquadronRaid(WorldModel w, params string[] squadron)
        {
            DateTime now = w.UtcNow;
            PendingFleetAction raid = MakePending(ActionKind.Raid, "A", "B", "B1", now, now);
            raid.OriginNodeId = "A1";
            raid.ConvoySize = squadron.Length;
            raid.ShipPrefabId = squadron[0];
            raid.Squadron = new List<string>(squadron);
            return raid;
        }

        [Fact]
        public void Raid_SquadronLeadDestroyed_ReturnsSurvivingHullsAndLogsPartialReturn()
        {
            // A player kills the lead of a three-hull squadron. The mission fails (ratio 0), but the
            // two escorts flew home: only the hull that died leaves the pool.
            WorldModel w = MakeWorld();
            var attacker = MakeState("A");
            SpatialNode origin = AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);

            PendingFleetAction raid = MakeSquadronRaid(w, "BigGunship", "Hauler", "Fighter");
            raid.MarkHullLost("BigGunship");                  // the lead's grid was destroyed
            raid.MaterialisedEntityId = -1;                   // the sentinel the host pairs with it
            raid.ForcedRatio = 0.0;
            raid.ForcedRatioCause = "grid_destroyed";
            attacker.PendingFleets.Add(raid);

            var states = new List<FactionState> { attacker, MakeState("B") };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            int bigGunshipStock, haulerStock, fighterStock;
            origin.ShipStock.TryGetValue("BigGunship", out bigGunshipStock);
            origin.ShipStock.TryGetValue("Hauler", out haulerStock);
            origin.ShipStock.TryGetValue("Fighter", out fighterStock);
            Assert.Equal(0, bigGunshipStock);   // the hull that died stays dead
            Assert.Equal(1, haulerStock);
            Assert.Equal(1, fighterStock);

            string joined = string.Join("\n", log);
            Assert.Contains("SHIP_RETURNED", joined);
            Assert.Contains("hulls_returned=2/3", joined);
        }

        [Fact]
        public void Raid_SquadronDismissedBySpawnDedup_ReturnsNothing()
        {
            // spawn_dedup and sibling_dismiss are bookkeeping, not combat: the entry never had grids
            // in the world, so there is no survivor to credit and the whole squadron is consumed
            // exactly as it was before partial returns existed.
            WorldModel w = MakeWorld();
            var attacker = MakeState("A");
            SpatialNode origin = AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);

            PendingFleetAction raid = MakeSquadronRaid(w, "BigGunship", "Hauler", "Fighter");
            raid.MaterialisedEntityId = -1;
            raid.ForcedRatio = 0.0;
            raid.ForcedRatioCause = "spawn_dedup";
            attacker.PendingFleets.Add(raid);

            var states = new List<FactionState> { attacker, MakeState("B") };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Empty(origin.ShipStock);
            string joined = string.Join("\n", log);
            Assert.DoesNotContain("SHIP_RETURNED", joined);
            Assert.Contains("SHIP_LOST", joined);
        }

        [Fact]
        public void Raid_SingleHullSquadronDestroyed_ReturnsNothing()
        {
            // The one-grid case the old all-or-nothing rule already got right: its only hull died,
            // so HullsToReturn is empty and the partial-return branch credits nothing.
            WorldModel w = MakeWorld();
            var attacker = MakeState("A");
            SpatialNode origin = AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);

            PendingFleetAction raid = MakeSquadronRaid(w, "Solo");
            raid.MarkHullLost("Solo");
            raid.MaterialisedEntityId = -1;
            raid.ForcedRatio = 0.0;
            raid.ForcedRatioCause = "grid_destroyed";
            attacker.PendingFleets.Add(raid);

            var states = new List<FactionState> { attacker, MakeState("B") };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            int soloStock;
            origin.ShipStock.TryGetValue("Solo", out soloStock);
            Assert.Equal(0, soloStock);
            Assert.DoesNotContain("SHIP_RETURNED", string.Join("\n", log));
        }

        [Fact]
        public void Raid_SquadronSucceedsWithOneEscortLost_ReturnsAllButThatHull()
        {
            // A successful mission credits the hulls that survived it, not the roster it left with:
            // the escort a player shot down on the way in does not come home with the other two.
            WorldModel w = MakeWorld();
            var attacker = MakeState("A");
            SpatialNode origin = AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);

            PendingFleetAction raid = MakeSquadronRaid(w, "BigGunship", "Hauler", "Fighter");
            raid.MarkHullLost("Fighter");   // an escort died; the lead flew the mission to success
            attacker.PendingFleets.Add(raid);

            var states = new List<FactionState> { attacker, MakeState("B") };
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            int bigGunshipStock, haulerStock, fighterStock;
            origin.ShipStock.TryGetValue("BigGunship", out bigGunshipStock);
            origin.ShipStock.TryGetValue("Hauler", out haulerStock);
            origin.ShipStock.TryGetValue("Fighter", out fighterStock);
            Assert.Equal(1, bigGunshipStock);
            Assert.Equal(1, haulerStock);
            Assert.Equal(0, fighterStock);
            Assert.Contains("hulls_returned=2/3", string.Join("\n", log));
        }

        // ---- MarkHullLost ---------------------------------------------------------------------

        [Fact]
        public void MarkHullLost_SeedsSurvivorsFromSquadron_AndRemovesOneOccurrenceOnly()
        {
            var fleet = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A",
                Squadron = new List<string> { "Fighter", "Fighter", "Hauler" }
            };
            Assert.Null(fleet.SurvivingHulls);
            Assert.Equal(new List<string> { "Fighter", "Fighter", "Hauler" }, fleet.HullsToReturn);

            fleet.MarkHullLost("Fighter");

            Assert.Equal(new List<string> { "Fighter", "Hauler" }, fleet.SurvivingHulls);
            Assert.Equal(new List<string> { "Fighter", "Hauler" }, fleet.HullsToReturn);
            Assert.Equal(3, fleet.HullCount);   // the roster it left with, unchanged by a casualty
            Assert.Equal(new List<string> { "Fighter", "Fighter", "Hauler" }, fleet.Squadron);
        }

        [Fact]
        public void MarkHullLost_IsNoOp_ForNonSquadronFleetAndUnknownPrefab()
        {
            var solo = new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "A", ShipPrefabId = "Hauler", ConvoySize = 2
            };
            solo.MarkHullLost("Hauler");
            Assert.Null(solo.SurvivingHulls);       // no squadron to seed from
            Assert.Empty(solo.HullsToReturn);       // convoy accounting stays on ConvoySize

            var squad = new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A",
                Squadron = new List<string> { "Fighter", "Hauler" }
            };
            squad.MarkHullLost("SomethingElse");
            Assert.Equal(new List<string> { "Fighter", "Hauler" }, squad.SurvivingHulls);
        }

        [Fact]
        public void IsPhysicalLoss_IsTrueOnlyForGridCauses()
        {
            var fleet = new PendingFleetAction { Kind = ActionKind.Raid, ActorTag = "A" };
            Assert.False(fleet.IsPhysicalLoss);     // no forced ratio at all

            fleet.ForcedRatio = 0.0;
            foreach (string cause in new[] { "grid_destroyed", "grid_captured", "grid_stalled", "grid_disabled" })
            {
                fleet.ForcedRatioCause = cause;
                Assert.True(fleet.IsPhysicalLoss, cause + " must count as a physical loss");
            }
            foreach (string cause in new[] { "spawn_dedup", "sibling_dismiss", "guard_duel" })
            {
                fleet.ForcedRatioCause = cause;
                Assert.False(fleet.IsPhysicalLoss, cause + " must not count as a physical loss");
            }
        }

        // ---- Conquer resolution --------------------------------------------------------------

        [Fact]
        public void ResolveSingle_Conquer_WeakTarget_CapturesNodeAndLogsCaptured()
        {
            WorldModel w = MakeWorld();
            w.Tuning.ConquerHoldCeiling = 0.15;
            var attacker = MakeState("A");
            var victim = MakeState("B");
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.10);

            DateTime now = w.UtcNow;
            var fleet = MakePending(ActionKind.Conquer, "A", "B", "B1", now.AddMinutes(-1), now);
            fleet.OriginNodeId = "B1";
            attacker.PendingFleets.Add(fleet);

            var states = new List<FactionState> { attacker, victim };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal("A", target.OwnerFactionTag);
            Assert.Contains(log, l => l.Contains("CONQUER_RESOLVED") && l.Contains("[captured]"));
        }

        [Fact]
        public void ResolveSingle_Conquer_RecoveredTarget_DoesNotCaptureAndLogsRepelled()
        {
            WorldModel w = MakeWorld();
            w.Tuning.ConquerHoldCeiling = 0.15;
            var attacker = MakeState("A");
            var victim = MakeState("B");
            // Hold recovered above the ceiling between dispatch and arrival (e.g. a Reinforce).
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.9);

            DateTime now = w.UtcNow;
            var fleet = MakePending(ActionKind.Conquer, "A", "B", "B1", now.AddMinutes(-1), now);
            fleet.OriginNodeId = "B1";
            attacker.PendingFleets.Add(fleet);

            var states = new List<FactionState> { attacker, victim };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal("B", target.OwnerFactionTag);
            Assert.True(target.HoldStrength < 0.9, "recovered-target Conquer must still apply fallback hold damage");
            Assert.Contains(log, l => l.Contains("CONQUER_RESOLVED") && l.Contains("[repelled]"));
        }

        [Fact]
        public void ResolveSingle_Conquer_Intercepted_NeverCallsWorldConquer()
        {
            WorldModel w = MakeWorld();
            w.Tuning.ConquerHoldCeiling = 0.15;
            var attacker = MakeState("A");
            var victim = MakeState("B");
            SpatialNode target = AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.10);

            DateTime now = w.UtcNow;
            var fleet = MakePending(ActionKind.Conquer, "A", "B", "B1", now.AddMinutes(-1), now);
            fleet.OriginNodeId = "B1";
            fleet.Intercepted = true;
            attacker.PendingFleets.Add(fleet);

            var states = new List<FactionState> { attacker, victim };
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Equal("B", target.OwnerFactionTag);
            Assert.DoesNotContain(log, l => l.Contains("CONQUER_RESOLVED"));
            Assert.Empty(attacker.PendingFleets); // intercepted fleet is cleaned up regardless
        }
            // ---- FAC-305: an intercepted convoy's hull is written down ------------------------------

        [Fact]
        public void RaidInterceptsTrade_LogsShipLostForTheConvoyHull()
        {
            WorldModel w = MakeWorld();
            var stRaider = MakeState("RVN");
            var stTrader = MakeState("MERC");
            AddNode(w, "MERC1", "OTH", new Vec3(0, 0, 0), hold: 0.8);   // foreign ground: the 15% roll applies
            w.SetTreasury("RVN", 50000);

            DateTime now = w.UtcNow;
            stRaider.PendingFleets.Add(MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now));
            var trade = MakePending(ActionKind.Trade, "MERC", "MERC", "MERC1", now, now);
            trade.ShipPrefabId = "Spadi-Space";
            trade.OriginNodeId = "MERC1";
            trade.ConvoySize = 1;
            stTrader.PendingFleets.Add(trade);

            var states = new List<FactionState> { stRaider, stTrader };
            var log    = new List<string>();
            w.ClearPendingEvents();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            string logStr = string.Join("\n", log);
            Assert.Contains("RAID_LOOTS", logStr);
            string lost = Assert.Single(log, l => l.StartsWith("SHIP_LOST ") && l.Contains(" MERC "));
            Assert.Contains("prefab=Spadi-Space", lost);
            Assert.Contains("count=1", lost);
            Assert.Contains("to=MERC1", lost);
            Assert.Contains("why=intercepted:RVN", lost);
            Assert.Contains(" raid=[", lost);
            Assert.Contains(w.PendingEvents, e => e.Kind == FactionEventKind.ShipLost
                                                  && e.ActorTag == "MERC" && e.OtherTag == "RVN");
            Assert.Empty(stTrader.PendingFleets);   // removed exactly as before -- the line is the only change
        }

        [Fact]
        public void RaidInterceptsTrade_TradeEscapes_NoShipLost()
        {
            WorldModel w = MakeWorld();
            var stRaider = MakeState("RVN");
            var stTrader = MakeState("MERC");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);

            DateTime now = w.UtcNow;
            stRaider.PendingFleets.Add(MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now));
            var trade = MakePending(ActionKind.Trade, "MERC", "MERC", "MERC1", now, now);
            trade.ShipPrefabId = "Spadi-Space";
            stTrader.PendingFleets.Add(trade);

            var states = new List<FactionState> { stRaider, stTrader };
            var log    = new List<string>();
            w.ClearPendingEvents();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysLose(), log, null);

            Assert.Contains("TRADE_ESCAPES", string.Join("\n", log));
            Assert.DoesNotContain(log, l => l.StartsWith("SHIP_LOST ") && l.Contains(" MERC "));
            Assert.DoesNotContain(w.PendingEvents, e => e.Kind == FactionEventKind.ShipLost && e.ActorTag == "MERC");
        }

        [Fact]
        public void RaidInterceptsTrade_ConvoyWithoutAHull_NoShipLost()
        {
            WorldModel w = MakeWorld();
            var stRaider = MakeState("RVN");
            var stTrader = MakeState("MERC");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            w.SetTreasury("RVN", 50000);

            DateTime now = w.UtcNow;
            stRaider.PendingFleets.Add(MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now));
            stTrader.PendingFleets.Add(MakePending(ActionKind.Trade, "MERC", "MERC", "MERC1", now, now)); // no prefab

            var states = new List<FactionState> { stRaider, stTrader };
            var log    = new List<string>();
            w.ClearPendingEvents();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            Assert.Contains("RAID_LOOTS", string.Join("\n", log));
            Assert.DoesNotContain(log, l => l.StartsWith("SHIP_LOST ") && l.Contains(" MERC "));
        }

        [Fact]
        public void TripleEncounter_RaidWinsAndIntercepts_LogsShipLost()
        {
            WorldModel w = MakeWorld();
            var stRaider   = MakeState("RVN");
            var stDefender = MakeState("MERC");
            var stTrader   = MakeState("STL");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            w.SetTreasury("RVN", 50000);

            DateTime now = w.UtcNow;
            stDefender.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MERC1",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true,
                WeaponBands = Bands(10),
            });
            var raid = MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now);
            raid.WeaponBands = Bands(1000);
            stRaider.PendingFleets.Add(raid);
            var trade = MakePending(ActionKind.Trade, "STL", "MERC", "MERC1", now, now);
            trade.ShipPrefabId = "Clipper";
            stTrader.PendingFleets.Add(trade);

            var states = new List<FactionState> { stDefender, stRaider, stTrader };
            var log    = new List<string>();
            w.ClearPendingEvents();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            string logStr = string.Join("\n", log);
            Assert.Contains("TRIPLE RAID_LOOTS", logStr);
            string lost = Assert.Single(log, l => l.StartsWith("SHIP_LOST ") && l.Contains(" STL "));
            Assert.Contains("prefab=Clipper", lost);
            Assert.Contains("why=intercepted:RVN", lost);
            Assert.Contains(w.PendingEvents, e => e.Kind == FactionEventKind.ShipLost && e.ActorTag == "STL");
        }
            // ---- FAC-306: the convoy's firepower decides the encounter ------------------------------

        [Fact]
        public void UnarmedConvoyAtItsOwnNode_IsRobbedButKeepsTheHull()
        {
            WorldModel w = MakeWorld();
            var stRaider = MakeState("RVN");
            var stTrader = MakeState("MERC");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            w.SetTreasury("RVN", 50000);

            DateTime now = w.UtcNow;
            stRaider.PendingFleets.Add(MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now));
            var trade = MakePending(ActionKind.Trade, "MERC", "MERC", "MERC1", now, now);
            trade.ShipPrefabId = "Spadi-Space";
            trade.Cargo = new Dictionary<string, long> { { "Ingot:Iron", 1000 } };
            stTrader.PendingFleets.Add(trade);

            var states = new List<FactionState> { stRaider, stTrader };
            var log    = new List<string>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, AlwaysWin(), log, null);

            string loots = Assert.Single(log, l => l.StartsWith("RAID_LOOTS "));
            Assert.Contains("hull=survived", loots);
            Assert.Contains("victim=weaker", loots);
            Assert.Contains("friendly_node", loots);
            Assert.Contains("loss_chance=0.00", loots);
            Assert.DoesNotContain(log, l => l.StartsWith("SHIP_LOST ") && l.Contains(" MERC "));
            // The convoy carried on: it was at its destination, so it sold and its hull came home in
            // the same pass. The loot LEFT the convoy: half the cargo was looted (loot=1000 at the
            // fallback unit price) and the sale settled the other half for the same amount -- a
            // copied loot would have settled 2000.
            Assert.Contains("loot=1000", loots);
            string resolved = Assert.Single(log, l => l.StartsWith("TRADE_RESOLVED ") && l.Contains(" MERC "));
            Assert.Contains("settled=1000", resolved);
            Assert.Contains(log, l => l.StartsWith("SHIP_RETURNED MERC prefab=Spadi-Space"));
            Assert.Empty(stTrader.PendingFleets);
        }

        [Fact]
        public void ConvoyThatOutgunsThePirate_RepelsItAndCancelsTheRaid()
        {
            WorldModel w = MakeWorld();
            var stRaider = MakeState("RVN");
            var stTrader = MakeState("MERC");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            AddNode(w, "RVN1", "RVN", new Vec3(50, 0, 0));
            w.SetTreasury("RVN", 50000);

            DateTime now = w.UtcNow;
            var raid = MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now);
            raid.WeaponBands = Bands(100);
            raid.ShipPrefabId = "Corsair";
            raid.OriginNodeId = "RVN1";
            stRaider.PendingFleets.Add(raid);
            var trade = MakePending(ActionKind.Trade, "MERC", "MERC", "MERC1", now, now);
            trade.WeaponBands = Bands(1000);
            trade.ShipPrefabId = "Clipper";
            stTrader.PendingFleets.Add(trade);

            var states = new List<FactionState> { stRaider, stTrader };
            var log    = new List<string>();

            // 0.95: passes the 0.98 intercept roll, misses the 0.90 hull-loss roll.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.95), log, null);

            string repel = Assert.Single(log, l => l.StartsWith("CONVOY_REPELS "));
            Assert.Contains("reason=outgunned", repel);
            Assert.Contains("hulls_lost=0/1", repel);
            Assert.Contains("victim=stronger", repel);
            Assert.DoesNotContain(log, l => l.StartsWith("RAID_LOOTS"));
            Assert.Empty(stRaider.PendingFleets);                       // the raid is over
            Assert.Contains(log, l => l.StartsWith("TRADE_RESOLVED ") && l.Contains(" MERC "));   // the convoy carried on
            Assert.DoesNotContain(log, l => l.StartsWith("SHIP_LOST ") && l.Contains(" MERC "));
            Assert.Equal(0.8, w.Graph.Nodes["MERC1"].HoldStrength);     // it never landed
            Assert.Equal(1, w.Graph.Nodes["RVN1"].ShipStock["Corsair"]); // the pirate went home
            Assert.Contains(log, l => l.StartsWith("SHIP_RETURNED RVN") && l.Contains("prefab=Corsair"));
            Assert.Contains(w.PendingEvents, e => e.Kind == FactionEventKind.RaidRepelled && e.ActorTag == "RVN");
        }

        [Fact]
        public void RepelledPirate_LosesHullsOnTheSeries_WeakestFirst()
        {
            WorldModel w = MakeWorld();
            var stRaider = MakeState("RVN");
            var stTrader = MakeState("MERC");
            AddNode(w, "MERC1", "MERC", new Vec3(0, 0, 0), hold: 0.8);
            AddNode(w, "RVN1", "RVN", new Vec3(50, 0, 0));

            DateTime now = w.UtcNow;
            var raid = MakePending(ActionKind.Raid, "RVN", "MERC", "MERC1", now, now);
            raid.WeaponBands = Bands(100);
            raid.ShipPrefabId = "Idris";
            raid.Squadron = new List<string> { "Idris", "Fighter", "Fighter" };
            raid.OriginNodeId = "RVN1";
            stRaider.PendingFleets.Add(raid);
            var trade = MakePending(ActionKind.Trade, "MERC", "MERC", "MERC1", now, now);
            trade.WeaponBands = Bands(1000);
            trade.ShipPrefabId = "Clipper";
            stTrader.PendingFleets.Add(trade);

            var states = new List<FactionState> { stRaider, stTrader };
            var log    = new List<string>();

            // 0.5: first hull rolls under 0.90 (lost), second is not under 0.50, third not under 0.10.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            string repel = Assert.Single(log, l => l.StartsWith("CONVOY_REPELS "));
            Assert.Contains("hulls_lost=1/3", repel);
            string lost = Assert.Single(log, l => l.StartsWith("SHIP_LOST ") && l.Contains(" RVN "));
            Assert.Contains("why=repelled:MERC", lost);
            Assert.Contains("count=1", lost);
            // The catalogue fake has no bands, so the series ran in squadron order: the lead went.
            Assert.Contains("hulls=Idris", lost);
            Assert.Equal(2, w.Graph.Nodes["RVN1"].ShipStock["Fighter"]);
            Assert.False(w.Graph.Nodes["RVN1"].ShipStock.ContainsKey("Idris"));
            Assert.Contains(w.PendingEvents, e => e.Kind == FactionEventKind.ShipLost && e.ActorTag == "RVN");
            // Two of three came home: repelled, with the one loss carried.
            FactionEvent ev = Assert.Single(w.PendingEvents, e => e.Kind == FactionEventKind.RaidRepelled);
            Assert.Equal(1, ev.Amount);
            Assert.Equal(1.0 / 3, ev.Ratio, 6);
        }

        [Fact]
        public void EqualConvoy_WinsTheDuel_AndThePirateIsRepelled()
        {
            WorldModel w = MakeWorld();
            var stRaider = MakeState("RVN");
            var stTrader = MakeState("MERC");
            AddNode(w, "MERC1", "OTH", new Vec3(0, 0, 0), hold: 0.8);
            AddNode(w, "RVN1", "RVN", new Vec3(50, 0, 0));

            DateTime now = w.UtcNow;
            var raid = MakePending(ActionKind.Raid, "RVN", "OTH", "MERC1", now, now);
            raid.WeaponBands = Bands(100);
            raid.ShipPrefabId = "Corsair";
            raid.OriginNodeId = "RVN1";
            stRaider.PendingFleets.Add(raid);
            var trade = MakePending(ActionKind.Trade, "MERC", "OTH", "MERC1", now, now);
            trade.WeaponBands = Bands(100);   // ratio 1.0: inside the equal band
            trade.ShipPrefabId = "Clipper";
            trade.OriginNodeId = "MERC2";
            AddNode(w, "MERC2", "MERC", new Vec3(0, 50, 0));
            stTrader.PendingFleets.Add(trade);

            var states = new List<FactionState> { stRaider, stTrader };
            var log    = new List<string>();

            // A fixed roll makes the duel symmetric: both sides fall below their break-off fractions on
            // the same turn and leave, and a tie goes to the victim (the pirate failed to win).
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.95), log, null);

            string duel = Assert.Single(log, l => l.StartsWith("CONVOY_DUEL "));
            Assert.Contains("result=victim_wins", duel);
            Assert.Contains("victim=equal", duel);
            Assert.Contains("broke_off=both", duel);
            Assert.Contains(log, l => l.StartsWith("CONVOY_REPELS ") && l.Contains("reason=broke_off"));
            Assert.Empty(stRaider.PendingFleets);
            Assert.Contains(log, l => l.StartsWith("TRADE_RESOLVED ") && l.Contains(" MERC "));   // the convoy carried on
            Assert.DoesNotContain(log, l => l.StartsWith("SHIP_LOST ") && l.Contains(" MERC "));
        }

        [Fact]
        public void EqualConvoy_LosesTheDuel_IsRobbedAndRollsTheDuelLossChance()
        {
            WorldModel w = MakeWorld();
            var stRaider = MakeState("RVN");
            var stTrader = MakeState("MERC");
            AddNode(w, "MERC1", "OTH", new Vec3(0, 0, 0), hold: 0.8);
            w.SetTreasury("RVN", 50000);

            DateTime now = w.UtcNow;
            var raid = MakePending(ActionKind.Raid, "RVN", "OTH", "MERC1", now, now);
            raid.WeaponBands = Bands(140);
            stRaider.PendingFleets.Add(raid);
            var trade = MakePending(ActionKind.Trade, "MERC", "OTH", "MERC1", now, now);
            trade.WeaponBands = Bands(100);   // ratio 0.71: equal band, but the pirate hits harder
            trade.ShipPrefabId = "Clipper";
            stTrader.PendingFleets.Add(trade);

            var states = new List<FactionState> { stRaider, stTrader };
            var log    = new List<string>();

            // 0.5 is under the 0.70 duel-loss chance on foreign ground: the hull goes.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            Assert.Contains(log, l => l.StartsWith("CONVOY_DUEL ") && l.Contains("result=pirate_wins"));
            string loots = Assert.Single(log, l => l.StartsWith("RAID_LOOTS "));
            Assert.Contains("hull=lost", loots);
            Assert.Contains("loss_chance=0.70", loots);
            string lost = Assert.Single(log, l => l.StartsWith("SHIP_LOST ") && l.Contains(" MERC "));
            Assert.Contains("why=intercepted:RVN", lost);
            Assert.Empty(stTrader.PendingFleets);
        }

        [Fact]
        public void ClassifyConvoy_UnarmedIsAlwaysWeaker_AndTheBandIsTheTuning()
        {
            FactionTuning t = TestSupport.Tuning();
            Assert.Equal("weaker",   PendingFleetResolver.ClassifyConvoy(0, 1, t));
            Assert.Equal("weaker",   PendingFleetResolver.ClassifyConvoy(0, 0, t));
            Assert.Equal("weaker",   PendingFleetResolver.ClassifyConvoy(60, 100, t));
            Assert.Equal("equal",    PendingFleetResolver.ClassifyConvoy(70, 100, t));
            Assert.Equal("equal",    PendingFleetResolver.ClassifyConvoy(150, 100, t));
            Assert.Equal("stronger", PendingFleetResolver.ClassifyConvoy(160, 100, t));
        }
            [Fact]
        public void ConvoySharingTwoWaypointsWithARaid_IsRobbedOnce_NotAtEveryWaypoint()
        {
            // Trade: TO -> M1 -> M2 -> RO; Raid: RO -> M2 -> M1 -> TO. At t=76 min both have entered
            // M1 and M2 since the last pass, so the meeting loop sees the pair twice. A surviving
            // convoy must not be robbed again at the second waypoint.
            WorldModel w = MakeWorld();
            w.Tuning = SlowFleetTuning();
            AddNode(w, "TO", "MERC", new Vec3(0, 0, 0));
            AddNode(w, "M1", null,   new Vec3(33, 0, 0));
            AddNode(w, "M2", null,   new Vec3(66, 0, 0));
            AddNode(w, "RO", "RVN",  new Vec3(100, 0, 0));
            w.Graph.Connect("TO", "M1"); w.Graph.Connect("M1", "M2"); w.Graph.Connect("M2", "RO");
            w.SetTreasury("RVN", 50000);
            DateTime t0 = w.UtcNow;

            var stMerc = MakeState("MERC");
            stMerc.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "MERC", TargetTag = "RVN", TargetNodeId = "RO",
                OriginNodeId = "TO", OriginPosKm = new Vec3(0,0,0), TargetPosKm = new Vec3(100,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                Cargo = new Dictionary<string, long> { { "Ingot:Iron", 1000 } }, CargoValue = 1000,
                PathNodeIds = new[] { "TO", "M1", "M2", "RO" }, LastResolvedSegment = -1
            });
            var stRvn = MakeState("RVN");
            stRvn.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "TO",
                OriginNodeId = "RO", OriginPosKm = new Vec3(100,0,0), TargetPosKm = new Vec3(0,0,0),
                DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(100),
                PathNodeIds = new[] { "RO", "M2", "M1", "TO" }, LastResolvedSegment = -1
            });
            var states = new List<FactionState> { stMerc, stRvn };
            var log    = new List<string>();

            w.UtcNow = t0.AddMinutes(76);
            // 0.5: passes the 0.80 intercept roll (no bands, no range factor), misses the 0.15 hull roll.
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0.5), log, null);

            string loots = Assert.Single(log, l => l.StartsWith("RAID_LOOTS "));
            Assert.Contains("hull=survived", loots);
            PendingFleetAction after = Assert.Single(stMerc.PendingFleets);
            Assert.Equal(500, after.Cargo["Ingot:Iron"]);   // half, not a quarter
        }
    }
}
