using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Haul and Mine competing for the same hull.
    ///
    /// <para>A cargo-carrying hull is Haul-suitable on capacity alone
    /// (<see cref="ShipPool.IsCargoSuitableValue"/> asks only for nonzero cargo), and the Haul pass
    /// used to run to completion before Mine was considered at all — so a faction's only drill was
    /// routinely spent on a haul while the ore field it was built for reported no_ship_anywhere.
    /// Live 2026-08-31: ISBT owned exactly one WHI-24Drill, whose roles are
    /// Trade/Ferry/Gift/Haul/Mine, and MINE_DISPATCHED was 0 world-wide for two days.</para>
    ///
    /// <para>Since FAC-395 slice 2b an NPC mines only on the extraction orders its planner authors,
    /// and those are walked before the Haul pass: an open order takes the drill, a paused one or
    /// none leaves it to Haul. An ore demand row is no longer a Mine trigger of its own.</para></summary>
    public sealed class MineHaulContentionTests
    {
        private sealed class Catalogue : IPrefabCatalogue
        {
            public bool HasPrefab(string prefabId) => true;
            public long CargoLiters(string prefabId) => 500_000;
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public double Dps(string prefabId) => 0;
            public double DryMassKg(string prefabId) => 0;
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) => null;
            public string GridSize(string prefabId) => "Small";
            public long Cost(string prefabId) => 0;
            public string FactionHint(string prefabId) => "All";
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) => new WeaponBand[0];
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) => new string[0];
            public IEnumerable<string> AllPrefabIds => new[] { "Drill" };
            public double DrillYieldScore(string prefabId) => 1000;
            public IReadOnlyList<string> RolesFor(string prefabId) => new[] { "Mine", "Haul", "Trade" };
        }

        private sealed class Volumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string subTypeId) => 1.0;
        }

        private static WorldModel MakeWorld()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = new Catalogue();
            return world;
        }

        private static SpatialNode Node(WorldModel world, string id, Vec3 pos)
        {
            SpatialNode n = world.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = "F";
            n.HoldStrength = 1.0;
            return n;
        }

        private static void Post(WorldModel world, string item, string nodeId, long qty, DemandClass cls)
        {
            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + item, NodeId = nodeId, Quantity = qty,
                ExpiresUtc = world.UtcNow.AddMinutes(5), Class = cls, FirstPostedUtc = world.UtcNow,
            });
        }

        /// <summary>The faction owns exactly ONE hull, parked at its ore field, and it is suitable for
        /// both jobs. A comfortable catalogue want competes with a starving ship build for it.</summary>
        private static WorldModel OneHullContestedBy(DemandClass mineClass, DemandClass haulClass,
            long haulHave, out FactionState st)
        {
            var world = MakeWorld();
            st = new FactionState { Tag = "F" };

            SpatialNode ore = Node(world, "Ore", new Vec3(0, 0, 0));
            var deposit = new NodeDeposit();
            deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
            ore.Deposit = deposit;
            ore.Richness = 1.0;   // ore under it AND measured: the mint and the drill read this
            ore.ShipStock["Drill"] = 1;              // the only hull the faction has

            // The Mine side: a node starving for ore, with a deposit next door.
            SpatialNode smelter = Node(world, "Smelter", new Vec3(1, 0, 0));
            Post(world, "Ore:Iron", smelter.Id, 200_000, mineClass);

            // The Haul side: a want for a component that really is sitting somewhere else, so a
            // vector forms and reaches for the one hull via an alternate pickup.
            SpatialNode depot = Node(world, "Depot", new Vec3(2, 0, 0));
            depot.Inventory["Comp:SteelPlate"] = 10_000;
            SpatialNode shop = Node(world, "Shop", new Vec3(3, 0, 0));
            if (haulHave > 0) shop.Inventory["Comp:SteelPlate"] = haulHave;
            Post(world, "Comp:SteelPlate", shop.Id, 1_000, haulClass);

            return world;
        }

        private static List<string> Tick(WorldModel world, FactionState st)
        {
            var log = new List<string>();
            new OperationsManager(itemVolumes: new Volumes())
                .Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);
            return log;
        }

        /// <summary>The defect the contest existed for: a hub starving for ore -- here an open
        /// extraction order on the smelter's measured draw -- gets the one hull ahead of a
        /// component want, because extraction orders are walked before the Haul pass.</summary>
        [Fact]
        public void OneHull_AnOpenExtractionOrderTakesIt_AheadOfAHaul()
        {
            FactionState st;
            WorldModel world = OneHullContestedBy(
                mineClass: DemandClass.ShipBuild, haulClass: DemandClass.ShipBuild, haulHave: 0, out st);
            world.Graph.Nodes["Smelter"].DemandHistory["Ore:Iron"] = 1e9;   // the planner's consume term

            List<string> log = Tick(world, st);

            Assert.Contains(st.PendingFleets, f => f.Kind == ActionKind.Mine);
            Assert.DoesNotContain(st.PendingFleets, f => f.Kind == ActionKind.Haul);
            Assert.Contains(log, l => l.Contains("MINE_DISPATCHED") && l.Contains("order=Smelter:Ore:Iron"));
        }

        /// <summary>The discriminator: an ore want with no extraction order behind it -- a nearly met
        /// catalogue row, no measured draw -- takes no drill, and the same single hull hauls.
        /// Without this the test above would pass on "Mine now always wins".</summary>
        [Fact]
        public void OneHull_Hauls_WhenNoExtractionOrderIsOpen()
        {
            FactionState st;
            // The ore want is nearly met and merely a catalogue preference; the component want is a
            // starving ship build.
            var world = MakeWorld();
            st = new FactionState { Tag = "F" };

            SpatialNode ore = Node(world, "Ore", new Vec3(0, 0, 0));
            var deposit = new NodeDeposit();
            deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
            ore.Deposit = deposit;
            ore.Richness = 1.0;   // ore under it AND measured: the mint and the drill read this
            ore.ShipStock["Drill"] = 1;

            SpatialNode smelter = Node(world, "Smelter", new Vec3(1, 0, 0));
            smelter.Inventory["Ore:Iron"] = 199_000;      // 199,000 of 200,000 -- nearly met
            Post(world, "Ore:Iron", smelter.Id, 200_000, DemandClass.Catalog);

            SpatialNode depot = Node(world, "Depot", new Vec3(2, 0, 0));
            depot.Inventory["Comp:SteelPlate"] = 10_000;
            SpatialNode shop = Node(world, "Shop", new Vec3(3, 0, 0));
            Post(world, "Comp:SteelPlate", shop.Id, 1_000, DemandClass.ShipBuild);

            Tick(world, st);

            Assert.Contains(st.PendingFleets, f => f.Kind == ActionKind.Haul);
            Assert.DoesNotContain(st.PendingFleets, f => f.Kind == ActionKind.Mine);
        }

        /// <summary>An ore want that Haul can cover from stock gets the Haul and no Mine trip: the
        /// Haul ranking holds no Mine options since FAC-395 slice 2b, and a demand row alone never
        /// dispatches a drill. The hub's own stock-less want reads to the planner as unmet input,
        /// but the deposit's passive mint covers that rate, so no order opens either.</summary>
        [Fact]
        public void AnOreWantHaulCanCover_GetsAHaul_AndNoMineTrip()
        {
            var world = MakeWorld();
            var st = new FactionState { Tag = "F" };

            SpatialNode ore = Node(world, "Ore", new Vec3(0, 0, 0));
            var deposit = new NodeDeposit();
            deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
            ore.Deposit = deposit;
            ore.Richness = 1.0;   // ore under it AND measured: the mint and the drill read this
            ore.ShipStock["Drill"] = 4;   // plenty, so contention is not what decides this

            // One destination wants ore, and a sibling node is holding more than enough of it.
            SpatialNode smelter = Node(world, "Smelter", new Vec3(1, 0, 0));
            SpatialNode stockpile = Node(world, "Stockpile", new Vec3(2, 0, 0));
            stockpile.Inventory["Ore:Iron"] = 500_000;
            stockpile.ShipStock["Drill"] = 1;
            Post(world, "Ore:Iron", smelter.Id, 1_000, DemandClass.RecipeInput);

            Tick(world, st);

            Assert.Contains(st.PendingFleets, f => f.Kind == ActionKind.Haul);
            Assert.DoesNotContain(st.PendingFleets, f => f.Kind == ActionKind.Mine);
        }
    }
}
