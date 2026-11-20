using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A damaged hull queued where no yard can repair it flies to the nearest owned yard that can
    /// (<see cref="RepairTransfer"/>). See docs/fleet-events.md, "Hull integrity".
    /// </summary>
    public class RepairTransferTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        /// <summary>DryMassKg for one prefab, so it fails every route's dry-TWR gate (it has no
        /// thrust); every other answer comes from the armed catalogue.</summary>
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

        private static ArmedYard.ArmedCatalogue Catalogue()
        {
            return new ArmedYard.ArmedCatalogue()
                .Add("Gunship", 400, "Large")
                .Add("Fighter", 100, "Small");
        }

        private static ArmedYard.CostRecipes Recipes()
        {
            var r = new ArmedYard.CostRecipes();
            r.CostByPrefab["Gunship"] = 400;
            r.CostByPrefab["Fighter"] = 100;
            return r;
        }

        private static SpatialNode Node(WorldModel w, string id, double x, string owner, bool yard)
        {
            SpatialNode n = w.Graph.Add(new Vec3(x, 0, 0), 1.0, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 1.0;
            if (yard) n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return n;
        }

        /// <summary>F's depot at 0 with no yard; F's yards at 100 (NEAR) and 300 (FAR); a paused F
        /// yard at 50 and a foreign yard at 20, both closer and both wrong.</summary>
        private static WorldModel Estate(out SpatialNode depot, out SpatialNode near, out SpatialNode far)
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = T0;
            w.PrefabCatalogue = Catalogue();
            w.Recipes = Recipes();
            depot = Node(w, "DEPOT", 0, "F", false);
            near = Node(w, "NEAR", 100, "F", true);
            far = Node(w, "FAR", 300, "F", true);
            Node(w, "PAUSED", 50, "F", true).ShipyardPaused = true;
            Node(w, "FOREIGN", 20, "G", true);
            return w;
        }

        private static DamagedHull Queue(SpatialNode node, string prefab, double integrity)
        {
            var hull = new DamagedHull { PrefabId = prefab, Integrity = integrity, QueuedUtc = T0.AddHours(-5) };
            hull.Bill["Comp:Steel"] = (long)Math.Ceiling(400 * (1 - integrity));
            node.DamagedHulls.Add(hull);
            return hull;
        }

        private static void Arrive(WorldModel w, FactionState st, List<string> log)
        {
            w.UtcNow = st.PendingFleets.Max(f => f.EstimatedArrivalUtc).AddSeconds(1);
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, w, w.Tuning,
                                                 new SeededRandom(7), log, null);
        }

        /// <summary>The ticket's case end to end: a fleet comes home damaged to a node with no yard,
        /// the shipyard pass flies the hull to the nearest owned yard that is working, and it queues
        /// for repair there on arrival.</summary>
        [Fact]
        public void DamagedHullLandingAtAYardlessNode_EndsUpQueuedAtTheNearestOwnedYard()
        {
            SpatialNode depot, near, far;
            WorldModel w = Estate(out depot, out near, out far);
            var st = new FactionState { Tag = "F" };
            var log = new List<string>();
            HullDamage.Land(w, depot, depot.Id, "Gunship", 2, 0.4, "[RET001]", "F", log);
            Assert.Contains(log, l => l.StartsWith("REPAIR_QUEUED F fleet=[RET001] prefab=Gunship node=DEPOT", StringComparison.Ordinal)
                                      && l.EndsWith(" yard=none", StringComparison.Ordinal));

            new ShipyardManager().Tick(new List<FactionState> { st }, w, w.Tuning, log);

            Assert.Empty(depot.DamagedHulls);
            PendingFleetAction transfer = Assert.Single(st.PendingFleets);
            Assert.Equal(ActionKind.Haul, transfer.Kind);
            Assert.Equal(RepairTransfer.Role, transfer.RelocationRole);
            Assert.Equal("NEAR", transfer.TargetNodeId);
            Assert.Equal("Gunship", transfer.ShipPrefabId);
            Assert.Equal(2, transfer.ConvoySize);
            Assert.Equal(0.4, transfer.IntegrityOf(0));
            Assert.True(transfer.EstimatedArrivalUtc > w.UtcNow);   // it flies; it is not teleported
            Assert.Contains(log, l => l.StartsWith("F REPAIR_REROUTED fleet=" + transfer.LogId
                                                   + " prefab=Gunship count=2 integrity=0.40 from=DEPOT to=NEAR km=100",
                                                   StringComparison.Ordinal));

            Arrive(w, st, log);

            Assert.Equal(2, near.DamagedHulls.Count);
            Assert.All(near.DamagedHulls, h => Assert.Equal(0.4, h.Integrity));
            Assert.Equal(240L, near.DamagedHulls[0].Bill["Comp:Steel"]);   // billed afresh at the yard
            Assert.Empty(depot.DamagedHulls);
            Assert.Empty(far.DamagedHulls);
            Assert.Equal(2, log.Count(l => l.StartsWith("REPAIR_QUEUED F fleet=" + transfer.LogId + " prefab=Gunship node=NEAR",
                                                        StringComparison.Ordinal)
                                          && !l.Contains("yard=none")));
            int stock;
            near.ShipStock.TryGetValue("Gunship", out stock);
            Assert.Equal(0, stock);   // damaged hulls go to repair, not into the pool
        }

        /// <summary>A queue persisted before the rule existed, at a node that has since lost its yard:
        /// the hulls leave, and the parts they had absorbed stay where they physically are.</summary>
        [Fact]
        public void AQueueAlreadyAtAYardlessNode_IsMoved_AndItsAbsorbedPartsStay()
        {
            SpatialNode depot, near, far;
            WorldModel w = Estate(out depot, out near, out far);
            DamagedHull a = Queue(depot, "Gunship", 0.5);
            a.Collected["Comp:Steel"] = 150;
            Queue(depot, "Gunship", 0.5);
            Queue(depot, "Fighter", 0.3);
            var st = new FactionState { Tag = "F" };
            var log = new List<string>();

            Assert.Equal(2, RepairTransfer.Dispatch(st, w, w.Tuning, log));

            Assert.Empty(depot.DamagedHulls);
            Assert.Equal(150L, depot.Inventory["Comp:Steel"]);
            PendingFleetAction gunships = st.PendingFleets.Single(f => f.ShipPrefabId == "Gunship");
            PendingFleetAction fighter = st.PendingFleets.Single(f => f.ShipPrefabId == "Fighter");
            Assert.Equal(2, gunships.ConvoySize);
            Assert.Equal(1, fighter.ConvoySize);
            Assert.Equal(0.3, fighter.IntegrityOf(0));
            Assert.Contains(log, l => l.StartsWith("F REPAIR_REROUTED fleet=" + gunships.LogId + " prefab=Gunship count=2", StringComparison.Ordinal)
                                      && l.EndsWith(" parts_returned=150", StringComparison.Ordinal));

            // Nothing is sent twice: the hulls are in flight, not queued.
            Assert.Equal(0, RepairTransfer.Dispatch(st, w, w.Tuning, log));
            Assert.Equal(2, st.PendingFleets.Count);

            Arrive(w, st, log);
            Assert.Equal(3, near.DamagedHulls.Count);
            Assert.Equal(2, near.DamagedHulls.Count(h => h.PrefabId == "Gunship" && h.Integrity == 0.5));
            Assert.All(near.DamagedHulls, h => Assert.Empty(h.Collected));
        }

        /// <summary>No owned, working yard anywhere: the queue stays where it is, visible, and moves
        /// once a yard exists.</summary>
        [Fact]
        public void WithNoYardInTheEstate_TheQueueStays_UntilOneExists()
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = T0;
            w.PrefabCatalogue = Catalogue();
            w.Recipes = Recipes();
            SpatialNode depot = Node(w, "DEPOT", 0, "F", false);
            SpatialNode later = Node(w, "LATER", 100, "F", false);
            Node(w, "PAUSED", 50, "F", true).ShipyardPaused = true;
            Node(w, "FOREIGN", 20, "G", true);
            Queue(depot, "Gunship", 0.5);
            var st = new FactionState { Tag = "F" };
            var log = new List<string>();

            Assert.Equal(0, RepairTransfer.Dispatch(st, w, w.Tuning, log));
            Assert.Single(depot.DamagedHulls);
            Assert.Empty(st.PendingFleets);
            Assert.Empty(log);
            Assert.Equal(RepairState.NoShipyard, HullDamage.StateOf(depot, depot.DamagedHulls[0]));

            later.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            Assert.Equal(1, RepairTransfer.Dispatch(st, w, w.Tuning, log));
            Assert.Empty(depot.DamagedHulls);
            Assert.Equal("LATER", st.PendingFleets[0].TargetNodeId);
        }

        /// <summary>A yard the hull cannot reach is not a destination: the Gunship waits, the Fighter
        /// goes.</summary>
        [Fact]
        public void AHullThatCannotReachAnyYard_Waits()
        {
            SpatialNode depot, near, far;
            WorldModel w = Estate(out depot, out near, out far);
            w.PrefabCatalogue = new Grounded(Catalogue(), "Gunship");
            Queue(depot, "Gunship", 0.5);
            Queue(depot, "Fighter", 0.5);
            var st = new FactionState { Tag = "F" };

            Assert.Equal(1, RepairTransfer.Dispatch(st, w, w.Tuning, new List<string>()));
            Assert.Equal("Gunship", Assert.Single(depot.DamagedHulls).PrefabId);
            Assert.Equal("Fighter", Assert.Single(st.PendingFleets).ShipPrefabId);
        }

        /// <summary>A node with a yard keeps its queue, and a queue on a node the faction does not own
        /// is not the faction's to move.</summary>
        [Fact]
        public void QueuesAtAYard_OrOnAnotherFactionsNode_AreLeftAlone()
        {
            SpatialNode depot, near, far;
            WorldModel w = Estate(out depot, out near, out far);
            Queue(near, "Gunship", 0.5);
            SpatialNode foreignDepot = Node(w, "GDEPOT", 10, "G", false);
            Queue(foreignDepot, "Gunship", 0.5);
            var st = new FactionState { Tag = "F" };

            Assert.Equal(0, RepairTransfer.Dispatch(st, w, w.Tuning, new List<string>()));
            Assert.Single(near.DamagedHulls);
            Assert.Single(foreignDepot.DamagedHulls);
        }

        /// <summary>While they fly, the hulls are still the faction's: the fleet census counts them as
        /// flying, and a doctrine line scoped to the yard counts them in its Have.</summary>
        [Fact]
        public void HullsInTransfer_AreCountedAsFlying()
        {
            SpatialNode depot, near, far;
            WorldModel w = Estate(out depot, out near, out far);
            Queue(depot, "Gunship", 0.5);
            Queue(depot, "Gunship", 0.5);
            var st = new FactionState { Tag = "F" };
            RepairTransfer.Dispatch(st, w, w.Tuning, new List<string>());

            FleetDoctrineReport report = FleetDoctrineReport.Build(w, st);
            FleetCensusRow row = report.Census.Single(r => r.PrefabId == "Gunship");
            Assert.Equal(2L, row.Flying);
            Assert.Equal(0, row.RepairWaiting);

            // Scoped by where the hulls come home to: the yard, not the depot they left.
            Assert.Equal(2L, ShipyardManager.CountDoctrineHullsInFlight(st, new DoctrineLine { PrefabId = "Gunship", ScopeNodeId = "NEAR" }));
            Assert.Equal(0L, ShipyardManager.CountDoctrineHullsInFlight(st, new DoctrineLine { PrefabId = "Gunship", ScopeNodeId = "DEPOT" }));
        }

        /// <summary>One fleet per destination, prefab and integrity, at most
        /// <see cref="RepairTransfer.MaxTransfersPerPass"/> a pass; the rest go on the next.</summary>
        [Fact]
        public void TransfersPerPass_AreCapped_AndTheRestGoNextPass()
        {
            SpatialNode depot, near, far;
            WorldModel w = Estate(out depot, out near, out far);
            int distinct = RepairTransfer.MaxTransfersPerPass + 3;
            for (int i = 0; i < distinct; i++) Queue(depot, "Gunship", 0.30 + i * 0.01);
            var st = new FactionState { Tag = "F" };

            Assert.Equal(RepairTransfer.MaxTransfersPerPass, RepairTransfer.Dispatch(st, w, w.Tuning, new List<string>()));
            Assert.Equal(3, depot.DamagedHulls.Count);
            Assert.Equal(3, RepairTransfer.Dispatch(st, w, w.Tuning, new List<string>()));
            Assert.Empty(depot.DamagedHulls);
            Assert.Equal(distinct, st.PendingFleets.Select(f => f.FleetId).Distinct().Count());
        }
    }
}
