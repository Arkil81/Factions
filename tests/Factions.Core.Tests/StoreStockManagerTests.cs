using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class StoreStockManagerTests
    {
        // ---- helpers --------------------------------------------------------------------

        private static WorldModel MakeWorld()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var tuning = new FactionTuning();
            world.Registry  = new NullItemRegistry();
            world.Catalogue = new TuningItemCatalogue(tuning);
            return world;
        }

        private static SpatialNode AddNode(WorldModel world, string id, bool hasShipyard = false,
                                           string tag = "F")
        {
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength    = 1.0;
            n.StableStationId = Math.Abs(id.GetHashCode()) + 1;
            if (hasShipyard)
                n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return n;
        }

        private static FactionState MakeState(string tag = "F")
            => new FactionState { Tag = tag };

        private static FactionTuning Tuning(int small = 5, int large = 2)
            => new FactionTuning
            {
                StoreShipSlotsSmallGrid   = small,
                StoreShipSlotsLargeGrid   = large,
                StoreStockIntervalSeconds = 60,
            };

        private static void Tick(WorldModel world, FactionState st, FactionTuning tuning = null,
                                 IFactionTypeCatalogue factionTypes = null, List<string> log = null)
        {
            new StoreStockManager(factionTypes).Tick(
                new List<FactionState> { st }, world, tuning ?? Tuning(), log ?? new List<string>());
        }

        /// <summary>Resolves one tag to one type; every other tag is unresolved, which
        /// <see cref="FactionStorePolicy"/> treats as "keeps its store".</summary>
        private sealed class StubFactionTypeCatalogue : IFactionTypeCatalogue
        {
            private static readonly IReadOnlyList<string> Empty = new List<string>().AsReadOnly();
            private readonly string _tag;
            private readonly string _type;

            public StubFactionTypeCatalogue(string tag, string type) { _tag = tag; _type = type; }

            public bool TryGetFactionType(string factionTag, out string factionType)
            {
                if (factionTag == _tag) { factionType = _type; return true; }
                factionType = null;
                return false;
            }
            public IReadOnlyList<string> OfferInvKeys(string factionType) { return Empty; }
            public IReadOnlyList<string> OrderInvKeys(string factionType) { return Empty; }
        }

        private static BuildQueueEntry QueueBuild(WorldModel world, SpatialNode node, string role,
                                                 string collectedKey = "Comp:SteelPlate", long collected = 40,
                                                 bool destinedForSale = false)
        {
            var entry = new BuildQueueEntry
            {
                Id         = role + ":001",
                Role       = role,
                PrefabId   = "SomePrefab",
                CreatedUtc = world.UtcNow.AddHours(-1),
                ExpiresUtc = world.UtcNow.AddHours(23),
                // Cancellation is keyed on purpose, not on the role name -- a shelf build names a
                // real capability role now, so the caller says which it is.
                DestinedForSale = destinedForSale,
            };
            entry.Bom[collectedKey]       = 100;
            entry.Collected[collectedKey] = collected;
            node.BuildQueue.Add(entry);
            return entry;
        }

        private static long NodeStock(SpatialNode node, string key)
        {
            long qty;
            return node.Inventory.TryGetValue(key, out qty) ? qty : 0;
        }

        /// <summary>Ship stock is unified and tag-based now (see ShipPool) — StoreSmall/StoreLarge
        /// are classified by catalogue GridSize, not a role bucket, so listing a ship for a role
        /// requires wiring a catalogue that reports the matching grid size for its prefab.</summary>
        private sealed class FakePrefabCatalogue : IPrefabCatalogue
        {
            public readonly Dictionary<string, string> GridSizeByPrefab = new Dictionary<string, string>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId)
            {
                string g;
                return GridSizeByPrefab.TryGetValue(prefabId, out g) ? g : "Small";
            }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static void QueueShips(WorldModel world, SpatialNode node, string gridSize, int count,
                                       string prefab = "SomePrefab")
        {
            var cat = world.PrefabCatalogue as FakePrefabCatalogue;
            if (cat == null) world.PrefabCatalogue = cat = new FakePrefabCatalogue();
            cat.GridSizeByPrefab[prefab] = gridSize;

            int reserved;
            node.ListedForSale.TryGetValue(prefab, out reserved);
            node.ListedForSale[prefab] = reserved + count;
            int stock;
            node.ShipStock.TryGetValue(prefab, out stock);
            node.ShipStock[prefab] = stock + count;
        }

        // ---- demand posting — small grid ------------------------------------------------

        [Fact]
        public void SmallGrid_NetworkEmpty_PostsDemandAtShipyard()
        {
            var world = MakeWorld();
            var yard  = AddNode(world, "Yard", hasShipyard: true);
            var st    = MakeState();

            Tick(world, st);

            Assert.True(HasShelfDemand(world, "F", "Small", "Yard"));
        }

        [Fact]
        public void SmallGrid_StockAtAnotherNode_StillPostsAtTheEmptyYard()
        {
            // The target is per site. Five hulls listed at a trade node say nothing about the yard's
            // own shelf, which is empty.
            var world  = MakeWorld();
            var yard   = AddNode(world, "Yard", hasShipyard: true);
            var market = AddNode(world, "Market");
            QueueShips(world, market, "Small", 5);
            var st = MakeState();

            Tick(world, st);

            Assert.True(HasShelfDemand(world, "F", "Small", "Yard"));
        }

        [Fact]
        public void SmallGrid_YardAtTarget_NoDemand()
        {
            var world = MakeWorld();
            var yard  = AddNode(world, "Yard", hasShipyard: true);
            QueueShips(world, yard, "Small", 5);
            var st = MakeState();

            Tick(world, st);

            Assert.False(HasShelfDemand(world, "F", "Small", "Yard"));
        }

        [Fact]
        public void SmallGrid_PartialNetwork_PostsDemand()
        {
            var world  = MakeWorld();
            var yard   = AddNode(world, "Yard", hasShipyard: true);
            var market = AddNode(world, "Market");
            QueueShips(world, market, "Small", 2); // 2 of 5 filled at a non-Shipyard node
            var st = MakeState();

            Tick(world, st);

            Assert.True(HasShelfDemand(world, "F", "Small", "Yard"));
        }

        // ---- demand posting — large grid ------------------------------------------------

        [Fact]
        public void LargeGrid_NetworkEmpty_PostsDemandAtShipyard()
        {
            var world = MakeWorld();
            AddNode(world, "Yard", hasShipyard: true);
            var st = MakeState();

            Tick(world, st);

            Assert.True(HasShelfDemand(world, "F", "Large", "Yard"));
        }

        [Fact]
        public void LargeGrid_NetworkFull_NoDemand()
        {
            var world  = MakeWorld();
            var yard   = AddNode(world, "Yard", hasShipyard: true);
            QueueShips(world, yard, "Large", 2);
            var st = MakeState();

            Tick(world, st);

            Assert.False(HasShelfDemand(world, "F", "Large", "Yard"));
        }

        // ---- multiple Shipyard nodes ----------------------------------------------------

        [Fact]
        public void MultipleShipyards_BothGetDemandWhenNetworkBelowTarget()
        {
            var world = MakeWorld();
            AddNode(world, "Yard1", hasShipyard: true);
            AddNode(world, "Yard2", hasShipyard: true);
            var st = MakeState();

            Tick(world, st);

            Assert.True(HasShelfDemand(world, "F", "Small", "Yard1"));
            Assert.True(HasShelfDemand(world, "F", "Small", "Yard2"));
        }

        [Fact]
        public void MultipleShipyards_EachYardIsJudgedOnItsOwnShelf()
        {
            // Five hulls split three/two across two yards used to read as one full shelf. Each yard
            // keeps its own, so only the one at target goes quiet.
            var world = MakeWorld();
            var yard1 = AddNode(world, "Yard1", hasShipyard: true);
            var yard2 = AddNode(world, "Yard2", hasShipyard: true);
            QueueShips(world, yard1, "Small", 5);
            QueueShips(world, yard2, "Small", 2);
            var st = MakeState();

            Tick(world, st);

            Assert.False(HasShelfDemand(world, "F", "Small", "Yard1"));
            Assert.True(HasShelfDemand(world, "F", "Small", "Yard2"));
        }

        // ---- no Shipyard / dormant faction ----------------------------------------------

        [Fact]
        public void NoShipyard_NoDemandPosted()
        {
            var world = MakeWorld();
            AddNode(world, "Market"); // economy station, no Shipyard
            var st = MakeState();

            Tick(world, st);

            Assert.False(HasShelfDemand(world, "F", "Small", "Market"));
            Assert.False(HasShelfDemand(world, "F", "Large", "Market"));
        }

        [Fact]
        public void DormantFaction_NoDemandPosted()
        {
            var world = MakeWorld();
            AddNode(world, "Yard", hasShipyard: true);
            var st = MakeState();
            st.DormantUntilUtc = world.UtcNow.AddHours(1);

            Tick(world, st);

            Assert.False(HasShelfDemand(world, "F", "Small", "Yard"));
        }

        // ---- free-build empty strings do not count toward quota -------------------------

        [Fact]
        public void EmptyStringEntries_NotCountedAsFilledSlots()
        {
            var world = MakeWorld();
            var yard  = AddNode(world, "Yard", hasShipyard: true);
            // Simulate 5 free-build entries (empty prefab id from ShipyardManager free-build mode).
            yard.ListedForSale[""] = 5;
            yard.ShipStock[""] = 5;
            var st = MakeState();

            Tick(world, st);

            // Empty entries must not satisfy the quota — demand should still be posted.
            Assert.True(HasShelfDemand(world, "F", "Small", "Yard"));
        }

        // ---- faction-type gate (FAC-152) -------------------------------------------------

        [Fact]
        public void FactionTypeNone_NoDemandPosted()
        {
            var world = MakeWorld();
            AddNode(world, "Yard", hasShipyard: true, tag: "FCTM");
            var st = MakeState("FCTM");

            Tick(world, st, factionTypes: new StubFactionTypeCatalogue("FCTM", "None"));

            Assert.False(HasShelfDemand(world, "FCTM", "Small", "Yard"));
            Assert.False(HasShelfDemand(world, "FCTM", "Large", "Yard"));
        }

        /// <summary>Operator direction 2026-08-29: SPRT sells, FCTM does not. Pirate is a trading
        /// type here even though the ticket's own suggested fix listed it for exclusion.</summary>
        [Fact]
        public void FactionTypePirate_StillPostsDemand()
        {
            var world = MakeWorld();
            AddNode(world, "Yard", hasShipyard: true, tag: "SPRT");
            var st = MakeState("SPRT");

            Tick(world, st, factionTypes: new StubFactionTypeCatalogue("SPRT", "Pirate"));

            Assert.True(HasShelfDemand(world, "SPRT", "Small", "Yard"));
        }

        /// <summary>The gate must never fire on a bare lookup miss — the Sim and any world without
        /// a faction-type catalogue keep the pre-gate behaviour.</summary>
        [Fact]
        public void UnresolvedTag_StillPostsDemand()
        {
            var world = MakeWorld();
            AddNode(world, "Yard", hasShipyard: true);
            var st = MakeState();

            Tick(world, st, factionTypes: new StubFactionTypeCatalogue("IGSL", "Trader"));

            Assert.True(HasShelfDemand(world, "F", "Small", "Yard"));
        }

        /// <summary>Withholding the demand is not enough on its own: ShipyardManager's orphan pass
        /// services an active entry with no live demand pointing at it, so an abandoned store build
        /// would absorb components forever and never reach the no-progress refund.</summary>
        [Fact]
        public void FactionTypeNone_InFlightStoreBuildsAreRefundedAndRemoved()
        {
            var world = MakeWorld();
            var yard  = AddNode(world, "Yard", hasShipyard: true, tag: "FCTM");
            QueueBuild(world, yard, "Trade", collected: 2856, destinedForSale: true);
            var st = MakeState("FCTM");

            Tick(world, st, factionTypes: new StubFactionTypeCatalogue("FCTM", "None"));

            Assert.Empty(yard.BuildQueue);
            Assert.Equal(2856, NodeStock(yard, "Comp:SteelPlate"));
        }

        [Fact]
        public void FactionTypeNone_ReservedInputsAreRefundedToo()
        {
            var world = MakeWorld();
            var yard  = AddNode(world, "Yard", hasShipyard: true, tag: "FCTM");
            BuildQueueEntry e = QueueBuild(world, yard, "Trade", collected: 0, destinedForSale: true);
            e.ReservedInputs["Ingot:Iron"] = 150;
            var st = MakeState("FCTM");

            Tick(world, st, factionTypes: new StubFactionTypeCatalogue("FCTM", "None"));

            Assert.Empty(yard.BuildQueue);
            Assert.Equal(150, NodeStock(yard, "Ingot:Iron"));
        }

        /// <summary>The cancel pass is keyed on the two store roles, not on the faction. A
        /// non-trading faction still raids, trades and mines.</summary>
        [Fact]
        public void FactionTypeNone_NonStoreBuildsSurvive()
        {
            var world = MakeWorld();
            var yard  = AddNode(world, "Yard", hasShipyard: true, tag: "FCTM");
            QueueBuild(world, yard, "Raid");
            QueueBuild(world, yard, "Trade", destinedForSale: true);
            var st = MakeState("FCTM");

            Tick(world, st, factionTypes: new StubFactionTypeCatalogue("FCTM", "None"));

            Assert.Single(yard.BuildQueue);
            Assert.Equal("Raid", yard.BuildQueue[0].Role);
        }

        /// <summary>Swept every tick, not once: a Ship:Store* demand posted before the gate began
        /// firing outlives it, and ShipyardManager can open an entry from it afterwards.</summary>
        [Fact]
        public void FactionTypeNone_CancelsAStoreBuildThatAppearsOnALaterTick()
        {
            var world = MakeWorld();
            var yard  = AddNode(world, "Yard", hasShipyard: true, tag: "FCTM");
            var st    = MakeState("FCTM");
            var cat   = new StubFactionTypeCatalogue("FCTM", "None");

            Tick(world, st, factionTypes: cat);
            QueueBuild(world, yard, "Trade", collected: 12, destinedForSale: true);
            Tick(world, st, factionTypes: cat);

            Assert.Empty(yard.BuildQueue);
            Assert.Equal(12, NodeStock(yard, "Comp:SteelPlate"));
        }

        [Fact]
        public void EligibleFaction_StoreBuildsAreNotCancelled()
        {
            var world = MakeWorld();
            var yard  = AddNode(world, "Yard", hasShipyard: true, tag: "IGSL");
            QueueBuild(world, yard, "Trade", destinedForSale: true);
            var st = MakeState("IGSL");

            Tick(world, st, factionTypes: new StubFactionTypeCatalogue("IGSL", "Trader"));

            Assert.Single(yard.BuildQueue);
        }

        /// <summary>Log-on-change: the cancel line names what it removed, and a later sweep with
        /// nothing left to remove is silent.</summary>
        [Fact]
        public void FactionTypeNone_LogsOnceWhenItActuallyCancels()
        {
            var world = MakeWorld();
            var yard  = AddNode(world, "Yard", hasShipyard: true, tag: "FCTM");
            QueueBuild(world, yard, "Trade", destinedForSale: true);
            var st  = MakeState("FCTM");
            var cat = new StubFactionTypeCatalogue("FCTM", "None");

            var first = new List<string>();
            Tick(world, st, factionTypes: cat, log: first);
            var second = new List<string>();
            Tick(world, st, factionTypes: cat, log: second);

            Assert.Contains(first, l => l.Contains("STORE_STOCK_DISABLED") && l.Contains("cancelled=1"));
            Assert.Contains(first, l => l.Contains("BUILD_CANCELLED") && l.Contains("reason=no_player_store"));
            Assert.Empty(second);
        }

        // ---- helper --------------------------------------------------------------------

        /// <summary>A shelf demand for one grid size at one node. The role is deliberately NOT
        /// asserted: after FAC-060 it is chosen by StoreShelfPlan from the faction's type and
        /// personality, so pinning it here would make these tests assert the composition rule
        /// rather than the restocking rule they are about. What must hold is that the demand exists,
        /// is marked for sale, and asks for the size the slot is short of.</summary>
        private static bool HasShelfDemand(WorldModel world, string faction, string gridSize, string nodeId)
        {
            foreach (DemandEntry d in world.Demand.SnapshotShipDemands(faction))
                if (d.NodeId == nodeId && d.DestinedForSale
                    && string.Equals(d.RequiredGridSize, gridSize, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Any ship demand at all for a faction — used where a test's point is that the
        /// manager stayed silent.</summary>
        private static bool HasAnyShipDemand(WorldModel world, string faction, string nodeId)
        {
            foreach (DemandEntry d in world.Demand.SnapshotShipDemands(faction))
                if (d.NodeId == nodeId) return true;
            return false;
        }
    }
}
