using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Verifies ShipyardManager proactive ship demand posting and the existing
    /// build-queue lifecycle (free build, BOM collection, investment escalation).
    /// </summary>
    public class ShipyardManagerTests
    {
        // ---- helpers -----------------------------------------------------------------------

        private static WorldModel MakeWorld()
        {
            var w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return w;
        }

        private static FactionState MakeState(string tag = "F")
            => new FactionState { Tag = tag };

        /// <summary>Ship stock is unified and tag-based now (see ShipPool) — proactive-demand
        /// counting requires a catalogue that classifies stocked prefabs as cargo/combat suitable,
        /// not just a nonzero count under a role-named key. These early tests stock prefabs under
        /// the literal ids "Trade"/"Raid"/"Patrol"; this catalogue classifies each by that literal
        /// id so the pre-unification "any stock under this role name satisfies the role" semantics
        /// still hold for them (Trade = cargo-only, Raid/Patrol = combat-only — matching the real
        /// prefab catalogue's mutual exclusivity, not a permissive "suitable for everything" stub).
        /// The "" (free-build/unknown-prefab) key is classified as both — production never hits it
        /// (FactionRuntime always wires a real PrefabListPicker), it's only reachable when these
        /// tests use a bare `new ShipyardManager()`, and a test wiring this catalogue is exercising
        /// "does stock get recognized on the next tick," not prefab-identity specifics.</summary>
        private sealed class AllSuitablePrefabCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return (prefabId == "Trade" || prefabId == "") ? 1000 : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
                => (prefabId == "Raid" || prefabId == "Patrol" || prefabId == "") ? new[] { new WeaponBand { Dps = 500, RangeM = 800 } } : new WeaponBand[0];
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private sealed class FakeVolumeCatalogue : IItemVolumeCatalogue
        {
            private readonly Dictionary<string, double> _litersByKey = new Dictionary<string, double>();
            public FakeVolumeCatalogue Set(string invKey, double litersPerUnit) { _litersByKey[invKey] = litersPerUnit; return this; }
            public double VolumeLitersPerUnit(string invKey)
            {
                double v;
                return _litersByKey.TryGetValue(invKey, out v) ? v : 0;
            }
        }

        private static SpatialNode AddStation(WorldModel world, string id, string tag, Vec3? pos = null)
        {
            SpatialNode n = world.Graph.Add(pos ?? new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength    = 1.0;
            n.StableStationId = 42;
            return n;
        }

        // ---- proactive demand: no stock, no Shipyard ---------------------------------------

        [Fact]
        public void NoShipStock_NoShipyard_PostsInvestmentRequest_NotShipDemand()
        {
            // When there is no Shipyard anywhere, the existing per-demand path escalates to
            // an InvestmentRequest. The proactive path should still post the Ship: demand so
            // the escalation can fire — it just cannot build immediately.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            // No Shipyard capability, no ship stock.

            var mgr   = new ShipyardManager();
            var log   = new List<string>();
            var state = MakeState();
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            // Trade always gets its own demand. Raid+Patrol share one combined-pool demand per
            // tick (see the ProactiveDemand_* tests below) — with both empty at posture=0, the
            // tie resolves to Raid; Patrol only gets demanded once it's the thinner role.
            List<DemandEntry> demands = world.Demand.SnapshotShipDemands("F");
            Assert.Contains(demands, d => d.Kind == "Ship:Trade" && d.NodeId == "N1");
            Assert.Contains(demands, d => d.Kind == "Ship:Raid"  && d.NodeId == "N1");
            Assert.DoesNotContain(demands, d => d.Kind == "Ship:Patrol");
            // Proactive log lines must appear.
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("Trade"));
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("Raid"));
        }

        [Fact]
        public void NoShipStock_WithShipyard_PostsDemandAtShipyardNode()
        {
            // With a Shipyard present in free-build mode, demands are posted and immediately
            // fulfilled in the same tick (free build = no BOM). Verify via log and ShipStock.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var mgr   = new ShipyardManager();
            var log   = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            // Proactive demands were posted (log evidence) and immediately fulfilled (free build).
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("Trade"));
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("Raid"));
            // Ships should now be in stock. Free build with no PrefabListPicker credits the
            // unknown-prefab "" bucket, not a role-named key.
            int stock;
            node.ShipStock.TryGetValue("", out stock);
            Assert.True(stock >= 1, "Trade ship should be built by free-build this tick");
        }

        [Fact]
        public void HasTradeStock_DoesNotPostTradeDemand_ButPostsCombatRole()
        {
            // Trade stock present → no Trade demand posted. Raid+Patrol share one combined-pool
            // demand; both empty at posture=0 → tie resolves to Raid, which gets demanded and
            // immediately free-built. Patrol stays undemanded until it's the thinner role.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            node.ShipStock["Trade"] = 1; // already have a trade ship
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            // Trade must NOT have been proactively demanded.
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("Trade"));
            // Raid should have been demanded (and free-built); Patrol should not.
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("Raid"));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("Patrol"));
            // Free build (no PrefabListPicker wired) credits the unknown-prefab "" bucket, not a
            // role-named key.
            int raidStock;
            node.ShipStock.TryGetValue("", out raidStock);
            Assert.True(raidStock >= 1, "Raid ship should be free-built this tick");
        }

        [Fact]
        public void AllRolesStocked_PostsNoDemands()
        {
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            node.ShipStock["Trade"]  = 2;
            node.ShipStock["Raid"]   = 1;
            node.ShipStock["Patrol"] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            List<DemandEntry> demands = world.Demand.SnapshotShipDemands("F");
            Assert.Empty(demands);
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE"));
        }

        [Fact]
        public void Expiry_RefundsReservedInputsAndClearsRecipeProgress_ToRoutableInventory()
        {
            // ProductionManager.ServiceShipBuildReservations earmarks raw recipe input into a build
            // entry's own isolated bucket (BuildQueueEntry.ReservedInputs) -- once the entry itself
            // expires and is removed, that stock must be returned to routable inventory the same way
            // Collected already is, or it would be silently destroyed along with the entry.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");

            var entry = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade",
                CreatedUtc = world.UtcNow.AddHours(-2),
                // Already past the no-progress limit. Elapsed time alone no longer abandons anything,
                // so this is now the only way to stage an about-to-be-abandoned entry.
                StalledSeconds = world.Tuning.BuildNoProgressHours * 3600.0 + 1.0,
            };
            entry.Bom["Comp:SteelPlate"] = 200;
            entry.ReservedInputs["Ingot:Iron"] = 150;
            entry.RecipeProgress["SteelPlate"] = new BuildRecipeSlot { Accum = 0.6, LastTick = world.UtcNow };
            node.BuildQueue.Add(entry);

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Empty(node.BuildQueue);
            long ironBack;
            node.Inventory.TryGetValue("Ingot:Iron", out ironBack);
            Assert.Equal(150, ironBack);
        }

        [Fact]
        public void ProactiveDemand_IsIdempotent_SecondTickDoesNotDuplicate()
        {
            // Posting the same demand twice within its TTL must not produce duplicate log lines.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();

            var mgr   = new ShipyardManager();
            var log   = new List<string>();
            var state = MakeState();
            var states = new List<FactionState> { state };

            mgr.Tick(states, world, world.Tuning, log);
            int firstCount = log.FindAll(l => l.Contains("SHIP_DEMAND_PROACTIVE")).Count;

            mgr.Tick(states, world, world.Tuning, log);
            int secondCount = log.FindAll(l => l.Contains("SHIP_DEMAND_PROACTIVE")).Count;

            // Second tick: demands already exist (not expired) → isNew=false → no new log lines.
            Assert.Equal(firstCount, secondCount);
        }

        [Fact]
        public void NoStation_PostsNoDemand()
        {
            // Without a station (StableStationId == 0 on all nodes), there is no sensible
            // target node and proactive posting should be skipped entirely.
            var world = MakeWorld();
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            n.OwnerFactionTag = "F";
            n.HoldStrength    = 1.0;
            n.StableStationId = 0; // not a founded station

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Empty(world.Demand.SnapshotShipDemands("F"));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE"));
        }

        [Fact]
        public void ShipyardNodePreferredOverFallback_WhenBothExist()
        {
            // If there is both a plain station and a Shipyard node, demands go to the Shipyard.
            var world   = MakeWorld();
            var plain   = AddStation(world, "Plain", "F");      // station, no Shipyard
            var yard    = AddStation(world, "Yard",  "F");
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            List<DemandEntry> demands = world.Demand.SnapshotShipDemands("F");
            Assert.All(demands, d => Assert.Equal("Yard", d.NodeId));
        }

        // ---- Trade demand is checked per-Shipyard-node, not network-wide (2026-08-10 fix) ---

        [Fact]
        public void ProactiveDemand_TwoShipyardsDifferentZones_OnlyUnderservedNodeGetsTradeDemand()
        {
            // "Surf" sits at 1.0g (an Ion-only hull fails there); "Deep" sits far away at 0g (the
            // same Ion-only hull is fine there). The faction's only cargo-suitable stock is
            // Ion-only. Under the old network-wide "any cargo ship anywhere" check both nodes would
            // be considered served; per-node checking must post a Trade demand only for "Surf".
            var world = MakeWorld();
            world.Planets = new TwoZoneCatalogue();

            var surf = AddStation(world, "Surf", "F", pos: new Vec3(0, 0, 0));
            surf.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            var deep = AddStation(world, "Deep", "F", pos: new Vec3(1000, 0, 0));
            deep.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("Ion_Hauler", dryMassKg: 1000, forceN: 5000); // TWR ~0.10 at g=1 -- fails; fine at g=0
            prefabs.CargoLitersByPrefab["Ion_Hauler"] = 1000;
            world.PrefabCatalogue = prefabs;
            // Stock's physical location doesn't matter to the check -- it's a prefab-physics
            // feasibility test against each Shipyard's own environment, not a proximity check.
            deep.ShipStock["Ion_Hauler"] = 1;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            // Free-build mode (no PrefabListPicker wired) completes and consumes the demand in the
            // same tick it's posted -- assert via the proactive-post log line, not a post-Tick
            // register snapshot (mirrors NoShipStock_WithShipyard_PostsDemandAtShipyardNode above).
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Trade") && l.Contains("node=Surf"));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Trade") && l.Contains("node=Deep"));
        }

        [Fact]
        public void ProactiveDemand_SingleShipyard_RouteFeasibleTradeStockExists_NoNewDemand_RegressionGuard()
        {
            // Single Shipyard at 1.0g with a genuinely route-feasible (Hydrogen) cargo ship already
            // in stock -- the low "don't stockpile" floor still holds, now correctly checked via
            // real route physics instead of a bare existence check.
            var world = MakeWorld();
            world.Planets = new TwoZoneCatalogue();

            var surf = AddStation(world, "Surf", "F", pos: new Vec3(0, 0, 0));
            surf.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetHydrogenDown("Hydrogen_Hauler", dryMassKg: 1000, forceN: 15000);
            prefabs.CargoLitersByPrefab["Hydrogen_Hauler"] = 1000;
            world.PrefabCatalogue = prefabs;
            surf.ShipStock["Hydrogen_Hauler"] = 1;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Trade"));
        }

        [Fact]
        public void ProactiveDemand_Trade_PostsRequiredZoneAndPlanetMetadataFromTheShipyardNode()
        {
            // The posted DemandEntry must carry the demanding Shipyard node's own PlanetId/Zone/
            // cargo requirement, not blank/default metadata -- Dashboard and any future consumer
            // depend on this being populated, not just the gating decision behind it. Free-build
            // mode consumes the DemandEntry the same tick it's posted (see the test above), so this
            // asserts via the log line -- built from the exact same MinCargoLiters/RequiredPlanetId/
            // RequiredZone locals PostShipDemand also puts on the DemandEntry, so a correct log line
            // is equally strong evidence the struct fields were populated correctly.
            var world = MakeWorld();
            var surf = AddStation(world, "Surf", "F");
            surf.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            surf.PlanetId = "Terra";
            surf.Zone     = NodeZone.Surface;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Trade")
                && l.Contains("node=Surf") && l.Contains("zone=Surface") && l.Contains("planet=Terra"));
        }

        // ---- existing build queue lifecycle (free-build path) -------------------------------

        [Fact]
        public void FreeBuild_CompletesImmediately_WhenNoPrefabCatalogue()
        {
            // Without a PrefabListPicker the manager picks free-build mode (BOM empty).
            // The ship should be added to ShipStock in the same tick as the demand.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var mgr   = new ShipyardManager(); // no PrefabListPicker → free build
            var log   = new List<string>();
            var state = MakeState();
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            // Proactive tick posts the demand; the same tick should fulfil it (free build).
            // Free build with no PrefabListPicker credits the unknown-prefab "" bucket, not a
            // role-named key — see ShipPool's doc on empty-string keys.
            int stock;
            node.ShipStock.TryGetValue("", out stock);
            Assert.True(stock >= 1, "Free-build Trade ship should be built this tick");
            Assert.Contains(log, l => l.Contains("SHIP_BUILT") && l.Contains("Trade"));
        }

        [Fact]
        public void OrphanedBuildQueueEntry_StillAbsorbsAndCompletes_WhenNoLiveDemandTargetsItsNode()
        {
            // A BuildQueueEntry can persist at a node after the brain's network-wide need for that
            // role is already satisfied elsewhere (or a second Shipyard node loses
            // PostProactiveShipDemands' single-node-per-role pick — see
            // ShipyardNodePreferredOverFallback_WhenBothExist). The entry must keep absorbing
            // available stock and finish, not freeze forever with matching components piling up
            // untouched right next to it (live-observed, IDIN Alder Station, 2026-08-05).
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            // Trade stock already sufficient network-wide -> PostProactiveShipDemands never posts
            // Ship:Trade anywhere this tick, so nothing will ever re-target this pre-existing entry
            // via the demand-driven loop.
            node.ShipStock["Trade"] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();

            var entry = new BuildQueueEntry
            {
                Id         = "Trade:001",
                Role       = "Trade",
                PrefabId   = "SomePrefab",
                CreatedUtc = world.UtcNow.AddHours(-1),
                ExpiresUtc = world.UtcNow.AddHours(23), // well within MaxBuildDurationHours
            };
            entry.Bom["Comp:SteelPlate"] = 100;
            entry.Collected["Comp:SteelPlate"] = 40; // partially collected on an earlier tick
            node.BuildQueue.Add(entry);
            node.Inventory["Comp:SteelPlate"] = 1000; // plenty sitting right there, unabsorbed

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Empty(node.BuildQueue); // completed and removed
            int stock;
            node.ShipStock.TryGetValue("SomePrefab", out stock);
            Assert.Equal(1, stock);
            Assert.Equal(940, node.Inventory["Comp:SteelPlate"]); // 1000 - (100 - 40) absorbed
            Assert.Contains(log, l => l.Contains("SHIP_BUILT") && l.Contains("Trade") && l.Contains("N1"));
        }

        [Fact]
        public void BomAbsorption_CreditsDemandHistory_ItemVolumeWeighted()
        {
            // DemandHistory's gravity term is fed by actual consumption, not delivery (see
            // docs/material-vectors.md's dated correction note) -- BOM absorption is one of the two
            // real feed points (the other is ProductionManager's recipe-input use). Pins that
            // absorbing a component credits a pending, item-volume-weighted DemandHistory entry.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            node.ShipStock["Trade"] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();

            var entry = new BuildQueueEntry
            {
                Id         = "Trade:001",
                Role       = "Trade",
                PrefabId   = "SomePrefab",
                CreatedUtc = world.UtcNow.AddHours(-1),
                ExpiresUtc = world.UtcNow.AddHours(23),
            };
            entry.Bom["Comp:SteelPlate"] = 100;
            entry.Collected["Comp:SteelPlate"] = 40;
            node.BuildQueue.Add(entry);
            node.Inventory["Comp:SteelPlate"] = 1000;

            var volumes = new FakeVolumeCatalogue().Set("Comp:SteelPlate", 3.0);
            var mgr = new ShipyardManager(volumes);
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            // 60 units absorbed (100 - 40 already collected), same as the sibling test above.
            Dictionary<string, double> credited = world.DrainPendingDemandHistory("N1");
            Assert.NotNull(credited);
            Assert.Equal(60.0 * 3.0, credited["Comp:SteelPlate"], 6);
        }

        // ---- investment escalation ---------------------------------------------------------

        [Fact]
        public void NoNetworkShipyard_PostsInvestmentRequest_WhenShipDemandExists()
        {
            // When a Ship: demand exists but no Shipyard is available network-wide,
            // an InvestmentRequest for Shipyard must be posted.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            // No Shipyard capability.

            var mgr   = new ShipyardManager();
            var log   = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            List<InvestmentRequest> reqs = world.Investments.SnapshotFor("F");
            Assert.Contains(reqs, r => r.Cap == NodeCapabilityKind.Shipyard);
            Assert.Contains(log, l => l.Contains("INVEST_REQUEST") && l.Contains("Shipyard"));
        }

        /// <summary>FAC-482. A player faction with no Shipyard anywhere, opted in to something else:
        /// the first-yard request goes through the opt-in gate like every other Shipyard request, so
        /// nothing is posted and the install BOM never becomes a claim. One refusal line however many
        /// demand groups reach the branch.</summary>
        [Fact]
        public void NoNetworkShipyard_PlayerNotOptedIn_PostsNoShipyardRequest()
        {
            var world = MakeWorld();
            AddStation(world, "N1", "EXP");
            var state = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            state.ReservationOptIn.Add(NodeCapabilityKind.ManufactoryII);

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.DoesNotContain(world.Investments.SnapshotFor("EXP"), r => r.Cap == NodeCapabilityKind.Shipyard);
            Assert.Single(log, l => l.Contains("INVEST_REQUEST Shipyard") && l.Contains("refused=opt-in"));
            Assert.DoesNotContain(log, l => l.Contains("INVEST_REQUEST Shipyard") && !l.Contains("refused=opt-in"));
        }

        [Fact]
        public void NoNetworkShipyard_PlayerOptedIn_PostsShipyardRequest()
        {
            var world = MakeWorld();
            AddStation(world, "N1", "EXP");
            var state = new FactionState { Tag = "EXP", IsPlayerFaction = true };
            state.ReservationOptIn.Add(NodeCapabilityKind.Shipyard);

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(world.Investments.SnapshotFor("EXP"), r => r.Cap == NodeCapabilityKind.Shipyard);
            Assert.DoesNotContain(log, l => l.Contains("refused=opt-in"));
        }

        // ---- Muster posture bias (SelectPrefabBom) ------------------------------------------

        /// <summary>Minimal fake exposing one recipe per prefab id, cost = sum of input amounts —
        /// enough for SelectPrefabBom's TotalCost/HasRecipe logic without a real SE blueprint.</summary>
        private sealed class FakeRecipeRegistry : IRecipeRegistry
        {
            public readonly Dictionary<string, long> CostByPrefab = new Dictionary<string, long>();
            /// <summary>A prefab's components by subtype, for a test that needs more than one kind;
            /// takes precedence over <see cref="CostByPrefab"/>.</summary>
            public readonly Dictionary<string, Dictionary<string, long>> BomByPrefab = new Dictionary<string, Dictionary<string, long>>();
            public IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId)
            {
                Dictionary<string, long> parts;
                if (outputTypeId == "Prefab" && BomByPrefab.TryGetValue(outputSubTypeId, out parts))
                {
                    var inputs = new List<RecipeItem>();
                    foreach (KeyValuePair<string, long> kv in parts)
                        inputs.Add(new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = kv.Key, Amount = kv.Value });
                    yield return new ConversionRecipe { BlueprintId = outputSubTypeId, Inputs = inputs.ToArray(), Outputs = new RecipeItem[0] };
                    yield break;
                }
                long cost;
                if (outputTypeId != "Prefab" || !CostByPrefab.TryGetValue(outputSubTypeId, out cost))
                    yield break;
                yield return new ConversionRecipe
                {
                    BlueprintId = outputSubTypeId,
                    Inputs = new[] { new RecipeItem { TypeId = "MyObjectBuilder_Component", SubTypeId = "Steel", Amount = cost } },
                    Outputs = new RecipeItem[0],
                };
            }
            public IEnumerable<ConversionRecipe> AllRecipes { get { yield break; } }
            public int Count { get { return CostByPrefab.Count; } }
        }

        private sealed class FakePrefabCatalogue : IPrefabCatalogue, IPrefabVersionCatalogue
        {
            /// <summary>Published versions: id → (family, version). Empty means none, and every id
            /// is its own family.</summary>
            public readonly Dictionary<string, KeyValuePair<string, int>> Versions
                = new Dictionary<string, KeyValuePair<string, int>>();

            public string FamilyOf(string prefabId)
            {
                KeyValuePair<string, int> v;
                return prefabId != null && Versions.TryGetValue(prefabId, out v) ? v.Key : prefabId;
            }
            public int VersionOf(string prefabId)
            {
                KeyValuePair<string, int> v;
                return prefabId != null && Versions.TryGetValue(prefabId, out v) ? v.Value : 1;
            }
            public string LatestFor(string prefabId, string factionTag)
            {
                var family = new List<PrefabVersionMember>();
                foreach (KeyValuePair<string, KeyValuePair<string, int>> kv in Versions)
                    if (kv.Value.Key == FamilyOf(prefabId))
                        family.Add(new PrefabVersionMember { Id = kv.Key, Version = kv.Value.Value });
                return PrefabVersions.Latest(prefabId, factionTag, family);
            }

            public readonly Dictionary<string, float> DpsByPrefab = new Dictionary<string, float>();
            public readonly Dictionary<string, double> DryMassKgByPrefab = new Dictionary<string, double>();
            public readonly Dictionary<string, long> CargoLitersByPrefab = new Dictionary<string, long>();
            public readonly Dictionary<string, double> DrillYieldScoreByPrefab = new Dictionary<string, double>();
            public readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> ThrustBandsByPrefab
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>();

            public void SetAtmoOnlyDown(string prefabId, double dryMassKg, float forceN)
            {
                DryMassKgByPrefab[prefabId] = dryMassKg;
                ThrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                    {
                        ThrusterType = "Atmospheric", ForceMagnitudeN = forceN,
                        MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
                        EffectivenessAtMinInfluence = 0f, EffectivenessAtMaxInfluence = 1f,
                        NeedsAtmosphereForInfluence = true,
                    }},
                };
            }

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

            /// <summary>Vanilla-shaped Ion curve: full effectiveness in deep space, ~20% at a
            /// planet surface — the real-catalogue curve behind the live stranded-stock finding.</summary>
            public void SetIonDown(string prefabId, double dryMassKg, float forceN)
            {
                DryMassKgByPrefab[prefabId] = dryMassKg;
                ThrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                    {
                        ThrusterType = "Ion", ForceMagnitudeN = forceN,
                        MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 1f,
                        EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 0.2f,
                        NeedsAtmosphereForInfluence = false,
                    }},
                };
            }

            public long CargoLiters(string prefabId)
            {
                long v;
                return CargoLitersByPrefab.TryGetValue(prefabId, out v) ? v : 0;
            }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                float dps;
                if (!DpsByPrefab.TryGetValue(prefabId, out dps) || dps <= 0) return new WeaponBand[0];
                return new[] { new WeaponBand { Dps = dps, RangeM = 800 } };
            }
            public readonly Dictionary<string, string> GridSizeByPrefab = new Dictionary<string, string>();
            public string GridSize(string prefabId)
            {
                string size;
                return GridSizeByPrefab.TryGetValue(prefabId, out size) ? size : "Small";
            }

            /// <summary>Settable universal ("All"-hint) candidate pool per role — mirrors
            /// <c>SePrefabCatalogue.GetNamesForRole</c>'s real fallback semantics enough for
            /// <see cref="ShipyardManager.SelectPrefabBom"/>'s universal-pool rescue tests: only
            /// the <c>"All"</c> hint returns anything, and only whatever a test explicitly wires.</summary>
            public readonly Dictionary<string, string[]> NamesForRoleAll = new Dictionary<string, string[]>();
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint)
            {
                string[] names;
                return factionHint == "All" && NamesForRoleAll.TryGetValue(role, out names) ? names : new string[0];
            }

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

            public double DrillYieldScore(string prefabId)
            {
                double v;
                return DrillYieldScoreByPrefab.TryGetValue(prefabId, out v) ? v : 0;
            }
        }

        private sealed class ConstantGravityCatalogue : IPlanetCatalogue
        {
            private readonly double _g;
            private readonly PlanetRecord[] _planets;

            public ConstantGravityCatalogue(double g, bool inAtmosphereEverywhere)
            {
                _g = g;
                _planets = inAtmosphereEverywhere
                    ? new[] { new PlanetRecord { Center = new Vec3(0, 0, 0), SurfaceRadius = 1,
                        GravityRadius = 1_000_000, AtmosphereRadius = 1_000_000, HasAtmosphere = true } }
                    : new PlanetRecord[0];
            }
            public IEnumerable<PlanetRecord> Planets { get { return _planets; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        /// <summary>Position-dependent gravity: 1.0g within 5km of the origin (Surface-like), 0g
        /// beyond that (DeepSpace-like) -- unlike ConstantGravityCatalogue above, lets a test place
        /// two Shipyard nodes in genuinely different real environments to prove
        /// PostProactiveShipDemands checks each node's own route feasibility independently instead
        /// of a single network-wide existence check.</summary>
        private sealed class TwoZoneCatalogue : IPlanetCatalogue
        {
            private static readonly Vec3 Origin = new Vec3(0, 0, 0);
            private readonly PlanetRecord _planet = new PlanetRecord
            {
                Id = "P1", Center = Origin, SurfaceRadius = 1,
                GravityRadius = 5, AtmosphereRadius = 5, HasAtmosphere = true,
            };
            public IEnumerable<PlanetRecord> Planets { get { yield return _planet; } }
            public double GravityAt(Vec3 posKm) { return posKm.DistanceTo(Origin) <= 5 ? 1.0 : 0.0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        [Fact]
        public void SelectPrefabBom_ZeroPosture_PicksCheapestCandidate_RegressionGuard()
        {
            // Peacetime baseline (MilitaryPosture == 0, the FactionState default) must behave
            // exactly like the pre-Muster code: cheapest-first, ignoring combat power entirely.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Cheap_Unarmed"] = 10;
            recipes.CostByPrefab["Costly_Armed"]  = 100;
            world.Recipes = recipes;

            var prefabs = new FakePrefabCatalogue();
            prefabs.DpsByPrefab["Costly_Armed"] = 1800;
            world.PrefabCatalogue = prefabs;

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Cheap_Unarmed", "Costly_Armed" } };
            var log = new List<string>();
            var state = MakeState(); // MilitaryPosture defaults to 0

            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Cheap_Unarmed", entry.PrefabId);
        }

        /// <summary>FAC-395: a Mine hull is built for drill rate per unit of BOM, not for being
        /// cheapest. Spadi is cheapest (0.1 score/unit), Rig is priciest but best per unit (0.2),
        /// Mid sits between on cost and last on value (0.05). Cheapest-first built Spadi.</summary>
        [Fact]
        public void SelectPrefabBom_MineRole_RanksByDrillRatePerCost()
        {
            ShipyardManager mgr;
            WorldModel world = MineBuildWorld(out mgr);

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = world.Graph.Nodes["N1"].BuildQueue.Find(e => e.Role == "Mine");
            Assert.NotNull(entry);
            Assert.Equal("Rig", entry.PrefabId);
        }

        /// <summary>The rank is the Mine role's alone: a cargo role offered the same drill-carrying
        /// candidates still builds the cheapest.</summary>
        [Fact]
        public void SelectPrefabBom_TradeRole_WithDrillCandidates_StillPicksCheapest_RegressionGuard()
        {
            ShipyardManager mgr;
            WorldModel world = MineBuildWorld(out mgr);

            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = world.Graph.Nodes["N1"].BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Spadi", entry.PrefabId);
        }

        /// <summary>A Shipyard node with a live <c>Ship:Mine</c> demand, whose candidate pool (the same
        /// for every role) is three drill hulls whose cheapest is not the best per unit.</summary>
        private static WorldModel MineBuildWorld(out ShipyardManager mgr)
        {
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Spadi"] = 10;
            recipes.CostByPrefab["Mid"]   = 100;
            recipes.CostByPrefab["Rig"]   = 1000;
            world.Recipes = recipes;

            var prefabs = new FakePrefabCatalogue();
            prefabs.DrillYieldScoreByPrefab["Spadi"] = 1;
            prefabs.DrillYieldScoreByPrefab["Mid"]   = 5;
            prefabs.DrillYieldScoreByPrefab["Rig"]   = 200;
            foreach (string id in new[] { "Spadi", "Mid", "Rig" }) prefabs.CargoLitersByPrefab[id] = 5000;
            world.PrefabCatalogue = prefabs;

            PostShipDemand(world, "Mine", "N1", destNodeId: "N1", minCargoLiters: 1);
            mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Spadi", "Mid", "Rig" } };
            return world;
        }

        [Fact]
        public void SelectPrefabBom_HighPosture_PicksHigherPowerCandidate_DespiteHigherCost()
        {
            // A faction actively building up its fleet (MilitaryPosture == 1) should tolerate
            // the costlier hull for the Dps payoff instead of defaulting to the unarmed cheap one.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Cheap_Unarmed"] = 10;
            recipes.CostByPrefab["Costly_Armed"]  = 100;
            world.Recipes = recipes;

            var prefabs = new FakePrefabCatalogue();
            prefabs.DpsByPrefab["Costly_Armed"] = 1800;
            world.PrefabCatalogue = prefabs;

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Cheap_Unarmed", "Costly_Armed" } };
            var log = new List<string>();
            var state = MakeState();
            state.MilitaryPosture = 1.0;

            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Costly_Armed", entry.PrefabId);
        }

        [Fact]
        public void SelectPrefabBom_HighPosture_AllCandidatesUnarmed_StillPicksCheapest()
        {
            // SelectPrefabBom is shared by every Ship: role, not just combat ones — Trade and
            // StoreSmall/StoreLarge candidates are typically all unarmed (PowerDps == 0 for every
            // entry). Blending on power is meaningless with no differentiation to blend on, so
            // even at MilitaryPosture == 1 the pick must stay cheapest-first for those role sets.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Cheap_Cargo"]  = 10;
            recipes.CostByPrefab["Costly_Cargo"] = 100;
            world.Recipes = recipes;
            world.PrefabCatalogue = new FakePrefabCatalogue(); // no entries -> PowerDps == 0 for both

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Cheap_Cargo", "Costly_Cargo" } };
            var log = new List<string>();
            var state = MakeState();
            state.MilitaryPosture = 1.0;

            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Cheap_Cargo", entry.PrefabId);
        }

        // ---- combined Raid+Patrol stock target (PostProactiveShipDemands) ------------------

        [Fact]
        public void ProactiveDemand_ZeroPosture_OneExistingCombatShip_NoNewDemand_RegressionGuard()
        {
            // MilitaryPosture defaults to 0 -> combined target == MusterStockBaseline (1), matching
            // pre-Muster "any nonzero network stock is enough" behavior exactly.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            node.ShipStock["Raid"] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Raid"));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Patrol"));
        }

        [Fact]
        public void ProactiveDemand_HighPosture_BelowCombinedTarget_RequestsThinnerRole()
        {
            // Combined stock (4) is below the posture=1 target (MusterStockBaseline 1 + MusterStockMaxBonus 4 = 5),
            // and Raid (0) is thinner than Patrol (4) -> demand must target Raid specifically, not Patrol,
            // even though Patrol alone already sits near what would've been its own old ceiling.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            node.ShipStock["Patrol"] = 4;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            var state = MakeState();
            state.MilitaryPosture = 1.0;
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Raid"));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Patrol"));
        }

        [Fact]
        public void ProactiveDemand_HighPosture_BothRolesEmpty_RequestsDemand()
        {
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            var state = MakeState();
            state.MilitaryPosture = 1.0;
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && (l.Contains("role=Raid") || l.Contains("role=Patrol")));
        }

        // ---- Store role selection diversifies by current per-prefab stock -------------------

        /// <summary>A shelf demand. Post-FAC-060 that is a real capability role carrying
        /// <see cref="DemandEntry.DestinedForSale"/>, not a role named after the shop.</summary>
        private static void PostStoreDemand(WorldModel world, string tag, string role, string nodeId,
                                            string gridSize = null)
        {
            world.Demand.Post(new DemandEntry
            {
                FactionTag = tag,
                Kind       = "Ship:" + role,
                NodeId     = nodeId,
                ExpiresUtc = world.UtcNow.AddSeconds(60),
                DestinedForSale = true,
                RequiredGridSize = gridSize,
            });
        }

        [Fact]
        public void SelectPrefabBom_StoreRole_PicksLeastStockedCandidate_RegardlessOfCost()
        {
            // Network stock: A=2, B=0, C=1 (via existing ListedForSale entries). B is strictly
            // thinnest, so it must be picked even though it's not the cheapest.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.PrefabCatalogue = new FakePrefabCatalogue(); // GridSize="Small" for every prefab
            node.ListedForSale["A"] = 2;
            node.ListedForSale["C"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["A"] = 10;  // cheapest, but already well-stocked
            recipes.CostByPrefab["B"] = 50;  // most expensive, but zero in stock
            recipes.CostByPrefab["C"] = 20;
            world.Recipes = recipes;

            PostStoreDemand(world, "F", "Trade", "N1");

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "A", "B", "C" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.DestinedForSale);
            Assert.NotNull(entry);
            Assert.Equal("B", entry.PrefabId);
        }

        [Fact]
        public void SelectPrefabBom_StoreRole_TiedStock_BreaksTieByCost()
        {
            // A and B are equally stocked (1 each) -> tie broken by cost, A wins.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.PrefabCatalogue = new FakePrefabCatalogue(); // GridSize="Small" for every prefab
            node.ListedForSale["A"] = 1;
            node.ListedForSale["B"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["A"] = 10;
            recipes.CostByPrefab["B"] = 50;
            world.Recipes = recipes;

            PostStoreDemand(world, "F", "Trade", "N1");

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "A", "B" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.DestinedForSale);
            Assert.NotNull(entry);
            Assert.Equal("A", entry.PrefabId);
        }

        [Fact]
        public void SelectPrefabBom_StoreRole_NoExistingStock_PicksCheapest_DayOneRegressionGuard()
        {
            // No prior ListedForSale entries -> every candidate ties at zero -> cheapest wins,
            // matching pre-diversification behavior for the very first build of a role.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Cheap"]  = 10;
            recipes.CostByPrefab["Costly"] = 100;
            world.Recipes = recipes;

            PostStoreDemand(world, "F", "Trade", "N1");

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Cheap", "Costly" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.DestinedForSale);
            Assert.NotNull(entry);
            Assert.Equal("Cheap", entry.PrefabId);
        }

        // ---- zone-aware build selection (stop building already-stranded ships) --------------

        [Fact]
        public void SelectPrefabBom_SurfaceBuildSite_PrefersHydrogenOverCheaperIon()
        {
            // Ion is cheaper but only ~20% effective at full planetary influence -- at this node's
            // real g=1/atmosphere, its dry TWR fails the real default MinDryTwr (1.05), so the zone
            // filter must exclude it even though pure cost-sort would otherwise pick it.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("Ion_Cheap", dryMassKg: 1000, forceN: 5000);          // TWR ~0.10 at g=1 -- fails
            prefabs.SetHydrogenDown("Hydrogen_Costly", dryMassKg: 1000, forceN: 15000); // TWR ~1.53 -- passes
            world.PrefabCatalogue = prefabs;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Ion_Cheap"]       = 10;
            recipes.CostByPrefab["Hydrogen_Costly"] = 100;
            world.Recipes = recipes;

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Ion_Cheap", "Hydrogen_Costly" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Hydrogen_Costly", entry.PrefabId);
        }

        [Fact]
        public void SelectPrefabBom_DeepSpaceBuildSite_ExcludesAtmoOnlyCandidate()
        {
            // The mirror case: an Atmo-only hull produces zero force outside atmosphere, so it's
            // exactly as broken in deep space as an Ion-only hull is on a surface -- the filter
            // must not be hardcoded to "Surface only".
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 0, inAtmosphereEverywhere: false);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetAtmoOnlyDown("Atmo_Cheap", dryMassKg: 1000, forceN: 20000); // 0 force outside atmosphere -- fails
            prefabs.SetIonDown("Ion_Costly", dryMassKg: 1000, forceN: 5000);       // full effectiveness in deep space -- passes
            world.PrefabCatalogue = prefabs;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Atmo_Cheap"] = 10;
            recipes.CostByPrefab["Ion_Costly"] = 100;
            world.Recipes = recipes;

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Atmo_Cheap", "Ion_Costly" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Ion_Costly", entry.PrefabId);
        }

        [Fact]
        public void SelectPrefabBom_NoZoneFeasibleCandidateExists_FallsBackToFullPool_RegressionGuard()
        {
            // Every candidate is Ion-only at a Surface node -- nothing survives the filter. The
            // shipyard must still build something (cheapest of the unfiltered pool) rather than
            // stalling the demand forever, and must log the fallback for live visibility.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("Ion_A", dryMassKg: 1000, forceN: 3000);
            prefabs.SetIonDown("Ion_B", dryMassKg: 1000, forceN: 4000);
            world.PrefabCatalogue = prefabs;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Ion_A"] = 10;
            recipes.CostByPrefab["Ion_B"] = 50;
            world.Recipes = recipes;

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Ion_A", "Ion_B" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Ion_A", entry.PrefabId); // cheapest of the unfiltered fallback pool
            Assert.Contains(log, l => l.Contains("BOM_ZONE_INFEASIBLE"));
        }

        [Fact]
        public void SelectPrefabBom_SameHintZoneInfeasible_FallsBackToUniversalPool()
        {
            // Same-hint candidates are all Ion at a Surface node (fail), but the universal "All"
            // pool for this role contains a Hydrogen hull that survives -- the rescue this fix adds,
            // matching the live audit's finding that faction-specific Trade buckets are uniformly
            // Ion while the universal pool usually isn't.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("Ion_A", dryMassKg: 1000, forceN: 3000);
            prefabs.SetIonDown("Ion_B", dryMassKg: 1000, forceN: 4000);
            prefabs.SetHydrogenDown("Hydrogen_Universal", dryMassKg: 1000, forceN: 15000);
            prefabs.NamesForRoleAll["Raid"] = new[] { "Hydrogen_Universal" };
            world.PrefabCatalogue = prefabs;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Ion_A"] = 10;
            recipes.CostByPrefab["Ion_B"] = 50;
            recipes.CostByPrefab["Hydrogen_Universal"] = 100;
            world.Recipes = recipes;

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Ion_A", "Ion_B" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Hydrogen_Universal", entry.PrefabId);
            // Scoped to role=Raid: the same tick also proactively demands Trade, which has no
            // NamesForRoleAll entry wired and independently hits the terminal BOM_ZONE_INFEASIBLE
            // fallback -- expected and unrelated to this test's Raid-specific rescue.
            Assert.Contains(log, l => l.Contains("BOM_ZONE_UNIVERSAL_FALLBACK") && l.Contains("role=Raid"));
            Assert.DoesNotContain(log, l => l.Contains("BOM_ZONE_INFEASIBLE") && l.Contains("role=Raid"));
        }

        [Fact]
        public void SelectPrefabBom_UniversalPoolAlsoZoneInfeasible_StillFallsBackToFullPool()
        {
            // Same-hint candidates fail, and the universal pool is wired but every candidate in it
            // also fails -- the terminal "guaranteed something gets built" contract must still hold
            // exactly as before this fix, proving the new rescue branch doesn't swallow it.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("Ion_A", dryMassKg: 1000, forceN: 3000);
            prefabs.SetIonDown("Ion_B", dryMassKg: 1000, forceN: 4000);
            prefabs.SetIonDown("Ion_Universal", dryMassKg: 1000, forceN: 3500); // also fails
            prefabs.NamesForRoleAll["Raid"] = new[] { "Ion_Universal" };
            world.PrefabCatalogue = prefabs;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Ion_A"] = 10;
            recipes.CostByPrefab["Ion_B"] = 50;
            recipes.CostByPrefab["Ion_Universal"] = 5;
            world.Recipes = recipes;

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Ion_A", "Ion_B" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Ion_A", entry.PrefabId); // cheapest of the unfiltered same-hint pool -- universal pool never chosen from
            Assert.Contains(log, l => l.Contains("BOM_ZONE_INFEASIBLE") && l.Contains("role=Raid"));
            Assert.DoesNotContain(log, l => l.Contains("BOM_ZONE_UNIVERSAL_FALLBACK") && l.Contains("role=Raid"));
        }

        [Fact]
        public void SelectPrefabBom_CargoFloorUnmetEverywhere_PicksTheBiggestHullThatCanFlyTheLeg()
        {
            // The discriminator for "biggest that can FLY this leg" against its two near-misses.
            // Big_Ion is the roomiest hull in the catalogue but cannot fly at 1g; Small_Hydro is the
            // cheapest thing that can. Neither is the answer -- the demand asked for volume, and the
            // most volume actually deliverable over these legs is Mid_Hydro.
            //
            // Getting Big_Ion would mean the route gate stopped being hard; getting Small_Hydro would
            // mean the floor was dropped rather than clamped, handing the whole route-feasible pool
            // to the cheapest-first ranker.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("Big_Ion", dryMassKg: 1000, forceN: 3000);       // roomiest, cannot fly at 1g
            prefabs.CargoLitersByPrefab["Big_Ion"] = 5000;
            prefabs.SetHydrogenDown("Mid_Hydro", dryMassKg: 1000, forceN: 15000);
            prefabs.CargoLitersByPrefab["Mid_Hydro"] = 800;                     // biggest that CAN fly
            prefabs.SetHydrogenDown("Small_Hydro", dryMassKg: 1000, forceN: 15000);
            prefabs.CargoLitersByPrefab["Small_Hydro"] = 100;                   // cheapest that can fly
            prefabs.CargoLitersByPrefab["StockShip"] = 50; // cargo > 0 suppresses the proactive Trade demand,
                                                          // but under the posted floor so the met-gate does
                                                          // not retire that demand as already satisfied
            world.PrefabCatalogue = prefabs;
            node.ShipStock["StockShip"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Big_Ion"] = 10;
            recipes.CostByPrefab["Mid_Hydro"] = 500;   // by far the priciest, so cost-first cannot pick it
            recipes.CostByPrefab["Small_Hydro"] = 20;
            world.Recipes = recipes;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", MinCargoLiters = 3000, // clears nothing flyable
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var mgr = new ShipyardManager
            {
                PrefabListPicker = (role, tag) => new[] { "Big_Ion", "Mid_Hydro", "Small_Hydro" },
            };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Mid_Hydro", entry.PrefabId);
            Assert.DoesNotContain(log, l => l.Contains("BOM_ZONE_INFEASIBLE") && l.Contains("role=Trade"));
        }

        [Fact]
        public void SelectPrefabBom_NoRouteFeasibleCandidateCarriesCargo_LeavesRouteAloneToDecide()
        {
            // A combat role posts the nominal 1-litre "must be cargo-capable" sentinel. Nothing that
            // can fly the leg carries any cargo, so there is no ceiling to clamp to -- route
            // feasibility alone decides and the cheapest flyable hull is built, as before. Without
            // the ceiling-of-0 branch this would fall through to the terminal unfiltered pool and
            // report a zone-infeasible failure it did not have.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetHydrogenDown("Gun_Cheap", dryMassKg: 1000, forceN: 15000);
            prefabs.SetHydrogenDown("Gun_Costly", dryMassKg: 1000, forceN: 15000);
            // Neither carries cargo -- CargoLitersByPrefab left unset, so both read 0.
            world.PrefabCatalogue = prefabs;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Gun_Cheap"] = 10;
            recipes.CostByPrefab["Gun_Costly"] = 500;
            world.Recipes = recipes;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Raid", NodeId = "N1", MinCargoLiters = 1,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Gun_Cheap", "Gun_Costly" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Gun_Cheap", entry.PrefabId);
            Assert.DoesNotContain(log, l => l.Contains("BOM_ZONE_INFEASIBLE") && l.Contains("role=Raid"));
        }

        [Fact]
        public void SelectPrefabBom_CargoFloorUnmetEverywhere_RelaxesTheFloorRatherThanStrandingTheHull()
        {
            // Route is a hard gate; the cargo floor is only a preference. When no candidate clears
            // both, an undersized hull that can actually fly the legs beats a roomy one that cannot:
            // the small one serves a later, smaller demand, while the stranded one occupies the
            // node's stock forever and re-blocks the dispatch that asked for it.
            //
            // Before this, the pair below fell straight through to the unfiltered terminal branch and
            // built route-infeasible Ion_A. Live 2026-08-23: 73 of 73 BOM_ZONE_INFEASIBLE lines were
            // role=Mine, every one at a node whose Ship:Mine demand carried a multi-million-litre
            // floor against a 16,875L catalogue ceiling.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("Ion_A", dryMassKg: 1000, forceN: 3000); // roomy but cannot fly at 1g
            prefabs.CargoLitersByPrefab["Ion_A"] = 1000;
            prefabs.SetHydrogenDown("Hydrogen_TooSmall", dryMassKg: 1000, forceN: 15000);
            prefabs.CargoLitersByPrefab["Hydrogen_TooSmall"] = 10; // route-feasible but under the floor
            prefabs.NamesForRoleAll["Trade"] = new[] { "Hydrogen_TooSmall" };
            prefabs.CargoLitersByPrefab["StockShip"] = 100; // cargo > 0 suppresses the proactive Trade demand,
                                                           // but under the posted 500L floor so the met-gate
                                                           // does not retire that demand as already satisfied
            world.PrefabCatalogue = prefabs;
            node.ShipStock["StockShip"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Ion_A"] = 10;
            recipes.CostByPrefab["Hydrogen_TooSmall"] = 50;
            world.Recipes = recipes;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", MinCargoLiters = 500,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Ion_A" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Hydrogen_TooSmall", entry.PrefabId);
            Assert.Contains(log, l => l.Contains("BOM_ZONE_UNIVERSAL_FALLBACK") && l.Contains("role=Trade"));
            Assert.DoesNotContain(log, l => l.Contains("BOM_ZONE_INFEASIBLE") && l.Contains("role=Trade"));
        }

        [Fact]
        public void SelectPrefabBom_CargoFloorStillPreferred_WhenACandidateClearsBothGates()
        {
            // The floor is relaxed only after both pools come up empty. A candidate meeting route AND
            // cargo must still beat a route-feasible undersized one, or the relaxation above would
            // have quietly demoted the floor to decoration.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetHydrogenDown("Hydrogen_TooSmall", dryMassKg: 1000, forceN: 15000);
            prefabs.CargoLitersByPrefab["Hydrogen_TooSmall"] = 10;   // cheapest, but under the floor
            prefabs.SetHydrogenDown("Hydrogen_BigEnough", dryMassKg: 1000, forceN: 15000);
            prefabs.CargoLitersByPrefab["Hydrogen_BigEnough"] = 900; // clears both gates
            prefabs.CargoLitersByPrefab["StockShip"] = 100;
            world.PrefabCatalogue = prefabs;
            node.ShipStock["StockShip"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Hydrogen_TooSmall"] = 10;
            recipes.CostByPrefab["Hydrogen_BigEnough"] = 500;
            world.Recipes = recipes;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", MinCargoLiters = 500,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var mgr = new ShipyardManager
            {
                PrefabListPicker = (role, tag) => new[] { "Hydrogen_TooSmall", "Hydrogen_BigEnough" }
            };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Hydrogen_BigEnough", entry.PrefabId); // costlier, but the floor still binds
            Assert.DoesNotContain(log, l => l.Contains("BOM_ZONE_INFEASIBLE") && l.Contains("role=Trade"));
        }

        [Fact]
        public void SelectPrefabBom_ShelfBuild_ObeysTheYardsZoneFilter_AndStillDiversifies()
        {
            // A shelf build is route-gated at the yard like every other build. An unsold shelf hull
            // is handed back to the fleet pool by StoreShelfRelease at the node it was built at, so
            // a hull that cannot lift off that node becomes stranded stock the moment its
            // reservation lapses.
            //
            // Diversification must survive the gate: among the hulls that CAN fly here, least-stocked
            // still wins. Both halves matter -- a version that gated correctly but lost
            // diversification would pass a route-only assertion.
            //
            // The yard is in the well; the faction's shop is out in vacuum. That the shop could sell
            // the ion hull is no longer a reason to build it in the well.
            var world = MakeWorld();
            world.Planets = new TwoZoneCatalogue();
            var node  = AddStation(world, "N1", "F", new Vec3(0, 0, 0));      // 1.0 g
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            AddStation(world, "Shop", "F", new Vec3(9000, 0, 0));              // 0 g, sells
            node.ListedForSale["A"] = 0;
            node.ListedForSale["B"] = 5; // heavily stocked

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("A", dryMassKg: 1000, forceN: 5000);          // fails the zone filter
            prefabs.SetHydrogenDown("B", dryMassKg: 1000, forceN: 15000);    // passes the zone filter
            world.PrefabCatalogue = prefabs;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["A"] = 10;
            recipes.CostByPrefab["B"] = 10;
            world.Recipes = recipes;

            PostStoreDemand(world, "F", "Trade", "N1");

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "A", "B" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.DestinedForSale);
            Assert.NotNull(entry);
            // "A" is least-stocked and would win on diversification alone; it cannot leave this yard,
            // so the heavily-stocked "B" is built instead.
            Assert.Equal("B", entry.PrefabId);
        }

        [Fact]
        public void SelectPrefabBom_ShelfBuild_HonoursTheDemandsGridSize()
        {
            // The store's small/large shelf depth is expressed as RequiredGridSize on the demand.
            // The grid-size narrowing runs in the same pass as the route gate, so a shelf build that
            // skipped one skipped the other and a Large slot could be filled by a Small hull.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var prefabs = new FakePrefabCatalogue();
            prefabs.GridSizeByPrefab["Small_Cheap"] = "Small";
            prefabs.GridSizeByPrefab["Large_Dear"]  = "Large";
            world.PrefabCatalogue = prefabs;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Small_Cheap"] = 10;
            recipes.CostByPrefab["Large_Dear"]  = 100;
            world.Recipes = recipes;

            PostStoreDemand(world, "F", "Trade", "N1", gridSize: "Large");

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Small_Cheap", "Large_Dear" } };
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, new List<string>());

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.DestinedForSale);
            Assert.NotNull(entry);
            Assert.Equal("Large_Dear", entry.PrefabId); // costlier, but the slot asked for Large
        }

        [Fact]
        public void SelectPrefabBom_UnscannedPrefab_NeverExcluded()
        {
            // A candidate with no scanned thrust data (DryMassKg == 0, the FakePrefabCatalogue
            // default) is "unknown, don't gate" -- it must stay eligible even at a Surface node,
            // matching ShipPool.IsRouteFeasible's own rollout-safety rule.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetHydrogenDown("Hydrogen_Scanned", dryMassKg: 1000, forceN: 15000); // passes anyway
            // "Unscanned" is never registered -- DryMassKg() returns 0.
            world.PrefabCatalogue = prefabs;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Unscanned"]       = 10;
            recipes.CostByPrefab["Hydrogen_Scanned"] = 100;
            world.Recipes = recipes;

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Unscanned", "Hydrogen_Scanned" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Unscanned", entry.PrefabId); // both eligible -> cheapest wins
        }

        [Fact]
        public void SelectPrefabBom_MilitaryPostureBranch_AlsoRespectsZoneFilter()
        {
            // At high posture the cost/Dps blend would normally favor the higher-power Ion hull --
            // the zone filter must exclude it before that blend ever runs, proving the filter
            // applies uniformly across all three scoring branches, not just the peacetime one.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("Ion_HighDps", dryMassKg: 1000, forceN: 5000);        // fails the zone filter
            prefabs.SetHydrogenDown("Hydrogen_LowDps", dryMassKg: 1000, forceN: 15000); // passes
            prefabs.DpsByPrefab["Ion_HighDps"]    = 1800;
            prefabs.DpsByPrefab["Hydrogen_LowDps"] = 500;
            world.PrefabCatalogue = prefabs;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Ion_HighDps"]     = 100;
            recipes.CostByPrefab["Hydrogen_LowDps"] = 50;
            world.Recipes = recipes;

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Ion_HighDps", "Hydrogen_LowDps" } };
            var log = new List<string>();
            var state = MakeState();
            state.MilitaryPosture = 1.0;
            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Raid");
            Assert.NotNull(entry);
            Assert.Equal("Hydrogen_LowDps", entry.PrefabId); // wins by elimination, not by the power blend
        }

        // ---- route-aware Ship demand: real destination + MinCargoLiters feed build selection --

        [Fact]
        public void BuildQueueCreation_UsesDemandDestNodeId_ForRouteBias_NotDegenerateSamePoint()
        {
            // Before this fix buildRoute was always a degenerate same-point route (originNode ->
            // originNode), so a demand naming a real destination had no effect on hull selection.
            // The Shipyard (N1) sits in deep space (g=0 -- Ion-friendly); the demand's real
            // destination (N2) sits at Surface gravity (g=1 -- Ion-hostile). Only a real two-endpoint
            // route correctly excludes Ion here -- the degenerate route would pass it (both ends g=0).
            var world = MakeWorld();
            var node = AddStation(world, "N1", "F", new Vec3(10, 0, 0)); // >5km from TwoZoneCatalogue's origin -> g=0
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            AddStation(world, "N2", "F", new Vec3(0, 0, 0)); // within 5km -> g=1
            world.Planets = new TwoZoneCatalogue();

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("Ion_Cheap", dryMassKg: 1000, forceN: 5000);            // fails at N2's g=1
            prefabs.SetHydrogenDown("Hydrogen_Costly", dryMassKg: 1000, forceN: 15000); // passes everywhere
            prefabs.CargoLitersByPrefab["Ion_Cheap"]       = 1000; // both clear the demand's floor, so only the
            prefabs.CargoLitersByPrefab["Hydrogen_Costly"] = 1000; // route can be what excludes Ion below
            prefabs.CargoLitersByPrefab["StockShip"]       = 100;  // cargo > 0 suppresses the proactive Trade demand,
            world.PrefabCatalogue = prefabs;                       // under the 500L floor so the met-gate keeps ours
            node.ShipStock["StockShip"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Ion_Cheap"]       = 10;
            recipes.CostByPrefab["Hydrogen_Costly"] = 100;
            world.Recipes = recipes;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", DestNodeId = "N2",
                MinCargoLiters = 500, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Ion_Cheap", "Hydrogen_Costly" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Hydrogen_Costly", entry.PrefabId); // Ion would win on cost alone under the old degenerate route
        }

        [Fact]
        public void BuildQueueCreation_UsesDemandViaNodeId_ForPickupLegRouteBias()
        {
            // The live bug this regression guards: a Haul/Trade Tier-3 fallback demand whose build
            // node happens to coincide with its own DestNodeId (a common case -- the shipyard IS the
            // material's own delivery destination) makes buildRoute a trivial same-point route that
            // ANY hull passes, completely hiding the real pickup leg (ViaNodeId) the built hull must
            // ALSO survive reaching. N1 (shipyard == dest) sits within the atmosphere zone (g=1) where
            // an atmosphere-only hull works fine; the real pickup node N2 sits in deep space (g=0,
            // >5km away) where it cannot generate any thrust at all. Only a route check that also
            // covers the via leg correctly excludes it.
            var world = MakeWorld();
            var node = AddStation(world, "N1", "F", new Vec3(0, 0, 0)); // within 5km -> g=1 (atmosphere)
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            AddStation(world, "N2", "F", new Vec3(10, 0, 0)); // >5km -> g=0 (deep space)
            world.Planets = new TwoZoneCatalogue();

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetAtmoOnlyDown("AtmoOnly_Bad", dryMassKg: 1000, forceN: 15000); // fails at N2 (no atmosphere there)
            prefabs.SetHydrogenDown("Hydrogen_Good", dryMassKg: 1000, forceN: 15000); // passes everywhere
            prefabs.CargoLitersByPrefab["AtmoOnly_Bad"]  = 1000; // both clear the demand's floor, so only the via
            prefabs.CargoLitersByPrefab["Hydrogen_Good"] = 1000; // leg can be what excludes AtmoOnly below
            prefabs.CargoLitersByPrefab["StockShip"]     = 100;  // cargo > 0 suppresses the proactive Trade demand,
            world.PrefabCatalogue = prefabs;                     // under the 500L floor so the met-gate keeps ours
            node.ShipStock["StockShip"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["AtmoOnly_Bad"]   = 10;
            recipes.CostByPrefab["Hydrogen_Good"]  = 100;
            world.Recipes = recipes;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", DestNodeId = "N1", ViaNodeId = "N2",
                MinCargoLiters = 500, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "AtmoOnly_Bad", "Hydrogen_Good" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Hydrogen_Good", entry.PrefabId); // AtmoOnly_Bad would win on cost alone if the via leg were ignored
        }

        [Fact]
        public void BuildQueueCreation_NoViaNodeId_BehavesIdenticallyToRouteWithoutOne_RegressionGuard()
        {
            // A demand with no ViaNodeId (the overwhelming majority -- proactive "keep this zone
            // warm" checks and any non-Haul/Trade-Tier-3 poster) must behave byte-identically to
            // before this field existed: buildRoute alone decides, no via leg is synthesized.
            var world = MakeWorld();
            var node = AddStation(world, "N1", "F", new Vec3(10, 0, 0)); // >5km -> g=0 (deep space)
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.Planets = new TwoZoneCatalogue();

            var prefabs = new FakePrefabCatalogue();
            prefabs.SetIonDown("Ion_Cheap", dryMassKg: 1000, forceN: 5000); // passes at N1's own g=0
            prefabs.SetHydrogenDown("Hydrogen_Costly", dryMassKg: 1000, forceN: 15000);
            prefabs.CargoLitersByPrefab["Ion_Cheap"]       = 1000; // both clear the demand's floor -- nothing but the
            prefabs.CargoLitersByPrefab["Hydrogen_Costly"] = 1000; // (absent) via leg could separate them
            prefabs.CargoLitersByPrefab["StockShip"]       = 100;  // cargo > 0 suppresses the proactive Trade demand,
            world.PrefabCatalogue = prefabs;                       // under the 500L floor so the met-gate keeps ours
            node.ShipStock["StockShip"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Ion_Cheap"]       = 10;
            recipes.CostByPrefab["Hydrogen_Costly"] = 100;
            world.Recipes = recipes;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", DestNodeId = null, ViaNodeId = null,
                MinCargoLiters = 500, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Ion_Cheap", "Hydrogen_Costly" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Ion_Cheap", entry.PrefabId); // cheapest wins -- no via leg to exclude it
        }

        [Fact]
        public void SelectPrefabBom_MinCargoLiters_ExcludesTooSmallCandidate()
        {
            // A route-feasible candidate that's too small to carry the demand's real cargo need must
            // still be excluded -- MinCargoLiters is a real size floor now, not just a display value.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var prefabs = new FakePrefabCatalogue();
            prefabs.CargoLitersByPrefab["Small_Cheap"] = 100;
            prefabs.CargoLitersByPrefab["Big_Costly"]  = 2000;
            prefabs.CargoLitersByPrefab["StockShip"]   = 100; // cargo > 0 suppresses the proactive Trade demand,
                                                             // but under the posted 500L floor so the met-gate
                                                             // does not retire that demand as already satisfied
            world.PrefabCatalogue = prefabs;
            node.ShipStock["StockShip"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Small_Cheap"] = 10;
            recipes.CostByPrefab["Big_Costly"]  = 100;
            world.Recipes = recipes;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", MinCargoLiters = 500,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Small_Cheap", "Big_Costly" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Big_Costly", entry.PrefabId); // Small_Cheap wins on cost alone unless the floor excludes it
        }

        [Fact]
        public void MultipleTradeDemandsAtSameNode_PicksPrefabClosestToMedianCargoNeed()
        {
            // OperationsManager's per-vector Haul-fallback can post many distinct-destination
            // Ship:Trade demands at the same origin Shipyard in one tick (DemandKey now includes
            // DestNodeId). Whichever demand the register happens to return first must not alone
            // dictate the build -- the group's representative should be the one closest to its
            // MEDIAN MinCargoLiters, so the built hull targets a typical shipment, not an outlier.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var prefabs = new FakePrefabCatalogue();
            prefabs.CargoLitersByPrefab["Small_Cheap"] = 100;
            prefabs.CargoLitersByPrefab["Mid"]         = 600;
            prefabs.CargoLitersByPrefab["Big_Costly"]  = 10000;
            prefabs.CargoLitersByPrefab["StockShip"]   = 100; // cargo > 0 suppresses the proactive Trade demand,
                                                             // but under the posted 500L floor so the met-gate
                                                             // does not retire that demand as already satisfied
            world.PrefabCatalogue = prefabs;
            node.ShipStock["StockShip"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Small_Cheap"] = 10;
            recipes.CostByPrefab["Mid"]         = 50;
            recipes.CostByPrefab["Big_Costly"]  = 100;
            world.Recipes = recipes;

            // Posted with the largest demand FIRST -- under the old first-match behavior this alone
            // would force Big_Costly. The fix must ignore posting order entirely.
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", DestNodeId = "D1",
                MinCargoLiters = 10000, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", DestNodeId = "D2",
                MinCargoLiters = 100, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", DestNodeId = "D3",
                MinCargoLiters = 500, ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Small_Cheap", "Mid", "Big_Costly" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            // Median of {10000, 100, 500} is 500 -- the representative demand is the 500 one
            // itself, whose floor excludes Small_Cheap (100) but not Mid (600); cheapest of the
            // remaining pool wins.
            Assert.Equal("Mid", entry.PrefabId);
        }

        [Fact]
        public void SelectPrefabBom_MinCargoLiters_NoCandidateMeetsFloor_ClampsToTheBiggestThatFlies()
        {
            // A demand the faction cannot fulfill at size still builds something rather than stalling
            // forever -- but this is not the zone-infeasible case and must not report itself as one.
            // Tiny_A/Tiny_B are unscanned, so the route gate passes both; only the 500L floor fails.
            // The floor is clamped to the biggest hull that can fly the legs (Tiny_B, 80L) rather than
            // dropped, and the terminal unfiltered branch (which would also have dropped the route
            // check) is never reached. Dropping the floor here would hand both hulls to the
            // cheapest-first ranker and build the 50L Tiny_A -- the smallest hull for the biggest
            // demand, which is the inversion this clamp exists to prevent.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var prefabs = new FakePrefabCatalogue();
            prefabs.CargoLitersByPrefab["Tiny_A"] = 50;
            prefabs.CargoLitersByPrefab["Tiny_B"] = 80;
            prefabs.CargoLitersByPrefab["StockShip"] = 100; // cargo > 0 suppresses the proactive Trade demand,
                                                           // but under the posted 500L floor so the met-gate
                                                           // does not retire that demand as already satisfied
            world.PrefabCatalogue = prefabs;
            node.ShipStock["StockShip"] = 1;

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Tiny_A"] = 10;
            recipes.CostByPrefab["Tiny_B"] = 50;
            world.Recipes = recipes;

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1", MinCargoLiters = 500,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Tiny_A", "Tiny_B" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Tiny_B", entry.PrefabId); // biggest that flies, though Tiny_A is 5x cheaper
            Assert.DoesNotContain(log, l => l.Contains("BOM_ZONE_INFEASIBLE") && l.Contains("role=Trade"));
        }

        // ---- ForceCollect (ActionKind.FinishBuild Enact handler) ---------------------------

        [Fact]
        public void ForceCollect_PullsShortfallFromDifferentOwnedNode_CompletesBuild()
        {
            var world = MakeWorld();
            var buildNode  = AddStation(world, "N1", "F");
            var stockNode  = AddStation(world, "N2", "F", new Vec3(100, 0, 0));

            var entry = new BuildQueueEntry { Id = "Trade:001", Role = "Trade" };
            entry.Bom["SteelPlate"]       = 100;
            entry.Collected["SteelPlate"] = 80; // 80% -- above the default 0.70 urgency threshold
            buildNode.BuildQueue.Add(entry);

            stockNode.Inventory["SteelPlate"] = 50; // more than the 20-unit shortfall

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.ForceCollect(MakeState(), world, "N1", world.Tuning, log);

            Assert.Empty(buildNode.BuildQueue);
            Assert.Equal(1, buildNode.ShipStock[""]);
            Assert.Equal(30, stockNode.Inventory["SteelPlate"]); // 50 - 20 shortfall
            Assert.Contains(log, l => l.Contains("SHIP_BUILT") && l.Contains("(force)"));
        }

        [Fact]
        public void ForceCollect_NetworkWideStockStillShort_LeavesEntryInPlace()
        {
            var world = MakeWorld();
            var buildNode = AddStation(world, "N1", "F");
            var stockNode = AddStation(world, "N2", "F", new Vec3(100, 0, 0));

            var entry = new BuildQueueEntry { Id = "Trade:001", Role = "Trade" };
            entry.Bom["SteelPlate"]       = 100;
            entry.Collected["SteelPlate"] = 80;
            buildNode.BuildQueue.Add(entry);

            stockNode.Inventory["SteelPlate"] = 5; // short of the 20-unit shortfall

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.ForceCollect(MakeState(), world, "N1", world.Tuning, log);

            Assert.Single(buildNode.BuildQueue); // not completed, not removed
            Assert.Equal(85, entry.Collected["SteelPlate"]); // absorbed what was available
            Assert.Empty(buildNode.ShipStock);
            Assert.Equal(0, stockNode.Inventory["SteelPlate"]); // fully drained, still not enough
            Assert.DoesNotContain(log, l => l.Contains("SHIP_BUILT"));
            Assert.Contains(log, l => l.Contains("FINISH_BUILD_FORCE"));
        }

        [Fact]
        public void ForceCollect_NeverDrawsFromReservedInventory()
        {
            var world = MakeWorld();
            var buildNode = AddStation(world, "N1", "F");
            var stockNode = AddStation(world, "N2", "F", new Vec3(100, 0, 0));

            var entry = new BuildQueueEntry { Id = "Trade:001", Role = "Trade" };
            entry.Bom["SteelPlate"]       = 100;
            entry.Collected["SteelPlate"] = 80;
            buildNode.BuildQueue.Add(entry);

            // Earmarked for something else (e.g. a Develop capability install) -- must stay untouched.
            stockNode.ReservedInventory["SteelPlate"] = 50;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.ForceCollect(MakeState(), world, "N1", world.Tuning, log);

            Assert.Single(buildNode.BuildQueue);
            Assert.Equal(80, entry.Collected["SteelPlate"]); // unchanged -- nothing routable existed
            Assert.Equal(50, stockNode.ReservedInventory["SteelPlate"]); // untouched
        }

        // ---- Player-triggered queue overrides (QueueBuild/CancelBuild) ---------------------

        private static WorldModel MakeQueueWorld(out SpatialNode node, out FakeRecipeRegistry recipes, out FakePrefabCatalogue prefabs)
        {
            var world = MakeWorld();
            node = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            recipes = new FakeRecipeRegistry();
            world.Recipes = recipes;

            prefabs = new FakePrefabCatalogue();
            world.PrefabCatalogue = prefabs;
            return world;
        }

        [Fact]
        public void QueueBuild_CreatesEntry_WhenSlotEmpty()
        {
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            recipes.CostByPrefab["Hauler_A"] = 40;
            prefabs.CargoLitersByPrefab["Hauler_A"] = 1000;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;
            PlayerQueueResult result = mgr.QueueBuild(world, world.Tuning, "F", "N1", "Trade", "Hauler_A", out bom, log);

            Assert.Equal(PlayerQueueResult.Ok, result);
            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Trade");
            Assert.NotNull(entry);
            Assert.Equal("Hauler_A", entry.PrefabId);
            Assert.Equal(40, entry.Bom["Comp:Steel"]);
            Assert.Equal(40, bom["Comp:Steel"]);
            Assert.Contains(log, l => l.Contains("BUILD_QUEUED_BY_PLAYER") && l.Contains("Hauler_A"));
        }

        [Fact]
        public void QueueBuild_AppendsBesideAnExistingEntryOfTheSameRole()
        {
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            recipes.CostByPrefab["Hauler_A"] = 40;
            recipes.CostByPrefab["Hauler_B"] = 90;
            prefabs.CargoLitersByPrefab["Hauler_A"] = 1000;
            prefabs.CargoLitersByPrefab["Hauler_B"] = 2000;

            var existing = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", PrefabId = "Hauler_A" };
            existing.Bom["Comp:Steel"] = 40;
            existing.Collected["Comp:Steel"] = 25;
            existing.ReservedInputs["Ingot:Iron"] = 12;
            node.BuildQueue.Add(existing);

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;
            PlayerQueueResult result = mgr.QueueBuild(world, world.Tuning, "F", "N1", "Trade", "Hauler_B", out bom, log);

            Assert.Equal(PlayerQueueResult.Ok, result);
            // Clicking Build a second time queues a second hull. The first keeps everything it has
            // collected and reserved -- nothing is refunded, because nothing was displaced.
            Assert.Equal(2, node.BuildQueue.Count);
            Assert.Same(existing, node.BuildQueue[0]);
            Assert.Equal(25, existing.Collected["Comp:Steel"]);
            Assert.Equal(12, existing.ReservedInputs["Ingot:Iron"]);
            Assert.False(node.Inventory.ContainsKey("Ingot:Iron"));

            BuildQueueEntry added = node.BuildQueue[1];
            Assert.Equal("Hauler_B", added.PrefabId);
            Assert.Equal(90, added.Bom["Comp:Steel"]);
            Assert.Empty(added.Collected);
            // Distinct ids: the production side resolves (nodeId, entryId) to the FIRST match, so a
            // repeated id would put this entry's reserved inputs into the other's bucket.
            Assert.NotEqual(existing.Id, added.Id);
            Assert.DoesNotContain(log, l => l.Contains("BUILD_OVERRIDDEN_BY_PLAYER"));
            Assert.Contains(log, l => l.Contains("BUILD_QUEUED_BY_PLAYER") && l.Contains("Hauler_B"));
        }

        [Fact]
        public void QueueBuild_RejectsUnknownRole()
        {
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;
            PlayerQueueResult result = mgr.QueueBuild(world, world.Tuning, "F", "N1", "NotARole", "Hauler_A", out bom, log);

            Assert.Equal(PlayerQueueResult.UnknownRole, result);
            Assert.Empty(node.BuildQueue);
        }

        [Fact]
        public void QueueBuild_RejectsPrefabNotSuitableForRole()
        {
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            recipes.CostByPrefab["Combat_Only"] = 40;
            prefabs.DpsByPrefab["Combat_Only"] = 500; // no cargo -- not Trade-suitable

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;
            PlayerQueueResult result = mgr.QueueBuild(world, world.Tuning, "F", "N1", "Trade", "Combat_Only", out bom, log);

            Assert.Equal(PlayerQueueResult.UnknownPrefab, result);
            Assert.Empty(node.BuildQueue);
        }

        [Fact]
        public void QueueBuild_RefusesAPrefabOverThePerformanceCeiling()
        {
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            recipes.CostByPrefab["Hauler_A"] = 40;
            prefabs.CargoLitersByPrefab["Hauler_A"] = 1000;
            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;

            // control: the same hull queues when nothing bans it
            Assert.Equal(PlayerQueueResult.Ok, mgr.QueueBuild(world, world.Tuning, "F", "N1", "Trade", "Hauler_A", out bom, log));
            int queued = node.BuildQueue.Count;

            world.PrefabOverCeiling = id => id == "Hauler_A";
            Assert.Equal(PlayerQueueResult.OverPerformanceCeiling,
                mgr.QueueBuild(world, world.Tuning, "F", "N1", "Trade", "Hauler_A", out bom, log));
            Assert.Equal(queued, node.BuildQueue.Count);
        }

        [Fact]
        public void QueueBuild_RejectsPrefabWithNoRegisteredRecipe()
        {
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            prefabs.CargoLitersByPrefab["Ghost_Hauler"] = 1000; // suitable, but no recipe registered

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;
            PlayerQueueResult result = mgr.QueueBuild(world, world.Tuning, "F", "N1", "Trade", "Ghost_Hauler", out bom, log);

            Assert.Equal(PlayerQueueResult.UnknownPrefab, result);
        }

        [Fact]
        public void QueueBuild_RejectsNotOwned()
        {
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            recipes.CostByPrefab["Hauler_A"] = 40;
            prefabs.CargoLitersByPrefab["Hauler_A"] = 1000;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;
            PlayerQueueResult result = mgr.QueueBuild(world, world.Tuning, "OTHER", "N1", "Trade", "Hauler_A", out bom, log);

            Assert.Equal(PlayerQueueResult.NotOwned, result);
            Assert.Empty(node.BuildQueue);
        }

        [Fact]
        public void QueueBuild_RejectsNoShipyard()
        {
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F"); // no Shipyard capability installed
            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Hauler_A"] = 40;
            world.Recipes = recipes;
            var prefabs = new FakePrefabCatalogue();
            prefabs.CargoLitersByPrefab["Hauler_A"] = 1000;
            world.PrefabCatalogue = prefabs;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;
            PlayerQueueResult result = mgr.QueueBuild(world, world.Tuning, "F", "N1", "Trade", "Hauler_A", out bom, log);

            Assert.Equal(PlayerQueueResult.NoShipyard, result);
        }

        [Fact]
        public void QueueBuild_RefusesTheRetiredStoreRole_RatherThanAcceptingItSilently()
        {
            // Replaces QueueBuild_AcceptsStoreLargePrefab_ByGridSize. That test asserted a player
            // could queue "StoreLarge" and have it validated by grid size alone; FAC-060 retired the
            // role, so the honest outcome is a refusal. Worth a test rather than a deletion: the
            // validation path is a role-string lookup, and a lookup that quietly accepted an unknown
            // string would queue a build nothing can ever satisfy.
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            recipes.CostByPrefab["Big_Freighter"] = 200;
            prefabs.GridSizeByPrefab["Big_Freighter"] = "Large";
            prefabs.CargoLitersByPrefab["Big_Freighter"] = 90000;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;

            PlayerQueueResult retired = mgr.QueueBuild(world, world.Tuning, "F", "N1", "StoreLarge",
                "Big_Freighter", out bom, log);
            // UnknownRole, not UnknownPrefab: the retired name is refused as a role rather than
            // surviving to blame the hull. Pre-registered as UnknownPrefab and corrected to what the
            // code does, which is the better of the two -- a player who types the old role gets told
            // the role is gone, not that their perfectly good freighter is wrong.
            Assert.Equal(PlayerQueueResult.UnknownRole, retired);
            Assert.Empty(node.BuildQueue);

            // The same hull under a role that still exists is accepted, so the refusal above is
            // about the retired role and not about the prefab being unusable.
            PlayerQueueResult live = mgr.QueueBuild(world, world.Tuning, "F", "N1", "Trade",
                "Big_Freighter", out bom, log);
            Assert.Equal(PlayerQueueResult.Ok, live);
            Assert.Single(node.BuildQueue);
        }

        [Fact]
        public void QueueBuild_AcceptsMinePrefab_RealDrillPlusCargo()
        {
            // "Mine" is not one of Tick's own proactively-auto-built roles, but it is a fully live
            // role in the same generic Ship:{role} pipeline (OperationsManager's real Mine dispatch,
            // shipped 2026-08-16, posts real Ship:Mine demands through this exact path) -- a player
            // must be able to request it too. IsMineSuitable requires BOTH a real drill (nonzero
            // DrillYieldScore) and cargo capacity -- a Trade hauler alone must not qualify.
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            recipes.CostByPrefab["Driller_A"] = 60;
            prefabs.DrillYieldScoreByPrefab["Driller_A"] = 500;
            prefabs.CargoLitersByPrefab["Driller_A"] = 800;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;
            PlayerQueueResult result = mgr.QueueBuild(world, world.Tuning, "F", "N1", "Mine", "Driller_A", out bom, log);

            Assert.Equal(PlayerQueueResult.Ok, result);
            BuildQueueEntry entry = node.BuildQueue.Find(e => e.Role == "Mine");
            Assert.NotNull(entry);
            Assert.Equal("Driller_A", entry.PrefabId);
        }

        [Fact]
        public void QueueBuild_RejectsMinePrefab_WithNoDrill()
        {
            // Cargo alone (a plain Trade hauler) must not satisfy Mine -- IsMineSuitable requires a
            // real drill.
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            recipes.CostByPrefab["Hauler_NoDrill"] = 40;
            prefabs.CargoLitersByPrefab["Hauler_NoDrill"] = 1000; // cargo, but no drill

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;
            PlayerQueueResult result = mgr.QueueBuild(world, world.Tuning, "F", "N1", "Mine", "Hauler_NoDrill", out bom, log);

            Assert.Equal(PlayerQueueResult.UnknownPrefab, result);
            Assert.Empty(node.BuildQueue);
        }

        [Fact]
        public void CancelBuild_RefundsCollectedAndReservedInputs_RemovesEntry()
        {
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");

            var entry = new BuildQueueEntry { Id = "Raid:001", Role = "Raid", PrefabId = "Warship_A" };
            entry.Bom["Comp:SteelPlate"] = 60;
            entry.Collected["Comp:SteelPlate"] = 35;
            entry.ReservedInputs["Ore:Iron"] = 20;
            node.BuildQueue.Add(entry);

            var mgr = new ShipyardManager();
            var log = new List<string>();
            BuildQueueEntry cancelled;
            PlayerQueueResult result = mgr.CancelBuild(world, "F", "N1", "Raid:001", out cancelled, log);

            Assert.Equal(PlayerQueueResult.Ok, result);
            Assert.Same(entry, cancelled);
            Assert.Empty(node.BuildQueue);
            Assert.Equal(35, node.Inventory["Comp:SteelPlate"]);
            Assert.Equal(20, node.Inventory["Ore:Iron"]);
            Assert.Contains(log, l => l.Contains("BUILD_CANCELLED_BY_PLAYER") && l.Contains("Warship_A"));
        }

        [Fact]
        public void CancelBuild_RejectsUnknownEntryId()
        {
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.BuildQueue.Add(new BuildQueueEntry { Id = "Raid:001", Role = "Raid" });

            var mgr = new ShipyardManager();
            var log = new List<string>();
            BuildQueueEntry cancelled;
            PlayerQueueResult result = mgr.CancelBuild(world, "F", "N1", "Trade:999", out cancelled, log);

            Assert.Equal(PlayerQueueResult.EntryNotFound, result);
            Assert.Null(cancelled);
            Assert.Single(node.BuildQueue);
        }

        [Fact]
        public void CancelBuild_RejectsNotOwned()
        {
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.BuildQueue.Add(new BuildQueueEntry { Id = "Raid:001", Role = "Raid" });

            var mgr = new ShipyardManager();
            var log = new List<string>();
            BuildQueueEntry cancelled;
            PlayerQueueResult result = mgr.CancelBuild(world, "OTHER", "N1", "Raid:001", out cancelled, log);

            Assert.Equal(PlayerQueueResult.NotOwned, result);
            Assert.Single(node.BuildQueue);
        }

        [Fact]
        public void PlayerQueuedEntry_IsServicedByNextTick_NoExtraWiringNeeded()
        {
            // Proves no extra wiring is needed: Tick's normal per-tick servicing (orphan pass, or
            // the live-demand path if PostProactiveShipDemands also fires one for this role/node)
            // finds and services a player-created entry exactly like any brain-created one,
            // absorbing available inventory on the very next manager tick.
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            recipes.CostByPrefab["Hauler_A"] = 40;
            prefabs.CargoLitersByPrefab["Hauler_A"] = 1000;
            node.Inventory["Comp:Steel"] = 40;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Dictionary<string, long> bom;
            Assert.Equal(PlayerQueueResult.Ok,
                mgr.QueueBuild(world, world.Tuning, "F", "N1", "Trade", "Hauler_A", out bom, log));

            log.Clear();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_BUILT") && l.Contains("Hauler_A"));
            int stock;
            node.ShipStock.TryGetValue("Hauler_A", out stock);
            Assert.Equal(1, stock);
        }

        // ---- idle-ship relocation (0-cargo Haul) --------------------------------------------

        /// <summary>Catalogue for the relocation tests: "Miner*" prefabs are drill+cargo suitable,
        /// "Gunship" is combat-suitable, everything ships with optional scanned mass so a uniform-
        /// gravity world can make a donor route-infeasible.</summary>
        private sealed class RelocationPrefabCatalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, double> DryMassByPrefab = new Dictionary<string, double>();
            public long CargoLiters(string prefabId) { return prefabId == "Gunship" ? 0 : 1000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
                => prefabId == "Gunship" ? new[] { new WeaponBand { Dps = 500, RangeM = 800 } } : new WeaponBand[0];
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
                { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId)
            {
                double v;
                return DryMassByPrefab.TryGetValue(prefabId, out v) ? v : 0;
            }
            public double DrillYieldScore(string prefabId)
                => !string.IsNullOrEmpty(prefabId) && prefabId.StartsWith("Miner", StringComparison.Ordinal) ? 1000 : 0;
        }

        private sealed class UniformGravity : IPlanetCatalogue
        {
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return 1.0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        // ---- FAC-237: the union pool, and the tie-break that keeps faction identity -------------

        [Fact]
        public void FleetBuild_TiedCost_PrefersTheFactionsOwnHullOverAGenericOne()
        {
            // The pool now arrives as the UNION, faction-specific first. At equal cost the
            // faction's own hull must still win, or the union would have quietly replaced faction
            // identity with "whatever the catalogue lists cheapest".
            //
            // WHAT THIS TEST DOES NOT PROVE, established by mutation rather than assumed: it
            // does NOT catch removal of the pool-position tie-break. Deleting that term leaves
            // this green -- checked across three runs at both 2 and 20 tied candidates, because
            // List<T>.Sort's introsort happens to leave element 0 in place either way. So this is
            // an end-to-end smoke test that the pool reaches the ranker and a build is queued;
            // the preference itself is proved by
            // PrefabRolePoolTests.AtEqualCost_TheEarlierPoolPositionWins, which can fail.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.PrefabCatalogue = new FakePrefabCatalogue();

            // Twenty tied candidates rather than two. The intent was to push past introsort's
            // insertion-sort threshold so the sort would genuinely reorder equal elements -- that
            // did NOT work (see above), and the count is kept only because a wide tied field is a
            // more realistic pool than a pair.
            var recipes = new FakeRecipeRegistry();
            var pool = new List<string> { "Own_Hull" };
            recipes.CostByPrefab["Own_Hull"] = 40;
            for (int i = 0; i < 19; i++)
            {
                string id = "Generic_Hull_" + i.ToString("00");
                pool.Add(id);
                recipes.CostByPrefab[id] = 40;   // every candidate exactly tied
            }
            world.Recipes = recipes;

            PostShipDemand(world, "Trade", "N1");

            // Specific first, as PrefabRolePool.Combine produces.
            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => pool };
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, new List<string>());

            BuildQueueEntry entry = node.BuildQueue.Find(e => !e.DestinedForSale);
            Assert.NotNull(entry);
            Assert.Equal("Own_Hull", entry.PrefabId);
        }

        [Fact]
        public void FleetBuild_ACheaperGenericHullStillWins_OrTheUnionWouldChangeNothing()
        {
            // The paired direction, and the one that makes the change worth making. Without it the
            // test above passes for a rule that simply always picks the faction's own hull -- which
            // is the override this ticket removed, reintroduced as a tie-break. FAC-235 shipped
            // inert for exactly this shape of mistake, so both directions are asserted.
            var world = MakeWorld();
            var node  = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.PrefabCatalogue = new FakePrefabCatalogue();

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Own_Hull"]     = 40;
            recipes.CostByPrefab["Generic_Hull"] = 39;   // one unit cheaper
            world.Recipes = recipes;

            PostShipDemand(world, "Trade", "N1");

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Own_Hull", "Generic_Hull" } };
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, new List<string>());

            BuildQueueEntry entry = node.BuildQueue.Find(e => !e.DestinedForSale);
            Assert.NotNull(entry);
            Assert.Equal("Generic_Hull", entry.PrefabId);
        }

        private static void PostShipDemand(WorldModel world, string role, string nodeId,
            string destNodeId = null, string viaNodeId = null, double minCargoLiters = 0)
        {
            world.Demand.Post(new DemandEntry
            {
                FactionTag     = "F",
                Kind           = "Ship:" + role,
                NodeId         = nodeId,
                DestNodeId     = destNodeId,
                ViaNodeId      = viaNodeId,
                MinCargoLiters = minCargoLiters,
                Quantity       = 1,
                ExpiresUtc     = world.UtcNow.AddSeconds(30),
            });
        }

        private static PendingFleetAction? RelocationFleetOrNull(FactionState st)
        {
            foreach (PendingFleetAction f in st.PendingFleets)
                if (f.RelocationRole != null) return f;
            return null;
        }

        // ---- build abandonment: no progress, not elapsed time --------------------------------

        /// <summary>Yard with a Shipyard, one partially-collected build, and no inventory to absorb —
        /// the shape every stall test needs.</summary>
        private static SpatialNode StalledYard(WorldModel world, out BuildQueueEntry entry, long collected = 40)
        {
            SpatialNode node = AddStation(world, "Yard", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            entry = new BuildQueueEntry
            {
                Id = "Raid:001", Role = "Raid", PrefabId = "Ridill",
                CreatedUtc = world.UtcNow,
                ExpiresUtc = world.UtcNow.AddHours(world.Tuning.MaxBuildDurationHours),
            };
            entry.Bom["Comp:SteelPlate"] = 100;
            entry.Collected["Comp:SteelPlate"] = collected;
            node.BuildQueue.Add(entry);
            return node;
        }

        [Fact]
        public void Build_AbsorbingComponents_IsNeverAbandoned_HoweverLongItTakes()
        {
            var world = MakeWorld();
            world.Tuning.BuildNoProgressHours = 1.0;
            BuildQueueEntry entry;
            SpatialNode node = StalledYard(world, out entry, collected: 0);
            // Big enough that absorbing one per sweep cannot finish it inside the window -- otherwise
            // the build completes in an hour and the test never reaches the elapsed time it claims to.
            entry.Bom["Comp:SteelPlate"] = 100000;

            var st = MakeState();
            var mgr = new ShipyardManager();
            var log = new List<string>();
            // Deliberately runs PAST the old MaxBuildDurationHours backstop (24h) in wall-clock terms
            // while absorbing one plate per sweep. The first version of this test only ran an hour,
            // so it never crossed the backstop and did not catch that the backstop would still refund
            // a build that was plainly making progress -- which was the whole point of the change.
            // Live 2026-08-22: EXP Raid:001 was absorbing (Comp:Thrust 2104 -> 2113) and would still
            // have been dropped at its 24h mark.
            DateTime start = world.UtcNow;
            for (int i = 0; i < 2000 && node.BuildQueue.Count > 0; i++)
            {
                world.AddNodeInventory("Yard", "Comp:SteelPlate", 1);
                world.UtcNow = world.UtcNow.AddSeconds(world.Tuning.ShipyardIntervalSeconds);
                mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);
            }
            Assert.True((world.UtcNow - start).TotalHours > world.Tuning.MaxBuildDurationHours,
                "test must outrun MaxBuildDurationHours to be meaningful");
            // Either it completed (60 plates were needed) or it is still queued -- never abandoned.
            Assert.DoesNotContain(log, l => l.Contains("BUILD_EXPIRED"));
            Assert.Equal(0.0, entry.StalledSeconds);
        }

        [Fact]
        public void Build_FedOnlyByItsOwnProduction_ResetsTheStallClock_RegressionGuard()
        {
            // Live 2026-08-22: EXP Raid:001 was visibly filling up -- Comp:Thrust 2113 -> 2122 -- while
            // StalledSeconds climbed 3443 -> 4337 straight through it, because ShipyardManager only
            // observes its own absorb-from-inventory loop. ProductionManager manufactures components
            // into Collected via IProductionExecutionContext.AddBuildCollected, a second path the
            // abandonment clock could not see, so a build fed entirely by its own dedicated production
            // was on course to be refunded for making no progress while making progress.
            var world = MakeWorld();
            world.Tuning.BuildNoProgressHours = 1.0;
            BuildQueueEntry entry;
            SpatialNode node = StalledYard(world, out entry);
            entry.StalledSeconds = 3000;

            var ctx = new LiveProductionExecutionContext(world, new List<string>());
            ctx.AddBuildCollected(node.Id, entry.Id, "Comp:SteelPlate", 9);

            Assert.Equal(0.0, entry.StalledSeconds);
            Assert.Equal(49, entry.Collected["Comp:SteelPlate"]); // 40 staged + 9 manufactured
        }

        [Fact]
        public void Build_ProductionRemovingCollected_DoesNotResetTheStallClock()
        {
            // Only a positive delta is progress. A negative one (a refund or correction) must not
            // launder itself into a fresh stall budget.
            var world = MakeWorld();
            BuildQueueEntry entry;
            SpatialNode node = StalledYard(world, out entry);
            entry.StalledSeconds = 3000;

            var ctx = new LiveProductionExecutionContext(world, new List<string>());
            ctx.AddBuildCollected(node.Id, entry.Id, "Comp:SteelPlate", -5);

            Assert.Equal(3000.0, entry.StalledSeconds);
        }

        [Fact]
        public void Build_NoProgress_IsAbandonedAfterBuildNoProgressHours_WithReason()
        {
            var world = MakeWorld();
            world.Tuning.BuildNoProgressHours = 1.0;
            BuildQueueEntry entry;
            SpatialNode node = StalledYard(world, out entry);

            var st = MakeState();
            var mgr = new ShipyardManager();
            var log = new List<string>();
            // Nothing in inventory to absorb: pure stall. One interval per sweep.
            for (int i = 0; i < 200 && node.BuildQueue.Count > 0; i++)
            {
                world.UtcNow = world.UtcNow.AddSeconds(world.Tuning.ShipyardIntervalSeconds);
                mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);
            }

            Assert.Empty(node.BuildQueue);
            Assert.Contains(log, l => l.Contains("BUILD_EXPIRED") && l.Contains("reason=no_progress"));
            // Refunded, not destroyed.
            Assert.Equal(40, world.GetNodeInventory("Yard", "Comp:SteelPlate"));
        }

        [Fact]
        public void Build_OvernightShutdown_DoesNotCountAsNoProgress_RegressionGuard()
        {
            // The host is a desktop that gets shut down overnight, and WorldModel.UtcNow is real UTC.
            // An unclamped elapsed check would read a 10-hour gap as 10 hours of no progress and
            // abandon every build in the network on the next boot.
            var world = MakeWorld();
            world.Tuning.BuildNoProgressHours = 6.0;
            BuildQueueEntry entry;
            SpatialNode node = StalledYard(world, out entry);

            var st = MakeState();
            var mgr = new ShipyardManager();
            var log = new List<string>();

            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);   // one normal sweep
            world.UtcNow = world.UtcNow.AddHours(10);                            // machine off overnight
            mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);   // first sweep back

            Assert.Single(node.BuildQueue);
            Assert.DoesNotContain(log, l => l.Contains("BUILD_EXPIRED"));
            // The whole 10h collapses to the clamp: two Shipyard cadences, not 36,000 seconds.
            Assert.True(entry.StalledSeconds <= world.Tuning.ShipyardIntervalSeconds * 2.0 + 0.001,
                "10h shutdown must clamp, got " + entry.StalledSeconds);
        }

        [Fact]
        public void Build_PausedYard_AccruesNoStallTime_AndResumeRebasesTheClock()
        {
            var world = MakeWorld();
            world.Tuning.BuildNoProgressHours = 1.0;
            BuildQueueEntry entry;
            SpatialNode node = StalledYard(world, out entry);
            node.ShipyardPaused = true;

            var st = MakeState();
            var mgr = new ShipyardManager();
            var log = new List<string>();
            for (int i = 0; i < 200; i++)
            {
                world.UtcNow = world.UtcNow.AddSeconds(world.Tuning.ShipyardIntervalSeconds);
                mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);
            }

            Assert.Single(node.BuildQueue);          // a pause freezes a build, never refunds it
            Assert.Equal(0.0, entry.StalledSeconds);
            Assert.DoesNotContain(log, l => l.Contains("BUILD_EXPIRED"));
        }

        [Fact]
        public void Build_WithLiveDemand_StillStalls_NoLongerImmortal_RegressionGuard()
        {
            // Before this change a live Ship demand refreshed ExpiresUtc every tick, so a
            // demand-backed build never expired however stuck it was, while an orphan-fed one aged
            // out on a fixed TTL no matter how much it had collected. Both now follow one rule.
            var world = MakeWorld();
            world.Tuning.BuildNoProgressHours = 1.0;
            BuildQueueEntry entry;
            SpatialNode node = StalledYard(world, out entry);
            entry.Role = "Trade";
            entry.Id = "Trade:001";

            var st = MakeState();
            var mgr = new ShipyardManager();
            var log = new List<string>();
            for (int i = 0; i < 200 && node.BuildQueue.Count > 0; i++)
            {
                PostShipDemand(world, "Trade", "Yard");   // re-posted every sweep, as Operations does
                world.UtcNow = world.UtcNow.AddSeconds(world.Tuning.ShipyardIntervalSeconds);
                mgr.Tick(new List<FactionState> { st }, world, world.Tuning, log);
            }

            Assert.Empty(node.BuildQueue);
            Assert.Contains(log, l => l.Contains("BUILD_EXPIRED") && l.Contains("reason=no_progress"));
        }

        // ---- demand retirement (met without a build) ----------------------------------------

        [Fact]
        public void ShipDemand_OriginNodeAlreadyHasSuitableHull_RetiresDemandAndLogsMet_NoRelocationNoBuild()
        {
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();
            world.Planets = new UniformGravity();
            SpatialNode need  = AddStation(world, "NeedN", "F");
            need.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            need.ShipStock["Miner"] = 1;                       // the thing the demand is asking for
            SpatialNode donor = AddStation(world, "Donor", "F");
            donor.ShipStock["Miner"] = 3;
            PostShipDemand(world, "Mine", "NeedN", destNodeId: "NeedN", minCargoLiters: 1);

            var st  = MakeState();
            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_MET") && l.Contains("role=Mine")
                                      && l.Contains("node=NeedN") && l.Contains("retired=1"));
            Assert.Null(RelocationFleetOrNull(st));
            Assert.Empty(need.BuildQueue);
            DemandEntry gone;
            Assert.False(world.Demand.TryGet("F", "Ship:Mine", "NeedN", "NeedN", out gone));
        }

        [Fact]
        public void ShipDemand_TwoNodesEachDemandingSameRole_DoNotRelocateIntoEachOther_RegressionGuard()
        {
            // Live 2026-08-22 (EXP, N048 <-> N8466): both nodes held a Mine-suitable hull and both
            // carried a live Ship:Mine demand, so each relocated its only hull to the other every ops
            // cycle indefinitely -- four dispatches per cycle, net zero, demands never retired.
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();
            world.Planets = new UniformGravity();
            SpatialNode a = AddStation(world, "A", "F", new Vec3(0, 0, 0));
            SpatialNode b = AddStation(world, "B", "F", new Vec3(10, 0, 0));
            a.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            b.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            a.ShipStock["Miner"] = 1;
            b.ShipStock["Miner"] = 1;
            PostShipDemand(world, "Mine", "A", destNodeId: "A", minCargoLiters: 1);
            PostShipDemand(world, "Mine", "B", destNodeId: "B", minCargoLiters: 1);

            var st  = MakeState();
            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.DoesNotContain(log, l => l.Contains("SHIP_RELOCATE_DISPATCHED"));
            Assert.Null(RelocationFleetOrNull(st));
            Assert.Equal(1, a.ShipStock["Miner"]);
            Assert.Equal(1, b.ShipStock["Miner"]);
        }

        [Fact]
        public void MetGate_TestsGroupMaxMinCargoLiters_NotTheMedianRepresentative()
        {
            // Three demands share one (node, role) build slot. The median cargo need fits the hull in
            // stock; the largest does not. Retiring on the median would drop a need nothing can serve.
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();  // CargoLiters = 1000 for "Miner*"
            world.Planets = new UniformGravity();
            SpatialNode need = AddStation(world, "NeedN", "F"); // no Shipyard: nothing here may complete a build
            need.ShipStock["MinerSmall"] = 1;
            PostShipDemand(world, "Mine", "NeedN", destNodeId: "D1", minCargoLiters: 10);
            PostShipDemand(world, "Mine", "NeedN", destNodeId: "D2", minCargoLiters: 500);
            PostShipDemand(world, "Mine", "NeedN", destNodeId: "D3", minCargoLiters: 9000);

            var st  = MakeState();
            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_MET"));
            DemandEntry still;
            Assert.True(world.Demand.TryGet("F", "Ship:Mine", "NeedN", "D3", out still));
        }

        [Fact]
        public void BuildCompletion_RetiresEverySiblingDestDemand_ForThatNodeAndRole()
        {
            var world = MakeWorld();   // no PrefabCatalogue => free build, completes on the first tick
            SpatialNode yard = AddStation(world, "Yard", "F");
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            PostShipDemand(world, "Trade", "Yard", destNodeId: "D1");
            PostShipDemand(world, "Trade", "Yard", destNodeId: "D2");
            PostShipDemand(world, "Trade", "Yard", destNodeId: "D3");

            var st  = MakeState();
            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_BUILT") && l.Contains("node=Yard"));
            DemandEntry gone;
            Assert.False(world.Demand.TryGet("F", "Ship:Trade", "Yard", "D1", out gone));
            Assert.False(world.Demand.TryGet("F", "Ship:Trade", "Yard", "D2", out gone));
            Assert.False(world.Demand.TryGet("F", "Ship:Trade", "Yard", "D3", out gone));
        }

        [Fact]
        public void MidCollectionBuild_IsNeverRetiredByMetGate()
        {
            // The met-gate is deliberately scoped to entry == null: a build already collecting
            // components must not be abandoned just because a usable hull turned up at the node.
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();
            world.Planets = new UniformGravity();
            SpatialNode yard = AddStation(world, "Yard", "F");
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            yard.ShipStock["Miner"] = 1;
            var entry = new BuildQueueEntry
            {
                Id = "Mine:001", Role = "Mine", PrefabId = "Miner",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(4),
            };
            entry.Bom["Comp:SteelPlate"] = 100;
            entry.Collected["Comp:SteelPlate"] = 40;
            yard.BuildQueue.Add(entry);
            PostShipDemand(world, "Mine", "Yard", destNodeId: "Yard", minCargoLiters: 1);

            var st  = MakeState();
            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_MET"));
            Assert.Single(yard.BuildQueue);
            Assert.Equal(40, yard.BuildQueue[0].Collected["Comp:SteelPlate"]);
        }

        [Fact]
        public void ShipMineDemand_IdleSuitableShipElsewhere_DispatchesRelocationHaul_InsteadOfBuild()
        {
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();
            SpatialNode need  = AddStation(world, "NeedN", "F");  // no Shipyard capability on purpose
            SpatialNode donor = AddStation(world, "Donor", "F");
            donor.ShipStock["Miner"] = 1;
            PostShipDemand(world, "Mine", "NeedN", destNodeId: "Hub", minCargoLiters: 1);

            var st  = MakeState();
            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            PendingFleetAction? relocOrNull = RelocationFleetOrNull(st);
            Assert.True(relocOrNull.HasValue, "expected a relocation fleet");
            PendingFleetAction reloc = relocOrNull.Value;
            Assert.Equal(ActionKind.Haul, reloc.Kind);
            Assert.Equal("Mine", reloc.RelocationRole);
            Assert.Null(reloc.Cargo);
            Assert.Equal("Donor", reloc.OriginNodeId);
            Assert.Equal("NeedN", reloc.TargetNodeId);
            Assert.Equal("Miner", reloc.ShipPrefabId);
            int donorStock;
            donor.ShipStock.TryGetValue("Miner", out donorStock);
            Assert.Equal(0, donorStock);
            Assert.DoesNotContain(need.BuildQueue, e => e.Role == "Mine");
            Assert.Contains(log, l => l.Contains("SHIP_RELOCATE_DISPATCHED role=Mine")
                && l.Contains("Donor -> NeedN") && l.Contains("for_dest=Hub"));
        }

        [Fact]
        public void Relocation_RouteInfeasibleDonor_FallsThroughToBuildPath()
        {
            var world = MakeWorld();
            var cat = new RelocationPrefabCatalogue();
            cat.DryMassByPrefab["Miner"] = 10_000; // scanned mass + zero thrust + 1g = stranded donor
            world.PrefabCatalogue = cat;
            world.Planets = new UniformGravity();
            SpatialNode need  = AddStation(world, "NeedN", "F");
            need.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            SpatialNode donor = AddStation(world, "Donor", "F");
            donor.ShipStock["Miner"] = 1;
            PostShipDemand(world, "Mine", "NeedN", minCargoLiters: 1);

            var st  = MakeState();
            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.False(RelocationFleetOrNull(st).HasValue);
            Assert.Equal(1, donor.ShipStock["Miner"]); // untouched
            // Free-build mode (no PrefabListPicker) completes the build immediately at the shipyard.
            Assert.Contains(log, l => l.Contains("SHIP_BUILT role=Mine"));
        }

        [Fact]
        public void Relocation_InFlight_SuppressesSecondRelocationAndBuild()
        {
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();
            SpatialNode need  = AddStation(world, "NeedN", "F");
            need.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            SpatialNode donor = AddStation(world, "Donor", "F");
            donor.ShipStock["Miner"] = 1;
            PostShipDemand(world, "Mine", "NeedN", minCargoLiters: 1);

            var st = MakeState();
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, RelocationRole = "Mine", ActorTag = "F",
                OriginNodeId = "Elsewhere", TargetNodeId = "NeedN",
                DepartedUtc = world.UtcNow, EstimatedArrivalUtc = world.UtcNow.AddMinutes(5),
                ShipPrefabId = "Miner", ConvoySize = 1, FleetId = "RELOC0", LastResolvedSegment = -1,
            });

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Single(st.PendingFleets); // no second relocation
            Assert.Equal(1, donor.ShipStock["Miner"]);
            Assert.DoesNotContain(need.BuildQueue, e => e.Role == "Mine");
            Assert.DoesNotContain(log, l => l.Contains("SHIP_BUILT role=Mine"));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_RELOCATE_DISPATCHED"));
        }

        [Fact]
        public void Relocation_ExistingBuildEntry_IsNeverAbandonedForRelocation()
        {
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();
            SpatialNode need  = AddStation(world, "NeedN", "F");
            need.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            var entry = new BuildQueueEntry
            {
                Id = "Mine:001", Role = "Mine", PrefabId = "Miner",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(4),
            };
            entry.Bom["Comp:SteelPlate"] = 100; // mid-collection -- must not complete instantly
            need.BuildQueue.Add(entry);
            SpatialNode donor = AddStation(world, "Donor", "F");
            donor.ShipStock["Miner"] = 1;
            PostShipDemand(world, "Mine", "NeedN", minCargoLiters: 1);

            var st  = MakeState();
            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.False(RelocationFleetOrNull(st).HasValue);
            Assert.Equal(1, donor.ShipStock["Miner"]);
            Assert.Contains(need.BuildQueue, e => e.Id == "Mine:001");
        }

        [Fact]
        public void Relocation_NonWhitelistedRole_NeverRelocates()
        {
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();
            AddStation(world, "NeedN", "F"); // no shipyard: a build cannot mask a wrong relocation
            SpatialNode donor = AddStation(world, "Donor", "F");
            donor.ShipStock["Gunship"] = 1; // Raid-suitable stock exists, idle
            PostShipDemand(world, "Raid", "NeedN");

            var st  = MakeState();
            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.False(RelocationFleetOrNull(st).HasValue);
            Assert.Equal(1, donor.ShipStock["Gunship"]);
        }

        [Fact]
        public void Relocation_RespectsListedForSale()
        {
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();
            AddStation(world, "NeedN", "F");
            SpatialNode donor = AddStation(world, "Donor", "F");
            donor.ShipStock["Miner"] = 1;
            donor.ListedForSale["Miner"] = 1; // reserved at the store block -- invisible to dispatch
            PostShipDemand(world, "Mine", "NeedN", minCargoLiters: 1);

            var st  = MakeState();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            Assert.False(RelocationFleetOrNull(st).HasValue);
            Assert.Equal(1, donor.ShipStock["Miner"]);
        }

        [Fact]
        public void RelocationHaul_Resolves_CreditsShipToDestinationStock_LogsRelocateArrived()
        {
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();
            AddStation(world, "Donor", "F");
            SpatialNode need = AddStation(world, "NeedN", "F");

            var st = MakeState();
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, RelocationRole = "Mine", ActorTag = "F",
                OriginNodeId = "Donor", TargetNodeId = "NeedN",
                DepartedUtc = world.UtcNow.AddSeconds(-60), EstimatedArrivalUtc = world.UtcNow.AddSeconds(-1),
                ShipPrefabId = "Miner", ConvoySize = 1, CargoCapacityLiters = 1000,
                FleetId = "RELOC1", LastResolvedSegment = -1,
            });

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, TestSupport.Tuning(),
                new SeededRandom(1), log);

            Assert.Equal(1, need.ShipStock["Miner"]);
            Assert.Contains(log, l => l.Contains("SHIP_RELOCATE_ARRIVED") && l.Contains("role=Mine") && l.Contains("NeedN"));
            Assert.Contains(log, l => l.Contains("HAUL_SHIP_RETURNED"));
            Assert.DoesNotContain(log, l => l.Contains("HAUL_RESOLVED"));
        }

        [Fact]
        public void RelocationHaul_Intercepted_ShipLost_NoCredit()
        {
            var world = MakeWorld();
            world.PrefabCatalogue = new RelocationPrefabCatalogue();
            AddStation(world, "Donor", "F");
            SpatialNode need = AddStation(world, "NeedN", "F");

            var st = MakeState();
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, RelocationRole = "Mine", ActorTag = "F",
                OriginNodeId = "Donor", TargetNodeId = "NeedN",
                DepartedUtc = world.UtcNow.AddSeconds(-60), EstimatedArrivalUtc = world.UtcNow.AddSeconds(-1),
                ShipPrefabId = "Miner", ConvoySize = 1, CargoCapacityLiters = 1000,
                FleetId = "RELOC2", LastResolvedSegment = -1, ForcedRatio = 0.0,
            });

            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world, TestSupport.Tuning(),
                new SeededRandom(1), log);

            int stock;
            need.ShipStock.TryGetValue("Miner", out stock);
            Assert.Equal(0, stock);
            Assert.Contains(log, l => l.Contains("HAUL_SHIP_LOST"));
        }

        // ---- pause + priority ---------------------------------------------------------------

        /// <summary>An unexpired entry at a Shipyard-capable node with plenty of the BOM component
        /// sitting in routable inventory -- the shape every pause/priority test below varies.</summary>
        private static BuildQueueEntry AddBuild(WorldModel world, SpatialNode node, string id, string role,
            string key, long bom, long collected, long inventory, int priority = 0)
        {
            var entry = new BuildQueueEntry
            {
                Id = id, Role = role, PrefabId = "SomePrefab", Priority = priority,
                CreatedUtc = world.UtcNow.AddHours(-1),
                ExpiresUtc = world.UtcNow.AddHours(23),
            };
            entry.Bom[key] = bom;
            if (collected > 0) entry.Collected[key] = collected;
            node.BuildQueue.Add(entry);
            node.Inventory[key] = inventory;
            return entry;
        }

        private static WorldModel MakeYard(out SpatialNode node)
        {
            var world = MakeWorld();
            node = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();
            return world;
        }

        [Fact]
        public void Paused_AbsorbsNothing_AndPostsNoItemDemand()
        {
            SpatialNode node;
            var world = MakeYard(out node);
            BuildQueueEntry entry = AddBuild(world, node, "Trade:001", "Trade", "Comp:MetalGrid", 100, 40, 1000);
            node.ShipyardPaused = true;

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Equal(40, entry.Collected["Comp:MetalGrid"]);   // frozen exactly where it was
            Assert.Equal(1000, node.Inventory["Comp:MetalGrid"]);  // nothing taken
            Assert.DoesNotContain(world.Demand.SnapshotItemDemands("F"), d => d.Kind == "Item:Comp:MetalGrid");
        }

        [Fact]
        public void Paused_ReleasesUncollectedBomShare_ToOtherConsumers()
        {
            // The whole point of the pause: an install (or any other consumer) can see the component
            // the build has NOT taken yet. What the build already holds stays with it.
            SpatialNode node;
            var world = MakeYard(out node);
            AddBuild(world, node, "Trade:001", "Trade", "Comp:MetalGrid", 100, 40, 1000);

            Assert.Equal(60, node.BuildQueueReserved("Comp:MetalGrid", world.UtcNow));
            node.ShipyardPaused = true;
            Assert.Equal(0, node.BuildQueueReserved("Comp:MetalGrid", world.UtcNow));
        }

        [Fact]
        public void Paused_ExemptsEntriesFromExpiryPruning_AndResumeReArmsTheDeadline()
        {
            // A paused entry never takes the demand path that refreshes ExpiresUtc, so without the
            // exemption a long pause would refund and drop the build instead of freezing it.
            SpatialNode node;
            var world = MakeYard(out node);
            BuildQueueEntry entry = AddBuild(world, node, "Trade:001", "Trade", "Comp:MetalGrid", 100, 40, 1000);
            node.ShipyardPaused = true;
            entry.ExpiresUtc = world.UtcNow.AddHours(-1);   // already past its deadline

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Single(node.BuildQueue);
            Assert.Equal(40, entry.Collected["Comp:MetalGrid"]);   // not refunded

            Assert.Equal(PlayerQueueResult.Ok,
                mgr.PauseShipyard(world, world.Tuning, "F", "N1", false, log));
            Assert.False(node.ShipyardPaused);
            Assert.True(entry.ExpiresUtc > world.UtcNow);
            Assert.Contains(log, l => l.Contains("SHIPYARD_PAUSE_BY_PLAYER") && l.Contains("paused=False"));
        }

        [Fact]
        public void Paused_DoesNotCreateAnEntryForAnExternallyPostedShipDemand()
        {
            // OperationsManager/TradeManager/FleetManager/StoreStockManager/Enact all post Ship:{role}
            // demands of their own -- none of them knows the owner just paused this yard.
            SpatialNode node;
            var world = MakeYard(out node);
            node.Inventory["Comp:MetalGrid"] = 1000;
            node.ShipyardPaused = true;
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "N1",
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, new List<string>());

            Assert.Empty(node.BuildQueue);
        }

        [Fact]
        public void Resumed_AbsorbsAgain()
        {
            SpatialNode node;
            var world = MakeYard(out node);
            BuildQueueEntry entry = AddBuild(world, node, "Trade:001", "Trade", "Comp:MetalGrid", 100, 40, 1000);
            node.ShipyardPaused = true;

            var mgr = new ShipyardManager();
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);
            Assert.Equal(40, entry.Collected["Comp:MetalGrid"]);

            mgr.PauseShipyard(world, world.Tuning, "F", "N1", false, log);
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Equal(940, node.Inventory["Comp:MetalGrid"]);   // absorbed the outstanding 60
        }

        [Fact]
        public void HigherPriorityEntry_ClaimsScarceComponent_EvenWhenListedSecond()
        {
            // The low-priority entry sits FIRST in BuildQueue, so a plain list-order claim would give
            // it everything. The floor, not the iteration order, decides.
            SpatialNode node;
            var world = MakeYard(out node);
            BuildQueueEntry low  = AddBuild(world, node, "Trade:001", "Trade", "Comp:MetalGrid", 100, 0, 60);
            BuildQueueEntry high = AddBuild(world, node, "Raid:002", "Raid", "Comp:MetalGrid", 100, 0, 60, priority: 5);

            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, new List<string>());

            long lowCollected; low.Collected.TryGetValue("Comp:MetalGrid", out lowCollected);
            Assert.Equal(60, high.Collected["Comp:MetalGrid"]);   // took all of the scarce supply
            Assert.Equal(0, lowCollected);
        }

        [Fact]
        public void EqualPriorityEntries_KeepTheOriginalFirstComeClaimOrder()
        {
            // Regression: with no priority set anywhere, behaviour is exactly what it was -- the
            // first entry claims what it needs and a later one gets whatever survives.
            SpatialNode node;
            var world = MakeYard(out node);
            BuildQueueEntry first  = AddBuild(world, node, "Trade:001", "Trade", "Comp:MetalGrid", 100, 0, 60);
            BuildQueueEntry second = AddBuild(world, node, "Raid:002", "Raid", "Comp:MetalGrid", 100, 0, 60);

            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, new List<string>());

            long secondCollected; second.Collected.TryGetValue("Comp:MetalGrid", out secondCollected);
            Assert.Equal(60, first.Collected["Comp:MetalGrid"]);
            Assert.Equal(0, secondCollected);
        }

        [Fact]
        public void SetBuildPriority_RejectsUnknownEntry_AndForeignNode()
        {
            SpatialNode node;
            var world = MakeYard(out node);
            AddBuild(world, node, "Trade:001", "Trade", "Comp:MetalGrid", 100, 0, 60);

            var mgr = new ShipyardManager();
            var log = new List<string>();
            Assert.Equal(PlayerQueueResult.EntryNotFound,
                mgr.SetBuildPriority(world, "F", "N1", "Trade:999", 3, log));
            Assert.Equal(PlayerQueueResult.NotOwned,
                mgr.SetBuildPriority(world, "OTHER", "N1", "Trade:001", 3, log));

            Assert.Equal(PlayerQueueResult.Ok, mgr.SetBuildPriority(world, "F", "N1", "Trade:001", 3, log));
            Assert.Equal(3, node.BuildQueue[0].Priority);
            Assert.Contains(log, l => l.Contains("BUILD_PRIORITY_BY_PLAYER") && l.Contains("to=3"));
        }

        [Fact]
        public void PauseShipyard_RequiresAnInstalledShipyard()
        {
            var world = MakeWorld();
            SpatialNode node = AddStation(world, "N1", "F");   // no Shipyard capability
            Assert.Equal(PlayerQueueResult.NoShipyard,
                new ShipyardManager().PauseShipyard(world, world.Tuning, "F", "N1", true, new List<string>()));
            Assert.False(node.ShipyardPaused);
        }

        [Fact]
        public void OutrankingInstallClaim_HoldsBackAbsorption_ButNotForAnEqualRankedBuild()
        {
            // The symmetric half of the priority lever: relaxing ProductionManager's ShipBuildFloor
            // alone would only let the install claim on its own tick, with absorption taking the same
            // component straight back on the next one.
            SpatialNode node;
            var world = MakeYard(out node);
            world.Tuning.InstallBoms[NodeCapabilityKind.RefineryII] =
                new Dictionary<string, long> { { "Comp:MetalGrid", 50 } };
            world.Investments.Post(new InvestmentRequest
            {
                FactionTag = "F", Cap = NodeCapabilityKind.RefineryII, Urgency = 0.6,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });

            var st = MakeState();
            st.InstallPriority[NodeCapabilityKind.RefineryII] = 1;
            BuildQueueEntry entry = AddBuild(world, node, "Trade:001", "Trade", "Comp:MetalGrid", 100, 0, 60);

            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            long collected; entry.Collected.TryGetValue("Comp:MetalGrid", out collected);
            Assert.Equal(10, collected);          // 60 on hand, 50 left for the outranking install
            Assert.Equal(50, node.Inventory["Comp:MetalGrid"]);

            // Rank the build level with the install: a tie goes to the build, so it takes the rest.
            // Level means the install's EFFECTIVE rank -- a set install rank is lifted clear of the
            // role-derived ranks builds start at (FactionState.InstallRankFor) -- so this tracks the
            // role table through the same helper instead of a literal.
            entry.Priority = 1 + world.Tuning.HighestBuildRolePriority();
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());
            Assert.Equal(60, entry.Collected["Comp:MetalGrid"]);
        }
        [Fact]
        public void OutrankingStockOrder_HoldsBackAbsorption_ButNotForAnEqualRankedBuild()
        {
            // The shipyard half of a Stock order's rank (FAC-501): the production pass publishes the
            // claims, and absorption leaves an outranking order's target at its node.
            SpatialNode node;
            var world = MakeYard(out node);
            var st = MakeState();
            st.AddStandingOrder(new StandingOrder
            {
                Id = st.MintStandingOrderId(), Kind = StandingOrderKind.Stock, NodeId = node.Id,
                ItemKey = "Comp:MetalGrid", TargetQuantity = 50, Priority = 1,
            });
            BuildQueueEntry entry = AddBuild(world, node, "Trade:001", "Trade", "Comp:MetalGrid", 100, 0, 60);
            world.SetStockClaims(st.Tag, StockClaimFloor.Build(st, world.Graph, new[] { node },
                (n, k) => world.GetNodeInventory(n, k), world.Tuning, null, null));

            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            long collected; entry.Collected.TryGetValue("Comp:MetalGrid", out collected);
            Assert.Equal(10, collected);          // 60 on hand, 50 kept for the outranking order
            Assert.Equal(50, node.Inventory["Comp:MetalGrid"]);

            entry.Priority = StockClaimFloor.ClaimRank(1, world.Tuning);
            new ShipyardManager().Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());
            Assert.Equal(60, entry.Collected["Comp:MetalGrid"]);
        }

        // ---- fleet doctrine: the evaluator ---------------------------------------------------
        //
        // Role "Mine" throughout unless a test says otherwise: it is the one PlayerQueueRoles entry
        // ShipyardManager never posts a proactive demand for, so these tests exercise the evaluator
        // alone rather than racing the proactive path for the same (node, role) slot. The state is
        // deliberately an NPC in most of them -- ServiceDoctrine is species-blind, and only the two
        // gates below ask FactionLifecycle.DoctrineActive.

        private static SpatialNode AddShipyard(WorldModel world, string id, Vec3? pos = null)
        {
            SpatialNode n = AddStation(world, id, "F", pos);
            n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return n;
        }

        private static WorldModel MakeBuildGoalWorld(out FakeRecipeRegistry recipes, out FakePrefabCatalogue prefabs)
        {
            var world = MakeWorld();
            recipes = new FakeRecipeRegistry();
            world.Recipes = recipes;
            prefabs = new FakePrefabCatalogue();
            world.PrefabCatalogue = prefabs;
            recipes.CostByPrefab["Ridill"] = 40;
            // Mine suitability is drill yield AND cargo capacity (ShipPool.IsMineSuitableValue) --
            // a drill with nowhere to put the ore is not a mining ship.
            prefabs.DrillYieldScoreByPrefab["Ridill"] = 1.0;
            prefabs.CargoLitersByPrefab["Ridill"] = 1000;
            return world;
        }

        private static List<string> TickShipyard(WorldModel world, FactionState st,
            Func<string, string, IList<string>> prefabPicker = null)
        {
            var log = new List<string>();
            var manager = new ShipyardManager();
            if (prefabPicker != null) manager.PrefabListPicker = prefabPicker;
            manager.Tick(new List<FactionState> { st }, world, world.Tuning, log);
            return log;
        }

        /// <summary>A picker that offers exactly <paramref name="prefabId"/> for
        /// <paramref name="role"/> and nothing for anything else -- without one, the demand-driven
        /// path free-builds (no BOM) and the entry completes in the same tick it is created, so a
        /// test asserting on the queue sees an empty one whether the gate fired or not.</summary>
        private static Func<string, string, IList<string>> PickerFor(string role, string prefabId)
        {
            return (r, tag) => r == role ? new List<string> { prefabId } : null;
        }

        private static int CountEntriesFor(WorldModel world, string prefabId)
        {
            int n = 0;
            foreach (SpatialNode node in world.Graph.OwnedBy("F"))
                foreach (BuildQueueEntry e in node.BuildQueue)
                    if (e.PrefabId == prefabId) n++;
            return n;
        }

        [Fact]
        public void Doctrine_QueuesUpToConcurrency_NotTarget()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddShipyard(world, "N1");
            AddShipyard(world, "N2", new Vec3(100, 0, 0));
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 10, maxConcurrent: 1);

            List<string> log = TickShipyard(world, st);

            // The whole point of the line: ten wanted, one at a time -- not ten entries competing
            // for the same components.
            Assert.Equal(1, CountEntriesFor(world, "Ridill"));
            Assert.Contains(log, l => l.Contains("DOCTRINE_BUILD ") && l.Contains("target=10") && l.Contains("placed=1"));
        }

        [Fact]
        public void Doctrine_ConcurrencyAboveOne_SpreadsAcrossShipyards()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddShipyard(world, "N1");
            AddShipyard(world, "N2", new Vec3(100, 0, 0));
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 10, maxConcurrent: 3);

            TickShipyard(world, st);

            // Three requested, two shipyards: a line places at most one build per yard per pass, so
            // it places two and does not override anything to reach three.
            Assert.Equal(2, CountEntriesFor(world, "Ridill"));
        }

        /// <summary>FAC-310, as seen live on 2026-09-13 (v0.219.0): every EXP yard already held a
        /// Patrol entry, and a yard holding an entry of the line's role was skipped, so the Patrol line
        /// read <c>placeable=0</c> for hours while a Raid line on the same estate read 4. A yard's
        /// existing work, of whatever role, never makes it unplaceable for a line.</summary>
        [Fact]
        public void Doctrine_AYardAlreadyBuildingSomethingElse_IsStillPlaceable()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddCombatHull(recipes, prefabs, "Gunboat");
            AddCombatHull(recipes, prefabs, "OldGun");
            foreach (SpatialNode yard in new[] { AddShipyard(world, "N1"), AddShipyard(world, "N2", new Vec3(100, 0, 0)) })
            {
                var existing = new BuildQueueEntry
                {
                    Id = yard.MintBuildEntryId("Patrol"), Role = "Patrol", PrefabId = "OldGun",
                    CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
                };
                existing.Bom["Comp:Steel"] = 40;
                yard.BuildQueue.Add(existing);
            }
            var st = MakePlayerState();
            StandingOrderFixtures.AddDoctrineLine(st, "Gunboat", targetCount: 10, maxConcurrent: 2);

            List<string> log = TickShipyard(world, st);

            Assert.Equal(2, CountEntriesFor(world, "Gunboat"));
            Assert.Contains(log, l => l.Contains("DOCTRINE_BUILD ") && l.Contains("prefab=Gunboat")
                                      && l.Contains("placeable=2") && l.Contains("placed=2"));
        }

        [Fact]
        public void Doctrine_SatisfiedByExistingStock_QueuesNothing()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            yard.ShipStock["Ridill"] = 2;
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 2);

            TickShipyard(world, st);

            Assert.Equal(0, CountEntriesFor(world, "Ridill"));
        }

        [Fact]
        public void Doctrine_RebuildsWhenStockIsDrawnDown_BecauseTheTargetIsAThreshold()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            yard.ShipStock["Ridill"] = 2;
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 2);

            TickShipyard(world, st);
            Assert.Equal(0, CountEntriesFor(world, "Ridill"));

            // A dispatch or a store sale draws the shelf down; the line re-arms from the live count.
            // This is the documented difference between "keep 2" and "build 2 and stop".
            yard.ShipStock["Ridill"] = 1;
            TickShipyard(world, st);

            Assert.Equal(1, CountEntriesFor(world, "Ridill"));
        }

        [Fact]
        public void Doctrine_InProgressBuildCountsTowardTheTarget()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode a = AddShipyard(world, "N1");
            AddShipyard(world, "N2", new Vec3(100, 0, 0));
            // Queued by hand, not by the line -- it still satisfies the same threshold, so counting
            // only the line's own builds would over-build.
            // A real BOM, not an empty one: an entry with no BOM is a free build that this same
            // tick's servicing pass completes immediately, which would take it out of the queue
            // before the assertion could see it counted.
            var inProgress = new BuildQueueEntry { Id = "Mine:001", Role = "Mine", PrefabId = "Ridill" };
            inProgress.Bom["Comp:Steel"] = 40;
            a.BuildQueue.Add(inProgress);
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 2, maxConcurrent: 2);

            TickShipyard(world, st);

            Assert.Equal(2, CountEntriesFor(world, "Ridill")); // the pre-existing one plus exactly one more
        }

        [Fact]
        public void Doctrine_NeverDestroysAnotherBuildAtTheYardItPlacesOn()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            recipes.CostByPrefab["Other"] = 10;
            prefabs.DrillYieldScoreByPrefab["Other"] = 1.0;
            prefabs.CargoLitersByPrefab["Other"] = 1000;
            SpatialNode a = AddShipyard(world, "N1");
            SpatialNode b = AddShipyard(world, "N2", new Vec3(100, 0, 0));
            var occupied = new BuildQueueEntry { Id = "Mine:001", Role = "Mine", PrefabId = "Other" };
            occupied.Bom["Comp:Steel"] = 100;
            occupied.Collected["Comp:Steel"] = 7;
            // The rank a real entry of this role carries. Left at 0 the occupant would lose the
            // working slot to the line's build on rank alone, which would test the priority model
            // rather than the thing this test is about.
            occupied.Priority = ShipyardManager.RolePriority("Mine", world.Tuning);
            a.BuildQueue.Add(occupied);
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 5);

            TickShipyard(world, st);

            // The guarantee, which survives the role key going away: a doctrine line never destroys
            // a build someone else queued. It now places BESIDE the occupant rather than skipping the
            // yard, and the occupant keeps everything it had collected.
            Assert.Contains(a.BuildQueue, e => ReferenceEquals(e, occupied));
            Assert.Equal(7, occupied.Collected["Comp:Steel"]);
            Assert.Contains(a.BuildQueue, e => e.PrefabId == "Ridill");
            // The yard works one build at a time, and the occupant got there first, so the line's
            // hull waits rather than taking the slot out from under it.
            Assert.True(a.IsActiveBuild(occupied));
            Assert.False(a.IsActiveBuild(a.BuildQueue.Find(e => e.PrefabId == "Ridill")));
        }

        [Fact]
        public void Doctrine_IsNeitherBlockedNorOutrankedByAShelfBuildOfTheSameRole()
        {
            // A shelf build carries a real fleet role (StoreShelfPlan maps its classes to
            // Trade/Raid/Mine), so before the role key left admission it excluded the player's own
            // line from that yard. Two properties now hold together, and both are needed: the line
            // PLACES beside it, and it takes the working slot, because a shelf build ranks
            // StoreShipBuildPriority against every fleet role's own.
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            var shelf = new BuildQueueEntry
            {
                Id = yard.MintBuildEntryId("Mine"), Role = "Mine", PrefabId = "Other",
                DestinedForSale = true,
                Priority = ShipyardManager.RolePriority("Mine", world.Tuning, destinedForSale: true),
            };
            shelf.Bom["Comp:Steel"] = 100;
            yard.BuildQueue.Add(shelf);
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 2);

            TickShipyard(world, st);

            BuildQueueEntry line = yard.BuildQueue.Find(e => e.PrefabId == "Ridill");
            Assert.NotNull(line);
            Assert.True(yard.IsActiveBuild(line), "the doctrine's hull must take the working slot");
            Assert.False(yard.IsActiveBuild(shelf), "the shelf build ranks below every fleet role");
            Assert.Contains(yard.BuildQueue, e => ReferenceEquals(e, shelf));
        }

        [Fact]
        public void Doctrine_ScopedToNode_CountsAndBuildsOnlyThere()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode a = AddShipyard(world, "N1");
            SpatialNode b = AddShipyard(world, "N2", new Vec3(100, 0, 0));
            b.ShipStock["Ridill"] = 5; // out of scope: must not satisfy a line scoped to N1
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 1, scopeNodeId: "N1");

            TickShipyard(world, st);

            Assert.Equal("Ridill", Assert.Single(a.BuildQueue).PrefabId);
            Assert.Empty(b.BuildQueue);
        }

        [Fact]
        public void Doctrine_ListedForSale_IsNeverCountedAsOwned()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            yard.ShipStock["Ridill"] = 2;
            yard.ListedForSale["Ridill"] = 2; // the whole shelf is spoken for by the store -- never "ours"
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 1);

            TickShipyard(world, st);

            Assert.Equal(1, CountEntriesFor(world, "Ridill"));
        }

        [Fact]
        public void Doctrine_PausedLine_IsNotEvaluated()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddShipyard(world, "N1");
            var st = MakeState();
            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 5);
            line.Paused = true;

            TickShipyard(world, st);

            Assert.Equal(0, CountEntriesFor(world, "Ridill"));
        }

        [Fact]
        public void Doctrine_HigherRankedLineTakesTheOnlyShipyard()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            recipes.CostByPrefab["Wanted"] = 10;
            prefabs.DrillYieldScoreByPrefab["Wanted"] = 1.0;
            prefabs.CargoLitersByPrefab["Wanted"] = 1000;
            AddShipyard(world, "N1");
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 5, priority: 0);
            StandingOrderFixtures.AddDoctrineLine(st, "Wanted", targetCount: 5, priority: 9);

            TickShipyard(world, st);

            // One shipyard, one working slot, two lines of the same role: rank decides which hull the
            // yard actually builds. The loser queues behind it rather than being refused outright --
            // it absorbs nothing while it waits, and takes the slot when the winner completes.
            SpatialNode yard = world.Graph.Nodes["N1"];
            BuildQueueEntry winner = yard.BuildQueue.Find(e => e.PrefabId == "Wanted");
            BuildQueueEntry loser  = yard.BuildQueue.Find(e => e.PrefabId == "Ridill");
            Assert.NotNull(winner);
            Assert.NotNull(loser);
            Assert.True(yard.IsActiveBuild(winner));
            Assert.False(yard.IsActiveBuild(loser));
        }

        [Fact]
        public void Doctrine_SteadyState_StopsLoggingUntilSomethingChanges()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            yard.ShipStock["Ridill"] = 3;
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 3);

            List<string> first = TickShipyard(world, st);
            List<string> second = TickShipyard(world, st);

            // A satisfied line is re-evaluated every Shipyard tick and must not narrate that.
            Assert.Contains(first, l => l.Contains("DOCTRINE_BUILD "));
            Assert.DoesNotContain(second, l => l.Contains("DOCTRINE_BUILD "));
        }


        // ---- fleet doctrine: in flight, the ceiling, and the route gate ----------------------

        /// <summary>A Trade-suitable hull ("Hauler") added to the Mine-suitable "Ridill" the shared
        /// build-goal world already carries, so a test can put a Ship:Trade demand and a Mine-role
        /// doctrine line in the same world without one answering the other.</summary>
        private static void AddTradeHull(FakeRecipeRegistry recipes, FakePrefabCatalogue prefabs, string id = "Hauler")
        {
            recipes.CostByPrefab[id] = 40;
            prefabs.CargoLitersByPrefab[id] = 5000;   // cargo and no drill == Trade, not Mine
        }

        private static void AddCombatHull(FakeRecipeRegistry recipes, FakePrefabCatalogue prefabs, string id)
        {
            recipes.CostByPrefab[id] = 40;
            prefabs.DpsByPrefab[id] = 500;   // armed, no hold, no drill == Raid/Patrol only
        }

        private static FactionState MakePlayerState(string tag = "F")
        {
            return new FactionState { Tag = tag, IsPlayerFaction = true };
        }

        /// <summary>Puts <paramref name="count"/> hulls of <paramref name="prefabId"/> in the air,
        /// coming home to <paramref name="home"/> -- the state a dispatch leaves behind.</summary>
        private static void AddFlight(FactionState st, WorldModel world, string prefabId, int count,
            string home, string fleetId = "F00001")
        {
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade,
                FleetId = fleetId,
                OriginNodeId = home,
                ReturnNodeId = home,
                TargetNodeId = home,
                ShipPrefabId = prefabId,
                ConvoySize = count,
                DepartedUtc = world.UtcNow,
                EstimatedArrivalUtc = world.UtcNow.AddHours(6),
            });
        }

        [Fact]
        public void Doctrine_InFlightHulls_CountTowardHave_SoADispatchDoesNotReopenTheShortfall()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 2);

            // Two owned, both parked: satisfied.
            yard.ShipStock["Ridill"] = 2;
            Assert.Equal(0, CountEntriesFor(world, "Ridill"));

            // Now dispatch the whole fleet. ShipStock is drawn down for the WHOLE journey, so a
            // parked-only count reads zero here and orders two more -- the defect the in-flight term
            // exists to close.
            yard.ShipStock["Ridill"] = 0;
            AddFlight(st, world, "Ridill", 2, "N1");

            List<string> log = TickShipyard(world, st);

            Assert.Equal(0, CountEntriesFor(world, "Ridill"));
            Assert.Contains(log, l => l.Contains("DOCTRINE_BUILD ") && l.Contains(" have=2")
                                      && l.Contains(" flying=2") && l.Contains("placed=0"));
        }

        [Fact]
        public void Doctrine_InFlightHulls_AreScopedByWhereTheHullComesHomeTo()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddShipyard(world, "N1");
            AddShipyard(world, "N2", new Vec3(100, 0, 0));
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 1, scopeNodeId: "N1");

            // The hull is flying home to N2, which the line's scope does not cover. Scoping counting
            // and placement the same way is the whole contract: a line confined to N1 must not be
            // satisfied by stock that will land somewhere else.
            AddFlight(st, world, "Ridill", 1, "N2");

            TickShipyard(world, st);

            Assert.Equal("Ridill", Assert.Single(world.Graph.Nodes["N1"].BuildQueue).PrefabId);
            Assert.Empty(world.Graph.Nodes["N2"].BuildQueue);
        }

        private static DamagedHull QueueRepair(SpatialNode node, string prefabId, bool partsIn, DateTime now)
        {
            var hull = new DamagedHull { PrefabId = prefabId, Integrity = 0.5, QueuedUtc = now };
            hull.Bill["Comp:SteelPlate"] = 20;
            if (partsIn)
            {
                hull.Collected["Comp:SteelPlate"] = 20;
                hull.PartsCompleteUtc = now;
            }
            node.DamagedHulls.Add(hull);
            return hull;
        }

        // ---- hull refit (FAC-437) --------------------------------------------------------------

        private static WorldModel MakeRefitWorld(out SpatialNode yard, out FactionState st)
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            prefabs.Versions["Ridill@2"] = new KeyValuePair<string, int>("Ridill", 2);
            // v1 carries a gun v2 drops (Gun 10), v2 adds thrust (Thruster 30); the hull is shared.
            recipes.BomByPrefab["Ridill"] = new Dictionary<string, long> { { "SteelPlate", 100 }, { "Gun", 10 } };
            recipes.BomByPrefab["Ridill@2"] = new Dictionary<string, long> { { "SteelPlate", 100 }, { "Thruster", 30 } };
            yard = AddShipyard(world, "N1");
            st = MakeState();
            return world;
        }

        [Fact]
        public void Refit_TakesUpToTheCap_BilledTheNetDifference()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = MakeRefitWorld(out yard, out st);
            yard.ShipStock["Ridill"] = 3;
            yard.ListedForSale["Ridill"] = 1;   // reserved for a buyer: never taken
            st.HullRefitCaps["Ridill"] = 5;

            var log = new List<string>();
            int taken = HullRefit.Intake(st, world, log);

            Assert.Equal(2, taken);
            Assert.Equal(1, yard.ShipStock["Ridill"]);
            Assert.Equal(2, yard.DamagedHulls.Count);
            DamagedHull hull = yard.DamagedHulls[0];
            Assert.Equal("Ridill@2", hull.RefitTo);
            Assert.Equal(new Dictionary<string, long> { { "Comp:Thruster", 30 } }, hull.Bill);
            Assert.Contains(log, l => l.Contains("REFIT_QUEUED prefab=Ridill to=Ridill@2 node=N1 bill_units=30"));

            // At the cap: a second pass takes nothing more.
            st.HullRefitCaps["Ridill"] = 2;
            yard.ListedForSale.Clear();
            Assert.Equal(0, HullRefit.Intake(st, world, log));
        }

        [Fact]
        public void Refit_Finished_RejoinsStockAsTheLatest_AndCreditsWhatItReplaced()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = MakeRefitWorld(out yard, out st);
            yard.ShipStock["Ridill"] = 1;
            st.HullRefitCaps["Ridill"] = 1;
            HullRefit.Intake(st, world, null);
            yard.Inventory["Comp:Thruster"] = 30;

            var log = new List<string>();
            HullDamage.ServiceRepairs(world, yard, st.Tag, log);           // parts in
            DamagedHull hull = Assert.Single(yard.DamagedHulls);
            Assert.True(hull.PartsComplete);
            world.UtcNow = world.UtcNow + HullDamage.WorkTime(hull);
            HullDamage.ServiceRepairs(world, yard, st.Tag, log);           // worked

            Assert.Empty(yard.DamagedHulls);
            Assert.Equal(1, yard.ShipStock["Ridill@2"]);
            Assert.False(yard.ShipStock.ContainsKey("Ridill"));
            long gun;
            Assert.True(yard.Inventory.TryGetValue("Comp:Gun", out gun));
            Assert.Equal(10L, gun);
            Assert.Contains(log, l => l.Contains("REFIT_DONE prefab=Ridill to=Ridill@2 node=N1"));
        }

        [Fact]
        public void RefitRows_ListEveryDesignWithOldHulls_AndWhatARefitCanTakeNow()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = MakeRefitWorld(out yard, out st);
            yard.ShipStock["Ridill"] = 3;
            yard.ListedForSale["Ridill"] = 1;          // on the shelf: not the fleet's to refit
            SpatialNode depot = AddStation(world, "N2", "F", new Vec3(100, 0, 0));
            depot.ShipStock["Ridill"] = 2;             // parked away from any yard
            yard.ShipStock["Ridill@2"] = 4;            // already latest: counted nowhere

            List<HullRefit.Row> rows = HullRefit.Rows(world, st);

            HullRefit.Row row = Assert.Single(rows);
            Assert.Equal("Ridill", row.Family);
            Assert.Equal("Ridill@2", row.Latest);
            Assert.Equal(0, row.Cap);
            Assert.Equal(4, row.OutdatedParked);
            Assert.Equal(2, row.OutdatedAtYards);

            st.HullRefitCaps["Ridill"] = 1;
            HullRefit.Intake(st, world, null);
            row = Assert.Single(HullRefit.Rows(world, st));
            Assert.Equal(1, row.Cap);
            Assert.Equal(1, row.InRefit);
            Assert.Equal(1, row.OutdatedAtYards);
        }

        [Fact]
        public void Refit_IsServedAfterEveryRepair()
        {
            var refit = new DamagedHull { PrefabId = "A", Integrity = 1.0, RefitTo = "A@2" };
            var repair = new DamagedHull { PrefabId = "B", Integrity = 0.3 };
            repair.Bill["Comp:SteelPlate"] = 500;   // a bigger bill than the refit's, still first
            refit.Bill["Comp:SteelPlate"] = 1;

            List<DamagedHull> order = HullDamage.ServedOrder(new List<DamagedHull> { refit, repair });

            Assert.Same(repair, order[0]);
            Assert.Same(refit, order[1]);
        }

        [Fact]
        public void Refit_SurvivesARestart()
        {
            var node = new SpatialNode { Id = "N1" };
            var hull = new DamagedHull { PrefabId = "A", Integrity = 1.0, RefitTo = "A@2", RefitShare = 0.25 };
            hull.Bill["Comp:Thruster"] = 3;
            var docs = FactionStore.DamagedHullDocs(new List<DamagedHull> { hull });
            FactionStore.LoadDamagedHulls(node, docs);

            DamagedHull back = Assert.Single(node.DamagedHulls);
            Assert.Equal("A@2", back.RefitTo);
            Assert.Equal(0.25, back.RefitShare);
            Assert.Equal(TimeSpan.FromHours(HullDamage.RepairHoursPerHull * 0.25), HullDamage.WorkTime(back));
        }

        /// <summary>A line names a design, not a revision: a newer version parked, in repair and
        /// flying each count toward a line naming the original, so a line at target builds nothing
        /// after a new version is published. Every term is the newer id, so an exact-id count in
        /// any one of the three reads short and builds.</summary>
        [Fact]
        public void Doctrine_EveryVersionOfTheLinesDesign_CountsTowardHave()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            prefabs.Versions["Ridill@2"] = new KeyValuePair<string, int>("Ridill", 2);
            recipes.CostByPrefab["Ridill@2"] = 50;
            prefabs.DrillYieldScoreByPrefab["Ridill@2"] = 1.0;
            prefabs.CargoLitersByPrefab["Ridill@2"] = 1000;
            SpatialNode yard = AddShipyard(world, "N1");
            yard.ShipStock["Ridill@2"] = 1;
            QueueRepair(yard, "Ridill@2", partsIn: false, now: world.UtcNow);
            var st = MakeState();
            AddFlight(st, world, "Ridill@2", 1, "N1");
            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 3);

            TickShipyard(world, st);

            Assert.Equal(0, CountEntriesFor(world, "Ridill") + CountEntriesFor(world, "Ridill@2"));
            Assert.Equal(3L, ShipyardManager.CountDoctrineHave(world, st, line));
        }

        /// <summary>The discriminator for the test above, and the other half of the rule: one short,
        /// the line builds exactly one, and it builds the latest version, not the id it names.</summary>
        [Fact]
        public void Doctrine_ShortOfTarget_BuildsTheLatestVersion()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            prefabs.Versions["Ridill@2"] = new KeyValuePair<string, int>("Ridill", 2);
            recipes.CostByPrefab["Ridill@2"] = 50;
            prefabs.DrillYieldScoreByPrefab["Ridill@2"] = 1.0;
            prefabs.CargoLitersByPrefab["Ridill@2"] = 1000;
            SpatialNode yard = AddShipyard(world, "N1");
            yard.ShipStock["Ridill"] = 1;
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 2);

            TickShipyard(world, st);

            Assert.Equal("Ridill@2", Assert.Single(yard.BuildQueue).PrefabId);
        }

        /// <summary>EXP's live shape: a line's hulls came home damaged and sit in the repair queue.
        /// They are coming back to stock, so a line at target counting them builds nothing; a line
        /// that did not count them would queue their replacements.</summary>
        [Fact]
        public void Doctrine_HullsInRepair_CountTowardHave_SoALineAtTargetBuildsNothing()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            yard.ShipStock["Ridill"] = 1;
            QueueRepair(yard, "Ridill", partsIn: false, now: world.UtcNow);
            QueueRepair(yard, "Ridill", partsIn: true, now: world.UtcNow);
            QueueRepair(yard, "Other", partsIn: false, now: world.UtcNow);   // not this line's hull
            var st = MakeState();
            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 3);

            List<string> log = TickShipyard(world, st);

            Assert.Equal(0, CountEntriesFor(world, "Ridill"));
            Assert.Contains(log, l => l.Contains("DOCTRINE_BUILD ") && l.Contains(" have=3")
                                      && l.Contains(" repair=2") && l.Contains("placed=0"));
            Assert.Equal(3L, ShipyardManager.CountDoctrineHave(world, st, line));
            Assert.Equal(2L, ShipyardManager.CountDoctrineHullsInRepair(world, "F", line));
        }

        /// <summary>The discriminator for the test above: one short of target with the same kind of
        /// queue, the line builds exactly one -- so the zero there is the repair term, not a line
        /// that cannot place.</summary>
        [Fact]
        public void Doctrine_HullsInRepair_LeaveTheRealShortfallToBuild()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            yard.ShipStock["Ridill"] = 1;
            QueueRepair(yard, "Ridill", partsIn: false, now: world.UtcNow);
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 3);

            List<string> log = TickShipyard(world, st);

            Assert.Equal(1, CountEntriesFor(world, "Ridill"));
            Assert.Contains(log, l => l.Contains("DOCTRINE_BUILD ") && l.Contains(" have=2")
                                      && l.Contains(" repair=1") && l.Contains("placed=1"));
        }

        [Fact]
        public void Doctrine_HullsInRepair_AreScopedByTheNodeTheyAreQueuedAt()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddShipyard(world, "N1");
            SpatialNode other = AddShipyard(world, "N2", new Vec3(100, 0, 0));
            QueueRepair(other, "Ridill", partsIn: true, now: world.UtcNow);
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 1, scopeNodeId: "N1");

            TickShipyard(world, st);

            Assert.Equal("Ridill", Assert.Single(world.Graph.Nodes["N1"].BuildQueue).PrefabId);
        }

        [Fact]
        public void Doctrine_AtTargetWithWorkWaiting_QueuesNothingAndReportsThePressure()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddTradeHull(recipes, prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            var st = MakePlayerState();
            StandingOrderFixtures.AddDoctrineLine(st, "Hauler", targetCount: 2);

            // Both hulls in the air: the fleet is at its target and there is nothing parked here, so
            // the demand below is genuinely unsatisfiable from local stock and reaches the gate.
            AddFlight(st, world, "Hauler", 2, "N1");
            world.Demand.Post(DemandEntry.ForShip(st.Tag, "Trade", yard.Id,
                world.UtcNow.AddHours(6), minCargoLiters: 1, postedUtc: world.UtcNow));

            List<string> log = TickShipyard(world, st);

            // The target is a CEILING as well as a floor: work waiting on hulls does not grow the
            // fleet. Raising the target is the player's call.
            Assert.Empty(yard.BuildQueue);
            Assert.Contains(log, l => l.Contains("DOCTRINE_PRESSURE") && l.Contains("role=Trade")
                                      && l.Contains("node=N1") && l.Contains("demands=1")
                                      && l.Contains("line=FD-001") && l.Contains("have=2/2"));
            // Not consumed: the demand is the standing evidence that work is waiting, and only its
            // own TTL retires it.
            Assert.NotEmpty(world.Demand.SnapshotShipDemands(st.Tag));
        }

        [Fact]
        public void Doctrine_PressureForARoleWithNoLine_ReportsNone()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddTradeHull(recipes, prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            var st = MakePlayerState();
            // A doctrine whose only hull is a gunboat, which fits no Trade work. "A role with no line
            // is not built" is the design; this line is what keeps the omission visible rather than
            // silent. (A miner would not do: its hold makes it fit Trade as well.)
            AddCombatHull(recipes, prefabs, "Gunboat");
            StandingOrderFixtures.AddDoctrineLine(st, "Gunboat", targetCount: 1);
            world.Demand.Post(DemandEntry.ForShip(st.Tag, "Trade", yard.Id,
                world.UtcNow.AddHours(6), minCargoLiters: 1, postedUtc: world.UtcNow));

            List<string> log = TickShipyard(world, st);

            Assert.Contains(log, l => l.Contains("DOCTRINE_PRESSURE") && l.Contains("role=Trade")
                                      && l.Contains("line=none"));
            Assert.DoesNotContain(yard.BuildQueue, e => e.Role == "Trade");
        }

        [Fact]
        public void Doctrine_PressureIsLoggedOnChange_NotEveryTick()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddTradeHull(recipes, prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            var st = MakePlayerState();
            AddCombatHull(recipes, prefabs, "Gunboat");
            StandingOrderFixtures.AddDoctrineLine(st, "Gunboat", targetCount: 1);
            world.Demand.Post(DemandEntry.ForShip(st.Tag, "Trade", yard.Id,
                world.UtcNow.AddHours(6), minCargoLiters: 1, postedUtc: world.UtcNow));

            List<string> first = TickShipyard(world, st);
            List<string> second = TickShipyard(world, st);

            // Standing pressure is re-evaluated every Shipyard tick and must not narrate that.
            Assert.Contains(first, l => l.Contains("DOCTRINE_PRESSURE"));
            Assert.DoesNotContain(second, l => l.Contains("DOCTRINE_PRESSURE"));
        }

        [Fact]
        public void Doctrine_YardTheHullCannotFlyFrom_IsNotPlaceable_AndTheLineSaysWhy()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            // One planet-bound yard at 1g. An ion hull has ~0.10 TWR there and cannot leave it.
            world.Planets = new ConstantGravityCatalogue(g: 1.0, inAtmosphereEverywhere: true);
            prefabs.SetIonDown("Ridill", dryMassKg: 1000, forceN: 5000);
            SpatialNode yard = AddShipyard(world, "N1");
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 2);

            List<string> log = TickShipyard(world, st);

            // The player chose the hull, so the cargo and Dps floors are advisory -- but a hull that
            // cannot leave the yard it is built at is a stranded grid and a wasted BOM.
            Assert.Empty(yard.BuildQueue);
            Assert.Contains(log, l => l.Contains("DOCTRINE_LINE_BLOCKED") && l.Contains("line=FD-001")
                                      && l.Contains("reason=route"));
            Assert.Contains(log, l => l.Contains("DOCTRINE_BUILD ") && l.Contains("placeable=0"));

            // Log-on-change: a line blocked for a day says so once.
            Assert.DoesNotContain(TickShipyard(world, st), l => l.Contains("DOCTRINE_LINE_BLOCKED"));
        }

        [Fact]
        public void Doctrine_YardTheHullCanFlyFrom_IsNeverReportedAsRouteBlocked()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            var occupied = new BuildQueueEntry { Id = "Mine:001", Role = "Mine", PrefabId = "Other" };
            occupied.Bom["Comp:Steel"] = 100;
            yard.BuildQueue.Add(occupied);
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 2);

            List<string> log = TickShipyard(world, st);

            // The paired negative for the route gate above: this yard already carries another build
            // and the hull flies from it fine, so the line places and nothing is blocked. A
            // "reason=route" here would send a player hunting a route problem that does not exist.
            Assert.DoesNotContain(log, l => l.Contains("DOCTRINE_LINE_BLOCKED"));
            Assert.Contains(log, l => l.Contains("DOCTRINE_BUILD ") && l.Contains("placeable=1"));
            Assert.Contains(yard.BuildQueue, e => e.PrefabId == "Ridill");
        }

        // ---- fleet doctrine: the two autonomy gates ------------------------------------------
        //
        // Each carries its NPC control in the same test and the same Tick call. A gate that turned
        // the behaviour off for everyone would pass the "EXP is silent" half on its own.

        [Fact]
        public void Doctrine_ProactiveShipDemands_AreSilentForADoctrineFaction_AndStillPostForAnNpc()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddTradeHull(recipes, prefabs);
            AddShipyard(world, "N1");                                  // F, the doctrine faction
            SpatialNode npcYard = AddStation(world, "N9", "NPC", new Vec3(9000, 0, 0));
            npcYard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var player = MakePlayerState();
            StandingOrderFixtures.AddDoctrineLine(player, "Hauler", targetCount: 1);
            var npc = new FactionState { Tag = "NPC" };

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { player, npc }, world, world.Tuning, log);

            // Neither yard holds a cargo hull, so the per-yard Trade floor fires for both -- unless
            // the faction says what its fleet is made of.
            Assert.DoesNotContain(log, l => l.StartsWith("F ") && l.Contains("SHIP_DEMAND_PROACTIVE"));
            Assert.Contains(log, l => l.StartsWith("NPC ") && l.Contains("SHIP_DEMAND_PROACTIVE"));
            // Nothing at all is left in the register for F: the gate returns before the first post,
            // so there is no Trade floor, no posture combat target and no Dps budget either.
            Assert.Empty(world.Demand.SnapshotShipDemands("F"));
        }

        [Fact]
        public void Doctrine_ShipDemand_CreatesNoBuildForADoctrineFaction_AndStillDoesForAnNpc()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddTradeHull(recipes, prefabs);
            SpatialNode playerYard = AddShipyard(world, "N1");
            SpatialNode npcYard = AddStation(world, "N9", "NPC", new Vec3(9000, 0, 0));
            npcYard.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            // The doctrine names a Mine line only, so any Trade-role entry that appears came from the
            // demand -- which is exactly what must not happen.
            var player = MakePlayerState();
            StandingOrderFixtures.AddDoctrineLine(player, "Ridill", targetCount: 1);
            var npc = new FactionState { Tag = "NPC" };

            foreach (FactionState st in new[] { player, npc })
                world.Demand.Post(DemandEntry.ForShip(st.Tag, "Trade",
                    st == player ? playerYard.Id : npcYard.Id,
                    world.UtcNow.AddHours(6), minCargoLiters: 1, postedUtc: world.UtcNow));

            var log = new List<string>();
            var manager = new ShipyardManager { PrefabListPicker = PickerFor("Trade", "Hauler") };
            manager.Tick(new List<FactionState> { player, npc }, world, world.Tuning, log);

            Assert.DoesNotContain(playerYard.BuildQueue, e => e.Role == "Trade");
            Assert.DoesNotContain(log, l => l.StartsWith("F ") && l.Contains("BUILD_QUEUED role=Trade"));
            // The control: the same demand, the same tick, an NPC -- which must still build.
            Assert.Contains(npcYard.BuildQueue, e => e.Role == "Trade");
            Assert.Contains(log, l => l.StartsWith("NPC ") && l.Contains("BUILD_QUEUED role=Trade"));
        }

        [Fact]
        public void Doctrine_ShelfBuildsAreOutsideIt_AStoreDemandStillBuilds()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddTradeHull(recipes, prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            var st = MakePlayerState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 1);

            // A shelf build is a different purpose with a different buyer (StoreStockManager), and
            // the doctrine says nothing about what the shop stocks. A gate that caught this too
            // would empty a player faction's store window as a side effect.
            world.Demand.Post(DemandEntry.ForShip(st.Tag, "Trade", yard.Id,
                world.UtcNow.AddHours(6), minCargoLiters: 1, postedUtc: world.UtcNow,
                destinedForSale: true));

            TickShipyard(world, st, PickerFor("Trade", "Hauler"));

            Assert.Contains(yard.BuildQueue, e => e.Role == "Trade" && e.DestinedForSale);
        }

        // ---- one working slot per yard, next build FIFO -------------------------------------

        /// <summary>A yard with two queued builds wanting DIFFERENT components -- the real shape of
        /// the defect, where each role's BOM drew on the yard's stock at the same time -- and a
        /// stocked Trade/Raid/Patrol network so PostProactiveShipDemands adds nothing of its own.
        /// The head's BOM is deliberately larger than what is on hand so it cannot finish and free
        /// the slot in the same tick.</summary>
        private static SpatialNode TwoQueuedBuildsYard(WorldModel world,
            out BuildQueueEntry head, out BuildQueueEntry behind, long headBom = 200)
        {
            SpatialNode node = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();
            node.ShipStock["Trade"]  = 1;
            node.ShipStock["Raid"]   = 1;
            node.ShipStock["Patrol"] = 1;

            head = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "HeadPrefab",
                CreatedUtc = world.UtcNow.AddHours(-2), ExpiresUtc = world.UtcNow.AddHours(23),
            };
            head.Bom["Comp:SteelPlate"] = headBom;

            behind = new BuildQueueEntry
            {
                Id = "Raid:001", Role = "Raid", PrefabId = "BehindPrefab",
                CreatedUtc = world.UtcNow.AddHours(-1), ExpiresUtc = world.UtcNow.AddHours(23),
            };
            // Deliberately larger than what is on hand: a build that COMPLETES frees the slot and
            // the next one is picked up in the same sweep, which is correct but would hide whether
            // the queue was respected at all.
            behind.Bom["Comp:InteriorPlate"] = 200;

            node.BuildQueue.Add(head);
            node.BuildQueue.Add(behind);
            node.Inventory["Comp:SteelPlate"]    = 100;
            node.Inventory["Comp:InteriorPlate"] = 100;
            return node;
        }

        [Fact]
        public void OneWorkingSlot_OnlyTheQueueHeadDrawsOnTheYardsStock()
        {
            // The defect: every role with a live demand opened its own build and they collected,
            // reserved and demanded components in parallel, so a yard's intake was divided N ways
            // and all N sat unfinished. The head takes the plate it needs; the interior plate the
            // build behind it wants must stay routable.
            var world = MakeWorld();
            BuildQueueEntry head, behind;
            SpatialNode node = TwoQueuedBuildsYard(world, out head, out behind);

            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, new List<string>());

            Assert.Equal(100, head.Collected["Comp:SteelPlate"]);
            Assert.Empty(behind.Collected);
            Assert.Equal(100, node.Inventory["Comp:InteriorPlate"]);
        }

        [Fact]
        public void OneWorkingSlot_HeadCompletes_ThenTheNextInLineIsWorked()
        {
            // The queue is a queue, not a freeze: the entry behind must actually be picked up once
            // the slot frees, or this change trades parallel half-builds for a permanent backlog.
            var world = MakeWorld();
            BuildQueueEntry head, behind;
            SpatialNode node = TwoQueuedBuildsYard(world, out head, out behind, headBom: 100);
            var mgr = new ShipyardManager();
            var state = MakeState();

            mgr.Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.DoesNotContain(head, node.BuildQueue);         // built and removed
            Assert.Equal(1, node.ShipStock["HeadPrefab"]);
            Assert.Equal(100, behind.Collected["Comp:InteriorPlate"]); // promoted and working
        }

        [Fact]
        public void NextBuildIsFifo_OldestWaitingEntryTakesTheFreedSlot_NotAllOfThem()
        {
            // Equal priority (the default for every automatic build) means order of arrival decides,
            // and exactly one entry is promoted -- freeing the slot does not release the whole queue.
            var world = MakeWorld();
            BuildQueueEntry head, second;
            SpatialNode node = TwoQueuedBuildsYard(world, out head, out second, headBom: 100);
            var third = new BuildQueueEntry
            {
                Id = "Patrol:001", Role = "Patrol", PrefabId = "ThirdPrefab",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(23),
            };
            third.Bom["Comp:Girder"] = 200;
            node.BuildQueue.Add(third);
            node.Inventory["Comp:Girder"] = 100;

            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, new List<string>());

            // Head finished and freed the slot; "second" queued before "third", so it goes next and
            // "third" waits with its own component untouched.
            Assert.Equal(1, node.ShipStock["HeadPrefab"]);
            Assert.Equal(100, second.Collected["Comp:InteriorPlate"]);
            Assert.Empty(third.Collected);
            Assert.Equal(100, node.Inventory["Comp:Girder"]);
        }

        [Fact]
        public void HigherPriorityEntry_TakesTheWorkingSlot_AheadOfAnOlderOne()
        {
            // The player-facing rank (`!factions station queue priority`) still outranks arrival
            // order -- FIFO is the tie-break, not the whole rule.
            //
            // Set through the verb rather than the field: BuildSlotAllocation reads a hand-set rank
            // as an instruction about the working slot, and the only thing that distinguishes one
            // from a role-derived rank is BuildQueueEntry.PriorityByPlayer, which the verb sets.
            var world = MakeWorld();
            BuildQueueEntry older, newer;
            SpatialNode node = TwoQueuedBuildsYard(world, out older, out newer);
            var mgr = new ShipyardManager();
            Assert.Equal(PlayerQueueResult.Ok,
                mgr.SetBuildPriority(world, "F", node.Id, newer.Id, 5, new List<string>()));

            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, new List<string>());

            Assert.Equal(100, newer.Collected["Comp:InteriorPlate"]);
            Assert.Empty(older.Collected);
            Assert.Equal(100, node.Inventory["Comp:SteelPlate"]);
        }

        [Fact]
        public void WaitingEntry_HoldsNoInventoryOffLimits_AndPullsNoSupplyTowardItsBom()
        {
            // Two things a waiting build must not do to the rest of the network: reserve stock it is
            // not collecting (BuildQueueReserved is what OperationsManager, the central bank and
            // trade procurement subtract), and pull hauled supply toward a BOM nobody is working on.
            var world = MakeWorld();
            BuildQueueEntry head, behind;
            SpatialNode node = TwoQueuedBuildsYard(world, out head, out behind);

            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, new List<string>());

            Assert.Equal(100, node.BuildQueueReserved("Comp:SteelPlate", world.UtcNow));   // head's shortfall
            Assert.Equal(0, node.BuildQueueReserved("Comp:InteriorPlate", world.UtcNow));  // not the one behind's
            List<DemandEntry> items = world.Demand.SnapshotItemDemands("F");
            Assert.Contains(items, d => d.Kind == "Item:Comp:SteelPlate");
            Assert.DoesNotContain(items, d => d.Kind == "Item:Comp:InteriorPlate");
        }

        [Fact]
        public void WaitingEntry_AccruesNoStallTime_AndIsNeverAbandonedForWaiting()
        {
            // StalledSeconds is the only abandonment rule, and it must not run while an entry is
            // queued behind another -- otherwise the second build in every queue ages out for time
            // the yard never gave it. Same treatment a paused yard already gets.
            var world = MakeWorld();
            BuildQueueEntry head, behind;
            SpatialNode node = TwoQueuedBuildsYard(world, out head, out behind);
            node.Inventory["Comp:SteelPlate"] = 0; // the head is starved, so it never frees the slot
            DateTime queuedExpiry = behind.ExpiresUtc;

            var mgr = new ShipyardManager();
            var state = MakeState();
            for (int i = 0; i < 40; i++)
            {
                world.UtcNow = world.UtcNow.AddHours(1);
                mgr.Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());
            }

            Assert.Contains(behind, node.BuildQueue);
            Assert.Equal(0.0, behind.StalledSeconds);
            Assert.True(behind.ExpiresUtc > queuedExpiry, "a waiting entry's deadline must roll forward");
            // The head, which WAS being serviced against an empty inventory, is the one that stalls.
            Assert.True(head.StalledSeconds > 0);
        }

        [Fact]
        public void BuildQueued_LogNamesWhetherTheYardStartedIt_OrQueuedItBehindSomething()
        {
            // A yard that quietly queues work instead of starting it must say so; without this the
            // waiting state is indistinguishable from a build that began and collected nothing.
            var world = MakeWorld();
            SpatialNode node = AddStation(world, "N1", "F");
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();

            // Priority set the way the real creation path would (ShipyardManager.RolePriority), not
            // left at the struct default: the yard works the top BuildSlots of OrderedBuildQueue, so
            // an entry hand-built at rank 0 would be displaced by any role-ranked build queued after
            // it and this fixture would stop describing an occupied slot at all.
            var occupied = new BuildQueueEntry
            {
                Id = "Mine:001", Role = "Mine", PrefabId = "Digger",
                CreatedUtc = world.UtcNow.AddHours(-2), ExpiresUtc = world.UtcNow.AddHours(23),
                Priority = ShipyardManager.RolePriority("Mine", world.Tuning),
            };
            occupied.Bom["Comp:SteelPlate"] = 100;
            node.BuildQueue.Add(occupied);

            var recipes = new FakeRecipeRegistry();
            recipes.CostByPrefab["Hull"] = 10;
            world.Recipes = recipes;

            var mgr = new ShipyardManager { PrefabListPicker = (role, tag) => new[] { "Hull" } };
            var log = new List<string>();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("BUILD_QUEUED ") && l.Contains("slot=waiting behind"));
            Assert.DoesNotContain(log, l => l.Contains("BUILD_QUEUED ") && l.Contains("slot=working"));
        }

    

        // ---- coverage sweep: a region with founded stations and no yard asks for one ------------

        /// <summary>A founded station in open space claimed by an authored feature. The Manufactory
        /// on the home node is the Shipyard's prerequisite, installed network-wide as the coverage
        /// predicate requires.</summary>
        private static SpatialNode AddNestStation(WorldModel world, string id, string feature)
        {
            SpatialNode n = AddStation(world, id, "F");
            n.FeatureKey = feature;
            return n;
        }

        private static List<InvestmentRequest> ScopedShipyardRequests(WorldModel world)
        {
            return world.Investments.SnapshotFor("F")
                .FindAll(r => r.Cap == NodeCapabilityKind.Shipyard && !NodeRegion.Of(r).IsNone);
        }

        [Fact]
        public void CoverageSweep_AsksForTheNestWithoutAYard_NotTheOneWithOne()
        {
            // FCTM's shape live (2026-09-05): six nests, two yards both in the home nest, and the
            // far nests generating no failed dispatch that could ever have asked.
            var world = MakeWorld();
            SpatialNode home = AddNestStation(world, "HOME", "Cinder Nest");
            home.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            home.Capabilities[NodeCapabilityKind.Shipyard]    = 1;
            home.ShipStock["Trade"] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();
            AddNestStation(world, "FAR1", "Far Nest Umbra");
            AddNestStation(world, "FAR2", "Far Nest Umbra");

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            List<InvestmentRequest> scoped = ScopedShipyardRequests(world);
            InvestmentRequest req = Assert.Single(scoped);
            Assert.Equal(NodeRegion.Feature("Far Nest Umbra"), NodeRegion.Of(req));
            Assert.Contains(log, l => l.Contains("F INVEST_REQUEST Shipyard feature=Far Nest Umbra yards=0/0 nodes=2"));
            Assert.DoesNotContain(log, l => l.Contains("INVEST_REQUEST Shipyard feature=Cinder Nest"));
        }

        [Fact]
        public void CoverageSweep_DoesNotAskForASecondYard_EvenWhereTheFirstIsWorking()
        {
            // Coverage only: saturation stays with the dispatch-failure path, which has a leg as
            // evidence. Presence is evidence of "no yard here" and nothing more.
            var world = MakeWorld();
            SpatialNode home = AddNestStation(world, "HOME", "Cinder Nest");
            home.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            home.Capabilities[NodeCapabilityKind.Shipyard]    = 1;
            home.ShipStock["Trade"] = 1;
            var working = new BuildQueueEntry { Id = "Trade:001", Role = "Trade", PrefabId = "P" };
            working.Bom["Comp:SteelPlate"] = 100;
            home.BuildQueue.Add(working);
            AddNestStation(world, "SPARE", "Cinder Nest");
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Empty(ScopedShipyardRequests(world));
        }

        [Fact]
        public void CoverageSweep_NeverAsksForOpenSpace_OrForARegionWithNoFoundedStation()
        {
            var world = MakeWorld();
            SpatialNode home = AddNestStation(world, "HOME", "Cinder Nest");
            home.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            home.Capabilities[NodeCapabilityKind.Shipyard]    = 1;
            home.ShipStock["Trade"] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();
            AddStation(world, "VOID", "F");                              // open space: no region
            SpatialNode claim = AddNestStation(world, "CLAIM", "Far Nest Umbra");
            claim.StableStationId = 0;                                   // in a region, not founded

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Empty(ScopedShipyardRequests(world));

            // ...and founding the far node is what makes the ask appear, so the refusal above was
            // the founded-station gate and not the fixture.
            claim.StableStationId = 43;
            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);
            Assert.Equal(NodeRegion.Feature("Far Nest Umbra"), NodeRegion.Of(Assert.Single(ScopedShipyardRequests(world))));
        }

        [Fact]
        public void CoverageSweep_AsksForABodyRegionToo_AndOnlyOnce()
        {
            // The same sweep covers planets: a founded station in a well with no yard there, while
            // the faction's one yard stands on another body. Re-posted next tick, one entry.
            var world = MakeWorld();
            SpatialNode home = AddStation(world, "HOME", "F");
            home.PlanetId = "Terra"; home.Site = StationSite.Surface;
            home.Capabilities[NodeCapabilityKind.Manufactory] = 1;
            home.Capabilities[NodeCapabilityKind.Shipyard]    = 1;
            home.ShipStock["Trade"] = 1;
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();
            SpatialNode ring = AddStation(world, "RING", "F");
            ring.PlanetId = "Cauldron"; ring.Site = StationSite.Orbital;

            var log = new List<string>();
            var mgr = new ShipyardManager();
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);
            mgr.Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            InvestmentRequest req = Assert.Single(ScopedShipyardRequests(world));
            Assert.Equal(NodeRegion.Body("Cauldron", StationSite.Orbital), NodeRegion.Of(req));
            Assert.Single(log.FindAll(l => l.Contains("INVEST_REQUEST Shipyard planet=Cauldron site=Orbital")));
        }
    

        // ---- Trade readiness is per REGION, not only per environment ------------------------

        [Fact]
        public void ProactiveDemand_TwoNestYards_OnlyTheNestWithoutCargoStockGetsTradeDemand()
        {
            // FCTM on v0.173.0: a yard developed in a far nest, every cargo hull stocked in the home
            // nest, and in zero gravity the route filter admits every hull -- so the far yard read
            // "stocked" and never posted a build order. Two nests, a yard in each, stock only at
            // home: the far yard must ask, the home one must not.
            var world = MakeWorld();
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();
            SpatialNode home = AddNestStation(world, "HOME", "Cinder Nest");
            home.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            home.ShipStock["Trade"] = 3;
            SpatialNode far = AddNestStation(world, "FAR", "Far Nest Umbra");
            far.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Trade") && l.Contains("node=FAR"));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Trade") && l.Contains("node=HOME"));
        }

        [Fact]
        public void ProactiveDemand_AYardInNoRegion_StillReadsTheWholeNetwork()
        {
            // Open space is no region; the pre-existing network-wide reading stands there, which is
            // also what keeps the two-zone tests above meaningful.
            var world = MakeWorld();
            world.PrefabCatalogue = new AllSuitablePrefabCatalogue();
            SpatialNode a = AddStation(world, "A", "F", pos: new Vec3(0, 0, 0));
            a.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            SpatialNode b = AddStation(world, "B", "F", pos: new Vec3(5000, 0, 0));
            b.ShipStock["Trade"] = 1;

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { MakeState() }, world, world.Tuning, log);

            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Trade"));
        }

        // ---- builds named by hull (no role) -------------------------------------------------------

        [Fact]
        public void QueueBuild_WithNoRole_NamesTheHull_AndStartsAtTheHighestRoleRank()
        {
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            AddTradeHull(recipes, prefabs, "Hauler_A");

            var log = new List<string>();
            Dictionary<string, long> bom;
            PlayerQueueResult result = new ShipyardManager().QueueBuild(world, world.Tuning, "F", "N1", null, "Hauler_A", out bom, log);

            Assert.Equal(PlayerQueueResult.Ok, result);
            BuildQueueEntry entry = Assert.Single(node.BuildQueue);
            Assert.Null(entry.Role);
            Assert.True(ShipyardManager.IsNamedByHull(entry));
            Assert.StartsWith("B:", entry.Id, StringComparison.Ordinal);
            Assert.Equal(world.Tuning.HighestBuildRolePriority(), entry.Priority);
            Assert.Contains(log, l => l.Contains("BUILD_QUEUED_BY_PLAYER role=- node=N1 prefab=Hauler_A"));
        }

        [Fact]
        public void QueueBuild_WithNoRole_RefusesAHullThatFitsNoRole()
        {
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            recipes.CostByPrefab["Brick"] = 40;   // no hold, no drill, no gun

            Dictionary<string, long> bom;
            PlayerQueueResult result = new ShipyardManager().QueueBuild(
                world, world.Tuning, "F", "N1", null, "Brick", out bom, new List<string>());

            Assert.Equal(PlayerQueueResult.UnknownPrefab, result);
            Assert.Empty(node.BuildQueue);
        }

        [Fact]
        public void Doctrine_PlacesABuildNamedByHull()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            var st = MakeState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 1);

            List<string> log = TickShipyard(world, st);

            BuildQueueEntry entry = Assert.Single(yard.BuildQueue, e => e.PrefabId == "Ridill");
            Assert.Null(entry.Role);
            Assert.Contains(log, l => l.Contains("DOCTRINE_BUILD ") && l.Contains("placed=1") && !l.Contains("role="));
        }

        [Fact]
        public void ADemand_AdoptsABuildNamedByHull_ThatItsHullFits_RatherThanOpeningItsOwn()
        {
            // A player's one-off Trade-capable build at a yard whose Trade demand would otherwise
            // open an automatic build beside it. Matching on role equality, the role-less entry is
            // invisible to the demand and the yard ends up building two hulls for one ask.
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddTradeHull(recipes, prefabs, "Hauler");
            AddTradeHull(recipes, prefabs, "AutoHauler");
            SpatialNode yard = AddShipyard(world, "N1");
            var st = MakePlayerState();
            Dictionary<string, long> bom;
            Assert.Equal(PlayerQueueResult.Ok, new ShipyardManager().QueueBuild(
                world, world.Tuning, "F", "N1", null, "Hauler", out bom, new List<string>()));
            world.Demand.Post(DemandEntry.ForShip(st.Tag, "Trade", yard.Id,
                world.UtcNow.AddHours(6), minCargoLiters: 1, postedUtc: world.UtcNow));

            List<string> log = TickShipyard(world, st, PickerFor("Trade", "AutoHauler"));

            Assert.Equal(0, CountEntriesFor(world, "AutoHauler"));
            BuildQueueEntry entry = Assert.Single(yard.BuildQueue);
            Assert.Equal("Hauler", entry.PrefabId);
            Assert.DoesNotContain(log, l => l.Contains("BUILD_QUEUED role=Trade"));
        }

        [Fact]
        public void ADemand_DoesNotAdoptABuildNamedByHull_ThatItsHullDoesNotFit()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddCombatHull(recipes, prefabs, "Gunboat");
            AddTradeHull(recipes, prefabs, "AutoHauler");
            SpatialNode yard = AddShipyard(world, "N1");
            var st = MakePlayerState();
            Dictionary<string, long> bom;
            Assert.Equal(PlayerQueueResult.Ok, new ShipyardManager().QueueBuild(
                world, world.Tuning, "F", "N1", null, "Gunboat", out bom, new List<string>()));
            world.Demand.Post(DemandEntry.ForShip(st.Tag, "Trade", yard.Id,
                world.UtcNow.AddHours(6), minCargoLiters: 1, postedUtc: world.UtcNow));

            TickShipyard(world, st, PickerFor("Trade", "AutoHauler"));

            Assert.Equal(1, CountEntriesFor(world, "Gunboat"));
            Assert.Equal(1, CountEntriesFor(world, "AutoHauler"));
        }

        [Fact]
        public void OneBuildNamedByHull_AnswersOneDemandGroupPerTick_NotRaidAndPatrolBoth()
        {
            // A combat hull fits Raid and Patrol alike. Both groups adopting the one entry would
            // service it twice in a tick; the second group must see it as taken and build its own.
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            AddCombatHull(recipes, prefabs, "Gunboat");
            AddCombatHull(recipes, prefabs, "AutoGun");
            SpatialNode yard = AddShipyard(world, "N1");
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 2;
            var st = MakePlayerState();
            Dictionary<string, long> bom;
            Assert.Equal(PlayerQueueResult.Ok, new ShipyardManager().QueueBuild(
                world, world.Tuning, "F", "N1", null, "Gunboat", out bom, new List<string>()));
            world.Demand.Post(DemandEntry.ForShip(st.Tag, "Raid", yard.Id,
                world.UtcNow.AddHours(6), postedUtc: world.UtcNow));
            world.Demand.Post(DemandEntry.ForShip(st.Tag, "Patrol", yard.Id,
                world.UtcNow.AddHours(6), postedUtc: world.UtcNow));

            TickShipyard(world, st, (r, tag) => r == "Raid" || r == "Patrol" ? new List<string> { "AutoGun" } : null);

            Assert.Equal(1, CountEntriesFor(world, "Gunboat"));
            Assert.Equal(1, CountEntriesFor(world, "AutoGun"));
        }

        // ---- the ban list and the ceiling's refit gap (FAC-451) ----------------------------------

        /// <summary>The refit world plus a control family, Clipper -> Clipper@2, that nothing excludes.</summary>
        private static WorldModel MakeTwoFamilyRefitWorld(out SpatialNode yard, out FactionState st)
        {
            WorldModel world = MakeRefitWorld(out yard, out st);
            var prefabs = (FakePrefabCatalogue)world.PrefabCatalogue;
            var recipes = (FakeRecipeRegistry)world.Recipes;
            prefabs.Versions["Clipper@2"] = new KeyValuePair<string, int>("Clipper", 2);
            prefabs.CargoLitersByPrefab["Clipper"] = 1000;
            prefabs.CargoLitersByPrefab["Clipper@2"] = 1000;
            recipes.BomByPrefab["Clipper"] = new Dictionary<string, long> { { "SteelPlate", 50 } };
            recipes.BomByPrefab["Clipper@2"] = new Dictionary<string, long> { { "SteelPlate", 60 } };
            yard.ShipStock["Ridill"] = 2;
            yard.ShipStock["Clipper"] = 2;
            st.HullRefitCaps["Ridill"] = 5;
            st.HullRefitCaps["Clipper"] = 5;
            return world;
        }

        [Fact]
        public void Refit_SkipsAFamilyWhoseLatestIsOverTheCeiling_AndTheControlStillRefits()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = MakeTwoFamilyRefitWorld(out yard, out st);
            world.PrefabOverCeiling = id => id == "Ridill@2";

            var log = new List<string>();
            HullRefit.Intake(st, world, log);

            Assert.DoesNotContain(log, l => l.Contains("REFIT_QUEUED") && l.Contains("to=Ridill@2"));
            Assert.Contains(log, l => l.Contains("REFIT_QUEUED prefab=Clipper to=Clipper@2"));
            Assert.Equal(2, yard.ShipStock["Ridill"]);
            HullRefit.Row row = HullRefit.Rows(world, st).Find(r => r.Family == "Ridill");
            Assert.Equal(WorldModel.ExclusionOverCeiling, row.Blocked);
            Assert.Null(HullRefit.Rows(world, st).Find(r => r.Family == "Clipper").Blocked);
        }

        [Fact]
        public void Refit_SkipsAFamilyWhoseLatestIsBanned()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = MakeTwoFamilyRefitWorld(out yard, out st);
            world.PrefabBanned = id => id == "Ridill" || id == "Ridill@2";

            var log = new List<string>();
            HullRefit.Intake(st, world, log);

            Assert.DoesNotContain(log, l => l.Contains("REFIT_QUEUED") && l.Contains("to=Ridill@2"));
            Assert.Contains(log, l => l.Contains("REFIT_QUEUED prefab=Clipper to=Clipper@2"));
            Assert.Equal(WorldModel.ExclusionBanned, HullRefit.Rows(world, st).Find(r => r.Family == "Ridill").Blocked);
        }

        [Fact]
        public void QueueBuild_RefusesABannedDesign_NamingTheBanBeforeTheCeiling()
        {
            SpatialNode node; FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeQueueWorld(out node, out recipes, out prefabs);
            AddTradeHull(recipes, prefabs, "Hauler_A");
            world.PrefabBanned = id => id == "Hauler_A";
            world.PrefabOverCeiling = id => id == "Hauler_A";

            Dictionary<string, long> bom;
            PlayerQueueResult result = new ShipyardManager().QueueBuild(
                world, world.Tuning, "F", "N1", null, "Hauler_A", out bom, new List<string>());

            Assert.Equal(PlayerQueueResult.Banned, result);
            Assert.Empty(node.BuildQueue);
        }

        [Fact]
        public void Doctrine_ALineForABannedDesign_PlacesNothing_AndTheReportSaysBanned()
        {
            FakeRecipeRegistry recipes; FakePrefabCatalogue prefabs;
            var world = MakeBuildGoalWorld(out recipes, out prefabs);
            SpatialNode yard = AddShipyard(world, "N1");
            var st = MakePlayerState();
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 2);
            world.PrefabBanned = id => id == "Ridill";

            TickShipyard(world, st);

            Assert.Empty(yard.BuildQueue);
            DoctrineLineReport line = Assert.Single(FleetDoctrineReport.Build(world, st).Lines);
            Assert.True(line.Banned);
            Assert.False(line.OverCeiling);
        }

        [Fact]
        public void ABan_CancelsAndRefundsQueuedBuilds_DelistsShelves_AndCancelsRefits_OfThatDesignOnly()
        {
            SpatialNode yard; FactionState st;
            WorldModel world = MakeTwoFamilyRefitWorld(out yard, out st);
            st.HullRefitCaps.Clear();
            var queued = new BuildQueueEntry { Id = "B:001", PrefabId = "Ridill@2" };
            queued.Bom["Comp:SteelPlate"] = 100;
            queued.Collected["Comp:SteelPlate"] = 40;
            yard.BuildQueue.Add(queued);
            var other = new BuildQueueEntry { Id = "B:002", PrefabId = "Clipper@2" };
            other.Bom["Comp:SteelPlate"] = 60;
            yard.BuildQueue.Add(other);
            yard.ListedForSale["Ridill"] = 1;
            var refit = new DamagedHull { PrefabId = "Ridill", Integrity = 1.0, QueuedUtc = world.UtcNow, RefitTo = "Ridill@2" };
            refit.Bill["Comp:Thruster"] = 30;
            refit.Collected["Comp:Thruster"] = 12;
            yard.DamagedHulls.Add(refit);
            long steelBefore = world.GetNodeInventory(yard.Id, "Comp:SteelPlate");
            world.PrefabBanned = id => FleetDecommission.Matches(world.PrefabCatalogue, id, "Ridill");

            var log = new List<string>();
            PrefabBanResult r = PrefabBanEnforcement.Apply(world, new[] { st }, "Ridill", log);

            Assert.Equal(1, r.BuildsCancelled);
            Assert.Equal(1, r.Delisted);
            Assert.Equal(1, r.RefitsCancelled);
            Assert.Equal(new[] { other }, yard.BuildQueue);
            Assert.Equal(steelBefore + 40, world.GetNodeInventory(yard.Id, "Comp:SteelPlate"));
            Assert.Equal(12, world.GetNodeInventory(yard.Id, "Comp:Thruster"));
            Assert.False(yard.ListedForSale.ContainsKey("Ridill"));
            Assert.Empty(yard.DamagedHulls);
            Assert.Equal(3, yard.ShipStock["Ridill"]);   // the two parked plus the hull out of refit
            Assert.Equal(3, r.Parked);
            Assert.Contains(log, l => l.Contains("BUILD_CANCELLED id=B:001") && l.Contains("reason=banned"));
            Assert.Contains(log, l => l.Contains("REFIT_CANCELLED prefab=Ridill to=Ridill@2") && l.Contains("reason=banned"));
        }
    }
}
