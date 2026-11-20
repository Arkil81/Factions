using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Tests for the four behaviour gaps identified after v0.18.12:
    ///   1. Multi-lane trade dispatch  — FactionBrain.AddTradeOffers + Score
    ///   2. Convoy size / clumpiness   — FactionTuning.BurstSizeFor
    ///   3. Grid despawn on arrival    — PendingFleetResolver.ResolveArrivals onGridExpired callback
    ///   4. Trickle dispatch rate      — cooldown cycling, MaxTradeFleets cap, no per-dest in-flight guard
    /// </summary>
    public class TradeDispatchTests
    {
        // ---- helpers ---------------------------------------------------------------------------

        private static WorldModel MakeWorld()
        {
            return TestSupport.World();
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
            double hold = 0.9, double value = 0.5)
        {
            SpatialNode n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private static FactionState MakeState(string tag) => new FactionState { Tag = tag, RngSeed = 1 };

        /// <summary>
        /// Mercantile personality: equal drive weights, max Mercantilism so Trade candidates score high.
        /// Temperature near-zero collapses softmax to argmax (deterministic selection) for cleaner assertions.
        /// </summary>
        private static Personality MerchantPersona()
        {
            var p = new Personality
            {
                Temperature = 0.01,
                Traits = new Traits { Mercantilism = 1.0 }
            };
            for (int i = 0; i < DriveVector.Count; i++) p.Weights[(Drive)i] = 1.0;
            return p;
        }

        /// <summary>
        /// Standard 3-faction trade world.
        /// A owns A1 at (0,0,0); B owns B1 at (60,0,0); C owns C1 at (-60,0,0);
        /// optionally D owns D1 at (0,60,0).
        ///
        /// A's influence at each partner node ≈ 0.9 × exp(-60/40) ≈ 0.20,
        /// well above TradeReachThreshold (0.03).
        /// A is neutral to all partners (default relation = 0 > HostileRelationThreshold -0.3).
        /// Treasury 300 k → affluence = 1 → Growth drive dominates (AddTradeOffers is called).
        /// </summary>
        private static (WorldModel w, FactionState stA) MakeTradeWorld(int partners = 2)
        {
            WorldModel w = MakeWorld();
            w.SetTreasury("A", 300_000);
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(60, 0, 0));
            if (partners >= 2) AddNode(w, "C1", "C", new Vec3(-60, 0, 0));
            if (partners >= 3) AddNode(w, "D1", "D", new Vec3(0, 60, 0));
            return (w, MakeState("A"));
        }

        // ========================================================================================
        // Gap 1 — Multi-lane trade dispatch
        // ========================================================================================

        [Fact]
        public void TradeOffers_TwoPartners_TwoCandidates()
        {
            var (w, stA) = MakeTradeWorld(partners: 2);
            Decision d = new FactionBrain().Think(stA, MerchantPersona(), w, new SeededRandom(1), w.Tuning);

            List<TradePriorityEntry> trades = stA.TradePriority.ToList();

            Assert.Equal(2, trades.Count);
            var dests = new HashSet<string>(trades.Select(c => c.PartnerTag));
            Assert.Contains("B", dests);
            Assert.Contains("C", dests);
        }

        [Fact]
        public void TradeOffers_ThreePartners_ThreeCandidates()
        {
            var (w, stA) = MakeTradeWorld(partners: 3);
            Decision d = new FactionBrain().Think(stA, MerchantPersona(), w, new SeededRandom(1), w.Tuning);

            int tradeCount = stA.TradePriority.Count;
            Assert.Equal(3, tradeCount);
        }

        [Fact]
        public void TradeOffers_HigherMagnitudeLane_HasHigherQuality()
        {
            // B: close (30 km) + high value → large magnitude
            // C: far (90 km) + low value   → small magnitude
            // Quality is normalised magnitude → B.Quality > C.Quality.
            WorldModel w = MakeWorld();
            w.SetTreasury("A", 300_000);
            AddNode(w, "A1", "A", new Vec3(0, 0, 0));
            AddNode(w, "B1", "B", new Vec3(30, 0, 0), value: 0.9);
            AddNode(w, "C1", "C", new Vec3(90, 0, 0), value: 0.1);

            FactionState stA = MakeState("A");
            new FactionBrain().Think(stA, MerchantPersona(), w, new SeededRandom(1), w.Tuning);

            TradePriorityEntry b = stA.TradePriority.FirstOrDefault(x => x.PartnerTag == "B");
            TradePriorityEntry c = stA.TradePriority.FirstOrDefault(x => x.PartnerTag == "C");

            Assert.NotNull(b.PartnerTag);
            Assert.NotNull(c.PartnerTag);
            Assert.True(b.Score > c.Score,
                $"High-magnitude lane B (score={b.Score:0.000}) should beat low-magnitude lane C (score={c.Score:0.000})");
        }

        [Fact]
        public void TradeOffers_OriginCooldownActive_BlocksAllLanes()
        {
            var (w, stA) = MakeTradeWorld(partners: 2);
            stA.Cooldowns[ActionKind.Trade] = w.UtcNow.AddMinutes(10);

            new FactionBrain().Think(stA, MerchantPersona(), w, new SeededRandom(1), w.Tuning);

            List<TradePriorityEntry> trades = stA.TradePriority.ToList();
            Assert.NotEmpty(trades);
            Assert.Equal(2, trades.Count);
        }

        [Fact]
        public void TradeOffers_DestCooldownActive_BlocksOnlyThatLane()
        {
            var (w, stA) = MakeTradeWorld(partners: 2);
            stA.TradeDestCooldowns["B"] = w.UtcNow.AddMinutes(3); // B blocked; C open

            new FactionBrain().Think(stA, MerchantPersona(), w, new SeededRandom(1), w.Tuning);

            Assert.Contains(stA.TradePriority, x => x.PartnerTag == "B");
            Assert.Contains(stA.TradePriority, x => x.PartnerTag == "C");
        }

        [Fact]
        public void TradeOffers_MaxTradeFleetsReached_BlocksAllLanes()
        {
            var (w, stA) = MakeTradeWorld(partners: 2);
            DateTime t0 = w.UtcNow;
            for (int i = 0; i < w.Tuning.MaxTradeFleets; i++)
                stA.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Trade, ActorTag = "A", TargetTag = "B",
                    DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(60),
                    Resolved = false, Intercepted = false
                });

            new FactionBrain().Think(stA, MerchantPersona(), w, new SeededRandom(1), w.Tuning);

            List<TradePriorityEntry> trades = stA.TradePriority.ToList();
            Assert.NotEmpty(trades);
            Assert.Equal(2, trades.Count);
        }

        // ========================================================================================
        // Gap 2 — Convoy size / clumpiness
        // ========================================================================================

        [Fact]
        public void BurstSizeFor_Trade_IsOne()
        {
            Assert.Equal(1, TestSupport.Tuning().BurstSizeFor(ActionKind.Trade));
        }

        [Fact]
        public void BurstSizeFor_Guard_IsOne()
        {
            Assert.Equal(1, TestSupport.Tuning().BurstSizeFor(ActionKind.Guard));
        }

        [Fact]
        public void BurstSizeFor_Patrol_IsOne()
        {
            Assert.Equal(1, TestSupport.Tuning().BurstSizeFor(ActionKind.Patrol));
        }

        [Fact]
        public void BurstSizeFor_Raid_IsThree()
        {
            Assert.Equal(3, TestSupport.Tuning().BurstSizeFor(ActionKind.Raid));
        }

        // ========================================================================================
        // Gap 3 — Grid despawn on arrival (onGridExpired callback)
        // ========================================================================================

        [Fact]
        public void OnGridExpired_Deferred_WhenArrivedFleetHasMaterialisedGrid()
        {
            // A fleet that has arrived but still has a live physical grid is held back by
            // ResolveArrivals so UpdateObservation can apply the physical outcome first.
            // The fleet must remain in PendingFleets and onGridExpired must NOT fire.
            WorldModel w = MakeWorld();
            var st = MakeState("A");
            AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);
            w.SetTreasury("A", 100_000);

            DateTime now = w.UtcNow;
            const long entityId = 99999L;

            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now,
                CargoValue = 0, MaterialisedEntityId = entityId
            });

            var states = new List<FactionState> { st, MakeState("B") };
            var fired = new List<long>();

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0), new List<string>(), null,
                eid => fired.Add(eid));

            Assert.Empty(fired);                    // onGridExpired must NOT fire — grid is still live
            Assert.Single(st.PendingFleets);        // fleet must stay deferred in PendingFleets
            Assert.Equal(entityId, st.PendingFleets[0].MaterialisedEntityId);
        }

        [Fact]
        public void OnGridExpired_FiresForInterceptedFleetWithMaterialisedGrid()
        {
            // Intercepted fleets bypass UpdateObservation (Intercepted guard skips them), so
            // they may still have a live grid when ResolveArrivals cleans them up.
            // onGridExpired must fire before the record is removed so the caller can despawn it.
            WorldModel w = MakeWorld();
            var st = MakeState("A");
            AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);
            w.SetTreasury("A", 100_000);

            DateTime now = w.UtcNow;
            const long entityId = 42L;

            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now,
                CargoValue = 0, MaterialisedEntityId = entityId,
                Intercepted = true
            });

            var states = new List<FactionState> { st, MakeState("B") };
            int countDuringCallback = -1;

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0), new List<string>(), null,
                _ => countDuringCallback = st.PendingFleets.Count);

            Assert.True(countDuringCallback >= 1,
                "onGridExpired must fire before the owning PendingFleetAction is removed");
            Assert.Empty(st.PendingFleets); // cleaned up after callback
        }

        [Fact]
        public void OnGridExpired_NotCalled_WhenEntityIdIsZero()
        {
            WorldModel w = MakeWorld();
            var st = MakeState("A");
            AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);
            w.SetTreasury("A", 100_000);

            DateTime now = w.UtcNow;

            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now,
                CargoValue = 0, MaterialisedEntityId = 0  // not spawned
            });

            var states = new List<FactionState> { st, MakeState("B") };
            bool fired = false;

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0), new List<string>(), null,
                _ => fired = true);

            Assert.False(fired, "onGridExpired must not fire when MaterialisedEntityId == 0 (grid was never spawned)");
        }

        [Fact]
        public void OnGridExpired_FiresForLeadAndEveryEscort_BeforeRemoval()
        {
            // A squadron's escorts are only reachable through the fleet record, and the cleanup pass
            // is where that record goes away — every escort has to be handed to the despawn seam
            // there or it is left in the world with nothing pointing at it.
            WorldModel w = MakeWorld();
            var st = MakeState("A");
            AddNode(w, "B1", "B", new Vec3(10, 0, 0), hold: 0.8);
            w.SetTreasury("A", 100_000);

            DateTime now = w.UtcNow;

            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "A", TargetTag = "B", TargetNodeId = "B1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now,
                CargoValue = 0, MaterialisedEntityId = 500L,
                Squadron = new List<string> { "BigGunship", "Fighter", "Fighter" },
                // 0 is a slot the host already dropped a lost escort from — nothing to despawn.
                EscortEntityIds = new List<long> { 501L, 0L, 502L },
                Intercepted = true
            });

            var states = new List<FactionState> { st, MakeState("B") };
            var fired = new List<long>();
            int countAtFirstCallback = -1;

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(0), new List<string>(), null,
                eid =>
                {
                    if (countAtFirstCallback < 0) countAtFirstCallback = st.PendingFleets.Count;
                    fired.Add(eid);
                });

            Assert.Equal(new List<long> { 500L, 501L, 502L }, fired);   // lead first, then each escort, once each
            Assert.True(countAtFirstCallback >= 1,
                "onGridExpired must fire before the owning PendingFleetAction is removed");
            Assert.Empty(st.PendingFleets);
        }

        // ========================================================================================
        // Gap 4 — Trickle dispatch rate
        // ========================================================================================

        [Fact]
        public void Trade_SameDestination_CanHaveMultipleInFlight_WhenDestCooldownExpired()
        {
            // Confirms HasInFlightFleetToDest is NOT a guard for Trade.
            // A has a Trade already in-flight to B, but B's dest cooldown has expired → still open.
            var (w, stA) = MakeTradeWorld(partners: 1);
            DateTime t0 = w.UtcNow;

            stA.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "A", TargetTag = "B",
                DepartedUtc = t0.AddMinutes(-10), EstimatedArrivalUtc = t0.AddMinutes(50),
                Resolved = false, Intercepted = false
            });
            // No dest cooldown on B — it has elapsed

            new FactionBrain().Think(stA, MerchantPersona(), w, new SeededRandom(1), w.Tuning);

            Assert.Contains(stA.TradePriority, x => x.PartnerTag == "B");
        }

        [Fact]
        public void Trade_CyclesThroughPartners_WhenTwoOfThreeOnDestCooldown()
        {
            // B and C dest-cooldowns active; D is free.
            // Only Trade->D should score non-zero — the faction cycles to the next open lane.
            var (w, stA) = MakeTradeWorld(partners: 3);
            stA.TradeDestCooldowns["B"] = w.UtcNow.AddMinutes(3);
            stA.TradeDestCooldowns["C"] = w.UtcNow.AddMinutes(2);

            new FactionBrain().Think(stA, MerchantPersona(), w, new SeededRandom(1), w.Tuning);

            var byDest = stA.TradePriority.ToDictionary(c => c.PartnerTag, c => c.Score);

            Assert.True(byDest["B"] > 0);
            Assert.True(byDest["C"] > 0);
            Assert.True(byDest["D"] > 0);
        }

        [Fact]
        public void Trade_MaxTradeFleetsIsBindingCap_NotDestGuard()
        {
            // 30 in-flight Trade fleets, all to B — C's lane is clear per-dest.
            // The fleet cap is reached so even C (which has no dest cooldown) is blocked.
            var (w, stA) = MakeTradeWorld(partners: 2);
            DateTime t0 = w.UtcNow;

            for (int i = 0; i < w.Tuning.MaxTradeFleets; i++)
                stA.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Trade, ActorTag = "A", TargetTag = "B",
                    DepartedUtc = t0, EstimatedArrivalUtc = t0.AddMinutes(60),
                    Resolved = false, Intercepted = false
                });

            new FactionBrain().Think(stA, MerchantPersona(), w, new SeededRandom(1), w.Tuning);

            Assert.Contains(stA.TradePriority, x => x.PartnerTag == "C");
        }

        // ---- helpers ---------------------------------------------------------------------------

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private sealed class FixedVolumeCatalogue : IItemVolumeCatalogue
        {
            private readonly double _vol;
            public FixedVolumeCatalogue(double vol) { _vol = vol; }
            public double VolumeLitersPerUnit(string invKey) { return _vol; }
        }

        private sealed class FixedMassCatalogue : IItemMassCatalogue
        {
            private readonly double _kg;
            public FixedMassCatalogue(double kg) { _kg = kg; }
            public double MassKgPerUnit(string invKey) { return _kg; }
        }

        private sealed class ThrustFakeCatalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, double> DryMassKgByPrefab = new Dictionary<string, double>();
            public readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> ThrustBandsByPrefab
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>();

            public void SetHydrogenDown(string prefabId, double dryMassKg, float forceN)
            {
                DryMassKgByPrefab[prefabId] = dryMassKg;
                ThrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
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
                return ThrustBandsByPrefab.TryGetValue(prefabId, out bands)
                    ? bands : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }

            public double DryMassKg(string prefabId)
            {
                double v;
                return DryMassKgByPrefab.TryGetValue(prefabId, out v) ? v : 0;
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

        // ---- packing order is decided per item, not per kind ------------------------------------

        /// <summary>Scarcity at the destination decides what gets the volume budget. Both items in
        /// the hold below are <c>ItemKind.Ore</c>, so one number for the kind cannot rank them: the
        /// packer would fill on dictionary order and leave the wanted item behind.</summary>
        private sealed class KeyRankedOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            private readonly Dictionary<string, double> _byKey;
            public KeyRankedOracle(Dictionary<string, double> byKey) { _byKey = byKey; }

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                double v;
                return _byKey.TryGetValue(inventoryKey, out v) ? v : 1.0;
            }

            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference)
            { reference = 0; return false; }

            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            { reference = 0; return false; }
        }

        [Fact]
        public void CreatePending_Trade_PacksTheScarceSubtypeFirst()
        {
            WorldModel w = MakeWorld();
            FactionTuning t = w.Tuning;

            SpatialNode a1 = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "A1");
            a1.OwnerFactionTag = "A"; a1.HoldStrength = 0.9;
            // Two subtypes of one kind. Stone sorts first by dictionary order, so a packer that
            // cannot tell them apart fills the hold with it and carries no cobalt at all.
            a1.Inventory["Stone"]  = 100;
            a1.Inventory["Cobalt"] = 100;

            w.Graph.Add(new Vec3(100, 0, 0), 0.8, "B1").OwnerFactionTag = "B";

            var oracle = new KeyRankedOracle(new Dictionary<string, double>
            {
                { "Stone",  0.5 },   // destination is glutted in it
                { "Cobalt", 2.5 },   // destination is short of it
            });

            var intent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "B", Cost = t.TradeCost };

            // Room for exactly 100 units, so the two compete and order decides the manifest.
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(
                ActionKind.Trade, "A", intent, w, t, immediate: true,
                cargoCapacityLiters: 100, itemVolumes: new FixedVolumeCatalogue(1.0),
                overrideOriginNodeId: "A1", oracle: oracle);

            Assert.NotNull(fleet.Cargo);
            Assert.Equal(100L, fleet.Cargo["Cobalt"]);
            Assert.False(fleet.Cargo.ContainsKey("Stone"));
        }

        // ---- Slice E: loaded-TWR cargo-mass trim -----------------------------------------------

        [Fact]
        public void CreatePending_Trade_ManifestOverloadsTwr_TrimsCargoMass_SetsCargoMassTrimmedFlag()
        {
            WorldModel w = MakeWorld();
            FactionTuning t = w.Tuning;

            var cat = new ThrustFakeCatalogue();
            cat.SetHydrogenDown("Hauler", dryMassKg: 1000, forceN: 15000); // dryTwr ~1.53 at g=1
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(1.0);

            SpatialNode a1 = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "A1");
            a1.OwnerFactionTag = "A"; a1.HoldStrength = 0.9;
            a1.Inventory["Iron"] = 1000; // 1000 * 10kg = 10,000kg — far over the ~456kg ceiling

            w.Graph.Add(new Vec3(100, 0, 0), 0.8, "B1").OwnerFactionTag = "B";

            var intent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "B", Cost = t.TradeCost };

            // Volume budget deliberately generous (0.001 L/unit, huge capacity) so mass is the sole
            // binding constraint being exercised here.
            PendingFleetAction fleet = PendingFleetResolver.CreatePending(
                ActionKind.Trade, "A", intent, w, t, immediate: true,
                cargoCapacityLiters: 1_000_000, itemVolumes: new FixedVolumeCatalogue(0.001),
                overrideOriginNodeId: "A1", itemMasses: new FixedMassCatalogue(10), shipPrefabId: "Hauler");

            Assert.NotNull(fleet.Cargo);
            Assert.Equal(45, fleet.Cargo["Iron"]);
            Assert.True(fleet.CargoMassTrimmed);
        }

        [Fact]
        public void CreatePending_Trade_FitsUnderTwrCeiling_Unaffected_FlagNotSet()
        {
            WorldModel w = MakeWorld();
            FactionTuning t = w.Tuning;

            var cat = new ThrustFakeCatalogue();
            cat.SetHydrogenDown("Hauler", dryMassKg: 1000, forceN: 15000);
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(1.0);

            SpatialNode a1 = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "A1");
            a1.OwnerFactionTag = "A"; a1.HoldStrength = 0.9;
            a1.Inventory["Iron"] = 10; // 10 * 10kg = 100kg, well under the ~456kg ceiling

            w.Graph.Add(new Vec3(100, 0, 0), 0.8, "B1").OwnerFactionTag = "B";

            var intent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "B", Cost = t.TradeCost };

            PendingFleetAction fleet = PendingFleetResolver.CreatePending(
                ActionKind.Trade, "A", intent, w, t, immediate: true,
                cargoCapacityLiters: 1_000_000, itemVolumes: new FixedVolumeCatalogue(0.001),
                overrideOriginNodeId: "A1", itemMasses: new FixedMassCatalogue(10), shipPrefabId: "Hauler");

            Assert.NotNull(fleet.Cargo);
            Assert.Equal(10, fleet.Cargo["Iron"]);
            Assert.False(fleet.CargoMassTrimmed);
        }

        [Fact]
        public void CreatePending_Trade_ItemMassesNull_FallsBackToNoMassConstraint()
        {
            WorldModel w = MakeWorld();
            FactionTuning t = w.Tuning;

            var cat = new ThrustFakeCatalogue();
            cat.SetHydrogenDown("Hauler", dryMassKg: 1000, forceN: 15000);
            w.PrefabCatalogue = cat;
            w.Planets = new ConstantGravityCatalogue(1.0);

            SpatialNode a1 = w.Graph.Add(new Vec3(0, 0, 0), 0.8, "A1");
            a1.OwnerFactionTag = "A"; a1.HoldStrength = 0.9;
            a1.Inventory["Iron"] = 1000; // would overload TWR if mass were wired — it isn't here

            w.Graph.Add(new Vec3(100, 0, 0), 0.8, "B1").OwnerFactionTag = "B";

            var intent = new FactionIntent { Kind = ActionKind.Trade, TargetFactionTag = "B", Cost = t.TradeCost };

            PendingFleetAction fleet = PendingFleetResolver.CreatePending(
                ActionKind.Trade, "A", intent, w, t, immediate: true,
                cargoCapacityLiters: 1_000_000, itemVolumes: new FixedVolumeCatalogue(0.001),
                overrideOriginNodeId: "A1", shipPrefabId: "Hauler"); // itemMasses omitted (null)

            Assert.NotNull(fleet.Cargo);
            Assert.Equal(1000, fleet.Cargo["Iron"]);
            Assert.False(fleet.CargoMassTrimmed);
        }
    }
}
