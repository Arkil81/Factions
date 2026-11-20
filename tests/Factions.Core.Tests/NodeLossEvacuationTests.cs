using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// What a node loss does to what stands on it (<see cref="HullRelocation.Evacuate"/>): on abandon
    /// the loser's hulls fly off, whole ones to the nearest owned node and damaged ones to a yard;
    /// on a claim or a conquest everything passes to the taker and the line says what.
    /// See docs/economy-territory.md, "What a lost node takes with it".
    /// </summary>
    public class NodeLossEvacuationTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        /// <summary>A prefab with no thrust at all fails every route's dry-TWR gate; every other
        /// answer comes from the armed catalogue.</summary>
        private sealed class Grounded : IPrefabCatalogue
        {
            private readonly IPrefabCatalogue _inner;
            private readonly string _grounded;
            public Grounded(IPrefabCatalogue inner, string grounded) { _inner = inner; _grounded = grounded; }
            public long CargoLiters(string p) { return _inner.CargoLiters(p); }
            public IReadOnlyDictionary<string, long> GasTankLiters(string p) { return _inner.GasTankLiters(p); }
            public long Cost(string p) { return _inner.Cost(p); }
            public string FactionHint(string p) { return _inner.FactionHint(p); }
            public IReadOnlyList<WeaponBand> WeaponBands(string p) { return _inner.WeaponBands(p); }
            public string GridSize(string p) { return _inner.GridSize(p); }
            public IReadOnlyList<string> GetNamesForRole(string r, string h) { return _inner.GetNamesForRole(r, h); }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string p) { return _inner.ThrustBands(p); }
            public double DryMassKg(string p) { return p == _grounded ? 50000 : 0; }
            public double DrillYieldScore(string p) { return _inner.DrillYieldScore(p); }
        }

        private static WorldModel World()
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = T0;
            w.PrefabCatalogue = new ArmedYard.ArmedCatalogue()
                .Add("Gunship", 400, "Large")
                .Add("Fighter", 100, "Small");
            var recipes = new ArmedYard.CostRecipes();
            recipes.CostByPrefab["Gunship"] = 400;
            recipes.CostByPrefab["Fighter"] = 100;
            w.Recipes = recipes;
            return w;
        }

        private static SpatialNode Node(WorldModel w, string id, double x, string owner, bool yard)
        {
            SpatialNode n = w.Graph.Add(new Vec3(x, 0, 0), 1.0, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 1.0;
            if (yard) n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return n;
        }

        private static DamagedHull Queue(SpatialNode node, string prefab, double integrity)
        {
            var hull = new DamagedHull { PrefabId = prefab, Integrity = integrity, QueuedUtc = T0.AddHours(-5) };
            hull.Bill["Comp:Steel"] = (long)Math.Ceiling(100 * (1 - integrity));
            node.DamagedHulls.Add(hull);
            return hull;
        }

        private static List<string> Arrive(WorldModel w, FactionState st)
        {
            var log = new List<string>();
            w.UtcNow = st.PendingFleets.Max(f => f.EstimatedArrivalUtc).AddSeconds(1);
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, w, w.Tuning,
                                                 new SeededRandom(7), log, null);
            return log;
        }

        private static int Stock(SpatialNode n, string prefab)
        {
            int count;
            return n.ShipStock.TryGetValue(prefab, out count) ? count : 0;
        }

        /// <summary>LOST at 0 is abandoned. F still owns DEPOT at 10 (no yard), a paused yard at 50
        /// and YARD at 100; a foreign yard at 2 is the closest and wrong.</summary>
        private static WorldModel Estate(out SpatialNode lost, out SpatialNode depot, out SpatialNode yard)
        {
            WorldModel w = World();
            lost = Node(w, "LOST", 0, "F", false);
            depot = Node(w, "DEPOT", 10, "F", false);
            yard = Node(w, "YARD", 100, "F", true);
            Node(w, "PAUSED", 50, "F", true).ShipyardPaused = true;
            Node(w, "FOREIGN", 2, "G", true);
            return w;
        }

        // ---- abandon: the hulls fly ---------------------------------------------------------------

        [Fact]
        public void Abandon_FliesWholeHullsToTheNearestOwnedNode_AndDamagedOnesToAYard_CreditingNothingAtOnce()
        {
            SpatialNode lost, depot, yard;
            WorldModel w = Estate(out lost, out depot, out yard);
            var st = new FactionState { Tag = "F" };
            lost.ShipStock["Gunship"] = 3;
            lost.ListedForSale["Gunship"] = 1;
            Queue(lost, "Fighter", 0.4);
            Queue(lost, "Fighter", 0.4).Collected["Comp:Steel"] = 20;
            lost.BuildQueue.Add(new BuildQueueEntry { Role = "Patrol", PrefabId = "Gunship" });

            w.Withdraw(st, "LOST");

            // Nothing is on the node any more, and nothing has arrived anywhere yet.
            Assert.Equal(NeutralFaction.Tag, lost.OwnerFactionTag);
            Assert.Empty(lost.ShipStock);
            Assert.Empty(lost.ListedForSale);
            Assert.Empty(lost.DamagedHulls);
            Assert.Equal(0, Stock(depot, "Gunship"));
            Assert.Empty(yard.DamagedHulls);
            // The build queue stays for the next owner; the absorbed parts stay as loot.
            Assert.Single(lost.BuildQueue);
            Assert.Equal(20L, lost.Inventory["Comp:Steel"]);

            Assert.Equal(2, st.PendingFleets.Count);
            PendingFleetAction whole = st.PendingFleets.Single(f => f.ShipPrefabId == "Gunship");
            Assert.Equal(ActionKind.Haul, whole.Kind);
            Assert.Equal(HullRelocation.EvacuateRole, whole.RelocationRole);
            Assert.Equal("DEPOT", whole.TargetNodeId);
            Assert.Equal("DEPOT", whole.ReturnNodeId);
            Assert.Equal(3, whole.ConvoySize);
            Assert.Equal(1.0, whole.IntegrityOf(0));
            Assert.True(whole.EstimatedArrivalUtc > w.UtcNow);

            PendingFleetAction damaged = st.PendingFleets.Single(f => f.ShipPrefabId == "Fighter");
            Assert.Equal(HullRelocation.EvacuateRole, damaged.RelocationRole);
            Assert.Equal("YARD", damaged.TargetNodeId);
            Assert.Equal(2, damaged.ConvoySize);
            Assert.Equal(0.4, damaged.IntegrityOf(0));
            Assert.True(damaged.EstimatedArrivalUtc > w.UtcNow);

            // One record per fleet, written as the host writes it.
            List<string> lines = w.ShipEvacuations.Select(e => e.LogLine()).ToList();
            Assert.Contains("SHIP_EVACUATED F prefab=Gunship count=3 from=LOST to=DEPOT why=nearest_owned fleet="
                            + whole.LogId + " eta=" + whole.EstimatedArrivalUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture), lines);
            Assert.Contains("SHIP_EVACUATED F prefab=Fighter count=2 from=LOST to=YARD why=yard integrity=0.40 fleet="
                            + damaged.LogId + " eta=" + damaged.EstimatedArrivalUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                            + " parts_left=20", lines);

            List<string> log = Arrive(w, st);

            Assert.Equal(3, Stock(depot, "Gunship"));
            Assert.Equal(2, yard.DamagedHulls.Count);
            Assert.All(yard.DamagedHulls, h => Assert.Equal(0.4, h.Integrity));
            Assert.Equal(0, Stock(yard, "Fighter"));   // damaged hulls go to repair, not into the pool
            Assert.Contains(log, l => l.StartsWith("SHIP_RELOCATE_ARRIVED " + whole.LogId + " F role=Evacuate -> DEPOT",
                                                   StringComparison.Ordinal));
        }

        [Fact]
        public void Abandon_DamagedHullWithNoReachableYard_LandsAtAnOwnedNode_AndTheRepairSweepMovesItOn()
        {
            WorldModel w = World();
            SpatialNode lost = Node(w, "LOST", 0, "F", false);
            SpatialNode depot = Node(w, "DEPOT", 10, "F", false);
            SpatialNode yard = Node(w, "YARD", 100, "F", true);
            yard.ShipyardPaused = true;   // the only yard is not working
            var st = new FactionState { Tag = "F" };
            Queue(lost, "Fighter", 0.3);

            w.Withdraw(st, "LOST");

            PendingFleetAction fleet = Assert.Single(st.PendingFleets);
            Assert.Equal("DEPOT", fleet.TargetNodeId);
            Assert.Equal("no_reachable_yard", Assert.Single(w.ShipEvacuations).Reason);

            Arrive(w, st);
            DamagedHull landed = Assert.Single(depot.DamagedHulls);
            Assert.Equal(0.3, landed.Integrity);

            // A yard comes back: the ordinary sweep flies the hull to it.
            yard.ShipyardPaused = false;
            var log = new List<string>();
            Assert.Equal(1, RepairTransfer.Dispatch(st, w, w.Tuning, log));
            Assert.Empty(depot.DamagedHulls);
            Assert.Contains(log, l => l.StartsWith("F REPAIR_REROUTED", StringComparison.Ordinal)
                                      && l.Contains(" from=DEPOT to=YARD "));
        }

        [Fact]
        public void Abandon_WithNothingReachable_StrandsBothKinds_AndSaysSo()
        {
            WorldModel w = World();
            w.PrefabCatalogue = new Grounded(w.PrefabCatalogue, "Gunship");
            SpatialNode lost = Node(w, "LOST", 0, "F", false);
            Node(w, "DEPOT", 10, "F", false);
            var st = new FactionState { Tag = "F" };
            lost.ShipStock["Gunship"] = 2;          // cannot lift: stays
            Queue(lost, "Gunship", 0.5);            // cannot lift: stays
            lost.ShipStock["Fighter"] = 1;          // flies

            w.Withdraw(st, "LOST");

            Assert.Equal(2, Stock(lost, "Gunship"));
            DamagedHull left = Assert.Single(lost.DamagedHulls);
            Assert.Equal("Gunship", left.PrefabId);
            PendingFleetAction flew = Assert.Single(st.PendingFleets);
            Assert.Equal("Fighter", flew.ShipPrefabId);

            List<string> lines = w.ShipEvacuations.Select(e => e.LogLine()).ToList();
            Assert.Contains("SHIP_STRANDED_ON_ABANDON F prefab=Gunship count=2 node=LOST why=no_owned_node", lines);
            Assert.Contains("SHIP_STRANDED_ON_ABANDON F prefab=Gunship count=1 node=LOST why=no_owned_node integrity=0.50", lines);
        }

        [Fact]
        public void Abandon_OfTheLastNode_StrandsEverything()
        {
            WorldModel w = World();
            SpatialNode lost = Node(w, "LOST", 0, "F", true);
            var st = new FactionState { Tag = "F" };
            lost.ShipStock["Gunship"] = 1;
            Queue(lost, "Fighter", 0.2);

            w.Withdraw(st, "LOST");

            Assert.Empty(st.PendingFleets);
            Assert.Equal(1, Stock(lost, "Gunship"));
            Assert.Single(lost.DamagedHulls);
            Assert.Equal(2, w.ShipEvacuations.Count);
            Assert.All(w.ShipEvacuations, e => Assert.Equal("no_owned_node", e.Reason));
        }

        [Fact]
        public void HoldDecay_FliesTheHullsOnTheFactionsFleetList()
        {
            SpatialNode lost, depot, yard;
            WorldModel w = Estate(out lost, out depot, out yard);
            var st = new FactionState { Tag = "F" };
            lost.HoldStrength = 0.1;   // the others hold 1.0 and survive one 0.5 step
            lost.ShipStock["Gunship"] = 1;
            Queue(lost, "Fighter", 0.6);

            w.DecayInfluence(0.5, 0.5, new List<FactionState> { st });

            Assert.Equal(NeutralFaction.Tag, lost.OwnerFactionTag);
            Assert.Equal(2, st.PendingFleets.Count);
            Assert.Empty(lost.ShipStock);
            Assert.Empty(lost.DamagedHulls);
            Assert.Equal(0, Stock(depot, "Gunship"));
        }

        /// <summary>The insolvency shed abandons a node inside the income pass: its hulls fly, and
        /// their records are there to log, when the pass that shed it returns -- not at the next decay.</summary>
        [Fact]
        public void InsolvencyShed_FliesTheHullsInsideTheIncomePass()
        {
            WorldModel w = World();
            w.Tuning.CentralBankGrantAmount = 0;
            SpatialNode keep = w.Graph.Add(new Vec3(0, 0, 0), 0.05, "N001");       // a mild drain
            SpatialNode drain = w.Graph.Add(new Vec3(5000, 0, 0), 0.0, "N002");    // the worst drain
            foreach (SpatialNode n in new[] { keep, drain })
            {
                n.OwnerFactionTag = "F";
                n.HoldStrength = 0.5;
                n.StableStationId = n.Id.GetHashCode();
            }
            drain.ShipStock["Fighter"] = 2;
            Queue(drain, "Fighter", 0.5);
            var st = new FactionState { Tag = "F" };
            var states = new List<FactionState> { st };
            w.SetTreasury("F", 2000);

            for (int i = 0; i < 30 && drain.OwnerFactionTag == "F"; i++)
            {
                w.AccrueIncome(states);
                if (drain.OwnerFactionTag == "F") Assert.Empty(w.ShipEvacuations);
            }

            Assert.Equal(NeutralFaction.Tag, drain.OwnerFactionTag);
            Assert.Equal(2, w.ShipEvacuations.Count);
            Assert.All(w.ShipEvacuations, e => Assert.Equal("N001", e.ToNodeId));
            Assert.Equal(2, st.PendingFleets.Count);
            Assert.Empty(drain.ShipStock);
            Assert.Empty(drain.DamagedHulls);
        }

        [Fact]
        public void Withdraw_WithoutAFactionState_LeavesTheHullsAndSaysWhy()
        {
            SpatialNode lost, depot, yard;
            WorldModel w = Estate(out lost, out depot, out yard);
            lost.ShipStock["Gunship"] = 1;
            Queue(lost, "Fighter", 0.4);

            w.Withdraw("F", "LOST");

            Assert.Equal(1, Stock(lost, "Gunship"));
            Assert.Single(lost.DamagedHulls);
            Assert.Equal(0, Stock(depot, "Gunship"));
            Assert.All(w.ShipEvacuations, e => Assert.Equal("no_faction_state", e.Reason));
            Assert.Equal(2, w.ShipEvacuations.Count);
        }

        // ---- in flight: interceptable like any Haul ------------------------------------------------

        [Fact]
        public void AnEvacuationShotDownInFlight_CreditsNothing()
        {
            SpatialNode lost, depot, yard;
            WorldModel w = Estate(out lost, out depot, out yard);
            var st = new FactionState { Tag = "F" };
            lost.ShipStock["Gunship"] = 2;
            w.Withdraw(st, "LOST");

            PendingFleetAction f = st.PendingFleets[0];
            f.ForcedRatio = 0.0;
            f.ForcedRatioCause = "grid_destroyed";
            st.PendingFleets[0] = f;
            List<string> log = Arrive(w, st);

            Assert.Equal(0, Stock(depot, "Gunship"));
            Assert.Equal(0, Stock(lost, "Gunship"));
            Assert.Contains(log, l => l.StartsWith("HAUL_SHIP_LOST " + f.LogId, StringComparison.Ordinal));
        }

        /// <summary>A destination lost while the evacuation is in the air: the hulls land at the
        /// nearest node the faction still owns, the same terminal every Haul takes.</summary>
        [Fact]
        public void AnEvacuationWhoseDestinationIsLostInFlight_LandsAtTheNextOwnedNode()
        {
            SpatialNode lost, depot, yard;
            WorldModel w = Estate(out lost, out depot, out yard);
            var st = new FactionState { Tag = "F" };
            lost.ShipStock["Gunship"] = 2;
            w.Withdraw(st, "LOST");
            Assert.Equal("DEPOT", st.PendingFleets[0].TargetNodeId);

            w.Withdraw(st, "DEPOT");
            Arrive(w, st);

            Assert.Equal(0, Stock(depot, "Gunship"));
            Assert.Equal(2, Stock(w.Graph.Nodes["PAUSED"], "Gunship"));
        }

        // ---- claim and conquest: everything passes to the taker ------------------------------------

        [Fact]
        public void Conquest_HandsWholeAndDamagedHullsBuildsAndInventoryToTheConqueror()
        {
            WorldModel w = World();
            SpatialNode n = Node(w, "N", 0, "F", true);
            Node(w, "F2", 10, "F", true);
            n.ShipStock["Gunship"] = 4;
            Queue(n, "Fighter", 0.3);
            Queue(n, "Fighter", 0.7);
            n.BuildQueue.Add(new BuildQueueEntry { Role = "Patrol", PrefabId = "Gunship" });
            n.Inventory["Ore:Iron"] = 500;

            Assert.Equal("F", w.CaptureNode("G", "N"));

            Assert.Equal("G", n.OwnerFactionTag);
            Assert.Equal(4, Stock(n, "Gunship"));
            Assert.Equal(2, n.DamagedHulls.Count);
            Assert.Single(n.BuildQueue);
            Assert.Equal(500L, n.Inventory["Ore:Iron"]);
            Assert.Empty(w.ShipEvacuations);   // a conquest flies nothing
            Assert.Equal(" stock=4 damaged=2 builds=1 items=500", NodeHoldings.Of(n).Suffix());
        }

        [Fact]
        public void ClaimOfADerelict_HandsWhatWasLeftBehindToTheClaimer()
        {
            WorldModel w = World();
            SpatialNode n = Node(w, "N", 0, NeutralFaction.Tag, false);
            n.ShipStock["Gunship"] = 1;
            Queue(n, "Fighter", 0.3);

            Assert.True(w.TryClaim("G", "N", false));

            Assert.Equal("G", n.OwnerFactionTag);
            Assert.Equal(1, Stock(n, "Gunship"));
            Assert.Single(n.DamagedHulls);
            Assert.Equal(" stock=1 damaged=1 builds=0 items=0", NodeHoldings.Of(n).Suffix());
        }

        // ---- the sweep says what it destroyed ------------------------------------------------------

        [Fact]
        public void RemoveNode_CountsTheDamagedHullsItDestroyed()
        {
            WorldModel w = World();
            SpatialNode n = Node(w, "B", 0, NeutralFaction.Tag, false);
            n.ShipStock["Gunship"] = 2;
            Queue(n, "Fighter", 0.3);
            Queue(n, "Fighter", 0.4);
            Queue(n, "Gunship", 0.9);

            RemovedNodeContents destroyed = w.RemoveNode("B");

            Assert.Equal(2, destroyed.Hulls);
            Assert.Equal(3, destroyed.DamagedHulls);
        }
    }
}
