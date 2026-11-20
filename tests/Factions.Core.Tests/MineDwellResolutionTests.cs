using System;
using System.Collections.Generic;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>Slice 3 coverage: the Mine dwell mechanic tested in isolation, hand-constructing a
    /// <see cref="PendingFleetAction"/> with <see cref="FleetStopKind.Mine"/> directly rather than
    /// going through any dispatcher (that's Slice 4's concern) -- proves the dwell-resolution
    /// mechanism itself is correct before anything produces a Mine-kind stop in practice.</summary>
    public class MineDwellResolutionTests
    {
        private sealed class FixedVolumeCatalogue : IItemVolumeCatalogue
        {
            private readonly Dictionary<string, double> _byKey = new Dictionary<string, double>();
            public FixedVolumeCatalogue Set(string key, double litersPerUnit) { _byKey[key] = litersPerUnit; return this; }
            public double VolumeLitersPerUnit(string invKey)
            {
                double v;
                return _byKey.TryGetValue(invKey, out v) ? v : 0;
            }
        }

        private static WorldModel MakeWorld()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return world;
        }

        private static SpatialNode AddNode(WorldModel world, string id, string tag = "F")
        {
            return AddNode(world, id, new Vec3(0, 0, 0), tag);
        }

        private static SpatialNode AddNode(WorldModel world, string id, Vec3 pos, string tag = "F")
        {
            SpatialNode n = world.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = 1.0;
            return n;
        }

        [Fact]
        public void MineDwell_Resolves_SplitsByWeight_LoadsCargo_AndSetsCooldownDeadline()
        {
            var world = MakeWorld();
            SpatialNode source = AddNode(world, "Ore1");
            AddNode(world, "Dest1");
            var tuning = TestSupport.Tuning();
            tuning.MineCooldownMultiplier = 10.0;
            var volumes = new FixedVolumeCatalogue().Set("Ore:Iron", 1.0).Set("Ore:Nickel", 1.0);

            var st = new FactionState { Tag = "F" };
            DateTime departed = world.UtcNow.AddSeconds(-120);
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F", OriginNodeId = "Ore1", TargetNodeId = "Ore1",
                OriginPosKm = source.Position, TargetPosKm = source.Position,
                DepartedUtc = departed, EstimatedArrivalUtc = world.UtcNow,
                CurrentStopKind = FleetStopKind.Mine,
                MineWeights = new Dictionary<string, double> { ["Ore:Iron"] = 0.6, ["Ore:Nickel"] = 0.4 },
                MineTargetLiters = 1000,
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "Dest1", Kind = FleetStopKind.Service } },
                CargoCapacityLiters = 1_000_000,
                FleetId = "abc123",
                LastResolvedSegment = -1,
            });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null, null, null, volumes);

            // Minted into the node, then immediately loaded onto the ship in the same resolution step
            // (mirrors Pickup's mint-then-load shape) -- node inventory nets back to 0, the ship's
            // Cargo carries it forward toward the queued Service delivery leg.
            Assert.False(source.Inventory.ContainsKey("Ore:Iron") && source.Inventory["Ore:Iron"] != 0);
            Assert.Equal(world.UtcNow, source.LastMinedUtc);
            Assert.Equal(world.UtcNow.AddSeconds(1200), source.MineCooldownUntilUtc); // 120s dwell * 10x multiplier

            PendingFleetAction fleet = st.PendingFleets[0];
            Assert.Equal(FleetStopKind.Service, fleet.CurrentStopKind); // advanced past the Mine leg
            Assert.Null(fleet.MineWeights);
            Assert.Equal(0, fleet.MineTargetLiters);
            Assert.Equal(600, fleet.Cargo["Ore:Iron"]);   // 1000L * 0.6 / 1.0 L/unit
            Assert.Equal(400, fleet.Cargo["Ore:Nickel"]); // 1000L * 0.4 / 1.0 L/unit
        }

        [Fact]
        public void MineDwell_ForcedRatio_ScalesExtractedQuantities()
        {
            var world = MakeWorld();
            SpatialNode source = AddNode(world, "Ore1");
            AddNode(world, "Dest1");
            var tuning = TestSupport.Tuning();
            var volumes = new FixedVolumeCatalogue().Set("Ore:Iron", 1.0);

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F", OriginNodeId = "Ore1", TargetNodeId = "Ore1",
                OriginPosKm = source.Position, TargetPosKm = source.Position,
                DepartedUtc = world.UtcNow.AddSeconds(-60), EstimatedArrivalUtc = world.UtcNow,
                CurrentStopKind = FleetStopKind.Mine,
                MineWeights = new Dictionary<string, double> { ["Ore:Iron"] = 1.0 },
                MineTargetLiters = 1000,
                ForcedRatio = 0.5, // e.g. a mid-dwell interception cut the mission short
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "Dest1", Kind = FleetStopKind.Service } },
                CargoCapacityLiters = 1_000_000,
                FleetId = "def456",
                LastResolvedSegment = -1,
            });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null, null, null, volumes);

            // 1000L * 1.0 weight * 0.5 ratio / 1.0 L/unit, loaded onto the ship's Cargo.
            Assert.Equal(500, st.PendingFleets[0].Cargo["Ore:Iron"]);
        }

        [Fact]
        public void MineDwell_LocalTopUp_SourceEqualsDest_CreditsNodeInventory()
        {
            // Source == destination (a local top-up, no travel) -- the Mine dwell leg is followed by
            // a same-node Service leg, so the just-loaded cargo is credited right back to this node
            // instead of being carried elsewhere. Exercises the queued-Service-leg path end to end,
            // including the final ActionKind.Mine delivery switch case (unchanged by this slice).
            var world = MakeWorld();
            SpatialNode self = AddNode(world, "Self1");
            var tuning = TestSupport.Tuning();
            var volumes = new FixedVolumeCatalogue().Set("Ore:Iron", 1.0);

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F", OriginNodeId = "Self1", TargetNodeId = "Self1",
                OriginPosKm = self.Position, TargetPosKm = self.Position,
                DepartedUtc = world.UtcNow.AddSeconds(-60), EstimatedArrivalUtc = world.UtcNow,
                CurrentStopKind = FleetStopKind.Mine,
                MineWeights = new Dictionary<string, double> { ["Ore:Iron"] = 1.0 },
                MineTargetLiters = 500,
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "Self1", Kind = FleetStopKind.Service } },
                CargoCapacityLiters = 1_000_000,
                FleetId = "ghi789",
                LastResolvedSegment = -1,
            });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            // Two passes: the Mine dwell resolves and advances to the Service leg, which itself needs
            // one more ResolveArrivals call since AdvanceToNextStop only departs the new leg -- it
            // doesn't resolve it in the same call. The leg starts out on the ring the miner worked
            // (MineWander), not at the node, so it has a flight to make even back to its own deposit.
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null, null, null, volumes);
            world.UtcNow = st.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null, null, null, volumes);

            Assert.Equal(500, self.Inventory["Ore:Iron"]);
            Assert.Empty(st.PendingFleets); // fully resolved, no more stops -- pruned during cleanup
        }

        [Fact]
        public void MineDwell_ShipReturnsToMineSite_NotTheDeliveryDestination()
        {
            // Source != destination -- proves the drill ship comes home to the deposit it worked
            // (OriginNodeId, reassigned to the dwell site by AdvanceToNextStop when the Service leg
            // departed), not to wherever the cargo was delivered. Mine was missing from
            // FleetResolution's ship-lifecycle block entirely until a live player test surfaced it
            // (a captured drill ship never came back, draining ship stock to zero).
            var world = MakeWorld();
            SpatialNode source = AddNode(world, "Ore1");
            SpatialNode dest = AddNode(world, "Dest1");
            var tuning = TestSupport.Tuning();
            var volumes = new FixedVolumeCatalogue().Set("Ore:Iron", 1.0);

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F", OriginNodeId = "Ore1", TargetNodeId = "Ore1",
                OriginPosKm = source.Position, TargetPosKm = source.Position,
                DepartedUtc = world.UtcNow.AddSeconds(-60), EstimatedArrivalUtc = world.UtcNow,
                CurrentStopKind = FleetStopKind.Mine,
                MineWeights = new Dictionary<string, double> { ["Ore:Iron"] = 1.0 },
                MineTargetLiters = 500,
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "Dest1", Kind = FleetStopKind.Service } },
                CargoCapacityLiters = 1_000_000,
                ShipPrefabId = "MinerX",
                ConvoySize = 1,
                FleetId = "mine-return-1",
                LastResolvedSegment = -1,
            });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            // Two passes, same reason as the local-top-up test above: the dwell resolves and departs
            // the Service leg first; a second call, at its arrival, actually resolves it.
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null, null, null, volumes);
            world.UtcNow = st.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null, null, null, volumes);

            Assert.Equal(500, dest.Inventory["Ore:Iron"]); // cargo delivered to the real destination
            int stock;
            source.ShipStock.TryGetValue("MinerX", out stock);
            Assert.Equal(1, stock); // ship comes home to the mine site it worked...
            Assert.False(dest.ShipStock.ContainsKey("MinerX")); // ...not to the delivery destination
        }

        /// <summary>FAC-277: the same trip, with a clearance waypoint between the deposit and the
        /// delivery node. The final leg then departs from a coordinate, not a node, and the hull
        /// used to be neither credited anywhere nor recorded lost -- it simply stopped existing
        /// (live: 72 of 79 routed mine trips, 2026-09-02..06, killed every small faction's only
        /// miner). The drill still has to come home to the deposit it worked.</summary>
        [Fact]
        public void MineDwell_RoutedFinalLeg_ShipStillComesHomeToTheMineSite()
        {
            var world = MakeWorld();
            SpatialNode source = AddNode(world, "Ore1", new Vec3(0, 0, 0));
            SpatialNode dest = AddNode(world, "Dest1", new Vec3(120, 0, 0));
            var tuning = TestSupport.Tuning();
            var volumes = new FixedVolumeCatalogue().Set("Ore:Iron", 1.0);

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F", OriginNodeId = "Ore1", TargetNodeId = "Ore1",
                OriginPosKm = source.Position, TargetPosKm = source.Position,
                DepartedUtc = world.UtcNow.AddSeconds(-60), EstimatedArrivalUtc = world.UtcNow,
                CurrentStopKind = FleetStopKind.Mine,
                MineWeights = new Dictionary<string, double> { ["Ore:Iron"] = 1.0 },
                MineTargetLiters = 500,
                // What PlanRoute/NavigationStops produce for a leg blocked by a planet: a
                // coordinate-only stop queued ahead of the semantic Service stop.
                RemainingStops = new List<FleetStop>
                {
                    new FleetStop { Kind = FleetStopKind.Navigation, HasPosition = true, PositionKm = new Vec3(60, 90, 0) },
                    new FleetStop { NodeId = "Dest1", Kind = FleetStopKind.Service }
                },
                CargoCapacityLiters = 1_000_000,
                ShipPrefabId = "MinerX",
                ConvoySize = 1,
                FleetId = "mine-routed-1",
                LastResolvedSegment = -1,
            });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            // Dwell -> waypoint -> delivery: one ResolveArrivals per leg arrival, the clock moved to
            // each leg's own ETA (AdvanceToNextStop departs a leg, it never resolves it).
            for (int leg = 0; leg < 3 && st.PendingFleets.Count > 0; leg++)
            {
                world.UtcNow = st.PendingFleets[0].EstimatedArrivalUtc;
                PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null, null, null, volumes);
            }

            Assert.Equal(500, dest.Inventory["Ore:Iron"]);   // cargo still delivered
            int stock;
            source.ShipStock.TryGetValue("MinerX", out stock);
            Assert.Equal(1, stock);                          // and the drill is home at the deposit
            Assert.False(dest.ShipStock.ContainsKey("MinerX"));
            Assert.Contains(log, l => l.StartsWith("SHIP_RETURNED F prefab=MinerX node=Ore1"));
            Assert.DoesNotContain(log, l => l.StartsWith("SHIP_LOST"));
        }

        /// <summary>FAC-277, legacy shape: a fleet persisted before the return node was recorded
        /// carries no such field, so the terminal has to fall back to the dispatch origin.</summary>
        [Fact]
        public void MineTerminal_FleetPersistedWithoutAReturnNode_FallsBackToItsOrigin()
        {
            var world = MakeWorld();
            SpatialNode source = AddNode(world, "Ore1");
            AddNode(world, "Dest1");
            var tuning = TestSupport.Tuning();

            var legacyDoc = new BrainDoc
            {
                Tag = "F",
                PendingFleets = new List<PendingFleetDoc>
                {
                    new PendingFleetDoc
                    {
                        Kind = "Mine", ActorTag = "F", OriginNodeId = "Ore1", TargetNodeId = "Dest1",
                        CurrentStopKind = "Service", ShipPrefabId = "MinerX", ConvoySize = 1,
                        DepartedUtc = world.UtcNow.AddSeconds(-60), EstimatedArrivalUtc = world.UtcNow,
                        FleetId = "legacy1",
                    }
                }
            };
            FactionState st = FactionStore.ApplyBrainDoc(legacyDoc, new SpatialGraph());
            Assert.Null(st.PendingFleets[0].ReturnNodeId);   // the field simply is not in the file

            var log = new List<string>();
            var states = new List<FactionState> { st };
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null);

            int stock;
            source.ShipStock.TryGetValue("MinerX", out stock);
            Assert.Equal(1, stock);
            Assert.Contains(log, l => l.StartsWith("SHIP_RETURNED F prefab=MinerX node=Ore1"));
        }

        /// <summary>FAC-277: nothing that survives its trip may disappear quietly. With no return
        /// node resolvable at all, the hull is still recorded as lost rather than deleted in
        /// silence.</summary>
        [Fact]
        public void MineTerminal_NoResolvableReturnNode_LogsTheLossInsteadOfDroppingTheHull()
        {
            var world = MakeWorld();
            // Owned by someone else, so "no resolvable return node" is genuinely the case under test:
            // since FAC-278 the terminal falls back to the nearest node the ACTOR owns before giving
            // up, and an F-owned Dest1 would now be a perfectly good landing.
            AddNode(world, "Dest1", "OTHER");
            var tuning = TestSupport.Tuning();

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F", OriginNodeId = "Gone1", TargetNodeId = "Dest1",
                DepartedUtc = world.UtcNow.AddSeconds(-60), EstimatedArrivalUtc = world.UtcNow,
                CurrentStopKind = FleetStopKind.Service,
                ShipPrefabId = "MinerX", ConvoySize = 1,
                FleetId = "gone1", LastResolvedSegment = -1,
            });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null);

            Assert.Contains(log, l => l.StartsWith("SHIP_LOST") && l.Contains("why=return_node_missing"));
            Assert.Contains(world.PendingEvents, e => e.Kind == FactionEventKind.ShipLost && e.ActorTag == "F");
        }

        /// <summary>The same fleet over ground the actor still owns: the deleted origin no longer
        /// costs the hull, because the terminal falls back to the nearest owned node.
        /// <para>Provenance: FAC-278.</para></summary>
        [Fact]
        public void MineTerminal_DeletedOrigin_LandsAtAnOwnedNodeInstead()
        {
            var world = MakeWorld();
            AddNode(world, "Dest1");
            var tuning = TestSupport.Tuning();

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F", OriginNodeId = "Gone1", TargetNodeId = "Dest1",
                DepartedUtc = world.UtcNow.AddSeconds(-60), EstimatedArrivalUtc = world.UtcNow,
                CurrentStopKind = FleetStopKind.Service,
                ShipPrefabId = "MinerX", ConvoySize = 1,
                FleetId = "gone2", LastResolvedSegment = -1,
            });

            var log = new List<string>();
            var states = new List<FactionState> { st };
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log, null);

            Assert.DoesNotContain(log, l => l.StartsWith("SHIP_LOST"));
            Assert.Contains(log, l => l.StartsWith("SHIP_RETURNED") && l.Contains("node=Dest1"));
            Assert.Equal(1, world.Graph.Nodes["Dest1"].ShipStock["MinerX"]);
        }
    }
}
