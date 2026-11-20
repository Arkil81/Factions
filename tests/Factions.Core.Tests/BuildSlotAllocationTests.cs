using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Which entries hold a faction's shipyard slots, chosen across all its yards at once
    /// (<see cref="BuildSlotAllocation"/>) rather than one queue at a time.
    ///
    /// <para>The failure every test here is written around is the one measured live 2026-09-15:
    /// SPRT held 43 entries across 9 yards and built 2 combat hulls in 243, because at each yard
    /// separately the role table put Trade above Patrol and its Trade demand never emptied. So the
    /// characteristic failure is <b>a queued role that is never active anywhere</b>, and a test that
    /// cannot express "anywhere" cannot see it — which is why these use two yards, not one.</para></summary>
    public class BuildSlotAllocationTests
    {
        private static WorldModel MakeWorld()
        {
            var w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return w;
        }

        private static int _yardSeq;

        private static SpatialNode Yard(WorldModel world, string id, int level = 1)
        {
            SpatialNode n = world.Graph.Add(new Vec3(1000.0 * ++_yardSeq, 0, 0), 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength    = 1.0;
            n.StableStationId = 42;
            n.Capabilities[NodeCapabilityKind.Shipyard] = level;
            return n;
        }

        /// <summary>Hours old, so a test can say which entry has been waiting longer without
        /// depending on the order it happened to be added in.</summary>
        private static BuildQueueEntry Queue(WorldModel world, SpatialNode node, string role,
            double ageHours = 1.0, int? priority = null, bool forSale = false)
        {
            var e = new BuildQueueEntry
            {
                Id = node.MintBuildEntryId(role), Role = role, PrefabId = role + "Prefab",
                DestinedForSale = forSale,
                CreatedUtc = world.UtcNow.AddHours(-ageHours),
                ExpiresUtc = world.UtcNow.AddHours(23),
                Priority = priority ?? ShipyardManager.RolePriority(role, world.Tuning, forSale),
            };
            e.Bom["Comp:SteelPlate"] = 100;
            node.BuildQueue.Add(e);
            return e;
        }

        private static void PostShipDemand(WorldModel world, string role, string nodeId)
        {
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:" + role, NodeId = nodeId,
                ExpiresUtc = world.UtcNow.AddMinutes(30),
            });
        }

        // ---- the FAC-331 case ---------------------------------------------------------------

        [Fact]
        public void AQueuedRoleIsWorkedSomewhere_EvenWhenAnotherRoleOutranksItAtEveryYard()
        {
            // Two single-slot yards, both holding a Trade entry and a Patrol entry. Ranked per node
            // the Trade entry (5) beats the Patrol entry (4) at BOTH, so Patrol is worked nowhere --
            // which is precisely SPRT's live shape. Faction-wide, two slots and two roles means one
            // each.
            var world = MakeWorld();
            SpatialNode a = Yard(world, "N1");
            SpatialNode b = Yard(world, "N2");
            BuildQueueEntry tradeA  = Queue(world, a, "Trade");
            BuildQueueEntry patrolA = Queue(world, a, "Patrol");
            BuildQueueEntry tradeB  = Queue(world, b, "Trade");
            BuildQueueEntry patrolB = Queue(world, b, "Patrol");
            PostShipDemand(world, "Trade", "N1");
            PostShipDemand(world, "Patrol", "N1");

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            Assert.True(a.IsActiveBuild(tradeA) || a.IsActiveBuild(patrolA));
            Assert.True(b.IsActiveBuild(tradeB) || b.IsActiveBuild(patrolB));
            Assert.True(a.IsActiveBuild(patrolA) || b.IsActiveBuild(patrolB));
            Assert.True(a.IsActiveBuild(tradeA)  || b.IsActiveBuild(tradeB));
        }

        [Fact]
        public void AShareFollowsLiveDemand_NotTheRoleTable()
        {
            // Four single-slot yards, each holding one Trade and one Raid entry. Three live Trade
            // demands against one Raid demand is a 3:1 share of four slots.
            var world = MakeWorld();
            var yards = new List<SpatialNode>();
            var trade = new List<BuildQueueEntry>();
            var raid  = new List<BuildQueueEntry>();
            for (int i = 0; i < 4; i++)
            {
                SpatialNode y = Yard(world, "N" + i);
                yards.Add(y);
                trade.Add(Queue(world, y, "Trade"));
                raid.Add(Queue(world, y, "Raid"));
            }
            for (int i = 0; i < 3; i++) PostShipDemand(world, "Trade", "N" + i);
            PostShipDemand(world, "Raid", "N0");

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            int tradeActive = 0, raidActive = 0;
            for (int i = 0; i < 4; i++)
            {
                if (yards[i].IsActiveBuild(trade[i])) tradeActive++;
                if (yards[i].IsActiveBuild(raid[i]))  raidActive++;
            }
            Assert.Equal(3, tradeActive);
            Assert.Equal(1, raidActive);
        }

        // ---- bootstrap ----------------------------------------------------------------------

        [Fact]
        public void ARoleTheFactionOwnsNoHullFor_IsServedBeforeAnyShare()
        {
            // The one thing the role table was really doing (FAC-204: a faction with no drill cannot
            // mine its way to one), stated for the role the table ranks LOWEST. Raid sits at 3 and
            // Trade at 5, so node by node Trade wins every yard -- the characteristic failure is Raid
            // active nowhere. Owning no armed hull at all is what has to override that.
            var world = MakeWorld();
            world.PrefabCatalogue = new CargoOnlyPrefabCatalogue();
            var yards = new List<SpatialNode>();
            var raid  = new List<BuildQueueEntry>();
            for (int i = 0; i < 4; i++)
            {
                SpatialNode y = Yard(world, "N" + i);
                yards.Add(y);
                Queue(world, y, "Trade");
                raid.Add(Queue(world, y, "Raid"));
                PostShipDemand(world, "Trade", "N" + i);
                // Hulls the faction DOES own, so only Raid is in bootstrap.
                y.ShipStock["Hauler"] = 1;
                y.ShipStock["Driller"] = 1;
            }
            PostShipDemand(world, "Raid", "N0");

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            for (int i = 0; i < 4; i++) Assert.True(yards[i].IsActiveBuild(raid[i]));
        }

        [Fact]
        public void BootstrapClearsItself_OnceOneSuitableHullExists()
        {
            // Same estate, one armed hull in stock. The override is gone and Raid is back on its
            // share -- which is what makes it a bootstrap rule rather than a new ranking.
            var world = MakeWorld();
            world.PrefabCatalogue = new CargoOnlyPrefabCatalogue();
            var yards = new List<SpatialNode>();
            var raid  = new List<BuildQueueEntry>();
            for (int i = 0; i < 4; i++)
            {
                SpatialNode y = Yard(world, "N" + i);
                yards.Add(y);
                Queue(world, y, "Trade");
                raid.Add(Queue(world, y, "Raid"));
                PostShipDemand(world, "Trade", "N" + i);
                y.ShipStock["Hauler"] = 1;
                y.ShipStock["Driller"] = 1;
            }
            PostShipDemand(world, "Raid", "N0");
            yards[0].ShipStock["Gunship"] = 1;

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            int raidActive = 0;
            for (int i = 0; i < 4; i++) if (yards[i].IsActiveBuild(raid[i])) raidActive++;
            Assert.Equal(1, raidActive);
        }

        // ---- the player's lever still moves the slot ----------------------------------------

        [Fact]
        public void ARankThePlayerRaised_TakesASlotTheShareWouldNotHaveGivenIt()
        {
            var world = MakeWorld();
            SpatialNode y = Yard(world, "N1");
            BuildQueueEntry trade  = Queue(world, y, "Trade", ageHours: 5);
            BuildQueueEntry patrol = Queue(world, y, "Patrol", ageHours: 1);
            for (int i = 0; i < 6; i++) PostShipDemand(world, "Trade", "N" + i);

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);
            Assert.True(y.IsActiveBuild(trade));       // the automatic answer

            new ShipyardManager().SetBuildPriority(world, "F", "N1", patrol.Id,
                ShipyardManager.RolePriority("Patrol", world.Tuning) + 1, new List<string>());
            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            Assert.True(y.IsActiveBuild(patrol));
            Assert.False(y.IsActiveBuild(trade));
        }

        [Fact]
        public void ARankThePlayerLowered_YieldsTheSlot()
        {
            // Lowered to the Patrol entry's own rank, not below it: at 4 against 4 the node-local
            // tie-break is creation order and the Trade entry is the older of the two, so it keeps
            // the slot unless the lowering is read as the instruction it is.
            var world = MakeWorld();
            SpatialNode y = Yard(world, "N1");
            BuildQueueEntry trade  = Queue(world, y, "Trade", ageHours: 5);
            BuildQueueEntry patrol = Queue(world, y, "Patrol", ageHours: 1);
            PostShipDemand(world, "Trade", "N1");

            new ShipyardManager().SetBuildPriority(world, "F", "N1", trade.Id,
                ShipyardManager.RolePriority("Patrol", world.Tuning), new List<string>());
            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            Assert.True(y.IsActiveBuild(patrol));
            Assert.False(y.IsActiveBuild(trade));
        }

        [Fact]
        public void ARankLeftBehindByARetiredTableRow_IsNotReadAsAPlayerInstruction()
        {
            // Measured live 2026-09-16, SPRT N007. `StoreSmall:003` was queued 2026-09-01 when the
            // table still carried a `StoreSmall: 2` row, so it holds rank 2 where RolePriority now
            // answers 1 for a shelf build. Read by comparing the two, a table change becomes a human
            // instruction and a shop hull is admitted ahead of the entire fleet.
            var world = MakeWorld();
            SpatialNode y = Yard(world, "N1");
            BuildQueueEntry legacy = Queue(world, y, "StoreSmall", ageHours: 200, priority: 2, forSale: true);
            BuildQueueEntry fleet  = Queue(world, y, "Trade", ageHours: 1);
            PostShipDemand(world, "Trade", "N1");

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            Assert.False(legacy.PriorityByPlayer);
            Assert.True(y.IsActiveBuild(fleet));
            Assert.False(y.IsActiveBuild(legacy));
        }

        [Fact]
        public void ThePlayerVerbIsWhatMarksARankAsHandSet()
        {
            // The other half of the pair: the same shape, but set through the verb, must still move
            // the slot. Without this the fix above would read as "hand-set ranks stopped working".
            var world = MakeWorld();
            SpatialNode y = Yard(world, "N1");
            BuildQueueEntry raised = Queue(world, y, "Raid", ageHours: 1);
            Queue(world, y, "Trade", ageHours: 200);
            PostShipDemand(world, "Trade", "N1");

            var mgr = new ShipyardManager();
            Assert.Equal(PlayerQueueResult.Ok,
                mgr.SetBuildPriority(world, "F", "N1", raised.Id,
                    ShipyardManager.RolePriority("Raid", world.Tuning) + 1, new List<string>()));
            Assert.True(raised.PriorityByPlayer);

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);
            Assert.True(y.IsActiveBuild(raised));
        }

        // ---- invariants the rest of the model depends on -------------------------------------

        [Fact]
        public void NoSlotIsLeftIdleWhileAnEntryWaitsAtThatYard()
        {
            // IsActiveBuild is a position test over OrderedBuildQueue, so "chosen by the allocator"
            // and "inside the first BuildSlotCount" must be the same set. They are only the same if
            // every yard is filled to capacity whenever it has the entries -- a quota that left a
            // slot empty would make the two disagree silently.
            var world = MakeWorld();
            SpatialNode big   = Yard(world, "N1", level: 3);
            SpatialNode small = Yard(world, "N2");
            for (int i = 0; i < 4; i++) Queue(world, big, "Trade", ageHours: 1 + i);
            Queue(world, small, "Raid");
            PostShipDemand(world, "Raid", "N2");

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            int bigActive = 0;
            foreach (BuildQueueEntry e in big.BuildQueue) if (big.IsActiveBuild(e)) bigActive++;
            Assert.Equal(3, bigActive);
            Assert.Equal(3, big.ActiveBuildIds.Count);
            Assert.Equal(1, small.ActiveBuildIds.Count);
        }

        [Fact]
        public void AllocationNeverOverfillsAYard()
        {
            var world = MakeWorld();
            SpatialNode y = Yard(world, "N1");
            Queue(world, y, "Trade");
            Queue(world, y, "Raid");
            Queue(world, y, "Mine");
            Queue(world, y, "Patrol");

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            Assert.Single(y.ActiveBuildIds);
        }

        [Fact]
        public void APausedYardCarriesNoAllocation()
        {
            // It claims nothing and services nothing whatever this says, so a set left behind would
            // only be a stale answer for whoever reads it next.
            var world = MakeWorld();
            SpatialNode y = Yard(world, "N1");
            Queue(world, y, "Trade");
            BuildSlotAllocation.Allocate(world, "F", world.Tuning);
            Assert.NotNull(y.ActiveBuildIds);

            y.ShipyardPaused = true;
            BuildSlotAllocation.Allocate(world, "F", world.Tuning);
            Assert.Null(y.ActiveBuildIds);
        }

        [Fact]
        public void AnUnallocatedNodeKeepsTheNodeLocalOrder()
        {
            // The fallback that makes a boot tick, the offline sim and every fixture that never
            // allocates behave exactly as they did.
            var world = MakeWorld();
            SpatialNode y = Yard(world, "N1");
            BuildQueueEntry low  = Queue(world, y, "Raid",  ageHours: 5);
            BuildQueueEntry high = Queue(world, y, "Mine",  ageHours: 1);

            Assert.Null(y.ActiveBuildIds);
            Assert.True(y.IsActiveBuild(high));    // rank 6 over rank 3, regardless of age
            Assert.False(y.IsActiveBuild(low));
        }

        [Fact]
        public void ClaimOrderStaysOnPriority_WhileTheSlotGoesToTheShare()
        {
            // The two questions now have two mechanisms and must not have drifted into one: the
            // component ranking is still the role table's, whoever is holding the slot.
            var world = MakeWorld();
            SpatialNode y = Yard(world, "N1");
            BuildQueueEntry patrol = Queue(world, y, "Patrol", ageHours: 5);
            BuildQueueEntry mine   = Queue(world, y, "Mine",   ageHours: 1);
            PostShipDemand(world, "Patrol", "N1");

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            Assert.True(y.IsActiveBuild(patrol));                       // oldest wait wins the slot
            Assert.Same(mine, y.ClaimOrderedBuildQueue()[0]);           // rank 6 still wins a component
        }

        // ---- the readback ---------------------------------------------------------------------

        [Fact]
        public void AChangedWorkingSet_IsLogged_AndAnUnchangedOneIsNot()
        {
            // The choice is derived and never persisted, so this line is the only thing that can say
            // which entry a yard is feeding. Silence when nothing moved is half the contract: a line
            // per sweep per yard would bury the change it exists to show.
            var world = MakeWorld();
            SpatialNode y = Yard(world, "N1");
            Queue(world, y, "Trade", ageHours: 5);
            BuildQueueEntry patrol = Queue(world, y, "Patrol", ageHours: 9);
            PostShipDemand(world, "Trade", "N1");

            var first = new List<string>();
            BuildSlotAllocation.Allocate(world, "F", world.Tuning, first);
            Assert.Contains(first, l => l.Contains("BUILD_SLOTS node=N1")
                                        && l.Contains("working=" + patrol.Id));

            var second = new List<string>();
            BuildSlotAllocation.Allocate(world, "F", world.Tuning, second);
            Assert.DoesNotContain(second, l => l.Contains("BUILD_SLOTS"));
        }

        // ---- where the demand is posted in the first place ----------------------------------

        [Fact]
        public void TheNetworkCombatDemand_LandsOnAYardWithARoomToBuildIt()
        {
            // A demand creates its build entry at the node it was posted at, so posting the one
            // network-wide combat order at the first yard in graph order queues it behind whatever
            // that yard is already working while the faction's other yards hold nothing. Live
            // 2026-09-15: EXP held 8 entries at N012 and six idle yards.
            var world = MakeWorld();
            world.Tuning.MusterStockBaseline = 1;    // so the combat target is above the zero stock
            SpatialNode first = Yard(world, "N1");
            SpatialNode spare = Yard(world, "N2");
            Queue(world, first, "Trade");            // N1's only slot is taken
            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            var log = new List<string>();
            new ShipyardManager().Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                world, world.Tuning, log);

            // Read at the posting site, not off the register: the same sweep services what it posts,
            // so a demand that was answered is gone by the time the register is snapshotted and its
            // absence would say nothing about where it went.
            Assert.Contains(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Raid")
                                      && l.Contains("node=" + spare.Id));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_DEMAND_PROACTIVE") && l.Contains("role=Raid")
                                            && l.Contains("node=" + first.Id));
        }

        /// <summary>Every stocked prefab hauls; only "Driller" drills and only "Gunship" shoots.
        /// Narrow on purpose: a catalogue that classified everything as everything could not express
        /// "this faction owns no armed hull", which is the whole bootstrap condition.</summary>
        private sealed class CargoOnlyPrefabCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 1000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return prefabId == "Gunship" ? new[] { new WeaponBand { Dps = 500, RangeM = 800 } } : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 1000.0; }
            public double DrillYieldScore(string prefabId) { return prefabId == "Driller" ? 1.0 : 0.0; }
            public string OwnerFactionTag(string prefabId) { return null; }
            public bool IsStationPrefab(string prefabId) { return false; }
            public string Site(string prefabId) { return null; }
        }

        // ---- builds named by hull ---------------------------------------------------------------

        /// <summary>A build a player or a doctrine line named by hull: no role, the starting rank of
        /// the highest role.</summary>
        private static BuildQueueEntry QueueByHull(WorldModel world, SpatialNode node, string prefabId,
            double ageHours = 1.0)
        {
            var e = new BuildQueueEntry
            {
                Id = node.MintBuildEntryId(null), PrefabId = prefabId,
                CreatedUtc = world.UtcNow.AddHours(-ageHours),
                ExpiresUtc = world.UtcNow.AddHours(23),
                Priority = ShipyardManager.RolePriority(null, world.Tuning),
            };
            e.Bom["Comp:SteelPlate"] = 100;
            node.BuildQueue.Add(e);
            return e;
        }

        [Fact]
        public void ABuildNamedByHull_IsBootstrap_WhenItsHullFitsARoleTheFactionOwnsNoHullFor()
        {
            // The FAC-204 shape again, with the armed hull asked for by name rather than by role:
            // the faction owns no armed hull, so a Gunship build is what clears that, whatever role
            // the player might have meant it for.
            var world = MakeWorld();
            world.PrefabCatalogue = new CargoOnlyPrefabCatalogue();
            var yards = new List<SpatialNode>();
            var gunships = new List<BuildQueueEntry>();
            for (int i = 0; i < 4; i++)
            {
                SpatialNode y = Yard(world, "N" + i);
                yards.Add(y);
                Queue(world, y, "Trade", ageHours: 5.0);
                gunships.Add(QueueByHull(world, y, "Gunship"));
                PostShipDemand(world, "Trade", "N" + i);
                y.ShipStock["Hauler"] = 1;
                y.ShipStock["Driller"] = 1;
            }

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            for (int i = 0; i < 4; i++) Assert.True(yards[i].IsActiveBuild(gunships[i]));
        }

        [Fact]
        public void ABuildNamedByHull_IsNotBootstrap_WhenTheFactionOwnsAHullForEveryRoleItFits()
        {
            var world = MakeWorld();
            world.PrefabCatalogue = new CargoOnlyPrefabCatalogue();
            SpatialNode y = Yard(world, "N0");
            BuildQueueEntry trade = Queue(world, y, "Trade", ageHours: 5.0);
            BuildQueueEntry gunship = QueueByHull(world, y, "Gunship");
            PostShipDemand(world, "Trade", "N0");
            y.ShipStock["Hauler"] = 1;
            y.ShipStock["Driller"] = 1;
            y.ShipStock["Gunship"] = 1;

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            // One slot: the older Trade entry keeps it on its share; the Gunship waits its turn.
            Assert.True(y.IsActiveBuild(trade));
            Assert.False(y.IsActiveBuild(gunship));
        }

        [Fact]
        public void BuildsNamedByHull_ShareTheSlotsByDesign_NotByAge()
        {
            // Three of one design queued first, one of another last, two single-slot yards. In one
            // share the oldest two would take both slots and the second design would start nowhere;
            // a share per design gives each a slot, the way a share per role does.
            var world = MakeWorld();
            SpatialNode a = Yard(world, "N1");
            SpatialNode b = Yard(world, "N2");
            QueueByHull(world, a, "Clipper", ageHours: 3.0);
            QueueByHull(world, a, "Clipper", ageHours: 2.5);
            QueueByHull(world, b, "Clipper", ageHours: 2.0);
            BuildQueueEntry other = QueueByHull(world, b, "Tanto", ageHours: 1.0);

            BuildSlotAllocation.Allocate(world, "F", world.Tuning);

            Assert.True(b.IsActiveBuild(other));
        }
    }
}
