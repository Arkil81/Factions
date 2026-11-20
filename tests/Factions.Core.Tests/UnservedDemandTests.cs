using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The unserved set an Operations pass publishes on <see cref="FactionState.UnservedDemands"/>,
    /// and the roster-wide in-flight claim index built beside it — the two seams a cross-border
    /// Trade vector reads. See docs/trade-haul-shaped.md.
    /// </summary>
    public sealed class UnservedDemandTests
    {
        private sealed class FakeHaulCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 1_000_000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
                => new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        [Fact]
        public void RowWithNoInternalSource_IsPublishedWithItsShortfallClassAndUrgency()
        {
            WorldModel world = BuildWorld(sourceStock: 0);
            var state = new FactionState { Tag = "F" };

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            UnservedDemand row = Assert.Single(state.UnservedDemands);
            Assert.Equal("Dest", row.NodeId);
            Assert.Equal("Ore", row.ItemKey);
            Assert.Equal(40, row.Remaining);
            Assert.Equal(DemandClass.RecipeInput, row.Class);
            Assert.Equal(ExpectedUrgency(world, have: 0), row.Urgency, 9);
            // A zero urgency would satisfy an equality against a zero expectation while proving the
            // field was never carried across.
            Assert.True(row.Urgency > 0);
        }

        [Fact]
        public void RowWithAnInternalSource_IsNotPublished()
        {
            WorldModel world = BuildWorld(sourceStock: 100);
            var state = new FactionState { Tag = "F" };

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            // The pass really did match the row — otherwise "not published" is just the empty case.
            Assert.Single(state.PendingFleets);
            Assert.Empty(state.UnservedDemands);
        }

        [Fact]
        public void ARowMostlyCoveredInternally_IsNotPublished()
        {
            // 30 of the 40 wanted units exist internally, 75% -- at or above UnservedPublishCoverage,
            // so the owner's own Haul is trusted to finish it and nothing crosses the border.
            WorldModel world = BuildWorld(sourceStock: 30);
            var state = new FactionState { Tag = "F" };

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Single(state.PendingFleets);
            Assert.Empty(state.UnservedDemands);
        }

        /// <summary>The discriminator: only 10 of the 40 exist internally, 25%. The owner hauls its 10
        /// and the remaining 30 is offered to a partner, marked partial so the owner's own in-flight
        /// haul does not read as a claim on it.</summary>
        [Fact]
        public void ARowCoveredUnderHalfInternally_PublishesTheRemainder()
        {
            WorldModel world = BuildWorld(sourceStock: 10);
            var state = new FactionState { Tag = "F" };

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Equal(10, Assert.Single(state.PendingFleets).Cargo["Ore"]);
            UnservedDemand row = Assert.Single(state.UnservedDemands);
            Assert.Equal(30, row.Remaining);
            Assert.True(row.Partial);
        }

        [Fact]
        public void CoverageZero_PublishesOnlyWhatNothingInternalTouched()
        {
            WorldModel world = BuildWorld(sourceStock: 10);
            world.Tuning.UnservedPublishCoverage = 0;
            var state = new FactionState { Tag = "F" };

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Single(state.PendingFleets);
            Assert.Empty(state.UnservedDemands);
        }

        [Fact]
        public void RowAlreadyClaimedByAnInFlightHaul_IsNotPublished()
        {
            // Same sourceless world as the publishing case above, so the only difference is the
            // claim: a row a convoy is already carrying is served, not unserved.
            WorldModel world = BuildWorld(sourceStock: 0);
            var state = new FactionState { Tag = "F" };
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind         = ActionKind.Haul,
                ActorTag     = "F",
                OriginNodeId = "Source",
                TargetNodeId = "Dest",
                Cargo        = new Dictionary<string, long> { { "Ore", 40 } },
            });

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Empty(state.UnservedDemands);
        }

        private static FactionState StateWithHaulInFlight(long carrying)
        {
            var state = new FactionState { Tag = "F" };
            state.PendingFleets.Add(new PendingFleetAction
            {
                Kind         = ActionKind.Haul,
                ActorTag     = "F",
                OriginNodeId = "Source",
                TargetNodeId = "Dest",
                Cargo        = new Dictionary<string, long> { { "Ore", carrying } },
            });
            return state;
        }

        /// <summary>A steady trickle keeps an own haul in flight toward the row almost all the time.
        /// What it carries counts as covered; under half of the shortfall, the rest is offered.</summary>
        [Fact]
        public void ARowAHaulInFlightCoversUnderHalf_PublishesTheRemainderAsPartial()
        {
            WorldModel world = BuildWorld(sourceStock: 0);
            FactionState state = StateWithHaulInFlight(carrying: 10);

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            UnservedDemand row = Assert.Single(state.UnservedDemands);
            Assert.Equal("Dest", row.NodeId);
            Assert.Equal(30, row.Remaining);
            Assert.True(row.Partial);
        }

        [Fact]
        public void ARowAHaulInFlightCoversHalf_IsNotPublished()
        {
            WorldModel world = BuildWorld(sourceStock: 0);
            FactionState state = StateWithHaulInFlight(carrying: 20);

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Empty(state.UnservedDemands);
        }

        [Fact]
        public void ARowAPlayerContractClaims_IsNotPublished()
        {
            WorldModel world = BuildWorld(sourceStock: 0);
            var state = new FactionState { Tag = "F" };
            state.ExternalHaulClaims.Add(("Dest", "Ore"));

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Empty(state.UnservedDemands);
        }

        [Fact]
        public void CoverageZero_PublishesNothingForARowWithAHaulInFlight()
        {
            WorldModel world = BuildWorld(sourceStock: 0);
            world.Tuning.UnservedPublishCoverage = 0;
            FactionState state = StateWithHaulInFlight(carrying: 1);

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Empty(state.UnservedDemands);
        }

        [Fact]
        public void StagedCycle_PublishesTheSameInFlightRemainderAsTheSynchronousTick()
        {
            WorldModel expectedWorld = BuildWorld(sourceStock: 0);
            FactionState expectedState = StateWithHaulInFlight(carrying: 10);
            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { expectedState }, expectedWorld, expectedWorld.Tuning, new List<string>());

            WorldModel actualWorld = BuildWorld(sourceStock: 0);
            FactionState actualState = StateWithHaulInFlight(carrying: 10);
            var states = new List<FactionState> { actualState };
            var manager = new OperationsManager(itemVolumes: null, itemMasses: null);
            Invoke(manager, "BeginSweepForTesting", states);
            int steps = 0;
            while (!(bool)Invoke(manager, "StepSweepOnceForTesting", states, actualWorld, actualWorld.Tuning, new List<string>()))
            {
                steps++;
                Assert.True(steps < 1000, "Operations sweep did not complete");
            }

            UnservedDemand expected = Assert.Single(expectedState.UnservedDemands);
            UnservedDemand actual = Assert.Single(actualState.UnservedDemands);
            Assert.Equal(expected.NodeId, actual.NodeId);
            Assert.Equal(expected.Remaining, actual.Remaining);
            Assert.Equal(expected.Class, actual.Class);
            Assert.Equal(expected.Partial, actual.Partial);
            Assert.Equal(expected.Urgency, actual.Urgency, 9);
        }

        [Fact]
        public void DormantFaction_PublishesNothingAndClearsAStaleSet()
        {
            WorldModel world = BuildWorld(sourceStock: 0);
            var state = new FactionState { Tag = "F" };
            state.DormantUntilUtc = world.UtcNow.AddHours(1);
            state.UnservedDemands.Add(new UnservedDemand { NodeId = "Dest", ItemKey = "Ore", Remaining = 40 });

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Empty(state.UnservedDemands);
        }

        [Fact]
        public void FactionWithNoDemandAtAll_ClearsAStaleSet()
        {
            WorldModel world = BuildWorld(sourceStock: 0);
            world.Demand.Consume("F", "Item:Ore", "Dest");
            var state = new FactionState { Tag = "F" };
            state.UnservedDemands.Add(new UnservedDemand { NodeId = "Dest", ItemKey = "Ore", Remaining = 40 });

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Empty(world.Demand.SnapshotItemDemands("F"));
            Assert.Empty(state.UnservedDemands);
        }

        [Fact]
        public void AutonomyDisabledFaction_PublishesNothing()
        {
            // A Magnate-claimed faction still hauls for itself, but until it opts in to acquisition
            // the human supplies its cross-border decisions, so it offers no rows to a partner.
            WorldModel world = BuildWorld(sourceStock: 0);
            var state = new FactionState { Tag = "F", IsPlayerFaction = true };

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Empty(state.UnservedDemands);

            // The same world with an NPC owner publishes the row — without this the assertion above
            // is equally consistent with the pass never reaching the matcher.
            WorldModel npcWorld = BuildWorld(sourceStock: 0);
            var npcState = new FactionState { Tag = "F" };
            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { npcState }, npcWorld, npcWorld.Tuning, new List<string>());
            Assert.Single(npcState.UnservedDemands);
        }

        // FAC-298, gate two. The same player faction publishes once it has opted in to BUYING, and
        // the reason this gate matters is that the list has two readers: the faction's own buy pass,
        // and every other faction's sell pass. An unpublished faction is not merely passive, it is
        // invisible -- no neighbour can discover it is short of anything.
        [Fact]
        public void PlayerFactionOptedInToBuying_PublishesItsRows()
        {
            WorldModel world = BuildWorld(sourceStock: 0);
            var state = new FactionState { Tag = "F", IsPlayerFaction = true };
            state.TradeBuyOptIn = true;

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            UnservedDemand row = Assert.Single(state.UnservedDemands);
            Assert.Equal("Dest", row.NodeId);
            Assert.Equal("Ore", row.ItemKey);
        }

        // The discriminating half: SELLING is not what makes a faction legible. Buying is the flag
        // that publishes, because publishing is how a neighbour offers to supply you. A one-flag
        // implementation would pass the test above and fail this one.
        [Fact]
        public void PlayerFactionOptedInToSellingOnly_StillPublishesNothing()
        {
            WorldModel world = BuildWorld(sourceStock: 0);
            var state = new FactionState { Tag = "F", IsPlayerFaction = true };
            state.TradeSellOptIn = true;
            state.TradeBuyOptIn = false;

            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { state }, world, world.Tuning, new List<string>());

            Assert.Empty(state.UnservedDemands);
        }

        [Fact]
        public void StagedCycle_PublishesTheSameSetAsTheSynchronousTick()
        {
            WorldModel expectedWorld = BuildWorld(sourceStock: 0);
            var expectedState = new FactionState { Tag = "F" };
            new OperationsManager(itemVolumes: null, itemMasses: null)
                .Tick(new List<FactionState> { expectedState }, expectedWorld, expectedWorld.Tuning, new List<string>());

            WorldModel actualWorld = BuildWorld(sourceStock: 0);
            var actualState = new FactionState { Tag = "F" };
            var states = new List<FactionState> { actualState };
            var manager = new OperationsManager(itemVolumes: null, itemMasses: null);
            Invoke(manager, "BeginSweepForTesting", states);
            int steps = 0;
            while (!(bool)Invoke(manager, "StepSweepOnceForTesting", states, actualWorld, actualWorld.Tuning, new List<string>()))
            {
                steps++;
                Assert.True(steps < 1000, "Operations sweep did not complete");
            }

            Assert.Single(expectedState.UnservedDemands); // the comparison below has something to compare
            Assert.Equal(expectedState.UnservedDemands.Count, actualState.UnservedDemands.Count);
            for (int i = 0; i < expectedState.UnservedDemands.Count; i++)
            {
                Assert.Equal(expectedState.UnservedDemands[i].NodeId, actualState.UnservedDemands[i].NodeId);
                Assert.Equal(expectedState.UnservedDemands[i].ItemKey, actualState.UnservedDemands[i].ItemKey);
                Assert.Equal(expectedState.UnservedDemands[i].Remaining, actualState.UnservedDemands[i].Remaining);
                Assert.Equal(expectedState.UnservedDemands[i].Class, actualState.UnservedDemands[i].Class);
                Assert.Equal(expectedState.UnservedDemands[i].Urgency, actualState.UnservedDemands[i].Urgency, 9);
            }
        }

        [Fact]
        public void StagedCycle_DiscardsTheUnservedSetWhenTheGuardInvalidates()
        {
            WorldModel world = BuildWorld(sourceStock: 0);
            world.Graph.Nodes["Source"].ShipStock["Hauler"] = 1;
            var state = new FactionState { Tag = "F" };
            var states = new List<FactionState> { state };
            var manager = new OperationsManager(itemVolumes: null, itemMasses: null);
            Invoke(manager, "BeginSweepForTesting", states);

            Assert.False((bool)Invoke(manager, "StepSweepOnceForTesting", states, world, world.Tuning, new List<string>()));
            Assert.False((bool)Invoke(manager, "StepSweepOnceForTesting", states, world, world.Tuning, new List<string>()));
            // Nothing is published mid-cycle: the set only appears at commit.
            Assert.Empty(state.UnservedDemands);

            world.Graph.Nodes["Source"].ShipStock["Hauler"] = 2; // invalidates the captured read set

            int steps = 0;
            while (!(bool)Invoke(manager, "StepSweepOnceForTesting", states, world, world.Tuning, new List<string>()))
            {
                steps++;
                Assert.True(steps < 2000, "invalidated Operations cycle did not restart and complete");
            }

            Assert.True(manager.LastTelemetry.Restarts >= 1);
            // The restarted cycle rebuilt the set from scratch: exactly one row, not two.
            UnservedDemand row = Assert.Single(state.UnservedDemands);
            Assert.Equal(40, row.Remaining);
        }

        [Fact]
        public void RosterIndex_SeesAPartnerFactionsHaulConvoy()
        {
            var mine = new FactionState { Tag = "A" };
            var partner = new FactionState { Tag = "B" };
            partner.PendingFleets.Add(new PendingFleetAction
            {
                Kind         = ActionKind.Haul,
                ActorTag     = "B",
                OriginNodeId = "B1",
                TargetNodeId = "Dest",
                Cargo        = new Dictionary<string, long> { { "Ore", 40 } },
            });

            HashSet<(string, string)> pairs = RosterPairs(new List<FactionState> { mine, partner });

            Assert.Contains(("Dest", "Ore"), pairs);
        }

        [Fact]
        public void RosterIndex_ClaimsASellConvoysServiceNode()
        {
            // Seller-flies: the goods land at the buyer's node, which is where the convoy services.
            var seller = new FactionState { Tag = "S" };
            seller.PendingFleets.Add(new PendingFleetAction
            {
                Kind            = ActionKind.Trade,
                ActorTag        = "S",
                TargetTag       = "B",
                OriginNodeId    = "S1",
                TargetNodeId    = "BuyerNode",
                CurrentStopKind = FleetStopKind.Service,
                Cargo           = new Dictionary<string, long> { { "Comp:Thrust", 12 } },
                RemainingStops  = new List<FleetStop>
                {
                    new FleetStop { NodeId = "S1", Kind = FleetStopKind.Return },
                },
            });

            HashSet<(string, string)> pairs = RosterPairs(new List<FactionState> { seller });

            Assert.Contains(("BuyerNode", "Comp:Thrust"), pairs);
            // The Return stop is where the hull goes home, not where the cargo lands.
            Assert.DoesNotContain(("S1", "Comp:Thrust"), pairs);
        }

        [Fact]
        public void RosterIndex_ClaimsAProcurementConvoysTerminalReturnNode()
        {
            // Buyer-flies: the pickup is at the seller, the delivery is the terminal Return stop.
            var buyer = new FactionState { Tag = "B" };
            buyer.PendingFleets.Add(new PendingFleetAction
            {
                Kind            = ActionKind.Trade,
                IsProcurement   = true,
                ActorTag        = "B",
                TargetTag       = "S",
                OriginNodeId    = "B1",
                TargetNodeId    = "SellerNode",
                CurrentStopKind = FleetStopKind.Pickup,
                PickupItemKey   = "Comp:Thrust",
                RemainingStops  = new List<FleetStop>
                {
                    new FleetStop { NodeId = "BuyerHome", Kind = FleetStopKind.Return },
                },
            });

            HashSet<(string, string)> pairs = RosterPairs(new List<FactionState> { buyer });

            Assert.Contains(("BuyerHome", "Comp:Thrust"), pairs);
            Assert.DoesNotContain(("SellerNode", "Comp:Thrust"), pairs);
        }

        [Fact]
        public void RosterIndex_SkipsResolvedAndInterceptedConvoys()
        {
            var st = new FactionState { Tag = "S" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "S", TargetNodeId = "Done", Resolved = true,
                Cargo = new Dictionary<string, long> { { "Ore", 1 } },
            });
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "S", TargetNodeId = "Lost", Intercepted = true,
                Cargo = new Dictionary<string, long> { { "Ore", 1 } },
            });
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "S", TargetNodeId = "Live",
                Cargo = new Dictionary<string, long> { { "Ore", 1 } },
            });

            HashSet<(string, string)> pairs = RosterPairs(new List<FactionState> { st });

            Assert.Contains(("Live", "Ore"), pairs);
            Assert.DoesNotContain(("Done", "Ore"), pairs);
            Assert.DoesNotContain(("Lost", "Ore"), pairs);
        }

        [Fact]
        public void PerFactionIndex_StillIgnoresTradeConvoys()
        {
            // Slice A changes nothing for Haul: its own matcher keeps reading the Haul-only index,
            // so a Trade convoy must not start suppressing a Haul the same faction would fly.
            var st = new FactionState { Tag = "S" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "S", TargetNodeId = "Dest",
                Cargo = new Dictionary<string, long> { { "Ore", 1 } },
            });

            var pairs = new HashSet<(string, string)>();
            PerFactionIndex().Invoke(null, new object[] { st, pairs });

            Assert.DoesNotContain(("Dest", "Ore"), pairs);
            Assert.Contains(("Dest", "Ore"), RosterPairs(new List<FactionState> { st }));
        }

        // ---- fixtures --------------------------------------------------------------------------

        /// <summary>"Dest" wants 40 Ore for a blocked recipe; "Source" holds
        /// <paramref name="sourceStock"/> of it and one Haul-suitable hull.</summary>
        private static WorldModel BuildWorld(long sourceStock)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.Tuning = TestSupport.Tuning();
            world.PrefabCatalogue = new FakeHaulCatalogue();

            SpatialNode source = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "Source");
            source.OwnerFactionTag = "F";
            if (sourceStock > 0) source.Inventory["Ore"] = sourceStock;
            source.ShipStock["Hauler"] = 1;

            SpatialNode dest = world.Graph.Add(new Vec3(10, 0, 0), 1.0, "Dest");
            dest.OwnerFactionTag = "F";

            world.Demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Ore", NodeId = "Dest", Quantity = 40,
                Class = DemandClass.RecipeInput,
                ExpiresUtc = world.UtcNow.AddMinutes(5),
            });
            return world;
        }

        private static double ExpectedUrgency(WorldModel world, long have)
        {
            DemandEntry demand = world.Demand.SnapshotFactionDemands("F").Single(d => d.Kind == "Item:Ore");
            return DemandUrgency.Score(demand, have, 0, world.UtcNow, world.Tuning);
        }

        private static HashSet<(string, string)> RosterPairs(IList<FactionState> roster)
        {
            var pairs = new HashSet<(string, string)>();
            MethodInfo method = typeof(OperationsManager).GetMethod("IndexInFlightHaulPairs",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(IList<FactionState>), typeof(HashSet<(string, string)>) }, null);
            Assert.NotNull(method);
            method.Invoke(null, new object[] { roster, pairs });
            return pairs;
        }

        private static MethodInfo PerFactionIndex()
        {
            MethodInfo method = typeof(OperationsManager).GetMethod("IndexInFlightHaulPairs",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(FactionState), typeof(HashSet<(string, string)>) }, null);
            Assert.NotNull(method);
            return method;
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.Invoke(target, arguments);
        }
    }
}
