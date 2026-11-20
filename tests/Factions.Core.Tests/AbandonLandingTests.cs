using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-278 coverage: a hull is credited to a node its faction actually owns, and a node handed to
    /// UNKN sends its hulls somewhere the owner can still reach before the grace sweep deletes it.
    /// Live 2026-09-06: 321 hulls standing on 68 UNKN nodes, and the sweep removing 100+ nodes a day
    /// with whatever was on them.
    /// </summary>
    public class AbandonLandingTests
    {
        /// <summary>Constant gravity everywhere, so dry-TWR is a property of the hull alone — the
        /// only axis these tests vary.</summary>
        private sealed class ConstantGravityCatalogue : IPlanetCatalogue
        {
            private readonly double _g;
            public ConstantGravityCatalogue(double g) { _g = g; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private sealed class ThrustCatalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, double> _dryMass = new Dictionary<string, double>(StringComparer.Ordinal);
            private readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> _bands
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>(StringComparer.Ordinal);

            /// <summary>A hull that can lift itself: full-effectiveness Down thrust.</summary>
            public ThrustCatalogue Lifter(string prefabId, double dryMassKg, float forceN)
            {
                _dryMass[prefabId] = dryMassKg;
                _bands[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    { ThrustDirection.Down, new List<ThrustBand> { new ThrustBand
                        {
                            ThrusterType = "Hydrogen", ForceMagnitudeN = forceN,
                            MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 0f,
                            EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f,
                            NeedsAtmosphereForInfluence = false,
                        } } },
                };
                return this;
            }

            /// <summary>A scanned hull with no Down thrust at all — a genuine gate failure at any
            /// gravity, not "unknown, don't gate".</summary>
            public ThrustCatalogue Deadweight(string prefabId, double dryMassKg)
            {
                _dryMass[prefabId] = dryMassKg;
                _bands[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
                return this;
            }

            public long CargoLiters(string prefabId) { return 1000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public double DrillYieldScore(string prefabId) { return 0; }

            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> b;
                return prefabId != null && _bands.TryGetValue(prefabId, out b)
                    ? b : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }

            public double DryMassKg(string prefabId)
            {
                double v;
                return prefabId != null && _dryMass.TryGetValue(prefabId, out v) ? v : 0;
            }
        }

        private static WorldModel MakeWorld()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
            return world;
        }

        private static SpatialNode AddNode(WorldModel world, string id, Vec3 pos, string tag = "F")
        {
            SpatialNode n = world.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = 1.0;
            return n;
        }

        private static List<string> Resolve(FactionState st, WorldModel world)
        {
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { st }, world,
                                                 TestSupport.Tuning(), new SeededRandom(1), log);
            return log;
        }

        private static string Line(IEnumerable<string> log, string tag)
        {
            return log.FirstOrDefault(l => l.StartsWith(tag, StringComparison.Ordinal));
        }

        // ---- in-flight ownership loss -------------------------------------------------------

        [Fact]
        public void HaulWhoseDestinationDecayedInFlight_LandsAtTheOrigin_AndSaysItWasRedirected()
        {
            WorldModel world = MakeWorld();
            AddNode(world, "N034", new Vec3(0, 0, 0), "UNGD");
            AddNode(world, "N260", new Vec3(100, 0, 0), "UNGD");

            var st = new FactionState { Tag = "UNGD" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "UNGD",
                OriginNodeId = "N034", TargetNodeId = "N260",
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "Cepheus M-25 Tugboat", ConvoySize = 1,
                FleetId = "620650",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            });

            // The destination decays three minutes after dispatch — the live sequence exactly.
            world.Withdraw("UNGD", "N260");

            string line = Line(Resolve(st, world), "HAUL_SHIP_RETURNED");

            Assert.NotNull(line);
            Assert.Contains("node=N034", line, StringComparison.Ordinal);
            Assert.Contains("redirected_from=N260 why=fallback_origin", line, StringComparison.Ordinal);

            int landed, orphaned;
            world.Graph.Nodes["N034"].ShipStock.TryGetValue("Cepheus M-25 Tugboat", out landed);
            world.Graph.Nodes["N260"].ShipStock.TryGetValue("Cepheus M-25 Tugboat", out orphaned);
            Assert.Equal(1, landed);
            Assert.Equal(0, orphaned);
        }

        [Fact]
        public void MineReturnWhoseReturnNodeWasAbandoned_LandsAtTheNearestOwnedNode()
        {
            WorldModel world = MakeWorld();
            AddNode(world, "Deposit", new Vec3(0, 0, 0));
            AddNode(world, "Near", new Vec3(10, 0, 0));
            AddNode(world, "Far", new Vec3(500, 0, 0));

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F",
                OriginNodeId = null, ReturnNodeId = "Deposit", TargetNodeId = "Far",
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "RhinoMiner", ConvoySize = 1,
                FleetId = "AAAAAA",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            });

            world.Withdraw("F", "Deposit");

            string line = Line(Resolve(st, world), "SHIP_RETURNED");

            Assert.NotNull(line);
            Assert.Contains("node=Near", line, StringComparison.Ordinal);
            Assert.Contains("redirected_from=Deposit why=nearest_owned", line, StringComparison.Ordinal);

            int landed;
            world.Graph.Nodes["Near"].ShipStock.TryGetValue("RhinoMiner", out landed);
            Assert.Equal(1, landed);
            Assert.Empty(world.Graph.Nodes["Deposit"].ShipStock);
        }

        [Fact]
        public void AReturnWithNoOwnedNodeAnywhere_IsLostWithAnOwnershipReason()
        {
            WorldModel world = MakeWorld();
            AddNode(world, "Home", new Vec3(0, 0, 0));

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "F",
                OriginNodeId = null, ReturnNodeId = "Home", TargetNodeId = "Home",
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "Trader", ConvoySize = 1,
                FleetId = "BBBBBB",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            });

            world.Withdraw("F", "Home");   // the faction's last node

            string line = Line(Resolve(st, world), "SHIP_LOST");

            Assert.NotNull(line);
            Assert.Contains("why=no_owned_node", line, StringComparison.Ordinal);
        }

        // ---- the itinerary Return leg -------------------------------------------------------

        /// <summary>Every kind with a return leg comes home through `FinalizeReturn`, not through the
        /// single-leg terminal above — the MiniMerchant/ConsignmentDrone hulls in FAC-278's census.</summary>
        [Fact]
        public void TradeReturnLegWhoseHomeDecayedInFlight_LandsAtTheNearestOwnedNode()
        {
            WorldModel world = MakeWorld();
            SpatialNode home = AddNode(world, "Home", new Vec3(0, 0, 0));
            AddNode(world, "Near", new Vec3(20, 0, 0));
            AddNode(world, "Far", new Vec3(400, 0, 0));

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "F",
                OriginNodeId = "Home", TargetNodeId = "Home", TargetPosKm = home.Position,
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "Eco_Ship_MiniMerchant", ConvoySize = 1,
                FleetId = "CCCCCC",
                CurrentStopKind = FleetStopKind.Return,
                LastResolvedSegment = -1,
            });

            world.Withdraw("F", "Home");

            string line = Line(Resolve(st, world), "FLEET_RETURNED");

            Assert.NotNull(line);
            Assert.Contains("ship_returned", line, StringComparison.Ordinal);
            Assert.Contains("redirected_from=Home why=nearest_owned", line, StringComparison.Ordinal);
            Assert.Equal(1, world.Graph.Nodes["Near"].ShipStock["Eco_Ship_MiniMerchant"]);
            Assert.Empty(world.Graph.Nodes["Home"].ShipStock);
        }

        /// <summary>Both named nodes gone from the graph: the scan is anchored on the fleet's own
        /// terminal position instead, so the hull is not destroyed while the faction owns reachable
        /// ground.</summary>
        [Fact]
        public void TradeReturnLegWhoseHomeWasDeleted_LandsOnTheFleetsPositionAnchor()
        {
            WorldModel world = MakeWorld();
            AddNode(world, "Refuge", new Vec3(30, 0, 0));

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "F",
                OriginNodeId = "AlsoGone", TargetNodeId = "Gone", TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "C21_Trade_ConsignmentDrone", ConvoySize = 1,
                FleetId = "DDDDDD",
                CurrentStopKind = FleetStopKind.Return,
                LastResolvedSegment = -1,
            });

            List<string> log = Resolve(st, world);

            Assert.Null(Line(log, "SHIP_LOST"));
            string line = Line(log, "FLEET_RETURNED");
            Assert.NotNull(line);
            Assert.Contains("ship_returned", line, StringComparison.Ordinal);
            Assert.Contains("redirected_from=Gone why=nearest_owned", line, StringComparison.Ordinal);
            Assert.Equal(1, world.Graph.Nodes["Refuge"].ShipStock["C21_Trade_ConsignmentDrone"]);
        }

        [Fact]
        public void ReturnLegWithNoOwnedNodeAnywhere_SaysShipLostOnBothLines()
        {
            WorldModel world = MakeWorld();
            AddNode(world, "Home", new Vec3(0, 0, 0), "SOMEONE_ELSE");

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "F",
                OriginNodeId = "Home", TargetNodeId = "Home", TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "Eco_Ship_MiniMerchant", ConvoySize = 1,
                FleetId = "EEEEEE",
                CurrentStopKind = FleetStopKind.Return,
                LastResolvedSegment = -1,
            });

            List<string> log = Resolve(st, world);

            Assert.Contains("why=no_owned_node", Line(log, "SHIP_LOST"), StringComparison.Ordinal);
            Assert.Contains("ship_lost", Line(log, "FLEET_RETURNED"), StringComparison.Ordinal);
            Assert.Empty(world.Graph.Nodes["Home"].ShipStock);
        }

        /// <summary>A fleet persisted without an actor tag cannot be ownership-tested; the old
        /// existence-only rule applies rather than deleting the hull.</summary>
        [Fact]
        public void AFleetWithNoActorTag_StillLandsWhereItAlwaysDid()
        {
            WorldModel world = MakeWorld();
            AddNode(world, "Home", new Vec3(0, 0, 0), "SOMEONE_ELSE");

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = null,
                OriginNodeId = "Home", TargetNodeId = "Home", TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "Legacy", ConvoySize = 1,
                FleetId = "FFFFFF",
                CurrentStopKind = FleetStopKind.Return,
                LastResolvedSegment = -1,
            });

            List<string> log = Resolve(st, world);

            Assert.Null(Line(log, "SHIP_LOST"));
            Assert.Contains("ship_returned", Line(log, "FLEET_RETURNED"), StringComparison.Ordinal);
            Assert.Equal(1, world.Graph.Nodes["Home"].ShipStock["Legacy"]);
        }

        [Fact]
        public void AHaulThatCannotLandAnywhere_UsesTheHaulLossVerb()
        {
            WorldModel world = MakeWorld();
            AddNode(world, "Dest", new Vec3(0, 0, 0), "SOMEONE_ELSE");

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "F",
                OriginNodeId = "Dest", TargetNodeId = "Dest", TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "Eco_Ship_H2BulwarkHauler", ConvoySize = 1,
                FleetId = "AABBCC",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            });

            List<string> log = Resolve(st, world);

            Assert.Null(Line(log, "SHIP_LOST"));
            Assert.Contains("why=no_owned_node", Line(log, "HAUL_SHIP_LOST"), StringComparison.Ordinal);
        }

        [Fact]
        public void ARedirectedRelocationHaul_NamesTheNodeItActuallyLandedAt()
        {
            WorldModel world = MakeWorld();
            SpatialNode dest = AddNode(world, "Dest", new Vec3(0, 0, 0));
            AddNode(world, "Yard", new Vec3(15, 0, 0));

            var st = new FactionState { Tag = "F" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "F", RelocationRole = "Trade",
                OriginNodeId = "Dest", TargetNodeId = "Dest", TargetPosKm = dest.Position,
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "Eco_Ship_MiniMerchant", ConvoySize = 1,
                FleetId = "BBCCDD",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            });

            world.Withdraw("F", "Dest");

            List<string> log = Resolve(st, world);

            string arrived = Line(log, "SHIP_RELOCATE_ARRIVED");
            string credited = Line(log, "HAUL_SHIP_RETURNED");
            Assert.NotNull(arrived);
            Assert.Contains("-> Yard redirected_from=Dest why=nearest_owned", arrived, StringComparison.Ordinal);
            Assert.Contains("node=Yard", credited, StringComparison.Ordinal);
            Assert.Equal(1, world.Graph.Nodes["Yard"].ShipStock["Eco_Ship_MiniMerchant"]);
        }

        // ---- abandonment evacuation ---------------------------------------------------------

        [Fact]
        public void Withdraw_FliesEveryReachableHullOff_AndRecordsTheOneThatCannotLeave()
        {
            WorldModel world = MakeWorld();
            world.Planets = new ConstantGravityCatalogue(1.0);
            world.PrefabCatalogue = new ThrustCatalogue()
                .Lifter("Hauler", 1000, 50000f)
                .Lifter("Tug", 1000, 50000f)
                .Deadweight("Sled", 1000);

            SpatialNode held = AddNode(world, "Held", new Vec3(0, 0, 0));
            AddNode(world, "Refuge", new Vec3(5, 0, 0));
            AddNode(world, "Distant", new Vec3(900, 0, 0));
            held.ShipStock["Hauler"] = 2;
            held.ShipStock["Tug"] = 1;
            held.ShipStock["Sled"] = 3;
            var st = new FactionState { Tag = "F" };

            world.Withdraw(st, "Held");

            // Both liftable prefabs are flying to the nearest owned node; the deadweight stayed.
            Assert.Empty(world.Graph.Nodes["Refuge"].ShipStock);
            Assert.Equal(new[] { "Sled" }, world.Graph.Nodes["Held"].ShipStock.Keys.ToArray());
            Assert.Equal(3, world.Graph.Nodes["Held"].ShipStock["Sled"]);
            Assert.Equal(2, st.PendingFleets.Count);
            Assert.All(st.PendingFleets, f => Assert.Equal("Refuge", f.TargetNodeId));

            IList<ShipEvacuation> records = world.ShipEvacuations;
            Assert.Equal(3, records.Count);

            ShipEvacuation hauler = records.Single(r => r.PrefabId == "Hauler");
            Assert.Equal("F", hauler.FactionTag);
            Assert.Equal(2, hauler.Count);
            Assert.Equal("Held", hauler.FromNodeId);
            Assert.Equal("Refuge", hauler.ToNodeId);
            Assert.Equal("nearest_owned", hauler.Reason);

            ShipEvacuation sled = records.Single(r => r.PrefabId == "Sled");
            Assert.Null(sled.ToNodeId);
            Assert.Equal(3, sled.Count);
            Assert.Equal("no_owned_node", sled.Reason);

            world.ClearShipEvacuations();
            Assert.Empty(world.ShipEvacuations);

            world.UtcNow = st.PendingFleets.Max(f => f.EstimatedArrivalUtc).AddSeconds(1);
            Resolve(st, world);
            Assert.Equal(2, world.Graph.Nodes["Refuge"].ShipStock["Hauler"]);
            Assert.Equal(1, world.Graph.Nodes["Refuge"].ShipStock["Tug"]);
        }

        [Fact]
        public void Withdraw_ClearsTheStoreReservationItMovesTheHullsOutFrom()
        {
            WorldModel world = MakeWorld();
            SpatialNode held = AddNode(world, "Held", new Vec3(0, 0, 0));
            AddNode(world, "Refuge", new Vec3(5, 0, 0));
            held.ShipStock["Hauler"] = 2;
            held.ListedForSale["Hauler"] = 2;   // both units reserved for a player purchase
            var st = new FactionState { Tag = "F" };

            world.Withdraw(st, "Held");

            // ListedForSale[id] <= ShipStock[id] is an invariant; a reservation left behind on a
            // node whose stock has left would break it.
            Assert.Empty(world.Graph.Nodes["Held"].ShipStock);
            Assert.Empty(world.Graph.Nodes["Held"].ListedForSale);
            Assert.Equal(2, Assert.Single(st.PendingFleets).ConvoySize);
        }

        [Fact]
        public void HoldDecayToZero_EvacuatesTheSameWayAnExplicitWithdrawDoes()
        {
            WorldModel world = MakeWorld();
            SpatialNode held = AddNode(world, "N260", new Vec3(0, 0, 0));
            AddNode(world, "N034", new Vec3(5, 0, 0));
            held.HoldStrength = 0.1;
            held.ShipStock["Hauler"] = 1;
            var st = new FactionState { Tag = "F" };

            world.DecayInfluence(0.5, 0.5, new List<FactionState> { st });

            Assert.Equal(NeutralFaction.Tag, world.Graph.Nodes["N260"].OwnerFactionTag);
            Assert.Empty(world.Graph.Nodes["N260"].ShipStock);
            Assert.Equal("N034", Assert.Single(st.PendingFleets).TargetNodeId);
            Assert.Single(world.ShipEvacuations);
        }

        // ---- the sweep says what it destroyed -----------------------------------------------

        [Fact]
        public void RemoveNode_ReportsTheHullsAndItemsItDestroyed()
        {
            WorldModel world = MakeWorld();
            SpatialNode doomed = AddNode(world, "B", new Vec3(0, 0, 0), NeutralFaction.Tag);
            doomed.ShipStock["Hauler"] = 2;
            doomed.ShipStock["Sled"] = 1;
            doomed.Inventory["Ore:Iron"] = 500;
            doomed.Inventory["Ingot:Steel"] = 10;

            RemovedNodeContents destroyed = world.RemoveNode("B");

            Assert.Equal(3, destroyed.Hulls);
            Assert.Equal(510L, destroyed.ItemUnits);
            Assert.False(world.Graph.Nodes.ContainsKey("B"));
        }

        [Fact]
        public void RemoveNode_OnAnEmptyNode_ReportsZeroes()
        {
            WorldModel world = MakeWorld();
            AddNode(world, "B", new Vec3(0, 0, 0), NeutralFaction.Tag);

            RemovedNodeContents destroyed = world.RemoveNode("B");

            Assert.Equal(0, destroyed.Hulls);
            Assert.Equal(0L, destroyed.ItemUnits);
        }
    }
}
